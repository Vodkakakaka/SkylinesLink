using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class LoadTransitionTests
    {
        private const string Instance = "load-probe-instance";
        private static readonly LoadTarget B = new LoadTarget("native-save", "asset-b", "content-v1");
        private static readonly LoadTarget BChanged = new LoadTarget("native-save", "asset-b", "content-v2");
        internal static async Task<int> RunAsync()
        {
            Program.Stage = "native load task and evidence ownership";
            int checks = 0;
            void Check(bool value, string name) { Program.Assert(value, name); checks++; }
            var context = new WorldContextTracker(Instance);
            var native = NewCompletion();
            int calls = 0;
            long bGeneration = 0;
            var gate = new NativeLoadGate(Instance, _ =>
            { calls++; bGeneration = context.EnterLoading(); return NativeLoadInvocation.Started(native.Task, B); });
            var load = new LoadTransition(new LoadIntent("menu-b", B, context.Read()));
            load.Start(gate);
            Check(calls == 1 && gate.HasInFlight && context.Read().State == "loading", "menu without World admits one native load");
            Check(load.Read(context.Read()).PhysicalTaskState == NativeLoadTaskState.Running, "submitted native Task remains running");
            Program.Expect<InvalidOperationException>(() => load.Start(gate)); checks++;
            Check(gate.TryBegin(new LoadIntent("second-client", B, context.Read())).Submission == LoadSubmission.Busy && calls == 1,
                "second client cannot overlap native Task");

            // 客户端只取消等待，不持有原生 Task 的 CancellationTokenSource。
            using (var disconnected = new CancellationTokenSource())
            {
                var waitEnded = NewCompletion();
                using (disconnected.Token.Register(() => waitEnded.TrySetCanceled()))
                {
                    disconnected.Cancel();
                    Check(await Task.WhenAny(load.NativeObservationCompletion, waitEnded.Task) == waitEnded.Task && gate.HasInFlight,
                        "cancelled client wait does not release native slot");
                }
            }
            long cGeneration = context.EnterLoading();
            context.ObserveReady(cGeneration, "same-city");
            load.EndObservation(LoadObservationEnd.Interrupted);
            Check(gate.HasInFlight && load.Read(context.Read()).ObservationEnd == LoadObservationEnd.Interrupted,
                "player load and observation end leave native Task owned");
            native.TrySetResult(true);
            await Program.MustComplete(load.NativeObservationCompletion);
            Check(!gate.HasInFlight && load.Read(context.Read()).PhysicalTaskState == NativeLoadTaskState.RanToCompletion &&
                !load.Read(context.Read()).HistoricalCompletionProven, "native termination releases slot without inventing target evidence");
            Check(!load.ObserveReady(Ready(bGeneration, B, "menu-b")) && !context.ObserveReady(bGeneration, "same-city") &&
                context.Read().Generation == cGeneration, "late B notification cannot make current C ready as B");

            context = new WorldContextTracker(Instance);
            int completedCalls = 0;
            var completedGate = new NativeLoadGate(Instance, _ =>
            { completedCalls++; return NativeLoadInvocation.Started(Task.FromResult(true), B); });
            var completed = new LoadTransition(new LoadIntent("completed-b", B, context.Read()));
            completed.Start(completedGate);
            await Program.MustComplete(completed.NativeObservationCompletion);
            Check(!completedGate.HasInFlight && !completed.Read(context.Read()).HistoricalCompletionProven,
                "already-complete native Task frees slot while readiness remains unknown");
            Check(completed.Read(context.Read()).LoadReturnValue == true, "true native return remains explicit before any ready observation");
            LoadStart next = completedGate.TryBegin(new LoadIntent("next-product-load", B, context.Read()));
            Check(next.Submission == LoadSubmission.Submitted && completedCalls == 2, "pending semantic observation does not occupy physical slot");
            await Program.MustComplete(next.NativeCompletion!);
            bGeneration = context.EnterLoading();
            context.ObserveReady(bGeneration, "same-city");
            completed.ObserveReady(Ready(bGeneration, B, "completed-b"));
            Check(completed.Read(context.Read()).HistoricalCompletionProven && completed.Read(context.Read()).CurrentTargetProven,
                "bound version plus correlated ready plus actual success close historical evidence");
            long replacement = context.EnterLoading();
            context.ObserveReady(replacement, "same-city");
            completed.ObserveReady(Ready(replacement, BChanged, null));
            Check(replacement != bGeneration && completed.Read(context.Read()).HistoricalCompletionProven &&
                !completed.Read(context.Read()).CurrentTargetProven && completed.Read(context.Read()).TargetMatch == LoadTargetMatch.Different,
                "same city and reused World do not merge different save content generations");
            Check(!context.ObserveReady(bGeneration, "same-city"), "old generation never regains world authority");

            var negativeContext = new WorldContextTracker(Instance);
            var negative = new LoadTransition(new LoadIntent("negative-native", B, negativeContext.Read()));
            var negativeGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(Task.FromResult(false), B));
            negative.Start(negativeGate); await Program.MustComplete(negative.NativeObservationCompletion);
            long negativeGeneration = negativeContext.EnterLoading(); negativeContext.ObserveReady(negativeGeneration, "same-city");
            negative.ObserveReady(Ready(negativeGeneration, B, "negative-native"));
            Check(!negativeGate.HasInFlight && negative.Read(negativeContext.Read()).PhysicalTaskState == NativeLoadTaskState.RanToCompletion &&
                negative.Read(negativeContext.Read()).LoadReturnValue == false && negative.Read(negativeContext.Read()).CorrelatedTargetReady &&
                !negative.Read(negativeContext.Read()).HistoricalCompletionProven, "false is a completed negative result even when separate ready evidence matches");
            var unbound = new LoadTransition(new LoadIntent("unbound-native", B, negativeContext.Read()));
            unbound.Start(new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(Task.FromResult(false), null)));
            await Program.MustComplete(unbound.NativeObservationCompletion);
            Check(unbound.BoundTarget == null && unbound.Submission == LoadSubmission.Submitted && unbound.Read(negativeContext.Read()).LoadReturnValue == false,
                "actual false-returning call needs no invented bound target and is not NotStarted");
            Program.Expect<ArgumentException>(() => new NativeLoadCompletion(NativeLoadTaskState.RanToCompletion, null)); checks++;
            Program.Expect<ArgumentException>(() => new NativeLoadCompletion(NativeLoadTaskState.Faulted, false)); checks++;

            context = new WorldContextTracker(Instance);
            var uncorrelated = new LoadTransition(new LoadIntent("uncorrelated", B, context.Read()));
            uncorrelated.Start(completedGate); await Program.MustComplete(uncorrelated.NativeObservationCompletion);
            long generation = context.EnterLoading(); context.ObserveReady(generation, "same-city");
            uncorrelated.ObserveReady(Ready(generation, B, null));
            Check(uncorrelated.Read(context.Read()).CurrentTargetProven && !uncorrelated.Read(context.Read()).HistoricalCompletionProven,
                "target currently matches but same asset notification alone proves no causality");
            Check(!uncorrelated.ObserveReady(new LoadReadyObservation(new WorldContextSnapshot("other-instance", generation, "ready", "same-city"), B, "uncorrelated")),
                "foreign instance observation rejected");

            var versionless = new LoadTarget("native-save", "asset-b", null);
            var noVersionGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(Task.FromResult(true), versionless));
            var noVersion = new LoadTransition(new LoadIntent("version-unverified", versionless, context.Read()));
            noVersion.Start(noVersionGate); await Program.MustComplete(noVersion.NativeObservationCompletion);
            generation = context.EnterLoading(); context.ObserveReady(generation, "same-city");
            noVersion.ObserveReady(Ready(generation, B, "version-unverified"));
            Check(noVersion.Read(context.Read()).TargetMatch == LoadTargetMatch.IdentityOnly && !noVersion.Read(context.Read()).HistoricalCompletionProven,
                "observed content cannot invent missing submission version binding");
            var boundVersion = new LoadTransition(new LoadIntent("bound-at-submit", versionless, context.Read()));
            boundVersion.Start(completedGate); await Program.MustComplete(boundVersion.NativeObservationCompletion);
            generation = context.EnterLoading(); context.ObserveReady(generation, "same-city");
            boundVersion.ObserveReady(Ready(generation, B, "bound-at-submit"));
            Check(boundVersion.Read(context.Read()).HistoricalCompletionProven, "trusted submission may bind previously unavailable content version");
            var changedGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(Task.FromResult(true), BChanged));
            var changed = new LoadTransition(new LoadIntent("changed-at-submit", B, context.Read()));
            changed.Start(changedGate); await Program.MustComplete(changed.NativeObservationCompletion);
            generation = context.EnterLoading(); context.ObserveReady(generation, "same-city");
            changed.ObserveReady(Ready(generation, B, "changed-at-submit"));
            Check(!changed.Read(context.Read()).HistoricalCompletionProven, "request expected version is not substituted for different submitted content");
            var stale = new LoadTransition(new LoadIntent("old-ready", B, context.Read()));
            stale.Start(completedGate); await Program.MustComplete(stale.NativeObservationCompletion);
            stale.ObserveReady(Ready(generation, B, "old-ready"));
            Check(stale.Read(context.Read()).CurrentTargetProven && !stale.Read(context.Read()).HistoricalCompletionProven,
                "source generation ready cannot be promoted into fresh load completion");

            var expiredNative = NewCompletion();
            var expiredGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(expiredNative.Task, B));
            var expired = new LoadTransition(new LoadIntent("expired-observation", B, context.Read()));
            expired.Start(expiredGate); expired.EndObservation(LoadObservationEnd.Expired);
            Check(expiredGate.HasInFlight && !expired.NativeObservationCompletion.IsCompleted, "observation deadline is not native completion");
            expiredNative.TrySetCanceled(); await Program.MustComplete(expired.NativeObservationCompletion);
            Check(!expiredGate.HasInFlight && expired.Read(context.Read()).PhysicalTaskState == NativeLoadTaskState.Cancelled && expired.Read(context.Read()).LoadReturnValue == null,
                "actual cancelled native Task is terminal without claiming rollback or ready");
            var faultGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.Started(Task.FromException<bool>(new OperationCanceledException()), B));
            var faulted = new LoadTransition(new LoadIntent("faulted-native", B, context.Read()));
            faulted.Start(faultGate); await Program.MustComplete(faulted.NativeObservationCompletion);
            Check(!faultGate.HasInFlight && faulted.Read(context.Read()).PhysicalTaskState == NativeLoadTaskState.Faulted && faulted.Read(context.Read()).LoadReturnValue == null,
                "faulted Task throwing cancellation exception remains faulted");

            int uncertainCalls = 0;
            var unknownGate = new NativeLoadGate(Instance, _ => { uncertainCalls++; throw new IOException("Synthetic lost submission handle."); });
            var unknown = new LoadTransition(new LoadIntent("unknown-submission", B, context.Read()));
            unknown.Start(unknownGate);
            Check(unknownGate.HasInFlight && unknownGate.SubmissionIsUnknown && unknown.Read(context.Read()).Submission == LoadSubmission.Unknown,
                "submission exception without Task remains owned and unknown");
            Check(unknownGate.TryBegin(new LoadIntent("retry-unknown", B, context.Read())).Submission == LoadSubmission.Busy && uncertainCalls == 1,
                "unknown submission cannot be blindly retried");
            unknownGate.StopAccepting();
            Check(unknownGate.HasInFlight && !unknown.NativeObservationCompletion.IsCompleted && context.Read().State == "ready",
                "stopping product admission neither releases unknown work nor blocks current queries");
            var notStartedGate = new NativeLoadGate(Instance, _ => NativeLoadInvocation.NotStarted());
            Check(notStartedGate.TryBegin(new LoadIntent("known-not-started", B, context.Read())).Submission == LoadSubmission.NotStarted && !notStartedGate.HasInFlight,
                "verified not-started releases provisional slot");
            int foreignCalls = 0;
            var instanceGate = new NativeLoadGate(Instance, _ => { foreignCalls++; return NativeLoadInvocation.NotStarted(); });
            Check(instanceGate.TryBegin(new LoadIntent("foreign-source", B, new WorldContextTracker("other-instance").Read())).Submission == LoadSubmission.NotStarted && foreignCalls == 0,
                "foreign source instance cannot enter native submission");

            var concurrentTask = NewCompletion();
            int concurrentCalls = 0;
            var concurrentGate = new NativeLoadGate(Instance, _ =>
            { Interlocked.Increment(ref concurrentCalls); return NativeLoadInvocation.Started(concurrentTask.Task, B); });
            var results = await Task.WhenAll(Task.Run(() => concurrentGate.TryBegin(new LoadIntent("client-one", B, context.Read()))),
                Task.Run(() => concurrentGate.TryBegin(new LoadIntent("client-two", B, context.Read()))));
            Check(concurrentCalls == 1 && ((results[0].Submission == LoadSubmission.Submitted && results[1].Submission == LoadSubmission.Busy) ||
                (results[1].Submission == LoadSubmission.Submitted && results[0].Submission == LoadSubmission.Busy)), "two concurrent clients cause exactly one native invocation");
            concurrentGate.StopAccepting();
            Check(concurrentGate.HasInFlight, "stopping admission preserves live native handle");
            concurrentTask.TrySetResult(true);
            await Program.MustComplete((results[0].NativeCompletion ?? results[1].NativeCompletion)!);
            Check(!concurrentGate.HasInFlight && concurrentGate.TryBegin(new LoadIntent("after-stop", B, context.Read())).Submission == LoadSubmission.Stopped,
                "actual terminal clears slot while stopped owner stays stopped");
            return checks;
        }

        private static TaskCompletionSource<bool> NewCompletion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static LoadReadyObservation Ready(long generation, LoadTarget target, string? correlation) =>
            new LoadReadyObservation(new WorldContextSnapshot(Instance, generation, "ready", "same-city"), target, correlation);
    }
}
