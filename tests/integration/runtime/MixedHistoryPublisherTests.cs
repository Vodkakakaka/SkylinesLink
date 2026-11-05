using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using Contracts = SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>Actual mixed writer/checkpoint IO; not game, Native or retirement-eligibility proof.</summary>
    internal static class MixedHistoryPublisherTests
    {
        private const string EnvironmentId = "mixed-history-probe";
        private static readonly TaskActor Actor = new TaskActor("planner", new string('a', 32), 1, 1);
        private static readonly WorldContextSnapshot Source = new WorldContextSnapshot("mixed-history-origin", 7, "ready", "city-a");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static int checks;
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static async Task<T> Done<T>(Task<T> work) { await Program.MustComplete(work); return await work; }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException || error is InvalidDataException || error is NotSupportedException ||
                error is InvalidOperationException || error is FormatException || error is Contracts.ContractValidationException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected mixed history rejection: " + label);
        }
        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await CompleteFactsAndSemanticRejections();
            await RawUnicodeRecoveryAndLargeCheckpoint();
            await TwoRotationsAndPublicationFailure();
            await LostJournalAcknowledgementAndClose();
            return checks;
        }

        private static TaskStatedGoal Goal(string title = "Original 😀", string request = "Exact user request") =>
            new TaskStatedGoal(title, request, new[] { "Normal rules", "Keep useful facts" }, new[] { "Explicit acceptance" });
        private static TaskDecision Decision(string key, WorldContextSnapshot source, string hash) =>
            new TaskDecision(new SubmissionKey("mixed-history-context", key), Actor, source, hash);
        private static TaskCreated Create(string id, TaskStatedGoal goal) =>
            new TaskCreated(id, Decision(id + "-create", Source, TaskCommandHash.Create(Source, goal)), goal);
        private static TaskGoalRevised Revise(string id, long previous, TaskStatedGoal goal) =>
            new TaskGoalRevised(id, previous, previous + 1, 1, Decision(id + "-revise-" + previous, Source,
                TaskCommandHash.Revise(id, previous, 1, Source, goal)), goal);
        private static TaskStopped Stop(string id) => new TaskStopped(id, 1, 2,
            Decision(id + "-stop", new WorldContextSnapshot("different-menu-audit", 19, "menu", null), TaskCommandHash.Stop(id, 1)));
        private static HistoryProjection Project(ConfirmedJournalPrefix prefix) => HistoryProjection.FromMixedConfirmedPrefix(prefix,
            EnvironmentId, "0.1.0-dev.0", "different-publisher", new string('c', 64), 123);
        private static JObject Json(HistoryProjection projection) =>
            (JObject)Contracts.ContractCodec.EncodeOperationHistoryCheckpointV2(projection.ToContractV2());
        private static HistoryProjection Read(JObject value) => HistoryProjection.FromContract(
            Contracts.ContractCodec.DecodeOperationHistoryCheckpointV2(value));

        private static async Task CompleteFactsAndSemanticRejections()
        {
            Program.Stage = "mixed history complete Task chains, sparse original positions and semantic rejection";
            using (var f = new Fixture())
            {
                await f.Create("task", Goal());
                await f.CompleteFalse("negative");
                await f.Revise("task", 1, Goal("Revised 😀", "Second exact request"));
                await f.Stop("task");
                var prefix = f.Prefix; var projection = Project(prefix); var task = projection.Tasks.Single();
                Check(projection.Source.FormatVersion == 2 && projection.Source.JournalSchemaRevision == 3 &&
                    projection.Source.ThroughSequence == 8 && projection.Source.ThroughBytes == prefix.ThroughBytes &&
                    projection.TaskRecords.Select(row => row.Sequence).SequenceEqual(new long[] { 2, 7, 8 }) &&
                    projection.Records.Single().TerminalSequence == 6,
                    "CP2 preserves one actual source and sparse global Task/operation positions without resequencing");
                Check(task.TaskId == "task" && task.Owner.ProfileInstanceId == Actor.ProfileInstanceId && task.CityId == Source.CityId &&
                    task.IsStopped && task.ControlVersion == 2 && task.GoalRevision == 2 && task.GoalHistory.Count == 2 &&
                    task.GoalHistory[0].Goal.Title == "Original 😀" && task.CurrentGoal.RequestText == "Second exact request" &&
                    projection.Records[0].Load.LoadReturnValue == false && projection.Records[0].State == "failed" &&
                    projection.Records[0].MayHaveApplied && projection.Source.ProducerInstanceId != Source.InstanceId,
                    "full stated goals, durable Stop/menu audit, owner and actual false survive the mixed projection");
                var bytes = new byte[HistoryFrameCodec.RequiredCapacity(projection)]; int length = HistoryFrameCodec.Encode(projection, bytes);
                var decoded = HistoryFrameCodec.Decode(bytes, length);
                Check(bytes[4] == 2 && decoded.TaskRecords.Count == 3 && decoded.Tasks.Single().GoalHistory.Count == 2 &&
                    decoded.Records.Single().Load.LoadReturnValue == false && decoded.Source.ThroughBytes == prefix.ThroughBytes,
                    "actual SLHC2 frame roundtrip preserves both halves of the complete mixed checkpoint");
                Reject(() => HistoryProjection.FromConfirmedPrefix(prefix, EnvironmentId, "0.1.0", "producer", new string('c', 64), 1),
                    "unchanged CP1 factory refuses a real family3 source");
                Reject(() => projection.ToContract(), "mixed projection cannot silently become the old CP1 DTO");
                Reject(() => Project(new ConfirmedJournalPrefix(prefix.Records, prefix.Records.Count, 3)),
                    "unknown actual body bytes cannot be replaced by re-encoding or capacity");
                var original = Json(projection);
                int capacity = HistoryFrameCodec.RequiredCapacity(projection);
                var atBudget = PaddedCheckpoint(original, capacity);
                Check(HistoryFrameCodec.Decode(atBudget, atBudget.Length).Tasks.Single().IsStopped,
                    "raw noncanonical trailing whitespace at the actual-source byte budget remains valid");
                var pastBudget = PaddedCheckpoint(original, capacity + 1);
                Reject(() => HistoryFrameCodec.Decode(pastBudget, pastBudget.Length),
                    "one extra raw input byte exceeds the source budget even though re-encoding would shrink the same document");
                foreach (string mutation in new[] { "late-hash", "owner", "missing-create", "cross-key", "cross-sequence", "environment", "watermark" })
                {
                    var bad = (JObject)original.DeepClone(); var rows = (JArray)bad["taskRecords"]!;
                    if (mutation == "late-hash") rows[2]["entry"]!["decision"]!["commandHash"] = new string('0', 64);
                    if (mutation == "owner") rows[1]["entry"]!["decision"]!["actor"]!["profileInstanceId"] = new string('b', 32);
                    if (mutation == "missing-create") rows.RemoveAt(0);
                    if (mutation == "cross-key") rows[0]["entry"]!["decision"]!["submissionKey"] = bad["records"]![0]!["submissionKey"]!.DeepClone();
                    if (mutation == "cross-sequence") rows[2]["sequence"] = bad["records"]![0]!["terminalSequence"]!.DeepClone();
                    if (mutation == "environment") rows[2]["environmentId"] = "another-environment";
                    if (mutation == "watermark") rows[2]["sequence"] = 9;
                    Reject(() => Read(bad), "whole CP2 rejects " + mutation + " even when the requested first goal is otherwise intact");
                }
                var mutable = projection.ToContractV2(); mutable.Source.StoreId = "changed";
                ((Contracts.TaskMetadataEntryTaskCreated)mutable.TaskRecords[0].Entry).Goal.Title = "changed";
                Check(projection.Source.StoreId != "changed" && projection.Tasks.Single().GoalHistory[0].Goal.Title == "Original 😀",
                    "caller mutation of generated CP2 values does not alias the published ordinary facts");
                using (var operation = f.Journal.TryReserveOperation()!)
                {
                    var intent = f.Intent("bad-tail");
                    await Done(operation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal));
                    await Done(operation.AppendAsync(new CommitIntended("never-accepted", intent.Target, new NativeHostIdentity(101, 123456)), JournalSlot.Normal));
                }
                Check(!JournalFactsFold.FoldMixed(f.Prefix.Records).AllRecordsValid, "late-semantic fixture contains a real flushed but invalid reference");
                Reject(() => Project(f.Prefix), "CP2 refuses a partial Fold instead of mixing its lower sequence with complete-prefix bytes");
            }
        }

        private static async Task RawUnicodeRecoveryAndLargeCheckpoint()
        {
            Program.Stage = "mixed history real noncanonical UTF8 recovery and CP2-only large syntax profile";
            using (var f = new Fixture())
            {
                var goal = new TaskStatedGoal(new string('\u0085', 128), new string('\u0085', 2048),
                    Enumerable.Repeat(new string('\u0085', 256), 16).ToArray(), Enumerable.Repeat(new string('\u0085', 256), 16).ToArray());
                long rawBytes = await f.SeedRawGoals(goal, 17);
                await f.Reopen();
                Check(f.Journal.Recovery.Complete && f.Journal.Recovery.DurableConfirmed && f.Journal.IsHealthy &&
                    f.Prefix.ThroughBytes == rawBytes && rawBytes < 1048576 && f.Prefix.ThroughSequence == 18,
                    "actual recovery Flush confirms 17 raw U+0085 goal frames and their original physical body length");
                var projection = Project(f.Prefix); var bytes = new byte[HistoryFrameCodec.RequiredCapacity(projection)];
                int length = HistoryFrameCodec.Encode(projection, bytes);
                Check(length > 1048576 && length <= 3 * rawBytes + 262144 + 4096,
                    "valid recovered short UTF8 becomes a genuinely over-1MiB CP2 within the proved actual-B budget");
                var decoded = HistoryFrameCodec.Decode(bytes, length);
                Check(decoded.Tasks.Single().GoalHistory.Count == 17 && decoded.Tasks.Single().CurrentGoal.RequestText == goal.RequestText &&
                    decoded.Source.ThroughBytes == rawBytes && decoded.TaskRecords.Last().Sequence == 18,
                    "large CP2 special parser roundtrips the entire recovered chain rather than only a selected goal");
                string json = Utf8.GetString(bytes, 12, length - 44);
                bool oldBudget = false;
                try { Contracts.ContractJson.Parse(json); }
                catch (Contracts.ContractValidationException error) { oldBudget = error.Issues.Any(issue => issue.Code == "BUDGET"); }
                Check(oldBudget, "ordinary public JSON parser retains its exact original one-MiB BUDGET rejection");
                f.StartPublisher(); await Program.MustComplete(f.History!.InitialAttempt);
                int publishedLength = HistoryFrameCodec.Encode(f.History!.Current!, bytes);
                Check(f.History!.Current!.Tasks.Single().GoalRevision == 17 && f.Store!.Atomic.Load()!.Source.ThroughBytes == rawBytes &&
                    new FileInfo(f.HistoryPath).Length == publishedLength,
                    "same actual publisher/AtomicHistoryStore installs and reloads the full large CP2 using real encoded bytes");
            }
        }

        private static async Task TwoRotationsAndPublicationFailure()
        {
            Program.Stage = "one mixed history publisher follows two smaller stores and preserves prior publication on failure";
            using (var f = new Fixture())
            {
                await f.Create("retained", Goal()); await f.CompleteFalse("first-retired"); await f.Revise("retained", 1, Goal("Second"));
                string first = f.Position.StoreId; f.StartPublisher(paused: true);
                await Program.MustComplete(f.Store!.Entered.Task);
                var second = await f.Rotate(Array.Empty<string>(), new[] { "retained" });
                Check(second.Outcome == JournalRotationOutcome.Installed && second.Prefix.ThroughSequence < 7 && f.History!.Current == null,
                    "actual rotation may finish while its old immutable checkpoint candidate is still waiting on file IO");
                f.Store!.Release.Set(); await Until(() => f.History!.Current?.Source.StoreId == f.Position.StoreId);
                string middle = f.Position.StoreId;
                Check(f.History!.Current!.Tasks.Single().GoalHistory.Count == 2 && f.History!.Current!.Records.Count == 0 &&
                    f.Store!.Atomic.Load()!.Source.StoreId == middle && f.History!.Current!.Source.ThroughBytes == second.Prefix.ThroughBytes,
                    "smaller new-store checkpoint keeps the complete selected Task chain and drops only the plan's terminal operation");
                await f.CompleteFalse("second-retired"); await f.Stop("retained"); await f.Create("remaining", Goal("Remaining"));
                var third = await f.Rotate(Array.Empty<string>(), new[] { "retained", "remaining" });
                await Until(() => f.History!.Current?.Source.StoreId == f.Position.StoreId);
                string latest = f.Position.StoreId; string[] stores = f.Store!.PublishedStores;
                int atMiddle = Array.IndexOf(stores, middle), atLatest = Array.IndexOf(stores, latest);
                Check(first != middle && middle != latest && atMiddle > 0 && atLatest > atMiddle &&
                    stores.Skip(atMiddle).All(value => value != first) && stores.Skip(atLatest).All(value => value == latest) &&
                    f.History!.Current!.Tasks.Single(value => value.TaskId == "retained").IsStopped && third.Prefix.ThroughSequence < 9,
                    "single worker never overwrites a newer store with an older in-flight candidate across two real rotations");
                long priorSequence = f.History!.Current!.Source.ThroughSequence, priorFailures = f.History!.Read().FailureCount;
                byte[] priorFile = File.ReadAllBytes(f.HistoryPath); f.Store!.Fail = true;
                JournalTaskReceipt ack = await f.Revise("remaining", 1, Goal("New durable goal"));
                await Until(() => f.History!.Read().FailureCount > priorFailures);
                Check(ack.Sequence > priorSequence && f.History!.Current!.Source.ThroughSequence == priorSequence &&
                    priorFile.SequenceEqual(File.ReadAllBytes(f.HistoryPath)) && f.History!.Read().LastFailureCode == "HISTORY_PUBLISH_FAILED",
                    "failed pre-replacement publication preserves old disk/Current without revoking the actual metadata journal ACK");
                f.Store!.Fail = false; await f.Stop("remaining");
                await Until(() => f.History!.Current?.Source.ThroughSequence == f.Prefix.ThroughSequence);
                await f.Reopen(); f.StartPublisher(); await Program.MustComplete(f.History!.InitialAttempt);
                Check(f.Journal.Recovery.DurableConfirmed && f.History!.Current!.Source.StoreId == latest &&
                    f.History!.Current!.Tasks.Single(value => value.TaskId == "remaining").CurrentGoal.Title == "New durable goal" &&
                    f.History!.Current!.Tasks.All(value => value.IsStopped) && f.Store!.Atomic.Load()!.TaskRecords.Count == 6,
                    "normal close/join and new-owner actual recovery preserve both Task histories and the true latest source");
            }
            using (var f = new Fixture())
            {
                f.StartPublisher(paused: true); await Program.MustComplete(f.Store!.Entered.Task);
                await f.Create("while-slow", Goal());
                await Program.MustComplete(f.Journal.StopAsync()); Task closing = f.History!.StopAsync();
                Check(!closing.IsCompleted, "publisher Close joins its real outstanding Atomic publication rather than just caller cancellation");
                f.Store!.Release.Set(); await Program.MustComplete(closing);
                Check(f.History!.Read().Stopped && f.History!.Current!.Tasks.Single().TaskId == "while-slow" && f.Store!.Atomic.FrameUsage.Count == 0,
                    "slow final Close captures the actual confirmed Task after the old candidate and releases owned frame capacity");
            }
        }

        private static async Task LostJournalAcknowledgementAndClose()
        {
            Program.Stage = "mixed history never publishes an unconfirmed real Flush and Close waits for that IO";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.Create("unknown", Goal()); f.StartPublisher(); await Program.MustComplete(f.History!.InitialAttempt);
                long sequence = f.Prefix.ThroughSequence; long? bytes = f.Prefix.ThroughBytes;
                flush.Arm(); Task<JournalTaskReceipt> pending = f.Revise("unknown", 1, Goal("May be on disk"));
                await Program.MustComplete(flush.Entered.Task);
                Task journalClose = f.Journal.StopAsync(), historyClose = f.History!.StopAsync();
                Check(!journalClose.IsCompleted && !historyClose.IsCompleted && f.Prefix.ThroughBytes == bytes,
                    "unconfirmed metadata and Close still own the actual paused physical Flush");
                flush.Release.Set(); bool lost = false;
                try { await Done(pending); } catch (JournalPersistenceException) { lost = true; }
                await Program.MustComplete(journalClose); await Program.MustComplete(historyClose);
                Check(lost && flush.Flushed && f.History!.Current!.Source.ThroughSequence == sequence &&
                    f.History!.Current!.Source.ThroughBytes == bytes && f.Store!.Atomic.Load()!.Tasks.Single().GoalRevision == 1,
                    "lost ACK after actual Flush leaves the old confirmed CP2 rather than inventing durable goal revision two");
                await f.Reopen(); f.StartPublisher(); await Program.MustComplete(f.History!.InitialAttempt);
                Check(f.History!.Current!.Tasks.Single().GoalRevision == 2 && f.History!.Current!.Tasks.Single().CurrentGoal.Title == "May be on disk" &&
                    f.History!.Current!.Source.ThroughBytes > bytes,
                    "only a new owner's actual recovery confirmation can publish the already-present revision");
            }
        }

        private static async Task Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Mixed history condition deadline."); await Task.Delay(5); }
        }
        private static byte[] PaddedCheckpoint(JObject document, int length)
        {
            byte[] payload = Utf8.GetBytes(document.ToString(Formatting.None));
            if (length < payload.Length + 44) throw new ArgumentException("Checkpoint padding would truncate its real payload.");
            var frame = new byte[length];
            frame[0] = (byte)'S'; frame[1] = (byte)'L'; frame[2] = (byte)'H'; frame[3] = (byte)'C'; frame[4] = 2;
            for (int i = 0; i < 4; i++) frame[8 + i] = (byte)((uint)(length - 44) >> (8 * i));
            Buffer.BlockCopy(payload, 0, frame, 12, payload.Length);
            for (int i = 12 + payload.Length; i < length - 32; i++) frame[i] = 32;
            using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(frame, 0, length - 32), 0, frame, length - 32, 32);
            return frame;
        }
        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            private int armed;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal bool Flushed;
            internal void Arm() => Volatile.Write(ref armed, 1);
            public void Flush(FileStream file)
            {
                bool selected = Interlocked.Exchange(ref armed, 0) == 1;
                if (selected) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Mixed history Flush deadline."); }
                file.Flush(true);
                if (selected) { Flushed = true; throw new IOException("Synthetic lost acknowledgement after real mixed Flush."); }
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
        private sealed class ControlledStore : IHistoryStore, IDisposable
        {
            private readonly object sync = new object();
            private readonly List<string> stores = new List<string>();
            private int pause;
            internal readonly AtomicHistoryStore Atomic;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release;
            internal volatile bool Fail;
            internal ControlledStore(AtomicHistoryStore atomic, bool paused) { Atomic = atomic; pause = paused ? 1 : 0; Release = new ManualResetEventSlim(!paused); }
            internal string[] PublishedStores { get { lock (sync) return stores.ToArray(); } }
            public HistoryProjection? Load() => Atomic.Load();
            public void Publish(HistoryProjection projection)
            {
                if (Interlocked.Exchange(ref pause, 0) == 1) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Mixed history publication deadline."); }
                if (Fail) throw new IOException("Synthetic failure before checkpoint replacement.");
                Atomic.Publish(projection); lock (sync) stores.Add(projection.Source.StoreId);
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "mixed-history-" + Guid.NewGuid().ToString("N"));
            private readonly PrivateConnectionStorage boundary;
            private readonly EnvironmentLease lease;
            internal OperationJournal Journal { get; private set; }
            internal HistoryPublisher? History { get; private set; }
            internal ControlledStore? Store { get; private set; }
            internal string HistoryPath => Path.Combine(boundary.DirectoryPath, "operations.history");
            internal ConfirmedJournalPrefix Prefix => Journal.CaptureConfirmedPrefix() ?? throw new InvalidOperationException("Confirmed mixed prefix unavailable.");
            internal JournalRotationPosition Position => Journal.CaptureRotationPosition() ?? throw new InvalidOperationException("Mixed rotation position unavailable.");
            internal Fixture(IJournalDurability? flush = null)
            {
                Directory.CreateDirectory(root); boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                Journal = OperationJournal.OpenMixedForAuthority(boundary, lease, EnvironmentId, true, flush);
            }
            internal OperationIntent Intent(string id)
            {
                var target = new LoadTarget("probe", "save-" + id, "v1");
                return new OperationIntent(id, Source.InstanceId, new SubmissionKey("mixed-history-context", id + "-operation"),
                    OperationCommandHash.Compute(Source, target), Actor.ProfileId, 1, 1, Source, target, Actor.ProfileInstanceId);
            }
            internal async Task CompleteFalse(string id)
            {
                var intent = Intent(id);
                using (var reservation = Journal.TryReserveOperation() ?? throw new InvalidOperationException("Operation credit unavailable."))
                {
                    await Done(reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal));
                    await Done(reservation.AppendAsync(new CommitIntended(id, intent.Target, new NativeHostIdentity(101, 123456)), JournalSlot.Normal));
                    await Done(reservation.AppendAsync(new NativeTerminated(id, NativeLoadTaskState.RanToCompletion, false, null), JournalSlot.NativeTerminal));
                    await Done(reservation.AppendAsync(new OperationFinished(id, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE",
                        new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)), JournalSlot.Terminal));
                }
            }
            internal async Task<JournalTaskReceipt> Create(string id, TaskStatedGoal goal)
            { using (var ticket = Journal.TryReserveTaskCreation(id) ?? throw new InvalidOperationException("Create credit unavailable.")) return await Done(ticket.AppendAsync(MixedHistoryPublisherTests.Create(id, goal))); }
            internal async Task<JournalTaskReceipt> Revise(string id, long previous, TaskStatedGoal goal)
            { using (var ticket = Journal.TryReserveTaskRevision(id) ?? throw new InvalidOperationException("Revision credit unavailable.")) return await Done(ticket.AppendAsync(MixedHistoryPublisherTests.Revise(id, previous, goal))); }
            internal async Task<JournalTaskReceipt> Stop(string id)
            { using (var ticket = Journal.TryReserveTaskStop(id) ?? throw new InvalidOperationException("Stop credit unavailable.")) return await Done(ticket.AppendAsync(MixedHistoryPublisherTests.Stop(id))); }
            internal async Task<JournalRotationResult> Rotate(string[] operations, string[] tasks)
            {
                var position = Position;
                var rotation = Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence, operations, tasks))
                    ?? throw new InvalidOperationException("Mixed history rotation refused.");
                return await Done(rotation.Completion);
            }
            internal void StartPublisher(bool paused = false)
            {
                if (History != null) throw new InvalidOperationException("Duplicate publisher.");
                Store = new ControlledStore(new AtomicHistoryStore(boundary, lease, EnvironmentId), paused);
                History = new HistoryPublisher(Journal, Store, EnvironmentId, "0.1.0-dev.0", "different-publisher", new string('c', 64));
            }
            internal async Task Reopen()
            {
                Store?.Release.Set(); await Program.MustComplete(Journal.StopAsync());
                if (History != null) await Program.MustComplete(History.StopAsync());
                History = null; Store?.Dispose(); Store = null;
                Journal = OperationJournal.OpenMixedForAuthority(boundary, lease, EnvironmentId, false);
            }
            internal async Task<long> SeedRawGoals(TaskStatedGoal goal, int count)
            {
                await Program.MustComplete(Journal.StopAsync());
                using (var file = new FileStream(Path.Combine(boundary.DirectoryPath, "operations.journal"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    var selected = JournalBankReader.ReadMixed(file, EnvironmentId); long actualBytes = selected.Current.ThroughBytes;
                    for (int i = 0; i < count; i++)
                    {
                        JournalEntry entry = i == 0 ? (JournalEntry)MixedHistoryPublisherTests.Create("raw", goal) : MixedHistoryPublisherTests.Revise("raw", i, goal);
                        byte[] encoded = JournalFrameCodec.EncodeMixed(new JournalRecord(i + 2, EnvironmentId, entry, 3), out int length);
                        string canonical = Utf8.GetString(encoded, 12, length - 44);
                        if (!canonical.Contains("\\u0085")) throw new InvalidOperationException("Raw fixture requires an actually shorter accepted UTF8 representation.");
                        byte[] raw = RawFrame(canonical.Replace("\\u0085", "\u0085"));
                        file.Position = JournalLayout.Mixed.BankStart(selected.Current.Index) + JournalBankFormat.BodyOffset + actualBytes;
                        file.Write(raw, 0, raw.Length); actualBytes += raw.Length;
                        Array.Clear(raw, 0, raw.Length); Array.Clear(encoded, 0, encoded.Length);
                    }
                    file.Flush(true); return actualBytes;
                }
            }
            private static byte[] RawFrame(string json)
            {
                byte[] payload = Utf8.GetBytes(json); var frame = new byte[payload.Length + 44];
                if (payload.Length > TaskJournalLimits.MaximumMetadataPayload) throw new InvalidDataException("Raw goal frame exceeds existing metadata budget.");
                frame[0] = (byte)'S'; frame[1] = (byte)'L'; frame[2] = (byte)'J'; frame[3] = (byte)'O'; frame[4] = 3;
                for (int i = 0; i < 4; i++) frame[8 + i] = (byte)((uint)payload.Length >> (8 * i));
                Buffer.BlockCopy(payload, 0, frame, 12, payload.Length);
                using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(frame, 0, payload.Length + 12), 0, frame, payload.Length + 12, 32);
                return frame;
            }
            public void Dispose()
            {
                Store?.Release.Set(); Journal.StopAsync().GetAwaiter().GetResult(); History?.StopAsync().GetAwaiter().GetResult(); Store?.Dispose(); lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") || !Path.GetFileName(root).StartsWith("mixed-history-", StringComparison.Ordinal))
                    throw new IOException("Mixed history fixture containment invalid.");
                foreach (string directory in new[] { boundary.DirectoryPath, root })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mixed history fixture refuses directory links.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mixed history fixture refuses file links."); File.Delete(path); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
