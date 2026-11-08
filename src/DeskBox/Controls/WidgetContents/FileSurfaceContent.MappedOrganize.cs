using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Fork feature 「映射整理」: blank-area menu entries of a mapped-organize widget,
/// and 「放回桌面」 for its items (the upstream 「移回桌面」 row, which in a mapped
/// widget would have nothing to move, unmaps instead).
/// </summary>
public sealed partial class FileSurfaceContent
{
    private string LocalizeFileItemMenu(string key) =>
        ViewModel.IsMappedOrganizeWidget && string.Equals(key, "Widget.MoveBackToDesktop", StringComparison.Ordinal)
            ? ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.MenuKeepOnDesktop)
            : T(key);

    /// <summary>Returns true when the items belonged to a mapped widget and were put back on the desktop.</summary>
    private bool TryKeepMappedItemsOnDesktop(IReadOnlyList<WidgetItem> items)
    {
        if (!ViewModel.IsMappedOrganizeWidget || MappedOrganizeService.Current is not { } service)
        {
            return false;
        }

        int kept = service.KeepOnDesktop(items
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path)));
        ShowFeedback(new WidgetFeedbackRequest(
            kept > 0
                ? ForkOrganizeModeText.Format(ForkOrganizeModeText.Key.KeptOnDesktopFormat, kept)
                : T("Widget.NoItemsMoved"),
            kept > 0 ? WidgetFeedbackSeverity.Success : WidgetFeedbackSeverity.Info,
            "mapped-keep-desktop"));
        return true;
    }

    /// <summary>
    /// 「一键整理」 (blank menu) and 「一键整理桌面」 (title 「…」 menu): organize the
    /// whole desktop in one click by the global 「整理方式」, 「放回桌面」 entries
    /// included (see ForkOneClickOrganizer).
    /// </summary>
    internal static void RunOneClickOrganize()
    {
        try
        {
            App.Current.OrganizeDesktopOneClick();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] One-click organize failed to start: {ex.Message}");
        }
    }

    /// <summary>「整理预览…」: the old organize preview window, kept as a secondary entry.</summary>
    internal static void OpenOrganizePreview() => App.Current.OpenDesktopOrganizationPreview();

    internal static MenuFlyoutItem CreateOrganizePreviewMenuItem(MenuFlyout flyout)
    {
        var preview = new MenuFlyoutItem
        {
            Text = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.MenuOrganizePreview),
            Icon = new FontIcon { Glyph = "\uE8A1" }
        };
        preview.Click += (_, _) =>
        {
            flyout.Hide();
            OpenOrganizePreview();
        };
        return preview;
    }

    private void AddMappedOrganizeMenuItems(MenuFlyout flyout)
    {
        if (!ViewModel.IsMappedOrganizeWidget || MappedOrganizeService.Current is not { } service)
        {
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem organizeNow = CreateMenuItem("MappedOrganize.Menu.OrganizeNow", "\uE8CB");
        organizeNow.Click += (_, _) =>
        {
            flyout.Hide();
            RunOneClickOrganize();
        };
        flyout.Items.Add(organizeNow);
        flyout.Items.Add(CreateOrganizePreviewMenuItem(flyout));

        var autoAssign = new ToggleMenuFlyoutItem
        {
            Text = T("MappedOrganize.Menu.AutoAssign"),
            IsChecked = service.AutoAssignNewItems
        };
        autoAssign.Click += (_, _) => service.SetAutoAssignNewItems(autoAssign.IsChecked);
        flyout.Items.Add(autoAssign);

        MenuFlyoutItem dissolveAll = CreateMenuItem("MappedOrganize.Menu.DissolveAll", "\uE7A7");
        dissolveAll.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(service.DissolveAllAsync);
        };
        flyout.Items.Add(dissolveAll);
    }
}
