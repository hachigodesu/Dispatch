using System.IO.Compression;

namespace Dispatch.Core.Mods.Packages;

public enum PackageFormat
{
    Folder,
    Zip,
    HashFsV1,
    HashFsV2,

    /// <summary>Recognised as a file but not readable (unknown or protected container, e.g. "AEM!").</summary>
    Opaque,
}

/// <summary>
/// Read-only view of a mod package. Paths are relative, '/'-separated, without a leading slash
/// ("manifest.sii", "def/world/..."). The root directory is "".
/// </summary>
public interface IModPackage : IDisposable
{
    string Path { get; }

    PackageFormat Format { get; }

    /// <summary>Returns the file's bytes, or null when it doesn't exist in this package.</summary>
    byte[]? TryReadFile(string path);

    /// <summary>
    /// Lists a directory: files as "name", subdirectories as "name/". Null when the directory is unknown
    /// (HashFS archives may deliberately omit listings).
    /// </summary>
    IReadOnlyList<string>? ListDirectory(string path);
}

public static class ModPackage
{
    /// <summary>Opens a mod by inspecting its header, never its extension.</summary>
    public static IModPackage Open(string path)
    {
        if (Directory.Exists(path))
        {
            return new FolderPackage(path);
        }

        Span<byte> header = stackalloc byte[6];
        using (var fs = OpenShared(path))
        {
            if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return new OpaquePackage(path, "file too short");
            }
        }

        if (header[0] == 'P' && header[1] == 'K')
        {
            return TolerantZipPackage.Open(path);
        }

        if (header[0] == 'S' && header[1] == 'C' && header[2] == 'S' && header[3] == '#')
        {
            return HashFsPackage.Open(path);
        }

        // A damaged or disguised start can still hide a ZIP: its central directory sits at the end.
        string headerHex = Convert.ToHexString(header[..4]);
        try
        {
            return TolerantZipPackage.Open(path);
        }
        catch (InvalidDataException)
        {
            return new OpaquePackage(path, $"unknown header {headerHex}");
        }
    }

    /// <summary>
    /// Opens a file read-only while letting other processes (the game, Steam) read, write or delete it.
    /// </summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    internal static byte[] Inflate(byte[] data, bool zlibHeader, int expectedSize)
    {
        using var input = new MemoryStream(data, writable: false);
        using Stream z = zlibHeader
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(expectedSize, 0));
        z.CopyTo(output);
        return output.ToArray();
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

internal sealed class OpaquePackage(string path, string reason) : IModPackage
{
    public string Path { get; } = path;

    public PackageFormat Format => PackageFormat.Opaque;

    public string Reason { get; } = reason;

    public byte[]? TryReadFile(string path) => null;

    public IReadOnlyList<string>? ListDirectory(string path) => null;

    public void Dispose()
    {
    }
}

internal sealed class FolderPackage(string root) : IModPackage
{
    public string Path { get; } = root;

    public PackageFormat Format => PackageFormat.Folder;

    public byte[]? TryReadFile(string path)
    {
        string full = System.IO.Path.Combine(Path, ModPackage.Normalize(path));
        if (!File.Exists(full))
        {
            return null;
        }

        using var fs = ModPackage.OpenShared(full);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    public IReadOnlyList<string>? ListDirectory(string path)
    {
        string full = System.IO.Path.Combine(Path, ModPackage.Normalize(path));
        if (!Directory.Exists(full))
        {
            return null;
        }

        return Directory.EnumerateDirectories(full).Select(d => System.IO.Path.GetFileName(d) + "/")
            .Concat(Directory.EnumerateFiles(full).Select(System.IO.Path.GetFileName))
            .Select(n => n!)
            .ToList();
    }

    public void Dispose()
    {
    }
}
