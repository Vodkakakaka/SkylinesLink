#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using LiveSubLane = Game.Net.SubLane;
using PrefabSubLane = Game.Prefabs.SubLane;
using Node = Game.Net.Node;

namespace SkylinesLink.Mod.Native
{
    // The live entities are already proven by the two owning graph ports.
    // Preserve the native owner predicate when including their Disabled chunks.
    internal static class PreviewLaneInputs
    {
        private const int RowLimit = 16;

        internal static JObject Read(EntityManager entities, Entity[] graph)
        {
            int remainingObjectRows = RowLimit;
            var owners = new JArray();
            bool emptyBranches = true;
            for (int i = 0; i < graph.Length; i++)
            {
                Entity entity = graph[i];
                Entity prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                RequirePrefab(entities, prefab);
                int? liveLanes = Count<LiveSubLane>(entities, entity);
                bool matches = liveLanes.HasValue && entities.HasComponent<Updated>(entity)
                    && !entities.HasComponent<Game.Net.OutsideConnection>(entity)
                    && !entities.HasComponent<Game.Objects.OutsideConnection>(entity)
                    && !entities.HasComponent<Game.Areas.Area>(entity);
                var owner = new JObject { ["entity"] = Id(entity), ["prefab"] = Id(prefab),
                    ["liveLanes"] = Number(liveLanes), ["matchesOwnerQuery"] = matches };
                if (i == 1)
                {
                    JObject node = ReadNode(entities, entity, prefab);
                    owner["node"] = node;
                    emptyBranches &= !matches || (liveLanes == 0 && (bool)node["emptyInputs"]!);
                }
                else
                {
                    var rows = ReadObjectRows(entities, prefab, ref remainingObjectRows);
                    var state = ReadObjectState(entities, entity);
                    owner["declaredLanes"] = rows;
                    owner["underConstruction"] = state.UnderConstruction;
                    owner["destroyed"] = state.Destroyed;
                    owner["upgrades"] = Number(state.Upgrades);
                    emptyBranches &= !matches || (liveLanes == 0 && (rows is not JArray table || table.Count == 0)
                        && !state.UnderConstruction && (state.Upgrades ?? 0) == 0);
                }
                owners.Add(owner);
            }
            return new JObject { ["owners"] = owners, ["emptyInputsObserved"] = emptyBranches, ["nativeInvoked"] = false };
        }

        internal static (bool Building, bool Transform, bool Owner, bool Edge, bool Node,
            bool UnderConstruction, bool Destroyed, int? Upgrades) ReadObjectState(EntityManager entities, Entity entity) =>
            (entities.HasComponent<Building>(entity), entities.HasComponent<Transform>(entity),
                entities.HasComponent<Owner>(entity), entities.HasComponent<Edge>(entity), entities.HasComponent<Node>(entity),
                entities.HasComponent<UnderConstruction>(entity), entities.HasComponent<Destroyed>(entity),
                Count<InstalledUpgrade>(entities, entity));

        private static JObject ReadNode(EntityManager entities, Entity entity, Entity prefab)
        {
            var node = entities.GetComponentData<Node>(entity);
            var geometry = entities.GetComponentData<NodeGeometry>(entity);
            var orphan = entities.GetComponentData<Orphan>(entity);
            var composition = entities.GetComponentData<NetCompositionData>(orphan.m_Composition);
            int? edges = Count<ConnectedEdge>(entities, entity);
            int? middle = Count<ConnectedNode>(entities, entity);
            var rows = new JArray();
            bool present = entities.HasBuffer<DefaultNetLane>(prefab);
            if (present)
            {
                var buffer = entities.GetBuffer<DefaultNetLane>(prefab, true);
                if (buffer.Length > RowLimit) throw new InvalidOperationException("PREVIEW_DEFAULT_LANE_BUDGET");
                for (int i = 0; i < buffer.Length; i++)
                {
                    var row = buffer[i];
                    RequirePrefab(entities, row.m_Lane);
                    var data = entities.GetComponentData<NetLaneData>(row.m_Lane);
                    var archetype = entities.GetComponentData<NetLaneArchetypeData>(row.m_Lane).m_NodeLaneArchetype;
                    rows.Add(new JObject { ["lane"] = Id(row.m_Lane), ["flags"] = (uint)row.m_Flags,
                        ["position"] = Vector(row.m_Position), ["laneFlags"] = (uint)data.m_Flags,
                        ["nodeArchetypeTypes"] = archetype.Valid ? new JValue(archetype.TypesCount) : JValue.CreateNull(),
                        ["utilityTypes"] = entities.HasComponent<UtilityLaneData>(row.m_Lane)
                            ? new JValue((uint)entities.GetComponentData<UtilityLaneData>(row.m_Lane).m_UtilityTypes)
                            : JValue.CreateNull() });
                }
            }
            return new JObject { ["position"] = Vector(node.m_Position), ["height"] = geometry.m_Position,
                ["composition"] = Id(orphan.m_Composition), ["general"] = (uint)composition.m_Flags.m_General,
                ["left"] = (uint)composition.m_Flags.m_Left, ["right"] = (uint)composition.m_Flags.m_Right,
                ["edges"] = Number(edges), ["middleNodes"] = Number(middle),
                ["defaultsPresent"] = present, ["defaults"] = rows,
                ["emptyInputs"] = edges == 0 && (middle ?? 0) == 0 && rows.Count == 0 };
        }

        private static JToken ReadObjectRows(EntityManager entities, Entity prefab, ref int remainingRows)
        {
            if (!entities.HasBuffer<PrefabSubLane>(prefab)) return JValue.CreateNull();
            var buffer = entities.GetBuffer<PrefabSubLane>(prefab, true);
            if (buffer.Length > remainingRows) throw new InvalidOperationException("PREVIEW_PREFAB_LANE_BUDGET");
            remainingRows -= buffer.Length;
            var rows = new JArray();
            for (int i = 0; i < buffer.Length; i++)
            {
                var row = buffer[i];
                RequirePrefab(entities, row.m_Prefab);
                rows.Add(new JObject { ["lane"] = Id(row.m_Prefab),
                    ["indices"] = new JArray(row.m_NodeIndex.x, row.m_NodeIndex.y),
                    ["parentMesh"] = new JArray(row.m_ParentMesh.x, row.m_ParentMesh.y),
                    ["curve"] = new JArray(Vector(row.m_Curve.a), Vector(row.m_Curve.b), Vector(row.m_Curve.c), Vector(row.m_Curve.d)) });
            }
            return rows;
        }

        private static int? Count<T>(EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData =>
            entities.HasBuffer<T>(entity) ? entities.GetBuffer<T>(entity, true).Length : null;

        private static void RequirePrefab(EntityManager entities, Entity entity)
        {
            if (!entities.Exists(entity) || !entities.HasComponent<PrefabData>(entity)
                || !entities.IsComponentEnabled<PrefabData>(entity) || entities.HasComponent<Deleted>(entity))
                throw new InvalidOperationException("PREVIEW_LANE_PREFAB_UNAVAILABLE");
        }

        private static JArray Vector(float3 value)
        {
            if (!math.all(math.isfinite(value))) throw new InvalidOperationException("PREVIEW_LANE_NONFINITE");
            return new JArray(value.x, value.y, value.z);
        }
        private static JArray Id(Entity value) => new JArray(value.Index, value.Version);
        private static JValue Number(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
    }
}
#endif
