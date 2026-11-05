#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // Development input inspection only. The caller must freshly establish Ready
    // and recheck its loading scope afterwards. This does not establish placement
    // eligibility, a preview price, or permission to run any native producer.
    internal static class ObjectDefinitionInputProbe
    {
        private const int MaxRoots = 128, MaxPrefabs = 256, MaxBuffer = 256, MaxEntries = 4096;
        private const int MaxDepth = 8, MaxAreaOutputNodes = 4096;

        internal enum Purpose { Medical, Transformer, WaterTower, WindPower }

        internal sealed class Result
        {
            // Borrowed source reference for this main-thread call chain only.
            // Never retain it across a loading boundary, await, or worker handoff.
            internal readonly World? Source;
            internal readonly Purpose Purpose;
            internal readonly Entity Root;
            internal readonly int RootCount;
            internal readonly bool Complete;
            internal readonly string? FailureCode;
            internal readonly IReadOnlyList<CandidateStatistics> Candidates;
            internal Result(World? source, Purpose purpose, Entity root, int count, bool complete, string? failure, List<CandidateStatistics> candidates)
            { Source = source; Purpose = purpose; Root = root; RootCount = count; Complete = complete; FailureCode = failure; Candidates = candidates.AsReadOnly(); }
        }

        internal sealed class CandidateStatistics
        {
            internal readonly Purpose Purpose;
            internal readonly int RootIndex, RootVersion, LotWidth, LotDepth;
            internal readonly int? PatientCapacity;
            // Purpose-specific native values, not a shared capacity or a claim
            // about actual supply. Their UI unit conversions remain unverified.
            internal readonly int? WaterSourceTypesRaw, WaterCapacityRaw, WindNominalProductionRaw, BasePowerPlantProductionRaw;
            internal readonly float? WaterPurificationRaw, WindMaximumWindRaw;
            internal readonly uint RootConstructionCost;
            // Complete also covers an explicit root-filter rejection; only
            // InspectedReferencesComplete states that the child graph was inspected.
            internal readonly bool Eligible, Complete, InspectedReferencesComplete;
            internal readonly string? FailureCode;
            // Source metadata counts per distinct physical prefab, not counts
            // of eventual instances; subobjects are deferred to later systems.
            internal readonly int UniquePrefabs, BufferEntries, SubObjects, SubNets, SubAreas, AreaSourceNodes;
            internal readonly int Placeholders, Requirements, ReferenceLeaves, MaximumDepth, MaximumNetNodeIndex;
            internal readonly int RootSubNetCount, RootSubAreaCount, CommandUpperBound, AreaOutputNodeUpperBound;
            internal readonly int DeferredContentReferences;
            internal CandidateStatistics(Candidate value)
            {
                Purpose = value.Purpose; RootIndex = value.Root.Index; RootVersion = value.Root.Version;
                PatientCapacity = value.Patients; LotWidth = value.Lot.x; LotDepth = value.Lot.y;
                WaterSourceTypesRaw = value.WaterTypes; WaterCapacityRaw = value.WaterCapacity; WaterPurificationRaw = value.WaterPurification;
                WindNominalProductionRaw = value.WindProduction; WindMaximumWindRaw = value.MaximumWind;
                BasePowerPlantProductionRaw = value.BasePowerProduction;
                RootConstructionCost = value.Cost; Eligible = value.Eligible; Complete = value.Complete;
                InspectedReferencesComplete = value.GraphComplete; FailureCode = value.Failure;
                UniquePrefabs = value.Seen.Count; BufferEntries = value.Entries; SubObjects = value.Objects;
                SubNets = value.Nets; SubAreas = value.Areas; AreaSourceNodes = value.Nodes;
                Placeholders = value.Placeholders; Requirements = value.Requirements; ReferenceLeaves = value.Leaves.Count;
                MaximumDepth = value.Depth; MaximumNetNodeIndex = value.MaximumNetIndex;
                RootSubNetCount = value.ImmediateNets; RootSubAreaCount = value.ImmediateAreas;
                CommandUpperBound = 4 + 6 * RootSubNetCount + 5 * RootSubAreaCount;
                AreaOutputNodeUpperBound = value.ImmediateNodes; DeferredContentReferences = value.DeferredContent.Count;
            }
        }

        internal sealed class Candidate
        {
            internal Entity Root;
            internal Purpose Purpose;
            internal uint Cost;
            internal int? Patients, WaterTypes, WaterCapacity, WindProduction, BasePowerProduction;
            internal float? WaterPurification, MaximumWind;
            internal int2 Lot;
            internal bool Eligible, GraphComplete, Complete = true;
            internal string? Failure;
            internal int Entries, Objects, Nets, Areas, Nodes, Placeholders, Requirements, Depth, MaximumNetIndex = -1;
            internal int ImmediateNets, ImmediateAreas, ImmediateNodes;
            internal readonly HashSet<Entity> Seen = new HashSet<Entity>();
            internal readonly HashSet<Entity> Stack = new HashSet<Entity>();
            internal readonly HashSet<Entity> Leaves = new HashSet<Entity>();
            internal readonly HashSet<Entity> DeferredContent = new HashSet<Entity>();
        }

        // These copies exist only during Select. Their fixed component set is
        // not a persistent prefab cache or a traversal of all asset metadata.
        private sealed class Node
        {
            internal readonly Entity Entity;
            internal readonly PrefabBase Managed;
            internal bool Expanded, ContentChecked, ContentRestricted, HasAreaNodes, HasPlaceholders;
            internal SubObject[] Objects = Array.Empty<SubObject>();
            internal SubNet[] Nets = Array.Empty<SubNet>();
            internal SubArea[] Areas = Array.Empty<SubArea>();
            internal SubAreaNode[] Nodes = Array.Empty<SubAreaNode>();
            internal PlaceholderObjectElement[] Placeholders = Array.Empty<PlaceholderObjectElement>();
            internal ObjectRequirementElement[] Requirements = Array.Empty<ObjectRequirementElement>();
            internal Node(Entity entity, PrefabBase managed) { Entity = entity; Managed = managed; }
        }

        private sealed class InputFailure : Exception
        {
            internal readonly string Code;
            internal InputFailure(string code) { Code = code; }
        }

        internal static Result Select(World source, PrefabSystem prefabs, CityConfigurationSystem configuration,
            Purpose purpose = Purpose.Medical)
        {
            var reports = new List<CandidateStatistics>();
            if (!MainThreadDispatcher.isMainThread || source == null || !source.IsCreated || prefabs == null || configuration == null
                || prefabs.World != source || configuration.World != source)
                return new Result(source, purpose, Entity.Null, 0, false, "SOURCE_CONTEXT_UNAVAILABLE", reports);
            ComponentType[]? rootComponents = RootComponents(purpose);
            if (rootComponents == null) return new Result(source, purpose, Entity.Null, 0, false, "PURPOSE_UNSUPPORTED", reports);
            var entities = source.EntityManager;
            var input = new Reader(entities, prefabs);
            // PrefabData is enableable: this ordinary query excludes disabled
            // registrations. Locked is checked separately with enable semantics.
            using (var query = entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = rootComponents,
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            }))
            {
                query.CompleteDependency();
                int count = query.CalculateEntityCount();
                if (count > MaxRoots) return new Result(source, purpose, Entity.Null, count, false, "ROOT_COUNT_BUDGET", reports);
                using (var roots = query.ToEntityArray(Allocator.Temp))
                {
                    if (roots.Length != count) return new Result(source, purpose, Entity.Null, count, false, "ROOT_SET_CHANGED", reports);
                    Candidate? chosen = null;
                    foreach (Entity root in roots)
                    {
                        var candidate = new Candidate { Root = root, Purpose = purpose };
                        try
                        {
                            input.Inspect(candidate);
                            if (candidate.Eligible && (chosen == null || Compare(candidate, chosen) < 0)) chosen = candidate;
                        }
                        catch (InputFailure failure) { candidate.Complete = false; candidate.Failure = failure.Code; }
                        catch (Exception) { candidate.Complete = false; candidate.Failure = "SOURCE_READ_FAILED"; }
                        reports.Add(new CandidateStatistics(candidate));
                        if (!candidate.Complete)
                            return new Result(source, purpose, Entity.Null, count, false, candidate.Failure, reports);
                    }
                    return new Result(source, purpose, chosen == null ? Entity.Null : chosen.Root, count, true,
                        chosen == null ? NoEligibleRoot(purpose) : null, reports);
                }
            }
        }

        private static ComponentType[]? RootComponents(Purpose purpose)
        {
            var all = new List<ComponentType> { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PlaceableObjectData>(), ComponentType.ReadOnly<BuildingData>() };
            switch (purpose)
            {
                case Purpose.Medical: all.Add(ComponentType.ReadOnly<HospitalData>()); break;
                case Purpose.Transformer: all.Add(ComponentType.ReadOnly<TransformerData>()); break;
                case Purpose.WaterTower: all.Add(ComponentType.ReadOnly<WaterTowerData>()); break;
                case Purpose.WindPower: all.Add(ComponentType.ReadOnly<WindPoweredData>()); break;
                default: return null;
            }
            return all.ToArray();
        }

        private static string NoEligibleRoot(Purpose purpose) => purpose switch
        {
            Purpose.Medical => "NO_COMPLETE_ELIGIBLE_MEDICAL_ROOT",
            Purpose.Transformer => "NO_COMPLETE_ELIGIBLE_TRANSFORMER_ROOT",
            Purpose.WaterTower => "NO_COMPLETE_ELIGIBLE_WATER_TOWER_ROOT",
            Purpose.WindPower => "NO_COMPLETE_ELIGIBLE_WIND_POWER_ROOT",
            _ => "PURPOSE_UNSUPPORTED"
        };

        private static int Compare(Candidate left, Candidate right)
        {
            int result = left.ImmediateNodes.CompareTo(right.ImmediateNodes);
            if (result == 0) result = (left.ImmediateNets + left.ImmediateAreas).CompareTo(right.ImmediateNets + right.ImmediateAreas);
            if (result == 0) result = left.Seen.Count.CompareTo(right.Seen.Count);
            if (result == 0) result = left.Entries.CompareTo(right.Entries);
            if (result == 0) result = ((long)left.Lot.x * left.Lot.y).CompareTo((long)right.Lot.x * right.Lot.y);
            if (result == 0) result = left.Cost.CompareTo(right.Cost);
            if (result == 0) result = left.Root.Index.CompareTo(right.Root.Index);
            return result == 0 ? left.Root.Version.CompareTo(right.Root.Version) : result;
        }

        private sealed class Reader
        {
            private readonly EntityManager entities;
            private readonly PrefabSystem prefabs;
            private readonly Dictionary<Entity, Node> nodes = new Dictionary<Entity, Node>();
            private int entries;
            internal Reader(EntityManager entities, PrefabSystem prefabs) { this.entities = entities; this.prefabs = prefabs; }

            internal void Inspect(Candidate candidate)
            {
                Node root = Basic(candidate.Root);
                candidate.Cost = entities.GetComponentData<PlaceableObjectData>(candidate.Root).m_ConstructionCost;
                candidate.Lot = entities.GetComponentData<BuildingData>(candidate.Root).m_LotSize;
                string? purposeIssue = ReadPurpose(candidate);
                if (entities.HasComponent<Locked>(candidate.Root) && entities.IsComponentEnabled<Locked>(candidate.Root))
                { candidate.Failure = "ROOT_LOCKED"; return; }
                if (candidate.Cost == 0 || candidate.Cost > int.MaxValue || candidate.Lot.x <= 0 || candidate.Lot.y <= 0)
                { candidate.Failure = candidate.Purpose == Purpose.Medical ? "ROOT_NOT_PAID_MEDICAL_BUILDING" : "ROOT_NOT_PAID_UTILITY_BUILDING"; return; }
                if (purposeIssue != null) { candidate.Failure = purposeIssue; return; }
                if (!(root.Managed is BuildingPrefab) || !entities.GetComponentData<ObjectData>(candidate.Root).m_Archetype.Valid
                    || entities.HasComponent<ServiceUpgradeData>(candidate.Root) || entities.HasComponent<AssetStampData>(candidate.Root)
                    || entities.HasComponent<MovingObjectData>(candidate.Root) || entities.HasComponent<CreatureSpawnData>(candidate.Root)
                    || entities.HasComponent<NetObjectData>(candidate.Root))
                { candidate.Failure = "ROOT_OUTSIDE_STANDALONE_NEW_BUILDING_PROBE"; return; }
                Content(root);
                if (root.ContentRestricted) { candidate.Failure = "ROOT_CONTENT_QUALIFICATION_OUTSIDE_PROBE"; return; }
                if (entities.HasBuffer<PlaceholderObjectElement>(candidate.Root)
                    && entities.GetBuffer<PlaceholderObjectElement>(candidate.Root, true).Length != 0)
                { candidate.Failure = "ROOT_VARIATION_OUTSIDE_PROBE"; return; }
                Expand(root);
                // The native root branch fetches SubAreaNode immediately when
                // SubArea exists, including an empty SubArea buffer.
                if (entities.HasBuffer<SubArea>(root.Entity))
                    Require(root.HasAreaNodes, "ROOT_SUBAREA_NODES_UNAVAILABLE");
                Visit(candidate, root, 0, directlyDefined: true, areaSelection: false);
                candidate.GraphComplete = true; candidate.Eligible = true;
            }

            private string? ReadPurpose(Candidate candidate)
            {
                switch (candidate.Purpose)
                {
                    case Purpose.Medical:
                        candidate.Patients = entities.GetComponentData<HospitalData>(candidate.Root).m_PatientCapacity;
                        return candidate.Patients > 0 ? null : "ROOT_NOT_PAID_MEDICAL_BUILDING";
                    case Purpose.Transformer:
                        // TransformerData is an empty marker. Its actual capacity
                        // depends on constructed network nodes; do not invent one.
                        return null;
                    case Purpose.WaterTower:
                        if (!entities.HasComponent<WaterPumpingStationData>(candidate.Root))
                            return "ROOT_WATER_PUMPING_DATA_UNAVAILABLE";
                        WaterPumpingStationData water = entities.GetComponentData<WaterPumpingStationData>(candidate.Root);
                        candidate.WaterTypes = (int)water.m_Types; candidate.WaterCapacity = water.m_Capacity;
                        if (!Finite(water.m_Purification)) return "ROOT_WATER_PURIFICATION_INVALID";
                        candidate.WaterPurification = water.m_Purification;
                        return water.m_Capacity > 0 ? null : "ROOT_WATER_CAPACITY_NOT_POSITIVE";
                    case Purpose.WindPower:
                        WindPoweredData wind = entities.GetComponentData<WindPoweredData>(candidate.Root);
                        candidate.WindProduction = wind.m_Production;
                        if (entities.HasComponent<PowerPlantData>(candidate.Root))
                            candidate.BasePowerProduction = entities.GetComponentData<PowerPlantData>(candidate.Root).m_ElectricityProduction;
                        if (!Finite(wind.m_MaximumWind)) return "ROOT_WIND_PARAMETER_INVALID";
                        candidate.MaximumWind = wind.m_MaximumWind;
                        return wind.m_Production > 0 && wind.m_MaximumWind > 0f ? null : "ROOT_WIND_PARAMETERS_NOT_POSITIVE";
                    default: return "PURPOSE_UNSUPPORTED";
                }
            }

            private Node Basic(Entity entity)
            {
                if (nodes.TryGetValue(entity, out Node node)) return node;
                Require(entity != Entity.Null && entities.Exists(entity) && entities.HasComponent<PrefabData>(entity)
                    && entities.IsComponentEnabled<PrefabData>(entity) && !entities.HasComponent<Deleted>(entity), "PREFAB_REFERENCE_UNAVAILABLE");
                Require(nodes.Count < MaxPrefabs, "UNIQUE_PREFAB_BUDGET");
                if (!prefabs.TryGetPrefab<PrefabBase>(entity, out PrefabBase managed) || managed == null
                    || !prefabs.TryGetEntity(managed, out Entity roundtrip) || roundtrip != entity)
                    throw new InputFailure("PREFAB_ROUNDTRIP_FAILED");
                node = new Node(entity, managed); nodes.Add(entity, node); return node;
            }

            private void Content(Node node)
            {
                if (node.ContentChecked) return;
                // Do not call PrefabSystem.IsAvailable: that public method can
                // update availability caches and invoke UI-facing events.
                node.ContentRestricted = node.Managed.TryGet<ContentPrerequisite>(out _)
                    || node.Managed.asset != null && !string.IsNullOrEmpty(node.Managed.asset.GetMeta().platformID);
                node.ContentChecked = true;
            }

            private T[] Copy<T>(Entity entity) where T : unmanaged, IBufferElementData
            {
                if (!entities.HasBuffer<T>(entity)) return Array.Empty<T>();
                DynamicBuffer<T> buffer = entities.GetBuffer<T>(entity, true);
                int count = buffer.Length;
                Require(count <= MaxBuffer && count <= MaxEntries - entries, "SOURCE_BUFFER_BUDGET");
                entries += count;
                var result = new T[count];
                for (int index = 0; index < count; index++) result[index] = buffer[index];
                return result;
            }

            private void Expand(Node node)
            {
                if (node.Expanded) return;
                node.Objects = Copy<SubObject>(node.Entity); node.Nets = Copy<SubNet>(node.Entity);
                node.Areas = Copy<SubArea>(node.Entity); node.HasAreaNodes = entities.HasBuffer<SubAreaNode>(node.Entity);
                node.Nodes = Copy<SubAreaNode>(node.Entity);
                node.HasPlaceholders = entities.HasBuffer<PlaceholderObjectElement>(node.Entity);
                node.Placeholders = Copy<PlaceholderObjectElement>(node.Entity);
                node.Requirements = Copy<ObjectRequirementElement>(node.Entity); node.Expanded = true;
            }

            private void Visit(Candidate candidate, Node node, int depth, bool directlyDefined, bool areaSelection)
            {
                Require(depth <= MaxDepth, "PREFAB_DEPTH_BUDGET");
                Content(node);
                Require(!directlyDefined || !node.ContentRestricted, "DEFINED_CONTENT_QUALIFICATION_OUTSIDE_PROBE");
                if (!directlyDefined && node.ContentRestricted) candidate.DeferredContent.Add(node.Entity);
                Expand(node);
                if (areaSelection)
                {
                    if (!node.HasPlaceholders) Require(entities.HasComponent<AreaGeometryData>(node.Entity), "AREA_GEOMETRY_UNAVAILABLE");
                    long totalWeight = 0;
                    foreach (PlaceholderObjectElement variation in node.Placeholders)
                    {
                        Node target = Basic(variation.m_Object);
                        Require(entities.HasComponent<SpawnableObjectData>(target.Entity)
                            && entities.GetComponentData<SpawnableObjectData>(target.Entity).m_Probability >= 0, "AREA_VARIATION_DATA_UNAVAILABLE");
                        int weight = entities.GetComponentData<SpawnableObjectData>(target.Entity).m_Probability;
                        totalWeight += weight;
                        Require(totalWeight <= int.MaxValue, "AREA_VARIATION_WEIGHT_BUDGET");
                        if (weight > 0)
                        {
                            Require(entities.HasComponent<AreaGeometryData>(target.Entity), "AREA_GEOMETRY_UNAVAILABLE");
                            Content(target); Require(!target.ContentRestricted, "DEFINED_CONTENT_QUALIFICATION_OUTSIDE_PROBE");
                        }
                    }
                }
                Require(!candidate.Stack.Contains(node.Entity), "PREFAB_GRAPH_CYCLE");
                if (!candidate.Seen.Add(node.Entity)) return;
                candidate.Stack.Add(node.Entity); candidate.Depth = Math.Max(candidate.Depth, depth);
                candidate.Objects += node.Objects.Length; candidate.Nets += node.Nets.Length; candidate.Areas += node.Areas.Length;
                candidate.Nodes += node.Nodes.Length; candidate.Placeholders += node.Placeholders.Length; candidate.Requirements += node.Requirements.Length;
                candidate.Entries += node.Objects.Length + node.Nets.Length + node.Areas.Length + node.Nodes.Length + node.Placeholders.Length + node.Requirements.Length;
                bool root = node.Entity == candidate.Root;
                if (root) { candidate.ImmediateNets = node.Nets.Length; candidate.ImmediateAreas = node.Areas.Length; }
                foreach (SubObject child in node.Objects)
                {
                    Require(Finite(child.m_Position) && math.all(math.isfinite(child.m_Rotation.value)), "SUBOBJECT_TRANSFORM_INVALID");
                    Visit(candidate, Basic(child.m_Prefab), depth + 1, directlyDefined: false, areaSelection: false);
                }
                foreach (SubNet net in node.Nets)
                {
                    int maximum = Math.Max(net.m_NodeIndex.x, net.m_NodeIndex.y);
                    Require(maximum < MaxBuffer, "SUBNET_NODE_SLOT_BUDGET");
                    Require(Finite(net.m_Curve.a) && Finite(net.m_Curve.b) && Finite(net.m_Curve.c) && Finite(net.m_Curve.d), "SUBNET_CURVE_INVALID");
                    candidate.MaximumNetIndex = Math.Max(candidate.MaximumNetIndex, maximum);
                    Visit(candidate, Basic(net.m_Prefab), depth + 1, directlyDefined: root, areaSelection: false);
                }
                foreach (SubArea area in node.Areas)
                {
                    if (root)
                    {
                        int start = area.m_NodeRange.x, end = area.m_NodeRange.y;
                        Require(node.HasAreaNodes && start >= 0 && end > start && end <= node.Nodes.Length, "SUBAREA_RANGE_INVALID");
                        int outputNodes = end - start + 1;
                        Require(outputNodes <= MaxAreaOutputNodes - candidate.ImmediateNodes, "AREA_OUTPUT_NODE_BUDGET");
                        candidate.ImmediateNodes += outputNodes;
                    }
                    // An area prefab's own SubArea may describe later Slave
                    // creation, not an ObjectTool root polygon. Only traverse
                    // that reference here; its NodeRange is not read by this producer.
                    Visit(candidate, Basic(area.m_Prefab), depth + 1, directlyDefined: root, areaSelection: root);
                }
                foreach (SubAreaNode point in node.Nodes) Require(Finite(point.m_Position), "SUBAREA_NODE_INVALID");
                foreach (PlaceholderObjectElement variation in node.Placeholders)
                {
                    bool definedVariation = directlyDefined && areaSelection
                        && entities.GetComponentData<SpawnableObjectData>(variation.m_Object).m_Probability > 0;
                    Visit(candidate, Basic(variation.m_Object), depth + 1, directlyDefined: definedVariation, areaSelection: false);
                }
                foreach (ObjectRequirementElement requirement in node.Requirements)
                    if (requirement.m_Requirement != Entity.Null) candidate.Leaves.Add(requirement.m_Requirement);
                if (entities.HasComponent<SpawnableObjectData>(node.Entity))
                {
                    SpawnableObjectData spawnable = entities.GetComponentData<SpawnableObjectData>(node.Entity);
                    Require(spawnable.m_Probability >= 0, "VARIATION_WEIGHT_INVALID");
                    // Native variation uses these as equality/group keys, not
                    // generated Object prefabs. Do not turn them into an asset walk.
                    if (spawnable.m_RandomizationGroup != Entity.Null) candidate.Leaves.Add(spawnable.m_RandomizationGroup);
                }
                candidate.Stack.Remove(node.Entity);
            }
        }

        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void Require(bool condition, string reason) { if (!condition) throw new InputFailure(reason); }
    }
}
#endif
