using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    internal static class InstalledCoreBindingTests
    {
        private static int checks;
        private static void Check(bool value, string label) { Program.Assert(value, label); checks++; }
        private static void Reject(Func<InstalledCoreBinding> action)
        {
            InstalledCoreBinding? unexpected = null;
            try { unexpected = action(); }
            catch (InvalidDataException) { checks++; return; }
            catch (ContractValidationException) { checks++; return; }
            finally { unexpected?.Dispose(); }
            throw new InvalidOperationException("Expected installation binding refusal.");
        }
        internal static int Run()
        {
            Program.Stage = "installed Core pins and loaded component cohort";
            checks = 0;
            var captured = LoadedManagedComponent.Capture(typeof(ContractCodec).Assembly);
            Check(captured.Name == "SkylinesLink.Contracts" && captured.ModuleId == typeof(ContractCodec).Assembly.ManifestModule.ModuleVersionId,
                "capture uses the exact type-anchored loaded module without Location or assembly enumeration");
            using (var fixture = new Fixture())
            {
                var binding = InstalledCoreBinding.Open(fixture.EnvironmentId.ToUpperInvariant(), fixture.Components);
                try
                {
                    Check(binding.EnvironmentId == fixture.EnvironmentId && binding.Version == Fixture.Release &&
                        binding.CoreManifestSha256 == fixture.ManifestHash && !Directory.Exists(binding.ConnectionDirectory),
                        "existing installation binds without creating connection, authorization or profile data");
                    foreach (string path in fixture.PinnedFiles)
                    {
                        try { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                        catch (IOException) { checks++; continue; }
                        throw new InvalidOperationException("A live Core binding did not hold its actual file.");
                    }
                }
                finally { binding.Dispose(); }
                binding.Dispose(); fixture.CheckReleased(); checks++;
                var wrong = fixture.Components.ToArray();
                wrong[0] = new LoadedManagedComponent(wrong[0].Name, Guid.NewGuid(), wrong[0].AssemblyVersion, wrong[0].InformationalVersion);
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, wrong)); fixture.CheckReleased();
                wrong = fixture.Components.ToArray();
                wrong[0] = new LoadedManagedComponent(wrong[0].Name, wrong[0].ModuleId, new Version(0, 2, 0, 0), wrong[0].InformationalVersion);
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, wrong)); fixture.CheckReleased();
                wrong = fixture.Components.ToArray();
                wrong[0] = new LoadedManagedComponent(wrong[0].Name, wrong[0].ModuleId, wrong[0].AssemblyVersion, "different-source");
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, wrong)); fixture.CheckReleased();
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components.Take(3).ToArray()));
                using (var stop = new CancellationTokenSource())
                {
                    stop.Cancel();
                    bool cancelled = false;
                    try { using (InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components, stop.Token)) { } }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "startup cancellation does not open a new binding");
                    fixture.CheckReleased();
                }
                File.AppendAllText(fixture.PayloadPaths[0], "changed");
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)); fixture.CheckReleased();
            }
            using (var fixture = new Fixture())
            {
                var index = (JObject)ContractJson.Parse(File.ReadAllText(fixture.IndexPath));
                index["components"]![1] = index["components"]![0]!.DeepClone();
                File.WriteAllText(fixture.IndexPath, index.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                fixture.Publish();
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)); fixture.CheckReleased();
            }
            using (var fixture = new Fixture())
            {
                File.Delete(fixture.IndexPath); fixture.Publish();
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)); fixture.CheckReleased();
                Check(!File.Exists(fixture.IndexPath), "old Core metadata is never fabricated from loaded modules");
            }
            using (var fixture = new Fixture())
            {
                File.WriteAllText(fixture.ManifestPath, "{}");
                Reject(() => InstalledCoreBinding.Open(fixture.EnvironmentId, fixture.Components)); fixture.CheckReleased();
            }
            return checks;
        }

        internal sealed class Fixture : IDisposable
        {
            internal const string Release = "0.1.0-probe";
            internal readonly string EnvironmentId = "probe-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            private readonly string root, releaseRoot, marker;
            internal readonly string IndexPath, ManifestPath, ActivationPath;
            internal readonly List<LoadedManagedComponent> Components = new List<LoadedManagedComponent>();
            internal readonly List<string> PayloadPaths = new List<string>();
            internal string ManifestHash = "";
            internal string CoreRoot => releaseRoot;
            internal string ModPath => Path.Combine(releaseRoot, "mod", "SkylinesLink.Mod.dll");
            internal string SetupPath => Path.Combine(releaseRoot, "desktop", "setup", "SkylinesLink.Setup.exe");
            internal string StableLauncherPath => Path.Combine(root, "launcher", "SkylinesLink.Launcher.exe");
            private readonly List<string> additionalOwnedFiles = new List<string>();
            internal IEnumerable<string> PinnedFiles => new[] { ActivationPath, ManifestPath, IndexPath }.Concat(PayloadPaths).Concat(additionalOwnedFiles);
            internal void InstallFixtureLauncher()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StableLauncherPath)!);
                if (!additionalOwnedFiles.Contains(StableLauncherPath)) additionalOwnedFiles.Add(StableLauncherPath);
                File.Copy(Path.Combine(releaseRoot, "desktop", "launcher", "SkylinesLink.Launcher.exe"), StableLauncherPath, true);
            }
            internal Fixture(bool complete = false)
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skylineslink", "environments", EnvironmentId);
                if (Directory.Exists(root)) throw new IOException("Fixture is not fresh.");
                releaseRoot = Path.Combine(root, "releases", Release);
                Directory.CreateDirectory(Path.Combine(releaseRoot, "mod"));
                marker = Path.Combine(root, ".owned-probe"); File.WriteAllText(marker, EnvironmentId);
                IndexPath = Path.Combine(releaseRoot, "mod", "managed-components.json");
                ManifestPath = Path.Combine(releaseRoot, "core-manifest.json"); ActivationPath = Path.Combine(root, "activation.json");
                var entries = new JArray();
                foreach (string name in new[] { "SkylinesLink.Contracts", "SkylinesLink.Application", "SkylinesLink.Infrastructure", "SkylinesLink.Mod" })
                {
                    // Synthetic identity facts exercise binding IO; these bytes are never loaded as assemblies.
                    var component = new LoadedManagedComponent(name, Guid.NewGuid(), new Version(0, 1, 0, 0), Release + "+" + new string('a', 40));
                    Components.Add(component);
                    string path = Path.Combine(releaseRoot, "mod", name + ".dll"); PayloadPaths.Add(path);
                    File.WriteAllText(path, "Synthetic known bytes for " + name, new UTF8Encoding(false));
                    entries.Add(new JObject { ["path"] = "mod/" + name + ".dll", ["moduleId"] = component.ModuleId.ToString("N") });
                }
                File.WriteAllText(IndexPath, new JObject { ["formatVersion"] = 1, ["productId"] = "skylineslink", ["components"] = entries }
                    .ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                if (complete)
                {
                    // File-set inputs only: none of these executable/archive bytes is loaded or launched.
                    foreach (string relative in new[] {
                        "LICENSE", "SOURCE.txt", "source/SkylinesLink-source.zip", "runtime/node.exe", "runtime/Node.LICENSE.txt",
                        "host/host.mjs", "host/host.mjs.LEGAL.txt", "host/host.THIRD_PARTY_NOTICES.txt",
                        "desktop/launcher/SkylinesLink.Launcher.exe", "desktop/launcher/DOTNET.LICENSE.txt",
                        "desktop/launcher/DOTNET.THIRD-PARTY-NOTICES.txt", "desktop/launcher/DOTNET.SOURCES.txt",
                        "desktop/setup/SkylinesLink.Setup.exe", "desktop/setup/DOTNET.LICENSE.txt",
                        "desktop/setup/DOTNET.THIRD-PARTY-NOTICES.txt", "desktop/setup/DOTNET.SOURCES.txt" })
                    {
                        string path = Path.Combine(releaseRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, "Synthetic complete Core payload: " + relative, new UTF8Encoding(false));
                        PayloadPaths.Add(path);
                    }
                }
                Publish();
            }
            internal void Publish()
            {
                var files = new JArray();
                foreach (string path in PayloadPaths.Concat(File.Exists(IndexPath) ? new[] { IndexPath } : Array.Empty<string>()))
                    files.Add(new JObject { ["path"] = path.Substring(releaseRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/'),
                        ["size"] = new FileInfo(path).Length, ["sha256"] = Hash(path) });
                File.WriteAllText(ManifestPath, new JObject { ["productId"] = "skylineslink", ["version"] = Release,
                    ["sourceCommit"] = new string('a', 40), ["contractVersion"] = "2", ["files"] = files }
                    .ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                ManifestHash = Hash(ManifestPath);
                File.WriteAllText(ActivationPath, new JObject { ["productId"] = "skylineslink", ["version"] = Release,
                    ["coreManifestSha256"] = ManifestHash }.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
            }
            internal void CheckReleased()
            {
                foreach (string path in PinnedFiles.Where(File.Exists)) using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            private static string Hash(string path)
            {
                using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            }
            public void Dispose()
            {
                if (!root.EndsWith(Path.DirectorySeparatorChar + EnvironmentId, StringComparison.Ordinal) || File.ReadAllText(marker) != EnvironmentId)
                    throw new IOException("Fixture cleanup identity mismatch.");
                string[] owned = PinnedFiles.Concat(new[] { marker }).ToArray();
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
                foreach (string path in owned)
                {
                    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Fixture cleanup file is outside its owned environment.");
                    for (string? directory = Path.GetDirectoryName(path); directory != null && directory != root; directory = Path.GetDirectoryName(directory))
                        directories.Add(directory);
                    if (!File.Exists(path)) continue;
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links.");
                    File.Delete(path);
                }
                foreach (string directory in directories.OrderByDescending(path => path.Length))
                {
                    if (!Directory.Exists(directory)) continue;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses directory links.");
                    Directory.Delete(directory);
                }
            }
        }
    }
}
