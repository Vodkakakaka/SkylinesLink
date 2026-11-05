using System;
using System.Collections.Generic;
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

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>同一真实writer上的Task专用信用；不代替Task授权或游戏控制验收。</summary>
    internal static class TaskJournalWriterTests
    {
        private const string EnvironmentId = "task-writer-probe";
        private static readonly TaskActor Actor = new TaskActor("planner", new string('a', 32), 1, 1);
        private static readonly WorldContextSnapshot Source = new WorldContextSnapshot("task-writer-instance", 1, "ready", "city-a");
        private static int checks;
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await LegacyAndOperationAdmission();
            await MaximumIntentThroughWriter();
            await MixedGroupCommit();
            await OrdinarySlotsAndStopLifetime();
            await CreationHandoff();
            await RecoveredStopsAndOriginalFinal();
            await RetainedTasksAndRotation();
            await StopBehindOwnedRotation();
            await BadStartupAndUnknownFlush();
            return checks;
        }

        private static TaskCreated Create(string id)
        {
            var goal = new TaskStatedGoal("Stated task " + id, "Keep the exact current user intent", Array.Empty<string>(), Array.Empty<string>());
            return new TaskCreated(id, Decision(id + "-create", Source, TaskCommandHash.Create(Source, goal)), goal);
        }
        private static TaskGoalRevised Revise(string id, long previous = 1)
        {
            var goal = new TaskStatedGoal("Revision " + previous, "Explicit revised user intent", Array.Empty<string>(), Array.Empty<string>());
            return new TaskGoalRevised(id, previous, previous + 1, 1,
                Decision(id + "-revise-" + previous, Source, TaskCommandHash.Revise(id, previous, 1, Source, goal)), goal);
        }
        private static TaskStopped Stop(string id) => new TaskStopped(id, 1, 2,
            Decision(id + "-stop", new WorldContextSnapshot("later-menu", 9, "menu", null), TaskCommandHash.Stop(id, 1)));
        private static TaskDecision Decision(string key, WorldContextSnapshot source, string hash) =>
            new TaskDecision(new SubmissionKey("task-writer-context", key), Actor, source, hash);
        private static OperationIntent Intent(string id) => new OperationIntent(id, Source.InstanceId,
            new SubmissionKey("task-writer-context", id + "-load"), OperationCommandHash.Compute(Source, new LoadTarget("probe", "save-" + id, "v1")),
            Actor.ProfileId, 1, 1, Source, new LoadTarget("probe", "save-" + id, "v1"), Actor.ProfileInstanceId);
        private static OperationFinished FalseFinal(string id) => new OperationFinished(id, "failed", true, SubmissionKnowledge.Submitted,
            "LOAD_RETURNED_FALSE", new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false,
                LoadObservationEnd.OwnerStopped, null));
        private static async Task<T> Done<T>(Task<T> work) { await Program.MustComplete(work); return await work; }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected task writer rejection: " + label);
        }
        private static async Task PersistenceFailure(Task work)
        {
            try { await Program.MustComplete(work); }
            catch (JournalPersistenceException) { Check(true, "unknown real IO is reported as journal persistence failure"); return; }
            throw new InvalidOperationException("Expected task writer persistence failure.");
        }

        private static async Task LegacyAndOperationAdmission()
        {
            Program.Stage = "task writer explicit format and operation credit boundary";
            using (var f = new Fixture(mixed: false))
            {
                Check(!f.Tasks.SupportsTaskMetadata && f.Tasks.TryReserveTaskCreation("legacy") == null &&
                    f.Tasks.TryReserveTaskRevision("legacy") == null && f.Tasks.TryReserveTaskStop("legacy") == null,
                    "the existing public authority factory never enables metadata or mixed stop credits");
                byte[] before = f.ReadBytes();
                using (var operation = f.Journal.TryReserveOperation()!)
                    Reject(() => operation.AppendAsync(Create("not-an-operation"), JournalSlot.Normal),
                        "ordinary four-frame operation credit cannot smuggle Task metadata into its normal stage");
                Check(f.Journal.IsHealthy && before.SequenceEqual(f.ReadBytes()),
                    "metadata refusal happens before IO and preserves the healthy legacy owner");
            }
            using (var f = new Fixture())
            {
                Check(f.Tasks.SupportsTaskMetadata && f.Journal.Recovery.JournalSchemaRevision == 3 &&
                    f.Journal.CaptureConfirmedPrefix()!.JournalSchemaRevision == 3 && f.Journal.Recovery.Records.Single().SchemaRevision == 3,
                    "only the explicit internal mixed factory starts the schema3 writer");
                var oldRows = f.Journal.Recovery.Records.Select(record => new JournalRecord(record.Sequence, record.EnvironmentId, record.Entry, 2)).ToArray();
                var mixedFamily = new ConfirmedJournalPrefix(oldRows, oldRows.Length, 3);
                Check(JournalFactsFold.Fold(oldRows).AllRecordsValid, "all-record2 fixture is independently valid despite its explicit mixed physical family");
                bool historyRefused = false;
                try { HistoryProjection.FromConfirmedPrefix(mixedFamily, EnvironmentId, "0.1.0", Source.InstanceId, new string('c', 64), 1); }
                catch (NotSupportedException) { historyRefused = true; }
                Check(historyRefused, "CP1 cannot infer journal2 provenance from a mixed family's all-record2 row projection");
                using (var operation = f.Journal.TryReserveOperation()!)
                    Reject(() => operation.ValidateEntry(Create("also-not-an-operation")),
                        "mixed operation reservations keep their original business stage boundary");
                await f.Create("mixed");
                await f.Journal.StopAsync(); byte[] before = f.ReadBytes();
                await f.OpenLegacy();
                Check(f.Journal.Recovery.Incompatible && !f.Journal.IsHealthy && !f.Tasks.SupportsTaskMetadata &&
                    f.Journal.TryReserveOperation() == null && f.Tasks.TryReserveTaskCreation("refused") == null,
                    "the unchanged authority factory refuses an existing control2 container without activating a mixed owner");
                await f.Journal.StopAsync();
                Check(before.SequenceEqual(f.ReadBytes()), "old-owner refusal preserves the mixed file bytes");
            }
        }

        private static async Task MixedGroupCommit()
        {
            Program.Stage = "one FIFO and one actual Flush acknowledge four mixed records";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.Create("a"); var facade = f.Journal; var lifetime = facade.Completion;
                long initialBodyBytes = f.ReadBodyBytes();
                Check(facade.CaptureConfirmedPrefix()!.ThroughBytes == initialBodyBytes && initialBodyBytes < new FileInfo(f.PathName).Length,
                    "mixed confirmed bytes cover actual business frames rather than fixed bank capacity or file EOF");
                string store = f.Position().StoreId; int baseline = flush.ActualFlushes;
                using (var blocker = facade.TryReserveOperation()!)
                using (var operation = facade.TryReserveOperation()!)
                {
                    flush.Arm(1);
                    Task<long> blocked = blocker.AppendAsync(new OperationAccepted(Intent("blocking-op")), JournalSlot.Normal);
                    await Program.MustComplete(flush.Entered.Task);
                    using (var revise = f.Tasks.TryReserveTaskRevision("a")!)
                    using (var create = f.Tasks.TryReserveTaskCreation("b")!)
                    {
                        Task<JournalTaskReceipt> r = revise.AppendAsync(Revise("a"));
                        Task<JournalTaskReceipt> c = create.AppendAsync(Create("b"));
                        using (var stop = f.Tasks.TryReserveTaskStop("a")!)
                        {
                            Task<JournalTaskReceipt> s = stop.AppendAsync(Stop("a"));
                            Task<long> o = operation.AppendAsync(new OperationAccepted(Intent("queued-op")), JournalSlot.Normal);
                            Check(facade.QueuedOrWriting == 5 && !r.IsCompleted && !c.IsCompleted && !s.IsCompleted && !o.IsCompleted &&
                                facade.CaptureConfirmedPrefix()!.ThroughBytes == initialBodyBytes,
                                "one physical blocked frame and four queued mixed frames share the original worker");
                            flush.Release.Set();
                            await Done(blocked); JournalTaskReceipt rr = await Done(r), cr = await Done(c), sr = await Done(s); long os = await Done(o);
                            Check(flush.ActualFlushes == baseline + 2 && rr.Sequence == 4 && cr.Sequence == 5 && sr.Sequence == 6 && os == 7 &&
                                rr.StoreId == store && cr.StoreId == store && sr.StoreId == store && rr.JournalSchemaRevision == 3 &&
                                facade.CaptureConfirmedPrefix()!.ThroughBytes == f.ReadBodyBytes(),
                                "the four already queued mixed records use one real group Flush and immutable actual-store receipts");
                            var facts = f.Fold();
                            Check(facts.AllRecordsValid && facts.Tasks.Single(task => task.TaskId == "a").IsStopped &&
                                facts.Tasks.Single(task => task.TaskId == "a").GoalRevision == 2 && facts.Operations.Count == 2,
                                "mixed group publication preserves the complete Task decisions and ordinary operation facts");
                        }
                    }
                    Check(f.Journal.TaskMetadataReservedCount == 0 && f.Journal.TaskStopReservedCount == 1 && f.Journal.RetainedTaskCount == 2 &&
                        ReferenceEquals(facade, f.Journal) && ReferenceEquals(lifetime, facade.Completion) && !lifetime.IsCompleted,
                        "actual stage completion returns ordinary slots while the other Task keeps its dedicated stop credit on the same facade");
                }
            }
        }

        private static async Task MaximumIntentThroughWriter()
        {
            Program.Stage = "maximum escaped Task intent passes the actual writer and new-owner recovery";
            using (var f = new Fixture())
            {
                string text = new string('\u0001', 256), id = new string('\u0001', 128);
                var goal = new TaskStatedGoal(id, new string('\u0001', 2048), Enumerable.Repeat(text, 16).ToArray(), Enumerable.Repeat(text, 16).ToArray());
                var source = new WorldContextSnapshot(id, long.MaxValue, "ready", id);
                var actor = new TaskActor(new string('a', 64), new string('a', 32), AuthorizationState.MaximumCounter, AuthorizationState.MaximumCounter);
                var entry = new TaskCreated(id, new TaskDecision(new SubmissionKey(id, id), actor, source, TaskCommandHash.Create(source, goal)), goal);
                byte[] encoded = JournalFrameCodec.EncodeMixed(new JournalRecord(2, EnvironmentId, entry, 3), out int length);
                Array.Clear(encoded, 0, encoded.Length);
                Check(length - 44 > 65536 && length <= TaskJournalLimits.MaximumMetadataFrame,
                    "writer fixture uses an actual over-64KiB intent within the separate metadata budget");
                using (var create = f.Tasks.TryReserveTaskCreation(id)!) await Done(create.AppendAsync(entry));
                await f.Reopen(); var facts = f.Fold().Tasks.Single();
                Check(f.Journal.IsHealthy && f.Journal.TaskStopReservedCount == 1 && facts.CurrentGoal.RequestText == goal.RequestText &&
                    facts.CurrentGoal.ConstraintsText.SequenceEqual(goal.ConstraintsText) && facts.GoalHistory[0].Decision.Source.Generation == long.MaxValue,
                    "real writer Flush and reopening preserve all maximum text and original source while rebuilding protected Stop credit");
                var stopEntry = new TaskStopped(id, 1, 2, new TaskDecision(new SubmissionKey("maximum-stop", "stop"), actor, Source, TaskCommandHash.Stop(id, 1)));
                using (var stop = f.Tasks.TryReserveTaskStop(id)!) await Done(stop.AppendAsync(stopEntry));
                await f.Reopen();
                Check(f.Fold().Tasks.Single().IsStopped && f.Journal.TaskStopReservedCount == 0,
                    "maximum accepted metadata retains a usable Stop and survives another actual owner recovery");
            }
        }

        private static async Task OrdinarySlotsAndStopLifetime()
        {
            Program.Stage = "ordinary metadata exhaustion cannot borrow protected Stop or operation credit";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.Create("a"); await f.Create("b");
                using (var reviseA = f.Tasks.TryReserveTaskRevision("a")!)
                using (var reviseB = f.Tasks.TryReserveTaskRevision("b")!)
                {
                    var operations = Enumerable.Range(0, 4).Select(_ => f.Journal.TryReserveOperation()!).ToArray();
                    try
                    {
                        Check(operations.All(item => item != null) && f.Journal.TryReserveOperation() == null &&
                            f.Journal.TaskMetadataReservedCount == 2 && f.Tasks.TryReserveTaskCreation("no-slot") == null,
                            "two ordinary metadata slots do not reduce the existing four active-operation reservation slots");
                        using (var unqueued = f.Tasks.TryReserveTaskStop("a")!)
                            Check(unqueued != null && f.Journal.TaskStopReservedCount == 2,
                                "claiming protected Stop does not require an ordinary metadata slot");
                        using (var stop = f.Tasks.TryReserveTaskStop("a")!)
                        {
                            flush.Arm(1); Task<JournalTaskReceipt> work = stop.AppendAsync(Stop("a"));
                            await Program.MustComplete(flush.Entered.Task);
                            Reject(() => reviseA.AppendAsync(Revise("a")), "a previously unqueued revision cannot append behind an already queued Stop");
                            reviseA.Dispose(); reviseB.Dispose(); foreach (var operation in operations) operation.Dispose();
                            stop.Dispose();
                            Task closing = f.Journal.StopAsync();
                            Check(!work.IsCompleted && !closing.IsCompleted && f.Journal.QueuedOrWriting == 1 &&
                                f.Journal.TaskStopReservedCount == 2 && f.Journal.TaskMetadataReservedCount == 0,
                                "disposing handles or closing the owner cannot refund an in-flight Stop or finish its real IO");
                            flush.Release.Set(); await Done(work); await Program.MustComplete(closing);
                            Check(f.Journal.TaskStopReservedCount == 0 && f.Journal.TaskMetadataReservedCount == 0,
                                "only after real worker termination may shutdown release its remaining lifetime credits");
                        }
                    }
                    finally { flush.Release.Set(); foreach (var operation in operations) operation?.Dispose(); }
                }
                await f.Reopen();
                Check(f.Journal.TaskStopReservedCount == 1 && f.Journal.RetainedTaskCount == 2 && f.Fold().Tasks.Single(task => task.TaskId == "a").IsStopped &&
                    !f.Fold().Tasks.Single(task => task.TaskId == "b").IsStopped,
                    "new-owner recovery reconstructs only the still-live Task stop obligation");
            }
        }

        private static async Task CreationHandoff()
        {
            Program.Stage = "Task creation transfers Stop ownership only through the actual queue lifetime";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                using (var unused = f.Tasks.TryReserveTaskCreation("unused")!)
                {
                    Check(f.Journal.RetainedTaskCount == 1 && f.Journal.TaskMetadataReservedCount == 1 &&
                        f.Journal.TaskStopReservedCount == 1 && f.Tasks.TryReserveTaskStop("unused") == null,
                        "a creation reservation holds both credits but cannot produce an orphan Stop before enqueue");
                }
                Check(f.Journal.RetainedTaskCount == 0 && f.Journal.TaskMetadataReservedCount == 0 && f.Journal.TaskStopReservedCount == 0,
                    "disposing an unqueued creation returns exactly its temporary Task slot and both unused credits");
                using (var create = f.Tasks.TryReserveTaskCreation("queued")!)
                {
                    flush.Arm(1); Task<JournalTaskReceipt> created = create.AppendAsync(Create("queued"));
                    await Program.MustComplete(flush.Entered.Task); create.Dispose();
                    using (var stop = f.Tasks.TryReserveTaskStop("queued")!)
                    {
                        Task<JournalTaskReceipt> stopped = stop.AppendAsync(Stop("queued")); stop.Dispose();
                        Check(f.Journal.TaskMetadataReservedCount == 1 && f.Journal.TaskStopReservedCount == 1 && !created.IsCompleted && !stopped.IsCompleted,
                            "queued creation owns its ordinary work until Flush and already protects the following Stop independently of handle Dispose");
                        flush.Release.Set(); JournalTaskReceipt c = await Done(created), s = await Done(stopped);
                        Check(c.Sequence == 2 && s.Sequence == 3 && c.StoreId == s.StoreId && f.Fold().Tasks.Single().IsStopped &&
                            f.Journal.TaskMetadataReservedCount == 0 && f.Journal.TaskStopReservedCount == 0,
                            "creation then Stop share the single FIFO and release only their actual finished stages");
                    }
                }
            }
        }

        private static async Task RecoveredStopsAndOriginalFinal()
        {
            Program.Stage = "joint recovery protects eight Task Stops and the original missing false final";
            using (var f = new Fixture())
            {
                for (int i = 0; i < 8; i++) await f.Create("task-" + i);
                var intent = Intent("needs-final");
                using (var operation = f.Journal.TryReserveOperation()!)
                {
                    await Done(operation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal));
                    await Done(operation.AppendAsync(new CommitIntended(intent.OperationId, intent.Target, new NativeHostIdentity(101, 123456)), JournalSlot.Normal));
                    await Done(operation.AppendAsync(new NativeTerminated(intent.OperationId, NativeLoadTaskState.RanToCompletion, false, null), JournalSlot.NativeTerminal));
                }
                using (var recoveredFlush = new ControlledFlush())
                {
                    await f.Reopen(recoveredFlush); long originalThrough = f.Journal.Recovery.Records.Count;
                    Check(recoveredFlush.ActualFlushes == 1 && f.Journal.IsHealthy && f.Journal.TaskStopReservedCount == 8 &&
                        f.Journal.RecoveryReservedTerminalCount == 1 && f.Journal.TryReserveOperation() == null &&
                        f.Tasks.TryReserveTaskRevision("task-1") == null,
                        "opening reconstructs joint stop and recovery-final credit before ordinary admission");
                    JournalTaskReceipt stopped = await f.Stop("task-0");
                    using (var recovery = f.Journal.TryReserveRecoveryTerminals(originalThrough, 1))
                    {
                        Check(stopped.Sequence == originalThrough + 1 && recovery != null && f.Journal.TaskStopReservedCount == 7,
                            "a protected Stop may append first without invalidating the exact original recovery reservation identity");
                        await Done(recovery!.AppendAsync(FalseFinal(intent.OperationId)));
                    }
                    using (var ordinary = f.Tasks.TryReserveTaskRevision("task-1"))
                        Check(ordinary != null && f.Journal.RecoveryReservedTerminalCount == 0,
                            "ordinary metadata reopens only after the original recovery-final reservation has physically finished");
                }
                await f.Reopen(); var facts = f.Fold();
                Check(facts.AllRecordsValid && facts.Tasks.Count == 8 && f.Journal.RetainedTaskCount == 8 && f.Journal.TaskStopReservedCount == 7 &&
                    facts.Operations.Single().Native!.LoadReturnValue == false && facts.Operations.Single().Final!.Load.LoadReturnValue == false,
                    "later recovery preserves all Task identities and the full original false operation evidence");
            }
            using (var f = new Fixture())
            {
                await f.Create("retained-stop");
                using (var operation = f.Journal.TryReserveOperation()!)
                    await Done(operation.AppendAsync(new OperationAccepted(Intent("unfinished-final")), JournalSlot.Normal));
                await f.Reopen();
                var recovery = f.Journal.TryReserveRecoveryTerminals(f.Journal.Recovery.Records.Count, 1)!;
                recovery.Dispose();
                Check(f.Journal.RecoveryReservedTerminalCount == 1 && f.Journal.TryReserveOperation() == null &&
                    f.Tasks.TryReserveTaskRevision("retained-stop") == null,
                    "disposing an unwritten startup final ticket cannot return its original obligation to ordinary admission");
                await f.Stop("retained-stop"); await f.Reopen();
                Check(f.Journal.RecoveryReservedTerminalCount == 1 && f.Journal.TaskStopReservedCount == 0 &&
                    f.Fold().Operations.Single().Final == null && f.Fold().Tasks.Single().IsStopped,
                    "dedicated Stop remains usable and new-owner recovery still owns the abandoned missing final");
            }
        }

        private static async Task RetainedTasksAndRotation()
        {
            Program.Stage = "stopped history still occupies Task capacity until real mixed retirement";
            using (var f = new Fixture())
            {
                for (int i = 0; i < 8; i++) { await f.Create("old-" + i); await f.Stop("old-" + i); }
                Check(f.Journal.RetainedTaskCount == 8 && f.Journal.TaskStopReservedCount == 0 && f.Tasks.TryReserveTaskCreation("ninth") == null,
                    "stopping eight Tasks never permits a ninth retained TaskCreated chain to break the mixed Fold limit");
                var original = f.Position();
                var first = f.Journal.TryStartRotation(new JournalRotationPlan(original.StoreId, original.ThroughSequence,
                    Array.Empty<string>(), new[] { "old-7" }))!;
                JournalRotationResult result = await Done(first.Completion);
                Check(result.Outcome == JournalRotationOutcome.Installed && f.Journal.RetainedTaskCount == 1 &&
                    f.Journal.TaskStopReservedCount == 0 && f.Fold().Tasks.Single().TaskId == "old-7",
                    "only installed retirement releases the stopped Task slots");
                await f.Create("live");
                var source = f.Position(); byte[] before = f.ReadBytes();
                Reject(() => f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence,
                    Array.Empty<string>(), Array.Empty<string>())), "rotation cannot discard a still-live Task carrying stop credit");
                Check(before.SequenceEqual(f.ReadBytes()) && f.Journal.IsHealthy,
                    "invalid live-Task retirement is rejected before touching the physical source");
                var second = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence,
                    Array.Empty<string>(), new[] { "live" }))!;
                JournalRotationResult next = await Done(second.Completion);
                Check(next.Outcome == JournalRotationOutcome.Installed && f.Journal.TaskStopReservedCount == 1 && f.Journal.RetainedTaskCount == 1 &&
                    f.Fold().Tasks.Single().TaskId == "live", "idle stop credit crosses the next actual generation without blocking or being refunded by rotation");
                JournalTaskReceipt stopped = await f.Stop("live");
                Check(stopped.StoreId == f.Position().StoreId && f.Journal.TaskStopReservedCount == 0 && f.Fold().Tasks.Single().IsStopped,
                    "the retained Task still owns a usable protected Stop after two bank handoffs");
            }
        }

        private static async Task StopBehindOwnedRotation()
        {
            Program.Stage = "Stop queued behind owned rotation receives its actual new source identity";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.Create("live"); await f.Create("retire"); await f.Stop("retire");
                var source = f.Position(); flush.Arm(1);
                IJournalRotation rotation = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence,
                    Array.Empty<string>(), new[] { "live" }))!;
                await Program.MustComplete(flush.Entered.Task);
                using (var stop = f.Tasks.TryReserveTaskStop("live")!)
                {
                    Task<JournalTaskReceipt> work = stop.AppendAsync(Stop("live"));
                    Check(!work.IsCompleted && f.Journal.QueuedOrWriting == 2 && f.Journal.TaskStopReservedCount == 1,
                        "owned maintenance does not reject or consume the live Task dedicated Stop admission");
                    flush.Release.Set(); JournalRotationResult installed = await Done(rotation.Completion); JournalTaskReceipt receipt = await Done(work);
                    string installedStore = ((StoreOpened)installed.Prefix.Records[0].Entry).StoreId;
                    Check(receipt.StoreId == installedStore && receipt.StoreId != source.StoreId && receipt.Sequence == installed.Prefix.ThroughSequence + 1 &&
                        receipt.JournalSchemaRevision == 3 && f.Fold().Tasks.Single().IsStopped,
                        "Stop is written after the handoff on the same worker and ACK carries its actual store and sequence");
                }
            }
        }

        private static async Task BadStartupAndUnknownFlush()
        {
            Program.Stage = "valid mixed bytes without joint recovery capacity remain readable but not writable";
            using (var f = new Fixture())
            {
                var rows = new List<JournalEntry> { Create("full") };
                for (int previous = 1; previous <= 127; previous++) rows.Add(Revise("full", previous));
                await f.AppendRaw(rows); byte[] before = f.ReadBytes(); await f.Reopen();
                Check(f.Journal.Recovery.Complete && f.Journal.Recovery.DurableConfirmed && !f.Journal.Recovery.Incompatible &&
                    f.Journal.Recovery.Records.Count == 129 && JournalFactsFold.FoldMixed(f.Journal.Recovery.Records).AllRecordsValid && !f.Journal.IsHealthy,
                    "a full but structurally valid source is not relabeled corrupt when its live Task stop credit cannot be restored");
                Check(f.Tasks.TryReserveTaskStop("full") == null && f.Tasks.TryReserveTaskCreation("new") == null && f.Journal.TryReserveOperation() == null &&
                    before.SequenceEqual(f.ReadBytes()), "failed recovery admission preserves every byte and rejects all new writes");
            }
            Program.Stage = "lost Task Stop acknowledgement is sticky until a genuinely new recovery owner";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.Create("unknown");
                long? confirmedBefore = f.Journal.CaptureConfirmedPrefix()!.ThroughBytes;
                using (var stop = f.Tasks.TryReserveTaskStop("unknown")!)
                {
                    flush.Arm(1, failAfterFlush: true); flush.Release.Set();
                    await PersistenceFailure(stop.AppendAsync(Stop("unknown"))); stop.Dispose();
                    int calls = flush.ActualFlushes;
                    Check(!f.Journal.IsHealthy && f.Tasks.TryReserveTaskStop("unknown") == null &&
                        f.Tasks.TryReserveTaskRevision("unknown") == null && flush.ActualFlushes == calls &&
                        f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == confirmedBefore && f.ReadBodyBytes() > confirmedBefore.GetValueOrDefault(),
                        "a real Stop Flush followed by lost acknowledgement neither refunds a retry nor revives the original owner");
                }
                using (var recovery = new ControlledFlush())
                {
                    await f.Reopen(recovery);
                    Check(recovery.ActualFlushes == 1 && f.Journal.IsHealthy && f.Journal.TaskStopReservedCount == 0 &&
                        f.Journal.RetainedTaskCount == 1 && f.Fold().Tasks.Single().IsStopped,
                        "only a new owner actual recovery Flush can confirm the already present Stop and close its dedicated obligation");
                }
            }
        }

        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            private int calls, actualFlushes, pauseAt;
            private bool failAfterFlush;
            internal int ActualFlushes => Volatile.Read(ref actualFlushes);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal void Arm(int relativeCall, bool failAfterFlush = false)
            { this.failAfterFlush = failAfterFlush; Volatile.Write(ref pauseAt, Volatile.Read(ref calls) + relativeCall); }
            public void Flush(FileStream file)
            {
                bool selected = Interlocked.Increment(ref calls) == Volatile.Read(ref pauseAt);
                if (selected) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Task writer fixture Flush deadline."); }
                file.Flush(true); Interlocked.Increment(ref actualFlushes);
                if (selected && failAfterFlush) throw new IOException("Synthetic lost acknowledgement after actual Task Flush.");
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }

        private sealed class ObservingDurability : IJournalDurability
        {
            private readonly IJournalDurability inner;
            internal ObservingDurability(IPrivateStorageBoundary boundary, IJournalDurability? inner)
            { this.inner = inner ?? new FileJournalDurability(boundary); }
            internal FileStream? Current { get; private set; }
            public void Flush(FileStream file) { Current = file; inner.Flush(file); }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "task-journal-writer-" + Guid.NewGuid().ToString("N"));
            private readonly IPrivateStorageBoundary boundary;
            private readonly EnvironmentLease lease;
            private readonly bool mixed;
            private ObservingDurability? observing;
            internal OperationJournal Journal { get; private set; }
            internal ITaskJournal Tasks => Journal;
            internal string PathName => Path.Combine(boundary.DirectoryPath, "operations.journal");
            internal long ReadBodyBytes()
            {
                using (var stream = new MemoryStream(ReadBytes(), false))
                    return JournalBankReader.Read(stream, EnvironmentId, mixed ? JournalLayout.Mixed : JournalLayout.Legacy).Current.ThroughBytes;
            }
            internal byte[] ReadBytes()
            {
                if (Journal.QueuedOrWriting != 0) throw new InvalidOperationException("Task writer snapshot requires physically quiescent IO.");
                FileStream? owned = observing?.Current;
                if (owned != null && owned.CanRead && owned.CanSeek)
                {
                    long position = owned.Position;
                    try { return ReadSnapshot(owned); }
                    finally { owned.Position = position; }
                }
                using (var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None))
                    return ReadSnapshot(stream);
            }
            private static byte[] ReadSnapshot(Stream stream)
            {
                if (stream.Length > JournalLayout.Mixed.FileBytes) throw new InvalidDataException("Task writer snapshot exceeds its fixed container.");
                var bytes = new byte[checked((int)stream.Length)];
                stream.Position = 0; JournalBankFormat.ReadExactly(stream, bytes, 0, bytes.Length);
                return bytes;
            }
            internal Fixture(IJournalDurability? flush = null, bool mixed = true)
            {
                this.mixed = mixed; Directory.CreateDirectory(root);
                boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                Journal = Open(true, flush);
            }
            private OperationJournal Open(bool initialize, IJournalDurability? flush)
            {
                observing = new ObservingDurability(boundary, flush);
                return mixed ? OperationJournal.OpenMixedForAuthority(boundary, lease, EnvironmentId, initialize, observing)
                    : OperationJournal.OpenForAuthority(boundary, lease, EnvironmentId, initialize, observing);
            }
            internal JournalRotationPosition Position() => Journal.CaptureRotationPosition() ?? throw new InvalidOperationException("Task writer position unavailable.");
            internal JournalFactsFoldResult Fold() => JournalFactsFold.FoldMixed(Journal.CaptureConfirmedPrefix()!.Records);
            internal async Task<JournalTaskReceipt> Create(string id)
            {
                using (var reservation = Tasks.TryReserveTaskCreation(id) ?? throw new InvalidOperationException("Task creation credit unavailable."))
                { var entry = TaskJournalWriterTests.Create(id); reservation.ValidateEntry(entry); return await Done(reservation.AppendAsync(entry)); }
            }
            internal async Task<JournalTaskReceipt> Stop(string id)
            {
                using (var reservation = Tasks.TryReserveTaskStop(id) ?? throw new InvalidOperationException("Protected Stop credit unavailable."))
                { var entry = TaskJournalWriterTests.Stop(id); reservation.ValidateEntry(entry); return await Done(reservation.AppendAsync(entry)); }
            }
            internal async Task Reopen(IJournalDurability? flush = null)
            { await Program.MustComplete(Journal.StopAsync()); Journal = Open(false, flush); }
            internal async Task OpenLegacy()
            {
                await Program.MustComplete(Journal.StopAsync()); observing = new ObservingDurability(boundary, null);
                Journal = OperationJournal.OpenForAuthority(boundary, lease, EnvironmentId, false, observing);
            }
            internal async Task AppendRaw(IReadOnlyList<JournalEntry> entries)
            {
                await Program.MustComplete(Journal.StopAsync());
                using (var stream = new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    var selected = JournalBankReader.ReadMixed(stream, EnvironmentId); long offset = selected.Current.ThroughBytes;
                    int sequence = selected.Current.Records.Count;
                    foreach (var entry in entries)
                    {
                        byte[] frame = JournalFrameCodec.EncodeMixed(new JournalRecord(++sequence, EnvironmentId, entry, 3), out int length);
                        try { stream.Position = JournalLayout.Mixed.BankStart(selected.Current.Index) + JournalBankFormat.BodyOffset + offset; stream.Write(frame, 0, length); offset += length; }
                        finally { Array.Clear(frame, 0, frame.Length); }
                    }
                    stream.Flush(true);
                }
            }
            public void Dispose()
            {
                Journal.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(root).StartsWith("task-journal-writer-", StringComparison.Ordinal)) throw new IOException("Task writer fixture containment invalid.");
                foreach (string directory in new[] { boundary.DirectoryPath, root })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Task writer fixture refuses directory links.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Task writer fixture refuses file links."); File.Delete(path); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
