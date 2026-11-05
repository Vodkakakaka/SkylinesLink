using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class HistoryPublisherTests
    {
        private static int checks;
        private static readonly LoadTarget Target = new LoadTarget("probe", "save", "v1");
        private static TaskCompletionSource<bool> Completion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static void Reject(Action action, string code)
        {
            try { action(); } catch (OperationRejectedException error) { Check(error.Code == code, "history rejects " + code); return; }
            throw new InvalidOperationException("Expected history rejection.");
        }
        private static void Invalid(Action action, string label)
        {
            try { action(); } catch (ArgumentException) { Check(true, label); return; }
            catch (InvalidDataException) { Check(true, label); return; }
            throw new InvalidOperationException("Expected invalid history data.");
        }
        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await SlowPublicationAndClose();
            await CurrentAuthorityAndFailures();
            await UnidentifiedReadyHistory();
            await ValidPrefixWithoutEofInference();
            await ReaderLeaseAndStartup();
            return checks;
        }
        private static async Task UnidentifiedReadyHistory()
        {
            Program.Stage = "unknown ready identity survives actual history publication and reload";
            using (var fixture = new Fixture())
            {
                var owner = await fixture.Create();
                try
                {
                    var subject = owner.Authority.Read("probe")!;
                    owner.Context.ObserveReady(owner.Context.EnterLoading());
                    string id = (await owner.Operations.SubmitAsync(new SubmissionKey(owner.Operations.IssueWriteContext(subject), "unknown-identity"), Target, subject)).OperationId!;
                    await owner.Operations.PersistCommitIntentAsync(id);
                    long generation = 0;
                    owner.Operations.TryCommitAtSafePoint(id, new NativeLoadGate(owner.Context.Read().InstanceId, _ =>
                    { generation = owner.Context.EnterLoading(); return NativeLoadInvocation.Started(Task.FromResult(true), Target); }));
                    owner.Context.ObserveReady(generation);
                    owner.Operations.ObserveReady(id, new LoadReadyObservation(owner.Context.Read(), Target, id));
                    await owner.Operations.DrainAsync(id);
                    await WaitUntil(() => owner.History.Current?.Select(id).Record != null);
                    var record = fixture.Store!.Atomic.Load()!.Select(id).Record!;
                    Check(record.SourceContext.State == "ready" && record.SourceContext.CityId == null &&
                        record.Load.ObservationContext!.State == "ready" && record.Load.ObservationContext.CityId == null &&
                        record.State == "succeeded" && record.Load.HistoricalCompletionProven,
                        "real checkpoint IO and semantic reload preserve ready scopes and causal completion without inventing persistent identity");
                }
                finally { await owner.CloseAsync(); }
            }
        }

        private static async Task SlowPublicationAndClose()
        {
            Program.Stage = "history slow publisher and final close";
            using (var fixture = new Fixture())
            {
                var owner = await fixture.Create(pauseFirst: true);
                await Program.MustComplete(fixture.Store!.Entered.Task);
                var subject = owner.Authority.Read("probe")!;
                Reject(() => owner.Operations.ReadHistory("unknown", subject), "HISTORY_UNAVAILABLE");
                string context = owner.Operations.IssueWriteContext(subject);
                for (int i = 0; i < OperationLimits.MaximumActive; i++)
                {
                    var ack = owner.Operations.SubmitAsync(new SubmissionKey(context, "slow-" + i), Target, subject);
                    await Program.MustComplete(ack);
                    Check((await ack).Durability == "durable", "journal ACK proceeds while checkpoint IO waits");
                }
                Task closing = owner.CloseAsync();
                await Program.MustComplete(owner.Operations.CloseAsync());
                await WaitUntil(() => owner.History.Read().Pending);
                Check(!closing.IsCompleted && !owner.FullyReleased && fixture.TryLease() == null,
                    "close retains environment lease while actual publisher still owns IO");
                fixture.Store.Permit.Set(); await Program.MustComplete(closing);
                var projection = owner.History.Current!;
                Check(projection.Source.ThroughSequence == 1 + 2 * OperationLimits.MaximumActive &&
                    projection.Records.Count == OperationLimits.MaximumActive && projection.Records.All(item => item.State == "cancelled" && !item.MayHaveApplied),
                    "final capture includes true journal shutdown terminal records without notification timing dependency");
                Check(fixture.Store.PublishCount <= 3 && owner.History.Read().Stopped && !owner.History.Read().Pending,
                    "single publisher coalesces finite pending notifications and actually stops");
                using (var lease = fixture.TryLease()) Check(lease != null && owner.FullyReleased, "publisher join precedes lease release");
                Check(fixture.Store.Atomic.FrameUsage.Count == 0 && fixture.Store.Atomic.FrameUsage.Capacity == 0 &&
                    Directory.GetFiles(fixture.Connection, "history-*.tmp").Length == 0, "history owned frame capacity and temporary file settle after close");
            }
        }
        private static async Task CurrentAuthorityAndFailures()
        {
            Program.Stage = "history actual consumer and publication failures";
            using (var fixture = new Fixture())
            {
                var owner = await fixture.Create();
                Check(await owner.History.InitialAttempt, "initial empty confirmed checkpoint is actually published");
                var subject = owner.Authority.Read("probe")!;
                JToken live = OperationQueryService.ExecuteV3(owner.Operations, Query("missing"), subject, owner.Context.Read().InstanceId, 1);
                Check((string?)live["result"]?["kind"] == "live" && (string?)live["result"]?["lookup"]?["kind"] == "absent", "omitted source keeps live query semantics");
                JToken missing = OperationQueryService.ExecuteV3(owner.Operations, Query("missing", true), subject, owner.Context.Read().InstanceId, 1);
                Check((string?)missing["result"]?["lookup"]?["reason"] == "HISTORY_NOT_COVERED", "valid empty history reports uncovered rather than absent");

                var key = new SubmissionKey(owner.Operations.IssueWriteContext(subject), "negative");
                string id = (await owner.Operations.SubmitAsync(key, Target, subject)).OperationId!;
                await owner.Operations.PersistCommitIntentAsync(id);
                Check(owner.Operations.TryCommitAtSafePoint(id, new NativeLoadGate(owner.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(Task.FromResult(false), null))),
                    "fixture enters actual native Task with known false return");
                await owner.Operations.DrainAsync(id);
                await WaitUntil(() => owner.History.Current?.Select(id).Record != null);
                HistorySelection selected = owner.Operations.ReadHistory(id, subject);
                var byKey = owner.Operations.ReadHistory(key, subject);
                Check(selected.Record!.State == "failed" && selected.Record.MayHaveApplied && selected.Record.Load.LoadReturnValue == false &&
                    ReferenceEquals(selected.Record, byKey.Record), "published immutable history preserves actual false and identical key/ID selection");
                var source = selected.Source; long now = source.PublishedAtUnixMs;
                var queryId = OperationQueryService.ExecuteV3(owner.Operations, Query(id, true), subject, owner.Context.Read().InstanceId, now);
                var queryKey = OperationQueryService.ExecuteV3(owner.Operations, Query(key), subject, owner.Context.Read().InstanceId, now);
                Check(JToken.DeepEquals(queryId["result"], queryKey["result"]) && (bool?)queryId["result"]?["lookup"]?["record"]?["load"]?["isObservationCurrent"] == false,
                    "real revision 4 consumer returns same historical record and never current observation");
                var clockBack = OperationQueryService.ExecuteV3(owner.Operations, Query(id, true), subject, owner.Context.Read().InstanceId, now - 1);
                Check(clockBack["result"]?["ageMs"]?.Type == JTokenType.Null, "backwards wall clock yields unknown history age");
                var copy = selected.Record.ToContract(); copy.State = "unknown"; copy.SourceContext.Generation = "9";
                Check(selected.Record.State == "failed" && selected.Record.SourceContext.Generation == 0, "mutable generated DTO never aliases published values");
                CheckFrames(owner.History.Current!);

                fixture.Store!.Fail = true;
                long oldSequence = owner.History.Current!.Source.ThroughSequence, oldFailures = owner.History.Read().FailureCount;
                var next = new SubmissionKey(owner.Operations.IssueWriteContext(subject), "publish-fails");
                string nextId = (await owner.Operations.SubmitAsync(next, Target, subject)).OperationId!;
                await WaitUntil(() => owner.History.Read().FailureCount > oldFailures);
                Check(owner.Operations.Lookup(nextId, subject).Durability == "durable" && owner.History.Current!.Source.ThroughSequence == oldSequence &&
                    owner.History.Read().LastFailureCode == "HISTORY_PUBLISH_FAILED", "failed checkpoint keeps prior projection without revoking durable journal acknowledgement");
                await owner.Authority.UpdateAsync("reader", GrantScope.Read, HandshakeProof.RandomHex(), null);
                var reader = owner.Authority.Read("reader")!;
                Check(owner.Operations.ReadHistory(id, reader).Record!.OperationId == id, "same environment current Read may query another submitting profile history");
                await owner.Authority.UpdateAsync("probe", GrantScope.None, HandshakeProof.RandomHex(), null);
                Reject(() => OperationQueryService.ExecuteV3(owner.Operations, Query(id, true), subject, owner.Context.Read().InstanceId, now), "UNAUTHORIZED");
                Check(owner.Operations.ReadHistory(id, reader).Record!.OperationId == id, "unrelated reader remains authorized after original profile revocation");
                fixture.Store.Fail = false;
                await owner.CloseAsync();
                Check(owner.History.Current!.Select(nextId).Record?.State == "cancelled" && owner.History.Current.Source.ThroughSequence > oldSequence && owner.History.Read().FailureCount > 0,
                    "final shutdown retries publication from actual confirmed prefix and retains failure evidence");
                var boundary = new CreateFailureBoundary(new PrivateConnectionStorage(fixture.Connection));
                using (var lease = new EnvironmentLeaseProvider(new PrivateConnectionStorage(fixture.Connection)).TryAcquireAuthority()!)
                {
                    var history = new AtomicHistoryStore(boundary, lease, "probe");
                    ExpectCreateFailure(() => history.Publish(owner.History.Current!), boundary);
                    Check(history.FrameUsage.Count == 0 && history.FrameUsage.Capacity == 0, "failed creation releases only its owned frame budget");
                    ExpectCreateFailure(() => new AtomicAuthorizationStore(boundary, lease, "probe").InitializeEmpty(), boundary);
                }
            }
        }
        private static void ExpectCreateFailure(Action action, CreateFailureBoundary boundary)
        {
            bool failed = false;
            try { action(); } catch (IOException) { failed = true; }
            Check(failed && boundary.CollisionPath != null && File.ReadAllBytes(boundary.CollisionPath).SequenceEqual(new byte[] { 0x5a }),
                "CreatePrivateFile failure never deletes a path whose ownership was not returned");
        }
        private static void CheckFrames(HistoryProjection projection)
        {
            byte[] frame = new byte[HistoryFrameCodec.MaximumFileBytes]; int length = HistoryFrameCodec.Encode(projection, frame);
            Check(length <= HistoryFrameCodec.MaximumFileBytes && HistoryFrameCodec.Decode(frame, length).Records[0].Load.LoadReturnValue == false,
                "SLHC exact bounded frame roundtrips known native return");
            foreach (int index in new[] { 0, 4, 6, 8, 15, length - 1 })
            {
                frame[index] ^= 1; Invalid(() => HistoryFrameCodec.Decode(frame, length), "SLHC detects header, length or digest corruption"); frame[index] ^= 1;
            }
            Invalid(() => HistoryFrameCodec.Decode(frame, length - 1), "SLHC rejects truncated trailer");
            Invalid(() => HistoryFrameCodec.Decode(frame, length + 1), "SLHC rejects bytes after complete frame");
            var bad = projection.ToContract(); bad.Records[0].SourceContext.Generation = "00";
            Invalid(() => HistoryProjection.FromContract(bad), "history generation obeys canonical journal decimal semantics");
            bad = projection.ToContract(); bad.Records[0].Load.IsObservationCurrent = true;
            Invalid(() => HistoryProjection.FromContract(bad), "historical projection rejects current observation claim");
            bad = projection.ToContract(); bad.Records.Add(bad.Records[0]);
            Invalid(() => HistoryProjection.FromContract(bad), "history rejects duplicate identity and sequence");
        }
        private static async Task ValidPrefixWithoutEofInference()
        {
            Program.Stage = "history confirmed valid prefix without authoritative EOF";
            using (var fixture = new Fixture())
            {
                var boundary = new PrivateConnectionStorage(fixture.Connection);
                using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!)
                {
                    var journal = new OperationJournal(boundary, lease, "probe", true);
                    var context = new WorldContextSnapshot("source-instance", 0, "menu", null);
                    var intent = new OperationIntent("complete", context.InstanceId, new SubmissionKey("context", "one"), OperationCommandHash.Compute(context, Target), "probe", 1, 1, context, Target);
                    using (var reservation = journal.TryReserveOperation()!)
                    {
                        await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                        await reservation.AppendAsync(new OperationFinished(intent.OperationId, "cancelled", false, SubmissionKnowledge.NotSubmitted, "CANCELLED",
                            new PersistedLoadFacts(NativeLoadTaskState.NotStarted, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)), JournalSlot.Terminal);
                    }
                    using (var reservation = journal.TryReserveOperation()!)
                    {
                        var pending = new OperationIntent("pending", context.InstanceId, new SubmissionKey("context", "two"), OperationCommandHash.Compute(context, Target), "probe", 1, 1, context, Target);
                        await reservation.AppendAsync(new OperationAccepted(pending), JournalSlot.Normal);
                        // 合法帧和实际 Flush，跨记录引用不存在操作。它不撤销之前已证的 final。
                        await reservation.AppendAsync(new CommitIntended("not-accepted", Target, new NativeHostIdentity(1, 1)), JournalSlot.Normal);
                    }
                    var store = new ControlledStore(new AtomicHistoryStore(boundary, lease, "probe"), false);
                    var publisher = new HistoryPublisher(journal, store, "probe", "0.1.0-dev.0", "publisher", new string('c', 64));
                    Check(await publisher.InitialAttempt && publisher.Current!.Source.ThroughSequence == 4 && journal.CaptureConfirmedPrefix()!.ThroughSequence == 5,
                        "semantic-invalid tail publishes exact valid watermark without a prior checkpoint");
                    Check(publisher.Current!.Records.Count == 1 && publisher.Current.Select("complete").Record != null && publisher.Current.Select("pending").Record == null,
                        "accepted-only valid prefix never invents a terminal from recovered EOF policy");
                    await journal.StopAsync(); await publisher.StopAsync();
                    Check(publisher.Current!.Source.ThroughSequence == 4 && store.Atomic.FrameUsage.Count == 0, "final publication does not attach invalid higher watermark");
                }
            }
        }
        private static async Task ReaderLeaseAndStartup()
        {
            Program.Stage = "history reader lease and finite startup retry";
            using (var fixture = new Fixture())
            {
                var original = await fixture.Create(); await original.CloseAsync();
                long epoch = original.Authority.State.AuthorityEpoch;
                var held = fixture.TryLease()!;
                var entered = Completion();
                var lifecycle = new GameConnectionLifecycle(token => { entered.TrySetResult(true); return fixture.CreateNullable(token, 1000); });
                Task<bool> start = lifecycle.StartAsync(); await Program.MustComplete(entered.Task);
                held.Dispose(); Check(await start && lifecycle.Owner!.Authority.State.AuthorityEpoch == epoch + 1,
                    "reader first then game owner retries only lease and activates once");
                await lifecycle.CloseAsync(); epoch = lifecycle.Owner!.Authority.State.AuthorityEpoch;
                using (var reader = fixture.TryLease()!)
                {
                    var busy = new GameConnectionLifecycle(token => fixture.CreateNullable(token, 80));
                    Check(!await busy.StartAsync() && busy.Read().FailureCode == "ENVIRONMENT_BUSY", "continuous reader occupancy has one bounded initialization deadline");
                    reader.Dispose();
                    Check(await busy.StartAsync() && busy.Owner!.Authority.State.AuthorityEpoch == epoch + 1, "explicit retry after busy reuses complete existing store without reinitializing epochs");
                    await busy.CloseAsync();
                }
                using (var reader = fixture.TryLease()!)
                {
                    entered = Completion();
                    var cancelled = new GameConnectionLifecycle(token => { entered.TrySetResult(true); return fixture.CreateNullable(token, 5000); });
                    var pending = cancelled.StartAsync(); await Program.MustComplete(entered.Task);
                    cancelled.RequestStop(); await Program.MustComplete(cancelled.CloseAsync(), 1000);
                    Check(!await pending && await cancelled.CloseAsync() && cancelled.Owner == null, "stop cancels lease retry without waiting full deadline or publishing later");
                }
            }
        }
        private static JObject Query(string id, bool history = false)
        { var query = new JObject { ["selector"] = new JObject { ["kind"] = "operationId", ["operationId"] = id } }; if (history) query["source"] = "history"; return query; }
        private static JObject Query(SubmissionKey key) => new JObject { ["source"] = "history", ["selector"] = new JObject { ["kind"] = "submissionKey",
            ["submissionKey"] = new JObject { ["writeContextId"] = key.WriteContextId, ["requestId"] = key.RequestId } } };
        private static async Task WaitUntil(Func<bool> ready)
        { for (int i = 0; i < 250 && !ready(); i++) await Task.Delay(10); if (!ready()) throw new TimeoutException("History fixture deadline."); }
        private sealed class ControlledStore : IHistoryStore
        {
            internal readonly AtomicHistoryStore Atomic;
            internal readonly TaskCompletionSource<bool> Entered = Completion();
            internal readonly ManualResetEventSlim Permit;
            internal volatile bool Fail;
            internal int PublishCount;
            internal ControlledStore(IHistoryStore inner, bool pause) { Atomic = (AtomicHistoryStore)inner; Permit = new ManualResetEventSlim(!pause); }
            public HistoryProjection? Load() => Atomic.Load();
            public void Publish(HistoryProjection projection)
            {
                Interlocked.Increment(ref PublishCount); Entered.TrySetResult(true);
                if (!Permit.Wait(5000)) throw new TimeoutException("History fixture release missing.");
                if (Fail) throw new IOException("Synthetic publication failure.");
                Atomic.Publish(projection);
            }
        }
        private sealed class CreateFailureBoundary : IPrivateStorageBoundary
        {
            private readonly IPrivateStorageBoundary inner;
            internal string? CollisionPath;
            internal CreateFailureBoundary(IPrivateStorageBoundary inner) { this.inner = inner; }
            public string DirectoryPath => inner.DirectoryPath;
            public void FlushToDisk(FileStream file) => inner.FlushToDisk(file);
            public void ValidateDirectory() => inner.ValidateDirectory();
            public void ValidateFile(string path, bool missing) => inner.ValidateFile(path, missing);
            public FileStream CreatePrivateFile(string path)
            {
                // 精确端口反例：调用者未获得文件句柄，不能因随机文件名就认定拥有它。
                using (var file = inner.CreatePrivateFile(path)) file.WriteByte(0x5a);
                CollisionPath = path;
                throw new IOException("Synthetic pre-existing target at create boundary.");
            }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "history-runtime-" + Guid.NewGuid().ToString("N"));
            internal string Connection => Path.Combine(root, "connection");
            internal ControlledStore? Store;
            private ConnectionAuthorityOwner? owner;
            private readonly string token = HandshakeProof.RandomHex();
            internal Fixture() { Directory.CreateDirectory(root); }
            internal async Task<ConnectionAuthorityOwner> Create(bool pauseFirst = false)
            { owner = await CreateNullable(CancellationToken.None, 1000, inner => Store = new ControlledStore(inner, pauseFirst)); return owner ?? throw new InvalidOperationException("Fixture owner unavailable."); }
            internal async Task<ConnectionAuthorityOwner?> CreateNullable(CancellationToken cancellation, int milliseconds, Func<IHistoryStore, IHistoryStore>? storePort = null)
            {
                var created = await ConnectionAuthorityOwner.CreateAsync(Connection, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                    new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations), true,
                    historyStorePort: storePort,
                    cancellationToken: cancellation, leaseWaitMilliseconds: milliseconds);
                if (created == null) return null;
                try
                {
                    var authorization = created.Authority.State;
                    if (authorization.RecordRevision == 2 && authorization.AuthorityEpoch == 1 && authorization.Profiles.Count == 0)
                        await created.Authority.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, token, null);
                    return created;
                }
                catch (Exception error) { await Program.CloseFailedRootAsync(created, error); throw; }
            }
            internal IDisposable? TryLease() => new EnvironmentLeaseProvider(new PrivateConnectionStorage(Connection)).TryAcquireAuthority();
            public void Dispose()
            {
                Store?.Permit.Set();
                if (owner != null) owner.CloseAsync().GetAwaiter().GetResult();
                if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture containment failed.");
                if (Directory.Exists(Connection))
                {
                    foreach (string file in Directory.GetFiles(Connection))
                    { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links."); File.Delete(file); }
                    Directory.Delete(Connection);
                }
                Directory.Delete(root);
            }
        }
    }
}
