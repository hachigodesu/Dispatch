using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dispatch.Core.Mods.Packages;

/// <summary>
/// Reader for SCS HashFS archives, versions 1 (game ≤ 1.49) and 2 (1.50+).
/// Files are looked up by CityHash64 of their path, so names are optional; directory listings,
/// when present, give names. Written from the public layout description; see docs/file-formats.md.
/// </summary>
internal sealed class HashFsPackage : IModPackage
{
    private const uint Magic = 0x23534353; // "SCS#"

    // v1 entry flags
    private const uint V1Directory = 1 << 0;
    private const uint V1Compressed = 1 << 1;
    private const uint V1Encrypted = 1 << 3;

    // v2 metadata chunk types
    private const byte ChunkPlain = 128;
    private const byte ChunkDirectory = 129;

    private readonly FileStream _stream;
    private readonly ushort _salt;
    private readonly Dictionary<ulong, List<Entry>> _entries = [];
    private uint[] _metadata = [];
    private readonly Dictionary<ulong, (uint MetaIndex, ushort MetaCount, ushort Flags)> _v2Raw = [];

    /// <summary>One archive entry. For v2, the main metadata has been resolved into Offset/sizes.</summary>
    internal readonly record struct Entry(ulong Hash, long Offset, uint Size, uint CompressedSize,
                                          bool IsDirectory, bool IsCompressed, bool IsEncrypted);

    private HashFsPackage(string path, FileStream stream, ushort version, ushort salt)
    {
        Path = path;
        _stream = stream;
        Version = version;
        _salt = salt;
    }

    public string Path { get; }

    public ushort Version { get; }

    public PackageFormat Format => Version == 1 ? PackageFormat.HashFsV1 : PackageFormat.HashFsV2;

    public int EntryCount => _entries.Values.Sum(l => l.Count);

    /// <summary>All path hashes in the archive (used later for conflict detection).</summary>
    public IEnumerable<ulong> Hashes => _entries.Keys;

    /// <summary>True when the table offset in the header was wrong and the table was found at the end.</summary>
    public bool UsedTableAtEndFallback { get; private set; }

    public static HashFsPackage Open(string path)
    {
        var fs = ModPackage.OpenShared(path);
        try
        {
            Span<byte> h = stackalloc byte[12];
            fs.ReadExactly(h);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic)
            {
                throw new InvalidDataException("Not a HashFS archive.");
            }

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            ushort salt = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            if (Encoding.ASCII.GetString(h[8..12]) != "CITY")
            {
                throw new InvalidDataException("Unsupported HashFS hash method.");
            }

            var pkg = new HashFsPackage(path, fs, version, salt);
            switch (version)
            {
                case 1: pkg.ReadV1Tables(); break;
                case 2: pkg.ReadV2Tables(); break;
                default: throw new InvalidDataException($"Unsupported HashFS version {version}.");
            }

            return pkg;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public ulong HashPath(string path)
    {
        string p = ModPackage.Normalize(path);
        if (_salt != 0)
        {
            p = _salt.ToString(CultureInfo.InvariantCulture) + p;
        }

        return CityHash.Hash64(p);
    }

    public byte[]? TryReadFile(string path)
    {
        if (!_entries.TryGetValue(HashPath(path), out var list))
        {
            return null;
        }

        var entry = list.FirstOrDefault(e => !e.IsDirectory);
        return entry.Hash == 0 && entry.Size == 0 && entry.Offset == 0 ? null : ReadEntry(entry);
    }

    public IReadOnlyList<string>? ListDirectory(string path)
    {
        if (!_entries.TryGetValue(HashPath(path), out var list) || !list.Any(e => e.IsDirectory))
        {
            return null;
        }

        var names = new List<string>();
        foreach (var entry in list.Where(e => e.IsDirectory))
        {
            byte[]? data = ReadEntry(entry);
            if (data is null)
            {
                continue;
            }

            names.AddRange(Version == 1 ? ParseV1Listing(data) : ParseV2Listing(data));
        }

        return names;
    }

    public void Dispose() => _stream.Dispose();

    private byte[]? ReadEntry(Entry e)
    {
        if (e.IsEncrypted)
        {
            return null;
        }

        int stored = (int)(e.IsCompressed ? e.CompressedSize : e.Size);
        if (e.Offset < 0 || e.Offset + stored > _stream.Length)
        {
            throw new InvalidDataException("HashFS entry points outside the archive.");
        }

        var raw = new byte[stored];
        _stream.Position = e.Offset;
        _stream.ReadExactly(raw);
        return e.IsCompressed ? ModPackage.Inflate(raw, zlibHeader: true, (int)e.Size) : raw;
    }

    // ---- v1 -----------------------------------------------------------------------------------

    private void ReadV1Tables()
    {
        Span<byte> h = stackalloc byte[12];
        _stream.Position = 12;
        _stream.ReadExactly(h);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h);
        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[4..]);
        long tableSize = (long)count * 32;
        if (offset < 24 || offset + tableSize > _stream.Length)
        {
            // Some protected archives lie about the table offset; the table then sits at the end.
            offset = _stream.Length - tableSize;
            UsedTableAtEndFallback = true;
        }

        var table = new byte[tableSize];
        _stream.Position = offset;
        _stream.ReadExactly(table);
        for (int i = 0; i < count; i++)
        {
            var r = table.AsSpan(i * 32, 32);
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(r);
            long entryOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[8..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
            uint csize = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
            Add(new Entry(hash, entryOffset, size, csize, (flags & V1Directory) != 0,
                          (flags & V1Compressed) != 0, (flags & V1Encrypted) != 0));
        }
    }

    private static IEnumerable<string> ParseV1Listing(byte[] data) =>
        Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.StartsWith('*') ? line[1..] + "/" : line);

    // ---- v2 -----------------------------------------------------------------------------------

    private void ReadV2Tables()
    {
        Span<byte> h = stackalloc byte[37];
        _stream.Position = 12;
        _stream.ReadExactly(h);
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(h);
        uint entryTableLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        uint metadataCount = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        uint metadataTableLength = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        long entryTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[16..]);
        long metadataTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[24..]);

        byte[] entries = ReadCompressedTable(entryTableOffset, entryTableLength, (int)entryCount * 16);
        byte[] meta = ReadCompressedTable(metadataTableOffset, metadataTableLength, (int)metadataCount * 4);
        _metadata = new uint[meta.Length / 4];
        for (int i = 0; i < _metadata.Length; i++)
        {
            _metadata[i] = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(i * 4));
        }

        for (int i = 0; i < entryCount; i++)
        {
            var r = entries.AsSpan(i * 16, 16);
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(r);
            uint metaIndex = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
            ushort metaCount = BinaryPrimitives.ReadUInt16LittleEndian(r[12..]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(r[14..]);
            _v2Raw[hash] = (metaIndex, metaCount, flags);
            if (TryResolveV2(metaIndex, metaCount, out var entry))
            {
                Add(entry with { Hash = hash, IsDirectory = entry.IsDirectory || (flags & 1) != 0 });
            }
            else
            {
                // Textures and other chunked objects: keep the hash (for conflict detection) without data.
                Add(new Entry(hash, -1, 0, 0, IsDirectory: false, IsCompressed: false, IsEncrypted: true));
            }
        }
    }

    private bool TryResolveV2(uint metaIndex, ushort metaCount, out Entry entry)
    {
        for (int i = 0; i < metaCount; i++)
        {
            uint header = _metadata[metaIndex + i];
            byte type = (byte)(header >> 24);
            if (type is not (ChunkPlain or ChunkDirectory))
            {
                continue;
            }

            // Low 24 bits: absolute word index of this chunk's data. Main (plain/directory) data is 4 words:
            // [compressed size:28 | flags:4] [size:28 | ?:4] [unknown] [offset in 16-byte blocks].
            uint idx = header & 0x00FFFFFF;
            if (idx + 3 >= _metadata.Length)
            {
                break;
            }

            uint w0 = _metadata[idx], w1 = _metadata[idx + 1], w3 = _metadata[idx + 3];
            uint csize = w0 & 0x0FFFFFFF;
            bool compressed = (w0 >> 28) != 0;
            uint size = w1 & 0x0FFFFFFF;
            long offset = (long)w3 * 16;
            entry = new Entry(0, offset, size, compressed ? csize : size, type == ChunkDirectory, compressed, false);
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Diagnostics for the spike: raw metadata words of the entry for a path.</summary>
    internal string DescribeV2(string path)
    {
        var sb = new StringBuilder();
        ulong hash = HashPath(path);
        sb.Append(CultureInfo.InvariantCulture, $"hash {hash:X16}: ");
        if (!_entries.TryGetValue(hash, out var list))
        {
            return sb.Append("not found").ToString();
        }

        if (_v2Raw.TryGetValue(hash, out var raw))
        {
            sb.Append(CultureInfo.InvariantCulture, $"metaIndex {raw.MetaIndex} count {raw.MetaCount} flags {raw.Flags:X4} words:");
            for (uint i = raw.MetaIndex; i < Math.Min(raw.MetaIndex + 10, (uint)_metadata.Length); i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {_metadata[i]:X8}");
            }

            sb.Append(' ');
        }

        foreach (var e in list)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[off {e.Offset} size {e.Size} csize {e.CompressedSize} dir {e.IsDirectory} z {e.IsCompressed}] ");
        }

        return sb.ToString();
    }

    private static List<string> ParseV2Listing(byte[] data)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int lengths = 4;
        int pos = 4 + (int)count;
        var names = new List<string>((int)count);
        for (int i = 0; i < count; i++)
        {
            int len = data[lengths + i];
            string name = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            names.Add(name.StartsWith('/') ? name[1..] + "/" : name);
        }

        return names;
    }

    private byte[] ReadCompressedTable(long offset, uint length, int expected)
    {
        if (offset < 0 || offset + length > _stream.Length)
        {
            throw new InvalidDataException("HashFS v2 table outside the archive.");
        }

        var raw = new byte[length];
        _stream.Position = offset;
        _stream.ReadExactly(raw);
        return ModPackage.Inflate(raw, zlibHeader: true, expected);
    }

    private void Add(Entry e)
    {
        if (!_entries.TryGetValue(e.Hash, out var list))
        {
            _entries[e.Hash] = list = new List<Entry>(1);
        }

        list.Add(e);
    }
}
