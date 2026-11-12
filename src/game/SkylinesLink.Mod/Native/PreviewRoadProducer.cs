#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Audio;
using Game.Buildings;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using NativeBuilding = Game.Buildings.Building;
using PrefabBuildingFlags = Game.Prefabs.BuildingFlags;
using Transform = Game.Objects.Transform;
using LiveSubLane = Game.Net.SubLane;
using LiveSubObject = Game.Objects.SubObject;
using LiveSubNet = Game.Net.SubNet;
using LiveSpawnLocation = Game.Objects.SpawnLocation;
using NetSearch = Game.Net.SearchSystem;
using ObjectSearch = Game.Objects.SearchSystem;

namespace SkylinesLink.Mod.Native
{
    // Executes the stock road consumer for one scoped Temp building with a
    // bounded negative search, including the original graph during relocation.
    [DisableAutoCreation]
    public sealed partial class PreviewRoadProducer : RoadConnectionSystem
    {
        internal const float SearchRadius = 8.4f;
        private const int ConnectionPrefabLimit = 256;
        private readonly ComponentTypeSet appliedTypes = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(),
            ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>());

        private Bindings? bindings;
        private PreviewRoadBarrier? barrier;
        private PreviewRoadSinks? sinks;
        private NetSearch? netSearch;
        private ObjectSearch? objectSearch;
        private EntityQuery modifications, updatedRoads, trafficConfig, buildingConfig, connectionPrefabs;
        private EntityArchetype eventArchetype;
        private Entity root;
        private Entity[]? newGraph;
        private GeneratedSubObjectsPayload? relocation;
        private (int Subnets, int Lanes, int Children, int SecondaryUtilities)? originalInputs;
        private Action? currentScope, searchBounds;
        private RootState before, after;
        private JObject? sinkResult;
        private int actor, modificationCount, modificationChunks, updatedRoadCount, trafficCount, buildingCount, connectionCount;
        private int? pendingBuffers;
        private bool creationStarted, destructionStarted, requested, rootBeforeObserved, rootAfterObserved;
        private bool nativeEntered, nativeReturned, nativeJoined, searchBoundsAttempted, searchBoundsReturned, buildingUnchanged;
        private bool patchQualified, queryCountsObserved;
        private int methodsChecked;
        private string? methodSetSha256, failureCode;

        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal bool RoadPassed { get; private set; }

        internal void Configure(int actor, PreviewRoadBarrier barrier, PreviewRoadSinks sinks)
        {
            RequireMainThread();
            Require(bindings == null && !creationStarted && World == null && actor > 0
                && barrier != null && sinks != null, "PREVIEW_ROAD_CONFIGURATION_REJECTED");
            RequireUnpatched();
            bindings = new Bindings();
            this.actor = actor;
            this.barrier = barrier;
            this.sinks = sinks;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            RequireMainThread();
            Enabled = false;
            var fields = bindings ?? throw new Rejection("PREVIEW_ROAD_CONFIGURATION_MISSING");
            netSearch = World.GetExistingSystemManaged<NetSearch>();
            objectSearch = World.GetExistingSystemManaged<ObjectSearch>();
            Require(netSearch != null && objectSearch != null, "PREVIEW_ROAD_SOURCE_DEPENDENCY_MISSING");
            var output = sinks ?? throw new Rejection("PREVIEW_ROAD_OUTPUT_OWNER_MISSING");
            Require(barrier != null, "PREVIEW_ROAD_OUTPUT_OWNER_MISSING");

            // Keep the stock three-way modification predicate, narrowed to this
            // Disabled actor. The other four queries are stock read sources.
            modifications = GetEntityQuery(
                ModificationRole(new[] { ComponentType.ReadOnly<NativeBuilding>() }),
                ModificationRole(new[] { ComponentType.ReadOnly<LiveSpawnLocation>() }),
                ModificationRole(new[] { ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<ConnectedBuilding>() },
                    new[] { ComponentType.ReadOnly<Temp>() }));
            modifications.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            updatedRoads = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<ConnectedBuilding>(),
                ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            trafficConfig = GetEntityQuery(ComponentType.ReadOnly<TrafficConfigurationData>());
            buildingConfig = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            connectionPrefabs = GetEntityQuery(ComponentType.ReadOnly<ConnectionLaneData>(), ComponentType.ReadOnly<PrefabData>());
            eventArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(),
                ComponentType.ReadWrite<RoadConnectionUpdated>());
            RequireForUpdate(modifications);

            fields.Modifications.SetValue(this, modifications);
            fields.UpdatedRoads.SetValue(this, updatedRoads);
            fields.TrafficConfig.SetValue(this, trafficConfig);
            fields.BuildingConfig.SetValue(this, buildingConfig);
            fields.ConnectionPrefabs.SetValue(this, connectionPrefabs);
            fields.NetSearch.SetValue(this, netSearch);
            fields.ObjectSearch.SetValue(this, objectSearch);
            fields.Barrier.SetValue(this, barrier);
            fields.Icons.SetValue(this, output.Icons);
            fields.Audio.SetValue(this, output.Audio);
            fields.EventArchetype.SetValue(this, eventArchetype);
            fields.AppliedTypes.SetValue(this, appliedTypes);
            RequireBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            RequireMainThread();
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "PREVIEW_ROAD_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void SearchRelocationOnce(GeneratedSubObjectsPayload cohort, Action requireScope, Action requireSearchBounds)
        {
            Require(cohort.IsRelocation && ReferenceEquals(cohort.Source, World) && cohort.Actor == actor,
                "PREVIEW_ROAD_RELOCATION_INPUT_CHANGED");
            relocation = cohort;
            Search(cohort.Root, requireScope, requireSearchBounds);
        }

        internal void SearchOnce(Entity[] graph, Action requireScope, Action requireSearchBounds)
        {
            RequireMainThread();
            Require(!requested && relocation == null && PreviewCommitGraph.IsNewMemberCount(graph.Length),
                "PREVIEW_ROAD_NEW_GRAPH_UNQUALIFIED");
            newGraph = (Entity[])graph.Clone();
            Search(newGraph[0], requireScope, requireSearchBounds);
        }

        private void Search(Entity root, Action requireScope, Action requireSearchBounds)
        {
            RequireMainThread();
            if (requireScope == null) throw new ArgumentNullException(nameof(requireScope));
            if (requireSearchBounds == null) throw new ArgumentNullException(nameof(requireSearchBounds));
            Require(CreationReturned && !destructionStarted && !requested && root != Entity.Null && root.Index >= 0,
                "PREVIEW_ROAD_SEARCH_REJECTED");
            requested = true;
            this.root = root;
            currentScope = requireScope;
            searchBounds = requireSearchBounds;
            try
            {
                Guard(requireScope);
                Require(ShouldRunSystem(), "PREVIEW_ROAD_REQUIRED_QUERY_REJECTED");
                Enabled = true;
                Update();
                Require(nativeEntered && nativeReturned, "PREVIEW_ROAD_NATIVE_NOT_RETURNED");
                JobHandle original = Dependency;
                Require(!original.Equals(default(JobHandle)), "PREVIEW_ROAD_NATIVE_WRITER_MISSING");
                var completion = original;
                completion.Complete();
                nativeJoined = true;

                // These outputs are private even if the city identity becomes
                // stale immediately after the native job has returned.
                sinkResult = sinks!.ReadAfterJoin(original);
                Guard(requireScope);
                pendingBuffers = barrier!.PendingCount;
                after = ReadRoot(EntityManager);
                rootAfterObserved = true;
                buildingUnchanged = before.Building.Equals(after.Building);
                Require(pendingBuffers == 2, "PREVIEW_ROAD_NATIVE_BUFFERS_UNCONFIRMED");
                Require((int?)sinkResult["iconCount"] == 0 && (int?)sinkResult["audioCount"] == 0,
                    "PREVIEW_ROAD_NEGATIVE_SINK_OUTPUT");
                Require(before.Matches(after), "PREVIEW_ROAD_NEGATIVE_ROOT_CHANGED");
                Guard(requireScope);
                RoadPassed = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_ROAD_SEARCH_FAILED";
                throw;
            }
            finally
            {
                Enabled = false;
                currentScope = null;
                searchBounds = null;
            }
        }

        protected override void OnUpdate()
        {
            var scope = currentScope ?? throw new Rejection("PREVIEW_ROAD_SCOPE_MISSING");
            var requireSearchBounds = searchBounds ?? throw new Rejection("PREVIEW_ROAD_SEARCH_BOUNDS_MISSING");
            Require(requested && !nativeEntered && !searchBoundsAttempted, "PREVIEW_ROAD_NATIVE_REPEATED");
            RequireUnpatched();
            before = Qualify(scope);
            rootBeforeObserved = true;
            Require(barrier!.PendingCount == 0, "PREVIEW_ROAD_OUTPUT_ALREADY_PRESENT");
            var output = sinks ?? throw new Rejection("PREVIEW_ROAD_OUTPUT_OWNER_MISSING");
            output.RequireReady();
            searchBoundsAttempted = true;
            requireSearchBounds();
            searchBoundsReturned = true;
            Guard(scope);
            updatedRoadCount = updatedRoads.CalculateEntityCount();
            Require(updatedRoadCount == 0 && before.Matches(ReadRoot(EntityManager)),
                "PREVIEW_ROAD_INPUT_CHANGED_AT_NATIVE_ENTRY");
            output.MarkNativeEntered();
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private RootState Qualify(Action scope)
        {
            Guard(scope);
            RequireBindings();
            modificationCount = modifications.CalculateEntityCount();
            modificationChunks = modifications.CalculateChunkCount();
            updatedRoadCount = updatedRoads.CalculateEntityCount();
            trafficCount = trafficConfig.CalculateEntityCount();
            buildingCount = buildingConfig.CalculateEntityCount();
            connectionCount = connectionPrefabs.CalculateEntityCount();
            queryCountsObserved = true;
            Require(modificationCount == 1 && modificationChunks == 1, "PREVIEW_ROAD_MODIFICATION_QUERY_BOUND");
            Require(updatedRoadCount == 0, "PREVIEW_ROAD_UPDATED_NETWORK_NOT_EMPTY");
            Require(trafficCount == 1 && buildingCount == 1 && connectionCount <= ConnectionPrefabLimit,
                "PREVIEW_ROAD_CONFIGURATION_QUERY_BOUND");
            using (var members = modifications.ToEntityArray(Allocator.Temp))
                Require(members.Length == 1 && members[0] == root, "PREVIEW_ROAD_FOREIGN_MODIFICATION_TARGET");
            RootState state = ReadRoot(EntityManager);
            Require(state.Building.m_RoadEdge == Entity.Null && state.Building.m_CurvePosition == 0f,
                "PREVIEW_ROAD_EXISTING_CONNECTION_REJECTED");
            Guard(scope);
            return state;
        }

        internal static float3 ReadFront(EntityManager entities, Entity root)
        {
            RequireMainThread();
            Require(entities.Exists(root) && entities.HasComponent<NativeBuilding>(root) && entities.HasComponent<Transform>(root),
                "PREVIEW_ROAD_FRONT_ROOT_UNAVAILABLE");
            ReadPrefab(entities, root, out BuildingData data);
            return CalculateFront(entities.GetComponentData<Transform>(root), data);
        }

        private RootState ReadRoot(EntityManager entities)
        {
            RequireOwned(entities, root);
            Require(entities.HasComponent<NativeBuilding>(root) && entities.HasComponent<Transform>(root)
                && entities.HasComponent<Updated>(root) && entities.HasComponent<Created>(root)
                && !entities.HasComponent<Owner>(root) && !entities.HasComponent<Game.Net.Edge>(root)
                && !entities.HasComponent<LiveSpawnLocation>(root) && !entities.HasBuffer<ConnectedBuilding>(root)
                && !entities.HasComponent<BackSide>(root), "PREVIEW_ROAD_ROOT_ROLE_CHANGED");
            Temp temp = entities.GetComponentData<Temp>(root);
            Require(relocation == null ? temp.m_Original == Entity.Null && temp.m_Flags == (TempFlags.Create | TempFlags.Essential)
                : temp.Equals(relocation.CapturedParentTemp(root)), "PREVIEW_ROAD_ROOT_TEMP_CHANGED");
            Require(entities.HasBuffer<LiveSubLane>(root) && entities.GetBuffer<LiveSubLane>(root, true).Length == 0
                && entities.HasBuffer<SpawnLocationElement>(root) && entities.GetBuffer<SpawnLocationElement>(root, true).Length == 0
                && entities.HasBuffer<LiveSubObject>(root) && entities.HasBuffer<LiveSubNet>(root), "PREVIEW_ROAD_SECONDARY_BRANCH_UNQUALIFIED");

            var subnets = entities.GetBuffer<LiveSubNet>(root, true);
            Require(subnets.Length == 1, "PREVIEW_ROAD_SUBNET_BRANCH_UNQUALIFIED");
            Entity node = subnets[0].m_SubNet;
            RequireOwned(entities, node);
            Require((relocation == null ? newGraph != null && node == newGraph[1] : node == relocation.Node)
                && entities.HasComponent<Game.Net.Node>(node) && !entities.HasComponent<Game.Net.Edge>(node)
                && !entities.HasBuffer<ConnectedBuilding>(node) && entities.HasComponent<Owner>(node)
                && entities.GetComponentData<Owner>(node).m_Owner == root, "PREVIEW_ROAD_SUBNET_BRANCH_UNQUALIFIED");
            var children = entities.GetBuffer<LiveSubObject>(root, true);
            Require(children.Length == (newGraph == null ? 4 : newGraph.Length - 3), "PREVIEW_ROAD_SECONDARY_BRANCH_UNQUALIFIED");
            var seen = newGraph == null ? null : new HashSet<Entity>();
            for (int index = 0; index < children.Length; index++)
            {
                Entity child = children[index].m_SubObject;
                RequireOwned(entities, child);
                Require(entities.HasComponent<Owner>(child) && entities.GetComponentData<Owner>(child).m_Owner == root
                    && !IsSecondaryUtility(entities, child)
                    && (newGraph == null || Array.IndexOf(newGraph, child, 2) >= 2 && seen!.Add(child)),
                    "PREVIEW_ROAD_SECONDARY_BRANCH_UNQUALIFIED");
            }
            if (newGraph != null)
            {
                Require(entities.HasBuffer<LiveSubObject>(node), "PREVIEW_ROAD_NODE_CHILD_UNQUALIFIED");
                var nodeChildren = entities.GetBuffer<LiveSubObject>(node, true);
                Require(nodeChildren.Length == 1, "PREVIEW_ROAD_NODE_CHILD_UNQUALIFIED");
                Entity child = nodeChildren[0].m_SubObject;
                RequireOwned(entities, child);
                Require(Array.IndexOf(newGraph, child, 2) >= 2 && seen!.Add(child)
                    && entities.HasComponent<Owner>(child) && entities.GetComponentData<Owner>(child).m_Owner == node
                    && seen!.Count == newGraph.Length - 2, "PREVIEW_ROAD_NODE_CHILD_UNQUALIFIED");
            }

            Entity prefab = ReadPrefab(entities, root, out BuildingData data);
            Transform transform = entities.GetComponentData<Transform>(root);
            NativeBuilding? original = relocation == null ? null : ReadOriginal(entities, relocation);
            return new RootState(prefab, node, entities.GetComponentData<NativeBuilding>(root), temp, transform,
                data, CalculateFront(transform, data), original);
        }

        private NativeBuilding ReadOriginal(EntityManager entities, GeneratedSubObjectsPayload cohort)
        {
            Entity building = cohort.CapturedParentTemp(root).m_Original;
            Entity node = cohort.CapturedParentTemp(cohort.OldNode).m_Original;
            Require(IsFormal(entities, building) && entities.HasComponent<NativeBuilding>(building)
                && IsFormal(entities, node) && entities.HasComponent<Game.Net.Node>(node)
                && !entities.HasBuffer<ConnectedBuilding>(node) && !entities.HasComponent<Game.Net.Edge>(node)
                && !entities.HasComponent<Game.Net.Curve>(node) && entities.HasComponent<Owner>(node)
                && entities.GetComponentData<Owner>(node).m_Owner == building,
                "PREVIEW_ROAD_ORIGINAL_ROLES_CHANGED");

            var subnets = entities.GetBuffer<LiveSubNet>(building, true);
            int laneCount = entities.HasBuffer<LiveSubLane>(building) ? entities.GetBuffer<LiveSubLane>(building, true).Length : -1;
            var children = entities.GetBuffer<LiveSubObject>(building, true);
            originalInputs = (subnets.Length, laneCount, children.Length, -1);
            Require(subnets.Length == 1 && subnets[0].m_SubNet == node && laneCount <= 0 && children.Length == 4,
                "PREVIEW_ROAD_ORIGINAL_BUFFERS_UNSUPPORTED");

            int secondary = 0;
            for (int i = 0; i < children.Length; i++)
            {
                Entity child = children[i].m_SubObject;
                Require(IsFormal(entities, child) && entities.HasComponent<Owner>(child)
                    && entities.GetComponentData<Owner>(child).m_Owner == building, "PREVIEW_ROAD_ORIGINAL_CHILD_CHANGED");
                if (IsSecondaryUtility(entities, child)) secondary++;
            }
            originalInputs = (subnets.Length, laneCount, children.Length, secondary);
            Require(secondary == 0, "PREVIEW_ROAD_ORIGINAL_SECONDARY_UTILITY_UNSUPPORTED");

            var value = entities.GetComponentData<NativeBuilding>(building);
            Require(value.m_RoadEdge == Entity.Null && value.m_CurvePosition == 0f,
                "PREVIEW_ROAD_ORIGINAL_CONNECTION_UNSUPPORTED");
            return value;
        }

        private static bool IsFormal(EntityManager entities, Entity entity) =>
            entities.Exists(entity) && !entities.HasComponent<Temp>(entity) && !entities.HasComponent<Deleted>(entity)
                && !entities.HasComponent<ObjectGenerationScope>(entity) && !entities.HasComponent<Unity.Entities.Disabled>(entity);

        private static bool IsSecondaryUtility(EntityManager entities, Entity entity) =>
            entities.HasComponent<Game.Objects.UtilityObject>(entity) && entities.HasComponent<Game.Objects.Secondary>(entity);

        private static Entity ReadPrefab(EntityManager entities, Entity root, out BuildingData data)
        {
            Require(entities.HasComponent<PrefabRef>(root), "PREVIEW_ROAD_PREFAB_UNAVAILABLE");
            Entity prefab = entities.GetComponentData<PrefabRef>(root).m_Prefab;
            Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && entities.HasComponent<BuildingData>(prefab), "PREVIEW_ROAD_PREFAB_UNAVAILABLE");
            data = entities.GetComponentData<BuildingData>(prefab);
            return prefab;
        }

        private static float3 CalculateFront(Transform transform, BuildingData data)
        {
            const PrefabBuildingFlags unsupported = PrefabBuildingFlags.NoRoadConnection | PrefabBuildingFlags.RequireRoad
                | PrefabBuildingFlags.BackAccess | PrefabBuildingFlags.CanBeOnRoad | PrefabBuildingFlags.CanBeOnRoadArea;
            Require((data.m_Flags & unsupported) == 0 && data.m_LotSize.y > 0
                && math.all(math.isfinite(transform.m_Position)) && math.all(math.isfinite(transform.m_Rotation.value))
                && math.abs(math.lengthsq(transform.m_Rotation.value) - 1f) <= 0.0001f, "PREVIEW_ROAD_FRONT_BRANCH_UNQUALIFIED");
            float3 front = BuildingUtils.CalculateFrontPosition(transform, data.m_LotSize.y);
            Require(math.all(math.isfinite(front)), "PREVIEW_ROAD_FRONT_INVALID");
            return front;
        }

        private void RequireOwned(EntityManager entities, Entity entity)
        {
            Require(entity != Entity.Null && entities.Exists(entity) && entities.HasComponent<Unity.Entities.Disabled>(entity)
                && entities.HasComponent<Temp>(entity) && !entities.HasComponent<Deleted>(entity)
                && entities.HasComponent<ObjectGenerationScope>(entity)
                && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor, "PREVIEW_ROAD_SCOPE_CHANGED");
        }

        private static EntityQueryDesc ModificationRole(ComponentType[] required, ComponentType[]? excluded = null)
        {
            var all = new ComponentType[required.Length + 2];
            Array.Copy(required, all, required.Length);
            all[required.Length] = ComponentType.ReadOnly<ObjectGenerationScope>();
            all[required.Length + 1] = ComponentType.ReadOnly<Unity.Entities.Disabled>();
            return new EntityQueryDesc
            {
                All = all, Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = excluded ?? Array.Empty<ComponentType>(), Options = EntityQueryOptions.IncludeDisabledEntities
            };
        }

        private void RequireBindings()
        {
            var fields = bindings ?? throw new Rejection("PREVIEW_ROAD_CONFIGURATION_MISSING");
            var output = sinks ?? throw new Rejection("PREVIEW_ROAD_OUTPUT_OWNER_MISSING");
            var registered = EntityQueries;
            Require(registered.Length == 5 && QueryMatches(fields.Modifications, modifications, registered, true)
                && QueryMatches(fields.UpdatedRoads, updatedRoads, registered, false)
                && QueryMatches(fields.TrafficConfig, trafficConfig, registered, false)
                && QueryMatches(fields.BuildingConfig, buildingConfig, registered, false)
                && QueryMatches(fields.ConnectionPrefabs, connectionPrefabs, registered, false)
                && ReferenceEquals(fields.NetSearch.GetValue(this), netSearch)
                && ReferenceEquals(fields.ObjectSearch.GetValue(this), objectSearch)
                && ReferenceEquals(World.GetExistingSystemManaged<NetSearch>(), netSearch)
                && ReferenceEquals(World.GetExistingSystemManaged<ObjectSearch>(), objectSearch)
                && ReferenceEquals(fields.Barrier.GetValue(this), barrier) && barrier!.CreationReturned && ReferenceEquals(barrier.World, World)
                && ReferenceEquals(fields.Icons.GetValue(this), output.Icons) && ReferenceEquals(fields.Audio.GetValue(this), output.Audio)
                && eventArchetype.Valid && ((EntityArchetype)fields.EventArchetype.GetValue(this)!).Equals(eventArchetype)
                && SameTypes((ComponentTypeSet)fields.AppliedTypes.GetValue(this)!, appliedTypes), "PREVIEW_ROAD_BINDINGS_CHANGED");
        }

        private bool QueryMatches(FieldInfo field, EntityQuery query, EntityQuery[] registered, bool filtered)
        {
            int matches = 0;
            foreach (EntityQuery value in registered) if (value.Equals(query)) matches++;
            return matches == 1 && query.HasFilter() == filtered && ((EntityQuery)field.GetValue(this)!).Equals(query);
        }

        private static bool SameTypes(ComponentTypeSet actual, ComponentTypeSet expected)
        {
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++) if (actual.GetTypeIndex(i) != expected.GetTypeIndex(i)) return false;
            return true;
        }

        private void RequireUnpatched()
        {
            var result = PreviewRoadPatchProbe.Observe();
            patchQualified = result.Qualified;
            methodsChecked = result.MethodsChecked;
            methodSetSha256 = result.MethodSetSha256;
            Require(patchQualified, result.Reason);
        }

        private void Guard(Action scope)
        {
            scope();
            Require(World != null && World.IsCreated && !destructionStarted, "PREVIEW_ROAD_SOURCE_UNAVAILABLE");
        }

        internal JObject Snapshot() => new JObject
        {
            ["passed"] = RoadPassed, ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
            ["searchBoundsAttempted"] = searchBoundsAttempted, ["searchBoundsReturned"] = searchBoundsReturned,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["patchQualified"] = patchQualified,
            ["methodsChecked"] = methodsChecked, ["methodSetSha256"] = methodSetSha256,
            ["modificationCount"] = ObservedCount(modificationCount), ["modificationChunks"] = ObservedCount(modificationChunks),
            ["updatedRoadCount"] = ObservedCount(updatedRoadCount), ["trafficConfigCount"] = ObservedCount(trafficCount),
            ["buildingConfigCount"] = ObservedCount(buildingCount), ["connectionPrefabCount"] = ObservedCount(connectionCount),
            ["pendingBuffers"] = pendingBuffers, ["sinks"] = sinkResult,
            ["front"] = rootBeforeObserved ? new JArray(before.Front.x, before.Front.y, before.Front.z) : null,
            ["beforeRoad"] = rootBeforeObserved ? Id(before.Building.m_RoadEdge) : null,
            ["afterRoad"] = rootAfterObserved ? Id(after.Building.m_RoadEdge) : null,
            ["beforeCurve"] = rootBeforeObserved ? Finite(before.Building.m_CurvePosition) : null,
            ["afterCurve"] = rootAfterObserved ? Finite(after.Building.m_CurvePosition) : null,
            ["buildingUnchanged"] = buildingUnchanged, ["creationReturned"] = CreationReturned,
            ["destructionReturned"] = DestructionReturned, ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot() => new JObject {
            ["entered"] = nativeEntered, ["returned"] = nativeReturned, ["joined"] = nativeJoined,
            ["modifications"] = ObservedCount(modificationCount), ["chunks"] = ObservedCount(modificationChunks),
            ["updatedRoads"] = ObservedCount(updatedRoadCount), ["buildingUnchanged"] = buildingUnchanged,
            ["iconCount"] = sinkResult?["iconCount"]?.DeepClone(), ["audioCount"] = sinkResult?["audioCount"]?.DeepClone(),
            ["originalInputs"] = originalInputs is { } observed ? new JObject {
                ["subnets"] = observed.Subnets, ["lanes"] = observed.Lanes, ["children"] = observed.Children,
                ["secondaryUtilities"] = observed.SecondaryUtilities
            } : null,
            ["failureCode"] = failureCode
        };

        private JToken ObservedCount(int value) => queryCountsObserved ? new JValue(value) : JValue.CreateNull();
        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static JToken Finite(float value) => math.isfinite(value) ? new JValue(value) : JValue.CreateNull();
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "PREVIEW_ROAD_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code) : base(code)
            { Code = code; }
        }

        private readonly struct RootState
        {
            internal readonly Entity Prefab, Node;
            internal readonly NativeBuilding Building;
            internal readonly NativeBuilding? OriginalBuilding;
            internal readonly Temp Temp;
            internal readonly Transform Transform;
            internal readonly BuildingData PrefabData;
            internal readonly float3 Front;
            internal RootState(Entity prefab, Entity node, NativeBuilding building, Temp temp, Transform transform,
                BuildingData prefabData, float3 front, NativeBuilding? originalBuilding)
            {
                Prefab = prefab; Node = node; Building = building; Temp = temp; Transform = transform;
                PrefabData = prefabData; Front = front; OriginalBuilding = originalBuilding;
            }
            internal bool Matches(RootState other) => Prefab == other.Prefab && Node == other.Node && Building.Equals(other.Building)
                && Temp.Equals(other.Temp) && Transform.Equals(other.Transform) && PrefabData.Equals(other.PrefabData) && Front.Equals(other.Front)
                && Nullable.Equals(OriginalBuilding, other.OriginalBuilding);
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Modifications = Field("m_ModificationQuery", typeof(EntityQuery));
            internal readonly FieldInfo UpdatedRoads = Field("m_UpdatedNetQuery", typeof(EntityQuery));
            internal readonly FieldInfo TrafficConfig = Field("m_TrafficConfigQuery", typeof(EntityQuery));
            internal readonly FieldInfo BuildingConfig = Field("m_BuildingConfigQuery", typeof(EntityQuery));
            internal readonly FieldInfo ConnectionPrefabs = Field("m_ConnectionQuery", typeof(EntityQuery));
            internal readonly FieldInfo NetSearch = Field("m_NetSearchSystem", typeof(NetSearch));
            internal readonly FieldInfo ObjectSearch = Field("m_ObjectSearchSystem", typeof(ObjectSearch));
            internal readonly FieldInfo Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier4B));
            internal readonly FieldInfo Icons = Field("m_IconCommandSystem", typeof(IconCommandSystem));
            internal readonly FieldInfo Audio = Field("m_AudioManager", typeof(AudioManager));
            internal readonly FieldInfo EventArchetype = Field("m_RoadConnectionEventArchetype", typeof(EntityArchetype));
            internal readonly FieldInfo AppliedTypes = Field("m_AppliedTypes", typeof(ComponentTypeSet));

            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(RoadConnectionSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_ROAD_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
