using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Infrastructure.Operations
{
    public sealed partial class OperationJournal
    {
        private readonly Dictionary<string, TaskCredit> taskCredits = new Dictionary<string, TaskCredit>(StringComparer.Ordinal);
        private int metadataReservations, taskStopReserved;
        private bool startupRecoveryReservation;
        private long recoveryOriginalSequence;

        public bool SupportsTaskMetadata => layout.JournalSchemaRevision == 3;
        public int TaskMetadataReservedCount { get { lock (sync) return metadataReservations; } }
        public int TaskStopReservedCount { get { lock (sync) return taskStopReserved; } }
        public int RetainedTaskCount { get { lock (sync) return taskCredits.Count; } }

        private static bool IsTaskMetadata(JournalEntry entry) => entry is TaskCreated || entry is TaskGoalRevised || entry is TaskStopped;

        // 在worker启动/普通准入之前一起重建所有停止和缺final的信用；不足只读，不改源文件。
        private bool RestoreTaskCredits(IReadOnlyList<JournalRecord> records)
        {
            var facts = layout.Fold(records);
            if (!facts.AllRecordsValid) return false;
            foreach (var task in facts.Tasks)
                taskCredits.Add(task.TaskId, new TaskCredit(task.TaskId) { CreateQueued = true, Stopped = task.IsStopped });
            int stops = facts.Tasks.Count(task => !task.IsStopped);
            int finals = facts.Operations.Count(operation => operation.Final == null);
            long bytes = (long)stops * TaskJournalLimits.MaximumMetadataFrame + (long)finals * OperationLimits.MaximumFrame;
            if (sequence + stops + finals > OperationLimits.MaximumFrames || occupiedBytes + bytes > BusinessByteLimit) return false;
            foreach (var task in taskCredits.Values)
                if (!task.Stopped) { task.StopReserved = true; taskStopReserved++; }
            reservedFrames += stops + finals; reservedBytes += bytes;
            if (finals > 0)
            {
                recoveryReservation = new RecoveryReservation(this, finals);
                startupRecoveryReservation = true; recoveryOriginalSequence = sequence;
            }
            return true;
        }

        public ITaskJournalReservation? TryReserveTaskCreation(string taskId)
        {
            lock (sync)
            {
                if (!CanReserveOrdinaryMetadata() || taskCredits.Count >= TaskJournalLimits.MaximumTasks || taskCredits.ContainsKey(taskId)) return null;
                TaskJournalLimits.ValidateTaskId(taskId);
                if (!HasMetadataCapacity(2)) return null;
                var task = new TaskCredit(taskId) { StopReserved = true };
                var reservation = new TaskReservation(this, task, TaskMetadataKind.Create);
                task.Ordinary = reservation; taskCredits.Add(taskId, task);
                metadataReservations++; taskStopReserved++; ReserveMetadataDisk(2);
                return reservation;
            }
        }

        public ITaskJournalReservation? TryReserveTaskRevision(string taskId)
        {
            lock (sync)
            {
                if (!CanReserveOrdinaryMetadata() || !taskCredits.TryGetValue(taskId, out var task) ||
                    !task.CreateQueued || task.Stopped || task.StopQueued || task.StopClaim != null || task.Ordinary != null || !HasMetadataCapacity(1)) return null;
                var reservation = new TaskReservation(this, task, TaskMetadataKind.Revise);
                task.Ordinary = reservation; metadataReservations++; ReserveMetadataDisk(1);
                return reservation;
            }
        }

        public ITaskJournalReservation? TryReserveTaskStop(string taskId)
        {
            lock (sync)
            {
                // 已持停止信用不受普通metadata、recovery和owned rotation的门禁挤占。
                if (!SupportsTaskMetadata || !healthy || stopping || !taskCredits.TryGetValue(taskId, out var task) ||
                    !task.CreateQueued || task.Stopped || task.StopQueued || !task.StopReserved || task.StopClaim != null) return null;
                var reservation = new TaskReservation(this, task, TaskMetadataKind.Stop);
                task.StopClaim = reservation; return reservation;
            }
        }

        private bool CanReserveOrdinaryMetadata() => SupportsTaskMetadata && healthy && !stopping && rotation == null &&
            recoveryReservation == null && metadataReservations < TaskJournalLimits.MaximumPendingMetadata;
        private bool HasMetadataCapacity(int frames) => sequence + reservedFrames + frames <= OperationLimits.MaximumFrames &&
            occupiedBytes + reservedBytes + (long)frames * TaskJournalLimits.MaximumMetadataFrame <= BusinessByteLimit;
        private void ReserveMetadataDisk(int frames)
        { reservedFrames += frames; reservedBytes += (long)frames * TaskJournalLimits.MaximumMetadataFrame; }
        private void ReleaseMetadataDisk()
        { reservedFrames--; reservedBytes -= TaskJournalLimits.MaximumMetadataFrame; }
        private void ReleaseStopCredit(TaskCredit task)
        {
            if (!task.StopReserved) return;
            task.StopReserved = false; taskStopReserved--; ReleaseMetadataDisk();
        }
        private void InstallTaskRetirement(JournalRotationPlan plan)
        {
            foreach (var task in taskCredits.Values.Where(task => !plan.RetainedTaskIds.Contains(task.TaskId)).ToArray())
            {
                // 准入已禁止退未停Task，也无ordinary工作；安装只移冻结的实际退休项。
                ReleaseStopCredit(task); taskCredits.Remove(task.TaskId);
            }
        }
        private void ReleaseTaskShutdownCredits()
        {
            foreach (var task in taskCredits.Values.ToArray())
            {
                task.Ordinary?.AbandonUnqueued(); task.StopClaim?.AbandonUnqueued(); ReleaseStopCredit(task);
            }
            taskCredits.Clear();
            // mixed启动的恢复票可能尚未被Authority领取；真实worker终结后才可释放。
            if (startupRecoveryReservation) recoveryReservation?.ReleaseAtShutdown();
        }

        private enum TaskMetadataKind { Create, Revise, Stop }
        private sealed class TaskCredit
        {
            internal TaskCredit(string taskId) { TaskId = taskId; }
            internal readonly string TaskId;
            internal bool CreateQueued, Stopped, StopReserved, StopQueued;
            internal TaskReservation? Ordinary, StopClaim;
        }

        private sealed class TaskMetadataWork : Work
        {
            internal TaskMetadataWork(IStageOwner owner, JournalEntry entry) : base(owner, entry, JournalSlot.Normal) { }
            internal readonly TaskCompletionSource<JournalTaskReceipt> Completion =
                new TaskCompletionSource<JournalTaskReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override void Succeed(string storeId, long sequence) => Completion.TrySetResult(new JournalTaskReceipt(storeId, sequence));
            internal override void Fail(JournalPersistenceException failure) => Completion.TrySetException(failure);
        }

        private sealed class TaskReservation : ITaskJournalReservation, IStageOwner
        {
            private readonly OperationJournal owner;
            private readonly TaskCredit task;
            private readonly TaskMetadataKind kind;
            private bool disposed, queued, completed;
            private JournalEntry? validated;
            internal TaskReservation(OperationJournal owner, TaskCredit task, TaskMetadataKind kind)
            { this.owner = owner; this.task = task; this.kind = kind; }

            private void RequireAvailable()
            {
                if (disposed || queued || completed || kind == TaskMetadataKind.Revise && (task.Stopped || task.StopQueued || task.StopClaim != null))
                    throw new InvalidOperationException("Task journal credit unavailable.");
            }
            private void Validate(JournalEntry entry)
            {
                RequireAvailable();
                string? id = kind == TaskMetadataKind.Create && entry is TaskCreated created ? created.TaskId :
                    kind == TaskMetadataKind.Revise && entry is TaskGoalRevised revised ? revised.TaskId :
                    kind == TaskMetadataKind.Stop && entry is TaskStopped stopped ? stopped.TaskId : null;
                if (id != task.TaskId) throw new ArgumentException("Entry does not match the reserved task decision.", nameof(entry));
                if (!ReferenceEquals(validated, entry)) owner.ValidateEntry(entry);
                validated = entry;
            }
            public void ValidateEntry(JournalEntry entry) { lock (owner.sync) Validate(entry); }
            public Task<JournalTaskReceipt> AppendAsync(JournalEntry entry)
            {
                lock (owner.sync)
                {
                    if (owner.stopping || !owner.healthy) return Task.FromException<JournalTaskReceipt>(new JournalPersistenceException());
                    Validate(entry);
                    var work = new TaskMetadataWork(this, entry);
                    queued = true; validated = null;
                    if (kind == TaskMetadataKind.Create) task.CreateQueued = true;
                    else if (kind == TaskMetadataKind.Stop) task.StopQueued = true;
                    owner.queue.Enqueue(work); owner.signal.Release();
                    return work.Completion.Task;
                }
            }
            public void Finish(JournalSlot slot, bool persisted)
            {
                completed = true; validated = null;
                if (kind == TaskMetadataKind.Stop)
                {
                    owner.ReleaseStopCredit(task); task.Stopped = persisted; task.StopClaim = null;
                }
                else
                {
                    owner.ReleaseMetadataDisk(); owner.metadataReservations--; task.Ordinary = null;
                }
            }
            public void Dispose()
            {
                lock (owner.sync) { if (disposed) return; disposed = true; AbandonUnqueued(); }
            }
            internal void AbandonUnqueued()
            {
                if (queued || completed) return;
                completed = true; validated = null;
                if (kind == TaskMetadataKind.Stop) task.StopClaim = null;
                else
                {
                    owner.ReleaseMetadataDisk(); owner.metadataReservations--; task.Ordinary = null;
                    if (kind == TaskMetadataKind.Create) { owner.ReleaseStopCredit(task); owner.taskCredits.Remove(task.TaskId); }
                }
            }
        }
    }
}
