using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Detection;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Mods;

public sealed record ModScanResult(IReadOnlyList<ModInfo> Mods, TimeSpan Elapsed, int FromCache);

/// <summary>
/// Finds and reads every local and Workshop mod. Read-only: packages are opened with shared read access and
/// never modified. Results are cached by path + size + last-write time, so unchanged mods aren't re-read.
/// </summary>
public sealed class ModScanner(string cacheDir)
{
    private const int CacheSchema = 2; // bump when ModInfo or lint messages change
    private static readonly string[] SubPackageExtensions = ["", ".zip", ".scs"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private string IconDir => Path.Combine(cacheDir, "icons");

    private string CacheFile => Path.Combine(cacheDir, "mods.json");

    public ModScanResult Scan(GameEnvironment env, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        var cache = LoadCache(env.SteamBuildId);
        var work = new List<(string Id, ModSource Source, ulong? WorkshopId, string Path)>();

        if (env.ModDir is { } modDir && Directory.Exists(modDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(modDir))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }

                bool isDir = (attributes & FileAttributes.Directory) != 0;
                string ext = Path.GetExtension(entry);
                if (isDir || ext.Equals(".scs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    work.Add((isDir ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry), ModSource.Local, null, entry));
                }
            }
        }

        foreach (var contentDir in env.WorkshopContentDirs)
        {
            foreach (var item in Directory.EnumerateDirectories(contentDir))
            {
                if (ulong.TryParse(Path.GetFileName(item), out ulong id))
                {
                    work.Add((ModInfo.WorkshopPackageId(id), ModSource.Workshop, id, item));
                }
            }
        }

        var results = new ModInfo[work.Count];
        int done = 0, hits = 0;
        var gameVersion = env.Version?.ToString() ?? "";
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, i =>
        {
            var (id, source, workshopId, path) = work[i];
            var (size, mtime) = Stamp(path);
            if (cache.TryGetValue(path, out var cached) && cached.SizeBytes == size && cached.LastWriteUtc == mtime &&
                (cached.IconFile is null || File.Exists(cached.IconFile)))
            {
                results[i] = cached;
                Interlocked.Increment(ref hits);
            }
            else
            {
                results[i] = ScanOne(id, source, workshopId, path, size, mtime, gameVersion);
            }

            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });

        var mods = results.OrderBy(m => m.Source).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        SaveCache(env.SteamBuildId, mods);
        return new ModScanResult(mods, sw.Elapsed, hits);
    }

    /// <summary>Reads a mod's description text (on demand; not cached). Colour tags like [orange] are kept.</summary>
    public static string? ReadDescription(ModInfo mod)
    {
        if (mod.Manifest?.DescriptionFile is not { } file)
        {
            return null;
        }

        try
        {
            using var pkg = ModPackage.Open(mod.PackagePath);
            return pkg.TryReadFile(file) is { } bytes ? Encoding.UTF8.GetString(bytes).TrimStart('﻿') : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ModInfo ScanOne(string id, ModSource source, ulong? workshopId, string path, long size, DateTime mtime, string gameVersion)
    {
        string packagePath = path;
        string? subPackage = null;
        int subCount = 0;
        var issues = new List<ModIssue>();
        try
        {
            if (source == ModSource.Workshop && File.Exists(Path.Combine(path, "versions.sii")))
            {
                var entries = VersionsEntry.Parse(File.ReadAllText(Path.Combine(path, "versions.sii")));
                subCount = entries.Count;
                var chosen = VersionsEntry.Select(entries, gameVersion);
                if (chosen is null)
                {
                    issues.Add(new ModIssue(IssueSeverity.Warning, "versions.sii has no entry for this game version and no universal entry."));
                }
                else
                {
                    subPackage = chosen.PackageName;
                    packagePath = SubPackageExtensions.Select(e => Path.Combine(path, chosen.PackageName + e))
                                      .FirstOrDefault(p => Directory.Exists(p) || File.Exists(p)) ?? path;
                }
            }

            using var pkg = ModPackage.Open(packagePath);
            if (pkg.Format == PackageFormat.Opaque)
            {
                issues.Add(new ModIssue(IssueSeverity.Warning,
                    "Protected or unknown package format. The game can load it, but Dispatch can't show its details."));
                return new ModInfo(id, source, workshopId, path, packagePath, pkg.Format, size, mtime, null, subPackage, subCount,
                                   null, null, null, issues);
            }

            if (pkg.TryReadFile("manifest.sii") is not { } manifestBytes)
            {
                issues.Add(new ModIssue(IssueSeverity.Info, "No manifest.sii: the game shows the file name and no details."));
                return new ModInfo(id, source, workshopId, path, packagePath, pkg.Format, size, mtime, null, subPackage, subCount,
                                   null, null, null, issues);
            }

            var manifest = ModManifest.Parse(manifestBytes);
            string? iconFile = null;
            (int, int)? iconSize = null;
            bool iconFound = false, iconIsJpeg = false;
            if (manifest.IconFile is { } icon && pkg.TryReadFile(icon) is { } iconBytes)
            {
                iconFound = true;
                iconSize = ImageInfo.TryGetJpegSize(iconBytes);
                iconIsJpeg = iconSize is not null;
                iconFile = SaveIcon(packagePath, mtime, iconBytes);
            }

            bool descriptionFound = manifest.DescriptionFile is { } d && pkg.TryReadFile(d) is not null;
            issues.AddRange(ModLinter.Check(manifest, iconFound, iconSize, iconIsJpeg, descriptionFound));
            return new ModInfo(id, source, workshopId, path, packagePath, pkg.Format, size, mtime, manifest, subPackage, subCount,
                               iconFile, iconSize?.Item1, iconSize?.Item2, issues);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            issues.Add(new ModIssue(IssueSeverity.Error, $"Couldn't read this mod: {ex.Message}"));
            return new ModInfo(id, source, workshopId, path, packagePath, PackageFormat.Opaque, size, mtime, null, subPackage, subCount,
                               null, null, null, issues);
        }
    }

    /// <summary>Icons are stored once per package version; JPEG and PNG are kept, other formats aren't shown.</summary>
    private string? SaveIcon(string packagePath, DateTime mtime, byte[] bytes)
    {
        bool jpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P';
        if (!jpeg && !png)
        {
            return null;
        }

        Directory.CreateDirectory(IconDir);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packagePath + "|" + mtime.Ticks)))[..16];
        string file = Path.Combine(IconDir, key + (jpeg ? ".jpg" : ".png"));
        if (!File.Exists(file))
        {
            File.WriteAllBytes(file, bytes);
        }

        return file;
    }

    private static (long Size, DateTime Mtime) Stamp(string path)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return (fi.Length, fi.LastWriteTimeUtc);
        }

        // Folder mods / Workshop items: newest file inside + total size.
        long size = 0;
        DateTime newest = Directory.GetLastWriteTimeUtc(path);
        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            size += f.Length;
            if (f.LastWriteTimeUtc > newest)
            {
                newest = f.LastWriteTimeUtc;
            }
        }

        return (size, newest);
    }

    private sealed record CacheFileModel(int Schema, string? BuildId, List<ModInfo> Mods);

    private Dictionary<string, ModInfo> LoadCache(string? buildId)
    {
        try
        {
            if (File.Exists(CacheFile) &&
                JsonSerializer.Deserialize<CacheFileModel>(File.ReadAllText(CacheFile), Json) is { } model &&
                model.Schema == CacheSchema && model.BuildId == buildId)
            {
                return model.Mods.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // A broken cache is simply rebuilt.
        }

        return new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache(string? buildId, List<ModInfo> mods)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            string temp = CacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheFileModel(CacheSchema, buildId, mods), Json));
            File.Move(temp, CacheFile, overwrite: true);
        }
        catch (IOException)
        {
            // Caching is an optimisation; failing to save it is not an error.
        }
    }
}

/// <summary>Helpers for mod description text.</summary>
public static class DescriptionText
{
    /// <summary>Removes the game's colour tags ([red], [orange], [normal], …) for plain display.</summary>
    public static string StripColorTags(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[(red|green|blue|orange|white|normal|yellow|black|grey|gray)\]", "",
                                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Unescapes a value the same way the game does for SII strings (for display only).</summary>
    public static string Unescape(string value) => SiiString.Unquote(value);
}
