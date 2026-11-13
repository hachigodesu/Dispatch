using Dispatch.Core.Games;
using Dispatch.Core.Tests.Detection;
using Dispatch.Core.Tests.Mods;
using Shouldly;

namespace Dispatch.Core.Tests.Versions;

public sealed class GamePackerTests
{
    private const string Exe = @"bin\win_x64\eurotrucks2.exe";

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A "Steam install" (the new version) and a full copy of an old version that mostly shares content with it.</summary>
    private sealed class Setup : IDisposable
    {
        public TempDir Tmp { get; } = new();

        public string Steam { get; }

        public string Copy { get; }

        public FakePlatform Platform { get; }

        public Dictionary<string, byte[]> Old { get; } = [];

        public Setup()
        {
            Steam = Path.Combine(Tmp.Path, "Steam", "Euro Truck Simulator 2");
            Copy = Path.Combine(Tmp.Path, "ETS2 1.60.1.7");
            Platform = new FakePlatform(Tmp.Path, null);

            byte[] baseNew = Random(3 << 20, 1);
            byte[] dlcNew = Random(1 << 20, 2);
            Write(Steam, "base.scs", baseNew);
            Write(Steam, "dlc_north.scs", dlcNew);
            Write(Steam, Exe, Random(40_000, 3));

            // The old base.scs: the same data with a piece missing in the middle and a different end.
            byte[] baseOld = [.. baseNew.AsSpan(0, 1 << 20), .. baseNew.AsSpan((1 << 20) + 50_000, 1 << 20), .. Random(200_000, 4)];
            Old["base.scs"] = baseOld;
            Old["dlc_north.scs"] = dlcNew; // unchanged between versions
            Old[Exe] = Random(40_000, 5);
            Old["dlc_old_only.scs"] = Random(300_000, 6);
            Old[@"empty.txt"] = [];
            foreach (var (path, bytes) in Old)
            {
                Write(Copy, path, bytes);
            }

            GameCopyRecord.Write(Copy, new GameCopyRecord(1, "ets2", "1.60.1.7", DateTime.UtcNow, []));
        }

        public long OldTotal => Old.Values.Sum(b => (long)b.Length);

        public PackResult Pack() => GamePacker.Pack(Platform, GameDefinition.Ets2, Copy, Steam, null, null, CancellationToken.None);

        public string Rebuild() => GamePacker.Rebuild(Platform, GameDefinition.Ets2, Copy, null, CancellationToken.None);

        public void ShouldRebuildTheOldVersion()
        {
            string game = Rebuild();
            game.ShouldBe(Path.Combine(Copy, PackedCopy.GameFolder));
            foreach (var (path, bytes) in Old)
            {
                File.ReadAllBytes(Path.Combine(game, path)).ShouldBe(bytes, path);
            }

            Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length.ShouldBe(Old.Count + 1); // + the marker
        }

        /// <summary>What Steam does on an update: build the new file elsewhere and move it over the old one.</summary>
        public void SteamReplaces(string path, byte[] bytes)
        {
            string staged = Path.Combine(Tmp.Path, "staged.bin");
            File.WriteAllBytes(staged, bytes);
            File.Move(staged, Path.Combine(Steam, path), overwrite: true);
        }

        public void Dispose() => Tmp.Dispose();

        private static void Write(string root, string path, byte[] bytes)
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    [Fact]
    public void The_chunker_cuts_shared_data_the_same_way_at_any_offset()
    {
        byte[] data = Random(1 << 20, 9);
        static List<string> Pieces(byte[] bytes)
        {
            var list = new List<string>();
            new ContentChunker(4096).Split(new MemoryStream(bytes), c => list.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c))));
            return list;
        }

        var a = Pieces(data);
        var b = Pieces([.. Random(12_345, 10), .. data]);
        a.Count.ShouldBeGreaterThan(50);
        b.Intersect(a).Count().ShouldBeGreaterThan(a.Count - 3);
    }

    [Fact]
    public void Packing_keeps_only_what_Steam_doesnt_have_and_rebuilds_the_exact_files()
    {
        using var s = new Setup();
        var result = s.Pack();

        PackedCopy.IsPacked(s.Copy).ShouldBeTrue();
        File.Exists(Path.Combine(s.Copy, "base.scs")).ShouldBeFalse(); // the full files are gone
        Directory.Exists(s.Copy + GamePacker.StagingSuffix).ShouldBeFalse();
        Directory.Exists(s.Copy + GamePacker.OldSuffix).ShouldBeFalse();
        result.Files.ShouldBe(s.Old.Count);
        result.DataBytes.ShouldBeLessThan(800_000); // the old end, the old-only DLC, the old exe, piece edges
        result.DataBytes.ShouldBeGreaterThan(540_000);

        var health = GameCopyRecord.Inspect(s.Copy);
        health.IsPacked.ShouldBeTrue();
        health.IsBroken.ShouldBeFalse();
        health.Pack!.StaleBaseBytes.ShouldBe(0);
        health.Pack.IsRebuilt.ShouldBeFalse();
        health.OwnBytes.ShouldBeLessThan(900_000);
        health.TotalBytes.ShouldBe(s.OldTotal);
        var copy = GameInstallations.ReadCopy(GameDefinition.Ets2, s.Copy).ShouldNotBeNull();
        copy.IsPacked.ShouldBeTrue();
        copy.Version.ShouldBe(new Dispatch.Core.Detection.GameVersion(1, 60, 1, 7));
        copy.ExePath(GameDefinition.Ets2).ShouldBe(Path.Combine(s.Copy, PackedCopy.GameFolder, Exe));

        s.ShouldRebuildTheOldVersion();
        // A file equal to Steam's is linked, not written.
        NativeFiles.LinkCount(Path.Combine(s.Copy, PackedCopy.GameFolder, "dlc_north.scs")).ShouldBe(3);
        GameCopyRecord.Inspect(s.Copy).Pack!.IsRebuilt.ShouldBeTrue();

        GamePacker.RemoveRebuilt(s.Copy);
        Directory.Exists(Path.Combine(s.Copy, PackedCopy.GameFolder)).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(s.Steam, "dlc_north.scs")).ShouldBe(s.Old["dlc_north.scs"]);
    }

    [Fact]
    public void A_rebuilt_folder_that_is_complete_is_reused()
    {
        using var s = new Setup();
        s.Pack();
        string game = s.Rebuild();
        var written = File.GetLastWriteTimeUtc(Path.Combine(game, PackedCopy.RebuiltMarker));
        s.Rebuild();
        File.GetLastWriteTimeUtc(Path.Combine(game, PackedCopy.RebuiltMarker)).ShouldBe(written);

        File.Delete(Path.Combine(game, "dlc_old_only.scs"));
        s.ShouldRebuildTheOldVersion(); // incomplete: built again
    }

    [Fact]
    public void After_a_Steam_update_the_copy_still_works_and_packing_again_frees_the_old_files()
    {
        using var s = new Setup();
        s.Pack();
        s.SteamReplaces("base.scs", Random(3 << 20, 20));
        s.SteamReplaces("dlc_north.scs", Random(1 << 20, 21));

        var health = GameCopyRecord.Inspect(s.Copy);
        health.IsBroken.ShouldBeFalse();
        health.Pack!.StaleBaseBytes.ShouldBe((3 << 20) + (1 << 20));
        s.ShouldRebuildTheOldVersion();

        var again = s.Pack(); // nothing in common with the new Steam files any more
        again.DataBytes.ShouldBeGreaterThan(s.OldTotal - 100_000);
        var after = GameCopyRecord.Inspect(s.Copy);
        after.Pack!.StaleBaseBytes.ShouldBe(0);
        after.Pack.IsRebuilt.ShouldBeFalse();
        s.ShouldRebuildTheOldVersion();
    }

    [Fact]
    public void A_damaged_packed_copy_is_never_rebuilt()
    {
        using var s = new Setup();
        s.Pack();
        string data = Path.Combine(s.Copy, PackedCopy.DataFile);
        using (var f = new FileStream(data, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Position = 1000;
            int b = f.ReadByte();
            f.Position = 1000;
            f.WriteByte((byte)(b ^ 0xFF));
        }

        File.SetLastWriteTimeUtc(data, File.GetLastWriteTimeUtc(data)); // same size: only the content check notices
        Should.Throw<InvalidDataException>(() => s.Rebuild());
        Directory.Exists(Path.Combine(s.Copy, PackedCopy.GameFolder)).ShouldBeFalse();
        Directory.Exists(Path.Combine(s.Copy, PackedCopy.GameFolder + ".dispatch-partial")).ShouldBeFalse();

        File.AppendAllText(data, "x");
        GameCopyRecord.Inspect(s.Copy).IsBroken.ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => s.Rebuild());
    }

    [Fact]
    public void Nothing_changes_when_the_game_runs_or_packing_is_cancelled()
    {
        using var s = new Setup();
        s.Platform.RunningProcesses.Add("eurotrucks2");
        Should.Throw<InvalidOperationException>(() => s.Pack());
        s.Platform.RunningProcesses.Clear();

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == PackPhase.Checking)
            {
                cts.Cancel();
            }
        });
        Should.Throw<OperationCanceledException>(() =>
            GamePacker.Pack(s.Platform, GameDefinition.Ets2, s.Copy, s.Steam, null, progress, cts.Token));

        PackedCopy.IsPacked(s.Copy).ShouldBeFalse();
        Directory.Exists(s.Copy + GamePacker.StagingSuffix).ShouldBeFalse();
        foreach (var (path, bytes) in s.Old)
        {
            File.ReadAllBytes(Path.Combine(s.Copy, path)).ShouldBe(bytes);
        }

        Should.Throw<InvalidOperationException>(() =>
            GamePacker.Pack(s.Platform, GameDefinition.Ets2, s.Copy, Path.Combine(s.Tmp.Path, "Steam"), null, null, CancellationToken.None));
    }

    [Fact]
    public void An_interrupted_swap_is_finished_or_undone()
    {
        using var s = new Setup();
        // Interrupted after the original was moved aside, before the packed copy took its place: back to the original.
        Directory.Move(s.Copy, s.Copy + GamePacker.OldSuffix);
        GamePacker.Recover(s.Copy);
        File.ReadAllBytes(Path.Combine(s.Copy, "base.scs")).ShouldBe(s.Old["base.scs"]);

        // Packed, but the moved-aside original wasn't deleted yet: it is now.
        s.Pack();
        Directory.CreateDirectory(s.Copy + GamePacker.OldSuffix);
        GamePacker.Recover(s.Copy);
        Directory.Exists(s.Copy + GamePacker.OldSuffix).ShouldBeFalse();
        PackedCopy.IsPacked(s.Copy).ShouldBeTrue();
    }

    /// <summary>A second old version (1.59) that has most of 1.60's own data, plus some of its own.</summary>
    private static (string Folder, Dictionary<string, byte[]> Files) OlderCopy(Setup s)
    {
        string folder = Path.Combine(s.Tmp.Path, "ETS2 1.59.2.1");
        var files = new Dictionary<string, byte[]>
        {
            ["base.scs"] = [.. s.Old["base.scs"].AsSpan(0, 2 << 20), .. Random(150_000, 30)],
            ["dlc_old_only.scs"] = s.Old["dlc_old_only.scs"], // only the old versions have it
            [Exe] = s.Old[Exe],
        };
        foreach (var (path, bytes) in files)
        {
            string full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        GameCopyRecord.Write(folder, new GameCopyRecord(1, "ets2", "1.59.2.1", DateTime.UtcNow, []));
        return (folder, files);
    }

    private static void ShouldRebuild(Setup s, string folder, Dictionary<string, byte[]> files)
    {
        string game = GamePacker.Rebuild(s.Platform, GameDefinition.Ets2, folder, null, CancellationToken.None);
        foreach (var (path, bytes) in files)
        {
            File.ReadAllBytes(Path.Combine(game, path)).ShouldBe(bytes, path);
        }

        GamePacker.RemoveRebuilt(folder);
    }

    [Fact]
    public void Packed_copies_share_data_with_each_other_and_survive_the_other_being_deleted()
    {
        using var s = new Setup();
        var first = s.Pack();
        var (older, files) = OlderCopy(s);

        long alone = GamePacker.Pack(s.Platform, GameDefinition.Ets2, older, s.Steam, null, null, CancellationToken.None, commit: false).DataBytes;

        var shared = GamePacker.Pack(s.Platform, GameDefinition.Ets2, older, s.Steam, null, null, CancellationToken.None,
                                     shareWith: [s.Copy, older, Path.Combine(s.Tmp.Path, "not there")]);
        shared.SharedCopies.ShouldBe(1);
        shared.DataBytes.ShouldBeLessThan(alone - 300_000); // dlc_old_only and the old exe come from 1.60's data
        first.DataBytes.ShouldBeGreaterThan(300_000);

        var health = GameCopyRecord.Inspect(older, s.Steam);
        health.IsBroken.ShouldBeFalse();
        health.Pack!.SharedWithCopiesBytes.ShouldBe(first.DataBytes);
        health.Pack.StaleBaseBytes.ShouldBe(0);
        ShouldRebuild(s, older, files);

        // The rebuilt game never gets a link to another copy's data file.
        string game = GamePacker.Rebuild(s.Platform, GameDefinition.Ets2, older, null, CancellationToken.None);
        NativeFiles.SameFile(Path.Combine(game, "dlc_old_only.scs"), Path.Combine(s.Copy, PackedCopy.DataFile)).ShouldBeFalse();
        GamePacker.RemoveRebuilt(older);

        // 1.60 is deleted: 1.59 keeps working, and the data it used is now its own.
        Directory.Delete(s.Copy, recursive: true);
        var alone2 = GameCopyRecord.Inspect(older, s.Steam);
        alone2.IsBroken.ShouldBeFalse();
        alone2.Pack!.SharedWithCopiesBytes.ShouldBe(0);
        alone2.OwnBytes.ShouldBeGreaterThan(shared.DataBytes + first.DataBytes - 1);
        ShouldRebuild(s, older, files);
    }

    [Fact]
    public void A_Steam_file_two_copies_still_link_counts_as_replaced_when_the_Steam_folder_is_known()
    {
        using var s = new Setup();
        s.Pack();
        var (older, _) = OlderCopy(s);
        GamePacker.Pack(s.Platform, GameDefinition.Ets2, older, s.Steam, null, null, CancellationToken.None, shareWith: [s.Copy]);
        s.SteamReplaces("base.scs", Random(3 << 20, 40));

        // The old base.scs still has two names (both copies' links), so its link count can't tell it was replaced.
        GameCopyRecord.Inspect(s.Copy).Pack!.StaleBaseBytes.ShouldBe(0);
        GameCopyRecord.Inspect(s.Copy, s.Steam).Pack!.StaleBaseBytes.ShouldBe(3 << 20);
        GameCopyRecord.Inspect(older, s.Steam).Pack!.StaleBaseBytes.ShouldBe(3 << 20);
    }

    private sealed class SyncProgress(Action<PackProgress> report) : IProgress<PackProgress>
    {
        public void Report(PackProgress value) => report(value);
    }
}
