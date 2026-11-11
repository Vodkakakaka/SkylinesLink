#if SKYLINESLINK_INTEGRATION_PROBE
using Game.Net;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Ordinary copies of the three verified definitions. The producing ECB
    // retains ownership of all native buffers and deferred entity identities.
    internal sealed class RelocationDefinitionPayload
    {
        internal World Source { get; }
        internal RelocationDefinitionSource Original { get; }
        internal CreationDefinition RootCreation { get; }
        internal ObjectDefinition RootObject { get; }
        internal CreationDefinition OldNodeCreation { get; }
        internal NetCourse OldNodeCourse { get; }
        internal CreationDefinition NewNodeCreation { get; }
        internal OwnerDefinition NewNodeOwner { get; }
        internal NetCourse NewNodeCourse { get; }
        internal Upgraded? NewNodeUpgraded { get; }

        internal RelocationDefinitionPayload(World source, RelocationDefinitionSource original,
            CreationDefinition rootCreation, ObjectDefinition rootObject,
            CreationDefinition oldNodeCreation, NetCourse oldNodeCourse, CreationDefinition newNodeCreation,
            OwnerDefinition newNodeOwner, NetCourse newNodeCourse, Upgraded? newNodeUpgraded)
        {
            Source = source;
            Original = original;
            RootCreation = rootCreation;
            RootObject = rootObject;
            OldNodeCreation = oldNodeCreation;
            OldNodeCourse = oldNodeCourse;
            NewNodeCreation = newNodeCreation;
            NewNodeOwner = newNodeOwner;
            NewNodeCourse = newNodeCourse;
            NewNodeUpgraded = newNodeUpgraded;
        }
    }
}
#endif
