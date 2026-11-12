#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Buildings;
using Game.Objects;
using Game.Simulation;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    internal static class SubObjectGenerationPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(SubObjectSystem);
                if (!NativeConstructionBuild.Game(parent.Module)) return Unqualified();
                Type? handles = parent.GetNestedType("TypeHandle", BindingFlags.NonPublic);
                if (handles == null || !handles.IsValueType || handles.DeclaringType != parent) return Unqualified();
                var types = new List<Type> { parent, handles, typeof(Game.Common.ModificationBarrier2B), typeof(Game.SafeCommandBufferSystem) };
                foreach (string name in new[] { "CheckSubObjectOwnersJob", "CollectSubObjectOwnersJob",
                    "FillIgnoreSetJob", "UpdateSubObjectsJob", "UpdateSubObjectsData" })
                {
                    Type? type = parent.GetNestedType(name, BindingFlags.NonPublic);
                    if (type == null || !type.IsValueType || type.DeclaringType != parent) return Unqualified();
                    types.Add(type);
                }
                if (!VoidMethod(parent, "OnCreate", Type.EmptyTypes) || !VoidMethod(parent, "OnUpdate", Type.EmptyTypes)
                    || !VoidMethod(parent, "OnDestroy", Type.EmptyTypes) || !VoidMethod(parent, "OnCreateForCompiler", Type.EmptyTypes)
                    || !VoidMethod(parent, "__AssignQueries", new[] { typeof(SystemState).MakeByRefType() })
                    || !VoidMethod(handles, "__AssignHandles", new[] { typeof(SystemState).MakeByRefType() })) return Unqualified();
                var methods = new List<MethodBase>();
                foreach (Type type in types)
                {
                    foreach (MethodInfo method in type.GetMethods(Declared)) methods.Add(method);
                    methods.AddRange(type.GetConstructors(Declared));
                }
                // This exact data-only overload is reached by the one admitted
                // StreetLight leaf. Other lighting systems are not in scope.
                MethodInfo? streetLight = typeof(StreetLightSystem).GetMethod("UpdateStreetLightState", Declared,
                    null, new[] { typeof(StreetLight).MakeByRefType(), typeof(Building) }, null);
                if (streetLight == null || !streetLight.IsPublic || !streetLight.IsStatic || streetLight.ReturnType != typeof(void))
                    return Unqualified();
                methods.Add(streetLight);
                return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), true, "SUBOBJECT_GENERATION");
            }
            catch (Exception) { return Unqualified(); }
        }

        private static bool VoidMethod(Type type, string name, Type[] parameters)
        {
            MethodInfo? value = type.GetMethod(name, Declared, null, parameters, null);
            return value != null && value.DeclaringType == type && !value.IsStatic && value.ReturnType == typeof(void);
        }
        private static DefinitionProducerPatchProbe Unqualified() => new DefinitionProducerPatchProbe {
            Reason = "SUBOBJECT_GENERATION_METADATA_UNQUALIFIED"
        };
    }
}
#endif
