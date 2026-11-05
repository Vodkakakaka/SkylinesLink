using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SkylinesLink.RuntimeProbe
{
    // 只读当前进程权限，用于准确标注测试条件；不创建进程或模拟其他权限。
    internal static class StandardUserProbe
    {
        internal static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    if (!GetTokenInformation(identity.Token, 20, buffer, 4, out int size) || size != 4) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return Marshal.ReadInt32(buffer) != 0;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int informationLength, out int returnLength);
    }
}
