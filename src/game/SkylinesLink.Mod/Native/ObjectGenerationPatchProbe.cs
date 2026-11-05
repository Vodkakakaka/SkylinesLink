#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Tools;

namespace SkylinesLink.Mod.Native
{
    // This role has a different native entry and jobs from definition creation.
    // Metadata is observed through the existing bounded Harmony reader only.
    internal static class ObjectGenerationPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(GenerateObjectsSystem);
                var types = new List<Type> { parent, typeof(Game.Common.ModificationBarrier1), typeof(Game.SafeCommandBufferSystem) };
                foreach (string name in new[] { "FillOldObjectsJob", "FillCreationListJob", "CollectCreationDataJob", "CreateObjectsJob", "TypeHandle" })
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
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), entry, "OBJECT_GENERATION");
            }
            catch (Exception) { return new DefinitionProducerPatchProbe(); }
        }
    }
}
#endif
