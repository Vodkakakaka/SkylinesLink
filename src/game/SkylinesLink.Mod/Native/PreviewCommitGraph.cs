#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Disabled = Unity.Entities.Disabled;
using Transform = Game.Objects.Transform;

namespace SkylinesLink.Mod.Native
{
    internal sealed class PreviewCommitGraph
    {
        private readonly Entity[] members, prefabs, owners;
        private readonly Transform placement;
        private readonly Temp[] previewTemps;
        private readonly Game.Net.Node previewNode;
        private readonly int price;
        private JObject? electricity, defaultRelations, previousReadyRelations;
        private int? previousReadyFrame;
        internal bool DefaultRelationsStable { get; private set; }
        internal bool InitialElectricityAbsent { get; }
        internal (Entity Root, Entity Node) BuildingAndNode => (members[0], members[1]);
        // Asset-required rows were proved at capture. This graph owns only the
        // structural bound: two parents, the admitted node row, and actual leaves.
        internal static bool IsNewMemberCount(int count) => count >= 2 + SubObjectGenerationPreflight.NormalNodeRows
            && count <= 2 + SubObjectGenerationPreflight.MaximumNormalCreates;

        internal PreviewCommitGraph(EntityManager entities, Entity[] graph)
        {
            Require(IsNewMemberCount(graph.Length), "PREVIEW_COMMIT_MEMBER_COUNT_UNCONFIRMED");
            members = (Entity[])graph.Clone();
            prefabs = new Entity[members.Length];
            owners = new Entity[members.Length];
            previewTemps = new Temp[members.Length];
            for (int index = 0; index < members.Length; index++)
            {
                Entity member = members[index];
                Require(member != Entity.Null && Array.IndexOf(members, member) == index
                    && entities.Exists(member) && entities.HasComponent<PrefabRef>(member)
                    && entities.HasComponent<Temp>(member), "PREVIEW_COMMIT_MEMBER_UNCONFIRMED");
                prefabs[index] = entities.GetComponentData<PrefabRef>(member).m_Prefab;
                previewTemps[index] = entities.GetComponentData<Temp>(member);
                owners[index] = entities.HasComponent<Owner>(member)
                    ? entities.GetComponentData<Owner>(member).m_Owner : Entity.Null;
            }
            Require(owners[0] == Entity.Null && owners[1] == members[0], "PREVIEW_COMMIT_PARENT_CHANGED");
            int nodeChildren = 0;
            for (int index = 2; index < members.Length; index++)
            {
                Require(owners[index] == members[0] || owners[index] == members[1], "PREVIEW_COMMIT_CHILD_OWNER_UNCONFIRMED");
                if (owners[index] == members[1]) nodeChildren++;
            }
            Require(nodeChildren == 1, "PREVIEW_COMMIT_NODE_CHILD_COUNT_UNCONFIRMED");
            placement = entities.GetComponentData<Transform>(members[0]);
            price = previewTemps[0].m_Cost;
            Require(price > 0 && entities.HasComponent<Building>(members[0]) && entities.HasComponent<Game.Net.Node>(members[1]),
                "PREVIEW_COMMIT_ROOT_OR_NODE_UNCONFIRMED");
            previewNode = entities.GetComponentData<Game.Net.Node>(members[1]);
            InitialElectricityAbsent = !entities.HasComponent<ElectricityBuildingConnection>(members[0])
                && !entities.HasComponent<ElectricityNodeConnection>(members[1]) && !entities.HasComponent<ElectricityValveConnection>(members[1]);
            Require(InitialElectricityAbsent, "PREVIEW_COMMIT_EXISTING_ELECTRICITY");
            RequireRelations(entities);
        }

        internal void RequirePrepared(EntityManager entities)
        {
            for (int index = 0; index < members.Length; index++)
            {
                Entity member = members[index];
                Require(entities.Exists(member) && entities.HasComponent<Disabled>(member)
                    && entities.HasComponent<ObjectGenerationScope>(member)
                    && entities.GetSharedComponent<ObjectGenerationScope>(member).Value == 1
                    && entities.HasComponent<Temp>(member) && entities.GetComponentData<Temp>(member).Equals(previewTemps[index])
                    && entities.HasComponent<PrefabRef>(member) && entities.GetComponentData<PrefabRef>(member).m_Prefab == prefabs[index]
                    && !entities.HasComponent<Deleted>(member) && !entities.HasComponent<Error>(member)
                    && !entities.HasComponent<Warning>(member) && !entities.HasComponent<Override>(member)
                    && !entities.HasComponent<Recent>(member), "PREVIEW_COMMIT_PREPARED_MEMBER_CHANGED");
            }
            Require(entities.GetComponentData<Transform>(members[0]).Equals(placement), "PREVIEW_COMMIT_PLACEMENT_CHANGED");
            RequireNodeBeforePlayback(entities);
        }

        internal JObject ObserveEvents(TriggerAction[] actions)
        {
            Require(actions.Length == members.Length - 1, "PREVIEW_COMMIT_EVENT_COUNT_CHANGED");
            var seen = new HashSet<Entity>();
            var rows = new JArray();
            foreach (var action in actions)
            {
                int index = Array.IndexOf(members, action.m_PrimaryTarget);
                Require(index >= 0 && index != 1 && seen.Add(action.m_PrimaryTarget)
                    && action.m_TriggerType == TriggerType.ObjectCreated
                    && action.m_SecondaryTarget == action.m_PrimaryTarget && action.m_TriggerPrefab == prefabs[index]
                    && math.isfinite(action.m_Value) && action.m_Value >= 0 && math.floor(action.m_Value) == action.m_Value,
                    "PREVIEW_COMMIT_EVENT_UNQUALIFIED");
                rows.Add(new JObject { ["entity"] = Id(action.m_PrimaryTarget), ["prefab"] = Id(action.m_TriggerPrefab),
                    ["previousCount"] = action.m_Value });
            }
            return new JObject { ["type"] = "ObjectCreated", ["objects"] = rows, ["netCount"] = 0 };
        }

        internal void RequireApplied(EntityManager entities, bool published, bool initialMarkersRequired)
        {
            for (int index = 0; index < members.Length; index++)
            {
                Entity member = members[index];
                Require(entities.Exists(member) && !entities.HasComponent<Temp>(member) && !entities.HasComponent<Deleted>(member)
                    && entities.HasComponent<Disabled>(member) != published
                    && entities.HasComponent<ObjectGenerationScope>(member) != published
                    && entities.GetComponentData<PrefabRef>(member).m_Prefab == prefabs[index], "PREVIEW_COMMIT_ENTITY_UNCONFIRMED");
                if (!published)
                    Require(entities.GetSharedComponent<ObjectGenerationScope>(member).Value == 1,
                        "PREVIEW_COMMIT_ACTOR_CHANGED");
                if (initialMarkersRequired)
                    Require(entities.HasComponent<Applied>(member) && entities.HasComponent<Created>(member)
                        && entities.HasComponent<Updated>(member), "PREVIEW_COMMIT_APPLY_MARKERS_UNCONFIRMED");
            }
            RequireRelations(entities);
            Require(entities.GetComponentData<Transform>(members[0]).Equals(placement), "PREVIEW_COMMIT_PLACEMENT_CHANGED");
            if (initialMarkersRequired)
                Require(entities.HasComponent<Recent>(members[0])
                    && entities.GetComponentData<Recent>(members[0]).m_ModificationCost == price, "PREVIEW_COMMIT_RECENT_COST_UNCONFIRMED");
        }

        internal void RequireNodeBeforePlayback(EntityManager entities)
        {
            Entity node = members[1];
            Require(entities.Exists(node) && entities.HasComponent<Disabled>(node)
                && entities.HasComponent<ObjectGenerationScope>(node) && entities.GetSharedComponent<ObjectGenerationScope>(node).Value == 1
                && entities.HasComponent<Temp>(node) && entities.GetComponentData<Temp>(node).Equals(previewTemps[1])
                && entities.HasComponent<Game.Net.Node>(node) && entities.GetComponentData<Game.Net.Node>(node).Equals(previewNode)
                && !entities.HasComponent<Game.Net.Edge>(node) && !entities.HasComponent<Game.Net.Lane>(node)
                && !entities.HasComponent<Game.Net.Aggregate>(node)
                && entities.GetComponentData<PrefabRef>(node).m_Prefab == prefabs[1], "PREVIEW_COMMIT_NODE_BEFORE_PLAYBACK_CHANGED");
            RequireRelations(entities);
        }

        internal bool ObserveDefault(EntityManager entities, Entity sourceNode, int frame)
        {
            if (!ReadDefaultRelations(entities, frame)) return false;
            electricity = ReadElectricity(entities, sourceNode);
            return electricity != null;
        }

        internal void RequireElectricityUnchanged(EntityManager entities, Entity sourceNode) =>
            Require(electricity != null && JToken.DeepEquals(electricity, ReadElectricity(entities, sourceNode)),
                "RELOCATION_SOURCE_ELECTRICITY_CHANGED");

        private bool ReadDefaultRelations(EntityManager entities, int frame)
        {
            bool core = IsWorldEntity(entities, members[0]) && IsWorldEntity(entities, members[1]);
            if (core)
            {
                var subNets = entities.GetBuffer<Game.Net.SubNet>(members[0], true);
                core = subNets.Length == 1 && subNets[0].m_SubNet == members[1]
                    && entities.GetBuffer<Game.Net.ConnectedEdge>(members[1], true).Length == 0
                    && entities.GetComponentData<Transform>(members[0]).Equals(placement);
            }
            var original = new JArray();
            bool originalsRemain = core;
            for (int index = 0; index < members.Length; index++)
            {
                Entity member = members[index];
                bool retained = IsWorldEntity(entities, member) && entities.HasComponent<PrefabRef>(member)
                    && entities.GetComponentData<PrefabRef>(member).m_Prefab == prefabs[index]
                    && OwnerOf(entities, member) == owners[index];
                originalsRemain &= retained;
                if (index >= 2) original.Add(new JObject { ["entity"] = Id(member), ["retained"] = retained, ["state"] = State(entities, member) });
            }
            var parents = new JArray();
            var seen = new HashSet<Entity>();
            bool relations = core;
            bool markersExpired = core && (State(entities, members[0]) & 56) == 0 && (State(entities, members[1]) & 56) == 0;
            for (int parent = 0; parent < 2; parent++)
            {
                Entity owner = members[parent];
                bool has = entities.Exists(owner) && entities.HasBuffer<Game.Objects.SubObject>(owner);
                if (!has) { parents.Add(new JObject { ["parent"] = Id(owner), ["count"] = null }); relations = false; continue; }
                var links = entities.GetBuffer<Game.Objects.SubObject>(owner, true);
                int length = links.Length;
                var rows = new JArray();
                parents.Add(new JObject { ["parent"] = Id(owner), ["count"] = length, ["rows"] = rows });
                if (length > 12) { relations = false; continue; }
                var ids = new List<Entity>(length);
                foreach (var link in links) ids.Add(link.m_SubObject);
                ids.Sort((a, b) => a.Index == b.Index ? a.Version.CompareTo(b.Version) : a.Index.CompareTo(b.Index));
                foreach (Entity id in ids)
                {
                    int index = Array.IndexOf(members, id);
                    Entity actualOwner = OwnerOf(entities, id);
                    Entity prefab = entities.Exists(id) && entities.HasComponent<PrefabRef>(id)
                        ? entities.GetComponentData<PrefabRef>(id).m_Prefab : Entity.Null;
                    var attached = entities.Exists(id) && entities.HasComponent<Game.Objects.Attached>(id)
                        ? entities.GetComponentData<Game.Objects.Attached>(id).m_Parent : Entity.Null;
                    int state = State(entities, id);
                    rows.Add(new JObject { ["entity"] = Id(id), ["prefab"] = Id(prefab), ["owner"] = Id(actualOwner),
                        ["attached"] = Id(attached), ["original"] = index >= 2, ["state"] = state });
                    relations &= IsWorldEntity(entities, id) && prefab != Entity.Null && entities.Exists(prefab)
                        && seen.Add(id) && (index >= 2 ? owners[index] == owner && actualOwner == owner
                            : index < 0 && (actualOwner == owner || attached == owner));
                    markersExpired &= (state & 56) == 0;
                }
            }
            for (int index = 2; index < members.Length; index++) relations &= seen.Contains(members[index]);
            bool ready = originalsRemain && relations && markersExpired;
            defaultRelations = new JObject { ["core"] = core, ["originalsRetained"] = originalsRemain,
                ["complete"] = relations, ["markersExpired"] = markersExpired, ["originalChildren"] = original, ["parents"] = parents };
            DefaultRelationsStable = ready && previousReadyFrame.HasValue && previousReadyFrame != frame
                && JToken.DeepEquals(previousReadyRelations, defaultRelations);
            previousReadyRelations = ready ? defaultRelations : null;
            previousReadyFrame = ready ? frame : (int?)null;
            return DefaultRelationsStable;
        }

        private JObject? ReadElectricity(EntityManager entities, Entity sourceNode) =>
            ReadElectricity(entities, members[0], members[1], sourceNode);

        internal static JObject? ReadElectricity(EntityManager entities, Entity root, Entity node, Entity sourceNode)
        {
            if (!entities.HasBuffer<Game.Net.ConnectedEdge>(node)) return null;
            int connectedEdges = entities.GetBuffer<Game.Net.ConnectedEdge>(node, true).Length;
            if (connectedEdges != 0) return null;
            if (!entities.HasComponent<ElectricityBuildingConnection>(root)
                || !entities.HasComponent<ElectricityNodeConnection>(node) || !entities.HasComponent<ElectricityValveConnection>(node))
                return null;

            Entity producerEdge = entities.GetComponentData<ElectricityBuildingConnection>(root).m_ProducerEdge;
            Entity network = entities.GetComponentData<ElectricityNodeConnection>(node).m_ElectricityNode;
            Entity valve = entities.GetComponentData<ElectricityValveConnection>(node).m_ValveNode;
            if (!IsFlowNode(entities, network) || !IsFlowNode(entities, valve) || network == valve
                || !IsWorldEntity(entities, producerEdge) || !entities.HasComponent<ElectricityFlowEdge>(producerEdge))
                return null;
            var producer = entities.GetComponentData<ElectricityFlowEdge>(producerEdge);
            if (producer.m_Start != sourceNode || !IsFlowNode(entities, producer.m_Start) || !IsFlowNode(entities, producer.m_End)
                || producer.m_End == valve || producer.m_End == network)
                return null;
            Entity producerToValve = ReadEdge(entities, producer.m_End, valve);
            Entity valveToNetwork = ReadEdge(entities, valve, network);
            if (producerToValve == Entity.Null || valveToNetwork == Entity.Null) return null;
            return new JObject { ["producerEdge"] = Id(producerEdge), ["producerNode"] = Id(producer.m_End),
                ["valveNode"] = Id(valve), ["networkNode"] = Id(network), ["producerToValve"] = Id(producerToValve),
                ["valveToNetwork"] = Id(valveToNetwork), ["sourceNode"] = Id(sourceNode), ["nodeConnectedNetEdges"] = connectedEdges };
        }

        private void RequireRelations(EntityManager entities)
        {
            var root = members[0];
            var node = members[1];
            var subNets = entities.GetBuffer<Game.Net.SubNet>(root, true);
            Require(subNets.Length == 1 && subNets[0].m_SubNet == node
                && entities.GetBuffer<Game.Net.ConnectedEdge>(node, true).Length == 0, "PREVIEW_COMMIT_SUBNET_CHANGED");
            var seen = new HashSet<Entity>();
            for (int parent = 0; parent < 2; parent++)
            {
                var children = entities.GetBuffer<Game.Objects.SubObject>(members[parent], true);
                Require(children.Length <= members.Length - 2, "PREVIEW_COMMIT_SUBOBJECT_COUNT_CHANGED");
                foreach (var row in children)
                {
                    int index = Array.IndexOf(members, row.m_SubObject, 2);
                    Require(index >= 2 && seen.Add(row.m_SubObject) && owners[index] == members[parent],
                        "PREVIEW_COMMIT_SUBOBJECT_EDGE_CHANGED");
                }
            }
            Require(seen.Count == members.Length - 2, "PREVIEW_COMMIT_SUBOBJECT_COUNT_CHANGED");
            for (int index = 0; index < members.Length; index++)
            {
                var owner = entities.HasComponent<Owner>(members[index])
                    ? entities.GetComponentData<Owner>(members[index]).m_Owner : Entity.Null;
                Require(owner == owners[index], "PREVIEW_COMMIT_OWNER_CHANGED");
            }
        }

        private static bool IsFlowNode(EntityManager entities, Entity value) =>
            IsWorldEntity(entities, value) && entities.HasComponent<ElectricityFlowNode>(value);

        private static bool IsWorldEntity(EntityManager entities, Entity value) =>
            value != Entity.Null && entities.Exists(value) && !entities.HasComponent<Deleted>(value)
            && !entities.HasComponent<Temp>(value) && !entities.HasComponent<Disabled>(value)
            && !entities.HasComponent<ObjectGenerationScope>(value);

        private static Entity OwnerOf(EntityManager entities, Entity value) =>
            entities.Exists(value) && entities.HasComponent<Owner>(value) ? entities.GetComponentData<Owner>(value).m_Owner : Entity.Null;

        private static int State(EntityManager entities, Entity value)
        {
            if (!entities.Exists(value)) return 0;
            return 1 | (entities.HasComponent<Deleted>(value) ? 2 : 0) | (entities.HasComponent<Temp>(value) ? 4 : 0)
                | (entities.HasComponent<Created>(value) ? 8 : 0) | (entities.HasComponent<Updated>(value) ? 16 : 0)
                | (entities.HasComponent<Applied>(value) ? 32 : 0) | (entities.HasComponent<Disabled>(value) ? 64 : 0);
        }

        private static Entity ReadEdge(EntityManager entities, Entity start, Entity end)
        {
            if (!entities.HasBuffer<ConnectedFlowEdge>(start)) return Entity.Null;
            var edges = entities.GetBuffer<ConnectedFlowEdge>(start, true);
            Require(edges.Length <= 32, "PREVIEW_COMMIT_LOCAL_FLOW_BUDGET");
            foreach (var row in edges)
            {
                if (!IsWorldEntity(entities, row.m_Edge) || !entities.HasComponent<ElectricityFlowEdge>(row.m_Edge)) continue;
                var edge = entities.GetComponentData<ElectricityFlowEdge>(row.m_Edge);
                if (edge.m_Start == start && edge.m_End == end) return row.m_Edge;
            }
            return Entity.Null;
        }

        internal JObject Snapshot()
        {
            var rows = new JArray();
            for (int i = 0; i < members.Length; i++)
                rows.Add(new JObject { ["entity"] = Id(members[i]), ["prefab"] = Id(prefabs[i]), ["owner"] = Id(owners[i]) });
            return new JObject { ["members"] = rows, ["price"] = price, ["initialElectricityAbsent"] = InitialElectricityAbsent,
                ["defaultRelations"] = defaultRelations?.DeepClone(), ["defaultRelationsStable"] = DefaultRelationsStable,
                ["electricity"] = electricity?.DeepClone() };
        }

        internal static JArray Id(Entity value) => new JArray(value.Index, value.Version);
        private static void Require(bool value, string code) { if (!value) throw new PreviewCommitExecution.Rejection(code); }
    }
}
#endif
