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
        private const float DungeonTrashScanRadius = 40f;
        internal const long DungeonAdvanceBlockMaxMs = 8000L;
        private const long DungeonForcedStepMaxMs = 8000L;
        private const long DungeonAdvancePathRefreshMs = 500L;
        private const float RestThreatRadius = 15f;
        private const float RestMeleeThreatRadius = 10f;
        private const long WorldTransitionGraceMs = 3000L;
        private const float DungeonForcedStepArrivalDistance = 2f;
        private const long GhostPortalStallWindowMs = 10000L;
        private const float GhostPortalProgressMinYards = 2.0f;
        private const int GhostPortalMaxFailedMoves = 3;
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
        private WoWPoint _lastGhostPosition = WoWPoint.Empty;
        private float _ghostBestEntranceDistance = float.MaxValue;
        private long _lastGhostProgressUtcMs;
        private long _nextGhostPortalAttemptUtcMs;
        private long _nextGhostNoEntranceLogUtcMs;
        private long _nextGhostEntranceResolveUtcMs;
        private WoWPoint _ghostEntrance;
        private uint _ghostEntranceDungeonId;
        private string _ghostEntranceSource = string.Empty;
        private uint _deathDungeonId;
        private WoWPoint _deathDungeonEntrance;
        private int _ghostPortalAttempts;
        private WoWPoint _ghostPortalApproach = WoWPoint.Empty;
        private int _ghostPathFailures;
        private int _ghostBestRouteGeneration;
        private float _ghostBestRouteDistance = float.MaxValue;
        private readonly Dictionary<string, long> _rejectedGhostEntranceUntilUtcMs = new(StringComparer.OrdinalIgnoreCase);
        private bool IsGhostSourceRejected(string source, long nowUtcMs) =>
            _rejectedGhostEntranceUntilUtcMs.TryGetValue(source, out long until) && nowUtcMs < until;
        private bool _dungeonScriptTicked;
        private bool _localWasNeedingRest;
        private long _dungeonAdvanceBlockedSinceUtcMs;
        private uint _dungeonAdvanceBlockedMapId;
        private WoWPoint _dungeonAdvanceBlockedObjective = WoWPoint.Zero;
        private long _dungeonAdvanceLastHoldEvalUtcMs;
        private WoWPoint _dungeonForcedStep = WoWPoint.Zero;
        private WoWPoint _dungeonForcedStepObjective = WoWPoint.Zero;
        private ulong _dungeonForcedStepLeaderGuid;
        private long _dungeonForcedStepIssuedUtcMs;
        private float _dungeonForcedStepBestDist = float.MaxValue;
        private float _dungeonForcedObjectiveBestDist = float.MaxValue;
        private long _dungeonForcedStepLastProgressUtcMs;
        private bool _dungeonForcedStepBypassTrashGrace;
        private bool _dungeonForcedStepWasHeldForTrash;
        private float _dungeonForcedStepMovePrecision;
        private WoWPoint _dungeonStepFailureObjective = WoWPoint.Zero;
        private int _dungeonStepConsecutiveFailures;
        private long _lastDungeonStepFailureUtcMs;
        private long _lastDungeonAdvancePathUtcMs;
        private WoWPoint _cachedDungeonAdvanceObjective = WoWPoint.Zero;
        private WoWPoint[] _cachedDungeonAdvancePath = Array.Empty<WoWPoint>();
        private ulong _failedDungeonMeleeTargetGuid;
        private long _failedDungeonMeleeUntilUtcMs;
        private WoWPoint _failedDungeonMeleeDest = WoWPoint.Zero;
        private bool _failedDungeonMeleeDirect;
        private long _packPullHoldSinceUtcMs;
        private bool _hasObservedMapId;
        private uint _lastObservedMapId;
        private long _lastMapChangeUtcMs;
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
            _lastGhostPosition = WoWPoint.Empty;
            _ghostBestEntranceDistance = float.MaxValue;
            _lastGhostProgressUtcMs = 0;
            _nextGhostPortalAttemptUtcMs = 0;
            _nextGhostNoEntranceLogUtcMs = 0;
            _nextGhostEntranceResolveUtcMs = 0;
            _ghostEntrance = WoWPoint.Empty;
            _ghostEntranceDungeonId = 0;
            _ghostEntranceSource = string.Empty;
            _deathDungeonId = 0;
            _deathDungeonEntrance = WoWPoint.Empty;
            _ghostPortalAttempts = 0;
            _ghostPortalApproach = WoWPoint.Empty;
            _ghostPathFailures = 0;
            _ghostBestRouteGeneration = 0;
            _ghostBestRouteDistance = float.MaxValue;
            _rejectedGhostEntranceUntilUtcMs.Clear();
            _localWasNeedingRest = false;
            _hasObservedMapId = false;
            _lastObservedMapId = 0;
            _lastMapChangeUtcMs = 0;
            ResetDungeonCorridorBlock();
            ClearDungeonForcedStep();
            ResetDungeonStepFailures();
            ClearDungeonAdvanceCache();
            _packPullHoldSinceUtcMs = 0;

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

            bool allyInCombat = HasLivingAllyInCombat(decision, me.Guid);
            if (_pullCommittedGuid != 0 && (allyInCombat || IsPullCommitReleased(now)))
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
                    ((!bossPullBlocked && !allyInCombat) || pugTank.CurrentTarget.Combat || pugTank.CurrentTarget.IsTargetingMyPartyMember))
                {
                    if (_currentFocusGuid != pugTank.CurrentTargetGuid)
                        _pullStartedUtcMs = now;
                    _currentFocusGuid = pugTank.CurrentTargetGuid;
                    _currentSecondaryGuid = 0UL;
                    return;
                }
            }

            if (ShouldClearCoordinatorTargetsWhenDead(me.Dead, me.IsGhost))
            {
                _currentFocusGuid = 0UL;
                _currentSecondaryGuid = 0UL;
                _pullStartedUtcMs = 0L;
                ClearPullCommit();
                return;
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
            bool localCannotPull = local == null || local.IsDead || local.IsGhost || local.IsInCombat ||
                                   local.NeedsRest || local.IsLooting || allyInCombat;
            bool commitActive = _pullCommittedGuid != 0 && now < _pullCommittedUntilMs;
            bool canInitiatePull = !bossPullBlocked && !anotherTankDrives && !allyInCombat &&
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

            var memberGuids = SquadContext.GetKnownMemberGuids();
            WoWUnit? secondary = Targeting.Instance.TargetList
                .FirstOrDefault(u => u != null && u.IsValid && !u.Dead && u.CanSelect && u.Guid != _currentFocusGuid &&
                    u.Combat &&
                    (u.IsTargetingMyPartyMember || u.IsTargetingMeOrPet || u.TaggedByMe ||
                     (u.CurrentTargetGuid != 0 && memberGuids.Contains(u.CurrentTargetGuid))));
            _currentSecondaryGuid = secondary?.Guid ?? 0UL;
        }

        private static bool HasLivingAllyInCombat(SquadLeaderState? leader, ulong localGuid)
        {
            return leader?.Members?.Any(m => m.Guid != localGuid && m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
        }

        /// <summary>
        /// Live hostile trash positions around the leader, used to decide whether the next dungeon
        /// breadcrumb step is safe. Hostiles already locked in a fight with the squad are included
        /// on purpose: the tank must finish that pack before walking deeper into the corridor.
        /// </summary>
        private static List<WoWPoint> CollectDungeonTrashPoints(WoWPoint from, float radius)
        {
            var points = new List<WoWPoint>();
            foreach (var unit in ObjectManager.CachedUnits)
            {
                if (unit == null || !unit.IsValid || unit.Dead)
                    continue;
                if (!unit.Attackable || unit.IsFriendly || unit.IsPlayer || unit.IsCritter || unit.IsNonCombatPet)
                    continue;
                if (unit.ControllingPlayer != null)
                    continue;
                if (unit.Location.Distance(from) > radius)
                    continue;
                points.Add(unit.Location);
            }
            return points;
        }

        private void ClearDungeonForcedStep()
        {
            _dungeonForcedStep = WoWPoint.Zero;
            _dungeonForcedStepObjective = WoWPoint.Zero;
            _dungeonForcedStepLeaderGuid = 0;
            _dungeonForcedStepIssuedUtcMs = 0;
            _dungeonForcedStepBestDist = float.MaxValue;
            _dungeonForcedObjectiveBestDist = float.MaxValue;
            _dungeonForcedStepLastProgressUtcMs = 0;
            _dungeonForcedStepBypassTrashGrace = false;
            _dungeonForcedStepWasHeldForTrash = false;
            _dungeonForcedStepMovePrecision = 0f;
        }

        private void ResetDungeonCorridorBlock()
        {
            _dungeonAdvanceBlockedSinceUtcMs = 0;
            _dungeonAdvanceBlockedMapId = 0;
            _dungeonAdvanceBlockedObjective = WoWPoint.Zero;
            _dungeonAdvanceLastHoldEvalUtcMs = 0;
        }

        internal static bool EvaluateDungeonCorridorBlock(
            bool bypassTrashGrace,
            bool hasTrashOnSegment,
            uint mapId,
            WoWPoint corridorObjective,
            ref uint blockedMapId,
            ref WoWPoint blockedObjective,
            ref long lastHoldEvalUtcMs,
            ref long blockedSinceUtcMs,
            long nowUtcMs,
            out bool newlyBlocked,
            out bool graceExpired,
            long blockGraceMs = DungeonAdvanceBlockMaxMs,
            long continuousEvalGapMs = 1500L)
        {
            newlyBlocked = false;
            graceExpired = false;

            if (bypassTrashGrace || !hasTrashOnSegment)
            {
                blockedSinceUtcMs = 0;
                blockedMapId = 0;
                blockedObjective = WoWPoint.Zero;
                lastHoldEvalUtcMs = 0;
                return false;
            }

            if (blockedSinceUtcMs > 0 &&
                (blockedMapId != mapId ||
                 blockedObjective.Distance(corridorObjective) > SquadDungeonService.HotspotArrivalDistance ||
                 (lastHoldEvalUtcMs > 0 && nowUtcMs - lastHoldEvalUtcMs >= continuousEvalGapMs)))
            {
                blockedSinceUtcMs = 0;
            }

            lastHoldEvalUtcMs = nowUtcMs;
            if (blockedSinceUtcMs == 0)
            {
                blockedSinceUtcMs = nowUtcMs;
                blockedMapId = mapId;
                blockedObjective = corridorObjective;
                newlyBlocked = true;
            }

            if (nowUtcMs - blockedSinceUtcMs < blockGraceMs)
                return true;

            blockedSinceUtcMs = 0;
            blockedMapId = 0;
            blockedObjective = WoWPoint.Zero;
            lastHoldEvalUtcMs = 0;
            graceExpired = true;
            return false;
        }

        private void ResetDungeonStepFailures()
        {
            _dungeonStepFailureObjective = WoWPoint.Zero;
            _dungeonStepConsecutiveFailures = 0;
            _lastDungeonStepFailureUtcMs = 0;
        }

        private void ClearDungeonAdvanceCache()
        {
            _cachedDungeonAdvanceObjective = WoWPoint.Zero;
            _cachedDungeonAdvancePath = Array.Empty<WoWPoint>();
            _lastDungeonAdvancePathUtcMs = 0;
        }

        internal static bool EvaluateDungeonStepProgress(
            float currentStepDistance,
            float currentObjectiveDistance,
            ref float bestStepDistance,
            ref float bestObjectiveDistance,
            ref long lastProgressUtcMs,
            long nowUtcMs,
            long stallTimeoutMs = SquadDungeonService.DungeonStepStallTimeoutMs,
            float minProgressYards = SquadDungeonService.DungeonStepMinProgressYards)
        {
            bool progressed = bestStepDistance == float.MaxValue ||
                              currentStepDistance <= bestStepDistance - minProgressYards ||
                              (currentObjectiveDistance < float.MaxValue && bestObjectiveDistance == float.MaxValue) ||
                              (currentObjectiveDistance < float.MaxValue &&
                               currentObjectiveDistance <= bestObjectiveDistance - minProgressYards);
            if (progressed)
            {
                if (currentStepDistance < bestStepDistance)
                    bestStepDistance = currentStepDistance;
                if (currentObjectiveDistance < bestObjectiveDistance)
                    bestObjectiveDistance = currentObjectiveDistance;
                lastProgressUtcMs = nowUtcMs;
                return false;
            }

            return lastProgressUtcMs > 0 && nowUtcMs - lastProgressUtcMs >= stallTimeoutMs;
        }

        internal static bool RecordDungeonStepFailure(
            WoWPoint objective,
            ref WoWPoint failureObjective,
            ref int consecutiveFailures,
            ref long lastFailureUtcMs,
            long nowUtcMs,
            int maxFailures = SquadDungeonService.DungeonStepMaxConsecutiveFailures,
            long failureCooldownMs = 500L)
        {
            if (objective == WoWPoint.Zero)
                return false;

            if (failureObjective == WoWPoint.Zero ||
                failureObjective.Distance(objective) > SquadDungeonService.HotspotArrivalDistance)
            {
                failureObjective = objective;
                consecutiveFailures = 0;
                lastFailureUtcMs = 0;
            }

            if (lastFailureUtcMs > 0 && nowUtcMs - lastFailureUtcMs < failureCooldownMs)
                return false;

            lastFailureUtcMs = nowUtcMs;
            consecutiveFailures++;
            if (consecutiveFailures < maxFailures)
                return false;

            consecutiveFailures = 0;
            lastFailureUtcMs = 0;
            failureObjective = WoWPoint.Zero;
            return true;
        }

        private void SyncDungeonForcedStepObjective(WoWPoint objective)
        {
            if (_dungeonForcedStep == WoWPoint.Zero)
                return;
            if (objective == WoWPoint.Zero || objective != _dungeonForcedStepObjective)
                ClearDungeonForcedStep();
        }

        private void HandleFailedDungeonStep(WoWPoint step, WoWPoint objective, uint mapId, long nowUtcMs)
        {
            ClearDungeonForcedStep();
            _cachedDungeonAdvanceObjective = objective;
            _cachedDungeonAdvancePath = Array.Empty<WoWPoint>();
            _lastDungeonAdvancePathUtcMs = nowUtcMs;
            if (StyxWoW.Me?.IsMoving == true)
                WoWMovement.MoveStop();
            Navigator.Clear();
            bool shouldBlacklist = RecordDungeonStepFailure(
                objective,
                ref _dungeonStepFailureObjective,
                ref _dungeonStepConsecutiveFailures,
                ref _lastDungeonStepFailureUtcMs,
                nowUtcMs);
            if (shouldBlacklist)
            {
                if (objective != WoWPoint.Zero)
                    _dungeon.BlacklistDungeonNavigationPoint(mapId, objective, nowUtcMs);
                if (step != WoWPoint.Zero && step.Distance(objective) > SquadDungeonService.HotspotArrivalDistance)
                    _dungeon.BlacklistDungeonNavigationPoint(mapId, step, nowUtcMs);
                Logging.WriteDebug("[PartyGrind Dungeon] Blacklisted unreachable advance target ({0:F1},{1:F1},{2:F1}) on map {3} for {4}s",
                    objective.X, objective.Y, objective.Z, mapId, SquadDungeonService.DungeonStepBlacklistCooldownMs / 1000L);
            }
        }

        private MoveResult IssueDungeonForcedStep(
            WoWPoint step,
            WoWPoint objective,
            ulong leaderGuid,
            long nowUtcMs,
            bool bypassTrashGrace = false)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return MoveResult.Failed;

            float stepPrecision = Math.Min(Navigator.PathPrecision, DungeonForcedStepArrivalDistance);
            MoveResult result = Navigator.MoveTo(step, stepPrecision, "Advancing through dungeon");
            if (result == MoveResult.ReachedDestination &&
                objective != WoWPoint.Zero &&
                me.Location.Distance(objective) > SquadDungeonService.HotspotArrivalDistance &&
                me.Location.Distance(step) > 0.75f)
            {
                stepPrecision = 0.75f;
                result = Navigator.MoveTo(step, stepPrecision, "Advancing through dungeon");
            }
            if (result == MoveResult.PathGenerationFailed || result == MoveResult.Failed)
            {
                HandleFailedDungeonStep(step, objective, me.MapId, nowUtcMs);
                return result;
            }

            if (result == MoveResult.ReachedDestination)
            {
                if (objective != WoWPoint.Zero && me.Location.Distance(objective) > SquadDungeonService.HotspotArrivalDistance)
                {
                    HandleFailedDungeonStep(step, objective, me.MapId, nowUtcMs);
                    return MoveResult.Failed;
                }

                ClearDungeonForcedStep();
                ResetDungeonStepFailures();
                return result;
            }

            _dungeonForcedStep = step;
            _dungeonForcedStepObjective = objective;
            _dungeonForcedStepLeaderGuid = leaderGuid;
            _dungeonForcedStepIssuedUtcMs = nowUtcMs;
            _dungeonForcedStepBestDist = me.Location.Distance(step);
            _dungeonForcedObjectiveBestDist = objective != WoWPoint.Zero ? me.Location.Distance(objective) : float.MaxValue;
            _dungeonForcedStepLastProgressUtcMs = nowUtcMs;
            _dungeonForcedStepBypassTrashGrace = bypassTrashGrace;
            _dungeonForcedStepMovePrecision = stepPrecision;
            return result;
        }

        private bool DungeonForcedStepActive(ulong leaderGuid, long nowUtcMs)
        {
            if (_dungeonForcedStep == WoWPoint.Zero)
                return false;

            var me = StyxWoW.Me;
            float arrivalDist = _dungeonForcedStepMovePrecision > 0f
                ? _dungeonForcedStepMovePrecision
                : DungeonForcedStepArrivalDistance;
            bool arrived = me != null && me.Location.Distance(_dungeonForcedStep) <= arrivalDist;
            bool stale = me == null ||
                         leaderGuid != _dungeonForcedStepLeaderGuid ||
                         nowUtcMs - _dungeonForcedStepIssuedUtcMs > DungeonForcedStepMaxMs ||
                         arrived;
            if (!stale)
                return true;

            ClearDungeonForcedStep();
            if (arrived)
                ResetDungeonStepFailures();
            return false;
        }

        internal static bool IsTransientWorldTransition(bool isInWorld, long mapChangeUtcMs, long nowUtcMs) =>
            !isInWorld || (mapChangeUtcMs > 0 && nowUtcMs - mapChangeUtcMs < WorldTransitionGraceMs);

        internal static bool IsSameMapForNavigation(uint localMapId, uint targetMapId) =>
            localMapId == targetMapId;

        internal static float ComputeGhostPortalProgressDistance(
            WoWPoint currentLocation,
            WoWPoint entranceLocation,
            IReadOnlyList<WoWPoint>? remainingPath)
        {
            if (remainingPath == null || remainingPath.Count == 0)
                return currentLocation.Distance(entranceLocation);

            float total = 0f;
            WoWPoint prev = currentLocation;
            bool visitedWaypoint = false;
            for (int i = 0; i < remainingPath.Count; i++)
            {
                WoWPoint wp = remainingPath[i];
                if (wp == WoWPoint.Zero)
                    continue;
                total += prev.Distance(wp);
                prev = wp;
                visitedWaypoint = true;
            }

            return visitedWaypoint ? total + prev.Distance(entranceLocation) : currentLocation.Distance(entranceLocation);
        }

        internal static bool EvaluateGhostPortalProgress(
            float progressDistance,
            bool hasRoutePath,
            int routeGeneration,
            ref int bestRouteGeneration,
            ref float bestRouteDistance,
            ref float bestStraightDistance,
            ref long lastProgressUtcMs,
            long nowUtcMs,
            long stallWindowMs = GhostPortalStallWindowMs,
            float minProgressYards = GhostPortalProgressMinYards)
        {
            if (hasRoutePath)
            {
                if (routeGeneration != bestRouteGeneration)
                {
                    bool improvedAcrossReplan = bestRouteDistance != float.MaxValue &&
                                                progressDistance <= bestRouteDistance - minProgressYards;
                    bestRouteGeneration = routeGeneration;
                    bestRouteDistance = progressDistance;
                    if (lastProgressUtcMs == 0 || improvedAcrossReplan)
                    {
                        lastProgressUtcMs = nowUtcMs;
                        return false;
                    }

                    return nowUtcMs - lastProgressUtcMs >= stallWindowMs;
                }

                return EvaluateGhostPortalProgress(
                    progressDistance,
                    ref bestRouteDistance,
                    ref lastProgressUtcMs,
                    nowUtcMs,
                    stallWindowMs,
                    minProgressYards);
            }

            if (bestStraightDistance == float.MaxValue && bestRouteDistance != float.MaxValue)
            {
                bestStraightDistance = progressDistance;
                return lastProgressUtcMs > 0 && nowUtcMs - lastProgressUtcMs >= stallWindowMs;
            }

            return EvaluateGhostPortalProgress(
                progressDistance,
                ref bestStraightDistance,
                ref lastProgressUtcMs,
                nowUtcMs,
                stallWindowMs,
                minProgressYards);
        }

        internal static bool EvaluateGhostPortalProgress(
            float entranceDistance,
            ref float bestEntranceDistance,
            ref long lastProgressUtcMs,
            long nowUtcMs,
            long stallWindowMs = GhostPortalStallWindowMs,
            float minProgressYards = GhostPortalProgressMinYards)
        {
            if (bestEntranceDistance == float.MaxValue ||
                entranceDistance <= bestEntranceDistance - minProgressYards)
            {
                bestEntranceDistance = entranceDistance;
                lastProgressUtcMs = nowUtcMs;
                return false;
            }

            if (lastProgressUtcMs == 0)
            {
                lastProgressUtcMs = nowUtcMs;
                return false;
            }

            return nowUtcMs - lastProgressUtcMs >= stallWindowMs;
        }

        internal static bool ShouldDeferRest(bool hostileAggroNearby, bool hostileInMeleeRange, bool partyMemberInCombat) =>
            partyMemberInCombat || hostileAggroNearby || hostileInMeleeRange;

        private static float LowestPartyManaPercent(SquadLeaderState? leader)
        {
            if (leader?.Members == null)
                return 100f;

            float healerLowest = 100f;
            float anyLowest = 100f;
            bool sawHealer = false;

            foreach (var member in leader.Members)
            {
                if (!member.IsConnected || member.IsDead || member.IsGhost)
                    continue;
                bool usesMana = member.PowerType == WoWPowerType.Mana || member.Class == WoWClass.Druid;
                if (!usesMana)
                    continue;

                float percent = member.Class == WoWClass.Druid && member.ManaPercent > 0f ? member.ManaPercent : member.PowerPercent;
                if (percent < anyLowest)
                    anyLowest = percent;

                if (member.Role == SquadRole.Healer || member.Role == SquadRole.HybridOffHeal)
                {
                    sawHealer = true;
                    if (percent < healerLowest)
                        healerLowest = percent;
                }
            }

            return sawHealer ? healerLowest : anyLowest;
        }

        private static bool HostilesNearbyForRest(out bool aggro, out bool inMeleeRange)
        {
            aggro = false;
            inMeleeRange = false;
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            foreach (var unit in ObjectManager.CachedUnits)
            {
                if (unit == null || !unit.IsValid || unit.Dead)
                    continue;
                if (!unit.Attackable || unit.IsFriendly || unit.IsPlayer || unit.IsCritter || unit.IsNonCombatPet)
                    continue;
                if (unit.ControllingPlayer != null)
                    continue;

                float distance = unit.Location.Distance(me.Location);
                if (distance > RestThreatRadius)
                    continue;
                if (distance <= RestMeleeThreatRadius)
                    inMeleeRange = true;
                if (unit.IsTargetingMyPartyMember || (unit.Combat && unit.CurrentTargetGuid == me.Guid))
                    aggro = true;
            }

            return aggro || inMeleeRange;
        }

        private void StopMovementUnlessDungeonForcedStep(ulong leaderGuid, long nowUtcMs)
        {
            if (DungeonForcedStepActive(leaderGuid, nowUtcMs))
                return;

            var me = StyxWoW.Me;
            if (me != null && me.IsMoving)
                WoWMovement.MoveStop();
        }

        private static bool IsBossPullBlocked(SquadSettings settings, SquadLeaderState? leader, LocalPlayer me)
        {
            if (leader?.BotMode != SquadBotMode.Dungeon || !SquadDungeonService.IsInDungeonMap(me))
                return false;

            var currentBoss = global::Bots.DungeonBuddy.BossManager.CurrentBoss;
            if (currentBoss == null || currentBoss.IsDead)
                return false;

            var bossUnit = currentBoss.ToWoWUnit();
            WoWPoint bossLocation = bossUnit is { IsValid: true, IsAlive: true }
                ? bossUnit.Location
                : (global::Bots.DungeonBuddy.Profiles.ProfileManager.CurrentProfile?.BossEncounters
                    .FirstOrDefault(b => b.Entry == currentBoss.Entry)?.Location ?? WoWPoint.Zero);

            if (bossLocation == WoWPoint.Zero)
                return false;

            var pullPosition = me.Location;
            if (leader.EffectiveTankGuid != 0 && leader.EffectiveTankGuid != me.Guid &&
                ObjectManager.GetObjectByGuid<WoWPlayer>(leader.EffectiveTankGuid) is { IsValid: true, IsAlive: true } tank)
                pullPosition = tank.Location;

            float distToBoss = pullPosition.Distance(bossLocation);
            WoWUnit? firstTarget = Targeting.Instance.FirstUnit;
            if (firstTarget is { IsValid: true, IsAlive: true } && firstTarget.Location.Distance(bossLocation) < distToBoss)
                distToBoss = firstTarget.Location.Distance(bossLocation);

            return SquadDungeonService.IsBossPrePullBarrierBlocking(settings, leader, distToBoss);
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
                MapId = StyxWoW.Me != null && StyxWoW.IsInWorld ? StyxWoW.Me.MapId : null,
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

            if (!_hasObservedMapId || me.MapId != _lastObservedMapId)
            {
                bool changedFromKnownMap = _hasObservedMapId;
                _hasObservedMapId = true;
                _lastObservedMapId = me.MapId;
                if (changedFromKnownMap)
                {
                    _lastMapChangeUtcMs = now;
                    ClearDungeonForcedStep();
                    ResetDungeonStepFailures();
                    ClearDungeonAdvanceCache();
                    ResetDungeonCorridorBlock();
                    Navigator.Clear();
                }
            }
            bool inWorldTransition = IsTransientWorldTransition(StyxWoW.IsInWorld, _lastMapChangeUtcMs, now);
            bool isDead = me.Dead && !me.IsGhost && !inWorldTransition;
            bool isGhost = me.IsGhost && !inWorldTransition;
            bool insideDungeon = SquadDungeonService.IsInDungeonMap(me);
            if (isDead)
                _diedInsideDungeon ??= insideDungeon;
            else if (!inWorldTransition && !me.Dead && !me.IsGhost)
            {
                _deathStartedUtcMs = 0;
                _diedInsideDungeon = null;
                _deathDungeonId = 0;
                _deathDungeonEntrance = WoWPoint.Empty;
                _ghostEntrance = WoWPoint.Empty;
                _ghostEntranceDungeonId = 0;
                _ghostEntranceSource = string.Empty;
                _ghostPortalAttempts = 0;
                _ghostPortalApproach = WoWPoint.Empty;
                _ghostPathFailures = 0;
                _ghostBestRouteGeneration = 0;
                _ghostBestRouteDistance = float.MaxValue;
                _ghostBestEntranceDistance = float.MaxValue;
                _rejectedGhostEntranceUntilUtcMs.Clear();
                _lastGhostPosition = WoWPoint.Empty;
                _lastGhostProgressUtcMs = 0;
            }
            bool usesMana = me.PowerType == WoWPowerType.Mana;
            // Druids need water even while shapeshifted into a non-mana form; use the
            // same class-aware predicate as the town-run need evaluation for requests.
            bool isDruid = me.Class == WoWClass.Druid;
            bool usesManaForDrinks = usesMana || isDruid;
            float restPowerPercent = isDruid && me.MaxMana > 0 ? (float)me.ManaPercent : (float)me.PowerPercent;

            _localWasNeedingRest = !me.Combat && !isDead && !isGhost && !me.IsSwimming &&
                SquadCoordinatorService.EvaluateRestHysteresis(
                    _localWasNeedingRest,
                    (float)me.HealthPercent,
                    restPowerPercent,
                    usesManaForDrinks,
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
                HasValidMap = StyxWoW.IsInWorld,
                ZoneId = me.ZoneId,
                X = me.Location.X,
                Y = me.Location.Y,
                Z = me.Location.Z,
                HealthPercent = (float)me.HealthPercent,
                PowerPercent = (float)me.PowerPercent,
                ManaPercent = (float)me.ManaPercent,
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

                new Decorator(
                    ctx => IsTransientWorldTransition(
                        StyxWoW.IsInWorld,
                        _lastMapChangeUtcMs,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    new Action(ctx =>
                    {
                        SquadPartySyncService.LogAction("Waiting: world transition");
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
                           !((SquadContext.ActiveSettings ?? SquadSettings.Instance).Role == SquadRole.Healer &&
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
                    ctx => IsRestingPhaseActive() && !IsProvisioningTickNeeded(),
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
            if (settings != null && !settings.IsCoordinator)
            {
                if (!SquadContext.IsIpcConnected)
                    return true;

                var me = StyxWoW.Me;
                bool isDeadOrGhost = me != null && (me.Dead || me.IsGhost);
                if (leader == null && !isDeadOrGhost && (me == null || !me.Combat))
                    return true;
            }
            return false;
        }

        private RunStatus RunDeathAndCorpseBehavior()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            if (IsTransientWorldTransition(StyxWoW.IsInWorld, _lastMapChangeUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                return RunStatus.Success;
            }

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var leader = SquadContext.GetLeaderStateSnapshot();

            if (!me.IsGhost)
            {
                _diedInsideDungeon ??= SquadDungeonService.IsInDungeonMap(me);
                if (_diedInsideDungeon == true && _deathDungeonId == 0)
                {
                    var deathDungeon = global::Bots.DungeonBuddy.DungeonManager.CurrentDungeon;
                    _deathDungeonId = deathDungeon?.DungeonId ?? leader?.ActiveDungeonId ?? 0U;
                    _deathDungeonEntrance = deathDungeon?.Entrance ?? WoWPoint.Empty;
                    Logging.WriteDebug("[PartyGrind CorpseRun {0}] Died in dungeonId={1} map={2} entrance=({3:F1},{4:F1},{5:F1})",
                        me.Name, _deathDungeonId, me.MapId, _deathDungeonEntrance.X, _deathDungeonEntrance.Y, _deathDungeonEntrance.Z);
                }
                if (_deathStartedUtcMs == 0)
                    _deathStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long deadElapsed = nowUtcMs - _deathStartedUtcMs;
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
            long ghostNowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WoWPoint ghostPosition = me.Location;

            WoWPoint corpsePoint = me.CorpsePoint;
            if (SquadContext.IsDungeonModeActive && _diedInsideDungeon != false)
            {
                bool insideDungeon = SquadDungeonService.IsInDungeonMap(me);
                if (insideDungeon)
                {
                    _lastGhostPosition = WoWPoint.Empty;
                    _ghostBestEntranceDistance = float.MaxValue;
                    _lastGhostProgressUtcMs = 0;
                    _nextGhostPortalAttemptUtcMs = 0;
                    _nextGhostEntranceResolveUtcMs = 0;
                    _ghostEntrance = WoWPoint.Empty;
                    _ghostEntranceDungeonId = 0;
                    _ghostPortalAttempts = 0;
                    _ghostPortalApproach = WoWPoint.Empty;
                    _ghostPathFailures = 0;
                    _ghostBestRouteGeneration = 0;
                    _ghostBestRouteDistance = float.MaxValue;
                    _rejectedGhostEntranceUntilUtcMs.Clear();
                }
                else
                {
                    WoWPoint dungeonEntrance = ResolveGhostDungeonEntrance(me, leader);
                    if (dungeonEntrance != WoWPoint.Zero)
                    {
                        float entranceDistance = me.Location.Distance(dungeonEntrance);
                        if (entranceDistance > 6f)
                        {
                            _ghostPortalApproach = ghostPosition;
                            _lastGhostPosition = ghostPosition;
                            MoveResult result = Navigator.MoveTo(dungeonEntrance, "Dungeon CorpseRun: returning to portal");
                            bool routeMatchesEntrance = Navigator.Destination != WoWPoint.Zero &&
                                                        Navigator.Destination.Distance(dungeonEntrance) <= 2f;
                            WoWPoint[] remainingPath = result == MoveResult.Moved && routeMatchesEntrance
                                ? Navigator.GetRemainingNavPath()
                                : Array.Empty<WoWPoint>();
                            bool hasRoutePath = result == MoveResult.Moved && routeMatchesEntrance && remainingPath.Length > 0;
                            float progressMetric = ComputeGhostPortalProgressDistance(
                                me.Location,
                                dungeonEntrance,
                                hasRoutePath ? remainingPath : null);
                            bool stalled = EvaluateGhostPortalProgress(
                                progressMetric,
                                hasRoutePath,
                                Navigator.CurrentPathGeneration,
                                ref _ghostBestRouteGeneration,
                                ref _ghostBestRouteDistance,
                                ref _ghostBestEntranceDistance,
                                ref _lastGhostProgressUtcMs,
                                ghostNowUtcMs);

                            if (result != MoveResult.ReachedDestination && (stalled || ghostNowUtcMs >= _nextGhostPortalAttemptUtcMs))
                            {
                                _nextGhostPortalAttemptUtcMs = ghostNowUtcMs + 5000L;
                                Logging.WriteDebug("[PartyGrind CorpseRun {0}] Portal approach distance={1:F1} result={2} stalled={3} pos=({4:F1},{5:F1},{6:F1}) portal=({7:F1},{8:F1},{9:F1}) attempt={10}",
                                    me.Name, entranceDistance, result, stalled, me.Location.X, me.Location.Y, me.Location.Z,
                                    dungeonEntrance.X, dungeonEntrance.Y, dungeonEntrance.Z, ++_ghostPortalAttempts);
                            }

                            bool failedMove = result == MoveResult.PathGenerationFailed || result == MoveResult.Failed;
                            if (stalled || (failedMove && ++_ghostPathFailures >= GhostPortalMaxFailedMoves))
                            {
                                if (!string.IsNullOrEmpty(_ghostEntranceSource))
                                    _rejectedGhostEntranceUntilUtcMs[_ghostEntranceSource] = ghostNowUtcMs + 30000L;
                                _ghostEntrance = WoWPoint.Empty;
                                _ghostEntranceSource = string.Empty;
                                _nextGhostEntranceResolveUtcMs = 0;
                                _ghostPathFailures = 0;
                                _ghostBestRouteGeneration = 0;
                                _ghostBestRouteDistance = float.MaxValue;
                                _ghostBestEntranceDistance = float.MaxValue;
                                _lastGhostProgressUtcMs = 0;
                                Navigator.Clear();
                            }
                            else if (result is MoveResult.Moved or MoveResult.ReachedDestination)
                            {
                                _ghostPathFailures = 0;
                            }
                            return RunStatus.Success;
                        }

                        if (ghostNowUtcMs >= _nextGhostPortalAttemptUtcMs)
                        {
                            var portalObject = ObjectManager.GetObjectsOfType<WoWGameObject>()
                                .FirstOrDefault(o => o != null && o.IsValid && IsDungeonPortalEntry(o.Entry) &&
                                                     o.Location.Distance(me.Location) <= 40f);
                            if (portalObject != null)
                            {
                                SquadPartySyncService.LogAction("Dungeon CorpseRun: interacting with entrance portal");
                                portalObject.Interact();
                                Logging.WriteDebug("[PartyGrind CorpseRun {0}] Portal interact entry={1} distance={2:F1} pos=({3:F1},{4:F1},{5:F1}) attempt={6}",
                                    me.Name, portalObject.Entry, portalObject.Distance, me.Location.X, me.Location.Y, me.Location.Z,
                                    ++_ghostPortalAttempts);
                            }
                            else
                            {
                                WoWPoint approachHeading = me.Location.Distance(dungeonEntrance) > 0.5f
                                    ? me.Location
                                    : (_ghostPortalApproach != WoWPoint.Empty ? _ghostPortalApproach : me.Location);
                                WoWPoint stepThrough = ComputePortalStepThrough(approachHeading, dungeonEntrance);
                                SquadPartySyncService.LogAction("Dungeon CorpseRun: stepping through entrance portal");
                                WoWMovement.ClickToMove(stepThrough);
                                Logging.WriteDebug("[PartyGrind CorpseRun {0}] Portal step attempt={1} map={2} pos=({3:F1},{4:F1},{5:F1}) portal=({6:F1},{7:F1},{8:F1}) step=({9:F1},{10:F1},{11:F1})",
                                    me.Name, ++_ghostPortalAttempts, me.MapId, me.Location.X, me.Location.Y, me.Location.Z,
                                    dungeonEntrance.X, dungeonEntrance.Y, dungeonEntrance.Z, stepThrough.X, stepThrough.Y, stepThrough.Z);
                            }
                            _nextGhostPortalAttemptUtcMs = ghostNowUtcMs + 5000L;
                        }
                        return RunStatus.Success;
                    }

                    if (ghostNowUtcMs >= _nextGhostNoEntranceLogUtcMs)
                    {
                        Logging.WriteDebug("[PartyGrind CorpseRun {0}] No dungeon entrance target; falling back to corpse run. dungeonId={1} map={2} corpse=({3:F1},{4:F1},{5:F1})",
                            me.Name, leader?.ActiveDungeonId ?? 0U, me.MapId, corpsePoint.X, corpsePoint.Y, corpsePoint.Z);
                        _nextGhostNoEntranceLogUtcMs = ghostNowUtcMs + 10000L;
                    }
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

        private static bool IsDungeonPortalEntry(uint entry) => entry is 192507U or 207896U;

        private static bool IsUsablePoint(WoWPoint point) =>
            point.IsValid && point != WoWPoint.Empty && point != WoWPoint.Zero;

        internal static WoWPoint ComputePortalStepThrough(WoWPoint current, WoWPoint entrance)
        {
            float dx = entrance.X - current.X;
            float dy = entrance.Y - current.Y;
            float horizontalLength = MathF.Sqrt(dx * dx + dy * dy);
            return horizontalLength > 0.1f
                ? new WoWPoint(entrance.X + dx / horizontalLength * 4f, entrance.Y + dy / horizontalLength * 4f, entrance.Z)
                : entrance;
        }

        private WoWPoint ResolveGhostDungeonEntrance(LocalPlayer me, SquadLeaderState? leader)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            uint dungeonId = _deathDungeonId != 0 ? _deathDungeonId : leader?.ActiveDungeonId ?? 0U;
            if (now < _nextGhostEntranceResolveUtcMs && IsUsablePoint(_ghostEntrance) && _ghostEntranceDungeonId == dungeonId)
                return _ghostEntrance;

            _nextGhostEntranceResolveUtcMs = now + 5000L;
            _ghostEntranceDungeonId = dungeonId;
            WoWPoint previousEntrance = _ghostEntrance;
            string previousSource = _ghostEntranceSource;
            string source;

            var portalObject = ObjectManager.GetObjectsOfType<WoWGameObject>()
                .FirstOrDefault(o => o != null && o.IsValid && IsDungeonPortalEntry(o.Entry) &&
                                     o.Location.Distance(me.Location) <= 60f);
            if (portalObject != null && !IsGhostSourceRejected("portal-object", now))
            {
                _ghostEntrance = portalObject.Location;
                source = "portal-object";
            }
            else if (IsUsablePoint(_deathDungeonEntrance) && !IsGhostSourceRejected("death-script", now))
            {
                _ghostEntrance = _deathDungeonEntrance;
                source = "death-script";
            }
            else if (dungeonId != 0 && IsUsablePoint(global::Bots.DungeonBuddy.DungeonManager.GetEntranceForDungeon(dungeonId)) && !IsGhostSourceRejected("dungeon-script", now))
            {
                _ghostEntrance = global::Bots.DungeonBuddy.DungeonManager.GetEntranceForDungeon(dungeonId);
                source = "dungeon-script";
            }
            else if (IsUsablePoint(_dungeon.GetGhostDungeonEntranceTarget()) && !IsGhostSourceRejected("leader-portal", now))
            {
                _ghostEntrance = _dungeon.GetGhostDungeonEntranceTarget();
                source = "leader-portal";
            }
            else if (IsUsablePoint(me.InstanceCorpseLocation) && !IsGhostSourceRejected("ghost-entrance", now))
            {
                _ghostEntrance = me.InstanceCorpseLocation;
                source = "ghost-entrance";
            }
            else
            {
                _ghostEntrance = WoWPoint.Zero;
                source = "none";
            }

            if (!string.Equals(source, previousSource, StringComparison.Ordinal) ||
                _ghostEntrance.Distance(previousEntrance) > 1f)
            {
                Logging.WriteDebug("[PartyGrind CorpseRun {0}] Entrance target source={1} dungeonId={2} target=({3:F1},{4:F1},{5:F1})",
                    me.Name, source, dungeonId, _ghostEntrance.X, _ghostEntrance.Y, _ghostEntrance.Z);
                _ghostEntranceSource = source;
                _ghostBestRouteGeneration = 0;
                _ghostBestRouteDistance = float.MaxValue;
                _ghostBestEntranceDistance = float.MaxValue;
                _lastGhostProgressUtcMs = 0;
                _ghostPathFailures = 0;
            }

            return _ghostEntrance;
        }

        internal static (bool Known, WoWPoint Position) TryGetEffectiveTankPosition(SquadLeaderState? leader, uint myMapId)
        {
            if (leader == null)
                return (false, WoWPoint.Zero);

            if (leader.EffectiveTankGuid != 0)
            {
                if (leader.LeaderGuid == leader.EffectiveTankGuid && leader.MapId == myMapId && !leader.IsDead && !leader.IsGhost)
                    return (true, new WoWPoint(leader.X, leader.Y, leader.Z));

                var tankMember = leader.Members?.FirstOrDefault(m => m.Guid == leader.EffectiveTankGuid);
                if (tankMember != null && tankMember.IsConnected && !tankMember.IsDead && !tankMember.IsGhost && tankMember.MapId == myMapId)
                    return (true, new WoWPoint(tankMember.X, tankMember.Y, tankMember.Z));

                var pugTank = ObjectManager.GetObjectByGuid<WoWPlayer>(leader.EffectiveTankGuid);
                if (pugTank != null && pugTank.IsValid && !pugTank.Dead)
                    return (true, pugTank.Location);

                return (false, WoWPoint.Zero);
            }

            var designatedTank = leader.Members?.FirstOrDefault(m => m.Role == SquadRole.TankLeader);
            if (designatedTank != null && designatedTank.IsConnected && !designatedTank.IsDead && !designatedTank.IsGhost && designatedTank.MapId == myMapId)
                return (true, new WoWPoint(designatedTank.X, designatedTank.Y, designatedTank.Z));

            if (leader.MapId == myMapId && leader.EffectiveTankGuid == 0 && !leader.IsDead && !leader.IsGhost)
                return (true, new WoWPoint(leader.X, leader.Y, leader.Z));

            return (false, WoWPoint.Zero);
        }

        internal static bool ShouldClearCoordinatorTargetsWhenDead(bool isDead, bool isGhost)
        {
            return isDead || isGhost;
        }

        internal static float GetTrueDungeonMeleeReach(float selfCombatReach, float targetCombatReach, bool targetIsPlayer = false)
        {
            return targetIsPlayer ? 3.5f : MathF.Max(5f, selfCombatReach + 1.3333334f + targetCombatReach);
        }

        internal static bool IsDungeonMeleeInTrueSpellRange(float distanceToTarget, float selfCombatReach, float targetCombatReach, bool targetIsPlayer = false)
        {
            return distanceToTarget <= GetTrueDungeonMeleeReach(selfCombatReach, targetCombatReach, targetIsPlayer);
        }

        internal static bool CanSwitchDungeonMeleeToNearbyAdd(
            bool primaryInMeleeRange,
            bool tankKnown,
            float tankDistanceToPrimary,
            float primaryMeleeGateReach,
            float candidateDistanceToSelf,
            float candidateDistanceToTank,
            float candidateMeleeGateReach = 0f)
        {
            if (primaryInMeleeRange)
                return false;
            if (!tankKnown || tankDistanceToPrimary <= primaryMeleeGateReach)
                return false;
            float addGate = candidateMeleeGateReach > 0f ? candidateMeleeGateReach : primaryMeleeGateReach;
            return candidateDistanceToSelf <= addGate || candidateDistanceToTank <= addGate;
        }

        private static bool TrySelectNearbyEngagedDungeonAdd(
            LocalPlayer me,
            bool tankKnown,
            WoWPoint tankPos,
            SquadLeaderState? leader,
            ulong primaryGuid,
            float tankDistToPrimary,
            float primaryMeleeGateReach,
            out WoWUnit? selectedAdd)
        {
            selectedAdd = null;
            if (!tankKnown || tankDistToPrimary <= primaryMeleeGateReach)
                return false;

            WoWUnit? best = null;
            float bestDist = float.MaxValue;
            var memberGuids = SquadContext.GetKnownMemberGuids();

            void ConsiderCandidate(WoWUnit? candidate)
            {
                if (candidate == null || !candidate.IsValid || candidate.Dead || !candidate.CanSelect || candidate.Guid == primaryGuid)
                    return;
                if (!candidate.Attackable || candidate.IsFriendly || candidate.IsPlayer)
                    return;
                bool engagedWithSquad = candidate.Combat &&
                                        (candidate.IsTargetingMyPartyMember ||
                                         candidate.IsTargetingMeOrPet ||
                                         candidate.TaggedByMe ||
                                         (candidate.CurrentTargetGuid != 0 && memberGuids.Contains(candidate.CurrentTargetGuid)));
                if (!engagedWithSquad)
                    return;

                float distSelf = (float)candidate.Distance;
                float distTank = (float)candidate.Location.Distance(tankPos);
                float candidateGate = GetTrueDungeonMeleeReach(me.CombatReach, candidate.CombatReach, candidate.IsPlayer);
                if (!CanSwitchDungeonMeleeToNearbyAdd(false, tankKnown, tankDistToPrimary, primaryMeleeGateReach, distSelf, distTank, candidateGate))
                    return;
                if (distSelf >= bestDist)
                    return;
                if (global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(candidate.Location))
                    return;
                if (distSelf > candidateGate && !Navigator.CanNavigateFully(me.Location, candidate.Location))
                    return;

                best = candidate;
                bestDist = distSelf;
            }

            if (leader != null && leader.SecondaryTargetGuid != 0 && leader.SecondaryTargetGuid != primaryGuid)
                ConsiderCandidate(ObjectManager.GetObjectByGuid<WoWUnit>(leader.SecondaryTargetGuid));

            foreach (WoWUnit unit in Targeting.Instance.TargetList)
                ConsiderCandidate(unit);

            selectedAdd = best;
            return selectedAdd != null;
        }

        internal static bool ShouldBlockDungeonMeleeRoutineFromRange(bool inDungeon, bool isMelee, bool isWithinMeleeRange)
        {
            return inDungeon && isMelee && !isWithinMeleeRange;
        }

        internal static bool ResolveDungeonMeleeMoveTarget(
            WoWPoint myLocation,
            WoWPoint meleePos,
            bool safeMeleePos,
            WoWPoint targetLocation,
            bool safeTargetPos,
            bool tankKnown,
            WoWPoint tankLocation,
            float meleeGateReach,
            out WoWPoint moveTarget,
            out bool isTankSideMove)
        {
            const float ArrivalTolerance = 0.8f;
            const float TightArrivalTolerance = 0.05f;

            if (safeMeleePos && myLocation.Distance(meleePos) > ArrivalTolerance)
            {
                moveTarget = meleePos;
                isTankSideMove = true;
                return true;
            }

            bool holdBehindApproachingTank = tankKnown && tankLocation.Distance(targetLocation) > meleeGateReach;
            if (!holdBehindApproachingTank && safeTargetPos && myLocation.Distance(targetLocation) > ArrivalTolerance)
            {
                moveTarget = targetLocation;
                isTankSideMove = false;
                return true;
            }

            if (!holdBehindApproachingTank && safeMeleePos && myLocation.Distance(meleePos) > TightArrivalTolerance)
            {
                moveTarget = meleePos;
                isTankSideMove = true;
                return true;
            }

            moveTarget = WoWPoint.Empty;
            isTankSideMove = false;
            return false;
        }

        internal static bool HasUnreachableDungeonMeleeApproach(bool safeMeleePos, bool safeTargetPos, MoveResult lastMoveResult)
        {
            if (!safeMeleePos && !safeTargetPos)
                return true;

            return lastMoveResult == MoveResult.PathGenerationFailed || lastMoveResult == MoveResult.Failed;
        }

        internal static bool IsFailedDungeonMeleePrimaryActive(ulong failedTargetGuid, long failedUntilUtcMs, ulong targetGuid, long nowUtcMs)
        {
            return failedTargetGuid != 0UL && failedTargetGuid == targetGuid && nowUtcMs < failedUntilUtcMs;
        }

        private static bool TryResolveSafeDungeonMeleePosition(
            LocalPlayer me,
            WoWUnit combatTarget,
            bool tankKnown,
            WoWPoint tankPosForEngage,
            float meleeFlankRadius,
            float meleeGateReach,
            int slotIdx,
            int slotCount,
            bool bypassFailedDest,
            WoWPoint failedDest,
            out WoWPoint meleePos)
        {
            meleePos = tankKnown
                ? SquadFormationService.ComputeDungeonMeleePosition(
                    combatTarget.Location, tankPosForEngage, combatTarget.Rotation, meleeFlankRadius, slotIdx, slotCount, combatTarget.CombatReach)
                : combatTarget.Location;
            bool slotFailed = bypassFailedDest && meleePos.DistanceSqr(failedDest) <= 1.0f;
            bool withinGate = !tankKnown ||
                              combatTarget.Location.Distance(tankPosForEngage) > meleeGateReach ||
                              combatTarget.Location.Distance(meleePos) <= meleeGateReach;
            bool safePos = !slotFailed &&
                           withinGate &&
                           !global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(meleePos) &&
                           Navigator.CanNavigateFully(me.Location, meleePos);
            bool tankFailed = bypassFailedDest && tankPosForEngage.DistanceSqr(failedDest) <= 1.0f;
            if (!safePos && tankKnown && !tankFailed &&
                !global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(tankPosForEngage) &&
                Navigator.CanNavigateFully(me.Location, tankPosForEngage))
            {
                meleePos = tankPosForEngage;
                safePos = true;
            }
            return safePos;
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
                    var (tankKnown, tankPos) = TryGetEffectiveTankPosition(leader, me.MapId);
                    float tankDist = tankKnown ? tankPos.Distance(focus.Location) : 999f;
                    if (CanFollowerEngageFocus(me.Combat, leader.InCombat, focus.Combat, focus.IsTargetingMyPartyMember, tankDist))
                        return true;
                }
            }

            if (settings.Role == SquadRole.Healer && SquadHealBridge.HasHealCandidate(leader))
                return true;
            if (settings.Role == SquadRole.HybridOffHeal &&
                SquadHealBridge.HasHealCandidate(leader, emergencyOnly: true))
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
                    if (tankFocusGuid == 0 && !me.Combat && HasLivingAllyInCombat(leader, me.Guid))
                        return RunStatus.Failure;
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
                    if (me.Combat || leader?.InCombat == true)
                    {
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        return RunStatus.Success;
                    }
                    return RunStatus.Failure;

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
                var (tankKnown, tankPosForEngage) = TryGetEffectiveTankPosition(leader, me.MapId);
                float tankDistToFocus = tankKnown ? tankPosForEngage.Distance(focus.Location) : 999f;
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
                var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
                ulong combatGuid = focus.Guid;
                WoWUnit petTarget = focus;
                if (isMelee)
                {
                    bool inDungeon = SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(me);
                    if (inDungeon)
                    {
                        var memberGuids = SquadContext.GetKnownMemberGuids();
                        WoWUnit? currentUnit = me.CurrentTarget;
                        bool keepCurrentEngagedAdd = currentUnit != null &&
                                                     currentUnit.IsValid &&
                                                     !currentUnit.Dead &&
                                                     currentUnit.CanSelect &&
                                                     currentUnit.Attackable &&
                                                     !currentUnit.IsFriendly &&
                                                     !currentUnit.IsPlayer &&
                                                     currentUnit.Guid != focus.Guid &&
                                                     currentUnit.Combat &&
                                                     (currentUnit.IsTargetingMyPartyMember ||
                                                      currentUnit.IsTargetingMeOrPet ||
                                                      currentUnit.TaggedByMe ||
                                                      (currentUnit.CurrentTargetGuid != 0 && memberGuids.Contains(currentUnit.CurrentTargetGuid))) &&
                                                     (currentTargetIsValidAdd ||
                                                      (!IsDungeonMeleeInTrueSpellRange((float)focus.Distance, me.CombatReach, focus.CombatReach, focus.IsPlayer) &&
                                                       tankKnown &&
                                                       tankPosForEngage.Distance(focus.Location) > GetTrueDungeonMeleeReach(me.CombatReach, focus.CombatReach, focus.IsPlayer) &&
                                                       (IsDungeonMeleeInTrueSpellRange((float)currentUnit.Distance, me.CombatReach, currentUnit.CombatReach, currentUnit.IsPlayer) ||
                                                        ((currentUnit.Location.Distance(tankPosForEngage) <= GetTrueDungeonMeleeReach(me.CombatReach, currentUnit.CombatReach, currentUnit.IsPlayer)) &&
                                                         !global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(currentUnit.Location) &&
                                                         Navigator.CanNavigateFully(me.Location, currentUnit.Location)))));

                        WoWUnit combatTarget = keepCurrentEngagedAdd && currentUnit != null ? currentUnit : focus;
                        float meleeGateReach = GetTrueDungeonMeleeReach(me.CombatReach, combatTarget.CombatReach, combatTarget.IsPlayer);
                        bool inTrueMelee = IsDungeonMeleeInTrueSpellRange((float)combatTarget.Distance, me.CombatReach, combatTarget.CombatReach, combatTarget.IsPlayer);
                        if (!inTrueMelee && tankKnown && combatTarget.Guid == focus.Guid &&
                            TrySelectNearbyEngagedDungeonAdd(
                                me,
                                tankKnown,
                                tankPosForEngage,
                                leader,
                                combatTarget.Guid,
                                (float)tankPosForEngage.Distance(combatTarget.Location),
                                meleeGateReach,
                                out WoWUnit? nearbyAdd) &&
                            nearbyAdd != null)
                        {
                            combatTarget = nearbyAdd;
                        }

                        if (me.CurrentTargetGuid != combatTarget.Guid)
                            combatTarget.Target();

                        if (me.CurrentTargetGuid != combatTarget.Guid && combatTarget.Guid != focus.Guid)
                        {
                            combatTarget = focus;
                            if (me.CurrentTargetGuid != combatTarget.Guid)
                                combatTarget.Target();
                        }

                        combatGuid = combatTarget.Guid;
                        petTarget = combatTarget;
                        meleeGateReach = GetTrueDungeonMeleeReach(me.CombatReach, combatTarget.CombatReach, combatTarget.IsPlayer);
                        inTrueMelee = IsDungeonMeleeInTrueSpellRange((float)combatTarget.Distance, me.CombatReach, combatTarget.CombatReach, combatTarget.IsPlayer);

                        if (me.GotAlivePet)
                        {
                            WoWPetControl.Attack(petTarget);
                        }

                        bool canMove = global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled;
                        if (ShouldBlockDungeonMeleeRoutineFromRange(inDungeon, isMelee, inTrueMelee))
                        {
                            if (canMove)
                            {
                                long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                bool bypassPrimary = IsFailedDungeonMeleePrimaryActive(
                                    _failedDungeonMeleeTargetGuid,
                                    _failedDungeonMeleeUntilUtcMs,
                                    combatTarget.Guid,
                                    nowUtcMs);
                                bool bypassSlot = bypassPrimary && !_failedDungeonMeleeDirect;
                                WoWPoint meleePos = combatTarget.Location;
                                bool safePos = TryResolveSafeDungeonMeleePosition(
                                    me,
                                    combatTarget,
                                    tankKnown,
                                    tankPosForEngage,
                                    settings.MeleeFlankRadius,
                                    meleeGateReach,
                                    slotIdx,
                                    slotCount,
                                    bypassSlot,
                                    _failedDungeonMeleeDest,
                                    out meleePos);
                                if (!safePos)
                                    meleePos = combatTarget.Location;

                                bool directFailed = bypassPrimary && _failedDungeonMeleeDirect;
                                bool safeTargetPos = !directFailed &&
                                                     !global::Bots.DungeonBuddy.Avoidance.AvoidanceManager.IsInAvoidance(combatTarget.Location) &&
                                                     Navigator.CanNavigateFully(me.Location, combatTarget.Location);
                                if (ResolveDungeonMeleeMoveTarget(
                                        me.Location,
                                        meleePos,
                                        safePos,
                                        combatTarget.Location,
                                        safeTargetPos,
                                        tankKnown,
                                        tankPosForEngage,
                                        meleeGateReach,
                                        out WoWPoint moveTarget,
                                        out bool isTankSideMove))
                                {
                                    float movePrecision = isTankSideMove && me.Location.Distance(meleePos) <= 0.8f ? 0.05f : 0.75f;
                                    SquadPartySyncService.LogAction(
                                        (isTankSideMove ? "Moving to tank-side melee position " : "Moving to melee ") + combatTarget.Name);
                                    MoveResult moveResult = Navigator.MoveTo(
                                        moveTarget,
                                        movePrecision,
                                        (isTankSideMove ? "Dungeon melee position " : "Moving to ") + combatTarget.Name);

                                    if (isTankSideMove && moveResult == MoveResult.ReachedDestination)
                                    {
                                        if (ResolveDungeonMeleeMoveTarget(
                                                me.Location,
                                                meleePos,
                                                safeMeleePos: false,
                                                combatTarget.Location,
                                                safeTargetPos,
                                                tankKnown,
                                                tankPosForEngage,
                                                meleeGateReach,
                                                out WoWPoint fallbackTarget,
                                                out _) &&
                                            me.Location.Distance(fallbackTarget) > 0.8f)
                                        {
                                            SquadPartySyncService.LogAction("Moving to melee " + combatTarget.Name);
                                            moveResult = Navigator.MoveTo(fallbackTarget, 0.75f, "Moving to " + combatTarget.Name);
                                            if (moveResult == MoveResult.PathGenerationFailed || moveResult == MoveResult.Failed)
                                            {
                                                _failedDungeonMeleeTargetGuid = combatTarget.Guid;
                                                _failedDungeonMeleeUntilUtcMs = nowUtcMs + 2500L;
                                                _failedDungeonMeleeDirect = true;
                                            }
                                        }
                                        else if (me.IsMoving)
                                        {
                                            WoWMovement.MoveStop();
                                        }
                                    }
                                    else if (moveResult == MoveResult.PathGenerationFailed || moveResult == MoveResult.Failed)
                                    {
                                        _failedDungeonMeleeTargetGuid = combatTarget.Guid;
                                        _failedDungeonMeleeUntilUtcMs = nowUtcMs + 2500L;
                                        if (isTankSideMove)
                                        {
                                            _failedDungeonMeleeDest = moveTarget;
                                            _failedDungeonMeleeDirect = false;
                                        }
                                        else
                                        {
                                            _failedDungeonMeleeDirect = true;
                                        }
                                    }
                                    else if (isTankSideMove || _failedDungeonMeleeTargetGuid != combatTarget.Guid)
                                    {
                                        _failedDungeonMeleeTargetGuid = 0UL;
                                        _failedDungeonMeleeDest = WoWPoint.Zero;
                                        _failedDungeonMeleeDirect = false;
                                    }

                                    if (HasUnreachableDungeonMeleeApproach(safePos, safeTargetPos, moveResult) && me.IsMoving)
                                    {
                                        WoWMovement.MoveStop();
                                    }
                                }
                                else if (me.IsMoving)
                                {
                                    WoWMovement.MoveStop();
                                }
                            }
                            return RunStatus.Success;
                        }

                        if (_activeCombatRoutine == null && canMove && tankKnown &&
                            SquadFormationService.IsPositionAheadOfTank(me.Location, combatTarget.Location, tankPosForEngage) &&
                            TryResolveSafeDungeonMeleePosition(
                                me,
                                combatTarget,
                                tankKnown,
                                tankPosForEngage,
                                settings.MeleeFlankRadius,
                                meleeGateReach,
                                slotIdx,
                                slotCount,
                                bypassFailedDest: false,
                                failedDest: WoWPoint.Zero,
                                out WoWPoint aheadMeleePos) &&
                            me.Location.Distance(aheadMeleePos) > 1.5f)
                        {
                            SquadPartySyncService.LogAction("Moving to tank-side melee position " + combatTarget.Name);
                            MoveResult repositionResult = Navigator.MoveTo(aheadMeleePos, 0.75f, "Dungeon melee position " + combatTarget.Name);
                            if (repositionResult != MoveResult.PathGenerationFailed && repositionResult != MoveResult.Failed)
                                return RunStatus.Success;
                        }
                    }
                    else
                    {
                        if (me.CurrentTargetGuid != focus.Guid && !currentTargetIsValidAdd)
                            focus.Target();

                        if (me.GotAlivePet)
                        {
                            WoWPetControl.Attack(petTarget);
                        }

                        bool canFlank = _activeCombatRoutine == null;
                        if (canFlank && focus.Distance <= 12f && !focus.IsWithinMeleeRange)
                        {
                            WoWPoint flankPos = SquadFormationService.ComputeMeleeFlankPosition(
                                focus.Location, focus.Rotation, settings.MeleeFlankRadius, slotIdx, slotCount, focus.CombatReach);
                            if (me.Location.Distance2D(flankPos) > 2.2f &&
                                Navigator.CanNavigateFully(me.Location, flankPos))
                            {
                                SquadPartySyncService.LogAction("Moving to flank " + focus.Name);
                                MoveResult flankMoveResult = Navigator.MoveTo(flankPos, "Flanking " + focus.Name);
                                if (flankMoveResult != MoveResult.PathGenerationFailed && flankMoveResult != MoveResult.Failed)
                                    return RunStatus.Success;
                            }
                        }
                    }
                }
                else
                {
                    if (me.CurrentTargetGuid != focus.Guid && !currentTargetIsValidAdd)
                        focus.Target();

                    if (me.GotAlivePet)
                    {
                        WoWPetControl.Attack(petTarget);
                    }

                    if (PositionBackline(settings.RangedStandoffDistance, leader, me, focus))
                        return RunStatus.Success;
                }

                return RunRoutineCombat(combatGuid);
            }

            return RunStatus.Failure;
        }

        private bool PositionBackline(float standoffDistance, SquadLeaderState? leader, LocalPlayer me, WoWUnit? selectedTarget = null)
        {
            if (leader == null || !leader.HasValidMap || leader.MapId != me.MapId || leader.IsDead || leader.IsGhost)
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
                        if (!me.IsMoving && !me.IsSafelyFacing(unit, 70f))
                            unit.Face();
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

            if (me.CurrentTarget != null && !me.IsMoving && !me.IsSafelyFacing(me.CurrentTarget, 70f))
                me.CurrentTarget.Face();

            SquadPartySyncService.LogAction((treatAsCombat ? "Combat routine" : "Pull routine") +
                (me.CurrentTarget != null ? ": " + me.CurrentTarget.Name : string.Empty));
            _combatTicked = true;
            RunStatus status = TickRoutine(ref _activeCombatRoutine, behavior);
            return status == RunStatus.Failure ? RunStatus.Success : status;
        }

        private static bool IsRestingPhaseActive()
        {
            var me = StyxWoW.Me;
            if (me == null || me.Combat || me.Dead || me.IsGhost || me.IsSwimming)
                return false;

            var leader = SquadContext.GetLeaderStateSnapshot();
            bool partyMemberInCombat = leader?.Members?.Any(m => m.Guid != me.Guid && m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
            if (HostilesNearbyForRest(out bool hostileAggroNearby, out bool hostileInMeleeRange) &&
                ShouldDeferRest(hostileAggroNearby, hostileInMeleeRange, partyMemberInCombat))
                return false;

            if (leader != null && leader.Phase == SquadPhase.Resting)
                return true;

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            float currentPowerPct = me.Class == WoWClass.Druid && me.MaxMana > 0 ? (float)me.ManaPercent : (float)me.PowerPercent;
            return SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true ||
                GetBossRecoveryManaTarget(settings, leader, me) > currentPowerPct;
        }

        private static int GetBossRecoveryManaTarget(SquadSettings settings, SquadLeaderState? leader, LocalPlayer me) =>
            (settings.Role == SquadRole.Healer || settings.Role == SquadRole.HybridOffHeal) &&
            (me.PowerType == WoWPowerType.Mana || me.Class == WoWClass.Druid) && IsBossPullBlocked(settings, leader, me)
                ? SquadDungeonService.GetBossReadinessManaPercent(settings)
                : 0;

        private static RunStatus RunRestBehavior()
        {
            SquadPartySyncService.LogAction("Resting");
            var me = StyxWoW.Me!;
            if (me.IsSwimming)
            {
                SquadPartySyncService.LogAction("Resting deferred while swimming");
                return RunStatus.Failure;
            }

            if (me.IsMoving)
                WoWMovement.MoveStop();

            if (me.Mounted)
                Mount.Dismount("Resting");

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            if (SquadProvisioningService.TryConjureForSelf(settings))
                return RunStatus.Success;
            if (me.HealthPercent < settings.ResumeRestHealthPct && !me.HasAura("Food") && !me.HasAura("Refreshment"))
                Rest.FeedImmediate();

            bool isDruid = me.Class == WoWClass.Druid;
            bool usesMana = me.PowerType == WoWPowerType.Mana || isDruid;
            float currentManaPct = isDruid && me.MaxMana > 0 ? (float)me.ManaPercent : (float)me.PowerPercent;
            int manaTarget = Math.Max(settings.ResumeRestManaPct,
                GetBossRecoveryManaTarget(settings, SquadContext.GetLeaderStateSnapshot(), me));
            if (usesMana && currentManaPct < manaTarget && !me.HasAura("Drink") && !me.HasAura("Refreshment"))
            {
                if (isDruid && me.Shapeshift != ShapeshiftForm.Normal)
                    Mount.ClearShapeshift();

                if (me.PowerType == WoWPowerType.Mana || me.Shapeshift == ShapeshiftForm.Normal)
                    Rest.DrinkImmediate();
            }

            var restBehavior = RoutineManager.Current?.RestBehavior;
            if (restBehavior != null && (me.HealthPercent < settings.ResumeRestHealthPct || (usesMana && currentManaPct < manaTarget)))
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
                if (!leader.HasValidMap || !IsSameMapForNavigation(me.MapId, leader.MapId))
                    return false;
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
            var me = StyxWoW.Me;
            return LootTargeting.Instance.LootingList.FirstOrDefault(o =>
            {
                if (o == null || !o.IsValid || LootRetry.ContainsKey(o.Guid))
                    return false;
                if (leader != null && (me == null || !leader.HasValidMap || !IsSameMapForNavigation(me.MapId, leader.MapId) ||
                                       o.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z)) > 25f))
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
                    bool followerCombat = leader?.Members?.Any(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
                    if (followerCombat)
                    {
                        ClearPullCommit();
                    }
                    else
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
                }

                if (SquadContext.IsDungeonModeActive && !isDungeon)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    return RunStatus.Success;
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

                    bool followerCombat = leader?.Members?.Any(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
                    if (!followerCombat && SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
                    {
                        WoWUnit? dungeonTarget = Targeting.Instance.FirstUnit;
                        if (dungeonTarget != null && dungeonTarget.IsValid && !dungeonTarget.Dead &&
                            dungeonTarget.Attackable && !dungeonTarget.IsFriendly)
                        {
                            WoWUnit? isolatedTarget = SquadTankBridge.SelectIsolatedPullTarget(
                                dungeonTarget, me.Location, SquadTankBridge.MaxPullCandidateScanRadius);
                            if (isolatedTarget != null && isolatedTarget.Guid != dungeonTarget.Guid)
                            {
                                SquadPartySyncService.LogAction("Dungeon: pulling " + isolatedTarget.Name +
                                    " instead of " + dungeonTarget.Name + " to keep the pack small");
                                dungeonTarget = isolatedTarget;
                            }

                            bool startingPackHold = _packPullHoldSinceUtcMs == 0;
                            if (startingPackHold)
                                _packPullHoldSinceUtcMs = now;

                            int packSize = SquadTankBridge.CountPullPackSize(dungeonTarget);
                            if (SquadTankBridge.IsPackPullTooRisky(
                                    packSize,
                                    LowestPartyManaPercent(leader),
                                    now - _packPullHoldSinceUtcMs))
                            {
                                if (startingPackHold)
                                {
                                    SquadPartySyncService.LogAction("Dungeon: holding pull of " + dungeonTarget.Name +
                                        " (pack of " + packSize + " while the group recovers mana)");
                                }

                                StopMovementUnlessDungeonForcedStep(leader?.LeaderGuid ?? 0UL, now);
                                return RunStatus.Success;
                            }

                            _packPullHoldSinceUtcMs = 0;
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
                        StopMovementUnlessDungeonForcedStep(leader.LeaderGuid, now);
                        return RunStatus.Success;
                    }

                    WoWPoint nextDungeonPt = _dungeon.ComputeLeaderNextDungeonPoint();
                    if (nextDungeonPt != WoWPoint.Zero && me.Location.Distance(nextDungeonPt) > SquadDungeonService.HotspotArrivalDistance)
                    {
                        ulong leaderGuid = leader?.LeaderGuid ?? 0UL;
                        SyncDungeonForcedStepObjective(nextDungeonPt);
                        var trashPoints = CollectDungeonTrashPoints(me.Location, DungeonTrashScanRadius);

                        if (DungeonForcedStepActive(leaderGuid, now))
                        {
                            WoWPoint activeStep = _dungeonForcedStep;
                            WoWPoint[] remainingActiveLegs =
                                Navigator.Destination != WoWPoint.Zero && Navigator.Destination.Distance(activeStep) <= 1f
                                    ? Navigator.GetRemainingNavPath()
                                    : Array.Empty<WoWPoint>();
                            bool hasTrashOnActiveStep = SquadDungeonService.HasTrashOnPolylineStep(
                                me.Location,
                                remainingActiveLegs.Length > 0 ? remainingActiveLegs : _cachedDungeonAdvancePath,
                                activeStep,
                                trashPoints);
                            bool holdForTrash = EvaluateDungeonCorridorBlock(
                                _dungeonForcedStepBypassTrashGrace,
                                hasTrashOnActiveStep,
                                me.MapId,
                                nextDungeonPt,
                                ref _dungeonAdvanceBlockedMapId,
                                ref _dungeonAdvanceBlockedObjective,
                                ref _dungeonAdvanceLastHoldEvalUtcMs,
                                ref _dungeonAdvanceBlockedSinceUtcMs,
                                now,
                                out bool newlyBlocked,
                                out bool graceExpired);
                            if (newlyBlocked)
                                SquadPartySyncService.LogAction("Dungeon: clearing trash before " + (leader?.CurrentBossName ?? "next objective"));
                            if (graceExpired)
                                _dungeonForcedStepBypassTrashGrace = true;

                            if (!holdForTrash)
                            {
                                if (_dungeonForcedStepWasHeldForTrash)
                                {
                                    _dungeonForcedStepWasHeldForTrash = false;
                                    _dungeonForcedStepLastProgressUtcMs = now;
                                }

                                float resumePrecision = _dungeonForcedStepMovePrecision > 0f
                                    ? _dungeonForcedStepMovePrecision
                                    : Math.Min(Navigator.PathPrecision, DungeonForcedStepArrivalDistance);
                                MoveResult activeResult = Navigator.MoveTo(activeStep, resumePrecision, "Advancing through dungeon");
                                if (Navigator.IsRidingElevator)
                                {
                                    _dungeonForcedStepIssuedUtcMs = now;
                                    _dungeonForcedStepLastProgressUtcMs = now;
                                    return RunStatus.Success;
                                }
                                float curStepDist = me.Location.Distance(activeStep);
                                float curObjDist = me.Location.Distance(nextDungeonPt);
                                bool stalled = EvaluateDungeonStepProgress(
                                    curStepDist,
                                    curObjDist,
                                    ref _dungeonForcedStepBestDist,
                                    ref _dungeonForcedObjectiveBestDist,
                                    ref _dungeonForcedStepLastProgressUtcMs,
                                    now);

                                if (activeResult == MoveResult.PathGenerationFailed || activeResult == MoveResult.Failed || stalled)
                                {
                                    HandleFailedDungeonStep(activeStep, nextDungeonPt, me.MapId, now);
                                }
                                else if ((activeResult == MoveResult.ReachedDestination &&
                                          (curStepDist <= resumePrecision ||
                                           curObjDist <= SquadDungeonService.HotspotArrivalDistance)) ||
                                         curStepDist <= resumePrecision)
                                {
                                    ClearDungeonForcedStep();
                                    ResetDungeonStepFailures();
                                }
                                return RunStatus.Success;
                            }

                            _dungeonForcedStepWasHeldForTrash = true;
                            _dungeonForcedStepLastProgressUtcMs = now;
                            if (me.IsMoving)
                                WoWMovement.MoveStop();
                            return RunStatus.Success;
                        }

                        if (_cachedDungeonAdvanceObjective != nextDungeonPt ||
                            now - _lastDungeonAdvancePathUtcMs >= DungeonAdvancePathRefreshMs)
                        {
                            _cachedDungeonAdvanceObjective = nextDungeonPt;
                            _cachedDungeonAdvancePath = Navigator.GeneratePath(me.Location, nextDungeonPt);
                            _lastDungeonAdvancePathUtcMs = now;
                        }
                        else if (_cachedDungeonAdvancePath.Length == 0)
                        {
                            StopMovementUnlessDungeonForcedStep(leaderGuid, now);
                            return RunStatus.Success;
                        }

                        WoWPoint advanceStep = SquadDungeonService.ResolvePolylineAdvanceStep(
                            me.Location,
                            _cachedDungeonAdvancePath,
                            nextDungeonPt,
                            trashPoints);

                        bool bypassTrashGrace = false;
                        if (EvaluateDungeonCorridorBlock(
                            bypassTrashGrace: false,
                            hasTrashOnSegment: advanceStep == WoWPoint.Zero,
                            me.MapId,
                            nextDungeonPt,
                            ref _dungeonAdvanceBlockedMapId,
                            ref _dungeonAdvanceBlockedObjective,
                            ref _dungeonAdvanceLastHoldEvalUtcMs,
                            ref _dungeonAdvanceBlockedSinceUtcMs,
                            now,
                            out bool blockedStarted,
                            out bool graceElapsed))
                        {
                            if (blockedStarted)
                                SquadPartySyncService.LogAction("Dungeon: clearing trash before " + (leader?.CurrentBossName ?? "next objective"));
                            StopMovementUnlessDungeonForcedStep(leaderGuid, now);
                            return RunStatus.Success;
                        }

                        if (graceElapsed)
                        {
                            bypassTrashGrace = true;
                            advanceStep = SquadDungeonService.ResolvePolylineAdvanceStep(
                                me.Location,
                                _cachedDungeonAdvancePath,
                                nextDungeonPt,
                                null,
                                clearance: 0f);
                        }

                        if (advanceStep == WoWPoint.Zero)
                        {
                            StopMovementUnlessDungeonForcedStep(leaderGuid, now);
                            return RunStatus.Success;
                        }

                        bool primaryStepUsable = !_dungeon.IsDungeonNavigationPointBlacklisted(me.MapId, advanceStep, now) &&
                                                 Navigator.CanNavigateFully(me.Location, advanceStep);
                        if (!primaryStepUsable)
                        {
                            WoWPoint fallbackPolyStep = WoWPoint.Zero;
                            if (_cachedDungeonAdvancePath.Length > 0)
                            {
                                WoWPoint cursor = me.Location;
                                float cumulativePolyDist = 0f;
                                int maxBudgetIndex = -1;
                                for (int i = 0; i < _cachedDungeonAdvancePath.Length; i++)
                                {
                                    WoWPoint wp = _cachedDungeonAdvancePath[i];
                                    if (wp == WoWPoint.Zero)
                                        continue;
                                    cumulativePolyDist += cursor.Distance(wp);
                                    cursor = wp;
                                    if (cumulativePolyDist > SquadDungeonService.DungeonAdvanceStepDistance)
                                        break;
                                    maxBudgetIndex = i;
                                }

                                for (int i = maxBudgetIndex; i >= 0; i--)
                                {
                                    WoWPoint candidate = _cachedDungeonAdvancePath[i];
                                    if (candidate == WoWPoint.Zero ||
                                        me.Location.Distance(candidate) <= DungeonForcedStepArrivalDistance)
                                        continue;
                                    if (_dungeon.IsDungeonNavigationPointBlacklisted(me.MapId, candidate, now))
                                        continue;
                                    if (!bypassTrashGrace &&
                                        SquadDungeonService.HasTrashOnPolylineStep(me.Location, _cachedDungeonAdvancePath, candidate, trashPoints))
                                        continue;
                                    if (Navigator.CanNavigateFully(me.Location, candidate))
                                    {
                                        fallbackPolyStep = candidate;
                                        break;
                                    }
                                }
                            }

                            if (fallbackPolyStep == WoWPoint.Zero)
                            {
                                if (me.IsMoving)
                                    WoWMovement.MoveStop();
                                HandleFailedDungeonStep(advanceStep, nextDungeonPt, me.MapId, now);
                                return RunStatus.Success;
                            }

                            advanceStep = fallbackPolyStep;
                        }

                        SquadPartySyncService.LogAction("Dungeon: advancing toward " + (leader?.CurrentBossName ?? "next objective"));
                        IssueDungeonForcedStep(advanceStep, nextDungeonPt, leaderGuid, now, bypassTrashGrace);
                    }
                    else if (me.IsMoving)
                    {
                        WoWMovement.MoveStop();
                    }
                    return RunStatus.Success;
                }

                if (SquadContext.IsDungeonModeActive)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    return RunStatus.Success;
                }

                // 3. Migrate cleanly to new sub-profile hotspot when MigratingZone.
                var grindArea = StyxWoW.AreaManager?.CurrentGrindArea ?? (ProfileManager.CurrentOuterProfile != null ? ProfileManager.CurrentProfile?.GrindArea : null);
                if (leader != null && leader.Phase == SquadPhase.MigratingZone && grindArea != null && grindArea.Hotspots.Count > 0)
                {
                    if (!leader.HasValidMap || !IsSameMapForNavigation(me.MapId, leader.MapId))
                    {
                        if (me.IsMoving) WoWMovement.MoveStop();
                        return RunStatus.Success;
                    }
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
                bool followerCombatGrind = leader?.Members?.Any(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.IsInCombat) == true;
                if (!followerCombatGrind && SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
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

                if (leader == null || !leader.HasValidMap || !IsSameMapForNavigation(me.MapId, leader.MapId))
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

                if (SquadContext.IsDungeonModeActive && !isDungeon)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    return RunStatus.Success;
                }

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
