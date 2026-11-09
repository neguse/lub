using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyCs.Tests;

internal static class FileLinkTestHelper
{
    public static void CreateHardLink(string linkPath, string targetPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(linkPath, targetPath, nint.Zero) ? 0 : -1
            : CreateHardLinkUnix(targetPath, linkPath);

        if (result != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName, string existingFileName, nint securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        string existingPath, string newPath);
}
