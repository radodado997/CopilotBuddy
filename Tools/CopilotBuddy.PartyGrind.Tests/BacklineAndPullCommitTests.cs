using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class BacklineAndPullCommitTests : IDisposable
    {
        private readonly string _tempDir;

        public BacklineAndPullCommitTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PartyGrindBacklineTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Styx.Logic.Combat.SpellManager.Shutdown();
            }
            catch
            {
            }
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        private SquadSettings CreateDefaultSettings()
        {
            string path = Path.Combine(_tempDir, "SquadSettings_Test.xml");
            return new SquadSettings(path)
            {
                TeamId = "TestSquad",
                IsCoordinator = true,
                Role = SquadRole.TankLeader,
                DisconnectGraceSeconds = 120,
                SoftWaitDistance = 35f,
                ResumeDistance = 20f,
                HardBacktrackDistance = 65f,
                MaxPullGatherDistance = 28f,
                TriggerRestHealthPct = 55,
                TriggerRestManaPct = 35,
                ResumeRestHealthPct = 85,
                ResumeRestManaPct = 80,
                MaxLootWindowSeconds = 6,
                WaitForPartyRessSeconds = 45,
                SyncTownRuns = true
            };
        }

        private static SquadMemberState MakeLeaderState(float x, float y)
        {
            return new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 100,
                Class = WoWClass.Warrior,
                Role = SquadRole.TankLeader,
                Level = 9,
                MapId = 0,
                X = x,
                Y = y,
                Z = 0f,
                HealthPercent = 100f,
                PowerPercent = 0f,
                PowerType = WoWPowerType.Rage,
                IsInCombat = false,
                TownTasksComplete = true
            };
        }

        private static SquadConnectedMemberSnapshot MakeFollower(string name, float x, float y, long nowMs)
        {
            return new SquadConnectedMemberSnapshot
            {
                CharacterName = name,
                SlotIndex = 1,
                IsConnected = true,
                LastReceivedUtcMs = nowMs,
                Handshake = new SquadHandshake
                {
                    CharacterName = name,
                    Guid = 201,
                    Class = WoWClass.Priest,
                    Role = SquadRole.Healer,
                    Level = 9
                },
                LatestState = new SquadMemberState
                {
                    CharacterName = name,
                    Guid = 201,
                    Class = WoWClass.Priest,
                    Role = SquadRole.Healer,
                    Level = 9,
                    MapId = 0,
                    X = x,
                    Y = y,
                    Z = 0f,
                    HealthPercent = 100f,
                    PowerPercent = 100f,
                    PowerType = WoWPowerType.Mana,
                    IsInCombat = false,
                    TownTasksComplete = true
                }
            };
        }

        [Fact]
        public void BacklineHysteresis_EntersAboveEightExitsBelowThreeWithReachDebounce()
        {
            var m = PartyGrindBot.EvaluateBacklineMovement(9f, false, 0, 1000, out long reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);
            Assert.Equal(0L, reached);

            m = PartyGrindBot.EvaluateBacklineMovement(5f, false, 0, 1100, out reached);
            Assert.Equal(BacklineMovement.HoldPosition, m);

            m = PartyGrindBot.EvaluateBacklineMovement(5f, true, 0, 1100, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, 0, 2000, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);
            Assert.Equal(2000L, reached);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, reached, 2300, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, reached, 2500, out reached);
            Assert.Equal(BacklineMovement.HoldPosition, m);
        }

        [Fact]
        public void BacklineHysteresis_HoldsDecisionAroundOldThreeYardThreshold_NoOscillation()
        {
            bool wasMoving = false;
            long reached = 0;

            var m = PartyGrindBot.EvaluateBacklineMovement(9f, wasMoving, reached, 1000, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.MoveToSlot, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.9f, wasMoving, reached, 1400, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.MoveToSlot, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.9f, wasMoving, reached, 1900, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.HoldPosition, m);

            m = PartyGrindBot.EvaluateBacklineMovement(3.1f, wasMoving, reached, 2000, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.HoldPosition, m);

            m = PartyGrindBot.EvaluateBacklineMovement(3.4f, wasMoving, reached, 2100, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.HoldPosition, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.8f, wasMoving, reached, 2200, out reached);
            wasMoving = m == BacklineMovement.MoveToSlot;
            Assert.Equal(BacklineMovement.HoldPosition, m);
        }

        [Fact]
        public void BacklineHysteresis_LeavingExitRadiusRestartsReachDebounce()
        {
            var m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, 0, 1000, out long reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);
            Assert.Equal(1000L, reached);

            m = PartyGrindBot.EvaluateBacklineMovement(4.0f, true, reached, 1300, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);
            Assert.Equal(0L, reached);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, reached, 1400, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);
            Assert.Equal(1400L, reached);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, reached, 1500, out reached);
            Assert.Equal(BacklineMovement.MoveToSlot, m);

            m = PartyGrindBot.EvaluateBacklineMovement(2.5f, true, reached, 1900, out reached);
            Assert.Equal(BacklineMovement.HoldPosition, m);
        }

        [Fact]
        public void BacklineLatch_HoldsSlotUntilTargetChangesTankDriftsOrExpires()
        {
            var tank0 = new WoWPoint(100, 100, 0);
            var tank5 = new WoWPoint(105, 100, 0);
            var tank11 = new WoWPoint(111, 100, 0);

            Assert.True(PartyGrindBot.IsBacklineLatchValid(10, 10, tank0, tank0, 10_000, 5_000));
            Assert.True(PartyGrindBot.IsBacklineLatchValid(10, 10, tank0, tank5, 10_000, 5_000));
            Assert.False(PartyGrindBot.IsBacklineLatchValid(10, 10, tank0, tank11, 10_000, 5_000));
            Assert.False(PartyGrindBot.IsBacklineLatchValid(10, 11, tank0, tank0, 10_000, 5_000));
            Assert.False(PartyGrindBot.IsBacklineLatchValid(10, 10, tank0, tank0, 10_000, 10_000));
            Assert.True(PartyGrindBot.IsBacklineLatchValid(10, 10, tank0, tank0, 10_000, 9_999));
        }

        [Fact]
        public void Dps_PrefersCombatWhenInRangeAndLoS_YieldsBacklineOnlyWhenEngagementBlocked()
        {
            Assert.True(PartyGrindBot.ShouldYieldBacklineToCombat(true, 25f, true, 30f));
            Assert.True(PartyGrindBot.ShouldYieldBacklineToCombat(true, 32f, true, 35f));
            Assert.False(PartyGrindBot.ShouldYieldBacklineToCombat(true, 35f, true, 30f));
            Assert.False(PartyGrindBot.ShouldYieldBacklineToCombat(true, 25f, false, 30f));
            Assert.False(PartyGrindBot.ShouldYieldBacklineToCombat(false, 25f, true, 30f));
        }

        [Fact]
        public void PullCommit_HoldsFocusWhenGatherOrWaitWouldZeroIt()
        {
            var leader = new SquadLeaderState
            {
                Phase = SquadPhase.Grinding,
                X = 100f,
                Y = 100f,
                Z = 0f,
                MapId = 0,
                Members = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary
                    {
                        CharacterName = "FollowerB",
                        IsConnected = true,
                        HasTelemetry = true,
                        MapId = 0,
                        X = 140f,
                        Y = 100f,
                        Z = 0f
                    }
                }
            };

            Assert.False(SquadTankBridge.CanInitiatePull(leader, 28f));

            ulong held = PartyGrindBot.ResolveCoordinatorFocusGuid(
                0, true, false, true, true, false, 42, true);
            Assert.Equal(42UL, held);

            Assert.Equal(0UL, PartyGrindBot.ResolveCoordinatorFocusGuid(
                42, true, false, true, true, false, 0, false));

            Assert.Equal(5UL, PartyGrindBot.ResolveCoordinatorFocusGuid(
                5, true, false, true, false, true, 0, false));

            Assert.Equal(5UL, PartyGrindBot.ResolveCoordinatorFocusGuid(
                5, false, true, false, true, false, 0, false));

            Assert.Equal(7UL, PartyGrindBot.ResolveCoordinatorFocusGuid(
                7, false, false, true, false, true, 42, true));

            Assert.Equal(42UL, PartyGrindBot.ResolveCoordinatorFocusGuid(
                42, false, false, true, false, true, 42, true));
        }

        [Fact]
        public void PullCommit_ReleasesOnTimeoutDeadDistanceEngagementOrFollowerTooFar()
        {
            long until = 10_000;

            Assert.False(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, false, false, false, 9_999, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, false, false, false, 10_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                false, false, 20f, false, false, false, 1_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, true, 20f, false, false, false, 1_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 61f, false, false, false, 1_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, true, false, false, 1_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, false, true, false, 1_000, until));
            Assert.True(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, false, false, true, 1_000, until));
            Assert.False(PartyGrindBot.EvaluatePullCommitRelease(
                true, false, 20f, false, false, false, 1_000, until));
        }

        [Fact]
        public void DebouncedWaiting_RequiresTwoSecondsStability_ImmediateWaitingUnchanged()
        {
            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            var profile = new SquadProfileSnapshot { MapId = 0 };
            long t = 100_000L;

            coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.True(coord.IsWaitingForFollower);
            Assert.False(coord.IsWaitingForFollowerDebounced);
            Assert.Equal(40f, coord.MaxFollowerDistance);

            t += 1000;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.True(coord.IsWaitingForFollower);
            Assert.False(coord.IsWaitingForFollowerDebounced);

            t += 1100;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.True(coord.IsWaitingForFollowerDebounced);

            t += 1000;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 118f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.False(coord.IsWaitingForFollower);
            Assert.True(coord.IsWaitingForFollowerDebounced);

            t += 2100;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 118f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.False(coord.IsWaitingForFollowerDebounced);
        }

        [Fact]
        public void DebouncedWaiting_BandBetweenSoftWaitAndResumeHoldsState()
        {
            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            var profile = new SquadProfileSnapshot { MapId = 0 };
            long t = 200_000L;

            for (int i = 0; i < 3; i++)
            {
                coord.EvaluateTick(
                    settings,
                    MakeLeaderState(100f, 100f),
                    new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                    profile, 0, 0, 0, 0f, t);
                t += 1100;
            }
            Assert.True(coord.IsWaitingForFollowerDebounced);

            for (int i = 0; i < 5; i++)
            {
                coord.EvaluateTick(
                    settings,
                    MakeLeaderState(100f, 100f),
                    new[] { MakeFollower("FollowerB", 125f, 100f, t) },
                    profile, 0, 0, 0, 0f, t);
                t += 500;
            }
            Assert.True(coord.IsWaitingForFollower);
            Assert.True(coord.IsWaitingForFollowerDebounced);
        }

        [Fact]
        public void PullCandidateDistance_RejectsTargetsBeyondCommitRadius()
        {
            Assert.True(PartyGrindBot.IsEligiblePullCandidateDistance(0.0));
            Assert.True(PartyGrindBot.IsEligiblePullCandidateDistance(55.0));
            Assert.False(PartyGrindBot.IsEligiblePullCandidateDistance(55.1));
            Assert.False(PartyGrindBot.IsEligiblePullCandidateDistance(68.0));
        }

        [Fact]
        public void CombatFocusStickiness_PreventsPingPongExceptForHealerPeel()
        {
            long switchedAt = 10_000L;

            Assert.True(PartyGrindBot.ShouldRetainCombatFocus(
                currentFocusGuid: 100UL,
                candidateFocusGuid: 200UL,
                currentFocusActiveInCombat: true,
                candidateTargetsHealer: false,
                currentTargetsHealer: false,
                focusChangedUtcMs: switchedAt,
                nowUtcMs: switchedAt + 500L));

            Assert.False(PartyGrindBot.ShouldRetainCombatFocus(
                currentFocusGuid: 100UL,
                candidateFocusGuid: 200UL,
                currentFocusActiveInCombat: true,
                candidateTargetsHealer: true,
                currentTargetsHealer: false,
                focusChangedUtcMs: switchedAt,
                nowUtcMs: switchedAt + 500L));

            Assert.False(PartyGrindBot.ShouldRetainCombatFocus(
                currentFocusGuid: 100UL,
                candidateFocusGuid: 200UL,
                currentFocusActiveInCombat: false,
                candidateTargetsHealer: false,
                currentTargetsHealer: false,
                focusChangedUtcMs: switchedAt,
                nowUtcMs: switchedAt + 500L));

            Assert.False(PartyGrindBot.ShouldRetainCombatFocus(
                currentFocusGuid: 100UL,
                candidateFocusGuid: 200UL,
                currentFocusActiveInCombat: true,
                candidateTargetsHealer: false,
                currentTargetsHealer: false,
                focusChangedUtcMs: switchedAt,
                nowUtcMs: switchedAt + 2600L));
        }

        [Fact]
        public void CoordinatorLeaderState_PublishesDebouncedWaitingFlag()
        {
            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            var profile = new SquadProfileSnapshot { MapId = 0 };
            long t = 300_000L;

            var initial = coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.False(initial.IsWaitingForFollower);

            t += 2100L;
            var debounced = coord.EvaluateTick(
                settings,
                MakeLeaderState(100f, 100f),
                new[] { MakeFollower("FollowerB", 140f, 100f, t) },
                profile, 0, 0, 0, 0f, t);
            Assert.True(debounced.IsWaitingForFollower);
        }

        [Fact]
        public void PortalStepThrough_ContinuesPastEntranceAlongApproachHeading()
        {
            WoWPoint step = PartyGrindBot.ComputePortalStepThrough(new WoWPoint(0f, 0f, 3f), new WoWPoint(10f, 0f, 5f));

            Assert.Equal(14f, step.X, 3);
            Assert.Equal(0f, step.Y, 3);
            Assert.Equal(5f, step.Z, 3);
        }

        [Fact]
        public void PortalStepThrough_KeepsEntranceWhenAlreadyStandingOnIt()
        {
            var entrance = new WoWPoint(4247.842f, 750.6f, -22.4f);

            Assert.Equal(entrance, PartyGrindBot.ComputePortalStepThrough(entrance, entrance));
        }

        [Fact]
        public void CanFollowerEngageFocus_PreventsPrematureBodyPullAheadOfTank()
        {
            Assert.False(PartyGrindBot.CanFollowerEngageFocus(
                meInCombat: false,
                leaderInCombat: false,
                focusInCombat: false,
                focusTargetingParty: false,
                tankDistanceToFocus: 24f));

            Assert.True(PartyGrindBot.CanFollowerEngageFocus(
                meInCombat: false,
                leaderInCombat: false,
                focusInCombat: false,
                focusTargetingParty: false,
                tankDistanceToFocus: 10f));

            Assert.True(PartyGrindBot.CanFollowerEngageFocus(
                meInCombat: false,
                leaderInCombat: true,
                focusInCombat: false,
                focusTargetingParty: false,
                tankDistanceToFocus: 30f));

            Assert.True(PartyGrindBot.CanFollowerEngageFocus(
                meInCombat: false,
                leaderInCombat: false,
                focusInCombat: true,
                focusTargetingParty: false,
                tankDistanceToFocus: 30f));
        }

        [Fact]
        public void OnSpellBookChanged_RetainsFailedTrailingRefreshUntilShutdown()
        {
            Styx.Logic.Combat.SpellManager.Shutdown();
            var args1 = new Styx.WoWInternals.LuaEventArgs("LEARNED_SPELL_IN_TAB", 0, Array.Empty<object>());
            Styx.Logic.Combat.SpellManager.OnSpellBookChanged(null!, args1);
            Assert.False(Styx.Logic.Combat.SpellManager.HasPendingTrailingRebuild);

            var args2 = new Styx.WoWInternals.LuaEventArgs("LEARNED_SPELL_IN_TAB", 0, Array.Empty<object>());
            Styx.Logic.Combat.SpellManager.OnSpellBookChanged(null!, args2);
            Assert.True(Styx.Logic.Combat.SpellManager.HasPendingTrailingRebuild);

            System.Threading.Thread.Sleep(270);
            Styx.Logic.Combat.SpellManager.CheckPendingTrailingRebuild();
            Assert.True(Styx.Logic.Combat.SpellManager.HasPendingTrailingRebuild);

            Styx.Logic.Combat.SpellManager.Shutdown();
            Assert.False(Styx.Logic.Combat.SpellManager.HasPendingTrailingRebuild);
        }

        [Fact]
        public void TryGetEffectiveTankPosition_DoesNotFallbackToDesignatedTankWhenEffectiveTankGuidNonZero()
        {
            var leader = new SquadLeaderState
            {
                LeaderGuid = 100UL,
                EffectiveTankGuid = 999UL,
                MapId = 1,
                X = 10f,
                Y = 10f,
                Z = 10f,
                Members = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary
                    {
                        Guid = 200UL,
                        Role = SquadRole.TankLeader,
                        MapId = 1,
                        X = 20f,
                        Y = 20f,
                        Z = 20f,
                        IsConnected = true,
                        IsDead = false,
                        IsGhost = false
                    }
                }
            };

            var (known, pos) = PartyGrindBot.TryGetEffectiveTankPosition(leader, myMapId: 1);
            Assert.False(known);
            Assert.Equal(WoWPoint.Zero, pos);

            leader.EffectiveTankGuid = 0UL;
            var (knownFallback, posFallback) = PartyGrindBot.TryGetEffectiveTankPosition(leader, myMapId: 1);
            Assert.True(knownFallback);
            Assert.Equal(new WoWPoint(20f, 20f, 20f), posFallback);
        }
    }
}
