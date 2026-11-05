#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Tools;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Fixed Game-kernel patch observation; metadata keys are never invoked.</summary>
    internal static class ObjectDefinitionPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(ObjectToolBaseSystem);
                Type? job = parent.GetNestedType("CreateDefinitionsJob", BindingFlags.NonPublic);
                Type? objectHandles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                Type? toolHandles = typeof(ToolBaseSystem).GetNestedType("TypeHandle", BindingFlags.NonPublic);
                if (job == null || !job.IsValueType || objectHandles == null || toolHandles == null)
                    return new DefinitionProducerPatchProbe();
                var methods = new List<MethodBase>();
                bool entry = false;
                foreach (Type type in new[] { parent, job, objectHandles, toolHandles,
                    typeof(ToolOutputBarrier), typeof(Game.SafeCommandBufferSystem) })
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        methods.Add(method);
                        entry |= type == job && method.Name == "Execute" && method.IsPublic && !method.IsStatic &&
                            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                    }
                    methods.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
                }
                foreach (MethodInfo method in typeof(ToolBaseSystem).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (method.Name == "OnCreateForCompiler" || method.Name == "__AssignQueries" ||
                        method.Name == "OnUpdate" && method.ReturnType == typeof(void)) methods.Add(method);
                methods.AddRange(typeof(ToolBaseSystem).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), entry, "OBJECT_DEFINITION");
            }
            catch (Exception) { return new DefinitionProducerPatchProbe(); }
            // Limited already-loaded Harmony observation, not a full call-graph
            // integrity check, native-detour detector or lock against new patches.
        }
    }
}
#endif
