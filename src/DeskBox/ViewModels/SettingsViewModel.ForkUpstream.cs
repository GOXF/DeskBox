using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// GOXF/DeskBox fork: on a user-initiated update check, also asks upstream
/// (Tianyu199509/DeskBox) whether it has released a newer base version and
/// shows a one-line notice under the update details. The fork's own update
/// flow never waits on this and is unaffected when it fails.
///
/// The notice text lives here instead of Strings/*.json on purpose: those
/// files are upstream-owned and edited on every upstream release, so fork
/// keys there would conflict on nearly every automatic sync.
/// </summary>
public partial class SettingsViewModel
{
    private string _upstreamReleaseNoticeText = string.Empty;
    private int _upstreamReleaseCheckInFlight;

    public string UpstreamReleaseNoticeText => _upstreamReleaseNoticeText;

    public Visibility UpstreamReleaseNoticeVisibility =>
        IsDirectInstallerUpdateDelivery && !string.IsNullOrWhiteSpace(_upstreamReleaseNoticeText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Fire-and-forget: started alongside the fork's own check, never awaited
    /// by it. Store builds skip it (they update through the Store).
    /// </summary>
    private void StartUpstreamReleaseCheck()
    {
        if (!IsDirectInstallerUpdateDelivery ||
            Interlocked.Exchange(ref _upstreamReleaseCheckInFlight, 1) == 1)
        {
            return;
        }

        _ = RunUpstreamReleaseCheckAsync(AppVersion);
    }

    private async Task RunUpstreamReleaseCheckAsync(string currentVersion)
    {
        try
        {
            UpstreamReleaseInfo? info = await UpstreamReleaseCheckService.CheckAsync(currentVersion)
                .ConfigureAwait(false);
            if (info is null)
            {
                // Unknown: keep whatever an earlier successful check showed.
                return;
            }

            string notice = info.IsNewer
                ? FormatUpstreamReleaseNotice(_localizationService.CurrentCultureName, info.UpstreamVersion, info.CurrentBaseVersion)
                : string.Empty;
            SetUpstreamReleaseNotice(notice);
        }
        catch (Exception ex)
        {
            App.Log($"[Update] Upstream release notice failed: {ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref _upstreamReleaseCheckInFlight, 0);
        }
    }

    private void SetUpstreamReleaseNotice(string notice)
    {
        if (_uiDispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => SetUpstreamReleaseNotice(notice));
            return;
        }

        if (string.Equals(_upstreamReleaseNoticeText, notice, StringComparison.Ordinal))
        {
            return;
        }

        _upstreamReleaseNoticeText = notice;
        OnPropertyChanged(nameof(UpstreamReleaseNoticeText));
        OnPropertyChanged(nameof(UpstreamReleaseNoticeVisibility));
    }

    internal static string FormatUpstreamReleaseNotice(string? cultureName, string upstreamVersion, string currentBaseVersion)
    {
        return cultureName switch
        {
            LocalizationService.LanguageChinese =>
                $"上游 DeskBox 已发布 v{upstreamVersion}（你当前基于 v{currentBaseVersion}）。自动同步会合并它，发布你的新版本后即可更新。",
            LocalizationService.LanguageChineseTraditional =>
                $"上游 DeskBox 已發布 v{upstreamVersion}（你目前基於 v{currentBaseVersion}）。自動同步會合併它，發布你的新版本後即可更新。",
            _ =>
                $"Upstream DeskBox released v{upstreamVersion} (this build is based on v{currentBaseVersion}). The automatic sync will merge it; update once your new fork release is published.",
        };
    }
}
