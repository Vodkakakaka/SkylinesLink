using System;

namespace SkylinesLink.Application.Observation
{
    /// <summary>原生速度属性的普通值；不推断暂停原因或此刻是否真的推进。</summary>
    public sealed class SimulationSpeedFact
    {
        private SimulationSpeedFact(float selectedSpeed, float smoothSpeed)
        { Available = true; SelectedSpeed = selectedSpeed; SmoothSpeed = smoothSpeed; }
        private SimulationSpeedFact(string reason) { Reason = reason; }

        public bool Available { get; }
        public float SelectedSpeed { get; }
        public float SmoothSpeed { get; }
        public string? Reason { get; }

        public static SimulationSpeedFact Observed(float selectedSpeed, float smoothSpeed)
        {
            if (!ValidSpeed(selectedSpeed)) throw new ArgumentOutOfRangeException(nameof(selectedSpeed));
            if (!ValidSpeed(smoothSpeed)) throw new ArgumentOutOfRangeException(nameof(smoothSpeed));
            return new SimulationSpeedFact(selectedSpeed, smoothSpeed);
        }

        public static SimulationSpeedFact Unavailable(string reason)
        {
            if (reason != "SOURCE_UNAVAILABLE" && reason != "INVALID_VALUE")
                throw new ArgumentException("Unknown simulation source reason.", nameof(reason));
            return new SimulationSpeedFact(reason);
        }

        private static bool ValidSpeed(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
    }
}
