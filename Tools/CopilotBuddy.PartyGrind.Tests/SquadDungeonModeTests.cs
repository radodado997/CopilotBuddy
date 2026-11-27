using System;
using System.Collections.Generic;
using System.Linq;
using CopilotBuddy.Bots.PartyGrind.Forms;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadDungeonModeTests : IDisposable
    {
        public SquadDungeonModeTests()
        {
            SquadContext.Reset();
        }

        public void Dispose()
        {
            SquadContext.Reset();
            global::Bots.DungeonBuddy.BossManager.Reset();
            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.RestoreMovement();
        }

        private static SquadSettings CreateDungeonSettings(
            SquadDungeonEntryMode entryMode = SquadDungeonEntryMode.LfgRandomNormal)
        {
            return new SquadSettings
            {
                BotMode = SquadBotMode.Dungeon,
                DungeonEntryMode = entryMode,
                DungeonLootMode = SquadDungeonLootMode.BossesOnly,
                KillOptionalBosses = false,
                AllowPartialSquadLfg = true,
                MaxInstancesPerHour = 5,
                Role = SquadRole.TankLeader,
                IsCoordinator = true,
                CoordinatorName = "TankA",
                ExpectedMemberNamesCsv = "TankA,HealB,DpsC,DpsD,DpsE",
                SyncTownRuns = true
            };
        }

        private static SquadMemberState MakeMemberState(
            string name,
            ulong guid,
            SquadRole role,
            WoWClass wowClass,
            int level,
            bool insideDungeon = false,
            bool isDead = false,
            bool canRes = false,
            bool proposalPending = false)
        {
            return new SquadMemberState
            {
                CharacterName = name,
                Guid = guid,
                Role = role,
                Class = wowClass,
                Level = level,
                HealthPercent = isDead ? 0f : 100f,
                PowerPercent = 100f,
                PowerType = WoWPowerType.Mana,
                FreeBagSlots = 20,
                LowestDurabilityPct = 100f,
                MapId = insideDungeon ? 36U : 0U,
                IsInsideDungeon = insideDungeon,
                HasDungeonTelemetry = true,
                IsDead = isDead,
                CanResurrect = canRes,
                LfgProposalPending = proposalPending
            };
        }

        private static SquadConnectedMemberSnapshot WrapClient(SquadMemberState state, long receivedUtcMs = 10_000L, bool isConnected = true)
        {
            return new SquadConnectedMemberSnapshot
            {
                CharacterName = state.CharacterName,
                IsConnected = isConnected,
                LastReceivedUtcMs = receivedUtcMs,
                Handshake = new SquadHandshake
                {
                    CharacterName = state.CharacterName,
                    Guid = state.Guid,
                    Role = state.Role,
                    Class = state.Class,
                    Level = state.Level
                },
                LatestState = state
            };
        }

        [Fact]
        public void DungeonCoordinator_TransitionsFromQueueing_ToClearing_ToCompleteExit()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.LfgRandomNormal);
            var leader = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 20, insideDungeon: false);
            var heal = WrapClient(MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 19, insideDungeon: false, proposalPending: true));
            var dps = WrapClient(MakeMemberState("DpsC", 3UL, SquadRole.DpsMelee, WoWClass.Rogue, 20, insideDungeon: false));

            var snapOutside = new SquadDungeonSnapshot
            {
                IsInsideDungeon = false,
                ActiveDungeonId = 258U,
                LfgMode = "queued"
            };

            var queueingState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 10_000L,
                dungeon: snapOutside);

            Assert.Equal(SquadBotMode.Dungeon, queueingState.BotMode);
            Assert.Equal(SquadPhase.DungeonQueueing, queueingState.Phase);
            Assert.True(queueingState.AcceptLfgProposalOrder);
            Assert.False(queueingState.LevelCapReached);

            leader.IsInsideDungeon = true;
            leader.MapId = 36U;
            heal.LatestState!.IsInsideDungeon = true;
            heal.LatestState.MapId = 36U;
            heal.LatestState.LfgProposalPending = false;
            dps.LatestState!.IsInsideDungeon = true;
            dps.LatestState.MapId = 36U;

            var snapInside = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 6U,
                ActiveDungeonMapId = 36U,
                ActiveDungeonName = "The Deadmines",
                CurrentBossEntry = 644U,
                CurrentBossName = "Rhahk'Zor",
                KilledBossCount = 0,
                TotalBossCount = 5,
                IsDungeonComplete = false,
                EffectiveTankGuid = 1UL
            };

            var clearingState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 12_000L,
                dungeon: snapInside);

            Assert.Equal(SquadPhase.DungeonClearing, clearingState.Phase);
            Assert.Equal("The Deadmines", clearingState.ActiveDungeonName);
            Assert.Equal("Rhahk'Zor", clearingState.CurrentBossName);
            Assert.Equal(5, clearingState.TotalBossCount);
            Assert.True(SquadTankBridge.CanInitiatePull(clearingState, settings.MaxPullGatherDistance));

            snapInside.KilledBossCount = 5;
            snapInside.IsDungeonComplete = true;

            var exitState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 30_000L,
                dungeon: snapInside);

            Assert.Equal(SquadPhase.DungeonCompleteExit, exitState.Phase);
            Assert.True(exitState.TeleportOutOrder);
            Assert.Equal(SquadDungeonResetPhase.Exiting, exitState.DungeonResetPhase);
            Assert.True(exitState.SuspendAutoInvite);

            leader.IsInsideDungeon = false;
            leader.MapId = 0U;
            var leaderOutside = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 31_000L,
                dungeon: snapOutside);
            Assert.Equal(SquadPhase.DungeonCompleteExit, leaderOutside.Phase);
            Assert.True(leaderOutside.TeleportOutOrder);
            Assert.Equal(SquadDungeonResetPhase.Exiting, leaderOutside.DungeonResetPhase);

            heal.LatestState!.IsInsideDungeon = false;
            dps.LatestState!.IsInsideDungeon = false;
            var staleOutside = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 31_500L,
                dungeon: snapOutside);
            Assert.Equal(SquadDungeonResetPhase.Exiting, staleOutside.DungeonResetPhase);

            heal = WrapClient(heal.LatestState, 32_000L);
            dps = WrapClient(dps.LatestState, 32_000L);
            snapOutside.IsInPartyOrRaid = true;
            var disbandingState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 32_000L,
                dungeon: snapOutside);
            Assert.Equal(SquadPhase.DungeonQueueing, disbandingState.Phase);
            Assert.False(disbandingState.TeleportOutOrder);
            Assert.Equal(SquadDungeonResetPhase.Disbanding, disbandingState.DungeonResetPhase);
            Assert.True(disbandingState.SuspendAutoInvite);

            snapOutside.IsInPartyOrRaid = false;
            snapOutside.AllExpectedMembersInParty = false;
            var resettingState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 33_000L,
                dungeon: snapOutside,
                instanceResetGeneration: 0);
            Assert.Equal(SquadDungeonResetPhase.Resetting, resettingState.DungeonResetPhase);
            Assert.True(resettingState.SuspendAutoInvite);

            var reinvitingState = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 34_000L,
                dungeon: snapOutside,
                instanceResetGeneration: 1);
            Assert.Equal(SquadDungeonResetPhase.Reinviting, reinvitingState.DungeonResetPhase);
            Assert.False(reinvitingState.SuspendAutoInvite);

            snapOutside.AllExpectedMembersInParty = true;
            var idleAgain = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 35_000L,
                dungeon: snapOutside,
                instanceResetGeneration: 1);
            Assert.Equal(SquadDungeonResetPhase.Idle, idleAgain.DungeonResetPhase);
            Assert.Equal(SquadPhase.DungeonQueueing, idleAgain.Phase);
            Assert.False(idleAgain.SuspendAutoInvite);
        }

        [Fact]
        public void WalkInPortalFarm_TracksHourlyResetLimit_AndEntersLockoutWait()
        {
            var dungeonService = new SquadDungeonService();
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.WalkInPortalFarm);
            settings.MaxInstancesPerHour = 5;

            long baseTime = 100_000L;
            for (int i = 0; i < 5; i++)
            {
                long t = baseTime + i * 60_000L;
                Assert.Equal(0, dungeonService.GetLockoutRemainingSeconds(t, settings.MaxInstancesPerHour));
                dungeonService.RecordInstanceReset(t);
            }

            long checkTime = baseTime + 300_000L;
            Assert.Equal(5, dungeonService.GetHourlyResetCount(checkTime));
            int remainingSec = dungeonService.GetLockoutRemainingSeconds(checkTime, settings.MaxInstancesPerHour);
            Assert.True(remainingSec > 3200 && remainingSec <= 3600);

            var leader = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 30, insideDungeon: false);
            var walkInSnap = new SquadDungeonSnapshot
            {
                IsInsideDungeon = false,
                ActiveDungeonId = 163U,
                ActiveDungeonName = "Scarlet Monastery - Graveyard",
                PortalX = 2915f,
                PortalY = -802f,
                PortalZ = 160f
            };

            var lockoutState = coord.EvaluateTick(
                settings, leader, Array.Empty<SquadConnectedMemberSnapshot>(), new SquadProfileSnapshot(),
                0, 0, 0, 0f, checkTime,
                dungeon: walkInSnap,
                instanceResetGeneration: dungeonService.InstanceResetGeneration,
                hourlyInstancesRunCount: dungeonService.GetHourlyResetCount(checkTime),
                lockoutRemainingSeconds: remainingSec);

            Assert.Equal(SquadPhase.DungeonLockoutWait, lockoutState.Phase);
            Assert.Equal(5, lockoutState.HourlyInstancesRunCount);
            Assert.Equal(remainingSec, lockoutState.LockoutRemainingSeconds);

            long expiredTime = baseTime + 3_600_001L;
            Assert.Equal(0, dungeonService.GetLockoutRemainingSeconds(expiredTime, settings.MaxInstancesPerHour));

            var travelState = coord.EvaluateTick(
                settings, leader, Array.Empty<SquadConnectedMemberSnapshot>(), new SquadProfileSnapshot(),
                0, 0, 0, 0f, expiredTime,
                dungeon: walkInSnap,
                instanceResetGeneration: dungeonService.InstanceResetGeneration,
                hourlyInstancesRunCount: dungeonService.GetHourlyResetCount(expiredTime),
                lockoutRemainingSeconds: 0);

            Assert.Equal(SquadPhase.DungeonTravelToPortal, travelState.Phase);
            Assert.False(travelState.TeleportOutOrder);
        }

        [Fact]
        public void BracketResolver_PicksMatchingDungeonBySquadMinLevel_AndHonorsExplicitIds()
        {
            var candidates = new[]
            {
                new DungeonBracketEntry(4U, 13, 18, allianceFriendly: false, hordeFriendly: true),
                new DungeonBracketEntry(6U, 16, 22, allianceFriendly: true, hordeFriendly: false),
                new DungeonBracketEntry(163U, 31, 36, allianceFriendly: true, hordeFriendly: true),
                new DungeonBracketEntry(202U, 68, 72, allianceFriendly: true, hordeFriendly: true),
                new DungeonBracketEntry(207U, 77, 80, allianceFriendly: true, hordeFriendly: true)
            };

            var settings = CreateDungeonSettings(SquadDungeonEntryMode.WalkInPortalFarm);
            settings.SelectedDungeonIdsCsv = string.Empty;

            uint autoClassic = SquadDungeonService.ResolveTargetDungeonId(
                settings,
                effectiveSquadMinLevel: 32,
                isAlliance: true,
                playlistOverride: candidates);
            Assert.Equal(163U, autoClassic);

            uint factionFiltered = SquadDungeonService.ResolveTargetDungeonId(
                settings,
                effectiveSquadMinLevel: 17,
                isAlliance: true,
                playlistOverride: candidates);
            Assert.Equal(6U, factionFiltered);

            settings.SelectedDungeonIdsCsv = "6,163";
            uint explicitDeadmines = SquadDungeonService.ResolveTargetDungeonId(
                settings,
                effectiveSquadMinLevel: 18,
                isAlliance: true,
                playlistOverride: candidates);
            Assert.Equal(6U, explicitDeadmines);

            settings.SelectedDungeonIdsCsv = "207";
            uint wotlkPick = SquadDungeonService.ResolveTargetDungeonId(
                settings,
                effectiveSquadMinLevel: 80,
                isAlliance: false,
                playlistOverride: candidates);
            Assert.Equal(207U, wotlkPick);
        }

        [Fact]
        public void PartialSquadWithPugs_DetectsRandomMembers_DelegatesPugTank_AndDisablesProvisioning()
        {
            var squadGuids = new HashSet<ulong> { 10UL, 11UL, 12UL };
            var partyMembers = new (ulong Guid, bool IsTankRoleAssigned, bool IsAlive)[]
            {
                (10UL, false, true),
                (11UL, false, true),
                (12UL, false, true),
                (99UL, true, true),
                (100UL, false, true)
            };

            var (hasRandoms, effectiveTank) = SquadDungeonService.EvaluatePartyComposition(
                coordinatorGuid: 10UL,
                coordinatorRole: SquadRole.DpsMelee,
                squadBotGuids: squadGuids,
                inGamePartyMembers: partyMembers);

            Assert.True(hasRandoms);
            Assert.Equal(99UL, effectiveTank);

            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.LfgRandomNormal);

            var leader = MakeMemberState("TankA", 10UL, SquadRole.TankLeader, WoWClass.Mage, 30, insideDungeon: true);
            leader.CanProvideMageDrink = true;
            var healer = WrapClient(MakeMemberState("HealB", 11UL, SquadRole.Healer, WoWClass.Priest, 30, insideDungeon: true));
            healer.LatestState!.DrinkCount = 0;
            healer.LatestState.WantsDrink = true;

            var snapWithPug = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 163U,
                ActiveDungeonMapId = 189U,
                EffectiveTankGuid = 99UL
            };

            var squadOnly = coord.EvaluateTick(
                settings, leader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 10_000L,
                dungeon: snapWithPug);
            Assert.Equal("TankA", squadOnly.ActiveProvisionerName);

            snapWithPug.HasRandomPartyMembers = true;
            var state = coord.EvaluateTick(
                settings, leader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 20_000L,
                dungeon: snapWithPug);

            Assert.True(state.HasRandomPartyMembers);
            Assert.Equal(99UL, state.EffectiveTankGuid);
            Assert.Equal(SquadPhase.DungeonClearing, state.Phase);
            Assert.Empty(state.ActiveProvisionerName);
            Assert.Empty(state.ActiveTradeTargetName);

            snapWithPug.HasRandomPartyMembers = false;
            var resumed = coord.EvaluateTick(
                settings, leader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 21_000L,
                dungeon: snapWithPug);
            Assert.Equal("TankA", resumed.ActiveProvisionerName);
        }

        [Fact]
        public void Coordinator_ResolvesDungeonAndPortalForTheEvaluatedMinimumLevel()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.WalkInPortalFarm);
            var leader = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 20);
            var healer = WrapClient(MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 18));
            var decision = coord.EvaluateTick(
                settings, leader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 10_000L,
                resolveDungeon: level => new SquadDungeonSnapshot
                {
                    ActiveDungeonId = level == 18 ? 8U : 6U,
                    PortalX = level == 18 ? 180f : 200f
                });

            Assert.Equal(18, decision.EffectiveSquadMinLevel);
            Assert.Equal(8U, decision.ActiveDungeonId);
            Assert.Equal(180f, decision.DungeonPortalX);
        }

        [Fact]
        public void CompletedRun_RequiresFreshOutsideTelemetryFromTheSameParticipant()
        {
            var member = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 20);
            member.MapId = 1U;
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, Array.Empty<SquadConnectedMemberSnapshot>(), 10_000L, 12_000L));
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { WrapClient(member, 9_999L) }, 10_000L, 12_000L));
            Assert.True(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { WrapClient(member, 11_000L) }, 10_000L, 12_000L));
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { WrapClient(member, 11_000L) }, 10_000L, 18_000L));
            member.HasDungeonTelemetry = false;
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { WrapClient(member, 11_000L) }, 10_000L, 12_000L));
            member.HasDungeonTelemetry = true;
            member.IsInsideDungeon = true;
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { WrapClient(member, 11_500L) }, 10_000L, 12_000L));

            member.IsInsideDungeon = false;
            var pingRefreshedPreCompletionSample = new SquadConnectedMemberSnapshot
            {
                CharacterName = member.CharacterName,
                SlotIndex = 0,
                Handshake = new SquadHandshake { CharacterName = member.CharacterName, Guid = member.Guid, Class = member.Class, Role = member.Role, Level = member.Level },
                LatestState = member,
                LastReceivedUtcMs = 11_500L,
                LastStateReceivedUtcMs = 9_500L,
                IsConnected = true
            };
            Assert.False(SquadDungeonService.HasFreshExitEvidence(2UL, new[] { pingRefreshedPreCompletionSample }, 10_000L, 12_000L));
        }

        [Fact]
        public void PartyComposition_FallsBackToLivingConfiguredTankInsteadOfCoordinator()
        {
            var party = new[] { (1UL, false, true), (2UL, true, false), (3UL, false, true) };
            var bots = new[] { 1UL, 3UL };

            Assert.Equal((true, 3UL), SquadDungeonService.EvaluatePartyComposition(bots, party, new[] { 3UL }));
            Assert.Equal((true, 0UL), SquadDungeonService.EvaluatePartyComposition(bots, party, new[] { 2UL }));
        }

        [Fact]
        public void DungeonPosition_RejectsBreadcrumbWhoseFinalOffsetIsTooCloseToFocus()
        {
            var focus = new WoWPoint(50f, 50f, 5f);
            var tank = new WoWPoint(50f, 55f, 5f);
            var crumbs = new[] { new SquadBreadcrumb { X = 56f, Y = 55f, Z = 5f, MapId = 36U } };

            var position = SquadFormationService.ComputeDungeonStandoffPosition(tank, 0f, focus, crumbs, 36U, 16f, 0, 1);

            Assert.True(position.Distance(focus) >= 16f);
            Assert.True(position.Y > tank.Y);
        }

        [Fact]
        public void DungeonDeathRecovery_WaitsForLivingHealerOnSingleDeath_AndCorpseRunsOnFullWipe()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings();

            var leaderAlive = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 25, insideDungeon: true, isDead: false);
            var healerAlive = WrapClient(MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 25, insideDungeon: true, isDead: false, canRes: true));
            var dpsDead = WrapClient(MakeMemberState("DpsC", 3UL, SquadRole.DpsMelee, WoWClass.Rogue, 25, insideDungeon: true, isDead: true));

            var snap = new SquadDungeonSnapshot { IsInsideDungeon = true, ActiveDungeonId = 6U, ActiveDungeonMapId = 36U };

            var singleDeath = coord.EvaluateTick(
                settings, leaderAlive, new[] { healerAlive, dpsDead }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 10_000L,
                dungeon: snap);
            Assert.Equal(SquadPhase.WaitingResurrection, singleDeath.Phase);

            var leaderDead = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 25, insideDungeon: false, isDead: true);
            healerAlive.LatestState!.IsDead = true;
            healerAlive.LatestState.HealthPercent = 0f;

            var fullWipe = coord.EvaluateTick(
                settings, leaderDead, new[] { healerAlive, dpsDead }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 12_000L,
                dungeon: snap);
            Assert.Equal(SquadPhase.CorpseRun, fullWipe.Phase);
        }

        [Fact]
        public void IndoorDungeonFormation_StaysCompactAndProjectsAlongBreadcrumbs()
        {
            var breadcrumbs = new List<SquadBreadcrumb>
            {
                new() { X = 100f, Y = 0f, Z = 5f, MapId = 36U, Sequence = 1, RouteGeneration = 1 },
                new() { X = 110f, Y = 0f, Z = 5f, MapId = 36U, Sequence = 2, RouteGeneration = 1 },
                new() { X = 120f, Y = 0f, Z = 5f, MapId = 36U, Sequence = 3, RouteGeneration = 1 }
            };

            var tankPos = new WoWPoint(125f, 0f, 5f);
            var bossPos = new WoWPoint(132f, 0f, 5f);

            WoWPoint back0 = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos, 0f, bossPos, breadcrumbs, 36U, 16f, 0, 2);
            WoWPoint back1 = SquadFormationService.ComputeDungeonStandoffPosition(
                tankPos, 0f, bossPos, breadcrumbs, 36U, 16f, 1, 2);

            Assert.True(back0.X < tankPos.X);
            Assert.True(MathF.Abs(back0.Y) <= 2.0f);
            Assert.True(back0.Distance(back1) >= 2.5f);

            var leader = new SquadLeaderState
            {
                X = 125f,
                Y = 0f,
                Z = 5f,
                Facing = 0f,
                MapId = 36U,
                LeaderGuid = 1UL,
                Breadcrumbs = breadcrumbs
            };

            WoWPoint follow0 = SquadFormationService.ComputeDungeonFollowPosition(new WoWPoint(118f, 0f, 5f), leader, 0);
            WoWPoint follow2 = SquadFormationService.ComputeDungeonFollowPosition(new WoWPoint(118f, 0f, 5f), leader, 2);
            Assert.True(follow0.Distance(new WoWPoint(125f, 0f, 5f)) < 5.0f);
            Assert.True(follow2.Distance(new WoWPoint(125f, 0f, 5f)) < 10.0f);
            Assert.True(MathF.Abs(follow2.Y) <= 1.2f);
        }

        [Fact]
        public void LauncherScript_DungeonMode_EmitsDungeonFlagsWithoutRequiringProfilePath()
        {
            string script = PartyGrindConfigWindow.BuildLauncherScript(
                clientCount: 5,
                teamId: "DungeonSquad",
                port: 45880,
                profilePath: string.Empty,
                characterNames: new[] { "WarTank", "PriestHeal", "RogueA", "MageB", "LockC" },
                roleNames: new[] { "TankLeader", "Healer", "DpsMelee", "DpsRanged", "DpsRanged" },
                botMode: SquadBotMode.Dungeon,
                dungeonEntryMode: SquadDungeonEntryMode.WalkInPortalFarm,
                dungeonIdsCsv: "6,163");

            Assert.Contains("-squadmode=Dungeon", script);
            Assert.Contains("-dungeonentry=WalkInPortalFarm", script);
            Assert.Contains("-dungeonids=\"6,163\"", script);
            Assert.DoesNotContain("-loadprofile=", script);
        }

        [Fact]
        public void BossReadinessManaPercent_ClampsBetween45And100()
        {
            var lowTrigger = CreateDungeonSettings();
            lowTrigger.TriggerRestManaPct = 15;
            Assert.Equal(45, SquadDungeonService.GetBossReadinessManaPercent(lowTrigger));

            var midTrigger = CreateDungeonSettings();
            midTrigger.TriggerRestManaPct = 60;
            Assert.Equal(75, SquadDungeonService.GetBossReadinessManaPercent(midTrigger));

            var highTrigger = CreateDungeonSettings();
            highTrigger.TriggerRestManaPct = 95;
            Assert.Equal(100, SquadDungeonService.GetBossReadinessManaPercent(highTrigger));
        }

        [Fact]
        public void HealthstoneProvisioning_ProceedsInDungeonEvenWithRandomPartyMembers()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.LfgRandomNormal);
            settings.EnableWarlockHealthstones = true;

            var warlockLeader = MakeMemberState("TankA", 10UL, SquadRole.TankLeader, WoWClass.Warlock, 30, insideDungeon: true);
            warlockLeader.CanProvideHealthstone = true;
            warlockLeader.HealthstoneMinReceiverLevel = 15;

            var healer = WrapClient(MakeMemberState("HealB", 11UL, SquadRole.Healer, WoWClass.Priest, 30, insideDungeon: true));
            healer.LatestState!.WantsHealthstone = true;
            healer.LatestState.FreeBagSlots = 5;

            var snapWithPug = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 163U,
                ActiveDungeonMapId = 189U,
                HasRandomPartyMembers = true,
                EffectiveTankGuid = 99UL
            };

            var state = coord.EvaluateTick(
                settings, warlockLeader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 10_000L,
                dungeon: snapWithPug);

            Assert.Equal("TankA", state.ActiveProvisionerName);
            Assert.Equal("HealB", state.ActiveTradeTargetName);
            Assert.Equal(SquadPhase.Provisioning, state.Phase);
        }

        [Fact]
        public void ShutdownRuntime_ReleasesScriptMovementBlocks_WhilePreservingUserDisable()
        {
            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.RestoreMovement();
            var initialMover = Navigator.PlayerMover;

            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.DisableMovement(() => true);
            Assert.False(global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);

            var dungeonService = new SquadDungeonService();
            dungeonService.ShutdownRuntime();
            Assert.True(global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);
            Assert.Same(initialMover, Navigator.PlayerMover);

            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.DisableUserMovement();
            Assert.False(global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);

            dungeonService.ShutdownRuntime();
            Assert.False(global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);

            global::Bots.DungeonBuddy.Helpers.ScriptHelpers.RestoreMovement();
            Assert.True(global::Bots.DungeonBuddy.Helpers.ScriptHelpers.MovementEnabled);
        }

        [Fact]
        public void RunParticipantsOutside_RequiresFreshConnectedExitEvidenceForEveryParticipant()
        {
            ulong[] participants = [1UL, 2UL, 3UL];
            var heal = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 30);
            heal.MapId = 1U;
            var dps = MakeMemberState("DpsC", 3UL, SquadRole.DpsMelee, WoWClass.Rogue, 30, insideDungeon: true);
            var staleOutside = WrapClient(heal, 59_000L, isConnected: false);
            var connectedOutside = WrapClient(heal, 59_000L, isConnected: true);

            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [staleOutside, WrapClient(dps, 59_000L)], 10_000L, 60_000L));
            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [connectedOutside, WrapClient(dps, 59_000L)], 10_000L, 60_000L));

            dps.IsInsideDungeon = false;
            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [connectedOutside, WrapClient(dps, 10_000L)], 10_000L, 60_000L));
            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [connectedOutside], 10_000L, 60_000L));

            dps.HasValidMap = false;
            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [connectedOutside, WrapClient(dps, 59_000L)], 10_000L, 60_000L));

            dps.HasValidMap = true;
            Assert.False(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [staleOutside, WrapClient(dps, 59_000L)], 10_000L, 60_000L));
            Assert.True(SquadDungeonService.AreAllRunParticipantsOutside(
                participants, 1UL, [connectedOutside, WrapClient(dps, 59_000L)], 10_000L, 60_000L));
        }

        [Fact]
        public void WalkInCompletion_UsesPendingResetToOrderExitAfterCoordinatorLeaves()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.WalkInPortalFarm);
            var tank = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Warrior, 30);
            var healer = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 30, insideDungeon: true);
            var snapshot = new SquadDungeonSnapshot { ResetPendingOutside = true };

            var state = coordinator.EvaluateTick(settings, tank, [WrapClient(healer)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 10_000L, dungeon: snapshot);

            Assert.Equal(SquadPhase.DungeonCompleteExit, state.Phase);
            Assert.False(state.TeleportOutOrder);
        }

        [Fact]
        public void DungeonBuddyRaidRoster_PreservesSelfAndEmptySlotsWithIndexedUnitIds()
        {
            var roster = global::Bots.DungeonBuddy.DungeonBuddy.CreateRaidRoster([11UL, 0UL, 22UL]);
            Assert.Equal(new ulong[] { 11UL, 0UL, 22UL }, roster.Select(member => member.Guid));
            Assert.Equal(new[] { "raid1", "raid2", "raid3" }, roster.Select(member => member.UnitId));
        }

        [Fact]
        public void WalkInEntryCounting_CountsFirstRunWithoutDoubleCountingReset()
        {
            var dungeonService = new SquadDungeonService();
            long baseTime = 100_000L;

            for (int run = 0; run < 4; run++)
            {
                long t = baseTime + run * 60_000L;
                dungeonService.RecordWalkInEntry(t);
                dungeonService.RecordInstanceReset(t + 30_000L);
            }

            long fifthEntryTime = baseTime + 240_000L;
            Assert.Equal(0, dungeonService.GetLockoutRemainingSeconds(fifthEntryTime, maxInstancesPerHour: 5));
            dungeonService.RecordWalkInEntry(fifthEntryTime);
            Assert.Equal(5, dungeonService.GetHourlyResetCount(fifthEntryTime + 1_000L));
            Assert.True(dungeonService.GetLockoutRemainingSeconds(fifthEntryTime + 1_000L, maxInstancesPerHour: 5) > 0);
            Assert.Equal(0, dungeonService.GetWalkInEntryLockoutRemainingSeconds(fifthEntryTime + 1_000L, maxInstancesPerHour: 5));

            dungeonService.MarkResetPendingOutside();
            Assert.True(dungeonService.GetWalkInEntryLockoutRemainingSeconds(fifthEntryTime + 2_000L, maxInstancesPerHour: 5) > 0);
            dungeonService.RecordInstanceReset(fifthEntryTime + 30_000L);
            Assert.Equal(5, dungeonService.GetHourlyResetCount(fifthEntryTime + 31_000L));
            Assert.True(dungeonService.GetWalkInEntryLockoutRemainingSeconds(fifthEntryTime + 31_000L, maxInstancesPerHour: 5) > 0);
        }

        [Fact]
        public void AdvanceBossBreadcrumb_FollowsCorridorBreadcrumbsBeforeLockingOntoDistantLiveBoss()
        {
            var crumbs = new List<WoWPoint>
            {
                new(10f, 0f, 0f),
                new(30f, 0f, 0f),
                new(60f, 0f, 0f)
            };
            var bossLoc = new WoWPoint(85f, 0f, 0f);
            int idx = 0;

            WoWPoint step1 = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(0f, 0f, 0f), crumbs, bossLoc, bossLoc, 85f, ref idx);
            Assert.Equal(crumbs[0], step1);
            Assert.Equal(0, idx);

            WoWPoint step2 = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(9f, 0f, 0f), crumbs, bossLoc, bossLoc, 76f, ref idx);
            Assert.Equal(crumbs[1], step2);
            Assert.Equal(1, idx);

            WoWPoint step3 = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(29f, 0f, 0f), crumbs, bossLoc, bossLoc, 56f, ref idx);
            Assert.Equal(crumbs[2], step3);
            Assert.Equal(2, idx);

            WoWPoint step4 = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(59f, 0f, 0f), crumbs, bossLoc, bossLoc, 26f, ref idx);
            Assert.Equal(bossLoc, step4);
            Assert.Equal(3, idx);

            int closeIdx = 0;
            WoWPoint closeStep = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(65f, 0f, 0f), crumbs, bossLoc, bossLoc, 20f, ref closeIdx);
            Assert.Equal(bossLoc, closeStep);

            int exhaustedIdx = crumbs.Count;
            var fallbackProfileLoc = new WoWPoint(70f, 0f, 0f);
            WoWPoint unreachableStep = SquadDungeonService.AdvanceBossBreadcrumb(
                new WoWPoint(60f, 0f, 0f), crumbs, fallbackProfileLoc, bossLoc, liveBossPathDistance: null, ref exhaustedIdx);
            Assert.Equal(fallbackProfileLoc, unreachableStep);
        }

        [Fact]
        public void LfgPartyOutsideEntry_SkipsTeleportInAfterCompletedRunSoSquadCanRequeue()
        {
            // Normal fresh LFG entry outside dungeon: teleport in
            Assert.True(SquadDungeonService.ShouldTeleportIntoLfgPartyOutsideDungeon(
                lfgMode: "lfgparty",
                isInDungeonMap: false,
                lfgRunCompletedAwaitingRequeue: false,
                completedReason: global::Bots.DungeonBuddy.Enums.CompleteReason.None,
                leaderDungeonComplete: false));

            // Completed run after teleporting out while party stays in 'lfgparty': must NOT re-enter completed instance
            Assert.False(SquadDungeonService.ShouldTeleportIntoLfgPartyOutsideDungeon(
                lfgMode: "lfgparty",
                isInDungeonMap: false,
                lfgRunCompletedAwaitingRequeue: true,
                completedReason: global::Bots.DungeonBuddy.Enums.CompleteReason.None,
                leaderDungeonComplete: false));

            Assert.False(SquadDungeonService.ShouldTeleportIntoLfgPartyOutsideDungeon(
                lfgMode: "lfgparty",
                isInDungeonMap: false,
                lfgRunCompletedAwaitingRequeue: false,
                completedReason: global::Bots.DungeonBuddy.Enums.CompleteReason.Completed,
                leaderDungeonComplete: false));

            Assert.False(SquadDungeonService.ShouldTeleportIntoLfgPartyOutsideDungeon(
                lfgMode: "lfgparty",
                isInDungeonMap: false,
                lfgRunCompletedAwaitingRequeue: false,
                completedReason: global::Bots.DungeonBuddy.Enums.CompleteReason.None,
                leaderDungeonComplete: true));
        }

        [Fact]
        public void BossManager_RestoreKilledBosses_RestoresKillsAndMarksDead()
        {
            global::Bots.DungeonBuddy.BossManager.Reset();
            global::Bots.DungeonBuddy.BossManager.RegisterBoss(1234, "TestBoss1");
            global::Bots.DungeonBuddy.BossManager.RegisterBoss(5678, "TestBoss2");

            Assert.Equal(2, global::Bots.DungeonBuddy.BossManager.Bosses.Count);
            Assert.True(global::Bots.DungeonBuddy.BossManager.Bosses.All(b => !b.IsDead));
            Assert.Empty(global::Bots.DungeonBuddy.BossManager.KilledBossIds);

            global::Bots.DungeonBuddy.BossManager.RestoreKilledBosses(new uint[] { 1234 });

            Assert.Contains(1234U, global::Bots.DungeonBuddy.BossManager.KilledBossIds);
            Assert.DoesNotContain(5678U, global::Bots.DungeonBuddy.BossManager.KilledBossIds);

            var boss1 = global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.EntryId == 1234);
            var boss2 = global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.EntryId == 5678);
            Assert.True(boss1.IsDead);
            Assert.False(boss2.IsDead);
            Assert.Equal(5678U, global::Bots.DungeonBuddy.BossManager.CurrentBoss?.EntryId);
        }

        [Fact]
        public void DungeonNavBlacklist_ActivatesAtThreeSpacedFailuresAndExpiresAfterCooldown()
        {
            var blacklist = new Dictionary<(int X, int Y, int Z, uint MapId), long>();
            var target = new WoWPoint(2517.19f, 859.79f, 47.68f);
            WoWPoint tracked = WoWPoint.Zero;
            int failures = 0;
            long lastFailMs = 0L;
            long t = 1_000_000L;

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.RecordDungeonStepFailure(
                target, ref tracked, ref failures, ref lastFailMs, t));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.RecordDungeonStepFailure(
                target, ref tracked, ref failures, ref lastFailMs, t + 100L));
            Assert.Equal(1, failures);

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.RecordDungeonStepFailure(
                target, ref tracked, ref failures, ref lastFailMs, t + 600L));
            Assert.Equal(2, failures);

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.RecordDungeonStepFailure(
                target, ref tracked, ref failures, ref lastFailMs, t + 1_200L));
            Assert.Equal(0, failures);

            SquadDungeonService.BlacklistNavPoint(blacklist, 129U, target, t + 1_200L);
            Assert.True(SquadDungeonService.IsNavPointBlacklisted(blacklist, 129U, target, t + 1_200L));
            Assert.False(SquadDungeonService.IsNavPointBlacklisted(blacklist, 1U, target, t + 1_200L));
            Assert.True(SquadDungeonService.IsNavPointBlacklisted(
                blacklist, 129U, target, t + 1_200L + SquadDungeonService.DungeonStepBlacklistCooldownMs - 1L));
            Assert.False(SquadDungeonService.IsNavPointBlacklisted(
                blacklist, 129U, target, t + 1_200L + SquadDungeonService.DungeonStepBlacklistCooldownMs));

            SquadDungeonService.PruneExpiredNavBlacklist(
                blacklist, t + 1_200L + SquadDungeonService.DungeonStepBlacklistCooldownMs);
            Assert.Empty(blacklist);
        }

        [Fact]
        public void DungeonStepProgress_FlagsStallOnMicroOscillationWithoutAdvancing()
        {
            float bestStep = float.MaxValue;
            float bestObj = float.MaxValue;
            long lastProgress = 0L;
            long t0 = 5_000_000L;

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonStepProgress(
                18.0f, 92.6f, ref bestStep, ref bestObj, ref lastProgress, t0));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonStepProgress(
                17.2f, 92.0f, ref bestStep, ref bestObj, ref lastProgress, t0 + 3_000L));
            Assert.Equal(t0, lastProgress);

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonStepProgress(
                17.4f, 92.3f, ref bestStep, ref bestObj, ref lastProgress,
                t0 + SquadDungeonService.DungeonStepStallTimeoutMs));

            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.EvaluateDungeonStepProgress(
                15.0f, 90.0f, ref bestStep, ref bestObj, ref lastProgress,
                t0 + SquadDungeonService.DungeonStepStallTimeoutMs + 100L));
            Assert.Equal(t0 + SquadDungeonService.DungeonStepStallTimeoutMs + 100L, lastProgress);
        }

        private sealed class FakeScriptOnlyEncounterDungeon : global::Bots.DungeonBuddy.Dungeon
        {
            public override uint DungeonId => 20U;
            public override string Name => "Fake RFD";

            [global::Bots.DungeonBuddy.Attributes.EncounterHandler(7356, "Plaguemaw the Rotting")]
            public TreeSharp.Composite PlaguemawHandler() => new TreeSharp.PrioritySelector();
        }

        [Fact]
        public void BossManager_ScriptOnlyEncountersBecomeOptional_AndFinalBossCompletesDungeonUnconditionally()
        {
            global::Bots.DungeonBuddy.BossManager.Reset();
            var xml = System.Xml.Linq.XElement.Parse(@"
                <DungeonBuddyProfile>
                    <Name>Razorfen Downs</Name>
                    <DungeonId>20</DungeonId>
                    <BossEncounters>
                        <Boss isFinal=""false"" entry=""7355"" killOrder=""1"" optional=""true"" name=""Tuten'kash"" />
                        <Boss isFinal=""false"" entry=""7357"" killOrder=""2"" optional=""false"" name=""Mordresh Fire Eye"" />
                        <Boss isFinal=""true"" entry=""7358"" killOrder=""3"" optional=""false"" name=""Amnennar the Coldbringer"" />
                    </BossEncounters>
                </DungeonBuddyProfile>");
            var profile = global::Bots.DungeonBuddy.Profiles.Profile.Load(xml, out var errors);
            Assert.Empty(errors);

            global::Bots.DungeonBuddy.BossManager.PopulateFromProfileAndType(
                profile,
                typeof(FakeScriptOnlyEncounterDungeon));

            var plaguemaw = global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.Entry == 7356);
            var amnennar = global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.Entry == 7358);
            Assert.True(plaguemaw.IsOptional);
            Assert.False(plaguemaw.IsFinal);
            Assert.True(amnennar.IsFinal);

            global::Bots.DungeonBuddy.BossManager.MarkBossDead(7358);
            Assert.Null(global::Bots.DungeonBuddy.BossManager.CurrentBoss);

            var (killed, total, finalDead, complete) = SquadDungeonService.EvaluateBossProgress(
                global::Bots.DungeonBuddy.BossManager.Bosses,
                global::Bots.DungeonBuddy.BossManager.CurrentBoss,
                killOptionalBosses: true,
                isHorde: true,
                inDungeon: true,
                global::Bots.DungeonBuddy.Enums.CompleteReason.None);

            Assert.True(finalDead);
            Assert.True(complete);
            Assert.Equal(1, killed);
            Assert.Equal(4, total);
        }

        [Fact]
        public void DungeonResetStateMachine_SuspendsTeleportOnCombat_AndDoesNotAdvanceOnNullSnapshot()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.LfgSpecificByBracket);
            var leader = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Paladin, 39, insideDungeon: true);
            var healer = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 39, insideDungeon: true);
            var snapInside = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 20U,
                ActiveDungeonMapId = 129U,
                IsDungeonComplete = true,
                IsInPartyOrRaid = true
            };

            var exiting = coord.EvaluateTick(settings, leader, [WrapClient(healer, 10_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 10_000L, dungeon: snapInside);
            Assert.Equal(SquadDungeonResetPhase.Exiting, exiting.DungeonResetPhase);
            Assert.True(exiting.TeleportOutOrder);

            healer.IsInCombat = true;
            var resumedCombat = coord.EvaluateTick(settings, leader, [WrapClient(healer, 11_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 11_000L, dungeon: snapInside);
            Assert.Equal(SquadPhase.DungeonClearing, resumedCombat.Phase);
            Assert.False(resumedCombat.TeleportOutOrder);

            healer.IsInCombat = false;
            leader.IsInsideDungeon = false;
            leader.MapId = 1U;
            healer.IsInsideDungeon = false;
            healer.MapId = 1U;
            var snapOutside = new SquadDungeonSnapshot { IsInsideDungeon = false, IsInPartyOrRaid = true };
            var disbanding = coord.EvaluateTick(settings, leader, [WrapClient(healer, 12_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 12_000L, dungeon: snapOutside, instanceResetGeneration: 3);
            Assert.Equal(SquadDungeonResetPhase.Disbanding, disbanding.DungeonResetPhase);

            coord.IsPausedManually = true;
            var paused = coord.EvaluateTick(settings, leader, [WrapClient(healer, 13_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 13_000L, dungeon: null, instanceResetGeneration: 3);
            Assert.Equal(SquadDungeonResetPhase.Disbanding, paused.DungeonResetPhase);
            Assert.Equal(SquadPhase.Paused, paused.Phase);

            coord.IsPausedManually = false;
            var resumedDisband = coord.EvaluateTick(settings, leader, [WrapClient(healer, 14_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 14_000L, dungeon: snapOutside, instanceResetGeneration: 3);
            Assert.Equal(SquadDungeonResetPhase.Disbanding, resumedDisband.DungeonResetPhase);
        }

        [Fact]
        public void BossManager_FiltersFactionRestrictedBosses()
        {
            global::Bots.DungeonBuddy.BossManager.Reset();
            global::Bots.DungeonBuddy.BossManager.RegisterBoss(
                6907, "Olaf", isOptional: true, isFinal: false,
                faction: global::Bots.DungeonBuddy.Enums.BossAvailableToFaction.Horde);
            global::Bots.DungeonBuddy.BossManager.RegisterBoss(
                2748, "Archaedas", isOptional: false, isFinal: true);

            var (_, allianceTotal, _, _) = SquadDungeonService.EvaluateBossProgress(
                global::Bots.DungeonBuddy.BossManager.Bosses,
                global::Bots.DungeonBuddy.BossManager.CurrentBoss,
                killOptionalBosses: true,
                isHorde: false,
                inDungeon: true,
                global::Bots.DungeonBuddy.Enums.CompleteReason.None);
            var (_, hordeTotal, _, _) = SquadDungeonService.EvaluateBossProgress(
                global::Bots.DungeonBuddy.BossManager.Bosses,
                global::Bots.DungeonBuddy.BossManager.CurrentBoss,
                killOptionalBosses: true,
                isHorde: true,
                inDungeon: true,
                global::Bots.DungeonBuddy.Enums.CompleteReason.None);

            Assert.Equal(1, allianceTotal);
            Assert.Equal(2, hordeTotal);

            var sharedProfileXml = System.Xml.Linq.XElement.Parse(@"
                <DungeonBuddyProfile>
                    <Name>Faction Test</Name>
                    <DungeonId>70</DungeonId>
                    <BossEncounters>
                        <Boss entry=""6907"" killOrder=""1"" optional=""false"" faction=""Horde"" name=""HordeLast"" />
                        <Boss entry=""6910"" killOrder=""2"" optional=""false"" faction=""Alliance"" name=""AllianceLast"" />
                    </BossEncounters>
                </DungeonBuddyProfile>");
            var factionProfile = global::Bots.DungeonBuddy.Profiles.Profile.Load(sharedProfileXml, out var errors);
            Assert.Empty(errors);
            global::Bots.DungeonBuddy.BossManager.PopulateFromProfileAndType(factionProfile, null);
            Assert.True(global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.Entry == 6907).IsFinal);
            Assert.True(global::Bots.DungeonBuddy.BossManager.Bosses.First(b => b.Entry == 6910).IsFinal);

            var sharedBeforeHordeXml = System.Xml.Linq.XElement.Parse(@"
                <DungeonBuddyProfile>
                    <Name>Shared Then Horde</Name>
                    <DungeonId>71</DungeonId>
                    <BossEncounters>
                        <Boss entry=""1001"" killOrder=""1"" optional=""false"" name=""SharedBoss"" />
                        <Boss entry=""1002"" killOrder=""2"" optional=""false"" faction=""Horde"" name=""HordeFinalBoss"" />
                    </BossEncounters>
                </DungeonBuddyProfile>");
            var sharedBeforeHordeProfile = global::Bots.DungeonBuddy.Profiles.Profile.Load(sharedBeforeHordeXml, out errors);
            Assert.Empty(errors);
            global::Bots.DungeonBuddy.BossManager.PopulateFromProfileAndType(sharedBeforeHordeProfile, null);
            global::Bots.DungeonBuddy.BossManager.MarkBossDead(1001);
            Assert.NotNull(global::Bots.DungeonBuddy.BossManager.SelectCurrentBoss(false, isHorde: true));
            Assert.Null(global::Bots.DungeonBuddy.BossManager.SelectCurrentBoss(false, isHorde: false));

            var dungeonService = new SquadDungeonService();
            dungeonService.MarkResetPendingOutside();
            Assert.True(dungeonService.IsResetPendingOutside);
            dungeonService.ShutdownRuntime();
            Assert.False(dungeonService.IsResetPendingOutside);
        }

        [Fact]
        public void DungeonResetStateMachine_TransitionsLootWindowToExitDisbandResetReinviteAndIdle()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateDungeonSettings(SquadDungeonEntryMode.LfgSpecificByBracket);
            settings.MaxLootWindowSeconds = 8;

            var leader = MakeMemberState("TankA", 1UL, SquadRole.TankLeader, WoWClass.Paladin, 39, insideDungeon: true);
            var healer = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 39, insideDungeon: true);
            leader.IsInCombat = true;
            healer.IsInCombat = true;

            var snapInside = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 20U,
                ActiveDungeonMapId = 129U,
                IsDungeonComplete = false,
                IsInPartyOrRaid = true,
                AllExpectedMembersInParty = true
            };

            coord.EvaluateTick(settings, leader, [WrapClient(healer, 10_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 10_000L, dungeon: snapInside, instanceResetGeneration: 0);

            leader.IsInCombat = false;
            healer.IsInCombat = false;
            snapInside.IsDungeonComplete = true;

            var completed = coord.EvaluateTick(settings, leader, [WrapClient(healer, 12_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 12_000L, dungeon: snapInside, instanceResetGeneration: 0);
            Assert.Equal(SquadDungeonResetPhase.Completed, completed.DungeonResetPhase);
            Assert.Equal(SquadPhase.Looting, completed.Phase);
            Assert.True(completed.SuspendAutoInvite);
            Assert.False(completed.AcceptLfgProposalOrder);

            var exiting = coord.EvaluateTick(settings, leader, [WrapClient(healer, 25_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 25_000L, dungeon: snapInside, instanceResetGeneration: 0);
            Assert.Equal(SquadDungeonResetPhase.Exiting, exiting.DungeonResetPhase);
            Assert.Equal(SquadPhase.DungeonCompleteExit, exiting.Phase);
            Assert.True(exiting.TeleportOutOrder);
            Assert.True(exiting.SuspendAutoInvite);

            leader.IsInsideDungeon = false;
            leader.MapId = 1U;
            healer.IsInsideDungeon = false;
            healer.MapId = 1U;
            var snapOutside = new SquadDungeonSnapshot
            {
                IsInsideDungeon = false,
                IsInPartyOrRaid = true,
                AllExpectedMembersInParty = true,
                LfgProposalPending = true
            };

            var disbanding = coord.EvaluateTick(settings, leader, [WrapClient(healer, 27_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 27_000L, dungeon: snapOutside, instanceResetGeneration: 0);
            Assert.Equal(SquadDungeonResetPhase.Disbanding, disbanding.DungeonResetPhase);
            Assert.True(disbanding.SuspendAutoInvite);
            Assert.False(disbanding.TeleportOutOrder);
            Assert.False(disbanding.AcceptLfgProposalOrder);

            snapOutside.IsInPartyOrRaid = false;
            snapOutside.AllExpectedMembersInParty = false;
            var resetting = coord.EvaluateTick(settings, leader, [WrapClient(healer, 28_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 28_000L, dungeon: snapOutside, instanceResetGeneration: 0);
            Assert.Equal(SquadDungeonResetPhase.Resetting, resetting.DungeonResetPhase);
            Assert.True(resetting.SuspendAutoInvite);

            var reinviting = coord.EvaluateTick(settings, leader, [WrapClient(healer, 29_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 29_000L, dungeon: snapOutside, instanceResetGeneration: 1);
            Assert.Equal(SquadDungeonResetPhase.Reinviting, reinviting.DungeonResetPhase);
            Assert.False(reinviting.SuspendAutoInvite);

            var stillWaitingFullParty = coord.EvaluateTick(settings, leader, [WrapClient(healer, 30_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 30_000L, dungeon: snapOutside, instanceResetGeneration: 1);
            Assert.Equal(SquadDungeonResetPhase.Reinviting, stillWaitingFullParty.DungeonResetPhase);
            Assert.False(stillWaitingFullParty.AcceptLfgProposalOrder);

            snapOutside.IsInPartyOrRaid = true;
            snapOutside.AllExpectedMembersInParty = true;
            var readyToQueue = coord.EvaluateTick(settings, leader, [WrapClient(healer, 31_000L)], new SquadProfileSnapshot(),
                0, 0, 0, 0f, 31_000L, dungeon: snapOutside, instanceResetGeneration: 1);
            Assert.Equal(SquadDungeonResetPhase.Idle, readyToQueue.DungeonResetPhase);
            Assert.Equal(SquadPhase.DungeonQueueing, readyToQueue.Phase);
            Assert.True(readyToQueue.AcceptLfgProposalOrder);
        }

        [Fact]
        public void SquadPartySync_UninviteScriptAndInviteTrackerReset_WorkForResetCycle()
        {
            string script = SquadPartySyncService.BuildUninviteAllScript();
            Assert.Contains("UninviteUnit", script);
            Assert.Contains("IsPartyLeader", script);
            Assert.Contains("IsRaidLeader", script);

            string inGroupScript = SquadDungeonService.BuildIsInPartyOrRaidScript();
            string emptyResetScript = SquadDungeonService.BuildEmptyGroupResetScript();
            Assert.DoesNotContain("true", inGroupScript);
            Assert.DoesNotContain("false", inGroupScript);
            Assert.Contains("and 1 or 0", inGroupScript);
            Assert.DoesNotContain("true", emptyResetScript);
            Assert.DoesNotContain("false", emptyResetScript);
            Assert.Contains("return 1", emptyResetScript);
            Assert.Contains("return 0", emptyResetScript);

            var tracker = new SquadInviteTracker();
            int invites = 0;
            tracker.Check("HealB", 1_000L, mayInvite =>
            {
                if (mayInvite) invites++;
                return "invited";
            });
            tracker.Check("HealB", 2_000L, mayInvite =>
            {
                if (mayInvite) invites++;
                return "waiting";
            });
            Assert.Equal(1, invites);

            tracker.Reset();
            tracker.Check("HealB", 2_100L, mayInvite =>
            {
                if (mayInvite) invites++;
                return "invited";
            });
            Assert.Equal(2, invites);
        }
    }
}
