using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// Fork feature 「映射整理」: the preview's 「只映射，不移动文件」 option. When
/// checked, confirming the preview creates mapped-organize widgets that only
/// record membership; no file leaves the Desktop folders.
/// </summary>
public sealed partial class DesktopOrganizationTaskView
{
    private bool _mapOnlyInitialized;

    private void MapOnlyCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (_mapOnlyInitialized)
        {
            return;
        }

        _mapOnlyInitialized = true;
        MapOnlyCheckBox.Content = T("MappedOrganize.Preview.MapOnly");
        ToolTipService.SetToolTip(MapOnlyCheckBox, T("MappedOrganize.Preview.MapOnlyDescription"));
        OrganizationCompleted += (_, _) => MapOnlyCheckBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>Returns true when the mapped path handled the confirm click.</summary>
    private async Task<bool> TryRunMappedOrganizeAsync(DesktopOrganizationPlan previewPlan)
    {
        if (MapOnlyCheckBox.IsChecked != true)
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
        MapOnlyCheckBox.IsEnabled = false;
        try
        {
            int count = await service.ApplyGroupsAsync(groups);
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
            MapOnlyCheckBox.IsEnabled = true;
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
