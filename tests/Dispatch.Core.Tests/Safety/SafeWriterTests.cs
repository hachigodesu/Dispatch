using System.Text;
using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Platform;
using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using Dispatch.Core.Sii;
using Dispatch.Core.Tests.Detection;

namespace Dispatch.Core.Tests.Safety;

/// <summary>A platform whose "game running" answer can change after a number of checks.</summary>
internal sealed class ScriptedPlatform(IPlatform inner) : IPlatform
{
    public int ChecksBeforeGameStarts { get; set; } = int.MaxValue;

    public int Checks { get; private set; }

    public string DocumentsFolder => inner.DocumentsFolder;

    public string LocalAppDataFolder => inner.LocalAppDataFolder;

    public string? SteamPath => inner.SteamPath;

    public uint? ActiveSteamAccountId => inner.ActiveSteamAccountId;

    public bool IsProcessRunning(string processName) => ++Checks > ChecksBeforeGameStarts;
}

internal sealed class ProfileFixture : IDisposable
{
    public ProfileFixture(string name = "Test", params string[] fileOrder)
    {
        Name = name;
        string folder = ProfileNameCodec.ToFolderName(name);
        var sb = new StringBuilder("SiiNunit\r\n{\r\nuser_profile : _nameless.1.2 {\r\n face: 1\r\n");
        sb.Append(" active_mods: ").Append(fileOrder.Length).Append("\r\n");
        for (int i = 0; i < fileOrder.Length; i++)
        {
            sb.Append(" active_mods[").Append(i).Append("]: \"").Append(fileOrder[i]).Append("\"\r\n");
        }

        sb.Append(" profile_name: ").Append(Dispatch.Core.Sii.Text.SiiString.Format(name)).Append("\r\n}\r\n\r\n}\r\n");
        Pc.File($@"Documents\Euro Truck Simulator 2\profiles\{folder}\profile.sii", sb.ToString());
        Pc.File($@"Documents\Euro Truck Simulator 2\profiles\{folder}\profile.bak.sii", "old backup");
        Pc.File($@"Documents\Euro Truck Simulator 2\profiles\{folder}\save\1\game.sii", "save data");
        Platform = new ScriptedPlatform(Pc.Platform());
        Backups = new BackupService(Path.Combine(Pc.Root, "backups"));
        Writer = new SafeWriter(Platform, Backups, "eurotrucks2");
    }

    public string Name { get; }

    public FakePc Pc { get; } = new();

    public ScriptedPlatform Platform { get; }

    public BackupService Backups { get; }

    public SafeWriter Writer { get; }

    public GameEnvironment Env => GameDetector.Detect(Pc.Platform(), GameDefinition.Ets2);

    /// <summary>The fixture's own profile (by name; duplicates made by a test have other names).</summary>
    public ProfileInfo Profile => ProfileRepository.List(Env).First(p => p.Kind == ProfileKind.Local && p.Name == Name);

    public string ProfileSii => Profile.ProfileSiiPath;

    public void Dispose() => Pc.Dispose();
}

public class SafeWriterTests
{
    private static List<ActiveModEntry> Entries(params string[] ids) => ids.Select(i => new ActiveModEntry(i, i.ToUpperInvariant())).ToList();

    [Fact]
    public void Saves_new_order_as_plain_text_with_a_backup_first()
    {
        using var f = new ProfileFixture("Test", "c|C", "b|B", "a|A"); // top-first: a, b, c
        var profile = f.Profile;
        var loaded = ProfileRepository.ReadLoadOrder(profile);
        loaded.TopFirst.Select(e => e.PackageId).ShouldBe(["a", "b", "c"]);
        byte[] before = File.ReadAllBytes(f.ProfileSii);

        var backup = LoadOrderService.Save(f.Writer, profile, loaded, Entries("c", "a"));

        ProfileRepository.ReadLoadOrder(profile).TopFirst.Select(e => e.PackageId).ShouldBe(["c", "a"]);
        SiiFormatDetector.Detect(File.ReadAllBytes(f.ProfileSii)).ShouldBe(SiiFormat.Text);
        backup.Files.ShouldBe(["profile.sii", "profile.bak.sii"], ignoreOrder: true);
        File.ReadAllBytes(Path.Combine(BackupService.ContentDir(backup), "profile.sii")).ShouldBe(before);
        File.ReadAllText(Path.Combine(profile.MainDir, "profile.bak.sii")).ShouldBe("old backup"); // never modified
        Directory.EnumerateFiles(profile.MainDir, "*.dispatch-tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Refuses_while_the_game_is_running_and_writes_nothing()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var loaded = ProfileRepository.ReadLoadOrder(f.Profile);
        byte[] before = File.ReadAllBytes(f.ProfileSii);
        f.Platform.ChecksBeforeGameStarts = 0;

        Should.Throw<GameRunningException>(() => LoadOrderService.Save(f.Writer, f.Profile, loaded, []));
        File.ReadAllBytes(f.ProfileSii).ShouldBe(before);
        f.Backups.ListAll().ShouldBeEmpty();
    }

    [Fact]
    public void Game_started_between_backup_and_swap_aborts_cleanly()
    {
        using var f = new ProfileFixture("Test", "a|A", "b|B");
        var loaded = ProfileRepository.ReadLoadOrder(f.Profile);
        byte[] before = File.ReadAllBytes(f.ProfileSii);
        f.Platform.ChecksBeforeGameStarts = 1; // first check passes, the re-check before the swap fails

        Should.Throw<GameRunningException>(() => LoadOrderService.Save(f.Writer, f.Profile, loaded, Entries("a")));
        File.ReadAllBytes(f.ProfileSii).ShouldBe(before);
        Directory.EnumerateFiles(f.Profile.MainDir, "*.dispatch-tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Refuses_when_the_file_changed_since_it_was_read()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var loaded = ProfileRepository.ReadLoadOrder(f.Profile);
        File.AppendAllText(f.ProfileSii, " "); // e.g. the game saved meanwhile
        byte[] changed = File.ReadAllBytes(f.ProfileSii);

        Should.Throw<ConcurrentChangeException>(() => LoadOrderService.Save(f.Writer, f.Profile, loaded, []));
        File.ReadAllBytes(f.ProfileSii).ShouldBe(changed);
    }

    [Fact]
    public void Failed_verification_leaves_the_original_untouched()
    {
        using var f = new ProfileFixture("Test", "a|A");
        byte[] before = File.ReadAllBytes(f.ProfileSii);
        var plan = new WritePlan(f.Profile, BackupReason.Manual,
                                 [new FileWrite("profile.sii", "garbage"u8.ToArray(), SafeWriter.HashFile(f.ProfileSii))], [],
                                 Verify: (_, bytes) => SiiFileCodec.Decode(bytes));

        Should.Throw<InvalidDataException>(() => f.Writer.Commit(plan));
        File.ReadAllBytes(f.ProfileSii).ShouldBe(before);
        Directory.EnumerateFiles(f.Profile.MainDir, "*.dispatch-tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Cloud_profiles_are_refused()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var cloud = f.Profile with { Kind = ProfileKind.SteamCloud };
        Should.Throw<InvalidOperationException>(() =>
            f.Writer.Commit(new WritePlan(cloud, BackupReason.Manual, [], [])));
    }

    [Fact]
    public void Restore_puts_the_old_files_back_and_can_itself_be_undone()
    {
        using var f = new ProfileFixture("Test", "a|A", "b|B");
        var profile = f.Profile;
        byte[] original = File.ReadAllBytes(f.ProfileSii);
        var backup = LoadOrderService.Save(f.Writer, profile, ProfileRepository.ReadLoadOrder(profile), Entries("a"));
        byte[] edited = File.ReadAllBytes(f.ProfileSii);

        var pre = f.Writer.Restore(backup, profile);

        File.ReadAllBytes(f.ProfileSii).ShouldBe(original);
        pre.Reason.ShouldBe(BackupReason.PreRestore);
        File.ReadAllBytes(Path.Combine(BackupService.ContentDir(pre), "profile.sii")).ShouldBe(edited);
        f.Backups.List(ProfileKind.Local, profile.FolderName).Count.ShouldBe(2);
    }

    [Fact]
    public void Keeps_only_the_newest_small_and_full_backups()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var backups = new BackupService(Path.Combine(f.Pc.Root, "b2"), new BackupRetention(Small: 3, Full: 1), () => time = time.AddSeconds(1));
        for (int i = 0; i < 5; i++)
        {
            backups.BackupFiles(f.Profile, BackupReason.Manual, ["profile.sii"]);
        }

        for (int i = 0; i < 2; i++)
        {
            backups.BackupWholeFolder(f.Profile, BackupReason.Rename, move: false);
        }

        var list = backups.List(ProfileKind.Local, f.Profile.FolderName);
        list.Count(b => !b.IsFullProfile).ShouldBe(3);
        list.Count(b => b.IsFullProfile).ShouldBe(1);
        list[0].CreatedUtc.ShouldBeGreaterThan(list[^1].CreatedUtc);
        list.Single(b => b.IsFullProfile).Files.ShouldContain(Path.Combine("save", "1", "game.sii"));
    }

    [Fact]
    public void Full_folder_restore_never_overwrites()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var profile = f.Profile;
        var full = f.Backups.BackupWholeFolder(profile, BackupReason.Rename, move: true);
        Directory.Exists(profile.MainDir).ShouldBeFalse();

        f.Writer.RestoreFolder(full).ShouldBe(profile.MainDir);
        File.Exists(Path.Combine(profile.MainDir, "save", "1", "game.sii")).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => f.Writer.RestoreFolder(full)); // exists now → refused
    }
}
