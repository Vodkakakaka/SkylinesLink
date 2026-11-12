#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using AgeMask = Game.Tools.AgeMask;
using PlacementFlags = Game.Objects.PlacementFlags;

namespace SkylinesLink.Mod.Native
{
    // One explicitly owned native definition call. No playback, tool activation,
    // retries, persistent scheduler, or inference about city construction.
    internal sealed class ObjectDefinitionExecutionProbe
    {
        private World? source;
        private NativeProbeSourceProtection? protection;
        private ObjectDefinitionBarrier? barrier;
        private ObjectDefinitionProducer? producer;
        private DefinitionProducerPatchProbe patch = new DefinitionProducerPatchProbe();
        private JObject? commands;
        private ObjectDefinitionPayload? payload;
        private RelocationDefinitionPayload? relocationPayload;
        private bool ran, finished, nativeBuildQualified, patchRechecked, commandInspectionPassed, inputRevalidated;
        private bool barrierAddAttempted, barrierAddReturned, producerAddAttempted, producerAddReturned;
        private bool barrierDestroyAttempted, barrierDestroyReturned, producerDestroyAttempted, producerDestroyReturned;
        private bool barrierCreated, barrierDestroyed, producerCreated, producerDestroyed;
        private bool barrierCreationStarted, barrierDestructionStarted, producerCreationStarted, producerDestructionStarted;
        private bool nativeCallStarted, nativeCallReturned, completionObserved, parameterAllocationUnconfirmed, parametersCreated;
        private bool producerReleased = true, barrierReleased = true, unknown, resourcesReleased = true;
        private bool inputObserved;
        private int inputNets, inputAreas, inputCommands, inputAreaNodes;
        private string? inputPurpose, failureCode, exceptionType, producerReleaseFailure, barrierReleaseFailure;
        private double elapsedMilliseconds;
        private float placementOffsetY;
        private bool groundPoseVerified, relocationSourceUnchanged;

        internal bool Finished => finished;
        internal string? FailureCode => failureCode;
        internal float3 PlacementPosition { get; private set; }
        internal float PlacementOffsetY => placementOffsetY;
        internal bool ChecksPassed => finished && commandInspectionPassed && failureCode == null && ResourcesReleased
            && ProtectionPassed;
        internal bool RelocationPassed => ChecksPassed && relocationSourceUnchanged && relocationPayload != null;
        internal bool ResourcesReleased => resourcesReleased && !UnknownNativeResponsibility;
        internal bool UnknownNativeResponsibility => unknown || NativeWorkMayReadSource || parameterAllocationUnconfirmed;
        internal bool NativeWorkMayReadSource => nativeCallStarted && !completionObserved;
        private bool ProtectionPassed => protection?.Passed == true;

        internal void RunOnce(ObjectDefinitionInputProbe.Result input, Entity theme, bool leftHandTraffic,
            GroundPreviewSite site, RandomSeed seed, AgeMask ages, Action requireCurrentScope, RelocationDefinitionSource? relocation = null)
        {
            if (ran) { Fail("OBJECT_EXECUTION_ALREADY_RAN"); return; }
            ran = true;
            var timer = Stopwatch.StartNew();
            try
            {
                Check(MainThreadDispatcher.isMainThread, "OBJECT_EXECUTION_MAIN_THREAD_REQUIRED");
                if (requireCurrentScope == null) throw new CheckFailure("OBJECT_EXECUTION_SCOPE_ASSERTION_REQUIRED");
                if (input == null) throw new CheckFailure("OBJECT_EXECUTION_INPUT_UNQUALIFIED");
                source = input.Source;
                var selected = ObserveInput(input);
                nativeBuildQualified = NativeConstructionBuild.Game(typeof(ObjectToolBaseSystem).Module)
                    && NativeConstructionBuild.Entities(typeof(World).Module);
                Check(nativeBuildQualified, "OBJECT_EXECUTION_NATIVE_BUILD_UNQUALIFIED");
                patch = ObjectDefinitionPatchProbe.Observe(); Check(patch.Qualified, patch.Reason);
                requireCurrentScope();
                CaptureBaseline();
                RequireFreshRoot(input, selected!);

                // Retain exact objects before Add: SystemCreated is invoked after
                // registration/OnCreate, before AddSystemManaged returns.
                barrier = new ObjectDefinitionBarrier(); barrierReleased = false; resourcesReleased = false;
                barrierAddAttempted = true;
                source!.AddSystemManaged(barrier); barrierAddReturned = true;
                requireCurrentScope();
                Check(barrier.CreationReturned && ReferenceEquals(barrier.World, source), "OBJECT_BARRIER_CREATE_UNCONFIRMED");
                RequireProtectionWithOwnedSystems(expectBarrier: true, expectProducer: false);
                producer = new ObjectDefinitionProducer(); producerReleased = false;
                producerAddAttempted = true;
                source!.AddSystemManaged(producer); producerAddReturned = true;
                requireCurrentScope();
                Check(producer.CreationReturned && ReferenceEquals(producer.World, source), "OBJECT_PRODUCER_CREATE_UNCONFIRMED");
                RequireProtectionWithOwnedSystems(expectBarrier: true, expectProducer: true);
                patch = ObjectDefinitionPatchProbe.Observe(); patchRechecked = true;
                Check(patch.Qualified, patch.Reason);
                // Add's callbacks can also mutate prefab buffers in place. A
                // stable World/Root identity and scalar values are insufficient
                // to carry the pre-creation graph's allocation bound forward.
                var prefabs = source!.GetExistingSystemManaged<PrefabSystem>();
                var configuration = source.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                Check(prefabs != null && configuration != null, "OBJECT_EXECUTION_DEPENDENCY_CHANGED");
                Entity expectedRoot = input.Root;
                input = ObjectDefinitionInputProbe.Select(source, prefabs!, configuration!, ObjectDefinitionInputProbe.Purpose.WindPower,
                    row => row.RootIndex == expectedRoot.Index && row.RootVersion == expectedRoot.Version);
                Check(input.Complete && input.Root == expectedRoot, "OBJECT_EXECUTION_INPUT_SELECTION_CHANGED");
                selected = ObserveInput(input);
                inputRevalidated = true;
                RequireFreshRoot(input, selected);
                Check(configuration!.defaultTheme == theme && configuration.leftHandTraffic == leftHandTraffic,
                    "OBJECT_EXECUTION_CITY_CONFIGURATION_CHANGED");
                // Add's SystemCreated listeners can start a load without
                // replacing World or old prefab entities. The single caller's
                // synchronous Ready/instance/generation assertion closes that gap.
                requireCurrentScope();
                ControlPoint point = site.RequireCurrent(source, input.Root, requireCurrentScope, out placementOffsetY);
                if (relocation != null)
                {
                    relocation.RequireUnchanged(source, input.Root, requireCurrentScope);
                    Check(relocation.AcceptsSite(point.m_Position.xz) && point.m_Rotation.Equals(relocation.RootTransform.m_Rotation),
                        "RELOCATION_DEFINITION_SITE_CHANGED");
                }
                producer.CaptureOnce(barrier, input.Root, theme, leftHandTraffic, point, seed, ages, relocation);
                ObserveProducer();
                Check(nativeCallStarted && nativeCallReturned && completionObserved, "OBJECT_PRODUCER_COMPLETION_UNCONFIRMED");
                RequireProtectionWithOwnedSystems(expectBarrier: true, expectProducer: true);
                if (relocation == null)
                {
                    commands = ObjectDefinitionCommandProbe.Capture(producer, input, out payload);
                    requireCurrentScope();
                    VerifyGroundDefinition(point);
                }
                else
                {
                    relocation.RequireUnchanged(source, input.Root, requireCurrentScope);
                    commands = ObjectDefinitionCommandProbe.CaptureRelocation(producer, relocation, point, out relocationPayload);
                    PlacementPosition = point.m_Position;
                    groundPoseVerified = true;
                }
                commandInspectionPassed = true;
            }
            catch (CheckFailure failure) { Fail(failure.Code); }
            catch (GroundPreviewSite.Rejection failure) { Fail(failure.Code); }
            catch (RelocationDefinitionSource.Rejection failure) { Fail(failure.Code); }
            catch (PreviewCommitExecution.Rejection failure) { Fail(failure.Code); }
            catch (Exception error) { Fail("OBJECT_DEFINITION_EXECUTION_FAILED"); exceptionType = ShortType(error); }
            finally
            {
                try
                {
                    ObserveProducer();
                    if (NativeWorkMayReadSource) unknown = true;
                    else
                    {
                        // Completion and disposal are separate facts. A failed
                        // decoder never prevents independent physical cleanup.
                        ReleaseProducer();
                        ReleaseBarrier();
                    }
                    AuditAfterRelease();
                    resourcesReleased = producerReleased && barrierReleased && !UnknownNativeResponsibility;
                    if (!resourcesReleased) Fail("OBJECT_DEFINITION_RELEASE_UNCONFIRMED");
                    if (protection != null && !ProtectionPassed) Fail("OBJECT_DEFINITION_PROTECTION_CHANGED");
                }
                catch (Exception error)
                {
                    unknown = true; resourcesReleased = false;
                    Fail("OBJECT_DEFINITION_CLEANUP_OBSERVATION_FAILED"); exceptionType ??= ShortType(error);
                }
                finally { timer.Stop(); elapsedMilliseconds = timer.Elapsed.TotalMilliseconds; finished = true; }
            }
            if (relocation != null && completionObserved && ResourcesReleased)
            {
                try
                {
                    relocation.RequireUnchanged(source!, input.Root, requireCurrentScope!);
                    relocationSourceUnchanged = true;
                }
                catch (RelocationDefinitionSource.Rejection failure) { Fail(failure.Code); }
                catch (PreviewCommitExecution.Rejection failure) { Fail(failure.Code); }
                catch (Exception error) { Fail("RELOCATION_DEFINITION_POST_RELEASE_CHECK_FAILED"); exceptionType ??= ShortType(error); }
            }
        }

        private ObjectDefinitionInputProbe.CandidateStatistics ObserveInput(ObjectDefinitionInputProbe.Result input)
        {
            Check(input.Complete && input.Source != null && input.Source.IsCreated && ReferenceEquals(input.Source, source)
                && input.Root != Entity.Null && input.Purpose == ObjectDefinitionInputProbe.Purpose.WindPower
                && input.Candidates.Count <= 128, "OBJECT_EXECUTION_INPUT_UNQUALIFIED");
            ObjectDefinitionInputProbe.CandidateStatistics? selected = null;
            foreach (var candidate in input.Candidates)
                if (candidate.RootIndex == input.Root.Index && candidate.RootVersion == input.Root.Version)
                {
                    Check(selected == null, "OBJECT_EXECUTION_INPUT_AMBIGUOUS"); selected = candidate;
                }
            Check(selected != null && selected.Eligible && selected.Complete && selected.InspectedReferencesComplete
                && selected.Purpose == input.Purpose, "OBJECT_EXECUTION_INPUT_UNQUALIFIED");
            inputPurpose = input.Purpose.ToString(); inputNets = selected!.RootSubNetCount; inputAreas = selected.RootSubAreaCount;
            inputCommands = selected.CommandUpperBound; inputAreaNodes = selected.AreaOutputNodeUpperBound;
            Check(inputNets >= 0 && inputNets <= 256 && inputAreas >= 0 && inputAreas <= 256
                && inputCommands == 4 + 6 * inputNets + 5 * inputAreas && inputAreaNodes >= 0 && inputAreaNodes <= 4096,
                "OBJECT_EXECUTION_INPUT_BUDGET");
            inputObserved = true;
            return selected;
        }

        private void CaptureBaseline()
        {
            protection = new NativeProbeSourceProtection(source!);
            Check(!protection.Contains<ObjectDefinitionBarrier>() && !protection.Contains<ObjectDefinitionProducer>(),
                "OBJECT_DEFINITION_OWNER_ALREADY_PRESENT");
        }
        private void RequireFreshRoot(ObjectDefinitionInputProbe.Result input, ObjectDefinitionInputProbe.CandidateStatistics selected)
        {
            Check(ReferenceEquals(input.Source, source) && source != null && source.IsCreated, "OBJECT_EXECUTION_SOURCE_CHANGED");
            var entities = source!.EntityManager;
            Check(entities.Exists(input.Root) && entities.HasComponent<PrefabData>(input.Root)
                && entities.IsComponentEnabled<PrefabData>(input.Root) && !entities.HasComponent<Deleted>(input.Root)
                && entities.HasComponent<PlaceableObjectData>(input.Root) && entities.HasComponent<WindPoweredData>(input.Root)
                && (!entities.HasComponent<Locked>(input.Root) || !entities.IsComponentEnabled<Locked>(input.Root)), "OBJECT_EXECUTION_ROOT_CHANGED");
            PlaceableObjectData placeable = entities.GetComponentData<PlaceableObjectData>(input.Root);
            WindPoweredData wind = entities.GetComponentData<WindPoweredData>(input.Root);
            Check(placeable.m_ConstructionCost == selected.RootConstructionCost && placeable.m_ConstructionCost > 0
                && placeable.m_ConstructionCost <= int.MaxValue && selected.WindNominalProductionRaw == wind.m_Production
                && selected.WindMaximumWindRaw == wind.m_MaximumWind && wind.m_Production > 0 && wind.m_MaximumWind > 0,
                "OBJECT_EXECUTION_ROOT_FACTS_CHANGED");
        }

        private void VerifyGroundDefinition(ControlPoint point)
        {
            Check(payload != null, "GROUND_PREVIEW_PAYLOAD_UNAVAILABLE");
            var actual = payload!.RootObject;
            Check(actual.m_Position.Equals(point.m_Position) && actual.m_Rotation.Equals(point.m_Rotation)
                && actual.m_Elevation == point.m_Elevation, "GROUND_PREVIEW_NATIVE_POSE_CHANGED");
            var manager = source!.EntityManager;
            var network = payload.CourseCreation.m_Prefab;
            Check(manager.Exists(network) && manager.HasComponent<NetGeometryData>(network)
                && !manager.HasComponent<OutsideConnectionData>(network)
                && (manager.GetComponentData<NetGeometryData>(network).m_Flags & Game.Net.GeometryFlags.OnWater) == 0,
                "GROUND_PREVIEW_SPECIAL_SUBNET");
            PlacementPosition = actual.m_Position;
            groundPoseVerified = true;
        }

        private void RequireProtectionWithOwnedSystems(bool expectBarrier, bool expectProducer)
        {
            ObserveProtection(expectBarrier, expectProducer);
            Check(ProtectionPassed, "OBJECT_DEFINITION_PROTECTION_CHANGED");
        }

        private void ObserveProtection(bool expectBarrier, bool expectProducer)
        {
            if (expectBarrier && expectProducer) protection!.Observe(barrier!, producer!);
            else if (expectBarrier) protection!.Observe(barrier!);
            else if (expectProducer) protection!.Observe(producer!);
            else protection!.Observe();
        }
        private void ObserveProducer()
        {
            if (producer != null)
            {
                producerCreationStarted = producer.CreationStarted; producerDestructionStarted = producer.DestructionStarted;
                producerCreated = producer.CreationReturned; producerDestroyed = producer.DestructionReturned;
                nativeCallStarted = producer.NativeCallStarted; nativeCallReturned = producer.NativeCallReturned;
                completionObserved = producer.CompletionObserved;
                parameterAllocationUnconfirmed = producer.ParameterAllocationUnconfirmed; parametersCreated = producer.ParametersCreated;
            }
            if (barrier != null)
            {
                barrierCreationStarted = barrier.CreationStarted; barrierDestructionStarted = barrier.DestructionStarted;
                barrierCreated = barrier.CreationReturned; barrierDestroyed = barrier.DestructionReturned;
            }
        }

        private void ReleaseProducer()
        {
            if (producer == null) { producerReleased = true; return; }
            try
            {
                Check(source != null && source.IsCreated && ReferenceEquals(producer.World, source)
                    && NativeProbeSourceProtection.IsRegistered(source!, producer), "PRODUCER_REGISTRATION_UNRESOLVED");
                Check(producer.ParametersCanBeReleased, "PRODUCER_NATIVE_WORK_UNRESOLVED");
                producerDestroyAttempted = true;
                source!.DestroySystemManaged(producer); producerDestroyReturned = true;
                ObserveProducer();
                producerReleased = producerDestroyReturned && producerDestroyed && !parametersCreated
                    && !parameterAllocationUnconfirmed && producer.World == null && !NativeProbeSourceProtection.IsRegistered(source!, producer);
                Check(producerReleased, "PRODUCER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { producerReleaseFailure = error is CheckFailure failure ? failure.Code : "PRODUCER_DESTROY_FAILED"; unknown = true; }
            finally { ObserveProducer(); }
        }

        private void ReleaseBarrier()
        {
            if (barrier == null) { barrierReleased = true; return; }
            try
            {
                Check(!NativeWorkMayReadSource && source != null && source.IsCreated && ReferenceEquals(barrier.World, source)
                    && NativeProbeSourceProtection.IsRegistered(source!, barrier), "BARRIER_REGISTRATION_UNRESOLVED");
                // A partially executed native OnCreate may have allocated only
                // some fields. Calling its ordinary destructor would be unsafe.
                Check(!barrier.CreationStarted || barrier.CreationReturned, "BARRIER_CREATION_UNRESOLVED");
                barrierDestroyAttempted = true;
                source!.DestroySystemManaged(barrier); barrierDestroyReturned = true;
                ObserveProducer();
                barrierReleased = barrierDestroyReturned && barrierDestroyed && barrier.World == null
                    && !NativeProbeSourceProtection.IsRegistered(source!, barrier);
                Check(barrierReleased, "BARRIER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error) { barrierReleaseFailure = error is CheckFailure failure ? failure.Code : "BARRIER_DESTROY_FAILED"; unknown = true; }
            finally { ObserveProducer(); }
        }

        private void AuditAfterRelease() { protection?.Observe(); }

        internal ObjectDefinitionPayload RequirePayload(World expectedSource)
        {
            Check(MainThreadDispatcher.isMainThread && ChecksPassed && payload != null && source != null
                && source.IsCreated && ReferenceEquals(source, expectedSource), "OBJECT_DEFINITION_PAYLOAD_UNAVAILABLE");
            return payload!;
        }

        internal RelocationDefinitionPayload RequireRelocationPayload(World expectedSource)
        {
            Check(MainThreadDispatcher.isMainThread && RelocationPassed && source != null && source.IsCreated
                && ReferenceEquals(source, expectedSource), "RELOCATION_DEFINITION_PAYLOAD_UNAVAILABLE");
            return relocationPayload!;
        }

        internal JObject RelocationSnapshot() => new JObject {
            ["passed"] = RelocationPassed, ["resourcesReleased"] = ResourcesReleased,
            ["nativeWorkMayReadSource"] = NativeWorkMayReadSource, ["nativeCompleted"] = completionObserved,
            ["sourceUnchanged"] = relocationSourceUnchanged, ["observed"] = commands?.DeepClone(), ["failureCode"] = Value(failureCode)
        };

        // Snapshot reads captured ordinary facts only, including after the
        // source has gone away. No activePrefab/GetPrefab/World getter is used.
        internal JObject Snapshot() => new JObject {
            ["finished"] = Finished, ["checksPassed"] = ChecksPassed, ["resourcesReleased"] = ResourcesReleased,
            ["unknownNativeResponsibility"] = UnknownNativeResponsibility, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
            ["nativeBuildQualified"] = nativeBuildQualified, ["patchRechecked"] = patchRechecked,
            ["inputRevalidatedAfterCreation"] = inputRevalidated,
            ["patchQualified"] = patch.Qualified, ["patchReason"] = patch.Reason,
            ["methodsChecked"] = patch.MethodsChecked, ["methodSetSha256"] = patch.MethodSetSha256,
            ["input"] = new JObject { ["observed"] = inputObserved, ["purpose"] = Value(inputPurpose), ["rootSubNetCount"] = inputNets,
                ["rootSubAreaCount"] = inputAreas, ["commandUpperBound"] = inputCommands, ["areaOutputNodeUpperBound"] = inputAreaNodes },
            ["nativeCallStarted"] = nativeCallStarted, ["nativeCallReturned"] = nativeCallReturned, ["completionObserved"] = completionObserved,
            ["parametersCreated"] = parametersCreated, ["parameterAllocationUnconfirmed"] = parameterAllocationUnconfirmed,
            ["producer"] = new JObject { ["addAttempted"] = producerAddAttempted, ["addReturned"] = producerAddReturned,
                ["creationStarted"] = producerCreationStarted, ["creationReturned"] = producerCreated, ["destroyAttempted"] = producerDestroyAttempted,
                ["destructionStarted"] = producerDestructionStarted,
                ["destroyReturned"] = producerDestroyReturned, ["destructionReturned"] = producerDestroyed,
                ["released"] = producerReleased, ["failureCode"] = Value(producerReleaseFailure) },
            ["barrier"] = new JObject { ["addAttempted"] = barrierAddAttempted, ["addReturned"] = barrierAddReturned,
                ["creationStarted"] = barrierCreationStarted, ["creationReturned"] = barrierCreated, ["destroyAttempted"] = barrierDestroyAttempted,
                ["destructionStarted"] = barrierDestructionStarted,
                ["destroyReturned"] = barrierDestroyReturned, ["destructionReturned"] = barrierDestroyed,
                ["released"] = barrierReleased, ["failureCode"] = Value(barrierReleaseFailure) },
            ["protection"] = protection == null ? new JObject { ["observed"] = false } : protection.Snapshot(),
            ["commandInspectionPassed"] = commandInspectionPassed, ["commands"] = commands == null ? JValue.CreateNull() : commands.DeepClone(),
            ["materializationInputSupported"] = payload != null,
            ["groundPoseVerified"] = groundPoseVerified, ["placementOffsetY"] = placementOffsetY,
            ["elapsedMilliseconds"] = elapsedMilliseconds, ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType),
            ["playbackCalled"] = false,
            ["scope"] = "One native definition job with an observed ground pose. Placement legality, fees and construction remain unqualified."
        };

        private void Fail(string code) { failureCode ??= code; }
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
