using Dispatch.Core.Profiles;

namespace Dispatch.Core.Tests.Profiles;

public class ProfileNameValidatorTests
{
    private static readonly string[] Names = ["Promods", "DispatchTest", "日本"];
    private static readonly string[] Folders = Names.Select(ProfileNameCodec.ToFolderName).ToArray();

    private static NameValidation V(string name) => ProfileNameValidator.Validate(name, Names, Folders);

    [Theory]
    [InlineData("DispatchTest2")]
    [InlineData("12345678901234567890")] // exactly 20, the game's limit
    [InlineData("名無しの運送")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")] // the game accepts these (S7)
    [InlineData("v1.62-test")]
    public void Accepts_names_the_game_accepts(string name)
    {
        var r = V(name);
        r.IsValid.ShouldBeTrue(string.Join("; ", r.Errors));
        r.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("", "Enter a name")]
    [InlineData("   ", "Enter a name")]
    [InlineData(" lead", "start or end with a space")]
    [InlineData("trail ", "start or end with a space")]
    [InlineData("123456789012345678901", "at most 20")]
    [InlineData("Truck 🚚", "emoji")]
    [InlineData("tab\there", "control character")]
    [InlineData("DispatchTest", "already exists")]
    [InlineData("日本", "already exists")]
    public void Rejects_invalid_names(string name, string reason)
    {
        var r = V(name);
        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Same_name_in_other_case_is_only_a_warning()
    {
        var r = V("dispatchtest");
        r.IsValid.ShouldBeTrue();
        r.Warnings.Single().ShouldContain("DispatchTest");
    }

    [Fact]
    public void Existing_folder_without_known_name_is_still_a_clash()
    {
        var r = ProfileNameValidator.Validate("Ghost", [], [ProfileNameCodec.ToFolderName("Ghost").ToLowerInvariant()]);
        r.Errors.ShouldContain(e => e.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void Checks_windows_path_length()
    {
        string root = @"C:\" + new string('x', 180) + @"\profiles";
        string deepFile = @"save\autosave_drive_5\preview_screenshot.tga";
        ProfileNameValidator.Validate("Short", [], [], root, deepFile).IsValid.ShouldBeTrue();
        var r = ProfileNameValidator.Validate("名無しの運送会社です", [], [], root, deepFile); // 10 CJK chars = 60 hex chars
        r.Errors.ShouldContain(e => e.Contains("too long for this folder location", StringComparison.Ordinal));
    }
}
