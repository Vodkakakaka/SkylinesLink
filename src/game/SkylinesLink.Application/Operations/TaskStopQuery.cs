using System;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Operations
{
    /// <summary>停止回执的普通值投影；不受理决定、不改授权、不等IO，也不以当前上下文替换原事实。</summary>
    public static class TaskStopQueryService
    {
        public static JToken Compact(TaskStopSelector selector, TaskDecisionLookupSnapshot value)
        {
            var pair = Freeze(selector);
            TaskStopReceipt? receipt = null;
            if (value.Receipt != null)
            {
                var original = ValidateReceipt(pair, value);
                receipt = new TaskStopReceipt
                {
                    SubmissionKey = new OperationSubmissionKey { WriteContextId = original.Decision.Key.WriteContextId,
                        RequestId = original.Decision.Key.RequestId },
                    ControlVersion = original.ControlVersion, StopRequested = true,
                    PersistenceAttempted = original.PersistenceAttempted, Journal = Position(original.Journal)
                };
            }
            TaskStopLookup lookup;
            if (value.Kind == "found" && receipt != null)
                lookup = new TaskStopLookupFound { Kind = "found", Receipt = receipt };
            else if (value.Kind == "pending" && receipt != null)
                lookup = new TaskStopLookupPending { Kind = "pending", Receipt = receipt };
            else if (value.Kind == "unavailable" || value.Kind == "expired" || value.Kind == "absent")
                lookup = new TaskStopLookupUnavailable { Kind = "unavailable", Receipt = receipt,
                    Reason = value.Reason == "RETENTION_NOT_COVERED" ? "RETENTION_NOT_COVERED" : "JOURNAL_UNAVAILABLE" };
            else throw new InvalidOperationException("A Stop receipt cannot be projected from this decision lookup.");
            return ContractCodec.EncodeTaskStopResult(new TaskStopResult { Selector = pair, Lookup = lookup });
        }

        public static JToken CompactUnavailable(TaskStopSelector selector, string reason)
            => ContractCodec.EncodeTaskStopResult(new TaskStopResult { Selector = Freeze(selector),
                Lookup = new TaskStopLookupUnavailable { Kind = "unavailable", Receipt = null, Reason = reason } });

        public static JToken Execute(OperationAuthority? authority, JToken arguments, GrantSnapshot subject,
            string instanceId, long nowUnixMs)
        {
            var query = ContractCodec.DecodeTaskStopDecisionQuery(arguments);
            var pair = Freeze(new TaskStopSelector { TaskId = query.TaskId, ExpectedControlVersion = query.ExpectedControlVersion });
            if (query.Source.HasValue && query.Source.Value == "history")
            {
                if (authority == null) throw new OperationRejectedException("HISTORY_UNAVAILABLE");
                var selected = authority.ReadTaskStopHistory(pair.TaskId, pair.ExpectedControlVersion, subject);
                TaskStopHistoryLookup lookup = selected.Record == null
                    ? (TaskStopHistoryLookup)new TaskStopHistoryLookupUnavailable { Kind = "unavailable", Reason = "HISTORY_NOT_COVERED" }
                    : new TaskStopHistoryLookupFound { Kind = "found", Receipt = HistoryReceipt(pair, selected.Record) };
                return ContractCodec.EncodeTaskStopDecisionLookup(new TaskStopDecisionLookup { Selector = pair,
                    Result = new TaskStopDecisionLookupResultHistory { Kind = "history", Access = "activeAuthority",
                        Source = selected.Source.ToContractV2(), AgeMs = selected.Source.AgeAt(nowUnixMs),
                        Integrity = "sha256Verified", Lookup = lookup } });
            }

            TaskStopLiveLookup live;
            if (authority == null) live = new TaskStopLiveLookupUnavailable { Kind = "unavailable", Receipt = null, Reason = "TASKS_UNAVAILABLE" };
            else
            {
                TaskDecisionLookupSnapshot value;
                try { value = authority.LookupTaskStopDecision(pair.TaskId, pair.ExpectedControlVersion, subject); }
                catch (OperationRejectedException error) when (error.Code == "TASKS_UNAVAILABLE")
                {
                    return EncodeLive(pair, instanceId, new TaskStopLiveLookupUnavailable
                    { Kind = "unavailable", Receipt = null, Reason = "TASKS_UNAVAILABLE" });
                }
                TaskStopDecisionReceipt? receipt = null;
                if (value.Receipt != null)
                {
                    var original = ValidateReceipt(pair, value);
                    receipt = new TaskStopDecisionReceipt { TaskId = original.TaskId, DecisionKind = "stop", GoalRevision = null,
                        ControlVersion = original.ControlVersion, Decision = TaskHistoryContractMapper.WriteDecision(original.Decision),
                        Journal = Position(original.Journal), PersistenceAttempted = original.PersistenceAttempted };
                }
                if (value.Kind == "found" && receipt != null)
                    live = new TaskStopLiveLookupFound { Kind = "found", Receipt = receipt };
                else if (value.Kind == "pending" && receipt != null)
                    live = new TaskStopLiveLookupPending { Kind = "pending", Receipt = receipt };
                else if (value.Kind == "unavailable") live = new TaskStopLiveLookupUnavailable { Kind = "unavailable", Receipt = receipt,
                    Reason = value.Reason == "RETENTION_NOT_COVERED" ? "RETENTION_NOT_COVERED" :
                        value.Reason == "STOP_NOT_COVERED" ? "STOP_NOT_COVERED" : "JOURNAL_UNAVAILABLE" };
                else throw new InvalidOperationException("A live Stop lookup has an unsupported state.");
            }
            return EncodeLive(pair, instanceId, live);
        }

        private static JToken EncodeLive(TaskStopSelector pair, string instanceId, TaskStopLiveLookup lookup)
            => ContractCodec.EncodeTaskStopDecisionLookup(new TaskStopDecisionLookup { Selector = pair,
                Result = new TaskStopDecisionLookupResultLive { Kind = "live", InstanceId = instanceId, Lookup = lookup } });

        private static TaskStopSelector Freeze(TaskStopSelector selector)
        {
            var pair = new TaskStopSelector { TaskId = selector.TaskId, ExpectedControlVersion = selector.ExpectedControlVersion };
            ContractCodec.EncodeTaskStopQuery(pair);
            return pair;
        }

        private static TaskMutationReceipt ValidateReceipt(TaskStopSelector pair, TaskDecisionLookupSnapshot value)
        {
            var receipt = value.Receipt!;
            if (receipt.Kind != "stop" || receipt.TaskId != pair.TaskId || receipt.GoalRevision.HasValue ||
                receipt.ControlVersion != pair.ExpectedControlVersion + 1 || !receipt.Decision.Key.Equals(value.Key) ||
                receipt.Decision.CommandHash != TaskCommandHash.Stop(pair.TaskId, pair.ExpectedControlVersion) ||
                (value.Kind == "found" ? receipt.Durability != "durable" || receipt.Journal == null : receipt.Durability != value.Kind) ||
                value.Kind == "pending" && receipt.Journal != null || receipt.Journal != null && !receipt.PersistenceAttempted)
                throw new InvalidOperationException("The Stop projection does not match the original decision.");
            return receipt;
        }

        private static TaskJournalReceipt? Position(JournalTaskReceipt? journal) => journal == null ? null
            : new TaskJournalReceipt { StoreId = journal.StoreId, Sequence = journal.Sequence };

        private static TaskHistoryDecisionReceipt HistoryReceipt(TaskStopSelector pair, JournalRecord row)
        {
            if (row.SchemaRevision != 3 || !(row.Entry is TaskStopped stop) || stop.TaskId != pair.TaskId ||
                stop.ExpectedControlVersion != pair.ExpectedControlVersion || stop.ControlVersion != pair.ExpectedControlVersion + 1 ||
                stop.Decision.CommandHash != TaskCommandHash.Stop(pair.TaskId, pair.ExpectedControlVersion))
                throw new InvalidOperationException("The published Stop does not match the selected decision.");
            return new TaskHistoryDecisionReceipt { TaskId = stop.TaskId, DecisionKind = "stop", GoalRevision = null,
                ControlVersion = stop.ControlVersion, Sequence = row.Sequence,
                Decision = TaskHistoryContractMapper.WriteDecision(stop.Decision) };
        }
    }
}
