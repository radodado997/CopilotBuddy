using System.Collections.Generic;
using System.Linq;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;

using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;


namespace Singular.ClassSpecific.Paladin
{
    public enum PaladinSeal
    {
        Auto,
        Command,
        Corruption,
        Justice,
        Light,
        Righteousness,
        Vengeance,
        Wisdom
    }
    public enum PaladinAura
    {
        Auto,
        Devotion,
        Retribution,
        // WotLK QC: In WotLK, resistance auras are separate: Shadow/Fire/Frost Resistance Aura
        // "Resistance Aura" (merged) was added in Cata 4.0.1. Keeping enum value for settings compat,
        // but Spell.BuffSelf("Resistance Aura") will silently fail — use specific auras in rotation if needed
        Resistance,
        Concentration,
        Crusader
    }

    enum PaladinBlessings
    {
        Auto, Kings, Might, Wisdom, Sanctuary // WotLK: Wisdom separate (merged into Might in Cata), Sanctuary is Prot talent
    }

    public class Common
    {
        [Class(WoWClass.Paladin)]
        [Behavior(BehaviorType.PreCombatBuffs)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Spec(TalentSpec.HolyPaladin)]
        [Spec(TalentSpec.ProtectionPaladin)]
        [Spec(TalentSpec.Lowbie)]
        [Context(WoWContext.All)]
        public static Composite CreatePaladinPreCombatBuffs()
        {
            return
                new PrioritySelector(
                // This won't run, but it's here for changes in the future. We NEVER run this method if we're mounted.
                    Spell.BuffSelf("Crusader Aura", ret => StyxWoW.Me.Mounted),
                new Decorator(
                    ret => !Styx.PartyGrind.SquadContext.IsActive || !(Styx.PartyGrind.SquadContext.ActiveSettings?.EnableCrossPartyBuffs ?? true),
                    CreatePaladinBlessBehavior()),
                new Decorator(
                    ret => TalentManager.CurrentSpec == TalentSpec.HolyPaladin,
                    new PrioritySelector(
                        Spell.BuffSelf("Concentration Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Auto && (!Styx.PartyGrind.SquadContext.IsActive || !(Styx.PartyGrind.SquadContext.ActiveSettings?.EnableCrossPartyBuffs ?? true))),
                            // WotLK uses Seal of Wisdom for mana regen (renamed to Seal of Insight in Cata 4.0.1)
                            Spell.BuffSelf("Seal of Wisdom"),
                            Spell.BuffSelf("Seal of Righteousness", ret => !SpellManager.HasSpell("Seal of Wisdom"))
                            )),
                    new Decorator(
                        ret => TalentManager.CurrentSpec != TalentSpec.HolyPaladin,
                        new PrioritySelector(
                            Spell.BuffSelf("Righteous Fury", ret => TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin && StyxWoW.Me.IsInParty),
                            new Decorator(
                                ret => !Styx.PartyGrind.SquadContext.IsActive || !(Styx.PartyGrind.SquadContext.ActiveSettings?.EnableCrossPartyBuffs ?? true) ||
                                       CopilotBuddy.Bots.PartyGrind.Logic.SquadPartyBuffService.IsNonHolyPaladinAuraOwner(StyxWoW.Me, Styx.PartyGrind.SquadContext.GetLeaderStateSnapshot()),
                                new PrioritySelector(
                                    Spell.BuffSelf(
                                        "Devotion Aura",
                                        ret =>
                                        SingularSettings.Instance.Paladin.Aura == PaladinAura.Auto &&
                                        (StyxWoW.Me.IsInParty && TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin ||
                                         TalentManager.CurrentSpec == TalentSpec.Lowbie && !SpellManager.HasSpell("Retribution Aura"))),
                                    Spell.BuffSelf(
                                        "Retribution Aura",
                                        ret =>
                                        SingularSettings.Instance.Paladin.Aura == PaladinAura.Auto &&
                                        ((!StyxWoW.Me.IsInParty && TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin) ||
                                         TalentManager.CurrentSpec == TalentSpec.Lowbie)),
                                    Spell.BuffSelf(
                                        "Retribution Aura",
                                        ret =>
                                        SingularSettings.Instance.Paladin.Aura == PaladinAura.Auto &&
                                        TalentManager.CurrentSpec == TalentSpec.RetributionPaladin))),
                            // Select seal added by xyFaded
                            new Decorator(
                                ret => SingularSettings.Instance.Paladin.Seal != PaladinSeal.Auto,
                                new PrioritySelector(
                                    Spell.BuffSelf("Seal of Command", ret => SpellManager.HasSpell("Seal of Command") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Command),
                                    Spell.BuffSelf("Seal of Corruption", ret => SpellManager.HasSpell("Seal of Corruption") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Corruption),
                                    Spell.BuffSelf("Seal of Justice", ret => SpellManager.HasSpell("Seal of Justice") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Justice),
                                    Spell.BuffSelf("Seal of Light", ret => SpellManager.HasSpell("Seal of Light") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Light),
                                    Spell.BuffSelf("Seal of Righteousness", ret => SpellManager.HasSpell("Seal of Righteousness") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Righteousness),
                                    Spell.BuffSelf("Seal of Vengeance", ret => SpellManager.HasSpell("Seal of Vengeance") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Vengeance),
                                    Spell.BuffSelf("Seal of Wisdom", ret => SpellManager.HasSpell("Seal of Wisdom") && SingularSettings.Instance.Paladin.Seal == PaladinSeal.Wisdom)
                                )
                            ),
                            new Decorator(
                                ret => SingularSettings.Instance.Paladin.Seal == PaladinSeal.Auto,
                                new PrioritySelector(
                                    Spell.BuffSelf("Seal of Vengeance", ret => !SpellManager.HasSpell("Seal of Corruption")),
                                    Spell.BuffSelf("Seal of Corruption"),
                                    Spell.BuffSelf("Seal of Righteousness", ret => !SpellManager.HasSpell("Seal of Vengeance") && !SpellManager.HasSpell("Seal of Corruption"))
                                )
                            ),
                    new Decorator(
                        ret => SingularSettings.Instance.Paladin.Aura != PaladinAura.Auto,
                        new PrioritySelector(
                            Spell.BuffSelf("Devotion Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Devotion),
                            Spell.BuffSelf("Concentration Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Concentration),
                            // WotLK QC: "Resistance Aura" does not exist in WotLK (Cata merged Shadow/Fire/Frost Resistance Aura)
                            // Using Shadow Resistance Aura as default since it's the most commonly useful
                            Spell.BuffSelf("Shadow Resistance Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Resistance),
                            Spell.BuffSelf("Retribution Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Retribution),
                            Spell.BuffSelf("Crusader Aura", ret => SingularSettings.Instance.Paladin.Aura == PaladinAura.Crusader)
                            ))
                    
                    )));
        }

        private static bool HasAnyBlessing(WoWPlayer p, string type)
        {
            return p.HasAura("Blessing of " + type) || p.HasAura("Greater Blessing of " + type);
        }

        private static List<WoWPlayer> GetBlessTargets()
        {
            var players = new List<WoWPlayer>();
            if (StyxWoW.Me.IsInRaid)
                players.AddRange(StyxWoW.Me.RaidMembers);
            else if (StyxWoW.Me.IsInParty)
                players.AddRange(StyxWoW.Me.PartyMembers);
            players.Add(StyxWoW.Me);
            return players;
        }

        private static bool WantSanctuary()
        {
            return SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Sanctuary ||
                   (SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Auto &&
                    TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin);
        }

        private static bool WantKings()
        {
            if (SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Might ||
                SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Wisdom ||
                SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Sanctuary)
                return false;
            if (SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Auto &&
                TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin &&
                SpellManager.HasSpell("Blessing of Sanctuary"))
                return false;
            return true;
        }

        private static WoWPlayer FindBlessTarget(System.Func<WoWPlayer, bool> needsBlessing)
        {
            return GetBlessTargets().FirstOrDefault(p => p.DistanceSqr < 30 * 30 && p.IsAlive && p.InLineOfSpellSight && needsBlessing(p));
        }

        private static bool CanBlessMight()
        {
            return SingularSettings.Instance.Paladin.Blessings != PaladinBlessings.Wisdom &&
                   SingularSettings.Instance.Paladin.Blessings != PaladinBlessings.Sanctuary &&
                   !(WantSanctuary() && SpellManager.HasSpell("Blessing of Sanctuary"));
        }

        private static bool NeedsMight(WoWPlayer p)
        {
            return !HasAnyBlessing(p, "Might") && !p.HasAura("Battle Shout") &&
                   (SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Might ||
                    (SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Auto &&
                     !SpellManager.HasSpell("Blessing of Kings") &&
                     !SpellManager.HasSpell("Blessing of Sanctuary")) ||
                    (HasAnyBlessing(p, "Kings") && !p.HasMyAura("Blessing of Kings") && !p.HasMyAura("Greater Blessing of Kings")));
        }

        private static Composite CreatePaladinBlessBehavior()
        {
            return
                new PrioritySelector(
                    // Sanctuary — Greater then regular fallback
                    new Throttle(2, Spell.Cast("Greater Blessing of Sanctuary",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Sanctuary")),
                        ret => SingularSettings.Instance.Paladin.UseGreaterBlessings && WantSanctuary())),
                    new Throttle(2, Spell.Cast("Blessing of Sanctuary",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Sanctuary")),
                        ret => WantSanctuary())),
                    // Wisdom — Greater then regular fallback
                    new Throttle(2, Spell.Cast("Greater Blessing of Wisdom",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Wisdom") && !p.HasAura("Mana Spring")),
                        ret => SingularSettings.Instance.Paladin.UseGreaterBlessings &&
                               SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Wisdom)),
                    new Throttle(2, Spell.Cast("Blessing of Wisdom",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Wisdom") && !p.HasAura("Mana Spring")),
                        ret => SingularSettings.Instance.Paladin.Blessings == PaladinBlessings.Wisdom)),
                    // Kings — Greater then regular fallback
                    new Throttle(2, Spell.Cast("Greater Blessing of Kings",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Kings")),
                        ret => SingularSettings.Instance.Paladin.UseGreaterBlessings && WantKings())),
                    new Throttle(2, Spell.Cast("Blessing of Kings",
                        ret => FindBlessTarget(p => !HasAnyBlessing(p, "Kings")),
                        ret => WantKings())),
                    // Might — Greater then regular fallback
                    new Throttle(2, Spell.Cast("Greater Blessing of Might",
                        ret => FindBlessTarget(NeedsMight),
                        ret => SingularSettings.Instance.Paladin.UseGreaterBlessings && CanBlessMight())),
                    new Throttle(2, Spell.Cast("Blessing of Might",
                        ret => FindBlessTarget(NeedsMight),
                        ret => CanBlessMight()))
                    );
        }

        internal static bool HasHealingProblems()
        {
            var me = StyxWoW.Me;
            if (me == null)
                return false;

            if (Styx.PartyGrind.SquadContext.IsActive)
            {
                var leader = Styx.PartyGrind.SquadContext.GetLeaderStateSnapshot();
                if (leader?.Members != null)
                {
                    var healer = leader.Members.FirstOrDefault(m => m.Role == Styx.PartyGrind.IPC.SquadRole.Healer);
                    if (healer == null)
                        return true;

                    if (healer.IsDead || healer.IsGhost || !healer.IsConnected)
                        return true;

                    if (healer.PowerType == WoWPowerType.Mana && (healer.PowerPercent < 20f || healer.NeedsRest))
                        return true;

                    if (healer.DistanceToLeader > 35f)
                        return true;

                    var healerPlayer = ObjectManager.GetObjectByGuid<WoWPlayer>(healer.Guid);
                    if (healerPlayer != null && (!healerPlayer.InLineOfSpellSight || healerPlayer.Distance > 35f))
                        return true;

                    return false;
                }
                return true;
            }

            if (!me.IsInParty)
                return true;

            var partyHealer = me.PartyMembers.FirstOrDefault(p =>
                p.IsAlive && (p.Class == WoWClass.Priest || p.Class == WoWClass.Druid || p.Class == WoWClass.Shaman || (p.Class == WoWClass.Paladin && p.Guid != me.Guid)));
            if (partyHealer == null || partyHealer.Dead || partyHealer.ManaPercent < 20f || partyHealer.Distance > 35f || !partyHealer.InLineOfSpellSight)
                return true;

            return false;
        }

        internal static Composite CreatePaladinCombatSelfHeal()
        {
            return new PrioritySelector(
                Spell.Cast("Lay on Hands", ret => StyxWoW.Me,
                    ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.LayOnHandsHealth &&
                           !StyxWoW.Me.HasAura("Forbearance")),
                Spell.Cast("Flash of Light", ret => StyxWoW.Me,
                    ret => StyxWoW.Me.HasAura("The Art of War") &&
                           StyxWoW.Me.HealthPercent <= 80f),
                new Decorator(
                    ret => HasHealingProblems(),
                    new PrioritySelector(
                        Spell.Heal("Holy Light", ret => StyxWoW.Me,
                            ret => !SpellManager.HasSpell("Flash of Light") &&
                                   StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.HolyLightHealth),
                        Spell.Heal("Flash of Light", ret => StyxWoW.Me,
                            ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.FlashOfLightHealth)
                    )
                )
            );
        }
    }
}
