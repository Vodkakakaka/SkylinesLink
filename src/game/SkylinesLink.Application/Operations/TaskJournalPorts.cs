using System;
using System.Threading.Tasks;

namespace SkylinesLink.Application.Operations
{
    /// <summary>同一writer返回的实际物理来源；不能用排队时的旧水位替代。</summary>
    public sealed class JournalTaskReceipt
    {
        public JournalTaskReceipt(string storeId, long sequence)
        {
            if (storeId == null || storeId.Length != 32 || sequence < 1 || sequence > OperationLimits.MaximumFrames)
                throw new ArgumentException("Invalid task journal receipt.");
            foreach (char part in storeId)
                if (!(part >= '0' && part <= '9' || part >= 'a' && part <= 'f')) throw new ArgumentException("Invalid task journal store.");
            StoreId = storeId; Sequence = sequence;
        }
        public string StoreId { get; }
        public long Sequence { get; }
        public int JournalSchemaRevision => 3;
    }

    /// <summary>单次具名metadata信用；Dispose不取消或提前释放已经入队的真实IO。</summary>
    public interface ITaskJournalReservation : IDisposable
    {
        void ValidateEntry(JournalEntry entry);
        Task<JournalTaskReceipt> AppendAsync(JournalEntry entry);
    }

    /// <summary>原journal的显式混合端口；停止信用归journal持有，不归调用方handle。</summary>
    public interface ITaskJournal
    {
        bool SupportsTaskMetadata { get; }
        ITaskJournalReservation? TryReserveTaskCreation(string taskId);
        ITaskJournalReservation? TryReserveTaskRevision(string taskId);
        ITaskJournalReservation? TryReserveTaskStop(string taskId);
    }
}
