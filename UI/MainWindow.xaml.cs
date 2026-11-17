using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using GreenMagic;
using Styx;
using Styx.Common;
using Styx.CommonBot;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Profiles;
using Styx.Logic.Relogin;
using Styx.WoWInternals;

using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Main window for CopilotBuddy - HB 5.4.8 style interface.
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        #region Fields

        private Memory? _memory;
        private ExecutorRand? _executor;
        private Paragraph? _logParagraph;
        private WpfFontFamily? _logFont;
        private int _logCount;
        private const int MaxLogLines = 1000;
        private bool _isRunning;
        private DispatcherTimer? _infoTimer;

        /// <summary>
        /// Mutex that claims ownership of the attached WoW process.
        /// Prevents another CopilotBuddy from attaching to the same WoW.
        /// HB 4.3.4 pattern: held for entire session lifetime.
        /// </summary>
        private Mutex? _processMutex;

        /// <summary>
        /// Selected WoW process ID from the process selector.
        /// null = pending selection, 0 = user cancelled.
        /// HB 4.3.4 pattern.
        /// </summary>
        private int? _selectedWoWProc;

        /// <summary>
        /// Auto Relogin event wiring happens once; the handlers survive every
        /// detach/attach cycle (the manager outlives a single game client).
        /// </summary>
        private bool _reloginWired;

        /// <summary>True when the bot was running at the moment the client went away.</summary>
        private bool _wasRunningBeforeRelogin;

        /// <summary>
        /// The command-line auto-load/auto-start tail of the startup path has not run yet
        /// (the startup path never completed an attach, and a replayed loss kept the late
        /// attach from applying it). Carried until a successful attach applies it.
        /// </summary>
        private bool _pendingAutoStartConfig;

        /// <summary>
        /// Set while the client is being relaunched and the bot re-attached, so a
        /// burst of manager events cannot start two recoveries at once.
        /// </summary>
        private volatile bool _reloginInProgress;

        /// <summary>
        /// 0 = no re-attach in flight, 1 = one is running. Guards against two
        /// simultaneous attach sequences (Interlocked, so it is race free).
        /// </summary>
        private int _reattachRunning;

        /// <summary>The running re-attach sequence, awaited by Window_Closing.</summary>
        private Task? _reattachTask;

        /// <summary>The running detach sequence, awaited by Window_Closing.</summary>
        private Task? _detachTask;

        /// <summary>Consecutive failed re-attach sequences (reset by a successful one).</summary>
        private int _reattachFailures;

        /// <summary>
        /// A ClientLost / ClientDisconnected that arrived while a recovery was already in
        /// flight. Stored (not dropped) and replayed once the re-attach worker released the
        /// attachment: -1 = pending with pid 0, 0 = nothing pending.
        /// </summary>
        private int _pendingRecoveryPid;

        /// <summary>
        /// Serializes the recovery-ownership handoff: the ownership read and the park in
        /// <see cref="DispatchClientRecovery"/> must not interleave with the flag clear and
        /// the drain of the pending slot that every owner performs when it finishes (the
        /// startup finally, the re-attach finally and the recovery callbacks). Without it a
        /// loss stored between a drain and the next read would keep the slot occupied with
        /// nobody left to replay it.
        /// </summary>
        private readonly object _recoveryGate = new object();

        /// <summary>How often a failed re-attach asks the monitor for another attempt.</summary>
        private const int MaxReattachRetries = 10;

        /// <summary>
        /// Set when a ClientLoggedIn event arrives while the detachment is still in
        /// flight: the attach would fail (ObjectManager is still initialized), so the
        /// pid is parked here and retried once DetachFromGame() has finished.
        /// </summary>
        private int _pendingLoggedInPid;

        /// <summary>
        /// 0 = the one-time, attachment independent initialization has not run yet.
        /// It must happen exactly once per process, no matter whether the first client
        /// was attached by the startup path or by Auto Relogin much later.
        /// </summary>
        private int _globalInitDone;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            // Setup logging
            InitializeLogging();

            // Setup info panel timer
            InitializeInfoTimer();

            // Log version (HB 4.3.4: "Honorbuddy v{0} started.")
            // AssemblyVersion is intentionally not set in .csproj (causes BAML crash under .NET 10).
            const string BotVersion = "1.6.7.6";
            Title = $"CopilotBuddy v{BotVersion}";
            Logging.Write("CopilotBuddy v{0} started. Original HonorBuddy by Apoc, raphus, highvoltz, bobby53, xanathos, chinajade, Natfoth. Ported to WotLK 3.3.5a by Likon69.", BotVersion);
        }

        #endregion

        #region Initialization

        private void InitializeLogging()
        {
            // Initialize RichTextBox
            rtbLog.Document.Blocks.Clear();
            _logParagraph = new Paragraph();
            rtbLog.Document.Blocks.Add(_logParagraph);
            _logFont = new WpfFontFamily("Consolas");
            _logCount = 0;

            // Setup log file path
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            var timestamp = DateTime.Now;
            Logging.LogFilePath = Path.Combine(logDir,
                $"{timestamp.Year}-{timestamp.Month:D2}-{timestamp.Day:D2}_{timestamp.Hour:D2}{timestamp.Minute:D2}_{Process.GetCurrentProcess().Id}.log");

            // Subscribe to logging events
            Logging.OnLogMessage += OnLogMessage;
        }

        private void InitializeInfoTimer()
        {
            _infoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _infoTimer.Tick += (s, e) => UpdateInfoPanel();
            
            // Subscribe to StatusText changes — updates StatusBar like HB 4.3.4
            TreeRoot.OnStatusTextChanged += (sender, e) =>
            {
                Dispatcher.BeginInvoke(() => lblActivityText.Content = e.NewStatus ?? "Ready");
            };
            
            // Show default values immediately
            UpdateInfoPanel();
        }

        /// <summary>
        /// WPF best practice: restore window geometry here — fires when the Win32 handle is
        /// created but BEFORE the window is shown.  Position applied here takes effect on the
        /// first rendered pixel with no visual flash (unlike Window_Loaded which fires after
        /// the window is already visible and the XAML size has been applied).
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var s = UISettings.Instance; // singleton already loaded in its ctor

                // Size — must be positive and non-default
                if (s.MainWindowWidth > 0)  this.Width  = s.MainWindowWidth;
                if (s.MainWindowHeight > 0) this.Height = s.MainWindowHeight;

                // Position — only restore if explicitly saved (-1 = never saved / first run)
                if (s.MainWindowLocationX >= 0) this.Left = s.MainWindowLocationX;
                if (s.MainWindowLocationY >= 0) this.Top  = s.MainWindowLocationY;

                // State — never restore Minimized (window would start hidden)
                if (s.MainWindowState != WindowState.Minimized)
                    this.WindowState = s.MainWindowState;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UISettings] Failed to restore window geometry: " + ex.Message);
            }
        }

        // Startup (attach + EndScene hook publication) runs on a worker thread.  These
        // let Window_Closing cancel it and wait for it, so an attach can never publish a
        // hook after teardown has already started.
        private readonly CancellationTokenSource _startupCts = new CancellationTokenSource();
        private Task? _startupTask;
        private volatile bool _isClosing;
        private bool _closingHandled;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Window geometry is already restored in OnSourceInitialized above.
            // Only do non-geometry UI setup here.
            try
            {
                // Restore Enhanced Mode checkbox state
                if (UISettings.Instance.EnhancedMode)
                    EnhancedMode.IsChecked = true;
            }
            catch { /* Ignore errors on first run */ }

            SetStatus("Searching for WoW...");
            ToggleButtons(false);

            // Auto Relogin has to be configurable *before* a client exists (that is the
            // whole point of "start the game for me"), so Settings stays reachable even
            // while nothing is attached.
            btnSettings.IsEnabled = true;

            CancellationToken startupToken = _startupCts.Token;
            _startupTask = Task.Run(() =>
            {
                try
                {
                    // Auto Relogin: wire the events first (they are needed by both the
                    // startup wait below and by every later crash/disconnect), then let
                    // the manager start the client if nothing is running.
                    InitializeAutoRelogin();

                    if (!FindAndAttachToWoW(startupToken))
                    {
                        // With Auto Relogin enabled the client may simply not be running
                        // yet: the monitor starts it and logs in, then we retry the attach.
                        if (!WaitForAutoReloginAndAttach(startupToken))
                            return;
                    }

                    if (ObjectManager.IsInitialized)
                    {
                        Logging.Write("Attached to WoW with ID {0}", _selectedWoWProc!.Value);

                        // Log character info (HB 4.3.4 pattern after attach)
                        if (ObjectManager.Me != null)
                        {
                            Logging.Write("Character is a level {0} {1} {2}",
                                ObjectManager.Me.Level,
                                ObjectManager.Me.Race,
                                ObjectManager.Me.Class);
                            // Honorbuddy 3.3.5a simply logged RealZoneText
                            Logging.Write("Current zone is {0}", ObjectManager.Me.RealZoneText);
                        }

                        // Reinitialize and load settings for this character (HB 4.3.4 pattern).
                        // Must run before the plugin initialization below: that one reads
                        // CharacterSettings.EnabledPlugins for the freshly attached character.
                        LoadSettings();

                        // One-time, attachment independent initialization: tree hooks, bot
                        // events, external bots, plugins. Shared with the Auto Relogin
                        // late-attach path so a reconnect never doubles the subscriptions.
                        EnsureGlobalBotInitialization();

                        // Initialize Combat Routines (compile and load from the Routines folder).
                        // This must be done AFTER attachment when ObjectManager.Me is available.
                        try
                        {
                            Styx.Logic.Combat.RoutineManager.Init();
                        }
                        catch (Exception ex)
                        {
                            Logging.Write(Colors.Red, "Failed to initialize combat routines: {0}", ex.Message);
                            Logging.WriteException(ex);
                        }

                        Dispatcher.Invoke(() =>
                        {
                            // The window may already be closing: Window_Closing pumps this
                            // dispatcher queue while it waits for the startup task, so this
                            // callback can run during teardown.  Do not touch the UI, restart
                            // the info timer or auto-start (StartBot is also guarded) then.
                            if (_isClosing)
                                return;

                            // Populate bot selector with all loaded bots (built-in + external)
                            cmbBotSelector.Items.Clear();
                            foreach (var bot in BotManager.Instance.Bots)
                            {
                                cmbBotSelector.Items.Add(bot.Key);
                            }

                            // Restore last selected bot (HB 4.3.4 pattern)
                            if (cmbBotSelector.Items.Count > 0)
                            {
                                int selectedIndex = CharacterSettings.Instance.SelectedBotIndex;
                                if (selectedIndex < 0 || selectedIndex >= cmbBotSelector.Items.Count)
                                {
                                    // Invalid index, reset to 0
                                    CharacterSettings.Instance.SelectedBotIndex = 0;
                                    selectedIndex = 0;
                                }
                                cmbBotSelector.SelectedIndex = selectedIndex;
                            }

                            ToggleButtons(true);
                            SetStatus("CopilotBuddy Startup Complete");
                            _infoTimer.Start();

                            // AfterInitOnAuthSuccess equivalent for HBRelog integration.
                            // Mirrors Honorbuddy 4.3.4 MainWindow.xaml.cs:419-437 + Start():847-866.
                            // CommandLine.AutoLoadProfile / AutoStartBotName are parsed in
                            // Styx/Helpers/CommandLine.cs but were never consumed prior to this.
                            ApplyAutoStartConfig();
                        });
                    }
                    else
                    {
                        Logging.WriteQuiet(Colors.Red, "Failed to initialize ObjectManager");
                        Dispatcher.Invoke(() => SetStatus("Initialization failed"));
                    }
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus("Error");
                        System.Windows.MessageBox.Show(
                            $"Injection failed!\n\nError: {ex.Message}",
                            "CopilotBuddy - Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
                finally
                {
                    // Startup no longer owns the attach. From here on Auto Relogin may
                    // re-attach by itself - also when nothing was attached yet, because
                    // the manager is the component that brings the client up and it
                    // raises ClientLoggedIn once the character is in the world.
                    _startupCompleted = true;

                    // Nothing attached and we are not shutting down: the client may already
                    // be in the world (the monitor saw it while the startup path was giving
                    // up, or it came up just after the wait expired), in which case no
                    // further ClientLoggedIn would ever be raised. Ask for one more.
                    if (!_isClosing && !ObjectManager.IsInitialized)
                    {
                        try { ReloginManager.Instance.RequestLoggedInSignal(); }
                        catch (Exception ex) { Logging.WriteDebug("Auto Relogin: could not request a login signal: {0}", ex.Message); }
                    }

                    // Replay a client loss that arrived while startup owned the attachment
                    // (it was parked, not dropped, by DispatchClientRecovery). The pid guard
                    // there decides whether it still concerns the current attachment.
                    ReplayParkedRecovery("the client was lost during startup");
                }
            });
        }

        /// <summary>
        /// Finds available WoW processes, handles auto-attach or process selection.
        /// Ported from HB 4.3.4 MainWindow.FindAndAttachToWoW() with 6.2.3 improvements.
        /// 
        /// Logic:
        ///   1. Check /pid= command line arg for explicit process selection
        ///   2. Find all Wow.exe processes matching build 12340
        ///   3. Filter: logged in + not claimed by another CopilotBuddy (via mutex)
        ///   4. If 0 available: show error
        ///   5. If 1 available: auto-attach
        ///   6. If 2+: show ProcessSelectorWindow
        ///   7. Attach to selected process
        /// </summary>
        /// <param name="preferPid">
        /// When set, that client is attached even if several clients are running. Used by the
        /// unattended Auto Relogin path, which must never open the process selector dialog.
        /// </param>
        private bool FindAndAttachToWoW(CancellationToken cancellationToken = default, int preferPid = 0)
        {
            // Cancelled while the user was closing the window: never start an attach.
            if (cancellationToken.IsCancellationRequested || _isClosing)
            {
                Logging.WriteDebug("Attach skipped: the application is closing.");
                return false;
            }

            Logging.Write("Searching for WoW process...");

            // Step 1: Check for /pid= command line argument
            string? pidArg = Environment.GetCommandLineArgs()
                .FirstOrDefault(s => s.StartsWith("/pid=", StringComparison.OrdinalIgnoreCase));

            // An explicit /pid= target stays authoritative while that process is alive, so
            // the relogin pid only takes over once it exited (or when there was no /pid=).
            int commandLinePid = GetCommandLinePid();

            if (preferPid != 0 && (commandLinePid == 0 || !ReloginNative.IsProcessAlive(commandLinePid)))
                pidArg = "/pid=" + preferPid;
            else if (commandLinePid != 0)
                pidArg = "/pid=" + commandLinePid;

            // Keep the Auto Relogin monitor on the same client: without this it would
            // follow the configured executable / the first live client and could log a
            // *different* WoW in while the pinned one is still running. The pin is only
            // held while that process lives - 0 means "follow any live client", which is
            // also what a crashed /pid= target has to fall back to.
            try
            {
                ReloginManager.Instance.PinnedProcessId =
                    ReloginNative.IsProcessAlive(commandLinePid) ? commandLinePid : 0;
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("Auto Relogin: could not pin the client: {0}", ex.Message);
            }

            _selectedWoWProc = null;
            var candidates = new List<Process>();

            if (pidArg == null)
            {
                // Find ALL Wow processes (covers the standard client, the WoW.exe spelling,
                // and the Luacct.exe variant used by some private-server clients)
                string[] wowProcessNames = { "Wow", "WoW", "Luacct" };
                var wowProcesses = wowProcessNames.SelectMany(n => Process.GetProcessesByName(n)).ToArray();

                foreach (var proc in wowProcesses)
                {
                    try
                    {
                        if (proc.HasExited) continue;
                        int build = proc.MainModule?.FileVersionInfo.FilePrivatePart ?? 0;
                        // build == 0 means no version resource (custom/private server client)
                        if (build == ObjectManager.SupportedBuild || build == 0)
                            candidates.Add(proc);
                    }
                    catch { /* Access denied or process exited */ }
                }
            }
            else
            {
                // Explicit PID from command line
                string pidStr = pidArg.Substring("/pid=".Length).Trim();
                if (int.TryParse(pidStr, out int pid))
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (!proc.HasExited)
                        {
                            int build = proc.MainModule?.FileVersionInfo.FilePrivatePart ?? 0;
                            // Same rule as the enumeration path above: build == 0 means the
                            // client has no version resource (private-server Wow.exe), which
                            // is still a valid 3.3.5a client.  Requiring the exact build here
                            // made HBRelog-style "/pid=<id>" attach fail on those clients.
                            if (build == ObjectManager.SupportedBuild || build == 0)
                                candidates.Add(proc);
                        }
                    }
                    catch
                    {
                        Logging.Write(Colors.Red, "Process with PID {0} not found or inaccessible.", pid);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                Logging.WriteQuiet(Colors.Red, "WoW.exe not found! Please launch WoW 3.3.5a first.");
                Dispatcher.Invoke(() => SetStatus("WoW not found"));
                return false;
            }

            // Step 2: Filter by logged-in state + mutex availability
            var available = new List<KeyValuePair<int, Mutex>>();
            foreach (var proc in candidates)
            {
                try
                {
                    using var tempMemory = new Memory(proc.Id);

                    // Check if player is logged in
                    bool isLoggedIn = tempMemory.Read<byte>(Styx.Offsets.GlobalOffsets.InGame) != 0;
                    if (!isLoggedIn)
                        continue;

                    // Try to claim the process via mutex
                    var mutex = ProcessMutex.Create(proc.Id, out bool createdNew);
                    if (!createdNew)
                    {
                        // Another CopilotBuddy already owns this process
                        mutex.Close();
                        continue;
                    }

                    available.Add(new KeyValuePair<int, Mutex>(proc.Id, mutex));
                }
                catch { /* Process exited or memory read failed */ }
            }

            if (available.Count == 0)
            {
                Logging.WriteQuiet(Colors.Red, "No available WoW processes. They may not be logged in or already attached by another CopilotBuddy.");
                Dispatcher.Invoke(() => SetStatus("No available WoW"));
                return false;
            }

            // Step 3: Auto-attach if only one, otherwise show selector
            if (available.Count == 1)
            {
                _selectedWoWProc = available[0].Key;
                _processMutex = available[0].Value;
            }
            else
            {
                // Release all mutexes — ProcessSelectorWindow will acquire its own
                foreach (var kvp in available)
                    kvp.Value.Close();

                // Show selector on UI thread (HB 4.3.4 pattern)
                _selectedWoWProc = null;
                ProcessSelectorWindow.ProcessEntry? selectedEntry = null;

                Dispatcher.Invoke(() =>
                {
                    var psw = new ProcessSelectorWindow { Owner = this };
                    if (psw.ShowDialog() == true && psw.Entry != null)
                    {
                        selectedEntry = psw.Entry;
                        _selectedWoWProc = psw.Entry.ProcessId;
                    }
                    else
                    {
                        _selectedWoWProc = 0; // Cancelled
                    }
                });

                if (_selectedWoWProc == null || _selectedWoWProc == 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus("Cancelled");
                        Close();
                    });
                    return false;
                }

                // Use the mutex from the dialog directly — it's already acquired
                _processMutex = selectedEntry!.Mutex;
            }

            // Step 4: Attach to selected process
            int wowPid = _selectedWoWProc.Value;
            Logging.Write(Colors.Green, "Please wait a few seconds while CopilotBuddy initializes.");
            // Everything below can fail. The claim acquired in Step 2/3 must not survive a
            // failed attach, otherwise the next attempt (and any other instance) sees the
            // client as owned and the attach can never succeed again.

            Logging.WriteDebug("Using WoW with process ID {0}", wowPid);
            Logging.WriteDebug("Platform: {0}", Environment.Is64BitProcess ? "x64" : "x86");
            Logging.WriteDebug("Executable Path: {0}", AppDomain.CurrentDomain.BaseDirectory);

            try
            {
                var memory = new Memory(wowPid);

                // Verify still logged in before committing
                if (memory.Read<byte>(Styx.Offsets.GlobalOffsets.InGame) == 0)
                {
                    memory.Dispose();
                    Logging.Write(Colors.Red, "Process ID {0} is not logged in game!", wowPid);
                    Dispatcher.Invoke(() => SetStatus("Not logged in"));
                    ReleaseProcessMutex();
                    return false;
                }

                // Last checkpoint before publishing the attachment (and installing the
                // EndScene hook): Window_Closing sets _isClosing and then waits for this
                // task to finish before it runs ObjectManager.Shutdown().
                if (cancellationToken.IsCancellationRequested || _isClosing)
                {
                    memory.Dispose();
                    Logging.WriteDebug("Attach aborted: the application is closing.");
                    ReleaseProcessMutex();
                    return false;
                }

                ObjectManager.Initialize(memory);

                // HB 6.2.3: Initialize hotkeys against the WoW window handle so that
                // hotkeys activate when WoW is in the foreground, not CopilotBuddy.
                HotkeysManager.Initialize(ObjectManager.WoWProcess!.MainWindowHandle);

                // Re-attaching after an Auto Relogin runs this method again. Register()
                // allocates a new hotkey id on every call, so skip the duplicate.
                if (!HotkeysManager.Hotkeys.Any(h => h.Name == "Core_BringBotToForeground"))
                    HotkeysManager.Register("Core_BringBotToForeground", Keys.F7, Styx.Common.ModifierKeys.Shift, BringBotToForeground);

                _memory = memory;
                _executor = ObjectManager.Executor;
                return true;
            }
            catch (Exception ex)
            {
                // Whatever got published before the failure is rolled back here; the claim
                // must not outlive a failed attach.
                try { ObjectManager.Shutdown(); } catch { /* best effort */ }
                ReleaseProcessMutex();
                _memory = null;
                _executor = null;

                Logging.Write(Colors.Red, "Error attaching to WoW! - {0}", ex.Message);
                Logging.WriteException(ex);
                Dispatcher.Invoke(() => SetStatus("Attach failed"));
                return false;
            }
        }

        /// <summary>
        /// Drops the process claim taken by the last attach attempt. Safe to call when no
        /// mutex is held. Must NOT be used after a successful attach - the claim protects
        /// the attached session for its whole lifetime.
        /// </summary>
        private void ReleaseProcessMutex()
        {
            Mutex? mutex = Interlocked.Exchange(ref _processMutex, null);
            if (mutex == null)
                return;

            try { mutex.Dispose(); } catch { /* already released */ }
        }

        #region Auto Relogin

        /// <summary>
        /// True once the startup task has finished and the UI is usable. Before that
        /// the startup flow owns the attach, so the Auto Relogin re-attach handler
        /// must stay out of its way.
        /// </summary>
        private volatile bool _startupCompleted;

        /// <summary>
        /// In-game state of the live client, for the post-detach re-arm: 1 = in the world,
        /// 0 = readable and not in the world (including "no client at all"), -1 = the state
        /// could not be determined because the process could not be read right now.
        /// </summary>
        private static int ProbeClientInGame()
        {
            try
            {
                Process? client = ReloginNative.FindClient(ReloginSettings.Instance.WowExePath,
                    ReloginManager.Instance.PinnedProcessId);
                if (client == null)
                    return 0;

                if (!ReloginNative.TryReadInGame(client, out bool inGame))
                    return -1;

                return inGame ? 1 : 0;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Dispatches a client loss that was parked in <see cref="_pendingRecoveryPid"/>,
        /// if any, so it is handled instead of lingering. Must be called on the UI thread
        /// after recovery ownership was released.
        /// </summary>
        private bool ReplayParkedRecovery(string reason)
        {
            int pendingRecovery;
            lock (_recoveryGate)
                pendingRecovery = Interlocked.Exchange(ref _pendingRecoveryPid, 0);

            if (pendingRecovery == 0 || _isClosing)
                return false;

            int pid = pendingRecovery == -1 ? 0 : pendingRecovery;

            // A parked loss only concerns the client this window is attached to. When it
            // names a different process, that client is already gone and a newer one was
            // attached in its place: replaying the loss would detach a healthy attachment
            // and throw away a bot that was just restarted. Report "nothing was replayed"
            // so the caller keeps the running decision for the current client. Nothing is
            // dropped while no attachment is published (ObjectManager.WoWProcess is only
            // null until the attach completes), so a genuine loss is never lost here.
            int attachedPid = 0;
            try
            {
                if (ObjectManager.IsInitialized)
                    attachedPid = ObjectManager.WoWProcess?.Id ?? 0;
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("Auto Relogin: could not determine the attached client: {0}", ex.Message);
            }

            if (pid != 0 && attachedPid != 0 && pid != attachedPid)
            {
                Logging.WriteDebug(
                    "Auto Relogin: dropping a parked client loss for pid {0}; the attached client is {1}.",
                    pid, attachedPid);
                return false;
            }

            Dispatcher.BeginInvoke(new Action<int, string>(DispatchClientRecovery), pid, reason);
            return true;
        }

        /// <summary>
        /// The pid from an explicit <c>/pid=</c> command line argument, 0 when there is
        /// none (or it is not a number).
        /// </summary>
        private static int GetCommandLinePid()
        {
            try
            {
                string? pidArg = Environment.GetCommandLineArgs()
                    .FirstOrDefault(s => s.StartsWith("/pid=", StringComparison.OrdinalIgnoreCase));

                if (pidArg == null)
                    return 0;

                return int.TryParse(pidArg.Substring("/pid=".Length).Trim(), out int pid) ? pid : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Hooks the manager events (once) and starts the monitor when the feature is
        /// enabled. Called from the startup task, i.e. off the UI thread.
        /// </summary>
        private void InitializeAutoRelogin()
        {
            try
            {
                if (!_reloginWired)
                {
                    _reloginWired = true;
                    ReloginManager.Instance.ClientLost += OnReloginClientLost;
                    ReloginManager.Instance.ClientDisconnected += OnReloginClientDisconnected;
                    ReloginManager.Instance.ClientLoggedIn += OnReloginClientLoggedIn;
                    ReloginManager.Instance.StatusChanged += OnReloginStatus;
                }

                ReloginSettings settings = ReloginSettings.Instance;
                if (!settings.Enabled)
                    return;

                if (!settings.HasClientPath)
                {
                    Logging.Write(Colors.Red,
                        "Auto Relogin is enabled but the WoW.exe path is missing or invalid: '{0}'", settings.WowExePath);
                    return;
                }

                if (!settings.HasCredentials)
                {
                    Logging.Write(Colors.Red,
                        "Auto Relogin is enabled but no account name / password is configured.");
                }

                // Pin the monitor to the explicit /pid= target *before* it starts, using
                // the same rule as FindAndAttachToWoW: while that process is alive no other
                // client may be logged in or adopted, and once it exits the pin falls back
                // to any live client.
                int commandLinePid = GetCommandLinePid();
                ReloginManager.Instance.PinnedProcessId =
                    ReloginNative.IsProcessAlive(commandLinePid) ? commandLinePid : 0;

                // The startup task may be finishing while the window is already closing;
                // Window_Closing stops the monitor before it waits for this task, so it must
                // not be started again afterwards.
                if (_isClosing || _startupCts.IsCancellationRequested)
                    return;

                ReloginManager.Instance.Start();
                Logging.Write("Auto Relogin: monitoring the game client ({0}).", settings.WowExePath);
            }
            catch (Exception ex)
            {
                // Never let the optional relogin feature abort the startup sequence.
                Logging.Write(Colors.Red, "Auto Relogin: initialization failed: {0}", ex.Message);
                Logging.WriteDebug("[AutoRelogin] {0}", ex);
            }
        }

        /// <summary>
        /// Startup fallback: when no client could be attached and Auto Relogin is
        /// enabled, wait for the monitor to start the client and log in, then attach.
        /// </summary>
        private bool WaitForAutoReloginAndAttach(CancellationToken cancellationToken)
        {
            ReloginSettings settings;
            try
            {
                settings = ReloginSettings.Instance;
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "Auto Relogin: settings unavailable: {0}", ex.Message);
                return false;
            }

            if (!settings.Enabled || !settings.HasClientPath || !settings.HasCredentials)
                return false;

            // The application is already shutting down: do not start (or keep running) the
            // monitor just to have it restarted by the next process.
            if (cancellationToken.IsCancellationRequested || _isClosing)
                return false;

            Logging.Write("Auto Relogin: no client to attach to - starting it and logging in.");
            Dispatcher.Invoke(() => SetStatus("Auto Relogin: starting WoW..."));

            ReloginManager.Instance.Start();

            // Closing can win the race between the cancellation check above and this start;
            // the monitor must not relaunch a client during the teardown.
            if (cancellationToken.IsCancellationRequested || _isClosing)
            {
                try { ReloginManager.Instance.Stop(); } catch { /* ignore */ }
                return false;
            }

            // Cold start of the client + login + world load, with some slack.
            int budgetMs = (settings.ClientStartupTimeoutSeconds
                            + settings.CharSelectDelaySeconds
                            + settings.EnterWorldTimeoutSeconds
                            + 60) * 1000;

            if (!ReloginManager.Instance.WaitForInGame(budgetMs, cancellationToken))
            {
                Logging.Write(Colors.Red, "Auto Relogin: the character did not reach the world in time.");
                Dispatcher.Invoke(() => SetStatus("Auto Relogin failed"));
                return false;
            }

            Logging.Write("Auto Relogin: client is in the world - attaching.");
            return FindAndAttachToWoW(cancellationToken);
        }

        private void OnReloginClientLost(int pid)
        {
            DispatchClientRecovery(pid, "the game client is gone (crash or closed)");
        }

        private void OnReloginClientDisconnected(int pid)
        {
            DispatchClientRecovery(pid, "the character left the world (disconnect or logout)");
        }

        /// <summary>
        /// Stops the bot, waits for the worker thread, then tears the attachment down
        /// so the manager can restart/log in the client and we can attach again.
        /// </summary>
        private void DispatchClientRecovery(int pid, string reason)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<int, string>(DispatchClientRecovery), pid, reason);
                return;
            }

            if (_isClosing)
                return;

            // Ownership handoff: the startup worker owns the attachment until its finally
            // releases it (_startupCompleted), _reloginInProgress covers a whole recovery
            // (detach, relogin, re-attach) and _reattachRunning is the token of a re-attach
            // worker - FindAndAttachToWoW, LoadSettings, EnsureGlobalBotInitialization and
            // RoutineManager.Init all run under one of them. Detaching underneath those
            // would corrupt what they publish, so the event is retained instead of dropped
            // and replayed when the owner is done.
            bool parked = false;
            lock (_recoveryGate)
            {
                if (_reloginInProgress || !_startupCompleted || Volatile.Read(ref _reattachRunning) != 0)
                {
                    Interlocked.Exchange(ref _pendingRecoveryPid, pid == 0 ? -1 : pid);
                    parked = true;
                }
            }

            if (parked)
            {
                Logging.WriteDebug("Auto Relogin: {0} while a relogin/re-attach is in flight; replaying it afterwards.", reason);
                return;
            }

            // Only recover the client this window is (or was) attached to.
            if (_selectedWoWProc.HasValue && pid != 0 && pid != _selectedWoWProc.Value)
                return;

            _reloginInProgress = true;
            _reattachFailures = 0;
            Interlocked.Exchange(ref _pendingRecoveryPid, 0);
            // OR-instead-of-assign: a loss that is replayed while a previous cycle is still
            // recovering must not lose the original decision ("the bot was running") just
            // because that cycle already stopped it. Reset after a successful attach.
            _wasRunningBeforeRelogin = _wasRunningBeforeRelogin
                                       || _isRunning
                                       || TreeRoot.State != TreeRootState.Stopped;

            Logging.Write(Colors.Orange, "Auto Relogin: {0}. Detaching from the client.", reason);

            if (_isRunning)
                StopBot();

            SetStatus("Reconnecting...");
            ToggleButtons(false);

            // ToggleButtons(false) also disables Settings; keep it reachable, because a
            // login that keeps failing (wrong password, realm down) is fixed there and
            // the recovery can run for minutes.
            btnSettings.IsEnabled = true;

            _infoTimer?.Stop();

            _detachTask = Task.Run(() =>
            {
                // The worker must be gone before the EndScene hook and the process
                // handles are released, or it keeps ticking against a dead client. A tick
                // in native or CPU bound work can outlive one 15 s window; TreeRoot.Stop()
                // only interrupts managed waits, and the worker reaches Stopped in its
                // finally once the current tick returns. Waiting longer is therefore safe,
                // while detaching on a timeout would pull the resources from under it -
                // keep waiting, and only give up when the application is closing.
                while (!WaitForTreeRootStop(15000) && !_isClosing && !_startupCts.IsCancellationRequested)
                    Logging.Write("Auto Relogin: the bot thread is still stopping; waiting before detaching.");

                // Window_Closing tears the attachment down itself; detaching a second
                // time here would race with ObjectManager.Shutdown().
                if (_isClosing || _startupCts.IsCancellationRequested)
                    return;

                DetachFromGame();

                // A ClientLoggedIn that arrived while we were detaching was parked above;
                // handle it now that the old attachment is gone, otherwise the client
                // would have to be logged in twice before the bot comes back.
                int pendingPid = Interlocked.Exchange(ref _pendingLoggedInPid, 0);
                if (pendingPid != 0 && !_isClosing)
                {
                    Dispatcher.BeginInvoke(new Action<int>(OnReloginClientLoggedIn), pendingPid);
                }
                else if (!_isClosing && _reattachFailures <= MaxReattachRetries)
                {
                    // No login was parked, but the character may be back in the world
                    // already (it reconnected while we were still attached or during the
                    // drain above). The monitor's in-game state is latched in that case, so
                    // it will not announce the login again on its own and this detached
                    // application would never attach. Ask for a fresh transition - without
                    // trusting a single read: a process that cannot be read right now must
                    // not be mistaken for "not in the world". The monitor keeps its latched
                    // state in that case, so the session would stay detached even after the
                    // reads recover.
                    int inGame = ProbeClientInGame();
                    for (int attempt = 1; attempt <= 3 && inGame < 0 && !_isClosing; attempt++)
                    {
                        Logging.WriteDebug("Auto Relogin: could not read the in-game state after the detach (attempt {0}); retrying.", attempt);
                        Thread.Sleep(1000);
                        inGame = ProbeClientInGame();
                    }

                    // 1 = in the world and -1 = unreadable both need the signal. 0 = the
                    // client is definitely out of the world, which the monitor detects (and
                    // relogins) on its own. Requesting it unconditionally would be harmless
                    // too, but this keeps the log meaningful.
                    if (inGame != 0 && !_isClosing)
                    {
                        Logging.WriteDebug("Auto Relogin: requesting a login signal after the detach (in-game probe: {0}).", inGame);
                        try { ReloginManager.Instance.RequestLoggedInSignal(); }
                        catch (Exception ex) { Logging.WriteDebug("Auto Relogin: could not request a login signal after the detach: {0}", ex.Message); }
                    }
                }
            });
        }

        /// <summary>
        /// Releases the current attachment (hook, handles, mutex) but keeps the
        /// application alive. Runs on a worker thread - never touch the UI here.
        /// </summary>
        private void DetachFromGame()
        {
            // Take our own state out of the fields FIRST; ObjectManager.Shutdown() below is
            // the detach-completion boundary. ObjectManager.IsInitialized (=> Wow != null)
            // only becomes false inside that call and a new attach is only allowed once it
            // is false, so no re-attach can publish _processMutex/_memory/_selectedWoWProc
            // while this method is still clearing them - clearing them afterwards could
            // wipe (and dispose) the freshly published attachment.
            //
            // The process claim itself stays alive until the shutdown returned: another
            // CopilotBuddy must not be able to claim the client while the EndScene hook is
            // still being restored. Only the field is cleared here, the mutex is disposed
            // in the finally below.
            Mutex? mutex = Interlocked.Exchange(ref _processMutex, null);
            _memory = null;
            _executor = null;
            _selectedWoWProc = null;

            try
            {
                ObjectManager.Shutdown();
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("Auto Relogin: ObjectManager.Shutdown failed: {0}", ex.Message);
            }
            finally
            {
                try { mutex?.Dispose(); } catch { /* already released */ }
            }

            // Make sure the monitor keeps running: it is the component that brings the
            // client back, and it also re-raises ClientLoggedIn when the world is up.
            // Not while closing: Window_Closing has already stopped it and must not have
            // it restarted (and relaunching clients) underneath the teardown.
            if (!_isClosing && !_startupCts.IsCancellationRequested)
            {
                try { ReloginManager.Instance.Start(); } catch { /* ignore */ }
            }

            Logging.WriteDebug("Auto Relogin: detached; waiting for the client to come back.");
        }

        private void OnReloginClientLoggedIn(int pid)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<int>(OnReloginClientLoggedIn), pid);
                return;
            }

            if (_isClosing || !_startupCompleted)
                return;

            // An explicit /pid= target is authoritative while it is alive: never attach to
            // a different client on its behalf (the monitor falls back to any live client
            // on its own once the pinned process is gone).
            int pinnedPid = ReloginManager.Instance.PinnedProcessId;
            if (pinnedPid != 0 && pid != pinnedPid && ReloginNative.IsProcessAlive(pinnedPid))
            {
                Logging.WriteDebug("Auto Relogin: ignoring client {0} - the pinned client {1} is still running.", pid, pinnedPid);
                return;
            }

            // Already attached to a live client. Either the event is just the monitor
            // noticing a healthy session, or the detachment triggered by ClientLost /
            // ClientDisconnected has not finished yet (ObjectManager is still initialized
            // for a moment). In the second case the login must not be dropped: park the
            // pid and let the detach task retry once the attachment is gone.
            if (ObjectManager.IsInitialized)
            {
                if (!_reloginInProgress)
                    return;

                Interlocked.Exchange(ref _pendingLoggedInPid, pid);
                Logging.WriteDebug("Auto Relogin: client {0} is in the world while detaching; will attach when the detach finishes.", pid);

                if (ObjectManager.IsInitialized)
                    return;

                // The detach finished between the check above and the store, so the detach
                // task has already drained the pending pid and nobody would pick it up.
                // Take it back and continue through the normal attach path below.
                pid = Interlocked.Exchange(ref _pendingLoggedInPid, 0);
                if (pid == 0)
                    return;
            }

            // One re-attach at a time. This also covers the case where the recovery
            // handshake is still in flight: the client is in the world, so attaching
            // now is exactly what we want.
            if (Interlocked.CompareExchange(ref _reattachRunning, 1, 0) != 0)
                return;

            Logging.Write(Colors.Green, "Auto Relogin: client {0} is in the world - re-attaching.", pid);
            _reattachTask = Task.Run(() => ReattachAfterRelogin(pid));
        }

        /// <summary>
        /// Re-attaches to the freshly logged in client and restarts the bot if it was
        /// running when the client went away. Runs on a worker thread.
        /// </summary>
        private void ReattachAfterRelogin(int pid)
        {
            bool requestAnother = false;

            try
            {
                CancellationToken token = _startupCts.Token;
                bool attached = false;

                for (int attempt = 1; attempt <= 6 && !token.IsCancellationRequested && !_isClosing; attempt++)
                {
                    if (attempt > 1)
                    {
                        Logging.WriteDebug("Auto Relogin: re-attach attempt {0}.", attempt);
                        Thread.Sleep(5000);
                    }

                    if (FindAndAttachToWoW(token, pid) && ObjectManager.IsInitialized)
                    {
                        attached = true;
                        break;
                    }
                }

                if (!attached)
                {
                    Logging.Write(Colors.Red, "Auto Relogin: could not re-attach to the client.");

                    // Ask the monitor for another ClientLoggedIn on a later tick: the
                    // client is in the world, so waiting for the character to leave and
                    // re-enter would never retry. Bounded, so an unattachable client
                    // (e.g. another CopilotBuddy owns it) does not retry forever.
                    // The request itself is raised in the finally below, once this
                    // sequence has released _reattachRunning - otherwise the event it
                    // triggers would be swallowed by the guard of the sequence that is
                    // still winding down.
                    requestAnother = ++_reattachFailures <= MaxReattachRetries
                                     && !_isClosing
                                     && !_startupCts.IsCancellationRequested;

                    if (!requestAnother)
                        Logging.Write(Colors.Red,
                            "Auto Relogin: giving up on re-attaching. Attach again manually (restart CopilotBuddy) once the problem is solved.");

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosing) return;
                        _reloginInProgress = false;

                        // No attachment exists, so Start/Stop stay disabled - the bot
                        // cannot be started without a client. Settings stay reachable.
                        btnSettings.IsEnabled = true;
                        ReplayParkedRecovery("the client went away during a relogin");
                        SetStatus(requestAnother
                            ? "Auto Relogin: waiting for the client"
                            : "Auto Relogin: re-attach failed");
                    }));
                    return;
                }

                _reattachFailures = 0;

                Logging.Write("Attached to WoW with ID {0}", _selectedWoWProc!.Value);

                // Same complete initialization the startup path performs, in the same order:
                // settings first (the plugin initialization reads CharacterSettings), then
                // the global block - which only runs once per process, so a plain reconnect
                // does not duplicate event subscriptions - then the combat routines.
                LoadSettings();
                bool globalInitRanNow = EnsureGlobalBotInitialization();

                // The combat routines were compiled against the previous attachment.
                try
                {
                    Styx.Logic.Combat.RoutineManager.Init();
                }
                catch (Exception ex)
                {
                    Logging.Write(Colors.Red, "Failed to initialize combat routines: {0}", ex.Message);
                    Logging.WriteException(ex);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosing) return;

                    // Populate the bot selector when the client only came up now (the
                    // startup path could not fill it because nothing was attached).
                    PopulateBotSelectorIfNeeded();

                    ToggleButtons(true);
                    _infoTimer?.Start();
                    SetStatus("CopilotBuddy Startup Complete");

                    // Release recovery ownership and consume anything parked while we had
                    // it, in the same UI operation: a loss queued before this callback can
                    // execute after it, and would otherwise stay parked with nobody left to
                    // replay it (the app would keep the stale attachment and ignore the
                    // replacement client's login).
                    _reloginInProgress = false;
                    bool lossReplayed = ReplayParkedRecovery("the client went away during a relogin");

                    // A replayed loss means the client went away again right after this
                    // attach; starting the bot now would only stop it again a moment later.
                    // The running decision is kept for the next successful attach.
                    bool restartBot = !lossReplayed
                                      && _wasRunningBeforeRelogin
                                      && ReloginSettings.Instance.RestartBotAfterRelogin;

                    if (!lossReplayed)
                        _wasRunningBeforeRelogin = false;

                    if (restartBot && BotManager.Current != null)
                    {
                        Logging.Write("Auto Relogin: restarting the bot.");
                        StartBot();
                    }
                    else if (restartBot)
                    {
                        Logging.Write(Colors.Red,
                            "Auto Relogin: the character is back but no botbase is selected - pick one and press Start.");
                    }
                    else if (globalInitRanNow || _pendingAutoStartConfig)
                    {
                        // The startup path never completed an attach, so its tail -
                        // command-line auto-load/auto-start - still has to run. A normal
                        // reconnect never gets here (restartBot covers that case).
                        if (lossReplayed)
                        {
                            // The client went away again before this attach could settle;
                            // applying the tail now would race with the replayed loss and
                            // would be attributed to a client that is already gone. Keep it
                            // pending for the next successful attach.
                            _pendingAutoStartConfig = true;
                            Logging.WriteDebug("Auto Relogin: the client went away again; the command-line auto-start configuration is deferred to the next attach.");
                        }
                        else
                        {
                            ApplyAutoStartConfig();
                            _pendingAutoStartConfig = false;
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "Auto Relogin: re-attach failed: {0}", ex.Message);
                Logging.WriteException(ex);
                _reloginInProgress = false;
            }
            finally
            {
                // Allow the next relogin to start a fresh attach sequence.
                Interlocked.Exchange(ref _reattachRunning, 0);

                // A login parked while this sequence ran is stale now: on success the
                // attachment already exists, on failure the re-signal below produces a
                // fresh ClientLoggedIn. Draining it keeps a later detach from dispatching
                // an attach for a pid that nobody is waiting for any more.
                Interlocked.Exchange(ref _pendingLoggedInPid, 0);

                // Only now ask for another ClientLoggedIn: the monitor answers on its
                // next tick, and that event has to find _reattachRunning released.
                if (requestAnother)
                {
                    try { ReloginManager.Instance.RequestLoggedInSignal(); }
                    catch (Exception ex) { Logging.WriteDebug("Auto Relogin: could not request another attempt: {0}", ex.Message); }
                }

                // Replay a loss that arrived while this worker owned the attachment. The
                // UI callbacks above were queued earlier, so _reloginInProgress is cleared
                // by the time this dispatch runs.
                ReplayParkedRecovery("the client went away again");
            }
        }

        /// <summary>
        /// One-time initialization that does not depend on a particular character: the
        /// TreeRoot hooks, the bot event subscriptions, the external botbases and the
        /// plugin containers. Runs exactly once per process - either right after the first
        /// successful attach at startup, or (when the client only came up later) from the
        /// Auto Relogin re-attach. Returns true when it ran on this call.
        /// </summary>
        private bool EnsureGlobalBotInitialization()
        {
            if (Interlocked.CompareExchange(ref _globalInitDone, 1, 0) != 0)
                return false;

            // HB 6.2.3 smethod_3: start the minimize guard thread + event handlers
            try
            {
                TreeRoot.Initialize();
                BotEvents.OnBotStarted += BotEvents_OnBotStarted;
                BotEvents.OnBotStopped += BotEvents_OnBotStopped;
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "Failed to initialize the behavior tree: {0}", ex.Message);
                Logging.WriteException(ex);
            }

            // Load additional bots from the Bots folder (external/custom bots)
            try
            {
                string botsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bots");
                if (Directory.Exists(botsPath))
                {
                    BotManager.Instance.LoadBots(botsPath);
                }
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex);
            }

            // Initialize plugins (compile and load from the Plugins folder)
            try
            {
                Logging.Write("Initializing Plugins...");

                // Pre-load System.Drawing.Common for plugin compilation
                try
                {
                    // Force load System.Drawing.Common into AppDomain
                    var _ = System.Drawing.Font.FromHdc(IntPtr.Zero);
                }
                catch { /* Ignore, just ensure assembly is loaded */ }

                // Load previously enabled plugins from CharacterSettings (null when the
                // settings for this character have not been initialized yet)
                var enabledPlugins = CharacterSettings.Instance?.EnabledPlugins ?? new string[0];
                Styx.Plugins.PluginManager.Initialize(enabledPlugins);
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "Failed to initialize plugins: {0}", ex.Message);
                Logging.WriteException(ex);
            }

            return true;
        }

        /// <summary>
        /// Fills the bot selector if the startup path could not do it (no client was
        /// attached when the window opened and Auto Relogin brought it up later).
        /// Must run on the UI thread.
        /// </summary>
        private void PopulateBotSelectorIfNeeded()
        {
            try
            {
                if (cmbBotSelector.Items.Count > 0)
                    return;

                cmbBotSelector.Items.Clear();
                foreach (var bot in BotManager.Instance.Bots)
                    cmbBotSelector.Items.Add(bot.Key);

                if (cmbBotSelector.Items.Count == 0)
                    return;

                int selectedIndex = CharacterSettings.Instance?.SelectedBotIndex ?? 0;
                if (selectedIndex < 0 || selectedIndex >= cmbBotSelector.Items.Count)
                {
                    selectedIndex = 0;
                    if (CharacterSettings.Instance != null)
                        CharacterSettings.Instance.SelectedBotIndex = 0;
                }

                cmbBotSelector.SelectedIndex = selectedIndex;
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("Auto Relogin: could not populate the bot selector: {0}", ex.Message);
            }
        }

        private void OnReloginStatus(string status)
        {
            Logging.WriteVerbose("[AutoRelogin] {0}", status);

            if (_isClosing)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                    SetStatus("Auto Relogin: " + status);
            }));
        }

        #endregion

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Re-entrancy guard: WaitForStartupToStop() pumps the dispatcher queue below, so
            // a queued Close()/SetStatus from the startup thread can re-enter this handler.
            if (_closingHandled)
                return;
            _closingHandled = true;

            // Cancel the Window_Loaded startup work first. An attach that has not reached
            // its last checkpoint aborts (and disposes its Memory) instead of publishing an
            // EndScene hook after teardown has begun.
            _isClosing = true;
            try { _startupCts.Cancel(); } catch { /* already disposed */ }

            // Auto Relogin must stop before the process teardown below: it holds no
            // handles itself, but it must not launch a new client while we are exiting.
            try
            {
                ReloginManager.Instance.Stop();
                ReloginSettings.Instance.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AutoRelogin] teardown failed: " + ex.Message);
            }

            if (_reloginWired)
            {
                try
                {
                    ReloginManager.Instance.ClientLost -= OnReloginClientLost;
                    ReloginManager.Instance.ClientDisconnected -= OnReloginClientDisconnected;
                    ReloginManager.Instance.ClientLoggedIn -= OnReloginClientLoggedIn;
                    ReloginManager.Instance.StatusChanged -= OnReloginStatus;
                }
                catch { /* ignore */ }
                _reloginWired = false;
            }

            // HB 6.2.3/Legion pattern: close all auxiliary windows first.
            foreach (Window window in System.Windows.Application.Current.Windows.Cast<Window>().ToList())
            {
                if (!ReferenceEquals(window, this))
                    window.Close();
            }

            // Stop bot and wait for worker shutdown before process teardown.
            if (TreeRoot.State != TreeRootState.Stopped)
            {
                TreeRoot.Stop("Main window is closing");
                if (!WaitForTreeRootStop(5000))
                    Logging.Write("Bot thread hung during window close");
            }

            // Disable all plugins so overlay/hotkeys and plugin-owned resources are released.
            // IsTearingDown keeps these cleanup-disables from wiping the saved EnabledPlugins list.
            Styx.Plugins.PluginManager.IsTearingDown = true;
            foreach (var pluginContainer in Styx.Plugins.PluginManager.Plugins)
            {
                pluginContainer.Enabled = false;
            }

            // Bounded wait for the startup task. It must complete BEFORE ObjectManager.Shutdown():
            // if the attach already got past its last checkpoint it publishes the executor, and
            // Shutdown has to run afterwards to restore EndScene deterministically.
            WaitForStartupToStop(5000);

            // An Auto Relogin detach or re-attach may be in flight (both attach/detach and
            // call Dispatcher.Invoke); they must finish before ObjectManager.Shutdown()
            // below. The detach task also checks _isClosing and returns without touching
            // the attachment once this handler has started.
            WaitForDetachToStop(5000);
            WaitForReattachToStop(5000);

            // The startup callback above was retired by the wait.  Re-check the worker:
            // a callback that slipped past the _isClosing guard just before it was set
            // could have started TreeRoot while the stop above was already running.
            if (TreeRoot.State != TreeRootState.Stopped)
            {
                Logging.Write("A worker was still running after startup shutdown; stopping it again.");
                TreeRoot.Stop("Main window is closing");
                if (!WaitForTreeRootStop(5000))
                    Logging.Write("Bot thread hung during the second stop attempt");
            }

            // Second plugin pass.  The startup task calls PluginManager.Initialize(...) after
            // attaching, so a startup that completed inside the wait above can have published
            // plugin containers after the first pass disabled them; those would keep running
            // their overlay/hotkeys through ObjectManager.Shutdown().  Best effort only: if
            // startup is still running past the timeout, the cancellation token and the
            // _isClosing guards are what stop it, not this loop.  Wrapped because the startup
            // thread may be replacing the collection while we enumerate it.
            try
            {
                Styx.Plugins.PluginManager.IsTearingDown = true;
                foreach (var pluginContainer in Styx.Plugins.PluginManager.Plugins)
                {
                    pluginContainer.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("Second plugin disable pass failed: {0}", ex.Message);
            }

            // Persist window geometry FIRST so that SaveSettings() below
            // already writes the correct coordinates to UISettings.xml.
            // Use RestoreBounds so we always capture the Normal-state rect
            // even when the window is currently maximized.
            try
            {
                // Always use RestoreBounds to get the Normal-state rect,
                // even if currently maximized — avoids saving the -8 maximized offset.
                Rect bounds = this.RestoreBounds;
                if (bounds.IsEmpty || double.IsNaN(bounds.X))
                    bounds = new Rect(this.Left, this.Top, this.Width, this.Height);

                if (!bounds.IsEmpty && !double.IsNaN(bounds.X) && bounds.X >= 0 && bounds.Y >= 0)
                {
                    UISettings.Instance.MainWindowLocationX = (int)bounds.X;
                    UISettings.Instance.MainWindowLocationY = (int)bounds.Y;
                    UISettings.Instance.MainWindowWidth     = (int)bounds.Width;
                    UISettings.Instance.MainWindowHeight    = (int)bounds.Height;
                }
                UISettings.Instance.MainWindowState = this.WindowState;
                UISettings.Instance.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UISettings] Failed to save window geometry: " + ex.Message);
            }

            // Save all other per-character settings (HB 4.3.4 pattern) BEFORE the teardown
            // below: SaveSettings() returns early once ObjectManager.Wow is null, so saving
            // afterwards would silently drop CharacterSettings/StyxSettings/LevelbotSettings.
            // UISettings is already flushed above with the correct geometry.
            SaveSettings();

            // Deterministic teardown: restore the EndScene hook and close the game's
            // process/thread handles here, on this thread.  Previously the only cleanup
            // lived in finalizers, i.e. it ran on the GC thread (often after the UI had
            // already gone away) and could freeze or desync the injected detour.
            try
            {
                ObjectManager.Shutdown();
            }
            catch (Exception ex)
            {
                Logging.Write("Shutdown cleanup failed: {0}", ex.Message);
            }

            // Release process mutex so another CopilotBuddy can claim this WoW
            try
            {
                _processMutex?.Dispose();
                _processMutex = null;
            }
            catch { /* Mutex may already be released */ }

            // Cleanup
            Logging.OnLogMessage -= OnLogMessage;
            BotEvents.OnBotStarted -= BotEvents_OnBotStarted;
            BotEvents.OnBotStopped -= BotEvents_OnBotStopped;
            _infoTimer?.Stop();
        }

        /// <summary>
        /// Waits (bounded) for the Window_Loaded startup task to finish, while continuing to
        /// pump the dispatcher queue: the startup thread calls Dispatcher.Invoke, so blocking
        /// this thread with Wait()/Join() would deadlock until the timeout expired.
        /// </summary>
        private void WaitForStartupToStop(int timeoutMs)
        {
            WaitForWorkerTask(_startupTask, timeoutMs, "Startup");
        }

        /// <summary>
        /// Same bounded, queue pumping wait for the Auto Relogin re-attach sequence: it also
        /// calls Dispatcher.Invoke (FindAndAttachToWoW) and must not publish an attachment
        /// after ObjectManager.Shutdown() has run.
        /// </summary>
        private void WaitForReattachToStop(int timeoutMs)
        {
            WaitForWorkerTask(_reattachTask, timeoutMs, "Auto Relogin re-attach");
        }

        /// <summary>Same wait for the Auto Relogin detach task.</summary>
        private void WaitForDetachToStop(int timeoutMs)
        {
            WaitForWorkerTask(_detachTask, timeoutMs, "Auto Relogin detach");
        }

        private void WaitForWorkerTask(Task? task, int timeoutMs, string what)
        {
            if (task == null || task.IsCompleted)
                return;

            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            {
                // DispatcherPriority.Background processes everything queued at Background and
                // above, which includes the worker thread's pending Dispatcher.Invoke calls.
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(10);
            }

            if (!task.IsCompleted)
                Logging.Write("{0} thread did not finish within {1} ms; continuing shutdown.", what, timeoutMs);
        }

        private static bool WaitForTreeRootStop(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (TreeRoot.State == TreeRootState.Stopped)
                    return true;

                Thread.Sleep(50);
            }

            return TreeRoot.State == TreeRootState.Stopped;
        }

        #endregion

        #region Logging

        private void OnLogMessage(ReadOnlyCollection<Logging.LogMessage> messages)
        {
            if (Thread.CurrentThread != Dispatcher.Thread)
            {
                // DispatcherPriority.Background ensures input events (clicks, keyboard)
                // at DispatcherPriority.Input are processed before log rendering.
                // Without this, heavy navigation debug logging starves the UI thread.
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Logging.LogMessageDelegate(OnLogMessage), messages);
                return;
            }

            // Clear if too many lines
            if (_logCount >= MaxLogLines)
            {
                _logParagraph.Inlines.Clear();
                _logCount = 0;
            }

            foreach (var message in messages)
            {
                // Filter by log level - only show messages at or below current LoggingLevel
                if (message.Level > Logging.LoggingLevel)
                    continue;

                // Create colored run
                var brush = new SolidColorBrush(message.Color);
                brush.Freeze();

                var run = new Run(message.ToString() + Environment.NewLine)
                {
                    Foreground = brush,
                    FontFamily = _logFont
                };

                _logParagraph.Inlines.Add(run);
                _logCount++;
            }

            // Auto-scroll to end
            scrollLog.ScrollToEnd();
        }

        #endregion

        #region Info Panel

        private void UpdateInfoPanel()
        {
            // HB 4.3.4 pattern: always display current values; no IsMeasuring gate.
            // Values start at 0 and accumulate once the bot starts.
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"XP/HR: {GameStats.XPPerHour:F0}");
            sb.AppendLine($"Kills: {GameStats.MobsKilled} ({GameStats.MobsPerHour:F0}/hr)");
            sb.AppendLine($"Deaths: {GameStats.Deaths} ({GameStats.DeathsPerHour:F0}/hr)");
            sb.AppendLine($"Loots: {GameStats.Loots} ({GameStats.LootsPerHour:F0}/hr)");
            sb.AppendLine($"Honor Gained: {GameStats.HonorGained} ({GameStats.HonorPerHour:F0}/hr)");
            sb.AppendLine($"BGs Won: {GameStats.BGsWon} Lost: {GameStats.BGsLost} Total: {GameStats.BGsCompleted} ({GameStats.BGsPerHour:F0}/hr)");

            // TTL only meaningful below max level (80 in WotLK 3.3.5a)
            var me = ObjectManager.Me;
            if (me != null && me.Level < 80 && GameStats.TimeToLevel > TimeSpan.Zero)
            {
                var ttl = GameStats.TimeToLevel;
                sb.AppendLine($"Time to Level: {(int)ttl.TotalHours} hour(s) {ttl.Minutes:00} minute(s) {ttl.Seconds:00} second(s)");
            }

            // Goal text — polled every second like HB 4.3.4
            var goalText = TreeRoot.GoalText;
            if (!string.IsNullOrEmpty(goalText))
            {
                sb.AppendLine();
                sb.AppendLine($"Goal: {goalText}");
            }

            tbInfoBlock.Text = sb.ToString();
            tbInfoBlock.TextWrapping = System.Windows.TextWrapping.Wrap;
        }

        #endregion

        #region UI Helpers

        private void ToggleButtons(bool enabled)
        {
            btnLoadProfile.IsEnabled = enabled;
            btnStart.IsEnabled = enabled;
            btnSettings.IsEnabled = enabled;
            cmbBotSelector.IsEnabled = enabled;
            btnBotConfig.IsEnabled = enabled;
            btnClassConfig.IsEnabled = enabled;
            btnDevTools.IsEnabled = enabled;
            btnPlugins.IsEnabled = enabled;
            EnhancedMode.IsEnabled = enabled;
        }

        private void SetStatus(string status)
        {
            lblActivityText.Content = status;
        }

        #endregion

        #region Bot Control

        /// <summary>
        /// AfterInitOnAuthSuccess equivalent for CopilotBuddy.
        /// Mirrors Honorbuddy 4.3.4 MainWindow.xaml.cs:419-437 (AfterInitOnAuthSuccess)
        /// and Start():847-866. Consumes command-line arguments forwarded by HBRelog
        /// (and any other external launcher) — loads the profile first, then selects
        /// the requested botbase, then auto-starts the bot.
        ///
        /// Command-line contract (see Styx/Helpers/CommandLine.cs):
        ///   /loadprofile=&lt;absolute path&gt;   — load this profile before starting
        ///   /botname=&lt;name fragment&gt;       — substring-match a BotBase key, case-insensitive
        ///   /autostart                       — start the bot without waiting for user click
        ///
        /// If none of these are present this method is a no-op and the user has to
        /// pick the botbase/profile/Start manually.
        /// </summary>
        private void ApplyAutoStartConfig()
        {
            try
            {
                string autoLoadProfile = CommandLine.AutoLoadProfile;
                string autoStartBot = CommandLine.AutoStartBotName;

                // Step 1 — load profile. Mirrors 4.3.4 AfterInitOnAuthSuccess:428-431.
                if (!string.IsNullOrEmpty(autoLoadProfile))
                {
                    Logging.Write("Auto-load profile from /loadprofile: {0}", autoLoadProfile);
                    try
                    {
                        ProfileManager.LoadNew(autoLoadProfile);
                        Logging.Write("Profile loaded: {0}", System.IO.Path.GetFileName(autoLoadProfile));
                    }
                    catch (Exception ex)
                    {
                        Logging.Write(Colors.Red, "Failed to auto-load profile '{0}': {1}", autoLoadProfile, ex.Message);
                        Logging.WriteException(ex);
                    }
                }

                // Step 2 — select botbase. Mirrors 4.3.4 MainWindow.xaml.cs:851-859 exactly:
                // substring match on bot.Name (which is the dictionary key). Honorbuddy "start
                // legion" method_18:819-836 uses strict OrdinalIgnoreCase equality on bot.Name.
                // We follow 4.3.4 since it's the version HBRelog was built for.
                //
                // IMPORTANT: bot.Name in CopilotBuddy is the *display* name (QuestBot class returns
                // "Questing", LevelBot returns "Grind", DiscoBot returns "Party Bot"). In HBRelog's
                // BotBase field you must type the bot.Name value, NOT the class name.
                // So /botname=Questing (not /botname=QuestBot) selects the questing bot.
                if (!string.IsNullOrEmpty(autoStartBot))
                {
                    string needle = autoStartBot.ToLowerInvariant();
                    var match = BotManager.Instance.Bots
                        .FirstOrDefault(b => b.Key != null && b.Key.ToLowerInvariant().Contains(needle));
                    if (match.Value != null)
                    {
                        Logging.Write("Auto-select botbase from /botname: {0}", match.Key);
                        BotManager.Instance.SetCurrent(match.Value);
                        // Sync the combobox so the UI matches what HBRelog requested; mirrors
                        // cmbBotSelector.SelectedItem = value at 4.3.4 Start():856.
                        if (cmbBotSelector.Items.Contains(match.Key))
                            cmbBotSelector.SelectedItem = match.Key;
                        else
                            CharacterSettings.Instance.SelectedBotIndex = 0;
                    }
                    else
                    {
                        Logging.Write(Colors.Red, "/botname='{0}' did not match any loaded bot base (available: {1})",
                            autoStartBot, string.Join(", ", BotManager.Instance.Bots.Keys));
                    }
                }

                // Step 3 — auto-start. Mirrors 4.3.4 AfterInitOnAuthSuccess:432-435.
                // We treat *any* auto-config (loadprofile or botname) as an implicit /autostart,
                // because HBRelog only forwards those two args together with /autostart.
                bool shouldAutoStart = !string.IsNullOrEmpty(autoLoadProfile) || !string.IsNullOrEmpty(autoStartBot);
                if (shouldAutoStart)
                    StartBot();
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "ApplyAutoStartConfig failed: {0}", ex.Message);
                Logging.WriteException(ex);
            }
        }

        private void StartBot()
        {
            // Never create a worker while the window is closing: teardown has already
            // stopped TreeRoot and is about to dispose the executor/hook, so anything
            // started here would be left running against a half-torn-down process.
            if (_isClosing)
            {
                Logging.WriteDebug("StartBot ignored: the application is closing.");
                return;
            }

            if (_isRunning) return;

            if (BotManager.Current == null)
            {
                Logging.Write(Colors.Red, "No bot selected!");
                return;
            }

            // Save settings before starting bot (HB 4.3.4 pattern)
            SaveSettings();

            _isRunning = true;
            btnStart.Visibility = Visibility.Hidden;
            btnStopGroup.Visibility = Visibility.Visible;
            btnLoadProfile.IsEnabled = false;
            cmbBotSelector.IsEnabled = false;
            btnSettings.IsEnabled = false;

            try
            {
                Logging.Write("Starting the bot.");
                Logging.Write("Currently Using BotBase : {0}", BotManager.Current.Name);
                if (ObjectManager.Me != null)
                {
                    Logging.Write("Character is a level {0} {1} {2}",
                        ObjectManager.Me.Level,
                        ObjectManager.Me.Race,
                        ObjectManager.Me.Class);
                    Task.Run(() =>
                    {
                        Logging.Write("Current zone is {0}", ObjectManager.Me.RealZoneText);
                    });
                }
                TreeRoot.Start();
                SetStatus("Running...");
            }
            catch (HonorbuddyUnableToStartException ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
            catch (UserException ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
        }

        private void StopBot()
        {
            if (!_isRunning) return;

            _isRunning = false;
            btnStart.Visibility = Visibility.Visible;
            btnStopGroup.Visibility = Visibility.Hidden;
            // Re-enable all controls that StartBot() disabled. Mirrors BotEvents_OnBotStopped
            // so the UI unlocks even when the worker thread never started (e.g. no profile loaded).
            btnLoadProfile.IsEnabled = true;
            cmbBotSelector.IsEnabled = true;
            btnSettings.IsEnabled = true;
            btnBotConfig.IsEnabled = true;
            btnClassConfig.IsEnabled = true;
            SetStatus("Stopping...");

            Logging.Write("Stopping the bot!");
            TreeRoot.Stop();
        }

        #endregion

        #region Button Click Handlers

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartBot();
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopBot();
        }

        private void BringBotToForeground(Hotkey _)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.Invoke(() => BringBotToForeground(_));
                return;
            }
            if (Visibility == Visibility.Hidden)
                Visibility = Visibility.Visible;
            Activate();
        }

        private void btnStopArrow_Click(object sender, RoutedEventArgs e)
        {
            if (btnStopArrow.ContextMenu != null)
            {
                btnStopArrow.ContextMenu.PlacementTarget = btnStopArrow;
                btnStopArrow.ContextMenu.IsOpen = true;
            }
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            var item = (System.Windows.Controls.MenuItem)sender;
            if (TreeRoot.IsPaused)
            {
                item.Header = "Pause";
                TreeRoot.Resume();
                SetStatus("Running...");
            }
            else
            {
                item.Header = "Resume";
                TreeRoot.Pause();
                SetStatus("Paused");
            }
        }

        private void BotEvents_OnBotStopped(EventArgs args)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<EventArgs>(BotEvents_OnBotStopped), args);
                return;
            }

            _isRunning = false;
            btnStart.Visibility = Visibility.Visible;
            btnStopGroup.Visibility = Visibility.Hidden;
            // Reset Pause MenuItem header back to "Pause" (HB 6.2.3 method_36 pattern).
            if (btnStopArrow.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem pauseItem)
                pauseItem.Header = "Pause";
            btnLoadProfile.IsEnabled = true;
            cmbBotSelector.IsEnabled = true;
            btnSettings.IsEnabled = true;
            SetStatus("CopilotBuddy Stopped");
        }

        private void BotEvents_OnBotStarted(EventArgs args)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<EventArgs>(BotEvents_OnBotStarted), args);
                return;
            }

            _isRunning = true;
            btnStart.Visibility = Visibility.Hidden;
            btnStopGroup.Visibility = Visibility.Visible;
            btnLoadProfile.IsEnabled = false;
            cmbBotSelector.IsEnabled = false;
            btnSettings.IsEnabled = false;
            SetStatus("Running...");
        }

        private void cmbBotSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbBotSelector.SelectedItem == null) return;

            string? botName = cmbBotSelector.SelectedItem.ToString();
            if (botName != null && BotManager.Instance.Bots.TryGetValue(botName, out BotBase? bot) && bot != null)
            {
                BotManager.Instance.SetCurrent(bot);
                
                // Show Bot Config button — always visible when a bot is selected.
                // The handler checks ConfigurationForm at click time.
                btnBotConfig.Visibility = Visibility.Visible;
                
                // Update selected bot index (no immediate save - HB 4.3.4 pattern)
                CharacterSettings.Instance.SelectedBotIndex = cmbBotSelector.SelectedIndex;
            }
        }

        private void btnLoadProfile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Profile files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Load Profile",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Default Profiles")
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    ProfileManager.LoadNew(openFileDialog.FileName);
                    Logging.Write($"Profile loaded: {Path.GetFileName(openFileDialog.FileName)}");
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    System.Windows.MessageBox.Show(
                        $"Failed to load profile!\n\nError: {ex.Message}",
                        "CopilotBuddy - Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void btnBotConfig_Click(object sender, RoutedEventArgs e)
        {
            if (BotManager.Current == null)
            {
                Logging.Write("No bot selected.");
                return;
            }

            var configWindow = BotManager.Current.ConfigurationWindow;
            if (configWindow != null)
            {
                configWindow.Owner = this;
                configWindow.ShowDialog();
                return;
            }

            var configForm = BotManager.Current.ConfigurationForm;
            if (configForm == null)
            {
                Logging.Write("[{0}] has no configuration window.", BotManager.Current.Name);
                return;
            }

            configForm.ShowDialog();
        }

        private void btnClassConfig_Click(object sender, RoutedEventArgs e)
        {
            // Open class/routine config
            var routine = Styx.Logic.Combat.RoutineManager.Current;
            if (routine != null && routine.WantButton)
            {
                try
                {
                    routine.OnButtonPress();
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    System.Windows.MessageBox.Show($"Error opening routine config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("No combat routine loaded or routine has no configuration.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void btnPlugins_Click(object sender, RoutedEventArgs e)
        {
            var pluginsWindow = new PluginsWindow
            {
                Owner = this
            };
            pluginsWindow.ShowDialog();
        }

        private void btnDevTools_Click(object sender, RoutedEventArgs e)
        {
            var devTools = new DeveloperToolsWindow();
            devTools.Owner = this;
            devTools.Show();
        }

        private void EnhancedMode_Checked(object sender, RoutedEventArgs e)
        {
            // Show all hidden buttons
            btnBotConfig.Visibility = Visibility.Visible;
            btnClassConfig.Visibility = Visibility.Visible;
            btnPlugins.Visibility = Visibility.Visible;
            btnDevTools.Visibility = Visibility.Visible;
            
            // Save setting
            UISettings.Instance.EnhancedMode = true;
            UISettings.Instance.Save();
        }

        private void EnhancedMode_Unchecked(object sender, RoutedEventArgs e)
        {
            // Hide optional buttons
            btnBotConfig.Visibility = Visibility.Hidden;
            btnClassConfig.Visibility = Visibility.Hidden;
            btnPlugins.Visibility = Visibility.Hidden;
            btnDevTools.Visibility = Visibility.Hidden;
            
            // Save setting
            UISettings.Instance.EnhancedMode = false;
            UISettings.Instance.Save();
        }

        #endregion

        #region Settings Management (HB 5.4.8 Pattern)

        /// <summary>
        /// Loads all settings after game attachment.
        /// Creates CharacterSettings instance (per-realm/per-character folder).
        /// Pattern from HB 5.4.8.
        /// </summary>
        private static void LoadSettings()
        {
            if (ObjectManager.Wow == null || !StyxWoW.IsInGame || StyxWoW.Me == null)
                return;

            try
            {
                // Create CharacterSettings instance for the current character
                // Pattern from HB 5.4.8 smethod_0() — called after game attachment
                CharacterSettings.Initialize();

                // Load other per-character settings
                UISettings.Instance.Load();
                StyxSettings.Instance.Load();
                LevelbotSettings.Instance.Load();
                
                Logging.WriteDebug("[Settings] Loaded settings for character: {0}", StyxWoW.Me?.Name ?? "Unknown");
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex);
            }
        }

        /// <summary>
        /// Saves all settings. Called at app close and before bot start.
        /// Pattern from HB 4.3.4.
        /// </summary>
        private static void SaveSettings()
        {
            try
            {
                if (ObjectManager.Wow == null || !ObjectManager.IsInGame)
                    return;

                UISettings.Instance.Save();
                CharacterSettings.Instance.Save();
                StyxSettings.Instance.Save();
                LevelbotSettings.Instance.Save();
                
                Logging.WriteDebug("[Settings] Saved all settings");
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[Settings] Error saving settings");
                Logging.WriteException(ex);
            }
        }

        #endregion
    }
}
