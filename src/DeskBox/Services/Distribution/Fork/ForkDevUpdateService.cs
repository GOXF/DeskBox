using System.Diagnostics;
using System.Text.Json;

namespace DeskBox.Services;

/// <summary>Latest dev-branch commit compared against the local dev build.</summary>
public sealed record ForkDevUpdateInfo(
    string LocalSha,
    string RemoteSha,
    string RemoteTitle,
    bool HasUpdate)
{
    public string LocalShortSha => ForkDevBuildInfo.ShortSha(LocalSha);
    public string RemoteShortSha => ForkDevBuildInfo.ShortSha(RemoteSha);
}

/// <summary>
/// GOXF/DeskBox fork: "local test build" update channel. Only active for a
/// <see cref="ForkDevBuildInfo.Current"/> local dev build; asks GitHub
/// (anonymous) for the tip of GOXF/DeskBox's dev branch and, on request,
/// hands over to scripts/fork-dev-update.ps1 (pull → Debug build → relaunch).
/// Every network failure yields null: this channel never surfaces errors.
/// </summary>
public static class ForkDevUpdateService
{
    public const string DevBranchCommitApiUrl = "https://api.github.com/repos/GOXF/DeskBox/commits/dev";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskBox-dev/{AppUpdateService.GetCurrentAppVersion()}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Null when this is not a local dev build or the check failed.</summary>
    public static Task<ForkDevUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        ForkDevBuild? build = ForkDevBuildInfo.Current;
        return build is null
            ? Task.FromResult<ForkDevUpdateInfo?>(null)
            : CheckAsync(build.LocalSha, s_httpClient, DevBranchCommitApiUrl, cancellationToken);
    }

    internal static async Task<ForkDevUpdateInfo?> CheckAsync(
        string localSha,
        HttpClient httpClient,
        string commitApiUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(commitApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseCommit(localSha, json);
        }
        catch (Exception ex)
        {
            App.Log($"[DevUpdate] dev branch check skipped: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Parses GET /repos/{o}/{r}/commits/{ref}: "sha" + first line of "commit.message".</summary>
    internal static ForkDevUpdateInfo? ParseCommit(string localSha, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sha", out JsonElement shaElement) ||
                shaElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? remoteSha = ForkDevBuildInfo.NormalizeSha(shaElement.GetString());
            string? local = ForkDevBuildInfo.NormalizeSha(localSha);
            if (remoteSha is null || local is null)
            {
                return null;
            }

            string title = string.Empty;
            if (root.TryGetProperty("commit", out JsonElement commit) &&
                commit.ValueKind == JsonValueKind.Object &&
                commit.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String)
            {
                title = FirstLine(message.GetString());
            }

            return new ForkDevUpdateInfo(local, remoteSha, title, ForkDevBuildInfo.IsRemoteNewer(local, remoteSha));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int newline = text.IndexOfAny(['\r', '\n']);
        string line = (newline >= 0 ? text[..newline] : text).Trim();
        return line.Length > 120 ? line[..119] + "…" : line;
    }

    /// <summary>
    /// powershell.exe arguments for the update script. -GraceSeconds lets
    /// DeskBox finish its own clean shutdown before the script force-kills
    /// whatever is left.
    /// </summary>
    internal static ProcessStartInfo BuildScriptStartInfo(ForkDevBuild build)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // Shell-execute => its own visible console window, independent of
            // DeskBox's lifetime, so the build progress stays on screen.
            UseShellExecute = true,
            WorkingDirectory = build.RepositoryRoot,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{build.ScriptPath}\" -Branch {ForkDevBuildInfo.DevBranch} -GraceSeconds 15",
        };
        return startInfo;
    }

    /// <summary>Starts the update script in its own window. The caller then exits DeskBox.</summary>
    public static bool TryStartUpdateScript(out string? error)
    {
        error = null;
        ForkDevBuild? build = ForkDevBuildInfo.Current;
        if (build is null)
        {
            error = "not a local dev build";
            return false;
        }

        if (!File.Exists(build.ScriptPath))
        {
            error = $"missing {build.ScriptPath}";
            return false;
        }

        try
        {
            using Process? process = Process.Start(BuildScriptStartInfo(build));
            if (process is null)
            {
                error = "powershell.exe did not start";
                return false;
            }

            App.Log($"[DevUpdate] Update script started (pid {process.Id}) from {build.RepositoryRoot}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            App.Log($"[DevUpdate] Update script failed to start: {ex}");
            return false;
        }
    }

    internal static string FormatStatus(string? cultureName, ForkDevUpdateInfo info)
    {
        bool zhHans = cultureName == LocalizationService.LanguageChinese;
        bool zhHant = cultureName == LocalizationService.LanguageChineseTraditional;
        string title = string.IsNullOrWhiteSpace(info.RemoteTitle) ? string.Empty : " " + info.RemoteTitle;
        if (info.HasUpdate)
        {
            return zhHans
                ? $"本地测试版：当前 {info.LocalShortSha}，有新提交 {info.RemoteShortSha}{title}"
                : zhHant
                    ? $"本地測試版：目前 {info.LocalShortSha}，有新提交 {info.RemoteShortSha}{title}"
                    : $"Local test build: current {info.LocalShortSha}, new commit {info.RemoteShortSha}{title}";
        }

        return zhHans
            ? $"本地测试版：当前 {info.LocalShortSha}，已是 dev 最新提交"
            : zhHant
                ? $"本地測試版：目前 {info.LocalShortSha}，已是 dev 最新提交"
                : $"Local test build: current {info.LocalShortSha}, up to date with dev";
    }

    internal static string FormatLocalOnlyStatus(string? cultureName, string localShortSha)
    {
        return cultureName switch
        {
            LocalizationService.LanguageChinese => $"本地测试版：当前 {localShortSha}（暂未取到 dev 最新提交）",
            LocalizationService.LanguageChineseTraditional => $"本地測試版：目前 {localShortSha}（暫未取得 dev 最新提交）",
            _ => $"Local test build: current {localShortSha} (dev tip not fetched yet)",
        };
    }

    internal static string FormatUpdateButton(string? cultureName) => cultureName switch
    {
        LocalizationService.LanguageChinese => "更新并重启",
        LocalizationService.LanguageChineseTraditional => "更新並重新啟動",
        _ => "Update and restart",
    };

    internal static (string Title, string Message) FormatNotification(string? cultureName, ForkDevUpdateInfo info)
    {
        string title = string.IsNullOrWhiteSpace(info.RemoteTitle) ? string.Empty : " " + info.RemoteTitle;
        return cultureName switch
        {
            LocalizationService.LanguageChinese =>
                ("DeskBox 本地测试版有新提交", $"dev 有新提交 {info.RemoteShortSha}{title}（当前 {info.LocalShortSha}）。在 设置 → 关于 → 更新详情 点「更新并重启」。"),
            LocalizationService.LanguageChineseTraditional =>
                ("DeskBox 本地測試版有新提交", $"dev 有新提交 {info.RemoteShortSha}{title}（目前 {info.LocalShortSha}）。在 設定 → 關於 → 更新詳情 點「更新並重新啟動」。"),
            _ =>
                ("DeskBox local test build: new commit", $"dev has {info.RemoteShortSha}{title} (current {info.LocalShortSha}). Settings → About → Update details → Update and restart."),
        };
    }

    internal static string FormatStartFailed(string? cultureName, string? error) => cultureName switch
    {
        LocalizationService.LanguageChinese => $"无法启动更新脚本：{error}",
        LocalizationService.LanguageChineseTraditional => $"無法啟動更新腳本：{error}",
        _ => $"Could not start the update script: {error}",
    };
}
