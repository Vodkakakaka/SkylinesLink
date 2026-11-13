#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Game.UI.InGame;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using Building = Game.Buildings.Building;

namespace SkylinesLink.Mod.Native
{
    // One empty-to-owned-fixture selection in the unsaved test city. Native UI
    // publication and the external MCP result are separate evidence boundaries.
    internal sealed class FixtureSelectionQualification
    {
        private readonly World source;
        private readonly UpdateSystem updates;
        private readonly GameManager manager;
        private readonly Action requireScope;
        private readonly SelectedInfoUISystem info;
        private readonly SystemHandle infoHandle;
        private readonly ToolSystem tool;
        private readonly DefaultToolSystem defaultTool;
        private readonly Entity target, prefab;
        private readonly int initialIndex;
        private readonly Action<Entity, Entity, float3> selectionChanged;
        private readonly Action<ToolBaseSystem> toolChanged;
        private bool ownsCommand, selectionSubscribed, toolSubscribed, ended;
        private bool published, guardsUnchanged = true, ownershipLost;
        private bool cleanupAttempted, cleanupRawFallback, rawSelectionRestored;
        private int? selectedFrame, publishedFrame;
        private string? failureCode;

        internal FixtureSelectionQualification(World source, UpdateSystem updates, Action requireScope, Entity target, Entity prefab)
        {
            RequireMainThread();
            this.source = source; this.updates = updates; this.requireScope = requireScope;
            this.target = target; this.prefab = prefab;
            manager = GameManager.instance ?? throw new InvalidOperationException("SELECTION_FIXTURE_SOURCE_UNAVAILABLE");
            info = source.GetExistingSystemManaged<SelectedInfoUISystem>()
                ?? throw new InvalidOperationException("SELECTION_FIXTURE_UI_UNAVAILABLE");
            infoHandle = info.SystemHandle;
            tool = source.GetExistingSystemManaged<ToolSystem>()
                ?? throw new InvalidOperationException("SELECTION_FIXTURE_TOOL_UNAVAILABLE");
            defaultTool = source.GetExistingSystemManaged<DefaultToolSystem>()
                ?? throw new InvalidOperationException("SELECTION_FIXTURE_DEFAULT_TOOL_UNAVAILABLE");
            initialIndex = tool.selectedIndex;
            selectionChanged = OnSelectionChanged;
            toolChanged = _ => { if (ownsCommand) LoseOwnership("SELECTION_FIXTURE_TOOL_CHANGED"); };
        }

        internal bool Published => published;
        internal bool IntentEnded => ended;
        internal bool ResourcesReleased => ended && !selectionSubscribed && !toolSubscribed;
        internal string? FailureCode => failureCode;
        internal bool Passed => ResourcesReleased && published && guardsUnchanged && rawSelectionRestored && !ownershipLost && failureCode == null;

        // Store this owner before calling Start: even a partially applied request
        // then remains reachable by the caller's ordinary main-thread stop path.
        internal void Start()
        {
            RequireMainThread();
            try
            {
                Require(!selectedFrame.HasValue && !ended, "SELECTION_FIXTURE_ALREADY_STARTED");
                RequireSource();
                Require(tool.selected == Entity.Null && info.selectedEntity == Entity.Null,
                    "SELECTION_FIXTURE_INITIAL_SELECTION_NOT_EMPTY");
                var entities = source.EntityManager;
                Require(target != Entity.Null && prefab != Entity.Null && entities.Exists(target)
                    && entities.HasComponent<Building>(target) && !entities.HasComponent<Deleted>(target)
                    && !entities.HasComponent<Temp>(target) && entities.HasComponent<PrefabRef>(target)
                    && entities.GetComponentData<PrefabRef>(target).m_Prefab == prefab,
                    "SELECTION_FIXTURE_TARGET_CHANGED");
                selectionSubscribed = true; info.eventSelectionChanged += selectionChanged;
                toolSubscribed = true; tool.EventToolChanged += toolChanged;
                ownsCommand = true; selectedFrame = UnityEngine.Time.frameCount;
                info.SetSelection(target);
                Observe();
            }
            catch (Rejection error) { Fail(error.Code); }
            catch { Fail("SELECTION_FIXTURE_SETUP_FAILED"); }
        }

        internal void Observe()
        {
            RequireMainThread();
            if (!ownsCommand || ended) return;
            try
            {
                RequireSource();
                Require(tool.selected == target, "SELECTION_FIXTURE_SELECTION_CHANGED");
                if (published) Require(info.selectedEntity == target && info.selectedPrefab == prefab,
                    "SELECTION_FIXTURE_PUBLISHED_SELECTION_CHANGED");
            }
            catch (Rejection error) { LoseOwnership(error.Code); }
            catch { LoseOwnership("SELECTION_FIXTURE_GUARD_CHANGED"); }
        }

        private void OnSelectionChanged(Entity entity, Entity selectedPrefab, float3 position)
        {
            if (!ownsCommand || ended) return;
            try
            {
                RequireMainThread(); RequireSource();
                Require(entity == target && selectedPrefab == prefab && tool.selected == target
                    && info.selectedEntity == entity && info.selectedPrefab == selectedPrefab
                    && math.all(info.selectedPosition == position) && math.all(math.isfinite(position)),
                    "SELECTION_FIXTURE_UI_SELECTION_CHANGED");
                published = true; publishedFrame ??= UnityEngine.Time.frameCount;
            }
            catch (Rejection error) { LoseOwnership(error.Code); }
            catch { LoseOwnership("SELECTION_FIXTURE_UI_SELECTION_CHANGED"); }
        }

        // No lease, polling updater, UI refresh or return gate. All callers use
        // this same bounded prefix before asking the owned city/root to stop.
        internal void ReleaseOwnedSelection()
        {
            RequireMainThread();
            if (ended) { ReleaseObservers(); return; }
            cleanupAttempted = ownsCommand;
            try
            {
                if (ownsCommand && !ownershipLost)
                {
                    RequireSource();
                    Require(tool.selected == target, "SELECTION_FIXTURE_SELECTION_CHANGED");
                    Require(info.selectedEntity == Entity.Null || info.selectedEntity == target && info.selectedPrefab == prefab,
                        "SELECTION_FIXTURE_PUBLISHED_SELECTION_CHANGED");
                    // SetSelection compares against the panel cache, not raw
                    // selected. Before publication SetSelection(Null) is a no-op.
                    ownsCommand = false;
                    if (info.selectedEntity == Entity.Null)
                    { cleanupRawFallback = true; tool.selected = Entity.Null; }
                    else info.SetSelection(Entity.Null);
                    rawSelectionRestored = tool.selected == Entity.Null && tool.selectedIndex == initialIndex
                        && ReferenceEquals(tool.activeTool, defaultTool);
                    Require(rawSelectionRestored, "SELECTION_FIXTURE_RESTORE_FAILED");
                }
            }
            catch (Rejection error) { LoseOwnership(error.Code); }
            catch { LoseOwnership("SELECTION_FIXTURE_RESTORE_UNAVAILABLE"); }
            finally
            {
                ownsCommand = false; ended = true;
                ReleaseObservers();
            }
        }

        private void ReleaseObservers()
        {
            // Ended intent never replays selection restoration. Failed removes
            // retain only their exact delegate and are retried by the stop pump.
            try { if (selectionSubscribed) info.eventSelectionChanged -= selectionChanged; selectionSubscribed = false; }
            catch { Fail("SELECTION_FIXTURE_OBSERVER_REMOVAL_FAILED"); }
            try { if (toolSubscribed) tool.EventToolChanged -= toolChanged; toolSubscribed = false; }
            catch { Fail("SELECTION_FIXTURE_OBSERVER_REMOVAL_FAILED"); }
        }

        private void RequireSource()
        {
            requireScope();
            Require(ReferenceEquals(manager, GameManager.instance) && manager.state == GameManager.State.WorldReady
                && manager.gameMode == GameMode.Game && !manager.isGameLoading && source.IsCreated
                && ReferenceEquals(updates.World, source) && ReferenceEquals(source.GetExistingSystemManaged<UpdateSystem>(), updates)
                && ReferenceEquals(source.GetExistingSystemManaged<SelectedInfoUISystem>(), info) && info.SystemHandle == infoHandle
                && ReferenceEquals(source.GetExistingSystemManaged<ToolSystem>(), tool)
                && ReferenceEquals(source.GetExistingSystemManaged<DefaultToolSystem>(), defaultTool)
                && ReferenceEquals(tool.activeTool, defaultTool) && tool.selectedIndex == initialIndex,
                "SELECTION_FIXTURE_SOURCE_OR_CONTROL_CHANGED");
        }

        internal JObject Snapshot() => new JObject {
            ["target"] = PreviewCommitGraph.Id(target), ["prefab"] = PreviewCommitGraph.Id(prefab),
            ["published"] = published, ["selectedFrame"] = Number(selectedFrame), ["publishedFrame"] = Number(publishedFrame),
            ["initialSelectedIndex"] = initialIndex, ["guardsUnchanged"] = guardsUnchanged,
            ["cleanupAttempted"] = cleanupAttempted, ["cleanupRawFallback"] = cleanupRawFallback,
            ["rawSelectionRestored"] = rawSelectionRestored, ["ownershipLost"] = ownershipLost,
            ["intentEnded"] = ended, ["observersRemoved"] = !selectionSubscribed && !toolSubscribed,
            ["checksPassed"] = Passed, ["externalClientResultRequired"] = true,
            ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode)
        };
        private static JToken Number(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        private void LoseOwnership(string code) { ownershipLost = true; guardsUnchanged = false; Fail(code); }
        private void Fail(string code) { failureCode ??= code; }
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("SELECTION_FIXTURE_MAIN_THREAD_REQUIRED"); }
        private sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) { Code = code; }
        }
    }
}
#endif
