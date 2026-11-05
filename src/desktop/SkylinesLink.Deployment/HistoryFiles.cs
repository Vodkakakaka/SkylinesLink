using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Deployment;

/// <summary>只读固定连接目录；句柄贯穿检查与 capture，不创建或修复任何对象。</summary>
[SupportedOSPlatform("windows")]
internal sealed class HistoryFiles : IDisposable
{
    private readonly SecurityIdentifier user;
    private readonly SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
    private readonly List<Pin> directories = new();
    private readonly List<(FileStream Stream, Pin Pin)> files = new();
    private readonly char[] finalPath = new char[4096];
    private readonly string connection;

    internal HistoryFiles(string environment)
    {
        connection = Path.Combine(CorePackage.EnvironmentRoot(environment), "connection");
        using var identity = WindowsIdentity.GetCurrent();
        user = identity.User ?? throw new HistoryReadException(HistoryReadCode.CorruptStore);
        try { HoldDirectory(connection); }
        catch { Dispose(); throw; }
    }

    internal FileStream OpenAuthorityLock() => OpenFile("authority.lock", 0, HistoryReadCode.StoreMissing, true);
    internal FileStream OpenAuthorization() => OpenFile("authorization.json", 1, HistoryReadCode.StoreMissing, false);
    internal FileStream OpenHistory() => OpenFile("operations.history", 1, HistoryReadCode.HistoryMissing, false);

    private void HoldDirectory(string path, int depth = 0)
    {
        path = Normalize(path);
        if (path.Length >= finalPath.Length || depth >= 64) throw Unsafe();
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null) HoldDirectory(parent, depth + 1);
        bool isPrivate = path.Equals(connection, StringComparison.OrdinalIgnoreCase);
        // No DELETE sharing: checked ancestors cannot be renamed while their descendants are captured.
        var handle = CreateFileW(path, isPrivate ? 0x00020080u : 0u, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            CheckOpen(handle, HistoryReadCode.StoreMissing, false);
            var pin = new Pin(path, handle, Inspect(handle), isPrivate);
            CheckIdentity(pin, true);
            if (isPrivate) CheckDirectorySecurity(handle);
            directories.Add(pin);
        }
        catch { handle.Dispose(); throw; }
    }

    private FileStream OpenFile(string name, uint sharing, HistoryReadCode missing, bool authorityLock)
    {
        string path = Path.Combine(connection, name);
        // OPEN_EXISTING + OPEN_REPARSE_POINT + OVERLAPPED. Never truncate or follow the final link.
        var handle = CreateFileW(path, 0x80000000, sharing, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
        try
        {
            CheckOpen(handle, missing, authorityLock);
            var pin = new Pin(path, handle, Inspect(handle), true);
            CheckIdentity(pin, false);
            if (!handle.IsAsync) throw Unsafe();
            var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
            try
            {
                CheckSecurity(stream.GetAccessControl());
                files.Add((stream, pin));
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal void Recheck()
    {
        foreach (var pin in directories)
        {
            CheckIdentity(pin, true);
            if (pin.Private) CheckDirectorySecurity(pin.Handle);
        }
        foreach (var file in files)
        {
            CheckIdentity(file.Pin, false);
            CheckSecurity(file.Stream.GetAccessControl());
        }
    }

    private static void CheckOpen(SafeFileHandle handle, HistoryReadCode missing, bool authorityLock)
    {
        if (!handle.IsInvalid) return;
        int error = Marshal.GetLastPInvokeError();
        if (authorityLock && error is 32 or 33) throw new HistoryReadException(HistoryReadCode.AuthorityBusy);
        if (error is 2 or 3) throw new HistoryReadException(missing);
        throw Unsafe();
    }

    private void CheckIdentity(Pin pin, bool directory)
    {
        var actual = Inspect(pin.Handle);
        if ((actual.Attributes & 0x400) != 0 || ((actual.Attributes & 0x10) != 0) != directory ||
            (!directory && actual.Links != 1) || actual.Volume != pin.Info.Volume || actual.IndexHigh != pin.Info.IndexHigh ||
            actual.IndexLow != pin.Info.IndexLow || actual.CreationHigh != pin.Info.CreationHigh || actual.CreationLow != pin.Info.CreationLow ||
            (!directory && (actual.SizeHigh != pin.Info.SizeHigh || actual.SizeLow != pin.Info.SizeLow))) throw Unsafe();
        uint length = GetFinalPathNameByHandleW(pin.Handle, finalPath, (uint)finalPath.Length, 0);
        if (length == 0 || length >= finalPath.Length) throw Unsafe();
        string resolved = new(finalPath, 0, checked((int)length));
        if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) resolved = "\\\\" + resolved[8..];
        else if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!Normalize(resolved).Equals(pin.Path, StringComparison.OrdinalIgnoreCase)) throw Unsafe();
    }

    private static FileInformation Inspect(SafeFileHandle handle)
    {
        if (handle.IsInvalid || GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var information)) throw Unsafe();
        return information;
    }

    private void CheckDirectorySecurity(SafeFileHandle handle)
    {
        // DirectoryInfo ACL APIs resolve a path again; this narrow call reads the already pinned directory.
        uint error = GetSecurityInfo(handle, 1, 5, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0) throw Unsafe();
        try
        {
            uint size = GetSecurityDescriptorLength(descriptor);
            if (size == 0 || size > 65536) throw Unsafe();
            byte[] bytes = new byte[checked((int)size)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access | AccessControlSections.Owner);
            CheckSecurity(security);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    private void CheckSecurity(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !user.Equals(descriptor.Owner) || descriptor.DiscretionaryAcl is null) throw Unsafe();
        bool allowed = false;
        foreach (GenericAce raw in descriptor.DiscretionaryAcl)
        {
            if (raw is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed ||
                (!user.Equals(ace.SecurityIdentifier) && !system.Equals(ace.SecurityIdentifier))) throw Unsafe();
            if (user.Equals(ace.SecurityIdentifier) && (ace.AceFlags & AceFlags.InheritOnly) == 0 &&
                (ace.AccessMask & (int)FileSystemRights.FullControl) == (int)FileSystemRights.FullControl) allowed = true;
        }
        if (!allowed) throw Unsafe();
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length == Path.GetPathRoot(full)!.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar);
    }
    private static HistoryReadException Unsafe() => new(HistoryReadCode.CorruptStore);
    public void Dispose()
    {
        // The authority lease was acquired first and must outlive every pinned data file.
        for (int i = files.Count - 1; i >= 0; i--) files[i].Stream.Dispose();
        files.Clear();
        for (int i = directories.Count - 1; i >= 0; i--) directories[i].Handle.Dispose();
        directories.Clear();
    }
    private sealed record Pin(string Path, SafeFileHandle Handle, FileInformation Info, bool Private);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
