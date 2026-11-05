using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Mod.Runtime
{
    /// <summary>私有、密封的 managed 载荷。唯一 owner 可转交；退役后现有读者完成才清除并退还容量。</summary>
    public sealed class OwnedPayload : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly PayloadState state;
        private bool owns;

        private OwnedPayload(PayloadState state) { this.state = state; owns = true; }

        public static OwnedPayload FromUtf8(string value, CapacityBudget budget, int maximumReaders = 4)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            int length = StrictUtf8.GetByteCount(value);
            var state = Allocate(length, budget, maximumReaders);
            try { StrictUtf8.GetBytes(value, 0, value.Length, state.Bytes!, 0); return new OwnedPayload(state); }
            catch { state.Retire(); throw; }
        }

        public static OwnedPayload CopyFrom(byte[] borrowed, CapacityBudget budget, int maximumReaders = 4)
        {
            if (borrowed == null) throw new ArgumentNullException(nameof(borrowed));
            var state = Allocate(borrowed.Length, budget, maximumReaders);
            try { Buffer.BlockCopy(borrowed, 0, state.Bytes!, 0, borrowed.Length); return new OwnedPayload(state); }
            catch { state.Retire(); throw; }
        }

        public static async Task<OwnedPayload> ReadExactlyAsync(Stream stream, int length, CapacityBudget budget,
            CancellationToken cancellation, int maximumReaders = 4)
        {
            var state = Allocate(length, budget, maximumReaders);
            try
            {
                int offset = 0;
                while (offset < length)
                {
                    int read = await stream.ReadAsync(state.Bytes!, offset, length - offset, cancellation).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                return new OwnedPayload(state);
            }
            catch { state.Retire(); throw; }
        }

        private static PayloadState Allocate(int length, CapacityBudget budget, int maximumReaders)
        {
            if (budget == null) throw new ArgumentNullException(nameof(budget));
            if (maximumReaders < 1 || maximumReaders > 32) throw new ArgumentOutOfRangeException(nameof(maximumReaders));
            var reservation = budget.TryReserve(length) ?? throw new PayloadCapacityException();
            try { return new PayloadState(new byte[length], reservation, maximumReaders); }
            catch { reservation.Dispose(); throw; }
        }

        public OwnedPayload TransferOwnership()
        {
            lock (state.Sync)
            {
                RequireOwner();
                var next = new OwnedPayload(state);
                owns = false;
                return next;
            }
        }

        public PayloadReader? TryAcquireReader()
        {
            lock (state.Sync)
            {
                RequireOwner();
                if (state.Readers == state.MaximumReaders) return null;
                var reader = new PayloadReader(state);
                state.Readers++;
                return reader;
            }
        }

        private void RequireOwner()
        {
            if (!owns || state.Retired) throw new ObjectDisposedException(nameof(OwnedPayload));
        }

        public void Dispose()
        {
            lock (state.Sync)
            {
                if (!owns) return;
                owns = false;
                state.Retire();
            }
        }

        internal sealed class PayloadState
        {
            internal readonly object Sync = new object();
            internal byte[]? Bytes;
            internal readonly int MaximumReaders;
            internal int Readers;
            internal bool Retired;
            private readonly CapacityReservation reservation;
            internal PayloadState(byte[] bytes, CapacityReservation reservation, int maximumReaders)
            { Bytes = bytes; this.reservation = reservation; MaximumReaders = maximumReaders; }
            internal void Retire()
            {
                lock (Sync) { Retired = true; ReleaseIfDrained(); }
            }
            internal void ReleaseReader()
            {
                lock (Sync) { Readers--; ReleaseIfDrained(); }
            }
            private void ReleaseIfDrained()
            {
                if (!Retired || Readers != 0 || Bytes == null) return;
                Array.Clear(Bytes, 0, Bytes.Length);
                Bytes = null;
                reservation.Dispose();
            }
        }

        public sealed class PayloadReader : IDisposable
        {
            private readonly object sync = new object();
            private PayloadState? state;
            private bool closed;
            private bool active;
            internal PayloadReader(PayloadState state) { this.state = state; }

            // 每个 reader 同时仅一次使用。Dispose 不会提前释放已提交的异步 IO。
            private PayloadState Begin()
            {
                lock (sync)
                {
                    if (closed || state == null) throw new ObjectDisposedException(nameof(PayloadReader));
                    if (active) throw new InvalidOperationException("Payload reader is already in use.");
                    active = true;
                    return state;
                }
            }
            private void End()
            {
                lock (sync) { active = false; if (closed) Release(); }
            }
            public string DecodeUtf8()
            {
                var current = Begin();
                try { return StrictUtf8.GetString(current.Bytes!); }
                finally { End(); }
            }
            public async Task WriteToAsync(Stream destination, CancellationToken cancellation)
            {
                // 仅交给遵守 Stream 契约的受信 IO：不改写输入，不在完成后继续保留/使用数组。
                // 本产品 NamedPipe 适配符合这一边界；此类型不是同进程恶意 Stream 的隔离沙箱。
                var current = Begin();
                try { await destination.WriteAsync(current.Bytes!, 0, current.Bytes!.Length, cancellation).ConfigureAwait(false); }
                finally { End(); }
            }
            public void Dispose()
            {
                lock (sync) { closed = true; if (!active) Release(); }
            }
            private void Release()
            {
                if (state == null) return;
                state.ReleaseReader();
                state = null;
            }
        }
    }

    public sealed class PayloadCapacityException : Exception
    {
        public PayloadCapacityException() : base("Owned payload capacity is exhausted.") { }
    }
}
