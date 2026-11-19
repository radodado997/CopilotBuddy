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
        private static string Secret(string name) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-test-only-" + name)));
        private static Dictionary<string, string> Secrets => new[] { "Follower", "LegitHealer", "Member_01", "Member_02", "Member_03", "Member_04" }.ToDictionary(n => n, Secret);

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

        [Fact]
        public void ProtectedFrames_RejectOversizeWithoutConsumingSequence()
        {
            var constructor = typeof(SquadPeerAuthentication).GetConstructors(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
            var sender = (SquadPeerAuthentication)constructor.Invoke(new object[] { "Follower", Secret("Follower"), "test", false });
            var receiver = (SquadPeerAuthentication)constructor.Invoke(new object[] { "Follower", Secret("Follower"), "test", true });
            Assert.Throws<ArgumentException>(() => sender.Protect(new string('x', SquadJson.MaxFrameCharacters)));
            Assert.Equal("valid", receiver.Unprotect(sender.Protect("valid")));
            Assert.Throws<ArgumentException>(() => sender.Protect(new string('界', SquadJson.MaxFrameCharacters / 3)));
            Assert.Equal("next", receiver.Unprotect(sender.Protect("next")));
        }

        [Fact]
        public async Task Authentication_RejectsDifferentTeamWithCorrectMemberSecret()
        {
            using var server = new SquadIpcServer(0, "SquadBlue", memberSecrets: Secrets);
            server.Start();
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            using var reader = new System.IO.StreamReader(client.GetStream());
            using var writer = new System.IO.StreamWriter(client.GetStream()) { AutoFlush = true };
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
            await Assert.ThrowsAsync<System.IO.IOException>(() => SquadPeerAuthentication.Client(
                reader, writer, "Follower", Secret("Follower"), "SquadRed", timeout.Token));
            Assert.Empty(server.GetMemberSnapshots());
        }

        [Fact]
        public async Task Authentication_AcceptsTeamCaseDifferences()
        {
            using var server = new SquadIpcServer(0, "SquadBlue", memberSecrets: Secrets);
            server.Start();
            using var client = new SquadIpcClient(server.Port, "squadblue", memberSecret: Secret("Follower"));
            client.Start(() => new SquadHandshake { CharacterName = "Follower", Level = 5 });
            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            Assert.Equal("Follower", Assert.Single(server.GetMemberSnapshots()).CharacterName);
        }

        [Fact]
        public async Task SameClient_ReconnectsAutomaticallyAfterListenerRestart()
        {
            using var server = new SquadIpcServer(0, "Retry", memberSecrets: Secrets);
            server.Start();
            int port = server.Port;
            using var client = new SquadIpcClient(port, "Retry", memberSecret: Secret("Follower"));
            client.Start(() => new SquadHandshake { CharacterName = "Follower", Level = 5 });
            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            server.BroadcastLeaderState(new SquadLeaderState { EffectiveSquadMinLevel = 5 });
            await WaitUntilAsync(() => client.LatestLeaderState?.EffectiveSquadMinLevel == 5);
            server.Stop();
            await WaitUntilAsync(() => !client.IsConnected);
            using var replacement = new SquadIpcServer(port, "Retry", memberSecrets: Secrets);
            replacement.Start();
            await WaitUntilAsync(() => replacement.GetMemberSnapshots().Count == 1);
            replacement.BroadcastLeaderState(new SquadLeaderState { EffectiveSquadMinLevel = 6 });
            await WaitUntilAsync(() => client.LatestLeaderState?.EffectiveSquadMinLevel == 6);
        }

        [Fact]
        public async Task WrongSecret_CannotReplaceAuthenticatedMember()
        {
            using var server = new SquadIpcServer(0, "Retry", memberSecrets: Secrets);
            server.Start();
            using var client = new SquadIpcClient(server.Port, "Retry", memberSecret: Secret("Follower"));
            client.Start(() => new SquadHandshake { CharacterName = "Follower", Guid = 42, Level = 5 });
            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            using var impostor = new SquadIpcClient(server.Port, "Retry", memberSecret: Secret("Wrong"));
            impostor.Start(() => new SquadHandshake { CharacterName = "Follower", Guid = 99, Level = 80 });
            await Task.Delay(750);
            Assert.Equal(42UL, Assert.Single(server.GetMemberSnapshots()).Handshake.Guid);
        }

        [Fact]
        public async Task Server_BindFailureCanRetry_AndClientWaitsForLeaderReadiness()
        {
            using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            occupied.Start();
            int port = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;
            using var server = new SquadIpcServer(port, "Retry", memberSecrets: Secrets);
            Assert.Throws<System.Net.Sockets.SocketException>(() => server.Start());
            Assert.False(server.IsListening);
            occupied.Stop();
            server.Start();
            using var client = new SquadIpcClient(port, "Retry", memberSecret: Secret("Follower"));
            client.Start(() => new SquadHandshake { CharacterName = "Follower", Level = 5 });
            await WaitUntilAsync(() => server.GetMemberSnapshots().Count == 1);
            Assert.False(client.IsConnected);
            server.BroadcastLeaderState(new SquadLeaderState { LeaderName = "Tank" });
            await WaitUntilAsync(() => client.IsConnected);
            Assert.NotNull(client.AssignedSlotIndex);
            client.Stop();
            Assert.Null(client.LatestLeaderState);
            Assert.Null(client.AssignedSlotIndex);
        }

        [Fact]
        public async Task BoundedFrames_RejectOversize_AndPreserveCaseInsensitiveAssignments()
        {
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                new string('x', SquadJson.MaxFrameCharacters + 1)));
            using var reader = new System.IO.StreamReader(stream);
            await Assert.ThrowsAsync<System.IO.IOException>(() => SquadJson.ReadFrameAsync(reader, default));
            var state = new SquadLeaderState();
            state.MemberAssignments["Follower"] = 3;
            state.MemberRoles["Follower"] = SquadRole.Healer;
            var envelope = SquadJson.DeserializeEnvelope(SquadJson.SerializeEnvelope(
                SquadMessageTypes.LeaderState, "Test", 1, state))!;
            var restored = SquadJson.DeserializePayload<SquadLeaderState>(envelope)!;
            Assert.Equal(3, restored.MemberAssignments["FOLLOWER"]);
            Assert.Equal(SquadRole.Healer, restored.MemberRoles["follower"]);
        }

        [Theory]
        [InlineData(2)] // 3-client squad: 1 coordinator + 2 clients
        [InlineData(3)] // 4-client squad: 1 coordinator + 3 clients
        [InlineData(4)] // 5-client squad: 1 coordinator + 4 clients
        public async Task ServerAndClients_AssignUniqueSlotsAndPropagateLeaderState(int followerCount)
        {
            using var server = new SquadIpcServer(0, "AlphaTeam", memberSecrets: Secrets);
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

                    var client = new SquadIpcClient(server.Port, "AlphaTeam", memberSecret: Secret(name))
                    {
                        DisconnectTimeoutMs = 5000
                    };
                    SquadRole role = i == 0 ? SquadRole.Healer : (i == 1 ? SquadRole.DpsMelee : SquadRole.DpsRanged);
                    ulong guid = (ulong)(1000 + i);
                    int level = 5 + i;
                    client.Start(() => new SquadHandshake
                    {
                        CharacterName = name,
                        Guid = guid,
                        Class = role == SquadRole.Healer ? WoWClass.Priest : WoWClass.Mage,
                        Role = role,
                        Level = level
                    });
                    clients.Add(client);
                }

                await WaitUntilAsync(() => server.GetMemberSnapshots().Count == followerCount);

                var handshakes = server.GetMemberSnapshots().ToDictionary(s => s.CharacterName, s => s.Handshake);
                for (int i = 0; i < followerCount; i++)
                    Assert.Equal(5 + i, handshakes[expectedNames[i]].Level);

                // Send telemetry from every follower.
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].SendMemberState(new SquadMemberState
                    {
                        CharacterName = expectedNames[i],
                        Role = handshakes[expectedNames[i]].Role,
                        Class = handshakes[expectedNames[i]].Class,
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
                    foreach (var hs in handshakes.Values)
                        Assert.Equal(hs.Role, client.LatestLeaderState!.MemberRoles[hs.CharacterName]);
                    Assert.True(client.IsConnected);
                    Assert.Equal(assignments[expectedNames[clients.IndexOf(client)]], client.AssignedSlotIndex);
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
        public async Task Server_RejectsMalformedAuthentication_AndClientReconnectsAfterRestart()
        {
            using var server = new SquadIpcServer(0, "SquadBlue", memberSecrets: Secrets);
            server.DisconnectTimeoutMs = 5000;
            server.Start();

            using var wrongTeamClient = new System.Net.Sockets.TcpClient();
            await wrongTeamClient.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            using var wrongWriter = new System.IO.StreamWriter(wrongTeamClient.GetStream()) { AutoFlush = true };
            await wrongWriter.WriteLineAsync(SquadJson.SerializeEnvelope(SquadMessageTypes.Handshake, "SquadRed", 1,
                new SquadHandshake { CharacterName = "Intruder", Level = 80 }));
            using var wrongReader = new System.IO.StreamReader(wrongTeamClient.GetStream());
            Assert.Null(await wrongReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Empty(server.GetMemberSnapshots());

            using var validClient = new SquadIpcClient(server.Port, "SquadBlue", memberSecret: Secret("LegitHealer"))
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

            using var restartedClient = new SquadIpcClient(server.Port, "SquadBlue", memberSecret: Secret("LegitHealer"))
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
