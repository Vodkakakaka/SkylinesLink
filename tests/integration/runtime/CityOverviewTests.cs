using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Observation;
using SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    internal static class CityOverviewTests
    {
        private static int checks;
        private static void Check(bool value, string name) { Program.Assert(value, name); checks++; }

        internal static async Task<int> RunAsync()
        {
            CheckMetricFacts();
            CheckSimulationAndClockFacts();
            await CheckScopesAndDelivery();
            await CheckSharedCaptureAndCancellation();
            await CheckStopAndRealPublisher();
            return checks;
        }

        private static void CheckMetricFacts()
        {
            Program.Stage = "city overview preserves zero, missing sources and unknown refresh times";
            var scope = ReadyContext().Read();
            var zero = CityOverviewQueryService.Encode(Facts(scope));
            Check((string?)zero["kind"] == "overview" && ContractCatalog.Validate("urn:skylineslink:contract:city-overview:2", zero).Valid,
                "the fixed observation is a valid public overview");
            var archived = (JObject)zero.DeepClone(); archived.Remove("simulation"); archived.Remove("clock");
            Check(ContractCatalog.Validate("urn:skylineslink:contract:city-overview:1", archived).Valid,
                "the archived first schema still accepts its original population and treasury shape");
            Check((int)zero["population"]!["value"]! == 0 && (string?)zero["population"]!["status"] == "observed" &&
                (int)zero["treasury"]!["value"]! == 0 && (bool)zero["treasury"]!["unlimited"]! == false,
                "native population and money zero remain observed values");
            Check(zero["scope"]!["cityId"]!.Type == JTokenType.Null && zero["population"]!["reason"]!.Type == JTokenType.Null,
                "an unknown persistent city identity does not prevent the loaded-scope observation");
            Check(zero["population"]!["sourceUpdatedFrame"]!.Type == JTokenType.Null &&
                zero["treasury"]!["sourceUpdatedFrame"]!.Type == JTokenType.Null &&
                (uint)zero["capture"]!["simulationFrameStart"]! == 42 && (uint)zero["capture"]!["simulationFrameEnd"]! == 42,
                "a stable sampling frame does not invent source refresh metadata");
            var debt = CityOverviewQueryService.Encode(Facts(scope, treasury: -120));
            Check((int)debt["treasury"]!["value"]! == -120 && (string?)debt["treasury"]!["status"] == "observed",
                "a negative finite treasury is valid game state");
            var unlimited = CityOverviewQueryService.Encode(Facts(scope, treasury: 2000000000, unlimited: true));
            Check((int)unlimited["treasury"]!["value"]! == 2000000000 && (bool)unlimited["treasury"]!["unlimited"]!,
                "the public native unlimited limit retains its mode instead of masquerading as finite money");
            var missing = CityOverviewQueryService.Encode(new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 42, 42,
                null, null, null, SimulationSpeedFact.Unavailable("SOURCE_UNAVAILABLE"),
                GameClockFact.Unavailable("COMPONENT_UNAVAILABLE"), "MISSING_COMPONENT", "INVALID_VALUE"));
            Check((string?)missing["kind"] == "overview" && missing["population"]!["value"]!.Type == JTokenType.Null &&
                (string?)missing["population"]!["status"] == "unavailable" &&
                (string?)missing["population"]!["reason"] == "MISSING_COMPONENT" &&
                missing["treasury"]!["value"]!.Type == JTokenType.Null && missing["treasury"]!["unlimited"]!.Type == JTokenType.Null &&
                (string?)missing["treasury"]!["reason"] == "INVALID_VALUE",
                "unavailable component values retain distinct fixed reasons and actual JSON null");
            Check(Encoding.UTF8.GetByteCount(zero.ToString(Formatting.None)) <= CityOverviewQueryService.MaximumRawResultBytes,
                "one fixed overview fits its declared result budget");
            var worstScope = new WorldContextSnapshot(new string('\0', 128), long.MaxValue, "ready", new string('\0', 128));
            var worst = CityOverviewQueryService.Encode(new CityOverviewCapture(worstScope, new string('\0', 64),
                9007199254740991L, 9007199254740991L, uint.MaxValue, uint.MaxValue, int.MaxValue, int.MinValue, false,
                SimulationSpeedFact.Observed(float.MaxValue, float.MaxValue),
                GameClockFact.Observed(int.MaxValue, 0.99999994f, 0.99999994f, 8191, uint.MaxValue,
                    int.MinValue, byte.MaxValue, byte.MaxValue, byte.MaxValue, uint.MaxValue)));
            Check((string?)worst["kind"] == "overview" &&
                Encoding.UTF8.GetByteCount(worst.ToString(Formatting.None)) <= CityOverviewQueryService.MaximumRawResultBytes,
                "maximum admitted identities with six-byte JSON escapes and numeric bounds fit the fixed result budget");
            Check(ContractCatalog.Validate("urn:skylineslink:contract:city-overview:2", JToken.Parse(worst.ToString(Formatting.None))).Valid,
                "maximum float speed and clock fractions remain valid after real JSON number formatting and parsing");
            Reject(() => Facts(new WorldContextSnapshot("instance", 0, "ready", null)),
                "a manually fabricated ready generation zero is not a valid loaded scope");
            Reject(() => Facts(scope, population: -1), "negative population cannot become an observed count");
            Reject(() => new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 42, 42, null, 0, false,
                    SimulationSpeedFact.Observed(1, 1), Clock()),
                "missing population requires an explicit source reason");
            Reject(() => new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 42, 42, 0, 0, false,
                    SimulationSpeedFact.Observed(1, 1), Clock(), "MISSING_COMPONENT"),
                "observed zero cannot simultaneously be missing");
            Reject(() => new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 42, 42, 0, null, false,
                    SimulationSpeedFact.Observed(1, 1), Clock(), null, "MISSING_COMPONENT"),
                "unavailable money cannot retain a made-up unlimited mode");
        }

        private static void CheckSimulationAndClockFacts()
        {
            Program.Stage = "city overview separates native speed and calendar observations from unknown causes";
            var scope = ReadyContext().Read();
            var zero = CityOverviewQueryService.Encode(new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 0, 0, 0, 0, false,
                SimulationSpeedFact.Observed(0, 0), GameClockFact.Observed(0, 0, 0, 1, 0, -1, 255, 255, 255, 0)));
            Check((string?)zero["kind"] == "overview" && (double)zero["simulation"]!["selectedSpeed"]! == 0 &&
                (bool)zero["simulation"]!["selectedPaused"]! && (double)zero["simulation"]!["smoothSpeed"]! == 0,
                "observed speed zero has selectedPaused true rather than an unavailable source");
            Check((int)zero["clock"]!["year"]! == 0 && (int)zero["clock"]!["daysPerYear"]! == 1 &&
                (double)zero["clock"]!["dayFraction"]! == 0 && (double)zero["clock"]!["yearFraction"]! == 0 &&
                (uint)zero["clock"]!["firstFrame"]! == 0 && (uint)zero["clock"]!["atSimulationFrame"]! == 0,
                "native calendar zero and frame zero remain observed values");
            Check((int)zero["clock"]!["epoch"]!["startingYear"]! == -1 &&
                (int)zero["clock"]!["epoch"]!["startingMonth"]! == 255 &&
                (int)zero["clock"]!["epoch"]!["startingHour"]! == 255 &&
                (int)zero["clock"]!["epoch"]!["startingMinute"]! == 255,
                "native byte epoch fields are preserved without invented Gregorian bounds");
            var selectedZero = CityOverviewQueryService.Encode(Facts(scope, simulation: SimulationSpeedFact.Observed(0, 1.5f)));
            Check((bool)selectedZero["simulation"]!["selectedPaused"]! &&
                selectedZero["simulation"]!["pauseReason"]!.Type == JTokenType.Null &&
                selectedZero["simulation"]!["advancingNow"]!.Type == JTokenType.Null &&
                selectedZero["simulation"]!["sourceUpdatedFrame"]!.Type == JTokenType.Null,
                "a zero selected value and nonzero smooth estimate do not identify a pause cause or actual advancement");
            var nonstandard = CityOverviewQueryService.Encode(Facts(scope, simulation: SimulationSpeedFact.Observed(2.5f, 0)));
            Check((double)nonstandard["simulation"]!["selectedSpeed"]! == 2.5 &&
                !(bool)nonstandard["simulation"]!["selectedPaused"]! &&
                nonstandard["simulation"]!["advancingNow"]!.Type == JTokenType.Null,
                "valid native speed is not restricted to a preset list or treated as evidence of progress");
            foreach (string reason in new[] { "SOURCE_UNAVAILABLE", "INVALID_VALUE" })
            {
                var unavailable = CityOverviewQueryService.Encode(Facts(scope, simulation: SimulationSpeedFact.Unavailable(reason)));
                var simulation = unavailable["simulation"]!;
                Check((string?)simulation["status"] == "unavailable" && (string?)simulation["reason"] == reason &&
                    simulation["selectedSpeed"]!.Type == JTokenType.Null && simulation["smoothSpeed"]!.Type == JTokenType.Null &&
                    simulation["selectedPaused"]!.Type == JTokenType.Null && (string?)simulation["source"] == "simulation-system",
                    "unavailable simulation projects actual null data and preserves its precise fixed source reason");
            }
            foreach (string reason in new[] { "SOURCE_UNAVAILABLE", "COMPONENT_UNAVAILABLE", "INVALID_VALUE" })
            {
                var unavailable = CityOverviewQueryService.Encode(Facts(scope, clock: GameClockFact.Unavailable(reason)));
                var clock = unavailable["clock"]!;
                bool nullData = true;
                foreach (string name in new[] { "year", "dayFraction", "yearFraction", "daysPerYear", "firstFrame", "epoch", "atSimulationFrame" })
                    nullData &= clock[name]!.Type == JTokenType.Null;
                Check((string?)clock["status"] == "unavailable" && (string?)clock["reason"] == reason && nullData &&
                    (int)clock["ticksPerDay"]! == 262144 && (string?)clock["source"] == "native-time-functions" &&
                    clock["sourceUpdatedFrame"]!.Type == JTokenType.Null,
                    "unavailable clock keeps known metadata while every observed data field remains null");
            }
            Check(GameClockFact.Observed(-1, 0.5f, 0.75f, 8191, uint.MaxValue, -1, 0, 0, 0, uint.MaxValue).Year == -1,
                "a negative native year and the maximum safe days-per-year are accepted");
            var wrap = new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, uint.MaxValue, 0, 0, 0, false,
                SimulationSpeedFact.Observed(1, 1), GameClockFact.Observed(2020, 0, 0, 12, 0, 2020, 0, 0, 0, 0));
            Check(wrap.Clock.AtSimulationFrame == 0, "a clock reading inside a wrapping uint frame interval is valid");
            Reject(() => Facts(scope, clock: GameClockFact.Observed(2020, 0, 0, 12, 42, 2020, 0, 0, 0, 43)),
                "a clock from outside its capture interval cannot be presented as this observation");
            Reject(() => new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, uint.MaxValue, 0, 0, 0, false,
                    SimulationSpeedFact.Observed(1, 1), GameClockFact.Observed(2020, 0, 0, 12, 0, 2020, 0, 0, 0, 1)),
                "a frame outside a wrapping interval is rejected rather than assuming numeric monotonicity");
            Reject(() => SimulationSpeedFact.Observed(float.NaN, 0), "non-finite selected speed cannot become an observed fact");
            Reject(() => SimulationSpeedFact.Observed(1, float.PositiveInfinity), "non-finite smooth speed cannot become an observed fact");
            Reject(() => SimulationSpeedFact.Observed(-1, 0), "negative selected speed is rejected without clamping");
            Reject(() => SimulationSpeedFact.Observed(1, -1), "negative smooth speed is rejected without clamping");
            Reject(() => GameClockFact.Observed(2020, 1, 0, 12, 0, 2020, 0, 0, 0, 0),
                "the DTO rejects day fraction one instead of silently normalizing it");
            Reject(() => GameClockFact.Observed(2020, 0, -float.Epsilon, 12, 0, 2020, 0, 0, 0, 0),
                "a negative fraction is not silently repaired by the DTO");
            Reject(() => GameClockFact.Observed(2020, float.NaN, 0, 12, 0, 2020, 0, 0, 0, 0),
                "non-finite native clock fractions are unavailable inputs rather than a fabricated date");
            Reject(() => GameClockFact.Observed(2020, 0, 0, 0, 0, 2020, 0, 0, 0, 0),
                "zero days-per-year cannot be treated as an observed clock");
            Reject(() => GameClockFact.Observed(2020, 0, 0, 8192, 0, 2020, 0, 0, 0, 0),
                "the days-per-year overflow boundary is rejected");
            Reject(() => GameClockFact.Observed(-2, 0, 0, 12, 0, -1, 0, 0, 0, 0),
                "a year preceding the supplied starting year is not silently wrapped");
        }

        private static async Task CheckScopesAndDelivery()
        {
            Program.Stage = "city overview never retargets a queued read across loaded generations";
            var context = new WorldContextTracker("overview-instance");
            var broker = new CityOverviewReadBroker(context);
            using (var menu = broker.Capture()) Check(Reason(await menu.Completion) == "MENU", "menu reads do not wait for or load a city");
            context.EnterLoading();
            using (var loading = broker.Capture()) Check(Reason(await loading.Completion) == "LOADING", "loading has its own unavailable reason");
            context.MarkUnavailable();
            using (var unavailable = broker.Capture()) Check(Reason(await unavailable.Completion) == "SOURCE_UNAVAILABLE", "lost sources do not become zero metrics");
            Ready(context);
            using (var stale = broker.Capture())
            {
                context.EnterLoading();
                Check(!broker.TryClaim(out _), "a queued old-generation read is not claimed against the replacement city");
                await Program.MustComplete(broker.WhenIdle);
                Check(Reason(await stale.Completion) == "SCOPE_CHANGED", "a queued scope change is explicit and ends real pending work");
            }
            context.ObserveReady(context.Read().Generation);
            using (var nativeFailure = broker.Capture())
            {
                Check(broker.TryClaim(out var work), "a current-generation read can be claimed");
                broker.Fail(work!, "SOURCE_UNAVAILABLE"); await Program.MustComplete(broker.WhenIdle);
                Check(Reason(await nativeFailure.Completion) == "SOURCE_UNAVAILABLE", "native source failure remains a classified unavailable result");
            }
            using (var mismatch = broker.Capture())
            {
                Check(broker.TryClaim(out var work), "a fresh read claims the current scope");
                broker.Complete(work!, Facts(new WorldContextSnapshot("another-instance", work!.Scope.Generation, "ready", null)));
                await Program.MustComplete(broker.WhenIdle);
                Check(Reason(await mismatch.Completion) == "SCOPE_CHANGED", "a capture from a different instance cannot satisfy this read");
            }
            var result = CityOverviewQueryService.Encode(Facts(context.Read()));
            Check(ReferenceEquals(broker.GetCurrentResult(result), result), "an unchanged scope keeps the owned result DOM");
            Check(context.ObserveCityIdentity(context.Read().Generation, "first-save-city"), "the current loaded scope may acquire a persistent identity");
            Check(ReferenceEquals(broker.GetCurrentResult(result), result), "same-generation identity enrichment does not invalidate the sampled scope");
            context.EnterLoading();
            Check(Reason(broker.GetCurrentResult(result)) == "SCOPE_CHANGED", "the send-time guard rejects a completed old-city overview");
            context.ObserveReady(context.Read().Generation, "first-save-city");
            Check(Reason(broker.GetCurrentResult(result)) == "SCOPE_CHANGED", "loading the same persistent city still invalidates the earlier generation");
            broker.Stop();
            Check(Reason(broker.GetCurrentResult(result)) == "STOPPED", "stopping prevents late successful publication");
            using (var stopped = broker.Capture()) Check(Reason(await stopped.Completion) == "STOPPED", "stopped brokers never create new native work");
        }

        private static async Task CheckSharedCaptureAndCancellation()
        {
            Program.Stage = "one city overview capture has eight cumulative consumers and independent owned replies";
            var context = ReadyContext(); var scheduler = new HeldPublisher();
            var broker = new CityOverviewReadBroker(context, scheduler.Run);
            using (var cancelled = broker.Capture())
            using (var first = broker.Capture())
            using (var second = broker.Capture())
            {
                Check(broker.TryClaim(out var work) && work != null && work.Scope.Generation == context.Read().Generation,
                    "same-scope consumers share one claimed native capture");
                Check(!broker.TryClaim(out _), "native work can only be claimed once");
                cancelled.Dispose();
                Check(cancelled.Completion.IsCanceled && !first.Completion.IsCompleted && !broker.WhenIdle.IsCompleted,
                    "cancelling one consumer leaves peers and claimed native work alive");
                for (int i = 0; i < 5; i++) broker.Capture().Dispose();
                using (var full = broker.Capture())
                    Check(Reason(await full.Completion) == "BUSY", "cancelled consumers do not recycle any of the cohort's eight slots");
                broker.Complete(work!, Facts(work!.Scope));
                Check(scheduler.Calls == 1 && !first.Completion.IsCompleted && !broker.WhenIdle.IsCompleted,
                    "one publisher owns the frozen capture without doing DOM work on the native caller");
                using (var publishing = broker.Capture())
                    Check(Reason(await publishing.Completion) == "BUSY", "publishing does not append another unbounded reader list");
                scheduler.Release(); await Program.MustComplete(scheduler.Published);
                var a = await first.Completion; var b = await second.Completion;
                Check((string?)a["kind"] == "overview" && (string?)b["kind"] == "overview" && !ReferenceEquals(a, b),
                    "live consumers receive independent ordinary DOMs from the same native facts");
                a["population"]!["value"] = 5;
                Check((int)b["population"]!["value"]! == 0, "one consumer cannot mutate a peer's captured result");
                Check(!broker.WhenIdle.IsCompleted, "publishing values is not proof that its actual worker has returned");
                scheduler.Finish(); await Program.MustComplete(broker.WhenIdle);
            }
            using (var fresh = broker.Capture())
            {
                bool claimed = broker.TryClaim(out var work);
                Check(!fresh.Completion.IsCompleted && claimed, "a later request samples again instead of reading a retained cache");
                broker.Fail(work!, "CAPTURE_FAILED"); await Program.MustComplete(broker.WhenIdle);
                Check(Reason(await fresh.Completion) == "CAPTURE_FAILED", "a later source failure is not replaced by the earlier observation");
            }
            broker.Stop();
        }

        private static async Task CheckStopAndRealPublisher()
        {
            Program.Stage = "city overview shutdown joins actual native capture and publication";
            var context = ReadyContext();
            var pendingBroker = new CityOverviewReadBroker(context);
            using (var pending = pendingBroker.Capture())
            {
                pendingBroker.Stop();
                Check(pending.Completion.IsCanceled && pendingBroker.WhenIdle.IsCompleted && !pendingBroker.TryClaim(out _),
                    "stopping unclaimed work cancels consumers without scheduling native work");
            }
            var claimedScheduler = new HeldPublisher(); var claimedBroker = new CityOverviewReadBroker(context, claimedScheduler.Run);
            using (var claimed = claimedBroker.Capture())
            {
                Check(claimedBroker.TryClaim(out var work), "shutdown case first owns actual native work");
                claimedBroker.Stop();
                Check(claimed.Completion.IsCanceled && !claimedBroker.WhenIdle.IsCompleted,
                    "Stop cannot pretend the claimed native producer has ended");
                claimedBroker.Complete(work!, Facts(work!.Scope)); await Program.MustComplete(claimedBroker.WhenIdle);
                Check(claimedScheduler.Calls == 0, "completed native work with no consumers needs no publisher");
            }
            var scheduler = new HeldPublisher(); var broker = new CityOverviewReadBroker(context, scheduler.Run);
            using (var read = broker.Capture())
            {
                broker.TryClaim(out var work); broker.Complete(work!, Facts(work!.Scope));
                var realIdle = broker.WhenIdle; broker.Stop();
                Check(read.Completion.IsCanceled && !realIdle.IsCompleted, "Stop cancels waiting readers but retains the scheduled publisher");
                scheduler.Release(); await Program.MustComplete(scheduler.Published);
                Check(!realIdle.IsCompleted, "even an abandoned publication remains owned until its actual task ends");
                scheduler.Finish(); await Program.MustComplete(realIdle);
            }
            var failedBroker = new CityOverviewReadBroker(context, _ => Task.FromException(new InvalidOperationException("private test failure")));
            using (var failure = failedBroker.Capture())
            {
                failedBroker.TryClaim(out var work); failedBroker.Complete(work!, Facts(work!.Scope));
                await Program.MustComplete(failedBroker.WhenIdle);
                Check(Reason(await failure.Completion) == "CAPTURE_FAILED", "a failed publication ends consumers with a fixed non-sensitive reason");
            }
            failedBroker.Stop();
        }

        private static WorldContextTracker ReadyContext()
        { var context = new WorldContextTracker("overview-instance"); Ready(context); return context; }
        private static void Ready(WorldContextTracker context)
        { long generation = context.EnterLoading(); context.ObserveReady(generation); }
        private static CityOverviewCapture Facts(WorldContextSnapshot scope, int population = 0, int treasury = 0, bool unlimited = false,
            SimulationSpeedFact? simulation = null, GameClockFact? clock = null) =>
            new CityOverviewCapture(scope, "1.6.2f1", 1000, 1001, 42, 42, population, treasury, unlimited,
                simulation ?? SimulationSpeedFact.Observed(1, 1), clock ?? Clock());
        private static GameClockFact Clock() => GameClockFact.Observed(2020, 0, 0, 12, 42, 2020, 0, 0, 0, 42);
        private static string? Reason(JToken result) => (string?)result["reason"];
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, name);
        }
        private sealed class HeldPublisher
        {
            private readonly TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Calls;
            internal Task Published => published.Task;
            internal async Task Run(Action action)
            {
                Calls++; await release.Task.ConfigureAwait(false);
                try { action(); } finally { published.TrySetResult(true); }
                await finish.Task.ConfigureAwait(false);
            }
            internal void Release() => release.TrySetResult(true);
            internal void Finish() => finish.TrySetResult(true);
        }
    }
}
