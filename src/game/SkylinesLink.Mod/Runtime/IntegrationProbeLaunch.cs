using System;
using System.Collections.Generic;

namespace SkylinesLink.Mod.Runtime
{
    // A per-launch development marker, never a persisted game/Steam setting.
    // This does not implement or authorize the product's MCP silent-start mode.
    internal static class IntegrationProbeLaunch
    {
        internal const string Prefix = "--skylineslink-dev-probe=";
        internal static bool TryReadRunId(IReadOnlyList<string> arguments, out string runId)
        {
            runId = "";
            string? requested = null;
            foreach (string argument in arguments)
            {
                if (argument == null || !argument.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                string candidate = argument.Substring(Prefix.Length);
                if (requested != null || !Guid.TryParseExact(candidate, "N", out var parsed) ||
                    parsed.ToString("N") != candidate) return false;
                requested = candidate;
            }
            if (requested == null) return false;
            runId = requested; return true;
        }
    }
}
