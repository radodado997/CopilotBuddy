using System;
using System.Collections.Generic;
using System.Linq;
using CopilotBuddy.Bots.PartyGrind.Forms;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
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
                LowestDurabilityPercent = 100f,
                MapId = insideDungeon ? 36U : 0U,
                IsInsideDungeon = insideDungeon,
                IsDead = isDead,
                CanResurrect = canRes,
                LfgProposalPending = proposalPending,
                TimestampUtcMs = 10_000L
            };
        }

        private static SquadConnectedMemberSnapshot WrapClient(SquadMemberState state)
        {
            return new SquadConnectedMemberSnapshot
            {
                CharacterName = state.CharacterName,
                IsConnected = true,
                LastSeenUtcMs = state.TimestampUtcMs,
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
            settings.AllowCrossClientProvisioning = true;

            var leader = MakeMemberState("TankA", 10UL, SquadRole.TankLeader, WoWClass.Mage, 30, insideDungeon: true);
            leader.CanConjureWater = true;
            var healer = WrapClient(MakeMemberState("HealB", 11UL, SquadRole.Healer, WoWClass.Priest, 30, insideDungeon: true));
            healer.LatestState!.ConjuredWaterCount = 0;

            var snapWithPug = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 163U,
                ActiveDungeonMapId = 189U,
                HasRandomPartyMembers = true,
                EffectiveTankGuid = 99UL
            };

            var state = coord.EvaluateTick(
                settings, leader, new[] { healer }, new SquadProfileSnapshot(), 0, 0, 0, 0f, 20_000L,
                dungeon: snapWithPug);

            Assert.True(state.HasRandomPartyMembers);
            Assert.Equal(99UL, state.EffectiveTankGuid);
            Assert.Equal(SquadPhase.DungeonClearing, state.Phase);
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
    }
}
