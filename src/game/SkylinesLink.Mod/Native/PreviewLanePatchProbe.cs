#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    internal static class PreviewLanePatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(LaneSystem);
                if (!NativeConstructionBuild.Game(parent.Module)
                    || !NativeConstructionBuild.Entities(typeof(EntityCommandBufferSystem).Module))
                    return Unqualified("PREVIEW_LANE_METADATA_UNQUALIFIED");

                var methods = new List<MethodBase>();
                AddDeclared(methods, parent);
                foreach (string name in new[] { "TypeHandle", "UpdateLanesJob", "LaneBuffer", "SourcePositionComparer", "TargetPositionComparer" })
                {
                    Type? nested = parent.GetNestedType(name, BindingFlags.NonPublic);
                    if (nested == null || !nested.IsValueType) return Unqualified("PREVIEW_LANE_METADATA_UNQUALIFIED");
                    AddDeclared(methods, nested);
                }
                bool found = AddMethod(methods, typeof(LaneReferencesSystem), "GetSkipLaneQueue", typeof(Unity.Collections.NativeQueue<Lane>))
                    && AddMethod(methods, typeof(LaneReferencesSystem), "AddSkipLaneWriter", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer))
                    && AddMethod(methods, typeof(RandomSeed), "Next", typeof(RandomSeed))
                    && AddMethod(methods, typeof(PseudoRandomSeed), "GetRandom", typeof(Unity.Mathematics.Random), typeof(uint))
                    && AddMethod(methods, typeof(CityConfigurationSystem), "get_leftHandTraffic", typeof(bool))
                    && AddMethod(methods, typeof(CityConfigurationSystem), "get_defaultTheme", typeof(Entity))
                    && AddMethod(methods, typeof(ToolSystem), "get_actionMode", typeof(GameMode))
                    && AddMethod(methods, typeof(GameModeExtensions), "IsEditor", typeof(bool), typeof(GameMode))
                    && AddMethod(methods, typeof(TerrainSystem), "GetHeightData", typeof(TerrainHeightData), typeof(bool))
                    && AddMethod(methods, typeof(TerrainSystem), "AddCPUHeightReader", typeof(void), typeof(JobHandle));
                var game = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_LANE");
                if (game.Qualified && !ObserveBarrierMethods(game.HarmonyMvid))
                    return Unqualified("PREVIEW_LANE_ECS_BARRIER_PATCH_UNQUALIFIED");
                return game;
            }
            catch (Exception) { return Unqualified("PREVIEW_LANE_METADATA_UNQUALIFIED"); }
        }

        // The shared Game observer intentionally admits only its existing
        // modules. These two inherited Unity.Entities keys are fixed here.
        private static bool ObserveBarrierMethods(string harmonyMvid)
        {
            var type = typeof(EntityCommandBufferSystem);
            var create = type.GetMethod("CreateCommandBuffer", Declared, null, Type.EmptyTypes, null);
            var addWriter = type.GetMethod("AddJobHandleForProducer", Declared, null, new[] { typeof(JobHandle) }, null);
            if (create == null || !create.IsPublic || create.IsStatic || create.ReturnType != typeof(EntityCommandBuffer)
                || addWriter == null || !addWriter.IsPublic || addWriter.IsStatic || addWriter.ReturnType != typeof(void))
                return false;
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (loaded.Length > 1024) return false;
            foreach (Assembly assembly in loaded)
            {
                if (assembly.GetName().Name != "0Harmony"
                    || assembly.ManifestModule.ModuleVersionId.ToString("N") != harmonyMvid) continue;
                Type? patches = assembly.GetType("HarmonyLib.Patches", false);
                var getInfo = assembly.GetType("HarmonyLib.Harmony", false)?.GetMethod("GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
                if (patches == null || getInfo == null || getInfo.ReturnType != patches) return false;
                foreach (MethodInfo key in new[] { create, addWriter })
                {
                    object? info = getInfo.Invoke(null, new object[] { key });
                    if (info == null) continue;
                    if (info.GetType() != patches) return false;
                    foreach (string name in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                        if (!(patches.GetField(name, Declared)?.GetValue(info) is ICollection group) || group.Count != 0)
                            return false;
                }
                return true;
            }
            return false;
        }

        private static void AddDeclared(List<MethodBase> methods, Type type)
        {
            methods.AddRange(type.GetMethods(Declared));
            methods.AddRange(type.GetConstructors(Declared));
        }

        private static bool AddMethod(List<MethodBase> methods, Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, Declared, null, parameters, null);
            if (method == null || method.ReturnType != result) return false;
            methods.Add(method);
            return true;
        }

        private static DefinitionProducerPatchProbe Unqualified(string reason) => new DefinitionProducerPatchProbe {
            Reason = reason
        };
    }
}
#endif
