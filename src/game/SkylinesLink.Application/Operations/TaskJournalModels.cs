using System;
using System.Collections.Generic;
using System.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Application.Operations
{
    /// <summary>仅调用方陈述；不是已解析范围、玩家授权、资金资格、执行计划或验收通过。</summary>
    public sealed class TaskStatedGoal
    {
        public TaskStatedGoal(string title, string requestText, IReadOnlyList<string> constraintsText, IReadOnlyList<string> acceptanceText)
        {
            TaskJournalValidation.Text(title, 128); TaskJournalValidation.Text(requestText, 2048);
            Title = title; RequestText = requestText;
            ConstraintsText = Copy(constraintsText); AcceptanceText = Copy(acceptanceText);
        }
        public string Title { get; }
        public string RequestText { get; }
        public IReadOnlyList<string> ConstraintsText { get; }
        public IReadOnlyList<string> AcceptanceText { get; }
        private static IReadOnlyList<string> Copy(IReadOnlyList<string> values)
        {
            if (values == null || values.Count > 16) throw new ArgumentException("Task stated text list is invalid.");
            var copy = values.ToArray();
            if (copy.Length > 16) throw new ArgumentException("Task stated text list is invalid.");
            foreach (string value in copy) TaskJournalValidation.Text(value, 256);
            return Array.AsReadOnly(copy);
        }
    }

    public sealed class TaskActor
    {
        public TaskActor(string profileId, string profileInstanceId, long authorityEpoch, long grantEpoch)
        {
            AuthorizationState.ValidateIdentifier(profileId); AuthorizationState.ValidateProfileInstanceId(profileInstanceId);
            AuthorizationState.ValidateCounter(authorityEpoch); AuthorizationState.ValidateCounter(grantEpoch);
            ProfileId = profileId; ProfileInstanceId = profileInstanceId; AuthorityEpoch = authorityEpoch; GrantEpoch = grantEpoch;
        }
        public string ProfileId { get; }
        public string ProfileInstanceId { get; }
        public long AuthorityEpoch { get; }
        public long GrantEpoch { get; }
    }

    public sealed class TaskDecision
    {
        public TaskDecision(SubmissionKey key, TaskActor actor, WorldContextSnapshot source, string commandHash)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key)); Actor = actor ?? throw new ArgumentNullException(nameof(actor));
            if (!TaskJournalValidation.Context(source)) throw new ArgumentException("Task decision source is invalid.", nameof(source));
            TaskJournalValidation.Digest(commandHash);
            Source = source; CommandHash = commandHash;
        }
        public SubmissionKey Key { get; }
        public TaskActor Actor { get; }
        public WorldContextSnapshot Source { get; }
        public string CommandHash { get; }
    }

    public sealed class TaskCreated : JournalEntry
    {
        public TaskCreated(string taskId, TaskDecision decision, TaskStatedGoal goal)
        { TaskJournalValidation.Text(taskId, 128); TaskId = taskId; Decision = decision ?? throw new ArgumentNullException(nameof(decision)); Goal = goal ?? throw new ArgumentNullException(nameof(goal)); }
        public string TaskId { get; }
        public TaskDecision Decision { get; }
        public TaskStatedGoal Goal { get; }
    }
    public sealed class TaskGoalRevised : JournalEntry
    {
        public TaskGoalRevised(string taskId, long expectedGoalRevision, long goalRevision, long expectedControlVersion, TaskDecision decision, TaskStatedGoal goal)
        {
            TaskJournalValidation.Text(taskId, 128); AuthorizationState.ValidateCounter(expectedGoalRevision);
            AuthorizationState.ValidateCounter(goalRevision); AuthorizationState.ValidateCounter(expectedControlVersion);
            TaskId = taskId; ExpectedGoalRevision = expectedGoalRevision; GoalRevision = goalRevision; ExpectedControlVersion = expectedControlVersion;
            Decision = decision ?? throw new ArgumentNullException(nameof(decision)); Goal = goal ?? throw new ArgumentNullException(nameof(goal));
        }
        public string TaskId { get; }
        public long ExpectedGoalRevision { get; }
        public long GoalRevision { get; }
        public long ExpectedControlVersion { get; }
        public TaskDecision Decision { get; }
        public TaskStatedGoal Goal { get; }
    }
    public sealed class TaskStopped : JournalEntry
    {
        public TaskStopped(string taskId, long expectedControlVersion, long controlVersion, TaskDecision decision)
        {
            TaskJournalValidation.Text(taskId, 128); AuthorizationState.ValidateCounter(expectedControlVersion); AuthorizationState.ValidateCounter(controlVersion);
            TaskId = taskId; ExpectedControlVersion = expectedControlVersion; ControlVersion = controlVersion;
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }
        public string TaskId { get; }
        public long ExpectedControlVersion { get; }
        public long ControlVersion { get; }
        public TaskDecision Decision { get; }
    }

    public sealed class TaskGoalRevisionFacts
    {
        internal TaskGoalRevisionFacts(long goalRevision, long sequence, TaskDecision decision, TaskStatedGoal goal)
        { GoalRevision = goalRevision; Sequence = sequence; Decision = decision; Goal = goal; }
        public long GoalRevision { get; }
        public long Sequence { get; }
        public TaskDecision Decision { get; }
        public TaskStatedGoal Goal { get; }
    }
    public sealed class TaskDecisionFacts
    {
        internal TaskDecisionFacts(long sequence, JournalEntry entry, TaskDecision decision)
        { Sequence = sequence; Entry = entry; Decision = decision; }
        public long Sequence { get; }
        public JournalEntry Entry { get; }
        public TaskDecision Decision { get; }
    }
    public sealed class JournalTaskFacts
    {
        internal JournalTaskFacts(string taskId, string cityId, TaskActor owner, long goalRevision, long controlVersion, bool isStopped,
            IReadOnlyList<TaskGoalRevisionFacts> goals, IReadOnlyList<TaskDecisionFacts> decisions)
        {
            TaskId = taskId; CityId = cityId; Owner = owner; GoalRevision = goalRevision; ControlVersion = controlVersion; IsStopped = isStopped;
            GoalHistory = Array.AsReadOnly(goals.ToArray()); Decisions = Array.AsReadOnly(decisions.ToArray());
            CurrentGoal = GoalHistory[GoalHistory.Count - 1].Goal;
        }
        public string TaskId { get; }
        public string CityId { get; }
        public TaskActor Owner { get; }
        public TaskStatedGoal CurrentGoal { get; }
        public long GoalRevision { get; }
        public long ControlVersion { get; }
        public bool IsStopped { get; }
        public IReadOnlyList<TaskGoalRevisionFacts> GoalHistory { get; }
        public IReadOnlyList<TaskDecisionFacts> Decisions { get; }
    }

    public static class TaskJournalLimits
    {
        public const int MaximumTasks = TaskJournalRules.MaximumTasks;
        public const int MaximumPendingMetadata = 2;
        public const int MaximumMetadataPayload = 96 * 1024;
        public const int MaximumMetadataFrame = MaximumMetadataPayload + 44;
        public static void ValidateTaskId(string taskId) => TaskJournalValidation.Text(taskId, 128);
    }

    internal static class TaskJournalValidation
    {
        internal static void Text(string value, int maximum)
        {
            if (!TaskJournalRules.IsTextValid(value, maximum)) throw new ArgumentException("Task text scalar bound is invalid.");
        }
        internal static void Digest(string value)
        {
            if (value == null || value.Length != 64 || value.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("Task command hash must be lowercase SHA256.");
        }
        internal static bool Context(WorldContextSnapshot? value) => value != null && TaskJournalRules.IsSourceValid(TaskCommandHash.SourceView(value));
    }
}
