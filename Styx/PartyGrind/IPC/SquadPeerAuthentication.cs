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
            string payload = (++_sent).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
            return payload + ":" + Mac(_context + _sendDirection + payload);
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

        /// <summary>
        /// Deserializes an authentication frame, routing malformed JSON through the intended
        /// <see cref="IOException"/> path instead of leaking a JSON exception.
        /// </summary>
        private static string[] ReadArray(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new IOException("Malformed squad authentication message.");

            string[]? values;
            try
            {
                values = JsonSerializer.Deserialize<string[]>(line);
            }
            catch (JsonException)
            {
                throw new IOException("Malformed squad authentication message.");
            }

            if (values == null)
                throw new IOException("Malformed squad authentication message.");

            return values;
        }

        private static bool IsUsableElement(string? value) => !string.IsNullOrEmpty(value);

        public static async Task<SquadPeerAuthentication> Server(StreamReader reader, StreamWriter writer,
            IReadOnlyDictionary<string, string> secrets, string team, CancellationToken ct)
        {
            var hello = ReadArray(await Read(reader, ct).ConfigureAwait(false));
            // Reject null elements before touching .Length, the secret table or base64 decoding.
            if (hello.Length != 2 || !IsUsableElement(hello[0]) || !IsUsableElement(hello[1]))
                throw new IOException("Unconfigured squad member.");

            string memberName = hello[0]!;
            string memberNonce = hello[1]!;
            if (memberNonce.Length != 64 || !secrets.TryGetValue(memberName, out string? secret))
                throw new IOException("Unconfigured squad member.");

            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string context = JsonSerializer.Serialize(new[] { team.ToUpperInvariant(), memberName, memberNonce, nonce });
            var auth = new SquadPeerAuthentication(memberName, secret, context, true);
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
            var reply = ReadArray(await Read(reader, ct).ConfigureAwait(false));
            if (reply.Length != 2 || !IsUsableElement(reply[0]) || !IsUsableElement(reply[1]))
                throw new IOException("Invalid squad challenge.");

            string serverNonce = reply[0]!;
            string serverProof = reply[1]!;
            if (serverNonce.Length != 64)
                throw new IOException("Invalid squad challenge.");

            string context = JsonSerializer.Serialize(new[] { team.ToUpperInvariant(), name, nonce, serverNonce });
            var auth = new SquadPeerAuthentication(name, secret, context, false);
            if (!auth.Verify(context + "server-proof", serverProof)) throw new IOException("Squad coordinator authentication failed.");
            await Write(writer, auth.Mac(context + "client-proof"), ct).ConfigureAwait(false);
            return auth;
        }
    }
}
