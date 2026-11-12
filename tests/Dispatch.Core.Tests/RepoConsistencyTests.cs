using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dispatch.Core.Tests;

/// <summary>Checks that hand-maintained or generated files in the repo stay in sync.</summary>
public partial class RepoConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dispatch.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Strings_designer_matches_resx()
    {
        string resources = Path.Combine(RepoRoot(), "src", "Dispatch.App", "Resources");
        var resxKeys = XDocument.Load(Path.Combine(resources, "Strings.resx")).Root!
            .Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
        var designerKeys = PropertyRegex().Matches(File.ReadAllText(Path.Combine(resources, "Strings.Designer.cs")))
            .Select(m => m.Groups[1].Value).ToHashSet();

        designerKeys.ShouldBe(resxKeys, ignoreOrder: true,
            customMessage: "Strings.resx and Strings.Designer.cs differ: run  python -I tools/gen-strings.py");
    }

    /// <summary>
    /// Every translation has exactly the English keys, no empty texts, the same {0}-style placeholders (a missing or
    /// extra one would crash string.Format at run time) and the same number of '|' (file dialog filters).
    /// </summary>
    [Fact]
    public void Translations_match_english()
    {
        string resources = Path.Combine(RepoRoot(), "src", "Dispatch.App", "Resources");
        static Dictionary<string, string> Load(string file) => XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
        static string Placeholders(string s) => string.Join(",", PlaceholderRegex().Matches(s).Select(m => m.Value).Order(StringComparer.Ordinal));

        var english = Load(Path.Combine(resources, "Strings.resx"));
        var files = Directory.GetFiles(resources, "Strings.*.resx");
        files.Length.ShouldBeGreaterThanOrEqualTo(6);
        foreach (string file in files)
        {
            string lang = Path.GetFileName(file);
            var tr = Load(file);
            tr.Keys.ShouldBe(english.Keys, ignoreOrder: true, customMessage: $"{lang}: keys differ from Strings.resx");
            foreach (var (key, text) in english)
            {
                tr[key].Trim().ShouldNotBeEmpty($"{lang}: {key} is empty");
                Placeholders(tr[key]).ShouldBe(Placeholders(text), $"{lang}: placeholders of {key} differ");
                tr[key].Count(c => c == '|').ShouldBe(text.Count(c => c == '|'), $"{lang}: '|' count of {key} differs");
            }
        }
    }

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"public static string (\w+) => Get\(")]
    private static partial Regex PropertyRegex();
}
