#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class NodeGeometryProducer : GeometrySystem
    {
        private Bindings? bindings;
        private TerrainSystem? terrain;
        private EntityQuery updatedEdges, updatedNodes, allEdges, allNodes;
        private int actor;
        private bool creationStarted, destructionStarted, emptyRequested, generateRequested;
        private bool emptyEntered, emptyReturned, emptyJoined, emptyPassed, emptyMode;
        private bool nativeEntered, nativeReturned, nativeJoined, queriesVerified, patchQualified;
        private Action? currentScope, requireLiveGraph;
        private GeometryRun[] cohort = Array.Empty<GeometryRun>();
        private Entity root;
        private Temp? rootTemp;
        private string? failureCode;

        internal bool CreationStarted => creationStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => (emptyEntered && !emptyJoined) || (nativeEntered && !nativeJoined);
        // SystemState owns the queries. The outer registration attests their
        // destruction. GeometrySystem schedules its own scratch disposals.
        internal bool StorageReleased => true;
        internal bool GeometryPassed { get; private set; }

        internal void Configure(int actor)
        {
            RequireMainThread();
            Require(bindings == null && !creationStarted && World == null && actor > 0,
                "NODE_GEOMETRY_CONFIGURATION_REJECTED");
            RequireUnpatched();
            bindings = new Bindings();
            this.actor = actor;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            RequireMainThread();
            Enabled = false;
            var fields = bindings ?? throw new Rejection("NODE_GEOMETRY_CONFIGURATION_MISSING");
            terrain = World.GetExistingSystemManaged<TerrainSystem>();
            Require(terrain != null && ReferenceEquals(terrain.World, World), "NODE_GEOMETRY_TERRAIN_UNAVAILABLE");

            updatedEdges = CreateScopedQuery(ComponentType.ReadWrite<EdgeGeometry>(), true);
            updatedNodes = CreateScopedQuery(ComponentType.ReadWrite<NodeGeometry>(), true);
            allEdges = CreateScopedQuery(ComponentType.ReadWrite<EdgeGeometry>(), false);
            allNodes = CreateScopedQuery(ComponentType.ReadWrite<NodeGeometry>(), false);
            fields.Terrain.SetValue(this, terrain);
            fields.UpdatedEdges.SetValue(this, updatedEdges);
            fields.UpdatedNodes.SetValue(this, updatedNodes);
            fields.AllEdges.SetValue(this, allEdges);
            fields.AllNodes.SetValue(this, allNodes);
            fields.Loaded.SetValue(this, false);
            RequireBindings();
            CreationReturned = true;
        }

        private EntityQuery CreateScopedQuery(ComponentType geometry, bool updated)
        {
            var scope = ComponentType.ReadOnly<ObjectGenerationScope>();
            var disabled = ComponentType.ReadOnly<Unity.Entities.Disabled>();
            var query = GetEntityQuery(new EntityQueryDesc {
                All = updated ? new[] { geometry, scope, disabled, ComponentType.ReadOnly<Updated>() }
                    : new[] { geometry, scope, disabled },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            return query;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "NODE_GEOMETRY_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void ProbeEmptyUpdate(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && !destructionStarted && !emptyRequested && !generateRequested,
                "NODE_GEOMETRY_EMPTY_REJECTED");
            emptyRequested = true;
            try
            {
                RunNative(scope, true);
                RequireEmpty(scope);
                emptyPassed = true;
            }
            catch (Exception error) { Record(error); throw; }
        }

        internal void GenerateOnce(CompositionSelectionReceipt receipt, Entity root, Action scope)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            Generate(root, new[] { new GeometryRun(receipt, requiresOwner: true) }, scope, null, null);
        }

        internal (NodeGeometry Old, NodeGeometry New) GenerateRelocationOnce(GeneratedSubObjectsPayload relocation,
            CompositionSelectionReceipt oldNode, CompositionSelectionReceipt newNode, Action scope, Action requireGraph)
        {
            Require(relocation.IsRelocation && ReferenceEquals(relocation.Source, World) && relocation.Actor == actor
                && oldNode.Node == relocation.OldNode && newNode.Node == relocation.Node,
                "NODE_GEOMETRY_RELOCATION_COHORT_REJECTED");
            var old = new GeometryRun(oldNode, requiresOwner: false, relocation.CapturedParentTemp(oldNode.Node));
            var next = new GeometryRun(newNode, requiresOwner: true, relocation.CapturedParentTemp(newNode.Node));
            Generate(relocation.Root, new[] { old, next }, scope, relocation.CapturedParentTemp(relocation.Root), requireGraph);
            return (old.Observed, next.Observed);
        }

        private void Generate(Entity parent, GeometryRun[] runs, Action scope, Temp? parentTemp, Action? requireGraph)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && emptyPassed && !destructionStarted && !generateRequested && failureCode == null,
                "NODE_GEOMETRY_GENERATION_REJECTED");
            generateRequested = true;
            root = parent; cohort = runs; rootTemp = parentTemp; requireLiveGraph = requireGraph;
            try
            {
                RunNative(scope, false);
                RequireCohort(scope);
                foreach (GeometryRun input in cohort)
                {
                    NodeGeometry expectedAfter = QualifyNode(input, scope);
                    input.Observed = EntityManager.GetComponentData<NodeGeometry>(input.Selection.Node);
                    input.ObservedRead = true; Guard(scope);
                    Require(Same(input.Expected, expectedAfter) && Same(input.Observed, input.Expected)
                        && !Same(input.Observed, input.Before), "NODE_GEOMETRY_READBACK_MISMATCH");
                }
                GeometryPassed = true;
            }
            catch (Exception error) { Record(error); throw; }
        }

        private void RunNative(Action scope, bool empty)
        {
            Guard(scope);
            currentScope = scope;
            emptyMode = empty;
            Enabled = true;
            try
            {
                Update();
                Require(empty ? emptyReturned : nativeReturned, "NODE_GEOMETRY_NATIVE_NOT_RETURNED");
                if (!empty && cohort.Length == 2)
                    Require(!Dependency.Equals(default(Unity.Jobs.JobHandle)), "NODE_GEOMETRY_RELOCATION_WRITER_UNAVAILABLE");
                Dependency.Complete();
                if (empty) emptyJoined = true;
                else nativeJoined = true;
                Guard(scope);
            }
            finally
            {
                Enabled = false;
                currentScope = null;
            }
        }

        protected override void OnUpdate()
        {
            var scope = currentScope ?? throw new Rejection("NODE_GEOMETRY_SCOPE_MISSING");
            RequireUnpatched();
            if (emptyMode)
            {
                Require(!emptyEntered, "NODE_GEOMETRY_EMPTY_REPEATED");
                RequireEmpty(scope);
                emptyEntered = true;
                base.OnUpdate();
                emptyReturned = true;
                return;
            }

            Require(generateRequested && !nativeEntered, "NODE_GEOMETRY_GENERATION_REPEATED");
            RequireCohort(scope);
            foreach (GeometryRun input in cohort)
            {
                input.Expected = QualifyNode(input, scope);
                input.Before = EntityManager.GetComponentData<NodeGeometry>(input.Selection.Node);
                input.BeforeRead = true;
            }
            RequireTerrain();
            Guard(scope);
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private void RequireEmpty(Action scope)
        {
            Guard(scope);
            RequireBindings();
            Require(updatedEdges.IsEmptyIgnoreFilter && allEdges.IsEmptyIgnoreFilter
                && updatedNodes.IsEmptyIgnoreFilter && allNodes.IsEmptyIgnoreFilter, "NODE_GEOMETRY_EMPTY_QUERY_CHANGED");
            Guard(scope);
        }

        private void RequireCohort(Action scope)
        {
            Guard(scope);
            requireLiveGraph?.Invoke();
            RequireBindings();
            RequireScoped(EntityManager, root, rootTemp);
            Require(updatedEdges.IsEmptyIgnoreFilter && allEdges.IsEmptyIgnoreFilter, "NODE_GEOMETRY_EDGE_QUERY_NOT_EMPTY");
            RequireOnlyNodes(updatedNodes);
            RequireOnlyNodes(allNodes);
            Guard(scope);
        }

        private NodeGeometry QualifyNode(GeometryRun input, Action scope)
        {
            Guard(scope);
            var receipt = input.Selection;
            Entity node = receipt.Node;
            receipt.RequireCurrentScope();
            Require(ReferenceEquals(receipt.Source, World) && receipt.Actor == actor
                && receipt.ObservedSelection && receipt.NativeEnteredReturnedJoined && receipt.CacheUnchanged
                && receipt.SelectedRow.HasValue && receipt.SelectedRowIndex.HasValue
                && root != node && root != Entity.Null && root.Index >= 0,
                "NODE_GEOMETRY_SELECTION_REJECTED");

            var manager = EntityManager;
            RequireScoped(manager, node, input.Temp);
            Require(manager.HasComponent<Node>(node) && manager.HasComponent<Updated>(node)
                && manager.HasComponent<NodeGeometry>(node) && manager.HasComponent<Owner>(node) == input.RequiresOwner
                && (!input.RequiresOwner || manager.GetComponentData<Owner>(node).m_Owner == root)
                && manager.HasComponent<Orphan>(node) && manager.GetComponentData<Orphan>(node).Equals(receipt.After)
                && manager.HasComponent<PrefabRef>(node)
                && manager.GetComponentData<PrefabRef>(node).m_Prefab == receipt.Prefab
                && manager.HasBuffer<ConnectedEdge>(node) && manager.GetBuffer<ConnectedEdge>(node, true).Length == 0,
                "NODE_GEOMETRY_NODE_ROLE_CHANGED");
            if (input.Temp is Temp captured && captured.m_Original != Entity.Null)
            {
                Entity original = captured.m_Original;
                Require(manager.Exists(original) && manager.HasComponent<Node>(original)
                    && !manager.HasComponent<Temp>(original) && !manager.HasComponent<Deleted>(original)
                    && manager.HasBuffer<ConnectedEdge>(original) && manager.GetBuffer<ConnectedEdge>(original, true).Length == 0,
                    "NODE_GEOMETRY_ORIGINAL_EDGE_BRANCH_UNSUPPORTED");
            }
            Require(manager.GetChunk(node).Count == 1, "NODE_GEOMETRY_FOREIGN_CHUNK_MEMBER");
            Guard(scope);

            var composition = receipt.After.m_Composition;
            Require(manager.Exists(receipt.Prefab) && manager.HasComponent<NetGeometryData>(receipt.Prefab)
                && manager.Exists(composition) && !manager.HasComponent<Unity.Entities.Disabled>(composition)
                && !manager.HasComponent<ObjectGenerationScope>(composition) && !manager.HasComponent<Deleted>(composition)
                && !manager.HasComponent<Created>(composition) && manager.HasComponent<PrefabRef>(composition)
                && manager.GetComponentData<PrefabRef>(composition).m_Prefab == receipt.Prefab
                && manager.HasComponent<NetCompositionData>(composition),
                "NODE_GEOMETRY_COMPOSITION_UNAVAILABLE");
            RequireSelectedRow(manager, receipt);
            var data = manager.GetComponentData<NetCompositionData>(composition);
            Require(data.m_Flags.Equals(receipt.SelectedRow!.Value.m_Mask)
                && (data.m_State & (CompositionState.LowerToTerrain | CompositionState.RaiseToTerrain)) == 0,
                "NODE_GEOMETRY_TERRAIN_SAMPLING_UNQUALIFIED");

            var position = manager.GetComponentData<Node>(node).m_Position;
            var halfWidth = data.m_Width * 0.5f;
            Require(math.all(math.isfinite(position)) && math.isfinite(halfWidth) && halfWidth >= 0f
                && math.isfinite(data.m_HeightRange.min) && math.isfinite(data.m_HeightRange.max)
                && data.m_HeightRange.min <= data.m_HeightRange.max, "NODE_GEOMETRY_INPUT_NONFINITE");
            var minimum = new float3(position.x - halfWidth, position.y + data.m_HeightRange.min, position.z - halfWidth);
            var maximum = new float3(position.x + halfWidth, position.y + data.m_HeightRange.max, position.z + halfWidth);
            Require(math.all(math.isfinite(minimum)) && math.all(math.isfinite(maximum)), "NODE_GEOMETRY_OUTPUT_NONFINITE");
            Guard(scope);
            return new NodeGeometry { m_Position = position.y, m_Bounds = new Bounds3(minimum, maximum) };
        }

        private void RequireScoped(EntityManager manager, Entity entity, Temp? expected)
        {
            Require(manager.Exists(entity) && manager.HasComponent<Unity.Entities.Disabled>(entity)
                && manager.HasComponent<ObjectGenerationScope>(entity) && !manager.HasComponent<Deleted>(entity)
                && manager.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor && manager.HasComponent<Temp>(entity),
                "NODE_GEOMETRY_SCOPE_CHANGED");
            var temp = manager.GetComponentData<Temp>(entity);
            Require(expected.HasValue ? temp.Equals(expected.Value)
                : temp.m_Original == Entity.Null && (temp.m_Flags & (TempFlags.Create | TempFlags.Delete)) == TempFlags.Create,
                "NODE_GEOMETRY_TEMP_CHANGED");
        }

        private void RequireOnlyNodes(EntityQuery query)
        {
            int count = cohort.Length;
            Require(query.CalculateEntityCount() == count && query.CalculateChunkCount() == count,
                "NODE_GEOMETRY_NODE_QUERY_CHANGED");
            if (count == 1)
            {
                Require(query.GetSingletonEntity() == cohort[0].Selection.Node, "NODE_GEOMETRY_NODE_QUERY_CHANGED");
                return;
            }

            using var values = query.ToEntityArray(Allocator.Temp);
            Entity old = cohort[0].Selection.Node, next = cohort[1].Selection.Node;
            Require(values.Length == 2 && (values[0] == old && values[1] == next || values[0] == next && values[1] == old),
                "NODE_GEOMETRY_NODE_QUERY_CHANGED");
        }

        private static void RequireSelectedRow(EntityManager manager, CompositionSelectionReceipt receipt)
        {
            Require(manager.HasBuffer<NetGeometryComposition>(receipt.Prefab), "NODE_GEOMETRY_CACHE_UNAVAILABLE");
            var rows = manager.GetBuffer<NetGeometryComposition>(receipt.Prefab, true);
            var index = receipt.SelectedRowIndex!.Value;
            var selected = receipt.SelectedRow!.Value;
            Require(index >= 0 && index < rows.Length && rows.Length <= 256
                && rows[index].Equals(selected), "NODE_GEOMETRY_CACHE_ROW_CHANGED");
            for (int earlier = 0; earlier < index; earlier++)
                Require(rows[earlier].m_Mask != selected.m_Mask, "NODE_GEOMETRY_CACHE_FIRST_MATCH_CHANGED");
        }

        private void RequireTerrain()
        {
            // The native schedule always supplies terrain arrays and computes
            // terrain bounds, even with no height samples in this branch.
            var data = terrain!.GetHeightData(false);
            var resolution = data.resolution;
            Require(data.heights.IsCreated && (!data.hasBackdrop || data.downscaledHeights.IsCreated)
                && resolution.x >= 2 && resolution.y >= 2 && resolution.z >= 2
                && data.heights.Length == (long)resolution.x * resolution.z
                && math.all(math.isfinite(data.scale)) && math.all(data.scale > 0f)
                && math.all(math.isfinite(data.offset)), "NODE_GEOMETRY_TERRAIN_DATA_UNAVAILABLE");
            var bounds = TerrainUtils.GetBounds(ref data);
            Require(math.all(math.isfinite(bounds.min)) && math.all(math.isfinite(bounds.max)),
                "NODE_GEOMETRY_TERRAIN_BOUNDS_NONFINITE");
        }

        private void RequireBindings()
        {
            var fields = bindings ?? throw new Rejection("NODE_GEOMETRY_BINDINGS_MISSING");
            var queries = EntityQueries;
            queriesVerified = queries.Length == 4
                && Matches(fields.UpdatedEdges, updatedEdges, queries) && Matches(fields.UpdatedNodes, updatedNodes, queries)
                && Matches(fields.AllEdges, allEdges, queries) && Matches(fields.AllNodes, allNodes, queries);
            Require(queriesVerified && (bool)fields.Loaded.GetValue(this)! == false
                && ReferenceEquals(fields.Terrain.GetValue(this), terrain)
                && ReferenceEquals(World.GetExistingSystemManaged<TerrainSystem>(), terrain), "NODE_GEOMETRY_BINDINGS_CHANGED");
        }

        private bool Matches(FieldInfo field, EntityQuery query, EntityQuery[] registered)
        {
            if (!((EntityQuery)field.GetValue(this)!).Equals(query) || !query.HasFilter()) return false;
            foreach (var actual in registered) if (actual.Equals(query)) return true;
            return false;
        }

        private void RequireUnpatched()
        {
            var result = NodeGeometryPatchProbe.Observe();
            patchQualified = result.Qualified;
            Require(patchQualified, result.Reason);
        }

        private void Guard(Action scope)
        {
            scope();
            Require(World != null && World.IsCreated && !destructionStarted, "NODE_GEOMETRY_SOURCE_UNAVAILABLE");
        }

        internal JObject RelocationSnapshot() => new JObject {
            ["passed"] = GeometryPassed, ["emptyPassed"] = emptyPassed, ["nativeEntered"] = nativeEntered,
            ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined, ["queriesVerified"] = queriesVerified,
            ["patchQualified"] = patchQualified, ["destructionReturned"] = DestructionReturned,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["failureCode"] = failureCode,
            ["oldNode"] = cohort.Length == 2 ? cohort[0].Snapshot() : null,
            ["newNode"] = cohort.Length == 2 ? cohort[1].Snapshot() : null
        };

        internal JObject Snapshot()
        {
            var input = cohort.Length == 1 ? cohort[0] : null;
            Entity node = input?.Selection.Node ?? Entity.Null;
            NodeGeometry observed = input?.Observed ?? default;
            return new JObject {
                ["passed"] = GeometryPassed, ["emptyPassed"] = emptyPassed, ["nativeEntered"] = nativeEntered,
                ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined, ["storageReleased"] = StorageReleased,
                ["creationReturned"] = CreationReturned, ["destructionReturned"] = DestructionReturned,
                ["queriesVerified"] = queriesVerified, ["patchQualified"] = patchQualified,
                ["node"] = new JObject { ["index"] = node.Index, ["version"] = node.Version },
                ["position"] = GeometryPassed ? new JValue(observed.m_Position) : JValue.CreateNull(),
                ["flatness"] = GeometryPassed ? new JValue(observed.m_Flatness) : JValue.CreateNull(),
                ["offset"] = GeometryPassed ? new JValue(observed.m_Offset) : JValue.CreateNull(),
                ["min"] = GeometryPassed ? (JToken)Vector(observed.m_Bounds.min) : JValue.CreateNull(),
                ["max"] = GeometryPassed ? (JToken)Vector(observed.m_Bounds.max) : JValue.CreateNull(),
                ["terrainSampled"] = false, ["nativeDisposalsObserved"] = false, ["failureCode"] = failureCode
            };
        }

        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        internal static bool Same(NodeGeometry a, NodeGeometry b) => a.m_Position == b.m_Position
            && a.m_Flatness == b.m_Flatness && a.m_Offset == b.m_Offset
            && math.all(a.m_Bounds.min == b.m_Bounds.min) && math.all(a.m_Bounds.max == b.m_Bounds.max);
        private void Record(Exception error) => failureCode ??= error is Rejection rejection ? rejection.Code : "NODE_GEOMETRY_FAILED";
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NODE_GEOMETRY_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        private sealed class GeometryRun
        {
            internal readonly CompositionSelectionReceipt Selection;
            internal readonly bool RequiresOwner;
            internal readonly Temp? Temp;
            internal NodeGeometry Before, Expected, Observed;
            internal bool BeforeRead, ObservedRead;

            internal GeometryRun(CompositionSelectionReceipt selection, bool requiresOwner, Temp? temp = null)
            { Selection = selection; RequiresOwner = requiresOwner; Temp = temp; }

            internal JObject Snapshot() => new JObject {
                ["node"] = new JArray(Selection.Node.Index, Selection.Node.Version),
                ["before"] = BeforeRead ? Value(Before) : null,
                ["expected"] = BeforeRead ? Value(Expected) : null,
                ["observed"] = ObservedRead ? Value(Observed) : null
            };

            private static JObject Value(NodeGeometry geometry) => new JObject {
                ["position"] = geometry.m_Position, ["flatness"] = geometry.m_Flatness, ["offset"] = geometry.m_Offset,
                ["min"] = Vector(geometry.m_Bounds.min), ["max"] = Vector(geometry.m_Bounds.max)
            };
        }

        private sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Terrain, UpdatedEdges, UpdatedNodes, AllEdges, AllNodes, Loaded;
            internal Bindings()
            {
                Require(typeof(GeometrySystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "NODE_GEOMETRY_NATIVE_BUILD_UNQUALIFIED");
                Terrain = Field("m_TerrainSystem", typeof(TerrainSystem));
                UpdatedEdges = Field("m_UpdatedEdgesQuery", typeof(EntityQuery));
                UpdatedNodes = Field("m_UpdatedNodesQuery", typeof(EntityQuery));
                AllEdges = Field("m_AllEdgesQuery", typeof(EntityQuery));
                AllNodes = Field("m_AllNodesQuery", typeof(EntityQuery));
                Loaded = Field("m_Loaded", typeof(bool));
            }

            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(GeometrySystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.DeclaringType == typeof(GeometrySystem) && field.FieldType == type
                    && !field.IsStatic && !field.IsInitOnly, "NODE_GEOMETRY_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
