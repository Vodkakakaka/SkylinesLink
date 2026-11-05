using System;
using System.Threading.Tasks;
using System.Threading;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.Mod.Runtime
{
    public enum GameConnectionPhase { Created, Starting, Activating, Active, Stopping, Stopped, Failed }

    public sealed class GameConnectionLifecycleSnapshot
    {
        internal GameConnectionLifecycleSnapshot(GameConnectionPhase phase, string? failureCode, string? exceptionType,
            bool fullyReleased, bool nativeLeaseRetained)
        { Phase = phase; FailureCode = failureCode; ExceptionType = exceptionType; FullyReleased = fullyReleased; NativeLeaseRetained = nativeLeaseRetained; }
        public GameConnectionPhase Phase { get; }
        public string? FailureCode { get; }
        public string? ExceptionType { get; }
        public bool FullyReleased { get; }
        public bool NativeLeaseRetained { get; }
    }

    /// <summary>IMod 的普通值生命周期协调；必须在 OnLoad 显式 new，不能依赖 IMod 构造器/字段初始化。</summary>
    public sealed class GameConnectionLifecycle
    {
        private readonly object sync = new object();
        private readonly Func<CancellationToken, Task<ConnectionAuthorityOwner?>> create;
        private readonly CancellationTokenSource startupCancellation = new CancellationTokenSource();
        private Task<bool>? starting, closing;
        private ConnectionAuthorityOwner? owner;
        private bool stopping;
        private bool initializationFullyReleased = true, initializationNativeLeaseRetained;
        private GameConnectionPhase phase;
        private string? failureCode, exceptionType;
        public GameConnectionLifecycle(Func<Task<ConnectionAuthorityOwner?>> create)
            : this(create == null ? throw new ArgumentNullException(nameof(create)) : _ => create()) { }
        public GameConnectionLifecycle(Func<CancellationToken, Task<ConnectionAuthorityOwner?>> create)
        { this.create = create ?? throw new ArgumentNullException(nameof(create)); }
        public ConnectionAuthorityOwner? Owner { get { lock (sync) return owner; } }
        public bool StopRequested { get { lock (sync) return stopping; } }
        public GameConnectionLifecycleSnapshot Read()
        {
            lock (sync) return new GameConnectionLifecycleSnapshot(phase, failureCode, exceptionType,
                owner?.FullyReleased ?? ((phase == GameConnectionPhase.Stopped || phase == GameConnectionPhase.Failed) && initializationFullyReleased),
                owner?.NativeLeaseRetained ?? initializationNativeLeaseRetained);
        }

        // Task 以 bool 明确结果，所有异常均被观察并以脱敏类别保留在 Read；不能仅凭 Task 完成报告 Ready。
        public Task<bool> StartAsync()
        {
            lock (sync)
            {
                if (stopping) return Task.FromResult(false);
                if (starting != null && !(starting.IsCompleted && owner == null && failureCode == "ENVIRONMENT_BUSY")) return starting;
                failureCode = null; exceptionType = null;
                initializationFullyReleased = true; initializationNativeLeaseRetained = false;
                phase = GameConnectionPhase.Starting;
                return starting = Task.Run(StartCoreAsync);
            }
        }
        private async Task<bool> StartCoreAsync()
        {
            ConnectionAuthorityOwner? created = null;
            bool activationAttempted = false;
            try
            {
                // 工厂可能在第一个 await 之前读目录；调用本身就在后台，不靠工厂自觉让出线程。
                created = await create(startupCancellation.Token).ConfigureAwait(false);
                if (created == null)
                {
                    lock (sync) { failureCode = "ENVIRONMENT_BUSY"; phase = stopping ? GameConnectionPhase.Stopping : GameConnectionPhase.Failed; }
                    return false;
                }
                Task? activation = null;
                lock (sync)
                {
                    owner = created;
                    if (stopping) created.RequestStop();
                    else
                    {
                        phase = GameConnectionPhase.Activating; activationAttempted = true;
                        // 只有任务调度在短锁内；Owner 激活的真实 IO 在自己的后台任务。
                        activation = created.ActivateAsync();
                    }
                }
                if (activation != null) await activation.ConfigureAwait(false);
                lock (sync)
                {
                    if (!stopping && created.IsActive) { phase = GameConnectionPhase.Active; return true; }
                }
                await created.CloseAsync().ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
            {
                if (created != null) await created.CloseAsync().ConfigureAwait(false);
                return false;
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    failureCode = activationAttempted ? "ACTIVATION_FAILED" : "INITIALIZATION_FAILED";
                    var initialization = error as ConnectionInitializationException;
                    exceptionType = initialization?.OriginalExceptionType ?? error.GetType().Name;
                    if (initialization != null)
                    {
                        initializationFullyReleased = initialization.FullyReleased;
                        initializationNativeLeaseRetained = initialization.NativeLeaseRetained;
                    }
                    stopping = true; phase = GameConnectionPhase.Failed;
                }
                if (created != null)
                {
                    try { await created.CloseAsync().ConfigureAwait(false); }
                    catch (Exception closeError) { lock (sync) { failureCode = "CLOSE_FAILED"; exceptionType = closeError.GetType().Name; } }
                }
                return false;
            }
        }
        public void RequestStop()
        {
            lock (sync)
            {
                if (!stopping)
                {
                    stopping = true; phase = GameConnectionPhase.Stopping;
                    startupCancellation.Cancel();
                    owner?.RequestStop();
                }
                if (closing == null) closing = Task.Run(CloseCoreAsync);
            }
        }
        public Task<bool> CloseAsync()
        { RequestStop(); lock (sync) return closing!; }
        private async Task<bool> CloseCoreAsync()
        {
            Task<bool>? startup;
            lock (sync) startup = starting;
            if (startup != null) await startup.ConfigureAwait(false);
            ConnectionAuthorityOwner? attached;
            lock (sync) attached = owner;
            try
            {
                if (attached != null) await attached.CloseAsync().ConfigureAwait(false);
                lock (sync) phase = GameConnectionPhase.Stopped;
                return attached?.FullyReleased ?? initializationFullyReleased;
            }
            catch (Exception error)
            {
                lock (sync) { failureCode = "CLOSE_FAILED"; exceptionType = error.GetType().Name; phase = GameConnectionPhase.Failed; }
                return false;
            }
            finally { startupCancellation.Dispose(); }
        }
    }
}
