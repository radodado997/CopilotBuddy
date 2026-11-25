using System;
using System.Collections.Generic;
using System.Linq;
using Bots.DungeonBuddy;
using Bots.DungeonBuddy.Avoidance;
using Bots.DungeonBuddy.Enums;
using Styx;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.DBC;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

using DbProfileManager = global::Bots.DungeonBuddy.Profiles.ProfileManager;
using AvoidanceManager = global::Bots.DungeonBuddy.Avoidance.AvoidanceManager;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    public sealed class SquadDungeonSnapshot
    {
        public bool IsInsideDungeon { get; set; }
        public uint ActiveDungeonId { get; set; }
        public uint ActiveDungeonMapId { get; set; }
        public string ActiveDungeonName { get; set; } = string.Empty;
        public uint CurrentBossEntry { get; set; }
        public string CurrentBossName { get; set; } = string.Empty;
        public int KilledBossCount { get; set; }
        public int TotalBossCount { get; set; }
        public bool IsDungeonComplete { get; set; }
        public bool ResetPendingOutside { get; set; }
        public ulong[] ResetExitConfirmedGuids { get; set; } = Array.Empty<ulong>();
        public long ResetExitEvidenceValidUntilUtcMs { get; set; }
        public bool LfgProposalPending { get; set; }
        public string LfgMode { get; set; } = string.Empty;
        public bool HasRandomPartyMembers { get; set; }
        public ulong EffectiveTankGuid { get; set; }
        public float PortalX { get; set; }
        public float PortalY { get; set; }
        public float PortalZ { get; set; }
        public uint PortalMapId { get; set; }
        public float ExitX { get; set; }
        public float ExitY { get; set; }
        public float ExitZ { get; set; }
    }

    public readonly struct DungeonBracketEntry
    {
        public DungeonBracketEntry(uint dungeonId, int minLevel, int maxLevel, bool allianceFriendly, bool hordeFriendly)
        {
            DungeonId = dungeonId;
            MinLevel = minLevel;
            MaxLevel = maxLevel;
            AllianceFriendly = allianceFriendly;
            HordeFriendly = hordeFriendly;
        }

        public uint DungeonId { get; }
        public int MinLevel { get; }
        public int MaxLevel { get; }
        public bool AllianceFriendly { get; }
        public bool HordeFriendly { get; }
    }

    public sealed class SquadDungeonService
    {
        private static readonly DungeonBracketEntry[] DefaultBracketPlaylist =
        {
            new DungeonBracketEntry(4, 13, 18, false, true),
            new DungeonBracketEntry(6, 16, 22, true, false),
            new DungeonBracketEntry(1, 17, 24, true, true),
            new DungeonBracketEntry(8, 18, 25, true, true),
            new DungeonBracketEntry(12, 22, 29, true, false),
            new DungeonBracketEntry(10, 22, 28, true, true),
            new DungeonBracketEntry(16, 25, 32, true, true),
            new DungeonBracketEntry(18, 28, 33, true, true),
            new DungeonBracketEntry(163, 31, 36, true, true),
            new DungeonBracketEntry(164, 34, 39, true, true),
            new DungeonBracketEntry(165, 36, 42, true, true),
            new DungeonBracketEntry(20, 35, 42, true, true),
            new DungeonBracketEntry(22, 37, 44, true, true),
            new DungeonBracketEntry(24, 42, 50, true, true),
            new DungeonBracketEntry(28, 46, 53, true, true),
            new DungeonBracketEntry(30, 50, 56, true, true),
            new DungeonBracketEntry(276, 54, 60, true, true),
            new DungeonBracketEntry(40, 56, 61, true, true),
            new DungeonBracketEntry(136, 59, 63, true, true),
            new DungeonBracketEntry(137, 60, 64, true, true),
            new DungeonBracketEntry(140, 61, 65, true, true),
            new DungeonBracketEntry(146, 63, 67, true, true),
            new DungeonBracketEntry(148, 64, 68, true, true),
            new DungeonBracketEntry(202, 68, 72, true, true),
            new DungeonBracketEntry(225, 70, 74, true, true),
            new DungeonBracketEntry(204, 71, 75, true, true),
            new DungeonBracketEntry(214, 73, 77, true, true),
            new DungeonBracketEntry(216, 75, 79, true, true),
            new DungeonBracketEntry(207, 77, 80, true, true),
            new DungeonBracketEntry(206, 78, 80, true, true)
        };

        private readonly List<long> _instanceResetTimestampsUtcMs = new List<long>();
        private bool _scriptsLoaded;
        private bool _lfgEventsAttached;
        private uint _lastSyncedMapId;
        private uint _lastSyncedDungeonId;
        private long _lastLfgPollUtcMs;
        private long _lastQueueAttemptUtcMs;
        private long _lastRoleSetUtcMs;
        private long _lastProposalAcceptUtcMs;
        private long _lastTeleportInUtcMs;
        private long _lastTeleportOutUtcMs;
        private int _instanceResetGeneration;
        private int _lastResetGeneration;
        private readonly HashSet<uint> _currentRunKilledBosses = new();
        internal const float HotspotArrivalDistance = 4.5f;
        internal const long ResetPendingOutsideTimeoutMs = 20000L;
        internal const long ResetDanceTimeoutMs = 45000L;
        internal const long ResetDanceStepMs = 1500L;
        internal const long ResetDanceInviteSettleMs = 1200L;
        internal const long ResetPendingOutsideLogThrottleMs = 5000L;
        // Trash is cleared one breadcrumb at a time: the leader never advances more than a
        // single step and refuses any step whose corridor still holds live hostile trash.
        internal const float DungeonAdvanceStepDistance = 18f;
        internal const float DungeonAdvanceTrashClearance = 12f;
        internal const float DungeonAdvanceClearanceHeight = 8f;
        // A boss may only be approached directly while it is already inside pull range;
        // farther bosses have to be reached through the cleared breadcrumb corridor.
        internal const float BossFallbackApproachDistance = 40f;
        private bool _runtimeInitialized;
        private bool _resetPendingOutside;
        private long _resetPendingOutsideSinceUtcMs;
        private long _lastResetPendingOutsideLogUtcMs;
        private ResetDancePhase _resetDancePhase;
        private bool _followerResetHandledForBroadcast;
        private readonly List<string> _resetRecoveryNames = new();
        private long _lastResetRecoveryInviteUtcMs;
        private long _resetRecoveryStartedUtcMs;
        private const long ResetRecoveryTimeoutMs = 60000L;
        private long _followerResetDanceRequestedUtcMs;
        private long _resetDanceStartedUtcMs;
        private long _resetDanceNextActionUtcMs;
        private int _resetDanceIndex;
        private readonly List<string> _resetDanceNames = new();
        private bool _sawGroupDisbandedForReset;
        private uint _walkInRunDungeonId;
        private uint _walkInRunMapId;
        private int _hotspotIndex;
        private uint _breadcrumbBossEntry;
        private int _breadcrumbIndex;
        private long _completedAtUtcMs;
        private bool _lfgRunCompletedAwaitingRequeue;
        private bool _countedCurrentWalkInEntry;
        private readonly HashSet<ulong> _runParticipants = new();
        private IReadOnlyList<SquadConnectedMemberSnapshot> _participantSnapshots = Array.Empty<SquadConnectedMemberSnapshot>();
        private Composite? _activeEncounterBehavior;
        private WoWUnit? _activeEncounterTarget;

        public int InstanceResetGeneration => _instanceResetGeneration;

        public void InitializeRuntime(SquadSettings settings)
        {
            if (SquadContext.ResolveBotMode(SquadContext.GetLeaderStateSnapshot(), settings) == SquadBotMode.Dungeon)
                InitializeDungeonRuntime();
        }

        private void InitializeDungeonRuntime()
        {
            if (_runtimeInitialized)
                return;

            if (!_scriptsLoaded)
            {
                DungeonManager.LoadDungeonScripts();
                _scriptsLoaded = true;
            }

            if (!_lfgEventsAttached)
            {
                LfgManager.AttachLfgEvents();
                _lfgEventsAttached = true;
            }

            WorldObstacleManager.Initialize();
            _runtimeInitialized = true;
            _lastSyncedMapId = 0;
            _lastSyncedDungeonId = 0;
            _lastLfgPollUtcMs = 0;
            _lastQueueAttemptUtcMs = 0;
            _lastRoleSetUtcMs = 0;
            _lastProposalAcceptUtcMs = 0;
            _lastTeleportOutUtcMs = 0;
            _hotspotIndex = 0;
            _breadcrumbBossEntry = 0;
            _breadcrumbIndex = 0;
            _lfgRunCompletedAwaitingRequeue = false;
            StopActiveEncounter();
        }

        public void ShutdownRuntime()
        {
            ResetDanceState();
            _followerResetHandledForBroadcast = false;
            StopActiveEncounter();
            if (_lfgEventsAttached)
            {
                try { LfgManager.DetachLfgEvents(); } catch { }
                _lfgEventsAttached = false;
            }
            try { global::Bots.DungeonBuddy.Helpers.ScriptHelpers.ReleaseScriptMovementBlocks(); } catch { }
            try { DungeonManager.Clear(); } catch { }
            try { BossManager.Reset(); } catch { }
            _currentRunKilledBosses.Clear();
            try { WorldObstacleManager.Shutdown(); } catch { }
            try { AvoidanceManager.Clear(); } catch { }
            _runtimeInitialized = false;
            _lastSyncedMapId = 0;
            _lastSyncedDungeonId = 0;
            _hotspotIndex = 0;
            _breadcrumbBossEntry = 0;
            _breadcrumbIndex = 0;
            _lfgRunCompletedAwaitingRequeue = false;
        }

        public void StopActiveEncounter()
        {
            var active = _activeEncounterBehavior;
            _activeEncounterBehavior = null;
            var target = _activeEncounterTarget;
            _activeEncounterTarget = null;
            if (active != null)
            {
                try { active.Stop(target); } catch { }
            }
        }

        public void PulseEnvironment(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            if (SquadContext.ResolveBotMode(leader, settings) != SquadBotMode.Dungeon)
            {
                if (_runtimeInitialized)
                    ShutdownRuntime();
                return;
            }

            InitializeDungeonRuntime();

            if (!settings.IsCoordinator)
            {
                if (leader?.DungeonResetPendingOutside != true)
                {
                    _followerResetHandledForBroadcast = false;
                    if (ResetDanceInProgress)
                        ResetDanceState();
                }
                else if (ResetDanceInProgress &&
                         (StyxWoW.Me is not { Dead: false, IsGhost: false } ||
                          nowUtcMs - _followerResetDanceRequestedUtcMs > ResetDanceTimeoutMs))
                {
                    ResetDanceState();
                    _followerResetHandledForBroadcast = true;
                }
            }

            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.UpdateMovementBlocks();
            AvoidanceManager.Update();
            DynamicBlackspotManager.Pulse();

            if (nowUtcMs - _lastLfgPollUtcMs >= 750L)
            {
                _lastLfgPollUtcMs = nowUtcMs;
                string mode = SafeGetLfgMode();
                if (mode == "proposal" && !LfgManager.ProposalPending)
                    LfgManager.ProposalPending = true;
                else if (mode == "rolecheck" && !LfgManager.RoleCheckPending)
                    LfgManager.RoleCheckPending = true;
            }

            SyncActiveDungeon(settings, leader);
            var deadBossCheck = BossManager.CreateCheckForDeadBossBehavior();
            deadBossCheck.Start(null);
            try { deadBossCheck.Tick(null); }
            finally { deadBossCheck.Stop(null); }
        }

        private void SyncActiveDungeon(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return;

            bool inDungeonMap = IsInDungeonMap(me);
            uint lfgId = LfgManager.CurrentLfgDungeonId;
            SquadDungeonEntryMode entryMode = leader?.DungeonEntryMode ?? settings.DungeonEntryMode;

            uint desiredDungeonId = 0;
            if (inDungeonMap)
            {
                int currentGen = settings.IsCoordinator ? _instanceResetGeneration : leader?.InstanceResetGeneration ?? _instanceResetGeneration;
                bool generationChanged = currentGen != _lastResetGeneration;
                if (generationChanged)
                {
                    StopActiveEncounter();
                    DungeonManager.Clear();
                    BossManager.Reset();
                    _currentRunKilledBosses.Clear();
                    _lastSyncedDungeonId = 0;
                    _lastResetGeneration = currentGen;
                }
                else
                {
                    foreach (var b in BossManager.Bosses.Where(b => b.IsDead))
                        _currentRunKilledBosses.Add(b.EntryId);
                }

                if (entryMode != SquadDungeonEntryMode.WalkInPortalFarm && lfgId > 0)
                {
                    desiredDungeonId = lfgId;
                }
                else if (entryMode == SquadDungeonEntryMode.WalkInPortalFarm)
                {
                    desiredDungeonId = ResolveWalkInTarget(settings, leader, SquadContext.GetEffectiveProfileLevel(), !me.IsHorde);
                }
                else if (!settings.IsCoordinator && leader?.ActiveDungeonMapId == me.MapId)
                {
                    desiredDungeonId = leader.ActiveDungeonId;
                }

                if (desiredDungeonId == 0)
                {
                    try { desiredDungeonId = DbProfileManager.GetLfgDungeonIdFromMapId(me.MapId); }
                    catch { desiredDungeonId = 0; }
                }

                if (desiredDungeonId > 0 && (generationChanged || _lastSyncedDungeonId != desiredDungeonId || _lastSyncedMapId != me.MapId || DungeonManager.CurrentDungeon == null))
                {
                    StopActiveEncounter();
                    _hotspotIndex = 0;
                    _breadcrumbBossEntry = 0;
                    _breadcrumbIndex = 0;
                    DungeonManager.SetDungeonById(desiredDungeonId);
                    BossManager.RestoreKilledBosses(_currentRunKilledBosses);
                    LfgManager.DungeonCompletedReason = CompleteReason.None;
                    _lfgRunCompletedAwaitingRequeue = false;
                    _lastSyncedDungeonId = desiredDungeonId;
                    _lastSyncedMapId = me.MapId;
                }
            }
            else
            {
                bool isCorpseRun = me != null && (me.Dead || me.IsGhost || leader?.Phase == SquadPhase.CorpseRun);
                if (!isCorpseRun && _lastSyncedDungeonId != 0)
                {
                    StopActiveEncounter();
                    DungeonManager.Clear();
                    BossManager.Reset();
                    _lastSyncedDungeonId = 0;
                    _hotspotIndex = 0;
                    _currentRunKilledBosses.Clear();
                }
                _lastSyncedMapId = me.MapId;
            }
        }

        private uint ResolveWalkInTarget(SquadSettings settings, SquadLeaderState? leader, int level, bool isAlliance)
        {
            if (!settings.IsCoordinator && leader != null)
                return leader.ActiveDungeonId;

            return _walkInRunDungeonId != 0 ? _walkInRunDungeonId : ResolveTargetDungeonId(settings, level, isAlliance);
        }

        public static bool IsInDungeonMap(LocalPlayer? me)
        {
            if (me == null)
                return false;
            try
            {
                return me.CurrentMap.IsDungeon || me.CurrentMap.IsRaid;
            }
            catch
            {
                return false;
            }
        }

        public static string SafeGetLfgMode()
        {
            try
            {
                return (Lua.GetReturnVal<string>("return GetLFGMode() or ''", 0) ?? string.Empty).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static bool HasDeserterOrRandomCooldown()
        {
            var me = StyxWoW.Me;
            if (!StyxWoW.IsInWorld || me is not { IsValid: true, IsAlive: true } || me.IsGhost)
                return true;
            try
            {
                return me.HasAura(71041) || me.HasAura(26013);
            }
            catch (OverflowException)
            {
                SquadPartySyncService.LogAction("Dungeon: LFG cooldown unreadable; deferring queue");
                return true;
            }
        }

        public static uint ResolveTargetDungeonId(
            SquadSettings settings,
            int effectiveSquadMinLevel,
            bool isAlliance,
            IReadOnlyList<DungeonBracketEntry>? playlistOverride = null)
        {
            uint[] explicitIds = settings.GetSelectedDungeonIds();
            if (explicitIds.Length > 0)
            {
                if (explicitIds.Length == 1 || effectiveSquadMinLevel <= 0)
                    return explicitIds[0];

                var playlist = playlistOverride ?? DefaultBracketPlaylist;
                foreach (uint id in explicitIds)
                {
                    var match = playlist.FirstOrDefault(e => e.DungeonId == id);
                    if (match.DungeonId == id && effectiveSquadMinLevel >= match.MinLevel && effectiveSquadMinLevel <= match.MaxLevel)
                        return id;
                }
                return explicitIds[0];
            }

            if (effectiveSquadMinLevel <= 0)
                return 0;

            var entries = playlistOverride ?? DefaultBracketPlaylist;
            DungeonBracketEntry? best = null;
            foreach (var entry in entries)
            {
                if (isAlliance && !entry.AllianceFriendly)
                    continue;
                if (!isAlliance && !entry.HordeFriendly)
                    continue;
                if (effectiveSquadMinLevel >= entry.MinLevel && effectiveSquadMinLevel <= entry.MaxLevel)
                {
                    if (best == null || entry.MinLevel > best.Value.MinLevel)
                        best = entry;
                }
            }

            if (best.HasValue)
                return best.Value.DungeonId;

            var fallback = entries
                .Where(e => (isAlliance && e.AllianceFriendly) || (!isAlliance && e.HordeFriendly))
                .Where(e => e.MinLevel <= effectiveSquadMinLevel)
                .OrderByDescending(e => e.MaxLevel)
                .FirstOrDefault();
            return fallback.DungeonId;
        }

        public static (bool HasRandomPlayers, ulong EffectiveTankGuid) EvaluatePartyComposition(
            ulong coordinatorGuid,
            SquadRole coordinatorRole,
            IReadOnlyCollection<ulong> squadBotGuids,
            IReadOnlyList<(ulong Guid, bool IsTankRoleAssigned, bool IsAlive)> inGamePartyMembers)
        {
            var configuredTanks = coordinatorRole == SquadRole.TankLeader
                ? new[] { coordinatorGuid }
                : Array.Empty<ulong>();
            return EvaluatePartyComposition(squadBotGuids, inGamePartyMembers, configuredTanks);
        }

        internal static (bool HasRandomPlayers, ulong EffectiveTankGuid) EvaluatePartyComposition(
            IReadOnlyCollection<ulong> squadBotGuids,
            IReadOnlyList<(ulong Guid, bool IsTankRoleAssigned, bool IsAlive)> inGamePartyMembers,
            IReadOnlyCollection<ulong> configuredTanks)
        {
            bool hasRandom = false;
            ulong assignedTankGuid = 0UL;
            ulong configuredTankGuid = 0UL;

            foreach (var pm in inGamePartyMembers)
            {
                if (pm.Guid == 0)
                    continue;
                if (!squadBotGuids.Contains(pm.Guid))
                    hasRandom = true;
                if (pm.IsTankRoleAssigned && pm.IsAlive && assignedTankGuid == 0UL)
                    assignedTankGuid = pm.Guid;
                if (pm.IsAlive && configuredTankGuid == 0UL && configuredTanks.Contains(pm.Guid))
                    configuredTankGuid = pm.Guid;
            }

            return (hasRandom, assignedTankGuid != 0UL ? assignedTankGuid : configuredTankGuid);
        }

        private void ClearWalkInRun()
        {
            _resetPendingOutside = false;
            _walkInRunDungeonId = 0;
            _walkInRunMapId = 0;
            _hotspotIndex = 0;
            _completedAtUtcMs = 0;
            _countedCurrentWalkInEntry = false;
            _lfgRunCompletedAwaitingRequeue = false;
            _currentRunKilledBosses.Clear();
            _runParticipants.Clear();
            _participantSnapshots = Array.Empty<SquadConnectedMemberSnapshot>();
        }

        internal static bool HasFreshExitEvidence(
            ulong participantGuid, IReadOnlyList<SquadConnectedMemberSnapshot> connected, long completedAtUtcMs, long nowUtcMs)
        {
            return connected.Any(m => HasFreshExitEvidence(m, completedAtUtcMs, nowUtcMs) &&
                                      m.LatestState!.Guid == participantGuid);
        }

        private static bool HasFreshExitEvidence(SquadConnectedMemberSnapshot member, long completedAtUtcMs, long nowUtcMs) =>
            member.LastStateReceivedUtcMs > completedAtUtcMs &&
            member.LastStateReceivedUtcMs <= nowUtcMs &&
            nowUtcMs - member.LastStateReceivedUtcMs <= 5000L &&
            member.LatestState is { HasDungeonTelemetry: true, IsInsideDungeon: false };

        internal static bool AreAllWalkInParticipantsClearedForReset(
            IReadOnlyCollection<ulong> runParticipants,
            IReadOnlyList<SquadConnectedMemberSnapshot> connected,
            IReadOnlyCollection<ulong> currentGroupMemberGuids,
            long completedAtUtcMs,
            long nowUtcMs)
        {
            return completedAtUtcMs > 0 && AreAllWalkInParticipantsClearedForReset(
                runParticipants, currentGroupMemberGuids,
                guid => HasFreshExitEvidence(guid, connected, completedAtUtcMs, nowUtcMs));
        }

        private static bool AreAllWalkInParticipantsClearedForReset(
            IReadOnlyCollection<ulong> runParticipants,
            IReadOnlyCollection<ulong> currentGroupMemberGuids,
            Func<ulong, bool> isConfirmedOutside) =>
            runParticipants.All(guid => !currentGroupMemberGuids.Contains(guid) || isConfirmedOutside(guid));

        public void RecordWalkInEntry(long nowUtcMs)
        {
            PruneResetHistory(nowUtcMs);
            _instanceResetTimestampsUtcMs.Add(nowUtcMs);
            _countedCurrentWalkInEntry = true;
        }

        public void RecordInstanceReset(long nowUtcMs)
        {
            PruneResetHistory(nowUtcMs);
            if (!_countedCurrentWalkInEntry)
            {
                _instanceResetTimestampsUtcMs.Add(nowUtcMs);
            }
            _instanceResetGeneration++;
            ClearWalkInRun();
        }

        public void PruneResetHistory(long nowUtcMs)
        {
            const long OneHourMs = 3600_000L;
            _instanceResetTimestampsUtcMs.RemoveAll(ts => nowUtcMs - ts >= OneHourMs);
        }

        public int GetHourlyResetCount(long nowUtcMs)
        {
            PruneResetHistory(nowUtcMs);
            return _instanceResetTimestampsUtcMs.Count;
        }

        public int GetLockoutRemainingSeconds(long nowUtcMs, int maxInstancesPerHour)
        {
            PruneResetHistory(nowUtcMs);
            int cap = Math.Max(1, maxInstancesPerHour);
            if (_instanceResetTimestampsUtcMs.Count < cap)
                return 0;

            long oldest = _instanceResetTimestampsUtcMs.Min();
            long unlockAt = oldest + 3600_000L;
            long remMs = Math.Max(0L, unlockAt - nowUtcMs);
            return (int)Math.Ceiling(remMs / 1000.0);
        }

        internal bool CanReenterCurrentWalkInRun => _countedCurrentWalkInEntry && !_resetPendingOutside;

        internal int GetWalkInEntryLockoutRemainingSeconds(long nowUtcMs, int maxInstancesPerHour) =>
            CanReenterCurrentWalkInRun ? 0 : GetLockoutRemainingSeconds(nowUtcMs, maxInstancesPerHour);

        public bool IsResetPendingOutside => _resetPendingOutside;

        public void MarkResetPendingOutside()
        {
            _resetPendingOutside = true;
            _resetPendingOutsideSinceUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastResetPendingOutsideLogUtcMs = 0;
            ResetDanceState();
        }

        internal static bool ShouldAbandonResetPendingOutside(
            bool resetIssued,
            long pendingSinceUtcMs,
            long nowUtcMs,
            long timeoutMs = ResetPendingOutsideTimeoutMs) =>
            !resetIssued &&
            pendingSinceUtcMs > 0 &&
            nowUtcMs - pendingSinceUtcMs >= timeoutMs;

        internal static long ResolveResetAbandonTimeoutMs(bool grouped) =>
            grouped ? ResetDanceTimeoutMs : ResetPendingOutsideTimeoutMs;

        internal static bool ShouldKickPartyMembers(bool isPartyLeader, int otherMembers) =>
            isPartyLeader && otherMembers > 0;

        private enum ResetDancePhase
        {
            None = 0,
            Kick = 1,
            Reset = 2,
            Invite = 3
        }

        private enum ResetDanceResult
        {
            Idle = 0,
            Working = 1,
            Completed = 2,
            Failed = 3
        }

        /// <summary>
        /// The reset dance runs on the coordinator, and on a follower that has become the instance
        /// leader (the coordinator left or handed over) while the broadcast reset flag is set.
        /// Non-leader followers keep their old path: they never start the dance themselves.
        /// </summary>
        /// <summary>
        /// Exit barrier shared by the coordinator's published evidence and the follower that would
        /// otherwise kick the group while a member is still inside (ResetInstances() then fails and
        /// the single request for this run is burnt).
        /// </summary>
        internal static bool AreMembersConfirmedOutside(
            ulong[]? confirmedGuids,
            long evidenceValidUntilUtcMs,
            long nowUtcMs,
            IEnumerable<ulong> groupGuids)
        {
            if (confirmedGuids == null || confirmedGuids.Length == 0)
                return false;
            if (evidenceValidUntilUtcMs != 0 && nowUtcMs > evidenceValidUntilUtcMs)
                return false;

            foreach (ulong guid in groupGuids)
            {
                if (guid == 0)
                    continue;
                if (Array.IndexOf(confirmedGuids, guid) < 0)
                    return false;
            }
            return true;
        }

        private bool AreLocalGroupMembersConfirmedOutside(SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;
            var group = me.GroupInfo;
            group.InvalidateCache();
            var guids = group.AllMembers.Where(m => m.Guid != 0).Select(m => m.Guid).ToList();
            if (guids.Count == 0)
                guids.Add(me.Guid);
            return AreMembersConfirmedOutside(
                leader?.DungeonResetExitConfirmedGuids,
                leader?.DungeonResetExitEvidenceValidUntilUtcMs ?? 0L,
                nowUtcMs,
                guids);
        }

        internal static bool ShouldEnterResetDance(
            bool resetPendingOutside,
            bool isCoordinator,
            bool leaderResetPendingOutside,
            bool localIsPartyLeader,
            bool followerRequestAlreadyHandled = false) =>
            resetPendingOutside ||
            (!isCoordinator && leaderResetPendingOutside && localIsPartyLeader && !followerRequestAlreadyHandled);

        /// <summary>
        /// True while the kick/reset/invite dance is running, so the periodic auto-invite pulse does
        /// not re-add a member the dance just removed (that would break the ungrouped reset).
        /// </summary>
        internal bool ResetDanceInProgress => _resetDancePhase != ResetDancePhase.None || _followerResetDanceRequestedUtcMs != 0;

        internal static ulong GetFollowerResetDanceOwner(IReadOnlyList<SquadConnectedMemberSnapshot> connected) =>
            connected.FirstOrDefault(m => m.LatestState is
                { DungeonResetDanceInProgress: true, IsInsideDungeon: false, IsDead: false, IsGhost: false, Guid: not 0 })
                ?.LatestState?.Guid ?? 0;

        internal static bool HasConfirmedFollowerGroupExit(
            SquadLeaderState leader, IReadOnlyCollection<ulong> groupGuids, ulong localGuid, long nowUtcMs) =>
            nowUtcMs <= leader.DungeonResetExitEvidenceValidUntilUtcMs &&
            AreAllWalkInParticipantsClearedForReset(groupGuids, groupGuids,
                guid => guid == localGuid || leader.DungeonResetExitConfirmedGuids.Contains(guid));

        internal static bool IsLocalPartyLeader() =>
            Lua.GetReturnVal<bool>("return (IsPartyLeader() and true) or false", 0);

        /// <summary>
        /// RDF keeps handing out the same instance ID after a completed run, and ResetInstances()
        /// is only accepted for a party leader with everyone outside. The leader therefore kicks
        /// the squad, resets while solo and invites everybody back before the next LFG queue.
        /// </summary>
        private ResetDanceResult RunInstanceResetDance(SquadSettings settings, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null)
            {
                ResetDanceState();
                return ResetDanceResult.Failed;
            }

            var group = me.GroupInfo;
            group.InvalidateCache();
            int others = group.NumPartyMembers > 0 ? group.NumPartyMembers : group.NumRaidMembers;
            // Leadership only matters while someone else is still grouped: a client that is
            // already alone can issue ResetInstances() itself.
            if (!IsLocalPartyLeader() && others > 0)
            {
                if (_resetDancePhase == ResetDancePhase.None)
                    return ResetDanceResult.Idle;
                ResetDanceState();
                return ResetDanceResult.Failed;
            }
            string[] expected = settings.GetExpectedMemberNames();
            int expectedOthers = Math.Max(0, expected.Length - 1);

            if (_resetDancePhase == ResetDancePhase.None)
            {
                _resetDancePhase = ShouldKickPartyMembers(true, others)
                    ? ResetDancePhase.Kick
                    : ResetDancePhase.Reset;

                _resetDanceStartedUtcMs = nowUtcMs;
                _resetDanceNextActionUtcMs = nowUtcMs;
                _resetDanceIndex = 0;
                _resetDanceNames.Clear();
            }

            if (nowUtcMs - _resetDanceStartedUtcMs > ResetDanceTimeoutMs)
            {
                Logging.Write("[PartyGrind Dungeon] Instance reset dance timed out; re-queueing without a reset.");
                RememberResetRecoveryNames(nowUtcMs);
                ResetDanceState();
                return ResetDanceResult.Failed;
            }

            if (nowUtcMs < _resetDanceNextActionUtcMs)
                return ResetDanceResult.Working;

            switch (_resetDancePhase)
            {
                case ResetDancePhase.Kick:
                {
                    var othersNow = group.AllMembers
                        .Where(m => m.Guid != 0 && m.Guid != me.Guid && !string.IsNullOrEmpty(m.Name))
                        .Select(m => m.Name)
                        .ToList();

                    if (othersNow.Count == 0)
                    {
                        _resetDancePhase = ResetDancePhase.Reset;
                        _resetDanceNextActionUtcMs = nowUtcMs + ResetDanceInviteSettleMs;
                        return ResetDanceResult.Working;
                    }

                    if (othersNow.Count > expectedOthers && expectedOthers > 0)
                    {
                        ResetDanceState();
                        Logging.Write("[PartyGrind Dungeon] Party holds unexpected members; skipping the kick step.");
                        return ResetDanceResult.Failed;
                    }

                    if (_resetDanceNames.Count == 0)
                        _resetDanceNames.AddRange(othersNow);

                    int index = Math.Min(_resetDanceIndex, othersNow.Count - 1);
                    string victim = othersNow[index];
                    Lua.DoString("UninviteUnit(\"" + Lua.Escape(victim) + "\")");
                    _resetDanceIndex++;
                    _resetDanceNextActionUtcMs = nowUtcMs + ResetDanceStepMs;
                    return ResetDanceResult.Working;
                }

                case ResetDancePhase.Reset:
                {
                    Lua.DoString("ResetInstances()");
                    SquadPartySyncService.LogAction("Dungeon: instances reset while the squad was ungrouped");
                    _resetDancePhase = ResetDancePhase.Invite;
                    _resetDanceIndex = 0;
                    _resetDanceNextActionUtcMs = nowUtcMs + ResetDanceInviteSettleMs;
                    return ResetDanceResult.Working;
                }

                case ResetDancePhase.Invite:
                {
                    var targets = _resetDanceNames.Count > 0
                        ? _resetDanceNames
                        : expected
                            .Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, me.Name, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                    if (targets.Count == 0)
                    {
                        Logging.Write("[PartyGrind Dungeon] No squadmate names known; skipping the re-invite step.");
                        ResetDanceState();
                        return ResetDanceResult.Failed;
                    }

                    var present = group.AllMembers
                        .Where(m => m.Guid != 0 && m.Guid != me.Guid)
                        .Select(m => m.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var missing = targets
                        .Where(n => !present.Contains(n))
                        .ToList();

                    if (missing.Count == 0)
                    {
                        RecordInstanceReset(nowUtcMs);
                        SquadPartySyncService.LogAction("Dungeon: party re-formed after the instance reset");
                        ResetDanceState();
                        return ResetDanceResult.Completed;
                    }

                    string invitee = missing[_resetDanceIndex % missing.Count];
                    Lua.DoString("InviteUnit(\"" + Lua.Escape(invitee) + "\")");
                    _resetDanceIndex++;
                    _resetDanceNextActionUtcMs = nowUtcMs + ResetDanceStepMs;
                    return ResetDanceResult.Working;
                }
            }

            return ResetDanceResult.Working;
        }

        /// <summary>
        /// The kick step is only safe when the squad is coming back: keep the kicked names so a
        /// failed or timed-out dance still restores the party instead of leaving it short.
        /// </summary>
        private void RememberResetRecoveryNames(long nowUtcMs)
        {
            if (_resetDanceNames.Count == 0)
                return;
            _resetRecoveryNames.Clear();
            _resetRecoveryNames.AddRange(_resetDanceNames);
            _resetRecoveryStartedUtcMs = nowUtcMs;
            _lastResetRecoveryInviteUtcMs = 0;
        }

        private void PulseResetRecovery(long nowUtcMs)
        {
            if (_resetRecoveryNames.Count == 0 || !IsLocalPartyLeader())
                return;

            var me = StyxWoW.Me;
            if (me == null || me.Dead || me.IsGhost || IsInDungeonMap(me))
                return;

            if (_resetRecoveryStartedUtcMs != 0 && nowUtcMs - _resetRecoveryStartedUtcMs > ResetRecoveryTimeoutMs)
            {
                _resetRecoveryNames.Clear();
                _resetRecoveryStartedUtcMs = 0;
                return;
            }

            var group = me.GroupInfo;
            group.InvalidateCache();
            var present = group.AllMembers
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .Select(m => m.Name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _resetRecoveryNames.RemoveAll(n => present.Contains(n));
            if (_resetRecoveryNames.Count == 0)
            {
                _resetRecoveryStartedUtcMs = 0;
                return;
            }

            if (nowUtcMs - _lastResetRecoveryInviteUtcMs < ResetDanceStepMs)
                return;

            _lastResetRecoveryInviteUtcMs = nowUtcMs;
            string missing = _resetRecoveryNames[0];
            Lua.DoString("InviteUnit(\"" + Lua.Escape(missing) + "\")");
            Logging.Write("[PartyGrind Dungeon] Restoring the party after the reset: inviting {0}.", missing);
        }

        private void ClearResetPendingOutside()
        {
            _resetPendingOutside = false;
            _resetPendingOutsideSinceUtcMs = 0;
            _lastResetPendingOutsideLogUtcMs = 0;
            ResetDanceState();
        }

        private void ResetDanceState()
        {
            _followerResetDanceRequestedUtcMs = 0;
            _resetDancePhase = ResetDancePhase.None;
            _resetDanceStartedUtcMs = 0;
            _resetDanceNextActionUtcMs = 0;
            _resetDanceIndex = 0;
            _resetDanceNames.Clear();
            _sawGroupDisbandedForReset = false;
        }

        public SquadDungeonSnapshot CaptureSnapshot(SquadSettings settings, IReadOnlyList<SquadConnectedMemberSnapshot> connected, int effectiveMinLevel, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            var botGuids = new HashSet<ulong>();
            var configuredTanks = new HashSet<ulong>();
            if (me != null && me.Guid != 0)
            {
                botGuids.Add(me.Guid);
                if (settings.Role == SquadRole.TankLeader)
                    configuredTanks.Add(me.Guid);
            }
            foreach (var c in connected)
            {
                if (c.IsConnected && c.Handshake.Guid != 0)
                {
                    botGuids.Add(c.Handshake.Guid);
                    if (c.Handshake.Role == SquadRole.TankLeader)
                        configuredTanks.Add(c.Handshake.Guid);
                }
                if (c.LatestState != null && c.LatestState.Guid != 0)
                    botGuids.Add(c.LatestState.Guid);
            }

            var partyList = new List<(ulong Guid, bool IsTankRoleAssigned, bool IsAlive)>();
            if (me != null)
            {
                bool meTankAssigned = (me.Role & WoWPartyMember.GroupRole.Tank) != 0;
                partyList.Add((me.Guid, meTankAssigned, me.IsAlive));
                foreach (var pm in me.GroupInfo.AllMembers)
                {
                    if (pm.Guid == 0 || pm.Guid == me.Guid)
                        continue;
                    bool isTank = pm.HasRole(WoWPartyMember.GroupRole.Tank);
                    var pObj = pm.ToPlayer();
                    bool alive = pObj is { IsValid: true } ? pObj.IsAlive : (pm.IsOnline && !pm.Dead && !pm.Ghost);
                    partyList.Add((pm.Guid, isTank, alive));
                }
            }

            var (hasRandom, effectiveTankGuid) = EvaluatePartyComposition(botGuids, partyList, configuredTanks);

            bool inDungeon = IsInDungeonMap(me);
            bool walkIn = settings.DungeonEntryMode == SquadDungeonEntryMode.WalkInPortalFarm;
            var currentDungeon = DungeonManager.CurrentDungeon;
            uint activeId = inDungeon ? currentDungeon?.DungeonId ?? _lastSyncedDungeonId : 0U;
            if (activeId == 0)
            {
                activeId = walkIn
                    ? ResolveWalkInTarget(settings, SquadContext.GetLeaderStateSnapshot(), effectiveMinLevel, me == null || !me.IsHorde)
                    : LfgManager.CurrentLfgDungeonId;
                if (!inDungeon && settings.IsCoordinator && settings.DungeonEntryMode == SquadDungeonEntryMode.LfgSpecificByBracket)
                    activeId = ResolveTargetDungeonId(settings, effectiveMinLevel, me == null || !me.IsHorde);
            }

            WoWPoint portal = inDungeon && currentDungeon != null
                ? currentDungeon.Entrance
                : (activeId > 0 ? DungeonManager.GetEntranceForDungeon(activeId) : WoWPoint.Zero);
            WoWPoint exit = currentDungeon?.ExitLocation ?? WoWPoint.Zero;

            var bosses = BossManager.Bosses;
            var currentBoss = BossManager.CurrentBoss;
            int totalBosses = bosses.Count(b => !b.IsOptional || settings.KillOptionalBosses);
            int killedBosses = bosses.Count(b => b.IsDead && (!b.IsOptional || settings.KillOptionalBosses));
            bool complete = inDungeon && (
                LfgManager.DungeonCompletedReason != CompleteReason.None ||
                (totalBosses > 0 && currentBoss == null));

            if (settings.IsCoordinator && me != null)
            {
                _participantSnapshots = connected;
                if (inDungeon)
                {
                    if (walkIn && !_countedCurrentWalkInEntry)
                    {
                        RecordWalkInEntry(nowUtcMs);
                    }
                    _walkInRunDungeonId = activeId;
                    _walkInRunMapId = me.MapId;
                    _runParticipants.UnionWith(partyList.Where(p => p.Guid != 0 && p.Guid != me.Guid).Select(p => p.Guid));
                    if (complete && _completedAtUtcMs == 0)
                    {
                        _completedAtUtcMs = nowUtcMs;
                        MarkResetPendingOutside();
                    }
                }
                if (_walkInRunDungeonId != 0)
                {
                    foreach (var participant in connected)
                    {
                        if (participant.LatestState is { HasDungeonTelemetry: true, IsInsideDungeon: true, Guid: not 0 } state &&
                            state.MapId == _walkInRunMapId && state.Guid != me.Guid &&
                            partyList.Any(member => member.Guid == state.Guid))
                            _runParticipants.Add(state.Guid);
                    }
                }
            }

            if (!walkIn && inDungeon && complete)
                _lfgRunCompletedAwaitingRequeue = true;

            uint instanceMapId = 0;
            if (activeId > 0)
            {
                try
                {
                    var dbc = LfgDungeons.GetById(activeId);
                    if (dbc != null)
                        instanceMapId = (uint)Math.Max(0, dbc.MapId);
                }
                catch { }
            }

            var confirmedOutside = new List<ulong>();
            long exitEvidenceValidUntilUtcMs = 0;
            if (!walkIn && settings.IsCoordinator && _resetPendingOutside && _completedAtUtcMs > 0)
            {
                exitEvidenceValidUntilUtcMs = nowUtcMs + 5000L;
                if (!inDungeon && me != null)
                    confirmedOutside.Add(me.Guid);
                foreach (var participant in connected)
                {
                    long received = participant.LastStateReceivedUtcMs;
                    if (HasFreshExitEvidence(participant, _completedAtUtcMs, nowUtcMs) &&
                        participant.LatestState is { Guid: not 0 } state)
                    {
                        confirmedOutside.Add(state.Guid);
                        exitEvidenceValidUntilUtcMs = Math.Min(exitEvidenceValidUntilUtcMs, received + 5000L);
                    }
                }
            }

            return new SquadDungeonSnapshot
            {
                IsInsideDungeon = inDungeon,
                ActiveDungeonId = activeId,
                ActiveDungeonMapId = inDungeon && me != null ? me.MapId : instanceMapId,
                ActiveDungeonName = currentDungeon?.Name ?? string.Empty,
                CurrentBossEntry = currentBoss?.Entry ?? 0U,
                CurrentBossName = currentBoss?.Name ?? string.Empty,
                KilledBossCount = killedBosses,
                TotalBossCount = totalBosses,
                IsDungeonComplete = complete,
                ResetPendingOutside = _resetPendingOutside,
                ResetExitConfirmedGuids = confirmedOutside.ToArray(),
                ResetExitEvidenceValidUntilUtcMs = exitEvidenceValidUntilUtcMs,
                LfgProposalPending = LfgManager.ProposalPending || SafeGetLfgMode() == "proposal",
                LfgMode = SafeGetLfgMode(),
                HasRandomPartyMembers = hasRandom,
                EffectiveTankGuid = effectiveTankGuid,
                PortalX = portal.X,
                PortalY = portal.Y,
                PortalZ = portal.Z,
                PortalMapId = me != null && !inDungeon ? me.MapId : 0U,
                ExitX = exit.X,
                ExitY = exit.Y,
                ExitZ = exit.Z
            };
        }

        public static void ApplySquadLfgRole(SquadRole role, bool isCoordinator)
        {
            bool tank = role == SquadRole.TankLeader;
            bool healer = role == SquadRole.Healer;
            bool dps = !tank && !healer;
            string leaderFlag = isCoordinator ? "true" : "false";
            Lua.DoString($"SetLFGRoles({leaderFlag},{(tank ? "true" : "false")},{(healer ? "true" : "false")},{(dps ? "true" : "false")})");
        }

        internal static bool ShouldTeleportIntoLfgPartyOutsideDungeon(
            string lfgMode,
            bool isInDungeonMap,
            bool lfgRunCompletedAwaitingRequeue,
            CompleteReason completedReason,
            bool leaderDungeonComplete)
        {
            if (isInDungeonMap || !string.Equals(lfgMode, "lfgparty", StringComparison.OrdinalIgnoreCase))
                return false;

            if (lfgRunCompletedAwaitingRequeue || completedReason != CompleteReason.None || leaderDungeonComplete)
                return false;

            return true;
        }

        public bool RunQueueingOrEntryTick(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null || me.Dead || me.IsGhost)
                return false;

            // A reset that failed or timed out must still leave the squad grouped.
            PulseResetRecovery(nowUtcMs);

            if (LfgManager.RoleCheckPending || SafeGetLfgMode() == "rolecheck")
            {
                _lfgRunCompletedAwaitingRequeue = false;
                ApplySquadLfgRole(settings.Role, settings.IsCoordinator);
                Lua.DoString("if LFDRoleCheckPopupAcceptButton and LFDRoleCheckPopupAcceptButton:IsVisible() then LFDRoleCheckPopupAcceptButton:Click() end; if StaticPopup1 and StaticPopup1:IsVisible() and StaticPopup1.which == 'LFG_ROLE_CHECK_CONFIRM' and StaticPopup1Button1 then StaticPopup1Button1:Click() end");
                LfgManager.RoleCheckPending = false;
                SquadPartySyncService.LogAction("Dungeon: confirmed LFG role check");
                return true;
            }

            bool proposalActive = LfgManager.ProposalPending || SafeGetLfgMode() == "proposal";
            bool shouldAccept = proposalActive && (settings.IsCoordinator || leader?.AcceptLfgProposalOrder == true || leader == null);
            if (shouldAccept && nowUtcMs - _lastProposalAcceptUtcMs >= 1500L)
            {
                _lastProposalAcceptUtcMs = nowUtcMs;
                _lfgRunCompletedAwaitingRequeue = false;
                LfgManager.AcceptProposal();
                LfgManager.ProposalPending = false;
                SquadPartySyncService.LogAction("Dungeon: accepting LFG proposal");
                return true;
            }

            if (LfgManager.BootProposalActive)
            {
                LfgManager.BootProposalActive = false;
                return true;
            }

            SquadDungeonEntryMode entryMode = leader?.DungeonEntryMode ?? settings.DungeonEntryMode;
            if (entryMode == SquadDungeonEntryMode.WalkInPortalFarm)
            {
                return RunWalkInPortalEntryTick(settings, leader, nowUtcMs);
            }

            bool leaderResetRequested = leader?.DungeonResetPendingOutside == true;
            bool resetGateOpen = ShouldEnterResetDance(
                _resetPendingOutside,
                settings.IsCoordinator,
                leaderResetRequested,
                ResetDanceInProgress || IsLocalPartyLeader(),
                _followerResetHandledForBroadcast);
            if (resetGateOpen)
            {
                if (IsInDungeonMap(me))
                    return false;

                if (settings.IsCoordinator && leader is { DungeonResetDanceOwnerGuid: not 0 } &&
                    leader.DungeonResetDanceOwnerGuid != me.Guid)
                {
                    if (me.GroupInfo.NumPartyMembers == 0 && me.GroupInfo.NumRaidMembers == 0)
                        _sawGroupDisbandedForReset = true;
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    return true;
                }

                var group = me.GroupInfo;
                group.InvalidateCache();
                if (_resetDancePhase == ResetDancePhase.None)
                {
                    var groupGuids = group.AllMembers.Where(m => m.Guid != 0).Select(m => m.Guid).ToHashSet();
                    int raidCount = group.NumRaidMembers;
                    int expectedGroupCount = raidCount > 0 ? raidCount : group.NumPartyMembers;
                    bool allOutside = groupGuids.Count == expectedGroupCount &&
                        (settings.IsCoordinator
                            ? AreAllWalkInParticipantsClearedForReset(
                                _runParticipants, _participantSnapshots, groupGuids, _completedAtUtcMs, nowUtcMs)
                            : leader != null && HasConfirmedFollowerGroupExit(leader, groupGuids, me.Guid, nowUtcMs));
                    if (!allOutside)
                    {
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        SquadPartySyncService.LogAction("Dungeon: waiting for exit confirmation from all run participants");
                        return true;
                    }
                }

                if (settings.IsCoordinator)
                {
                    if (_resetPendingOutsideSinceUtcMs == 0)
                        _resetPendingOutsideSinceUtcMs = nowUtcMs;

                    if (nowUtcMs - _lastResetPendingOutsideLogUtcMs >= ResetPendingOutsideLogThrottleMs)
                    {
                        _lastResetPendingOutsideLogUtcMs = nowUtcMs;
                        Logging.Write("[PartyGrind Dungeon] Resetting instances for next run.");
                    }

                    switch (RunInstanceResetDance(settings, nowUtcMs))
                    {
                        case ResetDanceResult.Working:
                            if (me.IsMoving)
                                WoWMovement.MoveStop();
                            return true;

                        case ResetDanceResult.Completed:
                            ClearResetPendingOutside();
                            _lfgRunCompletedAwaitingRequeue = false;
                            LfgManager.DungeonCompletedReason = CompleteReason.None;
                            return false;

                        case ResetDanceResult.Failed:
                            ClearResetPendingOutside();
                            _lfgRunCompletedAwaitingRequeue = false;
                            LfgManager.DungeonCompletedReason = CompleteReason.None;
                            return false;
                    }

                    bool groupDisbanded = group.NumPartyMembers == 0 && group.NumRaidMembers == 0;
                    if (groupDisbanded)
                        _sawGroupDisbandedForReset = true;
                    else if (_sawGroupDisbandedForReset)
                    {
                        string[] expectedNames = settings.GetExpectedMemberNames();
                        var presentNames = group.AllMembers
                            .Where(m => m.Guid != 0)
                            .Select(m => m.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool partyReformed = expectedNames.Length == 0 ||
                            expectedNames.All(n => string.Equals(n, me.Name, StringComparison.OrdinalIgnoreCase) || presentNames.Contains(n));
                        if (partyReformed)
                        {
                            RecordInstanceReset(nowUtcMs);
                            Logging.Write("[PartyGrind Dungeon] Party re-formed after the reset; re-queueing for a fresh instance.");
                            ClearResetPendingOutside();
                            _lfgRunCompletedAwaitingRequeue = false;
                            LfgManager.DungeonCompletedReason = CompleteReason.None;
                            return false;
                        }
                    }

                    long abandonTimeoutMs = ResolveResetAbandonTimeoutMs(me.IsInParty);
                    if (ShouldAbandonResetPendingOutside(false, _resetPendingOutsideSinceUtcMs, nowUtcMs, abandonTimeoutMs))
                    {
                        Logging.Write("[PartyGrind Dungeon] Instance reset could not be issued within {0}s ({1}); re-queueing without a reset.",
                            abandonTimeoutMs / 1000, me.IsInParty ? "not the party leader" : "no group");
                        ClearResetPendingOutside();
                        _lfgRunCompletedAwaitingRequeue = false;
                        LfgManager.DungeonCompletedReason = CompleteReason.None;
                        return false;
                    }

                    return true;
                }

                if (leaderResetRequested && !_followerResetHandledForBroadcast)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();

                    if (_resetDancePhase == ResetDancePhase.None && !IsLocalPartyLeader())
                    {
                        ResetDanceState();
                        return true;
                    }

                    if (_followerResetDanceRequestedUtcMs == 0)
                        _followerResetDanceRequestedUtcMs = nowUtcMs;
                    if (leader?.DungeonResetDanceOwnerGuid != me.Guid)
                        return true;

                    // Same barrier the coordinator applies before it issues the reset itself.
                    if (_resetDancePhase == ResetDancePhase.None && !AreLocalGroupMembersConfirmedOutside(leader, nowUtcMs))
                    {
                        if (nowUtcMs - _lastResetPendingOutsideLogUtcMs >= ResetPendingOutsideLogThrottleMs)
                        {
                            _lastResetPendingOutsideLogUtcMs = nowUtcMs;
                            SquadPartySyncService.LogAction("Dungeon: waiting for exit confirmation before the reset");
                        }
                        return true;
                    }

                    switch (RunInstanceResetDance(settings, nowUtcMs))
                    {
                        case ResetDanceResult.Working:
                            return true;

                        case ResetDanceResult.Completed:
                            // The coordinator keeps broadcasting until it sees the outcome; without
                            // this record the completed dance would start over for the same request.
                            _followerResetHandledForBroadcast = true;
                            return true;

                        case ResetDanceResult.Failed:
                            _followerResetHandledForBroadcast = true;
                            return false;
                    }

                    if (nowUtcMs - _lastResetPendingOutsideLogUtcMs >= ResetPendingOutsideLogThrottleMs)
                    {
                        _lastResetPendingOutsideLogUtcMs = nowUtcMs;
                        SquadPartySyncService.LogAction("Dungeon: waiting for the instance reset");
                    }
                    return true;
                }
            }

            if (nowUtcMs - _lastRoleSetUtcMs >= 20000L)
            {
                _lastRoleSetUtcMs = nowUtcMs;
                ApplySquadLfgRole(settings.Role, settings.IsCoordinator);
            }

            string lfgMode = SafeGetLfgMode();
            if (!string.IsNullOrEmpty(lfgMode) && lfgMode != "lfgparty" && lfgMode != "abandonedindungeon")
            {
                _lfgRunCompletedAwaitingRequeue = false;
                LfgManager.DungeonCompletedReason = CompleteReason.None;
            }

            bool leaderCompletedRun = leader != null &&
                (leader.IsDungeonComplete || leader.TeleportOutOrder || leader.Phase == SquadPhase.DungeonCompleteExit);
            if (ShouldTeleportIntoLfgPartyOutsideDungeon(
                    lfgMode,
                    IsInDungeonMap(me),
                    _lfgRunCompletedAwaitingRequeue,
                    LfgManager.DungeonCompletedReason,
                    leaderCompletedRun))
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                if (me.Mounted)
                {
                    Mount.Dismount("LFG teleport in");
                    return true;
                }
                if (nowUtcMs - _lastTeleportInUtcMs >= 3500L)
                {
                    _lastTeleportInUtcMs = nowUtcMs;
                    LfgManager.TeleportIn();
                    SquadPartySyncService.LogAction("Dungeon: teleporting into LFG instance");
                }
                return true;
            }

            if (!settings.IsCoordinator)
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                SquadPartySyncService.LogAction("Dungeon: waiting for coordinator LFG queue");
                return true;
            }

            if (lfgMode == "queued" || lfgMode == "proposal" || lfgMode == "rolecheck")
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                SquadPartySyncService.LogAction("Dungeon: in LFG queue (" + lfgMode + ")");
                return true;
            }

            if (HasDeserterOrRandomCooldown() ||
                (leader?.Members != null && leader.Members.Any(m => m.IsConnected && (m.IsDead || m.IsGhost))))
            {
                return true;
            }

            string[] expected = settings.GetExpectedMemberNames();
            if (!settings.AllowPartialSquadLfg)
            {
                var partyNames = new HashSet<string>(
                    me.GroupInfo.PartyMembers.Where(m => m.Guid != 0).Select(m => m.Name),
                    StringComparer.OrdinalIgnoreCase) { me.Name };
                if (!expected.All(partyNames.Contains))
                {
                    SquadPartySyncService.LogAction("Dungeon: waiting for full party before LFG queue");
                    return true;
                }
            }
            else if (expected.Length > 1 && leader?.Members != null)
            {
                int connectedBots = leader.Members.Count(m => m.IsConnected);
                int inPartyBots = 1 + me.PartyMemberInfos.Count(pm =>
                    leader.Members.Any(m => m.IsConnected && m.Guid == pm.Guid));
                if (inPartyBots < connectedBots)
                {
                    SquadPartySyncService.LogAction("Dungeon: waiting for connected squadmates to join party");
                    return true;
                }
            }

            if (nowUtcMs - _lastQueueAttemptUtcMs < 4000L)
                return true;

            _lastQueueAttemptUtcMs = nowUtcMs;
            ApplySquadLfgRole(settings.Role, isCoordinator: true);

            switch (entryMode)
            {
                case SquadDungeonEntryMode.LfgRandomHeroic:
                    LfgManager.QueueForRandomHeroic(setRoles: false);
                    SquadPartySyncService.LogAction("Dungeon: queued Random Heroic");
                    break;

                case SquadDungeonEntryMode.LfgSpecificByBracket:
                {
                    int minLvl = leader?.EffectiveSquadMinLevel > 0 ? leader.EffectiveSquadMinLevel : me.Level;
                    uint targetId = leader?.ActiveDungeonId ?? ResolveTargetDungeonId(settings, minLvl, !me.IsHorde);
                    if (targetId > 0)
                    {
                        LfgManager.QueueForSpecificDungeon(targetId, setRoles: false);
                        SquadPartySyncService.LogAction($"Dungeon: queued specific ID {targetId}");
                    }
                    else
                    {
                        LfgManager.QueueForRandomDungeon(setRoles: false);
                        SquadPartySyncService.LogAction("Dungeon: queued Random Normal (fallback)");
                    }
                    break;
                }

                case SquadDungeonEntryMode.LfgRandomNormal:
                default:
                    QueueRandomDungeonForSquadLevel(leader?.EffectiveSquadMinLevel > 0 ? leader.EffectiveSquadMinLevel : me.Level);
                    SquadPartySyncService.LogAction("Dungeon: queued Random Normal");
                    break;
            }

            LfgManager.DungeonCompletedReason = CompleteReason.None;
            return true;
        }

        private static void QueueRandomDungeonForSquadLevel(int squadMinLevel)
        {
            LfgDungeons? chosen = null;
            foreach (var kv in LfgDungeons.All)
            {
                var d = kv.Value;
                if (!d.IsValid || !d.IsRandomDungeon || d.Difficulty != 0U)
                    continue;
                if (squadMinLevel >= d.MinLevel && squadMinLevel <= d.MaxLevel)
                {
                    chosen = d;
                    break;
                }
            }

            if (chosen != null)
            {
                Lua.DoString($"ClearAllLFGDungeons(); SetLFGDungeon({chosen.Id}); JoinLFG();");
            }
            else
            {
                LfgManager.QueueForRandomDungeon(setRoles: false);
            }
        }

        private bool RunWalkInPortalEntryTick(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me!;
            if (IsInDungeonMap(me))
                return false;

            if (settings.IsCoordinator && _resetPendingOutside)
            {
                var group = me.GroupInfo;
                group.InvalidateCache();
                var groupGuids = group.AllMembers
                    .Where(m => m.Guid != 0)
                    .Select(m => m.Guid)
                    .ToHashSet();
                int raidCount = group.NumRaidMembers;
                int expectedGroupCount = raidCount > 0 ? raidCount : group.NumPartyMembers;
                bool allOutside = groupGuids.Count == expectedGroupCount &&
                    AreAllWalkInParticipantsClearedForReset(
                        _runParticipants,
                        _participantSnapshots,
                        groupGuids,
                        _completedAtUtcMs,
                        nowUtcMs);
                if (!allOutside)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    SquadPartySyncService.LogAction("Dungeon: waiting for exit confirmation from all run participants");
                    return true;
                }

                Logging.Write("[PartyGrind Dungeon] Resetting instances for next walk-in run.");
                bool resetIssued = Lua.GetReturnVal<bool>(
                    "if (GetNumPartyMembers() == 0 and GetNumRaidMembers() == 0) or IsPartyLeader() then ResetInstances(); return true end return false", 0);
                if (resetIssued)
                {
                    RecordInstanceReset(nowUtcMs);
                }
                return true;
            }

            if (!settings.IsCoordinator && leader?.DungeonResetPendingOutside == true)
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                return true;
            }

            int entryLockoutSeconds = settings.IsCoordinator
                ? GetWalkInEntryLockoutRemainingSeconds(nowUtcMs, settings.MaxInstancesPerHour)
                : (leader?.LockoutRemainingSeconds ?? 0);
            if (entryLockoutSeconds > 0)
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                SquadPartySyncService.LogAction($"Dungeon: hourly instance cap reached ({entryLockoutSeconds}s remaining)");
                return true;
            }

            uint targetId = ResolveWalkInTarget(settings, leader, SquadContext.GetEffectiveProfileLevel(), !me.IsHorde);
            WoWPoint portal = leader != null && leader.ActiveDungeonId == targetId &&
                (leader.DungeonPortalX != 0f || leader.DungeonPortalY != 0f)
                ? new WoWPoint(leader.DungeonPortalX, leader.DungeonPortalY, leader.DungeonPortalZ)
                : (targetId > 0 ? DungeonManager.GetEntranceForDungeon(targetId) : WoWPoint.Zero);

            if (portal == WoWPoint.Zero)
                return false;

            float dist = me.Location.Distance(portal);
            if (dist > 14f)
            {
                SquadPartySyncService.LogAction("Dungeon: moving to entrance portal");
                Navigator.MoveTo(portal, "Moving to dungeon portal");
                return true;
            }

            if (me.Mounted)
            {
                Mount.Dismount("Entering dungeon portal");
                return true;
            }

            if (!settings.IsCoordinator && leader != null)
            {
                var local = SquadContext.GetLocalMemberStateSnapshot();
                if (local != null && local.ObservedResetGeneration < leader.InstanceResetGeneration)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    return true;
                }

                if (!leader.IsInsideDungeon)
                {
                    var leaderPt = new WoWPoint(leader.X, leader.Y, leader.Z);
                    if (leader.MapId != me.MapId || leaderPt.Distance(portal) > 16f)
                    {
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        SquadPartySyncService.LogAction("Dungeon: waiting at portal for coordinator");
                        return true;
                    }

                    bool squadGatheredForFollower = leader.Members == null || leader.Members
                        .Where(m => m.IsConnected && !m.IsDead && !m.IsGhost)
                        .All(m => m.MapId == leader.MapId && new WoWPoint(m.X, m.Y, m.Z).Distance(portal) <= 18f);
                    if (!squadGatheredForFollower)
                    {
                        if (me.IsMoving)
                            WoWMovement.MoveStop();
                        SquadPartySyncService.LogAction("Dungeon: waiting at portal for squadmates");
                        return true;
                    }
                }
            }

            if (settings.IsCoordinator && leader?.Members != null)
            {
                bool squadGathered = leader.Members
                    .Where(m => m.IsConnected && !m.IsDead && !m.IsGhost)
                    .All(m => m.IsInsideDungeon || (m.MapId == me.MapId && new WoWPoint(m.X, m.Y, m.Z).Distance(portal) <= 18f));
                if (!squadGathered)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    SquadPartySyncService.LogAction("Dungeon: waiting at portal for squadmates");
                    return true;
                }
            }

            SquadPartySyncService.LogAction("Dungeon: stepping through portal");
            WoWMovement.ClickToMove(portal);
            return true;
        }

        public bool RunCompleteExitTick(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            if (!IsInDungeonMap(me))
                return false;

            SquadDungeonEntryMode entryMode = leader?.DungeonEntryMode ?? settings.DungeonEntryMode;
            if (entryMode != SquadDungeonEntryMode.WalkInPortalFarm)
            {
                _lfgRunCompletedAwaitingRequeue = true;
                if (nowUtcMs - _lastTeleportOutUtcMs >= 3500L)
                {
                    _lastTeleportOutUtcMs = nowUtcMs;
                    bool leaveParty = leader?.HasRandomPartyMembers == true && settings.ExpectedMemberNamesCsv.Trim().Length == 0;
                    Lua.DoString(leaveParty ? "LeaveParty(); LFGTeleport(true);" : "LFGTeleport(true);");
                    LfgManager.DungeonCompletedReason = CompleteReason.None;
                    SquadPartySyncService.LogAction("Dungeon: teleporting out of completed LFG instance");
                }
                return true;
            }

            WoWPoint exit = DungeonManager.CurrentDungeon?.ExitLocation ??
                (leader != null && (leader.DungeonExitX != 0f || leader.DungeonExitY != 0f)
                    ? new WoWPoint(leader.DungeonExitX, leader.DungeonExitY, leader.DungeonExitZ)
                    : WoWPoint.Zero);

            if (exit != WoWPoint.Zero)
            {
                SquadPartySyncService.LogAction("Dungeon: walking to exit portal");
                if (me.Location.Distance(exit) > 5f)
                    Navigator.MoveTo(exit, "Exiting completed dungeon");
                else
                    WoWMovement.ClickToMove(exit);
                return true;
            }

            return false;
        }

        public bool TryRunAvoidanceStep()
        {
            var me = StyxWoW.Me;
            if (me == null || !IsInDungeonMap(me))
                return false;

            if (!AvoidanceManager.IsInAvoidance(me.Location))
                return false;

            WoWPoint safe = AvoidanceManager.GetSafePoint(me.Location);
            if (safe == WoWPoint.Zero)
                return false;

            SquadPartySyncService.LogAction("Dungeon: moving out of ground hazard");
            Navigator.MoveTo(safe, "Avoiding dungeon hazard");
            return true;
        }

        public bool TryRunDungeonScriptBehaviors()
        {
            var me = StyxWoW.Me;
            if (me == null || DungeonManager.CurrentDungeon == null || !IsInDungeonMap(me))
            {
                StopActiveEncounter();
                return false;
            }

            var scriptBehavior = DungeonManager.CurrentDungeonBehavior;
            if (scriptBehavior != null)
            {
                if (!scriptBehavior.IsRunning)
                    scriptBehavior.Start(null);
                RunStatus scriptStatus = scriptBehavior.Tick(null);
                if (scriptStatus == RunStatus.Running || scriptStatus == RunStatus.Success)
                {
                    StopActiveEncounter();
                    if (scriptStatus != RunStatus.Running)
                        scriptBehavior.Stop(null);
                    return true;
                }
            }

            WoWUnit? target = me.CurrentTarget;
            if (me.Combat && target != null && target.IsValid && target.IsAlive &&
                BossManager.Bosses.Any(b => !b.IsDead && b.EntryId == target.Entry))
            {
                if (target.Guid != _activeEncounterTarget?.Guid)
                {
                    StopActiveEncounter();
                    _activeEncounterTarget = target;
                    _activeEncounterBehavior = DungeonManager.GetEncounterBehavior((int)target.Entry);
                    _activeEncounterBehavior?.Start(target);
                }

                if (_activeEncounterBehavior != null)
                {
                    RunStatus encStatus = _activeEncounterBehavior.Tick(target);
                    if (encStatus != RunStatus.Running)
                        StopActiveEncounter();
                    if (encStatus == RunStatus.Running || encStatus == RunStatus.Success)
                        return true;
                }
            }

            StopActiveEncounter();
            return false;
        }

        internal static WoWPoint AdvanceBossBreadcrumb(
            WoWPoint meLocation,
            IReadOnlyList<WoWPoint> bossPath,
            WoWPoint bossProfileLocation,
            WoWPoint? liveBossLocation,
            float? liveBossPathDistance,
            ref int breadcrumbIndex)
        {
            if (liveBossLocation.HasValue && liveBossLocation.Value != WoWPoint.Zero &&
                liveBossPathDistance.HasValue && liveBossPathDistance.Value <= 25f)
            {
                return liveBossLocation.Value;
            }

            if (bossPath != null && bossPath.Count > 0)
            {
                while (breadcrumbIndex < bossPath.Count)
                {
                    WoWPoint crumb = bossPath[breadcrumbIndex];
                    if (crumb == WoWPoint.Zero || meLocation.Distance(crumb) <= HotspotArrivalDistance)
                    {
                        breadcrumbIndex++;
                        continue;
                    }
                    return crumb;
                }
            }

            if (liveBossLocation.HasValue && liveBossLocation.Value != WoWPoint.Zero &&
                liveBossPathDistance.HasValue)
            {
                return liveBossLocation.Value;
            }

            if (bossProfileLocation != WoWPoint.Zero && meLocation.Distance(bossProfileLocation) > HotspotArrivalDistance)
                return bossProfileLocation;

            return WoWPoint.Zero;
        }

        /// <summary>
        /// Perpendicular (2D) distance from <paramref name="point"/> to the corridor segment
        /// <paramref name="from"/>-<paramref name="to"/>. Used to decide whether the next step
        /// of the leader corridor still runs through live trash.
        /// </summary>
        internal static float DistanceToCorridorSegment(WoWPoint point, WoWPoint from, WoWPoint to) =>
            DistanceToCorridorSegment(point, from, to, out _);

        internal static float DistanceToCorridorSegment(WoWPoint point, WoWPoint from, WoWPoint to, out float alongTrack)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0001f)
            {
                alongTrack = 0f;
                float offsetX = point.X - from.X;
                float offsetY = point.Y - from.Y;
                return MathF.Sqrt(offsetX * offsetX + offsetY * offsetY);
            }

            alongTrack = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / MathF.Sqrt(lengthSquared);
            float projection = Math.Clamp(alongTrack / MathF.Sqrt(lengthSquared), 0f, 1f);
            float closestX = from.X + dx * projection;
            float closestY = from.Y + dy * projection;
            float deltaX = point.X - closestX;
            float deltaY = point.Y - closestY;
            return MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        /// <summary>
        /// Resolves the next leader step along the dungeon corridor. The step is always clamped to
        /// <paramref name="maxStep"/> so the tank never charges a distant boss through several packs,
        /// and it is refused (Zero) while live hostile trash still sits inside
        /// <paramref name="clearance"/> of the corridor, so the pull logic clears that pack first.
        /// </summary>
        internal static WoWPoint ResolveDungeonAdvanceStep(
            WoWPoint from,
            WoWPoint destination,
            IReadOnlyList<WoWPoint>? hostileLocations,
            float maxStep = DungeonAdvanceStepDistance,
            float clearance = DungeonAdvanceTrashClearance)
        {
            if (from == WoWPoint.Zero || destination == WoWPoint.Zero)
                return WoWPoint.Zero;

            float distance = from.Distance(destination);
            if (distance <= 0.01f)
                return WoWPoint.Zero;

            WoWPoint step = destination;
            if (maxStep > 0f && distance > maxStep)
            {
                float ratio = maxStep / distance;
                step = new WoWPoint(
                    from.X + (destination.X - from.X) * ratio,
                    from.Y + (destination.Y - from.Y) * ratio,
                    from.Z + (destination.Z - from.Z) * ratio);
            }

            if (clearance > 0f && hostileLocations != null)
            {
                foreach (WoWPoint hostile in hostileLocations)
                {
                    if (hostile == WoWPoint.Zero)
                        continue;
                    if (MathF.Abs(hostile.Z - from.Z) > DungeonAdvanceClearanceHeight)
                        continue;
                    // Only trash the leader would walk into counts: a mob trailing behind the group
                    // must not freeze the advance for the whole corridor.
                    if (DistanceToCorridorSegment(hostile, from, step, out float alongTrack) <= clearance &&
                        alongTrack > 0f)
                        return WoWPoint.Zero;
                }
            }

            return step;
        }

        public WoWPoint ComputeLeaderNextDungeonPoint()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return WoWPoint.Zero;

            if (BotPoi.Current.Type == PoiType.Hotspot && BotPoi.Current.Location != WoWPoint.Zero)
            {
                WoWPoint hs = BotPoi.Current.Location;
                if (me.Location.Distance(hs) <= HotspotArrivalDistance)
                    BotPoi.Clear("Reached dungeon hotspot");
                else
                    return hs;
            }

            var profile = DbProfileManager.CurrentProfile;
            if (profile != null && profile.BossEncounters.Count > 0)
            {
                var currentBoss = BossManager.CurrentBoss;
                var profileBoss = currentBoss == null
                    ? null
                    : profile.BossEncounters.FirstOrDefault(b => b.Entry == currentBoss.Entry);

                if (profileBoss != null)
                {
                    if (_breadcrumbBossEntry != profileBoss.Entry)
                    {
                        _breadcrumbBossEntry = profileBoss.Entry;
                        _breadcrumbIndex = 0;
                        if (profileBoss.Path.Count > 1)
                        {
                            int closestIdx = 0;
                            float closestDist = float.MaxValue;
                            for (int i = 0; i < profileBoss.Path.Count; i++)
                            {
                                float d = me.Location.Distance(profileBoss.Path[i]);
                                if (d < closestDist)
                                {
                                    closestDist = d;
                                    closestIdx = i;
                                }
                            }
                            if (closestDist <= 30f && (closestIdx == 0 || Navigator.CanNavigateFully(me.Location, profileBoss.Path[closestIdx])))
                                _breadcrumbIndex = closestIdx;
                        }
                    }

                    WoWUnit? liveCurrentBoss = ObjectManager.GetObjectsOfType<WoWUnit>()
                        .FirstOrDefault(u => u != null && u.IsValid && u.IsAlive && u.Entry == profileBoss.Entry);
                    float? livePathDist = liveCurrentBoss != null
                        ? Navigator.PathDistance(me.Location, liveCurrentBoss.Location, 100f)
                        : null;

                    WoWPoint bossTarget = AdvanceBossBreadcrumb(
                        me.Location,
                        profileBoss.Path,
                        profileBoss.Location,
                        liveCurrentBoss?.Location,
                        livePathDist,
                        ref _breadcrumbIndex);
                    if (bossTarget != WoWPoint.Zero)
                        return bossTarget;

                    if (liveCurrentBoss == null && me.Location.Distance(profileBoss.Location) <= HotspotArrivalDistance)
                    {
                        BossManager.MarkBossDead(profileBoss.Entry);
                        _currentRunKilledBosses.Add(profileBoss.Entry);
                    }
                }

                var fallbackBoss = ObjectManager.GetObjectsOfType<WoWUnit>()
                    .Where(u => u != null && u.IsValid && u.IsAlive &&
                                profile.BossEncounters.Any(b => b.Entry == u.Entry &&
                                    (!b.Optional || BossManager.KillOptionalBossesEnabled) &&
                                    (currentBoss == null || b.Entry == currentBoss.Entry || b.KillOrder <=
                                     (profile.BossEncounters.FirstOrDefault(pb => pb.Entry == currentBoss.Entry)?.KillOrder ?? int.MaxValue))))
                    .Where(u => Navigator.PathDistance(me.Location, u.Location, BossFallbackApproachDistance) is float pd &&
                                pd < BossFallbackApproachDistance)
                    .OrderBy(u => profile.BossEncounters.FirstOrDefault(b => b.Entry == u.Entry)?.KillOrder ?? int.MaxValue)
                    .ThenBy(u => u.DistanceSqr)
                    .FirstOrDefault();

                if (fallbackBoss != null)
                    return fallbackBoss.Location;

                while (_hotspotIndex < profile.HotSpots.Count)
                {
                    WoWPoint hotspot = profile.HotSpots[_hotspotIndex];
                    if (hotspot != WoWPoint.Zero && me.Location.Distance(hotspot) > HotspotArrivalDistance)
                        return hotspot;
                    _hotspotIndex++;
                }
            }

            return WoWPoint.Zero;
        }

        internal static int GetBossReadinessManaPercent(SquadSettings settings) =>
            Math.Min(100, Math.Max(45, settings.TriggerRestManaPct + 15));

        public static bool IsBossPrePullBarrierBlocking(
            SquadSettings settings,
            SquadLeaderState? leader,
            float distanceToBoss)
        {
            if (leader == null || distanceToBoss > 40f || leader.InCombat)
                return false;

            if (leader.Members == null || leader.Members.Count == 0)
                return false;

            var leaderPos = new WoWPoint(leader.X, leader.Y, leader.Z);
            foreach (var m in leader.Members)
            {
                if (!m.IsConnected)
                    continue;
                if (m.IsDead || m.IsGhost || m.NeedsRest || m.MapId != leader.MapId)
                    return true;
                if (leaderPos.Distance(new WoWPoint(m.X, m.Y, m.Z)) > Math.Min(25f, settings.MaxPullGatherDistance))
                    return true;
                if ((m.Role == SquadRole.Healer || m.Role == SquadRole.HybridOffHeal) &&
                    m.PowerType == WoWPowerType.Mana &&
                    m.PowerPercent < GetBossReadinessManaPercent(settings))
                {
                    return true;
                }
            }

            return false;
        }

        public WoWPoint GetGhostDungeonEntranceTarget()
        {
            var current = DungeonManager.CurrentDungeon;
            if (current != null)
            {
                var portal = ObjectManager.GetObjectsOfType<WoWGameObject>()
                    .FirstOrDefault(o => o != null && o.IsValid && (o.Entry == 192507U || o.Entry == 207896U));
                if (portal != null)
                    return portal.Location;
                if (current.Entrance != WoWPoint.Zero)
                    return current.Entrance;
            }

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (leader != null && (leader.DungeonPortalX != 0f || leader.DungeonPortalY != 0f))
                return new WoWPoint(leader.DungeonPortalX, leader.DungeonPortalY, leader.DungeonPortalZ);

            return WoWPoint.Zero;
        }
    }
}
