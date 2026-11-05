using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Operations;

namespace SkylinesLink.RuntimeProbe
{
    internal static class JournalBankFormatTests
    {
        private static int checks;
        internal static int MaximumControlPayloadObserved { get; private set; }
        private const string EnvironmentId = "bank-probe";
        private static readonly string Container = new string('c', 32);
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static JournalControl Decode(JObject entry) => ContractCodec.DecodeJournalControl(
            new JObject { ["formatVersion"] = 1, ["entry"] = entry });
        private static JObject Root(string environment = EnvironmentId) => new JObject
        { ["kind"] = "root", ["containerId"] = Container, ["environmentId"] = environment, ["layoutVersion"] = 1 };
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ContractValidationException || error is ArgumentException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected bank format rejection: " + label);
        }

        internal static int Run()
        {
            checks = 0;
            Program.Stage = "bounded journal control codec";
            ControlSlots();
            MaximumControlSlots();
            Program.Stage = "real SLJO bank prefix and recovery selection";
            ActualRecords();
            PreparingAndSuccessors();
            return checks;
        }
        private static void ControlSlots()
        {
            Check(JournalBankFormat.FileBytes == 1101824 && JournalBankFormat.MaximumBusinessBytes == 534060 &&
                JournalBankFormat.BodyBytes >= JournalBankFormat.MaximumBusinessBytes && JournalBankFormat.MaximumControlPayload == 4052,
                "bank/control limits are independent of original business payload and legacy file limits");
            using (var empty = new MemoryStream(new byte[JournalBankFormat.SlotBytes]))
                Check(JournalControlCodec.Read(empty, 0) == null, "only a wholly zero control slot is empty");
            byte[] encoded = JournalControlCodec.Encode(Decode(Root()));
            using (var source = new MemoryStream(encoded, false))
            {
                var value = JournalControlCodec.Read(source, 0)!;
                Check(value.Value.Entry is JournalControlEntryRoot root && root.EnvironmentId == EnvironmentId &&
                    value.Digest == JournalBankFormat.Digest(encoded, encoded.Length), "typed control roundtrip binds the complete padded slot");
                Check(source.Length == 4096, "control encoding cannot grow its slot");
            }
            foreach (int position in new[] { 0, 5, 8, 12, encoded.Length - 1 })
            {
                byte[] bad = (byte[])encoded.Clone(); bad[position] ^= 0x7f;
                using (var source = new MemoryStream(bad, false))
                    Reject(() => JournalControlCodec.Read(source, 0), "bad magic/reserved/length/payload/padding control rejected");
            }
            using (var shortSlot = new MemoryStream(encoded, 0, 4095, false))
                Reject(() => JournalControlCodec.Read(shortSlot, 0), "short control slot cannot borrow following bytes");
            string unicode = string.Concat(Enumerable.Repeat("\U0001F3D9", 64));
            byte[] wide = JournalControlCodec.Encode(Decode(Root(unicode)));
            using (var source = new MemoryStream(wide, false))
                Check(((JournalControlEntryRoot)JournalControlCodec.Read(source, 0)!.Value.Entry).EnvironmentId == unicode,
                    "UTF8 scalar-bound control roundtrip uses its actual byte extent");
        }

        private static void ActualRecords()
        {
            using (var fixture = new Fixture(32))
            {
                var selected = fixture.Read();
                Check(selected.Kind == JournalBankSelectionKind.Active && selected.Current.Records.Count == 129 &&
                    JournalFactsFold.Fold(selected.Current.Records).AllRecordsValid,
                    "full 129 real variable SLJO2 records survive the unique business fold");
                Check(selected.Current.FrameEndBytes.Zip(new long[] { 0 }.Concat(selected.Current.FrameEndBytes), (end, start) => end - start).Distinct().Count() > 2,
                    "bank sample contains different real frame lengths");
                Check(selected.Current.Records.OfType<JournalRecord>().Count(record => record.Entry is NativeTerminated native && native.LoadReturnValue == false) == 32,
                    "bank selection preserves actual negative native return values");
                fixture.Stream.Position = fixture.BodyStart + selected.Current.ThroughBytes + 8192;
                fixture.Stream.WriteByte(1);
                fixture.RejectRead("nonzero after distant zero tail is not hidden by EOF");
            }
            using (var fixture = new Fixture(2))
            {
                fixture.Stream.Position = fixture.BodyStart + fixture.Read().Current.ThroughBytes;
                fixture.Stream.WriteByte((byte)'S');
                fixture.RejectRead("partial business header after confirmed prefix is rejected");
            }
            using (var fixture = new Fixture(2))
            {
                string before = fixture.Hash();
                Reject(() => JournalBankReader.Read(fixture.Stream, "other-environment"), "environment is caller-bound");
                Check(before == fixture.Hash(), "failed recovery selection never repairs or rewrites a file");
            }
        }

        private static void MaximumControlSlots()
        {
            // 最大字符串取 JSON 转义最宽的控制字符；身份/摘要仍取实际合法十六进制。
            var cut = new JObject { ["bank"] = 0, ["generation"] = int.MaxValue - 1, ["storeId"] = new string('a', 32),
                ["descriptorDigest"] = new string('d', 64), ["activationDigest"] = new string('e', 64),
                ["throughSequence"] = 129, ["throughBytes"] = JournalBankFormat.MaximumBusinessBytes, ["prefixDigest"] = new string('f', 64) };
            var values = new[] {
                new JObject { ["kind"] = "root", ["layoutVersion"] = 1 },
                new JObject { ["kind"] = "preparing", ["targetBank"] = 1, ["targetGeneration"] = int.MaxValue,
                    ["targetStoreId"] = new string('b', 32), ["source"] = cut.DeepClone() },
                new JObject { ["kind"] = "generation", ["bank"] = 1, ["generation"] = int.MaxValue, ["storeId"] = new string('b', 32),
                    ["source"] = cut.DeepClone(), ["seedFrameCount"] = 129, ["seedBytes"] = JournalBankFormat.MaximumBusinessBytes,
                    ["seedDigest"] = new string('d', 64), ["retentionApplied"] = true },
                new JObject { ["kind"] = "seal", ["source"] = cut.DeepClone(), ["successorBank"] = 1,
                    ["successorGeneration"] = int.MaxValue, ["successorStoreId"] = new string('b', 32), ["successorDescriptorDigest"] = new string('d', 64) },
                new JObject { ["kind"] = "activation", ["bank"] = 1, ["generation"] = int.MaxValue, ["storeId"] = new string('b', 32),
                    ["descriptorDigest"] = new string('d', 64), ["predecessorSealDigest"] = new string('e', 64) }
            };
            MaximumControlPayloadObserved = 0;
            foreach (var value in values)
            {
                value["containerId"] = Container; value["environmentId"] = new string('\0', 64);
                byte[] bytes = JournalControlCodec.Encode(Decode(value));
                int payload = bytes[8] | bytes[9] << 8 | bytes[10] << 16 | bytes[11] << 24;
                MaximumControlPayloadObserved = Math.Max(MaximumControlPayloadObserved, payload);
                using (var source = new MemoryStream(bytes, false))
                    Check(bytes.Length == 4096 && payload <= JournalBankFormat.MaximumControlPayload && JournalControlCodec.Read(source, 0) != null,
                        "maximum semantic control fields fit one actual slot: " + (string)value["kind"]!);
            }
        }

        private static void PreparingAndSuccessors()
        {
            using (var fixture = new Fixture(1))
            {
                var current = fixture.Read().Current;
                var cut = fixture.Cut(current);
                fixture.WriteControl(JournalBankFormat.BankStart(1), fixture.Preparing(cut));
                fixture.Stream.Position = JournalBankFormat.BankStart(1) + JournalBankFormat.SlotBytes + 19;
                fixture.Stream.WriteByte(0x71);
                fixture.Stream.Position = JournalBankFormat.BankStart(1) + JournalBankFormat.BodyOffset + 8192;
                fixture.Stream.WriteByte(0x72);
                Check(fixture.Read().Current.Descriptor.StoreId == current.Descriptor.StoreId,
                    "valid preparing ignores dirty target only after verifying its complete unsealed source");
                var more = Records(current.Descriptor.StoreId, 2);
                fixture.WriteRecords(0, more);
                Check(fixture.Read().Current.Records.Count == 9, "old preparing cut does not forbid a later valid source tail");
                var bad = (JObject)cut.DeepClone(); bad["activationDigest"] = new string('0', 64);
                fixture.WriteControl(JournalBankFormat.BankStart(1), fixture.Preparing(bad));
                fixture.RejectRead("valid control hash does not excuse a different source activation");
                bad = (JObject)cut.DeepClone(); bad["prefixDigest"] = new string('0', 64);
                fixture.WriteControl(JournalBankFormat.BankStart(1), fixture.Preparing(bad));
                fixture.RejectRead("valid control hash does not excuse a different source prefix");
                fixture.WriteControl(JournalBankFormat.BankStart(1), fixture.Preparing(cut));
                fixture.Stream.Position = JournalBankFormat.BankStart(0) + 2 * JournalBankFormat.SlotBytes;
                fixture.Stream.WriteByte(1);
                fixture.RejectRead("nonzero damaged source seal is not unsealed");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, true);
                string before = fixture.Hash();
                Check(fixture.Read().Current.Index == 0, "unsealed source wins over a complete prepared successor");
                fixture.Seal(current);
                var forward = fixture.Read();
                Check(forward.Kind == JournalBankSelectionKind.ForwardPrepared && forward.Candidate!.Records.Count == 5 &&
                    forward.Candidate.Activation == null, "sealed source selects only its exact prepared successor without writing activation");
                Check(before != fixture.Hash(), "fixture seal is a distinct physical fact");
                before = fixture.Hash(); fixture.Read();
                Check(before == fixture.Hash(), "forward selection does not invent a flush or repair activation");
                fixture.Activate();
                var active = fixture.Read();
                Check(active.Kind == JournalBankSelectionKind.Active && active.Current.Index == 1 && active.Current.Descriptor.RetentionApplied,
                    "matching successor receipt selects the new retained generation");
                fixture.Stream.Position = JournalBankFormat.BankStart(0) + 2 * JournalBankFormat.SlotBytes;
                fixture.Stream.Write(new byte[4096], 0, 4096);
                fixture.RejectRead("two activated unsealed banks cannot elect the highest generation");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, false);
                fixture.Seal(current); fixture.Activate();
                fixture.RejectRead("actually dropped operation requires retained coverage even when local hashes are valid");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, true);
                fixture.WriteRecords(0, Records(current.Descriptor.StoreId, 3));
                Check(fixture.Read().Current.Records.Count == 13, "stale prepared cut is allowed only while source is unsealed");
                fixture.Seal(fixture.Read().Current);
                fixture.RejectRead("sealed forwarding requires exact current cut rather than an earlier valid prefix");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, true);
                fixture.Seal(current);
                fixture.WriteControl(JournalBankFormat.BankStart(1), fixture.Preparing(fixture.Cut(current)));
                fixture.RejectRead("preparing is not the complete successor of a sealed source");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                var shortened = Records(new string('b', 32), 1).Take(2).ToList();
                fixture.Prepare(current, shortened, true);
                fixture.Seal(current);
                fixture.RejectRead("a separately valid accepted-only seed cannot hide the source native and final facts");
            }
            using (var fixture = new Fixture(1))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, false); fixture.Seal(current); fixture.Activate();
                Check(!fixture.Read().Current.Descriptor.RetentionApplied, "a complete copied chain does not falsely require deletion coverage");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                fixture.Prepare(current, 1, true); fixture.Seal(current); fixture.Activate();
                // 协议清复用区前必写Preparing，不能用全零源跳过后继的来源证明。
                var zero = new byte[4096];
                fixture.Stream.Position = JournalBankFormat.BankStart(0);
                for (int remaining = JournalBankFormat.BankBytes; remaining > 0;)
                {
                    int take = Math.Min(zero.Length, remaining);
                    fixture.Stream.Write(zero, 0, take); remaining -= take;
                }
                string before = fixture.Hash();
                fixture.RejectRead("an activated non-genesis bank cannot stand alone without its predecessor proof");
                Check(before == fixture.Hash(), "missing predecessor rejection does not repair or rewrite the source");
            }
            using (var fixture = new Fixture(2))
            {
                var current = fixture.Read().Current;
                var changed = Records(new string('b', 32), 1);
                var id = ((OperationAccepted)changed[1].Entry).Intent.OperationId;
                changed[3] = new JournalRecord(4, EnvironmentId, new NativeTerminated(id, NativeLoadTaskState.Faulted, null, null));
                changed[4] = new JournalRecord(5, EnvironmentId, new OperationFinished(id, "failed", true, SubmissionKnowledge.Submitted,
                    "NATIVE_TERMINATED", new PersistedLoadFacts(NativeLoadTaskState.Faulted, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)));
                Check(JournalFactsFold.Fold(changed).AllRecordsValid, "changed-fact negative fixture is independently semantically valid");
                fixture.Prepare(current, changed, true); fixture.Seal(current);
                fixture.RejectRead("an internally valid new result cannot replace the source known native false result");
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly MemoryStream Stream = new MemoryStream(new byte[JournalBankFormat.FileBytes], 0, JournalBankFormat.FileBytes, true, true);
            internal long BodyStart => JournalBankFormat.BankStart(0) + JournalBankFormat.BodyOffset;
            internal Fixture(int count)
            {
                WriteControl(0, Root());
                var records = Records(new string('a', 32), count);
                long bytes = WriteRecords(0, records);
                var descriptor = Generation(0, 0, new string('a', 32), records.Count, bytes, null, false);
                WriteControl(JournalBankFormat.BankStart(0), descriptor);
                WriteControl(JournalBankFormat.BankStart(0) + JournalBankFormat.SlotBytes, new JObject
                {
                    ["kind"] = "activation", ["containerId"] = Container, ["environmentId"] = EnvironmentId,
                    ["bank"] = 0, ["generation"] = 0, ["storeId"] = new string('a', 32),
                    ["descriptorDigest"] = SlotDigest(JournalBankFormat.BankStart(0)), ["predecessorSealDigest"] = JValue.CreateNull()
                });
            }
            internal JournalBankSelection Read() => JournalBankReader.Read(Stream, EnvironmentId);
            internal void RejectRead(string label)
            { string before = Hash(); Reject(() => Read(), label); Check(Hash() == before, "rejected bank input remains unchanged"); }
            internal string Hash() => JournalBankFormat.Digest(Stream.GetBuffer(), (int)Stream.Length);
            internal void WriteControl(long offset, JObject entry)
            { var bytes = JournalControlCodec.Encode(Decode(entry)); Stream.Position = offset; Stream.Write(bytes, 0, bytes.Length); }
            internal string SlotDigest(long offset)
            { var bytes = new byte[4096]; Stream.Position = offset; JournalBankFormat.ReadExactly(Stream, bytes, 0, bytes.Length); return JournalBankFormat.Digest(bytes, bytes.Length); }
            internal long WriteRecords(int bank, IReadOnlyList<JournalRecord> records)
            {
                long start = JournalBankFormat.BankStart(bank) + JournalBankFormat.BodyOffset; Stream.Position = start;
                foreach (var record in records)
                {
                    byte[] bytes = JournalFrameCodec.Encode(record, out int length);
                    try { Stream.Write(bytes, 0, length); } finally { Array.Clear(bytes, 0, bytes.Length); }
                }
                return Stream.Position - start;
            }
            internal JObject Generation(int bank, int generation, string store, int count, long bytes, JObject? source, bool retired)
            {
                var body = new byte[checked((int)bytes)]; Stream.Position = JournalBankFormat.BankStart(bank) + JournalBankFormat.BodyOffset;
                JournalBankFormat.ReadExactly(Stream, body, 0, body.Length);
                return new JObject { ["kind"] = "generation", ["containerId"] = Container, ["environmentId"] = EnvironmentId,
                    ["bank"] = bank, ["generation"] = generation, ["storeId"] = store, ["source"] = source == null ? JValue.CreateNull() : source,
                    ["seedFrameCount"] = count, ["seedBytes"] = bytes, ["seedDigest"] = JournalBankFormat.Digest(body, body.Length), ["retentionApplied"] = retired };
            }
            internal JObject Cut(JournalBankState state) => new JObject
            {
                ["bank"] = state.Index, ["generation"] = state.Descriptor.Generation, ["storeId"] = state.Descriptor.StoreId,
                ["descriptorDigest"] = state.DescriptorDigest, ["activationDigest"] = state.ActivationDigest,
                ["throughSequence"] = state.Records.Count, ["throughBytes"] = state.ThroughBytes,
                ["prefixDigest"] = HashBody(state.Index, state.ThroughBytes)
            };
            private string HashBody(int bank, long count)
            {
                byte[] bytes = new byte[checked((int)count)]; Stream.Position = JournalBankFormat.BankStart(bank) + JournalBankFormat.BodyOffset;
                JournalBankFormat.ReadExactly(Stream, bytes, 0, bytes.Length); return JournalBankFormat.Digest(bytes, bytes.Length);
            }
            internal JObject Preparing(JObject cut) => new JObject
            {
                ["kind"] = "preparing", ["containerId"] = Container, ["environmentId"] = EnvironmentId,
                ["targetBank"] = 1, ["targetGeneration"] = 1, ["targetStoreId"] = new string('b', 32), ["source"] = cut.DeepClone()
            };
            internal void Prepare(JournalBankState source, int keep, bool retired) => Prepare(source, Records(new string('b', 32), keep), retired);
            internal void Prepare(JournalBankState source, List<JournalRecord> records, bool retired)
            {
                long bytes = WriteRecords(1, records);
                WriteControl(JournalBankFormat.BankStart(1), Generation(1, 1, new string('b', 32), records.Count, bytes, Cut(source), retired));
            }
            internal void Seal(JournalBankState source) => WriteControl(JournalBankFormat.BankStart(0) + 2 * JournalBankFormat.SlotBytes,
                new JObject { ["kind"] = "seal", ["containerId"] = Container, ["environmentId"] = EnvironmentId, ["source"] = Cut(source),
                    ["successorBank"] = 1, ["successorGeneration"] = 1, ["successorStoreId"] = new string('b', 32),
                    ["successorDescriptorDigest"] = SlotDigest(JournalBankFormat.BankStart(1)) });
            internal void Activate() => WriteControl(JournalBankFormat.BankStart(1) + JournalBankFormat.SlotBytes,
                new JObject { ["kind"] = "activation", ["containerId"] = Container, ["environmentId"] = EnvironmentId,
                    ["bank"] = 1, ["generation"] = 1, ["storeId"] = new string('b', 32),
                    ["descriptorDigest"] = SlotDigest(JournalBankFormat.BankStart(1)),
                    ["predecessorSealDigest"] = SlotDigest(JournalBankFormat.BankStart(0) + 2 * JournalBankFormat.SlotBytes) });
            public void Dispose() => Stream.Dispose();
        }

        private static List<JournalRecord> Records(string store, int count)
        {
            var result = new List<JournalRecord> { new JournalRecord(1, EnvironmentId, new StoreOpened(store)) };
            for (int i = 0; i < count; i++)
            {
                var source = new WorldContextSnapshot("bank-instance", 1, "menu", null);
                var target = new LoadTarget("probe", "save-" + i + (i % 2 == 0 ? "城市" : ""), "v1");
                var intent = new OperationIntent("op-" + i, source.InstanceId, new SubmissionKey(source.InstanceId + ".w" + (i / 8 + 1), "r" + i),
                    OperationCommandHash.Compute(source, target), "probe", 1, 1, source, target);
                result.Add(new JournalRecord(result.Count + 1, EnvironmentId, new OperationAccepted(intent)));
                result.Add(new JournalRecord(result.Count + 1, EnvironmentId, new CommitIntended(intent.OperationId, target, new NativeHostIdentity(42, 12345))));
                result.Add(new JournalRecord(result.Count + 1, EnvironmentId, new NativeTerminated(intent.OperationId, NativeLoadTaskState.RanToCompletion, false, null)));
                var facts = new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null);
                result.Add(new JournalRecord(result.Count + 1, EnvironmentId, new OperationFinished(intent.OperationId, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE", facts)));
            }
            return result;
        }
    }
}
