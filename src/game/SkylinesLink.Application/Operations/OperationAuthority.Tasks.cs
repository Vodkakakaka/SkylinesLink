using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkylinesLink.Application.Operations
{
    public sealed partial class OperationAuthority
    {
        private readonly Dictionary<string, TaskEntry> tasks = new Dictionary<string, TaskEntry>(StringComparer.Ordinal);
        private HashSet<TaskEntry>? taskRetentionRisk;
        private string? taskProjectionStore;
        private long taskProjectionSequence;
        private bool IsMixedJournal => journal.Recovery.JournalSchemaRevision == 3 && journal is ITaskJournal port && port.SupportsTaskMetadata;
        private JournalFactsFoldResult FoldJournal(IReadOnlyList<JournalRecord> records) => journal.Recovery.JournalSchemaRevision == 3
            ? JournalFactsFold.FoldMixed(records) : JournalFactsFold.Fold(records);

        public TaskDecisionLookupSnapshot BeginCreateTask(SubmissionKey key, TaskStatedGoal goal, GrantSnapshot subject)
        {
            lock (sync) return TaskDecisionSnapshotForControl(AcceptTaskCreation(key, goal, subject), subject);
        }

        public Task<TaskDecisionLookupSnapshot> CreateTaskAsync(SubmissionKey key, TaskStatedGoal goal, GrantSnapshot subject)
        {
            lock (sync) return AwaitTaskDecisionAsync(AcceptTaskCreation(key, goal, subject), subject);
        }

        private TaskDecisionEntry AcceptTaskCreation(SubmissionKey key, TaskStatedGoal goal, GrantSnapshot subject)
        {
            Require(grants.Allows(subject, GrantScope.Control), "UNAUTHORIZED");
            if (byKey.TryGetValue(key, out var known))
            {
                var prior = RequireTaskDecision(known, "create"); RequireTaskOwner(prior.Task, subject);
                Require(prior.Decision.CommandHash == TaskCommandHash.Create(prior.Decision.Source, goal), "IDEMPOTENCY_CONFLICT");
                return prior;
            }
            RequireTaskOrdinaryAdmission(); var write = TaskWriteContext(key, subject);
            Require(tasks.Count < TaskJournalLimits.MaximumTasks, "BUSY");
            string id = Guid.NewGuid().ToString("N");
            var record = new TaskCreated(id, Decision(key, subject, write.Source, TaskCommandHash.Create(write.Source, goal)), goal);
            var reservation = ((ITaskJournal)journal).TryReserveTaskCreation(id); Require(reservation != null, "BUSY");
            var task = new TaskEntry(id, record.Decision.Source.CityId!, record.Decision.Actor);
            return AdmitTaskOrdinary(task, record, "create", 1, 1, write, subject, reservation!);
        }

        public TaskDecisionLookupSnapshot BeginReviseTaskGoal(SubmissionKey key, string taskId, long expectedGoalRevision,
            long expectedControlVersion, TaskStatedGoal goal, GrantSnapshot subject)
        {
            lock (sync) return TaskDecisionSnapshotForControl(AcceptTaskGoalRevision(key, taskId,
                expectedGoalRevision, expectedControlVersion, goal, subject), subject);
        }

        public Task<TaskDecisionLookupSnapshot> ReviseTaskGoalAsync(SubmissionKey key, string taskId, long expectedGoalRevision,
            long expectedControlVersion, TaskStatedGoal goal, GrantSnapshot subject)
        {
            lock (sync) return AwaitTaskDecisionAsync(AcceptTaskGoalRevision(key, taskId,
                expectedGoalRevision, expectedControlVersion, goal, subject), subject);
        }

        private TaskDecisionEntry AcceptTaskGoalRevision(SubmissionKey key, string taskId, long expectedGoalRevision,
            long expectedControlVersion, TaskStatedGoal goal, GrantSnapshot subject)
        {
            Require(grants.Allows(subject, GrantScope.Control), "UNAUTHORIZED");
            if (byKey.TryGetValue(key, out var known))
            {
                var prior = RequireTaskDecision(known, "reviseGoal"); RequireTaskOwner(prior.Task, subject);
                Require(prior.Task.Id == taskId && prior.Decision.CommandHash == TaskCommandHash.Revise(taskId,
                    expectedGoalRevision, expectedControlVersion, prior.Decision.Source, goal), "IDEMPOTENCY_CONFLICT");
                return prior;
            }
            RequireTaskOrdinaryAdmission(); ReconcileTasks(); var task = FindTask(taskId); RequireTaskOwner(task, subject);
            Require(!task.StopRequested && task.Facts?.IsStopped != true, "TASK_STOPPED");
            Require(task.Ordinary == null, "BUSY");
            Require(task.Facts != null && task.Facts.GoalRevision == expectedGoalRevision && task.Facts.ControlVersion == expectedControlVersion &&
                expectedGoalRevision < AuthorizationState.MaximumCounter, "TASK_VERSION_CONFLICT");
            var write = TaskWriteContext(key, subject); Require(write.Source.CityId == task.CityId, "STALE_CONTEXT");
            var record = new TaskGoalRevised(taskId, expectedGoalRevision, expectedGoalRevision + 1, expectedControlVersion,
                Decision(key, subject, write.Source, TaskCommandHash.Revise(taskId, expectedGoalRevision, expectedControlVersion, write.Source, goal)), goal);
            var reservation = ((ITaskJournal)journal).TryReserveTaskRevision(taskId); Require(reservation != null, "BUSY");
            return AdmitTaskOrdinary(task, record, "reviseGoal", record.GoalRevision, expectedControlVersion, write, subject, reservation!);
        }

        public TaskDecisionLookupSnapshot BeginStopTask(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            lock (sync) return TaskDecisionSnapshotForControl(AcceptTaskStop(taskId, expectedControlVersion, subject), subject);
        }

        public Task<TaskDecisionLookupSnapshot> StopTaskAsync(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            lock (sync) return AwaitTaskDecisionAsync(AcceptTaskStop(taskId, expectedControlVersion, subject), subject);
        }

        // 传输在占用受保护回复额度前核资格；不latch、不预留journal，也不授予后续Begin权限。
        public void ValidateTaskStop(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            lock (sync) PrepareTaskStop(taskId, expectedControlVersion, subject, out _, out _);
        }

        // owned回复出队时重取原决定和当前Control；不重调Stop，也不给Control-only主体通用Read。
        public TaskDecisionLookupSnapshot RefreshTaskStopReceipt(SubmissionKey key, GrantSnapshot subject)
        {
            lock (sync)
            {
                TaskDecisionLookupSnapshot result = null!;
                Require(grants.TryCommit(subject, GrantScope.Control, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    ReconcileTasks();
                    if (!byKey.TryGetValue(key, out var known)) { result = MissingTaskDecision(key); return; }
                    var prior = RequireTaskDecision(known, "stop"); RequireTaskOwner(prior.Task, subject);
                    result = DecisionSnapshot(prior);
                }), "UNAUTHORIZED");
                return result;
            }
        }

        private TaskDecisionEntry AcceptTaskStop(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            var task = PrepareTaskStop(taskId, expectedControlVersion, subject, out var key, out var prior);
            if (prior != null) return prior;
            var record = new TaskStopped(taskId, expectedControlVersion, expectedControlVersion + 1,
                Decision(key, subject, context.Read(), TaskCommandHash.Stop(taskId, expectedControlVersion)));
            var reservation = ((ITaskJournal)journal).TryReserveTaskStop(taskId);
            try
            {
                reservation?.ValidateEntry(record);
                TaskDecisionEntry? accepted = null;
                Require(grants.TryCommit(subject, GrantScope.Control, () =>
                {
                    task.StopRequested = true;
                    accepted = new TaskDecisionEntry(task, record, "stop", null, record.ControlVersion, reservation);
                    byKey.Add(key, new Submission(accepted)); task.LastStop = accepted;
                    if (reservation == null) { accepted.Durability = "unavailable"; task.PersistenceFailed = true; }
                    else { task.Stop = accepted; accepted.Work = PersistTaskDecisionAsync(accepted); }
                }), "UNAUTHORIZED");
                return accepted!;
            }
            catch { reservation?.Dispose(); throw; }
        }

        private TaskEntry PrepareTaskStop(string taskId, long expectedControlVersion, GrantSnapshot subject,
            out SubmissionKey key, out TaskDecisionEntry? prior)
        {
            Require(grants.Allows(subject, GrantScope.Control), "UNAUTHORIZED");
            Require(IsMixedJournal, "TASKS_UNAVAILABLE");
            ReconcileTasks(); var task = FindTask(taskId); RequireTaskOwner(task, subject);
            key = TaskCommandHash.StopKey(taskId, expectedControlVersion);
            if (byKey.TryGetValue(key, out var known))
            {
                prior = RequireTaskDecision(known, "stop");
                Require(prior.Task.Id == taskId && prior.Decision.CommandHash == TaskCommandHash.Stop(taskId, expectedControlVersion), "IDEMPOTENCY_CONFLICT");
                return task;
            }
            // 已有历史决定的原key不能被规范命名策略改写。
            if (task.LastStop?.Record is TaskStopped historical && historical.ExpectedControlVersion == expectedControlVersion)
            { prior = task.LastStop; return task; }
            Require(complete && journal.Recovery.DurableConfirmed && !stopping, "JOURNAL_UNAVAILABLE");
            Require(task.Facts?.IsStopped != true && !task.StopRequested, "TASK_STOPPED");
            Require((task.Facts?.ControlVersion ?? 1) == expectedControlVersion && expectedControlVersion < AuthorizationState.MaximumCounter,
                "TASK_VERSION_CONFLICT");
            prior = null;
            return task;
        }

        private TaskDecisionEntry AdmitTaskOrdinary(TaskEntry task, JournalEntry record, string kind, long goal,
            long control, WriteContext write, GrantSnapshot subject, ITaskJournalReservation reservation)
        {
            try
            {
                reservation.ValidateEntry(record); TaskDecisionEntry? accepted = null;
                Require(grants.TryCommit(subject, GrantScope.Control, () =>
                {
                    if (record is TaskCreated) tasks.Add(task.Id, task);
                    accepted = new TaskDecisionEntry(task, record, kind, goal, control, reservation);
                    byKey.Add(accepted.Decision.Key, new Submission(accepted)); task.Ordinary = accepted; write.Requests++;
                    accepted.Work = PersistTaskDecisionAsync(accepted);
                }), "UNAUTHORIZED");
                return accepted!;
            }
            catch { reservation.Dispose(); throw; }
        }

        private async Task PersistTaskDecisionAsync(TaskDecisionEntry decision)
        {
            try
            {
                decision.PersistenceAttempted = true;
                var receipt = await decision.Reservation!.AppendAsync(decision.Record).ConfigureAwait(false);
                lock (sync)
                {
                    decision.Position = receipt; decision.Durability = "durable";
                    ReconcileTasks(); // 同批续体可反序；只从唯一已确认全局前缀归并，不用旧Goal清latch。
                }
            }
            catch (JournalPersistenceException) { lock (sync) { decision.Durability = "unavailable"; decision.Task.PersistenceFailed = true; } }
            finally
            {
                lock (sync)
                {
                    decision.Reservation?.Dispose(); decision.Reservation = null;
                    if (ReferenceEquals(decision.Task.Ordinary, decision)) decision.Task.Ordinary = null;
                    if (ReferenceEquals(decision.Task.Stop, decision)) decision.Task.Stop = null;
                }
            }
        }

        private async Task<TaskDecisionLookupSnapshot> AwaitTaskDecisionAsync(TaskDecisionEntry decision, GrantSnapshot subject)
        {
            await decision.Work.ConfigureAwait(false);
            lock (sync) return TaskDecisionSnapshotForControl(decision, subject);
        }

        // 受理和持久工作不因撤权回滚；每次交出变更回执仍核当前Control，不能借Read代替。
        private TaskDecisionLookupSnapshot TaskDecisionSnapshotForControl(TaskDecisionEntry decision, GrantSnapshot subject)
        {
            TaskDecisionLookupSnapshot result = null!;
            Require(grants.TryCommit(subject, GrantScope.Control, () =>
            {
                ReconcileTasks();
                result = byKey.TryGetValue(decision.Decision.Key, out var current) && current.Metadata != null
                    ? DecisionSnapshot(current.Metadata) : MissingTaskDecision(decision.Decision.Key);
            }), "UNAUTHORIZED");
            return result;
        }

        private TaskDecisionLookupSnapshot MissingTaskDecision(SubmissionKey key)
        {
            if (complete && journal.IsHealthy && tasks.Values.Any(task => task.Facts != null && !task.StopRequested &&
                !task.Facts.IsStopped && TaskCommandHash.StopKey(task.Id, task.Facts.ControlVersion).Equals(key)))
                return new TaskDecisionLookupSnapshot("absent", key);
            var missing = Missing(key); return new TaskDecisionLookupSnapshot(missing.Kind, key, reason: missing.Reason);
        }
        private TaskDecisionLookupSnapshot DecisionSnapshot(TaskDecisionEntry decision)
        {
            var receipt = Receipt(decision);
            return new TaskDecisionLookupSnapshot(receipt.Durability == "durable" ? "found" : receipt.Durability,
                decision.Decision.Key, receipt, receipt.Durability == "unavailable" ? "JOURNAL_UNAVAILABLE" : null);
        }
        private TaskMutationReceipt Receipt(TaskDecisionEntry decision) => new TaskMutationReceipt(decision.Kind, decision.Task.Id, decision.Decision,
            decision.GoalRevision, decision.ControlVersion,
            taskRetentionRisk?.Contains(decision.Task) == true ? "unavailable" : decision.Durability, decision.Position, decision.PersistenceAttempted);

        private void RequireTaskOrdinaryAdmission()
        {
            Require(IsMixedJournal, "TASKS_UNAVAILABLE");
            Require(complete && recoveryReady && journal.IsHealthy && !stopping, "JOURNAL_UNAVAILABLE");
            Require(!retentionGate, retentionFailed ? "JOURNAL_UNAVAILABLE" : "BUSY");
        }
        private WriteContext TaskWriteContext(SubmissionKey key, GrantSnapshot subject)
        {
            Require(contexts.TryGetValue(key.WriteContextId, out var write), "CONTEXT_UNKNOWN");
            Require(write!.Deadline > clock(), "CONTEXT_EXPIRED");
            Require(SameSubject(write.Subject, subject) && SameContext(write.Source, context.Read()), "STALE_CONTEXT");
            Require(write.Source.State == "ready" && write.Source.CityId != null, "CONTEXT_UNAVAILABLE");
            Require(write.Requests < OperationLimits.MaximumRequestsPerContext, "BUSY"); return write;
        }
        private static TaskDecision Decision(SubmissionKey key, GrantSnapshot subject, Context.WorldContextSnapshot source, string hash) =>
            new TaskDecision(key, new TaskActor(subject.ProfileId, subject.ProfileInstanceId, subject.AuthorityEpoch, subject.Epoch), source, hash);
        private static TaskDecisionEntry RequireTaskDecision(Submission known, string kind)
        { Require(known.Metadata != null && known.Metadata.Kind == kind, "IDEMPOTENCY_CONFLICT"); return known.Metadata!; }
        private TaskEntry FindTask(string taskId)
        { Require(tasks.TryGetValue(taskId, out var task), retentionApplied ? "RETENTION_NOT_COVERED" : "TASK_UNKNOWN"); return task!; }
        private static void RequireTaskOwner(TaskEntry task, GrantSnapshot subject) =>
            Require(task.Owner.ProfileId == subject.ProfileId && task.Owner.ProfileInstanceId == subject.ProfileInstanceId, "TASK_OWNER_MISMATCH");

        private void RestoreTasks(JournalFactsFoldResult folded)
        {
            if (journal.Recovery.JournalSchemaRevision != 3) return;
            foreach (var facts in folded.Tasks)
            {
                var task = new TaskEntry(facts.TaskId, facts.CityId, facts.Owner) { Facts = facts, StopRequested = facts.IsStopped,
                    PersistenceFailed = !journal.Recovery.DurableConfirmed };
                tasks.Add(task.Id, task);
                foreach (var item in facts.Decisions)
                {
                    var metadata = FromPersisted(task, item.Entry);
                    metadata.Durability = journal.Recovery.DurableConfirmed ? "durable" : "unavailable";
                    metadata.PersistenceAttempted = true;
                    if (journal.Recovery.DurableConfirmed && folded.StoreId != null) metadata.Position = new JournalTaskReceipt(folded.StoreId, item.Sequence);
                    byKey.Add(item.Decision.Key, new Submission(metadata)); if (item.Entry is TaskStopped) task.LastStop = metadata;
                }
            }
            taskProjectionStore = folded.StoreId; taskProjectionSequence = folded.ThroughSequence;
        }
        private static TaskDecisionEntry FromPersisted(TaskEntry task, JournalEntry entry) => entry is TaskCreated
            ? new TaskDecisionEntry(task, entry, "create", 1, 1, null) : entry is TaskGoalRevised revise
            ? new TaskDecisionEntry(task, entry, "reviseGoal", revise.GoalRevision, revise.ExpectedControlVersion, null)
            : new TaskDecisionEntry(task, entry, "stop", null, ((TaskStopped)entry).ControlVersion, null);

        private void ReconcileTasks()
        {
            if (!IsMixedJournal) return;
            var prefix = journal.CaptureConfirmedPrefix();
            if (prefix == null || prefix.JournalSchemaRevision != 3 || !(prefix.Records[0].Entry is StoreOpened header)) return;
            if (header.StoreId == taskProjectionStore && prefix.ThroughSequence <= taskProjectionSequence) return;
            var folded = JournalFactsFold.FoldMixed(prefix.Records);
            if (!folded.AllRecordsValid) throw new JournalPersistenceException(new InvalidOperationException("Confirmed mixed task prefix invalid."));
            foreach (var facts in folded.Tasks)
            {
                if (!tasks.TryGetValue(facts.TaskId, out var task)) continue;
                task.Facts = facts; if (facts.IsStopped) task.StopRequested = true;
                foreach (var row in facts.Decisions)
                    if (byKey.TryGetValue(row.Decision.Key, out var known) && known.Metadata != null)
                    { known.Metadata.Position = new JournalTaskReceipt(header.StoreId, row.Sequence); known.Metadata.Durability = "durable"; }
            }
            taskProjectionStore = header.StoreId; taskProjectionSequence = prefix.ThroughSequence;
        }
        private Task DrainTaskDecisionsAsync()
        { lock (sync) return Task.WhenAll(byKey.Values.Where(item => item.Metadata != null).Select(item => item.Metadata!.Work).ToArray()); }
        private bool TaskDecisionsQuiescent() => byKey.Values.All(item => item.Metadata == null ||
            item.Metadata.Reservation == null && item.Metadata.Work.IsCompleted);

        private bool CanRetireTask(TaskEntry task) => task.Facts?.IsStopped == true && !task.PersistenceFailed &&
            task.Ordinary == null && task.Stop == null && task.Facts.Decisions.All(row =>
                !contexts.ContainsKey(row.Decision.Key.WriteContextId) && byKey.TryGetValue(row.Decision.Key, out var known) &&
                known.Metadata?.Durability == "durable" && known.Metadata.Work.IsCompleted);
        private void RemoveRetiredTask(TaskEntry task)
        {
            tasks.Remove(task.Id);
            foreach (var key in byKey.Where(item => ReferenceEquals(item.Value.Metadata?.Task, task)).Select(item => item.Key).ToArray()) byKey.Remove(key);
        }

        private sealed class Submission
        {
            internal Submission(Entry operation) { Operation = operation; }
            internal Submission(TaskDecisionEntry metadata) { Metadata = metadata; }
            internal readonly Entry? Operation;
            internal readonly TaskDecisionEntry? Metadata;
        }
        private sealed class TaskEntry
        {
            internal TaskEntry(string id, string cityId, TaskActor owner) { Id = id; CityId = cityId; Owner = owner; }
            internal readonly string Id, CityId;
            internal readonly TaskActor Owner;
            internal JournalTaskFacts? Facts;
            internal bool StopRequested, PersistenceFailed;
            internal TaskDecisionEntry? Ordinary, Stop, LastStop;
        }
        private sealed class TaskDecisionEntry
        {
            internal TaskDecisionEntry(TaskEntry task, JournalEntry record, string kind, long? goalRevision, long controlVersion, ITaskJournalReservation? reservation)
            {
                Task = task; Record = record; Kind = kind; GoalRevision = goalRevision; ControlVersion = controlVersion; Reservation = reservation;
                Decision = record is TaskCreated create ? create.Decision : record is TaskGoalRevised revise ? revise.Decision : ((TaskStopped)record).Decision;
            }
            internal readonly TaskEntry Task;
            internal readonly JournalEntry Record;
            internal readonly TaskDecision Decision;
            internal readonly string Kind;
            internal readonly long? GoalRevision;
            internal readonly long ControlVersion;
            internal ITaskJournalReservation? Reservation;
            internal Task Work = System.Threading.Tasks.Task.CompletedTask;
            internal string Durability = "pending";
            internal JournalTaskReceipt? Position;
            internal bool PersistenceAttempted;
        }
    }
}
