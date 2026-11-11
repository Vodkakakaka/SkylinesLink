#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Collections;
using Colossal.Core;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using NativeObject = Game.Objects.Object;
using Transform = Game.Objects.Transform;
using AreaSearch = Game.Areas.SearchSystem;
using NetSearch = Game.Net.SearchSystem;
using ObjectSearch = Game.Objects.SearchSystem;

namespace SkylinesLink.Mod.Native
{
    // The fixed windmill experiment uses the real ValidationSystem with its
    // original queries and jobs, but no shared command or result consumer.
    [DisableAutoCreation]
    public sealed partial class PreviewValidationProducer : ValidationSystem
    {
        private const int MemberCount = 7, RelocationMemberCount = 9, ErrorTypeCount = 31, ErrorPrefabLimit = 64;
        private Bindings? bindings;
        private PreviewValidationBarrier? barrier;
        private Components? result;
        private IconCommandSystem? icons;
        private ToolSystem? tools;
        private ObjectSearch? objectSearch;
        private NetSearch? netSearch;
        private AreaSearch? areaSearch;
        private InstanceCountSystem? instanceCount;
        private CitySystem? city;
        private WaterSystem? water;
        private TerrainSystem? terrain;
        private GroundWaterSystem? groundWater;
        private EntityQuery normal, editor, areas, errorPrefabs;
        private Entity[]? members;
        private Temp[]? relocationTemps;
        private RelocationDefinitionSource? original;
        private bool destinationSeparated;
        private int? originalSearchNodes, originalSearchItems, destinationBoundsCount;
        private Action? scope, liveGraph, markOutput;
        private JobHandle completed;
        private int actor, normalCount, editorCount, areaCount, errorPrefabCount, methodsChecked;
        private int? observedCost, observedMoney, boundsCount, visibleTempCount;
        private uint? normalErrorMask;
        private bool creationStarted, destructionStarted, requested, queryCountsObserved;
        private bool nativeEntered, nativeReturned, nativeJoined, patchQualified;
        private string? methodSetSha256, failureCode;

        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal JobHandle CompletedDependency => nativeJoined ? completed
            : throw new Rejection("PREVIEW_VALIDATION_NATIVE_JOIN_UNCONFIRMED");

        internal void Configure(int actor, PreviewValidationBarrier barrier, Components result, IconCommandSystem icons)
        {
            RequireMainThread();
            Require(bindings == null && !creationStarted && World == null && actor > 0
                && barrier != null && result != null && icons != null, "PREVIEW_VALIDATION_CONFIGURATION_REJECTED");
            RequireUnpatched();
            bindings = new Bindings();
            this.actor = actor; this.barrier = barrier; this.result = result; this.icons = icons;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            RequireMainThread();
            Enabled = false;
            var fields = bindings ?? throw new Rejection("PREVIEW_VALIDATION_CONFIGURATION_MISSING");
            tools = Existing<ToolSystem>();
            objectSearch = Existing<ObjectSearch>();
            netSearch = Existing<NetSearch>();
            areaSearch = Existing<AreaSearch>();
            instanceCount = Existing<InstanceCountSystem>();
            city = Existing<CitySystem>();
            water = Existing<WaterSystem>();
            terrain = Existing<TerrainSystem>();
            groundWater = Existing<GroundWaterSystem>();

            normal = GetEntityQuery(OwnedQuery(false, new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Objects.Relative>(),
                ComponentType.ReadOnly<Game.Objects.Moving>(), ComponentType.ReadOnly<Game.Objects.Stopped>() }));
            editor = GetEntityQuery(OwnedQuery(false, new[] { ComponentType.ReadOnly<Deleted>() }));
            areas = GetEntityQuery(OwnedQuery(true, new[] { ComponentType.ReadOnly<Deleted>() }));
            errorPrefabs = GetEntityQuery(ComponentType.ReadOnly<NotificationIconData>(), ComponentType.ReadOnly<ToolErrorData>());
            var filter = new ObjectGenerationScope { Value = actor };
            normal.SetSharedComponentFilter(filter);
            editor.SetSharedComponentFilter(filter);
            areas.SetSharedComponentFilter(filter);

            fields.Normal.SetValue(this, normal);
            fields.Editor.SetValue(this, editor);
            fields.Areas.SetValue(this, areas);
            fields.ErrorPrefabs.SetValue(this, errorPrefabs);
            fields.Barrier.SetValue(this, barrier);
            fields.Tool.SetValue(this, tools);
            fields.Components.SetValue(this, result);
            fields.Icon.SetValue(this, icons);
            fields.ObjectSearch.SetValue(this, objectSearch);
            fields.NetSearch.SetValue(this, netSearch);
            fields.AreaSearch.SetValue(this, areaSearch);
            fields.InstanceCount.SetValue(this, instanceCount);
            fields.City.SetValue(this, city);
            fields.Water.SetValue(this, water);
            fields.Terrain.SetValue(this, terrain);
            fields.GroundWater.SetValue(this, groundWater);
            fields.ChunkType.SetValue(this, new ChunkType(this));
            fields.EntityData.SetValue(this, new EntityData(this));
            RequireBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            RequireMainThread();
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "PREVIEW_VALIDATION_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void ValidateRelocationOnce(Entity[] graph, Temp[] captured, RelocationDefinitionSource original,
            Action requireScope, Action guardLiveGraph, Action markOutputEntered)
        {
            Require(!requested && captured.Length == RelocationMemberCount
                && captured[0].m_Original == original.Root && captured[1].m_Original == original.NodeEntity
                && captured[2].m_Original == Entity.Null, "PREVIEW_VALIDATION_RELOCATION_CAPTURE_CHANGED");
            relocationTemps = captured;
            this.original = original;
            ValidateOnce(graph, requireScope, guardLiveGraph, markOutputEntered);
        }

        internal void ValidateOnce(Entity[] graph, Action requireScope, Action guardLiveGraph, Action markOutputEntered)
        {
            RequireMainThread();
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (requireScope == null) throw new ArgumentNullException(nameof(requireScope));
            if (guardLiveGraph == null) throw new ArgumentNullException(nameof(guardLiveGraph));
            if (markOutputEntered == null) throw new ArgumentNullException(nameof(markOutputEntered));
            Require(CreationReturned && !destructionStarted && !requested && graph.Length == (original == null ? MemberCount : RelocationMemberCount),
                "PREVIEW_VALIDATION_RUN_REJECTED");
            requested = true;
            members = (Entity[])graph.Clone();
            scope = requireScope; liveGraph = guardLiveGraph; markOutput = markOutputEntered;
            try
            {
                Guard(requireScope);
                Require(ShouldRunSystem(), "PREVIEW_VALIDATION_SYSTEM_GATE_REJECTED");
                Enabled = true;
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_VALIDATION_NATIVE_NOT_RETURNED");
                JobHandle original = Dependency;
                Require(!original.Equals(default(JobHandle)), "PREVIEW_VALIDATION_NATIVE_WRITER_MISSING");
                var completion = original;
                completion.Complete();
                completed = original;
                nativeJoined = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_VALIDATION_RUN_FAILED";
                throw;
            }
            finally
            {
                Enabled = false;
                members = null; scope = null; liveGraph = null; markOutput = null;
            }
        }

        protected override void OnUpdate()
        {
            var requireScope = scope ?? throw new Rejection("PREVIEW_VALIDATION_SCOPE_MISSING");
            var guardLiveGraph = liveGraph ?? throw new Rejection("PREVIEW_VALIDATION_GRAPH_GUARD_MISSING");
            var markOutputEntered = markOutput ?? throw new Rejection("PREVIEW_VALIDATION_OUTPUT_GUARD_MISSING");
            Require(requested && !nativeEntered, "PREVIEW_VALIDATION_NATIVE_REPEATED");
            RequireUnpatched();
            Guard(requireScope);
            guardLiveGraph();
            Guard(requireScope);
            RequireBindings();
            QualifyInputs(EntityManager, members!);
            Guard(requireScope);
            markOutputEntered();
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private void QualifyInputs(EntityManager entities, Entity[] graph)
        {
            Require(tools!.actionMode == GameMode.Game, "PREVIEW_VALIDATION_GAME_MODE_UNQUALIFIED");
            Require(!normal.IsEmptyIgnoreFilter && areas.IsEmptyIgnoreFilter, "PREVIEW_VALIDATION_BRANCH_UNQUALIFIED");
            normalCount = normal.CalculateEntityCount();
            editorCount = editor.CalculateEntityCount();
            areaCount = areas.CalculateEntityCount();
            errorPrefabCount = errorPrefabs.CalculateEntityCount();
            queryCountsObserved = true;
            Require(normalCount == graph.Length && editorCount == graph.Length && areaCount == 0
                && errorPrefabCount > 0 && errorPrefabCount <= ErrorPrefabLimit, "PREVIEW_VALIDATION_QUERY_BOUND");
            RequireMembers(normal, graph);
            RequireMembers(editor, graph);
            // The first controlled city has no player-visible tool preview.
            // Default queries exclude this cohort because every member is Disabled.
            using (var visibleTemps = entities.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
                visibleTempCount = visibleTemps.CalculateEntityCount();
            Require(visibleTempCount == 0, "PREVIEW_VALIDATION_VISIBLE_TEMP_PRESENT");

            var holder = result ?? throw new Rejection("PREVIEW_VALIDATION_RESULT_UNAVAILABLE");
            Require(holder.World == null && !holder.m_ErrorMap.IsCreated && holder.m_ErrorMapDeps.Equals(default(JobHandle))
                && barrier!.PendingCount == 0, "PREVIEW_VALIDATION_RESULT_ALREADY_USED");
            Require(objectSearch!.GetStaticSearchTree(readOnly: true, out _).IsCreated
                && netSearch!.GetNetSearchTree(readOnly: true, out _).IsCreated
                && areaSearch!.GetSearchTree(readOnly: true, out _).IsCreated
                && instanceCount!.GetInstanceCounts(readOnly: true, out _).IsCreated
                && groundWater!.GetMap(readOnly: true, out _).IsCreated, "PREVIEW_VALIDATION_SPATIAL_INPUT_UNAVAILABLE");
            TerrainHeightData height = terrain!.GetHeightData();
            WaterSurfaceData<SurfaceWater> surface = water!.GetSurfaceData(out _);
            Require(height.isCreated && height.resolution.x > 2 && height.resolution.z > 2
                && surface.isCreated && surface.hasDepths && surface.resolution.x > 2 && surface.resolution.z > 2,
                "PREVIEW_VALIDATION_SURFACE_INPUT_UNAVAILABLE");

            Entity cityEntity = city!.City;
            Require(cityEntity != Entity.Null && entities.Exists(cityEntity) && entities.HasComponent<PlayerMoney>(cityEntity),
                "PREVIEW_VALIDATION_CITY_MONEY_UNAVAILABLE");
            observedMoney = entities.GetComponentData<PlayerMoney>(cityEntity).money;
            QualifyGraph(entities, graph);
            ReadErrorPrefabs(entities);
        }

        private void QualifyGraph(EntityManager entities, Entity[] graph)
        {
            long cost = 0;
            int objectBounds = 0, destinationBounds = 0;
            float2 destinationMin = new float2(float.PositiveInfinity);
            float2 destinationMax = new float2(float.NegativeInfinity);
            for (int i = 0; i < graph.Length; i++)
            {
                Entity entity = graph[i];
                Require(entity != Entity.Null && entities.Exists(entity) && Array.IndexOf(graph, entity) == i
                    && entities.HasComponent<Unity.Entities.Disabled>(entity) && entities.HasComponent<Updated>(entity)
                    && entities.HasComponent<ObjectGenerationScope>(entity) && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor
                    && entities.HasComponent<Temp>(entity) && !entities.HasComponent<Deleted>(entity), "PREVIEW_VALIDATION_GRAPH_MEMBER_CHANGED");
                Require(!entities.HasComponent<Game.Common.Native>(entity) && !entities.HasComponent<Error>(entity)
                    && !entities.HasComponent<Warning>(entity) && !entities.HasComponent<Override>(entity)
                    && !entities.HasComponent<Game.Net.Edge>(entity) && !entities.HasComponent<Game.Net.EdgeGeometry>(entity)
                    && !entities.HasComponent<Game.Net.Lane>(entity) && !entities.HasComponent<Game.Areas.Area>(entity)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(entity)
                    && !entities.HasComponent<Game.Routes.TransportStop>(entity) && !entities.HasBuffer<Game.Routes.RouteSegment>(entity)
                    && !entities.HasComponent<Game.Simulation.WaterSourceData>(entity) && !entities.HasComponent<Brush>(entity)
                    && !entities.HasComponent<Game.Objects.OutsideConnection>(entity) && !entities.HasComponent<Game.Objects.Stack>(entity),
                    "PREVIEW_VALIDATION_UNQUALIFIED_GRAPH_BRANCH");
                Temp temp = entities.GetComponentData<Temp>(entity);
                TempFlags expected = i == 0 ? TempFlags.Create | TempFlags.Essential : i == 1 ? TempFlags.Create | TempFlags.IsLast : TempFlags.Create;
                Require(temp.m_Cost >= 0 && (relocationTemps == null
                    ? temp.m_Original == Entity.Null && temp.m_Flags == expected
                    : temp.Equals(relocationTemps[i])),
                    "PREVIEW_VALIDATION_TEMP_CHANGED");
                cost += temp.m_Cost;
                Require(cost <= int.MaxValue, "PREVIEW_VALIDATION_COST_BOUND");
                Require(entities.HasComponent<Building>(entity) == (i == 0), "PREVIEW_VALIDATION_BUILDING_ROLE_CHANGED");
                bool node = i == 1 || original != null && i == 2;
                if (node)
                {
                    Require(entities.HasComponent<Game.Net.Node>(entity) && !entities.HasComponent<NativeObject>(entity)
                        && !entities.HasComponent<Transform>(entity), "PREVIEW_VALIDATION_NODE_ROLE_CHANGED");
                    if (original != null && i == 2)
                        ExtendDestination(entities.GetComponentData<Game.Net.NodeGeometry>(entity).m_Bounds);
                    continue;
                }
                var bounds = RequireObjectBounds(entities, entity);
                if (original != null && (temp.m_Flags & TempFlags.Delete) == 0)
                {
                    ExtendDestination(bounds);
                    destinationBounds++;
                }
                objectBounds++;
            }
            if (original != null)
            {
                original.RequireSeparateDestination(destinationMin, destinationMax);
                destinationBoundsCount = destinationBounds;
                RequireOriginalsOutsideSearch(original, new Bounds2(destinationMin, destinationMax));
                destinationSeparated = true;
            }
            observedCost = (int)cost;
            boundsCount = objectBounds;

            void ExtendDestination(Bounds3 bounds)
            {
                Require(math.all(math.isfinite(bounds.min)) && math.all(math.isfinite(bounds.max))
                    && math.all(bounds.min <= bounds.max), "PREVIEW_VALIDATION_DESTINATION_BOUNDS_INVALID");
                destinationMin = math.min(destinationMin, bounds.min.xz);
                destinationMax = math.max(destinationMax, bounds.max.xz);
            }
        }

        private void RequireOriginalsOutsideSearch(RelocationDefinitionSource original, Bounds2 destination)
        {
            var tree = objectSearch!.GetStaticSearchTree(readOnly: true, out var writers);
            writers.Complete();
            Require(tree.IsCreated, "PREVIEW_VALIDATION_ORIGINAL_SEARCH_UNAVAILABLE");
            var visit = new OriginalOverlapVisitor(original, destination);
            try { tree.Iterate(ref visit); }
            finally { originalSearchNodes = visit.Nodes; originalSearchItems = visit.Items; }
        }

        private struct OriginalOverlapVisitor : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            private readonly RelocationDefinitionSource original;
            private readonly Bounds2 destination;
            internal int Nodes, Items;

            internal OriginalOverlapVisitor(RelocationDefinitionSource original, Bounds2 destination)
            { this.original = original; this.destination = destination; Nodes = 0; Items = 0; }

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                Require(++Nodes <= EmptyNetBoundsVisitor.NodeBudget, "PREVIEW_VALIDATION_ORIGINAL_SEARCH_NODE_BUDGET");
                return MathUtils.Intersect(bounds.m_Bounds.xz, destination);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                Require(++Items <= EmptyNetBoundsVisitor.ItemBudget, "PREVIEW_VALIDATION_ORIGINAL_SEARCH_ITEM_BUDGET");
                Require(!original.IsOriginalObject(item) || !MathUtils.Intersect(bounds.m_Bounds.xz, destination),
                    "PREVIEW_VALIDATION_ORIGINAL_SPATIAL_HIT");
            }
        }

        private static Bounds3 RequireObjectBounds(EntityManager entities, Entity entity)
        {
            Require(entities.HasComponent<NativeObject>(entity) && entities.HasComponent<Transform>(entity)
                && entities.HasComponent<PrefabRef>(entity), "PREVIEW_VALIDATION_OBJECT_ROLE_CHANGED");
            Entity prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
            Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && entities.HasComponent<ObjectGeometryData>(prefab), "PREVIEW_VALIDATION_OBJECT_GEOMETRY_UNAVAILABLE");
            Transform transform = entities.GetComponentData<Transform>(entity);
            var geometry = entities.GetComponentData<ObjectGeometryData>(prefab);
            Require(math.all(math.isfinite(transform.m_Position)) && math.all(math.isfinite(transform.m_Rotation.value))
                && math.abs(math.lengthsq(transform.m_Rotation.value) - 1f) <= 0.0001f,
                "PREVIEW_VALIDATION_OBJECT_TRANSFORM_INVALID");
            Bounds3 bounds = Game.Objects.ObjectUtils.CalculateBounds(transform.m_Position, transform.m_Rotation, geometry);
            Require(math.all(math.isfinite(bounds.min)) && math.all(math.isfinite(bounds.max))
                && math.all(bounds.min <= bounds.max), "PREVIEW_VALIDATION_OBJECT_BOUNDS_INVALID");
            return bounds;
        }

        private void ReadErrorPrefabs(EntityManager entities)
        {
            uint mask = 0;
            using (var prefabs = errorPrefabs.ToEntityArray(Allocator.Temp))
            {
                Require(prefabs.Length == errorPrefabCount, "PREVIEW_VALIDATION_ERROR_PREFABS_CHANGED");
                foreach (Entity prefab in prefabs)
                {
                    var error = entities.GetComponentData<ToolErrorData>(prefab);
                    if ((error.m_Flags & ToolErrorFlags.DisableInGame) != 0) continue;
                    int index = (int)error.m_Error;
                    Require(index >= 0 && index < ErrorTypeCount, "PREVIEW_VALIDATION_ERROR_PREFAB_INDEX");
                    mask |= 1u << index;
                }
            }
            normalErrorMask = mask;
            const uint required = (1u << (int)ErrorType.OverlapExisting) | (1u << (int)ErrorType.NotEnoughMoney);
            Require((mask & required) == required, "PREVIEW_VALIDATION_REQUIRED_ERROR_PREFAB_MISSING");
        }

        private static void RequireMembers(EntityQuery query, Entity[] graph)
        {
            using var values = query.ToEntityArray(Allocator.Temp);
            Require(values.Length == graph.Length, "PREVIEW_VALIDATION_QUERY_CHANGED");
            foreach (Entity entity in values)
                Require(Array.IndexOf(graph, entity) >= 0, "PREVIEW_VALIDATION_FOREIGN_QUERY_MEMBER");
        }

        private T Existing<T>() where T : ComponentSystemBase
        {
            var system = World.GetExistingSystemManaged<T>();
            if (system == null || !ReferenceEquals(system.World, World))
                throw new Rejection("PREVIEW_VALIDATION_SOURCE_DEPENDENCY_MISSING");
            return system;
        }

        private static EntityQueryDesc OwnedQuery(bool area, ComponentType[] none)
        {
            var all = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(),
                ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() };
            if (area)
            {
                Array.Resize(ref all, all.Length + 1);
                all[all.Length - 1] = ComponentType.ReadOnly<Game.Areas.Area>();
            }
            return new EntityQueryDesc { All = all, None = none, Options = EntityQueryOptions.IncludeDisabledEntities };
        }

        private void RequireBindings()
        {
            var fields = bindings ?? throw new Rejection("PREVIEW_VALIDATION_CONFIGURATION_MISSING");
            var registered = EntityQueries;
            Require(registered.Length == 4 && QueryMatches(fields.Normal, normal, registered, true)
                && QueryMatches(fields.Editor, editor, registered, true) && QueryMatches(fields.Areas, areas, registered, true)
                && QueryMatches(fields.ErrorPrefabs, errorPrefabs, registered, false)
                && SourceMatches(fields.Tool, tools) && SourceMatches(fields.ObjectSearch, objectSearch)
                && SourceMatches(fields.NetSearch, netSearch) && SourceMatches(fields.AreaSearch, areaSearch)
                && SourceMatches(fields.InstanceCount, instanceCount) && SourceMatches(fields.City, city)
                && SourceMatches(fields.Water, water) && SourceMatches(fields.Terrain, terrain)
                && SourceMatches(fields.GroundWater, groundWater)
                && barrier != null && barrier.CreationReturned && ReferenceEquals(barrier.World, World)
                && ReferenceEquals(fields.Barrier.GetValue(this), barrier)
                && result != null && result.World == null && ReferenceEquals(fields.Components.GetValue(this), result)
                && icons != null && icons.World == null && ReferenceEquals(fields.Icon.GetValue(this), icons),
                "PREVIEW_VALIDATION_BINDINGS_CHANGED");
        }

        private bool SourceMatches<T>(FieldInfo field, T? value) where T : ComponentSystemBase =>
            value != null && ReferenceEquals(value.World, World) && ReferenceEquals(field.GetValue(this), value)
                && ReferenceEquals(World.GetExistingSystemManaged<T>(), value);

        private bool QueryMatches(FieldInfo field, EntityQuery query, EntityQuery[] registered, bool filtered)
        {
            int matches = 0;
            foreach (EntityQuery value in registered) if (value.Equals(query)) matches++;
            return matches == 1 && query.HasFilter() == filtered && ((EntityQuery)field.GetValue(this)!).Equals(query);
        }

        private void RequireUnpatched()
        {
            var patch = PreviewValidationPatchProbe.Observe();
            patchQualified = patch.Qualified;
            methodsChecked = patch.MethodsChecked;
            methodSetSha256 = patch.MethodSetSha256;
            Require(patchQualified, patch.Reason);
        }

        private void Guard(Action requireScope)
        {
            requireScope();
            Require(World != null && World.IsCreated && !destructionStarted, "PREVIEW_VALIDATION_SOURCE_UNAVAILABLE");
        }

        internal JObject Snapshot()
        {
            var result = new JObject
            {
                ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
                ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["patchQualified"] = patchQualified,
                ["methodsChecked"] = methodsChecked, ["methodSetSha256"] = methodSetSha256,
                ["normalCount"] = Count(normalCount), ["editorCount"] = Count(editorCount), ["areaCount"] = Count(areaCount),
                ["errorPrefabCount"] = Count(errorPrefabCount), ["normalErrorPrefabMask"] = normalErrorMask,
                ["observedCost"] = observedCost, ["observedMoney"] = observedMoney, ["objectBoundsCount"] = boundsCount,
                ["visibleTempCount"] = visibleTempCount,
                ["creationReturned"] = CreationReturned, ["destructionReturned"] = DestructionReturned, ["failureCode"] = failureCode
            };
            if (original != null)
            {
                result["destinationSeparated"] = destinationSeparated;
                result["destinationBoundsCount"] = destinationBoundsCount;
                result["originalSearchNodes"] = originalSearchNodes;
                result["originalSearchItems"] = originalSearchItems;
            }
            return result;
        }

        private JToken Count(int value) => queryCountsObserved ? new JValue(value) : JValue.CreateNull();
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "PREVIEW_VALIDATION_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : InvalidOperationException
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Normal = Field("m_UpdatedQuery", typeof(EntityQuery));
            internal readonly FieldInfo Editor = Field("m_UpdatedEditorQuery", typeof(EntityQuery));
            internal readonly FieldInfo Areas = Field("m_UpdatedAreaQuery", typeof(EntityQuery));
            internal readonly FieldInfo ErrorPrefabs = Field("m_ToolErrorPrefabQuery", typeof(EntityQuery));
            internal readonly FieldInfo Barrier = Field("m_ModificationBarrier", typeof(ModificationEndBarrier));
            internal readonly FieldInfo Tool = Field("m_ToolSystem", typeof(ToolSystem));
            internal readonly FieldInfo Components = Field("m_Components", typeof(ValidationSystem.Components));
            internal readonly FieldInfo Icon = Field("m_IconCommandSystem", typeof(IconCommandSystem));
            internal readonly FieldInfo ObjectSearch = Field("m_ObjectSearchSystem", typeof(Game.Objects.SearchSystem));
            internal readonly FieldInfo NetSearch = Field("m_NetSearchSystem", typeof(Game.Net.SearchSystem));
            internal readonly FieldInfo AreaSearch = Field("m_AreaSearchSystem", typeof(Game.Areas.SearchSystem));
            internal readonly FieldInfo InstanceCount = Field("m_InstanceCountSystem", typeof(InstanceCountSystem));
            internal readonly FieldInfo City = Field("m_CitySystem", typeof(CitySystem));
            internal readonly FieldInfo Water = Field("m_WaterSystem", typeof(WaterSystem));
            internal readonly FieldInfo Terrain = Field("m_TerrainSystem", typeof(TerrainSystem));
            internal readonly FieldInfo GroundWater = Field("m_GroundWaterSystem", typeof(GroundWaterSystem));
            internal readonly FieldInfo ChunkType = Field("m_ChunkType", typeof(ValidationSystem.ChunkType));
            internal readonly FieldInfo EntityData = Field("m_EntityData", typeof(ValidationSystem.EntityData));

            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(ValidationSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_VALIDATION_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
