#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    internal static class PreviewCancelToolPatchProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(ToolClearSystem);
                if (parent.Module.ModuleVersionId.ToString("N") != GameMvid
                    || typeof(EntityCommandBuffer).Module.ModuleVersionId.ToString("N") != EntitiesMvid)
                    return Unqualified("PREVIEW_CANCEL_TOOL_METADATA_UNQUALIFIED");

                var methods = new List<MethodBase>();
                AddDeclared(methods, parent);
                foreach (string name in new[] { "TypeHandle", "ClearEntitiesJob" })
                {
                    Type? type = parent.GetNestedType(name, BindingFlags.NonPublic);
                    if (type == null || !type.IsValueType) return Unqualified("PREVIEW_CANCEL_TOOL_METADATA_UNQUALIFIED");
                    AddDeclared(methods, type);
                }
                bool found = AddMethod(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer));
                var game = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_CANCEL_TOOL");
                if (game.Qualified && !ObserveEntitiesMethods(game.HarmonyMvid))
                    return Unqualified("PREVIEW_CANCEL_TOOL_ECS_PATCH_UNQUALIFIED");
                return game;
            }
            catch (Exception) { return Unqualified("PREVIEW_CANCEL_TOOL_METADATA_UNQUALIFIED"); }
        }

        private static bool ObserveEntitiesMethods(string harmonyMvid)
        {
            var commands = typeof(EntityCommandBuffer);
            var barrier = typeof(EntityCommandBufferSystem);
            var methods = new List<MethodBase>();
            if (!AddMethod(methods, barrier, "CreateCommandBuffer", typeof(EntityCommandBuffer))
                || !AddMethod(methods, barrier, "AddJobHandleForProducer", typeof(void), typeof(JobHandle))
                || !AddMethod(methods, barrier, "OnDestroy", typeof(void))
                || !AddMethod(methods, barrier, "FlushPendingBuffers", typeof(void), typeof(bool))
                || !AddMethod(methods, commands, "Playback", typeof(void), typeof(EntityManager))
                || !AddMethod(methods, typeof(EntityManager), "DestroyEntity", typeof(void), typeof(NativeArray<Entity>))
                || !AddMethod(methods, commands, "Dispose", typeof(void))) return false;

            MethodInfo? bulk = FindBulkAdd();
            if (bulk == null) return false;
            methods.Add(bulk);
            methods.Add(bulk.MakeGenericMethod(typeof(Deleted)));
            var abi = new ObjectDefinitionCommandProbe.Abi();
            methods.Add(abi.Constructor);
            var getter = abi.Commands.GetGetMethod(false);
            if (getter == null) return false;
            methods.Add(getter);

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

        private static MethodInfo? FindBulkAdd()
        {
            MethodInfo? found = null;
            foreach (MethodInfo method in typeof(EntityCommandBuffer.ParallelWriter).GetMethods(Declared))
            {
                if (method.Name != "AddComponent" || !method.IsPublic || method.IsStatic || !method.IsGenericMethodDefinition
                    || method.GetGenericArguments().Length != 1 || method.ReturnType != typeof(void)) continue;
                ParameterInfo[] args = method.GetParameters();
                if (args.Length != 2 || args[0].ParameterType != typeof(int) || args[1].ParameterType != typeof(NativeArray<Entity>)) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
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
