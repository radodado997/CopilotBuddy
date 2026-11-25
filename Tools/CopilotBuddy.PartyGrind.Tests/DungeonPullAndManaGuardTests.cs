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
        [Fact]
        public void DungeonAdvanceStep_ClampsDistantBossApproachToASingleBreadcrumb()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var boss = new WoWPoint(90f, 0f, 0f);

            WoWPoint step = SquadDungeonService.ResolveDungeonAdvanceStep(from, boss, Array.Empty<WoWPoint>());

            Assert.NotEqual(WoWPoint.Zero, step);
            Assert.Equal(SquadDungeonService.DungeonAdvanceStepDistance, step.Distance(from), 2);
            Assert.True(step.X > 0f && step.X < boss.X);
            Assert.Equal(0f, step.Y, 3);
        }

        [Fact]
        public void DungeonAdvanceStep_KeepsTheFinalCrumbWhenItIsCloserThanOneStep()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var crumb = new WoWPoint(6f, 8f, 0f);

            WoWPoint step = SquadDungeonService.ResolveDungeonAdvanceStep(from, crumb, new List<WoWPoint>());

            Assert.Equal(crumb, step);
        }

        [Fact]
        public void DungeonAdvanceStep_RefusesCorridorThatStillHoldsLiveTrash()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var objective = new WoWPoint(60f, 0f, 0f);
            var trash = new[] { new WoWPoint(10f, 4f, 0f) };

            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash));
        }

        [Fact]
        public void DungeonAdvanceStep_IgnoresTrashBesideTheCorridorAndOnAnotherFloor()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var objective = new WoWPoint(60f, 0f, 0f);

            var beside = new[] { new WoWPoint(10f, 30f, 0f) };
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, beside));

            var upstairs = new[] { new WoWPoint(10f, 0f, 40f) };
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, upstairs));
        }

        [Fact]
        public void DungeonAdvanceStep_IgnoresTrashTrailingBehindTheLeader()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var objective = new WoWPoint(60f, 0f, 0f);
            var trailing = new[] { new WoWPoint(-5f, 0f, 0f) };

            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trailing));
        }

        [Fact]
        public void DungeonAdvanceStep_CanBeForcedForwardWhenTheCorridorStaysBlocked()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var objective = new WoWPoint(60f, 0f, 0f);
            var trash = new[] { new WoWPoint(10f, 4f, 0f) };

            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash));
            Assert.NotEqual(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, trash, clearance: 0f));
        }

        [Fact]
        public void DungeonAdvanceStep_ReturnsZeroWithoutAnObjective()
        {
            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(WoWPoint.Zero, new WoWPoint(10f, 0f, 0f), null));
            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolveDungeonAdvanceStep(new WoWPoint(0f, 0f, 0f), WoWPoint.Zero, null));
        }

        [Fact]
        public void CorridorSegmentDistance_MeasuresPerpendicularOffsetAndEndpoints()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var to = new WoWPoint(10f, 0f, 0f);

            Assert.Equal(0f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(5f, 0f, 0f), from, to), 3);
            Assert.Equal(5f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(5f, 5f, 0f), from, to), 3);
            Assert.Equal(10f, SquadDungeonService.DistanceToCorridorSegment(new WoWPoint(-10f, 0f, 0f), from, to), 3);
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

            string[] isolated = SquadTankBridge.GetPullSpellPriority(WoWClass.Paladin, isolatedTarget: true);
            Assert.Equal("Avenger's Shield", isolated[0]);

            Assert.Equal(new[] { "Heroic Throw", "Taunt", "Shoot", "Throw" },
                SquadTankBridge.GetPullSpellPriority(WoWClass.Warrior, isolatedTarget: false));
            Assert.Empty(SquadTankBridge.GetPullSpellPriority(WoWClass.Hunter, isolatedTarget: true));
        }
    }
}
