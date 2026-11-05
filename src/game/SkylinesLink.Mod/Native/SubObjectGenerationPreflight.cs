#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using NativeNode = Game.Net.Node;
using NativeTransform = Game.Objects.Transform;
using PrefabSubObject = Game.Prefabs.SubObject;

namespace SkylinesLink.Mod.Native
{
    // Admission for this new-only scalar fixture, not a replacement for native
    // selection or generation. Every Run reads the current Source again.
    internal sealed class SubObjectGenerationPreflight
    {
        private const int TypeLimit = 256, TotalTypeLimit = 1024;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private IReadOnlyList<Candidate> candidates = Array.Empty<Candidate>();
        private Dictionary<Entity, Leaf> leaves = new Dictionary<Entity, Leaf>();
        private bool passed;
        private int runs, typesRead, queryAttempts, queriesAcquired, queriesDisposed;
        private bool serviceUpkeepRelevant;
        private int rootServiceUpkeepCount, nodeServiceUpkeepCount;
        private string? failureCode, exceptionType;
        private Temp rootTemp, nodeTemp;

        internal SubObjectGenerationPreflight(World source, Entity root, Entity node, int actor)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            if (root == Entity.Null || node == Entity.Null || root.Index < 0 || node.Index < 0 || root == node || actor <= 0)
                throw new ArgumentException("SUBOBJECT_PREFLIGHT_IDENTITY_REJECTED");
            Root = root; Node = node; Actor = actor;
        }

        internal World Source { get; }
        internal Entity Root { get; }
        internal Entity Node { get; }
        internal int Actor { get; }
        internal bool Passed => passed;
        internal bool ResourcesReleased => archetypes.ResourcesReleased
            && queryAttempts == queriesAcquired && queriesAcquired == queriesDisposed;
        internal int CreateUpperBound => passed ? 5 : 0;
        internal int CommandUpperBound => passed ? 47 : 0;
        internal IReadOnlyList<Candidate> Candidates => passed ? candidates : Array.Empty<Candidate>();
        internal NativeTransform RootTransform { get; private set; }
        internal NativeNode NodeData { get; private set; }
        internal float RootElevation { get; private set; }
        internal float NodeElevation { get; private set; }
        internal BuildingConfigurationData BuildingConfiguration { get; private set; }
        internal CitizenHappinessParameterData HappinessParameters { get; private set; }
        internal Entity DefaultTheme { get; private set; }

        internal Leaf GetLeaf(Entity prefab)
        {
            Require(passed && leaves.TryGetValue(prefab, out _), "SUBOBJECT_PREFLIGHT_LEAF_UNAVAILABLE");
            return leaves[prefab];
        }

        internal Temp OwnerTemp(Entity owner)
        {
            Require(passed && (owner == Root || owner == Node), "SUBOBJECT_PREFLIGHT_OWNER_UNAVAILABLE");
            return owner == Root ? rootTemp : nodeTemp;
        }

        internal float OwnerElevation(Entity owner)
        {
            Require(passed && (owner == Root || owner == Node), "SUBOBJECT_PREFLIGHT_OWNER_UNAVAILABLE");
            return owner == Root ? RootElevation : NodeElevation;
        }

        internal void Run(Action requireCurrentScope)
        {
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            passed = false; failureCode = exceptionType = null; typesRead = 0; runs++;
            try
            {
                Guard(requireCurrentScope);
                var entities = Source.EntityManager;
                var tool = Source.GetExistingSystemManaged<Game.Tools.ToolSystem>();
                var city = Source.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                var terrain = Source.GetExistingSystemManaged<Game.Simulation.TerrainSystem>();
                var water = Source.GetExistingSystemManaged<Game.Simulation.WaterSystem>();
                var prefabs = Source.GetExistingSystemManaged<PrefabSystem>();
                Guard(requireCurrentScope);
                Require(tool != null && city != null && terrain != null && water != null && prefabs != null
                    && ReferenceEquals(tool.World, Source) && ReferenceEquals(city.World, Source)
                    && ReferenceEquals(terrain.World, Source) && ReferenceEquals(water.World, Source)
                    && ReferenceEquals(prefabs.World, Source) && !tool.actionMode.IsEditor(),
                    "SUBOBJECT_PREFLIGHT_ENVIRONMENT_UNAVAILABLE");

                Temp freshRootTemp = Owner(entities, Root, 9, requireCurrentScope);
                Temp freshNodeTemp = Owner(entities, Node, 5, requireCurrentScope);
                Require(!entities.HasComponent<Owner>(Root) && entities.HasComponent<Owner>(Node)
                    && entities.GetComponentData<Owner>(Node).m_Owner == Root
                    && !entities.HasComponent<OwnerDefinition>(Root) && !entities.HasComponent<OwnerDefinition>(Node),
                    "SUBOBJECT_PREFLIGHT_OWNER_CHAIN_CHANGED");
                Require(entities.HasComponent<NativeTransform>(Root) && entities.HasComponent<NativeNode>(Node)
                    && !entities.HasComponent<NativeTransform>(Node) && !entities.HasComponent<Game.Net.Edge>(Root)
                    && !entities.HasComponent<Game.Net.Edge>(Node), "SUBOBJECT_PREFLIGHT_OWNER_ROLE_CHANGED");
                NativeTransform transform = entities.GetComponentData<NativeTransform>(Root);
                NativeNode nodeData = entities.GetComponentData<NativeNode>(Node);
                Require(Finite(transform.m_Position) && Finite(transform.m_Rotation.value)
                    && Finite(nodeData.m_Position) && Finite(nodeData.m_Rotation.value), "SUBOBJECT_PREFLIGHT_OWNER_GEOMETRY_INVALID");
                Require(entities.HasBuffer<Game.Net.ConnectedEdge>(Node), "SUBOBJECT_PREFLIGHT_CONNECTED_EDGES_MISSING");
                Empty<Game.Net.ConnectedEdge>(entities, Node);
                float rootHeight = ReadElevation(entities, Root), nodeHeight = ReadElevation(entities, Node);

                Entity rootPrefab = Prefab(entities, prefabs!, Root), nodePrefab = Prefab(entities, prefabs!, Node);
                serviceUpkeepRelevant = entities.HasComponent<Game.City.CityServiceUpkeep>(Root);
                Guard(requireCurrentScope);
                rootServiceUpkeepCount = nodeServiceUpkeepCount = 0;
                if (serviceUpkeepRelevant)
                {
                    rootServiceUpkeepCount = BoundedLength<ServiceUpkeepData>(entities, rootPrefab, requireCurrentScope);
                    nodeServiceUpkeepCount = BoundedLength<ServiceUpkeepData>(entities, nodePrefab, requireCurrentScope);
                }
                Require(!entities.HasComponent<MovingObjectData>(rootPrefab)
                    && (!entities.HasComponent<PlaceableObjectData>(rootPrefab)
                        || (entities.GetComponentData<PlaceableObjectData>(rootPrefab).m_Flags & Game.Objects.PlacementFlags.Swaying) == 0),
                    "SUBOBJECT_PREFLIGHT_RELATIVE_BRANCH_UNSUPPORTED");
                Guard(requireCurrentScope);
                var freshLeaves = new Dictionary<Entity, Leaf>();
                var freshCandidates = new List<Candidate>(5);
                ReadRows(entities, prefabs!, Root, rootPrefab, 4, freshCandidates, freshLeaves, requireCurrentScope);
                ReadRows(entities, prefabs!, Node, nodePrefab, 1, freshCandidates, freshLeaves, requireCurrentScope);
                Require(freshCandidates.Count == 5 && freshLeaves.Count == 4, "SUBOBJECT_PREFLIGHT_COHORT_CHANGED");
                int lights = 0;
                foreach (Candidate candidate in freshCandidates)
                    if (freshLeaves[candidate.Prefab].StreetLight.HasValue) lights++;
                Require(lights == 1, "SUBOBJECT_PREFLIGHT_STREET_LIGHT_COHORT_CHANGED");
                Require(entities.HasComponent<Game.Buildings.Building>(Root)
                    && !entities.HasComponent<Game.Vehicles.Watercraft>(Root)
                    && !entities.HasComponent<Game.Vehicles.Watercraft>(Node),
                    "SUBOBJECT_PREFLIGHT_STREET_LIGHT_OWNER_BRANCH_CHANGED");
                foreach (Candidate candidate in freshCandidates)
                    Require(!freshLeaves[candidate.Prefab].StreetLight.HasValue || candidate.Owner == Root,
                        "SUBOBJECT_PREFLIGHT_STREET_LIGHT_OWNER_BRANCH_CHANGED");
                Guard(requireCurrentScope);

                BuildingConfigurationData building = Singleton<BuildingConfigurationData>(requireCurrentScope);
                CitizenHappinessParameterData happiness = Singleton<CitizenHappinessParameterData>(requireCurrentScope);
                Entity theme = city!.defaultTheme;
                PrefabReference(entities, theme); PrefabReference(entities, building.m_DefaultRenterBrand);
                Guard(requireCurrentScope);
                Require(ResourcesReleased, "SUBOBJECT_PREFLIGHT_STORAGE_UNCONFIRMED");
                candidates = freshCandidates.AsReadOnly(); leaves = freshLeaves;
                RootTransform = transform; NodeData = nodeData; RootElevation = rootHeight; NodeElevation = nodeHeight;
                rootTemp = freshRootTemp; nodeTemp = freshNodeTemp; DefaultTheme = theme;
                BuildingConfiguration = building; HappinessParameters = happiness;
                passed = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "SUBOBJECT_PREFLIGHT_READ_FAILED";
                exceptionType = error.GetType().Name;
                throw;
            }
        }

        private Temp Owner(EntityManager entities, Entity entity, uint flags, Action scope)
        {
            Require(entities.Exists(entity) && entities.HasComponent<Disabled>(entity)
                && entities.HasComponent<ObjectGenerationScope>(entity)
                && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == Actor
                && entities.HasComponent<Created>(entity) && entities.HasComponent<Updated>(entity)
                && entities.HasComponent<Temp>(entity), "SUBOBJECT_PREFLIGHT_OWNER_SCOPE_CHANGED");
            Require(!entities.HasComponent<Deleted>(entity) && !entities.HasComponent<Game.Objects.Attachment>(entity)
                && !entities.HasComponent<Game.Objects.Attached>(entity) && !entities.HasComponent<Game.Objects.UnderConstruction>(entity)
                && !entities.HasComponent<Destroyed>(entity) && !entities.HasComponent<Overridden>(entity)
                && !entities.HasComponent<Game.Common.Native>(entity), "SUBOBJECT_PREFLIGHT_OWNER_BRANCH_UNSUPPORTED");
            Require(entities.HasBuffer<Game.Objects.SubObject>(entity), "SUBOBJECT_PREFLIGHT_SUBOBJECT_BUFFER_MISSING");
            Temp temp = entities.GetComponentData<Temp>(entity);
            Require(temp.m_Original == Entity.Null && (uint)temp.m_Flags == flags
                && Finite(temp.m_CurvePosition), "SUBOBJECT_PREFLIGHT_TEMP_CHANGED");
            Empty<Game.Objects.SubObject>(entities, entity);
            Empty<Game.Buildings.InstalledUpgrade>(entities, entity);
            Empty<Game.Buildings.Renter>(entities, entity);
            Empty<LinkedEntityGroup>(entities, entity);
            Guard(scope); return temp;
        }

        private void ReadRows(EntityManager entities, PrefabSystem prefabs, Entity owner, Entity prefab, int expected,
            List<Candidate> result, Dictionary<Entity, Leaf> leafSet, Action scope)
        {
            Empty<PlaceholderObjectElement>(entities, prefab); Empty<ObjectRequirementElement>(entities, prefab);
            Require(!entities.HasComponent<PlaceholderObjectData>(prefab) && entities.HasBuffer<PrefabSubObject>(prefab),
                "SUBOBJECT_PREFLIGHT_OWNER_PREFAB_BRANCH_UNSUPPORTED");
            var buffer = entities.GetBuffer<PrefabSubObject>(prefab, true);
            Require(buffer.Length == expected, "SUBOBJECT_PREFLIGHT_DECLARED_ROWS_CHANGED");
            var rows = new PrefabSubObject[expected];
            for (int index = 0; index < expected; index++) rows[index] = buffer[index];
            Guard(scope);
            for (int index = 0; index < rows.Length; index++)
            {
                PrefabSubObject row = rows[index];
                Require(row.m_Prefab != Entity.Null && (uint)row.m_Flags == 0 && row.m_ParentIndex == 0
                    && row.m_GroupIndex == 0 && row.m_Probability == 100
                    && Finite(row.m_Position) && Finite(row.m_Rotation.value), "SUBOBJECT_PREFLIGHT_ROW_BRANCH_UNSUPPORTED");
                if (!leafSet.TryGetValue(row.m_Prefab, out Leaf leaf))
                {
                    Require(leafSet.Count < 4, "SUBOBJECT_PREFLIGHT_LEAF_COUNT_BUDGET");
                    leaf = ReadLeaf(entities, prefabs, row.m_Prefab, scope); leafSet.Add(row.m_Prefab, leaf);
                }
                result.Add(new Candidate(owner, index, row, leaf.Archetype));
            }
        }

        private Leaf ReadLeaf(EntityManager entities, PrefabSystem prefabs, Entity prefab, Action scope)
        {
            Registered(entities, prefabs, prefab);
            Require(entities.HasComponent<ObjectData>(prefab) && entities.HasComponent<ObjectGeometryData>(prefab),
                "SUBOBJECT_PREFLIGHT_LEAF_DATA_MISSING");
            Require(!entities.HasComponent<PlaceholderObjectData>(prefab) && !entities.HasComponent<PillarData>(prefab)
                && !entities.HasComponent<TreeData>(prefab) && !entities.HasComponent<StackData>(prefab)
                && !entities.HasComponent<SpawnLocationData>(prefab), "SUBOBJECT_PREFLIGHT_LEAF_BRANCH_UNSUPPORTED");
            Empty<PlaceholderObjectElement>(entities, prefab); Empty<ObjectRequirementElement>(entities, prefab);
            Empty<PrefabSubObject>(entities, prefab);
            ObjectData data = entities.GetComponentData<ObjectData>(prefab);
            Require(data.m_Archetype.Valid && data.m_Archetype.TypesCount > 0 && data.m_Archetype.TypesCount <= TypeLimit
                && data.m_Archetype.TypesCount <= TotalTypeLimit - typesRead, "SUBOBJECT_PREFLIGHT_TYPE_BUDGET");
            typesRead += data.m_Archetype.TypesCount;
            Dictionary<TypeIndex, ComponentType> types = archetypes.Read(data.m_Archetype, TypeLimit);
            var metadata = new Dictionary<TypeIndex, TypeFacts>();
            foreach (TypeIndex index in types.Keys)
            {
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                Require(!index.IsSharedComponentType && !index.IsChunkComponent && !index.HasNativeContainer && !index.IsRefCounted
                    && !info.HasBlobAssetRefs && !info.HasWeakAssetRefs && !info.HasUnityObjectRefs
                    && info.BlobAssetRefOffsetCount == 0 && info.WeakAssetRefOffsetCount == 0 && info.UnityObjectRefOffsetCount == 0,
                    "SUBOBJECT_PREFLIGHT_TYPE_METADATA_UNSUPPORTED");
                Require(info.EntityOffsetCount == 0 || index == TypeManager.GetTypeIndex<PrefabRef>() && info.EntityOffsetCount == 1,
                    "SUBOBJECT_PREFLIGHT_ENTITY_REFERENCE_LAYOUT_UNSUPPORTED");
                metadata.Add(index, new TypeFacts(info, index));
            }
            SetTarget<PrefabRef>(types); SetTarget<NativeTransform>(types); SetTarget<PseudoRandomSeed>(types);
            Require(!types.ContainsKey(TypeManager.GetTypeIndex<Disabled>())
                && !types.ContainsKey(TypeManager.GetTypeIndex<ObjectGenerationScope>()), "SUBOBJECT_PREFLIGHT_LEAF_ALREADY_SCOPED");
            StreetLightData? light = entities.HasComponent<StreetLightData>(prefab)
                ? entities.GetComponentData<StreetLightData>(prefab) : (StreetLightData?)null;
            if (light.HasValue) SetTarget<Game.Objects.StreetLight>(types);
            ObjectGeometryData geometry = entities.GetComponentData<ObjectGeometryData>(prefab);
            Require(Finite(geometry.m_Bounds.min) && Finite(geometry.m_Bounds.max) && Finite(geometry.m_Size)
                && Finite(geometry.m_Pivot) && Finite(geometry.m_LegSize) && Finite(geometry.m_LegOffset),
                "SUBOBJECT_PREFLIGHT_LEAF_GEOMETRY_INVALID");
            PlaceableObjectData? placeable = entities.HasComponent<PlaceableObjectData>(prefab)
                ? entities.GetComponentData<PlaceableObjectData>(prefab) : (PlaceableObjectData?)null;
            Require(!placeable.HasValue || Finite(placeable.Value.m_PlacementOffset), "SUBOBJECT_PREFLIGHT_PLACEMENT_OFFSET_INVALID");
            Require(!placeable.HasValue || (placeable.Value.m_Flags & Game.Objects.PlacementFlags.Attached) == 0,
                "SUBOBJECT_PREFLIGHT_ATTACHED_CHILD_UNSUPPORTED");
            Guard(scope);
            return new Leaf(prefab, data, geometry, placeable, light, types, metadata);
        }

        private T Singleton<T>(Action scope) where T : unmanaged, IComponentData
        {
            EntityQuery query = default; bool acquired = false;
            queryAttempts++;
            try
            {
                query = Source.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
                acquired = true; queriesAcquired++; Guard(scope);
                query.CompleteDependency(); Guard(scope);
                Require(query.CalculateEntityCount() == 1, "SUBOBJECT_PREFLIGHT_SINGLETON_NOT_UNIQUE");
                T value = query.GetSingleton<T>(); Guard(scope); return value;
            }
            finally { if (acquired) { query.Dispose(); queriesDisposed++; } }
        }

        private static void SetTarget<T>(Dictionary<TypeIndex, ComponentType> types) where T : unmanaged, IComponentData
        { Require(types.ContainsKey(TypeManager.GetTypeIndex<T>()), "SUBOBJECT_PREFLIGHT_SET_TARGET_MISSING"); }

        private static Entity Prefab(EntityManager entities, PrefabSystem prefabs, Entity entity)
        {
            Require(entities.HasComponent<PrefabRef>(entity), "SUBOBJECT_PREFLIGHT_OWNER_PREFAB_MISSING");
            Entity prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
            Registered(entities, prefabs, prefab); return prefab;
        }

        private static void Registered(EntityManager entities, PrefabSystem prefabs, Entity prefab)
        {
            Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && !entities.HasComponent<Deleted>(prefab)
                && prefabs.TryGetPrefab<PrefabBase>(prefab, out PrefabBase managed) && managed != null
                && prefabs.TryGetEntity(managed, out Entity roundtrip) && roundtrip == prefab,
                "SUBOBJECT_PREFLIGHT_PREFAB_ROUNDTRIP_FAILED");
        }

        private static void PrefabReference(EntityManager entities, Entity reference)
        { Require(reference == Entity.Null || entities.Exists(reference) && entities.HasComponent<PrefabData>(reference), "SUBOBJECT_PREFLIGHT_CONFIGURATION_REFERENCE_MISSING"); }

        private static float ReadElevation(EntityManager entities, Entity entity)
        {
            float value = entities.HasComponent<Game.Objects.Elevation>(entity)
                ? entities.GetComponentData<Game.Objects.Elevation>(entity).m_Elevation
                : entities.HasComponent<Game.Net.Elevation>(entity) ? math.cmin(entities.GetComponentData<Game.Net.Elevation>(entity).m_Elevation) : 0f;
            Require(Finite(value), "SUBOBJECT_PREFLIGHT_ELEVATION_INVALID"); return value;
        }

        private int BoundedLength<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IBufferElementData
        {
            bool present = entities.HasBuffer<T>(entity); Guard(scope);
            if (!present) return 0;
            var buffer = entities.GetBuffer<T>(entity, true); Guard(scope);
            int length = buffer.Length; Guard(scope);
            Require(length >= 0 && length <= 64, "SUBOBJECT_PREFLIGHT_SERVICE_UPKEEP_BUDGET");
            return length;
        }

        private static void Empty<T>(EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData
        { Require(!entities.HasBuffer<T>(entity) || entities.GetBuffer<T>(entity, true).Length == 0, "SUBOBJECT_PREFLIGHT_NONEMPTY_BUFFER"); }

        private void Guard(Action scope)
        {
            Require(MainThreadDispatcher.isMainThread, "SUBOBJECT_PREFLIGHT_MAIN_THREAD_REQUIRED");
            scope();
            GameManager manager = GameManager.instance;
            Require(Source.IsCreated && manager != null && manager.state == GameManager.State.WorldReady
                && manager.gameMode == GameMode.Game && !manager.isGameLoading, "SUBOBJECT_PREFLIGHT_SOURCE_NOT_READY");
        }

        internal JObject Snapshot() => new JObject {
            ["serviceUpkeepRelevant"] = serviceUpkeepRelevant,
            ["rootServiceUpkeepCount"] = rootServiceUpkeepCount, ["nodeServiceUpkeepCount"] = nodeServiceUpkeepCount,
            ["passed"] = passed, ["runs"] = runs, ["candidateRows"] = passed ? candidates.Count : 0,
            ["uniqueLeaves"] = passed ? leaves.Count : 0, ["createUpperBound"] = CreateUpperBound,
            ["commandUpperBound"] = CommandUpperBound, ["typesRead"] = typesRead,
            ["typeArrays"] = archetypes.Snapshot(), ["queryAttempts"] = queryAttempts,
            ["queriesAcquired"] = queriesAcquired, ["queriesDisposed"] = queriesDisposed,
            ["resourcesReleased"] = ResourcesReleased, ["nativeInvoked"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };

        internal sealed class Candidate
        {
            internal readonly Entity Owner, Prefab;
            internal readonly int RowIndex, ParentIndex, GroupIndex, Probability;
            internal readonly float3 Position;
            internal readonly quaternion Rotation;
            internal readonly PrefabSubObject Definition;
            internal readonly EntityArchetype Archetype;
            internal Candidate(Entity owner, int index, PrefabSubObject value, EntityArchetype archetype)
            {
                Owner = owner; RowIndex = index; Prefab = value.m_Prefab; Position = value.m_Position; Rotation = value.m_Rotation;
                ParentIndex = value.m_ParentIndex; GroupIndex = value.m_GroupIndex; Probability = value.m_Probability; Archetype = archetype;
                Definition = value;
            }
        }

        internal sealed class Leaf
        {
            internal readonly Entity Prefab;
            internal readonly ObjectData ObjectData;
            internal readonly ObjectGeometryData Geometry;
            internal readonly PlaceableObjectData? Placeable;
            internal readonly StreetLightData? StreetLight;
            internal EntityArchetype Archetype => ObjectData.m_Archetype;
            internal readonly IReadOnlyDictionary<TypeIndex, ComponentType> Types;
            internal readonly IReadOnlyDictionary<TypeIndex, TypeFacts> TypeMetadata;
            internal Leaf(Entity prefab, ObjectData data, ObjectGeometryData geometry, PlaceableObjectData? placeable,
                StreetLightData? light, Dictionary<TypeIndex, ComponentType> types, Dictionary<TypeIndex, TypeFacts> metadata)
            {
                Prefab = prefab; ObjectData = data; Geometry = geometry; Placeable = placeable; StreetLight = light;
                Types = new ReadOnlyDictionary<TypeIndex, ComponentType>(types);
                TypeMetadata = new ReadOnlyDictionary<TypeIndex, TypeFacts>(metadata);
            }
        }

        internal readonly struct TypeFacts
        {
            internal readonly int Category, SizeInChunk, ElementSize, BufferCapacity, EntityReferences;
            internal readonly bool Buffer, Enableable;
            internal TypeFacts(in TypeManager.TypeInfo info, TypeIndex type)
            {
                Category = (int)info.Category; SizeInChunk = info.SizeInChunk; ElementSize = info.ElementSize;
                BufferCapacity = info.BufferCapacity; EntityReferences = info.EntityOffsetCount;
                Buffer = type.IsBuffer; Enableable = type.IsEnableable;
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(float2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(float3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : InvalidOperationException
        { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
