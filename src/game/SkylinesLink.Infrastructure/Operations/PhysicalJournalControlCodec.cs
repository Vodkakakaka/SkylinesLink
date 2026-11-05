using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Infrastructure.Operations
{
    /// <summary>两个有限版本共用一套4096字节帧；先核本槽版本及同源契约，再转内部值。</summary>
    internal static class PhysicalJournalControlCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static byte[] Encode(JournalControlDocument value, JournalLayout layout)
        {
            if (value == null || layout == null) throw new ArgumentNullException();
            if (value.FormatVersion != layout.ControlVersion) throw new UnsupportedJournalVersionException();
            return EncodeToken(ToToken(value, layout), layout);
        }
        internal static PhysicalJournalControlSlot? Read(Stream source, long offset, JournalLayout layout)
        {
            var token = ReadToken(source, offset, layout, out string? digest);
            return token == null ? null : new PhysicalJournalControlSlot(FromToken(token, layout), digest!);
        }
        private static string Schema(JournalLayout layout) => layout.ControlVersion == 1 ? SchemaIds.JournalControl : SchemaIds.JournalControlV2;

        // V1的typed wrapper也经过此入口；从不将V2值塞入V1 DTO。
        internal static byte[] EncodeToken(JToken document, JournalLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            ContractCatalog.Require(Schema(layout), document);
            var slot = new byte[JournalBankFormat.SlotBytes];
            try
            {
                int length;
                using (var target = new MemoryStream(slot, 12, JournalBankFormat.MaximumControlPayload, true, false))
                {
                    target.SetLength(0);
                    using (var text = new StreamWriter(target, Utf8, 256, true))
                    using (var json = new JsonTextWriter(text) { CloseOutput = false, Formatting = Formatting.None })
                    { document.WriteTo(json); json.Flush(); text.Flush(); }
                    length = checked((int)target.Length);
                }
                slot[0] = (byte)'S'; slot[1] = (byte)'L'; slot[2] = (byte)'J'; slot[3] = (byte)'C'; slot[4] = checked((byte)layout.ControlVersion);
                uint size = checked((uint)length);
                for (int i = 0; i < 4; i++) slot[8 + i] = (byte)(size >> (8 * i));
                using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(slot, 0, 12 + length), 0, slot, 12 + length, 32);
                return slot;
            }
            catch { Array.Clear(slot, 0, slot.Length); throw; }
        }
        internal static JToken? ReadToken(Stream source, long offset, JournalLayout layout, out string? digest)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            digest = null;
            if (!source.CanRead || !source.CanSeek || offset < 0 || offset > source.Length - JournalBankFormat.SlotBytes)
                throw new InvalidDataException("Journal control slot range invalid.");
            var slot = new byte[JournalBankFormat.SlotBytes];
            try
            {
                source.Position = offset; JournalBankFormat.ReadExactly(source, slot, 0, slot.Length);
                bool empty = true;
                foreach (byte part in slot) if (part != 0) { empty = false; break; }
                if (empty) return null;
                if (slot[0] != 'S' || slot[1] != 'L' || slot[2] != 'J' || slot[3] != 'C' || slot[5] != 0 || slot[6] != 0 || slot[7] != 0)
                    throw new InvalidDataException("Journal control header invalid.");
                if (slot[4] != layout.ControlVersion) throw new UnsupportedJournalVersionException();
                uint length = (uint)slot[8] | ((uint)slot[9] << 8) | ((uint)slot[10] << 16) | ((uint)slot[11] << 24);
                if (length < 1 || length > JournalBankFormat.MaximumControlPayload) throw new InvalidDataException("Journal control length invalid.");
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(slot, 0, 12 + (int)length);
                    int different = 0;
                    for (int i = 0; i < hash.Length; i++) different |= hash[i] ^ slot[12 + (int)length + i];
                    if (different != 0) throw new InvalidDataException("Journal control digest invalid.");
                }
                for (int i = 44 + (int)length; i < slot.Length; i++)
                    if (slot[i] != 0) throw new InvalidDataException("Journal control padding invalid.");
                var token = ContractCatalog.ParseAndValidate(Schema(layout), Utf8.GetString(slot, 12, (int)length));
                digest = JournalBankFormat.Digest(slot, slot.Length);
                return token;
            }
            finally { Array.Clear(slot, 0, slot.Length); }
        }

        private static JObject ToToken(JournalControlDocument document, JournalLayout layout)
        {
            var value = document.Entry ?? throw new InvalidDataException("Journal control entry missing.");
            var entry = new JObject { ["kind"] = value.Kind, ["containerId"] = value.ContainerId, ["environmentId"] = value.EnvironmentId };
            switch (value)
            {
                case JournalRootControl root:
                    if (root.LayoutVersion != layout.LayoutVersion || root.JournalSchemaRevision != layout.JournalSchemaRevision)
                        throw new InvalidDataException("Journal root layout or business family invalid.");
                    entry["layoutVersion"] = root.LayoutVersion;
                    if (layout.ControlVersion == 2) entry["journalSchemaRevision"] = root.JournalSchemaRevision;
                    break;
                case JournalGenerationControl generation:
                    entry["bank"] = generation.Bank; entry["generation"] = generation.Generation; entry["storeId"] = generation.StoreId;
                    entry["source"] = generation.Source == null ? JValue.CreateNull() : Cut(generation.Source);
                    entry["seedFrameCount"] = generation.SeedFrameCount; entry["seedBytes"] = generation.SeedBytes;
                    entry["seedDigest"] = generation.SeedDigest; entry["retentionApplied"] = generation.RetentionApplied; break;
                case JournalActivationControl activation:
                    entry["bank"] = activation.Bank; entry["generation"] = activation.Generation; entry["storeId"] = activation.StoreId;
                    entry["descriptorDigest"] = activation.DescriptorDigest; entry["predecessorSealDigest"] = activation.PredecessorSealDigest == null ? JValue.CreateNull() : new JValue(activation.PredecessorSealDigest); break;
                case JournalSealControl seal:
                    entry["source"] = Cut(seal.Source); entry["successorBank"] = seal.SuccessorBank; entry["successorGeneration"] = seal.SuccessorGeneration;
                    entry["successorStoreId"] = seal.SuccessorStoreId; entry["successorDescriptorDigest"] = seal.SuccessorDescriptorDigest; break;
                case JournalPreparingControl preparing:
                    entry["targetBank"] = preparing.TargetBank; entry["targetGeneration"] = preparing.TargetGeneration;
                    entry["targetStoreId"] = preparing.TargetStoreId; entry["source"] = Cut(preparing.Source); break;
                default: throw new InvalidDataException("Journal control entry unsupported.");
            }
            return new JObject { ["formatVersion"] = document.FormatVersion, ["entry"] = entry };
        }
        private static JObject Cut(JournalCut cut)
        {
            if (cut == null) throw new InvalidDataException("Journal control cut missing.");
            return new JObject { ["bank"] = cut.Bank, ["generation"] = cut.Generation, ["storeId"] = cut.StoreId,
                ["descriptorDigest"] = cut.DescriptorDigest, ["activationDigest"] = cut.ActivationDigest, ["throughSequence"] = cut.ThroughSequence,
                ["throughBytes"] = cut.ThroughBytes, ["prefixDigest"] = cut.PrefixDigest };
        }
        private static JournalControlDocument FromToken(JToken token, JournalLayout layout)
        {
            // ReadToken has already applied the selected strict generated schema.
            var entry = token["entry"]!;
            JournalPhysicalControl value;
            switch ((string)entry["kind"]!)
            {
                case "root": value = new JournalRootControl { LayoutVersion = (long)entry["layoutVersion"]!,
                    JournalSchemaRevision = layout.ControlVersion == 1 ? 2 : (long)entry["journalSchemaRevision"]! }; break;
                case "generation": value = new JournalGenerationControl { Bank = (long)entry["bank"]!, Generation = (long)entry["generation"]!, StoreId = (string)entry["storeId"]!,
                    Source = entry["source"]!.Type == JTokenType.Null ? null : Cut(entry["source"]!), SeedFrameCount = (long)entry["seedFrameCount"]!,
                    SeedBytes = (long)entry["seedBytes"]!, SeedDigest = (string)entry["seedDigest"]!, RetentionApplied = (bool)entry["retentionApplied"]! }; break;
                case "activation": value = new JournalActivationControl { Bank = (long)entry["bank"]!, Generation = (long)entry["generation"]!, StoreId = (string)entry["storeId"]!,
                    DescriptorDigest = (string)entry["descriptorDigest"]!, PredecessorSealDigest = entry["predecessorSealDigest"]!.Type == JTokenType.Null ? null : (string)entry["predecessorSealDigest"]! }; break;
                case "seal": value = new JournalSealControl { Source = Cut(entry["source"]!), SuccessorBank = (long)entry["successorBank"]!, SuccessorGeneration = (long)entry["successorGeneration"]!,
                    SuccessorStoreId = (string)entry["successorStoreId"]!, SuccessorDescriptorDigest = (string)entry["successorDescriptorDigest"]! }; break;
                case "preparing": value = new JournalPreparingControl { TargetBank = (long)entry["targetBank"]!, TargetGeneration = (long)entry["targetGeneration"]!,
                    TargetStoreId = (string)entry["targetStoreId"]!, Source = Cut(entry["source"]!) }; break;
                default: throw new InvalidDataException("Journal control entry unsupported.");
            }
            value.ContainerId = (string)entry["containerId"]!; value.EnvironmentId = (string)entry["environmentId"]!;
            return new JournalControlDocument((long)token["formatVersion"]!, value);
        }
        private static JournalCut Cut(JToken value) => new JournalCut { Bank = (long)value["bank"]!, Generation = (long)value["generation"]!, StoreId = (string)value["storeId"]!,
            DescriptorDigest = (string)value["descriptorDigest"]!, ActivationDigest = (string)value["activationDigest"]!, ThroughSequence = (long)value["throughSequence"]!,
            ThroughBytes = (long)value["throughBytes"]!, PrefixDigest = (string)value["prefixDigest"]! };
    }
}
