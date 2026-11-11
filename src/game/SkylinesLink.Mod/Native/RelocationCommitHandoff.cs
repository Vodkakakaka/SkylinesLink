#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Disabled = Unity.Entities.Disabled;
using SubNet = Game.Net.SubNet;

namespace SkylinesLink.Mod.Native
{
    internal sealed class RelocationCommitHandoff
    {
        private const int FollowupFrames = 180;
        private readonly World source;
        private readonly RelocationDefinitionSource original;
        private readonly int actor;
        private readonly Entity[] retired;
        private readonly Entity node, leaf, leafPrefab;
        private readonly Node nodePose;
        private readonly Transform placement;
        private readonly bool oldExtraChildSecondary;
        private readonly ElectricityBuildingGraphSystem electricity;
        private readonly ElectricityFlowSystem flow;
        private readonly uint graphVersion;
        private int? publishedAt, previousReadyFrame;
        private JObject? previousReadySample, settled;
        private JObject? simultaneousRebuildElectricity;
        private bool destroyEntered, destroyReturned, absenceConfirmed, publishEntered, publishReturned;
        internal bool WorldOwnershipConfirmed { get; private set; }
        internal bool DefaultReady { get; private set; }
        internal Entity NodeEntity => node;
        internal Entity Leaf => leaf;


        internal RelocationCommitHandoff(World source, RelocationDefinitionSource original, int actor, Entity[] graph, Temp[] temps, Entity leaf)
        {
            this.source = source;
            this.original = original;
            this.actor = actor;
            node = graph[2];
            this.leaf = leaf;
            var entities = source.EntityManager;
            Require(Array.IndexOf(graph, leaf) >= 3 && OwnerOf(entities, leaf) == node, "RELOCATION_PREVIEW_COMMIT_SURVIVORS_CHANGED");
            var remaining = new List<Entity>(7);
            foreach (Entity member in graph) if (member != node && member != leaf) remaining.Add(member);
            retired = remaining.ToArray();
            Require(retired.Length == 7, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_BOUND");
            placement = entities.GetComponentData<Transform>(graph[0]);
            nodePose = entities.GetComponentData<Node>(node);
            leafPrefab = entities.GetComponentData<PrefabRef>(leaf).m_Prefab;
            electricity = source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>()
                ?? throw new InvalidOperationException("RELOCATION_PREVIEW_COMMIT_ELECTRICITY_MISSING");
            flow = source.GetExistingSystemManaged<ElectricityFlowSystem>()
                ?? throw new InvalidOperationException("RELOCATION_PREVIEW_COMMIT_ELECTRICITY_FLOW_MISSING");
            graphVersion = electricity.CheckedStateRef.LastSystemVersion;
            Entity extra = Entity.Null;
            for (int index = 0; index < 2; index++)
            {
                Entity child = original.ChildAt(original.NodeEntity, index);
                if (Array.Exists(temps, temp => temp.m_Original == child)) continue;
                Require(extra == Entity.Null && !entities.HasComponent<Building>(child)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(child), "RELOCATION_PREVIEW_COMMIT_EXTRA_CHILD_UNQUALIFIED");
                extra = child;
            }
            Require(extra != Entity.Null, "RELOCATION_PREVIEW_COMMIT_EXTRA_CHILD_UNAVAILABLE");
            oldExtraChildSecondary = entities.HasComponent<Game.Objects.Secondary>(extra);
            RequireRetiredClosure(entities, applied: false);
        }

        internal void Publish(Action requireScope)
        {
            requireScope();
            Require(!destroyEntered && !publishEntered, "RELOCATION_PREVIEW_COMMIT_HANDOFF_REPEATED");
            EntityManager entities = source.EntityManager;
            entities.CompleteAllTrackedJobs();
            requireScope();
            RequirePublishedPair(entities, published: false, requireMarkers: true);
            RequirePendingFormalRemoval(entities);
            RequireRetiredClosure(entities, applied: true);

            using var retiredGroup = new NativeArray<Entity>(retired, Allocator.Temp);
            requireScope();
            destroyEntered = true;
            entities.DestroyEntity(retiredGroup);
            destroyReturned = true;
            requireScope();
            foreach (Entity member in retired)
                Require(!entities.Exists(member), "RELOCATION_PREVIEW_COMMIT_TEMP_RETAINED");
            absenceConfirmed = true;

            RequirePublishedPair(entities, published: false, requireMarkers: true);
            RequirePendingFormalRemoval(entities);
            using var publishedGroup = new NativeArray<Entity>(new[] { node, leaf }, Allocator.Temp);
            var isolation = new ComponentTypeSet(ComponentType.ReadWrite<Disabled>(), ComponentType.ReadWrite<ObjectGenerationScope>());
            requireScope();
            publishEntered = true;
            entities.RemoveComponent(publishedGroup, in isolation);
            publishReturned = true;
            requireScope();
            RequirePublishedPair(entities, published: true, requireMarkers: true);
            RequirePendingFormalRemoval(entities);
            WorldOwnershipConfirmed = true;
            publishedAt = UnityEngine.Time.frameCount;
        }

        private void RequirePendingFormalRemoval(EntityManager entities)
        {
            var links = entities.GetBuffer<SubNet>(original.Root, true);
            Require(links.Length == 2 && links[0].m_SubNet == original.NodeEntity && links[1].m_SubNet == node
                && OwnerOf(entities, original.NodeEntity) == original.Root && entities.HasComponent<Deleted>(original.NodeEntity)
                && !entities.HasComponent<Created>(original.NodeEntity),
                "RELOCATION_PREVIEW_COMMIT_FORMAL_REMOVAL_NOT_STAGED");
        }

        private void RequireRetiredClosure(EntityManager entities, bool applied)
        {
            var identities = new HashSet<Entity>(retired);
            Require(identities.Count == retired.Length, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_DUPLICATE");
            foreach (Entity member in retired)
            {
                Require(entities.Exists(member) && entities.HasComponent<Temp>(member)
                    && entities.HasComponent<Deleted>(member) == applied && entities.HasComponent<Disabled>(member)
                    && entities.HasComponent<ObjectGenerationScope>(member)
                    && entities.GetSharedComponent<ObjectGenerationScope>(member).Value == actor
                    && !entities.HasComponent<Attached>(member) && !entities.HasBuffer<LinkedEntityGroup>(member),
                    "RELOCATION_PREVIEW_COMMIT_RETIREMENT_MEMBER_CHANGED");
                Entity owner = OwnerOf(entities, member);
                Require(owner == Entity.Null || identities.Contains(owner), "RELOCATION_PREVIEW_COMMIT_RETIREMENT_OWNER_CHANGED");
                using var types = entities.GetComponentTypes(member, Allocator.Temp);
                Require(types.Length <= 280, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_TYPE_BOUND");
                foreach (ComponentType type in types)
                {
                    Require(!type.TypeIndex.IsCleanupComponent && !type.TypeIndex.IsCleanupBufferComponent && !type.TypeIndex.IsCleanupSharedComponent,
                        "RELOCATION_PREVIEW_COMMIT_RETIREMENT_CLEANUP_UNSUPPORTED");
                    if (!type.TypeIndex.IsBuffer) continue;
                    if (type.TypeIndex == TypeManager.GetTypeIndex<Game.Objects.SubObject>())
                    {
                        var children = entities.GetBuffer<Game.Objects.SubObject>(member, true);
                        Require(children.Length <= 4, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_ATTACHMENT_BOUND");
                        var seen = new HashSet<Entity>();
                        foreach (var link in children)
                            Require(identities.Contains(link.m_SubObject) && seen.Add(link.m_SubObject)
                                && OwnerOf(entities, link.m_SubObject) == member, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_ATTACHMENT_CHANGED");
                        continue;
                    }
                    if (!applied && member == retired[0] && type.TypeIndex == TypeManager.GetTypeIndex<SubNet>())
                    {
                        var pending = entities.GetBuffer<SubNet>(member, true);
                        Require(pending.Length == 1 && pending[0].m_SubNet == node, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_SUBNET_CHANGED");
                        continue;
                    }
                    var storage = entities.GetStorageInfo(member);
                    var handle = entities.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(TypeManager.GetType(type.TypeIndex)));
                    var buffers = storage.Chunk.GetUntypedBufferAccessor(ref handle);
                    Require(storage.IndexInChunk >= 0 && storage.IndexInChunk < buffers.Length
                        && buffers.GetBufferLength(storage.IndexInChunk) == 0, "RELOCATION_PREVIEW_COMMIT_RETIREMENT_BUFFER_NOT_EMPTY");
                }
            }
        }

        private void RequirePublishedPair(EntityManager entities, bool published, bool requireMarkers)
        {
            foreach (Entity member in new[] { node, leaf })
            {
                Require(entities.Exists(member) && !entities.HasComponent<Temp>(member) && !entities.HasComponent<Deleted>(member)
                    && !entities.HasComponent<Hidden>(member) && entities.HasComponent<Disabled>(member) != published
                    && entities.HasComponent<ObjectGenerationScope>(member) != published
                    && !entities.HasComponent<Attached>(member), "RELOCATION_PREVIEW_COMMIT_SURVIVOR_STATE_CHANGED");
                if (!published)
                    Require(entities.GetSharedComponent<ObjectGenerationScope>(member).Value == actor,
                        "RELOCATION_PREVIEW_COMMIT_SURVIVOR_SCOPE_CHANGED");
                if (requireMarkers)
                    Require(entities.HasComponent<Applied>(member) && entities.HasComponent<Created>(member)
                        && entities.HasComponent<Updated>(member), "RELOCATION_PREVIEW_COMMIT_SURVIVOR_MARKERS_CHANGED");
            }
            Require(OwnerOf(entities, node) == original.Root && OwnerOf(entities, leaf) == node
                && entities.GetComponentData<Node>(node).Equals(nodePose)
                && entities.GetComponentData<PrefabRef>(node).m_Prefab == original.NodePrefab
                && entities.GetComponentData<PrefabRef>(leaf).m_Prefab == leafPrefab
                && entities.GetComponentData<Transform>(original.Root).Equals(placement),
                "RELOCATION_PREVIEW_COMMIT_SURVIVOR_RELATION_CHANGED");
            if (requireMarkers)
            {
                var children = entities.GetBuffer<Game.Objects.SubObject>(node, true);
                Require(children.Length == 1 && children[0].m_SubObject == leaf
                    && entities.GetBuffer<ConnectedEdge>(node, true).Length == 0,
                    "RELOCATION_PREVIEW_COMMIT_SURVIVOR_CLOSURE_CHANGED");
            }
        }

        internal void ObserveDefault(Action requireScope)
        {
            requireScope();
            int frame = UnityEngine.Time.frameCount;
            Require(WorldOwnershipConfirmed && publishedAt.HasValue && frame >= publishedAt.Value
                && frame - publishedAt.Value <= FollowupFrames, "RELOCATION_PREVIEW_COMMIT_DEFAULT_NOT_READY");
            if (DefaultReady || frame == publishedAt) return;
            Require(ReferenceEquals(source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>(), electricity)
                && ReferenceEquals(source.GetExistingSystemManaged<ElectricityFlowSystem>(), flow),
                "RELOCATION_PREVIEW_COMMIT_ELECTRICITY_PROVIDER_CHANGED");
            if (electricity.CheckedStateRef.LastSystemVersion == graphVersion) return;
            electricity.CheckedStateRef.CompleteDependency();
            requireScope();
            var current = ReadSettled(source.EntityManager);
            DefaultReady = current != null && previousReadyFrame.HasValue && frame != previousReadyFrame
                && JToken.DeepEquals(previousReadySample, current);
            previousReadySample = current;
            previousReadyFrame = current == null ? (int?)null : frame;
            if (DefaultReady) settled = current;
        }

        private JObject? ReadSettled(EntityManager entities)
        {
            RequirePublishedPair(entities, published: true, requireMarkers: false);
            var subnet = entities.GetBuffer<SubNet>(original.Root, true);
            if (subnet.Length != 1 || subnet[0].m_SubNet != node || entities.Exists(original.NodeEntity)
                || entities.Exists(original.ChildAt(original.NodeEntity, 0)) || entities.Exists(original.ChildAt(original.NodeEntity, 1)))
                return null;

            var buildingChildren = entities.GetBuffer<Game.Objects.SubObject>(original.Root, true);
            var nodeChildren = entities.GetBuffer<Game.Objects.SubObject>(node, true);
            if (buildingChildren.Length != 4 || nodeChildren.Length != 2) return null;
            var buildingIds = new JArray();
            for (int index = 0; index < 4; index++)
            {
                Entity child = original.ChildAt(original.Root, index);
                int matches = 0;
                foreach (var link in buildingChildren) if (link.m_SubObject == child) matches++;
                Require(matches == 1 && entities.Exists(child) && OwnerOf(entities, child) == original.Root,
                    "RELOCATION_PREVIEW_COMMIT_DEFAULT_BUILDING_CHILD_CHANGED");
                if (!Stable(entities, child)) return null;
                buildingIds.Add(Id(child));
            }

            Entity first = nodeChildren[0].m_SubObject, second = nodeChildren[1].m_SubObject;
            if (first == second || first != leaf && second != leaf || !Stable(entities, original.Root) || !Stable(entities, node)) return null;
            Entity added = first == leaf ? second : first;
            if (!Stable(entities, leaf) || !Stable(entities, added) || OwnerOf(entities, added) != node) return null;
            var currentElectricity = PreviewCommitGraph.ReadElectricity(entities, original.Root, node, flow.sourceNode);
            if (currentElectricity == null) return null;
            return new JObject {
                ["buildingChildren"] = buildingIds, ["nodeChildren"] = new JArray(Id(leaf), Id(added)),
                ["electricity"] = currentElectricity
            };
        }

        internal void RequireSeparateBuild(Entity newRoot, Entity newNode, Action requireScope)
        {
            requireScope();
            var entities = source.EntityManager;
            Require(DefaultReady && newRoot != original.Root && newNode != node && newNode != original.NodeEntity
                && Stable(entities, newRoot) && Stable(entities, newNode) && OwnerOf(entities, newNode) == newRoot
                && !entities.GetComponentData<Transform>(newRoot).Equals(placement),
                "RELOCATION_PREVIEW_REBUILD_IDENTITY_OR_OWNER_CHANGED");
            Require(ReferenceEquals(source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>(), electricity)
                && ReferenceEquals(source.GetExistingSystemManaged<ElectricityFlowSystem>(), flow),
                "RELOCATION_PREVIEW_COMMIT_ELECTRICITY_PROVIDER_CHANGED");
            electricity.CheckedStateRef.CompleteDependency();
            requireScope();
            var current = ReadSettled(entities);
            var rebuiltElectricity = PreviewCommitGraph.ReadElectricity(entities, newRoot, newNode, flow.sourceNode);
            Require(current != null && rebuiltElectricity != null
                && JToken.DeepEquals(settled?["buildingChildren"], current["buildingChildren"])
                && JToken.DeepEquals(settled?["nodeChildren"], current["nodeChildren"]),
                "RELOCATION_PREVIEW_REBUILD_MIGRATED_GRAPH_CHANGED");
            foreach (string key in new[] { "producerEdge", "producerNode", "valveNode", "networkNode", "producerToValve", "valveToNetwork" })
                Require(!JToken.DeepEquals(current!["electricity"]?[key], rebuiltElectricity![key]),
                    "RELOCATION_PREVIEW_REBUILD_ELECTRICITY_ALIAS");
            Require(JToken.DeepEquals(current!["electricity"]?["sourceNode"], rebuiltElectricity!["sourceNode"]),
                "RELOCATION_PREVIEW_REBUILD_ELECTRICITY_SOURCE_CHANGED");
            settled = current;
            simultaneousRebuildElectricity = rebuiltElectricity;
        }

        private static Entity OwnerOf(EntityManager entities, Entity member) =>
            entities.Exists(member) && entities.HasComponent<Owner>(member) ? entities.GetComponentData<Owner>(member).m_Owner : Entity.Null;

        private static bool Stable(EntityManager entities, Entity member) => entities.Exists(member)
            && !entities.HasComponent<Deleted>(member) && !entities.HasComponent<Temp>(member)
            && !entities.HasComponent<Disabled>(member) && !entities.HasComponent<ObjectGenerationScope>(member)
            && !entities.HasComponent<Created>(member) && !entities.HasComponent<Applied>(member) && !entities.HasComponent<Updated>(member);

        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);

        internal JObject Snapshot() => new JObject {
            ["worldOwnershipConfirmed"] = WorldOwnershipConfirmed, ["defaultReady"] = DefaultReady,
            ["root"] = Id(original.Root), ["oldNode"] = Id(original.NodeEntity), ["newNode"] = Id(node), ["newChild"] = Id(leaf),
                        ["retiredCount"] = retired.Length, ["oldExtraChildSecondary"] = oldExtraChildSecondary,
            ["destroyEntered"] = destroyEntered, ["destroyReturned"] = destroyReturned,
            ["absenceConfirmed"] = absenceConfirmed, ["publishEntered"] = publishEntered, ["publishReturned"] = publishReturned,
                        ["publishedAt"] = publishedAt, ["electricity"] = settled?["electricity"]?.DeepClone(),
            ["buildingChildren"] = settled?["buildingChildren"]?.DeepClone(), ["nodeChildren"] = settled?["nodeChildren"]?.DeepClone(),
            ["separateElectricityConfirmed"] = simultaneousRebuildElectricity != null, ["rebuildElectricity"] = simultaneousRebuildElectricity?.DeepClone()
        };

        private static void Require(bool condition, string code)
        { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
