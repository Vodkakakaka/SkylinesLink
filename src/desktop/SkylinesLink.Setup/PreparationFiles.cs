using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Setup;

/// <summary>准备与激活期间固定祖先目录，文件经句柄验证后才允许写入；不改变权限。</summary>
internal sealed class PreparationFiles : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> directories = new(StringComparer.OrdinalIgnoreCase);

    public void HoldDirectory(string path, bool create)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (path.EndsWith(':')) path += Path.DirectorySeparatorChar;
        if (directories.ContainsKey(path)) return;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) HoldDirectory(parent, create);
        if (create && !Directory.Exists(path)) Directory.CreateDirectory(path);
        // 不共享 DELETE：本次准备结束前，已核祖先不能被重命名或替换。
        var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            var info = Inspect(handle);
            if ((info.Attributes & 0x410) != 0x10) throw new InvalidDataException("准备目录不是普通目录");
            directories.Add(path, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public FileStream OpenWritable(string path, bool createNew = false, bool canRename = false)
    {
        HoldDirectory(Path.GetDirectoryName(path)!, true);
        // OPEN_ALWAYS 不截断。先检查实际句柄，再由调用方决定是否重写半写文件。
        var handle = CreateFileW(path, 0xC0000000 | (canRename ? 0x10000u : 0u), 0, IntPtr.Zero, createNew ? 1u : 4u, 0x08200000, IntPtr.Zero);
        try
        {
            var info = Inspect(handle);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1)
                throw new InvalidDataException("准备文件不是普通单链接文件");
            return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024);
        }
        catch { handle.Dispose(); throw; }
    }

    public FileStream OpenRead(string path)
    {
        HoldDirectory(Path.GetDirectoryName(path)!, false);
        var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        try
        {
            var info = Inspect(handle);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1)
                throw new InvalidDataException("安装记录不是普通单链接文件");
            return new FileStream(handle, FileAccess.Read, 4096);
        }
        catch { handle.Dispose(); throw; }
    }

    public void PublishFile(FileStream file, string destination, bool replaceExisting = false)
    {
        destination = Path.GetFullPath(destination);
        HoldDirectory(Path.GetDirectoryName(destination)!, false);
        // 仅将同一个已核句柄改名；载荷路径不移动。普通 rename 尊重目标的在用 pins，
        // 不采用 POSIX 替换或删除旧目标。调用方必须先关闭自己的目标只读句柄。
        // Win32 FILE_RENAME_INFO 要求终止 NUL，FileNameLength 本身不包含它。
        var name = (destination + '\0').ToCharArray();
        var nameOffset = checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FirstName)));
        var size = checked(nameOffset + name.Length * sizeof(char));
        var information = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(information, 0, replaceExisting ? 1 : 0);
            Marshal.WriteIntPtr(information, checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Root))), IntPtr.Zero);
            Marshal.WriteInt32(information, checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.NameBytes))), destination.Length * sizeof(char));
            Marshal.Copy(name, 0, IntPtr.Add(information, nameOffset), name.Length);
            if (!SetFileInformationByHandle(file.SafeFileHandle, 3, information, (uint)size)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(information); }
    }

    private static FileInformation Inspect(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) throw new Win32Exception();
        return information;
    }

    public void Dispose()
    {
        foreach (var handle in directories.Values) handle.Dispose();
        directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint ReplaceIfExists;
        public IntPtr Root;
        public uint NameBytes;
        public char FirstName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
