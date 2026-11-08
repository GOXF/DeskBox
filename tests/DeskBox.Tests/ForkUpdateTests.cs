using System.Net;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// GOXF/DeskBox fork: the installed fork build must update only from the
/// fork's own GitHub releases, and a user-initiated check additionally
/// reports (informationally) whether upstream has a newer base version.
/// </summary>
public sealed class ForkUpdateTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_PointAtForkReleasesAndSkipTheUpstreamManifest()
    {
        Assert.Equal("https://api.github.com/repos/GOXF/DeskBox/releases/latest", AppUpdateService.DefaultGitHubLatestReleaseApiUrl);
        Assert.Equal(string.Empty, AppUpdateService.DefaultManifestUrl);
        Assert.Equal("https://github.com/GOXF/DeskBox/releases/latest", AppUpdateService.DefaultManualDownloadUrl);
        Assert.Equal("https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest", ForkUpdateConfig.UpstreamLatestReleaseApiUrl);
    }

    [Fact]
    public async Task ContentManifestFetch_IsSkippedWithoutAFeed()
    {
        Assert.Null(await AppUpdateService.FetchContentManifestAsync());
    }

    [Fact]
    public async Task CheckForUpdatesAsync_DefaultServiceOnlyQueriesTheForkRelease()
    {
        var requested = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            requested.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "tag_name": "v1.5.9.2",
                      "html_url": "https://github.com/GOXF/DeskBox/releases/tag/v1.5.9.2",
                      "body": "fork build",
                      "assets": [
                        {
                          "name": "DeskBox_Setup_1.5.9.2_{{AppUpdateService.CurrentInstallerArchitectureSuffix}}.exe",
                          "browser_download_url": "https://github.com/GOXF/DeskBox/releases/download/v1.5.9.2/DeskBox_Setup_1.5.9.2_{{AppUpdateService.CurrentInstallerArchitectureSuffix}}.exe",
                          "size": 1000,
                          "digest": "sha256:8ecb3092ae5bd6883f8a75bbea03d9800251e0c23fdbe1bf4d91fd3a62565561"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);
        AppUpdateCheckResult result = await service.CheckForUpdatesAsync("1.5.9.1");

        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.5.9.2", result.Manifest!.Version);
        Assert.Equal(new[] { ForkUpdateConfig.ForkLatestReleaseApiUrl }, requested);
        Assert.DoesNotContain(requested, url => url.Contains("deskbox.fun", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requested, url => url.Contains("Tianyu199509", StringComparison.OrdinalIgnoreCase));

        AppUpdateCheckResult same = await service.CheckForUpdatesAsync("1.5.9.2");
        Assert.Equal(AppUpdateCheckStatus.UpToDate, same.Status);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_RejectsAReleasePointingAtUpstreamInstallers()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "tag_name": "v1.6.0",
                  "assets": [
                    {
                      "name": "DeskBox_Setup_1.6.0_{{AppUpdateService.CurrentInstallerArchitectureSuffix}}.exe",
                      "browser_download_url": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.6.0/DeskBox_Setup_1.6.0_{{AppUpdateService.CurrentInstallerArchitectureSuffix}}.exe",
                      "size": 1000,
                      "digest": "sha256:8ecb3092ae5bd6883f8a75bbea03d9800251e0c23fdbe1bf4d91fd3a62565561"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        }));

        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);
        AppUpdateCheckResult result = await service.CheckForUpdatesAsync("1.5.9.1");

        Assert.NotEqual(AppUpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoForkReleaseYetIsAFailureNotAnUpstreamFallback()
    {
        var requested = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);
        AppUpdateCheckResult result = await service.CheckForUpdatesAsync("1.5.9.1");

        Assert.Equal(AppUpdateCheckStatus.Failed, result.Status);
        Assert.Single(requested);
    }

    [Theory]
    [InlineData("1.5.9.1", "1.5.9")]
    [InlineData("v1.6.0", "1.6.0")]
    [InlineData("1.6.0-beta.1", "1.6.0")]
    [InlineData("1.5", "1.5.0")]
    [InlineData("not-a-version", "")]
    [InlineData("", "")]
    public void GetBaseVersion_TakesTheFirstThreeSegments(string version, string expected)
    {
        Assert.Equal(expected, ForkUpdateConfig.GetBaseVersion(version));
    }

    [Theory]
    [InlineData("1.5.9.1", "v1.6.0", true)]
    [InlineData("1.5.9.3", "v1.5.10", true)]
    [InlineData("1.5.9.1", "v1.5.9", false)]
    [InlineData("1.6.0.1", "v1.5.9", false)]
    [InlineData("1.5.9.7", "1.5.9.9", false)]
    [InlineData("1.5.9.1", "garbage", false)]
    public void IsUpstreamNewer_ComparesOnlyTheUpstreamBase(string current, string upstream, bool expected)
    {
        Assert.Equal(expected, ForkUpdateConfig.IsUpstreamNewer(current, upstream));
    }

    [Theory]
    [InlineData("1.5.9.1", "https://github.com/GOXF/DeskBox/releases/tag/v1.5.9.1")]
    [InlineData("v1.5.9.2", "https://github.com/GOXF/DeskBox/releases/tag/v1.5.9.2")]
    [InlineData("1.5.8", "https://github.com/Tianyu199509/DeskBox/releases/tag/v1.5.8")]
    public void GetReleaseTagUrl_RoutesForkAndUpstreamVersions(string version, string expected)
    {
        Assert.Equal(expected, ForkUpdateConfig.GetReleaseTagUrl(version));
    }

    [Fact]
    public void ParseLatestRelease_ReportsANewerUpstreamBase()
    {
        UpstreamReleaseInfo? info = UpstreamReleaseCheckService.ParseLatestRelease(
            "1.5.9.1",
            """{"tag_name":"v1.6.0","html_url":"https://github.com/Tianyu199509/DeskBox/releases/tag/v1.6.0"}""");

        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal("1.6.0", info.UpstreamVersion);
        Assert.Equal("1.5.9", info.CurrentBaseVersion);
        Assert.Equal("https://github.com/Tianyu199509/DeskBox/releases/tag/v1.6.0", info.ReleaseUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v1.5.9"}""", false)]
    [InlineData("""{"tag_name":"v1.5.8"}""", false)]
    public void ParseLatestRelease_SameOrOlderUpstreamIsNotNewer(string json, bool expected)
    {
        UpstreamReleaseInfo? info = UpstreamReleaseCheckService.ParseLatestRelease("1.5.9.1", json);
        Assert.NotNull(info);
        Assert.Equal(expected, info!.IsNewer);
        Assert.Equal(ForkUpdateConfig.UpstreamReleasesPageUrl, info.ReleaseUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    public void ParseLatestRelease_MalformedPayloadYieldsNull(string json)
    {
        Assert.Null(UpstreamReleaseCheckService.ParseLatestRelease("1.5.9.1", json));
    }

    [Fact]
    public async Task UpstreamCheck_NetworkFailuresNeverThrow()
    {
        using var failing = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Null(await UpstreamReleaseCheckService.CheckAsync(
            "1.5.9.1", failing, ForkUpdateConfig.UpstreamLatestReleaseApiUrl, CancellationToken.None));

        using var limited = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Null(await UpstreamReleaseCheckService.CheckAsync(
            "1.5.9.1", limited, ForkUpdateConfig.UpstreamLatestReleaseApiUrl, CancellationToken.None));
    }

    [Fact]
    public async Task UpstreamCheck_QueriesUpstreamLatestRelease()
    {
        string? requested = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v1.6.0"}""", Encoding.UTF8, "application/json")
            };
        }));

        UpstreamReleaseInfo? info = await UpstreamReleaseCheckService.CheckAsync(
            "1.5.9.1", client, ForkUpdateConfig.UpstreamLatestReleaseApiUrl, CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest", requested);
        Assert.True(info!.IsNewer);
    }

    [Fact]
    public void UpstreamNotice_IsLocalizedForChineseAndFallsBackToEnglish()
    {
        Assert.Equal(
            "上游 DeskBox 已发布 v1.6.0（你当前基于 v1.5.9）。自动同步会合并它，发布你的新版本后即可更新。",
            SettingsViewModel.FormatUpstreamReleaseNotice(LocalizationService.LanguageChinese, "1.6.0", "1.5.9"));
        Assert.Contains("v1.6.0", SettingsViewModel.FormatUpstreamReleaseNotice(LocalizationService.LanguageChineseTraditional, "1.6.0", "1.5.9"));
        Assert.StartsWith("Upstream DeskBox released v1.6.0", SettingsViewModel.FormatUpstreamReleaseNotice("ja-JP", "1.6.0", "1.5.9"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
