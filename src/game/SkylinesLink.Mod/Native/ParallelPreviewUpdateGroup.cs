#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // UpdateSystem has no unregister API. This empty, disabled anchor belongs
    // to the World after registration; only its late-created actor is retired.
    [DisableAutoCreation]
    public sealed partial class ParallelPreviewUpdateGroup : ComponentSystemGroup
    {
        internal bool ToolPhaseRegistered { get; private set; }
        internal bool InputPhaseRegistered { get; private set; }
        internal bool ObservationPhaseRegistered { get; private set; }
        internal bool CreationStarted { get; private set; }
        internal bool CreationReturned { get; private set; }
        internal bool WorldRegistrationAttempted { get; private set; }
        internal bool WorldRegistrationObserved { get; private set; }
        internal bool WorldRegistered { get; private set; }
        internal bool WorldIdentityConsistent { get; private set; }
        internal bool OwnershipConfirmed => !WorldRegistrationAttempted && !CreationStarted
            || WorldRegistrationObserved && WorldIdentityConsistent
                && (WorldRegistered ? CreationReturned : !CreationStarted);

        protected override void OnCreate()
        {
            CreationStarted = true;
            base.OnCreate();
            Enabled = false;
            CreationReturned = true;
        }

        internal void Register(UpdateSystem updates)
        {
            Require(MainThreadDispatcher.isMainThread && updates.World.IsCreated
                && updates.currentPhase == SystemUpdatePhase.Invalid
                && NativeConstructionBuild.Game(typeof(UpdateSystem).Module)
                && NativeConstructionBuild.Entities(typeof(ComponentSystemGroup).Module),
                "PARALLEL_GROUP_CONTEXT_UNQUALIFIED");
            var world = updates.World;
            Require(world.GetExistingSystemManaged<ParallelPreviewUpdateGroup>() == null,
                "PARALLEL_GROUP_ALREADY_REGISTERED");
            Require(world.GetExistingSystemManaged<ToolOutputSystem>() != null
                && world.GetExistingSystemManaged<GenerateObjectsSystem>() != null
                && world.GetExistingSystemManaged<ModificationEndBarrier>() != null,
                "PARALLEL_GROUP_DEFAULT_TARGET_MISSING");
            var registration = new NativeSystemRegistration<ParallelPreviewUpdateGroup>(world, this);
            WorldRegistrationAttempted = true;
            try { registration.Add(); }
            finally
            {
                // Observe both facts even when Instance.World is null. A failed
                // creation must not hide a surviving registry entry.
                try
                {
                    WorldRegistered = NativeProbeSourceProtection.IsRegistered(world, this);
                    World? actual = World;
                    WorldIdentityConsistent = WorldRegistered ? ReferenceEquals(actual, world) : actual == null;
                    WorldRegistrationObserved = true;
                }
                catch { /* Unknown registration remains a failed ownership receipt. */ }
            }
            Require(OwnershipConfirmed && WorldRegistered, "PARALLEL_GROUP_WORLD_OWNERSHIP_UNCONFIRMED");
            // GetOrCreate now resolves this exact instance. A partial phase
            // registration still retains a valid World-owned, disabled group.
            updates.UpdateBefore<ParallelPreviewUpdateGroup, ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
            ToolPhaseRegistered = true;
            updates.UpdateBefore<ParallelPreviewUpdateGroup, GenerateObjectsSystem>(SystemUpdatePhase.Modification1);
            InputPhaseRegistered = true;
            updates.UpdateAfter<ParallelPreviewUpdateGroup, ModificationEndBarrier>(SystemUpdatePhase.ModificationEnd);
            ObservationPhaseRegistered = true;
        }

        internal void Attach(ExternalPreviewTool actor)
        {
            Require(MainThreadDispatcher.isMainThread && actor.World == World && ManagedSystems.Count == 0
                && ToolPhaseRegistered && InputPhaseRegistered && ObservationPhaseRegistered,
                "PARALLEL_GROUP_ATTACH_REJECTED");
            AddSystemToUpdateList(actor);
            SortSystems();
            Require(ManagedSystems.Count == 1 && ReferenceEquals(ManagedSystems[0], actor),
                "PARALLEL_GROUP_ATTACH_UNCONFIRMED");
            Enabled = true;
        }

        internal void Detach(ExternalPreviewTool actor)
        {
            Require(MainThreadDispatcher.isMainThread, "PARALLEL_GROUP_MAIN_THREAD_REQUIRED");
            Enabled = false;
            RemoveSystemFromUpdateList(actor);
            // Removal is deferred by Entities; sort while the actor is alive.
            SortSystems();
            Require(ManagedSystems.Count == 0, "PARALLEL_GROUP_DETACH_UNCONFIRMED");
            using var systems = GetAllSystems();
            Require(systems.Length == 0, "PARALLEL_GROUP_HANDLES_RETAINED");
        }

        private static void Require(bool condition, string code)
        { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : InvalidOperationException, INativeProbeFailure
        { public string Code { get; } internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
