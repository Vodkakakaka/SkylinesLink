using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class DurableOperationProcessProbe
    {
        internal static async Task RunAsync()
        {
            Program.Stage = "unknown-native-close-child";
            string? line = Console.ReadLine();
            if (line == null || line.Length > 4096) throw new InvalidOperationException("Probe configuration missing.");
            var config = (JObject)ContractJson.Parse(line);
            string directory = (string)config["directory"]!;
            string token = (string)config["token"]!;
            var lifecycle = new GameConnectionLifecycle(async () =>
            {
                var created = await ConnectionAuthorityOwner.CreateAsync(directory, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                    new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations), true);
                if (created == null) return null;
                try
                {
                    var authorization = created.Authority.State;
                    if (authorization.RecordRevision == 2 && authorization.AuthorityEpoch == 1 && authorization.Profiles.Count == 0)
                        await created.Authority.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, token, null);
                    return created;
                }
                catch (Exception error) { await Program.CloseFailedRootAsync(created, error); throw; }
            });
            if (!await lifecycle.StartAsync()) throw new InvalidOperationException("Probe activation failed.");
            var owner = lifecycle.Owner;
            if (owner == null) throw new InvalidOperationException("Probe lease unavailable.");
            var subject = owner.Authority.Read("probe")!;
            var key = new SubmissionKey(owner.Operations.IssueWriteContext(subject), "lost-native-handle");
            string id = (await owner.Operations.SubmitAsync(key, new LoadTarget("probe", "save-b", "content-1"), subject)).OperationId!;
            await owner.Operations.PersistCommitIntentAsync(id);
            owner.Operations.TryCommitAtSafePoint(id, new NativeLoadGate(owner.Context.Read().InstanceId, _ => throw new IOException("Synthetic lost receipt.")));
            await owner.Operations.DrainAsync(id);
            lifecycle.RequestStop();
            Task<bool> firstClose = lifecycle.CloseAsync(); await firstClose;
            Task<bool> secondClose = lifecycle.CloseAsync(); await secondClose;
            Program.Assert(!await firstClose && lifecycle.Read().Phase == GameConnectionPhase.Stopped && lifecycle.Read().NativeLeaseRetained,
                "unknown native lifecycle retains lease after IO shutdown");
            var replacement = await ConnectionAuthorityOwner.OpenAsync(directory, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations));
            Program.Assert(replacement == null, "same process cannot reacquire unknown native environment");
            Program.Emit(new JObject { ["kind"] = "unknown-closed", ["operationId"] = id,
                ["fullyReleased"] = owner.FullyReleased, ["nativeLeaseRetained"] = owner.NativeLeaseRetained,
                ["sameCloseTask"] = ReferenceEquals(firstClose, secondClose), ["connected"] = owner.Server.ConnectedCount });
            // 此隔离子进程退出由 OS 释放 retained lease；没有 Shell、提权或游戏实例。
        }
    }
}
