#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.City;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylinesLink.Mod.Native
{
    // Owns only the fee producer. The commit coordinator owns every buffer
    // in the shared barrier and the transfer of the preview entities.
    internal sealed class PreviewCommitPayment
    {
        private NativeSystemRegistration<PreviewCommitPaymentProducer>? registration;
        private bool attempted, producerReleased = true, protectionConfirmed;
        private int? commandCount;
        private string? failureCode;

        internal bool EffectEntered => registration?.Instance.EffectEntered == true;
        internal bool NativeWorkMayReadSource => registration?.Instance.NativeWorkMayReadSource == true;
        internal bool ResourcesReleased => producerReleased && !NativeWorkMayReadSource;
        internal bool OutputConfirmed => registration?.Instance.PaymentConfirmed == true
            && commandCount == 0 && protectionConfirmed && failureCode == null;
        internal bool Passed => OutputConfirmed && ResourcesReleased;

        internal void RunOnce(World source, Entity[] graph, PreviewCommitBarrier output,
            List<ComponentSystemBase> appended, Action requireScope, Action guardPreparedInputs, Action observeProtection,
            Action confirmEligibility)
        {
            Require(!attempted, "PREVIEW_COMMIT_PAYMENT_ALREADY_ATTEMPTED");
            attempted = true;
            try
            {
                Require(MainThreadDispatcher.isMainThread && PreviewCommitGraph.IsNewMemberCount(graph.Length),
                    "PREVIEW_COMMIT_PAYMENT_CONTEXT_INVALID");
                requireScope();
                guardPreparedInputs();
                var instance = RegisterProducer(source, output, appended, requireScope, observeProtection);

                confirmEligibility();
                PayPrepared(instance, graph, output, requireScope, guardPreparedInputs, guardPreparedInputs, observeProtection);

            }
            catch (Exception error)
            {
                failureCode = error switch
                {
                    Rejection rejected => rejected.Code,
                    PreviewValidationExecution.Rejection rejected => rejected.Code,
                    PreviewCommitExecution.Rejection rejected => rejected.Code,
                    PreviewCommitObjects.Rejection rejected => rejected.Code,
                    _ => "PREVIEW_COMMIT_PAYMENT_FAILED"
                };
                throw new Rejection(failureCode, error);
            }
        }

        internal int AdmitRelocation(World source, Entity[] graph, Temp[] temps, int actor, PreviewCommitBarrier output,
            List<ComponentSystemBase> appended, Action requireScope, Action guardGraph, Action observeProtection)
        {
            Require(MainThreadDispatcher.isMainThread && !attempted && graph.Length == 9 && temps.Length == graph.Length,
                "PREVIEW_COMMIT_PAYMENT_RELOCATION_CONTEXT_INVALID");
            attempted = true;
            requireScope();
            guardGraph();
            var producer = RegisterProducer(source, output, appended, requireScope, observeProtection, actor, temps);
            guardGraph();
            return producer.AdmitRelocation(graph);
        }

        internal void PayRelocation(Entity[] graph, PreviewCommitBarrier output, Action requireScope,
            Action before, Action after, Action observeProtection)
        {
            Require(registration != null && registration.Instance.RelocationConfigured, "PREVIEW_COMMIT_PAYMENT_RELOCATION_NOT_READY");
            PayPrepared(registration!.Instance, graph, output, requireScope, before, after, observeProtection);
        }

        private void PayPrepared(PreviewCommitPaymentProducer instance, Entity[] graph, PreviewCommitBarrier output,
            Action requireScope, Action before, Action after, Action observeProtection)
        {
            instance.Pay((Entity[])graph.Clone(), requireScope, before, after);
            requireScope();
            Require(instance.PaymentConfirmed && output.PendingCount == instance.BufferIndex + 1,
                "PREVIEW_COMMIT_PAYMENT_OUTPUT_UNCONFIRMED");
            var abi = new ObjectDefinitionCommandProbe.Abi();
            var proxy = abi.Constructor.Invoke(new object[] { output.Borrow(instance.BufferIndex) });
            commandCount = ((Array)abi.Commands.GetValue(proxy)!).Length;
            Require(commandCount == 0, "PREVIEW_COMMIT_PAYMENT_UNEXPECTED_COMMAND");
            observeProtection();
            protectionConfirmed = true;
        }

        private PreviewCommitPaymentProducer RegisterProducer(World source, PreviewCommitBarrier output,
            List<ComponentSystemBase> appended, Action requireScope, Action observeProtection, int actor = 1, Temp[]? relocation = null)
        {
            Require(source.IsCreated && ReferenceEquals(output.World, source)
                && ReferenceEquals(source.GetExistingSystemManaged<PreviewCommitBarrier>(), output)
                && source.GetExistingSystemManaged<PreviewCommitPaymentProducer>() == null,
                "PREVIEW_COMMIT_PAYMENT_OWNER_UNAVAILABLE");
            var instance = new PreviewCommitPaymentProducer();
            instance.Configure(output, actor, relocation);
            registration = new NativeSystemRegistration<PreviewCommitPaymentProducer>(source, instance);
            producerReleased = false;
            registration.Add();
            appended.Add(instance);
            requireScope();
            observeProtection();
            return instance;
        }

        internal void ReleaseProducer(List<ComponentSystemBase> appended)
        {
            if (registration == null || NativeWorkMayReadSource) return;
            try
            {
                var owner = registration;
                owner.Destroy();
                producerReleased = owner.PhysicalReleased && owner.Instance.StorageReleased
                    && (!owner.DestroyAttempted || owner.DestroyReturned);
                if (producerReleased) appended.Remove(owner.Instance);
                else failureCode ??= "PREVIEW_COMMIT_PAYMENT_RELEASE_UNCONFIRMED";
            }
            catch (Exception) { failureCode ??= "PREVIEW_COMMIT_PAYMENT_DESTROY_FAILED"; }
        }

        internal JObject Snapshot()
        {
            JObject result = registration?.Instance.Snapshot() ?? new JObject { ["effectEntered"] = false };
            result["nativeWorkMayReadSource"] = NativeWorkMayReadSource;
            result["resourcesReleased"] = ResourcesReleased;
            result["commandCount"] = commandCount;
            result["protectionConfirmed"] = protectionConfirmed;
            result["passed"] = Passed;
            result["failureCode"] = failureCode;
            return result;
        }

        internal static void Require(bool condition, string code)
        { if (!condition) throw new Rejection(code); }

        internal sealed class Rejection : InvalidOperationException, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal readonly string Code;
            internal Rejection(string code, Exception? inner = null) : base(code, inner)
            { Code = code; }
        }
    }

    [DisableAutoCreation]
    public sealed partial class PreviewCommitPaymentProducer : ToolApplySystem
    {
        private int actor;
        private Temp[]? relocation;
        private int ExpectedOutputSlot => relocation == null
            ? PreviewCommitBarrier.PaymentBufferIndex : PreviewCommitBarrier.RelocationPaymentBufferIndex;
        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private FieldInfo? queryField, barrierField, cityField;
        private PreviewCommitBarrier? output;
        private CitySystem? citySystem;
        private EntityQuery query;
        private Entity[]? members;
        private Action? scope, liveGraph;
        private Temp[]? tempsBefore;
        private bool attempted, creationStarted, creationReturned, destructionStarted, destructionReturned;
        private bool nativeReturned, nativeJoined, chargeObserved, tempUnchanged;
        private int? queryCount, chunkCount, expectedCost, treasuryBefore, treasuryAfter;
        private Entity city;
        private DefinitionProducerPatchProbe? patches;

        internal bool EffectEntered { get; private set; }
        internal bool NativeWorkMayReadSource => EffectEntered && !nativeJoined;
        internal bool StorageReleased => !creationStarted || destructionReturned;
        internal bool PaymentConfirmed => nativeJoined && chargeObserved && tempUnchanged;
        internal bool RelocationConfigured => relocation != null;
        internal int BufferIndex { get; private set; } = -1;

        internal void Configure(PreviewCommitBarrier barrier, int actor, Temp[]? relocation)
        {
            Require(!creationStarted && output == null && World == null && actor > 0
                && (relocation == null || relocation.Length == 9), "PREVIEW_COMMIT_PAYMENT_CONFIGURATION_REJECTED");
            this.actor = actor;
            this.relocation = relocation == null ? null : (Temp[])relocation.Clone();
            RequireUnpatched();
            queryField = Field("m_ApplyQuery", typeof(EntityQuery));
            barrierField = Field("m_ToolOutputBarrier", typeof(ToolOutputBarrier));
            cityField = Field("m_CitySystem", typeof(CitySystem));
            output = barrier;
        }

        protected override void OnCreate()
        {
            creationStarted = true;
            Enabled = false;
            Require(MainThreadDispatcher.isMainThread && output != null && ReferenceEquals(output.World, World)
                && queryField != null && barrierField != null && cityField != null,
                "PREVIEW_COMMIT_PAYMENT_CONFIGURATION_MISSING");
            citySystem = World.GetExistingSystemManaged<CitySystem>()
                ?? throw new PreviewCommitPayment.Rejection("PREVIEW_COMMIT_PAYMENT_CITY_UNAVAILABLE");
            query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ObjectGenerationScope>(), ComponentType.ReadOnly<Unity.Entities.Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Warning>(), ComponentType.ReadOnly<Override>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            query.SetSharedComponentFilter(new ObjectGenerationScope { Value = actor });
            queryField!.SetValue(this, query);
            barrierField!.SetValue(this, output);
            cityField!.SetValue(this, citySystem);
            RequireForUpdate(query);
            RequireBindings();
            creationReturned = true;
        }

        protected override void OnStartRunning() { }
        protected override void OnStopRunning() { }
        protected override void OnDestroy()
        {
            destructionStarted = true;
            Require(!NativeWorkMayReadSource, "PREVIEW_COMMIT_PAYMENT_NATIVE_RELEASE_UNRESOLVED");
            destructionReturned = true;
        }

        internal int AdmitRelocation(Entity[] graph)
        {
            Require(creationReturned && !destructionStarted && !attempted && relocation != null && !Enabled
                && graph.Length == relocation.Length && ShouldRunSystem(), "PREVIEW_COMMIT_PAYMENT_RELOCATION_NOT_READY");
            RequireUnpatched();
            RequireBindings();
            Require(output!.PendingCount == ExpectedOutputSlot, "PREVIEW_COMMIT_PAYMENT_OUTPUT_SLOT_CHANGED");
            RequireMembers(graph);
            int total = ReadCost(graph);
            Require(ReadFunds(citySystem!.City) >= total, "PREVIEW_COMMIT_PAYMENT_INSUFFICIENT_FUNDS");
            return total;
        }

        internal void Pay(Entity[] graph, Action requireScope, Action before, Action after)
        {
            Require(creationReturned && !destructionStarted && !attempted
                && (relocation == null ? PreviewCommitGraph.IsNewMemberCount(graph.Length) : graph.Length == relocation.Length),
                "PREVIEW_COMMIT_PAYMENT_PRODUCTION_REJECTED");
            attempted = true;
            members = graph;
            scope = requireScope;
            liveGraph = before;
            tempsBefore = new Temp[graph.Length];
            try
            {
                Guard(requireScope);
                Require(ShouldRunSystem(), "PREVIEW_COMMIT_PAYMENT_REQUIRED_QUERY_REJECTED");
                Enabled = true;
                Update();
                Require(EffectEntered && nativeReturned, "PREVIEW_COMMIT_PAYMENT_NATIVE_NOT_RETURNED");
                JobHandle writer = Dependency;
                Require(!writer.Equals(default(JobHandle)), "PREVIEW_COMMIT_PAYMENT_WRITER_MISSING");
                writer.Complete();
                nativeJoined = true;

                Guard(requireScope);
                treasuryAfter = ReadFunds(city);
                chargeObserved = treasuryAfter == treasuryBefore!.Value - expectedCost!.Value;
                Require(chargeObserved, "PREVIEW_COMMIT_PAYMENT_BALANCE_DELTA_UNCONFIRMED");
                after();
                RequireBindings();
                RequireMembers(graph);
                for (int i = 0; i < graph.Length; i++)
                    Require(ReadTemp(graph[i], i).Equals(tempsBefore![i]), "PREVIEW_COMMIT_PAYMENT_TEMP_CHANGED");
                Guard(requireScope);
                tempUnchanged = true;
            }
            finally { Enabled = false; members = null; scope = null; liveGraph = null; }
        }

        protected override void OnUpdate()
        {
            Action requireScope = scope ?? throw new PreviewCommitPayment.Rejection("PREVIEW_COMMIT_PAYMENT_SCOPE_MISSING");
            Require(attempted && !EffectEntered, "PREVIEW_COMMIT_PAYMENT_NATIVE_REPEATED");
            RequireUnpatched();
            Guard(requireScope);
            liveGraph!();
            RequireBindings();
            Entity[] graph = members!;
            RequireMembers(graph);
            expectedCost = ReadCost(graph, tempsBefore);
            BufferIndex = output!.PendingCount;
            Require(BufferIndex == ExpectedOutputSlot, "PREVIEW_COMMIT_PAYMENT_OUTPUT_SLOT_CHANGED");

            // A live PlayerMoney read completes its existing dependencies. No
            // native scheduling, callbacks or other writes intervene before
            // entering this scoped payment job on the main thread.
            Guard(requireScope);
            city = citySystem!.City;
            treasuryBefore = ReadFunds(city);
            Require(treasuryBefore.Value >= expectedCost.Value, "PREVIEW_COMMIT_PAYMENT_INSUFFICIENT_FUNDS");
            EffectEntered = true;
            base.OnUpdate();
            nativeReturned = true;
        }

        private int ReadCost(Entity[] graph, Temp[]? capture = null)
        {
            long total = 0;
            for (int i = 0; i < graph.Length; i++)
            {
                Temp temp = ReadTemp(graph[i], i);
                if (capture != null) capture[i] = temp;
                total += temp.m_Cost;
            }
            Require(total > 0 && total <= int.MaxValue, "PREVIEW_COMMIT_PAYMENT_COST_UNQUALIFIED");
            return (int)total;
        }

        private void RequireMembers(Entity[] graph)
        {
            queryCount = query.CalculateEntityCount();
            chunkCount = query.CalculateChunkCount();
            Require(queryCount == graph.Length && chunkCount >= 1 && chunkCount <= graph.Length,
                "PREVIEW_COMMIT_PAYMENT_QUERY_BOUND");
            using var values = query.ToEntityArray(Allocator.Temp);
            Require(values.Length == graph.Length, "PREVIEW_COMMIT_PAYMENT_QUERY_CHANGED");
            foreach (Entity entity in values)
                Require(Array.IndexOf(graph, entity) >= 0, "PREVIEW_COMMIT_PAYMENT_FOREIGN_QUERY_MEMBER");
        }

        private Temp ReadTemp(Entity entity, int index)
        {
            EntityManager entities = EntityManager;
            Require(entity != Entity.Null && entities.Exists(entity) && entities.HasComponent<Temp>(entity)
                && !entities.HasComponent<Deleted>(entity) && !entities.HasComponent<Error>(entity)
                && !entities.HasComponent<Warning>(entity) && !entities.HasComponent<Override>(entity),
                "PREVIEW_COMMIT_PAYMENT_ENTITY_STATE_CHANGED");
            Temp temp = entities.GetComponentData<Temp>(entity);
            if (relocation != null)
            {
                Require(temp.Equals(relocation[index]) && (temp.m_Flags & TempFlags.Cancel) == 0
                    && (index == 0 ? temp.m_Cost > 0 : temp.m_Cost == 0), "PREVIEW_COMMIT_PAYMENT_RELOCATION_TEMP_CHANGED");
                return temp;
            }
            TempFlags expected = index == 0 ? TempFlags.Create | TempFlags.Essential
                : index == 1 ? TempFlags.Create | TempFlags.IsLast : TempFlags.Create;
            Require(temp.m_Original == Entity.Null && temp.m_Flags == expected && temp.m_Cost >= 0,
                "PREVIEW_COMMIT_PAYMENT_TEMP_UNQUALIFIED");
            return temp;
        }

        private int ReadFunds(Entity expectedCity)
        {
            var entities = EntityManager;
            Require(citySystem != null && ReferenceEquals(citySystem.World, World)
                && ReferenceEquals(World.GetExistingSystemManaged<CitySystem>(), citySystem)
                && expectedCity != Entity.Null && citySystem.City == expectedCity
                && entities.Exists(expectedCity) && entities.HasComponent<PlayerMoney>(expectedCity),
                "PREVIEW_COMMIT_PAYMENT_CITY_CHANGED");
            PlayerMoney funds = entities.GetComponentData<PlayerMoney>(expectedCity);
            Require(!funds.m_Unlimited, "PREVIEW_COMMIT_PAYMENT_UNLIMITED_MONEY");
            return funds.money;
        }

        private void RequireBindings()
        {
            EntityQuery[] registered = EntityQueries;
            Require(registered.Length == 1 && registered[0].Equals(query) && query.HasFilter()
                && ((EntityQuery)queryField!.GetValue(this)!).Equals(query)
                && output != null && ReferenceEquals(barrierField!.GetValue(this), output)
                && ReferenceEquals(output.World, World) && ReferenceEquals(cityField!.GetValue(this), citySystem),
                "PREVIEW_COMMIT_PAYMENT_BINDINGS_CHANGED");
        }

        private void RequireUnpatched()
        {
            patches = PreviewCommitPaymentPatchProbe.Observe();
            Require(patches.Qualified, patches.Reason);
        }

        private void Guard(Action requireScope)
        {
            requireScope();
            Require(World != null && World.IsCreated && !destructionStarted, "PREVIEW_COMMIT_PAYMENT_SOURCE_UNAVAILABLE");
        }

        private static FieldInfo Field(string name, Type type)
        {
            var field = typeof(ToolApplySystem).GetField(name, Fields);
            Require(field != null && field.FieldType == type && !field.IsInitOnly, "PREVIEW_COMMIT_PAYMENT_FIELD_ABI_UNQUALIFIED");
            return field!;
        }

        private static void Require(bool condition, string code) => PreviewCommitPayment.Require(condition, code);

        internal JObject Snapshot() => new JObject
        {
            ["effectEntered"] = EffectEntered, ["nativeReturned"] = nativeReturned, ["nativeJoined"] = nativeJoined,
            ["chargeObserved"] = chargeObserved, ["expectedCost"] = expectedCost,
            ["treasuryBefore"] = treasuryBefore, ["treasuryAfter"] = treasuryAfter,
            ["city"] = city == Entity.Null ? null : new JArray(city.Index, city.Version),
            ["tempUnchanged"] = tempUnchanged, ["bufferIndex"] = BufferIndex >= 0 ? new JValue(BufferIndex) : JValue.CreateNull(),
            ["queryCount"] = queryCount, ["chunkCount"] = chunkCount, ["patchQualified"] = patches?.Qualified,
            ["methodsChecked"] = patches?.MethodsChecked, ["methodSetSha256"] = patches?.MethodSetSha256,
            ["creationReturned"] = creationReturned, ["destructionReturned"] = destructionReturned
        };
    }
}
#endif
