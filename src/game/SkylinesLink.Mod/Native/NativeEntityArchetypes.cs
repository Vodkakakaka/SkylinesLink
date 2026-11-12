#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Shared metadata mechanics, not domain permission or entity ownership.
    // Each domain retains one instance before its first temporary allocation.
    internal sealed class NativeEntityArchetypes
    {
        private int attempted, acquired, disposed;
        internal bool ResourcesReleased => attempted == acquired && acquired == disposed;
        internal JObject Snapshot() => new JObject { ["attempted"] = attempted, ["acquired"] = acquired, ["disposed"] = disposed };

        internal Dictionary<TypeIndex, ComponentType> Read(EntityArchetype archetype, int maximum)
        {
            Require(archetype.Valid && archetype.TypesCount > 0 && archetype.TypesCount <= maximum,
                "NATIVE_ARCHETYPE_TYPE_BUDGET");
            attempted++;
            NativeArray<ComponentType> values = archetype.GetComponentTypes(Allocator.Temp);
            acquired++;
            try
            {
                Require(values.Length == archetype.TypesCount - 1, "NATIVE_ARCHETYPE_TYPE_COUNT_CHANGED");
                var result = new Dictionary<TypeIndex, ComponentType>();
                foreach (ComponentType value in values)
                {
                    TypeIndex index = value.TypeIndex;
                    Require(!index.IsManagedType && !index.IsCleanupComponent && !index.IsCleanupBufferComponent
                        && !index.IsCleanupSharedComponent && !result.ContainsKey(index), "NATIVE_ARCHETYPE_TYPE_UNSUPPORTED");
                    result.Add(index, value);
                }
                return result;
            }
            finally { values.Dispose(); disposed++; }
        }

        // The caller has already qualified the complete domain-specific set.
        // This extends that same set, including all original prefab types.
        internal static EntityArchetype CreateScoped(EntityManager entities, Dictionary<TypeIndex, ComponentType> types)
        {
            Require(!types.ContainsKey(TypeManager.GetTypeIndex<Disabled>())
                && !types.ContainsKey(TypeManager.GetTypeIndex<ObjectGenerationScope>()), "NATIVE_ARCHETYPE_ALREADY_SCOPED");
            types.Add(TypeManager.GetTypeIndex<Disabled>(), ComponentType.ReadWrite<Disabled>());
            types.Add(TypeManager.GetTypeIndex<ObjectGenerationScope>(), ComponentType.ReadWrite<ObjectGenerationScope>());
            var merged = new ComponentType[types.Count];
            types.Values.CopyTo(merged, 0);
            // Bind the exact public array overload: net48 cannot resolve this
            // SDK's overload group containing ReadOnlySpan. No private API or
            // pointer is invoked, and no runtime is installed for this seam.
            MethodInfo? method = typeof(EntityManager).GetMethod("CreateArchetype", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(ComponentType[]) }, null);
            Require(method != null && method.DeclaringType == typeof(EntityManager) && method.ReturnType == typeof(EntityArchetype)
                && !method.IsGenericMethod && NativeConstructionBuild.Entities(method.Module),
                "NATIVE_ARCHETYPE_PUBLIC_API_UNQUALIFIED");
            return method!.Invoke(entities, new object[] { merged }) is EntityArchetype result
                ? result : throw new InvalidOperationException("NATIVE_ARCHETYPE_CREATE_UNCONFIRMED");
        }

        internal static void RequireSame(Dictionary<TypeIndex, ComponentType> actual, Dictionary<TypeIndex, ComponentType> expected)
        {
            Require(actual.Count == expected.Count, "NATIVE_ARCHETYPE_TYPE_SET_CHANGED");
            foreach (TypeIndex type in expected.Keys)
                Require(actual.ContainsKey(type), "NATIVE_ARCHETYPE_TYPE_SET_CHANGED");
        }

        internal static void AddExpected<T>(Dictionary<TypeIndex, ComponentType> types) where T : unmanaged, IComponentData
        { TypeIndex index = TypeManager.GetTypeIndex<T>(); if (!types.ContainsKey(index)) types.Add(index, ComponentType.ReadWrite<T>()); }

        internal static string TypeDigest(Dictionary<TypeIndex, ComponentType> types)
        {
            var names = new List<string> { typeof(Entity).FullName! };
            foreach (TypeIndex type in types.Keys) names.Add(TypeManager.GetType(type).FullName!);
            names.Sort(StringComparer.Ordinal);
            return Digest(string.Join("\n", names));
        }

        internal static string Digest(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        private static void Require(bool condition, string code)
        { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
