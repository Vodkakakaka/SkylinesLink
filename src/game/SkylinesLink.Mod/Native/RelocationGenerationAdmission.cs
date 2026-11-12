#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // Scope for the captured wind relocation only. Native generation still
    // requires exact owned definition queries and empty old-Temp queries.
    internal sealed class RelocationGenerationAdmission
    {
        private const string Prefix = "RELOCATION_GENERATION_PREFLIGHT";
        private RelocationDefinitionPayload? admitted;
        private ObjectGenerationPreflight.CourseSearch oldSearch, newSearch;
        private Entity economyEntity;
        private SimulationSystem? simulation;
        private bool attempted;
        private string? failureCode;

        internal EntityArchetype ObjectArchetype { get; private set; }
        internal EntityArchetype NodeArchetype { get; private set; }
        internal EconomyParameterData EconomyParameters { get; private set; }
        internal uint SimulationFrame { get; private set; }
        // One object queue entry. Each of the two node entries may emit one
        // deferred node and the old entry also emits source visibility tags.
        internal int ObjectCommandUpperBound => 64;
        internal int NodeCommandUpperBound => 48;
        internal bool Matches(World source, RelocationDefinitionPayload payload) =>
            ReferenceEquals(admitted, payload) && admitted != null && ReferenceEquals(admitted.Source, source);

        internal void Run(RelocationDefinitionPayload payload, Action requireScope)
        {
            Require(!attempted, Prefix + "_ALREADY_RAN");
            attempted = true;
            try
            {
                Require(MainThreadDispatcher.isMainThread && payload != null && requireScope != null,
                    Prefix + "_SOURCE_UNAVAILABLE");
                World source = payload!.Source;
                requireScope!();
                Require(source.IsCreated && ObjectGenerationPreflight.SearchVersionsQualified, Prefix + "_NATIVE_BUILD_UNQUALIFIED");
                var original = payload.Original;
                original.RequireUnchanged(source, original.Prefab, requireScope);
                RequireShape(payload);
                var entities = source.EntityManager;

                // Both jobs read the old node's ConnectedEdge buffer. The
                // object job also reads the two children, whose exact ownership
                // and absence of Attached are proved by RequireUnchanged.
                Require(entities.HasComponent<Standalone>(original.NodeEntity)
                    && !entities.HasComponent<Curve>(original.NodeEntity)
                    && !entities.HasComponent<Curve>(original.Root)
                    && !entities.HasComponent<Stopped>(original.Root),
                    Prefix + "_ORIGINAL_GENERATION_BRANCH_UNQUALIFIED");
                Require(ActivePrefab(entities, original.Prefab) && ActivePrefab(entities, original.NodePrefab)
                    && entities.HasComponent<ObjectData>(original.Prefab)
                    && entities.HasComponent<NetData>(original.NodePrefab)
                    && !entities.HasComponent<Game.Prefabs.RoadData>(original.NodePrefab),
                    Prefix + "_PREFAB_UNAVAILABLE");

                ObjectArchetype = entities.GetComponentData<ObjectData>(original.Prefab).m_Archetype;
                NodeArchetype = entities.GetComponentData<NetData>(original.NodePrefab).m_NodeArchetype;
                int threadCount = JobsUtility.ThreadIndexCount;
                Require(BoundedArchetype(ObjectArchetype) && BoundedArchetype(NodeArchetype)
                    && threadCount > 0 && threadCount <= 256, Prefix + "_ALLOCATION_BUDGET");

                // Equal endpoints skip both native curve-wide node searches.
                // Their remaining 3D edge lookups use the same width and local
                // connection metadata, including the old course's prefab fallback.
                oldSearch = new ObjectGenerationPreflight.CourseSearch(
                    ObjectGenerationPreflight.CourseSearchBounds(entities, original.NodePrefab, payload.OldNodeCourse.m_StartPosition, out _),
                    Prefix + "_OLD", original.NodeEntity);
                oldSearch.Walk(source);
                newSearch = new ObjectGenerationPreflight.CourseSearch(
                    ObjectGenerationPreflight.CourseSearchBounds(entities, original.NodePrefab, payload.NewNodeCourse.m_StartPosition, out _),
                    Prefix + "_NEW", Entity.Null);
                newSearch.Walk(source);

                simulation = source.GetExistingSystemManaged<SimulationSystem>();
                Require(simulation != null && ReferenceEquals(simulation.World, source), Prefix + "_SIMULATION_UNAVAILABLE");
                using (var economy = entities.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>()))
                {
                    Require(economy.CalculateEntityCount() == 1, Prefix + "_ECONOMY_UNAVAILABLE");
                    economyEntity = economy.GetSingletonEntity();
                    EconomyParameters = entities.GetComponentData<EconomyParameterData>(economyEntity);
                }
                SimulationFrame = simulation!.frameIndex;
                requireScope();
                admitted = payload;
            }
            catch (EmptyNetBoundsVisitor.Rejection error)
            {
                failureCode = error.Code;
                throw new Rejection(error.Code);
            }
            catch (ObjectGenerationPreflight.Rejection error)
            {
                failureCode = error.Code;
                throw new Rejection(error.Code);
            }
            catch (Rejection error)
            {
                failureCode = error.Code;
                throw;
            }
            catch
            {
                failureCode = Prefix + "_FAILED";
                throw;
            }
        }

        internal void RequireCurrentEconomy()
        {
            Require(admitted != null && admitted.Source.IsCreated, Prefix + "_SOURCE_UNAVAILABLE");
            var source = admitted!.Source;
            var entities = source.EntityManager;
            Require(ReferenceEquals(simulation, source.GetExistingSystemManaged<SimulationSystem>())
                && simulation!.frameIndex == SimulationFrame && entities.Exists(economyEntity)
                && entities.HasComponent<EconomyParameterData>(economyEntity)
                && entities.GetComponentData<EconomyParameterData>(economyEntity).Equals(EconomyParameters),
                Prefix + "_ECONOMY_CHANGED");
        }

        internal static bool SpatialMethodsQualified()
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var methods = new List<MethodBase>();
            if (!AddIterator(typeof(GenerateObjectsSystem), "FillCreationListJob")
                || !AddIterator(typeof(GenerateNodesSystem), "FillNodeMapJob")) return false;
            var search = typeof(Game.Net.SearchSystem);
            var getTree = search.GetMethod("GetNetSearchTree", instance, null,
                new[] { typeof(bool), typeof(JobHandle).MakeByRefType() }, null);
            var addReader = search.GetMethod("AddNetSearchTreeReader", instance, null, new[] { typeof(JobHandle) }, null);
            if (getTree == null || addReader == null || addReader.ReturnType != typeof(void)) return false;
            methods.Add(getTree);
            methods.Add(addReader);
            return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "RELOCATION_GENERATION_SPATIAL").Qualified;

            bool AddIterator(Type owner, string job)
            {
                var iterator = owner.GetNestedType(job, BindingFlags.NonPublic)?.GetNestedType("EdgeIterator", BindingFlags.NonPublic);
                if (iterator == null || !iterator.IsValueType) return false;
                var declared = iterator.GetMethods(instance);
                if (declared.Length == 0) return false;
                methods.AddRange(declared);
                return true;
            }
        }

        private static void RequireShape(RelocationDefinitionPayload payload)
        {
            var original = payload.Original;
            var root = payload.RootObject;
            var oldCourse = payload.OldNodeCourse;
            var newCourse = payload.NewNodeCourse;
            var owner = payload.NewNodeOwner;
            Require(DefinitionMatches(payload.RootCreation, original.Prefab, original.Root, CreationFlags.Relocate)
                && DefinitionMatches(payload.OldNodeCreation, Entity.Null, original.NodeEntity, CreationFlags.Delete | CreationFlags.Hidden)
                && DefinitionMatches(payload.NewNodeCreation, original.NodePrefab, Entity.Null, default)
                && root.m_ParentMesh == -1 && root.m_Elevation == 0f
                && math.all(math.isfinite(root.m_Position)) && root.m_Rotation.Equals(original.RootTransform.m_Rotation)
                && original.AcceptsSite(root.m_Position.xz)
                && owner.m_Prefab == original.Prefab && owner.m_Position.Equals(root.m_Position) && owner.m_Rotation.Equals(root.m_Rotation)
                && SinglePointCourse(oldCourse, original.NodeEntity, default)
                && oldCourse.m_StartPosition.m_Position.Equals(original.NodePose.m_Position)
                && SinglePointCourse(newCourse, Entity.Null, CoursePosFlags.IsFirst | CoursePosFlags.IsLast),
                Prefix + "_PAYLOAD_UNQUALIFIED");
        }

        private static bool SinglePointCourse(NetCourse course, Entity endpoint, CoursePosFlags flags)
        {
            var start = course.m_StartPosition;
            var end = course.m_EndPosition;
            return start.m_Entity == endpoint && end.m_Entity == endpoint && start.m_Flags == flags && end.m_Flags == flags
                && start.m_Position.Equals(end.m_Position) && math.all(math.isfinite(start.m_Position))
                && math.all(math.isfinite(start.m_Elevation)) && math.all(math.isfinite(end.m_Elevation));
        }

        private static bool DefinitionMatches(CreationDefinition value, Entity prefab, Entity original, CreationFlags flags) =>
            value.m_Prefab == prefab && value.m_Original == original && value.m_Flags == flags
            && value.m_SubPrefab == Entity.Null && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null;

        private static bool ActivePrefab(EntityManager entities, Entity prefab) => entities.Exists(prefab)
            && entities.HasComponent<PrefabData>(prefab) && entities.IsComponentEnabled<PrefabData>(prefab)
            && !entities.HasComponent<Deleted>(prefab);

        private static bool BoundedArchetype(EntityArchetype value) => value.Valid && value.TypesCount > 0 && value.TypesCount <= 256;

        internal JObject Snapshot() => new JObject {
            ["passed"] = admitted != null, ["failureCode"] = failureCode,
            ["objectCommandUpperBound"] = ObjectCommandUpperBound, ["nodeCommandUpperBound"] = NodeCommandUpperBound,
            ["simulationFrame"] = admitted == null ? JValue.CreateNull() : new JValue(SimulationFrame),
            ["old"] = SearchSnapshot(oldSearch), ["new"] = SearchSnapshot(newSearch)
        };

        private static JObject SearchSnapshot(ObjectGenerationPreflight.CourseSearch search) => new JObject {
            ["writersCompleted"] = search.WritersCompleted, ["completed"] = search.Completed,
            ["nodesVisited"] = search.Nodes, ["itemsVisited"] = search.Items,
            ["ignoredHits"] = search.IgnoredHits, ["foreignHit"] = search.Hit
        };

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

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
