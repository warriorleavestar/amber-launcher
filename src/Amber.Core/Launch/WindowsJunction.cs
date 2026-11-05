using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>用目录重解析点建立联接，不经过命令解释器，也不需要符号链接权限。</summary>
internal static class WindowsJunction
{
    internal static void Create(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        link = LocalDirectoryPath(link);
        target = LocalDirectoryPath(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        if (buffer.Length > 16 * 1024) throw new PathTooLongException();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(buffer.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        // CreateDirectory 与 Directory.CreateDirectory 不同：已经存在时必须失败，不能接管旧目录或联接。
        var nativeLink = @"\\?\" + link;
        if (!CreateDirectoryW(nativeLink, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var handle = CreateFileW(nativeLink, 0x40000000 | 0x00010000, 0, IntPtr.Zero, 3,
            0x00200000 | 0x02000000, IntPtr.Zero); // WRITE | DELETE，OPEN_REPARSE_POINT | BACKUP_SEMANTICS
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((attributes.FileAttributes & 0x400) != 0) throw new IOException(L.F("core.game.directory.unsafepath", link));

        if (!DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            // 失败时只通过独占句柄删除刚建的空目录；不会按路径递归删除，也不会跟随联接。
            var delete = new FileDispositionInfo { DeleteFile = 1 };
            SetFileInformationByHandle(handle, 4, ref delete, Marshal.SizeOf<FileDispositionInfo>());
            throw new Win32Exception(error);
        }
    }

    private static string LocalDirectoryPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/') || path.Contains('\0'))
            throw new IOException(L.F("core.game.directory.unsafepath", path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr securityAttributes,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDispositionInfo information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, int size);
}
