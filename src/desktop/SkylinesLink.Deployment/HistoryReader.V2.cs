using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Deployment;

[SupportedOSPlatform("windows")]
public static partial class HistoryReader
{
    public static NativeHistoryRequestV2 ParseRequestV2(string json)
    {
        try { return FreezeRequestV2(DeploymentContract.ParseNativeHistoryRequestV2(json)); }
        catch (HistoryReadException) { throw; }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or System.Text.Json.JsonException)
        { throw new HistoryReadException(HistoryReadCode.InvalidRequest); }
    }

    public static async Task<NativeHistoryCaptureV2> CaptureV2Async(NativeHistoryRequestV2 request, CancellationToken cancellation)
    {
        // Own one value snapshot before the first await; callers cannot change
        // the checked principal, selector or page while file IO is in flight.
        var frozen = FreezeRequestV2(request);
        var captured = await CaptureMixedAsync(frozen.EnvironmentId, frozen.ProfileId, frozen.ExpectedAuthorityEpoch,
            frozen.ExpectedGrantEpoch, frozen.ExpectedProfileInstanceId,
            bytes => HistoryCheckpointV2.Select(bytes, frozen.EnvironmentId, frozen.Query), cancellation).ConfigureAwait(false);
        var subject = captured.Subject;
        var selected = captured.Selection;
        return new NativeHistoryCaptureV2 { FormatVersion = 2, EnvironmentId = frozen.EnvironmentId, ProfileId = frozen.ProfileId,
            ProfileInstanceId = subject.ProfileInstanceId, AuthorizationRecordRevision = subject.Revision,
            AuthorityEpoch = subject.AuthorityEpoch, GrantEpoch = subject.GrantEpoch,
            Source = selected.Source, Query = frozen.Query, Result = selected.Result };
    }

    // Both mixed protocols own the same lease, complete CP2 validation scope
    // and recheck. Only their frozen selectors and bounded DTOs differ.
    private static async Task<(Subject Subject, TSelection Selection)> CaptureMixedAsync<TSelection>(
        string environment, string profile, long? authority, long? grant, string? instance,
        Func<byte[], TSelection> select, CancellationToken cancellation)
    {
        byte[]? authorizationBytes = null, historyBytes = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var files = new HistoryFiles(environment);
            _ = files.OpenAuthorityLock();
            cancellation.ThrowIfCancellationRequested();
            authorizationBytes = await ReadLimited(files.OpenAuthorization(), 65536, cancellation).ConfigureAwait(false);
            var subject = ReadSubject(authorizationBytes, environment, profile, authority, grant, instance);
            Array.Clear(authorizationBytes); authorizationBytes = null;
            historyBytes = await ReadHistoryBytes(files.OpenHistory(), 2, cancellation).ConfigureAwait(false);
            var selected = select(historyBytes);
            Array.Clear(historyBytes); historyBytes = null;
            cancellation.ThrowIfCancellationRequested();
            files.Recheck();
            cancellation.ThrowIfCancellationRequested();
            return (subject, selected);
        }
        catch (HistoryReadException) { throw; }
        catch (OperationCanceledException) { throw new HistoryReadException(HistoryReadCode.IoFailed); }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { throw Corrupt(); }
        catch (IOException) { throw new HistoryReadException(HistoryReadCode.IoFailed); }
        finally
        {
            if (authorizationBytes is not null) Array.Clear(authorizationBytes);
            if (historyBytes is not null) Array.Clear(historyBytes);
        }
    }

    private static NativeHistoryRequestV2 FreezeRequestV2(NativeHistoryRequestV2 request)
    {
        if (request is null) throw InvalidRequest();
        try
        {
            string environment = request.EnvironmentId, profile = request.ProfileId;
            long? authority = request.ExpectedAuthorityEpoch, grant = request.ExpectedGrantEpoch;
            string? instance = request.ExpectedProfileInstanceId;
            ValidateMixedIdentity(environment, profile, authority, grant, instance);
            if (request.FormatVersion != 2) throw InvalidRequest();
            return new NativeHistoryRequestV2 { FormatVersion = 2, EnvironmentId = environment, ProfileId = profile,
                ExpectedAuthorityEpoch = authority, ExpectedGrantEpoch = grant, ExpectedProfileInstanceId = instance,
                Query = FreezeQuery(request.Query) };
        }
        catch (HistoryReadException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NullReferenceException)
        { throw InvalidRequest(); }
    }

    private static void ValidateMixedIdentity(string environment, string profile, long? authority, long? grant, string? instance)
    {
        CorePackage.ValidateIdentifier(profile);
        if (CorePackage.NormalizeEnvironment(environment) != environment ||
            authority.HasValue != grant.HasValue || authority.HasValue != (instance is not null) ||
            authority is < 1 or > TaskJournalRules.MaximumCounter || grant is < 1 or > TaskJournalRules.MaximumCounter ||
            instance is not null && !IsLowerHex(instance, 32)) throw InvalidRequest();
    }

    private static NativeHistoryQuery FreezeQuery(NativeHistoryQuery query)
    {
        string Text(string? value)
        { if (!TaskJournalRules.IsTextValid(value, 128)) throw InvalidRequest(); return value!; }
        OperationSubmissionKey Key(OperationSubmissionKey value) => value is null ? throw InvalidRequest() :
            new OperationSubmissionKey { WriteContextId = Text(value.WriteContextId), RequestId = Text(value.RequestId) };
        switch (query)
        {
            case NativeHistoryQueryOperation operation when operation.Kind == "operation":
                OperationSelector selector = operation.Selector switch
                {
                    OperationSelectorOperationId value when value.Kind == "operationId" =>
                        new OperationSelectorOperationId { Kind = "operationId", OperationId = Text(value.OperationId) },
                    OperationSelectorSubmissionKey value when value.Kind == "submissionKey" =>
                        new OperationSelectorSubmissionKey { Kind = "submissionKey", SubmissionKey = Key(value.SubmissionKey) },
                    _ => throw InvalidRequest()
                };
                return new NativeHistoryQueryOperation { Kind = "operation", Selector = selector };
            case NativeHistoryQueryTask task when task.Kind == "task":
                return new NativeHistoryQueryTask { Kind = "task", TaskId = Text(task.TaskId) };
            case NativeHistoryQueryTaskDecision decision when decision.Kind == "taskDecision":
                return new NativeHistoryQueryTaskDecision { Kind = "taskDecision", SubmissionKey = Key(decision.SubmissionKey) };
            case NativeHistoryQueryTaskGoal goal when goal.Kind == "taskGoal":
                string id = Text(goal.TaskId), section = goal.Section;
                long revision = goal.GoalRevision, offset = goal.Offset, limit = goal.Limit;
                bool list = section == "constraintsText" || section == "acceptanceText";
                if (!(list || section == "title" || section == "requestText") ||
                    revision < 1 || revision > TaskJournalRules.MaximumCounter || offset < 0 ||
                    offset > (list ? 16 : section == "title" ? 128 : 2048) || limit < 1 || limit > (list ? 4 : 512)) throw InvalidRequest();
                return new NativeHistoryQueryTaskGoal { Kind = "taskGoal", TaskId = id, GoalRevision = revision,
                    Section = section, Offset = offset, Limit = limit };
            default: throw InvalidRequest();
        }
    }

    private static async Task<byte[]> ReadHistoryBytes(FileStream file, int expectedFormat, CancellationToken cancellation)
    {
        if (file.Length < 45 || file.Length > HistoryCheckpointV2.MaximumFileBytes) throw Corrupt();
        var header = new byte[12];
        byte[]? bytes = null;
        try
        {
            await file.ReadExactlyAsync(header.AsMemory(), cancellation).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("SLHC"u8) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)) != 0) throw Corrupt();
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) != expectedFormat)
                throw new HistoryReadException(HistoryReadCode.Incompatible);
            int maximum = expectedFormat == 1 ? HistoryCheckpoint.MaximumFileBytes : HistoryCheckpointV2.MaximumFileBytes;
            if (file.Length > maximum) throw Corrupt();
            bytes = new byte[checked((int)file.Length)];
            header.CopyTo(bytes, 0);
            await file.ReadExactlyAsync(bytes.AsMemory(header.Length), cancellation).ConfigureAwait(false);
            return bytes;
        }
        catch { if (bytes is not null) Array.Clear(bytes); throw; }
        finally { Array.Clear(header); }
    }
    private static HistoryReadException InvalidRequest() => new(HistoryReadCode.InvalidRequest);
}
