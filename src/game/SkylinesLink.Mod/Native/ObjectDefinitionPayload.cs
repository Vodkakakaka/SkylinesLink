#if SKYLINESLINK_INTEGRATION_PROBE
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A fixed, complete native value handoff for one new object and its one
    // course. No ECB alias, container, pointer, JSON reconstruction or entity
    // fixup is retained. References remain valid only in the caller's scope.
    internal sealed class ObjectDefinitionPayload
    {
        internal readonly World Source;
        internal readonly CreationDefinition RootCreation;
        internal readonly ObjectDefinition RootObject;
        internal readonly CreationDefinition CourseCreation;
        internal readonly OwnerDefinition CourseOwner;
        internal readonly NetCourse Course;

        internal ObjectDefinitionPayload(World source, CreationDefinition rootCreation, ObjectDefinition rootObject,
            CreationDefinition courseCreation, OwnerDefinition courseOwner, NetCourse course)
        {
            Source = source;
            RootCreation = rootCreation; RootObject = rootObject;
            CourseCreation = courseCreation; CourseOwner = courseOwner; Course = course;
        }
    }
}
#endif
