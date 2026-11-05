#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Tools;

namespace SkylinesLink.Mod.Native
{
    // The resolver writes Owner before its ECB is inspected. Qualify this
    // separate native entry and shared job before allowing that write.
    internal static class NodeOwnerResolutionPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(FindOwnersSystem2);
                Type? handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                Type? writer = typeof(FindOwnersSystem).GetNestedType("SetSubEntityOwnerJob", BindingFlags.Public | BindingFlags.NonPublic);
                if (handles == null || !handles.IsValueType || writer == null || !writer.IsValueType)
                    return new DefinitionProducerPatchProbe();
                var methods = new List<MethodBase>();
                bool entry = false;
                foreach (Type type in new[] { parent, handles, writer, typeof(Game.Common.ModificationBarrier2B), typeof(Game.SafeCommandBufferSystem) })
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
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), entry, "NODE_OWNER_RESOLUTION");
            }
            catch (Exception) { return new DefinitionProducerPatchProbe(); }
        }
    }
}
#endif
