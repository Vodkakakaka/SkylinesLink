using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SkylinesLink.Shared.Tasks
{
    // Borrowed synchronous views, not a second persisted Task model. The caller
    // owns and stabilizes the lists for validation, hashing and transition.
    internal readonly struct TaskSourceView
    {
        internal TaskSourceView(string instanceId, long generation, string state, string? cityId)
        { InstanceId = instanceId; Generation = generation; State = state; CityId = cityId; }
        internal string InstanceId { get; }
        internal long Generation { get; }
        internal string State { get; }
        internal string? CityId { get; }
    }

    internal readonly struct TaskOwnerView
    {
        internal TaskOwnerView(string profileId, string profileInstanceId, long authorityEpoch, long grantEpoch)
        { ProfileId = profileId; ProfileInstanceId = profileInstanceId; AuthorityEpoch = authorityEpoch; GrantEpoch = grantEpoch; }
        internal string ProfileId { get; }
        internal string ProfileInstanceId { get; }
        internal long AuthorityEpoch { get; }
        internal long GrantEpoch { get; }
    }

    internal readonly struct TaskGoalView
    {
        internal TaskGoalView(string title, string requestText, IReadOnlyList<string> constraintsText, IReadOnlyList<string> acceptanceText)
        { Title = title; RequestText = requestText; ConstraintsText = constraintsText; AcceptanceText = acceptanceText; }
        internal string Title { get; }
        internal string RequestText { get; }
        internal IReadOnlyList<string> ConstraintsText { get; }
        internal IReadOnlyList<string> AcceptanceText { get; }
    }

    internal readonly struct TaskStateView
    {
        internal TaskStateView(string taskId, string cityId, string profileId, string profileInstanceId,
            long goalRevision, long controlVersion, bool isStopped)
        { TaskId = taskId; CityId = cityId; ProfileId = profileId; ProfileInstanceId = profileInstanceId;
            GoalRevision = goalRevision; ControlVersion = controlVersion; IsStopped = isStopped; }
        internal string TaskId { get; }
        internal string CityId { get; }
        internal string ProfileId { get; }
        internal string ProfileInstanceId { get; }
        internal long GoalRevision { get; }
        internal long ControlVersion { get; }
        internal bool IsStopped { get; }
    }

    internal enum TaskDecisionKind { Create, ReviseGoal, Stop }

    internal readonly struct TaskTransitionInput
    {
        internal TaskTransitionInput(TaskDecisionKind kind, string taskId, TaskOwnerView actor, TaskSourceView source,
            string commandHash, long expectedGoalRevision, long goalRevision, long expectedControlVersion,
            long controlVersion, TaskGoalView? goal)
        { Kind = kind; TaskId = taskId; Actor = actor; Source = source; CommandHash = commandHash;
            ExpectedGoalRevision = expectedGoalRevision; GoalRevision = goalRevision;
            ExpectedControlVersion = expectedControlVersion; ControlVersion = controlVersion; Goal = goal; }
        internal TaskDecisionKind Kind { get; }
        internal string TaskId { get; }
        internal TaskOwnerView Actor { get; }
        internal TaskSourceView Source { get; }
        internal string CommandHash { get; }
        internal long ExpectedGoalRevision { get; }
        internal long GoalRevision { get; }
        internal long ExpectedControlVersion { get; }
        internal long ControlVersion { get; }
        internal TaskGoalView? Goal { get; }
    }

    /// <summary>Pure record3 Task rules; no keys, history, capacity, permissions or execution ownership.</summary>
    internal static class TaskJournalRules
    {
        internal const long MaximumCounter = 9007199254740991;
        internal const int MaximumTasks = 8;
        private const string HashDomain = "SkylinesLink/task-intent/1";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool IsTextValid(string? value, int maximum)
        {
            if (value == null || maximum < 1) return false;
            int count = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (char.IsHighSurrogate(current))
                { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
                else if (char.IsLowSurrogate(current)) return false;
                if (++count > maximum) return false;
            }
            return count > 0;
        }

        internal static bool IsSourceValid(TaskSourceView value) => IsTextValid(value.InstanceId, 128) && value.Generation >= 0 &&
            (value.State == "ready" ? value.CityId == null || IsTextValid(value.CityId, 128) :
                (value.State == "menu" || value.State == "loading" || value.State == "unavailable") && value.CityId == null);

        internal static bool IsProfileIdValid(string? value)
        {
            if (value == null || value.Length < 1 || value.Length > 64 || value == "." || value == ".." ||
                value.EndsWith(".", StringComparison.Ordinal)) return false;
            foreach (char part in value)
                if (!(part >= 'A' && part <= 'Z' || part >= 'a' && part <= 'z' || part >= '0' && part <= '9' ||
                    part == '.' || part == '_' || part == '-')) return false;
            return true;
        }

        internal static bool IsProfileInstanceIdValid(string? value) => IsLowerHex(value, 32);

        internal static bool IsGoalValid(TaskGoalView value) => IsTextValid(value.Title, 128) && IsTextValid(value.RequestText, 2048) &&
            IsTextListValid(value.ConstraintsText) && IsTextListValid(value.AcceptanceText);

        private static bool IsTextListValid(IReadOnlyList<string>? values)
        {
            if (values == null) return false;
            int count = values.Count;
            if (count < 0 || count > 16) return false;
            for (int index = 0; index < count; index++) if (!IsTextValid(values[index], 256)) return false;
            return true;
        }

        private static bool IsLowerHex(string? value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char part in value) if (!(part >= '0' && part <= '9' || part >= 'a' && part <= 'f')) return false;
            return true;
        }
        private static bool IsCounter(long value) => value >= 1 && value <= MaximumCounter;
        private static bool IsActorValid(TaskOwnerView actor) => IsProfileIdValid(actor.ProfileId) &&
            IsProfileInstanceIdValid(actor.ProfileInstanceId) && IsCounter(actor.AuthorityEpoch) && IsCounter(actor.GrantEpoch);
        private static bool IsStateValid(TaskStateView state) => IsTextValid(state.TaskId, 128) && IsTextValid(state.CityId, 128) &&
            IsProfileIdValid(state.ProfileId) && IsProfileInstanceIdValid(state.ProfileInstanceId) &&
            IsCounter(state.GoalRevision) && IsCounter(state.ControlVersion);

        internal static string ComputeCreateHash(TaskSourceView source, TaskGoalView goal)
        {
            if (!IsSourceValid(source) || !IsGoalValid(goal)) throw new ArgumentException("Task intent source or goal invalid.");
            return CreateHash(source, goal);
        }
        internal static string ComputeReviseHash(string taskId, long expectedGoalRevision, long expectedControlVersion,
            TaskSourceView source, TaskGoalView goal)
        {
            if (!IsTextValid(taskId, 128) || !IsCounter(expectedGoalRevision) || !IsCounter(expectedControlVersion) ||
                !IsSourceValid(source) || !IsGoalValid(goal)) throw new ArgumentException("Task revision intent invalid.");
            return ReviseHash(taskId, expectedGoalRevision, expectedControlVersion, source, goal);
        }
        internal static string ComputeStopHash(string taskId, long expectedControlVersion)
        {
            if (!IsTextValid(taskId, 128) || !IsCounter(expectedControlVersion)) throw new ArgumentException("Task stop intent invalid.");
            return StopHash(taskId, expectedControlVersion);
        }

        // A false result leaves all caller-owned state, key sets and history
        // untouched. Allocation/provider failures remain failures, not bad rows.
        internal static bool TryTransition(TaskStateView? current, TaskTransitionInput input, out TaskStateView next)
        {
            next = default;
            if (!IsTextValid(input.TaskId, 128) || !IsActorValid(input.Actor) || !IsSourceValid(input.Source) ||
                !IsLowerHex(input.CommandHash, 64)) return false;
            if (input.Kind == TaskDecisionKind.Create)
            {
                if (current.HasValue || input.ExpectedGoalRevision != 0 || input.GoalRevision != 1 ||
                    input.ExpectedControlVersion != 0 || input.ControlVersion != 1 || input.Source.State != "ready" || input.Source.CityId == null ||
                    !input.Goal.HasValue || !IsGoalValid(input.Goal.Value) || input.CommandHash != CreateHash(input.Source, input.Goal.Value)) return false;
                next = new TaskStateView(input.TaskId, input.Source.CityId!, input.Actor.ProfileId,
                    input.Actor.ProfileInstanceId, 1, 1, false);
                return true;
            }
            if (!current.HasValue || !IsStateValid(current.Value)) return false;
            var state = current.Value;
            if (state.TaskId != input.TaskId || state.IsStopped || state.ProfileId != input.Actor.ProfileId ||
                state.ProfileInstanceId != input.Actor.ProfileInstanceId) return false;
            if (input.Kind == TaskDecisionKind.ReviseGoal)
            {
                if (input.Source.State != "ready" || input.Source.CityId != state.CityId ||
                    input.ExpectedGoalRevision != state.GoalRevision || input.ExpectedControlVersion != state.ControlVersion ||
                    state.GoalRevision >= MaximumCounter || input.GoalRevision != state.GoalRevision + 1 ||
                    input.ControlVersion != state.ControlVersion || !input.Goal.HasValue || !IsGoalValid(input.Goal.Value) ||
                    input.CommandHash != ReviseHash(input.TaskId, input.ExpectedGoalRevision, input.ExpectedControlVersion,
                        input.Source, input.Goal.Value)) return false;
                next = new TaskStateView(state.TaskId, state.CityId, state.ProfileId, state.ProfileInstanceId,
                    input.GoalRevision, state.ControlVersion, false);
                return true;
            }
            if (input.Kind == TaskDecisionKind.Stop)
            {
                // Source is audit-only here: it need not be the goal's city,
                // ready world, generation or instance, and is not hashed.
                if (input.Goal.HasValue || input.ExpectedGoalRevision != 0 || input.GoalRevision != 0 ||
                    input.ExpectedControlVersion != state.ControlVersion || state.ControlVersion >= MaximumCounter ||
                    input.ControlVersion != state.ControlVersion + 1 ||
                    input.CommandHash != StopHash(input.TaskId, input.ExpectedControlVersion)) return false;
                next = new TaskStateView(state.TaskId, state.CityId, state.ProfileId, state.ProfileInstanceId,
                    state.GoalRevision, input.ControlVersion, true);
                return true;
            }
            return false;
        }

        // Count once before allocating the reusable field buffer. This adds a
        // bounded text scan so short intents do not allocate the maximum size.
        private static string CreateHash(TaskSourceView source, TaskGoalView goal)
        {
            string generation = Word(source.Generation), constraints = Word(goal.ConstraintsText.Count), acceptance = Word(goal.AcceptanceText.Count);
            int maximum = MaxFieldBytes(MaxFieldBytes(0, HashDomain), "create");
            maximum = MeasureSource(maximum, source, generation);
            maximum = MeasureGoal(maximum, goal, constraints, acceptance);
            using (var hash = new CanonicalHash(BoundedFieldBytes(maximum, 8192)))
            {
                hash.Append(HashDomain); hash.Append("create"); AddSource(hash, source, generation);
                AddGoal(hash, goal, constraints, acceptance); return hash.Finish();
            }
        }
        private static string ReviseHash(string taskId, long expectedGoalRevision, long expectedControlVersion,
            TaskSourceView source, TaskGoalView goal)
        {
            string expectedGoal = Word(expectedGoalRevision), expectedControl = Word(expectedControlVersion);
            string generation = Word(source.Generation), constraints = Word(goal.ConstraintsText.Count), acceptance = Word(goal.AcceptanceText.Count);
            int maximum = MaxFieldBytes(MaxFieldBytes(MaxFieldBytes(0, HashDomain), "reviseGoal"), taskId);
            maximum = MaxFieldBytes(MaxFieldBytes(maximum, expectedGoal), expectedControl);
            maximum = MeasureSource(maximum, source, generation);
            maximum = MeasureGoal(maximum, goal, constraints, acceptance);
            using (var hash = new CanonicalHash(BoundedFieldBytes(maximum, 8192)))
            {
                hash.Append(HashDomain); hash.Append("reviseGoal"); hash.Append(taskId);
                hash.Append(expectedGoal); hash.Append(expectedControl);
                AddSource(hash, source, generation); AddGoal(hash, goal, constraints, acceptance); return hash.Finish();
            }
        }
        private static string StopHash(string taskId, long expectedControlVersion)
        {
            string expectedControl = Word(expectedControlVersion);
            int maximum = MaxFieldBytes(MaxFieldBytes(MaxFieldBytes(0, HashDomain), "stop"), taskId);
            maximum = MaxFieldBytes(maximum, expectedControl);
            using (var hash = new CanonicalHash(BoundedFieldBytes(maximum, 512)))
            { hash.Append(HashDomain); hash.Append("stop"); hash.Append(taskId); hash.Append(expectedControl); return hash.Finish(); }
        }
        private static string Word(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static int MaxFieldBytes(int maximum, string? value) => value == null ? maximum : Math.Max(maximum, Utf8.GetByteCount(value));
        private static int BoundedFieldBytes(int value, int maximum)
        {
            if (value < 1 || value > maximum) throw new ArgumentException("Task hash field exceeds its scalar bound.");
            return value;
        }
        private static int MeasureSource(int maximum, TaskSourceView source, string generation)
        { return MaxFieldBytes(MaxFieldBytes(MaxFieldBytes(MaxFieldBytes(maximum, source.InstanceId), generation), source.State), source.CityId); }
        private static int MeasureGoal(int maximum, TaskGoalView goal, string constraints, string acceptance)
        {
            maximum = MaxFieldBytes(MaxFieldBytes(MaxFieldBytes(maximum, goal.Title), goal.RequestText), constraints);
            for (int index = 0; index < goal.ConstraintsText.Count; index++) maximum = MaxFieldBytes(maximum, goal.ConstraintsText[index]);
            maximum = MaxFieldBytes(maximum, acceptance);
            for (int index = 0; index < goal.AcceptanceText.Count; index++) maximum = MaxFieldBytes(maximum, goal.AcceptanceText[index]);
            return maximum;
        }
        private static void AddSource(CanonicalHash hash, TaskSourceView source, string generation)
        { hash.Append(source.InstanceId); hash.Append(generation); hash.Append(source.State); hash.Append(source.CityId); }
        private static void AddGoal(CanonicalHash hash, TaskGoalView goal, string constraints, string acceptance)
        {
            hash.Append(goal.Title); hash.Append(goal.RequestText); hash.Append(constraints);
            for (int index = 0; index < goal.ConstraintsText.Count; index++) hash.Append(goal.ConstraintsText[index]);
            hash.Append(acceptance);
            for (int index = 0; index < goal.AcceptanceText.Count; index++) hash.Append(goal.AcceptanceText[index]);
        }

        // Keep the existing length-prefixed byte grammar while avoiding the
        // complete intent List<byte> plus ToArray allocation on every hash.
        private sealed class CanonicalHash : IDisposable
        {
            private readonly SHA256 sha = SHA256.Create();
            private readonly byte[] header = new byte[5];
            private readonly byte[] field;
            internal CanonicalHash(int maximumFieldBytes) { field = new byte[maximumFieldBytes]; }
            internal void Append(string? value)
            {
                if (value == null)
                { header[0] = 0; sha.TransformBlock(header, 0, 1, header, 0); return; }
                int length = Utf8.GetByteCount(value);
                if (length > field.Length) throw new ArgumentException("Task hash field exceeds its scalar bound.");
                Utf8.GetBytes(value, 0, value.Length, field, 0);
                header[0] = 1;
                uint size = checked((uint)length);
                for (int index = 0; index < 4; index++) header[index + 1] = (byte)(size >> (index * 8));
                sha.TransformBlock(header, 0, header.Length, header, 0);
                if (length > 0) sha.TransformBlock(field, 0, length, field, 0);
            }
            internal string Finish()
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                byte[] digest = sha.Hash ?? throw new InvalidOperationException("Task hash was not produced.");
                var text = new char[64];
                try
                {
                    const string alphabet = "0123456789abcdef";
                    for (int index = 0; index < digest.Length; index++)
                    { text[index * 2] = alphabet[digest[index] >> 4]; text[index * 2 + 1] = alphabet[digest[index] & 15]; }
                    return new string(text);
                }
                finally { Array.Clear(digest, 0, digest.Length); Array.Clear(text, 0, text.Length); }
            }
            public void Dispose()
            { try { sha.Dispose(); } finally { Array.Clear(header, 0, header.Length); Array.Clear(field, 0, field.Length); } }
        }
    }
}
