using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Application.Modules;
using SkylinesLink.Application.Observation;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    internal static class StopPipeTests
    {
        private static int checks;
        private static void Check(bool condition, string name) { Program.Assert(condition, name); checks++; }
        internal static async Task<int> RunAsync()
        {
            CheckReplyOwnership();
            await CheckFrozenFramesAndDirectionalCapacity();
            await CheckUnavailableAndScopes();
            await CheckPreflightRejections();
            await CheckSlowFlush();
            await CheckStoppedReaderAndQueuedRevocation();
            await CheckDuplicateAndOriginalReceipt();
            await CheckDeferredReadsDoNotBlockStop();
            await CheckDeferredCloseAndRevocation();
            await CheckDeferredAbsoluteDeadline();
            await CheckModuleObservationPort();
            await CheckCityOverviewPort();
            return checks;
        }

        private static async Task CheckCityOverviewPort()
        {
            Program.Stage = "city overview uses the authenticated bounded deferred port";
            using (var f = new Fixture(withCityOverview: true))
            using (var client = await f.Connect())
            {
                await Send(client, f.Limits, "city-read", "city.overview", new JObject());
                CityOverviewReadBroker.CaptureWork? work = null;
                await WaitUntil(() => f.CityOverview!.TryClaim(out work));
                await Send(client, f.Limits, "city-context", "system.context", new JObject());
                Check((string?)(await Read(client, f.Limits, "system.context"))["requestId"] == "city-context",
                    "a pending native city read does not block another ordinary read");
                var facts = new CityOverviewCapture(work!.Scope, "test-game", 10, 11, 0, 0, 0, -7, false,
                    SimulationSpeedFact.Observed(0, 0), GameClockFact.Unavailable("COMPONENT_UNAVAILABLE"));
                f.CityOverview!.Complete(work, facts);
                var result = (await Read(client, f.Limits, "city.overview"))["result"]!;
                Check((string?)result["scope"]?["instanceId"] == f.Identity.InstanceId &&
                    (int?)result["population"]?["value"] == 0 && (int?)result["treasury"]?["value"] == -7 &&
                    result["population"]?["sourceUpdatedFrame"]?.Type == JTokenType.Null,
                    "the real pipe preserves legitimate zero, negative money and unknown source age");
                await f.CityOverview.WhenIdle;

                // The same ordinary-value guard is called by the real sender
                // immediately before freezing a deferred result into its frame.
                using (var guarded = new DeferredCapabilityRead(Task.FromResult(result), new NoReadConsumer(), f.CityOverview.GetCurrentResult))
                {
                    f.Context.EnterMenu();
                    Check((string?)guarded.GetCurrentResult()["reason"] == "SCOPE_CHANGED",
                        "a completed read is revalidated at result acquisition after changing city scope");
                }
                await Send(client, f.Limits, "city-menu", "city.overview", new JObject());
                Check((string?)(await Read(client, f.Limits, "city.overview"))["result"]?["reason"] == "MENU",
                    "menu reads report unavailable without waiting for or loading a city");
            }
        }

        private sealed class NoReadConsumer : IDisposable { public void Dispose() { } }

        private static async Task CheckModuleObservationPort()
        {
            Program.Stage = "registered module observation through the authenticated named pipe";
            using (var f = new Fixture(withModules: true))
            {
                string task = (await f.CreateTasks(1))[0];
                using (var client = await f.Connect())
                {
                    await Send(client, f.Limits, "modules-capture", "system.modules.get", new JObject { ["kind"] = "capture" });
                    await WaitUntil(() => f.Modules!.ReadUsage().ActiveCaptureCount == 1);
                    await Send(client, f.Limits, "modules-stop", "tasks.stop", StopArguments(task));
                    Check((string?)(await Read(client, f.Limits, "tasks.stop"))["requestId"] == "modules-stop" &&
                        f.Ops.LookupTask(task, f.Observer).StopRequested,
                        "a real module capture consumer cannot block protected Stop admission or delivery");
                    Check(f.Modules!.TryClaim(out var capture), "only the native-facing source port can claim the queued capture");
                    f.Modules.Complete(capture!, ModuleRuntimeObservationTests.Facts(20));
                    var response = await Read(client, f.Limits, "system.modules.get");
                    var result = response["result"]!; string snapshot = (string)result["snapshot"]!["snapshotId"]!;
                    Check((string?)response["requestId"] == "modules-capture" && (int)result["returnedCount"]! == 16 &&
                        (string?)result["snapshot"]?["instanceId"] == f.Identity.InstanceId,
                        "the registered output returns the original read identity and exact source instance");
                    await Send(client, f.Limits, "modules-page", "system.modules.get", new JObject { ["kind"] = "page", ["snapshotId"] = snapshot, ["offset"] = 16 });
                    var page = (await Read(client, f.Limits, "system.modules.get"))["result"]!;
                    Check((int)page["returnedCount"]! == 4 && (string?)page["snapshot"]?["snapshotId"] == snapshot,
                        "later pipe pages use the same immutable observation rather than another native enumeration");
                }
                await f.WaitDisconnected();
            }
            using (var f = new Fixture())
            using (var client = await f.Connect())
            {
                await Send(client, f.Limits, "no-module-source", "system.modules.get", new JObject { ["kind"] = "capture" });
                Check((string?)(await Read(client, f.Limits, "system.modules.get"))["result"]?["reason"] == "SOURCE_UNAVAILABLE",
                    "an unconfigured composition never fabricates an empty installed-module catalog");
            }
        }

        private static async Task CheckDeferredReadsDoNotBlockStop()
        {
            Program.Stage = "pending native read cannot occupy receiver or serial sender";
            using (var f = new Fixture(deferOrdinary: true))
            {
                string task = (await f.CreateTasks(1))[0];
                using (var client = await f.Connect())
                {
                    await Send(client, f.Limits, "waiting-read", "system.context", new JObject());
                    await WaitUntil(() => f.Dispatcher.PendingCount == 1);
                    await Send(client, f.Limits, "ready-read", "operations.get", OperationArguments(), "5");
                    var ready = await ReadAny(client, f.Limits);
                    Check((string?)ready["requestId"] == "ready-read" && (string?)ready["kind"] == "result",
                        "a later ready ordinary read bypasses one pending main-thread observation");
                    await Send(client, f.Limits, "ready-stop", "tasks.stop", StopArguments(task));
                    var stopped = await Read(client, f.Limits, "tasks.stop");
                    Check((string?)stopped["requestId"] == "ready-stop" && f.Ops.LookupTask(task, f.Observer).StopRequested &&
                        f.Server.ReplyUsage.OrdinaryCount == 1 && f.Dispatcher.ConsumerReleases == 0,
                        "Stop is accepted and delivered while the earlier read still owns its ordinary credit and consumer");
                    f.Dispatcher.CompleteRead(0);
                    var observed = await Read(client, f.Limits, "system.context");
                    Check((string?)observed["requestId"] == "waiting-read" && (string?)observed["kind"] == "result",
                        "completion wakes the same bounded sender and returns the original read identity");
                    await WaitUntil(() => f.Dispatcher.ConsumerReleases == 1 && f.Server.ReplyUsage.OrdinaryCount == 0);
                }
                await f.WaitDisconnected();
            }
        }

        private static async Task CheckDeferredCloseAndRevocation()
        {
            foreach (bool revoke in new[] { false, true })
            using (var f = new Fixture(deferOrdinary: true))
            {
                Program.Stage = revoke ? "revoke pending read before first output byte" : "close eight pending read consumers";
                string? task = revoke ? null : (await f.CreateTasks(1))[0];
                using (var client = await f.Connect())
                {
                    for (int i = 0; i < 8; i++)
                        await Send(client, f.Limits, "waiting-" + i, "system.context", new JObject());
                    await WaitUntil(() => f.Dispatcher.PendingCount == 8);
                    Check(f.Server.ReplyUsage.OrdinaryCount == 8 && f.Dispatcher.ConsumerReleases == 0,
                        "eight deferred consumers retain the actual global ordinary limit before producing any output");
                    if (revoke)
                    {
                        await f.Grants.UpdateAsync("probe", GrantScope.Control, f.Token, null);
                        f.Dispatcher.CompleteRead(0);
                        var denied = await ReadAny(client, f.Limits);
                        Check((string?)denied["kind"] == "error" && (string?)denied["code"] == "UNAUTHORIZED",
                            "revoked Read never serializes a completed native observation");
                    }
                    else
                    {
                        await Send(client, f.Limits, "full-read-stop", "tasks.stop", StopArguments(task!));
                        var stopped = await Read(client, f.Limits, "tasks.stop");
                        Check((string?)stopped["requestId"] == "full-read-stop" && f.Server.ReplyUsage.OrdinaryCount == 8 &&
                            f.Dispatcher.ConsumerReleases == 0 && f.Ops.LookupTask(task!, f.Observer).StopRequested,
                            "eight unready observations leave the protected Stop receiver and sender usable");
                        client.Dispose();
                    }
                    await f.WaitDisconnected();
                    Check(f.Dispatcher.ConsumerReleases == 8 && f.Server.ReplyUsage.OrdinaryCount == 0,
                        "real connection close cancels each pending consumer and releases each reply once");
                    Check(!f.Dispatcher.CompleteRead(7),
                        "a late source completion cannot revive an abandoned consumer or touch the disposed queue signal");
                }
            }
        }

        private static async Task CheckDeferredAbsoluteDeadline()
        {
            Program.Stage = "ordinary read deadline cannot be renewed by valid subsequent input";
            using (var f = new Fixture(deferOrdinary: true, frameMilliseconds: 1500))
            using (var client = await f.Connect())
            {
                await Send(client, f.Limits, "deadline-read", "system.context", new JObject());
                await WaitUntil(() => f.Dispatcher.PendingCount == 1);
                await Task.Delay(900);
                await Send(client, f.Limits, "keep-alive", "operations.get", OperationArguments(), "5");
                var ready = await ReadAny(client, f.Limits);
                Check((string?)ready["requestId"] == "keep-alive", "valid ordinary activity leaves the first read pending");
                var expired = await ReadAny(client, f.Limits);
                Check((string?)expired["requestId"] == "deadline-read" && (string?)expired["code"] == "READ_TIMEOUT" &&
                    f.Dispatcher.ConsumerReleases == 1,
                    "admission-based timeout cancels the consumer while the connection's newer frame deadline is still open");
                Check(!f.Dispatcher.CompleteRead(0), "timeout cannot later emit a second successful observation");
                await Send(client, f.Limits, "after-timeout", "operations.get", OperationArguments(), "5");
                Check((string?)(await ReadAny(client, f.Limits))["requestId"] == "after-timeout",
                    "one unavailable observation does not close healthy reads on the connection");
                await Send(client, f.Limits, "source-failure", "system.context", new JObject());
                await WaitUntil(() => f.Dispatcher.PendingCount == 2);
                f.Dispatcher.FailRead(1);
                var failed = await ReadAny(client, f.Limits);
                Check((string?)failed["requestId"] == "source-failure" && (string?)failed["code"] == "OBSERVATION_CHANGED",
                    "a deferred source failure retains the fixed public reason instead of closing the entire connection");
                await WaitUntil(() => f.Dispatcher.ConsumerReleases == 2 && f.Server.ReplyUsage.OrdinaryCount == 0);
            }
        }

        private static void CheckReplyOwnership()
        {
            Program.Stage = "Stop pipe global reply responsibility and immutable frame";
            var budget = new PipeReplyBudget();
            var ordinary = new List<PipeReplyBudget.ReplyCredit>();
            var control = new List<PipeReplyBudget.ReplyCredit>();
            try
            {
                for (int i = 0; i < 8; i++) ordinary.Add(budget.TryOrdinary()!);
                Check(ordinary.All(x => x != null) && budget.TryOrdinary() == null, "eight ordinary replies hold the server-wide ordinary limit");
                for (int i = 0; i < 8; i++) control.Add(budget.TryControl("task-" + i, 1)!);
                Check(control.All(x => x != null) && budget.Read().ControlCount == 8,
                    "ordinary reply saturation leaves eight different protected Stop identities available");
                Check(budget.TryControl("task-0", 1) == null && budget.TryControl("task-extra", 1) == null,
                    "a duplicate identity has no follower and a ninth active control RPC has no credit");
                var frame = FrameCodec.Freeze(new JObject { ["value"] = "frozen" }, Program.Limits());
                ordinary[0].OwnText(frame);
                Check(budget.Read().OwnedTextCharacters == frame.Json.Length && budget.Read().OwnedTextUtf8Bytes == frame.PayloadBytes,
                    "queued immutable text has a separate logical ownership metric from raw payload bytes");
                ordinary[0].Dispose(); ordinary[0].Dispose();
                Check(budget.Read().OrdinaryCount == 7 && budget.Read().OwnedTextCharacters == 0,
                    "one completed reply releases its own credit and text once");
            }
            finally { foreach (var credit in ordinary) credit?.Dispose(); foreach (var credit in control) credit?.Dispose(); }
            Check(budget.Read().OrdinaryCount == 0 && budget.Read().ControlCount == 0 && budget.Read().OwnedTextUtf8Bytes == 0,
                "all explicit reply owners actually finish without retained followers");
        }

        private static async Task CheckFrozenFramesAndDirectionalCapacity()
        {
            Program.Stage = "Stop pipe separate ingress and egress raw capacity";
            var limits = Program.Limits(5000);
            var source = new JObject { ["value"] = "original" };
            var frozen = FrameCodec.Freeze(source, limits); source["value"] = "mutated";
            using (var wire = new MemoryStream())
            {
                await FrameCodec.WriteFrozenAsync(wire, frozen, limits, CancellationToken.None);
                wire.Position = 0;
                Check((string?)ContractJson.Parse((await FrameCodec.ReadAsync(wire, limits, limits.FrameTimeout, CancellationToken.None))!)["value"] == "original",
                    "a queued frame uses its frozen serialized value rather than the caller DOM");
            }

            var ingress = new CapacityBudget(4, 4 * 65536);
            var egress = new CapacityBudget(4, 4 * 65536);
            var writes = new GateWriteStream[4]; var reads = new GateReadStream[4];
            var send = new Task[4]; var receive = new Task[4];
            try
            {
                // 这是受控Stream的容量/真实结束反例，不冒充真实Mono或任意公开工具的最大响应。
                var maximum = FrameCodec.Freeze(new JValue(new string('x', 65534)), limits);
                Check(maximum.PayloadBytes == 65536, "raw capacity fixture has an exact legal 64KiB frame");
                for (int i = 0; i < 4; i++)
                {
                    writes[i] = new GateWriteStream(); reads[i] = new GateReadStream(65536);
                    send[i] = FrameCodec.WriteFrozenAsync(writes[i], maximum, limits, CancellationToken.None, egress);
                    receive[i] = ExpectTruncated(FrameCodec.ReadAsync(reads[i], limits, limits.FrameTimeout, CancellationToken.None, ingress));
                }
                await Program.MustComplete(Task.WhenAll(writes.Select(x => x.Entered.Task).Concat(reads.Select(x => x.Entered.Task))));
                var combined = new PipePayloadUsage(ingress.Read(), egress.Read(), limits);
                Check(ingress.Read().Count == 4 && egress.Read().Count == 4 && combined.Count == 8 && combined.Capacity == 524288 &&
                    combined.MaximumCount == 8 && combined.MaximumCapacity == 524288,
                    "four real held input allocations coexist with four held output allocations at the declared 512KiB raw limit");
                for (int i = 0; i < 4; i++) { writes[i].Release(); reads[i].Release(); }
                await Program.MustComplete(Task.WhenAll(send.Concat(receive)));
                Check(ingress.Read().Count == 0 && egress.Read().Count == 0 && ingress.Read().Capacity == 0 && egress.Read().Capacity == 0,
                    "both directions release only after all controlled stream IO has actually returned");
            }
            finally
            {
                for (int i = 0; i < 4; i++) { writes[i]?.Release(); reads[i]?.Release(); }
                if (send.All(x => x != null) && receive.All(x => x != null)) await Program.MustComplete(Task.WhenAll(send.Concat(receive)));
                foreach (var stream in writes) stream?.Dispose(); foreach (var stream in reads) stream?.Dispose();
            }
        }

        private static async Task ExpectTruncated(Task<string?> work)
        {
            try { await work; throw new InvalidOperationException("Expected an incomplete payload."); }
            catch (PipeProtocolException error) when (error.Code == "TRUNCATED_FRAME") { }
        }

        private static async Task CheckUnavailableAndScopes()
        {
            Program.Stage = "Stop pipe Legacy and null authority remain explicitly unavailable";
            foreach (bool noAuthority in new[] { false, true })
            using (var f = new Fixture(mixed: false, noAuthority: noAuthority))
            {
                await f.Grants.UpdateAsync("probe", GrantScope.Control, f.Token, null);
                using (var client = await f.Connect())
                {
                    await Send(client, f.Limits, "legacy-stop", "tasks.stop", StopArguments("unknown"));
                    var result = await Read(client, f.Limits, "tasks.stop");
                    Check((string?)result["result"]?["lookup"]?["kind"] == "unavailable" &&
                        result["result"]?["lookup"]?["receipt"]?.Type == JTokenType.Null &&
                        (string?)result["result"]?["lookup"]?["reason"] == "TASKS_UNAVAILABLE" && f.Server.ReplyUsage.ControlCount == 0,
                        "Legacy or missing authority returns compact unavailable without a protected Stop claim");
                }
                await f.WaitDisconnected();
            }

            Program.Stage = "Stop pipe Read-only is not Control";
            using (var f = new Fixture())
            {
                string id = (await f.CreateTasks(1))[0];
                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                using (var client = await f.Connect())
                {
                    await Send(client, f.Limits, "read-only-stop", "tasks.stop", StopArguments(id));
                    var error = await ReadAny(client, f.Limits);
                    Check((string?)error["code"] == "UNAUTHORIZED" && !f.Ops.LookupTask(id, f.Observer).StopRequested,
                        "Read-only cannot enter Stop admission through the actual authenticated pipe");
                }
                await f.WaitDisconnected();
            }
        }

        private static async Task CheckSlowFlush()
        {
            Program.Stage = "Stop pipe Control-only Begin returns before slow ordinary Flush";
            using (var f = new Fixture())
            {
                var ids = await f.CreateTasks(2); string id = ids[0];
                await f.Grants.UpdateAsync("probe", GrantScope.Control, f.Token, null);
                var subject = f.Grants.Read("probe")!;
                var key = new SubmissionKey(f.Ops.IssueWriteContext(subject), "revision");
                f.Flush.Arm();
                Task<TaskDecisionLookupSnapshot> revision = f.Ops.ReviseTaskGoalAsync(key, id, 1, 1, Goal("changed"), subject);
                await Program.MustComplete(f.Flush.Entered.Task);
                using (var client = await f.Connect())
                {
                    await Send(client, f.Limits, "slow-stop", "tasks.stop", StopArguments(id));
                    var response = await Read(client, f.Limits, "tasks.stop");
                    Check(!revision.IsCompleted && !f.Flush.Release.IsSet && f.Ops.LookupTask(id, f.Observer).StopRequested &&
                        (string?)response["result"]?["lookup"]?["kind"] == "pending" &&
                        (bool?)response["result"]?["lookup"]?["receipt"]?["stopRequested"] == true,
                        "Control-only pipe Stop latches and returns accurate pending while the original checked Flush remains held");
                    await Send(client, f.Limits, "control-cannot-read", "tasks.stop.get", StopArguments(id));
                    Check((string?)(await ReadAny(client, f.Limits))["code"] == "UNAUTHORIZED",
                        "Control-only receipt access does not grant the public Read query");
                }
                f.Flush.Release.Set(); await Program.MustComplete(revision);
                await Program.MustComplete(f.Ops.StopTaskAsync(id, 1, subject));
                await f.WaitDisconnected();

                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                using (var reader = await f.Connect())
                {
                    await Send(reader, f.Limits, "read-stop-decision", "tasks.stop.get", StopArguments(id));
                    var result = await Read(reader, f.Limits, "tasks.stop.get");
                    Check((string?)result["result"]?["result"]?["kind"] == "live" &&
                        (string?)result["result"]?["result"]?["lookup"]?["kind"] == "found",
                        "a fresh Read-only binding retrieves the original confirmed Stop through the public read capability");
                    await Send(reader, f.Limits, "read-cannot-stop-second", "tasks.stop", StopArguments(ids[1]));
                    Check((string?)(await ReadAny(reader, f.Limits))["code"] == "UNAUTHORIZED" && !f.Ops.LookupTask(ids[1], f.Observer).StopRequested,
                        "public Stop decision reading cannot mutate another unstopped Task");
                }
                await f.WaitDisconnected();
            }
        }

        private static async Task CheckPreflightRejections()
        {
            Program.Stage = "Stop pipe owner and CAS errors do not consume protected reply credits";
            using (var f = new Fixture())
            {
                string id = (await f.CreateTasks(1))[0]; string otherToken = HandshakeProof.RandomHex();
                await f.Grants.UpdateAsync("other", GrantScope.Read | GrantScope.Control, otherToken, null);
                using (var client = await f.Connect("other", otherToken))
                {
                    await Send(client, f.Limits, "wrong-owner", "tasks.stop", StopArguments(id));
                    Check((string?)(await ReadAny(client, f.Limits))["code"] == "TASK_OWNER_MISMATCH" &&
                        f.Server.ReplyUsage.ControlCount == 0 && !f.Ops.LookupTask(id, f.Observer).StopRequested,
                        "schema-valid wrong-owner Stop uses no protected credit and admits no decision");
                }
                await f.WaitDisconnected();
                using (var client = await f.Connect())
                {
                    var wrongVersion = StopArguments(id); wrongVersion["expectedControlVersion"] = 2;
                    await Send(client, f.Limits, "wrong-version", "tasks.stop", wrongVersion);
                    Check((string?)(await ReadAny(client, f.Limits))["code"] == "TASK_VERSION_CONFLICT" &&
                        f.Server.ReplyUsage.ControlCount == 0 && !f.Ops.LookupTask(id, f.Observer).StopRequested,
                        "schema-valid new-CAS mismatch is rejected before protected transport credit");
                    await Send(client, f.Limits, "read-after-invalid-stop", "system.context", new JObject());
                    Check((string?)(await Read(client, f.Limits, "system.context"))["kind"] == "result",
                        "ordinary finite preflight errors do not corrupt the authenticated byte stream");
                }
                await f.WaitDisconnected();
            }
        }

        private static async Task CheckStoppedReaderAndQueuedRevocation()
        {
            Program.Stage = "Stop pipe eight ordinary replies stalled and eight protected Stops";
            using (var f = new Fixture(largeOrdinary: true))
            {
                var ids = await f.CreateTasks(8);
                using (var client = await f.Connect())
                {
                    int sent = await FillOrdinary(f, client);
                    for (int i = 0; i < ids.Count; i++) await Send(client, f.Limits, "protected-" + i, "tasks.stop", StopArguments(ids[i]));
                    await WaitUntil(() => ids.All(id => f.Ops.LookupTask(id, f.Observer).StopRequested));
                    Check(f.Server.ReplyUsage.OrdinaryCount == 8 && f.Server.ReplyUsage.ControlCount == 8 &&
                        f.Server.EgressPayloadUsage.Count == 1 && f.Server.IngressPayloadUsage.Count == 0,
                        "eight distinct Stop RPCs reach the same Authority while all eight ordinary reply responsibilities and output IO remain held");
                    await f.Grants.UpdateAsync("probe", GrantScope.None, f.Token, null);
                    JToken? stopError = null;
                    for (int i = 0; i <= sent; i++)
                    {
                        var message = await ReadAny(client, f.Limits);
                        if ((string?)message["requestId"] == "protected-0") { stopError = message; break; }
                    }
                    Check((string?)stopError?["kind"] == "error" && (string?)stopError?["code"] == "UNAUTHORIZED" &&
                        ids.All(id => f.Ops.LookupTask(id, f.Observer).StopRequested),
                        "a queued Stop receipt rechecks Control at dequeue after revocation without rolling back any admitted Stop");
                }
                await f.WaitDisconnected();
                Check(f.Server.ReplyUsage.OrdinaryCount == 0 && f.Server.ReplyUsage.ControlCount == 0 &&
                    f.Server.ReplyUsage.OwnedTextCharacters == 0 && f.Server.IngressPayloadUsage.Count == 0 && f.Server.EgressPayloadUsage.Count == 0,
                    "actual connection close joins both loops and releases queued, in-flight, text and directional owners");
            }
        }

        private static async Task CheckDuplicateAndOriginalReceipt()
        {
            Program.Stage = "Stop pipe repeated protected identity is finite and preserves the original decision";
            using (var f = new Fixture(largeOrdinary: true))
            {
                string id = (await f.CreateTasks(1))[0]; SubmissionKey original;
                using (var client = await f.Connect())
                {
                    await FillOrdinary(f, client);
                    await Send(client, f.Limits, "original-stop", "tasks.stop", StopArguments(id));
                    await WaitUntil(() => f.Server.ReplyUsage.ControlCount == 1 && f.Ops.LookupTask(id, f.Observer).StopRequested);
                    original = f.Ops.LookupTaskStopDecision(id, 1, f.Observer).Key;
                    await Send(client, f.Limits, "duplicate-stop", "tasks.stop", StopArguments(id));
                    // A策略：原身份仍持额，普通错误额度也满，明确close而非挂第二个waiter。
                    await f.WaitDisconnected();
                    Check(f.Server.ReplyUsage.ControlCount == 0 && f.Server.ReplyUsage.OrdinaryCount == 0 && f.Ops.LookupTask(id, f.Observer).StopRequested,
                        "duplicate active Stop with no ordinary error capacity closes and drains without cancelling the original sticky decision");
                }
                await Program.MustComplete(f.Ops.StopTaskAsync(id, 1, f.Grants.Read("probe")!));
                using (var retry = await f.Connect())
                {
                    await Send(retry, f.Limits, "retry-original-stop", "tasks.stop", StopArguments(id));
                    var result = await Read(retry, f.Limits, "tasks.stop"); var key = result["result"]?["lookup"]?["receipt"]?["submissionKey"];
                    Check((string?)result["result"]?["lookup"]?["kind"] == "found" && (string?)key?["writeContextId"] == original.WriteContextId &&
                        (string?)key?["requestId"] == original.RequestId &&
                        f.Journal.CaptureConfirmedPrefix()!.Records.Count(row => row.Entry is TaskStopped stop && stop.TaskId == id) == 1,
                        "after actual close a fresh RPC retrieves the original Stop key and does not append another physical Stop");
                }
                await f.WaitDisconnected();
            }
        }

        private static async Task<int> FillOrdinary(Fixture f, Stream client)
        {
            int sent = 0;
            do
            {
                await Send(client, f.Limits, "ordinary-" + ++sent, "system.context", new JObject());
                await WaitUntil(() => f.Dispatcher.OrdinaryCalls >= sent || f.Server.ConnectedCount == 0);
                Check(f.Server.ConnectedCount == 1, "bounded ordinary output fixture keeps its one authenticated connection");
            }
            while (f.Server.ReplyUsage.OrdinaryCount < 8 && sent < 60);
            await WaitUntil(() => f.Server.ReplyUsage.OrdinaryCount == 8 && f.Server.EgressPayloadUsage.Count == 1);
            return sent;
        }

        private static Task Send(Stream client, PipeLimits limits, string id, string capability, JToken arguments, string? revision = null) => FrameCodec.WriteAsync(client,
            new JObject { ["kind"] = "request", ["requestId"] = id, ["capabilityId"] = capability,
                ["schemaRevision"] = revision ?? CurrentRevision(capability), ["arguments"] = arguments }, limits, CancellationToken.None);
        private static string CurrentRevision(string capability) => ContractCatalog.Capabilities.Single(row => row.Id == capability).SchemaRevision;
        private static JObject OperationArguments() => new JObject { ["selector"] = new JObject { ["kind"] = "operationId", ["operationId"] = "missing" } };
        private static JObject StopArguments(string id) => new JObject { ["taskId"] = id, ["expectedControlVersion"] = 1 };
        private static TaskStatedGoal Goal(string title) => new TaskStatedGoal(title, "fixture intent", Array.Empty<string>(), Array.Empty<string>());
        private static async Task<JToken> ReadAny(Stream client, PipeLimits limits)
        {
            string? value = await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None);
            if (value == null) throw new IOException("Expected a complete server frame."); return ContractJson.Parse(value);
        }
        private static async Task<JToken> Read(Stream client, PipeLimits limits, string capability)
        {
            var value = await ReadAny(client, limits);
            var validated = ContractCatalog.Validate(SchemaIds.WireResult, value, new RegisteredBinding(capability, CurrentRevision(capability)));
            if (!validated.Valid) throw new ContractValidationException(validated.Issues); return value;
        }
        private static async Task WaitUntil(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready()) { if (watch.ElapsedMilliseconds >= 3000) throw new TimeoutException("Stop pipe fixture scheduling deadline."); await Task.Delay(5); }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "stop-pipe-" + Guid.NewGuid().ToString("N"));
            private readonly PrivateConnectionStorage boundary;
            private readonly EnvironmentLease lease;
            internal readonly string Token = HandshakeProof.RandomHex();
            internal readonly PipeLimits Limits;
            internal readonly GrantAuthority Grants;
            internal readonly OperationJournal Journal;
            internal readonly OperationAuthority Ops;
            internal readonly CheckedFlush Flush;
            internal readonly BoundedPipeServer Server;
            internal readonly ObservedDispatcher Dispatcher;
            internal readonly EnvironmentDiscovery Identity;
            internal readonly ModuleRuntimeObservationBroker? Modules;
            internal readonly CityOverviewReadBroker? CityOverview;
            internal readonly WorldContextTracker Context;
            internal GrantSnapshot Observer => Grants.Read("observer")!;
            internal Fixture(bool mixed = true, bool noAuthority = false, bool largeOrdinary = false,
                bool deferOrdinary = false, int frameMilliseconds = 5000, bool withModules = false, bool withCityOverview = false)
            {
                Limits = Program.Limits(frameMilliseconds);
                Directory.CreateDirectory(root); boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                var store = new AtomicAuthorizationStore(boundary, lease, "probe"); store.InitializeEmpty();
                var activated = store.Load().Activate(); store.Save(1, activated); Grants = new GrantAuthority(activated, store);
                Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, Token, null).GetAwaiter().GetResult();
                Grants.UpdateAsync("observer", GrantScope.Read, HandshakeProof.RandomHex(), null).GetAwaiter().GetResult();
                Flush = new CheckedFlush(boundary);
                Journal = mixed ? OperationJournal.OpenMixedForAuthority(boundary, lease, "probe", true, Flush)
                    : OperationJournal.OpenForAuthority(boundary, lease, "probe", true, Flush);
                string instance = Guid.NewGuid().ToString("N"); var context = new WorldContextTracker(instance);
                Context = context;
                long generation = context.EnterLoading(); context.ObserveReady(generation, "city-a");
                Ops = new OperationAuthority(Journal, Grants, context, new HostIdentity(), () => 0);
                Identity = new EnvironmentDiscovery { ProductId = "skylineslink", EnvironmentId = "probe", InstanceId = instance,
                    AuthorityEpoch = Grants.State.AuthorityEpoch, PipeName = "skylineslink-" + Guid.NewGuid().ToString("N"),
                    ProductVersion = "0.1.0-dev.0", ContractVersion = "2", CoreManifestSha256 = new string('c', 64), ProcessId = Process.GetCurrentProcess().Id };
                Modules = withModules ? new ModuleRuntimeObservationBroker(instance) : null;
                CityOverview = withCityOverview ? new CityOverviewReadBroker(context) : null;
                Dispatcher = new ObservedDispatcher(new SnapshotCapabilityDispatcher(context, Program.ProductVersion, noAuthority ? null : Ops, Modules, CityOverview), largeOrdinary, deferOrdinary);
                Server = new BoundedPipeServer(Identity, Limits, Grants, Dispatcher); Server.Start();
            }
            internal async Task<List<string>> CreateTasks(int count)
            {
                var subject = Grants.Read("probe")!; string write = Ops.IssueWriteContext(subject); var ids = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var creation = await Ops.CreateTaskAsync(new SubmissionKey(write, "create-" + i), Goal("task-" + i), subject);
                    ids.Add(creation.Receipt!.TaskId);
                }
                return ids;
            }
            internal async Task<NamedPipeClientStream> Connect(string profile = "probe", string? token = null)
            {
                var client = new NamedPipeClientStream(".", Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(2000); var subject = Grants.Read(profile)!; string secret = token ?? Token;
                    await FrameCodec.WriteAsync(client, ContractCodec.EncodeWireAuthenticateV2(new WireAuthenticateV2 { Kind = "authenticate", RequestId = "auth",
                        EnvironmentId = Identity.EnvironmentId, ProfileId = profile, ExpectedInstanceId = Identity.InstanceId, ProductVersion = Identity.ProductVersion,
                        ContractVersion = Identity.ContractVersion, CoreManifestSha256 = Identity.CoreManifestSha256, ExpectedAuthorityEpoch = subject.AuthorityEpoch,
                        ExpectedGrantEpoch = subject.Epoch, ClientNonce = HandshakeProof.RandomHex() }), Limits, CancellationToken.None);
                    var challenge = ContractCodec.DecodeWireChallengeV2(await ReadAny(client, Limits));
                    Check(HandshakeProof.EqualsProof(challenge.ServerProof, HandshakeProof.Compute(secret, "server", challenge)), "Stop pipe authenticates the actual server possession proof");
                    await FrameCodec.WriteAsync(client, ContractCodec.EncodeWireProofV2(new WireProofV2 { Kind = "proof", RequestId = "auth",
                        ClientProof = HandshakeProof.Compute(secret, "client", challenge) }), Limits, CancellationToken.None);
                    var accepted = ContractCodec.DecodeWireAuthenticatedV2(await ReadAny(client, Limits));
                    Check(accepted.InstanceId == Identity.InstanceId && accepted.GrantEpoch == subject.Epoch, "Stop pipe fixes the authenticated current subject");
                    return client;
                }
                catch { client.Dispose(); throw; }
            }
            internal Task WaitDisconnected() => WaitUntil(() => Server.ConnectedCount == 0 && Server.AuthenticatedCount == 0 &&
                Server.IngressPayloadUsage.Count == 0 && Server.EgressPayloadUsage.Count == 0 &&
                Server.ReplyUsage.OrdinaryCount == 0 && Server.ReplyUsage.ControlCount == 0);
            public void Dispose()
            {
                Modules?.Stop(); CityOverview?.Stop(); Flush.Release.Set(); Server.Dispose(); Program.MustComplete(Server.Completion).GetAwaiter().GetResult();
                if (Modules != null) Program.MustComplete(Modules.WhenIdle).GetAwaiter().GetResult();
                if (CityOverview != null) Program.MustComplete(CityOverview.WhenIdle).GetAwaiter().GetResult();
                Program.MustComplete(Ops.CloseAsync()).GetAwaiter().GetResult(); Program.MustComplete(Grants.StopAsync()).GetAwaiter().GetResult(); lease.Dispose(); Flush.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") || !Path.GetFileName(root).StartsWith("stop-pipe-", StringComparison.Ordinal))
                    throw new IOException("Stop pipe fixture containment failed.");
                foreach (string directory in new[] { root, boundary.DirectoryPath })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Stop pipe fixture refuses directory links.");
                foreach (string file in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Stop pipe fixture refuses file links."); File.Delete(file); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }

        private sealed class ObservedDispatcher : IReadOnlyCapabilityDispatcher, ITaskStopCapabilityDispatcher, IDeferredReadCapabilityDispatcher
        {
            private readonly SnapshotCapabilityDispatcher inner;
            private readonly bool largeOrdinary;
            private readonly bool deferOrdinary;
            private readonly List<ControlledRead> pending = new List<ControlledRead>();
            private int consumerReleases;
            internal int PendingCount { get { lock (pending) return pending.Count; } }
            internal int ConsumerReleases => Volatile.Read(ref consumerReleases);
            private int ordinaryCalls;
            internal int OrdinaryCalls => Volatile.Read(ref ordinaryCalls);
            internal ObservedDispatcher(SnapshotCapabilityDispatcher inner, bool largeOrdinary, bool deferOrdinary)
            { this.inner = inner; this.largeOrdinary = largeOrdinary; this.deferOrdinary = deferOrdinary; }
            public DeferredCapabilityRead BeginRead(string capability, string revision, JToken arguments, GrantSnapshot subject)
            {
                if (capability == "system.modules.get" && revision == "1" || capability == "city.overview" && revision == "2")
                    return inner.BeginRead(capability, revision, arguments, subject);
                var result = Execute(capability, revision, arguments, subject);
                if (!deferOrdinary || capability != "system.context")
                    return new DeferredCapabilityRead(Task.FromResult(result), new EmptyConsumer());
                var read = new ControlledRead(result, () => Interlocked.Increment(ref consumerReleases));
                lock (pending) pending.Add(read);
                return new DeferredCapabilityRead(read.Completion.Task, read);
            }
            internal bool CompleteRead(int index)
            { lock (pending) return pending[index].Completion.TrySetResult(pending[index].Result); }
            internal void FailRead(int index)
            { lock (pending) pending[index].Completion.TrySetException(new CapabilityFailure("OBSERVATION_CHANGED", "本次观察已失效。")); }
            public JToken Execute(string capability, string revision, JToken arguments, GrantSnapshot subject)
            {
                var result = inner.Execute(capability, revision, arguments, subject);
                if (largeOrdinary && capability == "system.context")
                {
                    // 合法schema的合成普通值让真实pipe填满；不声称这份fixture是当前游戏事实。
                    result["productVersion"] = new string('\u0001', 64); result["contractVersion"] = new string('\u0002', 64);
                    result["catalogRevision"] = new string('\u0003', 128);
                    result["world"] = new JObject { ["cityId"] = new string('\u0004', 128), ["generation"] = new string('\u0005', 128) };
                }
                Interlocked.Increment(ref ordinaryCalls); return result;
            }
            public void ValidateTaskStop(TaskStopSelector selector, GrantSnapshot subject) => inner.ValidateTaskStop(selector, subject);
            public TaskDecisionLookupSnapshot BeginTaskStop(TaskStopSelector selector, GrantSnapshot subject) => inner.BeginTaskStop(selector, subject);
            public JToken RefreshTaskStop(TaskStopSelector selector, SubmissionKey key, GrantSnapshot subject) => inner.RefreshTaskStop(selector, key, subject);
            private sealed class EmptyConsumer : IDisposable { public void Dispose() { } }
            private sealed class ControlledRead : IDisposable
            {
                internal readonly JToken Result;
                internal readonly TaskCompletionSource<JToken> Completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                private Action? released;
                internal ControlledRead(JToken result, Action released) { Result = result; this.released = released; }
                public void Dispose()
                {
                    var action = Interlocked.Exchange(ref released, null);
                    if (action == null) return;
                    Completion.TrySetCanceled(); action();
                }
            }
        }
        private sealed class HostIdentity : INativeHostIdentitySource
        {
            public NativeHostIdentity Current { get; } = new NativeHostIdentity(101, 123456);
            public NativeHostState Observe(NativeHostIdentity identity) => NativeHostState.Ended;
        }
        private sealed class CheckedFlush : IJournalDurability, IDisposable
        {
            private readonly IJournalDurability real;
            private int armed;
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(true);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CheckedFlush(IPrivateStorageBoundary boundary) { real = new FileJournalDurability(boundary); }
            internal void Arm() { Release.Reset(); Volatile.Write(ref armed, 1); }
            public void Flush(FileStream file)
            {
                if (Interlocked.Exchange(ref armed, 0) == 1) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Stop pipe controlled Flush deadline."); }
                real.Flush(file);
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }

        private abstract class FixtureStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
        private sealed class GateWriteStream : FixtureStream
        {
            private int writes;
            private readonly TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Release() => done.TrySetResult(true);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            { if (Interlocked.Increment(ref writes) == 1) return Task.CompletedTask; Entered.TrySetResult(true); return done.Task; }
        }
        private sealed class GateReadStream : FixtureStream
        {
            private readonly byte[] header = new byte[12];
            private int reads;
            private readonly TaskCompletionSource<int> done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal GateReadStream(int length)
            { header[0] = (byte)'S'; header[1] = (byte)'L'; header[2] = (byte)'N'; header[3] = (byte)'K'; header[4] = 1;
                header[8] = (byte)length; header[9] = (byte)(length >> 8); header[10] = (byte)(length >> 16); header[11] = (byte)(length >> 24); }
            internal void Release() => done.TrySetResult(0);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            { if (Interlocked.Increment(ref reads) == 1) { Buffer.BlockCopy(header, 0, buffer, offset, 12); return Task.FromResult(12); }
                Entered.TrySetResult(true); return done.Task; }
        }
    }
}
