#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class NodeCostProducer : CostSystem
    {
        private Bindings? bindings;
        private ToolSystem? tools;
        private SimulationSystem? simulation;
        private EntityQuery nodes, economy;
        private Entity node;
        private GeneratedSubObjectsPayload? relocation;
        private Action? currentScope;
        private (Temp NewNode, Temp? OldNode) before;
        private int actor, queryCount, queryChunks, economyCount;
        private bool creationStarted, destructionStarted, requested;
        private bool nativeEntered, nativeReturned, nativeJoined, tempUnchanged, patchQualified;
        private string? failureCode;

        internal bool CreationStarted => creationStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool NativeWorkMayReadSource => nativeEntered && !nativeJoined;
        internal bool StorageReleased => true;
        internal bool CostPassed { get; private set; }

        internal void Configure(int actor)
        {
            RequireMainThread();
            Require(bindings == null && !creationStarted && World == null && actor > 0,
                "NODE_COST_CONFIGURATION_REJECTED");
            RequireUnpatched();
            bindings = new Bindings();
            this.actor = actor;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            RequireMainThread();
            Enabled = false;
            var fields = bindings ?? throw new Rejection("NODE_COST_CONFIGURATION_MISSING");
            tools = World.GetExistingSystemManaged<ToolSystem>();
            simulation = World.GetExistingSystemManaged<SimulationSystem>();
            Require(tools != null && simulation != null, "NODE_COST_SOURCE_DEPENDENCY_MISSING");
            nodes = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Node>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            nodes.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            economy = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            RequireForUpdate(nodes);
            RequireForUpdate(economy);
            fields.Tools.SetValue(this, tools);
            fields.Simulation.SetValue(this, simulation);
            fields.Nodes.SetValue(this, nodes);
            fields.Economy.SetValue(this, economy);
            RequireBindings();
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "NODE_COST_RELEASE_UNRESOLVED");
            DestructionReturned = true;
        }

        internal void CalculateRelocationOnce(GeneratedSubObjectsPayload cohort, Action requireCurrentScope)
        {
            Require(cohort.IsRelocation && ReferenceEquals(cohort.Source, World) && cohort.Actor == actor,
                "NODE_COST_RELOCATION_COHORT_CHANGED");
            relocation = cohort;
            CalculateOnce(cohort.Node, requireCurrentScope);
        }

        internal void CalculateOnce(Entity node, Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && !destructionStarted && !requested && node != Entity.Null && node.Index >= 0,
                "NODE_COST_CALCULATION_REJECTED");
            requested = true;
            this.node = node;
            currentScope = requireCurrentScope;
            try
            {
                RequireInput(requireCurrentScope);
                Require(ShouldRunSystem(), "NODE_COST_REQUIRED_QUERY_REJECTED");
                Guard(requireCurrentScope);
                Enabled = true;
                Update();
                Require(nativeEntered && nativeReturned, "NODE_COST_NATIVE_NOT_RETURNED");
                var handle = Dependency;
                Require(!handle.Equals(default(JobHandle)), "NODE_COST_NATIVE_JOB_UNCONFIRMED");
                handle.Complete();
                nativeJoined = true;
                Guard(requireCurrentScope);
                tempUnchanged = RequireInput(requireCurrentScope).Equals(before);
                Require(tempUnchanged, "NODE_COST_TEMP_CHANGED");
                CostPassed = true;
            }
            catch (Exception error)
            {
                failureCode ??= error is Rejection rejected ? rejected.Code : "NODE_COST_CALCULATION_FAILED";
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
            var scope = currentScope ?? throw new Rejection("NODE_COST_SCOPE_MISSING");
            Require(requested && !nativeEntered, "NODE_COST_NATIVE_REPEATED");
            RequireUnpatched();
            before = RequireInput(scope);
            nativeEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private (Temp NewNode, Temp? OldNode) RequireInput(Action scope)
        {
            Guard(scope);
            RequireBindings();
            Require(tools!.actionMode == GameMode.Game, "NODE_COST_GAME_MODE_CHANGED");
            queryCount = nodes.CalculateEntityCount();
            queryChunks = nodes.CalculateChunkCount();
            economyCount = economy.CalculateEntityCount();
            int expected = relocation == null ? 1 : 2;
            Require(economyCount == 1 && queryCount == expected && queryChunks == expected, "NODE_COST_QUERY_UNQUALIFIED");
            using (var matched = nodes.ToEntityArray(Unity.Collections.Allocator.Temp))
                Require(relocation == null ? matched.Length == 1 && matched[0] == node
                    : matched.Length == 2 && (matched[0] == node && matched[1] == relocation.OldNode
                        || matched[1] == node && matched[0] == relocation.OldNode), "NODE_COST_QUERY_MEMBERS_CHANGED");

            Temp next = ReadNonEssentialNode(node);
            Require(next.m_Original == Entity.Null && next.m_Flags == (TempFlags.Create | TempFlags.IsLast),
                "NODE_COST_TEMP_BRANCH_UNQUALIFIED");
            Temp? old = null;
            if (relocation != null)
            {
                old = ReadNonEssentialNode(relocation.OldNode);
                Require(next.Equals(relocation.CapturedParentTemp(node)) && old.Value.Equals(relocation.CapturedParentTemp(relocation.OldNode))
                    && old.Value.m_Original != Entity.Null && old.Value.m_Flags == (TempFlags.Delete | TempFlags.Hidden),
                    "NODE_COST_RELOCATION_TEMP_CHANGED");
            }
            Guard(scope);
            return (next, old);
        }

        private Temp ReadNonEssentialNode(Entity target)
        {
            var entities = EntityManager;
            Require(entities.GetChunk(target).Count == 1 && entities.HasComponent<Node>(target)
                && !entities.HasComponent<Edge>(target) && !entities.HasComponent<Deleted>(target)
                && entities.GetSharedComponent<ObjectGenerationScope>(target).Value == actor,
                "NODE_COST_NODE_ROLE_CHANGED");
            Temp value = entities.GetComponentData<Temp>(target);
            // Native CostSystem clears both fields before the Essential branch.
            Require((value.m_Flags & TempFlags.Essential) == 0 && value.m_Cost == 0 && value.m_Value == 0,
                "NODE_COST_TEMP_BRANCH_UNQUALIFIED");
            return value;
        }

        private void RequireBindings()
        {
            var fields = bindings ?? throw new Rejection("NODE_COST_CONFIGURATION_MISSING");
            var queries = EntityQueries;
            Require(queries.Length == 2 && !nodes.Equals(economy)
                && (queries[0].Equals(nodes) && queries[1].Equals(economy)
                    || queries[1].Equals(nodes) && queries[0].Equals(economy))
                && ((EntityQuery)fields.Nodes.GetValue(this)!).Equals(nodes)
                && ((EntityQuery)fields.Economy.GetValue(this)!).Equals(economy)
                && nodes.HasFilter() && !economy.HasFilter()
                && ReferenceEquals(fields.Tools.GetValue(this), tools)
                && ReferenceEquals(fields.Simulation.GetValue(this), simulation)
                && ReferenceEquals(tools!.World, World) && ReferenceEquals(simulation!.World, World)
                && ReferenceEquals(World.GetExistingSystemManaged<ToolSystem>(), tools)
                && ReferenceEquals(World.GetExistingSystemManaged<SimulationSystem>(), simulation),
                "NODE_COST_BINDINGS_CHANGED");
        }

        private void RequireUnpatched()
        {
            var result = NodeCostPatchProbe.Observe();
            patchQualified = result.Qualified;
            Require(patchQualified, result.Reason);
        }

        private void Guard(Action scope)
        {
            scope();
            Require(World != null && World.IsCreated && !destructionStarted, "NODE_COST_SOURCE_UNAVAILABLE");
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = CostPassed, ["nativeEntered"] = nativeEntered, ["nativeReturned"] = nativeReturned,
            ["nativeJoined"] = nativeJoined, ["tempUnchanged"] = tempUnchanged, ["patchQualified"] = patchQualified,
            ["queryCount"] = queryCount, ["queryChunks"] = queryChunks, ["economyCount"] = economyCount,
            ["creationReturned"] = CreationReturned, ["destructionReturned"] = DestructionReturned,
            ["failureCode"] = failureCode
        };

        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NODE_COST_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        private sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Tools, Simulation, Nodes, Economy;

            internal Bindings()
            {
                Require(typeof(CostSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "NODE_COST_NATIVE_BUILD_UNQUALIFIED");
                Tools = Field("m_ToolSystem", typeof(ToolSystem));
                Simulation = Field("m_SimulationSystem", typeof(SimulationSystem));
                Nodes = Field("m_UpdatedTempNetQuery", typeof(EntityQuery));
                Economy = Field("m_EconomyParameterQuery", typeof(EntityQuery));
            }

            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(CostSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(field != null && field.FieldType == type && !field.IsInitOnly, "NODE_COST_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }
}
#endif
