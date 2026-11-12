#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Colossal.Core;
using Game;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Node = Game.Net.Node;
using SubObject = Game.Objects.SubObject;
using PrefabSubObject = Game.Prefabs.SubObject;
using AgeMask = Game.Tools.AgeMask;

namespace SkylinesLink.Mod.Native
{
    // First concurrency prerequisite only: one real default-pipeline external
    // preview. No AI preview runs beside it yet and no building is committed.
    internal sealed class ParallelPreviewProbe
    {
        private const int FrameLimit = 180, HoldFrames = 3, MaximumRows = 8;
        private enum Stage { NotStarted, Emit, DefinitionsQueued, AwaitPreview, Hold, Clear, AwaitAbsence, Yield, Released }
        private readonly World source;
        private readonly UpdateSystem updates;
        private readonly Action requireScope;
        private readonly ObjectDefinitionExecutionProbe definition = new ObjectDefinitionExecutionProbe();
        private ParallelPreviewUpdateGroup? group;
        private NativeSystemRegistration<ExternalPreviewTool>? actorOwner;
        private ToolSystem? tools;
        private DefaultToolSystem? defaultTool;
        private CitySystem? city;
        private Entity selected;
        private int selectedIndex;
        private ToolBaseSystem[] originalTools = Array.Empty<ToolBaseSystem>();
        private ComponentSystemBase[] originalSystems = Array.Empty<ComponentSystemBase>();
        private ObjectDefinitionPayload? expected;
        private GroundPreviewSite? site;
        private Entity theme;
        private bool leftHandTraffic;
        private ControlPoint point;
        private EntityQuery definitions, visibleTemps, objectInputs, nodeInputs;
        private bool definitionsCreated, tempsCreated, actorRemoved, definitionsConfirmed, previewConfirmed, holdConfirmed;
        private bool prefabExpansionQualified;
        private bool cancellationConfirmed, clearAdvanced, stopRequested, groupDetached;
        private bool actorActivated, yieldRequested, nativeResultUnresolved;
        private bool actorRetireAttempted;
        private bool cleanupDeadlineReached;
        private int cleanupStartedFrame = -1;
        private int startedFrame, emittedFrame, observedFrame, lastHoldFrame, heldFrames, maximumMembers;
        private int nativeTypesDeclared, nativeMethodsDeclared;
        private string? patchFailureType;
        private int? moneyBefore, moneyBeforeClear, moneyAfter;
        private uint applyVersion, clearVersion, validationVersion, componentsVersion;
        private Entity root, node;
        private Entity[] definitionIds = Array.Empty<Entity>(), members = Array.Empty<Entity>();
        private Entity[] rootChildren = Array.Empty<Entity>(), nodeChildren = Array.Empty<Entity>();
        private JObject? inputReceipt, graphReceipt;
        private Stage stage;
        private string? failureCode, exceptionType;
        private DefinitionProducerPatchProbe patch = new DefinitionProducerPatchProbe();
        private DefinitionProducerPatchProbe? latestPatchObservation;

        internal ParallelPreviewProbe(World source, UpdateSystem updates, Action requireScope)
        { this.source = source; this.updates = updates; this.requireScope = requireScope; }

        internal bool NativeStateUncertain => actorOwner?.Instance.NativeWorkMayReadSource == true || definition.NativeWorkMayReadSource;
        internal bool CleanupPending => stage != Stage.Released && actorOwner != null && !actorRemoved && !NativeStateUncertain;
        internal bool ResourcesReleased => stage == Stage.Released && actorRemoved && groupDetached
            && !definitionsCreated && !tempsCreated && definition.ResourcesReleased && !NativeStateUncertain
            && (actorOwner?.Instance.InputStatesReleased ?? true)
            && (group?.OwnershipConfirmed ?? true);
        internal bool WorkersFinished => !NativeStateUncertain && (stage == Stage.Released || failureCode != null);
        internal bool Passed => ResourcesReleased && failureCode == null && definitionsConfirmed && previewConfirmed
            && holdConfirmed && cancellationConfirmed && clearAdvanced;
        internal string? FailureCode => failureCode;

        internal void Advance()
        {
            Require(MainThreadDispatcher.isMainThread, "PARALLEL_PREVIEW_MAIN_THREAD_REQUIRED");
            if (stage == Stage.Released) return;
            try
            {
                if (cleanupStartedFrame >= 0 && !NativeStateUncertain
                    && UnityEngine.Time.frameCount - cleanupStartedFrame > FrameLimit)
                {
                    FinishUnconfirmedCleanup();
                    return;
                }
                if (failureCode != null || stopRequested && actorOwner == null)
                {
                    AdvanceFailureStop();
                    return;
                }
                requireScope();
                if (stage == Stage.NotStarted) Start();
                if (actorOwner == null) return;
                if (stage != Stage.Yield && stage != Stage.Released && actorActivated
                    && !ReferenceEquals(tools!.activeTool, actorOwner.Instance))
                {
                    nativeResultUnresolved |= !cancellationConfirmed;
                    throw new Rejection("PARALLEL_PREVIEW_TOOL_TAKEN_OVER");
                }
                if (stage != Stage.Yield && UnityEngine.Time.frameCount - startedFrame > FrameLimit)
                    throw new Rejection("PARALLEL_PREVIEW_FRAME_LIMIT");
                if (stopRequested && stage == Stage.Hold && previewConfirmed) stage = Stage.Clear;
                if (stage == Stage.Yield) RetireActor();
            }
            catch (Exception error) { RecordFailure(error); }
        }

        internal void RequestStop()
        {
            stopRequested = true;
            BeginCleanupDeadline();
            // Stopping does not invent permission to clear an unproved cohort.
            if (stage == Stage.Hold && previewConfirmed) stage = Stage.Clear;
            // Start may have acquired a group and queries before actor creation.
            // Advance owns their cleanup even while the stage is NotStarted.
        }

        private void Start()
        {
            Require(ReferenceEquals(updates.World, source) && source.IsCreated && updates.currentPhase == SystemUpdatePhase.Invalid
                && typeof(ToolSystem).Module.ModuleVersionId.ToString("N") == "46198354d4f94d23bf327e3c085d8b92"
                && NativeConstructionBuild.Game(typeof(ToolSystem).Module), "PARALLEL_PREVIEW_BUILD_OR_PHASE_UNQUALIFIED");
            startedFrame = UnityEngine.Time.frameCount;
            tools = Existing<ToolSystem>(); defaultTool = Existing<DefaultToolSystem>(); city = Existing<CitySystem>();
            Require(ReferenceEquals(tools.activeTool, defaultTool) && tools.actionMode == GameMode.Game && !tools.ignoreErrors,
                "PARALLEL_PREVIEW_DEFAULT_TOOL_REQUIRED");
            selected = tools.selected; selectedIndex = tools.selectedIndex; originalTools = tools.tools.ToArray();
            group = new ParallelPreviewUpdateGroup();
            group.Register(updates);
            originalSystems = ReadSystems(); // Includes the retained World anchor.
            definitions = source.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CreationDefinition>());
            definitionsCreated = true;
            visibleTemps = source.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>());
            tempsCreated = true;
            objectInputs = DefaultInput(Existing<GenerateObjectsSystem>());
            nodeInputs = DefaultInput(Existing<GenerateNodesSystem>());
            RequireEmptyInput();
            var prefabs = Existing<PrefabSystem>();
            var config = Existing<CityConfigurationSystem>();
            var input = ObjectDefinitionInputProbe.Select(source, prefabs, config, ObjectDefinitionInputProbe.Purpose.WindPower,
                row => row.RootConstructionCost == 25000 && row.LotWidth == 3 && row.LotDepth == 3
                    && row.RootSubNetCount == 1 && row.RootSubAreaCount == 0);
            Require(input.Complete && input.Root != Entity.Null && ReferenceEquals(input.Source, source),
                "PARALLEL_PREVIEW_WIND_INPUT_UNAVAILABLE");
            site = GroundPreviewSite.Select(source, input.Root, requireScope);
            theme = config.defaultTheme; leftHandTraffic = config.leftHandTraffic;
            definition.RunOnce(input, theme, leftHandTraffic, site, default, AgeMask.Sapling, requireScope);
            Require(definition.ChecksPassed && definition.ResourcesReleased, definition.FailureCode ?? "PARALLEL_DEFINITION_UNQUALIFIED");
            expected = definition.RequirePayload(source);
            Require(expected.IsDetachedSingleNodeCourse && expected.RootCreation.m_Original == Entity.Null
                && expected.CourseCreation.m_Original == Entity.Null && expected.RootCreation.m_Owner == Entity.Null
                && expected.CourseCreation.m_Owner == Entity.Null, "PARALLEL_PREVIEW_NEW_ONLY_REQUIRED");
            rootChildren = ReadDeclaredChildren(expected.RootCreation.m_Prefab, 4);
            nodeChildren = ReadDeclaredChildren(expected.CourseCreation.m_Prefab, 1);
            maximumMembers = 2 + rootChildren.Length + nodeChildren.Length;
            RequirePrefabGenerationClosure();
            point = site.RequireCurrent(source, input.Root, requireScope, out _);
            patch = ObservePipeline(); Require(patch.Qualified, patch.Reason);
            applyVersion = Existing<ToolApplySystem>().CheckedStateRef.LastSystemVersion;
            validationVersion = Existing<ValidationSystem>().CheckedStateRef.LastSystemVersion;
            componentsVersion = Existing<ValidationSystem.Components>().CheckedStateRef.LastSystemVersion;
            moneyBefore = ReadMoney();
            var actor = new ExternalPreviewTool();
            actorOwner = new NativeSystemRegistration<ExternalPreviewTool>(source, actor);
            actorOwner.Add();
            Require(actor.CreationReturned, "EXTERNAL_TOOL_CREATE_UNCONFIRMED");
            actor.Configure(this, prefabs.GetPrefab<PrefabBase>(input.Root));
            group.Attach(actor);
            RequireEmptyInput();
            Require(tools.tools.Count == originalTools.Length + 1 && ReferenceEquals(tools.tools[tools.tools.Count - 1], actor),
                "EXTERNAL_TOOL_LIST_REGISTRATION_UNCONFIRMED");
            RequireSelection();
            stage = Stage.Emit;
            tools.activeTool = actor;
            actorActivated = true;
            tools.RequireFullUpdate();
        }

        internal JobHandle OnNativePhase(ExternalPreviewTool actor, JobHandle inputDeps)
        {
            if (stage == Stage.Released) return inputDeps;
            requireScope();
            Require(actorOwner != null && ReferenceEquals(actorOwner.Instance, actor) && ReferenceEquals(tools!.activeTool, actor)
                && actor.World == source, "EXTERNAL_TOOL_PHASE_OWNER_CHANGED");
            RequireSelection();
            switch (updates.currentPhase)
            {
                case SystemUpdatePhase.ToolUpdate:
                    actor.Hold();
                    if (stage == Stage.Emit)
                    {
                        Require(!stopRequested, "PARALLEL_PREVIEW_STOPPED_BEFORE_EMISSION");
                        RequireEmptyInput(); RequirePipeline();
                        RequireCurrentPrefabInput();
                        var admission = new ObjectGenerationPreflight();
                        admission.Run(source, expected!, requireScope);
                        Require(admission.Passed && admission.ResourcesReleased, admission.FailureCode ?? "EXTERNAL_GENERATION_ADMISSION_FAILED");
                        point = site!.RequireCurrent(source, expected!.RootCreation.m_Prefab, requireScope, out _);
                        Require(point.m_Position.Equals(expected.RootObject.m_Position)
                            && point.m_Rotation.Equals(expected.RootObject.m_Rotation), "EXTERNAL_PREVIEW_GROUND_POSE_CHANGED");
                        emittedFrame = UnityEngine.Time.frameCount;
                        var result = actor.Emit(expected, point, theme, leftHandTraffic, inputDeps);
                        stage = Stage.DefinitionsQueued;
                        return result;
                    }
                    if (stage == Stage.Clear)
                    {
                        Require(previewConfirmed && definitionsConfirmed && !nativeResultUnresolved, "EXTERNAL_CLEAR_COHORT_UNPROVED");
                        RequirePipeline();
                        ReadGraph(requireStable: true);
                        Require(ReadMoney() == moneyBefore, "EXTERNAL_PREVIEW_FUNDS_CHANGED");
                        RequireDefinitionSet(requireUpdated: false);
                        moneyBeforeClear = ReadMoney();
                        clearVersion = Existing<ToolClearSystem>().CheckedStateRef.LastSystemVersion;
                        var result = actor.ClearDefinitions(definitions, inputDeps);
                        stage = Stage.AwaitAbsence;
                        return result;
                    }
                    break;
                case SystemUpdatePhase.Modification1:
                    if (stage == Stage.DefinitionsQueued)
                    {
                        Require(UnityEngine.Time.frameCount == emittedFrame && visibleTemps.CalculateEntityCount() == 0,
                            "EXTERNAL_DEFINITION_PUBLICATION_WINDOW_CHANGED");
                        RequireDefinitionSet(requireUpdated: true);
                        definitionsConfirmed = true;
                        stage = Stage.AwaitPreview;
                    }
                    break;
                case SystemUpdatePhase.ModificationEnd:
                    ObserveDefaultOutput(actor);
                    break;
            }
            return inputDeps;
        }

        private void ObserveDefaultOutput(ExternalPreviewTool actor)
        {
            if (stage == Stage.AwaitPreview || stage == Stage.Hold)
            {
                Require(definitionsConfirmed && UnityEngine.Time.frameCount >= emittedFrame,
                    "EXTERNAL_PREVIEW_CAUSAL_INPUT_MISSING");
                Existing<GenerateObjectsSystem>().CheckedStateRef.CompleteDependency();
                Existing<GenerateNodesSystem>().CheckedStateRef.CompleteDependency();
                Existing<ValidationSystem>().CheckedStateRef.CompleteDependency();
                Existing<ValidationSystem.Components>().CheckedStateRef.CompleteDependency();
                if (Existing<ValidationSystem>().CheckedStateRef.LastSystemVersion == validationVersion
                    || Existing<ValidationSystem.Components>().CheckedStateRef.LastSystemVersion == componentsVersion) return;
                // The two inputs may remain after Updated expires; no new
                // default producer input is allowed during the holding window.
                Require(objectInputs.CalculateEntityCount() == 0 || UnityEngine.Time.frameCount == emittedFrame,
                    "EXTERNAL_PREVIEW_ADDITIONAL_OBJECT_INPUT");
                Require(nodeInputs.CalculateEntityCount() == 0 || UnityEngine.Time.frameCount == emittedFrame,
                    "EXTERNAL_PREVIEW_ADDITIONAL_NODE_INPUT");
                if (!previewConfirmed && !GraphReady()) return;
                var observed = ReadGraph(requireStable: previewConfirmed);
                Require(actor.AllowApply(), "EXTERNAL_PREVIEW_NATIVE_QUALIFICATION_REJECTED");
                Require(ReadMoney() == moneyBefore, "EXTERNAL_PREVIEW_FUNDS_CHANGED");
                Require(Existing<ToolApplySystem>().CheckedStateRef.LastSystemVersion == applyVersion,
                    "EXTERNAL_PREVIEW_UNEXPECTED_APPLY_PHASE");
                if (!previewConfirmed)
                {
                    graphReceipt = observed; previewConfirmed = true; observedFrame = UnityEngine.Time.frameCount;
                    lastHoldFrame = observedFrame; stage = Stage.Hold;
                }
                else if (UnityEngine.Time.frameCount != lastHoldFrame)
                {
                    lastHoldFrame = UnityEngine.Time.frameCount;
                    heldFrames++;
                    if (heldFrames >= HoldFrames)
                    { holdConfirmed = true; stage = Stage.Clear; BeginCleanupDeadline(); }
                }
            }
            else if (stage == Stage.AwaitAbsence)
            {
                Existing<ToolClearSystem>().CheckedStateRef.CompleteDependency();
                clearAdvanced |= Existing<ToolClearSystem>().CheckedStateRef.LastSystemVersion != clearVersion;
                if (visibleTemps.CalculateEntityCount() != 0 || definitions.CalculateEntityCount() != 0) return;
                foreach (Entity entity in members) if (source.EntityManager.Exists(entity)) return;
                foreach (Entity entity in definitionIds) if (source.EntityManager.Exists(entity)) return;
                moneyAfter = ReadMoney();
                Require(clearAdvanced && moneyAfter == moneyBeforeClear && moneyAfter == moneyBefore
                    && Existing<ToolApplySystem>().CheckedStateRef.LastSystemVersion == applyVersion,
                    "EXTERNAL_PREVIEW_CANCELLATION_UNCONFIRMED");
                cancellationConfirmed = true;
                stage = Stage.Yield;
                BeginCleanupDeadline();
            }
        }

        private void RequireDefinitionSet(bool requireUpdated)
        {
            Require(definitions.CalculateEntityCount() == 2, "EXTERNAL_DEFINITION_COMPLETE_SET_CHANGED");
            using var ids = definitions.ToEntityArray(Allocator.Temp);
            Require(ids.Length == 2, "EXTERNAL_DEFINITION_SET_CHANGED");
            var entities = source.EntityManager;
            Entity rootDefinition = Entity.Null, courseDefinition = Entity.Null;
            foreach (Entity entity in ids)
            {
                Require(!entities.HasComponent<Disabled>(entity) && !entities.HasComponent<Deleted>(entity)
                    && entities.HasComponent<Updated>(entity) == requireUpdated, "EXTERNAL_DEFINITION_STATE_CHANGED");
                var creation = entities.GetComponentData<CreationDefinition>(entity);
                if (entities.HasComponent<ObjectDefinition>(entity))
                {
                    Require(rootDefinition == Entity.Null && creation.Equals(expected!.RootCreation)
                        && entities.GetComponentData<ObjectDefinition>(entity).Equals(expected.RootObject)
                        && !entities.HasComponent<NetCourse>(entity), "EXTERNAL_ROOT_DEFINITION_CHANGED");
                    rootDefinition = entity;
                }
                else
                {
                    Require(courseDefinition == Entity.Null && entities.HasComponent<NetCourse>(entity)
                        && entities.HasComponent<OwnerDefinition>(entity) && creation.Equals(expected!.CourseCreation)
                        && entities.GetComponentData<OwnerDefinition>(entity).Equals(expected.CourseOwner)
                        && entities.GetComponentData<NetCourse>(entity).Equals(expected.Course), "EXTERNAL_COURSE_DEFINITION_CHANGED");
                    courseDefinition = entity;
                }
            }
            Require(rootDefinition != Entity.Null && courseDefinition != Entity.Null
                && (!definitionsConfirmed || definitionIds[0] == rootDefinition && definitionIds[1] == courseDefinition),
                "EXTERNAL_DEFINITION_IDENTITIES_CHANGED");
            if (requireUpdated)
            {
                Require(objectInputs.CalculateEntityCount() == 2 && nodeInputs.CalculateEntityCount() == 2,
                    "EXTERNAL_DEFAULT_INPUT_QUERY_CHANGED");
                RequireExact(objectInputs, rootDefinition, courseDefinition);
                RequireExact(nodeInputs, rootDefinition, courseDefinition);
                definitionIds = new[] { rootDefinition, courseDefinition };
                inputReceipt = new JObject {
                    ["root"] = Id(rootDefinition), ["course"] = Id(courseDefinition),
                    ["frame"] = UnityEngine.Time.frameCount, ["completeDefaultInputs"] = 2,
                    ["nativeValuesEqual"] = true, ["singleProducer"] = true
                };
            }
        }

        private JObject ReadGraph(bool requireStable)
        {
            int count = visibleTemps.CalculateEntityCount();
            Require(count == maximumMembers && count <= 2 + 2 * MaximumRows, "EXTERNAL_PREVIEW_MEMBER_BOUND_CHANGED");
            using var ids = visibleTemps.ToEntityArray(Allocator.Temp);
            Require(ids.Length == count, "EXTERNAL_PREVIEW_MEMBER_SET_CHANGED");
            var actual = ids.ToArray();
            Array.Sort(actual, (a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Version.CompareTo(b.Version));
            var entities = source.EntityManager;
            Entity foundRoot = Entity.Null, foundNode = Entity.Null;
            foreach (Entity entity in actual)
            {
                Require(entities.HasComponent<PrefabRef>(entity) && !entities.HasComponent<Deleted>(entity)
                    && !entities.HasComponent<Disabled>(entity) && !entities.HasComponent<Game.Common.Native>(entity)
                    && !entities.HasComponent<Game.Net.Edge>(entity) && !entities.HasComponent<Game.Net.Lane>(entity)
                    && !entities.HasComponent<Game.Areas.Area>(entity) && !entities.HasComponent<Attached>(entity)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(entity), "EXTERNAL_PREVIEW_UNQUALIFIED_MEMBER");
                Temp temp = entities.GetComponentData<Temp>(entity);
                Require(temp.m_Original == Entity.Null && temp.m_Cost >= 0, "EXTERNAL_PREVIEW_ORIGINAL_OR_COST_CHANGED");
                RequireEmptyBuffer<Game.Net.ConnectedEdge>(entity);
                RequireEmptyBuffer<Game.Net.SubLane>(entity);
                RequireEmptyBuffer<Game.Areas.SubArea>(entity);
                if (entities.HasComponent<Game.Buildings.Building>(entity))
                { Require(foundRoot == Entity.Null, "EXTERNAL_PREVIEW_MULTIPLE_ROOTS"); foundRoot = entity; }
                if (entities.HasComponent<Node>(entity))
                { Require(foundNode == Entity.Null, "EXTERNAL_PREVIEW_MULTIPLE_NODES"); foundNode = entity; }
            }
            Require(foundRoot != Entity.Null && foundNode != Entity.Null && foundRoot != foundNode
                && entities.GetComponentData<PrefabRef>(foundRoot).m_Prefab == expected!.RootCreation.m_Prefab
                && entities.GetComponentData<PrefabRef>(foundNode).m_Prefab == expected.CourseCreation.m_Prefab
                && !entities.HasComponent<Owner>(foundRoot) && entities.HasComponent<Owner>(foundNode)
                && entities.GetComponentData<Owner>(foundNode).m_Owner == foundRoot, "EXTERNAL_PREVIEW_PARENT_ROLES_CHANGED");
            var nets = entities.GetBuffer<Game.Net.SubNet>(foundRoot, true);
            Require(nets.Length == 1 && nets[0].m_SubNet == foundNode, "EXTERNAL_PREVIEW_SUBNET_CHANGED");
            RequireEmptyBuffer<Game.Net.SubNet>(foundNode);
            RequireChildren(foundRoot, rootChildren, actual);
            RequireChildren(foundNode, nodeChildren, actual);
            var rows = new JArray();
            int totalCost = 0;
            foreach (Entity entity in actual)
            {
                Temp temp = entities.GetComponentData<Temp>(entity);
                TempFlags flags = entity == foundRoot ? TempFlags.Create | TempFlags.Essential
                    : entity == foundNode ? TempFlags.Create | TempFlags.IsLast : TempFlags.Create;
                Require(temp.m_Flags == flags, "EXTERNAL_PREVIEW_TEMP_FLAGS_CHANGED");
                checked { totalCost += temp.m_Cost; }
                Entity owner = entities.HasComponent<Owner>(entity) ? entities.GetComponentData<Owner>(entity).m_Owner : Entity.Null;
                if (entity != foundRoot && entity != foundNode)
                {
                    Require((owner == foundRoot || owner == foundNode) && entities.HasComponent<Transform>(entity),
                        "EXTERNAL_PREVIEW_CHILD_OWNER_CHANGED");
                    RequireEmptyBuffer<SubObject>(entity); RequireEmptyBuffer<Game.Net.SubNet>(entity);
                }
                float3 position; quaternion rotation;
                if (entity == foundNode)
                { var pose = entities.GetComponentData<Node>(entity); position = pose.m_Position; rotation = pose.m_Rotation; }
                else
                { var pose = entities.GetComponentData<Transform>(entity); position = pose.m_Position; rotation = pose.m_Rotation; }
                Require(math.all(math.isfinite(position)) && math.all(math.isfinite(rotation.value)), "EXTERNAL_PREVIEW_POSE_INVALID");
                rows.Add(new JObject {
                    ["entity"] = Id(entity), ["prefab"] = Id(entities.GetComponentData<PrefabRef>(entity).m_Prefab),
                    ["owner"] = Id(owner), ["flags"] = (uint)temp.m_Flags, ["cost"] = temp.m_Cost, ["value"] = temp.m_Value,
                    ["position"] = new JArray(position.x, position.y, position.z),
                    ["rotation"] = new JArray(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w)
                });
            }
            Require(totalCost == 25000, "EXTERNAL_PREVIEW_QUOTED_COST_CHANGED");
            var receipt = new JObject { ["root"] = Id(foundRoot), ["node"] = Id(foundNode), ["cost"] = totalCost, ["members"] = rows };
            if (requireStable)
                Require(root == foundRoot && node == foundNode && JToken.DeepEquals(graphReceipt, receipt), "EXTERNAL_PREVIEW_GRAPH_CHANGED_WHILE_HELD");
            else { root = foundRoot; node = foundNode; members = actual; }
            return receipt;
        }

        private void RequireChildren(Entity parent, Entity[] prefabs, Entity[] all)
        {
            var entries = source.EntityManager.GetBuffer<SubObject>(parent, true);
            Require(entries.Length == prefabs.Length, "EXTERNAL_PREVIEW_CHILD_COUNT_CHANGED");
            var remaining = new List<Entity>(prefabs);
            var seen = new HashSet<Entity>();
            foreach (SubObject entry in entries)
            {
                Entity child = entry.m_SubObject;
                Require(Array.IndexOf(all, child) >= 0 && seen.Add(child) && source.EntityManager.HasComponent<Owner>(child)
                    && source.EntityManager.GetComponentData<Owner>(child).m_Owner == parent,
                    "EXTERNAL_PREVIEW_CHILD_REFERENCE_CHANGED");
                Require(remaining.Remove(source.EntityManager.GetComponentData<PrefabRef>(child).m_Prefab),
                    "EXTERNAL_PREVIEW_CHILD_PREFAB_CHANGED");
            }
            Require(remaining.Count == 0, "EXTERNAL_PREVIEW_DECLARED_CHILD_MISSING");
        }

        private Entity[] ReadDeclaredChildren(Entity prefab, int expectedCount)
        {
            var entries = source.EntityManager.GetBuffer<PrefabSubObject>(prefab, true);
            Require(entries.Length == expectedCount && entries.Length <= MaximumRows, "EXTERNAL_PREVIEW_PREFAB_ROW_BOUND_CHANGED");
            var result = new Entity[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var row = entries[i];
                Require(row.m_Prefab != Entity.Null && (uint)row.m_Flags == 0 && row.m_ParentIndex == 0
                    && row.m_GroupIndex == 0 && row.m_Probability == 100, "EXTERNAL_PREVIEW_PREFAB_ROW_BRANCH_CHANGED");
                result[i] = row.m_Prefab;
            }
            return result;
        }

        private void RetireActor()
        {
            if (NativeStateUncertain || actorOwner == null || group == null) return;
            var actor = actorOwner.Instance;
            if (!yieldRequested)
            {
                Require(cancellationConfirmed || failureCode != null, "EXTERNAL_TOOL_YIELD_UNQUALIFIED");
                if (ReferenceEquals(tools!.activeTool, actor))
                {
                    // Yield this fixture only; never restore over a player tool.
                    tools.activeTool = defaultTool!;
                    tools.RequireFullUpdate();
                }
                yieldRequested = true;
                return;
            }
            // ToolSystem still holds its previous tool until its next real
            // ToolUpdate. Observe OnStopRunning before removing that instance.
            if (actorActivated && (!actor.Stopped || actor.Enabled)) return;
            Require(!ReferenceEquals(tools!.activeTool, actor) && updates.currentPhase == SystemUpdatePhase.Invalid,
                "EXTERNAL_TOOL_RETIRE_PHASE_UNQUALIFIED");
            if (actorRetireAttempted)
            {
                // A failed destroy/dispose is not replayed. End this helper's
                // pump without claiming resource release or blocking normal
                // game exit when no job can still read the source.
                DisposeQueries(); stage = Stage.Released;
                return;
            }
            actorRetireAttempted = true;
            if (!groupDetached) { group.Detach(actor); groupDetached = true; }
            try
            {
                RequirePipeline();
                if (!actor.PartialCleanupReturned) actor.ReleaseInputStates();
            }
            catch (Exception error) { RecordFailure(error); }
            if (!actorOwner.DestroyAttempted) actorOwner.Destroy();
            actorRemoved = actorOwner.PhysicalReleased && (actor.DestructionReturned || actor.PartialCleanupReturned) && actor.ParametersReleased;
            Require(actorRemoved, "EXTERNAL_TOOL_RETIRE_UNCONFIRMED");
            if (!SameTools() || !SameSystems()) failureCode ??= "EXTERNAL_TOOL_SOURCE_REGISTRY_CHANGED";
            if (tools.selected != selected || tools.selectedIndex != selectedIndex)
                failureCode ??= "EXTERNAL_FIXTURE_SELECTION_CHANGED";
            if (failureCode == null)
                Require(visibleTemps.CalculateEntityCount() == 0 && definitions.CalculateEntityCount() == 0,
                    "EXTERNAL_TOOL_OUTPUT_REMAINED_AFTER_CANCELLATION");
            if (!actor.InputStatesReleased) failureCode ??= "EXTERNAL_INPUT_RELEASE_UNCONFIRMED";
            DisposeQueries();
            stage = Stage.Released;
        }

        internal void RecordFailure(Exception error)
        {
            failureCode ??= NativeProbeFailure.CodeOf(error) ?? "PARALLEL_PREVIEW_NATIVE_FAILED";
            string type = error.GetType().FullName ?? error.GetType().Name;
            exceptionType ??= type.Length <= 160 ? type : type.Substring(0, 160);
            stopRequested = true;
            BeginCleanupDeadline();
            // A previously proved held cohort may still use normal cancellation.
            if (stage == Stage.Hold && previewConfirmed && !NativeStateUncertain) stage = Stage.Clear;
            else if (stage == Stage.Clear)
            {
                // Failure while rechecking the cancellation closure does not
                // authorize a broader clear. Default-pipeline output stays with
                // the World and is reported unconfirmed, never manually deleted.
                nativeResultUnresolved = true;
                stage = Stage.Yield;
            }
        }

        private void AdvanceFailureStop()
        {
            if (NativeStateUncertain) return;
            if (actorOwner == null)
            {
                if (group?.WorldRegistered == true)
                { group.Enabled = false; groupDetached = group.ManagedSystems.Count == 0; }
                else groupDetached = true;
                DisposeQueries(); actorRemoved = true; stage = Stage.Released;
                return;
            }
            if (stage == Stage.Clear || stage == Stage.AwaitAbsence) return;
            nativeResultUnresolved |= actorOwner.Instance.NativeStarted && !cancellationConfirmed;
            stage = Stage.Yield;
            RetireActor();
        }

        private void BeginCleanupDeadline()
        {
            if (cleanupStartedFrame < 0) cleanupStartedFrame = UnityEngine.Time.frameCount;
        }

        private void FinishUnconfirmedCleanup()
        {
            // No native reader remains. Stop pumping this experiment after a
            // bounded cleanup attempt; incomplete input/actor retirement still
            // fails ResourcesReleased and never becomes an exit prerequisite.
            cleanupDeadlineReached = true;
            failureCode ??= "PARALLEL_PREVIEW_CLEANUP_UNCONFIRMED";
            nativeResultUnresolved |= actorOwner?.Instance.NativeStarted == true && !cancellationConfirmed;
            try
            {
                if (actorOwner != null && ReferenceEquals(tools?.activeTool, actorOwner.Instance))
                {
                    tools!.activeTool = defaultTool!;
                    tools.RequireFullUpdate();
                }
                if (group?.WorldRegistered == true)
                {
                    group.Enabled = false;
                    if (actorOwner != null && !groupDetached) group.Detach(actorOwner.Instance);
                    groupDetached = group.ManagedSystems.Count == 0;
                }
            }
            catch (Exception error) { RecordFailure(error); }
            try { DisposeQueries(); }
            catch (Exception error) { RecordFailure(error); }
            stage = Stage.Released;
        }

        private void DisposeQueries()
        {
            if (definitionsCreated) { definitions.Dispose(); definitionsCreated = false; }
            if (tempsCreated) { visibleTemps.Dispose(); tempsCreated = false; }
        }

        private void RequireCurrentPrefabInput()
        {
            var config = Existing<CityConfigurationSystem>();
            Entity rootPrefab = expected!.RootCreation.m_Prefab;
            var input = ObjectDefinitionInputProbe.Select(source, Existing<PrefabSystem>(), config,
                ObjectDefinitionInputProbe.Purpose.WindPower,
                row => row.RootIndex == rootPrefab.Index && row.RootVersion == rootPrefab.Version
                    && row.RootConstructionCost == 25000 && row.LotWidth == 3 && row.LotDepth == 3
                    && row.RootSubNetCount == 1 && row.RootSubAreaCount == 0);
            Require(input.Complete && input.Root == rootPrefab && config.defaultTheme == theme
                && config.leftHandTraffic == leftHandTraffic, "EXTERNAL_PREVIEW_PREFAB_INPUT_CHANGED");
            RequireSameRows(rootChildren, ReadDeclaredChildren(rootPrefab, 4));
            RequireSameRows(nodeChildren, ReadDeclaredChildren(expected.CourseCreation.m_Prefab, 1));
            RequirePrefabGenerationClosure();
        }

        private void RequirePrefabGenerationClosure()
        {
            prefabExpansionQualified = false;
            var entities = source.EntityManager;
            Entity rootPrefab = expected!.RootCreation.m_Prefab;
            Entity nodePrefab = expected.CourseCreation.m_Prefab;
            RequireEmptyBuffer<Game.Prefabs.SubLane>(rootPrefab);
            RequireEmptyBuffer<Game.Prefabs.SubLane>(nodePrefab);
            RequireEmptyBuffer<Game.Prefabs.SubNet>(nodePrefab);
            RequireEmptyBuffer<Game.Prefabs.SubArea>(nodePrefab);
            RequireEmptyBuffer<Game.Prefabs.SubArea>(rootPrefab);
            foreach (Entity prefab in rootChildren) RequireLeafDeclaration(prefab);
            foreach (Entity prefab in nodeChildren) RequireLeafDeclaration(prefab);
            PreviewLaneProducer.RequireStandaloneUtilityDefaults(entities, nodePrefab);
            // This detached course has one owner and no connected edges. The
            // qualified utility rows therefore have no second connection per
            // order; object-declared lanes and recursive children are excluded.
            // The real default output still must satisfy the complete graph.
            prefabExpansionQualified = true;
        }

        private void RequireLeafDeclaration(Entity prefab)
        {
            var entities = source.EntityManager;
            Require(entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && entities.IsComponentEnabled<PrefabData>(prefab) && !entities.HasComponent<Deleted>(prefab)
                && !entities.HasComponent<PlaceholderObjectData>(prefab), "EXTERNAL_PREVIEW_LEAF_UNQUALIFIED");
            RequireEmptyBuffer<PrefabSubObject>(prefab);
            RequireEmptyBuffer<Game.Prefabs.SubNet>(prefab);
            RequireEmptyBuffer<Game.Prefabs.SubArea>(prefab);
            RequireEmptyBuffer<Game.Prefabs.SubLane>(prefab);
        }

        private static void RequireSameRows(Entity[] before, Entity[] after)
        {
            Require(before.Length == after.Length, "EXTERNAL_PREVIEW_PREFAB_ROWS_CHANGED");
            for (int i = 0; i < before.Length; i++) Require(before[i] == after[i], "EXTERNAL_PREVIEW_PREFAB_ROWS_CHANGED");
        }

        private bool GraphReady()
        {
            int count = visibleTemps.CalculateEntityCount();
            Require(count >= 0 && count <= maximumMembers, "EXTERNAL_PREVIEW_MEMBER_BOUND_CHANGED");
            if (count != maximumMembers) return false;
            using var ids = visibleTemps.ToEntityArray(Allocator.Temp);
            var entities = source.EntityManager;
            Entity candidateRoot = Entity.Null, candidateNode = Entity.Null;
            foreach (Entity entity in ids)
            {
                if (entities.HasComponent<Game.Buildings.Building>(entity))
                { Require(candidateRoot == Entity.Null, "EXTERNAL_PREVIEW_MULTIPLE_ROOTS"); candidateRoot = entity; }
                if (entities.HasComponent<Node>(entity))
                { Require(candidateNode == Entity.Null, "EXTERNAL_PREVIEW_MULTIPLE_NODES"); candidateNode = entity; }
            }
            if (candidateRoot == Entity.Null || candidateNode == Entity.Null || !entities.HasBuffer<SubObject>(candidateRoot)
                || !entities.HasBuffer<SubObject>(candidateNode) || !entities.HasBuffer<Game.Net.SubNet>(candidateRoot)
                || !entities.HasComponent<Owner>(candidateNode)) return false;
            return entities.GetBuffer<SubObject>(candidateRoot, true).Length == rootChildren.Length
                && entities.GetBuffer<SubObject>(candidateNode, true).Length == nodeChildren.Length
                && entities.GetBuffer<Game.Net.SubNet>(candidateRoot, true).Length == 1
                && entities.GetComponentData<Owner>(candidateNode).m_Owner == candidateRoot;
        }

        private void RequireEmptyInput() => Require(definitions.CalculateEntityCount() == 0 && visibleTemps.CalculateEntityCount() == 0
            && objectInputs.CalculateEntityCount() == 0 && nodeInputs.CalculateEntityCount() == 0,
            "EXTERNAL_PREVIEW_SOURCE_NOT_EMPTY");

        private EntityQuery DefaultInput(ComponentSystemBase system)
        {
            var field = system.GetType().GetField("m_DefinitionQuery", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Require(field != null && field.FieldType == typeof(EntityQuery), "EXTERNAL_DEFAULT_QUERY_METADATA_CHANGED");
            return (EntityQuery)field!.GetValue(system)!; // Borrowed read-only; never filtered or disposed here.
        }

        private static void RequireExact(EntityQuery query, Entity a, Entity b)
        {
            using var values = query.ToEntityArray(Allocator.Temp);
            Require(values.Length == 2 && (values[0] == a && values[1] == b || values[0] == b && values[1] == a),
                "EXTERNAL_DEFAULT_QUERY_MEMBERS_CHANGED");
        }

        private void RequireSelection() => Require(tools!.selected == selected && tools.selectedIndex == selectedIndex,
            "EXTERNAL_FIXTURE_CHANGED_SELECTION");
        private void RequireEmptyBuffer<T>(Entity entity) where T : unmanaged, IBufferElementData =>
            Require(!source.EntityManager.HasBuffer<T>(entity) || source.EntityManager.GetBuffer<T>(entity, true).Length == 0,
                "EXTERNAL_PREVIEW_UNEXPECTED_REFERENCE_BUFFER");
        private int ReadMoney()
        {
            var money = source.EntityManager.GetComponentData<PlayerMoney>(city!.City);
            Require(!money.m_Unlimited, "EXTERNAL_PREVIEW_UNLIMITED_MONEY");
            return money.money;
        }
        private T Existing<T>() where T : ComponentSystemBase => source.GetExistingSystemManaged<T>()
            ?? throw new Rejection("EXTERNAL_PREVIEW_NATIVE_SYSTEM_MISSING");
        private ComponentSystemBase[] ReadSystems()
        {
            Require(source.Systems.Count <= 4096, "EXTERNAL_PREVIEW_SYSTEM_LIST_BOUND");
            var result = new ComponentSystemBase[source.Systems.Count];
            for (int i = 0; i < result.Length; i++) result[i] = source.Systems[i];
            return result;
        }
        private bool SameSystems()
        {
            var current = ReadSystems(); if (current.Length != originalSystems.Length) return false;
            for (int i = 0; i < current.Length; i++) if (!ReferenceEquals(current[i], originalSystems[i])) return false;
            return true;
        }
        private bool SameTools()
        {
            if (tools!.tools.Count != originalTools.Length) return false;
            for (int i = 0; i < originalTools.Length; i++) if (!ReferenceEquals(tools.tools[i], originalTools[i])) return false;
            return true;
        }

        private DefinitionProducerPatchProbe ObservePipeline()
        {
            var types = new HashSet<Type>();
            // A finite developer contract, not an all-mod integrity claim.
            foreach (string name in new[] {
                "Game.Tools.ToolBaseSystem", "Game.Tools.ToolSystem", "Game.Tools.ToolOutputSystem", "Game.Tools.ToolOutputBarrier",
                "Game.Tools.ToolClearSystem", "Game.Tools.GenerateObjectsSystem", "Game.Tools.GenerateNodesSystem",
                "Game.Tools.ValidationSystem", "Game.Tools.ValidationSystem+Components", "Game.Tools.OriginalDeletedSystem",
                "Game.Net.LaneSystem", "Game.Tools.FindOwnersSystem", "Game.Tools.FindOwnersSystem2",
                "Game.Input.UIInputActionCollection", "Game.Input.UIInputAction", "Game.Input.UIInputCombinedAction",
                "Game.Input.UIInputOverrideAction", "Game.Input.InputActivator", "Game.Input.DisplayNameOverride",
                "Game.Input.ProxyAction",
                "Game.Objects.SubObjectSystem", "Game.Objects.SubObjectReferencesSystem", "Game.Net.SubNetReferencesSystem",
                "Game.Buildings.InitializeSystem", "Game.Common.PrepareCleanUpSystem", "Game.Common.CleanUpSystem" })
            {
                Type? type = typeof(ToolSystem).Assembly.GetType(name, false);
                Require(type != null, "EXTERNAL_PREVIEW_NATIVE_TYPE_MISSING");
                types.Add(type!);
            }
            var pending = new Queue<Type>(types);
            while (pending.Count != 0)
                foreach (Type nested in pending.Dequeue().GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    if (types.Add(nested))
                    {
                        Require(types.Count <= 128, "EXTERNAL_PREVIEW_TYPE_BOUND");
                        pending.Enqueue(nested);
                    }
            Require(types.Count > 0 && types.Count <= 128, "EXTERNAL_PREVIEW_TYPE_BOUND");
            var ordered = new List<Type>(types);
            ordered.Sort((left, right) => StringComparer.Ordinal.Compare(left.FullName, right.FullName));
            var result = new DefinitionProducerPatchProbe();
            var identity = new StringBuilder();
            nativeTypesDeclared = ordered.Count; nativeMethodsDeclared = 0; patchFailureType = null;
            foreach (Type type in ordered)
            {
                var methods = new List<MethodBase>(); AddMethods(type, methods);
                // Reuse the original per-type identity and allocation bounds.
                // Empty marker types have no executable methods to inspect.
                Require(methods.Count <= 128, "EXTERNAL_PREVIEW_TYPE_METHOD_BOUND");
                nativeMethodsDeclared += methods.Count;
                Require(nativeMethodsDeclared <= 4096, "EXTERNAL_PREVIEW_METHOD_BOUND");
                if (methods.Count == 0) continue;
                var batch = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "EXTERNAL_PREVIEW");
                result.MethodsChecked += batch.MethodsChecked; result.PatchedMethods += batch.PatchedMethods;
                if (!batch.Qualified)
                {
                    patchFailureType = type.FullName;
                    result.Reason = batch.Reason;
                    result.CopyPatchSampleFrom(batch);
                    latestPatchObservation = result;
                    return result;
                }
                if (result.HarmonyMvid.Length == 0) result.HarmonyMvid = batch.HarmonyMvid;
                Require(result.HarmonyMvid == batch.HarmonyMvid, "EXTERNAL_PREVIEW_PATCH_PROVIDER_CHANGED");
                string name = type.FullName ?? throw new Rejection("EXTERNAL_PREVIEW_TYPE_IDENTITY_MISSING");
                Require(name.Length <= 256, "EXTERNAL_PREVIEW_TYPE_IDENTITY_BOUND");
                identity.Append(name).Append(':').Append(methods.Count).Append(':')
                    .Append(batch.MethodSetSha256).Append(':').Append(batch.HarmonyMvid).Append('\n');
            }
            Require(nativeMethodsDeclared > 0 && result.MethodsChecked == nativeMethodsDeclared,
                "EXTERNAL_PREVIEW_METHOD_COVERAGE_INCOMPLETE");
            using (var sha = SHA256.Create())
                result.MethodSetSha256 = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString())))
                    .Replace("-", "").ToLowerInvariant();
            result.Qualified = true; result.Reason = "NO_REGISTERED_EXTERNAL_PREVIEW_PATCH_OBSERVED";
            latestPatchObservation = result;
            return result;
        }
        private static void AddMethods(Type type, List<MethodBase> methods)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            methods.AddRange(type.GetMethods(flags)); methods.AddRange(type.GetConstructors(flags));
        }
        private void RequirePipeline()
        {
            var observed = ObservePipeline();
            Require(observed.Qualified && observed.MethodSetSha256 == patch.MethodSetSha256,
                "EXTERNAL_PREVIEW_NATIVE_PATCH_CONTRACT_CHANGED");
        }

        private JToken PatchObservationSnapshot()
        {
            var observation = latestPatchObservation;
            if (observation == null) return JValue.CreateNull();
            // Fixed keys, two 41-byte declaration IDs and 256 encoded text bytes
            // keep this compact JSON addition below 1 KiB, including escaping.
            return new JObject {
                ["source"] = ReferenceEquals(observation, patch) ? "initial" : "recheck",
                ["methodsChecked"] = observation.MethodsChecked,
                ["sample"] = !observation.PatchSampleObserved ? JValue.CreateNull() : new JObject {
                    ["originalIdentity"] = Value(observation.PatchSampleOriginalIdentity),
                    ["originalMethod"] = Value(observation.PatchSampleOriginalName),
                    ["category"] = Value(observation.PatchSampleCategory),
                    ["owner"] = Value(observation.PatchSampleOwner),
                    ["patchIdentity"] = Value(observation.PatchSampleMethodIdentity),
                    ["patchMethod"] = Value(observation.PatchSampleMethodName),
                    ["methodPatchCount"] = observation.PatchSampleMethodPatchCount,
                    ["sampleIncomplete"] = observation.PatchSampleIncomplete,
                    ["fieldsMissing"] = observation.PatchSampleFieldsMissing,
                    ["truncated"] = observation.PatchSampleTruncated
                }
            };
        }

        internal JObject Snapshot() => new JObject {
            ["stage"] = stage.ToString(), ["passed"] = Passed, ["resourcesReleased"] = ResourcesReleased,
            ["nativeWorkMayReadSource"] = NativeStateUncertain, ["defaultPreviewOnly"] = true,
            ["defaultOutputRetainedOrUnconfirmed"] = nativeResultUnresolved,
            ["aiParallelQualified"] = false, ["commitQualified"] = false,
            ["definitionInputsConfirmed"] = definitionsConfirmed, ["previewConfirmed"] = previewConfirmed,
            ["heldFrames"] = heldFrames, ["holdConfirmed"] = holdConfirmed, ["cancellationConfirmed"] = cancellationConfirmed,
            ["toolClearAdvanced"] = clearAdvanced, ["actorRemoved"] = actorRemoved, ["groupDetached"] = groupDetached,
            ["phaseAnchorRetainedByWorld"] = group?.WorldRegistered ?? false, ["phaseAnchorUnregistered"] = false,
            ["phaseAnchorCreationReturned"] = group?.CreationReturned ?? false,
            ["phaseAnchorRegistrationAttempted"] = group?.WorldRegistrationAttempted ?? false,
            ["phaseAnchorRegistrationObserved"] = group?.WorldRegistrationObserved ?? false,
            ["phaseAnchorOwnershipConfirmed"] = group?.OwnershipConfirmed ?? true,
            ["phaseRegistrationsReturned"] = group?.ObservationPhaseRegistered ?? false,
            ["nativeCallStarted"] = actorOwner?.Instance.NativeStarted ?? false,
            ["nativeCallReturned"] = actorOwner?.Instance.NativeReturned ?? false,
            ["nativeJoined"] = actorOwner?.Instance.NativeJoined ?? false,
            ["inputCreationUnconfirmed"] = actorOwner?.Instance.InputCreationUnconfirmed ?? false,
            ["inputStatesReleased"] = actorOwner?.Instance.InputStatesReleased ?? true,
            ["partialCreationCleanupAttempted"] = actorOwner?.Instance.PartialCleanupAttempted ?? false,
            ["partialCreationCleanupReturned"] = actorOwner?.Instance.PartialCleanupReturned ?? false,
            ["inputStates"] = actorOwner?.Instance.InputStateReceipt() ?? new JArray(),
            ["cleanupDeadlineReached"] = cleanupDeadlineReached,
            ["moneyBefore"] = Value(moneyBefore), ["moneyBeforeClear"] = Value(moneyBeforeClear), ["moneyAfter"] = Value(moneyAfter),
            ["prefabExpansionQualified"] = prefabExpansionQualified,
            ["maximumMembers"] = maximumMembers, ["observedFrame"] = observedFrame,
            ["nativeMethodsChecked"] = patch.MethodsChecked,
            ["nativeTypesDeclared"] = nativeTypesDeclared, ["nativeMethodsDeclared"] = nativeMethodsDeclared,
            ["patchFailureType"] = patchFailureType == null ? JValue.CreateNull() : new JValue(patchFailureType),
            ["patchObservation"] = PatchObservationSnapshot(),
            ["definitionResourcesReleased"] = definition.ResourcesReleased,
            ["ownedDefinitionQueryRetained"] = definitionsCreated, ["ownedTempQueryRetained"] = tempsCreated,
            ["inputs"] = (JToken?)inputReceipt?.DeepClone() ?? JValue.CreateNull(),
            ["preview"] = (JToken?)graphReceipt?.DeepClone() ?? JValue.CreateNull(),
            ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType),
            ["scope"] = "One native default-pipeline external preview and normal cancellation; no AI concurrency or permanent construction proof. Empty disabled phase anchor remains World-owned."
        };
        private static JToken Value(object? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : InvalidOperationException, INativeProbeFailure
        { public string Code { get; } internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
