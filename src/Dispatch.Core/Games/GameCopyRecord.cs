using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Core.Games;

/// <summary>
/// dispatch-copy.json in a copy made by Dispatch: which files are shared with the install (hard links) and their size
/// and time when the copy was made. A shared file's size or time changing means someone wrote into it, which changes
/// it for the copy too, so the copy is no longer that version. (When Steam replaces a file, which is how it updates,
/// the copy keeps the old file with its old size and time.) The game ignores the file.
/// </summary>
public sealed record GameCopyRecord(int Schema, string Game, string? Version, DateTime CreatedUtc, IReadOnlyList<GameCopyRecord.SharedFile> Shared)
{
    public const string FileName = "dispatch-copy.json";

    public sealed record SharedFile(string Path, long Size, long WriteTicks);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string folder, GameCopyRecord record) =>
        File.WriteAllText(System.IO.Path.Combine(folder, FileName), JsonSerializer.Serialize(record, Json));

    public static GameCopyRecord? Read(string folder)
    {
        try
        {
            string file = System.IO.Path.Combine(folder, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<GameCopyRecord>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How a copy is doing: whether its shared files are still the ones it was made from, and how much space it really
    /// uses (files no other folder shares). Only reads file metadata; quick even for the whole game.
    /// </summary>
    public static CopyHealth Inspect(string folder)
    {
        if (PackedCopy.IsPacked(folder))
        {
            return PackedCopy.Inspect(folder);
        }

        var record = Read(folder);
        var changed = new List<string>();
        int stillShared = 0;
        foreach (var f in record?.Shared ?? [])
        {
            var info = new FileInfo(System.IO.Path.Combine(folder, f.Path));
            if (!info.Exists || info.Length != f.Size || info.LastWriteTimeUtc.Ticks != f.WriteTicks)
            {
                changed.Add(f.Path);
            }
        }

        long own = 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            total += f.Length;
            if (NativeFiles.LinkCount(f.FullName) is > 1)
            {
                stillShared++;
            }
            else
            {
                own += f.Length;
            }
        }

        return new CopyHealth(record is not null, record?.Shared.Count ?? 0, stillShared, changed, own, total);
    }
}

/// <param name="MadeByDispatch">The copy has a record (older copies and copies made by hand don't).</param>
/// <param name="SharedAtCopy">Files shared with the install when the copy was made.</param>
/// <param name="StillShared">Files that still have another name somewhere (usually: the install's file).</param>
/// <param name="Changed">Shared files that were written to since (the copy is broken when there are any).</param>
/// <param name="OwnBytes">Space only this copy uses.</param>
/// <param name="TotalBytes">Size as Explorer shows it (shared files counted fully); for a packed copy, the game's size.</param>
/// <param name="Pack">Set for a packed copy (see <see cref="PackedCopy"/>).</param>
public sealed record CopyHealth(bool MadeByDispatch, int SharedAtCopy, int StillShared, IReadOnlyList<string> Changed, long OwnBytes, long TotalBytes,
                                PackInfo? Pack = null)
{
    public bool IsBroken => Changed.Count > 0;

    public bool IsPacked => Pack is not null;
}
