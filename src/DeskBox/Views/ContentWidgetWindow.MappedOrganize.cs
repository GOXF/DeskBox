using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// GOXF fork, 「映射整理」: the title-bar 「…」 menu of a mapped widget — or of a
/// tab group holding one — offers 「一键整理桌面」 (one click, 「放回桌面」 items
/// included, see ForkOneClickOrganizer) and 「整理预览…」 (the old preview).
/// Visibility: <see cref="MappedOrganizeReorganizePolicy.ShouldOfferReorganize"/>.
/// </summary>
public sealed partial class ContentWidgetWindow
{
    private void AppendMappedOrganizeMoreMenuItems(MenuFlyout flyout)
    {
        try
        {
            if (MappedOrganizeService.Current is null ||
                !MappedOrganizeReorganizePolicy.ShouldOfferReorganize(
                    _config,
                    App.Current.SettingsService.Settings.WidgetLayout.Widgets,
                    App.Current.SettingsService.Settings.WidgetLayout.WidgetGroups))
            {
                return;
            }

            var reorganize = new MenuFlyoutItem
            {
                Text = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.MenuReorganizeDesktop),
                Icon = new FontIcon { Glyph = "\uE8B1" }
            };
            reorganize.Click += (_, _) =>
            {
                flyout.Hide();
                FileSurfaceContent.RunOneClickOrganize();
            };
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(reorganize);
            flyout.Items.Add(FileSurfaceContent.CreateOrganizePreviewMenuItem(flyout));
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Title menu entry failed: {ex.Message}");
        }
    }
}
