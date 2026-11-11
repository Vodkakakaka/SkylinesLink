#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Disabled = Unity.Entities.Disabled;
using LiveSubObject = Game.Objects.SubObject;
using NativeSubObjectSystem = Game.Objects.SubObjectSystem;

namespace SkylinesLink.Mod.Native
{
    internal sealed class PreviewCancelSubObjects
    {
        internal const int ChildCount = 5;
        private NativeSystemRegistration<PreviewCancelSubObjectProducer>? producer;
        private NativeSystemRegistration<PreviewCancelSubObjectBarrier>? barrier;
        private bool attempted, producerReleased = true, barrierReleased = true, childMarkersConfirmed, protectionConfirmed;
        private int? observedCommands;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => producer?.Instance.NativeWorkMayReadSource == true
            || PlaybackEntered && !PlaybackCallEnded;
        internal bool ResourcesReleased => producerReleased && barrierReleased && !NativeWorkMayReadSource;
        internal bool PlaybackEntered { get; private set; }
        internal bool PlaybackReturned { get; private set; }
        internal bool PlaybackCallEnded { get; private set; }
        internal bool Passed => childMarkersConfirmed && protectionConfirmed && ResourcesReleased && failureCode == null;

        internal void RunOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardMarkedParents, Action observeProtection, int actor = 1, Temp[]? captured = null)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted, "PREVIEW_CANCEL_SUBOBJECT_EXECUTION_REJECTED");
            attempted = true;
            try
            {
                requireScope();
                guardMarkedParents();
                int parents = captured == null ? 2 : 3;
                Require(graph.Length == (captured == null ? ChildCount + 2 : 9)
                    && (captured == null || captured.Length == graph.Length), "PREVIEW_CANCEL_SUBOBJECT_COHORT_SIZE");
                var cohort = (Entity[])graph.Clone();
                var output = new PreviewCancelSubObjectBarrier();
                barrier = new NativeSystemRegistration<PreviewCancelSubObjectBarrier>(source, output);
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                var instance = new PreviewCancelSubObjectProducer();
                instance.Configure(output, actor, captured);
                producer = new NativeSystemRegistration<PreviewCancelSubObjectProducer>(source, instance);
                producerReleased = false;
                Register(producer, appended, requireScope);
                observeProtection();

                instance.Propagate(cohort, requireScope, guardMarkedParents);
                var buffer = output.Borrow(1);
                observedCommands = Inspect(output.Borrow(0), buffer, cohort, parents);
                instance.RequirePlaybackBoundary();
                PlaybackEntered = true;
                try { buffer.Playback(source.EntityManager); PlaybackReturned = true; }
                finally { PlaybackCallEnded = true; }
                instance.RequireApplied();
                childMarkersConfirmed = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejection ? rejection.Code : "PREVIEW_CANCEL_SUBOBJECT_FAILED";
                throw new Rejection(failureCode, error);
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    if (producer != null) producerReleased = Release(producer, appended, producer.Instance.StorageReleased,
                        () => producer.Instance.DestructionReturned);
                    if (barrier != null) barrierReleased = Release(barrier, appended, barrier.Instance.StorageReleased,
                        () => barrier.Instance.DestructionReturned);
                }
            }

            if (!ResourcesReleased) failureCode ??= "PREVIEW_CANCEL_SUBOBJECT_RELEASE_UNCONFIRMED";
            if (failureCode != null) throw new Rejection(failureCode);
            try
            {
                requireScope();
                guardMarkedParents();
                observeProtection();
                protectionConfirmed = true;
            }
            catch (Exception error)
            {
                failureCode = "PREVIEW_CANCEL_SUBOBJECT_PROTECTION_UNCONFIRMED";
                throw new Rejection(failureCode, error);
            }
        }

        private static int Inspect(EntityCommandBuffer first, EntityCommandBuffer second, Entity[] graph, int parents)
        {
            var abi = new ObjectDefinitionCommandProbe.Abi();
            Array Commands(EntityCommandBuffer buffer)
            {
                object view = abi.Constructor.Invoke(new object[] { buffer });
                return (Array)abi.Commands.GetValue(view)!;
            }

            Require(Commands(first).Length == 0, "PREVIEW_CANCEL_SUBOBJECT_UNEXPECTED_PARENT_COMMANDS");
            var commands = Commands(second);
            int children = graph.Length - parents;
            Require(commands.Length == 2 * children, "PREVIEW_CANCEL_SUBOBJECT_COMMAND_COUNT");
            var removeView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic);
            Require(removeView != null, "PREVIEW_CANCEL_SUBOBJECT_COMMAND_ABI");
            var stages = new int[children];
            var sortKeys = new int[children];
            foreach (object command in commands)
            {
                var entity = (Entity)abi.Value(command, "Entity", typeof(Entity));
                int target = Array.IndexOf(graph, entity, parents) - parents;
                int bytes = abi.Int(command, "TotalSizeInBytes");
                int sort = abi.Int(command, "SortKey");
                Require(target >= 0 && target < children && bytes > 0 && bytes <= 2048 && sort >= 0 && sort < parents
                    && abi.Int(command, "BatchCount") == 1 && abi.Int(command, "IdentityIndex") == 0,
                    "PREVIEW_CANCEL_SUBOBJECT_COMMAND_TARGET");
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                if (stages[target] == 0)
                {
                    Require(command.GetType() == removeView && kind == "RemoveMultipleComponents"
                        && PreviewCancelSubObjectProducer.SameMarkers((ComponentTypeSet)abi.Value(command, "TypeSet", typeof(ComponentTypeSet))),
                        "PREVIEW_CANCEL_SUBOBJECT_REMOVE_COMMAND");
                    sortKeys[target] = sort;
                }
                else
                {
                    Require(stages[target] == 1 && sort == sortKeys[target] && command.GetType() == abi.Component && kind == "AddComponent"
                        && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == TypeManager.GetTypeIndex<Deleted>()
                        && (short)abi.Value(command, "ComponentSize", typeof(short)) == 0
                        && abi.Value(command, "ComponentValue", typeof(object)) == null
                        && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)),
                        "PREVIEW_CANCEL_SUBOBJECT_ADD_COMMAND");
                }
                stages[target]++;
            }

            foreach (int stage in stages) Require(stage == 2, "PREVIEW_CANCEL_SUBOBJECT_COMMAND_SET_INCOMPLETE");
            return commands.Length;
        }

        private static void Register<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            Action requireScope) where T : ComponentSystemBase
        {
            registration.Add();
            appended.Add(registration.Instance);
            requireScope();
        }

        private bool Release<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            bool storageBeforeDestroy, Func<bool> destructionReturned) where T : ComponentSystemBase
        {
            try
            {
                registration.Destroy();
                bool released = registration.PhysicalReleased
                    && (registration.DestroyAttempted ? registration.DestroyReturned && destructionReturned() : storageBeforeDestroy);
                if (released) appended.Remove(registration.Instance);
                else failureCode ??= "PREVIEW_CANCEL_SUBOBJECT_RELEASE_UNCONFIRMED";
                return released;
            }
            catch (Exception)
            {
                failureCode ??= "PREVIEW_CANCEL_SUBOBJECT_DESTROY_FAILED";
                return false;
            }
        }

        internal JObject Snapshot() => new JObject
        {
            ["passed"] = Passed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["playbackEntered"] = PlaybackEntered, ["playbackReturned"] = PlaybackReturned,
            ["playbackCallEnded"] = PlaybackCallEnded, ["childMarkersConfirmed"] = childMarkersConfirmed,
            ["commandsObserved"] = observedCommands, ["producerReleased"] = producerReleased, ["barrierReleased"] = barrierReleased,
            ["producer"] = producer?.Instance.Snapshot(), ["failureCode"] = failureCode
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
    public sealed partial class PreviewCancelSubObjectProducer : NativeSubObjectSystem
    {
        private int actor;
        private Temp[]? captured;
        private int ParentCount => captured == null ? 2 : 3;
        private int MemberCount => captured == null ? PreviewCancelSubObjects.ChildCount + 2 : 9;
        private static readonly ComponentTypeSet Markers = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(),
            ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>());
        private static readonly string[] BindingNames = { "m_TerrainSystem", "m_WaterSystem", "m_ToolSystem", "m_CityConfigurationSystem",
            "m_ModificationBarrier", "m_UpdateQuery", "m_TempQuery", "m_ContainerQuery", "m_BuildingSettingsQuery",
            "m_HappinessParameterQuery", "m_AppliedTypes", "m_LoopErrorPrefabs" };

        private PreviewCancelSubObjectBarrier? output;
        private FieldInfo[]? fields;
        private object[]? values;
        private TerrainSystem? terrain;
        private WaterSystem? water;
        private ToolSystem? tools;
        private CityConfigurationSystem? city;
        private EntityQuery updates, temps, containers, buildings, happiness;
        private NativeQueue<Entity> loopErrors;
        private bool allocationAttempted, allocationReturned, queueReleaseAttempted, queueReleased;
        private bool creationReturned, nativeEntered, nativeReturned, nativeJoined;
        private int? ownerCount, tempCount;
        private Entity[]? cohort;
        private Temp[]? before;
        private Action? requireScope, guardMarkedParents;
        private int? methodsChecked, patchedMethods;

        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal bool StorageReleased => !allocationAttempted || queueReleased;

        internal void Configure(PreviewCancelSubObjectBarrier barrier, int actor, Temp[]? captured)
        {
            Require(output == null && World == null && actor > 0, "PREVIEW_CANCEL_SUBOBJECT_CONFIGURE_REJECTED");
            output = barrier ?? throw new ArgumentNullException(nameof(barrier));
            this.actor = actor;
            this.captured = captured;
        }

        protected override void OnCreate()
        {
            Enabled = false;
            Require(MainThreadDispatcher.isMainThread && output?.CreationReturned == true
                && ReferenceEquals(output.World, World), "PREVIEW_CANCEL_SUBOBJECT_CONFIGURATION_MISSING");
            try
            {
                terrain = World.GetExistingSystemManaged<TerrainSystem>();
                water = World.GetExistingSystemManaged<WaterSystem>();
                tools = World.GetExistingSystemManaged<ToolSystem>();
                city = World.GetExistingSystemManaged<CityConfigurationSystem>();
                Require(terrain != null && water != null && tools != null && city != null, "PREVIEW_CANCEL_SUBOBJECT_PROVIDERS_MISSING");

                // Select the native Deleted owner branch. Updated-only owners
                // and event paths belong to generation, not to this step.
                updates = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<LiveSubObject>(), ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                temps = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Owner>(),
                        ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>() }, Options = EntityQueryOptions.IncludeDisabledEntities
                });
                var filter = new ObjectGenerationScope { Value = actor };
                updates.SetSharedComponentFilter(filter);
                temps.SetSharedComponentFilter(filter);
                containers = GetEntityQuery(ComponentType.ReadOnly<EditorContainerData>(), ComponentType.ReadOnly<ObjectData>());
                buildings = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
                happiness = GetEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>());
                RequireForUpdate(updates);

                Require(typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6"
                    && typeof(NativeQueue<Entity>).Module.ModuleVersionId.ToString("N") == "f4ddbc30cb47406191074a184ca0481e",
                    "PREVIEW_CANCEL_SUBOBJECT_NATIVE_BUILD_UNQUALIFIED");
                allocationAttempted = true;
                loopErrors = new NativeQueue<Entity>(Allocator.Persistent);
                allocationReturned = true;
                values = new object[] { terrain!, water!, tools!, city!, output!, updates, temps, containers, buildings, happiness, Markers, loopErrors };
                var types = new[] { typeof(TerrainSystem), typeof(WaterSystem), typeof(ToolSystem), typeof(CityConfigurationSystem),
                    typeof(ModificationBarrier2B), typeof(EntityQuery), typeof(EntityQuery), typeof(EntityQuery), typeof(EntityQuery),
                    typeof(EntityQuery), typeof(ComponentTypeSet), typeof(NativeQueue<Entity>) };
                fields = new FieldInfo[BindingNames.Length];
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = typeof(NativeSubObjectSystem).GetField(BindingNames[i],
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    Require(field != null && field.FieldType == types[i] && !field.IsInitOnly, "PREVIEW_CANCEL_SUBOBJECT_FIELD_ABI");
                    fields[i] = field!;
                    field!.SetValue(this, values[i]);
                }
                creationReturned = true;
            }
            catch
            {
                // Registration rollback cannot retire this standalone queue.
                if (allocationReturned && !queueReleaseAttempted)
                    try { ReleaseQueue(); } catch { /* Retain the unconfirmed receipt. */ }
                throw;
            }
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        internal void Propagate(Entity[] graph, Action scope, Action markedParents)
        {
            Require(creationReturned && cohort == null && graph.Length == MemberCount,
                "PREVIEW_CANCEL_SUBOBJECT_NATIVE_REPEATED");
            cohort = graph;
            requireScope = scope;
            guardMarkedParents = markedParents;
            before = ReadGraph(applied: false);
            Require(ShouldRunSystem(), "PREVIEW_CANCEL_SUBOBJECT_REQUIRED_QUERY");
            Enabled = true;
            try
            {
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_CANCEL_SUBOBJECT_NATIVE_NOT_RETURNED");
                JobHandle dependency = Dependency;
                Require(!dependency.Equals(default(JobHandle)), "PREVIEW_CANCEL_SUBOBJECT_NATIVE_DEPENDENCY_MISSING");
                dependency.Complete();
                nativeJoined = true;
                CheckScope();
                Require(output!.PendingCount == 2 && loopErrors.IsEmpty(), "PREVIEW_CANCEL_SUBOBJECT_NATIVE_OUTPUT_UNCONFIRMED");
            }
            finally { Enabled = false; }
        }

        protected override void OnUpdate()
        {
            Require(!nativeEntered && before != null, "PREVIEW_CANCEL_SUBOBJECT_NATIVE_NOT_ARMED");
            var patch = SubObjectGenerationPatchProbe.Observe();
            methodsChecked = patch.MethodsChecked;
            patchedMethods = patch.PatchedMethods;
            Require(patch.Qualified, "PREVIEW_CANCEL_SUBOBJECT_PATCH_UNQUALIFIED");
            RequireBinding();
            RequirePlaybackBoundary();
            ReadQueryMembers();
            Require(output!.PendingCount == 0 && loopErrors.IsEmpty(), "PREVIEW_CANCEL_SUBOBJECT_PRIOR_OUTPUT");
            CheckScope();
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        internal void RequirePlaybackBoundary() => CompareBefore(ReadGraph(applied: false));
        internal void RequireApplied() => CompareBefore(ReadGraph(applied: true));

        private Temp[] ReadGraph(bool applied)
        {
            CheckScope();
            var graph = cohort!;
            var entities = EntityManager;
            var state = new Temp[graph.Length];
            for (int i = 0; i < graph.Length; i++)
            {
                var entity = graph[i];
                Require(entity != Entity.Null && Array.IndexOf(graph, entity) == i && entities.Exists(entity)
                    && entities.HasComponent<Disabled>(entity) && entities.HasComponent<ObjectGenerationScope>(entity)
                    && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor && entities.HasComponent<Temp>(entity)
                    && entities.HasComponent<PrefabRef>(entity), "PREVIEW_CANCEL_SUBOBJECT_COHORT_CHANGED");
                var prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab),
                    "PREVIEW_CANCEL_SUBOBJECT_PREFAB_UNAVAILABLE");
                state[i] = entities.GetComponentData<Temp>(entity);
                Require((captured == null ? state[i].m_Original == Entity.Null : state[i].Equals(captured[i]))
                    && entities.HasComponent<Deleted>(entity) == (i < ParentCount || applied),
                    "PREVIEW_CANCEL_SUBOBJECT_MARKERS_CHANGED");
                if (i < ParentCount) continue;
                Require(entities.HasComponent<Game.Objects.Object>(entity) && entities.HasComponent<Owner>(entity)
                    && Array.IndexOf(graph, entities.GetComponentData<Owner>(entity).m_Owner, 0, ParentCount) >= 0
                    && !entities.HasComponent<Game.Objects.Secondary>(entity) && !entities.HasComponent<Game.Buildings.Building>(entity)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(entity)
                    && (!entities.HasComponent<Game.Objects.Attached>(entity)
                        || entities.GetComponentData<Game.Objects.Attached>(entity).m_Parent == Entity.Null)
                    && (!entities.HasBuffer<LiveSubObject>(entity) || entities.GetBuffer<LiveSubObject>(entity, true).Length == 0),
                    "PREVIEW_CANCEL_SUBOBJECT_LEAF_NOT_CLOSED");
                if (applied)
                    Require(!entities.HasComponent<Applied>(entity) && !entities.HasComponent<Created>(entity)
                        && !entities.HasComponent<Updated>(entity), "PREVIEW_CANCEL_SUBOBJECT_MARKERS_NOT_APPLIED");
            }

            Require(entities.HasComponent<Game.Buildings.Building>(graph[0]) && entities.HasComponent<Game.Net.Node>(graph[1])
                && !entities.HasComponent<Game.Objects.Object>(graph[1]), "PREVIEW_CANCEL_SUBOBJECT_PARENT_ROLE_CHANGED");
            RequireRows(entities, graph[0], graph, ParentCount, 4);
            RequireRows(entities, graph[1], graph, ParentCount, 1);
            if (ParentCount == 3)
            {
                Require(entities.HasComponent<Game.Net.Node>(graph[2]) && !entities.HasComponent<Game.Objects.Object>(graph[2]),
                    "PREVIEW_CANCEL_SUBOBJECT_PARENT_ROLE_CHANGED");
                RequireRows(entities, graph[2], graph, ParentCount, 1);
            }
            CheckScope();
            return state;
        }

        private static void RequireRows(EntityManager entities, Entity owner, Entity[] graph, int first, int count)
        {
            Require(entities.HasBuffer<LiveSubObject>(owner), "PREVIEW_CANCEL_SUBOBJECT_PARENT_BUFFER_MISSING");
            var rows = entities.GetBuffer<LiveSubObject>(owner, true);
            Require(rows.Length == count, "PREVIEW_CANCEL_SUBOBJECT_PARENT_ROWS_CHANGED");
            int seen = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                Entity child = rows[i].m_SubObject;
                int index = Array.IndexOf(graph, child, first) - first;
                Require(index >= 0 && (seen & (1 << index)) == 0
                    && entities.GetComponentData<Owner>(child).m_Owner == owner,
                    "PREVIEW_CANCEL_SUBOBJECT_PARENT_ROWS_CHANGED");
                seen |= 1 << index;
            }
        }

        private void CompareBefore(Temp[] current)
        {
            for (int i = 0; i < current.Length; i++)
                Require(current[i].Equals(before![i]), "PREVIEW_CANCEL_SUBOBJECT_TEMP_CHANGED");
        }

        private void ReadQueryMembers()
        {
            var graph = cohort!;
            ownerCount = updates.CalculateEntityCount();
            tempCount = temps.CalculateEntityCount();
            Require(ownerCount == ParentCount && tempCount >= 0 && tempCount <= MemberCount - ParentCount
                && buildings.CalculateEntityCount() == 1 && happiness.CalculateEntityCount() == 1,
                "PREVIEW_CANCEL_SUBOBJECT_QUERY_COUNT");
            using (var owners = updates.ToEntityArray(Allocator.Temp))
            {
                Require(owners.Length == ParentCount && updates.CalculateChunkCount() == ParentCount,
                    "PREVIEW_CANCEL_SUBOBJECT_QUERY_OWNER_CHUNKS");
                var seen = new HashSet<Entity>();
                foreach (Entity owner in owners)
                    Require(Array.IndexOf(graph, owner, 0, ParentCount) >= 0 && seen.Add(owner)
                        && EntityManager.GetChunk(owner).Count == 1, "PREVIEW_CANCEL_SUBOBJECT_QUERY_OWNERS");
            }
            using (var ignored = temps.ToEntityArray(Allocator.Temp))
            {
                Require(ignored.Length == tempCount, "PREVIEW_CANCEL_SUBOBJECT_QUERY_IGNORES");
                foreach (var entity in ignored)
                    Require(Array.IndexOf(graph, entity, ParentCount) >= ParentCount, "PREVIEW_CANCEL_SUBOBJECT_QUERY_IGNORES");
            }
        }

        private void RequireBinding()
        {
            Require(fields != null && values != null && loopErrors.IsCreated && EntityQueries.Length == 5
                && updates.HasFilter() && temps.HasFilter() && !containers.HasFilter() && !buildings.HasFilter() && !happiness.HasFilter()
                && tools!.actionMode == Game.GameMode.Game && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && ReferenceEquals(World.GetExistingSystemManaged<TerrainSystem>(), terrain)
                && ReferenceEquals(World.GetExistingSystemManaged<WaterSystem>(), water)
                && ReferenceEquals(World.GetExistingSystemManaged<CityConfigurationSystem>(), city),
                "PREVIEW_CANCEL_SUBOBJECT_BINDINGS_CHANGED");
            for (int i = 0; i < fields!.Length; i++)
                Require(Equals(fields[i].GetValue(this), values![i]), "PREVIEW_CANCEL_SUBOBJECT_BINDINGS_CHANGED");
        }

        private void CheckScope()
        {
            requireScope!();
            Require(creationReturned && World != null && World.IsCreated && ReferenceEquals(output!.World, World),
                "PREVIEW_CANCEL_SUBOBJECT_SOURCE_UNAVAILABLE");
            guardMarkedParents!();
            requireScope();
        }

        protected override void OnDestroy()
        {
            Require(!NativeWorkMayReadSource && (!allocationAttempted || allocationReturned)
                && (!queueReleaseAttempted || queueReleased), "PREVIEW_CANCEL_SUBOBJECT_STORAGE_UNRESOLVED");
            if (allocationReturned && !queueReleaseAttempted) ReleaseQueue();
            DestructionReturned = true;
        }

        private void ReleaseQueue()
        {
            queueReleaseAttempted = true;
            loopErrors.Dispose();
            queueReleased = true;
        }

        internal static bool SameMarkers(ComponentTypeSet actual)
        {
            if (actual.Length != Markers.Length) return false;
            for (int i = 0; i < actual.Length; i++)
                if (actual.GetTypeIndex(i) != Markers.GetTypeIndex(i)) return false;
            return true;
        }

        internal JObject Snapshot() => new JObject
        {
            ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
            ["ownerCount"] = ownerCount, ["tempCount"] = tempCount,
            ["methodsChecked"] = methodsChecked, ["patchedMethods"] = patchedMethods,
            ["storageReleased"] = StorageReleased, ["destructionReturned"] = DestructionReturned
        };

        private static void Require(bool condition, string code) => PreviewCancelSubObjects.Require(condition, code);
    }

    [DisableAutoCreation]
    public sealed partial class PreviewCancelSubObjectBarrier : ModificationBarrier2B
    {
        private bool creationStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_CANCEL_SUBOBJECT_BARRIER_NOT_SCHEDULED");

        internal EntityCommandBuffer Borrow(int index)
        {
            PreviewCancelSubObjects.Require(CreationReturned && !DestructionReturned && PendingCount == 2
                && index >= 0 && index < 2, "PREVIEW_CANCEL_SUBOBJECT_BUFFER_UNAVAILABLE");
            var buffer = PendingBuffers[index];
            PreviewCancelSubObjects.Require(buffer.IsCreated, "PREVIEW_CANCEL_SUBOBJECT_BUFFER_UNAVAILABLE");
            return buffer;
        }

        protected override void OnDestroy()
        {
            PreviewCancelSubObjects.Require(!creationStarted || CreationReturned, "PREVIEW_CANCEL_SUBOBJECT_BARRIER_CREATION_UNRESOLVED");
            if (CreationReturned) base.OnDestroy();
            DestructionReturned = true;
        }
    }
}
#endif
