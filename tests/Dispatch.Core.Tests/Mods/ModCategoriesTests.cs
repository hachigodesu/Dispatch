using Dispatch.Core.Mods;

namespace Dispatch.Core.Tests.Mods;

public class ModCategoriesTests
{
    [Theory]
    [InlineData("map", "map")]
    [InlineData("maps", "map")]       // the real-world mistake (5 of the user's mods)
    [InlineData("Map", "map")]
    [InlineData("sounds", "sound")]
    [InlineData("trucks", "truck")]
    [InlineData("model", "models")]
    [InlineData("tuning parts", "tuning_parts")]
    [InlineData("Tuning-Parts", "tuning_parts")]
    [InlineData("paintjob", "paint_job")]
    [InlineData("weather", "weather_setup")]
    [InlineData("ui", "ui")]
    public void Matches_common_mistakes_to_the_valid_category(string declared, string expected) =>
        ModCategories.Canonical(declared).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vehicles")]
    [InlineData("everything")]
    public void Unknown_values_stay_unknown(string declared) =>
        ModCategories.Canonical(declared).ShouldBeNull();

    [Fact]
    public void Linter_still_reports_the_invalid_value()
    {
        var m = new ModManifest(".x", null, "M", null, ["maps"], null, null, [], [], null, []);
        ModLinter.Check(m, false, null, false, false).ShouldContain(i => i.Message.Contains("'maps'", StringComparison.Ordinal));
    }
}
