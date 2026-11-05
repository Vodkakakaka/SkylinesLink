#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A complete copied definition. It grants neither publication nor cache readiness.
    internal sealed class CompositionCreationDefinition
    {
        private readonly NetCompositionPiece[] pieces;
        internal CompositionCreationDefinition(CompositionSelectionPreflight inputs, EntityArchetype archetype,
            PrefabRef prefab, NetCompositionData data, TerrainComposition? terrain, CompositionPieceGeneration generated)
        {
            if (!inputs.Passed || !generated.Passed || generated.ContainsTerrain != terrain.HasValue
                || prefab.m_Prefab != inputs.Prefab || !archetype.Valid
                || !data.Equals(new NetCompositionData { m_Flags = data.m_Flags })
                || (data.m_Flags.m_General & CompositionFlags.General.Node) == 0)
                throw new InvalidOperationException("COMPOSITION_DEFINITION_UNQUALIFIED");
            Source = inputs.Source; Prefab = prefab; Data = data; Terrain = terrain; SourceArchetype = archetype;
            pieces = new NetCompositionPiece[generated.Count];
            for (int index = 0; index < pieces.Length; index++) pieces[index] = generated.At(index);
        }
        internal World Source { get; }
        internal EntityArchetype SourceArchetype { get; }
        internal PrefabRef Prefab { get; }
        internal NetCompositionData Data { get; }
        internal TerrainComposition? Terrain { get; }
        internal int PieceCount => pieces.Length;
        internal NetCompositionPiece PieceAt(int index) => pieces[index];
        internal NetCompositionPiece[] CopyPieces() => (NetCompositionPiece[])pieces.Clone();
    }
}
#endif
