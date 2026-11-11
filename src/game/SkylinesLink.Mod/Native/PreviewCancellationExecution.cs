#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using LiveSubNet = Game.Net.SubNet;
using LiveSubObject = Game.Objects.SubObject;

namespace SkylinesLink.Mod.Native
{
    // The normal tool marks roots first. Physical retirement is a single
    // closed group after the native child and reference passes have joined.
    internal sealed class PreviewCancellationExecution
    {
        private readonly PreviewCancelTool tool = new PreviewCancelTool();
        private readonly PreviewCancelSubObjects subObjects = new PreviewCancelSubObjects();
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private Entity[] targets = Array.Empty<Entity>();
        private bool attempted, postToolClosureConfirmed, referencesDetached, fundsUnchanged;
        private bool destroyEntered, destroyReturned, groupAbsent, slotsReleased, protectionConfirmed;
        private string? failureCode;

        internal bool NativeWorkMayReadSource => tool.NativeWorkMayReadSource || subObjects.NativeWorkMayReadSource;
        internal bool StructuralStateUnknown => tool.EffectsStarted && !groupAbsent;
        internal bool GroupAbsent => groupAbsent;
        internal bool ResourcesReleased => tool.ResourcesReleased && subObjects.ResourcesReleased
            && archetypes.ResourcesReleased && (!tool.EffectsStarted || groupAbsent && slotsReleased);
        internal bool Passed => tool.Passed && subObjects.Passed && postToolClosureConfirmed
            && referencesDetached && destroyReturned && groupAbsent && slotsReleased && protectionConfirmed
            && fundsUnchanged && ResourcesReleased && failureCode == null;

        internal void RunOnce(World source, Entity[] graph, List<ComponentSystemBase> appended,
            Action requireScope, Action guardLiveGraph, Action unlinkChildren, Action observeProtection,
            Action confirmGroupRemoval)
        {
            Require(!attempted && MainThreadDispatcher.isMainThread && graph.Length == 7,
                "PREVIEW_CANCEL_EXECUTION_UNAVAILABLE");
            attempted = true;
            targets = (Entity[])graph.Clone();
            try
            {
                guardLiveGraph();
                RequireGraph(source, requireScope, parentsMarked: false);
                var costs = new PreviewCostObservation(source, targets);
                tool.RunOnce(source, targets, appended, requireScope, guardLiveGraph, observeProtection);
                Require(tool.Passed && !NativeWorkMayReadSource, "PREVIEW_CANCEL_TOOL_UNCONFIRMED");

                void GuardMarkedParents() => RequireGraph(source, requireScope, parentsMarked: true);
                GuardMarkedParents();
                postToolClosureConfirmed = true;
                subObjects.RunOnce(source, targets, appended, requireScope, GuardMarkedParents, observeProtection);
                Require(subObjects.Passed && !NativeWorkMayReadSource, "PREVIEW_CANCEL_CHILDREN_UNCONFIRMED");
                GuardMarkedParents();
                unlinkChildren();
                GuardMarkedParents();
                var entities = source.EntityManager;
                Require(entities.GetBuffer<LiveSubObject>(targets[0], true).Length == 0
                    && entities.GetBuffer<LiveSubObject>(targets[1], true).Length == 0,
                    "PREVIEW_CANCEL_SUBOBJECT_REFERENCES_RETAINED");
                referencesDetached = true;

                Require((bool)costs.Complete(source, targets)["fundsUnchanged"]!, "PREVIEW_CANCEL_FEE_CHANGED");
                DestroyGroup(source, requireScope);
                confirmGroupRemoval();
                slotsReleased = true;
                costs.RequireFundsUnchanged(source);
                fundsUnchanged = true;
                observeProtection();
                protectionConfirmed = true;
                Require(Passed, "PREVIEW_CANCEL_UNCONFIRMED");
            }
            catch (Exception error)
            {
                failureCode = error is Rejection rejected ? rejected.Code
                    : (string?)tool.Snapshot()["failureCode"] ?? (string?)subObjects.Snapshot()["failureCode"]
                        ?? "PREVIEW_CANCEL_EXECUTION_FAILED";
                throw new Rejection(failureCode, error);
            }
        }

        private void RequireGraph(World source, Action requireScope, bool parentsMarked)
        {
            requireScope();
            Require(source.IsCreated, "PREVIEW_CANCEL_SOURCE_UNAVAILABLE");
            var entities = source.EntityManager;
            Entity root = targets[0], node = targets[1];
            for (int index = 0; index < targets.Length; index++)
            {
                Entity target = targets[index];
                Require(target != Entity.Null && Array.IndexOf(targets, target) == index && entities.Exists(target)
                    && entities.HasComponent<Disabled>(target) && entities.HasComponent<ObjectGenerationScope>(target)
                    && entities.GetSharedComponent<ObjectGenerationScope>(target).Value == 1
                    && entities.HasComponent<Temp>(target)
                    && entities.GetComponentData<Temp>(target).m_Original == Entity.Null,
                    "PREVIEW_CANCEL_GRAPH_IDENTITY_CHANGED");
                if (index < 2)
                    Require(entities.HasComponent<Deleted>(target) == parentsMarked, "PREVIEW_CANCEL_PARENT_MARKER_CHANGED");
                if (index != 0)
                    Require(entities.HasComponent<Owner>(target)
                        && (entities.GetComponentData<Owner>(target).m_Owner == root
                            || index > 1 && entities.GetComponentData<Owner>(target).m_Owner == node),
                        "PREVIEW_CANCEL_OWNER_CHANGED");
                else Require(!entities.HasComponent<Owner>(root), "PREVIEW_CANCEL_OWNER_CHANGED");
                Require(Empty<Game.Areas.SubArea>(entities, target) && Empty<Game.Routes.SubRoute>(entities, target)
                    && Empty<Game.Vehicles.OwnedVehicle>(entities, target) && Empty<Game.Net.ConnectedEdge>(entities, target)
                    && (index == 0 || Empty<LiveSubNet>(entities, target))
                    && !entities.HasComponent<Game.Vehicles.Vehicle>(target) && Empty<LinkedEntityGroup>(entities, target),
                    "PREVIEW_CANCEL_EXTERNAL_CLOSURE_UNQUALIFIED");
            }
            Require(entities.HasBuffer<LiveSubNet>(root), "PREVIEW_CANCEL_SUBNET_UNAVAILABLE");
            var subNets = entities.GetBuffer<LiveSubNet>(root, true);
            Require(subNets.Length == 1 && subNets[0].m_SubNet == node, "PREVIEW_CANCEL_SUBNET_CHANGED");
            requireScope();
        }

        private void DestroyGroup(World source, Action requireScope)
        {
            Require(!destroyEntered && !NativeWorkMayReadSource, "PREVIEW_CANCEL_GROUP_DESTROY_UNAVAILABLE");
            RequireGraph(source, requireScope, parentsMarked: true);
            var entities = source.EntityManager;
            foreach (Entity target in targets)
            {
                Require(entities.HasComponent<Deleted>(target), "PREVIEW_CANCEL_GROUP_MARKERS_INCOMPLETE");
                archetypes.Read(entities.GetChunk(target).Archetype, 272);
            }
            using var group = new NativeArray<Entity>(targets, Allocator.Temp);
            requireScope();
            Require(PreviewCancelToolPatchProbe.Observe().Qualified, "PREVIEW_CANCEL_GROUP_DESTROY_PATCH_UNQUALIFIED");
            destroyEntered = true;
            entities.DestroyEntity(group);
            destroyReturned = true;
            requireScope();
            foreach (Entity target in targets)
                Require(!entities.Exists(target), "PREVIEW_CANCEL_GROUP_ENTITY_RETAINED");
            requireScope();
            groupAbsent = true;
        }

        private static bool Empty<T>(EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData =>
            !entities.HasBuffer<T>(entity) || entities.GetBuffer<T>(entity, true).Length == 0;

        internal JObject Snapshot()
        {
            var members = new JArray();
            foreach (Entity target in targets) members.Add(new JArray(target.Index, target.Version));
            return new JObject {
                ["passed"] = Passed, ["members"] = members, ["tool"] = tool.Snapshot(), ["subObjects"] = subObjects.Snapshot(),
                ["postToolClosureConfirmed"] = postToolClosureConfirmed, ["referencesDetached"] = referencesDetached,
                ["subNetInsideGroupUntilDestroy"] = destroyEntered, ["destroyEntered"] = destroyEntered,
                ["destroyReturned"] = destroyReturned, ["groupAbsent"] = groupAbsent, ["slotsReleased"] = slotsReleased,
                ["archetypes"] = archetypes.Snapshot(), ["fundsUnchanged"] = fundsUnchanged,
                ["resourcesReleased"] = ResourcesReleased, ["nativeWorkMayReadSource"] = NativeWorkMayReadSource,
                ["structuralStateUnknown"] = StructuralStateUnknown, ["failureCode"] = failureCode
            };
        }

        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        internal sealed class Rejection : InvalidOperationException
        {
            internal string Code { get; }
            internal Rejection(string code, Exception? inner = null) : base(code, inner) { Code = code; }
        }
    }
}
#endif
