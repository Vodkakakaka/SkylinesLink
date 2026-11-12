#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Collections;
using Colossal.Core;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    internal sealed class PreviewRoadSearchBounds
    {
        private const float Radius = 8.4f;
        private bool attempted, writersCompleted, readCompleted;
        private EmptyNetBoundsVisitor visitor;
        private float3 front;
        private string? failureCode;

        internal void RequireEmpty(World source, Entity root, Action requireScope)
        {
            if (attempted || !MainThreadDispatcher.isMainThread)
                throw new InvalidOperationException("PREVIEW_ROAD_BOUNDS_ATTEMPT_UNAVAILABLE");
            attempted = true;
            try
            {
                requireScope();
                if (!NativeConstructionBuild.ColossalCollections(typeof(NativeQuadTree<Entity, QuadTreeBoundsXZ>).Module)
                    || !NativeConstructionBuild.Mathematics(typeof(MathUtils).Module))
                    throw new InvalidOperationException("PREVIEW_ROAD_BOUNDS_ABI_CHANGED");

                var search = source.GetExistingSystemManaged<SearchSystem>();
                if (search == null || !ReferenceEquals(search.World, source))
                    throw new InvalidOperationException("PREVIEW_ROAD_NET_SEARCH_UNAVAILABLE");
                var tree = search.GetNetSearchTree(readOnly: true, out var writers);
                writers.Complete();
                writersCompleted = true;
                requireScope();
                front = PreviewRoadProducer.ReadFront(source.EntityManager, root);
                if (!tree.IsCreated || !math.all(math.isfinite(front)))
                    throw new InvalidOperationException("PREVIEW_ROAD_SEARCH_BOUNDS_UNAVAILABLE");
                // The stock iterator uses the same front position, radius and
                // horizontal overlap. Its bound can only shrink after a hit.
                visitor = new EmptyNetBoundsVisitor(new Bounds3(front - Radius, front + Radius), "PREVIEW_ROAD",
                    horizontalOnly: true);
                tree.Iterate(ref visitor);
                readCompleted = true;
                requireScope();
            }
            catch (EmptyNetBoundsVisitor.Rejection error)
            {
                failureCode = error.Code;
                throw;
            }
            catch (Exception)
            {
                failureCode = "PREVIEW_ROAD_BOUNDS_FAILED";
                throw;
            }
        }

        internal JObject RelocationSnapshot() => new JObject {
            ["completed"] = readCompleted && writersCompleted, ["nodes"] = visitor.Nodes,
            ["items"] = visitor.Items, ["hit"] = visitor.Hit, ["failureCode"] = failureCode
        };

        internal JObject Snapshot() => new JObject {
            ["attempted"] = attempted, ["writersCompleted"] = writersCompleted, ["readCompleted"] = readCompleted,
            ["front"] = new JArray(front.x, front.y, front.z), ["radius"] = Radius, ["horizontalOnly"] = true,
            ["nodesVisited"] = visitor.Nodes, ["itemsVisited"] = visitor.Items, ["spatialHit"] = visitor.Hit,
            ["failureCode"] = failureCode
        };
    }
}
#endif
