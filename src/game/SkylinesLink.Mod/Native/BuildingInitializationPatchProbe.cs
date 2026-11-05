#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkylinesLink.Mod.Native
{
    internal static class BuildingInitializationPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(Game.Buildings.InitializeSystem);
                var types = new List<Type> { parent, typeof(Game.Common.ModificationBarrier2), typeof(Game.SafeCommandBufferSystem) };
                foreach (string name in new[] { "InitializeBuildingsJob", "InitializeCoverageTypeJob", "TypeHandle" })
                {
                    Type? nested = parent.GetNestedType(name, BindingFlags.NonPublic);
                    if (nested == null || !nested.IsValueType) return new DefinitionProducerPatchProbe();
                    types.Add(nested);
                }
                var methods = new List<MethodBase>();
                bool entry = false;
                foreach (Type type in types)
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        methods.Add(method);
                        entry |= type == parent && method.Name == "OnUpdate" && !method.IsStatic
                            && method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                    }
                    methods.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
                }
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), entry, "BUILDING_INITIALIZATION");
            }
            catch (Exception) { return new DefinitionProducerPatchProbe(); }
        }
    }
}
#endif
