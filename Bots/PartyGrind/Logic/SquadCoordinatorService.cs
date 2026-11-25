using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Snapshot of active profile metadata used by <see cref="SquadCoordinatorService"/>.
    /// Kept decoupled from WoW memory so coordinator logic is fully unit-testable.
    /// </summary>
    public sealed class SquadProfileSnapshot
    {
        public string ProfileXmlPath { get; set; } = string.Empty;
        public string ProfileXmlHash { get; set; } = string.Empty;
        public string SubProfileName { get; set; } = string.Empty;
        public int SubProfileMinLevel { get; set; } = 1;
        public int SubProfileMaxLevel { get; set; } = 85;
        /// <summary>Highest MaxLevel of the loaded profile's sub-profiles; 0 when unknown.</summary>
        public int ProfileMaxLevel { get; set; }
        public uint MapId { get; set; }
        public float HotspotX { get; set; }
        public float HotspotY { get; set; }
        public float HotspotZ { get; set; }
        public uint? TownRallyMapId { get; set; }
        public float TownRallyX { get; set; }
        public float TownRallyY { get; set; }
        public float TownRallyZ { get; set; }
        public float SafeCorpseRallyX { get; set; }
        public float SafeCorpseRallyY { get; set; }
        public float SafeCorpseRallyZ { get; set; }
        public bool HasProfile { get; set; } = true;
    }

    /// <summary>
    /// Authoritative coordinator state machine for PartyGrindBot.
    /// Computes EffectiveSquadMinLevel, synchronized SquadPhase, breadcrumbs, distance hysteresis,
    /// TownRun completion gate, wipe/corpse recovery, and provisioning arbitration.
    /// </summary>
    public sealed class SquadCoordinatorService
    {
        private sealed class MemberHistoryRecord
        {
            public string Name = string.Empty;
            public ulong Guid;
            public SquadRole Role;
            public WoWClass Class;
            public int Level;
            public string LevelSource = string.Empty;
            public (string Source, int Raw)? RejectedLevel;
            public bool Disconnected;
            public bool GraceExpired;
            public float XpPercent;
            public long LastSeenUtcMs;
            public bool NeedsRest;
            public SquadMemberState? LastState;
        }

        private readonly Dictionary<string, MemberHistoryRecord> _history = new Dictionary<string, MemberHistoryRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _provisionCooldownUntilMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SquadBreadcrumb> _breadcrumbs = new List<SquadBreadcrumb>();

        private int? _lastLoggedMinLevel;
        private bool _isWaitingForFollower;
        private bool _isHardBacktracking;
        private long _waitingSinceUtcMs;
        private long _notWaitingSinceUtcMs;
        private bool _isWaitingForFollowerDebounced;

        private const long DistanceDebounceMs = 2000L;
        private bool _inTownRun;
        private bool _allTownTasksComplete;
        private bool _inLootWindow;
        private bool _dungeonCompleted;
        private long _lootWindowEndsUtcMs;
        private bool _wasAnyInCombat;
        private long _resWaitStartedUtcMs;
        private string _resWaitMemberKey = string.Empty;
        private bool _inCorpseRun;
        private bool _migratingZone;
        private string _lastSubProfileKey = string.Empty;
        private uint _lastBreadcrumbMapId;
        private int _breadcrumbGeneration;
        private long _breadcrumbSequence;
        private string _activeProvisioner = string.Empty;
        private string _activeTradeTarget = string.Empty;
        private long _provisionStartedUtcMs;
        private SquadPhase? _lastLoggedPhase;
        private bool _assignedFood, _assignedDrink, _assignedHealthstone;
        private string _townRunRequestedByName = string.Empty;
        private string _townRunRequestReason = string.Empty;
        private long _townRunRequestedUtcMs;
        private readonly Dictionary<string, long> _townRunRequestAckUtcMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public bool IsPausedManually { get; set; }
        public string ManualPauseReason { get; set; } = string.Empty;
        public bool IsWaitingForFollower => _isWaitingForFollower;
        public bool IsWaitingForFollowerDebounced => _isWaitingForFollowerDebounced;
        public bool IsHardBacktracking => _isHardBacktracking;
        public float MaxFollowerDistance { get; private set; }

        public void Reset()
        {
            IsPausedManually = false;
            ManualPauseReason = string.Empty;
            _history.Clear();
            _lastLoggedMinLevel = null;
            _provisionCooldownUntilMs.Clear();
            ClearBreadcrumbs();
            _isWaitingForFollower = false;
            _isHardBacktracking = false;
            _waitingSinceUtcMs = 0;
            _notWaitingSinceUtcMs = 0;
            _isWaitingForFollowerDebounced = false;
            MaxFollowerDistance = 0f;
            _inTownRun = false;
            _dungeonCompleted = false;
            _allTownTasksComplete = false;
            _inLootWindow = false;
            _lootWindowEndsUtcMs = 0;
            _wasAnyInCombat = false;
            _resWaitStartedUtcMs = 0;
            _resWaitMemberKey = string.Empty;
            _inCorpseRun = false;
            _migratingZone = false;
            _lastSubProfileKey = string.Empty;
            _lastBreadcrumbMapId = 0;
            _activeProvisioner = string.Empty;
            _activeTradeTarget = string.Empty;
            _provisionStartedUtcMs = 0;
            _townRunRequestedByName = string.Empty;
            _townRunRequestReason = string.Empty;
            _townRunRequestedUtcMs = 0;
            _townRunRequestAckUtcMs.Clear();
        }

        /// <summary>
        /// Evaluates one coordinator tick using purely provided inputs and returns the broadcastable
        /// <see cref="SquadLeaderState"/>.
        /// </summary>
        public SquadLeaderState EvaluateTick(
            SquadSettings settings,
            SquadMemberState leaderLocal,
            IReadOnlyList<SquadConnectedMemberSnapshot> connectedClients,
            SquadProfileSnapshot profile,
            ulong focusTargetGuid,
            ulong secondaryTargetGuid,
            long pullStartedUtcMs,
            float leaderFacing,
            long nowUtcMs,
            float? effectiveProfileLevelHint = null,
            Func<int, SquadProfileSnapshot>? resolveProfile = null,
            SquadTownRunRequest? localTownRunRequest = null,
            SquadDungeonSnapshot? dungeon = null,
            int instanceResetGeneration = 0,
            int hourlyInstancesRunCount = 0,
            int lockoutRemainingSeconds = 0,
            Func<int, SquadDungeonSnapshot>? resolveDungeon = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (leaderLocal == null) throw new ArgumentNullException(nameof(leaderLocal));
            profile ??= new SquadProfileSnapshot { HasProfile = false };
            bool isDungeonMode = settings.BotMode == SquadBotMode.Dungeon;

            // 1. Merge coordinator + connected clients into roster and update level history.
            var rosterStates = new List<SquadMemberState>(1 + connectedClients.Count) { leaderLocal };
            var connectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(leaderLocal.CharacterName))
            {
                connectedNames.Add(leaderLocal.CharacterName.Trim());
                UpsertHistory(leaderLocal, "local", nowUtcMs);
            }

            var assignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var roles = new Dictionary<string, SquadRole>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(leaderLocal.CharacterName))
                roles[leaderLocal.CharacterName.Trim()] = leaderLocal.Role;

            foreach (var snap in connectedClients)
            {
                if (string.IsNullOrWhiteSpace(snap.CharacterName))
                    continue;
                string name = snap.CharacterName.Trim();
                assignments[name] = snap.SlotIndex;

                var state = snap.LatestState ?? new SquadMemberState
                {
                    CharacterName = name,
                    Guid = snap.Handshake.Guid,
                    Class = snap.Handshake.Class,
                    Role = snap.Handshake.Role,
                    Level = snap.Handshake.Level
                };

                roles[name] = state.Role;
                if (snap.IsConnected)
                {
                    connectedNames.Add(name);
                    rosterStates.Add(state);
                    UpsertHistory(state, snap.LatestState == null ? "handshake" : "telemetry", nowUtcMs, snap.Handshake.Level);
                    if (snap.LatestState == null) _history[name].LastState = null;
                }
            }

            // 2. Compute EffectiveSquadMinLevel (including recently disconnected members within DisconnectGraceSeconds).
            long graceMs = Math.Max(0, settings.DisconnectGraceSeconds) * 1000L;
            foreach (var rec in _history.Values.Where(r => !r.Disconnected && !connectedNames.Contains(r.Name)))
            {
                rec.Disconnected = true;
                Logging.WriteDebug("[PartyGrind Level] Disconnected member={0} guid={1} confirmed={2} graceMs={3}",
                    rec.Name, rec.Guid, rec.Level, graceMs);
            }
            foreach (var rec in _history.Values.Where(r => r.Disconnected && !r.GraceExpired && nowUtcMs - r.LastSeenUtcMs > graceMs))
            {
                Logging.WriteDebug("[PartyGrind Level] Grace expired member={0} guid={1} confirmed={2}", rec.Name, rec.Guid, rec.Level);
                rec.GraceExpired = true;
                rec.LastState = null;
                rec.NeedsRest = false;
            }
            var lowest = _history.Values.Where(r => !r.GraceExpired && r.Level > 0)
                .OrderBy(r => r.Level).ThenBy(r => r.XpPercent).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            int effectiveMinLevel = lowest?.Level ?? 0;
            string lowestName = lowest?.Name ?? string.Empty;
            float lowestXpPct = lowest?.XpPercent ?? 0f;
            if (_lastLoggedMinLevel != effectiveMinLevel)
            {
                Logging.WriteDebug("[PartyGrind Level] Profile minimum {0} -> {1} member={2} guid={3} source={4} inGrace={5}",
                    _lastLoggedMinLevel, effectiveMinLevel, lowestName, lowest?.Guid, lowest?.LevelSource, lowest?.Disconnected);
                _lastLoggedMinLevel = effectiveMinLevel;
            }

            var telemetryNames = new HashSet<string>(
                connectedClients.Where(s => s.IsConnected && s.LatestState != null).Select(s => s.CharacterName.Trim()),
                StringComparer.OrdinalIgnoreCase);
            telemetryNames.Add(leaderLocal.CharacterName.Trim());
            rosterStates.RemoveAll(m => !telemetryNames.Contains(m.CharacterName.Trim()));
            if (effectiveMinLevel > 0 && resolveProfile != null) profile = resolveProfile(effectiveMinLevel);
            if (isDungeonMode && resolveDungeon != null) dungeon = resolveDungeon(effectiveMinLevel);

            // The squad grinds until its lowest member passes the highest level range of the
            // loaded profile. Any member above that range keeps grinding with the squad
            // instead of being dropped from the profile. An unknown profile maximum never
            // ends the session: the sub-profile stays loaded and the squad keeps grinding.
            // The cap is only meaningful once every expected member has reported a level:
            // a member that is still missing could be the lowest one and keep the squad below
            // the maximum, so an incomplete roster is treated as unknown and never caps.
            var reportedLevelNames = new HashSet<string>(
                _history.Values.Where(r => !r.GraceExpired && r.Level > 0).Select(r => r.Name),
                StringComparer.OrdinalIgnoreCase);
            bool awaitingExpectedMember = settings.GetExpectedMemberNames()
                .Any(n => !reportedLevelNames.Contains(n));
            int profileMaxLevel = profile.ProfileMaxLevel;

            // Detect sub-profile transition -> MigratingZone.
            string subProfileKey = $"{profile.ProfileXmlPath}|{profile.ProfileXmlHash}|{profile.SubProfileName}|{profile.SubProfileMinLevel}|{profile.SubProfileMaxLevel}|{profile.MapId}";
            if (profile.HasProfile)
            {
                if (!string.IsNullOrEmpty(_lastSubProfileKey) &&
                    !string.Equals(_lastSubProfileKey, subProfileKey, StringComparison.Ordinal))
                {
                    _migratingZone = true;
                    ClearBreadcrumbs();
                }
                _lastSubProfileKey = subProfileKey;
            }

            // 3. Record movement breadcrumbs (every >= 5 yd, max 30 entries).
            UpdateBreadcrumbs(leaderLocal.MapId, leaderLocal.X, leaderLocal.Y, leaderLocal.Z, nowUtcMs);

            // 4. Evaluate distance hysteresis to living connected members on the same map.
            var leaderPoint = new WoWPoint(leaderLocal.X, leaderLocal.Y, leaderLocal.Z);
            float maxFollowerDist = 0f;
            foreach (var m in rosterStates)
            {
                if (string.Equals(m.CharacterName, leaderLocal.CharacterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (m.IsDead || m.IsGhost || m.MapId != leaderLocal.MapId)
                    continue;
                float d = leaderPoint.Distance(new WoWPoint(m.X, m.Y, m.Z));
                if (d > maxFollowerDist)
                    maxFollowerDist = d;
            }

            if (!_isWaitingForFollower && maxFollowerDist > settings.SoftWaitDistance)
                _isWaitingForFollower = true;
            else if (_isWaitingForFollower && maxFollowerDist <= settings.ResumeDistance)
                _isWaitingForFollower = false;

            if (!_isHardBacktracking && maxFollowerDist > settings.HardBacktrackDistance)
                _isHardBacktracking = true;
            else if (_isHardBacktracking && maxFollowerDist <= settings.ResumeDistance)
                _isHardBacktracking = false;

            MaxFollowerDistance = maxFollowerDist;
            bool rawWaiting = maxFollowerDist > settings.SoftWaitDistance;
            bool rawResume = maxFollowerDist <= settings.ResumeDistance;
            if (rawWaiting)
            {
                if (_waitingSinceUtcMs == 0)
                    _waitingSinceUtcMs = nowUtcMs;
                _notWaitingSinceUtcMs = 0;
                if (nowUtcMs - _waitingSinceUtcMs >= DistanceDebounceMs)
                    _isWaitingForFollowerDebounced = true;
            }
            else if (rawResume)
            {
                if (_notWaitingSinceUtcMs == 0)
                    _notWaitingSinceUtcMs = nowUtcMs;
                _waitingSinceUtcMs = 0;
                if (nowUtcMs - _notWaitingSinceUtcMs >= DistanceDebounceMs)
                    _isWaitingForFollowerDebounced = false;
            }
            else
            {
                _waitingSinceUtcMs = 0;
                _notWaitingSinceUtcMs = 0;
            }

            // 5. Evaluate combat, death/resurrection, looting, resting, provisioning, town run.
            bool anyInCombat = rosterStates.Any(m => m.IsInCombat);
            bool anyDead = rosterStates.Any(m => m.IsDead || m.IsGhost);
            bool allDead = rosterStates.Count > 0 && rosterStates.All(m => m.IsDead || m.IsGhost);
            bool hasLivingResCapable = rosterStates.Any(m =>
                !m.IsDead && !m.IsGhost &&
                m.CanResurrect);
            // A connected member that has not reported telemetry is unknown, not dead and
            // not proof that nobody can resurrect. Do not act on an incomplete roster.
            bool rosterIncomplete = connectedClients.Any(s => s.IsConnected &&
                s.LatestState == null && !string.IsNullOrWhiteSpace(s.CharacterName));

            // Level cap: the squad has finished the profile only when its lowest member passed
            // the profile maximum and every expected member reported a level. A missing or
            // not-yet-reporting member is unknown, so the cap stays false until all of them
            // are accounted for instead of ending the session on a partial roster.
            bool levelCapReached = !isDungeonMode && profile.HasProfile && profileMaxLevel > 0 &&
                !rosterIncomplete && !awaitingExpectedMember &&
                effectiveMinLevel > profileMaxLevel;

            if (_wasAnyInCombat && !anyInCombat && !allDead)
            {
                _inLootWindow = true;
                _lootWindowEndsUtcMs = nowUtcMs + Math.Max(1, settings.MaxLootWindowSeconds) * 1000L;
            }
            _wasAnyInCombat = anyInCombat;

            if (_inLootWindow)
            {
                bool anyLooting = rosterStates.Any(m => m.IsLooting);
                if (anyInCombat || (!anyLooting && nowUtcMs >= _lootWindowEndsUtcMs) || nowUtcMs >= (_lootWindowEndsUtcMs + 4000L))
                {
                    _inLootWindow = false;
                }
            }

            string? deadMemberKey = !anyInCombat
                ? rosterStates.Where(m => m.IsDead || m.IsGhost)
                    .OrderBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => $"{m.CharacterName?.Trim()}|{m.Guid}")
                    .FirstOrDefault()
                : null;

            if (deadMemberKey != null)
            {
                if (string.IsNullOrEmpty(_resWaitMemberKey))
                {
                    _resWaitStartedUtcMs = nowUtcMs;
                    _resWaitMemberKey = deadMemberKey;
                }
            }
            else if (!anyDead)
            {
                _resWaitStartedUtcMs = 0;
                _resWaitMemberKey = string.Empty;
                _inCorpseRun = false;
            }

            bool resTimeoutExceeded = _resWaitStartedUtcMs > 0 &&
                (nowUtcMs - _resWaitStartedUtcMs) >= Math.Max(5, settings.WaitForPartyRessSeconds) * 1000L;

            if (!rosterIncomplete &&
                (allDead || (anyDead && !hasLivingResCapable && !anyInCombat) || resTimeoutExceeded))
            {
                _inCorpseRun = true;
            }

            // Check migration completion
            if (_migratingZone)
            {
                var hotspot = new WoWPoint(profile.HotspotX, profile.HotspotY, profile.HotspotZ);
                bool allArrived = rosterStates.Where(m => !m.IsDead && !m.IsGhost).All(m =>
                    m.MapId == profile.MapId &&
                    new WoWPoint(m.X, m.Y, m.Z).Distance(hotspot) <= settings.SoftWaitDistance);

                if (allArrived)
                    _migratingZone = false;
            }

            // Evaluate per-member rest hysteresis.
            bool anyNeedsRest = false;
            foreach (var m in rosterStates)
            {
                if (m.IsDead || m.IsGhost)
                    continue;
                string key = m.CharacterName?.Trim() ?? string.Empty;
                bool wasResting = !string.IsNullOrEmpty(key) && _history.TryGetValue(key, out var rec) && rec.NeedsRest;
                bool isDruid = m.Class == WoWClass.Druid;
                bool usesMana = m.PowerType == WoWPowerType.Mana || isDruid;
                float powerPct = isDruid ? m.ManaPercent : m.PowerPercent;
                bool evaluated = EvaluateRestHysteresis(wasResting, m.HealthPercent, powerPct, usesMana, settings);
                bool combinedRest = evaluated || m.NeedsRest;
                if (!string.IsNullOrEmpty(key) && _history.TryGetValue(key, out var histRec))
                    histRec.NeedsRest = combinedRest;
                if (combinedRest)
                    anyNeedsRest = true;
            }

            // Evaluate TownRun trigger & completion gate. A member announcement
            // (TownRunRequested) is the explicit "idę do miasta" signal and always starts a
            // group run; the per-member Needs* flags are the automatic trigger and only
            // apply while SyncTownRuns is enabled.
            var townRally = new WoWPoint(profile.TownRallyX, profile.TownRallyY, profile.TownRallyZ);
            SquadTownRunRequest? townRequest = ResolveTownRunRequest(connectedClients, localTownRunRequest);
            bool anyInsideDungeon = isDungeonMode && (dungeon?.IsInsideDungeon == true || rosterStates.Any(m => m.IsInsideDungeon));

            _dungeonCompleted = isDungeonMode &&
                (dungeon?.ResetPendingOutside == true ||
                 (anyInsideDungeon && (dungeon?.IsDungeonComplete == true || _dungeonCompleted)));

            if (!_inTownRun)
            {
                bool anyNeedsTown = rosterStates.Any(m =>
                    !m.IsDead && !m.IsGhost && HasTownNeed(m));
                if (!anyInCombat && !anyDead && !anyInsideDungeon && (townRequest != null || (settings.SyncTownRuns && anyNeedsTown)))
                {
                    _inTownRun = true;
                    _allTownTasksComplete = false;
                    if (townRequest != null)
                    {
                        _townRunRequestedByName = townRequest.CharacterName;
                        _townRunRequestReason = townRequest.Reason;
                        _townRunRequestedUtcMs = townRequest.RequestedUtcMs;
                        // Consume every pending announcement, including older ones, so a
                        // superseded request cannot resurface after the run finishes.
                        AckAllTownRunRequests(connectedClients, localTownRunRequest);
                    }
                    else
                    {
                        _townRunRequestedByName = string.Empty;
                        _townRunRequestReason = string.Empty;
                        _townRunRequestedUtcMs = 0;
                    }
                }
            }
            else
            {
                // A second member may announce while the run is already active: keep the
                // newest announcement visible without restarting the run.
                if (townRequest != null)
                {
                    AckAllTownRunRequests(connectedClients, localTownRunRequest);
                    _townRunRequestedByName = townRequest.CharacterName;
                    _townRunRequestReason = townRequest.Reason;
                    _townRunRequestedUtcMs = townRequest.RequestedUtcMs;
                }

                // Completion must account for every connected participant, including a
                // reconnecting member that has not yet sent a telemetry sample.
                var townParticipants = new List<SquadMemberState>(1 + connectedClients.Count) { leaderLocal };
                foreach (var snap in connectedClients.Where(s => s.IsConnected && !string.IsNullOrWhiteSpace(s.CharacterName)))
                {
                    townParticipants.Add(snap.LatestState ?? new SquadMemberState
                    {
                        CharacterName = snap.CharacterName.Trim(),
                        Guid = snap.Handshake.Guid,
                        Class = snap.Handshake.Class,
                        Role = snap.Handshake.Role,
                        Level = snap.Handshake.Level
                    });
                }
                bool pendingTelemetry = connectedClients.Any(s => s.IsConnected && s.LatestState == null &&
                    !string.IsNullOrWhiteSpace(s.CharacterName));
                bool anyStillHasNeed = townParticipants.Any(m => HasTownNeed(m));
                bool allDoneAndAtRally = !pendingTelemetry && townParticipants.Count > 0 && townParticipants.All(m =>
                    m.TownTasksComplete &&
                    (!profile.TownRallyMapId.HasValue || m.MapId == profile.TownRallyMapId.Value) &&
                    new WoWPoint(m.X, m.Y, m.Z).Distance2D(townRally) <= 20.0f);

                if (!anyInCombat && !anyDead && allDoneAndAtRally && !anyStillHasNeed)
                {
                    _allTownTasksComplete = true;
                    _inTownRun = false;
                    _townRunRequestedByName = string.Empty;
                    _townRunRequestReason = string.Empty;
                    _townRunRequestedUtcMs = 0;
                }
                else
                {
                    _allTownTasksComplete = false;
                }
            }

            // Provisioning arbitration. A dry healer is a wipe waiting to happen, so a
            // mage-to-healer water run stays allowed in combat windows when both are idle.
            string? waterOnlyRecipient = anyInCombat
                ? SelectDryHealerWaterRecipient(rosterStates, settings)
                : null;
            bool provisioningBlocked = anyDead || _inTownRun || _migratingZone ||
                (anyInCombat && string.IsNullOrEmpty(waterOnlyRecipient));
            if (provisioningBlocked)
            {
                if (!string.IsNullOrEmpty(_activeProvisioner))
                {
                    Logging.WriteDebug("[PartyGrind Provision] Cleared '{0}' (combat/death/town/rest/migration)",
                        _activeProvisioner);
                    SquadPartySyncService.MarkActivity();
                }
                ClearProvisioning();
            }
            else
            {
                EvaluateProvisioningArbitration(settings, rosterStates, nowUtcMs,
                    dungeon?.HasRandomPartyMembers != true, waterOnlyRecipient);
            }

            SquadPhase phase;
            string pauseReason = string.Empty;
            if (IsPausedManually)
            {
                phase = SquadPhase.Paused;
                pauseReason = ManualPauseReason;
            }
            else if (!anyInCombat && !anyDead && !anyInsideDungeon && rosterStates.Any(m => !string.IsNullOrEmpty(m.TownBlockedReason)))
            {
                // Keep the town block pending, but never let it suppress combat or
                // death recovery that must be resolved first.
                phase = SquadPhase.Paused;
                pauseReason = rosterStates.First(m => !string.IsNullOrEmpty(m.TownBlockedReason)).TownBlockedReason;
            }
            else if (!isDungeonMode && !profile.HasProfile)
            {
                phase = SquadPhase.Initializing;
            }
            else if (_inCorpseRun && anyDead)
            {
                phase = SquadPhase.CorpseRun;
            }
            else if (anyDead && !anyInCombat)
            {
                phase = SquadPhase.WaitingResurrection;
            }
            else if (isDungeonMode)
            {
                if (anyInCombat)
                {
                    phase = SquadPhase.DungeonClearing;
                }
                else if (anyInsideDungeon && _dungeonCompleted && !_inLootWindow)
                {
                    phase = SquadPhase.DungeonCompleteExit;
                }
                else if (_inLootWindow && (settings.DungeonLootMode == SquadDungeonLootMode.AllTrashAndBosses || _dungeonCompleted))
                {
                    phase = SquadPhase.Looting;
                }
                else if (!string.IsNullOrEmpty(_activeProvisioner) &&
                         (_assignedHealthstone || dungeon?.HasRandomPartyMembers != true))
                {
                    phase = SquadPhase.Provisioning;
                }
                else if (anyNeedsRest)
                {
                    phase = SquadPhase.Resting;
                }
                else if (!anyInsideDungeon && _inTownRun)
                {
                    phase = SquadPhase.TownRun;
                }
                else if (!anyInsideDungeon && lockoutRemainingSeconds > 0 && settings.DungeonEntryMode == SquadDungeonEntryMode.WalkInPortalFarm)
                {
                    phase = SquadPhase.DungeonLockoutWait;
                }
                else if (anyInsideDungeon)
                {
                    phase = SquadPhase.DungeonClearing;
                }
                else if (settings.DungeonEntryMode == SquadDungeonEntryMode.WalkInPortalFarm)
                {
                    phase = SquadPhase.DungeonTravelToPortal;
                }
                else
                {
                    phase = SquadPhase.DungeonQueueing;
                }
            }
            else if (anyInCombat)
            {
                phase = _migratingZone ? SquadPhase.MigratingZone : SquadPhase.Grinding;
            }
            else if (_migratingZone)
            {
                phase = SquadPhase.MigratingZone;
            }
            else if (_inLootWindow)
            {
                // Only the bounded loot window drives this phase; a stale follower
                // IsLooting flag must not keep the squad in Looting after its hard cap.
                phase = SquadPhase.Looting;
            }
            else if (!string.IsNullOrEmpty(_activeProvisioner))
            {
                phase = SquadPhase.Provisioning;
            }
            else if (anyNeedsRest)
            {
                phase = SquadPhase.Resting;
            }
            else if (_inTownRun)
            {
                phase = SquadPhase.TownRun;
            }
            else
            {
                phase = SquadPhase.Grinding;
            }

            if (_lastLoggedPhase != phase)
            {
                Logging.WriteDebug("[PartyGrind Phase] {0} -> {1} (provisioner='{2}', target='{3}', pause='{4}')",
                    _lastLoggedPhase?.ToString() ?? "init", phase, _activeProvisioner, _activeTradeTarget, pauseReason);
                SquadPartySyncService.MarkActivity();
                _lastLoggedPhase = phase;
            }

            // 7. Build member summaries for HUD & peer services.
            var summaries = BuildMemberSummaries(rosterStates, connectedNames, leaderPoint, leaderLocal.MapId, nowUtcMs);

            return new SquadLeaderState
            {
                LeaderGuid = leaderLocal.Guid,
                LeaderName = leaderLocal.CharacterName ?? string.Empty,
                Phase = phase,
                PauseReason = pauseReason,
                ProfileXmlPath = profile.ProfileXmlPath,
                ProfileXmlHash = profile.ProfileXmlHash,
                ActiveSubProfileName = profile.SubProfileName,
                ActiveSubProfileMinLevel = profile.SubProfileMinLevel,
                ActiveSubProfileMaxLevel = profile.SubProfileMaxLevel,
                ProfileMaxLevel = profileMaxLevel,
                EffectiveSquadMinLevel = effectiveMinLevel,
                LowestMemberName = lowestName,
                LowestMemberXpPercent = lowestXpPct,
                LevelCapReached = levelCapReached,
                MapId = leaderLocal.MapId,
                ZoneId = leaderLocal.ZoneId,
                X = leaderLocal.X,
                Y = leaderLocal.Y,
                Z = leaderLocal.Z,
                Facing = leaderFacing,
                IsMounted = leaderLocal.IsMounted,
                IsFlying = false,
                InCombat = anyInCombat,
                IsWaitingForFollower = _isWaitingForFollowerDebounced,
                ResumeDistance = settings.ResumeDistance,
                IsLootWindowActive = _inLootWindow,
                IsDead = leaderLocal.IsDead,
                IsGhost = leaderLocal.IsGhost,
                FocusTargetGuid = focusTargetGuid,
                SecondaryTargetGuid = secondaryTargetGuid,
                PullStartedUtcMs = pullStartedUtcMs,
                ActiveHotspotX = profile.HotspotX,
                ActiveHotspotY = profile.HotspotY,
                ActiveHotspotZ = profile.HotspotZ,
                Breadcrumbs = _breadcrumbs.Select(b => new SquadBreadcrumb
                {
                    MapId = b.MapId,
                    X = b.X,
                    Y = b.Y,
                    Z = b.Z,
                    TimestampUtcMs = b.TimestampUtcMs,
                    RouteGeneration = b.RouteGeneration,
                    Sequence = b.Sequence
                }).ToList(),
                TownRallyX = profile.TownRallyX,
                TownRallyY = profile.TownRallyY,
                TownRallyZ = profile.TownRallyZ,
                TownRallyMapId = profile.TownRallyMapId,
                AllTownTasksComplete = _allTownTasksComplete,
                TownRunInterrupted = _inTownRun && (anyInCombat || anyDead),
                TownRunRequestedByName = _townRunRequestedByName,
                TownRunRequestReason = _townRunRequestReason,
                TownRunRequestedUtcMs = _townRunRequestedUtcMs,
                TownRunRequestAcknowledgements = new Dictionary<string, long>(_townRunRequestAckUtcMs, StringComparer.OrdinalIgnoreCase),
                SquadCorpseRallyX = profile.SafeCorpseRallyX,
                SquadCorpseRallyY = profile.SafeCorpseRallyY,
                SquadCorpseRallyZ = profile.SafeCorpseRallyZ,
                ReleaseCorpseOrder = !rosterIncomplete && _inCorpseRun && anyDead,
                ActiveProvisionerName = _activeProvisioner,
                ActiveTradeTargetName = _activeTradeTarget,
                BotMode = settings.BotMode,
                DungeonEntryMode = settings.DungeonEntryMode,
                DungeonLootMode = settings.DungeonLootMode,
                KillOptionalBosses = settings.KillOptionalBosses,
                IsInsideDungeon = dungeon?.IsInsideDungeon == true || leaderLocal.IsInsideDungeon,
                ActiveDungeonId = dungeon?.ActiveDungeonId ?? 0U,
                ActiveDungeonMapId = dungeon?.ActiveDungeonMapId ?? 0U,
                ActiveDungeonName = dungeon?.ActiveDungeonName ?? string.Empty,
                CurrentBossEntry = dungeon?.CurrentBossEntry ?? 0U,
                CurrentBossName = dungeon?.CurrentBossName ?? string.Empty,
                KilledBossCount = dungeon?.KilledBossCount ?? 0,
                TotalBossCount = dungeon?.TotalBossCount ?? 0,
                IsDungeonComplete = _dungeonCompleted,
                AcceptLfgProposalOrder = isDungeonMode && !_inTownRun && !anyDead && (dungeon?.LfgProposalPending == true || rosterStates.Any(m => m.LfgProposalPending)),
                DungeonResetPendingOutside = isDungeonMode && dungeon?.ResetPendingOutside == true,
                TeleportOutOrder = isDungeonMode && phase == SquadPhase.DungeonCompleteExit && settings.DungeonEntryMode != SquadDungeonEntryMode.WalkInPortalFarm,
                LfgStatusText = dungeon?.LfgMode ?? string.Empty,
                HasRandomPartyMembers = dungeon?.HasRandomPartyMembers == true,
                EffectiveTankGuid = dungeon?.EffectiveTankGuid ?? (settings.Role == SquadRole.TankLeader ? leaderLocal.Guid : 0UL),
                DungeonPortalX = dungeon?.PortalX ?? 0f,
                DungeonPortalY = dungeon?.PortalY ?? 0f,
                DungeonPortalZ = dungeon?.PortalZ ?? 0f,
                DungeonPortalMapId = dungeon?.PortalMapId ?? 0U,
                DungeonExitX = dungeon?.ExitX ?? 0f,
                DungeonExitY = dungeon?.ExitY ?? 0f,
                DungeonExitZ = dungeon?.ExitZ ?? 0f,
                InstanceResetGeneration = instanceResetGeneration,
                HourlyInstancesRunCount = hourlyInstancesRunCount,
                LockoutRemainingSeconds = lockoutRemainingSeconds,
                MemberAssignments = assignments,
                MemberRoles = roles,
                Members = summaries
            };
        }

        /// <summary>
        /// Picks the newest not-yet-consumed town-run announcement. Acknowledgement is
        /// deliberately left to the caller: a request deferred by combat must stay
        /// pending so it can still start the run once the fight is over.
        /// </summary>
        private SquadTownRunRequest? ResolveTownRunRequest(
            IReadOnlyList<SquadConnectedMemberSnapshot> connectedClients,
            SquadTownRunRequest? localRequest)
        {
            SquadTownRunRequest? best = null;
            ConsiderTownRunRequest(localRequest, ref best);
            foreach (var snap in connectedClients)
            {
                if (!snap.IsConnected)
                    continue;
                ConsiderTownRunRequest(snap.LatestTownRunRequest, ref best);
            }
            return best;
        }

        private void ConsiderTownRunRequest(SquadTownRunRequest? request, ref SquadTownRunRequest? best)
        {
            if (!IsTownRunRequestActionable(request))
                return;
            if (best == null || request!.RequestedUtcMs > best.RequestedUtcMs)
                best = request;
        }

        private void AckAllTownRunRequests(
            IReadOnlyList<SquadConnectedMemberSnapshot> connectedClients,
            SquadTownRunRequest? localRequest)
        {
            AckTownRunRequest(localRequest);
            foreach (var snap in connectedClients)
            {
                if (snap.IsConnected)
                    AckTownRunRequest(snap.LatestTownRunRequest);
            }
        }

        private bool IsTownRunRequestActionable(SquadTownRunRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CharacterName) || request.RequestedUtcMs <= 0)
                return false;
            string name = request.CharacterName.Trim();
            return !_townRunRequestAckUtcMs.TryGetValue(name, out long ack) || request.RequestedUtcMs > ack;
        }

        private void AckTownRunRequest(SquadTownRunRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CharacterName))
                return;
            string name = request.CharacterName.Trim();
            if (!_townRunRequestAckUtcMs.TryGetValue(name, out long ack) || request.RequestedUtcMs > ack)
                _townRunRequestAckUtcMs[name] = request.RequestedUtcMs;
        }

        private static bool HasTownNeed(SquadMemberState m)
        {
            return m.NeedsSell || m.NeedsRepair || m.NeedsBuy || m.NeedsTrain || m.NeedsMail;
        }

        /// <summary>
        /// Rest hysteresis check shared between Coordinator and Members.
        /// </summary>
        public static bool EvaluateRestHysteresis(
            bool wasNeedingRest,
            float healthPct,
            float powerPct,
            bool usesMana,
            SquadSettings settings)
        {
            if (wasNeedingRest)
            {
                bool hpOk = healthPct >= settings.ResumeRestHealthPct;
                bool mpOk = !usesMana || powerPct >= settings.ResumeRestManaPct;
                return !(hpOk && mpOk);
            }

            bool lowHp = healthPct < settings.TriggerRestHealthPct;
            bool lowMp = usesMana && powerPct < settings.TriggerRestManaPct;
            return lowHp || lowMp;
        }

        public WoWPoint? GetBacktrackTarget(WoWPoint currentLeaderPos, WoWPoint trailingMemberPos)
        {
            if (_breadcrumbs.Count == 0)
                return trailingMemberPos;

            // Walk reversed breadcrumbs to find the point roughly halfway back toward the trailing member.
            for (int i = _breadcrumbs.Count - 1; i >= 0; i--)
            {
                var bc = _breadcrumbs[i];
                var pt = new WoWPoint(bc.X, bc.Y, bc.Z);
                if (pt.Distance(currentLeaderPos) >= 10f &&
                    pt.Distance(trailingMemberPos) < currentLeaderPos.Distance(trailingMemberPos))
                    return pt;
            }
            return trailingMemberPos;
        }

        public void RecordProvisionCooldown(string characterName, int cooldownSeconds, long nowUtcMs)
        {
            if (string.IsNullOrWhiteSpace(characterName))
                return;
            _provisionCooldownUntilMs[characterName.Trim()] = nowUtcMs + Math.Max(1, cooldownSeconds) * 1000L;
        }

        private void UpsertHistory(SquadMemberState state, string source, long nowUtcMs, int handshakeLevel = 0)
        {
            string name = state.CharacterName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                return;

            if (!_history.TryGetValue(name, out var rec) || (state.Guid != 0 && rec.Guid != 0 && state.Guid != rec.Guid))
            {
                rec = new MemberHistoryRecord { Name = name };
                _history[name] = rec;
            }

            if (rec.Disconnected)
                Logging.WriteDebug("[PartyGrind Level] Reconnected member={0} guid={1} afterMs={2}", name, state.Guid, nowUtcMs - rec.LastSeenUtcMs);
            rec.Disconnected = false;
            rec.GraceExpired = false;
            if (state.Guid != 0) rec.Guid = state.Guid;
            rec.Role = state.Role;
            rec.Class = state.Class;
            int previousLevel = rec.Level;
            if (handshakeLevel > rec.Level && handshakeLevel <= byte.MaxValue)
            {
                rec.Level = handshakeLevel;
                rec.XpPercent = 0f;
                rec.LevelSource = "handshake";
            }
            if (state.Level is <= 0 or > byte.MaxValue || state.Level < rec.Level)
            {
                if (rec.RejectedLevel != (source, state.Level))
                    Logging.WriteDebug("[PartyGrind Level] Rejected member={0} guid={1} source={2} raw={3} confirmed={4} confirmedSource={5}",
                        name, state.Guid, source, state.Level, rec.Level, rec.LevelSource);
                rec.RejectedLevel = (source, state.Level);
            }
            else
            {
                if (rec.RejectedLevel.HasValue)
                    Logging.WriteDebug("[PartyGrind Level] Recovered member={0} guid={1} source={2} raw={3}", name, state.Guid, source, state.Level);
                rec.RejectedLevel = null;
                if (source != "handshake")
                {
                    float xp = ComputeXpPercent(state);
                    rec.XpPercent = state.Level == rec.Level ? Math.Max(rec.XpPercent, xp) : xp;
                }
                rec.Level = state.Level;
                rec.LevelSource = source;
            }
            if (rec.Level != previousLevel)
                Logging.WriteDebug("[PartyGrind Level] Accepted member={0} guid={1} source={2} level={3}->{4}",
                    name, rec.Guid, rec.LevelSource, previousLevel, rec.Level);
            rec.LastSeenUtcMs = nowUtcMs;
            rec.LastState = state;
        }

        private void ClearBreadcrumbs()
        {
            _breadcrumbs.Clear();
            _breadcrumbGeneration++;
        }

        private void UpdateBreadcrumbs(uint mapId, float x, float y, float z, long nowUtcMs)
        {
            if (_lastBreadcrumbMapId != mapId)
            {
                ClearBreadcrumbs();
                _lastBreadcrumbMapId = mapId;
            }

            var current = new WoWPoint(x, y, z);
            if (_breadcrumbs.Count == 0)
            {
                _breadcrumbs.Add(new SquadBreadcrumb
                {
                    MapId = mapId, X = x, Y = y, Z = z, TimestampUtcMs = nowUtcMs,
                    RouteGeneration = _breadcrumbGeneration, Sequence = ++_breadcrumbSequence
                });
                return;
            }

            var last = _breadcrumbs[^1];
            if (current.Distance(new WoWPoint(last.X, last.Y, last.Z)) >= 5.0f)
            {
                _breadcrumbs.Add(new SquadBreadcrumb
                {
                    MapId = mapId, X = x, Y = y, Z = z, TimestampUtcMs = nowUtcMs,
                    RouteGeneration = _breadcrumbGeneration, Sequence = ++_breadcrumbSequence
                });
                while (_breadcrumbs.Count > 30)
                    _breadcrumbs.RemoveAt(0);
            }
        }

        private static bool CanProvideHealthstoneTo(SquadMemberState provider, SquadMemberState receiver) =>
            provider.CanProvideHealthstone && provider.HealthstoneMinReceiverLevel is int minLevel && receiver.Level >= minLevel;

        internal static bool ClassUsesDrinks(WoWClass cls) =>
            cls == WoWClass.Mage || cls == WoWClass.Priest || cls == WoWClass.Warlock ||
            cls == WoWClass.Druid || cls == WoWClass.Shaman || cls == WoWClass.Paladin;

        /// <summary>
        /// A member whose bag-slot guard or momentary combat flag suppressed <c>WantsDrink</c> still
        /// needs water. Without this the arbitration resolves the pair as "needs resolved" while the
        /// healer's own bag count is empty, which is how a dry healer entered the instance.
        /// </summary>
        internal static bool SquadWantsDrink(SquadMemberSummary member, SquadSettings settings)
        {
            if (member == null)
                return false;
            if (member.WantsDrink)
                return true;
            return member.FreeBagSlots >= 2 &&
                ClassUsesDrinks(member.Class) &&
                member.DrinkCount < settings.MinMemberDrinkStack;
        }

        /// <summary>
        /// Picks a squad healer that is out of mana and out of drink while everyone is idle enough
        /// for a mage to hand water over. Returns null when no such window exists.
        /// </summary>
        internal static string? SelectDryHealerWaterRecipient(
            IReadOnlyList<SquadMemberState> rosterStates,
            SquadSettings settings)
        {
            if (rosterStates == null || !settings.EnableMageProvisioning)
                return null;

            bool mageReady = rosterStates.Any(m =>
                !m.IsDead && !m.IsGhost && !m.IsInCombat && m.CanProvideMageDrink);
            if (!mageReady)
                return null;

            var healer = rosterStates.FirstOrDefault(m =>
                !m.IsDead && !m.IsGhost && !m.IsInCombat &&
                (m.Role == SquadRole.Healer || m.Role == SquadRole.HybridOffHeal) &&
                SquadWantsDrink(m, settings) &&
                m.ManaPercent < 40f &&
                m.FreeBagSlots >= 2);
            return healer?.CharacterName;
        }

        private void EvaluateProvisioningArbitration(
            SquadSettings settings,
            List<SquadMemberState> rosterStates,
            long nowUtcMs,
            bool allowFoodAndDrink,
            string? waterOnlyRecipient = null)
        {
            if (!allowFoodAndDrink && (_assignedFood || _assignedDrink))
            {
                ClearProvisioning();
                SquadPartySyncService.MarkActivity();
            }

            long timeoutMs = Math.Max(4, settings.TradeOfferTimeoutSeconds + 3) * 1000L;

            if (!string.IsNullOrEmpty(_activeProvisioner))
            {
                var prov = rosterStates.FirstOrDefault(m => string.Equals(m.CharacterName, _activeProvisioner, StringComparison.OrdinalIgnoreCase));
                var recv = string.IsNullOrEmpty(_activeTradeTarget)
                    ? null
                    : rosterStates.FirstOrDefault(m => string.Equals(m.CharacterName, _activeTradeTarget, StringComparison.OrdinalIgnoreCase));

                if (_assignedHealthstone && recv != null && prov?.IsHealthstoneRitualPending == true)
                    return;

                bool timedOut = (nowUtcMs - _provisionStartedUtcMs) > timeoutMs;
                bool receiverUnknown = !string.IsNullOrEmpty(_activeTradeTarget) && recv == null;
                bool stillNeeded = prov != null && (prov.IsProvisioningBusy || receiverUnknown ||
                    (recv != null && ((_assignedFood && recv.WantsFood) ||
                        (_assignedDrink && SquadWantsDrink(recv, settings)) || (_assignedHealthstone && recv.WantsHealthstone))));

                bool healthstoneSupplyLost = _assignedHealthstone &&
                    (prov == null || (!prov.IsProvisioningBusy &&
                        (recv == null || !CanProvideHealthstoneTo(prov, recv))));

                if (healthstoneSupplyLost)
                {
                    Logging.WriteDebug("[PartyGrind Provision] Released '{0}' -> '{1}' (healthstone unavailable)",
                        _activeProvisioner, _activeTradeTarget);
                    SquadPartySyncService.MarkActivity();
                    ClearProvisioning();
                }
                else if (timedOut)
                {
                    if (!string.IsNullOrEmpty(_activeTradeTarget))
                        RecordProvisionCooldown(_activeTradeTarget, settings.ProvisionCooldownSeconds, nowUtcMs);
                    Logging.WriteDebug("[PartyGrind Provision] Timeout '{0}' -> '{1}' (cooldown {2}s)",
                        _activeProvisioner, _activeTradeTarget, settings.ProvisionCooldownSeconds);
                    SquadPartySyncService.MarkActivity();
                    ClearProvisioning();
                }
                else if (stillNeeded)
                {
                    return;
                }
                else
                {
                    Logging.WriteDebug("[PartyGrind Provision] Done '{0}' -> '{1}' (needs resolved)",
                        _activeProvisioner, _activeTradeTarget);
                    SquadPartySyncService.MarkActivity();
                    ClearProvisioning();
                }
            }

            // Pick next provisioner -> target pair (Mage first, then Warlock).
            if (allowFoodAndDrink && settings.EnableMageProvisioning)
            {
                foreach (var mage in rosterStates.Where(m => !m.IsDead && !m.IsGhost && (m.CanProvideMageFood || m.CanProvideMageDrink)))
                {
                    var magePos = new WoWPoint(mage.X, mage.Y, mage.Z);
                    var target = rosterStates.FirstOrDefault(m =>
                        !m.IsDead && !m.IsGhost &&
                        !string.Equals(m.CharacterName, mage.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        m.MapId == mage.MapId &&
                        magePos.Distance(new WoWPoint(m.X, m.Y, m.Z)) <= 40f &&
                        m.FreeBagSlots >= 2 &&
                        (waterOnlyRecipient == null || string.Equals(m.CharacterName, waterOnlyRecipient, StringComparison.OrdinalIgnoreCase)) &&
                        ((mage.CanProvideMageFood && m.WantsFood) || (mage.CanProvideMageDrink && SquadWantsDrink(m, settings))) &&
                        !IsOnProvisionCooldown(m.CharacterName, nowUtcMs));

                    if (target != null)
                    {
                        _assignedFood = waterOnlyRecipient == null && mage.CanProvideMageFood && target.WantsFood;
                        _assignedDrink = mage.CanProvideMageDrink && SquadWantsDrink(target, settings);
                        _assignedHealthstone = false;
                        _activeProvisioner = mage.CharacterName;
                        _activeTradeTarget = target.CharacterName;
                        _provisionStartedUtcMs = nowUtcMs;
                        Logging.WriteDebug("[PartyGrind Provision] Assigned '{0}' -> '{1}' (food={2}, drink={3})",
                            mage.CharacterName, target.CharacterName, _assignedFood, _assignedDrink);
                        SquadPartySyncService.MarkActivity();
                        return;
                    }
                }
            }

            if (settings.EnableWarlockHealthstones && waterOnlyRecipient == null)
            {
                foreach (var warlock in rosterStates.Where(m => !m.IsDead && !m.IsGhost && m.CanProvideHealthstone))
                {
                    var lockPos = new WoWPoint(warlock.X, warlock.Y, warlock.Z);
                    var target = rosterStates.FirstOrDefault(m =>
                        !m.IsDead && !m.IsGhost &&
                        !string.Equals(m.CharacterName, warlock.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        m.MapId == warlock.MapId &&
                        lockPos.Distance(new WoWPoint(m.X, m.Y, m.Z)) <= 40f &&
                        m.FreeBagSlots >= 2 &&
                        m.WantsHealthstone && CanProvideHealthstoneTo(warlock, m) &&
                        !IsOnProvisionCooldown(m.CharacterName, nowUtcMs));

                    if (target != null)
                    {
                        _assignedFood = _assignedDrink = false;
                        _assignedHealthstone = true;
                        _activeProvisioner = warlock.CharacterName;
                        _activeTradeTarget = target.CharacterName;
                        _provisionStartedUtcMs = nowUtcMs;
                        Logging.WriteDebug("[PartyGrind Provision] Assigned '{0}' -> '{1}' (healthstone)",
                            warlock.CharacterName, target.CharacterName);
                        SquadPartySyncService.MarkActivity();
                        return;
                    }
                }
            }
        }

        private bool IsOnProvisionCooldown(string name, long nowUtcMs)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                _provisionCooldownUntilMs.TryGetValue(name.Trim(), out long until) &&
                nowUtcMs < until;
        }

        private void ClearProvisioning()
        {
            _activeProvisioner = string.Empty;
            _activeTradeTarget = string.Empty;
            _provisionStartedUtcMs = 0;
        }

        private List<SquadMemberSummary> BuildMemberSummaries(
            List<SquadMemberState> activeRoster,
            HashSet<string> connectedNames,
            WoWPoint leaderPoint,
            uint leaderMapId,
            long nowUtcMs)
        {
            var result = new List<SquadMemberSummary>();
            var activeByName = new Dictionary<string, SquadMemberState>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in activeRoster)
            {
                if (!string.IsNullOrWhiteSpace(m.CharacterName))
                    activeByName[m.CharacterName.Trim()] = m;
            }

            foreach (var m in activeRoster)
            {
                result.Add(ToSummary(m, isConnected: true, leaderPoint, leaderMapId));
            }

            foreach (var kv in _history)
            {
                if (kv.Value.GraceExpired || activeByName.ContainsKey(kv.Key))
                    continue;
                if (kv.Value.LastState == null)
                {
                    result.Add(new SquadMemberSummary
                    {
                        CharacterName = kv.Value.Name, Guid = kv.Value.Guid,
                        Role = kv.Value.Role, Class = kv.Value.Class, Level = kv.Value.Level, XpPercent = kv.Value.XpPercent,
                        HasTelemetry = false, IsConnected = connectedNames.Contains(kv.Key),
                        DistanceToLeader = -1f, StateLabel = "Awaiting telemetry"
                    });
                }
                else
                {
                    var summary = ToSummary(kv.Value.LastState, isConnected: connectedNames.Contains(kv.Key), leaderPoint, leaderMapId);
                    summary.StateLabel = connectedNames.Contains(kv.Key) ? "Awaiting telemetry" : "Disconnected";
                    result.Add(summary);
                }
            }

            return result
                .OrderBy(s => s.Role == SquadRole.TankLeader ? 0 : 1)
                .ThenBy(s => s.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private SquadMemberSummary ToSummary(SquadMemberState m, bool isConnected, WoWPoint leaderPoint, uint leaderMapId)
        {
            _history.TryGetValue(m.CharacterName.Trim(), out var rec);
            string label = m.IsDead || m.IsGhost ? "Dead"
                : m.IsInCombat ? "Combat"
                : !m.TownTasksComplete && (m.NeedsSell || m.NeedsRepair || m.NeedsBuy || m.NeedsTrain || m.NeedsMail) && _inTownRun ? "TownRun"
                : m.IsResting || m.NeedsRest ? "Resting"
                : m.IsLooting ? "Looting"
                : m.IsProvisioningBusy ? "Provisioning"
                : "Grinding";

            float dist = m.MapId == leaderMapId ? leaderPoint.Distance(new WoWPoint(m.X, m.Y, m.Z)) : -1f;
            return new SquadMemberSummary
            {
                CharacterName = m.CharacterName,
                Guid = m.Guid,
                Role = m.Role,
                Class = m.Class,
                Level = rec?.Level ?? m.Level,
                XpPercent = rec?.XpPercent ?? ComputeXpPercent(m),
                XpPerHour = m.XpPerHour,
                HealthPercent = m.HealthPercent,
                PowerPercent = m.PowerPercent,
                PowerType = m.PowerType,
                MapId = m.MapId,
                X = m.X,
                Y = m.Y,
                Z = m.Z,
                DistanceToLeader = dist,
                IsDead = m.IsDead,
                IsGhost = m.IsGhost,
                IsInCombat = m.IsInCombat,
                NeedsRest = m.NeedsRest,
                IsResting = m.IsResting,
                IsLooting = m.IsLooting,
                NeedsTown = m.NeedsSell || m.NeedsRepair || m.NeedsBuy || m.NeedsTrain || m.NeedsMail,
                TownTasksComplete = m.TownTasksComplete,
                KnownBlessings = m.KnownBlessings,
                CanMount = m.CanMount,
                CanFly = m.CanFly,
                IsMounted = m.IsMounted,
                FreeBagSlots = m.FreeBagSlots,
                WantsFood = m.WantsFood,
                WantsDrink = m.WantsDrink,
                WantsHealthstone = m.WantsHealthstone,
                CanProvideMageFood = m.CanProvideMageFood,
                CanProvideMageDrink = m.CanProvideMageDrink,
                CanProvideHealthstone = m.CanProvideHealthstone,
                IsProvisioningBusy = m.IsProvisioningBusy,
                IsInsideDungeon = m.IsInsideDungeon,
                IsConnected = isConnected,
                StateLabel = label
            };
        }

        private static float ComputeXpPercent(SquadMemberState m)
        {
            if (m.NextLevelXp == 0)
                return 0f;
            float pct = (m.CurrentXp * 100f) / m.NextLevelXp;
            return Math.Clamp(pct, 0f, 100f);
        }
    }
}
