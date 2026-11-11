#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    /// <summary>Physical ownership of at most six exact entities and their output slots.</summary>
    internal sealed class NativeEntityBatch
    {
        private readonly World source;
        private readonly Receipt[] receipts;
        private NativeArray<Entity> slots;
        private bool allocationAttempted, allocationReturned, releaseCalled, worldOwnershipTransferred;
        private bool outputsReadBeforeRelease, slotsReleaseAttempted, slotsReleaseReturned;
        private string? failureCode, exceptionType;

        // No native allocation in the constructor: the caller can retain this
        // owner before the first allocation or structural operation is entered.
        internal NativeEntityBatch(World source, int capacity)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            if (capacity < 1 || capacity > 6) throw new ArgumentOutOfRangeException(nameof(capacity));
            receipts = new Receipt[capacity];
            for (int index = 0; index < capacity; index++) receipts[index] = new Receipt();
        }

        internal string? FailureCode => failureCode;
        internal string? ExceptionType => exceptionType;
        internal Entity EntityAt(int slot) => At(slot).Handle;
        internal bool CreationAttempted(int slot) => At(slot).CreateAttempted;
        internal bool CreationReturned(int slot) => At(slot).CreateReturned;
        internal bool DestructionReturned(int slot) => At(slot).DestroyReturned;
        // Physical absence is narrower than successful release: a structural
        // tail may throw after the entity is already proven gone. Never infer
        // absence from a missing Source or an unobserved create output.
        internal bool AbsenceConfirmed(int slot)
        {
            Receipt receipt = At(slot);
            return !receipt.CreateAttempted || receipt.HandleObserved && receipt.AbsentObserved;
        }
        internal bool UnknownCreation
        {
            get
            {
                foreach (Receipt receipt in receipts)
                    if (receipt.CreateAttempted && !receipt.HandleObserved) return true;
                return false;
            }
        }
        internal bool StorageReleased => !allocationAttempted || allocationReturned && slotsReleaseReturned;
        internal bool EntitiesDestroyed
        {
            get
            {
                foreach (Receipt receipt in receipts) if (!Released(receipt)) return false;
                return !UnknownCreation;
            }
        }
        internal bool ResourcesReleased
        {
            get
            {
                return StorageReleased && (worldOwnershipTransferred || EntitiesDestroyed);
            }
        }

        internal Entity Create(int slot, EntityArchetype archetype)
        {
            RequireMainThread();
            Receipt receipt = At(slot);
            Require(!releaseCalled && !slotsReleaseAttempted && !receipt.Requested, "NATIVE_ENTITY_BATCH_CREATE_REPEATED");
            receipt.Requested = true;
            try
            {
                Require(source.IsCreated, "NATIVE_ENTITY_BATCH_SOURCE_UNAVAILABLE");
                AllocateSlots();
                EntityManager entities = source.EntityManager;
                NativeArray<Entity> output = slots.GetSubArray(slot, 1);
                receipt.CreateAttempted = true;
                try
                {
                    // The public API writes this preheld slot before its
                    // EndStructuralChanges step. The slice is only an alias.
                    entities.CreateEntity(archetype, output);
                    receipt.CreateReturned = true;
                }
                catch (Exception error)
                { RecordFailure(receipt, "NATIVE_ENTITY_BATCH_CREATE_FAILED", error); throw; }
                finally
                {
                    try { ObserveHandle(receipt, slot); }
                    catch (Exception error)
                    { RecordFailure(receipt, "NATIVE_ENTITY_BATCH_OUTPUT_READ_FAILED", error); throw; }
                }
                Require(receipt.HandleObserved, "NATIVE_ENTITY_BATCH_HANDLE_UNCONFIRMED");
                return receipt.Handle;
            }
            catch (Exception error)
            { RecordFailure(receipt, "NATIVE_ENTITY_BATCH_CREATE_UNCONFIRMED", error); throw; }
        }

        private void AllocateSlots()
        {
            if (allocationReturned) return;
            Require(!allocationAttempted, "NATIVE_ENTITY_BATCH_ALLOCATION_UNCONFIRMED");
            allocationAttempted = true;
            try
            {
                slots = new NativeArray<Entity>(receipts.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                allocationReturned = true;
            }
            catch (Exception error)
            { RecordFailure(null, "NATIVE_ENTITY_BATCH_ALLOCATION_FAILED", error); throw; }
        }

        private void ObserveHandle(Receipt receipt, int slot)
        {
            receipt.OutputReadReturned = false;
            Entity observed = slots[slot];
            Require(!receipt.HandleObserved || receipt.Handle == observed, "NATIVE_ENTITY_BATCH_OUTPUT_CHANGED");
            if (observed != Entity.Null)
            {
                Require(observed.Index >= 0, "NATIVE_ENTITY_BATCH_OUTPUT_INVALID");
                receipt.Handle = observed; receipt.HandleObserved = true;
            }
            receipt.OutputReadReturned = true;
        }

        // The caller must first join any consumer of these entities. This
        // owner has no job, query, component, playback or scope authority.
        internal void Release(Action? beforeSourceAccess = null) =>
            ReleaseBySlot(beforeSourceAccess == null ? null : _ => beforeSourceAccess());

        internal void ReleaseBySlot(Action<int>? beforeSlotAccess)
        {
            RequireMainThread();
            if (releaseCalled) return;
            releaseCalled = true;
            RefreshOutputs();
            for (int index = receipts.Length - 1; index >= 0; index--)
            {
                int slot = index;
                ReleaseEntity(receipts[slot], beforeSlotAccess == null ? null : () => beforeSlotAccess(slot));
            }
            ReleaseSlots();
        }

        // The enclosing cancellation already destroyed its complete group.
        // Retire only these exact slots after independently observing absence.
        internal void ConfirmGroupRemoval(Action requireScope)
        {
            RequireMainThread();
            Require(!releaseCalled && RefreshOutputs() && !UnknownCreation, "NATIVE_ENTITY_BATCH_GROUP_RELEASE_UNAVAILABLE");
            releaseCalled = true;
            requireScope();
            Require(source.IsCreated, "NATIVE_ENTITY_BATCH_RELEASE_SOURCE_UNAVAILABLE");
            EntityManager entities = source.EntityManager;
            foreach (Receipt receipt in receipts)
            {
                Require(receipt.CreateReturned && receipt.HandleObserved && !entities.Exists(receipt.Handle),
                    "NATIVE_ENTITY_BATCH_GROUP_ABSENCE_UNCONFIRMED");
                requireScope();
                receipt.ReleaseSourceChecked = receipt.ReleaseSourceAvailable = receipt.AbsentObserved = true;
            }
            ReleaseSlots();
            Require(ResourcesReleased, "NATIVE_ENTITY_BATCH_GROUP_RELEASE_UNCONFIRMED");
        }

        internal void ConfirmWorldTransfer(Action requireScope)
        {
            RequireMainThread();
            Require(!releaseCalled && RefreshOutputs() && !UnknownCreation, "NATIVE_ENTITY_BATCH_TRANSFER_UNAVAILABLE");
            releaseCalled = true;
            requireScope();
            Require(source.IsCreated, "NATIVE_ENTITY_BATCH_TRANSFER_SOURCE_UNAVAILABLE");
            var entities = source.EntityManager;
            foreach (Receipt receipt in receipts)
            {
                Require(receipt.CreateReturned && receipt.HandleObserved && entities.Exists(receipt.Handle)
                    && !entities.HasComponent<Game.Tools.Temp>(receipt.Handle) && !entities.HasComponent<Disabled>(receipt.Handle)
                    && !entities.HasComponent<ObjectGenerationScope>(receipt.Handle), "NATIVE_ENTITY_BATCH_TRANSFER_UNCONFIRMED");
                requireScope();
            }
            worldOwnershipTransferred = true;
            ReleaseSlots();
            Require(ResourcesReleased, "NATIVE_ENTITY_BATCH_TRANSFER_STORAGE_UNCONFIRMED");
        }

        internal void ConfirmMixedTransfer(Entity survivor, Action requireScope)
        {
            RequireMainThread();
            Require(!releaseCalled && RefreshOutputs() && !UnknownCreation, "NATIVE_ENTITY_BATCH_TRANSFER_UNAVAILABLE");
            releaseCalled = true;
            requireScope();
            Require(source.IsCreated && survivor != Entity.Null, "NATIVE_ENTITY_BATCH_TRANSFER_SOURCE_UNAVAILABLE");
            var entities = source.EntityManager;
            bool transferred = false;
            foreach (Receipt receipt in receipts)
            {
                Require(receipt.CreateReturned && receipt.HandleObserved, "NATIVE_ENTITY_BATCH_TRANSFER_UNCONFIRMED");
                if (receipt.Handle == survivor)
                {
                    Require(!transferred && entities.Exists(survivor) && !entities.HasComponent<Game.Tools.Temp>(survivor)
                        && !entities.HasComponent<Disabled>(survivor) && !entities.HasComponent<ObjectGenerationScope>(survivor)
                        && !entities.HasComponent<Game.Common.Deleted>(survivor), "NATIVE_ENTITY_BATCH_TRANSFER_UNCONFIRMED");
                    transferred = true;
                }
                else
                {
                    Require(!entities.Exists(receipt.Handle), "NATIVE_ENTITY_BATCH_GROUP_ABSENCE_UNCONFIRMED");
                    receipt.ReleaseSourceChecked = receipt.ReleaseSourceAvailable = receipt.AbsentObserved = true;
                }
                requireScope();
            }
            Require(transferred, "NATIVE_ENTITY_BATCH_TRANSFER_UNCONFIRMED");
            worldOwnershipTransferred = true;
            ReleaseSlots();
            Require(ResourcesReleased, "NATIVE_ENTITY_BATCH_TRANSFER_STORAGE_UNCONFIRMED");
        }

        // Domain owners may inspect recovered handles before authorizing a
        // destroy that could otherwise cascade through LinkedEntityGroup.
        internal bool RefreshOutputs()
        {
            RequireMainThread();
            if (!allocationReturned) return !allocationAttempted;
            if (slotsReleaseAttempted) return slotsReleaseReturned && outputsReadBeforeRelease;
            bool allRead = true;
            for (int index = 0; index < receipts.Length; index++)
            {
                try { ObserveHandle(receipts[index], index); }
                catch (Exception error)
                { allRead = false; RecordFailure(receipts[index], "NATIVE_ENTITY_BATCH_OUTPUT_READ_FAILED", error); }
            }
            outputsReadBeforeRelease = allRead;
            return allRead;
        }

        // This frees only this owner's output array, never an entity. A failed
        // or refused domain cleanup keeps its cached handle/unknown obligation.
        internal void ReleaseSlots()
        {
            RequireMainThread();
            if (slotsReleaseAttempted) return;
            if (allocationReturned && RefreshOutputs())
            {
                slotsReleaseAttempted = true;
                try { slots.Dispose(); slotsReleaseReturned = true; }
                catch (Exception error)
                { RecordFailure(null, "NATIVE_ENTITY_BATCH_SLOTS_RELEASE_FAILED", error); }
            }
        }

        private void ReleaseEntity(Receipt receipt, Action? beforeSourceAccess)
        {
            if (!receipt.CreateAttempted || !receipt.HandleObserved) return;
            if (!GuardReleaseAccess(receipt, beforeSourceAccess)) return;
            try
            {
                receipt.ReleaseSourceChecked = true;
                receipt.ReleaseSourceAvailable = source.IsCreated;
                Require(receipt.ReleaseSourceAvailable, "NATIVE_ENTITY_BATCH_RELEASE_SOURCE_UNAVAILABLE");
                EntityManager entities = source.EntityManager;
                if (!entities.Exists(receipt.Handle)) { receipt.AbsentObserved = true; return; }
                receipt.DestroyAttempted = true;
                try { entities.DestroyEntity(receipt.Handle); receipt.DestroyReturned = true; }
                catch (Exception error)
                { RecordReleaseFailure(receipt, "NATIVE_ENTITY_BATCH_DESTROY_FAILED", error); throw; }
                // Destroy's structural tail can synchronously invoke SDK
                // callbacks. Do not read absence from a later load generation.
                finally
                { if (GuardReleaseAccess(receipt, beforeSourceAccess)) receipt.AbsentObserved = !entities.Exists(receipt.Handle); }
                Require(receipt.AbsentObserved, "NATIVE_ENTITY_BATCH_ENTITY_STILL_PRESENT");
            }
            catch (Exception error)
            {
                RecordReleaseFailure(receipt, receipt.ReleaseSourceAvailable
                    ? "NATIVE_ENTITY_BATCH_DESTROY_UNCONFIRMED" : "NATIVE_ENTITY_BATCH_RELEASE_SOURCE_UNAVAILABLE", error);
            }
        }

        private bool GuardReleaseAccess(Receipt receipt, Action? beforeSourceAccess)
        {
            try { beforeSourceAccess?.Invoke(); return true; }
            catch (Exception error)
            { RecordReleaseFailure(receipt, "NATIVE_ENTITY_BATCH_RELEASE_GUARD_FAILED", error); return false; }
        }

        private static bool Released(Receipt receipt) => !receipt.CreateAttempted
            || receipt.HandleObserved && receipt.AbsentObserved && !receipt.ReleaseFailed
                && (!receipt.DestroyAttempted || receipt.DestroyReturned);

        // All projections use recorded values only, including after native
        // storage or the Source World has gone away.
        internal JObject ReceiptSnapshot(int slot)
        {
            Receipt receipt = At(slot);
            return new JObject {
                ["index"] = receipt.Handle.Index, ["version"] = receipt.Handle.Version,
                ["requested"] = receipt.Requested, ["createAttempted"] = receipt.CreateAttempted,
                ["createReturned"] = receipt.CreateReturned, ["handleObserved"] = receipt.HandleObserved,
                ["outputReadReturned"] = receipt.OutputReadReturned,
                ["releaseSourceChecked"] = receipt.ReleaseSourceChecked,
                ["releaseSourceAvailable"] = receipt.ReleaseSourceAvailable,
                ["destroyAttempted"] = receipt.DestroyAttempted, ["destroyReturned"] = receipt.DestroyReturned,
                ["absentObserved"] = receipt.AbsentObserved, ["releaseFailed"] = receipt.ReleaseFailed,
                ["failureCode"] = Value(receipt.FailureCode), ["exceptionType"] = Value(receipt.ExceptionType),
                ["releaseFailureCode"] = Value(receipt.ReleaseFailureCode), ["releaseExceptionType"] = Value(receipt.ReleaseExceptionType)
            };
        }
        internal JObject SlotsSnapshot() => new JObject {
            ["capacity"] = receipts.Length, ["allocationAttempted"] = allocationAttempted,
            ["allocationReturned"] = allocationReturned, ["releaseCalled"] = releaseCalled,
            ["readBeforeReleaseReturned"] = outputsReadBeforeRelease,
            ["releaseAttempted"] = slotsReleaseAttempted, ["releaseReturned"] = slotsReleaseReturned,
            ["unknownCreation"] = UnknownCreation, ["resourcesReleased"] = ResourcesReleased,
            ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType)
        };

        internal JObject ReceiptSummary(int slot) => Select(ReceiptSnapshot(slot), "index", "version", "createAttempted",
            "createReturned", "handleObserved", "outputReadReturned", "destroyAttempted", "destroyReturned", "absentObserved",
            "releaseFailed", "releaseFailureCode", "releaseExceptionType");
        internal JObject SlotsSummary() => Select(SlotsSnapshot(), "allocationAttempted", "allocationReturned",
            "readBeforeReleaseReturned", "releaseAttempted", "releaseReturned");
        private static JObject Select(JObject value, params string[] fields)
        {
            var result = new JObject();
            foreach (string field in fields) result[field] = value[field]?.DeepClone() ?? JValue.CreateNull();
            return result;
        }

        private void RecordFailure(Receipt? receipt, string code, Exception error)
        {
            failureCode ??= code; exceptionType ??= error.GetType().Name;
            if (receipt != null)
            { receipt.FailureCode ??= code; receipt.ExceptionType ??= error.GetType().Name; }
        }
        private void RecordReleaseFailure(Receipt receipt, string code, Exception error)
        {
            receipt.ReleaseFailed = true;
            receipt.ReleaseFailureCode ??= code; receipt.ReleaseExceptionType ??= error.GetType().Name;
            RecordFailure(receipt, code, error);
        }
        private Receipt At(int slot)
        {
            if (slot < 0 || slot >= receipts.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            return receipts[slot];
        }
        private sealed class Receipt
        {
            internal Entity Handle;
            internal bool Requested, CreateAttempted, CreateReturned, HandleObserved, OutputReadReturned;
            internal bool ReleaseSourceChecked, ReleaseSourceAvailable, DestroyAttempted, DestroyReturned, AbsentObserved, ReleaseFailed;
            internal string? FailureCode, ExceptionType, ReleaseFailureCode, ReleaseExceptionType;
        }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() => Require(MainThreadDispatcher.isMainThread, "NATIVE_ENTITY_BATCH_MAIN_THREAD_REQUIRED");
        private static void Require(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
    }
}
#endif
