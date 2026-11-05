using System;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Application.Observation
{
    /// <summary>一次原生读取冻结的城市普通值；采样帧不冒充各来源的最后更新帧。</summary>
    public sealed class CityOverviewCapture
    {
        private const long MaximumJsonInteger = 9007199254740991L;

        public CityOverviewCapture(WorldContextSnapshot scope, string gameVersion,
            long startedAtUnixMs, long endedAtUnixMs, uint simulationFrameStart, uint simulationFrameEnd,
            int? population, int? treasury, bool? unlimited, SimulationSpeedFact simulation, GameClockFact clock,
            string? populationIssue = null, string? treasuryIssue = null)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.State != "ready" || scope.Generation <= 0 || !ValidText(scope.InstanceId, 128) ||
                scope.CityId != null && !ValidText(scope.CityId, 128))
                throw new ArgumentException("A ready loaded scope is required.", nameof(scope));
            if (!ValidText(gameVersion, 64)) throw new ArgumentException("A bounded game version is required.", nameof(gameVersion));
            if (startedAtUnixMs < 0 || startedAtUnixMs > MaximumJsonInteger)
                throw new ArgumentOutOfRangeException(nameof(startedAtUnixMs));
            if (endedAtUnixMs < startedAtUnixMs || endedAtUnixMs > MaximumJsonInteger)
                throw new ArgumentOutOfRangeException(nameof(endedAtUnixMs));
            if (population < 0) throw new ArgumentOutOfRangeException(nameof(population));
            RequireIssue(population.HasValue, populationIssue, nameof(populationIssue));
            RequireIssue(treasury.HasValue, treasuryIssue, nameof(treasuryIssue));
            if (treasury.HasValue != unlimited.HasValue)
                throw new ArgumentException("Money and its unlimited mode must share source availability.", nameof(unlimited));

            Scope = scope; GameVersion = gameVersion;
            StartedAtUnixMs = startedAtUnixMs; EndedAtUnixMs = endedAtUnixMs;
            SimulationFrameStart = simulationFrameStart; SimulationFrameEnd = simulationFrameEnd;
            Population = population; Treasury = treasury; Unlimited = unlimited;
            Simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            // The clock is sampled within this interval, including uint wrap.
            // This association says nothing about when native statistics updated.
            if (Clock.Available && (simulationFrameStart <= simulationFrameEnd
                ? Clock.AtSimulationFrame < simulationFrameStart || Clock.AtSimulationFrame > simulationFrameEnd
                : Clock.AtSimulationFrame < simulationFrameStart && Clock.AtSimulationFrame > simulationFrameEnd))
                throw new ArgumentException("The clock frame must belong to this capture interval.", nameof(clock));
            PopulationIssue = populationIssue; TreasuryIssue = treasuryIssue;
        }

        public WorldContextSnapshot Scope { get; }
        public string GameVersion { get; }
        public long StartedAtUnixMs { get; }
        public long EndedAtUnixMs { get; }
        public uint SimulationFrameStart { get; }
        public uint SimulationFrameEnd { get; }
        public int? Population { get; }
        public int? Treasury { get; }
        public bool? Unlimited { get; }
        public SimulationSpeedFact Simulation { get; }
        public GameClockFact Clock { get; }
        public string? PopulationIssue { get; }
        public string? TreasuryIssue { get; }

        private static void RequireIssue(bool observed, string? issue, string parameter)
        {
            if (observed ? issue != null : issue != "MISSING_COMPONENT" && issue != "INVALID_VALUE")
                throw new ArgumentException("Metric availability must have exactly one fixed reason when unavailable.", parameter);
        }

        private static bool ValidText(string? value, int maximumScalars)
        {
            if (value == null || string.IsNullOrWhiteSpace(value) || value.Length > maximumScalars * 2) return false;
            int scalars = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (char.IsHighSurrogate(current))
                {
                    if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
                }
                else if (char.IsLowSurrogate(current)) return false;
                if (++scalars > maximumScalars) return false;
            }
            return true;
        }
    }
}
