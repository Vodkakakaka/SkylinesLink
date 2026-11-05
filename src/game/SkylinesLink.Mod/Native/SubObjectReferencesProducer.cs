#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using LiveObject = Game.Objects.Object;
using LiveSubObject = Game.Objects.SubObject;
using NativeReferencesSystem = Game.Objects.SubObjectReferencesSystem;

namespace SkylinesLink.Mod.Native
{
    // Owns the two native reference updates, not the child entities. The graph
    // port stays live until their physical owner has confirmed every absence.
    [DisableAutoCreation]
    public sealed partial class SubObjectReferencesProducer : NativeReferencesSystem
    {
        private const int ChildCount = 5;
        private int actor, registeredCount, queryCount, queryChunks, arraysAttempted, arraysAcquired, arraysDisposed;
        private bool configured, bindingsVerified, registeredExactly, emptyPassed, emptyShouldRun;
        private bool parentsBound, childrenBound, linkRequested, unlinkRequested, linkArmed, unlinkArmed;
        private bool linkStarted, linkReturned, linkJoined, linkPassed, unlinkStarted, unlinkReturned, unlinkJoined, unlinkPassed;
        private bool joinFailed, graphRejected, latestRootComplete, latestNodeComplete;
        private int latestRootLength, latestNodeLength;
        private Entity root, node;
        private readonly Entity[] childIds = new Entity[ChildCount], owners = new Entity[ChildCount];
        private Entity[] latestRoot = Array.Empty<Entity>(), latestNode = Array.Empty<Entity>();
        private Entity[] linkedRoot = Array.Empty<Entity>(), linkedNode = Array.Empty<Entity>();
        private Entity[] unlinkedRoot = Array.Empty<Entity>(), unlinkedNode = Array.Empty<Entity>();
        private Entity[] latestQueryMembers = Array.Empty<Entity>();
        private EntityQuery objects;
        private World? source;
        private Bindings? fields;
        private GeneratedSubObjectEntities? children;
        private Action? parentScope, currentScope;
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
            Require(!configured && !CreationStarted && World == null && actor > 0, "SUBOBJECT_REFERENCES_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            Require(configured && fields != null, "SUBOBJECT_REFERENCES_CONFIGURATION_MISSING");
            objects = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<LiveObject>(), ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Game.Vehicles.Vehicle>(), ComponentType.ReadOnly<Game.Creatures.Creature>(),
                    ComponentType.ReadOnly<Game.Objects.Secondary>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            objects.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            fields!.Objects.SetValue(this, objects);
            RequireForUpdate(objects);
            // SystemState owns this query and the inherited compiler hook
            // initializes the native handles. No Game lifecycle was registered.
            ObserveBindings(null);
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !parentsBound && !NativeCallStarted,
                "SUBOBJECT_REFERENCES_EMPTY_REJECTED");
            try
            {
                Guard(scope); ObserveBindings(scope); ReadQueryCounts(scope);
                Require(queryCount == 0, "SUBOBJECT_REFERENCES_NOT_EMPTY");
                emptyShouldRun = ShouldRunSystem(); Guard(scope);
                Require(!emptyShouldRun, "SUBOBJECT_REFERENCES_EMPTY_GATE_CHANGED");
                currentScope = scope; Enabled = true;
                try { Update(); }
                finally { Enabled = false; currentScope = null; }
                Guard(scope); ReadQueryCounts(scope);
                Require(OnUpdateEntries == 0 && queryCount == 0 && !NativeCallStarted,
                    "SUBOBJECT_REFERENCES_EMPTY_NOT_SKIPPED");
                emptyPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void BindParents(Entity root, Entity node, Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && emptyPassed && !parentsBound && !DestructionStarted
                && root != Entity.Null && node != Entity.Null && root != node && root.Index >= 0 && node.Index >= 0,
                "SUBOBJECT_REFERENCES_PARENT_BIND_REJECTED");
            Guard(scope); source = World; this.root = root; this.node = node; parentScope = scope; parentsBound = true;
            RequireCurrentGraph();
        }

        internal void BindChildren(GeneratedSubObjectEntities children, GeneratedSubObjectsPayload payload)
        {
            RequireMainThread();
            if (children == null) throw new ArgumentNullException(nameof(children));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            Require(parentsBound && !childrenBound && !linkRequested && !DestructionStarted && children.CreateReturned
                && !children.UnknownCreation && children.TargetCount == ChildCount && payload.TargetCount == ChildCount
                && ReferenceEquals(payload.Source, source) && payload.Root == root && payload.Node == node && payload.Actor == actor,
                "SUBOBJECT_REFERENCES_CHILD_BIND_REJECTED");
            RequireCurrentGraph();
            int rootCount = 0, nodeCount = 0;
            var distinct = new HashSet<Entity>();
            for (int index = 0; index < ChildCount; index++)
            {
                Entity child = children.EntityAt(index), owner = payload.TargetAt(index).Owner;
                Require(child != Entity.Null && child.Index >= 0 && child != root && child != node && distinct.Add(child)
                    && (owner == root || owner == node), "SUBOBJECT_REFERENCES_CHILD_IDENTITY_REJECTED");
                childIds[index] = child; owners[index] = owner;
                if (owner == root) rootCount++; else nodeCount++;
            }
            Require(rootCount == 4 && nodeCount == 1, "SUBOBJECT_REFERENCES_OWNER_COUNTS_REJECTED");
            this.children = children; childrenBound = true;
            QualifyInputs(parentScope!, false);
        }

        // This port intentionally does not require five query members after
        // unlink: the physical owner destroys those exact children one by one.
        internal void RequireCurrentGraph()
        {
            RequireMainThread();
            Require(parentsBound && parentScope != null && !graphRejected && !NativeWorkMayReadSource,
                "SUBOBJECT_REFERENCES_GRAPH_UNAVAILABLE");
            Require((!linkStarted || linkPassed) && (!unlinkStarted || unlinkPassed),
                "SUBOBJECT_REFERENCES_GRAPH_UNCONFIRMED");
            ObserveGraph(parentScope!, linkPassed && !unlinkPassed);
        }

        internal void RequireLinkedGraph()
        {
            RequireMainThread();
            Require(linkPassed && !unlinkStarted && !unlinkPassed, "SUBOBJECT_REFERENCES_NOT_LINKED");
            RequireCurrentGraph();
        }

        internal void LinkOnce(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(childrenBound && !linkRequested && !unlinkRequested && !DestructionStarted,
                "SUBOBJECT_REFERENCES_LINK_REJECTED");
            linkRequested = true;
            try
            {
                QualifyInputs(scope, false);
                RunUpdate(scope, false);
                ObserveGraph(scope, true);
                linkedRoot = (Entity[])latestRoot.Clone(); linkedNode = (Entity[])latestNode.Clone();
                Require(linkJoined && OnUpdateEntries == 1, "SUBOBJECT_REFERENCES_LINK_UNCONFIRMED");
                linkPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void UnlinkOnce(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(childrenBound && linkPassed && !unlinkRequested && !DestructionStarted,
                "SUBOBJECT_REFERENCES_UNLINK_REJECTED");
            unlinkRequested = true;
            try
            {
                Require(children!.DiscardReady, "SUBOBJECT_REFERENCES_DISCARD_NOT_READY");
                QualifyInputs(scope, true);
                RunUpdate(scope, true);
                ObserveGraph(scope, false);
                unlinkedRoot = (Entity[])latestRoot.Clone(); unlinkedNode = (Entity[])latestNode.Clone();
                Require(unlinkJoined && OnUpdateEntries == 2, "SUBOBJECT_REFERENCES_UNLINK_UNCONFIRMED");
                unlinkPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        private void RunUpdate(Action scope, bool unlink)
        {
            currentScope = scope; linkArmed = !unlink; unlinkArmed = unlink; Enabled = true;
            try
            {
                Update();
                Require(unlink ? unlinkReturned : linkReturned, "SUBOBJECT_REFERENCES_NATIVE_NOT_RETURNED");
                JoinOwnedDependencies();
            }
            finally { Enabled = false; linkArmed = false; unlinkArmed = false; currentScope = null; }
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Action scope = currentScope ?? throw Failure("SUBOBJECT_REFERENCES_SCOPE_CALLBACK_MISSING");
            Require(linkArmed != unlinkArmed && (linkArmed ? !linkStarted : !unlinkStarted),
                "SUBOBJECT_REFERENCES_NOT_ARMED");
            // Recheck after SystemBase.BeforeOnUpdate refreshed dependencies.
            QualifyInputs(scope, unlinkArmed);
            if (unlinkArmed)
            { unlinkStarted = true; base.OnUpdate(); unlinkReturned = true; }
            else
            { linkStarted = true; base.OnUpdate(); linkReturned = true; }
        }

        private void QualifyInputs(Action scope, bool unlink)
        {
            Guard(scope); ObserveBindings(scope);
            if (unlink) RequireLinkedGraph(); else RequireCurrentGraph();
            Require(childrenBound && children != null && children.CreateReturned && !children.UnknownCreation,
                "SUBOBJECT_REFERENCES_CHILDREN_UNAVAILABLE");
            if (unlink) Require(children!.DiscardReady, "SUBOBJECT_REFERENCES_DISCARD_NOT_READY");
            EntityManager entities = EntityManager; Guard(scope);
            for (int index = 0; index < ChildCount; index++)
            {
                Entity child = childIds[index];
                RequireScoped(entities, child, scope, unlink);
                Require(Has<LiveObject>(entities, child, scope) && Has<Created>(entities, child, scope)
                    && Has<Owner>(entities, child, scope) && !Has<Game.Vehicles.Vehicle>(entities, child, scope)
                    && !Has<Game.Creatures.Creature>(entities, child, scope) && !Has<Game.Objects.Secondary>(entities, child, scope),
                    "SUBOBJECT_REFERENCES_CHILD_ROLE_CHANGED");
                Require(Read<Owner>(entities, child, scope).m_Owner == owners[index], "SUBOBJECT_REFERENCES_CHILD_OWNER_CHANGED");
            }
            ReadQueryCounts(scope);
            Require(queryCount == ChildCount && queryChunks > 0 && queryChunks <= ChildCount,
                "SUBOBJECT_REFERENCES_QUERY_BUDGET");
            arraysAttempted++;
            NativeArray<Entity> values = objects.ToEntityArray(Allocator.Temp); arraysAcquired++;
            try
            {
                Guard(scope); Require(values.Length == ChildCount, "SUBOBJECT_REFERENCES_QUERY_COUNT_CHANGED");
                var actual = new Entity[ChildCount];
                for (int index = 0; index < ChildCount; index++) actual[index] = values[index];
                latestQueryMembers = actual;
                var seen = new HashSet<Entity>();
                foreach (Entity value in actual)
                    Require(IndexOfChild(value) >= 0 && seen.Add(value), "SUBOBJECT_REFERENCES_FOREIGN_QUERY_MEMBER");
            }
            finally { values.Dispose(); arraysDisposed++; }
            Guard(scope);
            bool shouldRun = ShouldRunSystem(); Guard(scope);
            Require(shouldRun, "SUBOBJECT_REFERENCES_NATIVE_GATE_CHANGED");
        }

        private void ObserveGraph(Action scope, bool linked)
        {
            latestRootComplete = latestNodeComplete = false;
            latestRoot = latestNode = Array.Empty<Entity>(); latestRootLength = latestNodeLength = -1;
            try
            {
                Guard(scope); Require(!NativeWorkMayReadSource, "SUBOBJECT_REFERENCES_GRAPH_NATIVE_UNKNOWN");
                RequireParents(scope);
                latestRoot = ReadParentBuffer(root, scope, out latestRootLength); latestRootComplete = true;
                latestNode = ReadParentBuffer(node, scope, out latestNodeLength); latestNodeComplete = true;
                RequireParents(scope);
                if (!linked)
                    Require(latestRoot.Length == 0 && latestNode.Length == 0, "SUBOBJECT_REFERENCES_EXPECTED_EMPTY_GRAPH");
                else
                {
                    Require(childrenBound && latestRoot.Length == 4 && latestNode.Length == 1,
                        "SUBOBJECT_REFERENCES_GRAPH_COUNTS_CHANGED");
                    RequireExactEdges(latestRoot, root); RequireExactEdges(latestNode, node);
                    EntityManager entities = EntityManager; Guard(scope);
                    for (int index = 0; index < ChildCount; index++)
                    {
                        RequireScoped(entities, childIds[index], scope, null);
                        Require(Has<Owner>(entities, childIds[index], scope)
                            && Read<Owner>(entities, childIds[index], scope).m_Owner == owners[index],
                            "SUBOBJECT_REFERENCES_GRAPH_OWNER_CHANGED");
                    }
                }
                Guard(scope);
            }
            catch (Exception error) { graphRejected = true; RecordFailure(error); throw; }
        }

        private void RequireParents(Action scope)
        {
            EntityManager entities = EntityManager; Guard(scope);
            RequireScoped(entities, root, scope, false); RequireScoped(entities, node, scope, false);
            Require(Has<LiveObject>(entities, root, scope) && !Has<Owner>(entities, root, scope)
                && !Has<LiveObject>(entities, node, scope) && Has<Owner>(entities, node, scope),
                "SUBOBJECT_REFERENCES_PARENT_ROLE_CHANGED");
            Require(Read<Owner>(entities, node, scope).m_Owner == root, "SUBOBJECT_REFERENCES_NODE_OWNER_CHANGED");
        }

        private Entity[] ReadParentBuffer(Entity parent, Action scope, out int length)
        {
            EntityManager entities = EntityManager; Guard(scope);
            bool exists = entities.HasBuffer<LiveSubObject>(parent); Guard(scope);
            Require(exists, "SUBOBJECT_REFERENCES_PARENT_BUFFER_MISSING");
            DynamicBuffer<LiveSubObject> buffer = entities.GetBuffer<LiveSubObject>(parent, true); Guard(scope);
            length = buffer.Length; Guard(scope);
            Require(length >= 0 && length <= ChildCount, "SUBOBJECT_REFERENCES_PARENT_BUFFER_BUDGET");
            var result = new Entity[length];
            for (int index = 0; index < length; index++) { result[index] = buffer[index].m_SubObject; Guard(scope); }
            return result;
        }

        private void RequireExactEdges(Entity[] values, Entity owner)
        {
            var seen = new HashSet<Entity>();
            foreach (Entity value in values)
            {
                int index = IndexOfChild(value);
                Require(index >= 0 && owners[index] == owner && seen.Add(value), "SUBOBJECT_REFERENCES_FOREIGN_OR_DUPLICATE_EDGE");
            }
            for (int index = 0; index < ChildCount; index++)
                if (owners[index] == owner) Require(seen.Contains(childIds[index]), "SUBOBJECT_REFERENCES_EDGE_MISSING");
        }

        private void RequireScoped(EntityManager entities, Entity entity, Action scope, bool? deleted)
        {
            bool exists = entities.Exists(entity); Guard(scope);
            Require(exists, "SUBOBJECT_REFERENCES_SOURCE_SCOPE_CHANGED");
            bool scoped = entities.HasComponent<ObjectGenerationScope>(entity); Guard(scope);
            Require(scoped && Has<Disabled>(entities, entity, scope)
                && Has<Temp>(entities, entity, scope), "SUBOBJECT_REFERENCES_SOURCE_SCOPE_CHANGED");
            int actual = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value; Guard(scope);
            Require(actual == actor, "SUBOBJECT_REFERENCES_ACTOR_CHANGED");
            if (deleted.HasValue) Require(Has<Deleted>(entities, entity, scope) == deleted.Value, "SUBOBJECT_REFERENCES_DELETION_STATE_CHANGED");
            Temp temp = Read<Temp>(entities, entity, scope);
            Require(temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0,
                "SUBOBJECT_REFERENCES_NOT_NEW_TEMP");
        }

        private void ReadQueryCounts(Action scope)
        { queryCount = objects.CalculateEntityCount(); Guard(scope); queryChunks = objects.CalculateChunkCount(); Guard(scope); }

        private void ObserveBindings(Action? scope)
        {
            Require(!DestructionStarted && !NativeWorkMayReadSource && fields != null, "SUBOBJECT_REFERENCES_BINDINGS_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)fields!.Objects.GetValue(this)!).Equals(objects);
            EntityQuery[] values = EntityQueries; if (scope != null) Guard(scope);
            registeredCount = values.Length; registeredExactly = registeredCount == 1 && values[0].Equals(objects);
            bool filtered = objects.HasFilter(); if (scope != null) Guard(scope);
            Require(bindingsVerified && registeredExactly && filtered, "SUBOBJECT_REFERENCES_BINDINGS_CHANGED");
        }

        private void JoinOwnedDependencies()
        {
            // An exception before native OnUpdate returned can hide a scheduled
            // handle. Completing a default Dependency cannot prove that case.
            Require(!joinFailed && (!linkStarted || linkReturned) && (!unlinkStarted || unlinkReturned),
                "SUBOBJECT_REFERENCES_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (linkReturned) linkJoined = true; if (unlinkReturned) unlinkJoined = true; }
            catch { joinFailed = true; throw; }
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource, "SUBOBJECT_REFERENCES_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // No unmatched Game OnDestroy or borrowed query disposal.
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["storageReleased"] = StorageReleased,
            ["emptyPassed"] = emptyPassed, ["emptyShouldRun"] = emptyShouldRun,
            ["parentsBound"] = parentsBound, ["childrenBound"] = childrenBound,
            ["bindingsVerified"] = bindingsVerified, ["registeredExactly"] = registeredExactly, ["registeredCount"] = registeredCount,
            ["root"] = Id(root), ["node"] = Id(node), ["children"] = Ids(childrenBound ? childIds : Array.Empty<Entity>()),
            ["queryCount"] = queryCount, ["queryChunks"] = queryChunks, ["queryMembers"] = Ids(latestQueryMembers),
            ["queryArrays"] = new JObject { ["attempted"] = arraysAttempted, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
            ["onUpdateEntries"] = OnUpdateEntries,
            ["link"] = new JObject { ["requested"] = linkRequested, ["started"] = linkStarted, ["returned"] = linkReturned,
                ["joined"] = linkJoined, ["passed"] = linkPassed, ["rootEntries"] = Ids(linkedRoot), ["nodeEntries"] = Ids(linkedNode) },
            ["unlink"] = new JObject { ["requested"] = unlinkRequested, ["started"] = unlinkStarted, ["returned"] = unlinkReturned,
                ["joined"] = unlinkJoined, ["passed"] = unlinkPassed, ["rootEntries"] = Ids(unlinkedRoot), ["nodeEntries"] = Ids(unlinkedNode) },
            ["latestGraph"] = new JObject { ["rootLength"] = latestRootLength, ["nodeLength"] = latestNodeLength,
                ["rootComplete"] = latestRootComplete, ["nodeComplete"] = latestNodeComplete,
                ["rootEntries"] = Ids(latestRoot), ["nodeEntries"] = Ids(latestNode), ["rejected"] = graphRejected },
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["checksPassed"] = ChecksPassed, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
            ["scope"] = "five-owned-children-native-link-and-deleted-unlink-only"
        };

        private void Guard(Action scope)
        {
            scope(); if (parentScope != null && !ReferenceEquals(scope, parentScope)) parentScope();
            Require(World != null && World.IsCreated && !DestructionStarted && (!parentsBound || ReferenceEquals(World, source)),
                "SUBOBJECT_REFERENCES_SOURCE_UNAVAILABLE");
        }
        private bool Has<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { bool value = entities.HasComponent<T>(entity); Guard(scope); return value; }
        private T Read<T>(EntityManager entities, Entity entity, Action scope) where T : unmanaged, IComponentData
        { T value = entities.GetComponentData<T>(entity); Guard(scope); return value; }
        private int IndexOfChild(Entity value)
        { for (int index = 0; index < ChildCount; index++) if (childIds[index] == value) return index; return -1; }
        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "SUBOBJECT_REFERENCES_FAILED"; exceptionType ??= error.GetType().Name; }
        private static JArray Ids(Entity[] values)
        { var result = new JArray(); foreach (Entity value in values) result.Add(Id(value)); return result; }
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "SUBOBJECT_REFERENCES_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private sealed class Bindings
        {
            internal readonly FieldInfo Objects;
            internal Bindings()
            {
                Require(typeof(NativeReferencesSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
                    "SUBOBJECT_REFERENCES_NATIVE_BUILD_UNQUALIFIED");
                FieldInfo? value = typeof(NativeReferencesSystem).GetField("m_ObjectQuery",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(NativeReferencesSystem) && value.FieldType == typeof(EntityQuery)
                    && !value.IsStatic && !value.IsInitOnly, "SUBOBJECT_REFERENCES_FIELD_ABI_UNQUALIFIED");
                Objects = value!;
            }
        }
    }
}
#endif
