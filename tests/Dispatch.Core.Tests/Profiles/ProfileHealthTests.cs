using Dispatch.Core.Detection;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Profiles;

public class ProfileHealthTests
{
    private static ModInfo Mod(string id, string? version = "1", string[]? games = null, string[]? dlc = null, string? name = null) =>
        new(id, ModSource.Local, null, id, id, PackageFormat.Zip, 1, DateTime.UtcNow,
            new ModManifest(".package_name", version, name ?? id, null, ["other"], null, null, games ?? [], dlc ?? [], null, []),
            null, 0, null, null, null, []);

    private static ActiveModEntry E(string id) => new(id, id);

    [Fact]
    public void Finds_missing_wrong_version_missing_dlc_older_copies_and_what_the_save_needs()
    {
        var old = Mod("trucks_v1", "1.0", name: "Trucks");
        var newer = Mod("trucks_v2", "2.0", name: "Trucks");
        var mods = new[] { Mod("fine"), Mod("for160", games: ["1.60.*"]), Mod("needs_north", dlc: ["dlc_north"]), old, newer, Mod("saved_only") };
        var save = new SaveInfo("autosave", "", DateTime.UtcNow,
                                [new("mod", "saved_only", "Saved only"), new("mod", "gone_too", "Gone too"), new("mod", "fine", "fine"), new("dlc", "dlc_east", "East")]);
        var input = new HealthInput([E("fine"), E("for160"), E("needs_north"), E("trucks_v1"), E("gone")],
                                    mods.ToDictionary(m => m.PackageId), GameVersion.TryParse("1.62.3.2"), new HashSet<string> { "dlc_east" },
                                    ModDuplicates.Find(mods), null, save);

        var issues = ProfileHealthCheck.Check(input);

        issues.Select(i => i.Kind).ShouldBe([HealthIssueKind.MissingMods, HealthIssueKind.WrongVersion, HealthIssueKind.MissingDlc,
                                             HealthIssueKind.OlderCopyActive, HealthIssueKind.SaveNeedsMods]);
        issues[0].Mods.Single().PackageId.ShouldBe("gone");
        issues[1].Mods.Single().PackageId.ShouldBe("for160");
        issues[2].Mods.Single().PackageId.ShouldBe("needs_north");
        issues[3].Mods.Single().Replacement!.PackageId.ShouldBe("trucks_v2");
        issues[4].Detail.ShouldBe("autosave");
        issues[4].Mods.Select(m => (m.PackageId, m.Mod is not null)).ShouldBe([("saved_only", true), ("gone_too", false)]);

        // The same profile on 1.60 with the DLC: only the older copy and the missing mods are left.
        var on160 = ProfileHealthCheck.Check(input with
        {
            LaunchVersion = GameVersion.TryParse("1.60.1.7"), OwnedDlc = new HashSet<string> { "dlc_north" }, NewestSave = null,
        });
        on160.Select(i => i.Kind).ShouldBe([HealthIssueKind.MissingMods, HealthIssueKind.OlderCopyActive]);
    }

    [Fact]
    public void A_healthy_profile_has_no_issues_and_the_newer_copy_can_replace_the_older_in_place()
    {
        ProfileHealthCheck.Check(new HealthInput([E("a")], new Dictionary<string, ModInfo> { ["a"] = Mod("a") }, null,
                                                 new HashSet<string>(), new Dictionary<ModInfo, DuplicateInfo>(), null, null)).ShouldBeEmpty();

        var replaced = LoadOrderEdits.Replace([E("x"), E("old"), E("y"), E("new")], new Dictionary<string, ActiveModEntry> { ["old"] = E("new") });
        replaced.Select(e => e.PackageId).ShouldBe(["x", "new", "y"]);
    }

    [Fact]
    public void Reads_the_newest_save_of_a_profile()
    {
        using var tmp = new TempDir();
        string profile = tmp.File("profile");
        void Save(string slot, string name, DateTime when, params string[] deps)
        {
            string dir = Path.Combine(profile, "save", slot);
            Directory.CreateDirectory(dir);
            string list = string.Join("\n", deps.Select((d, i) => $" dependencies[{i}]: \"{d}\""));
            string file = Path.Combine(dir, "info.sii");
            File.WriteAllText(file, $"SiiNunit\n{{\nsave_container : _nameless.1.2.3 {{\n name: \"{name}\"\n dependencies: {deps.Length}\n{list}\n}}\n}}\n");
            File.SetLastWriteTimeUtc(file, when);
        }

        Save("1", "Before the update", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "mod|old|Old");
        Save("autosave", "", new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), "mod|promods-def|ProMods Definition", "dlc|dlc_north|Beyond the Baltic Sea",
             "mod|mod_workshop_package.00000000C3CC68CD|RPM | QuickStart");
        Directory.CreateDirectory(Path.Combine(profile, "save", "broken"));

        var info = new ProfileInfo("Test", "54657374", ProfileKind.Local, profile, null, null, DateTime.UtcNow, 2);
        var newest = SaveReader.Newest(info).ShouldNotBeNull();
        newest.Label.ShouldBe("autosave");
        newest.Mods.Select(m => (m.PackageId, m.DisplayName)).ShouldBe([
            ("promods-def", "ProMods Definition"), ("mod_workshop_package.00000000C3CC68CD", "RPM | QuickStart")]);
        SaveReader.TryRead(Path.Combine(profile, "save", "1"))!.Label.ShouldBe("Before the update");
        SaveReader.TryRead(Path.Combine(profile, "save", "broken")).ShouldBeNull();
    }
}
