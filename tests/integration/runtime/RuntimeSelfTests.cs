using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    internal static partial class RuntimeSelfTests
    {
        private static int checks;
        private const string Profile = "Probe.With_Id";
        private static readonly string Core = new string('c', 64);
        internal static async Task RunAsync()
        {
            CheckIntegrationProbeLaunch();
            checks += OwnedChildProcessTests.Run();
            checks += ProbeTaskObservationTests.Run();
            CheckContextAndFrames();
            checks += InstalledCoreBindingTests.Run();
            checks += BundledCoreBindingTests.Run();
            checks += await SetupInitializationTests.RunAsync();
            checks += await AutomaticCorePreparationTests.RunAsync();
            checks += ConnectionSettingsPresentationTests.Run();
            checks += await InstalledGameConnectionTests.RunAsync();
            CheckProofVectors();
            checks += WindowsBoundaryTests.Run();
            await CheckSharedFrameVectorsAsync();
            await CheckFragmentedFramesAsync();
            checks += await PayloadOwnershipTests.RunAsync();
            checks += await LoadTransitionTests.RunAsync();
            checks += await DurableOperationTests.RunAsync();
            checks += await RecoverySealingTests.RunAsync();
            checks += JournalBankFormatTests.Run();
            checks += await JournalRotationTests.RunAsync();
            checks += await OperationRetentionTests.RunAsync();
            checks += MixedJournalFoldTests.Run();
            checks += TaskSharedRulesTests.Run();
            checks += await MixedJournalFrameTests.RunAsync();
            checks += await MixedJournalBankTests.RunAsync();
            checks += await TaskJournalWriterTests.RunAsync();
            checks += await TaskAuthorityTests.RunAsync();
            checks += await StopPipeTests.RunAsync();
            checks += await ModuleRuntimeObservationTests.RunAsync();
            checks += await CityOverviewTests.RunAsync();
            checks += await HistoryPublisherTests.RunAsync();
            checks += await MixedHistoryPublisherTests.RunAsync();
            checks += await ConnectionLifecycleTests.RunAsync();
            await CheckAuthorityAsync();
            await CheckProfileIdentityAsync();
            await CheckAuthorizationV2BoundaryAsync();
            await CheckFailureAsync();
            await CheckConnectionInitializationAsync();
            await CheckPipeAsync();
            await CheckProcessesAsync();
            Program.Emit(new JObject { ["kind"] = "self-test", ["checks"] = checks, ["result"] = "passed",
                ["automaticPreparationDanglingLinkValidated"] = AutomaticCorePreparationTests.DanglingLinkValidated,
                ["journalControlMaxPayloadBytes"] = JournalBankFormatTests.MaximumControlPayloadObserved,
                ["mixedMetadataMaxPayloadBytes"] = MixedJournalFrameTests.MaximumMetadataPayloadObserved,
                ["mixedControlMaxPayloadBytes"] = MixedJournalBankTests.MaximumControlPayloadObserved,
                ["runtime"] = "Windows .NET Framework 4.8", ["isElevated"] = StandardUserProbe.IsElevated(), ["gameRuntimeValidated"] = false });
        }
        private static void CheckIntegrationProbeLaunch()
        {
            Program.Stage = "normal launches cannot activate a stale development probe";
            const string run = "0123456789abcdef0123456789abcdef";
            const string flag = SkylinesLink.Mod.Runtime.IntegrationProbeLaunch.Prefix;
            bool Read(string[] args) => SkylinesLink.Mod.Runtime.IntegrationProbeLaunch.TryReadRunId(args, out _);
            Program.Assert(!Read(new[] { "Cities2.exe" }) && !Read(new[] { "Cities2.exe", "-silent", "-batchmode" }), "normal and unrelated flags never select the development probe");
            Program.Assert(SkylinesLink.Mod.Runtime.IntegrationProbeLaunch.TryReadRunId(new[] { "Cities2.exe", flag + run }, out var selected) && selected == run,
                "only an exact per-launch development run marker is returned");
            Program.Assert(!Read(new[] { flag + run, flag + run }) && !Read(new[] { flag + run, flag + new string('a', 32) }), "duplicate launch markers are rejected even when equal");
            Program.Assert(!Read(new[] { flag + run.ToUpperInvariant() }) && !Read(new[] { flag + run + "x" }) && !Read(new[] { flag }), "noncanonical or malformed launch markers are rejected");
            Program.Assert(!Read(new[] { "--skylineslink-dev-probe", run }) && !Read(new[] { "--skylineslink-dev-probe-other=" + run }), "similar arguments cannot opt a normal launch into the probe");
            Program.Assert(!SkylinesLink.Mod.Runtime.IntegrationProbeLaunch.TryReadRunId(new[] { flag + run, flag }, out var rejected) && rejected == "", "rejected launch never publishes a partial run marker");
            checks += 6;
        }
        private static async Task CheckFragmentedFramesAsync()
        {
            var limits = Program.Limits();
            using (var encoded = new MemoryStream())
            {
                await FrameCodec.WriteAsync(encoded, new JObject { ["requestId"] = "first" }, limits, CancellationToken.None);
                await FrameCodec.WriteAsync(encoded, new JObject { ["requestId"] = "second" }, limits, CancellationToken.None);
                using (var fragmented = new FragmentedStream(encoded.ToArray()))
                {
                    var first = ContractJson.Parse((await FrameCodec.ReadAsync(fragmented, limits, limits.FrameTimeout, CancellationToken.None))!);
                    var second = ContractJson.Parse((await FrameCodec.ReadAsync(fragmented, limits, limits.FrameTimeout, CancellationToken.None))!);
                    Check((string?)first["requestId"] == "first" && (string?)second["requestId"] == "second" &&
                        await FrameCodec.ReadAsync(fragmented, limits, limits.FrameTimeout, CancellationToken.None) == null, "coalesced frames survive three-byte reads");
                }
            }
        }
        private static async Task CheckSharedFrameVectorsAsync()
        {
            Program.Stage = "shared pipe frame vectors";
            var vectors = (JObject)ContractJson.Parse(File.ReadAllText("contracts/fixtures/pipe-frame-v1.json"));
            var limits = Program.Limits();
            Check((string?)vectors["format"] == "SLNK/1" && (int)vectors["maximumBytes"]! == limits.MaximumPayloadBytes, "shared frame identity and limit");
            foreach (JObject vector in (JArray)vectors["encodeVectors"]!)
            {
                string name = (string)vector["name"]!, json = (string)vector["json"]!;
                byte[] expected = FrameHex((string)vector["frameHex"]!);
                using (var encoded = new MemoryStream())
                {
                    await FrameCodec.WriteAsync(encoded, ContractJson.Parse(json), limits, CancellationToken.None);
                    Check(encoded.ToArray().SequenceEqual(expected) && expected.Length - FrameCodec.HeaderLength == (int)vector["payloadBytes"]! &&
                        expected.Skip(FrameCodec.HeaderLength).SequenceEqual(FrameHex((string)vector["payloadHex"]!)), "shared exact encoding " + name);
                }
                using (var input = new FragmentedStream(expected))
                {
                    string? decoded = await FrameCodec.ReadAsync(input, limits, limits.FrameTimeout, CancellationToken.None);
                    Check(decoded == json, "shared fragmented decode " + name);
                    // A one- or two-byte JSON payload is frame-valid, not a Wire message.
                    if ((int)vector["payloadBytes"]! <= 2)
                        Reject<ContractValidationException>(() => ContractCatalog.ParseAndValidate(SchemaIds.WireChallengeV2, decoded!), "small frame is not Wire " + name);
                }
            }
            foreach (JObject vector in (JArray)vectors["headers"]!)
            {
                string name = (string)vector["name"]!;
                byte[] header = FrameHex((string)vector["hex"]!);
                if (vector["length"]!.Type != JTokenType.Null)
                    Check(FrameCodec.DecodeLength(header, limits.MaximumPayloadBytes) == (int)vector["length"]!, "shared header " + name);
                else
                {
                    try { FrameCodec.DecodeLength(header, limits.MaximumPayloadBytes); throw new InvalidOperationException("Expected frame header rejection absent."); }
                    catch (PipeProtocolException error) { Check(error.Code == (string)vector["error"]!, "shared header rejection " + name); }
                    if ((string?)vector["error"] == "FRAME_LIMIT")
                    {
                        // Header-only stream: the same read path must reject before acquiring a body owner.
                        var budget = new CapacityBudget(1, limits.MaximumPayloadBytes);
                        using (var input = new FragmentedStream(header))
                            await RejectFrameAsync(() => FrameCodec.ReadAsync(input, limits, limits.FrameTimeout, CancellationToken.None, budget), "FRAME_LIMIT", "shared preallocation rejection " + name);
                        Check(budget.Read().PeakCapacity == 0, "no body allocated " + name);
                    }
                }
            }
            foreach (JObject vector in (JArray)vectors["invalidUtf8"]!)
            {
                string name = (string)vector["name"]!;
                byte[] frame = FrameHex((string)vector["frameHex"]!);
                Check(FrameCodec.DecodeLength(frame.Take(FrameCodec.HeaderLength).ToArray(), limits.MaximumPayloadBytes) == frame.Length - FrameCodec.HeaderLength, "invalid UTF8 still has valid header " + name);
                var budget = new CapacityBudget(1, limits.MaximumPayloadBytes);
                using (var input = new FragmentedStream(frame))
                    await RejectFrameAsync(() => FrameCodec.ReadAsync(input, limits, limits.FrameTimeout, CancellationToken.None, budget), (string)vector["error"]!, "shared UTF8 rejection " + name);
                Check(budget.Read().Count == 0 && budget.Read().Capacity == 0, "invalid UTF8 body released " + name);
            }
            var shortHeader = (JObject)((JArray)vectors["headers"]!).Single(v => (string?)v["name"] == "short-header");
            using (var input = new FragmentedStream(FrameHex((string)shortHeader["hex"]!)))
                await RejectFrameAsync(() => FrameCodec.ReadAsync(input, limits, limits.FrameTimeout, CancellationToken.None), "TRUNCATED_FRAME", "EOF during header is truncated stream");
        }
        private static byte[] FrameHex(string value)
        {
            if (value.Length % 2 != 0) throw new FormatException("Odd frame hex length.");
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }
        private static async Task RejectFrameAsync(Func<Task> work, string code, string name)
        {
            Program.Stage = name;
            try { await work(); }
            catch (PipeProtocolException error) { Check(error.Code == code, name); return; }
            throw new InvalidOperationException("Expected frame read rejection absent.");
        }
        private static void Check(bool condition, string name) { Program.Assert(condition, name); checks++; }
        private static void Reject<T>(Action work, string name) where T : Exception { Program.Stage = name; Program.Expect<T>(work); checks++; }
        private static async Task RejectAsync<T>(Func<Task> work, string name) where T : Exception
        { Program.Stage = name; try { await work(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected rejection absent."); }
        private static void CheckContextAndFrames()
        {
            var context = new WorldContextTracker("instance-a");
            Check(!context.AllowsWorld("instance-a", 0), "menu is not world authority");
            long first = context.EnterLoading();
            Check(context.ObserveReady(first, "city-a") && context.AllowsWorld("instance-a", first), "current loaded generation");
            long second = context.EnterLoading();
            Check(!context.AllowsWorld("instance-a", first) && !context.ObserveReady(first, "city-a"), "late world result");
            Check(context.ObserveReady(second, "city-a") && first != second, "same city new generation");
            context.EnterMenu(); Check(!context.AllowsWorld("instance-a", second), "menu invalidates generation");
            long unidentified = context.EnterLoading();
            Check(context.ObserveReady(unidentified) && context.Read().CityId == null && context.AllowsWorld("instance-a", unidentified) &&
                !context.AllowsWorld("other-instance", unidentified), "verified loaded scope does not require persistent city identity");
            Check(context.ObserveCityIdentity(unidentified, "saved-city") && context.Read().Generation == unidentified &&
                context.AllowsWorld("instance-a", unidentified), "first identity observation enriches the existing loaded scope");
            Check(!context.ObserveCityIdentity(unidentified, "different-city") && !context.ObserveCityIdentity(second, "saved-city") &&
                context.Read().CityId == "saved-city", "conflicting or stale identity cannot silently replace the loaded city");
            context.Stop(); var terminal = context.Read();
            context.Stop(); context.MarkUnavailable();
            Check(context.IsStopped && terminal.State == "unavailable" && terminal.CityId == null &&
                ReferenceEquals(terminal, context.Read()) && !context.AllowsWorld("instance-a", unidentified),
                "terminal stop invalidates the scope once and repeated stop or unavailability cannot advance it");
            Check(!context.ObserveReady(unidentified, "late-city") && !context.ObserveCityIdentity(unidentified, "late-city") &&
                ReferenceEquals(terminal, context.Read()), "late native ready and identity observations cannot revive a stopped context");
            Reject<InvalidOperationException>(() => context.EnterLoading(), "stopped context cannot enter another loading generation");
            Reject<InvalidOperationException>(() => context.EnterMenu(), "stopped context cannot return to menu");
            var header = new byte[12]; header[0] = 83; header[1] = 76; header[2] = 78; header[3] = 75; header[4] = 1;
            for (int i = 8; i < 12; i++) header[i] = 255;
            Reject<PipeProtocolException>(() => FrameCodec.DecodeLength(header, 65536), "preallocation frame limit");
            header[8] = 1; header[9] = header[10] = header[11] = 0; header[6] = 1;
            Reject<PipeProtocolException>(() => FrameCodec.DecodeLength(header, 65536), "reserved frame field");
            Reject<ArgumentException>(() => BoundedPipeServer.ValidatePipeName("\\\\remote\\pipe\\x"), "remote endpoint rejected");
            var rules = LogonPipeSecurity.Create().GetAccessRules(true, true, typeof(SecurityIdentifier));
            Check(rules.Count == 1 && ((PipeAccessRule)rules[0]).AccessControlType == AccessControlType.Allow &&
                ((SecurityIdentifier)rules[0].IdentityReference).IsWellKnown(WellKnownSidType.LogonIdsSid), "explicit logon ACL");
            AuthorizationState.ValidateIdentifier("CON");
            Check(AuthorizationState.NormalizeEnvironment("COM0") == "com0", "profile and environment identifier semantics");
            Reject<ArgumentException>(() => AuthorizationState.NormalizeEnvironment("CON"), "reserved environment");
        }
        private static void CheckProofVectors()
        {
            Program.Stage = "shared HMAC vectors";
            var vectors = (JArray)ContractJson.Parse(File.ReadAllText("contracts/fixtures/handshake-v2.json"))["vectors"]!;
            foreach (JObject vector in vectors)
            {
                var fields = (JObject)vector["fields"]!.DeepClone(); fields["kind"] = "challenge"; fields["serverProof"] = vector["serverProof"];
                var identity = ContractCodec.DecodeWireChallengeV2(fields);
                foreach (string role in new[] { "server", "client" })
                    Check(HandshakeProof.Compute((string)vector["internalToken"]!, role, identity) == (string)vector[role + "Proof"]! &&
                        HandshakeProof.Transcript(role, identity).Length == (int)vector[role + "TranscriptBytes"]!, "shared HMAC " + (string)vector["name"]! + " " + role);
            }
        }
        private static async Task CheckAuthorityAsync()
        {
            Program.Stage = "authority-tests";
            using (var fixture = new Fixture())
            {
                ControlledStore? control = null;
                var owner = (await Open(fixture, 5000, store => control = new ControlledStore(store)))!;
                try
                {
                    var authority = owner.Authority;
                    var initial = authority.State;
                    Check(initial.AuthorityEpoch == 1 && initial.RecordRevision == 3 && initial.Profiles.Count == 1, "durable activation precedes profile");
                    var old = authority.Read(Profile)!;
                    string originalProfileIdentity = old.ProfileInstanceId;
                    Check(originalProfileIdentity.Length == 32 && originalProfileIdentity.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') &&
                        authority.CaptureAuthentication(Profile, old.AuthorityEpoch, old.Epoch)!.Subject.ProfileInstanceId == originalProfileIdentity,
                        "Read and authentication capture share one stable lowercase profile identity");
                    await authority.UpdateAsync(Profile, GrantScope.Read | GrantScope.Write, fixture.Token, null);
                    Check(!authority.Allows(old, GrantScope.Read), "scope change invalidates captured grant");
                    Check(authority.Read(Profile)!.ProfileInstanceId == originalProfileIdentity, "scope update keeps the same profile incarnation");
                    await authority.UpdateAsync(Profile, GrantScope.Read | GrantScope.Write, HandshakeProof.RandomHex(), null);
                    Check(authority.Read(Profile)!.ProfileInstanceId == originalProfileIdentity, "token rotation keeps the same profile incarnation");
                    var admitted = authority.Read(Profile)!;
                    int positiveCommit = 0;
                    Check(authority.TryCommit(admitted, GrantScope.Write, () => positiveCommit++) && positiveCommit == 1, "authorized synchronous commit positive control");
                    var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var scheduler = new CommitScheduler(); int nativeCount = 0;
                    Task<bool> work = WaitForAckAndCommit(ack.Task, scheduler, () => authority.TryCommit(admitted, GrantScope.Write, () => nativeCount++));
                    ack.SetResult(true);
                    await Program.MustComplete(scheduler.Entered.Task);
                    await authority.UpdateAsync(Profile, GrantScope.None, fixture.Token, null);
                    scheduler.Run();
                    Check(!await work && nativeCount == 0, "durable ack then revocation prevents queued native commit");
                    await authority.UpdateAsync(Profile, GrantScope.Read, fixture.Token, null);
                    var deleted = authority.Read(Profile)!;
                    Check(deleted.ProfileInstanceId == originalProfileIdentity, "scope None and later regrant do not recreate an existing profile");
                    await authority.RemoveAsync(Profile);
                    await authority.UpdateAsync(Profile, GrantScope.Read, fixture.Token, null);
                    Check(!authority.Allows(deleted, GrantScope.Read) && authority.Read(Profile)!.Epoch > deleted.Epoch &&
                        authority.Read(Profile)!.ProfileInstanceId != deleted.ProfileInstanceId, "deleted profile cannot revive old grant or stable ownership identity");
                    await authority.UpdateAsync("healthy", GrantScope.Read, HandshakeProof.RandomHex(), null);
                    control!.Pause = true;
                    Task change = authority.UpdateAsync(Profile, GrantScope.None, fixture.Token, null);
                    await Program.MustComplete(control.Entered.Task);
                    Check(authority.Read(Profile) == null && authority.Read("healthy") != null, "pending persistence denies affected profile only");
                    Task close = owner.CloseAsync();
                    Check(!close.IsCompleted, "close waits pending writer");
                    using (var competitor = new EnvironmentLeaseProvider(new PrivateConnectionStorage(fixture.Connection)).TryAcquireAuthority())
                        Check(competitor == null, "pending writer retains environment lease");
                    control.Release.Set(); await change; await Program.MustComplete(close);
                    using (var released = new EnvironmentLeaseProvider(new PrivateConnectionStorage(fixture.Connection)).TryAcquireAuthority())
                        Check(released != null && !File.Exists(Path.Combine(fixture.Connection, "discovery.json")), "lease released after pipe and writer completion");
                }
                finally { control?.Release.Set(); await owner.CloseAsync(); }
                // 已有目录丢失授权记录不是一个新配置，initialize=true 也不能重置 epoch。
                File.Delete(Path.Combine(fixture.Connection, "authorization.json"));
                await RejectAsync<InvalidOperationException>(async () => { await Open(fixture); }, "missing existing authorization never recreated");
                Check(!File.Exists(Path.Combine(fixture.Connection, "authorization.json")), "missing authority remains missing");
            }
        }
        private static async Task CheckProfileIdentityAsync()
        {
            Program.Stage = "stable profile identity and atomic continuity";
            using (var fixture = new Fixture())
            {
                var boundary = new PrivateConnectionStorage(fixture.Connection);
                using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!)
                {
                    var store = new AtomicAuthorizationStore(boundary, lease, "probe"); store.InitializeEmpty();
                    var activated = store.Load().Activate(); store.Save(1, activated);
                    var authority = new GrantAuthority(activated, store);
                    await authority.UpdateAsync(Profile, GrantScope.Read | GrantScope.Control, fixture.Token, null);
                    var subject = authority.Read(Profile)!; var current = store.Load(); var profile = current.Find(Profile)!;
                    var foreignProfile = new AuthorizationProfile(Profile, Guid.NewGuid().ToString("N"), profile.GrantEpoch, profile.Scope, profile.InternalToken, profile.ExternalHttpToken);
                    var foreign = new GrantAuthority(new AuthorizationState("probe", current.RecordRevision, current.AuthorityEpoch, new[] { foreignProfile }), store);
                    int commits = 0;
                    Check(!authority.Allows(foreign.Read(Profile)!, GrantScope.Read) &&
                        !authority.TryCommit(foreign.Read(Profile)!, GrantScope.Control, () => commits++) && commits == 0,
                        "matching profile name and epochs cannot authorize another profile incarnation");
                    await foreign.StopAsync();

                    string before = File.ReadAllText(store.AuthorizationPath);
                    long nextRevision = AuthorizationState.Next(current.RecordRevision);
                    var swapped = new AuthorizationProfile(Profile, Guid.NewGuid().ToString("N"), nextRevision, profile.Scope, profile.InternalToken, profile.ExternalHttpToken);
                    Reject<InvalidDataException>(() => store.Save(current.RecordRevision,
                        new AuthorizationState("probe", nextRevision, current.AuthorityEpoch, new[] { swapped })),
                        "atomic Save cannot replace the incarnation of an existing profile name");
                    var renamed = new AuthorizationProfile("other", profile.ProfileInstanceId, nextRevision, profile.Scope, profile.InternalToken, profile.ExternalHttpToken);
                    Reject<InvalidDataException>(() => store.Save(current.RecordRevision,
                        new AuthorizationState("probe", nextRevision, current.AuthorityEpoch, new[] { renamed })),
                        "atomic Save cannot transfer an existing incarnation to another profile name");
                    Check(File.ReadAllText(store.AuthorizationPath) == before, "identity continuity failures leave the exact authorization bytes unchanged");

                    await authority.StopAsync();
                    var next = store.Load().Activate(); store.Save(current.RecordRevision, next);
                    var reopened = new AtomicAuthorizationStore(boundary, lease, "probe").Load();
                    var after = new GrantAuthority(reopened, store);
                    var read = after.Read(Profile)!;
                    Check(reopened.AuthorityEpoch == current.AuthorityEpoch + 1 && read.ProfileInstanceId == subject.ProfileInstanceId &&
                        after.CaptureAuthentication(Profile, read.AuthorityEpoch, read.Epoch)!.Subject.ProfileInstanceId == subject.ProfileInstanceId &&
                        !after.Allows(subject, GrantScope.Read),
                        "durable activation and store reopen keep profile identity while invalidating the old authority epoch");
                    await after.StopAsync();
                }
            }
            string identity = new string('a', 32);
            var first = new AuthorizationProfile("first", identity, 1, GrantScope.Read, HandshakeProof.RandomHex(), null);
            var second = new AuthorizationProfile("second", identity, 1, GrantScope.Read, HandshakeProof.RandomHex(), null);
            Reject<ArgumentException>(() => new AuthorizationState("probe", 2, 1, new[] { first, second }), "different names cannot share one profile incarnation");
            foreach (string invalid in new[] { new string('a', 31), new string('A', 32), new string('g', 32) })
                Reject<ArgumentException>(() => new AuthorizationProfile("invalid", invalid, 1, GrantScope.Read, HandshakeProof.RandomHex(), null),
                    "profile incarnation must be exactly 32 lowercase hexadecimal characters");
        }

        private static async Task CheckAuthorizationV2BoundaryAsync()
        {
            Program.Stage = "authorization format 2 rejects legacy and malformed identities without migration";
            using (var fixture = new Fixture())
            {
                var owner = (await Open(fixture))!;
                string path = Path.Combine(fixture.Connection, "authorization.json");
                string valid = File.ReadAllText(path);
                var current = (JObject)ContractJson.Parse(valid);
                Check((int?)current["formatVersion"] == 2 && ((JArray)current["profiles"]!).All(item => (string?)item["profileInstanceId"] != null),
                    "fresh private authorization writes explicit format 2 and every stable profile identity");
                string identity = owner.Authority.State.Find(Profile)!.ProfileInstanceId;
                await owner.CloseAsync();
                foreach (bool explicitVersionOne in new[] { false, true })
                {
                    var legacy = (JObject)current.DeepClone(); legacy.Remove("formatVersion");
                    foreach (JObject profile in (JArray)legacy["profiles"]!) profile.Remove("profileInstanceId");
                    if (explicitVersionOne) legacy["formatVersion"] = 1;
                    string original = legacy.ToString(Newtonsoft.Json.Formatting.None); File.WriteAllText(path, original);
                    await RejectAsync<ContractValidationException>(async () => { await Open(fixture); }, "legacy authorization is rejected without guessing an incarnation");
                    Check(File.ReadAllText(path) == original, "legacy authorization rejection preserves the exact existing file");
                }
                var malformed = (JObject)current.DeepClone(); malformed["profiles"]![0]!["profileInstanceId"] = new string('G', 32);
                string bad = malformed.ToString(Newtonsoft.Json.Formatting.None); File.WriteAllText(path, bad);
                await RejectAsync<ArgumentException>(async () => { await Open(fixture); }, "format 2 with invalid hexadecimal identity cannot activate");
                Check(File.ReadAllText(path) == bad, "invalid format 2 identity is not repaired or overwritten");
                File.WriteAllText(path, valid);
                var reopened = (await Open(fixture))!;
                Check(reopened.Authority.State.Find(Profile)!.ProfileInstanceId == identity, "normal format 2 reopen preserves the original stored identity");
                await reopened.CloseAsync();
            }
        }
        private static async Task<bool> WaitForAckAndCommit(Task ack, CommitScheduler scheduler, Func<bool> commit)
        { await ack; return await scheduler.Enqueue(commit); }
        private static async Task CheckFailureAsync()
        {
            Program.Stage = "authority-failure-tests";
            using (var fixture = new Fixture())
            {
                ControlledStore? control = null;
                var owner = (await Open(fixture, 5000, store => control = new ControlledStore(store)))!;
                var old = owner.Authority.Read(Profile)!;
                string profileIdentity = old.ProfileInstanceId;
                control!.FailAfterSave = true;
                await RejectAsync<IOException>(() => owner.Authority.UpdateAsync(Profile, GrantScope.None, fixture.Token, null), "uncertain persistence failure surfaced");
                Check(!owner.Authority.Allows(old, GrantScope.Read) && owner.Authority.Read(Profile) == null, "failed writer never restores old grant");
                await RejectAsync<InvalidOperationException>(() => owner.Authority.EnsureProfileAsync(Profile, old.Scope, false,
                    () => throw new InvalidOperationException("A failed writer must not request credentials.")),
                    "sticky persistence failure takes precedence over an apparently matching old configuration");
                await owner.CloseAsync();
                var restarted = (await Open(fixture))!;
                Check(restarted.Authority.State.AuthorityEpoch == 2 && restarted.Authority.Read(Profile) == null &&
                    restarted.Authority.State.Find(Profile)!.ProfileInstanceId == profileIdentity, "restart reloads durable revocation without recreating its profile incarnation");
                await restarted.CloseAsync();
                string path = Path.Combine(fixture.Connection, "authorization.json");
                File.WriteAllText(path, "{}");
                await RejectAsync<ContractValidationException>(async () => { await Open(fixture); }, "corrupt authorization rejected");
                Check(File.ReadAllText(path) == "{}", "corrupt authority never overwritten");
            }
            var profile = new AuthorizationProfile("CON", 1, GrantScope.Read, HandshakeProof.RandomHex(), null);
            Reject<ArgumentException>(() => new AuthorizationState("test", 1, 0, new[] { profile }), "inactive authority must be empty");
            Reject<ArgumentException>(() => new AuthorizationState("test", 2, 1, new[] { profile, profile }), "duplicate profile rejected");
            Reject<InvalidOperationException>(() => new AuthorizationState("test", AuthorizationState.MaximumCounter, 1, new[] { profile }).Activate(), "revision exhaustion fails closed");
            using (var fixture = new Fixture())
            {
                await RejectAsync<InvalidOperationException>(async () => { await ConnectionAuthorityOwner.OpenAsync(fixture.Connection, "probe", Core, Program.ProductVersion, Program.Limits(),
                    new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations)); }, "unconfigured probe cannot create storage");
                Check(!Directory.Exists(fixture.Connection), "read-only missing configuration leaves no directory");
                Directory.CreateDirectory(fixture.Connection);
                Reject<UnauthorizedAccessException>(() => new PrivateConnectionStorage(fixture.Connection), "unsafe existing directory not repaired");
            }
        }
        private static async Task CheckPipeAsync()
        {
            Program.Stage = "pipe-owner-open";
            using (var fixture = new Fixture())
            {
                var owner = (await Open(fixture, 1000))!;
                try
                {
                    checks += WindowsBoundaryTests.CheckActualPipeHandles(owner.Server);
                    var limits = Program.Limits(1000);
                    Program.Stage = "pipe-authenticated-context";
                    using (var client = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await client.ConnectAsync(2000);
                        var challenge = await BeginHandshake(client, owner, limits);
                        Check(HandshakeProof.EqualsProof(challenge.ServerProof, HandshakeProof.Compute(fixture.Token, "server", challenge)), "server possession proof");
                        await FinishHandshake(client, challenge, fixture.Token, limits);
                        await Query(client, "q-1", limits);
                        var result = ContractCatalog.ParseAndValidate(SchemaIds.WireResult, (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!, new RegisteredBinding("system.context", "2"));
                        Check((string?)result["result"]?["gameState"] == "menu", "real v2 authenticated read");
                        long loaded = owner.Context.EnterLoading(); owner.Context.ObserveReady(loaded);
                        await Query(client, "q-unidentified", limits);
                        var ready = ContractCatalog.ParseAndValidate(SchemaIds.WireResult,
                            (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!, new RegisteredBinding("system.context", "2"));
                        Check((string?)ready["result"]?["gameState"] == "ready" && ready["result"]?["world"] is JObject &&
                            ready["result"]!["world"]!["cityId"]!.Type == JTokenType.Null &&
                            (string?)ready["result"]!["world"]!["generation"] == loaded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "real authenticated context revision 2 exposes a ready scope with unknown persistent identity");
                        await QueryOperation(client, "op-live", false, limits);
                        var operation = ContractCatalog.ParseAndValidate(SchemaIds.WireResult,
                            (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!, new RegisteredBinding("operations.get", "5"));
                        Check((string?)operation["result"]?["result"]?["kind"] == "live" &&
                            (string?)operation["result"]?["result"]?["lookup"]?["kind"] == "absent", "active operation revision 5 has real authenticated live envelope");
                        await owner.History.InitialAttempt;
                        await QueryOperation(client, "op-history", true, limits);
                        var history = ContractCatalog.ParseAndValidate(SchemaIds.WireResult,
                            (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!, new RegisteredBinding("operations.get", "5"));
                        Check((string?)history["result"]?["result"]?["kind"] == "history" &&
                            (string?)history["result"]?["result"]?["lookup"]?["reason"] == "HISTORY_NOT_COVERED", "active revision 5 history uses actual published source and never live absent");
                        await owner.Authority.UpdateAsync(Profile, GrantScope.None, fixture.Token, null);
                        await Query(client, "q-2", limits);
                        var denied = await ReadError(client, limits);
                        Check((string?)denied["code"] == "UNAUTHORIZED", "revoked live connection denied");
                    }
                    await owner.Authority.UpdateAsync(Profile, GrantScope.Read, fixture.Token, null);
                    Program.Stage = "pipe-proof-replay";
                    string oldProof;
                    using (var first = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await first.ConnectAsync(2000); var challenge = await BeginHandshake(first, owner, limits);
                        oldProof = HandshakeProof.Compute(fixture.Token, "client", challenge);
                    }
                    using (var second = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await second.ConnectAsync(2000); var challenge = await BeginHandshake(second, owner, limits);
                        Check(oldProof != HandshakeProof.Compute(fixture.Token, "client", challenge), "fresh server nonce changes proof");
                        await FrameCodec.WriteAsync(second, ContractCodec.EncodeWireProofV2(new WireProofV2 { Kind = "proof", RequestId = challenge.RequestId, ClientProof = oldProof }), limits, CancellationToken.None);
                        Check((string?)(await ReadError(second, limits))["code"] == "UNAUTHORIZED", "captured proof cannot replay");
                    }
                    using (var pending = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        Program.Stage = "pipe-revocation-during-challenge";
                        await pending.ConnectAsync(2000); var challenge = await BeginHandshake(pending, owner, limits);
                        await owner.Authority.UpdateAsync(Profile, GrantScope.None, fixture.Token, null);
                        await FrameCodec.WriteAsync(pending, ContractCodec.EncodeWireProofV2(new WireProofV2 { Kind = "proof", RequestId = challenge.RequestId, ClientProof = HandshakeProof.Compute(fixture.Token, "client", challenge) }), limits, CancellationToken.None);
                        Check((string?)(await ReadError(pending, limits))["code"] == "UNAUTHORIZED", "revocation during challenge denies authentication");
                    }
                    using (var half = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        Program.Stage = "pipe-half-frame-deadline";
                        await half.ConnectAsync(2000); await half.WriteAsync(new byte[] { 83 }, 0, 1); await Task.Delay(1300);
                        Check(owner.Server.ConnectedCount == 0, "absolute handshake deadline closes half-frame");
                    }
                    await owner.Authority.UpdateAsync(Profile, GrantScope.Read, fixture.Token, null);
                    using (var slow = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        Program.Stage = "pipe-whole-handshake-deadline";
                        await slow.ConnectAsync(2000); await Task.Delay(600);
                        await BeginHandshake(slow, owner, limits); await Task.Delay(600);
                        Check(owner.Server.ConnectedCount == 0 && owner.Server.AuthenticatedCount == 0, "whole handshake deadline cannot renew after hello");
                    }
                    var slots = new List<NamedPipeClientStream>();
                    Program.Stage = "pipe-slot-saturation";
                    try
                    {
                        for (int i = 0; i < limits.MaximumConnections; i++)
                        {
                            var slot = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                            slots.Add(slot); await slot.ConnectAsync(500);
                        }
                        await WaitCount(owner.Server, limits.MaximumConnections);
                        Check(owner.Server.ConnectedCount == limits.MaximumConnections, "pending authentication bounded by actual slots");
                        using (var excess = new NamedPipeClientStream(".", owner.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                        {
                            try { await excess.ConnectAsync(100); throw new InvalidOperationException("Excess connection was accepted."); }
                            catch (TimeoutException) { }
                            // .NET Framework 的 WaitNamedPipe 路径把 ERROR_SEM_TIMEOUT 作为 IOException 暴露。
                            catch (IOException error) when ((error.HResult & 0xffff) == 121) { }
                            Check(!excess.IsConnected && owner.Server.ConnectedCount == limits.MaximumConnections, "excess pipe connection cannot acquire a slot");
                        }
                    }
                    finally { foreach (var slot in slots) slot.Dispose(); }
                    await WaitCount(owner.Server, 0);
                    Check(owner.Server.ConnectedCount == 0, "saturated slots release after clients close");
                }
                finally { await owner.CloseAsync(); }
                Check(owner.Server.ConnectedCount == 0 && owner.Server.AuthenticatedCount == 0, "pipe shutdown reclaims all counters");
            }
        }
        private static async Task WaitCount(BoundedPipeServer server, int expected)
        {
            var watch = Stopwatch.StartNew();
            while (server.ConnectedCount != expected && watch.ElapsedMilliseconds < 500) await Task.Delay(5);
        }
        private static async Task<WireChallengeV2> BeginHandshake(Stream client, ConnectionAuthorityOwner owner, PipeLimits limits)
        {
            var id = owner.Discovery; var grant = owner.Authority.State.Find(Profile)!;
            var hello = new WireAuthenticateV2 { Kind = "authenticate", RequestId = "auth-test", EnvironmentId = id.EnvironmentId, ProfileId = Profile,
                ExpectedInstanceId = id.InstanceId, ProductVersion = id.ProductVersion, ContractVersion = id.ContractVersion, CoreManifestSha256 = id.CoreManifestSha256,
                ExpectedAuthorityEpoch = id.AuthorityEpoch, ExpectedGrantEpoch = grant.GrantEpoch, ClientNonce = new string('a', 64) };
            await FrameCodec.WriteAsync(client, ContractCodec.EncodeWireAuthenticateV2(hello), limits, CancellationToken.None);
            return ContractCodec.DecodeWireChallengeV2(ContractCatalog.ParseAndValidate(SchemaIds.WireChallengeV2, (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!));
        }
        private static async Task FinishHandshake(Stream client, WireChallengeV2 challenge, string token, PipeLimits limits)
        {
            await FrameCodec.WriteAsync(client, ContractCodec.EncodeWireProofV2(new WireProofV2 { Kind = "proof", RequestId = challenge.RequestId, ClientProof = HandshakeProof.Compute(token, "client", challenge) }), limits, CancellationToken.None);
            var accepted = ContractCodec.DecodeWireAuthenticatedV2(ContractCatalog.ParseAndValidate(SchemaIds.WireAuthenticatedV2, (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!));
            Check(accepted.InstanceId == challenge.InstanceId && accepted.GrantEpoch == challenge.GrantEpoch && accepted.AuthorityEpoch == challenge.AuthorityEpoch, "authenticated binding matches challenge");
        }
        private static Task Query(Stream client, string id, PipeLimits limits) => FrameCodec.WriteAsync(client,
            new JObject { ["kind"] = "request", ["requestId"] = id, ["capabilityId"] = "system.context", ["schemaRevision"] = "2", ["arguments"] = new JObject() }, limits, CancellationToken.None);
        private static Task QueryOperation(Stream client, string id, bool history, PipeLimits limits)
        {
            var arguments = new JObject { ["selector"] = new JObject { ["kind"] = "operationId", ["operationId"] = "missing" } };
            if (history) arguments["source"] = "history";
            return FrameCodec.WriteAsync(client, new JObject { ["kind"] = "request", ["requestId"] = id,
                ["capabilityId"] = "operations.get", ["schemaRevision"] = "5", ["arguments"] = arguments }, limits, CancellationToken.None);
        }
        private static async Task<JToken> ReadError(Stream client, PipeLimits limits) => ContractCatalog.ParseAndValidate(SchemaIds.WireError,
            (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!);
        private static async Task<ConnectionAuthorityOwner?> Open(Fixture fixture, int deadline = 5000, Func<IAuthorizationStore, IAuthorizationStore>? port = null)
        {
            var owner = await ConnectionAuthorityOwner.CreateAsync(fixture.Connection, "probe", Core, Program.ProductVersion, Program.Limits(deadline),
                new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations),
                true, persistencePort: port);
            if (owner == null) return null;
            try
            {
                var authorization = owner.Authority.State;
                if (authorization.RecordRevision == 2 && authorization.AuthorityEpoch == 1 && authorization.Profiles.Count == 0)
                    await owner.Authority.UpdateAsync(Profile, GrantScope.Read, fixture.Token, null);
                await owner.ActivateAsync();
                return owner;
            }
            catch (Exception error) { await Program.CloseFailedRootAsync(owner, error); throw; }
        }

        private static async Task CheckProcessesAsync()
        {
            Program.Stage = "cross-process-lease";
            using (var fixture = new Fixture())
            using (var first = new Child(fixture))
            {
                var ready = await first.Read(); Check((string?)ready["kind"] == "ready", "hidden owner child ready");
                string authorization = File.ReadAllText(Path.Combine(fixture.Connection, "authorization.json"));
                string discovery = File.ReadAllText(Path.Combine(fixture.Connection, "discovery.json"));
                using (var second = new Child(fixture))
                {
                    Check((string?)(await second.Read())["kind"] == "busy", "second process cannot activate");
                    await second.WaitExit();
                    Check(authorization == File.ReadAllText(Path.Combine(fixture.Connection, "authorization.json")) && discovery == File.ReadAllText(Path.Combine(fixture.Connection, "discovery.json")), "second process leaves shared records unchanged");
                }
                first.Send("revoke"); Check((string?)(await first.Read())["kind"] == "revoked", "child revocation durably acknowledged");
                first.Kill(); await first.WaitExit(false);
                using (var afterCrash = new Child(fixture))
                {
                    var next = await afterCrash.Read();
                    Check((long)next["authorityEpoch"]! > (long)ready["authorityEpoch"]! && (string?)next["instanceId"] != (string?)ready["instanceId"], "crash releases lease and advances persistent epoch");
                    var state = ContractCodec.DecodeAuthorizationSnapshotV2(ContractCatalog.ParseAndValidate(SchemaIds.AuthorizationSnapshotV2, File.ReadAllText(Path.Combine(fixture.Connection, "authorization.json"))));
                    Check(state.Profiles.Single().ScopeMask == 0, "crash restart does not restore default grant");
                    afterCrash.Send("stop"); Check((string?)(await afterCrash.Read())["kind"] == "stopped", "child shutdown report"); await afterCrash.WaitExit();
                }
            }
        }
        private sealed class CommitScheduler
        {
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private Action? callback;
            internal Task<bool> Enqueue(Func<bool> work)
            { var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); callback = () => result.SetResult(work()); Entered.SetResult(true); return result.Task; }
            internal void Run() { var work = callback ?? throw new InvalidOperationException(); callback = null; work(); }
        }
        private sealed class FragmentedStream : MemoryStream
        {
            internal FragmentedStream(byte[] bytes) : base(bytes, false) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => base.ReadAsync(buffer, offset, Math.Min(count, 3), cancellationToken);
        }
        private sealed class ControlledStore : IAuthorizationStore
        {
            private readonly IAuthorizationStore inner;
            internal bool Pause, FailBeforeSave, FailAfterSave;
            internal int SaveCalls;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal ControlledStore(IAuthorizationStore inner) { this.inner = inner; }
            public AuthorizationState Load() => inner.Load();
            public void Save(long expected, AuthorizationState replacement)
            {
                Interlocked.Increment(ref SaveCalls);
                if (Pause) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException(); }
                if (FailBeforeSave) throw new IOException("Synthetic pre-save failure.");
                inner.Save(expected, replacement);
                if (FailAfterSave) throw new IOException("Synthetic post-save acknowledgement failure.");
            }
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetFullPath("artifacts"), "runtime-fixture-" + Guid.NewGuid().ToString("N"));
            internal string Connection => Path.Combine(Root, "connection");
            internal readonly string Token = HandshakeProof.RandomHex();
            internal Fixture() { Directory.CreateDirectory(Root); }
            public void Dispose() { RemoveOwned(Root); }
            private void RemoveOwned(string path)
            {
                if (Path.GetFullPath(path) != Root && !Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture containment failed.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links.");
                foreach (string file in Directory.GetFiles(path)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links."); File.Delete(file); }
                foreach (string directory in Directory.GetDirectories(path)) RemoveOwned(directory);
                Directory.Delete(path);
            }
        }
        private sealed class Child : IDisposable
        {
            private readonly Process process;
            private readonly Task<string> errors;
            internal Child(Fixture fixture)
            {
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--serve") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.CurrentDirectory };
                process = Process.Start(start) ?? throw new InvalidOperationException(); errors = process.StandardError.ReadToEndAsync();
                Send(new JObject { ["privateDirectory"] = fixture.Connection, ["environmentId"] = "probe", ["coreManifestSha256"] = Core,
                    ["profileId"] = Profile, ["internalToken"] = fixture.Token, ["externalHttpToken"] = null, ["initialize"] = true }.ToString(Newtonsoft.Json.Formatting.None));
            }
            internal void Send(string value) { process.StandardInput.WriteLine(value); process.StandardInput.Flush(); }
            internal async Task<JObject> Read()
            {
                var line = process.StandardOutput.ReadLineAsync(); await Program.MustComplete(line);
                string? value = await line;
                if (value == null)
                {
                    await Program.MustComplete(errors);
                    foreach (string errorLine in (await errors).Split('\n'))
                        if (errorLine.StartsWith("Runtime probe failed:", StringComparison.Ordinal) || errorLine.StartsWith("Native error code:", StringComparison.Ordinal) || errorLine.StartsWith("Stage method:", StringComparison.Ordinal) || errorLine.StartsWith("IO HRESULT:", StringComparison.Ordinal) || errorLine.StartsWith("Probe encoding facts:", StringComparison.Ordinal))
                            Console.Error.WriteLine("Child " + errorLine.TrimEnd());
                    throw new IOException("Child output missing.");
                }
                return (JObject)ContractJson.Parse(value);
            }
            internal void Kill() { if (!process.HasExited) process.Kill(); }
            internal async Task WaitExit(bool success = true)
            {
                bool exited = await Task.Run(() => process.WaitForExit(5000));
                Check(exited && (!success || process.ExitCode == 0), "owned child exit");
                if (success) Check((await errors).Length == 0, "owned child has no error output");
            }
            public void Dispose() { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } process.Dispose(); }
        }
    }
}
