using System;
using System.Collections.Generic;
using System.Linq;
using SkylinesLink.Application.Simulation;

namespace SkylinesLink.Application.Operations
{
    /// <summary>仅折叠已提供的普通记录；不推断文件 EOF、授权、原生宿主寿命或未发生的终态。</summary>
    public static class JournalFactsFold
    {
        public static JournalFactsFoldResult Fold(IReadOnlyList<JournalRecord> records)
            => FoldCore(records, false);
        public static JournalFactsFoldResult FoldMixed(IReadOnlyList<JournalRecord> records)
            => FoldCore(records, true);
        private static JournalFactsFoldResult FoldCore(IReadOnlyList<JournalRecord> records, bool mixed)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var entries = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            var keys = new HashSet<SubmissionKey>();
            var ordered = new List<Accumulator>();
            var tasks = mixed ? new TaskJournalReducer() : null;
            string? environment = null, storeId = null;
            int validCount = 0;
            foreach (var record in records)
            {
                if (validCount >= OperationLimits.MaximumFrames || record.Sequence != validCount + 1 ||
                    (mixed ? record.SchemaRevision != 2 && record.SchemaRevision != 3 : record.SchemaRevision != 2) ||
                    record.SchemaRevision == 2 && record.Entry is OperationAccepted recorded && recorded.Intent.ProfileInstanceId != null) break;
                if (validCount == 0)
                {
                    if (!(record.Entry is StoreOpened header)) break;
                    environment = record.EnvironmentId; storeId = header.StoreId;
                }
                else if (record.EnvironmentId != environment ||
                    !(mixed && TaskJournalReducer.IsMetadata(record.Entry) ? tasks!.Apply(record, keys) : Apply(record, entries, keys, ordered))) break;
                validCount++;
            }
            return new JournalFactsFoldResult(environment, storeId, validCount, validCount > 0 && validCount == records.Count,
                ordered.Select(entry => new JournalOperationFacts(entry.Intent, entry.Commit, entry.Native, entry.Final, entry.FinalSequence)).ToArray(), tasks?.Snapshot());
        }

        private static bool Apply(JournalRecord record, Dictionary<string, Accumulator> entries,
            HashSet<SubmissionKey> keys, List<Accumulator> ordered)
        {
            if (record.Entry is OperationAccepted accepted)
            {
                var intent = accepted.Intent;
                if (entries.ContainsKey(intent.OperationId) || keys.Contains(intent.Key) || entries.Count >= OperationLimits.MaximumRetained ||
                    intent.OriginInstanceId != intent.Source.InstanceId ||
                    !(intent.Source.State == "menu" ? intent.Source.CityId == null : intent.Source.State == "ready") ||
                    intent.CommandHash != OperationCommandHash.Compute(intent.Source, intent.Target)) return false;
                var entry = new Accumulator(intent);
                entries.Add(intent.OperationId, entry); keys.Add(intent.Key); ordered.Add(entry);
                return true;
            }
            string? id = record.Entry is CommitIntended ci ? ci.OperationId : record.Entry is NativeTerminated nt ? nt.OperationId :
                record.Entry is OperationFinished of ? of.OperationId : null;
            if (id == null || !entries.TryGetValue(id, out var current) || current.Final != null) return false;
            if (record.Entry is CommitIntended commit)
            {
                if (current.Commit != null || !SameTarget(commit.Target, current.Intent.Target)) return false;
                current.Commit = commit;
            }
            else if (record.Entry is NativeTerminated native)
            {
                if (current.Commit == null || current.Native != null) return false;
                current.Native = native;
            }
            else if (record.Entry is OperationFinished final)
            {
                if (!ValidFinal(current, final)) return false;
                current.Final = final; current.FinalSequence = record.Sequence;
            }
            else return false;
            return true;
        }

        private static bool ValidFinal(Accumulator entry, OperationFinished final)
        {
            var facts = final.Load;
            if (facts.ObservationContext != null && (facts.ObservationContext.InstanceId != entry.Intent.Source.InstanceId ||
                facts.ObservationContext.State != "ready")) return false;
            if (facts.TargetMatch != LoadTargetMatch.Unknown && facts.ObservationContext == null) return false;
            if ((facts.CorrelatedTargetReady || facts.HistoricalCompletionProven) &&
                (facts.ObservationContext == null || facts.ObservationContext.Generation <= entry.Intent.Source.Generation)) return false;
            if (final.Submission == SubmissionKnowledge.NotSubmitted)
                return entry.Native == null && !final.MayHaveApplied && facts.PhysicalTaskState == NativeLoadTaskState.NotStarted &&
                    !facts.CorrelatedTargetReady && !facts.HistoricalCompletionProven && (final.State == "failed" || final.State == "cancelled");
            if (entry.Commit == null || !final.MayHaveApplied) return false;
            if (final.Submission == SubmissionKnowledge.Unknown)
                return entry.Native == null && facts.PhysicalTaskState == NativeLoadTaskState.Unknown && final.State == "unknown" && !facts.CorrelatedTargetReady && !facts.HistoricalCompletionProven;
            if (facts.PhysicalTaskState == NativeLoadTaskState.Unknown)
                return entry.Native == null && final.State == "unknown" && !facts.HistoricalCompletionProven;
            if (entry.Native == null || facts.PhysicalTaskState != entry.Native.State || facts.LoadReturnValue != entry.Native.LoadReturnValue) return false;
            if (facts.CorrelatedTargetReady || facts.HistoricalCompletionProven)
            {
                var bound = entry.Native.BoundTarget;
                if (bound == null || !entry.Intent.Target.SameIdentity(bound) || bound.ContentVersion == null ||
                    (entry.Intent.Target.ContentVersion != null && entry.Intent.Target.ContentVersion != bound.ContentVersion)) return false;
            }
            if (facts.HistoricalCompletionProven != (final.State == "succeeded")) return false;
            if (facts.PhysicalTaskState == NativeLoadTaskState.RanToCompletion)
                return facts.LoadReturnValue == false ? !facts.HistoricalCompletionProven && final.State == "failed" :
                    facts.HistoricalCompletionProven ? facts.LoadReturnValue == true && facts.CorrelatedTargetReady && final.State == "succeeded" : final.State == "unknown";
            if (facts.HistoricalCompletionProven) return false;
            return facts.PhysicalTaskState == NativeLoadTaskState.Faulted ? final.State == "failed" : facts.PhysicalTaskState == NativeLoadTaskState.Cancelled && final.State == "cancelled";
        }

        private static bool SameTarget(LoadTarget a, LoadTarget b) => a.SameIdentity(b) && a.ContentVersion == b.ContentVersion;
        private sealed class Accumulator
        {
            internal Accumulator(OperationIntent intent) { Intent = intent; }
            internal readonly OperationIntent Intent;
            internal CommitIntended? Commit;
            internal NativeTerminated? Native;
            internal OperationFinished? Final;
            internal long? FinalSequence;
        }
    }

    public sealed class JournalOperationFacts
    {
        internal JournalOperationFacts(OperationIntent intent, CommitIntended? commit, NativeTerminated? native, OperationFinished? final, long? finalSequence)
        { Intent = intent; Commit = commit; Native = native; Final = final; FinalSequence = finalSequence; }
        public OperationIntent Intent { get; }
        public CommitIntended? Commit { get; }
        public NativeTerminated? Native { get; }
        public OperationFinished? Final { get; }
        public long? FinalSequence { get; }
    }

    public sealed class JournalFactsFoldResult
    {
        internal JournalFactsFoldResult(string? environmentId, string? storeId, int validCount, bool allRecordsValid, JournalOperationFacts[] operations, JournalTaskFacts[]? tasks = null)
        { EnvironmentId = environmentId; StoreId = storeId; ValidRecordCount = validCount; AllRecordsValid = allRecordsValid; Operations = Array.AsReadOnly(operations); Tasks = Array.AsReadOnly(tasks ?? Array.Empty<JournalTaskFacts>()); }
        public string? EnvironmentId { get; }
        public string? StoreId { get; }
        public int ValidRecordCount { get; }
        public long ThroughSequence => ValidRecordCount;
        // 只表示本次输入全被接受，不表示权威文件已到 EOF；落后 checkpoint 不能借此推断未提交。
        public bool AllRecordsValid { get; }
        public IReadOnlyList<JournalOperationFacts> Operations { get; }
        public IReadOnlyList<JournalTaskFacts> Tasks { get; }
    }
}
