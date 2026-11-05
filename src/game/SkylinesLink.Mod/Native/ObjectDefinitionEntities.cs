#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Owns exactly two scoped definitions, never the prefabs they reference.</summary>
    internal sealed class ObjectDefinitionEntities
    {
        private readonly World source;
        private readonly ObjectDefinitionPayload payload;
        private readonly int actor;
        private readonly WriteReceipt root = new WriteReceipt();
        private readonly WriteReceipt course = new WriteReceipt();
        private readonly NativeEntityBatch batch;
        private Action? requireScope;
        private bool createCalled, createReturned, releaseCalled, referencesChecked, valuesVerified;
        private string stage = "not-started";
        private string? failureCode, exceptionType, releaseFailure;

        internal ObjectDefinitionEntities(World source, ObjectDefinitionPayload payload, int actor)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
            if (actor <= 0 || !ReferenceEquals(source, payload.Source))
                throw new InvalidOperationException("OBJECT_DEFINITION_ENTITIES_SOURCE_REJECTED");
            this.actor = actor; batch = new NativeEntityBatch(source, 2);
        }

        internal Entity Root => batch.EntityAt(0);
        internal Entity Course => batch.EntityAt(1);
        internal bool CreateReturned => createReturned;
        internal bool UnknownCreation => batch.UnknownCreation;
        internal bool ResourcesReleased => batch.ResourcesReleased;
        // The caller runs and joins any native consumer synchronously before
        // Release. Neither an output-slot alias nor these entities may be lent
        // to an unjoined job across this lifetime.
        internal void Create(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(!createCalled && !releaseCalled, "OBJECT_DEFINITION_ENTITIES_ALREADY_USED");
            createCalled = true;
            requireScope = requireCurrentScope;
            try
            {
                stage = "check-source-and-references";
                requireCurrentScope();
                RequireReferences();
                referencesChecked = true;

                EntityManager entities = source.EntityManager;
                stage = "create-root-archetype";
                // The public API also adds the native Entity/Simulate types.
                // Disabled and Scope exist from the first instant; Scope stays
                // at its default until all original component values are set.
                EntityArchetype rootArchetype = entities.CreateArchetype(
                    ComponentType.ReadWrite<CreationDefinition>(), ComponentType.ReadWrite<ObjectDefinition>(),
                    ComponentType.ReadWrite<Updated>(), ComponentType.ReadWrite<Disabled>(),
                    ComponentType.ReadWrite<ObjectGenerationScope>());
                requireCurrentScope();
                stage = "create-course-archetype";
                EntityArchetype courseArchetype = entities.CreateArchetype(
                    ComponentType.ReadWrite<CreationDefinition>(), ComponentType.ReadWrite<OwnerDefinition>(),
                    ComponentType.ReadWrite<NetCourse>(), ComponentType.ReadWrite<Updated>(),
                    ComponentType.ReadWrite<Disabled>(), ComponentType.ReadWrite<ObjectGenerationScope>());
                requireCurrentScope();

                stage = "create-root";
                batch.Create(0, rootArchetype);
                requireCurrentScope();
                stage = "write-root-values";
                root.WriteAttempted = true;
                entities.SetComponentData(Root, payload.RootCreation); root.CreationWritten = true;
                entities.SetComponentData(Root, payload.RootObject); root.DetailWritten = true;
                root.WriteReturned = true;

                stage = "create-course";
                batch.Create(1, courseArchetype);
                requireCurrentScope();
                Require(Root != Course, "OBJECT_DEFINITION_ENTITIES_IDENTITIES_COLLIDED");
                stage = "write-course-values";
                course.WriteAttempted = true;
                entities.SetComponentData(Course, payload.CourseCreation); course.CreationWritten = true;
                entities.SetComponentData(Course, payload.CourseOwner); course.OwnerWritten = true;
                entities.SetComponentData(Course, payload.Course); course.DetailWritten = true;
                course.WriteReturned = true;

                var scope = new ObjectGenerationScope { Value = actor };
                stage = "assign-root-scope";
                root.ScopeAttempted = true;
                entities.SetSharedComponent(Root, scope); root.ScopeReturned = true;
                requireCurrentScope();
                stage = "assign-course-scope";
                course.ScopeAttempted = true;
                entities.SetSharedComponent(Course, scope); course.ScopeReturned = true;
                requireCurrentScope();

                stage = "verify-native-values";
                RequireReferences();
                Require(entities.GetChunk(Root).Archetype.Equals(rootArchetype)
                    && entities.GetChunk(Course).Archetype.Equals(courseArchetype)
                    && entities.GetSharedComponent<ObjectGenerationScope>(Root).Value == actor
                    && entities.GetSharedComponent<ObjectGenerationScope>(Course).Value == actor
                    && entities.GetComponentData<CreationDefinition>(Root).Equals(payload.RootCreation)
                    && entities.GetComponentData<ObjectDefinition>(Root).Equals(payload.RootObject)
                    && entities.GetComponentData<CreationDefinition>(Course).Equals(payload.CourseCreation)
                    && entities.GetComponentData<OwnerDefinition>(Course).Equals(payload.CourseOwner)
                    && entities.GetComponentData<NetCourse>(Course).Equals(payload.Course),
                    "OBJECT_DEFINITION_ENTITIES_VALUES_CHANGED");
                valuesVerified = true;
                requireCurrentScope();
                createReturned = true;
                stage = "created";
            }
            catch (Exception error)
            {
                failureCode = "OBJECT_DEFINITION_ENTITIES_CREATE_FAILED";
                exceptionType = ShortType(error);
                throw;
            }
        }

        private void RequireReferences()
        {
            Require(source.IsCreated && ReferenceEquals(source, payload.Source), "OBJECT_DEFINITION_ENTITIES_SOURCE_CHANGED");
            EntityManager entities = source.EntityManager;
            Require(Detached(payload.RootCreation) && Detached(payload.CourseCreation)
                && payload.RootCreation.m_SubPrefab == Entity.Null
                && payload.CourseOwner.m_Prefab == payload.RootCreation.m_Prefab
                && payload.CourseOwner.m_Position.Equals(payload.RootObject.m_Position)
                && payload.CourseOwner.m_Rotation.Equals(payload.RootObject.m_Rotation)
                && payload.Course.m_StartPosition.m_Entity == Entity.Null
                && payload.Course.m_EndPosition.m_Entity == Entity.Null,
                "OBJECT_DEFINITION_ENTITIES_PAYLOAD_REJECTED");
            RequirePrefab(entities, payload.RootCreation.m_Prefab);
            Require(entities.HasComponent<ObjectData>(payload.RootCreation.m_Prefab)
                && entities.GetComponentData<ObjectData>(payload.RootCreation.m_Prefab).m_Archetype.Valid,
                "OBJECT_DEFINITION_ENTITIES_ROOT_PREFAB_UNAVAILABLE");
            RequirePrefab(entities, payload.CourseCreation.m_Prefab);
            Require(entities.HasComponent<NetData>(payload.CourseCreation.m_Prefab),
                "OBJECT_DEFINITION_ENTITIES_COURSE_PREFAB_UNAVAILABLE");
            if (payload.CourseCreation.m_SubPrefab != Entity.Null)
                RequirePrefab(entities, payload.CourseCreation.m_SubPrefab);
        }

        private static void RequirePrefab(EntityManager entities, Entity prefab)
        {
            Require(prefab != Entity.Null && prefab.Index >= 0 && entities.Exists(prefab)
                && entities.HasComponent<PrefabData>(prefab) && entities.IsComponentEnabled<PrefabData>(prefab)
                && !entities.HasComponent<Deleted>(prefab), "OBJECT_DEFINITION_ENTITIES_PREFAB_UNAVAILABLE");
        }

        private static bool Detached(CreationDefinition value) => value.m_Flags == (CreationFlags)0
            && value.m_Original == Entity.Null && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null
            && value.m_Prefab != Entity.Null && value.m_Prefab.Index >= 0
            && (value.m_SubPrefab == Entity.Null || value.m_SubPrefab.Index >= 0);

        internal void Release()
        {
            RequireMainThread();
            if (releaseCalled) return;
            releaseCalled = true;
            try { batch.Release(requireScope); }
            finally { batch.ReleaseSlots(); }
            if (!batch.ResourcesReleased) releaseFailure = "DEFINITION_ENTITY_RELEASE_UNCONFIRMED";
        }
        // Everything below is a projection of recorded values. Snapshot never
        // reads EntityManager, World state, native slots or a live SDK getter.
        internal JObject Snapshot() => new JObject {
            ["createCalled"] = createCalled, ["createReturned"] = createReturned,
            ["releaseCalled"] = releaseCalled, ["resourcesReleased"] = ResourcesReleased,
            ["unknownCreation"] = UnknownCreation, ["actor"] = actor,
            ["referencesChecked"] = referencesChecked, ["valuesVerified"] = valuesVerified,
            ["stage"] = stage, ["failureCode"] = Value(failureCode),
            ["exceptionType"] = Value(exceptionType), ["releaseFailure"] = Value(releaseFailure),
            ["outputSlots"] = batch.SlotsSnapshot(),
            ["root"] = Snapshot(root, 0), ["course"] = Snapshot(course, 1),
            ["nativePlaybackCalled"] = false, ["nativeEntityFixupCalled"] = false,
            ["scope"] = "Two Disabled actor-scoped native definitions only; no Temp, placement, fees or construction acceptance."
        };

        private JObject Snapshot(WriteReceipt receipt, int slot)
        {
            var value = batch.ReceiptSnapshot(slot);
            value["writeAttempted"] = receipt.WriteAttempted;
            value["creationWritten"] = receipt.CreationWritten;
            value["ownerWritten"] = receipt.OwnerWritten;
            value["detailWritten"] = receipt.DetailWritten;
            value["writeReturned"] = receipt.WriteReturned;
            value["scopeAttempted"] = receipt.ScopeAttempted;
            value["scopeReturned"] = receipt.ScopeReturned;
            return value;
        }

        private sealed class WriteReceipt
        {
            internal bool WriteAttempted, CreationWritten, OwnerWritten, DetailWritten, WriteReturned;
            internal bool ScopeAttempted, ScopeReturned;
        }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static string ShortType(Exception error) => error.GetType().Name;
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "OBJECT_DEFINITION_ENTITIES_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
