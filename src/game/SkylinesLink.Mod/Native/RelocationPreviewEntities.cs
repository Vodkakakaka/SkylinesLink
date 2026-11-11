#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Building = Game.Buildings.Building;
using Disabled = Unity.Entities.Disabled;
using SubNet = Game.Net.SubNet;
using UpdateFrame = Game.Simulation.UpdateFrame;

namespace SkylinesLink.Mod.Native
{
    // Owns the three Disabled roles and the lifecycle of their isolated graph.
    internal sealed class RelocationPreviewEntities
    {
        private const int MaximumTypes = 280;
        private readonly RelocationPreviewPayload payload;
        private readonly World source;
        private readonly int actor;
        private readonly Action requireScope;
        private readonly NativeEntityBatch batch;
        private readonly RelocationPreviewSuccessors successors;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private readonly Role[] roles = { new Role(), new Role(), new Role() };
        private bool attempted, verified, sourceUnchanged, visibilityUnchanged, detachAttempted;
        private bool groupDestroyEntered, groupDestroyReturned, groupAbsent, submit;
        private string stage = "unstarted";

        internal RelocationPreviewEntities(RelocationPreviewPayload payload, int actor, Action requireScope)
        {
            Require(actor > 0, "RELOCATION_PREVIEW_ACTOR_UNAVAILABLE");
            this.payload = payload; this.actor = actor; this.requireScope = requireScope;
            source = payload.Definitions.Source;
            batch = new NativeEntityBatch(source, roles.Length);
            successors = new RelocationPreviewSuccessors(source, actor, requireScope);
        }

        internal bool Finished { get; private set; }
        internal string? FailureCode { get; private set; }
        internal bool ResourcesReleased => batch.ResourcesReleased && archetypes.ResourcesReleased && successors.ResourcesReleased;
        private bool InputsAbsent => batch.AbsenceConfirmed(0) && batch.AbsenceConfirmed(1) && batch.AbsenceConfirmed(2);
        internal bool NativeStateUncertain => successors.CommitEffectEntered
            ? !successors.CommitWorldOwnershipConfirmed || successors.NativeWorkMayReadSource
            : !successors.CanRetireInputs || successors.OwnerWriteMayHaveOccurred && !InputsAbsent;
        internal bool CommitPublished => submit && Finished && FailureCode == null && ResourcesReleased && successors.CommitPublished;
        internal bool CommitPassed => CommitPublished && successors.CommitDefaultReady;
        internal void ObserveCommittedWorld() => successors.ObserveCommittedWorld();
        internal void RequireSeparateBuild(Entity root, Entity node) => successors.RequireSeparateBuild(root, node);
        internal JObject? CommitSnapshot() => successors.CommitSnapshot();
        internal bool Passed => Finished && verified && successors.Passed && sourceUnchanged
            && visibilityUnchanged && ResourcesReleased && FailureCode == null;

        internal void RunOnce(bool submit = false)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted, "RELOCATION_PREVIEW_ENTRY_REJECTED");
            attempted = true;
            this.submit = submit;
            var original = payload.Definitions.Original;
            bool rootMarked = false, nodeMarked = false, markersObserved = false;
            try
            {
                stage = "preflight";
                RequireOriginal();
                var entities = source.EntityManager;
                rootMarked = entities.HasComponent<BatchesUpdated>(original.Root);
                nodeMarked = entities.HasComponent<BatchesUpdated>(original.NodeEntity);
                markersObserved = true;

                Require(entities.GetComponentData<ObjectData>(original.Prefab).m_Archetype.Equals(payload.BuildingArchetype)
                    && entities.GetComponentData<NetData>(original.NodePrefab).m_NodeArchetype.Equals(payload.NodeArchetype),
                    "RELOCATION_PREVIEW_PREFAB_ARCHETYPE_CHANGED");
                var building = PrepareArchetype(payload.BuildingArchetype, building: true);
                var node = PrepareArchetype(payload.NodeArchetype, building: false);
                PrepareRole(0, building.archetype, building.types);
                PrepareRole(1, node.archetype, node.types);
                PrepareRole(2, node.archetype, node.types);

                stage = "prepare-successors";
                successors.Prepare();
                RequireOriginal();

                stage = "materialize";
                for (int index = 0; index < roles.Length; index++) Materialize(index);
                stage = "readback";
                CompleteTrackedWriters();
                for (int index = 0; index < roles.Length; index++) Verify(index);
                RequireOriginal();
                Require(MarkersUnchanged(), "RELOCATION_PREVIEW_SOURCE_VISIBILITY_CHANGED");
                verified = true;

                stage = "initialize";
                var root = RequireTarget(0);
                var newNode = RequireTarget(2);
                successors.BindAndInitialize(root, newNode, payload);
                Verify(0);
                RequireOriginal();

                stage = "resolve-owner";
                TypeIndex ownerDefinition = TypeManager.GetTypeIndex<OwnerDefinition>();
                roles[2].PendingShape = ownerDefinition;
                successors.ResolveOwner(root, newNode, payload);
                Require(!entities.HasComponent<OwnerDefinition>(RequireTarget(2)), "RELOCATION_PREVIEW_OWNER_KEY_RETAINED");
                roles[2].Types!.Remove(ownerDefinition);
                roles[2].PendingShape = null;
                RequireOriginal();

                stage = "attachments";
                var oldNode = RequireTarget(1);
                RequireUnownedOldNode();
                successors.QualifyAttachments(root, oldNode, newNode, payload);
                RequireOriginal();

                stage = "composition";
                var selections = successors.SelectCompositions();
                ConfirmComposition(1, selections.Old);
                ConfirmComposition(2, selections.New);
                RequireOriginal();

                stage = "link";
                RequireUnownedOldNode();
                successors.Link();
                RequireOriginal();

                stage = "node-geometry";
                var geometry = successors.GenerateGeometry(selections.Old, selections.New);
                roles[1].Geometry = geometry.Old;
                roles[2].Geometry = geometry.New;
                RequireOriginal();

                stage = "lanes";
                successors.GenerateLanes(selections.Old, selections.New, geometry);
                RequireOriginal();

                stage = "road";
                successors.GenerateRoad(RequireOriginal);
                RequireOriginal();

                stage = "node-cost";
                successors.CalculateNodeCost(RequireOriginal);
                stage = "validation";
                successors.CheckEligibility(original, RequireOriginal);
                RequireOriginal();

                stage = "prepare-apply-objects";
                successors.PrepareApplyObjects(original, RequireOriginal, submit
                    ? survivor => batch.ConfirmMixedTransfer(survivor, requireScope)
                    : (Action<Entity>?)null);
                if (submit) return;
                RequireOriginal();

                stage = "cancel";
                Entity[] cancelled = successors.Cancel(RequireOriginal, ConfirmParentMarkers);
                stage = "retire-cancelled-group";
                DestroyCancelledGroup(cancelled);
                RequireOriginal();
            }

            catch (Exception error) { Record(error, "RELOCATION_PREVIEW_STAGE_FAILED"); }
            finally
            {
                if (successors.AttachmentRetirementPending && !successors.NativeWorkMayReadSource)
                {
                    try { successors.RetireAttachments(); }
                    catch (Exception error) { Record(error, "RELOCATION_PREVIEW_ATTACHMENT_RETIREMENT_FAILED"); }
                }
                if (!successors.CommitEffectEntered && !successors.CancellationEntered && successors.LinkPassed && !detachAttempted && successors.AttachmentsRetired && !successors.NativeWorkMayReadSource)
                {
                    try { Detach(); }
                    catch (Exception error) { Record(error, "RELOCATION_PREVIEW_UNLINK_FAILED"); }
                }
                if (successors.CanRetireInputs)
                {
                    try
                    {
                        if (!successors.CancellationEntered) batch.ReleaseBySlot(slot =>
                        {
                            try { RequireSafeRelease(slot); }
                            catch (Exception error) { Record(error, "RELOCATION_PREVIEW_RELEASE_GUARD_FAILED"); throw; }
                        });
                    }
                    catch (Exception error) { Record(error, "RELOCATION_PREVIEW_RELEASE_FAILED"); }
                    finally { batch.ReleaseSlots(); }
                    if (InputsAbsent)
                    {
                        try { successors.ReleaseAfterInputs(); }
                        catch (Exception error) { Record(error, "RELOCATION_PREVIEW_SUCCESSOR_RELEASE_FAILED"); }
                    }
                }
                if (successors.CommitWorldOwnershipConfirmed)
                {
                    try { successors.ReleaseAfterInputs(); }
                    catch (Exception error) { Record(error, "RELOCATION_PREVIEW_SUCCESSOR_RELEASE_FAILED"); }
                }
                if (!ResourcesReleased) FailureCode ??= "RELOCATION_PREVIEW_RELEASE_UNCONFIRMED";
                if (!successors.CommitEffectEntered && !successors.NativeWorkMayReadSource)
                {
                    try
                    {
                        RequireOriginal();
                        sourceUnchanged = true;
                        visibilityUnchanged = MarkersUnchanged();
                        Require(visibilityUnchanged, "RELOCATION_PREVIEW_SOURCE_VISIBILITY_CHANGED");
                    }
                    catch (Exception error) { Record(error, "RELOCATION_PREVIEW_FINAL_READBACK_FAILED"); }
                }
                if (FailureCode == null && verified) stage = submit ? "published" : "retired";
                Finished = true;
            }

            bool MarkersUnchanged()
            {
                var entities = source.EntityManager;
                return markersObserved && rootMarked == entities.HasComponent<BatchesUpdated>(original.Root)
                    && nodeMarked == entities.HasComponent<BatchesUpdated>(original.NodeEntity);
            }
        }

        private (EntityArchetype archetype, Dictionary<TypeIndex, ComponentType> types) PrepareArchetype(
            EntityArchetype prefab, bool building)
        {
            requireScope();
            var types = archetypes.Read(prefab, 256);
            TypeIndex updateFrame = TypeManager.GetTypeIndex<UpdateFrame>();
            Require(types.ContainsKey(updateFrame) == building, "RELOCATION_PREVIEW_UPDATE_FRAME_ROLE_CHANGED");
            foreach (TypeIndex type in types.Keys)
            {
                Require(!type.IsChunkComponent && !type.HasNativeContainer && !type.IsRefCounted,
                    "RELOCATION_PREVIEW_PREFAB_TYPE_UNSUPPORTED");
                Require(!type.IsSharedComponentType || building && type == updateFrame,
                    "RELOCATION_PREVIEW_PREFAB_SHARED_TYPE_UNSUPPORTED");
            }

            var scoped = NativeEntityArchetypes.CreateScoped(source.EntityManager, types);
            requireScope();
            NativeEntityArchetypes.RequireSame(archetypes.Read(scoped, MaximumTypes), types);
            return (scoped, types);
        }

        private void PrepareRole(int index, EntityArchetype archetype, Dictionary<TypeIndex, ComponentType> birthTypes)
        {
            Role role = roles[index];
            role.Archetype = archetype;
            role.Types = new Dictionary<TypeIndex, ComponentType>(birthTypes);
            int count = payload.WriteCount(index);
            Require(count > 0 && count <= 24, "RELOCATION_PREVIEW_WRITE_BUDGET");
            for (int writeIndex = 0; writeIndex < count; writeIndex++)
            {
                var write = payload.WriteAt(index, writeIndex);
                if (write.Kind == RelocationGenerationCommands.Set || write.Kind == RelocationGenerationCommands.Disable)
                    Require(birthTypes.ContainsKey(write.Type.TypeIndex), "RELOCATION_PREVIEW_SET_TARGET_UNAVAILABLE");
            }
        }

        private void Materialize(int index)
        {
            requireScope();
            Role role = roles[index];
            var entities = source.EntityManager;
            Entity target = batch.Create(index, role.Archetype);
            Require(batch.CreationReturned(index) && RequireTarget(index, allowDefaultScope: true) == target,
                "RELOCATION_PREVIEW_BIRTH_UNCONFIRMED");
            CompleteTrackedWriters();
            Require(entities.GetChunk(target).Archetype.Equals(role.Archetype), "RELOCATION_PREVIEW_BIRTH_TYPE_CHANGED");
            CheckEmptyBuffers(entities, target, role.Types!);
            Require(entities.GetComponentData<PrefabRef>(target).m_Prefab == Entity.Null
                && !entities.HasComponent<Temp>(target), "RELOCATION_PREVIEW_BIRTH_REFERENCES_NOT_DEFAULT");

            role.ScopeAttempted = true;
            entities.SetSharedComponent(target, new ObjectGenerationScope { Value = actor });
            role.ScopeReturned = true;
            RequireTarget(index);
            for (int writeIndex = 0; writeIndex < payload.WriteCount(index); writeIndex++)
                Write(index, payload.WriteAt(index, writeIndex));
        }

        private void Write(int index, RelocationPreviewWrite write)
        {
            Entity target = RequireTarget(index);
            var entities = source.EntityManager;
            Role role = roles[index];
            TypeIndex type = write.Type.TypeIndex;
            switch (write.Kind)
            {
                case RelocationGenerationCommands.Add:
                    role.PendingShape = type;
                    entities.AddComponent(target, write.Type);
                    RequireTarget(index);
                    Require(entities.HasComponent(target, write.Type), "RELOCATION_PREVIEW_ADD_TARGET_UNAVAILABLE");
                    role.Types![type] = write.Type;
                    role.PendingShape = null;
                    write.SetValue(entities, target);
                    break;
                case RelocationGenerationCommands.Remove:
                    role.PendingShape = type;
                    entities.RemoveComponent(target, write.Type);
                    RequireTarget(index);
                    Require(!entities.HasComponent(target, write.Type), "RELOCATION_PREVIEW_REMOVE_TARGET_RETAINED");
                    role.Types!.Remove(type);
                    role.PendingShape = null;
                    break;
                case RelocationGenerationCommands.Set:
                    write.SetValue(entities, target);
                    break;
                case RelocationGenerationCommands.Disable:
                    Require(entities.HasComponent(target, write.Type), "RELOCATION_PREVIEW_ENABLE_TARGET_UNAVAILABLE");
                    entities.SetComponentEnabled(target, write.Type, false);
                    break;
                default:
                    throw new InvalidOperationException("RELOCATION_PREVIEW_WRITE_KIND_UNSUPPORTED");
            }
            RequireTarget(index);
            role.WritesReturned++;
        }

        private void Verify(int index, bool ownerResolved = false, Entity subnet = default)
        {
            Entity target = RequireTarget(index);
            var entities = source.EntityManager;
            Role role = roles[index];
            var actual = archetypes.Read(entities.GetChunk(target).Archetype, MaximumTypes);
            NativeEntityArchetypes.RequireSame(actual, role.Types!);
            role.BufferCount = CheckEmptyBuffers(entities, target, actual, subnet);
            for (int writeIndex = 0; writeIndex < payload.WriteCount(index); writeIndex++)
            {
                var write = payload.WriteAt(index, writeIndex);
                TypeIndex type = write.Type.TypeIndex;
                if (ownerResolved && (type == TypeManager.GetTypeIndex<Owner>() || type == TypeManager.GetTypeIndex<OwnerDefinition>()))
                    continue;
                if (type == TypeManager.GetTypeIndex<Orphan>() && role.Composition != null)
                    Require(entities.GetComponentData<Orphan>(target).Equals(role.Composition.After),
                        "RELOCATION_PREVIEW_COMPOSITION_CHANGED");
                else Require(write.Matches(entities, target), "RELOCATION_PREVIEW_VALUE_CHANGED");
            }
            if (role.Geometry is NodeGeometry geometry)
                Require(NodeGeometryProducer.Same(entities.GetComponentData<NodeGeometry>(target), geometry),
                    "RELOCATION_PREVIEW_NODE_GEOMETRY_CHANGED");
            Entity expectedOwner = ownerResolved ? RequireTarget(0) : Entity.Null;
            Entity owner = entities.HasComponent<Owner>(target) ? entities.GetComponentData<Owner>(target).m_Owner : Entity.Null;
            Require(owner == expectedOwner, "RELOCATION_PREVIEW_OWNER_CHANGED");
            Require(entities.HasComponent<OwnerDefinition>(target) == (index == 2 && !ownerResolved),
                "RELOCATION_PREVIEW_OWNER_KEY_CHANGED");
            if (index == 0)
                Require(entities.GetComponentData<Building>(target).m_RoadEdge == Entity.Null, "RELOCATION_PREVIEW_ROAD_ALREADY_LINKED");
            RequireTarget(index);
            role.Verified = true;
        }

        private void ConfirmComposition(int index, CompositionSelectionReceipt selection)
        {
            var target = RequireTarget(index);
            var entities = source.EntityManager;
            Require(roles[index].Composition == null && ReferenceEquals(selection.Source, source)
                && selection.Node == target && selection.Prefab == payload.Definitions.Original.NodePrefab
                && selection.Actor == actor && selection.Before.m_Composition == Entity.Null
                && selection.ObservedSelection && selection.CacheUnchanged && selection.NativeEnteredReturnedJoined
                && entities.GetComponentData<Orphan>(target).Equals(selection.After),
                "RELOCATION_PREVIEW_COMPOSITION_UNCONFIRMED");
            selection.RequireCurrentScope();
            roles[index].Composition = selection;
        }

        private Entity RequireTarget(int index, bool allowDefaultScope = false)
        {
            requireScope();
            Require(source.IsCreated, "RELOCATION_PREVIEW_SOURCE_UNAVAILABLE");
            Entity target = batch.EntityAt(index);
            var original = payload.Definitions.Original;
            var entities = source.EntityManager;
            Require(target.Index > 0 && !original.IsFixtureEntity(target) && entities.Exists(target)
                && entities.HasComponent<Disabled>(target) && entities.HasComponent<ObjectGenerationScope>(target),
                "RELOCATION_PREVIEW_TARGET_IDENTITY_CHANGED");
            for (int other = 0; other < roles.Length; other++)
                Require(other == index || target != batch.EntityAt(other), "RELOCATION_PREVIEW_TARGETS_COLLIDED");
            int value = entities.GetSharedComponent<ObjectGenerationScope>(target).Value;
            Role role = roles[index];
            bool valid = role.ScopeReturned ? value == actor : value == 0 || role.ScopeAttempted && value == actor;
            Require(valid && (allowDefaultScope || role.ScopeReturned), "RELOCATION_PREVIEW_TARGET_SCOPE_CHANGED");
            if (index == 0)
            {
                Require(entities.HasComponent<UpdateFrame>(target) && entities.GetSharedComponent<UpdateFrame>(target).m_Index == 0,
                    "RELOCATION_PREVIEW_UPDATE_FRAME_CHANGED");
                role.DefaultUpdateFrameObserved = true;
            }
            return target;
        }

        private void ConfirmParentMarkers()
        {
            Require(successors.CancellationEntered, "RELOCATION_PREVIEW_CANCEL_TOOL_NOT_ENTERED");
            var entities = source.EntityManager;
            for (int i = 0; i < roles.Length; i++)
            {
                var role = roles[i];
                Entity target = RequireTarget(i);
                var expected = new Dictionary<TypeIndex, ComponentType>(role.Types!);
                NativeEntityArchetypes.AddExpected<Deleted>(expected);
                NativeEntityArchetypes.RequireSame(archetypes.Read(entities.GetChunk(target).Archetype, MaximumTypes), expected);
                role.Types = expected;
            }
            RequireOriginal();
        }

        private void DestroyCancelledGroup(Entity[] graph)
        {
            Require(!groupDestroyEntered && graph.Length == 9 && !successors.NativeWorkMayReadSource,
                "RELOCATION_PREVIEW_CANCEL_GROUP_UNAVAILABLE");
            CompleteTrackedWriters();
            successors.RequireCancelledAttachments();
            RequireOriginal();
            for (int i = 0; i < roles.Length; i++)
                Require(graph[i] == RequireTarget(i), "RELOCATION_PREVIEW_CANCEL_GROUP_IDENTITY_CHANGED");

            var entities = source.EntityManager;
            foreach (Entity target in graph)
                Require(entities.Exists(target) && entities.HasComponent<Deleted>(target),
                    "RELOCATION_PREVIEW_CANCEL_GROUP_MARKERS_INCOMPLETE");
            Verify(0, subnet: graph[2]);
            Verify(1);
            Verify(2, ownerResolved: true);

            // The stock SubNetReferences deletion branch skips a Deleted
            // owner. This one edge stays inside the exact retired group.
            using var group = new NativeArray<Entity>(graph, Allocator.Temp);
            requireScope();
            Require(PreviewCancelToolPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_CANCEL_GROUP_PATCH_UNQUALIFIED");
            groupDestroyEntered = true;
            entities.DestroyEntity(group);
            groupDestroyReturned = true;
            requireScope();
            foreach (Entity target in graph)
                Require(!entities.Exists(target), "RELOCATION_PREVIEW_CANCEL_GROUP_ENTITY_RETAINED");
            batch.ConfirmGroupRemoval(requireScope);
            successors.ConfirmCancelledGroupRemoval();
            groupAbsent = true;
        }

        private void Detach()
        {
            Require(!detachAttempted && successors.AttachmentsRetired, "RELOCATION_PREVIEW_UNLINK_UNAVAILABLE");
            detachAttempted = true;
            RequireUnownedOldNode();
            successors.RequireLinkedGraph();
            var entities = source.EntityManager;
            var node = RequireTarget(2);
            var role = roles[2];
            Require(entities.HasComponent<Created>(node) && !entities.HasComponent<Deleted>(node),
                "RELOCATION_PREVIEW_UNLINK_MARKERS_CHANGED");

            // The stock reference query gives Created priority over Deleted.
            // Retire that marker before the single removal notification.
            TypeIndex created = TypeManager.GetTypeIndex<Created>();
            role.PendingShape = created;
            entities.RemoveComponent<Created>(node);
            Require(!entities.HasComponent<Created>(RequireTarget(2)), "RELOCATION_PREVIEW_CREATED_RETAINED");
            role.Types!.Remove(created);
            role.PendingShape = null;
            successors.RequireLinkedGraph();

            var deleted = ComponentType.ReadWrite<Deleted>();
            role.PendingShape = deleted.TypeIndex;
            entities.AddComponent(node, deleted);
            Require(entities.HasComponent<Deleted>(RequireTarget(2)), "RELOCATION_PREVIEW_DELETED_UNCONFIRMED");
            role.Types[deleted.TypeIndex] = deleted;
            role.PendingShape = null;
            RequireUnownedOldNode();
            successors.Unlink();
        }

        private void RequireUnownedOldNode()
        {
            var oldNode = RequireTarget(1);
            var entities = source.EntityManager;
            Require(!entities.HasComponent<Owner>(oldNode) && !entities.HasComponent<OwnerDefinition>(oldNode),
                "RELOCATION_PREVIEW_OLD_NODE_OWNER_CHANGED");
        }

        private void RequireSafeRelease(int slot)
        {
            CompleteTrackedWriters();
            successors.RequireEmptyGraph();
            if (slot == 0 && successors.OwnerWriteMayHaveOccurred)
                Require(batch.AbsenceConfirmed(2), "RELOCATION_PREVIEW_LINKED_CHILD_ABSENCE_UNCONFIRMED");
            var entities = source.EntityManager;
            for (int index = 0; index < roles.Length; index++)
            {
                Entity target = batch.EntityAt(index);
                if (target == Entity.Null || !entities.Exists(target)) continue;
                RequireTarget(index, allowDefaultScope: true);
                Role role = roles[index];
                var actual = archetypes.Read(entities.GetChunk(target).Archetype, MaximumTypes);
                // A structural tail may fail after applying the type change.
                // The exact known prefix and the single in-flight change are safe.
                foreach (TypeIndex type in actual.Keys)
                    Require(role.Types!.ContainsKey(type) || role.PendingShape == type, "RELOCATION_PREVIEW_RELEASE_TYPE_CHANGED");
                foreach (TypeIndex type in role.Types!.Keys)
                    Require(actual.ContainsKey(type) || role.PendingShape == type, "RELOCATION_PREVIEW_RELEASE_TYPE_CHANGED");
                CheckEmptyBuffers(entities, target, actual);
                if (entities.HasComponent<Owner>(target))
                {
                    Entity owner = entities.GetComponentData<Owner>(target).m_Owner;
                    Require(owner == Entity.Null || index == 2 && successors.OwnerWriteMayHaveOccurred
                        && owner == batch.EntityAt(0) && entities.Exists(owner), "RELOCATION_PREVIEW_RELEASE_OWNER_LINKED");
                }
            }
            requireScope();
        }

        private void CompleteTrackedWriters()
        {
            requireScope();
            Require(source.IsCreated, "RELOCATION_PREVIEW_SOURCE_UNAVAILABLE");
            // Structural calls fence tracked jobs as well. Complete them before
            // checking lengths, since a writer can populate a linked group.
            source.EntityManager.CompleteAllTrackedJobs();
            requireScope();
        }

        private static int CheckEmptyBuffers(EntityManager entities, Entity target, Dictionary<TypeIndex, ComponentType> types,
            Entity subnet = default)
        {
            int count = 0;
            foreach (TypeIndex type in types.Keys)
            {
                if (!type.IsBuffer) continue;
                var storage = entities.GetStorageInfo(target);
                var handle = entities.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(TypeManager.GetType(type)));
                var buffer = storage.Chunk.GetUntypedBufferAccessor(ref handle);
                Require(storage.IndexInChunk >= 0 && storage.IndexInChunk < buffer.Length
                    && buffer.Length == storage.Chunk.Count && buffer.ElementSize == TypeManager.GetTypeInfo(type).ElementSize,
                    "RELOCATION_PREVIEW_BUFFER_LAYOUT_CHANGED");
                if (subnet != Entity.Null && type == TypeManager.GetTypeIndex<SubNet>())
                {
                    var entries = entities.GetBuffer<SubNet>(target, true);
                    Require(entries.Length == 1 && entries[0].m_SubNet == subnet, "RELOCATION_PREVIEW_CANCEL_SUBNET_CHANGED");
                }
                else Require(buffer.GetBufferLength(storage.IndexInChunk) == 0, "RELOCATION_PREVIEW_BUFFER_NOT_EMPTY");
                count++;
            }
            return count;
        }

        private void RequireOriginal() =>
            payload.Definitions.Original.RequireUnchanged(source, payload.Definitions.Original.Prefab, requireScope);

        private void Record(Exception error, string fallback)
        {
            string message = error.Message;
            bool qualified = message.Length <= 96 && (message.StartsWith("RELOCATION_PREVIEW_", StringComparison.Ordinal)
                || message.StartsWith("RELOCATION_DEFINITION_", StringComparison.Ordinal)
                || message.StartsWith("BUILDING_INIT_", StringComparison.Ordinal)
                || message.StartsWith("NODE_OWNER_", StringComparison.Ordinal)
                || message.StartsWith("COMPOSITION_PREFLIGHT_", StringComparison.Ordinal)
                || message.StartsWith("COMPOSITION_SELECTION_", StringComparison.Ordinal)
                || message.StartsWith("NODE_GEOMETRY_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_LANE_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_ROAD_", StringComparison.Ordinal)
                || message.StartsWith("NODE_COST_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_COST_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_VALIDATION_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_CANCEL_", StringComparison.Ordinal)
                || message.StartsWith("PREVIEW_COMMIT_", StringComparison.Ordinal)
                || message.StartsWith("SUBNET_REFERENCES_", StringComparison.Ordinal)
                || message.StartsWith("SUBNET_RELOCATION_", StringComparison.Ordinal)
                || message.StartsWith("SUBOBJECT_", StringComparison.Ordinal)
                || message.StartsWith("RELOCATION_SUBOBJECT_", StringComparison.Ordinal)
                || message.StartsWith("GENERATED_SUBOBJECT_", StringComparison.Ordinal));
            for (int index = 0; qualified && index < message.Length; index++)
            {
                char character = message[index];
                if (character == '_' || character >= 'A' && character <= 'Z' || character >= '0' && character <= '9') continue;
                qualified = false;
                break;
            }
            FailureCode ??= qualified ? message : fallback;
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["resourcesReleased"] = ResourcesReleased, ["stage"] = stage,
            ["sourceUnchanged"] = sourceUnchanged, ["visibilityUnchanged"] = visibilityUnchanged,
            ["unknownCreation"] = batch.UnknownCreation, ["failureCode"] = FailureCode,
            ["successors"] = successors.Snapshot(),
            ["groupDestroyEntered"] = groupDestroyEntered, ["groupDestroyReturned"] = groupDestroyReturned,
            ["groupAbsent"] = groupAbsent,
            ["root"] = RoleSnapshot(0), ["oldNode"] = RoleSnapshot(1), ["newNode"] = RoleSnapshot(2)
        };

        private JObject RoleSnapshot(int index)
        {
            Entity target = batch.EntityAt(index);
            Role role = roles[index];
            return new JObject {
                ["id"] = new JArray(target.Index, target.Version), ["writes"] = role.WritesReturned,
                ["bufferCount"] = role.BufferCount, ["valuesVerified"] = role.Verified,
                ["created"] = batch.CreationReturned(index), ["destroyed"] = batch.DestructionReturned(index) || groupDestroyReturned && batch.AbsenceConfirmed(index),
                ["defaultUpdateFrameObserved"] = role.DefaultUpdateFrameObserved,
                ["absenceConfirmed"] = batch.AbsenceConfirmed(index)
            };
        }

        private sealed class Role
        {
            internal CompositionSelectionReceipt? Composition;
            internal NodeGeometry? Geometry;
            internal EntityArchetype Archetype;
            internal Dictionary<TypeIndex, ComponentType>? Types;
            internal TypeIndex? PendingShape;
            internal int WritesReturned, BufferCount;
            internal bool ScopeAttempted, ScopeReturned, Verified, DefaultUpdateFrameObserved;
        }

        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
