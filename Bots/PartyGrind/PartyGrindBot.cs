using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using Bots.Grind;
using CopilotBuddy.Bots.PartyGrind.Forms;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.CommonBot;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Combat;
using Styx.Logic.Common;
using Styx.Logic.Inventory.Frames.LootFrame;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

using Action = TreeSharp.Action;

namespace CopilotBuddy.Bots.PartyGrind
{
    /// <summary>
    /// Coordinated 3–5 client party grinding BotBase ("Party Grind").
    /// Synchronizes profiles, level-gating, phases, formation, tank/heal/DPS bridges,
    /// Mage/Warlock provisioning, TownRuns, and corpse recovery over TCP loopback NDJSON IPC.
    /// </summary>
    public sealed class PartyGrindBot : BotBase
    {
        private readonly SquadCoordinatorService _coordinator = new SquadCoordinatorService();
        private readonly SquadProvisioningService _provisioning = new SquadProvisioningService();
        private readonly SquadTownRunService _townRun = new SquadTownRunService();
        private readonly SquadDungeonService _dungeon = new SquadDungeonService();
        private readonly SquadPartySyncService _partySync;

        private SquadIpcServer? _ipcServer;
        private SquadIpcClient? _ipcClient;
        private Composite? _root;

        private long _lastIpcSendUtcMs;
        private long _lastIdleSnapshotUtcMs;
        private long _lastTownRallySearchUtcMs;
        private uint _cachedTownRallyMapId;
        private WoWPoint _cachedTownRallyPoint;
        private string _cachedProfilePath = string.Empty;
        private string _cachedProfileHash = string.Empty;
        private string _lastAttemptedLeaderProfilePath = string.Empty;
        private string _missingLeaderProfileHash = string.Empty;
        private long _nextLeaderProfileLookupUtcMs;
        private int _lastAppliedEffectiveLevel;
        private ulong _currentFocusGuid;
        private ulong _currentSecondaryGuid;
        private long _focusChangedUtcMs;
        private long _pullStartedUtcMs;
        private ulong _pullCommittedGuid;
        private long _pullCommittedUntilMs;
        private WoWPoint _latchedBacklinePos;
        private WoWPoint _latchedBacklineTankPos;
        private ulong _latchedBacklineTargetGuid;
        private long _latchedBacklineUntilMs;
        private long _reachedBacklineAtMs;
        private bool _wasMovingToBackline;

        private const float BacklineEnterDistance = 8.0f;
        private const float BacklineExitDistance = 3.0f;
        private const long BacklineLatchMs = 8000L;
        private const long BacklineReachDebounceMs = 500L;
        private const float BacklineLatchTankDrift = 10.0f;
        private const float BacklineCombatRangeRanged = 30.0f;
        private const float BacklineCombatRangeHealer = 35.0f;
        private const long PullCommitTimeoutMs = 10000L;
        private const float PullCommitMaxTargetDistance = 60.0f;
        private const float PullCandidateMaxDistance = 55.0f;
        private const long CombatFocusStickyMs = 2500L;
        private const long IdleSnapshotIntervalMs = 5000L;
        private const int TerminalStateDeliveryTimeoutMs = 1000;
        private bool _wasInCombat;
        private bool _townTasksCompleteLocal;
        private bool _townRunRequestPending;
        private long _townRunRequestAcknowledgedUtcMs;
        private string _townRunRequestReason = string.Empty;
        private long _townRunRequestUtcMs;
        private long _deathStartedUtcMs;
        private bool? _diedInsideDungeon;
        private bool _dungeonScriptTicked;
        private bool _localWasNeedingRest;
        private bool _lootFilterAttached;
        private bool _levelCapStopRequested;
        private SquadLeaderStateDelivery? _terminalStateDelivery;
        private long _terminalStateDeliveryDeadlineMs;
        private SquadTravelProgress _travelProgress = new();
        private static Composite? _activeCombatRoutine, _activeCombatBuffRoutine, _activePreCombatBuffRoutine, _activeRestRoutine;
        private static bool _combatTicked, _combatBuffTicked, _preCombatBuffTicked, _restTicked, _healTicked;

        private sealed class RoutineAwareRoot : PrioritySelector
        {
            private readonly PartyGrindBot _bot;

            public RoutineAwareRoot(PartyGrindBot bot, params Composite[] children) : base(children)
            {
                _bot = bot;
            }
            public override RunStatus Tick(object context)
            {
                if (IsRunning) { base.Stop(context); base.Start(context); }
                _combatTicked = _combatBuffTicked = _preCombatBuffTicked = _restTicked = _healTicked = false;
                _bot._dungeonScriptTicked = false;
                try { return base.Tick(context); }
                finally
                {
                    if (!_combatTicked) StopRoutine(ref _activeCombatRoutine);
                    if (!_combatBuffTicked) StopRoutine(ref _activeCombatBuffRoutine);
                    if (!_preCombatBuffTicked) StopRoutine(ref _activePreCombatBuffRoutine);
                    // Healing has its own ownership flag: a healer tick that is actively
                    // handling a target must not be mistaken for a finished combat branch.
                    if (!_healTicked) SquadHealBridge.StopActiveHealBehavior();
                    if (!_restTicked) StopRoutine(ref _activeRestRoutine);
                    if (!_bot._dungeonScriptTicked) _bot._dungeon.StopActiveEncounter();
                }
            }
        }

        private static void StopRoutine(ref Composite? active)
        {
            var routine = active;
            active = null;
            try { routine?.Stop(null); }
            catch (Exception ex) { Logging.WriteException(ex); }
        }

        private static RunStatus TickRoutine(ref Composite? active, Composite behavior)
        {
            if (!ReferenceEquals(active, behavior))
            {
                StopRoutine(ref active);
                active = behavior;
                active.Start(null);
            }
            var status = active.Tick(null);
            if (status != RunStatus.Running) StopRoutine(ref active);
            return status;
        }

        public PartyGrindBot()
        {
            _partySync = new SquadPartySyncService(_provisioning);
        }

        public override string Name => "Party Grind";

        public override bool RequiresProfile =>
            SquadSettings.Instance.BotMode != SquadBotMode.Dungeon &&
            (SquadSettings.Instance.IsCoordinator ||
             !SquadSettings.Instance.AutoLoadCoordinatorProfile ||
             ProfileManager.CurrentOuterProfile != null);

        public override PulseFlags PulseFlags => PulseFlags.All;

        public override Window ConfigurationWindow => new PartyGrindConfigWindow();

        public override Composite Root => _root ??= BuildRootBehavior();

        public override void Initialize()
        {
            SquadSettings.Initialize();
        }

        public override void Start()
        {
            SquadSettings settings = SquadSettings.Instance;
            settings.ApplyCliOverrides();
            _travelProgress = new SquadTravelProgress();
            SquadPartySyncService.ResetActionLog();

            _coordinator.Reset();
            _townRun.Reset();
            _lastIpcSendUtcMs = 0;
            _lastIdleSnapshotUtcMs = 0;
            _lastAppliedEffectiveLevel = StyxWoW.IsInGame ? StyxWoW.Me?.Level ?? 0 : 0;
            if (_lastAppliedEffectiveLevel is <= 0 or > byte.MaxValue)
                throw new HonorbuddyUnableToStartException("[PartyGrind Level] Cannot start until the local character is in world with a readable level.");
            _lastAttemptedLeaderProfilePath = string.Empty;
            _currentFocusGuid = 0;
            _currentSecondaryGuid = 0;
            _focusChangedUtcMs = 0;
            _pullStartedUtcMs = 0;
            ClearPullCommit();
            ResetBacklineState();
            _wasInCombat = false;
            _townTasksCompleteLocal = false;
            _townRunRequestPending = false;
            _townRunRequestAcknowledgedUtcMs = 0;
            _townRunRequestReason = string.Empty;
            _townRunRequestUtcMs = 0;
            _levelCapStopRequested = false;
            _terminalStateDelivery = null;
            _deathStartedUtcMs = 0;
            _diedInsideDungeon = null;
            _localWasNeedingRest = false;

            try
            {
                SquadContext.Reset();
                SquadContext.IsActive = true;
                SquadContext.ActiveSettings = settings;
                SquadContext.EffectiveLevelOverride = _lastAppliedEffectiveLevel;

                ProfileManager.LevelOverrideSelector = () => _lastAppliedEffectiveLevel;
                ProfileManager.KeepProfileAboveLevelRange = true;
                BotEvents.Profile.OnNewProfileLoaded += OnProfileRevisionLoaded;

                SquadTankBridge.AttachTargetingFilters();
                if (!_lootFilterAttached)
                {
                    LootTargeting.Instance.IncludeTargetsFilter += LevelBot.LevelbotIncludeLootsFilter;
                    _lootFilterAttached = true;
                }

                Mount.OnMountUp += SquadFormationService.HandleMountUpEvent;
                _partySync.AttachEvents();

                StartIpc(settings);
                if (!SquadContext.IsActive) return;
                _dungeon.InitializeRuntime(settings);
                if (ProfileManager.CurrentOuterProfile != null)
                    ProfileManager.RefreshProfileForLevel();
                SquadOverlay.Attach();

                Logging.Write("[PartyGrind] Started as {0} (Mode={1}, Coordinator={2}, Team={3}, Port={4}).",
                    settings.Role, settings.BotMode, settings.IsCoordinator, settings.TeamId, settings.IpcPort);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private static void Cleanup(System.Action action)
        {
            try { action(); }
            catch (Exception ex) { Logging.WriteException(ex); }
        }

        public override void Stop()
        {
            Cleanup(() => { BotEvents.Profile.OnNewProfileLoaded -= OnProfileRevisionLoaded; });
            Cleanup(() => { ProfileManager.LevelOverrideSelector = null; });
            Cleanup(() => { ProfileManager.KeepProfileAboveLevelRange = false; });
            Cleanup(() => { ProfileManager.RefreshProfileForLevel(); });
            Cleanup(() => { Mount.OnMountUp -= SquadFormationService.HandleMountUpEvent; });
            Cleanup(() => { SquadTankBridge.DetachTargetingFilters(); });

            if (_lootFilterAttached)
            {
                Cleanup(() => { LootTargeting.Instance.IncludeTargetsFilter -= LevelBot.LevelbotIncludeLootsFilter; });
                _lootFilterAttached = false;
            }

            Cleanup(() => { StopRoutine(ref _activeCombatRoutine); });
            Cleanup(() => { StopRoutine(ref _activeCombatBuffRoutine); });
            Cleanup(() => { StopRoutine(ref _activePreCombatBuffRoutine); });
            Cleanup(() => { StopRoutine(ref _activeRestRoutine); });
            Cleanup(() => { SquadHealBridge.StopActiveHealBehavior(); });
            Cleanup(() => { SquadPartyBuffService.ResetBuffCastThrottles(); });
            Cleanup(() => { _dungeon.ShutdownRuntime(); });
            Cleanup(() => { ClearPullCommit(); });
            Cleanup(() => { ResetBacklineState(); });
            Cleanup(() => { _provisioning.Stop(); });
            Cleanup(() => { _partySync.DetachEvents(); });
            Cleanup(() => { StopIpc(); });
            Cleanup(() => { SquadOverlay.Detach(); });
            Cleanup(() => { SquadContext.Reset(); });
            Cleanup(() => { _coordinator.Reset(); });
            Cleanup(() => { _townRun.Reset(); });
        }

        public override void OnPaused()
        {
            StopRoutine(ref _activeCombatRoutine);
            StopRoutine(ref _activeCombatBuffRoutine);
            StopRoutine(ref _activePreCombatBuffRoutine);
            StopRoutine(ref _activeRestRoutine);
            SquadHealBridge.StopActiveHealBehavior();
            ClearPullCommit();
            ResetBacklineState();
            _dungeon.StopActiveEncounter();
            _coordinator.IsPausedManually = true;
            _coordinator.ManualPauseReason = "Bot paused";
            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            if (settings.IsCoordinator && _ipcServer != null && StyxWoW.Me != null)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var localState = CaptureLocalMemberState(settings, now);
                var leaderState = _coordinator.EvaluateTick(
                    settings,
                    localState,
                    _ipcServer.GetMemberSnapshots(),
                    CaptureProfileSnapshot(now),
                    _currentFocusGuid,
                    _currentSecondaryGuid,
                    _pullStartedUtcMs,
                    StyxWoW.Me.Rotation,
                    now,
                    localTownRunRequest: GetLocalTownRunRequest());
                SquadContext.UpdateLeaderState(leaderState);
                _ipcServer.BroadcastLeaderState(leaderState, populateRoster: false);
            }
        }

        public override void OnResumed()
        {
            _coordinator.IsPausedManually = false;
            _coordinator.ManualPauseReason = string.Empty;
        }

        /// <summary>
        /// Announces to the squad that this character is heading to town. The coordinator
        /// converts the announcement into a synchronized TownRun, so every member can do
        /// its own sell/repair/buy/mail/train and then waits at the rally point together.
        /// The announcement is also raised automatically when local town needs appear.
        /// </summary>
        public void RequestTownRun(string reason)
        {
            // An explicit announcement is a deliberate group action, so it works even when
            // the automatic SyncTownRuns trigger is disabled.
            _townRunRequestPending = true;
            _townRunRequestAcknowledgedUtcMs = 0;
            _townRunRequestReason = string.IsNullOrWhiteSpace(reason) ? "Manual" : reason.Trim();
            // A repeated manual request must produce a fresh, strictly increasing
            // timestamp so the coordinator does not treat it as the previous one.
            _townRunRequestUtcMs = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _townRunRequestUtcMs + 1);
            Logging.Write("[PartyGrind Sync {0}] Town run requested: {1}",
                StyxWoW.Me?.Name ?? "?", _townRunRequestReason);
        }

        private SquadTownRunRequest? GetLocalTownRunRequest()
        {
            if (!_townRunRequestPending || _townRunRequestUtcMs <= 0)
                return null;
            var me = StyxWoW.Me;
            if (me == null || string.IsNullOrWhiteSpace(me.Name))
                return null;
            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            return new SquadTownRunRequest
            {
                CharacterName = me.Name,
                Guid = me.Guid,
                Role = settings.Role,
                Reason = _townRunRequestReason,
                RequestedUtcMs = _townRunRequestUtcMs
            };
        }

        private static string BuildTownRequestReason(TownNeedsEvaluation needs)
        {
            var reasons = new List<string>(5);
            if (needs.NeedsSell) reasons.Add("Sell");
            if (needs.NeedsRepair) reasons.Add("Repair");
            if (needs.NeedsBuy) reasons.Add("Buy");
            if (needs.NeedsTrain) reasons.Add("Train");
            if (needs.NeedsMail) reasons.Add("Mail");
            return string.Join(",", reasons);
        }

        public override void Pulse()
        {
            if (!SquadContext.IsActive || StyxWoW.Me == null)
                return;

            Navigator.PathPrecision = Math.Clamp(StyxWoW.Me.MovementInfo.CurrentSpeed * 0.15f, 1.5f, 10f);

            SquadSettings settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _dungeon.PulseEnvironment(settings, SquadContext.GetLeaderStateSnapshot(), now);

            SquadMemberState localState = CaptureLocalMemberState(settings, now);
            SquadContext.UpdateLocalMemberState(localState);

            if (settings.IsCoordinator)
            {
                PulseCoordinator(settings, localState, now);
            }
            else
            {
                PulseFollower(settings, localState, now);
            }

            EmitIdleSnapshot(now);
        }

        private void EmitIdleSnapshot(long now)
        {
            long lastLogUtcMs = Math.Max(SquadPartySyncService.LastActionUtcMs, _lastIdleSnapshotUtcMs);
            if (lastLogUtcMs != 0 && now - lastLogUtcMs < IdleSnapshotIntervalMs)
                return;
            var me = StyxWoW.Me;
            var name = me?.Name;
            if (me == null || string.IsNullOrWhiteSpace(name))
                return;
            var leader = SquadContext.GetLeaderStateSnapshot();
            var local = SquadContext.GetLocalMemberStateSnapshot();
            ulong focusGuid = leader is { FocusTargetGuid: not 0 } ? leader.FocusTargetGuid : _currentFocusGuid;
            string focusName = focusGuid != 0
                ? ObjectManager.GetObjectByGuid<WoWUnit>(focusGuid)?.Name ?? "?"
                : "-";
            long commitRemainingMs = _pullCommittedGuid != 0 ? Math.Max(0L, _pullCommittedUntilMs - now) : 0L;
            Logging.WriteDebug(
                "[PartyGrind Sync {0}] Idle@{1} phase={2} focus={3}:{4} commit={5}ms wait={6} dist={7:F0} prov='{8}' tgt='{9}' rest={10} casting={11} pos=({12:F0},{13:F0},{14:F0})",
                name,
                now,
                leader?.Phase.ToString() ?? "-",
                focusGuid,
                focusName,
                commitRemainingMs,
                _coordinator.IsWaitingForFollowerDebounced,
                _coordinator.MaxFollowerDistance,
                leader?.ActiveProvisionerName ?? string.Empty,
                leader?.ActiveTradeTargetName ?? string.Empty,
                local?.NeedsRest ?? false,
                me.IsCasting || me.ChanneledCastingSpellId != 0,
                me.Location.X, me.Location.Y, me.Location.Z);
            _lastIdleSnapshotUtcMs = now;
        }

        private static Dictionary<string, string> LoadMemberSecrets(SquadSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.IpcMemberSecretsPath))
                throw new InvalidOperationException("Configure IpcMemberSecretsPath with per-member random base64 secrets (at least 32 bytes each).");
            return new Dictionary<string, string>(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(settings.IpcMemberSecretsPath)) ?? throw new InvalidOperationException("Invalid member secrets file."), StringComparer.OrdinalIgnoreCase);
        }

        private static void ValidateIpcSecret(string secret)
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(secret);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("IPC secret for this character is not valid Base64.", ex);
            }
            if (decoded.Length < 32)
                throw new InvalidOperationException("IPC secret for this character must decode to at least 32 bytes.");
        }

        private void StartIpc(SquadSettings settings)
        {
            StopIpc();
            try
            {
                if (settings.IsCoordinator)
                {
                    var secrets = LoadMemberSecrets(settings);
                    var expected = new HashSet<string>(settings.GetExpectedMemberNames(), StringComparer.OrdinalIgnoreCase);
                    secrets = secrets.Where(kv => expected.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    _ipcServer = new SquadIpcServer(settings.IpcPort, settings.TeamId, memberSecrets: secrets);
                    _ipcServer.Start();
                    SquadContext.IsIpcConnected = true;
                }
                else
                {
                    var secrets = LoadMemberSecrets(settings);
                    if (!secrets.TryGetValue(StyxWoW.Me!.Name, out string? secret)) throw new InvalidOperationException("No IPC secret configured for this character.");
                    ValidateIpcSecret(secret);
                    _ipcClient = new SquadIpcClient(settings.IpcPort, settings.TeamId, memberSecret: secret);
                    _ipcClient.Start(BuildLocalHandshake);
                    SquadContext.IsIpcConnected = false;
                }
            }
            catch (Exception ex)
            {
                StopIpc();
                Stop();
                Logging.Write("[PartyGrind IPC] Startup failed: {0}", ex.Message);
                // TreeRoot.Start() handles HonorbuddyUnableToStartException by restoring
                // the stopped state and letting the UI recover; do not swallow the failure.
                throw new HonorbuddyUnableToStartException("[PartyGrind IPC] Startup failed: " + ex.Message, ex);
            }
        }

        private void OnProfileRevisionLoaded(BotEvents.Profile.NewProfileLoadedEventArgs args)
        {
            // A sub-profile revision can load without changing XmlLocation; record
            // the digest of the revision that is actually active.
            string path = ProfileManager.XmlLocation ?? string.Empty;
            if (string.IsNullOrEmpty(path))
                return;
            _cachedProfilePath = path;
            _cachedProfileHash = ComputeFileHash(path);
        }

        private void StopIpc()
        {
            AwaitTerminalStateDelivered();
            try { _ipcServer?.Dispose(); } catch { }
            _ipcServer = null;
            try { _ipcClient?.Dispose(); } catch { }
            _ipcClient = null;
        }

        /// <summary>
        /// Holds the IPC shutdown until the terminal leader state reached every session's
        /// socket. The wait is bounded: a peer that stopped reading must not stall the bot.
        /// </summary>
        private void AwaitTerminalStateDelivered()
        {
            SquadLeaderStateDelivery? delivery = _terminalStateDelivery;
            _terminalStateDelivery = null;
            if (delivery == null || delivery.IsDelivered)
                return;

            int remainingMs = (int)Math.Max(0, _terminalStateDeliveryDeadlineMs - Environment.TickCount64);
            if (!delivery.Wait(remainingMs))
                Logging.Write("[PartyGrind Level] Terminal leader state was still unsent after {0} ms; closing IPC anyway.",
                    TerminalStateDeliveryTimeoutMs);
        }

        private SquadHandshake BuildLocalHandshake()
        {
            var me = StyxWoW.Me;
            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            return new SquadHandshake
            {
                CharacterName = me?.Name ?? "Unknown",
                Guid = me?.Guid ?? 0UL,
                Class = me?.Class ?? WoWClass.None,
                Role = settings.Role,
                Level = StyxWoW.IsInGame ? me?.Level ?? 0 : 0
            };
        }

        private void PulseCoordinator(SquadSettings settings, SquadMemberState localState, long now)
        {
            var snapshots = _ipcServer?.GetMemberSnapshots() ?? new List<SquadConnectedMemberSnapshot>();
            _partySync.PulseCoordinatorPartySync(settings, snapshots);

            SquadProfileSnapshot profileSnap = CaptureProfileSnapshot(now);
            int hourlyResets = settings.BotMode == SquadBotMode.Dungeon ? _dungeon.GetHourlyResetCount(now) : 0;
            int lockoutRem = settings.BotMode == SquadBotMode.Dungeon ? _dungeon.GetWalkInEntryLockoutRemainingSeconds(now, settings.MaxInstancesPerHour) : 0;

            SquadLeaderState leaderState = _coordinator.EvaluateTick(
                settings,
                localState,
                snapshots,
                profileSnap,
                _currentFocusGuid,
                _currentSecondaryGuid,
                _pullStartedUtcMs,
                StyxWoW.Me?.Rotation ?? 0f,
                now,
                localTownRunRequest: GetLocalTownRunRequest(),
                resolveProfile: level =>
                {
                    if (_lastAppliedEffectiveLevel != level)
                    {
                        _lastAppliedEffectiveLevel = level;
                        if (ProfileManager.CurrentOuterProfile != null) ProfileManager.RefreshProfileForLevel();
                    }
                    return CaptureProfileSnapshot(now);
                },
                instanceResetGeneration: _dungeon.InstanceResetGeneration,
                hourlyInstancesRunCount: hourlyResets,
                lockoutRemainingSeconds: lockoutRem,
                resolveDungeon: level => _dungeon.CaptureSnapshot(settings, snapshots, level, now));

            UpdateCoordinatorTargets(now, leaderState);
            leaderState.FocusTargetGuid = _currentFocusGuid;
            leaderState.SecondaryTargetGuid = _currentSecondaryGuid;
            leaderState.PullStartedUtcMs = _pullStartedUtcMs;
            SquadContext.IsIpcConnected = _ipcServer?.IsListening == true;
            SquadContext.UpdateLeaderState(leaderState);

            if (leaderState.EffectiveSquadMinLevel > 0 && leaderState.EffectiveSquadMinLevel != _lastAppliedEffectiveLevel)
            {
                _lastAppliedEffectiveLevel = leaderState.EffectiveSquadMinLevel;
                if (ProfileManager.CurrentOuterProfile != null)
                    ProfileManager.RefreshProfileForLevel();
            }

            SquadPartyBuffService.UpdateRaidTargetMarkers(settings, leaderState);

            // The terminal state must reach the squad before the IPC server goes away, so the
            // first level-cap tick bypasses the throttle. Later ticks keep the normal cadence.
            bool terminalStatePending = leaderState.LevelCapReached && !_levelCapStopRequested &&
                _terminalStateDelivery == null;
            if (_ipcServer != null && (terminalStatePending || (now - _lastIpcSendUtcMs) >= 100))
            {
                SquadLeaderStateDelivery delivery = _ipcServer.BroadcastLeaderState(leaderState, populateRoster: false);
                _lastIpcSendUtcMs = now;
                if (terminalStatePending)
                {
                    _terminalStateDeliveryDeadlineMs = Environment.TickCount64 + TerminalStateDeliveryTimeoutMs;
                    _terminalStateDelivery = delivery;
                }
            }

            StopAtLevelCap(leaderState);
        }

        private void PulseFollower(SquadSettings settings, SquadMemberState localState, long now)
        {
            bool connected = _ipcClient != null && _ipcClient.IsConnected;
            if (SquadContext.IsIpcConnected != connected)
                Logging.WriteDebug("[PartyGrind IPC] Coordinator connection {0} member={1}", connected ? "restored" : "lost", localState.CharacterName);
            SquadContext.IsIpcConnected = connected;

            var leaderState = connected ? _ipcClient?.LatestLeaderState : null;
            if (leaderState == null) SquadContext.UpdateLeaderState(null);
            if (leaderState != null)
            {
                if (leaderState.EffectiveSquadMinLevel > 0 && leaderState.EffectiveSquadMinLevel != _lastAppliedEffectiveLevel)
                    Logging.WriteDebug("[PartyGrind Level] Received minimum {0} -> {1} from={2} lowest={3} subProfile={4}",
                        _lastAppliedEffectiveLevel, leaderState.EffectiveSquadMinLevel, leaderState.LeaderName, leaderState.LowestMemberName, leaderState.ActiveSubProfileName);
                if (leaderState.BotMode != SquadBotMode.Dungeon &&
                    settings.AutoLoadCoordinatorProfile &&
                    !EnsureLeaderProfile(leaderState))
                {
                    SquadContext.UpdateLeaderState(null);
                }
                else
                    SquadContext.UpdateLeaderState(leaderState);

                if (leaderState.EffectiveSquadMinLevel > 0 && leaderState.EffectiveSquadMinLevel != _lastAppliedEffectiveLevel)
                {
                    _lastAppliedEffectiveLevel = leaderState.EffectiveSquadMinLevel;
                    if (ProfileManager.CurrentOuterProfile != null)
                        ProfileManager.RefreshProfileForLevel();
                }

                StopAtLevelCap(leaderState);
            }

            if (_ipcClient != null && ((now - _lastIpcSendUtcMs) >= 120 || localState.IsInCombat != _wasInCombat))
            {
                _lastIpcSendUtcMs = now;
                _ipcClient.SendMemberState(localState);
            }
            _wasInCombat = localState.IsInCombat;
        }

        /// <summary>
        /// Ends the session once the lowest squad member passes the highest level range of the
        /// loaded profile. Every member keeps grinding until then, including a member (or the
        /// leader) that already outleveled the profile, so the squad never loses a client.
        /// </summary>
        private void StopAtLevelCap(SquadLeaderState leaderState)
        {
            if (!leaderState.LevelCapReached)
            {
                _levelCapStopRequested = false;
                _terminalStateDelivery = null;
                return;
            }

            SquadLeaderStateDelivery? delivery = _terminalStateDelivery;
            if (delivery != null && !delivery.IsDelivered && Environment.TickCount64 < _terminalStateDeliveryDeadlineMs)
                return;

            if (_levelCapStopRequested)
                return;

            _levelCapStopRequested = true;
            Logging.Write("[PartyGrind Level] Lowest member {0} (L{1}) passed the profile maximum L{2}. Stopping the squad.",
                leaderState.LowestMemberName, leaderState.EffectiveSquadMinLevel, leaderState.ProfileMaxLevel);
            TreeRoot.Stop("PartyGrind level cap reached");
        }

        private bool EnsureLeaderProfile(SquadLeaderState leader)
        {
            if (string.IsNullOrWhiteSpace(leader.ProfileXmlHash)) return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrEmpty(_missingLeaderProfileHash) &&
                string.Equals(_missingLeaderProfileHash, leader.ProfileXmlHash, StringComparison.OrdinalIgnoreCase) &&
                now < _nextLeaderProfileLookupUtcMs)
                return false; // cached miss; retry periodically for a newly installed profile

            string loadedHash = string.Empty;
            if (!string.IsNullOrEmpty(ProfileManager.XmlLocation))
            {
                // Compare against the digest of the loaded revision, not bytes that may
                // have changed on disk after the profile was loaded.
                loadedHash = string.Equals(ProfileManager.XmlLocation, _cachedProfilePath, StringComparison.OrdinalIgnoreCase)
                    ? _cachedProfileHash
                    : ComputeFileHash(ProfileManager.XmlLocation);
            }
            if (!string.IsNullOrEmpty(loadedHash) &&
                string.Equals(loadedHash, leader.ProfileXmlHash, StringComparison.OrdinalIgnoreCase))
            {
                _missingLeaderProfileHash = string.Empty;
                return true;
            }
            string root = Path.Combine(AppContext.BaseDirectory, "Profiles");
            var candidates = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.xml", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
            }) : Enumerable.Empty<string>();
            string? local = candidates.FirstOrDefault(p => string.Equals(ComputeFileHash(p), leader.ProfileXmlHash, StringComparison.OrdinalIgnoreCase));
            if (local == null)
            {
                if (_lastAttemptedLeaderProfilePath != leader.ProfileXmlPath)
                    Logging.Write("[PartyGrind] Missing trusted local profile: {0}. Place the matching XML under Profiles.", leader.ProfileXmlPath);
                _lastAttemptedLeaderProfilePath = leader.ProfileXmlPath;
                _missingLeaderProfileHash = leader.ProfileXmlHash;
                _nextLeaderProfileLookupUtcMs = now + 10000L;
                return false;
            }
            _missingLeaderProfileHash = string.Empty;
            try { ProfileManager.LoadNew(local, rememberMe: false); }
            catch (FileNotFoundException)
            {
                _missingLeaderProfileHash = leader.ProfileXmlHash;
                _nextLeaderProfileLookupUtcMs = now + 10000L;
                return false;
            }
            if (leader.EffectiveSquadMinLevel > 0) _lastAppliedEffectiveLevel = leader.EffectiveSquadMinLevel;
            ProfileManager.RefreshProfileForLevel();
            return true;
        }

        private void UpdateCoordinatorTargets(long now, SquadLeaderState decision)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return;

            if (_pullCommittedGuid != 0 && IsPullCommitReleased(now))
                ClearPullCommit();

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            bool bossPullBlocked = IsBossPullBlocked(settings, decision, me);
            bool anotherTankDrives = decision.BotMode == SquadBotMode.Dungeon &&
                decision.EffectiveTankGuid != 0 && decision.EffectiveTankGuid != me.Guid;
            if (bossPullBlocked || anotherTankDrives)
                ClearIdlePullCommit();

            if (anotherTankDrives)
            {
                var pugTank = ObjectManager.GetObjectByGuid<WoWPlayer>(decision.EffectiveTankGuid);
                if (pugTank != null && pugTank.IsValid && pugTank.Combat && pugTank.CurrentTarget != null &&
                    !pugTank.CurrentTarget.Dead && !pugTank.CurrentTarget.IsFriendly &&
                    (!bossPullBlocked || pugTank.CurrentTarget.Combat || pugTank.CurrentTarget.IsTargetingMyPartyMember))
                {
                    if (_currentFocusGuid != pugTank.CurrentTargetGuid)
                        _pullStartedUtcMs = now;
                    _currentFocusGuid = pugTank.CurrentTargetGuid;
                    _currentSecondaryGuid = 0UL;
                    return;
                }
            }

            WoWUnit? focus = me.CurrentTarget;
            if (focus == null || !focus.IsValid || focus.Dead || focus.IsFriendly)
            {
                focus = Targeting.Instance.FirstUnit;
            }

            if (focus != null && !focus.Combat && !focus.IsTargetingMyPartyMember &&
                !IsEligiblePullCandidateDistance(me.Location.Distance(focus.Location)))
            {
                focus = Targeting.Instance.TargetList.FirstOrDefault(u =>
                    u != null && u.IsValid && !u.Dead && u.Attackable && !u.IsFriendly &&
                    (u.Combat || u.IsTargetingMyPartyMember || IsEligiblePullCandidateDistance(me.Location.Distance(u.Location))));
            }

            ulong resolvedFocusGuid = (focus != null && focus.IsValid && !focus.Dead && !focus.IsFriendly) ? focus.Guid : 0UL;
            var local = SquadContext.GetLocalMemberStateSnapshot();
            bool anyFollowerStillInCombat = decision.Members?.Any(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
            bool localCannotPull = local == null || local.IsDead || local.IsGhost || local.IsInCombat ||
                                   local.NeedsRest || local.IsLooting || anyFollowerStillInCombat;
            bool commitActive = _pullCommittedGuid != 0 && now < _pullCommittedUntilMs;
            bool canInitiatePull = !bossPullBlocked && !anotherTankDrives &&
                SquadTankBridge.CanInitiatePull(decision, settings.MaxPullGatherDistance);
            bool focusIdle = focus != null && !focus.Combat && !focus.IsTargetingMyPartyMember;

            ulong newFocusGuid = ResolveCoordinatorFocusGuid(
                resolvedFocusGuid,
                focusIdle,
                localCannotPull,
                decision.Phase == SquadPhase.Grinding || decision.Phase == SquadPhase.DungeonClearing,
                _coordinator.IsWaitingForFollowerDebounced,
                canInitiatePull,
                _pullCommittedGuid,
                commitActive);

            if (newFocusGuid != 0 && _currentFocusGuid != 0 && newFocusGuid != _currentFocusGuid && !commitActive)
            {
                var prevUnit = ObjectManager.GetObjectByGuid<WoWUnit>(_currentFocusGuid);
                bool prevStillActiveInCombat = prevUnit != null && prevUnit.IsValid && !prevUnit.Dead &&
                                               !prevUnit.IsFriendly && prevUnit.Attackable &&
                                               (prevUnit.Combat || prevUnit.IsTargetingMyPartyMember || prevUnit.IsTargetingMeOrPet);
                bool newTargetsHealer = focus != null && focus.CurrentTargetGuid != 0 &&
                                        SquadContext.IsMemberRole(focus.CurrentTargetGuid, SquadRole.Healer);
                bool prevTargetsHealer = prevUnit != null && prevUnit.CurrentTargetGuid != 0 &&
                                         SquadContext.IsMemberRole(prevUnit.CurrentTargetGuid, SquadRole.Healer);
                if (ShouldRetainCombatFocus(
                        _currentFocusGuid,
                        newFocusGuid,
                        prevStillActiveInCombat,
                        newTargetsHealer,
                        prevTargetsHealer,
                        _focusChangedUtcMs,
                        now))
                {
                    newFocusGuid = _currentFocusGuid;
                }
            }

            if (newFocusGuid != 0 && newFocusGuid != _currentFocusGuid && !commitActive)
            {
                _pullStartedUtcMs = now;
                if (focus != null && focus.Guid == newFocusGuid && focusIdle &&
                    IsEligiblePullCandidateDistance(me.Location.Distance(focus.Location)))
                {
                    BeginPullCommit(newFocusGuid, now);
                }
            }
            else if (newFocusGuid == 0)
            {
                _pullStartedUtcMs = 0;
            }

            if (newFocusGuid != _currentFocusGuid)
            {
                Logging.WriteDebug("[PartyGrind Focus] {0} -> {1}", _currentFocusGuid, newFocusGuid);
                _focusChangedUtcMs = now;
                SquadPartySyncService.MarkActivity();
            }
            _currentFocusGuid = newFocusGuid;

            WoWUnit? secondary = Targeting.Instance.TargetList
                .FirstOrDefault(u => u != null && u.IsValid && !u.Dead && u.Guid != _currentFocusGuid && (u.Combat || u.IsTargetingMyPartyMember));
            _currentSecondaryGuid = secondary?.Guid ?? 0UL;
        }

        private static bool IsBossPullBlocked(SquadSettings settings, SquadLeaderState? leader, LocalPlayer me)
        {
            if (leader?.BotMode != SquadBotMode.Dungeon || !SquadDungeonService.IsInDungeonMap(me))
                return false;

            var boss = global::Bots.DungeonBuddy.BossManager.CurrentBoss?.ToWoWUnit();
            if (boss is not { IsValid: true, IsAlive: true })
                return false;

            var pullPosition = me.Location;
            if (leader.EffectiveTankGuid != 0 && leader.EffectiveTankGuid != me.Guid &&
                ObjectManager.GetObjectByGuid<WoWPlayer>(leader.EffectiveTankGuid) is { IsValid: true, IsAlive: true } tank)
                pullPosition = tank.Location;

            return SquadDungeonService.IsBossPrePullBarrierBlocking(settings, leader, pullPosition.Distance(boss.Location));
        }

        private void ClearIdlePullCommit()
        {
            if (_pullCommittedGuid == 0)
                return;

            var committed = ObjectManager.GetObjectByGuid<WoWUnit>(_pullCommittedGuid);
            if (committed == null || !committed.IsValid || (!committed.Combat && !committed.IsTargetingMyPartyMember))
                ClearPullCommit();
        }

        internal static bool IsEligiblePullCandidateDistance(double distance)
        {
            return distance <= PullCandidateMaxDistance;
        }

        internal static bool ShouldRetainCombatFocus(
            ulong currentFocusGuid,
            ulong candidateFocusGuid,
            bool currentFocusActiveInCombat,
            bool candidateTargetsHealer,
            bool currentTargetsHealer,
            long focusChangedUtcMs,
            long nowUtcMs)
        {
            if (currentFocusGuid == 0 || candidateFocusGuid == 0 || currentFocusGuid == candidateFocusGuid)
                return false;
            if (!currentFocusActiveInCombat)
                return false;
            if (candidateTargetsHealer && !currentTargetsHealer)
                return false;
            return focusChangedUtcMs > 0 && (nowUtcMs - focusChangedUtcMs) < CombatFocusStickyMs;
        }

        private void BeginPullCommit(ulong targetGuid, long nowUtcMs)
        {
            _pullCommittedGuid = targetGuid;
            _pullCommittedUntilMs = nowUtcMs + PullCommitTimeoutMs;
            _pullStartedUtcMs = nowUtcMs;
            SquadContext.ActivePullCommitGuid = targetGuid;
            Logging.WriteDebug("[PartyGrind Commit] Started {0} ({1}s)", targetGuid, PullCommitTimeoutMs / 1000L);
            SquadPartySyncService.MarkActivity();
        }

        private void ClearPullCommit()
        {
            bool wasActive = _pullCommittedGuid != 0;
            _pullCommittedGuid = 0;
            _pullCommittedUntilMs = 0;
            SquadContext.ActivePullCommitGuid = 0;
            if (wasActive)
            {
                Logging.WriteDebug("[PartyGrind Commit] Released");
                SquadPartySyncService.MarkActivity();
            }
        }

        private void ResetBacklineState()
        {
            _latchedBacklinePos = WoWPoint.Zero;
            _latchedBacklineTankPos = WoWPoint.Zero;
            _latchedBacklineTargetGuid = 0;
            _latchedBacklineUntilMs = 0;
            _reachedBacklineAtMs = 0;
            _wasMovingToBackline = false;
        }

        private bool IsPullCommitReleased(long nowUtcMs)
        {
            if (_pullCommittedGuid == 0)
                return true;

            var unit = ObjectManager.GetObjectByGuid<WoWUnit>(_pullCommittedGuid);
            var me = StyxWoW.Me;
            bool resolvable = unit != null && unit.IsValid;
            bool deadOrFriendly = !resolvable || unit!.Dead || unit.IsFriendly;
            float tankToTarget = resolvable && me != null ? me.Location.Distance(unit!.Location) : 0f;
            bool pullEngaged = resolvable && !deadOrFriendly && me != null && me.Combat && unit!.Combat &&
                               me.CurrentTargetGuid == _pullCommittedGuid;
            bool thirdPartyEngaged = resolvable && !deadOrFriendly && unit!.HealthPercent < 100f &&
                                     unit.IsTargetingMyPartyMember;
            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            bool followerTooFar = _coordinator.IsHardBacktracking &&
                                  _coordinator.MaxFollowerDistance > settings.HardBacktrackDistance + 10f;

            return EvaluatePullCommitRelease(resolvable, deadOrFriendly, tankToTarget,
                pullEngaged, thirdPartyEngaged, followerTooFar, nowUtcMs, _pullCommittedUntilMs);
        }

        internal static bool EvaluatePullCommitRelease(
            bool targetResolvable,
            bool targetDeadOrFriendly,
            float tankToTargetDistance,
            bool pullEngaged,
            bool thirdPartyEngaged,
            bool followerTooFar,
            long nowUtcMs,
            long committedUntilUtcMs)
        {
            if (!targetResolvable || targetDeadOrFriendly)
                return true;
            if (nowUtcMs >= committedUntilUtcMs)
                return true;
            if (tankToTargetDistance > PullCommitMaxTargetDistance)
                return true;
            if (pullEngaged || thirdPartyEngaged || followerTooFar)
                return true;
            return false;
        }

        internal static ulong ResolveCoordinatorFocusGuid(
            ulong resolvedFocusGuid,
            bool focusIdle,
            bool localCannotPull,
            bool phaseGrinding,
            bool waitingForFollower,
            bool canInitiatePull,
            ulong committedGuid,
            bool commitActive)
        {
            if (commitActive && committedGuid != 0)
            {
                bool hostileThreatTakesPrecedence = resolvedFocusGuid != 0 &&
                                                    resolvedFocusGuid != committedGuid &&
                                                    !focusIdle;
                if (!hostileThreatTakesPrecedence)
                    return committedGuid;
            }
            if (resolvedFocusGuid != 0 && focusIdle &&
                (localCannotPull || !phaseGrinding || waitingForFollower || !canInitiatePull))
                return 0UL;
            return resolvedFocusGuid;
        }

        private SquadProfileSnapshot CaptureProfileSnapshot(long now)
        {
            var outer = ProfileManager.CurrentOuterProfile;
            var current = outer != null ? ProfileManager.CurrentProfile : null;
            string xmlPath = ProfileManager.XmlLocation ?? string.Empty;

            if (!string.IsNullOrEmpty(xmlPath) && !string.Equals(xmlPath, _cachedProfilePath, StringComparison.OrdinalIgnoreCase))
            {
                _cachedProfilePath = xmlPath;
                _cachedProfileHash = ComputeFileHash(xmlPath);
            }

            var grindArea = current?.GrindArea ?? StyxWoW.AreaManager?.CurrentGrindArea;
            WoWPoint hotspot = (grindArea != null && grindArea.Hotspots.Count > 0)
                ? grindArea.CurrentHotSpot.Position
                : (StyxWoW.Me?.Location ?? WoWPoint.Zero);

            if ((now - _lastTownRallySearchUtcMs) > 10000L || _cachedTownRallyPoint == WoWPoint.Zero)
            {
                _lastTownRallySearchUtcMs = now;
                (_cachedTownRallyMapId, _cachedTownRallyPoint) = SquadTownRunService.ResolveTownRallyPoint();
            }

            WoWPoint safeRes = StyxWoW.Me != null && (StyxWoW.Me.Dead || StyxWoW.Me.IsGhost)
                ? StyxWoW.Me.CorpsePoint
                : (StyxWoW.Me?.Location ?? WoWPoint.Zero);

            return new SquadProfileSnapshot
            {
                HasProfile = current != null || outer != null,
                ProfileXmlPath = xmlPath,
                ProfileXmlHash = _cachedProfileHash,
                SubProfileName = current?.Name ?? outer?.Name ?? string.Empty,
                SubProfileMinLevel = current?.MinLevel ?? 1,
                SubProfileMaxLevel = current?.MaxLevel ?? 85,
                ProfileMaxLevel = ProfileManager.TopProfileMaxLevel,
                MapId = StyxWoW.Me?.MapId ?? 0,
                HotspotX = hotspot.X,
                HotspotY = hotspot.Y,
                HotspotZ = hotspot.Z,
                TownRallyMapId = _cachedTownRallyMapId,
                TownRallyX = _cachedTownRallyPoint.X,
                TownRallyY = _cachedTownRallyPoint.Y,
                TownRallyZ = _cachedTownRallyPoint.Z,
                SafeCorpseRallyX = safeRes.X,
                SafeCorpseRallyY = safeRes.Y,
                SafeCorpseRallyZ = safeRes.Z
            };
        }

        private SquadMemberState CaptureLocalMemberState(SquadSettings settings, long now)
        {
            var me = StyxWoW.Me!;
            var leader = SquadContext.GetLeaderStateSnapshot();

            bool isDead = me.Dead && !me.IsGhost;
            bool isGhost = me.IsGhost;
            bool insideDungeon = SquadDungeonService.IsInDungeonMap(me);
            if (isDead)
                _diedInsideDungeon ??= insideDungeon;
            else if (!isGhost)
            {
                _deathStartedUtcMs = 0;
                _diedInsideDungeon = null;
            }
            bool usesMana = me.PowerType == WoWPowerType.Mana;
            // Druids need water even while shapeshifted into a non-mana form; use the
            // same class-aware predicate as the town-run need evaluation for requests.
            bool usesManaForDrinks = usesMana || me.Class == WoWClass.Druid;

            _localWasNeedingRest = !me.Combat && !isDead && !isGhost &&
                SquadCoordinatorService.EvaluateRestHysteresis(
                    _localWasNeedingRest,
                    (float)me.HealthPercent,
                    (float)me.PowerPercent,
                    usesMana,
                    settings);

            ulong aggroGuid = 0UL;
            bool needsPeel = false;
            if (settings.Role != SquadRole.TankLeader && me.Combat)
            {
                var attacker = ObjectManager.CachedUnits.FirstOrDefault(u =>
                    u != null && u.IsValid && !u.Dead && u.Combat && u.Attackable && !u.IsFriendly &&
                    u.CurrentTargetGuid == me.Guid);
                if (attacker != null)
                {
                    aggroGuid = attacker.Guid;
                    needsPeel = true;
                }
            }

            int foodCount = SquadProvisioningService.CountFoodInBags();
            int drinkCount = SquadProvisioningService.CountDrinksInBags();
            bool hasHs = SquadProvisioningService.HasHealthstoneInBags();
            int? healthstoneMinReceiverLevel = me.Class == WoWClass.Warlock && settings.EnableWarlockHealthstones
                ? SquadProvisioningService.GetHealthstoneMinReceiverLevel(me)
                : null;
            bool squadHasWarlock = me.Class == WoWClass.Warlock ||
                (leader?.Members?.Any(m => m.IsConnected && m.Class == WoWClass.Warlock) ?? false);

            var provNeeds = SquadProvisioningService.EvaluateConsumableNeeds(
                me.Combat,
                (int)me.FreeNormalBagSlots,
                foodCount,
                drinkCount,
                usesManaForDrinks,
                hasHs,
                squadHasWarlock,
                settings);

            _townRun.ObservePhase(leader?.Phase, leader?.TownRunInterrupted == true);
            _townRun.RecheckBlocked(settings, leader);
            var townNeeds = SquadTownRunService.EvaluateLiveLocalNeeds(settings, leader);
            if (leader == null || leader.Phase != SquadPhase.TownRun ||
                townNeeds.NeedsSell || townNeeds.NeedsRepair || townNeeds.NeedsBuy || townNeeds.NeedsTrain || townNeeds.NeedsMail)
                _townTasksCompleteLocal = false;

            // Raise the "idę do miasta" announcement on the periodic telemetry. It is
            // repeated until the local needs are resolved, so a lost IPC frame can never
            // swallow the signal; the coordinator acknowledges it by timestamp.
            if (_townRunRequestPending && leader?.TownRunRequestAcknowledgements != null)
            {
                foreach (var acknowledgement in leader.TownRunRequestAcknowledgements)
                {
                    if (string.Equals(acknowledgement.Key, me.Name, StringComparison.OrdinalIgnoreCase))
                        _townRunRequestAcknowledgedUtcMs = Math.Max(
                            _townRunRequestAcknowledgedUtcMs, acknowledgement.Value);
                }
            }

            if (settings.SyncTownRuns && townNeeds.Any)
            {
                if (!_townRunRequestPending)
                {
                    _townRunRequestPending = true;
                    _townRunRequestAcknowledgedUtcMs = 0;
                    _townRunRequestReason = BuildTownRequestReason(townNeeds);
                    _townRunRequestUtcMs = Math.Max(now, _townRunRequestUtcMs + 1);
                    Logging.Write("[PartyGrind Sync {0}] Announcing town run: {1}", me.Name, _townRunRequestReason);
                }
            }
            else if (_townRunRequestPending)
            {
                if (_townRunRequestUtcMs > 0 &&
                    _townRunRequestAcknowledgedUtcMs >= _townRunRequestUtcMs &&
                    leader != null && leader.AllTownTasksComplete &&
                    leader.Phase != SquadPhase.TownRun)
                {
                    _townRunRequestPending = false;
                    _townRunRequestAcknowledgedUtcMs = 0;
                }
            }

            string xmlPath = ProfileManager.XmlLocation ?? string.Empty;
            if (!string.IsNullOrEmpty(xmlPath) && !string.Equals(xmlPath, _cachedProfilePath, StringComparison.OrdinalIgnoreCase))
            {
                _cachedProfilePath = xmlPath;
                _cachedProfileHash = ComputeFileHash(xmlPath);
            }

            var currentSub = ProfileManager.CurrentOuterProfile != null ? ProfileManager.CurrentProfile : null;
            bool isResting = !me.Combat && (me.HasAura("Food") || me.HasAura("Drink") || _localWasNeedingRest);
            bool isLooting = me.FreeNormalBagSlots > 0 && !me.Combat && !isDead && !isGhost &&
                (LootFrame.Instance.IsVisible || FindEligibleLoot() != null);

            return new SquadMemberState
            {
                CharacterName = me.Name ?? "Unknown",
                Guid = me.Guid,
                Role = settings.Role,
                Class = me.Class,
                Level = StyxWoW.IsInGame ? me.Level : 0,
                CurrentXp = me.XP,
                NextLevelXp = me.NextLevelXP,
                XpPerHour = GameStats.XPPerHour,
                MapId = me.MapId,
                ZoneId = me.ZoneId,
                X = me.Location.X,
                Y = me.Location.Y,
                Z = me.Location.Z,
                HealthPercent = (float)me.HealthPercent,
                PowerPercent = (float)me.PowerPercent,
                PowerType = me.PowerType,
                IsDead = isDead,
                IsGhost = isGhost,
                CorpseMapId = me.MapId,
                CorpseX = me.CorpsePoint.X,
                CorpseY = me.CorpsePoint.Y,
                CorpseZ = me.CorpsePoint.Z,
                IsInCombat = me.Combat,
                AggroAttackerGuid = aggroGuid,
                NeedsPeel = needsPeel,
                NeedsRest = _localWasNeedingRest,
                IsResting = isResting,
                TownBlockedReason = _townRun.BlockedReason,
                IsLooting = isLooting,
                KnownBlessings = new[] { "Blessing of Sanctuary", "Blessing of Kings", "Blessing of Might", "Blessing of Wisdom" }.Where(SpellManager.HasSpell).ToList(),
                CanResurrect = SquadHealBridge.GetResurrectionSpell(me.Class) is string resSpell && SpellManager.HasSpell(resSpell),
                CanMount = me.Level >= 20,
                CanFly = me.Level >= 60,
                MaxMountSpeed = me.Level >= 40 ? 100f : (me.Level >= 20 ? 60f : 0f),
                IsMounted = me.Mounted,
                FoodCount = foodCount,
                DrinkCount = drinkCount,
                HasHealthstone = hasHs,
                WantsFood = provNeeds.WantsFood,
                WantsDrink = provNeeds.WantsDrink,
                WantsHealthstone = provNeeds.WantsHealthstone,
                IsProvisioningBusy = _provisioning.IsTradeBusy,
                CanProvideMageFood = me.Class == WoWClass.Mage && settings.EnableMageProvisioning && SpellManager.HasSpell("Conjure Food"),
                CanProvideMageDrink = me.Class == WoWClass.Mage && settings.EnableMageProvisioning && SpellManager.HasSpell("Conjure Water"),
                CanProvideHealthstone = healthstoneMinReceiverLevel.HasValue,
                HealthstoneMinReceiverLevel = healthstoneMinReceiverLevel,
                IsHealthstoneRitualPending = _provisioning.IsSoulwellPending(now),
                NeedsSell = townNeeds.NeedsSell,
                NeedsRepair = townNeeds.NeedsRepair,
                NeedsBuy = townNeeds.NeedsBuy,
                NeedsTrain = townNeeds.NeedsTrain,
                NeedsMail = townNeeds.NeedsMail,
                TownTasksComplete = _townTasksCompleteLocal,
                FreeBagSlots = (int)me.FreeNormalBagSlots,
                LowestDurabilityPct = (float)me.LowestDurabilityPercent,
                TownRunRequested = _townRunRequestPending,
                TownRunRequestReason = _townRunRequestReason,
                TownRunRequestedUtcMs = _townRunRequestUtcMs,
                LoadedProfileHash = _cachedProfileHash,
                ActiveSubProfileMinLevel = currentSub?.MinLevel ?? 1,
                ActiveSubProfileMaxLevel = currentSub?.MaxLevel ?? 85,
                IsInsideDungeon = insideDungeon,
                HasDungeonTelemetry = true,
                CurrentLfgDungeonId = SquadContext.IsDungeonModeActive ? global::Bots.DungeonBuddy.LfgManager.CurrentLfgDungeonId : 0U,
                LfgMode = SquadContext.IsDungeonModeActive ? SquadDungeonService.SafeGetLfgMode() : string.Empty,
                LfgRoleCheckPending = SquadContext.IsDungeonModeActive && global::Bots.DungeonBuddy.LfgManager.RoleCheckPending,
                LfgProposalPending = SquadContext.IsDungeonModeActive && global::Bots.DungeonBuddy.LfgManager.ProposalPending,
                HasDungeonDeserter = SquadContext.IsDungeonModeActive && SquadDungeonService.HasDeserterOrRandomCooldown(),
                ObservedResetGeneration = !insideDungeon
                    ? (leader?.InstanceResetGeneration ?? _dungeon.InstanceResetGeneration)
                    : (SquadContext.GetLocalMemberStateSnapshot()?.ObservedResetGeneration ?? 0)
            };
        }

        private Composite BuildRootBehavior()
        {
            return new RoutineAwareRoot(this,
                // 1. Paused or follower disconnected safety pause
                new Decorator(
                    ctx => ShouldPauseMovement(),
                    new Action(ctx =>
                    {
                        SquadPartySyncService.LogAction("Waiting: squad paused or disconnected");
                        if (StyxWoW.Me?.IsMoving == true)
                            WoWMovement.MoveStop();
                        return RunStatus.Success;
                    })
                ),

                // 2. Death / WaitingResurrection / CorpseRun
                new Decorator(
                    ctx => StyxWoW.Me != null && (StyxWoW.Me.Dead || StyxWoW.Me.IsGhost),
                    new Action(ctx => RunDeathAndCorpseBehavior())
                ),

                // 3. Synchronized TownRun
                new Decorator(
                    ctx => SquadContext.GetLeaderStateSnapshot()?.Phase == SquadPhase.TownRun &&
                           SquadContext.GetLocalMemberStateSnapshot()?.IsInCombat != true,
                    new Action(ctx =>
                    {
                        var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
                        var leader = SquadContext.GetLeaderStateSnapshot();
                        if (_townRun.RunTownTick(settings, leader, out bool complete))
                        {
                            _townTasksCompleteLocal = complete;
                            SquadPartySyncService.LogAction(complete ? "Town tasks complete" : "Performing town tasks");
                            return RunStatus.Success;
                        }
                        _townTasksCompleteLocal = false;
                        return RunStatus.Failure;
                    })
                ),

                new Decorator(
                    ctx => SquadContext.IsDungeonModeActive && _dungeon.TryRunAvoidanceStep(),
                    new Action(ctx => RunStatus.Success)
                ),

                new Decorator(
                    ctx => SquadContext.IsDungeonModeActive &&
                           (SquadContext.GetLeaderStateSnapshot()?.Phase == SquadPhase.DungeonCompleteExit ||
                            SquadContext.GetLeaderStateSnapshot()?.TeleportOutOrder == true) &&
                           StyxWoW.Me?.Combat != true,
                    new Action(ctx => _dungeon.RunCompleteExitTick(
                        SquadContext.ActiveSettings ?? SquadSettings.Instance,
                        SquadContext.GetLeaderStateSnapshot(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        ? RunStatus.Success
                        : RunStatus.Failure)
                ),

                new Decorator(
                    ctx => SquadContext.IsDungeonModeActive &&
                           !SquadDungeonService.IsInDungeonMap(StyxWoW.Me) &&
                           StyxWoW.Me?.Combat != true &&
                           (SquadContext.GetLeaderStateSnapshot() == null ||
                            SquadContext.GetLeaderStateSnapshot()!.Phase == SquadPhase.DungeonQueueing ||
                            SquadContext.GetLeaderStateSnapshot()!.Phase == SquadPhase.DungeonTravelToPortal ||
                            SquadContext.GetLeaderStateSnapshot()!.Phase == SquadPhase.DungeonLockoutWait ||
                            (SquadContext.GetLeaderStateSnapshot()!.Phase == SquadPhase.DungeonClearing &&
                             (SquadContext.GetLeaderStateSnapshot()!.DungeonEntryMode != SquadDungeonEntryMode.WalkInPortalFarm ||
                              (SquadContext.IsCoordinator && _dungeon.CanReenterCurrentWalkInRun))) ||
                            SquadContext.GetLeaderStateSnapshot()!.AcceptLfgProposalOrder),
                    new Action(ctx => _dungeon.RunQueueingOrEntryTick(
                        SquadContext.ActiveSettings ?? SquadSettings.Instance,
                        SquadContext.GetLeaderStateSnapshot(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        ? RunStatus.Success
                        : RunStatus.Failure)
                ),

                new Decorator(
                    ctx => SquadContext.IsDungeonModeActive &&
                           SquadDungeonService.IsInDungeonMap(StyxWoW.Me) &&
                           !(SquadSettings.Instance.Role == SquadRole.Healer &&
                             SquadHealBridge.HasHealCandidate(SquadContext.GetLeaderStateSnapshot())),
                    new Action(ctx =>
                    {
                        _dungeonScriptTicked = true;
                        return _dungeon.TryRunDungeonScriptBehaviors() ? RunStatus.Success : RunStatus.Failure;
                    })
                ),

                // 4. Combat & Role Bridges (Healing, Tank Peel/Combat, DPS Focus & Positioning)
                new Decorator(
                    ctx => IsCombatOrHealNeeded(),
                    new Action(ctx => RunCombatAndRoleBehavior())
                ),

                // 5. Post-combat Resurrection (Healer / Hybrid / Res classes)
                new Decorator(
                    ctx => StyxWoW.Me != null && !StyxWoW.Me.Combat && SquadHealBridge.RunPostCombatResurrectionTick(),
                    new Action(ctx => RunStatus.Success)
                ),

                // 6. Synchronized Resting (Food / Drink / Routine Rest)
                new Decorator(
                    ctx => IsRestingPhaseActive(),
                    new Action(ctx => RunRestBehavior())
                ),

                // 7. Provisioning (Mage food/water/tables & Warlock stones)
                new Decorator(
                    ctx => IsProvisioningTickNeeded(),
                    new Action(ctx =>
                    {
                        var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
                        var provisionLeader = SquadContext.GetLeaderStateSnapshot();
                        var me = StyxWoW.Me;
                        bool iAmPairMember = me != null && provisionLeader != null &&
                            (string.Equals(me.Name, provisionLeader.ActiveProvisionerName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(me.Name, provisionLeader.ActiveTradeTargetName, StringComparison.OrdinalIgnoreCase));
                        if (me != null && me.Mounted && iAmPairMember)
                        {
                            Mount.Dismount("Squad provisioning");
                            SquadPartySyncService.LogAction("Provision: dismounting");
                            return RunStatus.Success;
                        }
                        if (iAmPairMember)
                        {
                            var mountedPartner = provisionLeader!.Members?.FirstOrDefault(m => m.IsMounted &&
                                (string.Equals(m.CharacterName, provisionLeader.ActiveProvisionerName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(m.CharacterName, provisionLeader.ActiveTradeTargetName, StringComparison.OrdinalIgnoreCase)));
                            if (mountedPartner != null)
                            {
                                SquadPartySyncService.LogAction("Provision: waiting for " + mountedPartner.CharacterName + " to dismount");
                                return RunStatus.Success;
                            }
                        }
                        return _provisioning.RunProvisioningTick(settings, provisionLeader)
                            ? RunStatus.Success
                            : RunStatus.Failure;
                    })
                ),

                new Decorator(
                    ctx => (SquadContext.GetLeaderStateSnapshot()?.Phase == SquadPhase.Grinding ||
                            SquadContext.GetLeaderStateSnapshot()?.Phase == SquadPhase.DungeonClearing) &&
                        _provisioning.TryUseSoulwellWhileGrinding(SquadContext.ActiveSettings ?? SquadSettings.Instance),
                    new Action(ctx => RunStatus.Success)
                ),

                // 8. Cross-party Buffs out of combat
                new Decorator(
                    ctx => RunPreCombatBuffBehavior(),
                    new Action(ctx => RunStatus.Success)
                ),

                new Decorator(
                    ctx => SquadPartyBuffService.RunOutOfCombatPartyBuffTick(
                        SquadContext.ActiveSettings ?? SquadSettings.Instance,
                        SquadContext.GetLeaderStateSnapshot()),
                    new Action(ctx => RunStatus.Success)
                ),

                // 9. Looting
                new Decorator(
                    ctx => IsLootNeeded(),
                    new Action(ctx => RunLootBehavior())
                ),

                // 10. Zone Migration & Travel / Pull / Roam
                new Action(ctx => RunTravelOrPullBehavior())
            );
        }

        private static bool ShouldPauseMovement()
        {
            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && leader.Phase == SquadPhase.Paused)
                return true;

            var settings = SquadContext.ActiveSettings;
            if (settings != null && !settings.IsCoordinator && (!SquadContext.IsIpcConnected || leader == null) &&
                (StyxWoW.Me == null || !StyxWoW.Me.Combat))
            {
                return true;
            }
            return false;
        }

        private RunStatus RunDeathAndCorpseBehavior()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var leader = SquadContext.GetLeaderStateSnapshot();

            if (!me.IsGhost)
            {
                _diedInsideDungeon ??= SquadDungeonService.IsInDungeonMap(me);
                if (_deathStartedUtcMs == 0)
                    _deathStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                long deadElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _deathStartedUtcMs;
                bool releaseOrdered = leader?.ReleaseCorpseOrder == true;
                bool waitExpired = deadElapsed >= Math.Max(5, settings.WaitForPartyRessSeconds) * 1000L;

                if (releaseOrdered || waitExpired)
                {
                    SquadPartySyncService.LogAction("Releasing spirit");
                    Lua.DoString("RepopMe()");
                }
                else
                {
                    SquadPartySyncService.LogAction("Waiting for resurrection");
                    if (me.IsMoving) WoWMovement.MoveStop();
                }
                return RunStatus.Success;
            }

            _deathStartedUtcMs = 0;
            WoWPoint corpsePoint = me.CorpsePoint;
            if (SquadContext.IsDungeonModeActive && _diedInsideDungeon != false)
            {
                WoWPoint instanceCorpse = me.InstanceCorpseLocation;
                bool hasInstanceCorpse = instanceCorpse != WoWPoint.Empty && instanceCorpse != WoWPoint.Zero;
                if (SquadDungeonService.IsInDungeonMap(me))
                {
                    return RunStatus.Success;
                }

                WoWPoint dungeonEntrance = _diedInsideDungeon == true
                    ? _dungeon.GetGhostDungeonEntranceTarget()
                    : WoWPoint.Zero;
                if (dungeonEntrance == WoWPoint.Zero && hasInstanceCorpse && instanceCorpse != corpsePoint)
                    dungeonEntrance = instanceCorpse;
                if (dungeonEntrance != WoWPoint.Zero)
                {
                    SquadPartySyncService.LogAction("Dungeon ghost run: moving to entrance portal");
                    if (me.Location.Distance(dungeonEntrance) > 6f)
                        Navigator.MoveTo(dungeonEntrance, "Dungeon CorpseRun: returning to portal");
                    else
                        WoWMovement.ClickToMove(dungeonEntrance);
                    return RunStatus.Success;
                }
            }

            WoWPoint targetPoint = corpsePoint;
            if (leader != null && leader.SquadCorpseRallyX != 0 && leader.SquadCorpseRallyY != 0)
            {
                var rally = new WoWPoint(leader.SquadCorpseRallyX, leader.SquadCorpseRallyY, leader.SquadCorpseRallyZ);
                if (rally.Distance(corpsePoint) <= 40f)
                    targetPoint = rally;
            }

            // Navigate to within 30 yards of the actual corpse. The rally waypoint is
            // only an intermediate target when it leads toward the corpse area.
            if (me.Location.Distance(corpsePoint) > 30f)
            {
                WoWPoint moveTarget = me.Location.Distance(targetPoint) <= 30f ? corpsePoint : targetPoint;
                SquadPartySyncService.LogAction("Moving to corpse");
                Navigator.MoveTo(moveTarget, "CorpseRun: returning to squad corpse");
                return RunStatus.Success;
            }

            // Wait briefly for squadmate ghosts before retrieving corpse if others are also ghosts nearby.
            SquadPartySyncService.LogAction("Retrieving corpse");
            Lua.DoString("RetrieveCorpse()");
            return RunStatus.Success;
        }

        internal static bool CanFollowerEngageFocus(
            bool meInCombat,
            bool leaderInCombat,
            bool focusInCombat,
            bool focusTargetingParty,
            float tankDistanceToFocus)
        {
            return meInCombat || leaderInCombat || focusInCombat || focusTargetingParty || tankDistanceToFocus <= 12f;
        }

        private bool IsCombatOrHealNeeded()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var leader = SquadContext.GetLeaderStateSnapshot();

            if (me.Combat)
                return true;

            if (leader != null && leader.InCombat)
                return true;

            var restSource = SquadContext.GetLocalMemberStateSnapshot();
            bool restRequired = (leader != null && leader.Phase == SquadPhase.Resting) ||
                                restSource?.NeedsRest == true;
            if (leader != null && leader.FocusTargetGuid != 0 && !restRequired)
            {
                var focus = ObjectManager.GetObjectByGuid<WoWUnit>(leader.FocusTargetGuid);
                if (IsBossPullBlocked(settings, leader, me))
                {
                    if (focus is { IsValid: true, IsAlive: true } &&
                        (focus.Combat || focus.IsTargetingMyPartyMember))
                        return true;
                }
                else if (settings.Role == SquadRole.TankLeader)
                {
                    return true;
                }
                else if (focus is { IsValid: true, IsAlive: true })
                {
                    var tankPos = new WoWPoint(leader.X, leader.Y, leader.Z);
                    float tankDist = leader.MapId == me.MapId ? tankPos.Distance(focus.Location) : 999f;
                    if (CanFollowerEngageFocus(me.Combat, leader.InCombat, focus.Combat, focus.IsTargetingMyPartyMember, tankDist))
                        return true;
                }
            }

            if (settings.Role == SquadRole.Healer && SquadHealBridge.HasHealCandidate(leader))
                return true;
            if (settings.Role == SquadRole.HybridOffHeal &&
                leader?.Members?.Any(m => m.IsConnected && !m.IsDead && m.HealthPercent < 88f) == true)
                return true;

            return false;
        }

        private RunStatus RunCombatAndRoleBehavior()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var leader = SquadContext.GetLeaderStateSnapshot();

            if (me.Mounted)
            {
                Mount.Dismount("Squad Combat");
            }

            switch (settings.Role)
            {
                case SquadRole.TankLeader:
                    SquadTankBridge.EnsureTankDefensiveStanceOrAura(settings);
                    if (SquadTankBridge.TryTauntLooseMob())
                        return RunStatus.Success;
                    ulong tankFocusGuid = leader?.FocusTargetGuid ?? 0UL;
                    if (!me.Combat && SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(me) &&
                        (leader == null || leader.EffectiveTankGuid == 0 || leader.EffectiveTankGuid == me.Guid) &&
                        SquadTankBridge.TryExecuteSafeDungeonPull(tankFocusGuid))
                    {
                        return RunStatus.Success;
                    }
                    return RunRoutineCombat(tankFocusGuid);

                case SquadRole.Healer:
                    if (SquadHealBridge.RunHealerTick(emergencyOnly: false, settings, leader))
                    {
                        _healTicked = true;
                        return RunStatus.Success;
                    }
                    if (PositionBackline(settings.HealerStandoffDistance, leader, me))
                        return RunStatus.Success;
                    if (SquadHealBridge.TryHealerAssistDps(settings, leader))
                    {
                        SquadPartySyncService.LogAction("Healer assisting attack");
                        return RunStatus.Success;
                    }
                    return RunStatus.Success;

                case SquadRole.HybridOffHeal:
                    if (SquadHealBridge.RunHealerTick(emergencyOnly: true, settings, leader))
                    {
                        _healTicked = true;
                        return RunStatus.Success;
                    }
                    return RunDpsBehavior(settings, leader, isMelee: false);

                case SquadRole.DpsMelee:
                    return RunDpsBehavior(settings, leader, isMelee: true);

                case SquadRole.DpsRanged:
                default:
                    return RunDpsBehavior(settings, leader, isMelee: false);
            }
        }

        private RunStatus RunDpsBehavior(SquadSettings settings, SquadLeaderState? leader, bool isMelee)
        {
            var me = StyxWoW.Me!;
            var local = SquadContext.GetLocalMemberStateSnapshot();

            // 1. Aggro Peel Rule: if a non-tank pulled aggro and tank is alive nearby, run within 12 yd of Tank.
            if (local != null && local.NeedsPeel && leader != null && leader.MapId == me.MapId && !leader.IsDead && !leader.IsGhost)
            {
                var tankPos = new WoWPoint(leader.X, leader.Y, leader.Z);
                if (me.Location.Distance(tankPos) > 12f)
                {
                    SquadPartySyncService.LogAction("Moving to tank with aggro");
                    Navigator.MoveTo(tankPos, "Bringing aggro to Tank");
                    return RunStatus.Success;
                }
            }

            // 2. Respect DpsEngageDelayMs after tank starts pull (unless already fighting or mob targets squad).
            if (leader != null && leader.FocusTargetGuid != 0 && leader.PullStartedUtcMs > 0)
            {
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - leader.PullStartedUtcMs;
                if (elapsed < settings.DpsEngageDelayMs && !me.Combat)
                {
                    WoWPetControl.SetStance(WoWPetSpell.PetStance.Passive);
                    return RunStatus.Success;
                }
            }

            // Resolve the leader's focus first, then the secondary target when the
            // focus unit is missing, dead, unattackable or friendly; fall back to
            // ordinary targeting only when neither leader target is usable.
            WoWUnit? focus = null;
            if (leader != null)
            {
                foreach (ulong guid in new[] { leader.FocusTargetGuid, leader.SecondaryTargetGuid })
                {
                    if (guid == 0)
                        continue;
                    var candidate = ObjectManager.GetObjectByGuid<WoWUnit>(guid);
                    if (candidate != null && candidate.IsValid && !candidate.Dead &&
                        candidate.Attackable && !candidate.IsFriendly)
                    {
                        focus = candidate;
                        break;
                    }
                }
            }
            if (focus == null)
            {
                // Only fall back to an unit that is already hostile toward the squad;
                // an idle target must not trigger a pull or a pet attack.
                var fallback = Targeting.Instance.FirstUnit;
                if (fallback != null && fallback.IsValid && !fallback.Dead && fallback.Attackable && !fallback.IsFriendly)
                {
                    ulong targetGuid = fallback.CurrentTargetGuid;
                    if (targetGuid == me.Guid || SquadContext.GetKnownMemberGuids().Contains(targetGuid))
                        focus = fallback;
                }
            }

            if (focus != null && focus.IsValid && !focus.Dead && focus.Attackable && !focus.IsFriendly)
            {
                var tankPosForEngage = leader != null ? new WoWPoint(leader.X, leader.Y, leader.Z) : me.Location;
                float tankDistToFocus = leader != null && leader.MapId == me.MapId
                    ? tankPosForEngage.Distance(focus.Location)
                    : 0f;
                bool targetEngaged = CanFollowerEngageFocus(
                    me.Combat,
                    leader?.InCombat == true,
                    focus.Combat,
                    focus.IsTargetingMyPartyMember,
                    tankDistToFocus);

                if (!targetEngaged)
                {
                    if (me.GotAlivePet)
                    {
                        WoWPetControl.SetStance(WoWPetSpell.PetStance.Passive);
                        WoWPetControl.Follow();
                    }
                    return RunStatus.Failure;
                }

                bool currentTargetIsValidAdd = me.CurrentTarget != null &&
                                               me.CurrentTarget.IsValid &&
                                               !me.CurrentTarget.Dead &&
                                               me.CurrentTarget.Combat &&
                                               !me.CurrentTarget.IsFriendly &&
                                               (me.IsCasting || me.ChanneledCastingSpellId != 0 || _activeCombatRoutine != null);
                if (me.CurrentTargetGuid != focus.Guid && !currentTargetIsValidAdd)
                    focus.Target();

                if (me.GotAlivePet)
                {
                    WoWPetControl.Attack(focus);
                }

                var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
                if (isMelee)
                {
                    bool canFlank = _activeCombatRoutine == null &&
                                    (!SquadContext.IsDungeonModeActive || global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);
                    if (canFlank && focus.Distance <= 12f && !focus.IsWithinMeleeRange)
                    {
                        WoWPoint flankPos = SquadFormationService.ComputeMeleeFlankPosition(
                            focus.Location, focus.Rotation, settings.MeleeFlankRadius, slotIdx, slotCount, focus.CombatReach);
                        bool safeFlank = !SquadContext.IsDungeonModeActive ||
                                         (!global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(flankPos) &&
                                          Navigator.CanNavigateFully(me.Location, flankPos));
                        if (safeFlank && me.Location.Distance2D(flankPos) > 2.2f)
                        {
                            SquadPartySyncService.LogAction("Moving to flank " + focus.Name);
                            Navigator.MoveTo(flankPos, "Flanking " + focus.Name);
                            return RunStatus.Success;
                        }
                    }
                }
                else
                {
                    if (PositionBackline(settings.RangedStandoffDistance, leader, me, focus))
                        return RunStatus.Success;
                }

                return RunRoutineCombat(focus.Guid);
            }

            return RunStatus.Failure;
        }

        private bool PositionBackline(float standoffDistance, SquadLeaderState? leader, LocalPlayer me, WoWUnit? selectedTarget = null)
        {
            if (leader == null || leader.MapId != me.MapId || leader.IsDead || leader.IsGhost)
                return false;

            if (SquadContext.IsDungeonModeActive && !global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
                return false;

            if (_activeCombatRoutine != null)
                return false;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;

            WoWUnit? guardTarget = selectedTarget;
            if (guardTarget == null || !guardTarget.IsValid || guardTarget.Dead || guardTarget.IsFriendly)
            {
                guardTarget = leader.FocusTargetGuid != 0
                    ? ObjectManager.GetObjectByGuid<WoWUnit>(leader.FocusTargetGuid)
                    : null;
            }

            if (guardTarget != null && guardTarget.IsValid && !guardTarget.Dead && !guardTarget.IsFriendly)
            {
                bool engaged = me.Combat || guardTarget.Combat || guardTarget.IsTargetingMyPartyMember;
                float engageRange = settings.Role == SquadRole.Healer ? BacklineCombatRangeHealer : BacklineCombatRangeRanged;
                if (ShouldYieldBacklineToCombat(engaged, (float)guardTarget.Distance, guardTarget.InLineOfSpellSight, engageRange))
                    return false;
            }

            var tankPos = new WoWPoint(leader.X, leader.Y, leader.Z);

            WoWPoint? focusPos = null;
            if (selectedTarget != null && selectedTarget.IsValid && !selectedTarget.Dead)
            {
                focusPos = selectedTarget.Location;
            }
            else if (leader.FocusTargetGuid != 0)
            {
                var focusUnit = ObjectManager.GetObjectByGuid<WoWUnit>(leader.FocusTargetGuid);
                if (focusUnit != null && focusUnit.IsValid && !focusUnit.Dead)
                    focusPos = focusUnit.Location;
            }

            ulong latchGuid = guardTarget != null && guardTarget.IsValid && !guardTarget.Dead
                ? guardTarget.Guid
                : 0UL;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool indoorDungeon = SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(me);
            WoWPoint slotPos;
            if (_latchedBacklinePos != WoWPoint.Zero &&
                (!indoorDungeon || !focusPos.HasValue || _latchedBacklinePos.Distance(focusPos.Value) >= 16f) &&
                IsBacklineLatchValid(_latchedBacklineTargetGuid, latchGuid, _latchedBacklineTankPos, tankPos, _latchedBacklineUntilMs, now))
            {
                slotPos = _latchedBacklinePos;
            }
            else
            {
                var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
                if (indoorDungeon)
                {
                    slotPos = SquadFormationService.ComputeDungeonStandoffPosition(
                        tankPos, leader.Facing, focusPos, leader.Breadcrumbs, leader.MapId, standoffDistance, slotIdx, slotCount);
                }
                else
                {
                    slotPos = SquadFormationService.ComputeStandoffPosition(
                        tankPos, leader.Facing, focusPos, standoffDistance, slotIdx, slotCount);
                }
                _latchedBacklinePos = slotPos;
                _latchedBacklineTankPos = tankPos;
                _latchedBacklineTargetGuid = latchGuid;
                _latchedBacklineUntilMs = now + BacklineLatchMs;
                _reachedBacklineAtMs = 0;
            }

            float distanceToSlot = me.Location.Distance2D(slotPos);
            BacklineMovement movement = EvaluateBacklineMovement(distanceToSlot, _wasMovingToBackline, _reachedBacklineAtMs, now, out _reachedBacklineAtMs);
            if (movement == BacklineMovement.MoveToSlot)
            {
                _wasMovingToBackline = true;
                SquadPartySyncService.LogAction("Moving to backline position");
                Navigator.MoveTo(slotPos, "Holding backline standoff");
                return true;
            }

            _wasMovingToBackline = false;
            return false;
        }

        internal static bool ShouldYieldBacklineToCombat(bool engaged, float targetDistance, bool inLineOfSpellSight, float engageRange)
        {
            return engaged && targetDistance <= engageRange && inLineOfSpellSight;
        }

        internal static bool IsBacklineLatchValid(ulong latchedTargetGuid, ulong currentTargetGuid, WoWPoint latchedTankPos, WoWPoint currentTankPos, long latchedUntilMs, long nowUtcMs)
        {
            return latchedTargetGuid == currentTargetGuid &&
                   nowUtcMs < latchedUntilMs &&
                   latchedTankPos.Distance(currentTankPos) <= BacklineLatchTankDrift;
        }

        internal static BacklineMovement EvaluateBacklineMovement(float distanceToSlot, bool wasMoving, long reachedAtMs, long nowUtcMs, out long newReachedAtMs)
        {
            newReachedAtMs = reachedAtMs;
            if (distanceToSlot > BacklineEnterDistance)
            {
                newReachedAtMs = 0;
                return BacklineMovement.MoveToSlot;
            }
            if (distanceToSlot >= BacklineExitDistance)
            {
                newReachedAtMs = 0;
            }
            else
            {
                if (newReachedAtMs == 0)
                    newReachedAtMs = nowUtcMs;
                if (nowUtcMs - newReachedAtMs >= BacklineReachDebounceMs)
                    return BacklineMovement.HoldPosition;
            }
            return wasMoving ? BacklineMovement.MoveToSlot : BacklineMovement.HoldPosition;
        }

        private static bool RunPreCombatBuffBehavior()
        {
            var me = StyxWoW.Me;
            if (me == null || me.Combat || me.Dead || me.IsGhost || me.Mounted)
                return false;

            if (SquadContext.ActivePullCommitGuid != 0)
                return false;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && (leader.InCombat || leader.FocusTargetGuid != 0 || leader.IsWaitingForFollower))
                return false;

            var preBuff = RoutineManager.Current?.PreCombatBuffBehavior;
            if (preBuff == null)
                return false;

            _preCombatBuffTicked = true;
            RunStatus status = TickRoutine(ref _activePreCombatBuffRoutine, preBuff);
            return status != RunStatus.Failure;
        }

        private static RunStatus RunRoutineCombat(ulong focusGuid)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            if (!me.Combat && IsBossPullBlocked(settings, SquadContext.GetLeaderStateSnapshot(), me))
            {
                var target = focusGuid == 0 ? me.CurrentTarget : ObjectManager.GetObjectByGuid<WoWUnit>(focusGuid);
                if (target == null || (!target.Combat && !target.IsTargetingMyPartyMember))
                    return RunStatus.Failure;
            }

            if (focusGuid != 0 && (me.CurrentTarget == null || me.CurrentTarget.Dead || me.CurrentTargetGuid != focusGuid))
            {
                bool keepOffTarget = settings.Role != SquadRole.TankLeader &&
                                     me.CurrentTarget != null &&
                                     me.CurrentTarget.IsValid &&
                                     !me.CurrentTarget.Dead &&
                                     me.CurrentTarget.Combat &&
                                     !me.CurrentTarget.IsFriendly &&
                                     (me.IsCasting || me.ChanneledCastingSpellId != 0 || _activeCombatRoutine != null);
                if (!keepOffTarget)
                {
                    var unit = ObjectManager.GetObjectByGuid<WoWUnit>(focusGuid);
                    if (unit == null || !unit.IsValid || unit.Dead || unit.IsFriendly)
                    {
                        if (!me.Combat)
                            return RunStatus.Failure;
                    }
                    else
                    {
                        unit.Target();
                    }
                }
            }

            if (!me.Combat && (me.CurrentTarget == null || !me.CurrentTarget.IsValid || me.CurrentTarget.Dead || me.CurrentTarget.IsFriendly))
                return RunStatus.Failure;

            var routine = RoutineManager.Current;
            if (routine == null)
                return RunStatus.Failure;

            bool treatAsCombat = me.Combat ||
                                 (settings.Role != SquadRole.TankLeader &&
                                  me.CurrentTarget != null &&
                                  (me.CurrentTarget.Combat || me.CurrentTarget.IsTargetingMyPartyMember || me.CurrentTarget.CurrentTargetGuid != 0));

            Composite? buffBehavior = treatAsCombat ? routine.CombatBuffBehavior : routine.PullBuffBehavior;
            if (buffBehavior != null)
            {
                _combatBuffTicked = true;
                RunStatus buffStatus = TickRoutine(ref _activeCombatBuffRoutine, buffBehavior);
                if (buffStatus == RunStatus.Running)
                    return RunStatus.Running;
            }

            Composite? behavior = treatAsCombat ? routine.CombatBehavior : (routine.PullBehavior ?? routine.CombatBehavior);
            if (behavior == null)
                return RunStatus.Failure;

            SquadPartySyncService.LogAction((treatAsCombat ? "Combat routine" : "Pull routine") +
                (me.CurrentTarget != null ? ": " + me.CurrentTarget.Name : string.Empty));
            _combatTicked = true;
            RunStatus status = TickRoutine(ref _activeCombatRoutine, behavior);
            return status == RunStatus.Failure ? RunStatus.Success : status;
        }

        private static bool IsRestingPhaseActive()
        {
            var me = StyxWoW.Me;
            if (me == null || me.Combat || me.Dead || me.IsGhost)
                return false;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && leader.Phase == SquadPhase.Resting)
                return true;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            return SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true ||
                GetBossRecoveryManaTarget(settings, leader, me) > me.PowerPercent;
        }

        private static int GetBossRecoveryManaTarget(SquadSettings settings, SquadLeaderState? leader, LocalPlayer me) =>
            (settings.Role == SquadRole.Healer || settings.Role == SquadRole.HybridOffHeal) &&
            me.PowerType == WoWPowerType.Mana && IsBossPullBlocked(settings, leader, me)
                ? SquadDungeonService.GetBossReadinessManaPercent(settings)
                : 0;

        private static RunStatus RunRestBehavior()
        {
            SquadPartySyncService.LogAction("Resting");
            var me = StyxWoW.Me!;
            if (me.IsMoving)
                WoWMovement.MoveStop();

            if (me.Mounted)
                Mount.Dismount("Resting");

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            if (SquadProvisioningService.TryConjureForSelf(settings))
                return RunStatus.Success;
            if (me.HealthPercent < settings.ResumeRestHealthPct && !me.HasAura("Food") && !me.HasAura("Refreshment"))
                Rest.FeedImmediate();

            bool usesMana = me.PowerType == WoWPowerType.Mana;
            int manaTarget = Math.Max(settings.ResumeRestManaPct,
                GetBossRecoveryManaTarget(settings, SquadContext.GetLeaderStateSnapshot(), me));
            if (usesMana && me.PowerPercent < manaTarget && !me.HasAura("Drink") && !me.HasAura("Refreshment"))
                Rest.DrinkImmediate();

            var restBehavior = RoutineManager.Current?.RestBehavior;
            if (restBehavior != null && (me.HealthPercent < settings.ResumeRestHealthPct || (usesMana && me.PowerPercent < manaTarget)))
            {
                _restTicked = true;
                TickRoutine(ref _activeRestRoutine, restBehavior);
            }

            return RunStatus.Success;
        }

        private bool IsProvisioningTickNeeded()
        {
            var me = StyxWoW.Me;
            if (me == null || me.Combat || me.Dead || me.IsGhost)
                return false;

            if (_provisioning.IsTradeBusy)
                return true;

            var leader = SquadContext.GetLeaderStateSnapshot();
            return leader != null && leader.Phase == SquadPhase.Provisioning;
        }

        private static bool IsLootNeeded()
        {
            var me = StyxWoW.Me;
            if (me == null || me.Combat || me.Dead || me.IsGhost || me.FreeNormalBagSlots == 0)
                return false;

            if (SquadContext.ActivePullCommitGuid != 0)
                return false;

            if (LootFrame.Instance.IsVisible)
                return true;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null)
            {
                if (leader.InCombat || leader.FocusTargetGuid != 0)
                    return false;
                if (leader.Phase != SquadPhase.Looting &&
                    leader.Phase != SquadPhase.Grinding &&
                    leader.Phase != SquadPhase.DungeonClearing)
                    return false;
            }

            if (HasNearbySquadThreat())
                return false;

            return FindEligibleLoot() != null;
        }

        private static bool HasNearbySquadThreat()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            var memberGuids = SquadContext.GetKnownMemberGuids();
            return ObjectManager.CachedUnits.Any(u =>
                u != null && u.IsValid && !u.Dead && u.Combat && u.Attackable && !u.IsFriendly && !u.IsPlayer &&
                u.Location.Distance(me.Location) <= 45f &&
                (u.IsTargetingMeOrPet || u.IsTargetingMyPartyMember ||
                 (u.CurrentTargetGuid != 0 && memberGuids.Contains(u.CurrentTargetGuid))));
        }

        private static readonly Dictionary<ulong, long> LootRetry = new();

        private static WoWObject? FindEligibleLoot()
        {
            var leader = SquadContext.GetLeaderStateSnapshot();
            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            bool bossesOnly = SquadContext.IsDungeonModeActive &&
                              SquadDungeonService.IsInDungeonMap(StyxWoW.Me) &&
                              (leader?.DungeonLootMode ?? settings.DungeonLootMode) == SquadDungeonLootMode.BossesOnly;
            long now = Environment.TickCount64;
            foreach (var guid in LootRetry.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                LootRetry.Remove(guid);
            return LootTargeting.Instance.LootingList.FirstOrDefault(o =>
            {
                if (o == null || !o.IsValid || LootRetry.ContainsKey(o.Guid))
                    return false;
                if (leader != null && o.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z)) > 25f)
                    return false;
                if (!bossesOnly)
                    return true;
                if (o is WoWGameObject)
                    return true;
                if (o is WoWUnit unit)
                {
                    return global::Bots.DungeonBuddy.BossManager.Bosses.Any(b => b.EntryId == unit.Entry) ||
                           global::Bots.DungeonBuddy.Profiles.ProfileManager.CurrentProfile?.BossEncounters.Any(b => b.Entry == unit.Entry) == true;
                }
                return false;
            });
        }

        private static RunStatus RunLootBehavior()
        {
            var me = StyxWoW.Me!;
            if (LootFrame.Instance.IsVisible)
            {
                SquadPartySyncService.LogAction("Looting");
                LootFrame.Instance.LootAll();
                StyxWoW.Sleep(200);
                LootFrame.Instance.Close();
                return RunStatus.Success;
            }

            var lootObj = FindEligibleLoot();
            if (lootObj == null || !lootObj.IsValid)
                return RunStatus.Failure;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && lootObj.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z)) > 25f)
                return RunStatus.Failure;

            if (lootObj.Location.Distance(me.Location) > 4.5f)
            {
                SquadPartySyncService.LogAction("Moving to loot " + lootObj.Name);
                if (Navigator.MoveTo(lootObj.Location, "Looting " + lootObj.Name) == MoveResult.PathGenerationFailed)
                {
                    LootRetry[lootObj.Guid] = Environment.TickCount64 + 15000;
                    Navigator.Clear();
                    return FindEligibleLoot() == null ? RunStatus.Failure : RunStatus.Success;
                }
                return RunStatus.Success;
            }

            if (me.IsMoving) WoWMovement.MoveStop();
            SquadPartySyncService.LogAction("Looting " + lootObj.Name);
            lootObj.Interact();
            return RunStatus.Success;
        }

        private RunStatus RunTravelOrPullBehavior()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var leader = SquadContext.GetLeaderStateSnapshot();
            bool isDungeon = SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(me);

            if (isDungeon && !global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled)
                return RunStatus.Success;

            ulong effectiveTankGuid = isDungeon ? leader?.EffectiveTankGuid ?? 0UL : 0UL;
            bool anotherTankDrives = effectiveTankGuid != 0 && effectiveTankGuid != me.Guid;
            bool drivesTravel = effectiveTankGuid != 0 ? !anotherTankDrives : settings.IsCoordinator;

            if (drivesTravel)
            {
                if (me.GotAlivePet)
                    WoWPetControl.Follow();

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_pullCommittedGuid != 0 && IsPullCommitReleased(now))
                    ClearPullCommit();

                bool bossPullBlocked = isDungeon && IsBossPullBlocked(settings, leader, me);
                if (bossPullBlocked)
                    ClearIdlePullCommit();

                if (_pullCommittedGuid != 0)
                {
                    var committed = ObjectManager.GetObjectByGuid<WoWUnit>(_pullCommittedGuid);
                    if (committed != null && committed.IsValid && !committed.Dead && !committed.IsFriendly)
                    {
                        if (me.CurrentTargetGuid != committed.Guid)
                            committed.Target();
                        if (!me.Combat && isDungeon && !anotherTankDrives &&
                            SquadTankBridge.TryExecuteSafeDungeonPull(_pullCommittedGuid))
                        {
                            return RunStatus.Success;
                        }
                        return RunRoutineCombat(_pullCommittedGuid);
                    }
                    ClearPullCommit();
                }

                // 1. Hard backtrack toward lagging squadmate if > HardBacktrackDistance.
                if (_coordinator.IsHardBacktracking && leader?.Members != null)
                {
                    var trailing = leader.Members
                        .Where(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.MapId == me.MapId)
                        .OrderByDescending(m => m.DistanceToLeader)
                        .FirstOrDefault();
                    if (trailing != null)
                    {
                        var backPt = _coordinator.GetBacktrackTarget(me.Location, new WoWPoint(trailing.X, trailing.Y, trailing.Z));
                        if (backPt.HasValue)
                        {
                            SquadPartySyncService.LogAction("Moving back for " + trailing.CharacterName);
                            Navigator.MoveTo(backPt.Value, "Backtracking for lagging squadmate");
                            return RunStatus.Success;
                        }
                    }
                }

                // 2. Soft wait when follower > SoftWaitDistance.
                if (_coordinator.IsWaitingForFollowerDebounced)
                {
                    SquadPartySyncService.LogAction("Waiting for squadmate");
                    if (me.IsMoving) WoWMovement.MoveStop();
                    return RunStatus.Success;
                }

                if (isDungeon)
                {
                    if (bossPullBlocked)
                    {
                        SquadPartySyncService.LogAction("Dungeon: holding pre-boss readiness barrier");
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        return RunStatus.Success;
                    }

                    if (SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
                    {
                        WoWUnit? dungeonTarget = Targeting.Instance.FirstUnit;
                        if (dungeonTarget != null && dungeonTarget.IsValid && !dungeonTarget.Dead &&
                            dungeonTarget.Attackable && !dungeonTarget.IsFriendly)
                        {
                            _currentFocusGuid = dungeonTarget.Guid;
                            BeginPullCommit(dungeonTarget.Guid, now);
                            dungeonTarget.Target();
                            if (!me.Combat && !anotherTankDrives &&
                                SquadTankBridge.TryExecuteSafeDungeonPull(dungeonTarget.Guid))
                            {
                                return RunStatus.Success;
                            }
                            return RunRoutineCombat(dungeonTarget.Guid);
                        }
                    }

                    if (leader != null && !SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
                    {
                        SquadPartySyncService.LogAction("Dungeon: waiting for squadmates to gather");
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        return RunStatus.Success;
                    }

                    WoWPoint nextDungeonPt = _dungeon.ComputeLeaderNextDungeonPoint();
                    if (nextDungeonPt != WoWPoint.Zero && me.Location.Distance(nextDungeonPt) > SquadDungeonService.HotspotArrivalDistance)
                    {
                        SquadPartySyncService.LogAction("Dungeon: advancing toward " + (leader?.CurrentBossName ?? "next objective"));
                        Navigator.MoveTo(nextDungeonPt, "Advancing through dungeon");
                    }
                    else if (me.IsMoving)
                    {
                        WoWMovement.MoveStop();
                    }
                    return RunStatus.Success;
                }

                // 3. Migrate cleanly to new sub-profile hotspot when MigratingZone.
                var grindArea = StyxWoW.AreaManager?.CurrentGrindArea ?? (ProfileManager.CurrentOuterProfile != null ? ProfileManager.CurrentProfile?.GrindArea : null);
                if (leader != null && leader.Phase == SquadPhase.MigratingZone && grindArea != null && grindArea.Hotspots.Count > 0)
                {
                    WoWPoint hs = grindArea.CurrentHotSpot.Position;
                    if (me.Location.Distance(hs) > 10f)
                    {
                        SquadPartySyncService.LogAction("Moving to new zone hotspot");
                        Navigator.MoveTo(hs, "Migrating to new sub-profile hotspot");
                        return RunStatus.Success;
                    }
                    return RunStatus.Success;
                }

                // 4. Ready to pull?
                if (SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
                {
                    WoWUnit? target = Targeting.Instance.TargetList.FirstOrDefault(u =>
                        u != null && u.IsValid && !u.Dead && u.Attackable && !u.IsFriendly &&
                        IsEligiblePullCandidateDistance(me.Location.Distance(u.Location)));
                    if (target != null)
                    {
                        _currentFocusGuid = target.Guid;
                        _focusChangedUtcMs = now;
                        BeginPullCommit(target.Guid, now);
                        target.Target();
                        return RunRoutineCombat(target.Guid);
                    }
                }

                // 5. Roam GrindArea hotspots (GrindArea.CurrentHotSpot automatically cycles hotspots)
                if (grindArea != null && grindArea.Hotspots.Count > 0)
                {
                    WoWPoint hotspot = grindArea.CurrentHotSpot.Position;
                    if (hotspot != WoWPoint.Zero && me.Location.Distance2D(hotspot) > 12f)
                    {
                        SquadPartySyncService.LogAction("Moving to grind hotspot");
                        Navigator.MoveTo(hotspot, "Roaming hotspot");
                        return RunStatus.Success;
                    }
                }

                return RunStatus.Success;
            }
            else
            {
                if (me.GotAlivePet)
                {
                    WoWPetControl.SetStance(WoWPetSpell.PetStance.Passive);
                    WoWPetControl.Follow();
                }

                if (leader == null)
                {
                    Navigator.Clear();
                    WoWMovement.MoveStop();
                    return RunStatus.Success;
                }

                if (anotherTankDrives)
                {
                    var tank = ObjectManager.GetObjectByGuid<WoWPlayer>(effectiveTankGuid);
                    if (tank != null && tank.IsValid && tank.IsAlive)
                    {
                        float dTank = me.Location.Distance(tank.Location);
                        if (dTank > 10.0f)
                        {
                            SquadPartySyncService.LogAction("Dungeon: following tank " + tank.Name);
                            Navigator.MoveTo(tank.Location, "Following dungeon tank " + tank.Name);
                        }
                        else if (me.IsMoving && dTank <= 5.5f)
                        {
                            WoWMovement.MoveStop();
                        }
                        return RunStatus.Success;
                    }
                }

                if (leader.MapId != me.MapId)
                    return RunStatus.Success;

                int slotIdx = SquadContext.GetMySlotIndex(me.Name, leader);
                WoWPoint followPos = isDungeon
                    ? SquadFormationService.ComputeDungeonFollowPosition(me.Location, leader, slotIdx, _travelProgress)
                    : SquadFormationService.ComputeTravelFollowPosition(me.Location, leader, slotIdx, _travelProgress);
                float stopWithin = isDungeon || leader.IsWaitingForFollower ? 2.5f : 4.0f;
                if (me.Location.Distance(followPos) > stopWithin)
                {
                    SquadPartySyncService.LogAction("Following " + leader.LeaderName);
                    Navigator.MoveTo(followPos, "Following " + leader.LeaderName);
                }
                else if (me.IsMoving)
                {
                    WoWMovement.MoveStop();
                }

                return RunStatus.Success;
            }
        }

        private static string ComputeFileHash(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return string.Empty;
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal enum BacklineMovement
    {
        HoldPosition,
        MoveToSlot
    }
}
