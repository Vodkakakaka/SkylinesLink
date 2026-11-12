#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    internal static class NodeCostPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                var parent = typeof(CostSystem);
                var handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                var job = parent.GetNestedType("CalculateCostJob", BindingFlags.NonPublic);
                if (!NativeConstructionBuild.Game(parent.Module)
                    || handles == null || job == null || !handles.IsValueType || !job.IsValueType
                    || !typeof(IJobChunk).IsAssignableFrom(job)) return Unqualified();

                var methods = new List<MethodBase>();
                foreach (Type type in new[] { parent, handles, job })
                {
                    methods.AddRange(type.GetMethods(Declared));
                    methods.AddRange(type.GetConstructors(Declared));
                }
                var execute = job.GetMethod("Execute", Declared);
                var wrapper = job.GetMethod("Unity.Entities.IJobChunk.Execute", Declared);
                var mode = typeof(ToolSystem).GetProperty("actionMode", Declared)?.GetGetMethod();
                var frame = typeof(SimulationSystem).GetProperty("frameIndex", Declared)?.GetGetMethod();
                var editor = typeof(GameModeExtensions).GetMethod("IsEditor", Declared, null, new[] { typeof(GameMode) }, null);
                if (execute == null || wrapper == null || mode?.ReturnType != typeof(GameMode)
                    || frame?.ReturnType != typeof(uint) || editor?.ReturnType != typeof(bool)) return Unqualified();
                methods.Add(mode); methods.Add(frame); methods.Add(editor);
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "NODE_COST");
            }
            catch (Exception) { return Unqualified(); }
        }

        private static DefinitionProducerPatchProbe Unqualified() => new DefinitionProducerPatchProbe {
            Reason = "NODE_COST_METADATA_UNQUALIFIED"
        };
    }
}
#endif
