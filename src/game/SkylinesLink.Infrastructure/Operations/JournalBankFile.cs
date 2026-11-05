using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;

namespace SkylinesLink.Infrastructure.Operations
{
    /// <summary>原 journal 单 writer 拥有的双区 IO；不拥有线程、文件关闭或环境 lease 的释放权。</summary>
    internal sealed class JournalBankFile
    {
        private readonly FileStream file;
        private readonly EnvironmentLease lease;
        private readonly string environment;
        private readonly IJournalDurability durability;
        private readonly JournalLayout layout;
        private JournalBankSelection? pendingRecovery;
        private bool confirmationAttempted, confirmed, poisoned;
        private JournalBankFile(FileStream file, EnvironmentLease lease, string environment,
            IJournalDurability durability, JournalBankSelection opened, bool confirmed, JournalLayout layout)
        {
            this.file = file; this.lease = lease; this.environment = environment; this.durability = durability;
            this.layout = layout;
            pendingRecovery = confirmed ? null : opened; Current = opened.Current; this.confirmed = confirmed; confirmationAttempted = confirmed;
        }
        internal JournalBankState Current { get; private set; }
        internal Exception? RecoveryFailure { get; private set; }

        internal static JournalBankFile Open(FileStream file, EnvironmentLease lease, string environment, IJournalDurability durability)
            => Open(file, lease, environment, durability, JournalLayout.Legacy);

        internal static JournalBankFile Open(FileStream file, EnvironmentLease lease, string environment, IJournalDurability durability,
            JournalLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            var selection = ReadOwned(lease, () => JournalBankReader.Read(file, environment, layout));
            return new JournalBankFile(file, lease, environment, durability, selection, false, layout);
        }

        // 调用方必须已经用 CreateNew 和新环境证明取得文件；缺文件不是初始化凭据。
        internal static JournalBankFile Create(FileStream file, EnvironmentLease lease, string environment, IJournalDurability durability)
            => Create(file, lease, environment, durability, JournalLayout.Legacy);

        internal static JournalBankFile Create(FileStream file, EnvironmentLease lease, string environment, IJournalDurability durability,
            JournalLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            RequireHeld(lease);
            Require(file.CanRead && file.CanWrite && file.CanSeek && file.Length == 0, "new file");
            string container = Guid.NewGuid().ToString("N"), store = Guid.NewGuid().ToString("N");
            byte[] frame = layout.EncodeRecord(new JournalRecord(1, environment, new StoreOpened(store), layout.JournalSchemaRevision), out int frameLength);
            var descriptor = new JournalGenerationControl {
                Kind = "generation", ContainerId = container, EnvironmentId = environment, Bank = 0, Generation = 0, StoreId = store,
                Source = null, SeedFrameCount = 1, SeedBytes = frameLength, SeedDigest = JournalBankFormat.Digest(frame, frameLength), RetentionApplied = false };
            byte[]? root = null, d = null, r = null;
            var zero = new byte[16384];
            try
            {
                root = Encode(new JournalRootControl { Kind = "root", ContainerId = container, EnvironmentId = environment,
                    LayoutVersion = layout.LayoutVersion, JournalSchemaRevision = layout.JournalSchemaRevision }, layout);
                d = Encode(descriptor, layout);
                r = Encode(new JournalActivationControl { Kind = "activation", ContainerId = container, EnvironmentId = environment,
                    Bank = 0, Generation = 0, StoreId = store, DescriptorDigest = JournalBankFormat.Digest(d, d.Length), PredecessorSealDigest = null }, layout);
                long offset = 0;
                while (offset < layout.FileBytes)
                {
                    int take = (int)Math.Min(zero.Length, layout.FileBytes - offset);
                    Write(file, lease, offset, zero, take); offset += take;
                }
                Write(file, lease, 0, root, root.Length);
                Write(file, lease, layout.BankStart(0), d, d.Length);
                Write(file, lease, layout.BankStart(0) + JournalBankFormat.BodyOffset, frame, frameLength);
                Write(file, lease, layout.BankStart(0) + JournalBankFormat.SlotBytes, r, r.Length);
                Flush(file, lease, durability);
                var selection = ReadOwned(lease, () => JournalBankReader.Read(file, environment, layout));
                Require(selection.Kind == JournalBankSelectionKind.Active && selection.Current.Descriptor.StoreId == store, "new activation");
                return new JournalBankFile(file, lease, environment, durability, selection, true, layout);
            }
            finally { Clear(frame); Clear(root); Clear(d); Clear(r); Clear(zero); }
        }

        // 读取合法字节与本 owner 实际取得 Flush 回执分别表达；失败只尝试一次，保已读事实。
        internal bool ConfirmRecovery()
        {
            if (confirmationAttempted) return confirmed;
            confirmationAttempted = true;
            byte[]? receipt = null;
            try
            {
                var opened = pendingRecovery!;
                JournalBankState? next = opened.Candidate;
                if (opened.Kind == JournalBankSelectionKind.ForwardPrepared)
                {
                    Require(next != null && opened.Current.SealDigest != null, "forward candidate");
                    receipt = Encode(Activation(next!.Descriptor, next.DescriptorDigest, opened.Current.SealDigest));
                }
                Flush(file, lease, durability);
                if (receipt != null)
                {
                    Write(file, lease, layout.BankStart(next!.Index) + JournalBankFormat.SlotBytes, receipt, receipt.Length);
                    Flush(file, lease, durability);
                    var selection = Read();
                    Require(selection.Kind == JournalBankSelectionKind.Active && selection.Current.DescriptorDigest == next.DescriptorDigest &&
                        selection.Current.Descriptor.StoreId == next.Descriptor.StoreId, "forward activation");
                    Current = selection.Current;
                }
                confirmed = true;
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ContractValidationException)
            { RecoveryFailure = error; poisoned = true; return false; }
            finally { Clear(receipt); pendingRecovery = null; }
        }

        internal long AppendOffset(long businessBytes)
        {
            Require(confirmed && !poisoned && businessBytes >= 0 && businessBytes <= layout.MaximumBusinessBytes, "append position");
            return layout.BankStart(Current.Index) + JournalBankFormat.BodyOffset + businessBytes;
        }

        internal JournalRotationResult Rotate(JournalRotationPlan plan, Func<bool> stopRequested, Func<bool> tryAdmitSeal)
        {
            Require(confirmed && !poisoned, "writer confirmation");
            if (plan == null || stopRequested == null || tryAdmitSeal == null) throw new ArgumentNullException();
            byte[]? preparing = null, descriptor = null, seal = null, activation = null;
            var zero = new byte[16384];
            try
            {
                var selection = Read();
                Require(selection.Kind == JournalBankSelectionKind.Active, "rotation source");
                var source = selection.Current;
                Require(source.Seal == null && source.Activation != null && source.Descriptor.StoreId == plan.ExpectedStoreId &&
                    source.Records.Count == plan.ExpectedThroughSequence, "rotation expected prefix");
                Require(source.Descriptor.Generation < int.MaxValue, "generation capacity");
                Current = source;
                var kept = new HashSet<string>(plan.RetainedOperationIds, StringComparer.Ordinal);
                var originals = new HashSet<string>(source.Records.Where(record => record.Entry is OperationAccepted)
                    .Select(record => ((OperationAccepted)record.Entry).Intent.OperationId), StringComparer.Ordinal);
                var keptTasks = new HashSet<string>(plan.RetainedTaskIds, StringComparer.Ordinal);
                var originalTasks = new HashSet<string>(source.Records.Where(record => record.Entry is TaskCreated)
                    .Select(record => ((TaskCreated)record.Entry).TaskId), StringComparer.Ordinal);
                if (!kept.IsSubsetOf(originals) || !keptTasks.IsSubsetOf(originalTasks) ||
                    kept.Count == originals.Count && keptTasks.Count == originalTasks.Count)
                    throw new ArgumentException("Rotation requires a known proper retained subset.", nameof(plan));
                var cut = Cut(source);
                int target = 1 - source.Index;
                long generation = checked(source.Descriptor.Generation + 1);
                string store = Guid.NewGuid().ToString("N");
                Require(store != source.Descriptor.StoreId, "successor store");
                var seed = new List<JournalRecord>(layout.MaximumFrames) {
                    new JournalRecord(1, environment, new StoreOpened(store), layout.JournalSchemaRevision) };
                for (int i = 1; i < source.Records.Count; i++)
                {
                    var original = source.Records[i];
                    string? id = JournalBankReader.OperationId(original.Entry);
                    string? taskId = JournalBankReader.TaskId(original.Entry);
                    Require(id != null || taskId != null, "original entry");
                    if (id != null && kept.Contains(id) || taskId != null && keptTasks.Contains(taskId))
                        seed.Add(new JournalRecord(seed.Count + 1, environment, original.Entry, original.SchemaRevision));
                }
                string seedDigest = SeedDigest(seed, out long seedBytes);
                var next = new JournalGenerationControl { Kind = "generation", ContainerId = selection.Root.ContainerId, EnvironmentId = environment,
                    Bank = target, Generation = generation, StoreId = store, Source = cut, SeedFrameCount = seed.Count, SeedBytes = seedBytes,
                    SeedDigest = seedDigest, RetentionApplied = source.Descriptor.RetentionApplied || kept.Count < originals.Count || keptTasks.Count < originalTasks.Count };
                preparing = Encode(new JournalPreparingControl { Kind = "preparing", ContainerId = selection.Root.ContainerId, EnvironmentId = environment,
                    TargetBank = target, TargetGeneration = generation, TargetStoreId = store, Source = cut });
                descriptor = Encode(next);
                string descriptorDigest = JournalBankFormat.Digest(descriptor, descriptor.Length);
                seal = Encode(new JournalSealControl { Kind = "seal", ContainerId = selection.Root.ContainerId, EnvironmentId = environment, Source = cut,
                    SuccessorBank = target, SuccessorGeneration = generation, SuccessorStoreId = store, SuccessorDescriptorDigest = descriptorDigest });
                activation = Encode(Activation(next, descriptorDigest, JournalBankFormat.Digest(seal, seal.Length)));
                var preserved = new JournalRotationResult(JournalRotationOutcome.CurrentPreserved,
                    new ConfirmedJournalPrefix(source.Records, source.Records.Count, layout.JournalSchemaRevision, source.ThroughBytes), source.Descriptor.RetentionApplied);
                var installed = new JournalRotationResult(JournalRotationOutcome.Installed,
                    new ConfirmedJournalPrefix(seed, seed.Count, layout.JournalSchemaRevision, seedBytes), next.RetentionApplied);
                if (stopRequested()) return Preserved(cut, preserved);

                long targetStart = layout.BankStart(target);
                Write(targetStart, preparing, preparing.Length); Flush();
                if (stopRequested()) return Preserved(cut, preserved);
                long cleared = JournalBankFormat.SlotBytes;
                while (cleared < layout.BankBytes)
                {
                    int take = (int)Math.Min(zero.Length, layout.BankBytes - cleared);
                    Write(targetStart + cleared, zero, take); cleared += take;
                    if (stopRequested()) return Preserved(cut, preserved);
                }
                Flush();
                if (stopRequested()) return Preserved(cut, preserved);
                long written = 0;
                foreach (var record in seed)
                {
                    byte[] frame = layout.EncodeRecord(record, out int length);
                    try { Write(targetStart + JournalBankFormat.BodyOffset + written, frame, length); written += length; }
                    finally { Clear(frame); }
                    if (stopRequested()) return Preserved(cut, preserved);
                }
                Require(written == seedBytes, "written seed extent");
                Write(targetStart, descriptor, descriptor.Length); Flush();
                // Prepared 的实际完整读取还核原链复制与零尾；不是仅凭编码成功承认交接。
                var preparedSelection = Read();
                SameSource(preparedSelection, cut);
                var preparedSlot = ReadOwned(lease, () => PhysicalJournalControlCodec.Read(file, targetStart, layout));
                Require(preparedSlot != null && preparedSlot.Digest == descriptorDigest, "prepared descriptor");
                if (stopRequested() || !tryAdmitSeal()) return Preserved(cut, preserved);

                // seal admission 后不因 Stop 退回旧代；只完成此次最小交接或沿异常关闭新ACK。
                Write(layout.BankStart(source.Index) + 2L * JournalBankFormat.SlotBytes, seal, seal.Length); Flush();
                Write(targetStart + JournalBankFormat.SlotBytes, activation, activation.Length); Flush();
                var complete = Read();
                Require(complete.Kind == JournalBankSelectionKind.Active && complete.Current.DescriptorDigest == descriptorDigest &&
                    complete.Current.Descriptor.StoreId == store && complete.Current.Records.Count == seed.Count && complete.Current.ThroughBytes == seedBytes,
                    "installed successor");
                Current = complete.Current;
                return installed;
            }
            catch { poisoned = true; throw; }
            finally { Clear(preparing); Clear(descriptor); Clear(seal); Clear(activation); Clear(zero); }
        }

        private JournalRotationResult Preserved(JournalCut cut, JournalRotationResult result)
        {
            var actual = Read(); SameSource(actual, cut); Current = actual.Current;
            return result;
        }
        private void SameSource(JournalBankSelection actual, JournalCut cut)
        {
            Require(actual.Kind == JournalBankSelectionKind.Active && actual.Current.Index == cut.Bank &&
                actual.Current.Descriptor.Generation == cut.Generation && actual.Current.Descriptor.StoreId == cut.StoreId &&
                actual.Current.DescriptorDigest == cut.DescriptorDigest && actual.Current.ActivationDigest == cut.ActivationDigest &&
                actual.Current.Records.Count == cut.ThroughSequence && actual.Current.ThroughBytes == cut.ThroughBytes,
                "preserved source");
            Require(ReadOwned(lease, () => JournalBankReader.HashRange(file,
                layout.BankStart(actual.Current.Index) + JournalBankFormat.BodyOffset, cut.ThroughBytes)) == cut.PrefixDigest,
                "preserved source digest");
        }
        private JournalCut Cut(JournalBankState source) => new JournalCut { Bank = source.Index,
            Generation = source.Descriptor.Generation, StoreId = source.Descriptor.StoreId, DescriptorDigest = source.DescriptorDigest,
            ActivationDigest = source.ActivationDigest!, ThroughSequence = source.Records.Count, ThroughBytes = source.ThroughBytes,
            PrefixDigest = ReadOwned(lease, () => JournalBankReader.HashRange(file,
                layout.BankStart(source.Index) + JournalBankFormat.BodyOffset, source.ThroughBytes)) };

        private static JournalActivationControl Activation(JournalGenerationControl descriptor, string descriptorDigest, string? predecessorSealDigest) =>
            new JournalActivationControl { Kind = "activation", ContainerId = descriptor.ContainerId, EnvironmentId = descriptor.EnvironmentId,
                Bank = descriptor.Bank, Generation = descriptor.Generation, StoreId = descriptor.StoreId,
                DescriptorDigest = descriptorDigest, PredecessorSealDigest = predecessorSealDigest };
        private static byte[] Encode(JournalPhysicalControl entry, JournalLayout layout) =>
            PhysicalJournalControlCodec.Encode(new JournalControlDocument { FormatVersion = layout.ControlVersion, Entry = entry }, layout);
        private byte[] Encode(JournalPhysicalControl entry) => Encode(entry, layout);
        private JournalBankSelection Read() => ReadOwned(lease, () => JournalBankReader.Read(file, environment, layout));
        private void Write(long offset, byte[] value, int count) => Write(file, lease, offset, value, count);
        private void Flush() => Flush(file, lease, durability);
        // Owner必须在实际journal worker终结后才Dispose file/lease；短检查不把磁盘等待转移到lease monitor。
        // 提早释放是调用方的所有权违约，前后检查会明确失败，不能据此报告IO未发生或退额。
        private static void Write(FileStream file, EnvironmentLease lease, long offset, byte[] value, int count)
        { RequireHeld(lease); file.Position = offset; file.Write(value, 0, count); RequireHeld(lease); }
        private static void Flush(FileStream file, EnvironmentLease lease, IJournalDurability durability)
        { RequireHeld(lease); durability.Flush(file); RequireHeld(lease); }
        private static T ReadOwned<T>(EnvironmentLease lease, Func<T> read)
        { RequireHeld(lease); T result = read(); RequireHeld(lease); return result; }
        private static void RequireHeld(EnvironmentLease lease) => lease.WhileHeld(() => true);

        private string SeedDigest(IReadOnlyList<JournalRecord> seed, out long totalBytes)
        {
            totalBytes = 0;
            using (var sha = SHA256.Create())
            {
                foreach (var record in seed)
                {
                    byte[] frame = layout.EncodeRecord(record, out int length);
                    try { totalBytes = checked(totalBytes + length); sha.TransformBlock(frame, 0, length, frame, 0); }
                    finally { Clear(frame); }
                }
                Require(totalBytes <= layout.MaximumBusinessBytes && seed.Count <= layout.MaximumFrames, "seed capacity");
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var text = new StringBuilder(64);
                foreach (byte part in sha.Hash!) text.Append(part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
        private static void Clear(byte[]? value) { if (value != null) Array.Clear(value, 0, value.Length); }
        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidDataException("Journal bank IO " + detail + " invalid."); }
    }
}
