#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using PrefabBuildingFlags = Game.Prefabs.BuildingFlags;
using LiveSubLane = Game.Net.SubLane;
using LiveSubObject = Game.Objects.SubObject;
using LiveSpawnLocation = Game.Objects.SpawnLocation;
using LiveUtilityObject = Game.Objects.UtilityObject;
using LiveSecondaryLane = Game.Net.SecondaryLane;

namespace SkylinesLink.Mod.Native
{
    // Read the seven still-live, Disabled members of the caller's proven
    // graph as if the native modification query included this isolated scope.
    internal static class PreviewRoadInputs
    {
        private const int MemberCount = 7, LaneLimit = 64;
        [Flags]
        private enum Facts
        {
            Building = 1, SpawnLocation = 2, Edge = 4, ConnectedBuilding = 8, Updated = 16,
            Deleted = 32, Temp = 64, Created = 128, UtilityObject = 256, Secondary = 512
        }

        internal static JObject Read(World source, Entity[] graph)
        {
            if (source == null || !source.IsCreated || graph == null || graph.Length != MemberCount)
                throw new InvalidOperationException("PREVIEW_ROAD_GRAPH_UNAVAILABLE");

            EntityManager entities = source.EntityManager;
            var members = new JArray();
            int scopedInputs = 0, actor = 0;
            bool rootMatches = false;
            for (int slot = 0; slot < graph.Length; slot++)
            {
                Entity entity = graph[slot];
                if (entity == Entity.Null || !entities.Exists(entity) || Array.IndexOf(graph, entity) != slot
                    || !entities.HasComponent<Disabled>(entity) || !entities.HasComponent<ObjectGenerationScope>(entity))
                    throw new InvalidOperationException("PREVIEW_ROAD_GRAPH_MEMBER_UNAVAILABLE");
                int currentActor = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value;
                if (slot == 0) actor = currentActor;
                if (actor <= 0 || actor != currentActor) throw new InvalidOperationException("PREVIEW_ROAD_GRAPH_SCOPE_CHANGED");

                Facts facts = MemberFacts(entities, entity);
                Entity owner = Read<Owner>(entities, entity)?.m_Owner ?? Entity.Null;
                int ownerSlot = owner == Entity.Null ? -1 : Array.IndexOf(graph, owner);
                if (owner != Entity.Null && ownerSlot < 0) ownerSlot = -2;
                bool matches = Matches(facts);
                if (matches) scopedInputs++;
                if (slot == 0)
                    rootMatches = matches && ownerSlot == -1 && (facts & (Facts.Building | Facts.Updated | Facts.Temp))
                        == (Facts.Building | Facts.Updated | Facts.Temp)
                        && (facts & (Facts.Deleted | Facts.SpawnLocation | Facts.Edge | Facts.ConnectedBuilding)) == 0;
                members.Add(new JArray(entity.Index, entity.Version, (int)facts, ownerSlot, matches));
            }

            Entity root = graph[0];
            Building? building = Read<Building>(entities, root);
            Temp? temp = Read<Temp>(entities, root);
            BackSide? back = Read<BackSide>(entities, root);
            Entity prefab = Read<PrefabRef>(entities, root)?.m_Prefab ?? Entity.Null;
            BuildingData? prefabData = prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                ? Read<BuildingData>(entities, prefab) : null;
            bool? noRoadConnection = prefabData.HasValue ? (prefabData.Value.m_Flags & PrefabBuildingFlags.NoRoadConnection) != 0 : (bool?)null;
            int? spawnLocations = Count<SpawnLocationElement>(entities, root);
            int? lanes = Count<LiveSubLane>(entities, root);
            int? secondaryLanes = ReadSecondaryLanes(entities, root, lanes);
            int? rootChildren = Count<LiveSubObject>(entities, root);
            int? nodeChildren = Count<LiveSubObject>(entities, graph[1]);
            int? affectedUtilityObjects = ReadAffectedUtilityObjects(entities, graph, rootChildren);
            bool defaultRoad = building.HasValue && building.Value.m_RoadEdge == Entity.Null && building.Value.m_CurvePosition == 0f;
            bool defaultBack = !back.HasValue || (back.Value.m_RoadEdge == Entity.Null && back.Value.m_CurvePosition == 0f);

            bool newTemp = temp.HasValue && temp.Value.m_Original == Entity.Null && (temp.Value.m_Flags & TempFlags.Create) != 0;
            string? reason = scopedInputs != 1 || !rootMatches || !newTemp
                ? "root-only-new-temp-not-established"
                : noRoadConnection != true ? "native-road-search-not-excluded"
                : !defaultRoad || !defaultBack ? "existing-road-fields-may-change"
                : lanes.HasValue && (secondaryLanes != 0 || spawnLocations != 0) ? "secondary-lane-or-spawn-branch-unresolved"
                : affectedUtilityObjects != 0 ? "secondary-utility-object-branch-unresolved"
                : null;

            return new JObject
            {
                ["nativeKernelInvoked"] = false,
                ["scopedInputCount"] = scopedInputs,
                ["memberColumns"] = new JArray("index", "version", "mask", "ownerSlot", "matches"),
                ["memberMask"] = new JObject { ["building"] = 1, ["spawnLocation"] = 2, ["edge"] = 4,
                    ["connectedBuilding"] = 8, ["updated"] = 16, ["deleted"] = 32, ["temp"] = 64,
                    ["created"] = 128, ["utilityObject"] = 256, ["secondary"] = 512 },
                ["members"] = members,
                ["root"] = new JObject
                {
                    ["prefab"] = Id(prefab),
                    ["prefabBuildingFlags"] = prefabData.HasValue ? new JValue((uint)prefabData.Value.m_Flags) : JValue.CreateNull(),
                    ["instanceBuildingFlags"] = building.HasValue ? new JValue((uint)building.Value.m_Flags) : JValue.CreateNull(),
                    ["noRoadConnection"] = Value(noRoadConnection),
                    ["requireRoad"] = Value(prefabData.HasValue ? (prefabData.Value.m_Flags & PrefabBuildingFlags.RequireRoad) != 0 : (bool?)null),
                    ["backAccess"] = Value(prefabData.HasValue ? (prefabData.Value.m_Flags & PrefabBuildingFlags.BackAccess) != 0 : (bool?)null),
                    ["lotDepth"] = Finite(prefabData?.m_LotSize.y),
                    ["roadEdge"] = Id(building?.m_RoadEdge), ["curvePosition"] = Finite(building?.m_CurvePosition),
                    ["backSidePresent"] = back.HasValue, ["backRoadEdge"] = Id(back?.m_RoadEdge), ["backCurvePosition"] = Finite(back?.m_CurvePosition),
                    ["tempOriginal"] = Id(temp?.m_Original), ["tempFlags"] = temp.HasValue ? new JValue((uint)temp.Value.m_Flags) : JValue.CreateNull(),
                    ["spawnLocations"] = Value(spawnLocations), ["subLanes"] = Value(lanes), ["secondaryLanes"] = Value(secondaryLanes),
                    ["subObjects"] = Value(rootChildren), ["nodeSubObjects"] = Value(nodeChildren),
                    ["affectedSecondaryUtilityObjects"] = Value(affectedUtilityObjects)
                },
                ["noGraphDeltaQualified"] = reason == null,
                ["unknownReason"] = reason,
                ["scope"] = "Current owned input only; no prediction after lane/graph changes. A native run may write equal values."
            };
        }

        private static Facts MemberFacts(EntityManager entities, Entity entity)
        {
            Facts facts = default;
            if (entities.HasComponent<Building>(entity)) facts |= Facts.Building;
            if (entities.HasComponent<LiveSpawnLocation>(entity)) facts |= Facts.SpawnLocation;
            if (entities.HasComponent<Edge>(entity)) facts |= Facts.Edge;
            if (entities.HasBuffer<ConnectedBuilding>(entity)) facts |= Facts.ConnectedBuilding;
            if (entities.HasComponent<Updated>(entity)) facts |= Facts.Updated;
            if (entities.HasComponent<Deleted>(entity)) facts |= Facts.Deleted;
            if (entities.HasComponent<Temp>(entity)) facts |= Facts.Temp;
            if (entities.HasComponent<Created>(entity)) facts |= Facts.Created;
            if (entities.HasComponent<LiveUtilityObject>(entity)) facts |= Facts.UtilityObject;
            if (entities.HasComponent<Secondary>(entity)) facts |= Facts.Secondary;
            return facts;
        }

        private static bool Matches(Facts facts) =>
            (facts & (Facts.Updated | Facts.Deleted)) != 0 && ((facts & (Facts.Building | Facts.SpawnLocation)) != 0
                || (facts & (Facts.Edge | Facts.ConnectedBuilding | Facts.Temp)) == (Facts.Edge | Facts.ConnectedBuilding));

        private static int? ReadSecondaryLanes(EntityManager entities, Entity root, int? length)
        {
            if (!length.HasValue || length.Value > LaneLimit) return null;
            var rows = entities.GetBuffer<LiveSubLane>(root, true);
            int count = 0;
            for (int index = 0; index < length.Value; index++)
            {
                Entity lane = rows[index].m_SubLane;
                if (lane == Entity.Null || !entities.Exists(lane)) return null;
                if (entities.HasComponent<LiveSecondaryLane>(lane)) count++;
            }
            return count;
        }

        private static int? ReadAffectedUtilityObjects(EntityManager entities, Entity[] graph, int? length)
        {
            if (length != 4) return null;
            var children = entities.GetBuffer<LiveSubObject>(graph[0], true);
            if (children.Length != 4) return null;
            int seen = 0, count = 0;
            const UtilityTypes affected = UtilityTypes.LowVoltageLine | UtilityTypes.SewagePipe | UtilityTypes.WaterPipe;
            for (int index = 0; index < children.Length; index++)
            {
                Entity child = children[index].m_SubObject;
                int slot = Array.IndexOf(graph, child, 2);
                if (slot < 2 || (seen & (1 << slot)) != 0 || Read<Owner>(entities, child)?.m_Owner != graph[0]) return null;
                seen |= 1 << slot;
                if (!entities.HasComponent<LiveUtilityObject>(child) || !entities.HasComponent<Secondary>(child)) continue;
                Entity prefab = Read<PrefabRef>(entities, child)?.m_Prefab ?? Entity.Null;
                if (prefab == Entity.Null || !entities.Exists(prefab) || !entities.HasComponent<UtilityObjectData>(prefab)) return null;
                if ((entities.GetComponentData<UtilityObjectData>(prefab).m_UtilityTypes & affected) != 0) count++;
            }
            return count;
        }

        private static T? Read<T>(EntityManager entities, Entity entity) where T : unmanaged, IComponentData =>
            entities.HasComponent<T>(entity) ? entities.GetComponentData<T>(entity) : (T?)null;

        private static int? Count<T>(EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData =>
            entities.HasBuffer<T>(entity) ? entities.GetBuffer<T>(entity, true).Length : (int?)null;

        private static JToken Id(Entity? entity) => entity.HasValue
            ? new JArray(entity.Value.Index, entity.Value.Version) : JValue.CreateNull();
        private static JToken Finite(float? value) => value.HasValue && math.isfinite(value.Value)
            ? new JValue(value.Value) : JValue.CreateNull();
        private static JToken Value(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        private static JToken Value(bool? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
    }
}
#endif
