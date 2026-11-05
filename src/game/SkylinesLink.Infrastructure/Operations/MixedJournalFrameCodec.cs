using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SchemaIds = SkylinesLink.Contracts.SchemaIds;

namespace SkylinesLink.Infrastructure.Operations
{
    internal static partial class JournalFrameCodec
    {
        // Explicit component limit, not a change to the live SLJO2/bank1 budget.
        // The largest stated-intent strings can exceed 62 KiB after JSON escaping.
        internal const int MaximumMixedMetadataPayload = TaskJournalLimits.MaximumMetadataPayload;
        internal const int MaximumMixedMetadataFrame = TaskJournalLimits.MaximumMetadataFrame;

        internal static bool IsTaskMetadata(JournalEntry entry) => entry is TaskCreated || entry is TaskGoalRevised || entry is TaskStopped;

        // Preserve the explicit ordinary-value revision. Neither path migrates data.
        internal static byte[] EncodeMixed(JournalRecord record, out int length)
        {
            if (record.SchemaRevision == 2) return Encode(record, out length);
            if (record.SchemaRevision != 3) throw new UnsupportedJournalVersionException();
            return EncodeDocument(ToMixedJson(record), SchemaIds.OperationJournalRecordV3, 3,
                IsTaskMetadata(record.Entry) ? MaximumMixedMetadataPayload : OperationLimits.MaximumRecordPayload, out length);
        }

        internal static JournalRecord? ReadMixed(Stream source) => ReadCore(source, true);

        private static JObject ToMixedJson(JournalRecord record)
        {
            JObject body;
            switch (record.Entry)
            {
                case TaskCreated created:
                    body = new JObject { ["kind"] = "taskCreated", ["taskId"] = created.TaskId, ["goalRevision"] = 1,
                        ["controlVersion"] = 1, ["decision"] = Decision(created.Decision), ["goal"] = Goal(created.Goal) };
                    break;
                case TaskGoalRevised revised:
                    body = new JObject { ["kind"] = "taskGoalRevised", ["taskId"] = revised.TaskId,
                        ["expectedGoalRevision"] = revised.ExpectedGoalRevision, ["goalRevision"] = revised.GoalRevision,
                        ["expectedControlVersion"] = revised.ExpectedControlVersion, ["decision"] = Decision(revised.Decision), ["goal"] = Goal(revised.Goal) };
                    break;
                case TaskStopped stopped:
                    body = new JObject { ["kind"] = "taskStopped", ["taskId"] = stopped.TaskId,
                        ["expectedControlVersion"] = stopped.ExpectedControlVersion, ["controlVersion"] = stopped.ControlVersion,
                        ["decision"] = Decision(stopped.Decision) };
                    break;
                default:
                    var ordinary = ToJson(record);
                    if (record.Entry is OperationAccepted accepted)
                        ordinary["entry"]!["actorIdentity"] = accepted.Intent.ProfileInstanceId == null
                            ? new JObject { ["kind"] = "legacyUnrecorded" }
                            : new JObject { ["kind"] = "recorded", ["profileInstanceId"] = accepted.Intent.ProfileInstanceId };
                    return ordinary;
            }
            return new JObject { ["sequence"] = record.Sequence, ["environmentId"] = record.EnvironmentId, ["entry"] = body };
        }

        private static JournalRecord FromMixedJson(JObject value)
        {
            var body = (JObject)value["entry"]!;
            string kind = (string)body["kind"]!;
            JournalEntry entry;
            switch (kind)
            {
                case "taskCreated": entry = new TaskCreated((string)body["taskId"]!, Decision((JObject)body["decision"]!), Goal((JObject)body["goal"]!)); break;
                case "taskGoalRevised": entry = new TaskGoalRevised((string)body["taskId"]!, (long)body["expectedGoalRevision"]!,
                    (long)body["goalRevision"]!, (long)body["expectedControlVersion"]!, Decision((JObject)body["decision"]!), Goal((JObject)body["goal"]!)); break;
                case "taskStopped": entry = new TaskStopped((string)body["taskId"]!, (long)body["expectedControlVersion"]!,
                    (long)body["controlVersion"]!, Decision((JObject)body["decision"]!)); break;
                default:
                    var ordinary = FromJson(value);
                    entry = ordinary.Entry;
                    if (entry is OperationAccepted accepted)
                    {
                        var identity = (JObject)body["actorIdentity"]!;
                        var intent = accepted.Intent;
                        string? profileInstanceId = (string)identity["kind"]! == "recorded" ? (string)identity["profileInstanceId"]! : null;
                        entry = new OperationAccepted(new OperationIntent(intent.OperationId, intent.OriginInstanceId, intent.Key,
                            intent.CommandHash, intent.ProfileId, intent.AuthorityEpoch, intent.GrantEpoch, intent.Source, intent.Target, profileInstanceId));
                    }
                    break;
            }
            return new JournalRecord((long)value["sequence"]!, (string)value["environmentId"]!, entry, 3);
        }

        private static JObject Decision(TaskDecision value) => new JObject {
            ["submissionKey"] = new JObject { ["writeContextId"] = value.Key.WriteContextId, ["requestId"] = value.Key.RequestId },
            ["actor"] = new JObject { ["profileId"] = value.Actor.ProfileId, ["profileInstanceId"] = value.Actor.ProfileInstanceId,
                ["authorityEpoch"] = value.Actor.AuthorityEpoch, ["grantEpoch"] = value.Actor.GrantEpoch },
            ["source"] = Source(value.Source), ["commandHash"] = value.CommandHash };
        private static TaskDecision Decision(JObject value)
        {
            var key = (JObject)value["submissionKey"]!; var actor = (JObject)value["actor"]!;
            return new TaskDecision(new SubmissionKey((string)key["writeContextId"]!, (string)key["requestId"]!),
                new TaskActor((string)actor["profileId"]!, (string)actor["profileInstanceId"]!, (long)actor["authorityEpoch"]!, (long)actor["grantEpoch"]!),
                Source((JObject)value["source"]!), (string)value["commandHash"]!);
        }
        private static JObject Goal(TaskStatedGoal value) => new JObject { ["kind"] = "statedIntent", ["title"] = value.Title,
            ["requestText"] = value.RequestText, ["constraintsText"] = new JArray(value.ConstraintsText), ["acceptanceText"] = new JArray(value.AcceptanceText) };
        private static TaskStatedGoal Goal(JObject value) => new TaskStatedGoal((string)value["title"]!, (string)value["requestText"]!,
            ((JArray)value["constraintsText"]!).Select(item => (string)item!).ToArray(), ((JArray)value["acceptanceText"]!).Select(item => (string)item!).ToArray());
    }
}
