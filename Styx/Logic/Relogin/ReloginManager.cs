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
        private bool _wasInGame;
        private DateTime? _outOfGameSinceUtc;
        private DateTime _nextAttemptUtc = DateTime.MinValue;

        /// <summary>Cool-down after MaxAttemptsPerDisconnect failures.</summary>
        private const int FailureCoolDownSeconds = 300;

        #endregion

        #region Properties

        public bool IsRunning => _thread != null && _thread.IsAlive;

        public string Status
        {
            get { lock (_stateLock) return _status; }
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

                Process? client = ReloginNative.FindClient(ReloginSettings.Instance.WowExePath);
                if (client != null && ReloginNative.TryReadInGame(client, out bool inGame) && inGame)
                    return true;

                Thread.Sleep(500);
            }

            return false;
        }

        /// <summary>True when a client process is currently in the world.</summary>
        public bool IsAnyClientInGame()
        {
            Process? client = ReloginNative.FindClient(ReloginSettings.Instance.WowExePath);
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

            Process? client = ReloginNative.FindClient(settings.WowExePath);

            // ---------------------------------------------------------------- no client
            if (client == null)
            {
                if (_wasInGame)
                {
                    _wasInGame = false;
                    SetStatus("Game client is gone (crash or closed).");
                    SafeRaise(ClientLost, _lastPid);
                }

                _outOfGameSinceUtc ??= DateTime.UtcNow;

                // A crashed client is relaunched immediately (after the configured
                // delay); there is nothing to wait for, so no grace period here.
                TryRelogin("client is not running", null);
                return;
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

            // ------------------------------------------------- restart a stuck client
            bool restartClient = _attempts > 1 && settings.RestartClientOnRepeatedFailure;
            if (restartClient && client != null)
            {
                SetStatus("Restarting the game client.");
                Logging.Write("[AutoRelogin] Restarting the client after a failed attempt.");
                ReloginNative.KillClient(client);
                client = null;
                Thread.Sleep(4000);
            }

            // ------------------------------------------------------------- start client
            if (client == null)
            {
                int delay = Math.Max(0, settings.RelaunchDelaySeconds);
                if (delay > 0)
                {
                    SetStatus($"Starting the client in {delay}s.");
                    SleepInterruptible(delay * 1000);
                }

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
            SleepInterruptible(3000);

            // ------------------------------------------------------- credentials + enter
            SetStatus("Sending credentials.");
            ReloginNative.FocusWindow(window);
            SleepInterruptible(800);

            // After a disconnect the client shows a modal "You have been disconnected
            // from the server" box whose OK button is the default, so Enter dismisses
            // it. Escape is deliberately NOT used: on the glue screen it can open the
            // exit dialog (and, with the login screen already gone, kill the client).
            // If the box is not there the Enter simply submits the current, empty
            // login; the next attempt (a restarted client) starts from a clean screen.
            ReloginNative.SendKey(ReloginNative.VkReturn);
            SleepInterruptible(1200);

            ReloginNative.FocusWindow(window);
            SleepInterruptible(300);

            // The account field is focused by default; clear whatever the client has
            // pre-filled (after a disconnect it holds the last account name) and retype.
            ReloginNative.SendSelectAll();
            ReloginNative.SendKey(ReloginNative.VkDelete);
            SleepInterruptible(200);

            ReloginNative.SendText(settings.AccountName);
            SleepInterruptible(400);
            ReloginNative.SendKey(ReloginNative.VkTab);
            SleepInterruptible(300);
            ReloginNative.SendText(settings.Password);
            SleepInterruptible(300);
            ReloginNative.SendKey(ReloginNative.VkReturn);

            SetStatus("Waiting for the character list.");
            SleepInterruptible(Math.Max(5, settings.CharSelectDelaySeconds) * 1000);

            // Character select: the first character is preselected and the "Enter World"
            // button has the focus once the list is loaded, so Enter enters the world.
            // The ladder below covers the other cases - a still loading list (focus not
            // on the button yet), a realm list, or the "press Enter again" prompt - by
            // alternating Enter with Tab + Enter until the deadline expires.
            SetStatus("Entering the world.");
            DateTime enterDeadline = DateTime.UtcNow.AddSeconds(Math.Max(30, settings.EnterWorldTimeoutSeconds));
            int press = 0;
            while (!_stopRequested && DateTime.UtcNow < enterDeadline)
            {
                ReloginNative.FocusWindow(window);

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
                if (_stopRequested)
                    return false;

                if (ReloginNative.TryReadInGame(client, out bool inGame) && inGame)
                    return true;

                Thread.Sleep(500);
            }

            return false;
        }

        /// <summary>Sleep that aborts early when the monitor is stopped.</summary>
        private void SleepInterruptible(int milliseconds)
        {
            int remaining = milliseconds;
            while (remaining > 0 && !_stopRequested)
            {
                int slice = Math.Min(250, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
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
