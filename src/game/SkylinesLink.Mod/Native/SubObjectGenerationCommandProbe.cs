#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Core;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using NativeTransform = Game.Objects.Transform;
using ObjectElevation = Game.Objects.Elevation;
using NativeAnimation = Game.Tools.Animation;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Copies one admitted scalar command stream; never plays its ECB.</summary>
    internal static class SubObjectGenerationCommandProbe
    {
        private const int MaximumComponentBytes = 1024, MaximumOutputBytes = 32768;

        internal static JObject Capture(SubObjectGenerationProducer producer, SubObjectGenerationPreflight input,
            out GeneratedSubObjectsPayload? payload)
        {
            payload = null;
            if (!MainThreadDispatcher.isMainThread || producer == null || input == null || !input.Passed || input.IsRelocation
                || !input.Source.IsCreated || !ReferenceEquals(producer.World, input.Source)
                || !producer.CompletedFor(input) || producer.PendingCount != 2
                || input.Root == Entity.Null || input.Node == Entity.Null || input.Root == input.Node || input.Actor <= 0
                || input.CreateUpperBound <= 0 || input.CreateUpperBound > SubObjectGenerationPreflight.MaximumNormalCreates
                || input.CommandUpperBound <= 0 || input.CommandUpperBound > SubObjectGenerationPreflight.MaximumNormalCommands
                || input.CreateUpperBound != input.Candidates.Count
                || producer.CreateUpperBound != input.CreateUpperBound
                || producer.OutputCommandUpperBound != input.CommandUpperBound) throw Failure();

            var abi = new ObjectDefinitionCommandProbe.Abi();
            // Preflight proves the first native ECB has no old-instance work.
            // Both full getters are called once, after their production bounds.
            Array first = ReadCommands(abi, producer.BorrowCompletedForInspection(0));
            if (first.Length != 0) throw Failure();
            Array commands = ReadCommands(abi, producer.BorrowCompletedForInspection(1));
            if (commands.Length > input.CommandUpperBound) throw Failure();

            var records = new JArray();
            List<Target> created = ReadTargets(abi, commands, input, maximumSortKey: 1, records, out long recordBytes);

            int alignedCount = 0, lightCount = 0, componentCount = 0;
            var immutable = new List<GeneratedSubObjectTarget>(created.Count);
            var observations = new JArray();
            var emittedRows = new HashSet<(Entity Owner, int Row)>();
            foreach (Target target in created)
            {
                if (!target.Version.HasValue) throw Failure();
                GeneratedSubObjectTarget copy = FinishTarget(input, target, out JObject observation);
                if (copy.Has(GeneratedSubObjectWriteKind.Aligned)) alignedCount++;
                if (copy.Has(GeneratedSubObjectWriteKind.StreetLight)) lightCount++;
                if (!copy.SourceRowIndex.HasValue || !emittedRows.Add((copy.Owner, copy.SourceRowIndex.Value)))
                    throw Failure();
                componentCount += copy.WriteCount;
                // This preserves all actual targets, including two outputs from
                // two rows sharing a prefab. No prefab-key deduplication occurs.
                immutable.Add(copy); observations.Add(observation);
            }
            int mandatoryRows = 0, optionalRows = 0, optionalCreated = 0;
            foreach (var row in input.Candidates)
            {
                bool emitted = emittedRows.Contains((row.Owner, row.RowIndex));
                if (row.Probability == 100) { mandatoryRows++; if (!emitted) throw Failure(); }
                else { optionalRows++; if (emitted) optionalCreated++; }
                if (row.Probability == 0 && emitted) throw Failure();
            }
            if (alignedCount != SubObjectGenerationPreflight.NormalNodeRows || lightCount > input.StreetLightRowCount
                || componentCount + created.Count != commands.Length) throw Failure();
            var result = new JObject
            {
                ["kind"] = "native-subobject-generation-scalar-commands", ["firstBufferCommandsObserved"] = first.Length,
                ["commandUpperBound"] = input.CommandUpperBound, ["createUpperBound"] = input.CreateUpperBound,
                ["commandsObserved"] = commands.Length, ["createCommands"] = created.Count,
                ["componentWriteCommands"] = componentCount, ["alignedWrites"] = alignedCount, ["streetLightWrites"] = lightCount,
                ["typedValuesCopied"] = true, ["sourceRowsUniquelyAssociated"] = true,
                ["mandatoryRows"] = mandatoryRows, ["allMandatoryRowsPresent"] = true,
                ["optionalRowsDeclared"] = optionalRows, ["optionalRowsCreated"] = optionalCreated,
                ["probabilitySelectionOwnedByNative"] = true,
                ["targets"] = observations, ["commands"] = records, ["recordBytes"] = recordBytes,
                ["recordBytesAreHeapBytes"] = false, ["bufferCommands"] = 0, ["removeCommands"] = 0,
                ["externalTargetCommands"] = 0, ["commandEntityReferencesClosed"] = true,
                ["playbackCalled"] = false, ["sourceChildrenMaterialized"] = false,
                ["ownerSubObjectOutputBuffersObserved"] = false, ["completePreviewObserved"] = false,
                ["generationNoOpProven"] = false, ["nativeEligibilityEstablished"] = false,
                ["qualificationObserved"] = false, ["feesChargedObserved"] = false,
                ["scope"] = "Complete unplayed scalar commands only; no Source children, owner reference graph, complete preview, qualification or fees."
            };
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > MaximumOutputBytes)
                throw new InvalidOperationException("SUBOBJECT_GENERATION_COMMAND_OUTPUT_BUDGET");
            // Nothing from the debugger, its native memory, or the JSON is used
            // by this handoff. All admitted SDK structs have already been copied.
            payload = new GeneratedSubObjectsPayload(input.Source, input.Root, input.Node, input.Actor, immutable, commands.Length, input);
            return result;
        }

        internal static JObject CaptureRelocation(SubObjectGenerationProducer producer, SubObjectGenerationPreflight input,
            out GeneratedSubObjectsPayload? payload)
        {
            payload = null;
            if (!MainThreadDispatcher.isMainThread || producer == null || input == null || !input.Passed || !input.IsRelocation
                || !input.Source.IsCreated || !ReferenceEquals(producer.World, input.Source)
                || !producer.CompletedFor(input) || producer.PendingCount != 2 || input.Actor <= 0
                || input.Root == Entity.Null || input.OldNode == Entity.Null || input.Node == Entity.Null
                || input.Root == input.OldNode || input.Root == input.Node || input.OldNode == input.Node
                || input.Candidates.Count != 6 || input.OriginalChildren.Count != 6
                || input.CreateUpperBound != 6 || input.CommandUpperBound != 57
                || producer.CreateUpperBound != input.CreateUpperBound
                || producer.OutputCommandUpperBound != input.CommandUpperBound) throw Failure();

            var abi = new ObjectDefinitionCommandProbe.Abi();
            Array first = ReadCommands(abi, producer.BorrowCompletedForInspection(0));
            if (first.Length != 0) throw Failure();
            Array commands = ReadCommands(abi, producer.BorrowCompletedForInspection(1));
            if (commands.Length > input.CommandUpperBound) throw Failure();

            // Only the second stream may create entities. The shared reader
            // rejects all positive targets and any non-scalar write.
            List<Target> created = ReadTargets(abi, commands, input, maximumSortKey: 2, records: null, out long recordBytes);
            var counts = new int[3];
            var sortKeys = new[] { -1, -1, -1 };
            var sortOwners = new Entity[3];
            var ownerPrefabCounts = new Dictionary<(Entity Owner, Entity Prefab, int? Aligned), int>();
            var usedOriginals = new HashSet<Entity>();
            var unmatched = new List<(Entity Parent, Entity Prefab, int? Aligned)>();
            var observations = new JArray();
            var immutable = new List<GeneratedSubObjectTarget>(created.Count);
            int writes = 0;

            foreach (Target target in created)
            {
                if (!target.Version.HasValue) throw Failure();
                Entity owner = Get<Owner>(target, GeneratedSubObjectWriteKind.Owner).m_Owner;
                Entity prefab = Get<PrefabRef>(target, GeneratedSubObjectWriteKind.Prefab).m_Prefab;
                int role = owner == input.Root ? 0 : owner == input.OldNode ? 1 : owner == input.Node ? 2 : -1;
                if (role < 0 || ++counts[role] > (role == 0 ? 4 : 1)
                    || sortKeys[role] >= 0 && sortKeys[role] != target.SortKey
                    || sortOwners[target.SortKey] != Entity.Null && sortOwners[target.SortKey] != owner) throw Failure();
                sortKeys[role] = target.SortKey; sortOwners[target.SortKey] = owner;
                List<int> candidates = RequireTargetWrites(input, target, owner, prefab,
                    Get<NativeTransform>(target, GeneratedSubObjectWriteKind.Transform), out int? aligned);
                var key = (owner, prefab, aligned);
                ownerPrefabCounts.TryGetValue(key, out int observed);
                if (++observed > candidates.Count) throw Failure();
                ownerPrefabCounts[key] = observed;

                var temp = Get<Temp>(target, GeneratedSubObjectWriteKind.Temp);
                var seed = Get<PseudoRandomSeed>(target, GeneratedSubObjectWriteKind.RandomSeed);
                Entity originalParent = input.OwnerTemp(owner).m_Original;
                SubObjectGenerationPreflight.OriginalChild? matched = null;
                if (temp.m_Original == Entity.Null)
                {
                    unmatched.Add((originalParent, prefab, aligned));
                }
                else
                {
                    foreach (var child in input.OriginalChildren)
                        if (child.Entity == temp.m_Original && MatchesOriginal(child, originalParent, prefab, aligned))
                        { matched = child; break; }
                    if (!matched.HasValue || !usedOriginals.Add(temp.m_Original)) throw Failure();
                    if (matched.Value.Seed is PseudoRandomSeed originalSeed && !originalSeed.Equals(seed)) throw Failure();
                }

                JToken lightState = JValue.CreateNull();
                if (target.Types.Contains(typeof(Game.Objects.StreetLight)))
                {
                    if (role != 0) throw Failure();
                    var light = Get<Game.Objects.StreetLight>(target, GeneratedSubObjectWriteKind.StreetLight);
                    var expected = matched?.StreetLight ?? default;
                    Game.Simulation.StreetLightSystem.UpdateStreetLightState(ref expected, input.RootBuilding);
                    if (!light.Equals(expected)) throw Failure();
                    lightState = new JValue((uint)light.m_State);
                }

                var pose = Get<NativeTransform>(target, GeneratedSubObjectWriteKind.Transform);
                var elevation = Get<ObjectElevation>(target, GeneratedSubObjectWriteKind.Elevation);
                var candidateRows = new JArray();
                foreach (int row in candidates) candidateRows.Add(row);
                writes += target.Writes.Count;
                var deferred = new Entity { Index = target.Identity, Version = target.Version.Value };
                immutable.Add(new GeneratedSubObjectTarget(deferred, owner, prefab, target.Archetype,
                    target.CreateOrdinal, candidates, target.Writes));
                observations.Add(new JObject {
                    ["role"] = role == 0 ? "root" : role == 1 ? "oldNode" : "newNode",
                    ["owner"] = Id(owner), ["prefab"] = Id(prefab),
                    ["deferredEntity"] = Id(deferred),
                    ["original"] = Id(temp.m_Original), ["originalParent"] = Id(originalParent),
                    ["flags"] = (uint)temp.m_Flags, ["candidateRows"] = candidateRows, ["alignedIndex"] = aligned,
                    ["position"] = Vec(pose.m_Position), ["rotation"] = Vec(pose.m_Rotation.value),
                    ["elevation"] = elevation.m_Elevation, ["seed"] = seed.m_Seed, ["streetLightState"] = lightState,
                    ["createOrdinal"] = target.CreateOrdinal, ["sortKey"] = target.SortKey
                });
            }

            // A null Original is valid when native matching had no unused
            // eligible exact parent/prefab/slot. Do not pick a nearby entity.
            foreach (var row in unmatched)
                foreach (var child in input.OriginalChildren)
                    if (!usedOriginals.Contains(child.Entity) && MatchesOriginal(child, row.Parent, row.Prefab, row.Aligned))
                        throw Failure();

            if (writes + created.Count != commands.Length) throw Failure();
            var unused = new JArray();
            foreach (var child in input.OriginalChildren)
                if (!usedOriginals.Contains(child.Entity))
                    unused.Add(new JObject { ["entity"] = Id(child.Entity), ["parent"] = Id(child.Parent),
                        ["prefab"] = Id(child.Prefab), ["eligible"] = child.Eligible });
            var result = new JObject {
                ["kind"] = "native-relocation-subobject-generation-scalar-commands",
                ["firstBufferCommandsObserved"] = first.Length, ["commandsObserved"] = commands.Length,
                ["commandUpperBound"] = input.CommandUpperBound, ["createUpperBound"] = input.CreateUpperBound,
                ["createCommands"] = created.Count, ["componentWriteCommands"] = writes, ["recordBytes"] = recordBytes,
                ["roleCounts"] = new JObject { ["root"] = counts[0], ["oldNode"] = counts[1], ["newNode"] = counts[2] },
                ["originalMatches"] = usedOriginals.Count, ["unmatchedTargets"] = unmatched.Count, ["unusedOriginals"] = unused,
                ["declaredRowsCovered"] = counts[0] == 4 && counts[1] == 1 && counts[2] == 1,
                ["targets"] = observations, ["externalTargetCommands"] = 0, ["commandEntityReferencesClosed"] = true,
                ["playbackCalled"] = false, ["sourceChildrenMaterialized"] = false
            };
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > 8192)
                throw new InvalidOperationException("SUBOBJECT_RELOCATION_COMMAND_OUTPUT_BUDGET");
            if (counts[0] == 4 && counts[1] == 1 && counts[2] == 1)
                payload = new GeneratedSubObjectsPayload(input.Source, input.Root, input.Node, input.Actor,
                    immutable, commands.Length, input);
            return result;
        }

        internal static bool MatchesOriginal(SubObjectGenerationPreflight.OriginalChild child, Entity parent, Entity prefab, int? aligned) =>
            parent != Entity.Null && child.Eligible && child.Parent == parent && child.Prefab == prefab
                && (!aligned.HasValue || child.Aligned?.m_SubObjectIndex == aligned.Value);

        private static List<Target> ReadTargets(ObjectDefinitionCommandProbe.Abi abi, Array commands,
            SubObjectGenerationPreflight input, int maximumSortKey, JArray? records, out long recordBytes)
        {
            var targets = new Dictionary<int, Target>();
            var created = new List<Target>(input.CreateUpperBound);
            int? bufferVersion = null;
            recordBytes = 0;
            for (int ordinal = 0; ordinal < commands.Length; ordinal++)
            {
                object command = commands.GetValue(ordinal)!;
                int size = abi.Int(command, "TotalSizeInBytes"), sortKey = abi.Int(command, "SortKey");
                if (size <= 0 || size > MaximumComponentBytes + 1024 || sortKey < 0 || sortKey > maximumSortKey) throw Failure();
                recordBytes = checked(recordBytes + size);
                string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                Type view = command.GetType();
                if (kind == "CreateEntity" && view == abi.Create)
                {
                    int identity = abi.Int(command, "EntityIdentityIndex");
                    EntityArchetype archetype = (EntityArchetype)abi.Value(command, "EntityArchetype", typeof(EntityArchetype));
                    if (identity >= 0 || abi.Int(command, "BatchCount") != 1 || targets.ContainsKey(identity)
                        || created.Count >= input.CreateUpperBound || !archetype.Valid || archetype.TypesCount <= 0
                        || archetype.TypesCount > 256 || !KnownArchetype(input, archetype)) throw Failure();
                    var target = new Target(identity, archetype, ordinal, sortKey);
                    targets.Add(identity, target); created.Add(target);
                    records?.Add(new JObject { ["ordinal"] = ordinal, ["kind"] = kind, ["identity"] = identity,
                        ["sortKey"] = sortKey, ["recordBytes"] = size });
                    continue;
                }

                // No remove, enabled bit, buffer, append, bulk, query, or external
                // target can be silently treated as a closed scalar operation.
                if (view != abi.Component || (kind != "AddComponent" && kind != "SetComponent")
                    || abi.Int(command, "BatchCount") != 1 || abi.Int(command, "IdentityIndex") != 0) throw Failure();
                Entity entity = (Entity)abi.Value(command, "Entity", typeof(Entity));
                if (entity.Index >= 0 || !targets.TryGetValue(entity.Index, out Target targetRow)
                    || targetRow.SortKey != sortKey || (bufferVersion.HasValue && bufferVersion.Value != entity.Version)) throw Failure();
                bufferVersion = entity.Version;
                if (targetRow.Version.HasValue && targetRow.Version.Value != entity.Version) throw Failure();
                targetRow.Version = entity.Version;

                TypeIndex index = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                Type type = TypeManager.GetType(index);
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                short bytes = (short)abi.Value(command, "ComponentSize", typeof(short));
                if (!index.IsComponentType || index.IsManagedType || index.IsSharedComponentType || index.IsBuffer
                    || index.IsChunkComponent || index.HasNativeContainer || index.IsCleanupComponent
                    || index.IsCleanupBufferComponent || index.IsCleanupSharedComponent
                    || info.Category != TypeManager.TypeCategory.ComponentData || info.SizeInChunk <= 0
                    || info.SizeInChunk > MaximumComponentBytes || bytes != info.SizeInChunk
                    || info.HasBlobAssetRefs || info.HasWeakAssetRefs || info.HasUnityObjectRefs
                    || (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))) throw Failure();
                object? value = abi.Value(command, "ComponentValue", typeof(object));
                if (value == null || value.GetType() != type || !targetRow.Types.Add(type)) throw Failure();
                GeneratedSubObjectWrite write = CaptureWrite(type, kind, value, bytes, ordinal, input);
                int expectedReferences = type == typeof(Owner) || type == typeof(PrefabRef) || type == typeof(Temp) ? 1 : 0;
                if (info.EntityOffsetCount != expectedReferences) throw Failure();
                targetRow.Writes.Add(write);
                records?.Add(new JObject { ["ordinal"] = ordinal, ["kind"] = kind, ["identity"] = entity.Index,
                    ["write"] = write.Kind.ToString(), ["sortKey"] = sortKey, ["recordBytes"] = size,
                    ["componentBytes"] = bytes });
            }
            return created;
        }

        private static Array ReadCommands(ObjectDefinitionCommandProbe.Abi abi, EntityCommandBuffer buffer)
        {
            object proxy = abi.Constructor.Invoke(new object[] { buffer });
            return (Array)abi.Commands.GetValue(proxy)!;
        }

        private static bool KnownArchetype(SubObjectGenerationPreflight input, EntityArchetype archetype)
        {
            foreach (var candidate in input.Candidates) if (candidate.Archetype.Equals(archetype)) return true;
            return false;
        }

        private static GeneratedSubObjectWrite CaptureWrite(Type type, string command, object value, int bytes, int ordinal,
            SubObjectGenerationPreflight input)
        {
            GeneratedSubObjectWriteKind kind;
            if (type == typeof(Owner) && command == "AddComponent" && value is Owner owner
                && (owner.m_Owner == input.Root || owner.m_Owner == input.Node
                    || input.IsRelocation && owner.m_Owner == input.OldNode)) kind = GeneratedSubObjectWriteKind.Owner;
            else if (type == typeof(PrefabRef) && command == "SetComponent" && value is PrefabRef prefab
                && prefab.m_Prefab != Entity.Null && prefab.m_Prefab.Index >= 0) kind = GeneratedSubObjectWriteKind.Prefab;
            else if (type == typeof(NativeTransform) && command == "SetComponent" && value is NativeTransform transform
                && Finite(transform.m_Position) && Finite(transform.m_Rotation.value)) kind = GeneratedSubObjectWriteKind.Transform;
            else if (type == typeof(Temp) && command == "AddComponent" && value is Temp temp
                && (temp.m_Original == Entity.Null || input.IsRelocation && temp.m_Original.Index >= 0 && temp.m_Original.Version > 0)
                && temp.m_CurvePosition == 0f && temp.m_Value == 0 && temp.m_Cost == 0)
                kind = GeneratedSubObjectWriteKind.Temp;
            else if (type == typeof(NativeAnimation) && command == "AddComponent" && value is NativeAnimation animation
                && animation.Equals(default(NativeAnimation))) kind = GeneratedSubObjectWriteKind.Animation;
            else if (type == typeof(InterpolatedTransform) && command == "AddComponent" && value is InterpolatedTransform interpolated
                && interpolated.Equals(default(InterpolatedTransform))) kind = GeneratedSubObjectWriteKind.Interpolated;
            else if (type == typeof(PseudoRandomSeed) && command == "SetComponent" && value is PseudoRandomSeed)
                kind = GeneratedSubObjectWriteKind.RandomSeed;
            else if (type == typeof(ObjectElevation) && command == "AddComponent" && value is ObjectElevation elevation
                && math.isfinite(elevation.m_Elevation) && elevation.m_Flags == 0) kind = GeneratedSubObjectWriteKind.Elevation;
            else if (type == typeof(Game.Objects.Aligned) && command == "AddComponent" && value is Game.Objects.Aligned)
                kind = GeneratedSubObjectWriteKind.Aligned;
            else if (type == typeof(Game.Objects.StreetLight) && command == "SetComponent" && value is Game.Objects.StreetLight)
                kind = GeneratedSubObjectWriteKind.StreetLight;
            else throw Failure();
            return new GeneratedSubObjectWrite(kind, value, bytes, ordinal, input.CommandUpperBound);
        }

        private static GeneratedSubObjectTarget FinishTarget(SubObjectGenerationPreflight input, Target target, out JObject observation)
        {
            Owner owner = Get<Owner>(target, GeneratedSubObjectWriteKind.Owner);
            PrefabRef prefab = Get<PrefabRef>(target, GeneratedSubObjectWriteKind.Prefab);
            NativeTransform transform = Get<NativeTransform>(target, GeneratedSubObjectWriteKind.Transform);
            List<int> candidates = RequireTargetWrites(input, target, owner.m_Owner, prefab.m_Prefab, transform, out int? alignedIndex);
            Temp actualTemp = Get<Temp>(target, GeneratedSubObjectWriteKind.Temp);
            bool light = target.Types.Contains(typeof(Game.Objects.StreetLight));
            var entity = new Entity { Index = target.Identity, Version = target.Version!.Value };
            var copy = new GeneratedSubObjectTarget(entity, owner.m_Owner, prefab.m_Prefab, target.Archetype,
                target.CreateOrdinal, candidates, target.Writes);
            NativeAnimation animation = Get<NativeAnimation>(target, GeneratedSubObjectWriteKind.Animation);
            InterpolatedTransform interpolated = Get<InterpolatedTransform>(target, GeneratedSubObjectWriteKind.Interpolated);
            ObjectElevation elevation = Get<ObjectElevation>(target, GeneratedSubObjectWriteKind.Elevation);
            var rows = new JArray(); foreach (int row in candidates) rows.Add(row);
            observation = new JObject
            {
                ["deferredEntity"] = Id(entity), ["owner"] = Id(owner.m_Owner), ["prefab"] = Id(prefab.m_Prefab),
                ["sourceArchetypeMatched"] = true, ["candidateRows"] = rows,
                ["sourceRow"] = copy.SourceRowIndex.HasValue ? new JValue(copy.SourceRowIndex.Value) : JValue.CreateNull(),
                ["transform"] = new JObject { ["position"] = Vec(transform.m_Position), ["rotation"] = Vec(transform.m_Rotation.value) },
                ["temp"] = new JObject { ["original"] = Id(actualTemp.m_Original), ["flags"] = (uint)actualTemp.m_Flags,
                    ["curvePosition"] = actualTemp.m_CurvePosition, ["value"] = actualTemp.m_Value, ["cost"] = actualTemp.m_Cost },
                ["animation"] = new JObject { ["targetPosition"] = Vec(animation.m_TargetPosition), ["position"] = Vec(animation.m_Position),
                    ["rotation"] = Vec(animation.m_Rotation.value), ["swayPivot"] = Vec(animation.m_SwayPivot),
                    ["swayPosition"] = Vec(animation.m_SwayPosition), ["swayVelocity"] = Vec(animation.m_SwayVelocity), ["pushFactor"] = animation.m_PushFactor },
                ["interpolatedTransform"] = new JObject { ["position"] = Vec(interpolated.m_Position),
                    ["rotation"] = Vec(interpolated.m_Rotation.value), ["flags"] = (uint)interpolated.m_Flags },
                ["randomSeed"] = Get<PseudoRandomSeed>(target, GeneratedSubObjectWriteKind.RandomSeed).m_Seed,
                ["elevation"] = new JObject { ["value"] = elevation.m_Elevation, ["flags"] = (uint)elevation.m_Flags },
                ["alignedIndex"] = alignedIndex.HasValue ? new JValue(alignedIndex.Value) : JValue.CreateNull(),
                ["streetLightState"] = light ? new JValue((uint)Get<Game.Objects.StreetLight>(target, GeneratedSubObjectWriteKind.StreetLight).m_State) : JValue.CreateNull()
            };
            return copy;
        }

        private static List<int> RequireTargetWrites(SubObjectGenerationPreflight input, Target target,
            Entity owner, Entity prefab, NativeTransform transform, out int? alignedIndex)
        {
            const int required = (1 << 8) - 1;
            int mask = 0;
            foreach (GeneratedSubObjectWrite write in target.Writes) mask |= 1 << (int)write.Kind;
            if ((mask & required) != required || target.Writes.Count < 8 || target.Writes.Count > 10) throw Failure();

            var leaf = input.GetLeaf(prefab);
            if (!leaf.Archetype.Equals(target.Archetype)) throw Failure();
            foreach (GeneratedSubObjectWrite write in target.Writes)
            {
                if (write.IsSet && !leaf.Types.ContainsKey(TypeManager.GetTypeIndex(write.Value.GetType()))) throw Failure();
            }
            Temp actualTemp = Get<Temp>(target, GeneratedSubObjectWriteKind.Temp);
            if (actualTemp.m_Flags != ChildTempFlags(input.OwnerTemp(owner))) throw Failure();
            bool aligned = (mask & (1 << (int)GeneratedSubObjectWriteKind.Aligned)) != 0;
            bool light = (mask & (1 << (int)GeneratedSubObjectWriteKind.StreetLight)) != 0;
            if (aligned != (owner == input.Node || input.IsRelocation && owner == input.OldNode)
                || light != leaf.StreetLight.HasValue) throw Failure();
            alignedIndex = aligned ? Get<Game.Objects.Aligned>(target, GeneratedSubObjectWriteKind.Aligned).m_SubObjectIndex : (int?)null;
            List<int> candidates = MatchingRows(input, owner, prefab, alignedIndex, transform);
            if (candidates.Count == 0) throw Failure();
            return candidates;
        }

        internal static TempFlags ChildTempFlags(Temp parent)
        {
            const TempFlags copied = TempFlags.Create | TempFlags.Delete | TempFlags.Dragging | TempFlags.Select
                | TempFlags.Modify | TempFlags.Hidden | TempFlags.Duplicate;
            TempFlags flags = parent.m_Flags & copied;
            if ((parent.m_Flags & TempFlags.Replace) != 0) flags |= TempFlags.Modify;
            return flags;
        }

        private static List<int> MatchingRows(SubObjectGenerationPreflight input, Entity owner, Entity prefab,
            int? alignedIndex, NativeTransform actualTransform)
        {
            var result = new List<int>();
            foreach (var candidate in input.Candidates)
            {
                if (candidate.Owner != owner || candidate.Prefab != prefab
                    || alignedIndex.HasValue && candidate.RowIndex != alignedIndex.Value) continue;
                if (!input.IsRelocation)
                {
                    NativeTransform parent = owner == input.Root ? input.RootTransform :
                        new NativeTransform(input.NodeData.m_Position, input.NodeData.m_Rotation);
                    NativeTransform expected = Game.Objects.ObjectUtils.LocalToWorld(parent,
                        new NativeTransform(candidate.Position, candidate.Rotation));
                    if (!expected.Equals(actualTransform)) continue;
                }
                result.Add(candidate.RowIndex);
            }
            return result;
        }

        private static T Get<T>(Target target, GeneratedSubObjectWriteKind kind) where T : struct
        {
            foreach (GeneratedSubObjectWrite write in target.Writes)
                if (write.Kind == kind && write.Value is T value) return value;
            throw Failure();
        }
        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static bool Finite(float4 value) => math.all(math.isfinite(value));
        private static JArray Vec(float3 value) => new JArray(value.x, value.y, value.z);
        private static JArray Vec(float4 value) => new JArray(value.x, value.y, value.z, value.w);
        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static InvalidOperationException Failure() => new InvalidOperationException("SUBOBJECT_GENERATION_COMMANDS_UNQUALIFIED");

        private sealed class Target
        {
            internal readonly int Identity, CreateOrdinal, SortKey;
            internal readonly EntityArchetype Archetype;
            internal readonly HashSet<Type> Types = new HashSet<Type>();
            internal readonly List<GeneratedSubObjectWrite> Writes = new List<GeneratedSubObjectWrite>(10);
            internal int? Version;
            internal Target(int identity, EntityArchetype archetype, int ordinal, int sortKey)
            { Identity = identity; Archetype = archetype; CreateOrdinal = ordinal; SortKey = sortKey; }
        }
    }

    internal enum GeneratedSubObjectWriteKind
    { Owner, Prefab, Transform, Temp, Animation, Interpolated, RandomSeed, Elevation, Aligned, StreetLight }

    // All values are the actual fixed unmanaged SDK structs copied by the debug
    // view. Ordinals preserve interleaving without retaining a native ECB alias.
    internal readonly struct GeneratedSubObjectWrite
    {
        internal readonly GeneratedSubObjectWriteKind Kind;
        internal readonly object Value;
        internal readonly int ComponentBytes, Ordinal;
        internal bool IsSet => Kind == GeneratedSubObjectWriteKind.Prefab || Kind == GeneratedSubObjectWriteKind.Transform
            || Kind == GeneratedSubObjectWriteKind.RandomSeed || Kind == GeneratedSubObjectWriteKind.StreetLight;
        internal GeneratedSubObjectWrite(GeneratedSubObjectWriteKind kind, object value, int bytes, int ordinal, int commandUpperBound)
        {
            if (bytes <= 0 || bytes > 1024 || commandUpperBound > SubObjectGenerationPreflight.MaximumNormalCommands
                || ordinal < 0 || ordinal >= commandUpperBound)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_WRITE_INVALID");
            Kind = kind; ComponentBytes = bytes; Ordinal = ordinal;
            // Explicit unbox/rebox makes an independent unmanaged-value copy,
            // rather than retaining even the debugger's boxed component object.
            switch (kind)
            {
                case GeneratedSubObjectWriteKind.Owner: Value = value is Owner owner ? (object)owner : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Prefab: Value = value is PrefabRef prefab ? (object)prefab : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Transform: Value = value is NativeTransform transform ? (object)transform : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Temp: Value = value is Temp temp ? (object)temp : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Animation: Value = value is NativeAnimation animation ? (object)animation : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Interpolated: Value = value is InterpolatedTransform interpolated ? (object)interpolated : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.RandomSeed: Value = value is PseudoRandomSeed seed ? (object)seed : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Elevation: Value = value is ObjectElevation elevation ? (object)elevation : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.Aligned: Value = value is Game.Objects.Aligned aligned ? (object)aligned : throw InvalidValue(); break;
                case GeneratedSubObjectWriteKind.StreetLight: Value = value is Game.Objects.StreetLight light ? (object)light : throw InvalidValue(); break;
                default: throw InvalidValue();
            }
        }
        private static InvalidOperationException InvalidValue() => new InvalidOperationException("GENERATED_SUBOBJECT_WRITE_VALUE_INVALID");
    }

    internal sealed class GeneratedSubObjectTarget
    {
        internal readonly Entity DeferredEntity, Owner, Prefab;
        internal readonly EntityArchetype SourceArchetype;
        internal readonly int CreateOrdinal;
        internal readonly int? SourceRowIndex;
        private readonly int[] candidateRows;
        private readonly GeneratedSubObjectWrite[] writes;
        internal int CandidateRowCount => candidateRows.Length;
        internal int CandidateRowAt(int index) => candidateRows[index];
        internal int WriteCount => writes.Length;
        internal GeneratedSubObjectWrite WriteAt(int index) => writes[index];
        internal bool Has(GeneratedSubObjectWriteKind kind)
        { foreach (GeneratedSubObjectWrite write in writes) if (write.Kind == kind) return true; return false; }
        internal GeneratedSubObjectTarget(Entity deferred, Entity owner, Entity prefab, EntityArchetype archetype,
            int ordinal, List<int> rows, List<GeneratedSubObjectWrite> values)
        {
            if (deferred.Index >= 0 || owner == Entity.Null || prefab == Entity.Null || !archetype.Valid
                || rows == null || rows.Count == 0 || rows.Count > 5 || values == null || values.Count < 8 || values.Count > 10)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_TARGET_INVALID");
            int mask = 0, previousOrdinal = ordinal;
            foreach (GeneratedSubObjectWrite write in values)
            {
                int bit = 1 << (int)write.Kind;
                if ((mask & bit) != 0 || write.Value == null || write.ComponentBytes <= 0 || write.Ordinal <= previousOrdinal)
                    throw new InvalidOperationException("GENERATED_SUBOBJECT_WRITE_INVALID");
                mask |= bit; previousOrdinal = write.Ordinal;
            }
            if ((mask & 255) != 255) throw new InvalidOperationException("GENERATED_SUBOBJECT_WRITES_INCOMPLETE");
            var uniqueRows = new HashSet<int>();
            foreach (int row in rows) if (row < 0 || !uniqueRows.Add(row)) throw new InvalidOperationException("GENERATED_SUBOBJECT_SOURCE_ROWS_INVALID");
            DeferredEntity = deferred; Owner = owner; Prefab = prefab; SourceArchetype = archetype; CreateOrdinal = ordinal;
            candidateRows = rows.ToArray(); writes = values.ToArray(); SourceRowIndex = rows.Count == 1 ? rows[0] : (int?)null;
        }
    }

    internal sealed class GeneratedSubObjectsPayload
    {
        internal readonly World Source;
        internal readonly Entity Root, Node, OldNode;
        internal readonly int Actor, CommandCount;
        private readonly SubObjectGenerationPreflight? relocation;
        private readonly Temp rootTemp, nodeTemp, oldNodeTemp;
        private readonly GeneratedSubObjectTarget[] targets;
        internal bool IsRelocation => relocation != null;
        internal int TargetCount => targets.Length;
        internal int RootChildCount { get; }
        internal int NodeChildCount { get; }
        internal GeneratedSubObjectTarget TargetAt(int index) => targets[index];
        internal int ChildCount(Entity owner) => owner == Root ? RootChildCount : owner == Node ? NodeChildCount
            : IsRelocation && owner == OldNode ? TargetCount - RootChildCount - NodeChildCount
            : throw new InvalidOperationException("GENERATED_SUBOBJECT_OWNER_UNAVAILABLE");
        internal GeneratedSubObjectsPayload(World source, Entity root, Entity node, int actor,
            List<GeneratedSubObjectTarget> values, int commandCount, SubObjectGenerationPreflight admission)
        {
            if (source == null || root == Entity.Null || node == Entity.Null || root == node || actor <= 0
                || values == null || commandCount < 0 || !admission.Passed || !ReferenceEquals(source, admission.Source)
                || admission.Root != root || admission.Node != node || admission.Actor != actor
                || values.Count > admission.CreateUpperBound
                || admission.IsRelocation && values.Count != admission.CreateUpperBound
                || commandCount > admission.CommandUpperBound)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_PAYLOAD_INVALID");
            relocation = admission.IsRelocation ? admission : null;
            Source = source; Root = root; Node = node; OldNode = relocation?.OldNode ?? Entity.Null;
            Actor = actor; CommandCount = commandCount;
            if (relocation != null)
            {
                rootTemp = relocation.OwnerTemp(root);
                nodeTemp = relocation.OwnerTemp(node);
                oldNodeTemp = relocation.OwnerTemp(OldNode);
            }
            var identities = new HashSet<Entity>(); var ordinals = new HashSet<int>(); int commands = values.Count;
            int rootChildren = 0, nodeChildren = 0;
            foreach (GeneratedSubObjectTarget target in values)
            {
                if (!IsParent(target.Owner) || !identities.Add(target.DeferredEntity)
                    || target.CreateOrdinal < 0 || target.CreateOrdinal >= commandCount || !ordinals.Add(target.CreateOrdinal))
                    throw new InvalidOperationException("GENERATED_SUBOBJECT_IDENTITY_INVALID");
                for (int index = 0; index < target.WriteCount; index++)
                {
                    int ordinal = target.WriteAt(index).Ordinal;
                    if (ordinal < 0 || ordinal >= commandCount || !ordinals.Add(ordinal))
                        throw new InvalidOperationException("GENERATED_SUBOBJECT_COMMAND_ORDER_INVALID");
                }
                if (target.Owner == Root) rootChildren++;
                if (target.Owner == Node) nodeChildren++;
                commands += target.WriteCount;
            }
            if (commands != commandCount || ordinals.Count != commandCount) throw new InvalidOperationException("GENERATED_SUBOBJECT_COMMANDS_INCOMPLETE");
            if (!IsRelocation && nodeChildren != SubObjectGenerationPreflight.NormalNodeRows)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_NODE_CHILDREN_INCOMPLETE");
            RootChildCount = rootChildren; NodeChildCount = nodeChildren;
            targets = values.ToArray();
        }

        internal bool IsParent(Entity entity) => entity == Root || entity == Node || IsRelocation && entity == OldNode;

        internal bool ParentTempMatches(Entity parent, Temp value) => relocation == null
            ? value.m_Original == Entity.Null
            : value.Equals(CapturedParentTemp(parent));

        internal bool ChildTempMatches(Entity parent, Entity prefab, int? aligned, Temp value)
        {
            if (value.m_CurvePosition != 0f || value.m_Cost != 0 || value.m_Value != 0) return false;
            if (relocation == null) return value.m_Original == Entity.Null && value.m_Flags == TempFlags.Create;
            Temp parentTemp = CapturedParentTemp(parent);
            if (value.m_Flags != SubObjectGenerationCommandProbe.ChildTempFlags(parentTemp)) return false;
            if (value.m_Original == Entity.Null) return true;
            foreach (var child in relocation.OriginalChildren)
                if (child.Entity == value.m_Original
                    && SubObjectGenerationCommandProbe.MatchesOriginal(child, parentTemp.m_Original, prefab, aligned)) return true;
            return false;
        }

        internal Temp CapturedParentTemp(Entity parent) => parent == Root ? rootTemp : parent == Node ? nodeTemp
            : parent == OldNode ? oldNodeTemp : throw new InvalidOperationException("GENERATED_SUBOBJECT_PARENT_UNKNOWN");

        internal void RequireRelocationWitness(Action scope) => relocation?.RequireRelocationWitness(scope);
    }
}
#endif
