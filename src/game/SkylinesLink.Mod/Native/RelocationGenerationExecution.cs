#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Runs the two stock generators on the same isolated definition batch.
    // Their ECBs are observed and discarded before returning to the game loop.
    internal sealed class RelocationGenerationExecution
    {
        private const int Actor = 1;
        private readonly List<ComponentSystemBase> appended = new List<ComponentSystemBase>(4);
        private readonly RelocationGenerationAdmission admission = new RelocationGenerationAdmission();
        private NativeSystemRegistration<ObjectGenerationBarrier>? objectBarrier;
        private NativeSystemRegistration<ObjectGenerationProducer>? objects;
        private NativeSystemRegistration<NodeGenerationBarrier>? nodeBarrier;
        private NativeSystemRegistration<NodeGenerationProducer>? nodes;
        private RelocationDefinitionEntities? definitions;
        private JObject? output;
        private RelocationPreviewPayload? preview;
        private bool attempted, registrationsReleased = true, sourceUnchanged;
        private string stage = "unstarted";
        private string? exceptionType;
        internal string? FailureCode { get; private set; }
        internal bool Finished { get; private set; }
        internal bool NativeWorkMayReadSource => objects?.Instance.NativeWorkMayReadSource == true || nodes?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => !NativeWorkMayReadSource && registrationsReleased && definitions?.ResourcesReleased != false;
        internal bool Passed => Finished && FailureCode == null && output != null && sourceUnchanged && ResourcesReleased;

        internal void RunOnce(RelocationDefinitionPayload payload, Action requireScope)
        {
            Require(!attempted && MainThreadDispatcher.isMainThread, "RELOCATION_GENERATION_EXECUTION_REJECTED");
            attempted = true;
            var source = payload.Source;
            NativeProbeSourceProtection? protection = null;
            try
            {
                stage = "register";
                requireScope();
                protection = new NativeProbeSourceProtection(source);
                Require(!protection.Contains<ObjectGenerationBarrier>() && !protection.Contains<ObjectGenerationProducer>()
                    && !protection.Contains<NodeGenerationBarrier>() && !protection.Contains<NodeGenerationProducer>(),
                    "RELOCATION_GENERATION_OWNER_ALREADY_PRESENT");
                RequireKernelsUnpatched();

                objectBarrier = new NativeSystemRegistration<ObjectGenerationBarrier>(source, new ObjectGenerationBarrier());
                Register(objectBarrier);
                var objectProducer = new ObjectGenerationProducer();
                objectProducer.Configure(Actor, objectBarrier.Instance);
                objects = new NativeSystemRegistration<ObjectGenerationProducer>(source, objectProducer);
                Register(objects);
                objectProducer.ProbeEmptyUpdate();
                ObserveProtection();

                nodeBarrier = new NativeSystemRegistration<NodeGenerationBarrier>(source, new NodeGenerationBarrier());
                Register(nodeBarrier);
                var nodeProducer = new NodeGenerationProducer();
                nodeProducer.Configure(Actor, nodeBarrier.Instance, objectProducer);
                nodes = new NativeSystemRegistration<NodeGenerationProducer>(source, nodeProducer);
                Register(nodes);
                nodeProducer.ProbeEmptyUpdate();
                ObserveProtection();

                stage = "definitions";
                definitions = new RelocationDefinitionEntities(payload, Actor, requireScope);
                definitions.Create();
                ObserveProtection();

                stage = "admission";
                RequireKernelsUnpatched();
                admission.Run(payload, requireScope);
                stage = "objects";
                objectProducer.GenerateRelocationOnce(payload, definitions, admission);
                payload.Original.RequireUnchanged(source, payload.Original.Prefab, requireScope);
                stage = "nodes";
                nodeProducer.GenerateRelocationOnce(payload, definitions, admission);
                payload.Original.RequireUnchanged(source, payload.Original.Prefab, requireScope);
                ObserveProtection();
                stage = "commands";
                admission.RequireCurrentEconomy();
                output = RelocationGenerationCommands.Capture(payload, objectProducer.BorrowRelocationCommands(payload),
                    nodeProducer.BorrowRelocationCommands(payload), admission, out var capturedPreview);
                preview = capturedPreview;
            }
            catch (Exception error)
            {
                FailureCode = IsDiagnosticCode(error.Message)
                    ? error.Message : "RELOCATION_GENERATION_" + stage.ToUpperInvariant() + "_FAILED";
                string name = error.GetType().Name;
                exceptionType = name.Length <= 64 ? name : name.Substring(0, 64);
            }
            finally
            {
                if (!NativeWorkMayReadSource)
                {
                    bool nodesReleased = Retire(nodes, value => !value.CreationStarted || value.ResourcesReleased);
                    bool nodeBarrierReleased = nodesReleased && Retire(nodeBarrier,
                        value => !value.CreationStarted || value.DestructionReturned);
                    bool objectsReleased = nodesReleased && Retire(objects,
                        value => value.StorageReleased && (!value.CreationStarted || value.DestructionReturned));
                    bool objectBarrierReleased = objectsReleased && Retire(objectBarrier,
                        value => !value.CreationStarted || value.DestructionReturned);
                    registrationsReleased = nodesReleased && objectsReleased && nodeBarrierReleased && objectBarrierReleased;
                    if (nodesReleased && objectsReleased)
                    {
                        try { definitions?.Release(); }
                        catch (Exception) { FailureCode ??= "RELOCATION_GENERATION_DEFINITION_RELEASE_FAILED"; }
                    }
                    try
                    {
                        if (protection != null) ObserveProtection();
                        payload.Original.RequireUnchanged(source, payload.Original.Prefab, requireScope);
                        sourceUnchanged = true;
                    }
                    catch (Exception) { FailureCode ??= "RELOCATION_GENERATION_FINAL_READBACK_FAILED"; }
                }
                if (!ResourcesReleased) FailureCode ??= "RELOCATION_GENERATION_RELEASE_UNCONFIRMED";
                Finished = true;
            }

            void Register<T>(NativeSystemRegistration<T> owner) where T : ComponentSystemBase
            {
                registrationsReleased = false;
                requireScope();
                owner.Add();
                appended.Add(owner.Instance);
                ObserveProtection();
            }

            void ObserveProtection()
            {
                requireScope();
                protection!.Observe(appended.ToArray());
                Require(protection.Passed, "RELOCATION_GENERATION_PROTECTION_CHANGED");
            }
        }

        internal RelocationPreviewEntities TransferToPreview(RelocationDefinitionPayload payload, Action requireScope)
        {
            Require(Passed && preview != null && ReferenceEquals(preview.Definitions, payload),
                "RELOCATION_PREVIEW_TRANSFER_UNAVAILABLE");
            requireScope();
            admission.RequireCurrentEconomy();
            var owner = new RelocationPreviewEntities(preview!, Actor, requireScope);
            preview = null;
            return owner;
        }

        private bool Retire<T>(NativeSystemRegistration<T>? owner, Func<T, bool> storageReleased)
            where T : ComponentSystemBase
        {
            if (owner == null) return true;
            try
            {
                owner.Destroy();
                if (!owner.PhysicalReleased || owner.DestroyAttempted && !owner.DestroyReturned || !storageReleased(owner.Instance))
                    return false;
                appended.Remove(owner.Instance);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void RequireKernelsUnpatched() => Require(ObjectGenerationPatchProbe.Observe().Qualified
            && NodeGenerationPatchProbe.Observe().Qualified && RelocationGenerationAdmission.SpatialMethodsQualified(),
            "RELOCATION_GENERATION_KERNELS_UNQUALIFIED");

        private static bool IsDiagnosticCode(string value)
        {
            if (value.Length > 96 || !(value.StartsWith("RELOCATION_", StringComparison.Ordinal)
                || value.StartsWith("OBJECT_RELOCATION_", StringComparison.Ordinal) || value.StartsWith("NODE_RELOCATION_", StringComparison.Ordinal)
                || value.StartsWith("OBJECT_GENERATION_", StringComparison.Ordinal) || value.StartsWith("NODE_GENERATION_", StringComparison.Ordinal))) return false;
            foreach (char character in value)
                if (character != '_' && (character < 'A' || character > 'Z') && (character < '0' || character > '9')) return false;
            return true;
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["resourcesReleased"] = ResourcesReleased, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["sourceUnchanged"] = sourceUnchanged, ["definitionsAbsent"] = definitions?.AllAbsent,
            ["playbackCalled"] = false, ["stage"] = stage, ["failureCode"] = FailureCode,
            ["exceptionType"] = exceptionType, ["objectNativeEntries"] = objects?.Instance.OnUpdateEntries ?? 0,
            ["nodeNativeEntries"] = nodes?.Instance.OnUpdateEntries ?? 0,
            ["globalRandomSeedAdvanceMayHaveOccurred"] = objects?.Instance.GlobalRandomSeedAdvanceMayHaveOccurred == true,
            ["admission"] = admission.Snapshot(), ["observed"] = output?.DeepClone()
        };

        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
