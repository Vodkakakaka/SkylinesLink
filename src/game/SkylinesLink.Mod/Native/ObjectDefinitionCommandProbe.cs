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
                !ReferenceEquals(producer.World, input.Source) || producer.CapturedPrefab != input.Root || producer.CapturedOriginal != Entity.Null ||
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
            CommandObservation observation = ReadCommands(producer, upperBound, entityBound);
            var targets = observation.Targets;
            int objects = 0, nets = 0, areas = 0;
            JObject? rootTransform = null;
            var netGeometry = new JArray();
            Target? objectRow = null, courseRow = null;
            foreach (Target row in targets.Values)
            {
                if (row.Prefab == Entity.Null || row.Prefab.Index < 0 || row.Creation.m_Original != Entity.Null
                    || row.Creation.m_Owner != Entity.Null) throw Failure();
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
                        ["rotation"] = Rotation(row.Object.m_Rotation) };
                }
                else
                {
                    if (!row.Types.Contains(typeof(OwnerDefinition))) throw Failure();
                    if (isNet)
                    {
                        if (nets >= rootSubNets) throw Failure();
                        nets++;
                        courseRow = row;
                        netGeometry.Add(CourseGeometry(row));
                    }
                    else areas++;
                }
            }
            if (objects != 1 || nets > rootSubNets || areas > rootSubAreas || observation.AddBuffers != areas
                || observation.AddComponents > 3 + 5 * rootSubNets + 3 * rootSubAreas) throw Failure();
            // This narrower materialization branch does not weaken observation
            // coverage. Other complete diagnostics remain valid without a payload.
            if (targets.Count == 2 && nets == 1 && areas == 0 && rootSubNets == 1 && rootSubAreas == 0 &&
                observation.CommandCount == 9 && objectRow != null && courseRow != null &&
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
            foreach (var count in observation.ComponentCounts) components[count.Key] = count.Value;
            return new JObject {
                ["kind"] = "native-definition-command-observation", ["commandUpperBound"] = upperBound,
                ["commandsObserved"] = observation.CommandCount, ["deferredTargets"] = targets.Count,
                ["addComponentCommands"] = observation.AddComponents, ["opaqueNodeBufferCommands"] = observation.AddBuffers,
                ["recordBytes"] = observation.RecordBytes, ["recordBytesAreHeapBytes"] = false,
                ["areaOutputNodeUpperBoundFromInput"] = areaOutputNodeUpperBound,
                ["bufferContentsObserved"] = false, ["objectDefinitions"] = objects,
                ["netCourses"] = nets, ["areaDefinitions"] = areas, ["rootTransform"] = rootTransform,
                ["netCourseGeometryComplete"] = netGeometry.Count == nets, ["netCourseGeometry"] = netGeometry,
                ["nativeOutputsObserved"] = false,
                ["componentCommands"] = components, ["playbackCalled"] = false,
                ["scope"] = "Fixed producer command diagnostics only; buffer geometry, Temp, complete attachments, qualification and fees are not observed."
            };
        }

        internal static JObject CaptureRelocation(ObjectDefinitionProducer producer, RelocationDefinitionSource original, ControlPoint destination,
            out RelocationDefinitionPayload? payload)
        {
            payload = null;
            RelocationDefinitionSource.Require(producer.CapturedPrefab == original.Prefab && producer.CapturedOriginal == original.Root,
                "RELOCATION_DEFINITION_INPUT_CHANGED");
            CommandObservation observation = ReadCommands(producer, original.CommandCount, 3);
            RelocationDefinitionSource.Require(observation.CommandCount == original.CommandCount && observation.Targets.Count == 3
                && observation.AddBuffers == 0, "RELOCATION_DEFINITION_COMMAND_COUNT_CHANGED");

            Target? root = null, previousNode = null, nextNode = null;
            foreach (Target row in observation.Targets.Values)
            {
                Entity prior = row.Creation.m_Original;
                if (prior == original.Root && root == null) root = row;
                else if (prior == original.NodeEntity && previousNode == null) previousNode = row;
                else if (prior == Entity.Null && nextNode == null) nextNode = row;
                else throw new RelocationDefinitionSource.Rejection("RELOCATION_DEFINITION_UNEXPECTED_TARGET");
            }

            RelocationDefinitionSource.Require(root != null && root.Types.Count == 3 && root.Types.Contains(typeof(ObjectDefinition))
                && Rootless(root.Creation) && root.Prefab == original.Prefab && root.Creation.m_Flags == CreationFlags.Relocate
                && root.Object.m_Position.Equals(destination.m_Position) && root.Object.m_Rotation.Equals(destination.m_Rotation)
                && root.Object.m_LocalPosition.Equals(root.Object.m_Position) && root.Object.m_LocalRotation.Equals(root.Object.m_Rotation)
                && root.Object.m_Elevation == destination.m_Elevation && root.Object.m_ParentMesh == -1,
                "RELOCATION_DEFINITION_BUILDING_UNQUALIFIED");
            RelocationDefinitionSource.Require(previousNode != null && previousNode.Types.Count == 3 && previousNode.Types.Contains(typeof(NetCourse))
                && Rootless(previousNode.Creation) && previousNode.Prefab == Entity.Null && previousNode.Creation.m_RandomSeed == 0
                && previousNode.Creation.m_Flags == (CreationFlags.Delete | CreationFlags.Hidden) && MatchesRetiringNode(previousNode.Course, original),
                "RELOCATION_DEFINITION_RETIRING_NODE_UNQUALIFIED");
            RelocationDefinitionSource.Require(nextNode != null && nextNode.Types.Count == original.CommandCount - 9
                && nextNode.Types.Contains(typeof(NetCourse)) && nextNode.Types.Contains(typeof(OwnerDefinition))
                && nextNode.Types.Contains(typeof(Game.Net.Upgraded)) == (original.CommandCount == 14)
                && nextNode.Upgraded.m_Flags.Equals(original.Upgrades)
                && Detached(nextNode.Creation) && Rootless(nextNode.Creation) && nextNode.Prefab == original.NodePrefab
                && nextNode.Owner.m_Prefab == original.Prefab && nextNode.Owner.m_Position.Equals(destination.m_Position)
                && nextNode.Owner.m_Rotation.Equals(destination.m_Rotation) && MatchesNewNode(nextNode.Course, original, destination),
                "RELOCATION_DEFINITION_NEW_NODE_UNQUALIFIED");

            payload = new RelocationDefinitionPayload(producer.World, original, root!.Creation, root.Object,
                previousNode!.Creation, previousNode.Course, nextNode!.Creation, nextNode.Owner, nextNode.Course,
                original.CommandCount == 14 ? nextNode.Upgraded : (Game.Net.Upgraded?)null);
            return new JObject {
                ["commands"] = observation.CommandCount, ["targets"] = observation.Targets.Count,
                ["root"] = new JArray(original.Root.Index, original.Root.Version),
                ["oldNode"] = new JArray(original.NodeEntity.Index, original.NodeEntity.Version),
                ["from"] = Vector(original.RootTransform.m_Position), ["to"] = Vector(root.Object.m_Position),
                ["oldNodePosition"] = Vector(previousNode.Course.m_StartPosition.m_Position),
                ["newNodePosition"] = Vector(nextNode.Course.m_StartPosition.m_Position),
                ["newNodeCurveStart"] = Vector(nextNode.Course.m_Curve.a),
                ["playbackCalled"] = false
            };
        }

        private static bool Rootless(CreationDefinition value) => value.m_Owner == Entity.Null
            && value.m_Attached == Entity.Null && value.m_SubPrefab == Entity.Null;

        private static bool MatchesRetiringNode(NetCourse course, RelocationDefinitionSource source)
        {
            float3 position = source.NodePose.m_Position;
            return course.m_Length == 0f && course.m_FixedIndex == -1 && course.m_Elevation.Equals(default(float2))
                && course.m_Curve.a.Equals(position) && course.m_Curve.b.Equals(position)
                && course.m_Curve.c.Equals(position) && course.m_Curve.d.Equals(position)
                && MatchesEndpoint(course.m_StartPosition, 0f) && MatchesEndpoint(course.m_EndPosition, 1f);

            bool MatchesEndpoint(CoursePos endpoint, float delta) => endpoint.m_Entity == source.NodeEntity
                && endpoint.m_Position.Equals(position) && endpoint.m_Rotation.Equals(source.NodePose.m_Rotation)
                && endpoint.m_CourseDelta == delta && endpoint.m_ParentMesh == -1 && endpoint.m_Flags == default
                && endpoint.m_Elevation.Equals(default(float2)) && endpoint.m_SplitPosition == 0f;
        }

        private static bool MatchesNewNode(NetCourse course, RelocationDefinitionSource source, ControlPoint destination)
        {
            var expected = source.ExpectedNodeAt(destination);
            var start = course.m_StartPosition;
            var end = course.m_EndPosition;
            return course.m_Length <= 0.002f && course.m_FixedIndex == -1 && start.m_Position.Equals(end.m_Position)
                && math.distancesq(start.m_Position, expected) <= 0.002f * 0.002f
                && math.distancesq(course.m_Curve.a.xz, expected.xz) <= 0.002f * 0.002f
                && course.m_Curve.a.Equals(course.m_Curve.b) && course.m_Curve.a.Equals(course.m_Curve.c)
                && course.m_Curve.a.Equals(course.m_Curve.d) && MatchesEndpoint(start, 0f) && MatchesEndpoint(end, 1f);

            bool MatchesEndpoint(CoursePos endpoint, float delta) => endpoint.m_Entity == Entity.Null
                && endpoint.m_CourseDelta == delta && endpoint.m_ParentMesh == (source.HasNodeElevation ? 0 : -1)
                && endpoint.m_Flags == (CoursePosFlags.IsFirst | CoursePosFlags.IsLast) && endpoint.m_SplitPosition == 0f;
        }

        private static CommandObservation ReadCommands(ObjectDefinitionProducer producer, int upperBound, int entityBound)
        {
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
                    else if (component == typeof(Game.Net.Upgraded)) row.Upgraded = (Game.Net.Upgraded)value!;
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

            foreach (Target row in targets.Values)
            {
                if (!row.Types.Contains(typeof(CreationDefinition)) || !row.Types.Contains(typeof(Updated))) throw Failure();
            }
            return new CommandObservation(targets, counts, commands.Length, addComponents, addBuffers, recordBytes);
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
            internal Game.Net.Upgraded Upgraded;
        }

        private sealed class CommandObservation
        {
            internal readonly Dictionary<int, Target> Targets;
            internal readonly Dictionary<string, int> ComponentCounts;
            internal readonly int CommandCount, AddComponents, AddBuffers;
            internal readonly long RecordBytes;

            internal CommandObservation(Dictionary<int, Target> targets, Dictionary<string, int> componentCounts,
                int commandCount, int addComponents, int addBuffers, long recordBytes)
            {
                Targets = targets;
                ComponentCounts = componentCounts;
                CommandCount = commandCount;
                AddComponents = addComponents;
                AddBuffers = addBuffers;
                RecordBytes = recordBytes;
            }
        }

        internal sealed class Abi
        {
            internal readonly ConstructorInfo Constructor;
            internal readonly PropertyInfo Commands;
            internal readonly FieldInfo CommandType;
            internal readonly Type Create, Component, Buffer;
            internal Abi()
            {
                if (!NativeConstructionBuild.Entities(typeof(EntityCommandBuffer).Module)) throw Failure();
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
