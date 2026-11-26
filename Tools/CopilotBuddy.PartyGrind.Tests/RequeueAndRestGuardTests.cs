using System;
using System.Collections.Generic;
using CopilotBuddy.Bots.PartyGrind;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
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
        public void GhostPortalProgress_RequiresDecreasingEntranceDistanceAndFlagsStallWhenDriftingAway()
        {
            float bestDist = float.MaxValue;
            long lastProgress = 0L;
            long t0 = 1_000_000L;

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(692.8f, ref bestDist, ref lastProgress, t0));
            Assert.Equal(692.8f, bestDist, 2);
            Assert.Equal(t0, lastProgress);

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(710.0f, ref bestDist, ref lastProgress, t0 + 4_000L));
            Assert.Equal(t0, lastProgress);

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(777.0f, ref bestDist, ref lastProgress, t0 + 9_500L));
            Assert.Equal(t0, lastProgress);

            Assert.True(PartyGrindBot.EvaluateGhostPortalProgress(777.0f, ref bestDist, ref lastProgress, t0 + 10_000L));

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(690.0f, ref bestDist, ref lastProgress, t0 + 10_500L));
            Assert.Equal(690.0f, bestDist, 2);
            Assert.Equal(t0 + 10_500L, lastProgress);
        }

        [Fact]
        public void CrossMapNavigationGuard_AllowsEasternKingdomsZeroAndRejectsMismatch()
        {
            Assert.True(PartyGrindBot.IsSameMapForNavigation(0U, 0U));
            Assert.True(PartyGrindBot.IsSameMapForNavigation(1U, 1U));
            Assert.True(PartyGrindBot.IsSameMapForNavigation(129U, 129U));
            Assert.False(PartyGrindBot.IsSameMapForNavigation(1U, 129U));
            Assert.False(PartyGrindBot.IsSameMapForNavigation(129U, 1U));
            Assert.False(PartyGrindBot.IsSameMapForNavigation(0U, 129U));
            Assert.False(PartyGrindBot.IsSameMapForNavigation(129U, 0U));
        }

        [Fact]
        public void UnavailableMapTelemetry_PropagatesExplicitlyDistinctFromEasternKingdomsZero()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = new SquadSettings
            {
                Role = SquadRole.TankLeader,
                BotMode = SquadBotMode.OpenWorldGrind,
                IsCoordinator = true
            };
            var leaderLocal = new SquadMemberState
            {
                CharacterName = "LeaderA",
                Guid = 1UL,
                Role = SquadRole.TankLeader,
                Class = WoWClass.Warrior,
                Level = 20,
                MapId = 0U,
                HasValidMap = false
            };
            var profile = new SquadProfileSnapshot
            {
                HasProfile = true,
                MapId = null
            };
            var dungeon = new SquadDungeonSnapshot
            {
                ActiveDungeonMapId = null,
                PortalMapId = null
            };

            SquadLeaderState state = coordinator.EvaluateTick(
                settings,
                leaderLocal,
                Array.Empty<SquadConnectedMemberSnapshot>(),
                profile,
                0UL,
                0UL,
                0L,
                0f,
                10_000L,
                dungeon: dungeon);

            Assert.False(state.HasValidMap);
            Assert.Null(state.ActiveDungeonMapId);
            Assert.Null(state.DungeonPortalMapId);
        }

        [Fact]
        public void AdvanceBossBreadcrumb_PreservesNextCrumbWhenLiveBossIsExcluded()
        {
            var path = new[]
            {
                new WoWPoint(100f, 100f, 0f),
                new WoWPoint(140f, 100f, 0f)
            };
            int breadcrumbIndex = 0;

            WoWPoint next = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(50f, 100f, 0f),
                path,
                WoWPoint.Zero,
                liveBossLocation: null,
                liveBossPathDistance: null,
                ref breadcrumbIndex);

            Assert.Equal(path[0], next);
            Assert.Equal(0, breadcrumbIndex);
        }

        [Fact]
        public void CorridorGraceBypass_RetainsBypassUntilClearedWhileNormalBlockedStepStillWaitsGrace()
        {
            uint blockedMap = 0;
            WoWPoint blockedObj = WoWPoint.Zero;
            long lastEval = 0L;
            long blockedSince = 0L;
            long t0 = 1_000_000L;
            var objA = new WoWPoint(100f, 200f, 30f);
            var objB = new WoWPoint(200f, 300f, 30f);

            bool hold0 = PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                129U,
                objA,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                t0,
                out bool newlyBlocked,
                out bool graceExpired);
            Assert.True(hold0);
            Assert.True(newlyBlocked);
            Assert.False(graceExpired);

            // Build elapsed time on objA continuously up to 4s (< 8s grace), then switch to objB after only 500ms (< 1500ms gap):
            // objective change alone must reset the grace timer without waiting for an inactivity timeout.
            for (long dt = 500L; dt <= 4_000L; dt += 500L)
            {
                PartyGrindBot.EvaluateDungeonCorridorBlock(
                    bypassTrashGrace: false,
                    hasTrashOnSegment: true,
                    129U,
                    objA,
                    ref blockedMap,
                    ref blockedObj,
                    ref lastEval,
                    ref blockedSince,
                    t0 + dt,
                    out _,
                    out _);
            }

            bool holdB = PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                129U,
                objB,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                t0 + 4_500L,
                out bool bNewlyBlocked,
                out bool bGraceExpired);
            Assert.True(holdB);
            Assert.True(bNewlyBlocked);
            Assert.False(bGraceExpired);

            // An inactivity interruption (>= 1500ms) on the SAME objective (objB) must also reset the grace timer.
            bool holdAfterInterruption = PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                129U,
                objB,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                t0 + 11_500L,
                out bool interruptedNewlyBlocked,
                out bool interruptedGraceExpired);
            Assert.True(holdAfterInterruption);
            Assert.True(interruptedNewlyBlocked);
            Assert.False(interruptedGraceExpired);

            // Continuous hold ticks (every 500ms < 1500ms gap) advance B's grace window to expiry
            bool holdAtExpire = true;
            bool graceExpiredAfterWindow = false;
            for (long dt = 500L; dt <= PartyGrindBot.DungeonAdvanceBlockMaxMs; dt += 500L)
            {
                holdAtExpire = PartyGrindBot.EvaluateDungeonCorridorBlock(
                    bypassTrashGrace: false,
                    hasTrashOnSegment: true,
                    129U,
                    objB,
                    ref blockedMap,
                    ref blockedObj,
                    ref lastEval,
                    ref blockedSince,
                    t0 + 11_500L + dt,
                    out _,
                    out graceExpiredAfterWindow);
            }
            Assert.False(holdAtExpire);
            Assert.True(graceExpiredAfterWindow);
            Assert.Equal(0L, blockedSince);

            bool holdOnNextTickWithBypass = PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: true,
                hasTrashOnSegment: true,
                129U,
                objB,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                t0 + 23_600L,
                out bool newlyBlockedWithBypass,
                out _);
            Assert.False(holdOnNextTickWithBypass);
            Assert.False(newlyBlockedWithBypass);
            Assert.Equal(0L, blockedSince);

            bool holdNextNormalStep = PartyGrindBot.EvaluateDungeonCorridorBlock(
                bypassTrashGrace: false,
                hasTrashOnSegment: true,
                129U,
                objB,
                ref blockedMap,
                ref blockedObj,
                ref lastEval,
                ref blockedSince,
                t0 + 26_000L,
                out bool nextStepBlocked,
                out _);
            Assert.True(holdNextNormalStep);
            Assert.True(nextStepBlocked);
        }

        [Fact]
        public void GhostPortalProgress_SeparatesRouteAndStraightBaselinesAcrossUnstuckSamples()
        {
            var entrance = new WoWPoint(100f, 100f, 0f);
            var corner1 = new WoWPoint(0f, 300f, 0f);
            var corner2 = new WoWPoint(100f, 300f, 0f);
            var remainingPath = new[] { corner1, corner2, entrance };

            int bestGen = 0;
            float bestRoute = float.MaxValue;
            float bestStraight = float.MaxValue;
            long lastProgress = 0L;
            long t0 = 2_000_000L;

            float metric1 = PartyGrindBot.ComputeGhostPortalProgressDistance(new WoWPoint(0f, 100f, 0f), entrance, remainingPath);
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                metric1,
                hasRoutePath: true,
                routeGeneration: 1,
                ref bestGen,
                ref bestRoute,
                ref bestStraight,
                ref lastProgress,
                t0));

            // 15s later ghost moved North away from Euclidean entrance (100y -> 172y) along the detour path
            var detourPos = new WoWPoint(0f, 240f, 0f);
            Assert.True(detourPos.Distance(entrance) > new WoWPoint(0f, 100f, 0f).Distance(entrance));
            float metric2 = PartyGrindBot.ComputeGhostPortalProgressDistance(detourPos, entrance, remainingPath);
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                metric2,
                hasRoutePath: true,
                routeGeneration: 1,
                ref bestGen,
                ref bestRoute,
                ref bestStraight,
                ref lastProgress,
                t0 + 15_000L));

            // Transient non-Moved tick (e.g. UnstuckAttempt) submits straight-line 10y (< 28y); must NOT poison bestRoute
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                10f,
                hasRoutePath: false,
                routeGeneration: 1,
                ref bestGen,
                ref bestRoute,
                ref bestStraight,
                ref lastProgress,
                t0 + 18_000L));
            float expectedRouteAfterDetour = bestRoute;

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                expectedRouteAfterDetour - 7f,
                hasRoutePath: true,
                routeGeneration: 1,
                ref bestGen,
                ref bestRoute,
                ref bestStraight,
                ref lastProgress,
                t0 + 20_000L));
            Assert.Equal(t0 + 20_000L, lastProgress);

            // When movement along that route truly halts for 10s, stall still triggers
            Assert.True(PartyGrindBot.EvaluateGhostPortalProgress(
                expectedRouteAfterDetour - 7f,
                hasRoutePath: true,
                routeGeneration: 1,
                ref bestGen,
                ref bestRoute,
                ref bestStraight,
                ref lastProgress,
                t0 + 30_000L));
        }

        [Fact]
        public void GhostPortalProgress_RebasesAcrossRouteGenerationsWithoutGrantingFreeStallExtensions()
        {
            int bestGen = 0;
            float bestRoute = float.MaxValue;
            float bestStraight = float.MaxValue;
            long lastProgress = 0L;
            long t0 = 3_000_000L;

            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                500f, hasRoutePath: true, routeGeneration: 1,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0));
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                30f, hasRoutePath: true, routeGeneration: 1,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0 + 5_000L));

            // Route replaces a 30y tail with a 55y detour (gen 2, 7s after last progress), then advances 20y (11s after gen-1 progress):
            // rebaselining lets 35y count as progress without falsely firing stall at t0+16s.
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                55f, hasRoutePath: true, routeGeneration: 2,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0 + 12_000L));
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                35f, hasRoutePath: true, routeGeneration: 2,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0 + 16_000L));
            Assert.Equal(t0 + 16_000L, lastProgress);

            // Route generation change that ALREADY improved on previous best (>= 2y closer) credits lastProgress immediately.
            Assert.False(PartyGrindBot.EvaluateGhostPortalProgress(
                22f, hasRoutePath: true, routeGeneration: 3,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0 + 25_000L));
            Assert.Equal(t0 + 25_000L, lastProgress);

            // Stationary for 10s afterward still stalls cleanly.
            Assert.True(PartyGrindBot.EvaluateGhostPortalProgress(
                22f, hasRoutePath: true, routeGeneration: 3,
                ref bestGen, ref bestRoute, ref bestStraight, ref lastProgress, t0 + 35_000L));
        }

        [Fact]
        public void CorridorSegmentTrashCheck_UsesInterpolatedRampZInsteadOfStartZ()
        {
            var from = new WoWPoint(0f, 0f, 0f);
            var to = new WoWPoint(10f, 0f, 12f);
            var hostileNearTopOfRamp = new[] { new WoWPoint(9f, 1f, 11f) };
            var hostileOnUpperBridgeAboveFlatCorridor = new[] { new WoWPoint(5f, 1f, 12f) };

            Assert.True(SquadDungeonService.HasTrashOnSegment(from, to, hostileNearTopOfRamp));
            Assert.False(SquadDungeonService.HasTrashOnSegment(
                new WoWPoint(0f, 0f, 0f),
                new WoWPoint(10f, 0f, 0f),
                hostileOnUpperBridgeAboveFlatCorridor));

            var lPath = new[] { new WoWPoint(30f, 0f, 0f), new WoWPoint(30f, 30f, 0f) };
            Assert.False(SquadDungeonService.HasTrashOnPolylineStep(
                new WoWPoint(0f, 0f, 0f),
                lPath,
                lPath[1],
                new[] { new WoWPoint(15f, 15f, 0f) }));
            Assert.True(SquadDungeonService.HasTrashOnPolylineStep(
                new WoWPoint(0f, 0f, 0f),
                lPath,
                lPath[1],
                new[] { new WoWPoint(30f, 15f, 0f) }));
        }

        [Fact]
        public void LeaderStateSerialization_OmitsNullDungeonMapsWhilePreservingEasternKingdomsZero()
        {
            var openWorld = new SquadLeaderState
            {
                MapId = 0U,
                ActiveDungeonMapId = null,
                DungeonPortalMapId = null
            };
            string openJson = System.Text.Json.JsonSerializer.Serialize(openWorld, SquadJson.Options);
            Assert.DoesNotContain("ActiveDungeonMapId", openJson, StringComparison.Ordinal);
            Assert.DoesNotContain("DungeonPortalMapId", openJson, StringComparison.Ordinal);

            var ekPortal = new SquadLeaderState
            {
                MapId = 0U,
                ActiveDungeonMapId = 36U,
                DungeonPortalMapId = 0U
            };
            string ekJson = System.Text.Json.JsonSerializer.Serialize(ekPortal, SquadJson.Options);
            Assert.Contains("\"DungeonPortalMapId\":0", ekJson, StringComparison.Ordinal);
            var roundTripped = System.Text.Json.JsonSerializer.Deserialize<SquadLeaderState>(ekJson, SquadJson.Options)!;
            Assert.Equal(36U, roundTripped.ActiveDungeonMapId);
            Assert.Equal(0U, roundTripped.DungeonPortalMapId);
        }

        [Fact]
        public void FinalBossLootWindow_IsPreservedAcrossCorpseRunAndResumedBeforeExit()
        {
            var coord = new SquadCoordinatorService();
            var settings = new SquadSettings
            {
                Role = SquadRole.TankLeader,
                BotMode = SquadBotMode.Dungeon,
                DungeonLootMode = SquadDungeonLootMode.BossesOnly,
                IsCoordinator = true,
                MaxLootWindowSeconds = 20
            };

            SquadConnectedMemberSnapshot Wrap(SquadMemberState state) => new SquadConnectedMemberSnapshot
            {
                CharacterName = state.CharacterName,
                Handshake = new SquadHandshake
                {
                    CharacterName = state.CharacterName,
                    Guid = state.Guid,
                    Class = state.Class,
                    Role = state.Role,
                    Level = state.Level
                },
                IsConnected = true,
                LatestState = state
            };

            var leader = new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 1UL,
                Role = SquadRole.TankLeader,
                Class = WoWClass.Warrior,
                Level = 40,
                MapId = 129U,
                IsInsideDungeon = true,
                IsInCombat = true
            };
            var dps = new SquadMemberState
            {
                CharacterName = "MageB",
                Guid = 2UL,
                Role = SquadRole.DpsRanged,
                Class = WoWClass.Mage,
                Level = 40,
                MapId = 129U,
                IsInsideDungeon = true,
                IsInCombat = true
            };
            var dungeon = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 20U,
                ActiveDungeonMapId = 129U,
                IsDungeonComplete = false
            };

            coord.EvaluateTick(settings, leader, new[] { Wrap(dps) }, new SquadProfileSnapshot(), 0UL, 0UL, 0L, 0f, 1_000L, dungeon: dungeon);

            // Final boss dies while MageB is also dead; TankA survives -> combat ends, loot window opens, CorpseRun starts.
            leader.IsInCombat = false;
            dps.IsInCombat = false;
            dps.IsDead = true;
            dungeon.IsDungeonComplete = true;

            var wipeState = coord.EvaluateTick(settings, leader, new[] { Wrap(dps) }, new SquadProfileSnapshot(), 0UL, 0UL, 0L, 0f, 2_000L, dungeon: dungeon);
            Assert.Equal(SquadPhase.CorpseRun, wipeState.Phase);
            Assert.True(wipeState.IsLootWindowActive);

            // MageB returns alive within the loot window -> should resume Looting, NOT immediately DungeonCompleteExit.
            dps.IsDead = false;
            var resumed = coord.EvaluateTick(settings, leader, new[] { Wrap(dps) }, new SquadProfileSnapshot(), 0UL, 0UL, 0L, 0f, 5_000L, dungeon: dungeon);
            Assert.Equal(SquadPhase.Looting, resumed.Phase);
        }

        [Fact]
        public void MigratingZone_WaitsUntilConnectedFollowerReportsTelemetryBeforeCompleting()
        {
            var coord = new SquadCoordinatorService();
            var settings = new SquadSettings
            {
                Role = SquadRole.TankLeader,
                BotMode = SquadBotMode.OpenWorldGrind,
                IsCoordinator = true
            };

            var leader = new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 1UL,
                Role = SquadRole.TankLeader,
                Class = WoWClass.Warrior,
                Level = 15,
                MapId = 1U,
                X = 100f,
                Y = 100f
            };
            var followerConnectedNoTelemetry = new SquadConnectedMemberSnapshot
            {
                CharacterName = "HealB",
                Handshake = new SquadHandshake
                {
                    CharacterName = "HealB",
                    Guid = 2UL,
                    Class = WoWClass.Priest,
                    Role = SquadRole.Healer,
                    Level = 15
                },
                IsConnected = true,
                LatestState = null
            };
            var subProfile1 = new SquadProfileSnapshot
            {
                HasProfile = true,
                SubProfileName = "Zone1",
                MapId = 1U,
                HotspotX = 100f,
                HotspotY = 100f
            };
            var subProfile2 = new SquadProfileSnapshot
            {
                HasProfile = true,
                SubProfileName = "Zone2",
                MapId = 1U,
                HotspotX = 100f,
                HotspotY = 100f
            };

            coord.EvaluateTick(settings, leader, new[] { followerConnectedNoTelemetry }, subProfile1, 0UL, 0UL, 0L, 0f, 1_000L);
            SquadLeaderState migratingState = coord.EvaluateTick(
                settings, leader, new[] { followerConnectedNoTelemetry }, subProfile2, 0UL, 0UL, 0L, 0f, 2_000L);
            Assert.Equal(SquadPhase.MigratingZone, migratingState.Phase);
        }
    }
}
