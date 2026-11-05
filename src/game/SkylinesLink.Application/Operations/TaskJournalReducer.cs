using System;
using System.Collections.Generic;
using System.Linq;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Application.Operations
{
    /// <summary>同一全局序列中的纯Task reducer；不持有权限、writer、队列或可自行推进的任务。</summary>
    internal sealed class TaskJournalReducer
    {
        private readonly Dictionary<string, Accumulator> entries = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        private readonly List<Accumulator> ordered = new List<Accumulator>();
        internal static bool IsMetadata(JournalEntry entry) => entry is TaskCreated || entry is TaskGoalRevised || entry is TaskStopped;
        internal bool Apply(JournalRecord record, HashSet<SubmissionKey> globalKeys)
        {
            if (record.SchemaRevision != 3) return false;
            TaskTransitionInput input;
            TaskDecision decision;
            if (record.Entry is TaskCreated created)
            {
                if (entries.Count >= TaskJournalLimits.MaximumTasks) return false;
                decision = created.Decision;
                input = Input(TaskDecisionKind.Create, created.TaskId, decision, 0, 1, 0, 1, TaskCommandHash.GoalView(created.Goal));
            }
            else if (record.Entry is TaskGoalRevised revised)
            {
                decision = revised.Decision;
                input = Input(TaskDecisionKind.ReviseGoal, revised.TaskId, decision, revised.ExpectedGoalRevision, revised.GoalRevision,
                    revised.ExpectedControlVersion, revised.ExpectedControlVersion, TaskCommandHash.GoalView(revised.Goal));
            }
            else if (record.Entry is TaskStopped stopped)
            {
                decision = stopped.Decision;
                input = Input(TaskDecisionKind.Stop, stopped.TaskId, decision, 0, 0, stopped.ExpectedControlVersion, stopped.ControlVersion, null);
            }
            else return false;
            if (globalKeys.Contains(decision.Key)) return false;
            entries.TryGetValue(input.TaskId, out var current);
            if (!TaskJournalRules.TryTransition(current?.State, input, out var next)) return false;
            if (record.Entry is TaskCreated accepted)
            {
                var added = new Accumulator(accepted, record.Sequence, next);
                entries.Add(accepted.TaskId, added); ordered.Add(added);
            }
            else
            {
                if (record.Entry is TaskGoalRevised goal)
                    current!.Goals.Add(new TaskGoalRevisionFacts(goal.GoalRevision, record.Sequence, decision, goal.Goal));
                current!.Decisions.Add(new TaskDecisionFacts(record.Sequence, record.Entry, decision)); current.State = next;
            }
            globalKeys.Add(decision.Key); return true;
        }
        private static TaskTransitionInput Input(TaskDecisionKind kind, string taskId, TaskDecision decision, long expectedGoal, long goal,
            long expectedControl, long control, TaskGoalView? statedGoal) => new TaskTransitionInput(kind, taskId,
                new TaskOwnerView(decision.Actor.ProfileId, decision.Actor.ProfileInstanceId, decision.Actor.AuthorityEpoch, decision.Actor.GrantEpoch),
                TaskCommandHash.SourceView(decision.Source), decision.CommandHash, expectedGoal, goal, expectedControl, control, statedGoal);
        internal JournalTaskFacts[] Snapshot() => ordered.Select(item => new JournalTaskFacts(item.State.TaskId, item.State.CityId, item.Owner,
            item.State.GoalRevision, item.State.ControlVersion, item.State.IsStopped, item.Goals, item.Decisions)).ToArray();
        private sealed class Accumulator
        {
            internal Accumulator(TaskCreated created, long sequence, TaskStateView state)
            {
                State = state; Owner = created.Decision.Actor;
                Goals.Add(new TaskGoalRevisionFacts(1, sequence, created.Decision, created.Goal));
                Decisions.Add(new TaskDecisionFacts(sequence, created, created.Decision));
            }
            internal readonly TaskActor Owner;
            internal TaskStateView State;
            internal readonly List<TaskGoalRevisionFacts> Goals = new List<TaskGoalRevisionFacts>();
            internal readonly List<TaskDecisionFacts> Decisions = new List<TaskDecisionFacts>();
        }
    }
}
