#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Once playback enters, an exception can leave a cache reference or an
    // unfinished structural change. The actor never attempts to destroy or
    // republish the candidate from that boundary.
    internal sealed class CompositionPublication
    {
        private readonly CompositionSelectionPreflight inputs;
        private readonly CompositionCreationDefinition definition;
        private readonly Action requireScope;
        private Entity entity;
        private int rowIndex = -1;
        private bool attempted, bufferAllocationAttempted, bufferAllocated, bufferDisposed, cacheVerified, isolationRemoved;
        private string? failureCode;

        internal CompositionPublication(CompositionSelectionPreflight inputs, CompositionCreationDefinition definition, Action requireScope)
        {
            this.inputs = inputs;
            this.definition = definition;
            this.requireScope = requireScope;
        }

        internal bool EnteredPlayback { get; private set; }
        internal bool PlaybackReturned { get; private set; }
        internal bool CommandStorageReleased => !bufferAllocationAttempted || bufferAllocated && bufferDisposed;
        internal bool LocalOwnershipReleased { get; private set; }
        internal Entity PublishedEntity => LocalOwnershipReleased ? entity : Entity.Null;

        internal void PublishOnce(PreparedCompositionDefinition candidate)
        {
            Require(!attempted, "COMPOSITION_PUBLICATION_ALREADY_ATTEMPTED");
            attempted = true;
            try
            {
                requireScope();
                Require(ReferenceEquals(inputs.Source, definition.Source) && inputs.Prefab == definition.Prefab.m_Prefab
                    && !definition.Terrain.HasValue, "COMPOSITION_PUBLICATION_SOURCE_MISMATCH");
                inputs.RequireUnchanged();
                EntityManager manager = definition.Source.EntityManager;
                entity = candidate.RequirePublicationCandidate(inputs, definition);
                rowIndex = inputs.CacheCount;
                var row = new NetGeometryComposition { m_Composition = entity, m_Mask = definition.Data.m_Flags };
                Require(rowIndex < 256, "COMPOSITION_PUBLICATION_CACHE_BUDGET");
                for (int index = 0; index < rowIndex; index++)
                    Require(inputs.CacheAt(index).m_Mask != row.m_Mask, "COMPOSITION_PUBLICATION_ALREADY_CACHED");

                bufferAllocationAttempted = true;
                var commands = new EntityCommandBuffer(Allocator.TempJob, PlaybackPolicy.SinglePlayback);
                bufferAllocated = true;
                try
                {
                    var isolation = new ComponentTypeSet(new[] {
                        ComponentType.ReadWrite<Disabled>(), ComponentType.ReadWrite<ObjectGenerationScope>() });
                    commands.AppendToBuffer(inputs.Prefab, row);
                    commands.RemoveComponent(entity, in isolation);
                    requireScope();
                    candidate.MarkPublicationEntered();
                    EnteredPlayback = true;
                    commands.Playback(manager);
                    PlaybackReturned = true;
                }
                finally
                {
                    try { commands.Dispose(); bufferDisposed = true; }
                    finally { if (EnteredPlayback) candidate.ReleasePublicationStorage(); }
                }

                requireScope();
                VerifyCache(manager, row);
                requireScope();
                Require(manager.Exists(entity) && !manager.HasComponent<Disabled>(entity)
                    && !manager.HasComponent<ObjectGenerationScope>(entity)
                    && manager.GetChunk(entity).Archetype.Equals(definition.SourceArchetype)
                    && manager.GetComponentData<PrefabRef>(entity).Equals(definition.Prefab)
                    && manager.IsComponentEnabled<Simulate>(entity), "COMPOSITION_PUBLICATION_ISOLATION_UNCONFIRMED");
                isolationRemoved = true;
                requireScope();
                Require(candidate.PublicationStorageReleased, "COMPOSITION_PUBLICATION_STORAGE_UNCONFIRMED");
                LocalOwnershipReleased = true;
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejection ? rejection.Code : "COMPOSITION_PUBLICATION_FAILED";
                throw;
            }
        }

        private void VerifyCache(EntityManager manager, NetGeometryComposition row)
        {
            Require(manager.Exists(inputs.Prefab) && manager.HasBuffer<NetGeometryComposition>(inputs.Prefab),
                "COMPOSITION_PUBLICATION_CACHE_UNAVAILABLE");
            var cache = manager.GetBuffer<NetGeometryComposition>(inputs.Prefab, true);
            Require(cache.Length == rowIndex + 1, "COMPOSITION_PUBLICATION_CACHE_COUNT_CHANGED");
            for (int index = 0; index < rowIndex; index++)
                Require(cache[index].Equals(inputs.CacheAt(index)), "COMPOSITION_PUBLICATION_CACHE_PREFIX_CHANGED");
            Require(cache[rowIndex].Equals(row), "COMPOSITION_PUBLICATION_CACHE_ROW_UNCONFIRMED");
            cacheVerified = true;
        }

        internal JObject Snapshot() => new JObject {
            ["attempted"] = attempted, ["enteredPlayback"] = EnteredPlayback, ["playbackReturned"] = PlaybackReturned,
            ["bufferDisposed"] = bufferDisposed, ["commandStorageReleased"] = CommandStorageReleased,
            ["cacheVerified"] = cacheVerified, ["isolationRemoved"] = isolationRemoved,
            ["localOwnershipReleased"] = LocalOwnershipReleased,
            ["entity"] = new JObject { ["index"] = entity.Index, ["version"] = entity.Version },
            ["rowIndex"] = rowIndex, ["failureCode"] = failureCode
        };

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : Exception
        {
            internal string Code { get; }
            internal Rejection(string code) : base(code) { Code = code; }
        }
    }
}
#endif
