#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Fixed native metadata and already-loaded Harmony observations. This does
    // not invoke the private job or establish a complete native call graph.
    internal static class SubObjectReferencesPatchProbe
    {
        internal static DefinitionProducerPatchProbe Observe() => NativeReferencesPatchObservation.ObserveSubObjects();
    }

    // Exactly two native consumers share this metadata procedure. Callers
    // cannot supply an arbitrary consumer, job, buffer type or helper name.
    internal static class NativeReferencesPatchObservation
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe ObserveSubObjects() => Observe(false);
        internal static DefinitionProducerPatchProbe ObserveSubNets() => Observe(true);

        private static DefinitionProducerPatchProbe Observe(bool subNets)
        {
            string kind = subNets ? "SUBNET_REFERENCES" : "SUBOBJECT_REFERENCES";
            try
            {
                Type parent = subNets ? typeof(Game.Net.SubNetReferencesSystem) : typeof(Game.Objects.SubObjectReferencesSystem);
                Type element = subNets ? typeof(Game.Net.SubNet) : typeof(Game.Objects.SubObject);
                if (!NativeConstructionBuild.Game(parent.Module)
                    || !NativeConstructionBuild.Entities(typeof(World).Module)) return Unqualified(kind);
                Type? handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                Type? job = parent.GetNestedType(subNets ? "UpdateSubNetReferencesJob" : "UpdateSubObjectReferencesJob", BindingFlags.NonPublic);
                if (handles == null || job == null || !handles.IsValueType || !job.IsValueType
                    || handles.DeclaringType != parent || job.DeclaringType != parent) return Unqualified(kind);
                if (!VoidMethod(parent, "OnCreate", Type.EmptyTypes) || !VoidMethod(parent, "OnUpdate", Type.EmptyTypes)
                    || !VoidMethod(parent, "OnCreateForCompiler", Type.EmptyTypes)
                    || !VoidMethod(parent, "__AssignQueries", new[] { typeof(SystemState).MakeByRefType() })
                    || !VoidMethod(handles, "__AssignHandles", new[] { typeof(SystemState).MakeByRefType() })) return Unqualified(kind);
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
                if (!execute || !wrapper) return Unqualified(kind);
                if (!AddBufferHelper(methods, "TryAddUniqueValue", element) || !AddBufferHelper(methods, "RemoveValue", element)) return Unqualified(kind);
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, kind);
            }
            catch (Exception) { return Unqualified(kind); }
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

        private static bool AddBufferHelper(List<MethodBase> methods, string name, Type element)
        {
            if (element != typeof(Game.Objects.SubObject) && element != typeof(Game.Net.SubNet)) return false;
            Type collection = typeof(Colossal.Collections.CollectionUtils);
            if (!NativeConstructionBuild.ColossalCollections(collection.Module)) return false;
            MethodInfo? definition = null;
            // Query only these named public members. The NativeList overload
            // has a different first parameter and is outside this buffer path.
            foreach (MemberInfo member in collection.GetMember(name, MemberTypes.Method,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!(member is MethodInfo method) || !method.IsGenericMethodDefinition) continue;
                Type[] generic = method.GetGenericArguments(); ParameterInfo[] parameters = method.GetParameters();
                if (generic.Length != 1 || parameters.Length != 2 || !parameters[0].ParameterType.IsGenericType
                    || parameters[0].ParameterType.GetGenericTypeDefinition() != typeof(DynamicBuffer<>)
                    || parameters[0].ParameterType.GetGenericArguments()[0] != generic[0]
                    || parameters[1].ParameterType != generic[0]) continue;
                if (definition != null || method.DeclaringType != collection || !method.IsPublic || !method.IsStatic
                    || method.ReturnType != typeof(bool)) return false;
                definition = method;
            }
            if (definition == null) return false;
            MethodInfo closed = definition.MakeGenericMethod(element);
            ParameterInfo[] args = closed.GetParameters();
            if (closed.ContainsGenericParameters || closed.ReturnType != typeof(bool)
                || args.Length != 2 || args[0].ParameterType != typeof(DynamicBuffer<>).MakeGenericType(element)
                || args[1].ParameterType != element
                || !closed.GetGenericMethodDefinition().Equals(definition) || closed.Module != collection.Module) return false;
            // The public Harmony reader receives exact MethodBase keys. Both
            // forms are observed rather than assuming a closed key is folded
            // into its generic definition's patch record.
            methods.Add(definition); methods.Add(closed);
            return true;
        }

        private static DefinitionProducerPatchProbe Unqualified(string kind) => new DefinitionProducerPatchProbe {
            Reason = kind + "_METADATA_UNQUALIFIED"
        };
    }
}
#endif
