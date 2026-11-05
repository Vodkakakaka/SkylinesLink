using System;

namespace SkylinesLink.Application.Context
{
    public sealed class WorldContextSnapshot
    {
        public WorldContextSnapshot(string instanceId, long generation, string state, string? cityId)
        {
            InstanceId = instanceId;
            Generation = generation;
            State = state;
            CityId = cityId;
        }

        public string InstanceId { get; }
        public long Generation { get; }
        public string State { get; }
        public string? CityId { get; }
    }

    /// <summary>普通值上下文。原生适配必须显式报告加载边界，不能用 World 指针推断代次。</summary>
    public sealed class WorldContextTracker
    {
        private readonly object sync = new object();
        private WorldContextSnapshot current;
        private bool stopped;

        public WorldContextTracker(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 128) throw new ArgumentException("Instance identity is required.", nameof(instanceId));
            current = new WorldContextSnapshot(instanceId, 0, "menu", null);
        }

        public WorldContextSnapshot Read() { lock (sync) return current; }
        public bool IsStopped { get { lock (sync) return stopped; } }

        public long EnterLoading()
        {
            lock (sync)
            {
                RequireRunning();
                current = new WorldContextSnapshot(current.InstanceId, checked(current.Generation + 1), "loading", null);
                return current.Generation;
            }
        }

        public bool ObserveReady(long expectedGeneration, string? cityId = null)
        {
            ValidateCityIdentity(cityId);
            lock (sync)
            {
                if (stopped || current.Generation != expectedGeneration || current.State != "loading") return false;
                current = new WorldContextSnapshot(current.InstanceId, current.Generation, "ready", cityId);
                return true;
            }
        }

        // Trusted native observation may enrich the same loaded scope after a first save.
        // A conflicting identity needs a real loading boundary, never silent reassignment.
        public bool ObserveCityIdentity(long expectedGeneration, string cityId)
        {
            if (cityId == null) throw new ArgumentNullException(nameof(cityId));
            ValidateCityIdentity(cityId);
            lock (sync)
            {
                if (stopped || current.Generation != expectedGeneration || current.State != "ready" ||
                    current.CityId != null && current.CityId != cityId) return false;
                current = new WorldContextSnapshot(current.InstanceId, current.Generation, current.State, cityId);
                return true;
            }
        }

        private static void ValidateCityIdentity(string? cityId)
        {
            if (cityId != null && (string.IsNullOrWhiteSpace(cityId) || cityId.Length > 128))
                throw new ArgumentException("A known city identity must be nonempty and bounded.", nameof(cityId));
        }

        public void EnterMenu()
        {
            lock (sync)
            {
                RequireRunning();
                current = new WorldContextSnapshot(current.InstanceId, checked(current.Generation + 1), "menu", null);
            }
        }

        public void MarkUnavailable()
        {
            lock (sync)
            {
                if (stopped) return;
                current = new WorldContextSnapshot(current.InstanceId, checked(current.Generation + 1), "unavailable", null);
            }
        }

        // A service lifetime cannot be revived by a late native observation.
        // Temporary loading/unavailability uses the ordinary transitions above.
        public void Stop()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                current = new WorldContextSnapshot(current.InstanceId, checked(current.Generation + 1), "unavailable", null);
            }
        }

        private void RequireRunning()
        {
            if (stopped) throw new InvalidOperationException("The world context lifetime has stopped.");
        }

        public bool AllowsWorld(string instanceId, long generation)
        {
            lock (sync)
                return current.State == "ready" && current.InstanceId == instanceId && current.Generation == generation;
        }
    }
}
