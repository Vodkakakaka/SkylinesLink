using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Deployment;

/// <summary>Validates one complete CP2 before any bounded selection can escape.</summary>
[SupportedOSPlatform("windows")]
internal static partial class HistoryCheckpointV2
{
    internal const int MaximumFileBytes = PrivateHistoryLimits.MaximumFileBytes;
    internal const int MaximumPayloadBytes = MaximumFileBytes - 44;
    private const int MaximumJournalFrames = 129;
    private const int FixedProjectionAllowance = PrivateHistoryLimits.FixedProjectionAllowance;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // The caller owns the returned document and must dispose it after copying
    // its selected bounded result. No whole-document DTO tree is materialized.
    internal static JsonDocument ValidateDocument(byte[] frame, string environment)
    {
        if (frame is null || frame.Length < 45 || !frame.AsSpan(0, 4).SequenceEqual("SLHC"u8)) throw Corrupt();
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != 2)
            throw new HistoryReadException(HistoryReadCode.Incompatible);
        if (frame.Length > MaximumFileBytes || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0) throw Corrupt();
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4));
        if (length is 0 or > MaximumPayloadBytes || frame.Length != checked((int)length + 44)) throw Corrupt();
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(frame.AsSpan(0, frame.Length - 32), digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, frame.AsSpan(frame.Length - 32))) throw Corrupt();

        string json = Utf8.GetString(frame, 12, checked((int)length));
        var document = DeploymentContract.ParseHistoryCheckpointV2(json);
        try
        {
            var root = document.RootElement;
            var source = root.GetProperty("source");
            if (CorePackage.NormalizeEnvironment(environment) != environment || Text(source, "environmentId") != environment ||
                !HistoryReader.IsLowerHex(Text(source, "producerCoreManifestSha256"))) throw Corrupt();
            long through = Number(source, "throughSequence"), throughBytes = Number(source, "throughBytes");
            if (through > MaximumJournalFrames || throughBytes < 45 * through ||
                frame.Length > 3 * throughBytes + FixedProjectionAllowance) throw Corrupt();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<(string Context, string Request)>();
            var sequences = new HashSet<long>();
            foreach (var record in root.GetProperty("records").EnumerateArray())
            {
                if (!AddSequence(sequences, Number(record, "terminalSequence"), through) ||
                    !ids.Add(Text(record, "operationId")) || !AddKey(keys, record.GetProperty("submissionKey"))) throw Corrupt();
                HistoryCheckpoint.ValidateFacts(record);
            }

            // Metadata is the complete retained Task chain, but omitted Load
            // stages leave legal gaps in this shared journal sequence.
            var tasks = new Dictionary<string, TaskStateView>(StringComparer.Ordinal);
            long previousMetadataSequence = 1;
            foreach (var row in root.GetProperty("taskRecords").EnumerateArray())
            {
                long sequence = Number(row, "sequence");
                if (Text(row, "environmentId") != environment || sequence <= previousMetadataSequence ||
                    !AddSequence(sequences, sequence, through)) throw Corrupt();
                previousMetadataSequence = sequence;

                var entry = row.GetProperty("entry");
                var decision = entry.GetProperty("decision");
                if (!AddKey(keys, decision.GetProperty("submissionKey"))) throw Corrupt();
                string taskId = Text(entry, "taskId");
                bool found = tasks.TryGetValue(taskId, out var current);
                var input = ReadTransition(entry, decision, taskId);
                if (input.Kind == TaskDecisionKind.Create && tasks.Count >= TaskJournalRules.MaximumTasks ||
                    !TaskJournalRules.TryTransition(found ? current : null, input, out var next)) throw Corrupt();
                tasks[taskId] = next;
            }
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static TaskTransitionInput ReadTransition(JsonElement entry, JsonElement decision, string taskId)
    {
        var actor = decision.GetProperty("actor");
        var owner = new TaskOwnerView(Text(actor, "profileId"), Text(actor, "profileInstanceId"),
            Number(actor, "authorityEpoch"), Number(actor, "grantEpoch"));
        var source = ReadSource(decision.GetProperty("source"));
        string hash = Text(decision, "commandHash");
        switch (Text(entry, "kind"))
        {
            case "taskCreated":
                return new TaskTransitionInput(TaskDecisionKind.Create, taskId, owner, source, hash,
                    0, Number(entry, "goalRevision"), 0, Number(entry, "controlVersion"), ReadGoal(entry.GetProperty("goal")));
            case "taskGoalRevised":
                long expectedControl = Number(entry, "expectedControlVersion");
                return new TaskTransitionInput(TaskDecisionKind.ReviseGoal, taskId, owner, source, hash,
                    Number(entry, "expectedGoalRevision"), Number(entry, "goalRevision"), expectedControl, expectedControl,
                    ReadGoal(entry.GetProperty("goal")));
            case "taskStopped":
                return new TaskTransitionInput(TaskDecisionKind.Stop, taskId, owner, source, hash,
                    0, 0, Number(entry, "expectedControlVersion"), Number(entry, "controlVersion"), null);
            default: throw Corrupt();
        }
    }

    private static TaskSourceView ReadSource(JsonElement value)
    {
        string generationText = Text(value, "generation");
        if (!long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation < 0 ||
            generation.ToString(CultureInfo.InvariantCulture) != generationText) throw Corrupt();
        var city = value.GetProperty("cityId");
        return new TaskSourceView(Text(value, "instanceId"), generation, Text(value, "gameState"),
            city.ValueKind == JsonValueKind.Null ? null : city.GetString());
    }

    // Decode each Goal string/list once. These temporary borrowed views are
    // validated and hashed by Shared.Tasks; only the small resulting state stays.
    private static TaskGoalView ReadGoal(JsonElement value) => new(Text(value, "title"), Text(value, "requestText"),
        ReadTextList(value.GetProperty("constraintsText")), ReadTextList(value.GetProperty("acceptanceText")));
    private static string[] ReadTextList(JsonElement value)
    {
        var result = new string[value.GetArrayLength()];
        int index = 0;
        foreach (var item in value.EnumerateArray()) result[index++] = item.GetString()!;
        return result;
    }
    private static bool AddSequence(HashSet<long> values, long sequence, long through) =>
        sequence > 1 && sequence <= through && values.Add(sequence);
    private static bool AddKey(HashSet<(string Context, string Request)> values, JsonElement key) =>
        values.Add((Text(key, "writeContextId"), Text(key, "requestId")));
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    // Match the generated finite-profile numeric semantics, including accepted
    // integral exponent notation, rather than imposing a second integer parser.
    private static long Number(JsonElement value, string name) => checked((long)value.GetProperty(name).GetDouble());
    private static HistoryReadException Corrupt() => new(HistoryReadCode.CorruptStore);
}
