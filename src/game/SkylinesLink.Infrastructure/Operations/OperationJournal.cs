using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;
using SkylinesLink.Infrastructure.Connection;

namespace SkylinesLink.Infrastructure.Operations
{
    public interface IJournalDurability
    {
        void Flush(FileStream file);
    }
    public sealed class FileJournalDurability : IJournalDurability
    {
        private readonly IPrivateStorageBoundary boundary;
        public FileJournalDurability(IPrivateStorageBoundary boundary) { this.boundary = boundary ?? throw new ArgumentNullException(nameof(boundary)); }
        public void Flush(FileStream file) => boundary.FlushToDisk(file);
    }

    /// <summary>有限单 writer；受理先预留四帧磁盘额度和三个阶段槽，真实 Flush 返回后才确认。</summary>
    public sealed partial class OperationJournal : IOperationJournal, ITaskJournal
    {
        private readonly object sync = new object();
        private readonly EnvironmentLease lease;
        private readonly string environment;
        private readonly FileStream? file;
        private readonly IJournalDurability durability;
        private readonly JournalBankFile? bankFile;
        private readonly JournalLayout layout;
        private readonly Queue<Work> queue = new Queue<Work>();
        private readonly JournalRecord[] confirmed = new JournalRecord[OperationLimits.MaximumFrames];
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly Task worker;
        private bool healthy, stopping;
        private long sequence, occupiedBytes, confirmedBytes, reservedBytes;
        private int reservedFrames, reservations;
        private int confirmedCount;
        private Action? confirmedObserver;
        private bool notificationFailed;
        private RecoveryReservation? recoveryReservation;
        private bool recoveryClaimed;
        private bool retentionApplied;
        private Rotation? rotation;
        public JournalRecovery Recovery { get; }
        public bool IsHealthy { get { lock (sync) return healthy && !stopping; } }
        public Task Completion => worker;
        public int ReservedOperationCount { get { lock (sync) return reservations; } }
        public int QueuedOrWriting { get { lock (sync) return queue.Count + writingCount + (rotation == null ? 0 : 1); } }
        public int RecoveryReservedTerminalCount { get { lock (sync) return recoveryReservation?.Remaining ?? 0; } }
        public int RecoveryQueuedOrWriting { get { lock (sync) return recoveryReservation?.Pending == true ? 1 : 0; } }
        public bool ConfirmationNotificationFailed { get { lock (sync) return notificationFailed; } }
        private int writingCount;

        public OperationJournal(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment, bool initializeNew,
            IJournalDurability? durability = null)
            : this(boundary, lease, environment, initializeNew, durability, false) { }

        // 产品组合根与覆盖感知的Operations/查询修订一起使用；旧顺序文件保读，不隐式迁空。
        public static OperationJournal OpenForAuthority(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment,
            bool initializeNew, IJournalDurability? durability = null) =>
            new OperationJournal(boundary, lease, environment, initializeNew, durability, true);

        // 同一façade的显式格式选择；旧构造保留兼容读取/拒绝边界，不构成第二发行形态。
        internal OperationJournal(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment, bool initializeNew,
            IJournalDurability? durability, bool enableBankedStorage)
            : this(boundary, lease, environment, initializeNew, durability, enableBankedStorage, JournalLayout.Legacy) { }

        internal static OperationJournal OpenMixedForAuthority(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment,
            bool initializeNew, IJournalDurability? durability = null) =>
            new OperationJournal(boundary, lease, environment, initializeNew, durability, true, JournalLayout.Mixed);

        private OperationJournal(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment, bool initializeNew,
            IJournalDurability? durability, bool enableBankedStorage, JournalLayout layout)
        {
            this.lease = lease; this.environment = AuthorizationState.NormalizeEnvironment(environment);
            this.durability = durability ?? new FileJournalDurability(boundary);
            this.layout = layout;
            var records = new List<JournalRecord>();
            long recoveredFrameBytes = 0;
            bool complete = false, incompatible = false, durableConfirmed = false, constructed = false;
            try
            {
                try
                {
                    file = lease.WhileHeld(() =>
                    {
                        boundary.ValidateDirectory();
                        string path = Path.Combine(boundary.DirectoryPath, "operations.journal");
                        boundary.ValidateFile(path, true);
                        return initializeNew ? boundary.CreatePrivateFile(path) : new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);
                    });
                    if (initializeNew && enableBankedStorage)
                    {
                        bankFile = JournalBankFile.Create(file, lease, this.environment, this.durability, layout);
                        durableConfirmed = true;
                    }
                    else if (initializeNew)
                    {
                        // 初始化只由已证明新目录的组合根指定，不用缺文件推断新环境。
                        byte[] frame = JournalFrameCodec.Encode(new JournalRecord(1, this.environment, new StoreOpened(Guid.NewGuid().ToString("N"))), out int length);
                        try { lease.WhileHeld(() => { file.Write(frame, 0, length); this.durability.Flush(file); return true; }); }
                        finally { Array.Clear(frame, 0, frame.Length); }
                        file.Position = 0;
                    }
                    if (!initializeNew)
                    {
                        file.Position = 0;
                        var magic = new byte[4]; int count = file.Read(magic, 0, magic.Length); file.Position = 0;
                        if (count == 4 && magic[0] == 'S' && magic[1] == 'L' && magic[2] == 'J' && magic[3] == 'C')
                        {
                            // Authority/lookup尚未接保留覆盖；默认路径不能提前消费已退役新格式。
                            if (!enableBankedStorage) throw new UnsupportedJournalVersionException();
                            bankFile = JournalBankFile.Open(file, lease, this.environment, this.durability, layout);
                            durableConfirmed = bankFile.ConfirmRecovery();
                        }
                        else if (SupportsTaskMetadata) throw new UnsupportedJournalVersionException();
                    }
                    if (bankFile != null)
                    {
                        records.AddRange(bankFile.Current.Records);
                        occupiedBytes = bankFile.Current.ThroughBytes;
                        recoveredFrameBytes = occupiedBytes;
                        retentionApplied = bankFile.Current.Descriptor.RetentionApplied;
                    }
                    else
                    {
                        // 只在尚无 worker 的 IO 初始化阶段读长度。准入不能触碰共享 FileStream。
                        occupiedBytes = file.Length;
                        if (occupiedBytes > OperationLimits.MaximumJournalBytes) throw new InvalidDataException("Journal capacity invalid.");
                        JournalRecord? record;
                        while ((record = JournalFrameCodec.Read(file)) != null)
                        {
                            if (record.Sequence != records.Count + 1 || record.EnvironmentId != this.environment || records.Count >= OperationLimits.MaximumFrames ||
                                (records.Count == 0) != (record.Entry is StoreOpened)) throw new InvalidDataException("Journal prefix invalid.");
                            records.Add(record);
                            recoveredFrameBytes = file.Position;
                        }
                    }
                    complete = records.Count > 0;
                    sequence = records.Count;
                }
                catch (UnsupportedJournalVersionException)
                {
                    // 旧格式没有原生 bool，混合格式也不能猜测。保文件且不激活 writer，不迁移/补真值。
                    complete = false; incompatible = true; records.Clear();
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is ArgumentException || error is System.Text.DecoderFallbackException || error is SkylinesLink.Contracts.ContractValidationException)
                { complete = false; }
                if (initializeNew && bankFile == null) durableConfirmed = complete;
                if (!initializeNew && bankFile == null && records.Count > 0 && file != null)
                {
                    // 进程崩溃后完整帧可能仍只在 OS 缓存。只确认已有字节，不重写或截断坏尾。
                    try { lease.WhileHeld(() => { this.durability.Flush(file); return true; }); durableConfirmed = true; }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { durableConfirmed = false; }
                }
                Recovery = new JournalRecovery(complete, records, durableConfirmed, incompatible, retentionApplied, layout.JournalSchemaRevision);
                if (durableConfirmed)
                {
                    foreach (var record in records) confirmed[confirmedCount++] = record;
                    confirmedBytes = recoveredFrameBytes;
                }
                healthy = complete && durableConfirmed;
                if (SupportsTaskMetadata && healthy) healthy = RestoreTaskCredits(records);
                worker = Task.Run(WorkAsync);
                constructed = true;
            }
            finally { if (!constructed) { try { file?.Dispose(); } finally { signal.Dispose(); } } }
        }

        public ConfirmedJournalPrefix? CaptureConfirmedPrefix()
        {
            lock (sync) return confirmedCount == 0 ? null : new ConfirmedJournalPrefix(confirmed, confirmedCount, layout.JournalSchemaRevision, confirmedBytes);
        }
        public JournalRotationPosition? CaptureRotationPosition()
        {
            lock (sync)
            {
                if (!healthy || stopping || confirmedCount == 0 || !(confirmed[0].Entry is StoreOpened header)) return null;
                return new JournalRotationPosition(header.StoreId, sequence, retentionApplied, bankFile != null);
            }
        }
        public IJournalRotation? TryStartRotation(JournalRotationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            // 此公开 owner 尚为 record2/CP1；混合物理组件不能隐式升级其保留语义。
            if (!SupportsTaskMetadata && plan.RetainedTaskIds.Count != 0)
                throw new ArgumentException("Task retention requires a mixed journal owner.", nameof(plan));
            lock (sync)
            {
                if (!healthy || stopping || bankFile == null || bankFile.Current.Descriptor.Generation >= int.MaxValue ||
                    rotation != null || reservations != 0 || recoveryReservation != null ||
                    queue.Count != 0 || writingCount != 0 || metadataReservations != 0 ||
                    reservedFrames != taskStopReserved || confirmedCount == 0 ||
                    !(confirmed[0].Entry is StoreOpened header) || header.StoreId != plan.ExpectedStoreId || sequence != plan.ExpectedThroughSequence)
                    return null;
                var prefix = new ConfirmedJournalPrefix(confirmed, confirmedCount, layout.JournalSchemaRevision, confirmedBytes);
                var folded = layout.Fold(prefix.Records);
                if (!folded.AllRecordsValid) return null;
                var known = new HashSet<string>(folded.Operations.Select(item => item.Intent.OperationId), StringComparer.Ordinal);
                if (plan.RetainedOperationIds.Any(id => !known.Contains(id)))
                    throw new ArgumentException("Rotation plan contains an unknown operation.", nameof(plan));
                var knownTasks = new HashSet<string>(folded.Tasks.Select(item => item.TaskId), StringComparer.Ordinal);
                if (plan.RetainedTaskIds.Any(id => !knownTasks.Contains(id)))
                    throw new ArgumentException("Rotation plan contains an unknown task.", nameof(plan));
                if (taskCredits.Values.Any(task => !task.Stopped && !plan.RetainedTaskIds.Contains(task.TaskId)))
                    throw new ArgumentException("Rotation plan must preserve every live task and its stop credit.", nameof(plan));
                if (plan.RetainedOperationIds.Count == known.Count && plan.RetainedTaskIds.Count == knownTasks.Count) return null;
                var next = new Rotation(this, plan);
                rotation = next; signal.Release(); return next;
            }
        }
        public IDisposable ObserveConfirmed(Action notification)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            lock (sync)
            {
                if (confirmedObserver != null) throw new InvalidOperationException("Journal observer already attached.");
                confirmedObserver = notification;
                return new Observer(this, notification);
            }
        }
        private void NotifyConfirmed()
        {
            Action? observer;
            lock (sync) observer = confirmedObserver;
            try { observer?.Invoke(); }
            catch { lock (sync) notificationFailed = true; } // 旁路失败不能撤销真实持久 ACK。
        }

        public IOperationJournalReservation? TryReserveOperation()
        {
            lock (sync)
            {
                if (!healthy || stopping || rotation != null || recoveryReservation != null || reservations >= OperationLimits.MaximumActive ||
                    sequence + reservedFrames + 4 > OperationLimits.MaximumFrames ||
                    occupiedBytes + reservedBytes + 4L * OperationLimits.MaximumFrame > BusinessByteLimit) return null;
                reservations++; reservedFrames += 4; reservedBytes += 4L * OperationLimits.MaximumFrame;
                return new Reservation(this);
            }
        }
        public IRecoveryTerminalReservation? TryReserveRecoveryTerminals(long expectedThroughSequence, int terminalCount)
        {
            if (terminalCount < 1 || terminalCount > OperationLimits.MaximumRetained) throw new ArgumentOutOfRangeException(nameof(terminalCount));
            lock (sync)
            {
                if (SupportsTaskMetadata && startupRecoveryReservation)
                {
                    if (!healthy || stopping || rotation != null || recoveryClaimed ||
                        expectedThroughSequence != recoveryOriginalSequence || recoveryReservation == null ||
                        terminalCount != recoveryReservation.Remaining) return null;
                    recoveryClaimed = true; return recoveryReservation;
                }
                if (!healthy || stopping || rotation != null || !Recovery.Complete || !Recovery.DurableConfirmed || recoveryClaimed ||
                    expectedThroughSequence != sequence || reservations != 0 || queue.Count != 0 || writingCount != 0 ||
                    sequence + reservedFrames + terminalCount > OperationLimits.MaximumFrames ||
                    occupiedBytes + reservedBytes + (long)terminalCount * OperationLimits.MaximumFrame > BusinessByteLimit) return null;
                var reservation = new RecoveryReservation(this, terminalCount);
                recoveryClaimed = true;
                reservedFrames += terminalCount; reservedBytes += (long)terminalCount * OperationLimits.MaximumFrame;
                return recoveryReservation = reservation;
            }
        }
        public Task StopAsync()
        {
            lock (sync) { if (!stopping) { stopping = true; rotation?.RequestStop(); signal.Release(); } return worker; }
        }
        private int BusinessByteLimit => bankFile == null ? OperationLimits.MaximumJournalBytes : layout.MaximumBusinessBytes;
        private async Task WorkAsync()
        {
            try
            {
                while (true)
                {
                    await signal.WaitAsync().ConfigureAwait(false);
                    var batch = new List<Work>(OperationLimits.MaximumCommitBatchFrames);
                    Rotation? maintenance = null;
                    lock (sync)
                    {
                        // 已取得的维护归属必须收束，Stop-before-dequeue不能遗留未终结Completion。
                        if (rotation != null && !rotation.Started) { maintenance = rotation; maintenance.Started = true; }
                        else
                        {
                            if (queue.Count == 0) { if (stopping) return; continue; }
                            batch.Add(queue.Dequeue());
                            while (batch.Count < OperationLimits.MaximumCommitBatchFrames && queue.Count > 0)
                            {
                                batch.Add(queue.Dequeue());
                                signal.Wait(0); // 本轮只消费已经实际出队的额外通知，不等待新项。
                            }
                            writingCount = batch.Count;
                        }
                    }
                    if (maintenance != null) { ProcessRotation(maintenance); continue; }
                    Exception? failure = null; long firstSequence = 0; long batchBytes = 0;
                    var frames = new byte[batch.Count][];
                    var lengths = new int[batch.Count];
                    var records = new JournalRecord[batch.Count];
                    try
                    {
                        lock (sync) { if (!healthy) throw new IOException("Journal writer unavailable."); firstSequence = sequence + 1; }
                        for (int i = 0; i < batch.Count; i++)
                        {
                            records[i] = new JournalRecord(firstSequence + i, environment, batch[i].Entry, layout.JournalSchemaRevision);
                            frames[i] = layout.EncodeRecord(records[i], out lengths[i]);
                            batchBytes += lengths[i];
                        }
                        if (bankFile != null)
                        {
                            // 组合owner实际join worker后才释放借用lease；不持lease monitor等待磁盘。
                            lease.WhileHeld(() => true);
                            file!.Position = bankFile.AppendOffset(occupiedBytes);
                            for (int i = 0; i < batch.Count; i++) file!.Write(frames[i], 0, lengths[i]);
                            durability.Flush(file!); lease.WhileHeld(() => true);
                        }
                        else lease.WhileHeld(() =>
                        {
                            file!.Position = occupiedBytes;
                            for (int i = 0; i < batch.Count; i++) file.Write(frames[i], 0, lengths[i]);
                            durability.Flush(file); return true;
                        });
                    }
                    catch (Exception error) { failure = error; lock (sync) healthy = false; }
                    finally
                    {
                        foreach (var frame in frames) if (frame != null) Array.Clear(frame, 0, frame.Length);
                        lock (sync)
                        {
                            // 唯一 writer 成功确认后，实际占用与预留归还在同一边界切换；失败后禁止新准入。
                            if (failure == null)
                            {
                                // 一次真实 Flush 的整批记录与水位一起可见，不暴露半批确认。
                                foreach (var record in records) confirmed[confirmedCount++] = record;
                                sequence = firstSequence + batch.Count - 1; occupiedBytes += batchBytes;
                                confirmedBytes += batchBytes;
                            }
                            writingCount = 0;
                            foreach (var next in batch) next.Owner.Finish(next.Slot, failure == null);
                            if (stopping && queue.Count == 0) signal.Release();
                        }
                    }
                    // 帧字节已清零、在途阶段已结束才发布确认；不把等待超时视作 IO 完成。
                    if (failure == null)
                    {
                        string storeId = ((StoreOpened)confirmed[0].Entry).StoreId;
                        for (int i = 0; i < batch.Count; i++) batch[i].Succeed(storeId, firstSequence + i);
                        NotifyConfirmed();
                    }
                    else foreach (var next in batch) next.Fail(new JournalPersistenceException(failure));
                }
            }
            finally
            {
                lock (sync) ReleaseTaskShutdownCredits();
                file?.Dispose(); signal.Dispose();
            }
        }
        private void ProcessRotation(Rotation work)
        {
            JournalRotationResult? result = null;
            Exception? failure = null;
            try { result = bankFile!.Rotate(work.Plan, work.IsStopRequested, work.TryAdmitSeal); }
            catch (Exception error) { failure = error; }
            lock (sync)
            {
                if (failure != null) healthy = false;
                else if (result!.Outcome == JournalRotationOutcome.Installed)
                {
                    Array.Clear(confirmed, 0, confirmed.Length);
                    confirmedCount = 0;
                    foreach (var record in result.Prefix.Records) confirmed[confirmedCount++] = record;
                    sequence = result.Prefix.ThroughSequence; occupiedBytes = bankFile!.Current.ThroughBytes;
                    confirmedBytes = occupiedBytes;
                    retentionApplied = result.RetentionApplied;
                    InstallTaskRetirement(work.Plan);
                }
                rotation = null;
                if (stopping && queue.Count == 0) signal.Release();
            }
            if (failure != null) work.Done.TrySetException(new JournalPersistenceException(failure));
            else
            {
                work.Done.TrySetResult(result!);
                if (result!.Outcome == JournalRotationOutcome.Installed) NotifyConfirmed();
            }
        }
        private sealed class Rotation : IJournalRotation
        {
            private readonly OperationJournal owner;
            private bool stopped, sealAdmitted;
            internal bool Started;
            internal readonly JournalRotationPlan Plan;
            internal readonly TaskCompletionSource<JournalRotationResult> Done =
                new TaskCompletionSource<JournalRotationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Rotation(OperationJournal owner, JournalRotationPlan plan) { this.owner = owner; Plan = plan; }
            public Task<JournalRotationResult> Completion => Done.Task;
            public void RequestStop() { lock (owner.sync) stopped = true; }
            internal bool IsStopRequested() { lock (owner.sync) return stopped || owner.stopping; }
            internal bool TryAdmitSeal()
            {
                lock (owner.sync)
                {
                    if (stopped || owner.stopping || sealAdmitted) return false;
                    sealAdmitted = true; return true;
                }
            }
        }
        private sealed class Observer : IDisposable
        {
            private readonly OperationJournal owner;
            private Action? callback;
            internal Observer(OperationJournal owner, Action callback) { this.owner = owner; this.callback = callback; }
            public void Dispose()
            {
                lock (owner.sync)
                {
                    if (callback != null && ReferenceEquals(owner.confirmedObserver, callback)) owner.confirmedObserver = null;
                    callback = null;
                }
            }
        }
        private void ValidateEntry(JournalEntry entry)
        {
            try
            {
                byte[] frame = layout.EncodeRecord(new JournalRecord(OperationLimits.MaximumFrames, environment, entry, layout.JournalSchemaRevision), out _);
                Array.Clear(frame, 0, frame.Length);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is NotSupportedException ||
                error is OverflowException || error is Newtonsoft.Json.JsonException || error is SkylinesLink.Contracts.ContractValidationException)
            { throw new ArgumentException("Journal entry cannot be encoded within its contract and capacity.", nameof(entry), error); }
        }
        private interface IStageOwner { void Finish(JournalSlot slot, bool persisted); }
        private abstract class Work
        {
            internal Work(IStageOwner owner, JournalEntry entry, JournalSlot slot) { Owner = owner; Entry = entry; Slot = slot; }
            internal readonly IStageOwner Owner;
            internal readonly JournalEntry Entry;
            internal readonly JournalSlot Slot;
            internal abstract void Succeed(string storeId, long sequence);
            internal abstract void Fail(JournalPersistenceException failure);
        }
        private sealed class OperationWork : Work
        {
            internal OperationWork(IStageOwner owner, JournalEntry entry, JournalSlot slot) : base(owner, entry, slot) { }
            internal readonly TaskCompletionSource<long> Completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override void Succeed(string storeId, long sequence) => Completion.TrySetResult(sequence);
            internal override void Fail(JournalPersistenceException failure) => Completion.TrySetException(failure);
        }
        private sealed class Reservation : IOperationJournalReservation, IStageOwner
        {
            private readonly OperationJournal owner;
            private readonly bool[] inFlight = new bool[3];
            private readonly int[] used = new int[3];
            private int remaining = 4, pending;
            private bool disposed;
            internal Reservation(OperationJournal owner) { this.owner = owner; }
            public void ValidateEntry(JournalEntry entry)
            {
                lock (owner.sync)
                {
                    if (disposed || pending != 0) throw new InvalidOperationException("Journal reservation unavailable.");
                    if (IsTaskMetadata(entry)) throw new ArgumentException("Task metadata requires its own reserved credit.", nameof(entry));
                    owner.ValidateEntry(entry);
                }
            }
            public Task<long> AppendAsync(JournalEntry entry, JournalSlot slot)
            {
                lock (owner.sync)
                {
                    int index = (int)slot;
                    if (IsTaskMetadata(entry)) throw new ArgumentException("Task metadata requires its own reserved credit.", nameof(entry));
                    if (disposed || owner.stopping || !owner.healthy) return Task.FromException<long>(new JournalPersistenceException());
                    if (index < 0 || index > 2 || inFlight[index] || used[index] >= (slot == JournalSlot.Normal ? 2 : 1))
                        throw new InvalidOperationException("Journal stage credit exhausted.");
                    used[index]++; inFlight[index] = true; pending++;
                    var work = new OperationWork(this, entry, slot);
                    owner.queue.Enqueue(work); owner.signal.Release();
                    return work.Completion.Task;
                }
            }
            public void Finish(JournalSlot slot, bool persisted)
            {
                inFlight[(int)slot] = false; pending--; remaining--;
                owner.reservedFrames--; owner.reservedBytes -= OperationLimits.MaximumFrame;
                if (disposed && pending == 0) Release();
            }
            public void Dispose()
            {
                lock (owner.sync) { if (disposed) return; disposed = true; if (pending == 0) Release(); }
            }
            private void Release()
            {
                owner.reservedFrames -= remaining; owner.reservedBytes -= (long)remaining * OperationLimits.MaximumFrame;
                remaining = 0; owner.reservations--;
            }
        }
        // 所有缺失 final 一次预留磁盘；只有一个编码/排队 stage，不挪用 live operation 的四帧生命周期。
        private sealed class RecoveryReservation : IRecoveryTerminalReservation, IStageOwner
        {
            private readonly OperationJournal owner;
            private bool disposed;
            internal int Remaining { get; private set; }
            internal bool Pending { get; private set; }
            internal RecoveryReservation(OperationJournal owner, int count) { this.owner = owner; Remaining = count; }
            public void ValidateEntry(OperationFinished entry)
            {
                lock (owner.sync)
                {
                    if (disposed || Pending || Remaining == 0) throw new InvalidOperationException("Recovery terminal reservation unavailable.");
                    owner.ValidateEntry(entry);
                }
            }
            public Task<long> AppendAsync(OperationFinished entry)
            {
                lock (owner.sync)
                {
                    if (disposed || owner.stopping || !owner.healthy) return Task.FromException<long>(new JournalPersistenceException());
                    if (Pending || Remaining == 0) throw new InvalidOperationException("Recovery terminal stage credit exhausted.");
                    var work = new OperationWork(this, entry, JournalSlot.Terminal);
                    owner.queue.Enqueue(work); Pending = true; owner.signal.Release();
                    return work.Completion.Task;
                }
            }
            public void Finish(JournalSlot slot, bool persisted)
            {
                Pending = false; Remaining--;
                owner.reservedFrames--; owner.reservedBytes -= OperationLimits.MaximumFrame;
                if (disposed) Release();
            }
            public void Dispose()
            {
                lock (owner.sync) { if (disposed) return; disposed = true; if (!Pending) Release(); }
            }
            private void Release()
            {
                // mixed启动时缺final仍是原日志的收尾义务；未写完的票不能因调用方Dispose归给新intent。
                if (owner.startupRecoveryReservation && Remaining > 0 && owner.healthy && !owner.stopping) return;
                ReleaseAtShutdown();
            }
            internal void ReleaseAtShutdown()
            {
                owner.reservedFrames -= Remaining; owner.reservedBytes -= (long)Remaining * OperationLimits.MaximumFrame;
                Remaining = 0; owner.recoveryReservation = null;
            }
        }
    }
}
