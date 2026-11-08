using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Fork feature 「映射整理」: a mapped-organize widget maps the Desktop but only
/// lists the desktop entries recorded as its members. Everything else (open,
/// rename, delete, shell menus, hydration) is the ordinary folder pipeline
/// acting on the real files. See docs/architecture/mapped-organize.md.
/// </summary>
public partial class WidgetViewModel
{
    private bool _mappedOrganizeSubscribed;

    public bool IsMappedOrganizeWidget => MappedOrganizeWidgetMetadata.IsMappedOrganize(Config);

    private void AttachMappedOrganize()
    {
        if (_mappedOrganizeSubscribed)
        {
            return;
        }

        MappedOrganizeService.MembershipChanged += OnMappedOrganizeMembershipChanged;
        _mappedOrganizeSubscribed = true;
    }

    private void DetachMappedOrganize()
    {
        if (!_mappedOrganizeSubscribed)
        {
            return;
        }

        MappedOrganizeService.MembershipChanged -= OnMappedOrganizeMembershipChanged;
        _mappedOrganizeSubscribed = false;
    }

    private void OnMappedOrganizeMembershipChanged(IReadOnlyCollection<string> widgetIds)
    {
        if (_isDisposed || !IsMappedOrganizeWidget || !widgetIds.Contains(Config.Id))
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!_isDisposed)
                {
                    await RefreshFolderContentsAsync();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[MappedOrganize] Refresh after membership change failed widget={Config.Id}: {ex.Message}");
            }
        });
    }

    /// <summary>Desktop snapshot → only this widget's members (no-op for ordinary widgets).</summary>
    private IReadOnlyList<WidgetItem> ApplyMappedOrganizeFilter(IReadOnlyList<WidgetItem> items)
    {
        if (!IsMappedOrganizeWidget)
        {
            return items;
        }

        var members = MappedOrganizeMembership.GetMembers(Config).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) &&
                           members.Contains(MappedOrganizeMembership.NormalizePath(item.Path)))
            .ToList();
    }

    /// <summary>Incremental watcher upserts: a desktop entry shows only when it is a member.</summary>
    private bool IsHiddenByMappedOrganize(string path)
    {
        return IsMappedOrganizeWidget &&
               MappedOrganizeService.IsDesktopEntryPath(path) &&
               !MappedOrganizeMembership.IsMember(Config, path);
    }

    /// <summary>
    /// Drops onto a mapped widget: desktop entries (including drags from
    /// another mapped widget) only change membership; anything else follows
    /// the normal import into the Desktop folder and is then claimed by this
    /// widget when it appears there. Returns the paths still to import.
    /// </summary>
    private List<string> ClaimMappedOrganizeDrop(List<string> paths, out List<string> claimed)
    {
        claimed = [];
        if (!IsMappedOrganizeWidget || MappedOrganizeService.Current is not { } service)
        {
            return paths;
        }

        claimed = service.AssignDesktopPaths(Config.Id, paths).ToList();
        var claimedSet = claimed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> remaining = paths.Where(path => !claimedSet.Contains(path)).ToList();
        if (remaining.Count > 0)
        {
            service.ExpectIncoming(Config.Id, remaining);
        }

        return remaining;
    }
}
