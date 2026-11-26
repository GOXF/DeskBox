using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>Fork-specific mapped organize behavior integrated with the workbench session.</summary>
public sealed partial class DesktopOrganizationTaskView
{
    private bool _organizeModeInitialized;
    private readonly HashSet<string> _keptOnDesktopOptInsApplied = new(StringComparer.OrdinalIgnoreCase);

    private void IncludeKeptOnDesktopItems(DesktopOrganizationPlan scanned)
    {
        try
        {
            if (MappedOrganizeService.Current is not { } service ||
                !ForkOrganizeModePolicy.ConfirmMapsInsteadOfMoving(ForkOrganizeModeSettings.Load()))
            {
                return;
            }

            IReadOnlyList<string> optIns = MappedOrganizeReorganizePolicy.SelectKeptOptIns(
                scanned.SourceItems.Select(item => (item.SourcePath, item.CanOptIn)),
                service.KeptOnDesktopSnapshot(),
                _keptOnDesktopOptInsApplied);
            if (optIns.Count > 0)
            {
                Session.IncludeItems(optIns);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Preview could not include kept-on-desktop entries: {ex.Message}");
        }
    }

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
        AutomationProperties.SetName(
            OrganizeModeLink,
            ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SettingTitle));
    }

    /// <summary>Returns true when map-only mode handled the confirm click.</summary>
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

        MappedOrganizePreviewProjection projection = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            previewPlan,
            Session.BuildTargetSelectionsForExecution(),
            Session.ExcludedSourcePaths,
            service.KeptOnDesktopSnapshot().ToHashSet(StringComparer.OrdinalIgnoreCase),
            ResolveMappedCategoryName);
        if (projection.Groups.Count == 0)
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
            int count = await service.ApplyGroupsAsync(
                projection.Groups,
                projection.KeepOnDesktop,
                projection.SkipAutoAssignPaths);
            _hasCompletedExecution = true;
            ResultInfo.Severity = InfoBarSeverity.Success;
            ResultInfo.Title = T("DesktopOrganization.Result.SuccessTitle");
            ResultInfo.Message = Format("MappedOrganize.Result.Success", count, projection.Groups.Count);
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
            DeferredDesktopOrganizationClosePolicy.CompleteIfRequested(
                ref _closeAfterExecutionStops,
                () => CloseRequested?.Invoke(this, EventArgs.Empty));
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
