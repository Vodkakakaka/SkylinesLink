#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Runs the real selection and creation jobs for one admitted Orphan. The
    // retained ECB is observed and discarded; it never publishes a shared cache.
    [DisableAutoCreation]
    public sealed partial class CompositionSelectionProducer : CompositionSelectSystem
    {
        private const int MaximumCommands = 7, MaximumPieces = 4096, MaximumRecordBytes = 65536;
        private int actor, registeredCount, updatedCount, allCount, arraysAttempted, arraysAcquired, arraysDisposed;
        private int cacheCount, pieceUpperBound, inspectedCommands, pendingObserved;
        private bool configured, bindingsVerified, registeredExactly, emptyArmed, armed, emptyPassed;
        private bool emptyStarted, emptyReturned, emptyJoined, nativeStarted, nativeReturned, nativeJoined;
        private bool joinFailed, selectionRequested, observationPassed, inspected, afterObserved;
        private bool updatedIgnoredEmpty, allIgnoredEmpty, loadedObserved;
        private EntityQuery updated, all;
        private CityConfigurationSystem? configuration;
        private CompositionSelectionBarrier? output;
        private CompositionSelectionPreflight? admitted;
        private Bindings? fields;
        private Action? currentScope;
        private Orphan before, after;
        private NetCompositionData? selectedData;
        private MissObservation? proposal;
        private CompositionPieceGeneration? generatedPieces;
        private CompositionCreationDefinition? creationDefinition;
        private JObject? commands;
        private CompositionSelectionReceipt? receipt;
        private string? failureCode, exceptionType;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int OnUpdateEntries { get; private set; }
        internal bool NativeCallStarted => emptyStarted || nativeStarted;
        internal bool NativeWorkMayReadSource => emptyStarted && !emptyJoined || nativeStarted && !nativeJoined;
        internal bool StorageReleased => arraysAttempted == arraysAcquired && arraysAcquired == arraysDisposed
            && (generatedPieces == null || generatedPieces.ResourcesReleased);
        internal bool ResourcesReleased => DestructionReturned && StorageReleased;
        internal bool ObservationPassed => observationPassed && nativeReturned && nativeJoined && receipt != null && failureCode == null;
        internal bool SelectionApplied => ObservationPassed && receipt!.ObservedSelection;
        internal bool NeedsPublication => ObservationPassed && receipt!.NeedsPublication;
        internal CompositionSelectionReceipt? Receipt => receipt;
        internal CompositionCreationDefinition? CreationDefinition => creationDefinition;
        private int PendingCount { get { pendingObserved = output?.PendingCount ?? 0; return pendingObserved; } }

        internal void Configure(int actor, CompositionSelectionBarrier barrier)
        {
            RequireMainThread();
            if (barrier == null) throw new ArgumentNullException(nameof(barrier));
            Require(!configured && !CreationStarted && World == null && actor > 0, "COMPOSITION_SELECTION_CONFIGURATION_REJECTED");
            fields = new Bindings(); this.actor = actor; output = barrier; configured = true;
        }

        protected override void OnCreate()
        {
            CreationStarted = true;
            RequireMainThread(); Enabled = false;
            Require(configured && fields != null && output != null && output.CreationReturned && ReferenceEquals(output.World, World),
                "COMPOSITION_SELECTION_CONFIGURATION_MISSING");
            configuration = World.GetExistingSystemManaged<CityConfigurationSystem>();
            Require(configuration != null, "COMPOSITION_SELECTION_CITY_CONFIGURATION_UNAVAILABLE");
            updated = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadWrite<Composition>(), ComponentType.ReadWrite<Orphan>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            all = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadWrite<Composition>(), ComponentType.ReadWrite<Orphan>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
            var filter = new ObjectGenerationScope { Value = actor };
            updated.SetSharedComponentFilter(filter); all.SetSharedComponentFilter(filter);
            fields!.Updated.SetValue(this, updated); fields.All.SetValue(this, all);
            fields.Configuration.SetValue(this, configuration); fields.Barrier.SetValue(this, output);
            fields.Loaded.SetValue(this, false);
            // The native NetCompositionSystem field is unused by OnUpdate. Do
            // not create a default system or register Game callbacks to fill it.
            ObserveBindings(null);
            CreationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { if (!joinFailed) JoinOwnedDependencies(); }

        internal void ProbeEmptyUpdate(Action scope)
        {
            RequireMainThread();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && !DestructionStarted && !emptyPassed && !emptyStarted && !selectionRequested,
                "COMPOSITION_SELECTION_EMPTY_REJECTED");
            try
            {
                RequireEmpty(scope);
                bool shouldRun = ShouldRunSystem(); Guard(scope);
                Require(shouldRun, "COMPOSITION_SELECTION_EMPTY_GATE_CHANGED");
                currentScope = scope; emptyArmed = true; Enabled = true;
                try { Update(); Require(emptyReturned, "COMPOSITION_SELECTION_EMPTY_NATIVE_NOT_RETURNED"); JoinOwnedDependencies(); }
                finally { Enabled = false; emptyArmed = false; currentScope = null; }
                RequireEmpty(scope);
                Require(emptyJoined && OnUpdateEntries == 1 && PendingCount == 0, "COMPOSITION_SELECTION_EMPTY_UNCONFIRMED");
                emptyPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        internal void SelectOnce(CompositionSelectionPreflight inputs, Action scope)
        {
            RequireMainThread();
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Require(CreationReturned && emptyPassed && !DestructionStarted && !selectionRequested && !NativeWorkMayReadSource,
                "COMPOSITION_SELECTION_RUN_REJECTED");
            selectionRequested = true; admitted = inputs;
            try
            {
                Require(inputs.Passed && ReferenceEquals(inputs.Source, World) && inputs.Actor == actor
                    && inputs.BeforeOrphan.m_Composition == Entity.Null && inputs.CacheCount >= 0 && inputs.CacheCount <= 256
                    && inputs.PieceUpperBound >= 0 && inputs.PieceUpperBound <= MaximumPieces,
                    "COMPOSITION_SELECTION_PREFLIGHT_UNQUALIFIED");
                before = inputs.BeforeOrphan; cacheCount = inputs.CacheCount; pieceUpperBound = inputs.PieceUpperBound;
                Qualify(inputs, scope);
                currentScope = scope; armed = true; Enabled = true;
                try { Update(); Require(nativeReturned, "COMPOSITION_SELECTION_NATIVE_NOT_RETURNED"); JoinOwnedDependencies(); }
                finally { Enabled = false; armed = false; currentScope = null; }
                Guard(scope); inputs.RequireUnchanged(); Guard(scope);
                ObserveBindings(scope);
                Require(nativeJoined && OnUpdateEntries == 2 && PendingCount == 1, "COMPOSITION_SELECTION_COMPLETION_UNCONFIRMED");
                after = inputs.ReadCurrentOrphan(); Guard(scope); afterObserved = true;
                Array observed = InspectOnce();
                if (observed.Length == 0) ObserveHit(inputs, scope);
                else ObserveMiss(observed, inputs, scope);
                Require(receipt != null && !NativeWorkMayReadSource, "COMPOSITION_SELECTION_RECEIPT_UNCONFIRMED");
                observationPassed = true;
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }

        private void RequireEmpty(Action scope)
        {
            Guard(scope); ObserveBindings(scope); ReadCounts(scope);
            Require(updatedCount == 0 && allCount == 0 && updatedIgnoredEmpty && allIgnoredEmpty && PendingCount == 0,
                "COMPOSITION_SELECTION_NOT_EMPTY");
        }

        private void Qualify(CompositionSelectionPreflight inputs, Action scope)
        {
            Guard(scope); ObserveBindings(scope);
            inputs.RequireUnchanged(); Guard(scope);
            Require(inputs.Passed && ReferenceEquals(inputs.Source, World) && inputs.Actor == actor
                && inputs.BeforeOrphan.Equals(before) && inputs.PieceUpperBound == pieceUpperBound && inputs.CacheCount == cacheCount,
                "COMPOSITION_SELECTION_INPUT_CHANGED");
            ReadCounts(scope);
            Require(updatedCount == 1 && allCount == 1 && !updatedIgnoredEmpty && !allIgnoredEmpty,
                "COMPOSITION_SELECTION_QUERY_BUDGET");
            RequireOnlyNode(updated, inputs.Node, scope); RequireOnlyNode(all, inputs.Node, scope);
            bool shouldRun = ShouldRunSystem(); Guard(scope);
            Require(shouldRun && PendingCount == 0, "COMPOSITION_SELECTION_NATIVE_GATE_CHANGED");
        }

        private void RequireOnlyNode(EntityQuery query, Entity node, Action scope)
        {
            arraysAttempted++; NativeArray<Entity> values = query.ToEntityArray(Allocator.Temp); arraysAcquired++;
            try { Guard(scope); Require(values.Length == 1 && values[0] == node, "COMPOSITION_SELECTION_FOREIGN_QUERY_MEMBER"); }
            finally { values.Dispose(); arraysDisposed++; }
        }

        private void ReadCounts(Action scope)
        {
            updatedCount = updated.CalculateEntityCount(); Guard(scope); allCount = all.CalculateEntityCount(); Guard(scope);
            updatedIgnoredEmpty = updated.IsEmptyIgnoreFilter; Guard(scope); allIgnoredEmpty = all.IsEmptyIgnoreFilter; Guard(scope);
        }

        protected override void OnUpdate()
        {
            OnUpdateEntries++;
            Action scope = currentScope ?? throw Failure("COMPOSITION_SELECTION_SCOPE_CALLBACK_MISSING");
            if (emptyArmed)
            {
                Require(!emptyStarted && !nativeStarted, "COMPOSITION_SELECTION_EMPTY_ALREADY_STARTED");
                RequireEmpty(scope); emptyStarted = true; base.OnUpdate(); emptyReturned = true; return;
            }
            Require(armed && admitted != null && !nativeStarted, "COMPOSITION_SELECTION_NOT_ARMED");
            // The normal SystemBase update refreshed native dependency handles.
            Qualify(admitted!, scope); nativeStarted = true; base.OnUpdate(); nativeReturned = true;
        }

        private Array InspectOnce()
        {
            Require(!inspected && nativeReturned && nativeJoined && admitted != null && admitted.Passed && PendingCount == 1
                && pieceUpperBound >= 0 && pieceUpperBound <= MaximumPieces, "COMPOSITION_SELECTION_INSPECTION_REJECTED");
            inspected = true;
            // The input DAG and the single-Orphan branch prove <=7 commands
            // before this full getter. SetBuffer contents remain opaque.
            var abi = new ObjectDefinitionCommandProbe.Abi();
            object proxy = abi.Constructor.Invoke(new object[] { output!.Borrow() });
            var values = (Array)abi.Commands.GetValue(proxy)!;
            Require(values.Length <= MaximumCommands, "COMPOSITION_SELECTION_COMMAND_BUDGET");
            inspectedCommands = values.Length;
            return values;
        }

        private void ObserveHit(CompositionSelectionPreflight inputs, Action scope)
        {
            Require(after.m_Composition != Entity.Null && after.m_Composition.Index >= 0 && !after.Equals(before),
                "COMPOSITION_SELECTION_HIT_NOT_OBSERVED");
            Guard(scope); EntityManager entities = EntityManager;
            bool exists = entities.Exists(after.m_Composition); Guard(scope);
            Require(exists && entities.HasComponent<PrefabRef>(after.m_Composition)
                && entities.HasComponent<NetCompositionData>(after.m_Composition) && entities.HasBuffer<NetCompositionPiece>(after.m_Composition)
                && !entities.HasComponent<Deleted>(after.m_Composition), "COMPOSITION_SELECTION_CACHE_TARGET_UNAVAILABLE");
            PrefabRef prefab = entities.GetComponentData<PrefabRef>(after.m_Composition); Guard(scope);
            NetCompositionData data = entities.GetComponentData<NetCompositionData>(after.m_Composition); Guard(scope);
            Require(prefab.m_Prefab == inputs.Prefab, "COMPOSITION_SELECTION_CACHE_PREFAB_CHANGED");
            int selected = -1; NetGeometryComposition row = default;
            for (int index = 0; index < inputs.CacheCount; index++)
            {
                NetGeometryComposition candidate = inputs.CacheAt(index);
                if (candidate.m_Mask != data.m_Flags) continue;
                selected = index; row = candidate; break;
            }
            Require(selected >= 0 && row.m_Composition == after.m_Composition && row.m_Mask == data.m_Flags,
                "COMPOSITION_SELECTION_FIRST_CACHE_ROW_MISMATCH");
            inputs.RequireUnchanged(); Guard(scope);
            selectedData = data;
            commands = new JObject { ["commandsObserved"] = 0, ["opaquePieceBuffers"] = 0, ["sourcePlaybackCalled"] = false };
            // Keep the original root runtime guard. The receipt is used after
            // linked children and this producer may already have been released.
            receipt = new CompositionSelectionReceipt(inputs.Source, inputs.Node, inputs.Prefab, actor, before, after,
                true, false, selected, row, true, true, scope);
        }

        private void ObserveMiss(Array values, CompositionSelectionPreflight inputs, Action scope)
        {
            Require((values.Length == 6 || values.Length == 7) && after.Equals(before), "COMPOSITION_SELECTION_MISS_SOURCE_CHANGED");
            var abi = new ObjectDefinitionCommandProbe.Abi();
            var records = new JArray(); long recordBytes = 0;
            foreach (object value in values)
            {
                int bytes = abi.Int(value, "TotalSizeInBytes");
                Require(bytes > 0 && bytes <= MaximumRecordBytes && abi.Int(value, "SortKey") == int.MaxValue,
                    "COMPOSITION_SELECTION_RECORD_UNQUALIFIED");
                recordBytes = checked(recordBytes + bytes);
                Require(recordBytes <= MaximumRecordBytes, "COMPOSITION_SELECTION_RECORD_BUDGET");
                records.Add(new JObject { ["kind"] = Kind(abi, value), ["recordBytes"] = bytes });
            }
            object create = values.GetValue(0)!;
            Require(create.GetType() == abi.Create && Kind(abi, create) == "CreateEntity" && abi.Int(create, "BatchCount") == 1,
                "COMPOSITION_SELECTION_CREATE_UNQUALIFIED");
            int identity = abi.Int(create, "EntityIdentityIndex"); Require(identity < 0, "COMPOSITION_SELECTION_CREATE_NOT_DEFERRED");
            EntityArchetype archetype = (EntityArchetype)abi.Value(create, "EntityArchetype", typeof(EntityArchetype));

            object append = values.GetValue(1)!;
            NetGeometryComposition cache = ComponentValue<NetGeometryComposition>(abi, append, "AppendToBuffer", inputs.Prefab, true, true);
            Entity deferred = cache.m_Composition;
            Require(deferred.Index == identity && deferred.Index < 0, "COMPOSITION_SELECTION_CACHE_REFERENCE_CHANGED");
            PrefabRef prefab = ComponentValue<PrefabRef>(abi, values.GetValue(2)!, "SetComponent", deferred, false, false);
            Require(prefab.m_Prefab == inputs.Prefab, "COMPOSITION_SELECTION_PROPOSAL_PREFAB_CHANGED");
            NetCompositionData data = ComponentValue<NetCompositionData>(abi, values.GetValue(3)!, "SetComponent", deferred, false, false);
            Require(data.Equals(new NetCompositionData { m_Flags = cache.m_Mask })
                && (cache.m_Mask.m_General & CompositionFlags.General.Node) != 0, "COMPOSITION_SELECTION_PROPOSAL_DATA_CHANGED");
            Guard(scope); NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(inputs.Prefab); Guard(scope);
            EntityArchetype expected = (cache.m_Mask.m_General & CompositionFlags.General.Node) != 0
                ? geometry.m_NodeCompositionArchetype : geometry.m_EdgeCompositionArchetype;
            Require(archetype.Valid && archetype.TypesCount > 0 && archetype.TypesCount <= 256 && archetype.Equals(expected),
                "COMPOSITION_SELECTION_PROPOSAL_ARCHETYPE_CHANGED");
            for (int index = 0; index < inputs.CacheCount; index++)
                Require(inputs.CacheAt(index).m_Mask != cache.m_Mask, "COMPOSITION_SELECTION_MISS_ALREADY_CACHED");

            object buffer = values.GetValue(4)!;
            Require(buffer.GetType() == abi.Buffer && Kind(abi, buffer) == "SetBuffer", "COMPOSITION_SELECTION_PIECES_BUFFER_CHANGED");
            RequireTarget(abi, buffer, deferred);
            TypeIndex pieces = (TypeIndex)abi.Value(buffer, "ComponentTypeIndex", typeof(TypeIndex));
            ref readonly TypeManager.TypeInfo pieceInfo = ref TypeManager.GetTypeInfo(pieces);
            Require(pieces == TypeManager.GetTypeIndex<NetCompositionPiece>() && pieces.IsBuffer && !pieces.IsManagedType
                && !pieces.IsCleanupBufferComponent && !pieces.HasNativeContainer && pieceInfo.EntityOffsetCount == 1
                && (short)abi.Value(buffer, "ComponentSize", typeof(short)) == pieceInfo.SizeInChunk
                && (bool)abi.Value(buffer, "ValueRequiresEntityFixup", typeof(bool)), "COMPOSITION_SELECTION_PIECES_METADATA_UNQUALIFIED");
            // BufferNode is deliberately never read. No claim about observed
            // length, element values, deferred fixups or complete geometry.
            TerrainComposition? terrain = null;
            if (values.Length == 7)
            {
                TerrainComposition value = ComponentValue<TerrainComposition>(abi, values.GetValue(5)!, "AddComponent", deferred, false, false);
                Require(value.Equals(default(TerrainComposition)), "COMPOSITION_SELECTION_TERRAIN_DATA_CHANGED"); terrain = value;
            }
            Orphan orphan = ComponentValue<Orphan>(abi, values.GetValue(values.Length - 1)!, "SetComponent", inputs.Node, true, false);
            Require(orphan.m_Composition == deferred, "COMPOSITION_SELECTION_NODE_REFERENCE_CHANGED");
            inputs.RequireUnchanged(); Guard(scope);
            proposal = new MissObservation(deferred, archetype, cache, prefab, data, orphan, terrain);
            generatedPieces = new CompositionPieceGeneration();
            generatedPieces.RunOnce(inputs, terrain.HasValue, (result, geometrySections) => {
                // These read-only lookups are borrowed only for this synchronous
                // call. The complete preflight and native job have already joined.
                var subSections = GetBufferLookup<NetSubSection>(true);
                var sectionPieces = GetBufferLookup<NetSectionPiece>(true);
                NetCompositionHelpers.GetCompositionPieces(result, geometrySections, cache.m_Mask, subSections, sectionPieces);
            }, () => Guard(scope));
            Require(generatedPieces.Passed, "COMPOSITION_SELECTION_TYPED_PIECES_UNQUALIFIED");
            creationDefinition = new CompositionCreationDefinition(inputs, archetype, prefab, data, terrain, generatedPieces);
            commands = new JObject { ["commandsObserved"] = values.Length, ["commandUpperBound"] = MaximumCommands,
                ["createCommands"] = 1, ["cacheAppendCommands"] = 1, ["opaquePieceBuffers"] = 1,
                ["pieceUpperBound"] = pieceUpperBound, ["pieceContentsObserved"] = false,
                ["terrainAddCommands"] = terrain.HasValue ? 1 : 0, ["nodeSetCommands"] = 1,
                ["recordBytes"] = recordBytes, ["recordBytesAreHeapBytes"] = false,
                ["deferred"] = Id(deferred), ["cacheMask"] = Flags(cache.m_Mask), ["commands"] = records,
                ["sourcePlaybackCalled"] = false, ["sharedCachePublished"] = false };
            receipt = new CompositionSelectionReceipt(inputs.Source, inputs.Node, inputs.Prefab, actor, before, after,
                false, true, null, null, true, true, scope);
        }

        private static T ComponentValue<T>(ObjectDefinitionCommandProbe.Abi abi, object command, string kind,
            Entity target, bool fixup, bool bufferElement) where T : unmanaged
        {
            Require(command.GetType() == abi.Component && Kind(abi, command) == kind, "COMPOSITION_SELECTION_COMPONENT_COMMAND_CHANGED");
            RequireTarget(abi, command, target);
            TypeIndex type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
            ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
            Require(type == TypeManager.GetTypeIndex<T>() && !type.IsManagedType && !type.IsSharedComponentType && !type.IsChunkComponent
                && !type.IsCleanupComponent && !type.IsCleanupBufferComponent && !type.IsCleanupSharedComponent && !type.HasNativeContainer
                && !info.HasBlobAssetRefs && !info.HasWeakAssetRefs && !info.HasUnityObjectRefs
                && type.IsBuffer == bufferElement, "COMPOSITION_SELECTION_COMPONENT_METADATA_CHANGED");
            int expectedSize = bufferElement ? info.ElementSize : info.SizeInChunk;
            Require(expectedSize >= 0 && expectedSize <= 4096 && (short)abi.Value(command, "ComponentSize", typeof(short)) == expectedSize
                && (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)) == fixup, "COMPOSITION_SELECTION_COMPONENT_LAYOUT_CHANGED");
            object? value = abi.Value(command, "ComponentValue", typeof(object));
            if (expectedSize == 0) { Require(value == null, "COMPOSITION_SELECTION_TAG_VALUE_CHANGED"); return default; }
            Require(value != null && value.GetType() == typeof(T), "COMPOSITION_SELECTION_COMPONENT_VALUE_CHANGED");
            return (T)value!;
        }

        private static void RequireTarget(ObjectDefinitionCommandProbe.Abi abi, object command, Entity target) =>
            Require(abi.Int(command, "BatchCount") == 1 && abi.Int(command, "IdentityIndex") == 0
                && (Entity)abi.Value(command, "Entity", typeof(Entity)) == target, "COMPOSITION_SELECTION_COMMAND_TARGET_CHANGED");
        private static string Kind(ObjectDefinitionCommandProbe.Abi abi, object command) =>
            Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";

        private void ObserveBindings(Action? scope)
        {
            Require(!DestructionStarted && !NativeWorkMayReadSource && fields != null && output != null,
                "COMPOSITION_SELECTION_BINDINGS_UNAVAILABLE");
            bindingsVerified = ((EntityQuery)fields!.Updated.GetValue(this)!).Equals(updated)
                && ((EntityQuery)fields.All.GetValue(this)!).Equals(all)
                && ReferenceEquals(fields.Configuration.GetValue(this), configuration)
                && ReferenceEquals(fields.Barrier.GetValue(this), output) && fields.UnusedCompositionSystem.GetValue(this) == null;
            loadedObserved = (bool)fields.Loaded.GetValue(this)!;
            EntityQuery[] values = EntityQueries; if (scope != null) Guard(scope);
            registeredCount = values.Length; registeredExactly = registeredCount == 2 && Contains(values, updated) && Contains(values, all);
            bool filters = updated.HasFilter() && all.HasFilter(); if (scope != null) Guard(scope);
            Require(bindingsVerified && registeredExactly && filters && !loadedObserved && ReferenceEquals(output!.World, World),
                "COMPOSITION_SELECTION_BINDINGS_CHANGED");
        }

        private void JoinOwnedDependencies()
        {
            Require(!joinFailed && (!emptyStarted || emptyReturned) && (!nativeStarted || nativeReturned), "COMPOSITION_SELECTION_JOIN_UNRESOLVED");
            try { Dependency.Complete(); if (emptyReturned) emptyJoined = true; if (nativeReturned) nativeJoined = true; }
            catch { joinFailed = true; throw; }
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            Require(!NativeWorkMayReadSource, "COMPOSITION_SELECTION_RELEASE_UNRESOLVED");
            JoinOwnedDependencies();
            // Native main jobs ended. Their independently scheduled queue free
            // is not observed or disposed a second time by this adapter.
            DestructionReturned = true;
        }

        internal JObject Snapshot() => new JObject {
            ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned,
            ["resourcesReleased"] = ResourcesReleased, ["storageReleased"] = StorageReleased,
            ["emptyPassed"] = emptyPassed, ["emptyEntered"] = emptyStarted, ["emptyReturned"] = emptyReturned, ["emptyJoined"] = emptyJoined,
            ["registeredCount"] = registeredCount, ["registeredExactly"] = registeredExactly, ["bindingsVerified"] = bindingsVerified,
            ["loadedFlag"] = loadedObserved, ["updatedCount"] = updatedCount, ["allCount"] = allCount,
            ["cacheCount"] = cacheCount, ["pieceUpperBound"] = pieceUpperBound, ["pendingEcbCount"] = pendingObserved,
            ["queryArrays"] = new JObject { ["attempted"] = arraysAttempted, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
            ["onUpdateEntries"] = OnUpdateEntries, ["nativeEntered"] = nativeStarted, ["nativeReturned"] = nativeReturned,
            ["nativeJoined"] = nativeJoined, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["joinFailed"] = joinFailed,
            ["sourceOrphanMayHaveChanged"] = nativeStarted, ["before"] = Id(before.m_Composition),
            ["after"] = afterObserved ? Id(after.m_Composition) : JValue.CreateNull(),
            ["observationPassed"] = ObservationPassed, ["selectionApplied"] = ObservationPassed ? new JValue(SelectionApplied) : JValue.CreateNull(),
            ["needsPublication"] = ObservationPassed ? new JValue(NeedsPublication) : JValue.CreateNull(),
            ["receipt"] = receipt == null ? JValue.CreateNull() : receipt.Snapshot(),
            ["inspectedOnce"] = inspected, ["commandsObserved"] = inspectedCommands,
            ["commands"] = commands ?? (JToken)JValue.CreateNull(), ["typedProposalRetained"] = proposal != null,
            ["typedPieces"] = generatedPieces == null ? JValue.CreateNull() : generatedPieces.Snapshot(),
            ["selectedDataObserved"] = selectedData.HasValue, ["sourcePlaybackCalled"] = false,
            ["nativeInternalDeallocatorExecutionObserved"] = false, ["sharedCachePublished"] = false,
            ["cacheReadyQualified"] = false, ["wholePipelineQualified"] = false,
            ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
        };

        private void Guard(Action scope)
        { scope(); Require(World != null && World.IsCreated && !DestructionStarted, "COMPOSITION_SELECTION_SOURCE_UNAVAILABLE"); }
        private void RecordFailure(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "COMPOSITION_SELECTION_FAILED"; exceptionType ??= error.GetType().Name; }
        private static bool Contains(EntityQuery[] values, EntityQuery value)
        { foreach (EntityQuery candidate in values) if (candidate.Equals(value)) return true; return false; }
        private static JObject Flags(CompositionFlags value) => new JObject { ["general"] = (ulong)value.m_General, ["left"] = (ulong)value.m_Left, ["right"] = (ulong)value.m_Right };
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "COMPOSITION_SELECTION_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw Failure(code); }
        private static Rejection Failure(string code) => new Rejection(code);
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }

        private sealed class MissObservation
        {
            internal readonly Entity Deferred;
            internal readonly EntityArchetype Archetype;
            internal readonly NetGeometryComposition CacheAppend;
            internal readonly PrefabRef Prefab;
            internal readonly NetCompositionData Data;
            internal readonly Orphan NodeValue;
            internal readonly TerrainComposition? Terrain;
            internal MissObservation(Entity deferred, EntityArchetype archetype, NetGeometryComposition cache, PrefabRef prefab,
                NetCompositionData data, Orphan orphan, TerrainComposition? terrain)
            { Deferred = deferred; Archetype = archetype; CacheAppend = cache; Prefab = prefab; Data = data; NodeValue = orphan; Terrain = terrain; }
        }

        private sealed class Bindings
        {
            internal readonly FieldInfo Updated, All, Configuration, Barrier, Loaded, UnusedCompositionSystem;
            internal Bindings()
            {
                Require(typeof(CompositionSelectSystem).Module.ModuleVersionId.ToString("N") == "e15551e93dea48558dde1da3cea7d7ef"
                    && typeof(World).Module.ModuleVersionId.ToString("N") == "3d6b23aeb28144a6a5f4cbd5fd3d75e6", "COMPOSITION_SELECTION_NATIVE_BUILD_UNQUALIFIED");
                Updated = Field("m_UpdatedQuery", typeof(EntityQuery)); All = Field("m_AllQuery", typeof(EntityQuery));
                Configuration = Field("m_CityConfigurationSystem", typeof(CityConfigurationSystem));
                Barrier = Field("m_ModificationBarrier", typeof(ModificationBarrier3)); Loaded = Field("m_Loaded", typeof(bool));
                UnusedCompositionSystem = Field("m_NetCompositionSystem", typeof(NetCompositionSystem));
            }
            private static FieldInfo Field(string name, Type type)
            {
                FieldInfo? value = typeof(CompositionSelectSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Require(value != null && value.DeclaringType == typeof(CompositionSelectSystem) && value.FieldType == type
                    && !value.IsStatic && !value.IsInitOnly, "COMPOSITION_SELECTION_FIELD_ABI_UNQUALIFIED"); return value!;
            }
        }
    }

    [DisableAutoCreation]
    public sealed partial class CompositionSelectionBarrier : ModificationBarrier3
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("COMPOSITION_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate(); CreationReturned = true;
        }
        protected override void OnUpdate() => throw new InvalidOperationException("COMPOSITION_BARRIER_PLAYBACK_FORBIDDEN");
        internal EntityCommandBuffer Borrow()
        {
            if (!CreationReturned || DestructionStarted || PendingCount != 1) throw new InvalidOperationException("COMPOSITION_BARRIER_INSPECTION_REJECTED");
            return PendingBuffers[0];
        }
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("COMPOSITION_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
        internal JObject Snapshot() => new JObject { ["creationStarted"] = CreationStarted, ["creationReturned"] = CreationReturned,
            ["destructionStarted"] = DestructionStarted, ["destructionReturned"] = DestructionReturned, ["sourcePlaybackCalled"] = false };
    }
}
#endif
