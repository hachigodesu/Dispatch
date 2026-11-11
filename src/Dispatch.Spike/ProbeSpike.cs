using Dispatch.Core.Mods.Packages;

namespace Dispatch.Spike;

/// <summary>Low-level HashFS diagnostics: `probe &lt;archive&gt; [path...]`.</summary>
internal static class ProbeSpike
{
    public static int Run(string archive, IEnumerable<string> paths)
    {
        using var pkg = (HashFsPackage)ModPackage.Open(archive);
        Console.WriteLine($"{Path.GetFileName(archive)}: v{pkg.Version}, {pkg.EntryCount} entries, table-at-end fallback {pkg.UsedTableAtEndFallback}");
        Console.WriteLine($"CityHash64(\"\") = {CityHash.Hash64(""):X16} (expect 9AE16A3B2F90404F)");
        foreach (var p in paths)
        {
            Console.WriteLine($"  '{p}': {pkg.DescribeV2(p)}");
            try
            {
                var data = pkg.TryReadFile(p);
                Console.WriteLine($"     file: {(data is null ? "null" : $"{data.Length} bytes, starts '{System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(40, data.Length)).ReplaceLineEndings(" ")}'")}");
                var list = pkg.ListDirectory(p);
                Console.WriteLine($"     dir: {(list is null ? "null" : string.Join(", ", list.Take(12)))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        return 0;
    }
}
