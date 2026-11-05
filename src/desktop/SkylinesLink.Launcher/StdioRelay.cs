using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Launcher;

// 仅拥有字节和 IO 生命周期，不识别 MCP 消息。两个 worker 都有独占缓冲与原生线程。
internal sealed class StdioRelay : IDisposable
{
    internal const int StartupMilliseconds = 5000, WriteMilliseconds = 2000, CleanupMilliseconds = 2000;
    internal const int BufferCapacity = 16384;
    private readonly NamedPipeServerStream input, output;
    private readonly Worker inputWorker, outputWorker;
    private bool started, disposed;
    private long closeStarted;
    private int closeCompleted;

    public StdioRelay(SafeFileHandle externalInput, SafeFileHandle externalOutput)
    {
        Nonce = Guid.NewGuid().ToString("N");
        input = CreatePipe("in");
        try { output = CreatePipe("out"); }
        catch { input.Dispose(); throw; }
        inputWorker = new Worker(externalInput, input, toExternal: false);
        outputWorker = new Worker(externalOutput, output, toExternal: true);
    }

    public string Nonce { get; }
    public bool InputCompleted => inputWorker.Completed;
    public bool OutputCompleted => outputWorker.Completed;
    public bool Failed => inputWorker.Failed || outputWorker.Failed;
    public bool OutputExpired => outputWorker.WriteExpired;
    public int LiveBufferBytes => inputWorker.LiveBufferBytes + outputWorker.LiveBufferBytes;

    private NamedPipeServerStream CreatePipe(string suffix) => new(
        "skylineslink-stdio-" + Nonce + "-" + suffix, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, BufferCapacity, BufferCapacity);

    public void Connect(uint childProcessId)
    {
        using var deadline = new CancellationTokenSource(StartupMilliseconds);
        try
        {
            Task.WhenAll(ConnectOne(input, childProcessId, deadline.Token), ConnectOne(output, childProcessId, deadline.Token)).GetAwaiter().GetResult();
        }
        catch { deadline.Cancel(); input.Dispose(); output.Dispose(); throw; }
        started = true;
        inputWorker.Start();
        outputWorker.Start();
    }

    private static async Task ConnectOne(NamedPipeServerStream pipe, uint childProcessId, CancellationToken cancellation)
    {
        await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != childProcessId)
            throw new IOException("STDIO_RELAY_PEER");
    }

    public void StopInput() => inputWorker.RequestStop();
    public void RequestStop() { inputWorker.RequestStop(); outputWorker.RequestStop(); }

    // 返回 false 时仍由真实 worker 持有其 buffer/OVERLAPPED；不得宣称已释放。
    public bool Close()
    {
        if (Volatile.Read(ref closeCompleted) != 0) return true;
        var first = Interlocked.CompareExchange(ref closeStarted, Stopwatch.GetTimestamp(), 0);
        var deadlineStart = first == 0 ? Interlocked.Read(ref closeStarted) : first;
        RequestStop();
        while (started && (!inputWorker.Join(0) || !outputWorker.Join(0)))
        {
            inputWorker.CancelNative();
            outputWorker.CancelNative();
            // 显式Close后的Dispose复用同一期限；超时重入只核真实Join，不再追加等待。
            if (Stopwatch.GetElapsedTime(deadlineStart).TotalMilliseconds >= CleanupMilliseconds) return false;
            Thread.Sleep(10);
        }
        inputWorker.Dispose(); outputWorker.Dispose();
        Volatile.Write(ref closeCompleted, 1);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!Close()) return;
        inputWorker.Dispose(); outputWorker.Dispose();
        input.Dispose(); output.Dispose();
    }

    private sealed class Worker : IDisposable
    {
        private readonly object sync = new();
        private readonly SafeFileHandle external;
        private readonly NamedPipeServerStream pipe;
        private readonly bool toExternal;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Thread thread;
        private SafeFileHandle? nativeThread;
        private SafeFileHandle? ioEvent;
        private GCHandle pin;
        private IntPtr overlapped;
        private bool nativePending, launched;
        private bool resourcesReleased;
        private int stopping, completed, failed, liveBufferBytes;
        private long nativeWriteStarted;

        public Worker(SafeFileHandle external, NamedPipeServerStream pipe, bool toExternal)
        {
            this.external = external; this.pipe = pipe; this.toExternal = toExternal;
            thread = new Thread(Run) { IsBackground = true, Name = toExternal ? "SkylinesLink stdout" : "SkylinesLink stdin" };
        }
        public bool Completed => Volatile.Read(ref completed) != 0;
        public bool Failed => Volatile.Read(ref failed) != 0;
        public int LiveBufferBytes => Volatile.Read(ref liveBufferBytes);
        public bool WriteExpired
        {
            get
            {
                var start = Interlocked.Read(ref nativeWriteStarted);
                return start != 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= WriteMilliseconds;
            }
        }
        public void Start() { thread.Start(); launched = true; }
        public bool Join(int milliseconds) => !launched || thread.Join(milliseconds);

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref stopping, 1) == 0) cancellation.Cancel();
            CancelNative();
        }

        public void CancelNative()
        {
            lock (sync)
            {
                // 覆盖同步调用进入前的1168窗口；调用者直到Join前仍持取消责任。
                if (nativeThread is not null && !Completed) CancelSynchronousIo(nativeThread);
                if (nativePending) CancelIoEx(external, overlapped);
            }
        }

        private void Run()
        {
            try
            {
                var bytes = new byte[BufferCapacity];
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Volatile.Write(ref liveBufferBytes, bytes.Length);
                var process = GetCurrentProcess();
                if (!DuplicateHandle(process, GetCurrentThread(), process, out var duplicate, 0, false, 2)) throw new Win32Exception();
                lock (sync) nativeThread = duplicate;
                ioEvent = CreateEventW(IntPtr.Zero, true, false, null);
                if (ioEvent.IsInvalid) throw new Win32Exception();
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedValue>());
                while (Volatile.Read(ref stopping) == 0)
                {
                    if (toExternal)
                    {
                        var count = pipe.ReadAsync(bytes.AsMemory(), cancellation.Token).AsTask().GetAwaiter().GetResult();
                        if (count == 0) break;
                        var offset = 0;
                        while (offset < count && Volatile.Read(ref stopping) == 0)
                        {
                            var sent = Transfer(pin.AddrOfPinnedObject() + offset, count - offset, ioEvent);
                            if (sent == 0) throw new IOException("STDIO_RELAY_ZERO_WRITE");
                            offset += sent;
                        }
                    }
                    else
                    {
                        var count = Transfer(pin.AddrOfPinnedObject(), bytes.Length, ioEvent);
                        if (count == 0) break;
                        pipe.WriteAsync(bytes.AsMemory(0, count), cancellation.Token).AsTask().GetAwaiter().GetResult();
                    }
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref stopping) != 0) { }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 995 && Volatile.Read(ref stopping) != 0) { }
            catch { Volatile.Write(ref failed, 1); }
            finally
            {
                // 每个异步调用也已等待真实completion；绝不把Cancel/Dispose请求当完成。
                lock (sync) nativePending = false;
                pipe.Dispose();
                Volatile.Write(ref completed, 1);
            }
        }

        private int Transfer(IntPtr buffer, int count, SafeFileHandle ioEvent)
        {
            lock (sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ResetEvent(ioEvent)) throw new Win32Exception();
                Marshal.StructureToPtr(new NativeOverlappedValue { Event = ioEvent.DangerousGetHandle() }, overlapped, false);
                nativePending = true;
                if (toExternal) Interlocked.Exchange(ref nativeWriteStarted, Stopwatch.GetTimestamp());
            }
            try
            {
                var success = toExternal
                    ? WriteFile(external, buffer, (uint)count, IntPtr.Zero, overlapped)
                    : ReadFile(external, buffer, (uint)count, IntPtr.Zero, overlapped);
                var error = success ? 0 : Marshal.GetLastWin32Error();
                if (!success && error != 997)
                {
                    if (!toExternal && (error == 109 || error == 38)) return 0;
                    throw new Win32Exception(error);
                }
                if (!GetOverlappedResult(external, overlapped, out var transferred, true))
                {
                    error = Marshal.GetLastWin32Error();
                    if (!toExternal && (error == 109 || error == 38)) return 0;
                    throw new Win32Exception(error);
                }
                return checked((int)transferred);
            }
            finally
            {
                lock (sync) nativePending = false;
                Interlocked.Exchange(ref nativeWriteStarted, 0);
            }
        }

        public void Dispose()
        {
            if (!Join(0)) return;
            lock (sync)
            {
                if (resourcesReleased) return;
                resourcesReleased = true;
                nativeThread?.Dispose(); nativeThread = null;
                Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero;
                ioEvent?.Dispose();
                if (pin.IsAllocated) pin.Free();
                Volatile.Write(ref liveBufferBytes, 0);
                cancellation.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedValue { public nuint Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr bytes, uint count, IntPtr transferred, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, IntPtr bytes, uint count, IntPtr transferred, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(SafeFileHandle thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateEventW(IntPtr attributes, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ResetEvent(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
}
