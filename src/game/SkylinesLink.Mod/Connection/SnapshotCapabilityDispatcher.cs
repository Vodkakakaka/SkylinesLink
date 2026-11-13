using System;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Modules;
using SkylinesLink.Application.Observation;
using SkylinesLink.Application.Buildings;
using SkylinesLink.Contracts;
using BuildingInspectionQuery = SkylinesLink.Application.Buildings.BuildingInspectionQuery;

namespace SkylinesLink.Mod.Connection
{
    // Stop只有这个精确端口能受理；普通Execute仍只读取有限普通值。
    public interface ITaskStopCapabilityDispatcher
    {
        void ValidateTaskStop(TaskStopSelector selector, GrantSnapshot subject);
        TaskDecisionLookupSnapshot BeginTaskStop(TaskStopSelector selector, GrantSnapshot subject);
        JToken RefreshTaskStop(TaskStopSelector selector, SubmissionKey key, GrantSnapshot subject);
    }

    /// <summary>同Authority的普通值读取与精确Stop端口；不接触 Game/Unity。</summary>
    public sealed class SnapshotCapabilityDispatcher : IReadOnlyCapabilityDispatcher, ITaskStopCapabilityDispatcher, IDeferredReadCapabilityDispatcher
    {
        private readonly WorldContextTracker context;
        private readonly string productVersion;
        private readonly OperationAuthority? operations;
        private readonly ModuleRuntimeObservationBroker? modules;
        private readonly CityOverviewReadBroker? cityOverview;
        private readonly BuildingInspectionReadBroker? buildings;
        public SnapshotCapabilityDispatcher(WorldContextTracker context, string productVersion, OperationAuthority? operations = null,
            ModuleRuntimeObservationBroker? modules = null, CityOverviewReadBroker? cityOverview = null, BuildingInspectionReadBroker? buildings = null)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context)); this.operations = operations;
            AuthorizationState.ValidateIdentifier(productVersion);
            this.productVersion = productVersion;
            if (modules != null && modules.InstanceId != context.Read().InstanceId) throw new ArgumentException("Module source instance mismatch.");
            this.modules = modules;
            this.cityOverview = cityOverview;
            this.buildings = buildings;
        }
        public DeferredCapabilityRead BeginRead(string capabilityId, string revision, JToken arguments, GrantSnapshot subject)
        {
            if (capabilityId == "buildings.inspect" && revision == "1")
            {
                BuildingInspectionQuery query;
                try { query = BuildingInspectionQuery.Parse(arguments); }
                catch (ArgumentException) { throw new CapabilityFailure("INVALID_ARGUMENT", "建筑读取参数或加载代次不符合契约。"); }
                if (buildings == null) return Immediate(BuildingInspectionQueryService.Unavailable("SOURCE_UNAVAILABLE"));
                var read = buildings.Capture(query);
                return new DeferredCapabilityRead(read.Completion, read, buildings.GetCurrentResult);
            }
            if (capabilityId == "city.overview" && revision == "2")
            {
                if (!ContractCatalog.Validate("urn:skylineslink:contract:empty:1", arguments).Valid)
                    throw new CapabilityFailure("INVALID_ARGUMENT", "当前读取参数不符合契约。");
                if (cityOverview == null) return Immediate(CityOverviewQueryService.Unavailable("SOURCE_UNAVAILABLE"));
                var read = cityOverview.Capture();
                return new DeferredCapabilityRead(read.Completion, read, cityOverview.GetCurrentResult);
            }
            if (capabilityId == "system.modules.get" && revision == "1")
            {
                var checkedInput = ContractCatalog.Validate("urn:skylineslink:contract:module-runtime-query:1", arguments);
                if (!checkedInput.Valid) throw new CapabilityFailure("INVALID_ARGUMENT", "当前读取参数不符合契约。");
                if (modules == null) return Immediate(ModuleRuntimeQueryService.Unavailable("SOURCE_UNAVAILABLE"));
                var read = (string?)arguments["kind"] == "capture" ? modules.Capture(subject.ProfileInstanceId) :
                    modules.Page(subject.ProfileInstanceId, (string)arguments["snapshotId"]!, (int)arguments["offset"]!);
                return new DeferredCapabilityRead(read.Completion, read);
            }
            return Immediate(Execute(capabilityId, revision, arguments, subject));
        }
        private static DeferredCapabilityRead Immediate(JToken value) => new DeferredCapabilityRead(Task.FromResult(value), EmptyRead.Instance);
        private sealed class EmptyRead : IDisposable
        { internal static readonly EmptyRead Instance = new EmptyRead(); public void Dispose() { } }
        public JToken Execute(string capabilityId, string revision, JToken arguments, GrantSnapshot subject)
        {
            if (capabilityId == "tasks.stop.get" && revision == "1")
            {
                try { return TaskStopQueryService.Execute(operations, arguments, subject, context.Read().InstanceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                catch (OperationRejectedException error) { throw Failure(error); }
            }
            if (capabilityId == "operations.get")
            {
                try
                {
                    if (revision == "5") return OperationQueryService.ExecuteV4(operations, arguments, subject,
                        context.Read().InstanceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (revision == "4") return OperationQueryService.ExecuteV3(operations, arguments, subject,
                        context.Read().InstanceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (revision == "3") return operations == null
                        ? OperationQueryService.Encode(new OperationLookupSnapshot("unavailable", reason: "JOURNAL_UNAVAILABLE"))
                        : OperationQueryService.Execute(operations, arguments, subject);
                }
                catch (OperationRejectedException error)
                {
                    // 请求进入后仍可能同时撤权；保留领域原因，不把普通拒绝变成管道异常。
                    if (error.Code == "UNAUTHORIZED") throw new CapabilityFailure(error.Code, "当前读取授权已失效。");
                    if (error.Code == "HISTORY_UNAVAILABLE") throw new CapabilityFailure(error.Code, "尚无可核验的历史检查点。");
                    if (error.Code == "COMMAND_KIND_MISMATCH") throw new CapabilityFailure(error.Code, "此请求标识不属于操作查询。");
                    throw;
                }
            }
            if (capabilityId != "system.context" || revision != "2") throw new CapabilityFailure("CAPABILITY_UNAVAILABLE", "能力尚未实现。");
            var snapshot = context.Read();
            return ContractCodec.EncodeContextV2(new ContextV2 { ProductVersion = productVersion, ContractVersion = "2",
                InstanceId = snapshot.InstanceId, GameState = snapshot.State, CatalogRevision = "1",
                World = snapshot.State != "ready" ? null : new ContextV2World { CityId = snapshot.CityId, Generation = snapshot.Generation.ToString(CultureInfo.InvariantCulture) } });
        }

        public void ValidateTaskStop(TaskStopSelector selector, GrantSnapshot subject)
        {
            if (operations == null) throw new OperationRejectedException("TASKS_UNAVAILABLE");
            operations.ValidateTaskStop(selector.TaskId, selector.ExpectedControlVersion, subject);
        }
        public TaskDecisionLookupSnapshot BeginTaskStop(TaskStopSelector selector, GrantSnapshot subject)
        {
            if (operations == null) throw new OperationRejectedException("TASKS_UNAVAILABLE");
            return operations.BeginStopTask(selector.TaskId, selector.ExpectedControlVersion, subject);
        }
        public JToken RefreshTaskStop(TaskStopSelector selector, SubmissionKey key, GrantSnapshot subject)
        {
            if (operations == null) return TaskStopQueryService.CompactUnavailable(selector, "TASKS_UNAVAILABLE");
            return TaskStopQueryService.Compact(selector, operations.RefreshTaskStopReceipt(key, subject));
        }
        private static CapabilityFailure Failure(OperationRejectedException error) => new CapabilityFailure(error.Code, "当前请求不可用或授权已失效。");
    }
}
