using System;
using Colossal.Core;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Observation;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    /// <summary>读取当前城市已发布的原生值；不扫描实体、不创建系统、不推断统计刷新时点。</summary>
    public sealed class NativeCityOverviewCollector
    {
        private readonly UpdateSystem updateSystem;
        private readonly WorldContextTracker context;

        public NativeCityOverviewCollector(UpdateSystem updateSystem, WorldContextTracker context)
        {
            this.updateSystem = updateSystem ?? throw new ArgumentNullException(nameof(updateSystem));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public CityOverviewCapture Capture(WorldContextSnapshot expected)
        {
            if (!MainThreadDispatcher.isMainThread) throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");
            try
            {
                long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RequireScope(expected);
                var manager = GameManager.instance;
                var world = updateSystem.World;
                if (manager == null || world == null || !world.IsCreated)
                    throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");

                var loader = world.GetExistingSystemManaged<LoadGameSystem>();
                var citySystem = world.GetExistingSystemManaged<CitySystem>();
                var simulation = world.GetExistingSystemManaged<SimulationSystem>();
                if (loader == null || citySystem == null || simulation == null)
                    throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");
                RequireSource(manager, world, loader, citySystem, simulation);

                var entities = world.EntityManager;
                Entity city = citySystem.City;
                if (city == Entity.Null || !entities.Exists(city))
                    throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");

                uint frameStart = simulation.frameIndex;
                int? population = null;
                int? treasury = null;
                bool? unlimited = null;
                string? populationIssue = null;
                string? treasuryIssue = null;

                // EntityManager.GetComponentData completes this component's
                // pending writer jobs. Waiting may depend on native work; the
                // fixed amount of data read is not a hard latency guarantee.
                if (entities.HasComponent<Population>(city))
                {
                    int value = entities.GetComponentData<Population>(city).m_Population;
                    if (value < 0) populationIssue = "INVALID_VALUE";
                    else population = value;
                }
                else populationIssue = "MISSING_COMPONENT";

                if (entities.HasComponent<PlayerMoney>(city))
                {
                    PlayerMoney value = entities.GetComponentData<PlayerMoney>(city);
                    treasury = value.money;
                    unlimited = value.m_Unlimited;
                }
                else treasuryIssue = "MISSING_COMPONENT";

                SimulationSpeedFact speed = ReadSimulation(simulation);
                GameClockFact clock = ReadClock(world, simulation);
                string gameVersion = Game.Version.current.fullVersion;
                uint frameEnd = simulation.frameIndex;
                RequireSource(manager, world, loader, citySystem, simulation);
                if (citySystem.City != city || !entities.Exists(city))
                    throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");
                RequireScope(expected);
                long endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                return new CityOverviewCapture(expected, gameVersion, startedAt, endedAt,
                    frameStart, frameEnd, population, treasury, unlimited, speed, clock, populationIssue, treasuryIssue);
            }
            catch (CityOverviewReadFailure) { throw; }
            catch (Exception)
            {
                // Native failures may contain paths or internal objects. Keep
                // only the public category, without retaining an inner error.
                throw new CityOverviewReadFailure("CAPTURE_FAILED");
            }
        }

        private static SimulationSpeedFact ReadSimulation(SimulationSystem simulation)
        {
            try
            {
                float selected = simulation.selectedSpeed;
                float smooth = simulation.smoothSpeed;
                if (!IsFinite(selected) || !IsFinite(smooth) || selected < 0f || smooth < 0f)
                    return SimulationSpeedFact.Unavailable("INVALID_VALUE");
                return SimulationSpeedFact.Observed(selected, smooth);
            }
            catch (Exception) { return SimulationSpeedFact.Unavailable("SOURCE_UNAVAILABLE"); }
        }

        private static GameClockFact ReadClock(World world, SimulationSystem simulation)
        {
            try
            {
                var time = world.GetExistingSystemManaged<TimeSystem>();
                if (time == null || !HasClockSource(world, time))
                    return GameClockFact.Unavailable("SOURCE_UNAVAILABLE");

                var entities = world.EntityManager;
                GameClockFact clock;
                using (var dataQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<TimeData>()))
                using (var settingsQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<TimeSettingsData>()))
                {
                    if (!dataQuery.TryGetSingletonEntity<TimeData>(out Entity dataEntity) ||
                        !settingsQuery.TryGetSingletonEntity<TimeSettingsData>(out Entity settingsEntity))
                        clock = GameClockFact.Unavailable("COMPONENT_UNAVAILABLE");
                    else clock = ReadClockComponents(entities, time, simulation, dataEntity, settingsEntity);
                }
                // Both caller-owned queries have ended before this pure fact
                // leaves the collector; no query is retained across a city load.
                return HasClockSource(world, time) ? clock : GameClockFact.Unavailable("SOURCE_UNAVAILABLE");
            }
            catch (Exception) { return GameClockFact.Unavailable("SOURCE_UNAVAILABLE"); }
        }

        private static GameClockFact ReadClockComponents(EntityManager entities, TimeSystem time,
            SimulationSystem simulation, Entity dataEntity, Entity settingsEntity)
        {
            // Read the actual components, not the UI's default singleton
            // fallback or TimeSystem's previous loaded-city caches.
            TimeData data = entities.GetComponentData<TimeData>(dataEntity);
            TimeSettingsData settings = entities.GetComponentData<TimeSettingsData>(settingsEntity);
            if (settings.m_DaysPerYear <= 0 || settings.m_DaysPerYear > int.MaxValue / TimeSystem.kTicksPerDay)
                return GameClockFact.Unavailable("INVALID_VALUE");

            uint frame = simulation.frameIndex;
            // The public rendering-time helpers take elapsed frames.
            // An integer simulation frame deliberately has no render interpolation.
            double relativeFrame = (double)unchecked(frame - data.m_FirstFrame);
            float day = time.GetTimeOfDay(settings, data, relativeFrame);
            float yearPosition = time.GetTimeOfYear(settings, data, relativeFrame);
            int year = time.GetYear(settings, data, relativeFrame);
            if (!IsFinite(day) || !IsFinite(yearPosition) || year < data.m_StartingYear)
                return GameClockFact.Unavailable("INVALID_VALUE");

            // Public helper offsets can cross a year boundary, and a float day
            // fraction can round to 1. Normalize both public results as cyclic
            // fractions without reproducing native formulas.
            return GameClockFact.Observed(year, Fraction(day), Fraction(yearPosition), settings.m_DaysPerYear,
                data.m_FirstFrame, data.m_StartingYear, data.m_StartingMonth, data.m_StartingHour,
                data.m_StartingMinutes, frame);
        }

        private static bool HasClockSource(World world, TimeSystem time) => world.IsCreated &&
            ReferenceEquals(time.World, world) && ReferenceEquals(world.GetExistingSystemManaged<TimeSystem>(), time);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Fraction(float value) => (float)((double)value - Math.Floor(value));

        private void RequireScope(WorldContextSnapshot expected)
        {
            var current = context.Read();
            if (expected == null || context.IsStopped || expected.State != "ready" || current.State != "ready" ||
                !string.Equals(expected.InstanceId, current.InstanceId, StringComparison.Ordinal) ||
                expected.Generation != current.Generation ||
                expected.CityId != null && !string.Equals(expected.CityId, current.CityId, StringComparison.Ordinal))
                throw new CityOverviewReadFailure("SCOPE_CHANGED");
        }

        private void RequireSource(GameManager manager, World world, LoadGameSystem loader,
            CitySystem citySystem, SimulationSystem simulation)
        {
            if (!world.IsCreated || !ReferenceEquals(manager, GameManager.instance) ||
                !ReferenceEquals(updateSystem.World, world) ||
                !ReferenceEquals(world.GetExistingSystemManaged<UpdateSystem>(), updateSystem) ||
                !ReferenceEquals(loader.World, world) || !ReferenceEquals(world.GetExistingSystemManaged<LoadGameSystem>(), loader) ||
                !ReferenceEquals(citySystem.World, world) || !ReferenceEquals(world.GetExistingSystemManaged<CitySystem>(), citySystem) ||
                !ReferenceEquals(simulation.World, world) || !ReferenceEquals(world.GetExistingSystemManaged<SimulationSystem>(), simulation) ||
                manager.state != GameManager.State.WorldReady || manager.gameMode != GameMode.Game || manager.isGameLoading)
                throw new CityOverviewReadFailure("SOURCE_UNAVAILABLE");
        }
    }

    public sealed class CityOverviewReadFailure : Exception
    {
        internal CityOverviewReadFailure(string code) : base("The native city overview is unavailable.") { Code = code; }
        public string Code { get; }
    }
}
