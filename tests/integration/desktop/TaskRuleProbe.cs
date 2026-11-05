using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SkylinesLink.Deployment;
using SkylinesLink.Shared.Tasks;

internal static class TaskRuleProbe
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "task-rules") return 2;
        string path = Path.GetFullPath(args[1]);
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Task rule vectors exceed their bound.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("Unknown Task vector format.");
        var defaults = root.GetProperty("defaults");
        var hashes = root.GetProperty("hashes").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        if (hashes.Count < 1 || hashes.Count > 16) throw new InvalidDataException("Task hash vector count invalid.");
        foreach (var sample in hashes.Values)
        {
            string kind = Text(sample, "kind"), id = Pick(sample, sample, defaults, "taskId").GetString()!;
            string actual = kind == "create" ? TaskJournalRules.ComputeCreateHash(Source(Pick(sample, sample, defaults, "source")), Goal(Pick(sample, sample, defaults, "goal"))) :
                kind == "reviseGoal" ? TaskJournalRules.ComputeReviseHash(id, Counter(sample, sample, "expectedGoalRevision", 1), Counter(sample, sample, "expectedControlVersion", 1),
                    Source(Pick(sample, sample, defaults, "source")), Goal(Pick(sample, sample, defaults, "goal"))) :
                kind == "stop" ? TaskJournalRules.ComputeStopHash(id, Counter(sample, sample, "expectedControlVersion", 1)) : throw new InvalidDataException("Unknown Task hash kind.");
            Require(actual == Text(sample, "expected"), "fixed hash " + Text(sample, "name"));
        }
        int transitions = 0; TaskTransitionInput validStop = default;
        foreach (var sample in root.GetProperty("transitions").EnumerateArray())
        {
            if (++transitions > 32) throw new InvalidDataException("Task transition vectors exceed their bound.");
            string mode = Text(sample, "current"), id = defaults.GetProperty("taskId").GetString()!;
            var owner = Owner(defaults.GetProperty("actor"));
            TaskStateView? current = mode == "none" ? null : new TaskStateView(id, Text(defaults.GetProperty("source"), "cityId"),
                owner.ProfileId, owner.ProfileInstanceId, 1, mode == "stopped" ? 2 : 1, mode == "stopped");
            TaskStateView? before = current;
            var hash = hashes[Text(sample, "hash")];
            string kind = Text(sample, "kind");
            long expectedGoal = kind == "reviseGoal" ? Counter(sample, hash, "expectedGoalRevision", 1) : 0;
            long expectedControl = kind == "create" ? 0 : Counter(sample, hash, "expectedControlVersion", 1);
            TaskGoalView? goal = kind == "stop" ? null : Goal(Pick(sample, hash, defaults, "goal"), Flag(sample, "invalidScalar"));
            var input = new TaskTransitionInput(kind == "create" ? TaskDecisionKind.Create : kind == "reviseGoal" ? TaskDecisionKind.ReviseGoal : TaskDecisionKind.Stop,
                Pick(sample, hash, defaults, "taskId").GetString()!, Owner(Pick(sample, hash, defaults, "actor")), Source(Pick(sample, hash, defaults, "source")),
                Flag(sample, "badHash") ? new string('0', 64) : Text(hash, "expected"), expectedGoal, kind == "create" ? 1 : kind == "stop" ? 0 : expectedGoal + 1,
                expectedControl, kind == "create" ? 1 : kind == "stop" ? expectedControl + 1 : expectedControl, goal);
            if (Text(sample, "name") == "stop-menu") validStop = input;
            bool valid = TaskJournalRules.TryTransition(current, input, out var next);
            Require(valid == sample.GetProperty("valid").GetBoolean(), "transition " + Text(sample, "name"));
            Require(SameState(before, current), "original state unchanged " + Text(sample, "name"));
            if (valid)
                Require(next.TaskId == id && next.CityId == Text(defaults.GetProperty("source"), "cityId") && next.ProfileId == owner.ProfileId &&
                    next.ProfileInstanceId == owner.ProfileInstanceId && next.GoalRevision == sample.GetProperty("goalRevision").GetInt64() &&
                    next.ControlVersion == sample.GetProperty("controlVersion").GetInt64() && next.IsStopped == sample.GetProperty("isStopped").GetBoolean(),
                    "next state " + Text(sample, "name"));
            else Require(next.TaskId == null && next.CityId == null && next.GoalRevision == 0 && next.ControlVersion == 0 && !next.IsStopped,
                "rejected transition has no candidate next state " + Text(sample, "name"));
        }
        Require(transitions > 0, "nonempty transition vectors");
        // These type-default states cannot appear in JSON; exercise the actual
        // value-type boundary rather than substituting a separate parser rule.
        Require(!TaskJournalRules.TryTransition(null, default, out _), "default transition input rejected");
        Require(!TaskJournalRules.TryTransition(default(TaskStateView), validStop, out _), "default current state rejected with an otherwise valid Stop");
        var assembly = typeof(TaskJournalRules).Assembly;
        Require(assembly == typeof(CorePackage).Assembly && assembly != typeof(TaskRuleProbe).Assembly, "actual Deployment rule ownership");
        string[] references = assembly.GetReferencedAssemblies().Select(value => value.Name!).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] consumerReferences = typeof(TaskRuleProbe).Assembly.GetReferencedAssemblies().Select(value => value.Name!).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Require(!references.Any(Forbidden) && !consumerReferences.Any(Forbidden) && !AppDomain.CurrentDomain.GetAssemblies().Any(value => Forbidden(value.GetName().Name!)),
            "BCL-only Deployment dependency closure");
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "task-rules", runtimeMajor = Environment.Version.Major, managed = true,
            ruleAssembly = assembly.GetName().Name, hashes = hashes.Count, transitions, references, consumerReferences }));
        return 0;
    }

    private static bool Forbidden(string name) => name == "Newtonsoft.Json" || name == "SkylinesLink.Application" || name == "SkylinesLink.Contracts" || name == "SkylinesLink.Infrastructure";
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("Task rule probe failed: " + label); }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static JsonElement Pick(JsonElement sample, JsonElement hash, JsonElement defaults, string name) =>
        sample.TryGetProperty(name, out var direct) ? direct : hash.TryGetProperty(name, out var inherited) ? inherited : defaults.GetProperty(name);
    private static long Counter(JsonElement sample, JsonElement hash, string name, long fallback) =>
        sample.TryGetProperty(name, out var direct) ? direct.GetInt64() : hash.TryGetProperty(name, out var inherited) ? inherited.GetInt64() : fallback;
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.GetBoolean();
    private static TaskSourceView Source(JsonElement value) => value.ValueKind == JsonValueKind.Null ? default : new TaskSourceView(Text(value, "instanceId"),
        value.GetProperty("generation").GetInt64(), Text(value, "state"), value.GetProperty("cityId").ValueKind == JsonValueKind.Null ? null : Text(value, "cityId"));
    private static TaskOwnerView Owner(JsonElement value) => value.ValueKind == JsonValueKind.Null ? default :
        new TaskOwnerView(Text(value, "profileId"), Text(value, "profileInstanceId"), value.GetProperty("authorityEpoch").GetInt64(), value.GetProperty("grantEpoch").GetInt64());
    private static TaskGoalView Goal(JsonElement value, bool invalidScalar = false) => value.ValueKind == JsonValueKind.Null ? default :
        new TaskGoalView(invalidScalar ? "\ud800" : Text(value, "title"), Text(value, "requestText"),
            value.GetProperty("constraintsText").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            value.GetProperty("acceptanceText").EnumerateArray().Select(item => item.GetString()!).ToArray());
    private static bool SameState(TaskStateView? a, TaskStateView? b) => !a.HasValue ? !b.HasValue : b.HasValue && a.Value.TaskId == b.Value.TaskId &&
        a.Value.CityId == b.Value.CityId && a.Value.ProfileId == b.Value.ProfileId && a.Value.ProfileInstanceId == b.Value.ProfileInstanceId &&
        a.Value.GoalRevision == b.Value.GoalRevision && a.Value.ControlVersion == b.Value.ControlVersion && a.Value.IsStopped == b.Value.IsStopped;
}
