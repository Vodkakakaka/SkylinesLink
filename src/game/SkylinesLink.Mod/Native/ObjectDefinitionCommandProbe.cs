#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Common;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    /// <summary>
    /// Version-bound debugger observation of this one native producer, never
    /// Playback. This is not a public ECS command-reader or a construction API.
    /// Buffer contents remain opaque; no native pointers are read or retained.
    /// </summary>
    internal static class ObjectDefinitionCommandProbe
    {
        internal static JObject Capture(ObjectDefinitionProducer producer, ObjectDefinitionInputProbe.Result input,
            out ObjectDefinitionPayload? payload)
        {
            payload = null;
            if (producer == null || input == null || !input.Complete || input.Root == Entity.Null ||
                !ReferenceEquals(producer.World, input.Source) || producer.CapturedPrefab != input.Root ||
                input.Candidates.Count > 128) throw Failure();
            ObjectDefinitionInputProbe.CandidateStatistics? selected = null;
            foreach (var candidate in input.Candidates)
                if (candidate.RootIndex == input.Root.Index && candidate.RootVersion == input.Root.Version)
                {
                    if (selected != null) throw Failure();
                    selected = candidate;
                }
            if (selected == null || !selected.Eligible || !selected.Complete || !selected.InspectedReferencesComplete) throw Failure();
            Entity expectedPrefab = input.Root;
            int rootSubNets = selected.RootSubNetCount, rootSubAreas = selected.RootSubAreaCount;
            int areaOutputNodeUpperBound = selected.AreaOutputNodeUpperBound;
            // This admission bound is proved from the fixed producer's complete
            // normal/new-only/single-point branch and the caller's closed input.
            // It precedes the debugger's full walk/array/boxing allocations.
            if (rootSubNets < 0 || rootSubNets > 256 || rootSubAreas < 0 || rootSubAreas > 256 ||
                areaOutputNodeUpperBound < 0 || areaOutputNodeUpperBound > 4096)
                throw Failure();
            int upperBound = 4 + 6 * rootSubNets + 5 * rootSubAreas;
            if (upperBound != selected.CommandUpperBound) throw Failure();
            int entityBound = 1 + rootSubNets + rootSubAreas;
            var abi = new Abi();
            EntityCommandBuffer buffer = producer.BorrowCompletedForInspection();
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(proxy)!;
            if (commands.Length < 4 || commands.Length > upperBound) throw Failure();

            var targets = new Dictionary<int, Target>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int addComponents = 0, addBuffers = 0;
            int? bufferVersion = null;
            long recordBytes = 0;
            foreach (object command in commands)
            {
                int size = abi.Int(command, "TotalSizeInBytes");
                if (size <= 0) throw Failure();
                recordBytes = checked(recordBytes + size);
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Type view = command.GetType();
                if (kind == "CreateEntity" && view == abi.Create)
                {
                    int identity = abi.Int(command, "EntityIdentityIndex");
                    // The selected job interleaves each Create with its fields;
                    // it has no adjacent batch or source-archetype creation.
                    if (identity >= 0 || abi.Int(command, "BatchCount") != 1 || targets.ContainsKey(identity) ||
                        !((EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype))).Equals(default(EntityArchetype)) ||
                        targets.Count == entityBound) throw Failure();
                    targets.Add(identity, new Target());
                    continue;
                }
                if (abi.Int(command, "BatchCount") != 1) throw Failure();
                Entity target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                if (target.Index >= 0 || !targets.TryGetValue(target.Index, out Target row)) throw Failure();
                if (bufferVersion.HasValue && bufferVersion.Value != target.Version) throw Failure();
                bufferVersion = target.Version;
                row.DeferredEntity = target;
                Type component = TypeManager.GetType((TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)));
                if (kind == "AddComponent" && view == abi.Component)
                {
                    if (!KnownComponent(component) || !row.Types.Add(component)) throw Failure();
                    object? value = abi.Value(command, "ComponentValue", typeof(object));
                    if (component == typeof(Updated))
                    { if (value != null) throw Failure(); }
                    else if (value == null || value.GetType() != component) throw Failure();
                    if (component == typeof(CreationDefinition))
                    {
                        var creation = (CreationDefinition)value!;
                        if (creation.m_Prefab == Entity.Null || creation.m_Prefab.Index < 0 ||
                            creation.m_Original != Entity.Null || creation.m_Owner != Entity.Null) throw Failure();
                        row.Prefab = creation.m_Prefab;
                        row.CreationFlags = creation.m_Flags;
                        row.Creation = creation;
                    }
                    else if (component == typeof(ObjectDefinition))
                    {
                        var definition = (ObjectDefinition)value!;
                        if (!math.all(math.isfinite(definition.m_Position)) || !math.all(math.isfinite(definition.m_Rotation.value)) ||
                            !math.all(math.isfinite(definition.m_LocalPosition)) || !math.all(math.isfinite(definition.m_LocalRotation.value)) ||
                            !math.all(math.isfinite(definition.m_Scale)) || !math.isfinite(definition.m_Elevation) ||
                            !math.isfinite(definition.m_Intensity) || !math.isfinite(definition.m_Age))
                            throw Failure();
                        row.Object = definition;
                    }
                    else if (component == typeof(OwnerDefinition))
                    {
                        var owner = (OwnerDefinition)value!;
                        if (!math.all(math.isfinite(owner.m_Position)) || !math.all(math.isfinite(owner.m_Rotation.value))) throw Failure();
                        row.Owner = owner;
                    }
                    else if (component == typeof(NetCourse))
                    {
                        var course = (NetCourse)value!;
                        if (!math.all(math.isfinite(course.m_Curve.a)) || !math.all(math.isfinite(course.m_Curve.b)) ||
                            !math.all(math.isfinite(course.m_Curve.c)) || !math.all(math.isfinite(course.m_Curve.d)) ||
                            !math.all(math.isfinite(course.m_Elevation)) || !math.isfinite(course.m_Length) || course.m_Length < 0 ||
                            !Finite(course.m_StartPosition) || !Finite(course.m_EndPosition)) throw Failure();
                        // Fixed unmanaged copy only: no ECB/debug-view object,
                        // buffer header, Native container or pointer is retained.
                        row.Course = course;
                    }
                    addComponents++;
                    Increment(counts, component.FullName!);
                }
                else if (kind == "AddBuffer" && view == abi.Buffer)
                {
                    if (component != typeof(Game.Areas.Node) || !row.Types.Add(component)) throw Failure();
                    // BufferNode is deliberately never accessed. ComponentSize
                    // and record size do not give element count or heap bytes.
                    addBuffers++;
                    Increment(counts, component.FullName!);
                }
                else throw Failure();
            }

            int objects = 0, nets = 0, areas = 0;
            JObject? rootTransform = null;
            var netGeometry = new JArray();
            Target? objectRow = null, courseRow = null;
            foreach (Target row in targets.Values)
            {
                if (!row.Types.Contains(typeof(CreationDefinition)) || !row.Types.Contains(typeof(Updated))) throw Failure();
                bool isObject = row.Types.Contains(typeof(ObjectDefinition));
                bool isNet = row.Types.Contains(typeof(NetCourse));
                bool isArea = row.Types.Contains(typeof(Game.Areas.Node));
                if ((isObject ? 1 : 0) + (isNet ? 1 : 0) + (isArea ? 1 : 0) != 1) throw Failure();
                if (isObject)
                {
                    if (row.Prefab != expectedPrefab || row.Types.Contains(typeof(OwnerDefinition)) ||
                        row.Types.Contains(typeof(Game.Net.Upgraded))) throw Failure();
                    objects++;
                    objectRow = row;
                    rootTransform = new JObject { ["position"] = Vector(row.Object.m_Position),
                        ["rotation"] = new JArray(row.Object.m_Rotation.value.x, row.Object.m_Rotation.value.y,
                            row.Object.m_Rotation.value.z, row.Object.m_Rotation.value.w) };
                }
                else
                {
                    if (!row.Types.Contains(typeof(OwnerDefinition))) throw Failure();
                    if (isNet)
                    {
                        // The bound precedes geometry projection. Every observed
                        // course must fit; no prefix or silently truncated report.
                        if (nets >= rootSubNets) throw Failure();
                        nets++;
                        courseRow = row;
                        netGeometry.Add(CourseGeometry(row));
                    }
                    else areas++;
                }
            }
            if (objects != 1 || nets > rootSubNets || areas > rootSubAreas ||
                addBuffers != areas || addComponents > 3 + 5 * rootSubNets + 3 * rootSubAreas) throw Failure();
            // This narrower materialization branch does not weaken observation
            // coverage. Other complete diagnostics remain valid without a payload.
            if (targets.Count == 2 && nets == 1 && areas == 0 && rootSubNets == 1 && rootSubAreas == 0 &&
                commands.Length == 9 && objectRow != null && courseRow != null &&
                objectRow.Types.Count == 3 && courseRow.Types.Count == 4 &&
                Detached(objectRow.Creation) && Detached(courseRow.Creation) &&
                objectRow.Creation.m_SubPrefab == Entity.Null &&
                courseRow.Owner.m_Prefab == objectRow.Creation.m_Prefab &&
                courseRow.Owner.m_Position.Equals(objectRow.Object.m_Position) &&
                courseRow.Owner.m_Rotation.Equals(objectRow.Object.m_Rotation) &&
                courseRow.Course.m_StartPosition.m_Entity == Entity.Null && courseRow.Course.m_EndPosition.m_Entity == Entity.Null)
                payload = new ObjectDefinitionPayload(input.Source, objectRow.Creation, objectRow.Object,
                    courseRow.Creation, courseRow.Owner, courseRow.Course);
            var components = new JObject();
            foreach (var count in counts) components[count.Key] = count.Value;
            return new JObject {
                ["kind"] = "native-definition-command-observation", ["commandUpperBound"] = upperBound,
                ["commandsObserved"] = commands.Length, ["deferredTargets"] = targets.Count,
                ["addComponentCommands"] = addComponents, ["opaqueNodeBufferCommands"] = addBuffers,
                ["recordBytes"] = recordBytes, ["recordBytesAreHeapBytes"] = false,
                ["areaOutputNodeUpperBoundFromInput"] = areaOutputNodeUpperBound,
                ["bufferContentsObserved"] = false, ["objectDefinitions"] = objects,
                ["netCourses"] = nets, ["areaDefinitions"] = areas, ["rootTransform"] = rootTransform,
                ["netCourseGeometryComplete"] = netGeometry.Count == nets, ["netCourseGeometry"] = netGeometry,
                ["nativeOutputsObserved"] = false,
                ["componentCommands"] = components, ["playbackCalled"] = false,
                ["scope"] = "Fixed producer command diagnostics only; buffer geometry, Temp, complete attachments, qualification and fees are not observed."
            };
        }

        private static bool KnownComponent(Type type) => type == typeof(CreationDefinition) || type == typeof(ObjectDefinition) ||
            type == typeof(Updated) || type == typeof(OwnerDefinition) || type == typeof(NetCourse) || type == typeof(Game.Net.Upgraded);
        private static bool Detached(CreationDefinition value) => value.m_Flags == (CreationFlags)0 &&
            value.m_Original == Entity.Null && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null &&
            value.m_Prefab != Entity.Null && value.m_Prefab.Index >= 0 &&
            (value.m_SubPrefab == Entity.Null || value.m_SubPrefab.Index >= 0);
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray Pair(float2 value) => new JArray(value.x, value.y);
        private static JArray Rotation(quaternion value) => new JArray(value.value.x, value.value.y, value.value.z, value.value.w);
        private static JObject EntityValue(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static bool Finite(CoursePos value) => math.all(math.isfinite(value.m_Position))
            && math.all(math.isfinite(value.m_Rotation.value)) && math.all(math.isfinite(value.m_Elevation))
            && math.isfinite(value.m_CourseDelta) && math.isfinite(value.m_SplitPosition);
        private static JObject Position(CoursePos value) => new JObject {
            ["entity"] = EntityValue(value.m_Entity), ["position"] = Vector(value.m_Position),
            ["rotation"] = Rotation(value.m_Rotation), ["elevation"] = Pair(value.m_Elevation),
            ["courseDelta"] = value.m_CourseDelta, ["splitPosition"] = value.m_SplitPosition,
            ["flags"] = (uint)value.m_Flags, ["parentMesh"] = value.m_ParentMesh
        };
        private static JObject CourseGeometry(Target row)
        {
            NetCourse value = row.Course;
            // Full NetCourse plus raw identity/creation correlation. Equality
            // mirrors the native endpoint test; it is not a generated-node or
            // generated-edge classification. Buffer geometry remains opaque.
            return new JObject {
                ["target"] = EntityValue(row.DeferredEntity), ["prefab"] = EntityValue(row.Prefab),
                ["creationFlags"] = (uint)row.CreationFlags,
                ["curve"] = new JObject { ["a"] = Vector(value.m_Curve.a), ["b"] = Vector(value.m_Curve.b),
                    ["c"] = Vector(value.m_Curve.c), ["d"] = Vector(value.m_Curve.d) },
                ["elevation"] = Pair(value.m_Elevation), ["length"] = value.m_Length, ["fixedIndex"] = value.m_FixedIndex,
                ["start"] = Position(value.m_StartPosition), ["end"] = Position(value.m_EndPosition),
                ["endpointsCoincident"] = value.m_StartPosition.m_Position.Equals(value.m_EndPosition.m_Position)
            };
        }
        private static void Increment(Dictionary<string, int> values, string name)
        { values.TryGetValue(name, out int old); values[name] = old + 1; }
        private static InvalidOperationException Failure() => new InvalidOperationException("OBJECT_DEFINITION_COMMANDS_UNQUALIFIED");

        private sealed class Target
        {
            internal readonly HashSet<Type> Types = new HashSet<Type>();
            internal Entity DeferredEntity;
            internal Entity Prefab;
            internal CreationFlags CreationFlags;
            internal CreationDefinition Creation;
            internal OwnerDefinition Owner;
            internal ObjectDefinition Object;
            internal NetCourse Course;
        }

        internal sealed class Abi
        {
            internal readonly ConstructorInfo Constructor;
            internal readonly PropertyInfo Commands;
            internal readonly FieldInfo CommandType;
            internal readonly Type Create, Component, Buffer;
            internal Abi()
            {
                if (typeof(EntityCommandBuffer).Module.ModuleVersionId.ToString("N") != "3d6b23aeb28144a6a5f4cbd5fd3d75e6") throw Failure();
                Type basic = Nested("BasicCommandView"), proxy = Nested("EntityCommandBufferDebugView");
                Create = Nested("CreateCommandView"); Component = Nested("EntityComponentCommandView"); Buffer = Nested("EntityBufferCommandView");
                Constructor = proxy.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(EntityCommandBuffer) }, null)
                    ?? throw Failure();
                Commands = proxy.GetProperty("Commands", BindingFlags.Public | BindingFlags.Instance) ?? throw Failure();
                CommandType = basic.GetField("CommandType", BindingFlags.Public | BindingFlags.Instance) ?? throw Failure();
                if (Commands.PropertyType != basic.MakeArrayType() || Commands.GetGetMethod(false) == null ||
                    !CommandType.FieldType.IsEnum || CommandType.FieldType.Assembly != typeof(EntityCommandBuffer).Assembly) throw Failure();
            }
            private static Type Nested(string name) => typeof(EntityCommandBuffer).GetNestedType(name, BindingFlags.NonPublic) ?? throw Failure();
            internal int Int(object value, string name) => (int)Value(value, name, typeof(int));
            internal object Value(object value, string name, Type expected)
            {
                FieldInfo field = value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw Failure();
                if (field.FieldType != expected || field.Module != typeof(EntityCommandBuffer).Module) throw Failure();
                return field.GetValue(value)!;
            }
        }
    }
}
#endif
