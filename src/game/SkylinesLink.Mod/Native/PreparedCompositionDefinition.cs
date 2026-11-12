#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // The unpublished candidate owns an exact entity. Its actor may destroy
    // it; publishing into the World cache requires a different lifetime contract.
    internal sealed class PreparedCompositionDefinition
    {
        private readonly CompositionSelectionPreflight inputs;
        private readonly CompositionCreationDefinition definition;
        private readonly Action requireScope;
        private readonly NativeEntityBatch batch;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private Dictionary<TypeIndex, ComponentType>? birthTypes;
        private bool attempted, prepared, releaseCalled, valuesMatched, buffersMatched, scopeAttempted, scopeReturned;
        private int originalTypes, originalBuffers, copiedPieces;
        private string? originalTypeDigest, rejectedType, failureCode, exceptionType;
        private bool simulateEnabled, publicationAttempted;

        internal PreparedCompositionDefinition(CompositionSelectionPreflight inputs,
            CompositionCreationDefinition definition, Action requireScope)
        {
            this.inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
            this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
            this.requireScope = requireScope ?? throw new ArgumentNullException(nameof(requireScope));
            Require(ReferenceEquals(inputs.Source, definition.Source) && inputs.Prefab == definition.Prefab.m_Prefab,
                "COMPOSITION_CANDIDATE_SOURCE_MISMATCH");
            batch = new NativeEntityBatch(definition.Source, 1);
        }

        internal bool ResourcesReleased => releaseCalled && batch.ResourcesReleased && archetypes.ResourcesReleased;
        internal bool ChecksPassed => prepared && valuesMatched && buffersMatched && ResourcesReleased && failureCode == null;
        internal bool PublicationStorageReleased => publicationAttempted && batch.StorageReleased && archetypes.ResourcesReleased;

        internal Entity RequirePublicationCandidate(CompositionSelectionPreflight expectedInputs, CompositionCreationDefinition expectedDefinition)
        {
            Require(ReferenceEquals(inputs, expectedInputs) && ReferenceEquals(definition, expectedDefinition)
                && prepared && !publicationAttempted && !releaseCalled && failureCode == null,
                "COMPOSITION_CANDIDATE_PUBLICATION_NOT_READY");
            Verify();
            return Entity;
        }

        internal void MarkPublicationEntered() => publicationAttempted = true;
        internal void ReleasePublicationStorage() => batch.ReleaseSlots();

        internal void PrepareOnce()
        {
            Require(!attempted && !releaseCalled, "COMPOSITION_CANDIDATE_ALREADY_ATTEMPTED"); attempted = true;
            try
            {
                Guard(); inputs.RequireUnchanged(); Guard();
                var original = archetypes.Read(definition.SourceArchetype, 256); Guard();
                originalTypes = original.Count; originalTypeDigest = NativeEntityArchetypes.TypeDigest(original);
                RequireHeader(original); AdmitTypes(original);
                birthTypes = new Dictionary<TypeIndex, ComponentType>(original);
                if (definition.Terrain.HasValue) NativeEntityArchetypes.AddExpected<TerrainComposition>(birthTypes);
                EntityArchetype birth = NativeEntityArchetypes.CreateScoped(definition.Source.EntityManager, birthTypes); Guard();
                NativeEntityArchetypes.RequireSame(archetypes.Read(birth, 259), birthTypes); Guard();
                batch.Create(0, birth); Guard();
                Require(batch.CreationReturned(0), "COMPOSITION_CANDIDATE_CREATE_UNCONFIRMED");
                GuardOwned(); scopeAttempted = true;
                definition.Source.EntityManager.SetSharedComponent(Entity, new ObjectGenerationScope { Value = inputs.Actor });
                scopeReturned = true; GuardOwned();
                var manager = definition.Source.EntityManager;
                manager.SetComponentData(Entity, definition.Prefab); GuardOwned();
                manager.SetComponentData(Entity, definition.Data); GuardOwned();
                NetCompositionPiece[] pieces = definition.CopyPieces();
                // The borrowed buffer does not cross a scope callback.
                manager.GetBuffer<NetCompositionPiece>(Entity).CopyFrom(pieces); GuardOwned();
                if (definition.Terrain.HasValue) { manager.SetComponentData(Entity, definition.Terrain.Value); GuardOwned(); }
                Verify(); inputs.RequireUnchanged(); GuardOwned(); prepared = true;
            }
            catch (Exception error) { Record(error); throw; }
        }

        private Entity Entity => batch.EntityAt(0);
        private void Verify()
        {
            GuardOwned(); var manager = definition.Source.EntityManager;
            NativeEntityArchetypes.RequireSame(archetypes.Read(manager.GetChunk(Entity).Archetype, 259), birthTypes!); GuardOwned();
            Require(manager.GetComponentData<PrefabRef>(Entity).Equals(definition.Prefab), "COMPOSITION_CANDIDATE_PREFAB_MISMATCH"); GuardOwned();
            Require(manager.GetComponentData<NetCompositionData>(Entity).Equals(definition.Data), "COMPOSITION_CANDIDATE_DATA_MISMATCH"); GuardOwned();
            simulateEnabled = manager.IsComponentEnabled<Simulate>(Entity); GuardOwned();
            Require(simulateEnabled, "COMPOSITION_CANDIDATE_SIMULATE_DISABLED");
            Default<NetCompositionMeshRef>(); Default<PlaceableNetComposition>(); Default<RoadComposition>(); Default<TrackComposition>();
            Default<WaterwayComposition>(); Default<PathwayComposition>(); Default<TaxiwayComposition>(); Default<TerrainComposition>();
            valuesMatched = true;
            var actualPieces = manager.GetBuffer<NetCompositionPiece>(Entity, true);
            Require(actualPieces.Length == definition.PieceCount, "COMPOSITION_CANDIDATE_PIECE_COUNT_CHANGED");
            for (int index = 0; index < actualPieces.Length; index++)
                Require(actualPieces[index].Equals(definition.PieceAt(index)), "COMPOSITION_CANDIDATE_PIECE_VALUE_CHANGED");
            copiedPieces = actualPieces.Length;
            GuardOwned();
            Empty<NetCompositionLane>(); Empty<NetCompositionObject>(); Empty<NetCompositionArea>();
            Empty<NetCompositionCrosswalk>(); Empty<NetCompositionCarriageway>(); buffersMatched = true;
        }

        private void Default<T>() where T : unmanaged, IComponentData
        {
            GuardOwned(); var manager = definition.Source.EntityManager;
            if (manager.HasComponent<T>(Entity))
                Require(manager.GetComponentData<T>(Entity).Equals(default(T)), "COMPOSITION_CANDIDATE_DEFAULT_DATA_CHANGED");
            GuardOwned();
        }
        private void Empty<T>() where T : unmanaged, IBufferElementData
        {
            GuardOwned(); var manager = definition.Source.EntityManager;
            if (manager.HasBuffer<T>(Entity))
                Require(manager.GetBuffer<T>(Entity, true).Length == 0, "COMPOSITION_CANDIDATE_OUTPUT_BUFFER_NOT_EMPTY");
            GuardOwned();
        }

        private static void RequireHeader(Dictionary<TypeIndex, ComponentType> types)
        {
            foreach (TypeIndex type in new[] { TypeManager.GetTypeIndex<PrefabRef>(), TypeManager.GetTypeIndex<NetCompositionData>(),
                TypeManager.GetTypeIndex<NetCompositionPiece>(), TypeManager.GetTypeIndex<NetCompositionMeshRef>(),
                TypeManager.GetTypeIndex<Created>(), TypeManager.GetTypeIndex<Updated>(), TypeManager.GetTypeIndex<Simulate>() })
                Require(types.ContainsKey(type), "COMPOSITION_CANDIDATE_HEADER_MISSING");
        }
        private void AdmitTypes(Dictionary<TypeIndex, ComponentType> types)
        {
            var components = new HashSet<TypeIndex> { TypeManager.GetTypeIndex<PrefabRef>(), TypeManager.GetTypeIndex<NetCompositionData>(),
                TypeManager.GetTypeIndex<NetCompositionMeshRef>(), TypeManager.GetTypeIndex<Created>(), TypeManager.GetTypeIndex<Updated>(),
                TypeManager.GetTypeIndex<PlaceableNetComposition>(), TypeManager.GetTypeIndex<RoadComposition>(), TypeManager.GetTypeIndex<TrackComposition>(),
                TypeManager.GetTypeIndex<WaterwayComposition>(), TypeManager.GetTypeIndex<PathwayComposition>(), TypeManager.GetTypeIndex<TaxiwayComposition>(),
                TypeManager.GetTypeIndex<TerrainComposition>(), TypeManager.GetTypeIndex<Simulate>() };
            var buffers = new HashSet<TypeIndex> { TypeManager.GetTypeIndex<NetCompositionPiece>(), TypeManager.GetTypeIndex<NetCompositionLane>(),
                TypeManager.GetTypeIndex<NetCompositionObject>(), TypeManager.GetTypeIndex<NetCompositionArea>(),
                TypeManager.GetTypeIndex<NetCompositionCrosswalk>(), TypeManager.GetTypeIndex<NetCompositionCarriageway>() };
            foreach (TypeIndex type in types.Keys)
            {
                bool admitted = (!type.IsEnableable || type == TypeManager.GetTypeIndex<Simulate>())
                    && !type.IsSharedComponentType && !type.IsManagedType && !type.HasNativeContainer
                    && (components.Contains(type) || buffers.Contains(type));
                if (!admitted) { rejectedType = TypeManager.GetType(type).FullName; throw new Rejection("COMPOSITION_CANDIDATE_TYPE_NOT_QUALIFIED"); }
                if (buffers.Contains(type)) originalBuffers++;
            }
        }

        private void Guard()
        {
            Require(MainThreadDispatcher.isMainThread, "COMPOSITION_CANDIDATE_MAIN_THREAD_REQUIRED"); requireScope();
            Require(definition.Source.IsCreated, "COMPOSITION_CANDIDATE_SOURCE_UNAVAILABLE");
        }
        private void GuardOwned()
        {
            Guard(); var manager = definition.Source.EntityManager;
            Require(Entity != Entity.Null && manager.Exists(Entity) && manager.HasComponent<Disabled>(Entity)
                && manager.HasComponent<ObjectGenerationScope>(Entity), "COMPOSITION_CANDIDATE_OWNED_ENTITY_UNAVAILABLE");
            int actor = manager.GetSharedComponent<ObjectGenerationScope>(Entity).Value;
            Require(actor == inputs.Actor || !scopeReturned && actor == 0, "COMPOSITION_CANDIDATE_SCOPE_CHANGED");
        }

        internal void ReleaseUnpublished()
        {
            if (releaseCalled || publicationAttempted) return; releaseCalled = true;
            try
            {
                // The native class has no LinkedEntityGroup or cleanup type;
                // this exact unpublished entity cannot cascade into borrowed assets.
                batch.RefreshOutputs();
                batch.Release(GuardRelease);
            }
            catch (Exception error) { Record(error); }
            finally { batch.ReleaseSlots(); }
        }

        private void GuardRelease()
        {
            Guard();
            Require(Entity != Entity.Null && Entity.Index >= 0, "COMPOSITION_CANDIDATE_RELEASE_HANDLE_UNAVAILABLE");
            bool exists = definition.Source.EntityManager.Exists(Entity); Guard();
            // The physical owner repeats this callback in Destroy's tail.
            // Same-world absence is valid there; an unavailable Source is not.
            if (!exists) return;
            GuardOwned();
            NativeEntityArchetypes.RequireSame(archetypes.Read(definition.Source.EntityManager.GetChunk(Entity).Archetype, 259), birthTypes!);
            GuardOwned();
        }

        internal JObject Snapshot() => new JObject {
            ["attempted"] = attempted, ["prepared"] = prepared, ["checksPassed"] = ChecksPassed,
            ["releaseCalled"] = releaseCalled, ["resourcesReleased"] = ResourcesReleased,
            ["originalTypes"] = originalTypes, ["originalBuffers"] = originalBuffers,
            ["originalTypeDigest"] = Text(originalTypeDigest), ["valuesMatched"] = valuesMatched,
            ["buffersMatched"] = buffersMatched, ["copiedPieces"] = copiedPieces,
            ["scopeAttempted"] = scopeAttempted, ["scopeReturned"] = scopeReturned,
            ["simulateEnabled"] = simulateEnabled, ["rejectedType"] = Text(rejectedType),
            ["entity"] = batch.ReceiptSummary(0), ["slots"] = batch.SlotsSummary(), ["typeArrays"] = archetypes.Snapshot(),
            ["storageReleased"] = batch.StorageReleased, ["entitiesDestroyed"] = batch.EntitiesDestroyed,
            ["sourceCacheWritten"] = false, ["nodeWritten"] = false, ["publicationAttempted"] = publicationAttempted,
            ["nativeInitializationInvoked"] = false, ["cacheReadyQualified"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };
        private void Record(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "COMPOSITION_CANDIDATE_FAILED"; exceptionType ??= error.GetType().Name; }
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
