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
        public SquadTownRunRequest? LatestTownRunRequest { get; init; }
        public long LastReceivedUtcMs { get; init; }
        public bool IsConnected { get; init; }
    }

    /// <summary>
    /// Completion signal for a leader-state frame. It is satisfied only once every targeted
    /// session has actually written the frame to its socket, not when it was queued, so a
    /// caller that is about to shut the server down can wait for real delivery.
    /// </summary>
    public sealed class SquadLeaderStateDelivery
    {
        private readonly TaskCompletionSource _delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pending;

        internal SquadLeaderStateDelivery(int sessions)
        {
            _pending = sessions;
            if (sessions <= 0)
                _delivered.TrySetResult();
        }

        public Task WhenDelivered => _delivered.Task;

        public bool IsDelivered => _delivered.Task.IsCompleted;

        /// <summary>
        /// Marks one targeted session as settled. A session that was already gone counts as
        /// settled too, so a lost peer can never keep the caller waiting forever.
        /// </summary>
        internal void MarkSettled()
        {
            if (Interlocked.Decrement(ref _pending) <= 0)
                _delivered.TrySetResult();
        }

        public bool Wait(int timeoutMs)
        {
            try { return _delivered.Task.Wait(Math.Max(0, timeoutMs)); }
            catch (AggregateException) { return false; }
        }
    }

    /// <summary>
    /// Non-blocking TCP loopback NDJSON IPC server hosted by the Coordinator client.
    /// </summary>
    public sealed class SquadIpcServer : IDisposable
    {
        private sealed class OutboundFrame
        {
            public readonly string Line;
            public readonly SquadLeaderStateDelivery? Delivery;
            private int _settled;

            public OutboundFrame(string line, SquadLeaderStateDelivery? delivery)
            {
                Line = line;
                Delivery = delivery;
            }

            public void Settle()
            {
                if (Interlocked.Exchange(ref _settled, 1) == 0)
                    Delivery?.MarkSettled();
            }
        }

        private sealed class MemberSession : IDisposable
        {
            internal const int OutboundCapacity = 32;

            public readonly long SessionId;
            public readonly TcpClient Tcp;
            public readonly StreamWriter Writer;
            public readonly Channel<OutboundFrame> Outbound;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public SquadPeerAuthentication? Authentication;
            public string CharacterName = string.Empty;
            public SquadHandshake Handshake = new SquadHandshake();
            public SquadMemberState? LatestState;
            public SquadTownRunRequest? LatestTownRunRequest;
            public long LastReceivedUtcMs;
            public int SlotIndex = -1;
            public volatile bool Closed;
            private readonly HashSet<OutboundFrame> _pendingFrames = new HashSet<OutboundFrame>();
            private readonly Queue<OutboundFrame> _pendingOrder = new Queue<OutboundFrame>();

            public MemberSession(long sessionId, TcpClient tcp)
            {
                SessionId = sessionId;
                Tcp = tcp;
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                Outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(OutboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
                LastReceivedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            /// <summary>
            /// Queues one line for the writer. The optional delivery is settled here if the
            /// frame cannot be queued, and by the writer once the line is flushed.
            /// </summary>
            public void EnqueueLine(string line, SquadLeaderStateDelivery? delivery = null)
            {
                var frame = new OutboundFrame(line, delivery);

                if (delivery != null)
                {
                    // The channel drops its oldest entry when full, so track the deliveries we
                    // owe and settle the oldest as it is evicted. This keeps a stalled peer from
                    // accumulating an unbounded set of frames that will never be written.
                    lock (_pendingFrames)
                    {
                        _pendingOrder.Enqueue(frame);
                        _pendingFrames.Add(frame);
                        while (_pendingFrames.Count > OutboundCapacity)
                            EvictOldestPending();
                    }
                }

                // A frame that never enters the channel is already gone, so its delivery
                // settles here rather than waiting for a writer that will never see it.
                if (Closed || !Outbound.Writer.TryWrite(frame))
                    SettleFrame(frame);
            }

            /// <summary>
            /// Drops <paramref name="frame"/> from the eviction ledger without settling it. The
            /// channel cannot discard a frame a writer already took, so once the send loop owns
            /// it only the write itself may settle the delivery.
            /// </summary>
            public void MarkInFlight(OutboundFrame frame)
            {
                lock (_pendingFrames)
                {
                    _pendingFrames.Remove(frame);
                    while (_pendingOrder.Count > 0 && !_pendingFrames.Contains(_pendingOrder.Peek()))
                        _pendingOrder.Dequeue();
                }
            }

            private void EvictOldestPending()
            {
                while (_pendingOrder.Count > 0)
                {
                    var oldest = _pendingOrder.Dequeue();
                    if (_pendingFrames.Remove(oldest))
                    {
                        oldest.Settle();
                        return;
                    }
                }
            }

            /// <summary>
            /// Marks <paramref name="frame"/> settled and forgets it, so a delivery completes
            /// exactly once whether the writer sent it or the session tore down first.
            /// </summary>
            public void SettleFrame(OutboundFrame frame)
            {
                lock (_pendingFrames)
                {
                    _pendingFrames.Remove(frame);
                    while (_pendingOrder.Count > 0 && !_pendingFrames.Contains(_pendingOrder.Peek()))
                        _pendingOrder.Dequeue();
                }
                frame.Settle();
            }

            /// <summary>
            /// Fails every frame still owed to a writer so their deliveries settle instead of
            /// waiting on a session that will never send again. Only touches the pending set,
            /// never the channel reader that the send loop owns.
            /// </summary>
            public void AbandonPendingDeliveries()
            {
                OutboundFrame[] pending;
                lock (_pendingFrames)
                {
                    pending = new OutboundFrame[_pendingFrames.Count];
                    _pendingFrames.CopyTo(pending);
                    _pendingFrames.Clear();
                    _pendingOrder.Clear();
                }
                foreach (var frame in pending)
                    frame.Settle();
            }

            public void Dispose()
            {
                if (Closed) return;
                Closed = true;
                try { Cts.Cancel(); } catch { }
                try { Outbound.Writer.TryComplete(); } catch { }
                AbandonPendingDeliveries();
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
                        LatestTownRunRequest = session.LatestTownRunRequest,
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
        /// Returns a delivery signal that completes only after every targeted session has
        /// written the frame, so a caller can wait for transmission before shutting down.
        /// </summary>
        public SquadLeaderStateDelivery BroadcastLeaderState(SquadLeaderState state, bool populateRoster = true)
        {
            if (state == null || !IsListening)
                return new SquadLeaderStateDelivery(0);

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
                targets = _namedSessions.Values.Where(s => !s.Closed && (populateRoster || state.MemberAssignments.ContainsKey(s.CharacterName))).ToList();
            }

            // The delivery is completed by each session's writer after the frame reaches the
            // socket, so the caller can wait for real transmission instead of assuming the
            // enqueue was delivery.
            var delivery = new SquadLeaderStateDelivery(targets.Count);
            if (targets.Count == 0)
                return delivery;

            long seq = Interlocked.Increment(ref _nextSequenceId);
            string line = SquadJson.SerializeEnvelope(SquadMessageTypes.LeaderState, _teamId, seq, state);
            foreach (var session in targets)
            {
                session.EnqueueLine(line, delivery);
            }
            return delivery;
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
                    await Task.Delay(DisconnectTimeoutMs, ct).ConfigureAwait(false);
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
                await foreach (OutboundFrame frame in session.Outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // The frame left the channel, so it can no longer be evicted; only the
                    // write below may settle its delivery now.
                    session.MarkInFlight(frame);

                    // Settle in a finally: a frame that cannot be written is gone, and its
                    // delivery must not keep a shutdown waiting on a session that failed.
                    try
                    {
                        string protectedFrame;
                        try { protectedFrame = session.Authentication!.Protect(frame.Line); }
                        catch (ArgumentException) { continue; }
                        await session.Writer.WriteLineAsync(protectedFrame.AsMemory(), ct).ConfigureAwait(false);
                        await session.Writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.SettleFrame(frame);
                    }
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
            finally
            {
                session.AbandonPendingDeliveries();
            }
        }

        private static bool HasValidCoordinates(SquadMemberState state) =>
            float.IsFinite(state.X) && Math.Abs(state.X) <= 17066.666f &&
            float.IsFinite(state.Y) && Math.Abs(state.Y) <= 17066.666f &&
            float.IsFinite(state.Z) && Math.Abs(state.Z) <= 17066.666f &&
            float.IsFinite(state.CorpseX) && Math.Abs(state.CorpseX) <= 17066.666f &&
            float.IsFinite(state.CorpseY) && Math.Abs(state.CorpseY) <= 17066.666f &&
            float.IsFinite(state.CorpseZ) && Math.Abs(state.CorpseZ) <= 17066.666f;

        private void HandleIncomingLine(MemberSession session, string line, CancellationToken linkedToken)
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
                lock (_sessionsLock)
                {
                    // The linked token can be canceled by the deadline task or server
                    // shutdown while this line is being processed. Re-check under the
                    // session lock so a stale handshake never replaces an active member.
                    if (linkedToken.IsCancellationRequested || session.Closed || session.CharacterName.Length > 0)
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
                }

                replaced?.Dispose();
                MemberConnected?.Invoke(hs);
            }
            else if (string.Equals(envelope.MessageType, SquadMessageTypes.MemberState, StringComparison.OrdinalIgnoreCase))
            {
                var state = SquadJson.DeserializePayload<SquadMemberState>(envelope);
                if (state == null)
                    return;
                // Reject malformed coordinates before overwriting the last valid sample.
                if (!HasValidCoordinates(state))
                    return;

                lock (_sessionsLock)
                {
                    if (linkedToken.IsCancellationRequested || session.Closed ||
                        string.IsNullOrEmpty(session.CharacterName) ||
                        !string.Equals(session.CharacterName, state.CharacterName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        state.Guid != session.Handshake.Guid || state.Class != session.Handshake.Class || state.Role != session.Handshake.Role ||
                        !_namedSessions.TryGetValue(session.CharacterName, out var current) || !ReferenceEquals(current, session))
                        return;
                    session.LastReceivedUtcMs = now;
                    session.LatestState = state;
                    // The announcement rides the periodic telemetry on purpose: a one-shot
                    // control frame could be dropped by the bounded outbound queue, while
                    // MemberState is resent until the coordinator clears it.
                    session.LatestTownRunRequest = state.TownRunRequested && state.TownRunRequestedUtcMs > 0
                        ? new SquadTownRunRequest
                        {
                            CharacterName = session.CharacterName,
                            Guid = state.Guid,
                            Role = state.Role,
                            Reason = state.TownRunRequestReason ?? string.Empty,
                            RequestedUtcMs = state.TownRunRequestedUtcMs
                        }
                        : null;
                }

                MemberStateReceived?.Invoke(state);
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
