#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using LiveSubLane = Game.Net.SubLane;
using LiveSubObject = Game.Objects.SubObject;
using PrefabSubLane = Game.Prefabs.SubLane;
using Node = Game.Net.Node;
using Transform = Game.Objects.Transform;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class PreviewLaneProducer : LaneSystem
    {
        internal const int OutputCommandUpperBound = 32;

        private Bindings? bindings;
        private PreviewLaneBarrier? barrier;
        private PreviewLaneSkipSink? sink;
        private CityConfigurationSystem? city;
        private ToolSystem? tools;
        private TerrainSystem? terrain;
        private EntityQuery owners, buildingSettings;
        private Entity root, node;
        private GeneratedSubObjectsPayload? cohort;
        private RelocationInput? relocation;
        private (int RootLanes, int RootUpgrades, int OldNodeLanes, int OldNodeEdges)? originalBuffers;
        private Action? currentScope;
        private DefaultNetLane[] admitted = Array.Empty<DefaultNetLane>();
        // A bounded value copy of the latest admission read, for a rejected
        // candidate. These fields do not grant eligibility or retain ECS data.
        private Node? inspectedNode;
        private (bool Building, bool Transform, bool Owner, bool Edge, bool Node,
            bool UnderConstruction, bool Destroyed, int? Upgrades)? inspectedRoot;
        private uint? inspectedGeneral, inspectedLeft, inspectedRight;
        private int? inspectedDefaultCount;
        private DefaultNetLane[] inspectedDefaults = Array.Empty<DefaultNetLane>();
        private int actor, ownerCount, chunkCount, configCount;
        private bool creationStarted, destructionStarted, requested, patchQualified;
        private bool nativeEntered, nativeReturned, nativeJoined, writerConfirmed;
        private string? failureCode;

        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && (!nativeJoined || !writerConfirmed);
        internal bool LanePassed { get; private set; }
        internal bool StorageReleased => true;

        internal void Configure(int actor, PreviewLaneBarrier barrier, PreviewLaneSkipSink sink)
        {
            RequireMainThread();
            Require(bindings == null && !creationStarted && World == null && actor > 0
                && barrier != null && sink != null, "PREVIEW_LANE_CONFIGURATION_REJECTED");
            RequireUnpatched();
            bindings = new Bindings();
            this.actor = actor;
            this.barrier = barrier;
            this.sink = sink;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            RequireMainThread();
            Enabled = false;
            var fields = bindings ?? throw new Rejection("PREVIEW_LANE_CONFIGURATION_MISSING");
            city = World.GetExistingSystemManaged<CityConfigurationSystem>();
            tools = World.GetExistingSystemManaged<ToolSystem>();
            terrain = World.GetExistingSystemManaged<TerrainSystem>();
            Require(city != null && tools != null && terrain != null, "PREVIEW_LANE_SOURCE_DEPENDENCY_MISSING");

            owners = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<LiveSubLane>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(), ComponentType.ReadOnly<Game.Areas.Area>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            owners.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            buildingSettings = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            RequireForUpdate(owners);
            fields.Owners.SetValue(this, owners);
            fields.BuildingSettings.SetValue(this, buildingSettings);
            fields.City.SetValue(this, city);
            fields.Tools.SetValue(this, tools);
            fields.Terrain.SetValue(this, terrain);
            fields.Barrier.SetValue(this, barrier);
            fields.LaneReferences.SetValue(this, sink);
            fields.AppliedTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Applied>(),
                ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>()));
            fields.DeletedTempTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Deleted>(), ComponentType.ReadWrite<Temp>()));
            fields.TempOwnerTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<Temp>(), ComponentType.ReadWrite<Owner>()));
            fields.HideLaneTypes.SetValue(this, new ComponentTypeSet(ComponentType.ReadWrite<CullingInfo>(),
                ComponentType.ReadWrite<MeshBatch>(), ComponentType.ReadWrite<MeshColor>()));
            RequireBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "PREVIEW_LANE_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void GenerateRelocationOnce(GeneratedSubObjectsPayload cohort, CompositionSelectionReceipt oldNode,
            CompositionSelectionReceipt newNode, (NodeGeometry Old, NodeGeometry New) geometry, Action requireScope)
        {
            Require(cohort.IsRelocation && ReferenceEquals(cohort.Source, World) && cohort.Actor == actor
                && cohort.OldNode == oldNode.Node && cohort.Node == newNode.Node,
                "PREVIEW_LANE_RELOCATION_INPUT_CHANGED");
            relocation = new RelocationInput(cohort, oldNode, newNode, geometry);
            GenerateOnce(cohort, requireScope);
        }

        internal void GenerateOnce(GeneratedSubObjectsPayload cohort, Action requireScope)
        {
            RequireMainThread();
            if (cohort == null) throw new ArgumentNullException(nameof(cohort));
            if (requireScope == null) throw new ArgumentNullException(nameof(requireScope));
            Require(CreationReturned && !destructionStarted && !requested && ReferenceEquals(cohort.Source, World)
                && cohort.Actor == actor && cohort.Root.Index >= 0 && cohort.Node.Index >= 0
                && cohort.IsRelocation == (relocation != null), "PREVIEW_LANE_GENERATION_REJECTED");
            requested = true;
            this.cohort = cohort;
            root = cohort.Root;
            node = cohort.Node;
            currentScope = requireScope;
            try
            {
                Guard(requireScope);
                Require(ShouldRunSystem(), "PREVIEW_LANE_REQUIRED_QUERY_REJECTED");
                Enabled = true;
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_LANE_NATIVE_NOT_RETURNED");
                JobHandle original = Dependency;
                Require(!original.Equals(default(JobHandle)), "PREVIEW_LANE_NATIVE_WRITER_UNCONFIRMED");
                var completion = original;
                completion.Complete();
                nativeJoined = true;
                sink!.RequireWriter(original);
                writerConfirmed = true;
                Guard(requireScope);
                Require(barrier!.PendingCount == 1, "PREVIEW_LANE_NATIVE_OUTPUT_UNCONFIRMED");
                var observed = Qualify(requireScope);
                for (int i = 0; i < admitted.Length; i++)
                    Require(admitted[i].Equals(observed[i]), "PREVIEW_LANE_DEFAULTS_CHANGED");
                LanePassed = true;
            }
            catch (Exception error)
            {
                failureCode ??= error is Rejection rejected ? rejected.Code : "PREVIEW_LANE_GENERATION_FAILED";
                throw;
            }
            finally
            {
                Enabled = false;
                currentScope = null;
            }
        }

        protected override void OnUpdate()
        {
            var scope = currentScope ?? throw new Rejection("PREVIEW_LANE_SCOPE_MISSING");
            Require(requested && !nativeEntered, "PREVIEW_LANE_NATIVE_REPEATED");
            RequireUnpatched();
            admitted = Qualify(scope);
            Require(barrier!.PendingCount == 0 && !sink!.HasQueue, "PREVIEW_LANE_OUTPUT_ALREADY_PRESENT");
            Guard(scope);
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private DefaultNetLane[] Qualify(Action scope)
        {
            ResetInputObservation();
            Guard(scope);
            RequireBindings();
            Require(tools!.actionMode == GameMode.Game, "PREVIEW_LANE_MODE_CHANGED");
            ownerCount = owners.CalculateEntityCount();
            chunkCount = owners.CalculateChunkCount();
            configCount = buildingSettings.CalculateEntityCount();
            int expectedOwners = relocation == null ? 2 : 3;
            Require(ownerCount == expectedOwners && chunkCount == expectedOwners && configCount == 1,
                "PREVIEW_LANE_QUERY_UNQUALIFIED");
            using (var members = owners.ToEntityArray(Allocator.Temp))
            {
                var expected = new HashSet<Entity> { root, node };
                if (relocation != null) expected.Add(relocation.OldNode.Node);
                for (int i = 0; i < members.Length; i++)
                    Require(expected.Remove(members[i]), "PREVIEW_LANE_QUERY_MEMBERS_CHANGED");
                Require(expected.Count == 0, "PREVIEW_LANE_QUERY_MEMBERS_CHANGED");
            }

            var entities = EntityManager;
            RequireOwner(entities, root, TempFlags.Create | TempFlags.Essential);
            var rootState = PreviewLaneInputs.ReadObjectState(entities, root);
            inspectedRoot = rootState;
            Require(rootState.Building && rootState.Transform && !rootState.Owner && !rootState.Edge && !rootState.Node
                && !rootState.UnderConstruction && !rootState.Destroyed && (rootState.Upgrades ?? 0) == 0,
                "PREVIEW_LANE_ROOT_BRANCH_CHANGED");
            Entity prefab = RequireNode(entities, node, requiresOwner: true, relocation?.NewNode, relocation?.Geometry.New);
            if (relocation != null)
            {
                Require(RequireNode(entities, relocation.OldNode.Node, requiresOwner: false, relocation.OldNode,
                    relocation.Geometry.Old) == prefab, "PREVIEW_LANE_RELOCATION_PREFAB_MISMATCH");
                RequireOriginalBuffers(entities, relocation.Cohort);
            }

            RequireNoDeclaredLanes(entities, root);
            var childIds = new HashSet<Entity>();
            RequireChildren(entities, root, cohort!.ChildCount(root), childIds);
            RequireChildren(entities, node, cohort.ChildCount(node), childIds);
            if (relocation != null)
                RequireChildren(entities, relocation.OldNode.Node, cohort.ChildCount(relocation.OldNode.Node), childIds);

            var data = terrain!.GetHeightData(false);
            Require(data.heights.IsCreated && (!data.hasBackdrop || data.downscaledHeights.IsCreated)
                && data.resolution.x > 2 && data.resolution.z > 2
                && data.heights.Length == (long)data.resolution.x * data.resolution.z,
                "PREVIEW_LANE_TERRAIN_UNAVAILABLE");
            var rows = ReadDefaults(entities, prefab);
            Guard(scope);
            return rows;
        }

        private Entity RequireNode(EntityManager entities, Entity target, bool requiresOwner,
            CompositionSelectionReceipt? receipt, NodeGeometry? expectedGeometry)
        {
            ResetNodeInputObservation();
            RequireOwner(entities, target, TempFlags.Create | TempFlags.IsLast);
            Require(entities.HasComponent<Node>(target) && entities.HasComponent<NodeGeometry>(target)
                && !entities.HasComponent<Transform>(target) && !entities.HasComponent<Edge>(target)
                && !entities.HasComponent<Composition>(target)
                && entities.HasComponent<Owner>(target) == requiresOwner
                && (!requiresOwner || entities.GetComponentData<Owner>(target).m_Owner == root)
                && entities.HasBuffer<ConnectedEdge>(target) && entities.GetBuffer<ConnectedEdge>(target, true).Length == 0
                && !entities.HasBuffer<ConnectedNode>(target), "PREVIEW_LANE_NODE_BRANCH_CHANGED");
            var current = entities.GetComponentData<Node>(target);
            var geometry = entities.GetComponentData<NodeGeometry>(target);
            inspectedNode = current;
            Require(math.all(math.isfinite(current.m_Position)) && current.m_Rotation.Equals(quaternion.identity)
                && geometry.m_Position == current.m_Position.y && math.all(math.isfinite(geometry.m_Bounds.min))
                && math.all(math.isfinite(geometry.m_Bounds.max)), "PREVIEW_LANE_NODE_GEOMETRY_INVALID");

            Entity prefab = entities.GetComponentData<PrefabRef>(target).m_Prefab;
            RequirePrefab(entities, prefab);
            var orphan = entities.GetComponentData<Orphan>(target);
            if (receipt != null)
            {
                receipt.RequireCurrentScope();
                Require(ReferenceEquals(receipt.Source, World) && receipt.Actor == actor && receipt.Node == target
                    && receipt.Prefab == prefab && receipt.ObservedSelection && orphan.Equals(receipt.After)
                    && expectedGeometry.HasValue && NodeGeometryProducer.Same(geometry, expectedGeometry.Value),
                    "PREVIEW_LANE_RELOCATION_GEOMETRY_CHANGED");
            }
            Entity composition = orphan.m_Composition;
            Require(entities.Exists(composition) && !entities.HasComponent<Deleted>(composition)
                && !entities.HasComponent<Unity.Entities.Disabled>(composition)
                && !entities.HasComponent<Created>(composition)
                && !entities.HasComponent<ObjectGenerationScope>(composition)
                && entities.GetComponentData<PrefabRef>(composition).m_Prefab == prefab,
                "PREVIEW_LANE_COMPOSITION_UNAVAILABLE");
            var flags = entities.GetComponentData<NetCompositionData>(composition).m_Flags;
            inspectedGeneral = (uint)flags.m_General;
            inspectedLeft = (uint)flags.m_Left;
            inspectedRight = (uint)flags.m_Right;
            Require(flags.m_General == (CompositionFlags.General.Node | CompositionFlags.General.Invert | CompositionFlags.General.DeadEnd)
                && flags.m_Left == 0 && flags.m_Right == 0, "PREVIEW_LANE_COMPOSITION_BRANCH_CHANGED");
            float width = entities.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth;
            Require(math.isfinite(width) && width > 0f, "PREVIEW_LANE_DEFAULT_WIDTH_INVALID");
            return prefab;
        }

        private void RequireOriginalBuffers(EntityManager entities, GeneratedSubObjectsPayload cohort)
        {
            Entity originalRoot = cohort.CapturedParentTemp(root).m_Original;
            Entity originalNode = cohort.CapturedParentTemp(cohort.OldNode).m_Original;
            Require(entities.Exists(originalRoot) && entities.HasComponent<Building>(originalRoot)
                && !entities.HasComponent<Temp>(originalRoot) && !entities.HasComponent<Deleted>(originalRoot)
                && entities.Exists(originalNode) && entities.HasComponent<Node>(originalNode)
                && !entities.HasComponent<Temp>(originalNode) && !entities.HasComponent<Deleted>(originalNode),
                "PREVIEW_LANE_RELOCATION_ORIGINAL_CHANGED");
            var observed = (RootLanes: BufferLength<LiveSubLane>(entities, originalRoot),
                RootUpgrades: BufferLength<InstalledUpgrade>(entities, originalRoot),
                OldNodeLanes: BufferLength<LiveSubLane>(entities, originalNode),
                OldNodeEdges: BufferLength<ConnectedEdge>(entities, originalNode));
            Require(!originalBuffers.HasValue || originalBuffers.Value.Equals(observed), "PREVIEW_LANE_ORIGINAL_BUFFERS_CHANGED");
            originalBuffers = observed;
            Require(observed.RootLanes <= 0 && observed.RootUpgrades <= 0 && observed.OldNodeLanes <= 0 && observed.OldNodeEdges == 0,
                "PREVIEW_LANE_ORIGINAL_BUFFERS_UNSUPPORTED");
        }

        private static int BufferLength<T>(EntityManager entities, Entity owner) where T : unmanaged, IBufferElementData =>
            entities.HasBuffer<T>(owner) ? entities.GetBuffer<T>(owner, true).Length : -1;

        private void RequireOwner(EntityManager entities, Entity owner, TempFlags flags)
        {
            Require(entities.Exists(owner) && entities.GetChunk(owner).Count == 1
                && entities.HasComponent<Created>(owner) && entities.HasComponent<Updated>(owner)
                && entities.HasComponent<PseudoRandomSeed>(owner) && entities.HasComponent<Unity.Entities.Disabled>(owner)
                && !entities.HasComponent<Deleted>(owner) && entities.GetSharedComponent<ObjectGenerationScope>(owner).Value == actor
                && entities.HasBuffer<LiveSubLane>(owner) && entities.GetBuffer<LiveSubLane>(owner, true).Length == 0,
                "PREVIEW_LANE_OWNER_CHANGED");
            Temp temp = entities.GetComponentData<Temp>(owner);
            Require(relocation == null ? temp.m_Original == Entity.Null && temp.m_Flags == flags
                : temp.Equals(relocation.Cohort.CapturedParentTemp(owner)), "PREVIEW_LANE_OWNER_TEMP_CHANGED");
        }

        private void RequireChildren(EntityManager entities, Entity owner, int count, HashSet<Entity> seen)
        {
            var children = entities.GetBuffer<LiveSubObject>(owner, true);
            Require(children.Length == count, "PREVIEW_LANE_ANCHOR_GRAPH_CHANGED");
            for (int i = 0; i < children.Length; i++)
            {
                Entity child = children[i].m_SubObject;
                Require(child != root && child != node && child != relocation?.OldNode.Node
                    && seen.Add(child) && entities.Exists(child)
                    && entities.HasComponent<Unity.Entities.Disabled>(child) && !entities.HasComponent<Deleted>(child)
                    && entities.GetSharedComponent<ObjectGenerationScope>(child).Value == actor
                    && entities.GetComponentData<Owner>(child).m_Owner == owner && !entities.HasBuffer<LiveSubLane>(child),
                    "PREVIEW_LANE_ANCHOR_CHILD_CHANGED");
                // Relocation child Temp values are bound by the live attachment graph guard.
                if (relocation == null)
                {
                    Temp temp = entities.GetComponentData<Temp>(child);
                    Require(temp.m_Original == Entity.Null && temp.m_Flags == TempFlags.Create, "PREVIEW_LANE_ANCHOR_TEMP_CHANGED");
                }
                RequireNoDeclaredLanes(entities, child);
            }
        }

        private static void RequireNoDeclaredLanes(EntityManager entities, Entity owner)
        {
            Entity prefab = entities.GetComponentData<PrefabRef>(owner).m_Prefab;
            RequirePrefab(entities, prefab);
            Require(!entities.HasBuffer<PrefabSubLane>(prefab), "PREVIEW_LANE_OBJECT_LANES_PRESENT");
        }

        private DefaultNetLane[] ReadDefaults(EntityManager entities, Entity prefab)
        {
            var buffer = entities.GetBuffer<DefaultNetLane>(prefab, true);
            inspectedDefaultCount = buffer.Length;
            var rows = new DefaultNetLane[Math.Min(buffer.Length, 4)];
            for (int i = 0; i < rows.Length; i++) rows[i] = buffer[i];
            inspectedDefaults = rows;
            Require(buffer.Length == 4, "PREVIEW_LANE_DEFAULT_COUNT_CHANGED");
            RequireUtilityDefaultRows(entities, rows);
            return rows;
        }

        // Shared fixed-fixture input check: it does not run the native lane
        // system or infer that every orphan node has no output.
        internal static void RequireStandaloneUtilityDefaults(EntityManager entities, Entity prefab)
        {
            RequirePrefab(entities, prefab);
            Require(entities.HasBuffer<DefaultNetLane>(prefab), "PREVIEW_LANE_DEFAULT_COUNT_CHANGED");
            var buffer = entities.GetBuffer<DefaultNetLane>(prefab, true);
            Require(buffer.Length == 4, "PREVIEW_LANE_DEFAULT_COUNT_CHANGED");
            var rows = new DefaultNetLane[4];
            for (int i = 0; i < rows.Length; i++) rows[i] = buffer[i];
            RequireUtilityDefaultRows(entities, rows);
        }

        private static void RequireUtilityDefaultRows(EntityManager entities, DefaultNetLane[] rows)
        {
            const LaneFlags limits = LaneFlags.LeftLimit | LaneFlags.RightLimit | LaneFlags.Utility;
            Require(rows[0].m_Position.Equals(new float3(-0.5f, 9f, 0f))
                && rows[1].m_Position.Equals(new float3(0f, 9f, 0f))
                && rows[2].m_Position.Equals(new float3(0f, -0.65f, 0f))
                && rows[3].m_Position.Equals(new float3(0.5f, 9f, 0f))
                && rows[0].m_Flags == (limits | LaneFlags.FindAnchor)
                && rows[1].m_Flags == rows[0].m_Flags && rows[3].m_Flags == rows[0].m_Flags
                && rows[2].m_Flags == (limits | LaneFlags.Underground)
                && rows[0].m_Lane == rows[1].m_Lane && rows[0].m_Lane == rows[3].m_Lane && rows[0].m_Lane != rows[2].m_Lane,
                "PREVIEW_LANE_DEFAULT_BRANCH_CHANGED");
            RequireLanePrefab(entities, rows[0].m_Lane, LaneFlags.Utility, 16);
            RequireLanePrefab(entities, rows[2].m_Lane, LaneFlags.Utility | LaneFlags.Underground, 15);
        }

        private static void RequireLanePrefab(EntityManager entities, Entity prefab, LaneFlags flags, int typeCount)
        {
            RequirePrefab(entities, prefab);
            Require(!entities.HasBuffer<PlaceholderObjectElement>(prefab), "PREVIEW_LANE_VARIANTS_UNSUPPORTED");
            var lane = entities.GetComponentData<NetLaneData>(prefab);
            var archetype = entities.GetComponentData<NetLaneArchetypeData>(prefab).m_NodeLaneArchetype;
            Require(lane.m_Flags == flags && entities.GetComponentData<UtilityLaneData>(prefab).m_UtilityTypes == UtilityTypes.LowVoltageLine
                && archetype.Valid && archetype.TypesCount == typeCount, "PREVIEW_LANE_PREFAB_BRANCH_CHANGED");
        }

        private static void RequirePrefab(EntityManager entities, Entity prefab) =>
            Require(entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && entities.IsComponentEnabled<PrefabData>(prefab) && !entities.HasComponent<Deleted>(prefab),
                "PREVIEW_LANE_PREFAB_UNAVAILABLE");

        internal Entity[] CopyAdmittedLanePrefabs()
        {
            RequireMainThread();
            Require(LanePassed && admitted.Length == 4, "PREVIEW_LANE_ADMISSION_UNAVAILABLE");
            return new[] { admitted[0].m_Lane, admitted[2].m_Lane };
        }

        private void ResetInputObservation()
        {
            inspectedRoot = null;
            ResetNodeInputObservation();
        }

        private void ResetNodeInputObservation()
        {
            inspectedNode = null;
            inspectedGeneral = inspectedLeft = inspectedRight = null;
            inspectedDefaultCount = null;
            inspectedDefaults = Array.Empty<DefaultNetLane>();
        }

        internal JObject InputSnapshot()
        {
            var rows = new JArray();
            foreach (DefaultNetLane row in inspectedDefaults)
                rows.Add(new JArray(row.m_Lane.Index, row.m_Lane.Version,
                    row.m_Position.x, row.m_Position.y, row.m_Position.z, (uint)row.m_Flags));
            return new JObject {
                ["root"] = inspectedRoot is { } value ? new JObject {
                    ["building"] = value.Building, ["transform"] = value.Transform, ["owner"] = value.Owner,
                    ["edge"] = value.Edge, ["node"] = value.Node, ["underConstruction"] = value.UnderConstruction,
                    ["destroyed"] = value.Destroyed, ["upgrades"] = value.Upgrades.HasValue ? new JValue(value.Upgrades.Value) : JValue.CreateNull()
                } : null,
                ["rotation"] = inspectedNode.HasValue ? new JArray(inspectedNode.Value.m_Rotation.value.x,
                    inspectedNode.Value.m_Rotation.value.y, inspectedNode.Value.m_Rotation.value.z,
                    inspectedNode.Value.m_Rotation.value.w) : null,
                ["compositionFlags"] = new JArray(Observed(inspectedGeneral), Observed(inspectedLeft), Observed(inspectedRight)),
                ["defaultCount"] = inspectedDefaultCount.HasValue ? new JValue(inspectedDefaultCount.Value) : JValue.CreateNull(),
                ["rowColumns"] = new JArray("prefabIndex", "prefabVersion", "x", "y", "z", "flags"),
                ["rows"] = rows, ["rowsComplete"] = inspectedDefaultCount.HasValue && inspectedDefaultCount.Value == rows.Count
            };
        }

        private static JValue Observed(uint? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();

        private void RequireBindings()
        {
            var fields = bindings ?? throw new Rejection("PREVIEW_LANE_CONFIGURATION_MISSING");
            var queries = EntityQueries;
            Require(queries.Length == 2 && !owners.Equals(buildingSettings)
                && (queries[0].Equals(owners) && queries[1].Equals(buildingSettings)
                    || queries[1].Equals(owners) && queries[0].Equals(buildingSettings))
                && ((EntityQuery)fields.Owners.GetValue(this)!).Equals(owners)
                && ((EntityQuery)fields.BuildingSettings.GetValue(this)!).Equals(buildingSettings)
                && owners.HasFilter() && !buildingSettings.HasFilter()
                && ReferenceEquals(fields.City.GetValue(this), city) && ReferenceEquals(World.GetExistingSystemManaged<CityConfigurationSystem>(), city)
                && ReferenceEquals(fields.Tools.GetValue(this), tools) && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && ReferenceEquals(fields.Terrain.GetValue(this), terrain) && ReferenceEquals(World.GetExistingSystemManaged<TerrainSystem>(), terrain)
                && ReferenceEquals(fields.Barrier.GetValue(this), barrier) && barrier!.CreationReturned && ReferenceEquals(barrier.World, World)
                && ReferenceEquals(fields.LaneReferences.GetValue(this), sink) && sink!.CreationReturned && ReferenceEquals(sink.World, World),
                "PREVIEW_LANE_BINDINGS_CHANGED");
        }

        private void RequireUnpatched()
        {
            var result = PreviewLanePatchProbe.Observe();
            patchQualified = result.Qualified;
            Require(patchQualified, result.Reason);
        }

        private void Guard(Action scope)
        {
            scope();
            Require(World != null && World.IsCreated && !destructionStarted, "PREVIEW_LANE_SOURCE_UNAVAILABLE");
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = LanePassed, ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned,
            ["nativeJoined"] = nativeJoined, ["writerConfirmed"] = writerConfirmed, ["patchQualified"] = patchQualified,
            ["ownerCount"] = ownerCount, ["chunkCount"] = chunkCount, ["configCount"] = configCount,
            ["admittedRows"] = admitted.Length, ["commandUpperBound"] = OutputCommandUpperBound,
            ["globalRandomSeedAdvanceMayHaveOccurred"] = nativeEntered,
            ["creationReturned"] = CreationReturned, ["destructionReturned"] = DestructionReturned,
            ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot() => new JObject {
            ["passed"] = LanePassed, ["entered"] = nativeEntered, ["returned"] = nativeReturned,
            ["joined"] = nativeJoined, ["writerConfirmed"] = writerConfirmed, ["ownerCount"] = ownerCount,
            ["chunkCount"] = chunkCount, ["configCount"] = configCount, ["defaultRowsPerNode"] = admitted.Length,
            // The qualified stock wrapper advances the seed and registers the terrain reader before returning.
            ["stockSideEffectsReached"] = nativeReturned, ["failureCode"] = failureCode,
            ["originalBuffers"] = originalBuffers is { } observed ? new JObject {
                ["rootLanes"] = observed.RootLanes, ["rootUpgrades"] = observed.RootUpgrades,
                ["oldNodeLanes"] = observed.OldNodeLanes, ["oldNodeEdges"] = observed.OldNodeEdges
            } : null
        };

        private sealed class RelocationInput
        {
            internal readonly GeneratedSubObjectsPayload Cohort;
            internal readonly CompositionSelectionReceipt OldNode, NewNode;
            internal readonly (NodeGeometry Old, NodeGeometry New) Geometry;

            internal RelocationInput(GeneratedSubObjectsPayload cohort, CompositionSelectionReceipt oldNode,
                CompositionSelectionReceipt newNode, (NodeGeometry Old, NodeGeometry New) geometry)
            { Cohort = cohort; OldNode = oldNode; NewNode = newNode; Geometry = geometry; }
        }

        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "PREVIEW_LANE_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        private sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code) : base(code)
            { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Owners = Field("m_OwnerQuery", typeof(EntityQuery));
            internal readonly FieldInfo BuildingSettings = Field("m_BuildingSettingsQuery", typeof(EntityQuery));
            internal readonly FieldInfo City = Field("m_CityConfigurationSystem", typeof(CityConfigurationSystem));
            internal readonly FieldInfo Tools = Field("m_ToolSystem", typeof(ToolSystem));
            internal readonly FieldInfo Terrain = Field("m_TerrainSystem", typeof(TerrainSystem));
            internal readonly FieldInfo Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier4));
            internal readonly FieldInfo LaneReferences = Field("m_LaneReferencesSystem", typeof(LaneReferencesSystem));
            internal readonly FieldInfo AppliedTypes = Field("m_AppliedTypes", typeof(ComponentTypeSet));
            internal readonly FieldInfo DeletedTempTypes = Field("m_DeletedTempTypes", typeof(ComponentTypeSet));
            internal readonly FieldInfo TempOwnerTypes = Field("m_TempOwnerTypes", typeof(ComponentTypeSet));
            internal readonly FieldInfo HideLaneTypes = Field("m_HideLaneTypes", typeof(ComponentTypeSet));

            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(LaneSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_LANE_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
