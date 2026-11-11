#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Disabled = Unity.Entities.Disabled;
using SubNet = Game.Net.SubNet;

namespace SkylinesLink.Mod.Native
{
    // The coordinator owns the barrier, trigger sink and source entities.
    internal sealed class PreviewCommitNet
    {
        private NativeSystemRegistration<PreviewCommitNetProducer>? registration;
        private bool attempted, registrationReleased = true, protectionConfirmed;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => registration?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => registrationReleased && !NativeWorkMayReadSource;
        internal bool Passed => registration?.Instance.OutputConfirmed == true && protectionConfirmed
            && ResourcesReleased && failureCode == null;
        internal JobHandle CompletedDependency => registration?.Instance.CompletedDependency ?? default;

        internal void RunOnce(World source, Entity[] graph, PreviewCommitBarrier output, PreviewCommitTriggerSink triggers,
            List<ComponentSystemBase> appended, Action requireScope, Action guardLiveGraph, Action observeProtection)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted && graph.Length == 7,
                "PREVIEW_COMMIT_NET_EXECUTION_REJECTED");
            attempted = true;
            try
            {
                requireScope();
                guardLiveGraph();
                requireScope();
                var producer = RegisterProducer(source, output, triggers, appended, requireScope, observeProtection);

                producer.Prepare(graph[0], graph[1], requireScope, guardLiveGraph);
                producer.InspectOutput();
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_COMMIT_NET_FAILED";
                throw new Rejection(failureCode, error);
            }
            finally { ReleaseProducer(appended); }

            if (!ResourcesReleased) throw new Rejection(failureCode ??= "PREVIEW_COMMIT_NET_RELEASE_UNCONFIRMED");
            try
            {
                requireScope();
                guardLiveGraph();
                observeProtection();
                requireScope();
                protectionConfirmed = true;
            }
            catch (Exception error)
            {
                throw new Rejection(failureCode = "PREVIEW_COMMIT_NET_PROTECTION_UNCONFIRMED", error);
            }
        }

        internal void AdmitRelocation(World source, Entity[] graph, Temp[] temps, RelocationDefinitionSource original,
            int actor, PreviewCommitBarrier output, PreviewCommitTriggerSink triggers, List<ComponentSystemBase> appended,
            Action requireScope, Action guardGraph, Action observeProtection)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted && graph.Length == 9 && temps.Length == graph.Length,
                "PREVIEW_COMMIT_NET_RELOCATION_CONTEXT_INVALID");
            attempted = true;
            requireScope();
            guardGraph();
            var producer = RegisterProducer(source, output, triggers, appended, requireScope, observeProtection, actor);
            guardGraph();
            producer.AdmitRelocation(graph, temps, original);
        }

        internal void ApplyRelocation(Entity[] graph, Temp[] temps, RelocationDefinitionSource original, Action requireScope,
            Action guardBeforeWrite, Action observeProtection)
        {
            Require(registration != null, "PREVIEW_COMMIT_NET_RELOCATION_NOT_READY");
            registration!.Instance.ApplyRelocation(graph, temps, original, requireScope, guardBeforeWrite);
            observeProtection();
            protectionConfirmed = true;
        }

        private PreviewCommitNetProducer RegisterProducer(World source, PreviewCommitBarrier output, PreviewCommitTriggerSink triggers,
            List<ComponentSystemBase> appended, Action requireScope, Action observeProtection, int actor = 1)
        {
            Require(source.IsCreated && source.GetExistingSystemManaged<PreviewCommitNetProducer>() == null
                && ReferenceEquals(source.GetExistingSystemManaged<PreviewCommitBarrier>(), output)
                && ReferenceEquals(source.GetExistingSystemManaged<PreviewCommitTriggerSink>(), triggers)
                && ReferenceEquals(output.World, source) && ReferenceEquals(triggers.World, source) && triggers.Enabled,
                "PREVIEW_COMMIT_NET_OWNER_UNAVAILABLE");
            var producer = new PreviewCommitNetProducer();
            producer.Configure(output, triggers, actor);
            registration = new NativeSystemRegistration<PreviewCommitNetProducer>(source, producer);
            registrationReleased = false;
            registration.Add();
            appended.Add(producer);
            requireScope();
            observeProtection();
            return producer;
        }

        internal void ReleaseProducer(List<ComponentSystemBase> appended)
        {
            if (registration == null || NativeWorkMayReadSource) return;
            var owned = registration;
            try
            {
                owned.Destroy();
                registrationReleased = owned.PhysicalReleased && (!owned.DestroyAttempted
                    || owned.DestroyReturned && owned.Instance.DestructionReturned);
                if (registrationReleased) appended.Remove(owned.Instance);
            }
            catch (Exception) { failureCode ??= "PREVIEW_COMMIT_NET_RELEASE_FAILED"; }
        }

        internal JObject Snapshot() => new JObject
        {
            ["passed"] = Passed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["resourcesReleased"] = ResourcesReleased, ["protectionConfirmed"] = protectionConfirmed,
            ["producer"] = registration?.Instance.Snapshot(), ["failureCode"] = failureCode
        };

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
    public sealed partial class PreviewCommitNetProducer : ApplyNetSystem
    {
        private const int BufferIndex = PreviewCommitBarrier.NetBufferIndex;
        private int actor;
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private PreviewCommitBarrier? output;
        private PreviewCommitTriggerSink? triggers;
        private SimulationSystem? simulation;
        private EntityQuery nodes, economy;
        private ComponentTypeSet appliedTypes;
        private readonly List<(FieldInfo Field, object Value)> bindings = new List<(FieldInfo, object)>(8);
        private Entity root, node;
        private Entity[]? relocationGraph;
        private Temp[]? relocationTemps;
        private RelocationDefinitionSource? relocationOriginal;
        private Node oldNodeBefore;
        private bool relocationReferencesConfirmed;
        private Action? requireScope, guardLiveGraph;
        private Temp before;
        private Node nodeBefore;
        private bool creationReturned, armed, nativeEntered, nativeReturned, nativeJoined;
        private int? count, chunkCount, methodsChecked, commandsObserved, expectedCommands;
        private uint? frame;

        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal bool OutputConfirmed { get; private set; }
        internal JobHandle CompletedDependency { get; private set; }

        internal void Configure(PreviewCommitBarrier barrier, PreviewCommitTriggerSink sink, int actor)
        {
            Require(output == null && World == null && actor > 0, "PREVIEW_COMMIT_NET_CONFIGURATION_REJECTED");
            this.actor = actor;
            output = barrier;
            triggers = sink;
        }

        protected override void OnCreate()
        {
            Enabled = false;
            Require(MainThreadDispatcher.isMainThread && output != null && triggers != null
                && ReferenceEquals(output.World, World) && ReferenceEquals(triggers.World, World) && triggers.Enabled,
                "PREVIEW_COMMIT_NET_CONFIGURATION_MISSING");
            RequirePinnedModules();
            simulation = World.GetExistingSystemManaged<SimulationSystem>()
                ?? throw new PreviewCommitNet.Rejection("PREVIEW_COMMIT_NET_SIMULATION_UNAVAILABLE");
            nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Disabled>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>() },
                Any = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Lane>(), ComponentType.ReadOnly<Aggregate>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            nodes.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            economy = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            RequireForUpdate(nodes);
            RequireForUpdate(economy);
            appliedTypes = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Created>(),
                ComponentType.ReadWrite<Updated>());
            Bind<ToolOutputBarrier>("m_ToolOutputBarrier", output!);
            Bind("m_SimulationSystem", simulation);
            Bind<TriggerSystem>("m_TriggerSystem", triggers!);
            Bind("m_TempQuery", nodes);
            Bind("m_EconomyParameterQuery", economy);
            Bind("m_ApplyCreatedTypes", appliedTypes);
            Bind("m_ApplyUpdatedTypes", new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Updated>()));
            Bind("m_ApplyDeletedTypes", new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Deleted>()));
            creationReturned = true;
        }

        private void Bind<T>(string name, T value) where T : notnull
        {
            FieldInfo? field = typeof(ApplyNetSystem).GetField(name, Declared);
            Require(field != null && !field.IsStatic && !field.IsInitOnly && field.FieldType == typeof(T),
                "PREVIEW_COMMIT_NET_FIELD_ABI");
            field!.SetValue(this, value);
            bindings.Add((field, value));
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            Require(!NativeWorkMayReadSource, "PREVIEW_COMMIT_NET_NATIVE_WORK_UNRESOLVED");
            requireScope = guardLiveGraph = null;
            DestructionReturned = true;
        }

        internal void AdmitRelocation(Entity[] graph, Temp[] temps, RelocationDefinitionSource original)
        {
            Require(creationReturned && !armed && !Enabled && ShouldRunSystem(), "PREVIEW_COMMIT_NET_RELOCATION_NOT_READY");
            RequireRelocationInputs(graph, temps, original, PreviewCommitBarrier.RelocationNotificationBufferIndex);
        }

        private void RequireRelocationInputs(Entity[] graph, Temp[] temps, RelocationDefinitionSource original, int outputSlot)
        {
            RequireBindings();
            Require(output!.PendingCount == outputSlot
                && economy.CalculateEntityCount() == 1, "PREVIEW_COMMIT_NET_RELOCATION_ECONOMY_OR_SLOT");
            var patches = ObservePatches();
            methodsChecked = patches.MethodsChecked;
            Require(patches.Qualified && SubNetReferencesPatchProbe.Observe().Qualified,
                "PREVIEW_COMMIT_NET_RELOCATION_PATCH_UNQUALIFIED");

            Entity old = graph[1], incoming = graph[2];
            count = nodes.CalculateEntityCount();
            chunkCount = nodes.CalculateChunkCount();
            Require(count == 2 && chunkCount == 2 && old != incoming, "PREVIEW_COMMIT_NET_RELOCATION_QUERY_BOUND");
            using (var queried = nodes.ToEntityArray(Allocator.Temp))
                Require(queried.Length == 2 && ((queried[0] == old && queried[1] == incoming)
                    || (queried[0] == incoming && queried[1] == old)), "PREVIEW_COMMIT_NET_RELOCATION_QUERY_MEMBER");

            var entities = EntityManager;
            Require(temps[0].m_Original == original.Root && temps[0].m_Flags == (TempFlags.Modify | TempFlags.Essential)
                && temps[1].m_Original == original.NodeEntity && temps[1].m_Flags == (TempFlags.Delete | TempFlags.Hidden)
                && temps[2].m_Original == Entity.Null && temps[2].m_Flags == (TempFlags.Create | TempFlags.IsLast)
                && temps[1].m_Cost == 0 && temps[2].m_Cost == 0
                && entities.GetComponentData<Temp>(graph[0]).Equals(temps[0])
                && entities.GetComponentData<Temp>(old).Equals(temps[1])
                && entities.GetComponentData<Temp>(incoming).Equals(temps[2]),
                "PREVIEW_COMMIT_NET_RELOCATION_TEMP_CHANGED");
            Require(IsIsolatedNode(entities, old) && IsIsolatedNode(entities, incoming)
                && IsIsolatedNode(entities, original.NodeEntity)
                && !entities.HasComponent<Owner>(old) && !entities.HasComponent<OwnerDefinition>(old)
                && entities.HasComponent<Owner>(incoming) && !entities.HasComponent<OwnerDefinition>(incoming)
                && entities.GetComponentData<Owner>(incoming).m_Owner == graph[0]
                && entities.GetComponentData<Owner>(original.NodeEntity).m_Owner == original.Root,
                "PREVIEW_COMMIT_NET_RELOCATION_OWNER_OR_NODE_CHANGED");
            Require(ContainsOnlySubnet(entities, graph[0], incoming)
                && ContainsOnlySubnet(entities, original.Root, original.NodeEntity),
                "PREVIEW_COMMIT_NET_RELOCATION_SUBNET_CHANGED");
        }

        internal void ApplyRelocation(Entity[] graph, Temp[] temps, RelocationDefinitionSource original, Action scope, Action before)
        {
            Require(creationReturned && !armed, "PREVIEW_COMMIT_NET_RELOCATION_ALREADY_ARMED");
            relocationGraph = graph;
            relocationTemps = temps;
            relocationOriginal = original;
            Prepare(graph[0], graph[2], scope, before);
            scope();
            RequireBindings();
            Require(nativeJoined && simulation!.frameIndex == frame && output!.PendingCount == PreviewCommitBarrier.RelocationNetBufferIndex + 1,
                "PREVIEW_COMMIT_NET_RELOCATION_OUTPUT_UNAVAILABLE");
            RequireTransferredReferences(graph, temps, original);
            relocationReferencesConfirmed = true;
            InspectRelocationCommands(graph, original);
            OutputConfirmed = true;
        }

        private void RequireTransferredReferences(Entity[] graph, Temp[] temps, RelocationDefinitionSource original)
        {
            var entities = EntityManager;
            var formal = entities.GetBuffer<SubNet>(original.Root, true);
            Require(entities.GetBuffer<SubNet>(graph[0], true).Length == 0 && formal.Length == 2
                && formal[0].m_SubNet == original.NodeEntity && formal[1].m_SubNet == graph[2]
                && entities.GetComponentData<Owner>(graph[2]).m_Owner == original.Root
                && entities.GetComponentData<Owner>(original.NodeEntity).m_Owner == original.Root
                && !entities.HasComponent<Owner>(graph[1]), "PREVIEW_COMMIT_NET_RELOCATION_REFERENCES_UNCONFIRMED");
            for (int i = 0; i < 3; i++)
                Require(entities.GetComponentData<Temp>(graph[i]).Equals(temps[i]), "PREVIEW_COMMIT_NET_RELOCATION_TEMP_CHANGED");
            Require(IsIsolatedNode(entities, graph[1]) && IsIsolatedNode(entities, graph[2])
                && IsIsolatedNode(entities, original.NodeEntity)
                && entities.GetComponentData<Node>(graph[1]).Equals(oldNodeBefore)
                && entities.GetComponentData<Node>(graph[2]).Equals(nodeBefore)
                && entities.GetComponentData<Node>(original.NodeEntity).Equals(original.NodePose),
                "PREVIEW_COMMIT_NET_RELOCATION_NODE_POSE_CHANGED");
        }

        private void InspectRelocationCommands(Entity[] graph, RelocationDefinitionSource original)
        {
            var abi = new ObjectDefinitionCommandProbe.Abi();
            var setView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic);
            Require(setView != null, "PREVIEW_COMMIT_NET_COMMAND_ABI");
            object view = abi.Constructor.Invoke(new object[] { output!.Borrow(PreviewCommitBarrier.RelocationNetBufferIndex) });
            var commands = (Array)abi.Commands.GetValue(view)!;
            commandsObserved = commands.Length;
            Require(commands.Length == 4, "PREVIEW_COMMIT_NET_RELOCATION_COMMAND_COUNT");
            bool oldFormal = false, oldTemp = false, newTempRemoved = false, newMarkers = false;
            foreach (object command in commands)
            {
                var target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Require(abi.Int(command, "SortKey") >= 0 && abi.Int(command, "BatchCount") == 1
                    && abi.Int(command, "IdentityIndex") == 0 && abi.Int(command, "TotalSizeInBytes") is > 0 and <= 2048,
                    "PREVIEW_COMMIT_NET_RELOCATION_COMMAND_HEADER");
                if (target == original.NodeEntity || target == graph[1])
                {
                    Require(target == original.NodeEntity ? !oldFormal : !oldTemp, "PREVIEW_COMMIT_NET_RELOCATION_COMMAND_DUPLICATE");
                    RequireComponent<Deleted>(command, kind, "AddComponent", abi, hasValue: false);
                    if (target == original.NodeEntity) oldFormal = true;
                    else oldTemp = true;
                }
                else
                {
                    Require(target == graph[2] && !newMarkers, "PREVIEW_COMMIT_NET_RELOCATION_COMMAND_TARGET");
                    if (!newTempRemoved)
                    {
                        RequireComponent<Temp>(command, kind, "RemoveComponent", abi, hasValue: false);
                        newTempRemoved = true;
                    }
                    else
                    {
                        RequireAppliedTypeSet(command, setView!, kind, abi);
                        newMarkers = true;
                    }
                }
            }
            Require(oldFormal && oldTemp && newTempRemoved && newMarkers, "PREVIEW_COMMIT_NET_RELOCATION_COMMAND_CLOSURE");
        }

        private static bool IsIsolatedNode(EntityManager entities, Entity node) => entities.Exists(node)
            && entities.HasComponent<Node>(node) && !entities.HasComponent<Edge>(node)
            && !entities.HasComponent<Lane>(node) && !entities.HasComponent<Aggregate>(node)
            && !entities.HasComponent<Deleted>(node) && entities.HasBuffer<ConnectedEdge>(node)
            && entities.GetBuffer<ConnectedEdge>(node, true).Length == 0
            && (!entities.HasBuffer<ConnectedNode>(node) || entities.GetBuffer<ConnectedNode>(node, true).Length == 0);

        private static bool ContainsOnlySubnet(EntityManager entities, Entity parent, Entity expected)
        {
            if (!entities.Exists(parent) || !entities.HasBuffer<SubNet>(parent)) return false;
            var subnet = entities.GetBuffer<SubNet>(parent, true);
            return subnet.Length == 1 && subnet[0].m_SubNet == expected;
        }

        internal void Prepare(Entity rootEntity, Entity nodeEntity, Action scope, Action liveGraph)
        {
            Require(creationReturned && !armed, "PREVIEW_COMMIT_NET_COHORT_REJECTED");
            armed = true;
            root = rootEntity;
            node = nodeEntity;
            requireScope = scope;
            guardLiveGraph = liveGraph;
            CheckGraph();
            Require(ShouldRunSystem(), "PREVIEW_COMMIT_NET_REQUIRED_QUERY");
            Enabled = true;
            try
            {
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_COMMIT_NET_NATIVE_NOT_RETURNED");
                JobHandle original = Dependency;
                Require(!original.Equals(default(JobHandle)), "PREVIEW_COMMIT_NET_COMBINED_DEPENDENCY_MISSING");
                var completion = original;
                completion.Complete();
                CompletedDependency = original;
                nativeJoined = true;
            }
            finally { Enabled = false; }
        }

        protected override void OnUpdate()
        {
            Require(armed && !nativeEntered, "PREVIEW_COMMIT_NET_NATIVE_NOT_ARMED");
            CheckGraph();
            if (relocationOriginal != null)
            {
                RequireRelocationInputs(relocationGraph!, relocationTemps!, relocationOriginal, PreviewCommitBarrier.RelocationNetBufferIndex);
                nodeBefore = EntityManager.GetComponentData<Node>(node);
                oldNodeBefore = EntityManager.GetComponentData<Node>(relocationGraph![1]);
                frame = simulation!.frameIndex;
                expectedCommands = 4;
                requireScope!();
                nativeEntered = true;
                base.OnUpdate();
                nativeReturned = true;
                return;
            }
            var patches = ObservePatches();
            methodsChecked = patches.MethodsChecked;
            Require(patches.Qualified, "PREVIEW_COMMIT_NET_PATCH_UNQUALIFIED");
            RequireBindings();
            Require(output!.PendingCount == BufferIndex && economy.CalculateEntityCount() == 1,
                "PREVIEW_COMMIT_NET_ECONOMY_OR_BARRIER_BOUND");
            before = ReadNode();
            nodeBefore = EntityManager.GetComponentData<Node>(node);
            frame = simulation!.frameIndex;
            expectedCommands = before.m_Cost > 0 ? 3 : 2;
            requireScope!();
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private Temp ReadNode()
        {
            count = nodes.CalculateEntityCount();
            chunkCount = nodes.CalculateChunkCount();
            Require(count == 1 && chunkCount == 1, "PREVIEW_COMMIT_NET_QUERY_BOUND");
            using (var entities = nodes.ToEntityArray(Allocator.Temp))
                Require(entities.Length == 1 && entities[0] == node, "PREVIEW_COMMIT_NET_QUERY_MEMBER");

            var manager = EntityManager;
            Require(root.Index > 0 && node.Index > 0 && node != root && manager.Exists(root) && manager.Exists(node)
                && manager.HasComponent<Disabled>(root) && manager.HasComponent<ObjectGenerationScope>(root)
                && manager.GetSharedComponent<ObjectGenerationScope>(root).Value == actor
                && manager.HasComponent<Temp>(root) && manager.HasBuffer<SubNet>(root)
                && manager.HasComponent<Node>(node) && !manager.HasComponent<Edge>(node)
                && !manager.HasComponent<Lane>(node) && !manager.HasComponent<Aggregate>(node)
                && manager.HasComponent<Owner>(node) && manager.HasBuffer<ConnectedEdge>(node)
                && !manager.HasComponent<Deleted>(node) && !manager.HasComponent<Recent>(node),
                "PREVIEW_COMMIT_NET_ROLE_UNQUALIFIED");
            var subNets = manager.GetBuffer<SubNet>(root, true);
            Require(subNets.Length == 1 && subNets[0].m_SubNet == node, "PREVIEW_COMMIT_NET_SUBNET_UNQUALIFIED");
            var temp = manager.GetComponentData<Temp>(node);
            Require(manager.GetComponentData<Owner>(node).m_Owner == root
                && manager.GetComponentData<Temp>(root).m_Original == Entity.Null
                && manager.GetBuffer<ConnectedEdge>(node, true).Length == 0
                && temp.m_Original == Entity.Null && temp.m_Flags == (TempFlags.Create | TempFlags.IsLast) && temp.m_Cost >= 0,
                "PREVIEW_COMMIT_NET_NODE_UNQUALIFIED");
            return temp;
        }

        internal void InspectOutput()
        {
            CheckGraph();
            RequireBindings();
            Require(nativeJoined && !OutputConfirmed && expectedCommands.HasValue && frame.HasValue
                && output!.PendingCount == BufferIndex + 1 && simulation!.frameIndex == frame.Value,
                "PREVIEW_COMMIT_NET_OUTPUT_UNAVAILABLE");
            Require(ReadNode().Equals(before) && EntityManager.GetComponentData<Node>(node).Equals(nodeBefore),
                "PREVIEW_COMMIT_NET_SOURCE_CHANGED");

            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type? setView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic);
            Require(setView != null, "PREVIEW_COMMIT_NET_COMMAND_ABI");
            object view = abi.Constructor.Invoke(new object[] { output!.Borrow(BufferIndex) });
            var commands = (Array)abi.Commands.GetValue(view)!;
            commandsObserved = commands.Length;
            Require(commands.Length == expectedCommands!.Value, "PREVIEW_COMMIT_NET_COMMAND_COUNT");
            int sortKey = abi.Int(commands.GetValue(0)!, "SortKey");
            for (int index = 0; index < commands.Length; index++)
            {
                object command = commands.GetValue(index)!;
                int bytes = abi.Int(command, "TotalSizeInBytes");
                Require((Entity)abi.Value(command, "Entity", typeof(Entity)) == node
                    && sortKey >= 0 && abi.Int(command, "SortKey") == sortKey
                    && abi.Int(command, "BatchCount") == 1 && abi.Int(command, "IdentityIndex") == 0
                    && bytes > 0 && bytes <= 2048, "PREVIEW_COMMIT_NET_COMMAND_TARGET");
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                if (index == 0)
                {
                    RequireComponent<Temp>(command, kind, "RemoveComponent", abi, hasValue: false);
                    continue;
                }
                if (index == commands.Length - 1)
                {
                    RequireAppliedTypeSet(command, setView!, kind, abi);
                    continue;
                }
                RequireComponent<Recent>(command, kind, "AddComponent", abi, hasValue: true);
                var recent = (Recent)abi.Value(command, "ComponentValue", typeof(object));
                Require(recent.m_ModificationFrame == frame!.Value && recent.m_ModificationCost == before.m_Cost,
                    "PREVIEW_COMMIT_NET_RECENT_VALUE");
            }
            CheckGraph();
            OutputConfirmed = true;
        }

        private void RequireAppliedTypeSet(object command, Type setView, string kind, ObjectDefinitionCommandProbe.Abi abi)
        {
            Require(command.GetType() == setView && kind == "AddMultipleComponents", "PREVIEW_COMMIT_NET_COMMAND_TYPE_SET");
            var actual = (ComponentTypeSet)abi.Value(command, "TypeSet", typeof(ComponentTypeSet));
            Require(actual.Length == appliedTypes.Length, "PREVIEW_COMMIT_NET_COMMAND_TYPE_SET");
            for (int i = 0; i < actual.Length; i++)
                Require(actual.GetTypeIndex(i) == appliedTypes.GetTypeIndex(i), "PREVIEW_COMMIT_NET_COMMAND_TYPE_SET");
        }

        private static void RequireComponent<T>(object command, string kind, string expectedKind,
            ObjectDefinitionCommandProbe.Abi abi, bool hasValue) where T : unmanaged, IComponentData
        {
            TypeIndex type = TypeManager.GetTypeIndex<T>();
            int size = hasValue ? TypeManager.GetTypeInfo(type).TypeSize : 0;
            Require(command.GetType() == abi.Component && kind == expectedKind
                && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == type
                && (short)abi.Value(command, "ComponentSize", typeof(short)) == size
                && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)),
                "PREVIEW_COMMIT_NET_COMMAND_COMPONENT");
            object? value = abi.Value(command, "ComponentValue", typeof(object));
            Require(hasValue ? value is T : value == null, "PREVIEW_COMMIT_NET_COMMAND_VALUE");
        }

        private void CheckGraph()
        {
            requireScope!();
            Require(creationReturned && World != null && World.IsCreated && !DestructionReturned,
                "PREVIEW_COMMIT_NET_SOURCE_UNAVAILABLE");
            guardLiveGraph!();
            requireScope();
        }

        private void RequireBindings()
        {
            Require(ReferenceEquals(World.GetExistingSystemManaged<SimulationSystem>(), simulation)
                && ReferenceEquals(World.GetExistingSystemManaged<PreviewCommitBarrier>(), output)
                && ReferenceEquals(World.GetExistingSystemManaged<PreviewCommitTriggerSink>(), triggers)
                && ReferenceEquals(output!.World, World) && ReferenceEquals(triggers!.World, World) && triggers.Enabled,
                "PREVIEW_COMMIT_NET_BINDINGS_CHANGED");
            foreach (var binding in bindings)
                Require(Equals(binding.Field.GetValue(this), binding.Value), "PREVIEW_COMMIT_NET_BINDINGS_CHANGED");
        }

        private static void RequirePinnedModules()
        {
            Require(typeof(ApplyNetSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                && typeof(EntityCommandBuffer).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                "PREVIEW_COMMIT_NET_MODULE_ABI");
        }

        private static DefinitionProducerPatchProbe ObservePatches()
        {
            Type owner = typeof(ApplyNetSystem);
            var methods = new List<MethodBase>();
            AddDeclared(owner);
            foreach (string name in new[] { "PatchTempReferencesJob", "FixConnectedEdgesJob", "HandleTempEntitiesJob", "TypeHandle" })
            {
                Type? type = owner.GetNestedType(name, BindingFlags.NonPublic);
                Require(type != null && type.IsValueType, "PREVIEW_COMMIT_NET_METHOD_ABI");
                AddDeclared(type!);
            }
            bool found = Add(owner, "OnUpdate", typeof(void), alreadyIncluded: true)
                && Add(typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer))
                && Add(typeof(TriggerSystem), "CreateActionBuffer", typeof(NativeQueue<TriggerAction>))
                && Add(typeof(TriggerSystem), "AddActionBufferWriter", typeof(void), false, typeof(JobHandle))
                && Add(typeof(SimulationSystem), "get_frameIndex", typeof(uint));
            return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_COMMIT_NET");

            void AddDeclared(Type type)
            {
                methods.AddRange(type.GetMethods(Declared));
                methods.AddRange(type.GetConstructors(Declared));
            }

            bool Add(Type type, string name, Type result, bool alreadyIncluded = false, params Type[] parameters)
            {
                var method = type.GetMethod(name, Declared, null, parameters, null);
                if (method == null || method.ReturnType != result) return false;
                if (!alreadyIncluded) methods.Add(method);
                return true;
            }
        }

        internal JObject Snapshot() => new JObject
        {
            ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
            ["targetCount"] = count, ["chunkCount"] = chunkCount, ["appliedFrame"] = frame,
            ["methodsChecked"] = methodsChecked, ["expectedCommands"] = expectedCommands,
            ["commandsObserved"] = commandsObserved, ["outputConfirmed"] = OutputConfirmed,
            ["destructionReturned"] = DestructionReturned, ["relocationReferencesConfirmed"] = relocationReferencesConfirmed
        };

        private static void Require(bool condition, string code) => PreviewCommitNet.Require(condition, code);
    }
}
#endif
