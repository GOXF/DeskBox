using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// GOXF fork, 「映射整理」 → 「重新整理桌面」: entries put back on the desktop
/// (「放回桌面」, see <see cref="MappedOrganizeKeepList"/>) are skipped by
/// automatic sorting of new files and by the mapped widget's quick
/// 「一键整理」, but the organize preview treats them like any other desktop
/// entry: sorted into their category by the normal rules and selected by
/// default; confirming maps them and clears the mark
/// (<see cref="MappedOrganizeService.ApplyGroupsAsync"/>), unticking keeps it.
/// Pure helpers, unit-tested.
/// </summary>
public static class MappedOrganizeReorganizePolicy
{
    /// <summary>
    /// Kept entries the preview must opt in on its own: those the scanner
    /// leaves out only for an opt-in reason (folder, large file, batch limit),
    /// which would otherwise start unticked. Each path is opted in once per
    /// preview (<paramref name="alreadyApplied"/> is updated), so a later
    /// refresh does not re-tick what the user unticked.
    /// </summary>
    public static IReadOnlyList<string> SelectKeptOptIns(
        IEnumerable<(string Path, bool CanOptIn)> scannedItems,
        IReadOnlyCollection<string> kept,
        ISet<string> alreadyApplied)
    {
        ArgumentNullException.ThrowIfNull(alreadyApplied);
        if (scannedItems is null || kept is null || kept.Count == 0)
        {
            return [];
        }

        var keptSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in kept)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                keptSet.Add(MappedOrganizeMembership.NormalizePath(path));
            }
        }

        var result = new List<string>();
        foreach ((string path, bool canOptIn) in scannedItems)
        {
            if (!canOptIn || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized = MappedOrganizeMembership.NormalizePath(path);
            if (keptSet.Contains(normalized) && alreadyApplied.Add(normalized))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a widget's menus offer 「重新整理桌面」: the widget is a mapped
    /// widget, or it is a member of a tab group that holds one.
    /// </summary>
    public static bool ShouldOfferReorganize(
        WidgetConfig? config,
        IEnumerable<WidgetConfig>? widgets,
        IEnumerable<WidgetGroupConfig>? groups)
    {
        if (config is null)
        {
            return false;
        }

        if (MappedOrganizeWidgetMetadata.IsMappedOrganize(config))
        {
            return true;
        }

        if (widgets is null || groups is null)
        {
            return false;
        }

        WidgetGroupConfig? group = groups.FirstOrDefault(candidate =>
            candidate?.MemberIds is { } members &&
            members.Contains(config.Id, StringComparer.Ordinal));
        if (group is null)
        {
            return false;
        }

        var memberIds = group.MemberIds.ToHashSet(StringComparer.Ordinal);
        return widgets.Any(widget =>
            widget is not null &&
            memberIds.Contains(widget.Id) &&
            MappedOrganizeWidgetMetadata.IsMappedOrganize(widget));
    }
}
