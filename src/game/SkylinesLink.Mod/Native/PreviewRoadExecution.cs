#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class PreviewRoadBarrier : ModificationBarrier4B
    {
        private bool creationStarted, destructionStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            if (!MainThreadDispatcher.isMainThread)
                throw new InvalidOperationException("PREVIEW_ROAD_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_ROAD_BARRIER_CANNOT_PLAYBACK");

        internal int[] ReadJoinedCommandCounts()
        {
            if (!CreationReturned || destructionStarted || PendingCount != 2)
                throw new InvalidOperationException("PREVIEW_ROAD_BUFFER_COUNT_CHANGED");
            var abi = new ObjectDefinitionCommandProbe.Abi();
            var counts = new int[2];
            for (int i = 0; i < counts.Length; i++)
            {
                var buffer = PendingBuffers[i];
                if (!buffer.IsCreated) throw new InvalidOperationException("PREVIEW_ROAD_BUFFER_UNAVAILABLE");
                object view = abi.Constructor.Invoke(new object[] { buffer });
                counts[i] = ((Array)abi.Commands.GetValue(view)!).Length;
            }
            return counts;
        }

        protected override void OnDestroy()
        {
            destructionStarted = true;
            if (creationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("PREVIEW_ROAD_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }

    internal sealed class PreviewRoadExecution
    {
        private NativeSystemRegistration<PreviewRoadBarrier>? barrier;
        private NativeSystemRegistration<PreviewRoadProducer>? producer;
        private PreviewRoadSinks? sinks;
        private readonly PreviewRoadSearchBounds bounds = new PreviewRoadSearchBounds();
        private int[]? commandCounts;
        private bool attempted, barrierReleased = true, producerReleased = true;
        private bool graphUnchanged, fundsUnchanged, protectionConfirmed;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => producer?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => barrierReleased && producerReleased && !NativeWorkMayReadSource
            && (sinks?.ResourcesReleased ?? true);
        internal bool Passed => protectionConfirmed && failureCode == null;

        internal void RunOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection) =>
            Run(source, graph[0], graph, null, appended, requireScope, guardLiveGraph, observeProtection);

        internal void RunRelocationOnce(World source, GeneratedSubObjectsPayload cohort, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection) =>
            Run(source, cohort.Root, null, cohort, appended, requireScope, guardLiveGraph, observeProtection);

        private void Run(World source, Entity root, Entity[]? newGraph, GeneratedSubObjectsPayload? relocation,
            List<ComponentSystemBase> appended, Action requireScope, Action guardLiveGraph, Action observeProtection)
        {
            Require(!attempted, "PREVIEW_ROAD_EXECUTION_REPEATED");
            attempted = true;
            try
            {
                guardLiveGraph();
                barrier = new NativeSystemRegistration<PreviewRoadBarrier>(source, new PreviewRoadBarrier());
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                sinks = new PreviewRoadSinks();
                sinks.Prepare();
                var instance = new PreviewRoadProducer();
                instance.Configure(relocation?.Actor ?? 1, barrier.Instance, sinks);
                producer = new NativeSystemRegistration<PreviewRoadProducer>(source, instance);
                producerReleased = false;
                Register(producer, appended, requireScope);
                observeProtection();
                guardLiveGraph();

                JObject? before = null;
                PreviewCostObservation? costs = null;
                if (relocation == null)
                {
                    before = PreviewRoadInputs.Read(source, newGraph!);
                    costs = new PreviewCostObservation(source, newGraph!);
                    instance.SearchOnce(newGraph!, requireScope, () => bounds.RequireEmpty(source, root, requireScope));
                }
                else
                {
                    // The relocation guard checks all captured Temp values and the authoritative city balance.
                    instance.SearchRelocationOnce(relocation, guardLiveGraph, () => bounds.RequireEmpty(source, root, guardLiveGraph));
                }
                Require(instance.RoadPassed && !NativeWorkMayReadSource, "PREVIEW_ROAD_NATIVE_RESULT_UNCONFIRMED");
                guardLiveGraph();
                commandCounts = barrier.Instance.ReadJoinedCommandCounts();
                Require(commandCounts[0] == 0 && commandCounts[1] == 0, "PREVIEW_ROAD_UNEXPECTED_COMMANDS");
                if (relocation == null)
                {
                    graphUnchanged = JToken.DeepEquals(before, PreviewRoadInputs.Read(source, newGraph!));
                    Require(graphUnchanged, "PREVIEW_ROAD_GRAPH_CHANGED");
                    fundsUnchanged = (bool)costs!.Complete(source, newGraph!)["fundsUnchanged"]!;
                }
                else
                {
                    // This readback includes the exact attached graph and original city funds.
                    guardLiveGraph();
                    graphUnchanged = fundsUnchanged = true;
                }
                guardLiveGraph();
            }
            catch (Exception error)
            {
                failureCode = error switch {
                    Rejection rejected => rejected.Code,
                    PreviewRoadProducer.Rejection rejected => rejected.Code,
                    EmptyNetBoundsVisitor.Rejection spatial => spatial.Code,
                    _ => "PREVIEW_ROAD_EXECUTION_FAILED"
                };
                throw new Rejection(failureCode, error);
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    if (producer != null) producerReleased = Release(producer, appended, () => producer.Instance.DestructionReturned);
                    if (barrier != null) barrierReleased = Release(barrier, appended, () => barrier.Instance.DestructionReturned);
                    try { sinks?.Release(); }
                    catch (Exception) { failureCode ??= "PREVIEW_ROAD_SINK_RELEASE_FAILED"; }
                }
            }
            if (!ResourcesReleased) failureCode ??= "PREVIEW_ROAD_RELEASE_UNCONFIRMED";
            if (!fundsUnchanged) failureCode ??= "PREVIEW_ROAD_FUNDS_CHANGED";
            if (failureCode != null) throw new Rejection(failureCode);
            try
            {
                requireScope();
                observeProtection();
                protectionConfirmed = true;
            }
            catch (Exception error)
            {
                failureCode = "PREVIEW_ROAD_PROTECTION_UNCONFIRMED";
                throw new Rejection(failureCode, error);
            }
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
                else failureCode ??= "PREVIEW_ROAD_RELEASE_UNCONFIRMED";
                return released;
            }
            catch (Exception)
            {
                failureCode ??= "PREVIEW_ROAD_DESTROY_FAILED";
                return false;
            }
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["producer"] = (JToken?)producer?.Instance.Snapshot() ?? JValue.CreateNull(),
            ["search"] = bounds.Snapshot(), ["commandCounts"] = commandCounts == null ? JValue.CreateNull() : new JArray(commandCounts),
            ["graphUnchanged"] = graphUnchanged, ["fundsUnchanged"] = fundsUnchanged, ["barrierReleased"] = barrierReleased,
            ["producerReleased"] = producerReleased, ["sinksReleased"] = sinks?.ResourcesReleased ?? true,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["sourcePlaybackCalled"] = false, ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot() => new JObject {
            ["passed"] = Passed, ["native"] = producer?.Instance.RelocationSnapshot(), ["search"] = bounds.RelocationSnapshot(),
            ["commands"] = commandCounts == null ? null : new JArray(commandCounts),
            ["graphUnchanged"] = graphUnchanged, ["fundsUnchanged"] = fundsUnchanged,
            ["resourcesReleased"] = ResourcesReleased, ["sourcePlaybackCalled"] = false, ["failureCode"] = failureCode
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
