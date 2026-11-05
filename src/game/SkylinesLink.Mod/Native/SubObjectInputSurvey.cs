#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Core;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using LiveSubObject = Game.Objects.SubObject;
using PrefabSubObject = Game.Prefabs.SubObject;
using NativeTransform = Game.Objects.Transform;

namespace SkylinesLink.Mod.Native
{
    // Development-only first-layer reads. No native selection, random draw,
    // generation, or proof that an empty prefab table makes the kernel a no-op.
    internal static class SubObjectInputSurvey
    {
        internal static JObject Capture(World source, Entity root, Entity node, int actor, Action requireCurrentScope)
            => new Reader(source, root, node, actor, requireCurrentScope).Capture();

        private sealed class Reader
        {
            private const int TableLimit = 64, RowLimit = 256, OutputLimit = 16384, TypeLimit = 256, TotalTypeLimit = 1024;
            private readonly World source;
            private readonly Entity root, node;
            private readonly int actor;
            private readonly Action requireScope;
            private readonly HashSet<string> reasons = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<Entity> children = new HashSet<Entity>();
            private readonly HashSet<Entity> selections = new HashSet<Entity>();
            private readonly NativeEntityArchetypes archetypes = new NativeEntityArchetypes();
            private int rows, typesRead, declaredSubObjectRows;
            private bool complete = true;

            internal Reader(World source, Entity root, Entity node, int actor, Action requireScope)
            {
                this.source = source ?? throw new ArgumentNullException(nameof(source));
                this.requireScope = requireScope ?? throw new ArgumentNullException(nameof(requireScope));
                this.root = root; this.node = node; this.actor = actor;
            }

            private EntityManager Entities => source.EntityManager;

            private void Guard()
            {
                if (!MainThreadDispatcher.isMainThread || actor <= 0 || root == node
                    || root.Index < 0 || root.Version <= 0 || node.Index < 0 || node.Version <= 0)
                    throw new InvalidOperationException("SUBOBJECT_SURVEY_IDENTITY_REJECTED");
                requireScope();
                if (!source.IsCreated) throw new InvalidOperationException("SUBOBJECT_SURVEY_SOURCE_LOST");
                EntityManager entities = Entities;
                RequireOwn(entities, root); RequireOwn(entities, node);
            }

            private void RequireOwn(EntityManager entities, Entity entity)
            {
                if (!entities.Exists(entity) || !entities.HasComponent<Disabled>(entity)
                    || !entities.HasComponent<ObjectGenerationScope>(entity)
                    || entities.GetSharedComponent<ObjectGenerationScope>(entity).Value != actor)
                    throw new InvalidOperationException("SUBOBJECT_SURVEY_SCOPE_CHANGED");
            }

            private void Unknown(string reason, bool readIncomplete = false)
            { reasons.Add(reason); if (readIncomplete) complete = false; }

            private bool Has<T>(Entity entity) where T : unmanaged, IComponentData
            {
                bool present = entity != Entity.Null && Entities.Exists(entity) && Entities.HasComponent<T>(entity);
                Guard(); return present;
            }

            private bool Read<T>(Entity entity, out T value) where T : unmanaged, IComponentData
            {
                if (!Has<T>(entity)) { value = default; return false; }
                value = Entities.GetComponentData<T>(entity); Guard(); return true;
            }

            private bool Reserve(int length, string name)
            {
                if (length < 0 || length > TableLimit || length > RowLimit - rows)
                { Unknown("ROW_BUDGET:" + name, true); return false; }
                rows += length; return true;
            }

            private JObject Length<T>(Entity entity, bool oldInstances = false) where T : unmanaged, IBufferElementData
            {
                bool present = entity != Entity.Null && Entities.Exists(entity) && Entities.HasBuffer<T>(entity);
                Guard();
                int length = 0;
                if (present) { length = Entities.GetBuffer<T>(entity, true).Length; Guard(); }
                if (oldInstances && length != 0) Unknown("NONEMPTY_LIVE_BUFFER:" + typeof(T).FullName);
                return new JObject { ["present"] = present, ["length"] = length };
            }

            private JObject Table<T>(Entity entity, string name, Func<T, JToken> encode) where T : unmanaged, IBufferElementData
            {
                bool present = entity != Entity.Null && Entities.Exists(entity) && Entities.HasBuffer<T>(entity);
                Guard();
                if (!present) return new JObject { ["present"] = false, ["length"] = 0, ["complete"] = true, ["rows"] = new JArray() };
                DynamicBuffer<T> buffer = Entities.GetBuffer<T>(entity, true); Guard();
                int length = buffer.Length;
                if (!Reserve(length, name))
                    return new JObject { ["present"] = true, ["length"] = length, ["complete"] = false };
                // Copy the entire admitted table before encoding or reading other components.
                var copy = new T[length];
                for (int index = 0; index < length; index++) copy[index] = buffer[index];
                Guard();
                var values = new JArray();
                for (int index = 0; index < copy.Length; index++) values.Add(encode(copy[index]));
                return new JObject { ["present"] = true, ["length"] = length, ["complete"] = true, ["rows"] = values };
            }

            internal JObject Capture()
            {
                Guard();
                JObject rootLive = Live(root), nodeLive = Live(node);
                Entity rootPrefab = Prefab(root), nodePrefab = Prefab(node);
                var prefabs = new JArray { PrefabInputs(rootPrefab), PrefabInputs(nodePrefab) };
                JObject environment = EnvironmentInputs();
                JObject queryInputs = Queries();
                var contexts = new JArray { RequirementContext(root, rootPrefab), RequirementContext(node, nodePrefab) };
                var construction = new JArray { Construction(root, rootPrefab), Construction(node, nodePrefab) };
                JObject childInputs = ChildInputs();
                JObject selectionInputs = SelectionInputs();
                Guard();
                var unresolved = new JArray();
                foreach (string reason in reasons) unresolved.Add(reason);
                var result = new JObject
                {
                    ["scope"] = "first-layer-only", ["completed"] = complete,
                    ["nativeEligibilityEstablished"] = false, ["nativeKernelInvoked"] = false,
                    ["generationNoOpProven"] = false, ["live"] = new JArray { rootLive, nodeLive },
                    ["prefabs"] = prefabs, ["queries"] = queryInputs, ["environment"] = environment,
                    ["requirementContexts"] = contexts, ["construction"] = construction,
                    ["directChildren"] = childInputs, ["selectOnlyTargets"] = selectionInputs,
                    ["rowsRead"] = rows, ["perTableLimit"] = TableLimit, ["totalRowLimit"] = RowLimit,
                    ["archetypeTypesAdmitted"] = typesRead, ["perArchetypeTypeLimit"] = TypeLimit, ["totalArchetypeTypeLimit"] = TotalTypeLimit,
                    ["typeArrays"] = archetypes.Snapshot(), ["typeArraysReleased"] = archetypes.ResourcesReleased,
                    ["typeMetadataColumns"] = new JArray("type", "category", "sizeInChunk", "elementSize", "bufferCapacity",
                        "entityReferences", "blobReferences", "weakReferences", "unityReferences", "flags"),
                    ["typeMetadataFlags"] = new JObject { ["buffer"] = 1, ["shared"] = 2, ["enableable"] = 4,
                        ["chunk"] = 8, ["nativeContainer"] = 16, ["managed"] = 32, ["cleanup"] = 64,
                        ["refCounted"] = 128, ["hasBlobReferences"] = 256, ["hasWeakReferences"] = 512, ["hasUnityReferences"] = 1024 },
                    ["unresolvedInputs"] = unresolved
                };
                if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > OutputLimit)
                    throw new InvalidOperationException("SUBOBJECT_SURVEY_OUTPUT_BUDGET");
                return result;
            }

            private Entity Prefab(Entity entity)
            {
                if (Read(entity, out PrefabRef value) && value.m_Prefab != Entity.Null && Entities.Exists(value.m_Prefab))
                { Guard(); return value.m_Prefab; }
                Guard(); Unknown("PREFAB_UNAVAILABLE", true); return Entity.Null;
            }

            private JObject Live(Entity entity)
            {
                var result = new JObject { ["entity"] = Id(entity), ["object"] = Has<Game.Objects.Object>(entity),
                    ["created"] = Has<Created>(entity), ["updated"] = Has<Updated>(entity), ["deleted"] = Has<Deleted>(entity),
                    ["overridden"] = Has<Overridden>(entity) };
                result["prefab"] = Read(entity, out PrefabRef prefab) ? Id(prefab.m_Prefab) : JValue.CreateNull();
                if (Read(entity, out Temp temp))
                {
                    result["temp"] = new JObject { ["original"] = Id(temp.m_Original), ["flags"] = (uint)temp.m_Flags,
                        ["cost"] = temp.m_Cost, ["value"] = temp.m_Value, ["curvePosition"] = temp.m_CurvePosition };
                    if (temp.m_Original != Entity.Null) Unknown("NONNEW_TEMP");
                }
                else { result["temp"] = JValue.CreateNull(); Unknown("TEMP_UNAVAILABLE", true); }
                Entity owner = Read(entity, out Owner ownerData) ? ownerData.m_Owner : Entity.Null;
                result["owner"] = new JObject { ["present"] = Has<Owner>(entity), ["entity"] = Id(owner) };
                bool expectedOwner = entity == root ? owner == Entity.Null : owner == root;
                result["expectedOwnerChain"] = expectedOwner;
                if (!expectedOwner) Unknown("OWNER_CHAIN_NOT_CLOSED");
                if (Read(entity, out Game.Objects.Attachment attachment))
                {
                    result["attachment"] = Id(attachment.m_Attached);
                    if (attachment.m_Attached != Entity.Null && !Own(attachment.m_Attached)) Unknown("FOREIGN_ATTACHMENT_NOT_FOLLOWED");
                }
                else result["attachment"] = JValue.CreateNull();
                if (Read(entity, out Game.Objects.Attached attached))
                {
                    result["attached"] = new JObject { ["parent"] = Id(attached.m_Parent), ["oldParent"] = Id(attached.m_OldParent), ["curvePosition"] = attached.m_CurvePosition };
                    if ((attached.m_Parent != Entity.Null && !Own(attached.m_Parent)) || (attached.m_OldParent != Entity.Null && !Own(attached.m_OldParent)))
                        Unknown("FOREIGN_ATTACHED_NOT_FOLLOWED");
                }
                else result["attached"] = JValue.CreateNull();
                result["transform"] = Read(entity, out NativeTransform transform)
                    ? new JObject { ["position"] = Vec(transform.m_Position), ["rotation"] = Vec(transform.m_Rotation.value) } : JValue.CreateNull();
                result["node"] = Read(entity, out Game.Net.Node netNode)
                    ? new JObject { ["position"] = Vec(netNode.m_Position), ["rotation"] = Vec(netNode.m_Rotation.value) } : JValue.CreateNull();
                result["underConstruction"] = Read(entity, out Game.Objects.UnderConstruction under)
                    ? new JObject { ["newPrefab"] = Id(under.m_NewPrefab), ["progress"] = under.m_Progress, ["speed"] = under.m_Speed } : JValue.CreateNull();
                if (Read(entity, out Destroyed destroyed))
                {
                    result["destroyed"] = new JObject { ["event"] = Id(destroyed.m_Event), ["cleared"] = destroyed.m_Cleared };
                    Unknown("DESTROYED_PROCEDURAL_BRANCH_NOT_INSPECTED");
                }
                else result["destroyed"] = JValue.CreateNull();
                result["seed"] = Read(entity, out PseudoRandomSeed seed) ? new JValue(seed.m_Seed) : JValue.CreateNull();
                result["native"] = Has<Game.Common.Native>(entity);
                result["relativePresent"] = Has<Game.Objects.Relative>(entity);
                // These three observed inputs control the owner's relative/interpolated
                // branch; this reader never runs that branch or modifies a transform.
                result["interpolatedTransformPresent"] = Has<Game.Rendering.InterpolatedTransform>(entity);
                result["buildingFlags"] = Read(entity, out Game.Buildings.Building building) ? new JValue((uint)building.m_Flags) : JValue.CreateNull();
                result["watercraftFlags"] = Read(entity, out Game.Vehicles.Watercraft watercraft) ? new JValue((uint)watercraft.m_Flags) : JValue.CreateNull();
                result["elevation"] = Read(entity, out Game.Net.Elevation elevation) ? Vec(elevation.m_Elevation) : JValue.CreateNull();
                bool objectElevation = Has<Game.Objects.Elevation>(entity);
                result["objectElevationPresent"] = objectElevation;
                result["objectElevation"] = Read(entity, out Game.Objects.Elevation objectHeight)
                    ? new JObject { ["value"] = objectHeight.m_Elevation, ["flags"] = (uint)objectHeight.m_Flags } : JValue.CreateNull();
                result["fixed"] = Read(entity, out Game.Net.Fixed fixedNode) ? new JValue(fixedNode.m_Index) : JValue.CreateNull();
                result["outsideConnection"] = Read(entity, out Game.Net.OutsideConnection outside) ? new JValue(outside.m_Delay) : JValue.CreateNull();
                result["orphan"] = Read(entity, out Game.Net.Orphan orphan) ? Id(orphan.m_Composition) : JValue.CreateNull();
                result["subObjects"] = Length<LiveSubObject>(entity, true);
                result["installedUpgrades"] = Length<Game.Buildings.InstalledUpgrade>(entity, true);
                result["renters"] = Length<Game.Buildings.Renter>(entity, true);
                result["connectedEdges"] = Length<Game.Net.ConnectedEdge>(entity, true);
                return result;
            }

            private JObject PrefabInputs(Entity prefab)
            {
                var result = new JObject { ["entity"] = Id(prefab), ["exists"] = prefab != Entity.Null && Entities.Exists(prefab) };
                Guard();
                if (prefab == Entity.Null) return result;
                bool netGeometry = Has<NetGeometryData>(prefab), placeableNet = Has<PlaceableNetData>(prefab);
                result["netGeometryDataPresent"] = netGeometry;
                result["placeableNetDataPresent"] = placeableNet;
                if (netGeometry || placeableNet) Unknown("NET_PLACEMENT_GEOMETRY_NOT_INSPECTED");
                result["movingObjectDataPresent"] = Has<MovingObjectData>(prefab);
                result["placeable"] = PlaceableInputs(prefab);
                result["subObjects"] = Table<PrefabSubObject>(prefab, "prefab-subobjects", value =>
                {
                    declaredSubObjectRows++;
                    if (value.m_ParentIndex % 1000 > 0) Unknown("PARENT_MESH_BONE_INPUTS_NOT_INSPECTED");
                    if (value.m_Prefab != Entity.Null) children.Add(value.m_Prefab);
                    return new JObject { ["prefab"] = Id(value.m_Prefab), ["flags"] = (uint)value.m_Flags,
                        ["position"] = Vec(value.m_Position), ["rotation"] = Vec(value.m_Rotation.value),
                        ["parentIndex"] = value.m_ParentIndex, ["groupIndex"] = value.m_GroupIndex, ["probability"] = value.m_Probability };
                });
                result["requirements"] = Table<ObjectRequirementElement>(prefab, "prefab-requirements", value =>
                {
                    if ((value.m_Type & ObjectRequirementType.SelectOnly) != 0 && value.m_Requirement != Entity.Null) selections.Add(value.m_Requirement);
                    return new JObject { ["requirement"] = Id(value.m_Requirement), ["group"] = value.m_Group,
                        ["type"] = (uint)value.m_Type, ["require"] = (uint)value.m_RequireFlags, ["forbid"] = (uint)value.m_ForbidFlags };
                });
                result["placeholder"] = PlaceholderData(prefab);
                result["placeholderObjects"] = Table<PlaceholderObjectElement>(prefab, "prefab-placeholders", value =>
                { if (value.m_Object != Entity.Null) children.Add(value.m_Object); return Id(value.m_Object); });
                return result;
            }

            private JToken PlaceholderData(Entity prefab) => Read(prefab, out PlaceholderObjectData value)
                ? new JObject { ["requirementMask"] = (uint)value.m_RequirementMask, ["randomizeGroupIndex"] = value.m_RandomizeGroupIndex } : JValue.CreateNull();

            private JObject ChildInputs()
            {
                var result = new JObject { ["count"] = children.Count, ["declaredSubObjectRowsRead"] = declaredSubObjectRows,
                    ["fixedCreationInputsRequested"] = true, ["fullSubObjectAdmissionEstablished"] = false };
                if (!Reserve(children.Count, "direct-child-metadata")) { result["complete"] = false; return result; }
                var values = new JArray();
                bool simpleCandidateBranch = true, childReadsComplete = true;
                foreach (Entity child in children)
                {
                    bool exists = Entities.Exists(child); Guard();
                    var item = new JObject { ["entity"] = Id(child), ["exists"] = exists };
                    if (exists)
                    {
                        item["placeholder"] = PlaceholderData(child);
                        JObject placeholders = Length<PlaceholderObjectElement>(child), requirements = Length<ObjectRequirementElement>(child), subObjects = Length<PrefabSubObject>(child);
                        item["placeholderObjects"] = placeholders; item["requirements"] = requirements; item["subObjects"] = subObjects;
                        bool placeholderBranch = item["placeholder"]!.Type != JTokenType.Null && placeholders["present"]!.Value<bool>();
                        bool unexpandedRows = placeholders["length"]!.Value<int>() != 0 || requirements["length"]!.Value<int>() != 0 || subObjects["length"]!.Value<int>() != 0;
                        item["unexpandedChildRows"] = unexpandedRows;
                        simpleCandidateBranch &= !placeholderBranch;
                        if (placeholderBranch || unexpandedRows) Unknown("CHILD_SELECTION_OR_DESCENDANTS_NOT_EXPANDED");
                        item["spawnable"] = Read(child, out SpawnableObjectData spawnable)
                            ? new JObject { ["randomizationGroup"] = Id(spawnable.m_RandomizationGroup), ["probability"] = spawnable.m_Probability } : JValue.CreateNull();
                        JObject content = ContentInputs(child); item["content"] = content;
                        bool? prefabEnabled = content["prefabDataEnabled"]!.Type == JTokenType.Boolean
                            ? content["prefabDataEnabled"]!.Value<bool>() : (bool?)null;
                        bool geometryPresent = Read(child, out ObjectGeometryData geometry);
                        item["geometry"] = geometryPresent ? GeometryInputs(geometry) : JValue.CreateNull();
                        item["placeable"] = PlaceableInputs(child);
                        item["pillar"] = Read(child, out PillarData pillar)
                            ? new JObject { ["type"] = (int)pillar.m_Type, ["offsetRange"] = new JArray(pillar.m_OffsetRange.min, pillar.m_OffsetRange.max) } : JValue.CreateNull();
                        bool tree = Read(child, out TreeData treeData), stack = Read(child, out StackData stackData);
                        bool streetLight = Read(child, out StreetLightData lightData), spawnLocation = Has<SpawnLocationData>(child);
                        item["tree"] = tree ? new JObject { ["woodAmountRaw"] = treeData.m_WoodAmount } : JValue.CreateNull();
                        item["stack"] = stack ? new JObject { ["direction"] = (int)stackData.m_Direction,
                            ["first"] = new JArray(stackData.m_FirstBounds.min, stackData.m_FirstBounds.max),
                            ["middle"] = new JArray(stackData.m_MiddleBounds.min, stackData.m_MiddleBounds.max),
                            ["last"] = new JArray(stackData.m_LastBounds.min, stackData.m_LastBounds.max),
                            ["dontScale"] = new JArray(stackData.m_DontScale.x, stackData.m_DontScale.y, stackData.m_DontScale.z) } : JValue.CreateNull();
                        item["streetLight"] = streetLight ? new JObject { ["layer"] = (uint)lightData.m_Layer } : JValue.CreateNull();
                        // CreateSubObject consumes only presence of SpawnLocationData,
                        // BuildingExtensionData and SubLane, not their component values.
                        item["spawnLocationDataPresent"] = spawnLocation;
                        item["buildingExtensionDataPresent"] = Has<BuildingExtensionData>(child);
                        item["subLanes"] = Length<Game.Prefabs.SubLane>(child);
                        item["transportStopDataPresent"] = Has<Game.Prefabs.TransportStopData>(child);
                        JObject archetype = ArchetypeInputs(child, prefabEnabled, geometryPresent, tree, stack, streetLight, spawnLocation);
                        item["archetype"] = archetype;
                        if (archetype["metadataComplete"]?.Type == JTokenType.Boolean && !archetype["metadataComplete"]!.Value<bool>()) childReadsComplete = false;
                    }
                    else { Unknown("DIRECT_CHILD_UNAVAILABLE", true); simpleCandidateBranch = false; childReadsComplete = false; }
                    values.Add(item);
                }
                // The bound is only for these declared rows taking the non-placeholder
                // new-object branch. It excludes procedural/deeper generation and does
                // not predict which candidates pass requirements or random probability.
                result["declaredRowsNewCreateUpperBound"] = simpleCandidateBranch && complete
                    ? new JValue(declaredSubObjectRows) : JValue.CreateNull();
                result["createBoundScope"] = "declared-rows-only-excludes-procedural-and-descendants";
                if (children.Count != 0) Unknown("FULL_SUBOBJECT_ADMISSION_NOT_EVALUATED");
                result["complete"] = childReadsComplete; result["values"] = values; return result;
            }

            private JObject ContentInputs(Entity prefab)
            {
                bool present = Read(prefab, out PrefabData data);
                bool? enabled = present ? Entities.IsComponentEnabled<PrefabData>(prefab) : (bool?)null; Guard();
                bool lockedPresent = Has<Locked>(prefab);
                bool? locked = lockedPresent ? Entities.IsComponentEnabled<Locked>(prefab) : (bool?)null; Guard();
                var result = new JObject { ["prefabDataPresent"] = present,
                    ["prefabIndex"] = present ? new JValue(data.m_Index) : JValue.CreateNull(),
                    ["prefabDataEnabled"] = enabled.HasValue ? new JValue(enabled.Value) : JValue.CreateNull(),
                    ["lockedEnabled"] = locked.HasValue ? new JValue(locked.Value) : JValue.CreateNull(),
                    ["prerequisite"] = Read(prefab, out ContentPrerequisiteData content) ? Id(content.m_ContentPrerequisite) : JValue.CreateNull(),
                    ["contentEligibilityEvaluated"] = false };
                if (!present) Unknown("CHILD_PREFAB_DATA_UNAVAILABLE");
                // Locked/content references are observations, not root-build rules.
                // Native CreateSubObject uses enabled only in specific tree/stack paths.
                return result;
            }

            private JToken PlaceableInputs(Entity prefab) => Read(prefab, out PlaceableObjectData value)
                ? new JObject { ["offset"] = Vec(value.m_PlacementOffset), ["flags"] = (uint)value.m_Flags,
                    ["swaying"] = (value.m_Flags & Game.Objects.PlacementFlags.Swaying) != 0 } : JValue.CreateNull();

            private static JObject GeometryInputs(ObjectGeometryData geometry) => new JObject
            {
                ["min"] = Vec(geometry.m_Bounds.min), ["max"] = Vec(geometry.m_Bounds.max), ["size"] = Vec(geometry.m_Size),
                ["pivot"] = Vec(geometry.m_Pivot), ["legSize"] = Vec(geometry.m_LegSize), ["legOffset"] = Vec(geometry.m_LegOffset),
                ["flags"] = (uint)geometry.m_Flags, ["minLod"] = geometry.m_MinLod,
                ["layers"] = (ulong)geometry.m_Layers, ["subObjectMask"] = (uint)geometry.m_SubObjectMask
            };

            private JObject ArchetypeInputs(Entity prefab, bool? prefabEnabled, bool geometry, bool tree, bool stack, bool streetLight, bool spawnLocation)
            {
                if (!Read(prefab, out ObjectData data))
                { Unknown("CHILD_OBJECT_DATA_UNAVAILABLE"); return new JObject { ["objectDataPresent"] = false }; }
                EntityArchetype archetype = data.m_Archetype;
                var result = new JObject { ["objectDataPresent"] = true, ["valid"] = archetype.Valid };
                if (!archetype.Valid)
                {
                    result["newBranchSkipsInvalidArchetype"] = true;
                    return result;
                }
                int count = archetype.TypesCount; result["typesCount"] = count;
                if (count <= 0 || count > TypeLimit || count > TotalTypeLimit - typesRead)
                { Unknown("CHILD_ARCHETYPE_TYPE_BUDGET", true); result["metadataComplete"] = false; return result; }
                typesRead += count;
                Dictionary<TypeIndex, ComponentType> types;
                try { types = archetypes.Read(archetype, TypeLimit); }
                catch (InvalidOperationException error) when (error.Message == "NATIVE_ARCHETYPE_TYPE_UNSUPPORTED")
                {
                    Guard(); Unknown("ARCHETYPE_MECHANICS_READER_UNSUPPORTED", true);
                    result["metadataComplete"] = false; result["mechanicsReaderUnsupported"] = true;
                    // This helper rejects managed/cleanup/duplicate type sets. Rejection
                    // limits our read mechanics; it does not prohibit native generation.
                    return result;
                }
                Guard();
                var ordered = new List<TypeIndex>(types.Keys);
                ordered.Sort((left, right) => StringComparer.Ordinal.Compare(TypeManager.GetType(left).FullName, TypeManager.GetType(right).FullName));
                var metadata = new JArray(); var exceptional = new JArray();
                int entityReferences = 0;
                foreach (TypeIndex index in ordered)
                {
                    ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                    int flags = (index.IsBuffer ? 1 : 0) | (index.IsSharedComponentType ? 2 : 0) | (index.IsEnableable ? 4 : 0)
                        | (index.IsChunkComponent ? 8 : 0) | (index.HasNativeContainer ? 16 : 0) | (index.IsManagedType ? 32 : 0)
                        | (index.IsCleanupComponent || index.IsCleanupBufferComponent || index.IsCleanupSharedComponent ? 64 : 0)
                        | (index.IsRefCounted ? 128 : 0) | (info.HasBlobAssetRefs ? 256 : 0)
                        | (info.HasWeakAssetRefs ? 512 : 0) | (info.HasUnityObjectRefs ? 1024 : 0);
                    var row = new JArray(TypeManager.GetType(index).FullName, (int)info.Category, info.SizeInChunk,
                        info.ElementSize, info.BufferCapacity, info.EntityOffsetCount, info.BlobAssetRefOffsetCount,
                        info.WeakAssetRefOffsetCount, info.UnityObjectRefOffsetCount, flags);
                    metadata.Add(row); entityReferences += info.EntityOffsetCount;
                    if (flags != 0 || info.EntityOffsetCount != 0 || info.BlobAssetRefOffsetCount != 0
                        || info.WeakAssetRefOffsetCount != 0 || info.UnityObjectRefOffsetCount != 0) exceptional.Add(row.DeepClone());
                }
                result["metadataComplete"] = true; result["metadataTypesExcludingEntity"] = types.Count;
                result["typeSetSha256"] = NativeEntityArchetypes.TypeDigest(types);
                result["metadataSha256"] = NativeEntityArchetypes.Digest(metadata.ToString(Formatting.None));
                result["entityReferenceCount"] = entityReferences; result["exceptionalTypes"] = exceptional;
                var targets = new JObject();
                targets["PrefabRef"] = Target<PrefabRef>(types, true);
                targets["Transform"] = Target<NativeTransform>(types, true);
                if (geometry) targets["PseudoRandomSeed"] = Target<PseudoRandomSeed>(types, true);
                if (tree)
                {
                    targets["Decoration"] = Target<Game.Objects.Decoration>(types, prefabEnabled == true, "enabled-prefab-tree", true);
                    targets["Tree"] = Target<Game.Objects.Tree>(types, false, "enabled-prefab-and-original-tree-copy");
                }
                if (stack) targets["Stack"] = Target<Game.Objects.Stack>(types, prefabEnabled == true, "enabled-prefab-stack");
                if (streetLight) targets["StreetLight"] = Target<Game.Objects.StreetLight>(types, true);
                if (spawnLocation) targets["SpawnLocation"] = Target<Game.Objects.SpawnLocation>(types, true);
                result["setTargets"] = targets;
                result["conditionalSetTargetsRequireRuntimeBranch"] = tree || stack;
                Guard(); return result;
            }

            // Presence and actual TypeInfo are facts about the complete original set;
            // no missing native Set target is silently added to an archetype here.
            private JObject Target<T>(Dictionary<TypeIndex, ComponentType> types, bool required, string? nativeCondition = null, bool enabledWrite = false) where T : unmanaged, IComponentData
            {
                TypeIndex index = TypeManager.GetTypeIndex<T>(); bool present = types.ContainsKey(index);
                var result = new JObject { ["present"] = present, ["requiredWhenReached"] = required };
                if (nativeCondition != null) result["nativeCondition"] = nativeCondition;
                if (enabledWrite) result["operation"] = "SetComponentEnabled";
                if (present)
                {
                    ref readonly TypeManager.TypeInfo info = ref TypeManager.GetTypeInfo(index);
                    result["bytes"] = info.SizeInChunk; result["entityReferences"] = info.EntityOffsetCount;
                    result["enableable"] = index.IsEnableable;
                }
                else if (required) Unknown("CHILD_SET_TARGET_MISSING:" + typeof(T).Name);
                return result;
            }

            private JObject SelectionInputs()
            {
                var result = new JObject { ["count"] = selections.Count };
                if (!Reserve(selections.Count, "select-only-targets")) { result["complete"] = false; return result; }
                var values = new JArray();
                foreach (Entity selection in selections)
                {
                    bool exists = Entities.Exists(selection); Guard();
                    var item = new JObject { ["entity"] = Id(selection), ["exists"] = exists };
                    if (exists)
                    {
                        item["theme"] = Has<ThemeData>(selection);
                        item["companyBrands"] = Table<CompanyBrandElement>(selection, "company-brands", value => Id(value.m_Brand));
                        item["affiliatedBrands"] = Table<AffiliatedBrandElement>(selection, "affiliated-brands", value => Id(value.m_Brand));
                    }
                    else Unknown("SELECT_ONLY_TARGET_UNAVAILABLE", true);
                    values.Add(item);
                }
                result["complete"] = true; result["values"] = values; return result;
            }

            private JObject RequirementContext(Entity entity, Entity ownerPrefab)
            {
                // Native UpdateSubObjects uses Transform presence for isObject;
                // the common Object marker does not identify this branch.
                bool isObject = Has<NativeTransform>(entity);
                Entity effective = entity;
                if (Read(entity, out Owner owner))
                {
                    if (owner.m_Owner == Entity.Null)
                    {
                        if (!isObject) { Unknown("REQUIREMENT_OWNER_NULL"); effective = Entity.Null; }
                    }
                    else if (!Own(owner.m_Owner)) { Unknown("FOREIGN_REQUIREMENT_OWNER_NOT_FOLLOWED"); effective = Entity.Null; }
                    else if (!isObject || Has<Game.Areas.Area>(owner.m_Owner))
                    {
                        effective = owner.m_Owner;
                        if (Read(effective, out Game.Objects.Attachment attachment))
                        {
                            effective = attachment.m_Attached;
                            if (effective != Entity.Null && !Own(effective))
                            { Unknown("FOREIGN_REQUIREMENT_ATTACHMENT_NOT_FOLLOWED"); effective = Entity.Null; }
                        }
                        // An area-owned object has another native owner hop. This survey
                        // deliberately does not approximate that branch with one hop.
                        if (isObject) { Unknown("AREA_REQUIREMENT_OWNER_BRANCH_NOT_EXPANDED"); effective = Entity.Null; }
                    }
                }
                var result = new JObject { ["entity"] = Id(entity), ["ownerPrefabArgument"] = Id(ownerPrefab),
                    ["effectiveOwner"] = Id(effective), ["contextRead"] = effective != Entity.Null };
                if (effective == Entity.Null) return result;
                bool service = Has<Game.City.CityServiceUpkeep>(effective);
                result["cityServiceUpkeep"] = service;
                result["residentialProperty"] = Has<Game.Buildings.ResidentialProperty>(effective);
                result["garbageFacility"] = Has<Game.Buildings.GarbageFacility>(effective);
                result["accumulatedSnow"] = Read(effective, out Game.Objects.Surface surface) ? new JValue(surface.m_AccumulatedSnow) : JValue.CreateNull();
                result["renters"] = Length<Game.Buildings.Renter>(effective, true);
                if (service)
                {
                    // Native code keeps the original ownerPrefab argument after changing owner.
                    result["serviceUpkeep"] = Table<ServiceUpkeepData>(ownerPrefab, "service-upkeep", value =>
                        new JObject { ["resource"] = (ulong)value.m_Upkeep.m_Resource, ["amount"] = value.m_Upkeep.m_Amount, ["scaleWithUsage"] = value.m_ScaleWithUsage });
                    result["storageCompany"] = Read(ownerPrefab, out StorageCompanyData storage)
                        ? new JObject { ["storedResources"] = (ulong)storage.m_StoredResources,
                            ["transportInterval"] = new JArray(storage.m_TransportInterval.x, storage.m_TransportInterval.y) } : JValue.CreateNull();
                }
                return result;
            }

            private JObject Construction(Entity entity, Entity prefab)
            {
                bool branch = Has<NativeTransform>(entity) && Read(entity, out Game.Objects.UnderConstruction under) && under.m_NewPrefab == Entity.Null;
                bool building = Has<BuildingData>(prefab);
                var result = new JObject { ["entity"] = Id(entity), ["underConstructionBranch"] = branch, ["buildingData"] = building };
                if (!branch || !building) return result;
                Unknown("CONSTRUCTION_CHILD_GRAPH_NOT_EXPANDED");
                if (Read(prefab, out ObjectGeometryData geometry))
                    result["geometry"] = GeometryInputs(geometry);
                else { result["geometry"] = JValue.CreateNull(); Unknown("CONSTRUCTION_GEOMETRY_UNAVAILABLE", true); }
                result["subMeshes"] = Table<SubMesh>(prefab, "construction-submeshes", value =>
                    new JObject { ["mesh"] = Id(value.m_SubMesh), ["position"] = Vec(value.m_Position), ["rotation"] = Vec(value.m_Rotation.value),
                        ["flags"] = (uint)value.m_Flags, ["localeFlags"] = (uint)value.m_LocaleFlags, ["seed"] = value.m_RandomSeed });
                return result;
            }

            private JObject EnvironmentInputs()
            {
                var tool = source.GetExistingSystemManaged<Game.Tools.ToolSystem>(); Guard();
                var city = source.GetExistingSystemManaged<Game.City.CityConfigurationSystem>(); Guard();
                var result = new JObject { ["toolSystemAvailable"] = tool != null, ["cityConfigurationAvailable"] = city != null,
                    ["isEditor"] = tool != null ? new JValue(tool.actionMode.IsEditor()) : JValue.CreateNull(),
                    ["defaultTheme"] = city != null ? Id(city.defaultTheme) : JValue.CreateNull() };
                Guard();
                if (tool == null || city == null) Unknown("ENVIRONMENT_INPUT_UNAVAILABLE", true);
                EntityQuery query = default;
                bool acquired = false;
                try
                {
                    query = Entities.CreateEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>()); acquired = true; Guard();
                    query.CompleteDependency(); Guard();
                    int count = query.CalculateEntityCount(); Guard(); result["buildingConfigurationCount"] = count;
                    if (count == 1)
                    {
                        BuildingConfigurationData configuration = query.GetSingleton<BuildingConfigurationData>(); Guard();
                        result["constructionObject"] = Id(configuration.m_ConstructionObject);
                        result["defaultRenterBrand"] = Id(configuration.m_DefaultRenterBrand);
                    }
                    else Unknown("BUILDING_CONFIGURATION_NOT_UNIQUE");
                }
                finally { if (acquired) query.Dispose(); }
                Guard(); return result;
            }

            private JObject Queries() => new JObject
            {
                ["updateObjects"] = Query(new[] { ComponentType.ReadOnly<LiveSubObject>() },
                    new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() }, Array.Empty<ComponentType>(), false),
                ["updateEvents"] = Query(new[] { ComponentType.ReadOnly<Game.Common.Event>() },
                    new[] { ComponentType.ReadOnly<Game.Buildings.RentersUpdated>(), ComponentType.ReadOnly<Game.Objects.SubObjectsUpdated>() }, Array.Empty<ComponentType>(), true),
                ["tempObjects"] = Query(new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Updated>() }, Array.Empty<ComponentType>(), new[] { ComponentType.ReadOnly<Deleted>() }, false)
            };

            private JObject Query(ComponentType[] all, ComponentType[] any, ComponentType[] none, bool eventBranch)
            {
                var scopedAll = new ComponentType[all.Length + 2]; Array.Copy(all, scopedAll, all.Length);
                scopedAll[all.Length] = ComponentType.ReadOnly<Disabled>(); scopedAll[all.Length + 1] = ComponentType.ReadOnly<ObjectGenerationScope>();
                EntityQuery query = default; bool acquired = false;
                var result = new JObject();
                try
                {
                    query = Entities.CreateEntityQuery(new EntityQueryDesc { All = scopedAll, Any = any, None = none,
                        Options = EntityQueryOptions.IncludeDisabledEntities }); acquired = true; Guard();
                    query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor }); Guard();
                    query.CompleteDependency(); Guard();
                    int count = query.CalculateEntityCount(); Guard(); result["count"] = count;
                    if (eventBranch && count != 0) Unknown("EVENT_BRANCH_NOT_EMPTY", true);
                    if (count > 2) { Unknown("QUERY_MEMBER_BUDGET", true); result["complete"] = false; return result; }
                    NativeArray<Entity> members = query.ToEntityArray(Allocator.Temp);
                    try
                    {
                        Guard();
                        if (members.Length != count) throw new InvalidOperationException("SUBOBJECT_SURVEY_QUERY_CHANGED");
                        var ids = new JArray(); bool ownOnly = true;
                        for (int index = 0; index < members.Length; index++) { ids.Add(Id(members[index])); ownOnly &= Own(members[index]); }
                        result["members"] = ids; result["ownOnly"] = ownOnly; result["complete"] = true;
                        if (!ownOnly) Unknown("FOREIGN_QUERY_MEMBER_NOT_FOLLOWED");
                    }
                    finally { members.Dispose(); }
                }
                finally { if (acquired) query.Dispose(); }
                Guard(); return result;
            }

            private bool Own(Entity value) => value == root || value == node;
            private static JArray Id(Entity value) => new JArray(value.Index, value.Version);
            private static JArray Vec(float2 value) => new JArray(value.x, value.y);
            private static JArray Vec(float3 value) => new JArray(value.x, value.y, value.z);
            private static JArray Vec(float4 value) => new JArray(value.x, value.y, value.z, value.w);
        }
    }
}
#endif
