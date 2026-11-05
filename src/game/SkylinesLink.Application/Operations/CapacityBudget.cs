using System;

namespace SkylinesLink.Application.Operations
{
    /// <summary>自有分配的容量账，不是 CLR 堆或进程内存测量。准入立即成功或拒绝，不积累等待者。</summary>
    public sealed class CapacityBudget
    {
        private readonly object sync = new object();
        private readonly long maximumCapacity;
        private readonly int maximumCount;
        private long capacity;
        private long peakCapacity;
        private int count;
        private int peakCount;

        public CapacityBudget(int maximumCount, long maximumCapacity)
        {
            if (maximumCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumCount));
            if (maximumCapacity < 1) throw new ArgumentOutOfRangeException(nameof(maximumCapacity));
            this.maximumCount = maximumCount;
            this.maximumCapacity = maximumCapacity;
        }

        public CapacityReservation? TryReserve(int requestedCapacity)
        {
            if (requestedCapacity < 1) throw new ArgumentOutOfRangeException(nameof(requestedCapacity));
            lock (sync)
            {
                if (count == maximumCount || requestedCapacity > maximumCapacity - capacity) return null;
                var reservation = new CapacityReservation(this, requestedCapacity);
                count++;
                capacity += requestedCapacity;
                peakCount = Math.Max(peakCount, count);
                peakCapacity = Math.Max(peakCapacity, capacity);
                return reservation;
            }
        }

        public CapacityUsage Read()
        {
            lock (sync) return new CapacityUsage(count, capacity, peakCount, peakCapacity);
        }

        internal void Release(int releasedCapacity)
        {
            lock (sync) { count--; capacity -= releasedCapacity; }
        }
    }

    public sealed class CapacityUsage
    {
        internal CapacityUsage(int count, long capacity, int peakCount, long peakCapacity)
        { Count = count; Capacity = capacity; PeakCount = peakCount; PeakCapacity = peakCapacity; }
        public int Count { get; }
        public long Capacity { get; }
        public int PeakCount { get; }
        public long PeakCapacity { get; }
    }

    public sealed class CapacityReservation : IDisposable
    {
        private readonly object sync = new object();
        private CapacityBudget? budget;
        internal CapacityReservation(CapacityBudget budget, int capacity) { this.budget = budget; Capacity = capacity; }
        public int Capacity { get; }
        public void Dispose()
        {
            lock (sync)
            {
                if (budget == null) return;
                budget.Release(Capacity);
                budget = null;
            }
        }
    }
}
