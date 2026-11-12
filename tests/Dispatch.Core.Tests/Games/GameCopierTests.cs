using Dispatch.Core.Games;
using Dispatch.Core.Tests.Detection;
using Dispatch.Core.Tests.Mods;
using Shouldly;

namespace Dispatch.Core.Tests.Versions;

public sealed class GameCopierTests
{
    private const int BaseSize = (5 << 20) + 123; // bigger than the copy buffer

    private static (TempDir Tmp, string Source, FakePlatform Platform) Setup()
    {
        var tmp = new TempDir();
        string source = Path.Combine(tmp.Path, "Euro Truck Simulator 2");
        Directory.CreateDirectory(Path.Combine(source, @"bin\win_x64"));
        File.WriteAllText(Path.Combine(source, @"bin\win_x64\eurotrucks2.exe"), "exe");
        File.WriteAllBytes(Path.Combine(source, "base.scs"), new byte[BaseSize]);
        File.WriteAllText(Path.Combine(source, "dlc_north.scs"), "north");
        return (tmp, source, new FakePlatform(tmp.Path, null));
    }

    private static string[] GameFiles(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != GameCopyRecord.FileName).ToArray();

    private static string KeepCopy(FakePlatform platform, string source, string dest)
    {
        GameCopier.Copy(platform, GameDefinition.Ets2, GameCopier.Prepare(platform, GameDefinition.Ets2, source, dest), null, CancellationToken.None);
        return dest;
    }

    [Fact]
    public void On_the_same_drive_files_are_shared_not_copied()
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string dest = Path.Combine(tmp.Path, "copies", "ETS2 1.62.2.1");

        var plan = GameCopier.Prepare(platform, GameDefinition.Ets2, source, dest);
        plan.Share.ShouldBeTrue();
        plan.BytesToWrite.ShouldBe(0);
        plan.FileCount.ShouldBe(3);
        long last = 0;
        GameCopier.Copy(platform, GameDefinition.Ets2, plan, new SyncProgress(b => last = b), CancellationToken.None, "1.62.2.1");

        last.ShouldBe(plan.TotalBytes);
        GameFiles(dest).Length.ShouldBe(3);
        File.ReadAllText(Path.Combine(dest, @"bin\win_x64\eurotrucks2.exe")).ShouldBe("exe");
        NativeFiles.LinkCount(Path.Combine(dest, "base.scs")).ShouldBe(2);
        NativeFiles.LinkCount(Path.Combine(source, "base.scs")).ShouldBe(2);
        Directory.Exists(dest + GameCopier.PartialSuffix).ShouldBeFalse();

        var record = GameCopyRecord.Read(dest)!;
        record.Game.ShouldBe("ets2");
        record.Version.ShouldBe("1.62.2.1");
        record.Shared.Count.ShouldBe(3);
        var health = GameCopyRecord.Inspect(dest);
        health.IsBroken.ShouldBeFalse();
        health.StillShared.ShouldBe(3);
        health.OwnBytes.ShouldBeLessThan(1000); // only the record itself
    }

    [Fact]
    public void A_file_replaced_by_an_update_stays_old_in_the_copy()
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string dest = KeepCopy(platform, source, Path.Combine(tmp.Path, "ETS2 old"));

        // What Steam does: build the new file elsewhere, then move it over the old one.
        string staged = Path.Combine(tmp.Path, "staged.scs");
        File.WriteAllText(staged, "north v2, longer");
        File.Move(staged, Path.Combine(source, "dlc_north.scs"), overwrite: true);

        File.ReadAllText(Path.Combine(dest, "dlc_north.scs")).ShouldBe("north");
        File.ReadAllText(Path.Combine(source, "dlc_north.scs")).ShouldBe("north v2, longer");
        var health = GameCopyRecord.Inspect(dest);
        health.IsBroken.ShouldBeFalse();
        health.StillShared.ShouldBe(2);
        health.OwnBytes.ShouldBeGreaterThanOrEqualTo(5); // the old dlc_north.scs is now the copy's own
        health.OwnBytes.ShouldBeLessThan(BaseSize);
    }

    [Fact]
    public void A_file_changed_in_place_marks_the_copy_broken()
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string dest = KeepCopy(platform, source, Path.Combine(tmp.Path, "ETS2 old"));

        File.SetLastWriteTimeUtc(Path.Combine(source, "base.scs"), DateTime.UtcNow.AddMinutes(5));
        File.AppendAllText(Path.Combine(source, "dlc_north.scs"), "!");

        var health = GameCopyRecord.Inspect(dest);
        health.IsBroken.ShouldBeTrue();
        health.Changed.ShouldBe(["base.scs", "dlc_north.scs"], ignoreOrder: true);
    }

    [Fact]
    public void A_full_copy_shares_nothing()
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string dest = Path.Combine(tmp.Path, "full");
        var plan = GameCopier.Prepare(platform, GameDefinition.Ets2, source, dest, allowSharing: false);
        plan.Share.ShouldBeFalse();
        plan.BytesToWrite.ShouldBe(plan.TotalBytes);
        GameCopier.Copy(platform, GameDefinition.Ets2, plan, null, CancellationToken.None);

        new FileInfo(Path.Combine(dest, "base.scs")).Length.ShouldBe(BaseSize);
        NativeFiles.LinkCount(Path.Combine(dest, "base.scs")).ShouldBe(1);
        NativeFiles.LinkCount(Path.Combine(source, "base.scs")).ShouldBe(1);
        var health = GameCopyRecord.Inspect(dest);
        health.MadeByDispatch.ShouldBeTrue();
        health.SharedAtCopy.ShouldBe(0);
        health.OwnBytes.ShouldBeGreaterThan(BaseSize);
    }

    [Fact]
    public void Copies_without_a_record_are_never_broken()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("base.scs"), "x");
        var health = GameCopyRecord.Inspect(tmp.Path);
        health.MadeByDispatch.ShouldBeFalse();
        health.IsBroken.ShouldBeFalse();
        health.TotalBytes.ShouldBe(1);
    }

    [Fact]
    public void Refuses_existing_destinations_folders_inside_the_game_and_a_running_game()
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string existing = Directory.CreateDirectory(Path.Combine(tmp.Path, "taken")).FullName;
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "mine");

        Should.Throw<InvalidOperationException>(() => GameCopier.Prepare(platform, GameDefinition.Ets2, source, existing));
        File.ReadAllText(Path.Combine(existing, "keep.txt")).ShouldBe("mine");
        Should.Throw<InvalidOperationException>(() => GameCopier.Prepare(platform, GameDefinition.Ets2, source, Path.Combine(source, "copy")));
        Should.Throw<InvalidOperationException>(() => GameCopier.Prepare(platform, GameDefinition.Ets2, tmp.Path, Path.Combine(tmp.Path, "x")));

        platform.RunningProcesses.Add("eurotrucks2");
        Should.Throw<InvalidOperationException>(() => GameCopier.Prepare(platform, GameDefinition.Ets2, source, Path.Combine(tmp.Path, "new")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cancelled_copy_leaves_nothing_behind_and_a_leftover_is_replaced(bool share)
    {
        var (tmp, source, platform) = Setup();
        using var _ = tmp;
        string dest = Path.Combine(tmp.Path, "ETS2 copy");
        var plan = GameCopier.Prepare(platform, GameDefinition.Ets2, source, dest, share);

        using var cts = new CancellationTokenSource();
        Should.Throw<OperationCanceledException>(() =>
            GameCopier.Copy(platform, GameDefinition.Ets2, plan, new SyncProgress(_ => cts.Cancel()), cts.Token));
        Directory.Exists(dest).ShouldBeFalse();
        Directory.Exists(dest + GameCopier.PartialSuffix).ShouldBeFalse();
        GameFiles(source).Length.ShouldBe(3); // removing shared names never removes the install's files
        File.ReadAllText(Path.Combine(source, "dlc_north.scs")).ShouldBe("north");

        Directory.CreateDirectory(dest + GameCopier.PartialSuffix);
        File.WriteAllText(Path.Combine(dest + GameCopier.PartialSuffix, "stale.bin"), "old");
        GameCopier.Copy(platform, GameDefinition.Ets2, plan, null, CancellationToken.None);
        File.Exists(Path.Combine(dest, "stale.bin")).ShouldBeFalse();
        GameFiles(dest).Length.ShouldBe(3);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the callback to run right away.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
