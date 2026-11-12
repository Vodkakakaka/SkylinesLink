#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using NativeReferencesSystem = Game.Net.ReferencesSystem;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Runs the native empty-edge branch for one scoped new node.</summary>
    [DisableAutoCreation]
    public sealed partial class NodeReferencesProducer : NativeReferencesSystem
    {
        private int actor, registeredCount, nodeCount, nodeChunks, edgeCount, tempEdgeCount;
        private bool configured, bindingsVerified, registeredExactly, inputsBound, requested, armed, emptyArmed;
        private bool emptyStarted, emptyReturned, emptyCompleted, emptyPassed, emptyShouldRun;
        private bool nativeStarted, nativeReturned, completed, joinFailed, checksPassed;
        private bool nodeIgnoredEmpty, edgeIgnoredEmpty, tempEdgeIgnoredEmpty, shouldRun, zeroEdgesBefore, zeroEdgesAfter;
        private Entity root, node;
        private EntityQuery nodes, edges, tempEdges;
        private Bindings? fields;
        private Action? currentScope;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeCallStarted => nativeStarted;
        internal bool NativeWorkMayReadSource => emptyStarted && !emptyCompleted || nativeStarted && !completed;
        // Only SystemState owns native query storage; this adapter allocates no
        // independent container. Physical registration has a separate receipt.
        internal bool StorageReleased => true;
        internal bool ResourcesReleased => DestructionReturned;
        internal bool ChecksPassed => checksPassed && completed && zeroEdgesAfter && failureCode == null;

        internal void Configure(int actor)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0, "NODE_REFERENCES_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            Require(configured && fields != null, "NODE_REFERENCES_CONFIGURATION_MISSING");
            // Keep each native All/Any/None role intact. Only this actor's
            // Disabled scope is added; no default query or filter is replaced.
            edges = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            nodes = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                    ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            tempEdges = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                None = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            var scope = new ObjectGenerationScope { Value = actor };
            edges.SetSharedComponentFilter(scope); nodes.SetSharedComponentFilter(scope); tempEdges.SetSharedComponentFilter(scope);
            fields!.Edges.SetValue(this, edges); fields.Nodes.SetValue(this, nodes); fields.TempEdges.SetValue(this, tempEdges);
            // Native ReferencesSystem has no required query. Its inherited
            // compiler hook still initializes the complete native TypeHandle.
            ObserveBindings(null);
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !inputsBound && !emptyStarted,
                "NODE_REFERENCES_EMPTY_REJECTED");
            try
            {
                RequireEmpty(requireCurrentScope);
                emptyShouldRun = ShouldRunSystem(); Guard(requireCurrentScope);
                Require(emptyShouldRun, "NODE_REFERENCES_EMPTY_GATE_UNEXPECTED");
                currentScope = requireCurrentScope; emptyArmed = true; Enabled = true;
                try
                {
                    Update();
                    Require(emptyReturned, "NODE_REFERENCES_EMPTY_NATIVE_NOT_RETURNED");
                    JoinOwnedDependencies();
                }
                finally { emptyArmed = false; Enabled = false; currentScope = null; }
                RequireEmpty(requireCurrentScope);
                Require(emptyCompleted && OnUpdateEntries == 1, "NODE_REFERENCES_EMPTY_NOT_CONFIRMED");
                emptyPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void BindInputs(Entity root, Entity node)
        {
            RequireMainThread();
            Require(CreationReturned && emptyPassed && !DestructionStarted && !inputsBound && !requested
                && root != Entity.Null && node != Entity.Null && root.Index >= 0 && node.Index >= 0 && root != node,
                "NODE_REFERENCES_INPUT_BIND_REJECTED");
            this.root = root; this.node = node; inputsBound = true;
        }

        internal void UpdateOnce(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && emptyPassed && inputsBound && !DestructionStarted && !requested && failureCode == null,
                "NODE_REFERENCES_UPDATE_REJECTED");
            requested = true;
            try
            {
                QualifyInputs(requireCurrentScope);
                zeroEdgesBefore = true;
                currentScope = requireCurrentScope; armed = true; Enabled = true;
                try
                {
                    Update();
                    Require(nativeReturned, "NODE_REFERENCES_NATIVE_NOT_RETURNED");
                    JoinOwnedDependencies();
                }
                finally { armed = false; Enabled = false; currentScope = null; }
                QualifyInputs(requireCurrentScope);
                zeroEdgesAfter = true;
                Require(completed && OnUpdateEntries == 2, "NODE_REFERENCES_COMPLETION_NOT_CONFIRMED");
                checksPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        private void RequireEmpty(Action scope)
        {
            Guard(scope); ObserveBindings(scope); ReadQueryFacts(scope);
            Require(nodeCount == 0 && edgeCount == 0 && tempEdgeCount == 0
                && nodeIgnoredEmpty && edgeIgnoredEmpty && tempEdgeIgnoredEmpty, "NODE_REFERENCES_NOT_EMPTY");
        }

        private void QualifyInputs(Action scope)
        {
            Guard(scope); ObserveBindings(scope);
            EntityManager entities = EntityManager; Guard(scope);
            RequireScoped(entities, root, scope); RequireScoped(entities, node, scope);
            Require(Has<Node>(entities, node, scope) && Has<Updated>(entities, node, scope)
                && !Has<Deleted>(entities, node, scope) && Has<Owner>(entities, node, scope), "NODE_REFERENCES_NODE_ROLE_CHANGED");
            Require(Read<Owner>(entities, node, scope).m_Owner == root, "NODE_REFERENCES_OWNER_CHANGED");
            bool hasEdges = entities.HasBuffer<ConnectedEdge>(node); Guard(scope);
            Require(hasEdges, "NODE_REFERENCES_CONNECTED_EDGES_MISSING");
            DynamicBuffer<ConnectedEdge> connected = entities.GetBuffer<ConnectedEdge>(node, true); Guard(scope);
            int length = connected.Length; Guard(scope);
            Require(length == 0, "NODE_REFERENCES_CONNECTED_EDGES_NOT_EMPTY");
            ReadQueryFacts(scope);
            Require(nodeCount == 1 && nodeChunks == 1 && !nodeIgnoredEmpty && edgeCount == 0 && tempEdgeCount == 0
                && edgeIgnoredEmpty && tempEdgeIgnoredEmpty, "NODE_REFERENCES_QUERY_BUDGET");
            Entity onlyNode = nodes.GetSingletonEntity(); Guard(scope);
            Require(onlyNode == node, "NODE_REFERENCES_FOREIGN_QUERY_MEMBER");
            shouldRun = ShouldRunSystem(); Guard(scope);
            Require(shouldRun, "NODE_REFERENCES_NATIVE_GATE_CHANGED");
        }

        private void RequireScoped(EntityManager entities, Entity entity, Action scope)
        {
            bool exists = entities.Exists(entity); Guard(scope);
            Require(exists, "NODE_REFERENCES_SOURCE_SCOPE_CHANGED");
            bool scopePresent = entities.HasComponent<ObjectGenerationScope>(entity); Guard(scope);
            Require(scopePresent && Has<Unity.Entities.Disabled>(entities, entity, scope) && Has<Temp>(entities, entity, scope)
                && !Has<Deleted>(entities, entity, scope), "NODE_REFERENCES_SOURCE_SCOPE_CHANGED");
            int value = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value; Guard(scope);
            Require(value == actor, "NODE_REFERENCES_ACTOR_CHANGED");
            Temp temp = Read<Temp>(entities, entity, scope);
            Require(temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0, "NODE_REFERENCES_NOT_NEW_TEMP");
        }

        private void ReadQueryFacts(Action scope)
        {
            nodeCount = nodes.CalculateEntityCount(); Guard(scope);
            nodeChunks = nodes.CalculateChunkCount(); Guard(scope);
            edgeCount = edges.CalculateEntityCount(); Guard(scope);
            tempEdgeCount = tempEdges.CalculateEntityCount(); Guard(scope);
            nodeIgnoredEmpty = nodes.IsEmptyIgnoreFilter; Guard(scope);
            edgeIgnoredEmpty = edges.IsEmptyIgnoreFilter; Guard(scope);
            tempEdgeIgnoredEmpty = tempEdges.IsEmptyIgnoreFilter; Guard(scope);
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            var scope = currentScope ?? throw Failure("NODE_REFERENCES_SCOPE_CALLBACK_MISSING");
            if (emptyArmed)
            {
                Require(!emptyStarted && !nativeStarted, "NODE_REFERENCES_EMPTY_ALREADY_STARTED");
                RequireEmpty(scope);
                emptyStarted = true; base.OnUpdate(); emptyReturned = true;
                return;
            }
            Require(armed && !nativeStarted, "NODE_REFERENCES_NOT_ARMED");
            // Recheck after SystemBase.BeforeOnUpdate and before the real job.
            QualifyInputs(scope);
            nativeStarted = true; base.OnUpdate(); nativeReturned = true;
        }

        private void JoinOwnedDependencies()
        {
            // A throw before native OnUpdate returned may have scheduled a job
            // without publishing its handle. A default Dependency is no fence.
            Require(!joinFailed && (!emptyStarted || emptyReturned) && (!nativeStarted || nativeReturned),
                "NODE_REFERENCES_JOIN_UNRESOLVED");
            try
            {
                Dependency.Complete();
                if (emptyReturned) emptyCompleted = true;
                if (nativeReturned) completed = true;
            }
            catch { joinFailed = true; throw; }
        }

        private void ObserveBindings(Action? scope)
        {
            Require(!DestructionStarted && !NativeWorkMayReadSource && fields != null, "NODE_REFERENCES_BINDINGS_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)fields!.Edges.GetValue(this)!).Equals(edges)
                && ((EntityQuery)fields.Nodes.GetValue(this)!).Equals(nodes)
                && ((EntityQuery)fields.TempEdges.GetValue(this)!).Equals(tempEdges);
            EntityQuery[] values = EntityQueries;
            if (scope != null) Guard(scope);
            registeredCount = values.Length;
            registeredExactly = registeredCount == 3 && Contains(values, edges) && Contains(values, nodes) && Contains(values, tempEdges);
            bool edgeFilter = edges.HasFilter(); if (scope != null) Guard(scope);
            bool nodeFilter = nodes.HasFilter(); if (scope != null) Guard(scope);
            bool tempEdgeFilter = tempEdges.HasFilter(); if (scope != null) Guard(scope);
            Require(bindingsVerified && registeredExactly && edgeFilter && nodeFilter && tempEdgeFilter,
                "NODE_REFERENCES_BINDINGS_CHANGED");
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource, "NODE_REFERENCES_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // Queries belong to SystemState. No unmatched Game lifecycle, ECB,
            // map or default-system resource is disposed by this adapter.
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["emptyPassed"] = emptyPassed, ["emptyShouldRun"] = emptyShouldRun,
            ["emptyNativeStarted"] = emptyStarted, ["emptyNativeReturned"] = emptyReturned, ["emptyJoined"] = emptyCompleted,
            ["emptyAdmittedBranches"] = emptyPassed ? new JValue(0) : JValue.CreateNull(),
            ["inputsBound"] = inputsBound, ["root"] = Id(root), ["node"] = Id(node),
            ["bindingsVerified"] = bindingsVerified, ["registeredExactly"] = registeredExactly, ["registeredCount"] = registeredCount,
            ["nodeCount"] = nodeCount, ["nodeChunks"] = nodeChunks, ["edgeCount"] = edgeCount, ["tempEdgeCount"] = tempEdgeCount,
            ["nodeIgnoredEmpty"] = nodeIgnoredEmpty, ["edgeIgnoredEmpty"] = edgeIgnoredEmpty, ["tempEdgeIgnoredEmpty"] = tempEdgeIgnoredEmpty,
            ["shouldRun"] = shouldRun, ["zeroEdgesBefore"] = zeroEdgesBefore, ["zeroEdgesAfter"] = zeroEdgesAfter,
            ["onUpdateEntries"] = OnUpdateEntries, ["nativeStarted"] = nativeStarted, ["nativeReturned"] = nativeReturned,
            ["jobJoined"] = completed, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["checksPassed"] = ChecksPassed, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
            ["scope"] = "single-new-node-empty-connected-edges"
        };

        private void Guard(Action scope)
        { scope(); Require(World != null && World.IsCreated && !DestructionStarted, "NODE_REFERENCES_SOURCE_UNAVAILABLE"); }
        private bool Has<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { bool value = entities.HasComponent<T>(entity); Guard(scope); return value; }
        private T Read<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { T value = entities.GetComponentData<T>(entity); Guard(scope); return value; }
        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection value ? value.Code : "NODE_REFERENCES_FAILED"; exceptionType ??= error.GetType().Name; }
        private static bool Contains(EntityQuery[] values, EntityQuery expected)
        { foreach (EntityQuery value in values) if (value.Equals(expected)) return true; return false; }
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NODE_REFERENCES_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Edges, Nodes, TempEdges;
            internal Bindings()
            {
                Require(NativeConstructionBuild.Game(typeof(NativeReferencesSystem).Module)
                    && NativeConstructionBuild.Entities(typeof(World).Module),
                    "NODE_REFERENCES_NATIVE_BUILD_UNQUALIFIED");
                Edges = Field("m_EdgeQuery"); Nodes = Field("m_NodeQuery"); TempEdges = Field("m_TempEdgeQuery");
            }
            private static FieldInfo Field(string name)
            {
                FieldInfo? value = typeof(NativeReferencesSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(NativeReferencesSystem) && value.FieldType == typeof(EntityQuery)
                    && !value.IsStatic && !value.IsInitOnly, "NODE_REFERENCES_FIELD_ABI_UNQUALIFIED");
                return value!;
            }
        }
    }
}
#endif
