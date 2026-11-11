#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.City;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    internal static class PreviewCommitPaymentPatchProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            Type native = typeof(ToolApplySystem);
            if (native.Module.ModuleVersionId.ToString("N") != GameMvid
                || typeof(EntityCommandBuffer).Module.ModuleVersionId.ToString("N") != EntitiesMvid)
                return Unqualified();

            var methods = new List<MethodBase>();
            AddDeclared(methods, native);
            foreach (string name in new[] { "TypeHandle", "ApplyEntitiesJob" })
            {
                Type? nested = native.GetNestedType(name, BindingFlags.NonPublic);
                if (nested == null || !nested.IsValueType) return Unqualified();
                AddDeclared(methods, nested);
            }

            // These game methods are on the actual funds/write and buffer
            // acquisition paths. The fixed observation does not patch them.
            if (!AddMethod(methods, typeof(PlayerMoney), "Subtract", typeof(void), typeof(int))
                || !AddMethod(methods, typeof(PlayerMoney), "Add", typeof(void), typeof(int))
                || !AddMethod(methods, typeof(PlayerMoney), "get_money", typeof(int))
                || !AddMethod(methods, typeof(CitySystem), "get_City", typeof(Entity))
                || !AddMethod(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer)))
                return Unqualified();

            return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "PREVIEW_COMMIT_PAYMENT");
        }

        private static void AddDeclared(List<MethodBase> methods, Type type)
        {
            methods.AddRange(type.GetMethods(Declared));
            methods.AddRange(type.GetConstructors(Declared));
        }

        private static bool AddMethod(List<MethodBase> methods, Type type, string name, Type result, params Type[] parameters)
        {
            MethodInfo? method = type.GetMethod(name, Declared, null, parameters, null);
            if (method == null || method.ReturnType != result || method.IsStatic) return false;
            methods.Add(method);
            return true;
        }

        private static DefinitionProducerPatchProbe Unqualified() =>
            new DefinitionProducerPatchProbe { Reason = "PREVIEW_COMMIT_PAYMENT_METADATA_UNQUALIFIED" };
    }
}
#endif
