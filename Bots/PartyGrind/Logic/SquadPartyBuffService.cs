using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Handles cross-party buffs (Fortitude/Spirit, Arcane Intellect, Mark/Thorns,
    /// deterministic multi-Paladin Blessings/Auras, Warrior shouts) and Coordinator
    /// Raid Target Markers (Skull/Cross/Square/Star).
    /// </summary>
    public static class SquadPartyBuffService
    {
        private const long RecentCastThrottleMs = 4000;
        private static long _lastMarkerCheckUtcMs;
        private static readonly Dictionary<(ulong TargetGuid, string SpellName), long> _recentBuffCasts = new();

        internal static bool IsBuffCastThrottled(ulong targetGuid, string spellName, long nowMs)
        {
            if (_recentBuffCasts.TryGetValue((targetGuid, spellName), out long untilMs))
            {
                if (nowMs < untilMs)
                    return true;
                _recentBuffCasts.Remove((targetGuid, spellName));
            }
            return false;
        }

        internal static void RecordBuffCast(ulong targetGuid, string spellName, long nowMs)
        {
            if (_recentBuffCasts.Count > 64)
                _recentBuffCasts.Clear();
            _recentBuffCasts[(targetGuid, spellName)] = nowMs + RecentCastThrottleMs;
        }

        internal static void ResetBuffCastThrottles()
        {
            _recentBuffCasts.Clear();
        }

        private static bool TryCastPartyBuff(string spellName, WoWUnit target, bool checkRange)
        {
            long now = Environment.TickCount64;
            if (IsBuffCastThrottled(target.Guid, spellName, now))
                return false;
            if (!SpellManager.CanCast(spellName, target, checkRange, false))
                return false;
            if (!SpellManager.Cast(spellName, target))
                return false;
            RecordBuffCast(target.Guid, spellName, now);
            return true;
        }

        public static bool RunOutOfCombatPartyBuffTick(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !settings.EnableCrossPartyBuffs || !me.IsAlive || me.Combat || me.Mounted)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
                return true;

            if (me.PowerType == WoWPowerType.Mana && me.PowerPercent < 40.0)
                return false;

            var allies = CollectBuffableAllies(me, leader);
            if (allies.Count == 0)
                return false;

            switch (me.Class)
            {
                case WoWClass.Mage:
                    return BuffMage(me, allies);
                case WoWClass.Priest:
                    return BuffPriest(me, allies);
                case WoWClass.Druid:
                    return BuffDruid(me, allies);
                case WoWClass.Paladin:
                    return BuffPaladin(me, allies, leader);
                case WoWClass.Warrior:
                    return BuffWarrior(me, allies);
            }

            return false;
        }

        /// <summary>
        /// Throttled Coordinator Raid Target Marker assignment:
        /// Skull (8) on FocusTarget, Cross (7) on SecondaryTarget,
        /// Square (6) on TankLeader, Star (1) on Healer.
        /// </summary>
        public static void UpdateRaidTargetMarkers(SquadSettings settings, SquadLeaderState leader)
        {
            if (!settings.SetRaidTargetMarkers || !settings.IsCoordinator || StyxWoW.Me == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if ((now - _lastMarkerCheckUtcMs) < 500)
                return;
            _lastMarkerCheckUtcMs = now;

            // Mark Tank (Square=6) and Healer (Star=1) via player/partyN unit IDs
            var healer = leader.Members?.FirstOrDefault(m => m.Role == SquadRole.Healer);
            EnsurePartyUnitMarker(leader.LeaderName, 6);
            if (healer != null)
                EnsurePartyUnitMarker(healer.CharacterName, 1);

            // Mark current combat focus target (Skull = 8) when targeted
            if (leader.FocusTargetGuid != 0 && StyxWoW.Me.CurrentTargetGuid == leader.FocusTargetGuid)
            {
                Lua.DoString("if UnitExists('target') and GetRaidTargetIndex('target') ~= 8 then SetRaidTarget('target', 8) end");
            }
            else if (leader.SecondaryTargetGuid != 0 && leader.SecondaryTargetGuid != leader.FocusTargetGuid &&
                StyxWoW.Me.CurrentTargetGuid == leader.SecondaryTargetGuid)
            {
                Lua.DoString("if UnitExists('target') and GetRaidTargetIndex('target') ~= 7 then SetRaidTarget('target', 7) end");
            }
        }

        private static void EnsurePartyUnitMarker(string? charName, int icon)
        {
            if (string.IsNullOrWhiteSpace(charName))
                return;

            string escaped = Lua.Escape(charName.Trim());
            Lua.DoString(
                $"for _,u in ipairs({{'player','party1','party2','party3','party4'}}) do " +
                $"if UnitExists(u) and UnitName(u) == '{escaped}' then " +
                $"if GetRaidTargetIndex(u) ~= {icon} then SetRaidTarget(u, {icon}) end break end end");
        }

        private static List<WoWUnit> CollectBuffableAllies(LocalPlayer me, SquadLeaderState? leader)
        {
            var list = new List<WoWUnit> { me };
            var seen = new HashSet<ulong> { me.Guid };

            foreach (ulong guid in SquadContext.GetKnownMemberGuids())
            {
                if (!seen.Add(guid))
                    continue;

                WoWUnit? unit = ObjectManager.GetObjectByGuid<WoWUnit>(guid);
                if (unit != null && unit.IsValid && unit.IsAlive && !unit.Dead && !unit.IsGhost &&
                    unit.Location.Distance(me.Location) <= 30f)
                {
                    list.Add(unit);
                }
            }

            return list;
        }

        private static bool BuffMage(LocalPlayer me, List<WoWUnit> allies)
        {
            var manaAllies = allies.Where(u => u.PowerType == WoWPowerType.Mana || u.Class == WoWClass.Druid).ToList();
            int missing = manaAllies.Count(u =>
                !u.HasAura("Arcane Intellect") &&
                !u.HasAura("Arcane Brilliance") &&
                !u.HasAura("Dalaran Intellect") &&
                !u.HasAura("Fel Intelligence"));

            if (missing == 0)
                return false;

            if (missing >= 2 && TryCastPartyBuff("Arcane Brilliance", me, false))
                return true;

            var target = manaAllies.FirstOrDefault(u =>
                !u.HasAura("Arcane Intellect") &&
                !u.HasAura("Arcane Brilliance") &&
                !u.HasAura("Dalaran Intellect") &&
                !u.HasAura("Fel Intelligence") && SpellManager.CanCast("Arcane Intellect", u, true, false));

            if (target != null && TryCastPartyBuff("Arcane Intellect", target, true))
                return true;

            return false;
        }

        private static bool BuffPriest(LocalPlayer me, List<WoWUnit> allies)
        {
            var missingFort = allies.Where(u =>
                !u.HasAura("Power Word: Fortitude") &&
                !u.HasAura("Prayer of Fortitude")).ToList();

            if (missingFort.Count >= 2 && TryCastPartyBuff("Prayer of Fortitude", me, false))
                return true;

            var missingFortTarget = missingFort.FirstOrDefault(u => SpellManager.CanCast("Power Word: Fortitude", u, true, false));
            if (missingFortTarget != null && TryCastPartyBuff("Power Word: Fortitude", missingFortTarget, true))
                return true;

            var missingSpirit = allies.Where(u =>
                (u.PowerType == WoWPowerType.Mana || u.Class == WoWClass.Druid) &&
                !u.HasAura("Divine Spirit") &&
                !u.HasAura("Prayer of Spirit") &&
                !u.HasAura("Fel Intelligence")).ToList();

            if (missingSpirit.Count >= 2 && TryCastPartyBuff("Prayer of Spirit", me, false))
                return true;

            var missingSpiritTarget = missingSpirit.FirstOrDefault(u => SpellManager.CanCast("Divine Spirit", u, true, false));
            if (missingSpiritTarget != null && TryCastPartyBuff("Divine Spirit", missingSpiritTarget, true))
                return true;

            return false;
        }

        private static bool BuffDruid(LocalPlayer me, List<WoWUnit> allies)
        {
            var missingMotw = allies.Where(u =>
                !u.HasAura("Mark of the Wild") &&
                !u.HasAura("Gift of the Wild")).ToList();

            if (missingMotw.Count >= 2 && TryCastPartyBuff("Gift of the Wild", me, false))
                return true;

            var missingMotwTarget = missingMotw.FirstOrDefault(u => SpellManager.CanCast("Mark of the Wild", u, true, false));
            if (missingMotwTarget != null && TryCastPartyBuff("Mark of the Wild", missingMotwTarget, true))
                return true;

            var tankOrMelee = allies.FirstOrDefault(u =>
                (SquadContext.IsMemberRole(u.Guid, SquadRole.TankLeader) || SquadContext.IsMemberRole(u.Guid, SquadRole.DpsMelee)) &&
                !u.HasAura("Thorns") && SpellManager.CanCast("Thorns", u, true, false));
            if (tankOrMelee != null && TryCastPartyBuff("Thorns", tankOrMelee, true))
                return true;

            return false;
        }

        private static bool BuffPaladin(LocalPlayer me, List<WoWUnit> allies, SquadLeaderState? leader)
        {
            // Determine Paladin index among party Paladins so multiple Paladins pick distinct auras & blessings.
            var paladinNames = (leader?.Members ?? new List<SquadMemberSummary>())
                .Where(m => m.Class == WoWClass.Paladin && m.IsConnected && !m.IsDead && !m.IsGhost && m.MapId == me.MapId)
                .Select(m => m.CharacterName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int paladinIndex = Math.Max(0, paladinNames.FindIndex(n => string.Equals(n, me.Name, StringComparison.OrdinalIgnoreCase)));

            string[] preferredAuras = paladinIndex switch
            {
                0 => new[] { "Devotion Aura", "Retribution Aura", "Concentration Aura" },
                1 => new[] { "Retribution Aura", "Concentration Aura", "Devotion Aura" },
                _ => new[] { "Concentration Aura", "Shadow Resistance Aura", "Fire Resistance Aura" }
            };

            foreach (string aura in preferredAuras)
            {
                if (!me.HasAura(aura) && TryCastPartyBuff(aura, me, false))
                {
                    SquadPartySyncService.LogAction("Buffing aura " + aura);
                    return true;
                }
                if (me.GetAllAuras().Any(a => a.Name == aura && a.CreatorGuid == me.Guid))
                    break;
            }

            foreach (var ally in allies)
            {
                var role = SquadContext.GetMemberByGuid(ally.Guid)?.Role ??
                           (ally.Guid == me.Guid ? SquadContext.ActiveRole : SquadRole.DpsMelee);

                string desiredBlessing = SelectBlessingForRole(role, ally.Class, paladinIndex);
                if (role == SquadRole.TankLeader && paladinNames.Count > 1)
                {
                    var capabilities = paladinNames.Select(name => (IReadOnlyList<string>)(
                        string.Equals(name, me.Name, StringComparison.OrdinalIgnoreCase)
                            ? new[] { "Blessing of Sanctuary", "Blessing of Kings", "Blessing of Might", "Blessing of Wisdom" }.Where(SpellManager.HasSpell).ToList()
                            : leader!.Members.First(m => m.CharacterName == name).KnownBlessings)).ToList();
                    desiredBlessing = SelectTankBlessing(paladinIndex, capabilities);
                }
                if (!string.IsNullOrEmpty(desiredBlessing) &&
                    !ally.HasAura(desiredBlessing) &&
                    !ally.HasAura("Greater " + desiredBlessing) &&
                    TryCastPartyBuff(desiredBlessing, ally, true))
                {
                    SquadPartySyncService.LogAction("Buffing " + ally.Name + ": " + desiredBlessing);
                    return true;
                }
            }

            return false;
        }

        public static string SelectTankBlessing(int index, IReadOnlyList<IReadOnlyList<string>> known)
        {
            if (known == null || known.Count == 0 || index < 0 || index >= known.Count)
                return string.Empty;

            // Choose an assignment for every participating Paladin at once: maximize
            // the number of distinct blessings first, then honor each paladin's
            // preference order (deterministically, in paladin index order).
            var preferences = Enumerable.Range(0, known.Count)
                .Select(i => (i == 0
                    ? new[] { "Blessing of Sanctuary", "Blessing of Might", "Blessing of Kings", "Blessing of Wisdom" }
                    : new[] { "Blessing of Kings", "Blessing of Might", "Blessing of Sanctuary", "Blessing of Wisdom" })
                    .Where(s => known[i].Contains(s)).ToArray())
                .ToArray();

            var best = new string[known.Count];
            var current = new string[known.Count];
            int bestDistinct = -1, bestPenalty = int.MaxValue;

            void Search(int i, HashSet<string> used, int penalty)
            {
                if (i == preferences.Length)
                {
                    int distinct = used.Count;
                    if (distinct > bestDistinct || (distinct == bestDistinct && penalty < bestPenalty))
                    {
                        bestDistinct = distinct;
                        bestPenalty = penalty;
                        Array.Copy(current, best, current.Length);
                    }
                    return;
                }

                for (int rank = 0; rank <= preferences[i].Length; rank++)
                {
                    string choice = rank < preferences[i].Length ? preferences[i][rank] : string.Empty;
                    bool added = !string.IsNullOrEmpty(choice) && used.Add(choice);
                    current[i] = choice;
                    Search(i + 1, used, penalty + (string.IsNullOrEmpty(choice) ? 100 : rank));
                    if (added) used.Remove(choice);
                }
            }

            Search(0, new HashSet<string>(), 0);
            return best[index] ?? string.Empty;
        }

        public static string SelectBlessingForRole(SquadRole role, WoWClass targetClass, int paladinIndex)
        {
            if (paladinIndex == 0)
            {
                if (role == SquadRole.TankLeader)
                    return new[] { "Blessing of Sanctuary", "Blessing of Kings", "Blessing of Might" }.FirstOrDefault(SpellManager.HasSpell) ?? string.Empty;
                if (role == SquadRole.Healer || targetClass is WoWClass.Mage or WoWClass.Priest or WoWClass.Warlock)
                    return SpellManager.HasSpell("Blessing of Wisdom") ? "Blessing of Wisdom" : "Blessing of Kings";
                return SpellManager.HasSpell("Blessing of Might") ? "Blessing of Might" : "Blessing of Kings";
            }

            if (role == SquadRole.TankLeader || role == SquadRole.DpsMelee || role == SquadRole.DpsRanged)
                return SpellManager.HasSpell("Blessing of Kings") ? "Blessing of Kings" : "Blessing of Might";
            return SpellManager.HasSpell("Blessing of Kings") ? "Blessing of Kings" : "Blessing of Wisdom";
        }

        private static bool BuffWarrior(LocalPlayer me, List<WoWUnit> allies)
        {
            if (!me.HasAura("Battle Shout") && !me.HasAura("Commanding Shout") &&
                !me.HasAura("Blessing of Might") && !me.HasAura("Greater Blessing of Might"))
            {
                if (TryCastPartyBuff("Battle Shout", me, false))
                    return true;
                if (TryCastPartyBuff("Commanding Shout", me, false))
                    return true;
            }
            return false;
        }
    }
}
