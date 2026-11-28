using System;
using System.Collections.Generic;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
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

        [Fact]
        public void TankCombatTargetWeight_PenalizesDistantCastersOverNearbyMeleeMobs_ThrottlesTaunt_AndClearsDeadCoordinator()
        {
            double nearbyMeleeMob = SquadTankBridge.ComputeTankTargetBonus(
                isTargetingNonTankPartyMember: false,
                isTargetingHealer: false,
                alreadyInCombatWithSquad: true,
                distanceToTank: 4.5f,
                tankInCombat: true);
            double nearbyHealerPeel = SquadTankBridge.ComputeTankTargetBonus(
                isTargetingNonTankPartyMember: true,
                isTargetingHealer: true,
                alreadyInCombatWithSquad: true,
                distanceToTank: 6.0f,
                tankInCombat: true);
            double distantCasterHittingHealer = SquadTankBridge.ComputeTankTargetBonus(
                isTargetingNonTankPartyMember: true,
                isTargetingHealer: true,
                alreadyInCombatWithSquad: true,
                distanceToTank: 20.0f,
                tankInCombat: true);

            Assert.True(nearbyHealerPeel > nearbyMeleeMob);
            Assert.True(nearbyMeleeMob > distantCasterHittingHealer);

            Assert.False(SquadTankBridge.CanAttemptTauntPeel(lastGlobalTauntMs: 10000L, lastMobTauntMs: 0L, nowMs: 11500L));
            Assert.False(SquadTankBridge.CanAttemptTauntPeel(lastGlobalTauntMs: 5000L, lastMobTauntMs: 9000L, nowMs: 12000L));
            Assert.True(SquadTankBridge.CanAttemptTauntPeel(lastGlobalTauntMs: 5000L, lastMobTauntMs: 5000L, nowMs: 10000L));

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldClearCoordinatorTargetsWhenDead(isDead: true, isGhost: false));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldClearCoordinatorTargetsWhenDead(isDead: false, isGhost: true));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldClearCoordinatorTargetsWhenDead(isDead: false, isGhost: false));
        }

        [Fact]
        public void DungeonPolylineAdvanceStep_FollowsNavmeshCorridorAroundObstacle()
        {
            var from = new WoWPoint(2519.44f, 917.63f, 46.07f);
            var objective = new WoWPoint(2517.19f, 859.79f, 47.68f);
            var polyPath = new[]
            {
                from,
                new WoWPoint(2529.44f, 917.63f, 46.07f),
                new WoWPoint(2529.44f, 907.63f, 46.57f),
                new WoWPoint(2518.74f, 899.65f, 46.57f)
            };

            WoWPoint step = SquadDungeonService.ResolvePolylineAdvanceStep(
                from,
                polyPath,
                objective,
                Array.Empty<WoWPoint>());

            Assert.NotEqual(WoWPoint.Zero, step);
            Assert.Equal(2529.44f, step.X, 2);
            Assert.Equal(909.64f, step.Y, 2);
            Assert.Equal(8.0f, new WoWPoint(2529.44f, 917.63f, 46.07f).Distance(step), 2);
            Assert.True(step.Distance(new WoWPoint(2518.74f, 899.65f, 46.57f)) > 5f);
        }

        [Fact]
        public void DungeonPolylineAdvanceStep_RefusesBlockedSegmentAndFallsBackWhenEmpty()
        {
            var from = Origin;
            var objective = new WoWPoint(160f, 200f, 30f);
            var polyPath = new[]
            {
                from,
                new WoWPoint(110f, 200f, 30f),
                new WoWPoint(110f, 208f, 30f)
            };
            var trashOnTurn = new[] { new WoWPoint(110f, 206f, 30f) };

            Assert.Equal(WoWPoint.Zero, SquadDungeonService.ResolvePolylineAdvanceStep(
                from, polyPath, objective, trashOnTurn));

            WoWPoint besideTrash = SquadDungeonService.ResolvePolylineAdvanceStep(
                from, polyPath, objective, new[] { new WoWPoint(130f, 206f, 30f) });
            Assert.Equal(new WoWPoint(110f, 208f, 30f), besideTrash);

            WoWPoint fallback = SquadDungeonService.ResolvePolylineAdvanceStep(
                from, Array.Empty<WoWPoint>(), objective, Array.Empty<WoWPoint>());
            Assert.Equal(SquadDungeonService.ResolveDungeonAdvanceStep(from, objective, Array.Empty<WoWPoint>()), fallback);
        }

        [Fact]
        public void DungeonPullLosRecovery_AdvancesCloserAndBlacklistsAfterRepeatFailure()
        {
            Assert.True(SquadTankBridge.IsLineOfSightErrorMessage("Target not in line of sight"));
            Assert.True(SquadTankBridge.IsLineOfSightErrorMessage("You are not in LINE OF SIGHT"));
            Assert.True(SquadTankBridge.IsLineOfSightErrorMessage("Ziel nicht im Sichtfeld", "Ziel nicht im Sichtfeld"));
            Assert.False(SquadTankBridge.IsLineOfSightErrorMessage("Out of range.", "Ziel nicht im Sichtfeld"));
            Assert.False(SquadTankBridge.IsLineOfSightErrorMessage("Out of range."));
            Assert.False(SquadTankBridge.IsLineOfSightErrorMessage(null));

            Assert.Equal(
                SquadTankBridge.DungeonPullAction.CastOpener,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, false, long.MaxValue, false, 0, 22f));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.HoldAfterCast,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 300L, false, 0, 22f));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.HoldAfterCast,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 1600L, false, 0, 22f, isCasting: true));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.Approach,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 50L, true, 1, 20f));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.Approach,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 1300L, false, 1, 20f));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.CastOpener,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 1300L, true, 1, 5.0f));
            Assert.Equal(
                SquadTankBridge.DungeonPullAction.AbandonAndBlacklist,
                SquadTankBridge.EvaluateDungeonPullStep(true, true, true, 50L, true, 2, 5.0f));
        }

        [Fact]
        public void DungeonPullElevationPenalty_StronglyPrefersSameFloorOverElevatedLedge()
        {
            double olafFloor = SquadTankBridge.ComputeDungeonPullElevationPenalty(-48.5f, -49.0f);
            double baelogLedge = SquadTankBridge.ComputeDungeonPullElevationPenalty(-44.45f, -49.0f);

            Assert.Equal(0.0, olafFloor);
            Assert.True(baelogLedge < -150.0);
        }

        [Fact]
        public void BacklineCornerLos_MovesTowardTankWhenFar_YieldsNearTank_AndPrefersLosBreadcrumb()
        {
            Assert.Equal(
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.BacklineCornerAction.MoveTowardTankForLos,
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateEngagedNoLosBacklineAction(
                    engaged: true, inLineOfSpellSight: false, distanceToTank: 18f, hasTankPosition: true));
            Assert.Equal(
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.BacklineCornerAction.YieldToCombatRoutine,
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateEngagedNoLosBacklineAction(
                    engaged: true, inLineOfSpellSight: false, distanceToTank: 8f, hasTankPosition: true));
            Assert.Equal(
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.BacklineCornerAction.None,
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateEngagedNoLosBacklineAction(
                    engaged: true, inLineOfSpellSight: true, distanceToTank: 18f, hasTankPosition: true));
            Assert.Equal(
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.BacklineCornerAction.None,
                CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateEngagedNoLosBacklineAction(
                    engaged: false, inLineOfSpellSight: false, distanceToTank: 18f, hasTankPosition: true));

            var tankPos = new WoWPoint(100f, 100f, 0f);
            var mobPos = new WoWPoint(110f, 100f, 0f);
            var blindCrumb = new SquadBreadcrumb { X = 84f, Y = 100f, Z = 0f, MapId = 70U };
            var losCrumb = new SquadBreadcrumb { X = 88f, Y = 94f, Z = 0f, MapId = 70U };
            var crumbs = new[] { losCrumb, blindCrumb };

            WoWPoint chosen = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos,
                0f,
                mobPos,
                crumbs,
                70U,
                16f,
                0,
                1,
                lineOfSightCheck: (candidate, _) => candidate.Y < 99.0f);

            Assert.NotEqual(WoWPoint.Zero, chosen);
            Assert.True(chosen.Y < 99.0f);
            Assert.True(chosen.Distance(mobPos) >= 16f);

            WoWPoint unreachable = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos,
                0f,
                mobPos,
                crumbs,
                70U,
                16f,
                0,
                1,
                lineOfSightCheck: null,
                isReachableCheck: _ => false);
            Assert.Equal(WoWPoint.Zero, unreachable);
        }

        [Fact]
        public void DungeonObjectScripts_YieldToCombatAndLoot_AndApplyEscalatingCooldown()
        {
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(
                inDungeonMode: true,
                meInCombat: false,
                leaderInCombat: false,
                leaderFocusTargetGuid: 0UL,
                combatOrHealNeeded: false,
                hasNearbyThreat: false,
                lootNeeded: false,
                restOrProvisionNeeded: false));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, true, false, 0UL, false, false, false, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, true, 0UL, false, false, false, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, false, 999UL, false, false, false, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, false, 0UL, true, false, false, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, false, 0UL, false, true, false, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, false, 0UL, false, false, true, false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.CanRunDungeonObjectScripts(true, false, false, 0UL, false, false, false, true));

            var service = new SquadDungeonService();
            const ulong altarGuid = 0x130511UL;

            service.RecordObjectScriptSuccess(altarGuid, 10_000L);
            Assert.True(service.IsObjectSuppressedAt(altarGuid, 14_999L));
            Assert.False(service.IsObjectSuppressedAt(altarGuid, 15_000L));

            service.RecordObjectScriptSuccess(altarGuid, 20_000L);
            Assert.True(service.IsObjectSuppressedAt(altarGuid, 79_999L));
            Assert.False(service.IsObjectSuppressedAt(altarGuid, 80_000L));

            service.RecordObjectScriptSuccess(altarGuid, 90_000L);
            Assert.True(service.IsObjectSuppressedAt(altarGuid, 389_999L));

            service.ClearObjectScriptTracking();
            Assert.False(service.IsObjectSuppressedAt(altarGuid, 95_000L));

            service.RecordObjectScriptTimeout(altarGuid, 100_000L);
            Assert.True(service.IsObjectSuppressedAt(altarGuid, 129_999L));
        }

        [Fact]
        public void DungeonChestLooting_HandlesUnlockedFlags_AndDelegatesOnlyToEffectiveTank()
        {
            Assert.True(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Ready,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.None));
            Assert.True(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Ready,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.NoDespawn));

            Assert.False(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Active,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.None));
            Assert.False(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Destroyed,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.None));
            Assert.False(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Ready,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.InUse));
            Assert.False(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Ready,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.NotSelectable));
            Assert.False(Styx.WoWInternals.WoWObjects.WoWGameObject.CanLootUnlockedChest(
                Styx.WoWInternals.WoWObjects.WoWGameObjectState.Ready,
                Styx.WoWInternals.WoWObjects.GameObjectFlags.ConditionInteract));

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsDesignatedDungeonChestLooter(
                SquadBotMode.Dungeon, isInsideDungeonMap: true, SquadRole.TankLeader, localGuid: 100UL, effectiveTankGuid: 100UL));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsDesignatedDungeonChestLooter(
                SquadBotMode.Dungeon, isInsideDungeonMap: true, SquadRole.DpsRanged, localGuid: 200UL, effectiveTankGuid: 100UL));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsDesignatedDungeonChestLooter(
                SquadBotMode.Dungeon, isInsideDungeonMap: true, SquadRole.Healer, localGuid: 300UL, effectiveTankGuid: 0UL));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsDesignatedDungeonChestLooter(
                SquadBotMode.Dungeon, isInsideDungeonMap: false, SquadRole.DpsRanged, localGuid: 200UL, effectiveTankGuid: 100UL));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsDesignatedDungeonChestLooter(
                SquadBotMode.OpenWorldGrind, isInsideDungeonMap: false, SquadRole.DpsRanged, localGuid: 200UL, effectiveTankGuid: 100UL));
        }

        [Fact]
        public void ComputeDungeonStandoffPosition_PrefersReachableLoSGeometricOverBlindBreadcrumb()
        {
            var tankPos = new WoWPoint(100f, 100f, 0f);
            var targetPos = new WoWPoint(110f, 100f, 0f);
            var breadcrumbs = new[]
            {
                new SquadBreadcrumb { MapId = 70U, X = 100f, Y = 88f, Z = 0f, TimestampUtcMs = 1000L }
            };

            WoWPoint chosen = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos,
                tankFacingRadians: 0f,
                mapId: 70U,
                standoffDistance: 16f,
                backlineSlotIndex: 0,
                totalBacklineCount: 1,
                breadcrumbs: breadcrumbs,
                focusTargetPos: targetPos,
                lineOfSightCheck: (from, _) => from.Y > 92f,
                isReachableCheck: _ => true);

            Assert.True(chosen.Y > 92f);
            Assert.NotEqual(WoWPoint.Zero, chosen);
        }

        [Fact]
        public void ComputeDungeonStandoffPosition_DeduplicatesRepeatedProjectedDestinations()
        {
            var tankPos = new WoWPoint(100f, 100f, 0f);
            var targetPos = new WoWPoint(100f, 110f, 0f);
            var breadcrumbs = new[]
            {
                new SquadBreadcrumb { MapId = 70U, X = 80f, Y = 100f, Z = 0f, TimestampUtcMs = 1000L },
                new SquadBreadcrumb { MapId = 70U, X = 82f, Y = 100f, Z = 0f, TimestampUtcMs = 2000L },
                new SquadBreadcrumb { MapId = 70U, X = 84f, Y = 100f, Z = 0f, TimestampUtcMs = 3000L }
            };

            var checkedPoints = new List<WoWPoint>();
            WoWPoint chosen = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos,
                tankFacingRadians: 0f,
                mapId: 70U,
                standoffDistance: 16f,
                backlineSlotIndex: 0,
                totalBacklineCount: 1,
                breadcrumbs: breadcrumbs,
                focusTargetPos: targetPos,
                lineOfSightCheck: null,
                isReachableCheck: pt =>
                {
                    checkedPoints.Add(pt);
                    return false;
                });

            Assert.Equal(WoWPoint.Zero, chosen);
            Assert.Equal(2, checkedPoints.Count);
            Assert.Equal(1, checkedPoints.Count(p => p == new WoWPoint(84f, 100f, 0f)));
            Assert.Equal(1, checkedPoints.Count(p => p == new WoWPoint(100f, 84f, 0f)));
        }
    }

    public sealed class DungeonPullBlacklistStateTests : IDisposable
    {
        public DungeonPullBlacklistStateTests()
        {
            SquadTankBridge.ResetPullStateForTests();
        }

        public void Dispose()
        {
            SquadTankBridge.ResetPullStateForTests();
        }

        [Fact]
        public void TempBlacklist_BlocksUntilExpiryAndClearsOnReset()
        {
            const ulong baelogGuid = 0xDEAD0001UL;
            SquadTankBridge.BlacklistPullTarget(baelogGuid, 10_000L);
            Assert.True(SquadTankBridge.IsTemporarilyPullBlacklisted(baelogGuid, 17_999L));
            Assert.False(SquadTankBridge.IsTemporarilyPullBlacklisted(baelogGuid, 18_000L));
            SquadTankBridge.BlacklistPullTarget(baelogGuid, 20_000L);
            Assert.True(SquadTankBridge.IsTemporarilyPullBlacklisted(baelogGuid, 21_000L));
            SquadTankBridge.ResetPullStateForTests();
            Assert.False(SquadTankBridge.IsTemporarilyPullBlacklisted(baelogGuid, 21_000L));
            Assert.True(SquadTankBridge.ShouldRejectIdlePullTarget(isTemporarilyBlacklisted: true, targetEngaged: false));
            Assert.False(SquadTankBridge.ShouldRejectIdlePullTarget(isTemporarilyBlacklisted: true, targetEngaged: true));
        }

        [Fact]
        public void CorridorBlock_SlowTwoSecondTicksDoNotResetEightSecondGraceWindow()
        {
            uint blockedMap = 0U;
            WoWPoint blockedObj = WoWPoint.Zero;
            long lastEval = 0L;
            long blockedSince = 0L;
            long now = 5_000_000L;
            var reveloshPos = new WoWPoint(-233.5f, 171.1f, -44.6f);

            bool firstHold = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                70U,
                reveloshPos,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                now,
                out bool newlyBlocked,
                out bool graceExpired);
            Assert.True(firstHold);
            Assert.True(newlyBlocked);
            Assert.False(graceExpired);

            for (int tick = 1; tick <= 4; tick++)
            {
                now += 1700L;
                bool hold = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonCorridorBlock(
                    bypassTrashGrace: false,
                    hasTrashOnSegment: true,
                    70U,
                    reveloshPos,
                    ref blockedMap,
                    ref blockedObj,
                    ref lastEval,
                    ref blockedSince,
                    now,
                    out bool restarted,
                    out bool tickExpired);
                Assert.True(hold);
                Assert.False(restarted);
                Assert.False(tickExpired);
            }

            now += 1700L;
            bool holdAtEnd = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                70U,
                reveloshPos,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                now,
                out _,
                out bool expiredAtEnd);
            Assert.False(holdAtEnd);
            Assert.True(expiredAtEnd);
            Assert.Equal(0L, blockedSince);
        }

        [Fact]
        public void DungeonTrashScan_MatchesPullEligibilityAndExcludesUnpullableUnits()
        {
            float radius = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.DungeonTrashScanRadius;

            Assert.Equal(35f, radius);
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: true, inCombat: false, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: false,
                distance: 34.5f, scanRadius: radius));

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: false, inCombat: true, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: false,
                distance: 15f, scanRadius: radius));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: true, inCombat: false, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: false,
                distance: 38f, scanRadius: radius));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: false, inCombat: false, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: false,
                distance: 12f, scanRadius: radius));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: false, attackable: true,
                isHostile: true, inCombat: false, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: false,
                distance: 12f, scanRadius: radius));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: true, inCombat: false, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: true,
                distance: 12f, scanRadius: radius));

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsEligibleDungeonTrashUnit(
                isValid: true, isDead: false, canSelect: true, attackable: true,
                isHostile: true, inCombat: true, isFriendly: false, isPlayer: false,
                isCritter: false, isNonCombatPet: false, hasControllingPlayer: false,
                inPullBlackspot: true,
                distance: 12f, scanRadius: radius));
        }

        [Fact]
        public void DungeonObjectTimeout_SuspendsWhileCastingOrChanneling()
        {
            long started = 100_000L;
            long afterCutoff = started + SquadDungeonService.ObjectRunningTimeoutMs;

            Assert.False(SquadDungeonService.ShouldTimeOutDungeonObjectHandler(started, afterCutoff, isCastingOrChanneling: true));
            Assert.True(SquadDungeonService.ShouldTimeOutDungeonObjectHandler(started, afterCutoff, isCastingOrChanneling: false));
            Assert.False(SquadDungeonService.ShouldTimeOutDungeonObjectHandler(started, afterCutoff - 1L, isCastingOrChanneling: false));
        }

        [Fact]
        public void PullAndLeaderNavCaches_ReuseWithinTtlAndMoveToleranceOnly()
        {
            var origin = new WoWPoint(-250f, 134f, -47f);
            var target = new WoWPoint(-233.5f, 171.1f, -44.6f);

            Assert.True(SquadTankBridge.ShouldReusePullNavCache(
                70U, origin, target, 35f, 10_000L,
                70U, new WoWPoint(-249.5f, 134f, -47f), target, 35f, 10_400L));
            Assert.False(SquadTankBridge.ShouldReusePullNavCache(
                70U, origin, target, 35f, 10_000L,
                70U, origin, target, 35f, 10_500L));
            Assert.False(SquadTankBridge.ShouldReusePullNavCache(
                70U, origin, target, 35f, 10_000L,
                70U, new WoWPoint(-247f, 134f, -47f), target, 35f, 10_100L));
            Assert.False(SquadTankBridge.ShouldReusePullNavCache(
                70U, origin, target, 35f, 10_000L,
                109U, origin, target, 35f, 10_100L));

            Assert.True(SquadDungeonService.ShouldReuseLeaderTargetCache(
                10_000L, 70U, origin, 6910U, PoiType.None, WoWPoint.Zero, 0, target,
                10_250L, 70U, new WoWPoint(-249f, 134f, -47f), 6910U, PoiType.None, WoWPoint.Zero, 0));
            Assert.False(SquadDungeonService.ShouldReuseLeaderTargetCache(
                10_000L, 70U, origin, 6910U, PoiType.None, WoWPoint.Zero, 0, target,
                10_400L, 70U, origin, 6910U, PoiType.None, WoWPoint.Zero, 0));
            Assert.False(SquadDungeonService.ShouldReuseLeaderTargetCache(
                10_000L, 70U, origin, 6910U, PoiType.None, WoWPoint.Zero, 0, target,
                10_100L, 70U, origin, 7228U, PoiType.None, WoWPoint.Zero, 0));
            Assert.False(SquadDungeonService.ShouldReuseLeaderTargetCache(
                10_000L, 70U, origin, 6910U, PoiType.None, WoWPoint.Zero, 0, target,
                10_100L, 70U, new WoWPoint(-234f, 170f, -44.6f), 6910U, PoiType.None, WoWPoint.Zero, 0));
        }
    }
}
