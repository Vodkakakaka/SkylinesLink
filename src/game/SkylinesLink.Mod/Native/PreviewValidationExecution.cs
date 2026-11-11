#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    [DisableAutoCreation]
    public sealed partial class PreviewValidationBarrier : ModificationEndBarrier
    {
        private bool creationStarted;
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;

        protected override void OnCreate()
        {
            creationStarted = true;
            base.OnCreate();
            CreationReturned = true;
        }

        protected override void OnUpdate() => throw new InvalidOperationException("PREVIEW_VALIDATION_BARRIER_CANNOT_PLAYBACK");

        internal (Array commands, ObjectDefinitionCommandProbe.Abi abi) ReadJoinedCommands()
        {
            if (!CreationReturned || PendingCount != 1 || !PendingBuffers[0].IsCreated)
                throw new PreviewValidationExecution.Rejection("PREVIEW_VALIDATION_COMMAND_BUFFER_UNCONFIRMED");
            var abi = new ObjectDefinitionCommandProbe.Abi();
            object view = abi.Constructor.Invoke(new object[] { PendingBuffers[0] });
            return ((Array)abi.Commands.GetValue(view)!, abi);
        }

        protected override void OnDestroy()
        {
            if (creationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("PREVIEW_VALIDATION_BARRIER_CREATION_UNRESOLVED");
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
    }

    // One inspection of the normal-game kernel. Components and IconCommandSystem
    // are unregistered holders. The native result buffers are never played.
    internal sealed class PreviewValidationExecution
    {
        private const int DiagnosticLimit = 16;
        private readonly ValidationSystem.Components errors = new ValidationSystem.Components();
        private readonly PreviewIconOutput icons = new PreviewIconOutput();
        private NativeSystemRegistration<PreviewValidationBarrier>? barrier;
        private NativeSystemRegistration<PreviewValidationProducer>? producer;
        private bool attempted, entered, releaseStarted, mapWriterConfirmed, iconWriterConfirmed, mapReleaseAttempted;
        private bool barrierReleased = true, producerReleased = true, mapReleased = true;
        private bool fundsUnchanged, outcomesObserved, eligible, protectionConfirmed;
        private JArray? errorRows, cancellationRows, iconRows;
        private JObject? relocationCosts;
        private int? nativeErrorCount, nativeCancelCount, nativeIconCount;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => producer?.Instance.NativeWorkMayReadSource == true
            || entered && (!mapWriterConfirmed || !iconWriterConfirmed);
        internal bool ResourcesReleased => !NativeWorkMayReadSource && barrierReleased && producerReleased
            && mapReleased && icons.ResourcesReleased;
        internal bool Passed => outcomesObserved && eligible && protectionConfirmed && ResourcesReleased && failureCode == null;

        internal void RunOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection, RelocationInput? relocation = null)
        {
            try { ObserveOnce(source, graph, appended, requireScope, guardLiveGraph, observeProtection, relocation); }
            finally { Release(appended); }

            RequireFinal(ResourcesReleased, "PREVIEW_VALIDATION_RELEASE_UNCONFIRMED");
            try
            {
                requireScope();
                observeProtection();
            }
            catch (Exception error)
            {
                throw new Rejection(failureCode = "PREVIEW_VALIDATION_PROTECTION_UNCONFIRMED", error);
            }
        }

        internal void ObserveOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action observeProtection, RelocationInput? relocation = null)
        {
            Require(!attempted && MainThreadDispatcher.isMainThread, "PREVIEW_VALIDATION_EXECUTION_UNAVAILABLE");
            attempted = true;
            try
            {
                guardLiveGraph();
                barrier = new NativeSystemRegistration<PreviewValidationBarrier>(source, new PreviewValidationBarrier());
                barrierReleased = false;
                Register(barrier, appended, requireScope);
                icons.Prepare();

                var instance = new PreviewValidationProducer();
                instance.Configure(relocation?.Actor ?? 1, barrier.Instance, errors, icons.System);
                producer = new NativeSystemRegistration<PreviewValidationProducer>(source, instance);
                producerReleased = false;
                Register(producer, appended, requireScope);
                observeProtection();
                guardLiveGraph();

                var costs = new PreviewCostObservation(source, graph, relocation?.Temps);
                if (relocation == null) instance.ValidateOnce(graph, requireScope, guardLiveGraph, MarkEntered);
                else instance.ValidateRelocationOnce(graph, relocation.Temps, relocation.Original, requireScope, guardLiveGraph, MarkEntered);
                var writer = instance.CompletedDependency;
                ConfirmMapWriter(writer);
                ConfirmIconWriter(writer);
                var iconCommands = icons.ReadAfterJoin(writer, DiagnosticLimit);
                guardLiveGraph();
                var observedErrors = ReadErrors();
                ReadCancellations(source.EntityManager, graph, observedErrors);
                ReadIcons(source.EntityManager, graph, iconCommands);
                var observedCosts = costs.Complete(source, graph);
                fundsUnchanged = (bool)observedCosts["fundsUnchanged"]!;
                if (relocation != null) relocationCosts = observedCosts;
                outcomesObserved = true;
                eligible = observedErrors.Count == 0 && nativeCancelCount == 0 && !HasBlockingIcon(iconCommands);
                guardLiveGraph();
                Require(fundsUnchanged, "PREVIEW_VALIDATION_FUNDS_CHANGED");
                observeProtection();
                protectionConfirmed = true;
                Require(eligible, "PREVIEW_VALIDATION_NATIVE_RESULT_REJECTED");
            }
            catch (Exception error)
            {
                failureCode = error switch
                {
                    Rejection rejected => rejected.Code,
                    PreviewValidationProducer.Rejection rejected => rejected.Code,
                    _ => "PREVIEW_VALIDATION_EXECUTION_FAILED"
                };
                throw new Rejection(failureCode, error);
            }
        }

        private void MarkEntered()
        {
            icons.MarkNativeEntered();
            entered = true;
            mapReleased = false;
        }

        private void ConfirmMapWriter(JobHandle joinedWriter)
        {
            Require(entered && !joinedWriter.Equals(default(JobHandle)) && joinedWriter.IsCompleted
                && errors.World == null && errors.m_ErrorMap.IsCreated && errors.m_ErrorMapDeps.Equals(joinedWriter),
                "PREVIEW_VALIDATION_ERROR_WRITER_UNCONFIRMED");
            mapWriterConfirmed = true;
        }

        private void ConfirmIconWriter(JobHandle writer)
        {
            icons.ConfirmJoin(writer);
            iconWriterConfirmed = true;
        }

        private Dictionary<Entity, ErrorSeverity> ReadErrors()
        {
            var observed = new Dictionary<Entity, ErrorSeverity>();
            var rows = new JArray();
            nativeErrorCount = errors.m_ErrorMap.Count;
            Require(nativeErrorCount >= 0 && nativeErrorCount <= DiagnosticLimit, "PREVIEW_VALIDATION_ERROR_BUDGET");
            using (var iterator = errors.m_ErrorMap.GetEnumerator())
            {
                while (iterator.MoveNext())
                {
                    var item = iterator.Current;
                    Require(rows.Count < nativeErrorCount && item.Key != Entity.Null
                        && item.Value >= ErrorSeverity.Override && item.Value <= ErrorSeverity.CancelError,
                        "PREVIEW_VALIDATION_ERROR_ROW_INVALID");
                    observed.Add(item.Key, item.Value);
                    rows.Add(new JObject { ["entity"] = Id(item.Key), ["severity"] = item.Value.ToString() });
                }
            }
            Require(rows.Count == nativeErrorCount, "PREVIEW_VALIDATION_ERROR_COUNT_CHANGED");
            errorRows = rows;
            return observed;
        }

        private void ReadCancellations(EntityManager entities, Entity[] graph, Dictionary<Entity, ErrorSeverity> observedErrors)
        {
            var (commands, abi) = barrier!.Instance.ReadJoinedCommands();
            nativeCancelCount = commands.Length;
            Require(commands.Length <= graph.Length, "PREVIEW_VALIDATION_CANCEL_BUDGET");
            var targets = new HashSet<Entity>();
            var rows = new JArray();
            foreach (object command in commands)
            {
                Require(command.GetType() == abi.Component
                    && Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) == "SetComponent"
                    && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == TypeManager.GetTypeIndex<Temp>()
                    && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool)),
                    "PREVIEW_VALIDATION_CANCEL_COMMAND_UNSUPPORTED");
                var target = (Entity)abi.Value(command, "Entity", typeof(Entity));
                Require(Array.IndexOf(graph, target) >= 0 && targets.Add(target) && entities.HasComponent<Temp>(target)
                    && observedErrors.TryGetValue(target, out var severity) && severity >= ErrorSeverity.Cancel,
                    "PREVIEW_VALIDATION_CANCEL_TARGET_UNQUALIFIED");
                var expected = entities.GetComponentData<Temp>(target);
                expected.m_Flags |= TempFlags.Hidden | TempFlags.Cancel;
                Require(abi.Value(command, "ComponentValue", typeof(object)) is Temp value && expected.Equals(value),
                    "PREVIEW_VALIDATION_CANCEL_VALUE_CHANGED");
                rows.Add(new JObject { ["entity"] = Id(target), ["flags"] = (uint)expected.m_Flags,
                    ["cost"] = expected.m_Cost, ["value"] = expected.m_Value });
            }
            cancellationRows = rows;
        }

        private void ReadIcons(EntityManager entities, Entity[] graph, IconCommandBuffer.Command[] commands)
        {
            nativeIconCount = commands.Length;
            Require(commands.Length <= DiagnosticLimit, "PREVIEW_VALIDATION_ICON_BUDGET");
            var rows = new JArray();
            foreach (var command in commands)
            {
                bool positioned = (command.m_Flags & IconFlags.CustomLocation) != 0;
                Require(command.m_CommandFlags == (IconCommandBuffer.CommandFlags.Add | IconCommandBuffer.CommandFlags.Temp)
                    && command.m_ClusterLayer == IconClusterLayer.Default && command.m_BufferIndex == 0 && command.m_Delay == 0f
                    && command.m_Priority >= IconPriority.Info && command.m_Priority <= IconPriority.Error
                    && Array.IndexOf(graph, command.m_Owner) >= 0 && command.m_Prefab != Entity.Null
                    && entities.HasComponent<ToolErrorData>(command.m_Prefab)
                    && (!positioned || math.all(math.isfinite(command.m_Location))),
                    "PREVIEW_VALIDATION_ICON_COMMAND_UNSUPPORTED");
                var type = entities.GetComponentData<ToolErrorData>(command.m_Prefab).m_Error;
                rows.Add(new JObject { ["owner"] = Id(command.m_Owner), ["prefab"] = Id(command.m_Prefab),
                    ["target"] = Id(command.m_Target), ["priority"] = command.m_Priority.ToString(), ["type"] = type.ToString(),
                    ["location"] = positioned ? new JArray(command.m_Location.x, command.m_Location.y, command.m_Location.z) : null });
            }
            iconRows = rows;
        }

        private static bool HasBlockingIcon(IconCommandBuffer.Command[] commands)
        {
            foreach (var command in commands)
                if (command.m_Priority == IconPriority.Error) return true;
            return false;
        }

        internal void Release(List<ComponentSystemBase> appended)
        {
            if (!attempted || releaseStarted || producer?.Instance.NativeWorkMayReadSource == true) return;
            releaseStarted = true;
            if (entered)
            {
                try
                {
                    var writer = producer!.Instance.CompletedDependency;
                    if (!mapWriterConfirmed) ConfirmMapWriter(writer);
                    Require(!mapReleaseAttempted, "PREVIEW_VALIDATION_MAP_RELEASE_REPEATED");
                    mapReleaseAttempted = true;
                    errors.m_ErrorMap.Dispose();
                    errors.m_ErrorMap = default;
                    errors.m_ErrorMapDeps = default;
                    mapReleased = true;
                }
                catch (Exception) { failureCode ??= "PREVIEW_VALIDATION_MAP_RELEASE_UNCONFIRMED"; }
                try { if (!iconWriterConfirmed) ConfirmIconWriter(producer!.Instance.CompletedDependency); }
                catch (Exception) { failureCode ??= "PREVIEW_VALIDATION_ICON_WRITER_UNCONFIRMED"; }
            }

            if (!NativeWorkMayReadSource)
            {
                if (producer != null) producerReleased = Retire(producer, appended, () => producer.Instance.DestructionReturned);
                if (barrier != null) barrierReleased = Retire(barrier, appended, () => barrier.Instance.DestructionReturned);
            }
            try { icons.Release(); }
            catch (Exception) { failureCode ??= "PREVIEW_VALIDATION_ICON_RELEASE_UNCONFIRMED"; }
        }

        private static void Register<T>(NativeSystemRegistration<T> owner, List<ComponentSystemBase> appended,
            Action requireScope) where T : ComponentSystemBase
        {
            owner.Add();
            appended.Add(owner.Instance);
            requireScope();
        }

        private bool Retire<T>(NativeSystemRegistration<T> owner, List<ComponentSystemBase> appended,
            Func<bool> destructionReturned) where T : ComponentSystemBase
        {
            try
            {
                owner.Destroy();
                if (owner.PhysicalReleased && (!owner.DestroyAttempted || owner.DestroyReturned && destructionReturned()))
                {
                    appended.Remove(owner.Instance);
                    return true;
                }
            }
            catch (Exception) { }
            failureCode ??= "PREVIEW_VALIDATION_REGISTRATION_RELEASE_UNCONFIRMED";
            return false;
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["observed"] = outcomesObserved, ["eligible"] = outcomesObserved && eligible,
            ["producer"] = (JToken?)producer?.Instance.Snapshot() ?? JValue.CreateNull(),
            ["errorCount"] = nativeErrorCount, ["cancelCommandCount"] = nativeCancelCount, ["iconCount"] = nativeIconCount,
            ["errors"] = errorRows, ["cancellations"] = cancellationRows, ["icons"] = iconRows, ["fundsUnchanged"] = fundsUnchanged,
            ["mapWriterConfirmed"] = mapWriterConfirmed, ["iconWriterConfirmed"] = iconWriterConfirmed,
            ["mapReleased"] = mapReleased, ["iconsReleased"] = icons.ResourcesReleased,
            ["producerReleased"] = producerReleased, ["barrierReleased"] = barrierReleased,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["sourcePlaybackCalled"] = false, ["failureCode"] = failureCode
        };

        internal JObject RelocationSnapshot()
        {
            var value = Snapshot();
            value["costs"] = relocationCosts;
            return value;
        }

        internal sealed class RelocationInput
        {
            internal readonly int Actor;
            internal readonly RelocationDefinitionSource Original;
            internal readonly Temp[] Temps;

            internal RelocationInput(int actor, RelocationDefinitionSource original, Temp[] temps)
            { Actor = actor; Original = original; Temps = temps; }
        }

        private void RequireFinal(bool condition, string code)
        {
            if (condition && failureCode == null) return;
            failureCode ??= code;
            throw new Rejection(failureCode);
        }

        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : InvalidOperationException
        {
            internal string Code { get; }
            internal Rejection(string code, Exception? inner = null) : base(code, inner) { Code = code; }
        }
    }
}
#endif
