#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;

namespace SkylinesLink.Mod.Native
{
    // Intentional probe rejections carry a code across component boundaries.
    // Other SDK exceptions remain unknown; their messages are never codes.
    internal interface INativeProbeFailure
    {
        string Code { get; }
    }

    internal static class NativeProbeFailure
    {
        internal static string? CodeOf(Exception error)
        {
            while (true)
            {
                if (error is INativeProbeFailure rejected) return rejected.Code;
                if (error is TargetInvocationException invocation && invocation.InnerException != null)
                    error = invocation.InnerException;
                else return null;
            }
        }
    }
}
#endif
