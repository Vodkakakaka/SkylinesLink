#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Fixed metadata and already-loaded Harmony observations for the one native
    // node branch. No patch body or private native method is invoked here.
    internal static class NodeReferencesPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(Game.Net.ReferencesSystem);
                if (!NativeConstructionBuild.Game(parent.Module)) return Unqualified();
                Type? handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                Type? job = parent.GetNestedType("UpdateNodeReferencesJob", BindingFlags.NonPublic);
                if (handles == null || job == null || !handles.IsValueType || !job.IsValueType
                    || handles.DeclaringType != parent || job.DeclaringType != parent) return Unqualified();
                if (!VoidMethod(parent, "OnCreate", Type.EmptyTypes) || !VoidMethod(parent, "OnUpdate", Type.EmptyTypes)
                    || !VoidMethod(parent, "OnCreateForCompiler", Type.EmptyTypes)
                    || !VoidMethod(parent, "__AssignQueries", new[] { typeof(SystemState).MakeByRefType() })
                    || !VoidMethod(handles, "__AssignHandles", new[] { typeof(SystemState).MakeByRefType() })) return Unqualified();
                bool execute = false, wrapper = false;
                var methods = new List<MethodBase>();
                foreach (Type type in new[] { parent, handles, job })
                {
                    foreach (MethodInfo method in type.GetMethods(Declared))
                    {
                        methods.Add(method);
                        if (type != job || !ChunkExecute(method)) continue;
                        execute |= method.Name == "Execute" && method.IsPublic;
                        wrapper |= method.Name == "Unity.Entities.IJobChunk.Execute" && method.IsPrivate;
                    }
                    methods.AddRange(type.GetConstructors(Declared));
                }
                if (!execute || !wrapper) return Unqualified();
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "NODE_REFERENCES");
            }
            catch (Exception) { return Unqualified(); }
        }

        private static bool VoidMethod(Type type, string name, Type[] parameters)
        {
            MethodInfo? method = type.GetMethod(name, Declared, null, parameters, null);
            return method != null && method.DeclaringType == type && !method.IsStatic && method.ReturnType == typeof(void);
        }

        private static bool ChunkExecute(MethodInfo method)
        {
            if (method.IsStatic || method.ReturnType != typeof(void)) return false;
            ParameterInfo[] args = method.GetParameters();
            return args.Length == 4 && args[0].ParameterType == typeof(ArchetypeChunk).MakeByRefType()
                && args[1].ParameterType == typeof(int) && args[2].ParameterType == typeof(bool)
                && args[3].ParameterType.FullName == "Unity.Burst.Intrinsics.v128&";
        }

        private static DefinitionProducerPatchProbe Unqualified() => new DefinitionProducerPatchProbe {
            Reason = "NODE_REFERENCES_METADATA_UNQUALIFIED"
        };
    }
}
#endif
