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
        public uint? ActiveDungeonMapId { get; set; }
        public string ActiveDungeonName { get; set; } = string.Empty;
        public uint CurrentBossEntry { get; set; }
        public string CurrentBossName { get; set; } = string.Empty;
        public int KilledBossCount { get; set; }
        public int TotalBossCount { get; set; }
        public bool IsDungeonComplete { get; set; }
        public bool ResetPendingOutside { get; set; }
        public bool IsInPartyOrRaid { get; set; }
        public bool AllExpectedMembersInParty { get; set; } = true;
        public bool LfgProposalPending { get; set; }
        public string LfgMode { get; set; } = string.Empty;
        public bool HasRandomPartyMembers { get; set; }
        public ulong EffectiveTankGuid { get; set; }
        public float PortalX { get; set; }
        public float PortalY { get; set; }
        public float PortalZ { get; set; }
        public uint? PortalMapId { get; set; }
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
        private long _lastInstanceResetAttemptUtcMs;
        private int _instanceResetGeneration;
        private int _lastResetGeneration;
        private int _lastObservedResetGenOnFollower;
        private readonly HashSet<uint> _currentRunKilledBosses = new();
        internal const float HotspotArrivalDistance = 4.5f;
        internal const long ResetPendingOutsideTimeoutMs = 20000L;
        // Trash is cleared one breadcrumb at a time: the leader never advances more than a
        // single step and refuses any step whose corridor still holds live hostile trash.
        internal const float DungeonAdvanceStepDistance = 18f;
        internal const float DungeonAdvanceTrashClearance = 12f;
        internal const float DungeonAdvanceClearanceHeight = 8f;
        internal const long DungeonStepBlacklistCooldownMs = 30000L;
        internal const int DungeonStepMaxConsecutiveFailures = 3;
        internal const long DungeonStepStallTimeoutMs = 4500L;
        internal const float DungeonStepMinProgressYards = 1.5f;
        // A boss may only be approached directly while it is already inside pull range;
        // farther bosses have to be reached through the cleared breadcrumb corridor.
        internal const float BossFallbackApproachDistance = 40f;
        private bool _runtimeInitialized;
        private bool _resetPendingOutside;
        private long _resetPendingOutsideSinceUtcMs;
        private uint _walkInRunDungeonId;
        private uint _walkInRunMapId;
        private int _hotspotIndex;
        private readonly HashSet<int> _visitedFallbackHotspots = new();
        private uint _breadcrumbBossEntry;
        private int _breadcrumbIndex;
        private readonly Dictionary<(int X, int Y, int Z, uint MapId), long> _blacklistedNavPoints = new();
        private bool _lfgRunCompletedAwaitingRequeue;
        private bool _countedCurrentWalkInEntry;
        internal const long ObjectFirstCompletionCooldownMs = 5000L;
        internal const long ObjectRepeatCompletionCooldownMs = 60000L;
        internal const long ObjectMaxCompletionCooldownMs = 300000L;
        internal const long ObjectRunningTimeoutMs = 12000L;
        internal const long ObjectTimeoutCooldownMs = 30000L;

        private sealed class DungeonObjectAttemptState
        {
            public int CompletionCount { get; set; }
            public long SuppressedUntilMs { get; set; }
        }

        private readonly Dictionary<ulong, DungeonObjectAttemptState> _objectAttemptStates = new();
        private readonly Dictionary<Composite, long> _suppressedObjectHandlersUntilMs = new();
        private Composite? _activeObjectBehavior;
        private Composite? _activeObjectHandler;
        private ulong _activeObjectGuid;
        private long _activeObjectRunningSinceMs;
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
            _lastInstanceResetAttemptUtcMs = 0;
            _lastObservedResetGenOnFollower = 0;
            _hotspotIndex = 0;
            _visitedFallbackHotspots.Clear();
            _breadcrumbBossEntry = 0;
            _breadcrumbIndex = 0;
            _blacklistedNavPoints.Clear();
            _lfgRunCompletedAwaitingRequeue = false;
            ClearResetPendingOutside();
            ClearObjectScriptTracking();
            DungeonManager.IsObjectHandlerSuppressed = IsDungeonObjectSuppressed;
            DungeonManager.IsObjectHandlerCompositeSuppressed = IsDungeonObjectCompositeSuppressed;
            StopActiveEncounter();
        }

        public void ShutdownRuntime()
        {
            StopActiveEncounter();
            StopActiveObjectBehavior();
            DungeonManager.IsObjectHandlerSuppressed = null;
            DungeonManager.IsObjectHandlerCompositeSuppressed = null;
            ClearObjectScriptTracking();
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
            _visitedFallbackHotspots.Clear();
            _breadcrumbBossEntry = 0;
            _breadcrumbIndex = 0;
            _blacklistedNavPoints.Clear();
            _lfgRunCompletedAwaitingRequeue = false;
            _lastInstanceResetAttemptUtcMs = 0;
            _lastObservedResetGenOnFollower = 0;
            ClearWalkInRun();
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

        public void StopActiveEncounterScriptBehavior()
        {
            DungeonManager.StopEncounterHandlersBehaviorIfRunning();
            StopActiveEncounter();
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

            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.UpdateMovementBlocks();
            AvoidanceManager.Update();
            DynamicBlackspotManager.Pulse();

            if (nowUtcMs - _lastLfgPollUtcMs >= 750L)
            {
                _lastLfgPollUtcMs = nowUtcMs;
                string mode = SafeGetLfgMode();
                if (mode == "proposal" && !LfgManager.ProposalPending)
                    LfgManager.MarkProposalPending();
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
                    StopActiveObjectBehavior();
                    DungeonManager.Clear();
                    BossManager.Reset();
                    _currentRunKilledBosses.Clear();
                    ClearObjectScriptTracking();
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
                    StopActiveObjectBehavior();
                    _hotspotIndex = 0;
                    _visitedFallbackHotspots.Clear();
                    _breadcrumbBossEntry = 0;
                    _breadcrumbIndex = 0;
                    _blacklistedNavPoints.Clear();
                    ClearObjectScriptTracking();
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
                    StopActiveObjectBehavior();
                    DungeonManager.Clear();
                    BossManager.Reset();
                    _lastSyncedDungeonId = 0;
                    _hotspotIndex = 0;
                    _visitedFallbackHotspots.Clear();
                    _currentRunKilledBosses.Clear();
                    ClearObjectScriptTracking();
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

        internal static string BuildIsInPartyOrRaidScript() =>
            "return (GetNumPartyMembers() > 0 or GetNumRaidMembers() > 0) and 1 or 0";

        internal static string BuildEmptyGroupResetScript() =>
            "if GetNumPartyMembers() == 0 and GetNumRaidMembers() == 0 then ResetInstances(); return 1 end return 0";

        private static bool IsInPartyOrRaidLua() =>
            Lua.GetReturnVal<int>(BuildIsInPartyOrRaidScript(), 0) == 1;

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
            ClearResetPendingOutside();
            _walkInRunDungeonId = 0;
            _walkInRunMapId = 0;
            _hotspotIndex = 0;
            _countedCurrentWalkInEntry = false;
            _lfgRunCompletedAwaitingRequeue = false;
            _currentRunKilledBosses.Clear();
        }

        internal static bool HasFreshExitEvidence(
            ulong participantGuid, IReadOnlyList<SquadConnectedMemberSnapshot> connected, long completedAtUtcMs, long nowUtcMs)
        {
            return connected.Any(m => m.IsConnected &&
                                      m.LastStateReceivedUtcMs > completedAtUtcMs &&
                                      m.LastStateReceivedUtcMs <= nowUtcMs &&
                                      nowUtcMs - m.LastStateReceivedUtcMs <= 5000L &&
                                      m.LatestState is { HasDungeonTelemetry: true, HasValidMap: true, IsInsideDungeon: false } state &&
                                      state.Guid == participantGuid);
        }

        internal static bool AreAllRunParticipantsOutside(
            IReadOnlyCollection<ulong> participantGuids,
            ulong coordinatorGuid,
            IReadOnlyList<SquadConnectedMemberSnapshot> connected,
            long completedAtUtcMs,
            long nowUtcMs)
        {
            if (completedAtUtcMs <= 0)
                return false;

            foreach (ulong guid in participantGuids)
            {
                if (guid == 0 || guid == coordinatorGuid)
                    continue;
                if (!HasFreshExitEvidence(guid, connected, completedAtUtcMs, nowUtcMs))
                    return false;
            }

            foreach (var snap in connected)
            {
                if (!snap.IsConnected)
                    continue;
                ulong guid = snap.LatestState?.Guid ?? snap.Handshake.Guid;
                if (guid == 0 || guid == coordinatorGuid)
                    continue;
                if (!HasFreshExitEvidence(guid, connected, completedAtUtcMs, nowUtcMs))
                    return false;
            }

            return true;
        }

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
        }

        internal static bool ShouldAbandonResetPendingOutside(bool resetIssued, long pendingSinceUtcMs, long nowUtcMs) =>
            !resetIssued &&
            pendingSinceUtcMs > 0 &&
            nowUtcMs - pendingSinceUtcMs >= ResetPendingOutsideTimeoutMs;

        private void ClearResetPendingOutside()
        {
            _resetPendingOutside = false;
            _resetPendingOutsideSinceUtcMs = 0;
        }

        internal static (int KilledBosses, int TotalBosses, bool FinalBossKilled, bool IsComplete) EvaluateBossProgress(
            IReadOnlyList<BossManager.Boss> bosses,
            BossManager.Boss? currentBoss,
            bool killOptionalBosses,
            bool? isHorde,
            bool inDungeon,
            CompleteReason lfgCompletedReason)
        {
            int totalBosses = bosses.Count(b =>
                BossManager.IsBossAvailableToFaction(b, isHorde) &&
                (!b.IsOptional || killOptionalBosses));
            int killedBosses = bosses.Count(b =>
                b.IsDead &&
                BossManager.IsBossAvailableToFaction(b, isHorde) &&
                (!b.IsOptional || killOptionalBosses));
            bool finalBossKilled = bosses.Any(b =>
                b.IsDead && BossManager.IsBossFinalForFaction(b, isHorde));
            bool complete = inDungeon && (
                lfgCompletedReason != CompleteReason.None ||
                finalBossKilled ||
                (totalBosses > 0 && currentBoss == null));
            return (killedBosses, totalBosses, finalBossKilled, complete);
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

            bool? isHorde = me != null && StyxWoW.IsInGame ? (bool?)me.IsHorde : null;
            bool killOptionalBosses = BossManager.KillOptionalBossesEnabled;
            var bosses = BossManager.Bosses;
            var currentBoss = BossManager.SelectCurrentBoss(killOptionalBosses, isHorde);
            var (killedBosses, totalBosses, _, complete) = EvaluateBossProgress(
                bosses,
                currentBoss,
                killOptionalBosses,
                isHorde,
                inDungeon,
                LfgManager.DungeonCompletedReason);

            bool isInPartyOrRaid = partyList.Count > 1;
            bool attendanceConfirmed = true;
            var partyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (me != null && !string.IsNullOrWhiteSpace(me.Name))
                partyNames.Add(me.Name.Trim());
            if (StyxWoW.IsInGame && me != null)
            {
                bool luaRosterReadSucceeded = false;
                try
                {
                    isInPartyOrRaid = IsInPartyOrRaidLua();
                    string luaRoster = Lua.GetReturnVal<string>(
                        "local s='' " +
                        "if GetNumRaidMembers() > 0 then " +
                            "for i=1,GetNumRaidMembers() do local n,_,_,_,_,_,_,online=GetRaidRosterInfo(i) if n and n~='' and online then s=s..n..';' end end " +
                        "elseif GetNumPartyMembers() > 0 then " +
                            "for i=1,GetNumPartyMembers() do local n=UnitName('party'..i) if n and n~='' and UnitIsConnected('party'..i) then s=s..n..';' end end " +
                        "end return s", 0) ?? string.Empty;
                    foreach (string entry in luaRoster.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        partyNames.Add(entry.Trim());
                    luaRosterReadSucceeded = true;
                }
                catch { }

                if (!luaRosterReadSucceeded)
                {
                    int validMembers = 0;
                    foreach (var pm in me.GroupInfo.AllMembers)
                    {
                        if (pm.Guid != 0 && pm.IsOnline && !string.IsNullOrWhiteSpace(pm.Name))
                        {
                            partyNames.Add(pm.Name.Trim());
                            validMembers++;
                        }
                    }
                    if (isInPartyOrRaid && validMembers == 0)
                        attendanceConfirmed = false;
                }
            }
            else if (me != null)
            {
                foreach (var pm in me.GroupInfo.AllMembers)
                {
                    if (pm.Guid != 0 && !string.IsNullOrWhiteSpace(pm.Name))
                        partyNames.Add(pm.Name.Trim());
                }
            }

            var requiredNames = new HashSet<string>(settings.GetExpectedMemberNames(), StringComparer.OrdinalIgnoreCase);
            if (requiredNames.Count == 0)
            {
                foreach (var c in connected.Where(c => c.IsConnected && !string.IsNullOrWhiteSpace(c.CharacterName)))
                    requiredNames.Add(c.CharacterName.Trim());
            }
            if (me != null && !string.IsNullOrWhiteSpace(me.Name))
                requiredNames.Add(me.Name.Trim());
            bool allExpectedMembersInParty = attendanceConfirmed && requiredNames.All(partyNames.Contains);

            if (settings.IsCoordinator && me != null)
            {
                if (inDungeon)
                {
                    if (walkIn && !_countedCurrentWalkInEntry)
                    {
                        RecordWalkInEntry(nowUtcMs);
                    }
                    _walkInRunDungeonId = activeId;
                    _walkInRunMapId = me.MapId;
                    if (complete && !_resetPendingOutside)
                        MarkResetPendingOutside();
                }
            }

            if (!walkIn && inDungeon && complete)
                _lfgRunCompletedAwaitingRequeue = true;

            uint? instanceMapId = null;
            if (activeId > 0)
            {
                try
                {
                    var dbc = LfgDungeons.GetById(activeId);
                    if (dbc != null && dbc.MapId >= 0)
                        instanceMapId = (uint)dbc.MapId;
                }
                catch { }
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
                IsInPartyOrRaid = isInPartyOrRaid,
                AllExpectedMembersInParty = allExpectedMembersInParty,
                LfgProposalPending = LfgManager.ProposalPending || SafeGetLfgMode() == "proposal",
                LfgMode = SafeGetLfgMode(),
                HasRandomPartyMembers = hasRandom,
                EffectiveTankGuid = effectiveTankGuid,
                PortalX = portal.X,
                PortalY = portal.Y,
                PortalZ = portal.Z,
                PortalMapId = me != null && !inDungeon && StyxWoW.IsInWorld ? me.MapId : null,
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

        private bool HandleDungeonResetStateTick(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            if (!settings.IsCoordinator && leader != null)
            {
                if (leader.InstanceResetGeneration != _lastObservedResetGenOnFollower)
                {
                    _lastObservedResetGenOnFollower = leader.InstanceResetGeneration;
                    _lfgRunCompletedAwaitingRequeue = false;
                    LfgManager.DungeonCompletedReason = CompleteReason.None;
                    LfgManager.ResetProposalFlags();
                }
            }

            SquadDungeonResetPhase resetPhase = leader?.DungeonResetPhase ?? SquadDungeonResetPhase.Idle;
            if (resetPhase == SquadDungeonResetPhase.Idle)
                return false;

            if (me.IsMoving)
                WoWMovement.MoveStop();

            switch (resetPhase)
            {
                case SquadDungeonResetPhase.Completed:
                case SquadDungeonResetPhase.Exiting:
                    SquadPartySyncService.LogAction("Dungeon: waiting for reset exit confirmation");
                    return true;

                case SquadDungeonResetPhase.Disbanding:
                {
                    bool squadPausedCombatOrDead = leader != null &&
                        (leader.Phase == SquadPhase.Paused || leader.InCombat || leader.IsDead || leader.IsGhost ||
                         leader.Members.Any(m => m.IsConnected && (m.IsDead || m.IsGhost)));
                    if (!squadPausedCombatOrDead)
                    {
                        if (settings.IsCoordinator)
                        {
                            long disbandingMs = leader != null ? nowUtcMs - leader.DungeonResetStateSinceUtcMs : 0L;
                            if (leader != null &&
                                ((!me.GroupInfo.IsLeader && disbandingMs >= 4000L) || disbandingMs >= 6000L) &&
                                IsInPartyOrRaidLua())
                            {
                                Lua.DoString("LeaveParty();");
                            }
                        }
                        else if (!IsInDungeonMap(me) &&
                                 leader != null &&
                                 nowUtcMs - leader.DungeonResetStateSinceUtcMs >= 2500L &&
                                 IsInPartyOrRaidLua())
                        {
                            Lua.DoString("LeaveParty();");
                        }
                    }
                    SquadPartySyncService.LogAction("Dungeon: disbanding party before reset");
                    return true;
                }

                case SquadDungeonResetPhase.Resetting:
                    if (settings.IsCoordinator && !IsInDungeonMap(me) && nowUtcMs - _lastInstanceResetAttemptUtcMs >= 500L)
                    {
                        _lastInstanceResetAttemptUtcMs = nowUtcMs;
                        bool emptyAndReset = Lua.GetReturnVal<int>(BuildEmptyGroupResetScript(), 0) == 1;
                        if (emptyAndReset)
                        {
                            RecordInstanceReset(nowUtcMs);
                            _lfgRunCompletedAwaitingRequeue = false;
                            LfgManager.DungeonCompletedReason = CompleteReason.None;
                            LfgManager.ResetProposalFlags();
                            BossManager.Reset();
                            DungeonManager.Clear();
                            ClearResetPendingOutside();
                            Logging.Write("[PartyGrind Dungeon] Group empty; called ResetInstances() (generation={0}).", _instanceResetGeneration);
                        }
                    }
                    else if (!settings.IsCoordinator)
                    {
                        _lfgRunCompletedAwaitingRequeue = false;
                        LfgManager.DungeonCompletedReason = CompleteReason.None;
                    }
                    SquadPartySyncService.LogAction("Dungeon: resetting instance");
                    return true;

                case SquadDungeonResetPhase.Reinviting:
                    _lfgRunCompletedAwaitingRequeue = false;
                    LfgManager.DungeonCompletedReason = CompleteReason.None;
                    SquadPartySyncService.LogAction("Dungeon: waiting for all members to rejoin party");
                    return true;

                default:
                    return false;
            }
        }

        public bool RunQueueingOrEntryTick(SquadSettings settings, SquadLeaderState? leader, long nowUtcMs)
        {
            var me = StyxWoW.Me;
            if (me == null || me.Dead || me.IsGhost)
                return false;

            if (HandleDungeonResetStateTick(settings, leader, nowUtcMs))
                return true;

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

            LfgManager.ResetProposalFlags();
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
            {
                if (leader?.Phase == SquadPhase.DungeonCompleteExit ||
                    leader?.DungeonResetPhase == SquadDungeonResetPhase.Exiting)
                {
                    if (me.IsMoving)
                        WoWMovement.MoveStop();
                    SquadPartySyncService.LogAction("Dungeon: exited instance, waiting for squadmates");
                    return true;
                }
                return false;
            }

            SquadDungeonEntryMode entryMode = leader?.DungeonEntryMode ?? settings.DungeonEntryMode;
            if (entryMode != SquadDungeonEntryMode.WalkInPortalFarm)
            {
                _lfgRunCompletedAwaitingRequeue = true;
                if (nowUtcMs - _lastTeleportOutUtcMs >= 3500L)
                {
                    _lastTeleportOutUtcMs = nowUtcMs;
                    Lua.DoString("LFGTeleport(true);");
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

        internal static long ComputeObjectCompletionCooldownMs(int completionCount)
        {
            if (completionCount <= 1)
                return ObjectFirstCompletionCooldownMs;
            if (completionCount == 2)
                return ObjectRepeatCompletionCooldownMs;
            return ObjectMaxCompletionCooldownMs;
        }

        internal bool IsObjectSuppressedAt(ulong guid, long nowUtcMs)
        {
            return guid != 0UL &&
                   _objectAttemptStates.TryGetValue(guid, out var state) &&
                   nowUtcMs < state.SuppressedUntilMs;
        }

        internal void RecordObjectScriptSuccess(ulong guid, long nowUtcMs)
        {
            if (guid == 0UL)
                return;

            if (!_objectAttemptStates.TryGetValue(guid, out var state))
            {
                state = new DungeonObjectAttemptState();
                _objectAttemptStates[guid] = state;
            }

            state.CompletionCount++;
            state.SuppressedUntilMs = nowUtcMs + ComputeObjectCompletionCooldownMs(state.CompletionCount);
        }

        internal void RecordObjectScriptTimeout(ulong guid, long nowUtcMs)
        {
            if (guid == 0UL)
                return;

            if (!_objectAttemptStates.TryGetValue(guid, out var state))
            {
                state = new DungeonObjectAttemptState();
                _objectAttemptStates[guid] = state;
            }

            state.CompletionCount++;
            state.SuppressedUntilMs = nowUtcMs + Math.Max(ObjectTimeoutCooldownMs, ComputeObjectCompletionCooldownMs(state.CompletionCount));
        }

        internal void ClearObjectScriptTracking()
        {
            _objectAttemptStates.Clear();
            _suppressedObjectHandlersUntilMs.Clear();
            _activeObjectHandler = null;
            _activeObjectGuid = 0UL;
            _activeObjectRunningSinceMs = 0L;
        }

        private bool IsDungeonObjectSuppressed(WoWGameObject obj)
        {
            return obj != null && IsObjectSuppressedAt(obj.Guid, Environment.TickCount64);
        }

        private bool IsDungeonObjectCompositeSuppressed(Composite composite)
        {
            return composite != null &&
                   _suppressedObjectHandlersUntilMs.TryGetValue(composite, out long untilMs) &&
                   Environment.TickCount64 < untilMs;
        }

        public void StopActiveObjectBehavior()
        {
            var objectBehavior = _activeObjectBehavior;
            _activeObjectBehavior = null;
            _activeObjectHandler = null;
            if (objectBehavior != null && objectBehavior.IsRunning)
            {
                try { objectBehavior.Stop(null); } catch { }
            }
            DungeonManager.ResetLastActiveObject();
            _activeObjectGuid = 0UL;
            _activeObjectRunningSinceMs = 0L;
        }

        public bool TryRunDungeonEncounterBehaviors()
        {
            var me = StyxWoW.Me;
            if (me == null || DungeonManager.CurrentDungeon == null || !IsInDungeonMap(me))
            {
                StopActiveEncounter();
                return false;
            }

            var encounterScript = DungeonManager.CurrentEncounterHandlersBehavior;
            if (encounterScript != null)
            {
                if (!encounterScript.IsRunning)
                    encounterScript.Start(null);
                RunStatus scriptStatus = encounterScript.Tick(null);
                if (scriptStatus != RunStatus.Running && encounterScript.IsRunning)
                    encounterScript.Stop(null);
                if (scriptStatus == RunStatus.Running || scriptStatus == RunStatus.Success)
                {
                    StopActiveEncounter();
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

        public bool TryRunDungeonObjectBehaviors()
        {
            var me = StyxWoW.Me;
            if (me == null || DungeonManager.CurrentDungeon == null || !IsInDungeonMap(me))
            {
                StopActiveObjectBehavior();
                return false;
            }

            DungeonManager.IsObjectHandlerSuppressed = IsDungeonObjectSuppressed;
            DungeonManager.IsObjectHandlerCompositeSuppressed = IsDungeonObjectCompositeSuppressed;
            var objectScript = DungeonManager.CurrentObjectHandlersBehavior;
            if (objectScript == null)
                return false;

            if (!ReferenceEquals(_activeObjectBehavior, objectScript))
            {
                StopActiveObjectBehavior();
                _activeObjectBehavior = objectScript;
            }

            long now = Environment.TickCount64;
            DungeonManager.ResetLastActiveObject();
            if (!objectScript.IsRunning)
                objectScript.Start(null);
            RunStatus scriptStatus = objectScript.Tick(null);
            if (scriptStatus != RunStatus.Running)
            {
                if (objectScript.IsRunning)
                    objectScript.Stop(null);
                _activeObjectBehavior = null;
            }

            ulong handledGuid = DungeonManager.LastActiveObjectGuid;
            Composite? handledComposite = DungeonManager.LastActiveObjectComposite;
            if (scriptStatus == RunStatus.Running)
            {
                if (!ReferenceEquals(_activeObjectHandler, handledComposite))
                {
                    _activeObjectHandler = handledComposite;
                    _activeObjectRunningSinceMs = now;
                    _activeObjectGuid = handledGuid;
                }
                else
                {
                    if (_activeObjectRunningSinceMs == 0L)
                        _activeObjectRunningSinceMs = now;

                    if (handledGuid != 0UL)
                    {
                        if (_activeObjectGuid != 0UL && _activeObjectGuid != handledGuid)
                            _activeObjectRunningSinceMs = now;
                        _activeObjectGuid = handledGuid;
                    }
                }

                if (now - _activeObjectRunningSinceMs >= ObjectRunningTimeoutMs)
                {
                    ulong timedOutGuid = handledGuid != 0UL ? handledGuid : _activeObjectGuid;
                    if (timedOutGuid != 0UL)
                        RecordObjectScriptTimeout(timedOutGuid, now);
                    else if (handledComposite != null)
                        _suppressedObjectHandlersUntilMs[handledComposite] = now + ObjectTimeoutCooldownMs;
                    SquadPartySyncService.LogAction("Dungeon: skipping unresponsive object " + (DungeonManager.LastActiveObjectName ?? "object"));
                    StopActiveObjectBehavior();
                    return false;
                }

                return true;
            }

            if (scriptStatus == RunStatus.Success)
            {
                ulong completedGuid = handledGuid != 0UL
                    ? handledGuid
                    : (ReferenceEquals(_activeObjectHandler, handledComposite) ? _activeObjectGuid : 0UL);
                if (completedGuid != 0UL)
                    RecordObjectScriptSuccess(completedGuid, now);
                else if (handledComposite != null)
                    _suppressedObjectHandlersUntilMs[handledComposite] = now + ObjectFirstCompletionCooldownMs;
                _activeObjectHandler = null;
                _activeObjectGuid = 0UL;
                _activeObjectRunningSinceMs = 0L;
                return true;
            }

            _activeObjectHandler = null;
            _activeObjectGuid = 0UL;
            _activeObjectRunningSinceMs = 0L;
            return false;
        }

        public bool TryRunDungeonScriptBehaviors()
        {
            if (TryRunDungeonEncounterBehaviors())
            {
                StopActiveObjectBehavior();
                return true;
            }
            return TryRunDungeonObjectBehaviors();
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
            DistanceToCorridorSegment(point, from, to, out _, out _);

        internal static float DistanceToCorridorSegment(WoWPoint point, WoWPoint from, WoWPoint to, out float alongTrack) =>
            DistanceToCorridorSegment(point, from, to, out alongTrack, out _);

        internal static float DistanceToCorridorSegment(
            WoWPoint point,
            WoWPoint from,
            WoWPoint to,
            out float alongTrack,
            out float projectedZ)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0001f)
            {
                alongTrack = 0f;
                projectedZ = from.Z;
                float offsetX = point.X - from.X;
                float offsetY = point.Y - from.Y;
                return MathF.Sqrt(offsetX * offsetX + offsetY * offsetY);
            }

            float length = MathF.Sqrt(lengthSquared);
            alongTrack = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / length;
            float projection = Math.Clamp(alongTrack / length, 0f, 1f);
            projectedZ = from.Z + (to.Z - from.Z) * projection;
            float closestX = from.X + dx * projection;
            float closestY = from.Y + dy * projection;
            float deltaX = point.X - closestX;
            float deltaY = point.Y - closestY;
            return MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        internal static bool HasTrashOnSegment(
            WoWPoint from,
            WoWPoint to,
            IReadOnlyList<WoWPoint>? hostileLocations,
            float clearance = DungeonAdvanceTrashClearance)
        {
            if (clearance <= 0f || hostileLocations == null || hostileLocations.Count == 0)
                return false;

            foreach (WoWPoint hostile in hostileLocations)
            {
                if (hostile == WoWPoint.Zero)
                    continue;
                if (DistanceToCorridorSegment(hostile, from, to, out float alongTrack, out float projectedZ) <= clearance &&
                    alongTrack > 0f &&
                    MathF.Abs(hostile.Z - projectedZ) <= DungeonAdvanceClearanceHeight)
                    return true;
            }

            return false;
        }

        private static float DistanceToPointSegment3D(WoWPoint point, WoWPoint start, WoWPoint end)
        {
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float dz = end.Z - start.Z;
            float lengthSquared = dx * dx + dy * dy + dz * dz;
            if (lengthSquared <= 0.0001f)
                return point.Distance(start);

            float t = Math.Clamp(
                ((point.X - start.X) * dx + (point.Y - start.Y) * dy + (point.Z - start.Z) * dz) / lengthSquared,
                0f,
                1f);
            var closest = new WoWPoint(start.X + dx * t, start.Y + dy * t, start.Z + dz * t);
            return point.Distance(closest);
        }

        internal static bool HasTrashOnPolylineStep(
            WoWPoint from,
            IReadOnlyList<WoWPoint>? pathPoints,
            WoWPoint step,
            IReadOnlyList<WoWPoint>? hostileLocations,
            float clearance = DungeonAdvanceTrashClearance)
        {
            if (clearance <= 0f || hostileLocations == null || hostileLocations.Count == 0)
                return false;

            if (pathPoints != null && pathPoints.Count > 0)
            {
                WoWPoint cursor = from;
                bool matchedOnPath = false;
                var legs = new List<(WoWPoint From, WoWPoint To)>();
                for (int i = 0; i < pathPoints.Count; i++)
                {
                    WoWPoint wp = pathPoints[i];
                    if (wp == WoWPoint.Zero)
                        continue;

                    if (cursor.Distance(wp) <= 0.1f)
                    {
                        cursor = wp;
                        continue;
                    }

                    if (DistanceToPointSegment3D(step, cursor, wp) <= 0.35f)
                    {
                        if (cursor.Distance(step) > 0.1f)
                            legs.Add((cursor, step));
                        matchedOnPath = true;
                        break;
                    }

                    legs.Add((cursor, wp));
                    cursor = wp;
                }

                if (matchedOnPath)
                {
                    foreach (var leg in legs)
                    {
                        if (HasTrashOnSegment(leg.From, leg.To, hostileLocations, clearance))
                            return true;
                    }
                    return false;
                }
            }

            return HasTrashOnSegment(from, step, hostileLocations, clearance);
        }

        internal static (int X, int Y, int Z, uint MapId) QuantizeNavPoint(uint mapId, WoWPoint point) =>
            ((int)MathF.Round(point.X), (int)MathF.Round(point.Y), (int)MathF.Round(point.Z), mapId);

        internal static bool IsNavPointBlacklisted(
            IReadOnlyDictionary<(int X, int Y, int Z, uint MapId), long> blacklist,
            uint mapId,
            WoWPoint point,
            long nowUtcMs)
        {
            if (point == WoWPoint.Zero || blacklist.Count == 0)
                return false;

            return blacklist.TryGetValue(QuantizeNavPoint(mapId, point), out long untilUtcMs) && nowUtcMs < untilUtcMs;
        }

        internal static void BlacklistNavPoint(
            IDictionary<(int X, int Y, int Z, uint MapId), long> blacklist,
            uint mapId,
            WoWPoint point,
            long nowUtcMs,
            long durationMs = DungeonStepBlacklistCooldownMs)
        {
            if (point == WoWPoint.Zero || durationMs <= 0L)
                return;

            blacklist[QuantizeNavPoint(mapId, point)] = nowUtcMs + durationMs;
        }

        internal static void PruneExpiredNavBlacklist(
            IDictionary<(int X, int Y, int Z, uint MapId), long> blacklist,
            long nowUtcMs)
        {
            if (blacklist.Count == 0)
                return;

            List<(int X, int Y, int Z, uint MapId)>? expired = null;
            foreach (var entry in blacklist)
            {
                if (nowUtcMs >= entry.Value)
                    (expired ??= new List<(int, int, int, uint)>()).Add(entry.Key);
            }

            if (expired == null)
                return;

            foreach (var key in expired)
                blacklist.Remove(key);
        }

        public void BlacklistDungeonNavigationPoint(uint mapId, WoWPoint point, long nowUtcMs)
        {
            BlacklistNavPoint(_blacklistedNavPoints, mapId, point, nowUtcMs);
        }

        public bool IsDungeonNavigationPointBlacklisted(uint mapId, WoWPoint point, long nowUtcMs) =>
            IsNavPointBlacklisted(_blacklistedNavPoints, mapId, point, nowUtcMs);

        internal static WoWPoint ResolvePolylineAdvanceStep(
            WoWPoint from,
            IReadOnlyList<WoWPoint>? pathPoints,
            WoWPoint fallbackDestination,
            IReadOnlyList<WoWPoint>? hostileLocations,
            float maxStep = DungeonAdvanceStepDistance,
            float clearance = DungeonAdvanceTrashClearance)
        {
            if (from == WoWPoint.Zero)
                return WoWPoint.Zero;

            if (pathPoints != null && pathPoints.Count > 0)
            {
                WoWPoint cursor = from;
                float remaining = maxStep > 0f ? maxStep : float.MaxValue;
                WoWPoint selected = WoWPoint.Zero;

                for (int i = 0; i < pathPoints.Count; i++)
                {
                    WoWPoint wp = pathPoints[i];
                    if (wp == WoWPoint.Zero)
                        continue;

                    float segLen = cursor.Distance(wp);
                    if (segLen <= 0.25f)
                        continue;

                    WoWPoint segmentEnd = wp;
                    if (remaining < segLen)
                    {
                        float ratio = remaining / segLen;
                        segmentEnd = new WoWPoint(
                            cursor.X + (wp.X - cursor.X) * ratio,
                            cursor.Y + (wp.Y - cursor.Y) * ratio,
                            cursor.Z + (wp.Z - cursor.Z) * ratio);
                    }

                    if (HasTrashOnSegment(cursor, segmentEnd, hostileLocations, clearance))
                        return WoWPoint.Zero;

                    selected = segmentEnd;
                    remaining -= cursor.Distance(segmentEnd);
                    cursor = segmentEnd;

                    if (remaining <= 0.1f)
                        break;
                }

                if (selected != WoWPoint.Zero && from.Distance(selected) > 0.01f)
                    return selected;
            }

            return ResolveDungeonAdvanceStep(from, fallbackDestination, hostileLocations, maxStep, clearance);
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

            if (HasTrashOnSegment(from, step, hostileLocations, clearance))
                return WoWPoint.Zero;

            return step;
        }

        private bool IsReachableDungeonCandidate(uint mapId, WoWPoint from, WoWPoint candidate, long nowUtcMs)
        {
            if (candidate == WoWPoint.Zero)
                return false;
            if (IsNavPointBlacklisted(_blacklistedNavPoints, mapId, candidate, nowUtcMs))
                return false;
            return Navigator.NavigationProvider != null && Navigator.CanNavigateFully(from, candidate);
        }

        public WoWPoint ComputeLeaderNextDungeonPoint()
        {
            var me = StyxWoW.Me;
            if (me == null || Navigator.NavigationProvider == null)
                return WoWPoint.Zero;

            long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PruneExpiredNavBlacklist(_blacklistedNavPoints, nowUtcMs);

            if (BotPoi.Current.Type == PoiType.Hotspot && BotPoi.Current.Location != WoWPoint.Zero)
            {
                WoWPoint hs = BotPoi.Current.Location;
                if (me.Location.Distance(hs) <= HotspotArrivalDistance)
                    BotPoi.Clear("Reached dungeon hotspot");
                else if (!IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, hs, nowUtcMs))
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
                    WoWPoint? eligibleLiveBossLocation = liveCurrentBoss != null &&
                        !IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, liveCurrentBoss.Location, nowUtcMs)
                        ? liveCurrentBoss.Location
                        : null;
                    float? livePathDist = eligibleLiveBossLocation.HasValue
                        ? Navigator.PathDistance(me.Location, eligibleLiveBossLocation.Value, 100f)
                        : null;

                    int committedIndex = _breadcrumbIndex;
                    int scanIndex = committedIndex;
                    while (scanIndex < profileBoss.Path.Count)
                    {
                        int probeIdx = scanIndex;
                        WoWPoint crumb = AdvanceBossBreadcrumb(
                            me.Location,
                            profileBoss.Path,
                            WoWPoint.Zero,
                            eligibleLiveBossLocation,
                            livePathDist,
                            ref probeIdx);
                        if (crumb == WoWPoint.Zero)
                        {
                            if (!eligibleLiveBossLocation.HasValue && scanIndex == committedIndex)
                                committedIndex = probeIdx;
                            break;
                        }
                        if (scanIndex == committedIndex)
                            committedIndex = probeIdx;
                        if (eligibleLiveBossLocation.HasValue && crumb == eligibleLiveBossLocation.Value)
                        {
                            _breadcrumbIndex = committedIndex;
                            return crumb;
                        }
                        if (!IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, crumb, nowUtcMs))
                        {
                            _breadcrumbIndex = committedIndex;
                            return crumb;
                        }
                        scanIndex = probeIdx + 1;
                    }

                    _breadcrumbIndex = committedIndex;
                    bool breadcrumbRouteComplete = profileBoss.Path.Count == 0 ||
                        _breadcrumbIndex >= profileBoss.Path.Count;
                    int bossIdx = committedIndex;
                    WoWPoint bossTarget = AdvanceBossBreadcrumb(
                        me.Location,
                        Array.Empty<WoWPoint>(),
                        profileBoss.Location,
                        eligibleLiveBossLocation,
                        livePathDist,
                        ref bossIdx);
                    if (bossTarget != WoWPoint.Zero && IsReachableDungeonCandidate(me.MapId, me.Location, bossTarget, nowUtcMs))
                        return bossTarget;

                    if (liveCurrentBoss == null &&
                        breadcrumbRouteComplete &&
                        me.Location.Distance(profileBoss.Location) <= HotspotArrivalDistance)
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
                    .Where(u => !IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, u.Location, nowUtcMs))
                    .Where(u => Navigator.PathDistance(me.Location, u.Location, BossFallbackApproachDistance) is float pd &&
                                pd < BossFallbackApproachDistance)
                    .OrderBy(u => profile.BossEncounters.FirstOrDefault(b => b.Entry == u.Entry)?.KillOrder ?? int.MaxValue)
                    .ThenBy(u => u.DistanceSqr)
                    .FirstOrDefault();

                if (fallbackBoss != null)
                    return fallbackBoss.Location;

                foreach (var altBoss in profile.BossEncounters.OrderBy(b => b.KillOrder))
                {
                    if (profileBoss != null && (altBoss.Entry == profileBoss.Entry || altBoss.KillOrder > profileBoss.KillOrder))
                        continue;
                    if (altBoss.Optional && !BossManager.KillOptionalBossesEnabled)
                        continue;
                    if (BossManager.Bosses.Any(b => b.Entry == altBoss.Entry && b.IsDead))
                        continue;
                    if (altBoss.Location != WoWPoint.Zero &&
                        me.Location.Distance(altBoss.Location) > HotspotArrivalDistance &&
                        !IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, altBoss.Location, nowUtcMs) &&
                        Navigator.CanNavigateFully(me.Location, altBoss.Location))
                    {
                        return altBoss.Location;
                    }
                }

                while (_hotspotIndex < profile.HotSpots.Count)
                {
                    WoWPoint hs = profile.HotSpots[_hotspotIndex];
                    if (hs == WoWPoint.Zero ||
                        _visitedFallbackHotspots.Contains(_hotspotIndex) ||
                        me.Location.Distance(hs) <= HotspotArrivalDistance)
                    {
                        _visitedFallbackHotspots.Remove(_hotspotIndex);
                        _hotspotIndex++;
                        continue;
                    }
                    break;
                }

                for (int i = _hotspotIndex; i < profile.HotSpots.Count; i++)
                {
                    WoWPoint hotspot = profile.HotSpots[i];
                    if (hotspot == WoWPoint.Zero || _visitedFallbackHotspots.Contains(i))
                        continue;
                    if (me.Location.Distance(hotspot) <= HotspotArrivalDistance)
                    {
                        _visitedFallbackHotspots.Add(i);
                        continue;
                    }
                    if (!IsNavPointBlacklisted(_blacklistedNavPoints, me.MapId, hotspot, nowUtcMs))
                        return hotspot;
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
