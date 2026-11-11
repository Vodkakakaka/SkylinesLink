#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using AgeMask = Game.Tools.AgeMask;

namespace SkylinesLink.Mod.Native
{
    /// <summary>
    /// Developer-only adapter for one parameterized native definition capture.
    /// It is not a player tool and never plays commands into the source World.
    /// Native rule validation, fees and construction are separate, unqualified steps.
    /// </summary>
    [DisableAutoCreation]
    public sealed partial class ObjectDefinitionProducer : ObjectToolBaseSystem
    {
        private NativeList<ControlPoint> points;
        private ObjectDefinitionBarrier? output;
        private Entity prefab, theme, original;
        private RandomSeed seed;
        private AgeMask ages;
        private bool leftHandTraffic, armed, attempted, returned, completed;
        private bool parameterAllocationAttempted, parameterAllocationReturned;

        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }

        public override string toolID => "SkylinesLink.DefinitionCapture";
        public override PrefabBase GetPrefab() => null!;
        public override bool TrySetPrefab(PrefabBase value) => false;

        internal bool NativeCallStarted => attempted;
        internal bool NativeCallReturned => returned;
        internal bool CompletionObserved => completed;
        internal Entity CapturedPrefab => prefab;
        internal Entity CapturedOriginal => original;
        internal bool ParametersCreated => points.IsCreated;
        internal bool ParameterAllocationUnconfirmed => parameterAllocationAttempted && !parameterAllocationReturned;
        internal bool ParametersCanBeReleased => !ParameterAllocationUnconfirmed && (!attempted || completed);

        protected override void OnCreate()
        {
            CreationStarted = true;
            // World.CreateSystemManaged still invokes the inherited compiler
            // chain. This method deliberately replaces Tool/Game registration:
            // no input states, ToolSystem.tools entry or scene callbacks exist.
            RequireMainThread();
            Enabled = false;
            if (typeof(ObjectToolBaseSystem).Module.ModuleVersionId.ToString("N") != "e15551e93dea48558dde1da3cea7d7ef" ||
                typeof(World).Module.ModuleVersionId.ToString("N") != "3d6b23aeb28144a6a5f4cbd5fd3d75e6")
                throw new InvalidOperationException("OBJECT_PRODUCER_NATIVE_BUILD_UNQUALIFIED");
            m_ObjectSearchSystem = World.GetExistingSystemManaged<Game.Objects.SearchSystem>()
                ?? throw new InvalidOperationException("OBJECT_PRODUCER_SEARCH_UNAVAILABLE");
            m_WaterSystem = World.GetExistingSystemManaged<WaterSystem>()
                ?? throw new InvalidOperationException("OBJECT_PRODUCER_WATER_UNAVAILABLE");
            m_TerrainSystem = World.GetExistingSystemManaged<TerrainSystem>()
                ?? throw new InvalidOperationException("OBJECT_PRODUCER_TERRAIN_UNAVAILABLE");
            CreationReturned = true;
        }

        // SystemBase's Before/AfterOnUpdate remain active, refreshing and
        // publishing real ECS dependencies. ToolBase's input hooks do not run.
        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }

        internal void CaptureOnce(ObjectDefinitionBarrier barrier, Entity objectPrefab, Entity cityTheme,
            bool cityLeftHandTraffic, ControlPoint point, RandomSeed randomSeed, AgeMask ageMask,
            RelocationDefinitionSource? relocation = null)
        {
            RequireMainThread();
            if (attempted || points.IsCreated || armed || barrier == null || !ReferenceEquals(barrier.World, World) ||
                barrier.PendingCount != 0 || objectPrefab == Entity.Null || !EntityManager.Exists(objectPrefab) ||
                point.m_OriginalEntity != Entity.Null || !math.all(math.isfinite(point.m_Position)) ||
                !math.all(math.isfinite(point.m_Rotation.value)) || !math.isfinite(point.m_Elevation) ||
                math.abs(math.lengthsq(point.m_Rotation.value) - 1f) > 0.0001f ||
                relocation != null && (relocation.Prefab != objectPrefab || !EntityManager.Exists(relocation.Root)))
                throw new InvalidOperationException("OBJECT_PRODUCER_INPUT_REJECTED");
            output = barrier;
            m_ToolOutputBarrier = barrier;
            prefab = objectPrefab; theme = cityTheme; leftHandTraffic = cityLeftHandTraffic;
            original = relocation?.Root ?? Entity.Null;
            seed = randomSeed; ages = ageMask;
            parameterAllocationAttempted = true;
            points = new NativeList<ControlPoint>(1, Allocator.Persistent);
            parameterAllocationReturned = true;
            points.Add(point);
            armed = true;
            try
            {
                Enabled = true;
                Update();
                // The protected API has returned and the actual native job must
                // finish before output can be borrowed or parameter memory freed.
                Dependency.Complete();
                completed = true;
                if (!returned || barrier.PendingCount != 1)
                    throw new InvalidOperationException("OBJECT_PRODUCER_OUTPUT_NOT_CONFIRMED");
            }
            finally
            {
                armed = false;
                Enabled = false;
                // This is also a real SystemBase dependency join, not merely an
                // input-state switch. A failed protected call is still uncertain.
                Update();
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (!armed || attempted || output == null || !ReferenceEquals(m_ToolOutputBarrier, output))
                throw new InvalidOperationException("OBJECT_PRODUCER_NOT_ARMED");
            attempted = true;
            JobHandle job = CreateDefinitions(prefab, Entity.Null, Entity.Null, Entity.Null, original,
                Entity.Null, theme, points, default, false, leftHandTraffic, false, false,
                1f, 0f, 1f, 0f, 0f, seed, Snap.Upright, ages, false, default, inputDeps);
            returned = true;
            return job;
        }

        internal EntityCommandBuffer BorrowCompletedForInspection()
        {
            RequireMainThread();
            if (!completed || !returned || output == null || output.PendingCount != 1)
                throw new InvalidOperationException("OBJECT_PRODUCER_OUTPUT_NOT_COMPLETED");
            EntityCommandBuffer buffer = output.BorrowSingle();
            if (!buffer.IsCreated) throw new InvalidOperationException("OBJECT_PRODUCER_OUTPUT_NOT_CREATED");
            // Inspection must never Playback or Dispose this alias. The native
            // queue keeps sole buffer ownership and is normally discarded.
            return buffer;
        }

        protected override void OnDestroy()
        {
            DestructionStarted = true;
            // No Game/Tool OnCreate registrations need base removal. Unexpected
            // failure after Schedule but before the protected method returned
            // cannot be declared joined or freed; the probe owner retains that
            // uncertainty and must not claim release or retry the operation.
            if (!ParametersCanBeReleased) throw new InvalidOperationException("OBJECT_PRODUCER_NATIVE_RESPONSIBILITY_UNRESOLVED");
            if (points.IsCreated) points.Dispose();
            DestructionReturned = true;
        }

        private static void RequireMainThread()
        {
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("OBJECT_PRODUCER_MAIN_THREAD_REQUIRED");
        }
    }

    /// <summary>Own native queue; never registered with UpdateSystem or automatically played.</summary>
    [DisableAutoCreation]
    public sealed partial class ObjectDefinitionBarrier : ToolOutputBarrier
    {
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool DestructionStarted { get; private set; }
        internal bool DestructionReturned { get; private set; }
        internal int PendingCount => PendingBuffers.Length;
        protected override void OnCreate()
        {
            CreationStarted = true;
            if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("OBJECT_BARRIER_MAIN_THREAD_REQUIRED");
            base.OnCreate();
            CreationReturned = true;
        }
        protected override void OnDestroy()
        {
            DestructionStarted = true;
            if (CreationStarted)
            {
                if (!CreationReturned) throw new InvalidOperationException("OBJECT_BARRIER_CREATION_UNRESOLVED");
                // Native Flush(false) joins its producers and disposes the sole
                // queued ECB plus allocator. Never dispose an inspection alias.
                base.OnDestroy();
            }
            DestructionReturned = true;
        }
        protected override void OnUpdate()
        {
            // Inherited Update would Flush(true) into the real source World.
            // This diagnostic queue only uses inherited OnDestroy/Flush(false).
            throw new InvalidOperationException("OBJECT_DEFINITION_CAPTURE_CANNOT_PLAYBACK");
        }
        internal EntityCommandBuffer BorrowSingle()
        {
            if (PendingBuffers.Length != 1) throw new InvalidOperationException("OBJECT_PRODUCER_SINGLE_BUFFER_REQUIRED");
            return PendingBuffers[0];
        }
    }
}
#endif
