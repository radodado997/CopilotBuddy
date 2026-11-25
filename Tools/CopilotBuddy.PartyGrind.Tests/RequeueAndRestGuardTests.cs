using System;
using System.Collections.Generic;
using CopilotBuddy.Bots.PartyGrind;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Combat.CombatRoutine;
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
    }
}
