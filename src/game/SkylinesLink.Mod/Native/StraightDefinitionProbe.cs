#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using System.Globalization;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // A developer-only, synchronous native definition producer experiment.
    // No source/live ECB playback, native Tool activation or city qualification.
    internal sealed class StraightDefinitionProbe
    {
        // Qualified local SDK builds for this development experiment. Launch
        // evidence also hashes the actual on-disk binaries; an MVID alone is
        // neither a file-integrity check nor a runtime-patch proof.
        private const string QualifiedGameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string QualifiedEntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const int MaximumSubObjectsPerPrefab = 64;
        private const int MaximumSubObjectReads = 4096;
        private readonly World? source;
        private World? output;
        private Reader? reader;
        private EntityCommandBuffer commands;
        private NativeList<ControlPoint> points;
        private NativeList<NetToolSystem.UpgradeState> upgrades;
        private bool ran, checksPassed, releaseAttempted, released, constructorUncertain, nativeUncertain;
        private bool defaultUnchanged, worldsUnchanged, systemsUnchanged, toolUnchanged, selectionUnchanged;
        private bool produced, playedBack, prefabMatched, noOwner, endpointsMatched, selectionCompleted;
        private string? failureCode, exceptionType, releaseExceptionType;
        private string sourceSequence = "", prefabName = "", gameMvid = "";
        private int sourceCount, candidateCount, prefabIndex, prefabVersion, prefabDataIndex, outputCount;
        private int subObjectEntriesRead;
        private bool? lockedEnabled;
        private bool lockedPresent;
        private uint placementFlags;
        private float width, length;
        private float elevationMin, elevationMax, edgeMin, edgeMax;
        private float3 curveA, curveB, curveC, curveD;
        private double elapsed, dependencyMilliseconds, producerMilliseconds;
        private DefinitionProducerPatchProbe patch = new DefinitionProducerPatchProbe();
        private readonly CandidateSelection selection = new CandidateSelection();

        internal StraightDefinitionProbe(World? source) { this.source = source; }
        internal bool ResourcesReleased => released && !constructorUncertain && !nativeUncertain && releaseExceptionType == null;

        internal void Run()
        {
            RequireMainThread();
            if (ran) throw new InvalidOperationException("A definition experiment may run only once.");
            ran = true;
            var timer = Stopwatch.StartNew();
            World? beforeDefault = World.DefaultGameObjectInjectionWorld;
            World[] beforeWorlds = Array.Empty<World>();
            ComponentSystemBase[] beforeSystems = Array.Empty<ComponentSystemBase>();
            ToolSystem? tools = null;
            ToolBaseSystem? beforeTool = null;
            Entity beforeSelection = Entity.Null;
            try
            {
                gameMvid = typeof(NetToolSystem).Assembly.ManifestModule.ModuleVersionId.ToString("N");
                Check(gameMvid == QualifiedGameMvid && typeof(World).Assembly.ManifestModule.ModuleVersionId.ToString("N") == QualifiedEntitiesMvid,
                    "NATIVE_BUILD_UNQUALIFIED");
                CheckMenu();
                Check(source != null && source.IsCreated, "SOURCE_WORLD_UNAVAILABLE");
                sourceSequence = source!.SequenceNumber.ToString(CultureInfo.InvariantCulture);
                beforeWorlds = CaptureWorlds(); Check(beforeWorlds.Length <= 64, "WORLD_OBSERVATION_BUDGET");
                beforeSystems = CaptureSystems(source); Check(beforeSystems.Length <= 4096, "SYSTEM_OBSERVATION_BUDGET");
                tools = source.GetExistingSystemManaged<ToolSystem>();
                Check(tools != null && ReferenceEquals(tools.World, source), "TOOL_SOURCE_UNAVAILABLE");
                beforeTool = tools!.activeTool; beforeSelection = tools.selected;
                Check(source.GetExistingSystemManaged<Reader>() == null, "READ_OWNER_ALREADY_PRESENT");
                // Construction can perform global early initialization. Do it
                // before the final source lookups, fences and candidate recheck.
                try { output = new World("SkylinesLink owned straight definitions", WorldFlags.None); }
                catch { constructorUncertain = true; throw; }
                Check(output.Systems.Count == 0, "OUTPUT_HAS_SYSTEMS");
                try { points = new NativeList<ControlPoint>(2, Allocator.TempJob); }
                catch { constructorUncertain = true; throw; }
                points.Add(Point(new float3(0, 0, 0))); points.Add(Point(new float3(64, 0, 0)));
                try { upgrades = new NativeList<NetToolSystem.UpgradeState>(0, Allocator.TempJob); }
                catch { constructorUncertain = true; throw; }
                try { commands = new EntityCommandBuffer(Allocator.TempJob, PlaybackPolicy.SinglePlayback); }
                catch { constructorUncertain = true; throw; }
                try { reader = source.CreateSystemManaged<Reader>(); }
                catch { constructorUncertain = true; throw; }
                reader.Owner = this;
                reader.Update();
                Check(produced, "PRODUCER_DID_NOT_RETURN");
                CheckMenu(); Check(ReferenceEquals(reader.World, source) && source.IsCreated, "SOURCE_WORLD_CHANGED");
                DefinitionProducerPatchProbe after = DefinitionProducerPatchProbe.Observe();
                Check(after.Qualified && after.HarmonyMvid == patch.HarmonyMvid && after.MethodsChecked == patch.MethodsChecked
                    && after.MethodSetSha256 == patch.MethodSetSha256,
                    "PRODUCER_BINDING_CHANGED");
                int beforeCount = output.EntityManager.UniversalQuery.CalculateEntityCount();
                try { commands.Playback(output.EntityManager); playedBack = true; }
                catch { nativeUncertain = true; throw; }
                outputCount = output.EntityManager.UniversalQuery.CalculateEntityCount() - beforeCount;
                Check(outputCount == 1, "OUTPUT_COUNT_MISMATCH");
                using (var query = output.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CreationDefinition>(),
                    ComponentType.ReadOnly<NetCourse>(), ComponentType.ReadOnly<Updated>()))
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    Check(entities.Length == 1 && output.EntityManager.Exists(entities[0]), "OUTPUT_NOT_OWNED");
                    CreationDefinition definition = output.EntityManager.GetComponentData<CreationDefinition>(entities[0]);
                    NetCourse course = output.EntityManager.GetComponentData<NetCourse>(entities[0]);
                    prefabMatched = definition.m_Prefab.Index == prefabIndex && definition.m_Prefab.Version == prefabVersion;
                    noOwner = definition.m_Original == Entity.Null && definition.m_Owner == Entity.Null && definition.m_Attached == Entity.Null
                        && definition.m_SubPrefab == Entity.Null && !output.EntityManager.HasComponent<OwnerDefinition>(entities[0]);
                    curveA = course.m_Curve.a; curveB = course.m_Curve.b; curveC = course.m_Curve.c; curveD = course.m_Curve.d;
                    length = course.m_Length;
                    endpointsMatched = Near(curveA, new float3(0, 0, 0)) && Near(curveB, new float3(64f / 3f, 0, 0))
                        && Near(curveC, new float3(128f / 3f, 0, 0)) && Near(curveD, new float3(64, 0, 0))
                        && Near(course.m_StartPosition.m_Position, curveA) && Near(course.m_EndPosition.m_Position, curveD)
                        && course.m_StartPosition.m_Entity == Entity.Null && course.m_EndPosition.m_Entity == Entity.Null;
                    Check(prefabMatched && noOwner && endpointsMatched && Finite(curveA) && Finite(curveB) && Finite(curveC)
                        && Finite(curveD) && Finite(length) && Math.Abs(length - 64f) <= 0.001f && course.m_FixedIndex == -1,
                        "OUTPUT_GEOMETRY_OR_REFERENCES_MISMATCH");
                }
                checksPassed = true;
            }
            catch (Exception error)
            { failureCode = error is CheckFailure check ? check.Code : "STRAIGHT_DEFINITION_FAILED"; exceptionType = error.GetType().Name; }
            finally
            {
                Release();
                try
                {
                    defaultUnchanged = ReferenceEquals(beforeDefault, World.DefaultGameObjectInjectionWorld);
                    worldsUnchanged = Same(beforeWorlds, CaptureWorlds());
                    if (source != null && source.IsCreated)
                    {
                        systemsUnchanged = Same(beforeSystems, CaptureSystems(source));
                        toolUnchanged = tools != null && ReferenceEquals(beforeTool, tools.activeTool);
                        selectionUnchanged = tools != null && beforeSelection == tools.selected;
                    }
                }
                catch (Exception error) { failureCode = failureCode ?? "FINAL_SOURCE_OBSERVATION_FAILED"; exceptionType = error.GetType().Name; }
                timer.Stop(); elapsed = timer.Elapsed.TotalMilliseconds;
            }
        }

        private void Produce(Reader owner)
        {
            CheckMenu(); Check(source != null && source.IsCreated && ReferenceEquals(source, owner.World), "SOURCE_WORLD_CHANGED");
            var prefabs = source!.GetExistingSystemManaged<PrefabSystem>();
            Check(prefabs != null && ReferenceEquals(prefabs.World, source), "PREFAB_SOURCE_UNAVAILABLE");
            var wait = Stopwatch.StartNew();
            EntityQuery query = owner.PrefabQuery();
            NetToolSystem.CreateDefinitionsJob job = Lookups(owner);
            var netData = owner.GetComponentLookup<NetData>(true);
            var locked = owner.GetComponentLookup<Locked>(true);
            var prefabData = owner.GetComponentLookup<PrefabData>(true);
            owner.JoinReads(); wait.Stop(); dependencyMilliseconds = wait.Elapsed.TotalMilliseconds;
            sourceCount = query.CalculateEntityCount(); Check(sourceCount > 0 && sourceCount <= 2048, "PREFAB_SOURCE_BUDGET_OR_EMPTY");
            Entity selected = Entity.Null;
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                Check(entities.Length == sourceCount, "PREFAB_SOURCE_CHANGED");
                foreach (Entity entity in entities)
                {
                    selection.Scanned++;
                    if (!source.EntityManager.Exists(entity) || prefabData[entity].m_Index < 0
                        || !prefabs!.TryGetPrefab<PrefabBase>(entity, out PrefabBase prefab) || prefab == null)
                    { selection.Identity++; continue; }
                    if (!(prefab is RoadPrefab road) || road.m_RoadType != RoadType.Normal)
                    { selection.RoadType++; continue; }
                    if (!prefabs.TryGetEntity(prefab, out Entity roundTrip) || roundTrip != entity)
                    { selection.Identity++; continue; }
                    string name = prefab.name;
                    if (!ValidName(name)) { selection.Name++; continue; }
                    selection.Evaluated++;
                    NetGeometryData geometry = job.m_NetGeometryData[entity]; PlaceableNetData placeable = job.m_PlaceableData[entity];
                    const Game.Net.PlacementFlags allowed = Game.Net.PlacementFlags.OnGround | Game.Net.PlacementFlags.AllowParallel
                        | Game.Net.PlacementFlags.DisallowZoneOverlay | Game.Net.PlacementFlags.DisallowPlacementOverride;
                    // Observe each rejection predicate, including overlapping
                    // reasons. Course-object inspection is bounded below;
                    // counting itself introduces no producer invocation.
                    bool rejected = Reject((netData[entity].m_ConnectLayers & Game.Net.Layer.Road) == 0, ref selection.Layer);
                    rejected |= Reject((placeable.m_PlacementFlags & Game.Net.PlacementFlags.OnGround) == 0, ref selection.OnGround);
                    rejected |= Reject((placeable.m_PlacementFlags & ~allowed) != 0, ref selection.Flags);
                    rejected |= Reject(!placeable.m_SetUpgradeFlags.Equals(default(CompositionFlags)), ref selection.SetUpgrades);
                    rejected |= Reject(!placeable.m_UnsetUpgradeFlags.Equals(default(CompositionFlags)), ref selection.UnsetUpgrades);
                    rejected |= Reject(!Range(placeable.m_ElevationRange.min, placeable.m_ElevationRange.max, 0), ref selection.Elevation);
                    rejected |= Reject(!Range(geometry.m_EdgeLengthRange.min, geometry.m_EdgeLengthRange.max, 64), ref selection.EdgeLength);
                    rejected |= Reject(!Finite(geometry.m_DefaultWidth) || geometry.m_DefaultWidth <= 0 || geometry.m_DefaultWidth > 64, ref selection.Width);
                    rejected |= Reject(!Finite(geometry.m_MaxSlopeSteepness) || geometry.m_MaxSlopeSteepness < 0, ref selection.Slope);
                    rejected |= Reject(!Ordered(geometry.m_DefaultHeightRange.min, geometry.m_DefaultHeightRange.max), ref selection.Height);
                    rejected |= Reject(!Ordered(geometry.m_DefaultSurfaceHeight.min, geometry.m_DefaultSurfaceHeight.max), ref selection.Surface);
                    rejected |= Reject(RequiresCourseObjects(job.m_PrefabSubObjects, entity), ref selection.CourseObjects);
                    rejected |= Reject(Nonempty(job.m_PrefabSubNets, entity), ref selection.SubNets);
                    rejected |= Reject(Nonempty(job.m_PrefabSubAreas, entity), ref selection.SubAreas);
                    rejected |= Reject(Nonempty(job.m_PrefabSubAreaNodes, entity), ref selection.SubAreaNodes);
                    rejected |= Reject(Nonempty(job.m_PrefabPlaceholderElements, entity), ref selection.Placeholders);
                    if (rejected) continue;
                    candidateCount++;
                    if (selected != Entity.Null && string.CompareOrdinal(name, prefabName) >= 0) continue;
                    selected = entity; prefabName = name; width = geometry.m_DefaultWidth;
                    elevationMin = placeable.m_ElevationRange.min; elevationMax = placeable.m_ElevationRange.max;
                    edgeMin = geometry.m_EdgeLengthRange.min; edgeMax = geometry.m_EdgeLengthRange.max;
                    placementFlags = (uint)placeable.m_PlacementFlags; prefabDataIndex = prefabData[entity].m_Index;
                }
            }
            selectionCompleted = true;
            Check(selected != Entity.Null, "NO_CLOSED_STRAIGHT_PREFAB");
            CheckMenu(); Check(source.IsCreated && source.SequenceNumber.ToString(CultureInfo.InvariantCulture) == sourceSequence
                && prefabs!.TryGetPrefab<PrefabBase>(selected, out PrefabBase current) && current != null
                && prefabs.TryGetEntity(current, out Entity currentEntity) && currentEntity == selected, "SOURCE_PREFAB_CHANGED");
            lockedPresent = locked.HasComponent(selected); lockedEnabled = lockedPresent ? locked.IsComponentEnabled(selected) : (bool?)null;
            prefabIndex = selected.Index; prefabVersion = selected.Version;
            patch = DefinitionProducerPatchProbe.Observe();
            Check(patch.Qualified, patch.Reason);
            job.m_NetPrefab = selected; job.m_EditorMode = false; job.m_Mode = NetToolSystem.Mode.Straight;
            job.m_ControlPoints = points; job.m_UpgradeStates = upgrades; job.m_CommandBuffer = commands;
            // NoFlow was checked from real source data. Water is not sampled by
            // this closed Execute path; default is not a valid-city-water claim.
            // default RandomSeed is an explicit fixture seed, never Next().
            var work = Stopwatch.StartNew();
            try { job.Execute(); produced = true; }
            catch { nativeUncertain = true; throw; }
            finally { work.Stop(); producerMilliseconds = work.Elapsed.TotalMilliseconds; }
        }

        private static NetToolSystem.CreateDefinitionsJob Lookups(Reader owner) => new NetToolSystem.CreateDefinitionsJob
        {
            m_EdgeData = owner.GetComponentLookup<Game.Net.Edge>(true),
            m_NodeData = owner.GetComponentLookup<Game.Net.Node>(true),
            m_CurveData = owner.GetComponentLookup<Game.Net.Curve>(true),
            m_UpgradedData = owner.GetComponentLookup<Game.Net.Upgraded>(true),
            m_FixedData = owner.GetComponentLookup<Game.Net.Fixed>(true),
            m_EditorContainerData = owner.GetComponentLookup<Game.Tools.EditorContainer>(true),
            m_OwnerData = owner.GetComponentLookup<Game.Common.Owner>(true),
            m_TempData = owner.GetComponentLookup<Temp>(true),
            m_LocalTransformCacheData = owner.GetComponentLookup<LocalTransformCache>(true),
            m_TransformData = owner.GetComponentLookup<Game.Objects.Transform>(true),
            m_AttachmentData = owner.GetComponentLookup<Game.Objects.Attachment>(true),
            m_BuildingData = owner.GetComponentLookup<Game.Buildings.Building>(true),
            m_ExtensionData = owner.GetComponentLookup<Game.Buildings.Extension>(true),
            m_PrefabRefData = owner.GetComponentLookup<PrefabRef>(true),
            m_NetGeometryData = owner.GetComponentLookup<NetGeometryData>(true),
            m_PlaceableData = owner.GetComponentLookup<PlaceableNetData>(true),
            m_PrefabSpawnableObjectData = owner.GetComponentLookup<SpawnableObjectData>(true),
            m_PrefabAreaGeometryData = owner.GetComponentLookup<AreaGeometryData>(true),
            m_ConnectedEdges = owner.GetBufferLookup<Game.Net.ConnectedEdge>(true),
            m_SubReplacements = owner.GetBufferLookup<Game.Net.SubReplacement>(true),
            m_SubNets = owner.GetBufferLookup<Game.Net.SubNet>(true),
            m_CachedNodes = owner.GetBufferLookup<LocalNodeCache>(true),
            m_SubAreas = owner.GetBufferLookup<Game.Areas.SubArea>(true),
            m_AreaNodes = owner.GetBufferLookup<Game.Areas.Node>(true),
            m_InstalledUpgrades = owner.GetBufferLookup<Game.Buildings.InstalledUpgrade>(true),
            m_PrefabSubObjects = owner.GetBufferLookup<Game.Prefabs.SubObject>(true),
            m_PrefabSubNets = owner.GetBufferLookup<Game.Prefabs.SubNet>(true),
            m_PrefabSubAreas = owner.GetBufferLookup<Game.Prefabs.SubArea>(true),
            m_PrefabSubAreaNodes = owner.GetBufferLookup<SubAreaNode>(true),
            m_PrefabPlaceholderElements = owner.GetBufferLookup<PlaceholderObjectElement>(true)
        };

        internal void Release()
        {
            RequireMainThread(); if (releaseAttempted || nativeUncertain) return; releaseAttempted = true;
            try
            {
                if (commands.IsCreated) commands.Dispose();
                if (points.IsCreated) points.Dispose();
                if (upgrades.IsCreated) upgrades.Dispose();
                if (reader != null)
                {
                    Check(source != null && source.IsCreated && ReferenceEquals(reader.World, source), "READ_OWNER_SOURCE_LOST");
                    source!.DestroySystemManaged(reader); reader = null;
                }
                if (output != null) { Check(output.IsCreated, "OUTPUT_OWNER_LOST"); output.Dispose(); output = null; }
                released = !constructorUncertain;
            }
            catch (Exception error) { nativeUncertain = true; releaseExceptionType = error.GetType().Name; }
        }

        internal JObject Read() => new JObject
        {
            ["kind"] = "straightDefinition", ["ran"] = ran,
            ["passed"] = checksPassed && selectionCompleted && ResourcesReleased && defaultUnchanged && worldsUnchanged && systemsUnchanged && toolUnchanged && selectionUnchanged && failureCode == null,
            ["resourcesReleased"] = ResourcesReleased, ["constructorUncertain"] = constructorUncertain, ["nativeUncertain"] = nativeUncertain,
            ["produced"] = produced, ["playedBackToOwnedOutput"] = playedBack, ["outputCount"] = outputCount,
            ["sourceSequence"] = sourceSequence, ["gameMvid"] = gameMvid,
            ["prefab"] = new JObject { ["name"] = prefabName, ["index"] = prefabIndex, ["version"] = prefabVersion, ["dataIndex"] = prefabDataIndex,
                ["sourceCount"] = sourceCount, ["candidateCount"] = candidateCount, ["lockedPresent"] = lockedPresent,
                ["lockedEnabled"] = lockedEnabled.HasValue ? new JValue(lockedEnabled.Value) : JValue.CreateNull(), ["placementFlags"] = placementFlags,
                ["subObjectEntriesRead"] = subObjectEntriesRead,
                ["width"] = width, ["elevationRange"] = new JArray(elevationMin, elevationMax), ["edgeRange"] = new JArray(edgeMin, edgeMax) },
            ["selectionCompleted"] = selectionCompleted, ["selection"] = selection.Read(),
            ["curve"] = new JArray(Vector(curveA), Vector(curveB), Vector(curveC), Vector(curveD)), ["length"] = length,
            ["prefabMatched"] = prefabMatched, ["noOwnerOrOriginal"] = noOwner, ["endpointsMatched"] = endpointsMatched,
            ["defaultUnchanged"] = defaultUnchanged, ["worldMembersUnchanged"] = worldsUnchanged, ["sourceSystemsUnchanged"] = systemsUnchanged,
            ["toolUnchanged"] = toolUnchanged, ["selectionUnchanged"] = selectionUnchanged,
            ["harmony"] = new JObject { ["qualifiedObservation"] = patch.Qualified, ["reason"] = patch.Reason, ["methods"] = patch.MethodsChecked,
                ["patchedMethods"] = patch.PatchedMethods, ["mvid"] = patch.HarmonyMvid, ["methodSetSha256"] = patch.MethodSetSha256 },
            ["timingMilliseconds"] = new JObject { ["total"] = elapsed, ["readSetupAndJoin"] = dependencyMilliseconds, ["producer"] = producerMilliseconds },
            ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType), ["releaseExceptionType"] = Value(releaseExceptionType),
            ["scope"] = "native synchronous NoFlow definition only; city qualification/water/Temp/validation/fees/live Apply/Cancel/async/player concurrency/performance unknown"
        };

        // Fixed-size development diagnostics, not a retained prefab inventory.
        // Identity/type/name are staged; remaining counts overlap and apply
        // only to Evaluated candidates. Their sum is not a rejected row count.
        private sealed class CandidateSelection
        {
            internal int Scanned, Identity, RoadType, Name, Evaluated;
            internal int Layer, OnGround, Flags, SetUpgrades, UnsetUpgrades, Elevation, EdgeLength, Width, Slope, Height, Surface;
            internal int CourseObjects, SubNets, SubAreas, SubAreaNodes, Placeholders;
            internal JObject Read() => new JObject
            {
                ["scanned"] = Scanned, ["identity"] = Identity, ["roadType"] = RoadType, ["name"] = Name, ["evaluated"] = Evaluated,
                ["layer"] = Layer, ["onGround"] = OnGround, ["flags"] = Flags, ["setUpgrades"] = SetUpgrades, ["unsetUpgrades"] = UnsetUpgrades,
                ["elevation"] = Elevation, ["edgeLength"] = EdgeLength, ["width"] = Width, ["slope"] = Slope, ["height"] = Height, ["surface"] = Surface,
                ["courseObjects"] = CourseObjects, ["subNets"] = SubNets, ["subAreas"] = SubAreas, ["subAreaNodes"] = SubAreaNodes, ["placeholders"] = Placeholders
            };
        }

        [DisableAutoCreation]
        public sealed partial class Reader : SystemBase
        {
            internal StraightDefinitionProbe? Owner;
            public Reader() { }
            protected override void OnUpdate() { if (Owner == null) throw new InvalidOperationException(); Owner.Produce(this); }
            internal void JoinReads() { Dependency.Complete(); Dependency = default; }
            internal EntityQuery PrefabQuery() => GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<RoadData>(), ComponentType.ReadOnly<NetData>(),
                    ComponentType.ReadOnly<NetGeometryData>(), ComponentType.ReadOnly<PlaceableNetData>() },
                None = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludePrefab
            });
        }
        private static ControlPoint Point(float3 position) => new ControlPoint
        { m_Position = position, m_HitPosition = position, m_Rotation = quaternion.identity, m_ElementIndex = new int2(-1), m_OriginalEntity = Entity.Null, m_Elevation = 0 };
        private static bool Nonempty<T>(BufferLookup<T> lookup, Entity entity) where T : unmanaged, IBufferElementData => lookup.HasBuffer(entity) && lookup[entity].Length != 0;
        private bool RequiresCourseObjects(BufferLookup<Game.Prefabs.SubObject> lookup, Entity entity)
        {
            if (!lookup.HasBuffer(entity)) return false;
            var buffer = lookup[entity];
            // Abort the entire experiment on an uninspected budget suffix;
            // never select an earlier candidate from a partial scan.
            Check(buffer.Length >= 0 && buffer.Length <= MaximumSubObjectsPerPrefab
                && buffer.Length <= MaximumSubObjectReads - subObjectEntriesRead, "PREFAB_SUBOBJECT_BUDGET");
            for (int index = 0; index < buffer.Length; index++)
            {
                var flags = buffer[index].m_Flags; subObjectEntriesRead++;
                // In the qualified Straight/GetOwnerDefinition branch only
                // these bits create course objects or an owner definition.
                if ((flags & (SubObjectFlags.MakeOwner | SubObjectFlags.CoursePlacement)) != 0) return true;
            }
            return false;
        }
        private static bool Reject(bool rejected, ref int count) { if (rejected) count++; return rejected; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(float3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Ordered(float min, float max) => Finite(min) && Finite(max) && min <= max;
        private static bool Range(float min, float max, float value) => Ordered(min, max) && min <= value && max >= value;
        private static bool Near(float3 a, float3 b) => math.all(math.abs(a - b) <= new float3(0.001f));
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static bool ValidName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i])) return false;
                if (char.IsHighSurrogate(value[i])) { if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false; }
                else if (char.IsLowSurrogate(value[i])) return false;
            }
            return true;
        }
        private static World[] CaptureWorlds()
        { var list = World.All; var result = new World[Math.Min(65, list.Count)]; for (int i = 0; i < result.Length; i++) result[i] = list[i]; return result; }
        private static ComponentSystemBase[] CaptureSystems(World world)
        { var list = world.Systems; var result = new ComponentSystemBase[Math.Min(4097, list.Count)]; for (int i = 0; i < result.Length; i++) result[i] = list[i]; return result; }
        private static bool Same<T>(T[] a, T[] b) where T : class
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false; return true; }
        private static void CheckMenu()
        { var game = GameManager.instance; Check(game != null && game.state == GameManager.State.WorldReady && game.gameMode == GameMode.MainMenu && !game.isGameLoading, "MENU_SOURCE_REQUIRED"); }
        private static void RequireMainThread() { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Main thread required."); }
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private sealed class CheckFailure : Exception { internal string Code { get; } internal CheckFailure(string code) { Code = code; } }
    }
}
#endif
