using System;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Simulation
{
    public enum LoadSubmission { Busy, Stopped, NotStarted, Submitted, Unknown }
    public enum NativeLoadTaskState { NotStarted, Running, RanToCompletion, Faulted, Cancelled, Unknown }
    public enum LoadTargetMatch { Unknown, Different, IdentityOnly, Exact }
    public enum LoadObservationEnd { Open, Expired, Interrupted, OwnerStopped }

    /// <summary>实际 Load Task 结束后的普通值；Task 正常结束与它返回的 bool 分别保留。</summary>
    public sealed class NativeLoadCompletion
    {
        public NativeLoadCompletion(NativeLoadTaskState physicalTaskState, bool? loadReturnValue)
        {
            if (physicalTaskState != NativeLoadTaskState.RanToCompletion && physicalTaskState != NativeLoadTaskState.Faulted && physicalTaskState != NativeLoadTaskState.Cancelled)
                throw new ArgumentException("A physical terminal state is required.");
            CheckReturnValue(physicalTaskState, loadReturnValue);
            PhysicalTaskState = physicalTaskState; LoadReturnValue = loadReturnValue;
        }
        public NativeLoadTaskState PhysicalTaskState { get; }
        public bool? LoadReturnValue { get; }
        internal static void CheckReturnValue(NativeLoadTaskState state, bool? value)
        {
            if (!Enum.IsDefined(typeof(NativeLoadTaskState), state) || (state == NativeLoadTaskState.RanToCompletion) != value.HasValue)
                throw new ArgumentException("Load return value does not match physical Task state.");
        }
    }

    /// <summary>稳定保存身份与可用内容版本；没有显示城名或通过 World 指针推测的版本。</summary>
    public sealed class LoadTarget
    {
        public LoadTarget(string providerId, string assetId, string? contentVersion)
        {
            CheckIdentity(providerId); CheckIdentity(assetId);
            if (contentVersion != null) CheckIdentity(contentVersion);
            ProviderId = providerId; AssetId = assetId; ContentVersion = contentVersion;
        }
        public string ProviderId { get; }
        public string AssetId { get; }
        public string? ContentVersion { get; }
        public bool SameIdentity(LoadTarget other) => ProviderId == other.ProviderId && AssetId == other.AssetId;
        internal static void CheckIdentity(string value)
        {
            // 与契约 Identity 的 Unicode scalar 语义一致；空白不额外收窄合法身份，孤立代理项不是标量。
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("A bounded stable identity is required.");
            int scalars = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (++scalars > 128) throw new ArgumentException("A bounded stable identity is required.");
                if (char.IsHighSurrogate(value[i]))
                { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ArgumentException("A valid scalar identity is required."); }
                else if (char.IsLowSurrogate(value[i])) throw new ArgumentException("A valid scalar identity is required.");
            }
        }
    }

    public sealed class LoadIntent
    {
        public LoadIntent(string transitionId, LoadTarget target, WorldContextSnapshot source)
        {
            LoadTarget.CheckIdentity(transitionId);
            TransitionId = transitionId; Target = target ?? throw new ArgumentNullException(nameof(target));
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }
        public string TransitionId { get; }
        public LoadTarget Target { get; }
        public WorldContextSnapshot Source { get; }
    }

    public sealed class LoadStart
    {
        private LoadStart(LoadSubmission submission, LoadTarget? boundTarget, Task<NativeLoadCompletion>? completion)
        { Submission = submission; BoundTarget = boundTarget; NativeCompletion = completion; }
        public LoadSubmission Submission { get; }
        // 这是可信端口在提交时核实的值，不得把请求 expected version 原样当作读取证据。
        public LoadTarget? BoundTarget { get; }
        // 这是普通事实通知，不是原生句柄；实际 Task<bool> 仅由 Runtime 持有，不能被客户端取消。
        public Task<NativeLoadCompletion>? NativeCompletion { get; }
        public static LoadStart Rejected(LoadSubmission reason)
        {
            if (reason != LoadSubmission.Busy && reason != LoadSubmission.Stopped && reason != LoadSubmission.NotStarted && reason != LoadSubmission.Unknown)
                throw new ArgumentOutOfRangeException(nameof(reason));
            return new LoadStart(reason, null, null);
        }
        public static LoadStart Submitted(LoadTarget? boundTarget, Task<NativeLoadCompletion> completion) =>
            new LoadStart(LoadSubmission.Submitted, boundTarget,
                completion ?? throw new ArgumentNullException(nameof(completion)));
    }

    /// <summary>仅由正常提交阶段消费；不接收客户端取消令牌。实际线程与原生 Task 归适配器。</summary>
    public interface INativeLoadPort { LoadStart TryBegin(LoadIntent intent); }

    public sealed class LoadReadyObservation
    {
        public LoadReadyObservation(WorldContextSnapshot context, LoadTarget? actualTarget, string? correlatedTransitionId)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            if (context.State != "ready") throw new ArgumentException("A verified ready observation is required.");
            if (correlatedTransitionId != null) LoadTarget.CheckIdentity(correlatedTransitionId);
            ActualTarget = actualTarget; CorrelatedTransitionId = correlatedTransitionId;
        }
        public WorldContextSnapshot Context { get; }
        public LoadTarget? ActualTarget { get; }
        // 只有可信适配器具备本次调用关联证据才填写；同资产、时间先后或当前 pending 均不充分。
        public string? CorrelatedTransitionId { get; }
    }

    public sealed class LoadTransitionSnapshot
    {
        internal LoadTransitionSnapshot(LoadSubmission submission, NativeLoadTaskState physicalTaskState, bool? loadReturnValue, LoadTargetMatch targetMatch,
            bool isCurrent, bool correlatedReady, bool historicalCompletion, LoadObservationEnd observationEnd, WorldContextSnapshot? observationContext)
        {
            Submission = submission; PhysicalTaskState = physicalTaskState; LoadReturnValue = loadReturnValue; TargetMatch = targetMatch; IsObservationCurrent = isCurrent;
            CorrelatedTargetReady = correlatedReady; HistoricalCompletionProven = historicalCompletion; ObservationEnd = observationEnd;
            ObservationContext = observationContext;
        }
        public LoadSubmission Submission { get; }
        public NativeLoadTaskState PhysicalTaskState { get; }
        public bool? LoadReturnValue { get; }
        public LoadTargetMatch TargetMatch { get; }
        public bool IsObservationCurrent { get; }
        public bool CurrentTargetProven => IsObservationCurrent && TargetMatch == LoadTargetMatch.Exact;
        public bool CorrelatedTargetReady { get; }
        public bool HistoricalCompletionProven { get; }
        public LoadObservationEnd ObservationEnd { get; }
        public WorldContextSnapshot? ObservationContext { get; }
    }

    /// <summary>单次操作的有界普通值证据。没有原生对象、计时器、全局表或第二 journal。</summary>
    public sealed class LoadTransition
    {
        private readonly object sync = new object();
        private readonly LoadIntent intent;
        private bool started;
        private LoadStart start = LoadStart.Rejected(LoadSubmission.NotStarted);
        private NativeLoadTaskState physicalTaskState = NativeLoadTaskState.NotStarted;
        private bool? loadReturnValue;
        private LoadReadyObservation? latestReady;
        private LoadReadyObservation? correlatedReady;
        private bool correlatedProof;
        private bool historicalCompletion;
        private LoadObservationEnd observationEnd;
        private readonly TaskCompletionSource<bool> nativeObservation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public LoadTransition(LoadIntent intent) { this.intent = intent ?? throw new ArgumentNullException(nameof(intent)); }

        public Task NativeObservationCompletion => nativeObservation.Task;
        public LoadTarget? BoundTarget { get { lock (sync) return start.BoundTarget; } }
        public LoadSubmission Submission { get { lock (sync) return start.Submission; } }

        // 调用者须先完成 Operations 的受理/提交守卫；本类型不复制授权或 durable intent 机制。
        public void Start(INativeLoadPort port)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            lock (sync)
            {
                if (started) throw new InvalidOperationException("Transition has already attempted submission.");
                started = true;
                physicalTaskState = NativeLoadTaskState.Unknown;
                start = LoadStart.Rejected(LoadSubmission.Unknown);
            }
            LoadStart receipt = port.TryBegin(intent);
            lock (sync)
            {
                start = receipt;
                physicalTaskState = receipt.Submission == LoadSubmission.Submitted ? NativeLoadTaskState.Running :
                    receipt.Submission == LoadSubmission.Unknown ? NativeLoadTaskState.Unknown : NativeLoadTaskState.NotStarted;
                AccumulateProof();
            }
            if (receipt.NativeCompletion != null) _ = ObserveNativeAsync(receipt.NativeCompletion);
            else if (receipt.Submission != LoadSubmission.Unknown) nativeObservation.TrySetResult(true);
        }

        private async Task ObserveNativeAsync(Task<NativeLoadCompletion> completion)
        {
            NativeLoadCompletion? terminal;
            try { terminal = await completion.ConfigureAwait(false); }
            catch { terminal = null; }
            lock (sync)
            {
                physicalTaskState = terminal?.PhysicalTaskState ?? NativeLoadTaskState.Unknown;
                loadReturnValue = terminal?.LoadReturnValue;
                AccumulateProof();
            }
            nativeObservation.TrySetResult(true);
        }

        public bool ObserveReady(LoadReadyObservation observation)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            lock (sync)
            {
                if (observationEnd != LoadObservationEnd.Open || observation.Context.InstanceId != intent.Source.InstanceId) return false;
                if (latestReady == null || observation.Context.Generation >= latestReady.Context.Generation) latestReady = observation;
                if (started && observation.Context.Generation > intent.Source.Generation && observation.CorrelatedTransitionId == intent.TransitionId &&
                    observation.ActualTarget != null && intent.Target.SameIdentity(observation.ActualTarget) &&
                    (intent.Target.ContentVersion == null || intent.Target.ContentVersion == observation.ActualTarget.ContentVersion))
                    correlatedReady = observation;
                AccumulateProof();
                return true;
            }
        }

        public void EndObservation(LoadObservationEnd reason)
        {
            if (reason != LoadObservationEnd.Expired && reason != LoadObservationEnd.Interrupted && reason != LoadObservationEnd.OwnerStopped)
                throw new ArgumentOutOfRangeException(nameof(reason));
            lock (sync) { if (observationEnd == LoadObservationEnd.Open) observationEnd = reason; }
        }

        public LoadTransitionSnapshot Read(WorldContextSnapshot current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            lock (sync)
            {
                LoadTarget effectiveTarget = EffectiveTarget();
                LoadTargetMatch match = Match(effectiveTarget, latestReady?.ActualTarget);
                bool isCurrent = latestReady != null && current.State == "ready" && current.InstanceId == latestReady.Context.InstanceId &&
                    current.Generation == latestReady.Context.Generation;
                return new LoadTransitionSnapshot(start.Submission, physicalTaskState, loadReturnValue, match, isCurrent, correlatedProof,
                    historicalCompletion, observationEnd, latestReady?.Context);
            }
        }

        private LoadTarget EffectiveTarget() => intent.Target.ContentVersion == null && start.BoundTarget != null &&
            intent.Target.SameIdentity(start.BoundTarget) ? start.BoundTarget : intent.Target;

        private void AccumulateProof()
        {
            var target = EffectiveTarget();
            correlatedProof |= start.Submission == LoadSubmission.Submitted && Match(target, correlatedReady?.ActualTarget) == LoadTargetMatch.Exact &&
                Match(target, start.BoundTarget) == LoadTargetMatch.Exact;
            historicalCompletion |= physicalTaskState == NativeLoadTaskState.RanToCompletion && loadReturnValue == true && correlatedProof;
        }

        private static LoadTargetMatch Match(LoadTarget expected, LoadTarget? actual)
        {
            if (actual == null) return LoadTargetMatch.Unknown;
            if (!expected.SameIdentity(actual)) return LoadTargetMatch.Different;
            if (expected.ContentVersion == null || actual.ContentVersion == null) return LoadTargetMatch.IdentityOnly;
            return expected.ContentVersion == actual.ContentVersion ? LoadTargetMatch.Exact : LoadTargetMatch.Different;
        }
    }
}
