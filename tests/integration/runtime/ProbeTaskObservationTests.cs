using System;
using System.Threading.Tasks;
using SkylinesLink.Mod.Native;

namespace SkylinesLink.RuntimeProbe
{
    internal static class ProbeTaskObservationTests
    {
        internal static int Run()
        {
            Program.Stage = "one task observation per native step";
            int checks = 0;
            void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }

            var missing = ProbeTaskObservation.Capture((Task?)null);
            Check(missing.State == ProbeTaskState.Missing && !missing.IsTerminal,
                "a missing handle does not prove an attempted native call finished");

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedBeforeCompletion = ProbeTaskObservation.Capture(completion.Task);
            completion.SetResult(true);
            Check(observedBeforeCompletion.State == ProbeTaskState.Pending && !observedBeforeCompletion.IsTerminal
                && observedBeforeCompletion.Result == null && observedBeforeCompletion.StatusText == "pending",
                "completion after capture does not change this step into a terminal decision");
            var observedNextStep = ProbeTaskObservation.Capture(completion.Task);
            Check(observedNextStep.IsTerminal && observedNextStep.HasTrueResult && observedNextStep.StatusText == "true",
                "the next step can use the actual completed boolean result");

            var rejected = ProbeTaskObservation.Capture(Task.FromResult(false));
            Check(rejected.State == ProbeTaskState.Completed && rejected.Result == false && !rejected.HasTrueResult
                && rejected.StatusText == "false", "a completed false result is not a successful native action");

            var queueWrapper = ProbeTaskObservation.Capture((Task)Task.FromResult(false));
            Check(queueWrapper.State == ProbeTaskState.Completed && queueWrapper.Result == null
                && queueWrapper.StatusText == "completed", "queue completion does not expose or invent a native result");

            var cancelled = new TaskCompletionSource<bool>(); cancelled.SetCanceled();
            var cancellation = ProbeTaskObservation.Capture(cancelled.Task);
            Check(cancellation.State == ProbeTaskState.Cancelled && cancellation.IsTerminal
                && !cancellation.HasTrueResult, "cancelled tasks remain distinguishable from false and success");

            var faulted = new TaskCompletionSource<bool>(); faulted.SetException(new InvalidOperationException("fixture"));
            var failure = ProbeTaskObservation.Capture(faulted.Task);
            Check(failure.State == ProbeTaskState.Faulted && failure.IsTerminal && failure.Result == null,
                "faulted task observation retains failure without throwing through the native step");
            Check(ProbeTaskObservation.Capture(Task.CompletedTask).State == ProbeTaskState.Completed,
                "ordinary completion is observed without a fabricated boolean result");
            return checks;
        }
    }
}
