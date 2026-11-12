#if SKYLINESLINK_INTEGRATION_PROBE
using System;
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
using LiveSubObject = Game.Objects.SubObject;
using NativeSubObjectSystem = Game.Objects.SubObjectSystem;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Own native SubObject producer for the admitted construction and relocation leaf branches.</summary>
    [DisableAutoCreation]
    public sealed partial class SubObjectGenerationProducer : NativeSubObjectSystem
    {
        private int actor, registeredCount, updateCount, updateChunks, tempCount, membersAttempted, membersAcquired, membersDisposed, inspectionMask;
        private bool configured, bindingsVerified, registeredExactly, requiredRegistered, emptyPassed, requested, armed;
        private bool nativeStarted, nativeReturned, completed, joinFailed, generationPassed;
        private bool queueAllocationAttempted, queueAllocationReturned, queueBound, queueReleaseAttempted, queueReleaseReturned;
        private bool queueEmptyObserved, queueIdentityVerified, shouldRun;
        private EntityQuery updates, temps, containers, buildings, happiness;
        private NativeQueue<Entity> loopErrors;
        private TerrainSystem? terrain;
        private WaterSystem? water;
        private ToolSystem? tools;
        private CityConfigurationSystem? city;
        private SubObjectGenerationBarrier? output;
        private Bindings? fields;
        private SubObjectGenerationPreflight? admitted;
        private Action? currentScope;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeCallStarted => nativeStarted;
        internal bool GlobalRandomSeedAdvanceConfirmed => nativeReturned;
        internal bool NativeWorkMayReadSource => nativeStarted && !completed;
        internal bool StorageReleased => (!queueAllocationAttempted || queueReleaseReturned)
            && membersAttempted == membersAcquired && membersAcquired == membersDisposed;
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;
        internal bool GenerationPassed => generationPassed && completed && failureCode == null;
        internal int PendingCount => output?.PendingCount ?? 0;
        internal int OutputCommandUpperBound => admitted?.CommandUpperBound ?? 0;
        internal int CreateUpperBound => admitted?.CreateUpperBound ?? 0;
        internal bool CompletedFor(SubObjectGenerationPreflight input) => ReferenceEquals(admitted, input)
            && nativeReturned && completed && generationPassed && !DestructionStarted && failureCode == null;

        internal void Configure(int actor, SubObjectGenerationBarrier barrier)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0 && barrier != null,
                "SUBOBJECT_GENERATION_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; output = barrier; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            try { CreateConfiguredState(); CreationReturned = true; }
            catch
            {
                // No producer job can exist before this OnCreate returns. SDK
                // rollback owns the registered queries, not this queue.
                if (queueAllocationReturned && !queueReleaseAttempted)
                    try { ReleaseQueue(); } catch { /* Keep the exact unconfirmed-release flags. */ }
                throw;
            }
        }

        private void CreateConfiguredState()
        {
            RequireMainThread(); Enabled = false;
            var bound = fields ?? throw Failure("SUBOBJECT_GENERATION_CONFIGURATION_MISSING");
            var barrier = output ?? throw Failure("SUBOBJECT_GENERATION_CONFIGURATION_MISSING");
            Require(configured && barrier.CreationReturned && ReferenceEquals(barrier.World, World),
                "SUBOBJECT_GENERATION_CONFIGURATION_MISSING");
            terrain = World.GetExistingSystemManaged<TerrainSystem>() ?? throw Failure("SUBOBJECT_GENERATION_TERRAIN_UNAVAILABLE");
            water = World.GetExistingSystemManaged<WaterSystem>() ?? throw Failure("SUBOBJECT_GENERATION_WATER_UNAVAILABLE");
            tools = World.GetExistingSystemManaged<ToolSystem>() ?? throw Failure("SUBOBJECT_GENERATION_TOOLS_UNAVAILABLE");
            city = World.GetExistingSystemManaged<CityConfigurationSystem>() ?? throw Failure("SUBOBJECT_GENERATION_CITY_UNAVAILABLE");
            // Both native update roles retain their own All/Any conditions.
            // Scope also applies to the event role, which must remain empty.
            updates = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<LiveSubObject>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            }, new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Game.Common.Event>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Game.Buildings.RentersUpdated>(), ComponentType.ReadOnly<Game.Objects.SubObjectsUpdated>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            temps = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }, Options = EntityQueryOptions.IncludeDisabledEntities });
            containers = GetEntityQuery(ComponentType.ReadOnly<EditorContainerData>(), ComponentType.ReadOnly<ObjectData>());
            buildings = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            happiness = GetEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>());
            var scope = new ObjectGenerationScope { Value = actor };
            updates.SetSharedComponentFilter(scope); temps.SetSharedComponentFilter(scope);
            bound.Terrain.SetValue(this, terrain); bound.Water.SetValue(this, water); bound.Tools.SetValue(this, tools);
            bound.City.SetValue(this, city); bound.Barrier.SetValue(this, barrier);
            bound.Updates.SetValue(this, updates); bound.Temps.SetValue(this, temps); bound.Containers.SetValue(this, containers);
            bound.Buildings.SetValue(this, buildings); bound.Happiness.SetValue(this, happiness);
            bound.Applied.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Applied>(),
                ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>()));
            RequireForUpdate(updates); requiredRegistered = true;
            queueAllocationAttempted = true;
            loopErrors = new NativeQueue<Entity>(Allocator.Persistent);
            queueAllocationReturned = true;
            Require(loopErrors.IsCreated, "SUBOBJECT_GENERATION_QUEUE_NOT_CREATED");
            bound.LoopErrors.SetValue(this, loopErrors); queueBound = true;
            ObserveBindings(null);
        }

        // Preserve the inherited compiler chain and SystemBase publication, but
        // do not register or unregister unmatched Game lifecycle callbacks.
        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !requested, "SUBOBJECT_GENERATION_EMPTY_REJECTED");
            try
            {
                RequireEmpty(requireCurrentScope);
                shouldRun = ShouldRunSystem(); Guard(requireCurrentScope);
                Require(!shouldRun, "SUBOBJECT_GENERATION_EMPTY_GATE_UNEXPECTED");
                Enabled = true;
                try { Update(); JoinOwnedDependencies(); }
                finally { Enabled = false; }
                RequireEmpty(requireCurrentScope);
                Require(OnUpdateEntries == 0, "SUBOBJECT_GENERATION_EMPTY_ENTERED_KERNEL");
                emptyPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void GenerateOnce(SubObjectGenerationPreflight input, Action requireCurrentScope) =>
            GenerateOnce(input, requireCurrentScope, relocation: false);

        internal void GenerateRelocationOnce(SubObjectGenerationPreflight input, Action requireCurrentScope) =>
            GenerateOnce(input, requireCurrentScope, relocation: true);

        private void GenerateOnce(SubObjectGenerationPreflight input, Action requireCurrentScope, bool relocation)
        {
            RequireMainThread();
            if (input == null || requireCurrentScope == null) throw new ArgumentNullException();
            Require(input.IsRelocation == relocation, "SUBOBJECT_GENERATION_BRANCH_MISMATCH");
            Require(CreationReturned && emptyPassed && !DestructionStarted && !requested && failureCode == null,
                "SUBOBJECT_GENERATION_REQUEST_REJECTED");
            requested = true; admitted = input;
            try
            {
                QualifyInputs(input, requireCurrentScope);
                currentScope = requireCurrentScope; armed = true; Enabled = true;
                try
                {
                    Update();
                    Require(nativeReturned, "SUBOBJECT_GENERATION_NATIVE_NOT_RETURNED");
                    JoinOwnedDependencies();
                }
                finally { armed = false; Enabled = false; currentScope = null; }
                Guard(requireCurrentScope);
                if (relocation) RequireScopedMembers(input, requireCurrentScope);
                int pending = PendingCount; Guard(requireCurrentScope);
                Require(pending == 2 && OnUpdateEntries == 1, "SUBOBJECT_GENERATION_OUTPUT_NOT_CONFIRMED");
                queueEmptyObserved = loopErrors.IsEmpty(); Guard(requireCurrentScope);
                Require(queueEmptyObserved, "SUBOBJECT_GENERATION_UNEXPECTED_RECURSION_ERROR");
                generationPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        private void RequireEmpty(Action scope)
        {
            Guard(scope); ObserveBindings(scope);
            bool updateEmpty = updates.IsEmptyIgnoreFilter; Guard(scope);
            bool tempEmpty = temps.IsEmptyIgnoreFilter; Guard(scope);
            updateCount = updates.CalculateEntityCount(); Guard(scope);
            tempCount = temps.CalculateEntityCount(); Guard(scope);
            int pending = PendingCount; Guard(scope);
            queueEmptyObserved = loopErrors.IsEmpty(); Guard(scope);
            Require(updateEmpty && tempEmpty && updateCount == 0 && tempCount == 0 && pending == 0 && queueEmptyObserved,
                "SUBOBJECT_GENERATION_NOT_EMPTY");
        }

        private void QualifyInputs(SubObjectGenerationPreflight input, Action scope)
        {
            Guard(scope);
            Require(ReferenceEquals(input.Source, World) && input.Actor == actor && input.Root != Entity.Null
                && input.Node != Entity.Null && input.Root != input.Node, "SUBOBJECT_GENERATION_SOURCE_MISMATCH");
            if (input.IsRelocation)
                Require(input.OldNode != Entity.Null && input.OldNode != input.Root && input.OldNode != input.Node,
                    "SUBOBJECT_GENERATION_OLD_NODE_MISMATCH");
            input.Run(scope); Guard(scope);
            Require(input.Passed && input.ResourcesReleased
                && (input.IsRelocation ? input.CreateUpperBound == 6 && input.CommandUpperBound == 57
                    : input.CreateUpperBound > 0 && input.CreateUpperBound <= SubObjectGenerationPreflight.MaximumNormalCreates
                        && input.CommandUpperBound > 0 && input.CommandUpperBound <= SubObjectGenerationPreflight.MaximumNormalCommands),
                "SUBOBJECT_GENERATION_OUTPUT_BOUND_UNQUALIFIED");
            ObserveBindings(scope);
            int pending = PendingCount; Guard(scope);
            Require(pending == 0, "SUBOBJECT_GENERATION_OUTPUT_NOT_EMPTY");
            RequireScopedMembers(input, scope);
            int buildingCount = buildings.CalculateEntityCount(); Guard(scope);
            int happinessCount = happiness.CalculateEntityCount(); Guard(scope);
            Require(buildingCount == 1 && happinessCount == 1, "SUBOBJECT_GENERATION_ENVIRONMENT_SINGLETONS");
            shouldRun = ShouldRunSystem(); Guard(scope);
            queueEmptyObserved = loopErrors.IsEmpty(); Guard(scope);
            Require(shouldRun && queueEmptyObserved, "SUBOBJECT_GENERATION_NATIVE_GATE_CHANGED");
        }

        private void RequireScopedMembers(SubObjectGenerationPreflight input, Action scope)
        {
            int expected = input.IsRelocation ? 3 : 2;
            updateCount = updates.CalculateEntityCount(); Guard(scope);
            updateChunks = updates.CalculateChunkCount(); Guard(scope);
            tempCount = temps.CalculateEntityCount(); Guard(scope);
            Require(updateCount == expected && updateChunks >= 1 && updateChunks <= expected && tempCount == 0,
                "SUBOBJECT_GENERATION_QUERY_BUDGET");
            // A bounded exact membership witness also excludes the event role.
            membersAttempted++;
            NativeArray<Entity> members = updates.ToEntityArray(Allocator.Temp); membersAcquired++;
            try
            {
                Guard(scope);
                Require(members.Length == expected, "SUBOBJECT_GENERATION_MEMBERS_CHANGED");
                bool rootSeen = false, nodeSeen = false;
                bool oldNodeSeen = !input.IsRelocation;
                EntityManager entities = EntityManager; Guard(scope);
                foreach (Entity member in members)
                {
                    bool isEvent = entities.HasComponent<Game.Common.Event>(member); Guard(scope);
                    Require(!isEvent, "SUBOBJECT_GENERATION_EVENT_OUTSIDE_BRANCH");
                    if (input.IsRelocation)
                    {
                        // The old node's TempFlags.Delete is input to child generation.
                        // An ECS Deleted marker would select the native deletion branch instead.
                        bool isLiveTemp = entities.HasComponent<Temp>(member) && entities.HasComponent<Updated>(member)
                            && !entities.HasComponent<Deleted>(member);
                        Guard(scope);
                        Require(isLiveTemp, "SUBOBJECT_GENERATION_RELOCATION_MARKERS_CHANGED");
                    }
                    if (member == input.Root) { Require(!rootSeen, "SUBOBJECT_GENERATION_DUPLICATE_MEMBER"); rootSeen = true; }
                    else if (member == input.Node) { Require(!nodeSeen, "SUBOBJECT_GENERATION_DUPLICATE_MEMBER"); nodeSeen = true; }
                    else if (input.IsRelocation && member == input.OldNode)
                    { Require(!oldNodeSeen, "SUBOBJECT_GENERATION_DUPLICATE_MEMBER"); oldNodeSeen = true; }
                    else throw Failure("SUBOBJECT_GENERATION_FOREIGN_MEMBER");
                }
                Require(rootSeen && nodeSeen && oldNodeSeen, "SUBOBJECT_GENERATION_OWNERS_NOT_FOUND");
            }
            finally { members.Dispose(); membersDisposed++; }
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            var input = admitted ?? throw Failure("SUBOBJECT_GENERATION_ADMISSION_MISSING");
            var scope = currentScope ?? throw Failure("SUBOBJECT_GENERATION_SCOPE_MISSING");
            Require(armed && !nativeStarted, "SUBOBJECT_GENERATION_NOT_ARMED");
            // Re-run the exact typed preflight after SystemBase.BeforeOnUpdate.
            QualifyInputs(input, scope);
            nativeStarted = true; base.OnUpdate(); nativeReturned = true;
        }

        internal EntityCommandBuffer BorrowCompletedForInspection(int index)
        {
            RequireMainThread();
            Require(admitted != null && CompletedFor(admitted) && PendingCount == 2 && index >= 0 && index < 2
                && (inspectionMask & (1 << index)) == 0, "SUBOBJECT_GENERATION_INSPECTION_NOT_READY");
            inspectionMask |= 1 << index;
            return output!.Borrow(index);
        }

        private void JoinOwnedDependencies()
        {
            // The native method can throw between scheduling a job and saving
            // Dependency. A default handle cannot settle that unknown work.
            Require(!joinFailed && (!nativeStarted || nativeReturned), "SUBOBJECT_GENERATION_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (nativeReturned) completed = true; }
            catch { joinFailed = true; throw; }
        }

        private void ObserveBindings(Action? scope)
        {
            var bound = fields ?? throw Failure("SUBOBJECT_GENERATION_BINDINGS_UNAVAILABLE");
            Require(!DestructionStarted && !NativeWorkMayReadSource && queueBound && !queueReleaseAttempted,
                "SUBOBJECT_GENERATION_OBSERVATION_UNAVAILABLE");
            NativeQueue<Entity> boundQueue = (NativeQueue<Entity>)bound.LoopErrors.GetValue(this)!;
            // This pinned SDK's NativeQueue has one pointer-valued instance
            // field and no Equals override. ValueType equality compares that
            // stored value; it does not inspect the queue's native contents.
            queueIdentityVerified = loopErrors.IsCreated && boundQueue.IsCreated && loopErrors.Equals(boundQueue);
            bindingsVerified = ((EntityQuery)bound.Updates.GetValue(this)!).Equals(updates)
                && ((EntityQuery)bound.Temps.GetValue(this)!).Equals(temps)
                && ((EntityQuery)bound.Containers.GetValue(this)!).Equals(containers)
                && ((EntityQuery)bound.Buildings.GetValue(this)!).Equals(buildings)
                && ((EntityQuery)bound.Happiness.GetValue(this)!).Equals(happiness)
                && ReferenceEquals(bound.Terrain.GetValue(this), terrain) && ReferenceEquals(bound.Water.GetValue(this), water)
                && ReferenceEquals(bound.Tools.GetValue(this), tools) && ReferenceEquals(bound.City.GetValue(this), city)
                && ReferenceEquals(bound.Barrier.GetValue(this), output)
                && queueIdentityVerified;
            EntityQuery[] values = EntityQueries; if (scope != null) Guard(scope);
            registeredCount = values.Length;
            registeredExactly = registeredCount == 5 && Contains(values, updates) && Contains(values, temps)
                && Contains(values, containers) && Contains(values, buildings) && Contains(values, happiness);
            bool updateFilter = updates.HasFilter(); if (scope != null) Guard(scope);
            bool tempFilter = temps.HasFilter(); if (scope != null) Guard(scope);
            bool terrainCurrent = ReferenceEquals(World.GetExistingSystemManaged<TerrainSystem>(), terrain);
            if (scope != null) Guard(scope);
            bool waterCurrent = ReferenceEquals(World.GetExistingSystemManaged<WaterSystem>(), water);
            if (scope != null) Guard(scope);
            bool toolsCurrent = ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools);
            if (scope != null) Guard(scope);
            bool cityCurrent = ReferenceEquals(World.GetExistingSystemManaged<CityConfigurationSystem>(), city);
            if (scope != null) Guard(scope);
            Require(bindingsVerified && registeredExactly && requiredRegistered && updateFilter && tempFilter,
                "SUBOBJECT_GENERATION_BINDINGS_CHANGED");
            Require(terrainCurrent && waterCurrent && toolsCurrent && cityCurrent, "SUBOBJECT_GENERATION_PROVIDER_CHANGED");
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource && membersAttempted == membersAcquired && membersAcquired == membersDisposed
                && (!queueAllocationAttempted || queueAllocationReturned) && (!queueReleaseAttempted || queueReleaseReturned),
                "SUBOBJECT_GENERATION_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            if (queueAllocationReturned && !queueReleaseAttempted) ReleaseQueue();
            DestructionReturned = true;
        }

        private void ReleaseQueue()
        {
            Require(!queueReleaseAttempted, "SUBOBJECT_GENERATION_QUEUE_RELEASE_ALREADY_ATTEMPTED");
            queueReleaseAttempted = true;
            // The bound native field aliases this allocation. Never invoke the
            // native/Game OnDestroy or dispose that field copy a second time.
            loopErrors.Dispose(); queueReleaseReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["storageReleased"] = StorageReleased,
            ["emptyPassed"] = emptyPassed, ["onUpdateEntries"] = OnUpdateEntries,
            ["bindingsVerified"] = bindingsVerified, ["registeredExactly"] = registeredExactly, ["registeredCount"] = registeredCount,
            ["requiredRegistered"] = requiredRegistered, ["updateCount"] = updateCount, ["updateChunks"] = updateChunks,
            ["tempCount"] = tempCount, ["shouldRun"] = shouldRun,
            ["queueAllocationAttempted"] = queueAllocationAttempted, ["queueAllocationReturned"] = queueAllocationReturned,
            ["queueBound"] = queueBound, ["queueEmptyObserved"] = queueEmptyObserved,
            ["queueIdentityVerified"] = queueIdentityVerified,
            ["queueReleaseAttempted"] = queueReleaseAttempted, ["queueReleaseReturned"] = queueReleaseReturned,
            ["membersAttempted"] = membersAttempted, ["membersAcquired"] = membersAcquired, ["membersDisposed"] = membersDisposed,
            ["nativeStarted"] = nativeStarted, ["nativeReturned"] = nativeReturned, ["jobJoined"] = completed,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["generationPassed"] = GenerationPassed, ["createUpperBound"] = CreateUpperBound,
            ["commandUpperBound"] = OutputCommandUpperBound, ["inspectionMask"] = inspectionMask,
            ["nativeTransientCleanupScheduled"] = nativeReturned, ["nativeTransientReclamationObserved"] = false,
            ["relocationInputs"] = admitted?.IsRelocation ?? false,
            ["globalRandomSeedAdvanceMayHaveOccurred"] = nativeStarted,
            ["globalRandomSeedAdvanceConfirmed"] = GlobalRandomSeedAdvanceConfirmed, ["sourcePlaybackCalled"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };

        private void Guard(Action scope)
        { scope(); Require(World != null && World.IsCreated && !DestructionStarted, "SUBOBJECT_GENERATION_SOURCE_UNAVAILABLE"); }
        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection value ? value.Code : "SUBOBJECT_GENERATION_FAILED"; exceptionType ??= error.GetType().Name; }
        private static bool Contains(EntityQuery[] values, EntityQuery expected)
        { foreach (EntityQuery value in values) if (value.Equals(expected)) return true; return false; }
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "SUBOBJECT_GENERATION_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Terrain, Water, Tools, City, Barrier, Updates, Temps, Containers, Buildings, Happiness, Applied, LoopErrors;
            internal Bindings()
            {
                Require(NativeConstructionBuild.Game(typeof(NativeSubObjectSystem).Module)
                    && NativeConstructionBuild.Entities(typeof(World).Module)
                    && NativeConstructionBuild.Collections(typeof(NativeQueue<Entity>).Module),
                    "SUBOBJECT_GENERATION_NATIVE_BUILD_UNQUALIFIED");
                Terrain = Field("m_TerrainSystem", typeof(TerrainSystem)); Water = Field("m_WaterSystem", typeof(WaterSystem));
                Tools = Field("m_ToolSystem", typeof(ToolSystem)); City = Field("m_CityConfigurationSystem", typeof(CityConfigurationSystem));
                Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier2B)); Updates = Field("m_UpdateQuery", typeof(EntityQuery));
                Temps = Field("m_TempQuery", typeof(EntityQuery)); Containers = Field("m_ContainerQuery", typeof(EntityQuery));
                Buildings = Field("m_BuildingSettingsQuery", typeof(EntityQuery)); Happiness = Field("m_HappinessParameterQuery", typeof(EntityQuery));
                Applied = Field("m_AppliedTypes", typeof(ComponentTypeSet)); LoopErrors = Field("m_LoopErrorPrefabs", typeof(NativeQueue<Entity>));
            }
            private static FieldInfo Field(string name, Type expected)
            {
                FieldInfo? value = typeof(NativeSubObjectSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(NativeSubObjectSystem) && value.FieldType == expected
                    && !value.IsStatic && !value.IsInitOnly, "SUBOBJECT_GENERATION_FIELD_ABI_UNQUALIFIED");
                return value!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class SubObjectGenerationBarrier : ModificationBarrier2B
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("SUBOBJECT_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("SUBOBJECT_BARRIER_PLAYBACK_FORBIDDEN");
        internal EntityCommandBuffer Borrow(int index)
        {
            if (!CreationReturned || DestructionStarted || PendingCount != 2 || index < 0 || index >= 2)
                throw new InvalidOperationException("SUBOBJECT_BARRIER_INSPECTION_REJECTED");
            return PendingBuffers[index];
        }
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("SUBOBJECT_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy(); // Native producer join and discard only.
            }
            DestructionReturned = true;
        }
        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["sourcePlaybackCalled"] = false
        };
    }
}
#endif
