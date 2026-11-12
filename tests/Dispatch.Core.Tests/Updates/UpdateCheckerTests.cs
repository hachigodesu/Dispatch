using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dispatch.Core.Tests.Mods;
using Dispatch.Core.Updates;

namespace Dispatch.Core.Tests.Updates;

/// <summary>Answers requests from a table; no network.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}

public class UpdateCheckerTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is an installer");
    private static readonly string InstallerSha = Convert.ToHexString(SHA256.HashData(Installer));

    private static string Release(string tag, bool draft = false, bool prerelease = true, string? digest = null, string body = "",
                                  long? size = null) => $$"""
        {
          "tag_name": "{{tag}}", "name": "Dispatch {{tag}}", "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/hachigodesu/Dispatch/releases/tag/{{tag}}",
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "assets": [
            { "name": "Dispatch-{{tag.TrimStart('v')}}-setup.exe", "size": {{size ?? Installer.Length}},
              "browser_download_url": "https://github.com/hachigodesu/Dispatch/releases/download/{{tag}}/Dispatch-{{tag.TrimStart('v')}}-setup.exe"
              {{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} },
            { "name": "Dispatch-{{tag.TrimStart('v')}}-portable.zip", "size": 10,
              "browser_download_url": "https://github.com/hachigodesu/Dispatch/releases/download/{{tag}}/Dispatch-{{tag.TrimStart('v')}}-portable.zip" }
          ]
        }
        """;

    private static UpdateChecker Checker(string json, out FakeHttp http, byte[]? download = null)
    {
        http = new FakeHttp(req => req.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download ?? Installer), RequestMessage = req });
        return new UpdateChecker(new HttpClient(http));
    }

    [Fact]
    public async Task Picks_the_newest_newer_release_and_skips_drafts()
    {
        string json = $"[{Release("v0.7.0", draft: true)}, {Release("v0.6.1", digest: "sha256:" + InstallerSha.ToLowerInvariant())}, {Release("v0.6.0")}, {Release("v0.5.0")}]";
        var update = await Checker(json, out var http).CheckAsync(new Version(0, 5, 0, 0), TestContext.Current.CancellationToken);

        update.ShouldNotBeNull();
        update.Version.ShouldBe(new Version(0, 6, 1));
        update.Setup!.Sha256.ShouldBe(InstallerSha);
        update.Portable!.Name.ShouldBe("Dispatch-0.6.1-portable.zip");
        http.Requests.Single().Headers.UserAgent.ToString().ShouldStartWith("Dispatch-update-check/0.5.0");
    }

    [Fact]
    public async Task Up_to_date_gives_null()
    {
        string json = $"[{Release("v0.5.0")}, {Release("0.4.2")}]";
        (await Checker(json, out _).CheckAsync(new Version(0, 5, 0), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Github_errors_are_http_exceptions()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Should.ThrowAsync<HttpRequestException>(() =>
            new UpdateChecker(new HttpClient(http)).CheckAsync(new Version(0, 5, 0), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.6", "0.6.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("latest", null)]
    public void Parses_tags(string tag, string? expected) =>
        UpdateChecker.ParseTag(tag)?.ToString().ShouldBe(expected);

    [Fact]
    public async Task Download_is_verified_with_the_checksum_from_the_release_notes()
    {
        string body = $"## Checksums (SHA-256)\n```\n{InstallerSha}  Dispatch-0.6.0-setup.exe\n```";
        var checker = Checker($"[{Release("v0.6.0", body: body)}]", out _);
        var update = await checker.CheckAsync(new Version(0, 5, 0), TestContext.Current.CancellationToken);
        using var tmp = new TempDir();

        string file = await checker.DownloadAsync(update!.Setup!, tmp.Path, null, TestContext.Current.CancellationToken);

        File.ReadAllBytes(file).ShouldBe(Installer);
        Path.GetFileName(file).ShouldBe("Dispatch-0.6.0-setup.exe");
    }

    [Fact]
    public async Task Tampered_download_is_deleted_and_refused()
    {
        var tampered = Encoding.UTF8.GetBytes("pretend this is malware!!!!!");
        var checker = Checker($"[{Release("v0.6.0", digest: "sha256:" + InstallerSha)}]", out _, tampered);
        var update = await checker.CheckAsync(new Version(0, 5, 0), TestContext.Current.CancellationToken);
        using var tmp = new TempDir();

        var ex = await Should.ThrowAsync<InvalidDataException>(() =>
            checker.DownloadAsync(update!.Setup!, tmp.Path, null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("checksum");
        Directory.EnumerateFiles(tmp.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_checksum_nothing_is_downloaded()
    {
        var checker = Checker($"[{Release("v0.6.0")}]", out var http);
        var update = await checker.CheckAsync(new Version(0, 5, 0), TestContext.Current.CancellationToken);
        using var tmp = new TempDir();

        await Should.ThrowAsync<InvalidDataException>(() =>
            checker.DownloadAsync(update!.Setup!, tmp.Path, null, TestContext.Current.CancellationToken));
        http.Requests.Count.ShouldBe(1); // only the release list
    }

    [Fact]
    public async Task Links_outside_the_repository_are_refused()
    {
        var checker = Checker("[]", out var http);
        var evil = new ReleaseAsset("Dispatch-9.9.9-setup.exe", new Uri("https://example.com/hachigodesu/Dispatch/releases/download/x/a.exe"),
                                    10, InstallerSha);
        using var tmp = new TempDir();
        await Should.ThrowAsync<InvalidDataException>(() => checker.DownloadAsync(evil, tmp.Path, null, TestContext.Current.CancellationToken));
        var traversal = evil with
        {
            Name = @"..\evil.exe",
            DownloadUrl = new Uri("https://github.com/hachigodesu/Dispatch/releases/download/x/evil.exe"),
        };
        await Should.ThrowAsync<InvalidDataException>(() => checker.DownloadAsync(traversal, tmp.Path, null, TestContext.Current.CancellationToken));
        http.Requests.ShouldBeEmpty();
    }
}
