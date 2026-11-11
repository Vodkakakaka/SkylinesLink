#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using EditorContainer = Game.Tools.EditorContainer;
using NativeNode = Game.Net.Node;
using NativeTransform = Game.Objects.Transform;
using ServiceUpgrade = Game.Buildings.ServiceUpgrade;

namespace SkylinesLink.Mod.Native
{
    internal static class RelocationGenerationCommands
    {
        internal const string Add = "AddComponent", Set = "SetComponent", Remove = "RemoveComponent";
        internal const string Disable = "SetComponentEnabled";
        private const int MaxTypeBytes = 1024, MaxRecordBytes = 2048, MaxCommands = 128;

        internal static JObject Capture(RelocationDefinitionPayload payload, EntityCommandBuffer objectCommands,
            EntityCommandBuffer nodeCommands, RelocationGenerationAdmission admission, out RelocationPreviewPayload preview)
        {
            Require(MainThreadDispatcher.isMainThread && payload.Source.IsCreated, "RELOCATION_GENERATION_COMMAND_SOURCE");
            Require(Valid(admission.ObjectArchetype) && Valid(admission.NodeArchetype)
                && admission.ObjectCommandUpperBound > 0 && admission.ObjectCommandUpperBound <= MaxCommands
                && admission.NodeCommandUpperBound > 0 && admission.NodeCommandUpperBound <= MaxCommands,
                "RELOCATION_GENERATION_COMMAND_ADMISSION");
            var original = payload.Original;
            Require(original.Root.Index > 0 && original.NodeEntity.Index > 0 && original.Root != original.NodeEntity
                && payload.RootCreation.m_Prefab == original.Prefab && payload.RootCreation.m_Original == original.Root
                && payload.RootCreation.m_Flags == CreationFlags.Relocate && IsDetached(payload.RootCreation)
                && payload.OldNodeCreation.m_Prefab == Entity.Null && payload.OldNodeCreation.m_Original == original.NodeEntity
                && payload.OldNodeCreation.m_Flags == (CreationFlags.Delete | CreationFlags.Hidden) && IsDetached(payload.OldNodeCreation)
                && payload.NewNodeCreation.m_Prefab == original.NodePrefab && payload.NewNodeCreation.m_Original == Entity.Null
                && payload.NewNodeCreation.m_Flags == (CreationFlags)0 && IsDetached(payload.NewNodeCreation)
                && payload.NewNodeOwner.m_Prefab == original.Prefab
                && payload.NewNodeOwner.m_Position.Equals(payload.RootObject.m_Position)
                && payload.NewNodeOwner.m_Rotation.Equals(payload.RootObject.m_Rotation),
                "RELOCATION_GENERATION_COMMAND_DEFINITION");

            Dictionary<TypeIndex, Write> root = ExpectRoot(payload, admission);
            Dictionary<TypeIndex, Write> oldNode = ExpectNode(payload, admission.NodeArchetype, old: true);
            Dictionary<TypeIndex, Write> newNode = ExpectNode(payload, admission.NodeArchetype, old: false);
            Require(root.Count + 3 <= admission.ObjectCommandUpperBound
                && oldNode.Count + newNode.Count + 4 <= admission.NodeCommandUpperBound,
                "RELOCATION_GENERATION_COMMAND_EXPECTED_BOUND");

            var abi = new ObjectDefinitionCommandProbe.Abi();
            var objects = ReadStream(objectCommands, admission.ObjectArchetype, admission.ObjectCommandUpperBound,
                original.Root, 1, 0, abi);
            var nodes = ReadStream(nodeCommands, admission.NodeArchetype, admission.NodeCommandUpperBound,
                original.NodeEntity, 2, int.MaxValue, abi);
            Target rootTarget = FindByOriginal(objects, original.Root);
            Target oldTarget = FindByOriginal(nodes, original.NodeEntity);
            Target newTarget = FindByOriginal(nodes, Entity.Null);
            preview = new RelocationPreviewPayload(payload, admission.ObjectArchetype, admission.NodeArchetype,
                Verify(rootTarget, root, "RELOCATION_GENERATION_ROOT_WRITE_SET"),
                Verify(oldTarget, oldNode, "RELOCATION_GENERATION_OLD_NODE_WRITE_SET"),
                Verify(newTarget, newNode, "RELOCATION_GENERATION_NEW_NODE_WRITE_SET"), rootTarget.Temp, oldTarget.Temp, newTarget.Temp);

            return new JObject
            {
                ["objectCommandUpperBound"] = admission.ObjectCommandUpperBound,
                ["nodeCommandUpperBound"] = admission.NodeCommandUpperBound,
                ["objectCommandsObserved"] = objects.CommandCount, ["nodeCommandsObserved"] = nodes.CommandCount,
                ["objectRecordBytes"] = objects.RecordBytes, ["nodeRecordBytes"] = nodes.RecordBytes,
                ["root"] = Describe(rootTarget, original.Prefab),
                ["oldNode"] = Describe(oldTarget, original.NodePrefab),
                ["newNode"] = Describe(newTarget, original.NodePrefab),
                ["hiddenTargets"] = new JArray(Id(original.Root), Id(original.NodeEntity)),
                ["batchesUpdatedTargets"] = new JArray(Id(original.Root), Id(original.NodeEntity)),
                ["completeCommandValidation"] = true, ["commandEntityReferencesClosed"] = true,
                ["sourcePlaybackCalled"] = false, ["materialized"] = false, ["feesCharged"] = false
            };
        }

        private static Dictionary<TypeIndex, Write> ExpectRoot(RelocationDefinitionPayload payload,
            RelocationGenerationAdmission admission)
        {
            var manager = payload.Source.EntityManager;
            Entity original = payload.Original.Root, prefab = payload.Original.Prefab;
            var definition = payload.RootObject;
            Require(manager.Exists(original) && manager.Exists(prefab) && manager.HasComponent<ObjectData>(prefab)
                && manager.GetComponentData<ObjectData>(prefab).m_Archetype.Equals(admission.ObjectArchetype)
                && manager.HasComponent<ObjectGeometryData>(prefab) && manager.HasComponent<PlaceableObjectData>(prefab)
                && manager.HasComponent<BuildingData>(prefab) && manager.HasComponent<PrefabData>(prefab)
                && manager.IsComponentEnabled<PrefabData>(prefab) && !manager.HasComponent<Stopped>(original)
                && !manager.HasComponent<Destroyed>(original) && !manager.HasComponent<EditorContainer>(original)
                && !manager.HasComponent<TreeData>(prefab) && !manager.HasComponent<StackData>(prefab)
                && definition.m_ParentMesh == -1 && math.isfinite(definition.m_Elevation)
                && Finite(definition.m_Position, definition.m_Rotation)
                && math.distance(payload.Original.RootTransform.m_Position.xz, definition.m_Position.xz) > 0.1f,
                "RELOCATION_GENERATION_ROOT_BRANCH_UNQUALIFIED");
            var placement = manager.GetComponentData<PlaceableObjectData>(prefab);
            Require((placement.m_Flags & Game.Objects.PlacementFlags.Attached) == 0,
                "RELOCATION_GENERATION_ROOT_ATTACHMENT_UNQUALIFIED");
            int value = (int)placement.m_ConstructionCost;
            int cost = manager.HasComponent<Recent>(original)
                ? ObjectUtils.GetRelocationCost(value, manager.GetComponentData<Recent>(original),
                    admission.SimulationFrame, admission.EconomyParameters)
                : ObjectUtils.GetRelocationCost(value, admission.EconomyParameters);
            Require(value > 0 && cost >= 0 && cost <= value, "RELOCATION_GENERATION_ROOT_COST_UNQUALIFIED");

            var expected = new Dictionary<TypeIndex, Write>();
            using (var types = admission.ObjectArchetype.GetComponentTypes())
                foreach (var type in types)
                    if (type.TypeIndex == TypeManager.GetTypeIndex<CustomMeshColor>())
                        expected.Add(type.TypeIndex, new Write(Disable, null, 0, BindMeshColor));
            Value(expected, Set, new NativeTransform(definition.m_Position, definition.m_Rotation));
            Value(expected, Set, new PrefabRef { m_Prefab = prefab }, 1);
            if ((manager.GetComponentData<ObjectGeometryData>(prefab).m_Flags & Game.Objects.GeometryFlags.Physical) != 0)
                Value(expected, Add, manager.HasComponent<Surface>(original) ? manager.GetComponentData<Surface>(original) : default);
            Value(expected, Add, new Temp(original, TempFlags.Modify | TempFlags.Essential) { m_Value = value, m_Cost = cost }, 1);
            Value(expected, Add, default(Game.Tools.Animation));
            Value(expected, Add, default(InterpolatedTransform));
            if ((manager.GetComponentData<BuildingData>(prefab).m_Flags & Game.Prefabs.BuildingFlags.BackAccess) != 0)
                Tag<BackSide>(expected);
            if (definition.m_Elevation != 0f)
                Value(expected, Add, new Game.Objects.Elevation(definition.m_Elevation, ElevationFlags.OnGround));
            Value(expected, Set, manager.HasComponent<PseudoRandomSeed>(original)
                ? manager.GetComponentData<PseudoRandomSeed>(original) : new PseudoRandomSeed((ushort)payload.RootCreation.m_RandomSeed));
            Copy<Damaged>(expected, manager, original);
            Copy<Relative>(expected, manager, original);
            Copy<Extension>(expected, manager, original);
            Copy<GoodsDeliveryVehicle>(expected, manager, original);
            if (manager.HasComponent<UnderConstruction>(original))
            {
                var construction = manager.GetComponentData<UnderConstruction>(original);
                Require(construction.m_NewPrefab == Entity.Null, "RELOCATION_GENERATION_ROOT_CONSTRUCTION_REFERENCE");
                Value(expected, Add, construction, 1);
            }
            return expected;
        }

        private static Dictionary<TypeIndex, Write> ExpectNode(RelocationDefinitionPayload payload, EntityArchetype archetype, bool old)
        {
            var manager = payload.Source.EntityManager;
            Entity original = payload.Original.NodeEntity, prefab = payload.Original.NodePrefab;
            var creation = old ? payload.OldNodeCreation : payload.NewNodeCreation;
            var course = old ? payload.OldNodeCourse : payload.NewNodeCourse;
            var end = course.m_EndPosition;
            Require(manager.Exists(original) && manager.Exists(prefab) && manager.HasComponent<NetGeometryData>(prefab)
                && manager.HasComponent<NetData>(prefab)
                && manager.GetComponentData<NetData>(prefab).m_NodeArchetype.Equals(archetype)
                && !manager.HasComponent<Game.Prefabs.RoadData>(prefab)
                && !manager.HasComponent<EditorContainer>(original) && !manager.HasComponent<ServiceUpgrade>(original)
                && course.m_StartPosition.m_Position.Equals(end.m_Position)
                && course.m_StartPosition.m_Entity == (old ? original : Entity.Null)
                && end.m_Entity == (old ? original : Entity.Null)
                && end.m_Flags == (old ? (CoursePosFlags)0 : CoursePosFlags.IsFirst | CoursePosFlags.IsLast)
                && Finite(end.m_Position, end.m_Rotation) && math.all(math.isfinite(end.m_Elevation)),
                "RELOCATION_GENERATION_NODE_BRANCH_UNQUALIFIED");
            NativeNode pose = old ? payload.Original.NodePose : new NativeNode { m_Position = end.m_Position, m_Rotation = end.m_Rotation };
            var terrain = payload.Source.GetExistingSystemManaged<TerrainSystem>();
            Require(terrain != null && ReferenceEquals(terrain.World, payload.Source), "RELOCATION_GENERATION_NODE_TERRAIN_UNAVAILABLE");
            var height = terrain!.GetHeightData();
            Require(MathUtils.Intersect(MathUtils.Expand(TerrainUtils.GetBounds(ref height).xz, -0.1f), pose.m_Position.xz),
                "RELOCATION_GENERATION_NODE_OUTSIDE_TERRAIN");

            var expected = new Dictionary<TypeIndex, Write>();
            Value(expected, Set, new PrefabRef { m_Prefab = prefab }, 1);
            expected.Add(TypeManager.GetTypeIndex<ServiceUpgrade>(),
                new Write(Remove, null, 0, RelocationPreviewWrite.Bind<ServiceUpgrade>));
            Value(expected, Set, pose);
            Value(expected, Add, default(Orphan), 1);
            var random = new PseudoRandomSeed((ushort)creation.m_RandomSeed).GetRandom(PseudoRandomSeed.kEdgeNodes);
            var seed = old && manager.HasComponent<PseudoRandomSeed>(original)
                ? manager.GetComponentData<PseudoRandomSeed>(original) : new PseudoRandomSeed((ushort)random.NextInt2().y);
            Value(expected, Set, seed);
            bool standalone = !old || manager.HasComponent<Standalone>(original);
            if (standalone) Tag<Standalone>(expected);
            if (manager.HasComponent<LocalConnectData>(prefab)) Tag<LocalConnect>(expected);
            CompositionFlags upgrades = old ? payload.Original.Upgrades : payload.NewNodeUpgraded?.m_Flags ?? default;
            upgrades &= CompositionFlags.nodeMask;
            if (!upgrades.Equals(default(CompositionFlags))) Value(expected, Add, new Upgraded { m_Flags = upgrades });
            if (old)
            {
                if (manager.HasComponent<Game.Net.Elevation>(original))
                    Value(expected, Add, manager.GetComponentData<Game.Net.Elevation>(original));
                if (standalone && manager.HasComponent<Game.Common.Native>(original)) Tag<Game.Common.Native>(expected);
                Copy<Game.Net.Fixed>(expected, manager, original);
            }
            else
            {
                if (math.any(end.m_Elevation != 0f) || end.m_ParentMesh >= 0)
                    Value(expected, Add, new Game.Net.Elevation(end.m_Elevation));
                Value(expected, Add, new Owner(Entity.Null), 1);
                Value(expected, Add, payload.NewNodeOwner, 1);
            }
            Value(expected, Add, new Temp(old ? original : Entity.Null,
                old ? TempFlags.Delete | TempFlags.Hidden : TempFlags.Create | TempFlags.IsLast), 1);
            return expected;
        }

        private static Stream ReadStream(EntityCommandBuffer buffer, EntityArchetype archetype, int bound,
            Entity sourceTarget, int targetCount, int sortKey, ObjectDefinitionCommandProbe.Abi abi)
        {
            Require(buffer.IsCreated, "RELOCATION_GENERATION_COMMAND_BUFFER_UNAVAILABLE");
            Type enabledView = typeof(EntityCommandBuffer).GetNestedType("EntityComponentEnabledCommandView", BindingFlags.NonPublic)
                ?? throw Failure("RELOCATION_GENERATION_COMMAND_ENABLED_ABI");
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(proxy)!;
            Require(commands.Length >= targetCount + 2 && commands.Length <= bound, "RELOCATION_GENERATION_COMMAND_COUNT");
            var targets = new Dictionary<int, Target>(targetCount);
            var markers = new HashSet<TypeIndex>();
            int recordBytes = 0;
            foreach (object command in commands)
            {
                int size = abi.Int(command, "TotalSizeInBytes");
                Require(size > 0 && size <= MaxRecordBytes && abi.Int(command, "SortKey") == sortKey
                    && abi.Int(command, "BatchCount") == 1, "RELOCATION_GENERATION_COMMAND_RECORD");
                recordBytes = checked(recordBytes + size);
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Type view = command.GetType();
                if (kind == "CreateEntity" && view == abi.Create)
                {
                    int identity = abi.Int(command, "EntityIdentityIndex");
                    Require(identity < 0 && targets.Count < targetCount && !targets.ContainsKey(identity)
                        && ((EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype))).Equals(archetype),
                        "RELOCATION_GENERATION_COMMAND_CREATE");
                    targets.Add(identity, new Target());
                    continue;
                }
                Require(abi.Int(command, "IdentityIndex") == 0 && (view == abi.Component && (kind == Add || kind == Set || kind == Remove)
                    || view == enabledView && kind == Disable), "RELOCATION_GENERATION_COMMAND_ROLE");
                var entity = (Entity)abi.Value(command, "Entity", typeof(Entity));
                var type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                Write write = ReadWrite(command, type, kind, abi);
                if (entity.Index >= 0)
                {
                    Require(entity == sourceTarget && kind == Add && write.Value == null && write.References == 0
                        && (type == TypeManager.GetTypeIndex<Hidden>() || type == TypeManager.GetTypeIndex<BatchesUpdated>())
                        && markers.Add(type), "RELOCATION_GENERATION_COMMAND_SOURCE_WRITE");
                    continue;
                }
                Require(targets.TryGetValue(entity.Index, out var target), "RELOCATION_GENERATION_COMMAND_TEMP_TARGET");
                Require((target!.Entity == Entity.Null || target.Entity == entity) && !target.Writes.ContainsKey(type),
                    "RELOCATION_GENERATION_COMMAND_TEMP_TARGET");
                target.Entity = entity;
                target.Writes.Add(type, write);
                target.Order.Add(type);
            }
            Require(targets.Count == targetCount && markers.Count == 2, "RELOCATION_GENERATION_COMMAND_SET_INCOMPLETE");
            return new Stream(targets, commands.Length, recordBytes);
        }

        private static Write ReadWrite(object command, TypeIndex type, string kind, ObjectDefinitionCommandProbe.Abi abi)
        {
            if (kind == Disable)
            {
                Require(type == TypeManager.GetTypeIndex<CustomMeshColor>() && type.IsEnableable
                    && (byte)abi.Value(command, "IsEnabled", typeof(byte)) == 0, "RELOCATION_GENERATION_COMMAND_ENABLEABLE");
                return new Write(kind, null, 0);
            }
            ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
            Require(type.IsComponentType && !type.IsManagedComponent && !type.IsSharedComponentType && !type.IsBuffer
                && !type.IsChunkComponent && !type.HasNativeContainer && info.Category == TypeManager.TypeCategory.ComponentData
                && !info.HasBlobAssetRefs && !info.HasWeakAssetRefs && !info.HasUnityObjectRefs
                && info.SizeInChunk >= 0 && info.SizeInChunk <= MaxTypeBytes
                && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)), "RELOCATION_GENERATION_COMMAND_COMPONENT_ABI");
            short size = (short)abi.Value(command, "ComponentSize", typeof(short));
            object? value = abi.Value(command, "ComponentValue", typeof(object));
            Require(size == (kind == Remove ? 0 : info.SizeInChunk)
                && (size == 0 ? value == null : value != null && value.GetType() == TypeManager.GetType(type)),
                "RELOCATION_GENERATION_COMMAND_COMPONENT_VALUE");
            return new Write(kind, value, kind == Remove ? 0 : info.EntityOffsetCount);
        }

        private static Target FindByOriginal(Stream stream, Entity original)
        {
            Target? result = null;
            foreach (Target target in stream.Targets.Values)
            {
                Require(target.Entity.Index < 0 && target.Writes.TryGetValue(TypeManager.GetTypeIndex<Temp>(), out _),
                    "RELOCATION_GENERATION_COMMAND_TEMP_MISSING");
                if (target.Temp.m_Original != original) continue;
                Require(result == null, "RELOCATION_GENERATION_COMMAND_DUPLICATE_ORIGINAL");
                result = target;
            }
            return result ?? throw Failure("RELOCATION_GENERATION_COMMAND_ORIGINAL_MISSING");
        }

        private static RelocationPreviewWrite[] Verify(Target target, Dictionary<TypeIndex, Write> expected, string code)
        {
            foreach (var pair in expected)
            {
                if (!target.Writes.TryGetValue(pair.Key, out var actual)) throw ComponentFailure(pair.Key, "MISSING");
                if (actual.Kind != pair.Value.Kind) throw ComponentFailure(pair.Key, "KIND");
                if (actual.References != pair.Value.References) throw ComponentFailure(pair.Key, "REFERENCES");
                if (Equals(actual.Value, pair.Value.Value)) continue;
                if (actual.Value is Temp observed && pair.Value.Value is Temp wanted)
                {
                    if (observed.m_Flags != wanted.m_Flags) throw ComponentFailure(pair.Key, "FLAGS");
                    if (observed.m_Cost != wanted.m_Cost) throw ComponentFailure(pair.Key, "COST");
                }
                throw ComponentFailure(pair.Key, "VALUE");
            }
            var verified = new RelocationPreviewWrite[target.Order.Count];
            for (int index = 0; index < target.Order.Count; index++)
            {
                TypeIndex type = target.Order[index];
                if (!expected.TryGetValue(type, out var declaration) || declaration.Bind == null)
                    throw ComponentFailure(type, "UNEXPECTED");
                Write actual = target.Writes[type];
                verified[index] = declaration.Bind(actual.Kind, actual.Value);
            }
            return verified;

            InvalidOperationException ComponentFailure(TypeIndex type, string reason) =>
                Failure(code + "_" + TypeManager.GetType(type).Name.ToUpperInvariant() + "_" + reason);
        }

        private static void Copy<T>(Dictionary<TypeIndex, Write> target, EntityManager manager, Entity source)
            where T : unmanaged, IComponentData
        {
            if (manager.HasComponent<T>(source)) Value(target, Add, manager.GetComponentData<T>(source));
        }

        private static void Value<T>(Dictionary<TypeIndex, Write> target, string kind, T value, int references = 0)
            where T : unmanaged, IComponentData
        {
            TypeIndex type = TypeManager.GetTypeIndex<T>();
            target.Add(type, new Write(kind, TypeManager.GetTypeInfo(type).SizeInChunk == 0 ? null : (object)value,
                references, RelocationPreviewWrite.Bind<T>));
        }

        private static void Tag<T>(Dictionary<TypeIndex, Write> target) where T : unmanaged, IComponentData =>
            Value(target, Add, default(T));

        private static RelocationPreviewWrite BindMeshColor(string kind, object? value) =>
            RelocationPreviewWrite.BindDisabled(ComponentType.ReadWrite<CustomMeshColor>(), kind, value);

        private static JObject Describe(Target target, Entity prefab)
        {
            Temp temp = target.Temp;
            NativeTransform pose;
            if (target.Writes.TryGetValue(TypeManager.GetTypeIndex<NativeTransform>(), out var transform))
                pose = (NativeTransform)transform.Value!;
            else
            {
                var node = (NativeNode)target.Writes[TypeManager.GetTypeIndex<NativeNode>()].Value!;
                pose = new NativeTransform(node.m_Position, node.m_Rotation);
            }
            var owner = target.Writes.TryGetValue(TypeManager.GetTypeIndex<OwnerDefinition>(), out var definition)
                ? (OwnerDefinition?)definition.Value : null;
            return new JObject
            {
                ["deferredTarget"] = Id(target.Entity), ["original"] = Id(temp.m_Original), ["prefab"] = Id(prefab),
                ["flags"] = (uint)temp.m_Flags, ["value"] = temp.m_Value, ["cost"] = temp.m_Cost,
                ["position"] = Vector(pose.m_Position), ["rotation"] = Rotation(pose.m_Rotation),
                ["ownerDefinition"] = owner.HasValue ? new JObject
                {
                    ["prefab"] = Id(owner.Value.m_Prefab), ["position"] = Vector(owner.Value.m_Position),
                    ["rotation"] = Rotation(owner.Value.m_Rotation)
                } : null,
                ["writesObserved"] = target.Writes.Count
            };
        }

        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray Rotation(quaternion value) => new JArray(value.value.x, value.value.y, value.value.z, value.value.w);
        private static bool Valid(EntityArchetype value) => value.Valid && value.TypesCount > 0 && value.TypesCount <= 256;
        private static bool IsDetached(CreationDefinition value) => value.m_Owner == Entity.Null && value.m_Attached == Entity.Null
            && value.m_SubPrefab == Entity.Null;
        private static bool Finite(float3 position, quaternion rotation) => math.all(math.isfinite(position)) && math.all(math.isfinite(rotation.value));
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static InvalidOperationException Failure(string code) => new InvalidOperationException(code);

        private readonly struct Write
        {
            internal readonly string Kind;
            internal readonly object? Value;
            internal readonly int References;
            internal readonly Func<string, object?, RelocationPreviewWrite>? Bind;
            internal Write(string kind, object? value, int references, Func<string, object?, RelocationPreviewWrite>? bind = null)
            { Kind = kind; Value = value; References = references; Bind = bind; }
        }

        private sealed class Target
        {
            internal Entity Entity;
            internal readonly Dictionary<TypeIndex, Write> Writes = new Dictionary<TypeIndex, Write>();
            internal readonly List<TypeIndex> Order = new List<TypeIndex>(16);
            internal Temp Temp => Writes[TypeManager.GetTypeIndex<Temp>()].Value is Temp value
                ? value : throw Failure("RELOCATION_GENERATION_COMMAND_TEMP_VALUE");
        }

        private sealed class Stream
        {
            internal readonly Dictionary<int, Target> Targets;
            internal readonly int CommandCount, RecordBytes;
            internal Stream(Dictionary<int, Target> targets, int commandCount, int recordBytes)
            { Targets = targets; CommandCount = commandCount; RecordBytes = recordBytes; }
        }
    }
}
#endif
