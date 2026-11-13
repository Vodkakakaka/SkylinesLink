using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Buildings;
using SkylinesLink.Application.Context;
using SkylinesLink.Contracts;
using Query = SkylinesLink.Application.Buildings.BuildingInspectionQuery;

namespace SkylinesLink.RuntimeProbe
{
    internal static class BuildingInspectionTests
    {
        private static int checks;
        private static void Check(bool value, string name) { Program.Assert(value, name); checks++; }
        internal static Query Selected(int limit = 8) => Query.Parse(new JObject { ["target"] = new JObject { ["kind"] = "selectedInfo" }, ["relationLimit"] = limit });
        internal static Query Exact(WorldContextSnapshot scope, int index = 42, int version = 7, int limit = 8) => Query.Parse(new JObject {
            ["target"] = new JObject { ["kind"] = "entityRef", ["ref"] = new JObject { ["instanceId"] = scope.InstanceId,
                ["generation"] = scope.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), ["index"] = index, ["version"] = version } }, ["relationLimit"] = limit });
        internal static BuildingInspectionCapture Facts(WorldContextSnapshot scope, Query query, bool maximum = false)
        {
            var empty = new BuildingDirectRelations(true, 0, Array.Empty<BuildingRelationEntry>(), null);
            BuildingDirectRelations Rows(bool upgrades) => maximum ? new BuildingDirectRelations(true, int.MaxValue,
                Enumerable.Range(0, query.RelationLimit).Select(_ => new BuildingRelationEntry(new BuildingEntityReference(int.MaxValue, int.MinValue), true,
                    upgrades ? (uint?)uint.MaxValue : null)).ToArray(), "LIMIT") : empty;
            var data = new BuildingInspectionData(new BuildingEntityReference(77, 1),
                BuildingTextFact.Read(() => maximum ? new string('\0', 128) : "风力发电机", 128),
                BuildingTextFact.Read(() => maximum ? new string('\0', 512) : "BuildingPrefab:WindTurbine", 512),
                3, 3, null, 0, null, false, null,
                new double[] { float.MaxValue, -float.MaxValue, 0 }, new double[] { 0, 0, 0, 1 }, null, false, 0, uint.MaxValue, byte.MaxValue,
                Rows(false), Rows(false), maximum ? Rows(true) : BuildingDirectRelations.Missing(),
                new BuildingBoundsFact(new double[] { -12, 0, -12 }, new double[] { 12, 40, 12 }, null));
            return new BuildingInspectionCapture(scope, query, new BuildingEntityReference(query.SelectedInfo ? 42 : query.Index, query.SelectedInfo ? 7 : query.Version),
                maximum ? new string('\0', 64) : "test-game", 1000, 1001, uint.MaxValue, 0, data);
        }
        internal static async Task<int> RunAsync()
        {
            CheckSelectionScopeWitness();
            Program.Stage = "building inspection pure values and bounded coverage";
            var context = Ready(); var query = Exact(context.Read());
            var result = BuildingInspectionQueryService.Encode(Facts(context.Read(), query));
            Check((string?)result["kind"] == "observed" && ContractCatalog.Validate(SchemaIds.BuildingInspection, result).Valid,
                "one exact building encodes under the shared schema without native types");
            Check((uint)result["prefab"]!["baseConstructionCost"]!["value"]! == 0 && !(bool)result["prefab"]!["locked"]!["value"]! &&
                result["selectionSourceUpdatedFrame"]!.Type == JTokenType.Null, "zero cost and false marker are facts while panel refresh time remains unknown");
            Check((bool)result["relations"]!["subObjects"]!["complete"]! && !(bool)result["relations"]!["installedUpgrades"]!["complete"]! &&
                result["relations"]!["installedUpgrades"]!["length"]!.Type == JTokenType.Null,
                "empty observed buffers differ from missing buffers and neither implies a complete transitive facility");
            Check(BuildingTextFact.Read(() => new string('字', 129), 128).Reason == "TOO_LONG" &&
                BuildingTextFact.Read(() => new string('字', 129), 128).Value == null &&
                BuildingTextFact.Read(() => "\ud800", 128).Reason == "INVALID_VALUE", "oversized and invalid native text is unavailable, never shortened");
            foreach (string generation in new[] { "0", "01", "-1", "9223372036854775808" })
            {
                bool rejected = false;
                try { Query.Parse(new JObject { ["target"] = new JObject { ["kind"] = "entityRef", ["ref"] = new JObject {
                    ["instanceId"] = "inspection-instance", ["generation"] = generation, ["index"] = 42, ["version"] = 7 } } }); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "noncanonical or overflowing generations are not alternate loading identities");
            }
            var worstScope = new WorldContextSnapshot(new string('\0', 128), long.MaxValue, "ready", new string('\0', 128));
            var maximum = BuildingInspectionQueryService.Encode(Facts(worstScope, Selected(16), true));
            int bytes = Encoding.UTF8.GetByteCount(maximum.ToString(Formatting.None));
            Check((string?)maximum["kind"] == "observed" && bytes <= BuildingInspectionQueryService.MaximumRawResultBytes,
                "48 maximum relative references and maximum escaped metadata fit the 16KiB raw result budget");
            Check((int)maximum["relations"]!["subObjects"]!["length"]! == int.MaxValue &&
                (int)maximum["relations"]!["subObjects"]!["returned"]! == 16 && !(bool)maximum["relations"]!["subObjects"]!["complete"]!,
                "a known large buffer returns only its declared bounded prefix and retains exact native length");

            Program.Stage = "building selectors, cancellation and real publisher lifetime";
            var publisher = new HeldPublisher(); var broker = new BuildingInspectionReadBroker(context, publisher.Run);
            using (var first = broker.Capture(query))
            using (var second = broker.Capture(Exact(context.Read())))
            {
                using (var different = broker.Capture(Exact(context.Read(), index: 43)))
                    Check((string?) (await different.Completion)["reason"] == "BUSY", "different exact objects are never merged");
                using (var differentLimit = broker.Capture(Exact(context.Read(), limit: 16)))
                    Check((string?)(await differentLimit.Completion)["reason"] == "BUSY", "different relation limits are never merged");
                using (var selected = broker.Capture(Selected()))
                    Check((string?)(await selected.Completion)["reason"] == "BUSY", "panel selection is not treated as an exact-reference alias");
                Check(broker.TryClaim(out var work), "one main-thread owner claims the same-selector cohort");
                first.Dispose(); Check(first.Completion.IsCanceled && !broker.WhenIdle.IsCompleted, "one cancelled waiter cannot release live native work");
                broker.Complete(work!, Facts(work!.Scope, work.Query));
                Check(!second.Completion.IsCompleted && !broker.WhenIdle.IsCompleted, "pure-value encoding remains owned by the scheduled publisher");
                publisher.Release(); await Program.MustComplete(publisher.Published);
                var value = await second.Completion;
                Check((string?)value["kind"] == "observed" && !broker.WhenIdle.IsCompleted, "delivery does not claim the actual publisher task ended");
                publisher.Finish(); await Program.MustComplete(broker.WhenIdle);
                context.EnterLoading();
                Check((string?)broker.GetCurrentResult(value)["reason"] == "SCOPE_CHANGED", "sender acquisition rejects a completed observation after a loading boundary");
            }
            broker.Stop();
            context = Ready(); broker = new BuildingInspectionReadBroker(context);
            using (var pending = broker.Capture(Selected()))
            {
                Check(broker.TryClaim(out var work), "stop test owns the native capture"); broker.Stop();
                Check(pending.Completion.IsCanceled && !broker.WhenIdle.IsCompleted, "stop cancels delivery but cannot release a claimed native capture");
                broker.Fail(work!, "STOPPED"); await Program.MustComplete(broker.WhenIdle);
            }
            context = Ready(); broker = new BuildingInspectionReadBroker(context);
            using (var pending = broker.Capture(Selected()))
            {
                context.EnterLoading();
                Check(!broker.TryClaim(out _), "queued reads do not retarget across a load");
                Check((string?)(await pending.Completion)["reason"] == "SCOPE_CHANGED", "old-scope queued work terminates without a native call");
                await Program.MustComplete(broker.WhenIdle);
            }
            broker.Stop();
            context = Ready(); broker = new BuildingInspectionReadBroker(context);
            using (var notReady = broker.Capture(Selected()))
            {
                broker.TryClaim(out var work); broker.Fail(work!, "SELECTION_SOURCE_NOT_READY");
                Check((string?)(await notReady.Completion)["reason"] == "SELECTION_SOURCE_NOT_READY",
                    "missing selection scope proof remains a precise unavailable result without an object reference");
                await Program.MustComplete(broker.WhenIdle);
            }
            broker.Stop();
            return checks;
        }
        private static void CheckSelectionScopeWitness()
        {
            Program.Stage = "building selection proof never rebinds a prior scope or a retired source callback";
            var context = Ready(); var witness = new BuildingSelectionScopeWitness(context);
            Action? events = null, observed = null; int otherCalls = 0;
            Action other = () => otherCalls++; events += other;
            Action<Action> add = callback => { observed = callback; events += callback; };
            Action<Action> remove = callback => events -= callback;
            var first = context.Read();
            Check(witness.TryBind(first, add, remove) && !witness.HasProof(first), "hot attachment to an already-ready scope does not certify an existing cache");
            Action oldCallback = observed!; Action oldUnsubscribe = () => remove(oldCallback);
            events!(); Check(witness.HasProof(first) && otherCalls == 1, "an observed native signal establishes only the frozen current scope");
            long generation = context.EnterLoading(); context.ObserveReady(generation);
            var next = context.Read(); oldCallback();
            Check(!witness.HasProof(next) && !witness.HasProof(first), "an old subscription cannot stamp the next loaded scope even when object values alias");
            Check(witness.TryBind(next, add, remove) && !witness.HasProof(next), "a newly ready scope retires the old ticket and starts unproven");
            oldCallback(); Check(!witness.HasProof(next), "a retained late callback cannot revive its retired ticket");
            oldUnsubscribe(); events!();
            Check(witness.HasProof(next) && otherCalls == 2, "an old unsubscriber removes only its own delegate and cannot erase a later binding");
            Action? replacementEvents = null; int replacementOtherCalls = 0; replacementEvents += () => replacementOtherCalls++;
            Action previousCallback = observed!;
            Check(witness.TryBind(next, callback => { observed = callback; replacementEvents += callback; }, callback => replacementEvents -= callback),
                "same-scope system replacement creates a new source ticket");
            previousCallback(); Check(!witness.HasProof(next), "the replaced system cannot certify its replacement");
            events!(); Check(otherCalls == 3 && !witness.HasProof(next), "retirement preserves unrelated source listeners and removes the owned listener");
            replacementEvents!(); Check(witness.HasProof(next) && replacementOtherCalls == 1, "only the new source can establish its own current proof");
            Action lateAfterStop = observed!; witness.RequestStop(); lateAfterStop();
            Check(!witness.HasProof(next) && witness.TryDetach() && witness.IsReleased, "stop invalidates callbacks before actual delegate removal");
            replacementEvents!(); Check(replacementOtherCalls == 2 && !witness.HasProof(next), "shutdown preserves all other subscribers without restoring an old list");

            witness = new BuildingSelectionScopeWitness(context); Action? partialEvents = other; Action? partialCallback = null;
            Check(!witness.TryBind(next, callback => { partialCallback = callback; partialEvents += callback; throw new InvalidOperationException(); }, callback => partialEvents -= callback),
                "a partially applied subscription failure is contained within the optional selection source");
            partialCallback!(); partialEvents!();
            Check(witness.IsReleased && !witness.IsBoundTo(next) && !witness.HasProof(next) && otherCalls == 4, "partial registration is detached and cannot certify a scope later");
            bool failRemove = true; int additionalAdds = 0;
            Check(witness.TryBind(next, add, callback => { if (failRemove) throw new InvalidOperationException(); remove(callback); }), "failure test owns one subscription");
            Check(!witness.TryDetach() && !witness.IsReleased && !witness.HasProof(next), "failed physical removal preserves one obligation but invalidates its proof");
            Check(!witness.TryBind(next, _ => additionalAdds++, _ => {}) && additionalAdds == 0,
                "a failed old detach cannot accumulate new subscriptions");
            failRemove = false;
            Check(witness.TryDetach() && witness.IsReleased, "a later explicit cleanup can release the original owned delegate");
            witness.RequestStop();
            Check(!witness.TryBind(next, _ => additionalAdds++, _ => {}) && additionalAdds == 0, "stopped witnesses cannot rebind or restart a source");
        }
        private static WorldContextTracker Ready()
        { var context = new WorldContextTracker("inspection-instance"); long generation = context.EnterLoading(); context.ObserveReady(generation); return context; }
        private sealed class HeldPublisher
        {
            private readonly TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Published => published.Task;
            internal async Task Run(Action action) { await release.Task; try { action(); } finally { published.TrySetResult(true); } await finish.Task; }
            internal void Release() => release.TrySetResult(true);
            internal void Finish() => finish.TrySetResult(true);
        }
    }
}
