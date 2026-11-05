using System;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Infrastructure.Operations
{
    public sealed class HistoryPublisherSnapshot
    {
        internal HistoryPublisherSnapshot(bool publishing, bool pending, bool stopped, long sequence, long failures, string? lastFailure)
        { Publishing = publishing; Pending = pending; Stopped = stopped; PublishedThroughSequence = sequence; FailureCount = failures; LastFailureCode = lastFailure; }
        public bool Publishing { get; }
        public bool Pending { get; }
        public bool Stopped { get; }
        public long PublishedThroughSequence { get; }
        public long FailureCount { get; }
        public string? LastFailureCode { get; }
    }

    /// <summary>一个 worker、一个合并位、一个候选。历史出版失败不撤销操作日志 ACK。</summary>
    public sealed class HistoryPublisher : IHistoryProjectionSource
    {
        private readonly object sync = new object();
        private readonly IOperationJournal journal;
        private readonly IHistoryStore store;
        private readonly string environment, version, instance, coreHash;
        private readonly Func<long> utcMilliseconds;
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0, 1);
        private readonly IDisposable observer;
        private readonly Task worker;
        private readonly TaskCompletionSource<bool> initial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private HistoryProjection? current;
        private bool dirty = true, stopping, stopped, publishing;
        private long failures;
        private string? lastFailure;
        private Task? closing;
        public HistoryPublisher(IOperationJournal journal, IHistoryStore store, string environment, string version, string instance, string coreHash,
            Func<long>? utcMilliseconds = null)
        {
            this.journal = journal; this.store = store; this.environment = environment; this.version = version; this.instance = instance; this.coreHash = coreHash;
            this.utcMilliseconds = utcMilliseconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            observer = journal.ObserveConfirmed(Notify);
            worker = Task.Run(RunAsync);
        }
        public HistoryProjection? Current { get { lock (sync) return current; } }
        public Task<bool> InitialAttempt => initial.Task;
        public HistoryPublisherSnapshot Read()
        { lock (sync) return new HistoryPublisherSnapshot(publishing, dirty, stopped, current?.Source.ThroughSequence ?? 0, failures, lastFailure); }
        private void WakeLocked() { if (signal.CurrentCount == 0) signal.Release(); }
        private void Notify()
        { lock (sync) { if (stopping || stopped) return; dirty = true; WakeLocked(); } }
        // Operations 仍是 journal 的关闭 owner；这里等待它真实结束，再作最后一次确认前缀出版。
        public Task StopAsync()
        { lock (sync) return closing ?? (closing = StopCoreAsync()); }
        private async Task StopCoreAsync()
        {
            try { await journal.Completion.ConfigureAwait(false); }
            finally
            {
                observer.Dispose();
                lock (sync) { if (!stopped) { stopping = true; dirty = true; WakeLocked(); } }
                await worker.ConfigureAwait(false);
            }
        }
        private async Task RunAsync()
        {
            try
            {
                try
                {
                    var loaded = store.Load();
                    if (loaded != null && loaded.Source.EnvironmentId != environment) throw new ArgumentException("History environment mismatch.");
                    lock (sync) current = loaded;
                }
                catch { Failed("HISTORY_READ_FAILED"); }
                while (true)
                {
                    bool work, final;
                    lock (sync)
                    {
                        work = dirty; final = stopping;
                        if (work) { dirty = false; publishing = true; }
                        else if (stopping) return;
                    }
                    if (!work) { await signal.WaitAsync().ConfigureAwait(false); continue; }
                    try { PublishLatest(final); }
                    catch { Failed("HISTORY_PUBLISH_FAILED"); }
                    finally { lock (sync) publishing = false; initial.TrySetResult(Current != null); }
                }
            }
            catch { Failed("HISTORY_WORKER_FAILED"); }
            finally
            {
                observer.Dispose();
                lock (sync) { stopped = true; dirty = false; publishing = false; }
                initial.TrySetResult(Current != null); signal.Dispose();
            }
        }
        private void PublishLatest(bool final)
        {
            var prefix = journal.CaptureConfirmedPrefix();
            if (prefix == null) { Failed("HISTORY_SOURCE_UNAVAILABLE"); return; }
            var candidate = prefix.JournalSchemaRevision == 3
                ? HistoryProjection.FromMixedConfirmedPrefix(prefix, environment, version, instance, coreHash, utcMilliseconds())
                : HistoryProjection.FromConfirmedPrefix(prefix, environment, version, instance, coreHash, utcMilliseconds());
            var previous = Current;
            // 同一 store 的无新通知无需重写；关闭时仍重新 capture 并确认最终前缀。
            if (!final && previous != null && previous.Source.ThroughSequence == candidate.Source.ThroughSequence &&
                previous.Source.StoreId == candidate.Source.StoreId && previous.Source.FormatVersion == candidate.Source.FormatVersion &&
                previous.Source.JournalSchemaRevision == candidate.Source.JournalSchemaRevision &&
                previous.Source.Selection == candidate.Source.Selection && previous.Source.ThroughBytes == candidate.Source.ThroughBytes) return;
            store.Publish(candidate); // 慢 IO 完全在旁路 worker，未持 journal/lease/publisher monitor。
            lock (sync) current = candidate;
        }
        private void Failed(string code)
        { lock (sync) { lastFailure = code; if (failures < long.MaxValue) failures++; } }
    }
}
