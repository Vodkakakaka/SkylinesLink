#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using Disabled = Unity.Entities.Disabled;
using EditorContainer = Game.Tools.EditorContainer;
using SubObject = Game.Objects.SubObject;
using SubNet = Game.Net.SubNet;

namespace SkylinesLink.Mod.Native
{
    // Read authority for one already published fixture. It never adopts a
    // city-wide selection or supplies a payload to the new-build consumers.
    internal sealed class RelocationDefinitionSource
    {
        private const int RootChildren = 4, NodeChildren = 2;
        private readonly World world;
        private readonly PreviewCommitExecution committed;
        private readonly CitySystem citySystem;
        private readonly Entity city;
        private readonly int funds;
        private readonly Entity[] children = new Entity[RootChildren + NodeChildren];
        private readonly Entity[] childPrefabs = new Entity[RootChildren + NodeChildren];
        private readonly Transform?[] childTransforms = new Transform?[RootChildren + NodeChildren];
        private readonly float2 footprintMin, footprintMax;
        private readonly NetGeometryData nodePrefabGeometry;
        private readonly NodeGeometry nodeInstanceGeometry;

        internal Entity Root { get; }
        internal Entity Prefab { get; }
        internal Entity NodeEntity { get; }
        internal Entity NodePrefab { get; }
        internal Transform RootTransform { get; }
        internal Node NodePose { get; }
        internal bool HasNodeElevation { get; }
        internal CompositionFlags Upgrades { get; }
        internal int CommandCount => Upgrades.Equals(default(CompositionFlags)) ? 13 : 14;

        internal RelocationDefinitionSource(World world, PreviewCommitExecution committed, Entity prefab, Action requireScope)
        {
            requireScope();
            this.world = world;
            this.committed = committed;
            var published = committed.RequirePublishedBuildingAndNode();
            Root = published.Root;
            Prefab = prefab;
            var entities = world.EntityManager;
            RequireRoot(entities);
            RootTransform = entities.GetComponentData<Transform>(Root);
            NodeEntity = ReadNode(entities);
            Require(NodeEntity == published.Node, "RELOCATION_SOURCE_PUBLISHED_NODE_CHANGED");
            NodePrefab = entities.GetComponentData<PrefabRef>(NodeEntity).m_Prefab;
            NodePose = entities.GetComponentData<Node>(NodeEntity);
            HasNodeElevation = entities.HasComponent<Game.Net.Elevation>(NodeEntity);
            Upgrades = ReadUpgrades(entities);
            nodePrefabGeometry = ReadNodePrefabGeometry(entities);
            nodeInstanceGeometry = ReadNodeInstanceGeometry(entities);
            Require((nodePrefabGeometry.m_Flags & Game.Net.GeometryFlags.OnWater) == 0
                && math.all(math.isfinite(NodePose.m_Position)) && math.all(math.isfinite(NodePose.m_Rotation.value)),
                "RELOCATION_SOURCE_NODE_GEOMETRY_UNQUALIFIED");

            ReadChildren(entities, Root, 0, RootChildren, capture: true);
            ReadChildren(entities, NodeEntity, RootChildren, NodeChildren, capture: true);
            ReadFootprint(entities, out footprintMin, out footprintMax);

            citySystem = world.GetExistingSystemManaged<CitySystem>() ?? throw new Rejection("RELOCATION_SOURCE_CITY_UNAVAILABLE");
            city = citySystem.City;
            funds = ReadFunds();
            committed.RequirePublishedElectricity(world, requireScope);
            requireScope();
        }

        internal bool IsFixtureEntity(Entity entity) =>
            entity == Root || entity == NodeEntity || entity == city || entity == Prefab || entity == NodePrefab
            || Array.IndexOf(children, entity) >= 0;

        // Returns only an identity captured from the verified old graph.
        internal Entity ChildAt(Entity parent, int index)
        {
            if (parent == Root && (uint)index < RootChildren) return children[index];
            if (parent == NodeEntity && (uint)index < NodeChildren) return children[RootChildren + index];
            throw new ArgumentOutOfRangeException(nameof(index), "RELOCATION_SOURCE_CHILD_SLOT_INVALID");
        }

        internal bool AcceptsSite(float2 candidate)
        {
            float2 offset = candidate - RootTransform.m_Position.xz;
            // Use the copied source footprint translated at the same rotation.
            // This separates the two fixture sites before native collision tests.
            return math.any(footprintMax + 1f < footprintMin + offset)
                || math.any(footprintMax + offset + 1f < footprintMin);
        }

        internal bool IsOriginalObject(Entity entity) => entity == Root || Array.IndexOf(children, entity) >= 0;

        internal void RequireSeparateDestination(float2 min, float2 max)
        {
            var entities = world.EntityManager;
            Require(!entities.HasComponent<Stack>(Root), "PREVIEW_VALIDATION_ORIGINAL_STACK_UNSUPPORTED");
            for (int i = 0; i < children.Length; i++)
                Require(childTransforms[i].HasValue && !entities.HasComponent<Stack>(children[i]),
                    "PREVIEW_VALIDATION_ORIGINAL_CHILD_BOUNDS_UNSUPPORTED");

            float2 oldMin = math.min(footprintMin, nodeInstanceGeometry.m_Bounds.min.xz);
            float2 oldMax = math.max(footprintMax, nodeInstanceGeometry.m_Bounds.max.xz);
            Require(math.all(math.isfinite(min)) && math.all(math.isfinite(max)) && math.all(min <= max)
                && math.all(math.isfinite(oldMin)) && math.all(math.isfinite(oldMax)) && math.all(oldMin <= oldMax)
                && (math.any(oldMax + 1f < min) || math.any(max + 1f < oldMin)),
                "PREVIEW_VALIDATION_RELOCATION_SITES_OVERLAP");
        }

        internal float3 ExpectedNodeAt(ControlPoint point)
        {
            var local = ObjectUtils.WorldToLocal(ObjectUtils.InverseTransform(RootTransform), NodePose.m_Position);
            return ObjectUtils.LocalToWorld(new Transform(point.m_Position, point.m_Rotation), local);
        }

        internal void RequireUnchanged(World source, Entity prefab, Action requireScope, int charged = 0)
        {
            requireScope();
            Require(ReferenceEquals(source, world) && source.IsCreated && prefab == Prefab, "RELOCATION_SOURCE_WORLD_CHANGED");
            var entities = source.EntityManager;
            RequireRoot(entities);
            Require(entities.GetComponentData<Transform>(Root).Equals(RootTransform) && ReadNode(entities) == NodeEntity
                && entities.GetComponentData<PrefabRef>(NodeEntity).m_Prefab == NodePrefab
                && entities.GetComponentData<Node>(NodeEntity).Equals(NodePose)
                && entities.HasComponent<Game.Net.Elevation>(NodeEntity) == HasNodeElevation
                && ReadUpgrades(entities).Equals(Upgrades) && ReadNodePrefabGeometry(entities).Equals(nodePrefabGeometry)
                && NodeGeometryProducer.Same(ReadNodeInstanceGeometry(entities), nodeInstanceGeometry),
                "RELOCATION_SOURCE_POSE_OR_NODE_CHANGED");
            ReadChildren(entities, Root, 0, RootChildren, capture: false);
            ReadChildren(entities, NodeEntity, RootChildren, NodeChildren, capture: false);
            ReadFootprint(entities, out var min, out var max);
            Require(min.Equals(footprintMin) && max.Equals(footprintMax), "RELOCATION_SOURCE_FOOTPRINT_CHANGED");
            RequireBalanceAfterCharge(charged);
            committed.RequirePublishedElectricity(source, requireScope);
            requireScope();
        }

        private void RequireRoot(EntityManager entities)
        {
            Require(IsStable(entities, Root) && entities.HasComponent<Building>(Root) && entities.HasComponent<Transform>(Root)
                && entities.HasComponent<PrefabRef>(Root) && entities.GetComponentData<PrefabRef>(Root).m_Prefab == Prefab
                && entities.Exists(Prefab) && entities.HasComponent<ObjectGeometryData>(Prefab)
                && !entities.HasComponent<Owner>(Root) && !entities.HasComponent<Attached>(Root)
                && !entities.HasComponent<EditorContainer>(Root)
                && Empty<InstalledUpgrade>(entities, Root) && Empty<Game.Areas.SubArea>(entities, Root)
                && Empty<Game.Prefabs.SubArea>(entities, Prefab)
                && (!entities.HasBuffer<Game.Prefabs.SubArea>(Prefab) || entities.HasBuffer<SubAreaNode>(Prefab)),
                "RELOCATION_SOURCE_BUILDING_UNQUALIFIED");
        }

        private Entity ReadNode(EntityManager entities)
        {
            Require(entities.HasBuffer<SubNet>(Root), "RELOCATION_SOURCE_SUBNET_UNAVAILABLE");
            var subNets = entities.GetBuffer<SubNet>(Root, true);
            Require(subNets.Length == 1, "RELOCATION_SOURCE_SUBNET_BOUND");
            Entity node = subNets[0].m_SubNet;
            Require(IsStable(entities, node) && entities.HasComponent<Node>(node) && entities.HasComponent<PrefabRef>(node)
                && !entities.HasComponent<Edge>(node) && !entities.HasComponent<EditorContainer>(node)
                && entities.HasComponent<Owner>(node) && entities.GetComponentData<Owner>(node).m_Owner == Root
                && entities.HasBuffer<ConnectedEdge>(node) && Empty<ConnectedEdge>(entities, node), "RELOCATION_SOURCE_NODE_UNQUALIFIED");
            return node;
        }

        private void ReadChildren(EntityManager entities, Entity parent, int offset, int count, bool capture)
        {
            Require(entities.HasBuffer<SubObject>(parent), "RELOCATION_SOURCE_SUBOBJECT_UNAVAILABLE");
            var entries = entities.GetBuffer<SubObject>(parent, true);
            Require(entries.Length == count, "RELOCATION_SOURCE_SUBOBJECT_BOUND");
            for (int index = 0; index < count; index++)
            {
                int slot = offset + index;
                Entity child = entries[index].m_SubObject;
                Require(IsStable(entities, child) && child != Root && child != NodeEntity
                    && entities.HasComponent<Owner>(child) && entities.GetComponentData<Owner>(child).m_Owner == parent
                    && !entities.HasComponent<Attached>(child) && entities.HasComponent<PrefabRef>(child)
                    && Empty<SubObject>(entities, child), "RELOCATION_SOURCE_SUBOBJECT_UNQUALIFIED");
                Entity prefab = entities.GetComponentData<PrefabRef>(child).m_Prefab;
                Require(entities.Exists(prefab), "RELOCATION_SOURCE_SUBOBJECT_PREFAB_UNAVAILABLE");
                Transform? transform = entities.HasComponent<Transform>(child) ? entities.GetComponentData<Transform>(child) : (Transform?)null;
                if (capture)
                {
                    Require(Array.IndexOf(children, child, 0, slot) < 0, "RELOCATION_SOURCE_DUPLICATE_CHILD");
                    children[slot] = child;
                    childPrefabs[slot] = prefab;
                    childTransforms[slot] = transform;
                }
                else
                {
                    Require(child == children[slot] && prefab == childPrefabs[slot] && Nullable.Equals(transform, childTransforms[slot]),
                        "RELOCATION_SOURCE_SUBOBJECT_CHANGED");
                }
            }
        }

        private static void ExtendBounds(EntityManager entities, Entity prefab, Transform pose, ref float2 min, ref float2 max)
        {
            Require(math.all(math.isfinite(pose.m_Position)) && math.all(math.isfinite(pose.m_Rotation.value)),
                "RELOCATION_SOURCE_INVALID_TRANSFORM");
            min = math.min(min, pose.m_Position.xz);
            max = math.max(max, pose.m_Position.xz);
            if (!entities.HasComponent<ObjectGeometryData>(prefab)) return;
            var geometry = entities.GetComponentData<ObjectGeometryData>(prefab);
            var bounds = ObjectUtils.CalculateBounds(pose.m_Position, pose.m_Rotation, geometry);
            Require(math.all(math.isfinite(bounds.min)) && math.all(math.isfinite(bounds.max)) && math.all(bounds.min <= bounds.max),
                "RELOCATION_SOURCE_INVALID_BOUNDS");
            min = math.min(min, bounds.min.xz);
            max = math.max(max, bounds.max.xz);
        }

        private void ReadFootprint(EntityManager entities, out float2 min, out float2 max)
        {
            min = math.min(RootTransform.m_Position.xz, NodePose.m_Position.xz);
            max = math.max(RootTransform.m_Position.xz, NodePose.m_Position.xz);
            ExtendBounds(entities, Prefab, RootTransform, ref min, ref max);
            for (int i = 0; i < children.Length; i++)
                if (childTransforms[i] is Transform pose) ExtendBounds(entities, childPrefabs[i], pose, ref min, ref max);
        }

        private NodeGeometry ReadNodeInstanceGeometry(EntityManager entities)
        {
            Require(entities.HasComponent<NodeGeometry>(NodeEntity), "RELOCATION_SOURCE_NODE_INSTANCE_GEOMETRY_MISSING");
            return entities.GetComponentData<NodeGeometry>(NodeEntity);
        }

        private NetGeometryData ReadNodePrefabGeometry(EntityManager entities)
        {
            Require(entities.Exists(NodePrefab) && entities.HasComponent<NetGeometryData>(NodePrefab),
                "RELOCATION_SOURCE_NODE_GEOMETRY_UNAVAILABLE");
            return entities.GetComponentData<NetGeometryData>(NodePrefab);
        }

        private CompositionFlags ReadUpgrades(EntityManager entities) => entities.HasComponent<Upgraded>(NodeEntity)
            ? entities.GetComponentData<Upgraded>(NodeEntity).m_Flags : default;

        internal void RequireBalanceAfterCharge(int charged)
        {
            Require(charged >= 0 && charged <= funds && ReadFunds() == funds - charged,
                "RELOCATION_DEFINITION_CHANGED_FUNDS");
        }

        private int ReadFunds()
        {
            var entities = world.EntityManager;
            Require(ReferenceEquals(world.GetExistingSystemManaged<CitySystem>(), citySystem) && citySystem.City == city
                && entities.Exists(city) && entities.HasComponent<PlayerMoney>(city), "RELOCATION_SOURCE_CITY_CHANGED");
            PlayerMoney money = entities.GetComponentData<PlayerMoney>(city);
            Require(!money.m_Unlimited, "RELOCATION_SOURCE_UNLIMITED_MONEY");
            return money.money;
        }

        private static bool IsStable(EntityManager entities, Entity value) => entities.Exists(value)
            && !entities.HasComponent<Deleted>(value) && !entities.HasComponent<Temp>(value) && !entities.HasComponent<Hidden>(value)
            && !entities.HasComponent<Disabled>(value) && !entities.HasComponent<ObjectGenerationScope>(value)
            && !entities.HasComponent<Created>(value) && !entities.HasComponent<Updated>(value) && !entities.HasComponent<Applied>(value);

        private static bool Empty<T>(EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData =>
            !entities.HasBuffer<T>(entity) || entities.GetBuffer<T>(entity, true).Length == 0;

        internal static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
