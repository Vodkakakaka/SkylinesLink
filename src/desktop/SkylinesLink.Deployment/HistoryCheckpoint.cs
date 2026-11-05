using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkylinesLink.Deployment.Generated;

namespace SkylinesLink.Deployment;

[SupportedOSPlatform("windows")]
internal static class HistoryCheckpoint
{
    internal const int MaximumFileBytes = PrivateHistoryLimits.LegacyMaximumFileBytes;
    private const int MaximumPayloadBytes = MaximumFileBytes - 44;

    // Synchronous scope: neither JsonElement, JsonDocument nor the complete file escapes selection.
    internal static (HistorySource Source, HistoryRecord? Record) Select(byte[] frame, string environment, OperationSelector selector)
    {
        if (frame.Length < 45 || frame.Length > MaximumFileBytes || !frame.AsSpan(0, 4).SequenceEqual("SLHC"u8)) throw Corrupt();
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != 1) throw new HistoryReadException(HistoryReadCode.Incompatible);
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0) throw Corrupt();
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4));
        if (length is 0 or > MaximumPayloadBytes || frame.Length != length + 44) throw Corrupt();
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(frame.AsSpan(0, frame.Length - 32), digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, frame.AsSpan(frame.Length - 32))) throw Corrupt();
        string json = new UTF8Encoding(false, true).GetString(frame, 12, checked((int)length));
        StrictJsonSyntax.Validate(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 33, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        // Known format changes get a distinct incompatibility result; all shape rules remain generated.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("source", out var rawSource) && rawSource.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, expected) in new[] { ("formatVersion", 1), ("journalSchemaRevision", 2) })
                if (rawSource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var version) && version != expected)
                    throw new HistoryReadException(HistoryReadCode.Incompatible);
        }
        DeploymentContract.ValidateOperationHistoryCheckpoint(root);
        var sourceElement = root.GetProperty("source");
        if (sourceElement.GetProperty("environmentId").GetString() != environment ||
            !HistoryReader.IsLowerHex(sourceElement.GetProperty("producerCoreManifestSha256").GetString()!)) throw Corrupt();
        long through = Number(sourceElement, "throughSequence");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string, string)>();
        var sequences = new HashSet<long>();
        JsonElement? selected = null;
        foreach (var record in root.GetProperty("records").EnumerateArray())
        {
            long sequence = Number(record, "terminalSequence");
            var key = record.GetProperty("submissionKey");
            string id = Text(record, "operationId"), context = Text(key, "writeContextId"), request = Text(key, "requestId");
            if (sequence > through || !sequences.Add(sequence) || !ids.Add(id) || !keys.Add((context, request))) throw Corrupt();
            ValidateFacts(record);
            bool matches = selector switch
            {
                OperationSelectorOperationId byId => string.Equals(byId.OperationId, id, StringComparison.Ordinal),
                OperationSelectorSubmissionKey byKey => string.Equals(byKey.SubmissionKey.WriteContextId, context, StringComparison.Ordinal) &&
                    string.Equals(byKey.SubmissionKey.RequestId, request, StringComparison.Ordinal),
                _ => throw new HistoryReadException(HistoryReadCode.InvalidRequest)
            };
            if (matches) selected = record;
        }
        // Validate the entire document before decoding any selected DTO. Producer identity is not origin identity.
        return (DeploymentContract.DecodeHistorySource(sourceElement), selected is { } found ? DeploymentContract.DecodeHistoryRecord(found) : null);
    }

    internal static void ValidateFacts(JsonElement record)
    {
        string origin = Text(record, "originInstanceId");
        var source = record.GetProperty("sourceContext");
        long sourceGeneration = Context(source, origin, false);
        var load = record.GetProperty("load");
        string physical = Text(load, "physicalTaskState"), state = Text(record, "state");
        bool applied = record.GetProperty("mayHaveApplied").GetBoolean();
        bool correlated = load.GetProperty("correlatedTargetReady").GetBoolean();
        bool historical = load.GetProperty("historicalCompletionProven").GetBoolean();
        var returnValue = load.GetProperty("loadReturnValue");
        var observation = load.GetProperty("observationContext");
        if (load.GetProperty("isObservationCurrent").GetBoolean() || historical != (state == "succeeded")) throw Corrupt();
        if ((physical == "ranToCompletion") != (returnValue.ValueKind != JsonValueKind.Null)) throw Corrupt();
        long? observedGeneration = observation.ValueKind == JsonValueKind.Null ? null : Context(observation, origin, true);
        if (Text(load, "targetMatch") != "unknown" && observedGeneration is null) throw Corrupt();
        if ((correlated || historical) && (observedGeneration is null || observedGeneration <= sourceGeneration)) throw Corrupt();
        if (historical && (!correlated || physical != "ranToCompletion" || !returnValue.GetBoolean())) throw Corrupt();
        bool valid = physical switch
        {
            "notStarted" => !applied && !correlated && !historical && (state == "failed" || state == "cancelled"),
            "unknown" => applied && !historical && state == "unknown",
            "faulted" => applied && !historical && state == "failed",
            "cancelled" => applied && !historical && state == "cancelled",
            "ranToCompletion" => applied && (returnValue.GetBoolean() ? state == (historical ? "succeeded" : "unknown") : state == "failed"),
            _ => false
        };
        if (!valid) throw Corrupt();
    }

    private static long Context(JsonElement value, string origin, bool observation)
    {
        string state = Text(value, "gameState");
        bool city = value.GetProperty("cityId").ValueKind != JsonValueKind.Null;
        string generationText = Text(value, "generation");
        if (Text(value, "instanceId") != origin ||
            !(state == "ready" || !observation && state == "menu" && !city) ||
            !long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation < 0 ||
            generation.ToString(CultureInfo.InvariantCulture) != generationText) throw Corrupt();
        return generation;
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    // The generated numeric validator intentionally accepts integral JSON exponent notation.
    private static long Number(JsonElement value, string name) => checked((long)value.GetProperty(name).GetDouble());
    private static HistoryReadException Corrupt() => new(HistoryReadCode.CorruptStore);
}
