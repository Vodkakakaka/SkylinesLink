using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SkylinesLink.Infrastructure.Installation
{
    /// <summary>本次自身资产的普通来源值；由原生主线程取得，不携带SDK对象。</summary>
    public sealed class ModBundleOrigin
    {
        public ModBundleOrigin(string assetPath, bool packaged, Guid moduleId)
        {
            AssetPath = assetPath ?? throw new ArgumentNullException(nameof(assetPath));
            Packaged = packaged;
            ModuleId = moduleId;
        }
        public string AssetPath { get; }
        public bool Packaged { get; }
        public Guid ModuleId { get; }
    }

    /// <summary>验证本次加载模组的同包完整Core；调用方持有至实际Setup进程收束。</summary>
    public sealed class BundledCoreBinding : IDisposable
    {
        private readonly ManagedCoreBinding core;
        private BundledCoreBinding(ManagedCoreBinding core) { this.core = core; }
        public string Root => core.Root;
        public string Version => core.Version;
        public string CoreManifestSha256 => core.ManifestSha256;
        public string SetupPath => core.RequireFile("desktop/setup/SkylinesLink.Setup.exe");

        public static BundledCoreBinding Open(ModBundleOrigin origin, IReadOnlyCollection<LoadedManagedComponent> loaded,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (origin == null) throw new ArgumentNullException(nameof(origin));
            if (origin.Packaged) throw new InvalidDataException("The packaged asset carrier is not supported for direct execution.");
            string path = origin.AssetPath;
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathRooted(path))
                throw new InvalidDataException("The own asset has no supported absolute path.");
            string pathRoot = Path.GetPathRoot(path)!;
            // IsPathRooted also accepts drive-relative C:foo and root-relative
            // \foo on Windows. Neither is a complete SDK source location.
            if (pathRoot.Length <= 2 || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidDataException("The own asset has no supported absolute path.");
            path = Path.GetFullPath(path);
            if (!Path.GetFileName(path).Equals("SkylinesLink.Mod.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The own asset is not in the supported Core layout.");
            string mod = Path.GetDirectoryName(path)!;
            if (!Path.GetFileName(mod).Equals("mod", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The own asset is not in the supported Core layout.");
            string? root = Path.GetDirectoryName(mod);
            if (root == null) throw new InvalidDataException("The own asset has no Core root.");
            var core = new ManagedCoreBinding(loaded, cancellationToken);
            try
            {
                if (origin.ModuleId == Guid.Empty || origin.ModuleId != core.ModModuleId)
                    throw new InvalidDataException("The own asset does not belong to the loaded Mod.");
                // Exactly this adjacent root, never a parent search or a fallback
                // to activation/another installation to fill missing bundle files.
                core.Read(root, cancellationToken);
                core.RequireCompletePayload();
                return new BundledCoreBinding(core);
            }
            catch (Exception failure)
            {
                try { core.Dispose(); }
                catch (Exception closeFailure) { throw new AggregateException(failure, closeFailure); }
                throw;
            }
        }

        public void Dispose() => core.Dispose();
    }
}
