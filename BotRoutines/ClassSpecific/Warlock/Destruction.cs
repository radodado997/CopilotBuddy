using System.Linq;

using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;

using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Styx.Logic.Combat;

namespace Singular.ClassSpecific.Warlock
{
    public class Destruction
    {
        #region Common

        [Class(WoWClass.Warlock)]
        [Spec(TalentSpec.DestructionWarlock)]
        [Behavior(BehaviorType.PreCombatBuffs)]
        [Context(WoWContext.All)]
        [Priority(1)]
        public static Composite CreateDestructionWarlockPreCombatBuffs()
        {
            return new PrioritySelector(
                Spell.WaitForCast(false),
                Pet.CreateSummonPet("Imp")
                );
        }

        #endregion

        #region Normal Rotation

        private static Composite CreateDestructionSingleTargetRotation(Composite curseSelector)
        {
            return new PrioritySelector(
                curseSelector,
                Spell.Buff("Immolate", true, ret => StyxWoW.Me.CurrentTarget != null &&
                    !StyxWoW.Me.CurrentTarget.IsImmune(WoWSpellSchool.Fire) &&
                    (StyxWoW.Me.CurrentTarget.IsBoss() || StyxWoW.Me.CurrentTarget.HealthPercent > 25)),
                Spell.Cast("Conflagrate", ret => StyxWoW.Me.CurrentTarget != null &&
                    StyxWoW.Me.CurrentTarget.HasMyAura("Immolate") &&
                    !StyxWoW.Me.CurrentTarget.IsImmune(WoWSpellSchool.Fire)),
                Spell.Buff("Corruption", true, ret => StyxWoW.Me.CurrentTarget != null &&
                    (StyxWoW.Me.CurrentTarget.IsBoss() || StyxWoW.Me.CurrentTarget.HealthPercent > 30)),
                Spell.Cast("Chaos Bolt"),
                Spell.Cast("Shadowburn", ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.HealthPercent <= 20),
                Spell.Cast("Incinerate", ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.CurrentTarget.IsImmune(WoWSpellSchool.Fire)),
                Spell.Cast("Shadow Bolt", ret => !SpellManager.HasSpell("Incinerate") ||
                    (StyxWoW.Me.CurrentTarget != null &&
                     (StyxWoW.Me.CurrentTarget.IsImmune(WoWSpellSchool.Fire) ||
                      !SpellManager.CanCast("Incinerate", StyxWoW.Me.CurrentTarget, false, false)))),
                Helpers.Common.CreateUseWand(ret => StyxWoW.Me.ManaPercent < 15 && !SpellManager.CanCast("Life Tap"))
                );
        }

        [Class(WoWClass.Warlock)]
        [Spec(TalentSpec.DestructionWarlock)]
        [Behavior(BehaviorType.Pull)]
        [Context(WoWContext.Normal)]
        public static Composite CreateDestructionWarlockNormalPull()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.PreventDoubleCast("Immolate"),
                Spell.WaitForCastOrChannel(),
                Helpers.Common.CreatePetAttack(),
                Spell.Cast("Soul Fire"),
                Spell.Buff("Immolate", true, ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.CurrentTarget.IsImmune(WoWSpellSchool.Fire)),
                Spell.Cast("Shadow Bolt"),
                Movement.CreateMoveToTargetBehavior(true, 28f)
                );
        }

        [Class(WoWClass.Warlock)]
        [Spec(TalentSpec.DestructionWarlock)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Normal)]
        public static Composite CreateDestructionWarlockNormalCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.PreventDoubleCast("Immolate"),
                Spell.WaitForCastOrChannel(),
                Helpers.Common.CreatePetAttack(),
                Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                // Cooldowns
                Spell.BuffSelf("Soulshatter", ret => Unit.NearbyUnfriendlyUnits.Any(u => u.IsTargetingMeOrPet)),
                Spell.Cast("Death Coil", ret => StyxWoW.Me.HealthPercent <= 70),

                // AoE rotation
                Spell.BuffSelf("Shadowflame",
                            ret => Unit.NearbyUnfriendlyUnits.Count(u => u.DistanceSqr < 10 * 10 && StyxWoW.Me.IsSafelyFacing(u, 90)) >= 3),

                Spell.BuffSelf("Howl of Terror", ret => Unit.NearbyUnfriendlyUnits.Count(u => u.DistanceSqr < 10 * 10) >= 3),
                Spell.Buff("Fear", ret => Targeting.Instance.TargetList.ElementAtOrDefault(1), ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.CurrentTarget.HasAura("Fear")),
                Spell.Buff("Fear", ret => StyxWoW.Me.HealthPercent < 80),

                // Single target rotation
                // WotLK: Improved Soul Fire does not exist (Cata 4.0.1).
                // WotLK QC: Removed "Soul Fire on Empowered Imp" — in WotLK, Empowered Imp gives +100% crit on next spell,
                // it does NOT make Soul Fire instant (that's the Cata redesign). Hard-casting 6s Soul Fire mid-combat = massive DPS loss
                // vs. letting the 100% crit proc be consumed by the next Incinerate/Conflagrate/Chaos Bolt (~2s cast).
                // WotLK: Only one curse per target — use Curse of Doom (solo DPS), skip CoE to avoid ping-pong
                CreateDestructionSingleTargetRotation(
                    new PrioritySelector(
                        Spell.Buff("Curse of Doom", true, ret => StyxWoW.Me.CurrentTarget != null &&
                            (StyxWoW.Me.CurrentTarget.Elite || StyxWoW.Me.CurrentTarget.HealthPercent > 75)),
                        Spell.Buff("Curse of Agony", true, ret => !SpellManager.HasSpell("Curse of Doom") &&
                            StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.HealthPercent > 40)
                        )),

                Movement.CreateMoveToTargetBehavior(true, 28f)
                );
        }

        #endregion

        #region Battleground Rotation

        [Class(WoWClass.Warlock)]
        [Spec(TalentSpec.DestructionWarlock)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Battlegrounds)]
        public static Composite CreateDestructionWarlockPvPPullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.PreventDoubleCast("Immolate"),
                Spell.WaitForCastOrChannel(),
                Helpers.Common.CreatePetAttack(),
                Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                // Cooldowns
                Spell.BuffSelf("Soulshatter", ret => Unit.NearbyUnfriendlyUnits.Any(u => u.IsTargetingMeOrPet)),
                Spell.Cast("Death Coil", ret => StyxWoW.Me.HealthPercent <= 70),

                Spell.CastOnGround("Shadowfury", ret => StyxWoW.Me.CurrentTarget != null ? StyxWoW.Me.CurrentTarget.Location : Styx.Logic.Pathing.WoWPoint.Zero),

                Spell.BuffSelf("Howl of Terror", ret => Unit.NearbyUnfriendlyUnits.Count(u => u.DistanceSqr < 10 * 10) >= 3),
                // Dimishing returns fucks Fear up. Avoid using it until a proper DR logic.
                //Spell.Buff("Fear", ret => Targeting.Instance.TargetList.ElementAtOrDefault(1)),
                // Single target rotation
                // WotLK QC: Removed "Soul Fire on Empowered Imp" — see Normal rotation comment for details.
                // WotLK QC: Fixed spell name — "Curse of the Elements" (with "the")
                // WotLK: Curse of Doom removed from PvP — CoT/CoE above are more valuable, only one curse allowed per target
                CreateDestructionSingleTargetRotation(
                    new PrioritySelector(
                        Spell.Buff("Curse of Tongues", ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.PowerType == WoWPowerType.Mana),
                        Spell.Buff("Curse of the Elements", ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.PowerType != WoWPowerType.Mana)
                        )),

                Movement.CreateMoveToTargetBehavior(true, 28f)
                );
        }

        #endregion

        #region Instance Rotation

        [Class(WoWClass.Warlock)]
        [Spec(TalentSpec.DestructionWarlock)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Instances)]
        public static Composite CreateDestructionWarlockInstancePullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.PreventDoubleCast("Immolate"),
                Spell.WaitForCastOrChannel(),
                Helpers.Common.CreatePetAttack(),
                Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                // Cooldowns
                Spell.BuffSelf("Soulshatter", ret => Unit.NearbyUnfriendlyUnits.Any(u => u.IsTargetingMeOrPet)),
                Spell.Cast("Death Coil", ret => StyxWoW.Me.HealthPercent <= 70),

                // AoE rotation
                new Decorator(
                    ret => StyxWoW.Me.CurrentTarget != null &&
                           Unit.NearbyUnfriendlyUnits.Count(u => u.IsTargetingMeOrPet || u.IsTargetingMyPartyMember || u.IsTargetingMyRaidMember) >= 3,
                    new PrioritySelector(
                        Spell.CastOnGround("Shadowfury", ret => StyxWoW.Me.CurrentTarget != null ? StyxWoW.Me.CurrentTarget.Location : Styx.Logic.Pathing.WoWPoint.Zero),
                        Spell.CastOnGround("Rain of Fire", ret => StyxWoW.Me.CurrentTarget != null ? StyxWoW.Me.CurrentTarget.Location : Styx.Logic.Pathing.WoWPoint.Zero)
                        )),

                // Single target rotation
                // WotLK: Improved Soul Fire does not exist (Cata 4.0.1).
                // WotLK QC: Removed "Soul Fire on Empowered Imp" — see Normal rotation comment for details.
                // WotLK: Only one curse per target — CoE for raid debuff on bosses, CoD for sustained DPS
                // WotLK QC: Added missing Conflagrate to Instance rotation (core Destruction DPS ability)
                CreateDestructionSingleTargetRotation(
                    new PrioritySelector(
                        Spell.Buff("Curse of the Elements", ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.IsBoss()),
                        Spell.Buff("Curse of Doom", true, ret => StyxWoW.Me.CurrentTarget != null &&
                            !StyxWoW.Me.CurrentTarget.IsBoss() && StyxWoW.Me.CurrentTarget.HealthPercent > 60),
                        Spell.Buff("Curse of Agony", true, ret => !SpellManager.HasSpell("Curse of Doom") &&
                            StyxWoW.Me.CurrentTarget != null &&
                            ((StyxWoW.Me.CurrentTarget.IsBoss() && !SpellManager.HasSpell("Curse of the Elements")) ||
                             (!StyxWoW.Me.CurrentTarget.IsBoss() && StyxWoW.Me.CurrentTarget.HealthPercent > 50 && !StyxWoW.Me.CurrentTarget.HasAura("Curse of the Elements"))))
                        )),

                Movement.CreateMoveToTargetBehavior(true, 28f)
                );
        }

        #endregion
    }
}
