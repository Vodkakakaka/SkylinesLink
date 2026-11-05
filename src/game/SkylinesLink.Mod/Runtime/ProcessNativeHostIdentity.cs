using System;
using System.Diagnostics;
using SkylinesLink.Application.Operations;

namespace SkylinesLink.Mod.Runtime
{
    /// <summary>只观察进程生存身份，不启动、终止或控制其他进程。</summary>
    public sealed class ProcessNativeHostIdentity : INativeHostIdentitySource
    {
        public ProcessNativeHostIdentity()
        { using (var process = Process.GetCurrentProcess()) Current = new NativeHostIdentity(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc()); }
        public NativeHostIdentity Current { get; }
        public NativeHostState Observe(NativeHostIdentity identity)
        {
            try
            {
                using (var process = Process.GetProcessById(identity.ProcessId))
                    return process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != identity.CreationTime ? NativeHostState.Ended : NativeHostState.Alive;
            }
            catch (ArgumentException) { return NativeHostState.Ended; }
            catch (InvalidOperationException) { return NativeHostState.Unknown; }
            catch (System.ComponentModel.Win32Exception) { return NativeHostState.Unknown; }
        }
    }
}
