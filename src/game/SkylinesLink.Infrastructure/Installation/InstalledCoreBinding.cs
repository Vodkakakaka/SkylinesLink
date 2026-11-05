using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SkylinesLink.Contracts;
using SkylinesLink.Shared.Installation;

namespace SkylinesLink.Infrastructure.Installation
{
    /// <summary>只读绑定已有活动发行和实际已载模块；包括activation在内的pins由同一core持有。</summary>
    public sealed class InstalledCoreBinding : IDisposable
    {
        private readonly ManagedCoreBinding core;
        private InstalledCoreBinding(string environment, IReadOnlyCollection<LoadedManagedComponent> loaded, CancellationToken cancellation)
        {
            EnvironmentId = CoreInstallationPaths.NormalizeEnvironment(environment);
            ConnectionDirectory = Path.Combine(CoreInstallationPaths.EnvironmentRoot(EnvironmentId), "connection");
            core = new ManagedCoreBinding(loaded, cancellation);
        }
        public string EnvironmentId { get; }
        public string ConnectionDirectory { get; }
        public string Version => core.Version;
        public string CoreManifestSha256 => core.ManifestSha256;

        /// <summary>绑定已有Core；保留仅含托管角色的既有读取语义，不要求完整桌面安装。</summary>
        public static InstalledCoreBinding Open(string environment, IReadOnlyCollection<LoadedManagedComponent> loaded,
            CancellationToken cancellationToken = default) => OpenCore(environment, loaded, false, cancellationToken);

        /// <summary>绑定完整Core及字节一致的固定启动入口；入口pin随本绑定持有至Dispose。</summary>
        public static InstalledCoreBinding OpenComplete(string environment, IReadOnlyCollection<LoadedManagedComponent> loaded,
            CancellationToken cancellationToken = default) => OpenCore(environment, loaded, true, cancellationToken);

        private static InstalledCoreBinding OpenCore(string environment, IReadOnlyCollection<LoadedManagedComponent> loaded,
            bool completeInstallation, CancellationToken cancellationToken)
        {
            if (loaded == null) throw new ArgumentNullException(nameof(loaded));
            var binding = new InstalledCoreBinding(environment, loaded, cancellationToken);
            try
            {
                var root = CoreInstallationPaths.EnvironmentRoot(binding.EnvironmentId);
                var activation = binding.ReadActivation(Path.Combine(root, "activation.json"));
                CoreInstallationPaths.ValidateIdentifier(activation.Version);
                if (CoreInstallationPaths.IsReservedDeviceName(activation.Version)) throw new InvalidDataException("Invalid active version.");
                string release = CoreInstallationPaths.ResolveCorePath(Path.Combine(root, "releases"), activation.Version);
                binding.core.Read(release, cancellationToken, activation.Version, activation.CoreManifestSha256);
                if (completeInstallation)
                {
                    binding.core.RequireCompletePayload();
                    binding.core.RequireStableLauncher(binding.EnvironmentId, cancellationToken);
                }
                return binding;
            }
            catch (Exception failure)
            {
                try { binding.Dispose(); }
                catch (Exception closeFailure) { throw new AggregateException(failure, closeFailure); }
                throw;
            }
        }

        private ActivationRecord ReadActivation(string path)
        {
            try { return core.ReadActivation(path); }
            // This read opens the activation first, then parses only its pinned
            // bytes. Payload/manifest reads occur later, outside this boundary.
            catch (IOException failure) when (failure is FileNotFoundException || failure is DirectoryNotFoundException)
            {
                CoreInstallationPaths.RejectReparseAncestors(path);
                // A late-created leaf is no longer confirmed missing. This is
                // a path observation, not an atomic claim across concurrent IO.
                try { File.GetAttributes(path); }
                catch (FileNotFoundException) { throw new MissingCoreActivationException(); }
                catch (DirectoryNotFoundException) { throw new MissingCoreActivationException(); }
                throw new IOException("The Core activation record could not be opened.");
            }
        }

        public void Dispose() => core.Dispose();
    }

    /// <summary>激活记录的打开路径缺失；不表示已有Core、入口或授权可被替换。</summary>
    public sealed class MissingCoreActivationException : IOException
    {
        internal MissingCoreActivationException() : base("The Core activation record is missing.") { }
    }
}
