#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using Colossal.Core;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Development-only prerequisite experiment. Neither World is the player's
    // World; all command targets below are created by the same owned ECB.
    // A scratch World is not a sandbox for arbitrary native commands or refs.
    internal sealed class DefinitionContainerProbe
    {
        public struct Sentinel : IComponentData { public int Value; }
        public struct Definition : IComponentData { public Entity External, Self; public int Value; }

        private World? source, target;
        private EntityCommandBuffer commands, discarded;
        private bool ran, checksPassed, defaultUnchanged, membersUnchanged;
        private bool sourceUnchanged, sourceOwnsReference, targetRejectsExternal;
        private bool componentFixed, componentExternalRetained, discardUnpublished;
        private int createdWorlds, disposedWorlds, sourceSystems, targetSystems;
        private double elapsedMilliseconds;
        private bool constructorUncertain, nativeReleaseFailed, releaseAttempted, releaseConfirmed;
        private string? failureCode, exceptionType, releaseExceptionType;

        internal bool ResourcesReleased => releaseConfirmed && !constructorUncertain && !nativeReleaseFailed;

        internal void Run()
        {
            RequireMainThread();
            if (ran) throw new InvalidOperationException("A container experiment may run only once.");
            ran = true;
            var clock = Stopwatch.StartNew();
            var defaultBefore = World.DefaultGameObjectInjectionWorld;
            World[] before = Array.Empty<World>();
            try
            {
                before = CaptureWorldMembers();
                Check(before.Length <= 64, "WORLD_OBSERVATION_BUDGET");
                // Do not request systems, player-loop registration, Default
                // assignment, live copies or cross-World typed dereferencing.
                // World creation still touches global registries/early inits;
                // this experiment makes no zero-side-effect or hard-frame claim.
                source = CreateOwnedWorld("SkylinesLink owned reference fixture"); createdWorlds++;
                target = CreateOwnedWorld("SkylinesLink owned definition fixture"); createdWorlds++;
                sourceSystems = source.Systems.Count; targetSystems = target.Systems.Count;
                Check(sourceSystems == 0 && targetSystems == 0, "UNEXPECTED_SYSTEMS");
                Entity external = source.EntityManager.CreateEntity();
                source.EntityManager.AddComponentData(external, new Sentinel { Value = 17 });
                sourceOwnsReference = source.EntityManager.Exists(external);
                targetRejectsExternal = !target.EntityManager.Exists(external);
                Check(sourceOwnsReference && targetRejectsExternal, "WORLD_MEMBERSHIP_MISMATCH");
                using (var query = target.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Definition>()))
                {
                    Check(query.CalculateEntityCount() == 0, "TARGET_NOT_EMPTY");
                    discarded = new EntityCommandBuffer(Allocator.TempJob, PlaybackPolicy.SinglePlayback);
                    Entity unplayed = discarded.CreateEntity();
                    discarded.AddComponent(unplayed, new Definition { External = external, Self = unplayed, Value = 19 });
                    ReleaseBuffer(ref discarded);
                    discardUnpublished = query.CalculateEntityCount() == 0;
                    Check(discardUnpublished, "DISCARD_PUBLISHED_OUTPUT");

                    commands = new EntityCommandBuffer(Allocator.TempJob, PlaybackPolicy.SinglePlayback);
                    Entity deferred = commands.CreateEntity();
                    Check(deferred.Index < 0, "EXPECTED_DEFERRED_ENTITY");
                    commands.AddComponent(deferred, new Definition { External = external, Self = deferred, Value = 23 });
                    commands.Playback(target.EntityManager);
                    using (var entities = query.ToEntityArray(Allocator.Temp))
                    {
                        Check(entities.Length == 1, "OUTPUT_COUNT_MISMATCH");
                        Entity actual = entities[0];
                        Check(target.EntityManager.Exists(actual), "OUTPUT_NOT_OWNED");
                        Definition definition = target.EntityManager.GetComponentData<Definition>(actual);
                        componentFixed = definition.Self == actual && actual != deferred;
                        componentExternalRetained = definition.External == external;
                        Check(definition.Value == 23 && componentFixed && componentExternalRetained, "COMPONENT_FIXUP_MISMATCH");
                    }
                    ReleaseBuffer(ref commands);
                }
                // The external data is read only through its known owner.
                sourceUnchanged = source.EntityManager.GetComponentData<Sentinel>(external).Value == 17;
                Check(sourceUnchanged, "SOURCE_DATA_CHANGED");
                checksPassed = true;
            }
            catch (Exception error)
            {
                failureCode = error is ProbeCheckFailure failure ? failure.Code : "CONTAINER_EXPERIMENT_FAILED";
                exceptionType = error.GetType().Name;
            }
            finally
            {
                Release();
                try
                {
                    defaultUnchanged = ReferenceEquals(defaultBefore, World.DefaultGameObjectInjectionWorld);
                    World[] after = CaptureWorldMembers();
                    membersUnchanged = before.Length <= 64 && before.Length == after.Length;
                    if (membersUnchanged)
                        for (int i = 0; i < before.Length; i++) membersUnchanged &= ReferenceEquals(before[i], after[i]);
                }
                catch (Exception error)
                { failureCode = failureCode ?? "WORLD_OBSERVATION_FAILED"; exceptionType = error.GetType().Name; }
                clock.Stop(); elapsedMilliseconds = clock.Elapsed.TotalMilliseconds;
            }
        }

        // Native Dispose is not transactional or safely retryable after an
        // exception. Retain unknown responsibility until the host ends; never
        // treat a nulled pointer or a later Stop as a second cleanup attempt.
        internal void Release()
        {
            RequireMainThread();
            if (releaseAttempted || nativeReleaseFailed) return;
            releaseAttempted = true;
            try
            {
                if (commands.IsCreated) ReleaseBuffer(ref commands);
                if (discarded.IsCreated) ReleaseBuffer(ref discarded);
                ReleaseWorld(ref target); ReleaseWorld(ref source);
                releaseConfirmed = !constructorUncertain;
            }
            catch (Exception error)
            { nativeReleaseFailed = true; releaseExceptionType = error.GetType().Name; }
        }
        private void ReleaseWorld(ref World? world)
        {
            if (world == null) return;
            Check(world.IsCreated, "UNEXPECTED_WORLD_RELEASE");
            world.Dispose();
            Check(!world.IsCreated, "WORLD_RELEASE_UNCONFIRMED");
            world = null; disposedWorlds++;
        }
        private void ReleaseBuffer(ref EntityCommandBuffer buffer)
        {
            try
            {
                buffer.Dispose();
                Check(!buffer.IsCreated, "BUFFER_RELEASE_UNCONFIRMED");
            }
            catch (Exception error)
            { nativeReleaseFailed = true; releaseExceptionType = error.GetType().Name; throw; }
        }
        private World CreateOwnedWorld(string name)
        {
            try { return new World(name, WorldFlags.None); }
            catch { constructorUncertain = true; throw; }
        }
        private static World[] CaptureWorldMembers()
        {
            // This SDK's NoAllocReadOnlyCollection explicitly rejects boxing
            // through IEnumerable; LINQ enumeration is not supported.
            var worlds = World.All;
            int count = Math.Min(65, worlds.Count);
            var result = new World[count];
            for (int i = 0; i < count; i++) result[i] = worlds[i];
            return result;
        }
        internal JObject Read() => new JObject
        {
            ["kind"] = "definitionContainer", ["ran"] = ran,
            ["passed"] = checksPassed && ResourcesReleased && defaultUnchanged && membersUnchanged && failureCode == null && releaseExceptionType == null,
            ["resourcesReleased"] = ResourcesReleased, ["createdWorlds"] = createdWorlds, ["disposedWorlds"] = disposedWorlds,
            ["constructorUncertain"] = constructorUncertain, ["nativeReleaseFailed"] = nativeReleaseFailed,
            ["sourceSystems"] = sourceSystems, ["targetSystems"] = targetSystems,
            ["sourceOwnsReference"] = sourceOwnsReference, ["targetRejectsExternalReference"] = targetRejectsExternal,
            ["sourceDataUnchanged"] = sourceUnchanged, ["deferredComponentFixed"] = componentFixed,
            ["positiveComponentRetained"] = componentExternalRetained, ["discardUnpublished"] = discardUnpublished,
            ["defaultWorldUnchanged"] = defaultUnchanged, ["worldMembersUnchanged"] = membersUnchanged,
            ["elapsedMilliseconds"] = elapsedMilliseconds,
            ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType), ["releaseExceptionType"] = Value(releaseExceptionType),
            ["scope"] = "owned synthetic component container only; not buffers, native generator, qualification, preview, construction, sandbox or performance acceptance"
        };
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void Check(bool condition, string code) { if (!condition) throw new ProbeCheckFailure(code); }
        private sealed class ProbeCheckFailure : Exception
        { internal ProbeCheckFailure(string code) { Code = code; } internal string Code { get; } }
        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Container probe requires the main thread."); }
    }
}
#endif
