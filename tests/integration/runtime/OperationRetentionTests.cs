using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class OperationRetentionTests
    {
        private static int checks;
        private static readonly LoadTarget Target = new LoadTarget("probe", "save", "v1");
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static void Reject(Action action, string code)
        {
            try { action(); }
            catch (OperationRejectedException error) { Check(error.Code == code, "retention preserves rejection " + code); return; }
            throw new InvalidOperationException("Missing retention rejection: " + code);
        }
        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await CurrentContextAndRequestCounters();
            await FullCapacityTwoRoundsAndReopen();
            await RecoveredSiblingFence();
            await LiveWorkRemainsBusy();
            await PreparingRiskAndStop();
            await UnknownFlushKeepsRisk();
            await OldPlanningFaultDoesNotSkipLaterNativeDrain();
            await LateTicketUsesCurrentIndex();
            await HistoryPublishesNewStoreAfterOldInflight();
            return checks;
        }

        private static async Task LateTicketUsesCurrentIndex()
        {
            Program.Stage = "late accepted ticket rechecks the current retained index";
            using (var f = new Fixture())
            {
                var key = new SubmissionKey(f.Ops.IssueWriteContext(f.Subject, 1), "late-ticket");
                string id = await f.Complete(key);
                // 仅捕获真实ticket持有的Entry并复用其最终投影入口；不调OS续体队列或增产品hook。
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var index = (System.Collections.IDictionary)typeof(OperationAuthority).GetField("entries", flags)!.GetValue(f.Ops)!;
                object captured = index[id]!;
                var render = typeof(OperationAuthority).GetMethod("AwaitAcceptedAsync", flags)!;
                var before = await (Task<OperationLookupSnapshot>)render.Invoke(f.Ops, new[] { captured })!;
                Check(before.Kind == "found" && before.Durability == "durable", "the captured accepted ticket initially refers to a durable indexed result");
                f.Now = 2;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed,
                    "late-ticket boundary fixture actually retires its complete durable chain");
                var late = await (Task<OperationLookupSnapshot>)render.Invoke(f.Ops, new[] { captured })!;
                Check(late.Kind == "unavailable" && late.Reason == "RETENTION_NOT_COVERED",
                    "a ticket projection using a captured retired entry cannot claim a current durable result");
            }
        }

        private static async Task CurrentContextAndRequestCounters()
        {
            Program.Stage = "retention preserves live context identity, deduplication and cumulative requests";
            using (var f = new Fixture())
            {
                string old = f.Ops.IssueWriteContext(f.Subject, 1);
                await f.Complete(new SubmissionKey(old, "old"));
                f.Now = 2;
                string live = f.Ops.IssueWriteContext(f.Subject);
                var keys = Enumerable.Range(0, 7).Select(i => new SubmissionKey(live, "live-" + i)).ToArray();
                var ids = new string[keys.Length];
                for (int i = 0; i < keys.Length; i++) ids[i] = await f.Complete(keys[i]);
                OperationAuthority original = f.Ops;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed && ReferenceEquals(original, f.Ops),
                    "one authority retires expired-context facts without reconstructing its issuer or live contexts");
                var prefix = f.Journal.CaptureConfirmedPrefix()!;
                Check(JournalFactsFold.Fold(prefix.Records).Operations.Count == 7 &&
                    keys.All(key => f.Ops.Lookup(key, f.Subject).Durability == "durable"),
                    "all seven accepted keys of an unexhausted valid context remain durably indexed");
                Check((await f.Ops.SubmitAsync(keys[0], Target, f.Subject)).OperationId == ids[0],
                    "same accepted key still returns its original operation after maintenance");
                Reject(() => f.Ops.SubmitAsync(keys[0], new LoadTarget("probe", "different", "v1"), f.Subject), "IDEMPOTENCY_CONFLICT");
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.NoEligible &&
                    f.Ops.Lookup(new SubmissionKey(live, "not-submitted"), f.Subject).Kind == "absent",
                    "unexhausted live-context protection keeps its keys and local missing-key coverage");
                var eighth = new SubmissionKey(live, "eighth"); string eighthId = await f.Complete(eighth);
                Check(f.Ops.Lookup(eighth, f.Subject).OperationId == eighthId,
                    "the same preserved context accepts exactly its one remaining request after maintenance");
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(live, "ninth"), Target, f.Subject), "BUSY");
                Check(f.Ops.IssueWriteContext(f.Subject).EndsWith(".w3", StringComparison.Ordinal),
                    "retirement never resets the same authority issuer to an earlier context identifier");
            }
            using (var f = new Fixture(banked: false))
            {
                string context = f.Ops.IssueWriteContext(f.Subject, 1);
                await f.Complete(new SubmissionKey(context, "legacy")); f.Now = 2;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Unsupported,
                    "legacy-only journal reports unsupported maintenance rather than pretending to reclaim it");
            }
        }

        private static async Task FullCapacityTwoRoundsAndReopen()
        {
            Program.Stage = "retention reclaims full capacity without reissuing old contexts or false absence";
            using (var f = new Fixture())
            {
                SubmissionKey? firstKey = null; string? firstId = null;
                string context = "";
                for (int i = 0; i < 32; i++)
                {
                    if (i % 8 == 0) context = f.Ops.IssueWriteContext(f.Subject, 1);
                    var key = new SubmissionKey(context, "full-" + i); string id = await f.Complete(key);
                    if (i == 0) { firstKey = key; firstId = id; }
                }
                Check(f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 129,
                    "four eight-request contexts fill the original bounded journal exactly");
                string firstStore = f.Position().StoreId;
                Check(f.Now == 0 && await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 1 && f.Position().StoreId != firstStore,
                    "quiescent exhausted context quotas close irreversibly and release full capacity before their deadlines");
                Missing(f, firstId!, firstKey!);
                Reject(() => f.Ops.SubmitAsync(firstKey!, Target, f.Subject), "CONTEXT_UNKNOWN");
                string next = f.Ops.IssueWriteContext(f.Subject, 1);
                Check(next.EndsWith(".w5", StringComparison.Ordinal), "next context after retiring 32 operations advances the original issuer");
                var secondKey = new SubmissionKey(next, "second-round"); string secondId = await f.Complete(secondKey);
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.NoEligible && f.Ops.Lookup(secondKey, f.Subject).OperationId == secondId,
                    "a later unexhausted context still pins its accepted key while its deadline remains valid");
                f.Now = 2;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed && f.Position().RetentionApplied,
                    "deadline expiration independently permits the second retirement and preserves inherited coverage");
                Missing(f, secondId, secondKey);
                await f.Reopen();
                Missing(f, firstId!, firstKey!); Missing(f, secondId, secondKey);
                Check(f.Journal.Recovery.RetentionApplied && f.Ops.IsRecoveryReady,
                    "new authority restores durable retention coverage from the same bank without replay");
            }
        }

        private static async Task RecoveredSiblingFence()
        {
            Program.Stage = "retention keeps same-context native fences and reports partial historical coverage honestly";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var retiredKey = new SubmissionKey("historical-shared", "retirable");
                var fenceKey = new SubmissionKey("historical-shared", "fenced");
                await f.SeedRecovered(retiredKey, "retirable", commitOnly: false);
                await f.SeedRecovered(fenceKey, "fenced", commitOnly: true);
                f.Host.State = NativeHostState.Alive;
                await f.Reopen(flush);
                Check(f.Ops.HasNativeFence && f.Ops.Lookup("fenced", f.Subject).State == "unknown" &&
                    f.Ops.Lookup("fenced", f.Subject).MayHaveApplied && f.Ops.Lookup("fenced", f.Subject).Durability == "durable",
                    "actual recovery sealing keeps unresolved native commit facts and their original-host fence");
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed && f.Ops.HasNativeFence,
                    "one expired sibling retires while the unresolved sibling retains its complete chain");
                Missing(f, "retirable", retiredKey);
                Check((await f.Ops.SubmitAsync(fenceKey, Target, f.Subject)).OperationId == "fenced",
                    "retained sibling key still deduplicates even when its context is no longer acceptable");
                f.Host.State = NativeHostState.Unknown;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.NoEligible && f.Ops.HasNativeFence,
                    "unknown original-host state is not proof of physical termination");
                f.Host.State = NativeHostState.Ended;
                flush.Arm(1);
                Task<OperationRetentionOutcome> retirement = f.Ops.TryRetireAsync();
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    var observed = f.Ops.Lookup("fenced", f.Subject);
                    Check(!f.Ops.HasNativeFence && observed.State == "unknown" && observed.MayHaveApplied &&
                        observed.Load!.PhysicalTaskState == NativeLoadTaskState.Unknown && observed.Load.LoadReturnValue == null,
                        "ended-host proof removes only the physical fence, never rewrites unknown execution facts");
                }
                finally { flush.Release.Set(); await Program.MustComplete(retirement); }
                Check(await retirement == OperationRetentionOutcome.Installed && f.Host.ObservedOnlyOriginal,
                    "retirement observes the original persisted native host identity and can reclaim after its end");
            }
        }

        private static async Task LiveWorkRemainsBusy()
        {
            Program.Stage = "retention does not mistake a running native task for a reclaimable final";
            using (var f = new Fixture())
            {
                var key = new SubmissionKey(f.Ops.IssueWriteContext(f.Subject, 1), "running");
                string id = (await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Check(f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId,
                    _ => NativeLoadInvocation.Started(native.Task, Target))), "fixture starts one actual owned native Task");
                try
                {
                    f.Now = 2;
                    Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Busy && f.Ops.ActiveCount == 1,
                        "expired context does not retire a still-running native task or its IO obligations");
                }
                finally { native.TrySetResult(false); await f.Ops.DrainAsync(id); }
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed,
                    "the same operation becomes eligible only after real native and final work settle");
            }
        }

        private static async Task PreparingRiskAndStop()
        {
            Program.Stage = "retirement risk is one precise live projection and Stop joins the real maintenance";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var retired = new SubmissionKey(f.Ops.IssueWriteContext(f.Subject, 1), "retire"); string retiredId = await f.Complete(retired);
                f.Now = 2;
                string live = f.Ops.IssueWriteContext(f.Subject);
                var kept = new SubmissionKey(live, "keep"); string keptId = await f.Complete(kept);
                flush.Arm(1);
                Task<OperationRetentionOutcome> work = f.Ops.TryRetireAsync(); Task? close = null;
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    Check(ReferenceEquals(work, f.Ops.TryRetireAsync()), "repeated retirement calls join the single owned maintenance Task");
                    var id = f.Ops.Lookup(retiredId, f.Subject); var key = f.Ops.Lookup(retired, f.Subject);
                    var duplicate = await f.Ops.SubmitAsync(retired, Target, f.Subject);
                    Check(new[] { id, key, duplicate }.All(value => value.Kind == "found" && value.OperationId == retiredId &&
                        value.State == "failed" && value.MayHaveApplied && value.Durability == "unavailable" && value.Load!.LoadReturnValue == false),
                        "ID, key and duplicate acceptance share exact retiring risk without inventing new execution facts");
                    Check(f.Ops.Lookup(keptId, f.Subject).Durability == "durable", "nonretiring known operation keeps its original durable projection");
                    Reject(() => f.Ops.IssueWriteContext(f.Subject), "BUSY");
                    Reject(() => f.Ops.SubmitAsync(new SubmissionKey(live, "new-key"), Target, f.Subject), "BUSY");
                    Reject(() => f.Ops.SubmitAsync(retired, new LoadTarget("probe", "different", "v1"), f.Subject), "IDEMPOTENCY_CONFLICT");
                    close = f.Ops.CloseAsync();
                    Check(!close.IsCompleted && !work.IsCompleted && !f.Journal.Completion.IsCompleted,
                        "authority Close waits for the actual paused preparing IO and journal worker");
                }
                finally { flush.Release.Set(); await Program.MustComplete(work); if (close != null) await Program.MustComplete(close); }
                Check(await work == OperationRetentionOutcome.CurrentPreserved && f.Ops.Lookup(retiredId, f.Subject).Durability == "durable",
                    "proved pre-seal Stop restores only this risk projection and preserves the current store");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
            }
        }

        private static async Task UnknownFlushKeepsRisk()
        {
            Program.Stage = "unknown maintenance persistence keeps its precise risk and closed admission";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var retired = new SubmissionKey(f.Ops.IssueWriteContext(f.Subject, 1), "retire"); string retiredId = await f.Complete(retired);
                f.Now = 2;
                var kept = new SubmissionKey(f.Ops.IssueWriteContext(f.Subject), "keep"); string keptId = await f.Complete(kept);
                flush.Arm(1, failAfterFlush: true); flush.Release.Set();
                Task<OperationRetentionOutcome> work = f.Ops.TryRetireAsync();
                bool failed = false;
                try { await Program.MustComplete(work); } catch (JournalPersistenceException) { failed = true; }
                Check(failed && f.Ops.Lookup(retiredId, f.Subject).Durability == "unavailable" &&
                    f.Ops.Lookup(retired, f.Subject).Durability == "unavailable" &&
                    (await f.Ops.SubmitAsync(retired, Target, f.Subject)).Durability == "unavailable",
                    "Flush acknowledgement loss leaves retiring snapshots unavailable through every existing ticket path");
                Check(f.Ops.Lookup(keptId, f.Subject).Durability == "durable" && !f.Journal.IsHealthy,
                    "unknown maintenance does not corrupt a different known operation's fact or claim a healthy writer");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(kept.WriteContextId, "new-key"), Target, f.Subject), "JOURNAL_UNAVAILABLE");
                await f.Reopen();
                Check(f.Ops.Lookup(retiredId, f.Subject).Durability == "durable" && f.Ops.Lookup(keptId, f.Subject).Durability == "durable" &&
                    !f.Journal.Recovery.RetentionApplied, "new owner recovers original facts only after proving the unsealed source and actual Flush");
            }
        }

        private static async Task OldPlanningFaultDoesNotSkipLaterNativeDrain()
        {
            Program.Stage = "old retirement planning fault cannot bypass a later real native task on Close";
            using (var f = new Fixture())
            {
                string expired = f.Ops.IssueWriteContext(f.Subject, 1);
                await f.Complete(new SubmissionKey(expired, "completed-before-fault"));
                f.Now = 2;
                string live = f.Ops.IssueWriteContext(f.Subject);
                var source = f.Position();
                var failure = new InvalidOperationException("Synthetic one-time retention clock failure.");
                f.ClockFailure = failure;
                Task<OperationRetentionOutcome> planning = f.Ops.TryRetireAsync();
                bool originalFailure = false;
                try { await Program.MustComplete(planning); }
                catch (InvalidOperationException error) { originalFailure = ReferenceEquals(error, failure); }
                Check(originalFailure && f.Journal.IsHealthy && f.Position().StoreId == source.StoreId &&
                    f.Position().ThroughSequence == source.ThroughSequence && !f.Ops.HasNativeFence,
                    "one pre-IO clock failure preserves its original fault and leaves the healthy authority unfenced");

                var key = new SubmissionKey(live, "accepted-after-fault");
                string id = (await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Check(f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId,
                    _ => NativeLoadInvocation.Started(native.Task, Target))),
                    "planning gate truly reopened for a later normally accepted native task without bypassing any fence");
                Task close = f.Ops.CloseAsync();
                try
                {
                    Check(!close.IsCompleted && !f.Journal.Completion.IsCompleted && f.Ops.ActiveCount == 1 && !native.Task.IsCompleted,
                        "Close keeps the later native task and writer alive despite its cached old planning failure");
                }
                finally { native.TrySetResult(false); await Program.MustComplete(close); }
                var final = f.Ops.Lookup(id, f.Subject);
                Check(final.State == "failed" && final.MayHaveApplied && final.Load!.LoadReturnValue == false &&
                    final.Durability == "durable" && f.Ops.ActiveCount == 0 && f.Journal.Completion.IsCompleted &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == source.ThroughSequence + 4,
                    "Close waits through actual native false completion and durable final before ending the same writer");
                Check(planning.IsFaulted && ReferenceEquals(planning.Exception!.GetBaseException(), failure),
                    "draining later healthy work never converts the original maintenance failure into success");
            }
        }

        private static async Task HistoryPublishesNewStoreAfterOldInflight()
        {
            Program.Stage = "one real history publisher follows a smaller new store after its old in-flight publication";
            using (var f = new Fixture())
            {
                string context = f.Ops.IssueWriteContext(f.Subject, 1);
                for (int i = 0; i < 3; i++) await f.Complete(new SubmissionKey(context, "history-" + i));
                string oldStore = f.Position().StoreId;
                f.StartHistory(paused: true);
                try
                {
                    await Program.MustComplete(f.HistoryStore!.Entered.Task);
                    f.Now = 2;
                    Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed &&
                        f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 1 && f.History!.Current == null,
                        "journal retirement completes while actual old checkpoint publication is still pending");
                    string nextStore = f.Position().StoreId;
                    f.HistoryStore.Release.Set();
                    await Until(() => f.History!.Current?.Source.StoreId == nextStore);
                    var projection = f.History!.Current!; var disk = f.HistoryStore.Atomic.Load()!;
                    string[] published = f.HistoryStore.PublishedStores;
                    Check(projection.Source.StoreId == nextStore && projection.Source.ThroughSequence == 1 && projection.Records.Count == 0 &&
                        disk.Source.StoreId == nextStore && disk.Source.ThroughSequence == 1,
                        "smaller sequence is actually published and loaded from AtomicHistoryStore under its new store identity");
                    int firstNew = Array.IndexOf(published, nextStore);
                    Check(published.Length >= 2 && published[0] == oldStore && firstNew > 0 && published.Skip(firstNew).All(store => store == nextStore),
                        "the single publisher completes old source before new and never writes an old checkpoint over a confirmed new one");
                }
                finally { f.HistoryStore!.Release.Set(); }
            }
        }

        private static void Missing(Fixture f, string id, SubmissionKey key)
        {
            Check(f.Ops.Lookup(id, f.Subject).Kind == "unavailable" && f.Ops.Lookup(id, f.Subject).Reason == "RETENTION_NOT_COVERED" &&
                f.Ops.Lookup(key, f.Subject).Kind == "unavailable" && f.Ops.Lookup(key, f.Subject).Reason == "RETENTION_NOT_COVERED",
                "retired ID and key report uncovered history, never false absent or sibling-derived expired");
        }
        private static async Task Until(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready()) { if (watch.ElapsedMilliseconds > 3000) throw new TimeoutException("Retention fixture observation deadline."); await Task.Delay(1); }
        }

        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            private int calls, pauseAt; private bool failAfterFlush;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal void Arm(int relativeCall, bool failAfterFlush = false)
            { this.failAfterFlush = failAfterFlush; Volatile.Write(ref pauseAt, Volatile.Read(ref calls) + relativeCall); }
            public void Flush(FileStream file)
            {
                bool selected = Interlocked.Increment(ref calls) == Volatile.Read(ref pauseAt);
                if (selected) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Retention fixture Flush deadline."); }
                file.Flush(true);
                if (selected && failAfterFlush) throw new IOException("Synthetic retention acknowledgement loss after actual Flush.");
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
        private sealed class HostProof : INativeHostIdentitySource
        {
            public NativeHostIdentity Current { get; } = new NativeHostIdentity(101, 123456);
            internal NativeHostState State = NativeHostState.Ended;
            internal bool ObservedOnlyOriginal = true;
            public NativeHostState Observe(NativeHostIdentity identity)
            { ObservedOnlyOriginal &= Current.Equals(identity); if (!Current.Equals(identity)) throw new InvalidOperationException("Wrong original native host."); return State; }
        }
        private sealed class ControlledHistory : IHistoryStore
        {
            private readonly object sync = new object();
            private readonly List<string> published = new List<string>();
            internal readonly AtomicHistoryStore Atomic;
            internal readonly ManualResetEventSlim Release;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ControlledHistory(AtomicHistoryStore atomic, bool paused) { Atomic = atomic; Release = new ManualResetEventSlim(!paused); }
            internal string[] PublishedStores { get { lock (sync) return published.ToArray(); } }
            public HistoryProjection? Load() => Atomic.Load();
            public void Publish(HistoryProjection projection)
            {
                Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Retention history fixture deadline.");
                Atomic.Publish(projection); lock (sync) published.Add(projection.Source.StoreId);
            }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "operation-retention-" + Guid.NewGuid().ToString("N"));
            private readonly PrivateConnectionStorage boundary;
            private readonly EnvironmentLease lease;
            private readonly bool banked;
            internal long Now;
            internal InvalidOperationException? ClockFailure;
            internal readonly HostProof Host = new HostProof();
            internal OperationJournal Journal { get; private set; }
            internal OperationAuthority Ops { get; private set; }
            internal WorldContextTracker Context { get; private set; }
            internal GrantAuthority Grants { get; }
            internal GrantSnapshot Subject => Grants.Read("probe")!;
            internal HistoryPublisher? History { get; private set; }
            internal ControlledHistory? HistoryStore { get; private set; }
            internal Fixture(IJournalDurability? flush = null, bool banked = true)
            {
                this.banked = banked;
                Directory.CreateDirectory(root); boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                var authorization = new AtomicAuthorizationStore(boundary, lease, "probe"); authorization.InitializeEmpty();
                var activated = authorization.Load().Activate(); authorization.Save(1, activated); Grants = new GrantAuthority(activated, authorization);
                Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('a', 64), null).GetAwaiter().GetResult();
                Journal = new OperationJournal(boundary, lease, "probe", true, flush, banked);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                Ops = new OperationAuthority(Journal, Grants, Context, Host, ReadClock);
            }
            internal JournalRotationPosition Position() => Journal.CaptureRotationPosition() ?? throw new InvalidOperationException("Retention position unavailable.");
            internal async Task<string> Complete(SubmissionKey key)
            {
                string id = (await Ops.SubmitAsync(key, Target, Subject)).OperationId!;
                await Ops.PersistCommitIntentAsync(id);
                Program.Assert(Ops.TryCommitAtSafePoint(id, new NativeLoadGate(Context.Read().InstanceId,
                    _ => NativeLoadInvocation.Started(Task.FromResult(false), Target))), "Retention fixture commit not permitted.");
                await Ops.DrainAsync(id); return id;
            }
            internal async Task SeedRecovered(SubmissionKey key, string id, bool commitOnly)
            {
                var source = new WorldContextSnapshot("original-instance", 3, "menu", null);
                var intent = new OperationIntent(id, source.InstanceId, key, OperationCommandHash.Compute(source, Target), "probe", Subject.AuthorityEpoch, Subject.Epoch, source, Target);
                using (var reservation = Journal.TryReserveOperation()!)
                {
                    await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                    if (commitOnly) await reservation.AppendAsync(new CommitIntended(id, Target, Host.Current), JournalSlot.Normal);
                    else await reservation.AppendAsync(new OperationFinished(id, "cancelled", false, SubmissionKnowledge.NotSubmitted, "CANCELLED",
                        new PersistedLoadFacts(NativeLoadTaskState.NotStarted, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)), JournalSlot.Terminal);
                }
            }
            internal async Task Reopen(IJournalDurability? flush = null)
            {
                if (History != null) throw new InvalidOperationException("Fixture does not reopen an active publisher.");
                await Program.MustComplete(Ops.CloseAsync());
                Journal = new OperationJournal(boundary, lease, "probe", false, flush, banked);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                Ops = new OperationAuthority(Journal, Grants, Context, Host, ReadClock);
                await Program.MustComplete(Ops.SealRecoveryAsync());
            }
            private long ReadClock()
            {
                var failure = ClockFailure;
                if (failure != null) { ClockFailure = null; throw failure; }
                return Now;
            }
            internal void StartHistory(bool paused)
            {
                if (History != null) throw new InvalidOperationException("Duplicate retention history publisher.");
                HistoryStore = new ControlledHistory(new AtomicHistoryStore(boundary, lease, "probe"), paused);
                History = new HistoryPublisher(Journal, HistoryStore, "probe", "0.1.0-dev.0", Context.Read().InstanceId, new string('c', 64));
            }
            public void Dispose()
            {
                HistoryStore?.Release.Set(); Ops.CloseAsync().GetAwaiter().GetResult();
                History?.StopAsync().GetAwaiter().GetResult(); HistoryStore?.Release.Dispose();
                Grants.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(root).StartsWith("operation-retention-", StringComparison.Ordinal)) throw new IOException("Retention fixture containment failed.");
                foreach (string directory in new[] { root, boundary.DirectoryPath })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retention fixture refuses directory links.");
                foreach (string file in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retention fixture refuses file links."); File.Delete(file); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
