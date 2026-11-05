using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>固定向量经过实际Application adapter；没有复制共享transition或hash算法。</summary>
    internal static class TaskSharedRulesTests
    {
        private static int checks;
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        internal static int Run()
        {
            checks = 0; Program.Stage = "fixed shared Task vectors through the actual Application adapters";
            string path = Path.Combine("contracts", "fixtures", "task-rules-vectors.json");
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Shared Task vectors exceed their test bound.");
            var document = JObject.Parse(File.ReadAllText(path)); var defaults = (JObject)document["defaults"]!;
            var hashes = ((JArray)document["hashes"]!).Cast<JObject>().ToDictionary(item => (string)item["name"]!, StringComparer.Ordinal);
            Check((int)document["formatVersion"]! == 1 && hashes.Count > 0 && hashes.Count <= 16,
                "shared Task vectors have one explicit small test format");
            foreach (var sample in hashes.Values)
            {
                string kind = (string)sample["kind"]!, id = (string)(sample["taskId"] ?? defaults["taskId"])!;
                long goalRevision = (long?)sample["expectedGoalRevision"] ?? 1, control = (long?)sample["expectedControlVersion"] ?? 1;
                string actual = kind == "create" ? TaskCommandHash.Create(Source(sample["source"] ?? defaults["source"]!), Goal(sample["goal"] ?? defaults["goal"]!)) :
                    kind == "reviseGoal" ? TaskCommandHash.Revise(id, goalRevision, control, Source(sample["source"] ?? defaults["source"]!), Goal(sample["goal"] ?? defaults["goal"]!)) :
                    kind == "stop" ? TaskCommandHash.Stop(id, control) : throw new InvalidDataException("Unknown shared Task hash kind.");
                Check(actual == (string)sample["expected"]!, "Application hash equals fixed independent baseline: " + (string)sample["name"]!);
            }
            var transitions = (JArray)document["transitions"]!;
            Check(transitions.Count > 0 && transitions.Count <= 32, "shared Task transition vectors remain finite");
            foreach (JObject sample in transitions)
            {
                string label = (string)sample["name"]!, current = (string)sample["current"]!;
                var records = new List<JournalRecord> { new JournalRecord(1, "shared-vectors", new StoreOpened(new string('c', 32)), 3) };
                string id = (string)defaults["taskId"]!;
                if (current != "none") records.Add(new JournalRecord(records.Count + 1, "shared-vectors", BaseCreate(defaults, hashes), 3));
                if (current == "stopped") records.Add(new JournalRecord(records.Count + 1, "shared-vectors", BaseStop(defaults, hashes), 3));
                var before = JournalFactsFold.FoldMixed(records); var prior = before.Tasks.SingleOrDefault();
                bool valid = false; var after = before;
                try
                {
                    var hash = hashes[(string)sample["hash"]!];
                    JournalEntry entry = Entry(sample, hash, defaults);
                    records.Add(new JournalRecord(records.Count + 1, "shared-vectors", entry, 3));
                    after = JournalFactsFold.FoldMixed(records); valid = after.AllRecordsValid;
                }
                catch (ArgumentException) { }
                Check(valid == (bool)sample["valid"]!, "Application transition agrees with fixed vector: " + label);
                if (valid)
                {
                    var state = after.Tasks.Single();
                    Check(state.TaskId == id && state.CityId == (string)defaults["source"]!["cityId"]! &&
                        state.Owner.ProfileId == (string)defaults["actor"]!["profileId"]! && state.Owner.ProfileInstanceId == (string)defaults["actor"]!["profileInstanceId"]! &&
                        state.GoalRevision == (long)sample["goalRevision"]! && state.ControlVersion == (long)sample["controlVersion"]! && state.IsStopped == (bool)sample["isStopped"]!,
                        "Application publishes only the specified next Task state: " + label);
                }
                else
                    Check(after.ThroughSequence == before.ThroughSequence && SameTask(prior, after.Tasks.SingleOrDefault()),
                        "rejected Application metadata leaves its complete valid prefix intact: " + label);
                Check(before.ThroughSequence == (current == "none" ? 1 : current == "active" ? 2 : 3) &&
                    (prior == null || prior.GoalRevision == 1 && prior.ControlVersion == (current == "stopped" ? 2 : 1) && prior.IsStopped == (current == "stopped")),
                    "folding another candidate does not mutate a previously published state: " + label);
            }
            return checks;
        }

        private static JToken Pick(JObject sample, JObject hash, JObject defaults, string name) =>
            sample.Property(name) != null ? sample[name]! : hash.Property(name) != null ? hash[name]! : defaults[name]!;
        private static string Scalar(JToken token) => token.Type == JTokenType.Null ? null! :
            (string?)token ?? throw new InvalidDataException("Shared Task vector scalar is missing.");
        private static long Counter(JObject sample, JObject hash, string name, long fallback) => (long?)(sample[name] ?? hash[name]) ?? fallback;
        private static JournalEntry Entry(JObject sample, JObject hash, JObject defaults)
        {
            string kind = (string)sample["kind"]!, id = Scalar(Pick(sample, hash, defaults, "taskId"));
            var actor = Actor(Pick(sample, hash, defaults, "actor")); var source = Source(Pick(sample, hash, defaults, "source"));
            string digest = (bool?)sample["badHash"] == true ? new string('0', 64) : (string)hash["expected"]!;
            var decision = new TaskDecision(new SubmissionKey("shared-vector-context", (string)sample["name"]!), actor, source, digest);
            if (kind == "create") return new TaskCreated(id, decision, Goal(Pick(sample, hash, defaults, "goal"), (bool?)sample["invalidScalar"] == true));
            long control = Counter(sample, hash, "expectedControlVersion", 1);
            if (kind == "stop") return new TaskStopped(id, control, control + 1, decision);
            long expected = Counter(sample, hash, "expectedGoalRevision", 1);
            return new TaskGoalRevised(id, expected, expected + 1, control, decision, Goal(Pick(sample, hash, defaults, "goal")));
        }
        private static TaskCreated BaseCreate(JObject defaults, IReadOnlyDictionary<string, JObject> hashes) => new TaskCreated((string)defaults["taskId"]!,
            new TaskDecision(new SubmissionKey("shared-vector-context", "base-create"), Actor(defaults["actor"]!), Source(defaults["source"]!), (string)hashes["create-unicode"]["expected"]!), Goal(defaults["goal"]!));
        private static TaskStopped BaseStop(JObject defaults, IReadOnlyDictionary<string, JObject> hashes) => new TaskStopped((string)defaults["taskId"]!, 1, 2,
            new TaskDecision(new SubmissionKey("shared-vector-context", "base-stop"), Actor(defaults["actor"]!), new WorldContextSnapshot("base-menu", 8, "menu", null), (string)hashes["stop-control-1"]["expected"]!));
        private static TaskActor Actor(JToken token) => token.Type == JTokenType.Null ? null! :
            new TaskActor((string)token["profileId"]!, (string)token["profileInstanceId"]!, (long)token["authorityEpoch"]!, (long)token["grantEpoch"]!);
        private static WorldContextSnapshot Source(JToken token) => token.Type == JTokenType.Null ? null! :
            new WorldContextSnapshot((string)token["instanceId"]!, (long)token["generation"]!, (string)token["state"]!, (string?)token["cityId"]);
        private static TaskStatedGoal Goal(JToken token, bool invalidScalar = false) => token.Type == JTokenType.Null ? null! :
            new TaskStatedGoal(invalidScalar ? "\ud800" : (string)token["title"]!, (string)token["requestText"]!,
                token["constraintsText"]!.Select(item => (string)item!).ToArray(), token["acceptanceText"]!.Select(item => (string)item!).ToArray());
        private static bool SameTask(JournalTaskFacts? before, JournalTaskFacts? after) => before == null ? after == null : after != null &&
            before.TaskId == after.TaskId && before.CityId == after.CityId && before.Owner.ProfileId == after.Owner.ProfileId &&
            before.Owner.ProfileInstanceId == after.Owner.ProfileInstanceId && before.GoalRevision == after.GoalRevision && before.ControlVersion == after.ControlVersion &&
            before.IsStopped == after.IsStopped && before.GoalHistory.Count == after.GoalHistory.Count && before.Decisions.Count == after.Decisions.Count;
    }
}
