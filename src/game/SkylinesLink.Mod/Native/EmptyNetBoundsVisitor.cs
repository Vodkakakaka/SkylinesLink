#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // The two native readers test different axes. Road lookup intersects XZ
    // even for an edge outside the vertical range of the requested bounds.
    internal struct EmptyNetBoundsVisitor : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
    {
        internal const int NodeBudget = 512, ItemBudget = 2048;
        private readonly Bounds3 search;
        private readonly string codePrefix;
        private readonly int nodeLimit, itemLimit;
        private readonly bool rejectHits, horizontalOnly;
        internal int Nodes, Items;
        internal bool Hit;

        internal EmptyNetBoundsVisitor(Bounds3 search, string codePrefix, bool horizontalOnly = false,
            bool rejectHits = true, int nodeLimit = NodeBudget, int itemLimit = ItemBudget)
        {
            this.search = search;
            this.codePrefix = codePrefix;
            this.horizontalOnly = horizontalOnly;
            this.rejectHits = rejectHits;
            this.nodeLimit = nodeLimit;
            this.itemLimit = itemLimit;
            Nodes = 0;
            Items = 0;
            Hit = false;
        }

        public bool Intersect(QuadTreeBoundsXZ bounds)
        {
            if (++Nodes > nodeLimit) throw new Rejection(codePrefix + "_NODE_BUDGET");
            return Overlaps(bounds);
        }

        public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
        {
            if (++Items > itemLimit) throw new Rejection(codePrefix + "_ITEM_BUDGET");
            if (rejectHits && Overlaps(bounds))
            {
                Hit = true;
                throw new Rejection(codePrefix + "_SOURCE_SPATIAL_HIT");
            }
        }

        private bool Overlaps(QuadTreeBoundsXZ bounds) => horizontalOnly
            ? MathUtils.Intersect(bounds.m_Bounds.xz, search.xz)
            : MathUtils.Intersect(bounds.m_Bounds, search);

        internal sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }
    }
}
#endif
