using System;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Operations
{
    public static class OperationQueryService
    {
        public static JToken ExecuteV3(OperationAuthority? authority, JToken arguments, GrantSnapshot subject,
            string instanceId, long nowUnixMs)
        {
            var query = ContractCodec.DecodeOperationQueryV3(arguments);
            if (query.Source.HasValue && query.Source.Value == "history")
            {
                var selected = ReadHistory(authority, query.Selector, subject);
                return ContractCodec.EncodeOperationLookupV3(new OperationLookupV3 { Selector = query.Selector,
                    Result = new OperationLookupV3ResultHistory { Kind = "history", Access = "activeAuthority",
                        Source = selected.Source.ToContract(), AgeMs = selected.Source.AgeAt(nowUnixMs), Integrity = "sha256Verified",
                        Lookup = selected.Record == null
                            ? (OperationLookupV3ResultHistoryLookup)new OperationLookupV3ResultHistoryLookupUnavailable { Kind = "unavailable", Reason = "HISTORY_NOT_COVERED" }
                            : new OperationLookupV3ResultHistoryLookupFound { Kind = "found", Record = selected.Record.ToContract() } } });
            }

            // 两个公开来源共用原选择器，live保持原账查询语义，不转读历史文件。
            var selector = ContractCodec.EncodeOperationQueryV3(query)["selector"]!;
            var legacyInput = new JObject { ["selector"] = selector.DeepClone() };
            var legacy = authority == null ? Encode(new OperationLookupSnapshot("unavailable", reason: "JOURNAL_UNAVAILABLE"))
                : Execute(authority, legacyInput, subject);
            var output = new JObject { ["selector"] = selector.DeepClone(), ["result"] = new JObject {
                ["kind"] = "live", ["instanceId"] = instanceId, ["lookup"] = legacy["lookup"]!.DeepClone() } };
            return ContractCodec.EncodeOperationLookupV3(ContractCodec.DecodeOperationLookupV3(output));
        }

        public static JToken ExecuteV4(OperationAuthority? authority, JToken arguments, GrantSnapshot subject,
            string instanceId, long nowUnixMs)
        {
            var query = ContractCodec.DecodeOperationQueryV3(arguments);
            if (query.Source.HasValue && query.Source.Value == "history")
            {
                var selected = ReadHistory(authority, query.Selector, subject);
                return ContractCodec.EncodeOperationLookupV4(new OperationLookupV4 { Selector = query.Selector,
                    Result = new OperationLookupV4ResultHistory { Kind = "history", Access = "activeAuthority",
                        Source = selected.Source.ToContract(), AgeMs = selected.Source.AgeAt(nowUnixMs), Integrity = "sha256Verified",
                        Lookup = selected.Record == null
                            ? (OperationLookupV4ResultHistoryLookup)new OperationLookupV4ResultHistoryLookupUnavailable { Kind = "unavailable", Reason = "HISTORY_NOT_COVERED" }
                            : new OperationLookupV4ResultHistoryLookupFound { Kind = "found", Record = selected.Record.ToContract() } } });
            }

            OperationLookupSnapshot result;
            if (authority == null) result = new OperationLookupSnapshot("unavailable", reason: "JOURNAL_UNAVAILABLE");
            else if (query.Selector is OperationSelectorOperationId id) result = authority.Lookup(id.OperationId, subject);
            else if (query.Selector is OperationSelectorSubmissionKey key)
                result = authority.Lookup(new SubmissionKey(key.SubmissionKey.WriteContextId, key.SubmissionKey.RequestId), subject);
            else throw new InvalidOperationException("Unsupported operation selector.");
            var selector = ContractCodec.EncodeOperationQueryV3(query)["selector"]!;
            // live普通事实直接进入本修订；旧LookupV2枚举不能承接保留未覆盖原因。
            var output = new JObject { ["selector"] = selector.DeepClone(), ["result"] = new JObject {
                ["kind"] = "live", ["instanceId"] = instanceId, ["lookup"] = LookupObject(result) } };
            return ContractCodec.EncodeOperationLookupV4(ContractCodec.DecodeOperationLookupV4(output));
        }

        private static HistorySelection ReadHistory(OperationAuthority? authority, OperationSelector selector, GrantSnapshot subject)
        {
            if (authority == null) throw new OperationRejectedException("HISTORY_UNAVAILABLE");
            if (selector is OperationSelectorOperationId id) return authority.ReadHistory(id.OperationId, subject);
            if (selector is OperationSelectorSubmissionKey key)
                return authority.ReadHistory(new SubmissionKey(key.SubmissionKey.WriteContextId, key.SubmissionKey.RequestId), subject);
            throw new InvalidOperationException("Unsupported operation selector.");
        }

        public static JToken Execute(OperationAuthority authority, JToken arguments, GrantSnapshot subject)
        {
            var query = ContractCodec.DecodeOperationQueryV2(arguments);
            OperationLookupSnapshot result;
            if (query.Selector is OperationQueryV2SelectorOperationId id) result = authority.Lookup(id.OperationId, subject);
            else if (query.Selector is OperationQueryV2SelectorSubmissionKey key)
                result = authority.Lookup(new SubmissionKey(key.SubmissionKey.WriteContextId, key.SubmissionKey.RequestId), subject);
            else throw new InvalidOperationException("Unsupported operation selector.");
            return Encode(result);
        }
        public static JToken Encode(OperationLookupSnapshot value)
            => ContractCodec.EncodeOperationLookupV2(ContractCodec.DecodeOperationLookupV2(new JObject { ["lookup"] = LookupObject(value) }));

        private static JObject LookupObject(OperationLookupSnapshot value)
        {
            var lookup = new JObject { ["kind"] = value.Kind };
            if (value.Kind == "found" || value.Kind == "pending")
            {
                lookup["operationId"] = value.OperationId; lookup["originInstanceId"] = value.OriginInstanceId;
                lookup["durability"] = value.Durability;
            }
            if (value.Kind == "unavailable") lookup["reason"] = value.Reason;
            if (value.Kind == "found")
            {
                lookup["state"] = value.State; lookup["mayHaveApplied"] = value.MayHaveApplied;
                if (value.Load != null) lookup["load"] = new JObject { ["physicalTaskState"] = Word(value.Load.PhysicalTaskState), ["targetMatch"] = Word(value.Load.TargetMatch),
                    ["loadReturnValue"] = value.Load.LoadReturnValue.HasValue ? new JValue(value.Load.LoadReturnValue.Value) : JValue.CreateNull(),
                    ["isObservationCurrent"] = value.IsObservationCurrent, ["correlatedTargetReady"] = value.Load.CorrelatedTargetReady,
                    ["historicalCompletionProven"] = value.Load.HistoricalCompletionProven, ["observationEnd"] = Word(value.Load.ObservationEnd) };
            }
            return lookup;
        }
        private static string Word<T>(T value) => char.ToLowerInvariant(value!.ToString()![0]) + value.ToString()!.Substring(1);
    }
}
