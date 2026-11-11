#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A value already checked against the native command and its declared role.
    // The operation holds neither an ECB reference nor an entity to write to.
    internal readonly struct RelocationPreviewWrite
    {
        internal readonly ComponentType Type;
        internal readonly string Kind;
        private readonly object? value;
        private readonly Action<EntityManager, Entity, object>? setValue;
        private readonly Func<EntityManager, Entity, object, bool>? verifyValue;

        private RelocationPreviewWrite(ComponentType type, string kind, object? value,
            Action<EntityManager, Entity, object>? setValue, Func<EntityManager, Entity, object, bool>? verifyValue)
        {
            Type = type; Kind = kind; this.value = value;
            this.setValue = setValue; this.verifyValue = verifyValue;
        }

        internal static RelocationPreviewWrite Bind<T>(string kind, object? actual) where T : unmanaged, IComponentData
        {
            if (actual is T)
                return new RelocationPreviewWrite(ComponentType.ReadWrite<T>(), kind, actual, Store<T>, Matches<T>);
            if (actual == null && (kind == RelocationGenerationCommands.Remove
                || kind == RelocationGenerationCommands.Add && TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).SizeInChunk == 0))
                return new RelocationPreviewWrite(ComponentType.ReadWrite<T>(), kind, null, null, null);
            throw new InvalidOperationException("RELOCATION_PREVIEW_COMMAND_VALUE_UNSUPPORTED");
        }

        internal static RelocationPreviewWrite BindDisabled(ComponentType type, string kind, object? actual)
        {
            if (kind != RelocationGenerationCommands.Disable || actual != null || !type.TypeIndex.IsEnableable)
                throw new InvalidOperationException("RELOCATION_PREVIEW_COMMAND_ENABLE_UNSUPPORTED");
            return new RelocationPreviewWrite(type, kind, null, null, null);
        }

        internal void SetValue(EntityManager entities, Entity target)
        {
            if (value == null) return;
            if (!entities.HasComponent(target, Type))
                throw new InvalidOperationException("RELOCATION_PREVIEW_SET_TARGET_UNAVAILABLE");
            setValue!(entities, target, value);
        }

        internal bool Matches(EntityManager entities, Entity target)
        {
            bool present = entities.HasComponent(target, Type);
            if (Kind == RelocationGenerationCommands.Remove) return !present;
            if (!present) return false;
            if (Kind == RelocationGenerationCommands.Disable) return !entities.IsComponentEnabled(target, Type);
            return value == null || verifyValue!(entities, target, value);
        }

        private static void Store<T>(EntityManager entities, Entity target, object value) where T : unmanaged, IComponentData =>
            entities.SetComponentData(target, (T)value);

        private static bool Matches<T>(EntityManager entities, Entity target, object value) where T : unmanaged, IComponentData =>
            entities.GetComponentData<T>(target).Equals((T)value);
    }

    internal sealed class RelocationPreviewPayload
    {
        internal RelocationDefinitionPayload Definitions { get; }
        internal EntityArchetype BuildingArchetype { get; }
        internal EntityArchetype NodeArchetype { get; }
        internal Temp RootTemp { get; }
        internal Temp OldNodeTemp { get; }
        internal Temp NewNodeTemp { get; }
        private readonly RelocationPreviewWrite[][] targets;

        internal RelocationPreviewPayload(RelocationDefinitionPayload definitions, EntityArchetype building, EntityArchetype node,
            RelocationPreviewWrite[] root, RelocationPreviewWrite[] oldNode, RelocationPreviewWrite[] newNode,
            Temp rootTemp, Temp oldNodeTemp, Temp newNodeTemp)
        {
            Definitions = definitions; BuildingArchetype = building; NodeArchetype = node;
            targets = new[] { root, oldNode, newNode };
            RootTemp = rootTemp; OldNodeTemp = oldNodeTemp; NewNodeTemp = newNodeTemp;
        }

        internal int WriteCount(int role) => targets[role].Length;
        internal RelocationPreviewWrite WriteAt(int role, int index) => targets[role][index];
    }
}
#endif
