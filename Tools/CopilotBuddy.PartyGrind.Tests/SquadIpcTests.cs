using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Styx.Combat.CombatRoutine;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadIpcTests
    {
        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 4000)
        {
            long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + timeoutMs;
            while (!predicate())
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                    throw new TimeoutException($"Condition was not met within {timeoutMs} ms.");
                await Task.Delay(25);
            }
        }

        [Theory]
        [InlineData(2)] // 3-client squad: 1 coordinator + 2 clients
        [InlineData(3)] // 4-client squad: 1 coordinator + 3 clients
        [InlineData(4)] // 5-client squad: 1 coordinator + 4 clients
        public async Task ServerAndClients_AssignUniqueSlotsAndPropagateLeaderState(int followerCount)
        {
            using var server = new SquadIpcServer(0, "AlphaTeam");
            server.DisconnectTimeoutMs = 5000;
            server.Start();
            Assert.True(server.IsListening);
            Assert.True(server.Port > 0);

            var clients = new List<SquadIpcClient>(followerCount);
            var expectedNames = new List<string>(followerCount);

            try
            {
                for (int i = 0; i < followerCount; i++)
                {
                    string name = $"Member_{followerCount - i:D2}"; // intentionally reverse order to verify sorting
                    expectedNames.Add(name);

                    var client = new SquadIpcClient(server.Port, "AlphaTeam")
                    {
                        DisconnectTimeoutMs = 5000
                    };
                    SquadRole role = i == 0 ? SquadRole.Healer : (i == 1 ? SquadRole.DpsMelee : SquadRole.DpsRanged);
                    ulong guid = (ulong)(1000 + i);
                    client.Start(() => new SquadHandshake
                    {
                        CharacterName = name,
                        Guid = guid,
                        Class = role == SquadRole.Healer ? WoWClass.Priest : WoWClass.Mage,
                        Role = role,
                        Level = 5 + i
                    });
                    clients.Add(client);
                }

                await WaitUntilAsync(() => server.GetMemberSnapshots().Count == followerCount);

                // Send telemetry from every follower.
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].SendMemberState(new SquadMemberState
                    {
                        CharacterName = expectedNames[i],
                        Guid = (ulong)(1000 + i),
                        Level = 5 + i,
                        HealthPercent = 98f,
                        PowerPercent = 92f
                    });
                }

                await WaitUntilAsync(() => server.GetMemberSnapshots().All(s => s.LatestState != null));

                var leaderState = new SquadLeaderState
                {
                    LeaderGuid = 1,
                    LeaderName = "MainTank",
                    Phase = SquadPhase.Grinding,
                    EffectiveSquadMinLevel = 5,
                    FocusTargetGuid = 999888UL,
                    ActiveSubProfileName = "Zone 1-6",
                    ActiveSubProfileMinLevel = 1,
                    ActiveSubProfileMaxLevel = 6
                };
                server.BroadcastLeaderState(leaderState);

                await WaitUntilAsync(() => clients.All(c =>
                    c.LatestLeaderState != null &&
                    c.LatestLeaderState.FocusTargetGuid == 999888UL));

                // Deterministic slots 0..(followerCount - 1) sorted alphabetically by character name.
                var assignments = server.GetMemberAssignments();
                Assert.Equal(followerCount, assignments.Count);
                Assert.Equal(
                    Enumerable.Range(0, followerCount),
                    assignments.Values.OrderBy(v => v));

                var expectedSorted = expectedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                for (int slot = 0; slot < expectedSorted.Count; slot++)
                {
                    Assert.Equal(slot, assignments[expectedSorted[slot]]);
                }

                foreach (var client in clients)
                {
                    Assert.True(client.IsConnected);
                    Assert.NotNull(client.AssignedSlotIndex);
                    Assert.Equal(5, client.LatestLeaderState!.EffectiveSquadMinLevel);
                    Assert.Equal(SquadPhase.Grinding, client.LatestLeaderState.Phase);
                    Assert.Equal("Zone 1-6", client.LatestLeaderState.ActiveSubProfileName);
                }
            }
            finally
            {
                foreach (var c in clients)
                    c.Dispose();
            }
        }

        [Fact]
        public async Task Server_RejectsWrongTeamId_AndClientReconnectsAfterRestart()
        {
            using var server = new SquadIpcServer(0, "SquadBlue");
            server.DisconnectTimeoutMs = 5000;
            server.Start();

            using var wrongTeamClient = new SquadIpcClient(server.Port, "SquadRed");
            wrongTeamClient.Start(() => new SquadHandshake
            {
                CharacterName = "Intruder",
                Guid = 777,
                Level = 80,
                Role = SquadRole.DpsMelee
            });

            var validClient = new SquadIpcClient(server.Port, "SquadBlue")
            {
                DisconnectTimeoutMs = 5000
            };
            validClient.Start(() => new SquadHandshake
            {
                CharacterName = "LegitHealer",
                Guid = 2001,
                Level = 10,
                Role = SquadRole.Healer
            });

            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            await Task.Delay(150);

            // Intruder from SquadRed must never appear in SquadBlue snapshots.
            var snapshots = server.GetMemberSnapshots();
            Assert.Single(snapshots);
            Assert.Equal("LegitHealer", snapshots[0].CharacterName);

            server.BroadcastLeaderState(new SquadLeaderState
            {
                LeaderName = "MainTank",
                Phase = SquadPhase.Resting,
                EffectiveSquadMinLevel = 10
            });
            await WaitUntilAsync(() => validClient.LatestLeaderState?.Phase == SquadPhase.Resting);

            // Simulate disconnect & client restart on same character name.
            validClient.Stop();
            validClient.Dispose();

            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 0);

            using var restartedClient = new SquadIpcClient(server.Port, "SquadBlue")
            {
                DisconnectTimeoutMs = 5000
            };
            restartedClient.Start(() => new SquadHandshake
            {
                CharacterName = "LegitHealer",
                Guid = 2001,
                Level = 11,
                Role = SquadRole.Healer
            });

            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            server.BroadcastLeaderState(new SquadLeaderState
            {
                LeaderName = "MainTank",
                Phase = SquadPhase.Grinding,
                EffectiveSquadMinLevel = 11
            });

            await WaitUntilAsync(() => restartedClient.LatestLeaderState?.Phase == SquadPhase.Grinding);
            Assert.Equal(11, restartedClient.LatestLeaderState!.EffectiveSquadMinLevel);
        }
    }
}
