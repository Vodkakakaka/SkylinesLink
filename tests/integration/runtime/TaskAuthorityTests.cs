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
using SkylinesLink.Mod.Runtime;
using Newtonsoft.Json.Linq;
using Contracts = SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>真实授权、上下文和单writer的Task控制；不提供游戏执行或第二业务账。</summary>
    internal static class TaskAuthorityTests
    {
        private const string EnvironmentId = "task-authority-probe";
        private static readonly LoadTarget Target = new LoadTarget("probe", "task-authority-save", "v1");
        private static int checks;
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static TaskStatedGoal Goal(string title = "Current user task") =>
            new TaskStatedGoal(title, "Preserve the user's exact request", new[] { "Normal game rules" }, new[] { "Observe the actual result" });
        private static async Task<T> Done<T>(Task<T> task) { await Program.MustComplete(task); return await task; }
        private static TaskMutationReceipt Receipt(TaskDecisionLookupSnapshot value) =>
            value.Receipt ?? throw new InvalidOperationException("Task fixture expected a decision receipt.");
        private static async Task Reject(Func<Task> action, string code)
        {
            try { await action(); }
            catch (OperationRejectedException error) { Check(error.Code == code, "Task authority rejects with " + code); return; }
            throw new InvalidOperationException("Expected Task authority rejection " + code + ".");
        }
        private static void RejectRead(Action action, string code)
        {
            try { action(); }
            catch (OperationRejectedException error) { Check(error.Code == code, "Task lookup rejects with " + code); return; }
            throw new InvalidOperationException("Expected Task lookup rejection " + code + ".");
        }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await CreateReviseStopAndRecovery();
            await StopFromUnidentifiedLoadedScope();
            await OneKeyRegistryAndOriginalSource();
            await BeginCreationAndOwnedClose();
            await ControlOnlyMutationReceipts();
            await GrantEpochAndProfileIncarnation();
            await ExhaustedOrdinaryAdmissionStillStops();
            await PendingRevisionAndStopShareFlush();
            await UnknownFlushAndCloseJoin();
            await RecoveryFlushDoesNotPublishDurableTask();
            await RetentionAndRecoveryObligations();
            await LateRotationContinuationCannotUndoStop();
            await GoalPagesAndQueryKindBoundary();
            await TaskReadFamilyQualification();
            await BoundedTaskDiscovery();
            await IncompleteRecoveryCannotListTasks();
            await StopPreflightDoesNotAdmitOrGrant();
            await StopSelectorPreservesRestoredOriginalDecision();
            await StopContractProjectionPreservesLiveAndPublishedFacts();
            return checks;
        }

        private static async Task TaskReadFamilyQualification()
        {
            Program.Stage = "Task reads reject the real default Legacy family without changing its original journal";
            using (var f = new Fixture(mixed: false))
            {
                byte[] original = f.ReadJournalBytes(); var subject = f.Subject;
                var key = new SubmissionKey("unissued", "missing-task-decision");
                Check(f.Journal.Recovery.JournalSchemaRevision == 2 && f.Journal.IsHealthy,
                    "the existing default journal is healthy Legacy, not an empty Mixed Task history");
                RejectRead(() => f.Ops.LookupTask("not-created", subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.LookupTaskDecision(key, subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.ReadTaskGoal("not-created", 1, TaskGoalSection.Title, 0, 128, subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.LookupTasks(subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.BeginStopTask("not-created", 1, subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.ValidateTaskStop("not-created", 1, subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.RefreshTaskStopReceipt(key, subject), "TASKS_UNAVAILABLE");
                RejectRead(() => f.Ops.LookupTaskStopDecision("not-created", 1, subject), "TASKS_UNAVAILABLE");
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('b', 64), null);
                var controller = f.Subject;
                RejectRead(() => f.Ops.LookupTask("not-created", controller), "UNAUTHORIZED");
                RejectRead(() => f.Ops.LookupTaskDecision(key, controller), "UNAUTHORIZED");
                RejectRead(() => f.Ops.ReadTaskGoal("not-created", 1, TaskGoalSection.Title, 0, 128, controller), "UNAUTHORIZED");
                RejectRead(() => f.Ops.LookupTasks(controller), "UNAUTHORIZED");
                Check(f.Journal.Recovery.JournalSchemaRevision == 2 && f.Journal.IsHealthy && original.SequenceEqual(f.ReadJournalBytes()),
                    "Read authority is checked before family qualification and no Task query migrates, rewrites or expands the Legacy file");
            }
        }

        private static async Task StopPreflightDoesNotAdmitOrGrant()
        {
            Program.Stage = "Stop preflight shares the exact Control/owner/CAS rules without admitting work or preserving a grant";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var key = f.Key("preflight-create");
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(key, Goal(), f.Subject))).TaskId;
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('b', 64), null);
                var controller = f.Subject;
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                int queued = f.Journal.QueuedOrWriting;
                f.Ops.ValidateTaskStop(id, 1, controller);
                RejectRead(() => f.Ops.RefreshTaskStopReceipt(key, controller), "IDEMPOTENCY_CONFLICT");
                using (var reservation = f.Journal.TryReserveTaskStop(id))
                    Check(reservation != null && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through &&
                        f.Journal.QueuedOrWriting == queued,
                        "Control-only preflight leaves the journal Stop claim available and does not append or queue work");
                RejectRead(() => f.Ops.LookupTask(id, controller), "UNAUTHORIZED");
                RejectRead(() => f.Ops.ValidateTaskStop(id, 2, controller), "TASK_VERSION_CONFLICT");

                await f.Grants.UpdateAsync("other", GrantScope.Control, new string('c', 64), null);
                RejectRead(() => f.Ops.ValidateTaskStop(id, 1, f.Grants.Read("other")!), "TASK_OWNER_MISMATCH");
                await f.Grants.UpdateAsync("probe", GrantScope.Read, new string('d', 64), null);
                RejectRead(() => f.Ops.BeginStopTask(id, 1, controller), "UNAUTHORIZED");
                Check(!f.Ops.LookupTask(id, f.Subject).StopRequested && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "a successful preflight does not retain permission or latch after its Control grant is revoked");

                await f.Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('e', 64), null);
                var current = f.Subject;
                var revisionKey = f.Key("preflight-revision"); flush.Arm(1);
                f.Ops.BeginReviseTaskGoal(revisionKey, id, 1, 1, Goal("Pending revision"), current);
                await Program.MustComplete(flush.Entered.Task);
                queued = f.Journal.QueuedOrWriting;
                f.Ops.ValidateTaskStop(id, 1, current);
                Check(!f.Ops.LookupTask(id, current).StopRequested && f.Journal.QueuedOrWriting == queued,
                    "a slow ordinary flush does not block Stop preflight and validation alone never requests a stop");
                var pending = f.Ops.BeginStopTask(id, 1, current);
                f.Ops.ValidateTaskStop(id, 1, current);
                Check(f.Ops.RefreshTaskStopReceipt(Receipt(pending).Decision.Key, current).Kind == "pending",
                    "sender-side refresh returns the same pending Stop without awaiting its slow flush or another admission");
                Check(f.Ops.LookupTaskStopDecision(id, 1, current).Kind == "pending",
                    "the ordinary Read selector observes the original pending Stop without claiming a history confirmation");
                Check(f.Ops.LookupTask(id, current).StopRequested && Receipt(pending).Durability == "pending",
                    "the actual Begin latches independently, and pending original Stop revalidation precedes stale CAS rejection");
                flush.Release.Set();
                var confirmed = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, current)));
                long stoppedThrough = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                f.Ops.ValidateTaskStop(id, 1, current);
                RejectRead(() => f.Ops.ValidateTaskStop(id, 2, current), "TASK_STOPPED");
                Check(f.Ops.BeginStopTask(id, 1, current).Receipt!.Decision.Key.Equals(confirmed.Decision.Key) &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == stoppedThrough,
                    "confirmed original Stop validates and returns its original decision without another physical record");
                await f.Grants.UpdateAsync("probe", GrantScope.Read, new string('f', 64), null);
                RejectRead(() => f.Ops.RefreshTaskStopReceipt(confirmed.Decision.Key, current), "UNAUTHORIZED");
                RejectRead(() => f.Ops.RefreshTaskStopReceipt(confirmed.Decision.Key, f.Subject), "UNAUTHORIZED");
                Check(f.Ops.LookupTask(id, f.Subject).IsStopped && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == stoppedThrough,
                    "revoking Control after admission prevents a later receipt refresh without undoing or replaying the persisted Stop");
            }
        }

        private static async Task StopSelectorPreservesRestoredOriginalDecision()
        {
            Program.Stage = "same-Authority Stop selector reads pending and restored noncanonical original decisions without mutation";
            using (var f = new Fixture())
            {
                string id = Guid.NewGuid().ToString("N");
                var subject = f.Subject; var source = f.Context.Read(); var goal = Goal("Restored task");
                var actor = new TaskActor(subject.ProfileId, subject.ProfileInstanceId, subject.AuthorityEpoch, subject.Epoch);
                var createKey = new SubmissionKey("historical-creation", "create");
                var originalKey = new SubmissionKey("historical-control-namespace", "original-stop-request");
                using (var creation = f.Journal.TryReserveTaskCreation(id)!)
                    await Done(creation.AppendAsync(new TaskCreated(id,
                        new TaskDecision(createKey, actor, source, TaskCommandHash.Create(source, goal)), goal)));
                using (var stop = f.Journal.TryReserveTaskStop(id)!)
                    await Done(stop.AppendAsync(new TaskStopped(id, 1, 2,
                        new TaskDecision(originalKey, actor, source, TaskCommandHash.Stop(id, 1)))));
                await f.Reopen();
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                var selected = f.Ops.LookupTaskStopDecision(id, 1, f.Subject);
                var receipt = Receipt(selected);
                Check(selected.Kind == "found" && receipt.Kind == "stop" && receipt.Decision.Key.Equals(originalKey) &&
                    !receipt.Decision.Key.Equals(TaskCommandHash.StopKey(id, 1)) &&
                    receipt.Decision.Source.InstanceId == source.InstanceId && receipt.Decision.Actor.ProfileInstanceId == actor.ProfileInstanceId,
                    "the logical Stop selector preserves the restored original key, actor, source and actual confirmed receipt");
                var pair = new Contracts.TaskStopSelector { TaskId = id, ExpectedControlVersion = 1 };
                var compact = TaskStopQueryService.Compact(pair, selected);
                var full = TaskStopQueryService.Execute(f.Ops,
                    new JObject { ["taskId"] = id, ["expectedControlVersion"] = 1 }, f.Subject, f.Context.Read().InstanceId, 1000);
                Check((string?)compact["lookup"]!["receipt"]!["submissionKey"]!["writeContextId"] == originalKey.WriteContextId &&
                    (string?)full["result"]!["lookup"]!["receipt"]!["decision"]!["submissionKey"]!["requestId"] == originalKey.RequestId &&
                    (string?)full["result"]!["lookup"]!["receipt"]!["decision"]!["commandHash"] == receipt.Decision.CommandHash,
                    "compact and full live wire mappers also retain the recovered noncanonical original key and hash");
                f.Ops.ValidateTaskStop(id, 1, f.Subject);
                Check(f.Ops.BeginStopTask(id, 1, f.Subject).Receipt!.Decision.Key.Equals(originalKey) &&
                    f.Ops.RefreshTaskStopReceipt(originalKey, f.Subject).Kind == "found",
                    "preflight, idempotent Begin and late sender refresh all retain the same restored noncanonical decision");
                Check(f.Ops.LookupTaskStopDecision(id, 2, f.Subject).Reason == "STOP_NOT_COVERED" &&
                    f.Ops.LookupTaskStopDecision("uncovered-task", 1, f.Subject).Reason == "STOP_NOT_COVERED" &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "unknown Stop pairs remain uncovered, and neither lookup nor original retries append a new record");
                await f.Grants.UpdateAsync("reader", GrantScope.Read, new string('b', 64), null);
                Check(f.Ops.LookupTaskStopDecision(id, 1, f.Grants.Read("reader")!).Receipt!.Decision.Actor.ProfileInstanceId == actor.ProfileInstanceId,
                    "a current Read principal may inspect history without replacing the original Task owner");
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('c', 64), null);
                RejectRead(() => f.Ops.LookupTaskStopDecision(id, 1, f.Subject), "UNAUTHORIZED");
            }
        }

        private static async Task StopContractProjectionPreservesLiveAndPublishedFacts()
        {
            Program.Stage = "Stop compact/live/history contracts preserve real decisions at different confirmation waterlines";
            var history = new FrozenHistory();
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush, history: history))
            {
                var creation = await Done(f.Ops.CreateTaskAsync(f.Key("projection-create"), Goal(), f.Subject));
                string id = Receipt(creation).TaskId;
                var pair = new Contracts.TaskStopSelector { TaskId = id, ExpectedControlVersion = 1 };
                JObject Query(bool historical = false) => new JObject { ["taskId"] = id, ["expectedControlVersion"] = 1,
                    ["source"] = historical ? "history" : "live" };
                JToken Live() => TaskStopQueryService.Execute(f.Ops, Query(), f.Subject, f.Context.Read().InstanceId, 1000);
                JToken Published(long now = 1000) => TaskStopQueryService.Execute(f.Ops, Query(true), f.Subject, f.Context.Read().InstanceId, now);
                RejectRead(() => Published(), "HISTORY_UNAVAILABLE");
                history.Value = HistoryProjection.FromMixedConfirmedPrefix(f.Journal.CaptureConfirmedPrefix()!,
                    EnvironmentId, "0.1.0-dev.0", "published-instance", new string('c', 64), 900);
                var uncovered = Published();
                Check((string?)uncovered["result"]!["kind"] == "history" &&
                    (string?)uncovered["result"]!["lookup"]!["reason"] == "HISTORY_NOT_COVERED" &&
                    (long?)uncovered["result"]!["ageMs"] == 100,
                    "an existing complete CP2 without Stop returns its true source/age and not-covered rather than fallback or absence");
                Check((string?)Live()["result"]!["lookup"]!["reason"] == "STOP_NOT_COVERED",
                    "live Stop selection before admission is uncovered without creating a decision");
                flush.Arm(1);
                var admitted = f.Ops.BeginStopTask(id, 1, f.Subject);
                await Program.MustComplete(flush.Entered.Task);
                var compact = TaskStopQueryService.Compact(pair, admitted);
                var pending = Live();
                Check((string?)compact["lookup"]!["kind"] == "pending" &&
                    (bool?)compact["lookup"]!["receipt"]!["stopRequested"] == true &&
                    compact["lookup"]!["receipt"]!["journal"]!.Type == JTokenType.Null &&
                    (long?)compact["lookup"]!["receipt"]!["controlVersion"] == 2 &&
                    (string?)pending["result"]!["lookup"]!["kind"] == "pending" &&
                    (long?)f.Ops.LookupTask(id, f.Subject).ControlVersion == 1,
                    "pending compact ACK and full live receipt report the admitted result version while confirmed Task facts remain at version one");
                var originalDecision = pending["result"]!["lookup"]!["receipt"]!["decision"]!.DeepClone();
                Check((string?)Published()["result"]!["lookup"]!["reason"] == "HISTORY_NOT_COVERED",
                    "pending live Stop does not leak into the earlier confirmed history projection");
                var frozenCompact = compact.DeepClone(); pair.TaskId = "caller-mutated";
                Check(JToken.DeepEquals(compact, frozenCompact), "caller selector mutation cannot change the already projected ACK");
                bool wrongSelector = false, wrongKind = false;
                try { TaskStopQueryService.Compact(pair, admitted); } catch (InvalidOperationException) { wrongSelector = true; }
                try { TaskStopQueryService.Compact(new Contracts.TaskStopSelector { TaskId = id, ExpectedControlVersion = 1 }, creation); }
                catch (InvalidOperationException) { wrongKind = true; }
                Check(wrongSelector && wrongKind, "shape-valid selector mismatch and non-Stop receipts fail semantic projection");
                pair.TaskId = id;
                flush.Release.Set(); await Done(f.Ops.StopTaskAsync(id, 1, f.Subject));
                var refreshed = f.Ops.RefreshTaskStopReceipt(Receipt(admitted).Decision.Key, f.Subject);
                var confirmed = TaskStopQueryService.Compact(pair, refreshed);
                Check((string?)confirmed["lookup"]!["kind"] == "found" &&
                    confirmed["lookup"]!["receipt"]!["journal"]!.Type == JTokenType.Object &&
                    (bool?)confirmed["lookup"]!["receipt"]!["persistenceAttempted"] == true &&
                    (string?)confirmed["lookup"]!["receipt"]!["journal"]!["storeId"] == Receipt(refreshed).Journal!.StoreId &&
                    (long?)confirmed["lookup"]!["receipt"]!["journal"]!["sequence"] == Receipt(refreshed).Journal!.Sequence,
                    "sender refresh projects actual confirmed journal evidence without re-admitting the decision");
                history.Value = HistoryProjection.FromMixedConfirmedPrefix(f.Journal.CaptureConfirmedPrefix()!,
                    EnvironmentId, "0.1.0-dev.0", "published-instance", new string('c', 64), 900);
                f.Ready("a-new-current-city");
                var full = Published(); var value = full["result"]!;
                Check((string?)value["kind"] == "history" && (string?)value["access"] == "activeAuthority" &&
                    (string?)value["integrity"] == "sha256Verified" && (string?)value["lookup"]!["kind"] == "found" &&
                    (long?)value["lookup"]!["receipt"]!["sequence"] == Receipt(refreshed).Journal!.Sequence &&
                    (long?)value["source"]!["throughSequence"] == f.Journal.CaptureConfirmedPrefix()!.ThroughSequence &&
                    (long?)value["source"]!["throughBytes"] == f.Journal.CaptureConfirmedPrefix()!.ThroughBytes &&
                    JToken.DeepEquals(value["lookup"]!["receipt"]!["decision"], originalDecision) &&
                    JToken.DeepEquals(Live()["result"]!["lookup"]!["receipt"]!["decision"], originalDecision),
                    "both full query sources preserve the original actor/source/hash after the current city and generation change");
                Check(Published(899)["result"]!["ageMs"]!.Type == JTokenType.Null,
                    "future publication wall time has unknown age rather than negative or fabricated freshness");
                var returned = full.DeepClone(); returned["result"]!["source"]!["storeId"] = "caller-changed";
                Check((string?)Published()["result"]!["source"]!["storeId"] != "caller-changed",
                    "full output values do not alias the immutable published source");
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('b', 64), null);
                RejectRead(() => Live(), "UNAUTHORIZED"); RejectRead(() => Published(), "UNAUTHORIZED");
                Check((string?)TaskStopQueryService.Compact(pair,
                    f.Ops.RefreshTaskStopReceipt(Receipt(admitted).Decision.Key, f.Subject))["lookup"]!["kind"] == "found",
                    "Control-only may receive the original compact mutation receipt without acquiring ordinary Read");
            }
            using (var legacy = new Fixture(mixed: false))
            {
                var unavailable = TaskStopQueryService.Execute(legacy.Ops,
                    new JObject { ["taskId"] = "legacy-uncovered", ["expectedControlVersion"] = 1 }, legacy.Subject, "legacy-live", 1000);
                Check((string?)unavailable["result"]!["lookup"]!["reason"] == "TASKS_UNAVAILABLE" &&
                    unavailable["result"]!["lookup"]!["receipt"]!.Type == JTokenType.Null,
                    "the unchanged Legacy family yields explicit unavailable and no fabricated Stop receipt");
            }
        }

        private static async Task BoundedTaskDiscovery()
        {
            Program.Stage = "one authorized bounded Task list preserves summary, pending Stop and snapshot ownership";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var initial = f.Journal.CaptureConfirmedPrefix()!;
                Check(f.Ops.LookupTasks(f.Subject).Count == 0 && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == initial.ThroughSequence &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == initial.ThroughBytes,
                    "an empty qualified Mixed Task list is truthful and does not append any metadata");
                var ids = new List<string>();
                for (int index = 0; index < TaskJournalLimits.MaximumTasks - 1; index++)
                    ids.Add(Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("discover-" + index), Goal("Task " + index), f.Subject))).TaskId);

                var lastKey = f.Key("discover-pending"); var lastGoal = Goal("Pending task"); flush.Arm(1);
                var admitted = f.Ops.BeginCreateTask(lastKey, lastGoal, f.Subject); string pendingId = Receipt(admitted).TaskId; ids.Add(pendingId);
                await Program.MustComplete(flush.Entered.Task);
                string stoppedId = ids[0]; f.Ops.BeginStopTask(stoppedId, 1, f.Subject);
                var waitingCreate = f.Ops.CreateTaskAsync(lastKey, lastGoal, f.Subject);
                var waitingStop = f.Ops.StopTaskAsync(stoppedId, 1, f.Subject);
                var before = f.Journal.CaptureConfirmedPrefix()!;
                var listed = f.Ops.LookupTasks(f.Subject);
                var pending = listed.Single(task => task.TaskId == pendingId);
                var stopping = listed.Single(task => task.TaskId == stoppedId);
                Check(listed.Count == TaskJournalLimits.MaximumTasks &&
                    listed.Select(task => task.TaskId).SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal)) &&
                    pending.Kind == "pending" && pending.Title == null && pending.GoalRevision == null && pending.Durability == "pending" &&
                    pending.PendingOrdinary?.Kind == "create" && !pending.IsStopped && !pending.StopRequested &&
                    stopping.Kind == "found" && !stopping.IsStopped && stopping.StopRequested && stopping.PendingStop?.Kind == "stop",
                    "all eight tasks are discovered in Ordinal identity order while pending creation and accepted Stop remain distinct from confirmed facts");
                Check(listed.All(task => Newtonsoft.Json.JsonConvert.SerializeObject(task) ==
                    Newtonsoft.Json.JsonConvert.SerializeObject(f.Ops.LookupTask(task.TaskId, f.Subject))),
                    "list items and single-task reads expose the same complete bounded summary at the blocked journal waterline");
                bool immutable = false;
                try { ((IList<TaskLookupSnapshot>)listed)[0] = listed[0]; }
                catch (NotSupportedException) { immutable = true; }
                Check(immutable, "the returned list cannot replace a frozen summary through a mutable collection cast");
                RejectRead(() => f.Ops.BeginCreateTask(new SubmissionKey(lastKey.WriteContextId, "ninth-task"), Goal(), f.Subject), "BUSY");

                await f.Grants.UpdateAsync("reader", GrantScope.Read, new string('b', 64), null);
                var reader = f.Grants.Read("reader")!; var otherReader = f.Ops.LookupTasks(reader);
                Check(otherReader.Count == listed.Count && otherReader.All(task => task.Owner!.ProfileId == "probe" &&
                    task.Owner!.ProfileInstanceId == f.Subject.ProfileInstanceId) &&
                    otherReader.Select(task => task.TaskId).SequenceEqual(listed.Select(task => task.TaskId)),
                    "a different current Read principal can discover the shared environment without being substituted for historical task owners");
                RejectRead(() => f.Ops.BeginStopTask(stoppedId, 1, reader), "UNAUTHORIZED");
                await f.Grants.UpdateAsync("reader", GrantScope.Control, new string('c', 64), null);
                RejectRead(() => f.Ops.LookupTasks(reader), "UNAUTHORIZED");
                RejectRead(() => f.Ops.LookupTasks(f.Grants.Read("reader")!), "UNAUTHORIZED");
                var after = f.Journal.CaptureConfirmedPrefix()!;
                Check(after.ThroughSequence == before.ThroughSequence && after.ThroughBytes == before.ThroughBytes &&
                    !waitingCreate.IsCompleted && !waitingStop.IsCompleted,
                    "list, single and cross-reader queries neither append journal records nor complete or cancel the owned pending IO");
                flush.Release.Set(); await Done(waitingCreate); await Done(waitingStop);
                var current = f.Ops.LookupTasks(f.Subject);
                Check(current.Count == 8 && current.All(task => task.Kind == "found") &&
                    current.Single(task => task.TaskId == stoppedId).IsStopped &&
                    current.Single(task => task.TaskId == pendingId).Title == lastGoal.Title &&
                    pending.Kind == "pending" && pending.Title == null && !stopping.IsStopped && stopping.StopRequested &&
                    stopping.PendingStop?.Durability == "pending",
                    "fresh discovery follows confirmed progress while the earlier list keeps its original pending and safety-latch evidence");
            }
        }

        private static async Task IncompleteRecoveryCannotListTasks()
        {
            Program.Stage = "real damaged Mixed recovery cannot claim a complete empty Task discovery";
            using (var f = new Fixture())
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("before-bad-tail"), Goal(), f.Subject))).TaskId;
                await Program.MustComplete(f.Ops.CloseAsync()); f.DamageJournalTail(); byte[] damaged = f.ReadJournalBytes();
                await f.Reopen(sealRecovery: false);
                Check(!f.Journal.Recovery.Complete && f.Journal.Recovery.JournalSchemaRevision == 3 &&
                    f.Journal.Recovery.Records.Count == 0 && !f.Journal.IsHealthy,
                    "the current Mixed reader rejects the entire damaged bank instead of pretending it recovered a partial Task chain");
                RejectRead(() => f.Ops.LookupTasks(f.Subject), "JOURNAL_UNAVAILABLE");
                Check(f.Ops.LookupTask(id, f.Subject).Kind == "unavailable",
                    "a failed discovery never becomes an empty success or an absent known identity");
                await Program.MustComplete(f.Ops.CloseAsync());
                Check(damaged.SequenceEqual(f.ReadJournalBytes()), "rejecting the damaged discovery never rewrites its source");
            }
        }

        private static async Task GoalPagesAndQueryKindBoundary()
        {
            Program.Stage = "Task goal pages preserve Unicode, explicit revision and the operation-query error boundary";
            using (var f = new Fixture())
            {
                string emoji = "\U0001F600";
                var goal = new TaskStatedGoal("Paged goal", string.Concat(Enumerable.Repeat(emoji, 600)),
                    Enumerable.Repeat(string.Concat(Enumerable.Repeat(emoji, 256)), 16).ToArray(), new[] { "Observe native results" });
                var key = f.Key("paged-create"); string id = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, f.Subject))).TaskId;
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                var first = f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.RequestText, 0, 512, f.Subject);
                var last = f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.RequestText, first.NextOffset!.Value, 512, f.Subject);
                Check(first.Text!.Length == 1024 && first.NextOffset == 512 && last.Text!.Length == 176 && last.NextOffset == null &&
                    first.Text + last.Text == goal.RequestText, "text pages count Unicode scalars and never split a surrogate pair");
                var items = f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.ConstraintsText, 0, 4, f.Subject);
                Check(items.Items.Count == 4 && items.NextOffset == 4 && items.Items.All(item => item.Length == 512) &&
                    items.Journal!.Sequence == 2 && items.Durability == "durable" && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "array pages bind their explicit revision/source and querying never activates a journal write");
                RejectRead(() => f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.RequestText, 0, 513, f.Subject), "INVALID_ARGUMENT");
                RejectRead(() => f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.ConstraintsText, 0, 5, f.Subject), "INVALID_ARGUMENT");
                var dispatcher = new SnapshotCapabilityDispatcher(f.Context, Program.ProductVersion, f.Ops);
                var arguments = new Newtonsoft.Json.Linq.JObject { ["selector"] = new Newtonsoft.Json.Linq.JObject {
                    ["kind"] = "submissionKey", ["submissionKey"] = new Newtonsoft.Json.Linq.JObject {
                        ["writeContextId"] = key.WriteContextId, ["requestId"] = key.RequestId } } };
                bool kindRejected = false;
                try { dispatcher.Execute("operations.get", "5", arguments, f.Subject); }
                catch (CapabilityFailure error) { kindRejected = error.Code == "COMMAND_KIND_MISMATCH"; }
                Check(kindRejected && dispatcher.Execute("system.context", "2", new Newtonsoft.Json.Linq.JObject(), f.Subject) != null,
                    "known Task keys use the existing typed capability-error route rather than a fabricated absent operation");
                await Done(f.Ops.ReviseTaskGoalAsync(f.Key("paged-revise"), id, 1, 1, Goal("Later title"), f.Subject));
                Check(f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.RequestText, 512, 512, f.Subject).Text == last.Text &&
                    f.Ops.ReadTaskGoal(id, 2, TaskGoalSection.Title, 0, 512, f.Subject).Text == "Later title",
                    "reading an old goal revision cannot silently return the latest task wording");
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('b', 64), null);
                RejectRead(() => f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.Title, 0, 128, f.Subject), "UNAUTHORIZED");
            }
        }

        private static async Task CreateReviseStopAndRecovery()
        {
            Program.Stage = "Task authority real create revise Stop and restart";
            using (var f = new Fixture())
            {
                var createKey = f.Key("create"); var goal = Goal();
                var created = await Done(f.Ops.CreateTaskAsync(createKey, goal, f.Subject)); var c = Receipt(created);
                string id = c.TaskId; var state = f.Ops.LookupTask(id, f.Subject);
                Check(created.Kind == "found" && c.Kind == "create" && c.GoalRevision == 1 && c.ControlVersion == 1 &&
                    c.Durability == "durable" && c.Journal!.JournalSchemaRevision == 3 && state.Kind == "found" && state.CityId == "city-a" &&
                    state.Owner!.ProfileInstanceId == f.Subject.ProfileInstanceId && state.Title == goal.Title && !state.IsStopped,
                    "real create commits only the stated goal with stable owner and actual schema3 receipt");
                var revised = Receipt(await Done(f.Ops.ReviseTaskGoalAsync(f.Key("revise"), id, 1, 1, Goal("Revised task"), f.Subject)));
                Check(revised.Kind == "reviseGoal" && revised.GoalRevision == 2 && revised.ControlVersion == 1 &&
                    f.Ops.LookupTask(id, f.Subject).Title == "Revised task", "goal revision changes neither owner nor control generation");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(f.Key("stale-revision"), id, 1, 1, goal, f.Subject), "TASK_VERSION_CONFLICT");
                f.Context.EnterMenu();
                var stopped = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, f.Subject)));
                state = f.Ops.LookupTask(id, f.Subject);
                Check(stopped.Kind == "stop" && stopped.GoalRevision == null && stopped.ControlVersion == 2 &&
                    stopped.Decision.Key.Equals(TaskCommandHash.StopKey(id, 1)) && stopped.Decision.Source.State == "menu" &&
                    state.IsStopped && state.StopRequested && state.GoalRevision == 2 && state.ControlVersion == 2,
                    "Stop ignores the old goal and world while committing a precise control decision");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(f.Key("after-stop"), id, 2, 2, goal, f.Subject), "TASK_STOPPED");
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                var retry = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, f.Subject)));
                Check(retry.Journal!.Sequence == stopped.Journal!.Sequence && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "the original canonical Stop key retries its known decision rather than adding another Stop");
                await f.Reopen(); state = f.Ops.LookupTask(id, f.Subject);
                Check(state.IsStopped && state.GoalRevision == 2 && state.ControlVersion == 2 && state.Title == "Revised task" &&
                    state.Durability == "durable" && f.Ops.LookupTaskDecision(createKey, f.Subject).Receipt!.Decision.Source.CityId == "city-a" &&
                    f.Fold().Tasks.Single().Decisions.Count == 3,
                    "actual new-owner recovery reconstructs the complete Task chain and original decision facts");
            }
        }

        private static async Task StopFromUnidentifiedLoadedScope()
        {
            Program.Stage = "unknown loaded identity preserves Stop without inventing long-lived Task ownership";
            using (var f = new Fixture())
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("known-city-task"), Goal(), f.Subject))).TaskId;
                f.Context.ObserveReady(f.Context.EnterLoading());
                await Reject(() => f.Ops.CreateTaskAsync(f.Key("unknown-city-create"), Goal(), f.Subject), "CONTEXT_UNAVAILABLE");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(f.Key("unknown-city-revise"), id, 1, 1, Goal(), f.Subject), "CONTEXT_UNAVAILABLE");
                var stopped = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, f.Subject)));
                Check(stopped.Durability == "durable" && stopped.Decision.Source.State == "ready" && stopped.Decision.Source.CityId == null,
                    "ready scope without stable city identity may stop an existing owned Task");
                await f.Reopen();
                var recovered = f.Ops.LookupTaskStopDecision(id, 1, f.Subject);
                Check(recovered.Receipt!.Decision.Source.State == "ready" && recovered.Receipt.Decision.Source.CityId == null &&
                    f.Ops.LookupTask(id, f.Subject).IsStopped && f.Fold().Tasks.Single().Decisions.Count == 2,
                    "actual Mixed journal fold and recovery retain unknown-identity Stop audit facts");
            }
        }

        private static async Task OneKeyRegistryAndOriginalSource()
        {
            Program.Stage = "Task and operation share one request registry and retry source";
            using (var f = new Fixture())
            {
                var key = f.Key("task-key"); var goal = Goal(); var source = f.Context.Read();
                var created = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, f.Subject))); string id = created.TaskId;
                await Reject(() => f.Ops.SubmitAsync(key, Target, f.Subject), "IDEMPOTENCY_CONFLICT");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(key, id, 1, 1, goal, f.Subject), "IDEMPOTENCY_CONFLICT");
                RejectRead(() => f.Ops.Lookup(key, f.Subject), "COMMAND_KIND_MISMATCH");
                var operationKey = f.Key("load-key"); string operation = await f.Complete(operationKey);
                await Reject(() => f.Ops.CreateTaskAsync(operationKey, goal, f.Subject), "IDEMPOTENCY_CONFLICT");
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                f.Ready("city-b");
                var retry = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, f.Subject)));
                Check(retry.TaskId == id && retry.Decision.Source.InstanceId == source.InstanceId && retry.Decision.Source.Generation == source.Generation &&
                    retry.Decision.Source.CityId == "city-a" && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "a known create retry hashes its original source instead of today's different city");
                await Reject(() => f.Ops.CreateTaskAsync(key, Goal("Changed intent"), f.Subject), "IDEMPOTENCY_CONFLICT");
                Check(f.Ops.Lookup(operation, f.Subject).Load!.LoadReturnValue == false,
                    "Task request collisions preserve the existing complete false operation result");
                await f.Reopen();
                var recovered = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, f.Subject)));
                Check(recovered.TaskId == id && recovered.Decision.Source.InstanceId == source.InstanceId &&
                    recovered.Decision.Source.CityId == "city-a", "retry after restart still uses the persisted original decision, not a replacement context");
            }
        }

        private static async Task BeginCreationAndOwnedClose()
        {
            Program.Stage = "Begin Task creation returns pending while the Authority owns the actual blocked Flush";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var key = f.Key("begin-create"); var goal = Goal(); flush.Arm(1);
                var admitted = f.Ops.BeginCreateTask(key, goal, f.Subject);
                await Program.MustComplete(flush.Entered.Task);
                var original = Receipt(admitted);
                var waiting = f.Ops.CreateTaskAsync(key, goal, f.Subject);
                var retry = f.Ops.BeginCreateTask(key, goal, f.Subject);
                Task close = f.Ops.CloseAsync();
                Check(admitted.Kind == "pending" && original.Durability == "pending" && original.Journal == null &&
                    original.Decision.Key.Equals(key) && original.PersistenceAttempted &&
                    retry.Kind == "pending" && Receipt(retry).TaskId == original.TaskId && !waiting.IsCompleted && !close.IsCompleted,
                    "Begin and an Async retry share one accepted identity and Work without waiting for IO or releasing the Authority's Close obligation");
                flush.Release.Set(); var confirmed = Receipt(await Done(waiting)); await Program.MustComplete(close);
                Check(confirmed.TaskId == original.TaskId && confirmed.Durability == "durable" && confirmed.Journal?.Sequence == 2 &&
                    f.Fold().Tasks.Single().Decisions.Count == 1 && f.Journal.Completion.IsCompleted && admitted.Kind == "pending",
                    "Close joins the one real creation; retry adds no record and the earlier pending snapshot never turns into a fabricated durable receipt");
            }
        }

        private static async Task ControlOnlyMutationReceipts()
        {
            Program.Stage = "Task mutation receipts require current Control without granting general Read access";
            using (var f = new Fixture())
            {
                await f.Grants.UpdateAsync("probe", GrantScope.Control, new string('b', 64), null);
                var subject = f.Subject; var goal = Goal(); var key = f.Key("control-only-create");
                var created = f.Ops.BeginCreateTask(key, goal, subject);
                var confirmed = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, subject))); string id = confirmed.TaskId;
                Check(Receipt(created).TaskId == id && confirmed.Durability == "durable" &&
                    f.Ops.BeginCreateTask(key, goal, subject).Kind == "found",
                    "a Control-only caller receives its own Begin and awaited creation receipts without an implicit Read scope");
                RejectRead(() => f.Ops.LookupTask(id, subject), "UNAUTHORIZED");
                RejectRead(() => f.Ops.LookupTaskDecision(key, subject), "UNAUTHORIZED");
                RejectRead(() => f.Ops.ReadTaskGoal(id, 1, TaskGoalSection.Title, 0, 128, subject), "UNAUTHORIZED");

                var revisionKey = f.Key("control-only-revise"); var revisionGoal = Goal("Control-only revision");
                var revision = f.Ops.BeginReviseTaskGoal(revisionKey, id, 1, 1, revisionGoal, subject);
                var revised = Receipt(await Done(f.Ops.ReviseTaskGoalAsync(revisionKey, id, 1, 1, revisionGoal, subject)));
                var stop = f.Ops.BeginStopTask(id, 1, subject);
                var stopped = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, subject)));
                Check(f.Ops.RefreshTaskStopReceipt(stopped.Decision.Key, subject).Kind == "found",
                    "a Control-only sender can refresh its own confirmed Stop receipt without being granted general Read");
                Check(Receipt(revision).Kind == "reviseGoal" && revised.GoalRevision == 2 && revised.Durability == "durable" &&
                    Receipt(stop).Kind == "stop" && stopped.ControlVersion == 2 && stopped.Durability == "durable" &&
                    f.Fold().Tasks.Single().Decisions.Count == 3,
                    "Control-only Begin revise and Stop retain exact receipts and their Async retries reuse the same three physical decisions");

                var unusedKey = f.Key("read-only-rejected");
                await f.Grants.UpdateAsync("probe", GrantScope.Read, new string('c', 64), null); var reader = f.Subject;
                long through = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                RejectRead(() => f.Ops.BeginCreateTask(unusedKey, goal, reader), "UNAUTHORIZED");
                RejectRead(() => f.Ops.BeginReviseTaskGoal(revisionKey, id, 1, 1, revisionGoal, reader), "UNAUTHORIZED");
                RejectRead(() => f.Ops.BeginStopTask(id, 1, reader), "UNAUTHORIZED");
                RejectRead(() => f.Ops.RefreshTaskStopReceipt(stopped.Decision.Key, reader), "UNAUTHORIZED");
                await Reject(() => f.Ops.CreateTaskAsync(key, goal, reader), "UNAUTHORIZED");
                Check(f.Ops.LookupTask(id, reader).IsStopped && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == through,
                    "Read-only callers cannot mutate or retrieve a mutation receipt through either admission path, but can read the existing facts");
            }
        }

        private static async Task GrantEpochAndProfileIncarnation()
        {
            Program.Stage = "current grant controls admission without transferring historical Task ownership";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                var old = f.Subject; var key = f.Key("accepted-before-revoke"); flush.Arm(1);
                Task<TaskDecisionLookupSnapshot> work = f.Ops.CreateTaskAsync(key, Goal(), old);
                await Program.MustComplete(flush.Entered.Task);
                string id = Receipt(f.Ops.LookupTaskDecision(key, old)).TaskId;
                await f.Grants.UpdateAsync("probe", GrantScope.Read, new string('b', 64), null);
                flush.Release.Set(); await Reject(() => work, "UNAUTHORIZED");
                Check(f.Subject.ProfileInstanceId == old.ProfileInstanceId && f.Subject.Epoch != old.Epoch &&
                    f.Ops.LookupTask(id, f.Subject).Durability == "durable" && f.Ops.LookupTaskDecision(key, f.Subject).Kind == "found" &&
                    f.Fold().Tasks.Single().Decisions.Count == 1,
                    "revocation refuses the old awaiting Control receipt without rolling back or duplicating the decision visible to a current Read grant");
                await Reject(() => f.Ops.StopTaskAsync(id, 1, old), "UNAUTHORIZED");
                await Reject(() => f.Ops.StopTaskAsync(id, 1, f.Subject), "UNAUTHORIZED");
                await f.Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('c', 64), null);
                var renewed = f.Subject;
                var stopped = await Done(f.Ops.StopTaskAsync(id, 1, renewed));
                Check(stopped.Kind == "found" && f.Ops.LookupTask(id, renewed).IsStopped,
                    "same stable profile incarnation may stop its Task using a new current control grant");
                string orphan = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("prior-incarnation"), Goal("Owned before removal"), renewed))).TaskId;
                await f.Grants.RemoveAsync("probe");
                await f.Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('d', 64), null);
                Check(f.Subject.ProfileInstanceId != renewed.ProfileInstanceId, "same-name recreation has a genuinely different owner incarnation");
                await Reject(() => f.Ops.StopTaskAsync(orphan, 1, f.Subject), "TASK_OWNER_MISMATCH");
                Check(!f.Fold().Tasks.Single(task => task.TaskId == orphan).IsStopped,
                    "removing and recreating a profile never inherits or fabricates control over its old Task");
            }
        }

        private static async Task ExhaustedOrdinaryAdmissionStillStops()
        {
            Program.Stage = "eight contexts with eight consumed requests cannot block protected Task Stop";
            using (var f = new Fixture())
            {
                string[] contexts = Enumerable.Range(0, 8).Select(_ => f.Ops.IssueWriteContext(f.Subject)).ToArray();
                var created = Receipt(await Done(f.Ops.CreateTaskAsync(new SubmissionKey(contexts[0], "quota-0"), Goal(), f.Subject)));
                string id = created.TaskId; long revision = 1;
                for (int request = 1; request < 64; request++)
                    await Done(f.Ops.ReviseTaskGoalAsync(new SubmissionKey(contexts[request / 8], "quota-" + request), id,
                        revision++, 1, Goal("Revision " + request), f.Subject));
                RejectRead(() => f.Ops.IssueWriteContext(f.Subject), "BUSY");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(new SubmissionKey(contexts[0], "ninth"), id, revision, 1, Goal(), f.Subject), "BUSY");
                f.Ready("city-b");
                var stopped = Receipt(await Done(f.Ops.StopTaskAsync(id, 1, f.Subject)));
                Check(stopped.Kind == "stop" && stopped.Decision.Source.CityId == "city-b" && f.Ops.LookupTask(id, f.Subject).IsStopped &&
                    f.Ops.LookupTask(id, f.Subject).GoalRevision == 64 && f.Fold().AllRecordsValid,
                    "Stop has its own stable identity and credit despite all ordinary request quotas and an obsolete Task world");
            }
        }

        private static async Task PendingRevisionAndStopShareFlush()
        {
            Program.Stage = "pending revision and Stop are truthful and share the original group Flush";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("task"), Goal(), f.Subject))).TaskId;
                int before = flush.ActualFlushes; flush.Arm(1);
                Task<OperationLookupSnapshot> blocker = f.Ops.SubmitAsync(f.Key("batch-blocker"), Target, f.Subject);
                await Program.MustComplete(flush.Entered.Task);
                var revisionKey = f.Key("pending-revision");
                var revisionGoal = Goal("Accepted revision");
                var admittedRevision = f.Ops.BeginReviseTaskGoal(revisionKey, id, 1, 1, revisionGoal, f.Subject);
                var admittedStop = f.Ops.BeginStopTask(id, 1, f.Subject);
                Task<TaskDecisionLookupSnapshot> revise = f.Ops.ReviseTaskGoalAsync(revisionKey, id, 1, 1, revisionGoal, f.Subject);
                Task<TaskDecisionLookupSnapshot> stop = f.Ops.StopTaskAsync(id, 1, f.Subject);
                var pending = f.Ops.LookupTask(id, f.Subject);
                Check(admittedRevision.Kind == "pending" && Receipt(admittedRevision).GoalRevision == 2 &&
                    admittedStop.Kind == "pending" && Receipt(admittedStop).ControlVersion == 2 &&
                    !revise.IsCompleted && !stop.IsCompleted && pending.GoalRevision == 1 && !pending.IsStopped && pending.StopRequested &&
                    pending.PendingOrdinary?.Kind == "reviseGoal" && pending.PendingStop?.Kind == "stop" &&
                    f.Ops.LookupTaskDecision(revisionKey, f.Subject).Kind == "pending",
                    "Begin returns both accepted decisions without waiting for blocked IO; Stop latches while Async retries still own the original Work");
                await Reject(() => f.Ops.ReviseTaskGoalAsync(f.Key("blocked-later"), id, 1, 1, Goal(), f.Subject), "TASK_STOPPED");
                flush.Release.Set(); await Done(blocker); var revised = Receipt(await Done(revise)); var stopped = Receipt(await Done(stop));
                var finished = f.Ops.LookupTask(id, f.Subject);
                Check(flush.ActualFlushes == before + 2 && revised.Journal!.Sequence + 1 == stopped.Journal!.Sequence &&
                    revised.Journal.StoreId == stopped.Journal.StoreId && finished.GoalRevision == 2 && finished.ControlVersion == 2 &&
                    finished.IsStopped && finished.PendingOrdinary == null && finished.PendingStop == null &&
                    f.Fold().Tasks.Single().Decisions.Count == 3 &&
                    f.Ops.BeginReviseTaskGoal(revisionKey, id, 1, 1, revisionGoal, f.Subject).Kind == "found" &&
                    f.Ops.BeginStopTask(id, 1, f.Subject).Kind == "found",
                    "already accepted revision then Stop commit in one real mixed batch without canceling or reordering their decisions");
            }
        }

        private static async Task UnknownFlushAndCloseJoin()
        {
            Program.Stage = "unknown Task IO preserves latch without inventing a durable Stop";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("task"), Goal(), f.Subject))).TaskId;
                flush.Arm(1, failAfterFlush: true); flush.Release.Set();
                var unknown = await Done(f.Ops.StopTaskAsync(id, 1, f.Subject)); var state = f.Ops.LookupTask(id, f.Subject);
                var listed = f.Ops.LookupTasks(f.Subject).Single();
                Check(unknown.Kind == "unavailable" && Receipt(unknown).Durability == "unavailable" && state.StopRequested && !state.IsStopped &&
                    !f.Journal.IsHealthy && listed.TaskId == id && listed.Durability == "unavailable" &&
                    listed.StopRequested && !listed.IsStopped && listed.PendingStop?.Durability == "unavailable",
                    "lost real Flush acknowledgement keeps the local safety latch and persistence gap visible in single and list summaries");
                var compactUnknown = TaskStopQueryService.Compact(new Contracts.TaskStopSelector { TaskId = id, ExpectedControlVersion = 1 }, unknown);
                Check((string?)compactUnknown["lookup"]!["kind"] == "unavailable" &&
                    (bool?)compactUnknown["lookup"]!["receipt"]!["stopRequested"] == true &&
                    compactUnknown["lookup"]!["receipt"]!["journal"]!.Type == JTokenType.Null &&
                    (bool?)compactUnknown["lookup"]!["receipt"]!["persistenceAttempted"] == true,
                    "unknown Flush maps its actual attempted Stop without fabricating a confirmed journal position");
                var liveUnknown = TaskStopQueryService.Execute(f.Ops,
                    new JObject { ["taskId"] = id, ["expectedControlVersion"] = 1 }, f.Subject, f.Context.Read().InstanceId, 1000);
                Check((string?)liveUnknown["result"]!["lookup"]!["kind"] == "unavailable" &&
                    (bool?)liveUnknown["result"]!["lookup"]!["receipt"]!["persistenceAttempted"] == true &&
                    liveUnknown["result"]!["lookup"]!["receipt"]!["journal"]!.Type == JTokenType.Null,
                    "full live lookup also preserves the real lost-ACK gap without promoting it to a durable decision");
                int calls = flush.ActualFlushes;
                Check(f.Ops.BeginStopTask(id, 1, f.Subject).Kind == "unavailable",
                    "Begin retry retains the accepted unknown Stop instead of inventing a fresh pending or durable decision");
                await Done(f.Ops.StopTaskAsync(id, 1, f.Subject));
                Check(flush.ActualFlushes == calls, "retry of an unknown Stop never creates another physical journal write");
                await f.Reopen();
                Check(f.Ops.LookupTask(id, f.Subject).IsStopped && f.Ops.LookupTask(id, f.Subject).Durability == "durable",
                    "new-owner actual recovery confirms the Stop bytes that were present without restarting any Task execution");
            }
            Program.Stage = "unknown ordinary revision cannot masquerade as an absent goal page";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("task"), Goal(), f.Subject))).TaskId;
                var next = Goal("Revision present after unknown Flush"); flush.Arm(1, failAfterFlush: true); flush.Release.Set();
                var unknown = await Done(f.Ops.ReviseTaskGoalAsync(f.Key("unknown-revision"), id, 1, 1, next, f.Subject));
                var page = f.Ops.ReadTaskGoal(id, 2, TaskGoalSection.Title, 0, 128, f.Subject);
                Check(unknown.Kind == "unavailable" && Receipt(unknown).Durability == "unavailable" &&
                    !f.Ops.LookupTask(id, f.Subject).StopRequested && page.Kind == "unavailable" && page.Journal == null && page.Text == null,
                    "lost ordinary ACK preserves an unknown revision page without inventing absence, content, position or a Stop latch");
                await f.Reopen(); page = f.Ops.ReadTaskGoal(id, 2, TaskGoalSection.Title, 0, 128, f.Subject);
                Check(page.Kind == "found" && page.Text == next.Title && page.Durability == "durable" && page.Journal != null &&
                    f.Ops.LookupTask(id, f.Subject).GoalRevision == 2 && !f.Ops.LookupTask(id, f.Subject).StopRequested,
                    "a new owner confirms the actual second revision and does not recover an unrecorded Stop");
            }
            Program.Stage = "Task authority Close joins both metadata Flush and the genuine owned Native Task";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("task"), Goal(), f.Subject))).TaskId;
                string op = (await Done(f.Ops.SubmitAsync(f.Key("native"), Target, f.Subject))).OperationId!;
                await f.Ops.PersistCommitIntentAsync(op);
                var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    Check(f.Ops.TryCommitAtSafePoint(op, new NativeLoadGate(f.Context.Read().InstanceId,
                        _ => NativeLoadInvocation.Started(native.Task, Target))), "fixture owns one actual synthetic Native Task");
                    flush.Arm(1); var admitted = f.Ops.BeginStopTask(id, 1, f.Subject);
                    var stop = f.Ops.StopTaskAsync(id, 1, f.Subject); await Program.MustComplete(flush.Entered.Task);
                    Task close = f.Ops.CloseAsync();
                    Check(admitted.Kind == "pending" && !stop.IsCompleted && !close.IsCompleted && !native.Task.IsCompleted,
                        "closing does not cancel the pending Flush or declare the still-running Native Task physically complete");
                    flush.Release.Set(); await Done(stop);
                    Check(!close.IsCompleted && !native.Task.IsCompleted, "finishing metadata alone cannot complete Close while Native work remains owned");
                    native.TrySetResult(false); await Program.MustComplete(close);
                    var facts = f.Fold().Operations.Single();
                    Check(f.Journal.Completion.IsCompleted && facts.Native!.LoadReturnValue == false && facts.Final!.Load.LoadReturnValue == false,
                        "Close returns only after Native completion and its real final journal evidence are also settled");
                }
                finally { flush.Release.Set(); native.TrySetResult(false); }
            }
        }

        private static async Task RecoveryFlushDoesNotPublishDurableTask()
        {
            Program.Stage = "readable recovered Task facts do not imply a confirmed recovery Flush";
            using (var f = new Fixture())
            {
                var key = f.Key("recovery-task"); var goal = Goal("Original readable goal");
                string id = Receipt(await Done(f.Ops.CreateTaskAsync(key, goal, f.Subject))).TaskId;
                using (var recovery = new ControlledFlush())
                {
                    recovery.Arm(1, failAfterFlush: true); recovery.Release.Set();
                    await f.Reopen(recoveryDurability: recovery);
                    var summary = f.Ops.LookupTask(id, f.Subject); var decision = f.Ops.LookupTaskDecision(key, f.Subject);
                    var listed = f.Ops.LookupTasks(f.Subject).Single();
                    Check(recovery.ActualFlushes == 1 && f.Journal.Recovery.Complete && !f.Journal.Recovery.DurableConfirmed && !f.Journal.IsHealthy,
                        "reopening reads the complete original source but loses confirmation after one actual recovery Flush");
                    Check(summary.Title == goal.Title && summary.GoalRevision == 1 && !summary.IsStopped &&
                        summary.Durability == "unavailable" && summary.Journal == null && decision.Kind == "unavailable" &&
                        decision.Receipt != null && decision.Receipt.Durability == "unavailable" && decision.Receipt.Journal == null &&
                        listed.TaskId == id && listed.Title == goal.Title && listed.Durability == "unavailable" && listed.Journal == null,
                        "single, decision and complete-list reads retain the readable goal without inventing a durable current position or Stop");
                    await Reject(() => f.Ops.ReviseTaskGoalAsync(new SubmissionKey("not-issued", "no-new-write"), id, 1, 1,
                        Goal("Must not append"), f.Subject), "JOURNAL_UNAVAILABLE");
                    Check(recovery.ActualFlushes == 1 && f.Journal.QueuedOrWriting == 0,
                        "reading restored Task facts and rejecting new mutation never add a recovery write or retry");
                }
            }
        }

        private static async Task RetentionAndRecoveryObligations()
        {
            Program.Stage = "Task retirement respects live contexts, unstopped Tasks and unknown operation fences";
            using (var f = new Fixture())
            {
                var key = f.Key("stopped-context-pin"); string id = Receipt(await Done(f.Ops.CreateTaskAsync(key, Goal(), f.Subject))).TaskId;
                var stopped = await Done(f.Ops.StopTaskAsync(id, 1, f.Subject));
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.NoEligible && f.Ops.LookupTask(id, f.Subject).Kind == "found",
                    "a stopped Task still keeps its full decision chain while its ordinary request context remains admissible");
                f.Now = 60001; Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed,
                    "an expired stopped Task can retire its whole chain through the original single writer");
                Check(f.Ops.LookupTask(id, f.Subject).Reason == "RETENTION_NOT_COVERED" &&
                    f.Ops.LookupTaskDecision(key, f.Subject).Reason == "RETENTION_NOT_COVERED",
                    "retired Task ID and decision key report uncovered history rather than false absence");
                var late = TaskStopQueryService.Compact(new Contracts.TaskStopSelector { TaskId = id, ExpectedControlVersion = 1 },
                    f.Ops.RefreshTaskStopReceipt(Receipt(stopped).Decision.Key, f.Subject));
                Check((string?)late["lookup"]!["kind"] == "unavailable" &&
                    (string?)late["lookup"]!["reason"] == "RETENTION_NOT_COVERED" &&
                    late["lookup"]!["receipt"]!.Type == JTokenType.Null,
                    "sender refresh after real retirement drops stale confirmed data and reports uncovered original history");
            }
            using (var f = new Fixture())
            {
                string live = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("live", 1), Goal(), f.Subject))).TaskId;
                await f.SeedUnfinishedOperation("fenced"); f.Host.State = NativeHostState.Alive;
                await f.Reopen(sealRecovery: false);
                Check(!f.Ops.IsRecoveryReady && f.Journal.RecoveryReservedTerminalCount == 1 && f.Ops.HasNativeFence,
                    "startup actually holds the original missing-final obligation and recovered Native fence");
                await Done(f.Ops.StopTaskAsync(live, 1, f.Subject));
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                f.Now = 2;
                Check(await f.Ops.TryRetireAsync() == OperationRetentionOutcome.Installed && f.Ops.HasNativeFence &&
                    f.Ops.Lookup("fenced", f.Subject).State == "unknown" && f.Ops.Lookup("fenced", f.Subject).MayHaveApplied &&
                    f.Ops.Lookup("fenced", f.Subject).Load!.LoadReturnValue == null,
                    "boot Stop bypasses ordinary recovery gating while retirement preserves the unrelated complete unknown operation obligation");
            }
        }

        private static async Task LateRotationContinuationCannotUndoStop()
        {
            Program.Stage = "late retirement continuation cannot replace a newer acknowledged Stop";
            using (var f = new Fixture(delayRotation: true))
            {
                string live = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("live", 1), Goal(), f.Subject))).TaskId;
                string dead = Receipt(await Done(f.Ops.CreateTaskAsync(f.Key("dead", 1), Goal(), f.Subject))).TaskId;
                await Done(f.Ops.StopTaskAsync(dead, 1, f.Subject)); f.Now = 2;
                f.Rotation!.DelayNext = true; var retirement = f.Ops.TryRetireAsync();
                JournalRotationResult physical = await Done(f.Rotation.PhysicallyInstalled.Task);
                var stopped = Receipt(await Done(f.Ops.StopTaskAsync(live, 1, f.Subject)));
                string store = ((StoreOpened)physical.Prefix.Records[0].Entry).StoreId;
                Check(!retirement.IsCompleted && stopped.Journal!.StoreId == store && stopped.Journal.Sequence == physical.Prefix.ThroughSequence + 1 &&
                    f.Ops.LookupTask(live, f.Subject).IsStopped,
                    "real rotation is installed and a later actual Stop is acknowledged before the Authority receives the old rotation result");
                f.Rotation.Release.TrySetResult(true); Check(await Done(retirement) == OperationRetentionOutcome.Installed,
                    "the delayed original maintenance result still completes as the single installed retirement");
                var current = f.Ops.LookupTask(live, f.Subject);
                var decision = Receipt(f.Ops.LookupTaskDecision(TaskCommandHash.StopKey(live, 1), f.Subject));
                Check(current.IsStopped && current.ControlVersion == 2 && current.Journal!.StoreId == store &&
                    current.Journal.Sequence == stopped.Journal!.Sequence && decision.Journal!.Sequence == stopped.Journal.Sequence &&
                    f.Ops.LookupTask(dead, f.Subject).Reason == "RETENTION_NOT_COVERED",
                    "late installation merges the current prefix monotonically and cannot roll back Task state or its actual Stop receipt");
            }
        }

        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            private int calls, actual, pauseAt; private bool fail;
            internal int ActualFlushes => Volatile.Read(ref actual);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Arm(int relativeCall, bool failAfterFlush = false) { fail = failAfterFlush; Volatile.Write(ref pauseAt, Volatile.Read(ref calls) + relativeCall); }
            public void Flush(FileStream file)
            {
                bool selected = Interlocked.Increment(ref calls) == Volatile.Read(ref pauseAt);
                if (selected) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Task authority fixture Flush deadline."); }
                file.Flush(true); Interlocked.Increment(ref actual);
                if (selected && fail) throw new IOException("Synthetic Task authority acknowledgement loss after actual Flush.");
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
        private sealed class HostProof : INativeHostIdentitySource
        {
            public NativeHostIdentity Current { get; } = new NativeHostIdentity(101, 123456);
            internal NativeHostState State = NativeHostState.Ended;
            public NativeHostState Observe(NativeHostIdentity identity)
            { if (!Current.Equals(identity)) throw new InvalidOperationException("Task fixture observed a different Native host."); return State; }
        }
        private sealed class ObservingDurability : IJournalDurability
        {
            private readonly IJournalDurability inner;
            internal ObservingDurability(IPrivateStorageBoundary boundary, IJournalDurability? inner)
            { this.inner = inner ?? new FileJournalDurability(boundary); }
            internal FileStream? Current { get; private set; }
            public void Flush(FileStream file) { Current = file; inner.Flush(file); }
        }
        private sealed class DelayedRotationJournal : IOperationJournal, ITaskJournal
        {
            private readonly OperationJournal inner;
            internal DelayedRotationJournal(OperationJournal inner) { this.inner = inner; }
            internal bool DelayNext;
            internal readonly TaskCompletionSource<JournalRotationResult> PhysicallyInstalled = new TaskCompletionSource<JournalRotationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public JournalRecovery Recovery => inner.Recovery;
            public bool IsHealthy => inner.IsHealthy;
            public Task Completion => inner.Completion;
            public bool SupportsTaskMetadata => inner.SupportsTaskMetadata;
            public ConfirmedJournalPrefix? CaptureConfirmedPrefix() => inner.CaptureConfirmedPrefix();
            public JournalRotationPosition? CaptureRotationPosition() => inner.CaptureRotationPosition();
            public IJournalRotation? TryStartRotation(JournalRotationPlan plan)
            {
                var rotation = inner.TryStartRotation(plan);
                if (rotation == null || !DelayNext) return rotation;
                DelayNext = false; return new HeldRotation(this, rotation);
            }
            public IDisposable ObserveConfirmed(Action notification) => inner.ObserveConfirmed(notification);
            public IOperationJournalReservation? TryReserveOperation() => inner.TryReserveOperation();
            public IRecoveryTerminalReservation? TryReserveRecoveryTerminals(long through, int count) => inner.TryReserveRecoveryTerminals(through, count);
            public ITaskJournalReservation? TryReserveTaskCreation(string id) => inner.TryReserveTaskCreation(id);
            public ITaskJournalReservation? TryReserveTaskRevision(string id) => inner.TryReserveTaskRevision(id);
            public ITaskJournalReservation? TryReserveTaskStop(string id) => inner.TryReserveTaskStop(id);
            public Task StopAsync() => inner.StopAsync();
            private sealed class HeldRotation : IJournalRotation
            {
                private readonly IJournalRotation inner;
                internal HeldRotation(DelayedRotationJournal owner, IJournalRotation inner) { this.inner = inner; Completion = Hold(owner, inner); }
                private static async Task<JournalRotationResult> Hold(DelayedRotationJournal owner, IJournalRotation inner)
                { var result = await inner.Completion.ConfigureAwait(false); owner.PhysicallyInstalled.TrySetResult(result); await owner.Release.Task.ConfigureAwait(false); return result; }
                public Task<JournalRotationResult> Completion { get; }
                public void RequestStop() => inner.RequestStop();
            }
        }
        private sealed class FrozenHistory : IHistoryProjectionSource
        {
            internal HistoryProjection? Value;
            public HistoryProjection? Current => Value;
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "task-authority-" + Guid.NewGuid().ToString("N"));
            private readonly IPrivateStorageBoundary boundary;
            private readonly EnvironmentLease lease;
            private readonly ControlledFlush? flush;
            private readonly bool mixed;
            private readonly IHistoryProjectionSource? history;
            private ObservingDurability observing;
            private int keyCounter;
            internal long Now;
            internal readonly HostProof Host = new HostProof();
            internal OperationJournal Journal { get; private set; }
            internal OperationAuthority Ops { get; private set; }
            internal WorldContextTracker Context { get; private set; }
            internal GrantAuthority Grants { get; }
            internal GrantSnapshot Subject => Grants.Read("probe")!;
            internal DelayedRotationJournal? Rotation { get; }
            internal Fixture(ControlledFlush? flush = null, bool delayRotation = false, bool mixed = true, IHistoryProjectionSource? history = null)
            {
                this.flush = flush; this.mixed = mixed; this.history = history; Directory.CreateDirectory(root); boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                var authorization = new AtomicAuthorizationStore(boundary, lease, EnvironmentId); authorization.InitializeEmpty();
                var activated = authorization.Load().Activate(); authorization.Save(1, activated); Grants = new GrantAuthority(activated, authorization);
                Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('a', 64), null).GetAwaiter().GetResult();
                observing = new ObservingDurability(boundary, flush);
                Journal = mixed ? OperationJournal.OpenMixedForAuthority(boundary, lease, EnvironmentId, true, observing)
                    : OperationJournal.OpenForAuthority(boundary, lease, EnvironmentId, true, observing);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N")); Ready("city-a");
                if (delayRotation) Rotation = new DelayedRotationJournal(Journal);
                Ops = new OperationAuthority((IOperationJournal?)Rotation ?? Journal, Grants, Context, Host, () => Now, history);
            }
            internal void Ready(string city) { long generation = Context.EnterLoading(); if (!Context.ObserveReady(generation, city)) throw new InvalidOperationException("Task fixture ready transition failed."); }
            internal SubmissionKey Key(string request, int lifetime = 60000) => new SubmissionKey(Ops.IssueWriteContext(Subject, lifetime), request + "-" + ++keyCounter);
            internal JournalFactsFoldResult Fold() => mixed ? JournalFactsFold.FoldMixed(Journal.CaptureConfirmedPrefix()!.Records)
                : JournalFactsFold.Fold(Journal.CaptureConfirmedPrefix()!.Records);
            internal byte[] ReadJournalBytes()
            {
                if (Journal.QueuedOrWriting != 0) throw new InvalidOperationException("Task authority snapshot requires physically quiescent IO.");
                var owned = observing.Current;
                if (owned != null && owned.CanRead && owned.CanSeek)
                {
                    long position = owned.Position;
                    try
                    {
                        if (owned.Length > JournalLayout.Mixed.FileBytes) throw new InvalidDataException("Task authority snapshot exceeds its fixed container.");
                        var bytes = new byte[checked((int)owned.Length)]; owned.Position = 0;
                        JournalBankFormat.ReadExactly(owned, bytes, 0, bytes.Length); return bytes;
                    }
                    finally { owned.Position = position; }
                }
                if (!Journal.Completion.IsCompleted) throw new InvalidOperationException("Task authority snapshot has no observed stream before owner close.");
                return File.ReadAllBytes(Path.Combine(boundary.DirectoryPath, "operations.journal"));
            }
            internal void DamageJournalTail()
            {
                if (!mixed) throw new InvalidOperationException("This fault targets the actual Mixed bank tail.");
                using (var file = new FileStream(Path.Combine(boundary.DirectoryPath, "operations.journal"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    var current = JournalBankReader.ReadMixed(file, EnvironmentId).Current;
                    file.Position = JournalLayout.Mixed.BankStart(current.Index) + JournalBankFormat.BodyOffset + current.ThroughBytes;
                    file.WriteByte(0x53); file.Flush(true);
                }
            }
            internal async Task<string> Complete(SubmissionKey key)
            {
                string id = (await Done(Ops.SubmitAsync(key, Target, Subject))).OperationId!; await Ops.PersistCommitIntentAsync(id);
                if (!Ops.TryCommitAtSafePoint(id, new NativeLoadGate(Context.Read().InstanceId,
                    _ => NativeLoadInvocation.Started(Task.FromResult(false), Target)))) throw new InvalidOperationException("Task fixture operation did not commit.");
                await Program.MustComplete(Ops.DrainAsync(id)); return id;
            }
            internal async Task SeedUnfinishedOperation(string id)
            {
                var source = Context.Read(); var subject = Subject;
                var intent = new OperationIntent(id, source.InstanceId, new SubmissionKey("original-context", id), OperationCommandHash.Compute(source, Target),
                    subject.ProfileId, subject.AuthorityEpoch, subject.Epoch, source, Target, subject.ProfileInstanceId);
                using (var reservation = Journal.TryReserveOperation()!)
                {
                    await Done(reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal));
                    await Done(reservation.AppendAsync(new CommitIntended(id, Target, Host.Current), JournalSlot.Normal));
                }
            }
            internal async Task Reopen(bool sealRecovery = true, IJournalDurability? recoveryDurability = null)
            {
                if (Rotation != null) throw new InvalidOperationException("Task fixture does not reopen a held rotation wrapper.");
                await Program.MustComplete(Ops.CloseAsync());
                observing = new ObservingDurability(boundary, recoveryDurability);
                Journal = mixed ? OperationJournal.OpenMixedForAuthority(boundary, lease, EnvironmentId, false, observing)
                    : OperationJournal.OpenForAuthority(boundary, lease, EnvironmentId, false, observing);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N")); Ready("city-a");
                Ops = new OperationAuthority(Journal, Grants, Context, Host, () => Now, history);
                if (sealRecovery) await Program.MustComplete(Ops.SealRecoveryAsync());
            }
            public void Dispose()
            {
                flush?.Release.Set(); Rotation?.Release.TrySetResult(true);
                Ops.CloseAsync().GetAwaiter().GetResult(); Grants.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(root).StartsWith("task-authority-", StringComparison.Ordinal)) throw new IOException("Task authority fixture containment invalid.");
                foreach (string directory in new[] { root, boundary.DirectoryPath })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Task authority fixture refuses directory links.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Task authority fixture refuses file links."); File.Delete(path); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
