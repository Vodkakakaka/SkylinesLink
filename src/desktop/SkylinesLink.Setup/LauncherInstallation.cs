using System;
using System.IO;
using System.Security.Cryptography;
using SkylinesLink.Deployment;

namespace SkylinesLink.Setup;

/// <summary>从本环境已完整准备的Core发布固定入口；不激活Core或配置授权。</summary>
internal static class LauncherInstallation
{
    private const string Payload = "desktop/launcher/SkylinesLink.Launcher.exe";

    public static string Install(string source, string environment)
    {
        var step = "verify-source";
        bool publicationAttempted = false;
        try
        {
            using var session = CoreInstallSession.Open(source, environment, createEnvironment: false, requireComplete: true, ref step);
            step = "verify-prepared";
            using var prepared = CorePreparation.OpenPrepared(session);
            return InstallPrepared(session, prepared, ref step, ref publicationAttempted);
        }
        catch (Exception error) { throw new LauncherInstallationException(step, publicationAttempted, error); }
    }

    internal static string InstallPrepared(CoreInstallSession session, CorePackage prepared, ref string step, ref bool publicationAttempted)
    {
        var paths = session.Paths;
        string target = CorePackage.StableLauncherPath(session.EnvironmentId);
        step = "inspect-launcher";
        paths.HoldDirectory(Path.GetDirectoryName(target)!, true);
        if (Directory.Exists(target)) throw new InvalidDataException("稳定入口路径是目录");
        bool replacing = File.Exists(target);
        if (replacing)
        {
            using var current = paths.OpenRead(target);
            if (Matches(prepared, current)) return "already-installed";
        }
        step = "write-launcher";
        // One inert work slot. Only a new explicit request may rewrite and
        // publish it; ordinary launcher startup never examines this file.
        using var next = paths.OpenWritable(Path.Combine(Path.GetDirectoryName(target)!, "launcher.next"), canRename: true);
        next.SetLength(0); next.Position = 0;
        prepared.CopyFileTo(Payload, next);
        next.Flush(true);
        if (!Matches(prepared, next)) throw new InvalidDataException("稳定入口复制校验失败");
        step = "publish-launcher";
        publicationAttempted = true;
        // A running executable or a reader without delete sharing rejects
        // this ordinary rename. Do not delete the old entry or use POSIX replacement.
        paths.PublishFile(next, target, replaceExisting: replacing);
        return "installed";
    }

    public static string Inspect(string source, string environment)
    {
        using var selected = CorePackage.Open(source);
        selected.RequireCompletePayload();
        var root = CorePackage.EnvironmentRoot(environment);
        using var paths = new PreparationFiles();
        paths.HoldDirectory(root, false);
        using var prepared = CorePreparation.OpenPrepared(root, selected, paths);
        string target = CorePackage.StableLauncherPath(environment);
        if (Directory.Exists(target)) throw new InvalidDataException("稳定入口路径是目录");
        if (!File.Exists(target)) return "not-installed";
        using var current = paths.OpenRead(target);
        return Matches(prepared, current) ? "matches-core" : "different";
    }

    private static bool Matches(CorePackage prepared, FileStream candidate)
    {
        foreach (var item in prepared.Manifest.Files)
        {
            if (!item.Path.Equals(Payload, StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.Length != item.Size) return false;
            candidate.Position = 0;
            return Convert.ToHexStringLower(SHA256.HashData(candidate)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        throw new InvalidDataException("核心未包含稳定入口");
    }
}

internal sealed class LauncherInstallationException : Exception
{
    public LauncherInstallationException(string step, bool publicationAttempted, Exception cause)
        : base("Stable launcher installation failed.", cause)
    { Step = step; PublicationAttempted = publicationAttempted; }
    public string Step { get; }
    public bool PublicationAttempted { get; }
}
