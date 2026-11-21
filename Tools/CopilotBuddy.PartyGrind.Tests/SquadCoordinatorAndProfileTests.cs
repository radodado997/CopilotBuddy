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
            ProfileManager.KeepProfileAboveLevelRange = false;
        }

        public void Dispose()
        {
            typeof(ProfileManager).GetField("_currentOuterProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, _previousOuter);
            typeof(ProfileManager).GetField("_currentProfile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, _previousCurrent);
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.XmlLocation))!.SetValue(null, _previousXml);
            ProfileManager.LevelOverrideSelector = null;
            ProfileManager.KeepProfileAboveLevelRange = false;
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

        [Fact]
        public void HealthstoneAssignment_SkipsUnavailableOrTooHighLevelSupply()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var receiver = MakeLeaderState(20);
            receiver.WantsHealthstone = true;
            receiver.FreeBagSlots = 5;
            var warlock = MakeConnectedClient("Warlock", 0, SquadRole.DpsRanged, WoWClass.Warlock, 34, 1000);
            var provider = warlock.LatestState!;

            SquadLeaderState Tick(long now) => coordinator.EvaluateTick(settings, receiver, new[] { warlock },
                new SquadProfileSnapshot(), 0, 0, 0, 0, now);

            Assert.Equal(SquadPhase.Grinding, Tick(1000).Phase);
            provider.CanProvideHealthstone = true;
            Assert.Empty(Tick(1100).ActiveProvisionerName);
            provider.HealthstoneMinReceiverLevel = 22;
            Assert.Empty(Tick(1200).ActiveProvisionerName);
            receiver.Level = 22;
            Assert.Equal("Warlock", Tick(1300).ActiveProvisionerName);
        }

        [Fact]
        public void LostHealthstoneSupply_ReleasesAssignmentWithoutReceiverCooldown()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            settings.TradeOfferTimeoutSeconds = 7;
            settings.ProvisionCooldownSeconds = 30;
            var receiver = MakeLeaderState(22);
            receiver.WantsHealthstone = true;
            receiver.FreeBagSlots = 5;
            var warlock = MakeConnectedClient("Warlock", 0, SquadRole.DpsRanged, WoWClass.Warlock, 34, 1000);
            var provider = warlock.LatestState!;
            provider.CanProvideHealthstone = true;
            provider.HealthstoneMinReceiverLevel = 10;

            SquadLeaderState Tick(long now) => coordinator.EvaluateTick(settings, receiver, new[] { warlock },
                new SquadProfileSnapshot(), 0, 0, 0, 0, now);

            Assert.Equal(receiver.CharacterName, Tick(1000).ActiveTradeTargetName);
            provider.CanProvideHealthstone = false;
            provider.HealthstoneMinReceiverLevel = null;
            var released = Tick(2000);
            Assert.Equal(SquadPhase.Grinding, released.Phase);
            Assert.Empty(released.ActiveTradeTargetName);

            provider.CanProvideHealthstone = true;
            provider.HealthstoneMinReceiverLevel = 10;
            Assert.Equal(receiver.CharacterName, Tick(3000).ActiveTradeTargetName);

            provider.CanProvideHealthstone = false;
            provider.HealthstoneMinReceiverLevel = null;
            Assert.Empty(Tick(14000).ActiveTradeTargetName);
            provider.CanProvideHealthstone = true;
            provider.HealthstoneMinReceiverLevel = 10;
            Assert.Equal(receiver.CharacterName, Tick(15000).ActiveTradeTargetName);
        }

        [Fact]
        public void LastShardSpentOnRitual_KeepsAssignmentPastTradeTimeoutUntilRitualWaitEnds()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            settings.TradeOfferTimeoutSeconds = 4;
            var receiver = MakeLeaderState(70);
            receiver.WantsHealthstone = true;
            receiver.FreeBagSlots = 5;
            var warlock = MakeConnectedClient("Warlock", 0, SquadRole.DpsRanged, WoWClass.Warlock, 70, 1000);
            var provider = warlock.LatestState!;
            provider.CanProvideHealthstone = true;
            provider.HealthstoneMinReceiverLevel = 10;

            SquadLeaderState Tick(long now) => coordinator.EvaluateTick(settings, receiver, new[] { warlock },
                new SquadProfileSnapshot(), 0, 0, 0, 0, now);

            Assert.Equal(receiver.CharacterName, Tick(1000).ActiveTradeTargetName);
            provider.CanProvideHealthstone = false;
            provider.HealthstoneMinReceiverLevel = null;
            provider.IsHealthstoneRitualPending = true;
            Assert.Equal(receiver.CharacterName, Tick(7000).ActiveTradeTargetName);
            Assert.Equal(receiver.CharacterName, Tick(9000).ActiveTradeTargetName);
            provider.IsHealthstoneRitualPending = false;
            Assert.Empty(Tick(13000).ActiveTradeTargetName);

            provider.CanProvideHealthstone = true;
            provider.HealthstoneMinReceiverLevel = 10;
            Assert.Equal(receiver.CharacterName, Tick(14000).ActiveTradeTargetName);
        }

        [Fact]
        public void LogoutAndReconnect_RetainConfirmedLevelAndXpUntilGraceExpires()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var leader = MakeLeaderState(15);
            var profile = new SquadProfileSnapshot();
            var levels = new List<int>();
            SquadLeaderState Tick(long time, params SquadConnectedMemberSnapshot[] members) =>
                coordinator.EvaluateTick(settings, leader, members, profile, 0, 0, 0, 0, time,
                    resolveProfile: level => { levels.Add(level); return profile; });
            var member = MakeConnectedClient("Healer", 0, SquadRole.Healer, WoWClass.Priest, 13, 1000);
            member.LatestState!.CurrentXp = 45;
            member.LatestState.NextLevelXp = 100;
            Tick(1000, member);
            member.LatestState.Level = 0;
            member.LatestState.CurrentXp = 0;
            var degraded = Tick(2000, member);
            Assert.Equal(45f, degraded.LowestMemberXpPercent);
            Assert.Equal(13, degraded.Members.Single(m => m.CharacterName == "Healer").Level);
            Tick(3000);
            var reconnect = new SquadConnectedMemberSnapshot
            {
                CharacterName = "Healer", IsConnected = true,
                Handshake = new SquadHandshake { CharacterName = "Healer", Guid = member.Handshake.Guid, Level = 1 }
            };
            Assert.Equal(45f, Tick(4000, reconnect).LowestMemberXpPercent);
            member.LatestState.Level = 14;
            Tick(5000, member);
            Tick(125000);
            Tick(125001);
            Assert.Equal(new[] { 13, 13, 13, 13, 14, 14, 15 }, levels);
        }

        [Fact]
        public void SameLevelXpRegression_IsIgnoredUntilLevelUp()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var leader = MakeLeaderState(15);
            var member = MakeConnectedClient("Healer", 0, SquadRole.Healer, WoWClass.Priest, 13, 1000);
            SquadLeaderState Tick(long time) => coordinator.EvaluateTick(settings, leader, new[] { member },
                new SquadProfileSnapshot(), 0, 0, 0, 0, time);
            var sample = member.LatestState!;
            sample.NextLevelXp = 100;
            sample.CurrentXp = 45;
            Tick(1000);
            sample.CurrentXp = 0;
            var regressed = Tick(2000);
            Assert.Equal(45f, regressed.LowestMemberXpPercent);
            Assert.Equal(45f, regressed.Members.Single(m => m.CharacterName == "Healer").XpPercent);
            sample.Level = 14;
            Assert.Equal(0f, Tick(3000).LowestMemberXpPercent);
        }

        [Fact]
        public void AfterGraceExpires_ReconnectUsesConfirmedProgressOnlyForSameIdentity()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var leader = MakeLeaderState(15);
            SquadLeaderState Tick(long time, params SquadConnectedMemberSnapshot[] members) =>
                coordinator.EvaluateTick(settings, leader, members, new SquadProfileSnapshot(), 0, 0, 0, 0, time);
            var member = MakeConnectedClient("Healer", 0, SquadRole.Healer, WoWClass.Priest, 13, 1000);
            member.LatestState!.CurrentXp = 45;
            member.LatestState.NextLevelXp = 100;
            Tick(1000, member);
            var expired = Tick(121001);
            Assert.Equal(15, expired.EffectiveSquadMinLevel);
            Assert.DoesNotContain(expired.Members, m => m.CharacterName == "Healer");
            var reconnect = new SquadConnectedMemberSnapshot
            {
                CharacterName = "Healer", IsConnected = true,
                Handshake = new SquadHandshake { CharacterName = "Healer", Guid = member.Handshake.Guid, Level = 1 }
            };
            var returned = Tick(122000, reconnect);
            Assert.Equal(13, returned.EffectiveSquadMinLevel);
            Assert.Equal(45f, returned.LowestMemberXpPercent);
            Assert.Equal(45f, returned.Members.Single(m => m.CharacterName == "Healer").XpPercent);
            Tick(242001);
            reconnect.Handshake.Guid++;
            var replacement = Tick(243000, reconnect);
            Assert.Equal(1, replacement.EffectiveSquadMinLevel);
            Assert.Equal(0f, replacement.LowestMemberXpPercent);
        }

        [Fact]
        public void UnknownLevels_DoNotSelectProfile_AndRealLevelOneIsAccepted()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = CreateDefaultSettings();
            var leader = MakeLeaderState(0);
            var member = MakeConnectedClient("New", 0, SquadRole.Healer, WoWClass.Priest, 0, 1000);
            var profile = new SquadProfileSnapshot();
            var levels = new List<int>();
            SquadLeaderState Tick() => coordinator.EvaluateTick(settings, leader, new[] { member },
                profile, 0, 0, 0, 0, 1000, resolveProfile: level => { levels.Add(level); return profile; });
            Assert.Equal(0, Tick().EffectiveSquadMinLevel);
            Assert.Empty(levels);
            leader.Level = 15;
            Assert.Equal(15, Tick().EffectiveSquadMinLevel);
            leader.Level = 1;
            Assert.Equal(15, Tick().EffectiveSquadMinLevel);
            member.LatestState!.Level = 1;
            Assert.Equal("New", Tick().LowestMemberName);
            Assert.Equal(new[] { 15, 15, 1 }, levels);
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

        [Fact]
        public void MemberAboveProfileMaxLevel_KeepsProfileLoadedAndSquadGrinding()
        {
            string xmlPath = Path.Combine(_tempDir, "CappedGrind.xml");
            File.WriteAllText(xmlPath, @"<HBProfile>
  <Name>Capped 1-10</Name>
  <SubProfile>
    <Name>Only L1-10</Name>
    <MinLevel>1</MinLevel>
    <MaxLevel>10</MaxLevel>
    <GrindArea>
      <Name>Spot</Name>
      <TargetMinLevel>1</TargetMinLevel>
      <TargetMaxLevel>10</TargetMaxLevel>
      <Hotspots><Hotspot X=""100"" Y=""100"" Z=""0"" /></Hotspots>
    </GrindArea>
  </SubProfile>
</HBProfile>");

            ProfileManager.KeepProfileAboveLevelRange = true;
            ProfileManager.LevelOverrideSelector = SquadContext.GetEffectiveProfileLevel;
            SquadContext.EffectiveLevelOverride = 12;
            ProfileManager.LoadNew(xmlPath, rememberMe: false);

            Assert.Equal(10, ProfileManager.TopProfileMaxLevel);
            Assert.NotNull(ProfileManager.CurrentProfile);
            Assert.Equal("Only L1-10", ProfileManager.CurrentProfile!.Name);

            var settings = CreateDefaultSettings();
            var coord = new SquadCoordinatorService();
            long t = 10_000L;
            var snap = new SquadProfileSnapshot
            {
                ProfileXmlPath = xmlPath,
                ProfileXmlHash = "hash",
                SubProfileName = ProfileManager.CurrentProfile.Name,
                SubProfileMinLevel = ProfileManager.CurrentProfile.MinLevel,
                SubProfileMaxLevel = ProfileManager.CurrentProfile.MaxLevel,
                ProfileMaxLevel = ProfileManager.TopProfileMaxLevel,
                MapId = 0,
                HotspotX = 100f,
                HotspotY = 100f
            };

            // The leader outleveled the profile but the lowest member is still 9 -> grind on.
            var grinding = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 9, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 9, t)
                },
                snap, 0, 0, 0, 0f, t);

            Assert.Equal(9, grinding.EffectiveSquadMinLevel);
            Assert.Equal(10, grinding.ProfileMaxLevel);
            Assert.False(grinding.LevelCapReached);
            Assert.Equal(SquadPhase.Grinding, grinding.Phase);
            Assert.True(SquadTankBridge.CanInitiatePull(grinding, settings.MaxPullGatherDistance));

            // The lowest member reaches the profile maximum itself -> still grinding.
            t += 500;
            var atMax = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 10, t)
                },
                snap, 0, 0, 0, 0f, t);

            Assert.Equal(10, atMax.EffectiveSquadMinLevel);
            Assert.False(atMax.LevelCapReached);
            Assert.Equal(SquadPhase.Grinding, atMax.Phase);

            // The last member reaches level 11 (> 10) -> the squad has finished the profile.
            t += 500;
            var capped = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 11, t),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 11, t)
                },
                snap, 0, 0, 0, 0f, t);

            Assert.Equal(11, capped.EffectiveSquadMinLevel);
            Assert.True(capped.LevelCapReached);
        }

        [Fact]
        public void LevelCap_WaitsForEveryExpectedMemberBeforeEndingTheSquad()
        {
            var settings = CreateDefaultSettings();
            settings.ExpectedMemberNamesCsv = "TankA,HealB,DpsC";
            var coord = new SquadCoordinatorService();
            long t = 20_000L;
            var snap = new SquadProfileSnapshot
            {
                ProfileXmlPath = "squad.xml",
                SubProfileName = "L1-10",
                SubProfileMinLevel = 1,
                SubProfileMaxLevel = 10,
                ProfileMaxLevel = 10,
                MapId = 0,
                HotspotX = 100f,
                HotspotY = 100f
            };

            // The two reporting members are above the maximum, but DpsC never reported a level.
            var incomplete = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[] { MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 11, t) },
                snap, 0, 0, 0, 0f, t);

            Assert.False(incomplete.LevelCapReached);
            Assert.Equal(SquadPhase.Grinding, incomplete.Phase);

            // DpsC is still below the maximum, so the squad keeps grinding.
            var lowestBelow = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 11, t + 500),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 10, t + 500)
                },
                snap, 0, 0, 0, 0f, t + 500);

            Assert.False(lowestBelow.LevelCapReached);
            Assert.Equal(10, lowestBelow.EffectiveSquadMinLevel);

            // Every expected member reported and all are above the maximum -> cap.
            var capped = coord.EvaluateTick(
                settings,
                MakeLeaderState(level: 12),
                new[]
                {
                    MakeConnectedClient("HealB", 0, SquadRole.Healer, WoWClass.Priest, 11, t + 1000),
                    MakeConnectedClient("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 11, t + 1000)
                },
                snap, 0, 0, 0, 0f, t + 1000);

            Assert.True(capped.LevelCapReached);
        }

        [Fact]
        public void FlatProfileAboveItsRange_IsUnloadedNormallyAndKeptInSquadMode()
        {
            string xmlPath = Path.Combine(_tempDir, "VanillaGrind.xml");
            File.WriteAllText(xmlPath, @"<HBProfile>
  <Name>Vanilla 1-10</Name>
  <MinLevel>1</MinLevel>
  <MaxLevel>10</MaxLevel>
  <GrindArea>
    <Name>Spot</Name>
    <TargetMinLevel>1</TargetMinLevel>
    <TargetMaxLevel>10</TargetMaxLevel>
    <Hotspots><Hotspot X=""100"" Y=""100"" Z=""0"" /></Hotspots>
  </GrindArea>
</HBProfile>");

            ProfileManager.KeepProfileAboveLevelRange = false;
            ProfileManager.LevelOverrideSelector = () => 11;
            ProfileManager.LoadNew(xmlPath, rememberMe: false);

            Assert.Null(ProfileManager.CurrentProfile);

            // The same out-of-range level still resolves in squad mode, so the member
            // that outleveled the profile keeps its grind area and does not stop.
            ProfileManager.KeepProfileAboveLevelRange = true;
            ProfileManager.LoadNew(xmlPath, rememberMe: false);

            Assert.NotNull(ProfileManager.CurrentProfile);
            Assert.Equal("Vanilla 1-10", ProfileManager.CurrentProfile!.Name);
            Assert.Equal(10, ProfileManager.TopProfileMaxLevel);
        }
    }
}
