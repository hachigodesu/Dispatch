using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Platform;

namespace Dispatch.Core.Mods;

public enum InstallOutcome
{
    Installed,
    Replaced,
    Skipped,
    Failed,
}

public sealed record InstallResult(string Source, string? Target, InstallOutcome Outcome, PackageFormat? Format, string Message);

/// <summary>
/// Installs mod files into Documents\…\mod and removes them again. Installing checks each file's header (by content, not
/// extension), copies through a temp file, and never overwrites silently: an existing mod with the same name is only
/// replaced when asked, and the old file goes to the Recycle Bin first. Workshop items are never touched.
/// </summary>
public sealed class ModInstaller(IPlatform platform, string processName, Action<string> sendToRecycleBin)
{
    private static readonly string[] Extensions = [".scs", ".zip"];

    public static bool IsModFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <param name="replaceExisting">Asked per file when a mod with the same file name is already installed.</param>
    /// <param name="libraryDir">The mod library: a mod of the same name there counts as installed too, and is replaced.</param>
    public IReadOnlyList<InstallResult> Install(string modDir, IEnumerable<string> files, Func<string, bool> replaceExisting, string? libraryDir = null)
    {
        ThrowIfGameRunning();
        Directory.CreateDirectory(modDir);
        var results = new List<InstallResult>();
        foreach (var source in files)
        {
            results.Add(InstallOne(modDir, source, replaceExisting, libraryDir));
        }

        return results;
    }

    private InstallResult InstallOne(string modDir, string source, Func<string, bool> replaceExisting, string? libraryDir)
    {
        if (!File.Exists(source))
        {
            return new(source, null, InstallOutcome.Failed, null, "The file doesn't exist.");
        }

        if (!IsModFile(source))
        {
            return new(source, null, InstallOutcome.Skipped, null, "Not a mod file (only .scs and .zip can be installed).");
        }

        PackageFormat format;
        string? warning = null;
        try
        {
            using var pkg = ModPackage.Open(source);
            format = pkg.Format;
            if (format == PackageFormat.Opaque)
            {
                warning = " It uses a protected or unknown format: the game may still load it, but Dispatch can't show its details.";
            }
            else if (pkg.TryReadFile("manifest.sii") is null)
            {
                warning = " It has no manifest.sii, so the game shows only its file name.";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new(source, null, InstallOutcome.Failed, null, $"This doesn't look like a mod package: {ex.Message}");
        }

        string target = Path.Combine(modDir, Path.GetFileName(source));
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return new(source, target, InstallOutcome.Skipped, format, "That file is already in the mod folder.");
        }

        // The same mod as a file or an unpacked folder, here or in the library (never the file being installed).
        var existing = new[] { modDir, libraryDir }.OfType<string>()
            .SelectMany(d => new[]
            {
                (Path: Path.Combine(d, Path.GetFileName(source)), Folder: false),
                (Path: Path.Combine(d, Path.GetFileNameWithoutExtension(source)), Folder: true),
            })
            .Where(c => (c.Folder ? Directory.Exists(c.Path) : File.Exists(c.Path)) &&
                        !string.Equals(Path.GetFullPath(c.Path), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .ToList();
        bool replacing = existing.Count > 0;
        if (replacing && !replaceExisting(Path.GetFileName(source)))
        {
            return new(source, target, InstallOutcome.Skipped, format, "A mod with this name is already installed; kept the existing one.");
        }

        string temp = Path.Combine(modDir, $".dispatch-install-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temp);
            if (new FileInfo(temp).Length != new FileInfo(source).Length)
            {
                throw new IOException("The copy is incomplete.");
            }

            ThrowIfGameRunning();
            foreach (string old in existing)
            {
                // The replaced version stays recoverable; an unpacked or library copy of the same mod would be a duplicate.
                sendToRecycleBin(old);
            }

            File.Move(temp, target);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }

        return new(source, target, replacing ? InstallOutcome.Replaced : InstallOutcome.Installed, format,
                   (replacing ? "Replaced the installed version (the old one is in the Recycle Bin)." : "Installed.") + warning);
    }

    /// <summary>Sends a local mod to the Recycle Bin. Workshop items must be unsubscribed in Steam instead.</summary>
    public void Uninstall(ModInfo mod)
    {
        if (mod.Source == ModSource.Workshop)
        {
            throw new InvalidOperationException($"\"{mod.DisplayName}\" is a Steam Workshop item. Unsubscribe from it in Steam to remove it.");
        }

        ThrowIfGameRunning();
        if (!File.Exists(mod.Path) && !Directory.Exists(mod.Path))
        {
            throw new InvalidOperationException($"\"{mod.DisplayName}\" is no longer where it was ({mod.Path}).");
        }

        sendToRecycleBin(mod.Path);
    }

    private void ThrowIfGameRunning()
    {
        if (platform.IsProcessRunning(processName))
        {
            throw new Safety.GameRunningException("The game is running. Close it before installing or removing mods. Nothing was changed.");
        }
    }
}
