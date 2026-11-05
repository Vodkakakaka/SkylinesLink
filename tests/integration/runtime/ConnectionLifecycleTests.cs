using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    internal static class ConnectionLifecycleTests
    {
        private static int checks;
        private static TaskCompletionSource<bool> Completion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static void Check(bool value, string label) { Program.Assert(value, label); checks++; }
        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await SharedContextLifetime();
            await ContextAdmissionAndBusyRetry();
            await ClosedContextCannotRestartIssuer();
            await FailedConstructionStopsContext();
            await BeforeAndDuringCreate();
            await LatePublication();
            await FailureAndRepeat();
            await NativeClose();
            await RecoveryFlushClose();
            await RecoveryConstructionFailure();
            return checks;
        }

        private static async Task SharedContextLifetime()
        {
            Program.Stage = "connection caller owned context lifetime";
            using (var fixture = new Fixture())
            {
                const string productVersion = "9.8.7-probe";
                var context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                string instance = context.Read().InstanceId;
                long generation = context.EnterLoading();
                Check(context.ObserveReady(generation), "native ready may precede connection construction");
                var ready = context.Read();
                WorldContextTracker? dispatchedContext = null;
                OperationAuthority? dispatchedOperations = null;
                SnapshotCapabilityDispatcher? dispatcher = null;
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(context: context, productVersion: productVersion, dispatcher: (operations, tracker, version) =>
                {
                    dispatchedContext = tracker; dispatchedOperations = operations;
                    return dispatcher = new SnapshotCapabilityDispatcher(tracker, version, operations);
                }));
                try
                {
                    Check(await lifecycle.StartAsync(), "shared context connection activates");
                    var owner = lifecycle.Owner ?? throw new InvalidOperationException("Shared context owner missing.");
                    Check(ReferenceEquals(context, owner.Context) && ReferenceEquals(context, dispatchedContext) &&
                        ReferenceEquals(owner.Operations, dispatchedOperations) && owner.Discovery.InstanceId == instance,
                        "factory owner dispatcher and discovery share the caller context and instance");
                    Check(ReferenceEquals(ready, owner.Context.Read()), "connection construction preserves the earlier native ready observation");
                    var value = dispatcher!.Execute("system.context", "2", new JObject(), owner.Authority.Read("probe")!);
                    var world = value["world"] as JObject;
                    Check((string?)value["instanceId"] == instance && (string?)value["gameState"] == "ready" && world != null &&
                        (string?)world["generation"] == generation.ToString(System.Globalization.CultureInfo.InvariantCulture) && world["cityId"]!.Type == JTokenType.Null,
                        "the real dispatcher exposes the pre-construction loaded scope without inventing city identity");
                    var publishedDiscovery = ContractCodec.DecodeEnvironmentDiscovery(JObject.Parse(File.ReadAllText(fixture.Discovery)));
                    Check((string?)value["productVersion"] == productVersion && owner.Discovery.ProductVersion == productVersion &&
                        publishedDiscovery.ProductVersion == productVersion,
                        "the factory version reaches Context:2 and the actually published discovery without a fixed fixture-version fallback");
                    Check(await owner.History.InitialAttempt, "the version probe awaits a real successful history checkpoint publication");
                    byte[] historyBytes = File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, "operations.history"));
                    var publishedHistory = HistoryFrameCodec.Decode(historyBytes, historyBytes.Length);
                    Check(publishedHistory.Source.ProducerVersion == productVersion && publishedHistory.Source.ProducerInstanceId == instance,
                        "the real HistoryPublisher writes the same root version and instance into the decoded on-disk source");
                    long late = context.EnterLoading();
                    lifecycle.RequestStop(); var terminal = context.Read();
                    Check(context.IsStopped && !context.ObserveReady(late) && !context.ObserveCityIdentity(late, "late-city"),
                        "owner stop terminally closes the same native observation context");
                    context.Stop(); context.MarkUnavailable();
                    Check(ReferenceEquals(terminal, context.Read()) && await lifecycle.CloseAsync() && owner.FullyReleased,
                        "late observations and repeated stop cannot revive the scope while actual close completes");
                }
                finally { await Program.MustComplete(lifecycle.CloseAsync()); }
            }
        }

        private static async Task ContextAdmissionAndBusyRetry()
        {
            Program.Stage = "connection context admission before storage";
            var stopped = new WorldContextTracker(Guid.NewGuid().ToString("N")); stopped.Stop();
            foreach (var context in new WorldContextTracker?[] { null, new WorldContextTracker("not-a-guid"),
                new WorldContextTracker(new string('A', 32)), new WorldContextTracker(new string('0', 32)), stopped })
            using (var fixture = new Fixture())
            {
                Exception? failure = null; ConnectionAuthorityOwner? owner = null;
                try
                {
                    owner = await ConnectionAuthorityOwner.CreateAsync(fixture.DirectoryPath, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                        context!, (operations, tracker, version) => new SnapshotCapabilityDispatcher(tracker, version, operations), true);
                }
                catch (Exception error) { failure = error; }
                finally { if (owner != null) await Program.MustComplete(owner.CloseAsync()); }
                Type expected = context == null ? typeof(ArgumentNullException) : context.IsStopped ? typeof(InvalidOperationException) : typeof(ArgumentException);
                Check(failure?.GetType() == expected && !Directory.Exists(fixture.DirectoryPath),
                    "invalid or stopped context is rejected before private directory or authorization creation");
            }
            foreach (string? productVersion in new string?[] { null, "9.8.7/probe" })
            using (var fixture = new Fixture())
            {
                Exception? failure = null; ConnectionAuthorityOwner? owner = null;
                try { owner = await fixture.Create(productVersion: productVersion!); }
                catch (Exception error) { failure = error; }
                finally { if (owner != null) await Program.MustComplete(owner.CloseAsync()); }
                Check(failure is ArgumentException && !Directory.Exists(fixture.DirectoryPath),
                    "an invalid root product version is rejected before any private storage is created");
            }
            using (var fixture = new Fixture())
            {
                var seed = await fixture.Create() ?? throw new InvalidOperationException("Busy fixture seed owner missing.");
                await Program.MustComplete(seed.CloseAsync());
                var context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                long generation = context.EnterLoading(); context.ObserveReady(generation);
                var before = context.Read();
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(context: context, leaseWaitMilliseconds: 25));
                try
                {
                    using (var held = new EnvironmentLeaseProvider(new PrivateConnectionStorage(fixture.DirectoryPath)).TryAcquireAuthority()
                        ?? throw new InvalidOperationException("Busy fixture lease missing."))
                    {
                        Check(!await lifecycle.StartAsync() && lifecycle.Read().FailureCode == "ENVIRONMENT_BUSY" &&
                            !lifecycle.StopRequested && !context.IsStopped && ReferenceEquals(before, context.Read()),
                            "real lease contention leaves caller context live and unchanged for retry");
                    }
                    Check(await lifecycle.StartAsync() && ReferenceEquals(context, lifecycle.Owner!.Context) &&
                        context.AllowsWorld(before.InstanceId, generation), "the same lifecycle retries with the same caller loaded scope after busy clears");
                }
                finally { await Program.MustComplete(lifecycle.CloseAsync()); }
            }
        }

        private static async Task ClosedContextCannotRestartIssuer()
        {
            Program.Stage = "closed context cannot restart write context issuer";
            using (var fixture = new Fixture())
            {
                var firstContext = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                var first = await fixture.Create(scope: GrantScope.Read | GrantScope.Control, context: firstContext)
                    ?? throw new InvalidOperationException("First issuer owner missing.");
                string firstWrite;
                try
                {
                    await first.ActivateAsync();
                    firstWrite = first.Operations.IssueWriteContext(first.Authority.Read("probe")!);
                    Check(first.IsActive && firstWrite == firstContext.Read().InstanceId + ".w1", "first owner issues its first write context under the actual instance");
                }
                finally { await Program.MustComplete(first.CloseAsync()); }
                string authorization = Path.Combine(fixture.DirectoryPath, "authorization.json");
                byte[] previousAuthorization = File.ReadAllBytes(authorization);
                bool rejected = false; ConnectionAuthorityOwner? reused = null;
                try { reused = await fixture.Create(context: firstContext); }
                catch (InvalidOperationException) { rejected = true; }
                finally { if (reused != null) await Program.MustComplete(reused.CloseAsync()); }
                Check(rejected && firstContext.IsStopped && previousAuthorization.SequenceEqual(File.ReadAllBytes(authorization)),
                    "closed instance cannot recreate its issuer or change durable authorization before rejection");

                var nextContext = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                var next = await fixture.Create(scope: GrantScope.Read | GrantScope.Control, context: nextContext)
                    ?? throw new InvalidOperationException("Replacement issuer owner missing.");
                try
                {
                    await next.ActivateAsync();
                    string nextWrite = next.Operations.IssueWriteContext(next.Authority.Read("probe")!);
                    Check(next.IsActive && nextWrite == nextContext.Read().InstanceId + ".w1" && firstWrite != nextWrite &&
                        first.Discovery.InstanceId != next.Discovery.InstanceId,
                        "restarted authority may reset its local counter but a new root instance prevents complete write context ID collision");
                }
                finally { await Program.MustComplete(next.CloseAsync()); }
            }
        }

        private static async Task FailedConstructionStopsContext()
        {
            Program.Stage = "dispatcher construction failure terminates caller context";
            using (var fixture = new Fixture())
            {
                var context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                long generation = context.EnterLoading();
                WorldContextTracker? dispatchedContext = null;
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(context: context, dispatcher: (_, tracker, __) =>
                {
                    dispatchedContext = tracker;
                    throw new IOException("Synthetic dispatcher construction failure without native work.");
                }));
                try
                {
                    Check(!await lifecycle.StartAsync() && lifecycle.Owner == null && ReferenceEquals(context, dispatchedContext) && context.IsStopped &&
                        lifecycle.Read().FailureCode == "INITIALIZATION_FAILED" && lifecycle.Read().ExceptionType == "IOException",
                        "dispatcher failure before owner return terminally closes the actual caller context");
                    using (var lease = new EnvironmentLeaseProvider(new PrivateConnectionStorage(fixture.DirectoryPath)).TryAcquireAuthority())
                        Check(lease != null && lifecycle.Read().FullyReleased && !lifecycle.Read().NativeLeaseRetained && !File.Exists(fixture.Discovery),
                            "ordinary construction failure joins IO and releases the actual lease without a native fence");
                    string authorization = Path.Combine(fixture.DirectoryPath, "authorization.json");
                    byte[] previousAuthorization = File.ReadAllBytes(authorization);
                    bool rejected = false; ConnectionAuthorityOwner? reused = null;
                    try { reused = await fixture.Create(context: context); }
                    catch (InvalidOperationException) { rejected = true; }
                    finally { if (reused != null) await Program.MustComplete(reused.CloseAsync()); }
                    Check(rejected && !context.ObserveReady(generation) && !context.ObserveCityIdentity(generation, "late-city") &&
                        previousAuthorization.SequenceEqual(File.ReadAllBytes(authorization)),
                        "failed construction context cannot be reused or revived and rejection does not reactivate authorization");
                }
                finally { await Program.MustComplete(lifecycle.CloseAsync()); }
            }
        }

        private static async Task RecoveryFlushClose()
        {
            Program.Stage = "recovery final activation close";
            using (var fixture = new Fixture())
            using (var flush = new RecoveryFlush())
            {
                string[] ids = await RecoveryLifetimeProcessProbe.SeedAsync(fixture.DirectoryPath, fixture.Token, 2, false);
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(durability: flush));
                Task<bool> start = lifecycle.StartAsync();
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    var owner = lifecycle.Owner ?? throw new InvalidOperationException("Recovery owner was not attached.");
                    Check(!start.IsCompleted && !owner.IsActive && owner.Server.Completion.IsCompleted && !File.Exists(fixture.Discovery),
                        "recovery final IO precedes listener and discovery activation");
                    lifecycle.RequestStop(); Task<bool> close = lifecycle.CloseAsync();
                    Check(owner.StopRequested && !close.IsCompleted && !owner.FullyReleased && owner.Server.Completion.IsCompleted,
                        "stop during recovery final waits real writer without opening listener");
                    var boundary = new PrivateConnectionStorage(fixture.DirectoryPath);
                    using (var contested = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority())
                        Check(contested == null, "pending recovery final retains actual exclusive environment lease");
                    flush.Release.Set(); await Program.MustComplete(close);
                    Check(!await start && await close && owner.FullyReleased && !owner.NativeLeaseRetained && !File.Exists(fixture.Discovery),
                        "released recovery flush completes abandoned activation and actual close");
                    Check(flush.Calls == 2 && ReferenceEquals(close, lifecycle.CloseAsync()),
                        "recovery stop admits no second final and reuses one close task");
                    using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Closed recovery lease remained busy."))
                    {
                        var journal = new OperationJournal(boundary, lease, "probe", false);
                        try
                        {
                            var records = journal.Recovery.Records;
                            var finals = records.Select(record => record.Entry).OfType<OperationFinished>().ToArray();
                            Check(journal.Recovery.Complete && journal.Recovery.DurableConfirmed && records.Count == 4 && finals.Length == 1 &&
                                ids.Contains(finals[0].OperationId) && finals[0].State == "cancelled" && !finals[0].MayHaveApplied &&
                                finals[0].Submission == SubmissionKnowledge.NotSubmitted && finals[0].Load.PhysicalTaskState == NativeLoadTaskState.NotStarted,
                                "only the already queued recovery final is durable after stopping");
                            var history = new AtomicHistoryStore(boundary, lease, "probe").Load();
                            Check(history != null && history.Source.ThroughSequence == 4 && history.Records.Count == 1 &&
                                history.Records[0].OperationId == finals[0].OperationId && !history.Records[0].MayHaveApplied,
                                "publisher joins with the actual sealed final and excludes unsealed recovery inference");
                        }
                        finally { await journal.StopAsync(); }
                    }
                }
                finally
                {
                    flush.Release.Set();
                    await Program.MustComplete(lifecycle.CloseAsync());
                }
            }
        }

        private static async Task RecoveryConstructionFailure()
        {
            Program.Stage = "recovery early construction failure process";
            using (var fixture = new Fixture())
            {
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--recovery-lifetime-probe") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.CurrentDirectory,
                    StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
                using (var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery child missing."))
                {
                    Task<string> output = ReadBounded(process.StandardOutput, 4096);
                    Task<string> error = ReadBounded(process.StandardError, 4096);
                    try
                    {
                        process.StandardInput.WriteLine(new JObject { ["directory"] = fixture.DirectoryPath, ["token"] = fixture.Token }.ToString(Newtonsoft.Json.Formatting.None));
                        process.StandardInput.Close();
                        await Program.MustComplete(Task.WhenAll(output, error, Task.Run(() => process.WaitForExit())));
                        Check(process.ExitCode == 0 && (await error).Length == 0, "owned recovery lifetime child exits normally with both output streams at EOF");
                        var value = (JObject)ContractJson.Parse(await output);
                        Check((string?)value["kind"] == "recovery-failure-closed" && !(bool)value["fullyReleased"]! &&
                            (bool)value["nativeLeaseRetained"]! && (string?)value["exceptionType"] == "IOException" &&
                            (bool)value["leaseBusy"]! && (bool)value["sameCloseTask"]! && !(bool)value["discoveryPresent"]!,
                            "early dispatcher failure reports actual retained native lease through owner-null lifecycle");
                        var boundary = new PrivateConnectionStorage(fixture.DirectoryPath);
                        using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority())
                            Check(lease != null, "normal child exit releases only its retained process lifetime lease");
                    }
                    finally
                    {
                        // 仅失败清理自有子进程；被 kill 的执行不会到达成功断言。
                        if (!process.HasExited) { process.Kill(); await Program.MustComplete(Task.Run(() => process.WaitForExit())); }
                        await Program.MustComplete(Task.WhenAll(output, error));
                    }
                }
            }
        }

        private static async Task<string> ReadBounded(StreamReader source, int maximum)
        {
            var result = new StringBuilder(); var chunk = new char[256]; int count;
            while ((count = await source.ReadAsync(chunk, 0, chunk.Length)) != 0)
            {
                if (result.Length + count > maximum) throw new IOException("Probe output budget exceeded.");
                result.Append(chunk, 0, count);
            }
            return result.ToString();
        }

        private sealed class RecoveryFlush : IJournalDurability, IDisposable
        {
            private int calls;
            internal int Calls => Volatile.Read(ref calls);
            internal readonly TaskCompletionSource<bool> Entered = Completion();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public void Flush(FileStream file)
            {
                // 第一次是已有前缀的恢复确认；只暂停第二次真实 final Write 之后的 Flush。
                if (Interlocked.Increment(ref calls) == 2)
                {
                    Entered.TrySetResult(true);
                    if (!Release.Wait(5000)) throw new TimeoutException("Recovery flush fixture deadline.");
                }
                file.Flush(true);
            }
            public void Dispose() => Release.Dispose();
        }
        private static async Task BeforeAndDuringCreate()
        {
            Program.Stage = "connection lifecycle creation and early stop";
            int calls = 0;
            var stopped = new GameConnectionLifecycle(() => { calls++; throw new InvalidOperationException(); });
            stopped.RequestStop();
            Check(await stopped.CloseAsync() && !await stopped.StartAsync() && calls == 0, "stop before start never invokes factory");
            using (var fixture = new Fixture())
            {
                var owner = await fixture.Create();
                Check(owner != null && !owner.IsActive && !File.Exists(fixture.Discovery) && owner.Server.Completion.IsCompleted,
                    "CreateAsync owns private state without listener or discovery");
                await owner!.CloseAsync();
                await RejectActivation(owner);
                Check(owner.FullyReleased && !File.Exists(fixture.Discovery), "unactivated owner closes and cannot activate afterwards");
            }
            using (var fixture = new Fixture())
            using (var release = new ManualResetEventSlim())
            {
                var entered = Completion();
                var lifecycle = new GameConnectionLifecycle(() =>
                {
                    calls++; entered.SetResult(true);
                    if (!release.Wait(5000)) throw new TimeoutException();
                    return fixture.Create();
                });
                Task<bool> start = lifecycle.StartAsync();
                Check(ReferenceEquals(start, lifecycle.StartAsync()), "repeated start has one factory task");
                await Program.MustComplete(entered.Task);
                lifecycle.RequestStop(); Task<bool> close = lifecycle.CloseAsync();
                Check(!start.IsCompleted && !close.IsCompleted && lifecycle.StopRequested,
                    "slow factory is background work and stop does not wait for it");
                release.Set(); await Program.MustComplete(close);
                Check(!await start && await close && lifecycle.Owner!.StopRequested && lifecycle.Owner.FullyReleased && !File.Exists(fixture.Discovery),
                    "owner created after stop is attached only for close, never published");
                Check(ReferenceEquals(close, lifecycle.CloseAsync()) && !await lifecycle.StartAsync(), "closed lifecycle cannot restart or duplicate close");
            }
            using (var fixture = new Fixture())
            {
                var entered = Completion(); var release = Completion();
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(initialize: async authority =>
                { await authority.UpdateAsync("probe", GrantScope.Read, fixture.Token, null); entered.SetResult(true); await release.Task; }));
                Task<bool> start = lifecycle.StartAsync(); await Program.MustComplete(entered.Task);
                lifecycle.RequestStop(); var close = lifecycle.CloseAsync();
                Check(!close.IsCompleted && await fixture.Create() == null, "stop during initialization retains lease until actual factory completion");
                release.SetResult(true); await Program.MustComplete(close);
                Check(!await start && lifecycle.Owner!.FullyReleased && !File.Exists(fixture.Discovery), "slow initialization cannot publish after stopping");
            }
        }
        private static async Task LatePublication()
        {
            Program.Stage = "connection lifecycle late activation IO";
            using (var fixture = new Fixture())
            {
                ControlledPublication? publication = null;
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(publisher: inner => publication = new ControlledPublication(inner, true)));
                Task<bool> start = lifecycle.StartAsync();
                await WaitUntil(() => publication != null);
                await Program.MustComplete(publication!.Entered.Task);
                var owner = lifecycle.Owner!; var subject = owner.Authority.Read("probe")!;
                lifecycle.RequestStop(); Task<bool> close = lifecycle.CloseAsync(); lifecycle.RequestStop();
                Check(owner.Context.Read().State == "unavailable" && !owner.Authority.Allows(subject, GrantScope.Read) && !close.IsCompleted,
                    "RequestStop immediately invalidates context and admission while publishing IO waits");
                Check(await fixture.Create() == null, "activation stop does not release authority lease early");
                publication.PermitPublish.Set(); await Program.MustComplete(publication.Published.Task);
                Check(File.Exists(fixture.Discovery) && !owner.IsActive && !owner.Authority.Allows(subject, GrantScope.Read) && !close.IsCompleted,
                    "uncancellable late publication cannot restore capabilities or report active");
                publication.PermitReturn.Set(); await Program.MustComplete(close);
                Check(!await start && await close && !File.Exists(fixture.Discovery) && publication.PublishCount == 1 && publication.UnpublishCount == 1,
                    "Close waits actual activation then withdraws its last publication exactly once");
                await RejectActivation(owner);
                Check(owner.Server.Completion.IsCompleted && owner.FullyReleased && !await lifecycle.StartAsync(), "closed owner has no later activation or IO producer");
                var replacement = await fixture.Create(); Check(replacement != null, "completed close permits explicit new owner"); await replacement!.CloseAsync();
            }
        }
        private static async Task FailureAndRepeat()
        {
            Program.Stage = "connection lifecycle failure ownership";
            using (var fixture = new Fixture())
            {
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(initialize: _ => throw new IOException("Synthetic private initialization message.")));
                Check(!await lifecycle.StartAsync() && lifecycle.Read().FailureCode == "INITIALIZATION_FAILED" && lifecycle.Read().ExceptionType == "IOException",
                    "initialization exception is observed and retained only as sanitized category");
                Check(await lifecycle.CloseAsync() && !File.Exists(fixture.Discovery), "initialization failure closes without a published endpoint");
                var replacement = await fixture.Create(); Check(replacement != null, "initialization exception releases its actual lease"); await replacement!.CloseAsync();
            }
            using (var fixture = new Fixture())
            {
                ControlledPublication? publication = null;
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(publisher: inner => publication = new ControlledPublication(inner, false) { FailAfterPublish = true }));
                Check(!await lifecycle.StartAsync() && lifecycle.Read().FailureCode == "ACTIVATION_FAILED", "activation IO failure is observed, never reported ready");
                Check(await lifecycle.CloseAsync() && lifecycle.Owner!.FullyReleased && !File.Exists(fixture.Discovery) && publication!.UnpublishCount == 1,
                    "activation failure cleans actual publication and listener through owner close");
                Check(ReferenceEquals(lifecycle.CloseAsync(), lifecycle.CloseAsync()), "failure and repeated stop share one close task");
            }
            using (var fixture = new Fixture())
            {
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create());
                Check(await lifecycle.StartAsync() && lifecycle.Read().Phase == GameConnectionPhase.Active && File.Exists(fixture.Discovery), "successful activation publishes before active result");
                var subject = lifecycle.Owner!.Authority.Read("probe")!;
                lifecycle.RequestStop();
                Check(!lifecycle.Owner.Authority.Allows(subject, GrantScope.Read) && lifecycle.Owner.Context.Read().State == "unavailable", "stop after active invalidates product immediately");
                Check(await lifecycle.CloseAsync() && lifecycle.Read().Phase == GameConnectionPhase.Stopped && !File.Exists(fixture.Discovery), "late stop completes ordinary release");
            }
        }
        private static async Task NativeClose()
        {
            Program.Stage = "connection lifecycle known native completion";
            using (var fixture = new Fixture())
            {
                var lifecycle = new GameConnectionLifecycle(() => fixture.Create(scope: GrantScope.Read | GrantScope.Control));
                Check(await lifecycle.StartAsync(), "controlled native lifecycle fixture starts explicitly authorized");
                var owner = lifecycle.Owner!; var subject = owner.Authority.Read("probe")!;
                var target = new LoadTarget("probe", "save", "version");
                string id = (await owner.Operations.SubmitAsync(new SubmissionKey(owner.Operations.IssueWriteContext(subject), "known-native"), target, subject)).OperationId!;
                await owner.Operations.PersistCommitIntentAsync(id);
                var native = Completion(); var gate = new NativeLoadGate(owner.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(native.Task, target));
                owner.Operations.TryCommitAtSafePoint(id, gate);
                lifecycle.RequestStop(); Task<bool> close = lifecycle.CloseAsync();
                Check(!close.IsCompleted && gate.HasInFlight && owner.Operations.HasNativeFence && !owner.FullyReleased,
                    "lifecycle stop does not equate native Task with IO shutdown");
                native.SetResult(true); await Program.MustComplete(close);
                Check(await close && !owner.NativeLeaseRetained && !owner.Operations.HasNativeFence && owner.FullyReleased,
                    "known native Task termination permits full lifecycle release");
            }
        }
        private static async Task RejectActivation(ConnectionAuthorityOwner owner)
        { try { await owner.ActivateAsync(); } catch (InvalidOperationException) { checks++; return; } throw new InvalidOperationException("Stopped owner activated."); }
        private static async Task WaitUntil(Func<bool> ready)
        {
            for (int i = 0; i < 100 && !ready(); i++) await Task.Delay(10);
            if (!ready()) throw new InvalidOperationException("Fixture scheduling deadline.");
        }
        private sealed class ControlledPublication : IConnectionPublication
        {
            private readonly IConnectionPublication inner;
            internal readonly TaskCompletionSource<bool> Entered = Completion(), Published = Completion();
            internal readonly ManualResetEventSlim PermitPublish, PermitReturn;
            internal bool FailAfterPublish;
            internal int PublishCount, UnpublishCount;
            internal ControlledPublication(IConnectionPublication inner, bool pause)
            { this.inner = inner; PermitPublish = new ManualResetEventSlim(!pause); PermitReturn = new ManualResetEventSlim(!pause); }
            public void Publish(EnvironmentDiscovery discovery)
            {
                Interlocked.Increment(ref PublishCount); Entered.TrySetResult(true);
                if (!PermitPublish.Wait(5000)) throw new TimeoutException();
                inner.Publish(discovery); Published.TrySetResult(true);
                if (!PermitReturn.Wait(5000)) throw new TimeoutException();
                if (FailAfterPublish) throw new IOException("Synthetic private publication failure.");
            }
            public void Unpublish(string instance, long epoch) { Interlocked.Increment(ref UnpublishCount); inner.Unpublish(instance, epoch); }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "lifecycle-runtime-" + Guid.NewGuid().ToString("N"));
            internal string DirectoryPath => Path.Combine(root, "connection");
            internal string Discovery => Path.Combine(DirectoryPath, "discovery.json");
            internal readonly string Token = HandshakeProof.RandomHex();
            internal Fixture() { Directory.CreateDirectory(root); }
            internal async Task<ConnectionAuthorityOwner?> Create(GrantScope scope = GrantScope.Read, Func<GrantAuthority, Task>? initialize = null,
                Func<IConnectionPublication, IConnectionPublication>? publisher = null, IJournalDurability? durability = null,
                WorldContextTracker? context = null, Func<OperationAuthority, WorldContextTracker, string, IReadOnlyCapabilityDispatcher>? dispatcher = null,
                int leaseWaitMilliseconds = 1000, string productVersion = Program.ProductVersion)
            {
                var owner = await ConnectionAuthorityOwner.CreateAsync(DirectoryPath, "probe", new string('c', 64), productVersion, Program.Limits(),
                    context ?? new WorldContextTracker(Guid.NewGuid().ToString("N")), dispatcher ?? ((operations, tracker, version) => new SnapshotCapabilityDispatcher(tracker, version, operations)), true,
                    publicationPort: publisher,
                    leaseWaitMilliseconds: leaseWaitMilliseconds, journalDurability: durability);
                if (owner == null) return null;
                try
                {
                    var authorization = owner.Authority.State;
                    if (authorization.RecordRevision == 2 && authorization.AuthorityEpoch == 1 && authorization.Profiles.Count == 0)
                        await (initialize == null ? owner.Authority.UpdateAsync("probe", scope, Token, null) : initialize(owner.Authority));
                    return owner;
                }
                catch (Exception error) { await Program.CloseFailedRootAsync(owner, error); throw; }
            }
            public void Dispose()
            {
                if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture containment failed.");
                if (Directory.Exists(DirectoryPath))
                {
                    foreach (string file in Directory.GetFiles(DirectoryPath))
                    { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links."); File.Delete(file); }
                    Directory.Delete(DirectoryPath);
                }
                Directory.Delete(root);
            }
        }
    }
}
