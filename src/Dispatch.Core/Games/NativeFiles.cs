using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dispatch.Core.Games;

/// <summary>The Windows file calls .NET doesn't offer: making a hard link, counting a file's links, comparing identities.</summary>
internal static partial class NativeFiles
{
    /// <summary>Makes <paramref name="link"/> a second name for <paramref name="existing"/> (same NTFS volume only).</summary>
    public static bool TryCreateHardLink(string link, string existing) => CreateHardLink(link, existing, IntPtr.Zero);

    /// <summary>How many names the file has (1 = not shared); null if it can't be read.</summary>
    public static int? LinkCount(string path)
    {
        try
        {
            // Share everything so a running Steam or game isn't disturbed.
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when both names are links to the same file (same volume and file index); false when they aren't or either
    /// can't be read.
    /// </summary>
    public static bool SameFile(string a, string b) => FileId(a) is { } x && FileId(b) is { } y && x == y;

    private static (uint Volume, ulong Index)? FileId(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
