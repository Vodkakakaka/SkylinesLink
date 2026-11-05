#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Tools;

namespace SkylinesLink.Mod.Native
{
    // Fixed metadata observation for the separate node consumer. This neither
    // executes private methods nor claims complete detour/call-graph coverage.
    internal static class NodeGenerationPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(GenerateNodesSystem);
                var types = new List<Type> { parent, typeof(Game.Common.ModificationBarrier1), typeof(Game.SafeCommandBufferSystem) };
                foreach (string name in new[] { "FillNodeMapJob", "FillOldNodesJob", "CollectUpdatesJob", "CreateNodesJob", "TypeHandle" })
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
                        entry |= type == parent && method.Name == "OnUpdate" && !method.IsStatic &&
                            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                    }
                    methods.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
                }
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), entry, "NODE_GENERATION");
            }
            catch (Exception) { return new DefinitionProducerPatchProbe(); }
        }
    }
}
#endif
