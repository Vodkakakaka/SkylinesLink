using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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

namespace SkylinesLink.RuntimeProbe
{
    internal static partial class RuntimeSelfTests
    {
        private static async Task CheckConnectionInitializationAsync()
        {
            await CheckBaselineContinuationAsync();
            await CheckBaselineJournalConflictsAsync();
            await CheckBaselineForwardRecoveryAsync();
            await CheckActivatedJournalLossAsync();
            await CheckProfileConfigurationRecoveryAsync();
            await CheckEnsureProfileTransitionsAsync();
        }

        private static Task<ConnectionAuthorityOwner?> CreateStorageOwner(Fixture fixture, bool initialize,
            Func<IAuthorizationStore, IAuthorizationStore>? persistencePort = null) =>
            ConnectionAuthorityOwner.CreateAsync(fixture.Connection, "probe", Core, Program.ProductVersion, Program.Limits(),
                new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations),
                initialize, persistencePort: persistencePort);

        private static async Task SeedBaselineAsync(Fixture fixture, string journalState)
        {
            var boundary = new PrivateConnectionStorage(fixture.Connection);
            using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Baseline lease missing."))
            {
                new AtomicAuthorizationStore(boundary, lease, "probe").InitializeEmpty();
                if (journalState == "missing") return;
                var sealFailure = journalState == "forward" ? new BaselineSealAcknowledgementLoss() : null;
                var journal = OperationJournal.OpenForAuthority(boundary, lease, "probe", true, sealFailure);
                try
                {
                    if (journalState == "business" || journalState == "retained" || journalState == "forward")
                    {
                        var source = new WorldContextTracker(Guid.NewGuid().ToString("N")).Read();
                        var target = new LoadTarget("probe", "baseline-conflict", "v1");
                        var intent = new OperationIntent("baseline-business", source.InstanceId, new SubmissionKey("baseline-key", "request"),
                            OperationCommandHash.Compute(source, target), Profile, 1, 1, source, target);
                        using (var reservation = journal.TryReserveOperation() ?? throw new InvalidOperationException("Baseline operation credit missing."))
                        {
                            await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                            await reservation.AppendAsync(new OperationFinished(intent.OperationId, "cancelled", false, SubmissionKnowledge.NotSubmitted,
                                "SYNTHETIC_BASELINE", new PersistedLoadFacts(NativeLoadTaskState.NotStarted, null, LoadTargetMatch.Unknown,
                                    false, false, LoadObservationEnd.OwnerStopped, null)), JournalSlot.Terminal);
                        }
                        if (journalState == "retained" || journalState == "forward")
                        {
                            var prefix = journal.CaptureConfirmedPrefix() ?? throw new InvalidOperationException("Baseline prefix missing.");
                            sealFailure?.Arm();
                            var rotation = journal.TryStartRotation(new JournalRotationPlan(((StoreOpened)prefix.Records[0].Entry).StoreId,
                                prefix.ThroughSequence, Array.Empty<string>())) ?? throw new InvalidOperationException("Baseline rotation missing.");
                            if (sealFailure != null)
                                await RejectAsync<JournalPersistenceException>(() => Program.MustComplete(rotation.Completion),
                                    "the baseline fixture loses acknowledgement after its actual fourth maintenance Flush seals the source");
                            else
                            {
                                await Program.MustComplete(rotation.Completion);
                                var result = await rotation.Completion;
                                Check(result.Outcome == JournalRotationOutcome.Installed && result.RetentionApplied && result.Prefix.Records.Count == 1,
                                    "the contradictory baseline fixture has actually retired its business chain into a one-header retained journal");
                            }
                        }
                    }
                }
                finally { await journal.StopAsync(); }
                if (journalState == "corrupt") File.WriteAllBytes(Path.Combine(fixture.Connection, "operations.journal"), new byte[] { 1, 2, 3 });
            }
        }

        private static async Task CheckBaselineContinuationAsync()
        {
            Program.Stage = "explicit unactivated baseline continuation";
            foreach (string journalState in new[] { "missing", "empty" })
            using (var fixture = new Fixture())
            {
                await SeedBaselineAsync(fixture, journalState);
                string authorization = Path.Combine(fixture.Connection, "authorization.json"), journal = Path.Combine(fixture.Connection, "operations.journal");
                byte[] originalAuthorization = File.ReadAllBytes(authorization);
                byte[]? originalJournal = File.Exists(journal) ? File.ReadAllBytes(journal) : null;
                string[] files = Directory.GetFiles(fixture.Connection).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                await RejectAsync<InvalidOperationException>(async () =>
                {
                    var unexpected = await CreateStorageOwner(fixture, false);
                    if (unexpected != null) await unexpected.CloseAsync();
                }, "an unactivated baseline does not resume without an explicit initialization request");
                Check(originalAuthorization.SequenceEqual(File.ReadAllBytes(authorization)) &&
                    files.SequenceEqual(Directory.GetFiles(fixture.Connection).OrderBy(path => path, StringComparer.Ordinal)) &&
                    (originalJournal == null ? !File.Exists(journal) : originalJournal.SequenceEqual(File.ReadAllBytes(journal))),
                    "refusing implicit baseline initialization leaves authorization, journal and file set unchanged");
                var owner = await CreateStorageOwner(fixture, true) ?? throw new InvalidOperationException("Explicit baseline owner missing.");
                try
                {
                    Check(owner.Authority.State.RecordRevision == 2 && owner.Authority.State.AuthorityEpoch == 1 &&
                        owner.Authority.State.Profiles.Count == 0 && owner.Authority.Read(Profile) == null && owner.Operations.IsRecoveryReady,
                        "explicit baseline completion establishes a healthy journal before active storage and creates no profile");
                    await owner.ActivateAsync();
                }
                finally { await owner.CloseAsync(); }
                Check(File.Exists(journal) && (originalJournal == null || originalJournal.SequenceEqual(File.ReadAllBytes(journal))),
                    "explicit initialization creates only the missing journal and preserves an existing complete empty business journal");
            }
            using (var fixture = new Fixture())
            {
                _ = new PrivateConnectionStorage(fixture.Connection);
                await RejectAsync<InvalidOperationException>(async () =>
                {
                    var unexpected = await CreateStorageOwner(fixture, true);
                    if (unexpected != null) await unexpected.CloseAsync();
                }, "an existing private directory without authorization is not a fresh baseline");
                Check(!File.Exists(Path.Combine(fixture.Connection, "authorization.json")) && !File.Exists(Path.Combine(fixture.Connection, "operations.journal")),
                    "explicit initialization does not fill missing authorization in an existing directory");
            }
        }

        private static async Task CheckBaselineJournalConflictsAsync()
        {
            Program.Stage = "unactivated authorization cannot conceal business history";
            foreach (string journalState in new[] { "business", "retained", "corrupt" })
            using (var fixture = new Fixture())
            {
                await SeedBaselineAsync(fixture, journalState);
                string authorization = Path.Combine(fixture.Connection, "authorization.json"), journal = Path.Combine(fixture.Connection, "operations.journal");
                byte[] originalAuthorization = File.ReadAllBytes(authorization), originalJournal = File.ReadAllBytes(journal);
                await RejectAsync<InvalidDataException>(async () =>
                {
                    var unexpected = await CreateStorageOwner(fixture, true);
                    if (unexpected != null) await unexpected.CloseAsync();
                }, "business, retention coverage or corruption contradicts an unactivated empty authorization baseline");
                Check(originalAuthorization.SequenceEqual(File.ReadAllBytes(authorization)) && originalJournal.SequenceEqual(File.ReadAllBytes(journal)) &&
                    !File.Exists(Path.Combine(fixture.Connection, "discovery.json")),
                    "baseline contradiction preserves the exact canonical authorization and journal without publishing a connection");
            }
        }

        private static async Task CheckBaselineForwardRecoveryAsync()
        {
            Program.Stage = "sealed baseline recovery precedes initialization eligibility";
            using (var fixture = new Fixture())
            {
                await SeedBaselineAsync(fixture, "forward");
                string authorization = Path.Combine(fixture.Connection, "authorization.json"), journalPath = Path.Combine(fixture.Connection, "operations.journal");
                byte[] originalAuthorization = File.ReadAllBytes(authorization), sealedJournal = File.ReadAllBytes(journalPath);
                string successor;
                using (var file = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var selected = JournalBankReader.Read(file, "probe");
                    Check(selected.Kind == JournalBankSelectionKind.ForwardPrepared && selected.Current.Seal != null &&
                        selected.Current.Records.Count == 3 && selected.Candidate!.Activation == null && selected.Candidate.Records.Count == 1,
                        "the fixture leaves the real accepted and cancelled-false chain sealed beside its unactivated one-header retained successor");
                    successor = selected.Candidate!.Descriptor.StoreId;
                }
                await RejectAsync<InvalidDataException>(async () =>
                {
                    var unexpected = await CreateStorageOwner(fixture, true);
                    if (unexpected != null) await unexpected.CloseAsync();
                }, "normal sealed-successor recovery does not authorize initialization of a retained baseline");
                Check(originalAuthorization.SequenceEqual(File.ReadAllBytes(authorization)) &&
                    !File.Exists(Path.Combine(fixture.Connection, "discovery.json")) && !sealedJournal.SequenceEqual(File.ReadAllBytes(journalPath)),
                    "refused initialization preserves authorization and publishes no connection while normal recovery activates the sealed successor");
                var boundary = new PrivateConnectionStorage(fixture.Connection);
                using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Refused baseline retained its lease."))
                {
                    var state = new AtomicAuthorizationStore(boundary, lease, "probe").Load();
                    Check(state.RecordRevision == 1 && state.AuthorityEpoch == 0 && state.Profiles.Count == 0,
                        "the released real lease exposes the original unactivated authorization with no granted profile");
                    using (var file = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var selected = JournalBankReader.Read(file, "probe");
                        Check(selected.Kind == JournalBankSelectionKind.Active && selected.Current.Descriptor.StoreId == successor,
                            "the refused factory has already completed the existing sealed successor rather than leaving recovery to the next reader");
                    }
                    var journal = OperationJournal.OpenForAuthority(boundary, lease, "probe", false);
                    try
                    {
                        var prefix = journal.CaptureConfirmedPrefix();
                        Check(journal.IsHealthy && journal.Recovery.Complete && journal.Recovery.DurableConfirmed && journal.Recovery.RetentionApplied &&
                            prefix != null && prefix.Records.Count == 1 && prefix.Records[0].Sequence == 1 &&
                            prefix.Records[0].Entry is StoreOpened opened && opened.StoreId == successor,
                            "a fresh normal reader confirms retained coverage and only the successor StoreOpened without inventing a new empty journal");
                    }
                    finally { await journal.StopAsync(); }
                }
            }
        }

        private sealed class BaselineSealAcknowledgementLoss : IJournalDurability
        {
            private int calls, failAt;
            internal void Arm() => Volatile.Write(ref failAt, Volatile.Read(ref calls) + 4);
            public void Flush(FileStream file)
            {
                int call = Interlocked.Increment(ref calls);
                file.Flush(true);
                if (call == Volatile.Read(ref failAt)) throw new IOException("Synthetic lost acknowledgement after actual source seal Flush.");
            }
        }

        private static async Task CheckActivatedJournalLossAsync()
        {
            Program.Stage = "activated storage never replaces lost business facts with an empty journal";
            foreach (bool missing in new[] { true, false })
            using (var fixture = new Fixture())
            {
                var original = await CreateStorageOwner(fixture, true) ?? throw new InvalidOperationException("Activated fixture owner missing.");
                string incarnation;
                try
                {
                    await original.Authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Control, false, HandshakeProof.RandomHex);
                    incarnation = original.Authority.State.Find(Profile)!.ProfileInstanceId;
                    await original.ActivateAsync();
                }
                finally { await original.CloseAsync(); }
                string journal = Path.Combine(fixture.Connection, "operations.journal");
                byte[] corruption = new byte[] { 4, 5, 6 };
                if (missing) File.Delete(journal); else File.WriteAllBytes(journal, corruption);
                var owner = await CreateStorageOwner(fixture, true) ?? throw new InvalidOperationException("Read-only recovery owner missing.");
                try
                {
                    await owner.ActivateAsync();
                    var subject = owner.Authority.Read(Profile)!;
                    var context = new SnapshotCapabilityDispatcher(owner.Context, Program.ProductVersion, owner.Operations).Execute("system.context", "2", new JObject(), subject);
                    Check(owner.Authority.Allows(subject, GrantScope.Read | GrantScope.Control) && owner.Authority.State.Find(Profile)!.ProfileInstanceId == incarnation &&
                        (string?)context["gameState"] == "menu" && owner.Operations.Lookup("not-covered", subject).Kind == "unavailable",
                        "current context and stored authorization remain readable while missing journal coverage stays explicitly unavailable");
                    bool refused = false;
                    try { owner.Operations.IssueWriteContext(subject); }
                    catch (OperationRejectedException error) { refused = error.Code == "JOURNAL_UNAVAILABLE"; }
                    Check(refused, "an activated store with missing or damaged journal cannot issue a new write context");
                }
                finally { await owner.CloseAsync(); }
                Check(missing ? !File.Exists(journal) : corruption.SequenceEqual(File.ReadAllBytes(journal)),
                    "even explicit initialization cannot repair an already activated missing or damaged journal into empty business history");
            }
        }

        private static async Task CheckProfileConfigurationRecoveryAsync()
        {
            Program.Stage = "first profile write failure and acknowledgement loss recover by canonical facts";
            foreach (bool savedBeforeFailure in new[] { false, true })
            using (var fixture = new Fixture())
            {
                ControlledStore? persistence = null;
                int generated = 0;
                Func<string> credential = () => { generated++; return HandshakeProof.RandomHex(); };
                var failed = await CreateStorageOwner(fixture, true, store => persistence = new ControlledStore(store)
                    { FailBeforeSave = !savedBeforeFailure, FailAfterSave = savedBeforeFailure }) ?? throw new InvalidOperationException("Configuration fixture owner missing.");
                var controlled = persistence ?? throw new InvalidOperationException("Configuration persistence port missing.");
                try
                {
                    await RejectAsync<IOException>(() => failed.Authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Control, false, credential),
                        "first explicit configuration surfaces a real before-save or post-save acknowledgement failure");
                    Check(failed.Authority.Read(Profile) == null && generated == 1 && controlled.SaveCalls == 1,
                        "failed configuration keeps its target unavailable instead of trusting either possible persistence outcome");
                    await RejectAsync<InvalidOperationException>(() => failed.Authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Control, false, credential),
                        "a failed writer cannot retry or generate another credential in the same lifetime");
                    var canonical = (JObject)ContractJson.Parse(File.ReadAllText(Path.Combine(fixture.Connection, "authorization.json")));
                    Check(((JArray)canonical["profiles"]!).Count == (savedBeforeFailure ? 1 : 0) && generated == 1 && controlled.SaveCalls == 1,
                        "canonical disk facts distinguish failed-before-write from an actual saved profile whose acknowledgement was lost");
                }
                finally { await failed.CloseAsync(); }
                var recovered = await CreateStorageOwner(fixture, false) ?? throw new InvalidOperationException("Configuration recovery owner missing.");
                try
                {
                    Check((recovered.Authority.Read(Profile) != null) == savedBeforeFailure,
                        "ordinary restart reads the canonical profile and never invents the missing first configuration");
                    await recovered.Authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Control, false, credential);
                    var configured = recovered.Authority.State.Find(Profile)!;
                    Check(generated == (savedBeforeFailure ? 1 : 2), "explicit retry preserves a saved matching credential and generates only when canonical configuration is missing");
                    await recovered.ActivateAsync();
                    var limits = Program.Limits();
                    using (var client = new NamedPipeClientStream(".", recovered.Discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        await client.ConnectAsync(2000);
                        var challenge = await BeginHandshake(client, recovered, limits);
                        Check(HandshakeProof.EqualsProof(challenge.ServerProof, HandshakeProof.Compute(configured.InternalToken, "server", challenge)),
                            "recovered configuration proves possession of the actual canonical token over the real pipe");
                        await FinishHandshake(client, challenge, configured.InternalToken, limits);
                        await Query(client, "configuration-retry-read", limits);
                        var result = ContractCatalog.ParseAndValidate(SchemaIds.WireResult,
                            (await FrameCodec.ReadAsync(client, limits, limits.FrameTimeout, CancellationToken.None))!, new RegisteredBinding("system.context", "2"));
                        Check((string?)result["result"]?["gameState"] == "menu", "explicit first-configuration retry authenticates and reads through the real connection");
                    }
                    var subject = recovered.Authority.Read(Profile)!;
                    string context = recovered.Operations.IssueWriteContext(subject);
                    var accepted = await recovered.Operations.SubmitAsync(new SubmissionKey(context, "configuration-retry"), new LoadTarget("probe", "save", "v1"), subject);
                    Check(accepted.Kind == "found" && accepted.Durability == "durable" && accepted.OperationId != null && !accepted.MayHaveApplied,
                        "recovered first configuration can issue and durably admit an operation without invoking native work");
                }
                finally { await recovered.CloseAsync(); }
            }
        }

        private static async Task CheckEnsureProfileTransitionsAsync()
        {
            Program.Stage = "explicit profile configuration is idempotent and ordinary recovery grants nothing";
            using (var fixture = new Fixture())
            {
                int generated = 0;
                Func<string> credential = () => { generated++; return HandshakeProof.RandomHex(); };
                ControlledStore? persistence = null;
                var owner = await CreateStorageOwner(fixture, true, store => persistence = new ControlledStore(store))
                    ?? throw new InvalidOperationException("Ensure fixture owner missing.");
                var controlled = persistence ?? throw new InvalidOperationException("Ensure persistence port missing.");
                try
                {
                    await owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, false, credential);
                    var configured = owner.Authority.State; var originalProfile = configured.Find(Profile)!;
                    byte[] originalBytes = File.ReadAllBytes(Path.Combine(fixture.Connection, "authorization.json"));
                    await owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, false, () => throw new InvalidOperationException("Matching configuration must not request credentials."));
                    Check(ReferenceEquals(configured, owner.Authority.State) && generated == 1 && controlled.SaveCalls == 1 &&
                        originalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Connection, "authorization.json"))),
                        "matching explicit configuration preserves identity, tokens, epochs and bytes without another credential or Save");
                    await RejectAsync<ArgumentException>(() => owner.Authority.EnsureProfileAsync(Profile, GrantScope.None, false, credential),
                        "Ensure rejects an empty scope rather than turning setup into revocation");
                    Check(ReferenceEquals(configured, owner.Authority.State) && generated == 1 && controlled.SaveCalls == 1,
                        "invalid Ensure scope does not request a credential or persist a change");
                    controlled.Pause = true;
                    Task changingScope = owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Control, false, credential);
                    try
                    {
                        await Program.MustComplete(controlled.Entered.Task);
                        await RejectAsync<InvalidOperationException>(() => owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, false, credential),
                            "a busy writer cannot report matching old configuration as successfully ensured");
                    }
                    finally { controlled.Release.Set(); }
                    await changingScope; controlled.Pause = false;
                    var changed = owner.Authority.State.Find(Profile)!;
                    Check(generated == 1 && changed.Scope == (GrantScope.Read | GrantScope.Control) && changed.GrantEpoch > originalProfile.GrantEpoch &&
                        changed.ProfileInstanceId == originalProfile.ProfileInstanceId && changed.InternalToken == originalProfile.InternalToken,
                        "explicit scope changes advance grant facts while preserving the existing credential and profile incarnation");
                    await owner.Authority.EnsureProfileAsync(Profile, changed.Scope, true, credential);
                    Check(generated == 2 && owner.Authority.State.Find(Profile)!.ExternalHttpToken != null &&
                        owner.Authority.State.Find(Profile)!.InternalToken == originalProfile.InternalToken,
                        "explicit HTTP enablement creates only the missing external credential and preserves the internal token");
                    var enabled = owner.Authority.State.Find(Profile)!;
                    await owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, true, credential);
                    var http = owner.Authority.State;
                    Check(generated == 2 && http.Find(Profile)!.Scope == GrantScope.Read && http.Find(Profile)!.InternalToken == enabled.InternalToken &&
                        http.Find(Profile)!.ExternalHttpToken == enabled.ExternalHttpToken && http.Find(Profile)!.GrantEpoch > enabled.GrantEpoch,
                        "scope changes with HTTP enabled preserve both existing credentials while advancing the grant");
                    await owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, true, () => throw new InvalidOperationException("Matching HTTP configuration must not request credentials."));
                    Check(ReferenceEquals(http, owner.Authority.State) && generated == 2 && controlled.SaveCalls == 4,
                        "matching HTTP configuration also preserves the existing grant and credentials");
                    await owner.Authority.EnsureProfileAsync(Profile, GrantScope.Read, false, credential);
                    Check(generated == 2 && owner.Authority.State.Find(Profile)!.ExternalHttpToken == null &&
                        owner.Authority.State.Find(Profile)!.InternalToken == originalProfile.InternalToken,
                        "explicit HTTP disablement clears only the external credential without rotating the internal token");
                    await owner.Authority.RemoveAsync(Profile);
                    Check(owner.Authority.Read(Profile) == null && owner.Authority.State.Find(Profile) == null, "explicit removal deletes the stored profile");
                    await owner.Authority.EnsureProfileAsync(Profile, changed.Scope, false, credential);
                    var recreated = owner.Authority.State.Find(Profile)!;
                    Check(generated == 3 && recreated.ProfileInstanceId != originalProfile.ProfileInstanceId,
                        "only a new explicit configuration after removal creates a new profile incarnation");
                    await owner.Authority.UpdateAsync(Profile, GrantScope.None, recreated.InternalToken, null);
                    await owner.CloseAsync();
                    owner = await CreateStorageOwner(fixture, false) ?? throw new InvalidOperationException("Revoked profile recovery owner missing.");
                    Check(owner.Authority.Read(Profile) == null && owner.Authority.State.Find(Profile)!.Scope == GrantScope.None &&
                        owner.Authority.State.Find(Profile)!.ProfileInstanceId == recreated.ProfileInstanceId && generated == 3,
                        "ordinary restart preserves None scope and the actual stored incarnation without regranting");
                    await owner.Authority.RemoveAsync(Profile);
                    await owner.CloseAsync();
                    owner = await CreateStorageOwner(fixture, false) ?? throw new InvalidOperationException("Removed profile recovery owner missing.");
                    Check(owner.Authority.Read(Profile) == null && owner.Authority.State.Find(Profile) == null && generated == 3,
                        "ordinary restart preserves explicit profile removal instead of restoring a default connection");
                }
                finally { await owner.CloseAsync(); }
            }
        }
    }
}
