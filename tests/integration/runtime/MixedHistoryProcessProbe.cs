using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using Contracts = SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>Finite synthetic facts through the actual private writer and publisher; no game or Control admission.</summary>
    internal static class MixedHistoryProcessProbe
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static async Task<T> Done<T>(Task<T> task) { await Program.MustComplete(task); return await task; }
        internal static async Task<int> RunAsync()
        {
            Program.Stage = "mixed-history-process-config";
            var bytes = new byte[4097]; int count = 0;
            using (var input = Console.OpenStandardInput())
            {
                while (true)
                {
                    int read = await input.ReadAsync(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                    if (count > 4096) throw new InvalidDataException("Mixed history configuration exceeds its byte limit.");
                }
            }
            var config = (JObject)Contracts.ContractJson.Parse(Utf8.GetString(bytes, 0, count)); Array.Clear(bytes, 0, bytes.Length);
            string environment = (string?)config["environmentId"] ?? throw new InvalidDataException("Missing fixture environment.");
            string profile = (string?)config["profileId"] ?? throw new InvalidDataException("Missing fixture profile.");
            string token = (string?)config["internalToken"] ?? throw new InvalidDataException("Missing fixture credential.");
            bool large = (bool?)config["large"] ?? false;
            int? taskCount = (int?)config["taskCount"];
            if (taskCount.HasValue && taskCount != 0 && taskCount != 8 || taskCount.HasValue && large)
                throw new InvalidDataException("Discovery fixture requires zero or eight tasks and no large revision mode.");
            if (!Regex.IsMatch(environment, "^probe-[a-f0-9]{24}$")) throw new InvalidDataException("Unowned fixture environment.");
            AuthorizationState.ValidateIdentifier(profile); AuthorizationState.ValidateHex(token);
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skylineslink", "environments");
            string root = Path.Combine(parent, environment);
            if (!Directory.Exists(parent) || Directory.Exists(root)) throw new InvalidOperationException("Fixture requires a fresh environment under the existing user root.");
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".owned-probe"), environment, Utf8);
            File.WriteAllText(Path.Combine(root, "activation.json"), new JObject { ["productId"] = "skylineslink", ["version"] = "0.0.0-fixture",
                ["coreManifestSha256"] = new string('c', 64) }.ToString(Formatting.None), Utf8);
            var boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
            JObject ready;
            Program.Stage = "mixed-history-process-owned-io";
            using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Fixture lease unavailable."))
            {
                var authorization = new AtomicAuthorizationStore(boundary, lease, environment); authorization.InitializeEmpty();
                var activated = authorization.Load().Activate(); authorization.Save(1, activated);
                var grants = new GrantAuthority(activated, authorization);
                await grants.UpdateAsync(profile, GrantScope.Read, token, null);
                var subject = grants.Read(profile) ?? throw new InvalidOperationException("Fixture subject unavailable.");
                var actor = new TaskActor(profile, subject.ProfileInstanceId, subject.AuthorityEpoch, subject.Epoch);
                var source = new WorldContextSnapshot("synthetic-task-source", 7, "ready", "synthetic-city");
                var revisionSource = new WorldContextSnapshot("synthetic-new-source", 10, "ready", source.CityId);
                var stopSource = new WorldContextSnapshot("synthetic-later-ready", 11, "ready", null);
                var operationSource = new WorldContextSnapshot(source.InstanceId, source.Generation, "ready", null);
                var observation = new WorldContextSnapshot(source.InstanceId, source.Generation + 1, "ready", null);
                var goal1 = large
                    ? new TaskStatedGoal(new string('\u0085', 128), new string('\u0085', 2048),
                        Enumerable.Repeat(new string('\u0085', 256), 16).ToArray(), Enumerable.Repeat(new string('\u0085', 256), 16).ToArray())
                    : new TaskStatedGoal("Fixture goal 1", string.Concat(Enumerable.Repeat("😀", 1100)),
                        Enumerable.Range(0, 6).Select(i => i + ":" + string.Concat(Enumerable.Repeat("😀", 254))).ToArray(), new[] { "Original acceptance" });
                var goal2 = large ? goal1 : new TaskStatedGoal("Fixture goal 2", "第二版:" + string.Concat(Enumerable.Repeat("🚆", 1096)),
                    new[] { "Revised constraint" }, new[] { "Revised acceptance" });
                const string taskId = "synthetic-task-😀", operationId = "synthetic-false-operation";
                var createKey = new SubmissionKey("fixture-task-context", "create");
                var reviseKey = new SubmissionKey("fixture-task-context", "revise");
                var stopKey = new SubmissionKey("fixture-task-context", "stop");
                var operationKey = new SubmissionKey("fixture-operation-context", "known-false");
                var journal = OperationJournal.OpenMixedForAuthority(boundary, lease, environment, true);
                HistoryPublisher? publisher = null;
                try
                {
                    if (taskCount.HasValue) await PopulateDiscoveryAsync(journal, actor, taskCount.Value);
                    else
                    {
                        using (var ticket = journal.TryReserveTaskCreation(taskId) ?? throw new InvalidOperationException("Fixture Task credit unavailable."))
                            await Done(ticket.AppendAsync(new TaskCreated(taskId, new TaskDecision(createKey, actor, source, TaskCommandHash.Create(source, goal1)), goal1)));
                        var target = new LoadTarget("synthetic", "fixture-save", "v1");
                        var intent = new OperationIntent(operationId, operationSource.InstanceId, operationKey, OperationCommandHash.Compute(operationSource, target),
                            profile, subject.AuthorityEpoch, subject.Epoch, operationSource, target, subject.ProfileInstanceId);
                        using (var ticket = journal.TryReserveOperation() ?? throw new InvalidOperationException("Fixture operation credit unavailable."))
                        {
                            await Done(ticket.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal));
                            await Done(ticket.AppendAsync(new CommitIntended(operationId, target, new NativeHostIdentity(101, 123456)), JournalSlot.Normal));
                            await Done(ticket.AppendAsync(new NativeTerminated(operationId, NativeLoadTaskState.RanToCompletion, false, null), JournalSlot.NativeTerminal));
                            await Done(ticket.AppendAsync(new OperationFinished(operationId, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE",
                                new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, observation)), JournalSlot.Terminal));
                        }
                        for (int previous = 1; previous <= (large ? 16 : 1); previous++)
                        {
                            if (large) reviseKey = new SubmissionKey("fixture-task-context", "revise-" + previous);
                            using (var ticket = journal.TryReserveTaskRevision(taskId) ?? throw new InvalidOperationException("Fixture revision credit unavailable."))
                                await Done(ticket.AppendAsync(new TaskGoalRevised(taskId, previous, previous + 1, 1, new TaskDecision(reviseKey, actor, revisionSource,
                                    TaskCommandHash.Revise(taskId, previous, 1, revisionSource, goal2)), goal2)));
                        }
                        using (var ticket = journal.TryReserveTaskStop(taskId) ?? throw new InvalidOperationException("Fixture Stop credit unavailable."))
                            await Done(ticket.AppendAsync(new TaskStopped(taskId, 1, 2, new TaskDecision(stopKey, actor, stopSource, TaskCommandHash.Stop(taskId, 1)))));
                    }
                    publisher = new HistoryPublisher(journal, new AtomicHistoryStore(boundary, lease, environment), environment,
                        taskCount == 8 ? RepeatScalar(0x1f600, 64) : "0.1.0-fixture",
                        taskCount == 8 ? RepeatScalar(0x1f600, 128) : "synthetic-checkpoint-producer", new string('c', 64), () => 123);
                    if (!await Done(publisher.InitialAttempt)) throw new InvalidOperationException("Fixture checkpoint publication failed.");
                    await Program.MustComplete(journal.StopAsync()); await Program.MustComplete(publisher.StopAsync()); await Program.MustComplete(grants.StopAsync());
                    var current = publisher.Current ?? throw new InvalidOperationException("Fixture confirmed history missing.");
                    ready = new JObject { ["kind"] = "mixed-history-ready", ["environmentId"] = environment, ["profileId"] = profile,
                        ["profileInstanceId"] = subject.ProfileInstanceId, ["authorityEpoch"] = subject.AuthorityEpoch, ["grantEpoch"] = subject.Epoch,
                        ["taskId"] = taskId, ["operationId"] = operationId, ["createKey"] = Key(createKey), ["reviseKey"] = Key(reviseKey),
                        ["stopKey"] = Key(stopKey), ["operationKey"] = Key(operationKey),
                        ["source"] = Contracts.ContractCodec.EncodeOperationHistoryCheckpointV2(current.ToContractV2())["source"]!.DeepClone(),
                        ["fixture"] = "synthetic-known-facts-no-game" };
                    if (large) ready["large"] = true;
                    if (taskCount.HasValue) ready["taskCount"] = taskCount.Value;
                }
                finally
                {
                    await Program.MustComplete(journal.StopAsync());
                    if (publisher != null) await Program.MustComplete(publisher.StopAsync());
                    await Program.MustComplete(grants.StopAsync());
                }
            }
            // The ready receipt comes only after actual journal/publisher IO and the real lease have ended.
            string output = ready.ToString(Formatting.None);
            if (Utf8.GetByteCount(output) > 4096 || output.Contains(token)) throw new InvalidOperationException("Fixture receipt exceeds its disclosure bound.");
            Console.WriteLine(output); return 0;
        }

        private static string RepeatScalar(int scalar, int count) => string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(scalar), count));

        private static async Task PopulateDiscoveryAsync(OperationJournal journal, TaskActor actor, int count)
        {
            var source = new WorldContextSnapshot(RepeatScalar(0x1f600, 128), 7, "ready", RepeatScalar(0x1f687, 128));
            var stopSource = new WorldContextSnapshot("synthetic-discovery-menu", 8, "menu", null);
            var original = new TaskStatedGoal(RepeatScalar(0x1f600, 128), "Original retained goal", Array.Empty<string>(), Array.Empty<string>());
            var revised = new TaskStatedGoal(RepeatScalar(0x1f687, 128), "Revised retained goal", Array.Empty<string>(), Array.Empty<string>());
            // Insert backwards; every summary identity, city and title reaches
            // its full supplementary-plane scalar bound in the native writer.
            for (int index = count - 1; index >= 0; index--)
            {
                string id = RepeatScalar(0x10000 + index, 128);
                var createKey = new SubmissionKey("fixture-discovery-context", "create-" + index);
                using (var ticket = journal.TryReserveTaskCreation(id) ?? throw new InvalidOperationException("Discovery Task credit unavailable."))
                    await Done(ticket.AppendAsync(new TaskCreated(id, new TaskDecision(createKey, actor, source,
                        TaskCommandHash.Create(source, original)), original)));
                if (index % 2 == 0)
                {
                    var reviseKey = new SubmissionKey("fixture-discovery-context", "revise-" + index);
                    using (var ticket = journal.TryReserveTaskRevision(id) ?? throw new InvalidOperationException("Discovery revision credit unavailable."))
                        await Done(ticket.AppendAsync(new TaskGoalRevised(id, 1, 2, 1, new TaskDecision(reviseKey, actor, source,
                            TaskCommandHash.Revise(id, 1, 1, source, revised)), revised)));
                    // Deliberately retain an old, noncanonical key.
                    var stopKey = new SubmissionKey("fixture-discovery-context", "historical-stop-" + index);
                    using (var ticket = journal.TryReserveTaskStop(id) ?? throw new InvalidOperationException("Discovery Stop credit unavailable."))
                        await Done(ticket.AppendAsync(new TaskStopped(id, 1, 2, new TaskDecision(stopKey, actor, stopSource,
                            TaskCommandHash.Stop(id, 1)))));
                }
            }
        }
        private static JObject Key(SubmissionKey key) => new JObject { ["writeContextId"] = key.WriteContextId, ["requestId"] = key.RequestId };
    }
}
