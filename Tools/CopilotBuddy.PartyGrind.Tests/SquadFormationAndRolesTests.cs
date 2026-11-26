using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Forms;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Combat.CombatRoutine;
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
        public void HealBridge_ConservesManaOutOfCombatAndPreservesActiveDrinking()
        {
            // Combat heals up to 75%, out-of-combat healing stays below the 50% rest trigger,
            // an active drink is only interrupted below 35%.
            Assert.Equal(75.0, SquadHealBridge.ComputeEffectiveHealThreshold(false, anyCombat: true, healerActivelyDrinkingOrEating: false, healerInRestPhase: false));
            Assert.Equal(40.0, SquadHealBridge.ComputeEffectiveHealThreshold(false, anyCombat: false, healerActivelyDrinkingOrEating: false, healerInRestPhase: false));
            Assert.Equal(40.0, SquadHealBridge.ComputeEffectiveHealThreshold(false, anyCombat: false, healerActivelyDrinkingOrEating: false, healerInRestPhase: true));
            Assert.Equal(35.0, SquadHealBridge.ComputeEffectiveHealThreshold(false, anyCombat: false, healerActivelyDrinkingOrEating: true, healerInRestPhase: true));

            var knownPriestSpells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Power Word: Shield", "Renew", "Flash Heal", "Heal", "Lesser Heal"
            };

            // Out of combat at 84% HP: must NOT spend mana on PW:S, Renew, or Heal
            var oocMinor = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 84.0,
                isTank: true,
                inCombat: false,
                emergencyOnly: false,
                injured78Count: 0,
                injured82Count: 0,
                hasWeakenedSoul: false,
                hasAuraFromHealer: _ => false,
                hasSpell: knownPriestSpells.Contains,
                canCastSpell: knownPriestSpells.Contains);
            Assert.Null(oocMinor.SpellName);

            // In combat at 86% HP with Renew already active: must NOT cast full Heal/Lesser Heal on minor scratch
            var combatMinorWithRenew = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 86.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 0,
                injured82Count: 0,
                hasWeakenedSoul: true,
                hasAuraFromHealer: aura => aura == "Renew",
                hasSpell: knownPriestSpells.Contains,
                canCastSpell: knownPriestSpells.Contains);
            Assert.Null(combatMinorWithRenew.SpellName);

            // In combat at 70% HP with Renew & Weakened Soul: chooses efficient Heal
            var combatModerate = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 70.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: true,
                hasAuraFromHealer: aura => aura == "Renew",
                hasSpell: knownPriestSpells.Contains,
                canCastSpell: knownPriestSpells.Contains);
            Assert.Equal("Heal", combatModerate.SpellName);

            // Low-mana fallback: knows Greater Heal & Heal & Lesser Heal, but only Lesser Heal is affordable
            var allPriestHeals = new HashSet<string>(knownPriestSpells, StringComparer.OrdinalIgnoreCase) { "Greater Heal" };
            var lowManaFallback = SquadHealBridge.EvaluateClassHealSpell(
                WoWClass.Priest,
                hp: 48.0,
                isTank: true,
                inCombat: true,
                emergencyOnly: false,
                injured78Count: 1,
                injured82Count: 1,
                hasWeakenedSoul: true,
                hasAuraFromHealer: aura => aura == "Renew",
                hasSpell: allPriestHeals.Contains,
                canCastSpell: spell => spell == "Lesser Heal");
            Assert.Equal("Lesser Heal", lowManaFallback.SpellName);
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

        [Theory]
        [InlineData(5.0f)]
        [InlineData(10.0f)]
        [InlineData(15.0f)]
        [InlineData(20.0f)]
        [InlineData(40.0f)]
        public void TravelFollowPositions_AllFourFollowersPlusToleranceStayWithinResumeDistance(float resumeDistance)
        {
            string settingsPath = Path.Combine(_tempDir, $"squad_settings_{Guid.NewGuid():N}.xml");
            var settings = new SquadSettings(settingsPath)
            {
                ResumeDistance = resumeDistance
            };
            var leader = new SquadLeaderState
            {
                LeaderGuid = 1UL,
                LeaderName = "Bedogie",
                MapId = 1,
                X = -1024f,
                Y = -2750f,
                Z = 92f,
                Facing = 1.2f,
                ResumeDistance = settings.ResumeDistance,
                Members = new List<SquadMemberSummary>
                {
                    new() { CharacterName = "Bedogie", Guid = 1UL, Role = SquadRole.TankLeader },
                    new() { CharacterName = "Bedoes", Guid = 2UL, Role = SquadRole.DpsRanged },
                    new() { CharacterName = "Belmondo", Guid = 3UL, Role = SquadRole.DpsMelee },
                    new() { CharacterName = "Ksionc", Guid = 4UL, Role = SquadRole.Healer },
                    new() { CharacterName = "Sentino", Guid = 5UL, Role = SquadRole.DpsRanged }
                }
            };

            var leaderPos = new WoWPoint(leader.X, leader.Y, leader.Z);
            foreach (string name in new[] { "Bedoes", "Belmondo", "Ksionc", "Sentino" })
            {
                int slot = SquadContext.GetMySlotIndex(name, leader);
                Assert.InRange(slot, 0, 3);
                WoWPoint followPos = SquadFormationService.ComputeTravelFollowPosition(leaderPos, leader, slot);
                float worstCaseWaitingStopDistance = followPos.Distance(leaderPos) + 2.5f;
                Assert.True(
                    worstCaseWaitingStopDistance <= settings.ResumeDistance,
                    $"Follower '{name}' (slot {slot}) waiting stop {worstCaseWaitingStopDistance:F2} yd exceeds ResumeDistance ({settings.ResumeDistance:F1} yd)");
            }
        }

        [Fact]
        public void TauntSpellAvailability_RequiresKnownClassTauntBeforeRetargeting()
        {
            Assert.False(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Paladin, _ => false));
            Assert.True(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Paladin, s => s == "Hand of Reckoning"));
            Assert.True(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Paladin, s => s == "Righteous Defense"));
            Assert.False(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Warrior, _ => false));
            Assert.True(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Warrior, s => s == "Taunt"));
            Assert.True(SquadTankBridge.HasAvailableTauntSpell(WoWClass.Druid, s => s == "Growl"));
            Assert.True(SquadTankBridge.HasAvailableTauntSpell(WoWClass.DeathKnight, s => s == "Dark Command"));
        }

        [Fact]
        public void PartyBuffThrottle_BlocksRepeatedBlessingSpamWithinWindowAndExpiresCleanly()
        {
            SquadPartyBuffService.ResetBuffCastThrottles();
            try
            {
                Assert.False(SquadPartyBuffService.IsBuffCastThrottled(42UL, "Blessing of Might", 10_000L));
                SquadPartyBuffService.RecordBuffCast(42UL, "Blessing of Might", 10_000L);
                Assert.True(SquadPartyBuffService.IsBuffCastThrottled(42UL, "Blessing of Might", 10_500L));
                Assert.True(SquadPartyBuffService.IsBuffCastThrottled(42UL, "blessing of might", 13_999L));
                Assert.False(SquadPartyBuffService.IsBuffCastThrottled(42UL, "Blessing of Kings", 10_500L));
                Assert.False(SquadPartyBuffService.IsBuffCastThrottled(99UL, "Blessing of Might", 10_500L));
                Assert.False(SquadPartyBuffService.IsBuffCastThrottled(42UL, "Blessing of Might", 14_001L));
            }
            finally
            {
                SquadPartyBuffService.ResetBuffCastThrottles();
            }
        }

        [Fact]
        public void MeleeFlankPosition_ScalesRadiusWithLargeTargetCombatReach()
        {
            var bossPos = new WoWPoint(100f, 200f, 10f);
            WoWPoint small = SquadFormationService.ComputeMeleeFlankPosition(bossPos, 0f, 2.8f, 0, 1, targetCombatReach: 1.5f);
            WoWPoint large = SquadFormationService.ComputeMeleeFlankPosition(bossPos, 0f, 2.8f, 0, 1, targetCombatReach: 6.0f);

            Assert.InRange(small.Distance2D(bossPos), 2.7f, 2.9f);
            Assert.True(large.Distance2D(bossPos) >= 7.1f);
        }

        [Fact]
        public void DungeonMeleePosition_StaysOnTankSide_AndNeverAdvancesPastTank()
        {
            var mobPos = new WoWPoint(120f, 200f, 10f);
            var tankInMelee = new WoWPoint(116.5f, 200f, 10f);

            for (int slot = 0; slot < 3; slot++)
            {
                WoWPoint pos = SquadFormationService.ComputeDungeonMeleePosition(
                    mobPos, tankInMelee, targetFacingRadians: 0f, meleeFlankRadius: 3.2f, meleeSlotIndex: slot, totalMeleeCount: 3);

                Assert.True(pos.X <= tankInMelee.X + 0.001f, $"Slot {slot} advanced past tank: X={pos.X:F2} > {tankInMelee.X:F2}");
                Assert.False(SquadFormationService.IsPositionAheadOfTank(pos, mobPos, tankInMelee));
                Assert.True(pos.Distance2D(mobPos) <= 4.33f, $"Slot {slot} out of melee reach: {pos.Distance2D(mobPos):F2}");
            }

            var tankApproaching = new WoWPoint(108f, 200f, 10f);
            WoWPoint clamped = SquadFormationService.ComputeDungeonMeleePosition(
                mobPos, tankApproaching, targetFacingRadians: 0f, meleeFlankRadius: 3.2f, meleeSlotIndex: 0, totalMeleeCount: 1);
            Assert.True(clamped.X <= tankApproaching.X + 0.001f);
            Assert.False(SquadFormationService.IsPositionAheadOfTank(clamped, mobPos, tankApproaching));

            WoWPoint maxRadiusSlot0 = SquadFormationService.ComputeDungeonMeleePosition(
                mobPos, tankInMelee, targetFacingRadians: 0f, meleeFlankRadius: 6.0f, meleeSlotIndex: 0, totalMeleeCount: 1);
            Assert.True(maxRadiusSlot0.X <= tankInMelee.X + 0.001f);
            Assert.True(maxRadiusSlot0.Distance2D(mobPos) <= 4.33f, $"Max flank radius pushed slot 0 out of melee reach: {maxRadiusSlot0.Distance2D(mobPos):F2}");

            Assert.True(SquadFormationService.IsPositionAheadOfTank(new WoWPoint(123f, 200f, 10f), mobPos, tankInMelee));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldBlockDungeonMeleeRoutineFromRange(inDungeon: true, isMelee: true, isWithinMeleeRange: false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldBlockDungeonMeleeRoutineFromRange(inDungeon: true, isMelee: true, isWithinMeleeRange: true));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldBlockDungeonMeleeRoutineFromRange(inDungeon: false, isMelee: true, isWithinMeleeRange: false));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ShouldBlockDungeonMeleeRoutineFromRange(inDungeon: true, isMelee: false, isWithinMeleeRange: false));
        }

        [Fact]
        public void DungeonMeleePosition_AtReachBoundary_KeepsDistinctSlotsWithinMeleeReach()
        {
            var mobPos = new WoWPoint(0f, 0f, 0f);
            foreach (float tankX in new[] { 3.699f, 4.2f })
            {
                var tankAtBoundary = new WoWPoint(tankX, 0f, 0f);
                var positions = new System.Collections.Generic.List<WoWPoint>();

                for (int slot = 0; slot < 3; slot++)
                {
                    WoWPoint pos = SquadFormationService.ComputeDungeonMeleePosition(
                        mobPos,
                        tankAtBoundary,
                        targetFacingRadians: 0f,
                        meleeFlankRadius: 3.2f,
                        meleeSlotIndex: slot,
                        totalMeleeCount: 3,
                        targetCombatReach: 1.5f);

                    Assert.True(pos.X >= tankAtBoundary.X - 0.001f, $"Slot {slot} advanced past tank ({tankX}): X={pos.X:F2}");
                    Assert.False(SquadFormationService.IsPositionAheadOfTank(pos, mobPos, tankAtBoundary), $"Slot {slot} ahead of tank ({tankX}): X={pos.X:F2}");
                    Assert.True(pos.Distance2D(mobPos) <= 4.331f, $"Slot {slot} out of melee reach ({tankX}): {pos.Distance2D(mobPos):F2}");
                    positions.Add(pos);
                }

                Assert.True(positions[0].Distance2D(positions[1]) > 0.5f, $"Slots 0 and 1 collapsed at tankX={tankX}.");
                Assert.True(positions[0].Distance2D(positions[2]) > 0.5f, $"Slots 0 and 2 collapsed at tankX={tankX}.");
                Assert.True(positions[1].Distance2D(positions[2]) > 0.5f, $"Slots 1 and 2 collapsed at tankX={tankX}.");
            }

            var tankFourMelee = new WoWPoint(4.0f, 0f, 0f);
            var fourPositions = new System.Collections.Generic.List<WoWPoint>();
            for (int slot = 0; slot < 4; slot++)
            {
                WoWPoint pos = SquadFormationService.ComputeDungeonMeleePosition(
                    mobPos,
                    tankFourMelee,
                    targetFacingRadians: 0f,
                    meleeFlankRadius: 3.4f,
                    meleeSlotIndex: slot,
                    totalMeleeCount: 4,
                    targetCombatReach: 1.5f);

                Assert.False(SquadFormationService.IsPositionAheadOfTank(pos, mobPos, tankFourMelee));
                Assert.True(pos.Distance2D(mobPos) <= 4.331f);
                fourPositions.Add(pos);
            }

            for (int i = 0; i < fourPositions.Count; i++)
            {
                for (int j = i + 1; j < fourPositions.Count; j++)
                {
                    Assert.True(fourPositions[i].Distance2D(fourPositions[j]) > 0.4f, $"4-melee slots {i} and {j} overlapped.");
                }
            }
        }

        [Fact]
        public void DungeonMeleeMoveTarget_HandlesVerticalDistance_AndMeleeToleranceGap()
        {
            const float meleeGateReach = 8.33f;
            var mobPos = new WoWPoint(0f, 0f, 0f);
            var meleePos = new WoWPoint(4.2f, 0f, 0f);
            var tankInMelee = new WoWPoint(4.2f, 0f, 0f);
            var tankWithinGate = new WoWPoint(6.0f, 0f, 0f);
            var tankApproaching = new WoWPoint(12.0f, 0f, 0f);

            var differentFloor = new WoWPoint(4.2f, 0f, 8.0f);
            bool movedVertical = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ResolveDungeonMeleeMoveTarget(
                differentFloor, meleePos, true, mobPos, true, true, tankInMelee, meleeGateReach,
                out WoWPoint verticalTarget, out bool verticalIsTankSide);
            Assert.True(movedVertical);
            Assert.True(verticalIsTankSide);
            Assert.Equal(meleePos, verticalTarget);

            var withinNavToleranceOutsideMelee = new WoWPoint(4.7f, 0f, 0f);
            bool steppedCloser = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ResolveDungeonMeleeMoveTarget(
                withinNavToleranceOutsideMelee, meleePos, true, mobPos, true, true, tankInMelee, meleeGateReach,
                out WoWPoint closeTarget, out bool closeIsTankSide);
            Assert.True(steppedCloser);
            Assert.False(closeIsTankSide);
            Assert.Equal(mobPos, closeTarget);

            bool tightenedToMeleePos = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ResolveDungeonMeleeMoveTarget(
                withinNavToleranceOutsideMelee, meleePos, true, mobPos, false, true, tankInMelee, meleeGateReach,
                out WoWPoint tightTarget, out bool tightIsTankSide);
            Assert.True(tightenedToMeleePos);
            Assert.True(tightIsTankSide);
            Assert.Equal(meleePos, tightTarget);

            var atTankWithinGate = new WoWPoint(9.0f, 0f, 0f);
            bool alignedFallback = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ResolveDungeonMeleeMoveTarget(
                atTankWithinGate, tankWithinGate, false, mobPos, true, true, tankWithinGate, meleeGateReach,
                out WoWPoint alignedTarget, out bool alignedIsTankSide);
            Assert.True(alignedFallback);
            Assert.False(alignedIsTankSide);
            Assert.Equal(mobPos, alignedTarget);

            var atApproachingTank = new WoWPoint(12.1f, 0f, 0f);
            bool heldBehindTank = CopilotBuddy.Bots.PartyGrind.PartyGrindBot.ResolveDungeonMeleeMoveTarget(
                atApproachingTank, tankApproaching, true, mobPos, true, true, tankApproaching, meleeGateReach,
                out _, out _);
            Assert.False(heldBehindTank);

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.HasUnreachableDungeonMeleeApproach(
                safeMeleePos: false, safeTargetPos: false, Styx.Logic.Pathing.MoveResult.Moved));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.HasUnreachableDungeonMeleeApproach(
                safeMeleePos: true, safeTargetPos: true, Styx.Logic.Pathing.MoveResult.PathGenerationFailed));
            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.HasUnreachableDungeonMeleeApproach(
                safeMeleePos: true, safeTargetPos: true, Styx.Logic.Pathing.MoveResult.Failed));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.HasUnreachableDungeonMeleeApproach(
                safeMeleePos: true, safeTargetPos: false, Styx.Logic.Pathing.MoveResult.Moved));

            Assert.True(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsFailedDungeonMeleePrimaryActive(100UL, 5000L, 100UL, 3000L));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsFailedDungeonMeleePrimaryActive(100UL, 5000L, 100UL, 5000L));
            Assert.False(CopilotBuddy.Bots.PartyGrind.PartyGrindBot.IsFailedDungeonMeleePrimaryActive(100UL, 5000L, 200UL, 3000L));
        }
    }
}
