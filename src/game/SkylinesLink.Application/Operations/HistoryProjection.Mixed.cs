using System;
using System.Collections.Generic;
using System.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Operations
{
    public sealed partial class HistoryProjection
    {
        private HistoryProjection(HistorySourceSnapshot source, HistoryRecordSnapshot[] records,
            JournalRecord[] taskRecords, JournalTaskFacts[] tasks)
        {
            Source = source;
            Records = Array.AsReadOnly(records);
            TaskRecords = Array.AsReadOnly(taskRecords);
            Tasks = Array.AsReadOnly(tasks);
        }

        /// <summary>原混合日志中的完整Task决定，保留原全局序号；不是当前现场或待执行计划。</summary>
        public IReadOnlyList<JournalRecord> TaskRecords { get; }
        public IReadOnlyList<JournalTaskFacts> Tasks { get; }

        public TaskStopHistorySelection SelectTaskStopDecision(string taskId, long expectedControlVersion)
        {
            if (Source.FormatVersion != 2 || Source.JournalSchemaRevision != 3)
                throw new OperationRejectedException("HISTORY_UNAVAILABLE");
            TaskCommandHash.Stop(taskId, expectedControlVersion);
            var selected = TaskRecords.FirstOrDefault(row => row.Entry is TaskStopped stop &&
                stop.TaskId == taskId && stop.ExpectedControlVersion == expectedControlVersion);
            return new TaskStopHistorySelection(Source, selected);
        }

        public OperationHistoryCheckpointV2 ToContractV2()
        {
            if (Source.FormatVersion != 2 || Source.JournalSchemaRevision != 3)
                throw new NotSupportedException("operation-history-checkpoint:2 requires a mixed journal source.");
            return new OperationHistoryCheckpointV2
            {
                Source = Source.ToContractV2(),
                Records = Records.Select(record => record.ToContract()).ToList(),
                TaskRecords = TaskRecords.Select(TaskHistoryContractMapper.ToContract).ToList()
            };
        }

        public static HistoryProjection FromMixedConfirmedPrefix(ConfirmedJournalPrefix prefix, string environment,
            string version, string instance, string coreHash, long publishedAtUnixMs)
        {
            if (prefix == null) throw new ArgumentNullException(nameof(prefix));
            if (prefix.JournalSchemaRevision != 3)
                throw new NotSupportedException("operation-history-checkpoint:2 requires journal business family 3.");
            if (!prefix.ThroughBytes.HasValue)
                throw new ArgumentException("Mixed history requires actual confirmed frame bytes.", nameof(prefix));

            var folded = JournalFactsFold.FoldMixed(prefix.Records);
            if (!folded.AllRecordsValid || folded.ValidRecordCount < 1 || folded.ValidRecordCount != prefix.Records.Count ||
                folded.StoreId == null || folded.EnvironmentId != environment || folded.ThroughSequence != prefix.ThroughSequence)
                throw new ArgumentException("Mixed history source prefix invalid.", nameof(prefix));

            // The full prefix is checked before selecting its two projections.
            // Never compact metadata sequence numbers or fabricate omitted Load stages.
            var value = new OperationHistoryCheckpointV2
            {
                Source = new HistorySourceV2
                {
                    FormatVersion = 2, ProductId = "skylineslink", EnvironmentId = environment,
                    StoreId = folded.StoreId, ThroughSequence = prefix.ThroughSequence,
                    ProducerVersion = version, ProducerInstanceId = instance,
                    ProducerCoreManifestSha256 = coreHash, JournalSchemaRevision = 3,
                    PublishedAtUnixMs = publishedAtUnixMs,
                    Selection = HistorySourceSnapshot.MixedSelection, ThroughBytes = prefix.ThroughBytes.Value
                },
                Records = folded.Operations.Where(operation => operation.Final != null)
                    .OrderBy(operation => operation.FinalSequence)
                    .Select(HistoryRecordSnapshot.FromOperationFacts).ToList(),
                TaskRecords = prefix.Records.Where(record => TaskJournalReducer.IsMetadata(record.Entry))
                    .Select(TaskHistoryContractMapper.ToContract).ToList()
            };
            return FromContract(value);
        }

        public static HistoryProjection FromContract(OperationHistoryCheckpointV2 value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Source == null || value.Records == null || value.TaskRecords == null || value.Records.Count > OperationLimits.MaximumRetained ||
                value.TaskRecords.Count >= OperationLimits.MaximumFrames)
                throw new ArgumentException("Mixed history row bound invalid.", nameof(value));
            // Includes the source's independent 2 MiB byte ceiling, closed rows
            // and the complete three-way metadata union before semantic replay.
            ContractCodec.EncodeOperationHistoryCheckpointV2(value);
            var source = new HistorySourceSnapshot(value.Source);
            if (source.ThroughSequence > OperationLimits.MaximumFrames || !source.ThroughBytes.HasValue ||
                source.ThroughBytes.Value < 45L * source.ThroughSequence)
                throw new ArgumentException("Mixed history confirmed source bound invalid.", nameof(value));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<SubmissionKey>();
            var sequences = new HashSet<long>();
            var records = value.Records.Select(record => new HistoryRecordSnapshot(record)).ToArray();
            foreach (var record in records)
            {
                if (record.TerminalSequence <= 1 || record.TerminalSequence > source.ThroughSequence ||
                    !ids.Add(record.OperationId) || !keys.Add(record.Key) || !sequences.Add(record.TerminalSequence))
                    throw new ArgumentException("Mixed history operation identity invalid.", nameof(value));
            }

            var taskRecords = new JournalRecord[value.TaskRecords.Count];
            var reducer = new TaskJournalReducer();
            long previousSequence = 1;
            for (int index = 0; index < taskRecords.Length; index++)
            {
                var row = value.TaskRecords[index];
                if (row.Sequence <= previousSequence || row.Sequence > source.ThroughSequence ||
                    row.EnvironmentId != source.EnvironmentId || !sequences.Add(row.Sequence))
                    throw new ArgumentException("Mixed history metadata source or sequence invalid.", nameof(value));
                var record = TaskHistoryContractMapper.FromContract(row);
                if (!reducer.Apply(record, keys))
                    throw new ArgumentException("Mixed history task decision chain invalid.", nameof(value));
                taskRecords[index] = record;
                previousSequence = row.Sequence;
            }
            return new HistoryProjection(source, records, taskRecords, reducer.Snapshot());
        }
    }

    public sealed class TaskStopHistorySelection
    {
        internal TaskStopHistorySelection(HistorySourceSnapshot source, JournalRecord? record)
        { Source = source; Record = record; }
        public HistorySourceSnapshot Source { get; }
        public JournalRecord? Record { get; }
    }
}
