using SkylinesLink.Application.Operations;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>两方向有序采样的合计，不声称同一原子时刻或同时实测峰值。</summary>
    public sealed class PipePayloadUsage
    {
        internal PipePayloadUsage(CapacityUsage ingress, CapacityUsage egress, PipeLimits limits)
        {
            Count = ingress.Count + egress.Count; Capacity = ingress.Capacity + egress.Capacity;
            MaximumCount = checked(2 * limits.MaximumDirectionalPayloadBuffers);
            MaximumCapacity = checked(2 * limits.MaximumDirectionalPayloadCapacity);
        }
        public int Count { get; }
        public long Capacity { get; }
        public int MaximumCount { get; }
        public long MaximumCapacity { get; }
    }
}
