using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Simulation;

namespace SkylinesLink.Application.Operations
{
    public sealed class OperationRejectedException : Exception
    { public OperationRejectedException(string code) : base(code) { Code = code; } public string Code { get; } }

    /// <summary>单环境唯一操作索引及状态权威。提交只在调用者指定的正常线程；IO 完成从不自动调用原生。</summary>
    public sealed partial class OperationAuthority
    {
        private readonly object sync = new object();
        private readonly IOperationJournal journal;
        private readonly GrantAuthority grants;
        private readonly WorldContextTracker context;
        private readonly INativeHostIdentitySource nativeHost;
        private readonly Func<long> clock;
        private readonly IHistoryProjectionSource? history;
        private readonly Dictionary<string, WriteContext> contexts = new Dictionary<string, WriteContext>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<SubmissionKey, Submission> byKey = new Dictionary<SubmissionKey, Submission>();
        private bool complete, stopping;
        private bool recoveryReady, recoverySealFailed;
        private Entry[] recoveryEntries = Array.Empty<Entry>();
        private long recoveryThroughSequence;
        private long issued;
        private int active;
        private Task? closing;
        private Task? recoverySealing;
        public OperationAuthority(IOperationJournal journal, GrantAuthority grants, WorldContextTracker context,
            INativeHostIdentitySource nativeHost, Func<long>? monotonicMilliseconds = null, IHistoryProjectionSource? history = null)
        {
            this.journal = journal; this.grants = grants; this.context = context; this.nativeHost = nativeHost;
            this.history = history;
            clock = monotonicMilliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
            Restore();
        }
        public bool HasNativeFence { get { lock (sync) return entries.Values.Any(Fenced); } }
        public int ActiveCount { get { lock (sync) return active; } }
        public bool IsRecoveryReady { get { lock (sync) return recoveryReady && !stopping; } }
        public Task SealRecoveryAsync()
        {
            lock (sync) return recoverySealing ?? (recoverySealing = SealRecoveryCoreAsync());
        }
        private async Task SealRecoveryCoreAsync()
        {
            if (stopping || !complete || !journal.Recovery.DurableConfirmed || !journal.IsHealthy) return;
            if (recoveryEntries.Length == 0) { recoveryReady = true; return; }
            try
            {
                using (var reservation = journal.TryReserveRecoveryTerminals(recoveryThroughSequence, recoveryEntries.Length))
                {
                    if (reservation == null) { FailRecoverySealing(); return; }
                    var finals = new OperationFinished[recoveryEntries.Length];
                    for (int i = 0; i < recoveryEntries.Length; i++)
                    {
                        var entry = recoveryEntries[i];
                        finals[i] = new OperationFinished(entry.Intent.OperationId, entry.State, entry.MayHaveApplied,
                            entry.Submission, "RECOVERY_SEALED", entry.Facts);
                        reservation.ValidateEntry(finals[i]);
                    }
                    for (int i = 0; i < recoveryEntries.Length; i++)
                    {
                        Task<bool> work;
                        lock (sync)
                        {
                            // RequestStop 与下一次真正排队共享边界；取消等待不结束已排队 IO。
                            if (stopping) return;
                            var entry = recoveryEntries[i];
                            work = SealEntryAsync(entry, finals[i], reservation);
                            entry.FinalWork = work;
                        }
                        if (!await work.ConfigureAwait(false)) { FailRecoverySealing(); return; }
                    }
                }
                // 专用额度实际退还以后才开放新操作；不占用 live active 四槽。
                lock (sync) if (!stopping) recoveryReady = true;
            }
            catch (Exception error) when (error is JournalPersistenceException ||
                error is ArgumentException || error is SkylinesLink.Contracts.ContractValidationException)
            { FailRecoverySealing(); }
        }
        private async Task<bool> SealEntryAsync(Entry entry, OperationFinished final, IRecoveryTerminalReservation reservation)
        {
            try
            {
                await reservation.AppendAsync(final).ConfigureAwait(false);
                lock (sync) { entry.Finished = true; entry.Durability = "durable"; }
                return true;
            }
            catch (JournalPersistenceException)
            { lock (sync) PersistenceFailed(entry); return false; }
        }
        private void FailRecoverySealing()
        {
            lock (sync)
            {
                recoverySealFailed = true; recoveryReady = false;
                foreach (var entry in recoveryEntries) if (!entry.Finished) PersistenceFailed(entry);
            }
        }
        public HistorySelection ReadHistory(string operationId, GrantSnapshot subject) => ReadHistory(subject, projection => projection.Select(operationId));
        public HistorySelection ReadHistory(SubmissionKey key, GrantSnapshot subject) => ReadHistory(subject, projection => projection.Select(key));
        private HistorySelection ReadHistory(GrantSnapshot subject, Func<HistoryProjection, HistorySelection> select)
        {
            Require(grants.TryRead(subject, () =>
            {
                var published = history?.Current;
                if (published == null) throw new OperationRejectedException("HISTORY_UNAVAILABLE");
                return select(published);
            }, out HistorySelection selected), "UNAUTHORIZED");
            return selected;
        }
        public string IssueWriteContext(GrantSnapshot subject, int lifetimeMilliseconds = 60000)
        {
            lock (sync)
            {
                Require(grants.Allows(subject, GrantScope.Control), "UNAUTHORIZED");
                Require(!stopping && complete && recoveryReady && journal.IsHealthy, "JOURNAL_UNAVAILABLE");
                Require(!retentionGate, retentionFailed ? "JOURNAL_UNAVAILABLE" : "BUSY");
                Require(lifetimeMilliseconds > 0 && lifetimeMilliseconds <= 60000, "INVALID_ARGUMENT");
                var source = context.Read();
                Require(source.State == "menu" || source.State == "ready", "CONTEXT_UNAVAILABLE");
                long now = clock();
                // 这里只回收受理槽。已受理操作、去重键和收尾额度有独立寿命，不能随 context 到期丢弃。
                foreach (var expired in contexts.Values.Where(write => !IsCurrentContext(write, now, source)).ToArray())
                    contexts.Remove(expired.Id);
                Require(contexts.Count < OperationLimits.MaximumContexts && issued < long.MaxValue, "BUSY");
                string id = source.InstanceId + ".w" + checked(++issued).ToString(System.Globalization.CultureInfo.InvariantCulture);
                contexts.Add(id, new WriteContext(id, subject, source, checked(now + lifetimeMilliseconds)));
                return id;
            }
        }
        public Task<OperationLookupSnapshot> SubmitAsync(SubmissionKey key, LoadTarget target, GrantSnapshot subject)
        {
            Entry entry;
            lock (sync)
            {
                Require(grants.Allows(subject, GrantScope.Control), "UNAUTHORIZED");
                if (byKey.TryGetValue(key, out var known))
                {
                    Require(known.Operation != null, "IDEMPOTENCY_CONFLICT"); entry = known.Operation!;
                    Require(entry.Intent.CommandHash == OperationCommandHash.Compute(entry.Intent.Source, target), "IDEMPOTENCY_CONFLICT");
                    return AwaitAcceptedAsync(entry);
                }
                Require(complete && recoveryReady && journal.IsHealthy && !stopping, "JOURNAL_UNAVAILABLE");
                Require(!retentionGate, retentionFailed ? "JOURNAL_UNAVAILABLE" : "BUSY");
                Require(contexts.TryGetValue(key.WriteContextId, out var write), "CONTEXT_UNKNOWN");
                Require(write!.Deadline > clock(), "CONTEXT_EXPIRED");
                Require(SameSubject(write.Subject, subject) && SameContext(write.Source, context.Read()), "STALE_CONTEXT");
                Require(write.Requests < OperationLimits.MaximumRequestsPerContext && entries.Count < OperationLimits.MaximumRetained && active < OperationLimits.MaximumActive, "BUSY");
                var reservation = journal.TryReserveOperation(); Require(reservation != null, "BUSY");
                var intent = new OperationIntent(Guid.NewGuid().ToString("N"), write.Source.InstanceId, key,
                    OperationCommandHash.Compute(write.Source, target), subject.ProfileId, subject.AuthorityEpoch, subject.Epoch, write.Source, target,
                    IsMixedJournal ? subject.ProfileInstanceId : null);
                try { reservation!.ValidateEntry(new OperationAccepted(intent)); }
                catch { reservation!.Dispose(); throw; }
                entry = new Entry(intent, reservation, subject); entries.Add(intent.OperationId, entry); byKey.Add(key, new Submission(entry));
                write.Requests++; active++;
                entry.AcceptedWork = PersistAcceptedAsync(entry);
                return AwaitAcceptedAsync(entry);
            }
        }
        private async Task<OperationLookupSnapshot> AwaitAcceptedAsync(Entry entry)
        {
            await entry.AcceptedWork.ConfigureAwait(false);
            // ticket续体不pin已终结操作；迟到时按当前唯一索引取事实，不把旧Entry冒充当前durable。
            lock (sync) return entries.TryGetValue(entry.Intent.OperationId, out var current) ? Snapshot(current) : Missing(entry.Intent.Key);
        }
        private async Task PersistAcceptedAsync(Entry entry)
        {
            try
            {
                await entry.Reservation!.AppendAsync(new OperationAccepted(entry.Intent), JournalSlot.Normal).ConfigureAwait(false);
                lock (sync) { entry.Accepted = true; entry.State = "queued"; entry.Durability = "durable"; }
            }
            catch (JournalPersistenceException) { lock (sync) { PersistenceFailed(entry); Release(entry); } }
        }
        public Task PersistCommitIntentAsync(string operationId)
        {
            lock (sync)
            {
                var entry = Find(operationId);
                Require(entry.Accepted && entry.Reservation != null && !entry.Finished && !entry.Started && !stopping, "NOT_COMMITTABLE");
                if (entry.CommitWork != null) return entry.CommitWork;
                Require(complete && journal.IsHealthy && !entries.Values.Any(Fenced), "NATIVE_LOAD_FENCED");
                entry.NativeHost = nativeHost.Current;
                entry.CommitWork = PersistIntentAsync(entry);
                return entry.CommitWork;
            }
        }
        private async Task PersistIntentAsync(Entry entry)
        {
            try
            {
                await entry.Reservation!.AppendAsync(new CommitIntended(entry.Intent.OperationId, entry.Intent.Target, entry.NativeHost!), JournalSlot.Normal).ConfigureAwait(false);
                lock (sync) entry.CommitDurable = true;
            }
            catch (JournalPersistenceException) { lock (sync) { entry.CommitFailed = true; PersistenceFailed(entry); } }
        }
        // 必须由正常提交线程调用；auth 检查与原生进入同一短同步边界，没有磁盘等待或客户端取消令牌。
        public bool TryCommitAtSafePoint(string operationId, INativeLoadPort port)
        {
            lock (sync)
            {
                var entry = Find(operationId);
                Require(entry.CommitDurable && !entry.Started && !entry.Finished, "NOT_COMMITTABLE");
                bool permitted = !stopping && complete && journal.IsHealthy && SameContext(entry.Intent.Source, context.Read()) &&
                    !entries.Values.Any(other => !ReferenceEquals(other, entry) && Fenced(other));
                if (permitted)
                    permitted = grants.TryCommit(entry.Subject!, GrantScope.Control, () =>
                    {
                        entry.Started = true; entry.Transition = new LoadTransition(new LoadIntent(entry.Intent.OperationId, entry.Intent.Target, entry.Intent.Source));
                        try { entry.Transition.Start(port); } catch { /* 调用之后失去可靠回执，保 Unknown 与宿主屏障。 */ }
                    });
                if (!permitted)
                {
                    entry.Facts = EmptyFacts(NativeLoadTaskState.NotStarted, LoadObservationEnd.Interrupted);
                    entry.FinalWork = FinishAsync(entry, "cancelled", false, SubmissionKnowledge.NotSubmitted, "COMMIT_GUARD_REJECTED");
                    return false;
                }
                var snapshot = entry.Transition!.Read(context.Read());
                entry.State = snapshot.PhysicalTaskState == NativeLoadTaskState.Running ? "running" : "unknown";
                entry.MayHaveApplied = snapshot.Submission == LoadSubmission.Submitted || snapshot.Submission == LoadSubmission.Unknown;
                MarkVolatile(entry);
                if (snapshot.Submission == LoadSubmission.Unknown)
                    entry.FinalWork = FinishAsync(entry, "unknown", true, SubmissionKnowledge.Unknown, "NATIVE_SUBMISSION_UNKNOWN");
                else if (snapshot.Submission != LoadSubmission.Submitted)
                    entry.FinalWork = FinishAsync(entry, "failed", false, SubmissionKnowledge.NotSubmitted, "NATIVE_NOT_STARTED");
                else entry.NativeWork = ObserveNativeAsync(entry);
                return snapshot.Submission == LoadSubmission.Submitted;
            }
        }
        private async Task ObserveNativeAsync(Entry entry)
        {
            await entry.Transition!.NativeObservationCompletion.ConfigureAwait(false);
            Task<long>? append;
            lock (sync)
            {
                var snapshot = entry.Transition.Read(context.Read());
                entry.NativeEnded = snapshot.PhysicalTaskState == NativeLoadTaskState.RanToCompletion || snapshot.PhysicalTaskState == NativeLoadTaskState.Faulted || snapshot.PhysicalTaskState == NativeLoadTaskState.Cancelled;
                append = entry.NativeEnded ? entry.Reservation!.AppendAsync(new NativeTerminated(entry.Intent.OperationId, snapshot.PhysicalTaskState, snapshot.LoadReturnValue, entry.Transition.BoundTarget), JournalSlot.NativeTerminal) : null;
                MarkVolatile(entry);
            }
            if (append != null) { try { await append.ConfigureAwait(false); } catch (JournalPersistenceException) { lock (sync) PersistenceFailed(entry); } }
            lock (sync)
            {
                entry.NativePersisted = true;
                TryFinishObserved(entry);
            }
        }
        public void ObserveReady(string operationId, LoadReadyObservation observation)
        {
            lock (sync)
            {
                var entry = Find(operationId);
                if (entry.Finished || entry.FinalWork != null || entry.Transition == null) return;
                if (entry.Transition.ObserveReady(observation)) { MarkVolatile(entry); TryFinishObserved(entry); }
            }
        }
        public void EndObservation(string operationId, LoadObservationEnd end)
        {
            lock (sync)
            {
                var entry = Find(operationId);
                if (entry.Finished || entry.FinalWork != null || entry.Transition == null) return;
                entry.Transition.EndObservation(end); MarkVolatile(entry); TryFinishObserved(entry);
            }
        }
        private void TryFinishObserved(Entry entry)
        {
            if (!entry.NativePersisted || entry.FinalWork != null || entry.Finished) return;
            var snapshot = entry.Transition!.Read(context.Read());
            if (snapshot.HistoricalCompletionProven)
                entry.FinalWork = FinishAsync(entry, "succeeded", true, SubmissionKnowledge.Submitted, "TARGET_READY_PROVEN");
            else if (snapshot.PhysicalTaskState == NativeLoadTaskState.RanToCompletion && snapshot.LoadReturnValue == false)
                entry.FinalWork = FinishAsync(entry, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE");
            else if (snapshot.PhysicalTaskState == NativeLoadTaskState.Faulted || snapshot.PhysicalTaskState == NativeLoadTaskState.Cancelled)
                entry.FinalWork = FinishAsync(entry, snapshot.PhysicalTaskState == NativeLoadTaskState.Cancelled ? "cancelled" : "failed", true, SubmissionKnowledge.Submitted, "NATIVE_TERMINATED");
            else if (snapshot.ObservationEnd != LoadObservationEnd.Open)
                entry.FinalWork = FinishAsync(entry, "unknown", true, SubmissionKnowledge.Submitted, "TARGET_READY_UNPROVEN");
        }
        private async Task FinishAsync(Entry entry, string state, bool mayHaveApplied, SubmissionKnowledge submission, string reason)
        {
            entry.State = state; entry.MayHaveApplied = mayHaveApplied; entry.Submission = submission;
            entry.Facts = entry.Transition == null ? entry.Facts : PersistedLoadFacts.From(entry.Transition.Read(context.Read()));
            MarkVolatile(entry);
            try
            {
                await entry.Reservation!.AppendAsync(new OperationFinished(entry.Intent.OperationId, state, mayHaveApplied, submission, reason, entry.Facts), JournalSlot.Terminal).ConfigureAwait(false);
                lock (sync) entry.Durability = "durable";
            }
            catch (JournalPersistenceException) { lock (sync) PersistenceFailed(entry); }
            finally { lock (sync) { entry.Finished = true; Release(entry); } }
        }
        public OperationLookupSnapshot Lookup(string operationId, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.Allows(subject, GrantScope.Read), "UNAUTHORIZED");
                return entries.TryGetValue(operationId, out var entry) ? Snapshot(entry) : Missing(null);
            }
        }
        public OperationLookupSnapshot Lookup(SubmissionKey key, GrantSnapshot subject)
        {
            lock (sync)
            {
                Require(grants.Allows(subject, GrantScope.Read), "UNAUTHORIZED");
                if (!byKey.TryGetValue(key, out var known)) return Missing(key);
                Require(known.Operation != null, "COMMAND_KIND_MISMATCH"); return Snapshot(known.Operation!);
            }
        }
        private OperationLookupSnapshot Missing(SubmissionKey? key)
        {
            if (journal.Recovery.Incompatible) return new OperationLookupSnapshot("unavailable", reason: "JOURNAL_INCOMPATIBLE");
            if (!complete) return new OperationLookupSnapshot("unavailable", reason: "RECOVERY_INCOMPLETE");
            if (recoverySealFailed || !journal.IsHealthy) return new OperationLookupSnapshot("unavailable", reason: "JOURNAL_UNAVAILABLE");
            if (key == null) return retentionApplied ? RetentionNotCovered() : new OperationLookupSnapshot("absent");
            if (retentionApplied)
            {
                // 有效context的整组去重事实始终保留；已退役的旧键不再由同context兄弟项推断expired。
                if (!stopping && contexts.TryGetValue(key.WriteContextId, out var current) && IsCurrentContext(current, clock(), context.Read()))
                    return new OperationLookupSnapshot("absent");
                return RetentionNotCovered();
            }
            if (contexts.TryGetValue(key.WriteContextId, out var write)) return new OperationLookupSnapshot(stopping ||
                !IsCurrentContext(write, clock(), context.Read()) ? "expired" : "absent");
            if (byKey.Keys.Any(known => known.WriteContextId == key.WriteContextId)) return new OperationLookupSnapshot("expired");
            return new OperationLookupSnapshot("unavailable", reason: "CONTEXT_UNKNOWN");
        }
        private OperationLookupSnapshot Snapshot(Entry entry)
        {
            if (!entry.Accepted) return new OperationLookupSnapshot("pending", entry.Intent.OperationId, entry.Intent.OriginInstanceId, durability: entry.Durability == "unavailable" ? "unavailable" : "pending");
            var facts = entry.Transition != null && !entry.Finished ? PersistedLoadFacts.From(entry.Transition.Read(context.Read())) : entry.Facts;
            string state = entry.State;
            if (!entry.Finished && entry.Started && entry.FinalWork == null)
                state = facts.HistoricalCompletionProven ? "succeeded" : facts.LoadReturnValue == false || facts.PhysicalTaskState == NativeLoadTaskState.Faulted ? "failed" :
                    facts.PhysicalTaskState == NativeLoadTaskState.Cancelled ? "cancelled" : facts.PhysicalTaskState == NativeLoadTaskState.RanToCompletion ? "waiting" :
                    facts.PhysicalTaskState == NativeLoadTaskState.Running ? "running" : "unknown";
            return new OperationLookupSnapshot("found", entry.Intent.OperationId, entry.Intent.OriginInstanceId, state, entry.MayHaveApplied,
                retentionRisk?.Contains(entry) == true ? "unavailable" : entry.Durability,
                facts, facts.ObservationContext != null && SameContext(facts.ObservationContext, context.Read()));
        }
        public Task DrainAsync(string operationId)
        {
            lock (sync)
            {
                var entry = Find(operationId);
                return DrainEntryAsync(entry);
            }
        }
        private static async Task DrainEntryAsync(Entry entry)
        { await entry.AcceptedWork.ConfigureAwait(false); if (entry.CommitWork != null) await entry.CommitWork.ConfigureAwait(false); if (entry.NativeWork != null) await entry.NativeWork.ConfigureAwait(false); if (entry.FinalWork != null) await entry.FinalWork.ConfigureAwait(false); }
        public Task CloseAsync()
        {
            lock (sync) return closing ?? (closing = CloseCoreAsync());
        }
        public void RequestStop() { lock (sync) { stopping = true; retentionRotation?.RequestStop(); } }
        private async Task CloseCoreAsync()
        {
            stopping = true; retentionRotation?.RequestStop();
            try
            {
                if (retentionWork != null)
                    try { await retentionWork.ConfigureAwait(false); }
                    catch { /* 原维护Task保留原失败；旧规划失败不能跳过后来健康entry的真实收尾。 */ }
                // 先收束已经开始的恢复封存；关闭本身不发起新的恢复 IO。
                if (recoverySealing != null) await recoverySealing.ConfigureAwait(false);
                await DrainTaskDecisionsAsync().ConfigureAwait(false);
                foreach (var entry in entries.Values)
                {
                    if (entry.Transition != null && entry.FinalWork == null) { entry.Transition.EndObservation(LoadObservationEnd.OwnerStopped); TryFinishObserved(entry); }
                }
                foreach (var entry in entries.Values.ToArray())
                {
                    await entry.AcceptedWork.ConfigureAwait(false);
                    if (entry.CommitWork != null) await entry.CommitWork.ConfigureAwait(false);
                    lock (sync)
                    {
                        if (entry.Accepted && !entry.Started && !entry.Finished && entry.FinalWork == null && entry.Reservation != null)
                            entry.FinalWork = FinishAsync(entry, "cancelled", false, SubmissionKnowledge.NotSubmitted, "OWNER_STOPPED");
                    }
                    await DrainEntryAsync(entry).ConfigureAwait(false);
                }
            }
            finally { await journal.StopAsync().ConfigureAwait(false); }
        }
        private bool Fenced(Entry entry)
        {
            if (entry.RecoveredFence) return true;
            if (!entry.Started) return false;
            var snapshot = entry.Transition?.Read(context.Read());
            return snapshot?.PhysicalTaskState == NativeLoadTaskState.Running || snapshot?.PhysicalTaskState == NativeLoadTaskState.Unknown;
        }
        private void Release(Entry entry)
        { if (entry.Reservation != null) { entry.Reservation.Dispose(); entry.Reservation = null; active--; } }
        private static void PersistenceFailed(Entry entry) { entry.PersistenceFailed = true; entry.Durability = "unavailable"; }
        private static void MarkVolatile(Entry entry) { entry.Durability = entry.PersistenceFailed ? "unavailable" : "volatile"; }
        private Entry Find(string operationId) { Require(entries.TryGetValue(operationId, out var entry), "OPERATION_UNKNOWN"); return entry!; }
        private static void Require(bool condition, string code) { if (!condition) throw new OperationRejectedException(code); }
        private static bool SameSubject(GrantSnapshot a, GrantSnapshot b) => a.ProfileId == b.ProfileId && a.ProfileInstanceId == b.ProfileInstanceId
            && a.AuthorityEpoch == b.AuthorityEpoch && a.Epoch == b.Epoch;
        private bool IsCurrentContext(WriteContext write, long now, WorldContextSnapshot source) =>
            write.Deadline > now && SameContext(write.Source, source) && grants.Allows(write.Subject, GrantScope.Control);
        // City identity may become known after a first save without replacing the loaded scope.
        // Long-lived Task ownership checks its known city separately.
        internal static bool SameContext(WorldContextSnapshot a, WorldContextSnapshot b) => a.InstanceId == b.InstanceId && a.Generation == b.Generation && a.State == b.State;
        private static PersistedLoadFacts EmptyFacts(NativeLoadTaskState state, LoadObservationEnd end = LoadObservationEnd.Open, bool? loadReturnValue = null) =>
            new PersistedLoadFacts(state, loadReturnValue, LoadTargetMatch.Unknown, false, false, end, null);

        private void Restore()
        {
            retentionApplied = journal.Recovery.RetentionApplied;
            var folded = FoldJournal(journal.Recovery.Records);
            complete = journal.Recovery.Complete && folded.AllRecordsValid &&
                (journal.Recovery.JournalSchemaRevision == 2 || IsMixedJournal);
            recoveryThroughSequence = folded.ThroughSequence;
            foreach (var facts in folded.Operations)
            {
                var entry = new Entry(facts.Intent, null, null)
                {
                    Accepted = true, Durability = journal.Recovery.DurableConfirmed ? "durable" : "unavailable",
                    CommitDurable = facts.Commit != null, NativeHost = facts.Commit?.NativeHost,
                    NativeEnded = facts.Native != null, BoundTarget = facts.Native?.BoundTarget
                };
                if (facts.Native != null) entry.Facts = EmptyFacts(facts.Native.State, LoadObservationEnd.OwnerStopped, facts.Native.LoadReturnValue);
                if (facts.Final != null)
                {
                    entry.Finished = true; entry.State = facts.Final.State; entry.MayHaveApplied = facts.Final.MayHaveApplied;
                    entry.Submission = facts.Final.Submission; entry.Facts = facts.Final.Load;
                }
                entries.Add(facts.Intent.OperationId, entry); byKey.Add(facts.Intent.Key, new Submission(entry));
            }
            // 只有本权威的真实恢复完整性可支持 EOF 推断；纯前缀折叠不生成这些恢复事实。
            foreach (var entry in entries.Values)
            {
                if (!entry.Finished)
                {
                    entry.Submission = entry.NativeEnded ? SubmissionKnowledge.Submitted : entry.CommitDurable ? SubmissionKnowledge.Unknown : SubmissionKnowledge.NotSubmitted;
                    entry.State = entry.NativeEnded && (entry.Facts.LoadReturnValue == false || entry.Facts.PhysicalTaskState == NativeLoadTaskState.Faulted) ? "failed" :
                        entry.NativeEnded && entry.Facts.PhysicalTaskState == NativeLoadTaskState.Cancelled ? "cancelled" : complete && !entry.CommitDurable ? "cancelled" : "unknown";
                    entry.MayHaveApplied = !complete || entry.CommitDurable;
                    if (!entry.NativeEnded) entry.Facts = EmptyFacts(entry.MayHaveApplied ? NativeLoadTaskState.Unknown : NativeLoadTaskState.NotStarted, LoadObservationEnd.OwnerStopped);
                    entry.Durability = journal.Recovery.DurableConfirmed ? "volatile" : "unavailable";
                }
                bool unresolved = entry.CommitDurable && !entry.NativeEnded && !(entry.Finished && entry.Submission == SubmissionKnowledge.NotSubmitted);
                entry.RecoveredFence = unresolved && nativeHost.Observe(entry.NativeHost!) != NativeHostState.Ended;
                // 损坏尾可能遮住提交意图；禁止新写，但不捏造 PID 作为独立 fence 权威。
            }
            recoveryEntries = folded.Operations.Where(facts => facts.Final == null).Select(facts => entries[facts.Intent.OperationId]).ToArray();
            recoveryReady = complete && journal.Recovery.DurableConfirmed && journal.IsHealthy && recoveryEntries.Length == 0;
            RestoreTasks(folded);
        }
        private sealed class WriteContext
        {
            internal WriteContext(string id, GrantSnapshot subject, WorldContextSnapshot source, long deadline) { Id = id; Subject = subject; Source = source; Deadline = deadline; }
            internal readonly string Id;
            internal readonly GrantSnapshot Subject;
            internal readonly WorldContextSnapshot Source;
            internal readonly long Deadline;
            internal int Requests;
        }
        private sealed class Entry
        {
            internal Entry(OperationIntent intent, IOperationJournalReservation? reservation, GrantSnapshot? subject) { Intent = intent; Reservation = reservation; Subject = subject; }
            internal readonly OperationIntent Intent;
            internal IOperationJournalReservation? Reservation;
            internal readonly GrantSnapshot? Subject;
            internal bool Accepted, CommitDurable, CommitFailed, Started, NativeEnded, NativePersisted, Finished, RecoveredFence, MayHaveApplied, PersistenceFailed;
            internal string State = "queued", Durability = "pending";
            internal SubmissionKnowledge Submission;
            internal NativeHostIdentity? NativeHost;
            internal LoadTarget? BoundTarget;
            internal LoadTransition? Transition;
            internal PersistedLoadFacts Facts = EmptyFacts(NativeLoadTaskState.NotStarted);
            internal Task AcceptedWork = Task.CompletedTask;
            internal Task? CommitWork, NativeWork, FinalWork;
        }
    }
}
