using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.RuntimeProbe
{
    internal static class SetupInitializationTests
    {
        private static int checks;
        private static void Check(bool condition, string message) { Program.Assert(condition, message); checks++; }
        private static ModBundleOrigin Origin(InstalledCoreBindingTests.Fixture fixture) =>
            new ModBundleOrigin(fixture.ModPath, false, fixture.Components.Single(item => item.Name == "SkylinesLink.Mod").ModuleId);

        internal static async Task<int> RunAsync()
        {
            Program.Stage = "complete installed Core and stable entry readback";
            checks = 0;
            CompleteReadback();
            Program.Stage = "bundled Setup actual Framework IO and lifetime";
            await FiniteOutput("normal", 0, PreparedInstallationState.MatchesSource, 24576);
            await FiniteOutput("empty", 0, PreparedInstallationState.MatchesSource, 0);
            await FiniteOutput("fail", 37, PreparedInstallationState.MatchesSource, null);
            await FiniteOutput("normal", 0, PreparedInstallationState.Unavailable, 24576, installEntry: false);
            await StopRunningChild(timeout: false);
            await StopRunningChild(timeout: true);
            await OutputLimit();
            await RejectedSource();
            return checks;
        }

        private static void CompleteReadback()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture())
            {
                bool rejected = false;
                try { using (InstalledCoreBinding.OpenComplete(fixture.EnvironmentId, fixture.Components)) { } }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "complete installation readback rejects a listed but incomplete Core");
                fixture.CheckReleased();
                using (InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)) { }
                Check(true, "ordinary installed binding retains its existing small-package behavior");
            }
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                bool missing = false;
                try { using (InstalledCoreBinding.OpenComplete(fixture.EnvironmentId, fixture.Components)) { } }
                catch (FileNotFoundException) { missing = true; }
                catch (DirectoryNotFoundException) { missing = true; }
                Check(missing, "complete readback does not create a missing stable entry");
                fixture.CheckReleased();
                fixture.InstallFixtureLauncher();
                using (var ready = InstalledCoreBinding.OpenComplete(fixture.EnvironmentId, fixture.Components))
                {
                    Check(ready.CoreManifestSha256 == fixture.ManifestHash, "complete readback keeps the actual active Core identity");
                    CheckPinned(fixture.StableLauncherPath);
                }
                fixture.CheckReleased();
                byte[] changed = File.ReadAllBytes(fixture.StableLauncherPath); changed[0] ^= 1;
                File.WriteAllBytes(fixture.StableLauncherPath, changed);
                bool bad = false;
                try { using (InstalledCoreBinding.OpenComplete(fixture.EnvironmentId, fixture.Components)) { } }
                catch (InvalidDataException) { bad = true; }
                Check(bad, "same-length bad stable bytes are not reported as a complete installation");
                fixture.CheckReleased();
            }
        }

        internal static void PrepareChild(InstalledCoreBindingTests.Fixture fixture, string mode, bool installEntry = true)
        {
            // Only this known Framework fixture replaces the synthetic Setup role.
            // Its BCL-only entry proves the copied exe needs no adjacent product DLLs.
            File.Copy(typeof(Program).Assembly.Location, fixture.SetupPath, true);
            File.WriteAllText(Path.Combine(fixture.CoreRoot, "SOURCE.txt"),
                "SkylinesLink owned Setup IO fixture\n" + mode, new UTF8Encoding(false));
            fixture.Publish();
            if (installEntry) fixture.InstallFixtureLauncher();
        }

        private static async Task FiniteOutput(string mode, uint exitCode, PreparedInstallationState installation,
            long? outputBytes, bool installEntry = true)
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                PrepareChild(fixture, mode, installEntry);
                byte[] activation = File.ReadAllBytes(fixture.ActivationPath);
                var run = SetupInitializationRun.Start(Origin(fixture), fixture.Components, fixture.EnvironmentId);
                try
                {
                    var result = await Finish(run);
                    Check(result.ProcessCause == "CHILD_EXITED" && result.ExitCode == exitCode,
                        "the copied BCL fixture actually exits with its observed code: " + mode);
                    Check(result.Installation == installation && result.Ready == (exitCode == 0 && installation == PreparedInstallationState.MatchesSource),
                        "process exit and actual complete-installation readback remain separate facts: " + mode);
                    var snapshot = run.Read();
                    Check(snapshot.ProcessId != 0 && snapshot.ChildExited && snapshot.ReadersJoined && snapshot.LiveBufferBytes == 0,
                        "completion follows real child exit, both readers and buffer release: " + mode);
                    if (outputBytes.HasValue) Check(snapshot.OutputBytes == outputBytes.Value, "both output streams are drained with exact finite byte count: " + mode);
                    Check(activation.SequenceEqual(File.ReadAllBytes(fixture.ActivationPath)), "runner/readback does not fabricate installation writes");
                    Check(!Directory.GetFiles(Path.GetDirectoryName(fixture.SetupPath)!, "*.dll").Any(),
                        "the copied Framework fixture ran without copying product dependencies beside it");
                    fixture.CheckReleased();
                }
                finally { run.RequestStop(); await run.Completion; }
            }
        }

        private static async Task StopRunningChild(bool timeout)
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                PrepareChild(fixture, "wait");
                var run = SetupInitializationRun.Start(Origin(fixture), fixture.Components, fixture.EnvironmentId,
                    timeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(20));
                try
                {
                    await WaitForOutput(run);
                    using (var child = Process.GetProcessById(checked((int)run.Read().ProcessId)))
                    {
                        Check(!child.HasExited && !run.Completion.IsCompleted, "stop test observes the actual live child after both readiness streams");
                        CheckPinned(fixture.ManifestPath); CheckPinned(fixture.SetupPath);
                        Check(run.Read().LiveBufferBytes == 8192, "two fixed small output buffers remain owned while native reads wait");
                        if (!timeout) run.RequestStop();
                        var result = await Finish(run);
                        Check(result.ProcessCause == (timeout ? "TIMEOUT" : "CANCELLED"), "deadline and explicit cancellation retain distinct causes");
                        Check(child.HasExited && run.Read().ChildExited && run.Read().ReadersJoined && run.Read().LiveBufferBytes == 0,
                            "stop completion proves actual child death and reader Join, rather than just a cancelled wait");
                        Check(result.Installation == PreparedInstallationState.MatchesSource && !result.Ready,
                            "a failed run still reads existing installation facts without claiming successful execution");
                    }
                    fixture.CheckReleased();
                }
                finally { run.RequestStop(); await run.Completion; }
            }
        }

        private static async Task OutputLimit()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                PrepareChild(fixture, "flood");
                var run = SetupInitializationRun.Start(Origin(fixture), fixture.Components, fixture.EnvironmentId);
                try
                {
                    var result = await Finish(run);
                    Check(result.ProcessCause == "OUTPUT_LIMIT" && !result.Ready, "combined child output crosses the stop threshold without becoming a captured log");
                    Check(run.Read().OutputBytes > 32768 && run.Read().ChildExited && run.Read().ReadersJoined && run.Read().LiveBufferBytes == 0,
                        "overflow joins both readers and reports actual observed bytes including in-flight reads");
                    fixture.CheckReleased();
                }
                finally { run.RequestStop(); await run.Completion; }
            }
        }

        private static async Task RejectedSource()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                PrepareChild(fixture, "empty");
                using (var file = File.Open(fixture.SetupPath, FileMode.Open, FileAccess.Write)) file.WriteByte(0);
                var run = SetupInitializationRun.Start(Origin(fixture), fixture.Components, fixture.EnvironmentId);
                var result = await Finish(run);
                Check(result.ProcessCause == "SOURCE_REJECTED" && result.Installation == PreparedInstallationState.NotObserved && run.Read().ProcessId == 0,
                    "bad verified-source bytes never execute and do not trigger an installed-state fallback");
                fixture.CheckReleased();
            }
        }

        private static async Task WaitForOutput(SetupInitializationRun run)
        {
            var watch = Stopwatch.StartNew();
            while (run.Read().OutputBytes < 8 && !run.Completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Check(run.Read().OutputBytes >= 8 && !run.Completion.IsCompleted, "both actual readiness outputs precede the cancellation/timeout assertion");
        }

        private static async Task<SetupInitializationResult> Finish(SetupInitializationRun run)
        {
            var finished = await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromSeconds(10)));
            Check(finished == run.Completion, "the actual Setup lifetime completed within the fixture observation window");
            return await run.Completion;
        }

        private static void CheckPinned(string path)
        {
            bool held = false;
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException) { held = true; }
            Check(held, "the exact source/entry remains actually pinned while its owner is live");
        }
    }
}
