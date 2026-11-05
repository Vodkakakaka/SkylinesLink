using System;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>尚未返回 owner 的构造失败仍可能保留原生租约；传播真实关闭事实而不泄露原始消息。</summary>
    public sealed class ConnectionInitializationException : Exception
    {
        internal ConnectionInitializationException(Exception cause, bool fullyReleased, bool nativeLeaseRetained)
            : base("Connection initialization did not release all ownership.", cause)
        { OriginalExceptionType = cause.GetType().Name; FullyReleased = fullyReleased; NativeLeaseRetained = nativeLeaseRetained; }
        public string OriginalExceptionType { get; }
        public bool FullyReleased { get; }
        public bool NativeLeaseRetained { get; }
    }
}
