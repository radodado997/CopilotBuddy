using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Relogin
{
    /// <summary>
    /// The 3.3.5a glue (login) screen currently shown by the client.
    /// Values mirror the screen-name strings stored at <see cref="GlobalOffsets.GlueScreenString"/>.
    /// </summary>
    internal enum GlueKind
    {
        /// <summary>Not readable, or a screen name we don't model.</summary>
        Unknown = 0,
        /// <summary>"login" — account/password form.</summary>
        Login,
        /// <summary>"charselect" — character selection.</summary>
        CharSelect,
        /// <summary>"charcreate" — character creation.</summary>
        CharCreate,
        /// <summary>"realmwizard" — realm list / realm wizard.</summary>
        RealmWizard,
        /// <summary>"patchdownload" — client updater.</summary>
        PatchDownload,
        /// <summary>"movie", "credits", "trialconvert" — screens that only need dismissing.</summary>
        Other
    }

    /// <summary>
    /// Low-level client operations used by <see cref="ReloginManager"/>.
    ///
    /// Everything here works directly against the WoW process and the glue screens: it does
    /// NOT depend on the bot tree, on ObjectManager state, or on any UI state, so it can be
    /// used while the bot is stopped and while the client is sitting on the login screen.
    ///
    /// Input mechanism: the project's existing one. Credentials, realm and character are
    /// submitted by running Lua inside the client through <see cref="Lua.DoString"/>, i.e.
    /// the same injected-EndScene mechanism the bot already uses for every in-game action
    /// (this codebase has no SendInput/keyboard path for text entry, and WotLK 3.3.5a ships
    /// the DefaultServerLogin / ChangeRealm / SelectCharacter / EnterWorld Lua API that
    /// HBRelog drives the same way). No new input layer is introduced.
    /// </summary>
    internal static class ReloginClient
    {
        /// <summary>g_ClientConnectionState — 1 once CGGameUI__EnterWorld ran, 0 on LeaveWorld.</summary>
        private const uint InGameOffset = GlobalOffsets.InGame;

        /// <summary>
        /// Non-zero while the client is on a loading screen / connecting. During that window
        /// g_ClientConnectionState is already 0 but the player has not logged out, so the
        /// logout detector must not count these polls.
        /// </summary>
        private const uint LoadingOffset = GlobalOffsets.IsLoadingOrConnecting;

        /// <summary>Length of the glue screen-name buffer at GlueScreenString.</summary>
        private const int GlueStringLength = 32;

        /// <summary>Process names the client can be running under (see MainWindow.FindAndAttachToWoW).</summary>
        internal static readonly string[] ProcessNames = { "Wow", "WoW", "Luacct" };

        #region Process helpers

        /// <summary>
        /// Looks a client process up by id. Returns null when the process is gone (already
        /// reaped or not started). Throws nothing.
        /// </summary>
        internal static Process? FindProcessById(int pid)
        {
            if (pid <= 0)
                return null;

            try
            {
                Process proc = Process.GetProcessById(pid);
                return proc.HasExited ? null : proc;
            }
            catch (ArgumentException)
            {
                return null; // no such process
            }
            catch (InvalidOperationException)
            {
                return null; // exited between GetProcessById and HasExited
            }
        }

        /// <summary>
        /// Finds a client process by executable name. Used only on startup of the monitor,
        /// when there is no tracked pid yet (e.g. the bot was attached through /pid= by HBRelog).
        /// </summary>
        internal static Process? FindClientProcessByName()
        {
            foreach (string name in ProcessNames)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (Process proc in procs)
                {
                    if (IsAlive(proc))
                        return proc;
                }
            }
            return null;
        }

        /// <summary>
        /// True when the process object still refers to a live process. HasExited throws when
        /// the handle was already closed (e.g. after ObjectManager.Shutdown disposed Memory),
        /// so this is the only safe way to ask.
        /// </summary>
        internal static bool IsAlive(Process? proc)
        {
            if (proc == null)
                return false;

            try
            {
                return !proc.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Closes a hung client: graceful window close first, then Kill. Returns true when the
        /// process is gone afterwards. Only called for the "window not responding for longer
        /// than the grace period" trigger, never for lag or a normal logout.
        /// </summary>
        internal static bool TerminateClient(Process? client, out string error)
        {
            error = "";
            if (!IsAlive(client))
                return true;

            try
            {
                Logging.Write("[Relogin] Closing the unresponsive client (pid {0}).", client!.Id);

                try
                {
                    if (client.CloseMainWindow())
                        client.WaitForExit(5000);
                }
                catch
                {
                    // No main window / already closing — fall through to Kill.
                }

                if (!client.HasExited)
                {
                    Logging.WriteDebug("[Relogin] Client did not close gracefully; terminating it.");
                    client.Kill();
                    client.WaitForExit(10_000);
                }

                return client.HasExited;
            }
            catch (Exception ex)
            {
                // Exited between the checks, or access denied.
                if (!IsAlive(client))
                    return true;

                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Starts the client executable. The working directory is set to the executable's own
        /// folder because the 3.3.5a client resolves Data/ and WTF/ relative to it.
        /// </summary>
        internal static Process? StartClient(string executablePath, out string error)
        {
            error = "";
            try
            {
                var psi = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                    UseShellExecute = false
                };

                Process? proc = Process.Start(psi);
                if (proc == null)
                {
                    error = "Process.Start returned null";
                    return null;
                }

                // Give the client time to create its window before anyone reads it. Bounded so
                // Stop()/window close never waits on a stuck launch.
                try { proc.WaitForInputIdle(15_000); } catch { /* non-interactive clients never idle */ }

                return proc;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        #endregion

        #region Memory helpers

        /// <summary>
        /// One direct read of the two addresses the feature cares about, without touching the
        /// bot's attached Memory/Executor. Used by the detection loop while the bot is not
        /// attached (or after ObjectManager.Shutdown disposed the old handle), so liveness
        /// stays observable through a whole crash → relaunch cycle.
        /// </summary>
        internal readonly struct ClientProbe
        {
            /// <summary>g_ClientConnectionState != 0.</summary>
            public readonly bool InGame;
            /// <summary>Raw glue screen name ("" when it could not be read).</summary>
            public readonly string GlueRaw;
            /// <summary>Mapped glue screen name.</summary>
            public readonly GlueKind Glue;
            /// <summary>True when at least one of the reads succeeded.</summary>
            public readonly bool Readable;
            /// <summary>Loading screen / connecting — not in world, but not logged out either.</summary>
            public readonly bool Loading;

            public ClientProbe(bool inGame, string glueRaw, GlueKind glue, bool readable, bool loading = false)
            {
                InGame = inGame;
                GlueRaw = glueRaw ?? "";
                Glue = glue;
                Readable = readable;
                Loading = loading;
            }

            public override string ToString()
            {
                if (!Readable) return "unreadable";
                if (InGame) return "in world";
                if (Loading) return "loading";
                return "glue: " + (GlueRaw.Length > 0 ? GlueRaw : "?");
            }
        }

        /// <summary>
        /// Reads the client state through a short-lived Memory handle. Never hooks anything,
        /// never publishes ObjectManager state — safe to call at 1 Hz from the monitor thread
        /// while the bot itself is stopped, running, or detaching.
        /// </summary>
        internal static ClientProbe Probe(int pid)
        {
            if (pid <= 0)
                return new ClientProbe(false, "", GlueKind.Unknown, false);

            Memory? memory = null;
            try
            {
                memory = new Memory(pid);
                if (!memory.IsProcessOpen)
                    return new ClientProbe(false, "", GlueKind.Unknown, false);

                bool inGame = false;
                try { inGame = memory.Read<byte>(InGameOffset) != 0; } catch { }

                bool loading = false;
                try { loading = memory.Read<byte>(LoadingOffset) != 0; } catch { }

                GlueKind glue = ReadGlueScreen(memory, out string raw, out bool glueReadable);

                return new ClientProbe(inGame, raw, glue, inGame || glueReadable, loading);
            }
            catch
            {
                return new ClientProbe(false, "", GlueKind.Unknown, false);
            }
            finally
            {
                try { memory?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Opens the client for read/write and installs the EndScene hook, exactly like the
        /// startup attach path does. Returns the Memory on success; on failure the partially
        /// initialised state is torn down and <paramref name="error"/> explains why.
        ///
        /// The hook is installed while the client is still on the glue screen: the login Lua
        /// needs the executor, and the executor is what the whole bot uses afterwards.
        /// </summary>
        /// <summary>
        /// Called with the new pid right before a replacement client is attached, so the
        /// process can be claimed through <see cref="ProcessMutex"/>. Returns the claimed
        /// mutex, or null plus a reason when the client must not be attached to (already
        /// owned by another instance, or the claim failed).
        /// </summary>
        internal static Func<int, (Mutex? Mutex, string Error)>? ClaimProcess;

        internal static Memory? TryAttachAndHook(int pid, out string error, bool isReplacement = false)
        {
            error = "";
            Memory? memory = null;

            try
            {
                // A previous attach may still be published (e.g. the client we were attached
                // to crashed). Release it before publishing a new one, otherwise the stale
                // Executor keeps being used by Lua.DoString.
                if (ObjectManager.Wow != null && !IsAttached(pid))
                {
                    Logging.WriteDebug("[Relogin] Releasing the previous client attachment before re-attaching.");
                    try { ObjectManager.Shutdown(); } catch { }
                    _attachedPid = 0;
                }

                // A client this feature launched is a new pid: the mutex MainWindow holds still
                // names the dead client, so claim the replacement before attaching to it. If the
                // claim fails, another instance owns that client and attaching would fight it.
                Mutex? claim = null;
                if (isReplacement && ClaimProcess != null)
                {
                    (Mutex? Mutex, string Error) claimed;
                    try { claimed = ClaimProcess(pid); }
                    catch (Exception ex) { claimed = (null, ex.Message); }

                    if (claimed.Mutex == null)
                    {
                        error = string.IsNullOrEmpty(claimed.Error)
                            ? "the relaunched client could not be claimed"
                            : claimed.Error;
                        return null;
                    }

                    claim = claimed.Mutex;
                }

                memory = new Memory(pid);
                if (!memory.IsProcessOpen)
                {
                    error = "OpenProcess failed (access denied or process exiting)";
                    memory.Dispose();
                    try { claim?.Dispose(); } catch { }
                    return null;
                }

                ObjectManager.Initialize(memory);
                if (ObjectManager.Executor == null || !ObjectManager.Executor.IsInitialized)
                {
                    error = "EndScene hook could not be installed";
                    ObjectManager.Shutdown();
                    memory.Dispose();
                    try { claim?.Dispose(); } catch { }
                    return null;
                }

                // Every static that remembers an address inside the previous client has to be
                // invalidated before the bot tree touches the new one (DBC tables and the
                // cache wrappers both cache raw client addresses).
                StyxWoW.Reset();
                _attachedPid = pid;

                return memory;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { ObjectManager.Shutdown(); } catch { }
                try { memory?.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// True when the bot is attached to the given client and the EndScene hook is live.
        /// The pid comparison matters after a crash: the old Memory handle stays "open" even
        /// though its process is gone, so a pid-less check would skip hooking the relaunched
        /// client and every Lua call would go to a dead process.
        /// </summary>
        internal static bool IsAttached(int pid)
        {
            return TryGetAttachedProcessId(out int attachedPid) && attachedPid == pid;
        }

        /// <summary>
        /// True when the bot holds a live, hooked attachment, regardless of which client.
        /// </summary>
        internal static bool IsAttached()
        {
            return TryGetAttachedProcessId(out _);
        }

        /// <summary>Pid of the client this class attached to, or 0.</summary>
        private static int _attachedPid;

        private static bool TryGetAttachedProcessId(out int pid)
        {
            pid = 0;

            // Memory.ProcessId throws on the legacy Memory type once the process object is
            // gone, so the pid recorded by TryAttachAndHook is used whenever available.
            Memory? wow = ObjectManager.Wow;
            if (wow == null || !wow.IsProcessOpen)
                return false;

            ExecutorRand? executor = ObjectManager.Executor;
            if (executor == null || !executor.IsInitialized)
                return false;

            if (_attachedPid > 0)
            {
                pid = _attachedPid;
                return true;
            }

            try
            {
                pid = wow.ProcessId;
                return pid > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the glue screen from memory. <paramref name="readable"/> is false when the
        /// address could not be read at all — a liveness signal, not a screen state.
        /// </summary>
        internal static GlueKind ReadGlueScreen(Memory? memory, out string raw, out bool readable)
        {
            raw = "";
            readable = false;
            if (memory == null || !memory.IsProcessOpen)
                return GlueKind.Unknown;

            byte[]? bytes;
            try
            {
                bytes = memory.ReadBytes(GlobalOffsets.GlueScreenString, GlueStringLength);
            }
            catch
            {
                return GlueKind.Unknown;
            }

            if (bytes == null)
                return GlueKind.Unknown; // read failed

            readable = true;

            // A fully zeroed buffer means the client has not written a screen name yet (it
            // does that very early at boot), which is not a usable screen state.
            int len = Array.IndexOf(bytes, (byte)0);
            if (len < 0)
                len = bytes.Length;
            if (len == 0)
                return GlueKind.Unknown;

            raw = Encoding.ASCII.GetString(bytes, 0, len).Trim().ToLowerInvariant();

            switch (raw)
            {
                case "login":
                    return GlueKind.Login;
                case "charselect":
                    return GlueKind.CharSelect;
                case "charcreate":
                    return GlueKind.CharCreate;
                case "realmwizard":
                case "realmselect":
                    return GlueKind.RealmWizard;
                case "patchdownload":
                    return GlueKind.PatchDownload;
                case "movie":
                case "credits":
                case "trialconvert":
                    return GlueKind.Other;
                default:
                    return GlueKind.Unknown;
            }
        }

        /// <summary>
        /// Reads g_ClientConnectionState. False means "not in the world" — it is also what a
        /// failed read returns, which is why callers pair it with the glue screen.
        /// </summary>
        internal static bool IsInGame(Memory? memory)
        {
            if (memory == null || !memory.IsProcessOpen)
                return false;

            try
            {
                return memory.Read<byte>(InGameOffset) != 0;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Glue scripts

        // WotLK 3.3.5a has no Battle.net: DefaultServerLogin(account, password) is the whole
        // credential step. The chunk also covers the game-account dialog and the realm list,
        // because after a successful auth the client may land on the realm list instead of
        // character selection (and later, GetServerName() may disagree with the configured
        // realm). One chunk per poll handles every glue screen the login sequence can hit.
        // Modelled on HBRelog's LoginLuaFormat for this exact client build.
        private const string LoginScriptFormat =
            "local acct = \"{0}\" " +
            "local user = \"{1}\" " +
            "local pass = \"{2}\" " +
            "if (WoWAccountSelectDialog and WoWAccountSelectDialog:IsShown()) then " +
                "for i = 1, GetNumGameAccounts() do " +
                    "if GetGameAccountInfo(i):upper() == acct:upper() then WoWAccountSelect_SelectAccount(i) end " +
                "end " +
            "elseif (AccountLoginUI and AccountLoginUI:IsVisible()) then " +
                "DefaultServerLogin(user, pass) " +
                "AccountLoginUI:Hide() " +
            "elseif (RealmList and RealmList:IsVisible()) then " +
                "{3}" +
            "end ";

        // Realm loop: {0} is the (escaped) realm name. Declares its own local so it can be
        // used both from the login chunk and from the character-selection chunk.
        private const string RealmLoopFormat =
            "do local server = \"{0}\" " +
            "if server ~= \"\" then " +
                "for i = 1, select('#', GetRealmCategories()) do " +
                    "for j = 1, GetNumRealms(i) do " +
                        "if GetRealmInfo(i, j):upper() == server:upper() then " +
                            "RealmList:Hide() " +
                            "ChangeRealm(i, j) " +
                        "end " +
                    "end " +
                "end " +
            "end end ";

        // Character selection. Returns one value so the caller can tell the states apart:
        //   1 = matched character selected and EnterWorld() issued
        //   0 = at character selection, no character of that name (list may still be loading)
        //   2 = not at character selection
        //   3 = a realm switch was requested; character list not available yet
        //   4 = configured realm is not in the realm list
        // Modelled on HBRelog's CharSelectLuaFormat for this client.
        private const string CharSelectScriptFormat =
            "local name = \"{0}\" " +
            "local server = \"{1}\" " +
            "if (CharacterSelectUI and CharacterSelectUI:IsVisible()) then " +
                "{2}" +
                "for i = 1, GetNumCharacters() do " +
                    "if GetCharacterInfo(i):upper() == name:upper() then " +
                        "CharacterSelect_SelectCharacter(i) " +
                        "EnterWorld() " +
                        "return 1 " +
                    "end " +
                "end " +
                "return 0 " +
            "elseif (CharCreateRandomizeButton and CharCreateRandomizeButton:IsVisible()) then " +
                "CharacterCreate_Back() " +
                "return 2 " +
            "end " +
            "return 2 ";

        // Realm switch inside the character-selection chunk: only when a realm is configured
        // and the client is currently on a different one.
        private const string CharSelectRealmGuardFormat =
            "if server ~= \"\" and GetServerName() and GetServerName():upper() ~= server:upper() then " +
                "RequestRealmList(1) " +
                "return 3 " +
            "end ";

        /// <summary>
        /// Emits the credential chunk. The password is byte-escaped (\ddd per UTF-8 byte)
        /// so non-ASCII passwords survive the Lua string round-trip — the same encoding
        /// HBRelog uses for DefaultServerLogin.
        /// </summary>
        internal static void SendLogin(string accountName, string password, string realmName)
        {
            // The account name also has to match GetGameAccountInfo()/the account-select
            // dialog, so it is compared as plain text — only escaped for Lua syntax.
            string acct = Lua.Escape(accountName ?? "");
            string user = EncodeLuaBytes(accountName);
            string pass = EncodeLuaBytes(password);
            string realmLoop = string.IsNullOrEmpty(realmName)
                ? ""
                : string.Format(CultureInfo.InvariantCulture, RealmLoopFormat, Lua.Escape(realmName));

            string script = string.Format(CultureInfo.InvariantCulture, LoginScriptFormat,
                acct, user, pass, realmLoop);

            Lua.DoString(script, "ReloginLogin");
        }

        /// <summary>
        /// Emits the character-selection chunk and returns its status code, or -1 when Lua
        /// could not run at all.
        /// </summary>
        internal static int SendCharacterSelect(string characterName, string realmName)
        {
            string name = Lua.Escape(characterName ?? "");
            string realm = Lua.Escape(realmName ?? "");

            string realmGuard = string.IsNullOrEmpty(realmName)
                ? ""
                : string.Format(CultureInfo.InvariantCulture, CharSelectRealmGuardFormat, realm);

            string script = string.Format(CultureInfo.InvariantCulture, CharSelectScriptFormat,
                name, realm, realmGuard);

            List<string> values = Lua.GetReturnValues(script, "ReloginCharSelect");
            if (values == null || values.Count == 0)
                return -1;

            return int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                ? code
                : -1;
        }

        /// <summary>Encodes every UTF-8 byte as \ddd so the Lua source stays 7-bit safe.</summary>
        private static string EncodeLuaBytes(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            var sb = new StringBuilder(utf8.Length * 4);
            foreach (byte b in utf8)
            {
                sb.Append('\\').Append(b.ToString("D3", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        #endregion
    }
}
