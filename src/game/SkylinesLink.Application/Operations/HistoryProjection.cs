using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Operations
{
    public interface IHistoryProjectionSource { HistoryProjection? Current { get; } }

    public sealed class HistorySourceSnapshot
    {
        internal HistorySourceSnapshot(HistorySource value)
        {
            if (AuthorizationState.NormalizeEnvironment(value.EnvironmentId) != value.EnvironmentId) throw new ArgumentException("History environment invalid.");
            AuthorizationState.ValidateHex(value.ProducerCoreManifestSha256);
            EnvironmentId = value.EnvironmentId; StoreId = value.StoreId; ThroughSequence = value.ThroughSequence;
            ProducerVersion = value.ProducerVersion; ProducerInstanceId = value.ProducerInstanceId;
            ProducerCoreManifestSha256 = value.ProducerCoreManifestSha256; PublishedAtUnixMs = value.PublishedAtUnixMs;
            FormatVersion = 1; JournalSchemaRevision = 2;
        }
        internal HistorySourceSnapshot(HistorySourceV2 value)
        {
            if (AuthorizationState.NormalizeEnvironment(value.EnvironmentId) != value.EnvironmentId) throw new ArgumentException("History environment invalid.");
            AuthorizationState.ValidateHex(value.ProducerCoreManifestSha256);
            if (value.FormatVersion != 2 || value.JournalSchemaRevision != 3 || value.Selection != MixedSelection)
                throw new ArgumentException("Mixed history source invalid.");
            EnvironmentId = value.EnvironmentId; StoreId = value.StoreId; ThroughSequence = value.ThroughSequence;
            ProducerVersion = value.ProducerVersion; ProducerInstanceId = value.ProducerInstanceId;
            ProducerCoreManifestSha256 = value.ProducerCoreManifestSha256; PublishedAtUnixMs = value.PublishedAtUnixMs;
            FormatVersion = 2; JournalSchemaRevision = 3; ThroughBytes = value.ThroughBytes;
        }
        public const string MixedSelection = "confirmedTaskMetadataAndOperationTerminals";
        public int FormatVersion { get; }
        public int JournalSchemaRevision { get; }
        public long? ThroughBytes { get; }
        public string Selection => FormatVersion == 1 ? "durableTerminalOnly" : MixedSelection;
        public string EnvironmentId { get; }
        public string StoreId { get; }
        public long ThroughSequence { get; }
        public string ProducerVersion { get; }
        public string ProducerInstanceId { get; }
        public string ProducerCoreManifestSha256 { get; }
        public long PublishedAtUnixMs { get; }
        public long? AgeAt(long nowUnixMs) => nowUnixMs < PublishedAtUnixMs || nowUnixMs < 0 ||
            nowUnixMs > 9007199254740991L ? (long?)null : nowUnixMs - PublishedAtUnixMs;
        public HistorySource ToContract()
        {
            if (FormatVersion != 1) throw new NotSupportedException("History source cannot be represented by format1.");
            return new HistorySource { FormatVersion = 1, ProductId = "skylineslink", EnvironmentId = EnvironmentId,
            StoreId = StoreId, ThroughSequence = ThroughSequence, ProducerVersion = ProducerVersion, ProducerInstanceId = ProducerInstanceId,
            ProducerCoreManifestSha256 = ProducerCoreManifestSha256, JournalSchemaRevision = 2, PublishedAtUnixMs = PublishedAtUnixMs, Selection = "durableTerminalOnly" };
        }
        public HistorySourceV2 ToContractV2()
        {
            if (FormatVersion != 2 || !ThroughBytes.HasValue) throw new NotSupportedException("History source cannot be represented by format2.");
            return new HistorySourceV2 { FormatVersion = 2, ProductId = "skylineslink", EnvironmentId = EnvironmentId,
                StoreId = StoreId, ThroughSequence = ThroughSequence, ProducerVersion = ProducerVersion, ProducerInstanceId = ProducerInstanceId,
                ProducerCoreManifestSha256 = ProducerCoreManifestSha256, JournalSchemaRevision = 3, PublishedAtUnixMs = PublishedAtUnixMs,
                Selection = MixedSelection, ThroughBytes = ThroughBytes.Value };
        }
    }

    public sealed class HistoryRecordSnapshot
    {
        internal HistoryRecordSnapshot(HistoryRecord value)
        {
            TerminalSequence = value.TerminalSequence; Key = new SubmissionKey(value.SubmissionKey.WriteContextId, value.SubmissionKey.RequestId);
            OperationId = value.OperationId; OriginInstanceId = value.OriginInstanceId; SourceContext = ReadContext(value.SourceContext);
            State = value.State; MayHaveApplied = value.MayHaveApplied;
            var load = value.Load;
            Load = new PersistedLoadFacts(Parse<NativeLoadTaskState>(load.PhysicalTaskState), load.LoadReturnValue,
                Parse<LoadTargetMatch>(load.TargetMatch), load.CorrelatedTargetReady, load.HistoricalCompletionProven,
                Parse<LoadObservationEnd>(load.ObservationEnd), load.ObservationContext == null ? null : ReadContext(load.ObservationContext));
            if (load.IsObservationCurrent || OriginInstanceId != SourceContext.InstanceId ||
                !(SourceContext.State == "menu" ? SourceContext.CityId == null : SourceContext.State == "ready"))
                throw new ArgumentException("History context invalid.");
            var observed = Load.ObservationContext;
            if (observed != null && (observed.State != "ready" || observed.InstanceId != SourceContext.InstanceId))
                throw new ArgumentException("History observation invalid.");
            if ((Load.TargetMatch != LoadTargetMatch.Unknown && observed == null) ||
                ((Load.CorrelatedTargetReady || Load.HistoricalCompletionProven) && (observed == null || observed.Generation <= SourceContext.Generation)))
                throw new ArgumentException("History observation evidence invalid.");
            if (Load.HistoricalCompletionProven != (State == "succeeded") ||
                (Load.HistoricalCompletionProven && (Load.PhysicalTaskState != NativeLoadTaskState.RanToCompletion || Load.LoadReturnValue != true || !Load.CorrelatedTargetReady)))
                throw new ArgumentException("History completion evidence invalid.");
            bool valid;
            switch (Load.PhysicalTaskState)
            {
                case NativeLoadTaskState.NotStarted:
                    valid = !MayHaveApplied && (State == "failed" || State == "cancelled") && !Load.CorrelatedTargetReady && !Load.HistoricalCompletionProven; break;
                case NativeLoadTaskState.Unknown: valid = MayHaveApplied && State == "unknown"; break;
                case NativeLoadTaskState.RanToCompletion:
                    valid = MayHaveApplied && (Load.LoadReturnValue == false ? State == "failed" : Load.HistoricalCompletionProven ? State == "succeeded" : State == "unknown"); break;
                case NativeLoadTaskState.Faulted: valid = MayHaveApplied && State == "failed"; break;
                case NativeLoadTaskState.Cancelled: valid = MayHaveApplied && State == "cancelled"; break;
                default: valid = false; break;
            }
            if (!valid) throw new ArgumentException("History terminal facts invalid.");
        }
        public long TerminalSequence { get; }
        public SubmissionKey Key { get; }
        public string OperationId { get; }
        public string OriginInstanceId { get; }
        public WorldContextSnapshot SourceContext { get; }
        public string State { get; }
        public bool MayHaveApplied { get; }
        public PersistedLoadFacts Load { get; }
        public HistoryRecord ToContract() => new HistoryRecord { TerminalSequence = TerminalSequence,
            SubmissionKey = new OperationSubmissionKey { WriteContextId = Key.WriteContextId, RequestId = Key.RequestId }, OperationId = OperationId,
            OriginInstanceId = OriginInstanceId, SourceContext = WriteContext(SourceContext), State = State, MayHaveApplied = MayHaveApplied, Durability = "durable",
            Load = new HistoryLoad { PhysicalTaskState = Lower(Load.PhysicalTaskState), LoadReturnValue = Load.LoadReturnValue,
                TargetMatch = Lower(Load.TargetMatch), IsObservationCurrent = false, CorrelatedTargetReady = Load.CorrelatedTargetReady,
                HistoricalCompletionProven = Load.HistoricalCompletionProven, ObservationEnd = Lower(Load.ObservationEnd),
                ObservationContext = Load.ObservationContext == null ? null : WriteContext(Load.ObservationContext) } };
        internal static HistoryContext WriteContext(WorldContextSnapshot value) => new HistoryContext { InstanceId = value.InstanceId,
            Generation = value.Generation.ToString(CultureInfo.InvariantCulture), GameState = value.State, CityId = value.CityId };
        private static WorldContextSnapshot ReadContext(HistoryContext value)
        {
            if (!long.TryParse(value.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation < 0 ||
                generation.ToString(CultureInfo.InvariantCulture) != value.Generation)
                throw new ArgumentException("History generation invalid.");
            return new WorldContextSnapshot(value.InstanceId, generation, value.GameState, value.CityId);
        }
        private static T Parse<T>(string value) where T : struct => (T)Enum.Parse(typeof(T), char.ToUpperInvariant(value[0]) + value.Substring(1));
        internal static string Lower<T>(T value) => char.ToLowerInvariant(value!.ToString()![0]) + value.ToString()!.Substring(1);
        internal static HistoryRecord FromOperationFacts(JournalOperationFacts operation)
        {
            var intent = operation.Intent; var final = operation.Final ?? throw new ArgumentException("History requires terminal facts."); var facts = final.Load;
            return new HistoryRecord { TerminalSequence = operation.FinalSequence!.Value,
                SubmissionKey = new OperationSubmissionKey { WriteContextId = intent.Key.WriteContextId, RequestId = intent.Key.RequestId },
                OperationId = intent.OperationId, OriginInstanceId = intent.OriginInstanceId, SourceContext = WriteContext(intent.Source),
                State = final.State, MayHaveApplied = final.MayHaveApplied, Durability = "durable", Load = new HistoryLoad {
                    PhysicalTaskState = Lower(facts.PhysicalTaskState), LoadReturnValue = facts.LoadReturnValue,
                    TargetMatch = Lower(facts.TargetMatch), IsObservationCurrent = false, CorrelatedTargetReady = facts.CorrelatedTargetReady,
                    HistoricalCompletionProven = facts.HistoricalCompletionProven, ObservationEnd = Lower(facts.ObservationEnd),
                    ObservationContext = facts.ObservationContext == null ? null : WriteContext(facts.ObservationContext) } };
        }
    }

    public sealed class HistorySelection
    {
        internal HistorySelection(HistorySourceSnapshot source, HistoryRecordSnapshot? record) { Source = source; Record = record; }
        public HistorySourceSnapshot Source { get; }
        public HistoryRecordSnapshot? Record { get; }
    }

    /// <summary>一次已确认出版的不可变普通值；选中结果不保活整个记录数组。</summary>
    public sealed partial class HistoryProjection
    {
        private HistoryProjection(HistorySourceSnapshot source, HistoryRecordSnapshot[] records)
            : this(source, records, Array.Empty<JournalRecord>(), Array.Empty<JournalTaskFacts>()) { }
        public HistorySourceSnapshot Source { get; }
        public IReadOnlyList<HistoryRecordSnapshot> Records { get; }
        public HistorySelection Select(string operationId)
        { RequireLegacy(); return new HistorySelection(Source, Records.FirstOrDefault(record => record.OperationId == operationId)); }
        public HistorySelection Select(SubmissionKey key)
        { RequireLegacy(); return new HistorySelection(Source, Records.FirstOrDefault(record => record.Key.Equals(key))); }
        private void RequireLegacy()
        { if (Source.FormatVersion != 1) throw new NotSupportedException("Legacy history selection cannot represent a mixed source."); }
        public OperationHistoryCheckpoint ToContract() => new OperationHistoryCheckpoint { Source = Source.ToContract(), Records = Records.Select(record => record.ToContract()).ToList() };
        public static HistoryProjection FromContract(OperationHistoryCheckpoint value)
        {
            ContractCodec.EncodeOperationHistoryCheckpoint(value); // 同源结构先核；以下仅历史语义与不可变拷贝。
            var source = new HistorySourceSnapshot(value.Source);
            var ids = new HashSet<string>(StringComparer.Ordinal); var keys = new HashSet<SubmissionKey>(); var sequences = new HashSet<long>();
            var records = value.Records.Select(record => new HistoryRecordSnapshot(record)).ToArray();
            foreach (var record in records)
                if (record.TerminalSequence > source.ThroughSequence || !ids.Add(record.OperationId) || !keys.Add(record.Key) || !sequences.Add(record.TerminalSequence))
                    throw new ArgumentException("History record identity invalid.");
            return new HistoryProjection(source, records);
        }
        public static HistoryProjection FromConfirmedPrefix(ConfirmedJournalPrefix prefix, string environment, string version, string instance, string coreHash, long publishedAtUnixMs)
        {
            if (prefix.JournalSchemaRevision != 2)
                throw new NotSupportedException("operation-history-checkpoint:1 cannot represent this journal business family.");
            var folded = JournalFactsFold.Fold(prefix.Records);
            if (folded.ValidRecordCount < 1 || folded.StoreId == null || folded.EnvironmentId != environment || folded.ThroughSequence > prefix.ThroughSequence)
                throw new ArgumentException("History source prefix invalid.");
            var value = new OperationHistoryCheckpoint { Source = new HistorySource { FormatVersion = 1, ProductId = "skylineslink", EnvironmentId = environment,
                StoreId = folded.StoreId!, ThroughSequence = folded.ThroughSequence, ProducerVersion = version, ProducerInstanceId = instance,
                ProducerCoreManifestSha256 = coreHash, JournalSchemaRevision = 2, PublishedAtUnixMs = publishedAtUnixMs, Selection = "durableTerminalOnly" },
                Records = new List<HistoryRecord>() };
            foreach (var operation in folded.Operations.Where(item => item.Final != null).OrderBy(item => item.FinalSequence))
                value.Records.Add(HistoryRecordSnapshot.FromOperationFacts(operation));
            return FromContract(value);
        }
    }
}
