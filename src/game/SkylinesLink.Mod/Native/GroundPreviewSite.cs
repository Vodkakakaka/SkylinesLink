#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using AreaTriangle = Game.Areas.Triangle;
using NativeTile = Game.Common.Native;

namespace SkylinesLink.Mod.Native
{
    // One inland ground sample for the isolated preview. All cross-frame
    // evidence is copied values; neither terrain nor water storage is owned here.
    internal sealed class GroundPreviewSite
    {
        private const int TileLimit = 64, TileNodeLimit = 64, TileTriangleLimit = 128, CandidateLimit = 128;
        private const float Clearance = 24f, SampleRadius = 8f, WaterDepthLimit = 0.05f, GradeLimit = 0.12f;
        private static readonly float2[] SampleOffsets =
        {
            new float2(0f), new float2(SampleRadius, 0f), new float2(-SampleRadius, 0f),
            new float2(0f, SampleRadius), new float2(0f, -SampleRadius)
        };

        private readonly World source;
        private readonly Entity prefab;
        private Entity tile;
        private float2 horizontal;
        private Observation observed;
        private int ownedTiles, candidates, surfaceSamples, freshAttempts, freshConfirmed;

        private GroundPreviewSite(World source, Entity prefab)
        {
            this.source = source;
            this.prefab = prefab;
        }

        internal static GroundPreviewSite Select(World source, Entity rootPrefab, Action scope, Func<float2, bool>? acceptsPoint = null)
        {
            EntityManager entities = RequireContext(source, rootPrefab, scope);
            var placement = ReadPlacement(entities, rootPrefab);
            var site = new GroundPreviewSite(source, rootPrefab);
            ReadSurfaces(source, scope, out var terrain, out var water);
            using var query = entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MapTile>(), ComponentType.ReadOnly<Area>() },
                None = new[] { ComponentType.ReadOnly<NativeTile>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Disabled>() }
            });
            site.ownedTiles = query.CalculateEntityCount();
            Require(site.ownedTiles > 0 && site.ownedTiles <= TileLimit, "GROUND_SITE_OWNED_TILE_BUDGET");
            using var tiles = query.ToEntityArray(Allocator.Temp);
            Require(tiles.Length == site.ownedTiles, "GROUND_SITE_OWNED_TILE_COUNT_CHANGED");

            foreach (Entity tile in tiles)
            {
                scope();
                ReadTile(entities, tile, out var nodes, out var triangles);
                for (int index = 0; index < triangles.Length; index++)
                {
                    int3 triangle = triangles[index];
                    float2 a = nodes[triangle.x], b = nodes[triangle.y], c = nodes[triangle.z];
                    // Centre, then one interior sample towards each vertex.
                    for (int variant = 0; variant < 4; variant++)
                    {
                        Require(site.candidates < CandidateLimit, "GROUND_SITE_CANDIDATE_BUDGET");
                        site.candidates++;
                        float2 point = variant switch
                        {
                            0 => (a + b + c) / 3f,
                            1 => 0.6f * a + 0.2f * (b + c),
                            2 => 0.6f * b + 0.2f * (a + c),
                            _ => 0.6f * c + 0.2f * (a + b)
                        };
                        float clearance = InteriorClearance(point, a, b, c);
                        if (clearance < Clearance || math.lengthsq(point) < 16f * 16f || acceptsPoint?.Invoke(point) == false
                            || !site.TryObserve(point, index, clearance, placement, ref terrain, ref water, out var value)) continue;
                        scope();
                        site.tile = tile; site.horizontal = point; site.observed = value;
                        return site;
                    }
                }
            }

            throw new Rejection("GROUND_SITE_NOT_FOUND");
        }

        internal ControlPoint RequireCurrent(World source, Entity rootPrefab, Action scope, out float appliedOffset)
        {
            Require(ReferenceEquals(this.source, source) && prefab == rootPrefab, "GROUND_SITE_IDENTITY_CHANGED");
            EntityManager entities = RequireContext(source, rootPrefab, scope);
            var placement = ReadPlacement(entities, rootPrefab);
            freshAttempts++;
            ReadTile(entities, tile, out var nodes, out var triangles);
            ReadSurfaces(source, scope, out var terrain, out var water);
            for (int index = 0; index < triangles.Length; index++)
            {
                int3 face = triangles[index];
                float clearance = InteriorClearance(horizontal, nodes[face.x], nodes[face.y], nodes[face.z]);
                if (clearance < Clearance) continue;
                Require(TryObserve(horizontal, index, clearance, placement, ref terrain, ref water, out var value),
                    "GROUND_SITE_SURFACE_CHANGED");
                Require(value.Flags == observed.Flags && value.FrontSample == observed.FrontSample
                    && value.HeightSample.xz.Equals(observed.HeightSample.xz)
                    && value.PrefabOffset.Equals(observed.PrefabOffset), "GROUND_SITE_PLACEMENT_CHANGED");
                scope();
                observed = value;
                freshConfirmed++;
                appliedOffset = value.AppliedOffset;
                return new ControlPoint
                {
                    m_OriginalEntity = Entity.Null,
                    m_Position = new float3(value.Point.x, value.HeightSample.y + appliedOffset, value.Point.z),
                    m_HitPosition = value.Point,
                    m_HitDirection = value.Normal,
                    m_Rotation = quaternion.identity,
                    m_ElementIndex = new int2(-1)
                };
            }

            throw new Rejection("GROUND_SITE_TILE_TRIANGLE_CHANGED");
        }

        private static Placement ReadPlacement(EntityManager entities, Entity prefab)
        {
            var data = entities.GetComponentData<PlaceableObjectData>(prefab);
            const PlacementFlags unsupported = PlacementFlags.RoadEdge | PlacementFlags.RoadNode | PlacementFlags.OwnerSide
                | PlacementFlags.Wall | PlacementFlags.Hanging | PlacementFlags.Hovering | PlacementFlags.Shoreline
                | PlacementFlags.Underwater | PlacementFlags.Waterway | PlacementFlags.NetObject | PlacementFlags.Attached;
            Require((data.m_Flags & unsupported) == 0 && (data.m_Flags & PlacementFlags.OnGround) != 0
                && !entities.HasComponent<OutsideConnectionData>(prefab) && !entities.HasComponent<StackData>(prefab)
                && math.isfinite(data.m_PlacementOffset.y), "GROUND_PREVIEW_SPECIAL_PLACEMENT");
            bool frontSample = (data.m_Flags & PlacementFlags.RoadSide) != 0 && entities.HasComponent<BuildingData>(prefab);
            int lotLength = frontSample ? entities.GetComponentData<BuildingData>(prefab).m_LotSize.y : 0;
            return new Placement(data.m_Flags, data.m_PlacementOffset.y, lotLength, frontSample);
        }

        private static EntityManager RequireContext(World source, Entity rootPrefab, Action scope)
        {
            if (!MainThreadDispatcher.isMainThread || source == null || scope == null)
                throw new Rejection("GROUND_SITE_SCOPE_UNAVAILABLE");
            scope();
            Require(source.IsCreated && rootPrefab != Entity.Null, "GROUND_SITE_SCOPE_UNAVAILABLE");
            EntityManager entities = source.EntityManager;
            Require(entities.Exists(rootPrefab) && entities.HasComponent<PlaceableObjectData>(rootPrefab),
                "GROUND_SITE_PREFAB_UNAVAILABLE");
            return entities;
        }

        private static void ReadTile(EntityManager entities, Entity tile, out float2[] nodes, out int3[] triangles)
        {
            // The game's MapTilePurchaseSystem counts MapTile without Native as owned.
            Require(entities.Exists(tile) && entities.HasComponent<MapTile>(tile) && entities.HasComponent<Area>(tile)
                && !entities.HasComponent<NativeTile>(tile) && !entities.HasComponent<Temp>(tile)
                && !entities.HasComponent<Deleted>(tile) && !entities.HasComponent<Disabled>(tile),
                "GROUND_SITE_TILE_NOT_OWNED");
            Require(entities.HasBuffer<AreaNode>(tile) && entities.HasBuffer<AreaTriangle>(tile), "GROUND_SITE_TILE_GEOMETRY_UNAVAILABLE");
            var nativeNodes = entities.GetBuffer<AreaNode>(tile, isReadOnly: true);
            var nativeTriangles = entities.GetBuffer<AreaTriangle>(tile, isReadOnly: true);
            Require(nativeNodes.Length >= 3 && nativeNodes.Length <= TileNodeLimit
                && nativeTriangles.Length > 0 && nativeTriangles.Length <= TileTriangleLimit, "GROUND_SITE_TILE_GEOMETRY_BUDGET");
            nodes = new float2[nativeNodes.Length];
            triangles = new int3[nativeTriangles.Length];
            for (int index = 0; index < nodes.Length; index++)
            {
                float3 position = nativeNodes[index].m_Position;
                Require(math.all(math.isfinite(position)), "GROUND_SITE_TILE_NODE_INVALID");
                nodes[index] = position.xz;
            }
            for (int index = 0; index < triangles.Length; index++)
            {
                int3 face = nativeTriangles[index].m_Indices;
                Require(math.all(face >= 0) && math.all(face < nodes.Length) && face.x != face.y && face.y != face.z && face.z != face.x,
                    "GROUND_SITE_TILE_TRIANGLE_INVALID");
                triangles[index] = face;
            }
        }

        private static void ReadSurfaces(World source, Action scope, out TerrainHeightData terrain, out WaterSurfaceData<SurfaceWater> water)
        {
            scope();
            var terrainSystem = source.GetExistingSystemManaged<TerrainSystem>();
            var waterSystem = source.GetExistingSystemManaged<WaterSystem>();
            if (terrainSystem == null || waterSystem == null || !ReferenceEquals(terrainSystem.World, source)
                || !ReferenceEquals(waterSystem.World, source))
                throw new Rejection("GROUND_SITE_SURFACE_UNAVAILABLE");
            water = waterSystem.GetSurfaceData(out var writers);
            writers.Complete();
            scope();
            terrain = terrainSystem.GetHeightData();
            Require(terrain.isCreated && UsableGrid(terrain.resolution, terrain.scale, terrain.offset, terrain.heights.Length)
                && water.isCreated && water.hasDepths && UsableGrid(water.resolution, water.scale, water.offset, water.depths.Length),
                "GROUND_SITE_SURFACE_GRID_UNAVAILABLE");
        }

        private static bool UsableGrid(int3 resolution, float3 scale, float3 offset, int length) =>
            resolution.x > 2 && resolution.z > 2 && (long)resolution.x * resolution.z == length
            && math.all(math.isfinite(scale)) && math.all(scale > 0f) && math.all(math.isfinite(offset));

        private static bool InGrid(float2 point, int3 resolution) =>
            math.all(math.isfinite(point)) && math.all(point >= 0f) && math.all(point < new float2(resolution.x - 1, resolution.z - 1));

        private bool TryObserve(float2 point, int triangle, float clearance, Placement placement,
            ref TerrainHeightData terrain, ref WaterSurfaceData<SurfaceWater> water, out Observation observation)
        {
            observation = default;
            float height = 0f, depth = 0f, maxDepth = 0f, maxGrade = 0f;
            float3 normal = default;
            for (int index = 0; index < SampleOffsets.Length; index++)
            {
                if (!TrySample(point + SampleOffsets[index], ref terrain, ref water,
                    out float localHeight, out float localDepth, out var localNormal)) return false;
                if (index == 0) { height = localHeight; depth = localDepth; normal = localNormal; }
                maxDepth = math.max(maxDepth, localDepth);
                maxGrade = math.max(maxGrade, math.abs(localHeight - height) / SampleRadius);
            }

            var ground = new float3(point.x, height, point.y);
            var heightSample = ground;
            if (placement.FrontSample)
            {
                // CalculateHeight samples the front of RoadSide buildings even
                // with road snapping off and no original entity.
                float2 front = BuildingUtils.CalculateFrontPosition(new Transform(ground, quaternion.identity), placement.LotLength).xz;
                float distance = math.distance(point, front);
                if (distance >= clearance || !TrySample(front, ref terrain, ref water, out float frontHeight, out float frontDepth, out _))
                    return false;
                heightSample = new float3(front.x, frontHeight, front.y);
                maxDepth = math.max(maxDepth, frontDepth);
                if (distance > 0f) maxGrade = math.max(maxGrade, math.abs(frontHeight - height) / distance);
            }

            if (maxDepth > WaterDepthLimit || maxGrade > GradeLimit) return false;
            float appliedOffset = placement.Offset;
            if ((placement.Flags & PlacementFlags.Floating) != 0)
            {
                // The native floating branch only adds its offset when the
                // water query finds at least 0.2 m of water at the hit.
                _ = WaterUtils.SampleHeight(ref water, ref terrain, ground, out float floatingDepth);
                if (!math.isfinite(floatingDepth) || floatingDepth < 0f || floatingDepth > WaterDepthLimit) return false;
                appliedOffset = 0f;
            }

            if (!math.isfinite(heightSample.y + appliedOffset)) return false;
            observation = new Observation(ground, normal, heightSample, appliedOffset, placement,
                depth, maxDepth, maxGrade, triangle, clearance);
            return true;
        }

        private bool TrySample(float2 point, ref TerrainHeightData terrain, ref WaterSurfaceData<SurfaceWater> water,
            out float height, out float depth, out float3 normal)
        {
            height = depth = 0f;
            normal = default;
            var sample = new float3(point.x, 0f, point.y);
            float2 waterPoint = WaterUtils.ToSurfaceSpace(ref water, sample).xz;
            if (!InGrid(TerrainUtils.ToHeightmapSpace(ref terrain, sample).xz, terrain.resolution)
                || !InGrid(waterPoint, water.resolution) || !FiniteWaterCell(ref water, waterPoint)) return false;
            height = TerrainUtils.SampleHeight(ref terrain, sample, out normal);
            depth = WaterUtils.SampleDepth(ref water, sample);
            surfaceSamples++;
            return math.isfinite(height) && math.isfinite(depth) && depth >= 0f
                && math.all(math.isfinite(normal)) && normal.y > 0f;
        }

        // SampleDepth clamps its result to zero. Check the underlying cell
        // before accepting zero as a valid dry reading.
        private static bool FiniteWaterCell(ref WaterSurfaceData<SurfaceWater> water, float2 point)
        {
            int2 cell = (int2)math.floor(point);
            int width = water.resolution.x;
            int row = cell.y * width + cell.x;
            var depths = new float4(water.depths[row].m_Depth, water.depths[row + 1].m_Depth,
                water.depths[row + width].m_Depth, water.depths[row + width + 1].m_Depth);
            return math.all(math.isfinite(depths)) && math.all(depths >= 0f);
        }

        private static float InteriorClearance(float2 point, float2 a, float2 b, float2 c)
        {
            float area = Cross(b - a, c - a);
            if (!math.isfinite(area) || math.abs(area) < 1f) return -1f;
            float sign = math.sign(area);
            float margin = math.min(sign * Cross(b - a, point - a) / math.distance(a, b),
                math.min(sign * Cross(c - b, point - b) / math.distance(b, c),
                    sign * Cross(a - c, point - c) / math.distance(c, a)));
            return math.isfinite(margin) ? margin : -1f;
        }

        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
        private static JArray Vector(float3 value) => new JArray(value.x, value.y, value.z);
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal JObject Snapshot() => new JObject
        {
            ["tile"] = new JObject { ["index"] = tile.Index, ["version"] = tile.Version },
            ["triangle"] = observed.Triangle,
            ["triangleClearance"] = observed.Clearance,
            ["groundPoint"] = Vector(observed.Point),
            ["terrainNormal"] = Vector(observed.Normal),
            ["heightSample"] = Vector(observed.HeightSample),
            ["placementFlags"] = (uint)observed.Flags,
            ["prefabOffsetY"] = observed.PrefabOffset,
            ["frontSample"] = observed.FrontSample,
            ["waterDepth"] = observed.Depth,
            ["maxSampledDepth"] = observed.MaxDepth,
            ["maxSampledGrade"] = observed.MaxGrade,
            ["ownedTiles"] = ownedTiles, ["candidates"] = candidates, ["surfaceSamples"] = surfaceSamples,
            ["freshAttempts"] = freshAttempts, ["freshConfirmed"] = freshConfirmed
        };

        private readonly struct Placement
        {
            internal readonly PlacementFlags Flags;
            internal readonly float Offset;
            internal readonly int LotLength;
            internal readonly bool FrontSample;

            internal Placement(PlacementFlags flags, float offset, int lotLength, bool frontSample)
            {
                Flags = flags; Offset = offset; LotLength = lotLength; FrontSample = frontSample;
            }
        }

        private readonly struct Observation
        {
            internal readonly float3 Point, Normal, HeightSample;
            internal readonly float AppliedOffset, PrefabOffset, Depth, MaxDepth, MaxGrade, Clearance;
            internal readonly PlacementFlags Flags;
            internal readonly bool FrontSample;
            internal readonly int Triangle;
            internal Observation(float3 point, float3 normal, float3 heightSample, float appliedOffset, Placement placement,
                float depth, float maxDepth, float maxGrade, int triangle, float clearance)
            {
                Point = point; Normal = normal; HeightSample = heightSample; AppliedOffset = appliedOffset;
                Flags = placement.Flags; PrefabOffset = placement.Offset; FrontSample = placement.FrontSample;
                Depth = depth; MaxDepth = maxDepth;
                MaxGrade = maxGrade; Triangle = triangle; Clearance = clearance;
            }
        }

        internal sealed class Rejection : InvalidOperationException
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }
    }
}
#endif
