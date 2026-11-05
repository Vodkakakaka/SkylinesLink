using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>Create private objects atomically; several Mono BCL overloads ignore their security argument.</summary>
    internal static class WindowsPrivateCreation
    {
        internal static void Directory(string path, DirectorySecurity security)
        {
            using (var descriptor = new Descriptor(security))
                if (!CreateDirectoryW(path, ref descriptor.Attributes)) throw Failure("Private directory creation failed.");
        }

        internal static FileStream File(string path, FileSecurity security)
        {
            SafeFileHandle handle;
            using (var descriptor = new Descriptor(security))
            {
                handle = CreateFileW(path, (uint)FileSystemRights.FullControl, 0, ref descriptor.Attributes, 1, 0x80000080, IntPtr.Zero);
                if (handle.IsInvalid) { var error = Failure("Private file creation failed."); handle.Dispose(); throw error; }
            }
            try { return new OwnedFileStream(handle); }
            catch { handle.Dispose(); throw; }
        }

        internal static void FlushToDisk(FileStream file)
        {
            // Mono FileStream.Flush(true) discards the native flush error. Flush its
            // managed buffer first, then observe the result of the actual disk API.
            file.Flush();
            if (!FlushFileBuffers(file.SafeFileHandle)) throw Failure("Private file flush failed.");
        }

        private static IOException Failure(string message) => new IOException(message,
            unchecked((int)(0x80070000u | (uint)Marshal.GetLastWin32Error())));

        // The supplied SafeHandle remains caller-owned in the game's Mono FileStream.
        // Modern .NET may dispose it too; SafeHandle.Dispose is idempotent.
        private sealed class OwnedFileStream : FileStream
        {
            private readonly SafeFileHandle owned;
            internal OwnedFileStream(SafeFileHandle handle) : base(handle, FileAccess.ReadWrite, 4096, false) { owned = handle; }
            protected override void Dispose(bool disposing)
            { try { base.Dispose(disposing); } finally { owned?.Dispose(); } }
        }

        private sealed class Descriptor : IDisposable
        {
            private GCHandle pinned;
            internal SecurityAttributes Attributes;
            internal Descriptor(FileSystemSecurity security)
            {
                byte[] bytes = security.GetSecurityDescriptorBinaryForm();
                pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), SecurityDescriptor = pinned.AddrOfPinnedObject(), InheritHandle = false };
            }
            public void Dispose() { if (pinned.IsAllocated) pinned.Free(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length; internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes,
            uint creationDisposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushFileBuffers(SafeFileHandle handle);
    }
}
