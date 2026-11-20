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
        private string _missingLeaderProfileHash = string.Empty;
        private long _nextLeaderProfileLookupUtcMs;
        private int _lastAppliedEffectiveLevel;
        private ulong _currentFocusGuid;
        private ulong _currentSecondaryGuid;
        private long _pullStartedUtcMs;
        private bool _wasInCombat;
        private bool _townTasksCompleteLocal;
        private bool _townRunRequestPending;
        private long _townRunRequestAcknowledgedUtcMs;
        private string _townRunRequestReason = string.Empty;
        private long _townRunRequestUtcMs;
        private long _deathStartedUtcMs;
        private bool _localWasNeedingRest;
        private bool _lootFilterAttached;
        private SquadTravelProgress _travelProgress = new();
        private static Composite? _activeCombatRoutine, _activeRestRoutine;
        private static bool _combatTicked, _restTicked, _healTicked;

        private sealed class RoutineAwareRoot : PrioritySelector
        {
            public RoutineAwareRoot(params Composite[] children) : base(children) { }
            public override RunStatus Tick(object context)
            {
                if (IsRunning) { base.Stop(context); base.Start(context); }
                _combatTicked = _restTicked = _healTicked = false;
                try { return base.Tick(context); }
                finally
                {
                    if (!_combatTicked) StopRoutine(ref _activeCombatRoutine);
                    // Healing has its own ownership flag: a healer tick that is actively
                    // handling a target must not be mistaken for a finished combat branch.
                    if (!_healTicked) SquadHealBridge.StopActiveHealBehavior();
                    if (!_restTicked) StopRoutine(ref _activeRestRoutine);
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
            _travelProgress = new SquadTravelProgress();
            SquadPartySyncService.ResetActionLog();

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
            _townRunRequestPending = false;
            _townRunRequestAcknowledgedUtcMs = 0;
            _townRunRequestReason = string.Empty;
            _townRunRequestUtcMs = 0;
            _deathStartedUtcMs = 0;
            _localWasNeedingRest = false;

            try
            {
                SquadContext.Reset();
                SquadContext.IsActive = true;
                SquadContext.ActiveSettings = settings;
                SquadContext.EffectiveLevelOverride = StyxWoW.Me?.Level ?? 1;

                ProfileManager.LevelOverrideSelector = () => _lastAppliedEffectiveLevel > 0 ? _lastAppliedEffectiveLevel : SquadContext.GetEffectiveProfileLevel();
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
                if (ProfileManager.CurrentOuterProfile != null)
                    ProfileManager.RefreshProfileForLevel();
                SquadOverlay.Attach();

                Logging.Write("[PartyGrind] Started as {0} (Coordinator={1}, Team={2}, Port={3}).",
                    settings.Role, settings.IsCoordinator, settings.TeamId, settings.IpcPort);
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
            Cleanup(() => { ProfileManager.RefreshProfileForLevel(); });
            Cleanup(() => { Mount.OnMountUp -= SquadFormationService.HandleMountUpEvent; });
            Cleanup(() => { SquadTankBridge.DetachTargetingFilters(); });

            if (_lootFilterAttached)
            {
                Cleanup(() => { LootTargeting.Instance.IncludeTargetsFilter -= LevelBot.LevelbotIncludeLootsFilter; });
                _lootFilterAttached = false;
            }

            Cleanup(() => { StopRoutine(ref _activeCombatRoutine); });
            Cleanup(() => { StopRoutine(ref _activeRestRoutine); });
            Cleanup(() => { SquadHealBridge.StopActiveHealBehavior(); });
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
            StopRoutine(ref _activeRestRoutine);
            SquadHealBridge.StopActiveHealBehavior();
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
                });

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
                if (settings.AutoLoadCoordinatorProfile && !EnsureLeaderProfile(leaderState))
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
            }

            if (_ipcClient != null && ((now - _lastIpcSendUtcMs) >= 120 || localState.IsInCombat != _wasInCombat))
            {
                _lastIpcSendUtcMs = now;
                _ipcClient.SendMemberState(localState);
            }
            _wasInCombat = localState.IsInCombat;
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
            _lastAppliedEffectiveLevel = leader.EffectiveSquadMinLevel;
            ProfileManager.RefreshProfileForLevel();
            return true;
        }

        private void UpdateCoordinatorTargets(long now, SquadLeaderState decision)
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
            // An idle focus is only publishable when our just-captured telemetry says we
            // are free to pull; active combat threats keep their separate selection path.
            var local = SquadContext.GetLocalMemberStateSnapshot();
            bool localCannotPull = local == null || local.IsDead || local.IsGhost || local.IsInCombat ||
                                   local.NeedsRest || local.IsLooting;
            if (newFocusGuid != 0 && focus != null && !focus.Combat && !focus.IsTargetingMyPartyMember &&
                (localCannotPull || decision.Phase != SquadPhase.Grinding || _coordinator.IsWaitingForFollower ||
                 !SquadTankBridge.CanInitiatePull(decision,
                    (SquadContext.ActiveSettings ?? SquadSettings.Instance).MaxPullGatherDistance)))
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
                CanProvideHealthstone = me.Class == WoWClass.Warlock && settings.EnableWarlockHealthstones && me.Level >= 10,
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
                ActiveSubProfileMaxLevel = currentSub?.MaxLevel ?? 85
            };
        }

        private Composite BuildRootBehavior()
        {
            return new RoutineAwareRoot(
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
                    return RunRoutineCombat(leader?.FocusTargetGuid ?? 0UL);

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

        private static bool PositionBackline(float standoffDistance, SquadLeaderState? leader, LocalPlayer me, WoWUnit? selectedTarget = null)
        {
            if (leader == null || leader.MapId != me.MapId || leader.IsDead || leader.IsGhost)
                return false;

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

            var settings = SquadContext.ActiveSettings ?? SquadSettings.Instance;
            var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name ?? string.Empty, settings.Role, leader);
            WoWPoint slotPos = SquadFormationService.ComputeStandoffPosition(
                tankPos, leader.Facing, focusPos, standoffDistance, slotIdx, slotCount);

            if (me.Location.Distance2D(slotPos) > 3.0f)
            {
                SquadPartySyncService.LogAction("Moving to backline position");
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

            SquadPartySyncService.LogAction((me.Combat ? "Combat routine" : "Pull routine") +
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

            return SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true;
        }

        private static RunStatus RunRestBehavior()
        {
            SquadPartySyncService.LogAction("Resting");
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

            if (LootFrame.Instance.IsVisible)
                return true;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && leader.Phase != SquadPhase.Looting && leader.Phase != SquadPhase.Grinding)
                return false;

            return FindEligibleLoot() != null;
        }

        private static readonly Dictionary<ulong, long> LootRetry = new();

        private static WoWObject? FindEligibleLoot()
        {
            var leader = SquadContext.GetLeaderStateSnapshot();
            long now = Environment.TickCount64;
            foreach (var guid in LootRetry.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                LootRetry.Remove(guid);
            return LootTargeting.Instance.LootingList.FirstOrDefault(o => o != null && o.IsValid && !LootRetry.ContainsKey(o.Guid) &&
                (leader == null || o.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z)) <= 25f));
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
                            SquadPartySyncService.LogAction("Moving back for " + trailing.CharacterName);
                            Navigator.MoveTo(backPt.Value, "Backtracking for lagging squadmate");
                            return RunStatus.Success;
                        }
                    }
                }

                // 2. Soft wait when follower > SoftWaitDistance.
                if (_coordinator.IsWaitingForFollower)
                {
                    SquadPartySyncService.LogAction("Waiting for squadmate");
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
                        SquadPartySyncService.LogAction("Moving to new zone hotspot");
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
                if (leader.MapId != me.MapId)
                    return RunStatus.Success;

                int slotIdx = SquadContext.GetMySlotIndex(me.Name, leader);
                WoWPoint followPos = SquadFormationService.ComputeTravelFollowPosition(me.Location, leader, slotIdx, _travelProgress);
                if (me.Location.Distance(followPos) > 4.0f)
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
}
