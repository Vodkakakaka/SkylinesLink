using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using ContractValidationException = SkylinesLink.Contracts.ContractValidationException;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>显式混合物理组件的真实文件验证；不激活公共Task准入或模拟游戏。</summary>
    internal static class MixedJournalBankTests
    {
        private const string EnvironmentId = "mixed-bank-probe";
        private static readonly JournalLayout Layout = JournalLayout.Mixed;
        private static readonly TaskActor Actor = new TaskActor("planner", new string('a', 32), 1, 1);
        private static readonly WorldContextSnapshot Ready = new WorldContextSnapshot("mixed-bank-instance", 3, "ready", "city-a");
        private static int checks;
        internal static int MaximumControlPayloadObserved { get; private set; }
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ContractValidationException || error is ArgumentException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected mixed bank rejection: " + label);
        }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            MaximumControlSlots();
            Program.Stage = "explicit mixed physical layout and real task/operation rotation";
            RoundTripsAndReuse();
            TaskOnlyRetirement();
            Program.Stage = "mixed bytes and rows are independently bounded";
            ByteAndRowBounds();
            Program.Stage = "mixed complete-chain proof rejects locally valid altered seeds";
            CompleteChainRejections();
            Program.Stage = "explicit mixed profile and old public owner refusal";
            await FormatRefusalAsync();
            Program.Stage = "mixed sealed recovery and partial activation";
            RecoveryBoundaries();
            return checks;
        }

        private static void MaximumControlSlots()
        {
            Program.Stage = "actual control2 fixed slots at the generated schema string bounds";
            var cases = JArray.Parse(File.ReadAllText(Path.Combine("contracts", "fixtures", "journal-control-v2-cases.json")));
            int kinds = 0;
            foreach (JToken sample in cases)
            {
                if (!((string)sample["name"]!).StartsWith("control2/maximum-escaped-", StringComparison.Ordinal)) continue;
                var value = SkylinesLink.Contracts.ContractCodec.DecodeJournalControlV2(JToken.Parse((string)sample["json"]!));
                byte[] slot = PhysicalJournalControlCodec.EncodeToken(SkylinesLink.Contracts.ContractCodec.EncodeJournalControlV2(value), Layout);
                try
                {
                    int length = PayloadLength(slot);
                    MaximumControlPayloadObserved = Math.Max(MaximumControlPayloadObserved, length);
                    using (var source = new MemoryStream(slot, false))
                        Check(length <= 4052 && slot.Length == 4096 && PhysicalJournalControlCodec.Read(source, 0, Layout) != null,
                            (string)sample["name"]! + " round-trips through the actual bounded binary slot");
                    kinds++;
                }
                finally { Array.Clear(slot, 0, slot.Length); }
            }
            Check(kinds == 5, "all five control2 kinds verify their actual binary payload bound");
        }

        private static TaskStatedGoal Goal(string title = "Preserve the stated city intent") =>
            new TaskStatedGoal(title, new string('建', 2048), new[] { new string('界', 256) }, new[] { "Keep the exact accepted wording" });
        private static TaskStatedGoal MaximumGoal() => new TaskStatedGoal(new string('\u0001', 128), new string('\u0001', 2048),
            Enumerable.Repeat(new string('\u0001', 256), 16).ToArray(), Enumerable.Repeat(new string('\u0001', 256), 16).ToArray());
        private static TaskDecision Decision(string key, WorldContextSnapshot source, string hash) =>
            new TaskDecision(new SubmissionKey("mixed-bank-context", key), Actor, source, hash);
        private static TaskCreated Create(string id, TaskStatedGoal? goal = null)
        {
            goal = goal ?? Goal();
            return new TaskCreated(id, Decision(id + "-create", Ready, TaskCommandHash.Create(Ready, goal)), goal);
        }
        private static TaskGoalRevised Revise(string id, long prior, TaskStatedGoal? goal = null)
        {
            goal = goal ?? Goal("Revised accepted intent");
            return new TaskGoalRevised(id, prior, prior + 1, 1,
                Decision(id + "-revise-" + prior, Ready, TaskCommandHash.Revise(id, prior, 1, Ready, goal)), goal);
        }
        private static TaskStopped Stop(string id) => new TaskStopped(id, 1, 2,
            Decision(id + "-stop", new WorldContextSnapshot("later-menu", 9, "menu", null), TaskCommandHash.Stop(id, 1)));
        private static JournalEntry[] Operation(string id, bool recorded)
        {
            var target = new LoadTarget("probe", "slot-" + id, "v1");
            var intent = new OperationIntent(id, Ready.InstanceId, new SubmissionKey("mixed-bank-context", id + "-load"),
                OperationCommandHash.Compute(Ready, target), Actor.ProfileId, 1, 1, Ready, target, recorded ? Actor.ProfileInstanceId : null);
            return new JournalEntry[] {
                new OperationAccepted(intent), new CommitIntended(id, target, new NativeHostIdentity(101, 123456)),
                new NativeTerminated(id, NativeLoadTaskState.RanToCompletion, false, null),
                new OperationFinished(id, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE",
                    new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)) };
        }
        private static void Seed(Fixture f, bool legacyOperation = true)
        {
            f.Append(3, Create("kept-task"));
            var legacy = Operation("kept-op", false);
            int schema = legacyOperation ? 2 : 3;
            f.Append(schema, legacy[0]);
            f.Append(3, Create("dropped-task"));
            f.Append(schema, legacy[1]);
            f.Append(3, Revise("kept-task", 1));
            f.Append(schema, legacy[2]);
            f.Append(3, Stop("kept-task"));
            f.Append(schema, legacy[3]);
            f.Append(3, Stop("dropped-task"));
            f.Append(3, Operation("dropped-op", true));
            f.Reopen();
        }

        private static void RoundTripsAndReuse()
        {
            using (var f = new Fixture())
            {
                Check(f.Stream.Length == 4222976 && Layout.BodyBytes == 2097152 && Layout.MaximumFrames == 129 &&
                    f.Bank.Current.Records.Single().SchemaRevision == 3, "mixed genesis uses its own fixed layout and explicit schema3 header");
                Seed(f);
                var originalTask = JournalFactsFold.FoldMixed(f.Bank.Current.Records).Tasks.Single(task => task.TaskId == "kept-task");
                Check(f.Bank.Current.FrameEndBytes.Zip(new long[] { 0 }.Concat(f.Bank.Current.FrameEndBytes), (a, b) => a - b).Any(length => length > 4140),
                    "real accepted Task wording occupies a metadata frame larger than an ordinary operation frame");
                var result = f.Rotate(new[] { "kept-op" }, new[] { "kept-task" });
                VerifyRetained(result.Prefix.Records, originalTask);
                Check(result.Outcome == JournalRotationOutcome.Installed && result.RetentionApplied && f.Bank.Current.Index == 1 &&
                    f.Bank.Current.Descriptor.Generation == 1, "first real handoff installs the full retained Task and operation chains");
                f.Append(3, Create("later-task"), Stop("later-task"));
                f.Reopen();
                var second = f.Rotate(new[] { "kept-op" }, new[] { "kept-task" });
                VerifyRetained(second.Prefix.Records, originalTask);
                Check(second.RetentionApplied && f.Bank.Current.Index == 0 && f.Bank.Current.Descriptor.Generation == 2 &&
                    f.Stream.Length == Layout.FileBytes, "second handoff reuses the old bank without growing or resetting coverage");
                f.Reopen();
                Check(f.Bank.Current.Records.Count == 8 && f.Bank.Current.Descriptor.RetentionApplied,
                    "actual close/open/Flush retains the second generation and complete mixed prefix");
            }
        }
        private static void VerifyRetained(IReadOnlyList<JournalRecord> records, JournalTaskFacts original)
        {
            var fold = JournalFactsFold.FoldMixed(records); var task = fold.Tasks.Single(); var op = fold.Operations.Single();
            Check(fold.AllRecordsValid && task.TaskId == "kept-task" && task.GoalRevision == 2 && task.ControlVersion == 2 && task.IsStopped &&
                task.GoalHistory.Count == 2 && task.Decisions.Count == 3 && task.CurrentGoal.RequestText == original.CurrentGoal.RequestText &&
                task.Owner.ProfileInstanceId == Actor.ProfileInstanceId, "rotation keeps every Task decision, revision, owner and exact goal text");
            Check(op.Intent.OperationId == "kept-op" && op.Intent.ProfileInstanceId == null && op.Native!.LoadReturnValue == false &&
                op.Final!.Load.LoadReturnValue == false && op.Final.MayHaveApplied, "rotation preserves unrecorded legacy actor and actual native/final false facts");
            Check(records.Where(record => record.Entry is OperationAccepted || record.Entry is CommitIntended || record.Entry is NativeTerminated ||
                    record.Entry is OperationFinished).All(record => record.SchemaRevision == 2) && records[0].SchemaRevision == 3,
                "mixed seed replaces only the header and sequence, retaining every old operation frame marker");
        }

        private static void TaskOnlyRetirement()
        {
            using (var f = new Fixture())
            {
                f.Append(3, Create("first"), Stop("first"), Create("second"), Stop("second")); f.Reopen();
                var first = f.Rotate(Array.Empty<string>(), new[] { "first" });
                Check(first.RetentionApplied && first.Prefix.Records.Count == 3 &&
                    JournalFactsFold.FoldMixed(first.Prefix.Records).Tasks.Single().TaskId == "first",
                    "dropping only a Task chain sets retained coverage without requiring an operation deletion");
                var empty = f.Rotate(Array.Empty<string>(), Array.Empty<string>());
                Check(empty.RetentionApplied && empty.Prefix.Records.Count == 1 && f.Bank.Current.Descriptor.Generation == 2,
                    "last Task retirement leaves a valid header-only generation with persistent coverage");
                f.Reopen();
                string before = f.Hash();
                Reject(() => f.Rotate(Array.Empty<string>(), new[] { "unknown-task" }), "unknown retained Task cannot be invented by a physical rotation");
                Check(before == f.Hash(), "unknown retention identity performs no physical writes");
            }
            using (var f = new Fixture())
            {
                f.Append(3, Create("shared-id"), Stop("shared-id")); f.Append(3, Operation("shared-id", true));
                f.Reopen(); var retained = f.Rotate(new[] { "shared-id" }, Array.Empty<string>());
                var facts = JournalFactsFold.FoldMixed(retained.Prefix.Records);
                Check(facts.AllRecordsValid && facts.Tasks.Count == 0 && facts.Operations.Single().Intent.OperationId == "shared-id" && retained.RetentionApplied,
                    "Task and operation IDs have separate retention namespaces even when their strings are identical");
            }
        }

        private static void ByteAndRowBounds()
        {
            using (var f = new Fixture())
            {
                var goal = MaximumGoal(); f.Append(3, Create("dense", goal));
                long revision = 1; JournalRecord next;
                while (true)
                {
                    next = new JournalRecord(f.Count + 1, EnvironmentId, Revise("dense", revision, goal), 3);
                    byte[] frame = JournalFrameCodec.EncodeMixed(next, out int size);
                    bool fits = f.BusinessBytes + size <= Layout.BodyBytes;
                    Array.Clear(frame, 0, frame.Length);
                    if (!fits) break;
                    f.Append(3, next.Entry); revision++;
                }
                f.Reopen();
                Check(f.Bank.Current.Records.Count < 129 && f.Bank.Current.ThroughBytes > JournalBankFormat.MaximumBusinessBytes &&
                    JournalFactsFold.FoldMixed(f.Bank.Current.Records).AllRecordsValid,
                    "legal dense metadata reaches its independent byte budget before the 129 row budget");
                byte[] crossing = JournalFrameCodec.EncodeMixed(next, out int length);
                int remaining = checked((int)(Layout.BodyBytes - f.BusinessBytes));
                Check(remaining >= 12 && remaining < length, "dense fixture leaves a positive partial-frame space inside its own body");
                f.Stream.Position = f.BodyStart + f.BusinessBytes; f.Stream.Write(crossing, 0, remaining); f.Stream.Flush(true);
                Array.Clear(crossing, 0, crossing.Length);
                Reject(() => f.Read(), "declared metadata frame cannot borrow bytes from the following bank");
                Check(f.Stream.Length == Layout.FileBytes, "byte-bound rejection does not grow the fixed physical container");
            }
            using (var f = new Fixture())
            {
                var small = new TaskStatedGoal("small", "request", Array.Empty<string>(), Array.Empty<string>());
                f.Append(3, Create("rows", small));
                for (int revision = 1; f.Count < 129; revision++) f.Append(3, Revise("rows", revision, small));
                f.Reopen();
                Check(f.Count == 129 && f.BusinessBytes < Layout.BodyBytes / 2 && JournalFactsFold.FoldMixed(f.Bank.Current.Records).AllRecordsValid,
                    "129 small mixed rows remain valid despite substantial unused body bytes");
                f.Append(3, Revise("rows", 128, small));
                Reject(() => f.Read(), "free physical bytes cannot permit a 130th mixed business row");
            }
        }

        private static void CompleteChainRejections()
        {
            foreach (bool downgradeMarker in new[] { false, true })
            using (var f = new Fixture())
            {
                Seed(f, !downgradeMarker); f.Rotate(new[] { "kept-op" }, new[] { "kept-task" });
                var altered = f.Bank.Current.Records.Where(record => downgradeMarker || !(record.Entry is TaskGoalRevised))
                    .Select(record => new JournalRecord(1, EnvironmentId, record.Entry,
                        downgradeMarker && record.Entry is NativeTerminated ? 2 : record.SchemaRevision)).ToList();
                // A revision3 native stage is also legal as revision2, but changing its marker is not an original copy.
                RewriteSuccessor(f, altered);
                Check(JournalFactsFold.FoldMixed(altered).AllRecordsValid,
                    "altered seed remains locally Fold-valid, so rejection requires the original-chain proof");
                string before = f.Hash();
                Reject(() => f.Read(), downgradeMarker ? "valid successor hashes cannot relabel a retained source frame revision" :
                    "valid successor hashes cannot omit an already accepted Task goal revision");
                Check(before == f.Hash(), "original-chain rejection never rewrites a forged successor");
            }
            using (var f = new Fixture())
            {
                f.Append(3, Create("kept"), Stop("kept"), Create("dropped"), Stop("dropped")); f.Reopen();
                f.Rotate(Array.Empty<string>(), new[] { "kept" });
                RewriteSuccessor(f, f.Bank.Current.Records.ToList(), retentionApplied: false);
                Reject(() => f.Read(), "valid local hashes cannot hide Task-only deletion behind false retention coverage");
            }
            using (var f = new Fixture())
            {
                f.Append(3, Create("key-owner"));
                var op = Operation("key-collision", true); var accepted = (OperationAccepted)op[0]; var original = accepted.Intent;
                var reused = new OperationIntent(original.OperationId, original.OriginInstanceId,
                    new SubmissionKey("mixed-bank-context", "key-owner-create"), original.CommandHash, original.ProfileId,
                    original.AuthorityEpoch, original.GrantEpoch, original.Source, original.Target, original.ProfileInstanceId);
                f.Append(3, new OperationAccepted(reused));
                Reject(() => f.Read(), "physical mixed recovery keeps one global request-key domain for Task and operation decisions");
            }
            using (var f = new Fixture())
            {
                Seed(f); f.Rotate(new[] { "kept-op" }, new[] { "kept-task" });
                int previous = 1 - f.Bank.Current.Index; f.Zero(Layout.BankStart(previous), Layout.BankBytes); f.Stream.Flush(true);
                string before = f.Hash();
                Reject(() => f.Read(), "generation one cannot become authoritative after its entire predecessor bank disappears");
                Check(before == f.Hash(), "missing-predecessor rejection does not invent genesis or repair controls");
            }
        }

        private static async Task FormatRefusalAsync()
        {
            using (var f = new Fixture())
            {
                f.Append(3, Create("not-legacy")); f.Reopen(); string before = f.Hash();
                Reject(() => JournalBankReader.Read(f.Stream, EnvironmentId), "default bank reader rejects an explicit mixed layout");
                Reject(() => JournalBankFile.Open(f.Stream, f.Lease, EnvironmentId, f.Durability), "default bank file owner cannot opt into mixed by magic alone");
                Check(before == f.Hash(), "default physical rejection preserves every original byte");
                f.CloseStream();
                var old = OperationJournal.OpenForAuthority(f.Boundary, f.Lease, EnvironmentId, false);
                try { Check(!old.IsHealthy && !old.Recovery.Complete && old.Recovery.Incompatible && old.Recovery.Records.Count == 0,
                    "current public OperationJournal refuses mixed as incompatible instead of enabling its old writer"); }
                finally { await old.StopAsync(); }
                Check(before == f.Hash(), "public owner rejection preserves the actual mixed file without migration or truncation");
            }
            using (var f = new Fixture())
            {
                var document = ReadControl(f.Stream, Layout.BankStart(0)); document["formatVersion"] = 1;
                byte[] oldSlot = PhysicalJournalControlCodec.EncodeToken(document, JournalLayout.Legacy);
                f.Stream.Position = Layout.BankStart(0); f.Stream.Write(oldSlot, 0, oldSlot.Length); f.Stream.Flush(true);
                Reject(() => f.Read(), "mixed root cannot accept an individually valid legacy descriptor slot");
            }
            using (var f = new Fixture())
            {
                var slot = ReadSlot(f.Stream, 0); int length = PayloadLength(slot);
                var text = Encoding.UTF8.GetString(slot, 12, length);
                string changed = text.Replace("\"journalSchemaRevision\":3", "\"journalSchemaRevision\":2");
                Check(changed != text && Encoding.UTF8.GetByteCount(changed) == length, "wrong-family fixture changes only one schema digit");
                Buffer.BlockCopy(Encoding.UTF8.GetBytes(changed), 0, slot, 12, length);
                using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(slot, 0, 12 + length), 0, slot, 12 + length, 32);
                f.Stream.Position = 0; f.Stream.Write(slot, 0, slot.Length); f.Stream.Flush(true);
                Reject(() => f.Read(), "a locally hashed root cannot claim operation2 provenance for the mixed body");
            }
        }

        private static void RecoveryBoundaries()
        {
            using (var f = new Fixture())
            {
                Seed(f); string oldStore = f.Bank.Current.Descriptor.StoreId; int oldCount = f.Count;
                f.Durability.FailAfter(4); // Preparing, clear, Prepared, then the actual source seal Flush.
                Reject(() => f.Rotate(new[] { "kept-op" }, new[] { "kept-task" }), "lost actual seal Flush acknowledgement does not report installation");
                f.OpenWithoutConfirmation(); string before = f.Hash(); int flushes = f.Durability.Calls;
                var pending = f.Read();
                Check(pending.Kind == JournalBankSelectionKind.ForwardPrepared && f.Bank.Current.Descriptor.StoreId == oldStore &&
                    f.Bank.Current.Records.Count == oldCount && before == f.Hash() && f.Durability.Calls == flushes,
                    "mixed forward Open is read-only and preserves all sealed source facts until actual recovery confirmation");
                Check(f.Bank.ConfirmRecovery() && f.Durability.Calls == flushes + 2 && f.Bank.Current.Records.Count == 8,
                    "mixed forward recovery uses real confirmation and activation Flushes before installing the retained Task chain");
                int confirmed = f.Durability.Calls;
                Check(f.Bank.ConfirmRecovery() && f.Durability.Calls == confirmed, "confirmed recovery does not manufacture another receipt or Flush");
            }
            using (var f = new Fixture(partialActivation: true))
            {
                Seed(f); ((ActivationCutStream)f.Stream).Armed = true;
                Reject(() => f.Rotate(new[] { "kept-op" }, new[] { "kept-task" }), "partial mixed activation is an unknown IO result");
                Check(((ActivationCutStream)f.Stream).Written == 71, "partial activation fixture wrote a real receipt prefix");
                string before = f.Hash();
                Reject(() => f.Read(), "half activation cannot masquerade as zero receipt or a fully committed successor");
                Check(before == f.Hash(), "half-receipt rejection preserves the damaged bytes for explicit diagnosis");
            }
        }

        private static void RewriteSuccessor(Fixture f, List<JournalRecord> records, bool? retentionApplied = null)
        {
            for (int i = 0; i < records.Count; i++) records[i] = new JournalRecord(i + 1, EnvironmentId, records[i].Entry, records[i].SchemaRevision);
            int active = f.Bank.Current.Index, previous = 1 - active; long body = Layout.BankStart(active) + JournalBankFormat.BodyOffset;
            f.Zero(body, Layout.BodyBytes);
            using (var encoded = new MemoryStream())
            {
                foreach (var record in records)
                {
                    byte[] frame = JournalFrameCodec.EncodeMixed(record, out int length);
                    encoded.Write(frame, 0, length); Array.Clear(frame, 0, frame.Length);
                }
                byte[] bytes = encoded.ToArray(); f.Stream.Position = body; f.Stream.Write(bytes, 0, bytes.Length);
                var d = ReadControl(f.Stream, Layout.BankStart(active)); var entry = d["entry"]!;
                entry["seedFrameCount"] = records.Count; entry["seedBytes"] = bytes.Length; entry["seedDigest"] = JournalBankFormat.Digest(bytes, bytes.Length);
                if (retentionApplied.HasValue) entry["retentionApplied"] = retentionApplied.Value;
                WriteControl(f.Stream, Layout.BankStart(active), d);
                string descriptor = SlotDigest(f.Stream, Layout.BankStart(active));
                long sealOffset = Layout.BankStart(previous) + 2L * JournalBankFormat.SlotBytes;
                var seal = ReadControl(f.Stream, sealOffset); seal["entry"]!["successorDescriptorDigest"] = descriptor; WriteControl(f.Stream, sealOffset, seal);
                long receiptOffset = Layout.BankStart(active) + JournalBankFormat.SlotBytes;
                var receipt = ReadControl(f.Stream, receiptOffset); receipt["entry"]!["descriptorDigest"] = descriptor;
                receipt["entry"]!["predecessorSealDigest"] = SlotDigest(f.Stream, sealOffset); WriteControl(f.Stream, receiptOffset, receipt);
                Array.Clear(bytes, 0, bytes.Length); f.Stream.Flush(true);
            }
        }
        private static int PayloadLength(byte[] slot) => slot[8] | slot[9] << 8 | slot[10] << 16 | slot[11] << 24;
        private static byte[] ReadSlot(Stream stream, long offset)
        { var bytes = new byte[4096]; stream.Position = offset; JournalBankFormat.ReadExactly(stream, bytes, 0, bytes.Length); return bytes; }
        private static JObject ReadControl(Stream stream, long offset)
        { byte[] slot = ReadSlot(stream, offset); return JObject.Parse(Encoding.UTF8.GetString(slot, 12, PayloadLength(slot))); }
        private static void WriteControl(Stream stream, long offset, JObject document)
        { byte[] bytes = PhysicalJournalControlCodec.EncodeToken(document, Layout); stream.Position = offset; stream.Write(bytes, 0, bytes.Length); }
        private static string SlotDigest(Stream stream, long offset)
        { byte[] bytes = ReadSlot(stream, offset); return JournalBankFormat.Digest(bytes, bytes.Length); }

        private sealed class CountingDurability : IJournalDurability
        {
            internal int Calls { get; private set; }
            private int failAt;
            internal void FailAfter(int calls) { failAt = Calls + calls; }
            public void Flush(FileStream file)
            { file.Flush(true); Calls++; if (Calls == failAt) { failAt = 0; throw new IOException("Synthetic acknowledgement loss after actual mixed Flush."); } }
        }
        private sealed class ActivationCutStream : FileStream
        {
            internal bool Armed; internal int Written { get; private set; }
            internal ActivationCutStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read) { }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count == 4096 && buffer[offset] == 'S' && buffer[offset + 3] == 'C' && buffer[offset + 4] == 2)
                {
                    var slot = new byte[count]; Buffer.BlockCopy(buffer, offset, slot, 0, count);
                    var document = JObject.Parse(Encoding.UTF8.GetString(slot, 12, PayloadLength(slot)));
                    if ((string)document["entry"]!["kind"]! == "activation" && (long)document["entry"]!["generation"]! > 0)
                    { Armed = false; base.Write(buffer, offset, 71); Written = 71; throw new IOException("Synthetic partial mixed activation."); }
                }
                base.Write(buffer, offset, count);
            }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "mixed-journal-bank-" + Guid.NewGuid().ToString("N"));
            private readonly bool partialActivation;
            internal readonly IPrivateStorageBoundary Boundary;
            internal readonly EnvironmentLease Lease;
            internal readonly CountingDurability Durability = new CountingDurability();
            internal FileStream Stream { get; private set; }
            internal JournalBankFile Bank { get; private set; }
            internal string PathName => Path.Combine(Boundary.DirectoryPath, "operations.journal");
            internal int Count { get; private set; }
            internal long BusinessBytes { get; private set; }
            internal long BodyStart => Layout.BankStart(Bank.Current.Index) + JournalBankFormat.BodyOffset;
            internal Fixture(bool partialActivation = false)
            {
                this.partialActivation = partialActivation; Directory.CreateDirectory(root);
                Boundary = new PrivateConnectionStorage(Path.Combine(root, "connection")); Lease = new EnvironmentLeaseProvider(Boundary).TryAcquireAuthority()!;
                using (Boundary.CreatePrivateFile(PathName)) { }
                Stream = partialActivation ? new ActivationCutStream(PathName) : new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                Bank = JournalBankFile.Create(Stream, Lease, EnvironmentId, Durability, Layout); SyncPosition();
            }
            internal void Append(int schema, params JournalEntry[] entries)
            {
                foreach (var entry in entries)
                {
                    byte[] frame = JournalFrameCodec.EncodeMixed(new JournalRecord(Count + 1, EnvironmentId, entry, schema), out int length);
                    try { Stream.Position = Bank.AppendOffset(BusinessBytes); Stream.Write(frame, 0, length); BusinessBytes += length; Count++; }
                    finally { Array.Clear(frame, 0, frame.Length); }
                }
                Stream.Flush(true);
            }
            internal JournalBankSelection Read() => JournalBankReader.Read(Stream, EnvironmentId, Layout);
            internal void CloseStream() => Stream.Dispose();
            internal void OpenWithoutConfirmation()
            {
                Stream.Dispose(); Boundary.ValidateFile(PathName, false);
                Stream = partialActivation ? new ActivationCutStream(PathName) : new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                Bank = JournalBankFile.Open(Stream, Lease, EnvironmentId, Durability, Layout); SyncPosition();
            }
            internal void Reopen()
            { OpenWithoutConfirmation(); if (!Bank.ConfirmRecovery()) throw new IOException("Mixed fixture recovery failed.", Bank.RecoveryFailure); SyncPosition(); }
            internal JournalRotationResult Rotate(IReadOnlyList<string> operations, IReadOnlyList<string> tasks)
            {
                var result = Bank.Rotate(new JournalRotationPlan(Bank.Current.Descriptor.StoreId, Count, operations, tasks), () => false, () => true);
                SyncPosition(); return result;
            }
            private void SyncPosition() { Count = Bank.Current.Records.Count; BusinessBytes = Bank.Current.ThroughBytes; }
            internal string Hash()
            {
                if (!Stream.CanRead) { byte[] bytes = File.ReadAllBytes(PathName); return JournalBankFormat.Digest(bytes, bytes.Length); }
                long position = Stream.Position;
                try
                {
                    Stream.Position = 0;
                    using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Stream)).Replace("-", "").ToLowerInvariant();
                }
                finally { Stream.Position = position; }
            }
            internal void Zero(long offset, int length)
            { var zero = new byte[16384]; Stream.Position = offset; while (length > 0) { int take = Math.Min(zero.Length, length); Stream.Write(zero, 0, take); length -= take; } }
            public void Dispose()
            {
                Stream.Dispose(); Lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(root).StartsWith("mixed-journal-bank-", StringComparison.Ordinal)) throw new IOException("Mixed fixture containment invalid.");
                foreach (string directory in new[] { Boundary.DirectoryPath, root })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mixed fixture refuses directory links.");
                foreach (string path in Directory.GetFiles(Boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mixed fixture refuses file links."); File.Delete(path); }
                Directory.Delete(Boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
