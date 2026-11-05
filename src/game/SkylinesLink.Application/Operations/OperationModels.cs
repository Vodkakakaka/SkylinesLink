using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Operations
{
    public sealed class SubmissionKey : IEquatable<SubmissionKey>
    {
        public SubmissionKey(string writeContextId, string requestId)
        {
            // 复用同源标量字符串规则；不把 UTF-16 Length 或空白规则叠成第二套 wire 限制。
            var input = new JObject { ["selector"] = new JObject { ["kind"] = "submissionKey", ["submissionKey"] =
                new JObject { ["writeContextId"] = writeContextId, ["requestId"] = requestId } } };
            if (!ContractCatalog.Validate(SchemaIds.OperationQueryV2, input).Valid) throw new ArgumentException("Invalid submission identity.");
            WriteContextId = writeContextId; RequestId = requestId;
        }
        public string WriteContextId { get; }
        public string RequestId { get; }
        public bool Equals(SubmissionKey? other) => other != null && WriteContextId == other.WriteContextId && RequestId == other.RequestId;
        public override bool Equals(object? value) => Equals(value as SubmissionKey);
        public override int GetHashCode() { unchecked { return (WriteContextId.GetHashCode() * 397) ^ RequestId.GetHashCode(); } }
    }

    public sealed class NativeHostIdentity : IEquatable<NativeHostIdentity>
    {
        public NativeHostIdentity(int processId, long creationTime)
        { if (processId < 1 || creationTime < 1) throw new ArgumentOutOfRangeException(nameof(processId)); ProcessId = processId; CreationTime = creationTime; }
        public int ProcessId { get; }
        public long CreationTime { get; }
        public bool Equals(NativeHostIdentity? other) => other != null && ProcessId == other.ProcessId && CreationTime == other.CreationTime;
        public override bool Equals(object? value) => Equals(value as NativeHostIdentity);
        public override int GetHashCode() { unchecked { return (ProcessId * 397) ^ CreationTime.GetHashCode(); } }
    }
    public enum NativeHostState { Alive, Ended, Unknown }
    public interface INativeHostIdentitySource
    {
        NativeHostIdentity Current { get; }
        NativeHostState Observe(NativeHostIdentity identity);
    }

    public sealed class OperationIntent
    {
        public OperationIntent(string operationId, string originInstanceId, SubmissionKey key, string commandHash,
            string profileId, long authorityEpoch, long grantEpoch, WorldContextSnapshot source, LoadTarget target, string? profileInstanceId = null)
        {
            if (profileInstanceId != null)
            {
                AuthorizationState.ValidateProfileInstanceId(profileInstanceId); AuthorizationState.ValidateIdentifier(profileId);
                AuthorizationState.ValidateCounter(authorityEpoch); AuthorizationState.ValidateCounter(grantEpoch);
            }
            OperationId = operationId; OriginInstanceId = originInstanceId; Key = key; CommandHash = commandHash;
            ProfileId = profileId; ProfileInstanceId = profileInstanceId; AuthorityEpoch = authorityEpoch; GrantEpoch = grantEpoch; Source = source; Target = target;
        }
        public string OperationId { get; }
        public string OriginInstanceId { get; }
        public SubmissionKey Key { get; }
        public string CommandHash { get; }
        public string ProfileId { get; }
        /// <summary>null只表示原记录未记载；不能从当前同名profile推断历史incarnation。</summary>
        public string? ProfileInstanceId { get; }
        public long AuthorityEpoch { get; }
        public long GrantEpoch { get; }
        public WorldContextSnapshot Source { get; }
        public LoadTarget Target { get; }
    }

    public sealed class PersistedLoadFacts
    {
        public PersistedLoadFacts(NativeLoadTaskState physicalTaskState, bool? loadReturnValue, LoadTargetMatch targetMatch, bool correlatedTargetReady,
            bool historicalCompletionProven, LoadObservationEnd observationEnd, WorldContextSnapshot? observationContext)
        { NativeLoadCompletion.CheckReturnValue(physicalTaskState, loadReturnValue); PhysicalTaskState = physicalTaskState; LoadReturnValue = loadReturnValue; TargetMatch = targetMatch; CorrelatedTargetReady = correlatedTargetReady;
            HistoricalCompletionProven = historicalCompletionProven; ObservationEnd = observationEnd; ObservationContext = observationContext; }
        public NativeLoadTaskState PhysicalTaskState { get; }
        public bool? LoadReturnValue { get; }
        public LoadTargetMatch TargetMatch { get; }
        public bool CorrelatedTargetReady { get; }
        public bool HistoricalCompletionProven { get; }
        public LoadObservationEnd ObservationEnd { get; }
        public WorldContextSnapshot? ObservationContext { get; }
        public static PersistedLoadFacts From(LoadTransitionSnapshot value) => new PersistedLoadFacts(value.PhysicalTaskState, value.LoadReturnValue, value.TargetMatch,
            value.CorrelatedTargetReady, value.HistoricalCompletionProven, value.ObservationEnd, value.ObservationContext);
    }

    public enum SubmissionKnowledge { NotSubmitted, Submitted, Unknown }
    public abstract class JournalEntry { }
    public sealed class StoreOpened : JournalEntry
    { public StoreOpened(string storeId) { StoreId = storeId; } public string StoreId { get; } }
    public sealed class OperationAccepted : JournalEntry
    { public OperationAccepted(OperationIntent intent) { Intent = intent; } public OperationIntent Intent { get; } }
    public sealed class CommitIntended : JournalEntry
    {
        public CommitIntended(string operationId, LoadTarget target, NativeHostIdentity nativeHost)
        { OperationId = operationId; Target = target; NativeHost = nativeHost; }
        public string OperationId { get; }
        public LoadTarget Target { get; }
        public NativeHostIdentity NativeHost { get; }
    }
    public sealed class NativeTerminated : JournalEntry
    {
        public NativeTerminated(string operationId, NativeLoadTaskState state, bool? loadReturnValue, LoadTarget? boundTarget)
        { var completion = new NativeLoadCompletion(state, loadReturnValue); OperationId = operationId; State = completion.PhysicalTaskState; LoadReturnValue = completion.LoadReturnValue; BoundTarget = boundTarget; }
        public string OperationId { get; }
        public NativeLoadTaskState State { get; }
        public bool? LoadReturnValue { get; }
        public LoadTarget? BoundTarget { get; }
    }
    public sealed class OperationFinished : JournalEntry
    {
        public OperationFinished(string operationId, string state, bool mayHaveApplied, SubmissionKnowledge submission,
            string reasonCode, PersistedLoadFacts load)
        { OperationId = operationId; State = state; MayHaveApplied = mayHaveApplied; Submission = submission; ReasonCode = reasonCode; Load = load; }
        public string OperationId { get; }
        public string State { get; }
        public bool MayHaveApplied { get; }
        public SubmissionKnowledge Submission { get; }
        public string ReasonCode { get; }
        public PersistedLoadFacts Load { get; }
    }
    public sealed class JournalRecord
    {
        public JournalRecord(long sequence, string environmentId, JournalEntry entry, int schemaRevision = 2)
        {
            if (schemaRevision != 2 && schemaRevision != 3) throw new ArgumentOutOfRangeException(nameof(schemaRevision));
            Sequence = sequence; EnvironmentId = environmentId; Entry = entry; SchemaRevision = schemaRevision;
        }
        public long Sequence { get; }
        public string EnvironmentId { get; }
        public JournalEntry Entry { get; }
        public int SchemaRevision { get; }
    }
    public sealed class JournalRecovery
    {
        public JournalRecovery(bool complete, IReadOnlyList<JournalRecord> records, bool durableConfirmed = true, bool incompatible = false, bool retentionApplied = false,
            int journalSchemaRevision = 2)
        {
            if (journalSchemaRevision != 2 && journalSchemaRevision != 3) throw new ArgumentOutOfRangeException(nameof(journalSchemaRevision));
            Complete = complete; Records = Array.AsReadOnly(records.ToArray()); DurableConfirmed = durableConfirmed;
            Incompatible = incompatible; RetentionApplied = retentionApplied; JournalSchemaRevision = journalSchemaRevision;
        }
        public bool Complete { get; }
        public bool DurableConfirmed { get; }
        public bool Incompatible { get; }
        public bool RetentionApplied { get; }
        public int JournalSchemaRevision { get; }
        public IReadOnlyList<JournalRecord> Records { get; }
    }
    /// <summary>单次水位对应的已确认不可变记录；不携带文件 EOF 或完整业务索引的断言。</summary>
    public sealed class ConfirmedJournalPrefix
    {
        public ConfirmedJournalPrefix(IReadOnlyList<JournalRecord> records, int count, int journalSchemaRevision = 2, long? throughBytes = null)
        {
            if (count < 1 || count > OperationLimits.MaximumFrames || count > records.Count) throw new ArgumentOutOfRangeException(nameof(count));
            if (journalSchemaRevision != 2 && journalSchemaRevision != 3) throw new ArgumentOutOfRangeException(nameof(journalSchemaRevision));
            if (throughBytes.HasValue && throughBytes.Value < 45L * count) throw new ArgumentOutOfRangeException(nameof(throughBytes));
            var copy = new JournalRecord[count];
            for (int i = 0; i < count; i++) copy[i] = records[i];
            Records = Array.AsReadOnly(copy); ThroughSequence = copy[count - 1].Sequence; JournalSchemaRevision = journalSchemaRevision; ThroughBytes = throughBytes;
        }
        public long ThroughSequence { get; }
        public int JournalSchemaRevision { get; }
        // Actual confirmed frame bytes in this store, excluding control slots,
        // unused capacity and rejected tails. Synthetic sources may be unknown.
        public long? ThroughBytes { get; }
        public IReadOnlyList<JournalRecord> Records { get; }
    }
    public enum JournalSlot { Normal, NativeTerminal, Terminal }
    /// <summary>日志端口无法确认本次持久结果；调用方保留既有事实，不据此重试写入。</summary>
    public sealed class JournalPersistenceException : Exception
    {
        public JournalPersistenceException(Exception? cause = null) : base("Journal persistence unavailable.", cause) { }
    }
    public interface IOperationJournalReservation : IDisposable
    { void ValidateEntry(JournalEntry entry); Task<long> AppendAsync(JournalEntry entry, JournalSlot slot); }
    public interface IRecoveryTerminalReservation : IDisposable
    { void ValidateEntry(OperationFinished entry); Task<long> AppendAsync(OperationFinished entry); }
    public interface IOperationJournal
    {
        JournalRecovery Recovery { get; }
        bool IsHealthy { get; }
        Task Completion { get; }
        ConfirmedJournalPrefix? CaptureConfirmedPrefix();
        JournalRotationPosition? CaptureRotationPosition();
        IJournalRotation? TryStartRotation(JournalRotationPlan plan);
        // 唯一旁路消费者只投递合并信号；不得在通知中做 IO、折叠或等待。
        IDisposable ObserveConfirmed(Action notification);
        IOperationJournalReservation? TryReserveOperation();
        IRecoveryTerminalReservation? TryReserveRecoveryTerminals(long expectedThroughSequence, int terminalCount);
        Task StopAsync();
    }
    public sealed class OperationLimits
    {
        public const int MaximumRetained = 32;
        public const int MaximumActive = 4;
        public const int MaximumContexts = 8;
        public const int MaximumRequestsPerContext = 8;
        public const int MaximumRecordPayload = 4096;
        public const int MaximumFrame = MaximumRecordPayload + 44;
        public const int MaximumJournalBytes = 1048576;
        public const int MaximumFrames = 129;
        public const int MaximumCommitBatchFrames = 4;
    }
    public sealed class OperationLookupSnapshot
    {
        public OperationLookupSnapshot(string kind, string? operationId = null, string? originInstanceId = null, string? state = null,
            bool mayHaveApplied = false, string? durability = null, PersistedLoadFacts? load = null, bool isObservationCurrent = false, string? reason = null)
        { Kind = kind; OperationId = operationId; OriginInstanceId = originInstanceId; State = state; MayHaveApplied = mayHaveApplied;
            Durability = durability; Load = load; IsObservationCurrent = isObservationCurrent; Reason = reason; }
        public string Kind { get; }
        public string? OperationId { get; }
        public string? OriginInstanceId { get; }
        public string? State { get; }
        public bool MayHaveApplied { get; }
        public string? Durability { get; }
        public PersistedLoadFacts? Load { get; }
        public bool IsObservationCurrent { get; }
        public string? Reason { get; }
    }
}
