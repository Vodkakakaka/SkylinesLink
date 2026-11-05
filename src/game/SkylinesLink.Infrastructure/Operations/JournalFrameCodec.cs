using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;

namespace SkylinesLink.Infrastructure.Operations
{
    internal sealed class UnsupportedJournalVersionException : IOException
    { internal UnsupportedJournalVersionException() : base("Operation journal format is incompatible.") { } }

    internal static partial class JournalFrameCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static byte[] Encode(JournalRecord record, out int length)
        {
            if (record.SchemaRevision != 2 || record.Entry is OperationAccepted accepted && accepted.Intent.ProfileInstanceId != null)
                throw new UnsupportedJournalVersionException();
            JObject document = ToJson(record);
            return EncodeDocument(document, SchemaIds.OperationJournalRecordV2, 2, OperationLimits.MaximumRecordPayload, out length);
        }

        private static byte[] EncodeDocument(JObject document, string schemaId, byte version, int maximumPayload, out int length)
        {
            if (!ContractCatalog.Validate(schemaId, document).Valid) throw new InvalidDataException("Journal record contract invalid.");
            var frame = new byte[maximumPayload + 44];
            try
            {
                int payloadLength;
                using (var target = new MemoryStream(frame, 12, maximumPayload, true, false))
                {
                    target.SetLength(0);
                    using (var text = new StreamWriter(target, Utf8, 256, true))
                    using (var json = new JsonTextWriter(text) { CloseOutput = false, Formatting = Formatting.None })
                    { document.WriteTo(json); json.Flush(); text.Flush(); }
                    payloadLength = checked((int)target.Length);
                }
                frame[0] = (byte)'S'; frame[1] = (byte)'L'; frame[2] = (byte)'J'; frame[3] = (byte)'O'; frame[4] = version;
                uint size = checked((uint)payloadLength);
                for (int i = 0; i < 4; i++) frame[8 + i] = (byte)(size >> (8 * i));
                using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(frame, 0, 12 + payloadLength), 0, frame, 12 + payloadLength, 32);
                length = payloadLength + 44;
                return frame;
            }
            catch { Array.Clear(frame, 0, frame.Length); throw; }
        }

        internal static JournalRecord? Read(Stream source) => ReadCore(source, false);

        private static JournalRecord? ReadCore(Stream source, bool allowMixed)
        {
            var header = new byte[12];
            int first = source.ReadByte();
            if (first < 0) return null;
            header[0] = checked((byte)first); ReadExactly(source, header, 1, 11);
            if (header[0] != 'S' || header[1] != 'L' || header[2] != 'J' || header[3] != 'O' || header[5] != 0 || header[6] != 0 || header[7] != 0)
                throw new InvalidDataException("Journal frame header invalid.");
            byte version = header[4];
            if (version != 2 && (version != 3 || !allowMixed)) throw new UnsupportedJournalVersionException();
            uint size = (uint)header[8] | ((uint)header[9] << 8) | ((uint)header[10] << 16) | ((uint)header[11] << 24);
            int maximumPayload = version == 3 ? MaximumMixedMetadataPayload : OperationLimits.MaximumRecordPayload;
            if (size == 0 || size > maximumPayload) throw new InvalidDataException("Journal frame length invalid.");
            var frame = new byte[checked((int)size + 44)];
            try
            {
                Buffer.BlockCopy(header, 0, frame, 0, header.Length); ReadExactly(source, frame, 12, checked((int)size + 32));
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(frame, 0, checked((int)size + 12));
                    int different = 0;
                    for (int i = 0; i < 32; i++) different |= hash[i] ^ frame[12 + (int)size + i];
                    if (different != 0) throw new InvalidDataException("Journal frame digest invalid.");
                }
                var schemaId = version == 3 ? SchemaIds.OperationJournalRecordV3 : SchemaIds.OperationJournalRecordV2;
                var value = (JObject)ContractCatalog.ParseAndValidate(schemaId, Utf8.GetString(frame, 12, (int)size));
                var record = version == 3 ? FromMixedJson(value) : FromJson(value);
                if (!IsTaskMetadata(record.Entry) && size > OperationLimits.MaximumRecordPayload)
                    throw new InvalidDataException("Operation frame exceeds its own payload budget.");
                return record;
            }
            finally { Array.Clear(frame, 0, frame.Length); }
        }
        private static void ReadExactly(Stream stream, byte[] value, int offset, int length)
        {
            while (length != 0)
            { int read = stream.Read(value, offset, length); if (read == 0) throw new EndOfStreamException(); offset += read; length -= read; }
        }

        private static JObject ToJson(JournalRecord record)
        {
            JObject body;
            switch (record.Entry)
            {
                case StoreOpened header: body = new JObject { ["kind"] = "header", ["storeId"] = header.StoreId }; break;
                case OperationAccepted accepted:
                    var intent = accepted.Intent;
                    body = new JObject { ["kind"] = "accepted", ["operationId"] = intent.OperationId, ["originInstanceId"] = intent.OriginInstanceId,
                        ["writeContextId"] = intent.Key.WriteContextId, ["requestId"] = intent.Key.RequestId, ["commandHash"] = intent.CommandHash,
                        ["profileId"] = intent.ProfileId, ["authorityEpoch"] = intent.AuthorityEpoch, ["grantEpoch"] = intent.GrantEpoch,
                        ["source"] = Source(intent.Source), ["target"] = Target(intent.Target) }; break;
                case CommitIntended commit:
                    body = new JObject { ["kind"] = "commitIntent", ["operationId"] = commit.OperationId, ["target"] = Target(commit.Target),
                        ["nativeHost"] = new JObject { ["processId"] = commit.NativeHost.ProcessId, ["creationTime"] = commit.NativeHost.CreationTime.ToString(CultureInfo.InvariantCulture) } }; break;
                case NativeTerminated terminal:
                    body = new JObject { ["kind"] = "nativeTerminal", ["operationId"] = terminal.OperationId, ["physicalTaskState"] = Word(terminal.State),
                        ["loadReturnValue"] = terminal.LoadReturnValue.HasValue ? new JValue(terminal.LoadReturnValue.Value) : JValue.CreateNull(),
                        ["boundTarget"] = terminal.BoundTarget == null ? JValue.CreateNull() : Target(terminal.BoundTarget) }; break;
                case OperationFinished final:
                    body = new JObject { ["kind"] = "terminal", ["operationId"] = final.OperationId, ["state"] = final.State,
                        ["mayHaveApplied"] = final.MayHaveApplied, ["submissionKnowledge"] = Word(final.Submission), ["reasonCode"] = final.ReasonCode,
                        ["load"] = Facts(final.Load) }; break;
                default: throw new InvalidDataException("Unsupported journal entry.");
            }
            return new JObject { ["sequence"] = record.Sequence, ["environmentId"] = record.EnvironmentId, ["entry"] = body };
        }
        private static JournalRecord FromJson(JObject value)
        {
            var body = (JObject)value["entry"]!;
            string kind = (string)body["kind"]!;
            JournalEntry entry;
            switch (kind)
            {
                case "header": entry = new StoreOpened((string)body["storeId"]!); break;
                case "accepted": entry = new OperationAccepted(new OperationIntent((string)body["operationId"]!, (string)body["originInstanceId"]!,
                    new SubmissionKey((string)body["writeContextId"]!, (string)body["requestId"]!), (string)body["commandHash"]!,
                    (string)body["profileId"]!, (long)body["authorityEpoch"]!, (long)body["grantEpoch"]!,
                    Source((JObject)body["source"]!), Target((JObject)body["target"]!))); break;
                case "commitIntent":
                    var native = (JObject)body["nativeHost"]!;
                    entry = new CommitIntended((string)body["operationId"]!, Target((JObject)body["target"]!),
                        new NativeHostIdentity((int)native["processId"]!, CanonicalNumber((string)native["creationTime"]!))); break;
                case "nativeTerminal": entry = new NativeTerminated((string)body["operationId"]!, EnumValue<NativeLoadTaskState>((string)body["physicalTaskState"]!),
                    (bool?)body["loadReturnValue"],
                    body["boundTarget"]!.Type == JTokenType.Null ? null : Target((JObject)body["boundTarget"]!)); break;
                case "terminal": entry = new OperationFinished((string)body["operationId"]!, (string)body["state"]!, (bool)body["mayHaveApplied"]!,
                    EnumValue<SubmissionKnowledge>((string)body["submissionKnowledge"]!), (string)body["reasonCode"]!, Facts((JObject)body["load"]!)); break;
                default: throw new InvalidDataException("Unsupported journal entry.");
            }
            return new JournalRecord((long)value["sequence"]!, (string)value["environmentId"]!, entry);
        }
        private static JObject Source(WorldContextSnapshot value) => new JObject { ["instanceId"] = value.InstanceId,
            ["generation"] = value.Generation.ToString(CultureInfo.InvariantCulture), ["gameState"] = value.State, ["cityId"] = value.CityId == null ? JValue.CreateNull() : new JValue(value.CityId) };
        private static WorldContextSnapshot Source(JObject value) => new WorldContextSnapshot((string)value["instanceId"]!,
            CanonicalNumber((string)value["generation"]!), (string)value["gameState"]!, (string?)value["cityId"]);
        private static JObject Target(LoadTarget value) => new JObject { ["providerId"] = value.ProviderId, ["assetId"] = value.AssetId, ["contentVersion"] = value.ContentVersion == null ? JValue.CreateNull() : new JValue(value.ContentVersion) };
        private static LoadTarget Target(JObject value) => new LoadTarget((string)value["providerId"]!, (string)value["assetId"]!, (string?)value["contentVersion"]);
        private static JObject Facts(PersistedLoadFacts value) => new JObject { ["physicalTaskState"] = Word(value.PhysicalTaskState),
            ["loadReturnValue"] = value.LoadReturnValue.HasValue ? new JValue(value.LoadReturnValue.Value) : JValue.CreateNull(), ["targetMatch"] = Word(value.TargetMatch),
            ["correlatedTargetReady"] = value.CorrelatedTargetReady, ["historicalCompletionProven"] = value.HistoricalCompletionProven,
            ["observationEnd"] = Word(value.ObservationEnd), ["observationContext"] = value.ObservationContext == null ? JValue.CreateNull() : Source(value.ObservationContext) };
        private static PersistedLoadFacts Facts(JObject value) => new PersistedLoadFacts(EnumValue<NativeLoadTaskState>((string)value["physicalTaskState"]!),
            (bool?)value["loadReturnValue"],
            EnumValue<LoadTargetMatch>((string)value["targetMatch"]!), (bool)value["correlatedTargetReady"]!, (bool)value["historicalCompletionProven"]!,
            EnumValue<LoadObservationEnd>((string)value["observationEnd"]!), value["observationContext"]!.Type == JTokenType.Null ? null : Source((JObject)value["observationContext"]!));
        internal static string Word<T>(T value) => char.ToLowerInvariant(value!.ToString()![0]) + value.ToString()!.Substring(1);
        private static T EnumValue<T>(string value) where T : struct => Enum.TryParse<T>(value, true, out var result) ? result : throw new InvalidDataException("Invalid journal enum.");
        private static long CanonicalNumber(string value)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result < 0 || result.ToString(CultureInfo.InvariantCulture) != value)
                throw new InvalidDataException("Invalid journal numeric identity.");
            return result;
        }
    }
}
