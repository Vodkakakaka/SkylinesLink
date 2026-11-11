#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using UtilityLane = Game.Net.UtilityLane;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using NativeNode = Game.Net.Node;

namespace SkylinesLink.Mod.Native
{
    // Only the isolated, new-only node utility branch is admitted here.
    internal static class PreviewLaneCommandProbe
    {
        private const int LaneLimit = 4, CommandLimit = 32, ComponentBytesLimit = 1024;
        private const TempFlags CopiedFlags = TempFlags.Create | TempFlags.Delete | TempFlags.Select
            | TempFlags.Modify | TempFlags.Hidden;

        internal static JObject Capture(EntityCommandBuffer buffer, World source, Entity node, Entity[] admittedLanePrefabs)
        {
            if (!MainThreadDispatcher.isMainThread || !buffer.IsCreated || source == null || !source.IsCreated
                || node == Entity.Null || admittedLanePrefabs == null || admittedLanePrefabs.Length == 0
                || admittedLanePrefabs.Length > LaneLimit)
                throw Failure("PREVIEW_LANE_COMMAND_CONTEXT_UNAVAILABLE");

            EntityManager entities = source.EntityManager;
            if (!entities.Exists(node) || !entities.HasComponent<NativeNode>(node) || !entities.HasComponent<Temp>(node)
                || !entities.HasComponent<Disabled>(node) || !entities.HasComponent<ObjectGenerationScope>(node)
                || entities.GetSharedComponent<ObjectGenerationScope>(node).Value <= 0)
                throw Failure("PREVIEW_LANE_COMMAND_NODE_CHANGED");
            Temp ownerTemp = entities.GetComponentData<Temp>(node);
            if (ownerTemp.m_Original != Entity.Null || (ownerTemp.m_Flags & CopiedFlags) != TempFlags.Create
                || (ownerTemp.m_Flags & TempFlags.Replace) != 0)
                throw Failure("PREVIEW_LANE_COMMAND_NOT_NEW_ONLY");

            var archetypes = new NativeEntityArchetypes();
            var admitted = new Dictionary<Entity, LanePrefab>(admittedLanePrefabs.Length);
            foreach (Entity prefab in admittedLanePrefabs)
            {
                if (prefab == Entity.Null || !entities.Exists(prefab) || admitted.ContainsKey(prefab)
                    || !entities.HasComponent<PrefabData>(prefab) || !entities.HasComponent<NetLaneData>(prefab)
                    || !entities.HasComponent<UtilityLaneData>(prefab) || !entities.HasComponent<NetLaneArchetypeData>(prefab))
                    throw Failure("PREVIEW_LANE_COMMAND_PREFAB_UNQUALIFIED");
                var net = entities.GetComponentData<NetLaneData>(prefab);
                var utility = entities.GetComponentData<UtilityLaneData>(prefab);
                EntityArchetype archetype = entities.GetComponentData<NetLaneArchetypeData>(prefab).m_NodeLaneArchetype;
                if ((net.m_Flags & LaneFlags.Utility) == 0 || utility.m_UtilityTypes != UtilityTypes.LowVoltageLine
                    || !archetype.Valid || archetype.TypesCount <= 0 || archetype.TypesCount > 64)
                    throw Failure("PREVIEW_LANE_COMMAND_ARCHETYPE_UNQUALIFIED");
                var types = archetypes.Read(archetype, 64);
                if (!archetypes.ResourcesReleased || !types.ContainsKey(TypeManager.GetTypeIndex<PrefabRef>())
                    || !types.ContainsKey(TypeManager.GetTypeIndex<Lane>()) || !types.ContainsKey(TypeManager.GetTypeIndex<Curve>())
                    || !types.ContainsKey(TypeManager.GetTypeIndex<UtilityLane>())
                    || (net.m_Flags & LaneFlags.PseudoRandom) != 0 && !types.ContainsKey(TypeManager.GetTypeIndex<PseudoRandomSeed>()))
                    throw Failure("PREVIEW_LANE_COMMAND_ARCHETYPE_UNQUALIFIED");
                admitted.Add(prefab, new LanePrefab(archetype, net.m_Flags, utility.m_UtilityTypes));
            }

            // The producer has already bounded native output before running.
            // The SDK debug view walks the entire buffer, then we check that bound.
            var reader = new Reader(buffer);
            var ids = new HashSet<int>();
            var rows = new JArray();
            while (!reader.AtEnd)
            {
                if (rows.Count == LaneLimit) throw Failure("PREVIEW_LANE_COMMAND_LANE_BUDGET");
                int start = reader.Ordinal, startBytes = reader.RecordBytes;
                EntityArchetype created = reader.Create(out int identity);
                if (!ids.Add(identity)) throw Failure("PREVIEW_LANE_COMMAND_DUPLICATE_DEFERRED_ENTITY");
                PrefabRef prefabRef = reader.Set<PrefabRef>(identity, 1);
                if (!admitted.TryGetValue(prefabRef.m_Prefab, out var prefab) || !created.Equals(prefab.Archetype))
                    throw Failure("PREVIEW_LANE_COMMAND_PREFAB_CHANGED");
                Lane lane = reader.Set<Lane>(identity, 0);
                Curve curve = reader.Set<Curve>(identity, 0);
                if (!Finite(curve.m_Bezier.a) || !Finite(curve.m_Bezier.b) || !Finite(curve.m_Bezier.c)
                    || !Finite(curve.m_Bezier.d) || !math.isfinite(curve.m_Length) || curve.m_Length <= 0f)
                    throw Failure("PREVIEW_LANE_COMMAND_CURVE_INVALID");
                JToken seed = (prefab.Flags & LaneFlags.PseudoRandom) != 0
                    ? new JValue((int)reader.Set<PseudoRandomSeed>(identity, 0).m_Seed) : JValue.CreateNull();
                reader.AddOwnerTemp(identity);
                Owner owner = reader.Set<Owner>(identity, 1);
                Temp temp = reader.Set<Temp>(identity, 1);
                if (owner.m_Owner != node || temp.m_Original != Entity.Null || temp.m_Flags != TempFlags.Create
                    || temp.m_Cost != 0 || temp.m_Value != 0 || temp.m_CurvePosition != 0f)
                    throw Failure("PREVIEW_LANE_COMMAND_OWNERSHIP_CHANGED");

                rows.Add(new JObject
                {
                    ["deferred"] = new JArray(identity, reader.Version ?? throw Failure("PREVIEW_LANE_COMMAND_DEFERRED_VERSION_UNAVAILABLE")), ["prefab"] = EntityValue(prefabRef.m_Prefab),
                    ["owner"] = EntityValue(owner.m_Owner), ["archetypeTypes"] = created.TypesCount,
                    ["utilityTypes"] = (uint)prefab.Utility, ["laneFlags"] = (uint)prefab.Flags,
                    ["underground"] = (prefab.Flags & LaneFlags.Underground) != 0,
                    ["utilityLaneComponentPresent"] = true, ["utilityLaneValueWritten"] = false,
                    ["start"] = Path(lane.m_StartNode, node), ["middle"] = Path(lane.m_MiddleNode, node),
                    ["end"] = Path(lane.m_EndNode, node),
                    ["curve"] = new JArray(Vector(curve.m_Bezier.a), Vector(curve.m_Bezier.b),
                        Vector(curve.m_Bezier.c), Vector(curve.m_Bezier.d)),
                    ["length"] = curve.m_Length, ["seed"] = seed,
                    ["temp"] = new JObject { ["original"] = EntityValue(temp.m_Original), ["flags"] = (uint)temp.m_Flags,
                        ["cost"] = temp.m_Cost, ["value"] = temp.m_Value, ["curvePosition"] = temp.m_CurvePosition },
                    ["commandCount"] = reader.Ordinal - start, ["recordBytes"] = reader.RecordBytes - startBytes
                });
            }

            return new JObject
            {
                ["node"] = EntityValue(node), ["commandsObserved"] = reader.Ordinal, ["lanesCreated"] = rows.Count,
                ["noOp"] = rows.Count == 0, ["lanes"] = rows, ["sortKey"] = reader.SortKey.HasValue
                    ? new JValue(reader.SortKey.Value) : JValue.CreateNull(),
                ["recordBytes"] = reader.RecordBytes, ["playbackCalled"] = false
            };
        }

        internal static int CountRelocationCommands(EntityCommandBuffer buffer)
        {
            if (!MainThreadDispatcher.isMainThread || !buffer.IsCreated)
                throw Failure("PREVIEW_LANE_COMMAND_CONTEXT_UNAVAILABLE");
            // The two isolated nodes were independently admitted before native entry.
            return ReadCommands(buffer, new ObjectDefinitionCommandProbe.Abi(), 2 * CommandLimit).Length;
        }

        private static Array ReadCommands(EntityCommandBuffer buffer, ObjectDefinitionCommandProbe.Abi abi, int limit)
        {
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var observed = (Array)abi.Commands.GetValue(proxy)!;
            if (observed.Length > limit) throw Failure("PREVIEW_LANE_COMMAND_BUDGET");
            return observed;
        }

        private sealed class Reader
        {
            private readonly ObjectDefinitionCommandProbe.Abi abi = new ObjectDefinitionCommandProbe.Abi();
            private readonly Array commands;
            private readonly Type typeSetView;
            internal int Ordinal { get; private set; }
            internal int RecordBytes { get; private set; }
            internal int? SortKey { get; private set; }
            internal int? Version { get; private set; }
            internal bool AtEnd => Ordinal == commands.Length;

            internal Reader(EntityCommandBuffer buffer)
            {
                typeSetView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic)
                    ?? throw Failure("PREVIEW_LANE_COMMAND_ABI_UNQUALIFIED");
                commands = ReadCommands(buffer, abi, CommandLimit);
            }

            internal EntityArchetype Create(out int identity)
            {
                object command = Take(abi.Create, "CreateEntity");
                identity = abi.Int(command, "EntityIdentityIndex");
                if (identity >= 0 || abi.Int(command, "BatchCount") != 1)
                    throw Failure("PREVIEW_LANE_COMMAND_CREATE_UNQUALIFIED");
                return (EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype));
            }

            internal T Set<T>(int identity, int references) where T : unmanaged, IComponentData
            {
                object command = Take(abi.Component, "SetComponent");
                RequireTarget(command, identity);
                TypeIndex type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                if (type != TypeManager.GetTypeIndex<T>() || (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)))
                    throw Failure("PREVIEW_LANE_COMMAND_COMPONENT_UNQUALIFIED");
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
                if (info.Category != TypeManager.TypeCategory.ComponentData || info.SizeInChunk <= 0
                    || info.SizeInChunk > ComponentBytesLimit || info.EntityOffsetCount != references
                    || info.HasBlobAssetRefs || info.HasWeakAssetRefs || info.HasUnityObjectRefs
                    || (short)abi.Value(command, "ComponentSize", typeof(short)) != info.SizeInChunk
                    || !(abi.Value(command, "ComponentValue", typeof(object)) is T value))
                    throw Failure("PREVIEW_LANE_COMMAND_COMPONENT_UNQUALIFIED");
                return value;
            }

            internal void AddOwnerTemp(int identity)
            {
                object command = Take(typeSetView, "AddMultipleComponents");
                RequireTarget(command, identity);
                var types = (ComponentTypeSet)abi.Value(command, "TypeSet", typeof(ComponentTypeSet));
                if (types.Length != 2) throw Failure("PREVIEW_LANE_COMMAND_OWNER_TYPES_CHANGED");
                TypeIndex first = types.GetTypeIndex(0), second = types.GetTypeIndex(1);
                TypeIndex owner = TypeManager.GetTypeIndex<Owner>(), temp = TypeManager.GetTypeIndex<Temp>();
                if (!(first == owner && second == temp || first == temp && second == owner))
                    throw Failure("PREVIEW_LANE_COMMAND_OWNER_TYPES_CHANGED");
            }

            private object Take(Type view, string kind)
            {
                if (AtEnd) throw Failure("PREVIEW_LANE_COMMAND_TRUNCATED");
                object command = commands.GetValue(Ordinal++)!;
                int sortKey = abi.Int(command, "SortKey"), size = abi.Int(command, "TotalSizeInBytes");
                if (command.GetType() != view || Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) != kind
                    || sortKey < 0 || SortKey.HasValue && sortKey != SortKey.Value || size <= 0 || size > ComponentBytesLimit + 1024)
                    throw Failure("PREVIEW_LANE_COMMAND_UNEXPECTED");
                SortKey = sortKey;
                RecordBytes = checked(RecordBytes + size);
                return command;
            }

            private void RequireTarget(object command, int identity)
            {
                Entity target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                if (target.Index != identity || abi.Int(command, "IdentityIndex") != 0 || abi.Int(command, "BatchCount") != 1
                    || Version.HasValue && Version.Value != target.Version)
                    throw Failure("PREVIEW_LANE_COMMAND_FOREIGN_TARGET");
                Version = target.Version;
            }
        }

        private readonly struct LanePrefab
        {
            internal readonly EntityArchetype Archetype;
            internal readonly LaneFlags Flags;
            internal readonly UtilityTypes Utility;
            internal LanePrefab(EntityArchetype archetype, LaneFlags flags, UtilityTypes utility)
            { Archetype = archetype; Flags = flags; Utility = utility; }
        }

        private static JArray Path(PathNode path, Entity node)
        {
            // PathNode encodes the owner's index only. Owner above establishes
            // the exact same-source node, including its entity version.
            if (!path.Equals(new PathNode(node, path.GetLaneIndex())))
                throw Failure("PREVIEW_LANE_COMMAND_FOREIGN_PATH_NODE");
            return new JArray(path.GetOwnerIndex(), (int)path.GetLaneIndex(), path.GetCurvePos());
        }

        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray EntityValue(Entity value) => new JArray(value.Index, value.Version);
        private static InvalidOperationException Failure(string code) => new InvalidOperationException(code);
    }
}
#endif
