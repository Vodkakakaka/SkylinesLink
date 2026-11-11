#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Disabled = Unity.Entities.Disabled;

namespace SkylinesLink.Mod.Native
{
    // Owns three scoped definition entities. The positive original identities
    // are component values and are never owned by this batch.
    internal sealed class RelocationDefinitionEntities
    {
        private readonly RelocationDefinitionPayload payload;
        private readonly int actor;
        private readonly NativeEntityBatch batch;
        private readonly EntityArchetype[] archetypes = new EntityArchetype[3];
        private readonly Action requireScope;
        private bool createStarted, valuesReady, releaseStarted;

        internal RelocationDefinitionEntities(RelocationDefinitionPayload payload, int actor, Action requireScope)
        {
            Require(actor > 0, "RELOCATION_DEFINITION_ACTOR_UNAVAILABLE");
            this.payload = payload;
            this.actor = actor;
            this.requireScope = requireScope;
            batch = new NativeEntityBatch(payload.Source, 3);
        }

        internal Entity Root => batch.EntityAt(0);
        internal Entity OldNode => batch.EntityAt(1);
        internal Entity NewNode => batch.EntityAt(2);
        internal bool CreateReturned { get; private set; }
        internal bool ResourcesReleased => batch.ResourcesReleased;
        internal bool AllAbsent => batch.AbsenceConfirmed(0) && batch.AbsenceConfirmed(1) && batch.AbsenceConfirmed(2);

        internal void Create()
        {
            Require(MainThreadDispatcher.isMainThread && !createStarted && !releaseStarted, "RELOCATION_DEFINITION_CREATE_REJECTED");
            createStarted = true;
            requireScope();
            var entities = payload.Source.EntityManager;
            archetypes[0] = entities.CreateArchetype(typeof(CreationDefinition), typeof(ObjectDefinition), typeof(Updated),
                typeof(Disabled), typeof(ObjectGenerationScope));
            requireScope();
            archetypes[1] = entities.CreateArchetype(typeof(CreationDefinition), typeof(NetCourse), typeof(Updated),
                typeof(Disabled), typeof(ObjectGenerationScope));
            requireScope();
            archetypes[2] = payload.NewNodeUpgraded.HasValue
                ? entities.CreateArchetype(typeof(CreationDefinition), typeof(NetCourse), typeof(OwnerDefinition), typeof(Upgraded),
                    typeof(Updated), typeof(Disabled), typeof(ObjectGenerationScope))
                : entities.CreateArchetype(typeof(CreationDefinition), typeof(NetCourse), typeof(OwnerDefinition),
                    typeof(Updated), typeof(Disabled), typeof(ObjectGenerationScope));
            requireScope();

            for (int index = 0; index < archetypes.Length; index++)
            {
                batch.Create(index, archetypes[index]);
                requireScope();
            }
            Require(Root != OldNode && Root != NewNode && OldNode != NewNode, "RELOCATION_DEFINITION_IDENTITIES_COLLIDED");
            entities.SetComponentData(Root, payload.RootCreation);
            entities.SetComponentData(Root, payload.RootObject);
            entities.SetComponentData(OldNode, payload.OldNodeCreation);
            entities.SetComponentData(OldNode, payload.OldNodeCourse);
            entities.SetComponentData(NewNode, payload.NewNodeCreation);
            entities.SetComponentData(NewNode, payload.NewNodeCourse);
            entities.SetComponentData(NewNode, payload.NewNodeOwner);
            if (payload.NewNodeUpgraded is Upgraded upgrade) entities.SetComponentData(NewNode, upgrade);

            var scope = new ObjectGenerationScope { Value = actor };
            for (int index = 0; index < archetypes.Length; index++)
            {
                entities.SetSharedComponent(batch.EntityAt(index), scope);
                requireScope();
            }
            valuesReady = true;
            RequireCurrent(payload, actor);
            CreateReturned = true;
        }

        internal void RequireCurrent(RelocationDefinitionPayload expected, int expectedActor)
        {
            requireScope();
            Require(MainThreadDispatcher.isMainThread && valuesReady && !releaseStarted && ReferenceEquals(payload, expected)
                && actor == expectedActor && payload.Source.IsCreated, "RELOCATION_DEFINITION_INPUT_UNAVAILABLE");
            var entities = payload.Source.EntityManager;
            for (int index = 0; index < archetypes.Length; index++)
            {
                var member = batch.EntityAt(index);
                Require(entities.Exists(member) && entities.GetChunk(member).Archetype.Equals(archetypes[index])
                    && entities.GetSharedComponent<ObjectGenerationScope>(member).Value == actor,
                    "RELOCATION_DEFINITION_INPUT_SHAPE_CHANGED");
            }
            Require(entities.GetComponentData<CreationDefinition>(Root).Equals(payload.RootCreation)
                && entities.GetComponentData<ObjectDefinition>(Root).Equals(payload.RootObject)
                && entities.GetComponentData<CreationDefinition>(OldNode).Equals(payload.OldNodeCreation)
                && entities.GetComponentData<NetCourse>(OldNode).Equals(payload.OldNodeCourse)
                && entities.GetComponentData<CreationDefinition>(NewNode).Equals(payload.NewNodeCreation)
                && entities.GetComponentData<NetCourse>(NewNode).Equals(payload.NewNodeCourse)
                && entities.GetComponentData<OwnerDefinition>(NewNode).Equals(payload.NewNodeOwner)
                && (!payload.NewNodeUpgraded.HasValue || entities.GetComponentData<Upgraded>(NewNode).Equals(payload.NewNodeUpgraded.Value)),
                "RELOCATION_DEFINITION_INPUT_VALUES_CHANGED");
            requireScope();
        }

        internal void Release()
        {
            if (releaseStarted) return;
            releaseStarted = true;
            try { batch.Release(RequireSafeRelease); }
            finally { batch.ReleaseSlots(); }
        }

        private void RequireSafeRelease()
        {
            requireScope();
            var entities = payload.Source.EntityManager;
            for (int index = 0; index < archetypes.Length; index++)
            {
                var member = batch.EntityAt(index);
                if (member == Entity.Null || !entities.Exists(member)) continue;
                Require(entities.GetChunk(member).Archetype.Equals(archetypes[index]) && !entities.HasBuffer<LinkedEntityGroup>(member),
                    "RELOCATION_DEFINITION_RELEASE_SHAPE_CHANGED");
            }
        }

        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
