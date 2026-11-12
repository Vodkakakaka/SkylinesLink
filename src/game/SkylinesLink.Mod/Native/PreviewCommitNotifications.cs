#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    // Reads the stock notification output for the same nine-member graph.
    // The caller owns the buffer and decides whether any result is published.
    [DisableAutoCreation]
    public sealed partial class PreviewCommitNotifications : ApplyNotificationsSystem
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private readonly List<(FieldInfo Field, object Value)> bindings = new List<(FieldInfo, object)>(3);
        private PreviewCommitBarrier? output;
        private EntityQuery query;
        private Entity[]? graph;
        private Temp[]? temps;
        private Action? guard;
        private int actor, slot, commands, queryCount, originalCount, nonemptyBuffers, originalIconCount, methodsChecked;
        private const int InlineIconLimit = 4;
        private NotificationInputs? inputs;
        private int retainedIcons, deletedIcons;
        private bool creationStarted, creationReturned, entered, returned, joined, confirmed;

        internal bool NativeWorkMayReadSource => entered && !joined;
        internal bool DestructionReturned { get; private set; }
        internal bool StorageReleased => !creationStarted || DestructionReturned;
        internal bool Passed => confirmed && !NativeWorkMayReadSource;

        internal void Configure(PreviewCommitBarrier barrier, int actor, Entity[] graph, Temp[] temps)
        {
            Require(output == null && World == null && actor > 0 && graph.Length == 9 && temps.Length == graph.Length,
                "PREVIEW_COMMIT_NOTIFICATION_CONFIGURATION_REJECTED");
            output = barrier;
            this.actor = actor;
            this.graph = (Entity[])graph.Clone();
            this.temps = (Temp[])temps.Clone();
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            Enabled = false;
            Require(MainThreadDispatcher.isMainThread && output != null && ReferenceEquals(output.World, World)
                && NativeConstructionBuild.Game(typeof(ApplyNotificationsSystem).Module),
                "PREVIEW_COMMIT_NOTIFICATION_CONFIGURATION_MISSING");
            // Include enabled actor Temp as well, then reject it by the exact
            // membership check. A Disabled filter could hide foreign actor Temp.
            query = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<ObjectGenerationScope>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            RequireForUpdate(query);
            Bind<ToolOutputBarrier>("m_ToolOutputBarrier", output!);
            Bind("m_TempQuery", query);
            Bind("m_AppliedTypes", new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Created>(),
                ComponentType.ReadWrite<Updated>()));
            creationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            Require(!NativeWorkMayReadSource, "PREVIEW_COMMIT_NOTIFICATION_NATIVE_RELEASE_UNRESOLVED");
            guard = null;
            DestructionReturned = true;
        }

        internal void Prepare(int expectedSlot, Action guardLiveGraph)
        {
            Require(creationReturned && !entered && guard == null && !Enabled && ShouldRunSystem()
                && output!.PendingCount == expectedSlot, "PREVIEW_COMMIT_NOTIFICATION_PREPARATION_REJECTED");
            guard = guardLiveGraph;
            slot = expectedSlot;
            Enabled = true;
            try
            {
                Update();
                Require(entered && returned, "PREVIEW_COMMIT_NOTIFICATION_NATIVE_NOT_RETURNED");
                JobHandle writer = Dependency;
                Require(!writer.Equals(default(JobHandle)), "PREVIEW_COMMIT_NOTIFICATION_WRITER_MISSING");
                writer.Complete();
                joined = true;
                guard();
                RequireBindings();
                Require(output!.PendingCount == slot + 1 && inputs!.Matches(ReadInputs()),
                    "PREVIEW_COMMIT_NOTIFICATION_INPUT_CHANGED");
                ConfirmCommands();
                guard();
                confirmed = true;
            }
            finally { Enabled = false; }
        }

        protected override void OnUpdate()
        {
            Require(guard != null && !entered, "PREVIEW_COMMIT_NOTIFICATION_NATIVE_NOT_ARMED");
            guard!();
            RequireBindings();
            Require(output!.PendingCount == slot, "PREVIEW_COMMIT_NOTIFICATION_BUFFER_SLOT_CHANGED");
            var patches = ObservePatches();
            methodsChecked = patches.MethodsChecked;
            Require(patches.Qualified, "PREVIEW_COMMIT_NOTIFICATION_PATCH_UNQUALIFIED");
            inputs = ReadInputs();
            guard();
            entered = true;
            base.OnUpdate();
            returned = true;
        }

        private NotificationInputs ReadInputs()
        {
            var entities = EntityManager;
            queryCount = query.CalculateEntityCount();
            Require(queryCount == graph!.Length, "PREVIEW_COMMIT_NOTIFICATION_QUERY_BOUND");
            using (var queried = query.ToEntityArray(Allocator.Temp))
            {
                Require(queried.Length == graph.Length, "PREVIEW_COMMIT_NOTIFICATION_QUERY_CHANGED");
                foreach (Entity entity in queried)
                    Require(Array.IndexOf(graph, entity) >= 0 && entities.HasComponent<Disabled>(entity)
                        && !entities.HasComponent<Icon>(entity) && !entities.HasBuffer<IconElement>(entity),
                        "PREVIEW_COMMIT_NOTIFICATION_TEMP_UNQUALIFIED");
            }
            var originals = new HashSet<Entity>();
            var icons = new HashSet<Entity>();
            var observed = new NotificationInputs();
            nonemptyBuffers = 0;
            originalIconCount = 0;
            for (int i = 0; i < graph.Length; i++)
            {
                Entity target = graph[i];
                Temp temp = entities.GetComponentData<Temp>(target);
                Require(temp.Equals(temps![i]), "PREVIEW_COMMIT_NOTIFICATION_TEMP_CHANGED");
                Entity original = temp.m_Original;
                if (original == Entity.Null) continue;
                Require(originals.Add(original) && entities.Exists(original) && !entities.HasComponent<Icon>(original),
                    "PREVIEW_COMMIT_NOTIFICATION_ORIGINAL_UNQUALIFIED");
                if (!entities.HasBuffer<IconElement>(original)) continue;
                var elements = entities.GetBuffer<IconElement>(original, true);
                Require(elements.Length <= InlineIconLimit, "PREVIEW_COMMIT_NOTIFICATION_ORIGINAL_BUFFER_BOUND");
                if (elements.Length > 0) nonemptyBuffers++;
                originalIconCount += elements.Length;
                var rows = new IconInput[elements.Length];
                for (int index = 0; index < elements.Length; index++)
                {
                    Entity icon = elements[index].m_Icon;
                    Require(icon.Index > 0 && icons.Add(icon) && entities.Exists(icon)
                        && entities.HasComponent<Icon>(icon) && entities.HasComponent<PrefabRef>(icon)
                        && !entities.HasComponent<Temp>(icon) && !entities.HasComponent<Deleted>(icon)
                        && !entities.HasComponent<Disabled>(icon) && !entities.HasComponent<ObjectGenerationScope>(icon)
                        && (!entities.HasComponent<Owner>(icon) || entities.GetComponentData<Owner>(icon).m_Owner == original),
                        "PREVIEW_COMMIT_NOTIFICATION_ORIGINAL_ICON_UNQUALIFIED");
                    Entity prefab = entities.GetComponentData<PrefabRef>(icon).m_Prefab;
                    Require(prefab.Index > 0 && entities.Exists(prefab), "PREVIEW_COMMIT_NOTIFICATION_ICON_PREFAB_UNAVAILABLE");
                    Icon value = entities.GetComponentData<Icon>(icon);
                    bool discard = entities.HasComponent<ToolErrorData>(prefab)
                        || (int)value.m_Priority >= 250 && entities.HasComponent<Destroyed>(original)
                            && !entities.HasComponent<Destroyed>(target);
                    rows[index] = new IconInput(icon, prefab, value, discard);
                    if (discard) observed.Deleted.Add(icon);
                }
                observed.Buffers.Add(original, rows);
            }
            originalCount = originals.Count;
            Require(originalCount == 7, "PREVIEW_COMMIT_NOTIFICATION_ORIGINAL_COUNT");
            return observed;
        }

        private void ConfirmCommands()
        {
            var abi = new ObjectDefinitionCommandProbe.Abi();
            object view = abi.Constructor.Invoke(new object[] { output!.Borrow(slot) });
            var observed = (Array)abi.Commands.GetValue(view)!;
            var planned = inputs!;
            commands = observed.Length;
            Require(commands == planned.Buffers.Count + planned.Deleted.Count, "PREVIEW_COMMIT_NOTIFICATION_COMMAND_COUNT");
            var targets = new HashSet<Entity>();
            foreach (object command in observed)
            {
                var entity = (Entity)abi.Value(command, "Entity", typeof(Entity));
                string? kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!);
                int bytes = abi.Int(command, "TotalSizeInBytes");
                Require(entity.Index > 0 && targets.Add(entity) && abi.Int(command, "BatchCount") == 1
                    && abi.Int(command, "IdentityIndex") == 0 && bytes is > 0 and <= 2048,
                    "PREVIEW_COMMIT_NOTIFICATION_COMMAND_HEADER");
                if (planned.Deleted.Contains(entity))
                {
                    RequireScalar<Deleted>(command, kind, "AddComponent", abi);
                    deletedIcons++;
                    continue;
                }
                Require(planned.Buffers.TryGetValue(entity, out var rows), "PREVIEW_COMMIT_NOTIFICATION_COMMAND_TARGET");
                var kept = new List<Entity>(InlineIconLimit);
                foreach (var row in rows!) if (!row.Discard) kept.Add(row.Entity);
                if (kept.Count == 0) RequireScalar<IconElement>(command, kind, "RemoveComponent", abi);
                else
                {
                    Require(kind == "SetBuffer" && command.GetType() == abi.Buffer,
                        "PREVIEW_COMMIT_NOTIFICATION_BUFFER_COMMAND");
                    RequireInlineIconBuffer(command, bytes, kept, abi);
                    retainedIcons += kept.Count;
                }
            }
        }

        private static void RequireScalar<T>(object command, string? kind, string expected, ObjectDefinitionCommandProbe.Abi abi)
        {
            Require(command.GetType() == abi.Component && kind == expected
                && (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == TypeManager.GetTypeIndex<T>()
                && (short)abi.Value(command, "ComponentSize", typeof(short)) == 0
                && !(bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))
                && abi.Value(command, "ComponentValue", typeof(object)) == null,
                "PREVIEW_COMMIT_NOTIFICATION_COMMAND_UNQUALIFIED");
        }

        private static void RequireInlineIconBuffer(object command, int bytes, List<Entity> expected,
            ObjectDefinitionCommandProbe.Abi abi)
        {
            // The debug view borrows a command owned by the joined ECB. Read
            // only the pinned inline storage, never a spill allocation or a
            // pointer after the command buffer is released or played.
            TypeIndex type = TypeManager.GetTypeIndex<IconElement>();
            var info = TypeManager.GetTypeInfo(type);
            Require((TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex)) == type
                && (short)abi.Value(command, "ComponentSize", typeof(short)) == info.SizeInChunk
                && (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))
                && info.BufferCapacity == InlineIconLimit && info.ElementSize == Marshal.SizeOf<IconElement>()
                && IntPtr.Size == 8, "PREVIEW_COMMIT_NOTIFICATION_BUFFER_TYPE");
            Assembly ecs = typeof(EntityCommandBuffer).Assembly;
            Type? nodeType = ecs.GetType("Unity.Entities.BufferHeaderNode", false);
            Type? headerType = ecs.GetType("Unity.Entities.BufferHeader", false);
            Type? commandType = ecs.GetType("Unity.Entities.EntityBufferCommand", false);
            var addressField = abi.Buffer.GetField("BufferNode", BindingFlags.Public | BindingFlags.Instance);
            var boxedAddress = addressField?.GetValue(command);
            var getAddress = typeof(Pointer).GetMethod("GetPointerValue", Declared, null, Type.EmptyTypes, null);
            Require(nodeType != null && headerType != null && commandType != null && addressField != null
                && addressField.FieldType == nodeType.MakePointerType()
                && nodeType.GetField("TempBuffer", Declared)?.FieldType == headerType
                && commandType.GetField("BufferNode", Declared)?.FieldType == nodeType
                && boxedAddress is Pointer && getAddress?.ReturnType == typeof(IntPtr),
                "PREVIEW_COMMIT_NOTIFICATION_BUFFER_ABI");
            int headerOffset = Marshal.OffsetOf(nodeType!, "TempBuffer").ToInt32();
            int commandOffset = Marshal.OffsetOf(commandType!, "BufferNode").ToInt32();
            int headerSize = Marshal.SizeOf(headerType!);
            Require(headerOffset == 8 && headerSize == 16
                && Marshal.OffsetOf(headerType!, "Pointer").ToInt32() == 0
                && Marshal.OffsetOf(headerType!, "Length").ToInt32() == 8
                && Marshal.OffsetOf(headerType!, "Capacity").ToInt32() == 12
                && commandOffset > 0 && commandOffset + headerOffset + headerSize + InlineIconLimit * info.ElementSize <= bytes,
                "PREVIEW_COMMIT_NOTIFICATION_BUFFER_LAYOUT");
            var node = (IntPtr)getAddress!.Invoke(boxedAddress, null)!;
            Require(node != IntPtr.Zero, "PREVIEW_COMMIT_NOTIFICATION_BUFFER_ADDRESS");
            var header = IntPtr.Add(node, headerOffset);
            Require(Marshal.ReadIntPtr(header) == IntPtr.Zero && Marshal.ReadInt32(header, 8) == expected.Count
                && Marshal.ReadInt32(header, 12) == InlineIconLimit, "PREVIEW_COMMIT_NOTIFICATION_BUFFER_LENGTH");
            var element = IntPtr.Add(header, headerSize);
            foreach (Entity expectedIcon in expected)
            {
                Require(Marshal.PtrToStructure<IconElement>(element).m_Icon == expectedIcon,
                    "PREVIEW_COMMIT_NOTIFICATION_BUFFER_VALUE");
                element = IntPtr.Add(element, info.ElementSize);
            }
        }

        private sealed class NotificationInputs
        {
            internal readonly Dictionary<Entity, IconInput[]> Buffers = new Dictionary<Entity, IconInput[]>(7);
            internal readonly HashSet<Entity> Deleted = new HashSet<Entity>();

            internal bool Matches(NotificationInputs current)
            {
                if (Buffers.Count != current.Buffers.Count || !Deleted.SetEquals(current.Deleted)) return false;
                foreach (var pair in Buffers)
                {
                    if (!current.Buffers.TryGetValue(pair.Key, out var rows) || rows.Length != pair.Value.Length) return false;
                    for (int index = 0; index < rows.Length; index++) if (!rows[index].Matches(pair.Value[index])) return false;
                }
                return true;
            }
        }

        private readonly struct IconInput
        {
            internal readonly Entity Entity;
            internal readonly bool Discard;
            private readonly Entity prefab;
            private readonly Icon value;

            internal IconInput(Entity entity, Entity prefab, Icon value, bool discard)
            { Entity = entity; this.prefab = prefab; this.value = value; Discard = discard; }

            internal bool Matches(IconInput other) => Entity == other.Entity && prefab == other.prefab
                && value.Equals(other.value) && Discard == other.Discard;
        }

        internal void RequirePrepared()
        {
            Require(Passed, "PREVIEW_COMMIT_NOTIFICATION_NOT_PREPARED");
            RequireBindings();
            Require(inputs!.Matches(ReadInputs()), "PREVIEW_COMMIT_NOTIFICATION_INPUT_CHANGED");
        }

        internal void RequireApplied(EntityManager entities)
        {
            Require(Passed, "PREVIEW_COMMIT_NOTIFICATION_NOT_PREPARED");
            foreach (var pair in inputs!.Buffers)
            {
                int count = 0;
                foreach (var icon in pair.Value) if (!icon.Discard) count++;
                Require(entities.HasBuffer<IconElement>(pair.Key) == (count > 0), "PREVIEW_COMMIT_NOTIFICATION_BUFFER_EFFECT_CHANGED");
                if (count == 0) continue;
                var entries = entities.GetBuffer<IconElement>(pair.Key, true);
                Require(entries.Length == count, "PREVIEW_COMMIT_NOTIFICATION_BUFFER_EFFECT_CHANGED");
                int index = 0;
                foreach (var icon in pair.Value)
                    if (!icon.Discard) Require(entries[index++].m_Icon == icon.Entity, "PREVIEW_COMMIT_NOTIFICATION_ICON_EFFECT_CHANGED");
            }
            foreach (Entity deleted in inputs.Deleted)
                Require(entities.Exists(deleted) && entities.HasComponent<Deleted>(deleted), "PREVIEW_COMMIT_NOTIFICATION_DELETE_UNCONFIRMED");
        }

        private void Bind<T>(string name, T value) where T : notnull
        {
            FieldInfo? field = typeof(ApplyNotificationsSystem).GetField(name, Declared);
            Require(field != null && !field.IsStatic && !field.IsInitOnly && field.FieldType == typeof(T),
                "PREVIEW_COMMIT_NOTIFICATION_FIELD_ABI");
            field!.SetValue(this, value);
            bindings.Add((field, value));
        }

        private void RequireBindings()
        {
            Require(World.IsCreated && ReferenceEquals(World.GetExistingSystemManaged<PreviewCommitBarrier>(), output)
                && ReferenceEquals(output!.World, World) && query.HasFilter(), "PREVIEW_COMMIT_NOTIFICATION_BINDING_CHANGED");
            foreach (var (field, value) in bindings)
                Require(Equals(field.GetValue(this), value), "PREVIEW_COMMIT_NOTIFICATION_BINDING_CHANGED");
        }

        private static DefinitionProducerPatchProbe ObservePatches()
        {
            Type owner = typeof(ApplyNotificationsSystem);
            var methods = new List<MethodBase>(15);
            AddDeclared(owner);
            foreach (string name in new[] { "ApplyTempIconsJob", "TypeHandle" })
            {
                Type? nested = owner.GetNestedType(name, BindingFlags.NonPublic);
                Require(nested != null && nested.IsValueType, "PREVIEW_COMMIT_NOTIFICATION_METHOD_ABI");
                AddDeclared(nested!);
            }
            MethodInfo? create = typeof(SafeCommandBufferSystem).GetMethod("CreateCommandBuffer", Declared, null, Type.EmptyTypes, null);
            if (create != null) methods.Add(create);
            return DefinitionProducerPatchProbe.ObserveMethods(methods.ToArray(),
                create?.ReturnType == typeof(EntityCommandBuffer), "PREVIEW_COMMIT_NOTIFICATION");

            void AddDeclared(Type type)
            {
                methods.AddRange(type.GetMethods(Declared));
                methods.AddRange(type.GetConstructors(Declared));
            }
        }

        internal JObject Snapshot() => new JObject {
            ["passed"] = Passed, ["nativeEntered"] = entered, ["nativeReturned"] = returned, ["nativeJoined"] = joined,
            ["queryCount"] = queryCount, ["originalCount"] = originalCount, ["originalBuffers"] = inputs?.Buffers.Count,
            ["nonemptyOriginalBuffers"] = nonemptyBuffers, ["originalIconCount"] = originalIconCount,
                        ["retainedIcons"] = retainedIcons, ["deletedIcons"] = deletedIcons, ["commandsObserved"] = commands,
            ["methodsChecked"] = methodsChecked, ["destructionReturned"] = DestructionReturned
        };

        private static void Require(bool condition, string code)
        {
            if (!condition) throw new PreviewCommitObjects.Rejection(code);
        }
    }
}
#endif
