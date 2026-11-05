#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Observes one qualified new-root ECB, without materializing its entities.</summary>
    internal static class ObjectGenerationCommandProbe
    {
        private const int CommandUpperBound = 64;
        private const int ComponentSizeUpperBound = 1024;

        internal static JObject Capture(ObjectGenerationProducer producer, ObjectDefinitionPayload payload,
            EntityArchetype expectedArchetype, out GeneratedObjectPayload? generatedPayload)
        {
            generatedPayload = null;
            if (producer == null || payload == null || !ReferenceEquals(producer.World, payload.Source) ||
                !producer.CompletedFor(payload) || producer.OutputCommandUpperBound != CommandUpperBound ||
                !producer.ArchetypeMatches(expectedArchetype) ||
                !expectedArchetype.Valid || expectedArchetype.TypesCount < 1 || expectedArchetype.TypesCount > 256 ||
                !Detached(payload.RootCreation) || payload.RootCreation.m_SubPrefab != Entity.Null ||
                !Finite(payload.RootObject.m_Position, payload.RootObject.m_Rotation)) throw Failure();

            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type enabledView = typeof(EntityCommandBuffer).GetNestedType("EntityComponentEnabledCommandView",
                BindingFlags.NonPublic) ?? throw Failure();
            // Borrowing requires this producer's completed, admitted root1 run.
            // The fixed native body has 64 ECB call sites, no repeated command
            // in its sole archetype loop, and no foreign queue expansion in
            // that admitted case. The bound precedes the full debugger walk.
            EntityCommandBuffer buffer = producer.BorrowCompletedForInspection(payload);
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(proxy)!;
            if (commands.Length < 4 || commands.Length > CommandUpperBound) throw Failure();

            int? identity = null, version = null;
            int componentWrites = 0, enabledWrites = 0;
            long recordBytes = 0;
            var writtenTypes = new HashSet<Type>();
            var enabledTypes = new HashSet<Type>();
            var records = new JArray();
            Transform? transform = null;
            Temp? temp = null;
            bool prefabObserved = false;
            bool typedWritesSupported = true;
            var typedWrites = new List<GeneratedObjectWrite>(8);
            foreach (object command in commands)
            {
                int size = abi.Int(command, "TotalSizeInBytes");
                if (size <= 0 || size > ComponentSizeUpperBound + 1024 || abi.Int(command, "SortKey") != 0)
                    throw Failure();
                recordBytes = checked(recordBytes + size);
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Type view = command.GetType();
                if (kind == "CreateEntity" && view == abi.Create)
                {
                    int created = abi.Int(command, "EntityIdentityIndex");
                    if (identity.HasValue || records.Count != 0 || created >= 0 || abi.Int(command, "BatchCount") != 1 ||
                        !((EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype))).Equals(expectedArchetype))
                        throw Failure();
                    identity = created;
                    records.Add(new JObject { ["kind"] = kind, ["recordBytes"] = size });
                    continue;
                }

                bool isComponent = view == abi.Component && (kind == "AddComponent" || kind == "SetComponent");
                bool isEnabled = view == enabledView && kind == "SetComponentEnabled";
                // No buffers, removes, bulk commands, query targets or external
                // entities can enter this observation through a broader view.
                if ((!isComponent && !isEnabled) || !identity.HasValue || abi.Int(command, "BatchCount") != 1 ||
                    abi.Int(command, "IdentityIndex") != 0) throw Failure();
                Entity target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                if (target.Index != identity.Value || target.Index >= 0 ||
                    (version.HasValue && version.Value != target.Version)) throw Failure();
                version = target.Version;
                TypeIndex index = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                Type component = TypeManager.GetType(index);
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                var record = new JObject { ["kind"] = kind, ["component"] = component.FullName,
                    ["recordBytes"] = size };
                if (isEnabled)
                {
                    // The native new-root path only disables these two known
                    // enableable types; it does not inspect their buffer data.
                    byte enabled = (byte)abi.Value(command, "IsEnabled", typeof(byte));
                    if ((component != typeof(Game.Rendering.CustomMeshColor) && component != typeof(Decoration)) ||
                        !index.IsEnableable || info.EntityOffsetCount != 0 || enabled != 0 || !enabledTypes.Add(component))
                        throw Failure();
                    record["enabled"] = false;
                    if (GeneratedObjectWrite.TryCapture(component, kind, false, 0, out var enabledWrite)) typedWrites.Add(enabledWrite);
                    else typedWritesSupported = false;
                    enabledWrites++;
                    records.Add(record);
                    continue;
                }

                short componentSize = (short)abi.Value(command, "ComponentSize", typeof(short));
                bool requiresFixup = (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool));
                if (!index.IsComponentType || index.IsManagedComponent || index.IsSharedComponentType ||
                    index.IsBuffer || index.IsChunkComponent || index.HasNativeContainer ||
                    info.Category != TypeManager.TypeCategory.ComponentData || info.SizeInChunk < 0 ||
                    info.SizeInChunk > ComponentSizeUpperBound || componentSize != info.SizeInChunk || requiresFixup ||
                    !writtenTypes.Add(component)) throw Failure();
                object? value = abi.Value(command, "ComponentValue", typeof(object));
                if (componentSize == 0 ? value != null : value == null || value.GetType() != component) throw Failure();
                if (component == typeof(PrefabRef))
                {
                    if (kind != "SetComponent" || value == null ||
                        ((PrefabRef)value).m_Prefab != payload.RootCreation.m_Prefab) throw Failure();
                    prefabObserved = true;
                }
                else if (component == typeof(Temp))
                {
                    if (kind != "AddComponent" || value == null) throw Failure();
                    var actual = (Temp)value;
                    if (actual.m_Original != Entity.Null || actual.m_Flags != (TempFlags.Create | TempFlags.Essential) ||
                        !math.isfinite(actual.m_CurvePosition) || actual.m_Cost != actual.m_Value) throw Failure();
                    temp = actual;
                }
                else
                {
                    // This is an entity-reference closure check, not an
                    // arbitrary recursive serializer of component contents.
                    if (info.EntityOffsetCount != 0) throw Failure();
                    if (component == typeof(Transform))
                    {
                        if (kind != "SetComponent" || value == null) throw Failure();
                        var actual = (Transform)value;
                        if (!Finite(actual.m_Position, actual.m_Rotation) ||
                            !actual.m_Position.Equals(payload.RootObject.m_Position) ||
                            !actual.m_Rotation.Equals(payload.RootObject.m_Rotation)) throw Failure();
                        transform = actual;
                    }
                }
                record["componentBytes"] = componentSize;
                if (GeneratedObjectWrite.TryCapture(component, kind, value, componentSize, out var typedWrite)) typedWrites.Add(typedWrite);
                else typedWritesSupported = false;
                record["entityOffsetCount"] = info.EntityOffsetCount;
                componentWrites++;
                records.Add(record);
            }

            if (!identity.HasValue || !version.HasValue || !prefabObserved || !temp.HasValue || !transform.HasValue)
                throw Failure();
            Temp actualTemp = temp.Value;
            Transform actualTransform = transform.Value;
            if (typedWritesSupported && commands.Length == 9 && typedWrites.Count == 8)
                generatedPayload = new GeneratedObjectPayload(payload.Source, expectedArchetype, typedWrites);
            return new JObject {
                ["kind"] = "native-object-generation-command-observation",
                ["commandUpperBound"] = CommandUpperBound, ["commandsObserved"] = commands.Length,
                ["createCommands"] = 1, ["componentWriteCommands"] = componentWrites,
                ["enabledWriteCommands"] = enabledWrites,
                ["target"] = new JObject { ["index"] = identity.Value, ["version"] = version.Value },
                ["sourceArchetypeMatched"] = true,
                ["prefab"] = new JObject { ["index"] = payload.RootCreation.m_Prefab.Index,
                    ["version"] = payload.RootCreation.m_Prefab.Version },
                ["transform"] = new JObject { ["position"] = Vector(actualTransform.m_Position),
                    ["rotation"] = new JArray(actualTransform.m_Rotation.value.x, actualTransform.m_Rotation.value.y,
                        actualTransform.m_Rotation.value.z, actualTransform.m_Rotation.value.w) },
                ["temp"] = new JObject { ["originalIsNull"] = true, ["flags"] = (uint)actualTemp.m_Flags,
                    ["curvePosition"] = actualTemp.m_CurvePosition, ["value"] = actualTemp.m_Value,
                    ["cost"] = actualTemp.m_Cost },
                ["commandEntityReferencesClosed"] = true, ["externalTargetCommands"] = 0,
                ["bufferCommands"] = 0, ["recordBytes"] = recordBytes, ["recordBytesAreHeapBytes"] = false,
                ["commands"] = records, ["playbackCalled"] = false,
                ["tempEntityObserved"] = false, ["completeAttachmentsObserved"] = false,
                ["qualificationObserved"] = false, ["feesChargedObserved"] = false,
                ["scope"] = "One unplayed new-root command stream; final entities, attachments, eligibility and payment are not observed."
            };
        }

        private static bool Detached(CreationDefinition value) => value.m_Flags == (CreationFlags)0 &&
            value.m_Original == Entity.Null && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null &&
            value.m_Prefab != Entity.Null && value.m_Prefab.Index >= 0;
        private static bool Finite(float3 position, quaternion rotation) =>
            math.all(math.isfinite(position)) && math.all(math.isfinite(rotation.value));
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static InvalidOperationException Failure() =>
            new InvalidOperationException("OBJECT_GENERATION_COMMANDS_UNQUALIFIED");
    }
}
#endif
