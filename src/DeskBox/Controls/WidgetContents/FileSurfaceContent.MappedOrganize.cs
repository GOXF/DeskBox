using DeskBox.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>Fork feature 「映射整理」: blank-area menu entries of a mapped-organize widget.</summary>
public sealed partial class FileSurfaceContent
{
    private void AddMappedOrganizeMenuItems(MenuFlyout flyout)
    {
        if (!ViewModel.IsMappedOrganizeWidget || MappedOrganizeService.Current is not { } service)
        {
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem organizeNow = CreateMenuItem("MappedOrganize.Menu.OrganizeNow", "\uE8CB");
        organizeNow.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(() => service.OrganizeNowAsync());
        };
        flyout.Items.Add(organizeNow);

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
