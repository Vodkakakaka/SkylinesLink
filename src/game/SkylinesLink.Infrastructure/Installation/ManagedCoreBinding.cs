using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SkylinesLink.Contracts;
using SkylinesLink.Shared.Installation;

namespace SkylinesLink.Infrastructure.Installation
{
    /// <summary>安装与同包来源共用的字节/已载身份验证及所有文件pins。</summary>
    internal sealed class ManagedCoreBinding : IDisposable
    {
        private const int MaximumManifestBytes = 1024 * 1024;
        private const long MaximumCoreBytes = 1024L * 1024 * 1024;
        private static readonly string[] ComponentNames = {
            "SkylinesLink.Contracts", "SkylinesLink.Application", "SkylinesLink.Infrastructure", "SkylinesLink.Mod"
        };
        private readonly List<FileStream> pins = new List<FileStream>();
        private readonly Dictionary<string, FileStream> files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LoadedManagedComponent> components = new Dictionary<string, LoadedManagedComponent>(StringComparer.Ordinal);

        internal ManagedCoreBinding(IReadOnlyCollection<LoadedManagedComponent> loaded, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (loaded == null) throw new ArgumentNullException(nameof(loaded));
            if (loaded.Count != ComponentNames.Length) throw new InvalidDataException("Loaded product component set is incomplete.");
            foreach (var item in loaded)
            {
                if (item == null || item.ModuleId == Guid.Empty || !Array.Exists(ComponentNames, name => name == item.Name) ||
                    components.ContainsKey(item.Name)) throw new InvalidDataException("Loaded product component set is invalid.");
                components.Add(item.Name, item);
            }
            if (components.Count != ComponentNames.Length) throw new InvalidDataException("Loaded product component set is incomplete.");
        }

        internal string Root { get; private set; } = "";
        internal string Version { get; private set; } = "";
        internal string ManifestSha256 { get; private set; } = "";
        internal Guid ModModuleId => components["SkylinesLink.Mod"].ModuleId;

        internal ActivationRecord ReadActivation(string path) =>
            ContractCodec.DecodeActivationRecord(ContractCatalog.ParseAndValidate(SchemaIds.ActivationRecord, ReadText(Pin(path), 4096)));

        internal void Read(string root, CancellationToken cancellation, string? expectedVersion = null, string? expectedHash = null)
        {
            cancellation.ThrowIfCancellationRequested();
            Root = Path.GetFullPath(root);
            var manifestPin = Pin(Path.Combine(Root, "core-manifest.json"));
            string manifestText = ReadText(manifestPin, MaximumManifestBytes);
            ManifestSha256 = Hash(manifestPin, cancellation);
            // The activation's known hash is checked before any payload IO.
            if (expectedHash != null && !ManifestSha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Active Core manifest hash mismatch.");
            var manifest = ContractCodec.DecodeCoreManifest(ContractCatalog.ParseAndValidate(SchemaIds.CoreManifest, manifestText));
            if ((expectedVersion != null && manifest.Version != expectedVersion) || manifest.ContractVersion != "2")
                throw new InvalidDataException("Core identity mismatch.");
            CoreInstallationPaths.ValidateIdentifier(manifest.Version);
            if (CoreInstallationPaths.IsReservedDeviceName(manifest.Version)) throw new InvalidDataException("Invalid Core version.");
            Version = manifest.Version;
            int suffix = Version.IndexOf('-');
            string number = suffix < 0 ? Version : Version.Substring(0, suffix);
            if (!System.Version.TryParse(number + ".0", out var expectedAssemblyVersion)) throw new InvalidDataException("Unsupported product version.");
            string expectedInformation = Version + "+" + manifest.SourceCommit;
            long total = 0;
            foreach (var item in manifest.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                total = checked(total + item.Size);
                if (total > MaximumCoreBytes) throw new InvalidDataException("Core exceeds the installation read budget.");
                string first = item.Path.Split('/')[0];
                if (first.Equals("core-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    first.Equals("preparation-manifest.json", StringComparison.OrdinalIgnoreCase) || files.ContainsKey(item.Path))
                    throw new InvalidDataException("Core file set contains duplicate or manifest entries.");
                var pin = Pin(CoreInstallationPaths.ResolveCorePath(Root, item.Path));
                files.Add(item.Path, pin);
                if (pin.Length != item.Size || !Hash(pin, cancellation).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Core payload hash or size mismatch.");
            }
            if (!files.TryGetValue("mod/managed-components.json", out var indexPin))
                throw new InvalidDataException("Core has no supported loaded-module binding metadata; update Core.");
            var index = ContractCodec.DecodeManagedComponents(ContractCatalog.ParseAndValidate(SchemaIds.ManagedComponents, ReadText(indexPin, 2048)));
            var moduleIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in index.Components)
            {
                if (moduleIds.ContainsKey(item.Path) || !files.ContainsKey(item.Path) ||
                    !Guid.TryParseExact(item.ModuleId, "N", out var moduleId) || moduleId == Guid.Empty)
                    throw new InvalidDataException("Core managed component binding metadata is invalid.");
                moduleIds.Add(item.Path, moduleId);
            }
            foreach (string name in ComponentNames)
            {
                cancellation.ThrowIfCancellationRequested();
                var actual = components[name];
                if (actual.AssemblyVersion != expectedAssemblyVersion || actual.InformationalVersion != expectedInformation ||
                    !moduleIds.TryGetValue("mod/" + name + ".dll", out var moduleId) || moduleId != actual.ModuleId)
                    throw new InvalidDataException("Loaded module release or identity mismatch.");
            }
        }

        internal void RequireCompletePayload()
        {
            foreach (string path in CorePayloadFiles.Required)
                if (!files.TryGetValue(path, out var file) || file.Length == 0)
                    throw new InvalidDataException("Core is missing a required nonempty payload.");
        }

        internal void RequireStableLauncher(string environment, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!files.TryGetValue("desktop/launcher/SkylinesLink.Launcher.exe", out var expected))
                throw new InvalidDataException("Core is missing its launcher payload.");
            // Only the fixed installation entry is admitted here. Both this pin
            // and the verified Core role remain owned by the same binding.
            var launcher = Pin(CoreInstallationPaths.StableLauncherPath(environment));
            if (launcher.Length != expected.Length ||
                !Hash(launcher, cancellation).Equals(Hash(expected, cancellation), StringComparison.Ordinal))
                throw new InvalidDataException("Stable launcher does not match the active Core payload.");
        }

        internal string RequireFile(string relativePath)
        {
            if (pins.Count == 0) throw new ObjectDisposedException(nameof(ManagedCoreBinding));
            if (!files.ContainsKey(relativePath)) throw new InvalidDataException("Core is missing a required payload.");
            return CoreInstallationPaths.ResolveCorePath(Root, relativePath);
        }

        private FileStream Pin(string path)
        {
            CoreInstallationPaths.RejectReparseAncestors(path);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            pins.Add(file);
            return file;
        }
        private static string ReadText(FileStream file, int maximum)
        {
            if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("Installation record exceeds its read budget.");
            var bytes = new byte[checked((int)file.Length)];
            file.Position = 0;
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = file.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        private static string Hash(FileStream file, CancellationToken cancellation)
        {
            file.Position = 0;
            using (var sha = SHA256.Create())
            {
                // Cancellation ends new reads only after the current actual read returns.
                var buffer = new byte[65536]; int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                cancellation.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
            }
        }
        public void Dispose()
        {
            List<Exception>? failures = null;
            foreach (var file in pins)
            {
                try { file.Dispose(); }
                catch (Exception failure) { (failures ?? (failures = new List<Exception>())).Add(failure); }
            }
            if (failures != null) throw new AggregateException("Core pin close failed.", failures);
            pins.Clear(); files.Clear();
        }
    }
}
