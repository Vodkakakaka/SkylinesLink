#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Owns the producer, pending commands, and skip queue for one attempt.
    // The player-facing lane-reference consumer is outside this boundary.
    internal sealed class PreviewLaneExecution
    {
        private NativeSystemRegistration<PreviewLaneBarrier>? barrier;
        private NativeSystemRegistration<PreviewLaneSkipSink>? skipSink;
        private NativeSystemRegistration<PreviewLaneProducer>? producer;
        private JObject? commands;
        private int? skipCount;
        private bool attempted, barrierReleased = true, sinkReleased = true, producerReleased = true;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => producer?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => barrierReleased && sinkReleased && producerReleased && !NativeWorkMayReadSource;
        internal bool Passed => commands != null && skipCount == 0 && ResourcesReleased && failureCode == null;
        internal bool NoLaneEntitiesRequired => Passed && (int?)commands?["lanesCreated"] == 0;

        internal void RunOnce(GeneratedSubObjectsPayload cohort, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection)
        {
            Run(cohort.Source, cohort.Actor, appended, requireScope, guardLiveGraph, observeProtection,
                instance => instance.GenerateOnce(cohort, requireScope),
                (buffer, instance) => PreviewLaneCommandProbe.Capture(buffer, cohort.Source, cohort.Node,
                    instance.CopyAdmittedLanePrefabs()));
        }

        internal void RunRelocationOnce(World source, GeneratedSubObjectsPayload cohort, CompositionSelectionReceipt oldNode,
            CompositionSelectionReceipt newNode, (Game.Net.NodeGeometry Old, Game.Net.NodeGeometry New) geometry,
            List<ComponentSystemBase> appended, Action requireScope, Action guardLiveGraph, Action observeProtection)
        {
            Run(source, cohort.Actor, appended, requireScope, guardLiveGraph, observeProtection,
                instance => instance.GenerateRelocationOnce(cohort, oldNode, newNode, geometry, guardLiveGraph),
                (buffer, _) =>
                {
                    int count = PreviewLaneCommandProbe.CountRelocationCommands(buffer);
                    return new JObject { ["commandsObserved"] = count };
                }, requireEmptyCommands: true);
        }

        private void Run(World source, int actor, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection,
            Action<PreviewLaneProducer> generate, Func<EntityCommandBuffer, PreviewLaneProducer, JObject> capture,
            bool requireEmptyCommands = false)
        {
            Require(!attempted, "PREVIEW_LANE_EXECUTION_REPEATED");
            attempted = true;
            try
            {
                requireScope();
                barrier = new NativeSystemRegistration<PreviewLaneBarrier>(source, new PreviewLaneBarrier());
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                skipSink = new NativeSystemRegistration<PreviewLaneSkipSink>(source, new PreviewLaneSkipSink());
                sinkReleased = false;
                Register(skipSink, appended, requireScope);
                var instance = new PreviewLaneProducer();
                instance.Configure(actor, barrier.Instance, skipSink.Instance);
                producer = new NativeSystemRegistration<PreviewLaneProducer>(source, instance);
                producerReleased = false;
                Register(producer, appended, requireScope);
                observeProtection();
                guardLiveGraph();
                generate(instance);
                Require(instance.LanePassed && !NativeWorkMayReadSource, "PREVIEW_LANE_NATIVE_RESULT_UNCONFIRMED");
                guardLiveGraph();
                skipCount = skipSink.Instance.ReadJoinedCount();
                if (skipCount == 0 || requireEmptyCommands)
                    commands = capture(barrier.Instance.BorrowSingle(), instance);
                Require(skipCount == 0, "PREVIEW_LANE_UTILITY_SKIP_QUEUE_NOT_EMPTY");
                Require(!requireEmptyCommands || (int?)commands?["commandsObserved"] == 0,
                    "PREVIEW_LANE_RELOCATION_OUTPUT_UNSUPPORTED");
                requireScope();
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejection ? rejection.Code : "PREVIEW_LANE_EXECUTION_FAILED";
                throw;
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    if (producer != null) producerReleased = Release(producer, appended, () => producer.Instance.DestructionReturned);
                    if (skipSink != null) sinkReleased = Release(skipSink, appended, () => skipSink.Instance.DestructionReturned);
                    if (barrier != null) barrierReleased = Release(barrier, appended, () => barrier.Instance.DestructionReturned);
                }
            }
            Require(Passed, "PREVIEW_LANE_RELEASE_UNCONFIRMED");
            requireScope();
            observeProtection();
        }

        private static void Register<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            Action requireScope) where T : ComponentSystemBase
        {
            registration.Add();
            appended.Add(registration.Instance);
            requireScope();
        }

        private bool Release<T>(NativeSystemRegistration<T> registration, List<ComponentSystemBase> appended,
            Func<bool> destructionReturned) where T : ComponentSystemBase
        {
            try
            {
                registration.Destroy();
                bool released = registration.PhysicalReleased
                    && (!registration.DestroyAttempted || registration.DestroyReturned && destructionReturned());
                if (released) appended.Remove(registration.Instance);
                else failureCode ??= "PREVIEW_LANE_RELEASE_UNCONFIRMED";
                return released;
            }
            catch (Exception)
            {
                failureCode ??= "PREVIEW_LANE_DESTROY_FAILED";
                return false;
            }
        }

        internal JObject? InputSnapshot() => producer?.Instance.InputSnapshot();

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["producer"] = (JToken?)producer?.Instance.Snapshot() ?? JValue.CreateNull(),
            ["commands"] = (JToken?)commands?.DeepClone() ?? JValue.CreateNull(),
            ["skipCount"] = skipCount.HasValue ? new JValue(skipCount.Value) : JValue.CreateNull(),
            ["barrierReleased"] = barrierReleased, ["sinkReleased"] = sinkReleased,
            ["producerReleased"] = producerReleased, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["sourcePlaybackCalled"] = false, ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot() => new JObject {
            ["passed"] = Passed, ["native"] = producer?.Instance.RelocationSnapshot(),
            ["commandsObserved"] = commands?["commandsObserved"]?.DeepClone(),
            ["skipCount"] = skipCount, ["sourcePlaybackCalled"] = false,
            ["resourcesReleased"] = ResourcesReleased, ["failureCode"] = failureCode
        };

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
