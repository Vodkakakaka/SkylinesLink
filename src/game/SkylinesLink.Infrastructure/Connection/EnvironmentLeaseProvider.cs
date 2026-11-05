using System;
using System.IO;
using SkylinesLink.Application.Context;

namespace SkylinesLink.Infrastructure.Connection
{
    public sealed class EnvironmentLease : IDisposable
    {
        private readonly object sync = new object();
        private FileStream? handle;
        internal EnvironmentLease(FileStream handle) { this.handle = handle; }
        public T WhileHeld<T>(Func<T> action)
        { lock (sync) { if (handle == null) throw new ObjectDisposedException(nameof(EnvironmentLease)); return action(); } }
        public void Dispose() { lock (sync) { handle?.Dispose(); handle = null; } }
    }

    /// <summary>路径由受控配置目录提供；句柄持有期间排他，进程退出由操作系统释放。</summary>
    public sealed class EnvironmentLeaseProvider : IEnvironmentLeaseProvider
    {
        private readonly string directory;
        private readonly IPrivateStorageBoundary? boundary;

        public EnvironmentLeaseProvider(IPrivateStorageBoundary boundary) : this(boundary.DirectoryPath)
        { this.boundary = boundary; boundary.ValidateDirectory(); }

        public EnvironmentLeaseProvider(string existingPrivateDirectory)
        {
            if (string.IsNullOrWhiteSpace(existingPrivateDirectory) || !Path.IsPathRooted(existingPrivateDirectory))
                throw new ArgumentException("An absolute private directory is required.", nameof(existingPrivateDirectory));
            directory = Path.GetFullPath(existingPrivateDirectory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Private environment directory is unavailable.");
            RejectReparseAncestors(directory);
        }

        public IDisposable? TryAcquire(string environmentId)
        {
            if (string.IsNullOrEmpty(environmentId) || environmentId.Length > 64) throw new ArgumentException("Invalid environment identity.", nameof(environmentId));
            foreach (char c in environmentId)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) throw new ArgumentException("Invalid environment identity.", nameof(environmentId));
            RejectReparseAncestors(directory);
            var path = Path.Combine(directory, environmentId + ".lock");
            boundary?.ValidateDirectory(); boundary?.ValidateFile(path, true);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Environment lock cannot be a reparse point.");
            try
            {
                if (boundary != null && !File.Exists(path))
                {
                    try { return new EnvironmentLease(boundary.CreatePrivateFile(path)); }
                    catch (IOException error) when ((error.HResult & 0xffff) == 80 || (error.HResult & 0xffff) == 183) { boundary.ValidateFile(path, false); }
                }
                return new EnvironmentLease(new FileStream(path, boundary == null ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) { return null; }
        }

        public EnvironmentLease? TryAcquireAuthority()
        { if (boundary == null) throw new InvalidOperationException("Authority requires private storage validation."); return (EnvironmentLease?)TryAcquire("authority"); }

        private static void RejectReparseAncestors(string path)
        {
            for (var item = new DirectoryInfo(path); item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Private environment directory cannot traverse reparse points.");
        }
    }
}
