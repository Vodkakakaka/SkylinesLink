using System;
using System.Collections.Generic;
using System.Linq;

namespace SkylinesLink.Application.Operations
{
    public sealed partial class OperationAuthority
    {
        public TaskLookupSnapshot LookupTask(string taskId, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.TryRead(subject, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    ReconcileTasks();
                    if (!tasks.TryGetValue(taskId, out var task))
                    { var missing = Missing(null); return new TaskLookupSnapshot(missing.Kind, taskId, reason: missing.Reason); }
                    return TaskSummarySnapshot(task);
                }, out TaskLookupSnapshot result), "UNAUTHORIZED");
                return result;
            }
        }

        public IReadOnlyList<TaskLookupSnapshot> LookupTasks(GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.TryRead<IReadOnlyList<TaskLookupSnapshot>>(subject, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    Require(complete, "JOURNAL_UNAVAILABLE");
                    ReconcileTasks();
                    return Array.AsReadOnly(tasks.Values.OrderBy(task => task.Id, StringComparer.Ordinal)
                        .Select(TaskSummarySnapshot).ToArray());
                }, out var result), "UNAUTHORIZED");
                return result;
            }
        }

        private TaskLookupSnapshot TaskSummarySnapshot(TaskEntry task)
        {
            var facts = task.Facts;
            var last = facts?.Decisions.LastOrDefault();
            var evidence = last == null ? null : byKey[last.Decision.Key].Metadata;
            var position = evidence?.Position;
            return new TaskLookupSnapshot(facts == null ? "pending" : "found", task.Id, task.CityId, task.Owner, facts?.CurrentGoal.Title,
                facts?.GoalRevision, facts?.ControlVersion, facts?.IsStopped ?? false, task.StopRequested,
                task.PersistenceFailed || taskRetentionRisk?.Contains(task) == true ? "unavailable" : facts == null ? "pending" :
                    position == null ? "unavailable" : evidence!.Durability, position,
                task.Ordinary == null ? null : Receipt(task.Ordinary), task.Stop != null ? Receipt(task.Stop) :
                    task.LastStop?.Durability == "unavailable" ? Receipt(task.LastStop) : null);
        }

        public TaskDecisionLookupSnapshot LookupTaskDecision(SubmissionKey key, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.TryRead(subject, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    ReconcileTasks(); if (!byKey.TryGetValue(key, out var known)) return MissingTaskDecision(key);
                    Require(known.Metadata != null, "COMMAND_KIND_MISMATCH"); return DecisionSnapshot(known.Metadata!);
                }, out TaskDecisionLookupSnapshot result), "UNAUTHORIZED");
                return result;
            }
        }

        public TaskDecisionLookupSnapshot LookupTaskStopDecision(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.TryRead(subject, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    var selectorKey = TaskCommandHash.StopKey(taskId, expectedControlVersion);
                    ReconcileTasks();
                    if (!tasks.TryGetValue(taskId, out var task))
                        return new TaskDecisionLookupSnapshot("unavailable", selectorKey,
                            reason: retentionApplied ? "RETENTION_NOT_COVERED" : "STOP_NOT_COVERED");
                    // 按实际逻辑决定选择原key；不能把当前命名策略当历史记录的身份。
                    if (task.LastStop?.Record is TaskStopped stopped && stopped.ExpectedControlVersion == expectedControlVersion &&
                        byKey.TryGetValue(stopped.Decision.Key, out var known) && known.Metadata != null &&
                        ReferenceEquals(known.Metadata.Task, task)) return DecisionSnapshot(known.Metadata);
                    return new TaskDecisionLookupSnapshot("unavailable", selectorKey,
                        reason: complete && journal.IsHealthy && journal.Recovery.DurableConfirmed ? "STOP_NOT_COVERED" : "JOURNAL_UNAVAILABLE");
                }, out TaskDecisionLookupSnapshot result), "UNAUTHORIZED");
                return result;
            }
        }

        public TaskStopHistorySelection ReadTaskStopHistory(string taskId, long expectedControlVersion, GrantSnapshot subject)
        {
            Require(grants.TryRead(subject, () =>
            {
                var published = history?.Current;
                if (published == null) throw new OperationRejectedException("HISTORY_UNAVAILABLE");
                return published.SelectTaskStopDecision(taskId, expectedControlVersion);
            }, out TaskStopHistorySelection selected), "UNAUTHORIZED");
            return selected;
        }

        public TaskGoalPage ReadTaskGoal(string taskId, long goalRevision, TaskGoalSection section, int offset, int limit, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.TryRead(subject, () =>
                {
                    Require(IsMixedJournal, "TASKS_UNAVAILABLE");
                    AuthorizationState.ValidateCounter(goalRevision);
                    bool text = section == TaskGoalSection.Title || section == TaskGoalSection.RequestText;
                    Require(Enum.IsDefined(typeof(TaskGoalSection), section) && offset >= 0 && limit >= 1 && limit <= (text ? 512 : 4), "INVALID_ARGUMENT");
                    ReconcileTasks();
                    if (!tasks.TryGetValue(taskId, out var task))
                    { var missing = Missing(null); return new TaskGoalPage(missing.Kind, taskId, goalRevision, section, offset, reason: missing.Reason); }
                    var facts = task.Facts?.GoalHistory.FirstOrDefault(goal => goal.GoalRevision == goalRevision);
                    if (facts == null)
                    {
                        var known = byKey.Values.Select(item => item.Metadata).FirstOrDefault(item => item != null &&
                            ReferenceEquals(item.Task, task) && item.GoalRevision == goalRevision);
                        string state = known?.Durability == "pending" ? "pending" : known?.Durability == "unavailable" ||
                            !complete || !journal.Recovery.DurableConfirmed || !journal.IsHealthy ? "unavailable" : "absent";
                        return new TaskGoalPage(state, taskId, goalRevision, section, offset,
                            reason: state == "unavailable" ? "JOURNAL_UNAVAILABLE" : null);
                    }
                    var decision = byKey[facts.Decision.Key].Metadata!; var receipt = Receipt(decision);
                    if (!text)
                    {
                        var values = section == TaskGoalSection.ConstraintsText ? facts.Goal.ConstraintsText : facts.Goal.AcceptanceText;
                        Require(offset <= values.Count, "INVALID_ARGUMENT");
                        int count = Math.Min(limit, values.Count - offset); int end = offset + count;
                        return new TaskGoalPage("found", taskId, goalRevision, section, offset, end < values.Count ? end : (int?)null,
                            items: values.Skip(offset).Take(count).ToArray(), journal: receipt.Journal, durability: receipt.Durability);
                    }
                    string value = section == TaskGoalSection.Title ? facts.Goal.Title : facts.Goal.RequestText;
                    int scalar = 0, begin = -1, finish = -1;
                    for (int index = 0; index < value.Length; scalar++)
                    {
                        if (scalar == offset) begin = index;
                        if (scalar == offset + limit) finish = index;
                        index += char.IsHighSurrogate(value[index]) ? 2 : 1;
                    }
                    Require(offset <= scalar, "INVALID_ARGUMENT");
                    if (begin < 0) begin = value.Length;
                    if (finish < 0) finish = value.Length;
                    return new TaskGoalPage("found", taskId, goalRevision, section, offset, offset + limit < scalar ? offset + limit : (int?)null,
                        value.Substring(begin, finish - begin), journal: receipt.Journal, durability: receipt.Durability);
                }, out TaskGoalPage result), "UNAUTHORIZED");
                return result;
            }
        }
    }
}
