#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game.Common;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // Reads only the fixed SDK predicates in this actor's scope. It neither
    // creates native consumers nor evaluates the default world's consumers.
    internal sealed class PostGenerationApplicabilityProbe
    {
        private readonly World source;
        private readonly Entity root, node;
        private readonly int actor;
        private readonly Action requireScope;
        private int queriesAttempted, queriesAcquired, queriesDisposed;
        private int arraysAttempted, arraysAcquired, arraysDisposed;
        private int? ownerInputs, attachmentInputs, compositionInputs, allCompositionInputs;
        private int compositionNodeChecks;
        private bool ran, completed, compositionNodeMatched;
        private string? failureCode;

        internal PostGenerationApplicabilityProbe(World source, Entity root, Entity node, int actor, Action requireScope)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.requireScope = requireScope ?? throw new ArgumentNullException(nameof(requireScope));
            Require(actor > 0 && root != Entity.Null && node != Entity.Null && root != node,
                "POST_GENERATION_APPLICABILITY_BIND_REJECTED");
            this.root = root; this.node = node; this.actor = actor;
        }

        internal bool ResourcesReleased => queriesAttempted == queriesAcquired && queriesAcquired == queriesDisposed
            && arraysAttempted == arraysAcquired && arraysAcquired == arraysDisposed;
        internal bool ChecksPassed => completed && ResourcesReleased && ownerInputs == 0 && attachmentInputs == 0
            && compositionInputs == 1 && allCompositionInputs == 1 && compositionNodeMatched
            && compositionNodeChecks == 2 && failureCode == null;

        internal void RunOnce()
        {
            Require(!ran, "POST_GENERATION_APPLICABILITY_ALREADY_ATTEMPTED"); ran = true;
            try
            {
                Guard();
                Require(typeof(Game.Net.SubNetReferencesSystem).Module.ModuleVersionId.ToString("N")
                    == "e15551e93dea48558dde1da3cea7d7ef", "POST_GENERATION_APPLICABILITY_SDK_CHANGED");
                ownerInputs = Read(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<OwnerDefinition>(), ComponentType.ReadOnly<Owner>(),
                        ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                    Any = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.Edge>(),
                        ComponentType.ReadOnly<Game.Areas.Area>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities }, false);
                attachmentInputs = Read(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Game.Objects.Attached>(),
                        ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                    Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities }, false);
                compositionInputs = Read(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<ObjectGenerationScope>(),
                        ComponentType.ReadOnly<Disabled>() },
                    Any = new[] { ComponentType.ReadOnly<Game.Net.Composition>(), ComponentType.ReadOnly<Game.Net.Orphan>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities }, true);
                allCompositionInputs = Read(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Disabled>() },
                    Any = new[] { ComponentType.ReadOnly<Game.Net.Composition>(), ComponentType.ReadOnly<Game.Net.Orphan>() },
                    Options = EntityQueryOptions.IncludeDisabledEntities }, true);
                Guard(); completed = true;
            }
            catch (Exception error) { failureCode = error is Rejection rejected ? rejected.Code : "POST_GENERATION_APPLICABILITY_FAILED"; throw; }
        }

        private int Read(EntityQueryDesc description, bool expectNode)
        {
            Guard(); queriesAttempted++;
            EntityQuery query = source.EntityManager.CreateEntityQuery(description); queriesAcquired++;
            try
            {
                query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor }); Guard();
                int count = query.CalculateEntityCount(); Guard();
                Require(count == (expectNode ? 1 : 0), "POST_GENERATION_APPLICABILITY_INPUTS_CHANGED");
                if (expectNode)
                {
                    arraysAttempted++; NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp); arraysAcquired++;
                    try { Guard(); compositionNodeMatched = entities.Length == 1 && entities[0] == node; compositionNodeChecks++;
                        Require(compositionNodeMatched, "POST_GENERATION_COMPOSITION_TARGET_CHANGED"); }
                    finally { entities.Dispose(); arraysDisposed++; }
                }
                Guard(); return count;
            }
            finally { query.Dispose(); queriesDisposed++; }
        }

        private void Guard()
        {
            Require(MainThreadDispatcher.isMainThread, "POST_GENERATION_APPLICABILITY_MAIN_THREAD_REQUIRED");
            requireScope(); Require(source.IsCreated, "POST_GENERATION_APPLICABILITY_SOURCE_UNAVAILABLE");
            EntityManager entities = source.EntityManager;
            GuardEntity(entities, root); GuardEntity(entities, node);
        }

        private void GuardEntity(EntityManager entities, Entity entity) =>
            Require(entities.Exists(entity) && entities.HasComponent<Disabled>(entity)
                && entities.HasComponent<ObjectGenerationScope>(entity)
                && entities.GetSharedComponent<ObjectGenerationScope>(entity).Value == actor,
                "POST_GENERATION_APPLICABILITY_SCOPE_CHANGED");

        internal JObject Snapshot() => new JObject {
            ["completed"] = completed, ["checksPassed"] = ChecksPassed, ["resourcesReleased"] = ResourcesReleased,
            ["findOwnersRequiredInputs"] = Number(ownerInputs), ["attachRequiredInputs"] = Number(attachmentInputs),
            ["updatedCompositionCandidates"] = Number(compositionInputs), ["compositionNodeMatched"] = compositionNodeMatched,
            ["allCompositionCandidates"] = Number(allCompositionInputs), ["compositionNodeChecks"] = compositionNodeChecks,
            ["queries"] = new JObject { ["attempted"] = queriesAttempted, ["acquired"] = queriesAcquired, ["disposed"] = queriesDisposed },
            ["arrays"] = new JObject { ["attempted"] = arraysAttempted, ["acquired"] = arraysAcquired, ["disposed"] = arraysDisposed },
            ["nativeSystemsInvoked"] = false, ["defaultConsumersAssessed"] = false,
            ["loadedBranchObserved"] = false, ["auxiliaryQueriesAssessed"] = false,
            ["fullPipelineQualified"] = false, ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode),
            ["scope"] = "Read-only actor-scoped original SDK predicates. Both composition query variants contain the node; selection is unexecuted. The loaded flag, auxiliary queries and patched or default consumers are not assessed."
        };
        private static JToken Number(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception { internal readonly string Code; internal Rejection(string code) : base(code) { Code = code; } }
    }
}
#endif
