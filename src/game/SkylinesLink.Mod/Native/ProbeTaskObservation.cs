#if SKYLINESLINK_INTEGRATION_PROBE
using System.Threading.Tasks;

namespace SkylinesLink.Mod.Native
{
    internal enum ProbeTaskState { Missing, Pending, Completed, Cancelled, Faulted }

    /// <summary>A single task observation reused by one native step and its status snapshot.</summary>
    internal readonly struct ProbeTaskObservation
    {
        private ProbeTaskObservation(ProbeTaskState state, bool? result = null)
        { State = state; Result = result; }

        internal ProbeTaskState State { get; }
        internal bool? Result { get; }
        internal bool IsTerminal => State == ProbeTaskState.Completed || State == ProbeTaskState.Cancelled || State == ProbeTaskState.Faulted;
        internal bool HasTrueResult => State == ProbeTaskState.Completed && Result == true;

        internal static ProbeTaskObservation Capture(Task? task)
        {
            if (task == null) return new ProbeTaskObservation(ProbeTaskState.Missing);
            TaskStatus status = task.Status;
            if (status == TaskStatus.RanToCompletion) return new ProbeTaskObservation(ProbeTaskState.Completed);
            if (status == TaskStatus.Canceled) return new ProbeTaskObservation(ProbeTaskState.Cancelled);
            if (status == TaskStatus.Faulted)
            {
                _ = task.Exception;
                return new ProbeTaskObservation(ProbeTaskState.Faulted);
            }
            return new ProbeTaskObservation(ProbeTaskState.Pending);
        }

        internal static ProbeTaskObservation Capture(Task<bool>? task)
        {
            ProbeTaskObservation observed = Capture((Task?)task);
            return observed.State == ProbeTaskState.Completed
                ? new ProbeTaskObservation(ProbeTaskState.Completed, task!.GetAwaiter().GetResult()) : observed;
        }

        internal string StatusText => State == ProbeTaskState.Completed
            ? (Result.HasValue ? (Result.Value ? "true" : "false") : "completed")
            : State.ToString().ToLowerInvariant();
    }
}
#endif
