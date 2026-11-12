using System.Buffers.Binary;
using System.Text;

namespace Dispatch.Core.Mods.Packages;

/// <summary>
/// ZIP reader that tolerates the deliberate damage found in "protected" mods: it trusts the central
/// directory, ignores broken local headers, and supports ZIP64 (mods can exceed 4 GB).
/// Only stored (0) and deflate (8) entries are readable, which is what the game supports.
/// </summary>
internal sealed class TolerantZipPackage : IModPackage
{
    private const uint EocdSig = 0x06054b50;
    private const uint Eocd64LocatorSig = 0x07064b50;
    private const uint Eocd64Sig = 0x06064b50;
    private const uint CentralSig = 0x02014b50;
    private const uint LocalSig = 0x04034b50;

    private readonly FileStream _stream;
    private readonly Dictionary<string, ZipEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { "" };

    internal readonly record struct ZipEntry(string Name, ushort Method, long CompressedSize, long Size,
                                             long LocalHeaderOffset, ushort CentralNameLength);

    private TolerantZipPackage(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public PackageFormat Format => PackageFormat.Zip;

    public int EntryCount => _files.Count;

    /// <summary>Count of entries whose local header was unusable and had to be worked around.</summary>
    public int RepairedLocalHeaders { get; private set; }

    public IEnumerable<string> FileNames => _files.Keys;

    public IEnumerable<string> FilePaths() => _files.Keys;

    public IEnumerable<ulong> FileHashes() => _files.Keys.Select(ModPackage.PathHash);

    public static TolerantZipPackage Open(string path)
    {
        var fs = ModPackage.OpenShared(path);
        try
        {
            var pkg = new TolerantZipPackage(path, fs);
            pkg.ReadCentralDirectory();
            return pkg;
        }
        catch (Exception ex) when (ex is IOException and not FileNotFoundException and not DirectoryNotFoundException
                                   || ex is ArgumentOutOfRangeException or OverflowException)
        {
            // Forged structures (seen in protected mods) point at impossible positions.
            fs.Dispose();
            throw new InvalidDataException($"ZIP structure is invalid: {ex.Message}", ex);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public byte[]? TryReadFile(string path)
    {
        if (!_files.TryGetValue(ModPackage.Normalize(path), out var e))
        {
            return null;
        }

        long dataOffset = FindDataOffset(e);
        if (e.CompressedSize > int.MaxValue || dataOffset + e.CompressedSize > _stream.Length)
        {
            throw new InvalidDataException($"ZIP entry '{e.Name}' points outside the archive.");
        }

        var raw = new byte[e.CompressedSize];
        _stream.Position = dataOffset;
        _stream.ReadExactly(raw);
        return e.Method switch
        {
            0 => raw,
            8 => ModPackage.Inflate(raw, zlibHeader: false, (int)Math.Min(e.Size, int.MaxValue)),
            _ => throw new InvalidDataException($"ZIP entry '{e.Name}' uses unsupported compression method {e.Method}."),
        };
    }

    public IReadOnlyList<string>? ListDirectory(string path)
    {
        string dir = ModPackage.Normalize(path);
        if (!_directories.Contains(dir))
        {
            return null;
        }

        string prefix = dir.Length == 0 ? "" : dir + "/";
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _files.Keys.Concat(_directories))
        {
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rest = name[prefix.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            result.Add(slash < 0 ? (_directories.Contains(name) ? rest + "/" : rest) : rest[..(slash + 1)]);
        }

        return result.ToList();
    }

    public void Dispose() => _stream.Dispose();

    private void ReadCentralDirectory()
    {
        // The EOCD record is at most 22 + 65535 (comment) bytes from the end.
        long length = _stream.Length;
        int tailSize = (int)Math.Min(length, 22 + 65535 + 20);
        var tail = new byte[tailSize];
        _stream.Position = length - tailSize;
        _stream.ReadExactly(tail);

        int eocd = -1;
        for (int i = tailSize - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSig)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException("ZIP end-of-central-directory record not found.");
        }

        var e = tail.AsSpan(eocd);
        long entries = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);

        // ZIP64: the locator sits just before the EOCD.
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == Eocd64LocatorSig)
        {
            ulong eocd64Offset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            var rec = new byte[56];
            if (eocd64Offset > (ulong)(length - rec.Length))
            {
                throw new InvalidDataException("ZIP64 locator points outside the file.");
            }

            _stream.Position = (long)eocd64Offset;
            if (_stream.ReadAtLeast(rec, rec.Length, throwOnEndOfStream: false) == rec.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(rec) == Eocd64Sig)
            {
                entries = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
                cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
            }
        }

        // Protected archives sometimes falsify the offset; the directory normally ends right at the EOCD.
        long eocdAbsolute = length - tailSize + eocd;
        if (cdOffset < 0 || cdOffset + cdSize > eocdAbsolute || cdSize <= 0)
        {
            cdOffset = eocdAbsolute - cdSize;
        }

        if (cdSize <= 0 || cdSize > 512L * 1024 * 1024 || cdOffset < 0 || entries < 0 || cdOffset + cdSize > length)
        {
            throw new InvalidDataException("ZIP central directory size is implausible.");
        }

        var cd = new byte[cdSize];
        _stream.Position = cdOffset;
        _stream.ReadExactly(cd);
        ParseCentralDirectory(cd, entries);
    }

    private void ParseCentralDirectory(byte[] cd, long declaredEntries)
    {
        int pos = 0;
        while (pos + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos)) == CentralSig)
        {
            var h = cd.AsSpan(pos);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            long csize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            ushort commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            if (pos + 46 + nameLen + extraLen + commentLen > cd.Length)
            {
                break;
            }

            string name = Encoding.UTF8.GetString(h.Slice(46, nameLen)).Replace('\\', '/').TrimStart('/');
            ReadZip64Extra(h.Slice(46 + nameLen, extraLen), ref usize, ref csize, ref localOffset);
            pos += 46 + nameLen + extraLen + commentLen;

            if (name.EndsWith('/'))
            {
                AddDirectory(name.TrimEnd('/'));
                continue;
            }

            if (name.Length == 0)
            {
                continue;
            }

            _files[name] = new ZipEntry(name, method, csize, usize, localOffset, nameLen);
            int slash = name.LastIndexOf('/');
            AddDirectory(slash < 0 ? "" : name[..slash]);
        }

        if (_files.Count == 0 && declaredEntries > 0)
        {
            throw new InvalidDataException("ZIP central directory could not be parsed.");
        }
    }

    private static void ReadZip64Extra(ReadOnlySpan<byte> extra, ref long usize, ref long csize, ref long offset)
    {
        while (extra.Length >= 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (4 + size > extra.Length)
            {
                return;
            }

            if (id == 0x0001)
            {
                var data = extra.Slice(4, size);
                int p = 0;
                if (usize == uint.MaxValue && p + 8 <= data.Length) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); p += 8; }
                if (csize == uint.MaxValue && p + 8 <= data.Length) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); p += 8; }
                if (offset == uint.MaxValue && p + 8 <= data.Length) { offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); }
                return;
            }

            extra = extra[(4 + size)..];
        }
    }

    private void AddDirectory(string dir)
    {
        while (_directories.Add(dir) && dir.Length > 0)
        {
            int slash = dir.LastIndexOf('/');
            dir = slash < 0 ? "" : dir[..slash];
        }
    }

    /// <summary>
    /// Finds where the entry's data starts. A good local header gives the exact offset; a damaged one
    /// (zeroed signature or lengths) is skipped using the central directory's name length.
    /// </summary>
    private long FindDataOffset(ZipEntry e)
    {
        if (e.LocalHeaderOffset < 0 || e.LocalHeaderOffset > _stream.Length - 30 || e.CompressedSize < 0)
        {
            throw new InvalidDataException($"ZIP entry '{e.Name}' has an impossible offset (forged directory?).");
        }

        var local = new byte[30];
        _stream.Position = e.LocalHeaderOffset;
        if (_stream.ReadAtLeast(local, 30, throwOnEndOfStream: false) == 30)
        {
            // Even when the signature is wiped, the length fields are usually intact: use them if plausible.
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
            ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
            long candidate = e.LocalHeaderOffset + 30 + nameLen + extraLen;
            if (nameLen == e.CentralNameLength && candidate + e.CompressedSize <= _stream.Length)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalSig)
                {
                    RepairedLocalHeaders++;
                }

                return candidate;
            }
        }

        RepairedLocalHeaders++;
        return e.LocalHeaderOffset + 30 + e.CentralNameLength;
    }
}
