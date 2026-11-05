using System;

namespace SkylinesLink.Mod.Connection
{
    public sealed class PipeLimits
    {
        public PipeLimits(int maximumConnections, int maximumPayloadBytes, int maximumJsonDepth, int maximumJsonTokens,
            TimeSpan handshakeTimeout, TimeSpan frameTimeout, TimeSpan sendTimeout,
            int? maximumDirectionalPayloadBuffers = null, long? maximumDirectionalPayloadCapacity = null)
        {
            if (maximumConnections < 1 || maximumConnections > 32) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
            if (maximumPayloadBytes < 256 || maximumPayloadBytes > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            if (maximumJsonDepth < 1 || maximumJsonDepth > 64) throw new ArgumentOutOfRangeException(nameof(maximumJsonDepth));
            if (maximumJsonTokens < 1 || maximumJsonTokens > 100000) throw new ArgumentOutOfRangeException(nameof(maximumJsonTokens));
            CheckDeadline(handshakeTimeout); CheckDeadline(frameTimeout); CheckDeadline(sendTimeout);
            MaximumConnections = maximumConnections;
            MaximumPayloadBytes = maximumPayloadBytes;
            MaximumJsonDepth = maximumJsonDepth;
            MaximumJsonTokens = maximumJsonTokens;
            HandshakeTimeout = handshakeTimeout;
            FrameTimeout = frameTimeout;
            SendTimeout = sendTimeout;
            MaximumDirectionalPayloadBuffers = maximumDirectionalPayloadBuffers ?? maximumConnections;
            MaximumDirectionalPayloadCapacity = maximumDirectionalPayloadCapacity ?? checked((long)maximumConnections * maximumPayloadBytes);
            if (MaximumDirectionalPayloadBuffers < 1 || MaximumDirectionalPayloadBuffers > 32) throw new ArgumentOutOfRangeException(nameof(maximumDirectionalPayloadBuffers));
            if (MaximumDirectionalPayloadCapacity < 1 || MaximumDirectionalPayloadCapacity > 32L * 1048576) throw new ArgumentOutOfRangeException(nameof(maximumDirectionalPayloadCapacity));
        }

        public int MaximumConnections { get; }
        public int MaximumPayloadBytes { get; }
        public int MaximumJsonDepth { get; }
        public int MaximumJsonTokens { get; }
        public TimeSpan HandshakeTimeout { get; }
        public TimeSpan FrameTimeout { get; }
        public TimeSpan SendTimeout { get; }
        // ingress与egress各持此额度；默认4连接/64KiB帧合计8 buffers/512KiB raw上界。
        public int MaximumDirectionalPayloadBuffers { get; }
        public long MaximumDirectionalPayloadCapacity { get; }

        private static void CheckDeadline(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }
}
