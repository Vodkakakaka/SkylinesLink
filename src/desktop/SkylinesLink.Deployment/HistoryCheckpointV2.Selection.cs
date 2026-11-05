using System;
using System.Collections.Generic;
using System.Text.Json;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Deployment;

internal static partial class HistoryCheckpointV2
{
    internal static (HistorySourceV2 Source, NativeHistoryCaptureV2Result Result) Select(
        byte[] frame, string environment, NativeHistoryQuery query)
    {
        using var document = ValidateDocument(frame, environment);
        var root = document.RootElement;
        var result = SelectValidated(root, query);
        // Only independent small DTOs escape the complete validation scope.
        return (DeploymentContract.DecodeHistorySourceV2(root.GetProperty("source")), result);
    }

    private static NativeHistoryCaptureV2Result SelectValidated(JsonElement root, NativeHistoryQuery query) => query switch
        {
            NativeHistoryQueryOperation operation when operation.Kind == "operation" =>
                SelectOperation(root.GetProperty("records"), operation.Selector),
            NativeHistoryQueryTask task when task.Kind == "task" && ValidIdentity(task.TaskId) =>
                SelectTask(root.GetProperty("taskRecords"), task.TaskId),
            NativeHistoryQueryTaskDecision decision when decision.Kind == "taskDecision" && ValidKey(decision.SubmissionKey) =>
                SelectTaskDecision(root.GetProperty("taskRecords"), decision.SubmissionKey),
            NativeHistoryQueryTaskGoal goal when goal.Kind == "taskGoal" =>
                SelectTaskGoal(root.GetProperty("taskRecords"), goal),
            _ => throw InvalidRequest()
        };

    private static NativeHistoryCaptureV2Result SelectOperation(JsonElement records, OperationSelector selector)
    {
        bool valid = selector switch
        {
            OperationSelectorOperationId id => id.Kind == "operationId" && ValidIdentity(id.OperationId),
            OperationSelectorSubmissionKey key => key.Kind == "submissionKey" && ValidKey(key.SubmissionKey),
            _ => false
        };
        if (!valid) throw InvalidRequest();
        foreach (var record in records.EnumerateArray())
        {
            bool matches = selector switch
            {
                OperationSelectorOperationId id => Text(record, "operationId") == id.OperationId,
                OperationSelectorSubmissionKey key => MatchesKey(record.GetProperty("submissionKey"), key.SubmissionKey),
                _ => false
            };
            if (matches) return new NativeHistoryCaptureV2ResultOperation
            { Kind = "operation", Record = DeploymentContract.DecodeHistoryRecord(record) };
        }
        return NotCovered();
    }

    private static NativeHistoryCaptureV2Result SelectTask(JsonElement records, string taskId)
    {
        JsonElement? created = null, goal = null, last = null;
        foreach (var row in records.EnumerateArray())
        {
            var entry = row.GetProperty("entry");
            if (Text(entry, "taskId") != taskId) continue;
            string kind = Text(entry, "kind");
            if (kind == "taskCreated") created = row;
            if (kind == "taskCreated" || kind == "taskGoalRevised") goal = row;
            last = row;
        }
        if (!created.HasValue) return NotCovered();
        if (!goal.HasValue || !last.HasValue) throw Corrupt();
        return new NativeHistoryCaptureV2ResultTask { Kind = "task", Summary =
            BuildTaskSummary(taskId, created.Value, goal.Value, last.Value) };
    }

    private static TaskHistorySummary BuildTaskSummary(string taskId, JsonElement first, JsonElement goal, JsonElement lastRow)
    {
        // Complete-chain validation has already established these rows and CAS.
        var firstDecision = DeploymentContract.DecodeTaskDecision(first.GetProperty("entry").GetProperty("decision"));
        var goalEntry = goal.GetProperty("entry");
        var lastEntry = lastRow.GetProperty("entry");
        bool stopped = Text(lastEntry, "kind") == "taskStopped";
        return new TaskHistorySummary
        {
            TaskId = taskId, CityId = firstDecision.Source.CityId!, Owner = firstDecision.Actor,
            Title = Text(goalEntry.GetProperty("goal"), "title"), GoalRevision = Number(goalEntry, "goalRevision"),
            ControlVersion = DecisionControlVersion(lastEntry), IsStopped = stopped,
            CreatedSequence = Number(first, "sequence"), LastDecisionSequence = Number(lastRow, "sequence")
        };
    }

    private static NativeHistoryCaptureV2Result SelectTaskDecision(JsonElement records, OperationSubmissionKey key)
    {
        foreach (var row in records.EnumerateArray())
        {
            var entry = row.GetProperty("entry");
            var decision = entry.GetProperty("decision");
            if (!MatchesKey(decision.GetProperty("submissionKey"), key)) continue;
            return new NativeHistoryCaptureV2ResultTaskDecision
            { Kind = "taskDecision", Receipt = BuildTaskDecisionReceipt(row) };
        }
        return NotCovered();
    }

    private static TaskHistoryDecisionReceipt BuildTaskDecisionReceipt(JsonElement row)
    {
        var entry = row.GetProperty("entry");
        string kind = Text(entry, "kind");
        return new TaskHistoryDecisionReceipt
        {
            TaskId = Text(entry, "taskId"), DecisionKind = kind switch
            { "taskCreated" => "create", "taskGoalRevised" => "reviseGoal", "taskStopped" => "stop", _ => throw Corrupt() },
            GoalRevision = kind == "taskStopped" ? null : Number(entry, "goalRevision"),
            ControlVersion = DecisionControlVersion(entry), Sequence = Number(row, "sequence"),
            Decision = DeploymentContract.DecodeTaskDecision(entry.GetProperty("decision"))
        };
    }

    private static NativeHistoryCaptureV2Result SelectTaskGoal(JsonElement records, NativeHistoryQueryTaskGoal query)
    {
        bool text = query.Section is "title" or "requestText";
        bool items = query.Section is "constraintsText" or "acceptanceText";
        if (!ValidIdentity(query.TaskId) || query.GoalRevision < 1 || query.GoalRevision > TaskJournalRules.MaximumCounter ||
            (!text && !items) || query.Offset < 0 || query.Offset > (items ? 16 : query.Section == "title" ? 128 : 2048) ||
            query.Limit < 1 || query.Limit > (text ? 512 : 4)) throw InvalidRequest();
        foreach (var row in records.EnumerateArray())
        {
            var entry = row.GetProperty("entry");
            if (Text(entry, "taskId") != query.TaskId || Text(entry, "kind") == "taskStopped" ||
                Number(entry, "goalRevision") != query.GoalRevision) continue;
            var goal = entry.GetProperty("goal");
            TaskHistoryGoalPage page;
            if (text)
            {
                var slice = SliceText(Text(goal, query.Section), checked((int)query.Offset), checked((int)query.Limit));
                page = new TaskHistoryGoalPageText
                {
                    Kind = "text", TaskId = query.TaskId, GoalRevision = query.GoalRevision, Section = query.Section,
                    Offset = query.Offset, NextOffset = slice.NextOffset, Text = slice.Text, Sequence = Number(row, "sequence")
                };
            }
            else
            {
                var values = goal.GetProperty(query.Section);
                int total = values.GetArrayLength(), offset = checked((int)query.Offset);
                if (offset > total) throw InvalidRequest();
                int end = Math.Min(total, offset + checked((int)query.Limit));
                var selected = new List<string>(end - offset);
                for (int index = offset; index < end; index++) selected.Add(values[index].GetString()!);
                page = new TaskHistoryGoalPageItems
                {
                    Kind = "items", TaskId = query.TaskId, GoalRevision = query.GoalRevision, Section = query.Section,
                    Offset = query.Offset, NextOffset = end < total ? (long?)end : null, Items = selected, Sequence = Number(row, "sequence")
                };
            }
            return new NativeHistoryCaptureV2ResultTaskGoal { Kind = "taskGoal", Page = page };
        }
        return NotCovered();
    }

    private static (string Text, long? NextOffset) SliceText(string value, int offset, int limit)
    {
        int scalars = 0, begin = -1, end = -1;
        for (int index = 0; index < value.Length; scalars++)
        {
            if (scalars == offset) begin = index;
            if (scalars == offset + limit) end = index;
            index += char.IsHighSurrogate(value[index]) ? 2 : 1;
        }
        if (offset > scalars) throw InvalidRequest();
        if (begin < 0) begin = value.Length;
        if (end < 0) end = value.Length;
        return (value.Substring(begin, end - begin), offset + limit < scalars ? (long?)(offset + limit) : null);
    }

    private static long DecisionControlVersion(JsonElement entry) =>
        Number(entry, Text(entry, "kind") == "taskGoalRevised" ? "expectedControlVersion" : "controlVersion");
    private static bool ValidIdentity(string? value) => TaskJournalRules.IsTextValid(value, 128);
    private static bool ValidKey(OperationSubmissionKey? value) =>
        value is not null && ValidIdentity(value.WriteContextId) && ValidIdentity(value.RequestId);
    private static bool MatchesKey(JsonElement value, OperationSubmissionKey key) =>
        Text(value, "writeContextId") == key.WriteContextId && Text(value, "requestId") == key.RequestId;
    private static NativeHistoryCaptureV2Result NotCovered() => new NativeHistoryCaptureV2ResultNotCovered { Kind = "notCovered" };
    private static HistoryReadException InvalidRequest() => new(HistoryReadCode.InvalidRequest);
}
