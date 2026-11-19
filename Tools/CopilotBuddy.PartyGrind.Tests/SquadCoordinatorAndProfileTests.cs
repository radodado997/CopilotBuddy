using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Profiles;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadCoordinatorAndProfileTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly object? _previousOuter = typeof(ProfileManager).GetField("_currentOuterProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null);
        private readonly object? _previousCurrent = typeof(ProfileManager).GetField("_currentProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null);
        private readonly string _previousXml = ProfileManager.XmlLocation;

        public SquadCoordinatorAndProfileTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PartyGrindTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SquadContext.Reset();
            ProfileManager.LevelOverrideSelector = null;
        }

        public void Dispose()
        {
            typeof(ProfileManager).GetField("_currentOuterProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, _previousOuter);
            typeof(ProfileManager).GetField("_currentProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, _previousCurrent);
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.XmlLocation))!.SetValue(null, _previousXml);
            ProfileManager.LevelOverrideSelector = null;
            SquadContext.Reset();
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
        public void TownBlock_ClearedTelemetryResumesWithoutCoordinatorReset()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var local = MakeLeaderState(10);
            local.NeedsSell = true;
            local.TownBlockedReason = "No eligible vendor found.";
            var profile = new SquadProfileSnapshot();
            Assert.Equal(SquadPhase.Paused, coordinator.EvaluateTick(settings, local,
                Array.Empty<SquadConnectedMemberSnapshot>(), profile, 0, 0, 0, 0, 1000).Phase);
            local.TownBlockedReason = string.Empty;
            Assert.Equal(SquadPhase.TownRun, coordinator.EvaluateTick(settings, local,
                Array.Empty<SquadConnectedMemberSnapshot>(), profile, 0, 0, 0, 0, 2000).Phase);
        }

        [Fact]
        public void UntrainedLivingHealer_DoesNotDelayCorpseRecovery()
        {
            var coord = new SquadCoordinatorService();
            var state = coord.EvaluateTick(CreateDefaultSettings(), MakeLeaderState(10), new[]
            {
                MakeConnectedClient("Healer", 0, SquadRole.Healer, WoWClass.Priest, 10, 1000),
                MakeConnectedClient("Dead", 1, SquadRole.DpsMelee, WoWClass.Rogue, 10, 1000, isDead: true)
            }, new SquadProfileSnapshot(), 0, 0, 0, 0, 1000);
            Assert.Equal(SquadPhase.CorpseRun, state.Phase);
        }

        [Fact]
        public void HandshakeOnlyMember_GatesLevelWithoutRestOrDistance_ThenUsesTelemetry()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var member = new SquadConnectedMemberSnapshot
            {
                CharacterName = "Pending", IsConnected = true,
                Handshake = new SquadHandshake { CharacterName = "Pending", Level = 2 }
            };
            var leader = MakeLeaderState(6);
            leader.CanMount = true;
            var state = coordinator.EvaluateTick(settings, leader, new[] { member },
                new SquadProfileSnapshot(), 0, 0, 0, 0, 1000);
            Assert.Equal(2, state.EffectiveSquadMinLevel);
            Assert.Equal(SquadPhase.Grinding, state.Phase);
            Assert.False(coordinator.IsWaitingForFollower);
            Assert.False(state.Members.Single(m => m.CharacterName == "Pending").HasTelemetry);
            Assert.False(SquadTankBridge.CanInitiatePull(state, 30));
            Assert.False(SquadFormationService.CanSquadMount(SquadMountPolicy.SyncWithSlowestMember, true, state));
            var real = MakeConnectedClient("Pending", 0, SquadRole.Healer, WoWClass.Priest, 2, 2000, healthPct: 10);
            real.LatestState!.CanMount = true;
            state = coordinator.EvaluateTick(settings, leader, new[] { real },
                new SquadProfileSnapshot(), 0, 0, 0, 0, 2000);
            Assert.Equal(SquadPhase.Resting, state.Phase);
            Assert.True(SquadFormationService.CanSquadMount(SquadMountPolicy.SyncWithSlowestMember, true, state));
            coordinator.IsPausedManually = true;
            coordinator.ManualPauseReason = "test";
            coordinator.Reset();
            Assert.False(coordinator.IsPausedManually);
            Assert.Empty(coordinator.ManualPauseReason);
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

        private static SquadConnectedMemberSnapshot MakeConnectedClient(
            string name,
            int slot,
            SquadRole role,
            WoWClass wowClass,
            int level,
            long nowMs,
            float x = 100f,
            float y = 100f,
            float healthPct = 100f,
            float powerPct = 100f,
            bool inCombat = false,
            bool isDead = false,
            bool townComplete = false,
            bool needsTrain = false,
            bool canResurrect = false)
        {
            return new SquadConnectedMemberSnapshot
            {
                CharacterName = name,
                SlotIndex = slot,
                IsConnected = true,
                LastReceivedUtcMs = nowMs,
                Handshake = new SquadHandshake
                {
                    CharacterName = name,
                    Guid = (ulong)(200 + slot),
                    Class = wowClass,
                    Role = role,
                    Level = level
                },
                LatestState = new SquadMemberState
                {
                    CanResurrect = canResurrect,
                    CharacterName = name,
                    Guid = (ulong)(200 + slot),
                    Class = wowClass,
                    Role = role,
                    Level = level,
                    MapId = 0,
                    X = x,
                    Y = y,
                    Z = 0f,
                    HealthPercent = healthPct,
                    PowerPercent = powerPct,
                    PowerType = wowClass == WoWClass.Warrior || wowClass == WoWClass.Rogue ? WoWPowerType.Rage : WoWPowerType.Mana,
                    IsInCombat = inCombat,
                    IsDead = isDead,
                    NeedsTrain = needsTrain,
                    TownTasksComplete = townComplete
                }
            };
        }

        private static SquadMemberState MakeLeaderState(
            int level,
            float x = 100f,
            float y = 100f,
            float healthPct = 100f,
            bool inCombat = false,
            bool townComplete = false)
        {
            return new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 100,
                Class = WoWClass.Warrior,
                Role = SquadRole.TankLeader,
                Level = level,
                MapId = 0,
                X = x,
                Y = y,
                Z = 0f,
                HealthPercent = healthPct,
                PowerPercent = 0f,
                PowerType = WoWPowerType.Rage,
                IsInCombat = inCombat,
                TownTasksComplete = townComplete
            };
        }

        [Fact]
        public void EffectiveSquadMinLevel_GatedByLowestMemberAndDisconnectGrace_AndSwitchesProfileSubProfile()
        {
            string xmlPath = Path.Combine(_tempDir, "MultiStageGrind.xml");
            File.WriteAllText(xmlPath, @"<HBProfile>
  <Name>MultiStage 1-15</Name>
  <SubProfile>
    <Name>Starter L1-6</Name>
    <MinLevel>1</MinLevel>
    <MaxLevel>6</MaxLevel>
    <GrindArea>
      <Name>Spot1</Name>
      <TargetMinLevel>1</TargetMinLevel>
      <TargetMaxLevel>6</TargetMaxLevel>
      <Hotspots><Hotspot X=""100"" Y=""100"" Z=""0"" /></Hotspots>
    </GrindArea>
  </SubProfile>
  <SubProfile>
    <Name>Mid L6-10</Name>
    <MinLevel>6</MinLevel>
    <MaxLevel>10</MaxLevel>
    <GrindArea>
      <Name>Spot2</Name>
      <TargetMinLevel>6</TargetMinLevel>
      <TargetMaxLevel>10</TargetMaxLevel>
      <Hotspots><Hotspot X=""500"" Y=""500"" Z=""0"" /></Hotspots>
    </GrindArea>
  </SubProfile>
</HBProfile>");

            ProfileManager.LevelOverrideSelector = SquadContext.GetEffectiveProfileLevel;
            SquadContext.EffectiveLevelOverride = 5;
            ProfileManager.LoadNew(xmlPath, rememberMe: false);

            Assert.NotNull(ProfileManager.CurrentProfile);
            Assert.Equal("Starter L1-6", ProfileManager.CurrentProfile!.Name);

            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            var profileSnap = new SquadProfileSnapshot
            {
                ProfileXmlPath = xmlPath,
                SubProfileName = "Starter L1-6",
                SubProfileMinLevel = 1,
                SubProfileMaxLevel = 6,
                MapId = 0,
                HotspotX = 100f,
                HotspotY = 100f,
                HotspotZ = 0f
            };

            long t = 1_000_000L;

            // Player A levels to 6, B & C still level 5 -> stays 5 (Starter L1-6)
            var state1 = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 5, t)
                },
                profileSnap, 0, 0, 0, 0f, t);

            Assert.Equal(5, state1.EffectiveSquadMinLevel);
            SquadContext.UpdateLeaderState(state1);
            ProfileManager.RefreshProfileForLevel();
            Assert.Equal("Starter L1-6", ProfileManager.CurrentProfile!.Name);

            // Player B levels to 6, Player C still level 5 -> still stays 5
            t += 500;
            var state2 = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 6, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 5, t)
                },
                profileSnap, 0, 0, 0, 0f, t);

            Assert.Equal(5, state2.EffectiveSquadMinLevel);

            // Player C disconnects while level 5 -> DisconnectGraceSeconds=120 preserves EffectiveSquadMinLevel=5!
            t += 10_000; // 10s later (< 120s grace)
            var stateDcWithinGrace = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 6, t)
                },
                profileSnap, 0, 0, 0, 0f, t);
            Assert.Equal(5, stateDcWithinGrace.EffectiveSquadMinLevel);

            // Player C reconnects and hits level 6 -> advances to 6 and switches sub-profile immediately!
            t += 1_000;
            var state3 = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 6, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 6, t)
                },
                profileSnap, 0, 0, 0, 0f, t, resolveProfile: level =>
                {
                    ProfileManager.LevelOverrideSelector = () => level;
                    ProfileManager.RefreshProfileForLevel();
                    var profile = ProfileManager.CurrentProfile!;
                    var hotspot = profile.GrindArea!.CurrentHotSpot.Position;
                    return new SquadProfileSnapshot
                    {
                        ProfileXmlPath = xmlPath, SubProfileName = profile.Name,
                        SubProfileMinLevel = profile.MinLevel, SubProfileMaxLevel = profile.MaxLevel,
                        MapId = 0, HotspotX = hotspot.X, HotspotY = hotspot.Y, HotspotZ = hotspot.Z
                    };
                });
            Assert.Equal(6, state3.EffectiveSquadMinLevel);
            Assert.Equal("Mid L6-10", state3.ActiveSubProfileName);
            Assert.Equal(6, state3.ActiveSubProfileMinLevel);
            Assert.Equal(10, state3.ActiveSubProfileMaxLevel);
            Assert.Equal(500f, state3.ActiveHotspotX);
            Assert.Equal(500f, state3.ActiveHotspotY);
            Assert.Equal(SquadPhase.MigratingZone, state3.Phase);
            var newSnap = new SquadProfileSnapshot
            {
                ProfileXmlPath = xmlPath, SubProfileName = state3.ActiveSubProfileName,
                SubProfileMinLevel = state3.ActiveSubProfileMinLevel, SubProfileMaxLevel = state3.ActiveSubProfileMaxLevel,
                MapId = 0, HotspotX = state3.ActiveHotspotX, HotspotY = state3.ActiveHotspotY, HotspotZ = state3.ActiveHotspotZ
            };
            t += 200;
            var migrating = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6, x: 100f, y: 100f),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 6, t, x: 105f, y: 105f),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 6, t, x: 110f, y: 110f)
                },
                newSnap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.MigratingZone, migrating.Phase);

            // Once all arrive within SoftWaitDistance of (500,500), MigratingZone finishes -> Grinding
            t += 1000;
            var arrived = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 6, x: 500f, y: 500f),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 6, t, x: 495f, y: 505f),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 6, t, x: 505f, y: 495f)
                },
                newSnap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, arrived.Phase);
        }

        [Fact]
        public void PhaseMachine_EnforcesRestAndDistanceHysteresis_AndTownRunCompletionGate()
        {
            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            var snap = new SquadProfileSnapshot
            {
                SubProfileName = "Zone",
                SubProfileMinLevel = 1,
                SubProfileMaxLevel = 10,
                MapId = 0,
                HotspotX = 100f,
                HotspotY = 100f,
                TownRallyMapId = 0,
                TownRallyX = 300f,
                TownRallyY = 300f
            };
            long t = 50_000L;

            // 1. Healer drops to 34% mana -> Phase == Resting & pulls blocked
            var resting1 = coord.EvaluateTick(
                settings,
                MakeLeaderState(5),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, powerPct: 34f) },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Resting, resting1.Phase);
            Assert.False(SquadTankBridge.CanInitiatePull(resting1, settings.MaxPullGatherDistance));

            // Hysteresis: Healer recovers to 60% mana (< 80% resume) -> still Resting!
            t += 1000;
            var resting2 = coord.EvaluateTick(
                settings,
                MakeLeaderState(5),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, powerPct: 60f) },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Resting, resting2.Phase);

            // Healer hits 82% mana (>= 80%) -> Grinding resumes and pulls allowed
            t += 1000;
            var recovered = coord.EvaluateTick(
                settings,
                MakeLeaderState(5),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, powerPct: 82f) },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, recovered.Phase);
            Assert.True(SquadTankBridge.CanInitiatePull(recovered, settings.MaxPullGatherDistance));

            // 2. Distance hysteresis: follower lags 40 yd (> 35), then 25 yd (still waiting), then 18 yd (<= 20)
            t += 1000;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 100f, y: 100f),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 140f, y: 100f) },
                snap, 0, 0, 0, 0f, t);
            Assert.True(coord.IsWaitingForFollower);

            t += 500;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 100f, y: 100f),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 125f, y: 100f) },
                snap, 0, 0, 0, 0f, t);
            Assert.True(coord.IsWaitingForFollower);

            t += 500;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 100f, y: 100f),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 118f, y: 100f) },
                snap, 0, 0, 0, 0f, t);
            Assert.False(coord.IsWaitingForFollower);

            // Hard backtrack at 70 yd (> 65)
            t += 500;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 100f, y: 100f),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 170f, y: 100f) },
                snap, 0, 0, 0, 0f, t);
            Assert.True(coord.IsHardBacktracking);

            // Reset distance
            t += 500;
            coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 100f, y: 100f),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 102f, y: 100f) },
                snap, 0, 0, 0, 0f, t);
            Assert.False(coord.IsHardBacktracking);

            // 3. TownRun completion gate: stays in TownRun until ALL members finish trainers & reach rally (300, 300)
            t += 1000;
            var town1 = coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 300f, y: 300f, townComplete: true),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 380f, y: 310f, townComplete: false, needsTrain: true),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 5, t, x: 302f, y: 302f, townComplete: true)
                },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.TownRun, town1.Phase);
            Assert.False(town1.AllTownTasksComplete);

            // HealB finishes trainer and walks back within <= 20 yd of rally point (300,300)
            t += 1000;
            var townDone = coord.EvaluateTick(
                settings,
                MakeLeaderState(5, x: 300f, y: 300f, townComplete: true),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 5, t, x: 305f, y: 295f, townComplete: true, needsTrain: false),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 5, t, x: 302f, y: 302f, townComplete: true)
                },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, townDone.Phase);
            Assert.True(townDone.AllTownTasksComplete);
        }

        [Fact]
        public void DeathAndTimeout_TransitionsFromWaitingResurrectionToCorpseRun()
        {
            var settings = CreateDefaultSettings();
            settings.WaitForPartyRessSeconds = 45;
            var coord = new SquadCoordinatorService();
            var snap = new SquadProfileSnapshot { MapId = 0 };
            long t = 100_000L;

            // DpsC dies, living Priest Healer present -> WaitingResurrection (do not release immediately)
            var waitRes = coord.EvaluateTick(
                settings,
                MakeLeaderState(10),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, t, isDead: false, canResurrect: true),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 10, t, isDead: true)
                },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.WaitingResurrection, waitRes.Phase);
            Assert.False(waitRes.ReleaseCorpseOrder);

            // After 46s timeout (> 45s), Coordinator commands CorpseRun
            t += 46_000;
            var timedOut = coord.EvaluateTick(
                settings,
                MakeLeaderState(10),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, t, isDead: false, canResurrect: true),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 10, t, isDead: true)
                },
                snap, 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.CorpseRun, timedOut.Phase);
            Assert.True(timedOut.ReleaseCorpseOrder);
        }
    }
}
