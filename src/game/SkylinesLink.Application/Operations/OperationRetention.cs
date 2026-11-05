using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkylinesLink.Application.Operations
{
    public enum OperationRetentionOutcome { Busy, Unsupported, NoEligible, Installed, CurrentPreserved }

    public sealed partial class OperationAuthority
    {
        private bool retentionGate, retentionFailed, retentionApplied;
        private Task<OperationRetentionOutcome>? retentionWork;
        private IJournalRotation? retentionRotation;
        private HashSet<Entry>? retentionRisk;

        /// <summary>显式内部维护，不由查询或断线触发。只规划已终结义务，原writer执行唯一持久交接。</summary>
        public Task<OperationRetentionOutcome> TryRetireAsync()
        {
            lock (sync)
            {
                if (retentionWork != null && (!retentionWork.IsCompleted || retentionFailed)) return retentionWork;
                if (stopping || !complete || !recoveryReady || !journal.IsHealthy || !RetentionQuiescent())
                    return Task.FromResult(OperationRetentionOutcome.Busy);
                var position = journal.CaptureRotationPosition();
                if (position == null) return Task.FromResult(OperationRetentionOutcome.Busy);
                if (!position.SupportsRotation) return Task.FromResult(OperationRetentionOutcome.Unsupported);
                retentionGate = true;
                // 先登记门与owned Task；delegate首段也取sync，系统进程观察在短锁外执行。
                return retentionWork = Task.Run(RetireCoreAsync);
            }
        }

        private bool RetentionQuiescent() => active == 0 && TaskDecisionsQuiescent() && (recoverySealing == null || recoverySealing.IsCompleted) &&
            entries.Values.All(entry => entry.Reservation == null && entry.AcceptedWork.IsCompleted &&
                (entry.CommitWork == null || entry.CommitWork.IsCompleted) &&
                (entry.NativeWork == null || entry.NativeWork.IsCompleted) &&
                (entry.FinalWork == null || entry.FinalWork.IsCompleted));

        private async Task<OperationRetentionOutcome> RetireCoreAsync()
        {
            bool ioOwned = false;
            try
            {
                KeyValuePair<Entry, NativeHostIdentity>[] fences;
                lock (sync)
                {
                    if (stopping || !RetentionQuiescent()) return OperationRetentionOutcome.Busy;
                    fences = entries.Values.Where(entry => entry.RecoveredFence && entry.NativeHost != null)
                        .Select(entry => new KeyValuePair<Entry, NativeHostIdentity>(entry, entry.NativeHost!)).ToArray();
                }
                var observations = new NativeHostState[fences.Length];
                for (int i = 0; i < fences.Length; i++) observations[i] = nativeHost.Observe(fences[i].Value);

                Entry[] retiring;
                TaskEntry[] retiringTasks;
                JournalRotationPlan plan;
                IJournalRotation work;
                lock (sync)
                {
                    for (int i = 0; i < fences.Length; i++)
                    {
                        var proof = fences[i];
                        if (observations[i] == NativeHostState.Ended && proof.Key.RecoveredFence && proof.Key.NativeHost!.Equals(proof.Value) &&
                            entries.TryGetValue(proof.Key.Intent.OperationId, out var same) && ReferenceEquals(same, proof.Key))
                            proof.Key.RecoveredFence = false; // 只解除物理义务，不补原生终结、返回值或历史成功。
                    }
                    if (stopping || !RetentionQuiescent() || !journal.IsHealthy) return OperationRetentionOutcome.Busy;
                    var prefix = journal.CaptureConfirmedPrefix();
                    var position = journal.CaptureRotationPosition();
                    if (prefix == null || position == null || !position.SupportsRotation) return OperationRetentionOutcome.Busy;
                    var folded = FoldJournal(prefix.Records);
                    if (!folded.AllRecordsValid || folded.StoreId != position.StoreId || folded.ThroughSequence != position.ThroughSequence ||
                        folded.Operations.Count != entries.Count || folded.Operations.Any(facts => !entries.ContainsKey(facts.Intent.OperationId)) ||
                        folded.Tasks.Count != tasks.Count || folded.Tasks.Any(facts => !tasks.ContainsKey(facts.TaskId)))
                        return OperationRetentionOutcome.Busy;
                    ReconcileTasks();

                    long now = clock(); var source = context.Read();
                    foreach (var closed in contexts.Values.Where(write => write.Requests >= OperationLimits.MaximumRequestsPerContext ||
                        !IsCurrentContext(write, now, source)).ToArray())
                        contexts.Remove(closed.Id); // 额度耗尽本已不可受理；issued/其他有效context/Requests均不重建。
                    var candidates = new List<Entry>(OperationLimits.MaximumRetained);
                    foreach (var facts in folded.Operations)
                    {
                        var entry = entries[facts.Intent.OperationId];
                        if (facts.Final != null && facts.FinalSequence <= prefix.ThroughSequence && entry.Finished && entry.Accepted &&
                            !entry.PersistenceFailed && entry.Durability == "durable" && !Fenced(entry) &&
                            !contexts.ContainsKey(entry.Intent.Key.WriteContextId)) candidates.Add(entry);
                    }
                    retiringTasks = tasks.Values.Where(CanRetireTask).ToArray();
                    if (candidates.Count == 0 && retiringTasks.Length == 0) return OperationRetentionOutcome.NoEligible;
                    retiring = candidates.ToArray();
                    retentionRisk = new HashSet<Entry>(retiring);
                    taskRetentionRisk = new HashSet<TaskEntry>(retiringTasks);
                    plan = new JournalRotationPlan(position.StoreId, position.ThroughSequence,
                        folded.Operations.Where(facts => !retentionRisk.Contains(entries[facts.Intent.OperationId]))
                            .Select(facts => facts.Intent.OperationId).ToArray(),
                        folded.Tasks.Where(facts => !taskRetentionRisk.Contains(tasks[facts.TaskId])).Select(facts => facts.TaskId).ToArray());
                    var acquired = journal.TryStartRotation(plan);
                    if (acquired == null) return OperationRetentionOutcome.Busy; // 契约保证未开始此轮IO，finally可撤risk。
                    retentionRotation = work = acquired;
                    ioOwned = true;
                }

                JournalRotationResult result = await work.Completion.ConfigureAwait(false);
                lock (sync)
                {
                    if (result.Outcome == JournalRotationOutcome.Installed)
                    {
                        var installed = FoldJournal(result.Prefix.Records);
                        if (!installed.AllRecordsValid || installed.StoreId == plan.ExpectedStoreId || !result.RetentionApplied ||
                            !new HashSet<string>(installed.Operations.Select(facts => facts.Intent.OperationId), StringComparer.Ordinal)
                                .SetEquals(plan.RetainedOperationIds) ||
                            !new HashSet<string>(installed.Tasks.Select(facts => facts.TaskId), StringComparer.Ordinal).SetEquals(plan.RetainedTaskIds))
                            throw new JournalPersistenceException(new InvalidOperationException("Rotation installation does not match its frozen plan."));
                        foreach (var entry in retiring)
                        { entries.Remove(entry.Intent.OperationId); byKey.Remove(entry.Intent.Key); }
                        foreach (var task in retiringTasks) RemoveRetiredTask(task);
                        retentionApplied = result.RetentionApplied;
                        recoveryEntries = Array.Empty<Entry>(); // 恢复封存已真实结束，不再持旧Entry作为第二索引。
                        ReconcileTasks(); // 使用writer当前水位；迟到rotation续体不能覆盖已经ACK的Stop。
                        return OperationRetentionOutcome.Installed;
                    }
                    if (result.Outcome != JournalRotationOutcome.CurrentPreserved ||
                        !(result.Prefix.Records[0].Entry is StoreOpened old) || old.StoreId != plan.ExpectedStoreId ||
                        result.Prefix.ThroughSequence != plan.ExpectedThroughSequence)
                        throw new JournalPersistenceException(new InvalidOperationException("Rotation did not confirm the original source."));
                    return OperationRetentionOutcome.CurrentPreserved;
                }
            }
            catch
            {
                lock (sync) if (ioOwned) retentionFailed = true;
                throw;
            }
            finally
            {
                lock (sync)
                {
                    if (!retentionFailed) { retentionGate = false; retentionRisk = null; taskRetentionRisk = null; retentionRotation = null; }
                }
            }
        }

        private static OperationLookupSnapshot RetentionNotCovered() => new OperationLookupSnapshot("unavailable", reason: "RETENTION_NOT_COVERED");
    }
}
