#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Assets;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Resident metadata observation only. target can still PostLoad/hydrate
    // metadata/cache; neither Full nor registered validity proves map content.
    internal sealed class MapIdentityProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const int EntryLimit = 128, CandidateLimit = 4;
        private readonly object sync = new object();
        private readonly World? source;
        private readonly Candidate?[] candidates = new Candidate?[CandidateLimit];
        private bool ran, released, checksPassed, scanComplete;
        private bool defaultUnchanged, worldsUnchanged, sourceSystemsUnchanged;
        private int visited, candidateCount;
        private int typeSkipped, externalSkipped, registrationSkipped, residentSkipped, guidSkipped;
        private int dataSkipped, hydrationSkipped, prerequisiteBoundsSkipped, prerequisiteSkipped, duplicateSkipped;
        private string? failureCode, exceptionType;

        internal MapIdentityProbe(World? source) { this.source = source; }
        internal bool ResourcesReleased { get { lock (sync) return released; } }

        internal void Run()
        {
            RequireMainThread();
            lock (sync)
            {
                Check(!ran, "MAP_IDENTITY_ALREADY_RAN"); ran = true;
                World? previousDefault = World.DefaultGameObjectInjectionWorld;
                World[]? beforeWorlds = null;
                ComponentSystemBase[]? beforeSystems = null;
                try
                {
                    Check(typeof(MapMetadata).Assembly.ManifestModule.ModuleVersionId.ToString("N") == GameMvid
                        && typeof(World).Assembly.ManifestModule.ModuleVersionId.ToString("N") == EntitiesMvid, "NATIVE_BUILD_UNQUALIFIED");
                    beforeWorlds = CaptureWorlds();
                    Check(source != null && source.IsCreated, "SOURCE_OBSERVATION_UNAVAILABLE");
                    beforeSystems = CaptureSystems(source!);
                    var manager = GameManager.instance;
                    Check(manager != null && manager.state == GameManager.State.WorldReady
                        && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading, "MAP_IDENTITY_MENU_REQUIRED");
                    var database = AssetDatabase.global;
                    Check(database != null, "MAP_IDENTITY_DATABASE_UNAVAILABLE");
                    using (var entries = database!.GetAssets<Metadata<MapInfo>>().GetEnumerator())
                    {
                        // Limit before Current, state or target access. Reaching either
                        // bound does not claim that the catalog has been exhausted.
                        while (visited < EntryLimit && candidateCount < CandidateLimit)
                        {
                            if (!entries.MoveNext()) { scanComplete = true; break; }
                            visited++;
                            var entry = entries.Current;
                            if (!(entry is MapMetadata metadata) || entry.GetType() != typeof(MapMetadata)) { typeSkipped++; continue; }
                            if (!metadata.isBuiltin) { externalSkipped++; continue; }
                            if (!metadata.isValid) { registrationSkipped++; continue; }
                            if (metadata.state != LoadState.Full) { residentSkipped++; continue; }
                            if (!TryGuid(metadata.id.guid.ToString(), out string metadataGuid)) { guidSkipped++; continue; }
                            // No target setter, GetMeta, explicit Load or save call.
                            // This getter may still PostLoad/cache or perform metadata IO.
                            var info = metadata.target;
                            if (info == null) { dataSkipped++; continue; }
                            bool hydrated = ReferenceEquals(info.metaData, metadata);
                            if (!hydrated) { hydrationSkipped++; continue; }
                            var data = info.mapData;
                            if (data == null) { dataSkipped++; continue; }
                            // A built-in metadata record alone does not establish
                            // the data source; require both identities to be built-in.
                            if (!data.isBuiltin) { externalSkipped++; continue; }
                            if (!data.isValid) { dataSkipped++; continue; }
                            if (!TryGuid(data.id.guid.ToString(), out string dataGuid)) { guidSkipped++; continue; }
                            // The native menu checks prerequisites for built-in maps;
                            // isPlayable only vetoes custom maps. Retain its raw value.
                            bool playable = info.isPlayable;
                            string[]? prerequisites = info.contentPrerequisites;
                            if (!BoundedPrerequisites(prerequisites)) { prerequisiteBoundsSkipped++; continue; }
                            // The same public check as the menu; availability can touch
                            // content metadata/cache. Null retains its native semantics.
                            bool prerequisitesMet = manager!.ArePrerequisitesMet(prerequisites!);
                            if (!prerequisitesMet) { prerequisiteSkipped++; continue; }
                            if (Contains(metadataGuid, dataGuid)) { duplicateSkipped++; continue; }
                            candidates[candidateCount++] = new Candidate(metadataGuid, dataGuid, hydrated, prerequisitesMet, playable);
                        }
                    }
                    Check(candidateCount > 0, "MAP_IDENTITY_NO_CANDIDATE");
                    checksPassed = true;
                }
                catch (CheckFailure error) { failureCode = error.Code; exceptionType = ExceptionName(error); }
                catch (Exception error) { failureCode = "MAP_IDENTITY_FAILED"; exceptionType = ExceptionName(error); }
                finally
                {
                    Release();
                    try
                    {
                        defaultUnchanged = ReferenceEquals(previousDefault, World.DefaultGameObjectInjectionWorld);
                        worldsUnchanged = beforeWorlds != null && Same(beforeWorlds, CaptureWorlds());
                        sourceSystemsUnchanged = beforeSystems != null && source != null && source.IsCreated
                            && Same(beforeSystems, CaptureSystems(source));
                        Check(defaultUnchanged && worldsUnchanged && sourceSystemsUnchanged, "MAP_IDENTITY_SOURCE_CHANGED");
                    }
                    catch (Exception error)
                    {
                        if (failureCode == null)
                        {
                            failureCode = error is CheckFailure failure ? failure.Code : "MAP_IDENTITY_FINAL_OBSERVATION_FAILED";
                            exceptionType = ExceptionName(error);
                        }
                    }
                }
            }
        }

        internal void Release()
        {
            RequireMainThread();
            // No owned World, entity query, native container or native job was
            // created. Metadata/cache effects belong to the game, not this probe.
            lock (sync) released = true;
        }

        internal JObject Read()
        {
            // Background readers see only bounded managed values, never SDK objects.
            lock (sync)
            {
                var rows = new JArray();
                for (int index = 0; index < candidateCount; index++)
                {
                    Candidate value = candidates[index]!;
                    rows.Add(new JObject { ["metadataGuid"] = value.MetadataGuid, ["dataGuid"] = value.DataGuid,
                        ["hydrated"] = value.Hydrated, ["prereq"] = value.PrerequisitesMet, ["playable"] = value.Playable });
                }
                return new JObject
                {
                    ["kind"] = "mapIdentity", ["ran"] = ran,
                    ["passed"] = checksPassed && released && defaultUnchanged && worldsUnchanged && sourceSystemsUnchanged && failureCode == null,
                    ["resourcesReleased"] = released, ["ownedNativeResources"] = 0,
                    ["visited"] = visited, ["scanComplete"] = scanComplete, ["candidates"] = rows,
                    ["skip"] = new JObject { ["type"] = typeSkipped, ["external"] = externalSkipped,
                        ["registration"] = registrationSkipped, ["notFull"] = residentSkipped, ["guid"] = guidSkipped,
                        ["data"] = dataSkipped, ["hydration"] = hydrationSkipped, ["prereqBounds"] = prerequisiteBoundsSkipped,
                        ["prereq"] = prerequisiteSkipped, ["duplicate"] = duplicateSkipped },
                    ["defaultUnchanged"] = defaultUnchanged, ["worldMembersUnchanged"] = worldsUnchanged,
                    ["sourceSystemsUnchanged"] = sourceSystemsUnchanged,
                    ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType),
                    ["scope"] = "resident built-in identities; target/prereq may hydrate metadata/cache or do metadata IO; no full catalog, map content, city/load/save qualification"
                };
            }
        }

        private bool Contains(string metadataGuid, string dataGuid)
        {
            for (int index = 0; index < candidateCount; index++)
                if (candidates[index]!.MetadataGuid == metadataGuid || candidates[index]!.DataGuid == dataGuid) return true;
            return false;
        }
        private static bool BoundedPrerequisites(string[]? values)
        {
            if (values == null) return true;
            if (values.Length > 16) return false;
            foreach (string value in values) if (value == null || value.Length == 0 || value.Length > 128) return false;
            return true;
        }
        private static bool TryGuid(string value, out string result)
        {
            result = "";
            if (value.Length != 32) return false;
            bool nonzero = false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))) return false;
                if (character != '0') nonzero = true;
            }
            if (!nonzero) return false;
            result = value.ToLowerInvariant(); return true;
        }
        private static World[] CaptureWorlds()
        {
            var list = World.All; Check(list.Count <= 64, "MAP_IDENTITY_WORLD_BUDGET");
            var result = new World[list.Count]; for (int index = 0; index < result.Length; index++) result[index] = list[index]; return result;
        }
        private static ComponentSystemBase[] CaptureSystems(World world)
        {
            var list = world.Systems; Check(list.Count <= 4096, "MAP_IDENTITY_SYSTEM_BUDGET");
            var result = new ComponentSystemBase[list.Count]; for (int index = 0; index < result.Length; index++) result[index] = list[index]; return result;
        }
        private static bool Same<T>(T[] before, T[] after) where T : class
        {
            if (before.Length != after.Length) return false;
            for (int index = 0; index < before.Length; index++) if (!ReferenceEquals(before[index], after[index])) return false;
            return true;
        }
        private sealed class Candidate
        {
            internal Candidate(string metadataGuid, string dataGuid, bool hydrated, bool prerequisitesMet, bool playable)
            { MetadataGuid = metadataGuid; DataGuid = dataGuid; Hydrated = hydrated; PrerequisitesMet = prerequisitesMet; Playable = playable; }
            internal string MetadataGuid { get; }
            internal string DataGuid { get; }
            internal bool Hydrated { get; }
            internal bool PrerequisitesMet { get; }
            internal bool Playable { get; }
        }
        private static string ExceptionName(Exception error)
        {
            string name = error.GetType().Name;
            if (name.Length > 64) return "Exception";
            foreach (char character in name)
                if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9') || character == '_')) return "Exception";
            return name;
        }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Main thread required."); }
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private sealed class CheckFailure : Exception { internal string Code { get; } internal CheckFailure(string code) { Code = code; } }
    }
}
#endif
