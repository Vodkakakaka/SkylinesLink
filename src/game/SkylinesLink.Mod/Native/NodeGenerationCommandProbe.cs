#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using NativeNode = Game.Net.Node;
using NativeElevation = Game.Net.Elevation;
using NativeFixed = Game.Net.Fixed;
using NativeEditorContainer = Game.Tools.EditorContainer;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Observes the complete admitted one-node ECB without playing it back.</summary>
    internal static class NodeGenerationCommandProbe
    {
        private const int ComponentSizeUpperBound = 1024;

        internal static JObject Capture(NodeGenerationProducer producer, ObjectDefinitionPayload payload,
            EntityArchetype expectedArchetype, out GeneratedNodePayload? generatedPayload)
        {
            generatedPayload = null;
            if (producer == null || payload == null) throw Failure();
            int bound = 14 + (payload.CourseCreation.m_SubPrefab != Entity.Null ? 1 : 0);
            if (!ReferenceEquals(producer.World, payload.Source) || !producer.CompletedFor(payload)
                || producer.OutputCommandUpperBound != bound || !producer.ArchetypeMatches(expectedArchetype)
                || !expectedArchetype.Valid || expectedArchetype.TypesCount < 1 || expectedArchetype.TypesCount > 256
                || !Detached(payload.CourseCreation) || !Detached(payload.RootCreation)
                || payload.RootCreation.m_SubPrefab != Entity.Null
                || !payload.IsDetachedSingleNodeCourse
                || !Finite(payload.Course.m_EndPosition.m_Rotation.value) || !Finite(payload.Course.m_EndPosition.m_Elevation)
                || payload.CourseOwner.m_Prefab != payload.RootCreation.m_Prefab
                || !payload.CourseOwner.m_Position.Equals(payload.RootObject.m_Position)
                || !payload.CourseOwner.m_Rotation.Equals(payload.RootObject.m_Rotation)) throw Failure();

            // This producer has already proved the one-entry queue, empty old
            // map and spatial no-hit bound. Only then may this getter walk all
            // native commands; the length check below is a consistency check.
            var abi = new ObjectDefinitionCommandProbe.Abi();
            EntityCommandBuffer buffer = producer.BorrowCompletedForInspection(payload);
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(proxy)!;
            if (commands.Length < 8 || commands.Length > bound) throw Failure();

            int? identity = null, version = null;
            long recordBytes = 0;
            int writes = 0, removes = 0;
            var seen = new HashSet<Type>();
            var records = new JArray();
            var typedWrites = new List<GeneratedNodeWrite>(commands.Length - 1);
            NativeNode? node = null;
            Temp? temp = null;
            NativeElevation? elevation = null;
            NativeFixed? fixedValue = null;
            PseudoRandomSeed? seed = null;
            Game.Net.Orphan? orphan = null;
            Game.Net.OutsideConnection? outside = null;
            NativeEditorContainer? editor = null;
            bool prefabSeen = false, ownerSeen = false, ownerDefinitionSeen = false;
            bool standaloneSeen = false, localConnectSeen = false, serviceUpgradeRemoved = false;

            var random = new PseudoRandomSeed((ushort)payload.CourseCreation.m_RandomSeed).GetRandom(PseudoRandomSeed.kEdgeNodes);
            ushort expectedSeed = (ushort)random.NextInt2().y;
            CoursePos end = payload.Course.m_EndPosition;
            TempFlags expectedFlags = TempFlags.Create;
            if ((end.m_Flags & (CoursePosFlags.IsLast | CoursePosFlags.IsParallel)) == CoursePosFlags.IsLast)
                expectedFlags |= TempFlags.IsLast;

            foreach (object command in commands)
            {
                int size = abi.Int(command, "TotalSizeInBytes");
                // CreateNodesJob owns a non-parallel ECB. Its pinned Entities
                // main-thread chain uses int.MaxValue, unlike the object job.
                if (size <= 0 || size > ComponentSizeUpperBound + 1024 || abi.Int(command, "SortKey") != int.MaxValue)
                    throw Failure();
                recordBytes = checked(recordBytes + size);
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Type view = command.GetType();
                if (kind == "CreateEntity" && view == abi.Create)
                {
                    int created = abi.Int(command, "EntityIdentityIndex");
                    if (identity.HasValue || records.Count != 0 || created >= 0 || abi.Int(command, "BatchCount") != 1
                        || !((EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype))).Equals(expectedArchetype))
                        throw Failure();
                    identity = created;
                    records.Add(new JObject { ["kind"] = kind, ["recordBytes"] = size });
                    continue;
                }

                if (view != abi.Component || (kind != "AddComponent" && kind != "SetComponent" && kind != "RemoveComponent")
                    || !identity.HasValue || abi.Int(command, "BatchCount") != 1 || abi.Int(command, "IdentityIndex") != 0)
                    throw Failure();
                Entity target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                if (target.Index >= 0 || target.Index != identity.Value || (version.HasValue && version.Value != target.Version))
                    throw Failure();
                version = target.Version;
                TypeIndex index = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                Type type = TypeManager.GetType(index);
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                if (!Known(type) || !index.IsComponentType || index.IsManagedComponent || index.IsSharedComponentType
                    || index.IsBuffer || index.IsChunkComponent || index.HasNativeContainer
                    || info.Category != TypeManager.TypeCategory.ComponentData || info.SizeInChunk < 0
                    || info.SizeInChunk > ComponentSizeUpperBound || !seen.Add(type)
                    || (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))) throw Failure();
                short bytes = (short)abi.Value(command, "ComponentSize", typeof(short));
                object? value = abi.Value(command, "ComponentValue", typeof(object));
                var record = new JObject { ["kind"] = kind, ["component"] = type.FullName,
                    ["recordBytes"] = size, ["componentBytes"] = bytes, ["entityOffsetCount"] = info.EntityOffsetCount };

                if (kind == "RemoveComponent")
                {
                    // A remove view has no value, even for a nonempty type.
                    if (type != typeof(Game.Buildings.ServiceUpgrade) || bytes != 0 || value != null) throw Failure();
                    if (!GeneratedNodeWrite.TryCapture(type, kind, value, bytes, out var remove)) throw Failure();
                    typedWrites.Add(remove);
                    serviceUpgradeRemoved = true; removes++; records.Add(record); continue;
                }
                if (type == typeof(Game.Buildings.ServiceUpgrade) || bytes != info.SizeInChunk
                    || (bytes == 0 ? value != null : value == null || value.GetType() != type)) throw Failure();

                int expectedReferences = 0;
                if (type == typeof(PrefabRef))
                {
                    if (kind != "SetComponent" || !(value is PrefabRef actual) || actual.m_Prefab != payload.CourseCreation.m_Prefab)
                        throw Failure();
                    expectedReferences = 1; prefabSeen = true;
                }
                else if (type == typeof(NativeNode))
                {
                    if (kind != "SetComponent" || !(value is NativeNode actual) || !Finite(actual.m_Position)
                        || !Finite(actual.m_Rotation.value) || !actual.m_Position.Equals(end.m_Position)
                        || !actual.m_Rotation.Equals(end.m_Rotation)) throw Failure();
                    node = actual;
                }
                else if (type == typeof(Temp))
                {
                    if (kind != "AddComponent" || !(value is Temp actual) || actual.m_Original != Entity.Null
                        || actual.m_Flags != expectedFlags || !math.isfinite(actual.m_CurvePosition)
                        || actual.m_CurvePosition != 0f || actual.m_Value != 0 || actual.m_Cost != 0) throw Failure();
                    expectedReferences = 1; temp = actual;
                }
                else if (type == typeof(Owner))
                {
                    if (kind != "AddComponent" || !(value is Owner actual) || actual.m_Owner != Entity.Null) throw Failure();
                    expectedReferences = 1; ownerSeen = true;
                }
                else if (type == typeof(OwnerDefinition))
                {
                    if (kind != "AddComponent" || !(value is OwnerDefinition actual) || !actual.Equals(payload.CourseOwner)
                        || !Finite(actual.m_Position) || !Finite(actual.m_Rotation.value)) throw Failure();
                    expectedReferences = 1; ownerDefinitionSeen = true;
                }
                else if (type == typeof(NativeEditorContainer))
                {
                    if (kind != "SetComponent" || !(value is NativeEditorContainer actual)
                        || payload.CourseCreation.m_SubPrefab == Entity.Null || actual.m_Prefab != payload.CourseCreation.m_SubPrefab
                        || !actual.m_Scale.Equals(default(float3)) || actual.m_Intensity != 0f || actual.m_GroupIndex != 0) throw Failure();
                    expectedReferences = 1; editor = actual;
                }
                else if (type == typeof(Game.Net.Orphan))
                {
                    if (kind != "AddComponent" || !(value is Game.Net.Orphan actual) || actual.m_Composition != Entity.Null) throw Failure();
                    expectedReferences = 1; orphan = actual;
                }
                else if (type == typeof(PseudoRandomSeed))
                {
                    if (kind != "SetComponent" || !(value is PseudoRandomSeed actual) || actual.m_Seed != expectedSeed) throw Failure();
                    seed = actual;
                }
                else if (type == typeof(NativeElevation))
                {
                    if (kind != "AddComponent" || !(value is NativeElevation actual) || !Finite(actual.m_Elevation)
                        || !actual.m_Elevation.Equals(end.m_Elevation)) throw Failure();
                    elevation = actual;
                }
                else if (type == typeof(NativeFixed))
                {
                    if (kind != "AddComponent" || !(value is NativeFixed actual) || actual.m_Index != payload.Course.m_FixedIndex) throw Failure();
                    fixedValue = actual;
                }
                else if (type == typeof(Game.Net.OutsideConnection))
                {
                    if (kind != "AddComponent" || !(value is Game.Net.OutsideConnection actual) || !math.isfinite(actual.m_Delay)
                        || actual.m_Delay != 0f) throw Failure();
                    outside = actual;
                }
                else
                {
                    if (kind != "AddComponent" || bytes != 0 || value != null) throw Failure();
                    if (type == typeof(Game.Net.Standalone)) standaloneSeen = true;
                    else if (type == typeof(Game.Net.LocalConnect)) localConnectSeen = true;
                    else throw Failure();
                }
                if (info.EntityOffsetCount != expectedReferences) throw Failure();
                if (!GeneratedNodeWrite.TryCapture(type, kind, value, bytes, out var write)) throw Failure();
                typedWrites.Add(write);
                writes++; records.Add(record);
            }

            if (!identity.HasValue || !version.HasValue || !prefabSeen || !node.HasValue || !temp.HasValue
                || !ownerSeen || !ownerDefinitionSeen || !standaloneSeen || !serviceUpgradeRemoved
                || editor.HasValue != (payload.CourseCreation.m_SubPrefab != Entity.Null)
                || elevation.HasValue != (math.any(end.m_Elevation != 0f) || end.m_ParentMesh >= 0)
                || fixedValue.HasValue != ((end.m_Flags & CoursePosFlags.IsFixed) != 0)
                || orphan.HasValue != seed.HasValue || typedWrites.Count != commands.Length - 1) throw Failure();

            NativeNode actualNode = node.Value;
            Temp actualTemp = temp.Value;
            var typedPayload = new GeneratedNodePayload(payload.Source, expectedArchetype, typedWrites);
            var observation = new JObject {
                ["kind"] = "native-node-generation-command-observation", ["commandUpperBound"] = bound,
                ["commandsObserved"] = commands.Length, ["createCommands"] = 1,
                ["componentWriteCommands"] = writes, ["removeCommands"] = removes,
                ["materializationInputSupported"] = true, ["typedOperationCount"] = typedPayload.WriteCount,
                ["target"] = EntityValue(new Entity { Index = identity.Value, Version = version.Value }),
                ["sourceArchetypeMatched"] = true, ["prefab"] = EntityValue(payload.CourseCreation.m_Prefab),
                ["subPrefab"] = EntityValue(payload.CourseCreation.m_SubPrefab),
                ["node"] = new JObject { ["position"] = Vector(actualNode.m_Position), ["rotation"] = Rotation(actualNode.m_Rotation) },
                ["temp"] = new JObject { ["original"] = EntityValue(actualTemp.m_Original), ["flags"] = (uint)actualTemp.m_Flags,
                    ["curvePosition"] = actualTemp.m_CurvePosition, ["value"] = actualTemp.m_Value, ["cost"] = actualTemp.m_Cost },
                ["owner"] = EntityValue(Entity.Null),
                ["ownerDefinition"] = new JObject { ["prefab"] = EntityValue(payload.CourseOwner.m_Prefab),
                    ["position"] = Vector(payload.CourseOwner.m_Position), ["rotation"] = Rotation(payload.CourseOwner.m_Rotation) },
                ["standalone"] = standaloneSeen, ["localConnect"] = localConnectSeen,
                ["orphan"] = !orphan.HasValue ? JValue.CreateNull() : (JToken)new JObject { ["composition"] = EntityValue(orphan.Value.m_Composition) },
                ["randomSeed"] = !seed.HasValue ? JValue.CreateNull() : new JValue((int)seed.Value.m_Seed),
                ["elevation"] = !elevation.HasValue ? JValue.CreateNull() : (JToken)Pair(elevation.Value.m_Elevation),
                ["fixedIndex"] = !fixedValue.HasValue ? JValue.CreateNull() : new JValue(fixedValue.Value.m_Index),
                ["outsideConnection"] = !outside.HasValue ? JValue.CreateNull() : (JToken)new JObject { ["delay"] = outside.Value.m_Delay },
                ["editorContainer"] = !editor.HasValue ? JValue.CreateNull() : (JToken)new JObject {
                    ["prefab"] = EntityValue(editor.Value.m_Prefab), ["scale"] = Vector(editor.Value.m_Scale),
                    ["intensity"] = editor.Value.m_Intensity, ["groupIndex"] = editor.Value.m_GroupIndex },
                ["commandEntityReferencesClosed"] = true, ["externalTargetCommands"] = 0, ["bufferCommands"] = 0,
                ["recordBytes"] = recordBytes, ["recordBytesAreHeapBytes"] = false, ["commands"] = records,
                ["playbackCalled"] = false, ["generatedNodeEntityObserved"] = false,
                ["completePreviewObserved"] = false, ["qualificationObserved"] = false, ["feesChargedObserved"] = false,
                ["scope"] = "Complete unplayed new-node command stream only; no Source node, finalized references, placement or payment acceptance."
            };
            generatedPayload = typedPayload;
            return observation;
        }

        private static bool Known(Type type) => type == typeof(PrefabRef) || type == typeof(NativeNode) || type == typeof(Temp)
            || type == typeof(Owner) || type == typeof(OwnerDefinition) || type == typeof(NativeEditorContainer)
            || type == typeof(Game.Net.Orphan) || type == typeof(PseudoRandomSeed) || type == typeof(NativeElevation)
            || type == typeof(NativeFixed) || type == typeof(Game.Net.OutsideConnection) || type == typeof(Game.Net.Standalone)
            || type == typeof(Game.Net.LocalConnect) || type == typeof(Game.Buildings.ServiceUpgrade);
        private static bool Detached(CreationDefinition value) => value.m_Flags == (CreationFlags)0
            && value.m_Original == Entity.Null && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null
            && value.m_Prefab != Entity.Null && value.m_Prefab.Index >= 0;
        private static bool Finite(float2 value) => math.all(math.isfinite(value));
        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static bool Finite(float4 value) => math.all(math.isfinite(value));
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray Pair(float2 value) => new JArray(value.x, value.y);
        private static JArray Rotation(quaternion value) => new JArray(value.value.x, value.value.y, value.value.z, value.value.w);
        private static JObject EntityValue(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static InvalidOperationException Failure() => new InvalidOperationException("NODE_GENERATION_COMMANDS_UNQUALIFIED");
    }
}
#endif
