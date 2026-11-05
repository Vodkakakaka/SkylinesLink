using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// 仅测试自己的匿名管道。不会接触继承的 stdin/stdout，也不实现产品 relay。
internal static class SyncIoProbe
{
    private const int Aborted = 995, NotFound = 1168;
    private const int BufferCapacity = 65536;

    public static int Main()
    {
        try
        {
            var cases = new List<object>
            {
                Run("blocked-read", write: false, earlyCancel: false, completeNormally: false),
                Run("blocked-write", write: true, earlyCancel: false, completeNormally: false),
                Run("cancel-before-read-entry", write: false, earlyCancel: true, completeNormally: false),
                Run("cancel-versus-read-completion", write: false, earlyCancel: false, completeNormally: true)
            };
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, cases }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { passed = false, failure = exception.Message, type = exception.GetType().Name }));
            return 1;
        }
    }

    private static object Run(string name, bool write, bool earlyCancel, bool completeNormally)
    {
        if (!CreatePipe(out var readEnd, out var writeEnd, IntPtr.Zero, 4096)) throw new Win32Exception();
        using (readEnd)
        using (writeEnd)
        using (var worker = new Worker(write ? writeEnd : readEnd, write))
        {
            worker.Start();
            var earlyError = 0;
            if (earlyCancel)
            {
                var earlyResult = CancelSynchronousIo(worker.NativeThread);
                earlyError = earlyResult ? 0 : Marshal.GetLastWin32Error();
                Require(!earlyResult && earlyError == NotFound, "idle cancellation must not promise future I/O cancellation");
                Require(!worker.HasReturned, "worker remains before I/O entry");
            }

            worker.AllowEntry();
            worker.WaitForPending();
            Require(!worker.HasReturned && worker.LiveBufferBytes == BufferCapacity, "actual I/O must own its buffer while pending");

            if (completeNormally)
            {
                var oneByte = new byte[] { 0x5a };
                if (!WriteFileBytes(writeEnd, oneByte, 1, out var sent, IntPtr.Zero) || sent != 1) throw new Win32Exception();
            }

            var watch = Stopwatch.StartNew();
            var cancelAccepted = false;
            var cancelAttempts = 0;
            // 这个有限夹具捕获 entry race：1168 只表示当前未找到，不是线程已结束。
            while (!worker.Join(10) && watch.ElapsedMilliseconds < 3000)
            {
                cancelAttempts++;
                if (CancelSynchronousIo(worker.NativeThread)) cancelAccepted = true;
                else if (Marshal.GetLastWin32Error() != NotFound) throw new Win32Exception();
            }
            Require(worker.Join(0), "cancel did not finish actual worker within fixture deadline");
            worker.ThrowFailure();
            Require(worker.LiveBufferBytes == 0, "buffer must be released only after actual native return");
            if (completeNormally)
                Require((worker.Succeeded && worker.Transferred == 1) || (!worker.Succeeded && worker.Error == Aborted), "completion race must report its real outcome");
            else
                Require(cancelAccepted && !worker.Succeeded && worker.Error == Aborted, "pending I/O must actually return ERROR_OPERATION_ABORTED");

            return new
            {
                name, pendingObserved = true, earlyCancelError = earlyError, cancelAccepted, cancelAttempts,
                nativeSucceeded = worker.Succeeded, nativeError = worker.Error, transferredBytes = worker.Transferred,
                workerJoined = true, liveBufferBytes = worker.LiveBufferBytes, elapsedMilliseconds = watch.ElapsedMilliseconds
            };
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Worker : IDisposable
    {
        private readonly SafeFileHandle pipe;
        private readonly bool write;
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new(false), enter = new(false);
        private SafeFileHandle? nativeThread;
        private Exception? failure;
        private int returned, liveBufferBytes;

        public Worker(SafeFileHandle pipe, bool write)
        {
            this.pipe = pipe;
            this.write = write;
            thread = new Thread(Execute) { IsBackground = true, Name = "SkylinesLink sync IO fixture" };
        }

        public SafeFileHandle NativeThread => nativeThread ?? throw new InvalidOperationException("thread handle unavailable");
        public bool HasReturned => Volatile.Read(ref returned) != 0;
        public int LiveBufferBytes => Volatile.Read(ref liveBufferBytes);
        public bool Succeeded { get; private set; }
        public int Error { get; private set; }
        public uint Transferred { get; private set; }

        public void Start()
        {
            thread.Start();
            Require(ready.Wait(3000), "worker start deadline");
            ThrowFailure();
        }

        public void AllowEntry() => enter.Set();
        public bool Join(int milliseconds) => thread.Join(milliseconds);
        public void ThrowFailure() { if (failure is not null) throw new InvalidOperationException("worker failed", failure); }

        public void WaitForPending()
        {
            var watch = Stopwatch.StartNew();
            do
            {
                if (!GetThreadIOPendingFlag(NativeThread, out var pending)) throw new Win32Exception();
                if (pending) return;
                Require(!HasReturned, "I/O returned before actual pending observation");
                Thread.Sleep(1);
            } while (watch.ElapsedMilliseconds < 3000);
            throw new InvalidOperationException("pending observation deadline");
        }

        private void Execute()
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                var process = GetCurrentProcess();
                if (!DuplicateHandle(process, GetCurrentThread(), process, out nativeThread, 0, false, 2)) throw new Win32Exception();
                ready.Set();
                enter.Wait();
                buffer = Marshal.AllocHGlobal(BufferCapacity);
                Volatile.Write(ref liveBufferBytes, BufferCapacity);
                if (write) Marshal.Copy(new byte[BufferCapacity], 0, buffer, BufferCapacity);
                Succeeded = write
                    ? WriteFile(pipe, buffer, BufferCapacity, out var transferred, IntPtr.Zero)
                    : ReadFile(pipe, buffer, BufferCapacity, out transferred, IntPtr.Zero);
                Error = Succeeded ? 0 : Marshal.GetLastWin32Error();
                Transferred = transferred;
            }
            catch (Exception exception) { failure = exception; ready.Set(); }
            finally
            {
                // 不在取消请求/控制超时路径退还仍可能由内核访问的内存。
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                Volatile.Write(ref liveBufferBytes, 0);
                Volatile.Write(ref returned, 1);
            }
        }

        public void Dispose()
        {
            enter.Set();
            if (thread.IsAlive)
            {
                // 失败清理仍只请求取消，等实际线程结束；绝不 TerminateThread 或提前 free。
                var watch = Stopwatch.StartNew();
                while (!thread.Join(10) && watch.ElapsedMilliseconds < 3000)
                    if (nativeThread is not null) CancelSynchronousIo(nativeThread);
            }
            if (thread.IsAlive) return; // 进程失败退出；不虚报已释放，也不释放仍使用的同步资源。
            nativeThread?.Dispose();
            ready.Dispose();
            enter.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)] private static extern bool WriteFileBytes(SafeFileHandle file, byte[] buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadIOPendingFlag(SafeFileHandle thread, out bool pending);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
}
