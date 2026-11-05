using System;
using System.Collections.Generic;
using System.IO;
using SkylinesLink.Deployment;

namespace SkylinesLink.Setup;

/// <summary>一次安装请求的来源、目标和短锁归属；已持锁动作共用此对象，不嵌套取锁。</summary>
internal sealed class CoreInstallSession : IDisposable
{
    private CorePackage? selected;
    private PreparationFiles? paths;
    private FileStream? setupLock;
    private CoreInstallSession() { }
    internal string EnvironmentId { get; private set; } = "";
    internal string Root { get; private set; } = "";
    internal CorePackage Selected => selected ?? throw new ObjectDisposedException(nameof(CoreInstallSession));
    internal PreparationFiles Paths => setupLock != null && paths != null ? paths : throw new ObjectDisposedException(nameof(CoreInstallSession));

    internal static CoreInstallSession Open(string source, string environment, bool createEnvironment, bool requireComplete, ref string step)
    {
        var session = new CoreInstallSession();
        try
        {
            step = "verify-source";
            session.selected = CorePackage.Open(source);
            if (requireComplete) session.selected.RequireCompletePayload();
            step = "open-environment";
            session.EnvironmentId = CorePackage.NormalizeEnvironment(environment);
            session.Root = CorePackage.EnvironmentRoot(session.EnvironmentId);
            session.paths = new PreparationFiles();
            session.paths.HoldDirectory(session.Root, createEnvironment);
            session.setupLock = session.paths.OpenWritable(Path.Combine(session.Root, "setup.lock"));
            return session;
        }
        catch (Exception failure)
        {
            try { session.Dispose(); }
            catch (Exception closeFailure) { throw new AggregateException(failure, closeFailure); }
            throw;
        }
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        try { selected?.Dispose(); selected = null; }
        catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
        try { paths?.Dispose(); paths = null; }
        catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
        // Keep the lock if another close is incomplete. A later Dispose (or the
        // actual process exit) owns that remaining responsibility.
        if (failures != null) throw new AggregateException("Installation resource close failed.", failures);
        setupLock?.Dispose(); setupLock = null;
    }
}
