using System;

namespace SkylinesLink.Application.Observation
{
    /// <summary>原生时间函数的已归一化结果及原始epoch；本模型不修改或补算日历。</summary>
    public sealed class GameClockFact
    {
        private GameClockFact(int year, float dayFraction, float yearFraction, int daysPerYear,
            uint firstFrame, int startingYear, byte startingMonth, byte startingHour, byte startingMinute, uint atSimulationFrame)
        {
            Available = true; Year = year; DayFraction = dayFraction; YearFraction = yearFraction; DaysPerYear = daysPerYear;
            FirstFrame = firstFrame; StartingYear = startingYear; StartingMonth = startingMonth;
            StartingHour = startingHour; StartingMinute = startingMinute; AtSimulationFrame = atSimulationFrame;
        }
        private GameClockFact(string reason) { Reason = reason; }

        public bool Available { get; }
        public int Year { get; }
        public float DayFraction { get; }
        public float YearFraction { get; }
        public int DaysPerYear { get; }
        public uint FirstFrame { get; }
        public int StartingYear { get; }
        public byte StartingMonth { get; }
        public byte StartingHour { get; }
        public byte StartingMinute { get; }
        public uint AtSimulationFrame { get; }
        public string? Reason { get; }

        public static GameClockFact Observed(int year, float dayFraction, float yearFraction, int daysPerYear,
            uint firstFrame, int startingYear, byte startingMonth, byte startingHour, byte startingMinute, uint atSimulationFrame)
        {
            if (!ValidFraction(dayFraction)) throw new ArgumentOutOfRangeException(nameof(dayFraction));
            if (!ValidFraction(yearFraction)) throw new ArgumentOutOfRangeException(nameof(yearFraction));
            if (daysPerYear < 1 || daysPerYear > 8191) throw new ArgumentOutOfRangeException(nameof(daysPerYear));
            if (year < startingYear) throw new ArgumentOutOfRangeException(nameof(year));
            return new GameClockFact(year, dayFraction, yearFraction, daysPerYear, firstFrame,
                startingYear, startingMonth, startingHour, startingMinute, atSimulationFrame);
        }

        public static GameClockFact Unavailable(string reason)
        {
            if (reason != "SOURCE_UNAVAILABLE" && reason != "COMPONENT_UNAVAILABLE" && reason != "INVALID_VALUE")
                throw new ArgumentException("Unknown native time source reason.", nameof(reason));
            return new GameClockFact(reason);
        }

        private static bool ValidFraction(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value < 1;
    }
}
