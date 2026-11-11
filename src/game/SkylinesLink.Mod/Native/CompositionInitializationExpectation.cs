#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // The default systems consume the published entity. The same public
    // calculations run here only on private scratch to predict their values.
    internal sealed class CompositionInitializationExpectation
    {
        private readonly CompositionCreationDefinition definition;
        private readonly NetCompositionData data;
        private readonly PlaceableNetComposition? placeable;
        private readonly NetCompositionPiece[] pieces;
        private readonly bool objects, areas, crosswalks, carriageways;

        private CompositionInitializationExpectation(CompositionCreationDefinition definition,
            NetCompositionData data, PlaceableNetComposition? placeable, NetCompositionPiece[] pieces,
            CompositionInitializationPreflight inputs)
        {
            this.definition = definition;
            this.data = data;
            this.placeable = placeable;
            this.pieces = pieces;
            objects = inputs.HasOutput<NetCompositionObject>();
            areas = inputs.HasOutput<NetCompositionArea>();
            crosswalks = inputs.HasOutput<NetCompositionCrosswalk>();
            carriageways = inputs.HasOutput<NetCompositionCarriageway>();
        }

        internal static CompositionInitializationExpectation Capture(CompositionCreationDefinition definition,
            CompositionInitializationPreflight inputs, ComponentLookup<NetPieceData> pieceData,
            ComponentLookup<NetVertexMatchData> vertexData, ComponentLookup<MeshData> meshData,
            ComponentLookup<PlaceableNetPieceData> placeableData)
        {
            NetGeometryData geometry = inputs.RequireEmptyOutputBranch(definition);
            var expectedPieces = definition.CopyPieces();
            foreach (var piece in expectedPieces)
                Require((piece.m_PieceFlags & NetPieceFlags.HasMesh) == 0, "COMPOSITION_INITIALIZATION_MESH_BRANCH_UNQUALIFIED");

            var scratch = new NativeArray<NetCompositionPiece>(expectedPieces, Allocator.Temp);
            var expectedData = definition.Data;
            PlaceableNetComposition? expectedPlaceable = null;
            try
            {
                NetCompositionHelpers.CalculateCompositionData(ref expectedData, scratch, pieceData, vertexData);
                NetCompositionHelpers.CalculateMinLod(ref expectedData, scratch, meshData);
                if ((geometry.m_Flags & GeometryFlags.Marker) != 0)
                    expectedData.m_State |= CompositionState.Marker | CompositionState.NoSubCollisions;
                if ((geometry.m_Flags & GeometryFlags.BlockZone) != 0)
                    expectedData.m_State |= CompositionState.BlockZone;
                if (inputs.HasOutput<PlaceableNetComposition>())
                {
                    var value = default(PlaceableNetComposition);
                    NetCompositionHelpers.CalculatePlaceableData(ref value, scratch, placeableData);
                    expectedPlaceable = value;
                }
                scratch.CopyTo(expectedPieces);
            }
            finally { scratch.Dispose(); }

            // A readback identical to the prepared Data would not establish
            // that the default initialization processed this exact entity.
            Require(!expectedData.Equals(definition.Data), "COMPOSITION_INITIALIZATION_NO_DATA_DELTA");
            foreach (var piece in expectedPieces)
                Require((piece.m_PieceFlags & NetPieceFlags.HasMesh) == 0, "COMPOSITION_INITIALIZATION_MESH_BRANCH_UNQUALIFIED");
            return new CompositionInitializationExpectation(definition, expectedData, expectedPlaceable, expectedPieces, inputs);
        }

        internal void RequireReady(Entity entity, Action requireScope)
        {
            requireScope();
            var manager = definition.Source.EntityManager;
            Require(entity != Entity.Null && manager.Exists(entity) && !manager.HasComponent<Disabled>(entity)
                && !manager.HasComponent<ObjectGenerationScope>(entity) && !manager.HasComponent<Deleted>(entity)
                && !manager.HasComponent<Created>(entity), "COMPOSITION_DEFAULT_ENTITY_NOT_READY");
            Require(manager.GetComponentData<PrefabRef>(entity).Equals(definition.Prefab)
                && manager.GetComponentData<NetCompositionData>(entity).Equals(data), "COMPOSITION_DEFAULT_DATA_MISMATCH");
            Require(manager.GetComponentData<NetCompositionMeshRef>(entity).Equals(default(NetCompositionMeshRef)),
                "COMPOSITION_DEFAULT_MESH_REF_MISMATCH");
            Require(manager.HasComponent<PlaceableNetComposition>(entity) == placeable.HasValue
                && (!placeable.HasValue || manager.GetComponentData<PlaceableNetComposition>(entity).Equals(placeable.Value)),
                "COMPOSITION_DEFAULT_PLACEABLE_MISMATCH");
            var actualPieces = manager.GetBuffer<NetCompositionPiece>(entity, true);
            Require(actualPieces.Length == pieces.Length, "COMPOSITION_DEFAULT_PIECE_COUNT_MISMATCH");
            for (int index = 0; index < pieces.Length; index++)
                Require(actualPieces[index].Equals(pieces[index]), "COMPOSITION_DEFAULT_PIECE_MISMATCH");
            RequireEmpty<NetCompositionObject>(manager, entity, objects);
            RequireEmpty<NetCompositionArea>(manager, entity, areas);
            RequireEmpty<NetCompositionCrosswalk>(manager, entity, crosswalks);
            RequireEmpty<NetCompositionCarriageway>(manager, entity, carriageways);
            Require(!manager.HasBuffer<NetCompositionLane>(entity), "COMPOSITION_DEFAULT_LANE_BRANCH_CHANGED");
            requireScope();
        }

        private static void RequireEmpty<T>(EntityManager manager, Entity entity, bool present) where T : unmanaged, IBufferElementData
        {
            Require(manager.HasBuffer<T>(entity) == present && (!present || manager.GetBuffer<T>(entity, true).Length == 0),
                "COMPOSITION_DEFAULT_BUFFER_MISMATCH");
        }

        private static void Require(bool condition, string code)
        { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }
    }
}
#endif
