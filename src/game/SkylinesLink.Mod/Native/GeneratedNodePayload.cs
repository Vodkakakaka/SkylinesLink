#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    internal enum GeneratedNodeWriteKind
    {
        Prefab, RemoveServiceUpgrade, Node, Orphan, RandomSeed, Standalone, Elevation,
        Fixed, LocalConnect, Owner, OwnerDefinition, OutsideConnection, EditorContainer, Temp
    }

    // Copies only the fixed unmanaged node values. A remove has no value; the
    // two zero-sized additions retain their actual default SDK struct types.
    internal readonly struct GeneratedNodeWrite
    {
        internal readonly GeneratedNodeWriteKind Kind;
        internal readonly object? Value;
        private GeneratedNodeWrite(GeneratedNodeWriteKind kind, object? value) { Kind = kind; Value = value; }

        internal static bool TryCapture(Type type, string command, object? value, int bytes, out GeneratedNodeWrite write)
        {
            write = default;
            if (type == typeof(PrefabRef) && command == "SetComponent" && value is PrefabRef)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Prefab, value);
            else if (type == typeof(Game.Buildings.ServiceUpgrade) && command == "RemoveComponent" && bytes == 0 && value == null)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.RemoveServiceUpgrade, null);
            else if (type == typeof(Game.Net.Node) && command == "SetComponent" && value is Game.Net.Node)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Node, value);
            else if (type == typeof(Game.Net.Orphan) && command == "AddComponent" && value is Game.Net.Orphan)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Orphan, value);
            else if (type == typeof(PseudoRandomSeed) && command == "SetComponent" && value is PseudoRandomSeed)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.RandomSeed, value);
            else if (type == typeof(Game.Net.Standalone) && command == "AddComponent"
                && (value is Game.Net.Standalone || bytes == 0 && value == null))
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Standalone, value ?? default(Game.Net.Standalone));
            else if (type == typeof(Game.Net.Elevation) && command == "AddComponent" && value is Game.Net.Elevation)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Elevation, value);
            else if (type == typeof(Game.Net.Fixed) && command == "AddComponent" && value is Game.Net.Fixed)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Fixed, value);
            else if (type == typeof(Game.Net.LocalConnect) && command == "AddComponent"
                && (value is Game.Net.LocalConnect || bytes == 0 && value == null))
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.LocalConnect, value ?? default(Game.Net.LocalConnect));
            else if (type == typeof(Owner) && command == "AddComponent" && value is Owner)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Owner, value);
            else if (type == typeof(OwnerDefinition) && command == "AddComponent" && value is OwnerDefinition)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.OwnerDefinition, value);
            else if (type == typeof(Game.Net.OutsideConnection) && command == "AddComponent" && value is Game.Net.OutsideConnection)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.OutsideConnection, value);
            else if (type == typeof(Game.Tools.EditorContainer) && command == "SetComponent" && value is Game.Tools.EditorContainer)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.EditorContainer, value);
            else if (type == typeof(Temp) && command == "AddComponent" && value is Temp)
                write = new GeneratedNodeWrite(GeneratedNodeWriteKind.Temp, value);
            else return false;
            return true;
        }

        internal bool HasTypedValue()
        {
            switch (Kind)
            {
                case GeneratedNodeWriteKind.Prefab: return Value is PrefabRef;
                case GeneratedNodeWriteKind.RemoveServiceUpgrade: return Value == null;
                case GeneratedNodeWriteKind.Node: return Value is Game.Net.Node;
                case GeneratedNodeWriteKind.Orphan: return Value is Game.Net.Orphan;
                case GeneratedNodeWriteKind.RandomSeed: return Value is PseudoRandomSeed;
                case GeneratedNodeWriteKind.Standalone: return Value is Game.Net.Standalone;
                case GeneratedNodeWriteKind.Elevation: return Value is Game.Net.Elevation;
                case GeneratedNodeWriteKind.Fixed: return Value is Game.Net.Fixed;
                case GeneratedNodeWriteKind.LocalConnect: return Value is Game.Net.LocalConnect;
                case GeneratedNodeWriteKind.Owner: return Value is Owner;
                case GeneratedNodeWriteKind.OwnerDefinition: return Value is OwnerDefinition;
                case GeneratedNodeWriteKind.OutsideConnection: return Value is Game.Net.OutsideConnection;
                case GeneratedNodeWriteKind.EditorContainer: return Value is Game.Tools.EditorContainer;
                case GeneratedNodeWriteKind.Temp: return Value is Temp;
                default: return false;
            }
        }
    }

    // The command observer is the provenance boundary. This immutable handoff
    // retains every admitted operation in its original order, not its JSON view
    // or an alias into the ECB. It is consumed only in the same Source scope.
    internal sealed class GeneratedNodePayload
    {
        internal readonly World Source;
        internal readonly EntityArchetype SourceArchetype;
        private readonly GeneratedNodeWrite[] writes;
        internal int WriteCount => writes.Length;
        internal GeneratedNodeWrite WriteAt(int index) => writes[index];

        internal GeneratedNodePayload(World source, EntityArchetype sourceArchetype, List<GeneratedNodeWrite> values)
        {
            if (source == null || values == null || values.Count < 7 || values.Count > 14)
                throw new InvalidOperationException("GENERATED_NODE_PAYLOAD_SHAPE_INVALID");
            int mask = 0;
            foreach (GeneratedNodeWrite value in values)
            {
                if (!value.HasTypedValue()) throw new InvalidOperationException("GENERATED_NODE_PAYLOAD_VALUE_INVALID");
                int bit = Bit(value.Kind);
                if ((mask & bit) != 0) throw new InvalidOperationException("GENERATED_NODE_PAYLOAD_DUPLICATE_WRITE");
                mask |= bit;
            }
            int required = Bit(GeneratedNodeWriteKind.Prefab) | Bit(GeneratedNodeWriteKind.RemoveServiceUpgrade)
                | Bit(GeneratedNodeWriteKind.Node) | Bit(GeneratedNodeWriteKind.Standalone) | Bit(GeneratedNodeWriteKind.Owner)
                | Bit(GeneratedNodeWriteKind.OwnerDefinition) | Bit(GeneratedNodeWriteKind.Temp);
            if ((mask & required) != required
                || ((mask & Bit(GeneratedNodeWriteKind.Orphan)) != 0) != ((mask & Bit(GeneratedNodeWriteKind.RandomSeed)) != 0))
                throw new InvalidOperationException("GENERATED_NODE_PAYLOAD_INCOMPLETE");
            Source = source; SourceArchetype = sourceArchetype; writes = values.ToArray();
        }

        private static int Bit(GeneratedNodeWriteKind kind) => 1 << (int)kind;
    }
}
#endif
