#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.City;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A read of native Temp values and the authoritative city's money.
    // Validation can still change cancellation and eligibility after this stage.
    internal sealed class PreviewCostObservation
    {
        private readonly Temp[] before;
        private readonly Temp[]? captured;
        private readonly CitySystem citySystem;
        private readonly Entity city;
        private readonly PlayerMoney funds;

        internal PreviewCostObservation(World source, Entity[] graph, Temp[]? captured = null)
        {
            if (captured != null && captured.Length != graph.Length)
                throw new InvalidOperationException("PREVIEW_COST_CAPTURED_GRAPH_CHANGED");
            this.captured = captured;
            citySystem = source.GetExistingSystemManaged<CitySystem>()
                ?? throw new InvalidOperationException("PREVIEW_COST_CITY_UNAVAILABLE");
            city = citySystem.City;
            funds = ReadFunds(source);
            before = new Temp[graph.Length];
            for (int i = 0; i < graph.Length; i++) before[i] = ReadTemp(source.EntityManager, graph[i], i);
        }

        internal JObject Complete(World source, Entity[] graph)
        {
            if (graph.Length != before.Length) throw new InvalidOperationException("PREVIEW_COST_GRAPH_LENGTH_CHANGED");
            var entities = source.EntityManager;
            var rows = new JArray();
            long cost = 0;
            long value = 0;
            for (int i = 0; i < graph.Length; i++)
            {
                Temp temp = ReadTemp(entities, graph[i], i);
                if (!temp.Equals(before[i]))
                    throw new InvalidOperationException("PREVIEW_COST_TEMP_CHANGED");
                cost += temp.m_Cost;
                value += temp.m_Value;
                rows.Add(new JObject { ["id"] = new JArray(graph[i].Index, graph[i].Version),
                    ["flags"] = (uint)temp.m_Flags, ["cost"] = temp.m_Cost, ["value"] = temp.m_Value });
            }
            var after = ReadFunds(source);
            if (after.money != funds.money || cost < 0 || cost > int.MaxValue)
                throw new InvalidOperationException("PREVIEW_COST_BALANCE_OR_TOTAL_CHANGED");
            return new JObject { ["entities"] = rows, ["observedTempCost"] = cost, ["observedTempValue"] = value,
                ["treasury"] = after.money, ["fundsUnchanged"] = true,
                ["observedAmountCovered"] = Math.Max(0, after.money) >= cost };
        }

        private PlayerMoney ReadFunds(World source)
        {
            if (!ReferenceEquals(citySystem.World, source) || !ReferenceEquals(source.GetExistingSystemManaged<CitySystem>(), citySystem)
                || city == Entity.Null || citySystem.City != city || !source.EntityManager.Exists(city)
                || !source.EntityManager.HasComponent<PlayerMoney>(city))
                throw new InvalidOperationException("PREVIEW_COST_CITY_CHANGED");
            PlayerMoney result = source.EntityManager.GetComponentData<PlayerMoney>(city);
            if (result.m_Unlimited) throw new InvalidOperationException("PREVIEW_COST_UNLIMITED_MONEY");
            return result;
        }

        internal void RequireFundsUnchanged(World source)
        {
            if (ReadFunds(source).money != funds.money)
                throw new InvalidOperationException("PREVIEW_COST_BALANCE_CHANGED");
        }

        private Temp ReadTemp(EntityManager entities, Entity entity, int slot)
        {
            var value = entities.GetComponentData<Temp>(entity);
            if (captured != null)
            {
                if (!value.Equals(captured[slot])) throw new InvalidOperationException("PREVIEW_COST_CAPTURED_TEMP_CHANGED");
                return value;
            }
            const TempFlags rejected = TempFlags.Cancel | TempFlags.Delete | TempFlags.Select | TempFlags.Duplicate;
            if (value.m_Original != Entity.Null || (value.m_Flags & (TempFlags.Create | rejected)) != TempFlags.Create)
                throw new InvalidOperationException("PREVIEW_COST_TEMP_NOT_NEW");
            return value;
        }
    }
}
#endif
