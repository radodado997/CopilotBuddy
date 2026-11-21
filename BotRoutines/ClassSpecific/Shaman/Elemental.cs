using System;
using System.Linq;
using System.Globalization;
using Action = TreeSharp.Action;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;

using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

using TreeSharp;

namespace Singular.ClassSpecific.Shaman
{
    public class Elemental
    {
        // ---- Combat state, refreshed once per tree tick by ObserveTarget() ----
        // A tiny Lua combat-log frame records OUR real hits. It provides:
        //  * measured Earth Shock / Lightning Bolt damage (includes resists, talents),
        //  * our own damage on the target, so time-to-die counts only OTHER sources,
        //  * the overkill of the last Earth Shock, used to self-calibrate the execute.
        private static ulong _sampledTarget;
        private static ulong _executeTarget;
        private static DateTime _sampleTime;
        private static double _sampleHealth;
        private static double _sampleOwnDamage;
        private static double _externalDps;
        private static int _externalSamples;
        private static DateTime _lastExternalDamage;
        private static DateTime _lastLuaInstall;
        private static double _ownDamageOnTarget;
        private static double _earthShockMinHit;      // lowest of last 10 non-crit hits
        private static double _lightningBoltMinHit;
        private static double _earthShockHits;        // monotonic counter of Earth Shock hits
        private static double _earthShockLastOverkill;
        private static bool _lastShockOnTarget;
        private static double _executeSafety = 1.0;   // 0.5..1.0, learned from outcomes
        private static bool _executePending;
        private static double _executeHitCounter;
        private static DateTime _executeTime;
        private static DateTime _spellPowerTime;
        private static double _naturePower;
        private static double _firePower;

        // 8042 = Earth Shock, 403 = Lightning Bolt (rank 1). GetSpellInfo returns the
        // localized name shared by all ranks, so this works on every client language.
        private const string CombatLogTracker = @"
if not SingularEle then
  SingularEle = { d = {}, es = {}, lb = {}, ne = 0, nl = 0, nes = 0, over = -1 }
  local t = SingularEle
  local esName, lbName = GetSpellInfo(8042), GetSpellInfo(403)
  t.f = CreateFrame('Frame')
  t.f:RegisterEvent('COMBAT_LOG_EVENT_UNFILTERED')
  t.f:RegisterEvent('PLAYER_REGEN_ENABLED')
  t.f:SetScript('OnEvent', function(self, event, ts, sub, sg, sn, sf, dg, dn, df, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18)
    if event == 'PLAYER_REGEN_ENABLED' then wipe(t.d) return end
    if sg ~= UnitGUID('player') then return end
    local amount, crit
    if sub == 'SWING_DAMAGE' then amount, crit = a9, a15
    elseif sub == 'SPELL_DAMAGE' or sub == 'SPELL_PERIODIC_DAMAGE' or sub == 'RANGE_DAMAGE' then amount, crit = a12, a18
    else return end
    if not amount then return end
    t.d[dg] = (t.d[dg] or 0) + amount
    if sub == 'SPELL_DAMAGE' then
      if a10 == esName then t.nes = t.nes + 1; t.over = a13 or -1; t.sg = dg end
      if not crit then
        if a10 == esName then t.ne = t.ne % 10 + 1; t.es[t.ne] = amount
        elseif a10 == lbName then t.nl = t.nl % 10 + 1; t.lb[t.nl] = amount end
      end
    end
  end)
end";

        private const string CombatLogQuery =
            "local t=SingularEle if not t then return '0|0|0|0|-1|0' end " +
            "local function lo(x) local m=0 for _,v in pairs(x) do if m==0 or v<m then m=v end end return m end " +
            "local g=UnitGUID('target') " +
            "return string.format('%d|%d|%d|%d|%d|%d', lo(t.es), lo(t.lb), g and t.d[g] or 0, t.nes, t.over, g and t.sg==g and 1 or 0)";

        private static void ReadCombatLogTracker()
        {
            string raw = Lua.GetReturnVal<string>(CombatLogQuery, 0);
            if (string.IsNullOrEmpty(raw))
                return;
            string[] parts = raw.Split('|');
            double value;
            if (parts.Length != 6)
                return;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _earthShockMinHit = value;
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _lightningBoltMinHit = value;
            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _ownDamageOnTarget = value;
            if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _earthShockHits = value;
            if (double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _earthShockLastOverkill = value;
            _lastShockOnTarget = parts[5] == "1";
        }

        private static void ObserveTarget()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastLuaInstall).TotalSeconds > 5)
            {
                // No-op once the frame exists; re-creates it after /reload.
                Lua.DoString(CombatLogTracker);
                _lastLuaInstall = now;
            }

            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || target.Dead)
                return;

            ReadCombatLogTracker();
            if (_executePending && target.Guid != _executeTarget)
                _executePending = false;
            if (_executePending && _earthShockHits > _executeHitCounter && _lastShockOnTarget)
            {
                // An execute hit with no overkill did not kill its target.
                // Tighten the safety margin so subsequent attempts need less HP.
                if (_earthShockLastOverkill <= 0)
                {
                    _executeSafety = Math.Max(0.5, _executeSafety - 0.10);
                    Logger.Write("[Elemental] Earth Shock did not finish target; safety reduced to {0:P0}",
                        0.70 * _executeSafety);
                }
                _executePending = false;
            }
            if (_executePending && (now - _executeTime).TotalSeconds > 2)
                _executePending = false; // miss/immune: never stall awaiting a hit

            double health = target.CurrentHealth;
            if (_sampledTarget != target.Guid || health > _sampleHealth ||
                (now - _sampleTime).TotalSeconds > 6)
            {
                _sampledTarget = target.Guid;
                _sampleTime = now;
                _sampleHealth = health;
                _sampleOwnDamage = _ownDamageOnTarget;
                _externalDps = 0;
                _externalSamples = 0;
                _lastExternalDamage = DateTime.MinValue;
                return;
            }

            double seconds = (now - _sampleTime).TotalSeconds;
            if (seconds < 1.0) // smooth health/combat-log timing skew
                return;

            double lost = _sampleHealth - health;
            double own = Math.Max(0, _ownDamageOnTarget - _sampleOwnDamage);
            double external = Math.Max(0, lost - own) / seconds;
            if (external > 0)
            {
                _externalDps = _externalDps > 0 ? 0.5 * _externalDps + 0.5 * external : external;
                _externalSamples++;
                _lastExternalDamage = now;
            }
            else
            {
                _externalDps *= 0.5;
                _externalSamples = 0;
            }
            _sampleHealth = health;
            _sampleTime = now;
            _sampleOwnDamage = _ownDamageOnTarget;
        }

        // Predict when OTHER players' damage will kill the mob. Our own shocks
        // and bolts must not convince the routine to stop attacking a solo target.
        private static double EstimateTimeToDie(WoWUnit target)
        {
            if (target == null || target.Dead || target.CurrentHealth <= 0)
                return 0;
            if (target.IsTrainingDummy() || (!StyxWoW.Me.IsInParty && !StyxWoW.Me.IsInRaid))
                return double.PositiveInfinity;
            if (_sampledTarget == target.Guid && _externalDps > 0 && _externalSamples >= 2 &&
                (DateTime.UtcNow - _lastExternalDamage).TotalSeconds < 2.5)
                return target.CurrentHealth / _externalDps;
            return double.PositiveInfinity; // insufficient data; keep attacking
        }

        // Rank-dependent base points plus WotLK spell-power coefficients. Predictions
        // are conservative, never guaranteed (resists, resilience, rolls, talents).
        private static double EstimateDamage(string name)
        {
            WoWSpell spell;
            if (!SpellManager.Spells.TryGetValue(name, out spell) || spell == null)
                return 0;
            var effect = spell.GetSpellEffect(0);
            if (effect == null || effect.BasePoints <= 0)
                return 0;

            DateTime now = DateTime.UtcNow;
            if ((now - _spellPowerTime).TotalSeconds > 2)
            {
                _naturePower = Math.Max(0, Lua.GetReturnVal<double>("return GetSpellBonusDamage(4)", 0));
                _firePower = Math.Max(0, Lua.GetReturnVal<double>("return GetSpellBonusDamage(3)", 0));
                _spellPowerTime = now;
            }
            double coefficient = name == "Earth Shock" ? 0.386 :
                                 name == "Lightning Bolt" ? 0.714 :
                                 name == "Chain Lightning" || name == "Lava Burst" ? 0.571 : 0;
            double power = name == "Lava Burst" ? _firePower : _naturePower;
            double predicted = Math.Max(0, (effect.BasePoints + 1 + coefficient * power) * 0.70);
            if (name == "Earth Shock" && _earthShockMinHit > 0)
                return Math.Min(predicted / 0.70, _earthShockMinHit * 0.90);
            if (name == "Lightning Bolt" && _lightningBoltMinHit > 0)
                return Math.Min(predicted / 0.70, _lightningBoltMinHit * 0.90);
            return predicted;
        }

        private static double EstimatedCastSeconds(string name)
        {
            WoWSpell spell;
            if (SpellManager.Spells.TryGetValue(name, out spell) && spell != null && spell.CastTime > 0)
                return spell.CastTime / 1000.0;
            return name == "Lava Burst" || name == "Chain Lightning" ? 2.0 : 2.5;
        }

        private static bool CanFinishWithEarthShock()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || target.Dead || _executePending)
                return false;
            double damage = EstimateDamage("Earth Shock");
            WoWSpell shock;
            return damage > 0 &&
                   SpellManager.Spells.TryGetValue("Earth Shock", out shock) &&
                   target.Distance < shock.MaxRange &&
                   SpellManager.CanCast("Earth Shock", target, false) &&
                   target.CurrentHealth <= damage * 0.70 * _executeSafety;
        }

        // The tree's action is the actual cast attempt. Track only a successful
        // SpellManager.Cast; failed checks do not suppress the fallback rotation.
        private static Composite ExecuteEarthShock()
        {
            return new Decorator(ret => CanFinishWithEarthShock(),
                new Action(ret =>
                {
                    WoWUnit target = StyxWoW.Me.CurrentTarget;
                    if (target == null || !SpellManager.Cast("Earth Shock", target))
                        return RunStatus.Failure;
                    _executePending = true;
                    _executeTime = DateTime.UtcNow;
                    _executeTarget = target.Guid;
                    _executeHitCounter = _earthShockHits;
                    Logger.Write("[Elemental] Earth Shock execute (target {0} HP, safety {1:P0})",
                        target.CurrentHealth, 0.70 * _executeSafety);
                    return RunStatus.Success;
                }));
        }

        private static bool ShouldStartCast()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || target.Dead || StyxWoW.Me.IsMoving)
                return false;
            // Never wait for a shock cooldown after a failed execute. Even if
            // external DPS predicts a very short lifetime, attack unless our
            // available Earth Shock really meets the conservative kill threshold.
            return !CanFinishWithEarthShock();
        }

        private static bool ShouldUseQuickChainLightning()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || !ShouldStartCast() ||
                Spell.GetSpellCooldown("Chain Lightning") > TimeSpan.Zero)
                return false;
            double ttd = EstimateTimeToDie(target);
            return EstimatedCastSeconds("Chain Lightning") + 0.4 < ttd &&
                   ttd <= EstimatedCastSeconds("Lightning Bolt") + 0.4;
        }

        private static bool ShouldApplyFlameShock()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || target.Dead || CanFinishWithEarthShock() ||
                EstimateTimeToDie(target) < 8)
                return false;
            double bolt = EstimateDamage("Lightning Bolt");
            // When TTD is unknown, require several direct hits' worth of HP.
            return bolt > 0 ? target.CurrentHealth > 3 * bolt : target.HealthPercent > 25;
        }

        private static bool ShouldCastLavaBurst()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            return target != null && target.GetAuraTimeLeft("Flame Shock", true).TotalSeconds >
                   EstimatedCastSeconds("Lava Burst") + 0.5 &&
                   Spell.GetSpellCooldown("Lava Burst") == TimeSpan.Zero && ShouldStartCast();
        }

        private static bool ShouldCastEarthShock()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || target.Dead)
                return false;
            // A missing shock must not suppress bolts. Its damage is a filler,
            // never a reason to idle when the spell is on cooldown or out of range.
            WoWSpell shock;
            if (!SpellManager.Spells.TryGetValue("Earth Shock", out shock) ||
                target.Distance >= shock.MaxRange || !SpellManager.CanCast("Earth Shock", target, false))
                return false;
            return CanFinishWithEarthShock() ||
                   EstimateTimeToDie(target) <= EstimatedCastSeconds("Lightning Bolt") + 0.4 ||
                   (StyxWoW.Me.IsMoving &&
                    (target.GetAuraTimeLeft("Flame Shock", true).TotalSeconds > 0 ||
                     !ShouldApplyFlameShock())) ||
                   (target.GetAuraTimeLeft("Flame Shock", true).TotalSeconds > 6 &&
                    !ShouldCastLavaBurst());
        }

        private static bool ShouldSetTotems()
        {
            WoWUnit target = StyxWoW.Me.CurrentTarget;
            if (target == null || CanFinishWithEarthShock() || EstimateTimeToDie(target) <= 5)
                return false;
            double bolt = EstimateDamage("Lightning Bolt");
            return bolt > 0 ? target.CurrentHealth > 3 * bolt : target.HealthPercent > 25;
        }

        #region Common

        // Water Shield emergency check: runs in the CombatBuffs pipeline (which pulses
        // before Combat on every bot tick, and is NOT gated by WaitForCast/WaitForCastOrChannel).
        // Uses Lua CastSpellByName directly to bypass SpellManager.CanCast()'s GCD / IsCasting
        // guard. WoW's built-in ~400 ms spell queue will hold the request and fire it the instant
        // the current GCD or cast finishes — so the shield is reapplied on the very next GCD
        // after it falls off, with zero additional delay.
        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.CombatBuffs)]
        [Behavior(BehaviorType.PullBuffs)]
        [Context(WoWContext.All)]
        public static Composite CreateElementalShamanCombatBuffs()
        {
            return new PrioritySelector(
                new Decorator(
                    ret => !StyxWoW.Me.HasAura("Water Shield") && SpellManager.HasSpell("Water Shield"),
                    new Action(ret =>
                    {
                        Logger.Write("[Water Shield] Missing — queueing cast via Lua spell queue.");
                        Lua.DoString("CastSpellByName('Water Shield')");
                        return RunStatus.Success;
                    }))
                );
        }

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.PreCombatBuffs)]
        [Context(WoWContext.All)]
        public static Composite CreateElementalShamanPreCombatBuffs()
        {
            return new PrioritySelector(
                Spell.BuffSelf("Water Shield"),

                new Decorator(
                    ret => !StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.IsValid && StyxWoW.Me.Inventory.Equipped.MainHand.ItemInfo.WeaponClass != WoWItemWeaponClass.FishingPole && SpellManager.HasSpell("Flametongue Weapon") &&
                            SpellManager.CanCast("Flametongue Weapon", null, false, false),
                    new Sequence(
                        new Action(ret => Lua.DoString("CancelItemTempEnchantment(1)")),
                        new Action(ret => Logger.Write("Imbuing main hand weapon with Flametongue")),
                        new Action(ret => SpellManager.Cast("Flametongue Weapon", null))
                        )),

                Totems.CreateRecallTotems()
                );
        }

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Rest)]
        [Context(WoWContext.All)]
        public static Composite CreateElementalShamanRest()
        {
            return
                new PrioritySelector(
                    Spell.BuffSelf("Water Shield", ret => !StyxWoW.Me.HasAura("Drink") && !StyxWoW.Me.HasAura("Food")),
                    new Decorator(
                        ret => !StyxWoW.Me.HasAura("Drink") && !StyxWoW.Me.HasAura("Food"),
                        CreateElementalShamanHeal()),
                    Rest.CreateDefaultRestBehaviour(),
                    Spell.Resurrect("Ancestral Spirit")
                    );
        }

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Heal)]
        [Context(WoWContext.Instances)]
        [Context(WoWContext.Normal)]
        public static Composite CreateElementalShamanHeal()
                {
                    return
                        new Decorator(
                            ret => SingularSettings.Instance.Shaman.ElementalHeal,
                            new PrioritySelector(
                                // Heal the party in dungeons if the healer is dead
                                new Decorator(
                                    ret => StyxWoW.Me.CurrentMap.IsDungeon && !StyxWoW.Me.IsInRaid &&
                                        Group.Healers.Count(h => h.IsAlive) == 0,
                                    Restoration.CreateRestoShamanHealingOnlyBehavior()),

                                // Self-heal in combat when HP is below 30% (solo, BGs, or when group healers are dead)
                                new Decorator(
                                    ret => !Group.Healers.Any(h => h.IsAlive),
                                    new PrioritySelector(
                                        Spell.Heal("Lesser Healing Wave",
                                            ret => StyxWoW.Me,
                                            ret => StyxWoW.Me.HealthPercent < 40),

                                        Spell.Heal("Healing Wave",
                                            ret => StyxWoW.Me,
                                            ret => !SpellManager.HasSpell("Lesser Healing Wave") && StyxWoW.Me.HealthPercent < 40)))
                                ));
                }

        #endregion

        #region Normal Rotation

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Pull)]
        [Context(WoWContext.Normal)]
        public static Composite CreateElementalShamanNormalPull()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                // Sample while casting as well, not just when the GCD is free.
                new Action(ret => { ObserveTarget(); return RunStatus.Failure; }),
                Spell.BuffSelf("Water Shield"),
                Spell.WaitForCast(true),

                ExecuteEarthShock(),
                new Decorator(
                    ret => StyxWoW.Me.CurrentTarget.DistanceSqr < 40 * 40 && ShouldSetTotems(),
                    Totems.CreateSetTotems()),
                Spell.Cast("Lightning Bolt", ret => ShouldStartCast()),
                Movement.CreateMoveToTargetBehavior(true, 32f)
                );
        }

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Normal)]
        public static Composite CreateElementalShamanNormalCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                new Action(ret => { ObserveTarget(); return RunStatus.Failure; }),
                Spell.BuffSelf("Water Shield"),
                Spell.WaitForCast(true),
                Spell.WaitForCastOrChannel(),
                new Decorator(
                    ret => !Spell.IsGlobalCooldown(),
                    new PrioritySelector(
                        Spell.BuffSelf("Water Shield"),
                        Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),
                        // Execute before setup, DoTs or AoE: shocks share a cooldown.
                        ExecuteEarthShock(),
                        new Decorator(ret => ShouldSetTotems(), Totems.CreateSetTotems()),
                        Spell.BuffSelf("Elemental Mastery", ret => ShouldSetTotems() &&
                            SingularSettings.Instance.Shaman.IncludeAoeRotation && Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2),
                        Spell.BuffSelf("Thunderstorm", ret => SingularSettings.Instance.Shaman.IncludeAoeRotation &&
                            Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2),
                        Spell.Buff("Flame Shock", true, ret => ShouldApplyFlameShock(), "Flame Shock"),
                        Spell.Cast("Lava Burst", ret => ShouldCastLavaBurst()),
                        Spell.Cast("Earth Shock", ret => ShouldCastEarthShock()),
                        Spell.Cast("Chain Lightning", ret => SingularSettings.Instance.Shaman.IncludeAoeRotation &&
                            Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2 && ShouldStartCast()),
                        Spell.Cast("Chain Lightning", ret => ShouldUseQuickChainLightning()),
                        Spell.Cast("Lightning Bolt", ret => ShouldStartCast()))),
                Movement.CreateMoveToTargetBehavior(true, 32f)
                );
        }

        #endregion

        #region Battleground Rotation

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Battlegrounds)]
        public static Composite CreateElementalShamanPvPPullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                new Action(ret => { ObserveTarget(); return RunStatus.Failure; }),
                Spell.BuffSelf("Water Shield"),
                Spell.WaitForCast(true),
                Spell.WaitForCastOrChannel(),
                new Decorator(
                    ret => !Spell.IsGlobalCooldown(),
                    new PrioritySelector(
                        Spell.BuffSelf("Water Shield"),
                        Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),
                        ExecuteEarthShock(),
                        new Decorator(ret => ShouldSetTotems(), Totems.CreateSetTotems()),

                Spell.BuffSelf("Elemental Mastery",
                    ret => ShouldSetTotems() && StyxWoW.Me.IsMoving && StyxWoW.Me.Combat),
                Spell.BuffSelf("Thunderstorm", ret => StyxWoW.Me.IsStunned()),
                new Decorator(
                    ret => SingularSettings.Instance.Shaman.IncludeAoeRotation &&
                           ShouldSetTotems() && Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2,
                    Spell.BuffSelf("Fire Elemental Totem", ret => !Totems.TotemsDisabled &&
                        !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental))),

                Spell.BuffSelf("Searing Totem",
                    ret => ShouldSetTotems() && !Totems.TotemsDisabled &&
                           StyxWoW.Me.CurrentTarget.Distance < Totems.GetTotemRange(WoWTotem.Searing) - 2f &&
                           !StyxWoW.Me.Totems.Any(
                                t => t.Unit != null && t.WoWTotem == WoWTotem.Searing &&
                                     t.Unit.Location.Distance(StyxWoW.Me.CurrentTarget.Location) < Totems.GetTotemRange(WoWTotem.Searing)) &&
                           !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),

                Spell.Buff("Flame Shock", true, ret => ShouldApplyFlameShock(), "Flame Shock"),
                Spell.Cast("Lava Burst", ret => ShouldCastLavaBurst()),
                Spell.Cast("Earth Shock", ret => ShouldCastEarthShock()),
                Spell.Cast("Chain Lightning", ret => SingularSettings.Instance.Shaman.IncludeAoeRotation &&
                    Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2 && ShouldStartCast()),
                Spell.Cast("Chain Lightning", ret => ShouldUseQuickChainLightning()),
                Spell.Cast("Lightning Bolt", ret => ShouldStartCast()))),
                Movement.CreateMoveToTargetBehavior(true, 32f)
                );
        }

        #endregion

        #region Instance Rotation

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Instances)]
        public static Composite CreateElementalShamanInstancePullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                new Action(ret => { ObserveTarget(); return RunStatus.Failure; }),
                Spell.BuffSelf("Water Shield"),
                Spell.WaitForCast(true),
                Spell.WaitForCastOrChannel(),
                new Decorator(
                    ret => !Spell.IsGlobalCooldown(),
                    new PrioritySelector(
                        Spell.BuffSelf("Water Shield"),
                        Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),
                        ExecuteEarthShock(),
                        new Decorator(ret => ShouldSetTotems(), Totems.CreateSetTotems()),

                Spell.BuffSelf("Elemental Mastery",
                    ret => ShouldSetTotems() && StyxWoW.Me.HasAnyAura("Bloodlust", "Heroism")),
                Spell.BuffSelf("Elemental Mastery",
                    ret => ShouldSetTotems() && StyxWoW.Me.IsMoving && StyxWoW.Me.Combat),
                
                // Totem stuff
                // Pop the ele on bosses
                Spell.BuffSelf("Fire Elemental Totem", ret => ShouldSetTotems() && !Totems.TotemsDisabled && StyxWoW.Me.CurrentTarget.IsBoss() && !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),
                Spell.BuffSelf("Searing Totem",
                    ret => ShouldSetTotems() && !Totems.TotemsDisabled &&
                           StyxWoW.Me.CurrentTarget.Distance < Totems.GetTotemRange(WoWTotem.Searing) - 2f &&
                           !StyxWoW.Me.Totems.Any(
                                t => t.Unit != null && t.WoWTotem == WoWTotem.Searing &&
                                     t.Unit.Location.Distance(StyxWoW.Me.CurrentTarget.Location) < Totems.GetTotemRange(WoWTotem.Searing)) &&
                           !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),

                Spell.Buff("Flame Shock", true, ret => ShouldApplyFlameShock(), "Flame Shock"),
                Spell.Cast("Lava Burst", ret => ShouldCastLavaBurst()),
                Spell.Cast("Earth Shock", ret => ShouldCastEarthShock()),
                Spell.Cast("Chain Lightning", ret => SingularSettings.Instance.Shaman.IncludeAoeRotation &&
                    Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2 && ShouldStartCast()),
                Spell.Cast("Chain Lightning", ret => ShouldUseQuickChainLightning()),
                Spell.Cast("Lightning Bolt", ret => ShouldStartCast()))),
                Movement.CreateMoveToTargetBehavior(true, 32f)
                );
        }

        #endregion
    }
}
