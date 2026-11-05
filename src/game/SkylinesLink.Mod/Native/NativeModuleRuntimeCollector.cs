using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.SceneFlow;
using SkylinesLink.Application.Modules;

namespace SkylinesLink.Mod.Native
{
    /// <summary>只观察已有 ModManager；不加载资产、不读取 URI/元数据、不调用模组实例。</summary>
    public sealed class NativeModuleRuntimeCollector
    {
        public ModuleRegistryCapture Capture()
        {
            RequireMainThread();
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var clock = Stopwatch.StartNew();
            try { return CaptureCore(started, clock); }
            catch (ModuleRegistryReadFailure) { throw; }
            catch (Exception) { throw new ModuleRegistryReadFailure("CAPTURE_FAILED"); }
        }

        private static ModuleRegistryCapture CaptureCore(long started, Stopwatch clock)
        {
            var game = GameManager.instance;
            var manager = game?.modManager;
            if (manager == null) throw new ModuleRegistryReadFailure("SOURCE_UNAVAILABLE");
            bool firstInitializationComplete = manager.isInitialized;
            bool restartRequired = manager.restartRequired;
            string? gameVersion = null, gameVersionIssue = "unavailable";
            if (!Expired(clock))
            {
                try
                {
                    string? nativeVersion = global::Game.Version.current.fullVersion;
                    gameVersionIssue = TextIssue(nativeVersion, 64);
                    if (gameVersionIssue == null) gameVersion = nativeVersion;
                }
                catch (Exception) { gameVersionIssue = "unavailable"; }
            }
            var entries = new List<ModuleRuntimeFact>(ModuleRegistryCapture.MaxEntries);
            int visited = 0; bool complete = false; string? reason = null;
            if (Expired(clock)) reason = "TIME_LIMIT";
            else
            {
                using (var iterator = manager.GetEnumerator())
                {
                    while (true)
                    {
                        if (Expired(clock)) { reason = "TIME_LIMIT"; break; }
                        bool next;
                        try { next = iterator.MoveNext(); }
                        catch (InvalidOperationException) { throw new ModuleRegistryReadFailure("REGISTRY_CHANGED"); }
                        // MoveNext cannot be preempted. Exhaustion is evidence even if that call overran the slice.
                        if (!next) { complete = true; break; }
                        visited++;
                        if (visited == ModuleRegistryCapture.MaxVisited) { reason = "ENTRY_LIMIT"; break; }
                        try
                        {
                            CheckSlice(clock);
                            ModManager.ModInfo? info;
                            try { info = iterator.Current; }
                            catch (InvalidOperationException) { throw new ModuleRegistryReadFailure("REGISTRY_CHANGED"); }
                            entries.Add(ReadEntry(entries.Count, info, clock));
                        }
                        catch (SliceExpired)
                        {
                            // Do not publish a half-read row as missing native fields.
                            reason = "TIME_LIMIT"; break;
                        }
                    }
                }
            }
            return new ModuleRegistryCapture(started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), gameVersion,
                gameVersionIssue, firstInitializationComplete, restartRequired, entries, visited, complete, reason);
        }

        private static ModuleRuntimeFact ReadEntry(int ordinal, ModManager.ModInfo? info, Stopwatch clock)
        {
            CheckSlice(clock);
            var issues = new List<ModuleFieldIssue>(8);
            if (info == null)
            {
                issues.Add(new ModuleFieldIssue("entry", "missing"));
                return new ModuleRuntimeFact(ordinal, null, null, null, null, null, null, null, null, "unknown", issues);
            }
            int state = (int)info.state; CheckSlice(clock);
            var instances = info.instances;
            int? instanceCount = instances?.Count;
            if (!instanceCount.HasValue) issues.Add(new ModuleFieldIssue("instanceCount", "missing"));
            else if (instanceCount.Value < 0) { instanceCount = null; issues.Add(new ModuleFieldIssue("instanceCount", "invalid")); }
            CheckSlice(clock);
            bool hasLoadError = info.loadError != null; CheckSlice(clock);
            var asset = info.asset; CheckSlice(clock);
            if (asset == null)
            {
                issues.Add(new ModuleFieldIssue("asset", "missing"));
                return new ModuleRuntimeFact(ordinal, null, null, state, null, null, null, instanceCount, hasLoadError, "unknown", issues);
            }
            // Identifier.ToString includes its URI. Only the separate guid is inspected.
            string? guid = asset.id.guid.ToString();
            string? guidIssue = GuidIssue(guid);
            if (guidIssue != null) { guid = null; issues.Add(new ModuleFieldIssue("assetGuid", guidIssue)); }
            CheckSlice(clock);
            bool isMod = asset.isMod; CheckSlice(clock);
            var assembly = asset.assembly; CheckSlice(clock);
            var database = asset.database; CheckSlice(clock);
            string databaseKind = database == null ? "unknown" : database is AssetDatabase<User> ? "user" :
                database is AssetDatabase<ParadoxMods> ? "paradoxMods" :
                database is AssetDatabase<Colossal.IO.AssetDatabase.Game> ? "game" : "other";
            if (database == null) issues.Add(new ModuleFieldIssue("databaseKind", "missing"));
            CheckSlice(clock);
            string? assemblyName = null, assemblyVersion = null;
            if (assembly != null) ReadAssemblyIdentity(assembly, issues, clock, out assemblyName, out assemblyVersion);
            CheckSlice(clock);
            return new ModuleRuntimeFact(ordinal, guid, isMod, state, assembly != null, assemblyName, assemblyVersion,
                instanceCount, hasLoadError, databaseKind, issues);
        }

        private static void ReadAssemblyIdentity(Assembly assembly, List<ModuleFieldIssue> issues, Stopwatch clock,
            out string? name, out string? version)
        {
            name = null; version = null;
            AssemblyName identity;
            CheckSlice(clock);
            try { identity = assembly.GetName(); }
            catch (Exception)
            {
                issues.Add(new ModuleFieldIssue("assemblyName", "unavailable"));
                issues.Add(new ModuleFieldIssue("assemblyVersion", "unavailable"));
                return;
            }
            CheckSlice(clock);
            string? issue;
            try { name = identity.Name; issue = TextIssue(name, 128); }
            catch (Exception) { issue = "unavailable"; }
            if (issue != null) { name = null; issues.Add(new ModuleFieldIssue("assemblyName", issue)); }
            CheckSlice(clock);
            try { version = identity.Version?.ToString(); issue = VersionIssue(version); }
            catch (Exception) { issue = "unavailable"; }
            if (issue != null) { version = null; issues.Add(new ModuleFieldIssue("assemblyVersion", issue)); }
            CheckSlice(clock);
        }

        private static string? GuidIssue(string? value)
        {
            if (value == null) return "missing";
            if (value.Length != 32) return "invalid";
            bool nonzero = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return "invalid";
                nonzero |= value[i] != '0';
            }
            return nonzero ? null : "invalid";
        }
        private static string? VersionIssue(string? value)
        {
            if (value == null) return "missing";
            if (value.Length > 32) return "tooLong";
            if (value.Length == 0) return "invalid";
            int dots = 0, digits = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= '0' && c <= '9') { digits++; continue; }
                if (c != '.' || digits == 0 || ++dots > 3) return "invalid";
                digits = 0;
            }
            return digits > 0 ? null : "invalid";
        }
        private static string? TextIssue(string? value, int maximumScalars)
        {
            if (value == null) return "missing";
            if (value.Length == 0) return "invalid";
            if (value.Length > maximumScalars * 2) return "tooLong";
            int scalars = 0; bool tooLong = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (++i == value.Length || !char.IsLowSurrogate(value[i])) return "invalid";
                }
                else if (char.IsLowSurrogate(c)) return "invalid";
                if (++scalars > maximumScalars) tooLong = true;
            }
            return tooLong ? "tooLong" : null;
        }
        private static bool Expired(Stopwatch clock) => clock.Elapsed.TotalMilliseconds >= ModuleRegistryCapture.SliceMilliseconds;
        private static void CheckSlice(Stopwatch clock) { if (Expired(clock)) throw new SliceExpired(); }
        private sealed class SliceExpired : Exception { }
        private static void RequireMainThread()
        {
            if (!MainThreadDispatcher.isMainThread) throw new ModuleRegistryReadFailure("CAPTURE_FAILED");
        }
    }
}
