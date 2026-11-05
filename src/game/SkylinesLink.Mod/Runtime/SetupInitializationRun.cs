using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;
using SkylinesLink.Infrastructure.Installation;

namespace SkylinesLink.Mod.Runtime
{
    /// <summary>一次同包准备的实际寿命。完成任务只在子进程、读取器与文件责任均释放后结束。</summary>
    internal sealed class SetupInitializationRun
    {
        private readonly object sync = new object();
        private readonly ModBundleOrigin origin;
        private readonly LoadedManagedComponent[] components;
        private readonly string environment;
        private readonly TimeSpan timeout;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Timer deadline;
        private Task<SetupInitializationResult> completion = null!;
        private SetupProcessExecution? execution;
        private string phase = "InspectingSource";
        private int stopReason;
        private long closingAt;

        private SetupInitializationRun(ModBundleOrigin origin, IReadOnlyCollection<LoadedManagedComponent> components,
            string environment, TimeSpan timeout)
        {
            this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
            if (components == null) throw new ArgumentNullException(nameof(components));
            this.components = components.ToArray();
            this.environment = AuthorizationState.NormalizeEnvironment(environment);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
            this.timeout = timeout;
            deadline = new Timer(_ => RequestTermination(2), null, Timeout.Infinite, Timeout.Infinite);
        }

        internal static SetupInitializationRun Start(ModBundleOrigin origin,
            IReadOnlyCollection<LoadedManagedComponent> components, string environment, TimeSpan? timeout = null)
        {
            var run = new SetupInitializationRun(origin, components, environment, timeout ?? TimeSpan.FromMinutes(2));
            try
            {
                run.deadline.Change(run.timeout, Timeout.InfiniteTimeSpan);
                // The worker must run even if a stop arrives before it starts:
                // its body owns verification, process cleanup and readback.
                run.completion = Task.Run(run.RunCore);
                return run;
            }
            catch
            {
                run.deadline.Dispose();
                run.stop.Dispose();
                throw;
            }
        }

        internal Task<SetupInitializationResult> Completion => completion;

        internal void RequestStop()
            => RequestTermination(1);

        private void RequestTermination(int reason)
        {
            // Both request sources record their reason before cancelling the
            // shared token. A later request cannot rewrite an earlier timeout.
            Interlocked.CompareExchange(ref stopReason, reason, 0);
            MarkClosing();
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* The actual run already released its cancellation owner. */ }
        }

        internal SetupInitializationSnapshot Read()
        {
            lock (sync)
            {
                long since = Interlocked.Read(ref closingAt);
                bool pending = !completion.IsCompleted && since != 0 &&
                    (Stopwatch.GetTimestamp() - since) * 1000d / Stopwatch.Frequency >= 5000;
                return new SetupInitializationSnapshot(phase, pending || execution?.CleanupPending == true,
                    execution?.ProcessId ?? 0, execution?.OutputBytes ?? 0, execution?.LiveBufferBytes ?? 0,
                    execution?.ChildExited ?? true, execution?.ReadersJoined ?? true);
            }
        }

        private void MarkClosing() => Interlocked.CompareExchange(ref closingAt, Stopwatch.GetTimestamp(), 0);

        private SetupInitializationResult RunCore()
        {
            BundledCoreBinding? source = null;
            InstalledCoreBinding? installed = null;
            SetupProcessExecution? process = null;
            var cause = "SOURCE_REJECTED";
            uint? exitCode = null;
            var observed = PreparedInstallationState.NotObserved;
            try
            {
                try
                {
                    source = BundledCoreBinding.Open(origin, components, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    var remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) cause = "TIMEOUT";
                    else
                    {
                        cause = "SETUP_START_FAILED";
                        process = new SetupProcessExecution(source.SetupPath,
                            new[] { "initialize-core", source.Root, environment }, source.Root);
                        lock (sync) { execution = process; phase = "RunningSetup"; }
                        var result = process.Run(stop.Token, remaining);
                        cause = result.Cause;
                        // Timer cancellation and a user's explicit stop share
                        // one private token, but retain distinct result causes.
                        if (cause == "CANCELLED" && Volatile.Read(ref stopReason) != 1) cause = "TIMEOUT";
                        exitCode = result.ExitCode;
                    }
                }
                catch (OperationCanceledException)
                { cause = Volatile.Read(ref stopReason) == 1 ? "CANCELLED" : "TIMEOUT"; }
                catch (Exception)
                {
                    // No paths, exception messages or child output enter status.
                    // Run retains any created child/IO until actual cleanup ends.
                }

                // Human-readable CLI output is never an installation contract.
                // Failure/cancellation may still have published either file.
                if (source != null && process != null && process.ProcessId != 0)
                {
                    lock (sync) phase = "VerifyingInstallation";
                    try
                    {
                        installed = InstalledCoreBinding.OpenComplete(environment, components);
                        observed = installed.Version == source.Version &&
                            installed.CoreManifestSha256.Equals(source.CoreManifestSha256, StringComparison.OrdinalIgnoreCase)
                            ? PreparedInstallationState.MatchesSource : PreparedInstallationState.DifferentCore;
                    }
                    catch (Exception) { observed = PreparedInstallationState.Unavailable; }
                }
                return new SetupInitializationResult(cause, exitCode, observed);
            }
            finally
            {
                lock (sync) phase = "Closing";
                // Dispose is retried only for retained file-close responsibility;
                // a close failure must not complete the run or allow another Setup.
                ReleasePins(ref installed, ref source);
                deadline.Dispose();
                stop.Dispose();
                lock (sync) phase = "Completed";
            }

        }

        private void ReleasePins(ref InstalledCoreBinding? installed, ref BundledCoreBinding? source)
        {
            while (installed != null || source != null)
            {
                try { installed?.Dispose(); installed = null; }
                catch (Exception) { MarkClosing(); }
                try { source?.Dispose(); source = null; }
                catch (Exception) { MarkClosing(); }
                if (installed != null || source != null) Thread.Sleep(250);
            }
        }
    }

    internal enum PreparedInstallationState { NotObserved, MatchesSource, DifferentCore, Unavailable }

    internal sealed class SetupInitializationResult
    {
        internal SetupInitializationResult(string processCause, uint? exitCode, PreparedInstallationState installation)
        { ProcessCause = processCause; ExitCode = exitCode; Installation = installation; }
        internal string ProcessCause { get; }
        internal uint? ExitCode { get; }
        internal PreparedInstallationState Installation { get; }
        internal bool Ready => ProcessCause == "CHILD_EXITED" && ExitCode == 0 && Installation == PreparedInstallationState.MatchesSource;
    }

    internal sealed class SetupInitializationSnapshot
    {
        internal SetupInitializationSnapshot(string phase, bool cleanupPending, uint processId, long outputBytes,
            int liveBufferBytes, bool childExited, bool readersJoined)
        { Phase = phase; CleanupPending = cleanupPending; ProcessId = processId; OutputBytes = outputBytes;
          LiveBufferBytes = liveBufferBytes; ChildExited = childExited; ReadersJoined = readersJoined; }
        internal string Phase { get; }
        internal bool CleanupPending { get; }
        internal uint ProcessId { get; }
        internal long OutputBytes { get; }
        internal int LiveBufferBytes { get; }
        internal bool ChildExited { get; }
        internal bool ReadersJoined { get; }
    }
}
