using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SkylinesLink.Deployment;

namespace SkylinesLink.Setup;

internal static class CorePreparation
{
    public static string Prepare(string source, string environment)
    {
        var step = "verify-source";
        try { return PrepareCore(source, environment, ref step); }
        catch (Exception error) { throw new PreparationException(step, error); }
    }

    private static string PrepareCore(string source, string environment, ref string step)
    {
        using var session = CoreInstallSession.Open(source, environment, createEnvironment: true, requireComplete: false, ref step);
        return PrepareSelected(session, ref step);
    }

    internal static string PrepareSelected(CoreInstallSession session, ref string step)
    {
        var package = session.Selected;
        var paths = session.Paths;
        var root = session.Root;
        step = "inspect-release";
        var releases = Path.Combine(root, "releases");
        paths.HoldDirectory(releases, true);
        var destination = CorePackage.ResolveCorePath(releases, package.Manifest.Version);
        paths.HoldDirectory(destination, true);
        var publishedManifest = Path.Combine(destination, "core-manifest.json");
        if (File.Exists(publishedManifest))
        {
            ValidateTree(destination, package, paths, preparing: false);
            using var existing = CorePackage.Open(destination, package.ManifestSha256);
            return "already-prepared";
        }

        // 自报清单不能扩大恢复归属；仅允许本次已核源清单中的路径。
        step = "inspect-preparation";
        ValidateTree(destination, package, paths, preparing: true);
        var preparationPath = Path.Combine(destination, "preparation-manifest.json");
        var hasPreparation = File.Exists(preparationPath);
        if (!hasPreparation)
        {
            using var entries = Directory.EnumerateFileSystemEntries(destination).GetEnumerator();
            if (entries.MoveNext())
                throw new InvalidDataException("缺少准备清单但暂存目录非空");
        }
        using var manifest = paths.OpenWritable(preparationPath, createNew: !hasPreparation, canRename: true);
        step = "write-preparation-manifest";
        if (!hasPreparation)
        {
            package.CopyManifestTo(manifest);
            manifest.Flush(true);
        }
        else
        {
            if (manifest.Length > 1024 * 1024 || !HashMatches(manifest, package.ManifestSha256))
                throw new InvalidDataException("准备清单损坏或不匹配");
        }

        foreach (var file in package.Manifest.Files)
        {
            step = "copy-payload";
            var target = CorePackage.ResolveCorePath(destination, file.Path);
            using var output = paths.OpenWritable(target);
            if (output.Length == file.Size && HashMatches(output, file.Sha256)) continue;
            output.SetLength(0);
            output.Position = 0;
            package.CopyFileTo(file.Path, output);
            output.Flush(true);
        }
        step = "verify-preparation";
        ValidateTree(destination, package, paths, preparing: true);
        using var verified = CorePackage.OpenPreparing(destination, manifest, package.ManifestSha256);
        // 完成标记最后发布。所有载荷 pins 和原准备清单句柄贯穿此步骤，不移动载荷目录。
        step = "publish-manifest";
        paths.PublishFile(manifest, publishedManifest);
        step = "verify-ready-tree";
        ValidateTree(destination, package, paths, preparing: false);
        return "prepared";
    }

    internal static CorePackage OpenPrepared(CoreInstallSession session) => OpenPrepared(session.Root, session.Selected, session.Paths);

    internal static CorePackage OpenPrepared(string root, CorePackage selected, PreparationFiles paths)
    {
        string directory = CorePackage.ResolveCorePath(Path.Combine(root, "releases"), selected.Manifest.Version);
        paths.HoldDirectory(directory, false);
        ValidateTree(directory, selected, paths, preparing: false);
        var prepared = CorePackage.Open(directory, selected.ManifestSha256);
        try
        {
            prepared.RequireCompletePayload();
            if (prepared.Manifest.Version != selected.Manifest.Version) throw new InvalidDataException("准备目录版本不匹配");
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    private static bool HashMatches(FileStream file, string expected)
    {
        file.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateTree(string root, CorePackage package, PreparationFiles paths, bool preparing)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { preparing ? "preparation-manifest.json" : "core-manifest.json" };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Manifest.Files)
        {
            files.Add(file.Path.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(file.Path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        var pending = new Stack<string>();
        pending.Push(root);
        var observed = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            paths.HoldDirectory(directory, false);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++observed > files.Count + directories.Count) throw new InvalidDataException("准备目录存在额外内容");
                var relative = Path.GetRelativePath(root, item);
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("准备路径含重解析点");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Contains(relative)) throw new InvalidDataException("准备目录存在额外内容");
                    pending.Push(item);
                }
                else if (!files.Contains(relative)) throw new InvalidDataException("准备目录存在额外内容");
            }
        }
    }
}

internal sealed class PreparationException : Exception
{
    public PreparationException(string step, Exception cause) : base("Core preparation failed.", cause) => Step = step;
    public string Step { get; }
}
