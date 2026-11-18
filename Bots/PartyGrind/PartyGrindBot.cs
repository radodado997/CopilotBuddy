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
        private readonly SquadPartySyncService _partySync;

        private SquadIpcServer? _ipcServer;
        private SquadIpcClient? _ipcClient;
        private Composite? _root;

        private long _lastIpcSendUtcMs;
        private long _lastTownRallySearchUtcMs;
        private uint _cachedTownRallyMapId;
        private WoWPoint _cachedTownRallyPoint;
        private string _cachedProfilePath = string.Empty;
        private string _cachedProfileHash = string.Empty;
        private string _lastAttemptedLeaderProfilePath = string.Empty;
        private int _lastAppliedEffectiveLevel;
        private ulong _currentFocusGuid;
        private ulong _currentSecondaryGuid;
        private long _pullStartedUtcMs;
        private bool _wasInCombat;
        private bool _townTasksCompleteLocal;
        private long _deathStartedUtcMs;
        private bool _localWasNeedingRest;
        private bool _lootFilterAttached;

        public PartyGrindBot()
        {
            _partySync = new SquadPartySyncService(_provisioning);
        }

        public override string Name => "Party Grind";

        public override bool RequiresProfile =>
            SquadSettings.Instance.IsCoordinator ||
            !SquadSettings.Instance.AutoLoadCoordinatorProfile ||
            ProfileManager.CurrentOuterProfile != null;

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

            _coordinator.Reset();
            _townRun.Reset();
            _lastIpcSendUtcMs = 0;
            _lastAppliedEffectiveLevel = 0;
            _lastAttemptedLeaderProfilePath = string.Empty;
            _currentFocusGuid = 0;
            _currentSecondaryGuid = 0;
            _pullStartedUtcMs = 0;
            _wasInCombat = false;
            _townTasksCompleteLocal = false;
            _deathStartedUtcMs = 0;
            _localWasNeedingRest = false;

            SquadContext.Reset();
            SquadContext.IsActive = true;
            SquadContext.ActiveSettings = settings;
            SquadContext.EffectiveLevelOverride = StyxWoW.Me?.Level ?? 1;

            ProfileManager.LevelOverrideSelector = () => SquadContext.GetEffectiveProfileLevel();

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
            if (ProfileManager.CurrentOuterProfile != null)
                ProfileManager.RefreshProfileForLevel();
            SquadOverlay.Attach();

            Logging.Write("[PartyGrind] Started as {0} (Coordinator={1}, Team={2}, Port={3}).",
                settings.Role, settings.IsCoordinator, settings.TeamId, settings.IpcPort);
        }

        public override void Stop()
        {
            ProfileManager.LevelOverrideSelector = null;
            Mount.OnMountUp -= SquadFormationService.HandleMountUpEvent;
            SquadTankBridge.DetachTargetingFilters();

            if (_lootFilterAttached)
            {
                LootTargeting.Instance.IncludeTargetsFilter -= LevelBot.LevelbotIncludeLootsFilter;
                _lootFilterAttached = false;
            }

            _partySync.DetachEvents();
            StopIpc();
            SquadOverlay.Detach();
            SquadContext.Reset();
            _coordinator.Reset();
            _townRun.Reset();
        }

        public override void OnPaused()
        {
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
                    now);
                SquadContext.UpdateLeaderState(leaderState);
                _ipcServer.BroadcastLeaderState(leaderState, populateRoster: false);
            }
        }

        public override void OnResumed()
        {
            _coordinator.IsPausedManually = false;
            _coordinator.ManualPauseReason = string.Empty;
        }

        public override void Pulse()
        {
            if (!SquadContext.IsActive || StyxWoW.Me == null)
                return;

            Navigator.PathPrecision = Math.Clamp(StyxWoW.Me.MovementInfo.CurrentSpeed * 0.15f, 1.5f, 10f);

            SquadSettings settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        }

        private void StartIpc(SquadSettings settings)
        {
            StopIpc();
            try
            {
                if (settings.IsCoordinator)
                {
                    _ipcServer = new SquadIpcServer(settings.IpcPort, settings.TeamId);
                    _ipcServer.Start();
                    SquadContext.IsIpcConnected = true;
                }
                else
                {
                    _ipcClient = new SquadIpcClient(settings.IpcPort, settings.TeamId);
                    _ipcClient.Start(BuildLocalHandshake);
                    SquadContext.IsIpcConnected = false;
                }
            }
            catch (Exception ex)
            {
                StopIpc();
                Stop();
                Logging.Write("[PartyGrind IPC] Startup failed: {0}", ex.Message);
            }
        }

        private void StopIpc()
        {
            try { _ipcServer?.Dispose(); } catch { }
            _ipcServer = null;
            try { _ipcClient?.Dispose(); } catch { }
            _ipcClient = null;
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
                Level = me?.Level ?? 1
            };
        }

        private void PulseCoordinator(SquadSettings settings, SquadMemberState localState, long now)
        {
            var snapshots = _ipcServer?.GetMemberSnapshots() ?? new List<SquadConnectedMemberSnapshot>();
            _partySync.PulseCoordinatorPartySync(settings, snapshots);

            UpdateCoordinatorTargets(now);
            SquadProfileSnapshot profileSnap = CaptureProfileSnapshot(now);

            SquadLeaderState leaderState = _coordinator.EvaluateTick(
                settings,
                localState,
                snapshots,
                profileSnap,
                _currentFocusGuid,
                _currentSecondaryGuid,
                _pullStartedUtcMs,
                StyxWoW.Me?.Rotation ?? 0f,
                now);

            SquadContext.IsIpcConnected = _ipcServer?.IsListening == true;
            SquadContext.UpdateLeaderState(leaderState);

            if (leaderState.EffectiveSquadMinLevel > 0 && leaderState.EffectiveSquadMinLevel != _lastAppliedEffectiveLevel)
            {
                _lastAppliedEffectiveLevel = leaderState.EffectiveSquadMinLevel;
                if (ProfileManager.CurrentOuterProfile != null)
                    ProfileManager.RefreshProfileForLevel();
            }

            SquadPartyBuffService.UpdateRaidTargetMarkers(settings, leaderState);

            if (_ipcServer != null && (now - _lastIpcSendUtcMs) >= 100)
            {
                _lastIpcSendUtcMs = now;
                _ipcServer.BroadcastLeaderState(leaderState, populateRoster: false);
            }
        }

        private void PulseFollower(SquadSettings settings, SquadMemberState localState, long now)
        {
            bool connected = _ipcClient != null && _ipcClient.IsConnected;
            SquadContext.IsIpcConnected = connected;

            var leaderState = connected ? _ipcClient?.LatestLeaderState : null;
            if (leaderState == null) SquadContext.UpdateLeaderState(null);
            if (leaderState != null)
            {
                SquadContext.UpdateLeaderState(leaderState);

                if (leaderState.EffectiveSquadMinLevel > 0 && leaderState.EffectiveSquadMinLevel != _lastAppliedEffectiveLevel)
                {
                    _lastAppliedEffectiveLevel = leaderState.EffectiveSquadMinLevel;
                    if (ProfileManager.CurrentOuterProfile != null)
                        ProfileManager.RefreshProfileForLevel();
                }
            }

            if (_ipcClient != null && ((now - _lastIpcSendUtcMs) >= 120 || localState.IsInCombat != _wasInCombat))
            {
                _lastIpcSendUtcMs = now;
                _ipcClient.SendMemberState(localState);
            }
            _wasInCombat = localState.IsInCombat;
        }

        private void UpdateCoordinatorTargets(long now)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return;

            WoWUnit? focus = me.CurrentTarget;
            if (focus == null || !focus.IsValid || focus.Dead || focus.IsFriendly)
            {
                focus = Targeting.Instance.FirstUnit;
            }

            ulong newFocusGuid = (focus != null && focus.IsValid && !focus.Dead && !focus.IsFriendly) ? focus.Guid : 0UL;
            if (newFocusGuid != 0 && focus != null && !focus.Combat && !focus.IsTargetingMyPartyMember &&
                !SquadTankBridge.CanInitiatePull(SquadContext.GetLeaderStateSnapshot(),
                    (SquadContext.ActiveSettings ?? SquadSettings.Instance).MaxPullGatherDistance))
                newFocusGuid = 0;
            if (newFocusGuid != 0 && newFocusGuid != _currentFocusGuid)
            {
                _pullStartedUtcMs = now;
            }
            else if (newFocusGuid == 0)
            {
                _pullStartedUtcMs = 0;
            }

            _currentFocusGuid = newFocusGuid;

            WoWUnit? secondary = Targeting.Instance.TargetList
                .FirstOrDefault(u => u != null && u.IsValid && !u.Dead && u.Guid != _currentFocusGuid && (u.Combat || u.IsTargetingMyPartyMember));
            _currentSecondaryGuid = secondary?.Guid ?? 0UL;
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

            var grindArea = StyxWoW.AreaManager?.CurrentGrindArea ?? current?.GrindArea;
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
            if (!isDead && !isGhost) _deathStartedUtcMs = 0;
            bool usesMana = me.PowerType == WoWPowerType.Mana;

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
            bool squadHasWarlock = me.Class == WoWClass.Warlock ||
                (leader?.Members?.Any(m => m.IsConnected && m.Class == WoWClass.Warlock) ?? false);

            var provNeeds = SquadProvisioningService.EvaluateConsumableNeeds(
                me.Combat,
                (int)me.FreeNormalBagSlots,
                foodCount,
                drinkCount,
                usesMana,
                hasHs,
                squadHasWarlock,
                settings);

            _townRun.ObservePhase(leader?.Phase);
            var townNeeds = SquadTownRunService.EvaluateLiveLocalNeeds(settings, leader);
            if (leader == null || leader.Phase != SquadPhase.TownRun)
                _townTasksCompleteLocal = false;

            string xmlPath = ProfileManager.XmlLocation ?? string.Empty;
            if (!string.IsNullOrEmpty(xmlPath) && !string.Equals(xmlPath, _cachedProfilePath, StringComparison.OrdinalIgnoreCase))
            {
                _cachedProfilePath = xmlPath;
                _cachedProfileHash = ComputeFileHash(xmlPath);
            }

            var currentSub = ProfileManager.CurrentOuterProfile != null ? ProfileManager.CurrentProfile : null;
            bool isResting = !me.Combat && (me.HasAura("Food") || me.HasAura("Drink") || _localWasNeedingRest);
            bool isLooting = me.FreeNormalBagSlots > 0 && !me.Combat && !isDead && !isGhost &&
                (LootFrame.Instance.IsVisible || LootTargeting.Instance.FirstObject != null);

            return new SquadMemberState
            {
                CharacterName = me.Name ?? "Unknown",
                Guid = me.Guid,
                Role = settings.Role,
                Class = me.Class,
                Level = me.Level,
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
                IsLooting = isLooting,
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
                CanProvideMageFood = me.Class == WoWClass.Mage && settings.EnableMageProvisioning && me.Level >= 6,
                CanProvideMageDrink = me.Class == WoWClass.Mage && settings.EnableMageProvisioning && me.Level >= 4,
                CanProvideHealthstone = me.Class == WoWClass.Warlock && settings.EnableWarlockHealthstones && me.Level >= 10,
                NeedsSell = townNeeds.NeedsSell,
                NeedsRepair = townNeeds.NeedsRepair,
                NeedsBuy = townNeeds.NeedsBuy,
                NeedsTrain = townNeeds.NeedsTrain,
                NeedsMail = townNeeds.NeedsMail,
                TownTasksComplete = _townTasksCompleteLocal,
                FreeBagSlots = (int)me.FreeNormalBagSlots,
                LowestDurabilityPct = (float)me.LowestDurabilityPercent,
                LoadedProfileHash = _cachedProfileHash,
                ActiveSubProfileMinLevel = currentSub?.MinLevel ?? 1,
                ActiveSubProfileMaxLevel = currentSub?.MaxLevel ?? 85
            };
        }

        private Composite BuildRootBehavior()
        {
            return new PrioritySelector(
                // 1. Paused or follower disconnected safety pause
                new Decorator(
                    ctx => ShouldPauseMovement(),
                    new Action(ctx =>
                    {
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
                    ctx => SquadContext.GetLeaderStateSnapshot()?.Phase == SquadPhase.TownRun,
                    new Action(ctx =>
                    {
                        var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
                        var leader = SquadContext.GetLeaderStateSnapshot();
                        if (_townRun.RunTownTick(settings, leader, out bool complete))
                        {
                            _townTasksCompleteLocal = complete;
                            return RunStatus.Success;
                        }
                        _townTasksCompleteLocal = false;
                        return RunStatus.Failure;
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
                        if (me != null && me.Mounted && provisionLeader != null &&
                            (string.Equals(me.Name, provisionLeader.ActiveProvisionerName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(me.Name, provisionLeader.ActiveTradeTargetName, StringComparison.OrdinalIgnoreCase)))
                        {
                            Mount.Dismount("Squad provisioning");
                            return RunStatus.Success;
                        }
                        if (provisionLeader?.Members.Any(m => m.IsMounted &&
                            (string.Equals(m.CharacterName, provisionLeader.ActiveProvisionerName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(m.CharacterName, provisionLeader.ActiveTradeTargetName, StringComparison.OrdinalIgnoreCase))) == true)
                            return RunStatus.Success;
                        return _provisioning.RunProvisioningTick(settings, provisionLeader)
                            ? RunStatus.Success
                            : RunStatus.Failure;
                    })
                ),

                // 8. Cross-party Buffs out of combat
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
            if (settings != null && !settings.IsCoordinator && !SquadContext.IsIpcConnected &&
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
                if (_deathStartedUtcMs == 0)
                    _deathStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                long deadElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _deathStartedUtcMs;
                bool releaseOrdered = leader?.ReleaseCorpseOrder == true;
                bool waitExpired = deadElapsed >= Math.Max(5, settings.WaitForPartyRessSeconds) * 1000L;

                if (releaseOrdered || waitExpired)
                {
                    Lua.DoString("RepopMe()");
                }
                else
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                }
                return RunStatus.Success;
            }

            _deathStartedUtcMs = 0;
            WoWPoint corpsePoint = me.CorpsePoint;
            WoWPoint targetPoint = corpsePoint;
            if (leader != null && leader.SquadCorpseRallyX != 0 && leader.SquadCorpseRallyY != 0)
            {
                var rally = new WoWPoint(leader.SquadCorpseRallyX, leader.SquadCorpseRallyY, leader.SquadCorpseRallyZ);
                if (rally.Distance(corpsePoint) <= 40f)
                    targetPoint = rally;
            }

            if (me.Location.Distance(targetPoint) > 30f && me.Location.Distance(corpsePoint) > 30f)
            {
                Navigator.MoveTo(targetPoint, "CorpseRun: returning to squad corpse");
                return RunStatus.Success;
            }

            // Wait briefly for squadmate ghosts before retrieving corpse if others are also ghosts nearby.
            Lua.DoString("RetrieveCorpse()");
            return RunStatus.Success;
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

            if (leader != null && (leader.InCombat || leader.FocusTargetGuid != 0))
                return true;

            if (settings.Role is SquadRole.Healer or SquadRole.HybridOffHeal)
            {
                if (leader?.Members?.Any(m => m.IsConnected && !m.IsDead && m.HealthPercent < 88f) == true)
                    return true;
            }

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
                    return RunRoutineCombat(leader?.FocusTargetGuid ?? 0UL);

                case SquadRole.Healer:
                    if (SquadHealBridge.RunHealerTick(emergencyOnly: false, settings, leader))
                        return RunStatus.Success;
                    if (PositionBackline(settings.HealerStandoffDistance, leader, me))
                        return RunStatus.Success;
                    if (SquadHealBridge.TryHealerAssistDps(settings, leader))
                        return RunStatus.Success;
                    return RunStatus.Success;

                case SquadRole.HybridOffHeal:
                    if (SquadHealBridge.RunHealerTick(emergencyOnly: true, settings, leader))
                        return RunStatus.Success;
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
            if (local != null && local.NeedsPeel && leader != null && !leader.IsDead && !leader.IsGhost)
            {
                var tankPos = new WoWPoint(leader.X, leader.Y, leader.Z);
                if (me.Location.Distance(tankPos) > 12f)
                {
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

            ulong targetGuid = leader?.FocusTargetGuid ?? 0UL;
            if (targetGuid == 0 && leader != null)
                targetGuid = leader.SecondaryTargetGuid;

            WoWUnit? focus = targetGuid != 0
                ? ObjectManager.GetObjectByGuid<WoWUnit>(targetGuid)
                : Targeting.Instance.FirstUnit;

            if (focus != null && focus.IsValid && !focus.Dead && focus.Attackable && !focus.IsFriendly)
            {
                if (me.CurrentTargetGuid != focus.Guid)
                    focus.Target();

                if (me.GotAlivePet)
                {
                    WoWPetControl.Attack(focus);
                }

                // Non-overlapping flank / standoff positioning
                var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
                if (isMelee)
                {
                    if (focus.Distance <= 12f)
                    {
                        WoWPoint flankPos = SquadFormationService.ComputeMeleeFlankPosition(
                            focus.Location, focus.Rotation, settings.MeleeFlankRadius, slotIdx, slotCount);
                        if (me.Location.Distance2D(flankPos) > 1.8f && focus.Distance > 4.5f)
                        {
                            Navigator.MoveTo(flankPos, "Flanking " + focus.Name);
                            return RunStatus.Success;
                        }
                    }
                }
                else
                {
                    if (PositionBackline(settings.RangedStandoffDistance, leader, me))
                        return RunStatus.Success;
                }

                return RunRoutineCombat(focus.Guid);
            }

            return RunStatus.Failure;
        }

        private static bool PositionBackline(float standoffDistance, SquadLeaderState? leader, LocalPlayer me)
        {
            if (leader == null || leader.IsDead || leader.IsGhost)
                return false;

            var tankPos = new WoWPoint(leader.X, leader.Y, leader.Z);

            WoWPoint? focusPos = null;
            if (leader.FocusTargetGuid != 0)
            {
                var focusUnit = ObjectManager.GetObjectByGuid<WoWUnit>(leader.FocusTargetGuid);
                if (focusUnit != null && focusUnit.IsValid)
                    focusPos = focusUnit.Location;
            }

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
            WoWPoint slotPos = SquadFormationService.ComputeStandoffPosition(
                tankPos, leader.Facing, focusPos, standoffDistance, slotIdx, slotCount);

            if (me.Location.Distance2D(slotPos) > 3.0f)
            {
                Navigator.MoveTo(slotPos, "Holding backline standoff");
                return true;
            }
            return false;
        }

        private static RunStatus RunRoutineCombat(ulong focusGuid)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return RunStatus.Failure;

            if (focusGuid != 0 && (me.CurrentTarget == null || me.CurrentTarget.Dead || me.CurrentTargetGuid != focusGuid))
            {
                var unit = ObjectManager.GetObjectByGuid<WoWUnit>(focusGuid);
                if (unit != null && unit.IsValid && !unit.Dead)
                    unit.Target();
            }

            var routine = RoutineManager.Current;
            if (routine == null)
                return RunStatus.Failure;

            Composite? behavior = me.Combat ? routine.CombatBehavior : (routine.PullBehavior ?? routine.CombatBehavior);
            if (behavior == null)
                return RunStatus.Failure;

            behavior.Start(null);
            RunStatus status = behavior.Tick(null);
            behavior.Stop(null);
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

            return SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true;
        }

        private static RunStatus RunRestBehavior()
        {
            var me = StyxWoW.Me!;
            if (me.IsMoving)
                WoWMovement.MoveStop();

            if (me.Mounted)
                Mount.Dismount("Resting");

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            if (me.HealthPercent < settings.ResumeRestHealthPct && !me.HasAura("Food") && !me.HasAura("Refreshment"))
                Rest.FeedImmediate();

            bool usesMana = me.PowerType == WoWPowerType.Mana;
            if (usesMana && me.PowerPercent < settings.ResumeRestManaPct && !me.HasAura("Drink") && !me.HasAura("Refreshment"))
                Rest.DrinkImmediate();

            var restBehavior = RoutineManager.Current?.RestBehavior;
            if (restBehavior != null && (me.HealthPercent < settings.ResumeRestHealthPct || (usesMana && me.PowerPercent < settings.ResumeRestManaPct)))
            {
                restBehavior.Start(null);
                restBehavior.Tick(null);
                restBehavior.Stop(null);
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

            if (LootFrame.Instance.IsVisible)
                return true;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && leader.Phase != SquadPhase.Looting && leader.Phase != SquadPhase.Grinding)
                return false;

            return LootTargeting.Instance.FirstObject != null;
        }

        private static RunStatus RunLootBehavior()
        {
            var me = StyxWoW.Me!;
            if (LootFrame.Instance.IsVisible)
            {
                LootFrame.Instance.LootAll();
                StyxWoW.Sleep(200);
                LootFrame.Instance.Close();
                return RunStatus.Success;
            }

            var lootObj = LootTargeting.Instance.FirstObject;
            if (lootObj == null || !lootObj.IsValid)
                return RunStatus.Failure;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && lootObj.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z)) > 25f)
                return RunStatus.Failure;

            if (lootObj.Location.Distance(me.Location) > 4.5f)
            {
                Navigator.MoveTo(lootObj.Location, "Looting " + lootObj.Name);
                return RunStatus.Success;
            }

            if (me.IsMoving) WoWMovement.MoveStop();
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

            if (settings.IsCoordinator)
            {
                if (me.GotAlivePet)
                    WoWPetControl.Follow();

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
                            Navigator.MoveTo(backPt.Value, "Backtracking for lagging squadmate");
                            return RunStatus.Success;
                        }
                    }
                }

                // 2. Soft wait when follower > SoftWaitDistance.
                if (_coordinator.IsWaitingForFollower)
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    return RunStatus.Success;
                }

                // 3. Migrate cleanly to new sub-profile hotspot when MigratingZone.
                var grindArea = StyxWoW.AreaManager?.CurrentGrindArea ?? (ProfileManager.CurrentOuterProfile != null ? ProfileManager.CurrentProfile?.GrindArea : null);
                if (leader != null && leader.Phase == SquadPhase.MigratingZone && grindArea != null && grindArea.Hotspots.Count > 0)
                {
                    WoWPoint hs = grindArea.CurrentHotSpot.Position;
                    if (me.Location.Distance(hs) > 10f)
                    {
                        Navigator.MoveTo(hs, "Migrating to new sub-profile hotspot");
                        return RunStatus.Success;
                    }
                    return RunStatus.Success;
                }

                // 4. Ready to pull?
                if (SquadTankBridge.CanInitiatePull(leader, settings.MaxPullGatherDistance))
                {
                    WoWUnit? target = Targeting.Instance.FirstUnit;
                    if (target != null && target.IsValid && !target.Dead && target.Attackable && !target.IsFriendly)
                    {
                        _currentFocusGuid = target.Guid;
                        _pullStartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

                if (leader == null || leader.MapId != me.MapId)
                    return RunStatus.Success;

                int slotIdx = SquadContext.GetMySlotIndex(me.Name, leader);
                WoWPoint followPos = SquadFormationService.ComputeTravelFollowPosition(me.Location, leader, slotIdx);
                if (me.Location.Distance(followPos) > 4.0f)
                {
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
}
