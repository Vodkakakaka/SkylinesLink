#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // Read admission for the current Node-only DataInit branch. This captures
    // inputs; it neither initializes a composition nor establishes mesh readiness.
    // Crosswalk dependencies are a conservative potential-read set until the
    // native geometry stage has supplied the sizes used by its short circuits.
    internal sealed class CompositionInitializationPreflight
    {
        private const int PieceLimit = 4096, RowLimit = 8192, EntityLimit = 4096, TypeLimit = 4096;
        private const long ComparisonLimit = 65536;
        private readonly CompositionCreationDefinition definition;
        private readonly Action requireScope;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private State? before;
        private bool attempted, passed, snapshotsReleased;
        private int unchangedChecks, captures, typeCount, dependencyCount, uniquePieces, copiedRows;
        private int vertexNaNOffsets;
        private long inputRows, objectRows, areaRows, laneRows, crosswalkBound, comparisonBound;
        private string? typeDigest, failureCode;
        private string[] branchTypes = Array.Empty<string>();
        private bool? prefabEnabled;

        internal CompositionInitializationPreflight(CompositionCreationDefinition definition, Action requireScope)
        {
            this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
            this.requireScope = requireScope ?? throw new ArgumentNullException(nameof(requireScope));
        }

        internal bool Passed => passed;
        // This is the physical Temp metadata-array obligation, not a claim that
        // managed snapshots or the engine's composition outputs occupy no memory.
        internal bool ResourcesReleased => archetypes.ResourcesReleased;

        internal NetGeometryData RequireEmptyOutputBranch(CompositionCreationDefinition candidate)
        {
            Guard();
            State state = before ?? throw new Rejection("COMPOSITION_INITIALIZATION_SNAPSHOT_UNAVAILABLE");
            Require(ReferenceEquals(definition, candidate) && passed && state.PrefabEnabled
                && !state.Terrain && !state.Road.HasValue && !state.Track.HasValue && !state.Waterway.HasValue
                && !state.Pathway.HasValue && !state.Taxiway.HasValue
                && state.ObjectRows == 0 && state.AreaRows == 0 && state.CrosswalkBound == 0,
                "COMPOSITION_INITIALIZATION_OUTPUT_BRANCH_UNQUALIFIED");
            return state.Geometry;
        }

        internal bool HasOutput<T>() where T : unmanaged
        {
            State state = before ?? throw new Rejection("COMPOSITION_INITIALIZATION_SNAPSHOT_UNAVAILABLE");
            return Contains<T>(state);
        }

        internal void RunOnce()
        {
            Require(!attempted && !snapshotsReleased, "COMPOSITION_INITIALIZATION_ALREADY_USED");
            attempted = true;
            try { before = Capture(); passed = true; }
            catch (Exception error) { Record(error); throw; }
        }

        internal void RequireUnchanged()
        {
            Require(passed && before != null && !snapshotsReleased, "COMPOSITION_INITIALIZATION_SNAPSHOT_UNAVAILABLE");
            try
            {
                State fresh = Capture();
                Require(before!.Same(fresh), "COMPOSITION_INITIALIZATION_INPUT_CHANGED");
                unchangedChecks++;
            }
            catch (Exception error) { Record(error); throw; }
        }

        internal void ReleaseSnapshots()
        {
            before = null;
            snapshotsReleased = true;
        }

        private State Capture()
        {
            Guard();
            Require(definition.PieceCount >= 0 && definition.PieceCount <= PieceLimit,
                "COMPOSITION_INITIALIZATION_PIECE_BUDGET");
            var state = new State { InputRows = definition.PieceCount };
            captures++;
            try
            {
                state.Types = archetypes.Read(definition.SourceArchetype, TypeLimit); Guard();
                Require(Contains<NetCompositionData>(state) && Contains<NetCompositionPiece>(state)
                    && Contains<NetCompositionMeshRef>(state) && Contains<PrefabRef>(state)
                    && Contains<Created>(state) && Contains<Updated>(state),
                    "COMPOSITION_INITIALIZATION_HEADER_MISSING");
                Require((definition.Data.m_Flags.m_General & CompositionFlags.General.Node) != 0
                    && !Contains<NetCompositionLane>(state), "COMPOSITION_INITIALIZATION_LANE_BRANCH_UNQUALIFIED");
                state.TypeDigest = NativeEntityArchetypes.TypeDigest(state.Types);
                state.Objects = Contains<NetCompositionObject>(state);
                state.Areas = Contains<NetCompositionArea>(state);
                state.Crosswalks = Contains<NetCompositionCrosswalk>(state);
                state.Placeable = Contains<PlaceableNetComposition>(state);
                state.Terrain = Contains<TerrainComposition>(state) || definition.Terrain.HasValue;

                Entity prefab = definition.Prefab.m_Prefab;
                Reference(prefab, state);
                state.Prefab = Read<PrefabData>(prefab);
                state.Geometry = Read<NetGeometryData>(prefab);
                Require(state.Geometry.m_NodeCompositionArchetype.Equals(definition.SourceArchetype),
                    "COMPOSITION_INITIALIZATION_ARCHETYPE_CHANGED");
                state.PrefabEnabled = definition.Source.EntityManager.IsComponentEnabled<PrefabData>(prefab); Guard();
                state.PrefabEnabledObserved = true;
                state.Road = Contains<RoadComposition>(state) ? Read<RoadData>(prefab) : (RoadData?)null;
                state.Track = Contains<TrackComposition>(state) ? Read<TrackData>(prefab) : (TrackData?)null;
                state.Waterway = Contains<WaterwayComposition>(state) ? Read<WaterwayData>(prefab) : (WaterwayData?)null;
                state.Pathway = Contains<PathwayComposition>(state) ? Read<PathwayData>(prefab) : (PathwayData?)null;
                state.Taxiway = Contains<TaxiwayComposition>(state) ? Read<TaxiwayData>(prefab) : (TaxiwayData?)null;
                state.Bridge = state.Areas && (definition.Data.m_Flags.m_General & CompositionFlags.General.Elevated) != 0
                    && Has<BridgeData>(prefab);

                // Count occurrences before copying any native table. A repeated
                // prefab still contributes again to the native loops/output bound.
                for (int index = 0; index < definition.PieceCount; index++)
                {
                    NetCompositionPiece piece = definition.PieceAt(index);
                    if (!state.Occurrences.TryGetValue(piece.m_Piece, out Occurrences count)) count = default;
                    count.Total++;
                    if ((piece.m_SectionFlags & (NetSectionFlags.Underground | NetSectionFlags.Overhead)) == 0) count.Vertex++;
                    if ((piece.m_PieceFlags & NetPieceFlags.Surface) != 0) count.Surface++;
                    state.Occurrences[piece.m_Piece] = count;
                    Require(state.Occurrences.Count <= EntityLimit, "COMPOSITION_INITIALIZATION_ENTITY_BUDGET");
                }
                foreach (KeyValuePair<Entity, Occurrences> entry in state.Occurrences)
                    CapturePiece(entry.Key, entry.Value, state);

                // The native object helper filters rows using only these flags;
                // offsets/data calculations do not change them.
                for (int index = 0; index < definition.PieceCount; index++)
                {
                    NetCompositionPiece piece = definition.PieceAt(index);
                    PieceInputs input = state.Pieces[piece.m_Piece];
                    if (input.Objects == null) continue;
                    bool invert = (piece.m_SectionFlags & NetSectionFlags.Invert) != 0;
                    CompositionFlags flags = invert ? NetCompositionHelpers.InvertCompositionFlags(definition.Data.m_Flags) : definition.Data.m_Flags;
                    NetSectionFlags section = invert ? NetCompositionHelpers.InvertSectionFlags(piece.m_SectionFlags) : piece.m_SectionFlags;
                    foreach (NetPieceObject row in input.Objects)
                        if (NetCompositionHelpers.TestObjectFlags(row, flags, section)) CaptureObject(row.m_Prefab, state);
                }
                Guard();
                return state;
            }
            finally { CacheSummary(state); }
        }

        private void CapturePiece(Entity entity, Occurrences count, State state)
        {
            Reference(entity, state);
            var input = new PieceInputs {
                Data = Read<NetPieceData>(entity), LodBias = Read<MeshData>(entity).m_LodBias,
                Vertex = count.Vertex != 0 ? Optional<NetVertexMatchData>(entity) : null,
                Placeable = state.Placeable ? Optional<PlaceableNetPieceData>(entity) : null,
                Terrain = state.Terrain ? Optional<NetTerrainData>(entity) : null,
                Crosswalk = state.Crosswalks && count.Surface != 0 ? Optional<NetCrosswalkData>(entity) : null
            };
            if (state.Objects) input.Objects = Table<NetPieceObject>(entity, count.Total, TableKind.Objects, state);
            if (state.Areas) input.Areas = Table<NetPieceArea>(entity, count.Total, TableKind.Areas, state);
            if (input.Crosswalk.HasValue)
            {
                state.CrosswalkBound += count.Surface;
                NetCrosswalkData crosswalk = input.Crosswalk.Value;
                // A null crosswalk lane is a native no-output possibility.
                // Non-null potentially emitted lanes must support its strong read.
                if (crosswalk.m_Lane != Entity.Null) CaptureLane(crosswalk.m_Lane, state);
                input.Lanes = Table<NetPieceLane>(entity, count.Surface, TableKind.Lanes, state);
                if (input.Lanes != null)
                    foreach (NetPieceLane lane in input.Lanes)
                        if (lane.m_Position.x >= crosswalk.m_Start.x && lane.m_Position.x <= crosswalk.m_End.x)
                            CaptureLane(lane.m_Lane, state);
            }
            state.Pieces.Add(entity, input);
        }

        private void CaptureObject(Entity entity, State state)
        {
            if (state.ObjectInputs.ContainsKey(entity)) return;
            // Native HasComponent checks accept a null object reference; it is
            // recorded rather than upgraded into a required prefab qualification.
            if (entity == Entity.Null) { state.ObjectInputs.Add(entity, default); return; }
            Guard();
            Require(entity.Index >= 0 && entity.Version > 0, "COMPOSITION_INITIALIZATION_REFERENCE_INVALID");
            AddDependency(entity, state);
            bool exists = definition.Source.EntityManager.Exists(entity); Guard();
            // This path performs only optional native HasComponent reads. A
            // missing entity is their legitimate false result, not a strong read.
            if (!exists) { state.ObjectInputs.Add(entity, default); return; }
            state.ObjectInputs.Add(entity, new ObjectInputs {
                Exists = true, Light = Optional<StreetLightData>(entity), Utility = Has<UtilityObjectData>(entity),
                Direction = Has<LaneDirectionData>(entity), Sign = Has<TrafficSignData>(entity)
            });
        }

        private void CaptureLane(Entity entity, State state)
        {
            if (state.LaneInputs.ContainsKey(entity)) return;
            Reference(entity, state);
            state.LaneInputs.Add(entity, Read<NetLaneData>(entity));
        }

        private enum TableKind { Objects, Areas, Lanes }

        private T[]? Table<T>(Entity entity, int occurrences, TableKind kind, State state) where T : unmanaged, IBufferElementData
        {
            Guard(); bool exists = definition.Source.EntityManager.HasBuffer<T>(entity); Guard();
            if (!exists) return null;
            T[] rows = CopyTable<T>(entity, occurrences, kind, state); Guard();
            return rows;
        }

        private T[] CopyTable<T>(Entity entity, int occurrences, TableKind kind, State state) where T : unmanaged, IBufferElementData
        {
            // The borrowed view does not cross a scope callback. Length and all
            // multiplicative work bounds are checked before array allocation.
            DynamicBuffer<T> buffer = definition.Source.EntityManager.GetBuffer<T>(entity, true);
            int length = buffer.Length;
            long rows = (long)length * occurrences;
            Require(length >= 0 && occurrences > 0 && rows <= RowLimit - state.InputRows,
                "COMPOSITION_INITIALIZATION_ROW_BUDGET");
            if (kind == TableKind.Objects)
            {
                long objects = state.ObjectRows + rows;
                long comparisons = objects * (objects - 1) / 2;
                Require(comparisons <= ComparisonLimit, "COMPOSITION_INITIALIZATION_COMPARISON_BUDGET");
                state.ObjectRows = objects; state.ComparisonBound = comparisons;
            }
            else if (kind == TableKind.Areas) state.AreaRows += rows;
            else state.LaneRows += rows;
            state.InputRows += rows;
            var result = new T[length];
            for (int index = 0; index < length; index++) result[index] = buffer[index];
            state.CopiedRows += length;
            return result;
        }

        private void Reference(Entity entity, State state)
        {
            Guard();
            Require(entity != Entity.Null && entity.Index >= 0 && entity.Version > 0,
                "COMPOSITION_INITIALIZATION_REFERENCE_INVALID");
            AddDependency(entity, state);
            bool exists = definition.Source.EntityManager.Exists(entity); Guard();
            Require(exists, "COMPOSITION_INITIALIZATION_REFERENCE_UNAVAILABLE");
        }

        private static void AddDependency(Entity entity, State state)
        {
            if (state.Entities.Contains(entity)) return;
            Require(state.Entities.Count < EntityLimit, "COMPOSITION_INITIALIZATION_ENTITY_BUDGET");
            state.Entities.Add(entity);
        }

        private bool Has<T>(Entity entity) where T : unmanaged, IComponentData
        { Guard(); bool value = definition.Source.EntityManager.HasComponent<T>(entity); Guard(); return value; }

        private T Read<T>(Entity entity) where T : unmanaged, IComponentData
        {
            Require(Has<T>(entity), "COMPOSITION_INITIALIZATION_COMPONENT_MISSING");
            T value = definition.Source.EntityManager.GetComponentData<T>(entity); Guard(); return value;
        }

        private T? Optional<T>(Entity entity) where T : unmanaged, IComponentData => Has<T>(entity) ? Read<T>(entity) : (T?)null;

        private static bool Contains<T>(State state) where T : unmanaged
            => state.Types.ContainsKey(TypeManager.GetTypeIndex<T>());

        private void Guard()
        {
            Require(MainThreadDispatcher.isMainThread, "COMPOSITION_INITIALIZATION_MAIN_THREAD_REQUIRED");
            requireScope();
            Require(definition.Source.IsCreated, "COMPOSITION_INITIALIZATION_SOURCE_UNAVAILABLE");
        }

        private void CacheSummary(State state)
        {
            typeCount = state.Types.Count; typeDigest = state.TypeDigest;
            dependencyCount = state.Entities.Count; uniquePieces = state.Pieces.Count; copiedRows = state.CopiedRows;
            inputRows = state.InputRows; objectRows = state.ObjectRows; areaRows = state.AreaRows; laneRows = state.LaneRows;
            crosswalkBound = state.CrosswalkBound; comparisonBound = state.ComparisonBound;
            prefabEnabled = state.PrefabEnabledObserved ? state.PrefabEnabled : (bool?)null;
            vertexNaNOffsets = 0;
            foreach (PieceInputs piece in state.Pieces.Values)
                if (piece.Vertex.HasValue)
                {
                    float3 offsets = piece.Vertex.Value.m_Offsets;
                    if (float.IsNaN(offsets.x)) vertexNaNOffsets++;
                    if (float.IsNaN(offsets.y)) vertexNaNOffsets++;
                    if (float.IsNaN(offsets.z)) vertexNaNOffsets++;
                }
            var names = new List<string>();
            foreach (Type type in new[] { typeof(NetCompositionData), typeof(NetCompositionPiece), typeof(NetCompositionMeshRef),
                typeof(NetCompositionObject), typeof(NetCompositionArea), typeof(NetCompositionCrosswalk), typeof(NetCompositionCarriageway),
                typeof(PlaceableNetComposition), typeof(RoadComposition), typeof(TrackComposition), typeof(WaterwayComposition),
                typeof(PathwayComposition), typeof(TaxiwayComposition), typeof(TerrainComposition), typeof(NetCompositionLane) })
                if (state.Types.ContainsKey(TypeManager.GetTypeIndex(type))) names.Add(type.FullName!);
            if (state.Terrain && !Contains<TerrainComposition>(state)) names.Add(typeof(TerrainComposition).FullName!);
            branchTypes = names.ToArray();
        }

        internal JObject Snapshot() => new JObject {
            ["attempted"] = attempted, ["passed"] = passed, ["captures"] = captures, ["unchangedChecks"] = unchangedChecks,
            ["resourcesReleased"] = ResourcesReleased, ["typeArrays"] = archetypes.Snapshot(),
            ["managedSnapshotsReleased"] = snapshotsReleased, ["managedSnapshotRetained"] = before != null,
            ["archetypeTypeCount"] = typeCount, ["archetypeTypeDigest"] = Text(typeDigest), ["branchTypes"] = new JArray(branchTypes),
            ["pieceOccurrences"] = definition.PieceCount, ["uniquePieceInputs"] = uniquePieces, ["dependencyEntities"] = dependencyCount,
            ["inputRows"] = inputRows, ["copiedTableRows"] = copiedRows, ["objectRowOccurrences"] = objectRows,
            ["areaRowOccurrences"] = areaRows, ["crosswalkLaneRowOccurrences"] = laneRows,
            ["objectComparisonUpperBound"] = passed ? new JValue(comparisonBound) : JValue.CreateNull(),
            ["objectOutputUpperBound"] = passed ? new JValue(objectRows) : JValue.CreateNull(),
            ["areaOutputUpperBound"] = passed ? new JValue(areaRows) : JValue.CreateNull(),
            ["crosswalkOutputUpperBound"] = passed ? new JValue(crosswalkBound) : JValue.CreateNull(),
            ["carriagewayOutputUpperBound"] = passed ? new JValue(0) : JValue.CreateNull(),
            ["prefabDataEnabled"] = prefabEnabled.HasValue ? new JValue(prefabEnabled.Value) : JValue.CreateNull(),
            ["pieceLimit"] = PieceLimit, ["inputRowLimit"] = RowLimit, ["entityLimit"] = EntityLimit,
            ["typeLimit"] = TypeLimit, ["objectComparisonLimit"] = ComparisonLimit,
            ["vertexOffsetNaNSentinelsPreserved"] = true, ["initializationInvoked"] = false,
            ["vertexNaNOffsetsObserved"] = vertexNaNOffsets,
            ["crosswalkReadSet"] = "conservativePotentialReads", ["crosswalkControlFlowReplayed"] = false,
            ["sharedCachePublished"] = false, ["meshLodReferencesInspected"] = false, ["cacheReadyQualified"] = false,
            ["failureCode"] = Text(failureCode)
        };

        private sealed class State
        {
            internal Dictionary<TypeIndex, ComponentType> Types = new Dictionary<TypeIndex, ComponentType>();
            internal string? TypeDigest;
            internal PrefabData Prefab;
            internal NetGeometryData Geometry;
            internal bool PrefabEnabled, PrefabEnabledObserved, Objects, Areas, Crosswalks, Placeable, Terrain, Bridge;
            internal RoadData? Road;
            internal TrackData? Track;
            internal WaterwayData? Waterway;
            internal PathwayData? Pathway;
            internal TaxiwayData? Taxiway;
            internal int CopiedRows;
            internal long InputRows, ObjectRows, AreaRows, LaneRows, CrosswalkBound, ComparisonBound;
            internal readonly HashSet<Entity> Entities = new HashSet<Entity>();
            internal readonly Dictionary<Entity, Occurrences> Occurrences = new Dictionary<Entity, Occurrences>();
            internal readonly Dictionary<Entity, PieceInputs> Pieces = new Dictionary<Entity, PieceInputs>();
            internal readonly Dictionary<Entity, ObjectInputs> ObjectInputs = new Dictionary<Entity, ObjectInputs>();
            internal readonly Dictionary<Entity, NetLaneData> LaneInputs = new Dictionary<Entity, NetLaneData>();

            internal bool Same(State other)
            {
                if (Types.Count != other.Types.Count || TypeDigest != other.TypeDigest || !Prefab.Equals(other.Prefab)
                    || !Geometry.Equals(other.Geometry) || PrefabEnabled != other.PrefabEnabled || Bridge != other.Bridge
                    || !Nullable.Equals(Road, other.Road) || !Nullable.Equals(Track, other.Track)
                    || !Nullable.Equals(Waterway, other.Waterway) || !Nullable.Equals(Pathway, other.Pathway)
                    || !Nullable.Equals(Taxiway, other.Taxiway) || InputRows != other.InputRows
                    || !Entities.SetEquals(other.Entities) || Pieces.Count != other.Pieces.Count
                    || ObjectInputs.Count != other.ObjectInputs.Count || LaneInputs.Count != other.LaneInputs.Count) return false;
                foreach (KeyValuePair<Entity, PieceInputs> entry in Pieces)
                    if (!other.Pieces.TryGetValue(entry.Key, out PieceInputs found) || !entry.Value.Same(found)) return false;
                foreach (KeyValuePair<Entity, ObjectInputs> entry in ObjectInputs)
                    if (!other.ObjectInputs.TryGetValue(entry.Key, out ObjectInputs found) || !entry.Value.Same(found)) return false;
                foreach (KeyValuePair<Entity, NetLaneData> entry in LaneInputs)
                    if (!other.LaneInputs.TryGetValue(entry.Key, out NetLaneData found) || !entry.Value.Equals(found)) return false;
                return true;
            }
        }

        private struct Occurrences { internal int Total, Vertex, Surface; }

        private sealed class PieceInputs
        {
            internal NetPieceData Data;
            internal float LodBias;
            internal NetVertexMatchData? Vertex;
            internal PlaceableNetPieceData? Placeable;
            internal NetTerrainData? Terrain;
            internal NetCrosswalkData? Crosswalk;
            internal NetPieceObject[]? Objects;
            internal NetPieceArea[]? Areas;
            internal NetPieceLane[]? Lanes;
            internal bool Same(PieceInputs other) => Data.Equals(other.Data) && SameFloat(LodBias, other.LodBias)
                && SameVertex(Vertex, other.Vertex) && Nullable.Equals(Placeable, other.Placeable)
                && Nullable.Equals(Terrain, other.Terrain) && Nullable.Equals(Crosswalk, other.Crosswalk)
                && SameRows(Objects, other.Objects) && SameRows(Areas, other.Areas) && SameRows(Lanes, other.Lanes);
        }

        private struct ObjectInputs
        {
            internal StreetLightData? Light;
            internal bool Exists, Utility, Direction, Sign;
            internal bool Same(ObjectInputs other) => Nullable.Equals(Light, other.Light)
                && Exists == other.Exists && Utility == other.Utility && Direction == other.Direction && Sign == other.Sign;
        }

        private static bool SameVertex(NetVertexMatchData? left, NetVertexMatchData? right)
        {
            if (!left.HasValue || !right.HasValue) return left.HasValue == right.HasValue;
            float3 a = left.Value.m_Offsets, b = right.Value.m_Offsets;
            return SameFloat(a.x, b.x) && SameFloat(a.y, b.y) && SameFloat(a.z, b.z);
        }
        private static bool SameFloat(float a, float b) => a == b || float.IsNaN(a) && float.IsNaN(b);
        private static bool SameRows<T>(T[]? left, T[]? right) where T : struct
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (!EqualityComparer<T>.Default.Equals(left[index], right[index])) return false;
            return true;
        }
        private void Record(Exception error)
        { passed = false; failureCode ??= error is Rejection rejection ? rejection.Code : "COMPOSITION_INITIALIZATION_READ_FAILED"; }
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
