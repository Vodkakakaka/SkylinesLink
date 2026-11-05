using System;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class AutomaticCorePreparationTests
    {
        private static int checks;
        internal static bool DanglingLinkValidated { get; private set; }
        private static void Check(bool condition, string message) { Program.Assert(condition, message); checks++; }
        private static ModBundleOrigin Origin(InstalledCoreBindingTests.Fixture fixture) =>
            new ModBundleOrigin(fixture.ModPath, false, fixture.Components.Single(item => item.Name == "SkylinesLink.Mod").ModuleId);

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            Program.Stage = "precise missing activation and ordinary automatic preparation";
            MissingRecordBoundary();
            DanglingActivationLink();
            await ExistingInstallationDoesNotPrepare();
            await MissingActivationExecutesOnce();
            await StopDuringPreparation();
            await PublishedInstallation(explicitProfile: false, stopBeforeRelease: false);
            await PublishedInstallation(explicitProfile: true, stopBeforeRelease: false);
            await PublishedInstallation(explicitProfile: true, stopBeforeRelease: true);
            return checks;
        }

        private static void DanglingActivationLink()
        {
            DanglingLinkValidated = false;
            using (var fixture = new InstalledCoreBindingTests.Fixture())
            {
                File.Delete(fixture.ActivationPath);
                string target = Path.Combine(Path.GetDirectoryName(fixture.ActivationPath)!, ".missing-link-target");
                bool created = CreateSymbolicLinkW(fixture.ActivationPath, target, 2);
                int error = created ? 0 : Marshal.GetLastWin32Error();
                if (!created && (error == 87 || error == 1314))
                {
                    created = CreateSymbolicLinkW(fixture.ActivationPath, target, 0);
                    error = created ? 0 : Marshal.GetLastWin32Error();
                }
                // Ordinary-user runs can lack this optional fixture privilege.
                // The result explicitly records whether this real path was tested.
                if (!created && error == 1314) return;
                if (!created) throw new Win32Exception(error);
                try
                {
                    bool rejected = false;
                    try { using (InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)) { } }
                    catch (MissingCoreActivationException) { throw new InvalidOperationException("A dangling activation link was classified as absent."); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "a real dangling activation symlink is blocked instead of triggering installation");
                    DanglingLinkValidated = true;
                }
                finally { File.Delete(fixture.ActivationPath); }
                fixture.CheckReleased();
            }
        }

        private static void MissingRecordBoundary()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                File.Delete(fixture.ActivationPath);
                bool missing = false;
                try { using (InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)) { } }
                catch (MissingCoreActivationException) { missing = true; }
                Check(missing, "an absent activation leaf is the explicit automatic-preparation boundary");
                fixture.CheckReleased();
                fixture.Publish();
                File.Delete(fixture.ModPath);
                bool payloadFailure = false;
                try { using (InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)) { } }
                catch (MissingCoreActivationException) { throw new InvalidOperationException("Missing payload was classified as missing activation."); }
                catch (IOException) { payloadFailure = true; }
                Check(payloadFailure, "a missing payload never becomes an activation absence");
                fixture.CheckReleased();

                string environment = "probe-path-" + Guid.NewGuid().ToString("N").Substring(0, 20);
                string blocked = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fixture.ActivationPath))!, environment);
                Check(!File.Exists(blocked) && !Directory.Exists(blocked), "the separate path-boundary fixture starts absent");
                missing = false;
                try { using (InstalledCoreBinding.Open(environment, fixture.Components)) { } }
                catch (MissingCoreActivationException) { missing = true; }
                Check(missing && !Directory.Exists(blocked), "a genuinely absent environment is classified without creating it");
                File.WriteAllText(blocked, environment);
                try
                {
                    bool rejected = false;
                    try { using (InstalledCoreBinding.Open(environment, fixture.Components)) { } }
                    catch (MissingCoreActivationException) { throw new InvalidOperationException("A non-directory ancestor was classified as missing."); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "error3 caused by a regular-file ancestor is a blocked path, not first installation");
                }
                finally
                {
                    if ((File.GetAttributes(blocked) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(blocked) != environment)
                        throw new IOException("The exact path-boundary fixture changed identity.");
                    File.Delete(blocked);
                }
            }
        }

        private static async Task ExistingInstallationDoesNotPrepare()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                // Deliberately unusable source metadata must not obstruct the
                // already verified installed cohort or create a helper attempt.
                var unavailable = new ModBundleOrigin(fixture.ModPath, true, Guid.Empty);
                var root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, bundledOrigin: unavailable);
                try
                {
                    Check(!await root.StartAsync() && root.ReadPreparation() == null && root.ProductVersion != null,
                        "unconfigured authorization is not treated as first installation even with unusable bundle metadata");
                    Check(!Directory.Exists(Connection(fixture)), "normal startup does not create authorization or replace installation files");
                }
                finally { Check(await root.CloseAsync(), "the failed unconfigured connection really closes"); }
                fixture.CheckReleased();

                File.WriteAllText(fixture.ActivationPath, "{}");
                root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, "probe", bundledOrigin: Origin(fixture));
                try
                {
                    Check(!await root.StartAsync() && root.ReadPreparation() == null && !Directory.Exists(Connection(fixture)),
                        "corrupt activation never starts bundled recovery or explicit grant creation");
                }
                finally { Check(await root.CloseAsync(), "bad-record connection closes without a helper"); }
                fixture.CheckReleased();
            }
        }

        private static async Task MissingActivationExecutesOnce()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                SetupInitializationTests.PrepareChild(fixture, "empty");
                File.Delete(fixture.ActivationPath);
                var root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, "probe", true, Origin(fixture));
                try
                {
                    Check(!await root.StartAsync(), "a helper's zero exit cannot substitute for a published installation");
                    var snapshot = root.ReadPreparation();
                    var result = root.PreparationResult;
                    Check(snapshot != null && snapshot.ProcessId != 0 && snapshot.ChildExited && snapshot.ReadersJoined,
                        "the ordinary factory executes the actual bundled helper and waits for release");
                    Check(result != null && result.ExitCode == 0 && result.Installation == PreparedInstallationState.Unavailable && !result.Ready,
                        "the ordinary factory retains the actual failed installation readback");
                    Check(!File.Exists(fixture.ActivationPath) && !Directory.Exists(Connection(fixture)),
                        "a failed one-shot preparation does not fabricate activation or continue into explicit grant setup");
                    uint pid = snapshot!.ProcessId;
                    Check(!await root.StartAsync() && root.ReadPreparation()!.ProcessId == pid,
                        "retrying a consumed lifetime cannot spawn a second automatic helper");
                }
                finally { Check(await root.CloseAsync(), "failed automatic preparation closes the real factory lifetime"); }
                fixture.CheckReleased();
            }
        }

        private static async Task StopDuringPreparation()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                SetupInitializationTests.PrepareChild(fixture, "wait");
                File.Delete(fixture.ActivationPath);
                var root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components, "probe", true, Origin(fixture));
                var starting = root.StartAsync();
                try
                {
                    var watch = Stopwatch.StartNew();
                    while ((root.ReadPreparation()?.OutputBytes ?? 0) < 8 && !starting.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
                        await Task.Delay(10);
                    var snapshot = root.ReadPreparation();
                    Check(snapshot != null && snapshot.OutputBytes >= 8 && !starting.IsCompleted,
                        "ordinary startup really waits inside the running helper before stop");
                    using (var child = Process.GetProcessById(checked((int)snapshot!.ProcessId)))
                    {
                        root.RequestStop();
                        Check(await root.CloseAsync() && !await starting && child.HasExited,
                            "ordinary Close waits for cancellation, actual child exit and the original factory");
                    }
                    Check(root.Context.IsStopped && root.ReadPreparation()!.ReadersJoined && root.ReadPreparation()!.LiveBufferBytes == 0 &&
                        root.PreparationResult?.ProcessCause == "CANCELLED" && !Directory.Exists(Connection(fixture)),
                        "stopping during preparation cannot create authorization or revive its context");
                    fixture.CheckReleased();
                }
                finally { await root.CloseAsync(); }
            }
        }

        private static async Task PublishedInstallation(bool explicitProfile, bool stopBeforeRelease)
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                SetupInitializationTests.PrepareChild(fixture, "release");
                byte[] publication = File.ReadAllBytes(fixture.ActivationPath);
                File.Delete(fixture.ActivationPath);
                using (var release = new EventWaitHandle(false, EventResetMode.ManualReset,
                    SetupChildFixture.ReleaseEventName(fixture.EnvironmentId), out bool created))
                {
                    Check(created, "the release event belongs to this unique preparation fixture");
                    var root = new InstalledGameConnection(fixture.EnvironmentId, fixture.Components,
                        explicitProfile ? "probe" : null, bundledOrigin: Origin(fixture));
                    var starting = root.StartAsync();
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        while ((root.ReadPreparation()?.OutputBytes ?? 0) < 8 && !starting.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
                            await Task.Delay(10);
                        Check((root.ReadPreparation()?.OutputBytes ?? 0) >= 8 && !starting.IsCompleted,
                            "publication is injected only after the real helper has entered its waiting stage");
                        // The parent publishes known fixture bytes. The child
                        // remains an IO fixture, never a second installer algorithm.
                        File.WriteAllBytes(fixture.ActivationPath, publication);
                        if (stopBeforeRelease) root.RequestStop();
                        release.Set();
                        bool active = await starting;
                        Check(root.PreparationResult?.Installation == PreparedInstallationState.MatchesSource &&
                            publication.SequenceEqual(File.ReadAllBytes(fixture.ActivationPath)),
                            "successful or cancelled execution retains the real matching publication");
                        if (stopBeforeRelease)
                            Check(!active && root.Context.IsStopped && !Directory.Exists(Connection(fixture)),
                                "stop before helper release blocks late Owner creation even when installation matches");
                        else if (explicitProfile)
                        {
                            var authorization = ContractCodec.DecodeAuthorizationSnapshotV2(ContractCatalog.ParseAndValidate(
                                SchemaIds.AuthorizationSnapshotV2, File.ReadAllText(Path.Combine(Connection(fixture), "authorization.json"))));
                            Check(active && root.PreparationResult?.Ready == true && authorization.Profiles.Single().ProfileId == "probe" &&
                                authorization.Profiles.Single().ScopeMask == (long)GrantScope.Read,
                                "explicit profile setup continues through a fresh installed binding into the real read-only authority");
                        }
                        else
                            Check(!active && root.PreparationResult?.Ready == true && root.ProductVersion != null && !Directory.Exists(Connection(fixture)),
                                "automatic installation success does not create default authorization during normal startup");
                    }
                    finally
                    {
                        root.RequestStop(); release.Set();
                        Check(await root.CloseAsync(), "the publication fixture joins the actual factory and authority");
                        InstalledGameConnectionTests.RemoveConnection(fixture);
                    }
                    fixture.CheckReleased();
                }
            }
        }

        private static string Connection(InstalledCoreBindingTests.Fixture fixture) =>
            Path.Combine(Path.GetDirectoryName(fixture.ActivationPath)!, "connection");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
    }
}
