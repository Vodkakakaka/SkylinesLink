using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Infrastructure.Operations
{
    internal enum JournalBankSelectionKind { Active, ForwardPrepared }

    internal sealed class JournalBankState
    {
        internal JournalBankState(int index, PhysicalJournalControlSlot descriptor, PhysicalJournalControlSlot? activation,
            PhysicalJournalControlSlot? seal, List<JournalRecord> records, List<long> ends)
        {
            Index = index; Descriptor = (JournalGenerationControl)descriptor.Value.Entry; DescriptorDigest = descriptor.Digest;
            Activation = activation?.Value.Entry as JournalActivationControl; ActivationDigest = activation?.Digest;
            Seal = seal?.Value.Entry as JournalSealControl; SealDigest = seal?.Digest;
            Records = Array.AsReadOnly(records.ToArray()); FrameEndBytes = Array.AsReadOnly(ends.ToArray());
            ThroughBytes = ends[ends.Count - 1];
        }
        internal int Index { get; }
        internal JournalGenerationControl Descriptor { get; }
        internal string DescriptorDigest { get; }
        internal JournalActivationControl? Activation { get; }
        internal string? ActivationDigest { get; }
        internal JournalSealControl? Seal { get; }
        internal string? SealDigest { get; }
        internal IReadOnlyList<JournalRecord> Records { get; }
        internal IReadOnlyList<long> FrameEndBytes { get; }
        internal long ThroughBytes { get; }
    }

    internal sealed class JournalBankSelection
    {
        internal JournalBankSelection(JournalBankSelectionKind kind, JournalRootControl root,
            JournalBankState current, JournalBankState? candidate = null)
        { Kind = kind; Root = root; Current = current; Candidate = candidate; }
        internal JournalBankSelectionKind Kind { get; }
        internal JournalRootControl Root { get; }
        internal JournalBankState Current { get; }
        internal JournalBankState? Candidate { get; }
    }

    /// <summary>只选择有完整关系的代；不写控制、不 Flush、不宣布持久确认。</summary>
    internal static class JournalBankReader
    {
        internal static JournalBankSelection Read(Stream source, string expectedEnvironment) => Read(source, expectedEnvironment, JournalLayout.Legacy);

        internal static JournalBankSelection ReadMixed(Stream source, string expectedEnvironment) => Read(source, expectedEnvironment, JournalLayout.Mixed);

        internal static JournalBankSelection Read(Stream source, string expectedEnvironment, JournalLayout layout)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            Require(source.CanRead && source.CanSeek && source.Length >= JournalBankFormat.SlotBytes, "container range");
            var rootSlot = PhysicalJournalControlCodec.Read(source, 0, layout);
            var root = rootSlot?.Value.Entry as JournalRootControl;
            Require(root != null && root.EnvironmentId == expectedEnvironment && root.LayoutVersion == layout.LayoutVersion &&
                root.JournalSchemaRevision == layout.JournalSchemaRevision, "root");
            Require(source.Length == layout.FileBytes, "container range");
            Hex(root!.ContainerId, 32);
            var descriptors = new[] {
                PhysicalJournalControlCodec.Read(source, layout.BankStart(0), layout),
                PhysicalJournalControlCodec.Read(source, layout.BankStart(1), layout) };
            for (int i = 0; i < descriptors.Length; i++)
            {
                if (!(descriptors[i]?.Value.Entry is JournalPreparingControl preparing)) continue;
                Require(!(descriptors[1 - i]?.Value.Entry is JournalPreparingControl), "two preparing banks");
                SameRoot(preparing.ContainerId, preparing.EnvironmentId, root);
                SourceShape(preparing.Source, layout);
                Target(preparing.TargetBank, preparing.TargetGeneration, preparing.TargetStoreId, preparing.Source, i);
                // 先核真实源；唯一合法 Preparing 才可豁免目标中获准销毁的残留。
                var current = ReadBank(source, 1 - i, descriptors[1 - i], root, layout);
                Require(current != null && current.Activation != null && current.Seal == null, "preparing source");
                MatchCut(source, preparing.Source, current!, false, layout);
                return new JournalBankSelection(JournalBankSelectionKind.Active, root, current!);
            }

            var a = ReadBank(source, 0, descriptors[0], root, layout);
            var b = ReadBank(source, 1, descriptors[1], root, layout);
            if (a == null || b == null)
            {
                var only = a ?? b;
                Require(only != null && only.Activation != null && only.Seal == null &&
                    only.Descriptor.Generation == 0 && only.Descriptor.Source == null, "missing authority");
                return new JournalBankSelection(JournalBankSelectionKind.Active, root, only!);
            }
            if (a.Activation == null || b.Activation == null)
            {
                Require(a.Activation != null || b.Activation != null, "two prepared banks");
                var current = a.Activation != null ? a : b;
                var candidate = a.Activation == null ? a : b;
                Require(candidate.Descriptor.Source != null, "prepared source");
                MatchCut(source, candidate.Descriptor.Source!, current, current.Seal != null, layout);
                if (current.Seal == null)
                {
                    Retention(current, candidate, layout);
                    return new JournalBankSelection(JournalBankSelectionKind.Active, root, current);
                }
                Link(source, current, candidate, layout);
                return new JournalBankSelection(JournalBankSelectionKind.ForwardPrepared, root, current, candidate);
            }
            Require((a.Seal == null) != (b.Seal == null), "ambiguous activated banks");
            var previous = a.Seal != null ? a : b;
            var active = a.Seal == null ? a : b;
            Link(source, previous, active, layout);
            Require(active.Activation!.PredecessorSealDigest == previous.SealDigest, "activation predecessor");
            return new JournalBankSelection(JournalBankSelectionKind.Active, root, active);
        }

        private static JournalBankState? ReadBank(Stream source, int index, PhysicalJournalControlSlot? descriptorSlot, JournalRootControl root, JournalLayout layout)
        {
            long start = layout.BankStart(index);
            if (descriptorSlot == null)
            {
                Zero(source, start + JournalBankFormat.SlotBytes, layout.BankBytes - JournalBankFormat.SlotBytes);
                return null;
            }
            var descriptor = descriptorSlot.Value.Entry as JournalGenerationControl;
            Require(descriptor != null, "descriptor kind");
            SameRoot(descriptor!.ContainerId, descriptor.EnvironmentId, root);
            Require(descriptor.Bank == index, "descriptor bank");
            Hex(descriptor.StoreId, 32); Hex(descriptor.SeedDigest, 64);
            if (descriptor.Generation == 0)
                Require(index == 0 && descriptor.Source == null && !descriptor.RetentionApplied, "genesis descriptor");
            else
            {
                Require(descriptor.Source != null, "generation source"); SourceShape(descriptor.Source!, layout);
                Target(descriptor.Bank, descriptor.Generation, descriptor.StoreId, descriptor.Source!, index);
            }
            var activationSlot = PhysicalJournalControlCodec.Read(source, start + JournalBankFormat.SlotBytes, layout);
            var sealSlot = PhysicalJournalControlCodec.Read(source, start + 2L * JournalBankFormat.SlotBytes, layout);
            var activation = activationSlot?.Value.Entry as JournalActivationControl;
            var seal = sealSlot?.Value.Entry as JournalSealControl;
            Require(activationSlot == null || activation != null, "activation kind");
            Require(sealSlot == null || seal != null, "seal kind");
            if (activation != null)
            {
                SameRoot(activation.ContainerId, activation.EnvironmentId, root);
                Require(activation.Bank == index && activation.Generation == descriptor.Generation && activation.StoreId == descriptor.StoreId &&
                    activation.DescriptorDigest == descriptorSlot.Digest, "activation binding");
                Hex(activation.StoreId, 32); Hex(activation.DescriptorDigest, 64);
                if (descriptor.Generation == 0) Require(activation.PredecessorSealDigest == null, "genesis activation");
                else { Require(activation.PredecessorSealDigest != null, "activation predecessor missing"); Hex(activation.PredecessorSealDigest!, 64); }
            }
            Require(activation != null || seal == null, "unactivated seal");
            var records = new List<JournalRecord>(layout.MaximumFrames);
            var ends = new List<long>(layout.MaximumFrames);
            long bodyStart = start + JournalBankFormat.BodyOffset;
            ReadRecords(source, bodyStart, records, ends, layout);
            Require(records.Count >= descriptor.SeedFrameCount && records.Count > 0 &&
                ends[checked((int)descriptor.SeedFrameCount - 1)] == descriptor.SeedBytes, "seed extent");
            Require(records[0].Entry is StoreOpened opened && opened.StoreId == descriptor.StoreId, "seed store");
            Require(HashRange(source, bodyStart, descriptor.SeedBytes) == descriptor.SeedDigest, "seed digest");
            var folded = layout.Fold(records);
            Require(folded.AllRecordsValid && folded.EnvironmentId == root.EnvironmentId, "business fold");
            if (activation == null)
                Require(descriptor.Generation != 0 && records.Count == descriptor.SeedFrameCount && ends[ends.Count - 1] == descriptor.SeedBytes, "prepared tail");
            var result = new JournalBankState(index, descriptorSlot, activationSlot, sealSlot, records, ends);
            if (seal != null)
            {
                SameRoot(seal.ContainerId, seal.EnvironmentId, root); SourceShape(seal.Source, layout);
                MatchCut(source, seal.Source, result, true, layout);
                Target(seal.SuccessorBank, seal.SuccessorGeneration, seal.SuccessorStoreId, seal.Source, 1 - index);
                Hex(seal.SuccessorDescriptorDigest, 64);
            }
            return result;
        }

        private static void ReadRecords(Stream source, long bodyStart, List<JournalRecord> records, List<long> ends, JournalLayout layout)
        {
            long offset = 0;
            var header = new byte[12];
            while (offset < layout.BodyBytes)
            {
                long remaining = layout.BodyBytes - offset;
                if (remaining < header.Length) { Zero(source, bodyStart + offset, remaining); break; }
                source.Position = bodyStart + offset; JournalBankFormat.ReadExactly(source, header, 0, header.Length);
                bool zero = true; foreach (byte part in header) if (part != 0) { zero = false; break; }
                if (zero) { Zero(source, bodyStart + offset, remaining); break; }
                uint payload = (uint)header[8] | ((uint)header[9] << 8) | ((uint)header[10] << 16) | ((uint)header[11] << 24);
                Require(payload > 0 && payload <= layout.MaximumRecordPayload && payload + 44L <= remaining &&
                    records.Count < layout.MaximumFrames, "business frame extent");
                source.Position = bodyStart + offset;
                var record = layout.ReadRecord(source);
                Require(record != null, "business frame missing");
                offset += payload + 44L;
                Require(source.Position == bodyStart + offset && offset <= layout.MaximumBusinessBytes, "business position");
                records.Add(record!); ends.Add(offset);
            }
            Require(records.Count > 0, "empty seed");
        }

        private static void Link(Stream source, JournalBankState previous, JournalBankState next, JournalLayout layout)
        {
            var seal = previous.Seal;
            Require(seal != null && next.Descriptor.Source != null, "successor missing");
            MatchCut(source, next.Descriptor.Source!, previous, true, layout);
            Require(seal!.SuccessorBank == next.Index && seal.SuccessorGeneration == next.Descriptor.Generation &&
                seal.SuccessorStoreId == next.Descriptor.StoreId && seal.SuccessorDescriptorDigest == next.DescriptorDigest, "successor binding");
            Retention(previous, next, layout);
        }

        private static void Retention(JournalBankState previous, JournalBankState next, JournalLayout layout)
        {
            Require(!previous.Descriptor.RetentionApplied || next.Descriptor.RetentionApplied, "retention reset");
            var cut = next.Descriptor.Source;
            Require(cut != null, "seed source missing");
            int sourceCount = checked((int)cut!.ThroughSequence);
            int seedCount = checked((int)next.Descriptor.SeedFrameCount);
            var keptOperations = new HashSet<string>(StringComparer.Ordinal);
            var keptTasks = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < seedCount; i++)
            {
                if (next.Records[i].Entry is OperationAccepted accepted) keptOperations.Add(accepted.Intent.OperationId);
                else if (next.Records[i].Entry is TaskCreated created) keptTasks.Add(created.TaskId);
            }

            var sourceOperations = new HashSet<string>(StringComparer.Ordinal);
            var sourceTasks = new HashSet<string>(StringComparer.Ordinal);
            int nextIndex = 1;
            for (int i = 1; i < sourceCount; i++)
            {
                var original = previous.Records[i];
                if (original.Entry is OperationAccepted accepted) sourceOperations.Add(accepted.Intent.OperationId);
                else if (original.Entry is TaskCreated created) sourceTasks.Add(created.TaskId);
                string? operationId = OperationId(original.Entry);
                string? taskId = TaskId(original.Entry);
                Require(operationId != null || taskId != null, "source entry kind");
                if (!(operationId != null ? keptOperations.Contains(operationId) : keptTasks.Contains(taskId!))) continue;
                Require(nextIndex < seedCount && SameEntry(original, next.Records[nextIndex], layout), "seed original chain");
                nextIndex++;
            }
            Require(nextIndex == seedCount && keptOperations.IsSubsetOf(sourceOperations) && keptTasks.IsSubsetOf(sourceTasks), "seed additional entry");
            Require(sourceOperations.Count == keptOperations.Count && sourceTasks.Count == keptTasks.Count || next.Descriptor.RetentionApplied,
                "retirement coverage missing");
        }

        internal static string? OperationId(JournalEntry entry) => entry is OperationAccepted accepted ? accepted.Intent.OperationId :
            entry is CommitIntended commit ? commit.OperationId : entry is NativeTerminated native ? native.OperationId :
            entry is OperationFinished final ? final.OperationId : null;

        internal static string? TaskId(JournalEntry entry) => entry is TaskCreated created ? created.TaskId :
            entry is TaskGoalRevised revised ? revised.TaskId : entry is TaskStopped stopped ? stopped.TaskId : null;

        private static bool SameEntry(JournalRecord a, JournalRecord b, JournalLayout layout)
        {
            if (a.SchemaRevision != b.SchemaRevision) return false;
            // 仅替换header与sequence；原记录版本与业务值走所属codec，不降版或另造字段比较模型。
            byte[] left = layout.EncodeRecord(new JournalRecord(1, a.EnvironmentId, a.Entry, a.SchemaRevision), out int leftLength);
            byte[]? right = null;
            try
            {
                right = layout.EncodeRecord(new JournalRecord(1, b.EnvironmentId, b.Entry, b.SchemaRevision), out int rightLength);
                if (leftLength != rightLength) return false;
                for (int i = 0; i < leftLength; i++) if (left[i] != right[i]) return false;
                return true;
            }
            finally
            {
                Array.Clear(left, 0, left.Length);
                if (right != null) Array.Clear(right, 0, right.Length);
            }
        }

        private static void MatchCut(Stream source, JournalCut cut, JournalBankState actual, bool exact, JournalLayout layout)
        {
            SourceShape(cut, layout);
            Require(actual.Activation != null && cut.Bank == actual.Index && cut.Generation == actual.Descriptor.Generation &&
                cut.StoreId == actual.Descriptor.StoreId && cut.DescriptorDigest == actual.DescriptorDigest &&
                cut.ActivationDigest == actual.ActivationDigest && cut.ThroughSequence <= actual.Records.Count, "source binding");
            Require(actual.FrameEndBytes[checked((int)cut.ThroughSequence - 1)] == cut.ThroughBytes &&
                (!exact || cut.ThroughSequence == actual.Records.Count && cut.ThroughBytes == actual.ThroughBytes), "source cut");
            Require(HashRange(source, layout.BankStart(actual.Index) + JournalBankFormat.BodyOffset, cut.ThroughBytes) == cut.PrefixDigest,
                "source prefix digest");
        }

        private static void SourceShape(JournalCut cut, JournalLayout layout)
        {
            Require(cut != null && cut.Bank >= 0 && cut.Bank <= 1 && cut.Generation >= 0 && cut.Generation <= int.MaxValue &&
                cut.ThroughSequence >= 1 && cut.ThroughSequence <= layout.MaximumFrames && cut.ThroughBytes >= 44 &&
                cut.ThroughBytes <= layout.MaximumBusinessBytes, "source shape");
            Hex(cut!.StoreId, 32); Hex(cut.DescriptorDigest, 64); Hex(cut.ActivationDigest, 64); Hex(cut.PrefixDigest, 64);
        }

        private static void Target(long bank, long generation, string store, JournalCut source, int physicalBank)
        {
            Hex(store, 32);
            Require(bank == physicalBank && bank != source.Bank && source.Generation < int.MaxValue && generation == source.Generation + 1 &&
                store != source.StoreId, "target identity");
        }

        private static void SameRoot(string container, string environment, JournalRootControl root)
        { Hex(container, 32); Require(container == root.ContainerId && environment == root.EnvironmentId, "container binding"); }

        private static void Hex(string value, int length)
        {
            Require(value != null && value.Length == length, "hex length");
            foreach (char part in value!) Require(part >= '0' && part <= '9' || part >= 'a' && part <= 'f', "hex character");
        }

        internal static string HashRange(Stream source, long offset, long count)
        {
            Require(count >= 0 && offset >= 0 && count <= source.Length - offset, "hash range");
            var buffer = new byte[4096];
            try
            {
                using (var sha = SHA256.Create())
                {
                    source.Position = offset;
                    while (count > 0)
                    {
                        int take = (int)Math.Min(buffer.Length, count);
                        JournalBankFormat.ReadExactly(source, buffer, 0, take);
                        sha.TransformBlock(buffer, 0, take, buffer, 0); count -= take;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var text = new StringBuilder(64);
                    foreach (byte part in sha.Hash!) text.Append(part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    return text.ToString();
                }
            }
            finally { Array.Clear(buffer, 0, buffer.Length); }
        }

        private static void Zero(Stream source, long offset, long count)
        {
            Require(count >= 0 && offset >= 0 && count <= source.Length - offset, "zero range");
            var buffer = new byte[4096];
            try
            {
                source.Position = offset;
                while (count > 0)
                {
                    int take = (int)Math.Min(buffer.Length, count);
                    JournalBankFormat.ReadExactly(source, buffer, 0, take);
                    for (int i = 0; i < take; i++) Require(buffer[i] == 0, "nonzero tail");
                    count -= take;
                }
            }
            finally { Array.Clear(buffer, 0, buffer.Length); }
        }

        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidDataException("Journal bank " + detail + " invalid."); }
    }
}
