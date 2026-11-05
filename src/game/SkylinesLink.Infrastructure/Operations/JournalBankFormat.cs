using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;

namespace SkylinesLink.Infrastructure.Operations
{
    // V1固定物理常量；混合布局显式由JournalLayout选择。
    internal static class JournalBankFormat
    {
        internal const int SlotBytes = 4096;
        internal const int MaximumControlPayload = SlotBytes - 44;
        internal const int BodyBytes = 536576;
        internal const int BodyOffset = 3 * SlotBytes;
        internal const int BankBytes = BodyOffset + BodyBytes;
        internal const int FileBytes = SlotBytes + 2 * BankBytes;
        internal const int MaximumBusinessBytes = OperationLimits.MaximumFrames * OperationLimits.MaximumFrame;
        internal static long BankStart(int bank)
        {
            if (bank < 0 || bank > 1) throw new ArgumentOutOfRangeException(nameof(bank));
            return SlotBytes + (long)bank * BankBytes;
        }
        internal static string Digest(byte[] value, int count)
        {
            using (var sha = SHA256.Create())
            {
                var result = new StringBuilder(64);
                foreach (byte part in sha.ComputeHash(value, 0, count))
                    result.Append(part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }
        internal static void ReadExactly(Stream source, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = source.Read(buffer, offset, count);
                if (read == 0) throw new EndOfStreamException();
                offset += read; count -= read;
            }
        }
    }

    internal sealed class JournalControlSlot
    {
        internal JournalControlSlot(JournalControl value, string digest)
        { Value = value; Digest = digest; }
        internal JournalControl Value { get; }
        internal string Digest { get; }
    }

    /// <summary>保留V1 typed入口；二进制边界与新版控制共享同一实现。</summary>
    internal static class JournalControlCodec
    {
        internal static byte[] Encode(JournalControl control) => PhysicalJournalControlCodec.EncodeToken(
            ContractCodec.EncodeJournalControl(control), JournalLayout.Legacy);
        internal static JournalControlSlot? Read(Stream source, long offset)
        {
            var token = PhysicalJournalControlCodec.ReadToken(source, offset, JournalLayout.Legacy, out string? digest);
            return token == null ? null : new JournalControlSlot(ContractCodec.DecodeJournalControl(token), digest!);
        }
    }
}
