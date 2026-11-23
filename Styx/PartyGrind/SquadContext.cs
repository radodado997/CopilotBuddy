using System;
using System.Collections.Generic;
using System.Linq;
using Styx.PartyGrind.IPC;

namespace Styx.PartyGrind
{
    /// <summary>
    /// Thread-safe runtime facade exposing active PartyGrind state to ProfileManager,
    /// overlay HUD, and role bridges.
    /// </summary>
    public static class SquadContext
    {
        private static readonly object _sync = new object();

        public static bool IsActive { get; internal set; }
        public static SquadSettings? ActiveSettings { get; internal set; }
        public static SquadLeaderState? LatestLeaderState { get; internal set; }
        public static SquadMemberState? LocalMemberState { get; internal set; }
        public static int? EffectiveLevelOverride { get; internal set; }
        public static bool IsIpcConnected { get; internal set; }
        public static long LastSyncTimestampUtcMs { get; internal set; }
        public static ulong ActivePullCommitGuid { get; internal set; }

        public static SquadRole ActiveRole => ActiveSettings?.Role ?? SquadRole.DpsMelee;
        public static bool IsCoordinator => ActiveSettings?.IsCoordinator ?? false;
        internal static SquadBotMode ResolveBotMode(SquadLeaderState? leader, SquadSettings? settings) =>
            leader?.BotMode ?? settings?.BotMode ?? SquadBotMode.OpenWorldGrind;

        public static bool IsDungeonModeActive =>
            IsActive && ResolveBotMode(LatestLeaderState, ActiveSettings) == SquadBotMode.Dungeon;
        public static bool ActiveKillOptionalBosses =>
            IsActive &&
            (LatestLeaderState?.KillOptionalBosses ?? ActiveSettings?.KillOptionalBosses ?? false);

        /// <summary>
        /// Returns the effective level used by <c>ProfileManager.LevelOverrideSelector</c>.
        /// </summary>
        public static int GetEffectiveProfileLevel()
        {
            int? overrideLvl = EffectiveLevelOverride;
            if (overrideLvl.HasValue && overrideLvl.Value > 0)
                return overrideLvl.Value;
            return StyxWoW.Me?.Level ?? 1;
        }

        public static void UpdateLeaderState(SquadLeaderState? state)
        {
            lock (_sync)
            {
                if (ReferenceEquals(LatestLeaderState, state)) return;
                LatestLeaderState = state;
                if (state == null) EffectiveLevelOverride = null;
                if (state != null)
                {
                    if (state.EffectiveSquadMinLevel > 0)
                        EffectiveLevelOverride = state.EffectiveSquadMinLevel;
                    LastSyncTimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
        }

        public static void UpdateLocalMemberState(SquadMemberState? state)
        {
            lock (_sync)
            {
                LocalMemberState = state;
            }
        }

        public static SquadLeaderState? GetLeaderStateSnapshot()
        {
            lock (_sync) return LatestLeaderState;
        }

        public static SquadMemberState? GetLocalMemberStateSnapshot()
        {
            lock (_sync) return LocalMemberState;
        }

        public static int GetMySlotIndex(string? myName = null, SquadLeaderState? leader = null)
        {
            myName ??= StyxWoW.Me?.Name ?? LocalMemberState?.CharacterName ?? string.Empty;
            leader ??= GetLeaderStateSnapshot();
            if (leader == null || string.IsNullOrEmpty(myName))
                return 0;

            foreach (var kv in leader.MemberAssignments)
            {
                if (string.Equals(kv.Key, myName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }

            if (leader.Members != null && leader.Members.Count > 0)
            {
                var followers = leader.Members
                    .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName) &&
                                m.Role != SquadRole.TankLeader &&
                                (leader.LeaderGuid == 0 || m.Guid != leader.LeaderGuid) &&
                                !string.Equals(m.CharacterName, leader.LeaderName, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.CharacterName.Trim())
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int idx = followers.FindIndex(n => string.Equals(n, myName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    return idx;
            }
            return 0;
        }

        public static IReadOnlyList<SquadMemberSummary> GetSquadMembers()
        {
            var leader = GetLeaderStateSnapshot();
            return leader?.Members ?? (IReadOnlyList<SquadMemberSummary>)Array.Empty<SquadMemberSummary>();
        }

        public static SquadMemberSummary? GetMemberByGuid(ulong guid)
        {
            if (guid == 0) return null;
            return GetSquadMembers().FirstOrDefault(m => m.Guid == guid);
        }

        public static SquadMemberSummary? GetMemberByRole(SquadRole role)
        {
            return GetSquadMembers().FirstOrDefault(m => m.Role == role);
        }

        public static bool IsMemberRole(ulong guid, SquadRole role)
        {
            var m = GetMemberByGuid(guid);
            return m != null && m.Role == role;
        }

        public static HashSet<ulong> GetKnownMemberGuids()
        {
            var set = new HashSet<ulong>();
            var leader = GetLeaderStateSnapshot();
            if (leader != null)
            {
                if (leader.LeaderGuid != 0)
                    set.Add(leader.LeaderGuid);
                foreach (var m in leader.Members)
                {
                    if (m.IsConnected && m.Guid != 0)
                        set.Add(m.Guid);
                }
            }
            if (StyxWoW.Me != null)
            {
                set.Add(StyxWoW.Me.Guid);
                foreach (var pm in StyxWoW.Me.PartyMemberInfos)
                {
                    if (pm.Guid != 0)
                        set.Add(pm.Guid);
                }
            }
            return set;
        }

        public static void Reset()
        {
            lock (_sync)
            {
                IsActive = false;
                ActiveSettings = null;
                LatestLeaderState = null;
                LocalMemberState = null;
                EffectiveLevelOverride = null;
                IsIpcConnected = false;
                LastSyncTimestampUtcMs = 0;
                ActivePullCommitGuid = 0;
            }
        }
    }
}
