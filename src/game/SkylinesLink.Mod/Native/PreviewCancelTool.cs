#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Buildings;
using Game.Common;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using NativeObject = Game.Objects.Object;

namespace SkylinesLink.Mod.Native
{
    internal sealed class PreviewCancelTool
    {
        private NativeSystemRegistration<PreviewCancelToolBarrier>? barrier;
        private NativeSystemRegistration<PreviewCancelClearProducer>? producer;
        private bool attempted, barrierReleased = true, producerReleased = true;
        private bool closureQualified, deletionReadback, protectionConfirmed;
        private int[]? commandCounts;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => producer?.Instance.NativeWorkMayReadSource == true;
        internal bool EffectsStarted { get; private set; }
        internal bool EffectsReturned { get; private set; }
        internal bool ResourcesReleased => barrierReleased && producerReleased && !NativeWorkMayReadSource;
        internal bool Passed => deletionReadback && protectionConfirmed && ResourcesReleased && failureCode == null;

        internal void RunOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection, int actor = 1, Temp[]? captured = null)
        {
            Require(MainThreadDispatcher.isMainThread, "PREVIEW_CANCEL_TOOL_MAIN_THREAD_REQUIRED");
            if (attempted) throw new Rejection("PREVIEW_CANCEL_TOOL_REPEATED");
            if (source == null || graph == null || appended == null || requireScope == null
                || guardLiveGraph == null || observeProtection == null) throw new Rejection("PREVIEW_CANCEL_TOOL_CONTEXT_MISSING");
            int parents = captured == null ? 2 : 3;
            Require((captured == null ? PreviewCommitGraph.IsNewMemberCount(graph.Length) : graph.Length == 9)
                && (captured == null || captured.Length == graph.Length),
                "PREVIEW_CANCEL_TOOL_GRAPH_BOUND");
            attempted = true;
            Entity[] members = (Entity[])graph.Clone();
            try
            {
                requireScope();
                guardLiveGraph();
                Require(source.IsCreated && source.GetExistingSystemManaged<PreviewCancelToolBarrier>() == null
                    && source.GetExistingSystemManaged<PreviewCancelClearProducer>() == null, "PREVIEW_CANCEL_TOOL_OWNER_ALREADY_PRESENT");
                barrier = new NativeSystemRegistration<PreviewCancelToolBarrier>(source, new PreviewCancelToolBarrier());
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                var instance = new PreviewCancelClearProducer();
                instance.Configure(barrier.Instance, actor, captured);
                producer = new NativeSystemRegistration<PreviewCancelClearProducer>(source, instance);
                producerReleased = false;
                Register(producer, appended, requireScope);
                observeProtection();
                guardLiveGraph();

                instance.Produce(members, requireScope, guardLiveGraph);
                Require(instance.NativeJoined && !NativeWorkMayReadSource, "PREVIEW_CANCEL_TOOL_NATIVE_UNCONFIRMED");
                EntityCommandBuffer buffer = barrier.Instance.BorrowSingle();
                commandCounts = ReadBulkMetadata(buffer, parents);
                requireScope();
                guardLiveGraph();
                observeProtection();
                instance.RequirePlaybackInputs(members, requireScope);
                EntityManager entities = source.EntityManager;
                closureQualified = true;

                // The pinned native job's exact input closure authorizes these
                // parent targets. The debug view has no safe target-array accessor.
                EffectsStarted = true;
                buffer.Playback(entities);
                EffectsReturned = true;
                requireScope();
                instance.RequireGraph(entities, members, markersApplied: true);
                requireScope();
                deletionReadback = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_CANCEL_TOOL_FAILED";
                throw new Rejection(failureCode, error);
            }
            finally
            {
                // A synchronous Playback throw may leave graph effects
                // unknown, but its call has ended and the joined ECB can retire.
                if (!NativeWorkMayReadSource)
                {
                    if (producer != null) producerReleased = Release(producer, appended, () => producer.Instance.StorageReleased);
                    if (barrier != null) barrierReleased = Release(barrier, appended, () => barrier.Instance.StorageReleased);
                }
            }
            if (!ResourcesReleased) throw new Rejection(failureCode ??= "PREVIEW_CANCEL_TOOL_RELEASE_UNCONFIRMED");
            try
            {
                requireScope();
                observeProtection();
                protectionConfirmed = true;
            }
            catch (Exception error) { throw new Rejection(failureCode = "PREVIEW_CANCEL_TOOL_PROTECTION_UNCONFIRMED", error); }
        }

        private static int[] ReadBulkMetadata(EntityCommandBuffer buffer, int parents)
        {
            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type bulk = typeof(EntityCommandBuffer).GetNestedType("MultipleEntitiesComponentCommandView",
                BindingFlags.NonPublic) ?? throw new Rejection("PREVIEW_CANCEL_TOOL_BULK_ABI_UNQUALIFIED");
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            var commands = (Array)abi.Commands.GetValue(proxy)!;
            Require(commands.Length == parents, "PREVIEW_CANCEL_TOOL_BULK_COMMAND_COUNT");
            var counts = new int[parents];
            var sortKeys = new HashSet<int>();
            for (int index = 0; index < commands.Length; index++)
            {
                object command = commands.GetValue(index)!;
                string? kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!);
                Require(command.GetType() == bulk && kind == "AddComponentForMultipleEntities", "PREVIEW_CANCEL_TOOL_BULK_COMMAND_ROLE");
                int count = abi.Int(command, "EntitiesCount");
                int size = abi.Int(command, "TotalSizeInBytes"), key = abi.Int(command, "SortKey");
                var type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                Require(count == 1 && size > 0 && size <= 2048 && key >= 0 && sortKeys.Add(key)
                    && type == TypeManager.GetTypeIndex<Deleted>() && (short)abi.Value(command, "ComponentSize", typeof(short)) == 0
                    && abi.Value(command, "ComponentValue", typeof(object)) == null
                    && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))
                    && (bool)abi.Value(command, "SkipDeferredEntityLookup", typeof(bool)), "PREVIEW_CANCEL_TOOL_BULK_COMMAND_UNQUALIFIED");
                counts[index] = count;
            }
            return counts;
        }

        private static void Register<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            Action requireScope) where T : ComponentSystemBase
        {
            registration.Add();
            appended.Add(registration.Instance);
            requireScope();
        }

        private bool Release<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            Func<bool> storageReleased) where T : ComponentSystemBase
        {
            try
            {
                registration.Destroy();
                bool released = registration.PhysicalReleased && storageReleased()
                    && (!registration.DestroyAttempted || registration.DestroyReturned);
                if (released) appended.Remove(registration.Instance);
                else failureCode ??= "PREVIEW_CANCEL_TOOL_RELEASE_UNCONFIRMED";
                return released;
            }
            catch (Exception)
            {
                failureCode ??= "PREVIEW_CANCEL_TOOL_DESTROY_FAILED";
                return false;
            }
        }

        internal JObject Snapshot() => new JObject
        {
            ["passed"] = Passed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["producer"] = producer?.Instance.Snapshot(), ["bulkDeletedCounts"] = commandCounts == null ? null : new JArray(commandCounts),
            ["targetsQualifiedByProducerClosure"] = closureQualified, ["bulkTargetArrayInspected"] = false,
            ["effectsStarted"] = EffectsStarted, ["effectsReturned"] = EffectsReturned,
            ["deletionReadback"] = deletionReadback, ["protectionConfirmed"] = protectionConfirmed,
            ["barrierReleased"] = barrierReleased, ["producerReleased"] = producerReleased, ["failureCode"] = failureCode
        };

        internal static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code, Exception? inner = null) : base(code, inner)
            { Code = code; }
        }
    }

    [DisableAutoCreation]
    public sealed partial class PreviewCancelToolBarrier : ToolOutputBarrier
    {
        private bool creationStarted, destructionStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            PreviewCancelTool.Require(MainThreadDispatcher.isMainThread, "PREVIEW_CANCEL_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new PreviewCancelTool.Rejection("PREVIEW_CANCEL_BARRIER_UNSCHEDULED_ONLY");

        internal EntityCommandBuffer BorrowSingle()
        {
            PreviewCancelTool.Require(CreationReturned && !destructionStarted && PendingCount == 1 && PendingBuffers[0].IsCreated,
                "PREVIEW_CANCEL_BARRIER_BUFFER_UNAVAILABLE");
            return PendingBuffers[0];
        }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            if (creationStarted)
            {
                PreviewCancelTool.Require(CreationReturned, "PREVIEW_CANCEL_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }

    [DisableAutoCreation]
    public sealed partial class PreviewCancelClearProducer : ToolClearSystem
    {
        private int actor;
        private Temp[]? captured;
        private int ParentCount => captured == null ? 2 : 3;
        private int memberCount;
        private static readonly BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private FieldInfo? queryField, barrierField;
        private PreviewCancelToolBarrier? barrier;
        private EntityQuery query;
        private Entity[]? members;
        private Action? scope, liveGraph;
        private bool creationStarted, destructionStarted, attempted, nativeEntered, nativeReturned, patchQualified;
        private int? queryCount, chunkCount;
        private int methodsChecked;
        private string? methodSetSha256;

        internal bool DestructionReturned { get; private set; }
        private bool CreationReturned { get; set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal bool NativeJoined { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !NativeJoined;

        internal void Configure(PreviewCancelToolBarrier output, int actor, Temp[]? captured)
        {
            Require(!creationStarted && barrier == null && output != null && World == null && actor > 0,
                "PREVIEW_CANCEL_TOOL_CONFIGURATION_REJECTED");
            RequireUnpatched();
            queryField = Field("m_ClearQuery", typeof(EntityQuery));
            barrierField = Field("m_ToolOutputBarrier", typeof(ToolOutputBarrier));
            barrier = output;
            this.actor = actor;
            this.captured = captured;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            Require(MainThreadDispatcher.isMainThread, "PREVIEW_CANCEL_TOOL_MAIN_THREAD_REQUIRED");
            Enabled = false;
            Require(barrier != null && barrier.CreationReturned && ReferenceEquals(barrier.World, World)
                && queryField != null && barrierField != null, "PREVIEW_CANCEL_TOOL_CONFIGURATION_MISSING");
            query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            queryField!.SetValue(this, query);
            barrierField!.SetValue(this, barrier);
            RequireForUpdate(query);
            RequireBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "PREVIEW_CANCEL_TOOL_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void Produce(Entity[] graph, Action requireScope, Action guardLiveGraph)
        {
            Require(CreationReturned && !destructionStarted && !attempted
                && (captured == null ? PreviewCommitGraph.IsNewMemberCount(graph.Length) : graph.Length == 9 && captured.Length == graph.Length),
                "PREVIEW_CANCEL_TOOL_PRODUCTION_REJECTED");
            attempted = true;
            memberCount = graph.Length;
            members = graph; scope = requireScope; liveGraph = guardLiveGraph;
            try
            {
                Guard(requireScope);
                Require(ShouldRunSystem(), "PREVIEW_CANCEL_TOOL_REQUIRED_QUERY_REJECTED");
                Enabled = true;
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_CANCEL_TOOL_NATIVE_NOT_RETURNED");
                JobHandle writer = Dependency;
                Require(!writer.Equals(default(JobHandle)), "PREVIEW_CANCEL_TOOL_WRITER_MISSING");
                writer.Complete();
                NativeJoined = true;
            }
            finally { Enabled = false; members = null; scope = null; liveGraph = null; }
        }

        protected override void OnUpdate()
        {
            var requireScope = scope ?? throw new PreviewCancelTool.Rejection("PREVIEW_CANCEL_TOOL_SCOPE_MISSING");
            var guardLiveGraph = liveGraph ?? throw new PreviewCancelTool.Rejection("PREVIEW_CANCEL_TOOL_GRAPH_GUARD_MISSING");
            Require(attempted && !nativeEntered, "PREVIEW_CANCEL_TOOL_NATIVE_REPEATED");
            RequireUnpatched();
            Guard(requireScope);
            guardLiveGraph();
            RequireBindings();
            RequireMembers(members!);
            RequireGraph(EntityManager, members!, markersApplied: false);
            Require(barrier!.PendingCount == 0, "PREVIEW_CANCEL_TOOL_OUTPUT_ALREADY_PRESENT");
            Guard(requireScope);
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        internal void RequirePlaybackInputs(Entity[] graph, Action requireScope)
        {
            Guard(requireScope);
            Require(NativeJoined && nativeReturned && barrier!.PendingCount == 1, "PREVIEW_CANCEL_TOOL_OUTPUT_NOT_READY");
            RequireUnpatched();
            RequireBindings();
            RequireMembers(graph);
            RequireGraph(EntityManager, graph, markersApplied: false);
            Guard(requireScope);
        }

        private void RequireMembers(Entity[] graph)
        {
            queryCount = query.CalculateEntityCount();
            chunkCount = query.CalculateChunkCount();
            Require(graph.Length == memberCount && queryCount == memberCount && chunkCount >= ParentCount && chunkCount <= memberCount,
                "PREVIEW_CANCEL_TOOL_QUERY_BOUND");
            using var values = query.ToEntityArray(Allocator.Temp);
            Require(values.Length == memberCount, "PREVIEW_CANCEL_TOOL_QUERY_CHANGED");
            foreach (Entity entity in values)
                Require(Array.IndexOf(graph, entity) >= 0, "PREVIEW_CANCEL_TOOL_FOREIGN_QUERY_MEMBER");
        }

        internal void RequireGraph(EntityManager entities, Entity[] graph, bool markersApplied)
        {
            Require(attempted && graph.Length == memberCount, "PREVIEW_CANCEL_TOOL_GRAPH_BOUND");
            var childCounts = new int[ParentCount];
            for (int i = 0; i < graph.Length; i++)
            {
                Entity entity = graph[i];
                Require(entity != Entity.Null && Array.IndexOf(graph, entity) == i && entities.Exists(entity)
                    && entities.HasComponent<Unity.Entities.Disabled>(entity) && entities.HasComponent<Temp>(entity)
                    && entities.HasComponent<ObjectGenerationScope>(entity)
                    && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor
                    && entities.HasComponent<Deleted>(entity) == (markersApplied && i < ParentCount), "PREVIEW_CANCEL_TOOL_ENTITY_STATE_CHANGED");
                Require(!entities.HasBuffer<Game.Net.AggregateElement>(entity) || entities.GetBuffer<Game.Net.AggregateElement>(entity, true).Length == 0,
                    "PREVIEW_CANCEL_TOOL_AGGREGATE_REFERENCE_PRESENT");
                Require(!entities.HasComponent<Game.Net.Lane>(entity) && !entities.HasComponent<Game.Vehicles.Vehicle>(entity)
                    && !entities.HasComponent<Game.Creatures.Creature>(entity) && !entities.HasComponent<ServiceUpgrade>(entity)
                    && entities.HasComponent<NativeObject>(entity) == (i == 0 || i >= ParentCount)
                    && entities.HasComponent<Game.Net.Node>(entity) == (i > 0 && i < ParentCount)
                    && entities.HasComponent<Building>(entity) == (i == 0), "PREVIEW_CANCEL_TOOL_NATIVE_ROLE_CHANGED");
                Temp temp = entities.GetComponentData<Temp>(entity);
                TempFlags expected = i == 0 ? TempFlags.Create | TempFlags.Essential : i == 1 ? TempFlags.Create | TempFlags.IsLast : TempFlags.Create;
                Require(captured == null ? temp.m_Original == Entity.Null && temp.m_Flags == expected : temp.Equals(captured[i]),
                    "PREVIEW_CANCEL_TOOL_TEMP_CHANGED");
                if (temp.m_Original != Entity.Null)
                    Require(entities.Exists(temp.m_Original) && !entities.HasComponent<Hidden>(temp.m_Original),
                        "PREVIEW_CANCEL_TOOL_FORMAL_HIDDEN_UNSUPPORTED");

                bool ownerRequired = i >= ParentCount || i == ParentCount - 1;
                Require(entities.HasComponent<Owner>(entity) == ownerRequired, "PREVIEW_CANCEL_TOOL_OWNER_CHANGED");
                if (!ownerRequired) continue;
                Entity owner = entities.GetComponentData<Owner>(entity).m_Owner;
                if (i == ParentCount - 1) Require(owner == graph[0], "PREVIEW_CANCEL_TOOL_OWNER_CHANGED");
                else
                {
                    int slot = Array.IndexOf(graph, owner, 0, ParentCount);
                    Require(slot >= 0 && (temp.m_Flags & TempFlags.Essential) == 0, "PREVIEW_CANCEL_TOOL_CHILD_BRANCH_CHANGED");
                    childCounts[slot]++;
                }
            }
            Require(childCounts[0] == (captured == null ? graph.Length - 3 : 4)
                && childCounts[1] == 1 && (ParentCount == 2 || childCounts[2] == 1),
                "PREVIEW_CANCEL_TOOL_CHILDREN_CHANGED");
        }

        private void RequireBindings()
        {
            EntityQuery[] registered = EntityQueries;
            Require(registered.Length == 1 && registered[0].Equals(query) && query.HasFilter()
                && ((EntityQuery)queryField!.GetValue(this)!).Equals(query)
                && barrier != null && ReferenceEquals(barrierField!.GetValue(this), barrier)
                && barrier.CreationReturned && ReferenceEquals(barrier.World, World), "PREVIEW_CANCEL_TOOL_BINDINGS_CHANGED");
        }

        private void RequireUnpatched()
        {
            var observed = PreviewCancelToolPatchProbe.Observe();
            patchQualified = observed.Qualified;
            methodsChecked = observed.MethodsChecked;
            methodSetSha256 = observed.MethodSetSha256;
            Require(patchQualified, observed.Reason);
        }

        private void Guard(Action requireScope)
        {
            requireScope();
            Require(World != null && World.IsCreated && !destructionStarted, "PREVIEW_CANCEL_TOOL_SOURCE_UNAVAILABLE");
        }

        private static FieldInfo Field(string name, Type type)
        {
            var field = typeof(ToolClearSystem).GetField(name, Fields);
            Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_CANCEL_TOOL_FIELD_ABI_UNQUALIFIED");
            return field!;
        }

        private static void Require(bool condition, string code) => PreviewCancelTool.Require(condition, code);

        internal JObject Snapshot() => new JObject
        {
            ["queryCount"] = queryCount, ["chunkCount"] = chunkCount, ["nativeEntered"] = nativeEntered,
            ["nativeReturned"] = nativeReturned, ["nativeJoined"] = NativeJoined, ["patchQualified"] = patchQualified,
            ["methodsChecked"] = methodsChecked, ["methodSetSha256"] = methodSetSha256,
            ["creationReturned"] = CreationReturned, ["destructionReturned"] = DestructionReturned
        };
    }
}
#endif
