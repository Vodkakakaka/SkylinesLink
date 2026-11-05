using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Deployment.Generated;

namespace SkylinesLink.Deployment;

/// <summary>同一既有 authority lease 内核验持久授权与历史；不读活动 journal，不修复资料。</summary>
[SupportedOSPlatform("windows")]
public static partial class HistoryReader
{
    public static NativeHistoryRequest ParseRequest(string json)
    {
        try
        {
            var request = DeploymentContract.ParseNativeHistoryRequest(json);
            ValidateRequest(request);
            return request;
        }
        catch (HistoryReadException) { throw; }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException)
        { throw new HistoryReadException(HistoryReadCode.InvalidRequest); }
    }

    public static async Task<NativeHistoryCapture> CaptureAsync(NativeHistoryRequest request, CancellationToken cancellation)
    {
        ValidateRequest(request);
        byte[]? authorizationBytes = null, historyBytes = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var files = new HistoryFiles(request.EnvironmentId);
            _ = files.OpenAuthorityLock();
            cancellation.ThrowIfCancellationRequested();
            authorizationBytes = await ReadLimited(files.OpenAuthorization(), 65536, cancellation).ConfigureAwait(false);
            var subject = ReadSubject(authorizationBytes, request.EnvironmentId, request.ProfileId, request.ExpectedAuthorityEpoch, request.ExpectedGrantEpoch);
            Array.Clear(authorizationBytes); authorizationBytes = null;
            historyBytes = await ReadHistoryBytes(files.OpenHistory(), 1, cancellation).ConfigureAwait(false);
            var selected = HistoryCheckpoint.Select(historyBytes, request.EnvironmentId, request.Selector);
            Array.Clear(historyBytes); historyBytes = null;
            cancellation.ThrowIfCancellationRequested();
            files.Recheck();
            cancellation.ThrowIfCancellationRequested();
            return new NativeHistoryCapture { FormatVersion = 1, EnvironmentId = request.EnvironmentId, ProfileId = request.ProfileId,
                AuthorizationRecordRevision = subject.Revision, AuthorityEpoch = subject.AuthorityEpoch, GrantEpoch = subject.GrantEpoch,
                Source = selected.Source, Record = selected.Record };
        }
        catch (HistoryReadException) { throw; }
        catch (OperationCanceledException) { throw new HistoryReadException(HistoryReadCode.IoFailed); }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { throw new HistoryReadException(HistoryReadCode.CorruptStore); }
        catch (IOException) { throw new HistoryReadException(HistoryReadCode.IoFailed); }
        finally
        {
            if (authorizationBytes is not null) { Array.Clear(authorizationBytes); authorizationBytes = null; }
            if (historyBytes is not null) { Array.Clear(historyBytes); historyBytes = null; }
        }
    }

    private static void ValidateRequest(NativeHistoryRequest request)
    {
        if (request is null) throw new HistoryReadException(HistoryReadCode.InvalidRequest);
        try
        {
            CorePackage.ValidateIdentifier(request.ProfileId);
            if (CorePackage.NormalizeEnvironment(request.EnvironmentId) != request.EnvironmentId || request.FormatVersion != 1 ||
                request.ExpectedAuthorityEpoch.HasValue != request.ExpectedGrantEpoch.HasValue ||
                request.ExpectedAuthorityEpoch is < 1 or > 9007199254740991 || request.ExpectedGrantEpoch is < 1 or > 9007199254740991 ||
                request.Selector is not (OperationSelectorOperationId or OperationSelectorSubmissionKey)) throw new InvalidDataException();
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NullReferenceException)
        { throw new HistoryReadException(HistoryReadCode.InvalidRequest); }
    }

    private static Subject ReadSubject(byte[] bytes, string environment, string profileId, long? expectedAuthorityEpoch,
        long? expectedGrantEpoch, string? expectedProfileInstanceId = null)
    {
        var json = new UTF8Encoding(false, true).GetString(bytes);
        AuthorizationSnapshotV2 authorization;
        try { authorization = DeploymentContract.ParseAuthorizationSnapshotV2(json); }
        catch (Exception error) when (error is InvalidDataException or FormatException)
        {
            // Recognize the old contract only to report incompatibility. Never
            // invent an identity or migrate a file from this read-only path.
            try { _ = DeploymentContract.ParseAuthorizationSnapshot(json); }
            catch (Exception legacyError) when (legacyError is InvalidDataException or FormatException) { throw Corrupt(); }
            throw new HistoryReadException(HistoryReadCode.Incompatible);
        }
        if (authorization.EnvironmentId != environment || authorization.AuthorityEpoch < 1 || authorization.AuthorityEpoch > authorization.RecordRevision) throw Corrupt();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var instances = new HashSet<string>(StringComparer.Ordinal);
        AuthorizationSnapshotV2ProfilesItem? selected = null;
        foreach (var profile in authorization.Profiles)
        {
            CorePackage.ValidateIdentifier(profile.ProfileId);
            if (!names.Add(profile.ProfileId) || !IsLowerHex(profile.ProfileInstanceId, 32) || !instances.Add(profile.ProfileInstanceId)
                || profile.GrantEpoch > authorization.RecordRevision || !IsLowerHex(profile.InternalToken) ||
                profile.ExternalHttpToken is not null && (!IsLowerHex(profile.ExternalHttpToken) || profile.ExternalHttpToken == profile.InternalToken)) throw Corrupt();
            if (profile.ProfileId == profileId) selected = profile;
        }
        if (selected is null || (selected.ScopeMask & 1) == 0) throw new HistoryReadException(HistoryReadCode.Unauthorized);
        if (expectedAuthorityEpoch is { } authority && (authorization.AuthorityEpoch != authority || selected.GrantEpoch != expectedGrantEpoch ||
            expectedProfileInstanceId is not null && selected.ProfileInstanceId != expectedProfileInstanceId))
            throw new HistoryReadException(HistoryReadCode.SubjectChanged);
        return new Subject(authorization.RecordRevision, authorization.AuthorityEpoch, selected.GrantEpoch, selected.ProfileInstanceId);
    }

    private static async Task<byte[]> ReadLimited(FileStream file, int maximum, CancellationToken cancellation)
    {
        if (file.Length is < 1 || file.Length > maximum) throw Corrupt();
        byte[] bytes = new byte[checked((int)file.Length)];
        try { await file.ReadExactlyAsync(bytes.AsMemory(), cancellation).ConfigureAwait(false); return bytes; }
        catch { Array.Clear(bytes); throw; }
    }
    internal static bool IsLowerHex(string value, int expectedLength = 64)
    {
        if (value.Length != expectedLength) return false;
        foreach (char c in value) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
    private sealed record Subject(long Revision, long AuthorityEpoch, long GrantEpoch, string ProfileInstanceId);
    private static HistoryReadException Corrupt() => new(HistoryReadCode.CorruptStore);
}
