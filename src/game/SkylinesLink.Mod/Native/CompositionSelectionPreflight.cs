#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using Attached = Game.Objects.Attached;
using NativeElevation = Game.Net.Elevation;
using NativeNode = Game.Net.Node;
using NativeOrphan = Game.Net.Orphan;
using NativeTransform = Game.Objects.Transform;
using SubObject = Game.Objects.SubObject;

namespace SkylinesLink.Mod.Native
{
    // A read-only qualification of this single, new Orphan. The complete
    // section DAG is conservative while the native selected mask is unknown;
    // rejecting a cycle here does not define the eventual product boundary.
    internal sealed class CompositionSelectionPreflight
    {
        private const int TableLimit = 256, SectionLimit = 256, RowLimit = 4096, PieceLimit = 4096;
        private readonly Action requireScope, requireLinkedChildren;
        private State? before;
        private bool attempted, passed;
        private int unchangedChecks;
        private NativeOrphan currentOrphan;
        private bool currentOrphanObserved;
        private string? failureCode, exceptionType;

        internal CompositionSelectionPreflight(World source, Entity root, Entity node, int actor,
            Action requireScope, Action requireLinkedChildren)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            this.requireScope = requireScope ?? throw new ArgumentNullException(nameof(requireScope));
            this.requireLinkedChildren = requireLinkedChildren ?? throw new ArgumentNullException(nameof(requireLinkedChildren));
            Require(root != Entity.Null && node != Entity.Null && root != node && root.Index >= 0
                && node.Index >= 0 && root.Version > 0 && node.Version > 0 && actor > 0,
                "COMPOSITION_PREFLIGHT_IDENTITY_REJECTED");
            Root = root; Node = node; Actor = actor;
        }

        internal World Source { get; }
        internal Entity Root { get; }
        internal Entity Node { get; }
        internal int Actor { get; }
        internal Entity Prefab => before?.NodePrefab.m_Prefab ?? Entity.Null;
        internal NativeOrphan BeforeOrphan => before?.Orphan ?? default;
        internal bool Passed => passed;
        // Only managed copies are made: no query, NativeArray, buffer view,
        // native container or dependency is retained by this preflight.
        internal bool ResourcesReleased => true;
        internal int CacheCount => passed && before != null ? before.Cache.Length : 0;
        internal int PieceUpperBound => passed && before != null ? before.PieceUpperBound : 0;

        internal NetGeometryComposition CacheAt(int index)
        {
            Require(passed && before != null && index >= 0 && index < before.Cache.Length,
                "COMPOSITION_PREFLIGHT_CACHE_UNAVAILABLE");
            return before!.Cache[index].Row;
        }

        internal NetGeometrySection[] CopyGeometryRoots()
        {
            Require(passed && before != null, "COMPOSITION_PREFLIGHT_NOT_QUALIFIED");
            return (NetGeometrySection[])before!.GeometryRoots.Clone();
        }

        internal bool RequireGeneratedPiece(NetCompositionPiece piece)
        {
            Require(passed && before != null && before.Pieces.ContainsKey(piece.m_Piece)
                && piece.m_SectionIndex >= 0 && piece.m_SectionIndex < before.GeometryRoots.Length
                && Finite(piece.m_Offset) && piece.m_Size.Equals(float3.zero),
                "COMPOSITION_PREFLIGHT_GENERATED_PIECE_UNQUALIFIED");
            return before!.Pieces[piece.m_Piece].Terrain.HasValue;
        }

        internal void RunOnce()
        {
            Require(!attempted, "COMPOSITION_PREFLIGHT_ALREADY_USED"); attempted = true;
            try
            {
                State captured = Capture(requireEmptyOrphan: true);
                before = captured; currentOrphan = captured.Orphan; currentOrphanObserved = true; passed = true;
            }
            catch (Exception error) { Record(error); throw; }
        }

        internal void RequireUnchanged()
        {
            Require(passed && before != null, "COMPOSITION_PREFLIGHT_NOT_QUALIFIED");
            try
            {
                State fresh = Capture(requireEmptyOrphan: false);
                Require(before!.SameInputs(fresh), "COMPOSITION_PREFLIGHT_INPUT_CHANGED");
                currentOrphan = fresh.Orphan; currentOrphanObserved = true; unchangedChecks++;
            }
            catch (Exception error) { Record(error); throw; }
        }

        internal NativeOrphan ReadCurrentOrphan()
        {
            Require(passed && before != null, "COMPOSITION_PREFLIGHT_NOT_QUALIFIED");
            try
            {
                Guard(); Scoped(Node); currentOrphan = Read<NativeOrphan>(Node);
                currentOrphanObserved = true; Guard(); return currentOrphan;
            }
            catch (Exception error) { Record(error); throw; }
        }

        private State Capture(bool requireEmptyOrphan)
        {
            Guard(); requireLinkedChildren(); Guard();
            var state = new State();
            var city = Source.GetExistingSystemManaged<CityConfigurationSystem>();
            var prefabs = Source.GetExistingSystemManaged<PrefabSystem>();
            Guard();
            Require(city != null && prefabs != null && ReferenceEquals(city.World, Source)
                && ReferenceEquals(prefabs.World, Source), "COMPOSITION_PREFLIGHT_PROVIDERS_UNAVAILABLE");
            state.City = city; state.Prefabs = prefabs; state.LeftHandTraffic = city!.leftHandTraffic; Guard();

            Scoped(Root); Scoped(Node);
            state.RootTemp = NewParent(Root, 9); state.NodeTemp = NewParent(Node, 5);
            Require(Has<Game.Objects.Object>(Root) && !Has<Owner>(Root)
                && !Has<Game.Net.Edge>(Root) && !Has<NativeOrphan>(Root) && !Has<Game.Net.Composition>(Root)
                && Has<NativeNode>(Node) && !Has<Game.Net.Edge>(Node) && Has<NativeOrphan>(Node)
                && !Has<Game.Net.Composition>(Node) && !Has<Game.Net.Upgraded>(Node)
                && !Has<OwnerDefinition>(Root) && !Has<OwnerDefinition>(Node),
                "COMPOSITION_PREFLIGHT_ROLE_CHANGED");
            state.NodeOwner = Read<Owner>(Node);
            Require(state.NodeOwner.m_Owner == Root, "COMPOSITION_PREFLIGHT_OWNER_CHANGED");
            state.RootTransform = Read<NativeTransform>(Root); state.NodeData = Read<NativeNode>(Node);
            Require(Finite(state.RootTransform.m_Position) && Finite(state.RootTransform.m_Rotation.value)
                && Finite(state.NodeData.m_Position) && Finite(state.NodeData.m_Rotation.value),
                "COMPOSITION_PREFLIGHT_GEOMETRY_INVALID");
            state.Elevation = Optional<NativeElevation>(Node);
            Require(!state.Elevation.HasValue || Finite(state.Elevation.Value.m_Elevation),
                "COMPOSITION_PREFLIGHT_ELEVATION_INVALID");
            state.Orphan = Read<NativeOrphan>(Node);
            Require(!requireEmptyOrphan || state.Orphan.m_Composition == Entity.Null,
                "COMPOSITION_PREFLIGHT_INITIAL_ORPHAN_NOT_EMPTY");
            state.ConnectedEdges = Buffer<Game.Net.ConnectedEdge>(Node, true, state)!;
            Require(state.ConnectedEdges.Length == 0, "COMPOSITION_PREFLIGHT_CONNECTED_EDGE_BRANCH_UNSUPPORTED");

            state.RootPrefab = Read<PrefabRef>(Root); state.NodePrefab = Read<PrefabRef>(Node);
            state.RootPrefabData = Registered(state.RootPrefab.m_Prefab, prefabs!);
            state.NodePrefabData = Registered(state.NodePrefab.m_Prefab, prefabs!);
            state.RootChildren = Buffer<SubObject>(Root, true, state)!;
            state.NodeChildren = Buffer<SubObject>(Node, true, state)!;
            Require(state.RootChildren.Length == 4 && state.NodeChildren.Length == 1,
                "COMPOSITION_PREFLIGHT_CHILD_COHORT_CHANGED");
            state.Child = state.NodeChildren[0].m_SubObject;
            Require(state.Child != Root && state.Child != Node, "COMPOSITION_PREFLIGHT_CHILD_IDENTITY_REJECTED");
            Scoped(state.Child); state.ChildOwner = Read<Owner>(state.Child);
            Require(state.ChildOwner.m_Owner == Node, "COMPOSITION_PREFLIGHT_CHILD_OWNER_CHANGED");
            state.ChildTemp = Read<Temp>(state.Child);
            Require(state.ChildTemp.m_Original == Entity.Null && state.ChildTemp.m_Flags == TempFlags.Create,
                "COMPOSITION_PREFLIGHT_CHILD_NOT_NEW");
            state.ChildPrefab = Read<PrefabRef>(state.Child);
            state.ChildPrefabData = Registered(state.ChildPrefab.m_Prefab, prefabs!);
            state.ChildAttached = Optional<Attached>(state.Child);
            Require(!state.ChildAttached.HasValue || state.ChildAttached.Value.m_Parent == Node
                && (state.ChildAttached.Value.m_OldParent == Entity.Null || state.ChildAttached.Value.m_OldParent == Node)
                && Finite(state.ChildAttached.Value.m_CurvePosition), "COMPOSITION_PREFLIGHT_FOREIGN_ATTACHMENT");
            state.ChildNetObject = Optional<NetObjectData>(state.ChildPrefab.m_Prefab);
            state.ChildPlaceable = Optional<PlaceableObjectData>(state.ChildPrefab.m_Prefab);
            Require(!state.ChildPlaceable.HasValue || Finite(state.ChildPlaceable.Value.m_PlacementOffset),
                "COMPOSITION_PREFLIGHT_PLACEMENT_INVALID");

            state.Net = Read<NetData>(state.NodePrefab.m_Prefab);
            state.Geometry = Read<NetGeometryData>(state.NodePrefab.m_Prefab);
            Require(Finite(state.Net.m_NodePriority) && Finite(state.Geometry.m_ElevationLimit)
                && state.Geometry.m_NodeCompositionArchetype.Valid,
                "COMPOSITION_PREFLIGHT_PREFAB_DATA_INVALID");
            Reference(state.Geometry.m_AggregateType, optional: true);
            Reference(state.Geometry.m_StyleType, optional: true);
            state.NodeStates = Buffer<NetGeometryNodeState>(state.NodePrefab.m_Prefab, false, state);
            var cache = Buffer<NetGeometryComposition>(state.NodePrefab.m_Prefab, true, state)!;
            state.Cache = new CacheRow[cache.Length];
            for (int index = 0; index < cache.Length; index++)
            {
                NetGeometryComposition row = cache[index]; Reference(row.m_Composition, optional: false);
                PrefabRef prefab = Read<PrefabRef>(row.m_Composition);
                NetCompositionData data = Read<NetCompositionData>(row.m_Composition);
                Require(prefab.m_Prefab == state.NodePrefab.m_Prefab && data.m_Flags == row.m_Mask,
                    "COMPOSITION_PREFLIGHT_CACHE_IDENTITY_MISMATCH");
                var pieces = Buffer<NetCompositionPiece>(row.m_Composition, true, state)!;
                foreach (NetCompositionPiece piece in pieces)
                {
                    Piece(piece.m_Piece, state, prefabs!);
                    Require(Finite(piece.m_Offset) && Finite(piece.m_Size), "COMPOSITION_PREFLIGHT_CACHE_PIECE_INVALID");
                }
                state.Cache[index] = new CacheRow(row, prefab, data, pieces);
            }

            state.GeometryRoots = Buffer<NetGeometrySection>(state.NodePrefab.m_Prefab, true, state)!;
            var active = new HashSet<Entity>();
            foreach (NetGeometrySection geometry in state.GeometryRoots)
            {
                Require(Finite(geometry.m_Offset), "COMPOSITION_PREFLIGHT_SECTION_OFFSET_INVALID");
                int maximum = Section(geometry.m_Section, state, active, prefabs!);
                Require(maximum <= PieceLimit - state.PieceUpperBound, "COMPOSITION_PREFLIGHT_PIECE_BUDGET");
                // A repeated geometry root may independently emit the same
                // section's pieces and must therefore be counted again.
                state.PieceUpperBound += maximum;
            }
            Guard(); requireLinkedChildren(); Guard();
            return state;
        }

        private int Section(Entity entity, State state, HashSet<Entity> active, PrefabSystem prefabs)
        {
            Require(!active.Contains(entity), "COMPOSITION_PREFLIGHT_SECTION_CYCLE");
            if (state.Sections.TryGetValue(entity, out SectionRow existing)) return existing.MaximumPieces;
            Require(state.Sections.Count < SectionLimit, "COMPOSITION_PREFLIGHT_SECTION_BUDGET");
            PrefabData data = Registered(entity, prefabs);
            var children = Buffer<NetSubSection>(entity, true, state)!;
            var pieces = Buffer<NetSectionPiece>(entity, true, state)!;
            // Register before descending so the unique-node budget also
            // includes nodes still on the recursion stack.
            var row = new SectionRow(data, children, pieces); state.Sections.Add(entity, row);
            active.Add(entity);
            try
            {
                int maximum = pieces.Length;
                foreach (NetSectionPiece piece in pieces)
                {
                    Require(Finite(piece.m_Offset), "COMPOSITION_PREFLIGHT_PIECE_OFFSET_INVALID");
                    Piece(piece.m_Piece, state, prefabs);
                }
                state.SectionEdges += children.Length;
                foreach (NetSubSection child in children)
                    maximum = Math.Max(maximum, Section(child.m_SubSection, state, active, prefabs));
                row.MaximumPieces = maximum;
                return maximum;
            }
            finally { active.Remove(entity); }
        }

        private void Piece(Entity entity, State state, PrefabSystem prefabs)
        {
            if (state.Pieces.ContainsKey(entity)) return;
            PrefabData data = Registered(entity, prefabs);
            // CreateComposition tests this presence to emit TerrainComposition;
            // no terrain helper or composition initialization is invoked here.
            state.Pieces.Add(entity, new PieceRow(data, Optional<NetTerrainData>(entity)));
        }

        private Temp NewParent(Entity entity, uint flags)
        {
            Require(Has<Created>(entity) && Has<Updated>(entity) && !Has<Deleted>(entity),
                "COMPOSITION_PREFLIGHT_PARENT_MARKERS_CHANGED");
            Temp value = Read<Temp>(entity);
            Require(value.m_Original == Entity.Null && (uint)value.m_Flags == flags && Finite(value.m_CurvePosition),
                "COMPOSITION_PREFLIGHT_PARENT_NOT_NEW");
            return value;
        }

        private void Scoped(Entity entity)
        {
            Reference(entity, optional: false);
            Guard(); bool scoped = Source.EntityManager.HasComponent<ObjectGenerationScope>(entity); Guard();
            Require(Has<Disabled>(entity) && scoped && Has<Temp>(entity),
                "COMPOSITION_PREFLIGHT_SCOPE_CHANGED");
            Guard(); int value = Source.EntityManager.GetSharedComponent<ObjectGenerationScope>(entity).Value; Guard();
            Require(value == Actor && !Has<Deleted>(entity), "COMPOSITION_PREFLIGHT_ACTOR_CHANGED");
        }

        private PrefabData Registered(Entity entity, PrefabSystem prefabs)
        {
            Reference(entity, optional: false);
            PrefabData data = Read<PrefabData>(entity);
            bool found = prefabs.TryGetPrefab<PrefabBase>(entity, out PrefabBase managed); Guard();
            Require(found && managed != null, "COMPOSITION_PREFLIGHT_PREFAB_UNAVAILABLE");
            bool roundtrip = prefabs.TryGetEntity(managed, out Entity actual); Guard();
            Require(roundtrip && actual == entity, "COMPOSITION_PREFLIGHT_PREFAB_ROUNDTRIP_FAILED");
            return data;
        }

        private void Reference(Entity entity, bool optional)
        {
            if (optional && entity == Entity.Null) return;
            Guard();
            Require(entity != Entity.Null && entity.Index >= 0 && entity.Version > 0,
                "COMPOSITION_PREFLIGHT_REFERENCE_INVALID");
            bool exists = Source.EntityManager.Exists(entity); Guard();
            Require(exists && !Has<Deleted>(entity), "COMPOSITION_PREFLIGHT_REFERENCE_UNAVAILABLE");
        }

        private bool Has<T>(Entity entity) where T : unmanaged, IComponentData
        { Guard(); bool result = Source.EntityManager.HasComponent<T>(entity); Guard(); return result; }

        private T Read<T>(Entity entity) where T : unmanaged, IComponentData
        {
            Require(Has<T>(entity), "COMPOSITION_PREFLIGHT_COMPONENT_MISSING");
            Guard(); T result = Source.EntityManager.GetComponentData<T>(entity); Guard(); return result;
        }

        private T? Optional<T>(Entity entity) where T : unmanaged, IComponentData
            => Has<T>(entity) ? Read<T>(entity) : (T?)null;

        private T[]? Buffer<T>(Entity entity, bool required, State state) where T : unmanaged, IBufferElementData
        {
            Guard(); bool present = Source.EntityManager.HasBuffer<T>(entity); Guard();
            Require(present || !required, "COMPOSITION_PREFLIGHT_BUFFER_MISSING");
            if (!present) return null;
            // The view exists only inside CopyBuffer, which makes no callbacks.
            // It has gone out of scope before the next Source/scope callback.
            T[] result = CopyBuffer<T>(entity, state); Guard(); return result;
        }

        private T[] CopyBuffer<T>(Entity entity, State state) where T : unmanaged, IBufferElementData
        {
            DynamicBuffer<T> buffer = Source.EntityManager.GetBuffer<T>(entity, true);
            int count = buffer.Length;
            Require(count >= 0 && count <= TableLimit && count <= RowLimit - state.TotalRows,
                "COMPOSITION_PREFLIGHT_TABLE_BUDGET");
            state.TotalRows += count;
            var result = new T[count];
            for (int index = 0; index < count; index++) result[index] = buffer[index];
            return result;
        }

        private void Guard()
        {
            Require(MainThreadDispatcher.isMainThread, "COMPOSITION_PREFLIGHT_MAIN_THREAD_REQUIRED");
            requireScope();
            GameManager manager = GameManager.instance;
            Require(Source.IsCreated && manager != null && manager.state == GameManager.State.WorldReady
                && manager.gameMode == GameMode.Game && !manager.isGameLoading,
                "COMPOSITION_PREFLIGHT_SOURCE_NOT_READY");
        }

        internal JObject Snapshot()
        {
            var rows = new JArray();
            if (before != null)
                foreach (CacheRow row in before.Cache)
                    rows.Add(new JObject { ["entity"] = EntityValue(row.Row.m_Composition), ["mask"] = Flags(row.Row.m_Mask),
                        ["prefabMatches"] = row.Prefab.m_Prefab == before.NodePrefab.m_Prefab,
                        ["dataFlags"] = Flags(row.Data.m_Flags), ["pieces"] = row.Pieces.Length });
            return new JObject {
                ["attempted"] = attempted, ["passed"] = passed, ["unchangedChecks"] = unchangedChecks,
                ["root"] = EntityValue(Root), ["node"] = EntityValue(Node), ["actor"] = Actor,
                ["prefab"] = EntityValue(Prefab), ["beforeOrphan"] = EntityValue(BeforeOrphan.m_Composition),
                ["currentOrphan"] = currentOrphanObserved ? EntityValue(currentOrphan.m_Composition) : JValue.CreateNull(),
                ["leftHandTraffic"] = before == null ? JValue.CreateNull() : new JValue(before.LeftHandTraffic),
                ["rootTempFlags"] = before == null ? JValue.CreateNull() : new JValue((uint)before.RootTemp.m_Flags),
                ["nodeTempFlags"] = before == null ? JValue.CreateNull() : new JValue((uint)before.NodeTemp.m_Flags),
                ["nodeSubObjects"] = before?.NodeChildren.Length ?? 0,
                ["childNetObject"] = before?.ChildNetObject.HasValue ?? false,
                ["childPlaceable"] = before?.ChildPlaceable.HasValue ?? false,
                ["childAttached"] = before?.ChildAttached.HasValue ?? false,
                ["childCompositionFlags"] = before?.ChildNetObject is NetObjectData netObject
                    ? Flags(netObject.m_CompositionFlags) : JValue.CreateNull(),
                ["cacheCount"] = before?.Cache.Length ?? 0, ["cache"] = rows,
                ["nodeStateRows"] = before?.NodeStates?.Length ?? 0,
                ["geometryRoots"] = before?.GeometryRoots.Length ?? 0, ["uniqueSections"] = before?.Sections.Count ?? 0,
                ["sectionEdges"] = before?.SectionEdges ?? 0, ["totalRows"] = before?.TotalRows ?? 0,
                ["pieceUpperBound"] = PieceUpperBound, ["baselineComplete"] = before != null,
                ["allReachableSectionsRead"] = passed && before != null,
                ["orphanValueExcludedFromUnchangedCheck"] = true, ["derivedCompositionDataReadyVerified"] = false,
                ["nativeAllocations"] = 0, ["resourcesReleased"] = ResourcesReleased, ["nativeInvoked"] = false,
                ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
            };
        }

        private sealed class State
        {
            internal CityConfigurationSystem? City;
            internal PrefabSystem? Prefabs;
            internal bool LeftHandTraffic;
            internal Temp RootTemp, NodeTemp, ChildTemp;
            internal Owner NodeOwner, ChildOwner;
            internal NativeTransform RootTransform;
            internal NativeNode NodeData;
            internal NativeOrphan Orphan;
            internal NativeElevation? Elevation;
            internal PrefabRef RootPrefab, NodePrefab, ChildPrefab;
            internal PrefabData RootPrefabData, NodePrefabData, ChildPrefabData;
            internal Entity Child;
            internal Attached? ChildAttached;
            internal NetObjectData? ChildNetObject;
            internal PlaceableObjectData? ChildPlaceable;
            internal NetData Net;
            internal NetGeometryData Geometry;
            internal Game.Net.ConnectedEdge[] ConnectedEdges = Array.Empty<Game.Net.ConnectedEdge>();
            internal SubObject[] RootChildren = Array.Empty<SubObject>(), NodeChildren = Array.Empty<SubObject>();
            internal NetGeometryNodeState[]? NodeStates;
            internal CacheRow[] Cache = Array.Empty<CacheRow>();
            internal NetGeometrySection[] GeometryRoots = Array.Empty<NetGeometrySection>();
            internal readonly Dictionary<Entity, SectionRow> Sections = new Dictionary<Entity, SectionRow>();
            internal readonly Dictionary<Entity, PieceRow> Pieces = new Dictionary<Entity, PieceRow>();
            internal int SectionEdges, TotalRows, PieceUpperBound;

            internal bool SameInputs(State other)
            {
                if (!ReferenceEquals(City, other.City) || !ReferenceEquals(Prefabs, other.Prefabs)
                    || LeftHandTraffic != other.LeftHandTraffic || !Same(RootTemp, other.RootTemp)
                    || !Same(NodeTemp, other.NodeTemp) || !Same(ChildTemp, other.ChildTemp)
                    || !Same(NodeOwner, other.NodeOwner) || !Same(ChildOwner, other.ChildOwner)
                    || !Same(RootTransform, other.RootTransform) || !Same(NodeData, other.NodeData)
                    || !Nullable.Equals(Elevation, other.Elevation) || !Same(RootPrefab, other.RootPrefab)
                    || !Same(NodePrefab, other.NodePrefab) || !Same(ChildPrefab, other.ChildPrefab)
                    || !Same(RootPrefabData, other.RootPrefabData) || !Same(NodePrefabData, other.NodePrefabData)
                    || !Same(ChildPrefabData, other.ChildPrefabData) || Child != other.Child
                    || !Nullable.Equals(ChildAttached, other.ChildAttached) || !Nullable.Equals(ChildNetObject, other.ChildNetObject)
                    || !Nullable.Equals(ChildPlaceable, other.ChildPlaceable) || !Same(Net, other.Net) || !Same(Geometry, other.Geometry)
                    || !SameRows(ConnectedEdges, other.ConnectedEdges) || !SameRows(RootChildren, other.RootChildren)
                    || !SameRows(NodeChildren, other.NodeChildren) || !SameRows(NodeStates, other.NodeStates)
                    || !SameRows(GeometryRoots, other.GeometryRoots) || Cache.Length != other.Cache.Length
                    || Sections.Count != other.Sections.Count || Pieces.Count != other.Pieces.Count
                    || SectionEdges != other.SectionEdges || TotalRows != other.TotalRows || PieceUpperBound != other.PieceUpperBound)
                    return false;
                for (int index = 0; index < Cache.Length; index++)
                    if (!Cache[index].Same(other.Cache[index])) return false;
                foreach (var entry in Sections)
                    if (!other.Sections.TryGetValue(entry.Key, out SectionRow row) || !entry.Value.Same(row)) return false;
                foreach (var entry in Pieces)
                    if (!other.Pieces.TryGetValue(entry.Key, out PieceRow row) || !entry.Value.Same(row)) return false;
                // Only this node's output Orphan is intentionally not compared.
                return true;
            }
        }

        private sealed class CacheRow
        {
            internal readonly NetGeometryComposition Row;
            internal readonly PrefabRef Prefab;
            internal readonly NetCompositionData Data;
            internal readonly NetCompositionPiece[] Pieces;
            internal CacheRow(NetGeometryComposition row, PrefabRef prefab, NetCompositionData data, NetCompositionPiece[] pieces)
            { Row = row; Prefab = prefab; Data = data; Pieces = pieces; }
            internal bool Same(CacheRow other) => CompositionSelectionPreflight.Same(Row, other.Row)
                && CompositionSelectionPreflight.Same(Prefab, other.Prefab)
                && CompositionSelectionPreflight.Same(Data, other.Data) && SameRows(Pieces, other.Pieces);
        }

        private sealed class SectionRow
        {
            internal readonly PrefabData Data;
            internal readonly NetSubSection[] Children;
            internal readonly NetSectionPiece[] Pieces;
            internal int MaximumPieces;
            internal SectionRow(PrefabData data, NetSubSection[] children, NetSectionPiece[] pieces)
            { Data = data; Children = children; Pieces = pieces; }
            internal bool Same(SectionRow other) => CompositionSelectionPreflight.Same(Data, other.Data)
                && SameRows(Children, other.Children) && SameRows(Pieces, other.Pieces) && MaximumPieces == other.MaximumPieces;
        }

        private sealed class PieceRow
        {
            internal readonly PrefabData Data;
            internal readonly NetTerrainData? Terrain;
            internal PieceRow(PrefabData data, NetTerrainData? terrain) { Data = data; Terrain = terrain; }
            internal bool Same(PieceRow other) => CompositionSelectionPreflight.Same(Data, other.Data)
                && Nullable.Equals(Terrain, other.Terrain);
        }

        private static bool Same<T>(T left, T right) where T : struct => EqualityComparer<T>.Default.Equals(left, right);
        private static bool SameRows<T>(T[]? left, T[]? right) where T : struct
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (!Same(left[index], right[index])) return false;
            return true;
        }
        private static JObject Flags(CompositionFlags value) => new JObject {
            ["general"] = (uint)value.m_General, ["left"] = (uint)value.m_Left, ["right"] = (uint)value.m_Right };
        private static JObject EntityValue(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(float2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(float3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private void Record(Exception error)
        {
            passed = false; failureCode = error is Rejection rejected ? rejected.Code : "COMPOSITION_PREFLIGHT_READ_FAILED";
            exceptionType = error.GetType().Name;
        }
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : InvalidOperationException
        { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
