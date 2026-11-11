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
    // Admission for the bounded wind fixture. The native system owns selection
    // and generation. Every Run reads the current Source again.
    internal sealed class SubObjectGenerationPreflight
    {
        private const int TypeLimit = 256, TotalTypeLimit = 1024;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private readonly RelocationDefinitionSource? original;
        private readonly Temp expectedRootTemp, expectedOldNodeTemp, expectedNodeTemp;
        private IReadOnlyList<Candidate> candidates = Array.Empty<Candidate>();
        private IReadOnlyList<OriginalChild> originalChildren = Array.Empty<OriginalChild>();
        private Dictionary<Entity, Leaf> leaves = new Dictionary<Entity, Leaf>();
        private bool passed;
        private int runs, typesRead, queryAttempts, queriesAcquired, queriesDisposed;
        private bool serviceUpkeepRelevant;
        private int rootServiceUpkeepCount, nodeServiceUpkeepCount;
        private string? failureCode, exceptionType;
        private Temp rootTemp, oldNodeTemp, nodeTemp;
        private PseudoRandomSeed rootSeed, oldNodeSeed, nodeSeed;
        private Game.Buildings.Building rootBuilding;
        private int commandUpperBound, relocationWitnessChecks;

        internal SubObjectGenerationPreflight(World source, Entity root, Entity node, int actor)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            if (root == Entity.Null || node == Entity.Null || root.Index < 0 || node.Index < 0 || root == node || actor <= 0)
                throw new ArgumentException("SUBOBJECT_PREFLIGHT_IDENTITY_REJECTED");
            Root = root; Node = node; Actor = actor;
        }

        internal SubObjectGenerationPreflight(World source, Entity root, Entity oldNode, Entity newNode, int actor,
            Temp expectedRoot, Temp expectedOldNode, Temp expectedNewNode, RelocationDefinitionSource original)
            : this(source, root, newNode, actor)
        {
            this.original = original ?? throw new ArgumentNullException(nameof(original));
            Require(root.Index > 0 && oldNode.Index > 0 && newNode.Index > 0
                && oldNode != root && oldNode != newNode
                && !original.IsFixtureEntity(root) && !original.IsFixtureEntity(oldNode) && !original.IsFixtureEntity(newNode)
                && expectedRoot.m_Original == original.Root && expectedRoot.m_Flags == (TempFlags.Modify | TempFlags.Essential)
                && expectedOldNode.m_Original == original.NodeEntity && expectedOldNode.m_Flags == (TempFlags.Delete | TempFlags.Hidden)
                && expectedNewNode.m_Original == Entity.Null && expectedNewNode.m_Flags == (TempFlags.Create | TempFlags.IsLast),
                "SUBOBJECT_PREFLIGHT_RELOCATION_IDENTITY_REJECTED");
            OldNode = oldNode;
            expectedRootTemp = expectedRoot; expectedOldNodeTemp = expectedOldNode; expectedNodeTemp = expectedNewNode;
        }

        internal World Source { get; }
        internal Entity Root { get; }
        internal Entity OldNode { get; }
        internal Entity Node { get; }
        internal bool IsRelocation => original != null;
        internal int Actor { get; }
        internal bool Passed => passed;
        internal bool ResourcesReleased => archetypes.ResourcesReleased
            && queryAttempts == queriesAcquired && queriesAcquired == queriesDisposed;
        internal int CreateUpperBound => passed ? candidates.Count : 0;
        internal int CommandUpperBound => passed ? commandUpperBound : 0;
        internal IReadOnlyList<Candidate> Candidates => passed ? candidates : Array.Empty<Candidate>();
        internal IReadOnlyList<OriginalChild> OriginalChildren => passed ? originalChildren : Array.Empty<OriginalChild>();
        internal NativeTransform RootTransform { get; private set; }
        internal NativeNode NodeData { get; private set; }
        internal NativeNode OldNodeData { get; private set; }
        internal float RootElevation { get; private set; }
        internal float OldNodeElevation { get; private set; }
        internal float NodeElevation { get; private set; }
        internal Game.Buildings.Building RootBuilding
        {
            get
            {
                Require(passed && IsRelocation, "SUBOBJECT_PREFLIGHT_RELOCATION_BUILDING_UNAVAILABLE");
                return rootBuilding;
            }
        }
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
            Require(passed && (owner == Root || owner == Node || IsRelocation && owner == OldNode),
                "SUBOBJECT_PREFLIGHT_OWNER_UNAVAILABLE");
            return owner == Root ? rootTemp : owner == Node ? nodeTemp : oldNodeTemp;
        }

        internal float OwnerElevation(Entity owner)
        {
            Require(passed && (owner == Root || owner == Node || IsRelocation && owner == OldNode),
                "SUBOBJECT_PREFLIGHT_OWNER_UNAVAILABLE");
            return owner == Root ? RootElevation : owner == Node ? NodeElevation : OldNodeElevation;
        }

        internal void Run(Action requireCurrentScope)
        {
            bool compareRelocation = IsRelocation && runs != 0;
            Require(!compareRelocation || passed, "SUBOBJECT_PREFLIGHT_RELOCATION_WITNESS_UNAVAILABLE");
            runs++;
            ReadInputs(requireCurrentScope, compareRelocation);
        }

        internal void RequireRelocationWitness(Action requireCurrentScope)
        {
            Require(passed && IsRelocation, "SUBOBJECT_PREFLIGHT_RELOCATION_WITNESS_UNAVAILABLE");
            ReadInputs(requireCurrentScope, compareRelocation: true);
            relocationWitnessChecks++;
        }

        internal void RequireRelocationOriginalWitness(Action requireCurrentScope)
        {
            Require(passed && original != null, "SUBOBJECT_PREFLIGHT_RELOCATION_WITNESS_UNAVAILABLE");
            Guard(requireCurrentScope);
            original!.RequireUnchanged(Source, original.Prefab, requireCurrentScope);
            RequireSameOriginalRows(ReadOriginalChildren(Source.EntityManager, candidates, requireCurrentScope));
            relocationWitnessChecks++;
        }

        private void ReadInputs(Action requireCurrentScope, bool compareRelocation)
        {
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            passed = false; failureCode = exceptionType = null; typesRead = 0;
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

                Temp freshRootTemp = Owner(entities, Root, 9, IsRelocation ? expectedRootTemp : (Temp?)null, requireCurrentScope);
                Temp freshNodeTemp = Owner(entities, Node, 5, IsRelocation ? expectedNodeTemp : (Temp?)null, requireCurrentScope);
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
                NativeNode oldNodeData = default;
                Temp freshOldNodeTemp = default;
                float oldNodeHeight = 0f;
                PseudoRandomSeed freshRootSeed = default, freshOldNodeSeed = default, freshNodeSeed = default;
                Game.Buildings.Building freshRootBuilding = default;
                if (original != null)
                {
                    freshOldNodeTemp = Owner(entities, OldNode, 0, expectedOldNodeTemp, requireCurrentScope);
                    Require(rootPrefab == original.Prefab && nodePrefab == original.NodePrefab
                        && Prefab(entities, prefabs!, OldNode) == nodePrefab, "SUBOBJECT_PREFLIGHT_RELOCATION_PREFAB_CHANGED");
                    Require(entities.HasComponent<Game.Objects.Object>(Root) && !entities.HasComponent<NativeNode>(Root)
                        && !entities.HasComponent<Game.Objects.Object>(Node) && !entities.HasComponent<Game.Objects.Object>(OldNode)
                        && entities.HasComponent<NativeNode>(OldNode) && !entities.HasComponent<NativeTransform>(OldNode)
                        && !entities.HasComponent<Game.Net.Edge>(OldNode)
                        && !entities.HasComponent<Owner>(OldNode) && !entities.HasComponent<OwnerDefinition>(OldNode)
                        && !entities.HasComponent<Game.City.CityServiceUpkeep>(OldNode)
                        && !entities.HasComponent<Game.Vehicles.Watercraft>(OldNode),
                        "SUBOBJECT_PREFLIGHT_RELOCATION_OWNER_ROLE_CHANGED");
                    Require(entities.HasBuffer<Game.Net.ConnectedEdge>(OldNode)
                        && entities.HasBuffer<Game.Net.ConnectedEdge>(original.NodeEntity),
                        "SUBOBJECT_PREFLIGHT_CONNECTED_EDGES_MISSING");
                    Empty<Game.Net.ConnectedEdge>(entities, OldNode);
                    Empty<Game.Net.ConnectedEdge>(entities, original.NodeEntity);
                    oldNodeData = entities.GetComponentData<NativeNode>(OldNode);
                    Require(oldNodeData.Equals(original.NodePose), "SUBOBJECT_PREFLIGHT_RELOCATION_OLD_NODE_CHANGED");
                    oldNodeHeight = ReadElevation(entities, OldNode);
                    freshRootSeed = ReadSeed(entities, Root); freshOldNodeSeed = ReadSeed(entities, OldNode);
                    freshNodeSeed = ReadSeed(entities, Node);
                    Require(entities.HasComponent<Game.Buildings.Building>(Root), "SUBOBJECT_PREFLIGHT_RELOCATION_BUILDING_MISSING");
                    freshRootBuilding = entities.GetComponentData<Game.Buildings.Building>(Root);
                    Require(freshRootBuilding.m_RoadEdge == Entity.Null, "SUBOBJECT_PREFLIGHT_RELOCATION_ROAD_CHANGED");
                }

                bool freshServiceUpkeepRelevant = entities.HasComponent<Game.City.CityServiceUpkeep>(Root);
                Guard(requireCurrentScope);
                int freshRootUpkeepCount = 0, freshNodeUpkeepCount = 0;
                if (freshServiceUpkeepRelevant)
                {
                    freshRootUpkeepCount = BoundedLength<ServiceUpkeepData>(entities, rootPrefab, requireCurrentScope);
                    freshNodeUpkeepCount = BoundedLength<ServiceUpkeepData>(entities, nodePrefab, requireCurrentScope);
                }
                Require(!entities.HasComponent<MovingObjectData>(rootPrefab)
                    && (!entities.HasComponent<PlaceableObjectData>(rootPrefab)
                        || (entities.GetComponentData<PlaceableObjectData>(rootPrefab).m_Flags & Game.Objects.PlacementFlags.Swaying) == 0),
                    "SUBOBJECT_PREFLIGHT_RELATIVE_BRANCH_UNSUPPORTED");
                Guard(requireCurrentScope);
                var freshLeaves = new Dictionary<Entity, Leaf>();
                int ownerCount = IsRelocation ? 3 : 2;
                var freshCandidates = new List<Candidate>(ownerCount + 3);
                ReadRows(entities, prefabs!, Root, rootPrefab, 4, freshCandidates, freshLeaves, requireCurrentScope);
                if (IsRelocation)
                    ReadRows(entities, prefabs!, OldNode, nodePrefab, 1, freshCandidates, freshLeaves, requireCurrentScope);
                ReadRows(entities, prefabs!, Node, nodePrefab, 1, freshCandidates, freshLeaves, requireCurrentScope);
                Require(freshCandidates.Count == ownerCount + 3 && freshLeaves.Count == 4, "SUBOBJECT_PREFLIGHT_COHORT_CHANGED");
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
                var freshOriginals = ReadOriginalChildren(entities, freshCandidates, requireCurrentScope);
                original?.RequireUnchanged(Source, original.Prefab, requireCurrentScope);
                Guard(requireCurrentScope);
                Require(ResourcesReleased, "SUBOBJECT_PREFLIGHT_STORAGE_UNCONFIRMED");

                if (compareRelocation)
                {
                    Require(RootTransform.Equals(transform) && NodeData.Equals(nodeData) && OldNodeData.Equals(oldNodeData)
                        && RootElevation.Equals(rootHeight) && NodeElevation.Equals(nodeHeight) && OldNodeElevation.Equals(oldNodeHeight)
                        && rootTemp.Equals(freshRootTemp) && oldNodeTemp.Equals(freshOldNodeTemp) && nodeTemp.Equals(freshNodeTemp)
                        && rootSeed.Equals(freshRootSeed) && oldNodeSeed.Equals(freshOldNodeSeed) && nodeSeed.Equals(freshNodeSeed)
                        && rootBuilding.Equals(freshRootBuilding) && DefaultTheme == theme
                        && BuildingConfiguration.Equals(building) && HappinessParameters.Equals(happiness)
                        && serviceUpkeepRelevant == freshServiceUpkeepRelevant
                        && rootServiceUpkeepCount == freshRootUpkeepCount && nodeServiceUpkeepCount == freshNodeUpkeepCount,
                        "SUBOBJECT_PREFLIGHT_RELOCATION_INPUT_CHANGED");
                    RequireSameRelocationRows(freshCandidates, freshLeaves, freshOriginals);
                }
                else
                {
                    candidates = freshCandidates.AsReadOnly(); leaves = freshLeaves;
                    if (IsRelocation) originalChildren = Array.AsReadOnly(freshOriginals);
                    // One Create and eight fixed scalar writes per row, plus
                    // one Aligned per node row and the one root StreetLight.
                    commandUpperBound = freshCandidates.Count * 9 + (ownerCount - 1) + lights;
                    RootTransform = transform; NodeData = nodeData; OldNodeData = oldNodeData;
                    RootElevation = rootHeight; NodeElevation = nodeHeight; OldNodeElevation = oldNodeHeight;
                    rootTemp = freshRootTemp; oldNodeTemp = freshOldNodeTemp; nodeTemp = freshNodeTemp;
                    rootSeed = freshRootSeed; oldNodeSeed = freshOldNodeSeed; nodeSeed = freshNodeSeed;
                    rootBuilding = freshRootBuilding;
                    DefaultTheme = theme; BuildingConfiguration = building; HappinessParameters = happiness;
                    serviceUpkeepRelevant = freshServiceUpkeepRelevant;
                    rootServiceUpkeepCount = freshRootUpkeepCount; nodeServiceUpkeepCount = freshNodeUpkeepCount;
                }
                passed = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "SUBOBJECT_PREFLIGHT_READ_FAILED";
                exceptionType = error.GetType().Name;
                throw;
            }
        }

        private Temp Owner(EntityManager entities, Entity entity, uint flags, Temp? captured, Action scope)
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
            bool matches = captured.HasValue ? temp.Equals(captured.Value) : temp.m_Original == Entity.Null && (uint)temp.m_Flags == flags;
            Require(matches && Finite(temp.m_CurvePosition), "SUBOBJECT_PREFLIGHT_TEMP_CHANGED");
            Empty<Game.Objects.SubObject>(entities, entity);
            Empty<Game.Buildings.InstalledUpgrade>(entities, entity);
            Empty<Game.Buildings.Renter>(entities, entity);
            Empty<LinkedEntityGroup>(entities, entity);
            Guard(scope); return temp;
        }

        private OriginalChild[] ReadOriginalChildren(EntityManager entities, IReadOnlyList<Candidate> freshCandidates, Action scope)
        {
            if (original == null) return Array.Empty<OriginalChild>();
            var result = new OriginalChild[6];
            ReadOriginalParent(entities, original.Root, Root, 0, 4, freshCandidates, result, scope);
            ReadOriginalParent(entities, original.NodeEntity, OldNode, 4, 2, freshCandidates, result, scope);
            return result;
        }

        private void ReadOriginalParent(EntityManager entities, Entity parent, Entity temporaryParent, int offset, int count,
            IReadOnlyList<Candidate> freshCandidates, OriginalChild[] result, Action scope)
        {
            var source = original!;
            for (int index = 0; index < count; index++)
            {
                Guard(scope);
                Entity child = source.ChildAt(parent, index);
                Require(child.Index > 0 && entities.Exists(child) && entities.HasComponent<Owner>(child)
                    && entities.GetComponentData<Owner>(child).m_Owner == parent && !entities.HasComponent<Game.Objects.Attached>(child)
                    && !entities.HasComponent<Temp>(child) && entities.HasComponent<PrefabRef>(child),
                    "SUBOBJECT_PREFLIGHT_RELOCATION_ORIGINAL_ROLE_CHANGED");
                Entity prefab = entities.GetComponentData<PrefabRef>(child).m_Prefab;
                bool eligible = !entities.HasComponent<Game.Buildings.ServiceUpgrade>(child)
                    && !entities.HasComponent<Game.Buildings.Building>(child) && !entities.HasComponent<Game.Objects.Secondary>(child);
                NativeTransform? transform = ReadOptional<NativeTransform>(entities, child);
                Game.Objects.Aligned? aligned = ReadOptional<Game.Objects.Aligned>(entities, child);
                foreach (Candidate candidate in freshCandidates)
                {
                    if (!eligible || candidate.Owner != temporaryParent || candidate.Prefab != prefab) continue;
                    // Original matching reads Transform for the building's
                    // unaligned rows. Node rows match on Aligned instead.
                    Require(temporaryParent != Root || transform.HasValue, "SUBOBJECT_PREFLIGHT_RELOCATION_ORIGINAL_TRANSFORM_MISSING");
                    Require(!entities.HasComponent<Game.Objects.UnderConstruction>(child)
                        && !entities.HasComponent<Destroyed>(child) && !entities.HasComponent<Overridden>(child),
                        "SUBOBJECT_PREFLIGHT_RELOCATION_ORIGINAL_BRANCH_UNSUPPORTED");
                    break;
                }
                Empty<Game.Objects.SubObject>(entities, child);
                result[offset + index] = new OriginalChild(child, parent, prefab, eligible, transform, aligned,
                    ReadOptional<PseudoRandomSeed>(entities, child), ReadOptional<Game.Objects.StreetLight>(entities, child),
                    entities.HasComponent<BatchesUpdated>(child));
                Guard(scope);
            }
        }

        private void RequireSameRelocationRows(IReadOnlyList<Candidate> freshCandidates, Dictionary<Entity, Leaf> freshLeaves,
            OriginalChild[] freshOriginals)
        {
            Require(candidates.Count == freshCandidates.Count && leaves.Count == freshLeaves.Count, "SUBOBJECT_PREFLIGHT_RELOCATION_ROWS_CHANGED");
            for (int index = 0; index < candidates.Count; index++)
            {
                Candidate before = candidates[index], after = freshCandidates[index];
                Require(before.Owner == after.Owner && before.RowIndex == after.RowIndex
                    && before.Definition.Equals(after.Definition) && before.Archetype.Equals(after.Archetype),
                    "SUBOBJECT_PREFLIGHT_RELOCATION_CANDIDATE_CHANGED");
            }
            foreach (var pair in leaves)
            {
                Leaf before = pair.Value;
                Require(freshLeaves.TryGetValue(pair.Key, out Leaf? after) && after != null
                    && before.ObjectData.Equals(after.ObjectData) && before.Geometry.Equals(after.Geometry)
                    && Nullable.Equals(before.Placeable, after.Placeable) && Nullable.Equals(before.StreetLight, after.StreetLight)
                    && before.PrefabEnabled == after.PrefabEnabled && before.HasDeclaredSubObjects == after.HasDeclaredSubObjects,
                    "SUBOBJECT_PREFLIGHT_RELOCATION_LEAF_CHANGED");
            }
            RequireSameOriginalRows(freshOriginals);
        }

        private void RequireSameOriginalRows(OriginalChild[] freshOriginals)
        {
            Require(originalChildren.Count == freshOriginals.Length, "SUBOBJECT_PREFLIGHT_RELOCATION_ORIGINAL_COUNT_CHANGED");
            for (int index = 0; index < originalChildren.Count; index++)
                Require(originalChildren[index].Matches(freshOriginals[index]), "SUBOBJECT_PREFLIGHT_RELOCATION_ORIGINAL_CHANGED");
        }

        private static PseudoRandomSeed ReadSeed(EntityManager entities, Entity owner)
        {
            Require(entities.HasComponent<PseudoRandomSeed>(owner), "SUBOBJECT_PREFLIGHT_RELOCATION_OWNER_SEED_MISSING");
            return entities.GetComponentData<PseudoRandomSeed>(owner);
        }

        private static T? ReadOptional<T>(EntityManager entities, Entity value) where T : unmanaged, IComponentData =>
            entities.HasComponent<T>(value) ? entities.GetComponentData<T>(value) : (T?)null;

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
            bool enabled = IsRelocation && entities.IsComponentEnabled<PrefabData>(prefab);
            bool hasDeclaredSubObjects = IsRelocation && entities.HasBuffer<PrefabSubObject>(prefab);
            Guard(scope);
            return new Leaf(prefab, data, geometry, placeable, light, types, metadata, enabled, hasDeclaredSubObjects);
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
            ["relocation"] = IsRelocation, ["originalRows"] = passed ? originalChildren.Count : 0,
            ["relocationWitnessChecks"] = relocationWitnessChecks, ["serviceUpkeepRelevant"] = serviceUpkeepRelevant,
            ["rootServiceUpkeepCount"] = rootServiceUpkeepCount, ["nodeServiceUpkeepCount"] = nodeServiceUpkeepCount,
            ["passed"] = passed, ["runs"] = runs, ["candidateRows"] = passed ? candidates.Count : 0,
            ["uniqueLeaves"] = passed ? leaves.Count : 0, ["createUpperBound"] = CreateUpperBound,
            ["commandUpperBound"] = CommandUpperBound, ["typesRead"] = typesRead,
            ["typeArrays"] = archetypes.Snapshot(), ["queryAttempts"] = queryAttempts,
            ["queriesAcquired"] = queriesAcquired, ["queriesDisposed"] = queriesDisposed,
            ["resourcesReleased"] = ResourcesReleased, ["nativeInvoked"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };

        internal readonly struct OriginalChild
        {
            internal readonly Entity Entity, Parent, Prefab;
            internal readonly bool Eligible;
            internal readonly NativeTransform? Transform;
            internal readonly Game.Objects.Aligned? Aligned;
            internal readonly PseudoRandomSeed? Seed;
            internal readonly Game.Objects.StreetLight? StreetLight;
            private readonly bool batchesUpdated;

            internal OriginalChild(Entity entity, Entity parent, Entity prefab, bool eligible, NativeTransform? transform,
                Game.Objects.Aligned? aligned, PseudoRandomSeed? seed, Game.Objects.StreetLight? streetLight, bool batchesUpdated)
            {
                Entity = entity; Parent = parent; Prefab = prefab; Eligible = eligible;
                Transform = transform; Aligned = aligned; Seed = seed; StreetLight = streetLight;
                this.batchesUpdated = batchesUpdated;
            }

            internal bool Matches(OriginalChild other) => Entity == other.Entity && Parent == other.Parent && Prefab == other.Prefab
                && Eligible == other.Eligible && Nullable.Equals(Transform, other.Transform) && Nullable.Equals(Aligned, other.Aligned)
                && Nullable.Equals(Seed, other.Seed) && Nullable.Equals(StreetLight, other.StreetLight)
                && batchesUpdated == other.batchesUpdated;
        }

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
            internal readonly bool PrefabEnabled, HasDeclaredSubObjects;
            internal EntityArchetype Archetype => ObjectData.m_Archetype;
            internal readonly IReadOnlyDictionary<TypeIndex, ComponentType> Types;
            internal readonly IReadOnlyDictionary<TypeIndex, TypeFacts> TypeMetadata;
            internal Leaf(Entity prefab, ObjectData data, ObjectGeometryData geometry, PlaceableObjectData? placeable,
                StreetLightData? light, Dictionary<TypeIndex, ComponentType> types, Dictionary<TypeIndex, TypeFacts> metadata,
                bool prefabEnabled, bool hasDeclaredSubObjects)
            {
                Prefab = prefab; ObjectData = data; Geometry = geometry; Placeable = placeable; StreetLight = light;
                PrefabEnabled = prefabEnabled; HasDeclaredSubObjects = hasDeclaredSubObjects;
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
