#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Buildings;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Game.Vehicles;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using Disabled = Unity.Entities.Disabled;
using LiveObject = Game.Objects.Object;
using LiveSubNet = Game.Net.SubNet;
using LiveSubObject = Game.Objects.SubObject;
using StaticObject = Game.Objects.Static;

namespace SkylinesLink.Mod.Native
{
    public sealed partial class PreviewCommitObjectProducer
    {
        private const int RelocationCommandLimit = 33;
        private Temp[]? relocationTemps;
        private Entity relocationEconomy;
        private RelocationPlan? relocationPlan;
        private bool relocationInputsRechecked, pathEventObserved, relocationTriggerObserved;
        private int? relocationRecentCost;

        private RelocationPlan ReadRelocationPlan(EntityManager entities, Entity[] graph, uint frameIndex)
        {
            var plan = new RelocationPlan();
            var oldTargets = new HashSet<Entity>();
            using var economyQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            Require(economyQuery.CalculateEntityCount() == 1 && economyQuery.GetSingletonEntity() == relocationEconomy,
                "PREVIEW_COMMIT_RELOCATION_OBJECT_ECONOMY_CHANGED");
            var config = entities.GetComponentData<EconomyParameterData>(relocationEconomy);
            plan.Economy = config;
            int modified = 0, deleted = 0;
            for (int i = 0; i < graph.Length; i++)
            {
                if (i == 1 || i == 2) continue;
                Entity target = graph[i];
                Require(target != Entity.Null && Array.IndexOf(graph, target) == i && entities.Exists(target)
                    && entities.HasComponent<LiveObject>(target) && entities.HasComponent<Temp>(target)
                    && entities.HasComponent<PrefabRef>(target) && entities.HasComponent<Transform>(target)
                    && entities.HasComponent<Disabled>(target) && entities.HasComponent<ObjectGenerationScope>(target)
                    && entities.GetSharedComponent<ObjectGenerationScope>(target).Value == actor
                    && !entities.HasComponent<Deleted>(target) && !entities.HasComponent<Error>(target)
                    && !entities.HasComponent<Warning>(target) && !entities.HasComponent<Override>(target)
                    && !entities.HasComponent<Recent>(target) && !entities.HasComponent<Attached>(target)
                    && !entities.HasComponent<ParkedCar>(target) && !entities.HasComponent<ParkedTrain>(target)
                    && !entities.HasComponent<Vehicle>(target) && !entities.HasComponent<Creature>(target)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(target)
                    && entities.HasComponent<Building>(target) == (i == 0),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_ROLE_CHANGED");
                Temp temp = entities.GetComponentData<Temp>(target);
                Require(temp.Equals(relocationTemps![i]) && (i == 0 ? temp.m_Cost > 0 : temp.m_Cost == 0),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_TEMP_CHANGED");
                Entity prefab = entities.GetComponentData<PrefabRef>(target).m_Prefab;
                Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                    && entities.IsComponentEnabled<PrefabData>(prefab), "PREVIEW_COMMIT_RELOCATION_OBJECT_PREFAB_CHANGED");
                Require(entities.HasComponent<Owner>(target) == (i != 0), "PREVIEW_COMMIT_RELOCATION_OBJECT_OWNER_CHANGED");
                Entity owner = i == 0 ? Entity.Null : entities.GetComponentData<Owner>(target).m_Owner;
                var role = plan.AddRole(target, prefab, owner, temp, entities.GetComponentData<Transform>(target));
                if (temp.m_Original == Entity.Null)
                {
                    Require(i != 0 && owner == graph[2] && temp.m_Flags == TempFlags.Create
                        && entities.GetComponentData<Temp>(owner).Equals(relocationTemps![2])
                        && relocationTemps![2].m_Original == Entity.Null && plan.CreatedTarget == Entity.Null,
                        "PREVIEW_COMMIT_RELOCATION_OBJECT_CREATE_BRANCH");
                    plan.CreatedTarget = target;
                    plan.CreatedPrefab = prefab;
                    role.Add(ObjectCommand.Markers("RemoveMultipleComponents", target, animationTypes));
                    if (entities.HasComponent<StaticObject>(target) && !entities.HasComponent<Swaying>(target)
                        && !entities.HasBuffer<TransformFrame>(target) || entities.HasComponent<Stopped>(target))
                        role.Add(ObjectCommand.Component<InterpolatedTransform>("RemoveComponent", target));
                    role.Add(ObjectCommand.Markers("AddMultipleComponents", target, appliedTypes));
                    continue;
                }

                Entity original = temp.m_Original;
                Require(oldTargets.Add(original) && entities.Exists(original) && Array.IndexOf(graph, original) < 0
                    && entities.HasComponent<LiveObject>(original) && entities.HasComponent<Transform>(original)
                    && entities.HasComponent<PrefabRef>(original) && entities.GetComponentData<PrefabRef>(original).m_Prefab == prefab
                    && entities.HasComponent<Building>(original) == (i == 0)
                    && !entities.HasComponent<Temp>(original) && !entities.HasComponent<Deleted>(original)
                    && !entities.HasComponent<Disabled>(original) && !entities.HasComponent<ObjectGenerationScope>(original)
                    && !entities.HasComponent<Hidden>(original) && !entities.HasComponent<Attached>(original)
                    && !entities.HasComponent<Vehicle>(original) && !entities.HasComponent<Creature>(original)
                    && !entities.HasComponent<Game.Buildings.ServiceUpgrade>(original) && !entities.HasComponent<Game.Routes.TransportStop>(original)
                    && !entities.HasComponent<CarCurrentLane>(original) && !entities.HasComponent<TrainCurrentLane>(original)
                    && !entities.HasComponent<WatercraftCurrentLane>(original) && !entities.HasComponent<AircraftCurrentLane>(original)
                    && !entities.HasComponent<HumanCurrentLane>(original) && !entities.HasComponent<AnimalCurrentLane>(original),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_ORIGINAL_CHANGED");
                role.OriginalTransform = entities.GetComponentData<Transform>(original);
                role.OriginalRecent = entities.HasComponent<Recent>(original) ? entities.GetComponentData<Recent>(original) : (Recent?)null;
                Require(entities.HasComponent<Owner>(original) == (i != 0),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_ORIGINAL_OWNER_CHANGED");

                if (owner == graph[1])
                {
                    Require(temp.m_Flags == (TempFlags.Delete | TempFlags.Hidden) && ++deleted == 1
                        && entities.GetComponentData<Owner>(original).m_Owner == relocationTemps![1].m_Original
                        && (!entities.HasBuffer<LiveSubNet>(original) || entities.GetBuffer<LiveSubNet>(original, true).Length == 0),
                        "PREVIEW_COMMIT_RELOCATION_OBJECT_DELETE_BRANCH");
                    role.Add(ObjectCommand.Component<Deleted>("AddComponent", original));
                    role.Add(ObjectCommand.Component<Deleted>("AddComponent", target));
                    continue;
                }

                Require(i == 0 ? temp.m_Flags == (TempFlags.Modify | TempFlags.Essential)
                    : temp.m_Flags == TempFlags.Modify && owner == graph[0]
                        && entities.GetComponentData<Owner>(original).m_Owner == relocationTemps![0].m_Original,
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_MODIFY_BRANCH");
                modified++;
                Require(entities.HasBuffer<LiveSubNet>(target) == entities.HasBuffer<LiveSubNet>(original)
                    && entities.HasBuffer<Game.Areas.SubArea>(target) == entities.HasBuffer<Game.Areas.SubArea>(original)
                    && entities.HasBuffer<Game.Net.SubLane>(target) == entities.HasBuffer<Game.Net.SubLane>(original)
                    && entities.HasBuffer<LiveSubObject>(target) == entities.HasBuffer<LiveSubObject>(original),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_BUFFER_BRANCH");
                CopyOptional<Game.Objects.Elevation>(entities, role, target, original);
                CopyOptional<LocalTransformCache>(entities, role, target, original);
                Require(!role.Transform.Equals(role.OriginalTransform), "PREVIEW_COMMIT_RELOCATION_OBJECT_POSE_UNCHANGED");
                role.Add(ObjectCommand.Component("SetComponent", original, role.Transform));
                if (i == 0)
                {
                    role.Add(ObjectCommand.EventBirth());
                    role.Add(ObjectCommand.Component("SetComponent", Entity.Null,
                        new PathTargetMoved(original, role.OriginalTransform.m_Position, role.Transform.m_Position)));
                    var recent = new Recent { m_ModificationFrame = frameIndex, m_ModificationCost = temp.m_Cost };
                    if (role.OriginalRecent is Recent old)
                    {
                        int previousRefund = ObjectUtils.GetRefundAmount(old, frameIndex, config);
                        old.m_ModificationFrame = frameIndex;
                        recent.m_ModificationCost = Math.Min(temp.m_Value, checked(recent.m_ModificationCost + old.m_ModificationCost
                            + previousRefund - ObjectUtils.GetRefundAmount(old, frameIndex, config)));
                    }
                    Require(recent.m_ModificationCost > 0, "PREVIEW_COMMIT_RELOCATION_OBJECT_RECENT_BRANCH");
                    role.Add(ObjectCommand.Component(role.OriginalRecent.HasValue ? "SetComponent" : "AddComponent", original, recent));
                    plan.RecentCost = recent.m_ModificationCost;
                }
                role.Add(ObjectCommand.Component<Updated>("AddComponent", original));
                role.Add(ObjectCommand.Component<Deleted>("AddComponent", target));
            }

            Require(modified == 5 && deleted == 1 && plan.CreatedTarget != Entity.Null
                && plan.Roles.Count == TargetCount && plan.CommandCount <= RelocationCommandLimit,
                "PREVIEW_COMMIT_RELOCATION_OBJECT_PLAN_BOUND");
            var counts = instanceCounts!.GetInstanceCounts(readOnly: true, out var writers);
            writers.Complete();
            Require(counts.IsCreated, "PREVIEW_COMMIT_RELOCATION_OBJECT_INSTANCE_COUNTS_UNAVAILABLE");
            plan.PreviousCount = counts.TryGetValue(plan.CreatedPrefab, out int previous) ? previous : 0;
            Require(plan.PreviousCount >= 0, "PREVIEW_COMMIT_RELOCATION_OBJECT_INSTANCE_COUNT_INVALID");
            return plan;
        }

        private static void CopyOptional<T>(EntityManager entities, ObjectRole role, Entity target, Entity original)
            where T : unmanaged, IComponentData
        {
            bool before = entities.HasComponent<T>(original);
            if (entities.HasComponent<T>(target))
            {
                role.Add(ObjectCommand.Component(before ? "SetComponent" : "AddComponent", original,
                    entities.GetComponentData<T>(target)));
                if (before) role.OriginalComponents.Add(entities.GetComponentData<T>(original));
            }
            else if (before)
            {
                role.Add(ObjectCommand.Component<T>("RemoveComponent", original));
                role.OriginalComponents.Add(entities.GetComponentData<T>(original));
            }
        }

        private RelocationPlan RequireRelocationInputs(EntityManager entities, Entity[] graph)
        {
            var current = ReadRelocationPlan(entities, graph, frame!.Value);
            var captured = relocationPlan!;
            Require(captured.CommandCount == current.CommandCount && captured.PreviousCount == current.PreviousCount
                && captured.Economy.Equals(current.Economy),
                "PREVIEW_COMMIT_RELOCATION_OBJECT_INPUT_CHANGED");
            for (int i = 0; i < captured.Roles.Count; i++)
                Require(captured.Roles[i].Matches(current.Roles[i]), "PREVIEW_COMMIT_RELOCATION_OBJECT_INPUT_CHANGED");
            return current;
        }

        private void InspectRelocationOutput()
        {
            var plan = RequireRelocationInputs(EntityManager, cohort!);
            relocationInputsRechecked = true;
            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type? typeSetView = typeof(EntityCommandBuffer).GetNestedType("EntityMultipleComponentsCommandView", BindingFlags.NonPublic);
            Require(typeSetView != null, "PREVIEW_COMMIT_OBJECT_COMMAND_ABI");
            var buffer = output!.Borrow(PreviewCommitBarrier.ObjectBufferIndex);
            var raw = (Array)abi.Commands.GetValue(abi.Constructor.Invoke(new object[] { buffer }))!;
            commandsObserved = raw.Length;
            Require(raw.Length == plan.CommandCount, "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_COUNT");

            var stages = new int[plan.Roles.Count];
            var sortKeys = new int[plan.Roles.Count];
            int? deferred = null;
            foreach (object command in raw)
            {
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                int sort = abi.Int(command, "SortKey");
                int bytes = abi.Int(command, "TotalSizeInBytes");
                Require(sort >= 0 && bytes > 0 && bytes <= 2048 && abi.Int(command, "BatchCount") == 1,
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_HEADER");
                bool creates = kind == "CreateEntity";
                Entity target = creates ? Entity.Null : (Entity)abi.Value(command, "Entity", typeof(Entity));
                int slot = target.Index <= 0 ? 0 : plan.FindRole(target);
                Require(slot >= 0 && stages[slot] < plan.Roles[slot].Commands.Count,
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_TARGET");
                int index = stages[slot]++;
                var expected = plan.Roles[slot].Commands[index];
                if (index == 0) sortKeys[slot] = sort;
                Require(sortKeys[slot] == sort && kind == expected.Kind,
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_ORDER");
                if (creates)
                {
                    Require(command.GetType() == abi.Create && deferred == null
                        && ((EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype))).Equals(pathTarget),
                        "PREVIEW_COMMIT_RELOCATION_OBJECT_EVENT_ARCHETYPE");
                    deferred = abi.Int(command, "EntityIdentityIndex");
                    Require(deferred < 0, "PREVIEW_COMMIT_RELOCATION_OBJECT_EVENT_IDENTITY");
                    continue;
                }
                Require(abi.Int(command, "IdentityIndex") == 0 && (expected.Target != Entity.Null
                    ? target == expected.Target : target.Index < 0 && target.Index == deferred),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_IDENTITY");
                if (expected.Types is ComponentTypeSet types)
                {
                    RequireTypeSet(command, kind, expected.Kind, typeSetView!, types, abi);
                    continue;
                }
                Require(command.GetType() == abi.Component
                    && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == expected.Type
                    && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))
                    && (short)abi.Value(command, "ComponentSize", typeof(short)) ==
                        (expected.Value == null ? 0 : TypeManager.GetTypeInfo(expected.Type).TypeSize)
                    && Equals(abi.Value(command, "ComponentValue", typeof(object)), expected.Value),
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_VALUE");
                if (expected.Value is PathTargetMoved) pathEventObserved = true;
            }
            for (int i = 0; i < stages.Length; i++)
                Require(stages[i] == plan.Roles[i].Commands.Count, "PREVIEW_COMMIT_RELOCATION_OBJECT_COMMAND_SET_INCOMPLETE");
            Require(pathEventObserved, "PREVIEW_COMMIT_RELOCATION_OBJECT_EVENT_UNCONFIRMED");
            relocationRecentCost = plan.RecentCost;
        }

        internal void RequireRelocationApplied(EntityManager entities)
        {
            Require(OutputConfirmed && relocationPlan != null, "PREVIEW_COMMIT_RELOCATION_OBJECT_PLAN_UNAVAILABLE");
            foreach (var role in relocationPlan!.Roles)
            {
                Entity original = role.Temp.m_Original;
                if (original == Entity.Null)
                {
                    Require(entities.Exists(role.Target) && !entities.HasComponent<Temp>(role.Target)
                        && entities.HasComponent<Applied>(role.Target) && entities.HasComponent<Created>(role.Target)
                        && entities.HasComponent<Updated>(role.Target) && !entities.HasComponent<Deleted>(role.Target)
                        && entities.GetComponentData<PrefabRef>(role.Target).m_Prefab == role.Prefab
                        && entities.GetComponentData<Owner>(role.Target).m_Owner == role.Owner
                        && entities.GetComponentData<Transform>(role.Target).Equals(role.Transform),
                        "PREVIEW_COMMIT_RELOCATION_OBJECT_CREATED_EFFECT");
                    continue;
                }

                Require(entities.Exists(role.Target) && entities.HasComponent<Deleted>(role.Target)
                    && entities.Exists(original) && entities.GetComponentData<PrefabRef>(original).m_Prefab == role.Prefab,
                    "PREVIEW_COMMIT_RELOCATION_OBJECT_DELETE_EFFECT");
                if ((role.Temp.m_Flags & TempFlags.Delete) != 0)
                    Require(entities.HasComponent<Deleted>(original), "PREVIEW_COMMIT_RELOCATION_OBJECT_OLD_CHILD_EFFECT");
                else
                    Require(!entities.HasComponent<Deleted>(original) && entities.HasComponent<Updated>(original)
                        && entities.GetComponentData<Transform>(original).Equals(role.Transform),
                        "PREVIEW_COMMIT_RELOCATION_OBJECT_MODIFIED_EFFECT");
            }
            var recent = entities.GetComponentData<Recent>(relocationPlan.Roles[0].Temp.m_Original);
            Require(recent.m_ModificationCost == relocationPlan.RecentCost && recent.m_ModificationFrame == frame,
                "PREVIEW_COMMIT_RELOCATION_OBJECT_RECENT_EFFECT");
        }

        internal void RequireRelocationTrigger(TriggerAction action)
        {
            var plan = relocationPlan ?? throw new InvalidOperationException("PREVIEW_COMMIT_RELOCATION_OBJECT_PLAN_UNAVAILABLE");
            Require(OutputConfirmed && action.m_TriggerType == TriggerType.ObjectCreated && action.m_TriggerPrefab == plan.CreatedPrefab
                && action.m_PrimaryTarget == plan.CreatedTarget && action.m_SecondaryTarget == plan.CreatedTarget
                && action.m_Value == plan.PreviousCount, "PREVIEW_COMMIT_RELOCATION_OBJECT_TRIGGER_CHANGED");
            relocationTriggerObserved = true;
        }

        internal JObject RelocationSnapshot()
        {
            var result = Snapshot();
            result["expectedCommands"] = expectedCommands;
            result["sourceInputsRechecked"] = relocationInputsRechecked;
            result["pathEventObserved"] = pathEventObserved;
            result["nativeRecentCost"] = relocationRecentCost;
            result["objectCreatedTriggerVerified"] = relocationTriggerObserved;
            return result;
        }

        private sealed class RelocationPlan
        {
            internal readonly List<ObjectRole> Roles = new List<ObjectRole>(7);
            internal Entity CreatedTarget, CreatedPrefab;
            internal int PreviousCount, RecentCost;
            internal EconomyParameterData Economy;
            internal int CommandCount
            {
                get { int count = 0; foreach (ObjectRole role in Roles) count += role.Commands.Count; return count; }
            }
            internal ObjectRole AddRole(Entity target, Entity prefab, Entity owner, Temp temp, Transform transform)
            {
                var role = new ObjectRole(target, prefab, owner, temp, transform);
                Roles.Add(role);
                return role;
            }
            internal int FindRole(Entity target)
            {
                for (int i = 0; i < Roles.Count; i++)
                    if (Roles[i].Target == target || Roles[i].Temp.m_Original == target) return i;
                return -1;
            }
        }

        private sealed class ObjectRole
        {
            internal readonly Entity Target, Prefab, Owner;
            internal readonly Temp Temp;
            internal readonly Transform Transform;
            internal Transform OriginalTransform;
            internal Recent? OriginalRecent;
            internal readonly List<object> OriginalComponents = new List<object>(2);
            internal readonly List<ObjectCommand> Commands = new List<ObjectCommand>(8);
            internal ObjectRole(Entity target, Entity prefab, Entity owner, Temp temp, Transform transform)
            { Target = target; Prefab = prefab; Owner = owner; Temp = temp; Transform = transform; }
            internal void Add(ObjectCommand command) => Commands.Add(command);
            internal bool Matches(ObjectRole other)
            {
                if (Target != other.Target || Prefab != other.Prefab || Owner != other.Owner || !Temp.Equals(other.Temp)
                    || !Transform.Equals(other.Transform) || !OriginalTransform.Equals(other.OriginalTransform)
                    || !Nullable.Equals(OriginalRecent, other.OriginalRecent)
                    || Commands.Count != other.Commands.Count || OriginalComponents.Count != other.OriginalComponents.Count) return false;
                for (int i = 0; i < OriginalComponents.Count; i++)
                    if (!Equals(OriginalComponents[i], other.OriginalComponents[i])) return false;
                for (int i = 0; i < Commands.Count; i++)
                    if (!Commands[i].Matches(other.Commands[i])) return false;
                return true;
            }
        }

        private readonly struct ObjectCommand
        {
            internal readonly string Kind;
            internal readonly Entity Target;
            internal readonly TypeIndex Type;
            internal readonly ComponentTypeSet? Types;
            internal readonly object? Value;
            private ObjectCommand(string kind, Entity target, TypeIndex type, ComponentTypeSet? types, object? value)
            { Kind = kind; Target = target; Type = type; Types = types; Value = value; }
            internal static ObjectCommand EventBirth() => new ObjectCommand("CreateEntity", Entity.Null, default, null, null);
            internal static ObjectCommand Markers(string kind, Entity target, ComponentTypeSet types) =>
                new ObjectCommand(kind, target, default, types, null);
            internal static ObjectCommand Component<T>(string kind, Entity target) where T : unmanaged, IComponentData =>
                new ObjectCommand(kind, target, TypeManager.GetTypeIndex<T>(), null, null);
            internal static ObjectCommand Component<T>(string kind, Entity target, T value) where T : unmanaged, IComponentData =>
                new ObjectCommand(kind, target, TypeManager.GetTypeIndex<T>(), null, value);
            internal bool Matches(ObjectCommand other) => Kind == other.Kind && Target == other.Target
                && Type == other.Type && Types.HasValue == other.Types.HasValue && Equals(Value, other.Value);
        }
    }
}
#endif
