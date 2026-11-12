#if SKYLINESLINK_INTEGRATION_PROBE
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

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
        internal ObjectDefinitionValues Values => new ObjectDefinitionValues(RootCreation, RootObject, CourseCreation, CourseOwner, Course);

        // Native node and spatial-search branches use exact endpoint equality.
        // The captured control points and sampled length remain separate facts.
        internal bool IsDetachedSingleNodeCourse =>
            Course.m_StartPosition.m_Entity == Entity.Null && Course.m_EndPosition.m_Entity == Entity.Null
            && math.all(math.isfinite(Course.m_StartPosition.m_Position))
            && Course.m_StartPosition.m_Position.Equals(Course.m_EndPosition.m_Position)
            && ((Course.m_StartPosition.m_Flags | Course.m_EndPosition.m_Flags) & CoursePosFlags.DontCreate) == 0;

        internal ObjectDefinitionPayload(World source, CreationDefinition rootCreation, ObjectDefinition rootObject,
            CreationDefinition courseCreation, OwnerDefinition courseOwner, NetCourse course)
        {
            Source = source;
            RootCreation = rootCreation; RootObject = rootObject;
            CourseCreation = courseCreation; CourseOwner = courseOwner; Course = course;
        }
    }

    // A value-only preview intent may outlive cancellation of its native batch.
    internal readonly struct ObjectDefinitionValues
    {
        private readonly CreationDefinition rootCreation, courseCreation;
        private readonly ObjectDefinition rootObject;
        private readonly OwnerDefinition courseOwner;
        private readonly NetCourse course;

        internal ObjectDefinitionValues(CreationDefinition rootCreation, ObjectDefinition rootObject,
            CreationDefinition courseCreation, OwnerDefinition courseOwner, NetCourse course)
        {
            this.rootCreation = rootCreation; this.rootObject = rootObject;
            this.courseCreation = courseCreation; this.courseOwner = courseOwner; this.course = course;
        }

        internal bool Matches(ObjectDefinitionValues other) => rootCreation.Equals(other.rootCreation)
            && rootObject.Equals(other.rootObject) && courseCreation.Equals(other.courseCreation)
            && courseOwner.Equals(other.courseOwner) && course.Equals(other.course);
    }
}
#endif
