using System;
using System.Collections.Generic;
using System.IO;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Infrastructure.Operations
{
    /// <summary>明确版本的有限物理/业务组合；不由文件中的任意数字扩展布局。</summary>
    internal sealed class JournalLayout
    {
        internal static JournalLayout Legacy { get; } = new JournalLayout(1, 1, 2, JournalBankFormat.BodyBytes, JournalBankFormat.MaximumBusinessBytes, OperationLimits.MaximumRecordPayload);
        internal static JournalLayout Mixed { get; } = new JournalLayout(2, 2, 3, 2097152, 2097152, JournalFrameCodec.MaximumMixedMetadataPayload);
        private JournalLayout(int controlVersion, int layoutVersion, int journalSchemaRevision, int bodyBytes, int maximumBusinessBytes, int maximumRecordPayload)
        {
            ControlVersion = controlVersion; LayoutVersion = layoutVersion; JournalSchemaRevision = journalSchemaRevision;
            BodyBytes = bodyBytes; MaximumBusinessBytes = maximumBusinessBytes; MaximumRecordPayload = maximumRecordPayload;
            BankBytes = checked(JournalBankFormat.BodyOffset + bodyBytes);
            FileBytes = checked(JournalBankFormat.SlotBytes + 2 * BankBytes);
        }
        internal int ControlVersion { get; }
        internal int LayoutVersion { get; }
        internal int JournalSchemaRevision { get; }
        internal int BodyBytes { get; }
        internal int BankBytes { get; }
        internal int FileBytes { get; }
        internal int MaximumBusinessBytes { get; }
        internal int MaximumRecordPayload { get; }
        internal int MaximumFrames => OperationLimits.MaximumFrames;
        internal long BankStart(int bank)
        {
            if (bank < 0 || bank > 1) throw new ArgumentOutOfRangeException(nameof(bank));
            return JournalBankFormat.SlotBytes + (long)bank * BankBytes;
        }
        internal byte[] EncodeRecord(JournalRecord record, out int length) => JournalSchemaRevision == 3
            ? JournalFrameCodec.EncodeMixed(record, out length) : JournalFrameCodec.Encode(record, out length);
        internal JournalRecord? ReadRecord(Stream source) => JournalSchemaRevision == 3 ? JournalFrameCodec.ReadMixed(source) : JournalFrameCodec.Read(source);
        internal JournalFactsFoldResult Fold(IReadOnlyList<JournalRecord> records) => JournalSchemaRevision == 3
            ? JournalFactsFold.FoldMixed(records) : JournalFactsFold.Fold(records);
    }
}
