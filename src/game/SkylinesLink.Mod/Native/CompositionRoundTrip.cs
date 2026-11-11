#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.City;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
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

        internal bool WorkersFinished => publish.Finished && reuse.Finished && commit.Finished && rebuild.Finished
            && relocation?.Finished != false && relocationGeneration?.Finished != false && relocationPreview?.Finished != false
            && moveDefinition?.Finished != false && moveGeneration?.Finished != false && move?.Finished != false;
        internal bool ResourcesReleased => publish.ResourcesReleased && reuse.ResourcesReleased && commit.ResourcesReleased && rebuild.ResourcesReleased
            && relocation?.ResourcesReleased != false && relocationGeneration?.ResourcesReleased != false
            && relocationPreview?.ResourcesReleased != false && moveDefinition?.ResourcesReleased != false
            && moveGeneration?.ResourcesReleased != false && move?.ResourcesReleased != false;
        internal bool NativeStateUncertain => publish.NativeStateUncertain || reuse.NativeStateUncertain || commit.NativeStateUncertain || rebuild.NativeStateUncertain
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
                RunBatch(rebuild, publish.Generation!.PublishedComposition, submit: true);
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
            Require(newlyBuilt.Root != oldFixture.Root && newlyBuilt.Node != oldFixture.Node
                && source.EntityManager.GetComponentData<Game.Objects.Transform>(newlyBuilt.Root).m_Position.Equals(commit.Definition!.PlacementPosition),
                "RELOCATION_PREVIEW_REBUILD_IDENTITY_OR_SITE_CHANGED");
            rebuildVerified = true;
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
            batch.RootPrefab = input.Root;
            try { site ??= GroundPreviewSite.Select(source, input.Root, requireScope); }
            catch (GroundPreviewSite.Rejection error) { throw new Rejection(error.Code); }
            batch.Definition = new ObjectDefinitionExecutionProbe();
            batch.Definition.RunOnce(input, configuration.defaultTheme, configuration.leftHandTraffic, site, default,
                AgeMask.Sapling, requireScope);
            Require(batch.Definition.ChecksPassed && batch.Definition.ResourcesReleased,
                batch.Definition.FailureCode ?? "OBJECT_EXECUTION_NOT_QUALIFIED");
            if (expectedComposition != Entity.Null)
                Require(batch.Definition.PlacementPosition.Equals(publish.Definition!.PlacementPosition)
                    && batch.Definition.PlacementOffsetY == publish.Definition.PlacementOffsetY,
                    "GROUND_PREVIEW_REUSE_POSITION_CHANGED");
            requireScope();
            batch.Generation = new ObjectGenerationExecutionProbe();
            batch.Generation.RunOnce(source, batch.Definition.RequirePayload(source), requireScope, expectedComposition, submit);
            Require(batch.Passed, batch.Generation.FailureCode ?? "OBJECT_GENERATION_LIFECYCLE_NOT_QUALIFIED");
            requireScope();
        }

        private ObjectDefinitionInputProbe.Result SelectWindInput(out CityConfigurationSystem configuration)
        {
            var prefabs = source.GetExistingSystemManaged<PrefabSystem>()
                ?? throw new Rejection("OBJECT_EXECUTION_DEPENDENCY_UNAVAILABLE");
            configuration = source.GetExistingSystemManaged<CityConfigurationSystem>()
                ?? throw new Rejection("OBJECT_EXECUTION_DEPENDENCY_UNAVAILABLE");
            var input = ObjectDefinitionInputProbe.Select(source, prefabs, configuration, ObjectDefinitionInputProbe.Purpose.WindPower);
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

        internal void ReleaseInputSnapshot() => publish.Generation?.InitializationInputs?.ReleaseSnapshots();
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
        internal sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }
    }
}
#endif
