using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SkylinesLink.Shared.Processes;

namespace SkylinesLink.Mod.Runtime
{
    internal sealed class SetupProcessResult
    {
        internal SetupProcessResult(string cause, uint? exitCode) { Cause = cause; ExitCode = exitCode; }
        internal string Cause { get; }
        internal uint? ExitCode { get; }
    }

    // Runs on its caller's worker. Completion means actual exit, reader Join and
    // resource release, not installation success or that the whole Job is empty.
    internal sealed class SetupProcessExecution
    {
        private const int BufferSize = 4096, OutputLimit = 32768, PollMilliseconds = 20;
        private const double CleanupSeconds = 5;
        private readonly string executable, workingDirectory;
        private readonly string[] arguments;
        private int runStarted, failure, readersStopping, cleanupPending, childExited, readersJoined = 1, liveBufferBytes;
        private uint processId;
        private long outputBytes;

        private enum Failure { None, Cancelled, Timeout, OutputLimit, Io, Start, Observation, Drain }

        internal SetupProcessExecution(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Missing executable.", nameof(executable));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            this.executable = executable;
            this.workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
            this.arguments = new string[arguments.Count];
            for (int index = 0; index < arguments.Count; index++)
                this.arguments[index] = arguments[index] ?? throw new ArgumentException("Null process argument.", nameof(arguments));
        }

        internal uint ProcessId => Volatile.Read(ref processId);
        internal long OutputBytes => Interlocked.Read(ref outputBytes);
        internal bool CleanupPending => Volatile.Read(ref cleanupPending) != 0;
        // False before creation as well as before an observed exit; ProcessId disambiguates.
        internal bool ChildExited => Volatile.Read(ref childExited) != 0;
        internal bool ReadersJoined => Volatile.Read(ref readersJoined) != 0;
        // Owned byte arrays, not a claim that the GC has already reclaimed their storage.
        internal int LiveBufferBytes => Volatile.Read(ref liveBufferBytes);

        internal SetupProcessResult Run(CancellationToken cancellation, TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (Interlocked.Exchange(ref runStarted, 1) != 0) throw new InvalidOperationException("Execution is single-use.");
            long started = Stopwatch.GetTimestamp();
            double timeoutSeconds = timeout.TotalSeconds;
            WindowsOwnedChild? child = null;
            SafeFileHandle? input = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null;
            OutputReader? outputReader = null, errorReader = null;
            uint? exitCode = null;
            try
            {
                if (!ObserveRequests(cancellation, started, timeoutSeconds))
                {
                    input = WindowsOwnedChild.OpenNull();
                    CreateOutputPipe(out outputRead, out outputWrite);
                    CreateOutputPipe(out errorRead, out errorWrite);
                    outputReader = new OutputReader(this, outputRead, "SkylinesLink setup stdout");
                    errorReader = new OutputReader(this, errorRead, "SkylinesLink setup stderr");
                    if (!ObserveRequests(cancellation, started, timeoutSeconds))
                    {
                        child = WindowsOwnedChild.CreateSuspended(executable, arguments, workingDirectory, input, outputWrite, errorWrite);
                        Volatile.Write(ref processId, child.ProcessId);
                        // These parent copies would otherwise prevent EOF after the child exits.
                        outputWrite.Dispose(); outputWrite = null;
                        errorWrite.Dispose(); errorWrite = null;
                        Volatile.Write(ref readersJoined, 0);
                        outputReader.Start();
                        errorReader.Start();
                        if (!ObserveRequests(cancellation, started, timeoutSeconds)) child.Resume();
                    }
                }
            }
            catch { Fail(Failure.Start); }
            finally
            {
                // This also covers Thread.Start/Resume failure with a still-suspended child.
                long cleanupStarted = Finish(child, outputReader, errorReader, cancellation, started, timeoutSeconds, ref exitCode);
                Release(cleanupStarted, ref child, ref outputReader, ref errorReader, ref input,
                    ref outputRead, ref outputWrite, ref errorRead, ref errorWrite);
            }
            return new SetupProcessResult(Cause(), exitCode);
        }

        private bool ObserveRequests(CancellationToken cancellation, long started, double timeoutSeconds)
        {
            if (cancellation.IsCancellationRequested) Fail(Failure.Cancelled);
            else if (ElapsedSeconds(started) >= timeoutSeconds) Fail(Failure.Timeout);
            return Volatile.Read(ref failure) != 0;
        }

        private long Finish(WindowsOwnedChild? child, OutputReader? output, OutputReader? error,
            CancellationToken cancellation, long started, double timeoutSeconds, ref uint? exitCode)
        {
            long cleanupStarted = 0;
            bool terminationRequested = false;
            while (true)
            {
                bool stopping = ObserveRequests(cancellation, started, timeoutSeconds);
                if (stopping)
                {
                    if (cleanupStarted == 0) cleanupStarted = Stopwatch.GetTimestamp();
                    Volatile.Write(ref readersStopping, 1);
                    if (child != null && !terminationRequested)
                    {
                        try { terminationRequested = child.Terminate(74); }
                        catch { Fail(Failure.Observation); }
                    }
                    output?.Cancel(); error?.Cancel();
                }
                if (child != null && !ChildExited)
                {
                    try
                    {
                        if (child.WaitForExit(PollMilliseconds))
                        {
                            Volatile.Write(ref childExited, 1);
                            try { exitCode = child.ExitCode; }
                            catch { Fail(Failure.Observation); }
                        }
                    }
                    catch { Fail(Failure.Observation); }
                }
                bool joined = false;
                try { joined = (output?.Join() ?? true) & (error?.Join() ?? true); }
                catch { Fail(Failure.Io); }
                Volatile.Write(ref readersJoined, joined ? 1 : 0);
                bool exited = child == null || ChildExited;
                if (exited && joined) return cleanupStarted == 0 ? Stopwatch.GetTimestamp() : cleanupStarted;
                if (exited && cleanupStarted == 0) cleanupStarted = Stopwatch.GetTimestamp();
                if (cleanupStarted != 0 && ElapsedSeconds(cleanupStarted) >= CleanupSeconds)
                {
                    // The normal exit path first permits EOF. A stuck reader still
                    // gets cancelled, but the owner remains until actual Join.
                    Volatile.Write(ref cleanupPending, 1);
                    // Aborting a normal drain is not successful EOF even if
                    // the cancelled threads subsequently Join.
                    Fail(Failure.Drain);
                    output?.Cancel(); error?.Cancel();
                }
                Pause();
            }
        }

        private void Release(long started, ref WindowsOwnedChild? child, ref OutputReader? output, ref OutputReader? error,
            ref SafeFileHandle? input, ref SafeFileHandle? outputRead, ref SafeFileHandle? outputWrite,
            ref SafeFileHandle? errorRead, ref SafeFileHandle? errorWrite)
        {
            while (true)
            {
                bool released = TryClose(ref output) & TryClose(ref error);
                released &= TryClose(ref input) & TryClose(ref outputRead) & TryClose(ref outputWrite)
                    & TryClose(ref errorRead) & TryClose(ref errorWrite) & TryClose(ref child);
                if (released) { Volatile.Write(ref cleanupPending, 0); return; }
                Fail(Failure.Io);
                if (ElapsedSeconds(started) >= CleanupSeconds) Volatile.Write(ref cleanupPending, 1);
                Pause();
            }
        }

        private static bool TryClose<T>(ref T? owned) where T : class, IDisposable
        {
            if (owned == null) return true;
            try { owned.Dispose(); owned = null; return true; }
            catch { return false; }
        }

        private void Fail(Failure cause)
        {
            Interlocked.CompareExchange(ref failure, (int)cause, 0);
            Volatile.Write(ref readersStopping, 1);
        }

        private void CountOutput(uint count)
        {
            // This is a stopping threshold, not a captured-byte budget. Two reads
            // already in flight may contribute their remaining fixed buffers.
            if (Interlocked.Add(ref outputBytes, count) > OutputLimit) Fail(Failure.OutputLimit);
        }

        private string Cause() => (Failure)Volatile.Read(ref failure) switch
        {
            Failure.None => "CHILD_EXITED", Failure.Cancelled => "CANCELLED", Failure.Timeout => "TIMEOUT",
            Failure.OutputLimit => "OUTPUT_LIMIT", Failure.Io => "IO_FAILED", Failure.Start => "START_FAILED",
            Failure.Drain => "OUTPUT_DRAIN_TIMEOUT",
            _ => "PROCESS_OBSERVATION_FAILED"
        };

        private static double ElapsedSeconds(long started) => (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        private void Pause()
        {
            try { Thread.Sleep(PollMilliseconds); }
            catch (ThreadInterruptedException) { Fail(Failure.Io); }
        }

        private static void CreateOutputPipe(out SafeFileHandle read, out SafeFileHandle write)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out read, out write, ref security, BufferSize))
            {
                int code = Marshal.GetLastWin32Error();
                read?.Dispose(); write?.Dispose();
                throw new Win32Exception(code);
            }
            try
            {
                if (!SetHandleInformation(read, 1, 0)) throw new Win32Exception();
            }
            catch { read.Dispose(); write.Dispose(); throw; }
        }

        private sealed class OutputReader : IDisposable
        {
            private readonly object sync = new object();
            private readonly SetupProcessExecution owner;
            private readonly SafeFileHandle pipe;
            private readonly Thread thread;
            private SafeFileHandle? nativeThread;
            private byte[]? buffer;
            private bool launchAttempted;

            internal OutputReader(SetupProcessExecution owner, SafeFileHandle pipe, string name)
            {
                this.owner = owner; this.pipe = pipe;
                thread = new Thread(Read) { IsBackground = true, Name = name };
                buffer = new byte[BufferSize];
                Interlocked.Add(ref owner.liveBufferBytes, BufferSize);
            }

            internal void Start() { launchAttempted = true; thread.Start(); }
            internal bool Join() => !launchAttempted ||
                (thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0 || thread.Join(0);

            internal void Cancel()
            {
                lock (sync)
                {
                    if (nativeThread == null) return;
                    try
                    {
                        // 1168 may precede entry into ReadFile. The coordinator
                        // repeats cancellation until Thread.Join actually succeeds.
                        if (!CancelSynchronousIo(nativeThread) && Marshal.GetLastWin32Error() != 1168) owner.Fail(Failure.Io);
                    }
                    catch { owner.Fail(Failure.Io); }
                }
            }

            private void Read()
            {
                try
                {
                    IntPtr process = GetCurrentProcess();
                    if (!DuplicateHandle(process, GetCurrentThread(), process, out var duplicate, 0, false, 2)) throw new Win32Exception();
                    lock (sync) nativeThread = duplicate;
                    byte[] bytes = buffer!;
                    while (Volatile.Read(ref owner.readersStopping) == 0)
                    {
                        bool success = ReadFile(pipe, bytes, (uint)bytes.Length, out uint count, IntPtr.Zero);
                        int code = success ? 0 : Marshal.GetLastWin32Error();
                        if (count != 0) owner.CountOutput(count);
                        if (!success)
                        {
                            if (code == 109 || code == 38 || (code == 995 && Volatile.Read(ref owner.readersStopping) != 0)) break;
                            throw new Win32Exception(code);
                        }
                        if (count == 0) break;
                    }
                }
                catch { owner.Fail(Failure.Io); }
            }

            public void Dispose()
            {
                if (!Join()) throw new InvalidOperationException("Reader is still running.");
                lock (sync) { nativeThread?.Dispose(); nativeThread = null; }
                if (buffer == null) return;
                buffer = null;
                Interlocked.Add(ref owner.liveBufferBytes, -BufferSize);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, uint count, out uint transferred, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    }
}
