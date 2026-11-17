using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Relogin
{
    /// <summary>Why a relogin sequence was started. Reported in the log for every attempt.</summary>
    public enum ReloginTrigger
    {
        None = 0,
        /// <summary>The client process disappeared (crash, killed, or closed by the user).</summary>
        ProcessExited,
        /// <summary>The process is alive but its window stopped responding for longer than HangGraceSeconds.</summary>
        ClientHung,
        /// <summary>The client dropped out of the world to the login screen while the bot was running.</summary>
        ClientLoggedOut
    }

    /// <summary>What the sequence is doing right now, surfaced in the log and as names.</summary>
    public enum ReloginStep
    {
        Idle = 0,
        WaitingForRetryDelay,
        StartingClient,
        WaitingForLoginScreen,
        LoggingIn,
        WaitingForCharacterSelect,
        SelectingCharacter,
        WaitingForInWorld
    }

    /// <summary>
    /// Detection + retry + glue orchestration for auto-relogin.
    ///
    /// How this fits the existing code:
    ///  * Detection runs on a dedicated background thread, not inside TreeRoot's tick.
    ///    TreeRoot already stops the bot when the process exits ("WoW process has exited")
    ///    and only ticks while the bot is running — neither can relaunch a client. A separate
    ///    thread also means the feature is completely inert while the option is disabled.
    ///  * "Process is gone" is detected with HasExited on the tracked Process, the same check
    ///    TreeRoot.RunTickBody and the minimize guard already use. Memory-read failures are
    ///    never treated as a crash: they are counted only after HangGraceSeconds of
    ///    *continuous* failure and reset on the first successful read, so lag never fires it.
    ///  * Glue state is the client's own screen-name string (0x00B6A9E0, the address HBRelog
    ///    uses for this build); in-world state is g_ClientConnectionState (0xBD0792), the same
    ///    byte ObjectManager.IsInGame reads.
    ///  * Login input reuses the project's existing Lua pipeline (Lua.DoString) — the injected
    ///    EndScene mechanism every in-game action already goes through.
    ///  * Resuming is raised through <see cref="ResumeBot"/> only after the in-world state is
    ///    confirmed, so the caller restarts the same BotBase/profile through the normal UI path.
    /// </summary>
    public static class ReloginManager
    {
        #region Configuration snapshot

        /// <summary>
        /// Values copied from <see cref="ReloginSettings"/> when the monitor starts, so a save
        /// in the middle of a sequence cannot change the semantics of an in-flight attempt.
        /// Re-read on every <see cref="Start"/>.
        /// </summary>
        private sealed class Config
        {
            public bool Enabled;
            public string AccountName = "";
            public string Password = "";
            public string RealmName = "";
            public string CharacterName = "";
            public string WowExecutablePath = "";
            public int RetryDelaySeconds = 30;
            public int MaxRetryCount = 3;
            public int LoginScreenTimeoutSeconds = 120;
            public int InWorldTimeoutSeconds = 180;
            public int HangGraceSeconds = 45;
            public int LoginRejectSeconds = 20;

            public static Config FromSettings(ReloginSettings s)
            {
                return new Config
                {
                    Enabled = s.Enabled,
                    AccountName = (s.AccountName ?? "").Trim(),
                    Password = s.Password ?? "",
                    RealmName = (s.RealmName ?? "").Trim(),
                    CharacterName = (s.CharacterName ?? "").Trim(),
                    WowExecutablePath = (s.WowExecutablePath ?? "").Trim(),
                    RetryDelaySeconds = Math.Max(1, s.RetryDelaySeconds),
                    MaxRetryCount = Math.Max(0, s.MaxRetryCount),
                    LoginScreenTimeoutSeconds = Math.Max(10, s.LoginScreenTimeoutSeconds),
                    InWorldTimeoutSeconds = Math.Max(10, s.InWorldTimeoutSeconds),
                    HangGraceSeconds = Math.Max(0, s.HangGraceSeconds),
                    LoginRejectSeconds = Math.Max(5, s.LoginRejectSeconds)
                };
            }

            /// <summary>
            /// Credentials are mandatory: without them a relaunched client would sit on the
            /// login form forever. Returns the user-facing reason when unusable.
            /// </summary>
            public string? Validate()
            {
                if (string.IsNullOrEmpty(AccountName) || string.IsNullOrEmpty(Password))
                    return "relogin credentials are not configured (account name / password)";
                if (string.IsNullOrEmpty(CharacterName))
                    return "relogin target character is not configured";
                return null;
            }
        }

        #endregion

        #region Fields

        private const int PollIntervalMs = 1000;

        /// <summary>Same threshold HBRelog uses for "WoW is not responding".</summary>
        private const int RespondingGraceMs = 20_000;

        private static readonly object _sync = new object();
        private static Thread? _thread;

        /// <summary>
        /// Monitor generation. Bumped by both <see cref="Start"/> and <see cref="Stop"/>;
        /// a monitor (and any relogin sequence it is running) only keeps working while its
        /// own generation is still current. This is what makes a slow Stop() safe: a thread
        /// that outlives the bounded join can never be joined by a second monitor thread.
        /// </summary>
        private static int _generation;

        private static volatile bool _sequenceActive;
        private static volatile bool _gaveUp;

        /// <summary>
        /// ProcessMutex claim for a client this feature launched. The mutex MainWindow holds
        /// belongs to the client it attached to, which after a crash is the dead one, so the
        /// replacement has to be claimed here or a second CopilotBuddy could attach to it.
        /// Handed over to MainWindow when the bot resumes.
        ///
        /// The claim is owned by one recovery generation: a monitor that outlives the bounded
        /// join can only ever drop its own claim, never the one a newer run has taken.
        /// </summary>
        private static Mutex? _claimedMutex;

        /// <summary>Recovery generation that owns <see cref="_claimedMutex"/> (0 = none).</summary>
        private static int _claimedMutexGeneration;

        private static Config _config = new Config();
        private static int _processId;

        private static int _attempt;
        private static ReloginTrigger _trigger = ReloginTrigger.None;

        #endregion

        #region Public state

        /// <summary>True while a relogin sequence (retry loop included) is running.</summary>
        public static bool IsSequenceActive => _sequenceActive;

        /// <summary>Current step of the sequence — for logs and diagnostics.</summary>
        public static ReloginStep CurrentStep { get; private set; } = ReloginStep.Idle;

        /// <summary>
        /// Raised once, after a relogin attempt reached the in-world state. The handler must
        /// restart the bot (the caller maps this to the normal Start path).
        /// </summary>
        /// <summary>
        /// Raised (on a ThreadPool thread) once a client is confirmed in world. The argument is
        /// the recovery generation that produced it: subscribers that hop to another thread
        /// must carry it and check <see cref="IsCurrentGeneration"/> before starting the bot,
        /// so retired or cancelled recovery work can never start it.
        /// </summary>
        public static event Action<int>? ResumeBot;

        #endregion

        #region Public API

        /// <summary>
        /// (Re)starts the monitor with the current <see cref="ReloginSettings"/>. Safe to call
        /// repeatedly (e.g. after the settings window closes) and safe to call while the
        /// feature is disabled — in that case it just stops any running monitor.
        /// </summary>
        /// <param name="processId">Client process the bot is attached to; 0 = discover by name.</param>
        public static void Start(int processId)
        {
            ReloginSettings settings = ReloginSettings.Instance;

            lock (_sync)
            {
                // Retire the previous monitor. Any thread still winding down sees the new
                // generation on its next poll and exits without touching anything.
                RetireMonitorThread();

                _config = Config.FromSettings(settings);
                if (!_config.Enabled)
                {
                    // Disabled (or switched off): leave the monitor stopped. Everything else
                    // in the bot behaves exactly as if the feature did not exist.
                    _processId = 0;
                    Logging.WriteDebug("[Relogin] Auto-relogin is disabled.");
                    return;
                }

                if (processId > 0)
                    _processId = processId;

                // Claiming is delegated so ReloginClient stays free of manager state. The
                // returned mutex is stored here and handed to MainWindow on resume.
                // The delegate is replaced on every Start(), so it carries the generation of
                // the run that installed it: a claim made by a retired monitor is dropped by
                // that monitor alone (see ReleaseClaimIfOlderThan).
                int claimGeneration = generation;
                ReloginClient.ClaimProcess = pid =>
                {
                    try
                    {
                        Mutex mutex = ProcessMutex.Create(pid, out bool createdNew);
                        if (!createdNew)
                        {
                            // Another CopilotBuddy already owns this client: don't attach.
                            mutex.Close();
                            string message = string.Format(
                                "the relaunched client (pid {0}) is already claimed by another CopilotBuddy instance", pid);
                            Logging.Write(Colors.Red, "[Relogin] Cannot attach: {0}.", message);
                            return (null, message);
                        }

                        // _sync is not taken here: Start()/Stop() already hold it when the
                        // delegate is installed, and the sequence runs on the monitor thread
                        // where a re-entrant lock would deadlock. Assignment is atomic.
                        Mutex? previous = Interlocked.Exchange(ref _claimedMutex, mutex);
                        Volatile.Write(ref _claimedMutexGeneration, claimGeneration);
                        try { previous?.Dispose(); } catch { }

                        return (mutex, "");
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteDebug("[Relogin] Could not claim pid {0}: {1}", pid, ex.Message);
                        return (null, "the relaunched client could not be claimed: " + ex.Message);
                    }
                };

                // The attach path disposes a claim itself when it cannot use it; the manager
                // must then forget it, or the resume path would hand out a disposed mutex.
                ReloginClient.ReleaseClaimFromManager = () =>
                {
                    // Only the reference is dropped: the attach path owns this instance and
                    // disposes it itself, so nothing is disposed twice.
                    Interlocked.Exchange(ref _claimedMutex, null);
                    Volatile.Write(ref _claimedMutexGeneration, 0);
                };

                _gaveUp = false;
                _attempt = 0;
                CurrentStep = ReloginStep.Idle;
                int generation = _generation;

                // Report an incomplete configuration once, up front, instead of making the
                // user wait for a crash to discover it. Relogin itself still fails safely
                // (and reports the exact reason) if it is triggered in this state.
                string? configError = _config.Validate();
                if (configError != null)
                    Logging.Write(Colors.Red, "[Relogin] Auto-relogin is enabled but {0}. Fill it in before the client crashes.", configError);
                else if (string.IsNullOrEmpty(_config.WowExecutablePath))
                    Logging.Write("[Relogin] Note: no WoW.exe path set — a crashed client cannot be relaunched, only an existing one logged back in.");

                _thread = new Thread(() => MonitorLoop(generation))
                {
                    IsBackground = true,
                    Name = "Auto-Relogin Monitor"
                };
                _thread.Start();
            }
        }

        /// <summary>
        /// Stops the monitor (application exit, or the option was turned off). A running
        /// sequence observes the stop request on its next poll and aborts cleanly.
        /// </summary>
        public static void Stop()
        {
            lock (_sync)
            {
                RetireMonitorThread();
            }
        }

        /// <summary>
        /// Called when the user starts the bot manually. Re-arms the retry budget so a fresh
        /// manual start re-enables the feature after it gave up.
        /// </summary>
        public static void OnBotStarted()
        {
            if (_attempt > 0 && !_sequenceActive)
                Logging.WriteDebug("[Relogin] Retry budget re-armed by a manual bot start.");
            _attempt = 0;
            _gaveUp = false;
        }

        /// <summary>
        /// Updates the process the monitor watches (after a successful relogin, or when the
        /// bot attaches to a different client).
        /// </summary>
        public static void SetAttachedProcess(int processId)
        {
            if (processId > 0)
                _processId = processId;
        }

        /// <summary>The client pid the monitor is currently watching.</summary>
        public static int AttachedProcessId => _processId;

        /// <summary>
        /// Takes ownership of the process claim created for a relaunched client, if any. The
        /// caller (the main window) then becomes responsible for disposing it. Returns null
        /// when no replacement client was launched, i.e. the existing claim still applies.
        /// </summary>
        internal static Mutex? TakeClaimedMutex()
        {
            lock (_sync)
            {
                Mutex? claimed = _claimedMutex;
                _claimedMutex = null;
                Volatile.Write(ref _claimedMutexGeneration, 0);
                return claimed;
            }
        }

        /// <summary>
        /// Drops the claim only if it was made by a generation older than
        /// <paramref name="generation"/>. A monitor that outlived the bounded join must not
        /// dispose the claim a newer run is already relying on, and the current run's claim is
        /// kept until its own failure cleanup or the hand-over to the UI.
        /// </summary>
        private static void ReleaseClaimIfOlderThan(int generation)
        {
            Mutex? stale = null;
            lock (_sync)
            {
                if (_claimedMutex != null && Volatile.Read(ref _claimedMutexGeneration) < generation)
                {
                    stale = _claimedMutex;
                    _claimedMutex = null;
                    Volatile.Write(ref _claimedMutexGeneration, 0);
                }
            }

            try { stale?.Dispose(); } catch { }
        }

        #endregion

        #region Monitor

        /// <summary>
        /// Retires the current monitor: every loop in this class compares its own generation
        /// against <see cref="_generation"/>, so bumping it is what makes the thread exit.
        /// The join is bounded — a monitor stuck in a native call is simply left to notice
        /// the new generation when it returns.
        /// </summary>
        private static void RetireMonitorThread()
        {
            Interlocked.Increment(ref _generation);

            Thread? thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                // The loops poll at 1 Hz; anything longer than a couple of seconds means a
                // blocking native call, which must not hold up the caller.
                try { thread.Join(3000); } catch { /* shutting down */ }
            }
        }

        /// <summary>
        /// True while <paramref name="generation"/> is the live monitor generation. Every
        /// poll in the monitor and in a relogin sequence tests this.
        /// </summary>
        private static bool IsCurrent(int generation)
        {
            return generation == Volatile.Read(ref _generation);
        }

        /// <summary>
        /// Public form of the generation check, for code that resumes on another thread (the
        /// UI) and must not act on recovery work that was retired while it was queued.
        /// </summary>
        public static bool IsCurrentGeneration(int generation) => IsCurrent(generation);

        /// <summary>The live monitor generation, for a subscriber that has not been given one.</summary>
        public static int CurrentGeneration => Volatile.Read(ref _generation);

        private static void MonitorLoop(int generation)
        {
            Process? client = null;
            var unresponsiveFor = new Stopwatch();
            var notInWorldFor = new Stopwatch();
            bool? lastInWorld = null;

            // Armed once the client is seen leaving the world while the bot is running, and
            // kept across loading polls. A logout commonly goes world -> loading screen ->
            // "login", so a loading poll must not disarm the detector; only a return to the
            // world, a stopped bot, or a fired trigger clears it.
            bool worldDeparted = false;

            // Confirmation timer running: the client has been sitting on the login or
            // character screen continuously since it was armed.
            bool pendingLogout = false;

            try
            {
                if (_processId <= 0)
                {
                    client = ReloginClient.FindClientProcessByName();
                    if (client != null)
                        _processId = client.Id;
                }

                Logging.WriteDebug("[Relogin] Monitor started (pid {0}, hang grace {1}s, retry delay {2}s, max retries {3}).",
                    _processId, _config.HangGraceSeconds, _config.RetryDelaySeconds, _config.MaxRetryCount);

                while (IsCurrent(generation))
                {
                    if (_sequenceActive)
                    {
                        SleepPollSlices(generation);
                        continue;
                    }

                    // Re-resolve every poll: after a relaunch the old Process object refers to
                    // the dead client, and re-using it would immediately look like another exit.
                    // Name discovery is only a bootstrap: once this session tracks a pid, a
                    // vanished pid means the client we were watching died. Adopting some other
                    // running client instead would silently hijack a process the user started.
                    client = _processId > 0
                        ? ReloginClient.FindProcessById(_processId)
                        : ReloginClient.FindClientProcessByName();
                    if (client != null)
                        _processId = client.Id;

                    if (!ReloginClient.IsAlive(client))
                    {
                        // A dead process is the one unambiguous crash/exit signal.
                        FireTrigger(ReloginTrigger.ProcessExited, "client process is gone", generation);
                        client = null;
                        unresponsiveFor.Reset();
                        notInWorldFor.Reset();
                        worldDeparted = false;
                        pendingLogout = false;
                        lastInWorld = null;
                        SleepPollSlices(generation);
                        continue;
                    }

                    int pid = client!.Id;
                    ReloginClient.ClientProbe probe = ReloginClient.Probe(pid);

                    // ---- Hang detection: window not responding, sustained ------------------
                    // Responding is the same signal HBRelog uses; unlike a raw memory read it
                    // is not perturbed by a lagging server, and a single hung frame is
                    // filtered out by the grace window.
                    if (_config.HangGraceSeconds > 0 && IsWindowNotResponding(client))
                    {
                        if (!unresponsiveFor.IsRunning)
                        {
                            unresponsiveFor.Start();
                            Logging.WriteDebug("[Relogin] Client window is not responding; watching for {0}s before treating it as a hang.",
                                Math.Max(_config.HangGraceSeconds, RespondingGraceMs / 1000));
                        }
                        else if (unresponsiveFor.ElapsedMilliseconds >= Math.Max(_config.HangGraceSeconds, RespondingGraceMs / 1000) * 1000L)
                        {
                            FireTrigger(ReloginTrigger.ClientHung,
                                string.Format("client window unresponsive for {0:F0}s", unresponsiveFor.Elapsed.TotalSeconds),
                                generation);
                            unresponsiveFor.Reset();
                            notInWorldFor.Reset();
                            worldDeparted = false;
                            pendingLogout = false;
                            lastInWorld = null;
                            continue;
                        }
                    }
                    else if (unresponsiveFor.IsRunning)
                    {
                        Logging.WriteDebug("[Relogin] Client window responding again after {0:F0}s; no relogin needed.",
                            unresponsiveFor.Elapsed.TotalSeconds);
                        unresponsiveFor.Reset();
                    }

                    // ---- Logout detection: dropped from the world to the login screen -----
                    // The transition has to be remembered across polls: after the first poll
                    // without the in-world flag, lastInWorld is false, so the confirmation
                    // window must be carried by an explicit pending state instead.
                    if (probe.Readable)
                    {
                        if (probe.InGame)
                        {
                            // Back in the world: whatever departure was being tracked is over.
                            if (worldDeparted)
                                Logging.WriteDebug("[Relogin] Client is back in the world; pending logout cancelled.");
                            worldDeparted = false;
                            pendingLogout = false;
                            notInWorldFor.Reset();
                        }
                        else
                        {
                            // The in-world flag dropped. Arm on the departure itself, while the
                            // client can still be seen leaving, and keep that armed state across
                            // loading polls — a logout is world -> loading -> "login", so
                            // arming only once a glue screen is visible would miss it.
                            //
                            // Only while the bot is running: if the bot is stopped the user is in
                            // control of the client, and logging out is exactly what they asked
                            // for. Relogin only covers a client that drops out underneath the bot.
                            if (!worldDeparted && lastInWorld == true)
                            {
                                if (Styx.Logic.BehaviorTree.TreeRoot.IsRunning)
                                {
                                    worldDeparted = true;
                                    Logging.WriteDebug("[Relogin] Client left the world while the bot is running; watching for a logout.");
                                }
                                else
                                {
                                    Logging.WriteDebug("[Relogin] Client left the world while the bot is stopped; not a crash, so no relogin.");
                                }
                            }

                            if (worldDeparted)
                            {
                                // The bot can be stopped mid-way: that hands the client back to
                                // the user, so nothing is recovered from here on.
                                if (!Styx.Logic.BehaviorTree.TreeRoot.IsRunning)
                                {
                                    worldDeparted = false;
                                    pendingLogout = false;
                                    notInWorldFor.Reset();
                                }
                                else if (probe.Loading)
                                {
                                    // Part of leaving the world, not a settled logout. Keep the
                                    // armed state, but the confirmation window only starts once
                                    // a login/character screen is actually showing.
                                    pendingLogout = false;
                                    notInWorldFor.Reset();
                                }
                                else if (probe.Glue == GlueKind.Login || probe.Glue == GlueKind.CharSelect)
                                {
                                    // The screen must be stable for a few seconds first: a
                                    // zoning transition briefly races the flag.
                                    if (!pendingLogout)
                                    {
                                        pendingLogout = true;
                                        notInWorldFor.Restart();
                                    }
                                    else if (notInWorldFor.Elapsed.TotalSeconds >= LogoutConfirmSeconds)
                                    {
                                        // Recovery starts: both states are cleared so a second
                                        // logout cannot fire while this one is being handled.
                                        worldDeparted = false;
                                        pendingLogout = false;
                                        notInWorldFor.Reset();
                                        lastInWorld = null;
                                        FireTrigger(ReloginTrigger.ClientLoggedOut,
                                            string.Format("client returned to {0} and stayed there for {1}s",
                                                probe.Glue, LogoutConfirmSeconds),
                                            generation);
                                        continue;
                                    }
                                }
                                else
                                {
                                    // Some other screen (character creation, credits, ...): not
                                    // a settled logout, so the confirmation window restarts.
                                    pendingLogout = false;
                                    notInWorldFor.Reset();
                                }
                            }
                        }

                        lastInWorld = probe.InGame;
                    }
                    else
                    {
                        // Unreadable poll: the elapsed time must not be credited towards the
                        // logout confirmation across a gap in what we could observe. The armed
                        // departure survives, so a later stable logout still fires.
                        pendingLogout = false;
                        notInWorldFor.Reset();
                    }

                    SleepPollSlices(generation);
                }
            }
            catch (Exception ex)
            {
                // Never retry inside a catch: report loudly and stop so the user can react.
                Logging.Write(Colors.Red, "[Relogin] Monitor stopped after an unexpected error: {0}", ex.Message);
                Logging.WriteException(ex);
            }
            finally
            {
                CurrentStep = ReloginStep.Idle;
                _sequenceActive = false;
                Logging.WriteDebug("[Relogin] Monitor stopped.");
            }
        }

        /// <summary>How long the login/character-selection screen has to be stable to count as a logout.</summary>
        private const int LogoutConfirmSeconds = 5;

        /// <summary>
        /// How many consecutive "switch to the configured realm" answers are tolerated before
        /// the attempt is failed. Enough for a normal realm-list round trip, short enough that
        /// a character that lives on another realm is reported instead of timing out.
        /// </summary>
        private const int MaxRealmSwitches = 5;

        private static bool IsWindowNotResponding(Process client)
        {
            try
            {
                return !client.HasExited && !client.Responding;
            }
            catch
            {
                return false; // exited or handle closed — the process check handles that case
            }
        }

        private static void SleepPollSlices(int generation)
        {
            for (int slept = 0; slept < PollIntervalMs && IsCurrent(generation); slept += 100)
                Thread.Sleep(100);
        }

        private static void FireTrigger(ReloginTrigger trigger, string reason, int generation)
        {
            if (!_config.Enabled || _gaveUp || !IsCurrent(generation))
                return;

            Logging.Write("[Relogin] Relogin triggered: {0} — {1}.", trigger, reason);

            _trigger = trigger;
            _attempt = 0;
            TryRunSequence(generation);
        }

        #endregion

        #region Sequence

        /// <summary>
        /// Runs the retry loop on the monitor thread: wait, relaunch/attach, log in, select
        /// the configured character, wait for the in-world state. Every attempt is logged
        /// with its trigger reason, number, the step it reached and the failure reason.
        /// </summary>
        private static void TryRunSequence(int generation)
        {
            _sequenceActive = true;
            bool resumed = false;
            string lastFailure = "";

            try
            {
                while (IsCurrent(generation))
                {
                    int maxRetries = _config.MaxRetryCount;
                    if (_attempt >= maxRetries)
                    {
                        Logging.Write(Colors.Red, "[Relogin] Giving up after {0} attempt(s), maximum retries exhausted. Last failure — {1}.",
                            _attempt, lastFailure);
                        _gaveUp = true;
                        return;
                    }

                    _attempt++;
                    Logging.Write("[Relogin] Attempt {0}/{1} (trigger: {2}).", _attempt, maxRetries, _trigger);

                    bool ok = RunAttempt(generation, out string failure);
                    ReloginStep reachedStep = CurrentStep;

                    if (ok)
                    {
                        Logging.Write(Colors.Green, "[Relogin] Attempt {0}/{1} succeeded: character is back in the world.",
                            _attempt, maxRetries);
                        resumed = true;
                        return;
                    }

                    lastFailure = string.Format("step {0}: {1}", reachedStep, failure);
                    Logging.Write(Colors.Red, "[Relogin] Attempt {0}/{1} failed at {2}.",
                        _attempt, maxRetries, lastFailure);

                    if (IsFatal(failure))
                    {
                        Logging.Write(Colors.Red, "[Relogin] Not retrying this attempt: {0}.", failure);
                        _gaveUp = true;
                        return;
                    }

                    if (_attempt >= maxRetries)
                        continue; // the loop head reports the give-up

                    CurrentStep = ReloginStep.WaitingForRetryDelay;
                    Logging.Write("[Relogin] Waiting {0}s before attempt {1}/{2}.", _config.RetryDelaySeconds, _attempt + 1, maxRetries);
                    if (!SleepWithStop(_config.RetryDelaySeconds * 1000, generation))
                        break;
                }
            }
            finally
            {
                _sequenceActive = false;
                CurrentStep = ReloginStep.Idle;

                // A claim is only meaningful for a client that was successfully brought back;
                // anything else must not keep a stale pid claimed. Scoped to this run, so a
                // monitor that was retired mid-sequence cannot dispose a newer claim.
                if (!resumed)
                    ReleaseClaimIfOlderThan(generation + 1);

                if (resumed && IsCurrent(generation))
                {
                    // Only now, with the in-world state confirmed, does the routine/profile
                    // resume — through the caller's normal Start path.
                    int resumeGeneration = generation;
                    ThreadPool.QueueUserWorkItem(_ => ResumeBot?.Invoke(resumeGeneration));
                }
            }
        }

        /// <summary>Reasons that must not be retried because retrying cannot fix them.</summary>
        private static bool IsFatal(string reason)
        {
            return reason != null &&
                   (reason.Contains("executable path", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("server refused the login", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("updater", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SleepWithStop(int milliseconds, int generation)
        {
            for (int slept = 0; slept < milliseconds; slept += 100)
            {
                if (!IsCurrent(generation))
                    return false;
                Thread.Sleep(Math.Min(100, milliseconds - slept));
            }
            return IsCurrent(generation);
        }

        /// <summary>
        /// One full attempt. Returns true when the client reached the in-world state;
        /// otherwise false with <paramref name="failure"/> naming the failing step and why.
        /// </summary>
        private static bool RunAttempt(int generation, out string failure)
        {
            failure = "";

            string? configError = _config.Validate();
            if (configError != null)
            {
                failure = configError;
                return false;
            }

            // ---- 1. Client process ---------------------------------------------------
            CurrentStep = ReloginStep.StartingClient;
            Process? client = _processId > 0 ? ReloginClient.FindProcessById(_processId) : null;
            bool launchedReplacement = false;

            // A hung client is still running, so it has to be closed before it can be
            // relaunched — otherwise the relaunch would just attach to the frozen process.
            if (client != null && _trigger == ReloginTrigger.ClientHung)
            {
                if (!ReloginClient.TerminateClient(client, out string closeError))
                {
                    failure = "the unresponsive client could not be closed: " + closeError;
                    return false;
                }

                Logging.Write("[Relogin] Unresponsive client closed.");
                client = null;

                // TerminateClient blocks for as long as the close takes. If the monitor was
                // retired meanwhile (settings saved, bot stopped, app closing), no replacement
                // may be launched: a retired generation must not publish a new pid or attach.
                if (!IsCurrent(generation))
                {
                    failure = "stopped";
                    return false;
                }
            }

            if (client == null)
            {
                if (string.IsNullOrEmpty(_config.WowExecutablePath))
                {
                    failure = "the client is not running and the WoW executable path is not set";
                    return false;
                }
                if (!File.Exists(_config.WowExecutablePath))
                {
                    failure = string.Format("WoW executable path is invalid: {0}", _config.WowExecutablePath);
                    return false;
                }

                if (!IsCurrent(generation))
                {
                    failure = "stopped";
                    return false;
                }

                Logging.Write("[Relogin] Starting client: {0}", _config.WowExecutablePath);
                client = ReloginClient.StartClient(_config.WowExecutablePath, out string startError);
                if (client == null)
                {
                    failure = "could not start the client: " + startError;
                    return false;
                }

                _processId = client.Id;
                launchedReplacement = true;
                Logging.Write("[Relogin] Client started, pid {0}.", _processId);
            }
            else
            {
                Logging.Write("[Relogin] Client still running (pid {0}); logging it back in.", client.Id);
            }

            int pid = client.Id;

            // ---- 2. Attach + hook, then wait for a readable glue screen ---------------
            CurrentStep = ReloginStep.WaitingForLoginScreen;
            var wait = Stopwatch.StartNew();
            ReloginClient.ClientProbe probe = new ReloginClient.ClientProbe(false, "", GlueKind.Unknown, false);

            while (wait.Elapsed.TotalSeconds < _config.LoginScreenTimeoutSeconds)
            {
                if (!IsCurrent(generation)) { failure = "stopped"; return false; }

                if (!ReloginClient.IsAlive(client))
                {
                    failure = "the client exited during startup (crash loop?)";
                    return false;
                }

                if (!ReloginClient.IsAttached(pid))
                {
                    Memory? attached = ReloginClient.TryAttachAndHook(pid, out string hookError, launchedReplacement);
                    if (attached == null)
                    {
                        Logging.WriteDebug("[Relogin] Hook not ready yet ({0}); retrying.", hookError);
                        Thread.Sleep(2000);
                        continue;
                    }
                    Logging.WriteDebug("[Relogin] Attached to pid {0} and hooked EndScene.", pid);
                }

                probe = ReloginClient.Probe(pid);
                if (probe.InGame || probe.Glue != GlueKind.Unknown)
                    break;

                // Still booting (glue name not written yet) or on a screen we don't model
                // ('movie', the intro clip, ...). Keep waiting inside the timeout rather than
                // failing on the first unreadable poll.
                Thread.Sleep(1000);
            }

            if (probe.InGame)
            {
                Logging.WriteDebug("[Relogin] Client is already in the world.");
                return ConfirmInWorld(client, pid, generation, out failure);
            }

            if (probe.Glue == GlueKind.Unknown)
            {
                failure = probe.Readable
                    ? string.Format("the client stayed on an unrecognised screen \"{0}\" for {1}s",
                        probe.GlueRaw, _config.LoginScreenTimeoutSeconds)
                    : string.Format("the login screen did not become readable within {0}s", _config.LoginScreenTimeoutSeconds);
                return false;
            }

            // ---- 3+4. Credentials, realm and character, until EnterWorld is issued ----
            // One loop for both screens: the glue state decides which chunk is sent, so a
            // client that lands on the realm list, bounces back to the login form, or already
            // sits on character selection all take the correct branch on every poll.
            CurrentStep = ReloginStep.WaitingForCharacterSelect;
            var selectWait = Stopwatch.StartNew();
            int submissions = 0;
            int realmSwitches = 0;
            bool entered = false;
            bool sawLoadingSinceSubmit = false;

            while (selectWait.Elapsed.TotalSeconds < _config.LoginScreenTimeoutSeconds)
            {
                if (!IsCurrent(generation)) { failure = "stopped"; return false; }
                if (!ReloginClient.IsAlive(client)) { failure = "the client exited during login"; return false; }

                probe = ReloginClient.Probe(pid);

                if (probe.InGame)
                {
                    entered = true; // already entering the world (no character step needed)
                    break;
                }

                // Remember whether authentication ever got past the login form: it is what
                // separates "the server refused these credentials" from "the server was not
                // reachable", and the client exposes no explicit rejection flag.
                if (probe.Loading)
                    sawLoadingSinceSubmit = true;

                switch (probe.Glue)
                {
                    case GlueKind.CharSelect:
                        CurrentStep = ReloginStep.WaitingForCharacterSelect;

                        int code = ReloginClient.SendCharacterSelect(_config.CharacterName, _config.RealmName);
                        switch (code)
                        {
                            case 1:
                                CurrentStep = ReloginStep.WaitingForInWorld;
                                realmSwitches = 0;
                                Logging.Write("[Relogin] Character '{0}' selected; EnterWorld issued.", _config.CharacterName);
                                entered = true;
                                break;

                            case 4:
                                failure = string.Format("character '{0}' was not found on realm '{1}'",
                                    _config.CharacterName,
                                    string.IsNullOrEmpty(_config.RealmName) ? "?" : _config.RealmName);
                                return false;

                            case 2:
                                // Not really on the character screen (realm list / loading).
                                realmSwitches = 0;
                                ReloginClient.SendLogin(_config.AccountName, _config.Password, _config.RealmName);
                                submissions++;
                                break;

                            case 3:
                                // The client is on a different realm and was asked to go back
                                // to the realm list. A handful of these is normal while the
                                // switch completes; a client that keeps asking means the realm
                                // is not reachable for this account, so report that instead of
                                // spinning until the screen timeout.
                                if (++realmSwitches > MaxRealmSwitches)
                                {
                                    failure = string.Format(
                                        "the client could not switch to realm '{0}' (character '{1}' is not reachable there)",
                                        string.IsNullOrEmpty(_config.RealmName) ? "?" : _config.RealmName,
                                        _config.CharacterName);
                                    return false;
                                }
                                Logging.WriteVerbose("[Relogin] Switching to realm '{0}' ({1}/{2}).",
                                    _config.RealmName, realmSwitches, MaxRealmSwitches);
                                ReloginClient.SendLogin(_config.AccountName, _config.Password, _config.RealmName);
                                break;

                            default:
                                // 0 = list still loading, -1 = Lua failed. Reading again is enough.
                                realmSwitches = 0;
                                break;
                        }
                        break;

                    case GlueKind.Login:
                        CurrentStep = ReloginStep.LoggingIn;

                        // Credentials bounce back to this screen when the attempt failed, and
                        // the client gives no explicit "rejected" flag. Two independent signals
                        // are therefore required before calling it a credential problem:
                        //   1. the client never entered the loading/connecting state, i.e.
                        //      authentication never got far enough to talk to the world server,
                        //      and
                        //   2. it came back faster than LoginRejectSeconds.
                        // If the client did connect (or came back slowly) authentication may
                        // still have been in progress, so the attempt stays retryable.
                        if (submissions > 0)
                        {
                            double sinceSubmit = selectWait.Elapsed.TotalSeconds;
                            if (!sawLoadingSinceSubmit && sinceSubmit < _config.LoginRejectSeconds)
                            {
                                failure = string.Format(
                                    "the server refused the login for '{0}' (wrong account name or password, or the account is not allowed to log in)",
                                    _config.AccountName);
                                return false;
                            }

                            if (sawLoadingSinceSubmit)
                            {
                                Logging.Write("[Relogin] Client reached the connecting state and came back to the login screen; treating as a connection failure, not bad credentials.");
                            }
                        }

                        Logging.WriteVerbose("[Relogin] Submitting credentials for {0} (attempt {1}).",
                            _config.AccountName, submissions + 1);
                        ReloginClient.SendLogin(_config.AccountName, _config.Password, _config.RealmName);
                        submissions++;
                        sawLoadingSinceSubmit = false;
                        break;

                    case GlueKind.RealmWizard:
                    case GlueKind.Other:
                        CurrentStep = ReloginStep.LoggingIn;
                        ReloginClient.SendLogin(_config.AccountName, _config.Password, _config.RealmName);
                        submissions++;
                        break;

                        case GlueKind.CharCreate:
                        // WotLK opens the creation screen if no character exists; back out of it.
                        Lua.DoString("if (CharCreateRandomizeButton and CharCreateRandomizeButton:IsVisible()) then CharacterCreate_Back() end", "ReloginBack");
                        break;

                    case GlueKind.PatchDownload:
                        failure = "the client is running its patch updater; login is not possible";
                        return false;

                    default:
                        // Screens we don't model, or one unreadable poll. Reading again next
                        // tick is enough — a single failed read is not a failure.
                        break;
                }

                if (entered)
                    break;

                Thread.Sleep(2000);
            }

            if (!entered)
            {
                failure = submissions > 0
                    ? "the server did not answer the login attempt (server unreachable or realm offline)"
                    : string.Format("character selection was not reached within {0}s", _config.LoginScreenTimeoutSeconds);
                return false;
            }

            // ---- 5. In-world confirmation -------------------------------------------
            return ConfirmInWorld(client, pid, generation, out failure);
        }

        /// <summary>
        /// Waits for g_ClientConnectionState + ObjectManager.Me. This is the resume gate:
        /// the routine/profile is only restarted once both are true.
        /// </summary>
        private static bool ConfirmInWorld(Process client, int pid, int generation, out string failure)
        {
            failure = "";
            CurrentStep = ReloginStep.WaitingForInWorld;
            var worldWait = Stopwatch.StartNew();
            int reissueAt = 15;

            while (worldWait.Elapsed.TotalSeconds < _config.InWorldTimeoutSeconds)
            {
                if (!IsCurrent(generation)) { failure = "stopped"; return false; }
                if (!ReloginClient.IsAlive(client)) { failure = "the client exited while entering the world"; return false; }

                bool inGame = ReloginClient.Probe(pid).InGame;
                if (inGame)
                {
                    try { ObjectManager.Update(); } catch { /* object list not ready yet */ }

                    if (ObjectManager.Me != null && ObjectManager.Me.BaseAddress != 0U)
                    {
                        Logging.Write("[Relogin] In the world as {0} (level {1} {2} {3}).",
                            ObjectManager.Me.Name, ObjectManager.Me.Level, ObjectManager.Me.Race, ObjectManager.Me.Class);

                        // A relaunched client is a new window: point the hotkeys at it before
                        // the bot resumes, otherwise F7-style hotkeys stay bound to the dead one.
                        try
                        {
                            Styx.Common.HotkeysManager.UpdateWindowHandle(client.MainWindowHandle);
                        }
                        catch
                        {
                            // Window not ready yet; the resume path in the UI re-initializes too.
                        }

                        return true;
                    }
                }

                // The 3.3.5a client drops an EnterWorld that races a character-list refresh;
                // re-issue it a couple of times before declaring the attempt failed.
                if (worldWait.Elapsed.TotalSeconds >= reissueAt && reissueAt < 60)
                {
                    Logging.WriteDebug("[Relogin] Still not in the world after {0:F0}s; re-issuing EnterWorld.", worldWait.Elapsed.TotalSeconds);
                    ReloginClient.SendCharacterSelect(_config.CharacterName, _config.RealmName);
                    reissueAt += 15;
                }

                Thread.Sleep(1000);
            }

            failure = string.Format("the in-world state was not reached within {0}s", _config.InWorldTimeoutSeconds);
            return false;
        }

        #endregion
    }
}
