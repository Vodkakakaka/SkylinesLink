using System;
using System.Collections.Generic;
using System.Text.Json;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Deployment;

internal static partial class HistoryCheckpointV2
{
    internal static (HistorySourceV2 Source, NativeHistoryCaptureV3Result Result) SelectV3(
        byte[] frame, string environment, NativeHistoryQueryV3 query)
    {
        using var document = ValidateDocument(frame, environment);
        var root = document.RootElement;
        NativeHistoryCaptureV3Result result = query switch
        {
            NativeHistoryQueryV3Tasks tasks when tasks.Kind == "tasks" => SelectTasks(root.GetProperty("taskRecords")),
            NativeHistoryQueryV3TaskStopDecision stop when stop.Kind == "taskStopDecision" =>
                SelectTaskStopDecision(root.GetProperty("taskRecords"), stop),
            _ => FromV2Result(SelectValidated(root, HistoryReader.ToV2Query(query)))
        };
        return (DeploymentContract.DecodeHistorySourceV2(root.GetProperty("source")), result);
    }

    private static NativeHistoryCaptureV3Result SelectTasks(JsonElement records)
    {
        var tasks = new Dictionary<string, TaskSummaryRows>(StringComparer.Ordinal);
        foreach (var row in records.EnumerateArray())
        {
            var entry = row.GetProperty("entry");
            string taskId = Text(entry, "taskId"), kind = Text(entry, "kind");
            if (!tasks.TryGetValue(taskId, out var task))
            {
                if (tasks.Count >= TaskJournalRules.MaximumTasks) throw Corrupt();
                tasks.Add(taskId, task = new TaskSummaryRows());
            }
            if (kind == "taskCreated") task.Created = row;
            if (kind == "taskCreated" || kind == "taskGoalRevised") task.Goal = row;
            task.Last = row;
        }
        var ids = new List<string>(tasks.Keys);
        ids.Sort(StringComparer.Ordinal);
        var summaries = new List<TaskHistorySummary>(ids.Count);
        foreach (var id in ids)
        {
            var task = tasks[id];
            if (!task.Created.HasValue || !task.Goal.HasValue || !task.Last.HasValue) throw Corrupt();
            summaries.Add(BuildTaskSummary(id, task.Created.Value, task.Goal.Value, task.Last.Value));
        }
        return new NativeHistoryCaptureV3ResultTasks { Kind = "tasks", Tasks = summaries };
    }

    private static NativeHistoryCaptureV3Result SelectTaskStopDecision(JsonElement records, NativeHistoryQueryV3TaskStopDecision query)
    {
        if (!ValidIdentity(query.TaskId) || query.ExpectedControlVersion < 1 || query.ExpectedControlVersion > TaskJournalRules.MaximumCounter)
            throw InvalidRequest();
        foreach (var row in records.EnumerateArray())
        {
            var entry = row.GetProperty("entry");
            if (Text(entry, "kind") != "taskStopped" || Text(entry, "taskId") != query.TaskId ||
                Number(entry, "expectedControlVersion") != query.ExpectedControlVersion) continue;
            // The persisted key is authoritative, including recovered keys from
            // an older naming policy. A miss never proves current absence.
            return new NativeHistoryCaptureV3ResultTaskDecision { Kind = "taskDecision", Receipt = BuildTaskDecisionReceipt(row) };
        }
        return new NativeHistoryCaptureV3ResultNotCovered { Kind = "notCovered" };
    }

    private static NativeHistoryCaptureV3Result FromV2Result(NativeHistoryCaptureV2Result result) => result switch
    {
        NativeHistoryCaptureV2ResultNotCovered => new NativeHistoryCaptureV3ResultNotCovered { Kind = "notCovered" },
        NativeHistoryCaptureV2ResultOperation value => new NativeHistoryCaptureV3ResultOperation { Kind = value.Kind, Record = value.Record },
        NativeHistoryCaptureV2ResultTask value => new NativeHistoryCaptureV3ResultTask { Kind = value.Kind, Summary = value.Summary },
        NativeHistoryCaptureV2ResultTaskDecision value => new NativeHistoryCaptureV3ResultTaskDecision { Kind = value.Kind, Receipt = value.Receipt },
        NativeHistoryCaptureV2ResultTaskGoal value => new NativeHistoryCaptureV3ResultTaskGoal { Kind = value.Kind, Page = value.Page },
        _ => throw Corrupt()
    };

    // At most eight borrowed row triples, disposed with the complete validated
    // document. Only copied summary values escape SelectV3.
    private sealed class TaskSummaryRows
    {
        internal JsonElement? Created, Goal, Last;
    }
}
