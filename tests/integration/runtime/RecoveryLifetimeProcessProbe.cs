using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class RecoveryLifetimeProcessProbe
    {
        // 只用真实 journal 接口制造进程中断前缀；不调用 Native，也不手工伪造恢复字节。
        internal static async Task<string[]> SeedAsync(string directory, string token, int acceptedCount, bool commitFirst)
        {
            if (acceptedCount < 1 || acceptedCount > 2) throw new ArgumentOutOfRangeException(nameof(acceptedCount));
            var boundary = new PrivateConnectionStorage(directory);
            using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Seed lease unavailable."))
            {
                var store = new AtomicAuthorizationStore(boundary, lease, "probe"); store.InitializeEmpty();
                var activated = store.Load().Activate(); store.Save(1, activated);
                var grants = new GrantAuthority(activated, store);
                OperationJournal? journal = null;
                try
                {
                    await grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, token, null);
                    journal = new OperationJournal(boundary, lease, "probe", true);
                    var operations = new OperationAuthority(journal, grants, new WorldContextTracker(Guid.NewGuid().ToString("N")), new ProcessNativeHostIdentity());
                    var subject = grants.Read("probe") ?? throw new InvalidOperationException();
                    string context = operations.IssueWriteContext(subject);
                    var ids = new string[acceptedCount];
                    for (int i = 0; i < ids.Length; i++)
                    {
                        var accepted = await operations.SubmitAsync(new SubmissionKey(context, "recovery-" + i), new LoadTarget("probe", "synthetic-save", "content-1"), subject);
                        if (accepted.Kind != "found" || accepted.Durability != "durable" || accepted.OperationId == null)
                            throw new InvalidOperationException("Seed acceptance failed.");
                        ids[i] = accepted.OperationId;
                    }
                    if (commitFirst) await operations.PersistCommitIntentAsync(ids[0]);
                    return ids;
                }
                finally
                {
                    // 有意只结束真实 writer：Ops.Close 会追加 final，无法形成缺 final 的恢复输入。
                    try { if (journal != null) await journal.StopAsync(); }
                    finally { await grants.StopAsync(); }
                }
            }
        }

        internal static async Task RunAsync()
        {
            Program.Stage = "recovery early failure child";
            string? line = Console.ReadLine();
            if (line == null || line.Length > 4096) throw new InvalidOperationException("Probe configuration missing.");
            var config = (JObject)ContractJson.Parse(line);
            string directory = (string)config["directory"]!;
            string token = (string)config["token"]!;
            string[] ids = await SeedAsync(directory, token, 1, true);
            bool sawFence = false;
            var lifecycle = new GameConnectionLifecycle(() => ConnectionAuthorityOwner.CreateAsync(directory, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, _, __) =>
                {
                    sawFence = operations.HasNativeFence;
                    throw new IOException("Synthetic dispatcher construction failure.");
                }));
            Program.Assert(!await lifecycle.StartAsync() && sawFence && lifecycle.Owner == null,
                "current-host commit is recovered before dispatcher failure without a complete owner");
            var failed = lifecycle.Read();
            Program.Assert(failed.Phase == GameConnectionPhase.Failed && failed.FailureCode == "INITIALIZATION_FAILED" &&
                failed.ExceptionType == "IOException" && !failed.FullyReleased && failed.NativeLeaseRetained,
                "initialization failure exposes retained lease before explicit close");
            Task<bool> first = lifecycle.CloseAsync();
            Program.Assert(!await first, "early failure close cannot claim native obligation fully released");
            Task<bool> second = lifecycle.CloseAsync();
            Program.Assert(!await second && ReferenceEquals(first, second), "early failure repeated close observes same ownership result");
            var snapshot = lifecycle.Read();
            var boundary = new PrivateConnectionStorage(directory);
            using (var contested = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority())
            {
                Program.Assert(contested == null && !snapshot.FullyReleased && snapshot.NativeLeaseRetained &&
                    !File.Exists(Path.Combine(directory, "discovery.json")), "early failure retains actual lease and publishes no discovery");
                Program.Emit(new JObject { ["kind"] = "recovery-failure-closed", ["operationId"] = ids[0],
                    ["fullyReleased"] = snapshot.FullyReleased, ["nativeLeaseRetained"] = snapshot.NativeLeaseRetained,
                    ["exceptionType"] = snapshot.ExceptionType, ["leaseBusy"] = contested == null,
                    ["sameCloseTask"] = ReferenceEquals(first, second), ["discoveryPresent"] = File.Exists(Path.Combine(directory, "discovery.json")) });
            }
            // 正常退出由 OS 释放仅保留的 lease；无游戏、实际 Native 调用、Shell 或提权。
        }
    }
}
