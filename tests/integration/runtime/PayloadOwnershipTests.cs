using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class PayloadOwnershipTests
    {
        internal static async Task<int> RunAsync()
        {
            int checks = 0;
            void Check(bool value, string name) { Program.Assert(value, name); checks++; }
            Program.Stage = "payload ownership and capacity";
            var budget = new CapacityBudget(2, 16);
            using (var first = budget.TryReserve(8)!)
            using (var second = budget.TryReserve(8)!)
            {
                Check(budget.TryReserve(1) == null, "capacity gate rejects without waiter");
                first.Dispose(); first.Dispose();
                Check(budget.Read().Count == 1 && budget.Read().Capacity == 8, "reservation releases once");
            }
            Check(budget.Read().Count == 0 && budget.Read().PeakCapacity == 16, "capacity peak survives release");
            Program.Expect<PayloadCapacityException>(() => OwnedPayload.CopyFrom(new byte[17], budget)); checks++;
            Check(budget.Read().Capacity == 0, "single oversized payload refused before ownership");

            // 外借 scratch 的容量由本夹具显式持有；冻结必须复制，不能用只读包装隐藏可变数组。
            using (var scratchCharge = budget.TryReserve(8)!)
            {
                var scratch = new byte[] { 65, 66, 67, 68, 69, 70, 71, 72 };
                var snapshot = OwnedPayload.CopyFrom(scratch, budget, 1);
                Check(budget.Read().Count == 2 && budget.Read().Capacity == 16, "copy overlap charges both allocations");
                scratch[0] = 90;
                using (var reader = snapshot.TryAcquireReader()!)
                {
                    Check(reader.DecodeUtf8() == "ABCDEFGH", "borrowed mutation cannot alter frozen payload");
                    Check(snapshot.TryAcquireReader() == null, "reader limit rejects without waiter");
                }
                Array.Clear(scratch, 0, scratch.Length);
                scratchCharge.Dispose();
                var resultOwner = snapshot.TransferOwnership();
                snapshot.Dispose();
                Program.Expect<ObjectDisposedException>(() => snapshot.TryAcquireReader()); checks++;
                Check(budget.Read().Capacity == 8 && budget.Read().Count == 1, "ownership transfer neither duplicates nor releases capacity");

                using (var delayed = new DelayedWriteStream())
                using (var cancellation = new CancellationTokenSource())
                {
                    var sender = resultOwner.TryAcquireReader()!;
                    Task write = sender.WriteToAsync(delayed, cancellation.Token);
                    await Program.MustComplete(delayed.Entered.Task);
                    cancellation.Cancel(); sender.Dispose(); resultOwner.Dispose();
                    Check(!write.IsCompleted && budget.Read().Capacity == 8 && delayed.FirstByte == 65,
                        "cancel and retirement preserve in-flight reader allocation");
                    Program.Expect<ObjectDisposedException>(() => resultOwner.TryAcquireReader()); checks++;
                    delayed.Complete();
                    await Program.MustComplete(write);
                    Check(budget.Read().Capacity == 0 && delayed.FirstByte == 0, "actual IO completion clears retired bytes and releases budget");
                }
            }

            using (var shortSource = new MemoryStream(new byte[] { 1, 2 }))
            {
                try { await OwnedPayload.ReadExactlyAsync(shortSource, 4, budget, CancellationToken.None); throw new InvalidOperationException("Expected truncated source."); }
                catch (EndOfStreamException) { checks++; }
                Check(budget.Read().Capacity == 0, "failed payload fill releases reservation");
            }
            using (var owner = OwnedPayload.FromUtf8("failed", budget))
            using (var reader = owner.TryAcquireReader()!)
            using (var failed = new DelayedWriteStream())
            {
                Task write = reader.WriteToAsync(failed, CancellationToken.None);
                await Program.MustComplete(failed.Entered.Task);
                reader.Dispose(); owner.Dispose(); failed.Fail();
                try { await Program.MustComplete(write); throw new InvalidOperationException("Expected failed send."); }
                catch (IOException) { checks++; }
                Check(budget.Read().Count == 0 && failed.FirstByte == 0, "failed actual send drains exactly once");
            }

            var limits = Program.Limits();
            var frameBudget = new CapacityBudget(1, limits.MaximumPayloadBytes);
            using (var destination = new DelayedWriteStream(skipFirstWrite: true))
            {
                Task send = FrameCodec.WriteAsync(destination, new JObject { ["value"] = "data" }, limits, CancellationToken.None, frameBudget);
                await Program.MustComplete(destination.Entered.Task);
                Check(frameBudget.Read().Count == 1 && frameBudget.Read().Capacity > 0, "FrameCodec uses real shared payload owner");
                using (var other = new MemoryStream())
                {
                    try { await FrameCodec.WriteAsync(other, new JObject { ["value"] = "other" }, limits, CancellationToken.None, frameBudget); throw new InvalidOperationException("Expected shared capacity denial."); }
                    catch (PipeProtocolException error) when (error.Code == "PAYLOAD_BUDGET") { checks++; }
                    Check(other.Length == 0, "shared denial writes no frame prefix");
                }
                destination.Complete(); await Program.MustComplete(send);
                Check(frameBudget.Read().Count == 0 && frameBudget.Read().Capacity == 0, "FrameCodec actual completion releases payload");
            }
            using (var wire = new MemoryStream())
            {
                await FrameCodec.WriteAsync(wire, new JObject { ["value"] = "data" }, limits, CancellationToken.None);
                wire.Position = 0;
                using (var held = frameBudget.TryReserve(1)!)
                {
                    try { await FrameCodec.ReadAsync(wire, limits, limits.FrameTimeout, CancellationToken.None, frameBudget); throw new InvalidOperationException("Expected shared read denial."); }
                    catch (PipeProtocolException error) when (error.Code == "PAYLOAD_BUDGET") { checks++; }
                    Check(wire.Position == FrameCodec.HeaderLength, "read gate runs after bounded header before payload consumption");
                }
            }
            Check(frameBudget.Read().Count == 0, "all payload fixture ownership released");
            return checks;
        }

        private sealed class DelayedWriteStream : Stream
        {
            private readonly TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly bool skipFirstWrite;
            private int writes;
            private byte[]? observed;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal DelayedWriteStream(bool skipFirstWrite = false) { this.skipFirstWrite = skipFirstWrite; }
            internal byte FirstByte => observed![0];
            internal void Complete() => done.TrySetResult(true);
            internal void Fail() => done.TrySetException(new IOException("Synthetic send failed."));
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            {
                if (++writes == 1 && skipFirstWrite) return Task.CompletedTask;
                observed = buffer; Entered.TrySetResult(true); return done.Task;
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
