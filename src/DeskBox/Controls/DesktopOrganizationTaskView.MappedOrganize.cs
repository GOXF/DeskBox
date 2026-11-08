using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// Fork feature 「映射整理」: the preview follows the global 「整理方式」 setting
/// (设置 → 桌面整理). The footer shows the current mode read-only with a link
/// to the setting; in 「只映射」 mode confirming the preview creates
/// mapped-organize widgets that only record membership and no file leaves the
/// Desktop folders.
/// </summary>
public sealed partial class DesktopOrganizationTaskView
{
    private bool _organizeModeInitialized;

    private void OrganizeModeLink_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshOrganizeModeLink();
        if (_organizeModeInitialized)
        {
            return;
        }

        _organizeModeInitialized = true;
        OrganizationCompleted += (_, _) => OrganizeModeLink.Visibility = Visibility.Collapsed;
        ForkOrganizeModeSettings.Changed += _ => DispatcherQueue?.TryEnqueue(RefreshOrganizeModeLink);
    }

    private void OrganizeModeLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Current.ShowSettings("DesktopOrganizationSettings");
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Opening the organize-mode setting failed: {ex.Message}");
        }
    }

    private void RefreshOrganizeModeLink()
    {
        DesktopOrganizeMode mode = ForkOrganizeModeSettings.Load();
        OrganizeModeLink.Content = ForkOrganizeModeText.Format(
                ForkOrganizeModeText.Key.PreviewModeFormat,
                ForkOrganizeModeText.ModeName(mode)) +
            ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.PreviewChange);
        ToolTipService.SetToolTip(OrganizeModeLink, ForkOrganizeModeText.Get(mode == DesktopOrganizeMode.MapOnly
            ? ForkOrganizeModeText.Key.PreviewTooltipMapOnly
            : ForkOrganizeModeText.Key.PreviewTooltipMove));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            OrganizeModeLink,
            ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SettingTitle));
    }

    /// <summary>Returns true when the mapped path handled the confirm click.</summary>
    private async Task<bool> TryRunMappedOrganizeAsync(DesktopOrganizationPlan previewPlan)
    {
        DesktopOrganizeMode mode = ForkOrganizeModeSettings.Load();
        RefreshOrganizeModeLink();
        if (!ForkOrganizeModePolicy.ConfirmMapsInsteadOfMoving(mode))
        {
            return false;
        }

        if (MappedOrganizeService.Current is not { } service)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
            return true;
        }

        // Entries the user deselected in the preview stay on the desktop
        // (「放回桌面」) instead of being swept up by automatic sorting.
        List<string> keepOnDesktop = previewPlan.Targets
            .SelectMany(target =>
                _targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) &&
                !selection.IsSelected
                    ? target.Items.Select(item => item.SourcePath)
                    : target.Items.Where(item => _excludedSourcePaths.Contains(item.SourcePath)).Select(item => item.SourcePath))
            .ToList();
        List<MappedOrganizeGroup> groups = previewPlan.Targets
            .Where(target => !_targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) ||
                             selection.IsSelected)
            .GroupBy(target => target.CategoryId, StringComparer.Ordinal)
            .Select(group => new MappedOrganizeGroup(
                group.Key,
                ResolveMappedCategoryName(group.Key),
                group.First().PlannedBounds,
                group.SelectMany(target => target.Items)
                    .Where(item => !_excludedSourcePaths.Contains(item.SourcePath))
                    .Select(item => item.SourcePath)
                    .ToList()))
            .Where(group => group.Paths.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            ResultInfo.Severity = InfoBarSeverity.Warning;
            ResultInfo.Title = T("DesktopOrganization.Preview.NothingSelectedTitle");
            ResultInfo.Message = T("DesktopOrganization.Preview.NothingSelectedBody");
            ResultInfo.IsOpen = true;
            return true;
        }

        _isExecuting = true;
        ExecuteButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        OrganizeModeLink.IsEnabled = false;
        try
        {
            int count = await service.ApplyGroupsAsync(groups, keepOnDesktop);
            _hasCompletedExecution = true;
            ResultInfo.Severity = InfoBarSeverity.Success;
            ResultInfo.Title = T("DesktopOrganization.Result.SuccessTitle");
            ResultInfo.Message = Format("MappedOrganize.Result.Success", count, groups.Count);
            ResultInfo.IsOpen = true;
            ExecuteButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;
            DoneButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            SourceSelectionHost.IsEnabled = false;
            TargetSelectionHost.IsEnabled = false;
            OrganizationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Mapped organize failed: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
            ExecuteButton.IsEnabled = true;
        }
        finally
        {
            _isExecuting = false;
            CancelButton.IsEnabled = true;
            OrganizeModeLink.IsEnabled = true;
            DoneButton.IsEnabled = true;
        }

        return true;
    }

    private static string ResolveMappedCategoryName(string categoryId)
    {
        string key = $"DesktopOrganization.Category.{categoryId}";
        string localized = T(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? categoryId : localized;
    }
}
