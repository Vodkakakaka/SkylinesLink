using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkylinesLink.Application.Operations
{
    public sealed class JournalRotationPosition
    {
        public JournalRotationPosition(string storeId, long throughSequence, bool retentionApplied, bool supportsRotation)
        { StoreId = storeId; ThroughSequence = throughSequence; RetentionApplied = retentionApplied; SupportsRotation = supportsRotation; }
        public string StoreId { get; }
        public long ThroughSequence { get; }
        public bool RetentionApplied { get; }
        public bool SupportsRotation { get; }
    }

    /// <summary>Operations冻结的有限保留集合；资格由Operations核，writer只核精确来源和完整复制。</summary>
    public sealed class JournalRotationPlan
    {
        public JournalRotationPlan(string expectedStoreId, long expectedThroughSequence, IReadOnlyList<string> retainedOperationIds)
            : this(expectedStoreId, expectedThroughSequence, retainedOperationIds, Array.Empty<string>()) { }

        public JournalRotationPlan(string expectedStoreId, long expectedThroughSequence, IReadOnlyList<string> retainedOperationIds,
            IReadOnlyList<string> retainedTaskIds)
        {
            if (string.IsNullOrEmpty(expectedStoreId) || expectedThroughSequence < 1 || expectedThroughSequence > OperationLimits.MaximumFrames ||
                retainedOperationIds == null || retainedOperationIds.Count > OperationLimits.MaximumRetained ||
                retainedTaskIds == null || retainedTaskIds.Count > TaskJournalLimits.MaximumTasks)
                throw new ArgumentException("Invalid journal rotation plan.");
            var copy = retainedOperationIds.ToArray();
            var tasks = retainedTaskIds.ToArray();
            if (copy.Any(string.IsNullOrEmpty) || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length ||
                tasks.Any(string.IsNullOrEmpty) || tasks.Distinct(StringComparer.Ordinal).Count() != tasks.Length)
                throw new ArgumentException("Rotation identities must be unique.");
            ExpectedStoreId = expectedStoreId; ExpectedThroughSequence = expectedThroughSequence;
            RetainedOperationIds = Array.AsReadOnly(copy);
            RetainedTaskIds = Array.AsReadOnly(tasks);
        }
        public string ExpectedStoreId { get; }
        public long ExpectedThroughSequence { get; }
        public IReadOnlyList<string> RetainedOperationIds { get; }
        public IReadOnlyList<string> RetainedTaskIds { get; }
    }

    public enum JournalRotationOutcome { Installed, CurrentPreserved }
    public sealed class JournalRotationResult
    {
        public JournalRotationResult(JournalRotationOutcome outcome, ConfirmedJournalPrefix prefix, bool retentionApplied)
        { Outcome = outcome; Prefix = prefix ?? throw new ArgumentNullException(nameof(prefix)); RetentionApplied = retentionApplied; }
        public JournalRotationOutcome Outcome { get; }
        public ConfirmedJournalPrefix Prefix { get; }
        public bool RetentionApplied { get; }
    }
    public interface IJournalRotation
    {
        Task<JournalRotationResult> Completion { get; }
        void RequestStop();
    }
}
