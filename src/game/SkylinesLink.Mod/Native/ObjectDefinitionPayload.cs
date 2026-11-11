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

        internal bool IsSinglePointCourse
        {
            get
            {
                // The native node producer chooses its single-node branch by
                // endpoint equality. The sampled length can retain round-off
                // when a constant Bézier is translated off the origin.
                var position = Course.m_StartPosition.m_Position;
                var curve = Course.m_Curve;
                return math.all(math.isfinite(position))
                    && Course.m_StartPosition.m_Entity == Entity.Null && Course.m_EndPosition.m_Entity == Entity.Null
                    && Course.m_EndPosition.m_Position.Equals(position)
                    && curve.a.Equals(position) && curve.b.Equals(position)
                    && curve.c.Equals(position) && curve.d.Equals(position)
                    && math.isfinite(Course.m_Length) && Course.m_Length >= 0f;
            }
        }

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
