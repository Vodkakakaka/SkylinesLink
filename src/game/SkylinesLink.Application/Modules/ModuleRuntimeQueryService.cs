using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Modules
{
    /// <summary>只投影已经拥有的普通值；小页不保留父观察或原生对象。</summary>
    public static class ModuleRuntimeQueryService
    {
        public const int MaximumRawResultBytes = 24 * 1024;
        internal const long RetentionMilliseconds = 300000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static JObject Unavailable(string reason)
        {
            switch (reason)
            {
                case "SOURCE_UNAVAILABLE": case "BUSY": case "REGISTRY_CHANGED": case "CAPTURE_FAILED":
                case "STOPPED": case "SNAPSHOT_UNAVAILABLE": case "PAGE_OUT_OF_RANGE": break;
                default: reason = "CAPTURE_FAILED"; break;
            }
            return new JObject { ["kind"] = "unavailable", ["reason"] = reason };
        }

        internal static JToken EncodePage(ModulePage page)
        {
            var entries = new JArray();
            foreach (var row in page.Rows)
            {
                var issues = new JArray();
                foreach (var issue in row.FieldIssues)
                    issues.Add(new JObject { ["field"] = issue.Field, ["reason"] = issue.Reason });
                entries.Add(new JObject { ["ordinal"] = row.Ordinal, ["assetGuid"] = Value(row.AssetGuid),
                    ["isMod"] = Value(row.IsMod), ["loaderStateCode"] = Value(row.LoaderStateCode), ["loaderState"] = row.LoaderState,
                    ["assemblyPresent"] = Value(row.AssemblyPresent), ["assemblyName"] = Value(row.AssemblyName),
                    ["assemblyVersion"] = Value(row.AssemblyVersion), ["instanceCount"] = Value(row.InstanceCount),
                    ["hasLoadError"] = Value(row.HasLoadError), ["databaseKind"] = row.DatabaseKind, ["fieldIssues"] = issues });
            }
            var result = new JObject { ["kind"] = "page", ["snapshot"] = new JObject {
                ["snapshotId"] = page.SnapshotId, ["instanceId"] = page.InstanceId,
                ["captureStartedAtUnixMs"] = page.Started, ["captureEndedAtUnixMs"] = page.Ended,
                ["expiresAtUnixMs"] = page.Expires, ["gameVersion"] = Value(page.GameVersion),
                ["gameVersionIssue"] = Value(page.GameVersionIssue),
                ["firstInitializationComplete"] = page.Initialized, ["restartRequired"] = page.RestartRequired,
                ["coverage"] = new JObject { ["complete"] = page.Complete, ["reason"] = Value(page.Reason),
                    ["visited"] = page.Visited, ["observedCount"] = page.Observed,
                    ["totalCount"] = page.Complete ? (JToken)new JValue(page.Observed) : JValue.CreateNull() } },
                ["offset"] = page.Offset, ["entries"] = entries, ["returnedCount"] = page.Rows.Count,
                ["nextOffset"] = page.Offset + page.Rows.Count < page.Observed
                    ? (JToken)new JValue(page.Offset + page.Rows.Count) : JValue.CreateNull() };
            if (!ContractCatalog.Validate("urn:skylineslink:contract:module-runtime-page:1", result).Valid ||
                Utf8.GetByteCount(result.ToString(Formatting.None)) > MaximumRawResultBytes)
                return Unavailable("CAPTURE_FAILED");
            return result;
        }
        // Newtonsoft的隐式null string会成为String类型JValue；边界须实际JSON null。
        private static JToken Value(object? value) => value == null ? JValue.CreateNull() : new JValue(value);

        internal sealed class ModulePage
        {
            internal ModulePage(string snapshotId, string instanceId, ModuleRegistryCapture capture, int offset)
            {
                SnapshotId = snapshotId; InstanceId = instanceId; Started = capture.CaptureStartedAtUnixMs;
                Ended = capture.CaptureEndedAtUnixMs; Expires = checked(Ended + RetentionMilliseconds);
                GameVersion = capture.GameVersion; GameVersionIssue = capture.GameVersionIssue;
                Initialized = capture.FirstInitializationComplete; RestartRequired = capture.RestartRequired;
                Complete = capture.Complete; Reason = capture.Reason; Visited = capture.Visited;
                Observed = capture.Entries.Count; Offset = offset;
                var rows = new List<ModuleRuntimeFact>(ModuleRegistryCapture.PageSize);
                for (int index = offset; index < Observed && rows.Count < ModuleRegistryCapture.PageSize; index++)
                    rows.Add(capture.Entries[index]);
                Rows = rows.AsReadOnly();
            }
            internal readonly string SnapshotId, InstanceId;
            internal readonly long Started, Ended, Expires;
            internal readonly string? GameVersion, GameVersionIssue, Reason;
            internal readonly bool Initialized, RestartRequired, Complete;
            internal readonly int Visited, Observed, Offset;
            internal readonly IReadOnlyList<ModuleRuntimeFact> Rows;
        }
    }
}
