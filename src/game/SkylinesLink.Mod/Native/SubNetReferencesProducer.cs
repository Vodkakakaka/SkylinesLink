#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using NativeReferencesSystem = Game.Net.SubNetReferencesSystem;

namespace SkylinesLink.Mod.Native
{
    // Owns the native reference updates and a live root.SubNet observation port.
    // Node/root physical ownership and modification-marker cleanup stay outside.
    [DisableAutoCreation]
    public sealed partial class SubNetReferencesProducer : NativeReferencesSystem
    {
        private int actor, registeredCount, queryCount, queryChunks, arraysAttempted, arraysAcquired, arraysDisposed;
        private bool configured, bindingsVerified, registeredExactly, emptyPassed, emptyShouldRun, bound;
        private bool linkRequested, unlinkRequested, linkArmed, unlinkArmed;
        private bool linkStarted, linkReturned, linkJoined, linkPassed, unlinkStarted, unlinkReturned, unlinkJoined, unlinkPassed;
        private bool joinFailed, graphRejected, rootPresent, nodePresent, rootPresenceObserved, nodePresenceObserved, rootBufferObserved;
        private int rootBufferLength = -1;
        private Entity root, node;
        private Entity[] latestEntries = Array.Empty<Entity>(), linkedEntries = Array.Empty<Entity>(), unlinkedEntries = Array.Empty<Entity>();
        private Entity[] queryMembers = Array.Empty<Entity>();
        private EntityQuery subNets;
        private World? source;
        private Bindings? fields;
        private Action? boundScope, currentScope;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeCallStarted => linkStarted || unlinkStarted;
        internal bool NativeWorkMayReadSource => linkStarted && !linkJoined || unlinkStarted && !unlinkJoined;
        internal bool StorageReleased => arraysAttempted == arraysAcquired && arraysAcquired == arraysDisposed;
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;
        internal bool LinkPassed => linkPassed;
        internal bool UnlinkPassed => unlinkPassed;
        internal bool ChecksPassed => emptyPassed && linkPassed && unlinkPassed && !graphRejected
            && !NativeWorkMayReadSource && failureCode == null;

        internal void Configure(int actor)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0, "SUBNET_REFERENCES_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            Require(configured && fields != null, "SUBNET_REFERENCES_CONFIGURATION_MISSING");
            // Preserve both native OR branches. Created must continue to win
            // inside the native kernel when an entity also carries Deleted.
            subNets = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Edge>() },
                Options = EntityQueryOptions.IncludeDisabledEntities }, new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Edge>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            subNets.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            fields!.SubNets.SetValue(this, subNets);
            RequireForUpdate(subNets);
            // Only SystemState owns query storage. The real inherited compiler
            // hook initializes the native handles without a Game registration.
            ObserveBindings(null);
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !bound && !NativeCallStarted,
                "SUBNET_REFERENCES_EMPTY_REJECTED");
            try
            {
                Guard(scope); ObserveBindings(scope); ReadQueryCounts(scope);
                Require(queryCount == 0, "SUBNET_REFERENCES_NOT_EMPTY");
                emptyShouldRun = ShouldRunSystem(); Guard(scope);
                Require(!emptyShouldRun, "SUBNET_REFERENCES_EMPTY_GATE_CHANGED");
                currentScope = scope; Enabled = true;
                try { Update(); }
                finally { Enabled = false; currentScope = null; }
                Guard(scope); ReadQueryCounts(scope);
                Require(queryCount == 0 && OnUpdateEntries == 0 && !NativeCallStarted,
                    "SUBNET_REFERENCES_EMPTY_NOT_SKIPPED");
                emptyPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void Bind(Entity root, Entity node, Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && emptyPassed && !bound && !DestructionStarted
                && root != Entity.Null && node != Entity.Null && root != node && root.Index >= 0 && node.Index >= 0,
                "SUBNET_REFERENCES_BIND_REJECTED");
            Guard(scope); source = World; this.root = root; this.node = node; boundScope = scope; bound = true;
            RequireEmptyGraph();
        }

        internal void LinkOnce(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(bound && !linkRequested && !unlinkRequested && !DestructionStarted, "SUBNET_REFERENCES_LINK_REJECTED");
            linkRequested = true;
            try
            {
                QualifyInputs(scope, false);
                RunUpdate(scope, false);
                ObserveGraph(scope, true);
                linkedEntries = (Entity[])latestEntries.Clone();
                Require(linkJoined && OnUpdateEntries == 1, "SUBNET_REFERENCES_LINK_UNCONFIRMED");
                linkPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void UnlinkOnce(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(bound && linkPassed && !unlinkRequested && !DestructionStarted, "SUBNET_REFERENCES_UNLINK_REJECTED");
            unlinkRequested = true;
            try
            {
                QualifyInputs(scope, true);
                RunUpdate(scope, true);
                ObserveGraph(scope, false);
                unlinkedEntries = (Entity[])latestEntries.Clone();
                Require(unlinkJoined && OnUpdateEntries == 2, "SUBNET_REFERENCES_UNLINK_UNCONFIRMED");
                unlinkPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        // The graph port reads current source facts, not a cached permission.
        // A never-started link or known unlink permits later node/root absence
        // during exact cleanup. A rejected request is not a native invocation.
        internal void RequireCurrentGraph()
        {
            RequireMainThread(); RequireGraphPort();
            ObserveGraph(boundScope!, linkPassed && !unlinkPassed);
        }

        internal void RequireLinkedGraph()
        {
            RequireMainThread();
            Require(linkPassed && !unlinkStarted && !unlinkPassed, "SUBNET_REFERENCES_NOT_LINKED");
            RequireCurrentGraph();
        }

        internal void RequireEmptyGraph()
        {
            RequireMainThread(); RequireGraphPort();
            Require(!linkStarted || unlinkPassed, "SUBNET_REFERENCES_NOT_UNLINKED");
            ObserveGraph(boundScope!, false);
        }

        private void RequireGraphPort()
        {
            Require(bound && boundScope != null && !graphRejected && !NativeWorkMayReadSource,
                "SUBNET_REFERENCES_GRAPH_UNAVAILABLE");
            Require((!linkStarted || linkPassed) && (!unlinkStarted || unlinkPassed), "SUBNET_REFERENCES_GRAPH_UNCONFIRMED");
        }

        private void RunUpdate(Action scope, bool unlink)
        {
            currentScope = scope; linkArmed = !unlink; unlinkArmed = unlink; Enabled = true;
            try
            {
                Update();
                Require(unlink ? unlinkReturned : linkReturned, "SUBNET_REFERENCES_NATIVE_NOT_RETURNED");
                JoinOwnedDependencies();
            }
            finally { Enabled = false; linkArmed = false; unlinkArmed = false; currentScope = null; }
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Action scope = currentScope ?? throw Failure("SUBNET_REFERENCES_SCOPE_CALLBACK_MISSING");
            Require(linkArmed != unlinkArmed && (linkArmed ? !linkStarted : !unlinkStarted), "SUBNET_REFERENCES_NOT_ARMED");
            // Recheck after the actual SystemBase dependency/handle refresh.
            QualifyInputs(scope, unlinkArmed);
            if (unlinkArmed) { unlinkStarted = true; base.OnUpdate(); unlinkReturned = true; }
            else { linkStarted = true; base.OnUpdate(); linkReturned = true; }
        }

        private void QualifyInputs(Action scope, bool unlink)
        {
            Guard(scope); ObserveBindings(scope);
            if (unlink) RequireLinkedGraph(); else RequireEmptyGraph();
            EntityManager entities = EntityManager; Guard(scope);
            RequireLiveRoot(entities, scope); RequireLiveNode(entities, scope);
            Require(Has<Created>(entities, node, scope) == !unlink && Has<Deleted>(entities, node, scope) == unlink,
                "SUBNET_REFERENCES_NODE_MARKERS_CHANGED");
            ReadQueryCounts(scope);
            Require(queryCount == 1 && queryChunks == 1, "SUBNET_REFERENCES_QUERY_BUDGET");
            arraysAttempted++;
            NativeArray<Entity> values = subNets.ToEntityArray(Allocator.Temp); arraysAcquired++;
            try
            {
                Guard(scope); Require(values.Length == 1, "SUBNET_REFERENCES_QUERY_COUNT_CHANGED");
                queryMembers = new[] { values[0] };
                Require(values[0] == node, "SUBNET_REFERENCES_FOREIGN_QUERY_MEMBER");
            }
            finally { values.Dispose(); arraysDisposed++; }
            Guard(scope);
            bool shouldRun = ShouldRunSystem(); Guard(scope);
            Require(shouldRun, "SUBNET_REFERENCES_NATIVE_GATE_CHANGED");
        }

        private void ObserveGraph(Action scope, bool linked)
        {
            rootPresent = nodePresent = rootPresenceObserved = nodePresenceObserved = rootBufferObserved = false;
            rootBufferLength = -1; latestEntries = Array.Empty<Entity>();
            try
            {
                Guard(scope); Require(!NativeWorkMayReadSource, "SUBNET_REFERENCES_GRAPH_NATIVE_UNKNOWN");
                EntityManager entities = EntityManager; Guard(scope);
                rootPresent = entities.Exists(root); Guard(scope); rootPresenceObserved = true;
                nodePresent = entities.Exists(node); Guard(scope); nodePresenceObserved = true;
                bool knownEmpty = !linkStarted || unlinkPassed;
                if (!rootPresent)
                {
                    // World validity was checked above. This is exact source
                    // absence in a known-empty prefix or after unlink, not a
                    // missing-World fallback or an unresolved native call.
                    Require(!linked && knownEmpty && !nodePresent, "SUBNET_REFERENCES_ROOT_ABSENCE_UNQUALIFIED");
                    return;
                }
                RequireLiveRoot(entities, scope);
                if (nodePresent) RequireLiveNode(entities, scope, allowUnresolvedOwner: !linkStarted);
                else Require(!linked && knownEmpty, "SUBNET_REFERENCES_NODE_ABSENCE_UNQUALIFIED");
                bool hasBuffer = entities.HasBuffer<SubNet>(root); Guard(scope);
                Require(hasBuffer, "SUBNET_REFERENCES_ROOT_BUFFER_MISSING");
                DynamicBuffer<SubNet> buffer = entities.GetBuffer<SubNet>(root, true); Guard(scope);
                rootBufferLength = buffer.Length; Guard(scope);
                Require(rootBufferLength >= 0 && rootBufferLength <= 1, "SUBNET_REFERENCES_ROOT_BUFFER_BUDGET");
                var actual = new Entity[rootBufferLength];
                for (int index = 0; index < actual.Length; index++) { actual[index] = buffer[index].m_SubNet; Guard(scope); }
                latestEntries = actual; rootBufferObserved = true;
                Require(linked ? actual.Length == 1 && actual[0] == node : actual.Length == 0,
                    "SUBNET_REFERENCES_GRAPH_CHANGED");
                RequireLiveRoot(entities, scope);
                if (nodePresent) RequireLiveNode(entities, scope, allowUnresolvedOwner: !linkStarted);
                Guard(scope);
            }
            catch (Exception error) { graphRejected = true; RecordFailure(error); throw; }
        }

        private void RequireLiveRoot(EntityManager entities, Action scope)
        {
            RequireScoped(entities, root, scope);
            Require(!Has<Owner>(entities, root, scope) && !Has<Deleted>(entities, root, scope), "SUBNET_REFERENCES_ROOT_ROLE_CHANGED");
        }

        private void RequireLiveNode(EntityManager entities, Action scope, bool allowUnresolvedOwner = false)
        {
            RequireScoped(entities, node, scope);
            Require(Has<Node>(entities, node, scope) && !Has<Edge>(entities, node, scope)
                && Has<Owner>(entities, node, scope), "SUBNET_REFERENCES_NODE_ROLE_CHANGED");
            Entity owner = Read<Owner>(entities, node, scope).m_Owner;
            // Early graph binding protects the birth/Resolver failure prefix.
            // Only that zero-graph prefix may still have the native null Owner;
            // actual Link/Unlink admission always uses the strict default.
            Require(owner == root || allowUnresolvedOwner && !linkStarted && owner == Entity.Null,
                "SUBNET_REFERENCES_NODE_OWNER_CHANGED");
        }

        private void RequireScoped(EntityManager entities, Entity entity, Action scope)
        {
            bool exists = entities.Exists(entity); Guard(scope);
            Require(exists, "SUBNET_REFERENCES_SOURCE_SCOPE_CHANGED");
            bool scoped = entities.HasComponent<ObjectGenerationScope>(entity); Guard(scope);
            Require(scoped && Has<Disabled>(entities, entity, scope) && Has<Temp>(entities, entity, scope),
                "SUBNET_REFERENCES_SOURCE_SCOPE_CHANGED");
            int actual = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value; Guard(scope);
            Require(actual == actor, "SUBNET_REFERENCES_ACTOR_CHANGED");
            Temp temp = Read<Temp>(entities, entity, scope);
            Require(temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0, "SUBNET_REFERENCES_NOT_NEW_TEMP");
        }

        private void ReadQueryCounts(Action scope)
        { queryCount = subNets.CalculateEntityCount(); Guard(scope); queryChunks = subNets.CalculateChunkCount(); Guard(scope); }

        private void ObserveBindings(Action? scope)
        {
            Require(!DestructionStarted && !NativeWorkMayReadSource && fields != null, "SUBNET_REFERENCES_BINDINGS_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)fields!.SubNets.GetValue(this)!).Equals(subNets);
            EntityQuery[] values = EntityQueries; if (scope != null) Guard(scope);
            registeredCount = values.Length; registeredExactly = registeredCount == 1 && values[0].Equals(subNets);
            bool filtered = subNets.HasFilter(); if (scope != null) Guard(scope);
            Require(bindingsVerified && registeredExactly && filtered, "SUBNET_REFERENCES_BINDINGS_CHANGED");
        }

        private void JoinOwnedDependencies()
        {
            // A throw before native return may hide a scheduled handle. Do not
            // let a default Dependency erase that unresolved source obligation.
            Require(!joinFailed && (!linkStarted || linkReturned) && (!unlinkStarted || unlinkReturned), "SUBNET_REFERENCES_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (linkReturned) linkJoined = true; if (unlinkReturned) unlinkJoined = true; }
            catch { joinFailed = true; throw; }
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource, "SUBNET_REFERENCES_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // SystemState disposes its own query. No unmatched Game lifecycle,
            // foreign buffer, persistent allocation or barrier is owned here.
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["storageReleased"] = StorageReleased, ["resourcesReleased"] = ResourcesReleased,
            ["emptyPassed"] = emptyPassed, ["emptyShouldRun"] = emptyShouldRun, ["bound"] = bound,
            ["root"] = Id(root), ["node"] = Id(node), ["registeredCount"] = registeredCount,
            ["registeredExactly"] = registeredExactly, ["bindingsVerified"] = bindingsVerified,
            ["queryCount"] = queryCount, ["queryChunks"] = queryChunks, ["queryMembers"] = Ids(queryMembers),
            ["queryArrays"] = new JObject { ["attempted"] = arraysAttempted, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
            ["onUpdateEntries"] = OnUpdateEntries,
            ["link"] = new JObject { ["requested"] = linkRequested, ["started"] = linkStarted, ["returned"] = linkReturned,
                ["joined"] = linkJoined, ["passed"] = linkPassed, ["entries"] = Ids(linkedEntries) },
            ["unlink"] = new JObject { ["requested"] = unlinkRequested, ["started"] = unlinkStarted, ["returned"] = unlinkReturned,
                ["joined"] = unlinkJoined, ["passed"] = unlinkPassed, ["entries"] = Ids(unlinkedEntries) },
            ["latestGraph"] = new JObject { ["rootPresent"] = rootPresenceObserved ? new JValue(rootPresent) : JValue.CreateNull(),
                ["nodePresent"] = nodePresenceObserved ? new JValue(nodePresent) : JValue.CreateNull(),
                ["bufferObserved"] = rootBufferObserved, ["length"] = rootBufferLength, ["entries"] = Ids(latestEntries), ["rejected"] = graphRejected },
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["checksPassed"] = ChecksPassed, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
            ["scope"] = "one-owned-node-native-subnet-link-and-noncreated-deleted-unlink-only"
        };

        private void Guard(Action scope)
        {
            scope(); if (boundScope != null && !ReferenceEquals(scope, boundScope)) boundScope();
            Require(World != null && World.IsCreated && !DestructionStarted && (!bound || ReferenceEquals(World, source)),
                "SUBNET_REFERENCES_SOURCE_UNAVAILABLE");
        }
        private bool Has<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { bool value = entities.HasComponent<T>(entity); Guard(scope); return value; }
        private T Read<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { T value = entities.GetComponentData<T>(entity); Guard(scope); return value; }
        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "SUBNET_REFERENCES_FAILED"; exceptionType ??= error.GetType().Name; }
        private static JArray Ids(Entity[] values)
        { var result = new JArray(); foreach (Entity value in values) result.Add(Id(value)); return result; }
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "SUBNET_REFERENCES_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private sealed class Bindings
        {
            internal readonly FieldInfo SubNets;
            internal Bindings()
            {
                Require(typeof(NativeReferencesSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6", "SUBNET_REFERENCES_NATIVE_BUILD_UNQUALIFIED");
                FieldInfo? value = typeof(NativeReferencesSystem).GetField("m_SubNetQuery",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(NativeReferencesSystem) && value.FieldType == typeof(EntityQuery)
                    && !value.IsStatic && !value.IsInitOnly, "SUBNET_REFERENCES_FIELD_ABI_UNQUALIFIED");
                SubNets = value!;
            }
        }
    }
}
#endif
