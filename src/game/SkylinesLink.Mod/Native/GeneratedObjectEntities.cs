#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using Colossal.Core;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using Animation = Game.Tools.Animation;
using EcsEntity = Unity.Entities.Entity;

namespace SkylinesLink.Mod.Native
{
    /// <summary>One Disabled, scoped materialization of the captured eight typed writes.</summary>
    internal sealed class GeneratedObjectEntities
    {
        private readonly World source;
        private readonly GeneratedObjectPayload payload;
        private readonly int actor;
        private readonly NativeEntityBatch batch;
        private readonly NativeEntityArchetypes typeSets = new NativeEntityArchetypes();
        private Action? requireScope, requireReleaseGraph;
        private readonly List<string> writeOrder = new List<string>(8);
        private JObject? readback;
        private bool createCalled, createReturned, releaseCalled, archetypeReturned, scopeReturned;
        private bool birthTypesVerified, finalTypesVerified, valuesVerified;
        private bool birthMeshEnabled, birthSimulateEnabled, birthDefaultsVerified;
        private bool birthLinkedChecked, birthLinkedEmpty, releaseLinkedChecked, releaseLinkedEmpty;
        private bool releaseSubNetChecked, releaseSubNetEmpty;
        private int writesAttempted, writesReturned;
        private int originalTypeCount, mergedTypeCount, finalTypeCount;
        private string stage = "not-started";
        private string? failureCode, exceptionType, releaseFailure;
        private string? finalTypeDigest, writeOrderDigest;

        internal GeneratedObjectEntities(World source, GeneratedObjectPayload payload, int actor)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
            if (!ReferenceEquals(source, payload.Source) || actor <= 0)
                throw new InvalidOperationException("GENERATED_OBJECT_SOURCE_REJECTED");
            this.actor = actor;
            batch = new NativeEntityBatch(source, 1);
        }

        internal EcsEntity Entity => batch.EntityAt(0);
        internal bool CreateReturned => createReturned;
        internal bool UnknownCreation => batch.UnknownCreation;
        internal bool ResourcesReleased => batch.ResourcesReleased && typeSets.ResourcesReleased;
        internal bool AbsenceConfirmed => batch.AbsenceConfirmed(0);

        internal void BindReleaseGraphGuard(Action requireEmptyGraph)
        {
            RequireMainThread();
            if (requireEmptyGraph == null) throw new ArgumentNullException(nameof(requireEmptyGraph));
            Require(createReturned && !releaseCalled && requireReleaseGraph == null,
                "GENERATED_OBJECT_RELEASE_GRAPH_BIND_REJECTED");
            requireReleaseGraph = requireEmptyGraph;
            requireEmptyGraph();
        }

        internal void Create(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(!createCalled && !releaseCalled, "GENERATED_OBJECT_ALREADY_USED");
            createCalled = true;
            requireScope = requireCurrentScope;
            try
            {
                stage = "check-source-and-archetype";
                requireCurrentScope();
                Require(source.IsCreated && ReferenceEquals(source, payload.Source) && payload.WriteCount == 8
                    && payload.SourceArchetype.Valid, "GENERATED_OBJECT_PAYLOAD_UNAVAILABLE");
                originalTypeCount = payload.SourceArchetype.TypesCount;
                Require(originalTypeCount > 1 && originalTypeCount <= 256, "GENERATED_OBJECT_ARCHETYPE_BUDGET");
                var types = typeSets.Read(payload.SourceArchetype, 256);
                Require(!types.ContainsKey(TypeManager.GetTypeIndex<Unity.Entities.Disabled>())
                    && !types.ContainsKey(TypeManager.GetTypeIndex<ObjectGenerationScope>()),
                    "GENERATED_OBJECT_ARCHETYPE_ALREADY_SCOPED");
                Require(types.ContainsKey(TypeManager.GetTypeIndex<Simulate>()), "GENERATED_OBJECT_SIMULATE_NOT_PRESENT");
                Require(types.ContainsKey(TypeManager.GetTypeIndex<Transform>())
                    && types.ContainsKey(TypeManager.GetTypeIndex<PrefabRef>())
                    && types.ContainsKey(TypeManager.GetTypeIndex<PseudoRandomSeed>())
                    && types.ContainsKey(TypeManager.GetTypeIndex<CustomMeshColor>()),
                    "GENERATED_OBJECT_SET_TARGET_MISSING");
                requireCurrentScope();
                stage = "create-scoped-archetype";
                EntityManager entities = source.EntityManager;
                EntityArchetype archetype = NativeEntityArchetypes.CreateScoped(entities, types);
                archetypeReturned = true; mergedTypeCount = archetype.TypesCount;
                NativeEntityArchetypes.RequireSame(typeSets.Read(archetype, 258), types);
                requireCurrentScope();

                stage = "create-disabled-root";
                batch.Create(0, archetype);
                Require(batch.CreationReturned(0) && Entity != EcsEntity.Null, "GENERATED_OBJECT_HANDLE_UNCONFIRMED");
                requireCurrentScope();
                NativeEntityArchetypes.RequireSame(typeSets.Read(entities.GetChunk(Entity).Archetype, 258), types);
                birthTypesVerified = true;
                birthLinkedChecked = true; birthLinkedEmpty = LinkedGroupIsEmpty(entities);
                Require(birthLinkedEmpty, "GENERATED_OBJECT_LINKED_GROUP_NOT_EMPTY");
                birthMeshEnabled = entities.IsComponentEnabled<CustomMeshColor>(Entity);
                birthSimulateEnabled = entities.IsComponentEnabled<Simulate>(Entity);
                Require(birthMeshEnabled && birthSimulateEnabled, "GENERATED_OBJECT_INITIAL_ENABLE_STATE_CHANGED");
                birthDefaultsVerified = true;

                stage = "assign-scope";
                entities.SetSharedComponent(Entity, new ObjectGenerationScope { Value = actor });
                scopeReturned = true;
                requireCurrentScope();
                for (int index = 0; index < payload.WriteCount; index++)
                {
                    GeneratedObjectWrite write = payload.WriteAt(index);
                    stage = "write-" + write.Kind;
                    writeOrder.Add(write.Kind.ToString());
                    writesAttempted++;
                    Write(entities, write);
                    writesReturned++;
                    requireCurrentScope();
                }
                writeOrderDigest = NativeEntityArchetypes.Digest(string.Join("|", writeOrder));

                stage = "verify-scoped-values";
                Require(entities.HasComponent<Unity.Entities.Disabled>(Entity)
                    && entities.GetSharedComponent<ObjectGenerationScope>(Entity).Value == actor
                    && LinkedGroupIsEmpty(entities), "GENERATED_OBJECT_SCOPE_CHANGED");
                for (int index = 0; index < payload.WriteCount; index++) Verify(entities, payload.WriteAt(index));
                valuesVerified = true;
                NativeEntityArchetypes.AddExpected<Surface>(types); NativeEntityArchetypes.AddExpected<Temp>(types);
                NativeEntityArchetypes.AddExpected<Animation>(types); NativeEntityArchetypes.AddExpected<InterpolatedTransform>(types);
                var actualTypes = typeSets.Read(entities.GetChunk(Entity).Archetype, 262);
                NativeEntityArchetypes.RequireSame(actualTypes, types);
                finalTypeDigest = NativeEntityArchetypes.TypeDigest(actualTypes); finalTypeCount = actualTypes.Count + 1; finalTypesVerified = true;
                Temp actualTemp = entities.GetComponentData<Temp>(Entity);
                readback = new JObject {
                    ["originalIsNull"] = actualTemp.m_Original == EcsEntity.Null, ["flags"] = (uint)actualTemp.m_Flags,
                    ["value"] = actualTemp.m_Value, ["cost"] = actualTemp.m_Cost,
                    ["meshColorEnabled"] = entities.IsComponentEnabled<CustomMeshColor>(Entity) };
                requireCurrentScope();
                createReturned = true; stage = "created";
            }
            catch (Exception error)
            {
                failureCode ??= error is Rejection rejected ? rejected.Code : "GENERATED_OBJECT_CREATE_FAILED";
                exceptionType ??= error.GetType().Name;
                throw;
            }
        }

        private void Write(EntityManager entities, GeneratedObjectWrite write)
        {
            switch (write.Kind)
            {
                case GeneratedObjectWriteKind.DisableMeshColor:
                    Require(write.Value is bool enabled && !enabled, "GENERATED_OBJECT_ENABLE_VALUE_INVALID");
                    entities.SetComponentEnabled<CustomMeshColor>(Entity, false); break;
                case GeneratedObjectWriteKind.Transform:
                    Transform transform = Value<Transform>(write);
                    Require(math.all(math.isfinite(transform.m_Position)) && math.all(math.isfinite(transform.m_Rotation.value)),
                        "GENERATED_OBJECT_TRANSFORM_INVALID");
                    entities.SetComponentData(Entity, transform); break;
                case GeneratedObjectWriteKind.Prefab:
                    PrefabRef prefab = Value<PrefabRef>(write);
                    Require(prefab.m_Prefab != EcsEntity.Null && prefab.m_Prefab.Index >= 0 && entities.Exists(prefab.m_Prefab)
                        && entities.HasComponent<PrefabData>(prefab.m_Prefab) && entities.IsComponentEnabled<PrefabData>(prefab.m_Prefab)
                        && !entities.HasComponent<Deleted>(prefab.m_Prefab), "GENERATED_OBJECT_PREFAB_UNAVAILABLE");
                    entities.SetComponentData(Entity, prefab); break;
                case GeneratedObjectWriteKind.Surface: entities.AddComponentData(Entity, Value<Surface>(write)); break;
                case GeneratedObjectWriteKind.Temp:
                    Temp temp = Value<Temp>(write);
                    Require(temp.m_Original == EcsEntity.Null && temp.m_Flags == (TempFlags.Create | TempFlags.Essential),
                        "GENERATED_OBJECT_TEMP_INVALID");
                    entities.AddComponentData(Entity, temp); break;
                case GeneratedObjectWriteKind.Animation: entities.AddComponentData(Entity, Value<Animation>(write)); break;
                case GeneratedObjectWriteKind.Interpolated: entities.AddComponentData(Entity, Value<InterpolatedTransform>(write)); break;
                case GeneratedObjectWriteKind.RandomSeed: entities.SetComponentData(Entity, Value<PseudoRandomSeed>(write)); break;
                default: throw new Rejection("GENERATED_OBJECT_WRITE_UNKNOWN");
            }
        }

        private void Verify(EntityManager entities, GeneratedObjectWrite write)
        {
            bool equal;
            switch (write.Kind)
            {
                case GeneratedObjectWriteKind.DisableMeshColor: equal = !entities.IsComponentEnabled<CustomMeshColor>(Entity); break;
                case GeneratedObjectWriteKind.Transform: equal = entities.GetComponentData<Transform>(Entity).Equals(Value<Transform>(write)); break;
                case GeneratedObjectWriteKind.Prefab: equal = entities.GetComponentData<PrefabRef>(Entity).Equals(Value<PrefabRef>(write)); break;
                case GeneratedObjectWriteKind.Surface: equal = entities.GetComponentData<Surface>(Entity).Equals(Value<Surface>(write)); break;
                case GeneratedObjectWriteKind.Temp: equal = entities.GetComponentData<Temp>(Entity).Equals(Value<Temp>(write)); break;
                case GeneratedObjectWriteKind.Animation: equal = entities.GetComponentData<Animation>(Entity).Equals(Value<Animation>(write)); break;
                case GeneratedObjectWriteKind.Interpolated: equal = entities.GetComponentData<InterpolatedTransform>(Entity).Equals(Value<InterpolatedTransform>(write)); break;
                case GeneratedObjectWriteKind.RandomSeed: equal = entities.GetComponentData<PseudoRandomSeed>(Entity).Equals(Value<PseudoRandomSeed>(write)); break;
                default: throw new Rejection("GENERATED_OBJECT_WRITE_UNKNOWN");
            }
            Require(equal, "GENERATED_OBJECT_VALUE_CHANGED");
        }

        private bool LinkedGroupIsEmpty(EntityManager entities) => !entities.HasComponent<LinkedEntityGroup>(Entity)
            || entities.GetBuffer<LinkedEntityGroup>(Entity, isReadOnly: true).Length == 0;

        internal void ConfirmGroupRemoval(Action requireScope)
        {
            batch.ConfirmGroupRemoval(requireScope);
            releaseCalled = true;
        }

        internal void ConfirmWorldTransfer(Action requireScope)
        {
            batch.ConfirmWorldTransfer(requireScope);
            releaseCalled = true;
        }

        // Fallback for fixtures whose normal cancel has not started.
        internal void Release()
        {
            RequireMainThread();
            if (releaseCalled) return;
            releaseCalled = true;
            try
            {
                requireScope?.Invoke();
                Require(batch.RefreshOutputs(), "GENERATED_OBJECT_OUTPUT_READ_UNCONFIRMED");
                Require(!batch.UnknownCreation, "GENERATED_OBJECT_HANDLE_UNCONFIRMED");
                if (Entity != EcsEntity.Null)
                {
                    Require(source.IsCreated, "GENERATED_OBJECT_RELEASE_SOURCE_UNAVAILABLE");
                    EntityManager entities = source.EntityManager;
                    if (entities.Exists(Entity))
                    {
                        Require(entities.HasComponent<Unity.Entities.Disabled>(Entity)
                            && entities.HasComponent<ObjectGenerationScope>(Entity), "GENERATED_OBJECT_RELEASE_SCOPE_MISSING");
                        int actualActor = entities.GetSharedComponent<ObjectGenerationScope>(Entity).Value;
                        Require(actualActor == actor || !scopeReturned && actualActor == 0,
                            "GENERATED_OBJECT_RELEASE_SCOPE_CHANGED");
                        // Reject cleanup residue and a linked group before the
                        // batch's DestroyEntity could retain or cascade targets.
                        typeSets.Read(entities.GetChunk(Entity).Archetype, 262);
                        releaseLinkedChecked = true; releaseLinkedEmpty = LinkedGroupIsEmpty(entities);
                        Require(releaseLinkedEmpty, "GENERATED_OBJECT_RELEASE_LINKED_GROUP_NOT_EMPTY");
                    }
                }
                batch.Release(RequireReleaseBoundary);
                if (!batch.ResourcesReleased)
                {
                    releaseFailure = "GENERATED_OBJECT_RELEASE_UNCONFIRMED";
                    exceptionType ??= batch.ExceptionType;
                }
            }
            catch (Exception error)
            {
                releaseFailure ??= error is Rejection rejected ? rejected.Code : "GENERATED_OBJECT_RELEASE_FAILED";
                exceptionType ??= error.GetType().Name;
            }
            finally { batch.ReleaseSlots(); }
        }

        private void RequireReleaseBoundary()
        {
            requireScope?.Invoke();
            requireReleaseGraph?.Invoke();
            if (Entity != EcsEntity.Null && source.IsCreated && source.EntityManager.Exists(Entity))
            {
                EntityManager entities = source.EntityManager;
                if (entities.HasBuffer<Game.Net.SubNet>(Entity))
                {
                    releaseSubNetChecked = true;
                    releaseSubNetEmpty = entities.GetBuffer<Game.Net.SubNet>(Entity, true).Length == 0;
                    Require(releaseSubNetEmpty, "GENERATED_OBJECT_RELEASE_SUBNET_NOT_EMPTY");
                }
            }
            requireScope?.Invoke();
            requireReleaseGraph?.Invoke();
        }

        private static T Value<T>(GeneratedObjectWrite write) where T : struct =>
            write.Value is T value ? value : throw new Rejection("GENERATED_OBJECT_WRITE_VALUE_INVALID");
        // All status fields below are cached managed values, including batch
        // receipts. No later status reader touches the Source or an archetype.
        internal JObject Snapshot() => new JObject {
            ["createCalled"] = createCalled, ["createReturned"] = createReturned,
            ["releaseCalled"] = releaseCalled, ["resourcesReleased"] = ResourcesReleased,
            ["unknownCreation"] = UnknownCreation, ["entity"] = batch.ReceiptSummary(0),
            ["slots"] = batch.SlotsSummary(),
            ["actor"] = actor,
            ["archetype"] = new JObject { ["created"] = archetypeReturned, ["originalCount"] = originalTypeCount,
                ["mergedCount"] = mergedTypeCount, ["finalCount"] = finalTypeCount, ["birthVerified"] = birthTypesVerified,
                ["finalVerified"] = finalTypesVerified, ["finalSha256"] = Text(finalTypeDigest) },
            ["defaults"] = new JObject { ["verified"] = birthDefaultsVerified, ["meshColorEnabled"] = birthMeshEnabled,
                ["simulateEnabled"] = birthSimulateEnabled, ["linkedChecked"] = birthLinkedChecked, ["linkedEmpty"] = birthLinkedEmpty },
            ["writes"] = new JObject { ["attempted"] = writesAttempted, ["returned"] = writesReturned,
                ["verified"] = valuesVerified, ["orderSha256"] = Text(writeOrderDigest) },
            ["temp"] = readback == null ? JValue.CreateNull() : readback.DeepClone(),
            ["cancel"] = new JObject { ["linkedChecked"] = releaseLinkedChecked, ["linkedEmpty"] = releaseLinkedEmpty },
            ["releaseGraph"] = new JObject { ["guardBound"] = requireReleaseGraph != null,
                ["subNetChecked"] = releaseSubNetChecked, ["subNetEmpty"] = releaseSubNetEmpty },
            ["typeArrays"] = typeSets.Snapshot(),
            ["stage"] = stage, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType),
            ["releaseFailure"] = Text(releaseFailure), ["nativePlaybackCalled"] = false,
            ["scope"] = "uninitialized-root-only"
        };
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "GENERATED_OBJECT_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
    }
}
#endif
