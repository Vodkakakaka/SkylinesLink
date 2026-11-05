using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;

namespace SkylinesLink.Mod.Connection
{
    public static class HandshakeProof
    {
        public static string RandomHex()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            try { return Hex(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public static string Compute(string token, string role, WireChallengeV2 identity)
        {
            if (role != "server" && role != "client") throw new ArgumentException("Invalid proof role.");
            byte[] key = Decode(token);
            byte[] body = Transcript(role, identity);
            try { using (var hmac = new HMACSHA256(key)) return Hex(hmac.ComputeHash(body)); }
            finally { Array.Clear(key, 0, key.Length); Array.Clear(body, 0, body.Length); }
        }
        public static byte[] Transcript(string role, WireChallengeV2 value)
        {
            var fields = new[] { "SkylinesLink/pipe-auth/v2", role, value.RequestId, value.EnvironmentId, value.InstanceId,
                value.ProductVersion, value.ContractVersion, value.CoreManifestSha256, value.AuthorityEpoch.ToString(CultureInfo.InvariantCulture),
                value.ProfileId, value.GrantEpoch.ToString(CultureInfo.InvariantCulture), value.ClientNonce, value.ServerNonce };
            using (var stream = new MemoryStream())
            {
                var utf8 = new UTF8Encoding(false, true);
                foreach (string field in fields)
                {
                    var bytes = utf8.GetBytes(field); uint count = checked((uint)bytes.Length);
                    for (int shift = 0; shift < 32; shift += 8) stream.WriteByte((byte)(count >> shift));
                    stream.Write(bytes, 0, bytes.Length);
                }
                return stream.ToArray();
            }
        }
        public static bool EqualsProof(string expected, string supplied)
        {
            AuthorizationState.ValidateHex(expected);
            try { AuthorizationState.ValidateHex(supplied); } catch (ArgumentException) { return false; }
            int difference = 0;
            for (int i = 0; i < 64; i++) difference |= expected[i] ^ supplied[i];
            return difference == 0;
        }
        private static byte[] Decode(string value)
        {
            AuthorizationState.ValidateHex(value); var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
