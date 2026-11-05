using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Observation
{
    /// <summary>城市概览的固定字段投影；不查询原生来源或推断统计刷新时点。</summary>
    public static class CityOverviewQueryService
    {
        public const int MaximumRawResultBytes = 8 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static JObject Encode(CityOverviewCapture capture)
        {
            var result = new JObject {
                ["kind"] = "overview",
                ["scope"] = new JObject {
                    ["instanceId"] = capture.Scope.InstanceId,
                    ["generation"] = capture.Scope.Generation.ToString(CultureInfo.InvariantCulture),
                    ["cityId"] = Value(capture.Scope.CityId)
                },
                ["gameVersion"] = capture.GameVersion,
                ["capture"] = new JObject {
                    ["startedAtUnixMs"] = capture.StartedAtUnixMs, ["endedAtUnixMs"] = capture.EndedAtUnixMs,
                    ["simulationFrameStart"] = capture.SimulationFrameStart, ["simulationFrameEnd"] = capture.SimulationFrameEnd
                },
                ["coverage"] = "city",
                ["population"] = Metric(capture.Population, "residents", "city-population-component", capture.PopulationIssue),
                ["treasury"] = Metric(capture.Treasury, "game-currency", "player-money-component", capture.TreasuryIssue),
                ["simulation"] = Simulation(capture.Simulation),
                ["clock"] = Clock(capture.Clock)
            };
            result["treasury"]!["unlimited"] = Value(capture.Unlimited);
            if (!ContractCatalog.Validate("urn:skylineslink:contract:city-overview:2", result).Valid ||
                Utf8.GetByteCount(result.ToString(Formatting.None)) > MaximumRawResultBytes)
                return Unavailable("CAPTURE_FAILED");
            return result;
        }

        public static JObject Unavailable(string reason) => new JObject {
            ["kind"] = "unavailable", ["reason"] = NormalizeReason(reason)
        };

        internal static string NormalizeReason(string reason)
        {
            switch (reason)
            {
                case "MENU": case "LOADING": case "SOURCE_UNAVAILABLE": case "SCOPE_CHANGED":
                case "BUSY": case "CAPTURE_FAILED": case "STOPPED": return reason;
                default: return "CAPTURE_FAILED";
            }
        }

        private static JObject Metric(int? value, string unit, string source, string? issue) => new JObject {
            ["status"] = value.HasValue ? "observed" : "unavailable", ["value"] = Value(value),
            ["unit"] = unit, ["source"] = source, ["sourceUpdatedFrame"] = JValue.CreateNull(), ["reason"] = Value(issue)
        };

        private static JObject Simulation(SimulationSpeedFact fact) => new JObject {
            ["status"] = fact.Available ? "observed" : "unavailable",
            ["selectedSpeed"] = FloatValue(fact.Available, fact.SelectedSpeed),
            ["selectedPaused"] = Value(fact.Available ? (bool?)(fact.SelectedSpeed == 0) : null),
            ["smoothSpeed"] = FloatValue(fact.Available, fact.SmoothSpeed),
            ["pauseReason"] = JValue.CreateNull(), ["advancingNow"] = JValue.CreateNull(),
            ["source"] = "simulation-system", ["sourceUpdatedFrame"] = JValue.CreateNull(), ["reason"] = Value(fact.Reason)
        };

        private static JObject Clock(GameClockFact fact) => new JObject {
            ["status"] = fact.Available ? "observed" : "unavailable",
            ["year"] = Value(fact.Available ? (int?)fact.Year : null),
            ["dayFraction"] = FloatValue(fact.Available, fact.DayFraction),
            ["yearFraction"] = FloatValue(fact.Available, fact.YearFraction),
            ["daysPerYear"] = Value(fact.Available ? (int?)fact.DaysPerYear : null),
            ["ticksPerDay"] = 262144,
            ["firstFrame"] = Value(fact.Available ? (uint?)fact.FirstFrame : null),
            ["epoch"] = fact.Available ? new JObject {
                ["startingYear"] = fact.StartingYear, ["startingMonth"] = fact.StartingMonth,
                ["startingHour"] = fact.StartingHour, ["startingMinute"] = fact.StartingMinute
            } : (JToken)JValue.CreateNull(),
            ["atSimulationFrame"] = Value(fact.Available ? (uint?)fact.AtSimulationFrame : null),
            ["source"] = "native-time-functions", ["sourceUpdatedFrame"] = JValue.CreateNull(), ["reason"] = Value(fact.Reason)
        };

        // Widen exactly before JSON formatting: float's short decimal spelling
        // can otherwise round beyond its actual value at schema boundaries.
        private static JToken FloatValue(bool available, float value) => available ? new JValue((double)value) : JValue.CreateNull();

        // Nullable value/string implicit conversions must remain actual JSON null.
        private static JToken Value(object? value) => value == null ? JValue.CreateNull() : new JValue(value);
    }
}
