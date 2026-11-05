using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Modules;
using SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    internal static class ModuleRuntimeObservationTests
    {
        private static int checks;
        private static void Check(bool value, string name) { Program.Assert(value, name); checks++; }
        private static string Profile(int value) => value.ToString("x32");
        internal static async Task<int> RunAsync()
        {
            await CheckSharedObservationAndPages();
            await CheckCancellationAndRealWork();
            await CheckRetentionReservationsAndFailure();
            await CheckMissingValuesRemainJsonNull();
            await CheckBoundsAndWorstAdmittedPage();
            return checks;
        }

        private static async Task CheckSharedObservationAndPages()
        {
            Program.Stage = "one module capture feeds isolated owned pages and profile-scoped refs";
            long now = 0; var scheduler = new HeldPublisher();
            var broker = new ModuleRuntimeObservationBroker("instance", () => now, scheduler.Run);
            using (var a = broker.Capture(Profile(1)))
            using (var a2 = broker.Capture(Profile(1)))
            using (var b = broker.Capture(Profile(2)))
            {
                bool claimed = broker.TryClaim(out var work);
                Check(broker.ReadUsage().ConsumerSlots == 3 && claimed && work != null,
                    "three readers share one actual native capture slot");
                Check(!broker.TryClaim(out _), "a capturing ticket cannot be claimed twice");
                broker.Complete(work!, Facts(20));
                Check(scheduler.Calls == 1 && !broker.WhenIdle.IsCompleted && !a.Completion.IsCompleted,
                    "one owned publisher remains active before its real task starts work");
                using (var busy = broker.Capture(Profile(3)))
                    Check((string?)busy.Completion.Result["reason"] == "BUSY", "publishing does not append new followers");
                scheduler.Release(); await Program.MustComplete(broker.WhenIdle);
                Check((string?)(await a.Completion)["kind"] == "page", "module producer must emit a valid page");
                var first = await a.Completion; var second = await a2.Completion; var peer = await b.Completion;
                Check(!ReferenceEquals(first, second) && !ReferenceEquals(first, peer), "each consumer owns a different result DOM");
                string snapshot = (string)first["snapshot"]!["snapshotId"]!;
                Check((string?)peer["snapshot"]?["snapshotId"] == snapshot && broker.ReadUsage().PublishedSnapshotSlots == 2,
                    "the same observation is retained under each profile identity without duplicate same-profile slots");
                first["entries"]![0]!["assemblyName"] = "caller-mutated";
                using (var page = broker.Page(Profile(1), snapshot, 0))
                    Check((string?)page.Completion.Result["entries"]?[0]?["assemblyName"] == "module",
                        "mutating one returned DOM cannot alter the retained facts or another reader");
                using (var page = broker.Page(Profile(1), snapshot, 16))
                {
                    var value = page.Completion.Result;
                    Check((int)value["returnedCount"]! == 4 && value["nextOffset"]!.Type == JTokenType.Null &&
                        (int)value["entries"]![0]!["ordinal"]! == 16,
                        "stable pages traverse the entire captured sequence even when all asset GUIDs are identical");
                }
                using (var page = broker.Page(Profile(1), snapshot, 20))
                    Check((int)page.Completion.Result["returnedCount"]! == 0, "the exact end offset is a valid empty tail");
                using (var page = broker.Page(Profile(1), snapshot, 21))
                    Check((string?)page.Completion.Result["reason"] == "PAGE_OUT_OF_RANGE", "an uncovered offset is not an empty successful page");
                using (var page = broker.Page(Profile(3), snapshot, 0))
                    Check((string?)page.Completion.Result["reason"] == "SNAPSHOT_UNAVAILABLE", "snapshot IDs do not substitute for the current profile identity");
                now = 299999;
                using (var page = broker.Page(Profile(1), snapshot, 0))
                    Check((string?)page.Completion.Result["kind"] == "page", "pages remain usable inside the fixed monotonic lifetime");
                now = 300000;
                using (var page = broker.Page(Profile(1), snapshot, 0))
                    Check((string?)page.Completion.Result["reason"] == "SNAPSHOT_UNAVAILABLE", "reading a page did not extend the original lifetime");
                Check(broker.ReadUsage().PublishedSnapshotSlots == 0, "expired retained snapshots are actually detached");
            }
            broker.Stop(); await Program.MustComplete(broker.WhenIdle);
        }

        private static async Task CheckCancellationAndRealWork()
        {
            Program.Stage = "cancelled module consumers do not end actual capture or publisher ownership";
            var scheduler = new HeldPublisher(); var broker = new ModuleRuntimeObservationBroker("instance", () => 0, scheduler.Run);
            var pending = broker.Capture(Profile(1)); pending.Dispose();
            Check(pending.Completion.IsCanceled && broker.WhenIdle.IsCompleted && !broker.TryClaim(out _),
                "zero consumers cancel an unclaimed capture without scheduling native work");
            var claimed = broker.Capture(Profile(1)); Check(broker.TryClaim(out var work), "the next independent request can claim the released pending slot");
            claimed.Dispose();
            Check(claimed.Completion.IsCanceled && !broker.WhenIdle.IsCompleted && broker.ReadUsage().ActiveCaptureCount == 1,
                "consumer cancellation is terminal immediately but the claimed native work remains owned");
            for (int i = 0; i < 7; i++) broker.Capture(Profile(1)).Dispose();
            using (var full = broker.Capture(Profile(1)))
                Check((string?)full.Completion.Result["reason"] == "BUSY" && broker.ReadUsage().ConsumerSlots == 8,
                    "cancelled followers do not grow or reuse an unbounded historical list");
            broker.Complete(work!, Facts(1)); await Program.MustComplete(broker.WhenIdle);
            Check(scheduler.Calls == 0 && broker.ReadUsage().PublishedSnapshotSlots == 0,
                "actual capture finish with no consumers discards the candidate without a publisher");
            var publishing = broker.Capture(Profile(1)); Check(broker.TryClaim(out var next), "a finished capture releases its actual slot");
            broker.Complete(next!, Facts(2)); Task idle = broker.WhenIdle;
            broker.Stop();
            Check(publishing.Completion.IsCanceled && !idle.IsCompleted && broker.ReadUsage().ActiveCaptureCount == 1,
                "Stop cancels waiting consumers but cannot claim a held publisher has returned");
            using (var stopped = broker.Capture(Profile(2)))
                Check((string?)stopped.Completion.Result["reason"] == "STOPPED", "Stop rejects new capture admission");
            scheduler.Release(); await Program.MustComplete(idle);
            Check(broker.ReadUsage().ActiveCaptureCount == 0 && broker.ReadUsage().PublishedSnapshotSlots == 0,
                "late publication after Stop does not repopulate the cache and real completion finally releases work");
            publishing.Dispose();
            long now = 0; var delayed = new HeldPublisher();
            var expired = new ModuleRuntimeObservationBroker("instance", () => now, delayed.Run);
            using (var read = expired.Capture(Profile(1)))
            {
                Check(expired.TryClaim(out var ticket), "an accepted capture owns its original expiry basis");
                expired.Complete(ticket!, Facts(1)); now = 300000;
                delayed.Release(); await Program.MustComplete(expired.WhenIdle);
                Check((string?)(await read.Completion)["reason"] == "CAPTURE_FAILED" && expired.ReadUsage().PublishedSnapshotSlots == 0,
                    "a publisher delayed past capture lifetime cannot refresh stale facts or return a page-only selector error");
            }
            expired.Stop();
        }

        private static async Task CheckRetentionReservationsAndFailure()
        {
            Program.Stage = "eight profile reservations and immutable failure-preserving refresh";
            var broker = new ModuleRuntimeObservationBroker("instance", () => 0);
            var leases = Enumerable.Range(1, 8).Select(i => broker.Capture(Profile(i))).ToArray();
            try
            {
                using (var ninth = broker.Capture(Profile(9)))
                    Check((string?)ninth.Completion.Result["reason"] == "BUSY", "uncached profiles reserve their slots before native work starts");
                Check(broker.TryClaim(out var work), "eight profiles coalesce into one native request");
                broker.Complete(work!, Facts(1)); await Program.MustComplete(broker.WhenIdle);
                string old = (string)(await leases[0].Completion)["snapshot"]!["snapshotId"]!;
                string other = (string)(await leases[1].Completion)["snapshot"]!["snapshotId"]!;
                Check(broker.ReadUsage().PublishedSnapshotSlots == 8, "publication obeys the shared eight-profile retention bound");
                using (var failed = broker.Capture(Profile(1)))
                {
                    Check(broker.TryClaim(out var refresh), "an existing profile may refresh without taking a ninth retained slot");
                    broker.Fail(refresh!, "REGISTRY_CHANGED");
                    Check((string?)(await failed.Completion)["reason"] == "REGISTRY_CHANGED", "capture failure retains its fixed public reason");
                }
                using (var page = broker.Page(Profile(1), old, 0))
                    Check((string?)page.Completion.Result["kind"] == "page", "failed refresh preserves the previous observation");
                using (var fresh = broker.Capture(Profile(1)))
                {
                    Check(broker.TryClaim(out var refresh), "a failed capture frees its actual work slot");
                    broker.Complete(refresh!, Facts(2)); await Program.MustComplete(broker.WhenIdle);
                    Check((string?)(await fresh.Completion)["snapshot"]?["snapshotId"] != old, "successful refresh publishes a new observation identity");
                }
                using (var page = broker.Page(Profile(1), old, 0))
                    Check((string?)page.Completion.Result["reason"] == "SNAPSHOT_UNAVAILABLE", "old refs are explicitly unavailable after their profile refreshes");
                using (var page = broker.Page(Profile(2), other, 0))
                    Check((string?)page.Completion.Result["kind"] == "page", "one profile refresh does not invalidate another profile's pages");
            }
            finally { foreach (var lease in leases) lease.Dispose(); broker.Stop(); await Program.MustComplete(broker.WhenIdle); }
        }

        private static async Task CheckBoundsAndWorstAdmittedPage()
        {
            Program.Stage = "bounded native values and worst admitted module page";
            bool Rejected(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
            Check(Rejected(() => Row(0, new string('x', 129))) && Rejected(() => Row(0, "\ud800")),
                "long or malformed UTF16 names are rejected rather than silently clipped");
            Check(Rejected(() => new ModuleRuntimeFact(0, new string('0', 32), true, 1, false, null, null, 0, false, "game", Array.Empty<ModuleFieldIssue>())),
                "the empty native GUID cannot become a valid asset identity");
            var broker = new ModuleRuntimeObservationBroker(new string('\u0001', 128), () => 0);
            using (var read = broker.Capture(Profile(1)))
            {
                Check(broker.TryClaim(out var work), "worst admitted values use the same bounded capture path");
                broker.Complete(work!, Facts(128, new string('\u0001', 128), new string('\u0002', 64), visited: 129, reason: "ENTRY_LIMIT"));
                await Program.MustComplete(broker.WhenIdle);
                var value = await read.Completion;
                int raw = Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
                Check((string?)value["kind"] == "page" && raw <= ModuleRuntimeQueryService.MaximumRawResultBytes &&
                    ContractCatalog.Validate(SchemaIds.ModuleRuntimePage, value).Valid,
                    "a semantically admitted maximum escaped page fits the actual 24KiB raw budget");
                Check(value["snapshot"]!["coverage"]!["totalCount"]!.Type == JTokenType.Null &&
                    !(bool)value["snapshot"]!["coverage"]!["complete"]!,
                    "a fully pageable captured prefix is still explicitly not the whole native registry");
                string snapshot = (string)value["snapshot"]!["snapshotId"]!;
                using (var last = broker.Page(Profile(1), snapshot, 112))
                    Check(last.Completion.Result["nextOffset"]!.Type == JTokenType.Null &&
                        !(bool)last.Completion.Result["snapshot"]!["coverage"]!["complete"]!,
                        "nextOffset null closes the observed prefix without upgrading partial native coverage");
            }
            broker.Stop(); await Program.MustComplete(broker.WhenIdle);
        }

        private static async Task CheckMissingValuesRemainJsonNull()
        {
            Program.Stage = "module field coverage is actual JSON null before the wire validator";
            var rows = new[] {
                new ModuleRuntimeFact(0, null, null, null, null, null, null, null, null, "unknown",
                    new[] { new ModuleFieldIssue("entry", "missing") }),
                new ModuleRuntimeFact(1, null, null, 1, null, null, null, 0, false, "unknown",
                    new[] { new ModuleFieldIssue("asset", "missing") }),
                new ModuleRuntimeFact(2, new string('f', 32), true, 2, false, null, null, 0, true, "user", Array.Empty<ModuleFieldIssue>()),
                new ModuleRuntimeFact(3, null, true, 8, true, null, null, null, true, "unknown", new[] {
                    new ModuleFieldIssue("assetGuid", "invalid"), new ModuleFieldIssue("assemblyName", "unavailable"),
                    new ModuleFieldIssue("assemblyVersion", "unavailable"), new ModuleFieldIssue("instanceCount", "missing"),
                    new ModuleFieldIssue("databaseKind", "missing") }) };
            var broker = new ModuleRuntimeObservationBroker("instance");
            using (var read = broker.Capture(Profile(1)))
            {
                Check(broker.TryClaim(out var work), "nullable source observations use the same capture owner");
                broker.Complete(work!, new ModuleRegistryCapture(1000, 1001, null, "unavailable", false, true, rows, 4, true, null));
                await Program.MustComplete(broker.WhenIdle);
                var page = await read.Completion;
                Check((string?)page["kind"] == "page" && ContractCatalog.Validate(SchemaIds.ModuleRuntimePage, page).Valid,
                    "all missing-parent and unloaded-assembly forms validate before serialization");
                Check(page["snapshot"]!["gameVersion"]!.Type == JTokenType.Null &&
                    page["snapshot"]!["coverage"]!["reason"]!.Type == JTokenType.Null &&
                    page["entries"]![0]!["assetGuid"]!.Type == JTokenType.Null &&
                    page["entries"]![3]!["assemblyName"]!.Type == JTokenType.Null &&
                    page["entries"]![3]!["instanceCount"]!.Type == JTokenType.Null,
                    "nullable CLR strings and values cannot masquerade as typed non-null JValues");
            }
            broker.Stop(); await Program.MustComplete(broker.WhenIdle);
        }

        internal static ModuleRegistryCapture Facts(int count, string name = "module", string version = "1.6.2f1",
            int? visited = null, string? reason = null) => new ModuleRegistryCapture(1000, 1001, version, null, true, false,
                Enumerable.Range(0, count).Select(index => Row(index, name)).ToArray(), visited ?? count, reason == null, reason);
        private static ModuleRuntimeFact Row(int ordinal, string name) => new ModuleRuntimeFact(ordinal, new string('f', 32),
            true, int.MaxValue, true, name, new string('9', 32), int.MaxValue, true, "paradoxMods", Array.Empty<ModuleFieldIssue>());
        private sealed class HeldPublisher
        {
            private readonly TaskCompletionSource<bool> released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Calls;
            internal async Task Run(Action action) { Calls++; await released.Task.ConfigureAwait(false); action(); }
            internal void Release() => released.TrySetResult(true);
        }
    }
}
