using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Styx.PartyGrind.IPC
{
    public sealed class SquadPeerAuthentication
    {
        private readonly byte[] _key;
        private readonly string _context;
        private readonly string _sendDirection, _receiveDirection;
        private long _sent, _received;
        public string MemberName { get; }

        private SquadPeerAuthentication(string name, string secret, string context, bool server)
        {
            MemberName = name;
            _key = Convert.FromBase64String(secret);
            if (_key.Length < 32) throw new IOException("Squad secrets require at least 32 random bytes.");
            _context = context;
            _sendDirection = server ? "server" : "client";
            _receiveDirection = server ? "client" : "server";
        }

        private string Mac(string value) => Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
        private bool Verify(string value, string proof)
        {
            try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Mac(value)), Convert.FromBase64String(proof)); }
            catch (FormatException) { return false; }
        }

        public string Protect(string line)
        {
            long next = checked(_sent + 1);
            string payload = next.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
            string frame = payload + ":" + Mac(_context + _sendDirection + payload);
            if (frame.Length > SquadJson.MaxFrameCharacters)
                throw new ArgumentException("IPC frame exceeds size limit.", nameof(line));
            _sent = next;
            return frame;
        }

        public string Unprotect(string line)
        {
            var parts = line.Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[0], out long seq) || seq != _received + 1 ||
                !Verify(_context + _receiveDirection + parts[0] + ":" + parts[1], parts[2]))
                throw new IOException("Invalid squad message authentication.");
            _received = seq;
            return Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        }

        private static async Task Write(StreamWriter writer, string line, CancellationToken ct)
        {
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<string> Read(StreamReader reader, CancellationToken ct) =>
            await SquadJson.ReadFrameAsync(reader, ct).ConfigureAwait(false) ?? throw new IOException("Squad authentication disconnected.");

        public static async Task<SquadPeerAuthentication> Server(StreamReader reader, StreamWriter writer,
            IReadOnlyDictionary<string, string> secrets, string team, CancellationToken ct)
        {
            var hello = JsonSerializer.Deserialize<string[]>(await Read(reader, ct).ConfigureAwait(false));
            if (hello == null || hello.Length != 2 || hello[1].Length != 64 || !secrets.TryGetValue(hello[0], out string? secret))
                throw new IOException("Unconfigured squad member.");
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string context = JsonSerializer.Serialize(new[] { team.ToUpperInvariant(), hello[0], hello[1], nonce });
            var auth = new SquadPeerAuthentication(hello[0], secret, context, true);
            await Write(writer, JsonSerializer.Serialize(new[] { nonce, auth.Mac(context + "server-proof") }), ct).ConfigureAwait(false);
            if (!auth.Verify(context + "client-proof", await Read(reader, ct).ConfigureAwait(false)))
                throw new IOException("Squad member authentication failed.");
            return auth;
        }

        public static async Task<SquadPeerAuthentication> Client(StreamReader reader, StreamWriter writer,
            string name, string secret, string team, CancellationToken ct)
        {
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await Write(writer, JsonSerializer.Serialize(new[] { name, nonce }), ct).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<string[]>(await Read(reader, ct).ConfigureAwait(false));
            if (reply == null || reply.Length != 2 || reply[0].Length != 64) throw new IOException("Invalid squad challenge.");
            string context = JsonSerializer.Serialize(new[] { team.ToUpperInvariant(), name, nonce, reply[0] });
            var auth = new SquadPeerAuthentication(name, secret, context, false);
            if (!auth.Verify(context + "server-proof", reply[1])) throw new IOException("Squad coordinator authentication failed.");
            await Write(writer, auth.Mac(context + "client-proof"), ct).ConfigureAwait(false);
            return auth;
        }
    }
}
