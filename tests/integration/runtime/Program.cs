using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
    internal static class Program
    {
        internal const string ProductVersion = "0.1.0-dev.0";
        internal const string ContractVersion = "2";
        internal static string Stage = "entry";
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "initialize-core") return SetupChildFixture.Run(args);
            return RunRuntimeAsync(args).GetAwaiter().GetResult();
        }

        // Keep the copied Setup fixture's BCL-only entry independent of the
        // normal probe's async state machine and external project dependencies.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<int> RunRuntimeAsync(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--owned-child-exit") return 23;
                // 探针进程间文本协议固定 UTF-8 无 BOM，不继承 dotnet/终端的代码页差异。
                Console.InputEncoding = new System.Text.UTF8Encoding(false, true);
                Console.OutputEncoding = new System.Text.UTF8Encoding(false, true);
                if (args.Length == 1 && args[0] == "--self-test") { await RuntimeSelfTests.RunAsync(); return 0; }
                if (args.Length == 1 && args[0] == "--setup-initialization-probe")
                { Emit(new JObject { ["kind"] = "setup-initialization-self-test", ["checks"] = await SetupInitializationTests.RunAsync(), ["gameRuntimeValidated"] = false }); return 0; }
                if (args.Length == 1 && args[0] == "--automatic-preparation-probe")
                { Emit(new JObject { ["kind"] = "automatic-preparation-self-test", ["checks"] = await AutomaticCorePreparationTests.RunAsync(),
                    ["danglingLinkValidated"] = AutomaticCorePreparationTests.DanglingLinkValidated, ["gameRuntimeValidated"] = false }); return 0; }
                if (args.Length == 1 && args[0] == "--module-runtime-probe")
                { Emit(new JObject { ["kind"] = "module-runtime-self-test", ["checks"] = await ModuleRuntimeObservationTests.RunAsync(), ["gameRuntimeValidated"] = false }); return 0; }
                if (args.Length == 1 && args[0] == "--city-overview-probe")
                { Emit(new JObject { ["kind"] = "city-overview-self-test", ["checks"] = await CityOverviewTests.RunAsync(), ["gameRuntimeValidated"] = false }); return 0; }
                if (args.Length == 1 && args[0] == "--building-inspection-tests")
                { Emit(new JObject { ["kind"] = "building-inspection-self-test", ["checks"] = await BuildingInspectionTests.RunAsync(), ["gameRuntimeValidated"] = false }); return 0; }
                if (args.Length == 1 && args[0] == "--mixed-history-probe") return await MixedHistoryProcessProbe.RunAsync();
                if (args.Length == 1 && args[0] == "--serve") { await Serve(); return 0; }
                if (args.Length == 1 && args[0] == "--unknown-close-probe") { await DurableOperationProcessProbe.RunAsync(); return 0; }
                if (args.Length == 1 && args[0] == "--recovery-lifetime-probe") { await RecoveryLifetimeProcessProbe.RunAsync(); return 0; }
                // 本轮不提供普通用户模拟或 Shell 启动入口。
                Console.Error.WriteLine("Usage: SkylinesLink.RuntimeProbe --self-test | --serve");
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Runtime probe failed: " + Stage + ":" + error.GetType().Name);
                if (error is System.ComponentModel.Win32Exception native) Console.Error.WriteLine("Native error code: " + native.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
                if (error is IOException io) Console.Error.WriteLine("IO HRESULT: 0x" + io.HResult.ToString("X8", CultureInfo.InvariantCulture));
                foreach (var frame in new StackTrace(error, false).GetFrames() ?? Array.Empty<StackFrame>())
                {
                    var method = frame.GetMethod();
                    if (method?.DeclaringType?.Namespace?.StartsWith("SkylinesLink", StringComparison.Ordinal) == true)
                        Console.Error.WriteLine("Stage method: " + method.DeclaringType.Name + "." + method.Name);
                }
                return 1;
            }
        }
        internal static PipeLimits Limits(int milliseconds = 1500) => new PipeLimits(4, 65536, 32, 8192,
            TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(milliseconds));
        private static async Task Serve()
        {
            Stage = "serve-config";
            string? line = Console.ReadLine();
            if (line == null || line.Length > 8192) throw new InvalidOperationException("Probe configuration missing.");
            if (line.Length > 0 && line[0] == '\uFEFF') Stage = "serve-config-unexpected-bom";
            JObject config;
            try { config = (JObject)ContractJson.Parse(line); }
            catch (ContractValidationException)
            {
                Console.Error.WriteLine("Probe encoding facts: codePage=" + Console.InputEncoding.CodePage.ToString(CultureInfo.InvariantCulture) +
                    ";objectPrefixIndex=" + line.IndexOf('{').ToString(CultureInfo.InvariantCulture) + ";containsNul=" + (line.IndexOf('\0') >= 0));
                throw;
            }
            string directory = (string?)config["privateDirectory"] ?? throw new InvalidOperationException();
            string environment = (string?)config["environmentId"] ?? throw new InvalidOperationException();
            string coreHash = (string?)config["coreManifestSha256"] ?? throw new InvalidOperationException();
            string profile = (string?)config["profileId"] ?? throw new InvalidOperationException();
            string token = (string?)config["internalToken"] ?? throw new InvalidOperationException();
            string? http = (string?)config["externalHttpToken"];
            bool initialize = (bool?)config["initialize"] ?? false;
            bool operationProbe = (bool?)config["operationProbe"] ?? false;
            var context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
            Stage = "serve-owner";
            var owner = await ConnectionAuthorityOwner.CreateAsync(directory, environment, coreHash, ProductVersion, Limits((int?)config["deadlineMilliseconds"] ?? 1500),
                context, (operations, tracker, version) => new SnapshotCapabilityDispatcher(tracker, version, operations), initialize);
            if (owner == null) { Emit(new JObject { ["kind"] = "busy" }); return; }
            try
            {
                var authorization = owner.Authority.State;
                if (initialize && authorization.RecordRevision == 2 && authorization.AuthorityEpoch == 1 && authorization.Profiles.Count == 0)
                    await owner.Authority.UpdateAsync(profile, operationProbe ? GrantScope.Read | GrantScope.Control : GrantScope.Read, token, http);
                await owner.ActivateAsync();
                var ready = (JObject)ContractCodec.EncodeEnvironmentDiscovery(owner.Discovery);
                ready["kind"] = "ready"; ready["grantEpoch"] = owner.Authority.State.Find(profile)?.GrantEpoch;
                ready["runtime"] = "Windows .NET Framework 4.8"; ready["isElevated"] = StandardUserProbe.IsElevated();
                Emit(ready);
                while ((line = Console.ReadLine()) != null)
                {
                    if (line == "stop") break;
                    if (line == "metrics")
                    {
                        var usage = owner.Server.OwnedPayloadUsage;
                        var ingress = owner.Server.IngressPayloadUsage; var egress = owner.Server.EgressPayloadUsage;
                        Emit(new JObject { ["kind"] = "metrics", ["connected"] = owner.Server.ConnectedCount, ["authenticated"] = owner.Server.AuthenticatedCount,
                            ["handles"] = Process.GetCurrentProcess().HandleCount, ["privateBytes"] = Process.GetCurrentProcess().PrivateMemorySize64,
                            ["ownedPayloadCount"] = usage.Count, ["ownedPayloadCapacity"] = usage.Capacity,
                            ["maximumOwnedPayloadCount"] = usage.MaximumCount, ["maximumOwnedPayloadCapacity"] = usage.MaximumCapacity,
                            ["payloadUsageSnapshotKind"] = "directionalSamples",
                            ["peakIngressPayloadCount"] = ingress.PeakCount, ["peakIngressPayloadCapacity"] = ingress.PeakCapacity,
                            ["peakEgressPayloadCount"] = egress.PeakCount, ["peakEgressPayloadCapacity"] = egress.PeakCapacity });
                    }
                    else if (line == "revoke" || line == "rotate")
                    {
                        var grant = owner.Authority.State.Find(profile) ?? throw new InvalidOperationException();
                        await owner.Authority.UpdateAsync(profile, line == "revoke" ? GrantScope.None : grant.Scope,
                            line == "rotate" ? HandshakeProof.RandomHex() : grant.InternalToken, grant.ExternalHttpToken);
                        Emit(new JObject { ["kind"] = line == "revoke" ? "revoked" : "rotated", ["grantEpoch"] = owner.Authority.State.Find(profile)!.GrantEpoch });
                    }
                    else if (line == "load-generation")
                    { long generation = context!.EnterLoading(); context.ObserveReady(generation); Emit(new JObject { ["kind"] = "loaded", ["generation"] = generation }); }
                    else if (operationProbe && line.StartsWith("{", StringComparison.Ordinal))
                    {
                        var control = (JObject)ContractJson.Parse(line);
                        var subject = owner.Authority.Read(profile) ?? throw new InvalidOperationException("Probe profile unavailable.");
                        string command = (string?)control["command"] ?? "";
                        if (command == "context") Emit(new JObject { ["kind"] = "write-context", ["writeContextId"] = owner.Operations.IssueWriteContext(subject) });
                        else if (command == "submit" || command == "submit-lost-ack" || command == "submit-complete-false")
                        {
                            var key = new SubmissionKey((string)control["writeContextId"]!, (string)control["requestId"]!);
                            var target = new LoadTarget("probe", "synthetic-save", "revision-1");
                            var accepted = await owner.Operations.SubmitAsync(key, target, subject);
                            if (command == "submit-complete-false")
                            {
                                // 仅受operationProbe配置和当前Control约束的进程夹具；不装载游戏或存档。
                                await owner.Operations.PersistCommitIntentAsync(accepted.OperationId!);
                                if (!owner.Operations.TryCommitAtSafePoint(accepted.OperationId!, new NativeLoadGate(context!.Read().InstanceId,
                                    _ => NativeLoadInvocation.Started(Task.FromResult(false), target))))
                                    throw new InvalidOperationException("Operation probe native entry was not permitted.");
                                await owner.Operations.DrainAsync(accepted.OperationId!);
                                var finished = owner.Operations.Lookup(accepted.OperationId!, subject);
                                Emit(new JObject { ["kind"] = "completed-false", ["operationId"] = finished.OperationId,
                                    ["writeContextId"] = key.WriteContextId, ["requestId"] = key.RequestId,
                                    ["state"] = finished.State, ["durability"] = finished.Durability, ["loadReturnValue"] = finished.Load!.LoadReturnValue });
                            }
                            else if (command == "submit-lost-ack") Emit(new JObject { ["kind"] = "ack-suppressed" });
                            else Emit(new JObject { ["kind"] = "accepted", ["operationId"] = accepted.OperationId, ["writeContextId"] = key.WriteContextId, ["requestId"] = key.RequestId });
                        }
                        else if (command == "retire")
                            Emit(new JObject { ["kind"] = "retirement", ["outcome"] = (await owner.Operations.TryRetireAsync()).ToString() });
                        else throw new InvalidOperationException("Unknown operation probe control.");
                    }
                    else throw new InvalidOperationException("Unknown probe control.");
                }
            }
            finally { await owner.CloseAsync(); }
            Emit(new JObject { ["kind"] = "stopped", ["connected"] = owner.Server.ConnectedCount, ["authenticated"] = owner.Server.AuthenticatedCount,
                ["ownedPayloadCount"] = owner.Server.OwnedPayloadUsage.Count, ["ownedPayloadCapacity"] = owner.Server.OwnedPayloadUsage.Capacity });
        }
        internal static async Task MustComplete(Task work, int timeout = 5000)
        { if (await Task.WhenAny(work, Task.Delay(timeout)) != work) throw new InvalidOperationException("Probe deadline exceeded."); await work; }
        internal static async Task CloseFailedRootAsync(ConnectionAuthorityOwner owner, Exception cause)
        {
            bool closeFailed = false;
            try { await owner.CloseAsync(); } catch { closeFailed = true; }
            if (closeFailed || !owner.FullyReleased)
                throw new ConnectionInitializationException(cause, owner.FullyReleased && !closeFailed, owner.NativeLeaseRetained);
        }
        internal static void Emit(JObject value) { Console.WriteLine(value.ToString(Newtonsoft.Json.Formatting.None)); Console.Out.Flush(); }
        internal static void Assert(bool condition, string name) { if (!condition) { Stage = name; throw new InvalidOperationException(name); } }
        internal static void Expect<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected rejection was absent."); }
    }
}
