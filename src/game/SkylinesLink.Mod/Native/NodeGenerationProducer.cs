#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    /// <summary>One source-bound, unplayed single-point node generation.</summary>
    [DisableAutoCreation]
    public sealed partial class NodeGenerationProducer : GenerateNodesSystem
    {
        private int actor;
        private bool configured, emptyUpdateReturned, armed, requested, nativeStarted, nativeReturned, completed, joinFailed;
        private bool queriesRegistered, bindingsVerified, filtersPresent, ownerKeyAbsent;
        private int definitionCount, deletedCount, mapCount, nodeTypeCount, registeredQueryCount;
        private EntityQuery definitions, deleted;
        private ToolSystem? tools;
        private Game.Net.SearchSystem? search;
        private TerrainSystem? terrain;
        private NodeGenerationBarrier? output;
        private ObjectGenerationProducer? objects;
        private Bindings? fields;
        private ObjectDefinitionPayload? admittedPayload;
        private RelocationDefinitionPayload? admittedRelocation;
        private Action? requireRelocationAtEntry;
        private bool relocationConfirmed;
        private EntityArchetype admittedArchetype;
        private int commandUpperBound;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeWorkMayReadSource => nativeStarted && !completed;
        // This class owns no persistent native allocation outside SystemState's
        // registered queries. The borrowed object map belongs to its producer.
        internal bool StorageReleased => true;
        internal bool ResourcesReleased => DestructionReturned;
        internal int OutputCommandUpperBound => commandUpperBound;
        internal EntityArchetype Archetype => admittedArchetype;
        internal bool ArchetypeMatches(EntityArchetype value) => admittedArchetype.Equals(value);
        internal bool CompletedFor(ObjectDefinitionPayload payload) => nativeReturned && completed && !DestructionStarted
            && ReferenceEquals(admittedPayload, payload) && commandUpperBound == 14 + (payload.CourseCreation.m_SubPrefab != Entity.Null ? 1 : 0);

        internal void Configure(int actor, NodeGenerationBarrier barrier, ObjectGenerationProducer objectProducer)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0 && barrier != null && objectProducer != null,
                "NODE_GENERATION_CONFIGURATION_REJECTED");
            fields = new Bindings();
            this.actor = actor; output = barrier; objects = objectProducer; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            var barrier = output ?? throw Failure("NODE_GENERATION_CONFIGURATION_MISSING");
            var objectProducer = objects ?? throw Failure("NODE_GENERATION_CONFIGURATION_MISSING");
            var bound = fields ?? throw Failure("NODE_GENERATION_CONFIGURATION_MISSING");
            Require(configured && barrier.CreationReturned && ReferenceEquals(barrier.World, World)
                && objectProducer.CreationReturned && !objectProducer.DestructionStarted
                && objectProducer.OwnerMapCreated && ReferenceEquals(objectProducer.World, World),
                "NODE_GENERATION_CONFIGURATION_MISSING");
            tools = World.GetExistingSystemManaged<ToolSystem>() ?? throw Failure("NODE_GENERATION_TOOL_UNAVAILABLE");
            search = World.GetExistingSystemManaged<Game.Net.SearchSystem>() ?? throw Failure("NODE_GENERATION_SEARCH_UNAVAILABLE");
            terrain = World.GetExistingSystemManaged<TerrainSystem>() ?? throw Failure("NODE_GENERATION_TERRAIN_UNAVAILABLE");
            Require(tools.actionMode == GameMode.Game, "NODE_GENERATION_NORMAL_MODE_REQUIRED");
            definitions = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<ObjectDefinition>(), ComponentType.ReadOnly<NetCourse>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            deleted = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() }, Options = EntityQueryOptions.IncludeDisabledEntities });
            var scope = new ObjectGenerationScope { Value = actor };
            definitions.SetSharedComponentFilter(scope); deleted.SetSharedComponentFilter(scope);
            bound.Tool.SetValue(this, tools); bound.Search.SetValue(this, search); bound.Terrain.SetValue(this, terrain);
            bound.Objects.SetValue(this, objectProducer); bound.Barrier.SetValue(this, barrier);
            bound.Definitions.SetValue(this, definitions); bound.Deleted.SetValue(this, deleted);
            RequireForUpdate(definitions);
            ObserveBindings();
            CreationReturned = true;
        }

        // The native compiler hook is inherited unchanged. These unmatched Game
        // lifecycle callbacks must not register or unsubscribe its global state.
        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate()
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && !emptyUpdateReturned, "NODE_GENERATION_EMPTY_PROBE_REJECTED");
            RequireEmpty();
            Require(!ShouldRunSystem(), "NODE_GENERATION_EMPTY_GATE_UNEXPECTED");
            Enabled = true;
            try { Update(); emptyUpdateReturned = true; }
            finally { Enabled = false; }
            RequireEmpty();
            Require(OnUpdateEntries == 0 && output!.PendingCount == 0, "NODE_GENERATION_EMPTY_ENTERED_KERNEL");
            ObserveBindings();
        }

        internal void GenerateOnce(ObjectDefinitionPayload payload, ObjectDefinitionEntities input, ObjectGenerationPreflight admission)
        {
            RequireMainThread();
            if (payload == null || input == null || admission == null) throw Failure("NODE_GENERATION_ADMISSION_UNQUALIFIED");
            var barrier = output ?? throw Failure("NODE_GENERATION_NOT_CREATED");
            var objectProducer = objects ?? throw Failure("NODE_GENERATION_NOT_CREATED");
            var sourceTools = tools ?? throw Failure("NODE_GENERATION_NOT_CREATED");
            Require(CreationReturned && !DestructionStarted && emptyUpdateReturned && !requested && input.CreateReturned
                && ReferenceEquals(payload.Source, World) && admission.Matches(World, payload) && sourceTools.actionMode == GameMode.Game
                && barrier.PendingCount == 0 && objectProducer.CompletedFor(payload) && objectProducer.OwnerMapCreated
                && ReferenceEquals(objectProducer.World, World), "NODE_GENERATION_ADMISSION_UNQUALIFIED");
            ObserveBindings();
            Require(definitionCount == 2 && deleted.IsEmptyIgnoreFilter && deletedCount == 0,
                "NODE_GENERATION_INPUT_COUNT_CHANGED");
            RequirePrepared(input.Root, payload.RootCreation); RequirePrepared(input.Course, payload.CourseCreation);
            Require(input.Root != input.Course && EntityManager.GetComponentData<ObjectDefinition>(input.Root).Equals(payload.RootObject)
                && EntityManager.GetComponentData<OwnerDefinition>(input.Course).Equals(payload.CourseOwner)
                && EntityManager.GetComponentData<NetCourse>(input.Course).Equals(payload.Course)
                && !EntityManager.HasComponent<OwnerDefinition>(input.Root) && !EntityManager.HasComponent<NetCourse>(input.Root)
                && !EntityManager.HasComponent<ObjectDefinition>(input.Course)
                && !EntityManager.HasComponent<Upgraded>(input.Course) && !EntityManager.HasComponent<LocalCurveCache>(input.Course),
                "NODE_GENERATION_INPUT_VALUES_CHANGED");
            Require(payload.RootCreation.m_Flags == 0 && payload.CourseCreation.m_Flags == 0
                && payload.RootCreation.m_Original == Entity.Null && payload.CourseCreation.m_Original == Entity.Null
                && payload.CourseCreation.m_Owner == Entity.Null && payload.CourseOwner.m_Prefab == payload.RootCreation.m_Prefab
                && payload.Course.m_StartPosition.m_Entity == Entity.Null && payload.Course.m_EndPosition.m_Entity == Entity.Null
                && payload.Course.m_StartPosition.m_Position.Equals(payload.Course.m_EndPosition.m_Position)
                && ((payload.Course.m_StartPosition.m_Flags | payload.Course.m_EndPosition.m_Flags) & CoursePosFlags.DontCreate) == 0,
                "NODE_GENERATION_SINGLE_POINT_REQUIRED");
            RequirePrefab(payload.CourseCreation.m_Prefab);
            if (payload.CourseCreation.m_SubPrefab != Entity.Null) RequirePrefab(payload.CourseCreation.m_SubPrefab);
            Require(EntityManager.HasComponent<NetData>(payload.CourseCreation.m_Prefab), "NODE_GENERATION_NET_PREFAB_UNAVAILABLE");
            EntityArchetype archetype = EntityManager.GetComponentData<NetData>(payload.CourseCreation.m_Prefab).m_NodeArchetype;
            Require(archetype.Valid, "NODE_GENERATION_NODE_ARCHETYPE_UNAVAILABLE");
            nodeTypeCount = archetype.TypesCount;
            Require(nodeTypeCount > 0 && nodeTypeCount <= 256, "NODE_GENERATION_NODE_ARCHETYPE_BUDGET");

            // Join the actual object-map writer, inspect only the one owner key,
            // and never replace this map with a guessed or copied dictionary.
            NativeHashMap<OwnerDefinition, Entity> map = objectProducer.GetReusedOwnerMap(out JobHandle dependencies);
            dependencies.Complete();
            Require(map.IsCreated, "NODE_GENERATION_OWNER_MAP_UNAVAILABLE");
            mapCount = map.Count; ownerKeyAbsent = !map.ContainsKey(payload.CourseOwner);
            Require(ownerKeyAbsent, "NODE_GENERATION_OWNER_ALREADY_REUSED");
            // The zero-hit admission has just been run on this main-thread
            // source. No yield, provider callback, or tree writer is invoked.
            admittedPayload = payload; admittedArchetype = archetype;
            commandUpperBound = 14 + (payload.CourseCreation.m_SubPrefab != Entity.Null ? 1 : 0);
            RunNativeGeneration();
        }

        internal void GenerateRelocationOnce(RelocationDefinitionPayload payload, RelocationDefinitionEntities input,
            RelocationGenerationAdmission admission)
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && emptyUpdateReturned && !requested
                && output != null && output.PendingCount == 0, "NODE_RELOCATION_GENERATION_NOT_READY");
            RequireRelocationInputs(payload, input, admission);
            admittedRelocation = payload;
            admittedArchetype = admission.NodeArchetype;
            commandUpperBound = admission.NodeCommandUpperBound;

            RunNativeGeneration(() => RequireRelocationInputs(payload, input, admission));
            RequireRelocationInputs(payload, input, admission);
            relocationConfirmed = true;
        }

        private void RequireRelocationInputs(RelocationDefinitionPayload payload, RelocationDefinitionEntities input,
            RelocationGenerationAdmission admission)
        {
            input.RequireCurrent(payload, actor);
            Require(ReferenceEquals(payload.Source, World) && admission.Matches(World, payload)
                && input.CreateReturned && admission.NodeCommandUpperBound == 48 && admission.NodeArchetype.Valid,
                "NODE_RELOCATION_ADMISSION_CHANGED");
            ObserveBindings();
            Require(tools != null && tools.actionMode == GameMode.Game
                && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && search != null && ReferenceEquals(World.GetExistingSystemManaged<Game.Net.SearchSystem>(), search)
                && terrain != null && ReferenceEquals(World.GetExistingSystemManaged<TerrainSystem>(), terrain)
                && output != null && ReferenceEquals(output.World, World)
                && ReferenceEquals(World.GetExistingSystemManaged<NodeGenerationBarrier>(), output)
                && objects != null && ReferenceEquals(objects.World, World)
                && ReferenceEquals(World.GetExistingSystemManaged<ObjectGenerationProducer>(), objects)
                && ObjectGenerationProducer.HasRelocationMembers(definitions, input)
                && deleted.IsEmptyIgnoreFilter && deletedCount == 0, "NODE_RELOCATION_BINDINGS_OR_QUERY_CHANGED");

            Entity prefab = payload.Original.NodePrefab;
            Require(EntityManager.HasComponent<NetData>(prefab)
                && EntityManager.GetComponentData<NetData>(prefab).m_NodeArchetype.Equals(admission.NodeArchetype),
                "NODE_RELOCATION_ARCHETYPE_CHANGED");
            nodeTypeCount = admission.NodeArchetype.TypesCount;
            Require(nodeTypeCount > 0 && nodeTypeCount <= 256, "NODE_RELOCATION_NODE_ARCHETYPE_BUDGET");

            // The original object producer remains the map owner. Only the
            // joined, exact same payload and its three definitions qualify.
            objects!.RequireRelocationOwnerMapEmpty(payload, input);
            mapCount = 0;
            ownerKeyAbsent = true;
        }

        private void RunNativeGeneration(Action? requireAtEntry = null)
        {
            var barrier = output ?? throw Failure("NODE_GENERATION_NOT_CREATED");
            requireRelocationAtEntry = requireAtEntry;
            requested = true; armed = true; Enabled = true;
            try
            {
                Update();
                Require(nativeReturned, "NODE_GENERATION_NATIVE_NOT_RETURNED");
                JoinOwnedDependencies();
                Require(barrier.PendingCount == 1, "NODE_GENERATION_BUFFER_NOT_CONFIRMED");
                ObserveBindings();
            }
            finally { armed = false; Enabled = false; requireRelocationAtEntry = null; }
        }

        private void RequirePrepared(Entity entity, CreationDefinition expected)
        {
            Require(entity != Entity.Null && EntityManager.Exists(entity) && EntityManager.HasComponent<Unity.Entities.Disabled>(entity)
                && EntityManager.HasComponent<Updated>(entity) && !EntityManager.HasComponent<Temp>(entity)
                && EntityManager.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor
                && EntityManager.GetComponentData<CreationDefinition>(entity).Equals(expected), "NODE_GENERATION_PREPARED_INPUT_CHANGED");
        }
        private void RequirePrefab(Entity prefab) => Require(prefab != Entity.Null && prefab.Index >= 0 && EntityManager.Exists(prefab)
            && EntityManager.HasComponent<PrefabData>(prefab) && EntityManager.IsComponentEnabled<PrefabData>(prefab)
            && !EntityManager.HasComponent<Deleted>(prefab), "NODE_GENERATION_PREFAB_UNAVAILABLE");
        private void RequireEmpty()
        {
            Require(definitions.IsEmptyIgnoreFilter && deleted.IsEmptyIgnoreFilter && definitions.CalculateEntityCount() == 0
                && deleted.CalculateEntityCount() == 0, "NODE_GENERATION_INPUT_NOT_EMPTY");
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Require(armed && !nativeStarted && tools!.actionMode == GameMode.Game, "NODE_GENERATION_NOT_ARMED");
            requireRelocationAtEntry?.Invoke();
            nativeStarted = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private void JoinOwnedDependencies()
        {
            Require(!joinFailed && (!nativeStarted || nativeReturned), "NODE_GENERATION_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (nativeReturned) completed = true; }
            catch { joinFailed = true; throw; }
        }

        internal EntityCommandBuffer BorrowCompletedForInspection(ObjectDefinitionPayload payload)
        {
            RequireMainThread();
            var barrier = output ?? throw Failure("NODE_GENERATION_INSPECTION_NOT_READY");
            Require(CompletedFor(payload) && barrier.PendingCount == 1, "NODE_GENERATION_INSPECTION_NOT_READY");
            return barrier.BorrowSingle();
        }

        internal EntityCommandBuffer BorrowRelocationCommands(RelocationDefinitionPayload payload)
        {
            RequireMainThread();
            Require(relocationConfirmed && nativeReturned && completed && !DestructionStarted
                && ReferenceEquals(admittedRelocation, payload), "NODE_RELOCATION_INSPECTION_NOT_READY");
            return output!.BorrowSingle();
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource, "NODE_GENERATION_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // No owned persistent map; SystemState disposes both registered
            // queries. The external object-map owner is released by the root.
            DestructionReturned = true;
        }

        private void ObserveBindings()
        {
            RequireMainThread();
            var bound = fields ?? throw Failure("NODE_GENERATION_BINDINGS_UNAVAILABLE");
            Require(!NativeWorkMayReadSource && !DestructionStarted, "NODE_GENERATION_OBSERVATION_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)bound.Definitions.GetValue(this)!).Equals(definitions)
                && ((EntityQuery)bound.Deleted.GetValue(this)!).Equals(deleted)
                && ReferenceEquals(bound.Barrier.GetValue(this), output) && ReferenceEquals(bound.Objects.GetValue(this), objects)
                && ReferenceEquals(bound.Tool.GetValue(this), tools) && ReferenceEquals(bound.Search.GetValue(this), search)
                && ReferenceEquals(bound.Terrain.GetValue(this), terrain);
            EntityQuery[] registered = EntityQueries;
            registeredQueryCount = registered.Length;
            queriesRegistered = registeredQueryCount == 2 && Contains(registered, definitions) && Contains(registered, deleted);
            definitionCount = definitions.CalculateEntityCount(); deletedCount = deleted.CalculateEntityCount();
            filtersPresent = definitions.HasFilter() && deleted.HasFilter();
            Require(bindingsVerified && queriesRegistered && filtersPresent,
                "NODE_GENERATION_BINDINGS_CHANGED");
        }

        internal JObject Snapshot() => new JObject {
            ["actor"] = actor, ["configured"] = configured, ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned, ["resourcesReleased"] = ResourcesReleased,
            ["emptyUpdateReturned"] = emptyUpdateReturned, ["onUpdateEntries"] = OnUpdateEntries,
            ["bindingsVerified"] = bindingsVerified, ["registeredQueryCount"] = registeredQueryCount,
            ["queriesRegistered"] = queriesRegistered, ["filtersPresent"] = filtersPresent,
            ["definitionCount"] = definitionCount, ["deletedCount"] = deletedCount,
            ["ownerMapCount"] = mapCount, ["ownerKeyAbsent"] = ownerKeyAbsent, ["nodeArchetypeTypesCount"] = nodeTypeCount,
            ["nativeCallStarted"] = nativeStarted, ["nativeCallReturned"] = nativeReturned, ["completionObserved"] = completed,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["outputCommandUpperBound"] = commandUpperBound, ["nativeTransientCleanupScheduled"] = nativeReturned,
            ["nativeTransientReclamationObserved"] = false, ["playbackCalled"] = false,
            ["scope"] = admittedRelocation == null
                ? "Unplayed single-point node commands; external object map remains owned by its producer."
                : "Unplayed old and new node commands; the original node is unchanged and the paired object producer owns the map."
        };

        private static bool Contains(EntityQuery[] values, EntityQuery expected)
        { foreach (EntityQuery value in values) if (value.Equals(expected)) return true; return false; }
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NODE_GENERATION_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static InvalidOperationException Failure(string code) => new InvalidOperationException(code);

        private sealed class Bindings
        {
            internal readonly FieldInfo Tool, Search, Terrain, Objects, Barrier, Definitions, Deleted;
            internal Bindings()
            {
                Require(typeof(GenerateNodesSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6"
                    && typeof(NativeHashMap<OwnerDefinition, Entity>).Module.ModuleVersionId.ToString("N") == "f4ddbc30cb47406191074a184ca0481e",
                    "NODE_GENERATION_NATIVE_BUILD_UNQUALIFIED");
                Tool = Field("m_ToolSystem", typeof(ToolSystem)); Search = Field("m_SearchSystem", typeof(Game.Net.SearchSystem));
                Terrain = Field("m_TerrainSystem", typeof(TerrainSystem)); Objects = Field("m_GenerateObjectsSystem", typeof(GenerateObjectsSystem));
                Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier1));
                Definitions = Field("m_DefinitionQuery", typeof(EntityQuery)); Deleted = Field("m_DeletedQuery", typeof(EntityQuery));
            }
            private static FieldInfo Field(string name, Type expected)
            {
                FieldInfo? field = typeof(GenerateNodesSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(field != null && field.DeclaringType == typeof(GenerateNodesSystem) && field.FieldType == expected
                    && !field.IsStatic && !field.IsInitOnly, "NODE_GENERATION_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class NodeGenerationBarrier : ModificationBarrier1
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("NODE_GENERATION_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("NODE_GENERATION_BARRIER_CANNOT_PLAYBACK");
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("NODE_GENERATION_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
        internal EntityCommandBuffer BorrowSingle()
        {
            if (!CreationReturned || DestructionStarted || PendingCount != 1)
                throw new InvalidOperationException("NODE_GENERATION_SINGLE_BUFFER_REQUIRED");
            return PendingBuffers[0];
        }
    }
}
#endif
