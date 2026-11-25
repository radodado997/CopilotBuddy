using System;
using System.Collections.Generic;
using CopilotBuddy.Bots.PartyGrind;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class RequeueAndRestGuardTests
    {
        public RequeueAndRestGuardTests()
        {
            SquadHealBridge.ResetHealCastHistory();
        }

        [Fact]
        public void ResetPendingOutside_IsAbandonedOnlyAfterTheTimeout()
        {
            long now = 1_000_000L;

            Assert.False(SquadDungeonService.ShouldAbandonResetPendingOutside(
                resetIssued: false, pendingSinceUtcMs: now, nowUtcMs: now + 1000L));
            Assert.False(SquadDungeonService.ShouldAbandonResetPendingOutside(
                resetIssued: false,
                pendingSinceUtcMs: now,
                nowUtcMs: now + SquadDungeonService.ResetPendingOutsideTimeoutMs - 1L));
            Assert.True(SquadDungeonService.ShouldAbandonResetPendingOutside(
                resetIssued: false,
                pendingSinceUtcMs: now,
                nowUtcMs: now + SquadDungeonService.ResetPendingOutsideTimeoutMs));
        }

        [Fact]
        public void ResetPendingOutside_IsKeptWhileTheResetSucceeds()
        {
            long now = 1_000_000L;

            Assert.False(SquadDungeonService.ShouldAbandonResetPendingOutside(
                resetIssued: true,
                pendingSinceUtcMs: now,
                nowUtcMs: now + SquadDungeonService.ResetPendingOutsideTimeoutMs * 5L));
            Assert.False(SquadDungeonService.ShouldAbandonResetPendingOutside(
                resetIssued: false, pendingSinceUtcMs: 0L, nowUtcMs: now + 10_000_000L));
        }

        [Theory]
        [InlineData(2, 10f, 0L, false)]
        [InlineData(3, 10f, 0L, false)]
        [InlineData(4, 40f, 0L, true)]
        [InlineData(5, 40f, 0L, true)]
        [InlineData(4, 90f, 0L, false)]
        [InlineData(6, 5f, 0L, false)]
        [InlineData(4, 5f, SquadTankBridge.MaxPackPullHoldMs, false)]
        public void PackPull_IsHeldOnlyForModeratePacksOnLowMana(
            int packSize, float lowestPartyManaPercent, long holdElapsedMs, bool expected)
        {
            Assert.Equal(expected, SquadTankBridge.IsPackPullTooRisky(
                packSize, lowestPartyManaPercent, holdElapsedMs));
        }

        [Theory]
        [InlineData(4, 10f, 5, 10f, true)]
        [InlineData(3, 10f, 7, 10f, true)]
        [InlineData(4, 10f, 4, 10f, false)]
        [InlineData(5, 10f, 6, 10f, false)]
        [InlineData(2, 10f, 3, 10f, false)]
        [InlineData(3, 30f, 7, 10f, false)]
        [InlineData(3, 21f, 7, 10f, true)]
        public void PullTarget_SwapsToASmallerPackWithinADetour(
            int candidatePack, float candidateDistance, int currentPack, float currentDistance, bool expected)
        {
            Assert.Equal(expected, SquadTankBridge.IsBetterPullCandidate(
                candidatePack, candidateDistance, currentPack, currentDistance));
        }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(false, false, true, true)]
        [InlineData(true, true, true, true)]
        public void Rest_IsDeferredWhenNearbyThreatOrCombatNeedsTheHealer(
            bool hostileAggroNearby, bool hostileInMeleeRange, bool partyMemberInCombat, bool expected)
        {
            Assert.Equal(expected, PartyGrindBot.ShouldDeferRest(
                hostileAggroNearby, hostileInMeleeRange, partyMemberInCombat));
        }

        [Fact]
        public void WorldTransition_SuppressesFalseDeathsForAGracePeriod()
        {
            long now = 5_000_000L;

            Assert.True(PartyGrindBot.IsTransientWorldTransition(isInWorld: false, mapChangeUtcMs: 0L, nowUtcMs: now));
            Assert.True(PartyGrindBot.IsTransientWorldTransition(isInWorld: true, mapChangeUtcMs: now - 500L, nowUtcMs: now));
            Assert.False(PartyGrindBot.IsTransientWorldTransition(isInWorld: true, mapChangeUtcMs: now - 5000L, nowUtcMs: now));
            Assert.False(PartyGrindBot.IsTransientWorldTransition(isInWorld: true, mapChangeUtcMs: 0L, nowUtcMs: now));
        }

        [Fact]
        public void ResetDance_KicksOnlyWhileSomeoneElseIsInTheParty()
        {
            Assert.False(SquadDungeonService.ShouldKickPartyMembers(false, 4));
            Assert.False(SquadDungeonService.ShouldKickPartyMembers(true, 0));
            Assert.True(SquadDungeonService.ShouldKickPartyMembers(true, 4));
        }

        [Fact]
        public void ResetDance_GetsALongerBudgetThanAPlainReset()
        {
            Assert.Equal(SquadDungeonService.ResetPendingOutsideTimeoutMs, SquadDungeonService.ResolveResetAbandonTimeoutMs(grouped: false));
            Assert.Equal(SquadDungeonService.ResetDanceTimeoutMs, SquadDungeonService.ResolveResetAbandonTimeoutMs(grouped: true));
            Assert.True(SquadDungeonService.ResetDanceTimeoutMs > SquadDungeonService.ResetPendingOutsideTimeoutMs);

            long now = 4_000_000L;
            long pendingSince = now - SquadDungeonService.ResetPendingOutsideTimeoutMs - 1000L;
            Assert.True(SquadDungeonService.ShouldAbandonResetPendingOutside(false, pendingSince, now));
            Assert.False(SquadDungeonService.ShouldAbandonResetPendingOutside(
                false, pendingSince, now, SquadDungeonService.ResetDanceTimeoutMs));
        }

        [Fact]
        public void PullManaGate_WaitsForTheHealerAndThenReleasesThePull()
        {
            var leader = new SquadLeaderState();
            leader.Members.Add(new SquadMemberSummary { Guid = 1, CharacterName = "Tank", IsConnected = true, Role = SquadRole.TankLeader, Class = WoWClass.Paladin, PowerType = WoWPowerType.Mana, PowerPercent = 90f, HealthPercent = 100f });
            leader.Members.Add(new SquadMemberSummary { Guid = 2, CharacterName = "Healer", IsConnected = true, Role = SquadRole.Healer, Class = WoWClass.Priest, PowerType = WoWPowerType.Mana, PowerPercent = 22f, HealthPercent = 100f });
            leader.Members.Add(new SquadMemberSummary { Guid = 3, CharacterName = "Mage", IsConnected = true, Role = SquadRole.DpsRanged, Class = WoWClass.Mage, PowerType = WoWPowerType.Mana, PowerPercent = 80f, HealthPercent = 100f });

            Assert.Equal(22f, SquadTankBridge.LowestHealerManaPercent(leader), 3);
            Assert.True(SquadTankBridge.IsManaHoldBlockingPull(22f, manaHoldExpired: false));
            Assert.False(SquadTankBridge.IsManaHoldBlockingPull(22f, manaHoldExpired: true));
            Assert.False(SquadTankBridge.IsManaHoldBlockingPull(SquadTankBridge.PullHealerManaFloor, manaHoldExpired: false));
        }

        [Fact]
        public void PullManaGate_FallsBackToEveryManaUserWithoutAHealerRole()
        {
            var leader = new SquadLeaderState();
            leader.Members.Add(new SquadMemberSummary { Guid = 1, CharacterName = "Druid", IsConnected = true, Role = SquadRole.DpsMelee, Class = WoWClass.Druid, PowerType = WoWPowerType.Energy, ManaPercent = 31f, PowerPercent = 100f, HealthPercent = 100f });
            leader.Members.Add(new SquadMemberSummary { Guid = 2, CharacterName = "Warlock", IsConnected = true, Role = SquadRole.DpsRanged, Class = WoWClass.Warlock, PowerType = WoWPowerType.Mana, PowerPercent = 74f, HealthPercent = 100f });

            Assert.Equal(31f, SquadTankBridge.LowestHealerManaPercent(leader), 3);
            Assert.Equal(100f, SquadTankBridge.LowestHealerManaPercent(null), 3);
        }

        [Fact]
        public void DeathDetection_NeedsAPersistentFlagOutsideTransitions()
        {
            long now = 7_000_000L;

            Assert.False(PartyGrindBot.ShouldConfirmDeath(rawDead: false, transientTransition: false, firstObservedUtcMs: now - 5000L, nowUtcMs: now));
            Assert.False(PartyGrindBot.ShouldConfirmDeath(rawDead: true, transientTransition: true, firstObservedUtcMs: now - 5000L, nowUtcMs: now));
            Assert.False(PartyGrindBot.ShouldConfirmDeath(rawDead: true, transientTransition: false, firstObservedUtcMs: 0L, nowUtcMs: now));
            Assert.False(PartyGrindBot.ShouldConfirmDeath(rawDead: true, transientTransition: false, firstObservedUtcMs: now, nowUtcMs: now + 300L));
            Assert.True(PartyGrindBot.ShouldConfirmDeath(rawDead: true, transientTransition: false, firstObservedUtcMs: now, nowUtcMs: now + 2000L));
        }

        [Fact]
        public void WorldReentry_AlsoCountsAsATransition()
        {
            long now = 8_000_000L;

            Assert.True(PartyGrindBot.IsTransientWorldTransition(isInWorld: true, mapChangeUtcMs: 0L, reentryGraceUntilUtcMs: now + 2000L, nowUtcMs: now));
            Assert.False(PartyGrindBot.IsTransientWorldTransition(isInWorld: true, mapChangeUtcMs: 0L, reentryGraceUntilUtcMs: now - 2000L, nowUtcMs: now));
            Assert.True(PartyGrindBot.IsTransientWorldTransition(isInWorld: false, mapChangeUtcMs: 0L, reentryGraceUntilUtcMs: 0L, nowUtcMs: now));
        }

        [Fact]
        public void MidWipe_OnlyTripsWhileSomeoneIsLeftToSave()
        {
            Assert.False(SquadHealBridge.IsSquadMidWipe(null));
            Assert.False(SquadHealBridge.IsSquadMidWipe(new SquadLeaderState()));

            Assert.False(SquadHealBridge.IsSquadMidWipe(Leader(
                (true, 0f), (false, 100f), (false, 92f))));
            Assert.True(SquadHealBridge.IsSquadMidWipe(Leader(
                (true, 0f), (false, 42f), (false, 30f), (false, 88f))));
            Assert.False(SquadHealBridge.IsSquadMidWipe(Leader(
                (true, 0f), (true, 0f), (true, 0f), (false, 55f))));
            Assert.False(SquadHealBridge.IsSquadMidWipe(Leader(
                (true, 0f), (false, 100f), (false, 96f))));
        }

        private static SquadLeaderState Leader(params (bool IsDead, float HealthPercent)[] members)
        {
            var state = new SquadLeaderState();
            for (int i = 0; i < members.Length; i++)
            {
                state.Members.Add(new SquadMemberSummary
                {
                    Guid = (ulong)(1000 + i),
                    CharacterName = "Member" + i,
                    IsConnected = true,
                    IsDead = members[i].IsDead,
                    HealthPercent = members[i].HealthPercent
                });
            }

            return state;
        }

        [Fact]
        public void HealCastHistory_BlocksTheSameSpellOnTheSameTargetWithinTheWindow()
        {
            const ulong tank = 0x3BBB3;
            long now = 9_000_000L;

            SquadHealBridge.RecordHealCast(tank, "Renew", now);

            Assert.True(SquadHealBridge.IsHealCastDuplicate(tank, "Renew", now + 500L));
            Assert.True(SquadHealBridge.IsHealCastDuplicate(tank, "renew", now + SquadHealBridge.HealDuplicateWindowMs - 1L));
            Assert.False(SquadHealBridge.IsHealCastDuplicate(tank, "Renew", now + SquadHealBridge.HealDuplicateWindowMs + 1L));
            Assert.False(SquadHealBridge.IsHealCastDuplicate(tank, "Flash Heal", now + 500L));
            Assert.False(SquadHealBridge.IsHealCastDuplicate(0x3BBB4, "Renew", now + 500L));
        }

        [Fact]
        public void HealCastHistory_IgnoresUnknownOrEmptySpells()
        {
            const ulong tank = 0x3BBB3;
            long now = 9_000_000L;

            SquadHealBridge.RecordHealCast(tank, null, now);
            SquadHealBridge.RecordHealCast(0, "Renew", now);

            Assert.False(SquadHealBridge.IsHealCastDuplicate(tank, "Renew", now));
            Assert.False(SquadHealBridge.IsHealCastDuplicate(tank, null, now));
            Assert.False(SquadHealBridge.IsHealCastDuplicate(0, "Renew", now));
        }

        [Fact]
        public void LowManaPriest_PrefersCheapHealsAndStopsToppingOff()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Flash Heal", "Renew", "Heal", "Lesser Heal", "Power Word: Shield"
            };

            var (moderateSpell, _) = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 55.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: false,
                hasAuraFromHealer: _ => false,
                hasSpell: known.Contains,
                canCastSpell: known.Contains,
                healerManaPercent: 20.0);
            Assert.Equal("Renew", moderateSpell);

            var (healthySpell, _) = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 78.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: false,
                hasAuraFromHealer: _ => false,
                hasSpell: known.Contains,
                canCastSpell: known.Contains,
                healerManaPercent: 20.0);
            Assert.Null(healthySpell);

            var (criticalSpell, _) = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 22.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: false,
                hasAuraFromHealer: _ => false,
                hasSpell: known.Contains,
                canCastSpell: known.Contains,
                healerManaPercent: 20.0);
            Assert.Equal("Flash Heal", criticalSpell);
        }

        [Fact]
        public void FullManaPriest_KeepsTheExistingLadder()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Flash Heal", "Renew", "Heal", "Lesser Heal", "Power Word: Shield"
            };

            var (combatTank, _) = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 60.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: true,
                hasAuraFromHealer: aura => aura == "Renew",
                hasSpell: known.Contains,
                canCastSpell: known.Contains,
                healerManaPercent: 92.0);
            Assert.Equal("Heal", combatTank);
        }
        [Fact]
        public void ResetDance_AlsoOpensForTheFollowerThatBecameTheLeader()
        {
            // The local flag always wins (coordinator or a follower that ran the reset itself).
            Assert.True(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: true, isCoordinator: true, leaderResetPendingOutside: false, localIsPartyLeader: false));
            Assert.True(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: true, isCoordinator: false, leaderResetPendingOutside: false, localIsPartyLeader: false));

            // A coordinator never derives the gate from the broadcast flag.
            Assert.False(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: true, leaderResetPendingOutside: true, localIsPartyLeader: true));

            // A follower only dances when it is the instance leader.
            Assert.True(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: false, leaderResetPendingOutside: true, localIsPartyLeader: true));
            Assert.False(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: false, leaderResetPendingOutside: true, localIsPartyLeader: false));
            Assert.False(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: false, leaderResetPendingOutside: false, localIsPartyLeader: true));

            // A broadcast request that was already danced for stays closed until it is cleared.
            Assert.False(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: false, leaderResetPendingOutside: true,
                localIsPartyLeader: true, followerRequestAlreadyHandled: true));
            Assert.True(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: true, isCoordinator: false, leaderResetPendingOutside: true,
                localIsPartyLeader: true, followerRequestAlreadyHandled: true));
            Assert.True(SquadDungeonService.ShouldEnterResetDance(
                resetPendingOutside: false, isCoordinator: false, leaderResetPendingOutside: true,
                localIsPartyLeader: true, followerRequestAlreadyHandled: false));
        }

        [Fact]
        public void Provisioning_WaterEmergencyOnlyKeepsTheWaterHandOff()
        {
            // The dry healer's water hand-off survives the release.
            Assert.True(SquadCoordinatorService.IsWaterOnlyEmergencyAssignment(
                assignedDrink: true, assignedFood: false, assignedHealthstone: false,
                activeTradeTarget: "Ksionc", recipient: "ksionc"));
            // Food, healthstone and other-recipient assignments do not.
            Assert.False(SquadCoordinatorService.IsWaterOnlyEmergencyAssignment(
                assignedDrink: true, assignedFood: true, assignedHealthstone: false,
                activeTradeTarget: "Ksionc", recipient: "Ksionc"));
            Assert.False(SquadCoordinatorService.IsWaterOnlyEmergencyAssignment(
                assignedDrink: false, assignedFood: false, assignedHealthstone: true,
                activeTradeTarget: "Ksionc", recipient: "Ksionc"));
            Assert.False(SquadCoordinatorService.IsWaterOnlyEmergencyAssignment(
                assignedDrink: true, assignedFood: false, assignedHealthstone: false,
                activeTradeTarget: "Bedoes", recipient: "Ksionc"));
        }

        [Fact]
        public void FollowerResetDance_RequiresFreshExitEvidenceForEveryOtherGroupMember()
        {
            var leader = new SquadLeaderState
            {
                DungeonResetExitConfirmedGuids = new[] { 1UL, 2UL },
                DungeonResetExitEvidenceValidUntilUtcMs = 5000L
            };

            Assert.True(SquadDungeonService.HasConfirmedFollowerGroupExit(leader, new[] { 1UL, 2UL, 3UL }, 3UL, 5000L));
            Assert.False(SquadDungeonService.HasConfirmedFollowerGroupExit(leader, new[] { 1UL, 2UL, 4UL }, 3UL, 5000L));
            Assert.False(SquadDungeonService.HasConfirmedFollowerGroupExit(leader, new[] { 1UL, 2UL, 3UL }, 3UL, 5001L));
        }

        [Fact]
        public void Provisioning_WaterOnlyModeNeedsAWaterCapableIdleMage()
        {
            var mage = new SquadMemberState
            {
                CharacterName = "Sentino",
                Class = WoWClass.Mage,
                CanProvideMageFood = true,
                CanProvideMageDrink = true
            };
            Assert.True(SquadCoordinatorService.CanProvideInWaterOnlyMode(mage));

            mage.IsInCombat = true;
            Assert.False(SquadCoordinatorService.CanProvideInWaterOnlyMode(mage));

            mage.IsInCombat = false;
            mage.CanProvideMageDrink = false; // food-only mage cannot serve a water emergency
            Assert.False(SquadCoordinatorService.CanProvideInWaterOnlyMode(mage));

            mage.CanProvideMageDrink = true;
            mage.IsDead = true;
            Assert.False(SquadCoordinatorService.CanProvideInWaterOnlyMode(mage));
        }

        [Fact]
        public void Provisioning_DrinkNeedSurvivesTheBagSlotAndCombatGuards()
        {
            var settings = SquadSettings.Instance;

            var priest = new SquadMemberSummary
            {
                Class = WoWClass.Priest,
                DrinkCount = 0,
                FreeBagSlots = 4,
                WantsDrink = false // suppressed by the need evaluation guard
            };
            Assert.True(SquadCoordinatorService.SquadWantsDrink(priest, settings));

            priest.DrinkCount = settings.MinMemberDrinkStack;
            Assert.False(SquadCoordinatorService.SquadWantsDrink(priest, settings));

            priest.DrinkCount = 0;
            priest.FreeBagSlots = 1; // cannot take a trade anyway
            Assert.False(SquadCoordinatorService.SquadWantsDrink(priest, settings));

            var warrior = new SquadMemberSummary { Class = WoWClass.Warrior, DrinkCount = 0, FreeBagSlots = 8 };
            Assert.False(SquadCoordinatorService.SquadWantsDrink(warrior, settings));

            // An explicitly reported need is always honoured.
            Assert.True(SquadCoordinatorService.SquadWantsDrink(
                new SquadMemberSummary { Class = WoWClass.Mage, DrinkCount = 99, FreeBagSlots = 8, WantsDrink = true },
                settings));
        }

        [Fact]
        public void Provisioning_GivesADryHealerWaterBetweenPulls()
        {
            var settings = SquadSettings.Instance;
            var mage = new SquadMemberState
            {
                CharacterName = "Sentino",
                Class = WoWClass.Mage,
                Role = SquadRole.DpsRanged,
                CanProvideMageDrink = true,
                CanProvideMageFood = true,
                FreeBagSlots = 6
            };
            var healer = new SquadMemberState
            {
                CharacterName = "Ksionc",
                Class = WoWClass.Priest,
                Role = SquadRole.Healer,
                WantsDrink = true,
                ManaPercent = 12f,
                FreeBagSlots = 5
            };

            var roster = new List<SquadMemberState> { mage, healer };
            Assert.Equal("Ksionc", SquadCoordinatorService.SelectDryHealerWaterRecipient(roster, settings));

            healer.ManaPercent = 90f;
            Assert.Null(SquadCoordinatorService.SelectDryHealerWaterRecipient(roster, settings));

            healer.ManaPercent = 12f;
            healer.IsInCombat = true;
            Assert.Null(SquadCoordinatorService.SelectDryHealerWaterRecipient(roster, settings));

            healer.IsInCombat = false;
            mage.IsInCombat = true;
            Assert.Null(SquadCoordinatorService.SelectDryHealerWaterRecipient(roster, settings));

            mage.IsInCombat = false;
            mage.CanProvideMageDrink = false;
            Assert.Null(SquadCoordinatorService.SelectDryHealerWaterRecipient(roster, settings));
        }

        [Fact]
        public void PullManaGate_DruidManaIsNeverReplacedByPower()
        {
            var druid = new SquadMemberSummary
            {
                Class = WoWClass.Druid,
                PowerType = WoWPowerType.Energy,
                ManaPercent = 0f,
                PowerPercent = 100f
            };
            Assert.Equal(0f, SquadTankBridge.ReadMemberManaPercent(druid), 3);

            var leader = new SquadLeaderState();
            leader.Members.Add(new SquadMemberSummary
            {
                Guid = 1,
                CharacterName = "Druid",
                IsConnected = true,
                Role = SquadRole.DpsMelee,
                Class = WoWClass.Druid,
                PowerType = WoWPowerType.Energy,
                ManaPercent = 0f,
                PowerPercent = 100f
            });
            Assert.Equal(0f, SquadTankBridge.LowestHealerManaPercent(leader), 3);

            // Missing telemetry must read as unknown, not as an empty pool.
            leader.Members[0].HasTelemetry = false;
            Assert.Equal(100f, SquadTankBridge.LowestHealerManaPercent(leader), 3);
        }

        [Fact]
        public void PullManaGate_ExpiryIsVisibleToEveryStaticCaller()
        {
            var leader = new SquadLeaderState
            {
                Phase = SquadPhase.DungeonClearing,
                X = 100f,
                Y = 100f,
                Z = 0f,
                MapId = 0
            };
            leader.Members.Add(new SquadMemberSummary
            {
                Guid = 1,
                CharacterName = "Healer",
                IsConnected = true,
                Role = SquadRole.Healer,
                Class = WoWClass.Priest,
                PowerType = WoWPowerType.Mana,
                PowerPercent = 10f,
                HealthPercent = 100f,
                MapId = 0,
                X = 105f,
                Y = 100f,
                Z = 0f
            });

            Assert.False(SquadTankBridge.CanInitiatePull(leader, 28f, manaHoldExpired: false));
            Assert.True(SquadTankBridge.CanInitiatePull(leader, 28f, manaHoldExpired: true));
        }

        [Theory]
        [InlineData(24f, 80f, true)]
        [InlineData(25f, 80f, false)]
        [InlineData(24f, 79f, false)]
        public void PullManaGate_ExpiredHoldOnlyExcusesRestForHealthyMembersWithDryHealer(
            float mana, float health, bool canPull)
        {
            var leader = new SquadLeaderState { Phase = SquadPhase.DungeonClearing };
            leader.Members.Add(new SquadMemberSummary
            {
                IsConnected = true,
                Role = SquadRole.Healer,
                Class = WoWClass.Priest,
                PowerType = WoWPowerType.Mana,
                PowerPercent = mana,
                HealthPercent = health,
                NeedsRest = true
            });

            Assert.Equal(canPull, SquadTankBridge.CanInitiatePull(leader, 28f, manaHoldExpired: true));
        }

        [Fact]
        public void Rest_YieldsToAPullOnlyWhenTheHoldExpiredOnADryHealer()
        {
            Assert.False(PartyGrindBot.ShouldYieldRestForPull(manaHoldExpired: false, localIsTank: true, healerManaPercent: 10f));
            Assert.False(PartyGrindBot.ShouldYieldRestForPull(manaHoldExpired: true, localIsTank: false, healerManaPercent: 10f));
            Assert.False(PartyGrindBot.ShouldYieldRestForPull(manaHoldExpired: true, localIsTank: true, healerManaPercent: 40f));
            Assert.True(PartyGrindBot.ShouldYieldRestForPull(manaHoldExpired: true, localIsTank: true, healerManaPercent: 20f));
        }

        [Fact]
        public void RoutineHealHandoff_StaysOffNonTankTargetsAtLowMana()
        {
            Assert.False(SquadHealBridge.IsRoutineHandoffTargetAppropriate(
                candidateIsTankHealTarget: false, candidateIsSelf: false, localManaPercent: 80f, localRole: SquadRole.Healer));
            Assert.True(SquadHealBridge.IsRoutineHandoffTargetAppropriate(
                candidateIsTankHealTarget: true, candidateIsSelf: false, localManaPercent: 80f, localRole: SquadRole.Healer));
            Assert.True(SquadHealBridge.IsRoutineHandoffTargetAppropriate(
                candidateIsTankHealTarget: false, candidateIsSelf: true, localManaPercent: 80f, localRole: SquadRole.Healer));
            Assert.False(SquadHealBridge.IsRoutineHandoffTargetAppropriate(
                candidateIsTankHealTarget: true, candidateIsSelf: false, localManaPercent: 20f, localRole: SquadRole.Healer));
            Assert.False(SquadHealBridge.IsRoutineHandoffTargetAppropriate(
                candidateIsTankHealTarget: true, candidateIsSelf: false, localManaPercent: 80f, localRole: SquadRole.DpsMelee));
        }

    }
}
