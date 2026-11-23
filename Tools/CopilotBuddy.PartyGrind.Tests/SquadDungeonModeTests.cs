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

            leader.IsInsideDungeon = false;
            leader.MapId = 0U;
            var leaderOutside = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 31_000L,
                dungeon: snapOutside);
            Assert.Equal(SquadPhase.DungeonCompleteExit, leaderOutside.Phase);
            Assert.True(leaderOutside.TeleportOutOrder);

            heal.LatestState.IsInsideDungeon = false;
            dps.LatestState.IsInsideDungeon = false;
            var allOutside = coord.EvaluateTick(
                settings, leader, new[] { heal, dps }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 32_000L,
                dungeon: snapOutside);
            Assert.Equal(SquadPhase.DungeonQueueing, allOutside.Phase);
            Assert.False(allOutside.TeleportOutOrder);
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
        public void WalkInResetWait_RequiresFreshExitEvidenceUntilParticipantLeavesGameGroup()
        {
            ulong[] participants = [2UL, 3UL];
            ulong[] fullGroup = [1UL, 2UL, 3UL];
            var heal = MakeMemberState("HealB", 2UL, SquadRole.Healer, WoWClass.Priest, 30);
            heal.MapId = 1U;
            var dps = MakeMemberState("DpsC", 3UL, SquadRole.DpsMelee, WoWClass.Rogue, 30, insideDungeon: true);
            var outside = WrapClient(heal, 59_000L, isConnected: false);

            Assert.False(SquadDungeonService.AreAllWalkInParticipantsClearedForReset(
                participants, [outside], fullGroup, 10_000L, 60_000L));
            Assert.False(SquadDungeonService.AreAllWalkInParticipantsClearedForReset(
                participants, [outside, WrapClient(dps, 59_000L)], fullGroup, 10_000L, 60_000L));

            dps.IsInsideDungeon = false;
            Assert.False(SquadDungeonService.AreAllWalkInParticipantsClearedForReset(
                participants, [outside, WrapClient(dps, 11_000L)], fullGroup, 10_000L, 60_000L));
            Assert.True(SquadDungeonService.AreAllWalkInParticipantsClearedForReset(
                participants, [outside, WrapClient(dps, 59_000L)], fullGroup, 10_000L, 60_000L));
            Assert.True(SquadDungeonService.AreAllWalkInParticipantsClearedForReset(
                participants, [outside], [1UL, 2UL], 10_000L, 60_000L));
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
    }
}
