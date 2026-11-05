#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Game.Net;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // One source-bound observation of joined native selection and creation
    // jobs. Referenced entities and the shared prefab cache are never owned here.
    internal sealed class CompositionSelectionReceipt
    {
        private readonly Action requireCurrentScope;
        private readonly JObject snapshot;

        internal CompositionSelectionReceipt(World source, Entity node, Entity prefab, int actor,
            Orphan before, Orphan after, bool observedSelection, bool needsPublication,
            int? selectedRowIndex, NetGeometryComposition? selectedRow,
            bool nativeEnteredReturnedJoined, bool cacheUnchanged, Action requireCurrentScope)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            this.requireCurrentScope = requireCurrentScope ?? throw new ArgumentNullException(nameof(requireCurrentScope));
            Require(node != Entity.Null && node.Index >= 0 && prefab != Entity.Null && prefab.Index >= 0 && actor > 0,
                "COMPOSITION_RECEIPT_IDENTITY_INVALID");
            Require(nativeEnteredReturnedJoined && cacheUnchanged, "COMPOSITION_RECEIPT_NATIVE_RESULT_UNQUALIFIED");
            Require(observedSelection != needsPublication, "COMPOSITION_RECEIPT_BRANCH_INVALID");
            if (observedSelection)
                Require(selectedRowIndex.HasValue && selectedRowIndex.Value >= 0 && selectedRow.HasValue
                    && after.m_Composition != Entity.Null && after.m_Composition.Index >= 0
                    && after.m_Composition == selectedRow.Value.m_Composition, "COMPOSITION_RECEIPT_SELECTED_ROW_INVALID");
            else
                Require(after.Equals(before) && !selectedRowIndex.HasValue && !selectedRow.HasValue,
                    "COMPOSITION_RECEIPT_UNPUBLISHED_RESULT_CHANGED");
            Node = node; Prefab = prefab; Actor = actor; Before = before; After = after;
            ObservedSelection = observedSelection; NeedsPublication = needsPublication;
            SelectedRowIndex = selectedRowIndex; SelectedRow = selectedRow;
            NativeEnteredReturnedJoined = nativeEnteredReturnedJoined; CacheUnchanged = cacheUnchanged;
            RequireCurrentScope();
            snapshot = new JObject {
                ["node"] = Id(Node), ["prefab"] = Id(Prefab), ["actor"] = Actor,
                ["before"] = OrphanValue(Before), ["after"] = OrphanValue(After),
                ["observedSelection"] = ObservedSelection, ["needsPublication"] = NeedsPublication,
                ["selectedRowIndex"] = SelectedRowIndex.HasValue ? new JValue(SelectedRowIndex.Value) : JValue.CreateNull(),
                ["selectedRow"] = SelectedRow.HasValue ? (JToken)new JObject {
                    ["composition"] = Id(SelectedRow.Value.m_Composition),
                    ["mask"] = new JObject { ["general"] = (uint)SelectedRow.Value.m_Mask.m_General,
                        ["left"] = (uint)SelectedRow.Value.m_Mask.m_Left, ["right"] = (uint)SelectedRow.Value.m_Mask.m_Right }
                } : JValue.CreateNull(),
                ["nativeEnteredReturnedJoined"] = NativeEnteredReturnedJoined, ["cacheUnchanged"] = CacheUnchanged,
                ["ownsSourceEntities"] = false, ["cacheReadyEstablished"] = false, ["fullGeometryEstablished"] = false
            };
        }

        internal World Source { get; }
        internal Entity Node { get; }
        internal Entity Prefab { get; }
        internal int Actor { get; }
        internal Orphan Before { get; }
        internal Orphan After { get; }
        internal bool ObservedSelection { get; }
        internal bool NeedsPublication { get; }
        internal int? SelectedRowIndex { get; }
        internal NetGeometryComposition? SelectedRow { get; }
        internal bool NativeEnteredReturnedJoined { get; }
        internal bool CacheUnchanged { get; }
        internal void RequireCurrentScope() => requireCurrentScope();
        internal JObject Snapshot() => (JObject)snapshot.DeepClone();

        private static JObject Id(Entity value) => new JObject { ["index"] = value.Index, ["version"] = value.Version };
        private static JObject OrphanValue(Orphan value) => new JObject { ["composition"] = Id(value.m_Composition) };
        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
