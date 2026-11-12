#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using Colossal.Collections;
using Colossal.Core;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace SkylinesLink.Mod.Native
{
    // Admission for this one managed, synchronous root1/course1 observation.
    // The source tree is borrowed, never copied, changed or disposed here.
    internal sealed class ObjectGenerationPreflight
    {
        private const string BoundsCode = "OBJECT_GENERATION_PREFLIGHT";
        private World? admittedSource;
        private ObjectDefinitionPayload? admittedPayload;
        private EntityArchetype archetype;
        private bool ran, finished, passed, versionsQualified, shapeQualified, boundsObserved;
        private bool writersCompleted, treeReadStarted, treeReadCompleted, sourceTreeReadCompleted;
        private bool scopeRechecked, hitObserved;
        private int archetypeTypes, threadIndexCount, nodesVisited, itemsVisited;
        private float radius;
        private float3 boundsMin, boundsMax;
        private string? failureCode, exceptionType;
        private double elapsedMilliseconds;
        private bool fixtureAllocationAttempted, fixtureAllocationReturned, fixtureReleaseAttempted, fixtureReleaseReturned;
        private bool fixturePassed, fixtureItemAbort, fixtureNodeAbort, fixtureDataUnchanged;
        private int fixtureItemsAdded, fixtureItemCallbacks, fixtureNodeCallbacks;

        internal bool Passed => passed;
        internal string? FailureCode => failureCode;
        // The qualification tree is owned; the source tree is only borrowed.
        // A managed callback exception unwinds the read before Run returns.
        internal bool ResourcesReleased => (!fixtureAllocationAttempted || fixtureAllocationReturned && fixtureReleaseReturned)
            && (!treeReadStarted || sourceTreeReadCompleted);
        internal bool SourceTreeReadCompleted => sourceTreeReadCompleted;
        internal int OutputCommandUpperBound => 64;
        internal EntityArchetype Archetype => archetype;
        internal bool Matches(World source, ObjectDefinitionPayload payload) => passed
            && ReferenceEquals(admittedSource, source) && ReferenceEquals(admittedPayload, payload);

        internal void Run(World source, ObjectDefinitionPayload payload, Action requireCurrentScope)
        {
            if (ran) { passed = false; failureCode ??= "OBJECT_GENERATION_PREFLIGHT_ALREADY_RAN"; return; }
            ran = true;
            var timer = Stopwatch.StartNew();
            try
            {
                Require(MainThreadDispatcher.isMainThread, "OBJECT_GENERATION_PREFLIGHT_MAIN_THREAD_REQUIRED");
                if (source == null || !source.IsCreated || payload == null || !ReferenceEquals(payload.Source, source)
                    || requireCurrentScope == null)
                    throw new Rejection("OBJECT_GENERATION_PREFLIGHT_SOURCE_UNAVAILABLE");
                versionsQualified = SearchVersionsQualified;
                Require(versionsQualified, "OBJECT_GENERATION_PREFLIGHT_NATIVE_BUILD_UNQUALIFIED");
                requireCurrentScope();
                QualifyManagedAbort();
                Require(IsSupportedShape(payload), "OBJECT_GENERATION_PREFLIGHT_PAYLOAD_OUTSIDE_PROBE");
                shapeQualified = true;

                EntityManager entities = source.EntityManager;
                RequirePrefab(entities, payload.RootCreation.m_Prefab);
                RequirePrefab(entities, payload.CourseCreation.m_Prefab);
                Require(entities.HasComponent<ObjectData>(payload.RootCreation.m_Prefab)
                    && entities.HasComponent<NetData>(payload.CourseCreation.m_Prefab),
                    "OBJECT_GENERATION_PREFLIGHT_PREFAB_DATA_UNAVAILABLE");
                archetype = entities.GetComponentData<ObjectData>(payload.RootCreation.m_Prefab).m_Archetype;
                Require(archetype.Valid, "OBJECT_GENERATION_PREFLIGHT_ARCHETYPE_UNAVAILABLE");
                archetypeTypes = archetype.TypesCount;
                threadIndexCount = JobsUtility.ThreadIndexCount;
                Require(archetypeTypes > 0 && archetypeTypes <= 256 && threadIndexCount > 0 && threadIndexCount <= 256,
                    "OBJECT_GENERATION_PREFLIGHT_NATIVE_ALLOCATION_BUDGET");

                Bounds3 bounds = CourseSearchBounds(entities, payload.CourseCreation.m_Prefab,
                    payload.Course.m_StartPosition, out radius);
                boundsMin = bounds.min; boundsMax = bounds.max; boundsObserved = true;
                var search = new CourseSearch(bounds, BoundsCode, Entity.Null);
                try { search.Walk(source); }
                finally
                {
                    nodesVisited = search.Nodes; itemsVisited = search.Items; hitObserved = search.Hit;
                    writersCompleted = search.WritersCompleted; treeReadStarted = search.Started;
                    treeReadCompleted = search.Completed; sourceTreeReadCompleted = search.Unwound;
                }

                requireCurrentScope(); scopeRechecked = true;
                admittedSource = source; admittedPayload = payload; passed = true;
            }
            catch (EmptyNetBoundsVisitor.Rejection error) { failureCode = error.Code; }
            catch (Rejection error) { failureCode = error.Code; }
            catch (Exception error)
            {
                failureCode = "OBJECT_GENERATION_PREFLIGHT_SOURCE_FAILED";
                string type = error.GetType().FullName ?? error.GetType().Name;
                exceptionType = type.Length <= 128 ? type : type.Substring(0, 128);
            }
            finally { timer.Stop(); elapsedMilliseconds = timer.Elapsed.TotalMilliseconds; finished = true; }
        }

        internal static bool SearchVersionsQualified =>
            NativeConstructionBuild.ColossalCollections(typeof(NativeQuadTree<Entity, QuadTreeBoundsXZ>).Module)
            && NativeConstructionBuild.Mathematics(typeof(MathUtils).Module);

        internal static Bounds3 CourseSearchBounds(EntityManager entities, Entity prefab, CoursePos start, out float radius)
        {
            NetGeometryData geometry = entities.HasComponent<NetGeometryData>(prefab)
                ? entities.GetComponentData<NetGeometryData>(prefab) : default;
            Require(Finite(geometry.m_DefaultWidth) && geometry.m_DefaultWidth >= 0f,
                "OBJECT_GENERATION_PREFLIGHT_SEARCH_METADATA_INVALID");
            float halfWidth = geometry.m_DefaultWidth * 0.5f;
            float searchDistance = 0f;
            float3 position = start.m_Position;
            float roadHeight = position.y - math.cmin(start.m_Elevation);
            float minimumY = roadHeight - halfWidth, maximumY = roadHeight + halfWidth;
            if (entities.HasComponent<LocalConnectData>(prefab))
            {
                LocalConnectData local = entities.GetComponentData<LocalConnectData>(prefab);
                Require(Finite(local.m_SearchDistance) && Finite(local.m_HeightRange.min)
                    && Finite(local.m_HeightRange.max) && local.m_HeightRange.min <= local.m_HeightRange.max,
                    "OBJECT_GENERATION_PREFLIGHT_SEARCH_METADATA_INVALID");
                searchDistance = math.max(0f, local.m_SearchDistance);
                minimumY = math.min(minimumY, position.y + local.m_HeightRange.min);
                maximumY = math.max(maximumY, position.y + local.m_HeightRange.max);
            }
            // This deliberately contains both native attachment/local-connect
            // branches, even when a layer flag would skip one of them.
            radius = halfWidth + searchDistance + 8f;
            var minimum = position - radius; var maximum = position + radius;
            minimum.y = minimumY; maximum.y = maximumY;
            Require(Finite(radius) && radius >= 0f && Finite(minimum) && Finite(maximum)
                && math.all(minimum <= maximum), "OBJECT_GENERATION_PREFLIGHT_SEARCH_BOUNDS_INVALID");
            return new Bounds3(minimum, maximum);
        }

        // Borrow the stock tree synchronously. In this SDK Iterate only uses
        // stack-local traversal state, so a managed callback throw ends the walk.
        internal struct CourseSearch : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            private readonly Bounds3 bounds;
            private readonly string code;
            private readonly Entity ignored;
            private EmptyNetBoundsVisitor visitor;

            internal CourseSearch(Bounds3 bounds, string code, Entity ignored)
            {
                this = default;
                this.bounds = bounds;
                this.code = code;
                this.ignored = ignored;
                visitor = new EmptyNetBoundsVisitor(bounds, code);
            }

            internal bool WritersCompleted { get; private set; }
            internal bool Started { get; private set; }
            internal bool Completed { get; private set; }
            internal bool Unwound { get; private set; }
            internal int IgnoredHits { get; private set; }
            internal int Nodes => visitor.Nodes;
            internal int Items => visitor.Items;
            internal bool Hit => visitor.Hit;

            internal void Walk(World source)
            {
                var search = source.GetExistingSystemManaged<Game.Net.SearchSystem>();
                Require(search != null && ReferenceEquals(search.World, source), code + "_SEARCH_UNAVAILABLE");
                var tree = search!.GetNetSearchTree(readOnly: true, out var dependencies);
                dependencies.Complete();
                WritersCompleted = true;
                Require(tree.IsCreated, code + "_SEARCH_UNAVAILABLE");

                Started = true;
                try { tree.Iterate(ref this); Completed = true; }
                finally { Unwound = true; }
            }

            public bool Intersect(QuadTreeBoundsXZ itemBounds) => visitor.Intersect(itemBounds);

            public void Iterate(QuadTreeBoundsXZ itemBounds, Entity item)
            {
                if (ignored == Entity.Null || item != ignored)
                {
                    visitor.Iterate(itemBounds, item);
                    return;
                }

                // The stock edge iterators skip this exact source entity.
                // Keep it in the callback budget even when it does not overlap.
                if (++visitor.Items > EmptyNetBoundsVisitor.ItemBudget)
                    throw new EmptyNetBoundsVisitor.Rejection(code + "_ITEM_BUDGET");
                if (MathUtils.Intersect(itemBounds.m_Bounds, bounds)) IgnoredHits++;
            }
        }

        private void QualifyManagedAbort()
        {
            var tree = default(NativeQuadTree<Entity, QuadTreeBoundsXZ>);
            var box = new Bounds3(new float3(-1f), new float3(1f));
            var bounds = new QuadTreeBoundsXZ(box);
            try
            {
                fixtureAllocationAttempted = true;
                tree = new NativeQuadTree<Entity, QuadTreeBoundsXZ>(4f, Allocator.Persistent);
                fixtureAllocationReturned = true;
                // These keys belong only to this container, never EntityManager.
                for (int index = 1; index <= 16; index++)
                {
                    Require(tree.TryAdd(new Entity { Index = index, Version = 1 }, bounds),
                        "OBJECT_GENERATION_PREFLIGHT_FIXTURE_ADD_FAILED");
                    fixtureItemsAdded++;
                }
                var itemVisitor = new EmptyNetBoundsVisitor(box, BoundsCode, rejectHits: false, itemLimit: 2);
                try { tree.Iterate(ref itemVisitor); }
                catch (EmptyNetBoundsVisitor.Rejection error) when (error.Code == "OBJECT_GENERATION_PREFLIGHT_ITEM_BUDGET") { fixtureItemAbort = true; }
                fixtureItemCallbacks = itemVisitor.Items;
                Require(fixtureItemAbort && fixtureItemCallbacks == 3, "OBJECT_GENERATION_PREFLIGHT_ITEM_ABORT_UNQUALIFIED");

                var nodeVisitor = new EmptyNetBoundsVisitor(box, BoundsCode, rejectHits: false, nodeLimit: 0);
                try { tree.Iterate(ref nodeVisitor); }
                catch (EmptyNetBoundsVisitor.Rejection error) when (error.Code == "OBJECT_GENERATION_PREFLIGHT_NODE_BUDGET") { fixtureNodeAbort = true; }
                fixtureNodeCallbacks = nodeVisitor.Nodes;
                Require(fixtureNodeAbort && fixtureNodeCallbacks == 1 && nodeVisitor.Items == 0,
                    "OBJECT_GENERATION_PREFLIGHT_NODE_ABORT_UNQUALIFIED");
                for (int index = 1; index <= fixtureItemsAdded; index++)
                    Require(tree.TryGet(new Entity { Index = index, Version = 1 }, out var actual) && actual.Equals(bounds),
                        "OBJECT_GENERATION_PREFLIGHT_FIXTURE_CHANGED");
                fixtureDataUnchanged = true;
                fixturePassed = true;
            }
            finally
            {
                if (fixtureAllocationReturned)
                {
                    fixtureReleaseAttempted = true;
                    tree.Dispose(); fixtureReleaseReturned = true;
                }
            }
        }

        internal static bool IsSupportedShape(ObjectDefinitionPayload payload) =>
            Detached(payload.RootCreation) && Detached(payload.CourseCreation)
                && payload.RootCreation.m_SubPrefab == Entity.Null
                && payload.RootObject.m_ParentMesh == -1 && payload.RootObject.m_Elevation == 0f
                && Finite(payload.RootObject.m_Position) && Finite(payload.RootObject.m_Rotation.value)
                && payload.CourseOwner.m_Prefab == payload.RootCreation.m_Prefab
                && payload.CourseOwner.m_Position.Equals(payload.RootObject.m_Position)
                && payload.CourseOwner.m_Rotation.Equals(payload.RootObject.m_Rotation)
                && payload.IsDetachedSingleNodeCourse && Finite(payload.Course.m_Elevation)
                && Finite(payload.Course.m_Length) && payload.Course.m_Length >= 0f
                && Finite(payload.Course.m_Curve.a) && Finite(payload.Course.m_Curve.b)
                && Finite(payload.Course.m_Curve.c) && Finite(payload.Course.m_Curve.d)
                && Finite(payload.Course.m_StartPosition) && Finite(payload.Course.m_EndPosition);

        private static bool Detached(CreationDefinition value) => value.m_Prefab != Entity.Null
            && value.m_Prefab.Index >= 0 && value.m_Flags == (CreationFlags)0 && value.m_Original == Entity.Null
            && value.m_Owner == Entity.Null && value.m_Attached == Entity.Null;
        private static void RequirePrefab(EntityManager entities, Entity prefab) => Require(entities.Exists(prefab)
            && entities.HasComponent<PrefabData>(prefab) && entities.IsComponentEnabled<PrefabData>(prefab)
            && !entities.HasComponent<Deleted>(prefab), "OBJECT_GENERATION_PREFLIGHT_PREFAB_UNAVAILABLE");
        private static bool Finite(CoursePos value) => Finite(value.m_Position) && Finite(value.m_Rotation.value)
            && Finite(value.m_Elevation) && Finite(value.m_CourseDelta) && Finite(value.m_SplitPosition);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(float2 value) => math.all(math.isfinite(value));
        private static bool Finite(float3 value) => math.all(math.isfinite(value));
        private static bool Finite(float4 value) => math.all(math.isfinite(value));
        private static void Require(bool condition, string code) { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code) : base(code)
            { Code = code; }
        }

        // Only cached ordinary facts are projected. No World, archetype or tree
        // getter is consulted by a later status reader.
        internal JObject Snapshot() => new JObject {
            ["finished"] = finished, ["passed"] = passed, ["resourcesReleased"] = ResourcesReleased,
            ["abortQualification"] = new JObject { ["passed"] = fixturePassed, ["itemsAdded"] = fixtureItemsAdded,
                ["itemAbortObserved"] = fixtureItemAbort, ["itemCallbacks"] = fixtureItemCallbacks,
                ["nodeAbortObserved"] = fixtureNodeAbort, ["nodeCallbacks"] = fixtureNodeCallbacks,
                ["dataUnchanged"] = fixtureDataUnchanged, ["allocationAttempted"] = fixtureAllocationAttempted,
                ["allocationReturned"] = fixtureAllocationReturned, ["releaseAttempted"] = fixtureReleaseAttempted,
                ["releaseReturned"] = fixtureReleaseReturned },
            ["nativeVersionsQualified"] = versionsQualified, ["shapeQualified"] = shapeQualified,
            ["archetypeTypesCount"] = archetypeTypes, ["threadIndexCount"] = threadIndexCount,
            ["outputCommandUpperBound"] = OutputCommandUpperBound,
            ["nodeBudget"] = EmptyNetBoundsVisitor.NodeBudget, ["itemBudget"] = EmptyNetBoundsVisitor.ItemBudget,
            ["nodesVisited"] = nodesVisited, ["itemsVisited"] = itemsVisited,
            ["sourceWritersCompleted"] = writersCompleted, ["sourceTreeReadStarted"] = treeReadStarted,
            ["sourceTreeWalkCompleted"] = treeReadCompleted, ["sourceTreeReadCompleted"] = sourceTreeReadCompleted,
            ["scopeRechecked"] = scopeRechecked, ["spatialHit"] = hitObserved,
            ["searchBounds"] = !boundsObserved ? JValue.CreateNull() : (JToken)new JObject {
                ["minimum"] = new JArray(boundsMin.x, boundsMin.y, boundsMin.z),
                ["maximum"] = new JArray(boundsMax.x, boundsMax.y, boundsMax.z), ["radius"] = radius },
            ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode),
            ["exceptionType"] = exceptionType == null ? JValue.CreateNull() : new JValue(exceptionType),
            ["elapsedMilliseconds"] = elapsedMilliseconds, ["privatePointerRead"] = false, ["sourceTreeModified"] = false,
            ["scope"] = "Managed single-point root1/course1 admission only; no placement, node generation, fees or Source playback."
        };
    }
}
#endif
