#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Tools;

namespace SkylinesLink.Mod.Native
{
    // Fixed developer observation of an already-loaded public Harmony API.
    // No patch creation, patch-body invocation, assembly loading or private
    // method invocation. All observation targets are fixed below.
    internal sealed class DefinitionProducerPatchProbe
    {
        internal bool Qualified;
        internal int MethodsChecked, PatchedMethods;
        internal string Reason = "PATCH_OBSERVATION_UNAVAILABLE", HarmonyMvid = "", MethodSetSha256 = "";
        internal bool PatchSampleObserved, PatchSampleIncomplete, PatchSampleFieldsMissing, PatchSampleTruncated;
        internal int PatchSampleMethodPatchCount;
        internal string? PatchSampleOriginalIdentity, PatchSampleOriginalName, PatchSampleCategory, PatchSampleOwner;
        internal string? PatchSampleMethodIdentity, PatchSampleMethodName;

        internal static DefinitionProducerPatchProbe Observe()
        {
            MethodInfo[] methods = typeof(NetToolSystem.CreateDefinitionsJob).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            bool entryFound = false;
            foreach (var method in methods) entryFound |= method.Name == "Execute" && method.IsPublic && !method.IsStatic && method.GetParameters().Length == 0;
            return ObserveMethods(methods, entryFound, "PRODUCER");
        }

        internal static DefinitionProducerPatchProbe ObserveScopedClear()
        {
            Type? job = typeof(ToolClearSystem).GetNestedType("ClearEntitiesJob", BindingFlags.NonPublic);
            if (job == null || job.DeclaringType != typeof(ToolClearSystem)) return new DefinitionProducerPatchProbe();
            var methods = new List<MethodBase>();
            bool entryFound = false;
            foreach (Type type in new[] { typeof(ToolClearSystem), job, typeof(ToolOutputBarrier), typeof(Game.SafeCommandBufferSystem) })
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    methods.Add(method);
                    entryFound |= type == typeof(ToolClearSystem) && method.Name == "OnUpdate" && !method.IsStatic && method.GetParameters().Length == 0;
                }
                methods.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            }
            return ObserveMethods(methods.ToArray(), entryFound, "SCOPED_CLEAR");
        }

        internal static DefinitionProducerPatchProbe ObserveMethods(MethodBase[] methods, bool entryFound, string kind)
        {
            var result = new DefinitionProducerPatchProbe();
            try
            {
                if (!entryFound || methods.Length < 1 || methods.Length > 128) return result;
                foreach (var method in methods) if (!AllowedModule(method, kind)) return result;
                Assembly? harmony = null;
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                if (loaded.Length > 1024) return result;
                foreach (Assembly assembly in loaded)
                {
                    if (assembly.GetName().Name != "0Harmony") continue;
                    if (harmony != null) { result.Reason = "MULTIPLE_HARMONY_ASSEMBLIES"; return result; }
                    harmony = assembly;
                }
                // This finite experiment uses the already present, inspected
                // 2.2.2 API. Missing/other providers are not a player dependency
                // and are not silently treated as unmodified native code.
                if (harmony == null) { result.Reason = "HARMONY_NOT_LOADED"; return result; }
                if (harmony.GetName().Version != new Version(2, 2, 2, 0))
                { result.Reason = "HARMONY_ABI_UNQUALIFIED"; return result; }
                result.HarmonyMvid = harmony.ManifestModule.ModuleVersionId.ToString("N");
                Type? api = harmony.GetType("HarmonyLib.Harmony", false);
                Type? patches = harmony.GetType("HarmonyLib.Patches", false);
                MethodInfo? getInfo = api?.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(MethodBase) }, null);
                if (getInfo == null || patches == null || getInfo.ReturnType != patches) return result;
                string[] names = { "Prefixes", "Postfixes", "Transpilers", "Finalizers" };
                FieldInfo[] fields = patches.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (fields.Length != names.Length) { result.Reason = "PATCH_RECORD_ABI_UNQUALIFIED"; return result; }
                var categories = new FieldInfo[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo? field = patches.GetField(names[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (field == null || !typeof(ICollection).IsAssignableFrom(field.FieldType)) return result;
                    categories[i] = field;
                }
                // Tokens are only unique inside one module; generic definitions
                // and their closed keys also share a token. Preserve every exact
                // key supplied to GetPatchInfo in this canonical identity set.
                var identities = new string[methods.Length];
                for (int i = 0; i < methods.Length; i++)
                {
                    identities[i] = MethodIdentity(methods[i]);
                    if (identities[i].Length > 2048) return result;
                }
                Array.Sort(identities, StringComparer.Ordinal);
                string identityText = string.Join("\n", identities);
                if (identityText.Length > 32768) return result;
                byte[] identityBytes = Encoding.UTF8.GetBytes(identityText);
                if (identityBytes.Length > 65536) return result;
                using (var sha = SHA256.Create())
                    result.MethodSetSha256 = BitConverter.ToString(sha.ComputeHash(identityBytes)).Replace("-", "").ToLowerInvariant();
                foreach (MethodBase method in methods)
                {
                    // GetPatchInfo and the inspected public PatchMethod getter
                    // only expose metadata. Producer/patch bodies are not invoked.
                    object? info = getInfo.Invoke(null, new object[] { method });
                    result.MethodsChecked++;
                    if (info == null) continue;
                    if (info.GetType() != patches) return result;
                    bool patched = false;
                    int methodPatchCount = 0;
                    foreach (FieldInfo category in categories)
                    {
                        if (!(category.GetValue(info) is ICollection values)) return result;
                        int count = values.Count;
                        if (count < 0 || count > 4096) return result;
                        patched |= count > 0;
                        methodPatchCount += count;
                    }
                    if (patched)
                    {
                        result.PatchedMethods++;
                        if (!result.PatchSampleObserved)
                            result.ObserveFirstPatch(method, info, categories, methodPatchCount, harmony);
                    }
                }
                result.Qualified = result.PatchedMethods == 0;
                result.Reason = result.Qualified ? "NO_REGISTERED_" + kind + "_PATCH_OBSERVED" : kind + "_PATCHES_UNQUALIFIED";
            }
            catch (Exception) { result.Qualified = false; result.Reason = "PATCH_OBSERVATION_FAILED"; }
            // This is not a native-detour detector, lock, sandbox or guarantee
            // against arbitrary code/TOCTOU. Provider qualification stays open.
            return result;
        }

        private void ObserveFirstPatch(MethodBase original, object info, FieldInfo[] categories, int patchCount, Assembly harmony)
        {
            PatchSampleObserved = PatchSampleIncomplete = true;
            PatchSampleMethodPatchCount = patchCount;
            try
            {
                // Declaration IDs are compact; canonical qualification keys above
                // still retain the complete declaring/generic context separately.
                PatchSampleOriginalIdentity = MetadataIdentity(original);
                PatchSampleOriginalName = DiagnosticText(original.Name, 64);
                object? first = null;
                foreach (FieldInfo category in categories)
                {
                    if (!(category.GetValue(info) is ICollection values) || values.Count < 0 || values.Count > 4096)
                    { PatchSampleFieldsMissing = true; return; }
                    if (values.Count == 0) continue;
                    PatchSampleCategory = category.Name;
                    // First record in fixed category order, not execution order
                    // or a complete patch list. Never enumerate additional rows.
                    foreach (object value in values) { first = value; break; }
                    break;
                }
                Type? patchType = harmony.GetType("HarmonyLib.Patch", false);
                if (first == null || patchType == null || first.GetType() != patchType)
                { PatchSampleFieldsMissing = true; return; }
                FieldInfo? owner = patchType.GetField("owner", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                PropertyInfo? method = patchType.GetProperty("PatchMethod", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                MethodInfo? getter = method?.GetGetMethod();
                if (owner == null || owner.FieldType != typeof(string) || method == null || method.PropertyType != typeof(MethodInfo)
                    || getter == null || getter.IsStatic || getter.GetParameters().Length != 0)
                { PatchSampleFieldsMissing = true; return; }
                if (owner.GetValue(first) is string ownerText) PatchSampleOwner = DiagnosticText(ownerText, 128);
                else PatchSampleFieldsMissing = true;
                // In inspected 2.2.2 this getter resolves metadata from loaded
                // modules only. Patch.GetMethod may invoke a factory; never use it.
                if (!(method.GetValue(first, null) is MethodInfo patchMethod))
                { PatchSampleFieldsMissing = true; return; }
                PatchSampleMethodIdentity = MetadataIdentity(patchMethod);
                PatchSampleMethodName = DiagnosticText(patchMethod.Name, 64);
            }
            catch (Exception) { PatchSampleFieldsMissing = true; }
            // Diagnostic availability never changes qualification or retirement.
        }

        internal void CopyPatchSampleFrom(DefinitionProducerPatchProbe source)
        {
            PatchSampleObserved = source.PatchSampleObserved; PatchSampleIncomplete = source.PatchSampleIncomplete;
            PatchSampleFieldsMissing = source.PatchSampleFieldsMissing; PatchSampleTruncated = source.PatchSampleTruncated;
            PatchSampleMethodPatchCount = source.PatchSampleMethodPatchCount;
            PatchSampleOriginalIdentity = source.PatchSampleOriginalIdentity; PatchSampleOriginalName = source.PatchSampleOriginalName;
            PatchSampleCategory = source.PatchSampleCategory; PatchSampleOwner = source.PatchSampleOwner;
            PatchSampleMethodIdentity = source.PatchSampleMethodIdentity; PatchSampleMethodName = source.PatchSampleMethodName;
        }

        private static string MetadataIdentity(MethodBase method) =>
            method.Module.ModuleVersionId.ToString("N") + ":" + method.MetadataToken.ToString("x8");

        private string DiagnosticText(string value, int encodedBudget)
        {
            int length = 0, used = 0;
            while (length < value.Length)
            {
                char c = value[length];
                // Six bytes per non-ASCII/control code unit also bounds JSON
                // escaping. Quotes/backslashes need two; printable ASCII one.
                int cost = c < 32 || c >= 127 ? 6 : c == '"' || c == '\\' ? 2 : 1;
                if (used + cost > encodedBudget) break;
                used += cost; length++;
            }
            if (length == value.Length) return value;
            if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
            PatchSampleTruncated = true;
            return value.Substring(0, length);
        }

        private static bool AllowedModule(MethodBase method, string kind)
        {
            if (method.Module == typeof(NetToolSystem).Module) return true;
            // Only these two fixed consumers may cross the original Game-only
            // boundary, through their exact DynamicBuffer helper keys.
            Type element;
            if (kind == "SUBOBJECT_REFERENCES") element = typeof(Game.Objects.SubObject);
            else if (kind == "SUBNET_REFERENCES") element = typeof(Game.Net.SubNet);
            else return false;
            if (!(method is MethodInfo helper) || !helper.IsPublic || !helper.IsStatic || !helper.IsGenericMethod
                || helper.DeclaringType != typeof(Colossal.Collections.CollectionUtils) || helper.ReturnType != typeof(bool)
                || !NativeConstructionBuild.ColossalCollections(helper.Module)
                || helper.Name != "TryAddUniqueValue" && helper.Name != "RemoveValue") return false;
            Type[] generic = helper.GetGenericArguments(); ParameterInfo[] parameters = helper.GetParameters();
            if (generic.Length != 1 || parameters.Length != 2) return false;
            Type argument = helper.IsGenericMethodDefinition ? generic[0] : element;
            return (helper.IsGenericMethodDefinition || generic[0] == element)
                && parameters[0].ParameterType == typeof(Unity.Entities.DynamicBuffer<>).MakeGenericType(argument)
                && parameters[1].ParameterType == argument;
        }

        private static string MethodIdentity(MethodBase method)
        {
            string identity = method.Module.ModuleVersionId.ToString("N") + ":" + method.MetadataToken.ToString("x8")
                + ":" + method.DeclaringType?.AssemblyQualifiedName;
            if (method is MethodInfo info && info.IsGenericMethod)
            {
                identity += info.IsGenericMethodDefinition ? ":definition" : ":closed";
                foreach (Type argument in info.GetGenericArguments())
                    identity += argument.IsGenericParameter ? ":!" + argument.GenericParameterPosition
                        : ":" + argument.Module.ModuleVersionId.ToString("N") + ":" + argument.AssemblyQualifiedName;
            }
            return identity;
        }
    }
}
#endif
