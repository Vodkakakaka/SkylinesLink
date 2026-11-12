#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // Owns only the bounded scratch used by the synchronous public helper.
    // The resulting managed values describe a proposal, not a published cache.
    internal sealed class CompositionPieceGeneration
    {
        private NativeArray<NetGeometrySection> roots;
        private NativeList<NetCompositionPiece> pieces;
        private NetCompositionPiece[]? values;
        private bool attempted, rootsAttempted, rootsAcquired, rootsReleased;
        private bool piecesAttempted, piecesAcquired, piecesReleased, helperEntered, helperReturned;
        private bool passed;
        private int upperBound, rootCount, capacityBefore, capacityAfter;
        private string? failureCode, exceptionType;

        internal bool Passed => passed && ResourcesReleased && failureCode == null;
        internal bool ResourcesReleased => (!rootsAttempted || rootsAcquired && rootsReleased)
            && (!piecesAttempted || piecesAcquired && piecesReleased);
        internal bool ContainsTerrain { get; private set; }
        internal int Count => values?.Length ?? 0;
        internal NetCompositionPiece At(int index)
        {
            Require(Passed && values != null && index >= 0 && index < values.Length,
                "COMPOSITION_PIECES_RESULT_UNAVAILABLE");
            return values![index];
        }

        internal void RunOnce(CompositionSelectionPreflight inputs, bool expectedTerrain,
            Action<NativeList<NetCompositionPiece>, NativeArray<NetGeometrySection>> invokeHelper, Action requireScope)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (invokeHelper == null) throw new ArgumentNullException(nameof(invokeHelper));
            if (requireScope == null) throw new ArgumentNullException(nameof(requireScope));
            Require(!attempted, "COMPOSITION_PIECES_ALREADY_ATTEMPTED"); attempted = true;
            try
            {
                requireScope(); inputs.RequireUnchanged(); requireScope();
                Require(inputs.Passed && inputs.PieceUpperBound >= 0 && inputs.PieceUpperBound <= 4096,
                    "COMPOSITION_PIECES_INPUT_UNQUALIFIED");
                NetGeometrySection[] geometry = inputs.CopyGeometryRoots();
                upperBound = inputs.PieceUpperBound; rootCount = geometry.Length;
                rootsAttempted = true;
                roots = new NativeArray<NetGeometrySection>(geometry, Allocator.Temp); rootsAcquired = true;
                piecesAttempted = true;
                pieces = new NativeList<NetCompositionPiece>(Math.Max(1, upperBound), Allocator.Temp); piecesAcquired = true;
                capacityBefore = pieces.Capacity;
                // These Temp allocations cannot update the Source World. The
                // same synchronous read window keeps its complete DAG proof;
                // still check live scope before the qualified RO invoker.
                requireScope();
                helperEntered = true; invokeHelper(pieces, roots); helperReturned = true;
                requireScope(); inputs.RequireUnchanged(); requireScope();
                capacityAfter = pieces.Capacity;
                Require(pieces.Length <= upperBound && capacityAfter == capacityBefore,
                    "COMPOSITION_PIECES_BOUND_CHANGED");
                var result = new NetCompositionPiece[pieces.Length];
                for (int index = 0; index < result.Length; index++)
                {
                    NetCompositionPiece value = pieces[index];
                    ContainsTerrain |= inputs.RequireGeneratedPiece(value);
                    result[index] = value;
                }
                Require(ContainsTerrain == expectedTerrain, "COMPOSITION_PIECES_TERRAIN_BRANCH_CHANGED");
                values = result;
            }
            catch (Exception error) { Record(error); throw; }
            finally
            {
                // Independent disposal attempts retain an accurate obligation
                // if either allocation or its cleanup did not return.
                if (piecesAcquired)
                    try { pieces.Dispose(); piecesReleased = true; } catch (Exception error) { Record(error); }
                if (rootsAcquired)
                    try { roots.Dispose(); rootsReleased = true; } catch (Exception error) { Record(error); }
            }
            Require(ResourcesReleased && failureCode == null, "COMPOSITION_PIECES_RELEASE_UNCONFIRMED");
            passed = true;
        }

        internal JObject Snapshot()
        {
            var rows = new JArray();
            if (values != null)
                foreach (NetCompositionPiece value in values)
                    rows.Add(new JObject {
                        ["piece"] = new JObject { ["index"] = value.m_Piece.Index, ["version"] = value.m_Piece.Version },
                        ["sectionIndex"] = value.m_SectionIndex, ["offset"] = Vector(value.m_Offset), ["size"] = Vector(value.m_Size),
                        ["sectionFlags"] = (uint)value.m_SectionFlags, ["pieceFlags"] = (uint)value.m_PieceFlags
                    });
            return new JObject {
                ["attempted"] = attempted, ["passed"] = Passed, ["helperEntered"] = helperEntered,
                ["helperReturned"] = helperReturned, ["pieceUpperBound"] = upperBound, ["geometryRootCount"] = rootCount,
                ["capacityBefore"] = capacityBefore, ["capacityAfter"] = capacityAfter, ["pieceCount"] = Count,
                ["containsTerrain"] = ContainsTerrain, ["pieces"] = rows,
                ["roots"] = Allocation(rootsAttempted, rootsAcquired, rootsReleased),
                ["result"] = Allocation(piecesAttempted, piecesAcquired, piecesReleased),
                ["resourcesReleased"] = ResourcesReleased, ["sourceCacheWritten"] = false,
                ["opaqueEcbBufferRead"] = false, ["cacheReadyQualified"] = false,
                ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType)
            };
        }

        private void Record(Exception error)
        { failureCode ??= error is Rejection rejected ? rejected.Code : "COMPOSITION_PIECES_FAILED"; exceptionType ??= error.GetType().Name; }
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static JObject Allocation(bool attempted, bool acquired, bool released) => new JObject {
            ["attempted"] = attempted, ["acquired"] = acquired, ["released"] = released
        };
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
