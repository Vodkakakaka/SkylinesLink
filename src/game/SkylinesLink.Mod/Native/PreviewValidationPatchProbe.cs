#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Collections;
using Game.Common;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Notifications;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using AreaSearch = Game.Areas.SearchSystem;
using NetSearch = Game.Net.SearchSystem;
using ObjectSearch = Game.Objects.SearchSystem;
using ObjectValidation = Game.Objects.ValidationHelpers;

namespace SkylinesLink.Mod.Native
{
    internal static class PreviewValidationPatchProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static DefinitionProducerPatchProbe Observe()
        {
            try
            {
                Type parent = typeof(ValidationSystem);
                if (!NativeConstructionBuild.Game(parent.Module)
                    || !NativeConstructionBuild.Entities(typeof(EntityCommandBufferSystem).Module))
                    return Unqualified("PREVIEW_VALIDATION_METADATA_UNQUALIFIED");

                var methods = new List<MethodBase>();
                AddDeclared(methods, parent);
                AddDeclared(methods, typeof(ValidationSystem.ChunkType));
                AddDeclared(methods, typeof(ValidationSystem.EntityData));
                foreach (string name in new[] { "TypeHandle", "BoundsListJob", "ValidationJob", "FillErrorPrefabsJob",
                    "ProcessValidationResultsJob" })
                    if (!AddStruct(methods, parent, name)) return Unqualified("PREVIEW_VALIDATION_METADATA_UNQUALIFIED");

                Type objectValidation = typeof(ObjectValidation);
                foreach (string name in new[] { "ObjectIterator", "NetIterator", "AreaIterator" })
                    if (!AddStruct(methods, objectValidation, name)) return Unqualified("PREVIEW_VALIDATION_METADATA_UNQUALIFIED");
                foreach (string name in new[] { "ValidateObject", "ValidateWorldBounds", "ValidateSubPlacement", "GetOwner", "CheckSurface" })
                    if (!AddUniqueMethod(methods, objectValidation, name)) return Unqualified("PREVIEW_VALIDATION_METADATA_UNQUALIFIED");

                Type dependency = typeof(JobHandle).MakeByRefType();
                Type tree = typeof(NativeQuadTree<Entity, QuadTreeBoundsXZ>);
                Type areaTree = typeof(NativeQuadTree<Game.Areas.AreaSearchItem, QuadTreeBoundsXZ>);
                bool found = AddUniqueMethod(methods, typeof(Game.Buildings.ValidationHelpers), "ValidateBuilding")
                    && AddMethod(methods, typeof(Game.Objects.ObjectUtils), "CalculateBounds", typeof(Bounds3),
                        typeof(float3), typeof(quaternion), typeof(ObjectGeometryData))
                    && AddMethod(methods, typeof(ToolSystem), "get_actionMode", typeof(GameMode))
                    && AddMethod(methods, typeof(GameModeExtensions), "IsEditor", typeof(bool), typeof(GameMode))
                    && AddMethod(methods, typeof(ObjectSearch), "GetStaticSearchTree", tree, typeof(bool), dependency)
                    && AddMethod(methods, typeof(ObjectSearch), "AddStaticSearchTreeReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(NetSearch), "GetNetSearchTree", tree, typeof(bool), dependency)
                    && AddMethod(methods, typeof(NetSearch), "AddNetSearchTreeReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(AreaSearch), "GetSearchTree", areaTree, typeof(bool), dependency)
                    && AddMethod(methods, typeof(AreaSearch), "AddSearchTreeReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(InstanceCountSystem), "GetInstanceCounts",
                        typeof(NativeParallelHashMap<Entity, int>), typeof(bool), dependency)
                    && AddMethod(methods, typeof(InstanceCountSystem), "AddCountReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(CellMapSystem<GroundWater>), "GetMap", typeof(NativeArray<GroundWater>), typeof(bool), dependency)
                    && AddMethod(methods, typeof(CitySystem), "get_City", typeof(Entity))
                    && AddMethod(methods, typeof(PlayerMoney), "get_money", typeof(int))
                    && AddMethod(methods, typeof(TerrainSystem), "GetHeightData", typeof(TerrainHeightData), typeof(bool))
                    && AddMethod(methods, typeof(TerrainSystem), "AddCPUHeightReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(TerrainUtils), "GetEditorCameraBounds", typeof(Bounds3), typeof(TerrainSystem),
                        typeof(TerrainHeightData).MakeByRefType())
                    && AddMethod(methods, typeof(WaterSystem), "GetSurfaceData", typeof(WaterSurfaceData<SurfaceWater>), dependency)
                    && AddMethod(methods, typeof(WaterSystem), "AddSurfaceReader", typeof(void), typeof(JobHandle))
                    && AddMethod(methods, typeof(SafeCommandBufferSystem), "CreateCommandBuffer", typeof(EntityCommandBuffer))
                    && AddMethod(methods, typeof(IconCommandSystem), "CreateCommandBuffer", typeof(IconCommandBuffer))
                    && AddMethod(methods, typeof(IconCommandSystem), "AddCommandBufferWriter", typeof(void), typeof(JobHandle));
                foreach (MethodInfo method in typeof(IconCommandBuffer).GetMethods(Declared))
                    if (method.Name == "Add") methods.Add(method);

                var game = DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(), found, "PREVIEW_VALIDATION");
                if (game.Qualified && !ObserveBarrierMethods(game.HarmonyMvid))
                    return Unqualified("PREVIEW_VALIDATION_ECS_BARRIER_PATCH_UNQUALIFIED");
                return game;
            }
            catch (Exception) { return Unqualified("PREVIEW_VALIDATION_METADATA_UNQUALIFIED"); }
        }

        private static bool ObserveBarrierMethods(string harmonyMvid)
        {
            var type = typeof(EntityCommandBufferSystem);
            var methods = new List<MethodBase>();
            if (!AddMethod(methods, type, "CreateCommandBuffer", typeof(EntityCommandBuffer))
                || !AddMethod(methods, type, "AddJobHandleForProducer", typeof(void), typeof(JobHandle))
                || !AddMethod(methods, type, "OnDestroy", typeof(void))
                || !AddMethod(methods, type, "FlushPendingBuffers", typeof(void), typeof(bool))) return false;
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (loaded.Length > 1024) return false;
            foreach (Assembly assembly in loaded)
            {
                if (assembly.GetName().Name != "0Harmony"
                    || assembly.ManifestModule.ModuleVersionId.ToString("N") != harmonyMvid) continue;
                Type? patches = assembly.GetType("HarmonyLib.Patches", false);
                var getInfo = assembly.GetType("HarmonyLib.Harmony", false)?.GetMethod("GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
                if (patches == null || getInfo == null || getInfo.ReturnType != patches) return false;
                foreach (MethodBase key in methods)
                {
                    object? info = getInfo.Invoke(null, new object[] { key });
                    if (info == null) continue;
                    if (info.GetType() != patches) return false;
                    foreach (string name in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                        if (!(patches.GetField(name, Declared)?.GetValue(info) is ICollection group) || group.Count != 0)
                            return false;
                }
                return true;
            }
            return false;
        }

        private static bool AddStruct(List<MethodBase> methods, Type parent, string name)
        {
            Type? type = parent.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
            if (type == null || !type.IsValueType) return false;
            AddDeclared(methods, type);
            return true;
        }

        private static void AddDeclared(List<MethodBase> methods, Type type)
        {
            methods.AddRange(type.GetMethods(Declared));
            methods.AddRange(type.GetConstructors(Declared));
        }

        private static bool AddUniqueMethod(List<MethodBase> methods, Type type, string name)
        {
            MethodInfo? method = type.GetMethod(name, Declared);
            if (method == null) return false;
            methods.Add(method);
            return true;
        }

        private static bool AddMethod(List<MethodBase> methods, Type type, string name, Type result, params Type[] parameters)
        {
            var method = type.GetMethod(name, Declared, null, parameters, null);
            if (method == null || method.ReturnType != result) return false;
            methods.Add(method);
            return true;
        }

        private static DefinitionProducerPatchProbe Unqualified(string reason) => new DefinitionProducerPatchProbe { Reason = reason };
    }
}
#endif
