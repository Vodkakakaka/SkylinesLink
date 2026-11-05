using System;
using System.Globalization;
using System.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Contracts;
using ContractTaskDecision = SkylinesLink.Contracts.TaskDecision;

namespace SkylinesLink.Application.Operations
{
    // A value-only projection. This mapper neither invents journal stages nor
    // retains mutable generated DTOs behind the published history snapshot.
    internal static class TaskHistoryContractMapper
    {
        internal static TaskMetadataRow ToContract(JournalRecord record)
        {
            if (record.SchemaRevision != 3)
                throw new ArgumentException("Task metadata requires record revision 3.", nameof(record));
            TaskMetadataEntry entry;
            switch (record.Entry)
            {
                case TaskCreated created:
                    entry = new TaskMetadataEntryTaskCreated
                    {
                        Kind = "taskCreated", TaskId = created.TaskId, GoalRevision = 1, ControlVersion = 1,
                        Decision = WriteDecision(created.Decision), Goal = WriteGoal(created.Goal)
                    };
                    break;
                case TaskGoalRevised revised:
                    entry = new TaskMetadataEntryTaskGoalRevised
                    {
                        Kind = "taskGoalRevised", TaskId = revised.TaskId,
                        ExpectedGoalRevision = revised.ExpectedGoalRevision, GoalRevision = revised.GoalRevision,
                        ExpectedControlVersion = revised.ExpectedControlVersion,
                        Decision = WriteDecision(revised.Decision), Goal = WriteGoal(revised.Goal)
                    };
                    break;
                case TaskStopped stopped:
                    entry = new TaskMetadataEntryTaskStopped
                    {
                        Kind = "taskStopped", TaskId = stopped.TaskId, ExpectedControlVersion = stopped.ExpectedControlVersion,
                        ControlVersion = stopped.ControlVersion, Decision = WriteDecision(stopped.Decision)
                    };
                    break;
                default: throw new ArgumentException("History row is not Task metadata.", nameof(record));
            }
            return new TaskMetadataRow { Sequence = record.Sequence, EnvironmentId = record.EnvironmentId, Entry = entry };
        }

        // The enclosing generated CP2 contract is checked before this method.
        internal static JournalRecord FromContract(TaskMetadataRow row)
        {
            JournalEntry entry;
            switch (row.Entry)
            {
                case TaskMetadataEntryTaskCreated created:
                    entry = new TaskCreated(created.TaskId, ReadDecision(created.Decision), ReadGoal(created.Goal));
                    break;
                case TaskMetadataEntryTaskGoalRevised revised:
                    entry = new TaskGoalRevised(revised.TaskId, revised.ExpectedGoalRevision, revised.GoalRevision,
                        revised.ExpectedControlVersion, ReadDecision(revised.Decision), ReadGoal(revised.Goal));
                    break;
                case TaskMetadataEntryTaskStopped stopped:
                    entry = new TaskStopped(stopped.TaskId, stopped.ExpectedControlVersion, stopped.ControlVersion,
                        ReadDecision(stopped.Decision));
                    break;
                default: throw new ArgumentException("History row is not Task metadata.", nameof(row));
            }
            return new JournalRecord(row.Sequence, row.EnvironmentId, entry, 3);
        }

        internal static ContractTaskDecision WriteDecision(TaskDecision value) => new ContractTaskDecision
        {
            SubmissionKey = new OperationSubmissionKey { WriteContextId = value.Key.WriteContextId, RequestId = value.Key.RequestId },
            Actor = new TaskDecisionActor
            {
                ProfileId = value.Actor.ProfileId, ProfileInstanceId = value.Actor.ProfileInstanceId,
                AuthorityEpoch = value.Actor.AuthorityEpoch, GrantEpoch = value.Actor.GrantEpoch
            },
            Source = HistoryRecordSnapshot.WriteContext(value.Source), CommandHash = value.CommandHash
        };

        private static TaskDecision ReadDecision(ContractTaskDecision value) => new TaskDecision(
            new SubmissionKey(value.SubmissionKey.WriteContextId, value.SubmissionKey.RequestId),
            new TaskActor(value.Actor.ProfileId, value.Actor.ProfileInstanceId, value.Actor.AuthorityEpoch, value.Actor.GrantEpoch),
            ReadContext(value.Source), value.CommandHash);

        private static TaskStatedIntent WriteGoal(TaskStatedGoal value) => new TaskStatedIntent
        {
            Kind = "statedIntent", Title = value.Title, RequestText = value.RequestText,
            ConstraintsText = value.ConstraintsText.ToList(), AcceptanceText = value.AcceptanceText.ToList()
        };

        private static TaskStatedGoal ReadGoal(TaskStatedIntent value) => new TaskStatedGoal(
            value.Title, value.RequestText, value.ConstraintsText, value.AcceptanceText);

        private static WorldContextSnapshot ReadContext(HistoryContext value)
        {
            if (!long.TryParse(value.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation < 0 ||
                generation.ToString(CultureInfo.InvariantCulture) != value.Generation)
                throw new ArgumentException("Task history source generation invalid.", nameof(value));
            return new WorldContextSnapshot(value.InstanceId, generation, value.GameState, value.CityId);
        }
    }
}
