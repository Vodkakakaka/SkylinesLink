#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    // One developer actor, not a production identity across loading boundaries.
    public struct ObjectGenerationScope : ISharedComponentData, IEquatable<ObjectGenerationScope>
    {
        public int Value;
        public bool Equals(ObjectGenerationScope other) => Value == other.Value;
        public override bool Equals(object? value) => value is ObjectGenerationScope other && Equals(other);
        public override int GetHashCode() => Value;
    }

    /// <summary>Own query/map adapter; no default-system mutation or output playback.</summary>
    [DisableAutoCreation]
    public sealed partial class ObjectGenerationProducer : GenerateObjectsSystem
    {
        private int actor;
        private bool configured, requiredQueriesRegistered, filtersSet, emptyUpdateReturned;
        private bool joinFailed;
        private bool armed, generationRequested, nativeStarted, nativeReturned, completed;
        private bool mapAllocationAttempted, mapAllocationReturned, mapReleaseAttempted, mapReleaseReturned;
        private bool mapBound, bindingsVerified;
        private NativeHashMap<OwnerDefinition, Entity> ownerMap;
        private EntityQuery definitions, deleted, economy;
        private ToolSystem? tools;
        private ObjectGenerationBarrier? output;
        private Bindings? fields;
        private JObject? inspection;
        private JObject? emptyInspection;
        private ObjectDefinitionPayload? admittedPayload;
        private RelocationDefinitionPayload? admittedRelocation;
        private RelocationDefinitionEntities? admittedRelocationInput;
        private Action? requireRelocationAtEntry;
        private bool relocationConfirmed;
        private EntityArchetype admittedArchetype;
        private int admittedCommandUpperBound;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeWorkMayReadSource => nativeStarted && !completed;
        internal bool GlobalRandomSeedAdvanceMayHaveOccurred => nativeStarted;
        internal int OutputCommandUpperBound => admittedCommandUpperBound;
        internal bool CompletedFor(ObjectDefinitionPayload payload) => nativeReturned && completed &&
            ReferenceEquals(admittedPayload, payload) && admittedCommandUpperBound == 64 && !DestructionStarted;
        internal bool ArchetypeMatches(EntityArchetype value) => admittedArchetype.Equals(value);
        internal bool OwnerMapCreated => mapAllocationReturned && !mapReleaseAttempted;
        internal bool MapAllocationUnconfirmed => mapAllocationAttempted && !mapAllocationReturned;
        internal bool MapReleaseUnconfirmed => mapReleaseAttempted && !mapReleaseReturned;
        internal bool StorageReleased => !MapAllocationUnconfirmed && (!mapAllocationAttempted || mapReleaseReturned);
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;

        internal void Configure(int actor, ObjectGenerationBarrier barrier)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0 && barrier != null,
                "OBJECT_GENERATION_CONFIGURATION_REJECTED");
            // Qualify the actual inherited compiler ABI before AddSystemManaged
            // enters its compiler hook. The owner retains this exact instance.
            fields = new Bindings();
            this.actor = actor; output = barrier; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            try { CreateConfiguredState(); CreationReturned = true; }
            catch
            {
                // No native update or external map borrower can run before this
                // OnCreate returns. Release a confirmed own allocation here;
                // SDK creation rollback only disposes registered queries/state.
                if (mapAllocationReturned && !mapReleaseAttempted)
                    try { ReleaseMap(); } catch { /* Explicit unconfirmed-release flags remain. */ }
                throw;
            }
        }

        private void CreateConfiguredState()
        {
            RequireMainThread(); Enabled = false;
            Require(configured && fields != null && output != null && output.CreationReturned
                && ReferenceEquals(output.World, World), "OBJECT_GENERATION_CONFIGURATION_MISSING");
            tools = World.GetExistingSystemManaged<ToolSystem>()
                ?? throw Failure("OBJECT_GENERATION_TOOL_SOURCE_UNAVAILABLE");
            var simulation = World.GetExistingSystemManaged<SimulationSystem>()
                ?? throw Failure("OBJECT_GENERATION_SIMULATION_UNAVAILABLE");
            var search = World.GetExistingSystemManaged<Game.Net.SearchSystem>()
                ?? throw Failure("OBJECT_GENERATION_SEARCH_UNAVAILABLE");
            Require(tools.actionMode == GameMode.Game, "OBJECT_GENERATION_NORMAL_MODE_REQUIRED");

            // GetEntityQuery registers both system ownership and dependency types.
            // Never call native OnCreate then replace its old required queries.
            definitions = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<ObjectDefinition>(), ComponentType.ReadOnly<NetCourse>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            deleted = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            economy = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            var scope = new ObjectGenerationScope { Value = actor };
            definitions.SetSharedComponentFilter(scope); deleted.SetSharedComponentFilter(scope); filtersSet = true;
            fields!.Tool.SetValue(this, tools); fields.Simulation.SetValue(this, simulation);
            fields.Search.SetValue(this, search); fields.Barrier.SetValue(this, output);
            fields.Definitions.SetValue(this, definitions); fields.Deleted.SetValue(this, deleted); fields.Economy.SetValue(this, economy);
            fields.SubTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Game.Objects.SubObject>(),
                ComponentType.ReadWrite<Game.Net.SubNet>(), ComponentType.ReadWrite<Game.Areas.SubArea>()));
            fields.StoppedTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Stopped>(),
                ComponentType.ReadWrite<ParkedCar>(), ComponentType.ReadWrite<ParkedTrain>(), ComponentType.ReadWrite<UpdateFrame>()));
            fields.PreviewTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Static>(),
                ComponentType.ReadWrite<Moving>(), ComponentType.ReadWrite<Game.Objects.TransformFrame>(),
                ComponentType.ReadWrite<Game.Rendering.InterpolatedTransform>(), ComponentType.ReadWrite<UpdateFrame>()));
            RequireForUpdate(definitions); RequireForUpdate(economy); requiredQueriesRegistered = true;
            mapAllocationAttempted = true;
            ownerMap = new NativeHashMap<OwnerDefinition, Entity>(32, Allocator.Persistent);
            mapAllocationReturned = true;
            Require(ownerMap.IsCreated, "OBJECT_GENERATION_MAP_NOT_CREATED");
            fields.Map.SetValue(this, ownerMap); mapBound = true;
            fields.MapRead.SetValue(this, default(JobHandle)); fields.MapWrite.SetValue(this, default(JobHandle));
            CaptureInspection();
        }

        // Preserve the native compiler chain and SystemBase Before/AfterOnUpdate.
        protected override void OnStartRunning() { }
        protected override void OnStopRunning()
        {
            // The native stop clears the map. This one-shot adapter keeps it for
            // subsequent owned consumers, joining its actual reader/writer fences.
            if (!joinFailed) JoinOwnedDependencies();
        }

        internal void ProbeEmptyUpdate()
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && !emptyUpdateReturned,
                "OBJECT_GENERATION_EMPTY_PROBE_REJECTED");
            RequireEmpty();
            Require(!ShouldRunSystem(), "OBJECT_GENERATION_EMPTY_GATE_UNEXPECTED");
            Enabled = true;
            try { Update(); emptyUpdateReturned = true; }
            finally { Enabled = false; }
            RequireEmpty();
            Require(OnUpdateEntries == 0 && output!.PendingCount == 0,
                "OBJECT_GENERATION_EMPTY_PROBE_ENTERED_KERNEL");
            CaptureInspection();
            // Preserve the actual empty-update witness without duplicating
            // the three unchanged query descriptions in every status snapshot.
            emptyInspection = new JObject {
                ["definitionCount"] = inspection!["definitionCount"]!.DeepClone(),
                ["deletedCount"] = inspection["deletedCount"]!.DeepClone(),
                ["economyCount"] = inspection["economyCount"]!.DeepClone(),
                ["definitionEmptyIgnoringFilter"] = inspection["definitionEmptyIgnoringFilter"]!.DeepClone(),
                ["deletedEmptyIgnoringFilter"] = inspection["deletedEmptyIgnoringFilter"]!.DeepClone(),
                ["shouldRunSystem"] = inspection["shouldRunSystem"]!.DeepClone(), ["onUpdateEntries"] = OnUpdateEntries
            };
        }

        internal void GenerateOnce(ObjectDefinitionPayload payload, ObjectDefinitionEntities input,
            ObjectGenerationPreflight admission)
        {
            RequireMainThread();
            if (payload == null || input == null || admission == null) throw Failure("OBJECT_GENERATION_ADMISSION_UNQUALIFIED");
            var pendingOutput = output ?? throw Failure("OBJECT_GENERATION_NOT_CREATED");
            var sourceTools = tools ?? throw Failure("OBJECT_GENERATION_NOT_CREATED");
            var boundFields = fields ?? throw Failure("OBJECT_GENERATION_NOT_CREATED");
            Require(CreationReturned && !DestructionStarted && emptyUpdateReturned && !generationRequested &&
                ReferenceEquals(payload.Source, World) && admission.Matches(World, payload) &&
                input.CreateReturned && sourceTools.actionMode == GameMode.Game && pendingOutput.PendingCount == 0,
                "OBJECT_GENERATION_ADMISSION_UNQUALIFIED");
            Require(boundFields.QueriesMatch(this, definitions, deleted, economy) && definitions.CalculateEntityCount() == 2 &&
                deleted.IsEmptyIgnoreFilter && deleted.CalculateEntityCount() == 0 && economy.CalculateEntityCount() == 1 &&
                EntityManager.GetComponentData<ObjectData>(payload.RootCreation.m_Prefab).m_Archetype.Equals(admission.Archetype),
                "OBJECT_GENERATION_INPUT_CHANGED");
            RequirePrepared(input.Root, payload.RootCreation);
            RequirePrepared(input.Course, payload.CourseCreation);
            Require(EntityManager.GetComponentData<ObjectDefinition>(input.Root).Equals(payload.RootObject) &&
                EntityManager.GetComponentData<OwnerDefinition>(input.Course).Equals(payload.CourseOwner) &&
                EntityManager.GetComponentData<NetCourse>(input.Course).Equals(payload.Course) &&
                !EntityManager.HasComponent<OwnerDefinition>(input.Root) && !EntityManager.HasComponent<NetCourse>(input.Root) &&
                !EntityManager.HasComponent<ObjectDefinition>(input.Course), "OBJECT_GENERATION_VALUES_CHANGED");
            Require(admission.OutputCommandUpperBound == 64, "OBJECT_GENERATION_OUTPUT_BOUND_UNQUALIFIED");
            admittedPayload = payload; admittedArchetype = admission.Archetype;
            admittedCommandUpperBound = admission.OutputCommandUpperBound;
            RunNativeGeneration();
        }

        internal void GenerateRelocationOnce(RelocationDefinitionPayload payload, RelocationDefinitionEntities input,
            RelocationGenerationAdmission admission)
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && emptyUpdateReturned && !generationRequested
                && output != null && output.PendingCount == 0, "OBJECT_RELOCATION_GENERATION_NOT_READY");
            RequireRelocationInputs(payload, input, admission);
            admittedRelocation = payload;
            admittedRelocationInput = input;
            admittedArchetype = admission.ObjectArchetype;
            admittedCommandUpperBound = admission.ObjectCommandUpperBound;

            RunNativeGeneration(() => RequireRelocationInputs(payload, input, admission));
            RequireRelocationInputs(payload, input, admission);
            relocationConfirmed = true;
        }

        private void RequireRelocationInputs(RelocationDefinitionPayload payload, RelocationDefinitionEntities input,
            RelocationGenerationAdmission admission)
        {
            input.RequireCurrent(payload, actor);
            Require(ReferenceEquals(payload.Source, World) && admission.Matches(World, payload)
                && input.CreateReturned && admission.ObjectCommandUpperBound == 64 && admission.ObjectArchetype.Valid,
                "OBJECT_RELOCATION_ADMISSION_CHANGED");
            var bound = fields!;
            Require(tools != null && tools.actionMode == GameMode.Game
                && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && ReferenceEquals(bound.Tool.GetValue(this), tools)
                && bound.Simulation.GetValue(this) is SimulationSystem simulation
                && ReferenceEquals(World.GetExistingSystemManaged<SimulationSystem>(), simulation)
                && bound.Search.GetValue(this) is Game.Net.SearchSystem search
                && ReferenceEquals(World.GetExistingSystemManaged<Game.Net.SearchSystem>(), search)
                && output != null && ReferenceEquals(output.World, World)
                && ReferenceEquals(World.GetExistingSystemManaged<ObjectGenerationBarrier>(), output)
                && ReferenceEquals(bound.Barrier.GetValue(this), output)
                && bound.QueriesMatch(this, definitions, deleted, economy)
                && definitions.HasFilter() && deleted.HasFilter() && HasRelocationMembers(definitions, input)
                && deleted.IsEmptyIgnoreFilter && deleted.CalculateEntityCount() == 0 && economy.CalculateEntityCount() == 1,
                "OBJECT_RELOCATION_BINDINGS_OR_QUERY_CHANGED");

            Entity prefab = payload.Original.Prefab;
            Require(EntityManager.HasComponent<ObjectData>(prefab)
                && EntityManager.GetComponentData<ObjectData>(prefab).m_Archetype.Equals(admission.ObjectArchetype),
                "OBJECT_RELOCATION_ARCHETYPE_CHANGED");
            RequireEmptyRelocationMap();
        }

        internal static bool HasRelocationMembers(EntityQuery query, RelocationDefinitionEntities input)
        {
            if (query.CalculateEntityCount() != 3) return false;
            using var values = query.ToEntityArray(Allocator.Temp);
            if (values.Length != 3) return false;
            int seen = 0;
            foreach (Entity entity in values)
            {
                if (entity == input.Root) seen |= 1;
                else if (entity == input.OldNode) seen |= 2;
                else if (entity == input.NewNode) seen |= 4;
                else return false;
            }
            return seen == 7;
        }

        internal void RequireRelocationOwnerMapEmpty(RelocationDefinitionPayload payload, RelocationDefinitionEntities input)
        {
            RequireMainThread();
            Require(CompletedRelocationFor(payload) && ReferenceEquals(admittedRelocationInput, input),
                "OBJECT_RELOCATION_OWNER_MAP_BATCH_MISMATCH");
            RequireEmptyRelocationMap();
        }

        private void RequireEmptyRelocationMap()
        {
            var nativeMap = GetReusedOwnerMap(out JobHandle writer);
            writer.Complete();
            Require(OwnerMapCreated && nativeMap.IsCreated && nativeMap.Equals(ownerMap) && nativeMap.Count == 0,
                "OBJECT_RELOCATION_OWNER_MAP_NOT_EMPTY_OR_CHANGED");
        }

        private bool CompletedRelocationFor(RelocationDefinitionPayload payload) => relocationConfirmed && nativeReturned
            && completed && !DestructionStarted && ReferenceEquals(admittedRelocation, payload);

        private void RunNativeGeneration(Action? requireAtEntry = null)
        {
            var pendingOutput = output ?? throw Failure("OBJECT_GENERATION_NOT_CREATED");
            requireRelocationAtEntry = requireAtEntry;
            generationRequested = true; armed = true; Enabled = true;
            try
            {
                Update();
                Require(nativeReturned, "OBJECT_GENERATION_NATIVE_NOT_RETURNED");
                JoinOwnedDependencies();
                Require(pendingOutput.PendingCount == 1, "OBJECT_GENERATION_BUFFER_NOT_CONFIRMED");
                CaptureInspection();
            }
            finally { armed = false; Enabled = false; requireRelocationAtEntry = null; }
        }

        private void RequirePrepared(Entity entity, CreationDefinition expected)
        {
            Require(entity != Entity.Null && EntityManager.Exists(entity) && EntityManager.HasComponent<Unity.Entities.Disabled>(entity) &&
                EntityManager.HasComponent<Updated>(entity) && !EntityManager.HasComponent<Temp>(entity) &&
                EntityManager.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor &&
                EntityManager.GetComponentData<CreationDefinition>(entity).Equals(expected), "OBJECT_GENERATION_PREPARED_INPUT_CHANGED");
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Require(armed && !nativeStarted && tools!.actionMode == GameMode.Game, "OBJECT_GENERATION_NOT_ARMED");
            requireRelocationAtEntry?.Invoke();
            nativeStarted = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        internal EntityCommandBuffer BorrowCompletedForInspection(ObjectDefinitionPayload payload)
        {
            RequireMainThread();
            var pendingOutput = output ?? throw Failure("OBJECT_GENERATION_INSPECTION_NOT_READY");
            Require(CompletedFor(payload) && pendingOutput.PendingCount == 1,
                "OBJECT_GENERATION_INSPECTION_NOT_READY");
            return pendingOutput.BorrowSingle();
        }

        internal EntityCommandBuffer BorrowRelocationCommands(RelocationDefinitionPayload payload)
        {
            RequireMainThread();
            Require(CompletedRelocationFor(payload), "OBJECT_RELOCATION_INSPECTION_NOT_READY");
            return output!.BorrowSingle();
        }

        private void RequireEmpty()
        {
            Require(definitions.IsEmptyIgnoreFilter && deleted.IsEmptyIgnoreFilter
                && definitions.CalculateEntityCount() == 0 && deleted.CalculateEntityCount() == 0
                && economy.CalculateEntityCount() == 1,
                "OBJECT_GENERATION_INPUT_NOT_EMPTY");
        }

        private void JoinOwnedDependencies()
        {
            Require(!joinFailed && (!nativeStarted || nativeReturned), "OBJECT_GENERATION_JOIN_UNRESOLVED");
            try
            {
                Dependency.Complete();
                if (fields != null && mapBound)
                {
                    ((JobHandle)fields.MapRead.GetValue(this)!).Complete();
                    ((JobHandle)fields.MapWrite.GetValue(this)!).Complete();
                }
                if (nativeReturned) completed = true;
            }
            catch { joinFailed = true; throw; }
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource && !MapAllocationUnconfirmed && !MapReleaseUnconfirmed,
                "OBJECT_GENERATION_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            if (mapAllocationReturned)
                ReleaseMap();
            // SystemState subsequently disposes registered queries. Do not also
            // Dispose their field aliases, or unmatched Game callbacks.
            DestructionReturned = true;
        }

        private void ReleaseMap()
        {
            Require(!mapReleaseAttempted, "OBJECT_GENERATION_MAP_RELEASE_ALREADY_ATTEMPTED");
            mapReleaseAttempted = true;
            // This copy and the bound native field alias one allocation. Only
            // this adapter disposes it; never call native/Game OnDestroy.
            ownerMap.Dispose();
            mapReleaseReturned = true;
        }

        private void CaptureInspection()
        {
            RequireMainThread();
            Require(!DestructionStarted && !NativeWorkMayReadSource && fields != null && mapBound,
                "OBJECT_GENERATION_INSPECTION_UNAVAILABLE");
            JoinOwnedDependencies();
            bindingsVerified = fields!.QueriesMatch(this, definitions, deleted, economy)
                && ReferenceEquals(fields.Barrier.GetValue(this), output);
            NativeHashMap<OwnerDefinition, Entity> nativeMap = GetReusedOwnerMap(out JobHandle write);
            var registered = EntityQueries;
            Require(registered.Length == 3, "OBJECT_GENERATION_QUERY_REGISTRATION_UNEXPECTED");
            bool registeredExactly = Contains(registered, definitions) && Contains(registered, deleted) && Contains(registered, economy);
            inspection = new JObject {
                ["bindingsVerified"] = bindingsVerified, ["registeredQueryCount"] = registered.Length,
                ["queriesRegisteredToThisSystem"] = registeredExactly, ["requiredQueriesRegistered"] = requiredQueriesRegistered,
                ["filtersSet"] = filtersSet, ["definitionFilterPresent"] = definitions.HasFilter(), ["deletedFilterPresent"] = deleted.HasFilter(),
                ["definitionCount"] = definitions.CalculateEntityCount(), ["deletedCount"] = deleted.CalculateEntityCount(),
                ["economyCount"] = economy.CalculateEntityCount(), ["definitionEmptyIgnoringFilter"] = definitions.IsEmptyIgnoreFilter,
                ["deletedEmptyIgnoringFilter"] = deleted.IsEmptyIgnoreFilter, ["shouldRunSystem"] = ShouldRunSystem(),
                ["definitions"] = Describe(definitions), ["deleted"] = Describe(deleted), ["economy"] = Describe(economy),
                ["nativeOwnerMapCreated"] = nativeMap.IsCreated, ["nativeOwnerMapCount"] = nativeMap.Count,
                ["ownedMapCount"] = ownerMap.Count, ["nativeMapWriteCompleted"] = write.IsCompleted,
                ["nativeMapReadCompleted"] = ((JobHandle)fields.MapRead.GetValue(this)!).IsCompleted
            };
            Require(bindingsVerified && registeredExactly, "OBJECT_GENERATION_BINDING_NOT_CONFIRMED");
        }

        internal JObject Snapshot() => new JObject {
            ["actor"] = actor, ["configured"] = configured, ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["ownerMapCreated"] = OwnerMapCreated, ["mapAllocationUnconfirmed"] = MapAllocationUnconfirmed,
            ["mapReleaseAttempted"] = mapReleaseAttempted, ["mapReleaseReturned"] = mapReleaseReturned,
            ["resourcesReleased"] = ResourcesReleased, ["storageReleased"] = StorageReleased,
            ["mapReleaseUnconfirmed"] = MapReleaseUnconfirmed, ["emptyUpdateReturned"] = emptyUpdateReturned, ["onUpdateEntries"] = OnUpdateEntries,
            ["nativeCallStarted"] = nativeStarted, ["nativeCallReturned"] = nativeReturned, ["completionObserved"] = completed,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["inspection"] = inspection == null ? JValue.CreateNull() : inspection.DeepClone(),
            ["emptyInspection"] = emptyInspection == null ? JValue.CreateNull() : emptyInspection.DeepClone(),
            ["outputCommandUpperBound"] = admittedCommandUpperBound,
            ["nativeTransientAllocationMayHaveStarted"] = nativeStarted,
            ["nativeTransientCleanupScheduled"] = nativeReturned, ["nativeTransientReclamationObserved"] = false,
            ["globalRandomSeedAdvanceMayHaveOccurred"] = GlobalRandomSeedAdvanceMayHaveOccurred,
            ["playbackCalled"] = false,
            ["scope"] = admittedRelocation == null
                ? "Owned query/map and qualified new-input native generation without Playback. Internal scheduled deallocator execution is not directly observed; no placement, fees or construction acceptance."
                : "Owned relocation definition generation; native commands are unplayed and the original object is not moved."
        };

        private static bool Contains(EntityQuery[] queries, EntityQuery expected)
        { foreach (var query in queries) if (query.Equals(expected)) return true; return false; }
        private static JArray Describe(EntityQuery query)
        {
            EntityQueryDesc[] desc = query.GetEntityQueryDescs();
            Require(desc.Length == 1, "OBJECT_GENERATION_QUERY_DESC_UNEXPECTED");
            var result = new JArray();
            foreach (EntityQueryDesc item in desc) result.Add(new JObject {
                ["all"] = Types(item.All), ["any"] = Types(item.Any), ["none"] = Types(item.None),
                ["options"] = (int)item.Options
            });
            return result;
        }
        private static JArray Types(ComponentType[]? types)
        {
            var result = new JArray(); if (types == null) return result;
            Require(types.Length <= 8, "OBJECT_GENERATION_QUERY_TYPE_BUDGET");
            foreach (ComponentType type in types) result.Add(TypeManager.GetType(type.TypeIndex).FullName);
            return result;
        }
        private static void RequireMainThread()
        { Require(MainThreadDispatcher.isMainThread, "OBJECT_GENERATION_MAIN_THREAD_REQUIRED"); }
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static InvalidOperationException Failure(string code) => new InvalidOperationException(code);

        // Fixed ABI for this exact native type and our sealed instance only.
        // No caller-supplied type/field name or default-system reference exists.
        private sealed class Bindings
        {
            internal readonly FieldInfo Tool, Simulation, Search, Barrier, Definitions, Deleted, Economy;
            internal readonly FieldInfo SubTypes, StoppedTypes, PreviewTypes, Map, MapRead, MapWrite;
            internal Bindings()
            {
                Require(NativeConstructionBuild.Game(typeof(GenerateObjectsSystem).Module)
                    && NativeConstructionBuild.Entities(typeof(World).Module)
                    && NativeConstructionBuild.Collections(typeof(NativeHashMap<OwnerDefinition, Entity>).Module),
                    "OBJECT_GENERATION_NATIVE_BUILD_UNQUALIFIED");
                Tool = Field("m_ToolSystem", typeof(ToolSystem)); Simulation = Field("m_SimulationSystem", typeof(SimulationSystem));
                Search = Field("m_NetSearchSystem", typeof(Game.Net.SearchSystem)); Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier1));
                Definitions = Field("m_DefinitionQuery", typeof(EntityQuery)); Deleted = Field("m_DeletedQuery", typeof(EntityQuery));
                Economy = Field("m_EconomyParameterQuery", typeof(EntityQuery)); SubTypes = Field("m_SubTypes", typeof(ComponentTypeSet));
                StoppedTypes = Field("m_StoppedUpdateFrameTypes", typeof(ComponentTypeSet)); PreviewTypes = Field("m_VehiclePreviewTypes", typeof(ComponentTypeSet));
                Map = Field("m_ReusedOwnerMap", typeof(NativeHashMap<OwnerDefinition, Entity>));
                MapRead = Field("m_OwnerMapReadDeps", typeof(JobHandle)); MapWrite = Field("m_OwnerMapWriteDeps", typeof(JobHandle));
            }
            internal bool QueriesMatch(ObjectGenerationProducer owner, EntityQuery definitions, EntityQuery deleted, EntityQuery economy)
                => ((EntityQuery)Definitions.GetValue(owner)!).Equals(definitions) && ((EntityQuery)Deleted.GetValue(owner)!).Equals(deleted)
                    && ((EntityQuery)Economy.GetValue(owner)!).Equals(economy);
            private static FieldInfo Field(string name, Type expected)
            {
                FieldInfo? field = typeof(GenerateObjectsSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(field != null && field.DeclaringType == typeof(GenerateObjectsSystem) && field.FieldType == expected
                    && !field.IsStatic && !field.IsInitOnly, "OBJECT_GENERATION_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class ObjectGenerationBarrier : ModificationBarrier1
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("OBJECT_GENERATION_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("OBJECT_GENERATION_BARRIER_CANNOT_PLAYBACK");
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("OBJECT_GENERATION_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy(); // Native discard/producer join, never Playback.
            }
            DestructionReturned = true;
        }
        internal EntityCommandBuffer BorrowSingle()
        {
            if (!CreationReturned || DestructionStarted || PendingCount != 1)
                throw new InvalidOperationException("OBJECT_GENERATION_SINGLE_BUFFER_REQUIRED");
            return PendingBuffers[0];
        }
    }
}
#endif
