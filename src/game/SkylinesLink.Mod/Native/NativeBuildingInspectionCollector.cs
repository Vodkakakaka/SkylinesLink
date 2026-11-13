using System;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using Game.UI.InGame;
using SkylinesLink.Application.Buildings;
using SkylinesLink.Application.Context;
using Unity.Entities;
using Unity.Mathematics;
using Building = Game.Buildings.Building;
using InstalledUpgrade = Game.Buildings.InstalledUpgrade;
using SubObject = Game.Objects.SubObject;
using SubNet = Game.Net.SubNet;
using Temp = Game.Tools.Temp;

namespace SkylinesLink.Mod.Native
{
    /// <summary>只读一个当前建筑及固定的直接引用前缀；不刷新UI、不调用工具、不创建系统。</summary>
    public sealed class NativeBuildingInspectionCollector
    {
        private readonly UpdateSystem updates;
        private readonly WorldContextTracker context;
        private readonly BuildingSelectionScopeWitness selectionWitness;
        private SelectedInfoUISystem? selectionSource;
        private SystemHandle selectionSourceHandle;
        public NativeBuildingInspectionCollector(UpdateSystem updates, WorldContextTracker context)
        { this.updates = updates ?? throw new ArgumentNullException(nameof(updates)); this.context = context ?? throw new ArgumentNullException(nameof(context));
            selectionWitness = new BuildingSelectionScopeWitness(context); }

        // Called only at lifetime start, a newly observed Ready scope, or a
        // selectedInfo request that discovers a missing/replaced source.
        public void ObserveSelectionScope(WorldContextSnapshot scope)
        {
            if (!MainThreadDispatcher.isMainThread || context.IsStopped) return;
            try
            {
                var manager = GameManager.instance; var world = updates.World;
                var source = world != null && world.IsCreated ? world.GetExistingSystemManaged<SelectedInfoUISystem>() : null;
                if (manager == null || source == null || !ReferenceEquals(source.World, world))
                { selectionWitness.TryDetach(); selectionSource = null; return; }
                if (ReferenceEquals(selectionSource, source) && source.SystemHandle == selectionSourceHandle && selectionWitness.IsBoundTo(scope)) return;
                selectionSource = source;
                SystemHandle handle = source.SystemHandle;
                selectionSourceHandle = handle;
                Action<Entity, Entity, float3>? bridge = null;
                bool bound = selectionWitness.TryBind(scope, observed => {
                    bridge = (entity, prefab, position) => {
                        try
                        {
                            if (!MainThreadDispatcher.isMainThread || context.IsStopped || scope.State != "ready" ||
                                !ReferenceEquals(manager, GameManager.instance) || manager.isGameLoading || manager.state != GameManager.State.WorldReady || manager.gameMode != GameMode.Game ||
                                world == null || !world.IsCreated || !ReferenceEquals(updates.World, world) || !ReferenceEquals(world.GetExistingSystemManaged<UpdateSystem>(), updates) ||
                                !ReferenceEquals(world.GetExistingSystemManaged<SelectedInfoUISystem>(), source) || !ReferenceEquals(selectionSource, source) || source.SystemHandle != handle ||
                                source.selectedEntity != entity || source.selectedPrefab != prefab || !math.all(source.selectedPosition == position)) return;
                            // The bridge is reached only through the native change
                            // signal after RefreshSelection/binding update. The
                            // witness checks this frozen scope and binding ticket.
                            observed();
                        }
                        catch { /* An unavailable optional source never disrupts the native UI update. */ }
                    };
                    source.eventSelectionChanged += bridge;
                }, _ => { if (bridge != null) source.eventSelectionChanged -= bridge; });
                if (!bound) selectionSource = null;
            }
            catch { selectionWitness.TryDetach(); selectionSource = null; }
        }
        public void StopSelectionSource() => selectionWitness.RequestStop();
        public bool ReleaseSelectionSource()
        {
            selectionWitness.RequestStop();
            bool released = selectionWitness.TryDetach();
            if (released) selectionSource = null;
            return released;
        }
        public bool IsSelectionSourceReleased => selectionWitness.IsReleased;

        public BuildingInspectionCapture Capture(WorldContextSnapshot expected, BuildingInspectionQuery query)
        {
            if (!MainThreadDispatcher.isMainThread) throw Failure("SOURCE_UNAVAILABLE");
            try
            {
                RequireScope(expected);
                if (!query.SelectedInfo && (query.InstanceId != expected.InstanceId || query.Generation != expected.Generation)) throw Failure("CONTEXT_MISMATCH");
                long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var manager = GameManager.instance;
                var world = updates.World;
                if (manager == null || world == null || !world.IsCreated) throw Failure("SOURCE_UNAVAILABLE");
                var loader = world.GetExistingSystemManaged<LoadGameSystem>();
                var simulation = world.GetExistingSystemManaged<SimulationSystem>();
                RequireSource(manager, world, loader, simulation);
                uint frameStart = simulation!.frameIndex;
                var entities = world.EntityManager;
                SelectedInfoUISystem? selection = null;
                Entity selectedPrefab = Entity.Null;
                Entity entity;
                if (query.SelectedInfo)
                {
                    selection = world.GetExistingSystemManaged<SelectedInfoUISystem>();
                    if (selection == null || !ReferenceEquals(selection.World, world))
                    { ObserveSelectionScope(expected); throw Failure("SOURCE_UNAVAILABLE"); }
                    if (!ReferenceEquals(selectionSource, selection) || selection.SystemHandle != selectionSourceHandle || !selectionWitness.IsBoundTo(expected)) ObserveSelectionScope(expected);
                    if (!ReferenceEquals(selectionSource, selection) || selection.SystemHandle != selectionSourceHandle || !selectionWitness.IsBoundTo(expected))
                        throw Failure("SOURCE_UNAVAILABLE");
                    if (!selectionWitness.HasProof(expected)) throw Failure("SELECTION_SOURCE_NOT_READY");
                    // The panel publishes a filtered target. ToolSystem.selected is
                    // not equivalent; refreshing/filtering it here can change selection.
                    entity = selection.selectedEntity; selectedPrefab = selection.selectedPrefab;
                    if (entity == Entity.Null) throw Failure("NO_SELECTION");
                }
                else entity = new Entity { Index = query.Index, Version = query.Version };
                if (!entities.Exists(entity)) throw Failure("ENTITY_UNAVAILABLE");
                string? unsupported = entities.HasComponent<Deleted>(entity) ? "DELETED" :
                    entities.HasComponent<Temp>(entity) ? "PREVIEW" : !entities.HasComponent<Building>(entity) ? "NOT_BUILDING" :
                    !entities.HasComponent<Transform>(entity) ? "MISSING_TRANSFORM" :
                    !entities.HasComponent<PrefabRef>(entity) ? "MISSING_PREFAB" : null;
                BuildingInspectionData? data = null;
                Entity prefab = Entity.Null;
                Transform original = default;
                if (unsupported == null)
                {
                    prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (prefab == Entity.Null || !entities.Exists(prefab) || entities.HasComponent<Deleted>(prefab)) unsupported = "MISSING_PREFAB";
                    else
                    {
                        if (selection != null && selectedPrefab != prefab) throw Failure("SELECTION_CHANGED");
                        original = entities.GetComponentData<Transform>(entity);
                        Building building = entities.GetComponentData<Building>(entity);
                        if (!math.all(math.isfinite(original.m_Position)) || !math.all(math.isfinite(original.m_Rotation.value)) ||
                            !math.isfinite(building.m_CurvePosition)) throw Failure("CAPTURE_FAILED");
                        var prefabs = world.GetExistingSystemManaged<PrefabSystem>();
                        PrefabBase? managed = null;
                        string metadataIssue = "MISSING_COMPONENT";
                        try
                        {
                            if (prefabs != null && ReferenceEquals(prefabs.World, world))
                            {
                                metadataIssue = "MISSING_VALUE";
                                if (prefabs.TryGetPrefab<PrefabBase>(prefab, out var candidate) && candidate != null &&
                                    prefabs.TryGetEntity(candidate, out var roundtrip) && roundtrip == prefab) managed = candidate;
                            }
                        }
                        catch { metadataIssue = "READ_FAILED"; }
                        var name = managed == null ? BuildingTextFact.Unavailable(metadataIssue) : BuildingTextFact.Read(() => managed.name, 128);
                        var typeName = managed == null ? BuildingTextFact.Unavailable(metadataIssue) : BuildingTextFact.Read(() => managed.GetType().Name, 128);
                        // GetPrefabID.ToString includes the raw type/name. Bound
                        // those first so an oversized native name is not copied again.
                        var nativeId = name.Value == null ? BuildingTextFact.Unavailable(name.Reason!) : typeName.Value == null ?
                            BuildingTextFact.Unavailable(typeName.Reason!) : BuildingTextFact.Read(() => managed!.GetPrefabID().ToString(), 512);
                        int? width = null, depth = null; string? lotIssue = "MISSING_COMPONENT";
                        if (entities.HasComponent<BuildingData>(prefab))
                        {
                            int2 lot = entities.GetComponentData<BuildingData>(prefab).m_LotSize;
                            if (lot.x >= 0 && lot.y >= 0) { width = lot.x; depth = lot.y; lotIssue = null; }
                            else lotIssue = "INVALID_VALUE";
                        }
                        uint? cost = null; string? costIssue = "MISSING_COMPONENT";
                        if (entities.HasComponent<PlaceableObjectData>(prefab)) { cost = entities.GetComponentData<PlaceableObjectData>(prefab).m_ConstructionCost; costIssue = null; }
                        // Absence of the enableable Locked marker means no active
                        // marker, not that placement or content eligibility passed.
                        bool locked = entities.HasComponent<Locked>(prefab) && entities.IsComponentEnabled<Locked>(prefab);
                        float3 p = original.m_Position; float4 q = original.m_Rotation.value;
                        data = new BuildingInspectionData(Reference(prefab)!, name, nativeId, width, depth, lotIssue, cost, costIssue, locked, null,
                            new double[] { p.x, p.y, p.z }, new double[] { q.x, q.y, q.z, q.w }, Reference(building.m_RoadEdge),
                            building.m_RoadEdge != Entity.Null && entities.Exists(building.m_RoadEdge), building.m_CurvePosition,
                            building.m_OptionMask, (byte)building.m_Flags, ReadSubObjects(entities, entity, query.RelationLimit),
                            ReadSubNets(entities, entity, query.RelationLimit), ReadUpgrades(entities, entity, query.RelationLimit), ReadBounds(entities, prefab));
                    }
                }
                RequireSource(manager, world, loader, simulation); RequireScope(expected);
                if (!entities.Exists(entity)) throw Failure("ENTITY_UNAVAILABLE");
                if (selection != null && (!ReferenceEquals(world.GetExistingSystemManaged<SelectedInfoUISystem>(), selection) ||
                    selection.SystemHandle != selectionSourceHandle || selection.selectedEntity != entity || selection.selectedPrefab != selectedPrefab ||
                    !selectionWitness.HasProof(expected))) throw Failure("SELECTION_CHANGED");
                if (data != null && (entities.HasComponent<Deleted>(entity) || entities.HasComponent<Temp>(entity) ||
                    !entities.HasComponent<Building>(entity) || !entities.HasComponent<PrefabRef>(entity) || !entities.HasComponent<Transform>(entity) ||
                    entities.GetComponentData<PrefabRef>(entity).m_Prefab != prefab || !SameTransform(original, entities.GetComponentData<Transform>(entity))))
                    throw Failure("SOURCE_UNAVAILABLE");
                return new BuildingInspectionCapture(expected, query, Reference(entity)!, Game.Version.current.fullVersion,
                    started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), frameStart, simulation!.frameIndex, data, unsupported);
            }
            catch (BuildingInspectionReadFailure) { throw; }
            catch (Exception) { throw Failure("CAPTURE_FAILED"); }
        }

        private static BuildingBoundsFact ReadBounds(EntityManager entities, Entity prefab)
        {
            try
            {
                if (!entities.HasComponent<ObjectGeometryData>(prefab)) return new BuildingBoundsFact(null, null, "MISSING_COMPONENT");
                var bounds = entities.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
                if (!math.all(math.isfinite(bounds.min)) || !math.all(math.isfinite(bounds.max)) || !math.all(bounds.min <= bounds.max))
                    return new BuildingBoundsFact(null, null, "INVALID_VALUE");
                return new BuildingBoundsFact(new double[] { bounds.min.x, bounds.min.y, bounds.min.z },
                    new double[] { bounds.max.x, bounds.max.y, bounds.max.z }, null);
            }
            catch { return new BuildingBoundsFact(null, null, "READ_FAILED"); }
        }

        private static BuildingDirectRelations ReadSubObjects(EntityManager entities, Entity entity, int limit)
        {
            try
            {
                if (!entities.HasBuffer<SubObject>(entity)) return BuildingDirectRelations.Missing();
                var buffer = entities.GetBuffer<SubObject>(entity, true); int length = buffer.Length, count = Math.Min(length, limit);
                var rows = new BuildingRelationEntry[count];
                for (int i = 0; i < count; i++) { Entity target = buffer[i].m_SubObject; rows[i] = new BuildingRelationEntry(Reference(target), target != Entity.Null && entities.Exists(target)); }
                return new BuildingDirectRelations(true, length, rows, length == count ? null : "LIMIT");
            }
            catch { return BuildingDirectRelations.Failed(); }
        }
        private static BuildingDirectRelations ReadSubNets(EntityManager entities, Entity entity, int limit)
        {
            try
            {
                if (!entities.HasBuffer<SubNet>(entity)) return BuildingDirectRelations.Missing();
                var buffer = entities.GetBuffer<SubNet>(entity, true); int length = buffer.Length, count = Math.Min(length, limit);
                var rows = new BuildingRelationEntry[count];
                for (int i = 0; i < count; i++) { Entity target = buffer[i].m_SubNet; rows[i] = new BuildingRelationEntry(Reference(target), target != Entity.Null && entities.Exists(target)); }
                return new BuildingDirectRelations(true, length, rows, length == count ? null : "LIMIT");
            }
            catch { return BuildingDirectRelations.Failed(); }
        }
        private static BuildingDirectRelations ReadUpgrades(EntityManager entities, Entity entity, int limit)
        {
            try
            {
                if (!entities.HasBuffer<InstalledUpgrade>(entity)) return BuildingDirectRelations.Missing();
                var buffer = entities.GetBuffer<InstalledUpgrade>(entity, true); int length = buffer.Length, count = Math.Min(length, limit);
                var rows = new BuildingRelationEntry[count];
                for (int i = 0; i < count; i++) { var item = buffer[i]; rows[i] = new BuildingRelationEntry(Reference(item.m_Upgrade), item.m_Upgrade != Entity.Null && entities.Exists(item.m_Upgrade), item.m_OptionMask); }
                return new BuildingDirectRelations(true, length, rows, length == count ? null : "LIMIT");
            }
            catch { return BuildingDirectRelations.Failed(); }
        }
        private static BuildingEntityReference? Reference(Entity entity) => entity == Entity.Null ? null : new BuildingEntityReference(entity.Index, entity.Version);
        private static bool SameTransform(Transform a, Transform b) => math.all(a.m_Position == b.m_Position) && math.all(a.m_Rotation.value == b.m_Rotation.value);
        private void RequireScope(WorldContextSnapshot expected)
        {
            var current = context.Read();
            if (context.IsStopped || current.State != "ready" || expected.State != "ready" || current.InstanceId != expected.InstanceId ||
                current.Generation != expected.Generation || expected.CityId != null && expected.CityId != current.CityId) throw Failure("SCOPE_CHANGED");
        }
        private void RequireSource(GameManager manager, World world, LoadGameSystem? loader, SimulationSystem? simulation)
        {
            if (!world.IsCreated || !ReferenceEquals(manager, GameManager.instance) || !ReferenceEquals(updates.World, world) ||
                !ReferenceEquals(world.GetExistingSystemManaged<UpdateSystem>(), updates) || loader == null || simulation == null ||
                !ReferenceEquals(loader.World, world) || !ReferenceEquals(simulation.World, world) ||
                !ReferenceEquals(world.GetExistingSystemManaged<LoadGameSystem>(), loader) || !ReferenceEquals(world.GetExistingSystemManaged<SimulationSystem>(), simulation) ||
                manager.state != GameManager.State.WorldReady || manager.gameMode != GameMode.Game || manager.isGameLoading) throw Failure("SOURCE_UNAVAILABLE");
        }
        private static BuildingInspectionReadFailure Failure(string code) => new BuildingInspectionReadFailure(code);
    }
    public sealed class BuildingInspectionReadFailure : Exception
    {
        internal BuildingInspectionReadFailure(string code) : base("The native building observation is unavailable.") { Code = code; }
        public string Code { get; }
    }
}
