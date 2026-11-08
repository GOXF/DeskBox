using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// GOXF fork, 「映射整理」: the title-bar 「…」 menu of a mapped widget — or of a
/// tab group holding one — offers 「重新整理桌面」, which opens the organize
/// preview where 「放回桌面」 items are sorted by the rules again
/// (<see cref="MappedOrganizeReorganizePolicy"/>).
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
                FileSurfaceContent.OpenMappedReorganizePreview();
            };
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(reorganize);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Title menu entry failed: {ex.Message}");
        }
    }
}
