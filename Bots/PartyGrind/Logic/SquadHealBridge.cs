using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.CommonBot;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Lightweight candidate representation used for priority heal selection and unit tests.
    /// </summary>
    public sealed class SquadHealCandidate
    {
        public ulong Guid { get; init; }
        public string Name { get; init; } = string.Empty;
        public double HealthPercent { get; init; }
        public double Distance { get; init; }
        public bool IsTankLeader { get; init; }
        public bool IsSelf { get; init; }
    }

    /// <summary>
    /// Two-stage healer & hybrid off-heal bridge that guarantees reliable party healing
    /// from level 1–80 even when Singular lowbie/open-world HealBehavior only self-heals.
    /// </summary>
    public static class SquadHealBridge
    {
        private static readonly SquadApproach HealApproach = new();
        private static readonly Dictionary<ulong, (int Failures, long RetryUntil)> _resApproaches = new();
        private static Composite? _activeHealBehavior;

        /// <summary>Stops a retained routine when healing is no longer being ticked.</summary>
        public static void StopActiveHealBehavior()
        {
            if (_activeHealBehavior == null)
                return;
            try { _activeHealBehavior.Stop(null); } catch { }
            _activeHealBehavior = null;
        }
        private static readonly string[] RangedAssistSpells =
        {
            "Smite", "Holy Fire", "Shadow Word: Pain",
            "Lightning Bolt", "Flame Shock", "Earth Shock",
            "Wrath", "Moonfire", "Starfire",
            "Exorcism", "Judgement of Light", "Judgement of Wisdom"
        };

        /// <summary>
        /// Selects the highest-priority heal target. Applies a -15% effective HP priority bonus
        /// for the TankLeader (unless emergency self-preservation puts self below critical HP).
        /// </summary>
        public static SquadHealCandidate? SelectPriorityHealTarget(
            IEnumerable<SquadHealCandidate> candidates,
            double healthThresholdPct,
            bool prioritizeSelfCritical = true)
        {
            if (candidates == null)
                return null;

            var eligible = candidates
                .Where(c => c.Distance <= 45.0 && c.HealthPercent < healthThresholdPct)
                .ToList();

            if (eligible.Count == 0)
                return null;

            if (prioritizeSelfCritical)
            {
                var selfCrit = eligible.FirstOrDefault(c => c.IsSelf && c.HealthPercent < 35.0);
                if (selfCrit != null)
                    return selfCrit;
            }

            return eligible
                .OrderBy(c => c.HealthPercent - (c.IsTankLeader ? 15.0 : 0.0))
                .ThenBy(c => c.HealthPercent)
                .ThenBy(c => c.Distance)
                .First();
        }

        public static int CountInjuredAllies(IEnumerable<SquadHealCandidate> candidates, double healthThresholdPct)
        {
            if (candidates == null)
                return 0;
            return candidates.Count(c => c.Distance <= 38.0 && c.HealthPercent < healthThresholdPct);
        }

        /// <summary>
        /// Runs one healing tick. Returns true if healing/positioning/resurrection work was handled this tick.
        /// </summary>
        public static bool RunHealerTick(bool emergencyOnly, SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
                return true;

            var candidates = CollectLiveHealCandidates(leader);
            if (candidates.Count == 0 || (!emergencyOnly && me.Level < 10))
                StopActiveHealBehavior();
            double threshold = emergencyOnly ? 55.0 : 92.0;

            // Check if a HybridOffHeal should step in aggressively because main healer is dead or < 20% mana.
            if (emergencyOnly && leader?.Members != null)
            {
                var mainHealer = leader.Members.FirstOrDefault(m => m.Role == SquadRole.Healer);
                if (mainHealer != null && (mainHealer.IsDead || mainHealer.IsGhost || !mainHealer.IsConnected || mainHealer.PowerPercent < 20f))
                {
                    threshold = 80.0;
                }
            }

            var remaining = candidates.ToList();
            while (remaining.Count > 0)
            {
                var priorityCandidate = SelectPriorityHealTarget(
                    remaining,
                    threshold,
                    prioritizeSelfCritical: settings.UseEmergencySelfHealBeforeParty);

                if (priorityCandidate == null)
                    return false;

                remaining.Remove(priorityCandidate);
                WoWUnit? targetUnit = priorityCandidate.IsSelf
                    ? me
                    : ObjectManager.GetObjectByGuid<WoWUnit>(priorityCandidate.Guid);

                if (targetUnit == null || !targetUnit.IsValid || targetUnit.Dead)
                    continue;

                // Stage 1: Try Routine's HealBehavior when level >= 10 and not in Hybrid emergency-only mode.
                if (!emergencyOnly && me.Level >= 10 && targetUnit.Distance <= 35f && targetUnit.InLineOfSpellSight)
                {
                    var healBehavior = RoutineManager.Current?.HealBehavior;
                    if (healBehavior != null)
                    {
                        try
                        {
                            HealTargeting.Instance.Pulse();
                            if (!ReferenceEquals(_activeHealBehavior, healBehavior))
                            {
                                _activeHealBehavior?.Stop(null);
                                _activeHealBehavior = null;
                                healBehavior.Start(null);
                                _activeHealBehavior = healBehavior;
                            }
                            RunStatus status = healBehavior.Tick(null);
                            // Running keeps the composite active and handled this tick;
                            // a cast also wins. Only Failure falls through to explicit healing.
                            if (status == RunStatus.Running || me.IsCasting || me.ChanneledCastingSpellId != 0)
                                return true;
                            healBehavior.Stop(null);
                            _activeHealBehavior = null;
                            if (status == RunStatus.Success)
                                return true;
                        }
                        catch (Exception ex)
                        {
                            // Start may have succeeded before Tick threw — clean up either way.
                            try { healBehavior.Stop(null); } catch { }
                            _activeHealBehavior = null;
                            Logging.WriteDebug("[PartyGrind Heal] Routine HealBehavior fallback: {0}", ex.Message);
                        }
                    }
                }

                // Stage 2: Explicit class-aware party heal fallback (Level 1-80, open world & instances).
                if (targetUnit.Distance > 35f || !targetUnit.InLineOfSpellSight)
                {
                    if (HealApproach.TryMove(targetUnit, "Moving to heal " + targetUnit.Name)) return true;
                    continue;
                }

                int injured78 = CountInjuredAllies(candidates, 78.0);
                int injured82 = CountInjuredAllies(candidates, 82.0);

                string? spellName = SelectHealSpellForClass(me, targetUnit, injured78, injured82, emergencyOnly);
                if (string.IsNullOrEmpty(spellName))
                    continue;

                if (SpellManager.Spells.TryGetValue(spellName, out WoWSpell? spellInfo) && spellInfo.CastTime > 0 && me.IsMoving)
                {
                    WoWMovement.MoveStop();
                }

                if (SpellManager.CanCast(spellName, targetUnit, true, false))
                {
                    if (SpellManager.Cast(spellName, targetUnit)) return true;
                }

            }
            return false;
        }

        /// <summary>
        /// Out-of-combat resurrection behavior for Healer / HybridOffHeal / res-capable classes.
        /// </summary>
        public static string? GetResurrectionSpell(WoWClass wowClass) => wowClass switch
            {
                WoWClass.Priest => "Resurrection",
                WoWClass.Paladin => "Redemption",
                WoWClass.Druid => "Revive",
                WoWClass.Shaman => "Ancestral Spirit",
                _ => null
            };


        public static bool RunPostCombatResurrectionTick()
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || me.Combat || me.Mounted)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
                return true;

            string? resSpell = GetResurrectionSpell(me.Class);

            if (string.IsNullOrEmpty(resSpell) || !SpellManager.HasSpell(resSpell))
                return false;

            var memberGuids = SquadContext.GetKnownMemberGuids();
            long now = Environment.TickCount64;
            foreach (ulong stale in _resApproaches.Keys.Where(g => !memberGuids.Contains(g)).ToList())
                _resApproaches.Remove(stale);
            // A new death episode starts with a clean slate.
            foreach (var player in ObjectManager.GetObjectsOfType<WoWPlayer>(false, false))
                if (player != null && _resApproaches.ContainsKey(player.Guid) && (!player.Dead || player.IsGhost))
                    _resApproaches.Remove(player.Guid);

            var deadAllies = ObjectManager.GetObjectsOfType<WoWPlayer>(false, false)
                .Where(p => p != null && p.IsValid && p.Dead && !p.IsGhost &&
                            memberGuids.Contains(p.Guid) && p.Location.Distance(me.Location) <= 45f)
                .OrderBy(p => p.Location.Distance(me.Location))
                .ToList();

            foreach (var deadAlly in deadAllies)
            {
                _resApproaches.TryGetValue(deadAlly.Guid, out var attempt);
                if (deadAlly.Distance > 28f || !deadAlly.InLineOfSpellSight)
                {
                    // Exhausted approaches suppress further movement, but the corpse
                    // remains eligible for a direct cast if it comes into range/LoS.
                    if (attempt.Failures >= 3 || now < attempt.RetryUntil)
                        continue;
                    var result = Navigator.MoveTo(deadAlly.Location, "Moving to resurrect " + deadAlly.Name);
                    // Path failures and arrival without line of sight count against this
                    // corpse; try the next one instead of retrying it forever.
                    if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.ReachedDestination)
                    {
                        int failures = _resApproaches.TryGetValue(deadAlly.Guid, out var previous) ? previous.Failures + 1 : 1;
                        _resApproaches[deadAlly.Guid] = (failures, now + 15000);
                        Navigator.Clear();
                        continue;
                    }
                    return true;
                }

                if (me.IsMoving)
                    WoWMovement.MoveStop();

                if (SpellManager.CanCast(resSpell, deadAlly, true, false))
                {
                    Logging.Write("[PartyGrind] Resurrecting {0} with {1}", deadAlly.Name, resSpell);
                    // A failed cast is not success: drop this corpse only when the cast
                    // actually lands, otherwise try the next eligible corpse.
                    if (SpellManager.Cast(resSpell, deadAlly))
                    {
                        _resApproaches.Remove(deadAlly.Guid);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Safe ranged assist DPS for Healer when all squadmates are healthy and mana >= threshold.
        /// Never walks into melee range.
        /// </summary>
        public static bool TryHealerAssistDps(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || !settings.HealerAssistDps || leader == null)
                return false;

            if (me.PowerType == WoWPowerType.Mana && me.PowerPercent < settings.HealerDpsAboveManaPct)
                return false;

            var candidates = CollectLiveHealCandidates(leader);
            if (candidates.Any(c => c.HealthPercent < 88.0))
                return false;

            ulong targetGuid = leader.FocusTargetGuid != 0 ? leader.FocusTargetGuid : leader.SecondaryTargetGuid;
            if (targetGuid == 0)
                return false;

            WoWUnit? target = ObjectManager.GetObjectByGuid<WoWUnit>(targetGuid);
            if (target == null || !target.IsValid || target.Dead || !target.Attackable || target.IsFriendly)
                return false;

            if (target.Distance > 30f || !target.InLineOfSpellSight)
                return false;

            if (me.CurrentTargetGuid != target.Guid)
                target.Target();

            if (!me.IsFacing(target))
                target.Face();

            foreach (string spell in RangedAssistSpells)
            {
                if (SpellManager.HasSpell(spell) && SpellManager.CanCast(spell, target, true, false))
                {
                    if (SpellManager.Spells.TryGetValue(spell, out WoWSpell? info) && info.CastTime > 0 && me.IsMoving)
                        WoWMovement.MoveStop();
                    return SpellManager.Cast(spell, target);
                }
            }

            return false;
        }

        private static List<SquadHealCandidate> CollectLiveHealCandidates(SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            var result = new List<SquadHealCandidate>();
            if (me == null)
                return result;

            var seenGuids = new HashSet<ulong>();
            ulong tankGuid = leader?.LeaderGuid ?? 0UL;

            result.Add(new SquadHealCandidate
            {
                Guid = me.Guid,
                Name = me.Name ?? "Me",
                HealthPercent = me.HealthPercent,
                Distance = 0,
                IsTankLeader = me.Guid == tankGuid || SquadContext.ActiveRole == SquadRole.TankLeader,
                IsSelf = true
            });
            seenGuids.Add(me.Guid);

            foreach (ulong guid in SquadContext.GetKnownMemberGuids())
            {
                if (!seenGuids.Add(guid))
                    continue;

                WoWUnit? unit = ObjectManager.GetObjectByGuid<WoWUnit>(guid);
                if (unit == null || !unit.IsValid || unit.Dead || unit.IsGhost)
                    continue;

                double dist = unit.Location.Distance(me.Location);
                if (dist > 45.0)
                    continue;

                bool isTank = guid == tankGuid || SquadContext.IsMemberRole(guid, SquadRole.TankLeader);
                result.Add(new SquadHealCandidate
                {
                    Guid = guid,
                    Name = unit.Name ?? string.Empty,
                    HealthPercent = unit.HealthPercent,
                    Distance = dist,
                    IsTankLeader = isTank,
                    IsSelf = false
                });
            }

            return result;
        }

        private static string? SelectHealSpellForClass(
            LocalPlayer me,
            WoWUnit target,
            int injured78Count,
            int injured82Count,
            bool emergencyOnly)
        {
            double hp = target.HealthPercent;
            bool isTank = SquadContext.IsMemberRole(target.Guid, SquadRole.TankLeader);

            switch (me.Class)
            {
                case WoWClass.Priest:
                    if (!emergencyOnly && injured78Count >= 3 && SpellManager.CanCast("Prayer of Healing", target, true, false))
                        return "Prayer of Healing";
                    if (!emergencyOnly && injured78Count >= 2 && SpellManager.CanCast("Circle of Healing", target, true, false))
                        return "Circle of Healing";
                    if (hp < 85.0 && !target.HasAura("Weakened Soul") && !target.HasAura("Power Word: Shield") &&
                        SpellManager.CanCast("Power Word: Shield", target, true, false))
                        return "Power Word: Shield";
                    if (!emergencyOnly && hp < 90.0 && !HasMyAura(me.Guid, target, "Renew") &&
                        SpellManager.CanCast("Renew", target, true, false))
                        return "Renew";
                    if (hp < 60.0 && SpellManager.CanCast("Flash Heal", target, true, false))
                        return "Flash Heal";
                    if (SpellManager.CanCast("Greater Heal", target, true, false))
                        return "Greater Heal";
                    if (SpellManager.CanCast("Heal", target, true, false))
                        return "Heal";
                    if (SpellManager.CanCast("Lesser Heal", target, true, false))
                        return "Lesser Heal";
                    break;

                case WoWClass.Paladin:
                    if (!emergencyOnly && isTank && !HasMyAura(me.Guid, target, "Beacon of Light") &&
                        SpellManager.CanCast("Beacon of Light", target, true, false))
                        return "Beacon of Light";
                    if (!emergencyOnly && isTank && !HasMyAura(me.Guid, target, "Sacred Shield") &&
                        SpellManager.CanCast("Sacred Shield", target, true, false))
                        return "Sacred Shield";
                    if (hp < 70.0 && SpellManager.CanCast("Holy Shock", target, true, false))
                        return "Holy Shock";
                    if (hp < 80.0 && SpellManager.CanCast("Flash of Light", target, true, false))
                        return "Flash of Light";
                    if (SpellManager.CanCast("Holy Light", target, true, false))
                        return "Holy Light";
                    break;

                case WoWClass.Druid:
                    if (!emergencyOnly && injured78Count >= 2 && SpellManager.CanCast("Wild Growth", target, true, false))
                        return "Wild Growth";
                    if (!emergencyOnly && isTank && hp < 95.0 && !HasMyAura(me.Guid, target, "Lifebloom") &&
                        SpellManager.CanCast("Lifebloom", target, true, false))
                        return "Lifebloom";
                    if (!emergencyOnly && hp < 90.0 && !HasMyAura(me.Guid, target, "Rejuvenation") &&
                        SpellManager.CanCast("Rejuvenation", target, true, false))
                        return "Rejuvenation";
                    if (hp < 75.0 && !HasMyAura(me.Guid, target, "Regrowth") &&
                        SpellManager.CanCast("Regrowth", target, true, false))
                        return "Regrowth";
                    if (hp < 70.0 && SpellManager.CanCast("Nourish", target, true, false))
                        return "Nourish";
                    if (SpellManager.CanCast("Healing Touch", target, true, false))
                        return "Healing Touch";
                    break;

                case WoWClass.Shaman:
                    if (!emergencyOnly && isTank && !HasMyAura(me.Guid, target, "Earth Shield") &&
                        SpellManager.CanCast("Earth Shield", target, true, false))
                        return "Earth Shield";
                    if (!emergencyOnly && injured82Count >= 2 && SpellManager.CanCast("Chain Heal", target, true, false))
                        return "Chain Heal";
                    if (hp < 85.0 && SpellManager.CanCast("Riptide", target, true, false))
                        return "Riptide";
                    if (hp < 65.0 && SpellManager.CanCast("Lesser Healing Wave", target, true, false))
                        return "Lesser Healing Wave";
                    if (SpellManager.CanCast("Healing Wave", target, true, false))
                        return "Healing Wave";
                    break;
            }

            return null;
        }

        private static bool HasMyAura(ulong myGuid, WoWUnit unit, string auraName)
        {
            foreach (WoWAura aura in unit.GetAllAuras())
            {
                if (aura.CreatorGuid == myGuid &&
                    string.Equals(aura.Name, auraName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
