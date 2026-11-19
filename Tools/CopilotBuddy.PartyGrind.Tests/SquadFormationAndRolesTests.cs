using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Forms;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Styx.Logic.Relogin;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadFormationAndRolesTests : IDisposable
    {
        private readonly string _tempDir;

        public SquadFormationAndRolesTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PartyGrindFormTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        [Fact]
        public void BreadcrumbFollow_AdvancesPastReachedPoint()
        {
            var leader = new SquadLeaderState { X = 100, MapId = 0, Breadcrumbs = new List<SquadBreadcrumb>
            {
                new() { X = 0 }, new() { X = 10 }, new() { X = 20 }
            } };
            var next = SquadFormationService.ComputeTravelFollowPosition(new WoWPoint(10, 0, 0), leader, 0);
            Assert.Equal(20f, next.X);
        }

        [Fact]
        public void BreadcrumbFollow_RetainsProgressAndResetsForNewRoute()
        {
            var progress = new SquadTravelProgress();
            var leader = new SquadLeaderState { X = 100, MapId = 1, LeaderGuid = 10,
                Breadcrumbs = new List<SquadBreadcrumb>
                {
                    new() { X = 0, MapId = 1, TimestampUtcMs = 1, RouteGeneration = 7, Sequence = 1 },
                    new() { X = 10, MapId = 1, TimestampUtcMs = 2, RouteGeneration = 7, Sequence = 2 },
                    new() { X = 20, MapId = 1, TimestampUtcMs = 3, RouteGeneration = 7, Sequence = 3 },
                    new() { X = 30, MapId = 1, TimestampUtcMs = 4, RouteGeneration = 7, Sequence = 4 }
                } };
            Assert.Equal(20f, SquadFormationService.ComputeTravelFollowPosition(new WoWPoint(10, 0, 0), leader, 0, progress).X);
            Assert.Equal(30f, SquadFormationService.ComputeTravelFollowPosition(new WoWPoint(20, 0, 0), leader, 0, progress).X);
            Assert.Equal(30f, SquadFormationService.ComputeTravelFollowPosition(new WoWPoint(10, 0, 0), leader, 0, progress).X);
            Assert.Equal(3, progress.LastReachedSequence);
            leader.LeaderGuid = 11;
            Assert.Equal(20f, SquadFormationService.ComputeTravelFollowPosition(new WoWPoint(10, 0, 0), leader, 0, progress).X);
            leader.MapId = 2;
            foreach (var breadcrumb in leader.Breadcrumbs)
            {
                breadcrumb.MapId = 2;
                breadcrumb.RouteGeneration = 8;
            }
            Assert.Equal(10f, SquadFormationService.ComputeTravelFollowPosition(WoWPoint.Zero, leader, 0, progress).X);
            Assert.Equal(1, progress.LastReachedSequence);
            Assert.Equal(8, progress.RouteGeneration);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1.047f)]
        [InlineData(3.14159f)]
        [InlineData(4.712f)]
        public void FormationMath_MeleeAndBacklineSlotsNeverOverlap(float facingRad)
        {
            var mobPos = new WoWPoint(200f, 300f, 10f);
            var tankPos = new WoWPoint(197f, 297f, 10f);

            // 3 Melee DPS in fan behind mob
            var meleeSlots = new List<WoWPoint>();
            for (int slot = 0; slot < 3; slot++)
            {
                meleeSlots.Add(SquadFormationService.ComputeMeleeFlankPosition(
                    mobPos, facingRad, meleeFlankRadius: 2f, meleeSlotIndex: slot, totalMeleeCount: 3));
            }

            for (int i = 0; i < meleeSlots.Count; i++)
            {
                for (int j = i + 1; j < meleeSlots.Count; j++)
                {
                    float sep = meleeSlots[i].Distance2D(meleeSlots[j]);
                    Assert.True(sep >= 2.0f, $"Melee slots {i} and {j} too close: {sep:F2} yd");
                }
            }

            // 2 Ranged DPS + 1 Healer behind tank at 20 yd standoff
            var backlineSlots = new List<WoWPoint>();
            for (int slot = 0; slot < 3; slot++)
            {
                WoWPoint p = SquadFormationService.ComputeStandoffPosition(
                    tankPos, facingRad, mobPos, standoffDistance: 20f, backlineSlotIndex: slot, totalBacklineCount: 3);
                backlineSlots.Add(p);

                float radial = p.Distance2D(tankPos);
                Assert.InRange(radial, 17.9f, 24.5f);
            }

            for (int i = 0; i < backlineSlots.Count; i++)
            {
                for (int j = i + 1; j < backlineSlots.Count; j++)
                {
                    float sep = backlineSlots[i].Distance2D(backlineSlots[j]);
                    Assert.True(sep >= 3.8f, $"Backline slots {i} and {j} too close: {sep:F2} yd");
                }
            }
        }

        [Fact]
        public void FourMeleeSlots_StayTwoYardsApartAtMinimumRadius()
        {
            var slots = Enumerable.Range(0, 4)
                .Select(i => SquadFormationService.ComputeMeleeFlankPosition(WoWPoint.Zero, 0, 2, i, 4)).ToArray();
            for (int i = 0; i < slots.Length; i++)
                for (int j = i + 1; j < slots.Length; j++)
                    Assert.True(slots[i].Distance(slots[j]) >= 2f);
        }

        [Fact]
        public void ResolveSubRoleSlot_PlacesHealerInCenterBetweenTwoRanged()
        {
            var leader = new SquadLeaderState
            {
                Members = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary { CharacterName = "Tank", Role = SquadRole.TankLeader },
                    new SquadMemberSummary { CharacterName = "Heal", Role = SquadRole.Healer },
                    new SquadMemberSummary { CharacterName = "MageA", Role = SquadRole.DpsRanged },
                    new SquadMemberSummary { CharacterName = "LockB", Role = SquadRole.DpsRanged }
                }
            };

            var (mageIdx, countM) = SquadFormationService.ResolveSubRoleSlot("MageA", SquadRole.DpsRanged, leader);
            var (lockIdx, countL) = SquadFormationService.ResolveSubRoleSlot("LockB", SquadRole.DpsRanged, leader);
            var (healIdx, countH) = SquadFormationService.ResolveSubRoleSlot("Heal", SquadRole.Healer, leader);

            Assert.Equal(3, countM);
            Assert.Equal(3, countL);
            Assert.Equal(3, countH);
            Assert.Equal(1, mageIdx);
            Assert.Equal(0, lockIdx);
            Assert.Equal(2, healIdx); // center index for 3-slot lateral pattern (-4.5, +4.5, 0)
        }

        [Fact]
        public void MountPolicy_SyncWithSlowestBlocksMountWhenAnyMemberTooLow()
        {
            var leader = new SquadLeaderState
            {
                Members = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary { CharacterName = "Tank", IsConnected = true, CanMount = true },
                    new SquadMemberSummary { CharacterName = "Heal", IsConnected = true, CanMount = false },
                    new SquadMemberSummary { CharacterName = "Dps", IsConnected = true, CanMount = true }
                }
            };

            Assert.False(SquadFormationService.CanSquadMount(SquadMountPolicy.SyncWithSlowestMember, true, leader));
            Assert.True(SquadFormationService.CanSquadMount(SquadMountPolicy.MountAndWaitAtCheckpoints, true, leader));
            Assert.False(SquadFormationService.CanSquadMount(SquadMountPolicy.NeverMount, true, leader));

            leader.Members[1].CanMount = true;
            Assert.True(SquadFormationService.CanSquadMount(SquadMountPolicy.SyncWithSlowestMember, true, leader));
        }

        [Fact]
        public void HealAndTankBridges_EnforceFocusAndPriorityRules()
        {
            // Tank target weights prioritize Healer peel > general party peel > squad combat > pull
            double healerPeel = SquadTankBridge.ComputeTankTargetBonus(true, true, true);
            double dpsPeel = SquadTankBridge.ComputeTankTargetBonus(true, false, true);
            double combatMob = SquadTankBridge.ComputeTankTargetBonus(false, false, true);
            Assert.True(healerPeel > dpsPeel && dpsPeel > combatMob);

            // Non-tank DPS must NOT pull unrelated idle mob
            var leader = new SquadLeaderState { FocusTargetGuid = 5001UL, SecondaryTargetGuid = 5002UL };
            Assert.False(SquadTankBridge.IsAllowedDpsTarget(9999UL, unitInCombat: false, false, false, 0UL, leader, 10f, true));
            Assert.True(SquadTankBridge.IsAllowedDpsTarget(5001UL, unitInCombat: false, false, false, 0UL, leader, 10f, true));

            // Healer gives -15% TankLeader priority bonus (Tank 50% beats DPS 42% because 50-15=35 < 42)
            var candidates = new[]
            {
                new SquadHealCandidate { Guid = 1, Name = "Dps", HealthPercent = 42.0, Distance = 15.0, IsTankLeader = false },
                new SquadHealCandidate { Guid = 2, Name = "Tank", HealthPercent = 50.0, Distance = 20.0, IsTankLeader = true }
            };
            var pick = SquadHealBridge.SelectPriorityHealTarget(candidates, 92.0, prioritizeSelfCritical: true);
            Assert.NotNull(pick);
            Assert.Equal("Tank", pick!.Name);
        }

        [Fact]
        public void Infrastructure_InstanceIsolationPidParsingStrictReloginAndLauncherScript()
        {
            Assert.Equal(4321, CommandLine.ParseProcessId(new[] { "CopilotBuddy.exe", "-pid=4321" }));
            Assert.Equal(9876, CommandLine.ParseProcessId(new[] { "CopilotBuddy.exe", "/pid=9876" }));
            Assert.Equal(0, CommandLine.ParseProcessId(new[] { "CopilotBuddy.exe", "-pid=abc" }));
            Assert.Equal(0, CommandLine.ParseProcessId(new[] { "CopilotBuddy.exe", "-pid=123", "-pid" }));
            Assert.Null(CommandLine.ParseProcessId(new[] { "CopilotBuddy.exe" }));

            string defaultDir = Settings.GetSettingsDirectory(@"C:\Bot", null);
            string client2Dir = Settings.GetSettingsDirectory(@"C:\Bot", "Client2");
            Assert.Equal(Path.Combine(@"C:\Bot", "Settings"), defaultDir);
            Assert.Equal(Path.Combine(@"C:\Bot", "Settings", "Instances", "Client2"), client2Dir);

            Assert.True(ReloginManager.ShouldDefaultStrictProcessBinding(1234, null));
            Assert.True(ReloginManager.ShouldDefaultStrictProcessBinding(0, "Client1"));
            Assert.False(ReloginManager.ShouldDefaultStrictProcessBinding(0, null));
            Assert.False(ReloginManager.ShouldAllowNameFallback(strictProcessBinding: true, trackedPid: 0));
            Assert.True(ReloginManager.ShouldAllowNameFallback(strictProcessBinding: false, trackedPid: 0));

            string script = PartyGrindConfigWindow.BuildLauncherScript(
                clientCount: 5,
                teamId: "TeamFive",
                port: 1339,
                profilePath: @"C:\Profiles\Multi.xml",
                characterNames: new[] { "TankA", "HealB", "RogueC", "MageD", "ShamE" },
                roleNames: new[] { "TankLeader", "Healer", "DpsMelee", "DpsRanged", "HybridOffHeal" });

            Assert.Contains("-instance=Client1", script);
            Assert.Contains("-instance=Client5", script);
            Assert.Contains("-pid=%PID1%", script);
            Assert.Contains("-pid=%PID5%", script);
            Assert.Contains("-botname=\"Party Grind\"", script);
            Assert.Contains("-squadcoordinator=true", script);
            Assert.Contains("-squadcoordinator=false", script);
            Assert.Contains("-squadteam=%TEAM_ID%", script);
            Assert.Contains("-squadroster=\"TankA,HealB,RogueC,MageD,ShamE\"", script);
        }
    }
}
