#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Game.Vehicles;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Disabled = Unity.Entities.Disabled;
using LiveObject = Game.Objects.Object;
using NativeAnimation = Game.Tools.Animation;
using StaticObject = Game.Objects.Static;

namespace SkylinesLink.Mod.Native
{
    internal sealed class PreviewCommitObjects
    {
        private NativeSystemRegistration<PreviewCommitObjectProducer>? registration;
        private bool attempted, registrationReleased = true, protectionConfirmed;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => registration?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => registrationReleased && !NativeWorkMayReadSource;
        internal bool Passed => registration?.Instance.OutputConfirmed == true && protectionConfirmed && ResourcesReleased && failureCode == null;
        internal JobHandle CompletedDependency => registration?.Instance.CompletedDependency ?? default;

        internal void RequirePreparedInputs(World source, Entity[] graph)
        {
            Require(Passed, "PREVIEW_COMMIT_OBJECT_PREPARATION_UNCONFIRMED");
            registration!.Instance.RequirePreparedInputs(source, graph);
        }

        internal void RunOnce(World source, Entity[] graph, PreviewCommitBarrier output, PreviewCommitTriggerSink triggers,
            List<ComponentSystemBase> appended, Action requireScope, Action guardLiveGraph, Action observeProtection,
            int actor = 1, Temp[]? relocation = null)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted, "PREVIEW_COMMIT_OBJECT_EXECUTION_REJECTED");
            attempted = true;
            try
            {
                requireScope();
                guardLiveGraph();
                var producer = new PreviewCommitObjectProducer();
                producer.Configure(output, triggers, actor, relocation);
                registration = new NativeSystemRegistration<PreviewCommitObjectProducer>(source, producer);
                registrationReleased = false;
                registration.Add();
                appended.Add(producer);
                requireScope();
                observeProtection();

                producer.Prepare(graph, requireScope, guardLiveGraph);
                producer.InspectOutput();
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_COMMIT_OBJECT_FAILED";
                throw new Rejection(failureCode, error);
            }
            finally
            {
                if (registration != null && !NativeWorkMayReadSource) Release(registration, appended);
            }

            if (!ResourcesReleased) failureCode ??= "PREVIEW_COMMIT_OBJECT_RELEASE_UNCONFIRMED";
            if (failureCode != null) throw new Rejection(failureCode);
            try
            {
                requireScope();
                guardLiveGraph();
                observeProtection();
                protectionConfirmed = true;
            }
            catch (Exception error)
            {
                failureCode = "PREVIEW_COMMIT_OBJECT_PROTECTION_UNCONFIRMED";
                throw new Rejection(failureCode, error);
            }
        }

        internal void RequireRelocationApplied(EntityManager entities)
        {
            Require(Passed, "PREVIEW_COMMIT_OBJECT_PREPARATION_UNCONFIRMED");
            registration!.Instance.RequireRelocationApplied(entities);
        }

        internal void RequireRelocationTrigger(TriggerAction action)
        {
            Require(Passed, "PREVIEW_COMMIT_OBJECT_PREPARATION_UNCONFIRMED");
            registration!.Instance.RequireRelocationTrigger(action);
        }

        private void Release(NativeSystemRegistration<PreviewCommitObjectProducer> owned, List<ComponentSystemBase> appended)
        {
            try
            {
                owned.Destroy();
                registrationReleased = owned.PhysicalReleased && (!owned.DestroyAttempted
                    || owned.DestroyReturned && owned.Instance.DestructionReturned);
                if (registrationReleased) appended.Remove(owned.Instance);
            }
            catch (Exception) { failureCode ??= "PREVIEW_COMMIT_OBJECT_RELEASE_FAILED"; }
        }

        internal JObject Snapshot() => new JObject
        {
            ["passed"] = Passed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["registrationReleased"] = registrationReleased, ["producer"] = registration?.Instance.Snapshot(),
            ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot()
        {
            var result = Snapshot();
            result["producer"] = registration?.Instance.RelocationSnapshot();
            return result;
        }

        internal static void Require(bool condition, string code)
        {
            if (!condition) throw new Rejection(code);
        }

        internal sealed class Rejection : InvalidOperationException
        {
            internal string Code { get; }
            internal Rejection(string code, Exception? inner = null) : base(code, inner) { Code = code; }
        }
    }

    [DisableAutoCreation]
    public sealed partial class PreviewCommitObjectProducer : ApplyObjectsSystem
    {
        private const int ObjectCount = 6;
        private int actor;
        private int TargetCount => relocationTemps == null ? ObjectCount : 7;
        private static readonly string[] FieldNames = { "m_ToolOutputBarrier", "m_ToolSystem", "m_SimulationSystem",
            "m_TriggerSystem", "m_InstanceCountSystem", "m_TempQuery", "m_EconomyParameterQuery", "m_PathTargetEventArchetype",
            "m_AppliedTypes", "m_TempAnimationTypes" };

        private PreviewCommitBarrier? output;
        private PreviewCommitTriggerSink? triggers;
        private ToolSystem? tools;
        private SimulationSystem? simulation;
        private InstanceCountSystem? instanceCounts;
        private EntityQuery objects, economy;
        private ComponentTypeSet animationTypes, appliedTypes;
        private EntityArchetype pathTarget;
        private FieldInfo[]? fields;
        private object[]? values;
        private Entity[]? cohort;
        private ObjectInput[]? expected;
        private Action? requireScope, guardLiveGraph;
        private bool creationReturned, nativeEntered, nativeReturned, nativeJoined;
        private uint? frame;
        private int? count, chunkCount, commandsObserved, expectedCommands, methodsChecked;

        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal bool OutputConfirmed { get; private set; }
        internal JobHandle CompletedDependency { get; private set; }

        internal void Configure(PreviewCommitBarrier barrier, PreviewCommitTriggerSink sink, int actor, Temp[]? relocation)
        {
            Require(output == null && World == null && actor > 0, "PREVIEW_COMMIT_OBJECT_CONFIGURATION_REJECTED");
            this.actor = actor;
            relocationTemps = relocation;
            output = barrier ?? throw new ArgumentNullException(nameof(barrier));
            triggers = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        protected override void OnCreate()
        {
            Enabled = false;
            Require(MainThreadDispatcher.isMainThread && output != null && triggers != null
                && ReferenceEquals(output.World, World) && ReferenceEquals(triggers.World, World),
                "PREVIEW_COMMIT_OBJECT_CONFIGURATION_MISSING");
            tools = World.GetExistingSystemManaged<ToolSystem>();
            simulation = World.GetExistingSystemManaged<SimulationSystem>();
            instanceCounts = World.GetExistingSystemManaged<InstanceCountSystem>();
            Require(tools != null && simulation != null && instanceCounts != null, "PREVIEW_COMMIT_OBJECT_PROVIDERS_MISSING");

            objects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<LiveObject>(),
                    ComponentType.ReadOnly<Disabled>(), ComponentType.ReadOnly<ObjectGenerationScope>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            objects.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            economy = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            RequireForUpdate(objects);
            RequireForUpdate(economy);
            pathTarget = EntityManager.CreateArchetype(ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<PathTargetMoved>());
            appliedTypes = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Created>(),
                ComponentType.ReadWrite<Updated>());
            animationTypes = new ComponentTypeSet(ComponentType.ReadWrite<Temp>(), ComponentType.ReadWrite<NativeAnimation>(),
                ComponentType.ReadWrite<BackSide>());
            values = new object[] { output!, tools!, simulation!, triggers!, instanceCounts!, objects, economy, pathTarget, appliedTypes, animationTypes };
            var types = new[] { typeof(ToolOutputBarrier), typeof(ToolSystem), typeof(SimulationSystem), typeof(TriggerSystem),
                typeof(InstanceCountSystem), typeof(EntityQuery), typeof(EntityQuery), typeof(EntityArchetype),
                typeof(ComponentTypeSet), typeof(ComponentTypeSet) };
            fields = new FieldInfo[FieldNames.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var field = typeof(ApplyObjectsSystem).GetField(FieldNames[i],
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == types[i] && !field.IsInitOnly, "PREVIEW_COMMIT_OBJECT_FIELD_ABI");
                fields[i] = field!;
                field!.SetValue(this, values[i]);
            }
            creationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        internal void Prepare(Entity[] graph, Action scope, Action liveGraph)
        {
            Require(creationReturned && cohort == null && (relocationTemps == null
                ? graph.Length == ObjectCount + 1 : graph.Length == 9 && relocationTemps.Length == graph.Length),
                "PREVIEW_COMMIT_OBJECT_COHORT_REJECTED");
            cohort = (Entity[])graph.Clone();
            requireScope = scope;
            guardLiveGraph = liveGraph;
            CheckGraph();
            Require(ShouldRunSystem(), "PREVIEW_COMMIT_OBJECT_REQUIRED_QUERY");
            Enabled = true;
            try
            {
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_COMMIT_OBJECT_NATIVE_NOT_RETURNED");
                JobHandle original = Dependency;
                Require(!original.Equals(default(JobHandle)), "PREVIEW_COMMIT_OBJECT_NATIVE_DEPENDENCY_MISSING");
                var completion = original;
                completion.Complete();
                nativeJoined = true;
                CompletedDependency = original;
            }
            finally { Enabled = false; }
        }

        protected override void OnUpdate()
        {
            Require(!nativeEntered && cohort != null, "PREVIEW_COMMIT_OBJECT_NATIVE_NOT_ARMED");
            CheckGraph();
            var patch = PreviewCommitObjectPatchProbe.Observe(relocationTemps != null);
            methodsChecked = patch.MethodsChecked;
            Require(patch.Qualified, "PREVIEW_COMMIT_OBJECT_PATCH_UNQUALIFIED");
            RequireBindings();
            count = objects.CalculateEntityCount();
            chunkCount = objects.CalculateChunkCount();
            Require(count == TargetCount && chunkCount > 0 && chunkCount <= TargetCount && economy.CalculateEntityCount() == 1
                && output!.PendingCount == PreviewCommitBarrier.ObjectBufferIndex, "PREVIEW_COMMIT_OBJECT_QUERY_OR_BARRIER_BOUND");
            using (var entities = objects.ToEntityArray(Allocator.Temp))
            {
                Require(entities.Length == TargetCount, "PREVIEW_COMMIT_OBJECT_QUERY_SET");
                int seen = 0;
                foreach (Entity entity in entities)
                {
                    int index = Array.IndexOf(cohort, entity);
                    Require(index >= 0 && index != 1 && (relocationTemps == null || index != 2) && (seen & (1 << index)) == 0, "PREVIEW_COMMIT_OBJECT_QUERY_SET");
                    seen |= 1 << index;
                }
            }

            frame = simulation!.frameIndex;
            if (relocationTemps == null)
            {
                expected = ReadInputs(EntityManager, cohort!);
                expectedCommands = 0;
                foreach (ObjectInput input in expected) expectedCommands += input.CommandCount;
            }
            else
            {
                relocationEconomy = economy.GetSingletonEntity();
                relocationPlan = ReadRelocationPlan(EntityManager, cohort!, frame.Value);
                expectedCommands = relocationPlan.CommandCount;
            }
            CheckGraph();
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        internal void InspectOutput()
        {
            CheckGraph();
            Require(nativeJoined && !OutputConfirmed && (expected != null || relocationPlan != null) && expectedCommands.HasValue && frame.HasValue
                && output!.PendingCount == PreviewCommitBarrier.ObjectBufferIndex + 1 && simulation!.frameIndex == frame.Value,
                "PREVIEW_COMMIT_OBJECT_OUTPUT_UNAVAILABLE");
            if (relocationPlan != null)
            {
                InspectRelocationOutput();
                CheckGraph();
                OutputConfirmed = true;
                return;
            }
            var current = ReadMatchingInputs(EntityManager, cohort!);

            var abi = new ObjectDefinitionCommandProbe.Abi();
            var typeSetView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic);
            Require(typeSetView != null, "PREVIEW_COMMIT_OBJECT_COMMAND_ABI");
            var buffer = output!.Borrow(PreviewCommitBarrier.ObjectBufferIndex);
            Require(buffer.IsCreated, "PREVIEW_COMMIT_OBJECT_BUFFER_UNAVAILABLE");
            object view = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(view)!;
            Require(commands.Length == expectedCommands!.Value, "PREVIEW_COMMIT_OBJECT_COMMAND_COUNT");
            var stages = new int[ObjectCount];
            var sortKeys = new int[ObjectCount];
            foreach (object command in commands)
            {
                Entity entity = (Entity)abi.Value(command, "Entity", typeof(Entity));
                int graphIndex = Array.IndexOf(cohort!, entity);
                int sort = abi.Int(command, "SortKey");
                int bytes = abi.Int(command, "TotalSizeInBytes");
                Require(graphIndex >= 0 && graphIndex != 1 && sort >= 0
                    && bytes > 0 && bytes <= 2048 && abi.Int(command, "BatchCount") == 1 && abi.Int(command, "IdentityIndex") == 0,
                    "PREVIEW_COMMIT_OBJECT_COMMAND_TARGET");
                int index = graphIndex == 0 ? 0 : graphIndex - 1;
                var input = current[index];
                int stage = stages[index]++;
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                if (stage == 0)
                {
                    sortKeys[index] = sort;
                    RequireTypeSet(command, kind, "RemoveMultipleComponents", typeSetView!, animationTypes, abi);
                    continue;
                }

                Require(sortKeys[index] == sort, "PREVIEW_COMMIT_OBJECT_COMMAND_ORDER");
                int markerStage = input.CommandCount - 1;
                Require(stage <= markerStage, "PREVIEW_COMMIT_OBJECT_COMMAND_ORDER");
                if (stage == markerStage)
                {
                    RequireTypeSet(command, kind, "AddMultipleComponents", typeSetView!, appliedTypes, abi);
                    continue;
                }

                if (input.RemoveInterpolated && stage == 1)
                {
                    RequireComponent<InterpolatedTransform>(command, kind, "RemoveComponent", abi, hasValue: false);
                    continue;
                }

                Require(input.Temp.m_Cost > 0, "PREVIEW_COMMIT_OBJECT_UNEXPECTED_RECENT");
                RequireComponent<Recent>(command, kind, "AddComponent", abi, hasValue: true);
                var recent = (Recent)abi.Value(command, "ComponentValue", typeof(object));
                Require(recent.m_ModificationCost == input.Temp.m_Cost && recent.m_ModificationFrame == frame!.Value,
                    "PREVIEW_COMMIT_OBJECT_RECENT_VALUE");
            }

            for (int i = 0; i < current.Length; i++)
                Require(stages[i] == current[i].CommandCount, "PREVIEW_COMMIT_OBJECT_COMMAND_SET_INCOMPLETE");
            CheckGraph();
            commandsObserved = commands.Length;
            OutputConfirmed = true;
        }

        internal void RequirePreparedInputs(World source, Entity[] graph)
        {
            Require(OutputConfirmed && DestructionReturned && ReferenceEquals(source.GetExistingSystemManaged<SimulationSystem>(), simulation)
                && simulation!.frameIndex == frame && ReferenceEquals(source.GetExistingSystemManaged<ToolSystem>(), tools)
                && tools!.actionMode == GameMode.Game, "PREVIEW_COMMIT_OBJECT_PREPARATION_CHANGED");
            if (relocationPlan == null) ReadMatchingInputs(source.EntityManager, graph);
            else
            {
                Require(ReferenceEquals(source.GetExistingSystemManaged<InstanceCountSystem>(), instanceCounts),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_INSTANCE_COUNTS_CHANGED");
                RequireRelocationInputs(source.EntityManager, graph);
            }
        }

        private ObjectInput[] ReadMatchingInputs(EntityManager manager, Entity[] graph)
        {
            var current = ReadInputs(manager, graph);
            for (int i = 0; i < current.Length; i++)
                Require(current[i].Matches(expected![i]), "PREVIEW_COMMIT_OBJECT_SOURCE_CHANGED");
            return current;
        }

        private ObjectInput[] ReadInputs(EntityManager manager, Entity[] graph)
        {
            var result = new ObjectInput[ObjectCount];
            for (int i = 0; i < graph.Length; i++)
            {
                if (i == 1) continue;
                Entity entity = graph[i];
                Require(entity != Entity.Null && Array.IndexOf(graph, entity) == i && manager.Exists(entity)
                    && manager.HasComponent<Temp>(entity) && manager.HasComponent<LiveObject>(entity)
                    && manager.HasComponent<PrefabRef>(entity) && manager.HasComponent<Transform>(entity)
                    && manager.HasComponent<Disabled>(entity) && manager.HasComponent<ObjectGenerationScope>(entity)
                    && manager.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor
                    && !manager.HasComponent<Deleted>(entity) && !manager.HasComponent<Error>(entity)
                    && !manager.HasComponent<Warning>(entity) && !manager.HasComponent<Override>(entity)
                    && !manager.HasComponent<Recent>(entity) && !manager.HasComponent<Attached>(entity)
                    && !manager.HasComponent<ParkedCar>(entity) && !manager.HasComponent<ParkedTrain>(entity)
                    && !manager.HasComponent<Vehicle>(entity) && !manager.HasComponent<Game.Creatures.Creature>(entity),
                    "PREVIEW_COMMIT_OBJECT_ROLE_UNQUALIFIED");

                Temp temp = manager.GetComponentData<Temp>(entity);
                TempFlags flags = i == 0 ? TempFlags.Create | TempFlags.Essential : TempFlags.Create;
                Require(temp.m_Original == Entity.Null && temp.m_Flags == flags && (i == 0 ? temp.m_Cost > 0 : temp.m_Cost == 0),
                    "PREVIEW_COMMIT_OBJECT_TEMP_UNQUALIFIED");
                Entity prefab = manager.GetComponentData<PrefabRef>(entity).m_Prefab;
                Require(prefab != Entity.Null && manager.Exists(prefab) && manager.HasComponent<PrefabData>(prefab)
                    && manager.IsComponentEnabled<PrefabData>(prefab), "PREVIEW_COMMIT_OBJECT_PREFAB_UNAVAILABLE");
                bool hasOwner = manager.HasComponent<Owner>(entity);
                Entity owner = hasOwner ? manager.GetComponentData<Owner>(entity).m_Owner : Entity.Null;
                Require(i == 0 ? !hasOwner && manager.HasComponent<Building>(entity)
                    : hasOwner && owner == (i == graph.Length - 1 ? graph[1] : graph[0])
                        && !manager.HasComponent<Building>(entity) && !manager.HasComponent<Game.Buildings.ServiceUpgrade>(entity),
                    "PREVIEW_COMMIT_OBJECT_OWNER_UNQUALIFIED");

                bool removeInterpolated = manager.HasComponent<StaticObject>(entity) && !manager.HasComponent<Swaying>(entity)
                    && !manager.HasBuffer<TransformFrame>(entity) || manager.HasComponent<Stopped>(entity);
                result[i == 0 ? 0 : i - 1] = new ObjectInput(entity, prefab, owner, temp,
                    manager.GetComponentData<Transform>(entity), removeInterpolated);
            }
            return result;
        }

        private void RequireBindings()
        {
            Require(fields != null && values != null && EntityQueries.Length == 2 && objects.HasFilter() && !economy.HasFilter()
                && pathTarget.Valid && tools!.actionMode == GameMode.Game && triggers!.Enabled
                && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && ReferenceEquals(World.GetExistingSystemManaged<SimulationSystem>(), simulation)
                && ReferenceEquals(World.GetExistingSystemManaged<InstanceCountSystem>(), instanceCounts)
                && ReferenceEquals(output!.World, World) && ReferenceEquals(triggers.World, World),
                "PREVIEW_COMMIT_OBJECT_BINDINGS_CHANGED");
            for (int i = 0; i < fields!.Length; i++)
                Require(Equals(fields[i].GetValue(this), values![i]), "PREVIEW_COMMIT_OBJECT_BINDINGS_CHANGED");
        }

        private void CheckGraph()
        {
            requireScope!();
            Require(creationReturned && World != null && World.IsCreated && !DestructionReturned,
                "PREVIEW_COMMIT_OBJECT_SOURCE_UNAVAILABLE");
            guardLiveGraph!();
            requireScope();
        }

        protected override void OnDestroy()
        {
            Require(!NativeWorkMayReadSource, "PREVIEW_COMMIT_OBJECT_NATIVE_WORK_UNRESOLVED");
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject
        {
            ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
            ["targetCount"] = count, ["chunkCount"] = chunkCount, ["appliedFrame"] = frame,
            ["methodsChecked"] = methodsChecked, ["commandsObserved"] = commandsObserved, ["outputConfirmed"] = OutputConfirmed,
            ["destructionReturned"] = DestructionReturned
        };

        private static void RequireTypeSet(object command, string kind, string expectedKind, Type view,
            ComponentTypeSet expected, ObjectDefinitionCommandProbe.Abi abi)
        {
            Require(command.GetType() == view && kind == expectedKind, "PREVIEW_COMMIT_OBJECT_COMMAND_TYPE_SET");
            var actual = (ComponentTypeSet)abi.Value(command, "TypeSet", typeof(ComponentTypeSet));
            Require(actual.Length == expected.Length, "PREVIEW_COMMIT_OBJECT_COMMAND_TYPE_SET");
            for (int i = 0; i < expected.Length; i++)
                Require(actual.GetTypeIndex(i) == expected.GetTypeIndex(i), "PREVIEW_COMMIT_OBJECT_COMMAND_TYPE_SET");
        }

        private static void RequireComponent<T>(object command, string kind, string expectedKind,
            ObjectDefinitionCommandProbe.Abi abi, bool hasValue) where T : unmanaged, IComponentData
        {
            TypeIndex index = TypeManager.GetTypeIndex<T>();
            int size = hasValue ? TypeManager.GetTypeInfo(index).TypeSize : 0;
            Require(command.GetType() == abi.Component && kind == expectedKind
                && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == index
                && (short)abi.Value(command, "ComponentSize", typeof(short)) == size
                && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)),
                "PREVIEW_COMMIT_OBJECT_COMMAND_COMPONENT");
            object? value = abi.Value(command, "ComponentValue", typeof(object));
            Require(hasValue ? value is T : value == null, "PREVIEW_COMMIT_OBJECT_COMMAND_VALUE");
        }

        private static void Require(bool condition, string code) => PreviewCommitObjects.Require(condition, code);

        private readonly struct ObjectInput
        {
            internal readonly Entity Entity, Prefab, Owner;
            internal readonly Temp Temp;
            internal readonly Transform Transform;
            internal readonly bool RemoveInterpolated;
            internal int CommandCount => 2 + (RemoveInterpolated ? 1 : 0) + (Temp.m_Cost > 0 ? 1 : 0);

            internal ObjectInput(Entity entity, Entity prefab, Entity owner, Temp temp, Transform transform, bool removeInterpolated)
            { Entity = entity; Prefab = prefab; Owner = owner; Temp = temp; Transform = transform; RemoveInterpolated = removeInterpolated; }

            internal bool Matches(ObjectInput other) => Entity == other.Entity && Prefab == other.Prefab && Owner == other.Owner
                && Temp.Equals(other.Temp) && Transform.Equals(other.Transform) && RemoveInterpolated == other.RemoveInterpolated;
        }
    }

    internal static class PreviewCommitObjectPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe(bool relocation = false)
        {
            var failure = new DefinitionProducerPatchProbe { Reason = "PREVIEW_COMMIT_OBJECT_METADATA_UNQUALIFIED" };
            try
            {
                Type owner = typeof(ApplyObjectsSystem);
                if (owner.Module.ModuleVersionId.ToString("N") != "e15551e93dea48558dde1da3cea7d7ef"
                    || typeof(EntityCommandBuffer).Module.ModuleVersionId.ToString("N") != "3d6b23aeb28144a6a5f4cbd5fd3d75e6") return failure;
                var methods = new List<MethodBase>();
                AddDeclared(methods, owner);
                foreach (string name in new[] { "PatchTempReferencesJob", "HandleTempEntitiesJob", "TypeHandle" })
                {
                    Type? nested = owner.GetNestedType(name, BindingFlags.NonPublic);
                    if (nested == null || !nested.IsValueType) return failure;
                    AddDeclared(methods, nested);
                }
                methods.AddRange(typeof(TriggerAction).GetConstructors(Declared));
                bool found = Add(methods, typeof(TriggerSystem), "CreateActionBuffer", typeof(NativeQueue<TriggerAction>))
                    && Add(methods, typeof(TriggerSystem), "AddActionBufferWriter", typeof(void), typeof(JobHandle))
                    && Add(methods, typeof(InstanceCountSystem), "GetInstanceCounts", typeof(NativeParallelHashMap<Entity, int>),
                        typeof(bool), typeof(JobHandle).MakeByRefType())
                    && Add(methods, typeof(InstanceCountSystem), "AddCountReader", typeof(void), typeof(JobHandle))
                    && Add(methods, typeof(SimulationSystem), "get_frameIndex", typeof(uint))
                    && Add(methods, typeof(ToolSystem), "get_actionMode", typeof(GameMode))
                    && Add(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer));
                if (relocation) found &= ObserveRelocationMethods(owner, methods);
                var result = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_COMMIT_OBJECT");
                return result.Qualified && !ObserveEcs(result.HarmonyMvid, relocation) ? failure : result;
            }
            catch (Exception) { return failure; }
        }

        private static bool ObserveRelocationMethods(Type owner, List<MethodBase> methods)
        {
            var job = owner.GetNestedType("HandleTempEntitiesJob", BindingFlags.NonPublic)!;
            var updateComponent = job.GetMethod("UpdateComponent", Declared);
            var updateBuffer = job.GetMethod("UpdateBuffer", Declared);
            var moved = typeof(PathTargetMoved).GetConstructor(Declared, null,
                new[] { typeof(Entity), typeof(float3), typeof(float3) }, null);
            if (updateComponent == null || updateBuffer == null || moved == null) return false;
            foreach (Type type in new[] { typeof(Game.Objects.Elevation), typeof(LocalTransformCache) })
                methods.Add(updateComponent.MakeGenericMethod(type));
            foreach (Type type in new[] { typeof(Game.Net.SubNet), typeof(Game.Areas.SubArea),
                typeof(Game.Net.SubLane), typeof(Game.Objects.SubObject) })
                methods.Add(updateBuffer.MakeGenericMethod(type));
            methods.Add(moved);
            return Add(methods, typeof(Game.Objects.ObjectUtils), "GetRefundAmount", typeof(int),
                    typeof(Recent), typeof(uint), typeof(EconomyParameterData))
                && Add(methods, typeof(Transform), "Equals", typeof(bool), typeof(Transform));
        }

        private static bool ObserveEcs(string harmonyMvid, bool relocation)
        {
            var methods = new List<MethodBase>();
            if (!Add(methods, typeof(SystemBase), "Update", typeof(void))
                || !Add(methods, typeof(EntityCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer))
                || !Add(methods, typeof(EntityCommandBufferSystem), "AddJobHandleForProducer", typeof(void), typeof(JobHandle))
                || !Add(methods, typeof(EntityCommandBufferSystem), "OnDestroy", typeof(void))
                || !Add(methods, typeof(EntityCommandBuffer), "Dispose", typeof(void))
                || !Add(methods, typeof(EntityManager), "RemoveComponent", typeof(void), typeof(NativeArray<Entity>),
                    typeof(ComponentTypeSet).MakeByRefType())
                || !Add(methods, typeof(EntityCommandBuffer), "Playback", typeof(void), typeof(EntityManager))) return false;
            if (relocation)
            {
                Type writer = typeof(EntityCommandBuffer.ParallelWriter);
                if (!Add(methods, writer, "CreateEntity", typeof(Entity), typeof(int), typeof(EntityArchetype))) return false;
                MethodInfo? setter = null;
                foreach (var method in writer.GetMethods(Declared))
                {
                    if (method.Name != "SetComponent" || !method.IsGenericMethodDefinition
                        || method.GetGenericArguments().Length != 1 || method.ReturnType != typeof(void)) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 3 || parameters[0].ParameterType != typeof(int)
                        || parameters[1].ParameterType != typeof(Entity)
                        || parameters[2].ParameterType != method.GetGenericArguments()[0]) continue;
                    if (setter != null) return false;
                    setter = method;
                }
                if (setter == null) return false;
                methods.Add(setter);
                methods.Add(setter.MakeGenericMethod(typeof(PathTargetMoved)));
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "0Harmony" || assembly.ManifestModule.ModuleVersionId.ToString("N") != harmonyMvid) continue;
                var patches = assembly.GetType("HarmonyLib.Patches", false);
                var getInfo = assembly.GetType("HarmonyLib.Harmony", false)?.GetMethod("GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
                if (patches == null || getInfo == null || getInfo.ReturnType != patches) return false;
                foreach (MethodBase method in methods)
                {
                    object? info = getInfo.Invoke(null, new object[] { method });
                    if (info == null) continue;
                    if (info.GetType() != patches) return false;
                    foreach (string category in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                        if (!(patches.GetField(category, Declared)?.GetValue(info) is ICollection group) || group.Count != 0) return false;
                }
                return true;
            }
            return false;
        }

        private static void AddDeclared(List<MethodBase> methods, Type type)
        {
            methods.AddRange(type.GetMethods(Declared));
            methods.AddRange(type.GetConstructors(Declared));
        }

        private static bool Add(List<MethodBase> methods, Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, Declared, null, parameters, null);
            if (method == null || method.ReturnType != result) return false;
            methods.Add(method);
            return true;
        }
    }
}
#endif
