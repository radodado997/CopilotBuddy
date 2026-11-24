using System;
using System.Collections.Generic;
using System.Linq;
using Bots.Grind;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Integrates Targeting filters, Tank peel/taunt priority, pull readiness gating,
    /// defensive aura enforcement, and wild-pull prevention for non-tank squadmates.
    /// </summary>
    public static class SquadTankBridge
    {
        private static bool _filtersAttached;
        private const float DungeonPullGatherDistance = 20f;
        private const float DungeonRouteTrashCorridorRadius = 12f;
        private const float DungeonRouteTrashScanDistance = 45f;
        private const float DungeonRouteTrashMaxVerticalDelta = 8f;
        private static readonly SquadApproach PeelApproach = new();

        public static void AttachTargetingFilters()
        {
            if (_filtersAttached)
                return;

            Targeting.Instance.IncludeTargetsFilter += SquadIncludeTargetsFilter;
            Targeting.Instance.RemoveTargetsFilter += SquadRemoveTargetsFilter;
            Targeting.Instance.WeighTargetsFilter += SquadWeighTargetsFilter;
            _filtersAttached = true;
        }

        public static void DetachTargetingFilters()
        {
            if (!_filtersAttached)
                return;

            Targeting.Instance.IncludeTargetsFilter -= SquadIncludeTargetsFilter;
            Targeting.Instance.RemoveTargetsFilter -= SquadRemoveTargetsFilter;
            Targeting.Instance.WeighTargetsFilter -= SquadWeighTargetsFilter;
            _filtersAttached = false;
        }

        internal static float GetEffectivePullGatherDistance(float configuredDistance, bool isDungeon)
        {
            return isDungeon ? Math.Min(configuredDistance, DungeonPullGatherDistance) : configuredDistance;
        }

        internal static bool IsWithinDungeonRouteCorridor(
            WoWPoint start,
            WoWPoint destination,
            WoWPoint candidate,
            float corridorRadius,
            float maxForwardDistance,
            float maxVerticalDelta)
        {
            float dx = destination.X - start.X;
            float dy = destination.Y - start.Y;
            float routeLength = (float)Math.Sqrt(dx * dx + dy * dy);
            if (routeLength <= 0.01f)
                return false;

            float nx = dx / routeLength;
            float ny = dy / routeLength;
            float candidateX = candidate.X - start.X;
            float candidateY = candidate.Y - start.Y;
            float alongRoute = candidateX * nx + candidateY * ny;
            if (alongRoute < 0f || alongRoute > maxForwardDistance || alongRoute > routeLength)
                return false;

            float lateral = Math.Abs(candidateX * ny - candidateY * nx);
            if (lateral > corridorRadius)
                return false;

            float expectedZ = start.Z + (destination.Z - start.Z) * (alongRoute / routeLength);
            return Math.Abs(candidate.Z - expectedZ) <= maxVerticalDelta;
        }

        internal static bool IsDungeonBossTarget(WoWUnit? unit)
        {
            return unit != null && global::Bots.DungeonBuddy.BossManager.Bosses.Any(b => b.EntryId == unit.Entry);
        }

        internal static bool IsIdleDungeonBossTarget(WoWUnit? unit)
        {
            return IsDungeonBossTarget(unit) && unit is { IsValid: true, IsAlive: true } &&
                   !unit.Combat && !unit.IsTargetingMyPartyMember && !unit.IsTargetingMeOrPet;
        }

        internal static bool IsDungeonPullTargetRetrying(ulong targetGuid)
        {
            return PullApproach.IsRetrying(targetGuid);
        }

        internal static WoWUnit? FindDungeonRouteTrashTarget(WoWPoint start, WoWPoint destination)
        {
            var me = StyxWoW.Me;
            if (me == null || destination == WoWPoint.Zero)
                return null;

            var bossEntries = new HashSet<uint>(
                global::Bots.DungeonBuddy.BossManager.Bosses
                    .Where(b => !b.IsDead)
                    .Select(b => b.EntryId));

            return ObjectManager.CachedUnits
                .Where(unit => unit != null && unit.IsValid && !unit.Dead && unit.CanSelect && unit.Attackable &&
                               unit.IsHostile && !unit.IsFriendly && !unit.IsPlayer && !unit.IsCritter &&
                               !unit.IsNonCombatPet && unit.ControllingPlayer == null && !unit.Combat &&
                               !unit.IsTargetingMyPartyMember && !unit.IsTargetingMeOrPet &&
                               !bossEntries.Contains(unit.Entry) && unit.Distance <= DungeonRouteTrashScanDistance &&
                               unit.InLineOfSight && !PullApproach.IsRetrying(unit.Guid) &&
                               !global::Bots.DungeonBuddy.Profiles.ProfileManager.IsNpcInPullBlackspot(unit) &&
                               IsWithinDungeonRouteCorridor(start, destination, unit.Location,
                                   DungeonRouteTrashCorridorRadius, DungeonRouteTrashScanDistance,
                                   DungeonRouteTrashMaxVerticalDelta))
                .OrderBy(unit => unit.Distance)
                .FirstOrDefault();
        }

        /// <summary>
        /// Pure testable readiness gate verifying whether the TankLeader may initiate a new pull.
        /// </summary>
        public static bool CanInitiatePull(SquadLeaderState? leader, float maxPullGatherDistance)
        {
            if (leader == null)
                return true;

            if (leader.Phase != SquadPhase.Grinding && leader.Phase != SquadPhase.DungeonClearing)
                return false;

            if (!string.IsNullOrEmpty(leader.ActiveProvisionerName))
                return false;

            // Use the coordinator's authoritative loot window instead of a possibly
            // stale follower IsLooting flag; an expired window must not veto pulls.
            if (leader.IsLootWindowActive)
                return false;

            if (leader.Members == null || leader.Members.Count == 0)
                return true;

            var leaderPos = new WoWPoint(leader.X, leader.Y, leader.Z);
            foreach (var m in leader.Members)
            {
                if (!m.IsConnected)
                    continue;
                if (!m.HasTelemetry || m.IsDead || m.IsGhost || m.IsInCombat || m.NeedsRest ||
                    (m.NeedsTown && !m.TownTasksComplete && leader.BotMode != SquadBotMode.Dungeon))
                    return false;
                if (m.MapId != leader.MapId)
                    return false;
                float dist = leaderPos.Distance(new WoWPoint(m.X, m.Y, m.Z));
                if (dist > maxPullGatherDistance)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Pure testable target whitelist for non-tank squadmates to forbid wild pulls.
        /// </summary>
        public static bool IsAllowedDpsTarget(
            ulong unitGuid,
            bool unitInCombat,
            bool targetingMeOrPet,
            bool targetingPartyMember,
            ulong myAggroGuid,
            SquadLeaderState? leader,
            float distanceToTank,
            bool tankAlive)
        {
            if (unitGuid == 0)
                return false;

            if (leader != null)
            {
                if (unitGuid == leader.FocusTargetGuid || (leader.SecondaryTargetGuid != 0 && unitGuid == leader.SecondaryTargetGuid))
                    return true;
            }

            if (unitInCombat && (targetingPartyMember || unitGuid == myAggroGuid))
                return true;

            if (unitInCombat && targetingMeOrPet && (!tankAlive || distanceToTank > 50f))
                return true;

            return false;
        }

        /// <summary>
        /// Pure testable weight bonus used by TankLeader to peel mobs off healers and allies first.
        /// </summary>
        public static double ComputeTankTargetBonus(
            bool isTargetingNonTankPartyMember,
            bool isTargetingHealer,
            bool alreadyInCombatWithSquad)
        {
            if (isTargetingNonTankPartyMember)
                return isTargetingHealer ? 10500.0 : 10000.0;
            if (alreadyInCombatWithSquad)
                return 2000.0;
            return 0.0;
        }

        private static void SquadIncludeTargetsFilter(List<WoWObject> incomingUnits, HashSet<WoWObject> outgoingUnits)
        {
            if (!SquadContext.IsActive || StyxWoW.Me == null)
                return;

            var settings = SquadContext.ActiveSettings;
            if (settings == null)
                return;

            var leader = SquadContext.GetLeaderStateSnapshot();
            var memberGuids = SquadContext.GetKnownMemberGuids();
            bool isDungeon = SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(StyxWoW.Me);
            bool pugIsTank = isDungeon && leader != null && leader.EffectiveTankGuid != 0 &&
                             leader.EffectiveTankGuid != StyxWoW.Me.Guid &&
                             leader.HasRandomPartyMembers;

            if (settings.Role == SquadRole.TankLeader && !pugIsTank)
            {
                ulong committedGuid = SquadContext.ActivePullCommitGuid;
                if (isDungeon)
                {
                    WoWPoint meLoc = StyxWoW.Me.Location;
                    float pullGatherDistance = GetEffectivePullGatherDistance(settings.MaxPullGatherDistance, isDungeon);
                    bool canPull = StyxWoW.Me.Combat || committedGuid != 0 || CanInitiatePull(leader, pullGatherDistance);
                    foreach (WoWObject obj in incomingUnits)
                    {
                        if (obj is not WoWUnit unit || !unit.IsValid || unit.Dead || !unit.CanSelect || !unit.Attackable)
                            continue;
                        if (unit.IsFriendly || unit.IsPlayer || unit.IsCritter || unit.IsNonCombatPet || unit.ControllingPlayer != null)
                            continue;

                        if (committedGuid != 0 && unit.Guid == committedGuid)
                        {
                            outgoingUnits.Add(unit);
                            continue;
                        }

                        bool partyThreat = unit.IsTargetingMyPartyMember || unit.IsTargetingMeOrPet ||
                                           (unit.Combat && unit.CurrentTargetGuid != 0 && memberGuids.Contains(unit.CurrentTargetGuid));

                        if (partyThreat && unit.Location.Distance(meLoc) <= 60f)
                        {
                            outgoingUnits.Add(unit);
                            continue;
                        }

                        if (!StyxWoW.Me.Combat && canPull)
                        {
                            if (!unit.IsHostile || unit.DistanceSqr > 1225.0)
                                continue;
                            if (global::Bots.DungeonBuddy.Profiles.ProfileManager.IsNpcInPullBlackspot(unit))
                                continue;
                            if (!unit.InLineOfSight)
                                continue;
                            if (!unit.IsFlying && !Navigator.CanNavigateWithinDistance(meLoc, unit.Location, 35f))
                                continue;
                            outgoingUnits.Add(unit);
                        }
                    }
                    return;
                }

                // Include standard LevelBot targets when we're ready to pull or already fighting.
                if (StyxWoW.Me.Combat || committedGuid != 0 ||
                    CanInitiatePull(leader, GetEffectivePullGatherDistance(settings.MaxPullGatherDistance, isDungeon)))
                {
                    LevelBot.LevelBotIncludeTargetsFilter(incomingUnits, outgoingUnits);
                }

                // Always include hostile mobs currently attacking any squad member (peel priority).
                WoWPoint meLocWorld = StyxWoW.Me.Location;
                foreach (WoWObject obj in incomingUnits)
                {
                    if (obj is not WoWUnit unit || !unit.IsValid || unit.Dead || !unit.CanSelect || !unit.Attackable)
                        continue;
                    if (unit.IsFriendly || unit.IsPlayer)
                        continue;
                    if (committedGuid != 0 && unit.Guid == committedGuid)
                    {
                        outgoingUnits.Add(unit);
                        continue;
                    }
                    if (unit.Location.Distance(meLocWorld) > 60f)
                        continue;

                    if (unit.IsTargetingMyPartyMember || unit.IsTargetingMeOrPet ||
                        (unit.Combat && unit.CurrentTargetGuid != 0 && memberGuids.Contains(unit.CurrentTargetGuid)))
                    {
                        outgoingUnits.Add(unit);
                    }
                }
            }
            else
            {
                ulong pugTankTargetGuid = GetPugTankTargetGuid(leader);

                // Non-tank members: include the leader's focus/secondary targets and active party threats.
                foreach (WoWObject obj in incomingUnits)
                {
                    if (obj is not WoWUnit unit || !unit.IsValid || unit.Dead || !unit.CanSelect || !unit.Attackable)
                        continue;
                    if (unit.IsFriendly || unit.IsPlayer)
                        continue;

                    if (pugTankTargetGuid != 0UL && unit.Guid == pugTankTargetGuid)
                    {
                        outgoingUnits.Add(unit);
                        continue;
                    }

                    if (leader != null &&
                        (unit.Guid == leader.FocusTargetGuid ||
                         (leader.SecondaryTargetGuid != 0 && unit.Guid == leader.SecondaryTargetGuid)))
                    {
                        outgoingUnits.Add(unit);
                        continue;
                    }

                    if (unit.Combat &&
                        (unit.IsTargetingMyPartyMember || unit.IsTargetingMeOrPet ||
                         (unit.CurrentTargetGuid != 0 && memberGuids.Contains(unit.CurrentTargetGuid))))
                    {
                        outgoingUnits.Add(unit);
                    }
                }
            }
        }

        private static ulong GetPugTankTargetGuid(SquadLeaderState? leader)
        {
            if (!SquadContext.IsDungeonModeActive || !SquadDungeonService.IsInDungeonMap(StyxWoW.Me) ||
                leader == null || !leader.HasRandomPartyMembers || leader.EffectiveTankGuid == 0 ||
                leader.EffectiveTankGuid == StyxWoW.Me.Guid)
                return 0UL;

            var tank = ObjectManager.GetObjectByGuid<WoWPlayer>(leader.EffectiveTankGuid);
            return tank != null && tank.IsValid && tank.Combat && tank.CurrentTarget != null
                ? tank.CurrentTargetGuid
                : 0UL;
        }

        private static void SquadRemoveTargetsFilter(List<WoWObject> units)
        {
            if (!SquadContext.IsActive || StyxWoW.Me == null)
                return;

            var settings = SquadContext.ActiveSettings;
            if (settings == null || settings.Role == SquadRole.TankLeader)
                return;

            var leader = SquadContext.GetLeaderStateSnapshot();
            var memberGuids = SquadContext.GetKnownMemberGuids();
            ulong myAggroGuid = SquadContext.GetLocalMemberStateSnapshot()?.AggroAttackerGuid ?? 0UL;

            bool tankAlive = leader != null && !leader.IsDead && !leader.IsGhost;
            float distToTank = leader != null
                ? StyxWoW.Me.Location.Distance(new WoWPoint(leader.X, leader.Y, leader.Z))
                : 999f;

            ulong pugTankTargetGuid = GetPugTankTargetGuid(leader);
            units.RemoveAll(obj =>
            {
                if (obj is not WoWUnit unit || !unit.IsValid || unit.Dead)
                    return true;

                bool targetingParty = unit.IsTargetingMyPartyMember ||
                    (unit.CurrentTargetGuid != 0 && memberGuids.Contains(unit.CurrentTargetGuid));

                return unit.Guid != pugTankTargetGuid && !IsAllowedDpsTarget(
                    unit.Guid,
                    unit.Combat,
                    unit.IsTargetingMeOrPet,
                    targetingParty,
                    myAggroGuid,
                    leader,
                    distToTank,
                    tankAlive);
            });
        }

        private static void SquadWeighTargetsFilter(List<Targeting.TargetPriority> units)
        {
            if (!SquadContext.IsActive || StyxWoW.Me == null)
                return;

            var settings = SquadContext.ActiveSettings;
            if (settings == null)
                return;

            var leader = SquadContext.GetLeaderStateSnapshot();
            var memberGuids = SquadContext.GetKnownMemberGuids();
            bool isDungeon = SquadContext.IsDungeonModeActive && SquadDungeonService.IsInDungeonMap(StyxWoW.Me);

            if (settings.Role == SquadRole.TankLeader)
            {
                ulong myGuid = StyxWoW.Me.Guid;
                foreach (var tp in units)
                {
                    if (tp.Object is not WoWUnit unit || !unit.IsValid)
                        continue;

                    ulong targetGuid = unit.CurrentTargetGuid;
                    bool targetingOtherParty = targetGuid != 0 && targetGuid != myGuid &&
                        (unit.IsTargetingMyPartyMember || memberGuids.Contains(targetGuid));
                    bool targetingHealer = targetingOtherParty && SquadContext.IsMemberRole(targetGuid, SquadRole.Healer);
                    bool inCombatWithSquad = unit.Combat && (unit.IsTargetingMeOrPet || unit.IsTargetingMyPartyMember || unit.TaggedByMe);

                    tp.Score += ComputeTankTargetBonus(targetingOtherParty, targetingHealer, inCombatWithSquad);
                    if (isDungeon && !StyxWoW.Me.Combat)
                        tp.Score -= unit.Distance * 2.0;
                }
            }
            else if (leader != null)
            {
                foreach (var tp in units)
                {
                    if (tp.Object is not WoWUnit unit || !unit.IsValid)
                        continue;

                    if (unit.Guid == leader.FocusTargetGuid)
                        tp.Score += 50000.0;
                    else if (leader.SecondaryTargetGuid != 0 && unit.Guid == leader.SecondaryTargetGuid)
                        tp.Score += 10000.0;
                }
            }
        }

        /// <summary>
        /// Ensures the TankLeader keeps its tanking stance, form, aura, or presence active.
        /// </summary>
        public static bool EnsureTankDefensiveStanceOrAura(SquadSettings settings)
        {
            var me = StyxWoW.Me;
            if (me == null || !settings.ForceTankDefensiveStanceOrAura || me.Mounted)
                return false;

            switch (me.Class)
            {
                case WoWClass.Warrior:
                    if (!me.HasAura("Defensive Stance") && SpellManager.CanCast("Defensive Stance", me, false, false))
                        return SpellManager.Cast("Defensive Stance", me);
                    break;

                case WoWClass.Druid:
                    if (!me.HasAura("Dire Bear Form") && !me.HasAura("Bear Form"))
                    {
                        if (SpellManager.CanCast("Dire Bear Form", me, false, false))
                            return SpellManager.Cast("Dire Bear Form", me);
                        if (SpellManager.CanCast("Bear Form", me, false, false))
                            return SpellManager.Cast("Bear Form", me);
                    }
                    break;

                case WoWClass.Paladin:
                    if (!me.HasAura("Righteous Fury") && SpellManager.CanCast("Righteous Fury", me, false, false))
                        return SpellManager.Cast("Righteous Fury", me);
                    break;

                case WoWClass.DeathKnight:
                    if (!me.HasAura("Frost Presence") && SpellManager.CanCast("Frost Presence", me, false, false))
                        return SpellManager.Cast("Frost Presence", me);
                    break;
            }

            return false;
        }

        public static bool HasAvailableTauntSpell(WoWClass playerClass, Func<string, bool> hasSpell)
        {
            return playerClass switch
            {
                WoWClass.Warrior => hasSpell("Taunt") || hasSpell("Challenging Shout") || hasSpell("Mocking Blow"),
                WoWClass.Druid => hasSpell("Growl") || hasSpell("Challenging Roar"),
                WoWClass.Paladin => hasSpell("Hand of Reckoning") || hasSpell("Righteous Defense"),
                WoWClass.DeathKnight => hasSpell("Dark Command") || hasSpell("Death Grip"),
                _ => false
            };
        }

        /// <summary>
        /// Scans for hostile mobs attacking non-tank party members and casts a class taunt to peel them.
        /// </summary>
        public static bool TryTauntLooseMob()
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive)
                return false;

            var leader = SquadContext.GetLeaderStateSnapshot();
            if (SquadContext.IsDungeonModeActive &&
                SquadDungeonService.IsInDungeonMap(me) &&
                leader != null &&
                leader.EffectiveTankGuid != 0 &&
                leader.EffectiveTankGuid != me.Guid)
            {
                return false;
            }

            if (!HasAvailableTauntSpell(me.Class, SpellManager.HasSpell))
                return false;

            var memberGuids = SquadContext.GetKnownMemberGuids();
            ulong myGuid = me.Guid;

            var looseMobs = ObjectManager.CachedUnits
                .Where(u => u != null && u.IsValid && !u.Dead && u.Combat && u.Attackable && !u.IsFriendly && !u.IsPlayer)
                .Where(u => u.CurrentTargetGuid != 0 && u.CurrentTargetGuid != myGuid &&
                            (u.IsTargetingMyPartyMember || memberGuids.Contains(u.CurrentTargetGuid)))
                .OrderByDescending(u => SquadContext.IsMemberRole(u.CurrentTargetGuid, SquadRole.Healer) ? 1 : 0)
                .ThenBy(u => u.Location.Distance(me.Location))
                .ToList();

            if (looseMobs.Count == 0)
                return false;

            foreach (WoWUnit loose in looseMobs)
            {
                if (me.Class == WoWClass.Paladin && !SpellManager.HasSpell("Hand of Reckoning"))
                {
                    WoWUnit? attackedAlly = ObjectManager.GetObjectByGuid<WoWUnit>(loose.CurrentTargetGuid);
                    if (attackedAlly != null && attackedAlly.IsValid && !attackedAlly.Dead &&
                        SpellManager.CanCast("Righteous Defense", attackedAlly, false, false))
                    {
                        if (attackedAlly.Distance > 38f || !attackedAlly.InLineOfSpellSight)
                        {
                            if (PeelApproach.TryMove(attackedAlly, "Peeling for " + attackedAlly.Name))
                                return true;
                            continue;
                        }
                        if (SpellManager.CanCast("Righteous Defense", attackedAlly, true, false))
                            return SpellManager.Cast("Righteous Defense", attackedAlly);
                    }
                    continue;
                }

                bool canTauntThisMob = me.Class switch
                {
                    WoWClass.Warrior =>
                        (looseMobs.Count(u => u.Distance <= 9f) >= 2 && SpellManager.CanCast("Challenging Shout", me, false, false)) ||
                        SpellManager.CanCast("Taunt", loose, false, false) ||
                        SpellManager.CanCast("Mocking Blow", loose, false, false),
                    WoWClass.Druid =>
                        (looseMobs.Count(u => u.Distance <= 9f) >= 2 && SpellManager.CanCast("Challenging Roar", me, false, false)) ||
                        SpellManager.CanCast("Growl", loose, false, false),
                    WoWClass.Paladin =>
                        SpellManager.CanCast("Hand of Reckoning", loose, false, false) ||
                        (ObjectManager.GetObjectByGuid<WoWUnit>(loose.CurrentTargetGuid) is WoWUnit ally &&
                         ally.IsValid && !ally.Dead && SpellManager.CanCast("Righteous Defense", ally, true, false)),
                    WoWClass.DeathKnight =>
                        SpellManager.CanCast("Dark Command", loose, false, false) ||
                        SpellManager.CanCast("Death Grip", loose, false, false),
                    _ => false
                };

                if (!canTauntThisMob)
                    continue;

                if (loose.Distance > 28f || !loose.InLineOfSpellSight)
                {
                    if (PeelApproach.TryMove(loose, "Peeling " + loose.Name)) return true;
                    continue;
                }

                if (me.CurrentTargetGuid != loose.Guid)
                    loose.Target();

                if (!me.IsFacing(loose))
                    loose.Face();

                switch (me.Class)
                {
                    case WoWClass.Warrior:
                        if (looseMobs.Count(u => u.Distance <= 9f) >= 2 && SpellManager.CanCast("Challenging Shout", me, false, false))
                            return SpellManager.Cast("Challenging Shout", me);
                        if (SpellManager.CanCast("Taunt", loose, true, false))
                            return SpellManager.Cast("Taunt", loose);
                        if (loose.Distance <= 5.5f && SpellManager.CanCast("Mocking Blow", loose, true, false))
                            return SpellManager.Cast("Mocking Blow", loose);
                        break;

                    case WoWClass.Druid:
                        if (looseMobs.Count(u => u.Distance <= 9f) >= 2 && SpellManager.CanCast("Challenging Roar", me, false, false))
                            return SpellManager.Cast("Challenging Roar", me);
                        if (SpellManager.CanCast("Growl", loose, true, false))
                            return SpellManager.Cast("Growl", loose);
                        break;

                    case WoWClass.Paladin:
                        if (SpellManager.CanCast("Hand of Reckoning", loose, true, false))
                            return SpellManager.Cast("Hand of Reckoning", loose);
                        WoWUnit? attackedAlly = ObjectManager.GetObjectByGuid<WoWUnit>(loose.CurrentTargetGuid);
                        if (attackedAlly != null && attackedAlly.IsValid && !attackedAlly.Dead &&
                            SpellManager.CanCast("Righteous Defense", attackedAlly, true, false))
                        {
                            return SpellManager.Cast("Righteous Defense", attackedAlly);
                        }
                        break;

                    case WoWClass.DeathKnight:
                        if (SpellManager.CanCast("Dark Command", loose, true, false))
                            return SpellManager.Cast("Dark Command", loose);
                        if (SpellManager.CanCast("Death Grip", loose, true, false))
                            return SpellManager.Cast("Death Grip", loose);
                        break;
                }
            }
            return false;
        }

        private static long _lastDungeonPullCastUtcMs;
        private static ulong _lastDungeonPullTargetGuid;
        private static readonly SquadApproach PullApproach = new();

        public static bool TryExecuteSafeDungeonPull(ulong targetGuid)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || me.Combat || targetGuid == 0)
                return false;

            WoWUnit? target = ObjectManager.GetObjectByGuid<WoWUnit>(targetGuid);
            if (target == null || !target.IsValid || target.Dead || target.IsFriendly || !target.Attackable)
                return false;

            if (target.Combat || target.IsTargetingMyPartyMember || target.IsTargetingMeOrPet)
                return false;

            long now = Environment.TickCount64;
            if (_lastDungeonPullTargetGuid == targetGuid && now - _lastDungeonPullCastUtcMs < 3500L)
            {
                if (me.IsMoving)
                    WoWMovement.MoveStop();
                if (!me.IsFacing(target))
                    target.Face();
                return true;
            }

            string[] pullSpells = me.Class switch
            {
                WoWClass.Paladin => new[] { "Avenger's Shield", "Hand of Reckoning", "Exorcism" },
                WoWClass.Warrior => new[] { "Heroic Throw", "Taunt", "Shoot", "Throw" },
                WoWClass.Druid => new[] { "Faerie Fire (Feral)", "Growl", "Moonfire" },
                WoWClass.DeathKnight => new[] { "Dark Command", "Icy Touch", "Death Coil" },
                _ => Array.Empty<string>()
            };

            string? chosenSpell = pullSpells.FirstOrDefault(s =>
                SpellManager.HasSpell(s) && SpellManager.CanCast(s, target, checkRange: false, checkMovement: false));
            if (string.IsNullOrEmpty(chosenSpell))
                return false;

            float maxRange = 28f;
            if (SpellManager.Spells.TryGetValue(chosenSpell, out WoWSpell? info) && info.MaxRange > 5f)
                maxRange = Math.Max(8f, info.MaxRange - 1.5f);

            if (target.Distance > maxRange || !target.InLineOfSpellSight)
            {
                if (!global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled)
                    return false;
                return PullApproach.TryMove(target, "Approaching dungeon pull " + target.Name);
            }

            if (me.IsMoving)
                WoWMovement.MoveStop();

            if (me.CurrentTargetGuid != target.Guid)
                target.Target();

            if (!me.IsFacing(target))
                target.Face();

            if (SpellManager.CanCast(chosenSpell, target, true, false) && SpellManager.Cast(chosenSpell, target))
            {
                _lastDungeonPullTargetGuid = targetGuid;
                _lastDungeonPullCastUtcMs = now;
                SquadPartySyncService.LogAction("Pulling " + target.Name + " with " + chosenSpell);
                return true;
            }

            return false;
        }
    }
}
