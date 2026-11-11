using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using Dispatch.Core.Tests.Safety;

namespace Dispatch.Core.Tests.Profiles;

public class LoadOrderEditsTests
{
    private static readonly string[] Abcde = ["a", "b", "c", "d", "e"];

    private static string Join(IEnumerable<string> s) => string.Concat(s);

    [Theory]
    [InlineData("bd", "badce")]
    [InlineData("ab", "abcde")] // already at the top: stays
    [InlineData("ae", "abced")]
    public void MoveUp_moves_selection_one_step_as_a_block(string selected, string expected) =>
        Join(LoadOrderEdits.MoveUp(Abcde, selected.Select(c => c.ToString()).ToHashSet())).ShouldBe(expected);

    [Theory]
    [InlineData("bd", "acbed")]
    [InlineData("de", "abcde")]
    public void MoveDown_moves_selection_one_step(string selected, string expected) =>
        Join(LoadOrderEdits.MoveDown(Abcde, selected.Select(c => c.ToString()).ToHashSet())).ShouldBe(expected);

    [Fact]
    public void MoveToTop_and_bottom_keep_relative_order()
    {
        var sel = new HashSet<string> { "d", "b" };
        Join(LoadOrderEdits.MoveToTop(Abcde, sel)).ShouldBe("bdace");
        Join(LoadOrderEdits.MoveToBottom(Abcde, sel)).ShouldBe("acebd");
    }

    [Theory]
    [InlineData("bd", 0, "bdace")]
    [InlineData("bd", 5, "acebd")]
    [InlineData("a", 3, "bcade")]  // drop between c and d
    [InlineData("bd", 3, "acbde")] // drop between c and d: the block lands there
    public void MoveTo_inserts_the_block_at_the_drop_position(string selected, int index, string expected) =>
        Join(LoadOrderEdits.MoveTo(Abcde, selected.Select(c => c.ToString()).ToHashSet(), index)).ShouldBe(expected);

    [Fact]
    public void AddToTop_skips_mods_that_are_already_active()
    {
        var current = new List<ActiveModEntry> { new("a", "A"), new("b", "B") };
        var result = LoadOrderEdits.AddToTop(current, [new("x", "X"), new("b", "B"), new("y", "Y")]);
        Join(result.Select(e => e.PackageId)).ShouldBe("xyab");
    }

    [Fact]
    public void Remove_removes_only_the_given_ids()
    {
        var current = new List<ActiveModEntry> { new("a", "A"), new("b", "B"), new("c", "C") };
        Join(LoadOrderEdits.Remove(current, ["a", "c", "zz"]).Select(e => e.PackageId)).ShouldBe("b");
    }

    [Fact]
    public void Delete_moves_the_profile_into_a_restorable_backup()
    {
        using var f = new ProfileFixture("Test", "a|A");
        var profile = f.Profile;
        var backup = ProfileRemover.Delete(f.Writer, f.Backups, profile);

        Directory.Exists(profile.MainDir).ShouldBeFalse();
        backup.Reason.ShouldBe(BackupReason.Delete);
        backup.IsFullProfile.ShouldBeTrue();
        File.Exists(Path.Combine(BackupService.ContentDir(backup), "save", "1", "game.sii")).ShouldBeTrue();

        f.Writer.RestoreFolder(backup);
        Directory.Exists(profile.MainDir).ShouldBeTrue();
    }

    [Fact]
    public void Delete_is_refused_while_the_game_runs_and_for_cloud_profiles()
    {
        using var f = new ProfileFixture("Test", "a|A");
        Should.Throw<InvalidOperationException>(() =>
            ProfileRemover.Delete(f.Writer, f.Backups, f.Profile with { Kind = ProfileKind.SteamCloud }));
        f.Platform.ChecksBeforeGameStarts = 0;
        Should.Throw<GameRunningException>(() => ProfileRemover.Delete(f.Writer, f.Backups, f.Profile));
        Directory.Exists(f.Profile.MainDir).ShouldBeTrue();
    }
}
