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
    /// Identifies one specific confirmed recovery so delayed resume callbacks cannot act
    /// on a later replacement launched within the same monitor generation.
    /// </summary>
    public readonly struct ReloginResumeToken : IEquatable<ReloginResumeToken>
    {
        public int Generation { get; }
        public int ProcessId { get; }
        public long RecoveryId { get; }

        internal ReloginResumeToken(int generation, int processId, long recoveryId)
        {
            Generation = generation;
            ProcessId = processId;
            RecoveryId = recoveryId;
        }

        public bool IsValid => Generation > 0 && ProcessId > 0 && RecoveryId > 0;
        public bool Equals(ReloginResumeToken other) => Generation == other.Generation && ProcessId == other.ProcessId && RecoveryId == other.RecoveryId;
        public override bool Equals(object? obj) => obj is ReloginResumeToken other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Generation, ProcessId, RecoveryId);
        public static bool operator ==(ReloginResumeToken left, ReloginResumeToken right) => left.Equals(right);
        public static bool operator !=(ReloginResumeToken left, ReloginResumeToken right) => !left.Equals(right);
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
        private static long _nextRecoveryId;

        private static readonly object _stateSync = new object();

        private sealed class MonitorRun
        {
            public readonly int Generation;
            public readonly Config Config;
            public int ProcessId, Attempt, HungProcessId, ConfirmedProcessId;
            public long? ProcessStartTimeUtcTicks, HungProcessStartTimeUtcTicks;
            public long ActiveRecoveryId, ConfirmedRecoveryId;
            public string ConfirmedCharacterName = "";
            public string ConfirmedRealmName = "";
            public volatile bool SequenceActive, GaveUp;
            public volatile ReloginStep Step;
            public ReloginTrigger Trigger;
            public Func<int, (Mutex? Mutex, string Error)>? ClaimProcess;
            public Func<Mutex, bool>? ReleaseClaim;

            public MonitorRun(int generation, Config config, int processId, long? processStartTimeUtcTicks = null)
            {
                Generation = generation;
                Config = config;
                ProcessId = processId;
                ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
            }
        }

        private static MonitorRun _run = new MonitorRun(0, new Config(), 0);

        /// <summary>
        /// ProcessMutex claim for a client this feature launched. The mutex MainWindow holds
        /// belongs to the client it attached to, which after a crash is the dead one, so the
        /// replacement has to be claimed here or a second CopilotBuddy could attach to it.
        /// Handed over to MainWindow when the bot resumes.
        ///
        /// The claim is owned by one recovery generation and recovery token: a monitor that
        /// outlives the bounded join can only ever drop its own claim, and a delayed resume
        /// callback can only transfer the claim matching its exact PID and recovery token.
        /// </summary>
        private static readonly object _claimSync = new object();
        private static Mutex? _claimedMutex;
        private static int _claimedProcessId;

        /// <summary>Recovery generation that owns <see cref="_claimedMutex"/> (0 = none).</summary>
        private static int _claimedMutexGeneration;
        private static long _claimedMutexRecoveryId;

        private static int _processId;
        private static long? _processStartTimeUtcTicks;
        // Unlike _processId, this changes only when the UI owns the process claim.
        private static int _uiClaimedProcessId;


        #endregion

        #region Public state

        /// <summary>True while a relogin sequence (retry loop included) is running.</summary>
        public static bool IsSequenceActive => Volatile.Read(ref _run).SequenceActive;

        /// <summary>Current step of the sequence — for logs and diagnostics.</summary>
        public static ReloginStep CurrentStep => Volatile.Read(ref _run).Step;

        /// <summary>
        /// Raised (on a ThreadPool thread) once a client is confirmed in world. The argument
        /// identifies the exact monitor generation, confirmed process ID, and recovery token:
        /// subscribers that hop to another thread must carry it and validate via
        /// <see cref="IsCurrentResume"/> before adopting the claim or starting the bot.
        /// </summary>
        public static event Action<ReloginResumeToken>? ResumeBot;

        #endregion

        #region Public API

        /// <summary>
        /// Restarts the monitor with the current <see cref="ReloginSettings"/>, preserving
        /// the latest client process ID held by the manager after retiring the current run.
        /// </summary>
        public static void Start()
        {
            StartInternal(null);
        }

        /// <summary>
        /// (Re)starts the monitor with the current <see cref="ReloginSettings"/>. Safe to call
        /// repeatedly and safe to call while the feature is disabled — in that case it just
        /// stops any running monitor.
        /// </summary>
        /// <param name="processId">Client process the bot is attached to; 0 = preserve/discover by name.</param>
        public static void Start(int processId)
        {
            StartInternal(processId > 0 ? processId : (int?)null);
        }

        private static void StartInternal(int? explicitProcessId)
        {
            // Obtain a consistent AccountName/Password snapshot under ReloginSettings' named mutex.
            ReloginSettings settings = ReloginSettings.Instance.Snapshot();

            lock (_sync)
            {
                // Retire the previous monitor. Any thread still winding down sees the new
                // generation on its next poll and exits without touching anything.
                RetireMonitorThread();

                // Capture this run's generation once, after the bump above: the claim callback
                // and the monitor thread must both be stamped with exactly this value, so the
                // claim a run creates is always attributed to the run that made it.
                int generation = _generation;

                int trackedPid;
                long? trackedStartTicks;
                long? explicitStartTicks = null;
                if (explicitProcessId.HasValue)
                {
                    using Process? proc = ReloginClient.FindProcessById(explicitProcessId.Value);
                    explicitStartTicks = ReloginClient.TryGetProcessStartTimeUtcTicks(proc);
                }

                lock (_stateSync)
                {
                    if (explicitProcessId.HasValue)
                    {
                        Volatile.Write(ref _uiClaimedProcessId, explicitProcessId.Value);
                        _processId = explicitProcessId.Value;
                        _processStartTimeUtcTicks = explicitStartTicks;
                    }
                    trackedPid = _processId;
                    trackedStartTicks = _processStartTimeUtcTicks;
                }

                var run = new MonitorRun(generation, Config.FromSettings(settings), trackedPid, trackedStartTicks);
                lock (_stateSync) Volatile.Write(ref _run, run);
                if (!run.Config.Enabled)
                {
                    // Disabled (or switched off): leave the monitor stopped. Everything else
                    // in the bot behaves exactly as if the feature did not exist.
                    Logging.WriteDebug("[Relogin] Auto-relogin is disabled.");
                    return;
                }

                // Claiming is delegated so ReloginClient stays free of manager state. The
                // returned mutex is stored here and handed to MainWindow on resume.
                // The delegate is replaced on every Start(), so it carries the generation of
                // the run that installed it: a claim made by a retired monitor is dropped by
                // that monitor alone (see ReleaseClaimIfOlderThan).
                run.ClaimProcess = pid =>
                {
                    long recoveryId = Volatile.Read(ref run.ActiveRecoveryId);
                    lock (_claimSync)
                    {
                        if (!IsCurrent(generation))
                            return (null, "stopped");

                        // Reuse our own claim on a retry; creating the named mutex again
                        // would incorrectly report that another instance owns it.
                        if (_claimedMutex != null && _claimedProcessId == pid)
                        {
                            _claimedMutexGeneration = generation;
                            _claimedMutexRecoveryId = recoveryId;
                            return (_claimedMutex, "");
                        }

                        if (_claimedMutex != null && ReloginClient.HasAttachment(_claimedProcessId))
                            return (null, "the previous claimed client is still attached");

                        try
                        {
                            Mutex mutex = ProcessMutex.Create(pid, out bool createdNew);
                            if (!createdNew)
                            {
                                mutex.Close();
                                string message = string.Format(
                                    "the relaunched client (pid {0}) is already claimed by another CopilotBuddy instance", pid);
                                Logging.Write(Colors.Red, "[Relogin] Cannot attach: {0}.", message);
                                return (null, message);
                            }

                            Mutex? previous = _claimedMutex;
                            _claimedMutex = mutex;
                            _claimedProcessId = pid;
                            _claimedMutexGeneration = generation;
                            _claimedMutexRecoveryId = recoveryId;
                            try { previous?.Dispose(); } catch { }
                            return (mutex, "");
                        }
                        catch (Exception ex)
                        {
                            Logging.WriteDebug("[Relogin] Could not claim pid {0}: {1}", pid, ex.Message);
                            return (null, "the relaunched client could not be claimed: " + ex.Message);
                        }
                    }
                };

                // Authorize disposal only for this run's exact claim, after its attachment
                // is gone. A failed shutdown must not leave a live hook without a claim.
                run.ReleaseClaim = claim =>
                {
                    lock (_claimSync)
                    {
                        if (!ReferenceEquals(_claimedMutex, claim) || _claimedMutexGeneration != generation
                            || ReloginClient.HasAttachment(_claimedProcessId))
                            return false;

                        _claimedMutex = null;
                        _claimedProcessId = 0;
                        _claimedMutexGeneration = 0;
                        _claimedMutexRecoveryId = 0;
                        return true;
                    }
                };

                // Report an incomplete configuration once, up front, instead of making the
                // user wait for a crash to discover it. Relogin itself still fails safely
                // (and reports the exact reason) if it is triggered in this state.
                string? configError = run.Config.Validate();
                if (configError != null)
                    Logging.Write(Colors.Red, "[Relogin] Auto-relogin is enabled but {0}. Fill it in before the client crashes.", configError);
                else if (string.IsNullOrEmpty(run.Config.WowExecutablePath))
                    Logging.Write("[Relogin] Note: no WoW.exe path set — a crashed client cannot be relaunched, only an existing one logged back in.");

                _thread = new Thread(() => MonitorLoop(run))
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
                lock (_stateSync)
                    Volatile.Write(ref _run, new MonitorRun(_generation, new Config(), _processId, _processStartTimeUtcTicks));
            }
        }

        /// <summary>
        /// Called when the user starts the bot manually. Re-arms the retry budget so a fresh
        /// manual start re-enables the feature after it gave up. Never resets an active sequence.
        /// </summary>
        public static void OnBotStarted()
        {
            lock (_stateSync)
            {
                MonitorRun run = _run;
                if (run.SequenceActive)
                    return;

                if (run.Attempt > 0 || run.GaveUp)
                    Logging.WriteDebug("[Relogin] Retry budget re-armed by a manual bot start.");
                run.Attempt = 0;
                run.GaveUp = false;
            }
        }

        /// <summary>
        /// Updates the process the monitor watches (after a successful relogin, or when the
        /// bot attaches to a different client).
        /// </summary>
        public static void SetAttachedProcess(int processId)
        {
            if (processId <= 0)
                return;

            long? startTicks;
            using (Process? proc = ReloginClient.FindProcessById(processId))
                startTicks = ReloginClient.TryGetProcessStartTimeUtcTicks(proc);

            lock (_stateSync)
            {
                bool changed = _processId != processId;
                _processId = processId;
                _run.ProcessId = processId;
                if (changed || !_processStartTimeUtcTicks.HasValue)
                {
                    _processStartTimeUtcTicks = startTicks;
                    _run.ProcessStartTimeUtcTicks = startTicks;
                }
            }
        }

        /// <summary>The client pid the monitor is currently watching.</summary>
        public static int AttachedProcessId => Volatile.Read(ref _processId);

        /// <summary>
        /// Takes ownership of the process claim created for the exact relaunched client
        /// identified by <paramref name="token"/>, if any. Returns null when the token is
        /// stale or no matching replacement claim exists.
        /// </summary>
        internal static Mutex? TakeClaimedMutex(ReloginResumeToken token)
        {
            lock (_stateSync)
            {
                if (!IsCurrentResumeLocked(token))
                    return null;

                lock (_claimSync)
                {
                    if (_claimedMutex == null
                        || _claimedProcessId != token.ProcessId
                        || _claimedMutexGeneration != token.Generation
                        || _claimedMutexRecoveryId != token.RecoveryId)
                        return null;

                    Mutex claimed = _claimedMutex;
                    Volatile.Write(ref _uiClaimedProcessId, _claimedProcessId);
                    _claimedMutex = null;
                    _claimedProcessId = 0;
                    _claimedMutexGeneration = 0;
                    _claimedMutexRecoveryId = 0;
                    return claimed;
                }
            }
        }

        /// <summary>Releases a leftover recovery claim after application teardown, if detached.</summary>
        internal static void ReleaseDetachedClaim()
        {
            ReleaseClaimIfOlderThan(int.MaxValue);
        }

        /// <summary>
        /// Serializes startup ObjectManager.Initialize and publication with the closing teardown barrier.
        /// </summary>
        internal static bool TryPublishStartupAttachment(Memory memory, Func<bool> isCancelled, Action onPublished)
        {
            return ReloginClient.TryPublishStartupAttachment(memory, isCancelled, onPublished);
        }

        /// <summary>
        /// Serializes Window_Closing's ObjectManager.Shutdown with any in-flight attach.
        /// Returns true if teardown completed; returns false if deferred (transferring
        /// <paramref name="uiProcessMutex"/> to the foreground deferred cleanup owner).
        /// </summary>
        internal static bool ShutdownAttachmentForClosing(ref Mutex? uiProcessMutex, Action? pumpMessages = null)
        {
            return ReloginClient.ShutdownForClosing(ref uiProcessMutex, pumpMessages);
        }

        /// <summary>
        /// Drops the claim only if it was made by a generation older than
        /// <paramref name="generation"/>. A monitor that outlived the bounded join must not
        /// dispose the claim a newer run is already relying on. An attached replacement keeps
        /// its claim even after failure, until it is detached or the claim is handed to the UI.
        /// </summary>
        private static void ReleaseClaimIfOlderThan(int generation)
        {
            Mutex? stale = null;
            // An in-flight attach owns its claim too. Defer cleanup rather than block
            // Stop()/window close on a native attach that may be stalled.
            if (!Monitor.TryEnter(ReloginClient.AttachmentSync))
                return;
            try
            {
                lock (_claimSync)
                {
                    if (_claimedMutex != null && _claimedMutexGeneration < generation
                        && !ReloginClient.HasAttachment(_claimedProcessId))
                    {
                        stale = _claimedMutex;
                        _claimedMutex = null;
                        _claimedProcessId = 0;
                        _claimedMutexGeneration = 0;
                        _claimedMutexRecoveryId = 0;
                    }
                }
            }
            finally
            {
                Monitor.Exit(ReloginClient.AttachmentSync);
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
            lock (_stateSync) Interlocked.Increment(ref _generation);

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

        /// <summary>
        /// True when <paramref name="token"/> still refers to the latest confirmed recovery
        /// (same monitor generation, same recovery token, and same confirmed PID).
        /// </summary>
        public static bool IsCurrentResume(ReloginResumeToken token)
        {
            lock (_stateSync)
                return IsCurrentResumeLocked(token);
        }

        private static bool IsCurrentResumeLocked(ReloginResumeToken token)
        {
            MonitorRun run = _run;
            return token.IsValid
                && IsCurrent(token.Generation)
                && run.Generation == token.Generation
                && run.ConfirmedRecoveryId == token.RecoveryId
                && run.ConfirmedProcessId == token.ProcessId
                && _processId == token.ProcessId;
        }

        /// <summary>The live monitor generation, for a subscriber that has not been given one.</summary>
        public static int CurrentGeneration => Volatile.Read(ref _generation);

        private static bool ApplyIfCurrent(MonitorRun run, Action action)
        {
            lock (_stateSync)
            {
                if (!IsCurrent(run.Generation) || !ReferenceEquals(_run, run)) return false;
                action();
                return true;
            }
        }

        private static bool CanAttach(MonitorRun run, int pid)
        {
            lock (_stateSync)
                return IsCurrent(run.Generation) && ReferenceEquals(_run, run) && _processId == pid;
        }

        private static bool ApplyForAttachment(MonitorRun run, int pid, Action action)
        {
            bool applied = false;
            ApplyIfCurrent(run, () =>
            {
                if (_processId != pid) return;
                action();
                applied = true;
            });
            return applied;
        }

        private static bool PublishProcess(MonitorRun run, int expectedPid, int pid, long? startTimeUtcTicks, bool replaceStartTime = false)
        {
            bool published = false;
            ApplyIfCurrent(run, () =>
            {
                if (_processId != expectedPid) return;
                _processId = run.ProcessId = pid;
                if (replaceStartTime || expectedPid != pid || !_processStartTimeUtcTicks.HasValue)
                    _processStartTimeUtcTicks = run.ProcessStartTimeUtcTicks = startTimeUtcTicks;
                else
                    run.ProcessStartTimeUtcTicks = _processStartTimeUtcTicks;
                published = true;
            });
            return published;
        }

        /// <summary>
        /// Returns the character and (when configured) realm confirmed by <see cref="ConfirmInWorld"/>
        /// for <paramref name="token"/>, if that recovery is still current.
        /// </summary>
        internal static bool TryGetConfirmedIdentity(ReloginResumeToken token, out string characterName, out string realmName)
        {
            lock (_stateSync)
            {
                MonitorRun run = _run;
                if (!IsCurrentResumeLocked(token) || string.IsNullOrEmpty(run.ConfirmedCharacterName))
                {
                    characterName = "";
                    realmName = "";
                    return false;
                }
                characterName = run.ConfirmedCharacterName;
                realmName = run.ConfirmedRealmName;
                return true;
            }
        }

        internal static bool UpdateAttachedHotkeys(ReloginResumeToken token)
        {
            return UpdateAttachedHotkeys(token, out _);
        }

        internal static bool UpdateAttachedHotkeys(ReloginResumeToken token, out bool attachmentBusy)
        {
            attachmentBusy = false;
            if (!IsCurrentResume(token))
                return false;

            // The UI owns adoption. Never block indefinitely on AttachmentSync while an attach
            // is in progress, so stale recovery updates can retire cleanly.
            if (!Monitor.TryEnter(ReloginClient.AttachmentSync))
            {
                attachmentBusy = IsCurrentResume(token);
                return false;
            }
            try
            {
                lock (_stateSync)
                {
                    if (!IsCurrentResumeLocked(token) || !ReloginClient.IsAttached(token.ProcessId)) return false;
                    var process = ObjectManager.WoWProcess;
                    if (process == null || process.Id != token.ProcessId) return false;
                    IntPtr handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) return false;
                    Styx.Common.HotkeysManager.UpdateWindowHandle(handle);
                    return true;
                }
            }
            finally
            {
                Monitor.Exit(ReloginClient.AttachmentSync);
            }
        }

        private static void MonitorLoop(MonitorRun run)
        {
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
                if (run.ProcessId <= 0)
                {
                    using Process? initialClient = ReloginClient.FindClientProcessByName();
                    if (initialClient != null && !PublishProcess(run, run.ProcessId, initialClient.Id, ReloginClient.TryGetProcessStartTimeUtcTicks(initialClient)))
                        return;
                }

                Logging.WriteDebug("[Relogin] Monitor started (pid {0}, hang grace {1}s, retry delay {2}s, max retries {3}).",
                    run.ProcessId, run.Config.HangGraceSeconds, run.Config.RetryDelaySeconds, run.Config.MaxRetryCount);

                while (IsCurrent(run.Generation))
                {
                    if (run.SequenceActive)
                    {
                        SleepPollSlices(run);
                        continue;
                    }

                    Process? client = null;
                    bool skipRest = false;
                    bool skipSleep = false;
                    try
                    {
                        // Re-resolve every poll: after a relaunch the old Process object refers to
                        // the dead client, and re-using it would immediately look like another exit.
                        // Name discovery is only a bootstrap: once this session tracks a pid, a
                        // vanished pid (or a different process that reused that pid) means the
                        // client we were watching died.
                        if (run.ProcessId > 0)
                        {
                            client = ReloginClient.FindProcessById(run.ProcessId);
                            if (client != null)
                            {
                                var identity = ReloginClient.CheckProcessIdentity(client, run.ProcessId, run.ProcessStartTimeUtcTicks);
                                if (identity == ProcessIdentityCheck.Exited || identity == ProcessIdentityCheck.Mismatch)
                                {
                                    if (identity == ProcessIdentityCheck.Mismatch)
                                        Logging.WriteDebug("[Relogin] Tracked pid {0} now belongs to a different process instance; treating original client as exited.", run.ProcessId);
                                    client.Dispose();
                                    client = null;
                                }
                                else if (identity == ProcessIdentityCheck.Unreadable)
                                {
                                    // Reset both hang-confirmation and logout-confirmation timers across
                                    // an unreadable identity poll; preserve worldDeparted so a later
                                    // stable logout can still trigger recovery.
                                    Logging.WriteDebug("[Relogin] Could not read process identity for tracked pid {0}; retrying on next poll.", run.ProcessId);
                                    unresponsiveFor.Reset();
                                    pendingLogout = false;
                                    notInWorldFor.Reset();
                                    skipRest = true;
                                }
                            }
                        }
                        else
                        {
                            client = ReloginClient.FindClientProcessByName();
                        }

                        if (!skipRest && client != null && !PublishProcess(run, run.ProcessId, client.Id, ReloginClient.TryGetProcessStartTimeUtcTicks(client)))
                            return;

                        if (!skipRest && !ReloginClient.IsAlive(client))
                        {
                            // A dead process is the one unambiguous crash/exit signal.
                            FireTrigger(ReloginTrigger.ProcessExited, "client process is gone", run, null);
                            unresponsiveFor.Reset();
                            notInWorldFor.Reset();
                            worldDeparted = false;
                            pendingLogout = false;
                            lastInWorld = null;
                            skipRest = true;
                        }

                        ReloginClient.ClientProbe probe = default;
                        if (!skipRest)
                        {
                            int pid = client!.Id;
                            probe = ReloginClient.Probe(pid);

                            // ---- Hang detection: window not responding, sustained ------------------
                            // Responding is the same signal HBRelog uses; unlike a raw memory read it
                            // is not perturbed by a lagging server, and a single hung frame is
                            // filtered out by the grace window.
                            if (run.Config.HangGraceSeconds > 0 && IsWindowNotResponding(client))
                            {
                                if (!unresponsiveFor.IsRunning)
                                {
                                    unresponsiveFor.Start();
                                    Logging.WriteDebug("[Relogin] Client window is not responding; watching for {0}s before treating it as a hang.",
                                        Math.Max(run.Config.HangGraceSeconds, RespondingGraceMs / 1000));
                                }
                                else if (unresponsiveFor.ElapsedMilliseconds >= Math.Max(run.Config.HangGraceSeconds, RespondingGraceMs / 1000) * 1000L)
                                {
                                    FireTrigger(ReloginTrigger.ClientHung,
                                        string.Format("client window unresponsive for {0:F0}s", unresponsiveFor.Elapsed.TotalSeconds),
                                        run, client);
                                    unresponsiveFor.Reset();
                                    notInWorldFor.Reset();
                                    worldDeparted = false;
                                    pendingLogout = false;
                                    lastInWorld = null;
                                    skipRest = true;
                                }
                            }
                            else if (unresponsiveFor.IsRunning)
                            {
                                Logging.WriteDebug("[Relogin] Client window responding again after {0:F0}s; no relogin needed.",
                                    unresponsiveFor.Elapsed.TotalSeconds);
                                unresponsiveFor.Reset();
                            }
                        }

                        // ---- Logout detection: dropped from the world to the login screen -----
                        // The transition has to be remembered across polls: after the first poll
                        // without the in-world flag, lastInWorld is false, so the confirmation
                        // window must be carried by an explicit pending state instead.
                        if (!skipRest)
                        {
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
                                                bool ranRecovery = FireTrigger(ReloginTrigger.ClientLoggedOut,
                                                    string.Format("client returned to {0} and stayed there for {1}s",
                                                        probe.Glue, LogoutConfirmSeconds),
                                                    run, client);
                                                skipRest = true;
                                                // Probe immediately after logout recovery finishes so
                                                // lastInWorld's in-world baseline is restored right away;
                                                // keep the poll delay if FireTrigger did not start recovery.
                                                skipSleep = ranRecovery;
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

                                if (!skipRest)
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
                        }
                    }
                    finally
                    {
                        client?.Dispose();
                    }

                    if (!skipSleep)
                        SleepPollSlices(run);
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
                run.Step = ReloginStep.Idle;
                run.SequenceActive = false;
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
        private const int MaxLoginSubmissionsPerAttempt = 3;

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

        private static void SleepPollSlices(MonitorRun run)
        {
            for (int slept = 0; slept < PollIntervalMs && IsCurrent(run.Generation); slept += 100)
                Thread.Sleep(100);
        }

        private static bool FireTrigger(ReloginTrigger trigger, string reason, MonitorRun run, Process? observedClient)
        {
            long? observedStartTicks = observedClient != null
                ? ReloginClient.TryGetProcessStartTimeUtcTicks(observedClient)
                : null;

            lock (_stateSync)
            {
                if (!run.Config.Enabled || run.GaveUp || !IsCurrent(run.Generation) || !ReferenceEquals(_run, run) || run.SequenceActive)
                    return false;

                run.SequenceActive = true;
                run.ActiveRecoveryId = Interlocked.Increment(ref _nextRecoveryId);
                run.ConfirmedRecoveryId = 0;
                run.ConfirmedProcessId = 0;
                run.ConfirmedCharacterName = "";
                run.ConfirmedRealmName = "";
                run.Trigger = trigger;
                run.HungProcessId = trigger == ReloginTrigger.ClientHung ? run.ProcessId : 0;
                run.HungProcessStartTimeUtcTicks = trigger == ReloginTrigger.ClientHung
                    ? (run.ProcessStartTimeUtcTicks ?? observedStartTicks)
                    : null;
                run.Attempt = 0;
            }

            Logging.Write("[Relogin] Relogin triggered: {0} — {1}.", trigger, reason);
            TryRunSequence(run);
            return true;
        }

        #endregion

        #region Sequence

        /// <summary>
        /// Runs the retry loop on the monitor thread: wait, relaunch/attach, log in, select
        /// the configured character, wait for the in-world state. Every attempt is logged
        /// with its trigger reason, number, the step it reached and the failure reason.
        /// </summary>
        private static void TryRunSequence(MonitorRun run)
        {
            bool resumed = false;
            ReloginResumeToken resumeToken = default;
            string lastFailure = "";

            try
            {
                while (IsCurrent(run.Generation))
                {
                    int maxRetries = run.Config.MaxRetryCount;
                    int currentAttempt;
                    lock (_stateSync)
                    {
                        if (!IsCurrent(run.Generation) || !ReferenceEquals(_run, run))
                            return;

                        if (run.Attempt >= maxRetries)
                        {
                            run.GaveUp = true;
                            Logging.Write(Colors.Red, "[Relogin] Giving up after {0} attempt(s), maximum retries exhausted. Last failure — {1}.",
                                run.Attempt, lastFailure);
                            return;
                        }

                        run.Attempt++;
                        currentAttempt = run.Attempt;
                    }

                    Logging.Write("[Relogin] Attempt {0}/{1} (trigger: {2}).", currentAttempt, maxRetries, run.Trigger);

                    bool ok = RunAttempt(run, out string failure);
                    if (!IsCurrent(run.Generation)) return;
                    ReloginStep reachedStep = run.Step;

                    if (ok)
                    {
                        lock (_stateSync)
                        {
                            if (IsCurrent(run.Generation) && ReferenceEquals(_run, run)
                                && run.ConfirmedRecoveryId == run.ActiveRecoveryId && run.ConfirmedProcessId > 0)
                            {
                                resumeToken = new ReloginResumeToken(run.Generation, run.ConfirmedProcessId, run.ConfirmedRecoveryId);
                                resumed = true;
                            }
                        }

                        if (resumed)
                        {
                            Logging.Write(Colors.Green, "[Relogin] Attempt {0}/{1} succeeded: character is back in the world.",
                                currentAttempt, maxRetries);
                        }
                        return;
                    }

                    lastFailure = string.Format("step {0}: {1}", reachedStep, failure);
                    Logging.Write(Colors.Red, "[Relogin] Attempt {0}/{1} failed at {2}.",
                        currentAttempt, maxRetries, lastFailure);

                    if (IsFatal(failure))
                    {
                        Logging.Write(Colors.Red, "[Relogin] Not retrying this attempt: {0}.", failure);
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_run, run))
                                run.GaveUp = true;
                        }
                        return;
                    }

                    if (currentAttempt >= maxRetries)
                        continue; // the loop head reports the give-up

                    run.Step = ReloginStep.WaitingForRetryDelay;
                    Logging.Write("[Relogin] Waiting {0}s before attempt {1}/{2}.", run.Config.RetryDelaySeconds, currentAttempt + 1, maxRetries);
                    if (!SleepWithStop(run.Config.RetryDelaySeconds * 1000, run))
                        break;
                }
            }
            finally
            {
                lock (_stateSync)
                {
                    run.SequenceActive = false;
                    run.Step = ReloginStep.Idle;
                }

                // Release only detached claims owned by this run. A failed recovery can
                // still leave a hooked replacement available for a later manual start;
                // keep that client's claim for the full lifetime of its attachment.
                if (!resumed)
                    ReleaseClaimIfOlderThan(run.Generation + 1);

                if (resumed && IsCurrentResume(resumeToken))
                {
                    // Only now, with the in-world state confirmed, does the routine/profile
                    // resume — through the caller's normal Start path, bound to this exact recovery.
                    ReloginResumeToken tokenToDispatch = resumeToken;
                    ThreadPool.QueueUserWorkItem(_ => ResumeBot?.Invoke(tokenToDispatch));
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
                    || reason.Contains("updater", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("character identity mismatch", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SleepWithStop(int milliseconds, MonitorRun run)
        {
            for (int slept = 0; slept < milliseconds; slept += 100)
            {
                if (!IsCurrent(run.Generation))
                    return false;
                Thread.Sleep(Math.Min(100, milliseconds - slept));
            }
            return IsCurrent(run.Generation);
        }

        /// <summary>
        /// One full attempt. Returns true when the client reached the in-world state;
        /// otherwise false with <paramref name="failure"/> naming the failing step and why.
        /// </summary>
        private static bool RunAttempt(MonitorRun run, out string failure)
        {
            failure = "";

            string? configError = run.Config.Validate();
            if (configError != null)
            {
                failure = configError;
                return false;
            }

            // ---- 1. Client process ---------------------------------------------------
            run.Step = ReloginStep.StartingClient;
            Process? client = run.ProcessId > 0 ? ReloginClient.FindProcessById(run.ProcessId) : null;
            if (client != null)
            {
                var identity = ReloginClient.CheckProcessIdentity(client, run.ProcessId, run.ProcessStartTimeUtcTicks);
                if (identity == ProcessIdentityCheck.Exited || identity == ProcessIdentityCheck.Mismatch)
                {
                    if (identity == ProcessIdentityCheck.Mismatch)
                        Logging.Write("[Relogin] Tracked client pid {0} was reused by another process; treating original client as exited.", run.ProcessId);
                    client.Dispose();
                    client = null;
                    run.HungProcessId = 0;
                    run.HungProcessStartTimeUtcTicks = null;
                }
                else if (identity == ProcessIdentityCheck.Unreadable)
                {
                    client.Dispose();
                    failure = string.Format("could not verify the process identity of tracked client pid {0}", run.ProcessId);
                    return false;
                }
            }
            // run.ProcessId survives retries (and settings restarts); it does not prove that
            // the UI owns this client's claim. A replacement stays claim-required until hand-over.
            bool launchedReplacement = client != null && client.Id != Volatile.Read(ref _uiClaimedProcessId);

            // A hung client is still running, so it has to be closed before it can be
            // relaunched — otherwise the relaunch would just attach to the frozen process.
            if (client != null && run.Trigger == ReloginTrigger.ClientHung && client.Id == run.HungProcessId)
            {
                int hungPid = client.Id;
                long? hungStartTicks = run.HungProcessStartTimeUtcTicks;
                bool authorized = true;
                ProcessIdentityCheck identityStatus = ProcessIdentityCheck.Match;
                bool closed = ReloginClient.TerminateClient(client, out string closeError, action =>
                {
                    bool executed = ApplyForAttachment(run, hungPid, () =>
                    {
                        identityStatus = ReloginClient.CheckProcessIdentity(client, hungPid, hungStartTicks, requireStartTime: true);
                        if (identityStatus != ProcessIdentityCheck.Match)
                            return;
                        action();
                    });
                    if (!executed)
                    {
                        authorized = false;
                        return false;
                    }
                    return identityStatus == ProcessIdentityCheck.Match;
                });

                if (!authorized)
                {
                    failure = "stopped";
                    return false;
                }

                if (identityStatus == ProcessIdentityCheck.Unreadable)
                {
                    client.Dispose();
                    failure = string.Format("could not verify the process identity of unresponsive client pid {0} before termination", hungPid);
                    return false;
                }

                if (identityStatus == ProcessIdentityCheck.Exited || identityStatus == ProcessIdentityCheck.Mismatch)
                {
                    Logging.Write("[Relogin] Tracked hung client pid {0} {1}; skipping termination and relaunching.",
                        hungPid, identityStatus == ProcessIdentityCheck.Mismatch ? "was reused by another process" : "already exited");
                    run.HungProcessId = 0;
                    run.HungProcessStartTimeUtcTicks = null;
                    client.Dispose();
                    client = null;
                }
                else
                {
                    if (!closed)
                    {
                        failure = "the unresponsive client could not be closed: " + closeError;
                        return false;
                    }

                    Logging.Write("[Relogin] Unresponsive client closed.");
                    run.HungProcessId = 0;
                    run.HungProcessStartTimeUtcTicks = null;
                    client.Dispose();
                    client = null;
                }

                // TerminateClient blocks for as long as the close takes. If the monitor was
                // retired meanwhile (settings saved, bot stopped, app closing), no replacement
                // may be launched: a retired run must not publish a new pid or attach.
                if (!IsCurrent(run.Generation))
                {
                    failure = "stopped";
                    return false;
                }
            }

            if (client == null)
            {
                if (string.IsNullOrEmpty(run.Config.WowExecutablePath))
                {
                    failure = "the client is not running and the WoW executable path is not set";
                    return false;
                }
                if (!File.Exists(run.Config.WowExecutablePath))
                {
                    failure = string.Format("WoW executable path is invalid: {0}", run.Config.WowExecutablePath);
                    return false;
                }

                if (!IsCurrent(run.Generation))
                {
                    failure = "stopped";
                    return false;
                }

                int launchOwnerPid = run.ProcessId;
                Logging.Write("[Relogin] Starting client: {0}", run.Config.WowExecutablePath);
                client = ReloginClient.StartClient(run.Config.WowExecutablePath, out string startError);
                if (client == null)
                {
                    failure = "could not start the client: " + startError;
                    return false;
                }

                long? launchedStartTicks = ReloginClient.TryGetProcessStartTimeUtcTicks(client);
                if (!PublishProcess(run, launchOwnerPid, client.Id, launchedStartTicks, replaceStartTime: true))
                {
                    // The user owns this newly opened, unattached window after cancellation.
                    // Do not kill it or silently adopt it into the newer recovery run.
                    Logging.Write("[Relogin] Launch completed after cancellation; leaving client pid {0} open and unattached for manual use.", client.Id);
                    client.Dispose();
                    failure = "stopped";
                    return false;
                }
                launchedReplacement = true;
                Logging.Write("[Relogin] Client started, pid {0}.", run.ProcessId);
            }
            else
            {
                Logging.Write("[Relogin] Client still running (pid {0}); logging it back in.", client.Id);
            }

            int pid = client.Id;
            long? trackedStartTicks = run.ProcessStartTimeUtcTicks;
            if (!trackedStartTicks.HasValue)
            {
                long? fallbackStartTicks = ReloginClient.TryGetProcessStartTimeUtcTicks(client);
                if (fallbackStartTicks.HasValue)
                {
                    if (!PublishProcess(run, pid, pid, fallbackStartTicks))
                    {
                        failure = "stopped";
                        return false;
                    }
                    trackedStartTicks = fallbackStartTicks;
                }
            }

            // ---- 2. Attach + hook, then wait for a readable glue screen ---------------
            run.Step = ReloginStep.WaitingForLoginScreen;
            var wait = Stopwatch.StartNew();
            ReloginClient.ClientProbe probe = new ReloginClient.ClientProbe(false, "", GlueKind.Unknown, false);

            while (wait.Elapsed.TotalSeconds < run.Config.LoginScreenTimeoutSeconds)
            {
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }

                if (!ReloginClient.IsAlive(client))
                {
                    failure = "the client exited during startup (crash loop?)";
                    return false;
                }

                if (!ReloginClient.IsAttached(pid))
                {
                    Memory? attached = ReloginClient.TryAttachAndHook(pid, trackedStartTicks, out string hookError, launchedReplacement,
                        () => CanAttach(run, pid), run.ClaimProcess, run.ReleaseClaim);
                    if (attached == null)
                    {
                        Logging.WriteDebug("[Relogin] Hook not ready yet ({0}); retrying.", hookError);
                        Thread.Sleep(2000);
                        continue;
                    }
                    Logging.WriteDebug("[Relogin] Attached to pid {0} and hooked EndScene.", pid);
                }

                probe = ReloginClient.Probe(pid);
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                if (probe.Readable && (probe.InGame || probe.Glue != GlueKind.Unknown))
                    break;

                // Still booting (glue name not written yet) or on a screen we don't model
                // ('movie', the intro clip, ...). Keep waiting inside the timeout rather than
                // failing on the first unreadable poll.
                Thread.Sleep(1000);
            }

            if (!probe.Readable)
            {
                failure = string.Format("the login screen did not become readable within {0}s", run.Config.LoginScreenTimeoutSeconds);
                return false;
            }

            if (probe.InGame)
            {
                Logging.WriteDebug("[Relogin] Client is already in the world.");
                return ConfirmInWorld(client, pid, run, out failure);
            }

            if (probe.Glue == GlueKind.Unknown)
            {
                failure = string.Format("the client stayed on an unrecognised screen \"{0}\" for {1}s",
                    probe.GlueRaw, run.Config.LoginScreenTimeoutSeconds);
                return false;
            }

            // ---- 3+4. Credentials, realm and character, until EnterWorld is issued ----
            // One loop for both screens: the glue state decides which chunk is sent, so a
            // client that lands on the realm list, bounces back to the login form, or already
            // sits on character selection all take the correct branch on every poll.
            run.Step = ReloginStep.WaitingForCharacterSelect;
            var selectWait = Stopwatch.StartNew();
            int submissions = 0;
            int realmSwitches = 0;
            bool entered = false;
            bool sawLoadingSinceSubmit = false;
            bool unknownSinceSubmit = false;
            bool pendingUnknownSubmit = false;
            bool characterMissing = false;
            bool reachedCharSelect = false;
            bool sawCharacterMissing = false;
            double submittedAt = 0;

            void SendLogin(bool allowCredentialSubmit)
            {
                // Enforce both pendingUnknownSubmit and MaxLoginSubmissionsPerAttempt at the
                // point Lua would call DefaultServerLogin, while keeping WoWAccountSelectDialog
                // and RealmList handlers active whenever credentials are blocked.
                bool canSubmit = allowCredentialSubmit
                    && !pendingUnknownSubmit
                    && submissions < MaxLoginSubmissionsPerAttempt;

                double startedAt = selectWait.Elapsed.TotalSeconds;
                LoginSubmitResult submitResult = ReloginClient.SendLogin(
                    run.Config.AccountName, run.Config.Password, run.Config.RealmName, canSubmit);
                if (!canSubmit)
                    return;

                Logging.WriteDebug("[Relogin] Login submission result: {0}.", submitResult);

                if (submitResult == LoginSubmitResult.Submitted)
                {
                    submissions++;
                    submittedAt = startedAt;
                    sawLoadingSinceSubmit = false;
                    unknownSinceSubmit = false;
                    pendingUnknownSubmit = false;
                    reachedCharSelect = false;
                    sawCharacterMissing = false;
                }
                else if (submitResult == LoginSubmitResult.Unknown)
                {
                    submissions++;
                    submittedAt = startedAt;
                    sawLoadingSinceSubmit = false;
                    unknownSinceSubmit = true;
                    pendingUnknownSubmit = true;
                }
            }

            while (selectWait.Elapsed.TotalSeconds < run.Config.LoginScreenTimeoutSeconds)
            {
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                if (!ReloginClient.IsAlive(client)) { failure = "the client exited during login"; return false; }

                probe = ReloginClient.Probe(pid);
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                if (!probe.Readable)
                {
                    if (submissions > 0) unknownSinceSubmit = true;
                    characterMissing = false;
                    Thread.Sleep(2000);
                    continue;
                }
                if (probe.Glue != GlueKind.CharSelect) characterMissing = false;

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
                        run.Step = ReloginStep.WaitingForCharacterSelect;

                        int code = ReloginClient.SendCharacterSelect(run.Config.CharacterName, run.Config.RealmName);
                        characterMissing = false;
                        switch (code)
                        {
                            case 1:
                                pendingUnknownSubmit = false;
                                reachedCharSelect = true;
                                run.Step = ReloginStep.WaitingForInWorld;
                                realmSwitches = 0;
                                Logging.Write("[Relogin] Character '{0}' selected; EnterWorld issued.", run.Config.CharacterName);
                                entered = true;
                                break;

                            case 4:
                                // A nonempty list can still refresh; preserve that character
                                // selection was reached even if a later probe is unreadable.
                                pendingUnknownSubmit = false;
                                reachedCharSelect = true;
                                characterMissing = true;
                                sawCharacterMissing = true;
                                break;

                            case 2:
                                // Not really on the character screen (realm list / account select / loading).
                                // Keep pendingUnknownSubmit set; SendLogin handles account/realm dialogs
                                // while blocking credential resubmission if uncertain or at the limit.
                                realmSwitches = 0;
                                SendLogin(allowCredentialSubmit: true);
                                break;

                            case 3:
                                // The client is on a different realm and was asked to go back
                                // to the realm list. A handful of these is normal while the
                                // switch completes; a client that keeps asking means the realm
                                // is not reachable for this account, so report that instead of
                                // spinning until the screen timeout.
                                pendingUnknownSubmit = false;
                                reachedCharSelect = true;
                                if (++realmSwitches > MaxRealmSwitches)
                                {
                                    failure = string.Format(
                                        "the client could not switch to realm '{0}' (character '{1}' is not reachable there)",
                                        string.IsNullOrEmpty(run.Config.RealmName) ? "?" : run.Config.RealmName,
                                        run.Config.CharacterName);
                                    return false;
                                }
                                Logging.WriteVerbose("[Relogin] Switching to realm '{0}' ({1}/{2}).",
                                    run.Config.RealmName, realmSwitches, MaxRealmSwitches);
                                SendLogin(allowCredentialSubmit: false);
                                break;

                            case 0:
                                // CharacterSelectUI is visible but the character list is still loading.
                                pendingUnknownSubmit = false;
                                reachedCharSelect = true;
                                realmSwitches = 0;
                                break;

                            default:
                                // -1 = Lua failed / missing result: keep pendingUnknownSubmit unchanged.
                                realmSwitches = 0;
                                break;
                        }
                        break;

                    case GlueKind.Login:
                        run.Step = ReloginStep.LoggingIn;

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
                            double sinceSubmit = selectWait.Elapsed.TotalSeconds - submittedAt;
                            if (!unknownSinceSubmit && !sawLoadingSinceSubmit && sinceSubmit < run.Config.LoginRejectSeconds)
                            {
                                failure = string.Format(
                                    "the server refused the login for '{0}' (wrong account name or password, or the account is not allowed to log in)",
                                    run.Config.AccountName);
                                return false;
                            }

                            if (sawLoadingSinceSubmit)
                            {
                                Logging.Write("[Relogin] Client reached the connecting state and came back to the login screen; treating as a connection failure, not bad credentials.");
                            }
                        }

                        if (pendingUnknownSubmit)
                        {
                            // A previous SendLogin returned an empty/unknown Lua result: still
                            // allow account/realm dialogs if open, but do not submit credentials again.
                            Logging.WriteDebug("[Relogin] Login submission outcome was unknown; polling dialogs without re-submitting credentials.");
                            SendLogin(allowCredentialSubmit: false);
                            break;
                        }

                        if (submissions >= MaxLoginSubmissionsPerAttempt)
                        {
                            failure = "the server did not answer the login attempt (server unreachable or realm offline)";
                            return false;
                        }

                        Logging.WriteVerbose("[Relogin] Submitting credentials for {0} (attempt {1}).",
                            run.Config.AccountName, submissions + 1);
                        SendLogin(allowCredentialSubmit: true);
                        break;

                    case GlueKind.RealmWizard:
                    case GlueKind.Other:
                        run.Step = ReloginStep.LoggingIn;
                        // Keep realm/account selection working even after an unknown submission
                        // or after the credential submission limit is reached.
                        SendLogin(allowCredentialSubmit: true);
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
                failure = characterMissing
                    ? string.Format("character '{0}' was not found on realm '{1}'", run.Config.CharacterName,
                        string.IsNullOrEmpty(run.Config.RealmName) ? "current realm" : run.Config.RealmName)
                    : reachedCharSelect
                    ? (sawCharacterMissing
                        ? string.Format("character selection for '{0}' on realm '{1}' did not complete within {2}s (character was not seen before polling became inconclusive)",
                            run.Config.CharacterName,
                            string.IsNullOrEmpty(run.Config.RealmName) ? "current realm" : run.Config.RealmName,
                            run.Config.LoginScreenTimeoutSeconds)
                        : string.Format("character selection for '{0}' did not complete within {1}s",
                            run.Config.CharacterName, run.Config.LoginScreenTimeoutSeconds))
                    : submissions > 0
                    ? "the server did not answer the login attempt (server unreachable or realm offline)"
                    : string.Format("character selection was not reached within {0}s", run.Config.LoginScreenTimeoutSeconds);
                return false;
            }

            // ---- 5. In-world confirmation -------------------------------------------
            return ConfirmInWorld(client, pid, run, out failure);
        }

        /// <summary>
        /// Waits for g_ClientConnectionState + ObjectManager.Me. This is the resume gate:
        /// the routine/profile is only restarted once both are true.
        /// </summary>
        private static bool ConfirmInWorld(Process client, int pid, MonitorRun run, out string failure)
        {
            failure = "";
            ApplyIfCurrent(run, () =>
            {
                run.ConfirmedRecoveryId = 0;
                run.ConfirmedProcessId = 0;
                run.ConfirmedCharacterName = "";
                run.ConfirmedRealmName = "";
            });
            run.Step = ReloginStep.WaitingForInWorld;
            var worldWait = Stopwatch.StartNew();
            int reissueAt = 15;

            while (worldWait.Elapsed.TotalSeconds < run.Config.InWorldTimeoutSeconds)
            {
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                if (!ReloginClient.IsAlive(client)) { failure = "the client exited while entering the world"; return false; }

                var probe = ReloginClient.Probe(pid);
                if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                bool inGame = probe.Readable && probe.InGame;
                if (inGame)
                {
                    bool localPlayerUpdated = false;
                    try { localPlayerUpdated = ObjectManager.UpdateAndCheckLocalPlayer(); } catch { /* object list not ready yet */ }

                    if (localPlayerUpdated && ObjectManager.Me != null && ObjectManager.Me.BaseAddress != 0U)
                    {
                        if (!IsCurrent(run.Generation)) { failure = "stopped"; return false; }
                        string name = ObjectManager.Me.Name;
                        string realm = string.IsNullOrEmpty(run.Config.RealmName) ? "" : ObjectManager.Me.RealmName;
                        if (string.IsNullOrEmpty(name) || (!string.IsNullOrEmpty(run.Config.RealmName) && string.IsNullOrEmpty(realm)))
                        { Thread.Sleep(1000); continue; }
                        if (!string.Equals(name, run.Config.CharacterName, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(run.Config.RealmName) && !string.Equals(realm, run.Config.RealmName, StringComparison.OrdinalIgnoreCase)))
                        {
                            failure = string.Format("character identity mismatch: expected '{0}' on '{1}', found '{2}' on '{3}'; bot left stopped",
                                run.Config.CharacterName, run.Config.RealmName, name, realm);
                            return false;
                        }
                        if (!ApplyIfCurrent(run, () =>
                        {
                            if (_processId != pid) return;
                            run.ConfirmedRecoveryId = run.ActiveRecoveryId;
                            run.ConfirmedProcessId = pid;
                            run.ConfirmedCharacterName = name;
                            run.ConfirmedRealmName = realm;
                            lock (_claimSync)
                            {
                                if (_claimedMutex != null && _claimedProcessId == pid && _claimedMutexGeneration == run.Generation)
                                    _claimedMutexRecoveryId = run.ActiveRecoveryId;
                            }
                        }) || run.ConfirmedProcessId != pid)
                        {
                            failure = "stopped";
                            return false;
                        }
                        Logging.Write("[Relogin] In the world as {0} (level {1} {2} {3}).",
                            ObjectManager.Me.Name, ObjectManager.Me.Level, ObjectManager.Me.Race, ObjectManager.Me.Class);

                        return true;
                    }
                }

                // The 3.3.5a client drops an EnterWorld that races a character-list refresh;
                // re-issue it a couple of times before declaring the attempt failed.
                if (worldWait.Elapsed.TotalSeconds >= reissueAt && reissueAt < 60)
                {
                    Logging.WriteDebug("[Relogin] Still not in the world after {0:F0}s; re-issuing EnterWorld.", worldWait.Elapsed.TotalSeconds);
                    ReloginClient.SendCharacterSelect(run.Config.CharacterName, run.Config.RealmName);
                    reissueAt += 15;
                }

                Thread.Sleep(1000);
            }

            failure = string.Format("the in-world state was not reached within {0}s", run.Config.InWorldTimeoutSeconds);
            return false;
        }

        #endregion
    }
}
