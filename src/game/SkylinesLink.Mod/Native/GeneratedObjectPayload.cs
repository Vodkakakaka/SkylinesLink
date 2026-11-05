#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Entities;
using Animation = Game.Tools.Animation;

namespace SkylinesLink.Mod.Native
{
    internal enum GeneratedObjectWriteKind { DisableMeshColor, Transform, Prefab, Surface, Temp, Animation, Interpolated, RandomSeed }

    // Only seven known unmanaged SDK values and one enableable-bit write are
    // admitted. Boxing copies those values; no debug view or native alias stays.
    internal readonly struct GeneratedObjectWrite
    {
        internal readonly GeneratedObjectWriteKind Kind;
        internal readonly object? Value;
        private GeneratedObjectWrite(GeneratedObjectWriteKind kind, object? value) { Kind = kind; Value = value; }

        internal static bool TryCapture(Type type, string command, object? value, int bytes, out GeneratedObjectWrite write)
        {
            write = default;
            if (type == typeof(CustomMeshColor) && command == "SetComponentEnabled" && value is bool enabled && !enabled)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.DisableMeshColor, false);
            else if (type == typeof(Transform) && command == "SetComponent" && value is Transform)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Transform, value);
            else if (type == typeof(PrefabRef) && command == "SetComponent" && value is PrefabRef)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Prefab, value);
            else if (type == typeof(Surface) && command == "AddComponent" && (value is Surface || bytes == 0 && value == null))
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Surface, value ?? default(Surface));
            else if (type == typeof(Temp) && command == "AddComponent" && value is Temp)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Temp, value);
            else if (type == typeof(Animation) && command == "AddComponent" && (value is Animation || bytes == 0 && value == null))
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Animation, value ?? default(Animation));
            else if (type == typeof(InterpolatedTransform) && command == "AddComponent" && value is InterpolatedTransform)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.Interpolated, value);
            else if (type == typeof(PseudoRandomSeed) && command == "SetComponent" && value is PseudoRandomSeed)
                write = new GeneratedObjectWrite(GeneratedObjectWriteKind.RandomSeed, value);
            else return false;
            return true;
        }
    }

    internal sealed class GeneratedObjectPayload
    {
        internal readonly World Source;
        internal readonly EntityArchetype SourceArchetype;
        private readonly GeneratedObjectWrite[] writes;
        internal int WriteCount => writes.Length;
        internal GeneratedObjectWrite WriteAt(int index) => writes[index];

        internal GeneratedObjectPayload(World source, EntityArchetype sourceArchetype, List<GeneratedObjectWrite> values)
        {
            if (source == null || values == null || values.Count != 8) throw new InvalidOperationException("GENERATED_PAYLOAD_SHAPE_INVALID");
            int mask = 0;
            foreach (var value in values) { int bit = 1 << (int)value.Kind; if ((mask & bit) != 0) throw new InvalidOperationException("GENERATED_PAYLOAD_DUPLICATE_WRITE"); mask |= bit; }
            if (mask != 255) throw new InvalidOperationException("GENERATED_PAYLOAD_INCOMPLETE");
            Source = source; SourceArchetype = sourceArchetype; writes = values.ToArray();
        }
    }
}
#endif
