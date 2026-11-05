namespace SkylinesLink.Infrastructure.Operations
{
    // 版本校验后的内部普通值，既不冒充V1 DTO，也不公开可变控制记录。
    internal sealed class JournalControlDocument
    {
        internal JournalControlDocument() { }
        internal JournalControlDocument(long formatVersion, JournalPhysicalControl entry) { FormatVersion = formatVersion; Entry = entry; }
        internal long FormatVersion { get; set; }
        internal JournalPhysicalControl Entry { get; set; } = default!;
    }

    internal abstract class JournalPhysicalControl
    {
        protected JournalPhysicalControl(string kind) { Kind = kind; }
        internal string Kind { get; set; }
        internal string ContainerId { get; set; } = default!;
        internal string EnvironmentId { get; set; } = default!;
    }
    internal sealed class JournalRootControl : JournalPhysicalControl
    {
        internal JournalRootControl() : base("root") { }
        internal long LayoutVersion { get; set; }
        internal long JournalSchemaRevision { get; set; }
    }
    internal sealed class JournalGenerationControl : JournalPhysicalControl
    {
        internal JournalGenerationControl() : base("generation") { }
        internal long Bank { get; set; }
        internal long Generation { get; set; }
        internal string StoreId { get; set; } = default!;
        internal JournalCut? Source { get; set; }
        internal long SeedFrameCount { get; set; }
        internal long SeedBytes { get; set; }
        internal string SeedDigest { get; set; } = default!;
        internal bool RetentionApplied { get; set; }
    }
    internal sealed class JournalActivationControl : JournalPhysicalControl
    {
        internal JournalActivationControl() : base("activation") { }
        internal long Bank { get; set; }
        internal long Generation { get; set; }
        internal string StoreId { get; set; } = default!;
        internal string DescriptorDigest { get; set; } = default!;
        internal string? PredecessorSealDigest { get; set; }
    }
    internal sealed class JournalSealControl : JournalPhysicalControl
    {
        internal JournalSealControl() : base("seal") { }
        internal JournalCut Source { get; set; } = default!;
        internal long SuccessorBank { get; set; }
        internal long SuccessorGeneration { get; set; }
        internal string SuccessorStoreId { get; set; } = default!;
        internal string SuccessorDescriptorDigest { get; set; } = default!;
    }
    internal sealed class JournalPreparingControl : JournalPhysicalControl
    {
        internal JournalPreparingControl() : base("preparing") { }
        internal long TargetBank { get; set; }
        internal long TargetGeneration { get; set; }
        internal string TargetStoreId { get; set; } = default!;
        internal JournalCut Source { get; set; } = default!;
    }
    internal sealed class JournalCut
    {
        internal long Bank { get; set; }
        internal long Generation { get; set; }
        internal string StoreId { get; set; } = default!;
        internal string DescriptorDigest { get; set; } = default!;
        internal string ActivationDigest { get; set; } = default!;
        internal long ThroughSequence { get; set; }
        internal long ThroughBytes { get; set; }
        internal string PrefixDigest { get; set; } = default!;
    }
    internal sealed class PhysicalJournalControlSlot
    {
        internal PhysicalJournalControlSlot(JournalControlDocument value, string digest) { Value = value; Digest = digest; }
        internal JournalControlDocument Value { get; }
        internal string Digest { get; }
    }
}
