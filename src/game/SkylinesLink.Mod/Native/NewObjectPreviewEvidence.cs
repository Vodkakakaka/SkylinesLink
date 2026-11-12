#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Security.Cryptography;
using System.Text;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using NativeTransform = Game.Objects.Transform;
using ObjectElevation = Game.Objects.Elevation;

namespace SkylinesLink.Mod.Native
{
    // Copied preview intent and stable native results. The cancelled batch can
    // release its World, ECBs, archetypes and temporary entities independently.
    internal sealed class NewObjectPreviewEvidence
    {
        private readonly ObjectDefinitionValues definition;
        private readonly Entity rootPrefab, nodePrefab;
        private readonly NativeTransform rootTransform;
        private readonly Game.Net.Node node;
        private readonly ushort rootSeed, nodeSeed;
        private readonly Child[] children;
        private readonly string layoutSha256;
        internal int Cost { get; }

        internal NewObjectPreviewEvidence(ObjectDefinitionPayload input, EntityManager entities,
            GeneratedSubObjectsPayload cohort, GeneratedSubObjectEntities generated, int cost)
        {
            definition = input.Values; Cost = cost;
            rootPrefab = entities.GetComponentData<PrefabRef>(cohort.Root).m_Prefab;
            nodePrefab = entities.GetComponentData<PrefabRef>(cohort.Node).m_Prefab;
            rootTransform = entities.GetComponentData<NativeTransform>(cohort.Root);
            node = entities.GetComponentData<Game.Net.Node>(cohort.Node);
            rootSeed = entities.GetComponentData<PseudoRandomSeed>(cohort.Root).m_Seed;
            nodeSeed = entities.GetComponentData<PseudoRandomSeed>(cohort.Node).m_Seed;
            children = new Child[cohort.TargetCount];
            for (int i = 0; i < children.Length; i++)
                children[i] = new Child(cohort.TargetAt(i), generated.EntityAt(i), entities, cohort.Root);
            Array.Sort(children, (left, right) => left.Role != right.Role
                ? left.Role.CompareTo(right.Role) : left.Row.CompareTo(right.Row));
            layoutSha256 = ComputeLayoutDigest();
        }

        internal bool Matches(NewObjectPreviewEvidence expected)
        {
            if (!definition.Matches(expected.definition) || Cost != expected.Cost
                || rootPrefab != expected.rootPrefab || nodePrefab != expected.nodePrefab
                || !rootTransform.Equals(expected.rootTransform) || !node.Equals(expected.node)
                || rootSeed != expected.rootSeed || nodeSeed != expected.nodeSeed
                || children.Length != expected.children.Length)
                return false;
            for (int i = 0; i < children.Length; i++)
                if (!children[i].Matches(expected.children[i]))
                    return false;
            return true;
        }

        private string ComputeLayoutDigest()
        {
            var rows = new JArray();
            foreach (Child child in children) rows.Add(child.Snapshot());
            var layout = new JObject { ["rootSeed"] = rootSeed, ["nodeSeed"] = nodeSeed,
                ["rootPrefab"] = new JArray(rootPrefab.Index, rootPrefab.Version),
                ["nodePrefab"] = new JArray(nodePrefab.Index, nodePrefab.Version),
                ["rootPosition"] = new JArray(rootTransform.m_Position.x, rootTransform.m_Position.y, rootTransform.m_Position.z),
                ["rootRotation"] = new JArray(rootTransform.m_Rotation.value.x, rootTransform.m_Rotation.value.y,
                    rootTransform.m_Rotation.value.z, rootTransform.m_Rotation.value.w),
                ["nodePosition"] = new JArray(node.m_Position.x, node.m_Position.y, node.m_Position.z),
                ["nodeRotation"] = new JArray(node.m_Rotation.value.x, node.m_Rotation.value.y,
                    node.m_Rotation.value.z, node.m_Rotation.value.w), ["children"] = rows };
            string digest;
            using (var hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(layout.ToString(Formatting.None))))
                    .Replace("-", "").ToLowerInvariant();
            return digest;
        }

        internal JObject Snapshot() => new JObject { ["rootSeed"] = rootSeed, ["nodeSeed"] = nodeSeed,
            ["children"] = children.Length, ["cost"] = Cost, ["layoutSha256"] = layoutSha256 };

        private sealed class Child
        {
            internal readonly int Role, Row;
            private readonly Entity prefab;
            private readonly NativeTransform transform;
            private readonly PseudoRandomSeed seed;
            private readonly ObjectElevation elevation;
            private readonly Game.Objects.Aligned? aligned;
            private readonly Game.Objects.StreetLight? light;

            internal Child(GeneratedSubObjectTarget target, Entity entity, EntityManager entities, Entity root)
            {
                Role = target.Owner == root ? 0 : 1;
                Row = target.SourceRowIndex ?? throw new InvalidOperationException("PREVIEW_ROW_IDENTITY_UNAVAILABLE");
                // Followups may change native components after command playback.
                // The stable row comes from capture; layout comes from live ECS.
                prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                transform = entities.GetComponentData<NativeTransform>(entity);
                seed = entities.GetComponentData<PseudoRandomSeed>(entity);
                elevation = entities.GetComponentData<ObjectElevation>(entity);
                aligned = entities.HasComponent<Game.Objects.Aligned>(entity)
                    ? entities.GetComponentData<Game.Objects.Aligned>(entity) : (Game.Objects.Aligned?)null;
                light = entities.HasComponent<Game.Objects.StreetLight>(entity)
                    ? entities.GetComponentData<Game.Objects.StreetLight>(entity) : (Game.Objects.StreetLight?)null;
            }

            internal bool Matches(Child other) => Role == other.Role && Row == other.Row && prefab == other.prefab
                && transform.Equals(other.transform) && seed.Equals(other.seed) && elevation.Equals(other.elevation)
                && Nullable.Equals(aligned, other.aligned) && Nullable.Equals(light, other.light);

            internal JObject Snapshot() => new JObject {
                ["role"] = Role, ["row"] = Row, ["prefab"] = new JArray(prefab.Index, prefab.Version),
                ["position"] = new JArray(transform.m_Position.x, transform.m_Position.y, transform.m_Position.z),
                ["rotation"] = new JArray(transform.m_Rotation.value.x, transform.m_Rotation.value.y,
                    transform.m_Rotation.value.z, transform.m_Rotation.value.w),
                ["seed"] = seed.m_Seed, ["elevation"] = elevation.m_Elevation, ["elevationFlags"] = (uint)elevation.m_Flags,
                ["aligned"] = aligned.HasValue ? new JValue(aligned.Value.m_SubObjectIndex) : JValue.CreateNull(),
                ["streetLight"] = light.HasValue ? new JValue((uint)light.Value.m_State) : JValue.CreateNull()
            };
        }
    }
}
#endif
