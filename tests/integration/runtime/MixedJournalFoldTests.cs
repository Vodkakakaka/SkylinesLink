using System;
using System.Collections.Generic;
using System.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;

namespace SkylinesLink.RuntimeProbe
{
    internal static class MixedJournalFoldTests
    {
        private static int checks;
        private static readonly TaskActor Owner = new TaskActor("probe", new string('a', 32), 1, 1);
        private static readonly WorldContextSnapshot Source = new WorldContextSnapshot("instance-a", 1, "ready", "city-a");
        private static readonly LoadTarget Target = new LoadTarget("probe", "save", "v1");
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static void Invalid(Action action, string label)
        { try { action(); } catch (ArgumentException) { Check(true, label); return; } throw new InvalidOperationException("Expected mixed value rejection: " + label); }
        internal static int Run()
        {
            checks = 0;
            Program.Stage = "mixed journal immutable values and explicit versions";
            ValuesAndHashes();
            Program.Stage = "mixed journal shared operation reducer and exact global sequence";
            MixedSequenceAndLegacy();
            Program.Stage = "mixed journal invalid CAS/hash/owner leaves the valid task prefix";
            InvalidTaskDecisions();
            Program.Stage = "mixed journal one global request key set and bounded tasks";
            GlobalKeysAndLimits();
            return checks;
        }
        private static TaskStatedGoal Goal(string title = "goal") => new TaskStatedGoal(title, "current user request", new[] { "stated constraint" }, new[] { "stated acceptance" });
        private static SubmissionKey Key(string request) => new SubmissionKey("shared-context", request);
        private static TaskDecision Decision(string key, WorldContextSnapshot source, string hash, TaskActor? actor = null) => new TaskDecision(Key(key), actor ?? Owner, source, hash);
        private static TaskCreated Create(string task = "task", string key = "create", WorldContextSnapshot? source = null)
        {
            var goal = Goal(); var context = source ?? Source;
            return new TaskCreated(task, Decision(key, context, TaskCommandHash.Create(context, goal)), goal);
        }
        private static TaskGoalRevised Revise(string key = "revise", long expected = 1, long next = 2, long control = 1,
            WorldContextSnapshot? source = null, TaskActor? actor = null, bool badHash = false)
        {
            var goal = Goal("revised goal"); var context = source ?? new WorldContextSnapshot("instance-b", 5, "ready", "city-a");
            return new TaskGoalRevised("task", expected, next, control,
                Decision(key, context, badHash ? new string('0', 64) : TaskCommandHash.Revise("task", expected, control, context, goal), actor), goal);
        }
        private static TaskStopped Stop(string key = "stop", long expected = 1, long next = 2, WorldContextSnapshot? source = null, TaskActor? actor = null) =>
            new TaskStopped("task", expected, next, Decision(key, source ?? new WorldContextSnapshot("later-instance", 9, "menu", null), TaskCommandHash.Stop("task", expected), actor));
        private static List<JournalRecord> Records(params JournalEntry[] entries)
        {
            var result = new List<JournalRecord> { new JournalRecord(1, "probe", new StoreOpened("store")) };
            foreach (var entry in entries) result.Add(new JournalRecord(result.Count + 1, "probe", entry, 3));
            return result;
        }
        private static OperationIntent Intent(SubmissionKey? key = null, string? identity = null, string profile = "probe", long epoch = 1) =>
            new OperationIntent("op", Source.InstanceId, key ?? Key("operation"), OperationCommandHash.Compute(Source, Target), profile, epoch, epoch, Source, Target, identity);
        private static OperationFinished FalseFinal() => new OperationFinished("op", "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE",
            new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null));

        private static void ValuesAndHashes()
        {
            string emoji = char.ConvertFromUtf32(0x1f3d9);
            string[] constraints = { "original" }; string[] acceptance = { "acceptance" };
            var goal = new TaskStatedGoal(string.Concat(Enumerable.Repeat(emoji, 128)), "request", constraints, acceptance);
            constraints[0] = "mutated"; acceptance[0] = "mutated";
            Check(goal.Title.Length == 256 && goal.ConstraintsText.Single() == "original" && goal.AcceptanceText.Single() == "acceptance" &&
                ((IList<string>)goal.ConstraintsText).IsReadOnly, "Task intent counts Unicode scalars and owns immutable copies of external arrays");
            Invalid(() => new TaskStatedGoal(new string('x', 129), "request", Array.Empty<string>(), Array.Empty<string>()), "title scalar bound is enforced");
            Invalid(() => new TaskStatedGoal("title", new string('x', 2049), Array.Empty<string>(), Array.Empty<string>()), "request scalar bound is enforced");
            Invalid(() => new TaskStatedGoal("title", "request", Enumerable.Repeat("x", 17).ToArray(), Array.Empty<string>()), "stated constraint list is finite");
            Invalid(() => new TaskStatedGoal("title", "request", new[] { "\ud800" }, Array.Empty<string>()), "unpaired surrogate cannot become a different UTF8 intent");
            Invalid(() => Intent(identity: new string('b', 32), profile: "invalid/name"), "recorded operation actor has a valid profile identity");
            Invalid(() => Intent(identity: new string('b', 32), epoch: 0), "recorded operation actor epochs are positive");
            Invalid(() => new JournalRecord(1, "probe", new StoreOpened("store"), 4), "journal ordinary values retain an explicit supported schema marker");
            var a = new TaskStatedGoal("title", "request", new[] { "a", "bc" }, Array.Empty<string>());
            var b = new TaskStatedGoal("title", "request", new[] { "ab", "c" }, Array.Empty<string>());
            Check(TaskCommandHash.Create(Source, a) != TaskCommandHash.Create(Source, b), "length-prefixed task hashing distinguishes delimiter-like string partitions");
            Check(TaskCommandHash.Create(Source, a) == TaskCommandHash.Create(new WorldContextSnapshot(Source.InstanceId, Source.Generation, Source.State, Source.CityId),
                new TaskStatedGoal("title", "request", new[] { "a", "bc" }, Array.Empty<string>())), "equivalent stated intent hashes independently of object or generated task identity");
            Check(TaskCommandHash.Revise("task", 1, 1, Source, a) != TaskCommandHash.Revise("task", 1, 2, Source, a) &&
                TaskCommandHash.Stop("task", 1) != TaskCommandHash.Stop("other-task", 1), "target and expected control version remain part of their command hashes");
        }

        private static void MixedSequenceAndLegacy()
        {
            var actorAfterRestart = new TaskActor(Owner.ProfileId, Owner.ProfileInstanceId, 8, 10);
            var records = Records(new OperationAccepted(Intent(identity: new string('b', 32))), Create(),
                new CommitIntended("op", Target, new NativeHostIdentity(101, 123456)), Revise(actor: actorAfterRestart),
                new NativeTerminated("op", NativeLoadTaskState.RanToCompletion, false, Target), Stop(actor: actorAfterRestart), FalseFinal());
            var result = JournalFactsFold.FoldMixed(records); var task = result.Tasks.Single(); var operation = result.Operations.Single();
            Check(result.AllRecordsValid && result.ThroughSequence == 8 && operation.FinalSequence == 8 &&
                operation.Native!.LoadReturnValue == false && operation.Final!.Load.LoadReturnValue == false && operation.Final.MayHaveApplied,
                "mixed metadata never filters or renumbers the actual operation final or native false facts");
            Check(task.CityId == "city-a" && task.GoalRevision == 2 && task.ControlVersion == 2 && task.IsStopped &&
                task.CurrentGoal.Title == "revised goal" && task.GoalHistory.Select(item => item.Sequence).SequenceEqual(new long[] { 3, 5 }) &&
                task.Decisions.Select(item => item.Sequence).SequenceEqual(new long[] { 3, 5, 7 }) && task.Owner.ProfileInstanceId == Owner.ProfileInstanceId,
                "Task goal history, stable owner and decision evidence preserve their real global positions");
            Check(JournalFactsFold.Fold(records).ValidRecordCount == 1 && JournalFactsFold.Fold(records).Tasks.Count == 0,
                "strict old Fold rejects even operation-only revision 3 rather than claiming legacy fidelity");
            var legacy = Records(new OperationAccepted(Intent()), new CommitIntended("op", Target, new NativeHostIdentity(101, 123456)),
                new NativeTerminated("op", NativeLoadTaskState.RanToCompletion, false, Target), FalseFinal())
                .Select(record => new JournalRecord(record.Sequence, record.EnvironmentId, record.Entry)).ToArray();
            for (int index = 0; index < legacy.Length; index++)
            {
                var marked = (JournalRecord[])legacy.Clone(); var original = marked[index];
                marked[index] = new JournalRecord(original.Sequence, original.EnvironmentId, original.Entry, 3);
                Check(JournalFactsFold.Fold(marked).ValidRecordCount == index,
                    "strict revision 2 prefix stops exactly before a marked revision 3 header/accepted/commit/native/final");
            }
            Check(JournalFactsFold.Fold(legacy).AllRecordsValid && JournalFactsFold.Fold(legacy).Tasks.Count == 0 &&
                JournalFactsFold.FoldMixed(legacy).Operations.Single().Intent.ProfileInstanceId == null,
                "legacy operation facts stay valid and their unrecorded incarnation is never guessed");
            var unknownActor3 = legacy.Select(record => new JournalRecord(record.Sequence, record.EnvironmentId, record.Entry, 3)).ToArray();
            Check(JournalFactsFold.FoldMixed(unknownActor3).AllRecordsValid &&
                JournalFactsFold.FoldMixed(unknownActor3).Operations.Single().Intent.ProfileInstanceId == null,
                "explicit revision 3 may retain legacyUnrecorded operation actor evidence without fabricating identity");
            Check(JournalFactsFold.FoldMixed(new[] { legacy[0], new JournalRecord(2, "probe", new OperationAccepted(Intent(identity: new string('c', 32)))) }).ValidRecordCount == 1,
                "revision 2 ordinary values cannot smuggle recorded incarnation evidence through a codec that cannot preserve it");
            var unknown = Records(new OperationAccepted(Intent()), new CommitIntended("op", Target, new NativeHostIdentity(101, 123456)), Create(),
                new OperationFinished("op", "unknown", true, SubmissionKnowledge.Unknown, "RECOVERY_SEALED",
                    new PersistedLoadFacts(NativeLoadTaskState.Unknown, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)));
            Check(JournalFactsFold.FoldMixed(unknown).AllRecordsValid && JournalFactsFold.FoldMixed(unknown).Operations.Single().Native == null &&
                JournalFactsFold.FoldMixed(unknown).Operations.Single().Final!.MayHaveApplied,
                "Task records cannot turn a recovered unknown native commitment into a physical completion");
        }

        private static void InvalidTaskDecisions()
        {
            var foreignOwner = new TaskActor(Owner.ProfileId, new string('f', 32), 1, 1);
            foreach (var bad in new JournalEntry[] { Revise(expected: 2, next: 3), Revise(next: 3), Revise(control: 2),
                Revise(actor: foreignOwner), Revise(source: new WorldContextSnapshot("instance", 2, "ready", "other-city")),
                Revise(badHash: true), Stop(expected: 2, next: 3), Stop(actor: foreignOwner) })
            {
                var folded = JournalFactsFold.FoldMixed(Records(Create(), bad)); var task = folded.Tasks.Single();
                Check(!folded.AllRecordsValid && folded.ValidRecordCount == 2 && task.GoalRevision == 1 && task.ControlVersion == 1 &&
                    !task.IsStopped && task.GoalHistory.Count == 1 && task.Decisions.Count == 1,
                    "bad hash/CAS/owner/city decision leaves the valid prior task state and decision set untouched");
            }
            var notReady = Create(source: new WorldContextSnapshot("instance", 1, "loading", null));
            Check(JournalFactsFold.FoldMixed(Records(notReady)).ValidRecordCount == 1,
                "valid loading-context shape does not qualify Task creation for an unobserved city");
            var metadata2 = new[] { new JournalRecord(1, "probe", new StoreOpened("store")), new JournalRecord(2, "probe", Create()) };
            Check(JournalFactsFold.FoldMixed(metadata2).ValidRecordCount == 1 && JournalFactsFold.FoldMixed(metadata2).Tasks.Count == 0,
                "metadata cannot enter the mixed path marked as record 2");
            foreach (var source in new[] { new WorldContextSnapshot("later", 9, "menu", null), new WorldContextSnapshot("later", 9, "loading", null),
                new WorldContextSnapshot("later", 9, "ready", "other-city"), new WorldContextSnapshot("later", 9, "unavailable", null) })
            {
                var prefix = Records(Create(), Revise(), Stop(source: source)); var valid = JournalFactsFold.FoldMixed(prefix);
                Check(valid.AllRecordsValid && valid.Tasks.Single().IsStopped && valid.Tasks.Single().GoalRevision == 2,
                    "current-owner Stop is independent of the old goal/world and retains the latest stated revision");
                prefix.Add(new JournalRecord(5, "probe", Revise("after-stop", 2, 3, 2), 3));
                Check(JournalFactsFold.FoldMixed(prefix).ValidRecordCount == 4, "stopped tasks have no implicit revise/resume path");
                prefix[4] = new JournalRecord(5, "probe", Stop("second-stop", 2, 3, source), 3);
                Check(JournalFactsFold.FoldMixed(prefix).ValidRecordCount == 4, "a new Stop decision cannot advance an already stopped task");
            }
        }

        private static void GlobalKeysAndLimits()
        {
            var create = Create();
            var operationFirst = Records(new OperationAccepted(Intent(create.Decision.Key)), create);
            var taskFirst = Records(create, new OperationAccepted(Intent(create.Decision.Key)));
            Check(JournalFactsFold.FoldMixed(operationFirst).ValidRecordCount == 2 && JournalFactsFold.FoldMixed(operationFirst).Tasks.Count == 0 &&
                JournalFactsFold.FoldMixed(taskFirst).ValidRecordCount == 2 && JournalFactsFold.FoldMixed(taskFirst).Operations.Count == 0,
                "Task metadata and operation acceptance share one global request-key uniqueness rule in both orders");
            Check(JournalFactsFold.FoldMixed(Records(create, Revise(key: "create"))).ValidRecordCount == 2,
                "a metadata request identity cannot be reused as a different goal decision");
            var tasks = Records(Enumerable.Range(0, 9).Select(i => (JournalEntry)Create("task-" + i, "create-" + i)).ToArray());
            var bounded = JournalFactsFold.FoldMixed(tasks);
            Check(!bounded.AllRecordsValid && bounded.ValidRecordCount == 9 && bounded.Tasks.Count == 8,
                "Task quantity is bounded independently of the operation count");
            var history = Records(Create());
            for (int revision = 2; revision <= 128; revision++)
                history.Add(new JournalRecord(history.Count + 1, "probe", Revise("revision-" + revision, revision - 1, revision), 3));
            var maximum = JournalFactsFold.FoldMixed(history);
            Check(maximum.AllRecordsValid && maximum.ValidRecordCount == 129 && maximum.Tasks.Single().GoalHistory.Count == 128,
                "goal and decision histories stay bounded by the shared real 129-frame prefix");
            history.Add(new JournalRecord(130, "probe", Revise("overflow", 128, 129), 3));
            var overflow = JournalFactsFold.FoldMixed(history);
            Check(!overflow.AllRecordsValid && overflow.ValidRecordCount == 129 && overflow.Tasks.Single().GoalRevision == 128,
                "overflow does not mutate the last valid Task revision or invent an unrecorded history entry");
        }
    }
}
