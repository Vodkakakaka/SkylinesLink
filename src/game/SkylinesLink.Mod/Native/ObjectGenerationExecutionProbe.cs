#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Core;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Owner = Game.Common.Owner;

namespace SkylinesLink.Mod.Native
{
    // One source-bound native generation and diagnostic capture. Definitions
    // are born Disabled; only the owning barrier may discard the unplayed ECB.
    internal sealed class ObjectGenerationExecutionProbe
    {
        private NativeProbeSourceProtection? protection;
        private ObjectGenerationBarrier? barrier;
        private ObjectGenerationProducer? producer;
        private NodeGenerationBarrier? nodeBarrier;
        private NodeGenerationProducer? nodeProducer;
        private NodeOwnerResolutionProducer? resolver;
        private NodeOwnerResolutionBarrier? resolutionBarrier;
        private BuildingInitializationProducer? initializer;
        private BuildingInitializationBarrier? initializationBarrier;
        private NodeReferencesProducer? nodeReferences;
        private SubObjectGenerationProducer? subObjectProducer;
        private SubObjectGenerationBarrier? subObjectBarrier;
        private SubObjectReferencesProducer? subObjectReferences;
        private SubNetReferencesProducer? subNetReferences;
        private PostGenerationApplicabilityProbe? postApplicability;
        private CompositionSelectionProducer? compositionProducer;
        private CompositionSelectionBarrier? compositionBarrier;
        private CompositionSelectionPreflight? compositionInputs;
        private CompositionSelectionReceipt? compositionReceipt;
        private PreparedCompositionDefinition? compositionCandidate;
        private CompositionInitializationPreflight? compositionInitializationInputs;
        private CompositionPublication? compositionPublication;
        private CompositionInitializationExpectation? compositionExpectation;
        private NodeGeometryProducer? nodeGeometryProducer;
        private NodeCostProducer? nodeCostProducer;
        private PreviewLaneExecution? previewLane;
        private PreviewRoadExecution? previewRoad;
        private PreviewValidationExecution? previewValidation;
        private PreviewCancellationExecution? previewCancellation;
        private PreviewCommitExecution? previewCommitRehearsal, previewCommit;
        private bool WorldOwnershipTransferred => previewCommit?.WorldOwnershipConfirmed == true;
        internal PreviewCommitExecution? Commit => previewCommit;
        private JObject? previewFollowups;
        private NativeSystemRegistration<ObjectGenerationBarrier>? barrierRegistration;
        private NativeSystemRegistration<ObjectGenerationProducer>? producerRegistration;
        private NativeSystemRegistration<NodeGenerationBarrier>? nodeBarrierRegistration;
        private NativeSystemRegistration<NodeGenerationProducer>? nodeProducerRegistration;
        private NativeSystemRegistration<NodeOwnerResolutionBarrier>? resolutionBarrierRegistration;
        private NativeSystemRegistration<NodeOwnerResolutionProducer>? resolverRegistration;
        private NativeSystemRegistration<BuildingInitializationProducer>? initializerRegistration;
        private NativeSystemRegistration<BuildingInitializationBarrier>? initializationBarrierRegistration;
        private NativeSystemRegistration<NodeReferencesProducer>? nodeReferencesRegistration;
        private NativeSystemRegistration<SubObjectGenerationProducer>? subObjectProducerRegistration;
        private NativeSystemRegistration<SubObjectGenerationBarrier>? subObjectBarrierRegistration;
        private NativeSystemRegistration<SubObjectReferencesProducer>? subObjectReferencesRegistration;
        private NativeSystemRegistration<SubNetReferencesProducer>? subNetReferencesRegistration;
        private NativeSystemRegistration<CompositionSelectionProducer>? compositionProducerRegistration;
        private NativeSystemRegistration<CompositionSelectionBarrier>? compositionBarrierRegistration;
        private NativeSystemRegistration<NodeGeometryProducer>? nodeGeometryRegistration;
        private NativeSystemRegistration<NodeCostProducer>? nodeCostRegistration;
        private readonly List<ComponentSystemBase> appended = new List<ComponentSystemBase>(16);
        private DefinitionProducerPatchProbe patch = new DefinitionProducerPatchProbe();
        private JObject? producerFacts;
        private JObject? commands;
        private JObject? nodeCommands;
        private ObjectGenerationPreflight? nodeAdmission;
        private DefinitionProducerPatchProbe nodePatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe resolutionPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe initializationPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe nodeReferencesPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe subObjectPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe subObjectReferencesPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe subNetReferencesPatch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe compositionPatch = new DefinitionProducerPatchProbe();
        private SubObjectGenerationPreflight? subObjectAdmission;
        private GeneratedSubObjectEntities? generatedSubObjects;
        private JObject? subObjectCommands;
        private JObject? initializationCommands, initializationReadback, subObjectInputSurvey;
        private ObjectDefinitionEntities? definitions;
        private ObjectGenerationPreflight? admission;
        private GeneratedObjectEntities? generatedEntity;
        private GeneratedNodeEntities? generatedNode;
        private bool ran, finished, emptyUpdatePassed, generationPassed, patchRechecked;
        private bool nodeProducerReleased = true, nodeBarrierReleased = true, nodeGenerationPassed, nodeEmptyPassed;
        private bool resolverReleased = true, resolutionBarrierReleased = true, resolverEmptyPassed, resolutionPassed, rootReleaseHeld;
        private string? resolverReleaseFailure, resolutionBarrierReleaseFailure;
        private bool initializerReleased = true, initializationBarrierReleased = true, initializerEmptyPassed, initializationPassed;
        private string? initializerReleaseFailure, initializationBarrierReleaseFailure;
        private bool nodeReferencesReleased = true, nodeReferencesEmptyPassed, nodeReferencesPassed;
        private string? nodeReferencesReleaseFailure;
        private bool subObjectProducerReleased = true, subObjectBarrierReleased = true, subObjectEmptyPassed, subObjectGenerationPassed;
        private bool subObjectMaterializationPassed, subObjectParentReleaseHeld;
        private bool subObjectReferencesReleased = true, subObjectReferencesEmptyPassed, subObjectReferencesPassed;
        private bool subNetReferencesReleased = true, subNetEmptyPassed, subNetPassed;
        private bool nodeOwnerConfirmationAttempted, subNetParentReleaseHeld;
        private string? subNetReferencesReleaseFailure;
        private bool compositionProducerReleased = true, compositionBarrierReleased = true;
        private bool compositionProducerReleaseCalled, compositionBarrierReleaseCalled;
        private bool nodeGeometryReleased = true, nodeGeometryReleaseCalled;
        private bool nodeCostReleased = true;
        private bool compositionEmptyPassed, compositionObserved, compositionConfirmationAttempted;
        private bool subObjectDiscardAttempted, subObjectUnlinkAttempted;
        private bool nodeMarkerExpiryAttempted, nodeDiscardAttempted, subNetUnlinkAttempted;
        private string? graphRetirementFailure;
        internal int LivePreviewGraphChecks { get; private set; }
        private string? compositionProducerReleaseFailure, compositionBarrierReleaseFailure;
        private string? subObjectReferencesReleaseFailure;
        private string? subObjectProducerReleaseFailure, subObjectBarrierReleaseFailure;
        private string? nodeProducerReleaseFailure, nodeBarrierReleaseFailure;
        private bool producerReleased = true, barrierReleased = true;
        private string? failureCode, exceptionType, producerReleaseFailure, barrierReleaseFailure, definitionsReleaseFailure;
        private double elapsedMilliseconds;
        private enum Phase { RegisterSystems, RootGeneration, NodeGeneration, RootNodeMaterialization,
            BuildingInitialization, NodeOwnerResolution, NodeReferences, SubObjectSurvey,
            SubObjectGeneration, SubObjectMaterialization, SubObjectReferences,
            PostGenerationApplicability, CompositionSelectionPreflight, CompositionSelection, CompositionInitializationPreflight,
            CompositionDefinitionPreparation, CompositionPublication,
            SubNetLink, NodeGeometry, PreviewFollowups, NodeModificationCleanup, SubNetUnlink, Release }
        private readonly long[] phaseTicks = new long[(int)Phase.Release + 1];
        private uint enteredPhases;
        private Phase? currentPhase;
        private Phase? failedPhase;
        private string? failureSite;
        private long phaseStarted;

        internal bool Finished => finished;
        internal string? FailureCode => failureCode;
        internal NewObjectPreviewEvidence? ObservedPreviewEvidence { get; private set; }
        internal JObject? CancellationSnapshot() => previewCancellation?.Snapshot();
        internal bool StructuralStateUnknown => compositionPublication?.EnteredPlayback == true && !compositionPublication.PlaybackReturned
            || previewCancellation?.StructuralStateUnknown == true || previewCommit?.StructuralStateUnknown == true;
        internal Entity PublishedComposition => compositionPublication?.PublishedEntity ?? Entity.Null;
        internal CompositionSelectionReceipt? SelectionReceipt => compositionReceipt;
        internal CompositionInitializationExpectation? InitializationExpectation => compositionExpectation;
        internal CompositionInitializationPreflight? InitializationInputs => compositionInitializationInputs;
        internal bool NativeWorkMayReadSource => (producer?.NativeWorkMayReadSource ?? false)
            || (nodeProducer?.NativeWorkMayReadSource ?? false) || (resolver?.NativeWorkMayReadSource ?? false)
            || (initializer?.NativeWorkMayReadSource ?? false) || (nodeReferences?.NativeWorkMayReadSource ?? false)
            || (subObjectProducer?.NativeWorkMayReadSource ?? false) || (subObjectReferences?.NativeWorkMayReadSource ?? false)
            || (subNetReferences?.NativeWorkMayReadSource ?? false) || (compositionProducer?.NativeWorkMayReadSource ?? false)
            || (nodeGeometryProducer?.NativeWorkMayReadSource ?? false) || (nodeCostProducer?.NativeWorkMayReadSource ?? false)
            || (previewLane?.NativeWorkMayReadSource ?? false) || (previewRoad?.NativeWorkMayReadSource ?? false)
            || (previewValidation?.NativeWorkMayReadSource ?? false) || (previewCancellation?.NativeWorkMayReadSource ?? false)
            || (previewCommit?.NativeWorkMayReadSource ?? false) || (previewCommitRehearsal?.NativeWorkMayReadSource ?? false);
        internal bool ResourcesReleased => producerReleased && barrierReleased && !NativeWorkMayReadSource
            && (definitions?.ResourcesReleased ?? true) && (admission?.ResourcesReleased ?? true)
            && (generatedEntity?.ResourcesReleased ?? true) && nodeProducerReleased && nodeBarrierReleased
            && (nodeAdmission?.ResourcesReleased ?? true) && (generatedNode?.ResourcesReleased ?? true)
            && resolverReleased && resolutionBarrierReleased && !rootReleaseHeld && initializerReleased && initializationBarrierReleased
            && nodeReferencesReleased && subObjectProducerReleased && subObjectBarrierReleased
            && (subObjectAdmission?.ResourcesReleased ?? true) && (generatedSubObjects?.ResourcesReleased ?? true)
            && !subObjectParentReleaseHeld && subObjectReferencesReleased && subNetReferencesReleased
            && !subNetParentReleaseHeld && (postApplicability?.ResourcesReleased ?? true)
            && compositionProducerReleased && compositionBarrierReleased && (compositionInputs?.ResourcesReleased ?? true)
            && (compositionPublication?.EnteredPlayback == true
                ? compositionPublication.LocalOwnershipReleased : compositionCandidate?.ResourcesReleased ?? true)
            && (compositionPublication?.CommandStorageReleased ?? true)
            && (compositionInitializationInputs?.ResourcesReleased ?? true) && nodeGeometryReleased && nodeCostReleased
            && (previewLane?.ResourcesReleased ?? true) && (previewRoad?.ResourcesReleased ?? true)
            && (previewValidation?.ResourcesReleased ?? true) && (previewCancellation?.ResourcesReleased ?? true)
            && (previewCommit?.ResourcesReleased ?? true) && (previewCommitRehearsal?.ResourcesReleased ?? true);
        internal bool ChecksPassed => finished && emptyUpdatePassed && generationPassed && patchRechecked && patch.Qualified
            && nodeGenerationPassed && nodeEmptyPassed && nodePatch.Qualified && resolverEmptyPassed && resolutionPassed && resolutionPatch.Qualified
            && initializerEmptyPassed && initializationPassed && initializationPatch.Qualified
            && nodeReferencesEmptyPassed && nodeReferencesPassed && nodeReferencesPatch.Qualified
            && subObjectEmptyPassed && subObjectGenerationPassed && subObjectPatch.Qualified && subObjectMaterializationPassed
            && subObjectReferencesEmptyPassed && (subObjectReferencesPassed || WorldOwnershipTransferred && subObjectReferences?.LinkPassed == true)
            && subObjectReferencesPatch.Qualified
            && subNetEmptyPassed && subNetReferencesPatch.Qualified
            && postApplicability?.ChecksPassed == true
            && compositionEmptyPassed && compositionObserved && compositionPatch.Qualified && generatedNode?.CompositionConfirmed == true
            && (compositionCandidate == null || compositionPublication?.LocalOwnershipReleased == true)
            && (compositionInitializationInputs == null || compositionInitializationInputs.Passed)
            && (nodeGeometryProducer == null ? subNetPassed && generatedNode?.MarkerExpiryPassed == true : (nodeGeometryProducer.GeometryPassed
                && previewLane?.Passed == true && previewRoad?.Passed == true
                && nodeCostProducer?.CostPassed == true && previewFollowups?["costs"] is JObject
                && previewValidation?.Passed == true && (previewCommit != null ? previewCommit.HandoffPassed
                    : previewCancellation?.Passed == true && previewCommitRehearsal?.RehearsalPassed == true)))
            && ResourcesReleased && protection?.Passed == true && failureCode == null;

        internal void RunOnce(World source, ObjectDefinitionPayload payload, Action requireCurrentScope, Entity expectedComposition,
            bool submit = false, bool resolveComposition = false, NewObjectPreviewEvidence? expectedPreview = null)
        {
            if (ran) throw new InvalidOperationException("OBJECT_GENERATION_ALREADY_ATTEMPTED");
            ran = true;
            var timer = Stopwatch.StartNew();
            BeginPhase(Phase.RegisterSystems);
            try
            {
                Check(MainThreadDispatcher.isMainThread, "OBJECT_GENERATION_MAIN_THREAD_REQUIRED");
                Check(!resolveComposition || !submit && expectedComposition == Entity.Null, "COMPOSITION_RESOLUTION_MODE_INVALID");
                if (source == null) throw new ArgumentNullException(nameof(source));
                if (payload == null) throw new CheckFailure("OBJECT_GENERATION_PAYLOAD_SOURCE_MISMATCH");
                Check(ReferenceEquals(payload.Source, source), "OBJECT_GENERATION_PAYLOAD_SOURCE_MISMATCH");
                if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
                requireCurrentScope();
                protection = new NativeProbeSourceProtection(source);
                Check(!protection.Contains<ObjectGenerationBarrier>() && !protection.Contains<ObjectGenerationProducer>(),
                    "OBJECT_GENERATION_OWNER_ALREADY_PRESENT");
                Check(!protection.Contains<NodeGenerationBarrier>() && !protection.Contains<NodeGenerationProducer>(),
                    "NODE_GENERATION_OWNER_ALREADY_PRESENT");
                Check(!protection.Contains<NodeOwnerResolutionBarrier>() && !protection.Contains<NodeOwnerResolutionProducer>(),
                    "NODE_RESOLUTION_OWNER_ALREADY_PRESENT");
                Check(!protection.Contains<BuildingInitializationBarrier>() && !protection.Contains<BuildingInitializationProducer>(),
                    "BUILDING_INITIALIZATION_OWNER_ALREADY_PRESENT");
                patch = ObjectGenerationPatchProbe.Observe(); Check(patch.Qualified, patch.Reason);

                barrier = new ObjectGenerationBarrier(); barrierReleased = false;
                barrierRegistration = new NativeSystemRegistration<ObjectGenerationBarrier>(source, barrier);
                barrierRegistration.Add(); appended.Add(barrier);
                requireCurrentScope();
                Check(barrier.CreationReturned && ReferenceEquals(barrier.World, source), "GENERATION_BARRIER_CREATE_UNCONFIRMED");
                ObserveProtection();

                producer = new ObjectGenerationProducer(); producerReleased = false;
                producerRegistration = new NativeSystemRegistration<ObjectGenerationProducer>(source, producer);
                producer.Configure(1, barrier);
                producerRegistration.Add(); appended.Add(producer);
                requireCurrentScope();
                Check(producer.CreationReturned && ReferenceEquals(producer.World, source), "GENERATION_PRODUCER_CREATE_UNCONFIRMED");
                ObserveProtection();
                patch = ObjectGenerationPatchProbe.Observe(); patchRechecked = true; Check(patch.Qualified, patch.Reason);
                requireCurrentScope();
                producer.ProbeEmptyUpdate();
                emptyUpdatePassed = true;
                requireCurrentScope();
                ObserveProtection();

                nodePatch = NodeGenerationPatchProbe.Observe(); Check(nodePatch.Qualified, nodePatch.Reason);
                nodeBarrier = new NodeGenerationBarrier(); nodeBarrierReleased = false;
                nodeBarrierRegistration = new NativeSystemRegistration<NodeGenerationBarrier>(source, nodeBarrier);
                nodeBarrierRegistration.Add(); appended.Add(nodeBarrier);
                requireCurrentScope();
                Check(nodeBarrier.CreationReturned && ReferenceEquals(nodeBarrier.World, source), "NODE_GENERATION_BARRIER_CREATE_UNCONFIRMED");
                ObserveProtection();
                nodeProducer = new NodeGenerationProducer(); nodeProducerReleased = false;
                nodeProducerRegistration = new NativeSystemRegistration<NodeGenerationProducer>(source, nodeProducer);
                nodeProducer.Configure(1, nodeBarrier, producer);
                nodeProducerRegistration.Add(); appended.Add(nodeProducer);
                requireCurrentScope();
                Check(nodeProducer.CreationReturned && ReferenceEquals(nodeProducer.World, source), "NODE_GENERATION_PRODUCER_CREATE_UNCONFIRMED");
                nodeProducer.ProbeEmptyUpdate(); nodeEmptyPassed = true;
                ObserveProtection();

                resolutionPatch = NodeOwnerResolutionPatchProbe.Observe(); Check(resolutionPatch.Qualified, resolutionPatch.Reason);
                resolutionBarrier = new NodeOwnerResolutionBarrier(); resolutionBarrierReleased = false;
                resolutionBarrierRegistration = new NativeSystemRegistration<NodeOwnerResolutionBarrier>(source, resolutionBarrier);
                resolutionBarrierRegistration.Add(); appended.Add(resolutionBarrier);
                requireCurrentScope();
                Check(resolutionBarrier.CreationReturned, "NODE_RESOLUTION_BARRIER_CREATE_UNCONFIRMED");
                ObserveProtection();
                resolver = new NodeOwnerResolutionProducer(); resolverReleased = false;
                resolverRegistration = new NativeSystemRegistration<NodeOwnerResolutionProducer>(source, resolver);
                resolver.Configure(1, resolutionBarrier);
                resolverRegistration.Add(); appended.Add(resolver);
                requireCurrentScope();
                Check(resolver.CreationReturned, "NODE_RESOLUTION_PRODUCER_CREATE_UNCONFIRMED");
                resolver.ProbeEmptyUpdate(); resolverEmptyPassed = true;
                ObserveProtection();

                initializationPatch = BuildingInitializationPatchProbe.Observe(); Check(initializationPatch.Qualified, initializationPatch.Reason);
                initializationBarrier = new BuildingInitializationBarrier(); initializationBarrierReleased = false;
                initializationBarrierRegistration = new NativeSystemRegistration<BuildingInitializationBarrier>(source, initializationBarrier);
                initializationBarrierRegistration.Add(); appended.Add(initializationBarrier);
                requireCurrentScope();
                Check(initializationBarrier.CreationReturned, "BUILDING_INITIALIZATION_BARRIER_CREATE_UNCONFIRMED");
                ObserveProtection();
                initializer = new BuildingInitializationProducer(); initializerReleased = false;
                initializerRegistration = new NativeSystemRegistration<BuildingInitializationProducer>(source, initializer);
                initializer.Configure(1, initializationBarrier);
                initializerRegistration.Add(); appended.Add(initializer);
                requireCurrentScope();
                Check(initializer.CreationReturned, "BUILDING_INITIALIZATION_PRODUCER_CREATE_UNCONFIRMED");
                initializer.ProbeEmptyUpdate(); initializerEmptyPassed = true;
                ObserveProtection();

                nodeReferencesPatch = NodeReferencesPatchProbe.Observe(); Check(nodeReferencesPatch.Qualified, nodeReferencesPatch.Reason);
                nodeReferences = new NodeReferencesProducer(); nodeReferencesReleased = false;
                nodeReferencesRegistration = new NativeSystemRegistration<NodeReferencesProducer>(source, nodeReferences);
                nodeReferences.Configure(1);
                nodeReferencesRegistration.Add(); appended.Add(nodeReferences);
                requireCurrentScope();
                Check(nodeReferences.CreationReturned, "NODE_REFERENCES_PRODUCER_CREATE_UNCONFIRMED");
                nodeReferencesPatch = NodeReferencesPatchProbe.Observe(); Check(nodeReferencesPatch.Qualified, nodeReferencesPatch.Reason);
                requireCurrentScope();
                nodeReferences.ProbeEmptyUpdate(requireCurrentScope); nodeReferencesEmptyPassed = true;
                ObserveProtection();

                subObjectPatch = SubObjectGenerationPatchProbe.Observe(); Check(subObjectPatch.Qualified, subObjectPatch.Reason);
                subObjectBarrier = new SubObjectGenerationBarrier(); subObjectBarrierReleased = false;
                subObjectBarrierRegistration = new NativeSystemRegistration<SubObjectGenerationBarrier>(source, subObjectBarrier);
                subObjectBarrierRegistration.Add(); appended.Add(subObjectBarrier);
                requireCurrentScope();
                Check(subObjectBarrier.CreationReturned, "SUBOBJECT_GENERATION_BARRIER_CREATE_UNCONFIRMED");
                subObjectProducer = new SubObjectGenerationProducer(); subObjectProducerReleased = false;
                subObjectProducerRegistration = new NativeSystemRegistration<SubObjectGenerationProducer>(source, subObjectProducer);
                subObjectProducer.Configure(1, subObjectBarrier);
                subObjectProducerRegistration.Add(); appended.Add(subObjectProducer);
                requireCurrentScope();
                Check(subObjectProducer.CreationReturned, "SUBOBJECT_GENERATION_PRODUCER_CREATE_UNCONFIRMED");
                subObjectPatch = SubObjectGenerationPatchProbe.Observe(); Check(subObjectPatch.Qualified, subObjectPatch.Reason);
                requireCurrentScope();
                subObjectProducer.ProbeEmptyUpdate(requireCurrentScope); subObjectEmptyPassed = true;
                ObserveProtection();

                subObjectReferencesPatch = SubObjectReferencesPatchProbe.Observe();
                Check(subObjectReferencesPatch.Qualified, subObjectReferencesPatch.Reason);
                subObjectReferences = new SubObjectReferencesProducer(); subObjectReferencesReleased = false;
                subObjectReferencesRegistration = new NativeSystemRegistration<SubObjectReferencesProducer>(source, subObjectReferences);
                subObjectReferences.Configure(1);
                subObjectReferencesRegistration.Add(); appended.Add(subObjectReferences);
                requireCurrentScope();
                Check(subObjectReferences.CreationReturned, "SUBOBJECT_REFERENCES_CREATE_UNCONFIRMED");
                subObjectReferencesPatch = SubObjectReferencesPatchProbe.Observe();
                Check(subObjectReferencesPatch.Qualified, subObjectReferencesPatch.Reason);
                subObjectReferences.ProbeEmptyUpdate(requireCurrentScope); subObjectReferencesEmptyPassed = true;
                ObserveProtection();

                subNetReferencesPatch = SubNetReferencesPatchProbe.Observe();
                Check(subNetReferencesPatch.Qualified, subNetReferencesPatch.Reason);
                subNetReferences = new SubNetReferencesProducer(); subNetReferencesReleased = false;
                subNetReferencesRegistration = new NativeSystemRegistration<SubNetReferencesProducer>(source, subNetReferences);
                subNetReferences.Configure(1); subNetReferencesRegistration.Add(); appended.Add(subNetReferences);
                requireCurrentScope();
                Check(subNetReferences.CreationReturned, "SUBNET_REFERENCES_CREATE_UNCONFIRMED");
                subNetReferences.ProbeEmptyUpdate(requireCurrentScope); subNetEmptyPassed = true;
                ObserveProtection();

                compositionPatch = CompositionSelectionPatchProbe.Observe(); Check(compositionPatch.Qualified, compositionPatch.Reason);
                compositionBarrier = new CompositionSelectionBarrier(); compositionBarrierReleased = false;
                compositionBarrierRegistration = new NativeSystemRegistration<CompositionSelectionBarrier>(source, compositionBarrier);
                compositionBarrierRegistration.Add(); appended.Add(compositionBarrier);
                requireCurrentScope(); Check(compositionBarrier.CreationReturned, "COMPOSITION_BARRIER_CREATE_UNCONFIRMED");
                ObserveProtection();
                compositionProducer = new CompositionSelectionProducer(); compositionProducerReleased = false;
                compositionProducerRegistration = new NativeSystemRegistration<CompositionSelectionProducer>(source, compositionProducer);
                compositionProducer.Configure(1, compositionBarrier);
                compositionProducerRegistration.Add(); appended.Add(compositionProducer);
                requireCurrentScope(); Check(compositionProducer.CreationReturned, "COMPOSITION_PRODUCER_CREATE_UNCONFIRMED");
                compositionProducer.ProbeEmptyUpdate(requireCurrentScope); compositionEmptyPassed = true;
                ObserveProtection();
                if (expectedComposition != Entity.Null)
                {
                    Check(!protection.Contains<NodeGeometryProducer>(), "NODE_GEOMETRY_OWNER_ALREADY_PRESENT");
                    nodeGeometryProducer = new NodeGeometryProducer();
                    nodeGeometryReleased = false;
                    nodeGeometryRegistration = new NativeSystemRegistration<NodeGeometryProducer>(source, nodeGeometryProducer);
                    nodeGeometryProducer.Configure(1);
                    nodeGeometryRegistration.Add();
                    appended.Add(nodeGeometryProducer);
                    requireCurrentScope();
                    Check(nodeGeometryProducer.CreationReturned, "NODE_GEOMETRY_CREATE_UNCONFIRMED");
                    nodeGeometryProducer.ProbeEmptyUpdate(requireCurrentScope);
                    ObserveProtection();
                }

                BeginPhase(Phase.RootGeneration);
                definitions = new ObjectDefinitionEntities(source, payload, 1);
                definitions.Create(requireCurrentScope);
                requireCurrentScope();
                ObserveProtection();
                admission = new ObjectGenerationPreflight();
                admission.Run(source, payload, requireCurrentScope);
                Check(admission.Passed && admission.ResourcesReleased && admission.SourceTreeReadCompleted,
                    admission.FailureCode ?? "OBJECT_GENERATION_SPATIAL_ADMISSION_UNQUALIFIED");
                // Do not yield or invoke player/provider callbacks between the
                // successful tree witness and this native read of the same tree.
                producer.GenerateOnce(payload, definitions, admission);
                Observe();
                Check(!NativeWorkMayReadSource && producer.CompletedFor(payload), "OBJECT_GENERATION_COMPLETION_UNCONFIRMED");
                commands = ObjectGenerationCommandProbe.Capture(producer, payload, admission.Archetype, out var generatedPayload);
                if (generatedPayload == null) throw new CheckFailure("OBJECT_GENERATION_TYPED_OUTPUT_UNSUPPORTED");

                BeginPhase(Phase.NodeGeneration);
                // All owners and definitions already exist. Recheck the real
                // tree after their structural callbacks, immediately before
                // the separate native node consumer borrows this owner map.
                nodePatch = NodeGenerationPatchProbe.Observe(); Check(nodePatch.Qualified, nodePatch.Reason);
                nodeAdmission = new ObjectGenerationPreflight();
                nodeAdmission.Run(source, payload, requireCurrentScope);
                Check(nodeAdmission.Passed && nodeAdmission.ResourcesReleased && nodeAdmission.SourceTreeReadCompleted,
                    "NODE_GENERATION_SPATIAL_ADMISSION_UNQUALIFIED");
                nodeProducer.GenerateOnce(payload, definitions, nodeAdmission);
                Observe();
                Check(!NativeWorkMayReadSource && nodeProducer.CompletedFor(payload), "NODE_GENERATION_COMPLETION_UNCONFIRMED");
                var nodeArchetype = source.EntityManager.GetComponentData<Game.Prefabs.NetData>(payload.CourseCreation.m_Prefab).m_NodeArchetype;
                nodeCommands = NodeGenerationCommandProbe.Capture(nodeProducer, payload, nodeArchetype, out var generatedNodePayload);
                Check(generatedNodePayload != null, "NODE_GENERATION_TYPED_OUTPUT_UNSUPPORTED");
                nodeGenerationPassed = true;
                BeginPhase(Phase.RootNodeMaterialization);
                generatedEntity = new GeneratedObjectEntities(source, generatedPayload, 1);
                generatedEntity.Create(requireCurrentScope);
                Check(generatedEntity.CreateReturned, "OBJECT_GENERATION_SOURCE_TEMP_UNCONFIRMED");
                generatedNode = new GeneratedNodeEntities(source, generatedNodePayload!, 1);
                generatedNode.Create(requireCurrentScope);
                Check(generatedNode.CreateReturned, "NODE_GENERATION_SOURCE_TEMP_UNCONFIRMED");
                // Bind the release dependency before any later native stage
                // or owner confirmation can fail. The empty graph port accepts
                // the node's verified birth Owner.Null until native linking.
                generatedNode.BindParentReleaseGuard(subNetReferences.RequireEmptyGraph);
                subNetReferences.Bind(generatedEntity.Entity, generatedNode.Entity, requireCurrentScope);
                generatedEntity.BindReleaseGraphGuard(subNetReferences.RequireEmptyGraph);
                BeginPhase(Phase.BuildingInitialization);
                initializationPatch = BuildingInitializationPatchProbe.Observe(); Check(initializationPatch.Qualified, initializationPatch.Reason);
                initializer.BindRoot(generatedEntity.Entity);
                initializer.InitializeOnce(requireCurrentScope);
                BuildingInitializationOutput? initializedOutput = null;
                initializer.ApplyOnce(requireCurrentScope,
                    () => initializationCommands = BuildingInitializationCommandProbe.Capture(initializer, generatedEntity.Entity, out initializedOutput));
                Check(initializer.InitializationPassed && initializedOutput != null, "BUILDING_INITIALIZATION_NOT_QUALIFIED");
                requireCurrentScope();
                initializationReadback = initializedOutput!.Verify(source.EntityManager, generatedEntity.Entity);
                initializationPassed = true;
                BeginPhase(Phase.NodeOwnerResolution);
                resolutionPatch = NodeOwnerResolutionPatchProbe.Observe(); Check(resolutionPatch.Qualified, resolutionPatch.Reason);
                requireCurrentScope();
                resolver.BindInputs(generatedEntity.Entity, generatedNode.Entity);
                resolver.ResolveOnce(requireCurrentScope);
                Check(resolver.ResolutionPassed && !NativeWorkMayReadSource, "NODE_RESOLUTION_COMPLETION_UNCONFIRMED");
                resolutionPassed = true;
                nodeOwnerConfirmationAttempted = true;
                generatedNode.ConfirmNativeOwnerLink(generatedEntity.Entity);
                BeginPhase(Phase.NodeReferences);
                requireCurrentScope();
                nodeReferencesPatch = NodeReferencesPatchProbe.Observe(); Check(nodeReferencesPatch.Qualified, nodeReferencesPatch.Reason);
                nodeReferences.BindInputs(generatedEntity.Entity, generatedNode.Entity);
                nodeReferences.UpdateOnce(requireCurrentScope);
                Check(nodeReferences.ChecksPassed && !NativeWorkMayReadSource, "NODE_REFERENCES_COMPLETION_UNCONFIRMED");
                nodeReferencesPassed = true;
                requireCurrentScope();
                BeginPhase(Phase.SubObjectSurvey);
                subObjectInputSurvey = SubObjectInputSurvey.Capture(source, generatedEntity.Entity, generatedNode.Entity, 1, requireCurrentScope);
                requireCurrentScope();
                Check((bool?)subObjectInputSurvey["completed"] == true, "SUBOBJECT_INPUT_SURVEY_INCOMPLETE");
                BeginPhase(Phase.SubObjectGeneration);
                subObjectAdmission = new SubObjectGenerationPreflight(source, generatedEntity.Entity, generatedNode.Entity, 1);
                subObjectPatch = SubObjectGenerationPatchProbe.Observe(); Check(subObjectPatch.Qualified, subObjectPatch.Reason);
                requireCurrentScope();
                subObjectProducer.GenerateOnce(subObjectAdmission, requireCurrentScope);
                Check(!NativeWorkMayReadSource, "SUBOBJECT_GENERATION_COMPLETION_UNCONFIRMED");
                subObjectCommands = SubObjectGenerationCommandProbe.Capture(subObjectProducer, subObjectAdmission, out var subObjectPayload);
                Check(subObjectPayload != null, "SUBOBJECT_GENERATION_TYPED_OUTPUT_UNSUPPORTED");
                subObjectGenerationPassed = true;
                BeginPhase(Phase.SubObjectMaterialization);
                subObjectReferences.BindParents(subObjectPayload!, requireCurrentScope);
                generatedSubObjects = new GeneratedSubObjectEntities(source, subObjectPayload!, 1);
                generatedSubObjects.BindParentGraphGuard(subObjectReferences.RequireCurrentGraph);
                generatedSubObjects.Create(requireCurrentScope);
                Check(generatedSubObjects.CreateReturned, "GENERATED_SUBOBJECT_CREATION_UNCONFIRMED");
                subObjectMaterializationPassed = true;
                BeginPhase(Phase.SubObjectReferences);
                subObjectReferences.BindChildren(generatedSubObjects, subObjectPayload!);
                subObjectReferencesPatch = SubObjectReferencesPatchProbe.Observe();
                Check(subObjectReferencesPatch.Qualified, subObjectReferencesPatch.Reason);
                subObjectReferences.LinkOnce(requireCurrentScope);
                Check(subObjectReferences.LinkPassed && !NativeWorkMayReadSource, "SUBOBJECT_REFERENCES_LINK_UNCONFIRMED");
                BeginPhase(Phase.PostGenerationApplicability);
                postApplicability = new PostGenerationApplicabilityProbe(source, generatedEntity.Entity, generatedNode.Entity, 1, requireCurrentScope);
                postApplicability.RunOnce();
                Check(postApplicability.ChecksPassed, "POST_GENERATION_APPLICABILITY_UNQUALIFIED");
                BeginPhase(Phase.CompositionSelectionPreflight);
                compositionInputs = new CompositionSelectionPreflight(subObjectPayload!, generatedNode.Entity,
                    requireCurrentScope, subObjectReferences.RequireLinkedGraph);
                compositionInputs.RunOnce();
                Check(compositionInputs.Passed && compositionInputs.ResourcesReleased, "COMPOSITION_INPUTS_UNQUALIFIED");
                BeginPhase(Phase.CompositionSelection);
                compositionPatch = CompositionSelectionPatchProbe.Observe(); Check(compositionPatch.Qualified, compositionPatch.Reason);
                compositionProducer.SelectOnce(compositionInputs, requireCurrentScope);
                Check(compositionProducer.ObservationPassed && !NativeWorkMayReadSource, "COMPOSITION_OBSERVATION_UNQUALIFIED");
                compositionReceipt = compositionProducer.Receipt;
                Check(compositionReceipt != null, "COMPOSITION_RECEIPT_UNAVAILABLE"); compositionObserved = true;
                if (expectedComposition == Entity.Null)
                    Check(compositionProducer.NeedsPublication || resolveComposition && compositionReceipt!.ObservedSelection,
                        "COMPOSITION_INITIAL_CACHE_MISS_REQUIRED");
                else
                    Check(compositionReceipt!.ObservedSelection && compositionReceipt.After.m_Composition == expectedComposition,
                        "COMPOSITION_REUSE_ENTITY_MISMATCH");
                if (compositionProducer.NeedsPublication)
                {
                    Check(compositionProducer.CreationDefinition != null, "COMPOSITION_DEFINITION_UNAVAILABLE");
                    BeginPhase(Phase.CompositionInitializationPreflight);
                    compositionInitializationInputs = new CompositionInitializationPreflight(
                        compositionProducer.CreationDefinition!, requireCurrentScope);
                    compositionInitializationInputs.RunOnce();
                    Check(compositionInitializationInputs.Passed, "COMPOSITION_INITIALIZATION_INPUTS_UNQUALIFIED");
                    compositionExpectation = compositionProducer.CaptureInitializationExpectation(compositionInitializationInputs);
                    BeginPhase(Phase.CompositionDefinitionPreparation);
                    compositionCandidate = new PreparedCompositionDefinition(compositionInputs,
                        compositionProducer.CreationDefinition!, requireCurrentScope);
                    compositionCandidate.PrepareOnce();
                    compositionInitializationInputs.RequireUnchanged();
                    BeginPhase(Phase.CompositionPublication);
                    compositionPublication = new CompositionPublication(compositionInputs,
                        compositionProducer.CreationDefinition!, requireCurrentScope);
                    compositionPublication.PublishOnce(compositionCandidate);
                    Check(compositionPublication.LocalOwnershipReleased && PublishedComposition != Entity.Null,
                        "COMPOSITION_PUBLICATION_UNQUALIFIED");
                }
                BeginPhase(Phase.CompositionSelection);
                compositionConfirmationAttempted = true;
                generatedNode.ConfirmNativeComposition(compositionReceipt!, subObjectReferences);
                Check(generatedNode.CompositionConfirmed, "COMPOSITION_NODE_CONFIRMATION_UNQUALIFIED");
                BeginPhase(Phase.SubNetLink);
                subNetReferencesPatch = SubNetReferencesPatchProbe.Observe();
                Check(subNetReferencesPatch.Qualified, subNetReferencesPatch.Reason);
                subNetReferences.LinkOnce(requireCurrentScope);
                Check(subNetReferences.LinkPassed && !NativeWorkMayReadSource, "SUBNET_REFERENCES_LINK_UNCONFIRMED");
                RequireLivePreviewGraph(requireCurrentScope);
                if (nodeGeometryProducer != null)
                {
                    BeginPhase(Phase.NodeGeometry);
                    nodeGeometryProducer.GenerateOnce(compositionReceipt!, generatedEntity.Entity, requireCurrentScope);
                    Check(nodeGeometryProducer.GeometryPassed && !NativeWorkMayReadSource, "NODE_GEOMETRY_OUTPUT_UNQUALIFIED");
                    RequireLivePreviewGraph(requireCurrentScope);
                    BeginPhase(Phase.PreviewFollowups);
                    ObservePreviewFollowups(source, requireCurrentScope, submit, payload, subObjectPayload!, expectedPreview);
                }
                else
                {
                    RetireOwnedGraph(requireCurrentScope);
                    Check(subNetPassed, "SUBNET_REFERENCES_UNLINK_UNCONFIRMED");
                }
                generationPassed = true;
                requireCurrentScope();
                ObserveProtection();
            }
            catch (Exception error)
            {
                Fail(NativeProbeFailure.CodeOf(error) ?? "OBJECT_GENERATION_LIFECYCLE_FAILED");
                var cause = error.GetBaseException();
                exceptionType = ShortType(cause);
                var method = cause.TargetSite;
                if (method != null)
                {
                    string site = (method.DeclaringType?.Name ?? "") + "." + method.Name;
                    failureSite = site.Length <= 128 ? site : site.Substring(0, 128);
                }
            }
            finally
            {
                BeginPhase(Phase.Release);
                Observe();
                if (compositionPublication?.LocalOwnershipReleased != true)
                    compositionInitializationInputs?.ReleaseSnapshots();
                if (!NativeWorkMayReadSource && !StructuralStateUnknown)
                {
                    if (compositionPublication?.EnteredPlayback != true) compositionCandidate?.ReleaseUnpublished();
                    try
                    {
                        if (previewCancellation?.GroupAbsent != true && !WorldOwnershipTransferred)
                            RetireOwnedGraph(requireCurrentScope);
                    }
                    catch (Exception error)
                    {
                        graphRetirementFailure = error is CheckFailure check ? check.Code : "OBJECT_GENERATION_GRAPH_RETIREMENT_FAILED";
                        Fail(graphRetirementFailure); exceptionType ??= ShortType(error);
                    }
                }
                BeginPhase(Phase.Release);
                // Independent release attempts: a failed producer observation
                // must not skip the barrier when no native reader was started.
                // Retirement may itself have started an unjoined native job;
                // read the live flags again before freeing any of its inputs.
                if (!NativeWorkMayReadSource && !StructuralStateUnknown)
                {
                    ReleaseNodeCost(); ReleaseNodeGeometry(); ReleaseCompositionProducer(); ReleaseCompositionBarrier();
                    ReleaseSubObjectProducer(); ReleaseSubObjectBarrier(); ReleaseNodeReferences(); ReleaseResolver(); ReleaseResolutionBarrier();
                    ReleaseInitializer(); ReleaseInitializationBarrier(); ReleaseGeneratedSubObjects();
                    ReleaseSubObjectReferences();
                    subObjectParentReleaseHeld = !WorldOwnershipTransferred && generatedSubObjects != null && !generatedSubObjects.AbsenceConfirmed;
                    if (!subObjectParentReleaseHeld) ReleaseGeneratedNode();
                    // Preserve the fixture's parent while any exact node
                    // output remains unknown, including failures before bind
                    // or native owner confirmation has completed.
                    rootReleaseHeld = !WorldOwnershipTransferred && (subObjectParentReleaseHeld || generatedNode != null && !generatedNode.AbsenceConfirmed);
                    if (!rootReleaseHeld) ReleaseGeneratedEntity();
                    subNetParentReleaseHeld = !WorldOwnershipTransferred && (generatedNode != null && !generatedNode.AbsenceConfirmed
                        || generatedEntity != null && !generatedEntity.AbsenceConfirmed);
                    if (!subNetParentReleaseHeld) ReleaseSubNetReferences();
                    ReleaseNodeProducer(); ReleaseNodeBarrier(); ReleaseDefinitions(); ReleaseProducer(); ReleaseBarrier();
                }
                else
                {
                    subObjectParentReleaseHeld = generatedSubObjects != null && !generatedSubObjects.AbsenceConfirmed;
                    rootReleaseHeld = subObjectParentReleaseHeld || generatedNode != null && !generatedNode.AbsenceConfirmed
                        || generatedEntity != null && !generatedEntity.AbsenceConfirmed;
                    subNetParentReleaseHeld = generatedNode != null && !generatedNode.AbsenceConfirmed
                        || generatedEntity != null && !generatedEntity.AbsenceConfirmed;
                }
                try { if (!StructuralStateUnknown) protection?.Observe(); }
                catch (Exception error) { Fail("OBJECT_GENERATION_PROTECTION_UNAVAILABLE"); exceptionType ??= ShortType(error); }
                if (!ResourcesReleased) Fail("OBJECT_GENERATION_RELEASE_UNCONFIRMED");
                if (protection != null && !protection.Passed) Fail("OBJECT_GENERATION_PROTECTION_CHANGED");
                EndPhase(); timer.Stop(); elapsedMilliseconds = timer.Elapsed.TotalMilliseconds; finished = true;
            }
        }

        private void Observe()
        {
            if (producer != null)
            {
                try { producerFacts = producer.Snapshot(); }
                catch (Exception error) { Fail("OBJECT_GENERATION_DIAGNOSTIC_FAILED"); exceptionType ??= ShortType(error); }
            }
        }

        private void ObserveProtection()
        {
            protection!.Observe(appended.ToArray());
            Check(protection.Passed, "OBJECT_GENERATION_PROTECTION_CHANGED");
        }

        private void RequireLivePreviewGraph(Action requireCurrentScope)
        {
            GuardLivePreviewGraph(requireCurrentScope);
            LivePreviewGraphChecks++;
        }

        private void GuardLivePreviewGraph(Action requireCurrentScope)
        {
            requireCurrentScope();
            Check(!NativeWorkMayReadSource && subNetReferences != null && subObjectReferences != null,
                "LIVE_PREVIEW_GRAPH_UNAVAILABLE");
            subNetReferences!.RequireLinkedGraph();
            subObjectReferences!.RequireLiveLinkedGraph();
            requireCurrentScope();
        }

        private void ObservePreviewFollowups(World source, Action requireScope, bool submit,
            ObjectDefinitionPayload definition, GeneratedSubObjectsPayload cohort, NewObjectPreviewEvidence? expectedPreview)
        {
            ReplacePreviewRegistrations();
            // The initialized components have already been read back. Close
            // these owners before overlapping the commit and validation owners.
            ReleaseInitializer();
            Check(initializerReleased && appended.Remove(initializer!), "BUILDING_INITIALIZATION_RELEASE_UNCONFIRMED");
            ReleaseInitializationBarrier();
            Check(initializationBarrierReleased && appended.Remove(initializationBarrier!), "BUILDING_INITIALIZATION_BARRIER_RELEASE_UNCONFIRMED");
            ObserveProtection();
            GuardLivePreviewGraph(requireScope);

            var children = generatedSubObjects!;
            Check(PreviewCommitGraph.IsNewMemberCount(children.TargetCount + 2), "PREVIEW_FOLLOWUP_GRAPH_COUNT_CHANGED");
            var graph = new Entity[children.TargetCount + 2];
            graph[0] = generatedEntity!.Entity;
            graph[1] = generatedNode!.Entity;
            // Preserve the receipt order by reading actual ownership. Native
            // child creation order has no ownership meaning across chunks.
            var entities = source.EntityManager;
            int nextBuildingSlot = 2, nodeChildSlot = graph.Length - 1, nativeNodeChildIndex = -1;
            for (int i = 0; i < children.TargetCount; i++)
            {
                Entity child = children.EntityAt(i);
                Check(child != Entity.Null && Array.IndexOf(graph, child) < 0 && entities.HasComponent<Owner>(child),
                    "PREVIEW_FOLLOWUP_CHILD_IDENTITY_CHANGED");
                Entity owner = entities.GetComponentData<Owner>(child).m_Owner;
                if (owner == graph[0])
                {
                    Check(nextBuildingSlot < nodeChildSlot, "PREVIEW_FOLLOWUP_BUILDING_CHILD_COUNT_CHANGED");
                    graph[nextBuildingSlot++] = child;
                }
                else
                {
                    Check(owner == graph[1] && nativeNodeChildIndex < 0, "PREVIEW_FOLLOWUP_NODE_CHILD_CHANGED");
                    graph[nodeChildSlot] = child;
                    nativeNodeChildIndex = i;
                }
            }
            Check(nextBuildingSlot == nodeChildSlot && nativeNodeChildIndex >= 0, "PREVIEW_FOLLOWUP_CHILD_COHORT_CHANGED");

            previewFollowups = new JObject { ["nativeNodeChildIndex"] = nativeNodeChildIndex };
            var laneInputs = PreviewLaneInputs.Read(source.EntityManager, graph);
            previewFollowups["lanes"] = laneInputs;
            Check(!protection!.Contains<PreviewLaneBarrier>() && !protection.Contains<PreviewLaneSkipSink>()
                && !protection.Contains<PreviewLaneProducer>(), "PREVIEW_LANE_OWNER_ALREADY_PRESENT");
            previewLane = new PreviewLaneExecution();
            previewLane.RunOnce(cohort, appended, requireScope,
                () => GuardLivePreviewGraph(requireScope), ObserveProtection);
            GuardLivePreviewGraph(requireScope);
            Check(JToken.DeepEquals(laneInputs, PreviewLaneInputs.Read(source.EntityManager, graph)),
                "PREVIEW_LANE_INPUTS_CHANGED_WITHOUT_PLAYBACK");
            previewFollowups["laneInputsUnchanged"] = true;

            Check(previewLane.NoLaneEntitiesRequired, "PREVIEW_ROAD_REQUIRES_LANE_MATERIALIZATION");
            previewFollowups["roads"] = PreviewRoadInputs.Read(source, graph);
            Check(!protection!.Contains<PreviewRoadBarrier>() && !protection.Contains<PreviewRoadProducer>(),
                "PREVIEW_ROAD_OWNER_ALREADY_PRESENT");
            previewRoad = new PreviewRoadExecution();
            previewRoad.RunOnce(source, graph, appended, requireScope,
                () => GuardLivePreviewGraph(requireScope), ObserveProtection);
            GuardLivePreviewGraph(requireScope);

            Check(!protection!.Contains<NodeCostProducer>(), "PREVIEW_COST_OWNER_ALREADY_PRESENT");
            nodeCostProducer = new NodeCostProducer();
            nodeCostProducer.Configure(1);
            nodeCostRegistration = new NativeSystemRegistration<NodeCostProducer>(source, nodeCostProducer);
            nodeCostReleased = false;
            nodeCostRegistration.Add();
            appended.Add(nodeCostProducer);
            requireScope();
            Check(nodeCostProducer.CreationReturned, "PREVIEW_COST_CREATE_UNCONFIRMED");
            ObserveProtection();
            GuardLivePreviewGraph(requireScope);

            var costs = new PreviewCostObservation(source, graph);
            nodeCostProducer.CalculateOnce(graph[1], requireScope);
            GuardLivePreviewGraph(requireScope);
            previewFollowups["costs"] = costs.Complete(source, graph);
            GuardLivePreviewGraph(requireScope);
            ObservedPreviewEvidence = new NewObjectPreviewEvidence(definition, entities, cohort, children, costs.ObservedCost!.Value);
            Check(expectedPreview == null || ObservedPreviewEvidence.Matches(expectedPreview),
                "REBUILD_CANCELLED_PREVIEW_CHANGED");
            previewFollowups["cancelledPreviewMatched"] = expectedPreview == null ? JValue.CreateNull() : new JValue(true);

            ReleaseNodeCost();
            Check(nodeCostReleased && appended.Remove(nodeCostProducer), "PREVIEW_COST_RELEASE_UNCONFIRMED");
            ObserveProtection();

            Check(!protection!.Contains<PreviewValidationBarrier>() && !protection.Contains<PreviewValidationProducer>(),
                "PREVIEW_VALIDATION_OWNER_ALREADY_PRESENT");
            previewValidation = new PreviewValidationExecution();
            if (submit)
            {
                previewCommit = new PreviewCommitExecution();
                previewCommit.RunOnce(source, graph, appended, requireScope,
                    () => GuardLivePreviewGraph(requireScope), ObserveProtection, () => ConfirmWorldTransfer(requireScope), previewValidation);
                return;
            }

            previewValidation.RunOnce(source, graph, appended, requireScope,
                () => GuardLivePreviewGraph(requireScope), ObserveProtection);
            GuardLivePreviewGraph(requireScope);
            previewCommitRehearsal = new PreviewCommitExecution();
            previewCommitRehearsal.RunOnce(source, graph, appended, requireScope,
                () => GuardLivePreviewGraph(requireScope), ObserveProtection, confirmWorldOwnership: null);

            previewCancellation = new PreviewCancellationExecution();
            previewCancellation.RunOnce(source, graph, appended, requireScope,
                () => GuardLivePreviewGraph(requireScope),
                () => UnlinkCancelledSubObjects(requireScope), ObserveProtection,
                () => ConfirmCancelledGroupRemoval(requireScope));
        }

        private void UnlinkCancelledSubObjects(Action requireScope)
        {
            Check(subObjectReferences != null && !subObjectUnlinkAttempted, "PREVIEW_CANCEL_REFERENCES_UNAVAILABLE");
            subObjectReferencesPatch = SubObjectReferencesPatchProbe.Observe();
            Check(subObjectReferencesPatch.Qualified, subObjectReferencesPatch.Reason);
            subObjectUnlinkAttempted = true;
            subObjectReferences!.UnlinkOnce(requireScope, afterNativeCancellation: true);
            subObjectReferencesPassed = subObjectReferences.ChecksPassed;
            Check(subObjectReferencesPassed && !NativeWorkMayReadSource, "PREVIEW_CANCEL_REFERENCES_UNCONFIRMED");
        }

        private void ConfirmCancelledGroupRemoval(Action requireScope)
        {
            generatedSubObjects!.ConfirmGroupRemoval(requireScope);
            generatedNode!.ConfirmGroupRemoval(requireScope);
            generatedEntity!.ConfirmGroupRemoval(requireScope);
            Check(generatedSubObjects.ResourcesReleased && generatedNode.ResourcesReleased && generatedEntity.ResourcesReleased,
                "PREVIEW_CANCEL_GROUP_SLOTS_UNCONFIRMED");
        }

        private void ConfirmWorldTransfer(Action requireScope)
        {
            generatedSubObjects!.ConfirmWorldTransfer(requireScope);
            generatedNode!.ConfirmWorldTransfer(requireScope);
            generatedEntity!.ConfirmWorldTransfer(requireScope);
            Check(generatedSubObjects.ResourcesReleased && generatedNode.ResourcesReleased && generatedEntity.ResourcesReleased,
                "PREVIEW_COMMIT_GROUP_SLOTS_UNCONFIRMED");
        }

        private void ReplacePreviewRegistrations()
        {
            Check(appended.Count >= 3 && ReferenceEquals(appended[appended.Count - 1], nodeGeometryProducer)
                && ReferenceEquals(appended[appended.Count - 2], compositionProducer)
                && ReferenceEquals(appended[appended.Count - 3], compositionBarrier),
                "PREVIEW_PREREQUISITE_REGISTRATION_CHANGED");
            // Release only the completed tail, in reverse registration order.
            ReleaseNodeGeometry();
            Check(nodeGeometryReleased, "PREVIEW_GEOMETRY_RELEASE_UNCONFIRMED");
            appended.RemoveAt(appended.Count - 1);
            ReleaseCompositionProducer();
            Check(compositionProducerReleased, "PREVIEW_COMPOSITION_PRODUCER_RELEASE_UNCONFIRMED");
            appended.RemoveAt(appended.Count - 1);
            ReleaseCompositionBarrier();
            Check(compositionBarrierReleased, "PREVIEW_COMPOSITION_BARRIER_RELEASE_UNCONFIRMED");
            appended.RemoveAt(appended.Count - 1);
        }

        // Normal completion and exceptional teardown use the same graph
        // retirement. Only confirmed links are advanced; the physical owners
        // still require a fresh empty graph before destroying exact handles.
        private void RetireOwnedGraph(Action requireCurrentScope)
        {
            Check(!NativeWorkMayReadSource, "OBJECT_GENERATION_GRAPH_RETIREMENT_HAS_READER");
            if (generatedSubObjects != null && !generatedSubObjects.AbsenceConfirmed)
            {
                BeginPhase(Phase.SubObjectReferences);
                if (subObjectReferences?.LinkPassed == true && !subObjectReferences.UnlinkPassed)
                {
                    requireCurrentScope();
                    if (!generatedSubObjects.DiscardReady)
                    {
                        Check(!subObjectDiscardAttempted, "SUBOBJECT_DISCARD_PREVIOUS_ATTEMPT_UNCONFIRMED");
                        subObjectDiscardAttempted = true;
                        generatedSubObjects.RequestDiscard(subObjectReferences.RequireLinkedGraph);
                    }
                    Check(generatedSubObjects.DiscardReady, "SUBOBJECT_REFERENCES_DISCARD_UNCONFIRMED");
                    subObjectReferencesPatch = SubObjectReferencesPatchProbe.Observe();
                    Check(subObjectReferencesPatch.Qualified, subObjectReferencesPatch.Reason);
                    Check(!subObjectUnlinkAttempted, "SUBOBJECT_UNLINK_PREVIOUS_ATTEMPT_UNCONFIRMED");
                    subObjectUnlinkAttempted = true;
                    subObjectReferences.UnlinkOnce(requireCurrentScope);
                    Check(subObjectReferences.UnlinkPassed && !NativeWorkMayReadSource,
                        "SUBOBJECT_REFERENCES_UNLINK_UNCONFIRMED");
                }
                if (subObjectReferences?.UnlinkPassed == true)
                {
                    Check(subObjectReferences.ChecksPassed, "SUBOBJECT_REFERENCES_UNLINK_UNCONFIRMED");
                    subObjectReferencesPassed = true;
                }
                Check(!NativeWorkMayReadSource, "OBJECT_GENERATION_GRAPH_RETIREMENT_HAS_READER");
                // Partial births and never-linked prefixes use the same
                // domain guard. An unconfirmed link cannot authorize deletion.
                ReleaseGeneratedSubObjects();
                Check(generatedSubObjects.AbsenceConfirmed, "SUBNET_CHILD_ABSENCE_UNCONFIRMED");
            }

            if (subNetReferences?.LinkPassed != true || subNetReferences.UnlinkPassed) return;
            // Deleted must not invalidate the children's live-parent guard.
            Check(generatedSubObjects == null || generatedSubObjects.AbsenceConfirmed,
                "SUBNET_CHILD_ABSENCE_UNCONFIRMED");
            var node = generatedNode ?? throw new CheckFailure("SUBNET_NODE_RETIREMENT_UNAVAILABLE");
            Check(!NativeWorkMayReadSource, "SUBNET_NODE_RETIREMENT_UNAVAILABLE");
            requireCurrentScope();
            BeginPhase(Phase.NodeModificationCleanup);
            if (!node.MarkerExpiryPassed)
            {
                Check(!nodeMarkerExpiryAttempted, "NODE_MARKER_EXPIRY_PREVIOUS_ATTEMPT_UNCONFIRMED");
                nodeMarkerExpiryAttempted = true;
                node.ExpireModificationMarkers();
            }
            Check(node.MarkerExpiryPassed && !NativeWorkMayReadSource, "NODE_MODIFICATION_CLEANUP_UNCONFIRMED");
            if (!node.DiscardReady)
            {
                Check(!nodeDiscardAttempted, "NODE_DISCARD_PREVIOUS_ATTEMPT_UNCONFIRMED");
                nodeDiscardAttempted = true; node.RequestDiscard();
            }
            Check(node.DiscardReady, "SUBNET_NODE_DISCARD_UNCONFIRMED");
            BeginPhase(Phase.SubNetUnlink);
            subNetReferencesPatch = SubNetReferencesPatchProbe.Observe();
            Check(subNetReferencesPatch.Qualified, subNetReferencesPatch.Reason);
            Check(!subNetUnlinkAttempted, "SUBNET_UNLINK_PREVIOUS_ATTEMPT_UNCONFIRMED");
            subNetUnlinkAttempted = true;
            subNetReferences.UnlinkOnce(requireCurrentScope);
            Check(subNetReferences.ChecksPassed && !NativeWorkMayReadSource, "SUBNET_REFERENCES_UNLINK_UNCONFIRMED");
            subNetPassed = true;
        }

        private void ReleaseNodeProducer()
        {
            if (nodeProducer == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_GENERATION_RELEASE_HAS_READER");
                nodeProducerRegistration!.Destroy();
                nodeProducerReleased = RegistrationReleased(nodeProducerRegistration, nodeProducer.DestructionReturned)
                    && nodeProducer.StorageReleased;
                Check(nodeProducerReleased, "NODE_GENERATION_PRODUCER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { nodeProducerReleaseFailure = error is CheckFailure check ? check.Code : "NODE_GENERATION_PRODUCER_DESTROY_FAILED"; }
            finally { Observe(); }
        }

        private void ReleaseNodeBarrier()
        {
            if (nodeBarrier == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_GENERATION_RELEASE_HAS_READER");
                Check(!nodeBarrier.CreationStarted || nodeBarrier.CreationReturned, "NODE_GENERATION_BARRIER_CREATION_UNRESOLVED");
                nodeBarrierRegistration!.Destroy();
                nodeBarrierReleased = RegistrationReleased(nodeBarrierRegistration, nodeBarrier.DestructionReturned);
                Check(nodeBarrierReleased, "NODE_GENERATION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { nodeBarrierReleaseFailure = error is CheckFailure check ? check.Code : "NODE_GENERATION_BARRIER_DESTROY_FAILED"; }
        }

        private void ReleaseSubObjectProducer()
        {
            if (subObjectProducer == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "SUBOBJECT_GENERATION_RELEASE_HAS_READER");
                subObjectProducerRegistration!.Destroy();
                subObjectProducerReleased = RegistrationReleased(subObjectProducerRegistration, subObjectProducer.DestructionReturned)
                    && subObjectProducer.StorageReleased;
                Check(subObjectProducerReleased, "SUBOBJECT_GENERATION_PRODUCER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { subObjectProducerReleaseFailure = error is CheckFailure check ? check.Code : "SUBOBJECT_GENERATION_PRODUCER_DESTROY_FAILED"; }
        }

        private void ReleaseSubObjectBarrier()
        {
            if (subObjectBarrier == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "SUBOBJECT_GENERATION_RELEASE_HAS_READER");
                subObjectBarrierRegistration!.Destroy();
                subObjectBarrierReleased = RegistrationReleased(subObjectBarrierRegistration, subObjectBarrier.DestructionReturned);
                Check(subObjectBarrierReleased, "SUBOBJECT_GENERATION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { subObjectBarrierReleaseFailure = error is CheckFailure check ? check.Code : "SUBOBJECT_GENERATION_BARRIER_DESTROY_FAILED"; }
        }

        private void ReleaseNodeReferences()
        {
            if (nodeReferences == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_REFERENCES_RELEASE_HAS_READER");
                nodeReferencesRegistration!.Destroy();
                nodeReferencesReleased = RegistrationReleased(nodeReferencesRegistration, nodeReferences.DestructionReturned)
                    && nodeReferences.StorageReleased;
                Check(nodeReferencesReleased, "NODE_REFERENCES_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { nodeReferencesReleaseFailure = error is CheckFailure check ? check.Code : "NODE_REFERENCES_DESTROY_FAILED"; }
        }

        private void ReleaseResolver()
        {
            if (resolver == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_RESOLUTION_RELEASE_HAS_READER");
                resolverRegistration!.Destroy();
                resolverReleased = RegistrationReleased(resolverRegistration, resolver.DestructionReturned) && resolver.StorageReleased;
                Check(resolverReleased, "NODE_RESOLUTION_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { resolverReleaseFailure = error is CheckFailure check ? check.Code : "NODE_RESOLUTION_DESTROY_FAILED"; }
        }

        private void ReleaseResolutionBarrier()
        {
            if (resolutionBarrier == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_RESOLUTION_RELEASE_HAS_READER");
                Check(!resolutionBarrier.CreationStarted || resolutionBarrier.CreationReturned, "NODE_RESOLUTION_BARRIER_CREATION_UNRESOLVED");
                resolutionBarrierRegistration!.Destroy();
                resolutionBarrierReleased = RegistrationReleased(resolutionBarrierRegistration, resolutionBarrier.DestructionReturned);
                Check(resolutionBarrierReleased, "NODE_RESOLUTION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { resolutionBarrierReleaseFailure = error is CheckFailure check ? check.Code : "NODE_RESOLUTION_BARRIER_DESTROY_FAILED"; }
        }

        private void ReleaseInitializer()
        {
            if (initializer == null || initializerReleased || initializerReleaseFailure != null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "BUILDING_INITIALIZATION_RELEASE_HAS_READER");
                initializerRegistration!.Destroy();
                initializerReleased = RegistrationReleased(initializerRegistration, initializer.DestructionReturned)
                    && initializer.StorageReleased;
                Check(initializerReleased, "BUILDING_INITIALIZATION_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { initializerReleaseFailure = error is CheckFailure check ? check.Code : "BUILDING_INITIALIZATION_DESTROY_FAILED"; }
        }

        private void ReleaseInitializationBarrier()
        {
            if (initializationBarrier == null || initializationBarrierReleased || initializationBarrierReleaseFailure != null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "BUILDING_INITIALIZATION_RELEASE_HAS_READER");
                Check(!initializationBarrier.CreationStarted || initializationBarrier.CreationReturned,
                    "BUILDING_INITIALIZATION_BARRIER_CREATION_UNRESOLVED");
                initializationBarrierRegistration!.Destroy();
                initializationBarrierReleased = RegistrationReleased(initializationBarrierRegistration, initializationBarrier.DestructionReturned);
                Check(initializationBarrierReleased, "BUILDING_INITIALIZATION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { initializationBarrierReleaseFailure = error is CheckFailure check ? check.Code : "BUILDING_INITIALIZATION_BARRIER_DESTROY_FAILED"; }
        }

        private void ReleaseGeneratedNode()
        {
            if (generatedNode == null) return;
            // The native job can write Owner before its later removal ECB
            // fails. Confirm that exact link even on an overall failed run.
            if (resolver?.OwnerLinkObserved == true && generatedEntity != null && !nodeOwnerConfirmationAttempted)
                try { nodeOwnerConfirmationAttempted = true; generatedNode.ConfirmNativeOwnerLink(generatedEntity.Entity); }
                catch (Exception error) { Fail("GENERATED_NODE_LINK_CONFIRMATION_FAILED"); exceptionType ??= ShortType(error); }
            if (compositionReceipt != null && !compositionConfirmationAttempted
                && (generatedSubObjects == null || generatedSubObjects.AbsenceConfirmed))
                try { compositionConfirmationAttempted = true; generatedNode.ConfirmNativeComposition(compositionReceipt, null); }
                catch (Exception error) { Fail("GENERATED_NODE_COMPOSITION_CONFIRMATION_FAILED"); exceptionType ??= ShortType(error); }
            try { generatedNode.Release(); }
            catch (Exception error) { Fail("GENERATED_NODE_RELEASE_FAILED"); exceptionType ??= ShortType(error); }
            if (!generatedNode.ResourcesReleased) Fail("GENERATED_NODE_RELEASE_UNCONFIRMED");
        }

        private void ReleaseGeneratedSubObjects()
        {
            if (generatedSubObjects == null) return;
            try { generatedSubObjects.Release(); }
            catch (Exception error) { Fail("GENERATED_SUBOBJECT_RELEASE_FAILED"); exceptionType ??= ShortType(error); }
            if (!generatedSubObjects.ResourcesReleased) Fail("GENERATED_SUBOBJECT_RELEASE_UNCONFIRMED");
        }

        private void ReleaseSubObjectReferences()
        {
            if (subObjectReferences == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "SUBOBJECT_REFERENCES_RELEASE_HAS_READER");
                Check(WorldOwnershipTransferred || generatedSubObjects == null || generatedSubObjects.AbsenceConfirmed,
                    "SUBOBJECT_REFERENCES_CHILD_ABSENCE_UNCONFIRMED");
                subObjectReferencesRegistration!.Destroy();
                subObjectReferencesReleased = RegistrationReleased(subObjectReferencesRegistration, subObjectReferences.DestructionReturned)
                    && subObjectReferences.StorageReleased;
                Check(subObjectReferencesReleased, "SUBOBJECT_REFERENCES_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { subObjectReferencesReleaseFailure = error is CheckFailure check ? check.Code : "SUBOBJECT_REFERENCES_DESTROY_FAILED"; }
        }

        private void ReleaseCompositionProducer()
        {
            if (compositionProducer == null || compositionProducerReleased || compositionProducerReleaseCalled) return;
            compositionProducerReleaseCalled = true;
            try
            {
                Check(!NativeWorkMayReadSource, "COMPOSITION_PRODUCER_RELEASE_HAS_READER");
                compositionProducerRegistration!.Destroy();
                compositionProducerReleased = RegistrationReleased(compositionProducerRegistration, compositionProducer.DestructionReturned)
                    && compositionProducer.StorageReleased;
                Check(compositionProducerReleased, "COMPOSITION_PRODUCER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { compositionProducerReleaseFailure = error is CheckFailure check ? check.Code : "COMPOSITION_PRODUCER_DESTROY_FAILED"; }
        }

        private void ReleaseNodeGeometry()
        {
            if (nodeGeometryProducer == null || nodeGeometryReleased || nodeGeometryReleaseCalled) return;
            nodeGeometryReleaseCalled = true;
            try
            {
                Check(!NativeWorkMayReadSource, "NODE_GEOMETRY_RELEASE_HAS_READER");
                nodeGeometryRegistration!.Destroy();
                nodeGeometryReleased = RegistrationReleased(nodeGeometryRegistration, nodeGeometryProducer.DestructionReturned)
                    && nodeGeometryProducer.StorageReleased;
                Check(nodeGeometryReleased, "NODE_GEOMETRY_RELEASE_UNCONFIRMED");
            }
            catch (Exception error)
            {
                Fail(error is CheckFailure check ? check.Code : "NODE_GEOMETRY_DESTROY_FAILED");
                exceptionType ??= ShortType(error);
            }
        }

        private void ReleaseNodeCost()
        {
            if (nodeCostProducer == null || nodeCostReleased) return;
            try
            {
                Check(!NativeWorkMayReadSource, "PREVIEW_COST_RELEASE_HAS_READER");
                nodeCostRegistration!.Destroy();
                nodeCostReleased = RegistrationReleased(nodeCostRegistration, nodeCostProducer.DestructionReturned)
                    && nodeCostProducer.StorageReleased;
                Check(nodeCostReleased, "PREVIEW_COST_RELEASE_UNCONFIRMED");
            }
            catch (Exception error)
            {
                Fail(error is CheckFailure check ? check.Code : "PREVIEW_COST_DESTROY_FAILED");
                exceptionType ??= ShortType(error);
            }
        }

        internal JObject? PreviewFollowupSnapshot()
        {
            if (previewFollowups == null && nodeCostProducer == null) return null;
            var result = (JObject?)previewFollowups?.DeepClone() ?? new JObject();
            result["nativeLane"] = (JToken?)previewLane?.Snapshot() ?? JValue.CreateNull();
            result["nativeRoad"] = (JToken?)previewRoad?.Snapshot() ?? JValue.CreateNull();
            result["nativeValidation"] = (JToken?)previewValidation?.Snapshot() ?? JValue.CreateNull();
            result["nativeCancellation"] = (JToken?)previewCancellation?.Snapshot() ?? JValue.CreateNull();
            result["nativeCommitRehearsal"] = (JToken?)previewCommitRehearsal?.RehearsalSnapshot() ?? JValue.CreateNull();
            result["nodeCost"] = (JToken?)nodeCostProducer?.Snapshot() ?? JValue.CreateNull();
            result["costRegistrationReleased"] = nodeCostReleased;
            return result;
        }

        internal JObject? NodeGeometrySnapshot()
        {
            JObject? geometry = nodeGeometryProducer?.Snapshot();
            if (geometry != null) geometry["registrationReleased"] = nodeGeometryReleased;
            return geometry;
        }

        private void ReleaseCompositionBarrier()
        {
            if (compositionBarrier == null || compositionBarrierReleased || compositionBarrierReleaseCalled) return;
            compositionBarrierReleaseCalled = true;
            try
            {
                Check(!NativeWorkMayReadSource, "COMPOSITION_BARRIER_RELEASE_HAS_READER");
                Check(!compositionBarrier.CreationStarted || compositionBarrier.CreationReturned,
                    "COMPOSITION_BARRIER_CREATION_UNRESOLVED");
                compositionBarrierRegistration!.Destroy();
                compositionBarrierReleased = RegistrationReleased(compositionBarrierRegistration, compositionBarrier.DestructionReturned);
                Check(compositionBarrierReleased, "COMPOSITION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { compositionBarrierReleaseFailure = error is CheckFailure check ? check.Code : "COMPOSITION_BARRIER_DESTROY_FAILED"; }
        }

        private void ReleaseSubNetReferences()
        {
            if (subNetReferences == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "SUBNET_REFERENCES_RELEASE_HAS_READER");
                Check(WorldOwnershipTransferred || (generatedNode == null || generatedNode.AbsenceConfirmed)
                    && (generatedEntity == null || generatedEntity.AbsenceConfirmed),
                    "SUBNET_REFERENCES_SOURCE_ABSENCE_UNCONFIRMED");
                subNetReferencesRegistration!.Destroy();
                subNetReferencesReleased = RegistrationReleased(subNetReferencesRegistration, subNetReferences.DestructionReturned)
                    && subNetReferences.StorageReleased;
                Check(subNetReferencesReleased, "SUBNET_REFERENCES_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { subNetReferencesReleaseFailure = error is CheckFailure check ? check.Code : "SUBNET_REFERENCES_DESTROY_FAILED"; }
        }

        private void ReleaseDefinitions()
        {
            if (definitions == null) return;
            try { definitions.Release(); }
            catch (Exception) { definitionsReleaseFailure = "GENERATION_DEFINITIONS_RELEASE_FAILED"; }
            if (!definitions.ResourcesReleased) definitionsReleaseFailure ??= "GENERATION_DEFINITIONS_RELEASE_UNCONFIRMED";
        }

        private void ReleaseGeneratedEntity()
        {
            if (generatedEntity == null) return;
            try { generatedEntity.Release(); }
            catch (Exception error) { Fail("GENERATED_SOURCE_TEMP_RELEASE_FAILED"); exceptionType ??= ShortType(error); }
            if (!generatedEntity.ResourcesReleased) Fail("GENERATED_SOURCE_TEMP_RELEASE_UNCONFIRMED");
        }

        private void ReleaseProducer()
        {
            if (producer == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "GENERATION_RELEASE_HAS_READER");
                producerRegistration!.Destroy();
                producerReleased = RegistrationReleased(producerRegistration, producer.DestructionReturned) && producer.StorageReleased;
                Check(producerReleased, "GENERATION_PRODUCER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { producerReleaseFailure = error is CheckFailure check ? check.Code : "GENERATION_PRODUCER_DESTROY_FAILED"; }
            finally { Observe(); }
        }

        private void ReleaseBarrier()
        {
            if (barrier == null) return;
            try
            {
                Check(!NativeWorkMayReadSource, "GENERATION_RELEASE_HAS_READER");
                Check(!barrier.CreationStarted || barrier.CreationReturned, "GENERATION_BARRIER_CREATION_UNRESOLVED");
                barrierRegistration!.Destroy();
                barrierReleased = RegistrationReleased(barrierRegistration, barrier.DestructionReturned);
                Check(barrierReleased, "GENERATION_BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { barrierReleaseFailure = error is CheckFailure check ? check.Code : "GENERATION_BARRIER_DESTROY_FAILED"; }
            finally { Observe(); }
        }

        private static bool RegistrationReleased<T>(NativeSystemRegistration<T> registration, bool domainDestroyReturned)
            where T : ComponentSystemBase => registration.PhysicalReleased
                && (!registration.DestroyAttempted || registration.DestroyReturned && domainDestroyReturned);

        private static JObject SystemFacts<T>(NativeSystemRegistration<T>? registration, bool created, bool destroyed,
            bool released, string? failure) where T : ComponentSystemBase
        {
            var observation = registration?.Snapshot() ?? new JObject { ["addAttempted"] = false, ["addReturned"] = false,
                ["destroyAttempted"] = false, ["destroyReturned"] = false };
            var result = new JObject();
            foreach (string field in new[] { "addAttempted", "addReturned", "destroyAttempted", "destroyReturned", "physicalReleased" })
                result[field] = observation[field]?.DeepClone() ?? new JValue(false);
            foreach (string field in new[] { "failureCode", "exceptionType", "observationFailureCode", "observationExceptionType" })
                if (observation[field]?.Type == JTokenType.String) result[field] = observation[field]!.DeepClone();
            result["creationReturned"] = created; result["destructionReturned"] = destroyed; result["released"] = released;
            if (failure != null) result["failureCode"] = failure;
            return result;
        }

        internal JObject Snapshot() => new JObject {
            ["finished"] = finished, ["checksPassed"] = ChecksPassed, ["resourcesReleased"] = ResourcesReleased,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["emptyUpdatePassed"] = emptyUpdatePassed,
            ["generationPassed"] = generationPassed,
            ["patchRechecked"] = patchRechecked, ["patchQualified"] = patch.Qualified, ["patchReason"] = patch.Reason,
            ["methodsChecked"] = patch.MethodsChecked, ["methodSetSha256"] = patch.MethodSetSha256,
            ["producer"] = SystemFacts(producerRegistration, producer?.CreationReturned ?? false,
                producer?.DestructionReturned ?? false, producerReleased, producerReleaseFailure),
            ["barrier"] = SystemFacts(barrierRegistration, barrier?.CreationReturned ?? false,
                barrier?.DestructionReturned ?? false, barrierReleased, barrierReleaseFailure),
            ["native"] = producerFacts == null ? JValue.CreateNull() : producerFacts.DeepClone(),
            ["definitions"] = definitions == null ? JValue.CreateNull() : definitions.Snapshot(),
            ["generatedEntity"] = generatedEntity == null ? JValue.CreateNull() : generatedEntity.Snapshot(),
            ["generatedNode"] = generatedNode == null ? JValue.CreateNull() : generatedNode.Snapshot(),
            ["postGeneration"] = new JObject {
                ["buildingInitialization"] = BuildingInitializationFacts(),
                ["ownerResolution"] = OwnerResolutionFacts(),
                ["nodeReferences"] = NodeReferencesFacts(),
                ["subObjects"] = SubObjectGenerationFacts(), ["subNets"] = SubNetGenerationFacts(),
                ["composition"] = CompositionSelectionFacts() },
            ["subObjectInputSurvey"] = subObjectInputSurvey == null ? JValue.CreateNull() : subObjectInputSurvey.DeepClone(),
            ["nodes"] = new JObject {
                ["emptyPassed"] = nodeEmptyPassed, ["generationPassed"] = nodeGenerationPassed,
                ["patchQualified"] = nodePatch.Qualified, ["patchReason"] = nodePatch.Reason,
                ["methodsChecked"] = nodePatch.MethodsChecked, ["methodSetSha256"] = nodePatch.MethodSetSha256,
                ["native"] = nodeProducer == null ? JValue.CreateNull() : nodeProducer.Snapshot(),
                ["producer"] = SystemFacts(nodeProducerRegistration, nodeProducer?.CreationReturned ?? false,
                    nodeProducer?.DestructionReturned ?? false, nodeProducerReleased, nodeProducerReleaseFailure),
                ["barrier"] = SystemFacts(nodeBarrierRegistration, nodeBarrier?.CreationReturned ?? false,
                    nodeBarrier?.DestructionReturned ?? false, nodeBarrierReleased, nodeBarrierReleaseFailure),
                ["admission"] = nodeAdmission == null ? JValue.CreateNull() : nodeAdmission.Snapshot(),
                ["commands"] = nodeCommands == null ? JValue.CreateNull() : nodeCommands.DeepClone() },
            ["definitionReleaseFailure"] = Value(definitionsReleaseFailure),
            ["admission"] = admission == null ? JValue.CreateNull() : admission.Snapshot(),
            ["commands"] = commands == null ? JValue.CreateNull() : commands.DeepClone(),
            ["protection"] = protection == null ? new JObject { ["observed"] = false } : protection.Snapshot(),
            ["elapsedMilliseconds"] = elapsedMilliseconds, ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType),
            ["graphRetirementFailure"] = Value(graphRetirementFailure),
            ["timing"] = TimingSnapshot(),
            ["sourceDefinitionsCreationReturned"] = definitions?.CreateReturned ?? false,
            ["generationPlaybackCalled"] = false,
            ["playbackCalled"] = (resolutionBarrier?.PlaybackStarted ?? false) || (initializationBarrier?.PlaybackStarted ?? false),
                ["scope"] = "This section reports scoped native generation, value readback and retirement. Composition ownership, normal cancellation, payment and world handoff have separate results under compositionRoundTrip. Read those results for their actual outcome; this section alone does not qualify complete asset, geometry or operational coverage."
        };

        private JObject BuildingInitializationFacts() => new JObject {
            ["emptyPassed"] = initializerEmptyPassed, ["passed"] = initializationPassed,
            ["patchQualified"] = initializationPatch.Qualified, ["patchReason"] = initializationPatch.Reason,
            ["methodsChecked"] = initializationPatch.MethodsChecked, ["methodSetSha256"] = initializationPatch.MethodSetSha256,
            ["native"] = initializer == null ? JValue.CreateNull() : initializer.Snapshot(),
            ["commands"] = initializationCommands == null ? JValue.CreateNull() : initializationCommands.DeepClone(),
            ["readback"] = initializationReadback == null ? JValue.CreateNull() : initializationReadback.DeepClone(),
            ["producer"] = SystemFacts(initializerRegistration, initializer?.CreationReturned ?? false,
                initializer?.DestructionReturned ?? false, initializerReleased, initializerReleaseFailure),
            ["barrier"] = SystemFacts(initializationBarrierRegistration, initializationBarrier?.CreationReturned ?? false,
                initializationBarrier?.DestructionReturned ?? false, initializationBarrierReleased, initializationBarrierReleaseFailure) };

        private JObject OwnerResolutionFacts() => new JObject {
            ["emptyPassed"] = resolverEmptyPassed, ["passed"] = resolutionPassed, ["rootReleaseHeld"] = rootReleaseHeld,
            ["patchQualified"] = resolutionPatch.Qualified, ["methodsChecked"] = resolutionPatch.MethodsChecked,
            ["methodSetSha256"] = resolutionPatch.MethodSetSha256, ["patchReason"] = resolutionPatch.Reason,
            ["native"] = resolver == null ? JValue.CreateNull() : resolver.Snapshot(),
            ["producer"] = SystemFacts(resolverRegistration, resolver?.CreationReturned ?? false,
                resolver?.DestructionReturned ?? false, resolverReleased, resolverReleaseFailure),
            ["barrier"] = SystemFacts(resolutionBarrierRegistration, resolutionBarrier?.CreationReturned ?? false,
                resolutionBarrier?.DestructionReturned ?? false, resolutionBarrierReleased, resolutionBarrierReleaseFailure) };

        private JObject NodeReferencesFacts() => new JObject {
            ["emptyPassed"] = nodeReferencesEmptyPassed, ["passed"] = nodeReferencesPassed,
            ["patchQualified"] = nodeReferencesPatch.Qualified, ["methodsChecked"] = nodeReferencesPatch.MethodsChecked,
            ["methodSetSha256"] = nodeReferencesPatch.MethodSetSha256, ["patchReason"] = nodeReferencesPatch.Reason,
            ["native"] = nodeReferences == null ? JValue.CreateNull() : nodeReferences.Snapshot(),
            ["producer"] = SystemFacts(nodeReferencesRegistration, nodeReferences?.CreationReturned ?? false,
                nodeReferences?.DestructionReturned ?? false, nodeReferencesReleased, nodeReferencesReleaseFailure) };

        private JObject CompositionSelectionFacts() => new JObject {
            ["emptyPassed"] = compositionEmptyPassed, ["observationPassed"] = compositionObserved,
            ["patchQualified"] = compositionPatch.Qualified, ["patchReason"] = compositionPatch.Reason,
            ["methodsChecked"] = compositionPatch.MethodsChecked, ["methodSetSha256"] = compositionPatch.MethodSetSha256,
            ["inputs"] = compositionInputs == null ? JValue.CreateNull() : compositionInputs.Snapshot(),
            ["native"] = compositionProducer == null ? JValue.CreateNull() : compositionProducer.Snapshot(),
            ["receipt"] = compositionReceipt == null ? JValue.CreateNull() : compositionReceipt.Snapshot(),
            ["candidate"] = compositionCandidate == null ? JValue.CreateNull() : compositionCandidate.Snapshot(),
            ["initializationInputs"] = compositionInitializationInputs == null ? JValue.CreateNull() : compositionInitializationInputs.Snapshot(),
            ["publication"] = compositionPublication == null ? JValue.CreateNull() : compositionPublication.Snapshot(),
            ["structuralStateUnknown"] = StructuralStateUnknown,
            ["producer"] = SystemFacts(compositionProducerRegistration, compositionProducer?.CreationReturned ?? false,
                compositionProducer?.DestructionReturned ?? false, compositionProducerReleased, compositionProducerReleaseFailure),
            ["barrier"] = SystemFacts(compositionBarrierRegistration, compositionBarrier?.CreationReturned ?? false,
                compositionBarrier?.DestructionReturned ?? false, compositionBarrierReleased, compositionBarrierReleaseFailure),
            ["selectionApplied"] = compositionProducer?.SelectionApplied ?? false,
            ["needsSharedPublication"] = compositionProducer?.NeedsPublication ?? false,
            ["nodeReferenceConfirmed"] = generatedNode?.CompositionConfirmed ?? false,
            ["sharedCacheOwnedByActor"] = false, ["sourcePlaybackCalled"] = compositionPublication?.EnteredPlayback ?? false,
            ["cacheGeometryReadyEstablished"] = false, ["fullPipelineQualified"] = false
        };

        private JObject SubNetGenerationFacts() => new JObject {
            ["emptyPassed"] = subNetEmptyPassed, ["passed"] = subNetPassed,
            ["patchQualified"] = subNetReferencesPatch.Qualified, ["patchReason"] = subNetReferencesPatch.Reason,
            ["methodsChecked"] = subNetReferencesPatch.MethodsChecked,
            ["methodSetSha256"] = subNetReferencesPatch.MethodSetSha256,
            ["native"] = subNetReferences == null ? JValue.CreateNull() : subNetReferences.Snapshot(),
            ["producer"] = SystemFacts(subNetReferencesRegistration, subNetReferences?.CreationReturned ?? false,
                subNetReferences?.DestructionReturned ?? false, subNetReferencesReleased, subNetReferencesReleaseFailure),
            ["applicability"] = postApplicability == null ? JValue.CreateNull() : postApplicability.Snapshot(),
            ["parentReleaseHeld"] = subNetParentReleaseHeld,
            ["livePreviewGraphChecks"] = LivePreviewGraphChecks,
            ["markerExpiryPassed"] = generatedNode?.MarkerExpiryPassed ?? false,
            ["nodeDiscardReady"] = generatedNode?.DiscardReady ?? false,
            ["nativeCleanupControllerInvoked"] = false, ["sourcePlaybackCalled"] = false,
            ["normalCancellationQualified"] = false
        };

        private JObject SubObjectGenerationFacts() => new JObject {
            ["emptyPassed"] = subObjectEmptyPassed, ["passed"] = subObjectGenerationPassed,
            ["patchQualified"] = subObjectPatch.Qualified, ["methodsChecked"] = subObjectPatch.MethodsChecked,
            ["methodSetSha256"] = subObjectPatch.MethodSetSha256, ["patchReason"] = subObjectPatch.Reason,
            ["native"] = subObjectProducer == null ? JValue.CreateNull() : subObjectProducer.Snapshot(),
            ["admission"] = subObjectAdmission == null ? JValue.CreateNull() : subObjectAdmission.Snapshot(),
            ["commands"] = subObjectCommands == null ? JValue.CreateNull() : subObjectCommands.DeepClone(),
            ["materializationPassed"] = subObjectMaterializationPassed,
            ["materialized"] = generatedSubObjects == null ? JValue.CreateNull() : generatedSubObjects.Snapshot(),
            ["references"] = new JObject {
                ["emptyPassed"] = subObjectReferencesEmptyPassed, ["passed"] = subObjectReferencesPassed,
                ["patchQualified"] = subObjectReferencesPatch.Qualified, ["methodsChecked"] = subObjectReferencesPatch.MethodsChecked,
                ["methodSetSha256"] = subObjectReferencesPatch.MethodSetSha256, ["patchReason"] = subObjectReferencesPatch.Reason,
                ["native"] = subObjectReferences == null ? JValue.CreateNull() : subObjectReferences.Snapshot(),
                ["producer"] = SystemFacts(subObjectReferencesRegistration, subObjectReferences?.CreationReturned ?? false,
                    subObjectReferences?.DestructionReturned ?? false, subObjectReferencesReleased, subObjectReferencesReleaseFailure) },
            ["parentReleaseHeld"] = subObjectParentReleaseHeld,
            ["producer"] = SystemFacts(subObjectProducerRegistration, subObjectProducer?.CreationReturned ?? false,
                subObjectProducer?.DestructionReturned ?? false, subObjectProducerReleased, subObjectProducerReleaseFailure),
            ["barrier"] = SystemFacts(subObjectBarrierRegistration, subObjectBarrier?.CreationReturned ?? false,
                subObjectBarrier?.DestructionReturned ?? false, subObjectBarrierReleased, subObjectBarrierReleaseFailure),
            ["sourceChildrenCreated"] = generatedSubObjects?.CreateReturned == true,
            ["sourceChildrenMayExist"] = generatedSubObjects != null && !generatedSubObjects.AbsenceConfirmed,
            ["ownerSubObjectGraphObserved"] = subObjectReferences?.LinkPassed == true, ["sourcePlaybackCalled"] = false };

        private void BeginPhase(Phase phase)
        {
            EndPhase(); currentPhase = phase; enteredPhases |= 1u << (int)phase;
            phaseStarted = Stopwatch.GetTimestamp();
        }
        private void EndPhase()
        {
            if (!currentPhase.HasValue) return;
            phaseTicks[(int)currentPhase.Value] += Stopwatch.GetTimestamp() - phaseStarted;
            currentPhase = null;
        }
        private JObject TimingSnapshot()
        {
            var phases = new JObject();
            for (int index = 0; index < phaseTicks.Length; index++)
                phases[((Phase)index).ToString()] = (enteredPhases & (1u << index)) == 0
                    ? JValue.CreateNull() : new JValue(phaseTicks[index] * (1000d / Stopwatch.Frequency));
            return new JObject { ["phaseWallMilliseconds"] = phases, ["includesNativeJoinWait"] = true,
                ["includesStatusSerialization"] = false, ["cpuOrPercentileMeasurement"] = false };
        }

        internal JObject? FailureSnapshot()
        {
            if (failureCode == null) return null;
            var snapshot = new JObject {
                ["code"] = failureCode, ["phase"] = failedPhase?.ToString(), ["type"] = exceptionType, ["site"] = failureSite
            };
            if (failedPhase == Phase.PreviewFollowups && failureCode.StartsWith("PREVIEW_LANE_", StringComparison.Ordinal))
                snapshot["laneInputs"] = previewLane?.InputSnapshot();
            if (failedPhase == Phase.SubObjectGeneration && subObjectInputSurvey?["prefabs"] is JArray prefabs && prefabs.Count == 2)
            {
                snapshot["declaredRootChildren"] = prefabs[0]?["subObjects"]?["length"]?.DeepClone();
                snapshot["declaredNodeChildren"] = prefabs[1]?["subObjects"]?["length"]?.DeepClone();
                if (failureCode == "SUBOBJECT_PREFLIGHT_ROW_BRANCH_UNSUPPORTED")
                {
                    snapshot["rowColumns"] = new JArray("prefab", "flags", "parent", "group", "probability");
                    snapshot["rootRows"] = DescribeRows(prefabs[0]);
                    snapshot["nodeRows"] = DescribeRows(prefabs[1]);
                }
            }
            return snapshot;

            static JArray? DescribeRows(JToken owner)
            {
                if (owner["subObjects"]?["rows"] is not JArray rows || rows.Count > SubObjectGenerationPreflight.MaximumNormalCreates)
                    return null;
                var result = new JArray();
                foreach (var row in rows)
                    result.Add(new JArray(Cell(row, "prefab"), Cell(row, "flags"), Cell(row, "parentIndex"),
                        Cell(row, "groupIndex"), Cell(row, "probability")));
                return result;

                static JToken Cell(JToken row, string field) => row[field]?.DeepClone() ?? JValue.CreateNull();
            }
        }

        private void Fail(string code)
        {
            if (failureCode != null) return;
            failureCode = code;
            failedPhase = currentPhase;
        }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static string ShortType(Exception error)
        { string value = error.GetType().FullName ?? error.GetType().Name; return value.Length <= 128 ? value : value.Substring(0, 128); }
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private sealed class CheckFailure : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal CheckFailure(string code)
            { Code = code; }
        }
    }
}
#endif
