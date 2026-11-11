#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Net;
using Game.Tools;
using Game.Triggers;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Owns the native consumers of a live relocation preview. The caller owns
    // the exact entities and retains them until all jobs and references settle.
    internal sealed class RelocationPreviewSuccessors
    {
        private readonly World source;
        private readonly int actor;
        private readonly Action requireScope;
        private readonly List<ComponentSystemBase> appended = new List<ComponentSystemBase>(14);
        private NativeProbeSourceProtection? protection;
        private NativeSystemRegistration<BuildingInitializationBarrier>? initializationBarrier;
        private NativeSystemRegistration<BuildingInitializationProducer>? initializer;
        private NativeSystemRegistration<NodeOwnerResolutionBarrier>? resolutionBarrier;
        private NativeSystemRegistration<NodeOwnerResolutionProducer>? resolver;
        private NativeSystemRegistration<SubObjectGenerationBarrier>? attachmentBarrier;
        private NativeSystemRegistration<SubObjectGenerationProducer>? attachments;
        private NativeSystemRegistration<SubObjectReferencesProducer>? attachmentReferences;
        private NativeSystemRegistration<CompositionSelectionBarrier>? compositionBarrier;
        private NativeSystemRegistration<CompositionSelectionProducer>? composition;
        private NativeSystemRegistration<SubNetReferencesProducer>? references;
        private NativeSystemRegistration<NodeGeometryProducer>? geometry;
        private readonly PreviewLaneExecution lanes = new PreviewLaneExecution();
        private readonly PreviewRoadExecution road = new PreviewRoadExecution();
        private NativeSystemRegistration<NodeCostProducer>? nodeCost;
        private bool nodeCostReleased = true;
        private PreviewValidationExecution? validation;
        private PreviewCancelTool? cancelTool;
        private PreviewCancelSubObjects? cancelChildren;
        private bool cancelGroupAbsent;
        private PreviewCommitObjects? preparedObjects;
        private PreviewCommitPayment? preparedPayment;
        private PreviewCommitNet? preparedNet;
        private NativeSystemRegistration<PreviewCommitNotifications>? preparedNotifications;
        private bool preparationNotificationsReleased = true;
        private int? admittedPayment;
        private RelocationCommitHandoff? commitHandoff;
        private int? playbackEntered;
        private int playbackCompleted;
        private bool slotsTransferred, eventHandoffEntered, eventHandoffReturned;
        private bool preparationOutputsReleased = true, preparationSourceUnchanged;
        private SubObjectGenerationPreflight? attachmentInput;
        private JObject? attachmentCommands, attachmentDecodeFailure;
        private GeneratedSubObjectEntities? attachmentChildren;
        private GeneratedSubObjectsPayload? attachmentCohort;
        private bool attachmentWitnessVerified, attachmentWitnessAfterBirth, attachmentWitnessAfterRetirement;
        private bool attachmentRetirementAttempted, attachmentGraphWithSubnetObserved;
        private bool graphBound, initializationReadback, linkedGraphObserved, emptyGraphObserved;
        private int initializationCommands = -1;
        private bool registrationsReleased = true;

        internal RelocationPreviewSuccessors(World source, int actor, Action requireScope)
        { this.source = source; this.actor = actor; this.requireScope = requireScope; }

        internal bool NativeWorkMayReadSource => initializer?.Instance.NativeWorkMayReadSource == true
            || resolver?.Instance.NativeWorkMayReadSource == true || attachments?.Instance.NativeWorkMayReadSource == true
            || attachmentReferences?.Instance.NativeWorkMayReadSource == true || composition?.Instance.NativeWorkMayReadSource == true
            || references?.Instance.NativeWorkMayReadSource == true || geometry?.Instance.NativeWorkMayReadSource == true
            || lanes.NativeWorkMayReadSource || road.NativeWorkMayReadSource
            || nodeCost?.Instance.NativeWorkMayReadSource == true || validation?.NativeWorkMayReadSource == true
            || cancelTool?.NativeWorkMayReadSource == true || cancelChildren?.NativeWorkMayReadSource == true
            || preparedObjects?.NativeWorkMayReadSource == true || preparedPayment?.NativeWorkMayReadSource == true
            || preparedNet?.NativeWorkMayReadSource == true || preparedNotifications?.Instance.NativeWorkMayReadSource == true;
        internal bool CancellationEntered => cancelTool?.EffectsStarted == true;
        internal bool CommitEffectEntered => preparedPayment?.EffectEntered == true;
        internal bool CommitWorldOwnershipConfirmed => commitHandoff?.WorldOwnershipConfirmed == true;
        internal bool CommitOwnershipSettled => slotsTransferred && CommitWorldOwnershipConfirmed;
        internal bool CommitPublished => CommitOwnershipSettled && eventHandoffReturned && playbackCompleted == 3
            && preparedPayment?.Passed == true && preparedNet?.Passed == true;
        internal bool CommitDefaultReady => commitHandoff?.DefaultReady == true;
        internal void ObserveCommittedWorld() => commitHandoff!.ObserveDefault(requireScope);
        internal void RequireSeparateBuild(Entity root, Entity node) => commitHandoff!.RequireSeparateBuild(root, node, requireScope);
        internal bool AttachmentRetirementPending => !CommitEffectEntered && !CancellationEntered && attachmentChildren != null && !attachmentRetirementAttempted;
        internal bool AttachmentsRetired => attachmentChildren?.AbsenceConfirmed != false
            && (attachmentReferences?.Instance.NativeCallStarted != true || attachmentReferences.Instance.UnlinkPassed);
        internal bool CanRetireInputs => !CommitEffectEntered && !NativeWorkMayReadSource && AttachmentsRetired
            && (CancellationEntered ? cancelGroupAbsent : references?.Instance.NativeCallStarted != true || references.Instance.UnlinkPassed);
        internal bool ResourcesReleased => !NativeWorkMayReadSource && registrationsReleased
            && lanes.ResourcesReleased && road.ResourcesReleased && nodeCostReleased && validation?.ResourcesReleased != false
            && cancelTool?.ResourcesReleased != false && cancelChildren?.ResourcesReleased != false
            && preparationOutputsReleased && preparedObjects?.ResourcesReleased != false
            && preparedPayment?.ResourcesReleased != false && preparedNet?.ResourcesReleased != false && preparationNotificationsReleased
            && attachmentInput?.ResourcesReleased != false && attachmentChildren?.ResourcesReleased != false;
        internal bool OwnerWriteMayHaveOccurred => resolver?.Instance.OwnerWriteMayHaveOccurred == true;
        internal bool LinkPassed => references?.Instance.LinkPassed == true;
        internal bool Passed => initializationReadback && resolver?.Instance.ResolutionPassed == true
            && attachmentWitnessAfterRetirement && attachmentGraphWithSubnetObserved && AttachmentsRetired
            && attachments?.Instance.GenerationPassed == true && attachmentReferences?.Instance.ChecksPassed == true
            && composition?.Instance.RelocationSelectionPassed == true && geometry?.Instance.GeometryPassed == true
            && lanes.Passed && road.Passed && nodeCost?.Instance.CostPassed == true && validation?.Passed == true
            && preparedObjects?.Passed == true && preparationSourceUnchanged && admittedPayment > 0
            && preparedNotifications?.Instance.Passed == true
            && cancelTool?.Passed == true && cancelChildren?.Passed == true && cancelGroupAbsent
            && references?.Instance.LinkPassed == true && linkedGraphObserved
            && ResourcesReleased && protection?.Passed == true;

        internal void Prepare()
        {
            requireScope();
            protection = new NativeProbeSourceProtection(source);
            Require(!protection.Contains<BuildingInitializationBarrier>() && !protection.Contains<BuildingInitializationProducer>()
                && !protection.Contains<NodeOwnerResolutionBarrier>() && !protection.Contains<NodeOwnerResolutionProducer>()
                && !protection.Contains<SubObjectGenerationBarrier>() && !protection.Contains<SubObjectGenerationProducer>()
                && !protection.Contains<SubObjectReferencesProducer>() && !protection.Contains<SubNetReferencesProducer>()
                && !protection.Contains<CompositionSelectionBarrier>() && !protection.Contains<CompositionSelectionProducer>()
                && !protection.Contains<NodeGeometryProducer>()
                && !protection.Contains<PreviewLaneBarrier>() && !protection.Contains<PreviewLaneSkipSink>()
                && !protection.Contains<PreviewLaneProducer>() && !protection.Contains<PreviewRoadBarrier>()
                && !protection.Contains<PreviewRoadProducer>() && !protection.Contains<NodeCostProducer>()
                && !protection.Contains<PreviewValidationBarrier>() && !protection.Contains<PreviewValidationProducer>()
                && !protection.Contains<PreviewCancelToolBarrier>() && !protection.Contains<PreviewCancelClearProducer>()
                && !protection.Contains<PreviewCancelSubObjectBarrier>() && !protection.Contains<PreviewCancelSubObjectProducer>()
                && !protection.Contains<PreviewCommitBarrier>() && !protection.Contains<PreviewCommitTriggerSink>()
                && !protection.Contains<PreviewCommitObjectProducer>() && !protection.Contains<PreviewCommitPaymentProducer>()
                && !protection.Contains<PreviewCommitNetProducer>() && !protection.Contains<PreviewCommitNotifications>(),
                "RELOCATION_PREVIEW_SUCCESSOR_ALREADY_PRESENT");

            Require(BuildingInitializationPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_INITIALIZATION_PATCHED");
            initializationBarrier = new NativeSystemRegistration<BuildingInitializationBarrier>(source, new BuildingInitializationBarrier());
            Register(initializationBarrier);
            var init = new BuildingInitializationProducer();
            init.Configure(actor, initializationBarrier.Instance);
            initializer = new NativeSystemRegistration<BuildingInitializationProducer>(source, init);
            Register(initializer);
            init.ProbeEmptyUpdate();
            ObserveProtection();

            Require(NodeOwnerResolutionPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_OWNER_KERNEL_PATCHED");
            resolutionBarrier = new NativeSystemRegistration<NodeOwnerResolutionBarrier>(source, new NodeOwnerResolutionBarrier());
            Register(resolutionBarrier);
            var resolution = new NodeOwnerResolutionProducer();
            resolution.ConfigureRelocation(actor, resolutionBarrier.Instance);
            resolver = new NativeSystemRegistration<NodeOwnerResolutionProducer>(source, resolution);
            Register(resolver);
            resolution.ProbeEmptyUpdate();
            ObserveProtection();

            Require(SubObjectGenerationPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_KERNEL_PATCHED");
            attachmentBarrier = new NativeSystemRegistration<SubObjectGenerationBarrier>(source, new SubObjectGenerationBarrier());
            Register(attachmentBarrier);
            var generation = new SubObjectGenerationProducer();
            generation.Configure(actor, attachmentBarrier.Instance);
            attachments = new NativeSystemRegistration<SubObjectGenerationProducer>(source, generation);
            Register(attachments);
            generation.ProbeEmptyUpdate(requireScope);
            ObserveProtection();

            Require(SubObjectReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_REFERENCES_PATCHED");
            var childRefs = new SubObjectReferencesProducer();
            childRefs.Configure(actor);
            attachmentReferences = new NativeSystemRegistration<SubObjectReferencesProducer>(source, childRefs);
            Register(attachmentReferences);
            childRefs.ProbeEmptyUpdate(requireScope);
            ObserveProtection();

            Require(CompositionSelectionPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_COMPOSITION_KERNEL_PATCHED");
            compositionBarrier = new NativeSystemRegistration<CompositionSelectionBarrier>(source, new CompositionSelectionBarrier());
            Register(compositionBarrier);
            var selection = new CompositionSelectionProducer();
            selection.Configure(actor, compositionBarrier.Instance);
            composition = new NativeSystemRegistration<CompositionSelectionProducer>(source, selection);
            Register(composition);
            selection.ProbeEmptyUpdate(requireScope);
            ObserveProtection();

            Require(SubNetReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_REFERENCES_KERNEL_PATCHED");
            var refs = new SubNetReferencesProducer();
            refs.Configure(actor);
            references = new NativeSystemRegistration<SubNetReferencesProducer>(source, refs);
            Register(references);
            refs.ProbeEmptyUpdate(requireScope);
            ObserveProtection();

            var nodes = new NodeGeometryProducer();
            nodes.Configure(actor);
            geometry = new NativeSystemRegistration<NodeGeometryProducer>(source, nodes);
            Register(geometry);
            nodes.ProbeEmptyUpdate(requireScope);
            ObserveProtection();
        }

        internal void BindAndInitialize(Entity root, Entity node, RelocationPreviewPayload payload)
        {
            requireScope();
            var original = payload.Definitions.Original;
            references!.Instance.BindRelocation(root, node, payload.RootTemp, payload.NewNodeTemp, requireScope);
            graphBound = true;

            Require(BuildingInitializationPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_INITIALIZATION_PATCHED");
            var init = initializer!.Instance;
            init.BindRelocationRoot(root, original.Root, original.Prefab, payload.RootTemp);
            init.InitializeOnce(requireScope);
            BuildingInitializationOutput? result = null;
            init.ApplyOnce(requireScope, () =>
            {
                var receipt = BuildingInitializationCommandProbe.Capture(init, root, out result);
                initializationCommands = receipt.Value<int>("commandsObserved");
                Require(initializationCommands == 0 && init.BuildingCount == 1 && init.CoverageCount == 0
                    && init.PendingCount == 1, "RELOCATION_PREVIEW_INITIALIZATION_OUTPUT_CHANGED");
            });
            Require(init.InitializationPassed && result != null && !NativeWorkMayReadSource,
                "RELOCATION_PREVIEW_INITIALIZATION_UNCONFIRMED");
            requireScope();
            result!.Verify(source.EntityManager, root);
            initializationReadback = true;
            RequireEmptyGraph();
            ObserveProtection();
        }

        internal void ResolveOwner(Entity root, Entity node, RelocationPreviewPayload payload)
        {
            requireScope();
            Require(NodeOwnerResolutionPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_OWNER_KERNEL_PATCHED");
            var resolution = resolver!.Instance;
            resolution.BindRelocationInputs(root, node, payload.RootTemp, payload.NewNodeTemp);
            resolution.ResolveOnce(requireScope);
            Require(resolution.ResolutionPassed && resolution.OwnerDefinitionRemovalMayHaveApplied
                && resolution.ObservedOwner == root && resolution.ObservedOwnerDefinitionPresent == false
                && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_OWNER_UNCONFIRMED");
            RequireEmptyGraph();
            ObserveProtection();
        }

        internal void QualifyAttachments(Entity root, Entity oldNode, Entity newNode, RelocationPreviewPayload payload)
        {
            RequireEmptyGraph();
            attachmentInput = new SubObjectGenerationPreflight(source, root, oldNode, newNode, actor,
                payload.RootTemp, payload.OldNodeTemp, payload.NewNodeTemp, payload.Definitions.Original);
            Require(SubObjectGenerationPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_KERNEL_PATCHED");
            var generation = attachments!.Instance;
            generation.GenerateRelocationOnce(attachmentInput, requireScope);
            Require(generation.GenerationPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_ATTACHMENT_UNCONFIRMED");
            requireScope();
            attachmentInput.RequireRelocationWitness(requireScope);
            attachmentWitnessVerified = true;
            GeneratedSubObjectsPayload? captured;
            try { attachmentCommands = SubObjectGenerationCommandProbe.CaptureRelocation(generation, attachmentInput, out captured); }
            catch (Exception error)
            {
                attachmentDecodeFailure = DescribeDecodeFailure(error);
                throw;
            }
            Require(captured != null, "RELOCATION_PREVIEW_ATTACHMENT_COHORT_INCOMPLETE");
            attachmentCohort = captured;
            RequireEmptyGraph();
            ObserveProtection();

            // Only admitted scalar values cross the private command boundary.
            var refs = attachmentReferences!.Instance;
            refs.BindRelocationParents(captured!, requireScope);
            var children = new GeneratedSubObjectEntities(source, captured!, actor);
            attachmentChildren = children;
            children.BindParentGraphGuard(refs.RequireCurrentGraph);
            attachmentInput.RequireRelocationWitness(requireScope);
            children.Create(requireScope);
            attachmentInput.RequireRelocationWitness(requireScope);
            attachmentWitnessAfterBirth = true;

            refs.BindChildren(children, captured!);
            Require(SubObjectReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_REFERENCES_PATCHED");
            refs.LinkOnce(requireScope);
            Require(refs.LinkPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_ATTACHMENT_LINK_UNCONFIRMED");
            refs.RequireLiveLinkedGraph();
            RequireEmptyGraph();
            ObserveProtection();
        }

        internal (CompositionSelectionReceipt Old, CompositionSelectionReceipt New) SelectCompositions()
        {
            RequireEmptyGraph();
            Require(!NativeWorkMayReadSource && attachmentCohort != null, "RELOCATION_PREVIEW_COMPOSITION_INPUTS_UNAVAILABLE");
            var cohort = attachmentCohort!;
            Action requireChildren = attachmentReferences!.Instance.RequireLiveLinkedGraph;
            var oldNode = new CompositionSelectionPreflight(source, cohort.Root, cohort.OldNode, actor,
                requireScope, requireChildren, cohort);
            var newNode = new CompositionSelectionPreflight(source, cohort.Root, cohort.Node, actor,
                requireScope, requireChildren, cohort);
            oldNode.RunOnce();
            newNode.RunOnce();
            Require(CompositionSelectionPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_COMPOSITION_KERNEL_PATCHED");
            var result = composition!.Instance.SelectRelocationOnce(oldNode, newNode, requireScope);
            Require(!NativeWorkMayReadSource, "RELOCATION_PREVIEW_COMPOSITION_NOT_JOINED");
            requireChildren();
            RequireEmptyGraph();
            ObserveProtection();
            return result;
        }

        internal void RetireAttachments()
        {
            var children = attachmentChildren;
            if (children == null) return;
            Require(!attachmentRetirementAttempted && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_ATTACHMENT_RETIREMENT_BLOCKED");
            attachmentRetirementAttempted = true;
            var refs = attachmentReferences!.Instance;
            if (refs.NativeCallStarted)
            {
                Require(refs.LinkPassed, "RELOCATION_PREVIEW_ATTACHMENT_LINK_UNKNOWN");
                children.RequestDiscard(refs.RequireLinkedGraph);
                Require(SubObjectReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_REFERENCES_PATCHED");
                refs.UnlinkOnce(requireScope);
                Require(refs.ChecksPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_ATTACHMENT_UNLINK_UNCONFIRMED");
            }

            // A never-entered native call still permits the exact physical
            // prefix to retire under the same fresh empty graph guard.
            children.Release();
            Require(AttachmentsRetired && children.ResourcesReleased, "RELOCATION_PREVIEW_ATTACHMENT_RETIREMENT_UNCONFIRMED");
            if (attachmentWitnessAfterBirth)
            {
                attachmentInput!.RequireRelocationWitness(requireScope);
                attachmentWitnessAfterRetirement = true;
            }
            ObserveProtection();
        }

        private static JObject DescribeDecodeFailure(Exception error)
        {
            Exception cause = error;
            for (int index = 0; index < 4 && cause.InnerException != null; index++) cause = cause.InnerException;
            var trace = new StackTrace(cause, false);
            var frames = new JArray();
            for (int index = 0; index < trace.FrameCount && index < 8; index++)
            {
                var frame = trace.GetFrame(index);
                var method = frame?.GetMethod();
                string? type = method?.DeclaringType?.FullName;
                if (type == null || type.Length > 128 || method == null || method.Name.Length > 64) continue;
                if (!type.StartsWith("SkylinesLink.Mod.Native.", StringComparison.Ordinal)
                    && !type.StartsWith("Unity.Entities.", StringComparison.Ordinal)
                    && !type.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal)
                    && !type.StartsWith("System.", StringComparison.Ordinal)) continue;
                frames.Add(new JObject { ["type"] = type, ["method"] = method.Name, ["ilOffset"] = frame!.GetILOffset() });
            }
            return new JObject { ["exception"] = error.GetType().Name, ["cause"] = cause.GetType().Name, ["frames"] = frames };
        }

        internal void Link()
        {
            requireScope();
            Require(SubNetReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_REFERENCES_KERNEL_PATCHED");
            var refs = references!.Instance;
            refs.LinkOnce(requireScope);
            Require(refs.LinkPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_LINK_UNCONFIRMED");
            refs.RequireLinkedGraph();
            linkedGraphObserved = true;
            attachmentReferences!.Instance.RequireLiveLinkedGraph();
            attachmentGraphWithSubnetObserved = true;
            ObserveProtection();
        }

        internal (NodeGeometry Old, NodeGeometry New) GenerateGeometry(
            CompositionSelectionReceipt oldNode, CompositionSelectionReceipt newNode)
        {
            Require(!NativeWorkMayReadSource && attachmentCohort != null, "RELOCATION_PREVIEW_GEOMETRY_INPUTS_UNAVAILABLE");
            var result = geometry!.Instance.GenerateRelocationOnce(attachmentCohort!, oldNode, newNode, requireScope, RequireLiveGraph);
            Require(geometry.Instance.GeometryPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_GEOMETRY_UNCONFIRMED");
            RequireLiveGraph();
            ObserveProtection();
            return result;
        }

        internal void GenerateLanes(CompositionSelectionReceipt oldNode, CompositionSelectionReceipt newNode,
            (NodeGeometry Old, NodeGeometry New) generatedGeometry)
        {
            Require(!NativeWorkMayReadSource && geometry?.Instance.GeometryPassed == true && attachmentCohort != null,
                "RELOCATION_PREVIEW_LANE_INPUTS_UNAVAILABLE");
            lanes.RunRelocationOnce(source, attachmentCohort!, oldNode, newNode, generatedGeometry,
                appended, requireScope, RequireLiveGraph, ObserveProtection);
        }

        internal void GenerateRoad(Action requireOriginal)
        {
            Require(!NativeWorkMayReadSource && lanes.Passed && attachmentCohort != null,
                "RELOCATION_PREVIEW_ROAD_INPUTS_UNAVAILABLE");
            void RequireRoadGraph()
            {
                RequireLiveGraph();
                requireOriginal();
            }
            road.RunRelocationOnce(source, attachmentCohort!, appended, requireScope, RequireRoadGraph, ObserveProtection);
        }

        internal void CalculateNodeCost(Action requireOriginal)
        {
            Require(!NativeWorkMayReadSource && road.Passed && attachmentCohort != null && nodeCost == null,
                "RELOCATION_PREVIEW_NODE_COST_INPUTS_UNAVAILABLE");
            var producer = new NodeCostProducer();
            producer.Configure(actor);
            nodeCost = new NativeSystemRegistration<NodeCostProducer>(source, producer);
            nodeCostReleased = false;
            try
            {
                Register(nodeCost);
                producer.CalculateRelocationOnce(attachmentCohort!, Guard);
            }
            finally
            {
                if (!producer.NativeWorkMayReadSource)
                    nodeCostReleased = Retire(nodeCost, value => !value.CreationStarted
                        || value.DestructionReturned && value.StorageReleased);
            }
            Require(producer.CostPassed && nodeCostReleased, "RELOCATION_PREVIEW_NODE_COST_UNCONFIRMED");
            ObserveProtection();

            void Guard()
            {
                RequireLiveGraph();
                requireOriginal();
            }
        }

        internal void CheckEligibility(RelocationDefinitionSource original, Action requireOriginal)
        {
            Require(!NativeWorkMayReadSource && nodeCost?.Instance.CostPassed == true && nodeCostReleased
                && attachmentCohort != null && attachmentChildren?.TargetCount == 6 && validation == null,
                "RELOCATION_PREVIEW_VALIDATION_INPUTS_UNAVAILABLE");
            var (graph, temps) = CaptureMembers();
            validation = new PreviewValidationExecution();
            var input = new PreviewValidationExecution.RelocationInput(actor, original, temps);
            validation.RunOnce(source, graph, appended, requireScope, Guard, ObserveProtection, input);

            void Guard()
            {
                RequireLiveGraph();
                requireOriginal();
            }
        }

        internal void PrepareApplyObjects(RelocationDefinitionSource original, Action requireOriginal,
            Action<Entity>? confirmParentTransfer = null)
        {
            Require(validation?.Passed == true && !NativeWorkMayReadSource && preparedObjects == null,
                "RELOCATION_PREVIEW_OBJECT_PREPARATION_UNAVAILABLE");
            var (graph, temps) = CaptureMembers();
            var output = new NativeSystemRegistration<PreviewCommitBarrier>(source, new PreviewCommitBarrier());
            var triggers = new NativeSystemRegistration<PreviewCommitTriggerSink>(source, new PreviewCommitTriggerSink());
            preparedObjects = new PreviewCommitObjects();
            preparedPayment = new PreviewCommitPayment();
            preparedNet = new PreviewCommitNet();
            preparationOutputsReleased = false;
            try
            {
                Guard();
                Register(output);
                Register(triggers);
                preparedObjects.RunOnce(source, graph, output.Instance, triggers.Instance, appended,
                    requireScope, Guard, ObserveProtection, actor, temps);
                var action = triggers.Instance.CaptureRelocationObjectJoined(preparedObjects.CompletedDependency);
                preparedObjects.RequireRelocationTrigger(action);

                preparedNet.AdmitRelocation(source, graph, temps, original, actor, output.Instance, triggers.Instance,
                    appended, requireScope, Guard, ObserveProtection);
                var notifications = new PreviewCommitNotifications();
                notifications.Configure(output.Instance, actor, graph, temps);
                preparedNotifications = new NativeSystemRegistration<PreviewCommitNotifications>(source, notifications);
                preparationNotificationsReleased = false;
                Register(preparedNotifications);
                notifications.Prepare(PreviewCommitBarrier.RelocationNotificationBufferIndex, Guard);
                admittedPayment = preparedPayment.AdmitRelocation(source, graph, temps, actor, output.Instance,
                    appended, requireScope, Guard, ObserveProtection);
                Require(notifications.Passed, "RELOCATION_PREVIEW_NOTIFICATION_PREPARATION_UNCONFIRMED");
                preparedObjects.RequirePreparedInputs(source, graph);
                Guard();
                Require(!preparedPayment.EffectEntered && output.Instance.PendingCount == 2 && triggers.Instance.QueueCount == 1,
                    "RELOCATION_PREVIEW_APPLY_ADMISSION_CHANGED_OUTPUT");
                preparationSourceUnchanged = true;

                if (confirmParentTransfer != null)
                {
                    var trigger = source.GetExistingSystemManaged<TriggerSystem>();
                    Require(trigger != null && trigger.Enabled && ReferenceEquals(trigger.World, source),
                        "RELOCATION_PREVIEW_COMMIT_WORLD_TRIGGER_UNAVAILABLE");
                    commitHandoff = new RelocationCommitHandoff(source, original, actor, graph, temps, action.m_PrimaryTarget);
                    int charged = admittedPayment.Value;
                    preparedPayment.PayRelocation(graph, output.Instance, requireScope, GuardBeforePay, GuardAfterPay, ObserveProtection);
                    Require(preparedPayment.OutputConfirmed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_COMMIT_PAYMENT_UNCONFIRMED");
                    preparedNet.ApplyRelocation(graph, temps, original, requireScope, GuardAfterPay, ObserveProtection);
                    Require(!NativeWorkMayReadSource, "RELOCATION_PREVIEW_COMMIT_NET_UNJOINED");
                    triggers.Instance.RequireRelocationNetEmpty(preparedNet.CompletedDependency);
                    notifications.RequirePrepared();
                    original.RequireBalanceAfterCharge(charged);
                    Require(PreviewCommitObjectPatchProbe.Observe(relocation: true).Qualified,
                        "RELOCATION_PREVIEW_COMMIT_PLAYBACK_PATCH_CHANGED");
                    Playback(PreviewCommitBarrier.ObjectBufferIndex);
                    Playback(PreviewCommitBarrier.RelocationNetBufferIndex);
                    Playback(PreviewCommitBarrier.RelocationNotificationBufferIndex);
                    var entities = source.EntityManager;
                    preparedObjects.RequireRelocationApplied(entities);
                    notifications.RequireApplied(entities);
                    original.RequireBalanceAfterCharge(charged);
                    commitHandoff.Publish(requireScope);
                    confirmParentTransfer(commitHandoff.NodeEntity);
                    attachmentChildren!.ConfirmMixedTransfer(commitHandoff.Leaf, requireScope);
                    slotsTransferred = true;
                    requireScope();
                    Require(ReferenceEquals(source.GetExistingSystemManaged<TriggerSystem>(), trigger) && trigger!.Enabled,
                        "RELOCATION_PREVIEW_COMMIT_WORLD_TRIGGER_CHANGED");
                    eventHandoffEntered = true;
                    trigger!.CreateActionBuffer().Enqueue(action);
                    eventHandoffReturned = true;

                    void GuardBeforePay()
                    {
                        Guard();
                        preparedObjects.RequirePreparedInputs(source, graph);
                        notifications.RequirePrepared();
                    }

                    void GuardAfterPay()
                    {
                        RequireLiveGraph();
                        original.RequireUnchanged(source, original.Prefab, requireScope, charged);
                    }

                    void Playback(int slot)
                    {
                        requireScope();
                        var commands = output.Instance.Borrow(slot);
                        playbackEntered = slot;
                        commands.Playback(source.EntityManager);
                        playbackCompleted++;
                    }
                }
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    preparationNotificationsReleased = Retire(preparedNotifications, value => value.StorageReleased);
                    preparedNet.ReleaseProducer(appended);
                    preparedPayment.ReleaseProducer(appended);
                    bool triggersReleased = Retire(triggers, value => value.StorageReleased);
                    bool outputReleased = Retire(output, value => value.StorageReleased);
                    preparationOutputsReleased = triggersReleased && outputReleased;
                }
            }
            Require(preparationOutputsReleased, "RELOCATION_PREVIEW_OBJECT_PREPARATION_RELEASE_UNCONFIRMED");
            ObserveProtection();

            void Guard()
            {
                RequireLiveGraph();
                requireOriginal();
            }
        }



        internal Entity[] Cancel(Action requireOriginal, Action confirmParentMarkers)
        {
            Require(validation?.Passed == true && !NativeWorkMayReadSource && cancelTool == null,
                "RELOCATION_PREVIEW_CANCELLATION_UNAVAILABLE");
            var (graph, temps) = CaptureMembers();
            attachmentInput!.RequireRelocationOriginalWitness(requireScope);
            cancelTool = new PreviewCancelTool();
            cancelTool.RunOnce(source, graph, appended, requireScope, GuardLive, ObserveProtection, actor, temps);
            Require(cancelTool.Passed, "RELOCATION_PREVIEW_CANCEL_TOOL_UNCONFIRMED");
            confirmParentMarkers();

            var refs = attachmentReferences!.Instance;
            refs.AdmitRelocationCancellation();
            cancelChildren = new PreviewCancelSubObjects();
            cancelChildren.RunOnce(source, graph, appended, requireScope, GuardMarked, ObserveProtection, actor, temps);
            Require(cancelChildren.Passed, "RELOCATION_PREVIEW_CANCEL_CHILDREN_UNCONFIRMED");
            attachmentChildren!.RequireNativeCancellationShape();
            Require(SubObjectReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_ATTACHMENT_REFERENCES_PATCHED");
            refs.UnlinkOnce(requireScope, afterNativeCancellation: true);
            Require(refs.ChecksPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_CANCEL_UNLINK_UNCONFIRMED");
            ObserveProtection();
            return graph;

            void GuardLive()
            {
                RequireLiveGraph();
                requireOriginal();
            }

            void GuardMarked()
            {
                refs.RequireLinkedGraph();
                requireOriginal();
            }
        }

        internal void RequireCancelledAttachments()
        {
            Require(CancellationEntered && cancelChildren?.Passed == true && !NativeWorkMayReadSource
                && attachmentReferences?.Instance.UnlinkPassed == true, "RELOCATION_PREVIEW_CANCEL_GROUP_UNQUALIFIED");
            attachmentReferences!.Instance.RequireCurrentGraph();
            attachmentChildren!.RequireNativeCancellationShape();
        }

        internal void ConfirmCancelledGroupRemoval()
        {
            requireScope();
            Require(CancellationEntered && cancelChildren?.Passed == true && !NativeWorkMayReadSource
                && attachmentReferences?.Instance.UnlinkPassed == true, "RELOCATION_PREVIEW_CANCEL_GROUP_UNQUALIFIED");
            var cohort = attachmentCohort!;
            var entities = source.EntityManager;
            Require(!entities.Exists(cohort.Root) && !entities.Exists(cohort.OldNode) && !entities.Exists(cohort.Node),
                "RELOCATION_PREVIEW_CANCEL_PARENT_RETAINED");
            attachmentChildren!.ConfirmGroupRemoval(requireScope);
            Require(AttachmentsRetired && attachmentChildren.ResourcesReleased, "RELOCATION_PREVIEW_CANCEL_CHILD_RETAINED");
            attachmentInput!.RequireRelocationOriginalWitness(requireScope);
            attachmentWitnessAfterRetirement = true;
            cancelGroupAbsent = true;
            ObserveProtection();
        }

        private (Entity[] Graph, Temp[] Temps) CaptureMembers()
        {
            RequireLiveGraph();
            var cohort = attachmentCohort!;
            var children = attachmentChildren!;
            var graph = new Entity[9] { cohort.Root, cohort.OldNode, cohort.Node,
                children.EntityAt(0), children.EntityAt(1), children.EntityAt(2),
                children.EntityAt(3), children.EntityAt(4), children.EntityAt(5) };
            var temps = new Temp[graph.Length];
            for (int i = 0; i < 3; i++) temps[i] = cohort.CapturedParentTemp(graph[i]);
            for (int i = 0; i < children.TargetCount; i++) temps[i + 3] = children.CapturedTempAt(i);
            return (graph, temps);
        }

        private void RequireLiveGraph()
        {
            RequireLinkedGraph();
            attachmentReferences!.Instance.RequireLiveLinkedGraph();
        }

        internal void RequireLinkedGraph() => references!.Instance.RequireLinkedGraph();

        internal void Unlink()
        {
            requireScope();
            Require(SubNetReferencesPatchProbe.Observe().Qualified, "RELOCATION_PREVIEW_REFERENCES_KERNEL_PATCHED");
            var refs = references!.Instance;
            refs.UnlinkOnce(requireScope);
            Require(refs.ChecksPassed && !NativeWorkMayReadSource, "RELOCATION_PREVIEW_UNLINK_UNCONFIRMED");
            RequireEmptyGraph();
            emptyGraphObserved = true;
            ObserveProtection();
        }

        internal void RequireEmptyGraph()
        {
            if (graphBound) references!.Instance.RequireEmptyGraph();
        }

        internal void ReleaseAfterInputs()
        {
            if (NativeWorkMayReadSource || !CanRetireInputs && !CommitWorldOwnershipConfirmed) return;
            // All inputs are absent and jobs have joined. These registrations
            // belong to the captured World by instance, independent of the
            // city generation. Only the final game-state observation needs
            // that generation; deregistration does not read old entities.
            bool geometryReleased = Retire(geometry, value => !value.CreationStarted
                || value.DestructionReturned && value.StorageReleased);
            bool refsReleased = Retire(references, value => !value.CreationStarted || value.ResourcesReleased);
            bool compositionReleased = Retire(composition, value => !value.CreationStarted || value.ResourcesReleased);
            bool compositionBarrierReleased = compositionReleased && Retire(compositionBarrier,
                value => !value.CreationStarted || value.DestructionReturned);
            bool childRefsReleased = Retire(attachmentReferences, value => !value.CreationStarted || value.ResourcesReleased);
            bool attachmentsReleased = Retire(attachments, value => !value.CreationStarted || value.ResourcesReleased);
            bool attachmentBarrierReleased = attachmentsReleased && Retire(attachmentBarrier,
                value => !value.CreationStarted || value.DestructionReturned);
            bool resolverReleased = Retire(resolver, value => !value.CreationStarted || value.ResourcesReleased);
            bool ownerBarrierReleased = resolverReleased && Retire(resolutionBarrier,
                value => !value.CreationStarted || value.DestructionReturned);
            bool initializerReleased = Retire(initializer, value => !value.CreationStarted || value.ResourcesReleased);
            bool initBarrierReleased = initializerReleased && Retire(initializationBarrier,
                value => !value.CreationStarted || value.DestructionReturned);
            registrationsReleased = geometryReleased && refsReleased && compositionBarrierReleased && childRefsReleased && attachmentBarrierReleased
                && ownerBarrierReleased && initBarrierReleased;
            if (protection != null) ObserveProtection();
        }

        private void Register<T>(NativeSystemRegistration<T> registration) where T : ComponentSystemBase
        {
            registrationsReleased = false;
            requireScope();
            registration.Add();
            appended.Add(registration.Instance);
            ObserveProtection();
        }

        private bool Retire<T>(NativeSystemRegistration<T>? registration, Func<T, bool> storageReleased) where T : ComponentSystemBase
        {
            if (registration == null) return true;
            try
            {
                registration.Destroy();
                if (!registration.PhysicalReleased || registration.DestroyAttempted && !registration.DestroyReturned
                    || !storageReleased(registration.Instance)) return false;
                appended.Remove(registration.Instance);
                return true;
            }
            catch (Exception) { return false; }
        }

        private void ObserveProtection()
        {
            requireScope();
            protection!.Observe(appended.ToArray());
            Require(protection.Passed, "RELOCATION_PREVIEW_SUCCESSOR_PROTECTION_CHANGED");
        }

        internal JObject? CommitSnapshot() => commitHandoff == null ? null : new JObject {
            ["published"] = CommitPublished, ["payment"] = preparedPayment?.Snapshot(), ["network"] = preparedNet?.Snapshot(),
            ["notification"] = preparedNotifications?.Instance.Snapshot(), ["objects"] = preparedObjects?.RelocationSnapshot(),
            ["playbackEntered"] = playbackEntered, ["playbackCompleted"] = playbackCompleted,
            ["slotsTransferred"] = slotsTransferred, ["eventHandoffEntered"] = eventHandoffEntered,
            ["eventHandoffReturned"] = eventHandoffReturned, ["handoff"] = commitHandoff.Snapshot()
        };

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["initializationCommands"] = initializationCommands,
            ["initializationReadback"] = initializationReadback, ["initializationEntries"] = initializer?.Instance.OnUpdateEntries ?? 0,
            ["ownerResolved"] = resolver?.Instance.ResolutionPassed == true,
            ["ownerEntries"] = resolver?.Instance.OnUpdateEntries ?? 0,
            ["attachmentEntries"] = attachments?.Instance.OnUpdateEntries ?? 0,
            ["attachmentRandomSeedAdvanced"] = attachments?.Instance.GlobalRandomSeedAdvanceConfirmed == true,
            ["attachmentBuffersDiscarded"] = attachments?.Instance.GenerationPassed == true
                && attachmentBarrier?.Instance.DestructionReturned == true,
            ["attachmentWitnessVerified"] = attachmentWitnessVerified,
            ["attachmentCommands"] = attachmentCommands,
            ["attachmentEntities"] = attachmentChildren?.RelocationSnapshot(),
            ["attachmentReferences"] = attachmentReferences?.Instance.Snapshot(),
            ["compositionSelection"] = composition?.Instance.RelocationSnapshot(),
            ["compositionBarrierReleased"] = compositionBarrier?.Instance.DestructionReturned == true,
            ["nodeGeometry"] = geometry?.Instance.RelocationSnapshot(),
            ["lanes"] = lanes.RelocationSnapshot(), ["road"] = road.RelocationSnapshot(),
            ["applyPreparation"] = preparedObjects == null ? null : new JObject {
                ["objects"] = preparedObjects.RelocationSnapshot(), ["sourceUnchanged"] = preparationSourceUnchanged,
                ["privateOutputsReleased"] = preparationOutputsReleased, ["sourcePlaybackCalled"] = false,
                ["paymentAdmission"] = preparedPayment?.Snapshot(), ["admittedCost"] = admittedPayment,
                ["networkAdmission"] = preparedNet?.Snapshot(), ["notifications"] = preparedNotifications?.Instance.Snapshot(),
                ["notificationsReleased"] = preparationNotificationsReleased
            },
            ["cancellation"] = cancelTool == null ? null : new JObject {
                ["tool"] = cancelTool.Snapshot(), ["children"] = cancelChildren?.Snapshot(),
                ["subnetRetiredWithGroup"] = cancelGroupAbsent
            },
            ["eligibility"] = validation == null && nodeCost == null ? null : new JObject {
                ["nodeCost"] = nodeCost?.Instance.Snapshot(), ["nodeCostReleased"] = nodeCostReleased,
                ["validation"] = validation?.RelocationSnapshot()
            },
            ["attachmentGraphWithSubnetObserved"] = attachmentGraphWithSubnetObserved,
            ["attachmentsRetired"] = AttachmentsRetired,
            ["attachmentWitnessAfterBirth"] = attachmentWitnessAfterBirth,
            ["attachmentWitnessAfterRetirement"] = attachmentWitnessAfterRetirement,
            ["attachmentDecodeFailure"] = attachmentDecodeFailure,
            ["linkedGraphObserved"] = linkedGraphObserved, ["emptyGraphObserved"] = emptyGraphObserved,
            ["referencesEntries"] = references?.Instance.OnUpdateEntries ?? 0,
            ["registrationsReleased"] = registrationsReleased, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["sourceProtectionPassed"] = protection?.Passed == true
        };

        private static void Require(bool condition, string code)
        { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
