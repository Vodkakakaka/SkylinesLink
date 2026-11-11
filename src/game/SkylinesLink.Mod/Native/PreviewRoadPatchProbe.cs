#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Audio;
using Game.Buildings;
using Game.Common;
using Game.Effects;
using Game.Notifications;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;
using NetSearch = Game.Net.SearchSystem;
using ObjectSearch = Game.Objects.SearchSystem;

namespace SkylinesLink.Mod.Native
{
    internal static class PreviewRoadPatchProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const BindingFlags Nested = BindingFlags.Public | BindingFlags.NonPublic;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(RoadConnectionSystem);
                if (parent.Module.ModuleVersionId.ToString("N") != GameMvid
                    || typeof(EntityCommandBufferSystem).Module.ModuleVersionId.ToString("N") != EntitiesMvid)
                    return Unqualified("PREVIEW_ROAD_METADATA_UNQUALIFIED");

                var methods = new List<MethodBase>();
                AddDeclared(methods, parent);
                foreach (string name in new[] { "TypeHandle", "CheckRoadConnectionJob", "FillReplacementListJob",
                    "ReplaceRoad", "FindRoadConnectionJob", "ReplaceRoadConnectionJob", "UpdateSecondaryLanesJob" })
                {
                    Type? type = parent.GetNestedType(name, Nested);
                    if (type == null || !type.IsValueType) return Unqualified("PREVIEW_ROAD_METADATA_UNQUALIFIED");
                    AddDeclared(methods, type);
                    if (name == "CheckRoadConnectionJob" || name == "FindRoadConnectionJob")
                    {
                        string iteratorName = name == "CheckRoadConnectionJob" ? "CheckRoadConnectionIterator" : "FindRoadConnectionIterator";
                        Type? iterator = type.GetNestedType(iteratorName, Nested);
                        if (iterator == null || !iterator.IsValueType) return Unqualified("PREVIEW_ROAD_METADATA_UNQUALIFIED");
                        AddDeclared(methods, iterator);
                    }
                }

                Type dependency = typeof(JobHandle).MakeByRefType();
                Type tree = typeof(NativeQuadTree<Entity, QuadTreeBoundsXZ>);
                bool found = AddMethod(methods, typeof(BuildingUtils), "CalculateFrontPosition", typeof(float3), typeof(Transform), typeof(int))
                    && AddMethod(methods, typeof(NetSearch), "GetNetSearchTree", tree, typeof(bool), dependency)
                    && AddMethod(methods, typeof(NetSearch), "AddNetSearchTreeReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(ObjectSearch), "GetStaticSearchTree", tree, typeof(bool), dependency)
                    && AddMethod(methods, typeof(ObjectSearch), "AddStaticSearchTreeReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer))
                    && AddMethod(methods, typeof(AudioManager), "GetSourceUpdateData", typeof(SourceUpdateData), dependency)
                    && AddMethod(methods, typeof(SourceUpdateData), "AddSnap", typeof(void))
                    && AddMethod(methods, typeof(IconCommandSystem), "CreateCommandBuffer", typeof(IconCommandBuffer))
                    && AddMethod(methods, typeof(IconCommandSystem), "AddCommandBufferWriter", typeof(void), typeof(JobHandle));
                var game = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_ROAD");
                if (game.Qualified && !ObserveBarrierMethods(game.HarmonyMvid))
                    return Unqualified("PREVIEW_ROAD_ECS_BARRIER_PATCH_UNQUALIFIED");
                return game;
            }
            catch (Exception) { return Unqualified("PREVIEW_ROAD_METADATA_UNQUALIFIED"); }
        }

        private static bool ObserveBarrierMethods(string harmonyMvid)
        {
            var type = typeof(EntityCommandBufferSystem);
            var methods = new List<MethodBase>();
            if (!AddMethod(methods, type, "CreateCommandBuffer", typeof(EntityCommandBuffer))
                || !AddMethod(methods, type, "AddJobHandleForProducer", typeof(void), typeof(JobHandle))
                || !AddMethod(methods, type, "OnDestroy", typeof(void))
                || !AddMethod(methods, type, "FlushPendingBuffers", typeof(void), typeof(bool)))
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
                foreach (MethodBase key in methods)
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

        private static DefinitionProducerPatchProbe Unqualified(string reason) => new DefinitionProducerPatchProbe { Reason = reason };
    }
}
#endif
