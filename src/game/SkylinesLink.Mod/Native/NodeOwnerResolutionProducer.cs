#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using NativeTransform = Game.Objects.Transform;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Resolves one scoped node through the native Owner writer, then its one removal ECB.</summary>
    [DisableAutoCreation]
    public sealed partial class NodeOwnerResolutionProducer : FindOwnersSystem2
    {
        private int actor, ownerCount, ownerChunkCount, subCount, subChunkCount, matchingRoots, threadCount;
        private int arrayAttempts, arraysAcquired, arraysDisposed;
        private bool configured, emptyPassed, boundInputs, requested, armed, nativeStarted, nativeReturned, completed, joinFailed;
        private bool bindingsVerified, registeredExactly, ownerLinkObserved, factsAvailable, ownerDefinitionPresent, ownerDefinitionAbsent;
        private bool metadataQualified, scopeAfterPlayback;
        private int registeredCount, metadataCount, metadataBytes, metadataSortKey;
        private Entity root, node, observedOwner;
        private OwnerDefinition expectedDefinition;
        private EntityQuery owners, subs;
        private NodeOwnerResolutionBarrier? output;
        private Bindings? fields;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeCallStarted => nativeStarted;
        internal bool NativeWorkMayReadSource => nativeStarted && !completed
            || output != null && output.PlaybackStarted && !output.PlaybackCallEnded;
        internal bool StorageReleased => arrayAttempts == arraysAcquired && arraysAcquired == arraysDisposed;
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;
        internal bool OwnerLinkObserved => ownerLinkObserved;
        internal bool OwnerDefinitionAbsentObserved => ownerDefinitionAbsent;
        internal bool ResolutionPassed => completed && factsAvailable && observedOwner == root && ownerLinkObserved && ownerDefinitionAbsent && metadataQualified
            && output?.PlaybackReturned == true && scopeAfterPlayback && failureCode == null;

        internal void Configure(int actor, NodeOwnerResolutionBarrier barrier)
        {
            RequireMainThread();
            Require(!configured && !CreationStarted && World == null && actor > 0 && barrier != null,
                "NODE_OWNER_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; output = barrier; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            var barrier = output ?? throw Failure("NODE_OWNER_CONFIGURATION_MISSING");
            var bound = fields ?? throw Failure("NODE_OWNER_CONFIGURATION_MISSING");
            Require(configured && barrier.CreationReturned && ReferenceEquals(barrier.World, World), "NODE_OWNER_CONFIGURATION_MISSING");
            // Preserve the native owners' Updated/role/!Deleted conditions. The
            // extra scope/Disabled/Temp constraints exclude every player entity.
            owners = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Game.Net.SubNet>(), ComponentType.ReadOnly<Game.Areas.SubArea>(),
                    ComponentType.ReadOnly<Game.Objects.SubObject>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }, Options = EntityQueryOptions.IncludeDisabledEntities });
            // This experiment has exactly one new node. It does not admit the
            // native system's other edge, area or object sub-entity branches.
            subs = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<OwnerDefinition>(), ComponentType.ReadWrite<Owner>(),
                    ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            var scope = new ObjectGenerationScope { Value = actor };
            owners.SetSharedComponentFilter(scope); subs.SetSharedComponentFilter(scope);
            bound.Barrier.SetValue(this, barrier); bound.Owners.SetValue(this, owners); bound.Subs.SetValue(this, subs);
            RequireForUpdate(subs);
            ObserveBindings();
            CreationReturned = true;
        }

        // Compiler initialization and SystemBase's dependency publication remain
        // native. No unmatched Game lifecycle callback is registered or removed.
        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate()
        {
            RequireMainThread();
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !boundInputs, "NODE_OWNER_EMPTY_REJECTED");
            Require(owners.IsEmptyIgnoreFilter && subs.IsEmptyIgnoreFilter && owners.CalculateEntityCount() == 0
                && subs.CalculateEntityCount() == 0 && !ShouldRunSystem(), "NODE_OWNER_NOT_EMPTY");
            Enabled = true;
            try { Update(); emptyPassed = true; }
            finally { Enabled = false; }
            Require(OnUpdateEntries == 0 && output!.PendingCount == 0, "NODE_OWNER_EMPTY_ENTERED_KERNEL");
            ObserveBindings();
        }

        internal void BindInputs(Entity root, Entity node)
        {
            RequireMainThread();
            Require(CreationReturned && emptyPassed && !DestructionStarted && !boundInputs && !requested
                && root != Entity.Null && node != Entity.Null && root.Index >= 0 && node.Index >= 0 && root != node,
                "NODE_OWNER_INPUT_BIND_REJECTED");
            this.root = root; this.node = node; boundInputs = true;
        }

        internal void ResolveOnce(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(CreationReturned && boundInputs && !DestructionStarted && !requested, "NODE_OWNER_RESOLUTION_REJECTED");
            requested = true;
            var barrier = output ?? throw Failure("NODE_OWNER_NOT_CREATED");
            try
            {
                requireCurrentScope();
                QualifyInputs();
                requireCurrentScope();
                // Recheck after the last caller callback; no yield or provider
                // callback separates this witness from the native writer.
                QualifyInputs();
                armed = true; Enabled = true;
                try
                {
                    Update();
                    Require(nativeReturned, "NODE_OWNER_NATIVE_NOT_RETURNED");
                    JoinOwnedDependencies();
                }
                finally { armed = false; Enabled = false; }

                // The native job has already written Owner in place. This fact
                // must survive a later metadata, callback or Playback failure.
                ObserveNodeFacts();
                Require(factsAvailable && observedOwner == root && ownerLinkObserved && ownerDefinitionPresent, "NODE_OWNER_NATIVE_LINK_NOT_OBSERVED");
                ObserveRemovalMetadata();
                requireCurrentScope();
                RequireRemovalReady();
                barrier.PlaybackSingleOnce();
                ObserveNodeFacts();
                Require(factsAvailable && observedOwner == root && ownerLinkObserved && ownerDefinitionAbsent, "NODE_OWNER_REMOVAL_NOT_OBSERVED");
                requireCurrentScope(); scopeAfterPlayback = true;
            }
            catch (Exception error)
            {
                failureCode ??= error is Rejection rejected ? rejected.Code : "NODE_OWNER_RESOLUTION_FAILED";
                exceptionType ??= error.GetType().Name;
                // Playback has no asynchronous consumer. Once its call has
                // unwound, inspect this exact node instead of replaying it.
                if (completed) TryObserveNodeFacts();
                throw;
            }
        }

        private void QualifyInputs()
        {
            Require(World != null && World.IsCreated && output!.PendingCount == 0, "NODE_OWNER_SOURCE_UNAVAILABLE");
            ObserveBindings();
            EntityManager entities = EntityManager;
            RequireScoped(entities, root); RequireScoped(entities, node);
            Require(entities.HasComponent<Game.Net.Node>(node) && entities.HasComponent<Owner>(node)
                && entities.HasComponent<OwnerDefinition>(node) && entities.GetComponentData<Owner>(node).m_Owner == Entity.Null,
                "NODE_OWNER_NODE_NOT_UNRESOLVED");
            Require(entities.HasComponent<PrefabRef>(root) && entities.HasComponent<NativeTransform>(root)
                && entities.HasComponent<Updated>(root) && !entities.HasComponent<Deleted>(root), "NODE_OWNER_ROOT_ROLE_MISSING");
            OwnerDefinition definition = entities.GetComponentData<OwnerDefinition>(node);
            PrefabRef prefab = entities.GetComponentData<PrefabRef>(root);
            NativeTransform transform = entities.GetComponentData<NativeTransform>(root);
            Require(definition.m_Prefab == prefab.m_Prefab && definition.m_Position.Equals(transform.m_Position)
                && definition.m_Rotation.Equals(transform.m_Rotation), "NODE_OWNER_ROOT_IDENTITY_MISMATCH");
            ownerCount = owners.CalculateEntityCount(); ownerChunkCount = owners.CalculateChunkCount();
            subCount = subs.CalculateEntityCount(); subChunkCount = subs.CalculateChunkCount();
            threadCount = JobsUtility.ThreadIndexCount;
            Require(ownerCount >= 1 && ownerCount <= 2 && ownerChunkCount >= 1 && ownerChunkCount <= 2
                && subCount == 1 && subChunkCount == 1 && subs.GetSingletonEntity() == node
                && threadCount > 0 && threadCount <= 256, "NODE_OWNER_QUERY_BUDGET");
            arrayAttempts++;
            NativeArray<Entity> candidates = owners.ToEntityArray(Allocator.Temp); arraysAcquired++;
            try
            {
                Require(candidates.Length == ownerCount, "NODE_OWNER_CANDIDATES_CHANGED");
                bool rootSeen = false; matchingRoots = 0;
                for (int index = 0; index < candidates.Length; index++)
                {
                    Entity candidate = candidates[index];
                    Require(candidate == root || candidate == node, "NODE_OWNER_FOREIGN_CANDIDATE");
                    RequireScoped(entities, candidate);
                    rootSeen |= candidate == root;
                    if (!entities.HasComponent<PrefabRef>(candidate)
                        || entities.GetComponentData<PrefabRef>(candidate).m_Prefab != definition.m_Prefab) continue;
                    if (entities.HasComponent<NativeTransform>(candidate))
                    {
                        NativeTransform value = entities.GetComponentData<NativeTransform>(candidate);
                        if (value.m_Position.Equals(definition.m_Position) && value.m_Rotation.Equals(definition.m_Rotation))
                        { Require(candidate == root, "NODE_OWNER_AMBIGUOUS_MATCH"); matchingRoots++; }
                    }
                    else Require(!entities.HasComponent<Game.Net.Curve>(candidate), "NODE_OWNER_CURVE_MATCH_OUTSIDE_PROBE");
                }
                Require(rootSeen && matchingRoots == 1, "NODE_OWNER_UNIQUE_ROOT_NOT_FOUND");
            }
            finally { candidates.Dispose(); arraysDisposed++; }
            expectedDefinition = definition;
        }

        private void RequireScoped(EntityManager entities, Entity entity)
        {
            Require(entities.Exists(entity) && entities.HasComponent<Unity.Entities.Disabled>(entity)
                && entities.HasComponent<ObjectGenerationScope>(entity)
                && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor
                && entities.HasComponent<Temp>(entity) && !entities.HasComponent<Deleted>(entity), "NODE_OWNER_SCOPE_CHANGED");
            Temp temp = entities.GetComponentData<Temp>(entity);
            Require(temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0, "NODE_OWNER_NOT_NEW_TEMP");
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Require(armed && !nativeStarted, "NODE_OWNER_NOT_ARMED");
            nativeStarted = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private void JoinOwnedDependencies()
        {
            Require(!joinFailed && (!nativeStarted || nativeReturned), "NODE_OWNER_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (nativeReturned) completed = true; }
            catch { joinFailed = true; throw; }
        }

        private void ObserveNodeFacts()
        {
            Require(World != null && World.IsCreated, "NODE_OWNER_READ_SOURCE_UNAVAILABLE");
            EntityManager entities = EntityManager;
            RequireScoped(entities, node);
            Require(entities.HasComponent<Owner>(node), "NODE_OWNER_COMPONENT_MISSING");
            observedOwner = entities.GetComponentData<Owner>(node).m_Owner;
            factsAvailable = true; ownerLinkObserved |= observedOwner == root;
            ownerDefinitionPresent = entities.HasComponent<OwnerDefinition>(node);
            ownerDefinitionAbsent = !ownerDefinitionPresent;
        }
        private void TryObserveNodeFacts()
        { try { ObserveNodeFacts(); } catch { factsAvailable = false; } }

        private void RequireRemovalReady()
        {
            Require(completed && metadataQualified && output!.PendingCount == 1 && !output.PlaybackAttempted,
                "NODE_OWNER_REMOVAL_NOT_READY");
            EntityManager entities = EntityManager;
            RequireScoped(entities, root); RequireScoped(entities, node);
            Require(entities.HasComponent<Owner>(node) && entities.GetComponentData<Owner>(node).m_Owner == root
                && entities.HasComponent<OwnerDefinition>(node)
                && entities.GetComponentData<OwnerDefinition>(node).Equals(expectedDefinition)
                && subs.CalculateEntityCount() == 1 && subs.GetSingletonEntity() == node,
                "NODE_OWNER_REMOVAL_TARGET_CHANGED");
            Require(entities.GetComponentData<PrefabRef>(root).m_Prefab == expectedDefinition.m_Prefab
                && entities.GetComponentData<NativeTransform>(root).m_Position.Equals(expectedDefinition.m_Position)
                && entities.GetComponentData<NativeTransform>(root).m_Rotation.Equals(expectedDefinition.m_Rotation),
                "NODE_OWNER_REMOVAL_ROOT_CHANGED");
        }

        private void ObserveRemovalMetadata()
        {
            var barrier = output ?? throw Failure("NODE_OWNER_METADATA_NOT_READY");
            Require(completed && barrier.PendingCount == 1 && subCount == 1 && subChunkCount == 1, "NODE_OWNER_METADATA_NOT_READY");
            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type view = typeof(EntityCommandBuffer).GetNestedType("MultipleEntitiesComponentCommandView", BindingFlags.NonPublic)
                ?? throw Failure("NODE_OWNER_REMOVE_ABI_UNAVAILABLE");
            // Exactly one qualified sub chunk enqueues exactly one bulk remove.
            // That primary-source bound precedes the debugger's complete walk.
            object proxy = abi.Constructor.Invoke(new object[] { barrier.BorrowSingle() });
            Array commands = (Array)abi.Commands.GetValue(proxy)!;
            metadataCount = commands.Length;
            Require(metadataCount == 1, "NODE_OWNER_REMOVE_COUNT_UNEXPECTED");
            object command = commands.GetValue(0)!;
            string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
            Require(command.GetType() == view && kind == "RemoveComponentForMultipleEntities"
                && abi.Int(command, "EntitiesCount") == 1
                && (bool)abi.Value(command, "SkipDeferredEntityLookup", typeof(bool))
                && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == TypeManager.GetTypeIndex<OwnerDefinition>()
                && (short)abi.Value(command, "ComponentSize", typeof(short)) == 0
                && abi.Value(command, "ComponentValue", typeof(object)) == null
                && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)), "NODE_OWNER_REMOVE_METADATA_UNQUALIFIED");
            metadataBytes = abi.Int(command, "TotalSizeInBytes"); metadataSortKey = abi.Int(command, "SortKey");
            Require(metadataBytes > 0 && metadataBytes <= 1024 && metadataSortKey >= 0, "NODE_OWNER_REMOVE_METADATA_UNQUALIFIED");
            // Entities is an opaque EntityNode. Its Ptr is never read. The
            // target closure follows from this same one-member scoped query
            // and the pinned native job's unconditional array remove, not an
            // independent observation of the ECB's entity-array contents.
            metadataQualified = true;
        }

        private void ObserveBindings()
        {
            var bound = fields ?? throw Failure("NODE_OWNER_BINDINGS_UNAVAILABLE");
            Require(!NativeWorkMayReadSource && !DestructionStarted, "NODE_OWNER_OBSERVATION_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)bound.Owners.GetValue(this)!).Equals(owners)
                && ((EntityQuery)bound.Subs.GetValue(this)!).Equals(subs) && ReferenceEquals(bound.Barrier.GetValue(this), output);
            EntityQuery[] values = EntityQueries;
            registeredCount = values.Length;
            registeredExactly = registeredCount == 2 && Contains(values, owners) && Contains(values, subs);
            Require(bindingsVerified && registeredExactly && owners.HasFilter() && subs.HasFilter(), "NODE_OWNER_BINDINGS_CHANGED");
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource && StorageReleased, "NODE_OWNER_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["emptyUpdatePassed"] = emptyPassed, ["inputsBound"] = boundInputs,
            ["resolutionPassed"] = ResolutionPassed, ["root"] = Id(root), ["node"] = Id(node),
            ["bindingsVerified"] = bindingsVerified, ["queriesRegistered"] = registeredExactly, ["registeredQueryCount"] = registeredCount,
            ["ownerCount"] = ownerCount, ["ownerChunkCount"] = ownerChunkCount, ["subCount"] = subCount, ["subChunkCount"] = subChunkCount,
            ["matchingRoots"] = matchingRoots, ["threadCount"] = threadCount, ["onUpdateEntries"] = OnUpdateEntries,
            ["nativeCallStarted"] = nativeStarted, ["nativeCallReturned"] = nativeReturned, ["jobJoinObserved"] = completed,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["ownerWriteMayHaveOccurred"] = nativeStarted, ["joinFailed"] = joinFailed,
            ["ownerLinkObserved"] = ownerLinkObserved, ["factsAvailable"] = factsAvailable,
            ["observedOwner"] = factsAvailable ? (JToken)Id(observedOwner) : JValue.CreateNull(),
            ["ownerDefinitionPresent"] = factsAvailable ? new JValue(ownerDefinitionPresent) : JValue.CreateNull(),
            ["ownerDefinitionAbsentObserved"] = ownerDefinitionAbsent,
            ["bulk"] = new JObject { ["qualified"] = metadataQualified, ["count"] = metadataCount,
                ["bytes"] = metadataBytes, ["sortKey"] = metadataSortKey, ["opaqueTargetsIndependentlyRead"] = false },
            ["playback"] = output == null ? JValue.CreateNull() : (JToken)new JObject {
                ["attempted"] = output.PlaybackAttempted, ["started"] = output.PlaybackStarted,
                ["returned"] = output.PlaybackReturned, ["callEnded"] = output.PlaybackCallEnded,
                ["mayHaveApplied"] = output.PlaybackStarted },
            ["arrays"] = new JObject { ["attempted"] = arrayAttempts, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
            ["nativeTransientCleanupScheduled"] = nativeReturned, ["nativeTransientReclamationObserved"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType), ["scope"] = "private-node-owner-link-only"
        };

        private static bool Contains(EntityQuery[] values, EntityQuery expected)
        { foreach (EntityQuery value in values) if (value.Equals(expected)) return true; return false; }
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NODE_OWNER_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private sealed class Bindings
        {
            internal readonly FieldInfo Barrier, Owners, Subs;
            internal Bindings()
            {
                Require(typeof(FindOwnersSystem2).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6", "NODE_OWNER_NATIVE_BUILD_UNQUALIFIED");
                Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier2B));
                Owners = Field("m_OwnersQuery", typeof(EntityQuery)); Subs = Field("m_SubEntityQuery", typeof(EntityQuery));
            }
            private static FieldInfo Field(string name, Type expected)
            {
                FieldInfo? field = typeof(FindOwnersSystem2).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(field != null && field.DeclaringType == typeof(FindOwnersSystem2) && field.FieldType == expected
                    && !field.IsStatic && !field.IsInitOnly, "NODE_OWNER_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class NodeOwnerResolutionBarrier : ModificationBarrier2B
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool PlaybackAttempted { get; private set; }
        internal bool PlaybackStarted { get; private set; }
        internal bool PlaybackReturned { get; private set; }
        internal bool PlaybackCallEnded { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("NODE_OWNER_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("NODE_OWNER_BARRIER_AUTOPLAY_FORBIDDEN");
        internal EntityCommandBuffer BorrowSingle()
        {
            if (!CreationReturned || DestructionStarted || PendingCount != 1)
                throw new InvalidOperationException("NODE_OWNER_SINGLE_BUFFER_REQUIRED");
            return PendingBuffers[0];
        }
        internal void PlaybackSingleOnce()
        {
            if (!MainThreadDispatcher.isMainThread || PlaybackAttempted)
                throw new InvalidOperationException("NODE_OWNER_PLAYBACK_REJECTED");
            PlaybackAttempted = true;
            EntityCommandBuffer buffer = BorrowSingle();
            PlaybackStarted = true;
            try { buffer.Playback(EntityManager); PlaybackReturned = true; }
            finally { PlaybackCallEnded = true; }
            // No Dispose of this borrowed value. The original barrier remains
            // the sole disposer, including after a partially applied remove.
        }
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("NODE_OWNER_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }
}
#endif
