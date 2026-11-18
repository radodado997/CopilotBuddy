using System;
using System.IO;
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
    /// Non-blocking TCP loopback NDJSON IPC client with exponential backoff reconnect (500–2000 ms).
    /// </summary>
    public sealed class SquadIpcClient : IDisposable
    {
        private readonly int _port;
        private readonly string _teamId;
        private readonly Func<long> _utcNowMs;
        private readonly object _stateLock = new object();

        private Channel<string> _outbound = CreateOutboundChannel();
        private CancellationTokenSource? _cts;
        private Task? _workerTask;
        private Func<SquadHandshake>? _handshakeProvider;
        private SquadLeaderState? _latestLeaderState;
        private long _lastReceivedUtcMs;
        private long _nextSequenceId;
        private volatile bool _socketConnected;
        private string _localCharacterName = string.Empty;
        private bool _disposed;

        /// <summary>
        /// Maximum idle time in milliseconds before the connection is marked disconnected. Defaults to 3000 ms.
        /// </summary>
        public int DisconnectTimeoutMs { get; set; } = 3000;

        public SquadLeaderState? LatestLeaderState
        {
            get { lock (_stateLock) return _latestLeaderState; }
        }

        public long LastReceivedUtcMs => Interlocked.Read(ref _lastReceivedUtcMs);

        public bool IsConnected
        {
            get
            {
                if (!_socketConnected)
                    return false;
                long last = Interlocked.Read(ref _lastReceivedUtcMs);
                return last > 0 && (_utcNowMs() - last) <= DisconnectTimeoutMs;
            }
        }

        public int? AssignedSlotIndex
        {
            get
            {
                var leader = LatestLeaderState;
                if (leader == null || string.IsNullOrEmpty(_localCharacterName))
                    return null;
                foreach (var kv in leader.MemberAssignments)
                {
                    if (string.Equals(kv.Key, _localCharacterName, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                return null;
            }
        }

        public event Action<SquadLeaderState>? LeaderStateReceived;

        public SquadIpcClient(int port, string teamId, Func<long>? utcNowMs = null)
        {
            _port = port;
            _teamId = teamId ?? string.Empty;
            _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public void Start(Func<SquadHandshake> handshakeProvider)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SquadIpcClient));
            if (_cts != null && !_cts.IsCancellationRequested)
                return;

            _handshakeProvider = handshakeProvider ?? throw new ArgumentNullException(nameof(handshakeProvider));
            _outbound = CreateOutboundChannel();
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        }

        public void SendMemberState(SquadMemberState state)
        {
            if (state == null || _cts == null || _cts.IsCancellationRequested)
                return;

            if (!string.IsNullOrWhiteSpace(state.CharacterName))
                _localCharacterName = state.CharacterName.Trim();

            long seq = Interlocked.Increment(ref _nextSequenceId);
            string line = SquadJson.SerializeEnvelope(SquadMessageTypes.MemberState, _teamId, seq, state);
            _outbound.Writer.TryWrite(line);
        }

        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            _socketConnected = false;
            try { cts?.Cancel(); } catch { }
            try { _outbound.Writer.TryComplete(); } catch { }
            try { _workerTask?.Wait(500); } catch { }
            _workerTask = null;
            try { cts?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private static Channel<string> CreateOutboundChannel()
        {
            return Channel.CreateBounded<string>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        private async Task ConnectionLoopAsync(CancellationToken ct)
        {
            int backoffMs = 500;
            while (!ct.IsCancellationRequested)
            {
                TcpClient? tcp = null;
                try
                {
                    tcp = new TcpClient { NoDelay = true };
                    await tcp.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);
                    backoffMs = 500; // reset on successful TCP connect

                    SquadHandshake hs = _handshakeProvider!.Invoke();
                    _localCharacterName = hs.CharacterName?.Trim() ?? string.Empty;

                    Interlocked.Exchange(ref _lastReceivedUtcMs, _utcNowMs());
                    _socketConnected = true;

                    await RunConnectedSessionAsync(tcp, hs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logging.WriteDebug("[PartyGrind IPC] Client connection ended: {0}", ex.Message);
                }
                finally
                {
                    _socketConnected = false;
                    try { tcp?.Dispose(); } catch { }
                }

                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoffMs = Math.Min(2000, backoffMs * 2);
            }
        }

        private async Task RunConnectedSessionAsync(TcpClient tcp, SquadHandshake handshake, CancellationToken outerCt)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            CancellationToken ct = sessionCts.Token;

            var stream = tcp.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            using var reader = new StreamReader(stream, new UTF8Encoding(false));

            long seq = Interlocked.Increment(ref _nextSequenceId);
            string hsLine = SquadJson.SerializeEnvelope(SquadMessageTypes.Handshake, _teamId, seq, handshake);
            await writer.WriteLineAsync(hsLine.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);

            Task sendTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (string line in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { sessionCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }, ct);

            Task watchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                        long last = Interlocked.Read(ref _lastReceivedUtcMs);
                        if (last > 0 && (_utcNowMs() - last) > DisconnectTimeoutMs)
                        {
                            _socketConnected = false;
                            sessionCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null)
                        break;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    HandleIncomingLine(line);
                }
            }
            finally
            {
                _socketConnected = false;
                try { sessionCts.Cancel(); } catch { }
                try { tcp.Close(); } catch { }
                try { await sendTask.ConfigureAwait(false); } catch { }
                try { await watchdogTask.ConfigureAwait(false); } catch { }
            }
        }

        private void HandleIncomingLine(string line)
        {
            SquadEnvelope? envelope;
            try
            {
                envelope = SquadJson.DeserializeEnvelope(line);
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind IPC] Client ignored malformed JSON: {0}", ex.Message);
                return;
            }

            if (envelope == null)
                return;

            if (!string.IsNullOrEmpty(_teamId) &&
                !string.Equals(envelope.TeamId, _teamId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(envelope.MessageType, SquadMessageTypes.LeaderState, StringComparison.OrdinalIgnoreCase))
            {
                var leader = SquadJson.DeserializePayload<SquadLeaderState>(envelope);
                if (leader == null)
                    return;

                Interlocked.Exchange(ref _lastReceivedUtcMs, _utcNowMs());
                lock (_stateLock)
                {
                    _latestLeaderState = leader;
                }
                LeaderStateReceived?.Invoke(leader);
            }
        }
    }
}
