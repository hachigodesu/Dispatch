using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Tests.Safety;

public class ProfileClonerTests
{
    private static (IEnumerable<string> Names, IEnumerable<string> Folders) Existing(ProfileFixture f)
    {
        var all = ProfileRepository.List(f.Env);
        return (all.Select(p => p.Name).ToList(), all.Select(p => p.FolderName).ToList());
    }

    private static string ProfilesRoot(ProfileFixture f) => Path.GetDirectoryName(f.Profile.MainDir)!;

    [Fact]
    public void Duplicate_copies_everything_and_changes_only_the_name()
    {
        using var f = new ProfileFixture("Test", "a|A");
        File.WriteAllText(Path.Combine(f.Profile.MainDir, "profile.bak.sii"), File.ReadAllText(f.ProfileSii));
        var (names, folders) = Existing(f);

        var copy = ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f), new DuplicateOptions("Test 2"), names, folders);

        copy.FolderName.ShouldBe(ProfileNameCodec.ToFolderName("Test 2"));
        File.ReadAllText(Path.Combine(copy.MainDir, "save", "1", "game.sii")).ShouldBe("save data");
        foreach (var file in new[] { "profile.sii", "profile.bak.sii" })
        {
            string text = SiiFileCodec.Decode(File.ReadAllBytes(Path.Combine(copy.MainDir, file))).Text;
            ProfileText.ReadProfileName(text).ShouldBe("Test 2");
            ProfileText.ReadActiveMods(text).Select(m => m.PackageId).ShouldBe(["a"]);
        }

        ProfileText.ReadProfileName(SiiFileCodec.Decode(File.ReadAllBytes(f.ProfileSii)).Text).ShouldBe("Test"); // source untouched
        ProfileRepository.List(f.Env).Select(p => p.Name).ShouldBe(["Test", "Test 2"], ignoreOrder: true);
        Directory.EnumerateDirectories(ProfilesRoot(f), ".dispatch-staging-*").ShouldBeEmpty();
    }

    [Fact]
    public void Fresh_start_leaves_out_saves_and_can_clear_the_world_of_trucks_link()
    {
        using var f = new ProfileFixture("Test", "a|A");
        string text = File.ReadAllText(f.ProfileSii).Replace(" face: 1", " face: 1\r\n online_user_name: \"trucker\"\r\n online_password: \"abc\"", StringComparison.Ordinal);
        File.WriteAllText(f.ProfileSii, text);
        var (names, folders) = Existing(f);

        var copy = ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f),
                                           new DuplicateOptions("Fresh", IncludeSaves: false, KeepWorldOfTrucksLink: false), names, folders);

        Directory.Exists(Path.Combine(copy.MainDir, "save", "1")).ShouldBeFalse();
        Directory.Exists(Path.Combine(copy.MainDir, "save")).ShouldBeTrue();
        string copied = File.ReadAllText(Path.Combine(copy.MainDir, "profile.sii"));
        ProfileText.ReadField(copied, "online_user_name").ShouldBe("");
        ProfileText.ReadField(copied, "online_password").ShouldBe("");
    }

    [Fact]
    public void Duplicate_refuses_existing_names_invalid_names_and_a_running_game()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var (names, folders) = Existing(f);
        Should.Throw<ArgumentException>(() => ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f), new DuplicateOptions("Test"), names, folders));
        Should.Throw<ArgumentException>(() => ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f), new DuplicateOptions("🚚"), names, folders));

        // A folder that exists but isn't a listed profile is still never overwritten.
        Directory.CreateDirectory(Path.Combine(ProfilesRoot(f), ProfileNameCodec.ToFolderName("Ghost")));
        Should.Throw<InvalidOperationException>(() => ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f), new DuplicateOptions("Ghost"), names, folders));

        f.Platform.ChecksBeforeGameStarts = 0;
        Should.Throw<GameRunningException>(() => ProfileCloner.Duplicate(f.Writer, f.Profile, ProfilesRoot(f), new DuplicateOptions("Other"), names, folders));
        Directory.EnumerateDirectories(ProfilesRoot(f), ".dispatch-staging-*").ShouldBeEmpty();
    }

    [Fact]
    public void Rename_moves_the_original_into_a_full_backup_that_can_be_restored()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var source = f.Profile;
        var (names, folders) = Existing(f);

        var (renamed, original) = ProfileCloner.Rename(f.Writer, f.Backups, source, "Renamed", names, folders);

        Directory.Exists(source.MainDir).ShouldBeFalse();
        ProfileRepository.List(f.Env).Select(p => p.Name).ShouldBe(["Renamed"]);
        File.ReadAllText(Path.Combine(renamed.MainDir, "save", "1", "game.sii")).ShouldBe("save data");
        original.IsFullProfile.ShouldBeTrue();
        original.OriginalDir.ShouldBe(source.MainDir);

        // Undo: the original comes back next to the renamed copy (nothing is overwritten).
        f.Writer.RestoreFolder(original);
        ProfileRepository.List(f.Env).Select(p => p.Name).ShouldBe(["Test", "Renamed"], ignoreOrder: true);
    }

    [Fact]
    public void Rename_to_a_name_differing_only_in_case_is_allowed()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var (names, folders) = Existing(f);
        var (renamed, _) = ProfileCloner.Rename(f.Writer, f.Backups, f.Profile, "TEST", names, folders);
        renamed.Name.ShouldBe("TEST");
    }

    [Fact]
    public void Rename_to_the_same_name_is_refused() =>
        Should.Throw<ArgumentException>(() =>
        {
            using var f = new ProfileFixture("Test", "a|A");
            var (names, folders) = Existing(f);
            ProfileCloner.Rename(f.Writer, f.Backups, f.Profile, "Test", names, folders);
        });
}
