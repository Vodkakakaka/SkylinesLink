using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;

namespace SkylinesLink.Infrastructure.Operations
{
    public static class HistoryFrameCodec
    {
        public const int MaximumFileBytes = PrivateHistoryLimits.LegacyMaximumFileBytes;
        public const int MaximumPayloadBytes = MaximumFileBytes - 44;
        public const int MaximumMixedFileBytes = PrivateHistoryLimits.MaximumFileBytes;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static int RequiredCapacity(HistoryProjection projection)
        {
            if (projection.Source.FormatVersion == 1) return MaximumFileBytes;
            if (projection.Source.FormatVersion != 2 || projection.Source.JournalSchemaRevision != 3 || !projection.Source.ThroughBytes.HasValue)
                throw new ArgumentException("History source capacity unavailable.");
            return MixedCapacity(projection.Source.ThroughBytes.Value);
        }
        private static int MixedCapacity(long throughBytes)
        {
            if (throughBytes < 45 || throughBytes > PrivateHistoryLimits.MaximumSourceBytes) throw new InvalidDataException("History source byte bound invalid.");
            return checked((int)(3 * throughBytes + PrivateHistoryLimits.FixedProjectionAllowance));
        }
        internal static int MaximumForHeader(byte[] bytes, int count)
        {
            if (count < 12 || bytes.Length < count || bytes[0] != 83 || bytes[1] != 76 || bytes[2] != 72 || bytes[3] != 67 ||
                bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0) throw new InvalidDataException("History frame header invalid.");
            if (bytes[4] == 1) return MaximumFileBytes;
            if (bytes[4] == 2) return MaximumMixedFileBytes;
            throw new InvalidDataException("History frame format unsupported.");
        }
        // 调用方先取得整块容量，再分配/提供唯一 frame；不先物化完整 JSON 字符串。
        public static int Encode(HistoryProjection projection, byte[] frame)
        {
            int capacity = RequiredCapacity(projection);
            if (frame.Length != capacity) throw new ArgumentException("History frame capacity invalid.");
            frame[0] = 83; frame[1] = 76; frame[2] = 72; frame[3] = 67;
            frame[4] = checked((byte)projection.Source.FormatVersion); frame[5] = 0; frame[6] = 0; frame[7] = 0;
            int size;
            using (var memory = new MemoryStream(frame, 12, capacity - 44, true))
            using (var text = new StreamWriter(memory, Utf8, 1024, true))
            using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None, Culture = CultureInfo.InvariantCulture, StringEscapeHandling = StringEscapeHandling.Default })
            {
                var document = projection.Source.FormatVersion == 1
                    ? ContractCodec.EncodeOperationHistoryCheckpoint(projection.ToContract())
                    : ContractCodec.EncodeOperationHistoryCheckpointV2(projection.ToContractV2());
                document.WriteTo(writer);
                writer.Flush(); size = checked((int)memory.Position);
            }
            if (size < 1 || size > capacity - 44) throw new InvalidDataException("History payload exceeds capacity.");
            for (int i = 0; i < 4; i++) frame[8 + i] = (byte)((uint)size >> (8 * i));
            using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(frame, 0, size + 12), 0, frame, size + 12, 32);
            return size + 44;
        }
        public static HistoryProjection Decode(byte[] frame, int length)
        {
            if (length < 45 || length > frame.Length) throw new InvalidDataException("History frame length invalid.");
            int maximum = MaximumForHeader(frame, length);
            if (length > maximum) throw new InvalidDataException("History frame capacity invalid.");
            uint size = 0; for (int i = 0; i < 4; i++) size |= (uint)frame[8 + i] << (8 * i);
            if (size < 1 || size > maximum - 44 || size + 44 != length) throw new InvalidDataException("History frame length invalid.");
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(frame, 0, checked((int)size + 12)); int different = 0;
                for (int i = 0; i < digest.Length; i++) different |= digest[i] ^ frame[12 + (int)size + i];
                if (different != 0) throw new InvalidDataException("History frame digest invalid.");
            }
            string schema = frame[4] == 1 ? SchemaIds.OperationHistoryCheckpoint : SchemaIds.OperationHistoryCheckpointV2;
            var value = ContractCatalog.ParseAndValidate(schema, Utf8.GetString(frame, 12, checked((int)size)));
            if (frame[4] == 1) return HistoryProjection.FromContract(ContractCodec.DecodeOperationHistoryCheckpoint(value));
            long throughBytes = checked((long)value["source"]!["throughBytes"]!.Value<double>());
            if (length > MixedCapacity(throughBytes)) throw new InvalidDataException("History payload exceeds its actual-source budget.");
            return HistoryProjection.FromContract(ContractCodec.DecodeOperationHistoryCheckpointV2(value));
        }
    }
}
