#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    internal static class NodeGeometryPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                var parent = typeof(GeometrySystem);
                if (parent.Module.ModuleVersionId.ToString("N") != "e15551e93dea48558dde1da3cea7d7ef")
                    return Unqualified();

                var methods = new List<MethodBase>();
                AddDeclared(methods, parent);
                AddDeclared(methods, typeof(EdgeIterator));
                AddDeclared(methods, typeof(TerrainHeightData));
                var handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                if (handles == null || !handles.IsValueType || !AddMethod(methods, handles, "__AssignHandles",
                    typeof(void), typeof(SystemState).MakeByRefType())) return Unqualified();
                foreach (string name in new[] {
                    "InitializeNodeGeometryJob", "AllocateBuffersJob", "CalculateEdgeGeometryJob",
                    "FlattenNodeGeometryJob", "FinishEdgeGeometryJob", "CalculateNodeGeometryJob",
                    "CalculateIntersectionGeometryJob", "CopyNodeGeometryJob", "UpdateNodeGeometryJob"
                })
                {
                    var job = parent.GetNestedType(name, BindingFlags.NonPublic);
                    if (job == null || !job.IsValueType || job.DeclaringType != parent
                        || job.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) == null)
                        return Unqualified();
                    AddDeclared(methods, job);
                }

                var terrain = typeof(TerrainSystem);
                bool qualified = AddMethod(methods, terrain, "GetHeightData", typeof(TerrainHeightData), typeof(bool))
                    && AddMethod(methods, terrain, "AddCPUHeightReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, terrain, "get_HasBackdrop", typeof(bool))
                    && AddMethod(methods, terrain, "get_baseLod", typeof(int))
                    && AddMethod(methods, terrain, "get_heightScaleOffset", typeof(Unity.Mathematics.float2))
                    && AddMethod(methods, terrain, "get_positionOffset", typeof(Unity.Mathematics.float3))
                    && AddMethod(methods, terrain, "get_playableOffset", typeof(Unity.Mathematics.float2))
                    && AddMethod(methods, typeof(TerrainUtils), "GetBounds", typeof(Bounds3), typeof(TerrainHeightData).MakeByRefType());
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), qualified, "NODE_GEOMETRY");
            }
            catch (Exception) { return Unqualified(); }
        }

        private static void AddDeclared(List<MethodBase> methods, Type type)
        {
            methods.AddRange(type.GetMethods(Declared));
            methods.AddRange(type.GetConstructors(Declared));
        }

        private static bool AddMethod(List<MethodBase> methods, Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, Declared, null, parameters, null);
            if (method == null || method.DeclaringType != type || method.ReturnType != result) return false;
            methods.Add(method);
            return true;
        }

        private static DefinitionProducerPatchProbe Unqualified() => new DefinitionProducerPatchProbe {
            Reason = "NODE_GEOMETRY_METADATA_UNQUALIFIED"
        };
    }
}
#endif
