using System;
using System.Collections.Generic;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class DungeonPullAndManaGuardTests
    {
        private static readonly WoWPoint Origin = new WoWPoint(100f, 200f, 30f);

        [Fact]
        public void DungeonAdvanceStep_ClampsDistantBossApproachToASingleBreadcrumb()
        {
            var from = Origin;
            var boss = new WoWPoint(190f, 200f, 30f);

            WoWPoint step = SquadDungeonService.ResolveDungeonAdvanceStep(from, boss, Array.Empty<WoWPoint>());

            Assert.NotEqual(WoWPoint.Zero, step);
            Assert.Equal(SquadDungeonService.DungeonAdvanceStepDistance, step.Distance(from), 2);
            Assert.True(step.X > from.X && step.X < boss.X);
            Assert.Equal(from.Y, step.Y, 3);
        }

        [Fact]
        public void DungeonAdvanceStep_KeepsTheFinalCrumbWhenItIsCloserThanOneStep()
        {
            var from = Origin;
            var crumb = new WoWPoint(106f, 208f, 30f);

            WoWPoint step = SquadDungeonService.ResolveDungeonAdvanceStep(from, crumb, new List<WoWPoint>());

            Assert.Equal(crumb, step);
        }

        [Fact]
        public void DungeonAdvanceStep_RefusesCorridorThatStillHoldsLiveTrash()
        {
            var from = Origin;
            var objective = new WoWPoint(160f, 200f, 30f);
            var trash = new[] { new WoWPoint(110f, 204f, 30f) };

            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash));
        }

        [Fact]
        public void DungeonAdvanceStep_IgnoresTrashBesideTheCorridorAndOnAnotherFloor()
        {
            var from = Origin;
            var objective = new WoWPoint(160f, 200f, 30f);

            var beside = new[] { new WoWPoint(110f, 230f, 30f) };
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, beside));

            var upstairs = new[] { new WoWPoint(110f, 200f, 70f) };
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, upstairs));
        }

        [Fact]
        public void DungeonAdvanceStep_IgnoresTrashTrailingBehindTheLeader()
        {
            var from = Origin;
            var objective = new WoWPoint(160f, 200f, 30f);
            var trailing = new[] { new WoWPoint(95f, 200f, 30f) };

            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trailing));
        }

        [Fact]
        public void DungeonAdvanceStep_CanBeForcedForwardWhenTheCorridorStaysBlocked()
        {
            var from = Origin;
            var objective = new WoWPoint(160f, 200f, 30f);
            var trash = new[] { new WoWPoint(110f, 204f, 30f) };

            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash));
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash, clearance: 0f));
        }

        [Fact]
        public void DungeonAdvanceStep_ReturnsZeroWithoutAnObjective()
        {
            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(WoWPoint.Zero, new WoWPoint(110f, 200f, 30f), null));
            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(Origin, WoWPoint.Zero, null));
        }

        [Fact]
        public void CorridorSegmentDistance_MeasuresPerpendicularOffsetAndEndpoints()
        {
            var from = Origin;
            var to = new WoWPoint(110f, 200f, 30f);

            Assert.Equal(0f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(105f, 200f, 30f), from, to), 3);
            Assert.Equal(5f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(105f, 205f, 30f), from, to), 3);
            Assert.Equal(10f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(90f, 200f, 30f), from, to), 3);
        }

        [Theory]
        [InlineData(75.0, 100.0, 75.0)]
        [InlineData(75.0, 50.0, 75.0)]
        [InlineData(75.0, 44.0, 60.0)]
        [InlineData(75.0, 25.0, 50.0)]
        [InlineData(75.0, 10.0, 35.0)]
        [InlineData(30.0, 10.0, 30.0)]
        public void ManaReserve_LowersHealThresholdOnlyWhileTheHealerIsLowOnMana(
            double baseline, double healerManaPercent, double expected)
        {
            Assert.Equal(expected, SquadHealBridge.ApplyManaReserveThreshold(baseline, healerManaPercent), 3);
        }

        [Fact]
        public void BlessingAssignment_GivesEveryTargetOneOwningPaladin()
        {
            var protectionPaladin = new[] { "Blessing of Sanctuary", "Blessing of Kings", "Blessing of Might", "Blessing of Wisdom" };
            var retributionPaladin = new[] { "Blessing of Kings", "Blessing of Might" };
            var known = new List<IReadOnlyList<string>> { protectionPaladin, retributionPaladin };

            var tank = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.TankLeader, WoWClass.Paladin, known);
            Assert.Equal(0, tank.CasterIndex);
            Assert.Equal("Blessing of Sanctuary", tank.SpellName);

            var healer = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.Healer, WoWClass.Priest, known);
            Assert.Equal(0, healer.CasterIndex);
            Assert.Equal("Blessing of Wisdom", healer.SpellName);

            var melee = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.DpsMelee, WoWClass.Rogue, known);
            Assert.Equal(0, melee.CasterIndex);
            Assert.Equal("Blessing of Might", melee.SpellName);
        }

        [Fact]
        public void BlessingAssignment_HandsTheTargetToThePaladinThatKnowsTheBlessing()
        {
            var lowLevel = new[] { "Blessing of Might" };
            var veteran = new[] { "Blessing of Might", "Blessing of Kings", "Blessing of Wisdom" };
            var known = new List<IReadOnlyList<string>> { lowLevel, veteran };

            var might = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.DpsMelee, WoWClass.Rogue, known);
            Assert.Equal(0, might.CasterIndex);
            Assert.Equal("Blessing of Might", might.SpellName);

            var wisdom = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.Healer, WoWClass.Priest, known);
            Assert.Equal(1, wisdom.CasterIndex);
            Assert.Equal("Blessing of Wisdom", wisdom.SpellName);

            var tank = SquadPartyBuffService.SelectBlessingAssignment(SquadRole.TankLeader, WoWClass.Warrior, known);
            Assert.Equal(1, tank.CasterIndex);
            Assert.Equal("Blessing of Kings", tank.SpellName);
        }

        [Fact]
        public void BlessingAssignment_ReturnsNoOwnerWhenNobodyKnowsABlessing()
        {
            var none = SquadPartyBuffService.SelectBlessingAssignment(
                SquadRole.DpsMelee, WoWClass.Rogue, new List<IReadOnlyList<string>>());
            Assert.Equal(-1, none.CasterIndex);
            Assert.Equal(string.Empty, none.SpellName);

            var emptySpellLists = SquadPartyBuffService.SelectBlessingAssignment(
                SquadRole.TankLeader,
                WoWClass.Warrior,
                new List<IReadOnlyList<string>> { Array.Empty<string>(), Array.Empty<string>() });
            Assert.Equal(-1, emptySpellLists.CasterIndex);
            Assert.Equal(string.Empty, emptySpellLists.SpellName);
        }

        [Fact]
        public void AnyExistingBlessing_LocksTheBuffSlotAgainstOtherPaladins()
        {
            Assert.True(SquadPartyBuffService.HasAnyPaladinBlessing(aura => aura == "Blessing of Kings"));
            Assert.True(SquadPartyBuffService.HasAnyPaladinBlessing(aura => aura == "Greater Blessing of Might"));
            Assert.False(SquadPartyBuffService.HasAnyPaladinBlessing(aura => aura == "Arcane Intellect"));
            Assert.False(SquadPartyBuffService.HasAnyPaladinBlessing(_ => false));
        }

        [Fact]
        public void DungeonPullOpener_PrefersSingleTargetSpellWhenTheTargetHasPackNeighbours()
        {
            string[] packed = SquadTankBridge.GetPullSpellPriority(WoWClass.Paladin, isolatedTarget: false);
            Assert.Equal("Hand of Reckoning", packed[0]);
            Assert.Equal("Avenger's Shield", packed[1]);
            Assert.Equal("Exorcism", packed[2]);

            string[] packedUndead = SquadTankBridge.GetPullSpellPriority(
                WoWClass.Paladin, isolatedTarget: false, targetIsUndeadOrDemon: true);
            Assert.Equal("Hand of Reckoning", packedUndead[0]);
            Assert.Equal("Exorcism", packedUndead[1]);
            Assert.Equal("Avenger's Shield", packedUndead[2]);

            string[] isolated = SquadTankBridge.GetPullSpellPriority(WoWClass.Paladin, isolatedTarget: true);
            Assert.Equal("Avenger's Shield", isolated[0]);

            Assert.Equal(new[] { "Heroic Throw", "Taunt", "Shoot", "Throw" },
                SquadTankBridge.GetPullSpellPriority(WoWClass.Warrior, isolatedTarget: false));
            Assert.Empty(SquadTankBridge.GetPullSpellPriority(WoWClass.Hunter, isolatedTarget: true));
        }
    }
}
