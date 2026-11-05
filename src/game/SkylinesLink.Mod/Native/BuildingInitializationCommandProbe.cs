#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Reflection;
using Colossal.Core;
using Game.Buildings;
using Game.Net;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Complete, bounded observation of the owned initialization ECBs.</summary>
    internal static class BuildingInitializationCommandProbe
    {
        // Capture and the later Verify are enclosed by the caller's current
        // Source-scope guard. Neither method performs a structural write.
        internal static JObject Capture(BuildingInitializationProducer producer, Entity root,
            out BuildingInitializationOutput? expected)
        {
            expected = null;
            if (!MainThreadDispatcher.isMainThread || producer == null || root == Entity.Null || root.Index < 0 || !producer.CompletedFor(root)
                || producer.World == null || !producer.World.IsCreated) throw Failure();
            int bound = producer.OutputCommandUpperBound, pending = producer.PendingCount;
            int coverageIndex = producer.CoverageBufferIndex, buildingIndex = producer.BuildingBufferIndex;
            int coverageCount = producer.CoverageCount, buildingCount = producer.BuildingCount;
            if (bound < 0 || bound > 5 || pending < 0 || pending > 2
                || coverageCount < 0 || coverageCount > 1 || buildingCount < 0 || buildingCount > 1
                || producer.CoverageChunkCount < 0 || producer.CoverageChunkCount > 1
                || producer.BuildingChunkCount < 0 || producer.BuildingChunkCount > 1
                || coverageIndex < -1 || coverageIndex > 0 || buildingIndex < -1 || buildingIndex > 1
                || pending != (coverageIndex >= 0 ? 1 : 0) + (buildingIndex >= 0 ? 1 : 0)
                || buildingIndex >= 0 && buildingIndex != (coverageIndex >= 0 ? 1 : 0)
                || coverageCount > 0 && coverageIndex < 0 || buildingCount > 0 && buildingIndex < 0) throw Failure();

            var abi = new ObjectDefinitionCommandProbe.Abi();
            Type typeSetView = Nested("EntityMultipleComponentsCommandView");
            Type sharedView = Nested("EntityUnmanagedSharedComponentCommandView");
            int adds = 0, total = 0, buildingCommands = 0;
            bool removed = false;
            CoverageServiceType? coverage = null;
            long recordBytes = 0;
            var buffers = new JArray();
            for (int index = 0; index < pending; index++)
            {
                bool coverageBuffer = index == coverageIndex;
                if (!coverageBuffer && index != buildingIndex) throw Failure();
                // The producer proves the native per-root work bound and these
                // buffer roles before any complete debugger walk. An ECB can
                // legitimately be empty despite IsEmptyIgnoreFilter being false.
                EntityCommandBuffer buffer = producer.BorrowCompletedForInspection(index);
                object proxy = abi.Constructor.Invoke(new object[] { buffer });
                var commands = (Array)abi.Commands.GetValue(proxy)!;
                if (commands.Length > (coverageBuffer ? 1 : 4) || total + commands.Length > bound) throw Failure();
                var records = new JArray();
                foreach (object command in commands)
                {
                    int bytes = abi.Int(command, "TotalSizeInBytes"), sortKey = abi.Int(command, "SortKey");
                    if (bytes <= 0 || bytes > 2048 || sortKey < 0
                        || abi.Int(command, "BatchCount") != 1 || abi.Int(command, "IdentityIndex") != 0
                        || (Entity)abi.Value(command, "Entity", typeof(Entity)) != root) throw Failure();
                    recordBytes = checked(recordBytes + bytes);
                    string kind = Enum.GetName(abi.CommandType.FieldType, abi.CommandType.GetValue(command)!) ?? "";
                    Type view = command.GetType();
                    var record = new JObject { ["kind"] = kind, ["recordBytes"] = bytes, ["sortKey"] = sortKey };
                    if (coverageBuffer)
                    {
                        if (view != sharedView || kind != "SetUnmanagedSharedComponentData" || coverage.HasValue) throw Failure();
                        TypeIndex type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                        ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
                        if (type != TypeManager.GetTypeIndex<CoverageServiceType>() || !type.IsSharedComponentType
                            || type.IsManagedType || type.IsBuffer || type.IsChunkComponent || type.HasNativeContainer
                            || info.EntityOffsetCount != 0 || info.TypeSize <= 0 || info.TypeSize > 64
                            || (short)abi.Value(command, "ComponentSize", typeof(short)) != info.TypeSize
                            || (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))
                            || !(abi.Value(command, "ComponentValue", typeof(object)) is CoverageServiceType value)) throw Failure();
                        coverage = value;
                        record["component"] = typeof(CoverageServiceType).FullName;
                        record["serviceValue"] = (int)(byte)value.m_Service;
                        record["componentBytes"] = info.TypeSize;
                    }
                    else if (view == abi.Component && kind == "AddComponent")
                    {
                        TypeIndex type = (TypeIndex)abi.Value(command, "ComponentTypeIndex", typeof(TypeIndex));
                        Type actual = TypeManager.GetType(type);
                        int bit = BuildingInitializationOutput.AddBit(actual);
                        ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
                        // These are type-only additions even when the consumer
                        // has nonzero data. They do not embed a struct value.
                        if (bit == 0 || (adds & bit) != 0 || type.IsManagedType || type.IsSharedComponentType
                            || type.IsBuffer || type.IsChunkComponent || type.HasNativeContainer
                            || info.Category != TypeManager.TypeCategory.ComponentData
                            || (short)abi.Value(command, "ComponentSize", typeof(short)) != 0
                            || abi.Value(command, "ComponentValue", typeof(object)) != null
                            || (bool)abi.Value(command, "ValueRequiresEntityFixup", typeof(bool))) throw Failure();
                        adds |= bit;
                        record["component"] = actual.FullName; record["typeOnly"] = true;
                        record["componentBytes"] = 0;
                    }
                    else if (view == typeSetView && kind == "RemoveMultipleComponents")
                    {
                        if (removed) throw Failure();
                        var types = (ComponentTypeSet)abi.Value(command, "TypeSet", typeof(ComponentTypeSet));
                        if (types.Length != 4) throw Failure();
                        int mask = 0;
                        var names = new JArray();
                        for (int item = 0; item < types.Length; item++)
                        {
                            Type actual = TypeManager.GetType(types.GetTypeIndex(item));
                            int bit = BuildingInitializationOutput.AddBit(actual);
                            if (bit == 0 || bit == BuildingInitializationOutput.Crime || (mask & bit) != 0) throw Failure();
                            mask |= bit; names.Add(actual.FullName);
                        }
                        if (mask != BuildingInitializationOutput.RemovalMask) throw Failure();
                        removed = true; record["components"] = names;
                    }
                    else throw Failure();
                    records.Add(record);
                }
                total += commands.Length;
                if (!coverageBuffer) buildingCommands += commands.Length;
                buffers.Add(new JObject { ["index"] = index, ["role"] = coverageBuffer ? "coverage" : "building",
                    ["commandsObserved"] = commands.Length, ["commands"] = records });
            }
            if (coverage.HasValue != (coverageCount == 1) || buildingCount == 0 && buildingCommands != 0
                || removed && (adds & BuildingInitializationOutput.RemovalMask) != 0) throw Failure();

            // Read only the actual fixed additions before Playback. Native
            // type-only Add preserves existing values; absent data starts zero.
            var output = new BuildingInitializationOutput(producer.World, root, producer.EntityManager, adds, removed, coverage);
            var observation = new JObject {
                ["kind"] = "native-building-initialization-command-observation", ["commandUpperBound"] = bound,
                ["bufferCount"] = pending, ["commandsObserved"] = total, ["noOp"] = total == 0,
                ["target"] = Id(root), ["coverageBufferIndex"] = coverageIndex, ["buildingBufferIndex"] = buildingIndex,
                ["coverageCount"] = coverageCount, ["buildingCount"] = buildingCount,
                ["typeOnlyAdds"] = output.AddNames(), ["removedConsumerSet"] = removed,
                ["coverage"] = coverage.HasValue ? (JToken)new JObject { ["serviceValue"] = (int)(byte)coverage.Value.m_Service } : JValue.CreateNull(),
                ["commandEntityReferencesClosed"] = true, ["externalTargetCommands"] = 0,
                ["createCommands"] = 0, ["bufferCommands"] = 0, ["recordBytes"] = recordBytes,
                ["recordBytesAreHeapBytes"] = false, ["buffers"] = buffers, ["playbackCalled"] = false,
                ["scope"] = "Complete owned initialization commands only; zero output is preserved, not completed building or placement acceptance."
            };
            expected = output;
            return observation;
        }

        private static Type Nested(string name) => typeof(EntityCommandBuffer).GetNestedType(name, BindingFlags.NonPublic) ?? throw Failure();
        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static InvalidOperationException Failure() => new InvalidOperationException("BUILDING_INITIALIZATION_COMMANDS_UNQUALIFIED");
    }

    // A fixed post-Playback expectation, never a command writer or a JSON-based
    // reconstruction. Existing added-type data is captured before Playback.
    internal sealed class BuildingInitializationOutput
    {
        internal const int Crime = 1, Mail = 2, Electricity = 4, Water = 8, Garbage = 16;
        internal const int RemovalMask = Mail | Electricity | Water | Garbage;
        private readonly World source;
        private readonly Entity root;
        private readonly int adds;
        private readonly bool removed;
        private readonly CoverageServiceType? coverage;
        private readonly CrimeProducer? crimeBefore;
        private readonly MailProducer? mailBefore;
        private readonly ElectricityConsumer? electricityBefore;
        private readonly WaterConsumer? waterBefore;
        private readonly GarbageProducer? garbageBefore;

        internal BuildingInitializationOutput(World source, Entity root, EntityManager entities, int adds,
            bool removed, CoverageServiceType? coverage)
        {
            if (source == null || !source.IsCreated || !ReferenceEquals(entities.World, source) || root == Entity.Null
                || root.Index < 0 || !entities.Exists(root) || adds < 0 || (adds & ~(Crime | RemovalMask)) != 0
                || removed && (adds & RemovalMask) != 0) throw Failure();
            this.source = source; this.root = root; this.adds = adds; this.removed = removed; this.coverage = coverage;
            if ((adds & Crime) != 0 && entities.HasComponent<CrimeProducer>(root)) crimeBefore = entities.GetComponentData<CrimeProducer>(root);
            if ((adds & Mail) != 0 && entities.HasComponent<MailProducer>(root)) mailBefore = entities.GetComponentData<MailProducer>(root);
            if ((adds & Electricity) != 0 && entities.HasComponent<ElectricityConsumer>(root)) electricityBefore = entities.GetComponentData<ElectricityConsumer>(root);
            if ((adds & Water) != 0 && entities.HasComponent<WaterConsumer>(root)) waterBefore = entities.GetComponentData<WaterConsumer>(root);
            if ((adds & Garbage) != 0 && entities.HasComponent<GarbageProducer>(root)) garbageBefore = entities.GetComponentData<GarbageProducer>(root);
        }

        internal JObject Verify(EntityManager entities, Entity expectedRoot)
        {
            if (!MainThreadDispatcher.isMainThread || !source.IsCreated || !ReferenceEquals(entities.World, source)
                || expectedRoot != root || !entities.Exists(root)) throw Failure();
            var verified = new JArray();
            if ((adds & Crime) != 0) Check(entities, crimeBefore, verified);
            if ((adds & Mail) != 0) Check(entities, mailBefore, verified);
            if ((adds & Electricity) != 0) Check(entities, electricityBefore, verified);
            if ((adds & Water) != 0) Check(entities, waterBefore, verified);
            if ((adds & Garbage) != 0) Check(entities, garbageBefore, verified);
            if (removed && (entities.HasComponent<MailProducer>(root) || entities.HasComponent<ElectricityConsumer>(root)
                || entities.HasComponent<WaterConsumer>(root) || entities.HasComponent<GarbageProducer>(root))) throw Failure();
            if (coverage.HasValue && (!entities.HasComponent<CoverageServiceType>(root)
                || !entities.GetSharedComponent<CoverageServiceType>(root).Equals(coverage.Value))) throw Failure();
            return new JObject { ["target"] = new JObject { ["index"] = root.Index, ["version"] = root.Version },
                ["addedDataVerified"] = verified, ["removedConsumerSetVerified"] = removed,
                ["coverageVerified"] = coverage.HasValue, ["noOp"] = adds == 0 && !removed && !coverage.HasValue };
        }

        private void Check<T>(EntityManager entities, T? before, JArray verified) where T : unmanaged, IComponentData
        {
            if (!entities.HasComponent<T>(root) || !entities.GetComponentData<T>(root).Equals(before.GetValueOrDefault())) throw Failure();
            verified.Add(new JObject { ["component"] = typeof(T).FullName, ["presentBefore"] = before.HasValue,
                ["completeValueVerified"] = true });
        }

        internal JArray AddNames()
        {
            var names = new JArray();
            if ((adds & Crime) != 0) names.Add(typeof(CrimeProducer).FullName);
            if ((adds & Mail) != 0) names.Add(typeof(MailProducer).FullName);
            if ((adds & Electricity) != 0) names.Add(typeof(ElectricityConsumer).FullName);
            if ((adds & Water) != 0) names.Add(typeof(WaterConsumer).FullName);
            if ((adds & Garbage) != 0) names.Add(typeof(GarbageProducer).FullName);
            return names;
        }

        internal static int AddBit(Type type) => type == typeof(CrimeProducer) ? Crime : type == typeof(MailProducer) ? Mail
            : type == typeof(ElectricityConsumer) ? Electricity : type == typeof(WaterConsumer) ? Water
            : type == typeof(GarbageProducer) ? Garbage : 0;
        private static InvalidOperationException Failure() => new InvalidOperationException("BUILDING_INITIALIZATION_READBACK_UNQUALIFIED");
    }
}
#endif
