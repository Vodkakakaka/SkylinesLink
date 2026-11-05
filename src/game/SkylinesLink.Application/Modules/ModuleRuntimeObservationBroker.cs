using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Application.Modules
{
    /// <summary>注册模块观察的唯一采样槽、有限保留与真正工作结束；不访问游戏或IO。</summary>
    public sealed class ModuleRuntimeObservationBroker
    {
        private const int MaximumProfiles = 8, MaximumConsumers = 8;
        private readonly object sync = new object();
        private readonly string instanceId;
        private readonly Func<long> milliseconds;
        private readonly Func<Action, Task> publishWorker;
        private readonly Dictionary<string, Retained> retained = new Dictionary<string, Retained>(StringComparer.Ordinal);
        private CaptureWork? active;
        private bool stopped;
        private long nextRetainedExpiry = long.MaxValue;

        public ModuleRuntimeObservationBroker(string instanceId, Func<long>? monotonicMilliseconds = null,
            Func<Action, Task>? publishWorker = null)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 128) throw new ArgumentException("Instance identity is required.");
            this.instanceId = instanceId;
            milliseconds = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
            this.publishWorker = publishWorker ?? (action => Task.Run(action));
        }

        public Task WhenIdle { get { lock (sync) return active?.Ended.Task ?? Task.CompletedTask; } }
        public string InstanceId => instanceId;
        public ModuleObservationUsage ReadUsage()
        {
            lock (sync)
            {
                EvictExpired();
                return new ModuleObservationUsage(retained.Count, active == null ? 0 : 1,
                    active?.AddedConsumers ?? 0, active?.Phase ?? "idle");
            }
        }

        public ModuleRuntimeRead Capture(string profileInstanceId)
        {
            AuthorizationState.ValidateProfileInstanceId(profileInstanceId);
            lock (sync)
            {
                if (stopped) return Immediate("STOPPED");
                EvictExpired();
                if (active != null && (active.Phase == "publishing" || active.AddedConsumers == MaximumConsumers)) return Immediate("BUSY");
                var profiles = new HashSet<string>(retained.Keys, StringComparer.Ordinal);
                if (active != null) foreach (string profile in active.Profiles) profiles.Add(profile);
                profiles.Add(profileInstanceId);
                if (profiles.Count > MaximumProfiles) return Immediate("BUSY");
                if (active == null) active = new CaptureWork(this);
                var work = active;
                var lease = new ModuleRuntimeRead(this, work, profileInstanceId);
                work.Slots[work.AddedConsumers++] = lease;
                work.Profiles.Add(profileInstanceId);
                return lease;
            }
        }

        public ModuleRuntimeRead Page(string profileInstanceId, string snapshotId, int offset)
        {
            AuthorizationState.ValidateProfileInstanceId(profileInstanceId);
            if (!LowerHex(snapshotId) || offset < 0 || offset > ModuleRegistryCapture.MaxEntries)
                return Immediate("SNAPSHOT_UNAVAILABLE");
            ModuleRuntimeQueryService.ModulePage? page;
            lock (sync)
            {
                if (stopped) return Immediate("STOPPED");
                EvictExpired();
                if (!retained.TryGetValue(profileInstanceId, out var saved) || saved.Id != snapshotId)
                    return Immediate("SNAPSHOT_UNAVAILABLE");
                if (offset > saved.Capture.Entries.Count) return Immediate("PAGE_OUT_OF_RANGE");
                page = new ModuleRuntimeQueryService.ModulePage(saved.Id, instanceId, saved.Capture, offset);
            }
            // DOM在锁外、原receiver已持ordinary信用时构造，不保父snapshot数组。
            return ModuleRuntimeRead.Completed(ModuleRuntimeQueryService.EncodePage(page));
        }

        /// <summary>仅适用游戏线程调用；认领后必须恰好报告Complete或Fail。</summary>
        public bool TryClaim(out CaptureWork? work)
        {
            lock (sync)
            {
                EvictExpired();
                work = active;
                if (stopped || work == null || work.Phase != "pending") { work = null; return false; }
                work.Phase = "capturing"; return true;
            }
        }

        public void Complete(CaptureWork work, ModuleRegistryCapture capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            bool abandon;
            lock (sync)
            {
                RequireClaim(work);
                work.Capture = capture; work.NativeEndedAt = milliseconds(); work.Phase = "publishing";
                abandon = stopped || !work.Slots.Any(lease => lease != null && !lease.IsCancelled);
            }
            if (abandon) { Finish(work, null); return; }
            Task publisher;
            try { publisher = publishWorker(() => Publish(work)) ?? throw new InvalidOperationException("Publisher task is required."); }
            catch { Finish(work, "CAPTURE_FAILED"); return; }
            // 完成的是实际publisher Task，不以消费者取消或回执已写出代替work结束。
            publisher.ContinueWith(done =>
            {
                if (done.IsFaulted) _ = done.Exception;
                Finish(work, done.IsFaulted || done.IsCanceled ? "CAPTURE_FAILED" : null);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Fail(CaptureWork work, string reason)
        {
            lock (sync) RequireClaim(work);
            Finish(work, reason);
        }

        public void Stop()
        {
            ModuleRuntimeRead?[] leases;
            CaptureWork? pending = null;
            lock (sync)
            {
                if (stopped) return;
                stopped = true; retained.Clear(); nextRetainedExpiry = long.MaxValue;
                leases = active == null ? Array.Empty<ModuleRuntimeRead?>() :
                    active.Slots.Concat(new[] { active.PublishingLease }).ToArray();
                if (active?.Phase == "pending") { pending = active; active = null; }
            }
            foreach (var lease in leases) lease?.Dispose();
            if (pending != null)
            { Array.Clear(pending.Slots, 0, pending.Slots.Length); pending.Profiles.Clear(); pending.Ended.TrySetResult(true); }
        }

        private void Publish(CaptureWork work)
        {
            // 整个cohort累计最多8槽；不在取消后复用槽，不为每个请求排一个worker。
            for (int index = 0; index < work.Slots.Length; index++)
            {
                ModuleRuntimeRead? lease;
                ModuleRuntimeQueryService.ModulePage? page = null;
                lock (sync)
                {
                    lease = work.Slots[index]; work.Slots[index] = null;
                    if (lease == null) continue;
                    work.PublishingLease = lease;
                    if (!ReferenceEquals(active, work) || stopped || lease.IsCancelled) { }
                    else if (milliseconds() - work.NativeEndedAt >= ModuleRuntimeQueryService.RetentionMilliseconds) { }
                    else
                    {
                        var snapshot = work.Capture!;
                        var saved = new Retained(work.SnapshotId, snapshot,
                            checked(work.NativeEndedAt + ModuleRuntimeQueryService.RetentionMilliseconds));
                        retained[lease.ProfileInstanceId] = saved;
                        nextRetainedExpiry = Math.Min(nextRetainedExpiry, saved.Deadline);
                        page = new ModuleRuntimeQueryService.ModulePage(saved.Id, instanceId, snapshot, 0);
                    }
                }
                if (lease.IsCancelled)
                { lock (sync) work.PublishingLease = null; lease = null; continue; }
                if (page == null) lease.TryComplete(ModuleRuntimeQueryService.Unavailable(stopped ? "STOPPED" : "CAPTURE_FAILED"));
                else
                {
                    JToken? result = ModuleRuntimeQueryService.EncodePage(page);
                    lease.TryComplete(result); result = null;
                }
                // 已交出的TCS/DOM不留在work列表；额外publisher局部DOM仍单独计费。
                lock (sync) work.PublishingLease = null;
                page = null; lease = null;
            }
        }

        private void Finish(CaptureWork work, string? failure)
        {
            ModuleRuntimeRead?[] leases;
            lock (sync)
            {
                if (!ReferenceEquals(active, work)) return;
                active = null; leases = work.Slots.Concat(new[] { work.PublishingLease }).ToArray();
                Array.Clear(work.Slots, 0, work.Slots.Length); work.PublishingLease = null;
                work.Capture = null; work.Profiles.Clear();
            }
            foreach (var lease in leases)
                if (lease != null) lease.TryComplete(ModuleRuntimeQueryService.Unavailable(failure ?? (stopped ? "STOPPED" : "CAPTURE_FAILED")));
            work.Ended.TrySetResult(true);
        }

        private void Cancel(ModuleRuntimeRead lease, CaptureWork? work)
        {
            CaptureWork? pending = null;
            lock (sync)
            {
                if (ReferenceEquals(active, work) && work != null && work.Phase == "pending" &&
                    work.Slots.All(item => item == null || item.IsCancelled))
                { active = null; pending = work; Array.Clear(work.Slots, 0, work.Slots.Length); work.Profiles.Clear(); }
            }
            if (pending != null) pending.Ended.TrySetResult(true);
        }

        private void RequireClaim(CaptureWork work)
        {
            if (work == null || !ReferenceEquals(work.Owner, this) || !ReferenceEquals(active, work) || work.Phase != "capturing")
                throw new InvalidOperationException("The active capture must be reported exactly once.");
        }
        private void EvictExpired()
        {
            long now = milliseconds();
            // 主线程空闲轮询不分配LINQ迭代器/数组，也不反复扫描尚未到期的目录。
            if (now < nextRetainedExpiry) return;
            foreach (string key in retained.Where(pair => pair.Value.Deadline <= now).Select(pair => pair.Key).ToArray()) retained.Remove(key);
            nextRetainedExpiry = retained.Count == 0 ? long.MaxValue : retained.Values.Min(value => value.Deadline);
        }
        private static bool LowerHex(string? value) => value != null && value.Length == 32 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        private static ModuleRuntimeRead Immediate(string reason) => ModuleRuntimeRead.Completed(ModuleRuntimeQueryService.Unavailable(reason));

        public sealed class CaptureWork
        {
            internal CaptureWork(ModuleRuntimeObservationBroker owner) { Owner = owner; }
            internal readonly ModuleRuntimeObservationBroker Owner;
            internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ModuleRuntimeRead?[] Slots = new ModuleRuntimeRead?[MaximumConsumers];
            internal readonly HashSet<string> Profiles = new HashSet<string>(StringComparer.Ordinal);
            internal readonly string SnapshotId = Guid.NewGuid().ToString("N");
            internal int AddedConsumers;
            internal long NativeEndedAt;
            internal string Phase = "pending";
            internal ModuleRegistryCapture? Capture;
            internal ModuleRuntimeRead? PublishingLease;
        }
        private sealed class Retained
        {
            internal Retained(string id, ModuleRegistryCapture capture, long deadline) { Id = id; Capture = capture; Deadline = deadline; }
            internal readonly string Id;
            internal readonly ModuleRegistryCapture Capture;
            internal readonly long Deadline;
        }

        public sealed class ModuleRuntimeRead : IDisposable
        {
            private readonly TaskCompletionSource<JToken> completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            private ModuleRuntimeObservationBroker? owner;
            private CaptureWork? work;
            private int cancelled;
            internal ModuleRuntimeRead(ModuleRuntimeObservationBroker owner, CaptureWork work, string profileInstanceId)
            { this.owner = owner; this.work = work; ProfileInstanceId = profileInstanceId; }
            private ModuleRuntimeRead(JToken value) { ProfileInstanceId = ""; completion.TrySetResult(value); }
            internal static ModuleRuntimeRead Completed(JToken value) => new ModuleRuntimeRead(value);
            internal readonly string ProfileInstanceId;
            internal bool IsCancelled => Volatile.Read(ref cancelled) != 0;
            public Task<JToken> Completion => completion.Task;
            internal void TryComplete(JToken value) => completion.TrySetResult(value);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
                completion.TrySetCanceled();
                var current = Interlocked.Exchange(ref owner, null);
                var captured = Interlocked.Exchange(ref work, null);
                current?.Cancel(this, captured);
            }
        }
    }

    public sealed class ModuleObservationUsage
    {
        internal ModuleObservationUsage(int published, int active, int consumers, string phase)
        { PublishedSnapshotSlots = published; ActiveCaptureCount = active; ConsumerSlots = consumers; Phase = phase; }
        public int PublishedSnapshotSlots { get; }
        public int ActiveCaptureCount { get; }
        public int ConsumerSlots { get; }
        public string Phase { get; }
    }
}
