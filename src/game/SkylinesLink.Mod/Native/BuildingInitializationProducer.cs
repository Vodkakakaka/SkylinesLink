#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using NativeInitializeSystem = Game.Buildings.InitializeSystem;
using CoverageElement = Game.Pathfind.CoverageElement;
using Placeholder = Game.Objects.Placeholder;
using ServiceUpgrade = Game.Buildings.ServiceUpgrade;
using PoliceStation = Game.Buildings.PoliceStation;
using PostFacility = Game.Buildings.PostFacility;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Runs native building initialization on one scoped building Temp.</summary>
    [DisableAutoCreation]
    public sealed partial class BuildingInitializationProducer : NativeInitializeSystem
    {
        private const int CreatedFlag = 1, DestroyedFlag = 2, AbandonedFlag = 4;
        private int actor, registeredCount, pendingCount, commandUpperBound, observedPending = -1;
        private int arrayAttempts, arraysAcquired, arraysDisposed;
        private bool configured, emptyArmed, emptyStarted, emptyReturned, emptyCompleted, emptyPassed, emptyShouldRun;
        private bool rootBound, requested, armed, nativeStarted, nativeReturned, completed, joinFailed;
        private bool bindingsVerified, registeredExactly, outputQualified, scopeAfterPlayback;
        private Entity root;
        private Entity relocationPrefab;
        private Temp? relocationTemp;
        private EntityQuery coverage, buildings;
        private BuildingInitializationBarrier? output;
        private ElectricityRoadConnectionGraphSystem? electricity;
        private WaterPipeRoadConnectionGraphSystem? water;
        private Bindings? fields;
        private InputWitness admitted;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal int CoverageCount { get; private set; }
        internal int BuildingCount { get; private set; }
        internal int CoverageChunkCount { get; private set; }
        internal int BuildingChunkCount { get; private set; }
        internal int CoverageBufferIndex { get; private set; } = -1;
        internal int BuildingBufferIndex { get; private set; } = -1;
        internal int PendingCount => output?.PendingCount ?? 0;
        internal int OutputCommandUpperBound => commandUpperBound;
        internal bool NativeCallStarted => nativeStarted;
        internal bool NativeWorkMayReadSource => emptyStarted && !emptyCompleted || nativeStarted && !completed
            || output != null && output.PlaybackStarted && !output.PlaybackCallEnded;
        internal bool StorageReleased => arrayAttempts == arraysAcquired && arraysAcquired == arraysDisposed;
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;
        internal bool InitializationPassed => completed && outputQualified && output?.PlaybackReturned == true
            && scopeAfterPlayback && failureCode == null;
        internal bool CompletedFor(Entity value) => completed && nativeReturned && value == root && rootBound
            && !DestructionStarted && !joinFailed && failureCode == null;

        internal void Configure(int actor, BuildingInitializationBarrier barrier)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0 && barrier != null,
                "BUILDING_INIT_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; output = barrier; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            var barrier = output ?? throw Failure("BUILDING_INIT_CONFIGURATION_MISSING");
            var bound = fields ?? throw Failure("BUILDING_INIT_CONFIGURATION_MISSING");
            Require(configured && barrier.CreationReturned && ReferenceEquals(barrier.World, World),
                "BUILDING_INIT_CONFIGURATION_MISSING");
            electricity = World.GetExistingSystemManaged<ElectricityRoadConnectionGraphSystem>()
                ?? throw Failure("BUILDING_INIT_ELECTRICITY_SOURCE_UNAVAILABLE");
            water = World.GetExistingSystemManaged<WaterPipeRoadConnectionGraphSystem>()
                ?? throw Failure("BUILDING_INIT_WATER_SOURCE_UNAVAILABLE");
            // Preserve the native roles and exclusions, adding only this actor's
            // Disabled Temp scope. No native/default query is changed.
            coverage = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<CoverageServiceType>(), ComponentType.ReadOnly<CoverageElement>(),
                    ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                None = new[] { ComponentType.ReadOnly<Placeholder>() }, Options = EntityQueryOptions.IncludeDisabledEntities });
            buildings = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                None = new[] { ComponentType.ReadOnly<ServiceUpgrade>(), ComponentType.ReadOnly<Placeholder>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            var scope = new ObjectGenerationScope { Value = actor };
            coverage.SetSharedComponentFilter(scope); buildings.SetSharedComponentFilter(scope);
            bound.Barrier.SetValue(this, barrier); bound.Electricity.SetValue(this, electricity); bound.Water.SetValue(this, water);
            bound.Coverage.SetValue(this, coverage); bound.Buildings.SetValue(this, buildings);
            bound.DestroyedTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadOnly<ElectricityConsumer>(),
                ComponentType.ReadOnly<WaterConsumer>(), ComponentType.ReadOnly<GarbageProducer>(), ComponentType.ReadOnly<MailProducer>()));
            // Native InitializeSystem has no RequireForUpdate. Its inherited
            // compiler hook and SystemBase dependency publication remain native.
            ObserveBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate()
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !rootBound,
                "BUILDING_INIT_EMPTY_REJECTED");
            ObserveBindings();
            Require(coverage.IsEmptyIgnoreFilter && buildings.IsEmptyIgnoreFilter
                && coverage.CalculateEntityCount() == 0 && buildings.CalculateEntityCount() == 0
                && PendingCount == 0, "BUILDING_INIT_NOT_EMPTY");
            emptyShouldRun = ShouldRunSystem();
            Require(emptyShouldRun, "BUILDING_INIT_EMPTY_GATE_UNEXPECTED");
            emptyArmed = true; Enabled = true;
            try
            {
                Update();
                Require(emptyReturned, "BUILDING_INIT_EMPTY_NATIVE_NOT_RETURNED");
                JoinOwnedDependencies();
                Require(OnUpdateEntries == 1 && emptyCompleted && PendingCount == 0, "BUILDING_INIT_EMPTY_UNEXPECTED_OUTPUT");
                emptyPassed = true;
            }
            finally { emptyArmed = false; Enabled = false; }
        }

        internal void BindRoot(Entity value) => BindRootCore(value);

        internal void BindRelocationRoot(Entity value, Entity original, Entity prefab, Temp expectedTemp)
        {
            RequireMainThread();
            Require(value.Index > 0 && original.Index > 0 && prefab.Index > 0
                && value != original && value != prefab && original != prefab
                && expectedTemp.m_Original == original
                && expectedTemp.m_Flags == (TempFlags.Modify | TempFlags.Essential),
                "BUILDING_INIT_RELOCATION_BIND_REJECTED");
            BindRootCore(value);
            relocationPrefab = prefab;
            relocationTemp = expectedTemp;
        }

        private void BindRootCore(Entity value)
        {
            RequireMainThread();
            Require(CreationReturned && emptyPassed && !DestructionStarted && !rootBound && !requested
                && value != Entity.Null && value.Index >= 0, "BUILDING_INIT_ROOT_BIND_REJECTED");
            root = value; rootBound = true;
        }

        internal void InitializeOnce(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && emptyPassed && rootBound && !DestructionStarted && !requested,
                "BUILDING_INIT_REQUEST_REJECTED");
            requested = true;
            try
            {
                requireCurrentScope();
                Require(PendingCount == 0, "BUILDING_INIT_OUTPUT_NOT_EMPTY");
                InputWitness before = QualifyInputs();
                requireCurrentScope();
                admitted = QualifyInputs();
                Require(admitted.Equals(before), "BUILDING_INIT_INPUT_CHANGED");
                CoverageBufferIndex = admitted.CoverageBranch ? 0 : -1;
                BuildingBufferIndex = admitted.BuildingBranch ? (admitted.CoverageBranch ? 1 : 0) : -1;
                pendingCount = (admitted.CoverageBranch ? 1 : 0) + (admitted.BuildingBranch ? 1 : 0);
                commandUpperBound = CoverageCount + BuildingCount * 4;
                Require(commandUpperBound >= 0 && commandUpperBound <= 5, "BUILDING_INIT_COMMAND_BUDGET");
                armed = true; Enabled = true;
                try
                {
                    Update();
                    Require(nativeReturned, "BUILDING_INIT_NATIVE_NOT_RETURNED");
                    JoinOwnedDependencies();
                }
                finally { armed = false; Enabled = false; }
                requireCurrentScope();
                observedPending = PendingCount;
                Require(observedPending == pendingCount, "BUILDING_INIT_BUFFER_COUNT_CHANGED");
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        // All buffers must be decoded before any is played. A failed playback
        // is never retried, including when only an earlier buffer was applied.
        internal void ApplyOnce(Action requireCurrentScope, Action qualifyOutput)
        {
            RequireMainThread();
            if (requireCurrentScope == null || qualifyOutput == null) throw new ArgumentNullException();
            var barrier = output ?? throw Failure("BUILDING_INIT_APPLY_REJECTED");
            Require(CompletedFor(root) && !barrier.PlaybackAttempted, "BUILDING_INIT_APPLY_REJECTED");
            try
            {
                requireCurrentScope();
                Require(QualifyInputs().Equals(admitted), "BUILDING_INIT_INPUT_CHANGED");
                qualifyOutput();
                requireCurrentScope();
                Require(QualifyInputs().Equals(admitted) && PendingCount == pendingCount,
                    "BUILDING_INIT_APPLY_SOURCE_CHANGED");
                outputQualified = true;
                barrier.PlaybackAllOnce(pendingCount, () => { requireCurrentScope(); RequireScopedRoot(); });
                requireCurrentScope();
                RequireScopedRoot();
                scopeAfterPlayback = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal EntityCommandBuffer BorrowCompletedForInspection(int index)
        {
            RequireMainThread();
            Require(CompletedFor(root) && output?.PlaybackAttempted == false && PendingCount == pendingCount,
                "BUILDING_INIT_INSPECTION_NOT_READY");
            return output!.Borrow(index);
        }

        private InputWitness QualifyInputs()
        {
            Require(World != null && World.IsCreated && !DestructionStarted, "BUILDING_INIT_SOURCE_UNAVAILABLE");
            ObserveBindings(); RequireScopedRoot();
            CoverageCount = coverage.CalculateEntityCount(); CoverageChunkCount = coverage.CalculateChunkCount();
            BuildingCount = buildings.CalculateEntityCount(); BuildingChunkCount = buildings.CalculateChunkCount();
            Require(CoverageCount >= 0 && CoverageCount <= 1 && BuildingCount >= 0 && BuildingCount <= 1
                && CoverageChunkCount == CoverageCount && BuildingChunkCount == BuildingCount,
                "BUILDING_INIT_QUERY_BUDGET");
            Require(CoverageCount == 0 || coverage.GetSingletonEntity() == root, "BUILDING_INIT_FOREIGN_COVERAGE");
            Require(BuildingCount == 0 || buildings.GetSingletonEntity() == root, "BUILDING_INIT_FOREIGN_BUILDING");
            Require(!relocationTemp.HasValue || BuildingCount == 1, "BUILDING_INIT_RELOCATION_QUERY_MISMATCH");
            EntityManager entities = EntityManager;
            var witness = new InputWitness {
                Prefab = entities.GetComponentData<PrefabRef>(root).m_Prefab,
                Temp = entities.GetComponentData<Temp>(root),
                CoverageBranch = !coverage.IsEmptyIgnoreFilter, BuildingBranch = !buildings.IsEmptyIgnoreFilter,
                CoverageCount = CoverageCount, BuildingCount = BuildingCount,
                Flags = PresenceFlags(entities),
            };
            Require(witness.Prefab != Entity.Null && entities.Exists(witness.Prefab), "BUILDING_INIT_PREFAB_UNAVAILABLE");
            if (CoverageCount != 0)
            {
                Require(entities.HasComponent<CoverageData>(witness.Prefab), "BUILDING_INIT_COVERAGE_DATA_MISSING");
                witness.Coverage = entities.GetComponentData<CoverageData>(witness.Prefab);
            }
            if (BuildingCount != 0)
            {
                // Non-Created Destroyed chunks return before every prefab read.
                bool created = (witness.Flags & CreatedFlag) != 0, destroyed = (witness.Flags & DestroyedFlag) != 0;
                bool abandoned = (witness.Flags & AbandonedFlag) != 0;
                if (created || !destroyed)
                {
                    witness.Consumption = entities.HasComponent<ConsumptionData>(witness.Prefab);
                    if (witness.Consumption)
                    {
                        Require(entities.HasComponent<BuildingData>(witness.Prefab), "BUILDING_INIT_BUILDING_DATA_MISSING");
                        witness.Building = entities.GetComponentData<BuildingData>(witness.Prefab);
                    }
                    // Temp already excludes the native non-Temp Gate route
                    // branch. Only this remaining branch traverses ObjectData
                    // and can enqueue a real electricity/water road edge.
                    if (!created && !abandoned && entities.HasComponent<ObjectData>(witness.Prefab))
                    {
                        witness.ObjectDataPresent = true;
                        witness.Archetype = entities.GetComponentData<ObjectData>(witness.Prefab).m_Archetype;
                        witness.RoadEdge = entities.GetComponentData<Building>(root).m_RoadEdge;
                        Require(witness.RoadEdge == Entity.Null && witness.Archetype.Valid
                            && witness.Archetype.TypesCount > 0 && witness.Archetype.TypesCount <= 256,
                            "BUILDING_INIT_GRAPH_WRITE_OR_TYPE_BUDGET");
                        arrayAttempts++;
                        NativeArray<ComponentType> values = witness.Archetype.GetComponentTypes(Allocator.Temp); arraysAcquired++;
                        try { Require(values.Length == witness.Archetype.TypesCount - 1, "BUILDING_INIT_ARCHETYPE_CHANGED"); }
                        finally { values.Dispose(); arraysDisposed++; }
                    }
                }
            }
            return witness;
        }

        private int PresenceFlags(EntityManager entities)
        {
            int result = 0;
            if (entities.HasComponent<Created>(root)) result |= CreatedFlag;
            if (entities.HasComponent<Destroyed>(root)) result |= DestroyedFlag;
            if (entities.HasComponent<Abandoned>(root)) result |= AbandonedFlag;
            if (entities.HasComponent<PoliceStation>(root)) result |= 8;
            if (entities.HasComponent<PostFacility>(root)) result |= 16;
            if (entities.HasComponent<MailProducer>(root)) result |= 32;
            if (entities.HasComponent<ElectricityConsumer>(root)) result |= 64;
            if (entities.HasComponent<WaterConsumer>(root)) result |= 128;
            if (entities.HasComponent<GarbageProducer>(root)) result |= 256;
            return result;
        }

        private void RequireScopedRoot()
        {
            EntityManager entities = EntityManager;
            Require(rootBound && entities.Exists(root) && entities.HasComponent<Unity.Entities.Disabled>(root)
                && entities.HasComponent<ObjectGenerationScope>(root)
                && entities.GetSharedComponent<ObjectGenerationScope>(root).Value == actor
                && entities.HasComponent<Temp>(root) && entities.HasComponent<PrefabRef>(root)
                && !entities.HasComponent<Deleted>(root), "BUILDING_INIT_ROOT_SCOPE_CHANGED");
            Temp temp = entities.GetComponentData<Temp>(root);
            if (!relocationTemp.HasValue)
            {
                Require(temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0,
                    "BUILDING_INIT_ROOT_NOT_NEW_TEMP");
                return;
            }

            Require(temp.Equals(relocationTemp.Value)
                && entities.GetComponentData<PrefabRef>(root).m_Prefab == relocationPrefab,
                "BUILDING_INIT_RELOCATION_TEMP_CHANGED");
            // Keep the newborn building branch. A non-Created building can
            // enqueue a real road edge into the stock graph systems.
            Require(entities.HasComponent<Created>(root) && entities.HasComponent<Updated>(root)
                && !entities.HasComponent<Destroyed>(root) && !entities.HasComponent<Abandoned>(root)
                && entities.HasComponent<Building>(root)
                && entities.GetComponentData<Building>(root).m_RoadEdge == Entity.Null,
                "BUILDING_INIT_RELOCATION_BUILDING_CHANGED");
            Entity original = relocationTemp.Value.m_Original;
            Require(entities.Exists(original) && entities.HasComponent<Building>(original)
                && entities.HasComponent<PrefabRef>(original)
                && entities.GetComponentData<PrefabRef>(original).m_Prefab == relocationPrefab
                && !entities.HasComponent<Temp>(original) && !entities.HasComponent<Deleted>(original)
                && !entities.HasComponent<Unity.Entities.Disabled>(original)
                && !entities.HasComponent<ObjectGenerationScope>(original),
                "BUILDING_INIT_RELOCATION_ORIGINAL_CHANGED");
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            if (emptyArmed)
            {
                Require(!emptyStarted && !nativeStarted, "BUILDING_INIT_EMPTY_ALREADY_STARTED");
                emptyStarted = true; base.OnUpdate(); emptyReturned = true;
                return;
            }
            Require(armed && !nativeStarted, "BUILDING_INIT_NOT_ARMED");
            if (relocationTemp.HasValue)
                Require(QualifyInputs().Equals(admitted), "BUILDING_INIT_RELOCATION_INPUT_CHANGED");
            nativeStarted = true; base.OnUpdate(); nativeReturned = true;
        }

        private void JoinOwnedDependencies()
        {
            Require(!joinFailed && (!emptyStarted || emptyReturned) && (!nativeStarted || nativeReturned),
                "BUILDING_INIT_JOIN_UNRESOLVED");
            try
            {
                Dependency.Complete();
                if (emptyReturned) emptyCompleted = true;
                if (nativeReturned) completed = true;
            }
            catch { joinFailed = true; throw; }
        }

        private void ObserveBindings()
        {
            var bound = fields ?? throw Failure("BUILDING_INIT_BINDINGS_UNAVAILABLE");
            Require(!NativeWorkMayReadSource && !DestructionStarted, "BUILDING_INIT_OBSERVATION_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)bound.Coverage.GetValue(this)!).Equals(coverage)
                && ((EntityQuery)bound.Buildings.GetValue(this)!).Equals(buildings)
                && ReferenceEquals(bound.Barrier.GetValue(this), output)
                && ReferenceEquals(bound.Electricity.GetValue(this), electricity) && ReferenceEquals(bound.Water.GetValue(this), water)
                && ReferenceEquals(World.GetExistingSystemManaged<ElectricityRoadConnectionGraphSystem>(), electricity)
                && ReferenceEquals(World.GetExistingSystemManaged<WaterPipeRoadConnectionGraphSystem>(), water);
            EntityQuery[] values = EntityQueries; registeredCount = values.Length;
            registeredExactly = registeredCount == 2 && Contains(values, coverage) && Contains(values, buildings);
            Require(bindingsVerified && registeredExactly && coverage.HasFilter() && buildings.HasFilter(), "BUILDING_INIT_BINDINGS_CHANGED");
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource && StorageReleased, "BUILDING_INIT_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // SystemState owns the registered queries. We never registered the
            // native Game lifecycle, and own neither graph system nor its queue.
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["emptyPassed"] = emptyPassed, ["emptyShouldRun"] = emptyShouldRun,
            ["emptyNativeReturned"] = emptyReturned, ["emptyJoined"] = emptyCompleted, ["onUpdateEntries"] = OnUpdateEntries,
            ["rootBound"] = rootBound, ["relocationRootBound"] = relocationTemp.HasValue,
            ["root"] = new JObject { ["index"] = root.Index, ["version"] = root.Version },
            ["bindingsVerified"] = bindingsVerified, ["registeredExactly"] = registeredExactly, ["registeredCount"] = registeredCount,
            ["coverageCount"] = CoverageCount, ["coverageChunks"] = CoverageChunkCount, ["coverageBuffer"] = CoverageBufferIndex,
            ["buildingCount"] = BuildingCount, ["buildingChunks"] = BuildingChunkCount, ["buildingBuffer"] = BuildingBufferIndex,
            ["presenceFlags"] = admitted.Flags, ["graphEnqueueExcluded"] = nativeStarted, ["expectedBuffers"] = pendingCount,
            ["observedBuffers"] = observedPending < 0 ? JValue.CreateNull() : new JValue(observedPending),
            ["commandUpperBound"] = commandUpperBound, ["nativeStarted"] = nativeStarted, ["nativeReturned"] = nativeReturned,
            ["jobJoined"] = completed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["outputQualified"] = outputQualified, ["initializationPassed"] = InitializationPassed,
            ["playbackAttempted"] = output?.PlaybackAttempted ?? false, ["playbackStarted"] = output?.PlaybackStarted ?? false,
            ["playbackReturned"] = output?.PlaybackReturned ?? false, ["playbackCallEnded"] = output?.PlaybackCallEnded ?? false,
            ["mayHaveApplied"] = output?.PlaybackStarted ?? false,
            ["arrayAttempts"] = arrayAttempts, ["arraysAcquired"] = arraysAcquired, ["arraysDisposed"] = arraysDisposed,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };

        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection value ? value.Code : "BUILDING_INIT_FAILED"; exceptionType ??= error.GetType().Name; }
        private static bool Contains(EntityQuery[] values, EntityQuery expected)
        { foreach (EntityQuery value in values) if (value.Equals(expected)) return true; return false; }
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "BUILDING_INIT_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private struct InputWitness : IEquatable<InputWitness>
        {
            internal Entity Prefab, RoadEdge;
            internal Temp Temp;
            internal EntityArchetype Archetype;
            internal BuildingData Building;
            internal CoverageData Coverage;
            internal int Flags, CoverageCount, BuildingCount;
            internal bool CoverageBranch, BuildingBranch, Consumption, ObjectDataPresent;
            public bool Equals(InputWitness other) => Prefab == other.Prefab && RoadEdge == other.RoadEdge && Temp.Equals(other.Temp)
                && Archetype.Equals(other.Archetype) && Building.Equals(other.Building) && Coverage.Equals(other.Coverage)
                && Flags == other.Flags && CoverageCount == other.CoverageCount && BuildingCount == other.BuildingCount
                && CoverageBranch == other.CoverageBranch && BuildingBranch == other.BuildingBranch
                && Consumption == other.Consumption && ObjectDataPresent == other.ObjectDataPresent;
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Barrier, Electricity, Water, Coverage, Buildings, DestroyedTypes;
            internal Bindings()
            {
                Require(typeof(NativeInitializeSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "BUILDING_INIT_NATIVE_BUILD_UNQUALIFIED");
                Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier2));
                Electricity = Field("m_ElectricityRoadConnectionGraphSystem", typeof(ElectricityRoadConnectionGraphSystem));
                Water = Field("m_WaterPipeRoadConnectionGraphSystem", typeof(WaterPipeRoadConnectionGraphSystem));
                Coverage = Field("m_CoverageQuery", typeof(EntityQuery)); Buildings = Field("m_BuildingQuery", typeof(EntityQuery));
                DestroyedTypes = Field("m_DestroyedBuildingComponents", typeof(ComponentTypeSet));
            }
            private static FieldInfo Field(string name, Type expected)
            {
                FieldInfo? value = typeof(NativeInitializeSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(NativeInitializeSystem) && value.FieldType == expected
                    && !value.IsStatic && !value.IsInitOnly, "BUILDING_INIT_FIELD_ABI_UNQUALIFIED");
                return value!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class BuildingInitializationBarrier : ModificationBarrier2
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool PlaybackAttempted { get; private set; }
        internal bool PlaybackStarted { get; private set; }
        internal bool PlaybackReturned { get; private set; }
        internal bool PlaybackCallEnded { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("BUILDING_INIT_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("BUILDING_INIT_BARRIER_AUTOPLAY_FORBIDDEN");
        internal EntityCommandBuffer Borrow(int index)
        {
            if (!CreationReturned || DestructionStarted || index < 0 || index >= PendingCount || PendingCount > 2)
                throw new InvalidOperationException("BUILDING_INIT_BUFFER_INDEX_REJECTED");
            return PendingBuffers[index];
        }
        internal void PlaybackAllOnce(int expectedCount, Action requireCurrentScope)
        {
            if (!MainThreadDispatcher.isMainThread || !CreationReturned || DestructionStarted || PlaybackAttempted
                || expectedCount < 0 || expectedCount > 2 || PendingCount != expectedCount || requireCurrentScope == null)
                throw new InvalidOperationException("BUILDING_INIT_PLAYBACK_REJECTED");
            PlaybackAttempted = true;
            try
            {
                for (int index = 0; index < expectedCount; index++)
                {
                    requireCurrentScope();
                    EntityCommandBuffer buffer = Borrow(index);
                    PlaybackStarted = true;
                    buffer.Playback(EntityManager);
                }
                PlaybackReturned = true;
            }
            finally { PlaybackCallEnded = true; }
            // All values are borrowed. Native barrier OnDestroy remains their
            // sole disposer after either successful or partial application.
        }
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("BUILDING_INIT_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }
}
#endif
