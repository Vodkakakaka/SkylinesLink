using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SkylinesLink.Application.Context
{
    /// <summary>当前加载范围内共享一次有界采样及其发布寿命。</summary>
    public abstract class ScopedReadBroker<TQuery, TCapture> where TCapture : class
    {
        private const int MaximumConsumers = 8;
        private readonly object sync = new object();
        private readonly WorldContextTracker context;
        private readonly Func<Action, Task> publishWorker;
        private CaptureWork? active;
        private bool stopped;

        private protected ScopedReadBroker(WorldContextTracker context, Func<Action, Task>? publishWorker)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.publishWorker = publishWorker ?? (action => Task.Run(action));
        }

        public Task WhenIdle { get { lock (sync) return active?.Ended.Task ?? Task.CompletedTask; } }

        private protected ReadLease Request(TQuery query)
        {
            lock (sync)
            {
                if (stopped || context.IsStopped) return Immediate("STOPPED");
                var scope = context.Read();
                if (scope.State != "ready") return Immediate(scope.State == "menu" ? "MENU" :
                    scope.State == "loading" ? "LOADING" : "SOURCE_UNAVAILABLE");
                if (active != null && (active.Phase == CapturePhase.Publishing || active.AddedConsumers == MaximumConsumers ||
                    !SameScope(active.Scope, scope) || !SameQuery(active.Query, query))) return Immediate("BUSY");
                if (active == null) active = new CaptureWork(this, scope, query);
                var lease = new ReadLease(this, active);
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

        public void Complete(CaptureWork work, TCapture capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            lock (sync)
            {
                RequireClaim(work);
                if (SameScope(work.Scope, ScopeOf(capture)) && MatchesCapture(work.Query, capture)) work.Capture = capture;
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
                work.Failure = reason ?? "CAPTURE_FAILED";
                work.Phase = CapturePhase.Publishing;
            }
            StartPublisher(work);
        }

        /// <summary>发送线程在冻结响应前调用；同代身份富化不使已采样事实失效。</summary>
        public JToken GetCurrentResult(JToken result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (!HasResultScope(result)) return result;
            var scope = result["scope"];
            string? instance = (string?)scope?["instanceId"];
            string? generationText = (string?)scope?["generation"];
            bool parsed = long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out long generation);
            lock (sync)
            {
                if (stopped || context.IsStopped) return Unavailable("STOPPED");
                if (!parsed || generation <= 0 || instance == null || !context.AllowsWorld(instance, generation))
                    return Unavailable("SCOPE_CHANGED");
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
                ReadLease? lease;
                TCapture? capture;
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
                    var result = reason != null ? Unavailable(reason) :
                        Encode(capture!);
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
                        lease.TryComplete(Unavailable(failure ?? "CAPTURE_FAILED"));
                if (work.PublishingLease != null && !work.PublishingLease.IsCancelled)
                    work.PublishingLease.TryComplete(Unavailable(failure ?? "CAPTURE_FAILED"));
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
        private ReadLease Immediate(string reason) => ReadLease.Completed(Unavailable(reason));

        private protected virtual bool SameQuery(TQuery current, TQuery requested) => true;
        private protected virtual bool MatchesCapture(TQuery query, TCapture capture) => true;
        private protected abstract WorldContextSnapshot ScopeOf(TCapture capture);
        private protected abstract bool HasResultScope(JToken result);
        private protected abstract JToken Encode(TCapture capture);
        private protected abstract JToken Unavailable(string reason);

        internal enum CapturePhase { Pending, Capturing, Publishing }
        public sealed class CaptureWork
        {
            internal CaptureWork(ScopedReadBroker<TQuery, TCapture> owner, WorldContextSnapshot scope, TQuery query)
            { Owner = owner; Scope = scope; Query = query; }
            public WorldContextSnapshot Scope { get; }
            public TQuery Query { get; }
            internal readonly ScopedReadBroker<TQuery, TCapture> Owner;
            internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ReadLease?[] Slots = new ReadLease?[MaximumConsumers];
            internal int AddedConsumers;
            internal CapturePhase Phase;
            internal TCapture? Capture;
            internal string? Failure;
            internal ReadLease? PublishingLease;
        }

        public sealed class ReadLease : IDisposable
        {
            private readonly TaskCompletionSource<JToken> completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            private ScopedReadBroker<TQuery, TCapture>? owner;
            private CaptureWork? work;
            private int cancelled;
            internal ReadLease(ScopedReadBroker<TQuery, TCapture> owner, CaptureWork work) { this.owner = owner; this.work = work; }
            private ReadLease(JToken result) { completion.TrySetResult(result); }
            internal static ReadLease Completed(JToken result) => new ReadLease(result);
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
