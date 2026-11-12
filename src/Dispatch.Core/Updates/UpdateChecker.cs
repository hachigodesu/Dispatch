using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dispatch.Core.Updates;

/// <summary>One downloadable file of a release.</summary>
/// <param name="Sha256">Expected SHA-256 (upper-case hex), from GitHub's asset digest or the release notes; null if unknown.</param>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

public sealed record UpdateInfo(Version Version, string Tag, string Title, Uri PageUrl, bool IsPrerelease, string Notes,
                                ReleaseAsset? Setup, ReleaseAsset? Portable);

/// <summary>
/// Looks for a newer Dispatch release on GitHub and downloads its installer. Sends nothing but a plain request for the
/// public release list (no IDs, no usage data). Downloads are only accepted when their SHA-256 matches the release.
/// </summary>
public sealed partial class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "hachigodesu/Dispatch";

    /// <summary>Installers are about 60 MB; anything far bigger isn't ours.</summary>
    public const long MaxAssetBytes = 500L * 1024 * 1024;

    /// <summary>The newest non-draft release newer than <paramref name="current"/>, or null when up to date.</summary>
    /// <exception cref="HttpRequestException">GitHub couldn't be reached or answered with an error.</exception>
    /// <exception cref="InvalidDataException">The answer wasn't a release list.</exception>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=20");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Dispatch-update-check", current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return Newest(ParseReleases(json), current);
    }

    internal static UpdateInfo? Newest(IEnumerable<UpdateInfo> releases, Version current) =>
        releases.Where(r => r.Version > Normalize(current)).OrderByDescending(r => r.Version).FirstOrDefault();

    public static IReadOnlyList<UpdateInfo> ParseReleases(string json)
    {
        var list = new List<UpdateInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("GitHub didn't return a release list.");
            }

            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                {
                    continue;
                }

                string tag = Str(r, "tag_name") ?? "";
                if (ParseTag(tag) is not { } version || !Uri.TryCreate(Str(r, "html_url"), UriKind.Absolute, out var page))
                {
                    continue;
                }

                string notes = Str(r, "body") ?? "";
                var assets = new List<ReleaseAsset>();
                if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        string? name = Str(a, "name");
                        if (name is null || !Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url))
                        {
                            continue;
                        }

                        long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                        assets.Add(new ReleaseAsset(name, url, size, DigestOf(Str(a, "digest")) ?? ChecksumInNotes(notes, name)));
                    }
                }

                list.Add(new UpdateInfo(version, tag, Str(r, "name") ?? tag, page,
                                        r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(), notes,
                                        assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)),
                                        assets.FirstOrDefault(a => a.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("GitHub's answer couldn't be read.", ex);
        }

        return list;
    }

    /// <summary>"v0.6.0" or "0.6.0" → 0.6.0. Pre-release suffixes ("-beta") are ignored.</summary>
    public static Version? ParseTag(string tag)
    {
        var m = TagRegex().Match(tag);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>GitHub's "sha256:abc…" asset digest.</summary>
    private static string? DigestOf(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64
            ? digest[7..].ToUpperInvariant()
            : null;

    /// <summary>A "&lt;64 hex&gt;  &lt;file name&gt;" line, as in Dispatch's release notes.</summary>
    private static string? ChecksumInNotes(string notes, string fileName)
    {
        foreach (Match m in ChecksumRegex().Matches(notes))
        {
            if (string.Equals(m.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return m.Groups[1].Value.ToUpperInvariant();
            }
        }

        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Downloads an asset into <paramref name="directory"/> and checks it. Only https links into this repository's
    /// GitHub releases are followed, and the file is deleted again unless its size and SHA-256 match.
    /// </summary>
    /// <returns>The verified file.</returns>
    /// <exception cref="InvalidDataException">The file couldn't be verified (no checksum, wrong size or wrong hash).</exception>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string directory, IProgress<double>? progress, CancellationToken cancel)
    {
        if (asset.Sha256 is null)
        {
            throw new InvalidDataException("This release doesn't publish a checksum for its installer, so it can't be verified.");
        }

        if (asset.DownloadUrl.Scheme != Uri.UriSchemeHttps || asset.DownloadUrl.Host != "github.com" ||
            !asset.DownloadUrl.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal) ||
            asset.Size is <= 0 or > MaxAssetBytes || Path.GetFileName(asset.Name) != asset.Name)
        {
            throw new InvalidDataException("The update's download link doesn't look like a Dispatch release.");
        }

        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, asset.Name);
        string temp = target + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Dispatch-update-check", "1"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The download was redirected away from https.");
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > asset.Size)
                    {
                        throw new InvalidDataException("The download is bigger than the release says.");
                    }

                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    progress?.Report((double)done / asset.Size);
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset());
            if (done != asset.Size || !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded installer doesn't match the release's checksum, so it wasn't kept.");
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [GeneratedRegex(@"^v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\b([0-9A-Fa-f]{64})[ \t]+\*?([^\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumRegex();

    /// <summary>For logs and the UI: "60.2 MB".</summary>
    public static string FormatSize(long bytes) =>
        (bytes / 1048576.0).ToString("F1", CultureInfo.CurrentCulture) + " MB";
}
