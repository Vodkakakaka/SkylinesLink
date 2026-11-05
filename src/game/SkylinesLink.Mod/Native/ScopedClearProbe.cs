#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using System.Reflection;
using Colossal.Core;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A native consumer experiment over synthetic data in one owned World.
    // No source/player entity, game lifecycle, fee or normal Cancel claim.
    internal sealed class ScopedClearProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        public struct Scope : ISharedComponentData, IEquatable<Scope>
        {
            public int Value;
            public bool Equals(Scope other) => Value == other.Value;
            public override bool Equals(object? value) => value is Scope other && Equals(other);
            public override int GetHashCode() => Value;
        }
        public struct Sentinel : IComponentData { public int Value; }

        private readonly World? source;
        private World? owned;
        private Clear? clear;
        private ToolOutputBarrier? barrier;
        private bool ran, checksPassed, selectionVerified, firstMarked, secondUnmarked, secondMarked;
        private bool playerPreserved, otherScopePreserved, ordinaryEmpty, scopedRequiredRuns;
        private bool defaultUnchanged, worldsUnchanged, sourceSystemsUnchanged;
        private bool constructorUncertain, nativeUncertain, releaseAttempted, released;
        private string? failureCode, exceptionType, releaseExceptionType;
        private int ordinaryBefore, ordinarySecond, selectedBefore, systems;
        private double elapsed;
        private DefinitionProducerPatchProbe patch = new DefinitionProducerPatchProbe();

        internal ScopedClearProbe(World? source) { this.source = source; }
        internal bool ResourcesReleased => released && !constructorUncertain && !nativeUncertain && releaseExceptionType == null;

        internal void Run()
        {
            RequireMainThread(); Check(!ran, "SCOPED_CLEAR_ALREADY_RAN"); ran = true;
            var timer = Stopwatch.StartNew();
            World? previousDefault = World.DefaultGameObjectInjectionWorld;
            World[] beforeWorlds = Array.Empty<World>();
            ComponentSystemBase[] beforeSystems = Array.Empty<ComponentSystemBase>();
            try
            {
                Check(typeof(ToolClearSystem).Assembly.ManifestModule.ModuleVersionId.ToString("N") == GameMvid
                    && typeof(World).Assembly.ManifestModule.ModuleVersionId.ToString("N") == EntitiesMvid, "NATIVE_BUILD_UNQUALIFIED");
                beforeWorlds = CaptureWorlds();
                Check(beforeWorlds.Length <= 64 && source != null && source.IsCreated, "SOURCE_OBSERVATION_UNAVAILABLE");
                beforeSystems = CaptureSystems(source!); Check(beforeSystems.Length <= 4096, "SOURCE_SYSTEM_BUDGET");
                patch = DefinitionProducerPatchProbe.ObserveScopedClear(); Check(patch.Qualified, patch.Reason);
                try { owned = new World("SkylinesLink owned clear fixture", WorldFlags.None); }
                catch { constructorUncertain = true; throw; }
                Check(!ReferenceEquals(owned, source) && !ReferenceEquals(owned, previousDefault), "FIXTURE_WORLD_NOT_PRIVATE");
                try { barrier = owned.CreateSystemManaged<ToolOutputBarrier>(); clear = owned.CreateSystemManaged<Clear>(); }
                catch { constructorUncertain = true; throw; }
                systems = owned.Systems.Count;
                Check(systems == 2 && ReferenceEquals(barrier.World, owned) && ReferenceEquals(clear.World, owned), "UNEXPECTED_FIXTURE_SYSTEMS");
                var manager = owned.EntityManager;
                Entity selected = Make(manager, 17, 1, true);
                Entity other = Make(manager, 23, 2, true);
                Entity player = Make(manager, 31, 0, false);
                using (var ordinary = manager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
                {
                    ordinaryBefore = ordinary.CalculateEntityCount(); selectedBefore = clear.SelectedCount;
                    selectionVerified = ordinaryBefore == 1 && selectedBefore == 1 && clear.BindingVerified;
                    Check(selectionVerified && !manager.HasComponent<Game.Common.Deleted>(selected), "INITIAL_FIXTURE_SELECTION");
                    ExecuteClear();
                    firstMarked = manager.HasComponent<Game.Common.Deleted>(selected);
                    Check(firstMarked, "FIRST_CLEAR_NOT_MARKED");
                    playerPreserved = Preserved(manager, player, 31);
                    otherScopePreserved = Preserved(manager, other, 23);
                    Check(playerPreserved && otherScopePreserved, "UNSELECTED_FIXTURE_CHANGED");

                    // Test setup only: remove the first result and the plain
                    // surrogate's Temp, so no ordinary Temp can drive Update.
                    manager.RemoveComponent<Game.Common.Deleted>(selected);
                    manager.RemoveComponent<Temp>(player);
                    secondUnmarked = !manager.HasComponent<Game.Common.Deleted>(selected);
                    ordinarySecond = ordinary.CalculateEntityCount(); ordinaryEmpty = ordinarySecond == 0;
                    scopedRequiredRuns = clear.ShouldRunSystem();
                    Check(secondUnmarked && ordinaryEmpty && scopedRequiredRuns && clear.SelectedCount == 1, "SCOPED_REQUIRED_QUERY_NOT_ACTIVE");
                    ExecuteClear();
                    secondMarked = manager.HasComponent<Game.Common.Deleted>(selected);
                    playerPreserved &= manager.Exists(player) && manager.GetComponentData<Sentinel>(player).Value == 31
                        && !manager.HasComponent<Temp>(player) && !manager.HasComponent<Game.Common.Deleted>(player);
                    otherScopePreserved &= Preserved(manager, other, 23);
                    Check(secondMarked && playerPreserved && otherScopePreserved, "SECOND_CLEAR_SCOPE_MISMATCH");
                    Check(manager.GetComponentData<Sentinel>(selected).Value == 17 && manager.GetComponentData<Temp>(selected).m_Original == Entity.Null,
                        "SELECTED_FIXTURE_DATA_CHANGED");
                    var afterPatch = DefinitionProducerPatchProbe.ObserveScopedClear();
                    Check(SamePatch(afterPatch), "CLEAR_PATCH_OBSERVATION_CHANGED");
                    checksPassed = true;
                }
            }
            catch (CheckFailure error) { failureCode = error.Code; exceptionType = error.GetType().Name; }
            catch (Exception error) { failureCode = "SCOPED_CLEAR_FAILED"; exceptionType = error.GetType().Name; }
            finally
            {
                Release();
                try
                {
                    defaultUnchanged = ReferenceEquals(previousDefault, World.DefaultGameObjectInjectionWorld);
                    worldsUnchanged = Same(beforeWorlds, CaptureWorlds());
                    sourceSystemsUnchanged = source != null && source.IsCreated && Same(beforeSystems, CaptureSystems(source));
                }
                catch (Exception error) { failureCode = failureCode ?? "FINAL_SOURCE_OBSERVATION_FAILED"; exceptionType = error.GetType().Name; }
                timer.Stop(); elapsed = timer.Elapsed.TotalMilliseconds;
            }
        }

        private void ExecuteClear()
        {
            Check(clear != null && barrier != null && owned != null && owned.IsCreated
                && clear.BindingVerified && ReferenceEquals(clear.World, owned) && ReferenceEquals(barrier.World, owned), "CLEAR_OWNER_LOST");
            Check(SamePatch(DefinitionProducerPatchProbe.ObserveScopedClear()), "CLEAR_PATCH_OBSERVATION_CHANGED");
            try
            {
                barrier!.AllowUsage();
                clear!.Update(); clear.Join();
                barrier.Update();
            }
            catch { nativeUncertain = true; throw; }
        }
        private bool SamePatch(DefinitionProducerPatchProbe observed) => observed.Qualified && patch.Qualified
            && observed.HarmonyMvid == patch.HarmonyMvid && observed.MethodsChecked == patch.MethodsChecked
            && observed.MethodSetSha256 == patch.MethodSetSha256 && observed.PatchedMethods == 0;
        private static Entity Make(EntityManager manager, int value, int scope, bool disabled)
        {
            Entity entity = manager.CreateEntity();
            manager.AddComponentData(entity, default(Temp));
            manager.AddComponentData(entity, new Sentinel { Value = value });
            if (scope != 0) manager.AddSharedComponentManaged(entity, new Scope { Value = scope });
            if (disabled) manager.AddComponent<Unity.Entities.Disabled>(entity);
            return entity;
        }
        private static bool Preserved(EntityManager manager, Entity entity, int value) => manager.Exists(entity)
            && manager.GetComponentData<Sentinel>(entity).Value == value && manager.HasComponent<Temp>(entity)
            && manager.GetComponentData<Temp>(entity).m_Original == Entity.Null && !manager.HasComponent<Game.Common.Deleted>(entity);

        internal void Release()
        {
            RequireMainThread(); if (releaseAttempted || nativeUncertain) return; releaseAttempted = true;
            try
            {
                if (owned != null) { Check(owned.IsCreated, "FIXTURE_OWNER_LOST"); owned.Dispose(); Check(!owned.IsCreated, "FIXTURE_RELEASE_UNCONFIRMED"); owned = null; }
                clear = null; barrier = null; released = !constructorUncertain;
            }
            catch (Exception error) { nativeUncertain = true; releaseExceptionType = error.GetType().Name; }
        }
        internal JObject Read() => new JObject
        {
            ["kind"] = "scopedClear", ["ran"] = ran,
            ["passed"] = checksPassed && ResourcesReleased && defaultUnchanged && worldsUnchanged && sourceSystemsUnchanged && failureCode == null,
            ["resourcesReleased"] = ResourcesReleased, ["constructorUncertain"] = constructorUncertain, ["nativeUncertain"] = nativeUncertain,
            ["systems"] = systems, ["ordinaryBefore"] = ordinaryBefore, ["selectedBefore"] = selectedBefore, ["ordinarySecond"] = ordinarySecond,
            ["bindingVerified"] = selectionVerified, ["firstMarked"] = firstMarked, ["secondUnmarked"] = secondUnmarked, ["secondMarked"] = secondMarked,
            ["ordinaryEmpty"] = ordinaryEmpty, ["scopedRequiredRuns"] = scopedRequiredRuns,
            ["playerSurrogatePreserved"] = playerPreserved, ["otherScopePreserved"] = otherScopePreserved,
            ["defaultUnchanged"] = defaultUnchanged, ["worldMembersUnchanged"] = worldsUnchanged, ["sourceSystemsUnchanged"] = sourceSystemsUnchanged,
            ["harmony"] = new JObject { ["qualifiedObservation"] = patch.Qualified, ["reason"] = patch.Reason, ["methods"] = patch.MethodsChecked,
                ["patchedMethods"] = patch.PatchedMethods, ["mvid"] = patch.HarmonyMvid, ["methodSetSha256"] = patch.MethodSetSha256 },
            ["elapsedMilliseconds"] = elapsed, ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType), ["releaseExceptionType"] = Value(releaseExceptionType),
            ["scope"] = "synthetic private-World native Clear only; no GameSystemBase callbacks, source entities, normal Cancel, shared-reference writes, validation, fees or Apply acceptance"
        };

        [DisableAutoCreation]
        public sealed partial class Clear : ToolClearSystem
        {
            private EntityQuery selected;
            private ToolOutputBarrier? output;
            internal bool BindingVerified => output != null && ReferenceEquals(output.World, World)
                && QueryField().GetValue(this) is EntityQuery actual && actual.Equals(selected)
                && ReferenceEquals(BarrierField().GetValue(this), output);
            internal int SelectedCount => selected.CalculateEntityCount();
            protected override void OnCreate()
            {
                // This fixture intentionally has no game callbacks. The ECS
                // creation/compiler hooks still run normally; only this own
                // instance receives the two closed native field bindings.
                output = World.GetExistingSystemManaged<ToolOutputBarrier>();
                Check(output != null && !ReferenceEquals(World, World.DefaultGameObjectInjectionWorld), "CLEAR_PRIVATE_WORLD_REQUIRED");
                selected = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Scope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                selected.SetSharedComponentFilter(new Scope { Value = 1 });
                QueryField().SetValue(this, selected); BarrierField().SetValue(this, output);
                RequireForUpdate(selected);
                Check(BindingVerified, "CLEAR_BINDING_NOT_CONFIRMED");
            }
            protected override void OnDestroy() { }
            internal void Join() { Dependency.Complete(); Dependency = default; }
            private static FieldInfo QueryField() => Field("m_ClearQuery", typeof(EntityQuery));
            private static FieldInfo BarrierField() => Field("m_ToolOutputBarrier", typeof(ToolOutputBarrier));
            private static FieldInfo Field(string name, Type type)
            {
                var field = typeof(ToolClearSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Check(field != null && field.DeclaringType == typeof(ToolClearSystem) && field.FieldType == type && !field.IsStatic && !field.IsInitOnly,
                    "CLEAR_FIELD_ABI_UNQUALIFIED");
                return field!;
            }
        }
        private static World[] CaptureWorlds() { var list = World.All; var result = new World[Math.Min(65, list.Count)]; for (int i = 0; i < result.Length; i++) result[i] = list[i]; return result; }
        private static ComponentSystemBase[] CaptureSystems(World world) { var list = world.Systems; var result = new ComponentSystemBase[Math.Min(4097, list.Count)]; for (int i = 0; i < result.Length; i++) result[i] = list[i]; return result; }
        private static bool Same<T>(T[] a, T[] b) where T : class { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false; return true; }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Main thread required."); }
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private sealed class CheckFailure : Exception { internal string Code { get; } internal CheckFailure(string code) { Code = code; } }
    }
}
#endif
