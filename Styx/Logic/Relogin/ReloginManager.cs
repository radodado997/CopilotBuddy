using System;
using System.Diagnostics;
using System.Threading;
using Styx.Helpers;

namespace Styx.Logic.Relogin
{
    /// <summary>
    /// Auto Relogin monitor.
    ///
    /// Two failure modes are handled, both without any help from the user:
    ///
    ///   * disconnect - the client is still running but the character dropped back
    ///     to the glue screen.  After ReloginSettings.DisconnectGraceSeconds the
    ///     monitor brings the client to the front, types the configured account and
    ///     password, and enters the world again.
    ///
    ///   * crash / closed client - the process is gone.  The monitor starts
    ///     ReloginSettings.WowExePath, waits for its window, logs in, enters the
    ///     world, and if the first attempt fails it kills the client and starts over.
    ///
    /// The monitor only *observes* the client; re-attaching the bot afterwards is
    /// the UI's job (MainWindow subscribes to <see cref="ClientLoggedIn"/>).
    ///
    /// State is read with a plain ReadProcessMemory of the "in game" flag, so the
    /// monitor works with no bot attachment at all (glue screen, fresh client).
    /// </summary>
    public sealed class ReloginManager
    {
        #region Fields

        public static ReloginManager Instance { get; } = new ReloginManager();

        private ReloginManager()
        {
        }

        /// <summary>Human readable status line, mirrored to the log by the UI.</summary>
        public event Action<string>? StatusChanged;

        /// <summary>Raised once when the client process disappears (crash or closed).</summary>
        public event Action<int>? ClientLost;

        /// <summary>Raised once when a running client drops the character out of the world.</summary>
        public event Action<int>? ClientDisconnected;

        /// <summary>Raised once when a client is confirmed to be back in the world.</summary>
        public event Action<int>? ClientLoggedIn;

        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private readonly object _stateLock = new object();

        private Thread? _thread;
        private volatile bool _stopRequested;
        private volatile bool _suspended;

        private string _status = "Idle";
        private volatile bool _busy;
        private int _attempts;
        private int _lastPid;

        // Session state. Only ever written by the monitor thread (Tick and the relogin
        // sequence it runs); RequestLoggedInSignal does not touch it but asks Tick to
        // re-arm the transition through _loggedInSignalRequested.
        private bool _wasInGame;

        /// <summary>
        /// Pid that produced the current in-game state. The loss transition has to be
        /// reported for *this* pid - the monitor may have moved to a different client
        /// (pinned target exited, another client found) and must not lose the transition
        /// or attribute it to the wrong process.
        /// </summary>
        private int _inGamePid;

        /// <summary>Set by RequestLoggedInSignal (any thread), consumed by Tick.</summary>
        private volatile bool _loggedInSignalRequested;

        /// <summary>
        /// Set by the UI (BeginUiDetach) while it tears its attachment down after a loss
        /// event and cleared by it once DetachFromGame returned (EndUiDetach). The relogin
        /// sequence waits for it before it restarts or launches a client and before it types
        /// credentials, so the two state machines do not interleave on the same client.
        /// </summary>
        private volatile bool _uiDetachInProgress;
        private DateTime? _outOfGameSinceUtc;
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private int _pinnedProcessId;

        /// <summary>Cool-down after MaxAttemptsPerDisconnect failures.</summary>
        private const int FailureCoolDownSeconds = 300;

        #endregion

        #region Properties

        public bool IsRunning => _thread != null && _thread.IsAlive;

        public string Status
        {
            get { lock (_stateLock) return _status; }
        }

        /// <summary>
        /// Client the monitor must stay on while it is alive - the explicit /pid= target
        /// the bot was started with. 0 = follow any client (the configured executable,
        /// then any live one). Cleared automatically once that process exits, so a crashed
        /// pinned client can still be relaunched and adopted.
        /// </summary>
        public int PinnedProcessId
        {
            get => Volatile.Read(ref _pinnedProcessId);
            set => Volatile.Write(ref _pinnedProcessId, value);
        }

        /// <summary>True while a launch/login sequence is in flight.</summary>
        public bool IsRelogging => _busy;

        /// <summary>True when the monitor is paused (intentional logout, manual pause).</summary>
        public bool IsSuspended => _suspended;

        #endregion

        #region Control

        /// <summary>
        /// Starts the monitor thread. Safe to call repeatedly; the monitor is a
        /// no-op while ReloginSettings.Enabled is false.
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (IsRunning)
                    return;

                _stopRequested = false;
                _suspended = false;
                _outOfGameSinceUtc = null;
                _nextAttemptUtc = DateTime.MinValue;

                _thread = new Thread(MonitorLoop)
                {
                    IsBackground = true,
                    Name = "CopilotBuddy.AutoRelogin"
                };
                _thread.Start();

                Logging.WriteDebug("[AutoRelogin] Monitor started.");
            }
        }

        /// <summary>Stops the monitor thread and waits briefly for it to leave.</summary>
        public void Stop()
        {
            Thread? thread;
            lock (_stateLock)
            {
                if (!IsRunning)
                    return;

                _stopRequested = true;
                thread = _thread;
                _thread = null;
            }

            try { _wake.Set(); } catch { /* disposed */ }

            try
            {
                thread?.Join(3000);
            }
            catch
            {
                // ignore
            }

            SetStatus("Idle");
            Logging.WriteDebug("[AutoRelogin] Monitor stopped.");
        }

        /// <summary>
        /// Called when the bot logs the character out on purpose (e.g. the inactivity
        /// detector). Auto Relogin would otherwise fight the bot and log straight
        /// back in.
        /// </summary>
        public void NotifyIntentionalLogout()
        {
            _suspended = true;
            SetStatus("Paused - the bot logged out on purpose.");
            Logging.Write("[AutoRelogin] Intentional logout detected; auto relogin paused until it is enabled again.");
        }

        /// <summary>Clears a pause set by <see cref="NotifyIntentionalLogout"/>.</summary>
        public void Resume()
        {
            _suspended = false;
            _outOfGameSinceUtc = DateTime.UtcNow;
            SetStatus("Monitoring.");
        }

        #endregion

        #region Public helpers

        /// <summary>
        /// Blocks until a client is confirmed in game, or the timeout / cancellation
        /// fires. Used by the startup path when the bot has to wait for Auto Relogin
        /// to bring the client up before it can attach.
        /// </summary>
        public bool WaitForInGame(int timeoutMs, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancellationToken.IsCancellationRequested || _stopRequested)
                    return false;

                Process? client = ReloginNative.FindClient(ReloginSettings.Instance.WowExePath, PinnedProcessId);
                if (client != null && ReloginNative.TryReadInGame(client, out bool inGame) && inGame)
                    return true;

                Thread.Sleep(500);
            }

            return false;
        }

        /// <summary>
        /// Re-arms the "client is in the world" transition so the next monitor tick raises
        /// <see cref="ClientLoggedIn"/> again. Used when the bot could not attach to a
        /// client that is already in game (busy mutex, transient failure), where waiting
        /// for the character to leave and re-enter the world is not an option.
        ///
        /// Only a flag is set here: the session state itself stays owned by the monitor
        /// thread, which consumes the request before it looks at the client.
        /// </summary>
        public void RequestLoggedInSignal()
        {
            _loggedInSignalRequested = true;
            try { _wake.Set(); } catch { /* disposed */ }
        }

        /// <summary>
        /// Announces that the UI is about to detach from the lost client. Called before its
        /// detach worker starts, so the monitor defers the relogin instead of launching a
        /// client and typing into it while the old attachment is still being shut down.
        /// </summary>
        public void BeginUiDetach()
        {
            _uiDetachInProgress = true;
        }

        /// <summary>
        /// Announces that the UI finished detaching (called once <c>DetachFromGame</c>
        /// returned, or when the detach was abandoned because the application is closing).
        /// </summary>
        public void EndUiDetach()
        {
            _uiDetachInProgress = false;
        }

        /// <summary>
        /// Waits (bounded) while the UI is still detaching. Returns false when the sequence
        /// has to be abandoned (shutdown / feature disabled); the caller then leaves the
        /// attempt to the next tick, which keeps the existing grace period, the _busy check
        /// and the stop-request checks untouched.
        /// </summary>
        private bool WaitForUiDetach()
        {
            if (!_uiDetachInProgress)
                return true;

            SetStatus("Waiting for CopilotBuddy to detach from the client.");

            // The detach also waits for a bot tick that may be stuck in native code; after a
            // bounded wait the sequence continues anyway, because the attachment belongs to
            // the client that is already gone and blocking the monitor would only delay the
            // session further.
            const int maxWaitMs = 15000;

            for (int waited = 0; waited < maxWaitMs && _uiDetachInProgress; waited += 250)
            {
                if (!SleepInterruptible(250))
                    return false;
            }

            if (_uiDetachInProgress)
            {
                Logging.Write(System.Windows.Media.Colors.Orange,
                    "[AutoRelogin] CopilotBuddy is still detaching after {0}s; continuing with the relogin.", maxWaitMs / 1000);
            }

            return true;
        }

        /// <summary>True when a client process is currently in the world.</summary>
        public bool IsAnyClientInGame()
        {
            Process? client = ReloginNative.FindClient(ReloginSettings.Instance.WowExePath, PinnedProcessId);
            return client != null && ReloginNative.TryReadInGame(client, out bool inGame) && inGame;
        }

        #endregion

        #region Monitor

        private void MonitorLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Logging.WriteDebug("[AutoRelogin] Monitor tick failed: {0}", ex.Message);
                }

                int intervalMs;
                try
                {
                    intervalMs = Math.Max(1, ReloginSettings.Instance.MonitorIntervalSeconds) * 1000;
                }
                catch
                {
                    intervalMs = 5000;
                }

                try
                {
                    _wake.Wait(intervalMs);
                    _wake.Reset();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private void Tick()
        {
            if (_stopRequested)
                return;

            ReloginSettings settings = ReloginSettings.Instance;

            if (!settings.Enabled || _suspended)
                return;

            // Honor a pending re-arm request before looking at the client, so this tick
            // can already produce the requested ClientLoggedIn transition.
            if (_loggedInSignalRequested)
            {
                _loggedInSignalRequested = false;
                _wasInGame = false;
                _inGamePid = 0;
            }

            // Drop the pin as soon as the target exits: from then on any live client may
            // be relaunched / adopted again.
            int pinned = PinnedProcessId;
            if (pinned != 0 && !ReloginNative.IsProcessAlive(pinned))
            {
                Logging.Write("[AutoRelogin] The pinned client (pid {0}) is gone; following any live client again.", pinned);
                PinnedProcessId = 0;
                pinned = 0;
            }

            Process? client = ReloginNative.FindClient(settings.WowExePath, pinned);

            // ---------------------------------------------------------------- no client
            if (client == null)
            {
                if (_wasInGame)
                {
                    _wasInGame = false;
                    int lostPid = _inGamePid != 0 ? _inGamePid : _lastPid;
                    _inGamePid = 0;
                    SetStatus($"Game client {lostPid} is gone (crash or closed).");
                    SafeRaise(ClientLost, lostPid);
                }

                _outOfGameSinceUtc ??= DateTime.UtcNow;

                // A crashed client is relaunched immediately (after the configured
                // delay); there is nothing to wait for, so no grace period here.
                TryRelogin("client is not running", null);
                return;
            }

            // The client that was in the world is gone while another one is running (the
            // pinned target exited, or the monitor fell back to a different client). Report
            // the loss for the pid that actually entered the world *before* adopting the
            // new one, so the app detaches from it and the new pid can raise its own
            // ClientLoggedIn later.
            if (_wasInGame && _inGamePid != 0 && _inGamePid != client.Id && !ReloginNative.IsProcessAlive(_inGamePid))
            {
                _wasInGame = false;
                SetStatus($"Game client {_inGamePid} is gone (crash or closed).");
                SafeRaise(ClientLost, _inGamePid);
                _inGamePid = 0;
            }

            _lastPid = client.Id;

            // --------------------------------------------------- client not readable yet
            if (!ReloginNative.TryReadInGame(client, out bool inGame))
            {
                // Starting up or already exiting. Treat as out of game, but do not act
                // until the process is readable so a half-started client is not fought.
                _outOfGameSinceUtc ??= DateTime.UtcNow;
                return;
            }

            // ------------------------------------------------------------- in the world
            if (inGame)
            {
                _outOfGameSinceUtc = null;
                _attempts = 0;
                _nextAttemptUtc = DateTime.MinValue;

                if (!_wasInGame)
                {
                    _wasInGame = true;
                    _inGamePid = client.Id;
                    SetStatus("Character is in the world.");
                    SafeRaise(ClientLoggedIn, client.Id);
                }

                return;
            }

            // -------------------------------------------------------- out of the world
            _outOfGameSinceUtc ??= DateTime.UtcNow;
            double outOfGameSeconds = (DateTime.UtcNow - _outOfGameSinceUtc.Value).TotalSeconds;

            // The grace period is the single threshold: loading screens and zoning stay
            // below it, so the bot is only stopped and the session torn down once we are
            // actually going to log in again (never on a slow instance load).
            double graceSeconds = Math.Max(15, settings.DisconnectGraceSeconds);
            if (outOfGameSeconds < graceSeconds)
                return;

            if (_wasInGame)
            {
                _wasInGame = false;
                _inGamePid = 0;
                SetStatus($"Character left the world (pid {client.Id}).");
                SafeRaise(ClientDisconnected, client.Id);
            }

            TryRelogin("character is not in the world", client);
        }

        #endregion

        #region Relogin sequence

        private void TryRelogin(string reason, Process? client)
        {
            if (_stopRequested || _busy)
                return;

            ReloginSettings settings = ReloginSettings.Instance;

            if (!settings.HasClientPath)
            {
                SetStatus("Auto Relogin needs a valid WoW.exe path.");
                return;
            }

            if (!settings.HasCredentials)
            {
                SetStatus("Auto Relogin needs an account name and a password.");
                return;
            }

            if (DateTime.UtcNow < _nextAttemptUtc)
                return;

            // The loss event that brought us here also starts the UI's detach worker; it
            // must be done before this sequence restarts or launches a client.
            if (!WaitForUiDetach())
                return;

            _busy = true;
            try
            {
                RunReloginSequence(reason, client, settings);
            }
            catch (Exception ex)
            {
                Logging.Write(System.Windows.Media.Colors.Red, "[AutoRelogin] Relogin failed: {0}", ex.Message);
                Logging.WriteDebug("[AutoRelogin] Exception: {0}", ex);
                ApplyBackoff(settings);
            }
            finally
            {
                _busy = false;
            }
        }

        private void RunReloginSequence(string reason, Process? client, ReloginSettings settings)
        {
            _attempts++;

            Logging.Write("[AutoRelogin] Relogin attempt {0}/{1} ({2}).",
                _attempts, settings.MaxAttemptsPerDisconnect, reason);

            if (ShouldAbortSequence())
                return;

            // ------------------------------------------------- restart a stuck client
            bool restartClient = _attempts > 1 && settings.RestartClientOnRepeatedFailure;
            if (restartClient && client != null)
            {
                SetStatus("Restarting the game client.");
                Logging.Write("[AutoRelogin] Restarting the client after a failed attempt.");

                if (!ReloginNative.KillClient(client))
                {
                    // Termination failed: do not launch a second client next to the one
                    // that is still running. Keep the reference, back off and let the
                    // next attempt deal with it (it may have exited by then).
                    Logging.Write(System.Windows.Media.Colors.Red,
                        "[AutoRelogin] Could not terminate the game client (pid {0}).", client.Id);
                    ApplyBackoff(settings);
                    return;
                }

                client = null;
                SleepInterruptible(4000);

                if (ShouldAbortSequence())
                    return;
            }

            // ------------------------------------------------------------- start client
            if (client == null)
            {
                int delay = Math.Max(0, settings.RelaunchDelaySeconds);
                if (delay > 0)
                {
                    SetStatus($"Starting the client in {delay}s.");
                    if (!SleepInterruptible(delay * 1000))
                        return;
                }

                // The detach may have begun after this sequence claimed the work (the loss
                // event is dispatched asynchronously); never launch the replacement client
                // into a half torn down attachment.
                if (!WaitForUiDetach())
                    return;

                SetStatus("Starting the game client.");
                client = ReloginNative.StartClient(settings.WowExePath, settings.WowArguments);
                if (client == null)
                {
                    ApplyBackoff(settings);
                    return;
                }

                _lastPid = client.Id;
            }

            // ----------------------------------------------------------- wait for window
            SetStatus("Waiting for the game window.");
            IntPtr window = WaitForWindow(client, settings.ClientStartupTimeoutSeconds * 1000);
            if (window == IntPtr.Zero)
            {
                Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] The client window did not appear within {0}s.", settings.ClientStartupTimeoutSeconds);
                ApplyBackoff(settings);
                return;
            }

            // The window exists; give the client a moment to draw the login screen.
            if (!SleepInterruptible(3000))
                return;

            // ------------------------------------------------------- credentials + enter
            // Every keystroke below is aimed at the game window: if it cannot be brought
            // to the foreground we stop instead of typing the password into whatever
            // window happens to have the focus.
            // Never type into the client while the previous attachment is still being torn
            // down (the detach can start late if the loss event was dispatched while a
            // relaunch was already in progress).
            if (!WaitForUiDetach())
                return;

            SetStatus("Sending credentials.");
            if (!EnsureClientFocus(window, settings))
                return;

            if (!SleepInterruptible(800) || ShouldAbortSequence())
                return;

            // After a disconnect the client shows a modal "You have been disconnected
            // from the server" box whose OK button is the default, so Enter dismisses
            // it. Escape is deliberately NOT used: on the glue screen it can open the
            // exit dialog (and, with the login screen already gone, kill the client).
            // If the box is not there the Enter simply submits the current, empty
            // login; the next attempt (a restarted client) starts from a clean screen.
            ReloginNative.SendKey(ReloginNative.VkReturn);

            if (!SleepInterruptible(1200) || ShouldAbortSequence())
                return;

            if (!EnsureClientFocus(window, settings))
                return;

            if (!SleepInterruptible(300) || ShouldAbortSequence())
                return;

            // Re-checked immediately before the keys: the focus can be lost during the
            // delay above (screen saver, another window stealing it), and Ctrl+A/Delete
            // must never land in whatever window is in front.
            if (!ReloginNative.IsForeground(window))
            {
                Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] The game window lost the foreground before the account field could be cleared; not sending any keystrokes.");
                ApplyBackoff(settings);
                return;
            }

            // The account field is focused by default; clear whatever the client has
            // pre-filled (after a disconnect it holds the last account name) and retype.
            ReloginNative.SendSelectAll();
            ReloginNative.SendKey(ReloginNative.VkDelete);

            if (!SleepInterruptible(200) || ShouldAbortSequence())
                return;

            // SendText stops by itself as soon as the client loses the foreground, so a
            // stray Alt+Tab in the middle of the account name cannot leak the password
            // into another window.
            if (!ReloginNative.SendText(settings.AccountName, window))
            {
                Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] Lost the foreground while typing the account name.");
                ApplyBackoff(settings);
                return;
            }

            if (!SleepInterruptible(400) || ShouldAbortSequence())
                return;

            ReloginNative.SendKey(ReloginNative.VkTab);

            if (!SleepInterruptible(300) || ShouldAbortSequence())
                return;

            if (!ReloginNative.SendText(settings.Password, window))
            {
                Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] Lost the foreground while typing the password.");
                ApplyBackoff(settings);
                return;
            }

            if (!SleepInterruptible(300) || ShouldAbortSequence())
                return;

            ReloginNative.SendKey(ReloginNative.VkReturn);

            SetStatus("Waiting for the character list.");
            if (!SleepInterruptible(Math.Max(5, settings.CharSelectDelaySeconds) * 1000))
                return;

            // Character select: the first character is preselected and the "Enter World"
            // button has the focus once the list is loaded, so Enter enters the world.
            // The ladder below covers the other cases - a still loading list (focus not
            // on the button yet), a realm list, or the "press Enter again" prompt - by
            // alternating Enter with Tab + Enter until the deadline expires.
            SetStatus("Entering the world.");
            DateTime enterDeadline = DateTime.UtcNow.AddSeconds(Math.Max(30, settings.EnterWorldTimeoutSeconds));
            int press = 0;
            while (DateTime.UtcNow < enterDeadline && !ShouldAbortSequence())
            {
                // Never aim Enter/Tab at a window that is not the client; just wait for
                // the next round instead of fighting the foreground lock.
                if (!ReloginNative.FocusWindow(window))
                {
                    SleepInterruptible(1000);
                    continue;
                }

                if (press++ % 2 == 0)
                {
                    ReloginNative.SendKey(ReloginNative.VkReturn);
                }
                else
                {
                    ReloginNative.SendKey(ReloginNative.VkTab);
                    SleepInterruptible(250);
                    ReloginNative.SendKey(ReloginNative.VkReturn);
                }

                if (WaitForClientInGame(client, 6000))
                    break;
            }

            if (IsProcessInGame(client))
            {
                _attempts = 0;
                _outOfGameSinceUtc = null;
                _nextAttemptUtc = DateTime.MinValue;
                _wasInGame = true;
                _inGamePid = client.Id;
                SetStatus("Relogin successful - character is in the world.");
                Logging.Write(System.Windows.Media.Colors.Green, "[AutoRelogin] Client is back in the world (pid {0}).", client.Id);
                SafeRaise(ClientLoggedIn, client.Id);
                return;
            }

            Logging.Write(System.Windows.Media.Colors.Red,
                "[AutoRelogin] Still not in the world after {0}s.", settings.EnterWorldTimeoutSeconds);
            ApplyBackoff(settings);
        }

        private void ApplyBackoff(ReloginSettings settings)
        {
            int maxAttempts = Math.Max(1, settings.MaxAttemptsPerDisconnect);

            if (_attempts >= maxAttempts)
            {
                Logging.Write(System.Windows.Media.Colors.Red,
                    "[AutoRelogin] Giving up after {0} attempts; retrying in {1}s.", _attempts, FailureCoolDownSeconds);
                SetStatus("Relogin failed - cooling down.");
                _attempts = 0;
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(FailureCoolDownSeconds);
                return;
            }

            int backoff = Math.Min(120, 10 * _attempts);
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
            SetStatus($"Relogin failed - next attempt in {backoff}s.");
        }

        private IntPtr WaitForWindow(Process client, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (_stopRequested)
                    return IntPtr.Zero;

                try
                {
                    client.Refresh();
                    if (client.HasExited)
                        return IntPtr.Zero;

                    IntPtr handle = client.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                        return handle;
                }
                catch
                {
                    return IntPtr.Zero;
                }

                Thread.Sleep(500);
            }

            return IntPtr.Zero;
        }

        private static bool IsProcessInGame(Process client)
        {
            return ReloginNative.TryReadInGame(client, out bool inGame) && inGame;
        }

        private bool WaitForClientInGame(Process client, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (ShouldAbortSequence())
                    return false;

                if (ReloginNative.TryReadInGame(client, out bool inGame) && inGame)
                    return true;

                Thread.Sleep(500);
            }

            return false;
        }

        /// <summary>
        /// True when the sequence must not continue: the monitor was stopped, the bot
        /// logged out on purpose, or the user disabled the feature while we were waiting.
        /// Checked before every key / process operation so a Stop() or a settings change
        /// during a SleepInterruptible cannot be missed.
        /// </summary>
        private bool ShouldAbortSequence()
        {
            if (_stopRequested || _suspended)
                return true;

            try
            {
                return !ReloginSettings.Instance.Enabled;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Brings the client to the foreground or backs off. Returns false when the client
        /// cannot be focused, so the caller never sends keystrokes to an unknown window.
        /// </summary>
        private bool EnsureClientFocus(IntPtr window, ReloginSettings settings)
        {
            if (ReloginNative.FocusWindow(window))
                return true;

            Logging.Write(System.Windows.Media.Colors.Red,
                "[AutoRelogin] Could not bring the game window to the foreground; not sending any keystrokes.");
            ApplyBackoff(settings);
            return false;
        }

        /// <summary>
        /// Sleep that aborts early when the sequence has to stop. Returns true when the
        /// whole delay elapsed, false when it was interrupted.
        /// </summary>
        private bool SleepInterruptible(int milliseconds)
        {
            int remaining = milliseconds;
            while (remaining > 0)
            {
                if (ShouldAbortSequence())
                    return false;

                int slice = Math.Min(250, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }

            return !ShouldAbortSequence();
        }

        #endregion

        #region Helpers

        private void SetStatus(string status)
        {
            lock (_stateLock)
            {
                _status = status;
            }

            try
            {
                StatusChanged?.Invoke(status);
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[AutoRelogin] Status handler failed: {0}", ex.Message);
            }
        }

        private static void SafeRaise(Action<int>? handler, int pid)
        {
            if (handler == null)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<int>)subscriber)(pid);
                }
                catch (Exception ex)
                {
                    Logging.WriteDebug("[AutoRelogin] Event handler failed: {0}", ex.Message);
                }
            }
        }

        #endregion
    }
}
