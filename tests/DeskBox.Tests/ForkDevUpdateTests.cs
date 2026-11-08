using System.Net;
using System.Text;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// GOXF/DeskBox fork: local-test-build update channel. Only a DeskBox.exe
/// built inside a clone (".git" + scripts/fork-dev-update.ps1 above the exe
/// folder) enables it; it compares the build's commit with the tip of dev.
/// </summary>
public sealed class ForkDevUpdateTests : IDisposable
{
    private const string LocalSha = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RemoteSha = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "dev-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string CreateClone(string name, bool gitAsFile = false, bool withScript = true)
    {
        string root = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(root);
        if (gitAsFile)
        {
            string worktreeGitDir = Path.Combine(_tempRoot, name + "-gitdir");
            Directory.CreateDirectory(worktreeGitDir);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + worktreeGitDir);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
        }

        if (withScript)
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "fork-dev-update.ps1"), "# test");
        }

        return root;
    }

    private static string CreateBinDirectory(string root)
    {
        string bin = Path.Combine(root, "src", "DeskBox", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64");
        Directory.CreateDirectory(bin);
        return bin;
    }

    [Fact]
    public void FindRepositoryRoot_WalksUpFromTheExeFolderToTheClone()
    {
        string root = CreateClone("clone");
        string bin = CreateBinDirectory(root);

        Assert.Equal(Path.GetFullPath(root), ForkDevBuildInfo.FindRepositoryRoot(bin));
    }

    [Fact]
    public void FindRepositoryRoot_AcceptsAWorktreeGitFile()
    {
        string root = CreateClone("worktree", gitAsFile: true);

        Assert.Equal(Path.GetFullPath(root), ForkDevBuildInfo.FindRepositoryRoot(CreateBinDirectory(root)));
    }

    [Fact]
    public void FindRepositoryRoot_RequiresBothGitAndTheUpdateScript()
    {
        string noScript = CreateClone("no-script", withScript: false);
        Assert.Null(ForkDevBuildInfo.FindRepositoryRoot(CreateBinDirectory(noScript)));

        string noGit = Path.Combine(_tempRoot, "no-git");
        Directory.CreateDirectory(Path.Combine(noGit, "scripts"));
        File.WriteAllText(Path.Combine(noGit, "scripts", "fork-dev-update.ps1"), "# test");
        Assert.Null(ForkDevBuildInfo.FindRepositoryRoot(CreateBinDirectory(noGit)));
    }

    [Fact]
    public void InstalledBuild_IsNotALocalDevBuild()
    {
        string installDir = Path.Combine(_tempRoot, "Program Files", "DeskBox");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "DeskBox.exe"), string.Empty);

        Assert.Null(ForkDevBuildInfo.FindRepositoryRoot(installDir));
        Assert.Null(ForkDevBuildInfo.Detect(installDir, typeof(ForkDevBuildInfo).Assembly));
        Assert.Null(ForkDevBuildInfo.FindRepositoryRoot(null));
        Assert.Null(ForkDevBuildInfo.FindRepositoryRoot("   "));
    }

    [Fact]
    public void Detect_FallsBackToTheGitHeadWhenNothingIsEmbedded()
    {
        string root = CreateClone("loose-ref");
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/dev\n");
        Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(root, ".git", "refs", "heads", "dev"), LocalSha.ToUpperInvariant() + "\n");

        ForkDevBuild? build = ForkDevBuildInfo.Detect(CreateBinDirectory(root), assembly: null);

        Assert.NotNull(build);
        Assert.Equal(Path.GetFullPath(root), build!.RepositoryRoot);
        Assert.Equal(LocalSha, build.LocalSha);
        Assert.Equal("dev", build.Branch);
        Assert.Equal("1111111", build.ShortSha);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "scripts", "fork-dev-update.ps1"), build.ScriptPath);
    }

    [Fact]
    public void ReadHeadFromGitDirectory_ReadsPackedRefsAndDetachedHead()
    {
        string packed = CreateClone("packed");
        File.WriteAllText(Path.Combine(packed, ".git", "HEAD"), "ref: refs/heads/dev\n");
        File.WriteAllText(
            Path.Combine(packed, ".git", "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted\n" +
            RemoteSha + " refs/heads/custom\n" +
            LocalSha + " refs/heads/dev\n");
        Assert.Equal((LocalSha, "dev"), ForkDevBuildInfo.ReadHeadFromGitDirectory(packed));

        string detached = CreateClone("detached");
        File.WriteAllText(Path.Combine(detached, ".git", "HEAD"), RemoteSha + "\n");
        Assert.Equal((RemoteSha, (string?)null), ForkDevBuildInfo.ReadHeadFromGitDirectory(detached));

        string empty = CreateClone("empty");
        Assert.Null(ForkDevBuildInfo.ReadHeadFromGitDirectory(empty).Sha);
    }

    [Theory]
    [InlineData(LocalSha, RemoteSha, true)]
    [InlineData(LocalSha, LocalSha, false)]
    [InlineData("1111111", LocalSha, false)]
    [InlineData("1111111AAAA", LocalSha, false)]
    [InlineData(LocalSha, "1111111", false)]
    [InlineData(null, RemoteSha, false)]
    [InlineData(LocalSha, null, false)]
    [InlineData("not-a-sha", RemoteSha, false)]
    [InlineData(LocalSha, "zzzzzzzz", false)]
    public void IsRemoteNewer_ComparesShasSafely(string? local, string? remote, bool expected)
    {
        Assert.Equal(expected, ForkDevBuildInfo.IsRemoteNewer(local, remote));
    }

    [Fact]
    public void ParseCommit_ReadsShaAndFirstMessageLine()
    {
        string json = $$"""
            {
              "sha": "{{RemoteSha}}",
              "commit": { "message": "feat: 本地测试版更新通道\n\n详细说明" }
            }
            """;

        ForkDevUpdateInfo? info = ForkDevUpdateService.ParseCommit(LocalSha, json);

        Assert.NotNull(info);
        Assert.True(info!.HasUpdate);
        Assert.Equal("2222222", info.RemoteShortSha);
        Assert.Equal("1111111", info.LocalShortSha);
        Assert.Equal("feat: 本地测试版更新通道", info.RemoteTitle);
        Assert.Equal(
            "本地测试版：当前 1111111，有新提交 2222222 feat: 本地测试版更新通道",
            ForkDevUpdateService.FormatStatus(LocalizationService.LanguageChinese, info));
    }

    [Fact]
    public void ParseCommit_SameShaIsUpToDateAndMalformedIsNull()
    {
        ForkDevUpdateInfo? same = ForkDevUpdateService.ParseCommit(LocalSha, $$"""{ "sha": "{{LocalSha}}" }""");
        Assert.NotNull(same);
        Assert.False(same!.HasUpdate);
        Assert.Contains("已是 dev 最新提交", ForkDevUpdateService.FormatStatus(LocalizationService.LanguageChinese, same));

        Assert.Null(ForkDevUpdateService.ParseCommit(LocalSha, "not json"));
        Assert.Null(ForkDevUpdateService.ParseCommit(LocalSha, "[]"));
        Assert.Null(ForkDevUpdateService.ParseCommit(LocalSha, """{ "message": "Not Found" }"""));
        Assert.Null(ForkDevUpdateService.ParseCommit("", $$"""{ "sha": "{{RemoteSha}}" }"""));
    }

    [Fact]
    public async Task CheckAsync_QueriesTheDevBranchAndFailsSilently()
    {
        var requested = new List<string>();
        using var ok = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "sha": "{{RemoteSha}}", "commit": { "message": "fix: x" } }""", Encoding.UTF8, "application/json")
            };
        }));
        ForkDevUpdateInfo? info = await ForkDevUpdateService.CheckAsync(LocalSha, ok, ForkDevUpdateService.DevBranchCommitApiUrl, CancellationToken.None);
        Assert.Equal(new[] { "https://api.github.com/repos/GOXF/DeskBox/commits/dev" }, requested);
        Assert.True(info!.HasUpdate);

        using var rateLimited = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Null(await ForkDevUpdateService.CheckAsync(LocalSha, rateLimited, ForkDevUpdateService.DevBranchCommitApiUrl, CancellationToken.None));

        using var offline = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Null(await ForkDevUpdateService.CheckAsync(LocalSha, offline, ForkDevUpdateService.DevBranchCommitApiUrl, CancellationToken.None));
    }

    [Fact]
    public void BuildScriptStartInfo_RunsTheRepoScriptInAVisibleWindow()
    {
        string root = CreateClone("script");
        var build = new ForkDevBuild(Path.GetFullPath(root), LocalSha, "dev");

        var startInfo = ForkDevUpdateService.BuildScriptStartInfo(build);

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(build.RepositoryRoot, startInfo.WorkingDirectory);
        Assert.Contains("-ExecutionPolicy Bypass", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains($"-File \"{build.ScriptPath}\"", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains("-Branch dev", startInfo.Arguments, StringComparison.Ordinal);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
