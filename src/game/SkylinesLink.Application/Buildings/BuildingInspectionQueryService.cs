using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Buildings
{
    public static class BuildingInspectionQueryService
    {
        public const int MaximumRawResultBytes = 16 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static JObject Encode(BuildingInspectionCapture capture)
        {
            var scope = new JObject { ["instanceId"] = capture.Scope.InstanceId,
                ["generation"] = capture.Scope.Generation.ToString(CultureInfo.InvariantCulture), ["cityId"] = Value(capture.Scope.CityId) };
            var result = new JObject { ["kind"] = capture.Data == null ? "unsupported" : "observed", ["scope"] = scope, ["object"] = Reference(capture.Reference) };
            if (capture.Data == null) result["reason"] = capture.UnsupportedReason;
            else
            {
                var data = capture.Data;
                result["source"] = "native-ecs-building"; result["selectionSource"] = capture.Query.SelectedInfo ? "selectedInfo" : "entityRef";
                result["selectionSourceUpdatedFrame"] = JValue.CreateNull();
                result["gameVersion"] = capture.GameVersion; result["relationLimit"] = capture.Query.RelationLimit;
                result["capture"] = new JObject { ["startedAtUnixMs"] = capture.StartedAtUnixMs, ["endedAtUnixMs"] = capture.EndedAtUnixMs,
                    ["simulationFrameStart"] = capture.FrameStart, ["simulationFrameEnd"] = capture.FrameEnd };
                result["prefab"] = new JObject { ["ref"] = Reference(data.Prefab), ["name"] = Field(data.Name.Value, data.Name.Reason),
                    ["nativeId"] = Field(data.NativeId.Value, data.NativeId.Reason), ["lotCells"] = new JObject {
                        ["width"] = Value(data.LotWidth), ["depth"] = Value(data.LotDepth), ["reason"] = Value(data.LotIssue) },
                    ["baseConstructionCost"] = Field(data.BaseCost, data.CostIssue), ["locked"] = Field(data.Locked, data.LockedIssue) };
                result["prefab"]!["prefabLocalBounds"] = new JObject { ["coordinateSystem"] = "asset-local-meters",
                    ["min"] = data.PrefabLocalBounds.Min == null ? (JToken)JValue.CreateNull() : new JArray(data.PrefabLocalBounds.Min),
                    ["max"] = data.PrefabLocalBounds.Max == null ? (JToken)JValue.CreateNull() : new JArray(data.PrefabLocalBounds.Max),
                    ["reason"] = Value(data.PrefabLocalBounds.Reason) };
                result["transform"] = new JObject { ["position"] = new JArray(data.Position), ["rotation"] = new JArray(data.Rotation),
                    ["positionUnit"] = "world-meters", ["rotationConvention"] = "quaternion-xyzw" };
                result["building"] = new JObject { ["roadEdge"] = Reference(data.RoadEdge), ["roadEdgeExists"] = data.RoadEdgeExists,
                    ["curvePosition"] = data.CurvePosition, ["optionMask"] = data.OptionMask, ["flags"] = data.Flags };
                result["relations"] = new JObject { ["subObjects"] = Relations(data.SubObjects), ["subNets"] = Relations(data.SubNets),
                    ["installedUpgrades"] = Relations(data.InstalledUpgrades) };
            }
            if (!ContractCatalog.Validate("urn:skylineslink:contract:building-inspection:1", result).Valid ||
                Utf8.GetByteCount(result.ToString(Formatting.None)) > MaximumRawResultBytes) return Unavailable("CAPTURE_FAILED");
            return result;
        }
        public static JObject Unavailable(string reason) => new JObject { ["kind"] = "unavailable", ["reason"] = NormalizeReason(reason) };
        internal static string NormalizeReason(string reason)
        {
            switch (reason) {
                case "MENU": case "LOADING": case "SOURCE_UNAVAILABLE": case "SCOPE_CHANGED": case "CONTEXT_MISMATCH":
                case "ENTITY_UNAVAILABLE": case "NO_SELECTION": case "SELECTION_CHANGED": case "BUSY": case "STOPPED": return reason;
                case "SELECTION_SOURCE_NOT_READY": return reason;
                default: return "CAPTURE_FAILED";
            }
        }
        private static JObject Field(object? value, string? reason) => new JObject { ["value"] = Value(value), ["reason"] = Value(reason) };
        private static JToken Value(object? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static JToken Reference(BuildingEntityReference? value) => value == null ? (JToken)JValue.CreateNull() :
            new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JObject Relations(BuildingDirectRelations value) => new JObject {
            ["present"] = Value(value.Present), ["length"] = Value(value.Length), ["returned"] = value.Entries.Count,
            ["complete"] = value.Complete, ["reason"] = Value(value.Reason), ["entries"] = new JArray(value.Entries.Select(entry =>
                new JObject { ["ref"] = Reference(entry.Reference), ["exists"] = entry.Exists, ["optionMask"] = Value(entry.OptionMask) })) };
    }
}
