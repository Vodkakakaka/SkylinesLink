#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Simulation;
using Game.Triggers;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A single synchronous native submit. No caller may retire or replay the
    // graph once the actual payment producer can have started.
    internal sealed class PreviewCommitExecution
    {
        private const int FollowupFrames = 180;
        private readonly PreviewCommitPayment payment = new PreviewCommitPayment();
        private readonly PreviewCommitObjects objects = new PreviewCommitObjects();
        private readonly PreviewCommitNet net = new PreviewCommitNet();
        private PreviewValidationExecution? validation;
        private NativeSystemRegistration<PreviewCommitBarrier>? barrier;
        private NativeSystemRegistration<PreviewCommitTriggerSink>? triggerSink;
        private ElectricityBuildingGraphSystem? electricitySystem;
        private ElectricityFlowSystem? flowSystem;
        private uint electricityVersionBefore;
        private PreviewCommitGraph? graph;
        private JObject? events;
        private int playbackCompleted, publishedAt, electricityObservedAt;
        private int? playbackEntered;
        private bool attempted, rehearsal, outputsConfirmed, barrierReleased = true, triggerSinkReleased = true, stagedApplied;
        private bool eligibilityConfirmedBeforePayment;
        private bool publicationEntered, publicationReturned, worldOwnershipConfirmed, eventHandoffEntered, eventHandoffReturned;
        private bool slotsReleased, protectionConfirmed, electricityReady;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => payment.NativeWorkMayReadSource || objects.NativeWorkMayReadSource
            || net.NativeWorkMayReadSource || validation?.NativeWorkMayReadSource == true;
        internal bool StructuralStateUnknown => payment.EffectEntered && (!worldOwnershipConfirmed || !slotsReleased);
        internal bool WorldOwnershipConfirmed => worldOwnershipConfirmed;
        internal bool ResourcesReleased => !NativeWorkMayReadSource && barrierReleased && triggerSinkReleased
            && payment.ResourcesReleased && objects.ResourcesReleased && net.ResourcesReleased
            && validation?.ResourcesReleased != false
            && (!payment.EffectEntered || slotsReleased);
        internal bool HandoffPassed => outputsConfirmed && eligibilityConfirmedBeforePayment && payment.Passed && playbackCompleted == 2
            && stagedApplied && worldOwnershipConfirmed && eventHandoffReturned && slotsReleased && protectionConfirmed && ResourcesReleased;
        internal bool Passed => HandoffPassed && electricityReady && failureCode == null;
        internal bool RehearsalPassed => rehearsal && outputsConfirmed && protectionConfirmed && ResourcesReleased && failureCode == null;

        internal (Entity Root, Entity Node) RequirePublishedBuildingAndNode()
        {
            Require(Passed && ResourcesReleased, "PREVIEW_COMMIT_PUBLISHED_ROOT_UNAVAILABLE");
            return graph!.BuildingAndNode;
        }

        internal void RequirePublishedElectricity(World source, Action requireScope)
        {
            requireScope();
            Require(Passed && ReferenceEquals(source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>(), electricitySystem)
                && ReferenceEquals(source.GetExistingSystemManaged<ElectricityFlowSystem>(), flowSystem),
                "RELOCATION_SOURCE_ELECTRICITY_UNAVAILABLE");
            electricitySystem!.CheckedStateRef.CompleteDependency();
            requireScope();
            graph!.RequireElectricityUnchanged(source.EntityManager, flowSystem!.sourceNode);
        }

        internal void RunOnce(World source, Entity[] members, List<ComponentSystemBase> appended, Action requireScope,
            Action guardLiveGraph, Action observeProtection, Action? confirmWorldOwnership, PreviewValidationExecution? eligibility = null)
        {
            Require(!attempted && MainThreadDispatcher.isMainThread && PreviewCommitGraph.IsNewMemberCount(members.Length)
                && (confirmWorldOwnership == null) == (eligibility == null),
                "PREVIEW_COMMIT_UNAVAILABLE");
            attempted = true;
            rehearsal = confirmWorldOwnership == null;
            validation = eligibility;
            try
            {
                guardLiveGraph();
                Require(source.GetExistingSystemManaged<PreviewCommitBarrier>() == null
                    && source.GetExistingSystemManaged<PreviewCommitTriggerSink>() == null, "PREVIEW_COMMIT_OUTPUT_ALREADY_PRESENT");
                var worldTrigger = source.GetExistingSystemManaged<TriggerSystem>();
                Require(worldTrigger != null && ReferenceEquals(worldTrigger.World, source) && worldTrigger.Enabled,
                    "PREVIEW_COMMIT_WORLD_TRIGGER_UNAVAILABLE");
                electricitySystem = source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>();
                flowSystem = source.GetExistingSystemManaged<ElectricityFlowSystem>();
                Require(electricitySystem != null && ReferenceEquals(electricitySystem.World, source)
                    && flowSystem != null && ReferenceEquals(flowSystem.World, source),
                    "PREVIEW_COMMIT_WORLD_ELECTRICITY_UNAVAILABLE");
                electricityVersionBefore = electricitySystem!.CheckedStateRef.LastSystemVersion;
                graph = new PreviewCommitGraph(source.EntityManager, members);

                barrier = new NativeSystemRegistration<PreviewCommitBarrier>(source, new PreviewCommitBarrier());
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                triggerSink = new NativeSystemRegistration<PreviewCommitTriggerSink>(source, new PreviewCommitTriggerSink());
                triggerSinkReleased = false;
                Register(triggerSink, appended, requireScope);
                observeProtection();
                guardLiveGraph();
                Require(barrier.Instance.PendingCount == 0 && triggerSink.Instance.QueueCount == 0,
                    "PREVIEW_COMMIT_OUTPUT_NOT_EMPTY");
                Require(PreviewCommitObjectPatchProbe.Observe().Qualified, "PREVIEW_COMMIT_EFFECT_BOUNDARY_UNQUALIFIED");

                var costsBeforePreparation = new PreviewCostObservation(source, members);
                objects.RunOnce(source, members, barrier.Instance, triggerSink.Instance, appended, requireScope, guardLiveGraph, observeProtection);
                Require(objects.Passed && !NativeWorkMayReadSource, "PREVIEW_COMMIT_OBJECTS_UNCONFIRMED");
                net.RunOnce(source, members, barrier.Instance, triggerSink.Instance, appended, requireScope, guardLiveGraph, observeProtection);
                Require(net.Passed && !NativeWorkMayReadSource && barrier.Instance.PendingCount == PreviewCommitBarrier.PaymentBufferIndex,
                    "PREVIEW_COMMIT_NET_UNCONFIRMED");
                var actions = triggerSink.Instance.CaptureJoined(objects.CompletedDependency, net.CompletedDependency, members.Length);
                events = graph.ObserveEvents(actions);
                RequirePreparedInputs();
                costsBeforePreparation.RequireFundsUnchanged(source);
                outputsConfirmed = true;

                if (!rehearsal)
                {
                    payment.RunOnce(source, members, barrier.Instance, appended, requireScope, RequirePreparedInputs,
                        observeProtection, () =>
                        {
                            validation!.ObserveOnce(source, members, appended, requireScope, guardLiveGraph, observeProtection);
                            Require(PreviewCommitObjectPatchProbe.Observe().Qualified, "PREVIEW_COMMIT_EFFECT_BOUNDARY_CHANGED");
                            barrier.Instance.Borrow(PreviewCommitBarrier.ObjectBufferIndex);
                            barrier.Instance.Borrow(PreviewCommitBarrier.NetBufferIndex);
                            eligibilityConfirmedBeforePayment = true;
                        });
                    Require(payment.OutputConfirmed && !NativeWorkMayReadSource, "PREVIEW_COMMIT_PAYMENT_UNCONFIRMED");
                    Publish(source, members, requireScope, worldTrigger!, actions, confirmWorldOwnership!);
                }

                void RequirePreparedInputs()
                {
                    guardLiveGraph();
                    Require(ReferenceEquals(source.GetExistingSystemManaged<TriggerSystem>(), worldTrigger) && worldTrigger!.Enabled,
                        "PREVIEW_COMMIT_WORLD_TRIGGER_CHANGED");
                    graph.RequirePrepared(source.EntityManager);
                    objects.RequirePreparedInputs(source, members);
                }
            }
            catch (Exception error)
            {
                failureCode = error switch {
                    Rejection rejected => rejected.Code,
                    PreviewCommitPayment.Rejection paymentError => paymentError.Code,
                    PreviewCommitObjects.Rejection objectsError => objectsError.Code,
                    PreviewCommitNet.Rejection netError => netError.Code,
                    _ => (string?)objects.Snapshot()["failureCode"] ?? (string?)net.Snapshot()["failureCode"] ?? "PREVIEW_COMMIT_EXECUTION_FAILED"
                };
                throw new Rejection(failureCode, error);
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    validation?.Release(appended);
                    payment.ReleaseProducer(appended);
                    if (triggerSink != null)
                        triggerSinkReleased = Retire(triggerSink, appended, () => triggerSink.Instance.StorageReleased);
                    if (barrier != null)
                        barrierReleased = Retire(barrier, appended, () => barrier.Instance.StorageReleased);
                }
            }
            try
            {
                requireScope();
                observeProtection();
                protectionConfirmed = true;
                Require(rehearsal ? RehearsalPassed : HandoffPassed, "PREVIEW_COMMIT_HANDOFF_UNCONFIRMED");
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_COMMIT_PROTECTION_UNCONFIRMED";
                throw new Rejection(failureCode, error);
            }
        }

        private void Publish(World source, Entity[] members, Action requireScope, TriggerSystem worldTrigger,
            TriggerAction[] actions, Action confirmWorldOwnership)
        {
            Require(PreviewCommitObjectPatchProbe.Observe().Qualified, "PREVIEW_COMMIT_EFFECT_BOUNDARY_CHANGED");
            var entities = source.EntityManager;
            // The empty fee buffer is retained by the private barrier for disposal.
            // Only these two prepared buffers have structural commands to publish.
            for (int index = PreviewCommitBarrier.ObjectBufferIndex; index <= PreviewCommitBarrier.NetBufferIndex; index++)
            {
                requireScope();
                if (index == PreviewCommitBarrier.NetBufferIndex) graph!.RequireNodeBeforePlayback(entities);
                var buffer = barrier!.Instance.Borrow(index);
                playbackEntered = index;
                buffer.Playback(entities);
                playbackCompleted++;
            }
            requireScope();
            graph!.RequireApplied(entities, published: false, initialMarkersRequired: true);
            stagedApplied = true;
            using var group = new NativeArray<Entity>(members, Allocator.Temp);
            var isolation = new ComponentTypeSet(ComponentType.ReadWrite<Disabled>(), ComponentType.ReadWrite<ObjectGenerationScope>());
            publicationEntered = true;
            entities.RemoveComponent(group, in isolation);
            publicationReturned = true;
            requireScope();
            graph.RequireApplied(entities, published: true, initialMarkersRequired: true);
            publishedAt = UnityEngine.Time.frameCount;
            worldOwnershipConfirmed = true;
            confirmWorldOwnership();
            slotsReleased = true;

            Require(ReferenceEquals(source.GetExistingSystemManaged<TriggerSystem>(), worldTrigger) && worldTrigger.Enabled,
                "PREVIEW_COMMIT_WORLD_TRIGGER_CHANGED");
            eventHandoffEntered = true;
            var eventQueue = worldTrigger.CreateActionBuffer();
            foreach (var action in actions) eventQueue.Enqueue(action);
            eventHandoffReturned = true;
            requireScope();
        }

        internal void ObserveDefaultWorld(World source, Action requireScope)
        {
            if (Passed) return;
            try
            {
                requireScope();
                int frame = UnityEngine.Time.frameCount;
                Require(HandoffPassed && frame >= publishedAt && frame - publishedAt <= FollowupFrames,
                    graph?.DefaultRelationsStable == true ? "PREVIEW_COMMIT_ELECTRICITY_NOT_READY" : "PREVIEW_COMMIT_WORLD_GRAPH_NOT_READY");
                if (frame == publishedAt) return;
                Require(ReferenceEquals(source.GetExistingSystemManaged<ElectricityBuildingGraphSystem>(), electricitySystem)
                    && ReferenceEquals(source.GetExistingSystemManaged<ElectricityFlowSystem>(), flowSystem),
                    "PREVIEW_COMMIT_WORLD_ELECTRICITY_CHANGED");
                if (electricitySystem!.CheckedStateRef.LastSystemVersion == electricityVersionBefore) return;
                electricitySystem.CheckedStateRef.CompleteDependency();
                requireScope();
                if (!graph!.ObserveDefault(source.EntityManager, flowSystem!.sourceNode, frame)) return;
                requireScope();
                electricityObservedAt = frame;
                electricityReady = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code : "PREVIEW_COMMIT_WORLD_READBACK_FAILED";
                throw new Rejection(failureCode, error);
            }
        }

        private static void Register<T>(NativeSystemRegistration<T> owner, List<ComponentSystemBase> appended,
            Action requireScope) where T : ComponentSystemBase
        {
            owner.Add();
            appended.Add(owner.Instance);
            requireScope();
        }

        private static bool Retire<T>(NativeSystemRegistration<T> owner, List<ComponentSystemBase> appended, Func<bool> storageReleased)
            where T : ComponentSystemBase
        {
            try
            {
                owner.Destroy();
                if (!owner.PhysicalReleased || !storageReleased()) return false;
                appended.Remove(owner.Instance);
                return true;
            }
            catch (Exception) { return false; }
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["rehearsalPassed"] = RehearsalPassed, ["outputsConfirmed"] = outputsConfirmed,
            ["eligibilityConfirmedBeforePayment"] = eligibilityConfirmedBeforePayment,
            ["handoffPassed"] = HandoffPassed, ["payment"] = payment.Snapshot(),
            ["objects"] = objects.Snapshot(), ["net"] = net.Snapshot(), ["graph"] = graph?.Snapshot(), ["events"] = events?.DeepClone(),
            ["playbackEntered"] = playbackEntered, ["playbackCompleted"] = playbackCompleted, ["stagedApplied"] = stagedApplied,
            ["publicationEntered"] = publicationEntered, ["publicationReturned"] = publicationReturned,
            ["worldOwnershipConfirmed"] = worldOwnershipConfirmed, ["slotsReleased"] = slotsReleased,
            ["eventHandoffEntered"] = eventHandoffEntered, ["eventHandoffReturned"] = eventHandoffReturned,
            ["electricityReady"] = electricityReady, ["publishedAt"] = publishedAt, ["electricityObservedAt"] = electricityObservedAt,
            ["resourcesReleased"] = ResourcesReleased, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["structuralStateUnknown"] = StructuralStateUnknown, ["failureCode"] = failureCode
        };

        internal JObject RehearsalSnapshot() => new JObject {
            ["passed"] = RehearsalPassed, ["objects"] = objects.Snapshot(), ["net"] = net.Snapshot(),
            ["eventsObserved"] = ((JArray?)events?["objects"])?.Count, ["paid"] = payment.EffectEntered,
            ["playbackCompleted"] = playbackCompleted, ["resourcesReleased"] = ResourcesReleased,
            ["failureCode"] = failureCode
        };

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code, Exception? inner = null) : base(code, inner)
            { Code = code; }
        }
    }
}
#endif
