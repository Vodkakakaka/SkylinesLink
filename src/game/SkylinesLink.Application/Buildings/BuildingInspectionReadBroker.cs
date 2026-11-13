using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Buildings
{
    /// <summary>一个加载作用域的有限采样cohort；没有跨请求缓存、分页或原生对象。</summary>
    public sealed class BuildingInspectionReadBroker
    {
        private const int MaximumConsumers = 8;
        private readonly object sync = new object();
        private readonly WorldContextTracker context;
        private readonly Func<Action, Task> publishWorker;
        private CaptureWork? active;
        private bool stopped;

        public BuildingInspectionReadBroker(WorldContextTracker context, Func<Action, Task>? publishWorker = null)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.publishWorker = publishWorker ?? (action => Task.Run(action));
        }

        public Task WhenIdle { get { lock (sync) return active?.Ended.Task ?? Task.CompletedTask; } }

        public BuildingInspectionRead Capture(BuildingInspectionQuery query)
        {
            lock (sync)
            {
                if (query == null) throw new ArgumentNullException(nameof(query));
                if (stopped || context.IsStopped) return Immediate("STOPPED");
                var scope = context.Read();
                if (scope.State != "ready") return Immediate(scope.State == "menu" ? "MENU" :
                    scope.State == "loading" ? "LOADING" : "SOURCE_UNAVAILABLE");
                if (active != null && (active.Phase == CapturePhase.Publishing || active.AddedConsumers == MaximumConsumers ||
                    !SameScope(active.Scope, scope) || !active.Query.SameAs(query))) return Immediate("BUSY");
                if (active == null) active = new CaptureWork(this, scope, query);
                var lease = new BuildingInspectionRead(this, active);
                active.Slots[active.AddedConsumers++] = lease;
                return lease;
            }
        }

        /// <summary>仅游戏线程认领；成功认领必须报告Complete或Fail，即使所有消费者已取消。</summary>
        public bool TryClaim(out CaptureWork? work)
        {
            CaptureWork? invalid = null;
            lock (sync)
            {
                work = active;
                if (stopped || work == null || work.Phase != CapturePhase.Pending) { work = null; return false; }
                if (context.IsStopped || !context.AllowsWorld(work.Scope.InstanceId, work.Scope.Generation))
                {
                    work.Failure = context.IsStopped ? "STOPPED" : "SCOPE_CHANGED";
                    work.Phase = CapturePhase.Publishing; invalid = work; work = null;
                }
                else { work.Phase = CapturePhase.Capturing; return true; }
            }
            StartPublisher(invalid!);
            return false;
        }

        public void Complete(CaptureWork work, BuildingInspectionCapture capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            lock (sync)
            {
                RequireClaim(work);
                if (SameScope(work.Scope, capture.Scope) && work.Query.SameAs(capture.Query) &&
                    (work.Query.SelectedInfo || capture.Reference.Index == work.Query.Index && capture.Reference.Version == work.Query.Version)) work.Capture = capture;
                else work.Failure = "SCOPE_CHANGED";
                work.Phase = CapturePhase.Publishing;
            }
            StartPublisher(work);
        }

        public void Fail(CaptureWork work, string reason)
        {
            lock (sync)
            {
                RequireClaim(work);
                work.Failure = BuildingInspectionQueryService.NormalizeReason(reason);
                work.Phase = CapturePhase.Publishing;
            }
            StartPublisher(work);
        }

        /// <summary>发送线程在冻结响应前调用；同代身份富化不使已采样事实失效。</summary>
        public JToken GetCurrentResult(JToken result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if ((string?)result["kind"] == "unavailable") return result;
            var scope = result["scope"];
            string? instance = (string?)scope?["instanceId"];
            string? generationText = (string?)scope?["generation"];
            bool parsed = long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out long generation);
            lock (sync)
            {
                if (stopped || context.IsStopped) return BuildingInspectionQueryService.Unavailable("STOPPED");
                if (!parsed || generation <= 0 || instance == null || !context.AllowsWorld(instance, generation))
                    return BuildingInspectionQueryService.Unavailable("SCOPE_CHANGED");
                return result;
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                var work = active;
                if (work == null) return;
                // Dispose only cancels each consumer. A claimed native capture or
                // scheduled publisher remains active until its real completion.
                foreach (var lease in work.Slots) lease?.Dispose();
                work.PublishingLease?.Dispose();
            }
        }

        private void StartPublisher(CaptureWork work)
        {
            lock (sync)
            {
                if (!ReferenceEquals(active, work)) return;
                if (!HasConsumers(work)) { Finish(work, null); return; }
            }
            Task publisher;
            try { publisher = publishWorker(() => Publish(work)) ?? throw new InvalidOperationException("A publisher task is required."); }
            catch { Finish(work, "CAPTURE_FAILED"); return; }
            publisher.ContinueWith(done =>
            {
                if (done.IsFaulted) _ = done.Exception;
                Finish(work, done.IsFaulted || done.IsCanceled ? "CAPTURE_FAILED" : null);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Publish(CaptureWork work)
        {
            for (int index = 0; index < work.Slots.Length; index++)
            {
                BuildingInspectionRead? lease;
                BuildingInspectionCapture? capture;
                string? reason;
                lock (sync)
                {
                    lease = work.Slots[index]; work.Slots[index] = null;
                    if (lease == null) continue;
                    work.PublishingLease = lease;
                    capture = work.Capture; reason = work.Failure;
                    if (stopped || context.IsStopped) reason = "STOPPED";
                    else if (!context.AllowsWorld(work.Scope.InstanceId, work.Scope.Generation)) reason = "SCOPE_CHANGED";
                }
                if (!lease.IsCancelled)
                {
                    var result = reason != null ? BuildingInspectionQueryService.Unavailable(reason) :
                        BuildingInspectionQueryService.Encode(capture!);
                    lease.TryComplete(GetCurrentResult(result));
                }
                // Keep the current lease owned if encoding throws. Finish sees
                // it after the real publisher faults and terminates its waiter.
                lock (sync) work.PublishingLease = null;
            }
        }

        private void Finish(CaptureWork work, string? failure)
        {
            lock (sync)
            {
                if (!ReferenceEquals(active, work)) return;
                foreach (var lease in work.Slots)
                    if (lease != null && !lease.IsCancelled)
                        lease.TryComplete(BuildingInspectionQueryService.Unavailable(failure ?? "CAPTURE_FAILED"));
                if (work.PublishingLease != null && !work.PublishingLease.IsCancelled)
                    work.PublishingLease.TryComplete(BuildingInspectionQueryService.Unavailable(failure ?? "CAPTURE_FAILED"));
                Array.Clear(work.Slots, 0, work.Slots.Length);
                work.PublishingLease = null; work.Capture = null;
                // Observers must never see idle while the actual worker is still
                // responsible for a capture, result or consumer reference.
                work.Ended.TrySetResult(true);
                active = null;
            }
        }

        private void Cancel(CaptureWork work)
        {
            lock (sync)
            {
                if (!ReferenceEquals(active, work) || work.Phase != CapturePhase.Pending || HasConsumers(work)) return;
                Array.Clear(work.Slots, 0, work.Slots.Length);
                work.Ended.TrySetResult(true); active = null;
            }
        }

        private static bool HasConsumers(CaptureWork work)
        {
            foreach (var lease in work.Slots) if (lease != null && !lease.IsCancelled) return true;
            return work.PublishingLease != null && !work.PublishingLease.IsCancelled;
        }

        private void RequireClaim(CaptureWork work)
        {
            if (work == null || !ReferenceEquals(work.Owner, this) || !ReferenceEquals(active, work) || work.Phase != CapturePhase.Capturing)
                throw new InvalidOperationException("The claimed native capture must be reported exactly once.");
        }

        private static bool SameScope(WorldContextSnapshot left, WorldContextSnapshot right) =>
            left.InstanceId == right.InstanceId && left.Generation == right.Generation;
        private static BuildingInspectionRead Immediate(string reason) => BuildingInspectionRead.Completed(BuildingInspectionQueryService.Unavailable(reason));

        internal enum CapturePhase { Pending, Capturing, Publishing }
        public sealed class CaptureWork
        {
            internal CaptureWork(BuildingInspectionReadBroker owner, WorldContextSnapshot scope, BuildingInspectionQuery query) { Owner = owner; Scope = scope; Query = query; }
            public BuildingInspectionQuery Query { get; }
            public WorldContextSnapshot Scope { get; }
            internal readonly BuildingInspectionReadBroker Owner;
            internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly BuildingInspectionRead?[] Slots = new BuildingInspectionRead?[MaximumConsumers];
            internal int AddedConsumers;
            internal CapturePhase Phase;
            internal BuildingInspectionCapture? Capture;
            internal string? Failure;
            internal BuildingInspectionRead? PublishingLease;
        }

        public sealed class BuildingInspectionRead : IDisposable
        {
            private readonly TaskCompletionSource<JToken> completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            private BuildingInspectionReadBroker? owner;
            private CaptureWork? work;
            private int cancelled;
            internal BuildingInspectionRead(BuildingInspectionReadBroker owner, CaptureWork work) { this.owner = owner; this.work = work; }
            private BuildingInspectionRead(JToken result) { completion.TrySetResult(result); }
            internal static BuildingInspectionRead Completed(JToken result) => new BuildingInspectionRead(result);
            public Task<JToken> Completion => completion.Task;
            internal bool IsCancelled => Volatile.Read(ref cancelled) != 0;
            internal void TryComplete(JToken result) => completion.TrySetResult(result);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
                completion.TrySetCanceled();
                var current = Interlocked.Exchange(ref owner, null);
                var capture = Interlocked.Exchange(ref work, null);
                if (current != null && capture != null) current.Cancel(capture);
            }
        }
    }
}
