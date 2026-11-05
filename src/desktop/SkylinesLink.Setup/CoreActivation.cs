using System;
using System.IO;
using System.Text.Json;
using SkylinesLink.Deployment;
using SkylinesLink.Deployment.Generated;

namespace SkylinesLink.Setup;

/// <summary>显式激活已准备的同源Core；不部署Mod、不配置连接、不恢复遗留槽中的意图。</summary>
internal static class CoreActivation
{
    public static string Activate(string source, string environment)
    {
        var step = "verify-source";
        bool publicationAttempted = false;
        try
        {
            using var session = CoreInstallSession.Open(source, environment, createEnvironment: false, requireComplete: true, ref step);
            step = "verify-prepared";
            using var prepared = CorePreparation.OpenPrepared(session);
            return ActivatePrepared(session, prepared, createOnly: false, ref step, ref publicationAttempted);
        }
        catch (Exception error) { throw new ActivationException(step, publicationAttempted, error); }
    }

    internal static string ActivatePrepared(CoreInstallSession session, CorePackage prepared, bool createOnly,
        ref string step, ref bool publicationAttempted)
    {
        var root = session.Root;
        var paths = session.Paths;
        var activationPath = Path.Combine(root, "activation.json");
        step = "inspect-activation";
        if (Directory.Exists(activationPath)) throw new InvalidDataException("激活记录路径是目录");
        bool replacing = File.Exists(activationPath);
        if (replacing)
        {
            if (createOnly) throw new InvalidDataException("首次初始化期间出现活动记录，不允许覆盖");
            // Close our own read pin before publication. A live game's pin
            // must still block the actual rename, including a reader arriving later.
            using var currentFile = paths.OpenRead(activationPath);
            var current = CorePackage.ReadActivationRecord(currentFile);
            if (current.Version.Equals(prepared.Manifest.Version, StringComparison.OrdinalIgnoreCase))
            {
                if (current.Version != prepared.Manifest.Version ||
                    !current.CoreManifestSha256.Equals(prepared.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("同版活动核心内容冲突");
                return "already-active";
            }
        }
        step = "write-activation";
        // One reserved installer work slot, bounded to the tiny record.
        // Partial leftovers are rewritten only by a new explicit request
        // after all current inputs are revalidated; they are never scheduled work.
        using var next = paths.OpenWritable(Path.Combine(root, "activation.next"), canRename: true);
        next.SetLength(0); next.Position = 0;
        using (var writer = new Utf8JsonWriter(next))
            DeploymentContract.WriteActivationRecord(writer, prepared.CreateActivationRecord());
        next.Flush(true);
        step = "publish-activation";
        publicationAttempted = true;
        paths.PublishFile(next, activationPath, replaceExisting: !createOnly && replacing);
        // No business writes after the commit. Dispose releases the actual
        // record/payload handles; the stable setup lock remains until then.
        return "activated";
    }
}

internal sealed class ActivationException : Exception
{
    public ActivationException(string step, bool publicationAttempted, Exception cause) : base("Core activation failed.", cause)
    { Step = step; PublicationAttempted = publicationAttempted; }
    public string Step { get; }
    public bool PublicationAttempted { get; }
}
