using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// GOXF/DeskBox fork: "local test build" line + "update and restart" button
/// under About → Update details. Visible only for a local dev build (exe
/// inside a clone that has scripts/fork-dev-update.ps1); installed builds
/// never show it. Bound with x:Bind, so the AOT bindable-property list
/// (pinned by upstream tests) is untouched. Text lives in code for the same
/// merge-conflict reason as SettingsViewModel.ForkUpstream.cs.
/// </summary>
public partial class SettingsViewModel
{
    private ForkDevUpdateInfo? _forkDevUpdateInfo;
    private int _forkDevUpdateCheckInFlight;
    private int _forkDevUpdateCheckStarted;

    public Visibility ForkDevUpdateVisibility
    {
        get
        {
            // First render of the About section starts one check; the
            // explicit "check for updates" path re-runs it.
            if (ForkDevBuildInfo.IsLocalDevBuild &&
                Interlocked.Exchange(ref _forkDevUpdateCheckStarted, 1) == 0)
            {
                StartForkDevUpdateCheck();
            }

            return ForkDevBuildInfo.IsLocalDevBuild ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public string ForkDevUpdateStatusText
    {
        get
        {
            ForkDevBuild? build = ForkDevBuildInfo.Current;
            if (build is null)
            {
                return string.Empty;
            }

            return _forkDevUpdateInfo is { } info
                ? ForkDevUpdateService.FormatStatus(_localizationService.CurrentCultureName, info)
                : ForkDevUpdateService.FormatLocalOnlyStatus(_localizationService.CurrentCultureName, build.ShortSha);
        }
    }

    public Visibility ForkDevUpdateButtonVisibility =>
        _forkDevUpdateInfo is { HasUpdate: true } ? Visibility.Visible : Visibility.Collapsed;

    public string ForkDevUpdateButtonText =>
        ForkDevUpdateService.FormatUpdateButton(_localizationService.CurrentCultureName);

    /// <summary>Fire-and-forget; never awaited by the regular update flow.</summary>
    private void StartForkDevUpdateCheck()
    {
        if (!ForkDevBuildInfo.IsLocalDevBuild ||
            Interlocked.Exchange(ref _forkDevUpdateCheckInFlight, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _forkDevUpdateCheckStarted, 1);
        _ = RunForkDevUpdateCheckAsync();
    }

    private async Task RunForkDevUpdateCheckAsync()
    {
        try
        {
            ForkDevUpdateInfo? info = await ForkDevUpdateService.CheckAsync().ConfigureAwait(false);
            if (info is not null)
            {
                SetForkDevUpdateInfo(info);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DevUpdate] Settings check failed: {ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref _forkDevUpdateCheckInFlight, 0);
        }
    }

    private void SetForkDevUpdateInfo(ForkDevUpdateInfo info)
    {
        if (_uiDispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => SetForkDevUpdateInfo(info));
            return;
        }

        _forkDevUpdateInfo = info;
        OnPropertyChanged(nameof(ForkDevUpdateStatusText));
        OnPropertyChanged(nameof(ForkDevUpdateButtonVisibility));
    }

    /// <summary>Starts scripts/fork-dev-update.ps1; the window then shuts DeskBox down.</summary>
    public bool StartForkDevUpdateScript(out string message)
    {
        bool started = ForkDevUpdateService.TryStartUpdateScript(out string? error);
        message = started ? string.Empty : ForkDevUpdateService.FormatStartFailed(_localizationService.CurrentCultureName, error);
        return started;
    }
}
