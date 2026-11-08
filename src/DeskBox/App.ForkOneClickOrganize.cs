using DeskBox.Services;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox;

/// <summary>
/// GOXF fork: 「整理桌面」 finishes in one click. The tray item, the settings
/// buttons (一键整理桌面 / 整理桌面, via ISettingsAppActions) and the mapped
/// widgets' menus run <see cref="ForkOneClickOrganizer"/> directly and report
/// with a notification (with 撤销 when files were moved); the old preview
/// window stays available as 「整理预览…」 in the tray menu and the mapped
/// widgets' menus.
/// </summary>
public partial class App
{
    private int _forkOneClickOrganizeRunning;

    /// <summary>One-click organize (fire and forget; safe from any thread).</summary>
    public void OrganizeDesktopOneClick(IntPtr ownerHwnd = default)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => OrganizeDesktopOneClick(ownerHwnd));
            return;
        }

        _ = OrganizeDesktopOneClickAsync(ownerHwnd);
    }

    /// <summary>The previous behavior of 「整理桌面」: the organize preview window.</summary>
    public void OpenDesktopOrganizationPreview(IntPtr ownerHwnd = default)
    {
        try
        {
            CancelBackgroundMemoryCleanup();
            ShowDesktopOrganizationWindow(ownerHwnd);
        }
        catch (Exception ex)
        {
            Log($"[OneClickOrganize] Opening the organize preview failed: {ex.Message}");
        }
    }

    private async Task OrganizeDesktopOneClickAsync(IntPtr ownerHwnd)
    {
        if (Interlocked.Exchange(ref _forkOneClickOrganizeRunning, 1) == 1)
        {
            Log("[OneClickOrganize] Already running; ignored.");
            return;
        }

        try
        {
            CancelBackgroundMemoryCleanup();
            if (WidgetManager is not { } widgetManager || _desktopOrganizationWindow is not null)
            {
                // Not ready yet, or a preview is open: finish in the preview.
                OpenDesktopOrganizationPreview(ownerHwnd);
                return;
            }

            var coordinator = new DesktopOrganizationCoordinator(
                SettingsService,
                FileService,
                widgetManager,
                OrganizerService,
                LocalizationService);
            var organizer = new ForkOneClickOrganizer(coordinator, MappedOrganizeService.Current, ResolveForkCategoryName);
            ForkOneClickOrganizeResult result = await organizer.RunAsync(ownerHwnd);
            Log($"[OneClickOrganize] mode={result.Mode} outcome={result.Outcome} organized={result.OrganizedCount} retained={result.RetainedCount}");
            if (result.Outcome == ForkOneClickOrganizeOutcome.NeedsPreview)
            {
                OpenDesktopOrganizationPreview(ownerHwnd);
                return;
            }

            ShowForkOneClickOrganizeNotification(result);
            if (result.Outcome == ForkOneClickOrganizeOutcome.Organized)
            {
                _settingsWindow?.RefreshDesktopOrganizationState();
                _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostDesktopOrgRefresh, null);
            }
        }
        catch (Exception ex)
        {
            Log($"[OneClickOrganize] Failed: {ex}");
            ShowForkOrganizeNotice(
                ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OneClickFailedTitle),
                ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OneClickFailedBody),
                historyId: null);
        }
        finally
        {
            Interlocked.Exchange(ref _forkOneClickOrganizeRunning, 0);
            ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        }
    }

    private string ResolveForkCategoryName(string categoryId)
    {
        string key = $"DesktopOrganization.Category.{categoryId}";
        string localized = LocalizationService.T(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? categoryId
            : localized;
    }

    private void ShowForkOneClickOrganizeNotification(ForkOneClickOrganizeResult result)
    {
        if (result.Outcome == ForkOneClickOrganizeOutcome.NothingToOrganize)
        {
            ShowForkOrganizeNotice(
                ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OneClickNothingTitle),
                ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OneClickNothingBody),
                historyId: null);
            return;
        }

        string separator = LocalizationService.CurrentCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "、" : ", ";
        string targets = string.Join(separator, (result.TargetNames ?? []).Take(4));
        string body = result.RetainedCount > 0
            ? ForkOrganizeModeText.Format(ForkOrganizeModeText.Key.OneClickPartialFormat, result.OrganizedCount, result.RetainedCount)
            : ForkOrganizeModeText.Format(ForkOrganizeModeText.Key.OneClickDoneFormat, result.OrganizedCount,
                targets.Length > 0 ? targets : ForkOrganizeModeText.ModeName(result.Mode));
        ShowForkOrganizeNotice(
            ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OneClickDoneTitle),
            body,
            result.UndoHistoryId);
    }

    private void ShowForkOrganizeNotice(string title, string message, string? historyId)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var arguments = new Dictionary<string, string> { ["type"] = "desktop-organization" };
        var actions = new List<NativeAppNotificationAction>();
        if (!string.IsNullOrWhiteSpace(historyId))
        {
            arguments["historyId"] = historyId;
            actions.Add(new NativeAppNotificationAction(
                LocalizationService.T("DesktopOrganization.Notification.Undo"),
                new Dictionary<string, string>
                {
                    ["type"] = "desktop-organization",
                    ["action"] = "undo",
                    ["historyId"] = historyId
                }));
        }

        if (_nativeNotificationService?.TryShow(
                title,
                message,
                arguments,
                actions.Count > 0 ? actions : null,
                options: new NativeAppNotificationOptions(
                    Tag: "fork-one-click-organize",
                    Group: DesktopOrganizationNotificationGroup)) == true)
        {
            return;
        }

        try
        {
            _trayIcon?.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Log($"[OneClickOrganize] Tray notification failed: {ex.Message}");
        }
    }

    /// <summary>Tray: 「整理预览…」 right below 「整理桌面…」 (which is one click now).</summary>
    private MenuFlyoutItem CreateForkOrganizePreviewTrayItem(MenuFlyout contextMenu)
    {
        var item = new MenuFlyoutItem
        {
            Text = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.MenuOrganizePreview),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "\uE8A1" }
        };
        AttachTrayMenuItemInteraction(
            item,
            () => RunTraySettingsActionAsync(contextMenu, () => OpenDesktopOrganizationPreview()));
        return item;
    }
}
