using Dispatch.Core.Profiles;

namespace Dispatch.Core.Tests.Profiles;

public class ProfileTextTests
{
    private static string Sample(string nl = "\r\n") => string.Join(nl,
        "SiiNunit",
        "{",
        "user_profile : _nameless.221.4e89.3458 {",
        " face: 1",
        " brand: scania_s_2016",
        @" company_name: ""Test } Co"" ",
        " active_mods: 3",
        @" active_mods[0]: ""promods-eu-def-v283|ProMods Definition Package""",
        @" active_mods[1]: ""mod_workshop_package.00000000C3CC68CD|RPM - QuickStart""",
        @" active_mods[2]: ""odd|Name | with \""pipes\"" and \\ slash""",
        " customization: 0",
        @" profile_name: ""\xe6\x97\xa5\xe6\x9c\xac""",
        " creation_time: 1700000000",
        "}",
        "",
        "}",
        "");

    [Fact]
    public void Reads_profile_name_with_byte_escapes() =>
        ProfileText.ReadProfileName(Sample()).ShouldBe("日本");

    [Fact]
    public void Reads_the_map_path()
    {
        ProfileText.ReadMapPath(Sample()).ShouldBeNull();
        string text = Sample().Replace(" profile_name:", " map_path: \"/map/grandutopia.mbd\"\r\n profile_name:", StringComparison.Ordinal);
        ProfileText.ReadMapPath(text).ShouldBe("/map/grandutopia.mbd");
        new LoadOrderRead([], [], default, [], "/map/grandutopia.mbd").MapName.ShouldBe("grandutopia");
        new LoadOrderRead([], [], default, []).MapName.ShouldBeNull();
    }

    [Fact]
    public void Reads_active_mods_in_file_order()
    {
        var mods = ProfileText.ReadActiveMods(Sample());
        mods.Select(m => m.PackageId).ShouldBe(["promods-eu-def-v283", "mod_workshop_package.00000000C3CC68CD", "odd"]);
        mods[2].DisplayName.ShouldBe(@"Name | with ""pipes"" and \ slash");
    }

    [Fact]
    public void Rewriting_the_same_mods_is_byte_identical()
    {
        string text = Sample();
        ProfileText.WriteActiveMods(text, ProfileText.ReadActiveMods(text)).ShouldBe(text);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Reordering_only_changes_the_active_mods_block(string nl)
    {
        string text = Sample(nl);
        var reversed = ProfileText.ReadActiveMods(text).Reverse().ToList();
        string result = ProfileText.WriteActiveMods(text, reversed);

        ProfileText.ReadActiveMods(result).ShouldBe(reversed);
        result.Replace(nl, "\n", StringComparison.Ordinal).Split('\n').Length.ShouldBe(text.Replace(nl, "\n", StringComparison.Ordinal).Split('\n').Length);
        result.ShouldContain(" customization: 0" + nl + @" profile_name: ""\xe6\x97\xa5\xe6\x9c\xac""");
        if (nl == "\n")
        {
            result.ShouldNotContain("\r");
        }
    }

    [Fact]
    public void Can_clear_and_grow_the_list()
    {
        string empty = ProfileText.WriteActiveMods(Sample(), []);
        ProfileText.ReadActiveMods(empty).ShouldBeEmpty();
        empty.ShouldContain(" active_mods: 0\r\n customization: 0");

        var many = Enumerable.Range(0, 12).Select(i => new ActiveModEntry($"mod{i}", $"Mod {i}")).ToList();
        string grown = ProfileText.WriteActiveMods(empty, many);
        ProfileText.ReadActiveMods(grown).ShouldBe(many);
        grown.ShouldContain(" active_mods: 12\r\n active_mods[0]: \"mod0|Mod 0\"");
    }

    [Fact]
    public void Renames_profile_with_game_style_escapes()
    {
        string result = ProfileText.WriteProfileName(Sample(), "Dispatch Test 名");
        ProfileText.ReadProfileName(result).ShouldBe("Dispatch Test 名");
        result.ShouldContain(@" profile_name: ""Dispatch Test \xe5\x90\x8d""" + "\r\n creation_time");
        result.Length.ShouldBe(Sample().Length - @"\xe6\x97\xa5\xe6\x9c\xac".Length + @"Dispatch Test \xe5\x90\x8d".Length);
    }

    [Theory]
    [InlineData("Promods", " profile_name: Promods\r\n")]
    [InlineData("DispatchTest2", " profile_name: DispatchTest2\r\n")]
    [InlineData("Tuned EU", " profile_name: \"Tuned EU\"\r\n")]
    [InlineData("a-b", " profile_name: \"a-b\"\r\n")]
    public void Writes_simple_names_bare_like_the_game(string name, string expectedLine) =>
        ProfileText.WriteProfileName(Sample(), name).ShouldContain(expectedLine);

    [Fact]
    public void Reads_bare_profile_names() =>
        ProfileText.ReadProfileName(ProfileText.WriteProfileName(Sample(), "Promods")).ShouldBe("Promods");

    [Fact]
    public void Ignores_braces_inside_strings() =>
        ProfileText.ReadActiveMods(Sample()).Count.ShouldBe(3); // company_name contains '}'

    [Fact]
    public void Missing_user_profile_throws() =>
        Should.Throw<InvalidDataException>(() => ProfileText.ReadActiveMods("SiiNunit\r\n{\r\n}\r\n"));
}
