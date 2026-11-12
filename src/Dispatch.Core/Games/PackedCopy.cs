using System.Text;

namespace Dispatch.Core.Games;

/// <summary>
/// A game version kept small: only the data that differs from the Steam install is stored, the rest is shared with it.
/// <para>Folder layout:</para>
/// <code>
/// dispatch-pack.bin    the recipe: every file of the version, as pieces of the files below
/// dispatch-pack.data   the pieces no other file has (what's unique to this version)
/// base\…               hard links to the Steam install's files the recipe uses (no extra space while Steam keeps them)
/// game\…               the full game, rebuilt from the above before playing and removed afterwards
/// </code>
/// Steam updates a file by replacing it, so a base link keeps the old file alive: the recipe keeps working after any
/// update, the old file just starts to take space of its own. Packing again against the new Steam version frees it.
/// </summary>
public static class PackedCopy
{
    public const string RecipeFile = "dispatch-pack.bin";
    public const string DataFile = "dispatch-pack.data";
    public const string BaseFolder = "base";
    public const string GameFolder = "game";
    public const string RebuiltMarker = "dispatch-rebuilt.txt";

    public static bool IsPacked(string folder) => File.Exists(Path.Combine(folder, RecipeFile));

    /// <summary>
    /// Health and space of a packed copy, from file metadata only. Broken when the data file or a base file is missing
    /// or was written to; base files Steam has replaced since (no other name left) count as the copy's own space.
    /// </summary>
    public static CopyHealth Inspect(string folder)
    {
        var recipe = PackRecipe.Read(folder, withFiles: true);
        var changed = new List<string>();
        var data = new FileInfo(Path.Combine(folder, DataFile));
        long own = new FileInfo(Path.Combine(folder, RecipeFile)).Length;
        if (!data.Exists || data.Length != recipe.DataBytes)
        {
            changed.Add(DataFile);
        }
        else
        {
            own += data.Length;
        }

        int shared = 0;
        long stale = 0;
        foreach (var b in recipe.Bases)
        {
            var info = new FileInfo(Path.Combine(folder, BaseFolder, b.Path));
            if (!info.Exists || info.Length != b.Size || info.LastWriteTimeUtc.Ticks != b.WriteTicks)
            {
                changed.Add(Path.Combine(BaseFolder, b.Path));
                continue;
            }

            if (NativeFiles.LinkCount(info.FullName) is > 1)
            {
                shared++;
            }
            else
            {
                stale += b.Size;
            }
        }

        own += stale;
        long total = recipe.Files.Sum(f => f.Size);
        long rebuild = recipe.Files.Where(f => f.WholeBase < 0).Sum(f => f.Size);
        var pack = new PackInfo(recipe.DataBytes, stale, Directory.Exists(Path.Combine(folder, GameFolder)), rebuild);
        return new CopyHealth(true, recipe.Bases.Count, shared, changed, own, total, pack);
    }
}

/// <summary>What a packed copy needs besides its folder.</summary>
/// <param name="DataBytes">The data unique to this version.</param>
/// <param name="StaleBaseBytes">Steam files Steam has since replaced; packing again frees them.</param>
/// <param name="IsRebuilt">The full game folder is there right now (while playing, or left over).</param>
/// <param name="RebuildBytes">Space the rebuilt game folder needs (files that can't be shared).</param>
public sealed record PackInfo(long DataBytes, long StaleBaseBytes, bool IsRebuilt, long RebuildBytes);

/// <summary>A part of a file: <paramref name="Source"/> is a base file's index, or -1 for the data file.</summary>
public readonly record struct PackSegment(int Source, long Offset, long Length);

public sealed record PackBaseFile(string Path, long Size, long WriteTicks);

/// <param name="WholeBase">Index of a base file this file is identical to (rebuilt as a link, not written), or -1.</param>
public sealed record PackedFile(string Path, long Size, long WriteTicks, byte[] Sha256, int WholeBase, IReadOnlyList<PackSegment> Segments);

/// <summary>dispatch-pack.bin: header, base files, then the files with their pieces. Binary, as it can hold ~10⁵ pieces.</summary>
public sealed record PackRecipe(string Game, string? Version, DateTime CreatedUtc, long DataBytes,
                                IReadOnlyList<PackBaseFile> Bases, IReadOnlyList<PackedFile> Files)
{
    private const uint Magic = 0x4B415044; // "DPAK"
    private const int Schema = 1;

    public void Write(string folder)
    {
        using var w = new BinaryWriter(File.Create(Path.Combine(folder, PackedCopy.RecipeFile)), Encoding.UTF8);
        w.Write(Magic);
        w.Write(Schema);
        w.Write(Game);
        w.Write(Version ?? "");
        w.Write(CreatedUtc.Ticks);
        w.Write(DataBytes);
        w.Write(Bases.Count);
        foreach (var b in Bases)
        {
            w.Write(b.Path);
            w.Write(b.Size);
            w.Write(b.WriteTicks);
        }

        w.Write(Files.Count);
        foreach (var f in Files)
        {
            w.Write(f.Path);
            w.Write(f.Size);
            w.Write(f.WriteTicks);
            w.Write(f.Sha256);
            w.Write(f.WholeBase);
            w.Write(f.Segments.Count);
            foreach (var s in f.Segments)
            {
                w.Write(s.Source);
                w.Write(s.Offset);
                w.Write(s.Length);
            }
        }
    }

    /// <param name="withFiles">False reads only the header and base list (quick: for listing and checking).</param>
    public static PackRecipe Read(string folder, bool withFiles = true)
    {
        using var r = new BinaryReader(new FileStream(Path.Combine(folder, PackedCopy.RecipeFile), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16),
                                       Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != Schema)
        {
            throw new InvalidDataException("Not a packed copy Dispatch can read.");
        }

        string game = r.ReadString();
        string version = r.ReadString();
        var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        long dataBytes = r.ReadInt64();
        var bases = new List<PackBaseFile>(r.ReadInt32());
        for (int i = bases.Capacity; i > 0; i--)
        {
            bases.Add(new PackBaseFile(r.ReadString(), r.ReadInt64(), r.ReadInt64()));
        }

        var files = new List<PackedFile>();
        if (withFiles)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = r.ReadString();
                long size = r.ReadInt64();
                long ticks = r.ReadInt64();
                byte[] sha = r.ReadBytes(32);
                int whole = r.ReadInt32();
                var segments = new PackSegment[r.ReadInt32()];
                for (int s = 0; s < segments.Length; s++)
                {
                    segments[s] = new PackSegment(r.ReadInt32(), r.ReadInt64(), r.ReadInt64());
                    if (segments[s].Source >= bases.Count)
                    {
                        throw new InvalidDataException("The packed copy's recipe is damaged.");
                    }
                }

                files.Add(new PackedFile(path, size, ticks, sha, whole, segments));
            }
        }

        return new PackRecipe(game, version.Length == 0 ? null : version, created, dataBytes, bases, files);
    }
}

/// <summary>Open files of a packed copy (its base links and data file), shared by the streams that read it.</summary>
internal sealed class PackSources(string folder, PackRecipe recipe) : IDisposable
{
    private readonly FileStream?[] _bases = new FileStream?[recipe.Bases.Count];
    private FileStream? _data;

    public PackRecipe Recipe => recipe;

    public string BasePath(int index) => Path.Combine(folder, PackedCopy.BaseFolder, recipe.Bases[index].Path);

    public FileStream Get(int source) => source < 0
        ? _data ??= Open(Path.Combine(folder, PackedCopy.DataFile))
        : _bases[source] ??= Open(BasePath(source));

    public Stream OpenFile(PackedFile file) => new PackedFileStream(this, file);

    // Others may read too (Steam, the game); nobody may write while we read.
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);

    public void Dispose()
    {
        _data?.Dispose();
        foreach (var b in _bases)
        {
            b?.Dispose();
        }
    }
}

/// <summary>Reads one file of a packed copy front to back by following its pieces.</summary>
internal sealed class PackedFileStream(PackSources sources, PackedFile file) : Stream
{
    private int _segment;
    private long _within;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => file.Size;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_segment < file.Segments.Count)
        {
            var s = file.Segments[_segment];
            long left = s.Length - _within;
            if (left <= 0)
            {
                _segment++;
                _within = 0;
                continue;
            }

            int n = (int)Math.Min(left, buffer.Length);
            var source = sources.Get(s.Source);
            source.Position = s.Offset + _within;
            int read = source.Read(buffer[..n]);
            if (read == 0)
            {
                throw new IOException($"The packed copy is damaged: a piece of {file.Path} is missing.");
            }

            _within += read;
            _position += read;
            return read;
        }

        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
