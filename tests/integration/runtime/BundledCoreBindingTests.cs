using System;
using System.IO;
using System.Linq;
using System.Threading;
using SkylinesLink.Infrastructure.Installation;

namespace SkylinesLink.RuntimeProbe
{
    internal static class BundledCoreBindingTests
    {
        private static int checks;
        private static void Check(bool condition, string message) { Program.Assert(condition, message); checks++; }
        private static ModBundleOrigin Origin(InstalledCoreBindingTests.Fixture fixture) =>
            new ModBundleOrigin(fixture.ModPath, false, fixture.Components.Single(item => item.Name == "SkylinesLink.Mod").ModuleId);
        private static void Refuse<T>(Func<BundledCoreBinding> open, string message) where T : Exception
        {
            BundledCoreBinding? unexpected = null;
            try { unexpected = open(); }
            catch (T) { Check(true, message); return; }
            finally { unexpected?.Dispose(); }
            throw new InvalidOperationException("Expected bundled Core refusal: " + message);
        }

        internal static int Run()
        {
            Program.Stage = "own bundled Core exact source and held IO";
            checks = 0;
            CompleteSourcePins();
            ExactOrigin();
            CompletePayloadFailures();
            CancellationAndCohortFailure();
            return checks;
        }

        private static void CompleteSourcePins()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                string environmentRoot = Path.GetDirectoryName(fixture.ActivationPath)!;
                string[] before = Directory.GetFileSystemEntries(environmentRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                byte[] activation = File.ReadAllBytes(fixture.ActivationPath);
                var binding = BundledCoreBinding.Open(Origin(fixture), fixture.Components);
                string moved = Path.GetFullPath(fixture.CoreRoot + ".moved-probe");
                string ownedPrefix = Path.GetFullPath(environmentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(fixture.CoreRoot).StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !moved.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(moved) || File.Exists(moved))
                    throw new IOException("The source rename probe is not confined to its fresh owned environment.");
                bool renamed = false;
                try
                {
                    Check(binding.Root == fixture.CoreRoot && binding.Version == InstalledCoreBindingTests.Fixture.Release &&
                        binding.CoreManifestSha256 == fixture.ManifestHash && binding.SetupPath == fixture.SetupPath,
                        "exact own asset binds its complete neighboring Core and Setup without using an active installation");
                    foreach (string path in fixture.PinnedFiles.Where(path => path != fixture.ActivationPath))
                    {
                        bool pinned = false;
                        try { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                        catch (IOException) { pinned = true; }
                        Check(pinned, "the bundled manifest, index and each complete payload remain actually pinned");
                    }
                    using (File.Open(fixture.ActivationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    Check(true, "source binding leaves unrelated activation free for an installation change");
                    bool rejected = false;
                    try { Directory.Move(fixture.CoreRoot, moved); renamed = true; }
                    catch (IOException) { rejected = true; }
                    Check(rejected && Directory.Exists(fixture.CoreRoot) && !Directory.Exists(moved),
                        "the actual source directory cannot be renamed while its Core binding is held");
                }
                finally
                {
                    binding.Dispose();
                    // If the required pin invariant fails, restore only this exact
                    // fresh fixture directory after releasing handles before reporting it.
                    if (renamed) Directory.Move(moved, fixture.CoreRoot);
                }
                binding.Dispose(); fixture.CheckReleased();
                Check(before.SequenceEqual(Directory.GetFileSystemEntries(environmentRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)) &&
                    activation.SequenceEqual(File.ReadAllBytes(fixture.ActivationPath)),
                    "source inspection and release create no directory, authorization, activation or other installation writes");
            }
        }

        private static void ExactOrigin()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                var origin = Origin(fixture);
                // A packaged carrier is refused before touching even its otherwise valid source file.
                using (File.Open(fixture.ManifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Refuse<InvalidDataException>(() => BundledCoreBinding.Open(new ModBundleOrigin(origin.AssetPath, true, origin.ModuleId), fixture.Components),
                        "unsupported packaged origin is refused before manifest IO");
                Refuse<InvalidDataException>(() => BundledCoreBinding.Open(new ModBundleOrigin(origin.AssetPath, false, Guid.NewGuid()), fixture.Components),
                    "an origin from another Mod module cannot borrow the loaded cohort");
                foreach (string path in new[] { @"C:mod\SkylinesLink.Mod.dll", @"\mod\SkylinesLink.Mod.dll",
                    Path.Combine(fixture.CoreRoot, "mod", "Other.dll"), Path.Combine(fixture.CoreRoot, "other", "SkylinesLink.Mod.dll") })
                    Refuse<InvalidDataException>(() => BundledCoreBinding.Open(new ModBundleOrigin(path, false, origin.ModuleId), fixture.Components),
                        "relative or noncanonical own-asset layout cannot select a bundle");
                string nested = Path.Combine(fixture.CoreRoot, "nested-source-probe");
                string shadow = Path.Combine(nested, "mod", "SkylinesLink.Mod.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
                try
                {
                    File.Copy(fixture.ModPath, shadow);
                    Refuse<IOException>(() => BundledCoreBinding.Open(new ModBundleOrigin(shadow, false, origin.ModuleId), fixture.Components),
                        "a matching Mod under an incomplete adjacent root never searches upward into the valid parent Core");
                }
                finally
                {
                    if (File.Exists(shadow)) File.Delete(shadow);
                    Directory.Delete(Path.GetDirectoryName(shadow)!); Directory.Delete(nested);
                }
                fixture.CheckReleased();
            }
        }

        private static void CompletePayloadFailures()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                File.Delete(fixture.SetupPath); fixture.PayloadPaths.Remove(fixture.SetupPath); fixture.Publish();
                Refuse<InvalidDataException>(() => BundledCoreBinding.Open(Origin(fixture), fixture.Components),
                    "a self-consistent manifest with no Setup cannot claim a complete executable bundle");
                fixture.CheckReleased();
            }
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                byte[] bytes = File.ReadAllBytes(fixture.SetupPath); bytes[0] ^= 1; File.WriteAllBytes(fixture.SetupPath, bytes);
                Refuse<InvalidDataException>(() => BundledCoreBinding.Open(Origin(fixture), fixture.Components),
                    "a same-size bad Setup byte fails actual hash verification and releases every earlier file pin");
                fixture.CheckReleased();
            }
        }

        private static void CancellationAndCohortFailure()
        {
            using (var fixture = new InstalledCoreBindingTests.Fixture(complete: true))
            {
                using (var stop = new CancellationTokenSource())
                {
                    stop.Cancel();
                    Refuse<OperationCanceledException>(() => BundledCoreBinding.Open(Origin(fixture), fixture.Components, stop.Token),
                        "already cancelled source inspection never starts a new binding lifetime");
                    fixture.CheckReleased();
                }
                var wrong = fixture.Components.ToArray();
                var first = wrong[0];
                wrong[0] = new LoadedManagedComponent(first.Name, Guid.NewGuid(), first.AssemblyVersion, first.InformationalVersion);
                Refuse<InvalidDataException>(() => BundledCoreBinding.Open(Origin(fixture), wrong),
                    "the exact Mod origin still requires the shared complete loaded-component cohort");
                fixture.CheckReleased();
            }
        }
    }
}
