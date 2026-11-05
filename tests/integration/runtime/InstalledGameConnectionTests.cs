using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class InstalledGameConnectionTests
    {
        private const string Profile = "probe";
        private static int checks;
        private static void Check(bool condition, string message) { Program.Assert(condition, message); checks++; }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await TrustedStartupAndExplicitConfiguration();
            await BusyRetryAndStopDrain();
            return checks;
        }

        private static async Task TrustedStartupAndExplicitConfiguration()
        {
            Program.Stage = "installed connection uses the bound cohort and explicit authorization only";
            using (var fixture = new InstalledCoreBindingTests.Fixture())
            {
                InstalledGameConnection? root = null;
                try
                {
                    var wrong = fixture.Components.ToArray();
                    var first = wrong[0];
                    wrong[0] = new LoadedManagedComponent(first.Name, Guid.NewGuid(), first.AssemblyVersion, first.InformationalVersion);
                    root = new InstalledGameConnection(fixture.EnvironmentId, wrong, Profile);
                    Check(!await root.StartAsync() && root.Read().FailureCode == "INITIALIZATION_FAILED" &&
                        !Directory.Exists(Connection(fixture)), "a mismatched loaded cohort cannot initialize connection storage");
                    Check(await root.CloseAsync(), "a rejected cohort has no remaining connection lifetime");
                    fixture.CheckReleased();

                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components);
                    Check(!await root.StartAsync() && !File.Exists(Path.Combine(Connection(fixture), "authorization.json")),
                        "ordinary startup does not create default authorization when no profile was explicitly configured");
                    Check(await root.CloseAsync(), "missing authorization closes its attempted Core binding");
                    fixture.CheckReleased();

                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, Profile, true);
                    long generation = root.Context.EnterLoading();
                    Check(root.Context.ObserveReady(generation), "native loaded scope may arrive before explicit connection setup");
                    Check(await root.StartAsync() && root.Read().Phase == GameConnectionPhase.Active,
                        "explicit profile setup activates the actually bound installed Core");
                    var discovery = ContractCodec.DecodeEnvironmentDiscovery(JObject.Parse(File.ReadAllText(Path.Combine(Connection(fixture), "discovery.json"))));
                    var original = ReadAuthorization(fixture);
                    var profile = original.Profiles.Single();
                    Check(root.ProductVersion == InstalledCoreBindingTests.Fixture.Release && discovery.ProductVersion == root.ProductVersion &&
                        discovery.CoreManifestSha256 == fixture.ManifestHash && discovery.InstanceId == root.Context.Read().InstanceId &&
                        profile.ProfileId == Profile && profile.ScopeMask == (long)(GrantScope.Read | GrantScope.Write | GrantScope.Control),
                        "published discovery and explicit permissions come from the verified Core and requested setup");
                    byte[] authorization = File.ReadAllBytes(Path.Combine(Connection(fixture), "authorization.json"));
                    byte[] published = File.ReadAllBytes(Path.Combine(Connection(fixture), "discovery.json"));
                    await root.ConfigureAsync(Profile, true);
                    var unchanged = ReadAuthorization(fixture);
                    Check(unchanged.AuthorityEpoch == original.AuthorityEpoch && unchanged.RecordRevision == original.RecordRevision &&
                        unchanged.Profiles.Single().InternalToken == profile.InternalToken && authorization.SequenceEqual(File.ReadAllBytes(Path.Combine(Connection(fixture), "authorization.json"))) &&
                        published.SequenceEqual(File.ReadAllBytes(Path.Combine(Connection(fixture), "discovery.json"))),
                        "equal active configuration keeps the actual authority, token and published connection unchanged");
                    Check(await root.CloseAsync(), "configured installed connection closes completely");
                    fixture.CheckReleased();

                    var boundary = new PrivateConnectionStorage(Connection(fixture));
                    string httpToken;
                    using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Transport configuration lease unavailable."))
                    {
                        var store = new AtomicAuthorizationStore(boundary, lease, fixture.EnvironmentId);
                        var authority = new GrantAuthority(store.Load(), store);
                        try { await authority.EnsureProfileAsync(Profile, GrantScope.Read | GrantScope.Write | GrantScope.Control, true, HandshakeProof.RandomHex); }
                        finally { await authority.StopAsync(); }
                        httpToken = ReadAuthorization(fixture).Profiles.Single().ExternalHttpToken!;
                    }
                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components);
                    Check(await root.StartAsync(), "an existing transport configuration opens without a settings rewrite");
                    var beforeScopeChange = ReadAuthorization(fixture);
                    await root.ConfigureAsync(Profile, false);
                    var afterScopeChange = ReadAuthorization(fixture);
                    Check(afterScopeChange.AuthorityEpoch == beforeScopeChange.AuthorityEpoch &&
                        afterScopeChange.Profiles.Single().ScopeMask == (long)GrantScope.Read &&
                        afterScopeChange.Profiles.Single().InternalToken == profile.InternalToken &&
                        afterScopeChange.Profiles.Single().ExternalHttpToken == httpToken,
                        "applying city permissions preserves the existing HTTP transport credential it did not edit");
                    Check(await root.CloseAsync(), "the scoped configuration closes before independent explicit revocation");
                    using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Revocation lease unavailable."))
                    {
                        var store = new AtomicAuthorizationStore(boundary, lease, fixture.EnvironmentId);
                        var authority = new GrantAuthority(store.Load(), store);
                        try { await authority.UpdateAsync(Profile, GrantScope.None, profile.InternalToken, httpToken); }
                        finally { await authority.StopAsync(); }
                    }
                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components);
                    Check(await root.StartAsync(), "ordinary restart opens already configured storage without initial profile setup");
                    var restarted = ReadAuthorization(fixture);
                    Check(restarted.AuthorityEpoch == original.AuthorityEpoch + 2 && restarted.Profiles.Single().ScopeMask == 0 &&
                        restarted.Profiles.Single().ProfileInstanceId == profile.ProfileInstanceId && restarted.Profiles.Single().InternalToken == profile.InternalToken &&
                        restarted.Profiles.Single().ExternalHttpToken == httpToken,
                        "ordinary restart advances authority lifetime while preserving the explicit revocation and profile identity");
                }
                finally
                {
                    if (root != null) Check(await root.CloseAsync(), "installed startup fixture releases its actual lifetime");
                    RemoveConnection(fixture);
                }
            }
        }

        private static async Task BusyRetryAndStopDrain()
        {
            Program.Stage = "installed busy attempt releases pins and stop joins claimed module work";
            using (var fixture = new InstalledCoreBindingTests.Fixture())
            {
                InstalledGameConnection? root = null;
                try
                {
                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, Profile);
                    Check(await root.StartAsync() && await root.CloseAsync(), "busy retry fixture first establishes ordinary configured storage");
                    root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components);
                    long generation = root.Context.EnterLoading(); root.Context.ObserveReady(generation);
                    var ready = root.Context.Read();
                    var boundary = new PrivateConnectionStorage(Connection(fixture));
                    using (var lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority() ?? throw new InvalidOperationException("Busy fixture lease unavailable."))
                    {
                        Check(!await root.StartAsync() && root.Read().FailureCode == "ENVIRONMENT_BUSY" && !root.Context.IsStopped &&
                            ReferenceEquals(ready, root.Context.Read()), "a busy attempt preserves the same root and native loaded scope for retry");
                        fixture.CheckReleased();
                        Check(true, "a rejected authority attempt has actually released every Core cohort file pin");
                    }
                    Check(await root.StartAsync() && root.Read().Phase == GameConnectionPhase.Active && ReferenceEquals(ready, root.Context.Read()),
                        "the same installed root activates after the real conflicting authority lease is released");
                    using (var capture = root.Modules.Capture(ReadAuthorization(fixture).Profiles.Single().ProfileInstanceId))
                    {
                        Check(root.Modules.TryClaim(out var work), "the installed root owns an actual claimed module capture");
                        Task<bool> close = root.CloseAsync();
                        try
                        {
                            Check(root.Context.IsStopped && !root.Context.ObserveReady(generation) && !root.Context.ObserveCityIdentity(generation, "late-city") &&
                                !root.Modules.WhenIdle.IsCompleted && !close.IsCompleted,
                                "stop rejects late native context observations and cannot complete while claimed module work remains");
                            bool pinned = false;
                            try { using (File.Open(fixture.ActivationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                            catch (IOException) { pinned = true; }
                            Check(pinned, "Core activation stays pinned until the installed module producer has really ended");
                        }
                        finally { root.Modules.Fail(work!, "CAPTURE_FAILED"); }
                        await Program.MustComplete(close);
                        Check(await close && root.Modules.WhenIdle.IsCompleted && root.Read().FullyReleased,
                            "closing joins actual module termination and then releases the connection authority and Core pins");
                    }
                    fixture.CheckReleased();
                }
                finally
                {
                    if (root != null) Check(await root.CloseAsync(), "installed busy fixture releases its actual lifetime");
                    RemoveConnection(fixture);
                }
            }
        }

        private static string Connection(InstalledCoreBindingTests.Fixture fixture) => Path.Combine(Path.GetDirectoryName(fixture.ActivationPath)!, "connection");
        private static AuthorizationSnapshotV2 ReadAuthorization(InstalledCoreBindingTests.Fixture fixture) =>
            ContractCodec.DecodeAuthorizationSnapshotV2(ContractCatalog.ParseAndValidate(SchemaIds.AuthorizationSnapshotV2,
                File.ReadAllText(Path.Combine(Connection(fixture), "authorization.json"))));

        internal static void RemoveConnection(InstalledCoreBindingTests.Fixture fixture)
        {
            string connection = Connection(fixture), root = Path.GetDirectoryName(connection)!;
            if (Path.GetFileName(root) != fixture.EnvironmentId || File.ReadAllText(Path.Combine(root, ".owned-probe")) != fixture.EnvironmentId)
                throw new IOException("Connection fixture cleanup identity mismatch.");
            if (!Directory.Exists(connection)) return;
            if ((File.GetAttributes(connection) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(connection).Length != 0)
                throw new IOException("Connection fixture cleanup refuses links or unexpected directories.");
            foreach (string file in Directory.GetFiles(connection))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Connection fixture cleanup refuses links.");
                File.Delete(file);
            }
            Directory.Delete(connection);
        }
    }
}
