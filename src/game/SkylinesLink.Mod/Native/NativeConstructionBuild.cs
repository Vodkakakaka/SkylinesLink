#if SKYLINESLINK_INTEGRATION_PROBE
using System.Reflection;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Identity of the reviewed construction experiment's SDK set. Each caller
    // still proves its own fields, methods, queries and patch observations.
    // This is neither a general game compatibility claim nor a file hash check.
    internal static class NativeConstructionBuild
    {
        private static readonly Module game = typeof(global::Game.GameSystemBase).Module;
        private static readonly Module entities = typeof(World).Module;
        private static readonly Module collections = typeof(Unity.Collections.NativeList<int>).Module;
        private static readonly Module colossalCollections = typeof(Colossal.Collections.CollectionUtils).Module;
        private static readonly Module mathematics = typeof(Colossal.Mathematics.MathUtils).Module;
        private static readonly bool reviewed = Matches(
            "e15551e93dea48558dde1da3cea7d7ef", "3d6b23aeb28144a6a5f4cbd5fd3d75e6",
            "f4ddbc30cb47406191074a184ca0481e", "0e5688e687474130b7127b3c25780e8b", "26c1af9d8d9740058d0326fb5ad00f64")
            || Matches(
            "46198354d4f94d23bf327e3c085d8b92", "bfb47e8c9b6e4acf8c6dc80a7bff1c41",
            "4951ab8144ac480a9dbcbef7fda2a8c4", "1817618d5b2d40eb99041021cd3b6e28", "47506b756d9c4ea7a027823f03022cb2");

        internal static bool Game(Module module) => reviewed && ReferenceEquals(module, game);
        internal static bool Entities(Module module) => reviewed && ReferenceEquals(module, entities);
        internal static bool Collections(Module module) => reviewed && ReferenceEquals(module, collections);
        internal static bool ColossalCollections(Module module) => reviewed && ReferenceEquals(module, colossalCollections);
        internal static bool Mathematics(Module module) => reviewed && ReferenceEquals(module, mathematics);

        private static bool Matches(string gameId, string entitiesId, string collectionsId,
            string colossalCollectionsId, string mathematicsId) =>
            game.ModuleVersionId.ToString("N") == gameId && entities.ModuleVersionId.ToString("N") == entitiesId
            && collections.ModuleVersionId.ToString("N") == collectionsId
            && colossalCollections.ModuleVersionId.ToString("N") == colossalCollectionsId
            && mathematics.ModuleVersionId.ToString("N") == mathematicsId;
    }
}
#endif
