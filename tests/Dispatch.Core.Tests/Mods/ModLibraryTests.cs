using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Safety;
using Dispatch.Core.Tests.Detection;

namespace Dispatch.Core.Tests.Mods;

public class ModLibraryTests
{
    private static (GameEnvironment Env, ModScanner Scanner) Setup(FakePc pc)
    {
        string mod = @"Documents\Euro Truck Simulator 2\mod\";
        Fixtures.WriteZip(Path.Combine(pc.Root, mod + "used.scs"), Fixtures.SampleFiles());
        Fixtures.WriteZip(Path.Combine(pc.Root, mod + "unused.scs"), Fixtures.SampleFiles());
        pc.File(mod + @"unused_folder\manifest.sii", "SiiNunit { mod_package : .x {\n display_name: \"Folder\"\n } }");
        return (GameDetector.Detect(pc.Platform(), GameDefinition.Ets2), new ModScanner(Path.Combine(pc.Root, "cache")));
    }

    [Fact]
    public void Unused_mods_go_to_the_library_and_come_back_when_a_profile_needs_them()
    {
        using var pc = new FakePc();
        var (env, scanner) = Setup(pc);
        var platform = pc.Platform();
        var used = new HashSet<string>(StringComparer.Ordinal) { "used", "mod_workshop_package.00000000C3CC68CD" };
        var mods = scanner.Scan(env, cancel: TestContext.Current.CancellationToken).Mods;

        var unused = ModLibrary.Unused(mods, used);
        unused.Select(m => m.PackageId).ShouldBe(["unused", "unused_folder"], ignoreOrder: true);
        var moved = ModLibrary.Move(platform, "eurotrucks2", unused, env.ModLibraryDir!);
        moved.ShouldAllBe(m => m.Outcome == LibraryMoveOutcome.Moved);
        File.Exists(Path.Combine(env.ModDir!, "unused.scs")).ShouldBeFalse();
        File.Exists(Path.Combine(env.ModLibraryDir!, "unused.scs")).ShouldBeTrue();
        Directory.Exists(Path.Combine(env.ModLibraryDir!, "unused_folder")).ShouldBeTrue();
        File.ReadAllLines(Path.Combine(env.ModLibraryDir!, ModLibrary.JournalFile)).Length.ShouldBe(2);

        // The library is scanned too, and its mods are marked; the journal isn't a mod.
        mods = scanner.Scan(env, cancel: TestContext.Current.CancellationToken).Mods;
        mods.Single(m => m.PackageId == "unused").InLibrary.ShouldBeTrue();
        mods.Single(m => m.PackageId == "used").InLibrary.ShouldBeFalse();
        mods.Where(m => m.Source == ModSource.Local).Select(m => m.PackageId).ShouldBe(["used", "unused", "unused_folder"], ignoreOrder: true);
        ModLibrary.Unused(mods, used).ShouldBeEmpty();

        // A profile starts using one: it goes back.
        used.Add("unused");
        var back = ModLibrary.NeededBack(mods, used);
        back.Select(m => m.PackageId).ShouldBe(["unused"]);
        ModLibrary.Move(platform, "eurotrucks2", back, env.ModDir!).Single().Outcome.ShouldBe(LibraryMoveOutcome.Moved);
        File.Exists(Path.Combine(env.ModDir!, "unused.scs")).ShouldBeTrue();
        File.ReadAllLines(Path.Combine(env.ModLibraryDir!, ModLibrary.JournalFile)).Length.ShouldBe(3);
        scanner.Scan(env, cancel: TestContext.Current.CancellationToken).Mods.Single(m => m.PackageId == "unused").InLibrary.ShouldBeFalse();
    }

    [Fact]
    public void Nothing_is_overwritten_and_nothing_moves_while_the_game_runs()
    {
        using var pc = new FakePc();
        var (env, scanner) = Setup(pc);
        var platform = pc.Platform();
        Directory.CreateDirectory(env.ModLibraryDir!);
        File.WriteAllText(Path.Combine(env.ModLibraryDir!, "unused.scs"), "another mod of the same name");
        var mods = ModLibrary.Unused(scanner.Scan(env, cancel: TestContext.Current.CancellationToken).Mods.Where(m => !m.InLibrary), new HashSet<string> { "used" });

        var result = ModLibrary.Move(platform, "eurotrucks2", mods, env.ModLibraryDir!);
        result.Single(r => r.Mod.PackageId == "unused").Outcome.ShouldBe(LibraryMoveOutcome.Skipped);
        File.ReadAllText(Path.Combine(env.ModLibraryDir!, "unused.scs")).ShouldBe("another mod of the same name");
        File.Exists(Path.Combine(env.ModDir!, "unused.scs")).ShouldBeTrue();

        platform.RunningProcesses.Add("eurotrucks2");
        var back = scanner.Scan(env, cancel: TestContext.Current.CancellationToken).Mods.Where(m => m.InLibrary && m.PackageId == "unused_folder");
        Should.Throw<GameRunningException>(() => ModLibrary.Move(platform, "eurotrucks2", back, env.ModDir!));
        Directory.Exists(Path.Combine(env.ModLibraryDir!, "unused_folder")).ShouldBeTrue();
    }

    [Fact]
    public void Installing_a_mod_that_is_in_the_library_replaces_that_copy()
    {
        using var tmp = new TempDir();
        string modDir = tmp.File("mod");
        string library = tmp.File(ModLibrary.FolderName);
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "Cool Mod.scs"), "old version");
        var recycled = new List<string>();
        var installer = new ModInstaller(new FakePlatform(tmp.Path, null), "eurotrucks2", p =>
        {
            recycled.Add(p);
            File.Delete(p);
        });
        string zip = tmp.File("Cool Mod.scs");
        Fixtures.WriteZip(zip, Fixtures.SampleFiles());

        installer.Install(modDir, [zip], _ => false, library)[0].Outcome.ShouldBe(InstallOutcome.Skipped);
        File.Exists(Path.Combine(library, "Cool Mod.scs")).ShouldBeTrue();
        installer.Install(modDir, [zip], _ => true, library)[0].Outcome.ShouldBe(InstallOutcome.Replaced);
        recycled.ShouldBe([Path.Combine(library, "Cool Mod.scs")]);
        File.Exists(Path.Combine(modDir, "Cool Mod.scs")).ShouldBeTrue();
    }
}
