using System;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    /// <summary>
    /// Covers the member-initiated group TownRun announcement: one character reports
    /// "idę do miasta", the coordinator turns it into a synchronized TownRun and
    /// rebroadcasts it so every squadmate does its own tasks and waits together.
    /// </summary>
    public sealed class SquadTownRunRequestTests : IDisposable
    {
        private readonly string _tempDir;

        public SquadTownRunRequestTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PartyGrindTownRequestTests_" + Guid.NewGuid().ToString("N"));
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
        public void MemberAnnouncement_StartsTownRun_AndIsRebroadcast()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10);
            var request = MakeRequest("HealB", member.Guid, member.Role, t, "Sell,Repair");

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                new[] { MakeClient(member, 0, t, request) },
                MakeProfile(), 0, 0, 0, 0f, t);

            Assert.Equal(SquadPhase.TownRun, state.Phase);
            Assert.Equal("HealB", state.TownRunRequestedByName);
            Assert.Equal("Sell,Repair", state.TownRunRequestReason);
            Assert.Equal(t, state.TownRunRequestedUtcMs);
        }

        [Fact]
        public void LocalLeaderAnnouncement_StartsTownRun()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var request = MakeRequest("TankA", 100, SquadRole.TankLeader, t, "Repair");

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                Array.Empty<SquadConnectedMemberSnapshot>(),
                MakeProfile(), 0, 0, 0, 0f, t,
                localTownRunRequest: request);

            Assert.Equal(SquadPhase.TownRun, state.Phase);
            Assert.Equal("TankA", state.TownRunRequestedByName);
            Assert.Equal("Repair", state.TownRunRequestReason);
        }

        [Fact]
        public void RepeatedAnnouncement_DoesNotRestartFinishedRun()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, townComplete: true);
            var request = MakeRequest("HealB", member.Guid, member.Role, t, "Sell");
            var clients = new[] { MakeClient(member, 0, t, request) };

            var running = coord.EvaluateTick(settings, MakeLeader(townComplete: true), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.TownRun, running.Phase);

            t += 1000;
            var done = coord.EvaluateTick(settings, MakeLeader(townComplete: true), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, done.Phase);
            Assert.True(done.AllTownTasksComplete);
            Assert.Empty(done.TownRunRequestedByName);
            Assert.Equal(request.RequestedUtcMs, done.TownRunRequestAcknowledgements["HealB"]);

            // The same announcement (same timestamp) is already acknowledged.
            t += 1000;
            var after = coord.EvaluateTick(settings, MakeLeader(townComplete: true), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, after.Phase);
            Assert.True(after.AllTownTasksComplete);
        }

        [Fact]
        public void NewerAnnouncement_StartsAnotherRun()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, townComplete: true);
            var first = MakeRequest("HealB", member.Guid, member.Role, t, "Sell");

            Assert.Equal(SquadPhase.TownRun, coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[] { MakeClient(member, 0, t, first) }, MakeProfile(), 0, 0, 0, 0f, t).Phase);

            t += 1000;
            Assert.Equal(SquadPhase.Grinding, coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[] { MakeClient(member, 0, t, first) }, MakeProfile(), 0, 0, 0, 0f, t).Phase);

            t += 1000;
            var newer = MakeRequest("HealB", member.Guid, member.Role, t, "Repair");
            var again = coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[] { MakeClient(member, 0, t, newer) }, MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.TownRun, again.Phase);
            Assert.Equal("Repair", again.TownRunRequestReason);
        }

        [Fact]
        public void DisconnectedAnnouncement_IsIgnored()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10);
            var request = MakeRequest("HealB", member.Guid, member.Role, t);

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                new[] { MakeClient(member, 0, t, request, isConnected: false) },
                MakeProfile(), 0, 0, 0, 0f, t);

            Assert.Equal(SquadPhase.Grinding, state.Phase);
            Assert.Empty(state.TownRunRequestedByName);
        }

        [Fact]
        public void Announcement_StartsTownRunEvenWhenAutomaticSyncDisabled()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings(syncTownRuns: false);
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10);
            var request = MakeRequest("HealB", member.Guid, member.Role, t);

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                new[] { MakeClient(member, 0, t, request) },
                MakeProfile(), 0, 0, 0, 0f, t);

            // An explicit announcement is a deliberate group action; only the automatic
            // threshold trigger is gated by SyncTownRuns.
            Assert.Equal(SquadPhase.TownRun, state.Phase);
            Assert.Equal("HealB", state.TownRunRequestedByName);
        }

        [Fact]
        public void AutomaticNeeds_AreIgnoredWhenSyncTownRunsDisabled()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings(syncTownRuns: false);
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, needsSell: true);

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                new[] { MakeClient(member, 0, t) },
                MakeProfile(), 0, 0, 0, 0f, t);

            Assert.Equal(SquadPhase.Grinding, state.Phase);
        }

        [Fact]
        public void AutomaticNeeds_StillTriggerTownRun()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, needsSell: true);

            var state = coord.EvaluateTick(
                settings,
                MakeLeader(),
                new[] { MakeClient(member, 0, t) },
                MakeProfile(), 0, 0, 0, 0f, t);

            Assert.Equal(SquadPhase.TownRun, state.Phase);
            Assert.Empty(state.TownRunRequestedByName);
        }

        [Fact]
        public void Announcement_IsDeferredUntilCombatEnds()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var member = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10);
            var request = MakeRequest("HealB", member.Guid, member.Role, t);
            var clients = new[] { MakeClient(member, 0, t, request) };

            var inCombat = coord.EvaluateTick(settings, MakeLeader(inCombat: true), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Grinding, inCombat.Phase);
            Assert.Empty(inCombat.TownRunRequestedByName);
            Assert.Empty(inCombat.TownRunRequestReason);
            Assert.Equal(0L, inCombat.TownRunRequestedUtcMs);

            t += 1000;
            var afterCombat = coord.EvaluateTick(settings, MakeLeader(), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.Looting, afterCombat.Phase);

            t += Math.Max(1, settings.MaxLootWindowSeconds) * 1000L + 1;
            var afterLoot = coord.EvaluateTick(settings, MakeLeader(), clients,
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.TownRun, afterLoot.Phase);
            Assert.Equal("HealB", afterLoot.TownRunRequestedByName);
        }

        [Fact]
        public void SecondAnnouncementDuringRun_IsRecordedButDoesNotRestart()
        {
            var coord = new SquadCoordinatorService();
            var settings = CreateSettings();
            long t = 1000;
            var healer = MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10);
            var mage = MakeMember("DpsC", 1, SquadRole.DpsRanged, WoWClass.Mage, 10, townComplete: true);
            var first = MakeRequest("HealB", healer.Guid, healer.Role, t, "Sell");

            var running = coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[] { MakeClient(healer, 0, t, first), MakeClient(mage, 1, t) },
                MakeProfile(), 0, 0, 0, 0f, t);
            Assert.Equal(SquadPhase.TownRun, running.Phase);

            // DpsC announces while the run is active.
            t += 1000;
            var second = MakeRequest("DpsC", mage.Guid, mage.Role, t, "Repair");
            var local = MakeRequest("TankA", 100, SquadRole.TankLeader, t - 1, "Manual");
            var updated = coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[]
                {
                    MakeClient(healer, 0, t, first),
                    MakeClient(mage, 1, t, second)
                },
                MakeProfile(), 0, 0, 0, 0f, t, localTownRunRequest: local);
            Assert.Equal(SquadPhase.TownRun, updated.Phase);
            Assert.Equal("DpsC", updated.TownRunRequestedByName);
            Assert.Equal(first.RequestedUtcMs, updated.TownRunRequestAcknowledgements["HealB"]);
            Assert.Equal(second.RequestedUtcMs, updated.TownRunRequestAcknowledgements["DpsC"]);
            Assert.Equal(local.RequestedUtcMs, updated.TownRunRequestAcknowledgements["TankA"]);

            // Both members finish: the run completes and neither announcement replays.
            t += 1000;
            var done = coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[]
                {
                    MakeClient(MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, townComplete: true), 0, t, first),
                    MakeClient(mage, 1, t, second)
                },
                MakeProfile(), 0, 0, 0, 0f, t, localTownRunRequest: local);
            Assert.Equal(SquadPhase.Grinding, done.Phase);
            Assert.Empty(done.TownRunRequestedByName);
            Assert.Equal(first.RequestedUtcMs, done.TownRunRequestAcknowledgements["HealB"]);
            Assert.Equal(second.RequestedUtcMs, done.TownRunRequestAcknowledgements["DpsC"]);

            t += 1000;
            var after = coord.EvaluateTick(settings, MakeLeader(townComplete: true),
                new[]
                {
                    MakeClient(MakeMember("HealB", 0, SquadRole.Healer, WoWClass.Priest, 10, townComplete: true), 0, t, first),
                    MakeClient(mage, 1, t, second)
                },
                MakeProfile(), 0, 0, 0, 0f, t, localTownRunRequest: local);
            Assert.Equal(SquadPhase.Grinding, after.Phase);
        }

        [Fact]
        public void Acknowledgements_SurviveSerializationWithoutDisplayedRequest()
        {
            var leader = new SquadLeaderState
            {
                Phase = SquadPhase.Grinding,
                AllTownTasksComplete = true
            };
            leader.TownRunRequestAcknowledgements["HealB"] = 1000;
            leader.TownRunRequestAcknowledgements["DpsC"] = 2000;

            string json = SquadJson.SerializeEnvelope(SquadMessageTypes.LeaderState, "TestSquad", 1, leader);
            var envelope = SquadJson.DeserializeEnvelope(json);
            Assert.NotNull(envelope);
            var restored = SquadJson.DeserializePayload<SquadLeaderState>(envelope!);

            Assert.NotNull(restored);
            Assert.Equal(0L, restored!.TownRunRequestedUtcMs);
            Assert.True(restored.AllTownTasksComplete);
            Assert.Equal(1000L, restored.TownRunRequestAcknowledgements["HealB"]);
            Assert.Equal(2000L, restored.TownRunRequestAcknowledgements["DpsC"]);
        }

        private SquadSettings CreateSettings(bool syncTownRuns = true)
        {
            string path = Path.Combine(_tempDir, "SquadSettings_TownRequest.xml");
            return new SquadSettings(path)
            {
                TeamId = "TestSquad",
                IsCoordinator = true,
                Role = SquadRole.TankLeader,
                SyncTownRuns = syncTownRuns,
                DisconnectGraceSeconds = 120
            };
        }

        private static SquadMemberState MakeLeader(int level = 10, float x = 100f, float y = 100f, bool townComplete = false, bool inCombat = false)
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
                HealthPercent = 100f,
                PowerPercent = 100f,
                PowerType = WoWPowerType.Rage,
                IsInCombat = inCombat,
                TownTasksComplete = townComplete
            };
        }

        private static SquadMemberState MakeMember(
            string name,
            int slot,
            SquadRole role,
            WoWClass cls,
            int level,
            float x = 100f,
            float y = 100f,
            bool townComplete = false,
            bool needsSell = false)
        {
            return new SquadMemberState
            {
                CharacterName = name,
                Guid = (ulong)(200 + slot),
                Class = cls,
                Role = role,
                Level = level,
                MapId = 0,
                X = x,
                Y = y,
                Z = 0f,
                HealthPercent = 100f,
                PowerPercent = 100f,
                PowerType = WoWPowerType.Mana,
                NeedsSell = needsSell,
                TownTasksComplete = townComplete
            };
        }

        private static SquadConnectedMemberSnapshot MakeClient(
            SquadMemberState state,
            int slot,
            long nowMs,
            SquadTownRunRequest? request = null,
            bool isConnected = true)
        {
            return new SquadConnectedMemberSnapshot
            {
                CharacterName = state.CharacterName,
                SlotIndex = slot,
                IsConnected = isConnected,
                LastReceivedUtcMs = nowMs,
                Handshake = new SquadHandshake
                {
                    CharacterName = state.CharacterName,
                    Guid = state.Guid,
                    Class = state.Class,
                    Role = state.Role,
                    Level = state.Level
                },
                LatestState = state,
                LatestTownRunRequest = request
            };
        }

        private static SquadTownRunRequest MakeRequest(string name, ulong guid, SquadRole role, long requestedUtcMs, string reason = "Sell")
        {
            return new SquadTownRunRequest
            {
                CharacterName = name,
                Guid = guid,
                Role = role,
                Reason = reason,
                RequestedUtcMs = requestedUtcMs
            };
        }

        private static SquadProfileSnapshot MakeProfile(float x = 100f, float y = 100f)
        {
            return new SquadProfileSnapshot
            {
                HasProfile = true,
                SubProfileName = "Zone",
                SubProfileMinLevel = 1,
                SubProfileMaxLevel = 10,
                MapId = 0,
                HotspotX = x,
                HotspotY = y,
                HotspotZ = 0f,
                TownRallyMapId = 0,
                TownRallyX = x,
                TownRallyY = y,
                TownRallyZ = 0f
            };
        }
    }
}
