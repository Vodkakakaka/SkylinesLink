#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Fixed metadata for this single-Orphan branch, not a whole call-graph or
    // native-detour proof. No helper or private job is invoked by reflection.
    internal static class CompositionSelectionPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(CompositionSelectSystem);
                if (!NativeConstructionBuild.Game(parent.Module)
                    || !NativeConstructionBuild.Entities(typeof(EntityCommandBuffer).Module)) return Unqualified();
                Type? handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                Type? select = parent.GetNestedType("SelectCompositionJob", BindingFlags.NonPublic);
                Type? create = parent.GetNestedType("CreateCompositionJob", BindingFlags.NonPublic);
                Type? key = parent.GetNestedType("CreatedCompositionKey", BindingFlags.NonPublic);
                foreach (Type? type in new[] { handles, select, create, key })
                    if (type == null || !type.IsValueType || type.DeclaringType != parent) return Unqualified();
                if (!InstanceMethod(parent, "OnCreate", typeof(void), Type.EmptyTypes)
                    || !InstanceMethod(parent, "OnUpdate", typeof(void), Type.EmptyTypes)
                    || !InstanceMethod(parent, "GetLoaded", typeof(bool), Type.EmptyTypes)
                    || !InstanceMethod(parent, "OnCreateForCompiler", typeof(void), Type.EmptyTypes)
                    || !InstanceMethod(parent, "__AssignQueries", typeof(void), new[] { typeof(SystemState).MakeByRefType() })
                    || !InstanceMethod(handles!, "__AssignHandles", typeof(void), new[] { typeof(SystemState).MakeByRefType() })
                    || !InstanceMethod(create!, "Execute", typeof(void), Type.EmptyTypes)) return Unqualified();
                bool selectExecute = false, selectWrapper = false;
                var methods = new List<MethodBase>();
                foreach (Type type in new[] { parent, handles!, select!, create!, key!, typeof(CompositionFlags), typeof(EdgeIterator),
                    typeof(Game.Common.ModificationBarrier3), typeof(Game.SafeCommandBufferSystem) })
                {
                    foreach (MethodInfo method in type.GetMethods(Declared))
                    {
                        methods.Add(method);
                        if (type != select || !ChunkExecute(method)) continue;
                        selectExecute |= method.Name == "Execute" && method.IsPublic;
                        selectWrapper |= method.Name == "Unity.Entities.IJobChunk.Execute" && method.IsPrivate;
                    }
                    methods.AddRange(type.GetConstructors(Declared));
                }
                if (!selectExecute || !selectWrapper) return Unqualified();
                if (!AddHelper(methods, "GetElevationFlags", typeof(CompositionFlags), new[] { typeof(Elevation), typeof(Elevation),
                        typeof(Elevation), typeof(NetGeometryData) })
                    || !AddHelper(methods, "InvertCompositionFlags", typeof(CompositionFlags), new[] { typeof(CompositionFlags) })
                    || !AddHelper(methods, "InvertSectionFlags", typeof(NetSectionFlags), new[] { typeof(NetSectionFlags) })
                    || !AddHelper(methods, "TestSectionFlags", typeof(bool), new[] { typeof(NetGeometrySection), typeof(CompositionFlags) })
                    || !AddHelper(methods, "TestSubSectionFlags", typeof(bool), new[] { typeof(NetSubSection), typeof(CompositionFlags), typeof(NetSectionFlags) })
                    || !AddHelper(methods, "TestPieceFlags", typeof(bool), new[] { typeof(NetSectionPiece), typeof(CompositionFlags), typeof(NetSectionFlags) })
                    || !AddHelper(methods, "TestPieceFlags2", typeof(bool), new[] { typeof(NetSectionPiece), typeof(CompositionFlags), typeof(NetSectionFlags) })
                    || !AddHelper(methods, "GetCompositionPieces", typeof(void), new[] { typeof(NativeList<NetCompositionPiece>),
                        typeof(NativeArray<NetGeometrySection>), typeof(CompositionFlags), typeof(BufferLookup<NetSubSection>),
                        typeof(BufferLookup<NetSectionPiece>) })) return Unqualified();
                if (methods.Count > 128) return Unqualified();
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "COMPOSITION_SELECTION");
            }
            catch (Exception) { return Unqualified(); }
        }

        private static bool AddHelper(List<MethodBase> methods, string name, Type result, Type[] parameters)
        {
            MethodInfo? value = typeof(NetCompositionHelpers).GetMethod(name, Declared, null, parameters, null);
            if (value == null || !value.IsPublic || !value.IsStatic || value.IsGenericMethod
                || value.DeclaringType != typeof(NetCompositionHelpers) || value.ReturnType != result) return false;
            methods.Add(value); return true;
        }
        private static bool InstanceMethod(Type type, string name, Type result, Type[] parameters)
        {
            MethodInfo? value = type.GetMethod(name, Declared, null, parameters, null);
            return value != null && value.DeclaringType == type && !value.IsStatic && value.ReturnType == result;
        }
        private static bool ChunkExecute(MethodInfo method)
        {
            ParameterInfo[] args = method.GetParameters();
            return !method.IsStatic && method.ReturnType == typeof(void) && args.Length == 4
                && args[0].ParameterType == typeof(ArchetypeChunk).MakeByRefType() && args[1].ParameterType == typeof(int)
                && args[2].ParameterType == typeof(bool) && args[3].ParameterType.FullName == "Unity.Burst.Intrinsics.v128&";
        }
        private static DefinitionProducerPatchProbe Unqualified() => new DefinitionProducerPatchProbe {
            Reason = "COMPOSITION_SELECTION_METADATA_UNQUALIFIED"
        };
    }
}
#endif
