using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    internal static class MixedJournalFrameTests
    {
        private static int checks;
        internal static int MaximumMetadataPayloadObserved { get; private set; }
        private static readonly string Incarnation = new string('a', 32);
        private const string EnvironmentId = "mixed-frame-probe";
        private static void Check(bool value, string label) { Program.Assert(value, label); checks++; }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException ||
                error is NotSupportedException || error is SkylinesLink.Contracts.ContractValidationException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected mixed frame rejection: " + label);
        }
        private static WorldContextSnapshot Ready(long generation = 1, string city = "mixed-city") => new WorldContextSnapshot("mixed-instance", generation, "ready", city);
        private static TaskActor Actor() => new TaskActor("planner", Incarnation, 1, 2);
        private static TaskDecision Decision(string key, WorldContextSnapshot source, string hash) =>
            new TaskDecision(new SubmissionKey("mixed-write", key), Actor(), source, hash);
        private static TaskStatedGoal Goal(string text = "Plan a complete district") =>
            new TaskStatedGoal("District plan", text, new[] { "Keep the existing station" }, new[] { "Verify the actual resulting geometry" });

        internal static async Task<int> RunAsync()
        {
            checks = 0; MaximumMetadataPayloadObserved = 0;
            Program.Stage = "explicit mixed journal frame and revision boundaries";
            var records = Records();
            var decoded = new List<JournalRecord>();
            using (var stream = new MemoryStream())
            {
                foreach (var record in records)
                {
                    var frame = JournalFrameCodec.EncodeMixed(record, out int length);
                    try { Check(frame[4] == record.SchemaRevision, "mixed encoder preserves the explicit frame revision"); stream.Write(frame, 0, length); }
                    finally { Array.Clear(frame, 0, frame.Length); }
                }
                stream.Position = 0; JournalRecord? value;
                while ((value = JournalFrameCodec.ReadMixed(stream)) != null) decoded.Add(value);
                Check(stream.Position == stream.Length && decoded.Count == records.Count, "mixed reader consumes the exact complete sequence");
            }
            var folded = JournalFactsFold.FoldMixed(decoded);
            Check(folded.AllRecordsValid && folded.ValidRecordCount == 8 && folded.Tasks.Count == 1,
                "mixed Task and operation facts share one complete global sequence");
            Check(folded.Operations.Single().FinalSequence == 7 && folded.Operations.Single().Final!.Load.LoadReturnValue == false,
                "interleaved metadata does not renumber the original operation terminal or replace its false return");
            Check(((OperationAccepted)decoded[2].Entry).Intent.ProfileInstanceId == Incarnation,
                "recorded operation actor identity survives the actual binary codec");
            foreach (var record in decoded.Where(record => record.SchemaRevision == 3))
                Reject(() => JournalFrameCodec.Encode(record, out _), "default encoder refuses every record3 kind instead of silently downgrading it");
            Check(!JournalFactsFold.Fold(decoded).AllRecordsValid, "the legacy reducer cannot declare a mixed prefix complete");
            ActorAndVersionBoundaries();
            FrameBounds();
            MaximumStatedIntent();
            await RealFileAndLegacyRefusalAsync(records);
            return checks;
        }

        private static IReadOnlyList<JournalRecord> Records()
        {
            var source = Ready(); var goal = Goal(); var nextSource = Ready(2); var nextGoal = Goal("Retain the station and revise only the unbuilt area");
            var target = new LoadTarget("probe", "mixed-slot", "v1");
            var intent = new OperationIntent("mixed-load", source.InstanceId, new SubmissionKey("mixed-write", "load"),
                OperationCommandHash.Compute(source, target), "planner", 1, 2, source, target, Incarnation);
            var menu = new WorldContextSnapshot("other-instance", 9, "menu", null);
            return new[] {
                new JournalRecord(1, EnvironmentId, new StoreOpened("mixed-store")),
                new JournalRecord(2, EnvironmentId, new TaskCreated("task-one", Decision("create", source, TaskCommandHash.Create(source, goal)), goal), 3),
                new JournalRecord(3, EnvironmentId, new OperationAccepted(intent), 3),
                new JournalRecord(4, EnvironmentId, new CommitIntended(intent.OperationId, target, new NativeHostIdentity(1, 1)), 3),
                new JournalRecord(5, EnvironmentId, new TaskGoalRevised("task-one", 1, 2, 1,
                    Decision("revise", nextSource, TaskCommandHash.Revise("task-one", 1, 1, nextSource, nextGoal)), nextGoal), 3),
                new JournalRecord(6, EnvironmentId, new NativeTerminated(intent.OperationId, NativeLoadTaskState.RanToCompletion, false, null), 3),
                new JournalRecord(7, EnvironmentId, new OperationFinished(intent.OperationId, "failed", true, SubmissionKnowledge.Submitted, "NATIVE_FALSE",
                    new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null)), 3),
                new JournalRecord(8, EnvironmentId, new TaskStopped("task-one", 1, 2,
                    Decision("stop", menu, TaskCommandHash.Stop("task-one", 1))), 3),
            };
        }

        private static void ActorAndVersionBoundaries()
        {
            var source = Ready(); var target = new LoadTarget("probe", "legacy-slot", null);
            var intent = new OperationIntent("legacy-op", source.InstanceId, new SubmissionKey("legacy", "key"),
                OperationCommandHash.Compute(source, target), "planner", 1, 2, source, target);
            var legacy = new JournalRecord(2, EnvironmentId, new OperationAccepted(intent), 3);
            var frame = JournalFrameCodec.EncodeMixed(legacy, out int length);
            try
            {
                using (var input = new MemoryStream(frame, 0, length, false))
                {
                    var decoded = JournalFrameCodec.ReadMixed(input)!;
                    Check(decoded.SchemaRevision == 3 && ((OperationAccepted)decoded.Entry).Intent.ProfileInstanceId == null,
                        "legacyUnrecorded remains explicit unknown actor evidence inside a record3 value");
                    Reject(() => JournalFrameCodec.Encode(decoded, out _), "unknown actor evidence does not erase the record3 marker");
                }
                using (var input = new MemoryStream(frame, 0, length, false))
                    Reject(() => JournalFrameCodec.Read(input), "default binary reader refuses record3 before treating it as legacy data");
            }
            finally { Array.Clear(frame, 0, frame.Length); }
            var taggedAsTwo = new JournalRecord(2, EnvironmentId, new OperationAccepted(new OperationIntent(intent.OperationId,
                intent.OriginInstanceId, intent.Key, intent.CommandHash, intent.ProfileId, 1, 2, source, target, Incarnation)));
            Reject(() => JournalFrameCodec.Encode(taggedAsTwo, out _), "record2 cannot silently discard recorded actor evidence");
        }

        private static void FrameBounds()
        {
            var oversizedHeader = new byte[12]; Encoding.ASCII.GetBytes("SLJO").CopyTo(oversizedHeader, 0); oversizedHeader[4] = 3;
            uint size = JournalFrameCodec.MaximumMixedMetadataPayload + 1;
            for (int i = 0; i < 4; i++) oversizedHeader[8 + i] = (byte)(size >> (8 * i));
            using (var input = new MemoryStream(oversizedHeader))
            { Reject(() => JournalFrameCodec.ReadMixed(input), "mixed length is bounded before reading a declared payload"); Check(input.Position == 12, "oversized frame does not attempt to read its body"); }
            var padded = RawFrame(Encoding.UTF8.GetBytes("{\"sequence\":1,\"environmentId\":\"mixed-frame-probe\",\"entry\":{\"kind\":\"header\",\"storeId\":\"s\"}}" + new string(' ', 5000)));
            using (var input = new MemoryStream(padded)) Reject(() => JournalFrameCodec.ReadMixed(input), "non-metadata record3 retains the smaller operation budget");
            var record = Records()[1]; var frame = JournalFrameCodec.EncodeMixed(record, out int length);
            try
            {
                using (var truncated = new MemoryStream(frame, 0, length - 1, false)) Reject(() => JournalFrameCodec.ReadMixed(truncated), "partial metadata digest is not a complete decision");
                frame[12] ^= 1;
                using (var corrupt = new MemoryStream(frame, 0, length, false)) Reject(() => JournalFrameCodec.ReadMixed(corrupt), "metadata digest mismatch cannot produce a goal");
            }
            finally { Array.Clear(frame, 0, frame.Length); }
        }

        private static void MaximumStatedIntent()
        {
            var text = new string('\u0001', 256);
            var goal = new TaskStatedGoal(new string('\u0001', 128), new string('\u0001', 2048),
                Enumerable.Repeat(text, 16).ToArray(), Enumerable.Repeat(text, 16).ToArray());
            var source = new WorldContextSnapshot(new string('\u0001', 128), long.MaxValue, "ready", new string('\u0001', 128));
            string taskId = new string('\u0001', 128); long maximum = AuthorizationState.MaximumCounter;
            var key = new SubmissionKey(new string('\u0001', 128), new string('\u0001', 128));
            var actor = new TaskActor(new string('a', 64), Incarnation, maximum, maximum);
            var entries = new JournalEntry[] {
                new TaskCreated(taskId, new TaskDecision(key, actor, source, TaskCommandHash.Create(source, goal)), goal),
                new TaskGoalRevised(taskId, maximum - 1, maximum, maximum,
                    new TaskDecision(key, actor, source, TaskCommandHash.Revise(taskId, maximum - 1, maximum, source, goal)), goal),
                new TaskStopped(taskId, maximum - 1, maximum, new TaskDecision(key, actor, source, TaskCommandHash.Stop(taskId, maximum - 1))),
            };
            foreach (var entry in entries)
            {
                var record = new JournalRecord(OperationLimits.MaximumFrames, new string('e', 64), entry, 3);
                var frame = JournalFrameCodec.EncodeMixed(record, out int length);
                try
                {
                    MaximumMetadataPayloadObserved = Math.Max(MaximumMetadataPayloadObserved, length - 44);
                    Check(length - 44 <= JournalFrameCodec.MaximumMixedMetadataPayload && (entry is TaskStopped || length - 44 > 65536),
                        "maximum escaped metadata of each kind fits the explicit budget; full intents exceed the legacy/MCP message limit");
                    using (var input = new MemoryStream(frame, 0, length, false))
                    {
                        var decoded = JournalFrameCodec.ReadMixed(input)!.Entry;
                        Check(decoded.GetType() == entry.GetType(), "maximum metadata retains its exact event kind");
                        var decodedGoal = decoded is TaskCreated created ? created.Goal : decoded is TaskGoalRevised revised ? revised.Goal : null;
                        Check(decodedGoal == null || decodedGoal.RequestText == goal.RequestText && decodedGoal.AcceptanceText.SequenceEqual(goal.AcceptanceText),
                            "large metadata preserves complete intent instead of truncating to the old frame limit");
                        if (decoded is TaskStopped stopped) Check(stopped.ControlVersion == maximum && stopped.Decision.Source.Generation == long.MaxValue,
                            "maximum stop control and audit generation round-trip without lossy number conversion");
                    }
                }
                finally { Array.Clear(frame, 0, frame.Length); }
            }
        }

        private static byte[] RawFrame(byte[] payload)
        {
            var frame = new byte[payload.Length + 44]; Encoding.ASCII.GetBytes("SLJO").CopyTo(frame, 0); frame[4] = 3;
            for (int i = 0; i < 4; i++) frame[8 + i] = (byte)((uint)payload.Length >> (8 * i));
            Buffer.BlockCopy(payload, 0, frame, 12, payload.Length);
            using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(frame, 0, payload.Length + 12), 0, frame, payload.Length + 12, 32);
            return frame;
        }

        private static async Task RealFileAndLegacyRefusalAsync(IReadOnlyList<JournalRecord> records)
        {
            var root = Path.Combine(Path.GetFullPath("artifacts"), "mixed-frame-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); string connection = Path.Combine(root, "connection");
            try
            {
                var boundary = new PrivateConnectionStorage(connection);
                using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!)
                {
                    string path = Path.Combine(connection, "operations.journal");
                    using (var file = boundary.CreatePrivateFile(path))
                    {
                        foreach (var record in records)
                        {
                            var frame = JournalFrameCodec.EncodeMixed(record, out int length);
                            try { file.Write(frame, 0, length); } finally { Array.Clear(frame, 0, frame.Length); }
                        }
                        file.Flush(true);
                    }
                    var before = File.ReadAllBytes(path); var recovered = new List<JournalRecord>();
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { JournalRecord? value; while ((value = JournalFrameCodec.ReadMixed(file)) != null) recovered.Add(value); }
                    Check(JournalFactsFold.FoldMixed(recovered).AllRecordsValid && recovered.Count == 8,
                        "explicit mixed codec and reducer re-read real flushed file bytes without invoking a game");
                    var old = new OperationJournal(boundary, lease, EnvironmentId, false);
                    try { Check(old.Recovery.Incompatible && !old.IsHealthy, "current journal composition refuses the unsupported mixed file rather than activating partial Task support"); }
                    finally { await old.StopAsync(); }
                    Check(File.ReadAllBytes(path).SequenceEqual(before), "unsupported mixed file remains byte-for-byte intact after the current reader stops");
                }
            }
            finally
            {
                foreach (string name in new[] { "operations.journal", "authority.lock" })
                { string path = Path.Combine(connection, name); if (File.Exists(path)) File.Delete(path); }
                if (Directory.Exists(connection)) Directory.Delete(connection);
                Directory.Delete(root);
            }
        }
    }
}
