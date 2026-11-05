using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>Read actual process identity without unsupported Mono WindowsIdentity SID properties.</summary>
    internal static class WindowsTokenIdentity
    {
        internal static SecurityIdentifier CurrentUser() => ReadCurrent(1, false);
        internal static SecurityIdentifier CurrentLogon() => ReadCurrent(28, true);

        private static SecurityIdentifier ReadCurrent(int informationClass, bool logon)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 8, out var token))
            {
                int error = Marshal.GetLastWin32Error(); token?.Dispose(); throw new Win32Exception(error);
            }
            using (token)
            {
                GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out int size);
                if (Marshal.GetLastWin32Error() != 122 || size <= 0 || size > 65536)
                    throw new InvalidOperationException("Windows token identity unavailable.");
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, informationClass, buffer, size, out int returned))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    int offset = logon ? checked((int)Marshal.OffsetOf(typeof(TokenGroups), nameof(TokenGroups.FirstGroup))) : 0;
                    int stride = Marshal.SizeOf(typeof(SidAndAttributes));
                    if (returned > size || returned < offset + stride || (logon && Marshal.ReadInt32(buffer) != 1))
                        throw new InvalidOperationException("Windows token identity invalid.");
                    var item = (SidAndAttributes)Marshal.PtrToStructure(IntPtr.Add(buffer, offset), typeof(SidAndAttributes));
                    if (logon && (item.Attributes & 0xc0000000) != 0xc0000000)
                        throw new InvalidOperationException("An interactive logon identity is required.");
                    long available = checked(buffer.ToInt64() + returned - item.Sid.ToInt64());
                    if (item.Sid.ToInt64() < buffer.ToInt64() || available < 8 || !IsValidSid(item.Sid))
                        throw new InvalidOperationException("Windows token SID invalid.");
                    uint length = GetLengthSid(item.Sid);
                    if (length < 8 || length > 68 || length > available)
                        throw new InvalidOperationException("Windows token SID length invalid.");
                    var sid = new byte[length]; Marshal.Copy(item.Sid, sid, 0, sid.Length);
                    return new SecurityIdentifier(sid, 0);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TokenGroups { public uint Count; public SidAndAttributes FirstGroup; }
        private sealed class TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private TokenHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out TokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(TokenHandle token, int informationClass, IntPtr information, int informationLength, out int returnLength);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsValidSid(IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern uint GetLengthSid(IntPtr sid);
    }
}
