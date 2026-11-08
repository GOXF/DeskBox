using DeskBox.Services;
using H.NotifyIcon.Core;

namespace DeskBox;

/// <summary>
/// GOXF/DeskBox fork: background loop of the local-test-build update channel.
/// Only runs for a local dev build (<see cref="ForkDevBuildInfo.Current"/>):
/// shortly after startup and then every 30 minutes it asks GitHub for the tip
/// of GOXF/DeskBox's dev branch and, once per new commit, shows the same
/// style of update toast (tray balloon as fallback) the regular updater
/// uses. Clicking it opens About, where "update and restart" lives.
/// </summary>
public partial class App
{
    private string? _forkDevUpdateNotifiedSha;

    private void ScheduleForkDevUpdateCheck()
    {
        ForkDevBuild? build = ForkDevBuildInfo.Current;
        if (build is null)
        {
            return;
        }

        Log($"[DevUpdate] Local dev build {build.ShortSha} ({build.Branch}) at {build.RepositoryRoot}");
        _ = Task.Run(() => ForkDevUpdateLoopAsync(IsStartupMode ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15)));
    }

    private async Task ForkDevUpdateLoopAsync(TimeSpan initialDelay)
    {
        DateTimeOffset nextCheckAt = DateTimeOffset.Now + initialDelay;
        while (!IsShuttingDown)
        {
            // Wall-clock deadline, re-evaluated after every wake (timers
            // pause during sleep), same as the regular update loop.
            TimeSpan remaining = nextCheckAt - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < TimeSpan.FromMinutes(5) ? remaining : TimeSpan.FromMinutes(5));
                continue;
            }

            try
            {
                ForkDevUpdateInfo? info = await ForkDevUpdateService.CheckAsync();
                if (info is { HasUpdate: true } && !IsShuttingDown)
                {
                    Log($"[DevUpdate] dev has {info.RemoteShortSha} (local {info.LocalShortSha})");
                    UiDispatcherQueue?.TryEnqueue(() => ShowForkDevUpdateNotification(info));
                }
            }
            catch (Exception ex)
            {
                Log($"[DevUpdate] Background check crashed: {ex.GetType().Name}");
            }

            nextCheckAt = DateTimeOffset.Now + ForkDevUpdateService.CheckInterval;
        }
    }

    private void ShowForkDevUpdateNotification(ForkDevUpdateInfo info)
    {
        if (IsShuttingDown ||
            string.Equals(_forkDevUpdateNotifiedSha, info.RemoteSha, StringComparison.Ordinal) ||
            !SettingsService.Settings.ShowUpdateToastNotification)
        {
            return;
        }

        (string title, string message) = ForkDevUpdateService.FormatNotification(LocalizationService.CurrentCultureName, info);
        // {type=update} without an action opens Settings → About, where the
        // "update and restart" button is.
        var openAboutArguments = new Dictionary<string, string>
        {
            ["type"] = "update"
        };
        if (_nativeNotificationService?.TryShow(
                title,
                message,
                openAboutArguments,
                options: new NativeAppNotificationOptions(
                    Tag: "dev-update-available",
                    Group: "update")) == true)
        {
            _forkDevUpdateNotifiedSha = info.RemoteSha;
            return;
        }

        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(8));
            _forkDevUpdateNotifiedSha = info.RemoteSha;
        }
        catch (Exception ex)
        {
            Log($"[DevUpdate] Tray notification failed: {ex.Message}");
        }
    }
}
