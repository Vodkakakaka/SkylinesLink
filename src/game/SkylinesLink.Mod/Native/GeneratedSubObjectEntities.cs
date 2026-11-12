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
    // A same-call-chain materialization of admitted scalar outputs. It neither
    // runs References nor owns the parent entities or their SubObject buffers.
    internal sealed class GeneratedSubObjectEntities
    {
        private const int OriginalTypeLimit = 256, FinalTypeLimit = 265, SnapshotByteLimit = 20480;
        private readonly World source;
        private readonly GeneratedSubObjectsPayload payload;
        private readonly int actor;
        private readonly NativeEntityBatch batch;
        private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
        private readonly TargetState[] targets;
        private readonly Step[] steps;
        private Action? requireScope;
        private Action? parentGraphGuard;
        private PrefabSystem? prefabs;
        private bool createCalled, createReturned, releaseCalled, allInputsChecked;
        private bool discardRequested, discardReady;
        private int executedCommands, copiedValuesMatched;
        private string stage = "not-started";
        private string? failureCode, exceptionType, releaseFailure;
        private string? discardFailureCode, discardExceptionType;

        internal GeneratedSubObjectEntities(World source, GeneratedSubObjectsPayload payload, int actor)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
            if (!ReferenceEquals(source, payload.Source) || actor <= 0 || actor != payload.Actor
                || payload.TargetCount < 1 || payload.CommandCount < 1)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_SOURCE_REJECTED");
            this.actor = actor;
            batch = new NativeEntityBatch(source, payload.TargetCount);
            targets = new TargetState[payload.TargetCount]; steps = new Step[payload.CommandCount];
            var assigned = new bool[steps.Length]; var identities = new HashSet<Entity>();
            for (int index = 0; index < targets.Length; index++)
            {
                GeneratedSubObjectTarget target = payload.TargetAt(index);
                Require(identities.Add(target.DeferredEntity) && target.DeferredEntity.Index < 0
                    && payload.IsParent(target.Owner), "GENERATED_SUBOBJECT_TARGET_REJECTED");
                targets[index] = new TargetState(target);
                BindStep(assigned, target.CreateOrdinal, index, -1);
                for (int write = 0; write < target.WriteCount; write++) BindStep(assigned, target.WriteAt(write).Ordinal, index, write);
            }
            foreach (bool value in assigned) Require(value, "GENERATED_SUBOBJECT_COMMAND_GAP");
        }

        internal int TargetCount => targets.Length;
        internal bool Owns(GeneratedSubObjectsPayload values) => ReferenceEquals(payload, values);
        internal Temp CapturedTempAt(int index) => Value<Temp>(FindWrite(targets[index], GeneratedSubObjectWriteKind.Temp));
        internal Entity EntityAt(int index) => batch.EntityAt(index);
        internal bool CreateReturned => createReturned;
        internal bool DiscardReady => discardReady;
        private bool nativeCancellation;
        internal bool UnknownCreation => batch.UnknownCreation;
        internal bool ResourcesReleased => batch.ResourcesReleased && archetypes.ResourcesReleased;
        internal bool AbsenceConfirmed
        {
            get { for (int index = 0; index < targets.Length; index++) if (!batch.AbsenceConfirmed(index)) return false; return true; }
        }

        private void BindStep(bool[] assigned, int ordinal, int target, int write)
        {
            Require(ordinal >= 0 && ordinal < steps.Length && !assigned[ordinal], "GENERATED_SUBOBJECT_COMMAND_ORDER_INVALID");
            assigned[ordinal] = true; steps[ordinal] = new Step(target, write);
        }

        internal void BindParentGraphGuard(Action guard)
        {
            RequireMainThread();
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            Require(!createCalled && !releaseCalled && parentGraphGuard == null, "GENERATED_SUBOBJECT_GRAPH_GUARD_ALREADY_BOUND");
            parentGraphGuard = guard;
        }

        internal void Create(Action requireCurrentScope)
        {
            RequireMainThread();
            if (requireCurrentScope == null) throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(!createCalled && !releaseCalled, "GENERATED_SUBOBJECT_ALREADY_USED");
            createCalled = true; requireScope = requireCurrentScope;
            try
            {
                stage = "precheck-all-targets"; GuardParents(); RequireParentsEmpty();
                prefabs = source.GetExistingSystemManaged<PrefabSystem>(); GuardParents();
                Require(prefabs != null && ReferenceEquals(prefabs.World, source), "GENERATED_SUBOBJECT_PREFAB_SYSTEM_UNAVAILABLE");
                for (int index = 0; index < targets.Length; index++) Prepare(targets[index]);
                allInputsChecked = true;
                for (int ordinal = 0; ordinal < steps.Length; ordinal++)
                {
                    Step step = steps[ordinal]; TargetState state = targets[step.Target];
                    GuardParents(); RequireParentsEmpty();
                    if (step.Write < 0) Birth(step.Target, state);
                    else
                    {
                        GeneratedSubObjectWrite write = state.Target.WriteAt(step.Write);
                        stage = "write-" + write.Kind; state.WritesAttempted++;
                        Write(step.Target, state, write);
                        state.WritesReturned++; state.ReturnedMask |= 1 << (int)write.Kind;
                    }
                    GuardParents(); RequireParentsEmpty(); executedCommands++;
                }
                stage = "verify-all-targets";
                for (int index = 0; index < targets.Length; index++) Verify(index, targets[index]);
                GuardParents(); RequireParentsEmpty(); createReturned = true; stage = "created";
            }
            catch (Exception error) { Record(error, "GENERATED_SUBOBJECT_CREATE_FAILED"); throw; }
        }

        private void Prepare(TargetState state)
        {
            GuardParents(); RequirePrefab(state);
            EntityArchetype archetype = state.Target.SourceArchetype;
            Require(archetype.Valid && archetype.TypesCount > 0 && archetype.TypesCount <= OriginalTypeLimit,
                "GENERATED_SUBOBJECT_ARCHETYPE_BUDGET");
            Dictionary<TypeIndex, ComponentType> original = archetypes.Read(archetype, OriginalTypeLimit); GuardParents();
            ValidateMetadata(original, false, false);
            Require(!original.ContainsKey(TypeManager.GetTypeIndex<Disabled>())
                && !original.ContainsKey(TypeManager.GetTypeIndex<ObjectGenerationScope>()), "GENERATED_SUBOBJECT_ALREADY_SCOPED");
            state.OriginalTypes = original;
            var expected = new Dictionary<TypeIndex, ComponentType>(original);
            NativeEntityArchetypes.AddExpected<Disabled>(expected);
            expected.Add(TypeManager.GetTypeIndex<ObjectGenerationScope>(), ComponentType.ReadWrite<ObjectGenerationScope>());
            for (int index = 0; index < state.Target.WriteCount; index++)
            {
                GeneratedSubObjectWrite write = state.Target.WriteAt(index);
                TypeIndex type = TypeManager.GetTypeIndex(write.Value.GetType());
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(type);
                Require(write.ComponentBytes == info.SizeInChunk && info.SizeInChunk > 0 && info.SizeInChunk <= 1024,
                    "GENERATED_SUBOBJECT_WRITE_SIZE_CHANGED");
                Require(!write.IsSet || original.ContainsKey(type), "GENERATED_SUBOBJECT_SET_TARGET_MISSING");
                ValidateValue(state, write);
                if (!expected.ContainsKey(type)) expected.Add(type, ComponentType.FromTypeIndex(type));
            }
            ValidateMetadata(expected, true, true);
            state.ExpectedTypes = expected; state.OriginalTypeCount = archetype.TypesCount;
            state.OriginalTypeDigest = NativeEntityArchetypes.TypeDigest(original); state.InputsChecked = true;
            GuardParents();
        }

        private void Birth(int index, TargetState state)
        {
            Require(allInputsChecked && state.InputsChecked, "GENERATED_SUBOBJECT_INPUTS_UNCONFIRMED");
            stage = "create-scoped-archetype";
            var birthTypes = new Dictionary<TypeIndex, ComponentType>(state.OriginalTypes!);
            EntityArchetype scoped = NativeEntityArchetypes.CreateScoped(source.EntityManager, birthTypes); GuardParents();
            NativeEntityArchetypes.RequireSame(archetypes.Read(scoped, 258), birthTypes); GuardParents();
            RequirePrefab(state);
            stage = "create-disabled-child";
            batch.Create(index, scoped); GuardParents();
            Require(batch.CreationReturned(index), "GENERATED_SUBOBJECT_BIRTH_UNCONFIRMED");
            Entity entity = RequireChild(index, state, true);
            Dictionary<TypeIndex, ComponentType> actual = ReadTypes(index, state);
            NativeEntityArchetypes.RequireSame(actual, birthTypes); state.BirthTypesMatched = true;
            state.BirthBuffers = ReadEmptyBuffers(index, state, actual); state.LatestBuffers = state.BirthBuffers;
            state.BirthEnableableCount = ReadEnableables(index, state, actual);
            state.BirthEnableablesMatched = true;
            GuardChild(index, state); PrefabRef initial = source.EntityManager.GetComponentData<PrefabRef>(entity); GuardChild(index, state);
            Require(initial.m_Prefab == Entity.Null, "GENERATED_SUBOBJECT_BIRTH_REFERENCE_NOT_NULL");
            stage = "assign-child-scope"; state.ScopeAttempted = true;
            GuardChild(index, state); source.EntityManager.SetSharedComponent(entity, new ObjectGenerationScope { Value = actor });
            state.ScopeReturned = true; GuardParents(); RequireChild(index, state, false);
        }

        private void Write(int index, TargetState state, GeneratedSubObjectWrite write)
        {
            GuardParents(); RequireChild(index, state, false);
            switch (write.Kind)
            {
                case GeneratedSubObjectWriteKind.Owner: Add(index, state, Value<Owner>(write)); break;
                case GeneratedSubObjectWriteKind.Prefab: RequirePrefab(state); Set(index, state, Value<PrefabRef>(write)); break;
                case GeneratedSubObjectWriteKind.Transform: Set(index, state, Value<NativeTransform>(write)); break;
                case GeneratedSubObjectWriteKind.Temp: Add(index, state, Value<Temp>(write)); break;
                case GeneratedSubObjectWriteKind.Animation: Add(index, state, Value<NativeAnimation>(write)); break;
                case GeneratedSubObjectWriteKind.Interpolated: Add(index, state, Value<InterpolatedTransform>(write)); break;
                case GeneratedSubObjectWriteKind.RandomSeed: Set(index, state, Value<PseudoRandomSeed>(write)); break;
                case GeneratedSubObjectWriteKind.Elevation: Add(index, state, Value<ObjectElevation>(write)); break;
                case GeneratedSubObjectWriteKind.Aligned: Add(index, state, Value<Game.Objects.Aligned>(write)); break;
                case GeneratedSubObjectWriteKind.StreetLight: Set(index, state, Value<Game.Objects.StreetLight>(write)); break;
                default: throw new Rejection("GENERATED_SUBOBJECT_WRITE_UNKNOWN");
            }
        }

        private void Add<T>(int index, TargetState state, T value) where T : unmanaged, IComponentData
        {
            Entity entity = RequireChild(index, state, false); state.AddAttempted++;
            source.EntityManager.AddComponent<T>(entity); state.AddReturned++;
            // AddComponentData internally performs Add -> structural callbacks ->
            // Set without our scope check. Split this fixed typed operation here.
            GuardParents(); RequireChild(index, state, false);
            if (payload.IsRelocation)
            {
                RequireParentsEmpty();
                if (value is Temp temp && temp.m_Original != Entity.Null)
                    payload.RequireRelocationWitness(requireScope!);
            }
            Set(index, state, value);
        }

        private void Set<T>(int index, TargetState state, T value) where T : unmanaged, IComponentData
        {
            GuardParents(); Entity entity = RequireChild(index, state, false);
            Require(source.EntityManager.HasComponent<T>(entity), "GENERATED_SUBOBJECT_SET_TARGET_CHANGED"); GuardParents();
            RequireChild(index, state, false);
            state.SetAttempted++; source.EntityManager.SetComponentData(entity, value); state.SetReturned++;
            GuardParents(); RequireChild(index, state, false);
        }

        private void Verify(int index, TargetState state)
        {
            GuardParents(); RequireChild(index, state, false); RequirePrefab(state);
            Dictionary<TypeIndex, ComponentType> types = ReadTypes(index, state);
            NativeEntityArchetypes.RequireSame(types, state.ExpectedTypes!); ValidateMetadata(types, true, true);
            state.FinalTypeCount = types.Count + 1; state.FinalTypeDigest = NativeEntityArchetypes.TypeDigest(types);
            state.FinalTypesMatched = true; state.LatestBuffers = ReadEmptyBuffers(index, state, types);
            state.FinalEnableableCount = ReadEnableables(index, state, types); state.FinalEnableablesMatched = true;
            for (int write = 0; write < state.Target.WriteCount; write++)
            {
                GeneratedSubObjectWrite operation = state.Target.WriteAt(write);
                switch (operation.Kind)
                {
                    case GeneratedSubObjectWriteKind.Owner: Equal(index, state, Value<Owner>(operation)); break;
                    case GeneratedSubObjectWriteKind.Prefab: Equal(index, state, Value<PrefabRef>(operation)); break;
                    case GeneratedSubObjectWriteKind.Transform: Equal(index, state, Value<NativeTransform>(operation)); break;
                    case GeneratedSubObjectWriteKind.Temp: Equal(index, state, Value<Temp>(operation)); break;
                    case GeneratedSubObjectWriteKind.Animation: Equal(index, state, Value<NativeAnimation>(operation)); break;
                    case GeneratedSubObjectWriteKind.Interpolated: Equal(index, state, Value<InterpolatedTransform>(operation)); break;
                    case GeneratedSubObjectWriteKind.RandomSeed: Equal(index, state, Value<PseudoRandomSeed>(operation)); break;
                    case GeneratedSubObjectWriteKind.Elevation: Equal(index, state, Value<ObjectElevation>(operation)); break;
                    case GeneratedSubObjectWriteKind.Aligned: Equal(index, state, Value<Game.Objects.Aligned>(operation)); break;
                    case GeneratedSubObjectWriteKind.StreetLight: Equal(index, state, Value<Game.Objects.StreetLight>(operation)); break;
                    default: throw new Rejection("GENERATED_SUBOBJECT_READBACK_UNKNOWN");
                }
                state.ValuesMatched++; copiedValuesMatched++;
            }
            state.CopiedValuesMatched = state.ValuesMatched == state.Target.WriteCount;
            GuardChild(index, state);
        }

        private void Equal<T>(int index, TargetState state, T expected) where T : unmanaged, IComponentData
        {
            Entity entity = GuardChild(index, state); T actual = source.EntityManager.GetComponentData<T>(entity); GuardChild(index, state);
            Require(actual.Equals(expected), "GENERATED_SUBOBJECT_COPIED_VALUE_CHANGED");
        }

        private void ValidateValue(TargetState state, GeneratedSubObjectWrite write)
        {
            switch (write.Kind)
            {
                case GeneratedSubObjectWriteKind.Owner:
                    Require(Value<Owner>(write).m_Owner == state.Target.Owner, "GENERATED_SUBOBJECT_OWNER_VALUE_INVALID"); break;
                case GeneratedSubObjectWriteKind.Prefab:
                    Require(Value<PrefabRef>(write).m_Prefab == state.Target.Prefab, "GENERATED_SUBOBJECT_PREFAB_VALUE_INVALID"); break;
                case GeneratedSubObjectWriteKind.Transform:
                    NativeTransform transform = Value<NativeTransform>(write);
                    Require(Finite(transform.m_Position) && Finite(transform.m_Rotation.value), "GENERATED_SUBOBJECT_TRANSFORM_INVALID"); break;
                case GeneratedSubObjectWriteKind.Temp:
                    int? aligned = state.Target.Has(GeneratedSubObjectWriteKind.Aligned)
                        ? Value<Game.Objects.Aligned>(FindWrite(state, GeneratedSubObjectWriteKind.Aligned)).m_SubObjectIndex
                        : (int?)null;
                    Require(payload.ChildTempMatches(state.Target.Owner, state.Target.Prefab, aligned, Value<Temp>(write)),
                        "GENERATED_SUBOBJECT_TEMP_INVALID"); break;
                case GeneratedSubObjectWriteKind.Animation:
                    Require(Value<NativeAnimation>(write).Equals(default(NativeAnimation)), "GENERATED_SUBOBJECT_ANIMATION_INVALID"); break;
                case GeneratedSubObjectWriteKind.Interpolated:
                    Require(Value<InterpolatedTransform>(write).Equals(default(InterpolatedTransform)), "GENERATED_SUBOBJECT_INTERPOLATED_INVALID"); break;
                case GeneratedSubObjectWriteKind.RandomSeed: Value<PseudoRandomSeed>(write); break;
                case GeneratedSubObjectWriteKind.Elevation:
                    ObjectElevation elevation = Value<ObjectElevation>(write);
                    Require(math.isfinite(elevation.m_Elevation) && elevation.m_Flags == 0, "GENERATED_SUBOBJECT_ELEVATION_INVALID"); break;
                case GeneratedSubObjectWriteKind.Aligned:
                    Require(state.Target.Owner == payload.Node || payload.IsRelocation && state.Target.Owner == payload.OldNode,
                        "GENERATED_SUBOBJECT_ALIGNMENT_OWNER_INVALID");
                    int row = Value<Game.Objects.Aligned>(write).m_SubObjectIndex; bool found = false;
                    for (int index = 0; index < state.Target.CandidateRowCount; index++) found |= state.Target.CandidateRowAt(index) == row;
                    Require(found, "GENERATED_SUBOBJECT_ALIGNMENT_ROW_INVALID"); break;
                case GeneratedSubObjectWriteKind.StreetLight: Value<Game.Objects.StreetLight>(write); break;
                default: throw new Rejection("GENERATED_SUBOBJECT_VALUE_UNKNOWN");
            }
        }

        private void ValidateMetadata(Dictionary<TypeIndex, ComponentType> types, bool scoped, bool writtenReferences)
        {
            foreach (TypeIndex index in types.Keys)
            {
                ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                Require(!index.IsManagedType && !index.IsCleanupComponent && !index.IsCleanupBufferComponent
                    && !index.IsCleanupSharedComponent && !index.IsChunkComponent && !index.HasNativeContainer && !index.IsRefCounted
                    && (!index.IsSharedComponentType || scoped && index == TypeManager.GetTypeIndex<ObjectGenerationScope>())
                    && !info.HasBlobAssetRefs && !info.HasWeakAssetRefs && !info.HasUnityObjectRefs
                    && info.BlobAssetRefOffsetCount == 0 && info.WeakAssetRefOffsetCount == 0 && info.UnityObjectRefOffsetCount == 0,
                    "GENERATED_SUBOBJECT_TYPE_METADATA_UNSUPPORTED");
                int references = index == TypeManager.GetTypeIndex<PrefabRef>() || writtenReferences
                    && (index == TypeManager.GetTypeIndex<Owner>() || index == TypeManager.GetTypeIndex<Temp>()) ? 1 : 0;
                Require(info.EntityOffsetCount == references, "GENERATED_SUBOBJECT_REFERENCE_LAYOUT_UNSUPPORTED");
                if (index.IsBuffer) Require(KnownBuffer(index), "GENERATED_SUBOBJECT_BUFFER_UNSUPPORTED");
            }
        }

        private static bool KnownBuffer(TypeIndex index) => index == TypeManager.GetTypeIndex<CustomMeshColor>()
            || index == TypeManager.GetTypeIndex<MeshBatch>() || index == TypeManager.GetTypeIndex<MeshColor>()
            || index == TypeManager.GetTypeIndex<Emissive>() || index == TypeManager.GetTypeIndex<LightState>()
            || index == TypeManager.GetTypeIndex<Game.Effects.EnabledEffect>();

        private JObject ReadEmptyBuffers(int index, TargetState state, Dictionary<TypeIndex, ComponentType> types)
        {
            var result = new JObject();
            Empty<CustomMeshColor>(index, state, types, result); Empty<MeshBatch>(index, state, types, result);
            Empty<MeshColor>(index, state, types, result); Empty<Emissive>(index, state, types, result);
            Empty<LightState>(index, state, types, result); Empty<Game.Effects.EnabledEffect>(index, state, types, result);
            // A linked group must never turn this exact-handle destroy into a cascade.
            Entity entity = GuardChild(index, state);
            if (source.EntityManager.HasBuffer<LinkedEntityGroup>(entity))
            {
                int linked = source.EntityManager.GetBuffer<LinkedEntityGroup>(entity, true).Length; GuardChild(index, state);
                Require(linked == 0, "GENERATED_SUBOBJECT_LINKED_GROUP_NOT_EMPTY");
            }
            return result;
        }

        private void Empty<T>(int index, TargetState state, Dictionary<TypeIndex, ComponentType> types, JObject result) where T : unmanaged, IBufferElementData
        {
            if (!types.ContainsKey(TypeManager.GetTypeIndex<T>())) return;
            Entity entity = GuardChild(index, state); int length = source.EntityManager.GetBuffer<T>(entity, true).Length; GuardChild(index, state);
            result[typeof(T).Name] = length; Require(length == 0, "GENERATED_SUBOBJECT_BUFFER_NOT_EMPTY");
        }

        private int ReadEnableables(int index, TargetState state, Dictionary<TypeIndex, ComponentType> types)
        {
            int count = 0;
            foreach (ComponentType type in types.Values)
                if (type.TypeIndex.IsEnableable)
                {
                    Entity entity = GuardChild(index, state); bool enabled = source.EntityManager.IsComponentEnabled(entity, type); GuardChild(index, state);
                    Require(enabled, "GENERATED_SUBOBJECT_ENABLEABLE_CHANGED");
                    count++;
                }
            return count;
        }

        private Dictionary<TypeIndex, ComponentType> ReadTypes(int index, TargetState state)
        {
            Entity entity = GuardChild(index, state); EntityArchetype archetype = source.EntityManager.GetChunk(entity).Archetype; GuardChild(index, state);
            Dictionary<TypeIndex, ComponentType> types = archetypes.Read(archetype, FinalTypeLimit); GuardChild(index, state); return types;
        }

        private void RequirePrefab(TargetState state)
        {
            GuardParents(); Entity prefab = state.Target.Prefab; EntityManager entities = source.EntityManager;
            Require(prefab != Entity.Null && entities.Exists(prefab) && entities.HasComponent<PrefabData>(prefab)
                && entities.HasComponent<ObjectData>(prefab) && !entities.HasComponent<Deleted>(prefab), "GENERATED_SUBOBJECT_PREFAB_UNAVAILABLE");
            ObjectData currentObject = entities.GetComponentData<ObjectData>(prefab); GuardParents();
            Require(currentObject.m_Archetype.Equals(state.Target.SourceArchetype), "GENERATED_SUBOBJECT_PREFAB_ARCHETYPE_CHANGED");
            bool found = prefabs!.TryGetPrefab<PrefabBase>(prefab, out PrefabBase managed); GuardParents();
            Require(found && managed != null, "GENERATED_SUBOBJECT_PREFAB_ROUNDTRIP_FAILED");
            bool roundtrip = prefabs.TryGetEntity(managed, out Entity actual); GuardParents();
            Require(roundtrip && actual == prefab, "GENERATED_SUBOBJECT_PREFAB_ROUNDTRIP_FAILED");
            state.PrefabDataEnabledObserved = entities.IsComponentEnabled<PrefabData>(prefab); GuardParents();
            // Child content enabled bits are recorded, not promoted to root-build eligibility.
            state.PrefabRoundtripMatched = true;
        }

        private void GuardCore()
        {
            requireScope!.Invoke();
            Require(source.IsCreated, "GENERATED_SUBOBJECT_SOURCE_UNAVAILABLE");
        }

        private void GuardParents()
        {
            CheckParents();
            if (parentGraphGuard == null) RequireParentsEmpty();
            else
            {
                parentGraphGuard();
                CheckParents();
            }
        }

        private void CheckParents()
        {
            GuardCore(); CheckParent(payload.Root); CheckParent(payload.Node);
            if (payload.IsRelocation) CheckParent(payload.OldNode);
        }

        private void CheckParent(Entity entity)
        {
            EntityManager entities = source.EntityManager;
            Require(entities.Exists(entity) && entities.HasComponent<Disabled>(entity) && entities.HasComponent<ObjectGenerationScope>(entity)
                && entities.HasComponent<Temp>(entity) && entities.HasComponent<Deleted>(entity) == nativeCancellation,
                "GENERATED_SUBOBJECT_PARENT_CHANGED");
            int actual = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value; GuardCore();
            Temp temp = entities.GetComponentData<Temp>(entity); GuardCore();
            Require(actual == actor && payload.ParentTempMatches(entity, temp), "GENERATED_SUBOBJECT_PARENT_SCOPE_CHANGED");
            Require(entities.HasBuffer<Game.Objects.SubObject>(entity), "GENERATED_SUBOBJECT_PARENT_BUFFER_MISSING");
            if (entity == payload.Node)
            {
                Require(entities.HasComponent<Owner>(entity), "GENERATED_SUBOBJECT_NODE_OWNER_MISSING");
                Owner owner = entities.GetComponentData<Owner>(entity); GuardCore();
                Require(owner.m_Owner == payload.Root, "GENERATED_SUBOBJECT_NODE_OWNER_CHANGED");
            }
            else Require(!entities.HasComponent<Owner>(entity), "GENERATED_SUBOBJECT_ROOT_OWNER_CHANGED");
        }

        private void RequireParentsEmpty()
        {
            RequireParentEmpty(payload.Root); RequireParentEmpty(payload.Node);
            if (payload.IsRelocation) RequireParentEmpty(payload.OldNode);
        }

        private void RequireParentEmpty(Entity entity)
        {
            GuardCore(); CheckParent(entity);
            int length = source.EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true).Length;
            GuardCore(); CheckParent(entity);
            Require(length == 0, "GENERATED_SUBOBJECT_PARENT_BUFFER_NOT_EMPTY");
        }

        private Entity RequireChild(int index, TargetState state, bool allowInitialScope)
        {
            GuardCore(); Entity entity = batch.EntityAt(index); EntityManager entities = source.EntityManager;
            Require(entity != Entity.Null && !payload.IsParent(entity) && entities.Exists(entity)
                && entities.HasComponent<Disabled>(entity) && entities.HasComponent<ObjectGenerationScope>(entity),
                "GENERATED_SUBOBJECT_CHILD_IDENTITY_CHANGED");
            for (int other = 0; other < targets.Length; other++)
                if (other != index) Require(batch.EntityAt(other) != entity, "GENERATED_SUBOBJECT_CHILD_IDENTITY_DUPLICATE");
            int actual = entities.GetSharedComponent<ObjectGenerationScope>(entity).Value; GuardCore();
            Require(actual == actor || allowInitialScope && !state.ScopeReturned && actual == 0, "GENERATED_SUBOBJECT_CHILD_SCOPE_CHANGED");
            return entity;
        }

        private Entity GuardChild(int index, TargetState state)
        { GuardParents(); return RequireChild(index, state, true); }

        // Deleted is an owned request to discard these exact children. It stays
        // present through the native unlink and physical destruction; it is not
        // temporarily removed to satisfy the materialization type checks.
        internal void RequestDiscard(Action requireLinkedGraph)
        {
            RequireMainThread();
            if (requireLinkedGraph == null) throw new ArgumentNullException(nameof(requireLinkedGraph));
            Require(createReturned && parentGraphGuard != null && !releaseCalled && !discardRequested
                && targets.Length == (payload.IsRelocation ? 6 : payload.RootChildCount + payload.NodeChildCount),
                "GENERATED_SUBOBJECT_DISCARD_NOT_READY");
            discardRequested = true;
            try
            {
                stage = "precheck-discard";
                for (int index = 0; index < targets.Length; index++)
                {
                    TargetState state = targets[index]; Entity entity = GuardDiscard(index, state, requireLinkedGraph);
                    Dictionary<TypeIndex, ComponentType> types = ReadTypes(index, state);
                    NativeEntityArchetypes.RequireSame(types, state.ExpectedTypes!); ValidateMetadata(types, true, true);
                    Require(!source.EntityManager.HasComponent<Deleted>(entity), "GENERATED_SUBOBJECT_ALREADY_DELETED");
                    GuardDiscard(index, state, requireLinkedGraph);
                    Equal(index, state, Value<Owner>(FindWrite(state, GeneratedSubObjectWriteKind.Owner)));
                    Equal(index, state, Value<PrefabRef>(FindWrite(state, GeneratedSubObjectWriteKind.Prefab)));
                    Equal(index, state, Value<Temp>(FindWrite(state, GeneratedSubObjectWriteKind.Temp)));
                    state.LatestBuffers = ReadEmptyBuffers(index, state, types);
                    GuardDiscard(index, state, requireLinkedGraph);
                }
                for (int index = 0; index < targets.Length; index++)
                {
                    TargetState state = targets[index]; Entity entity = GuardDiscard(index, state, requireLinkedGraph);
                    Require(!source.EntityManager.HasComponent<Deleted>(entity), "GENERATED_SUBOBJECT_ALREADY_DELETED");
                    GuardDiscard(index, state, requireLinkedGraph);
                    stage = "request-child-discard"; state.DiscardAttempted = true;
                    NativeEntityArchetypes.AddExpected<Deleted>(state.ExpectedTypes!);
                    Exception? observationError = null;
                    try
                    {
                        source.EntityManager.AddComponent<Deleted>(entity);
                        state.DiscardReturned = true;
                    }
                    catch (Exception error)
                    {
                        state.DiscardFailureCode ??= Code(error, "GENERATED_SUBOBJECT_DISCARD_ADD_FAILED");
                        state.DiscardExceptionType ??= error.GetType().Name; throw;
                    }
                    finally
                    {
                        try { ObserveDeleted(index, state, requireLinkedGraph); }
                        catch (Exception error)
                        {
                            state.DiscardFailureCode ??= Code(error, "GENERATED_SUBOBJECT_DELETED_OBSERVATION_FAILED");
                            state.DiscardExceptionType ??= error.GetType().Name; observationError = error;
                        }
                    }
                    if (observationError != null) throw new Rejection("GENERATED_SUBOBJECT_DELETED_OBSERVATION_FAILED");
                    Require(state.DeletedObservationReturned && state.DeletedObserved == true, "GENERATED_SUBOBJECT_DELETED_NOT_OBSERVED");
                }
                for (int index = 0; index < targets.Length; index++)
                {
                    TargetState state = targets[index];
                    GuardDiscard(index, state, requireLinkedGraph); ObserveDeleted(index, state, requireLinkedGraph);
                    Dictionary<TypeIndex, ComponentType> types = ReadTypes(index, state);
                    NativeEntityArchetypes.RequireSame(types, state.ExpectedTypes!); ValidateMetadata(types, true, true);
                    Require(state.DiscardReturned && state.DeletedObservationReturned && state.DeletedObserved == true,
                        "GENERATED_SUBOBJECT_DISCARD_UNCONFIRMED");
                    state.DiscardTypesMatched = true; state.DiscardTypeDigest = NativeEntityArchetypes.TypeDigest(types);
                    GuardDiscard(index, state, requireLinkedGraph);
                }
                discardReady = true; stage = "discard-marked";
            }
            catch (Exception error)
            {
                discardFailureCode ??= Code(error, "GENERATED_SUBOBJECT_DISCARD_FAILED");
                discardExceptionType ??= error.GetType().Name; throw;
            }
        }

        private Entity GuardDiscard(int index, TargetState state, Action requireLinkedGraph)
        {
            GuardParents(); RequireChild(index, state, false);
            requireLinkedGraph();
            GuardParents(); return RequireChild(index, state, false);
        }

        private void ObserveDeleted(int index, TargetState state, Action requireLinkedGraph)
        {
            state.DeletedObservationReturned = false;
            Entity entity = GuardDiscard(index, state, requireLinkedGraph);
            bool deleted = source.EntityManager.HasComponent<Deleted>(entity);
            GuardDiscard(index, state, requireLinkedGraph);
            state.DeletedObserved = deleted; state.DeletedObservationReturned = true;
        }

        private static GeneratedSubObjectWrite FindWrite(TargetState state, GeneratedSubObjectWriteKind kind)
        {
            for (int index = 0; index < state.Target.WriteCount; index++)
                if (state.Target.WriteAt(index).Kind == kind) return state.Target.WriteAt(index);
            throw new Rejection("GENERATED_SUBOBJECT_WRITE_MISSING");
        }

        internal void RequireNativeCancellationShape()
        {
            RequireMainThread();
            Require(payload.IsRelocation && createReturned && !discardRequested && !releaseCalled && !UnknownCreation,
                "GENERATED_SUBOBJECT_NATIVE_CANCELLATION_UNAVAILABLE");
            nativeCancellation = true;
            GuardParents();
            var entities = source.EntityManager;
            for (int index = 0; index < targets.Length; index++)
            {
                var state = targets[index];
                Entity child = RequireChild(index, state, false);
                var expected = new Dictionary<TypeIndex, ComponentType>(state.ExpectedTypes!);
                expected.Remove(TypeManager.GetTypeIndex<Applied>());
                expected.Remove(TypeManager.GetTypeIndex<Created>());
                expected.Remove(TypeManager.GetTypeIndex<Updated>());
                NativeEntityArchetypes.AddExpected<Deleted>(expected);
                var actual = ReadTypes(index, state);
                NativeEntityArchetypes.RequireSame(actual, expected);
                ValidateMetadata(actual, true, true);
                state.LatestBuffers = ReadEmptyBuffers(index, state, actual);
                Require(entities.GetComponentData<Owner>(child).m_Owner == state.Target.Owner
                    && entities.GetComponentData<PrefabRef>(child).m_Prefab == state.Target.Prefab
                    && entities.GetComponentData<Temp>(child).Equals(CapturedTempAt(index)),
                    "GENERATED_SUBOBJECT_NATIVE_CANCELLATION_VALUE_CHANGED");
                state.DiscardTypesMatched = true;
                state.DiscardReturned = true;
                state.ReleaseClosureChecked = true;
                state.DeletedObserved = true;
                state.DeletedObservationReturned = true;
            }
            GuardCore();
        }

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

        internal void ConfirmMixedTransfer(Entity survivor, Action requireScope)
        {
            batch.ConfirmMixedTransfer(survivor, requireScope);
            releaseCalled = true;
        }

        // Parent joins/discards the producer before fallback retirement.
        internal void Release()
        {
            RequireMainThread(); if (releaseCalled) return; releaseCalled = true;
            try
            {
                // A missing output remains unknown. Known siblings may still be
                // released; no receipt or parent-retention obligation is erased.
                batch.RefreshOutputs();
                batch.Release(GuardAllForRelease);
                if (!ResourcesReleased) { releaseFailure = "GENERATED_SUBOBJECT_RELEASE_UNCONFIRMED"; exceptionType ??= batch.ExceptionType; }
            }
            catch (Exception error) { releaseFailure ??= Code(error, "GENERATED_SUBOBJECT_RELEASE_FAILED"); exceptionType ??= error.GetType().Name; }
            finally { batch.ReleaseSlots(); }
        }

        private void GuardAllForRelease()
        {
            GuardParents(); RequireParentsEmpty();
            for (int index = 0; index < targets.Length; index++)
            {
                Entity entity = batch.EntityAt(index); TargetState state = targets[index];
                if (entity == Entity.Null || !source.EntityManager.Exists(entity)) continue;
                RequireChild(index, state, true);
                Dictionary<TypeIndex, ComponentType> actual = ReadTypes(index, state); ValidateMetadata(actual, true, true);
                if (createReturned)
                    NativeEntityArchetypes.RequireSame(actual, state.ExpectedTypes!);
                else
                {
                    foreach (TypeIndex type in state.OriginalTypes!.Keys)
                        Require(actual.ContainsKey(type), "GENERATED_SUBOBJECT_RELEASE_ORIGINAL_TYPE_CHANGED");
                    foreach (TypeIndex type in actual.Keys)
                        Require(state.ExpectedTypes!.ContainsKey(type), "GENERATED_SUBOBJECT_RELEASE_TYPE_CHANGED");
                }
                state.LatestBuffers = ReadEmptyBuffers(index, state, actual);
                EntityManager entities = source.EntityManager;
                GuardChild(index, state); PrefabRef prefab = entities.GetComponentData<PrefabRef>(entity); GuardChild(index, state);
                Require(prefab.m_Prefab == state.Target.Prefab || !Returned(state, GeneratedSubObjectWriteKind.Prefab) && prefab.m_Prefab == Entity.Null,
                    "GENERATED_SUBOBJECT_RELEASE_PREFAB_CHANGED");
                if (entities.HasComponent<Owner>(entity))
                {
                    GuardChild(index, state); Owner owner = entities.GetComponentData<Owner>(entity); GuardChild(index, state);
                    Require(owner.m_Owner == state.Target.Owner || !Returned(state, GeneratedSubObjectWriteKind.Owner) && owner.m_Owner == Entity.Null,
                        "GENERATED_SUBOBJECT_RELEASE_OWNER_CHANGED");
                }
                if (entities.HasComponent<Temp>(entity))
                {
                    GuardChild(index, state); Temp temp = entities.GetComponentData<Temp>(entity); GuardChild(index, state);
                    bool expected = payload.IsRelocation
                        ? temp.Equals(Value<Temp>(FindWrite(state, GeneratedSubObjectWriteKind.Temp)))
                            || !Returned(state, GeneratedSubObjectWriteKind.Temp) && temp.Equals(default(Temp))
                        : temp.m_Original == Entity.Null;
                    Require(expected, "GENERATED_SUBOBJECT_RELEASE_ORIGINAL_CHANGED");
                }
                state.ReleaseClosureChecked = true;
            }
            GuardParents(); RequireParentsEmpty();
        }

        // Captured command details are already in the adjacent producer receipt.
        internal JObject RelocationSnapshot()
        {
            Require(payload.IsRelocation, "GENERATED_SUBOBJECT_RELOCATION_REQUIRED");
            var values = new JArray();
            for (int index = 0; index < targets.Length; index++)
            {
                TargetState state = targets[index];
                Temp temp = Value<Temp>(FindWrite(state, GeneratedSubObjectWriteKind.Temp));
                values.Add(new JObject {
                    ["deferred"] = Id(state.Target.DeferredEntity), ["entity"] = Id(batch.EntityAt(index)),
                    ["owner"] = Id(state.Target.Owner), ["prefab"] = Id(state.Target.Prefab),
                    ["original"] = Id(temp.m_Original), ["flags"] = (uint)temp.m_Flags,
                    ["createAttempted"] = batch.CreationAttempted(index), ["createReturned"] = batch.CreationReturned(index), ["scopeAssigned"] = state.ScopeReturned,
                    ["typesMatched"] = state.BirthTypesMatched && state.FinalTypesMatched,
                    ["enableablesMatched"] = state.BirthEnableablesMatched && state.FinalEnableablesMatched,
                    ["valuesMatched"] = state.ValuesMatched, ["buffersEmpty"] = state.FinalEnableablesMatched,
                    ["discardReturned"] = state.DiscardReturned, ["deletedObserved"] = state.DeletedObserved,
                    ["discardTypesMatched"] = state.DiscardTypesMatched,
                    ["releaseClosureChecked"] = state.ReleaseClosureChecked,
                    ["destroyReturned"] = batch.DestructionReturned(index) || nativeCancellation && releaseCalled && batch.AbsenceConfirmed(index),
                    ["absenceConfirmed"] = batch.AbsenceConfirmed(index)
                });
            }
            return new JObject {
                ["targets"] = values, ["commandsExecuted"] = executedCommands, ["matchedWriteCount"] = copiedValuesMatched,
                ["createReturned"] = CreateReturned, ["absenceConfirmed"] = AbsenceConfirmed, ["resourcesReleased"] = ResourcesReleased,
                ["unknownCreation"] = UnknownCreation, ["stage"] = stage, ["failureCode"] = Text(failureCode),
                ["exceptionType"] = Text(exceptionType), ["releaseFailure"] = Text(releaseFailure)
            };
        }

        internal JObject Snapshot()
        {
            var values = new JArray();
            for (int index = 0; index < targets.Length; index++)
            {
                TargetState state = targets[index]; var ordinals = new JArray();
                for (int write = 0; write < state.Target.WriteCount; write++) ordinals.Add(state.Target.WriteAt(write).Ordinal);
                values.Add(new JObject
                {
                    ["deferred"] = Id(state.Target.DeferredEntity), ["entity"] = Id(batch.EntityAt(index)),
                    ["owner"] = Id(state.Target.Owner), ["prefab"] = Id(state.Target.Prefab),
                    ["createOrdinal"] = state.Target.CreateOrdinal, ["writeOrdinals"] = ordinals,
                    ["inputsChecked"] = state.InputsChecked, ["scopeAttempted"] = state.ScopeAttempted, ["scopeReturned"] = state.ScopeReturned,
                    ["birthTypesMatched"] = state.BirthTypesMatched, ["originalTypeCount"] = state.OriginalTypeCount,
                    ["originalTypeSha256"] = Text(state.OriginalTypeDigest), ["finalTypeCount"] = state.FinalTypeCount,
                    ["finalTypesMatched"] = state.FinalTypesMatched, ["finalTypeSha256"] = Text(state.FinalTypeDigest),
                    ["birthBuffers"] = Clone(state.BirthBuffers), ["latestBuffers"] = Clone(state.LatestBuffers),
                    ["birthEnableableCount"] = state.BirthEnableableCount, ["birthEnableablesMatched"] = state.BirthEnableablesMatched,
                    ["finalEnableableCount"] = state.FinalEnableableCount, ["finalEnableablesMatched"] = state.FinalEnableablesMatched,
                    ["prefabRoundtripMatched"] = state.PrefabRoundtripMatched,
                    ["prefabDataEnabledObserved"] = state.PrefabDataEnabledObserved.HasValue ? new JValue(state.PrefabDataEnabledObserved.Value) : JValue.CreateNull(),
                    ["writesAttempted"] = state.WritesAttempted, ["writesReturned"] = state.WritesReturned,
                    ["copiedValuesMatched"] = state.CopiedValuesMatched, ["matchedWriteCount"] = state.ValuesMatched,
                    ["addAttempted"] = state.AddAttempted, ["addReturned"] = state.AddReturned,
                    ["setAttempted"] = state.SetAttempted, ["setReturned"] = state.SetReturned,
                    ["discardAttempted"] = state.DiscardAttempted, ["discardReturned"] = state.DiscardReturned,
                    ["deletedObserved"] = state.DeletedObserved.HasValue ? new JValue(state.DeletedObserved.Value) : JValue.CreateNull(),
                    ["deletedObservationReturned"] = state.DeletedObservationReturned,
                    ["discardTypesMatched"] = state.DiscardTypesMatched, ["discardTypeSha256"] = Text(state.DiscardTypeDigest),
                    ["discardFailureCode"] = Text(state.DiscardFailureCode), ["discardExceptionType"] = Text(state.DiscardExceptionType),
                    ["releaseClosureChecked"] = state.ReleaseClosureChecked, ["receipt"] = batch.ReceiptSummary(index)
                });
            }
            var result = new JObject
            {
                ["createCalled"] = createCalled, ["createReturned"] = createReturned, ["releaseCalled"] = releaseCalled,
                ["resourcesReleased"] = ResourcesReleased, ["unknownCreation"] = UnknownCreation, ["absenceConfirmed"] = AbsenceConfirmed,
                ["targetCount"] = targets.Length, ["commandCount"] = steps.Length, ["executedCommands"] = executedCommands,
                ["allInputsChecked"] = allInputsChecked, ["copiedValuesMatched"] = createReturned && copiedValuesMatched == steps.Length - targets.Length,
                ["matchedWriteCount"] = copiedValuesMatched,
                ["parentGraphGuardBound"] = parentGraphGuard != null, ["discardRequested"] = discardRequested, ["discardReady"] = discardReady,
                ["discardFailureCode"] = Text(discardFailureCode), ["discardExceptionType"] = Text(discardExceptionType),
                ["targets"] = values, ["slots"] = batch.SlotsSummary(), ["typeArrays"] = archetypes.Snapshot(),
                ["stage"] = stage, ["failureCode"] = Text(failureCode), ["exceptionType"] = Text(exceptionType), ["releaseFailure"] = Text(releaseFailure),
                ["originalEcbPlaybackCalled"] = false, ["invokesReferencesSystem"] = false,
                ["parentSubObjectBuffersEmptyForPhysicalRelease"] = true, ["completePreviewObserved"] = false,
                ["scope"] = "Exact Disabled scalar births, owned discard markers and self-release; live parent graph is observed through the bound port. References execution, normal tool cancel, qualification and fees are not owned here."
            };
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > SnapshotByteLimit)
                throw new InvalidOperationException("GENERATED_SUBOBJECT_SNAPSHOT_BUDGET");
            return result;
        }

        private static bool Returned(TargetState state, GeneratedSubObjectWriteKind kind) => (state.ReturnedMask & (1 << (int)kind)) != 0;
        private static T Value<T>(GeneratedSubObjectWrite write) where T : struct => write.Value is T value ? value
            : throw new Rejection("GENERATED_SUBOBJECT_WRITE_VALUE_INVALID");
        private void Record(Exception error, string fallback) { failureCode ??= Code(error, fallback); exceptionType ??= error.GetType().Name; }
        private static string Code(Exception error, string fallback) => error is Rejection rejected ? rejected.Code : fallback;
        private static JArray Id(Entity entity) => new JArray(entity.Index, entity.Version);
        private static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static JToken Clone(JObject? value) => value == null ? JValue.CreateNull() : value.DeepClone();
        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static bool Finite(float4 value) => math.all(math.isfinite(value));
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "GENERATED_SUBOBJECT_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }
        private sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }
        private readonly struct Step
        { internal readonly int Target, Write; internal Step(int target, int write) { Target = target; Write = write; } }
        private sealed class TargetState
        {
            internal readonly GeneratedSubObjectTarget Target;
            internal Dictionary<TypeIndex, ComponentType>? OriginalTypes, ExpectedTypes;
            internal JObject? BirthBuffers, LatestBuffers;
            internal bool InputsChecked, ScopeAttempted, ScopeReturned, BirthTypesMatched, FinalTypesMatched, BirthEnableablesMatched, FinalEnableablesMatched;
            internal bool PrefabRoundtripMatched, CopiedValuesMatched, ReleaseClosureChecked;
            internal bool DiscardAttempted, DiscardReturned, DeletedObservationReturned, DiscardTypesMatched;
            internal bool? PrefabDataEnabledObserved;
            internal bool? DeletedObserved;
            internal int OriginalTypeCount, FinalTypeCount, BirthEnableableCount, FinalEnableableCount;
            internal int WritesAttempted, WritesReturned, ValuesMatched, ReturnedMask, AddAttempted, AddReturned, SetAttempted, SetReturned;
            internal string? OriginalTypeDigest, FinalTypeDigest;
            internal string? DiscardTypeDigest, DiscardFailureCode, DiscardExceptionType;
            internal TargetState(GeneratedSubObjectTarget target) { Target = target; }
        }
    }
}
#endif
