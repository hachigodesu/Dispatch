using System.Globalization;
using System.Text;
using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using Dispatch.Core.Sii;

namespace Dispatch.Spike;

/// <summary>
/// Spikes S2/S3/S4. These are the only spike commands that WRITE, and only to local profiles whose name
/// starts with "DispatchTest". Every write: game must be closed → timestamped backup → temp file → verify → swap.
/// </summary>
internal static class InGameSpikes
{
    private const string AllowedPrefix = "DispatchTest";

    private static string ProfilesDir => Path.Combine(GamePaths.UserDataDir, "profiles");

    private static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "backups", "Local");

    public static int Run(string command, string[] args)
    {
        try
        {
            return command switch
            {
                "s3-read" => Read(Arg(args, 0)),
                "s2-write" => ReverseLoadOrder(Arg(args, 0)),
                "s2-check" => Check(Arg(args, 0)),
                "s4-duplicate" => Duplicate(Arg(args, 0), Arg(args, 1)),
                "restore" => Restore(Arg(args, 0)),
                "roundtrip" => RoundTrip(args.Length > 0 ? args[0] : "spike-output"),
                "decode" => DecodeFile(Arg(args, 0)),
                _ => throw new ArgumentException($"Unknown command {command}"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"REFUSED/FAILED: {ex.Message}");
            return 3;
        }
    }

    private static string Arg(string[] args, int i) =>
        i < args.Length ? args[i] : throw new ArgumentException("Missing profile name argument.");

    /// <summary>Resolves a DispatchTest* profile folder; refuses anything else.</summary>
    private static string ProfileDir(string name, bool mustExist = true)
    {
        if (!name.StartsWith(AllowedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Spikes only touch profiles named '{AllowedPrefix}…'; '{name}' refused.");
        }

        string dir = Path.Combine(ProfilesDir, ProfileNameCodec.ToFolderName(name));
        if (mustExist && !File.Exists(Path.Combine(dir, "profile.sii")))
        {
            throw new ArgumentException($"Local profile '{name}' not found ({dir}). Create it in the game first.");
        }

        return dir;
    }

    // ---- S3: read the order ---------------------------------------------------------------------

    private static int Read(string name)
    {
        string dir = ProfileDir(name);
        var result = SiiFileCodec.Decode(File.ReadAllBytes(Path.Combine(dir, "profile.sii")));
        var mods = ProfileText.ReadActiveMods(result.Text);
        Console.WriteLine($"{name}: profile.sii is {string.Join(" > ", result.Layers)}, {mods.Count} active mods");
        Console.WriteLine("File order (active_mods[i]):");
        for (int i = 0; i < mods.Count; i++)
        {
            Console.WriteLine($"  [{i}] {mods[i].DisplayName}   ({mods[i].PackageId})");
        }

        Console.WriteLine("Compare with the in-game Mod Manager list, top to bottom.");
        return 0;
    }

    /// <summary>Read-only: decode any profile.sii (e.g. a copy of a cloud profile) and print its load order.</summary>
    private static int DecodeFile(string path)
    {
        var result = SiiFileCodec.Decode(File.ReadAllBytes(path));
        File.WriteAllText(path + ".decoded.sii", result.Text);
        if (!result.Text.Contains("user_profile :", StringComparison.Ordinal))
        {
            Console.WriteLine($"{Path.GetFileName(path)}: {string.Join(" > ", result.Layers)}, {result.Text.Length:N0} chars → {path}.decoded.sii");
            return 0;
        }

        var mods = ProfileText.ReadActiveMods(result.Text);
        Console.WriteLine($"{Path.GetFileName(path)}: {string.Join(" > ", result.Layers)}, " +
                          $"profile_name '{ProfileText.ReadProfileName(result.Text)}', {mods.Count} active mods (file order):");
        for (int i = 0; i < mods.Count; i++)
        {
            Console.WriteLine($"  [{i}] {mods[i].DisplayName}   ({mods[i].PackageId})");
        }

        return 0;
    }

    // ---- S2: write plain text with a reversed order ---------------------------------------------

    private static int ReverseLoadOrder(string name)
    {
        GameProcessGuard.ThrowIfRunning();
        string dir = ProfileDir(name);
        string target = Path.Combine(dir, "profile.sii");
        byte[] original = File.ReadAllBytes(target);
        var decoded = SiiFileCodec.Decode(original);
        var mods = ProfileText.ReadActiveMods(decoded.Text);
        if (mods.Count < 2)
        {
            throw new InvalidOperationException("Enable at least 2 mods (ideally 3 small ones) in DispatchTest first.");
        }

        string backup = Backup(dir, "spike-s2", ["profile.sii", "profile.bak.sii"]);
        string newText = ProfileText.WriteActiveMods(decoded.Text, mods.Reverse().ToList());
        WriteAtomically(target, original, newText);

        var verify = ProfileText.ReadActiveMods(SiiFileCodec.Decode(File.ReadAllBytes(target)).Text);
        Console.WriteLine($"Backup: {backup}");
        Console.WriteLine($"Wrote plain-text profile.sii with the {mods.Count} mods in REVERSED order. New file order:");
        for (int i = 0; i < verify.Count; i++)
        {
            Console.WriteLine($"  [{i}] {verify[i].DisplayName}");
        }

        return 0;
    }

    private static int Check(string name)
    {
        string dir = ProfileDir(name);
        foreach (var file in new[] { "profile.sii", "profile.bak.sii" })
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"{file}: missing");
                continue;
            }

            byte[] data = File.ReadAllBytes(path);
            var decoded = SiiFileCodec.Decode(data);
            var mods = ProfileText.ReadActiveMods(decoded.Text);
            Console.WriteLine($"{file}: {SiiFormatDetector.Detect(data)} ({string.Join(" > ", decoded.Layers)}), " +
                              $"modified {File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}, " +
                              $"name '{ProfileText.ReadProfileName(decoded.Text)}', mods: {string.Join(" | ", mods.Select(m => m.DisplayName))}");
        }

        return 0;
    }

    // ---- S4: duplicate -------------------------------------------------------------------------

    private static int Duplicate(string sourceName, string newName)
    {
        GameProcessGuard.ThrowIfRunning();
        string source = ProfileDir(sourceName);
        string target = ProfileDir(newName, mustExist: false);
        if (Directory.Exists(target) || Directory.Exists(Path.Combine(GamePaths.UserDataDir, "steam_profiles", Path.GetFileName(target))))
        {
            throw new InvalidOperationException($"A profile named '{newName}' already exists; refusing to overwrite.");
        }

        // Stage under a non-hex name (the game ignores it), then a single rename makes it appear.
        string staging = Path.Combine(ProfilesDir, $".dispatch-staging-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(source, staging);
            foreach (var file in new[] { "profile.sii", "profile.bak.sii" })
            {
                string path = Path.Combine(staging, file);
                if (File.Exists(path))
                {
                    byte[] data = File.ReadAllBytes(path);
                    string text = ProfileText.WriteProfileName(SiiFileCodec.Decode(data).Text, newName);
                    WriteAtomically(path, data, text);
                }
            }

            GameProcessGuard.ThrowIfRunning();
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }

        Console.WriteLine($"Duplicated '{sourceName}' → '{newName}' ({target}).");
        Console.WriteLine("Only profile_name was changed (in profile.sii and profile.bak.sii); everything else is a byte copy.");
        return 0;
    }

    // ---- restore / round-trip ------------------------------------------------------------------

    private static int Restore(string name)
    {
        GameProcessGuard.ThrowIfRunning();
        string dir = ProfileDir(name);
        string backups = Path.Combine(BackupRoot, Path.GetFileName(dir));
        string latest = Directory.Exists(backups)
            ? Directory.EnumerateDirectories(backups).Order(StringComparer.Ordinal).LastOrDefault()
              ?? throw new InvalidOperationException("No backups found.")
            : throw new InvalidOperationException("No backups found.");

        string preRestore = Backup(dir, "pre-restore", ["profile.sii", "profile.bak.sii"]);
        foreach (var file in Directory.EnumerateFiles(latest))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        }

        Console.WriteLine($"Restored from {latest}. (State before the restore saved to {preRestore}.)");
        return 0;
    }

    /// <summary>Read-only: re-writing the active_mods of every decoded real profile copy must be byte-identical.</summary>
    private static int RoundTrip(string outDir)
    {
        int ok = 0, bad = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(outDir, "profiles"), "profile.decoded.sii", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            string modsRt = ProfileText.WriteActiveMods(text, ProfileText.ReadActiveMods(text));
            string nameRt = ProfileText.WriteProfileName(text, ProfileText.ReadProfileName(text)!);
            bool same = modsRt == text && nameRt == text;
            if (!same)
            {
                File.WriteAllText(file + ".roundtrip-mods.sii", modsRt);
                File.WriteAllText(file + ".roundtrip-name.sii", nameRt);
            }
            Console.WriteLine($"  {(same ? "identical" : "DIFFERENT")}: {Path.GetFileName(Path.GetDirectoryName(file))}");
            if (same) { ok++; } else { bad++; }
        }

        Console.WriteLine($"Round-trip: {ok} identical, {bad} different.");
        return bad == 0 ? 0 : 2;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string Backup(string profileDir, string reason, IEnumerable<string> files)
    {
        string dest = Path.Combine(BackupRoot, Path.GetFileName(profileDir),
                                   $"{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}_{reason}");
        Directory.CreateDirectory(dest);
        foreach (var f in files)
        {
            string src = Path.Combine(profileDir, f);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(dest, f));
            }
        }

        return dest;
    }

    /// <summary>Temp file next to the target → verify it decodes → re-check game → atomic replace.</summary>
    private static void WriteAtomically(string target, byte[] expectedCurrent, string text)
    {
        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(expectedCurrent))
        {
            throw new InvalidOperationException($"{target} changed while we were working; aborting.");
        }

        string temp = target + ".dispatch-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _ = SiiFileCodec.Decode(File.ReadAllBytes(temp)); // throws if not parseable
        GameProcessGuard.ThrowIfRunning();
        File.Replace(temp, target, destinationBackupFileName: null);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)));
        }
    }
}
