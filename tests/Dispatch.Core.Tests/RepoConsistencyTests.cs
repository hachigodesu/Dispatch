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

    [GeneratedRegex(@"public static string (\w+) => Get\(")]
    private static partial Regex PropertyRegex();
}
