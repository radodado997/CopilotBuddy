using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Styx.Helpers;

namespace Styx.PartyGrind.IPC
{
    /// <summary>
    /// Snapshot of a connected member session on the Coordinator IPC server.
    /// </summary>
    public sealed class SquadConnectedMemberSnapshot
    {
        public string CharacterName { get; init; } = string.Empty;
        public int SlotIndex { get; init; }
        public SquadHandshake Handshake { get; init; } = new SquadHandshake();
        public SquadMemberState? LatestState { get; init; }
        public long LastReceivedUtcMs { get; init; }
        public bool IsConnected { get; init; }
    }

    /// <summary>
    /// Non-blocking TCP loopback NDJSON IPC server hosted by the Coordinator client.
    /// </summary>
    public sealed class SquadIpcServer : IDisposable
    {
        private sealed class MemberSession : IDisposable
        {
            public readonly long SessionId;
            public readonly TcpClient Tcp;
            public readonly StreamWriter Writer;
            public readonly Channel<string> Outbound;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public volatile SquadPeerAuthentication? Authentication;
            public string CharacterName = string.Empty;
            public SquadHandshake Handshake = new SquadHandshake();
            public SquadMemberState? LatestState;
            public long LastReceivedUtcMs;
            public int SlotIndex = -1;
            public volatile bool Closed;

            public MemberSession(long sessionId, TcpClient tcp)
            {
                SessionId = sessionId;
                Tcp = tcp;
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                Outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
                LastReceivedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            public void EnqueueLine(string line)
            {
                if (!Closed)
                    Outbound.Writer.TryWrite(line);
            }

            public void Dispose()
            {
                if (Closed) return;
                Closed = true;
                try { Cts.Cancel(); } catch { }
                try { Outbound.Writer.TryComplete(); } catch { }
                try { Tcp.Close(); } catch { }
                try { Tcp.Dispose(); } catch { }
                try { Cts.Dispose(); } catch { }
            }
        }

        private readonly IReadOnlyDictionary<string, string> _memberSecrets;
        private readonly int _requestedPort;
        private readonly string _teamId;
        private readonly Func<long> _utcNowMs;
        private readonly object _sessionsLock = new object();
        private readonly object _callbackGate = new object();
        private bool _callbacksAllowed = true;
        private readonly Dictionary<string, MemberSession> _namedSessions = new Dictionary<string, MemberSession>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<long, MemberSession> _allSessions = new ConcurrentDictionary<long, MemberSession>();

        private TcpListener? _listener;
        private CancellationTokenSource? _serverCts;
        private Task? _acceptTask;
        private long _nextSessionId;
        private long _nextSequenceId;
        private int _actualPort;
        private bool _disposed;

        /// <summary>
        /// Maximum idle silence in milliseconds before a peer is treated as disconnected. Defaults to 3000 ms.
        /// </summary>
        public int DisconnectTimeoutMs { get; set; } = 3000;

        public int Port => _actualPort > 0 ? _actualPort : _requestedPort;
        public bool IsListening => _listener != null && _serverCts != null && !_serverCts.IsCancellationRequested;

        public event Action<SquadMemberState>? MemberStateReceived;
        public event Action<SquadHandshake>? MemberConnected;

        public SquadIpcServer(int port, string teamId, Func<long>? utcNowMs = null, IReadOnlyDictionary<string, string>? memberSecrets = null)
        {
            _memberSecrets = new Dictionary<string, string>(memberSecrets ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            _requestedPort = port;
            _teamId = teamId ?? string.Empty;
            _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SquadIpcServer));
            if (IsListening)
                return;

            var listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            try { listener.Start(); }
            catch { listener.Stop(); throw; }
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            lock (_callbackGate)
            {
                _callbacksAllowed = true;
            }
            _listener = listener;
            _serverCts = cts;
            _actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, token));
        }

        public void Stop()
        {
            var cts = _serverCts;
            _serverCts = null;
            try { cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var kv in _allSessions.ToArray())
            {
                if (_allSessions.TryRemove(kv.Key, out var session))
                    session.Dispose();
            }

            lock (_sessionsLock)
            {
                _namedSessions.Clear();
            }

            try { _acceptTask?.Wait(500); } catch { }
            _acceptTask = null;
            try { cts?.Dispose(); } catch { }

            // Waits for an in-flight MemberConnected callback (the gate is held while invoking) and
            // blocks every later one, so no callback can run after Stop() returns.
            lock (_callbackGate)
            {
                _callbacksAllowed = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        /// <summary>
        /// Computes deterministic slot assignments (0..N-1) sorted case-insensitively by character name.
        /// </summary>
        public Dictionary<string, int> GetMemberAssignments()
        {
            lock (_sessionsLock)
            {
                PruneTimedOutLocked(_utcNowMs());
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _namedSessions.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    result[kv.Key] = kv.Value.SlotIndex;
                }
                return result;
            }
        }

        /// <summary>
        /// Returns a consistent snapshot of active/recent member sessions.
        /// </summary>
        public List<SquadConnectedMemberSnapshot> GetMemberSnapshots()
        {
            long now = _utcNowMs();
            lock (_sessionsLock)
            {
                PruneTimedOutLocked(now);
                var list = new List<SquadConnectedMemberSnapshot>(_namedSessions.Count);
                foreach (var session in _namedSessions.Values.OrderBy(s => s.SlotIndex).ThenBy(s => s.CharacterName, StringComparer.OrdinalIgnoreCase))
                {
                    bool alive = !session.Closed && (now - session.LastReceivedUtcMs) <= DisconnectTimeoutMs;
                    list.Add(new SquadConnectedMemberSnapshot
                    {
                        CharacterName = session.CharacterName,
                        SlotIndex = session.SlotIndex,
                        Handshake = session.Handshake,
                        LatestState = session.LatestState,
                        LastReceivedUtcMs = session.LastReceivedUtcMs,
                        IsConnected = alive
                    });
                }
                return list;
            }
        }

        /// <summary>
        /// Broadcasts <see cref="SquadLeaderState"/> to every connected client session without blocking.
        /// Automatically populates <see cref="SquadLeaderState.MemberAssignments"/> and
        /// <see cref="SquadLeaderState.MemberRoles"/> when empty or stale.
        /// </summary>
        public void BroadcastLeaderState(SquadLeaderState state, bool populateRoster = true)
        {
            if (state == null || !IsListening)
                return;

            List<MemberSession> targets;
            lock (_sessionsLock)
            {
                PruneTimedOutLocked(_utcNowMs());
                if (populateRoster)
                {
                    foreach (var name in state.MemberAssignments.Keys.Where(n => !string.Equals(n, state.LeaderName, StringComparison.OrdinalIgnoreCase)).ToList())
                        state.MemberAssignments.Remove(name);
                    foreach (var name in state.MemberRoles.Keys.Where(n => !string.Equals(n, state.LeaderName, StringComparison.OrdinalIgnoreCase)).ToList())
                        state.MemberRoles.Remove(name);
                    foreach (var kv in _namedSessions.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        state.MemberAssignments[kv.Key] = kv.Value.SlotIndex;
                        state.MemberRoles[kv.Key] = kv.Value.Handshake.Role;
                    }
                }
                targets = _namedSessions.Values.Where(s => !s.Closed).ToList();
            }

            if (targets.Count == 0)
                return;

            long seq = Interlocked.Increment(ref _nextSequenceId);
            string line = SquadJson.SerializeEnvelope(SquadMessageTypes.LeaderState, _teamId, seq, state);
            foreach (var session in targets)
            {
                session.EnqueueLine(line);
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    Logging.WriteDebug("[PartyGrind IPC] Accept error: {0}", ex.Message);
                    continue;
                }

                if (_allSessions.Values.Count(s => string.IsNullOrEmpty(s.CharacterName)) >= 16)
                {
                    client.Dispose();
                    continue;
                }
                long sessionId = Interlocked.Increment(ref _nextSessionId);
                var session = new MemberSession(sessionId, client);
                _allSessions[sessionId] = session;
                _ = Task.Run(() => RunSessionAsync(session, ct));
            }
        }

        private async Task RunSessionAsync(MemberSession session, CancellationToken serverCt)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt, session.Cts.Token);
            CancellationToken ct = linkedCts.Token;

            Task sendTask = Task.CompletedTask;
            Task deadline = Task.Run(async () =>
            {
                try
                {
                    // Unauthenticated sockets only get a short, fixed window: a local process must not
                    // be able to occupy the pending-session budget for a whole disconnect timeout.
                    int preAuthMs = Math.Min(DisconnectTimeoutMs, 5000);
                    await Task.Delay(preAuthMs, ct).ConfigureAwait(false);
                    if (session.Authentication == null)
                    {
                        linkedCts.Cancel();
                        session.Tcp.Close();
                        return;
                    }

                    // Authenticated sessions keep the configured window to send their handshake.
                    int handshakeMs = Math.Max(0, DisconnectTimeoutMs - preAuthMs);
                    if (handshakeMs > 0)
                        await Task.Delay(handshakeMs, ct).ConfigureAwait(false);

                    lock (_sessionsLock)
                        if (string.IsNullOrEmpty(session.CharacterName))
                        {
                            linkedCts.Cancel();
                            session.Tcp.Close();
                        }
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                using var reader = new StreamReader(session.Tcp.GetStream(), new UTF8Encoding(false));
                session.Authentication = await SquadPeerAuthentication.Server(reader, session.Writer, _memberSecrets, _teamId, ct).ConfigureAwait(false);
                sendTask = Task.Run(() => SendLoopAsync(session, ct), ct);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await SquadJson.ReadFrameAsync(reader, ct).ConfigureAwait(false);
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try { HandleIncomingLine(session, session.Authentication.Unprotect(line), ct); }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind IPC] Session read error: {0}", ex.Message);
            }
            finally
            {
                try { linkedCts.Cancel(); } catch { }
                try { session.Tcp.Close(); } catch { }
                RemoveSession(session);
                try { await sendTask.ConfigureAwait(false); } catch { }
                try { await deadline.ConfigureAwait(false); } catch { }
                session.Dispose();
            }
        }

        private async Task SendLoopAsync(MemberSession session, CancellationToken ct)
        {
            try
            {
                await foreach (string line in session.Outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await session.Writer.WriteLineAsync(session.Authentication!.Protect(line).AsMemory(), ct).ConfigureAwait(false);
                    await session.Writer.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void HandleIncomingLine(MemberSession session, string line, CancellationToken serverRunToken)
        {
            SquadEnvelope? envelope;
            try
            {
                envelope = SquadJson.DeserializeEnvelope(line);
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind IPC] Malformed JSON envelope ignored: {0}", ex.Message);
                return;
            }

            if (envelope == null)
                return;

            if (!string.IsNullOrEmpty(_teamId) &&
                !string.Equals(envelope.TeamId, _teamId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long now = _utcNowMs();
            if (string.Equals(envelope.MessageType, SquadMessageTypes.Handshake, StringComparison.OrdinalIgnoreCase))
            {
                var hs = SquadJson.DeserializePayload<SquadHandshake>(envelope);
                if (hs == null || !string.Equals(hs.CharacterName, session.Authentication?.MemberName, StringComparison.OrdinalIgnoreCase))
                    return;

                MemberSession? replaced = null;
                bool registered = false;
                lock (_sessionsLock)
                {
                    if (session.CharacterName.Length > 0)
                        return;

                    // A handshake that completes while the server is shutting down must never join the
                    // roster or raise MemberConnected.
                    if (serverRunToken.IsCancellationRequested || session.Closed || !IsListening)
                        return;

                    session.CharacterName = hs.CharacterName.Trim();
                    session.Handshake = hs;
                    session.LastReceivedUtcMs = now;

                    if (_namedSessions.TryGetValue(session.CharacterName, out var existing) && !ReferenceEquals(existing, session))
                    {
                        replaced = existing;
                    }

                    _namedSessions[session.CharacterName] = session;
                    ReassignSlotsLocked();
                    registered = true;
                }

                if (!registered)
                    return;

                replaced?.Dispose();
                lock (_callbackGate)
                {
                    if (_callbacksAllowed)
                        MemberConnected?.Invoke(hs);
                }
            }
            else if (string.Equals(envelope.MessageType, SquadMessageTypes.MemberState, StringComparison.OrdinalIgnoreCase))
            {
                var state = SquadJson.DeserializePayload<SquadMemberState>(envelope);
                if (state == null)
                    return;

                lock (_sessionsLock)
                {
                    if (string.IsNullOrEmpty(session.CharacterName) ||
                        !string.Equals(session.CharacterName, state.CharacterName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        state.Guid != session.Handshake.Guid || state.Class != session.Handshake.Class || state.Role != session.Handshake.Role ||
                        !_namedSessions.TryGetValue(session.CharacterName, out var current) || !ReferenceEquals(current, session))
                        return;
                    session.LastReceivedUtcMs = now;
                    session.LatestState = state;
                    session.Handshake = new SquadHandshake
                    {
                        CharacterName = session.CharacterName,
                        Guid = state.Guid,
                        Class = state.Class,
                        Role = state.Role,
                        Level = state.Level,
                        ProtocolVersion = session.Handshake.ProtocolVersion
                    };
                }

                lock (_callbackGate)
                {
                    if (_callbacksAllowed)
                        MemberStateReceived?.Invoke(state);
                }
            }
        }

        private void RemoveSession(MemberSession session)
        {
            _allSessions.TryRemove(session.SessionId, out _);
            lock (_sessionsLock)
            {
                if (!string.IsNullOrEmpty(session.CharacterName)
                    && _namedSessions.TryGetValue(session.CharacterName, out var current)
                    && ReferenceEquals(current, session))
                {
                    _namedSessions.Remove(session.CharacterName);
                    ReassignSlotsLocked();
                }
            }
        }

        private void PruneTimedOutLocked(long nowMs)
        {
            List<MemberSession>? stale = null;
            foreach (var kv in _namedSessions)
            {
                if (kv.Value.Closed || (nowMs - kv.Value.LastReceivedUtcMs) > DisconnectTimeoutMs)
                {
                    stale ??= new List<MemberSession>();
                    stale.Add(kv.Value);
                }
            }

            if (stale == null)
                return;

            foreach (var s in stale)
            {
                if (!string.IsNullOrEmpty(s.CharacterName))
                    _namedSessions.Remove(s.CharacterName);
                s.Dispose();
            }
            ReassignSlotsLocked();
        }

        private void ReassignSlotsLocked()
        {
            int idx = 0;
            foreach (var kv in _namedSessions.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                kv.Value.SlotIndex = idx++;
            }
        }
    }
}
