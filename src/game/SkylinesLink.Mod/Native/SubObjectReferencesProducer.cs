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
        private const int RootRole = 0, NodeRole = 1, OldNodeRole = 2;
        private int actor, registeredCount, queryCount, queryChunks, arraysAttempted, arraysAcquired, arraysDisposed;
        private bool configured, bindingsVerified, registeredExactly, emptyPassed, emptyShouldRun;
        private bool parentsBound, childrenBound, linkRequested, unlinkRequested, linkArmed, unlinkArmed, cancellationUnlink;
        private bool linkStarted, linkReturned, linkJoined, linkPassed, unlinkStarted, unlinkReturned, unlinkJoined, unlinkPassed;
        private bool joinFailed, graphRejected;
        private Parent[] parents = Array.Empty<Parent>();
        private Child[] cohort = Array.Empty<Child>();
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
        private bool IsRelocation => parents.Length == 3;
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
            BindParents(new[] { new Parent(root, 4), new Parent(node, 1) }, scope);
        }

        internal void BindRelocationParents(GeneratedSubObjectsPayload payload, Action scope)
        {
            Require(payload.IsRelocation && ReferenceEquals(payload.Source, World) && payload.Actor == actor,
                "SUBOBJECT_REFERENCES_RELOCATION_PARENT_BIND_REJECTED");
            BindParents(new[] {
                new Parent(payload.Root, 4, payload.CapturedParentTemp(payload.Root)),
                new Parent(payload.Node, 1, payload.CapturedParentTemp(payload.Node)),
                new Parent(payload.OldNode, 1, payload.CapturedParentTemp(payload.OldNode))
            }, scope);
        }

        private void BindParents(Parent[] roles, Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && emptyPassed && !parentsBound && !DestructionStarted,
                "SUBOBJECT_REFERENCES_PARENT_BIND_REJECTED");
            var distinct = new HashSet<Entity>();
            foreach (Parent role in roles)
                Require(role.Entity.Index > 0 && role.Entity.Version > 0 && distinct.Add(role.Entity),
                    "SUBOBJECT_REFERENCES_PARENT_IDENTITY_REJECTED");
            Guard(scope); source = World; parents = roles; parentScope = scope; parentsBound = true;
            RequireCurrentGraph();
        }

        internal void BindChildren(GeneratedSubObjectEntities children, GeneratedSubObjectsPayload payload)
        {
            RequireMainThread();
            if (children == null) throw new ArgumentNullException(nameof(children));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            Require(parentsBound && !childrenBound && !linkRequested && !DestructionStarted && children.CreateReturned
                && !children.UnknownCreation && children.Owns(payload) && children.TargetCount == (IsRelocation ? 6 : 5)
                && ReferenceEquals(payload.Source, source) && payload.IsRelocation == IsRelocation && payload.Actor == actor
                && payload.Root == parents[RootRole].Entity && payload.Node == parents[NodeRole].Entity
                && (!IsRelocation || payload.OldNode == parents[OldNodeRole].Entity),
                "SUBOBJECT_REFERENCES_CHILD_BIND_REJECTED");
            RequireCurrentGraph();
            var counts = new int[parents.Length];
            var distinct = new HashSet<Entity>();
            var members = new Child[children.TargetCount];
            for (int index = 0; index < members.Length; index++)
            {
                Entity child = children.EntityAt(index), owner = payload.TargetAt(index).Owner;
                int role = IndexOfParent(owner);
                Require(child.Index > 0 && child.Version > 0 && IndexOfParent(child) < 0 && distinct.Add(child) && role >= 0,
                    "SUBOBJECT_REFERENCES_CHILD_IDENTITY_REJECTED");
                counts[role]++;
                members[index] = new Child(child, owner, IsRelocation ? children.CapturedTempAt(index) : (Temp?)null);
            }
            for (int index = 0; index < parents.Length; index++)
                Require(counts[index] == parents[index].ChildCount, "SUBOBJECT_REFERENCES_OWNER_COUNTS_REJECTED");
            this.children = children; cohort = members; childrenBound = true;
            QualifyInputs(parentScope!, false);
        }

        // After unlink, the physical owner retires exact children one by one.
        // The empty parent graph remains the authority throughout that release.

        internal void RequireCurrentGraph() => ObserveCurrentGraph(requireLiveChildren: false);

        private void ObserveCurrentGraph(bool requireLiveChildren)
        {
            RequireMainThread();
            Require(parentsBound && parentScope != null && !graphRejected && !NativeWorkMayReadSource,
                "SUBOBJECT_REFERENCES_GRAPH_UNAVAILABLE");
            Require((!linkStarted || linkPassed) && (!unlinkStarted || unlinkPassed),
                "SUBOBJECT_REFERENCES_GRAPH_UNCONFIRMED");
            bool linked = linkPassed && !unlinkPassed;
            Require(!requireLiveChildren || (linked && !unlinkStarted), "SUBOBJECT_REFERENCES_NOT_LINKED");
            ObserveGraph(parentScope!, linked, requireLiveChildren);
        }

        internal void RequireLinkedGraph()
        {
            RequireMainThread();
            Require(linkPassed && !unlinkStarted && !unlinkPassed, "SUBOBJECT_REFERENCES_NOT_LINKED");
            RequireCurrentGraph();
        }

        internal void RequireLiveLinkedGraph() => ObserveCurrentGraph(requireLiveChildren: true);

        internal int ReadLinkedNodeChildren(World source, Entity root, Entity node)
        {
            RequireMainThread();
            Require(parentsBound && ReferenceEquals(World, source) && parents[RootRole].Entity == root && parents[NodeRole].Entity == node,
                "SUBOBJECT_REFERENCES_GRAPH_IDENTITY_CHANGED");
            RequireLiveLinkedGraph();
            return parents[NodeRole].LatestLength;
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
                foreach (Parent parent in parents) parent.Linked = parent.Latest;
                Require(linkJoined && OnUpdateEntries == 1, "SUBOBJECT_REFERENCES_LINK_UNCONFIRMED");
                linkPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void AdmitRelocationCancellation()
        {
            RequireMainThread();
            Require(IsRelocation && linkPassed && !unlinkRequested && !cancellationUnlink,
                "SUBOBJECT_REFERENCES_RELOCATION_CANCEL_UNQUALIFIED");
            cancellationUnlink = true;
            RequireLinkedGraph();
        }

        internal void UnlinkOnce(Action scope, bool afterNativeCancellation = false)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(childrenBound && linkPassed && !unlinkRequested && !DestructionStarted,
                "SUBOBJECT_REFERENCES_UNLINK_REJECTED");
            unlinkRequested = true;
            Require(!IsRelocation || cancellationUnlink == afterNativeCancellation,
                "SUBOBJECT_REFERENCES_RELOCATION_CANCEL_UNQUALIFIED");
            cancellationUnlink = afterNativeCancellation;
            try
            {
                QualifyInputs(scope, true);
                RunUpdate(scope, true);
                ObserveGraph(scope, false);
                foreach (Parent parent in parents) parent.Unlinked = parent.Latest;
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
            if (unlink && !cancellationUnlink) Require(children!.DiscardReady, "SUBOBJECT_REFERENCES_DISCARD_NOT_READY");
            EntityManager entities = EntityManager; Guard(scope);
            foreach (Child member in cohort)
            {
                Entity child = member.Entity;
                RequireScoped(entities, child, scope, unlink, member.Temp);
                Require(Has<LiveObject>(entities, child, scope) && Has<Created>(entities, child, scope) != cancellationUnlink
                    && Has<Owner>(entities, child, scope) && !Has<Game.Vehicles.Vehicle>(entities, child, scope)
                    && !Has<Game.Creatures.Creature>(entities, child, scope) && !Has<Game.Objects.Secondary>(entities, child, scope),
                    "SUBOBJECT_REFERENCES_CHILD_ROLE_CHANGED");
                if (cancellationUnlink)
                    Require(!Has<Applied>(entities, child, scope) && !Has<Updated>(entities, child, scope),
                        "SUBOBJECT_REFERENCES_CANCELLED_CHILD_MARKERS_CHANGED");
                Require(Read<Owner>(entities, child, scope).m_Owner == member.Owner, "SUBOBJECT_REFERENCES_CHILD_OWNER_CHANGED");
            }
            ReadQueryCounts(scope);
            Require(queryCount == cohort.Length && queryChunks > 0 && queryChunks <= cohort.Length,
                "SUBOBJECT_REFERENCES_QUERY_BUDGET");
            arraysAttempted++;
            NativeArray<Entity> values = objects.ToEntityArray(Allocator.Temp); arraysAcquired++;
            try
            {
                Guard(scope); Require(values.Length == cohort.Length, "SUBOBJECT_REFERENCES_QUERY_COUNT_CHANGED");
                var actual = new Entity[cohort.Length];
                for (int index = 0; index < cohort.Length; index++) actual[index] = values[index];
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

        private void ObserveGraph(Action scope, bool linked, bool requireLiveChildren = false)
        {
            foreach (Parent parent in parents)
            {
                parent.Latest = Array.Empty<Entity>(); parent.LatestLength = -1; parent.LatestComplete = false;
            }
            try
            {
                Guard(scope); Require(!NativeWorkMayReadSource, "SUBOBJECT_REFERENCES_GRAPH_NATIVE_UNKNOWN");
                RequireParents(scope);
                foreach (Parent parent in parents) ReadParentBuffer(parent, scope);
                RequireParents(scope);
                foreach (Parent parent in parents)
                {
                    Require(parent.Latest.Length == (linked ? parent.ChildCount : 0),
                        linked ? "SUBOBJECT_REFERENCES_GRAPH_COUNTS_CHANGED" : "SUBOBJECT_REFERENCES_EXPECTED_EMPTY_GRAPH");
                    if (linked) RequireExactEdges(parent);
                }
                if (linked)
                {
                    Require(childrenBound, "SUBOBJECT_REFERENCES_CHILDREN_UNAVAILABLE");
                    EntityManager entities = EntityManager; Guard(scope);
                    foreach (Child member in cohort)
                    {
                        RequireScoped(entities, member.Entity, scope, requireLiveChildren ? false : (bool?)null, member.Temp);
                        Require(Has<Owner>(entities, member.Entity, scope)
                            && Read<Owner>(entities, member.Entity, scope).m_Owner == member.Owner,
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
            Entity root = parents[RootRole].Entity;
            for (int index = 0; index < parents.Length; index++)
            {
                Parent parent = parents[index];
                Entity entity = parent.Entity;
                RequireScoped(entities, entity, scope, cancellationUnlink, parent.Temp);
                Require(Has<LiveObject>(entities, entity, scope) == (index == RootRole)
                    && Has<Owner>(entities, entity, scope) == (index == NodeRole),
                    "SUBOBJECT_REFERENCES_PARENT_ROLE_CHANGED");
                if (index == NodeRole)
                    Require(Read<Owner>(entities, entity, scope).m_Owner == root,
                        "SUBOBJECT_REFERENCES_NODE_OWNER_CHANGED");
            }
        }

        private void ReadParentBuffer(Parent parent, Action scope)
        {
            EntityManager entities = EntityManager; Guard(scope);
            bool exists = entities.HasBuffer<LiveSubObject>(parent.Entity); Guard(scope);
            Require(exists, "SUBOBJECT_REFERENCES_PARENT_BUFFER_MISSING");
            DynamicBuffer<LiveSubObject> buffer = entities.GetBuffer<LiveSubObject>(parent.Entity, true); Guard(scope);
            int length = buffer.Length; Guard(scope);
            parent.LatestLength = length;
            Require(length >= 0 && length <= parent.ChildCount, "SUBOBJECT_REFERENCES_PARENT_BUFFER_BUDGET");
            if (length != 0)
            {
                var result = new Entity[length];
                for (int index = 0; index < length; index++) { result[index] = buffer[index].m_SubObject; Guard(scope); }
                parent.Latest = result;
            }
            parent.LatestComplete = true;
        }

        private void RequireExactEdges(Parent parent)
        {
            // The exact count is checked first. Every edge must be a distinct
            // cohort member of this role, which also proves no member is absent.
            var seen = new HashSet<Entity>();
            foreach (Entity value in parent.Latest)
            {
                int index = IndexOfChild(value);
                Require(index >= 0 && cohort[index].Owner == parent.Entity && seen.Add(value),
                    "SUBOBJECT_REFERENCES_FOREIGN_OR_DUPLICATE_EDGE");
            }
        }

        private void RequireScoped(EntityManager entities, Entity entity, Action scope, bool? deleted, Temp? expected)
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
            Require(expected.HasValue ? temp.Equals(expected.Value)
                : temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0,
                "SUBOBJECT_REFERENCES_TEMP_CHANGED");
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
            try
            {
                bool newLink = IsRelocation && linkReturned && !linkJoined;
                bool newUnlink = (IsRelocation || cancellationUnlink) && unlinkReturned && !unlinkJoined;
                if (newLink || newUnlink)
                    Require(!Dependency.Equals(default(Unity.Jobs.JobHandle)), "SUBOBJECT_REFERENCES_WRITER_UNAVAILABLE");
                Dependency.Complete();
                if (linkReturned) linkJoined = true;
                if (unlinkReturned) unlinkJoined = true;
            }
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

        internal JObject Snapshot()
        {
            Parent? root = parentsBound ? parents[RootRole] : null;
            Parent? node = parentsBound ? parents[NodeRole] : null;
            var children = new JArray();
            foreach (Child member in cohort) children.Add(Id(member.Entity));
            var link = new JObject { ["requested"] = linkRequested, ["started"] = linkStarted, ["returned"] = linkReturned,
                ["joined"] = linkJoined, ["passed"] = linkPassed, ["rootEntries"] = Ids(root?.Linked), ["nodeEntries"] = Ids(node?.Linked) };
            var unlink = new JObject { ["requested"] = unlinkRequested, ["started"] = unlinkStarted, ["returned"] = unlinkReturned,
                ["joined"] = unlinkJoined, ["passed"] = unlinkPassed, ["afterNativeCancellation"] = cancellationUnlink,
                ["rootEntries"] = Ids(root?.Unlinked), ["nodeEntries"] = Ids(node?.Unlinked) };
            var latest = new JObject { ["rootLength"] = root?.LatestLength ?? 0, ["nodeLength"] = node?.LatestLength ?? 0,
                ["rootComplete"] = root?.LatestComplete == true, ["nodeComplete"] = node?.LatestComplete == true,
                ["rootEntries"] = Ids(root?.Latest), ["nodeEntries"] = Ids(node?.Latest), ["rejected"] = graphRejected };
            var result = new JObject {
                ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
                ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
                ["resourcesReleased"] = ResourcesReleased, ["storageReleased"] = StorageReleased,
                ["emptyPassed"] = emptyPassed, ["emptyShouldRun"] = emptyShouldRun,
                ["parentsBound"] = parentsBound, ["childrenBound"] = childrenBound,
                ["bindingsVerified"] = bindingsVerified, ["registeredExactly"] = registeredExactly, ["registeredCount"] = registeredCount,
                ["root"] = Id(root?.Entity ?? Entity.Null), ["node"] = Id(node?.Entity ?? Entity.Null), ["children"] = children,
                ["queryCount"] = queryCount, ["queryChunks"] = queryChunks, ["queryMembers"] = Ids(latestQueryMembers),
                ["queryArrays"] = new JObject { ["attempted"] = arraysAttempted, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
                ["onUpdateEntries"] = OnUpdateEntries, ["link"] = link, ["unlink"] = unlink, ["latestGraph"] = latest,
                ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
                ["checksPassed"] = ChecksPassed, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
                ["scope"] = IsRelocation ? "six-relocation-children-native-link-and-deleted-unlink-only"
                    : "five-owned-children-native-link-and-deleted-unlink-only"
            };
            if (IsRelocation)
            {
                Parent old = parents[OldNodeRole];
                result["oldNode"] = Id(old.Entity);
                link["oldNodeEntries"] = Ids(old.Linked);
                unlink["oldNodeEntries"] = Ids(old.Unlinked);
                latest["oldNodeLength"] = old.LatestLength;
                latest["oldNodeComplete"] = old.LatestComplete;
                latest["oldNodeEntries"] = Ids(old.Latest);
            }
            return result;
        }

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
        private int IndexOfParent(Entity value)
        {
            for (int index = 0; index < parents.Length; index++)
                if (parents[index].Entity == value) return index;
            return -1;
        }

        private int IndexOfChild(Entity value)
        {
            for (int index = 0; index < cohort.Length; index++)
                if (cohort[index].Entity == value) return index;
            return -1;
        }

        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "SUBOBJECT_REFERENCES_FAILED"; exceptionType ??= error.GetType().Name; }
        private static JArray Ids(Entity[]? values)
        {
            var result = new JArray();
            if (values != null)
                foreach (Entity value in values) result.Add(Id(value));
            return result;
        }
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "SUBOBJECT_REFERENCES_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private sealed class Parent
        {
            internal readonly Entity Entity;
            internal readonly int ChildCount;
            internal readonly Temp? Temp;
            internal Entity[] Latest = Array.Empty<Entity>();
            internal Entity[] Linked = Array.Empty<Entity>();
            internal Entity[] Unlinked = Array.Empty<Entity>();
            internal int LatestLength;
            internal bool LatestComplete;

            internal Parent(Entity entity, int childCount, Temp? temp = null)
            { Entity = entity; ChildCount = childCount; Temp = temp; }
        }

        private readonly struct Child
        {
            internal readonly Entity Entity, Owner;
            internal readonly Temp? Temp;

            internal Child(Entity entity, Entity owner, Temp? temp)
            { Entity = entity; Owner = owner; Temp = temp; }
        }

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
