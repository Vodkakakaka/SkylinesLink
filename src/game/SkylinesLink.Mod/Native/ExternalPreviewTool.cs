#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Input;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using AgeMask = Game.Tools.AgeMask;

namespace SkylinesLink.Mod.Native
{
    // An explicit developer fixture playing the external-tool role. It uses
    // the default native definition/output pipeline, not keyboard or UI input.
    [DisableAutoCreation]
    public sealed partial class ExternalPreviewTool : ObjectToolBaseSystem
    {
        private ParallelPreviewProbe? owner;
        private PrefabBase? managedPrefab;
        private NativeList<ControlPoint> points;
        private bool allocationAttempted, allocationReturned;
        private readonly IProxyAction?[] ownedActions = new IProxyAction?[5];
        private readonly bool[] inputRead = new bool[5], disposeAttempted = new bool[5], disposeReturned = new bool[5];
        private readonly bool[] inputIdentityConfirmed = new bool[5];
        private FieldInfo[]? inputFields;
        private bool inputCreationStarted, baseCreationReturned, inputsCaptured;
        internal bool PartialCleanupAttempted { get; private set; }
        internal bool PartialCleanupReturned { get; private set; }
        private static readonly string[] InputNames = { "m_DefaultApply", "m_DefaultSecondaryApply", "m_DefaultCancel", "m_MouseApply", "m_MouseCancel" };
        internal bool CreationReturned { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal bool Stopped { get; private set; }
        internal bool NativeStarted { get; private set; }
        internal bool NativeReturned { get; private set; }
        internal bool NativeJoined { get; private set; }
        internal bool ClearStarted { get; private set; }
        internal bool ClearReturned { get; private set; }
        internal bool ClearJoined { get; private set; }
        internal bool NativeWorkMayReadSource => NativeStarted && !NativeJoined || ClearStarted && !ClearJoined;
        internal bool ParametersReleased => !points.IsCreated && (!allocationAttempted || allocationReturned);
        internal bool InputCreationUnconfirmed => inputCreationStarted && (!baseCreationReturned || !inputsCaptured);
        internal bool InputStatesReleased
        {
            get
            {
                if (!inputCreationStarted) return true;
                if (InputCreationUnconfirmed) return false;
                for (int i = 0; i < ownedActions.Length; i++)
                    if (ownedActions[i] != null && !disposeReturned[i]) return false;
                return true;
            }
        }

        public override string toolID => "SkylinesLink.ExternalPreviewFixture";
        public override PrefabBase GetPrefab() => managedPrefab!;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            RequireMainThread();
            inputFields = ResolveInputFields(); // Validate the read-only ABI before native registration.
            inputCreationStarted = true;
            try
            {
                base.OnCreate();
                baseCreationReturned = true;
                CaptureInputs();
            }
            catch
            {
                // Returned fields can be retired. A State or combined prefix
                // that never reached a field remains explicitly unconfirmed.
                PartialCleanupAttempted = true;
                try
                {
                    Enabled = false;
                    ReleaseInputStates();
                }
                catch { /* Per-state receipts and InputCreationUnconfirmed retain this responsibility. */ }
                try
                {
                    if (m_ToolSystem != null) m_ToolSystem.tools.Remove(this);
                    // Entities.CreateInstance's failure path does not call
                    // OnDestroy. Retire our GameSystem callbacks while the
                    // SystemState is still valid, preserving the original error.
                    base.OnDestroy();
                    PartialCleanupReturned = true;
                }
                catch { /* The missing cleanup receipt remains explicit. */ }
                throw;
            }
            Enabled = false;
            CreationReturned = true;
        }

        internal void Configure(ParallelPreviewProbe probe, PrefabBase prefab)
        {
            RequireMainThread();
            if (!CreationReturned || owner != null || prefab == null) throw new Rejection("EXTERNAL_TOOL_CONFIGURATION_REJECTED");
            owner = probe; managedPrefab = prefab;
        }

        // No synthetic actions, raycasts or input-state enabling are needed.
        protected override void OnStartRunning() { Stopped = false; }
        protected override void OnStopRunning() { Stopped = true; }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (owner == null) return inputDeps;
            try { return owner.OnNativePhase(this, inputDeps); }
            catch (Exception error)
            {
                applyMode = ApplyMode.None;
                owner.RecordFailure(error);
                return inputDeps;
            }
        }

        internal JobHandle Emit(ObjectDefinitionPayload expected, ControlPoint point, Entity theme,
            bool leftHandTraffic, JobHandle inputDeps)
        {
            RequireMainThread();
            if (NativeStarted || points.IsCreated) throw new Rejection("EXTERNAL_TOOL_DEFINITION_REPEATED");
            allocationAttempted = true;
            points = new NativeList<ControlPoint>(1, Allocator.Persistent);
            allocationReturned = true;
            points.Add(point);
            applyMode = ApplyMode.Clear;
            NativeStarted = true;
            var job = CreateDefinitions(expected.RootCreation.m_Prefab, Entity.Null, Entity.Null, Entity.Null,
                Entity.Null, Entity.Null, theme, points, default, false, leftHandTraffic, false, false,
                1f, 0f, 1f, 0f, 0f, default, Snap.Upright, AgeMask.Sapling, false, default, inputDeps);
            NativeReturned = true;
            job.Complete();
            NativeJoined = true;
            points.Dispose();
            return job;
        }

        internal JobHandle ClearDefinitions(EntityQuery exactDefinitions, JobHandle inputDeps)
        {
            RequireMainThread();
            if (ClearStarted) throw new Rejection("EXTERNAL_TOOL_CLEAR_REPEATED");
            applyMode = ApplyMode.Clear;
            ClearStarted = true;
            var job = DestroyDefinitions(exactDefinitions, m_ToolOutputBarrier, inputDeps);
            ClearReturned = true;
            job.Complete();
            ClearJoined = true;
            return job;
        }

        internal void Hold() => applyMode = ApplyMode.None;
        internal bool AllowApply() => GetAllowApply();

        internal void ReleaseInputStates()
        {
            RequireMainThread();
            if (NativeWorkMayReadSource || Enabled) throw new Rejection("EXTERNAL_INPUT_RELEASE_NOT_QUIESCENT");
            Exception? captureFailure = null;
            try { CaptureInputs(); }
            catch (Exception error) { captureFailure = error; }
            for (int i = 0; i < ownedActions.Length; i++)
            {
                var value = ownedActions[i];
                if (value == null || disposeAttempted[i]) continue;
                try
                {
                    if (!inputRead[i] || !ReferenceEquals(inputFields![i].GetValue(this), value))
                        throw new Rejection("EXTERNAL_INPUT_STATE_IDENTITY_CHANGED");
                    RequireState(value);
                    inputIdentityConfirmed[i] = true;
                    disposeAttempted[i] = true;
                    value.shouldBeEnabled = false;
                    // These concrete public disposers remove their own native
                    // activator/display entries. No InputManager list is edited.
                    if (value is UIInputAction.State simple)
                    {
                        simple.Dispose();
                        if (simple.displayName != null && !simple.displayName.isDisposed)
                            throw new Rejection("EXTERNAL_INPUT_DISPLAY_RELEASE_UNCONFIRMED");
                    }
                    else ((UIInputCombinedAction.State)value).Dispose();
                    disposeReturned[i] = true;
                }
                catch (Exception error) { captureFailure ??= error; }
            }
            if (captureFailure != null) throw captureFailure;
        }

        private static FieldInfo[] ResolveInputFields()
        {
            if (!NativeConstructionBuild.Game(typeof(ToolBaseSystem).Module)
                || !ReferenceEquals(typeof(UIInputAction.State).Module, typeof(ToolBaseSystem).Module)
                || !ReferenceEquals(typeof(UIInputCombinedAction.State).Module, typeof(ToolBaseSystem).Module))
                throw new Rejection("EXTERNAL_INPUT_NATIVE_MODULE_UNQUALIFIED");
            var result = new FieldInfo[InputNames.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var field = typeof(ToolBaseSystem).GetField(InputNames[i], BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null || field.FieldType != typeof(IProxyAction))
                    throw new Rejection("EXTERNAL_INPUT_FIELD_ABI_CHANGED");
                result[i] = field;
            }
            return result;
        }

        private void CaptureInputs()
        {
            if (inputFields == null) return;
            for (int i = 0; i < ownedActions.Length; i++)
            {
                if (inputRead[i]) continue;
                var value = (IProxyAction?)inputFields[i].GetValue(this);
                if (baseCreationReturned && value == null)
                    throw new Rejection("EXTERNAL_INPUT_STATE_MISSING");
                if (value != null)
                {
                    RequireState(value);
                    for (int j = 0; j < i; j++)
                        if (ReferenceEquals(ownedActions[j], value)) throw new Rejection("EXTERNAL_INPUT_STATE_ALIAS_UNQUALIFIED");
                }
                ownedActions[i] = value; inputRead[i] = true;
            }
            inputsCaptured = true;
        }

        private static void RequireState(IProxyAction state)
        {
            Type type = state.GetType();
            if ((type != typeof(UIInputAction.State) && type != typeof(UIInputCombinedAction.State))
                || !NativeConstructionBuild.Game(type.Module))
                throw new Rejection("EXTERNAL_INPUT_STATE_TYPE_UNQUALIFIED");
        }

        internal Newtonsoft.Json.Linq.JArray InputStateReceipt()
        {
            var result = new Newtonsoft.Json.Linq.JArray();
            for (int i = 0; i < InputNames.Length; i++)
                result.Add(new Newtonsoft.Json.Linq.JObject {
                    ["slot"] = InputNames[i], ["fieldRead"] = inputRead[i],
                    ["type"] = ownedActions[i]?.GetType().FullName,
                    ["moduleMvid"] = ownedActions[i]?.GetType().Module.ModuleVersionId.ToString("N"),
                    ["identityConfirmed"] = inputIdentityConfirmed[i],
                    ["disposeAttempted"] = disposeAttempted[i], ["disposeReturned"] = disposeReturned[i]
                });
            return result;
        }

        protected override void OnDestroy()
        {
            RequireMainThread();
            if (NativeWorkMayReadSource) throw new Rejection("EXTERNAL_TOOL_NATIVE_WORK_UNRESOLVED");
            if (points.IsCreated) points.Dispose();
            // ToolBase registers this exact entry but has no matching dynamic
            // removal. The coordinator first observes the real stop transition.
            if (m_ToolSystem != null) m_ToolSystem.tools.Remove(this);
            base.OnDestroy();
            owner = null; managedPrefab = null;
            DestructionReturned = true;
        }

        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new Rejection("EXTERNAL_TOOL_MAIN_THREAD_REQUIRED"); }
        private sealed class Rejection : InvalidOperationException, INativeProbeFailure
        { public string Code { get; } internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
