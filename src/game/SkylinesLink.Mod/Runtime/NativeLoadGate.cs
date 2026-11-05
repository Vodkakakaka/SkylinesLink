using System;
using System.Threading.Tasks;
using SkylinesLink.Application.Simulation;

namespace SkylinesLink.Mod.Runtime
{
    /// <summary>可信原生适配器的提交事实。NotStarted 只能在明确尚未调用原生时返回。</summary>
    public sealed class NativeLoadInvocation
    {
        private NativeLoadInvocation(Task<bool>? completion, LoadTarget? boundTarget) { Completion = completion; BoundTarget = boundTarget; }
        public Task<bool>? Completion { get; }
        public LoadTarget? BoundTarget { get; }
        public static NativeLoadInvocation NotStarted() => new NativeLoadInvocation(null, null);
        public static NativeLoadInvocation Started(Task<bool> actualNativeCompletion, LoadTarget? verifiedBoundTarget) =>
            new NativeLoadInvocation(actualNativeCompletion ?? throw new ArgumentNullException(nameof(actualNativeCompletion)),
                verifiedBoundTarget);
    }

    /// <summary>实例级物理排他。没有等待队列、按客户端取消或手动 End；不在锁内调用原生/上层。</summary>
    public sealed class NativeLoadGate : INativeLoadPort
    {
        private readonly object sync = new object();
        private readonly string instanceId;
        private readonly Func<LoadIntent, NativeLoadInvocation> invoke;
        private Attempt? active;
        private bool stopping;
        public NativeLoadGate(string instanceId, Func<LoadIntent, NativeLoadInvocation> invoke)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 128) throw new ArgumentException("Instance identity is required.");
            this.instanceId = instanceId;
            this.invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        }
        public bool HasInFlight { get { lock (sync) return active != null; } }
        public bool SubmissionIsUnknown { get { lock (sync) return active?.SubmissionUnknown == true; } }
        public void StopAccepting() { lock (sync) stopping = true; }

        public LoadStart TryBegin(LoadIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            if (intent.Source.InstanceId != instanceId) return LoadStart.Rejected(LoadSubmission.NotStarted);
            Attempt attempt;
            lock (sync)
            {
                if (stopping) return LoadStart.Rejected(LoadSubmission.Stopped);
                if (active != null) return LoadStart.Rejected(LoadSubmission.Busy);
                attempt = new Attempt();
                active = attempt;
            }
            NativeLoadInvocation invocation;
            try { invocation = invoke(intent) ?? throw new InvalidOperationException("Native submission result missing."); }
            catch
            {
                // 调用后失去句柄不能猜测未提交；仍保排他。查询与玩家入口不依赖本产品 gate。
                lock (sync) attempt.SubmissionUnknown = true;
                return LoadStart.Rejected(LoadSubmission.Unknown);
            }
            if (invocation.Completion == null)
            {
                lock (sync) active = null;
                return LoadStart.Rejected(LoadSubmission.NotStarted);
            }
            attempt.NativeTask = invocation.Completion;
            // 包含同步已终结的 Task，统一发布物理终结事实；绝不由就绪通知或观察截止完成它。
            _ = ObserveAsync(attempt, invocation.Completion);
            return LoadStart.Submitted(invocation.BoundTarget, attempt.Observed.Task);
        }

        private async Task ObserveAsync(Attempt attempt, Task<bool> nativeTask)
        {
            NativeLoadCompletion terminal;
            try { bool result = await nativeTask.ConfigureAwait(false); terminal = new NativeLoadCompletion(NativeLoadTaskState.RanToCompletion, result); }
            catch { terminal = new NativeLoadCompletion(nativeTask.IsCanceled ? NativeLoadTaskState.Cancelled : NativeLoadTaskState.Faulted, null); }
            lock (sync)
            {
                if (ReferenceEquals(active, attempt)) active = null;
                attempt.NativeTask = null;
            }
            attempt.Observed.TrySetResult(terminal);
        }

        private sealed class Attempt
        {
            internal Task<bool>? NativeTask;
            internal bool SubmissionUnknown;
            internal readonly TaskCompletionSource<NativeLoadCompletion> Observed =
                new TaskCompletionSource<NativeLoadCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
