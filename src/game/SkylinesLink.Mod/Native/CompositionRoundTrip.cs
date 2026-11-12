#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.City;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using AgeMask = Game.Tools.AgeMask;

namespace SkylinesLink.Mod.Native
{
    // Each batch finishes its own graph on the main thread. Only copied
    // inputs and the World-owned composition are used after the frame boundary.
    internal sealed class CompositionRoundTrip
    {
        private readonly World source;
        private readonly Action requireScope;
        private readonly Batch publish = new Batch();
        private readonly Batch reuse = new Batch();
        private readonly Batch commit = new Batch();
        private readonly Batch rebuild = new Batch();
        private Batch rebuildPreparation = new Batch();
        private Batch? rebuildPreview;
        private JObject? rebuildCancellation;
        private readonly JArray rejectedRebuildShapes = new JArray();
        private JObject? selectedRebuildShape;
        private GroundPreviewSite? rebuildSite;
        private Entity rebuildComposition;
        private int? rebuildPreparationFrame;
        private int? rebuildPreviewFrame;
        private NewObjectPreviewEvidence? rebuildPreviewEvidence;
        private uint rebuildDataInitBefore, rebuildMeshRefBefore;
        private uint rebuildQuotedCost;
        private bool rebuildCacheHit, rebuildCompositionReady;
        private readonly NetCompositionSystem dataInit;
        private readonly NetCompositionMeshRefSystem meshRef;
        private readonly uint dataInitBefore, meshRefBefore;
        private GroundPreviewSite? site;
        private ObjectDefinitionExecutionProbe? relocation;
        private RelocationGenerationExecution? relocationGeneration;
        private RelocationPreviewEntities? relocationPreview;
        private ObjectDefinitionExecutionProbe? moveDefinition;
        private RelocationGenerationExecution? moveGeneration;
        private RelocationPreviewEntities? move;
        private bool rebuildVerified;
        private int? publishFrame, observeFrame, commitFrame, relocationFrame;
        private bool dataInitAdvanced, meshRefAdvanced, readyBeforeReuse, survivesRetirement;

        internal CompositionRoundTrip(World source, Action requireScope)
        {
            this.source = source;
            this.requireScope = requireScope;
            requireScope();
            dataInit = source.GetExistingSystemManaged<NetCompositionSystem>()
                ?? throw new Rejection("COMPOSITION_DEFAULT_DATA_INIT_MISSING");
            meshRef = source.GetExistingSystemManaged<NetCompositionMeshRefSystem>()
                ?? throw new Rejection("COMPOSITION_DEFAULT_MESH_REF_MISSING");
            dataInitBefore = dataInit.CheckedStateRef.LastSystemVersion;
            meshRefBefore = meshRef.CheckedStateRef.LastSystemVersion;
        }

        internal bool WorkersFinished => publish.Finished && reuse.Finished && commit.Finished && rebuild.Finished && rebuildPreparation.Finished
            && rebuildPreview?.Finished != false
            && relocation?.Finished != false && relocationGeneration?.Finished != false && relocationPreview?.Finished != false
            && moveDefinition?.Finished != false && moveGeneration?.Finished != false && move?.Finished != false;
        internal bool ResourcesReleased => publish.ResourcesReleased && reuse.ResourcesReleased && commit.ResourcesReleased && rebuild.ResourcesReleased && rebuildPreparation.ResourcesReleased
            && rebuildPreview?.ResourcesReleased != false
            && relocation?.ResourcesReleased != false && relocationGeneration?.ResourcesReleased != false
            && relocationPreview?.ResourcesReleased != false && moveDefinition?.ResourcesReleased != false
            && moveGeneration?.ResourcesReleased != false && move?.ResourcesReleased != false;
        internal bool NativeStateUncertain => publish.NativeStateUncertain || reuse.NativeStateUncertain || commit.NativeStateUncertain || rebuild.NativeStateUncertain || rebuildPreparation.NativeStateUncertain
            || rebuildPreview?.NativeStateUncertain == true
            || relocation?.NativeWorkMayReadSource == true || relocationGeneration?.NativeWorkMayReadSource == true
            || relocationPreview?.NativeStateUncertain == true || moveDefinition?.NativeWorkMayReadSource == true
            || moveGeneration?.NativeWorkMayReadSource == true || move?.NativeStateUncertain == true;
        internal bool Passed => readyBeforeReuse && survivesRetirement && reuse.Passed && publish.Passed
            && commit.Passed && commit.Generation?.Commit?.Passed == true && relocation?.RelocationPassed == true
            && relocationGeneration?.Passed == true && relocationPreview?.Passed == true && move?.CommitPassed == true
            && rebuildVerified;

        internal void Advance()
        {
            requireScope();
            if (!publishFrame.HasValue)
            {
                RunBatch(publish, Entity.Null);
                publishFrame = UnityEngine.Time.frameCount;
                return;
            }
            if (Passed || publishFrame.Value == UnityEngine.Time.frameCount) return;
            if (survivesRetirement)
            {
                AdvanceCommit();
                return;
            }

            observeFrame = UnityEngine.Time.frameCount;
            var generation = publish.Generation!;
            var inputs = generation.InitializationInputs ?? throw new Rejection("COMPOSITION_INPUT_TICKET_MISSING");
            var expected = generation.InitializationExpectation ?? throw new Rejection("COMPOSITION_EXPECTATION_MISSING");
            Entity entity = generation.PublishedComposition;
            try
            {
                Require(ReferenceEquals(source.GetExistingSystemManaged<NetCompositionSystem>(), dataInit)
                    && ReferenceEquals(source.GetExistingSystemManaged<NetCompositionMeshRefSystem>(), meshRef),
                    "COMPOSITION_DEFAULT_SYSTEM_CHANGED");
                dataInit.CheckedStateRef.CompleteDependency();
                requireScope();
                meshRef.CheckedStateRef.CompleteDependency();
                requireScope();
                dataInitAdvanced = dataInit.CheckedStateRef.LastSystemVersion != dataInitBefore;
                meshRefAdvanced = meshRef.CheckedStateRef.LastSystemVersion != meshRefBefore;
                Require(dataInitAdvanced && meshRefAdvanced, "COMPOSITION_DEFAULT_SYSTEM_NOT_RUN");
                inputs.RequireUnchanged();
                expected.RequireReady(entity, requireScope);
                readyBeforeReuse = true;
                RunBatch(reuse, entity);
                inputs.RequireUnchanged();
                expected.RequireReady(entity, requireScope);
                RequirePublishedRow(reuse.Generation!.SelectionReceipt!, entity);
                survivesRetirement = true;
            }
            finally { ReleaseInputSnapshot(); }
        }

        private void AdvanceCommit()
        {
            int frame = UnityEngine.Time.frameCount;
            if (!commitFrame.HasValue)
            {
                if (frame == observeFrame) return;
                var shared = publish.Generation!.PublishedComposition;
                RequirePublishedRow(reuse.Generation!.SelectionReceipt!, shared);
                commitFrame = frame;
                RunBatch(commit, shared, submit: true);
                RequirePublishedRow(commit.Generation!.SelectionReceipt!, shared);
                return;
            }
            var submitted = commit.Generation!.Commit!;
            if (!submitted.Passed)
            {
                submitted.ObserveDefaultWorld(source, requireScope);
                return;
            }
            if (relocationPreview == null)
            {
                Require(!relocationFrame.HasValue, "RELOCATION_DEFINITION_ALREADY_ATTEMPTED");
                relocationFrame = frame;
                RunRelocation(submitted, submit: false);
                return;
            }
            if (move == null)
            {
                if (frame == relocationFrame) return;
                Require(relocationPreview.Passed && moveDefinition == null, "RELOCATION_PREVIEW_FRESH_COMMIT_UNAVAILABLE");
                RunRelocation(submitted, submit: true);
                return;
            }
            if (!move.CommitPassed)
            {
                move.ObserveCommittedWorld();
                return;
            }
            if (rebuild.Generation == null)
            {
                AdvanceAlternateBuild(frame);
                return;
            }
            var second = rebuild.Generation.Commit!;
            if (!second.Passed)
            {
                second.ObserveDefaultWorld(source, requireScope);
                return;
            }
            var newlyBuilt = second.RequirePublishedBuildingAndNode();
            var oldFixture = submitted.RequirePublishedBuildingAndNode();
            move.RequireSeparateBuild(newlyBuilt.Root, newlyBuilt.Node);
            var entities = source.EntityManager;
            Require(newlyBuilt.Root != oldFixture.Root && newlyBuilt.Node != oldFixture.Node
                && rebuild.RootPrefab == rebuildPreparation.RootPrefab && rebuild.RootPrefab != commit.RootPrefab
                && entities.GetComponentData<PrefabRef>(newlyBuilt.Root).m_Prefab == rebuild.RootPrefab
                && entities.GetComponentData<PrefabRef>(oldFixture.Root).m_Prefab == commit.RootPrefab
                && entities.GetComponentData<Game.Objects.Transform>(newlyBuilt.Root).m_Position.Equals(rebuild.Definition!.PlacementPosition),
                "RELOCATION_PREVIEW_REBUILD_IDENTITY_OR_SITE_CHANGED");
            rebuildVerified = true;
        }

        private void AdvanceAlternateBuild(int frame)
        {
            if (rebuildPreparation.Generation == null)
            {
                Require(rebuildPreparationFrame == null, "REBUILD_COMPOSITION_RESOLUTION_REPEATED");
                rebuildPreparationFrame = frame;
                PrepareAlternateDefinition();
                rebuildDataInitBefore = dataInit.CheckedStateRef.LastSystemVersion;
                rebuildMeshRefBefore = meshRef.CheckedStateRef.LastSystemVersion;
                RunGenerationBatch(rebuildPreparation, Entity.Null, submit: false, resolveComposition: true);

                var generation = rebuildPreparation.Generation!;
                var selected = generation.SelectionReceipt ?? throw new Rejection("REBUILD_COMPOSITION_SELECTION_MISSING");
                rebuildCacheHit = selected.ObservedSelection;
                rebuildComposition = rebuildCacheHit ? selected.After.m_Composition : generation.PublishedComposition;
                Require(rebuildComposition != Entity.Null && rebuildCacheHit != selected.NeedsPublication,
                    "REBUILD_COMPOSITION_IDENTITY_UNAVAILABLE");
                return;
            }

            int preparedAt = rebuildPreparationFrame ?? throw new Rejection("REBUILD_COMPOSITION_PREPARATION_MISSING");
            Require(frame >= preparedAt && frame - preparedAt <= 180 && rebuildPreparation.Passed && rebuild.Definition == null,
                "REBUILD_COMPOSITION_NOT_READY");
            if (frame == preparedAt) return;
            if (!rebuildCompositionReady)
            {
                var prepared = rebuildPreparation.Generation!;
                if (rebuildCacheHit) RequirePublishedRow(prepared.SelectionReceipt!, rebuildComposition);
                else
                {
                    Require(ReferenceEquals(source.GetExistingSystemManaged<NetCompositionSystem>(), dataInit)
                        && ReferenceEquals(source.GetExistingSystemManaged<NetCompositionMeshRefSystem>(), meshRef),
                        "REBUILD_COMPOSITION_PROVIDER_CHANGED");
                    if (dataInit.CheckedStateRef.LastSystemVersion == rebuildDataInitBefore
                        || meshRef.CheckedStateRef.LastSystemVersion == rebuildMeshRefBefore) return;
                    var inputs = prepared.InitializationInputs ?? throw new Rejection("REBUILD_COMPOSITION_INPUT_TICKET_MISSING");
                    try
                    {
                        dataInit.CheckedStateRef.CompleteDependency();
                        requireScope();
                        meshRef.CheckedStateRef.CompleteDependency();
                        requireScope();
                        inputs.RequireUnchanged();
                        prepared.InitializationExpectation!.RequireReady(rebuildComposition, requireScope);
                    }
                    finally { inputs.ReleaseSnapshots(); }
                }
                rebuildCompositionReady = true;
            }
            if (frame == rebuildPreviewFrame) return;

            Entity expectedPrefab = rebuildPreparation.RootPrefab;
            var confirmed = SelectWindInput(out var city, row =>
                row.RootIndex == expectedPrefab.Index && row.RootVersion == expectedPrefab.Version);
            if (!rebuildPreviewFrame.HasValue)
            {
                var preview = rebuildPreview = new Batch();
                RunDefinitionBatch(preview, confirmed, city, rebuildSite!, rebuildComposition, submit: false,
                    samePlacement: rebuildPreparation.Definition);
                var generation = preview.Generation!;
                RequirePublishedRow(generation.SelectionReceipt!, rebuildComposition);
                var cancelled = generation.CancellationSnapshot() ?? throw new Rejection("REBUILD_PREVIEW_CANCELLATION_MISSING");
                rebuildPreviewEvidence = generation.ObservedPreviewEvidence;
                Require(rebuildPreviewEvidence != null && rebuildPreviewEvidence.Cost > 0 && (bool?)cancelled["passed"] == true
                    && (bool?)cancelled["fundsUnchanged"] == true && (bool?)cancelled["groupAbsent"] == true
                    && (bool?)cancelled["slotsReleased"] == true, "REBUILD_PREVIEW_CANCELLATION_UNCONFIRMED");
                rebuildCancellation = new JObject {
                    ["frame"] = frame, ["cost"] = rebuildPreviewEvidence!.Cost, ["members"] = cancelled["members"]?.DeepClone(),
                    ["identity"] = rebuildPreviewEvidence.Snapshot(),
                    ["fundsUnchanged"] = true, ["groupAbsent"] = true, ["slotsReleased"] = true
                };
                rebuildPreviewFrame = frame;
                rebuildPreview = null;
                return;
            }
            RunDefinitionBatch(rebuild, confirmed, city, rebuildSite!, rebuildComposition, submit: true,
                samePlacement: rebuildPreparation.Definition, expectedPreview: rebuildPreviewEvidence);
            RequirePublishedRow(rebuild.Generation!.SelectionReceipt!, rebuildComposition);
        }

        private void PrepareAlternateDefinition()
        {
            var examined = new HashSet<Entity>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var input = SelectWindInput(out var configuration, EligibleAlternate);
                rebuildPreparation = new Batch();
                try { rebuildSite = site!.RequalifyAnchor(input.Root, requireScope); }
                catch (GroundPreviewSite.Rejection error) { throw new Rejection(error.Code); }
                RunDefinition(rebuildPreparation, input, configuration, rebuildSite);
                var payload = rebuildPreparation.Definition!.RequirePayload(source);
                var shape = DescribeRebuildCourse(payload, input.Root);
                if (ObjectGenerationPreflight.IsSupportedShape(payload))
                {
                    selectedRebuildShape = shape;
                    foreach (var row in input.Candidates)
                        if (row.RootIndex == input.Root.Index && row.RootVersion == input.Root.Version) rebuildQuotedCost = row.RootConstructionCost;
                    Require(rebuildQuotedCost > 0, "REBUILD_PREFAB_COST_UNAVAILABLE");
                    return;
                }

                rejectedRebuildShapes.Add(shape);
                examined.Add(input.Root);
                bool remaining = false;
                foreach (var row in input.Candidates) remaining |= row.Eligible && EligibleAlternate(row);
                if (!remaining) break;
            }
            throw new Rejection("REBUILD_NATIVE_SHAPE_OUTSIDE_PROBE");

            bool EligibleAlternate(ObjectDefinitionInputProbe.CandidateStatistics row)
            {
                var prefab = new Entity { Index = row.RootIndex, Version = row.RootVersion };
                return prefab != commit.RootPrefab && !examined.Contains(prefab)
                    && row.RootSubNetCount == 1 && row.RootSubAreaCount == 0;
            }
        }

        private static JObject DescribeRebuildCourse(ObjectDefinitionPayload payload, Entity prefab)
        {
            var course = payload.Course;
            var start = course.m_StartPosition;
            var end = course.m_EndPosition;
            var curve = course.m_Curve;
            return new JObject {
                ["prefab"] = PreviewCommitGraph.Id(prefab), ["coursePrefab"] = PreviewCommitGraph.Id(payload.CourseCreation.m_Prefab),
                ["nativeSingleNodeInput"] = payload.IsDetachedSingleNodeCourse,
                ["courseLength"] = course.m_Length,
                ["start"] = Point(start.m_Position), ["end"] = Point(end.m_Position),
                ["startEntity"] = PreviewCommitGraph.Id(start.m_Entity), ["endEntity"] = PreviewCommitGraph.Id(end.m_Entity),
                ["startFlags"] = (uint)start.m_Flags, ["endFlags"] = (uint)end.m_Flags,
                ["curve"] = new JArray(Point(curve.a), Point(curve.b), Point(curve.c), Point(curve.d)),
                ["ownerPositionMatches"] = payload.CourseOwner.m_Position.Equals(payload.RootObject.m_Position),
                ["ownerRotationMatches"] = payload.CourseOwner.m_Rotation.Equals(payload.RootObject.m_Rotation),
                ["rootElevation"] = payload.RootObject.m_Elevation
            };

            static JArray Point(float3 value) => new JArray(value.x, value.y, value.z);
        }

        private void RunRelocation(PreviewCommitExecution submitted, bool submit)
        {
            var input = SelectWindInput(out var configuration);
            Require(input.Root == commit.RootPrefab, "RELOCATION_DEFINITION_PREFAB_CHANGED");
            try
            {
                var original = new RelocationDefinitionSource(source, submitted, input.Root, requireScope);
                var destination = GroundPreviewSite.Select(source, input.Root, requireScope, original.AcceptsSite);
                var definition = new ObjectDefinitionExecutionProbe();
                if (submit) moveDefinition = definition;
                else relocation = definition;
                definition.RunOnce(input, configuration.defaultTheme, configuration.leftHandTraffic, destination, default,
                    AgeMask.Sapling, requireScope, original);
                Require(definition.RelocationPassed, definition.FailureCode ?? "RELOCATION_DEFINITION_UNQUALIFIED");
                var generation = new RelocationGenerationExecution();
                if (submit) moveGeneration = generation;
                else relocationGeneration = generation;
                var definitions = definition.RequireRelocationPayload(source);
                generation.RunOnce(definitions, requireScope);
                Require(generation.Passed, generation.FailureCode ?? "RELOCATION_GENERATION_UNQUALIFIED");
                var preview = generation.TransferToPreview(definitions, requireScope);
                if (submit) move = preview;
                else relocationPreview = preview;
                preview.RunOnce(submit);
                Require(submit ? preview.CommitPublished : preview.Passed,
                    preview.FailureCode ?? "RELOCATION_PREVIEW_UNQUALIFIED");
            }
            catch (GroundPreviewSite.Rejection error) { throw new Rejection(error.Code); }
            catch (RelocationDefinitionSource.Rejection error) { throw new Rejection(error.Code); }
            catch (PreviewCommitExecution.Rejection error) { throw new Rejection(error.Code); }
        }

        private void RunBatch(Batch batch, Entity expectedComposition, bool submit = false)
        {
            requireScope();
            Require(batch.Definition == null, "COMPOSITION_BATCH_ALREADY_ATTEMPTED");
            var input = SelectWindInput(out var configuration);
            if (expectedComposition != Entity.Null)
                Require(input.Root == publish.RootPrefab, "COMPOSITION_REUSE_ROOT_PREFAB_CHANGED");
            try { site ??= GroundPreviewSite.Select(source, input.Root, requireScope); }
            catch (GroundPreviewSite.Rejection error) { throw new Rejection(error.Code); }
            RunDefinitionBatch(batch, input, configuration, site!, expectedComposition, submit,
                samePlacement: expectedComposition == Entity.Null ? null : publish.Definition);
        }

        private void RunDefinitionBatch(Batch batch, ObjectDefinitionInputProbe.Result input, CityConfigurationSystem configuration,
            GroundPreviewSite ground, Entity expectedComposition, bool submit, bool resolveComposition = false,
            ObjectDefinitionExecutionProbe? samePlacement = null, NewObjectPreviewEvidence? expectedPreview = null)
        {
            RunDefinition(batch, input, configuration, ground);
            if (samePlacement != null)
                Require(batch.Definition!.RequirePayload(source).Values.Matches(samePlacement.RequirePayload(source).Values),
                    "GROUND_PREVIEW_REUSE_DEFINITION_CHANGED");
            RunGenerationBatch(batch, expectedComposition, submit, resolveComposition, expectedPreview);
        }

        private void RunDefinition(Batch batch, ObjectDefinitionInputProbe.Result input, CityConfigurationSystem configuration,
            GroundPreviewSite ground)
        {
            requireScope();
            Require(batch.Definition == null, "COMPOSITION_BATCH_ALREADY_ATTEMPTED");
            batch.RootPrefab = input.Root;
            batch.Definition = new ObjectDefinitionExecutionProbe();
            batch.Definition.RunOnce(input, configuration.defaultTheme, configuration.leftHandTraffic, ground, default,
                AgeMask.Sapling, requireScope);
            Require(batch.Definition.ChecksPassed && batch.Definition.ResourcesReleased,
                batch.Definition.FailureCode ?? "OBJECT_EXECUTION_NOT_QUALIFIED");
        }

        private void RunGenerationBatch(Batch batch, Entity expectedComposition, bool submit, bool resolveComposition = false,
            NewObjectPreviewEvidence? expectedPreview = null)
        {
            requireScope();
            Require(batch.Generation == null && batch.Definition?.ResourcesReleased == true, "COMPOSITION_BATCH_DEFINITION_UNAVAILABLE");
            batch.Generation = new ObjectGenerationExecutionProbe();
            batch.Generation.RunOnce(source, batch.Definition!.RequirePayload(source), requireScope, expectedComposition,
                submit, resolveComposition, expectedPreview);
            Require(batch.Passed, batch.Generation.FailureCode ?? "OBJECT_GENERATION_LIFECYCLE_NOT_QUALIFIED");
            requireScope();
        }

        private ObjectDefinitionInputProbe.Result SelectWindInput(out CityConfigurationSystem configuration,
            Func<ObjectDefinitionInputProbe.CandidateStatistics, bool>? accepts = null)
        {
            var prefabs = source.GetExistingSystemManaged<PrefabSystem>()
                ?? throw new Rejection("OBJECT_EXECUTION_DEPENDENCY_UNAVAILABLE");
            configuration = source.GetExistingSystemManaged<CityConfigurationSystem>()
                ?? throw new Rejection("OBJECT_EXECUTION_DEPENDENCY_UNAVAILABLE");
            var input = ObjectDefinitionInputProbe.Select(source, prefabs, configuration, ObjectDefinitionInputProbe.Purpose.WindPower, accepts);
            Require(input.Complete && input.Root != Entity.Null && ReferenceEquals(input.Source, source), "OBJECT_EXECUTION_INPUT_UNAVAILABLE");
            return input;
        }

        private void RequirePublishedRow(CompositionSelectionReceipt receipt, Entity entity)
        {
            requireScope();
            Require(ReferenceEquals(receipt.Source, source) && receipt.SelectedRow.HasValue && receipt.SelectedRowIndex.HasValue,
                "COMPOSITION_REUSE_RECEIPT_MISSING");
            var manager = source.EntityManager;
            Require(manager.Exists(receipt.Prefab) && manager.HasBuffer<NetGeometryComposition>(receipt.Prefab),
                "COMPOSITION_REUSE_CACHE_UNAVAILABLE");
            var rows = manager.GetBuffer<NetGeometryComposition>(receipt.Prefab, true);
            int selected = receipt.SelectedRowIndex!.Value;
            Require(selected >= 0 && selected < rows.Length && rows.Length <= 256,
                "COMPOSITION_REUSE_CACHE_RANGE_CHANGED");
            var mask = receipt.SelectedRow!.Value.m_Mask;
            for (int index = 0; index < selected; index++)
                Require(rows[index].m_Mask != mask, "COMPOSITION_REUSE_FIRST_MATCH_CHANGED");
            Require(rows[selected].m_Composition == entity && rows[selected].m_Mask == mask,
                "COMPOSITION_REUSE_CACHE_ROW_CHANGED");
            requireScope();
        }

        internal void ReleaseInputSnapshot()
        {
            publish.Generation?.InitializationInputs?.ReleaseSnapshots();
            rebuildPreparation.Generation?.InitializationInputs?.ReleaseSnapshots();
        }
        internal JObject? DefinitionSnapshot() => publish.Definition?.Snapshot();
        internal JObject? GenerationSnapshot() => publish.Generation?.Snapshot();
        internal JObject Snapshot()
        {
            var receipt = reuse.Generation?.SelectionReceipt;
            return new JObject {
                ["publishFrame"] = Frame(publishFrame), ["observeFrame"] = Frame(observeFrame),
                ["dataInitAdvanced"] = dataInitAdvanced, ["meshRefAdvanced"] = meshRefAdvanced,
                ["readyBeforeReuse"] = readyBeforeReuse,
                ["site"] = (JToken?)site?.Snapshot() ?? JValue.CreateNull(),
                ["reuse"] = reuse.Generation == null ? JValue.CreateNull() : new JObject {
                    ["passed"] = reuse.Passed, ["resourcesReleased"] = reuse.ResourcesReleased,
                    ["node"] = Id(receipt?.Node ?? Entity.Null), ["entity"] = Id(receipt?.After.m_Composition ?? Entity.Null),
                    ["rowIndex"] = Frame(receipt?.SelectedRowIndex), ["nativeHit"] = receipt?.ObservedSelection ?? false,
                    ["geometry"] = (JToken?)reuse.Generation.NodeGeometrySnapshot() ?? JValue.CreateNull(),
                    ["followups"] = (JToken?)reuse.Generation.PreviewFollowupSnapshot() ?? JValue.CreateNull(),
                    ["livePreviewGraphChecks"] = reuse.Generation.LivePreviewGraphChecks
                },
                ["commit"] = commit.Generation == null ? JValue.CreateNull() : new JObject {
                    ["frame"] = Frame(commitFrame), ["passed"] = commit.Passed,
                    ["resourcesReleased"] = commit.ResourcesReleased, ["failureCode"] = commit.Generation.FailureCode,
                    ["native"] = (JToken?)commit.Generation.Commit?.Snapshot() ?? JValue.CreateNull()
                },
                ["relocation"] = relocation == null ? JValue.CreateNull() : new JObject {
                    ["frame"] = Frame(relocationFrame), ["definition"] = relocation.RelocationSnapshot()
                },
                ["relocationGeneration"] = relocationGeneration?.Snapshot(),
                ["relocationPreview"] = relocationPreview?.Snapshot(),
                ["relocationCommit"] = move?.CommitSnapshot(), ["rebuildVerified"] = rebuildVerified,
                ["rebuild"] = rebuild.Generation?.Commit?.Snapshot(),
                ["rebuildChoice"] = rebuildPreparationFrame == null ? null : new JObject {
                    ["prefab"] = PreviewCommitGraph.Id(rebuildPreparation.RootPrefab), ["quotedCost"] = rebuildQuotedCost,
                    ["selectionFrame"] = Frame(rebuildPreparationFrame), ["nativeCacheHit"] = rebuildCacheHit,
                    ["previewCost"] = rebuild.Generation?.ObservedPreviewEvidence?.Cost,
                    ["submittedPreviewIdentity"] = rebuild.Generation?.ObservedPreviewEvidence?.Snapshot(),
                    ["composition"] = PreviewCommitGraph.Id(rebuildComposition), ["compositionReady"] = rebuildCompositionReady,
                    ["selectedShape"] = selectedRebuildShape?.DeepClone(),
                    ["rejectedShapes"] = rejectedRebuildShapes.DeepClone(),
                    ["cancellation"] = rebuildCancellation?.DeepClone(),
                    ["generationFailure"] = (rebuild.Generation ?? rebuildPreview?.Generation ?? rebuildPreparation.Generation)?.FailureSnapshot(),
                    ["site"] = rebuildSite?.Snapshot(), ["position"] = rebuild.Definition == null ? null : new JArray(
                        rebuild.Definition.PlacementPosition.x, rebuild.Definition.PlacementPosition.y, rebuild.Definition.PlacementPosition.z)
                },
                ["survivesRetirement"] = survivesRetirement, ["passed"] = Passed
            };
        }
        private static JToken Frame(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };

        private sealed class Batch
        {
            internal Entity RootPrefab;
            internal ObjectDefinitionExecutionProbe? Definition;
            internal ObjectGenerationExecutionProbe? Generation;
            internal bool Finished => (Definition?.Finished ?? true) && (Generation?.Finished ?? true);
            internal bool ResourcesReleased => (Definition?.ResourcesReleased ?? true) && (Generation?.ResourcesReleased ?? true);
            internal bool NativeStateUncertain => Definition?.NativeWorkMayReadSource == true
                || Generation?.NativeWorkMayReadSource == true || Generation?.StructuralStateUnknown == true;
            internal bool Passed => Definition?.ChecksPassed == true && Generation?.ChecksPassed == true && ResourcesReleased;
        }

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
