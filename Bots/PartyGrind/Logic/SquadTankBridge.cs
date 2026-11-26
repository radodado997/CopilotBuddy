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
            return ComputeTankTargetBonus(
                isTargetingNonTankPartyMember,
                isTargetingHealer,
                alreadyInCombatWithSquad,
                distanceToTank: 0f,
                tankInCombat: false);
        }

        public static double ComputeTankTargetBonus(
            bool isTargetingNonTankPartyMember,
            bool isTargetingHealer,
            bool alreadyInCombatWithSquad,
            float distanceToTank,
            bool tankInCombat)
        {
            double bonus = 0.0;
            if (isTargetingNonTankPartyMember)
                bonus = isTargetingHealer ? 10500.0 : 10000.0;
            else if (alreadyInCombatWithSquad)
                bonus = 2000.0;

            if (tankInCombat && distanceToTank > 10f)
                bonus -= 9500.0 + (distanceToTank - 10f) * 100.0;

            return bonus;
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
                    bool canPull = StyxWoW.Me.Combat || committedGuid != 0 || CanInitiatePull(leader, settings.MaxPullGatherDistance);
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
                if (StyxWoW.Me.Combat || committedGuid != 0 || CanInitiatePull(leader, settings.MaxPullGatherDistance))
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
                WoWPoint meLoc = StyxWoW.Me.Location;
                bool tankInCombat = StyxWoW.Me.Combat;
                foreach (var tp in units)
                {
                    if (tp.Object is not WoWUnit unit || !unit.IsValid)
                        continue;

                    ulong targetGuid = unit.CurrentTargetGuid;
                    bool targetingOtherParty = targetGuid != 0 && targetGuid != myGuid &&
                        (unit.IsTargetingMyPartyMember || memberGuids.Contains(targetGuid));
                    bool targetingHealer = targetingOtherParty && SquadContext.IsMemberRole(targetGuid, SquadRole.Healer);
                    bool inCombatWithSquad = unit.Combat && (unit.IsTargetingMeOrPet || unit.IsTargetingMyPartyMember || unit.TaggedByMe);
                    float distToTank = (float)unit.Location.Distance(meLoc);

                    tp.Score += ComputeTankTargetBonus(targetingOtherParty, targetingHealer, inCombatWithSquad, distToTank, tankInCombat);
                    if (isDungeon && !tankInCombat)
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

        private static long _lastGlobalTauntUtcMs;
        private static readonly Dictionary<ulong, long> LastMobTauntUtcMs = new();

        internal static bool CanAttemptTauntPeel(long lastGlobalTauntMs, long lastMobTauntMs, long nowMs)
        {
            return nowMs - lastGlobalTauntMs >= 2000L && nowMs - lastMobTauntMs >= 4000L;
        }

        private static bool RecordTauntCast(ulong mobGuid, long nowMs)
        {
            _lastGlobalTauntUtcMs = nowMs;
            LastMobTauntUtcMs[mobGuid] = nowMs;
            return true;
        }

        private static bool RecordAreaTauntCast(IEnumerable<WoWUnit> looseMobs, long nowMs)
        {
            _lastGlobalTauntUtcMs = nowMs;
            foreach (WoWUnit mob in looseMobs)
            {
                if (mob.Distance <= 9f)
                    LastMobTauntUtcMs[mob.Guid] = nowMs;
            }
            return true;
        }

        private static bool IsSquadOrPartyHealer(LocalPlayer me, ulong guid)
        {
            if (guid == 0UL)
                return false;
            if (SquadContext.IsMemberRole(guid, SquadRole.Healer))
                return true;
            return me.GroupInfo.AllMembers.Any(pm => pm.Guid == guid && pm.HasRole(WoWPartyMember.GroupRole.Healer));
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

            long nowMs = Environment.TickCount64;
            if (nowMs - _lastGlobalTauntUtcMs < 2000L)
                return false;

            if (LastMobTauntUtcMs.Count > 16)
            {
                foreach (ulong staleGuid in LastMobTauntUtcMs.Where(kv => nowMs - kv.Value > 15000L).Select(kv => kv.Key).ToList())
                    LastMobTauntUtcMs.Remove(staleGuid);
            }

            var memberGuids = SquadContext.GetKnownMemberGuids();
            ulong myGuid = me.Guid;

            var looseMobs = ObjectManager.CachedUnits
                .Where(u => u != null && u.IsValid && !u.Dead && u.Combat && u.Attackable && !u.IsFriendly && !u.IsPlayer)
                .Where(u => u.CurrentTargetGuid != 0 && u.CurrentTargetGuid != myGuid &&
                            (u.IsTargetingMyPartyMember || memberGuids.Contains(u.CurrentTargetGuid)))
                .OrderByDescending(u => IsSquadOrPartyHealer(me, u.CurrentTargetGuid) ? 1 : 0)
                .ThenBy(u => u.Location.Distance(me.Location))
                .ToList();

            if (looseMobs.Count == 0)
                return false;

            int unthrottledAreaCount = looseMobs.Count(u =>
                u.Distance <= 9f &&
                CanAttemptTauntPeel(
                    _lastGlobalTauntUtcMs,
                    LastMobTauntUtcMs.TryGetValue(u.Guid, out long areaMobMs) ? areaMobMs : 0L,
                    nowMs));

            foreach (WoWUnit loose in looseMobs)
            {
                long lastMobMs = LastMobTauntUtcMs.TryGetValue(loose.Guid, out long recordedMs) ? recordedMs : 0L;
                if (!CanAttemptTauntPeel(_lastGlobalTauntUtcMs, lastMobMs, nowMs))
                    continue;

                bool attackingHealer = IsSquadOrPartyHealer(me, loose.CurrentTargetGuid);

                if (me.Class == WoWClass.Paladin)
                {
                    bool mobInHandRange = loose.Distance <= 30f && loose.InLineOfSpellSight;
                    if (mobInHandRange && SpellManager.CanCast("Hand of Reckoning", loose, true, false))
                    {
                        WoWUnit? prevTarget = me.CurrentTarget;
                        if (me.CurrentTargetGuid != loose.Guid)
                            loose.Target();
                        if (!me.IsFacing(loose))
                            loose.Face();
                        if (SpellManager.Cast("Hand of Reckoning", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        if (prevTarget != null && prevTarget.IsValid && !prevTarget.Dead && me.CurrentTargetGuid != prevTarget.Guid)
                            prevTarget.Target();
                    }

                    WoWUnit? paladinAlly = ObjectManager.GetObjectByGuid<WoWUnit>(loose.CurrentTargetGuid);
                    if (paladinAlly != null && paladinAlly.IsValid && !paladinAlly.Dead &&
                        SpellManager.CanCast("Righteous Defense", paladinAlly, false, false))
                    {
                        if (paladinAlly.Distance <= 38f && paladinAlly.InLineOfSpellSight)
                        {
                            if (SpellManager.CanCast("Righteous Defense", paladinAlly, true, false) &&
                                SpellManager.Cast("Righteous Defense", paladinAlly))
                            {
                                return RecordTauntCast(loose.Guid, nowMs);
                            }
                        }
                        else if ((!me.Combat || attackingHealer) && PeelApproach.TryMove(paladinAlly, "Peeling for " + paladinAlly.Name))
                        {
                            return true;
                        }
                    }

                    if (!mobInHandRange && (!me.Combat || attackingHealer) && PeelApproach.TryMove(loose, "Peeling " + loose.Name))
                        return true;
                    continue;
                }

                bool canTauntThisMob = me.Class switch
                {
                    WoWClass.Warrior =>
                        (unthrottledAreaCount >= 2 && SpellManager.CanCast("Challenging Shout", me, false, false)) ||
                        SpellManager.CanCast("Taunt", loose, false, false) ||
                        SpellManager.CanCast("Mocking Blow", loose, false, false),
                    WoWClass.Druid =>
                        (unthrottledAreaCount >= 2 && SpellManager.CanCast("Challenging Roar", me, false, false)) ||
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

                if (loose.Distance > 30f || !loose.InLineOfSpellSight)
                {
                    if ((!me.Combat || attackingHealer) && PeelApproach.TryMove(loose, "Peeling " + loose.Name))
                        return true;
                    continue;
                }

                if (me.CurrentTargetGuid != loose.Guid)
                    loose.Target();

                if (!me.IsFacing(loose))
                    loose.Face();

                switch (me.Class)
                {
                    case WoWClass.Warrior:
                        if (unthrottledAreaCount >= 2 && SpellManager.CanCast("Challenging Shout", me, false, false) &&
                            SpellManager.Cast("Challenging Shout", me))
                            return RecordAreaTauntCast(looseMobs, nowMs);
                        if (SpellManager.CanCast("Taunt", loose, true, false) && SpellManager.Cast("Taunt", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        if (loose.Distance <= 5.5f && SpellManager.CanCast("Mocking Blow", loose, true, false) &&
                            SpellManager.Cast("Mocking Blow", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        break;

                    case WoWClass.Druid:
                        if (unthrottledAreaCount >= 2 && SpellManager.CanCast("Challenging Roar", me, false, false) &&
                            SpellManager.Cast("Challenging Roar", me))
                            return RecordAreaTauntCast(looseMobs, nowMs);
                        if (SpellManager.CanCast("Growl", loose, true, false) && SpellManager.Cast("Growl", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        break;

                    case WoWClass.Paladin:
                        if (SpellManager.CanCast("Hand of Reckoning", loose, true, false) && SpellManager.Cast("Hand of Reckoning", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        WoWUnit? attackedAlly = ObjectManager.GetObjectByGuid<WoWUnit>(loose.CurrentTargetGuid);
                        if (attackedAlly != null && attackedAlly.IsValid && !attackedAlly.Dead &&
                            SpellManager.CanCast("Righteous Defense", attackedAlly, true, false) &&
                            SpellManager.Cast("Righteous Defense", attackedAlly))
                        {
                            return RecordTauntCast(loose.Guid, nowMs);
                        }
                        break;

                    case WoWClass.DeathKnight:
                        if (SpellManager.CanCast("Dark Command", loose, true, false) && SpellManager.Cast("Dark Command", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        if (SpellManager.CanCast("Death Grip", loose, true, false) && SpellManager.Cast("Death Grip", loose))
                            return RecordTauntCast(loose.Guid, nowMs);
                        break;
                }
            }
            return false;
        }

        private static long _lastDungeonPullCastUtcMs;
        private static ulong _lastDungeonPullTargetGuid;
        private static readonly SquadApproach PullApproach = new();

        // Pulling a mob that already stands next to its pack with a multi-target opener
        // (Avenger's Shield bounces to three targets) drags the whole pack in one go.
        internal const float PullPackNeighbourRadius = 10f;
        internal const float MaxPullCandidateScanRadius = 40f;
        internal const float MaxPullCandidateDetour = 12f;
        internal const int MaxPullCandidateCount = 8;
        internal const int MaxComfortablePackSize = 3;
        internal const int MaxBlockedPackSize = 5;
        internal const float PackPullManaFloor = 55f;
        internal const long MaxPackPullHoldMs = 15000L;

        /// <summary>
        /// Pull opener priority per class. An isolated target may be opened with the strongest
        /// tool, while a packed target must be opened with a single-target spell so the pull stays
        /// one pack and never chains the next group behind it.
        /// </summary>
        public static string[] GetPullSpellPriority(
            WoWClass playerClass, bool isolatedTarget, bool targetIsUndeadOrDemon = false)
        {
            switch (playerClass)
            {
                case WoWClass.Paladin:
                    // Exorcism is single-target but only lands on undead/demons: it must not be
                    // queued ahead of a usable tool for the usual humanoid trash, or the opener
                    // silently fails and the tank never pulls.
                    if (isolatedTarget)
                        return new[] { "Avenger's Shield", "Hand of Reckoning", "Exorcism" };
                    return targetIsUndeadOrDemon
                        ? new[] { "Hand of Reckoning", "Exorcism", "Avenger's Shield" }
                        : new[] { "Hand of Reckoning", "Avenger's Shield", "Exorcism" };
                case WoWClass.Warrior:
                    return new[] { "Heroic Throw", "Taunt", "Shoot", "Throw" };
                case WoWClass.Druid:
                    return new[] { "Faerie Fire (Feral)", "Growl", "Moonfire" };
                case WoWClass.DeathKnight:
                    return new[] { "Dark Command", "Icy Touch", "Death Coil" };
                default:
                    return Array.Empty<string>();
            }
        }

        internal static int CountPullPackSize(WoWUnit target)
        {
            if (target == null)
                return 0;

            int count = 1;
            foreach (var unit in ObjectManager.CachedUnits)
            {
                if (unit == null || !unit.IsValid || unit.Dead || unit.Guid == target.Guid)
                    continue;
                if (!unit.Attackable || unit.IsFriendly || unit.IsPlayer || unit.IsCritter || unit.IsNonCombatPet)
                    continue;
                if (unit.ControllingPlayer != null)
                    continue;
                if (unit.Location.Distance(target.Location) > PullPackNeighbourRadius)
                    continue;
                count++;
            }

            return count;
        }

        internal static bool IsBetterPullCandidate(int candidatePack, float candidateDistance, int currentPack, float currentDistance)
        {
            if (currentPack <= MaxComfortablePackSize)
                return false;
            if (candidatePack >= currentPack)
                return false;
            if (candidatePack > MaxComfortablePackSize + 1)
                return false;
            return candidateDistance <= currentDistance + MaxPullCandidateDetour;
        }

        internal static WoWUnit? SelectIsolatedPullTarget(WoWUnit? current, WoWPoint from, float scanRadius)
        {
            if (current == null || !current.IsValid || current.Dead)
                return current;

            int currentPack = CountPullPackSize(current);
            if (currentPack <= MaxComfortablePackSize)
                return current;

            WoWUnit? best = null;
            int bestPack = currentPack;
            float bestDistance = from.Distance(current.Location);

            int inspected = 0;
            foreach (var unit in ObjectManager.CachedUnits)
            {
                if (inspected >= MaxPullCandidateCount)
                    break;
                if (unit == null || !unit.IsValid || unit.Dead || unit.Guid == current.Guid)
                    continue;
                if (!unit.Attackable || unit.IsFriendly || unit.IsPlayer || unit.IsCritter || unit.IsNonCombatPet)
                    continue;
                if (unit.ControllingPlayer != null || unit.Combat)
                    continue;

                float distance = from.Distance(unit.Location);
                if (distance > scanRadius)
                    continue;

                inspected++;
                int pack = CountPullPackSize(unit);
                if (!IsBetterPullCandidate(pack, distance, bestPack, bestDistance))
                    continue;

                best = unit;
                bestPack = pack;
                bestDistance = distance;
            }

            return best ?? current;
        }

        internal static bool IsPackPullTooRisky(int packSize, float lowestPartyManaPercent, long holdElapsedMs)
        {
            if (packSize <= MaxComfortablePackSize)
                return false;
            if (packSize > MaxBlockedPackSize)
                return false;
            if (holdElapsedMs >= MaxPackPullHoldMs)
                return false;
            return lowestPartyManaPercent < PackPullManaFloor;
        }

        private static bool TargetHasCloseNeighbours(WoWUnit target)
        {
            foreach (var unit in ObjectManager.CachedUnits)
            {
                if (unit == null || !unit.IsValid || unit.Dead || unit.Guid == target.Guid)
                    continue;
                if (!unit.Attackable || unit.IsFriendly || unit.IsPlayer)
                    continue;
                if (unit.Location.Distance(target.Location) > PullPackNeighbourRadius)
                    continue;
                return true;
            }
            return false;
        }

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

            string[] pullSpells = GetPullSpellPriority(
                me.Class,
                isolatedTarget: !TargetHasCloseNeighbours(target),
                targetIsUndeadOrDemon: target.IsUndead || target.IsDemon);

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
