using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TinyCs;

internal readonly record struct FileIdentity(
    ulong Volume, ulong FileIdLow, ulong FileIdHigh);

internal static class FileIdentityReader
{
    private const int FileNotFound = 2;
    private const int PathComponentNotFound = 20;
    private const int WindowsFileIdInfoClass = 18;

    public static FileIdentity? Get(string path)
    {
        if (OperatingSystem.IsWindows())
            return GetWindows(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return GetUnix(path);
        return null;
    }

    private static FileIdentity? GetWindows(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (handle)
        {
            if (!GetFileInformationByHandleEx(handle,
                    WindowsFileIdInfoClass, out var info,
                    (uint)Marshal.SizeOf<WindowsFileIdInformation>()))
                throw NativeIoException(path, Marshal.GetLastPInvokeError());

            return new FileIdentity(info.VolumeSerialNumber,
                info.FileId.Low, info.FileId.High);
        }
    }

    private static FileIdentity? GetUnix(string path)
    {
        if (GetUnixFileStatus(path, out var status) == 0)
        {
            return new FileIdentity(unchecked((ulong)status.Device),
                unchecked((ulong)status.Inode), 0);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is FileNotFound or PathComponentNotFound)
            return null;
        throw NativeIoException(path, error);
    }

    private static IOException NativeIoException(string path, int error) =>
        new($"cannot inspect file identity for '{path}': " +
            new Win32Exception(error).Message,
            new Win32Exception(error));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int informationClass,
        out WindowsFileIdInformation information, uint bufferSize);

    [DllImport("System.Native", EntryPoint = "SystemNative_Stat",
        SetLastError = true)]
    private static extern int GetUnixFileStatus(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out UnixFileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileIdInformation
    {
        public ulong VolumeSerialNumber;
        public WindowsFileId FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileId
    {
        public ulong Low;
        public ulong High;
    }

    // Cross-Unix layout exported by the targeted .NET 10 System.Native PAL.
    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        public int Flags;
        public int Mode;
        public uint UserId;
        public uint GroupId;
        public long Size;
        public long AccessTime;
        public long AccessTimeNanoseconds;
        public long ModificationTime;
        public long ModificationTimeNanoseconds;
        public long ChangeTime;
        public long ChangeTimeNanoseconds;
        public long BirthTime;
        public long BirthTimeNanoseconds;
        public long Device;
        public long RawDevice;
        public long Inode;
        public uint UserFlags;
    }
}
