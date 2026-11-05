#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using EcsEntity = Unity.Entities.Entity;
using NativeNode = Game.Net.Node;
using NativeElevation = Game.Net.Elevation;
using NativeFixed = Game.Net.Fixed;
using EditorContainer = Game.Tools.EditorContainer;
using SubObject = Game.Objects.SubObject;

namespace SkylinesLink.Mod.Native
{
    /// <summary>One exact, Disabled node; no generated lanes or linked entities are owned here.</summary>
    internal sealed class GeneratedNodeEntities
    {
        private readonly World source;
        private readonly GeneratedNodePayload payload;
        private readonly int actor;
        private readonly NativeEntityBatch batch;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private readonly OwnerDefinition ownerKey;
        private readonly bool hasExpectedOrphan;
        private Game.Net.Orphan currentExpectedOrphan;
        private readonly List<string> writeOrder = new List<string>(14);
        private Action? requireScope, parentReleaseGuard;
        private Dictionary<TypeIndex, ComponentType>? expectedTypes;
        private JObject? initialValues, birthBuffers, latestBuffers;
        private EcsEntity linkedRoot;
        private bool createCalled, createReturned, releaseCalled, scopeReturned;
        private bool birthTypesVerified, finalTypesVerified, birthEnableablesVerified, valuesVerified;
        private bool birthOwnerNull, ownerLinkAttempted, rootLinkConfirmed, ownerDefStillPresent, postLinkValuesVerified;
        private int originalTypeCount, finalTypeCount, enableableCount, writesAttempted, writesReturned, nonEmptyBufferLength;
        private bool removeReturned, serviceUpgradeWasPresent;
        private bool markerExpiryAttempted, markerPrimitiveCalled, markerExpiryReturned, markerExpiryPassed;
        private bool discardAttempted, discardReturned, discardObserved, discardValuesVerified;
        private int markerMaskBefore = -1, markerMaskAfter = -1, currentTypeCount;
        private string? currentTypeDigest;
        private bool compositionConfirmationAttempted, compositionObserved, compositionConfirmed;
        private bool compositionObservedSelection, compositionNeedsPublication;
        private Game.Net.Orphan? compositionBefore, compositionAfter;
        private string? compositionFailure, compositionExceptionType;
        private string stage = "not-started";
        private string? failureCode, exceptionType, releaseFailure, unsupportedBuffer, nonEmptyBuffer, finalTypeDigest, orderDigest;

        internal GeneratedNodeEntities(World source, GeneratedNodePayload payload, int actor)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
            if (!ReferenceEquals(source, payload.Source) || actor <= 0)
                throw new InvalidOperationException("GENERATED_NODE_SOURCE_REJECTED");
            this.actor = actor; batch = new NativeEntityBatch(source, 1);
            bool foundOwner = false;
            for (int index = 0; index < payload.WriteCount; index++)
            {
                if (payload.WriteAt(index).Kind == GeneratedNodeWriteKind.OwnerDefinition)
                { ownerKey = Value<OwnerDefinition>(payload.WriteAt(index)); foundOwner = true; }
                if (payload.WriteAt(index).Kind == GeneratedNodeWriteKind.Orphan)
                { currentExpectedOrphan = Value<Game.Net.Orphan>(payload.WriteAt(index)); hasExpectedOrphan = true; }
            }
            Require(foundOwner, "GENERATED_NODE_OWNER_KEY_MISSING");
        }

        internal EcsEntity Entity => batch.EntityAt(0);
        internal bool CreateReturned => createReturned;
        internal bool UnknownCreation => batch.UnknownCreation;
        internal bool ResourcesReleased => batch.ResourcesReleased && archetypes.ResourcesReleased;
        internal bool AbsenceConfirmed => batch.AbsenceConfirmed(0);
        internal bool MarkerExpiryPassed => markerExpiryPassed;
        internal bool DiscardReady => discardReturned && discardObserved && discardValuesVerified;
        internal bool CompositionConfirmed => compositionConfirmed;

        internal void BindParentReleaseGuard(Action guard)
        {
            RequireMainThread();
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            Require(!releaseCalled && parentReleaseGuard == null, "GENERATED_NODE_PARENT_GUARD_ALREADY_BOUND");
            parentReleaseGuard = guard;
        }

        internal void Create(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(!createCalled && !releaseCalled, "GENERATED_NODE_ALREADY_USED"); createCalled = true;
            requireScope = requireCurrentScope;
            try
            {
                stage = "check-source-and-archetype"; requireCurrentScope();
                Require(source.IsCreated && ReferenceEquals(source, payload.Source) && payload.WriteCount >= 7
                    && payload.WriteCount <= 14 && payload.SourceArchetype.Valid, "GENERATED_NODE_PAYLOAD_UNAVAILABLE");
                originalTypeCount = payload.SourceArchetype.TypesCount;
                var types = archetypes.Read(payload.SourceArchetype, 256);
                Require(types.ContainsKey(TypeManager.GetTypeIndex<Simulate>())
                    && types.ContainsKey(TypeManager.GetTypeIndex<NativeNode>())
                    && types.ContainsKey(TypeManager.GetTypeIndex<PrefabRef>()), "GENERATED_NODE_SET_TARGET_MISSING");
                RequireKnownBuffers(types);
                RequireSetTargets(types);
                EntityManager entities = source.EntityManager;
                stage = "create-scoped-archetype";
                EntityArchetype scoped = NativeEntityArchetypes.CreateScoped(entities, types);
                NativeEntityArchetypes.RequireSame(archetypes.Read(scoped, 258), types);
                requireCurrentScope();
                stage = "create-disabled-node"; batch.Create(0, scoped);
                Require(batch.CreationReturned(0) && Entity != EcsEntity.Null, "GENERATED_NODE_HANDLE_UNCONFIRMED");
                requireCurrentScope();
                NativeEntityArchetypes.RequireSame(archetypes.Read(entities.GetChunk(Entity).Archetype, 258), types);
                birthTypesVerified = true;
                birthBuffers = ReadEmptyBuffers(entities, types);
                foreach (ComponentType type in types.Values)
                    if (type.TypeIndex.IsEnableable)
                    { Require(entities.IsComponentEnabled(Entity, type), "GENERATED_NODE_BIRTH_ENABLE_STATE_CHANGED"); enableableCount++; }
                birthEnableablesVerified = true;

                stage = "assign-scope";
                entities.SetSharedComponent(Entity, new ObjectGenerationScope { Value = actor }); scopeReturned = true;
                requireCurrentScope();
                for (int index = 0; index < payload.WriteCount; index++)
                {
                    GeneratedNodeWrite write = payload.WriteAt(index);
                    stage = "write-" + write.Kind; writeOrder.Add(write.Kind.ToString()); writesAttempted++;
                    Write(entities, write); writesReturned++;
                    ApplyExpected(types, write.Kind);
                    requireCurrentScope();
                }
                orderDigest = NativeEntityArchetypes.Digest(string.Join("|", writeOrder));
                stage = "verify-native-values";
                RequireScope(entities);
                initialValues = ReadAndVerify(entities, EcsEntity.Null, allowRemovedOwnerDefinition: false);
                birthOwnerNull = entities.GetComponentData<Owner>(Entity).m_Owner == EcsEntity.Null;
                Require(birthOwnerNull, "GENERATED_NODE_BIRTH_OWNER_NOT_NULL");
                valuesVerified = true; ownerDefStillPresent = true;
                var actualTypes = archetypes.Read(entities.GetChunk(Entity).Archetype, 272);
                NativeEntityArchetypes.RequireSame(actualTypes, types);
                latestBuffers = ReadEmptyBuffers(entities, actualTypes);
                expectedTypes = types; finalTypeCount = actualTypes.Count + 1;
                finalTypeDigest = NativeEntityArchetypes.TypeDigest(actualTypes); finalTypesVerified = true;
                RecordCurrentTypes(actualTypes);
                requireCurrentScope(); createReturned = true; stage = "created";
            }
            catch (Exception error) { Record(error, "GENERATED_NODE_CREATE_FAILED"); throw; }
        }

        private void RequireSetTargets(Dictionary<TypeIndex, ComponentType> types)
        {
            for (int index = 0; index < payload.WriteCount; index++)
            {
                GeneratedNodeWriteKind kind = payload.WriteAt(index).Kind;
                if (kind == GeneratedNodeWriteKind.RandomSeed)
                    Require(types.ContainsKey(TypeManager.GetTypeIndex<PseudoRandomSeed>()), "GENERATED_NODE_SEED_TARGET_MISSING");
                if (kind == GeneratedNodeWriteKind.EditorContainer)
                    Require(types.ContainsKey(TypeManager.GetTypeIndex<EditorContainer>()), "GENERATED_NODE_EDITOR_TARGET_MISSING");
            }
        }

        private void Write(EntityManager entities, GeneratedNodeWrite write)
        {
            switch (write.Kind)
            {
                case GeneratedNodeWriteKind.Prefab:
                    PrefabRef prefab = Value<PrefabRef>(write);
                    Require(prefab.m_Prefab != EcsEntity.Null && prefab.m_Prefab.Index >= 0 && entities.Exists(prefab.m_Prefab)
                        && entities.HasComponent<PrefabData>(prefab.m_Prefab) && entities.IsComponentEnabled<PrefabData>(prefab.m_Prefab)
                        && !entities.HasComponent<Deleted>(prefab.m_Prefab), "GENERATED_NODE_PREFAB_UNAVAILABLE");
                    entities.SetComponentData(Entity, prefab); break;
                case GeneratedNodeWriteKind.RemoveServiceUpgrade:
                    serviceUpgradeWasPresent = entities.HasComponent<Game.Buildings.ServiceUpgrade>(Entity);
                    entities.RemoveComponent<Game.Buildings.ServiceUpgrade>(Entity); removeReturned = true; break;
                case GeneratedNodeWriteKind.Node: entities.SetComponentData(Entity, Value<NativeNode>(write)); break;
                case GeneratedNodeWriteKind.Orphan: entities.AddComponentData(Entity, Value<Game.Net.Orphan>(write)); break;
                case GeneratedNodeWriteKind.RandomSeed: entities.SetComponentData(Entity, Value<PseudoRandomSeed>(write)); break;
                case GeneratedNodeWriteKind.Standalone: entities.AddComponentData(Entity, Value<Game.Net.Standalone>(write)); break;
                case GeneratedNodeWriteKind.Elevation: entities.AddComponentData(Entity, Value<NativeElevation>(write)); break;
                case GeneratedNodeWriteKind.Fixed: entities.AddComponentData(Entity, Value<NativeFixed>(write)); break;
                case GeneratedNodeWriteKind.LocalConnect: entities.AddComponentData(Entity, Value<Game.Net.LocalConnect>(write)); break;
                case GeneratedNodeWriteKind.Owner:
                    Owner owner = Value<Owner>(write); Require(owner.m_Owner == EcsEntity.Null, "GENERATED_NODE_INITIAL_OWNER_INVALID");
                    entities.AddComponentData(Entity, owner); break;
                case GeneratedNodeWriteKind.OwnerDefinition:
                    Require(Value<OwnerDefinition>(write).Equals(ownerKey), "GENERATED_NODE_OWNER_KEY_CHANGED");
                    entities.AddComponentData(Entity, Value<OwnerDefinition>(write)); break;
                case GeneratedNodeWriteKind.OutsideConnection: entities.AddComponentData(Entity, Value<Game.Net.OutsideConnection>(write)); break;
                case GeneratedNodeWriteKind.EditorContainer: entities.SetComponentData(Entity, Value<EditorContainer>(write)); break;
                case GeneratedNodeWriteKind.Temp: entities.AddComponentData(Entity, Value<Temp>(write)); break;
                default: throw new Rejection("GENERATED_NODE_WRITE_UNKNOWN");
            }
        }

        private static void ApplyExpected(Dictionary<TypeIndex, ComponentType> types, GeneratedNodeWriteKind kind)
        {
            switch (kind)
            {
                case GeneratedNodeWriteKind.RemoveServiceUpgrade: types.Remove(TypeManager.GetTypeIndex<Game.Buildings.ServiceUpgrade>()); break;
                case GeneratedNodeWriteKind.Orphan: NativeEntityArchetypes.AddExpected<Game.Net.Orphan>(types); break;
                case GeneratedNodeWriteKind.Standalone: NativeEntityArchetypes.AddExpected<Game.Net.Standalone>(types); break;
                case GeneratedNodeWriteKind.Elevation: NativeEntityArchetypes.AddExpected<NativeElevation>(types); break;
                case GeneratedNodeWriteKind.Fixed: NativeEntityArchetypes.AddExpected<NativeFixed>(types); break;
                case GeneratedNodeWriteKind.LocalConnect: NativeEntityArchetypes.AddExpected<Game.Net.LocalConnect>(types); break;
                case GeneratedNodeWriteKind.Owner: NativeEntityArchetypes.AddExpected<Owner>(types); break;
                case GeneratedNodeWriteKind.OwnerDefinition: NativeEntityArchetypes.AddExpected<OwnerDefinition>(types); break;
                case GeneratedNodeWriteKind.OutsideConnection: NativeEntityArchetypes.AddExpected<Game.Net.OutsideConnection>(types); break;
                case GeneratedNodeWriteKind.Temp: NativeEntityArchetypes.AddExpected<Temp>(types); break;
            }
        }

        private JObject ReadAndVerify(EntityManager entities, EcsEntity expectedOwner, bool allowRemovedOwnerDefinition,
            Game.Net.Orphan? expectedOrphan = null)
        {
            var result = new JObject();
            for (int index = 0; index < payload.WriteCount; index++)
            {
                GeneratedNodeWrite write = payload.WriteAt(index); JToken data;
                switch (write.Kind)
                {
                    case GeneratedNodeWriteKind.Prefab: data = EntityValue(Read<PrefabRef>(entities, write).m_Prefab); break;
                    case GeneratedNodeWriteKind.RemoveServiceUpgrade:
                        Require(!entities.HasComponent<Game.Buildings.ServiceUpgrade>(Entity), "GENERATED_NODE_SERVICE_UPGRADE_RETAINED");
                        data = new JObject { ["absent"] = true }; break;
                    case GeneratedNodeWriteKind.Node:
                        NativeNode node = Read<NativeNode>(entities, write);
                        data = new JObject { ["position"] = Vector(node.m_Position), ["rotation"] = Rotation(node.m_Rotation) }; break;
                    case GeneratedNodeWriteKind.Orphan:
                        Require(hasExpectedOrphan, "GENERATED_NODE_ORPHAN_EXPECTATION_MISSING");
                        Game.Net.Orphan orphan = entities.GetComponentData<Game.Net.Orphan>(Entity);
                        Require(orphan.Equals(expectedOrphan ?? currentExpectedOrphan), "GENERATED_NODE_ORPHAN_VALUE_CHANGED");
                        data = EntityValue(orphan.m_Composition); break;
                    case GeneratedNodeWriteKind.RandomSeed: data = new JValue((int)Read<PseudoRandomSeed>(entities, write).m_Seed); break;
                    case GeneratedNodeWriteKind.Standalone:
                        Require(entities.HasComponent<Game.Net.Standalone>(Entity), "GENERATED_NODE_STANDALONE_MISSING");
                        data = new JObject { ["present"] = true }; break;
                    case GeneratedNodeWriteKind.Elevation: data = Pair(Read<NativeElevation>(entities, write).m_Elevation); break;
                    case GeneratedNodeWriteKind.Fixed: data = new JValue(Read<NativeFixed>(entities, write).m_Index); break;
                    case GeneratedNodeWriteKind.LocalConnect:
                        Require(entities.HasComponent<Game.Net.LocalConnect>(Entity), "GENERATED_NODE_LOCAL_CONNECT_MISSING");
                        data = new JObject { ["present"] = true }; break;
                    case GeneratedNodeWriteKind.Owner:
                        Owner owner = entities.GetComponentData<Owner>(Entity);
                        Require(expectedOwner == EcsEntity.Null ? owner.Equals(Value<Owner>(write)) : owner.m_Owner == expectedOwner,
                            "GENERATED_NODE_OWNER_VALUE_CHANGED"); data = EntityValue(owner.m_Owner); break;
                    case GeneratedNodeWriteKind.OwnerDefinition:
                        bool present = entities.HasComponent<OwnerDefinition>(Entity);
                        Require(present || allowRemovedOwnerDefinition, "GENERATED_NODE_OWNER_DEFINITION_MISSING");
                        if (present)
                        {
                            OwnerDefinition key = Read<OwnerDefinition>(entities, write);
                            data = new JObject { ["present"] = true, ["prefab"] = EntityValue(key.m_Prefab),
                                ["position"] = Vector(key.m_Position), ["rotation"] = Rotation(key.m_Rotation) };
                        }
                        else data = new JObject { ["present"] = false };
                        break;
                    case GeneratedNodeWriteKind.OutsideConnection: data = new JObject { ["delay"] = Read<Game.Net.OutsideConnection>(entities, write).m_Delay }; break;
                    case GeneratedNodeWriteKind.EditorContainer:
                        EditorContainer editor = Read<EditorContainer>(entities, write);
                        data = new JObject { ["prefab"] = EntityValue(editor.m_Prefab), ["scale"] = Vector(editor.m_Scale),
                            ["intensity"] = editor.m_Intensity, ["groupIndex"] = editor.m_GroupIndex }; break;
                    case GeneratedNodeWriteKind.Temp:
                        Temp temp = Read<Temp>(entities, write);
                        data = new JObject { ["original"] = EntityValue(temp.m_Original), ["flags"] = (uint)temp.m_Flags,
                            ["curvePosition"] = temp.m_CurvePosition, ["value"] = temp.m_Value, ["cost"] = temp.m_Cost }; break;
                    default: throw new Rejection("GENERATED_NODE_WRITE_UNKNOWN");
                }
                result[write.Kind.ToString()] = data;
            }
            return result;
        }

        private T Read<T>(EntityManager entities, GeneratedNodeWrite write) where T : unmanaged, IComponentData
        {
            T actual = entities.GetComponentData<T>(Entity);
            Require(actual.Equals(Value<T>(write)), "GENERATED_NODE_VALUE_CHANGED"); return actual;
        }

        // Called only after the parent has joined the Resolver and physically
        // observed this exact root link. Link proof is separate from successful
        // removal of OwnerDefinition by that Resolver's barrier playback.
        internal void ConfirmNativeOwnerLink(EcsEntity root)
        {
            RequireMainThread();
            Require(createReturned && !releaseCalled && !ownerLinkAttempted, "GENERATED_NODE_OWNER_CONFIRMATION_REJECTED");
            ownerLinkAttempted = true;
            try
            {
                requireScope!.Invoke();
                Require(source.IsCreated && root != EcsEntity.Null && root.Index >= 0 && root != Entity,
                    "GENERATED_NODE_LINK_ROOT_INVALID");
                EntityManager entities = source.EntityManager;
                Require(entities.Exists(Entity) && entities.Exists(root), "GENERATED_NODE_LINK_ENTITY_UNAVAILABLE");
                RequireScope(entities);
                Require(entities.HasComponent<Unity.Entities.Disabled>(root) && entities.HasComponent<ObjectGenerationScope>(root)
                    && entities.GetSharedComponent<ObjectGenerationScope>(root).Value == actor
                    && entities.HasComponent<PrefabRef>(root) && entities.GetComponentData<PrefabRef>(root).m_Prefab == ownerKey.m_Prefab
                    && entities.HasComponent<Transform>(root), "GENERATED_NODE_LINK_ROOT_UNQUALIFIED");
                Transform transform = entities.GetComponentData<Transform>(root);
                Require(transform.m_Position.Equals(ownerKey.m_Position) && transform.m_Rotation.Equals(ownerKey.m_Rotation)
                    && entities.GetComponentData<Owner>(Entity).m_Owner == root, "GENERATED_NODE_LINK_VALUE_CHANGED");
                bool keyPresent = entities.HasComponent<OwnerDefinition>(Entity);
                Require(!keyPresent || entities.GetComponentData<OwnerDefinition>(Entity).Equals(ownerKey),
                    "GENERATED_NODE_LINK_KEY_CHANGED");
                // Preserve the proved association even if a later, independent
                // value/buffer audit fails. This is not Resolver success.
                linkedRoot = root; rootLinkConfirmed = true; ownerDefStillPresent = keyPresent;
                var expected = new Dictionary<TypeIndex, ComponentType>(expectedTypes!);
                if (!keyPresent) expected.Remove(TypeManager.GetTypeIndex<OwnerDefinition>());
                expectedTypes = expected;
                var actual = archetypes.Read(entities.GetChunk(Entity).Archetype, 272);
                NativeEntityArchetypes.RequireSame(actual, expected);
                latestBuffers = ReadEmptyBuffers(entities, actual);
                ReadAndVerify(entities, root, allowRemovedOwnerDefinition: true);
                RecordCurrentTypes(actual);
                postLinkValuesVerified = true;
            }
            catch (Exception error) { Record(error, "GENERATED_NODE_OWNER_CONFIRMATION_FAILED"); throw; }
        }

        private void RequireKnownBuffers(Dictionary<TypeIndex, ComponentType> types)
        {
            foreach (TypeIndex type in types.Keys)
                if (type.IsBuffer && type != TypeManager.GetTypeIndex<Game.Net.ConnectedEdge>()
                    && type != TypeManager.GetTypeIndex<Game.Net.SubLane>() && type != TypeManager.GetTypeIndex<SubObject>()
                    && type != TypeManager.GetTypeIndex<MeshBatch>() && type != TypeManager.GetTypeIndex<LinkedEntityGroup>())
                {
                    unsupportedBuffer = TypeManager.GetType(type).FullName ?? TypeManager.GetType(type).Name;
                    throw new Rejection("GENERATED_NODE_BUFFER_UNSUPPORTED");
                }
        }
        private JObject ReadEmptyBuffers(EntityManager entities, Dictionary<TypeIndex, ComponentType> types)
        {
            RequireKnownBuffers(types);
            var result = new JObject();
            CheckBuffer<Game.Net.ConnectedEdge>(entities, types, result);
            CheckBuffer<Game.Net.SubLane>(entities, types, result);
            CheckBuffer<SubObject>(entities, types, result);
            CheckBuffer<MeshBatch>(entities, types, result);
            CheckBuffer<LinkedEntityGroup>(entities, types, result);
            return result;
        }
        private void CheckBuffer<T>(EntityManager entities, Dictionary<TypeIndex, ComponentType> types, JObject result)
            where T : unmanaged, IBufferElementData
        {
            if (!types.ContainsKey(TypeManager.GetTypeIndex<T>())) return;
            int length = entities.GetBuffer<T>(Entity, isReadOnly: true).Length;
            result[typeof(T).FullName!] = length;
            if (length != 0) { nonEmptyBuffer = typeof(T).FullName; nonEmptyBufferLength = length; }
            Require(length == 0, "GENERATED_NODE_BUFFER_NOT_EMPTY");
        }
        private void RequireScope(EntityManager entities) => Require(entities.HasComponent<Unity.Entities.Disabled>(Entity)
            && entities.HasComponent<ObjectGenerationScope>(Entity)
            && entities.GetSharedComponent<ObjectGenerationScope>(Entity).Value == actor, "GENERATED_NODE_SCOPE_CHANGED");

        // Called after the linked children are physically gone. Only this
        // qualified native delta may replace the node's concrete expected Orphan.
        internal void ConfirmNativeComposition(CompositionSelectionReceipt receipt)
        {
            RequireMainThread();
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            Require(createReturned && rootLinkConfirmed && !ownerDefStillPresent && hasExpectedOrphan && !releaseCalled
                && !markerExpiryAttempted && !discardAttempted && !compositionConfirmationAttempted,
                "GENERATED_NODE_COMPOSITION_CONFIRMATION_NOT_READY");
            compositionConfirmationAttempted = true;
            compositionBefore = receipt.Before; compositionAfter = receipt.After;
            compositionObservedSelection = receipt.ObservedSelection; compositionNeedsPublication = receipt.NeedsPublication;
            try
            {
                stage = "confirm-native-composition";
                requireScope!(); receipt.RequireCurrentScope(); requireScope!();
                Require(ReferenceEquals(receipt.Source, source) && receipt.Node == Entity && receipt.Actor == actor
                    && receipt.NativeEnteredReturnedJoined && receipt.CacheUnchanged
                    && receipt.Before.Equals(currentExpectedOrphan), "GENERATED_NODE_COMPOSITION_RECEIPT_CHANGED");
                Require(source.IsCreated, "GENERATED_NODE_SOURCE_UNAVAILABLE");
                EntityManager entities = source.EntityManager; RequireLiveNode(entities);
                Require(entities.GetComponentData<PrefabRef>(Entity).m_Prefab == receipt.Prefab,
                    "GENERATED_NODE_COMPOSITION_PREFAB_CHANGED");
                requireScope!(); receipt.RequireCurrentScope();
                compositionObserved = entities.GetComponentData<Game.Net.Orphan>(Entity).Equals(receipt.After);
                Require(compositionObserved, "GENERATED_NODE_COMPOSITION_RESULT_CHANGED");
                // The native job has already changed Orphan on a cache hit.
                // Audit against this one candidate value before committing it.
                VerifyCompletedNode(receipt.After);
                receipt.RequireCurrentScope(); requireScope!();
                currentExpectedOrphan = receipt.After; compositionConfirmed = true; stage = "composition-reference-confirmed";
            }
            catch (Exception error)
            {
                compositionFailure = error is Rejection rejected ? rejected.Code : "GENERATED_NODE_COMPOSITION_CONFIRMATION_FAILED";
                compositionExceptionType = error.GetType().Name;
                Record(error, "GENERATED_NODE_COMPOSITION_CONFIRMATION_FAILED"); throw;
            }
        }

        // Use the normal cleanup's exact six-marker rule through one public
        // primitive. There is no intervening empty-destroy structural tail.
        internal void ExpireModificationMarkers()
        {
            RequireMainThread();
            Require(createReturned && rootLinkConfirmed && !ownerDefStillPresent && !releaseCalled
                && !markerExpiryAttempted && !discardAttempted && (!compositionConfirmationAttempted || compositionConfirmed),
                "GENERATED_NODE_MARKER_EXPIRY_NOT_READY");
            markerExpiryAttempted = true;
            try
            {
                stage = "verify-before-marker-expiry";
                VerifyCompletedNode();
                EntityManager entities = source.EntityManager;
                Require(!entities.HasComponent<OwnerDefinition>(Entity) && !entities.HasComponent<Deleted>(Entity)
                    && entities.HasComponent<Created>(Entity), "GENERATED_NODE_MARKER_EXPIRY_INPUT_CHANGED");
                markerMaskBefore = ModificationMarkerMask(entities, Entity);
                requireScope!();
                var afterTypes = new Dictionary<TypeIndex, ComponentType>(expectedTypes!);
                ComponentTypeSet markers = ModificationMarkerTypes();
                for (int index = 0; index < markers.Length; index++) afterTypes.Remove(markers.GetTypeIndex(index));
                RequireLinkedSubNet(entities);
                stage = "remove-native-modification-markers"; markerPrimitiveCalled = true;
                entities.RemoveComponent(Entity, in markers); markerExpiryReturned = true;
                requireScope!();
                RequireLiveNode(entities);
                var actual = archetypes.Read(entities.GetChunk(Entity).Archetype, 272);
                requireScope!(); NativeEntityArchetypes.RequireSame(actual, afterTypes);
                markerMaskAfter = ModificationMarkerMask(entities, Entity);
                Require(markerMaskAfter == 0, "GENERATED_NODE_MARKERS_RETAINED");
                expectedTypes = afterTypes;
                VerifyCompletedNode(); RecordCurrentTypes(actual);
                markerExpiryPassed = true; stage = "markers-expired";
            }
            catch (Exception error) { Record(error, "GENERATED_NODE_MARKER_EXPIRY_FAILED"); throw; }
        }

        internal void RequestDiscard()
        {
            RequireMainThread();
            Require(markerExpiryPassed && createReturned && !releaseCalled && !discardAttempted,
                "GENERATED_NODE_DISCARD_NOT_READY");
            discardAttempted = true;
            try
            {
                stage = "verify-before-discard"; VerifyCompletedNode();
                EntityManager entities = source.EntityManager;
                Require(ModificationMarkerMask(entities, Entity) == 0
                    && !entities.HasComponent<Deleted>(Entity), "GENERATED_NODE_DISCARD_INPUT_CHANGED");
                requireScope!(); RequireLiveNode(entities);
                // Keep this owned marker through native unlink and destruction.
                // A failed structural tail is never retried as a new request.
                RequireLinkedSubNet(entities);
                NativeEntityArchetypes.AddExpected<Deleted>(expectedTypes!);
                stage = "request-node-discard";
                entities.AddComponent<Deleted>(Entity); discardReturned = true;
                requireScope!(); RequireLiveNode(entities);
                discardObserved = entities.HasComponent<Deleted>(Entity);
                Require(discardObserved, "GENERATED_NODE_DISCARD_NOT_OBSERVED");
                VerifyCompletedNode();
                RecordCurrentTypes(expectedTypes!); discardValuesVerified = true; stage = "discard-requested";
            }
            catch (Exception error) { Record(error, "GENERATED_NODE_DISCARD_FAILED"); throw; }
        }

        private void RequireLiveNode(EntityManager entities)
        {
            requireScope!();
            Require(source.IsCreated && Entity != EcsEntity.Null && entities.Exists(Entity), "GENERATED_NODE_ENTITY_UNAVAILABLE");
            RequireScope(entities); requireScope!();
            if (rootLinkConfirmed)
            {
                Require(linkedRoot != EcsEntity.Null && entities.Exists(linkedRoot)
                    && entities.HasComponent<Unity.Entities.Disabled>(linkedRoot)
                    && entities.HasComponent<ObjectGenerationScope>(linkedRoot)
                    && entities.GetSharedComponent<ObjectGenerationScope>(linkedRoot).Value == actor,
                    "GENERATED_NODE_PARENT_CHANGED");
                requireScope!();
            }
        }

        private void RequireLinkedSubNet(EntityManager entities)
        {
            RequireLiveNode(entities);
            Require(!entities.HasComponent<Deleted>(linkedRoot) && entities.HasBuffer<Game.Net.SubNet>(linkedRoot)
                && entities.HasBuffer<SubObject>(linkedRoot), "GENERATED_NODE_MARKER_PARENT_CHANGED");
            var nets = entities.GetBuffer<Game.Net.SubNet>(linkedRoot, true); requireScope!();
            Require(nets.Length == 1 && nets[0].m_SubNet == Entity, "GENERATED_NODE_MARKER_GRAPH_CHANGED");
            Require(entities.GetBuffer<SubObject>(linkedRoot, true).Length == 0, "GENERATED_NODE_PARENT_CHILDREN_RETAINED");
            requireScope!();
        }

        private static ComponentTypeSet ModificationMarkerTypes() => new ComponentTypeSet(new[] {
            ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>(), ComponentType.ReadWrite<Applied>(),
            ComponentType.ReadWrite<EffectsUpdated>(), ComponentType.ReadWrite<BatchesUpdated>(), ComponentType.ReadWrite<PathfindUpdated>() });

        private static int ModificationMarkerMask(EntityManager entities, EcsEntity entity) => (entities.HasComponent<Created>(entity) ? 1 : 0)
            | (entities.HasComponent<Updated>(entity) ? 2 : 0) | (entities.HasComponent<Applied>(entity) ? 4 : 0)
            | (entities.HasComponent<EffectsUpdated>(entity) ? 8 : 0) | (entities.HasComponent<BatchesUpdated>(entity) ? 16 : 0)
            | (entities.HasComponent<PathfindUpdated>(entity) ? 32 : 0);

        private void VerifyCompletedNode(Game.Net.Orphan? expectedOrphan = null)
        {
            requireScope!(); Require(source.IsCreated, "GENERATED_NODE_SOURCE_UNAVAILABLE");
            EntityManager entities = source.EntityManager; RequireLiveNode(entities);
            var actual = archetypes.Read(entities.GetChunk(Entity).Archetype, 272); requireScope!();
            NativeEntityArchetypes.RequireSame(actual, expectedTypes!);
            bool keyPresent = entities.HasComponent<OwnerDefinition>(Entity);
            Require(keyPresent == ownerDefStillPresent, "GENERATED_NODE_OWNER_DEFINITION_STATE_CHANGED");
            ReadAndVerify(entities, rootLinkConfirmed ? linkedRoot : EcsEntity.Null,
                allowRemovedOwnerDefinition: !ownerDefStillPresent, expectedOrphan: expectedOrphan);
            latestBuffers = ReadEmptyBuffers(entities, actual);
            foreach (ComponentType type in actual.Values)
                if (type.TypeIndex.IsEnableable)
                    Require(entities.IsComponentEnabled(Entity, type), "GENERATED_NODE_ENABLE_STATE_CHANGED");
            requireScope!(); RequireLiveNode(entities);
        }

        private void RecordCurrentTypes(Dictionary<TypeIndex, ComponentType> types)
        { currentTypeCount = types.Count + 1; currentTypeDigest = NativeEntityArchetypes.TypeDigest(types); }

        private void GuardAllForRelease()
        {
            requireScope?.Invoke(); parentReleaseGuard?.Invoke(); requireScope?.Invoke();
            Require(source.IsCreated, "GENERATED_NODE_RELEASE_SOURCE_UNAVAILABLE");
            EntityManager entities = source.EntityManager;
            if (Entity == EcsEntity.Null || !entities.Exists(Entity)) return;
            Require(entities.HasComponent<Unity.Entities.Disabled>(Entity) && entities.HasComponent<ObjectGenerationScope>(Entity),
                "GENERATED_NODE_RELEASE_SCOPE_MISSING");
            int actualActor = entities.GetSharedComponent<ObjectGenerationScope>(Entity).Value; requireScope?.Invoke();
            Require(actualActor == actor || !scopeReturned && actualActor == 0, "GENERATED_NODE_RELEASE_SCOPE_CHANGED");
            if (createReturned)
            {
                Require(!compositionConfirmationAttempted || compositionConfirmed, "GENERATED_NODE_COMPOSITION_UNCONFIRMED");
                Require(!markerExpiryAttempted || markerExpiryPassed, "GENERATED_NODE_MARKER_EXPIRY_UNCONFIRMED");
                Require(!discardAttempted || DiscardReady, "GENERATED_NODE_DISCARD_UNCONFIRMED");
                VerifyCompletedNode();
            }
            else
            {
                latestBuffers = ReadEmptyBuffers(entities, archetypes.Read(entities.GetChunk(Entity).Archetype, 272));
                if (entities.HasComponent<Owner>(Entity))
                {
                    EcsEntity owner = entities.GetComponentData<Owner>(Entity).m_Owner;
                    Require(owner == EcsEntity.Null || rootLinkConfirmed && owner == linkedRoot,
                        "GENERATED_NODE_RELEASE_OWNER_UNCONFIRMED");
                }
                if (entities.HasComponent<OwnerDefinition>(Entity))
                    Require(entities.GetComponentData<OwnerDefinition>(Entity).Equals(ownerKey), "GENERATED_NODE_RELEASE_KEY_CHANGED");
            }
            parentReleaseGuard?.Invoke(); requireScope?.Invoke();
        }

        internal void Release()
        {
            RequireMainThread(); if (releaseCalled) return; releaseCalled = true;
            try
            {
                Require(batch.RefreshOutputs() && !batch.UnknownCreation, "GENERATED_NODE_OUTPUT_UNCONFIRMED");
                if (Entity != EcsEntity.Null) GuardAllForRelease();
                batch.Release(GuardAllForRelease);
                if (!batch.ResourcesReleased) { releaseFailure = "GENERATED_NODE_RELEASE_UNCONFIRMED"; exceptionType ??= batch.ExceptionType; }
            }
            catch (Exception error)
            { releaseFailure ??= error is Rejection rejected ? rejected.Code : "GENERATED_NODE_RELEASE_FAILED"; exceptionType ??= error.GetType().Name; }
            finally { batch.ReleaseSlots(); }
        }

        internal JObject Snapshot() => new JObject {
            ["createCalled"] = createCalled, ["createReturned"] = createReturned, ["releaseCalled"] = releaseCalled,
            ["resourcesReleased"] = ResourcesReleased, ["unknownCreation"] = UnknownCreation, ["absenceConfirmed"] = AbsenceConfirmed,
            ["entity"] = batch.ReceiptSummary(0), ["slots"] = batch.SlotsSummary(), ["actor"] = actor,
            ["archetype"] = new JObject { ["originalCount"] = originalTypeCount, ["finalCount"] = finalTypeCount,
                ["birthVerified"] = birthTypesVerified, ["finalVerified"] = finalTypesVerified, ["finalSha256"] = Text(finalTypeDigest),
                ["currentCount"] = currentTypeCount, ["currentSha256"] = Text(currentTypeDigest) },
            ["birthEnableables"] = new JObject { ["count"] = enableableCount, ["allEnabled"] = birthEnableablesVerified },
            ["birthBuffers"] = birthBuffers == null ? JValue.CreateNull() : birthBuffers.DeepClone(),
            ["latestBuffers"] = latestBuffers == null ? JValue.CreateNull() : latestBuffers.DeepClone(),
            ["writes"] = new JObject { ["attempted"] = writesAttempted, ["returned"] = writesReturned,
                ["verified"] = valuesVerified, ["orderSha256"] = Text(orderDigest),
                ["removeReturned"] = removeReturned, ["serviceUpgradeWasPresent"] = serviceUpgradeWasPresent },
            ["initialValues"] = initialValues == null ? JValue.CreateNull() : initialValues.DeepClone(),
            ["birthOwnerNull"] = birthOwnerNull, ["ownerLinkAttempted"] = ownerLinkAttempted,
            ["rootLinkConfirmed"] = rootLinkConfirmed, ["linkedRoot"] = EntityValue(linkedRoot),
            ["ownerDefStillPresent"] = valuesVerified || rootLinkConfirmed ? new JValue(ownerDefStillPresent) : JValue.CreateNull(),
            ["postLinkValuesVerified"] = postLinkValuesVerified,
            ["currentOrphan"] = hasExpectedOrphan ? (JToken)OrphanValue(currentExpectedOrphan) : JValue.CreateNull(),
            ["composition"] = new JObject { ["attempted"] = compositionConfirmationAttempted, ["observed"] = compositionObserved,
                ["confirmed"] = compositionConfirmed,
                ["before"] = compositionBefore.HasValue ? (JToken)OrphanValue(compositionBefore.Value) : JValue.CreateNull(),
                ["after"] = compositionAfter.HasValue ? (JToken)OrphanValue(compositionAfter.Value) : JValue.CreateNull(),
                ["observedSelection"] = compositionConfirmationAttempted ? new JValue(compositionObservedSelection) : JValue.CreateNull(),
                ["needsPublication"] = compositionConfirmationAttempted ? new JValue(compositionNeedsPublication) : JValue.CreateNull(),
                ["failureCode"] = Text(compositionFailure), ["exceptionType"] = Text(compositionExceptionType),
                ["cacheReadyEstablished"] = false },
            ["parentReleaseGuardBound"] = parentReleaseGuard != null,
            ["markerExpiry"] = new JObject { ["attempted"] = markerExpiryAttempted, ["returned"] = markerExpiryReturned,
                ["passed"] = markerExpiryPassed, ["beforeMask"] = markerMaskBefore, ["afterMask"] = markerMaskAfter,
                ["primitiveCalled"] = markerPrimitiveCalled, ["nativeCleanupControllerInvoked"] = false,
                ["primitive"] = "EntityManager.RemoveComponent(Entity, ComponentTypeSet)",
                ["markerSetSource"] = "Game.Common.CleanUpSystem normal modification expiry",
                ["markerSet"] = new JArray(nameof(Created), nameof(Updated), nameof(Applied), nameof(EffectsUpdated), nameof(BatchesUpdated), nameof(PathfindUpdated)) },
            ["discard"] = new JObject { ["attempted"] = discardAttempted, ["returned"] = discardReturned,
                ["observed"] = discardObserved, ["valuesVerified"] = discardValuesVerified, ["ready"] = DiscardReady },
            ["typeArrays"] = archetypes.Snapshot(), ["unsupportedBuffer"] = Text(unsupportedBuffer),
            ["nonEmptyBuffer"] = Text(nonEmptyBuffer), ["nonEmptyBufferLength"] = nonEmptyBufferLength,
            ["stage"] = stage, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType), ["releaseFailure"] = Text(releaseFailure),
            ["nativePlaybackCalled"] = false, ["scope"] = "exact-node-values; native-owner-link-and-marker-expiry; parent-graph-guarded-discard"
        };
        private void Record(Exception error, string fallback)
        { failureCode ??= error is Rejection rejected ? rejected.Code : fallback; exceptionType ??= error.GetType().Name; }
        private static T Value<T>(GeneratedNodeWrite write) where T : struct =>
            write.Value is T value ? value : throw new Rejection("GENERATED_NODE_WRITE_VALUE_INVALID");
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray Pair(float2 value) => new JArray(value.x, value.y);
        private static JArray Rotation(quaternion value) => new JArray(value.value.x, value.value.y, value.value.z, value.value.w);
        private static JObject EntityValue(EcsEntity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JObject OrphanValue(Game.Net.Orphan value) => new JObject { ["composition"] = EntityValue(value.m_Composition) };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "GENERATED_NODE_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
