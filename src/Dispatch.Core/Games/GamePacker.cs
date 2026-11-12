using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Dispatch.Core.Platform;

namespace Dispatch.Core.Games;

public enum PackPhase
{
    /// <summary>Sharing the Steam install's files (hard links) and reading them.</summary>
    Indexing,

    /// <summary>Reading the copy and storing what Steam doesn't have.</summary>
    Packing,

    /// <summary>Reading the packed copy back and comparing it with the original, file by file.</summary>
    Checking,
}

public readonly record struct PackProgress(PackPhase Phase, long Done, long Total);

public sealed record PackResult(long DataBytes, int Files, int BaseFiles, TimeSpan Took);

/// <summary>
/// Packs a copy (see <see cref="PackedCopy"/>), packs a packed copy again against a newer Steam install, and rebuilds
/// the playable game folder from it.
/// <para>
/// Safety: the packed copy is built next to the copy (&lt;copy&gt;.dispatch-packing) and read back completely; every
/// file must match the original byte for byte (SHA-256) before the original is replaced. Steam's files are only read
/// and linked, never written: Dispatch never changes a file's content or times through a link.
/// </para>
/// </summary>
public static class GamePacker
{
    public const string StagingSuffix = ".dispatch-packing";
    public const string OldSuffix = ".dispatch-unpacked";
    private const string PartialSuffix = ".dispatch-partial";
    private const long CheckSpaceEvery = 256L << 20;

    private readonly record struct Location(int Source, long Offset, int Length);

    /// <summary>
    /// Packs <paramref name="copyFolder"/> (a full copy, or a packed copy to pack again) against the Steam install and
    /// puts the packed copy in its place. Throws InvalidOperationException (with the reason) when it can't start.
    /// </summary>
    /// <param name="commit">False: pack and check, then throw the packed copy away (a dry run; the copy stays as it is).</param>
    public static PackResult Pack(IPlatform platform, GameDefinition game, string copyFolder, string steamFolder, string? version,
                                  IProgress<PackProgress>? progress, CancellationToken cancel, bool commit = true)
    {
        copyFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyFolder));
        steamFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamFolder));
        if (!File.Exists(Path.Combine(steamFolder, game.ExeRelativePath)))
        {
            throw new InvalidOperationException($"{steamFolder} doesn't contain {game.ExeRelativePath}.");
        }

        if (GameCopier.IsInside(copyFolder, steamFolder) || GameCopier.IsInside(steamFolder, copyFolder))
        {
            throw new InvalidOperationException("The copy must be outside the game's install folder.");
        }

        if (!GameCopier.CanShare(steamFolder, copyFolder) || !GameCopier.ProbeLink(Path.Combine(steamFolder, game.ExeRelativePath), copyFolder))
        {
            throw new InvalidOperationException("Packing needs the copy on the same NTFS drive as the Steam install.");
        }

        ThrowIfRunning(platform, game);
        var watch = Stopwatch.StartNew();
        bool wasPacked = PackedCopy.IsPacked(copyFolder);
        if (wasPacked)
        {
            RemoveRebuilt(copyFolder);
        }

        using var source = Source.Open(copyFolder, wasPacked);
        if (!source.Files.Any(f => string.Equals(f.Path, game.ExeRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{copyFolder} doesn't contain {game.ExeRelativePath}.");
        }

        version ??= source.Version;
        string staging = copyFolder + StagingSuffix;
        GameCopier.TryDelete(staging);
        if (Directory.Exists(staging))
        {
            throw new IOException($"{staging} is left from an earlier attempt and can't be removed.");
        }

        try
        {
            Directory.CreateDirectory(staging);
            var bases = LinkSteamFiles(steamFolder, Path.Combine(staging, PackedCopy.BaseFolder), cancel);
            var chunker = new ContentChunker();
            var index = IndexBases(staging, bases, chunker, progress, cancel);

            var used = new bool[bases.Count];
            var files = new List<PackedFile>(source.Files.Count);
            long dataBytes = 0;
            long totalBytes = source.Files.Sum(f => f.Size);
            long done = 0;
            string root = Path.GetPathRoot(staging)!;
            using (var data = new FileStream(Path.Combine(staging, PackedCopy.DataFile), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var own = new Dictionary<UInt128, Location>();
                long sinceCheck = 0;
                foreach (var file in source.Files)
                {
                    ThrowIfRunning(platform, game);
                    var segments = new List<PackSegment>();
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long read = 0;
                    using (var input = source.OpenFile(file))
                    {
                        chunker.Split(input, chunk =>
                        {
                            sha.AppendData(chunk);
                            read += chunk.Length;
                            var key = Key(chunk);
                            if (!index.TryGetValue(key, out var at) && !own.TryGetValue(key, out at))
                            {
                                at = new Location(-1, dataBytes, chunk.Length);
                                own[key] = at;
                                data.Write(chunk);
                                dataBytes += chunk.Length;
                                sinceCheck += chunk.Length;
                            }

                            if (at.Source >= 0)
                            {
                                used[at.Source] = true;
                            }

                            Append(segments, new PackSegment(at.Source, at.Offset, at.Length));
                        }, cancel);
                    }

                    if (read != file.Size)
                    {
                        throw new IOException($"{file.Path} changed while it was packed.");
                    }

                    if (sinceCheck > CheckSpaceEvery)
                    {
                        sinceCheck = 0;
                        long free = new DriveInfo(root).AvailableFreeSpace;
                        if (free < GameCopier.Headroom)
                        {
                            throw new InsufficientSpaceException(root, GameCopier.Headroom, free);
                        }
                    }

                    int whole = segments is [{ Source: >= 0, Offset: 0 } only] && only.Length == file.Size && bases[only.Source].Size == file.Size
                        ? only.Source
                        : -1;
                    files.Add(new PackedFile(file.Path, file.Size, file.WriteTicks, sha.GetHashAndReset(), whole, segments));
                    done += file.Size;
                    progress?.Report(new PackProgress(PackPhase.Packing, done, totalBytes));
                }
            }

            // Keep only the Steam files the recipe uses, and renumber them.
            var keep = new List<PackBaseFile>();
            var renumber = new int[bases.Count];
            for (int i = 0; i < bases.Count; i++)
            {
                if (used[i])
                {
                    renumber[i] = keep.Count;
                    keep.Add(bases[i]);
                }
                else
                {
                    renumber[i] = -1;
                    File.Delete(Path.Combine(staging, PackedCopy.BaseFolder, bases[i].Path)); // only this name; Steam's file stays
                }
            }

            files = files.Select(f => f with
            {
                WholeBase = f.WholeBase < 0 ? -1 : renumber[f.WholeBase],
                Segments = f.Segments.Select(s => s.Source < 0 ? s : s with { Source = renumber[s.Source] }).ToArray(),
            }).ToList();
            var recipe = new PackRecipe(game.Key, version, DateTime.UtcNow, dataBytes, keep, files);
            recipe.Write(staging);

            Check(staging, totalBytes, progress, cancel);
            source.Dispose();
            var result = new PackResult(dataBytes, files.Count, keep.Count, watch.Elapsed);
            if (!commit)
            {
                GameCopier.TryDelete(staging);
                return result;
            }

            ThrowIfRunning(platform, game);
            Commit(copyFolder, staging);
            return result;
        }
        catch
        {
            GameCopier.TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the game folder of a packed copy (or reuses one that's already complete) and returns it. Files equal to
    /// a Steam file become links; the others are written and checked against their SHA-256.
    /// </summary>
    public static string Rebuild(IPlatform platform, GameDefinition game, string packedFolder, IProgress<(long Done, long Total)>? progress,
                                 CancellationToken cancel)
    {
        var recipe = PackRecipe.Read(packedFolder);
        string target = Path.Combine(packedFolder, PackedCopy.GameFolder);
        string marker = recipe.CreatedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsComplete(target, recipe, marker))
        {
            return target;
        }

        ThrowIfRunning(platform, game);
        var health = PackedCopy.Inspect(packedFolder);
        if (health.IsBroken)
        {
            throw new InvalidDataException($"The packed copy is damaged: {health.Changed[0]} changed or is missing.");
        }

        RemoveRebuilt(packedFolder);
        string root = Path.GetPathRoot(target)!;
        long needed = health.Pack!.RebuildBytes + GameCopier.Headroom;
        long free = new DriveInfo(root).AvailableFreeSpace;
        if (free < needed)
        {
            throw new InsufficientSpaceException(root, needed, free);
        }

        string partial = target + PartialSuffix;
        long total = recipe.Files.Sum(f => f.Size);
        long done = 0;
        try
        {
            Directory.CreateDirectory(partial);
            using var sources = new PackSources(packedFolder, recipe);
            var buffer = new byte[1 << 20];
            foreach (var file in recipe.Files)
            {
                cancel.ThrowIfCancellationRequested();
                ThrowIfRunning(platform, game);
                string path = Path.Combine(partial, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (file.WholeBase < 0 || !NativeFiles.TryCreateHardLink(path, sources.BasePath(file.WholeBase)))
                {
                    WriteFile(sources, file, path, buffer, ref done, total, progress, cancel);
                }
                else
                {
                    done += file.Size;
                    progress?.Report((done, total));
                }
            }

            File.WriteAllText(Path.Combine(partial, PackedCopy.RebuiltMarker), marker);
            Directory.Move(partial, target);
            return target;
        }
        catch
        {
            GameCopier.TryDelete(partial);
            throw;
        }
    }

    /// <summary>Removes the rebuilt game folder (and any half-built one). Only names: base files stay as they are.</summary>
    public static void RemoveRebuilt(string packedFolder)
    {
        string target = Path.Combine(packedFolder, PackedCopy.GameFolder);
        foreach (string folder in new[] { target, target + PartialSuffix })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>
    /// Finishes or undoes a pack that was interrupted while swapping folders (e.g. Dispatch was closed). Quick; safe to
    /// call on every refresh.
    /// </summary>
    public static void Recover(string copyFolder)
    {
        copyFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyFolder));
        string staging = copyFolder + StagingSuffix;
        string old = copyFolder + OldSuffix;
        if (!Directory.Exists(copyFolder))
        {
            if (Directory.Exists(old))
            {
                Directory.Move(old, copyFolder); // the swap hadn't happened yet: back to the original
            }
            else if (PackedCopy.IsPacked(staging))
            {
                Directory.Move(staging, copyFolder); // only the last step was missing (the staging copy was checked)
            }
        }

        if (Directory.Exists(copyFolder) && Directory.Exists(old))
        {
            GameCopier.TryDelete(old);
        }
    }

    private static void Commit(string copyFolder, string staging)
    {
        string old = copyFolder + OldSuffix;
        GameCopier.TryDelete(old);
        Directory.Move(copyFolder, old);
        try
        {
            Directory.Move(staging, copyFolder);
        }
        catch
        {
            Directory.Move(old, copyFolder);
            throw;
        }

        GameCopier.TryDelete(old); // the original's own files; its shared files stay with Steam
    }

    private static void WriteFile(PackSources sources, PackedFile file, string path, byte[] buffer, ref long done, long total,
                                  IProgress<(long Done, long Total)>? progress, CancellationToken cancel)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = sources.OpenFile(file))
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
        {
            output.SetLength(file.Size);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                done += read;
                progress?.Report((done, total));
            }

            if (output.Length != file.Size || !sha.GetHashAndReset().AsSpan().SequenceEqual(file.Sha256))
            {
                throw new InvalidDataException($"The packed copy is damaged: {file.Path} didn't come out as it went in.");
            }
        }

        File.SetLastWriteTimeUtc(path, new DateTime(file.WriteTicks, DateTimeKind.Utc));
    }

    private static bool IsComplete(string target, PackRecipe recipe, string marker)
    {
        try
        {
            string file = Path.Combine(target, PackedCopy.RebuiltMarker);
            return File.Exists(file) && File.ReadAllText(file) == marker &&
                   recipe.Files.All(f => new FileInfo(Path.Combine(target, f.Path)) is { Exists: true } i && i.Length == f.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<PackBaseFile> LinkSteamFiles(string steamFolder, string baseFolder, CancellationToken cancel)
    {
        var bases = new List<PackBaseFile>();
        foreach (var file in new DirectoryInfo(steamFolder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(steamFolder, file.FullName);
            string link = Path.Combine(baseFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (!NativeFiles.TryCreateHardLink(link, file.FullName))
            {
                continue; // e.g. a file Steam has locked; packing just can't use it
            }

            var info = new FileInfo(link);
            bases.Add(new PackBaseFile(relative, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        return bases;
    }

    private static Dictionary<UInt128, Location> IndexBases(string staging, List<PackBaseFile> bases, ContentChunker chunker,
                                                           IProgress<PackProgress>? progress, CancellationToken cancel)
    {
        var index = new Dictionary<UInt128, Location>();
        long total = bases.Sum(b => b.Size);
        long done = 0;
        for (int i = 0; i < bases.Count; i++)
        {
            long offset = 0;
            int source = i;
            using (var input = new FileStream(Path.Combine(staging, PackedCopy.BaseFolder, bases[i].Path), FileMode.Open, FileAccess.Read,
                                              FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
            {
                chunker.Split(input, chunk =>
                {
                    index.TryAdd(Key(chunk), new Location(source, offset, chunk.Length));
                    offset += chunk.Length;
                }, cancel);
            }

            if (offset != bases[i].Size)
            {
                throw new IOException($"{bases[i].Path} changed while it was read. Is Steam updating the game?");
            }

            done += offset;
            progress?.Report(new PackProgress(PackPhase.Indexing, done, total));
        }

        return index;
    }

    /// <summary>Reads every file of the packed copy back and compares it with the original's SHA-256.</summary>
    private static void Check(string folder, long totalBytes, IProgress<PackProgress>? progress, CancellationToken cancel)
    {
        var recipe = PackRecipe.Read(folder);
        using var sources = new PackSources(folder, recipe);
        var buffer = new byte[1 << 20];
        long done = 0;
        foreach (var file in recipe.Files)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var input = sources.OpenFile(file))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, read);
                    length += read;
                    done += read;
                    progress?.Report(new PackProgress(PackPhase.Checking, done, totalBytes));
                }
            }

            if (length != file.Size || !sha.GetHashAndReset().AsSpan().SequenceEqual(file.Sha256))
            {
                throw new InvalidDataException($"The packed copy didn't match the original at {file.Path}; nothing was changed.");
            }
        }
    }

    private static void Append(List<PackSegment> segments, PackSegment s)
    {
        if (segments.Count > 0 && segments[^1] is var last && last.Source == s.Source && last.Offset + last.Length == s.Offset)
        {
            segments[^1] = last with { Length = last.Length + s.Length };
        }
        else
        {
            segments.Add(s);
        }
    }

    private static UInt128 Key(ReadOnlySpan<byte> chunk)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(chunk, hash);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }

    private static void ThrowIfRunning(IPlatform platform, GameDefinition game)
    {
        if (platform.IsProcessRunning(game.ProcessName))
        {
            throw new InvalidOperationException($"{game.DisplayName} is running. Close it first.");
        }
    }

    /// <summary>What gets packed: the files of a full copy, or of a packed copy (read through its recipe).</summary>
    private sealed class Source : IDisposable
    {
        private PackSources? _packed;
        private Dictionary<string, PackedFile>? _byPath;

        public sealed record Item(string Path, long Size, long WriteTicks);

        private Source(string folder) => Folder = folder;

        public string Folder { get; }

        public List<Item> Files { get; } = [];

        public string? Version { get; private set; }

        public static Source Open(string folder, bool packed)
        {
            var source = new Source(folder);
            if (packed)
            {
                var recipe = PackRecipe.Read(folder);
                source._packed = new PackSources(folder, recipe);
                source._byPath = recipe.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                source.Version = recipe.Version;
                source.Files.AddRange(recipe.Files.Select(f => new Item(f.Path, f.Size, f.WriteTicks)));
                var health = PackedCopy.Inspect(folder);
                if (health.IsBroken)
                {
                    source.Dispose();
                    throw new InvalidOperationException($"The packed copy is damaged ({health.Changed[0]}); it can't be packed again.");
                }

                return source;
            }

            source.Version = GameCopyRecord.Read(folder)?.Version;
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (f.Name.Equals(GameCopyRecord.FileName, StringComparison.OrdinalIgnoreCase) && f.DirectoryName == folder)
                {
                    continue;
                }

                source.Files.Add(new Item(Path.GetRelativePath(folder, f.FullName), f.Length, f.LastWriteTimeUtc.Ticks));
            }

            return source;
        }

        public Stream OpenFile(Item file) => _packed is not null
            ? _packed.OpenFile(_byPath![file.Path])
            : new FileStream(Path.Combine(Folder, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20,
                             FileOptions.SequentialScan);

        public void Dispose()
        {
            _packed?.Dispose();
            _packed = null;
        }
    }
}
