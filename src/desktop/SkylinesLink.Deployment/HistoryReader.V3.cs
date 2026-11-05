using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Tasks;

namespace SkylinesLink.Deployment;

public static partial class HistoryReader
{
    public static NativeHistoryRequestV3 ParseRequestV3(string json)
    {
        try { return FreezeRequestV3(DeploymentContract.ParseNativeHistoryRequestV3(json)); }
        catch (HistoryReadException) { throw; }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or System.Text.Json.JsonException)
        { throw InvalidRequest(); }
    }

    public static async Task<NativeHistoryCaptureV3> CaptureV3Async(NativeHistoryRequestV3 request, CancellationToken cancellation)
    {
        var frozen = FreezeRequestV3(request);
        var captured = await CaptureMixedAsync(frozen.EnvironmentId, frozen.ProfileId, frozen.ExpectedAuthorityEpoch,
            frozen.ExpectedGrantEpoch, frozen.ExpectedProfileInstanceId,
            bytes => HistoryCheckpointV2.SelectV3(bytes, frozen.EnvironmentId, frozen.Query), cancellation).ConfigureAwait(false);
        var subject = captured.Subject;
        var selected = captured.Selection;
        return new NativeHistoryCaptureV3 { FormatVersion = 3, EnvironmentId = frozen.EnvironmentId, ProfileId = frozen.ProfileId,
            ProfileInstanceId = subject.ProfileInstanceId, AuthorizationRecordRevision = subject.Revision,
            AuthorityEpoch = subject.AuthorityEpoch, GrantEpoch = subject.GrantEpoch,
            Source = selected.Source, Query = frozen.Query, Result = selected.Result };
    }

    private static NativeHistoryRequestV3 FreezeRequestV3(NativeHistoryRequestV3 request)
    {
        if (request is null) throw InvalidRequest();
        try
        {
            string environment = request.EnvironmentId, profile = request.ProfileId;
            long? authority = request.ExpectedAuthorityEpoch, grant = request.ExpectedGrantEpoch;
            string? instance = request.ExpectedProfileInstanceId;
            ValidateMixedIdentity(environment, profile, authority, grant, instance);
            if (request.FormatVersion != 3) throw InvalidRequest();
            return new NativeHistoryRequestV3 { FormatVersion = 3, EnvironmentId = environment, ProfileId = profile,
                ExpectedAuthorityEpoch = authority, ExpectedGrantEpoch = grant, ExpectedProfileInstanceId = instance,
                Query = FreezeQueryV3(request.Query) };
        }
        catch (HistoryReadException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NullReferenceException)
        { throw InvalidRequest(); }
    }

    private static NativeHistoryQueryV3 FreezeQueryV3(NativeHistoryQueryV3 query)
    {
        switch (query)
        {
            case NativeHistoryQueryV3Tasks tasks when tasks.Kind == "tasks":
                return new NativeHistoryQueryV3Tasks { Kind = "tasks" };
            case NativeHistoryQueryV3TaskStopDecision stopped when stopped.Kind == "taskStopDecision":
                string id = stopped.TaskId;
                long expected = stopped.ExpectedControlVersion;
                if (!TaskJournalRules.IsTextValid(id, 128) || expected < 1 || expected > TaskJournalRules.MaximumCounter) throw InvalidRequest();
                return new NativeHistoryQueryV3TaskStopDecision { Kind = "taskStopDecision", TaskId = id, ExpectedControlVersion = expected };
            default:
                // Preserve the immutable four-query validator. Never widen its
                // union, or silently reinterpret a new selector as an old one.
                return FromV2Query(FreezeQuery(ToV2Query(query)));
        }
    }

    internal static NativeHistoryQuery ToV2Query(NativeHistoryQueryV3 query) => query switch
    {
        NativeHistoryQueryV3Operation value => new NativeHistoryQueryOperation { Kind = value.Kind, Selector = value.Selector },
        NativeHistoryQueryV3Task value => new NativeHistoryQueryTask { Kind = value.Kind, TaskId = value.TaskId },
        NativeHistoryQueryV3TaskDecision value => new NativeHistoryQueryTaskDecision { Kind = value.Kind, SubmissionKey = value.SubmissionKey },
        NativeHistoryQueryV3TaskGoal value => new NativeHistoryQueryTaskGoal { Kind = value.Kind, TaskId = value.TaskId,
            GoalRevision = value.GoalRevision, Section = value.Section, Offset = value.Offset, Limit = value.Limit },
        _ => throw InvalidRequest()
    };

    private static NativeHistoryQueryV3 FromV2Query(NativeHistoryQuery query) => query switch
    {
        NativeHistoryQueryOperation value => new NativeHistoryQueryV3Operation { Kind = value.Kind, Selector = value.Selector },
        NativeHistoryQueryTask value => new NativeHistoryQueryV3Task { Kind = value.Kind, TaskId = value.TaskId },
        NativeHistoryQueryTaskDecision value => new NativeHistoryQueryV3TaskDecision { Kind = value.Kind, SubmissionKey = value.SubmissionKey },
        NativeHistoryQueryTaskGoal value => new NativeHistoryQueryV3TaskGoal { Kind = value.Kind, TaskId = value.TaskId,
            GoalRevision = value.GoalRevision, Section = value.Section, Offset = value.Offset, Limit = value.Limit },
        _ => throw InvalidRequest()
    };
}
