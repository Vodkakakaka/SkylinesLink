using System;
using System.ComponentModel;
using System.IO;
using SkylinesLink.Deployment;

namespace SkylinesLink.Setup;

/// <summary>首次准备的同锁协调；不替换已有不同/损坏记录，不创建连接授权。</summary>
internal static class CoreInitialization
{
    internal static CoreInitializationResult Initialize(string source, string environment)
    {
        var step = "verify-source";
        bool launcherPublicationAttempted = false, activationPublicationAttempted = false;
        try
        {
            using var session = CoreInstallSession.Open(source, environment, createEnvironment: true, requireComplete: true, ref step);
            step = "inspect-existing-activation";
            using var currentFile = TryOpenActivation(session);
            if (currentFile != null)
            {
                // Keep this actual record pinned across verification/entry repair.
                // It is not rewritten, even when the stable entry needs repair.
                var current = CorePackage.ReadActivationRecord(currentFile);
                if (current.Version != session.Selected.Manifest.Version ||
                    !current.CoreManifestSha256.Equals(session.Selected.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已存在不同活动核心，首次初始化不得切换或回退");
                step = "verify-prepared";
                using var existing = CorePreparation.OpenPrepared(session);
                string entry = LauncherInstallation.InstallPrepared(session, existing, ref step, ref launcherPublicationAttempted);
                return new CoreInitializationResult(entry == "already-installed" ? "already-initialized" : "entry-repaired",
                    launcherPublicationAttempted, false);
            }

            CorePreparation.PrepareSelected(session, ref step);
            step = "verify-prepared";
            using var prepared = CorePreparation.OpenPrepared(session);
            LauncherInstallation.InstallPrepared(session, prepared, ref step, ref launcherPublicationAttempted);
            // No replace on the first publication. Even a writer ignoring our
            // setup lock cannot make this operation overwrite its late record.
            CoreActivation.ActivatePrepared(session, prepared, createOnly: true, ref step, ref activationPublicationAttempted);
            return new CoreInitializationResult("initialized", launcherPublicationAttempted, activationPublicationAttempted);
        }
        catch (Exception error)
        {
            throw new InitializationException(step, launcherPublicationAttempted, activationPublicationAttempted, error);
        }
    }

    private static FileStream? TryOpenActivation(CoreInstallSession session)
    {
        try { return session.Paths.OpenRead(Path.Combine(session.Root, "activation.json")); }
        // Only a missing leaf is absence. Access denied, reparse/hardlink,
        // directories and malformed records must not become a fresh install.
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }
}

internal readonly record struct CoreInitializationResult(string Status, bool LauncherPublicationAttempted, bool ActivationPublicationAttempted);

internal sealed class InitializationException : Exception
{
    internal InitializationException(string step, bool launcherPublicationAttempted, bool activationPublicationAttempted, Exception cause)
        : base("Core initialization failed.", cause)
    { Step = step; LauncherPublicationAttempted = launcherPublicationAttempted; ActivationPublicationAttempted = activationPublicationAttempted; }
    internal string Step { get; }
    internal bool LauncherPublicationAttempted { get; }
    internal bool ActivationPublicationAttempted { get; }
}
