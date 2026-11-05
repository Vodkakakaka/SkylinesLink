using System;
using System.Collections.Generic;
using System.Linq;

namespace SkylinesLink.Application.Operations
{
    /// <summary>固定原决定的逻辑结果；Journal是当前保留副本，不冒充永久不变的首次ACK位置。</summary>
    public sealed class TaskMutationReceipt
    {
        internal TaskMutationReceipt(string kind, string taskId, TaskDecision decision, long? goalRevision, long controlVersion,
            string durability, JournalTaskReceipt? journal, bool persistenceAttempted)
        { Kind = kind; TaskId = taskId; Decision = decision; GoalRevision = goalRevision; ControlVersion = controlVersion;
            Durability = durability; Journal = journal; PersistenceAttempted = persistenceAttempted; }
        public string Kind { get; }
        public string TaskId { get; }
        public TaskDecision Decision { get; }
        public long? GoalRevision { get; }
        public long ControlVersion { get; }
        public string Durability { get; }
        public JournalTaskReceipt? Journal { get; }
        public bool PersistenceAttempted { get; }
    }
    public sealed class TaskDecisionLookupSnapshot
    {
        internal TaskDecisionLookupSnapshot(string kind, SubmissionKey key, TaskMutationReceipt? receipt = null, string? reason = null)
        { Kind = kind; Key = key; Receipt = receipt; Reason = reason; }
        public string Kind { get; }
        public SubmissionKey Key { get; }
        public TaskMutationReceipt? Receipt { get; }
        public string? Reason { get; }
    }
    /// <summary>有界摘要；正文和目标历史通过指定revision的分字段页读取。</summary>
    public sealed class TaskLookupSnapshot
    {
        internal TaskLookupSnapshot(string kind, string taskId, string? cityId = null, TaskActor? owner = null, string? title = null,
            long? goalRevision = null, long? controlVersion = null, bool isStopped = false, bool stopRequested = false,
            string durability = "unavailable", JournalTaskReceipt? journal = null, TaskMutationReceipt? ordinary = null,
            TaskMutationReceipt? stop = null, string? reason = null)
        { Kind = kind; TaskId = taskId; CityId = cityId; Owner = owner; Title = title; GoalRevision = goalRevision;
            ControlVersion = controlVersion; IsStopped = isStopped; StopRequested = stopRequested; Durability = durability;
            Journal = journal; PendingOrdinary = ordinary; PendingStop = stop; Reason = reason; }
        public string Kind { get; }
        public string TaskId { get; }
        public string? CityId { get; }
        public TaskActor? Owner { get; }
        public string? Title { get; }
        public long? GoalRevision { get; }
        public long? ControlVersion { get; }
        public bool IsStopped { get; }
        public bool StopRequested { get; }
        public string Durability { get; }
        public JournalTaskReceipt? Journal { get; }
        public TaskMutationReceipt? PendingOrdinary { get; }
        public TaskMutationReceipt? PendingStop { get; }
        public string? Reason { get; }
    }
    public enum TaskGoalSection { Title, RequestText, ConstraintsText, AcceptanceText }
    public sealed class TaskGoalPage
    {
        internal TaskGoalPage(string kind, string taskId, long revision, TaskGoalSection section, int offset, int? nextOffset = null,
            string? text = null, IReadOnlyList<string>? items = null, JournalTaskReceipt? journal = null, string durability = "unavailable", string? reason = null)
        { Kind = kind; TaskId = taskId; GoalRevision = revision; Section = section; Offset = offset; NextOffset = nextOffset;
            Text = text; Items = Array.AsReadOnly(items?.ToArray() ?? Array.Empty<string>()); Journal = journal; Durability = durability; Reason = reason; }
        public string Kind { get; }
        public string TaskId { get; }
        public long GoalRevision { get; }
        public TaskGoalSection Section { get; }
        public int Offset { get; }
        public int? NextOffset { get; }
        public string? Text { get; }
        public IReadOnlyList<string> Items { get; }
        public JournalTaskReceipt? Journal { get; }
        public string Durability { get; }
        public string? Reason { get; }
    }
}
