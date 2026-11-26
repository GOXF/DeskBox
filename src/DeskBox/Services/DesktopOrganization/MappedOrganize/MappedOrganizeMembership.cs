using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure membership logic of 「映射整理」. Every desktop entry belongs to at most
/// one mapped-organize widget; membership lives in the widget's metadata.
/// No I/O happens here except where a caller passes a probe delegate, so the
/// rules are unit-testable (see MappedOrganizeMembershipTests).
/// </summary>
public static class MappedOrganizeMembership
{
    private const char Separator = '\n';

    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static IEnumerable<WidgetConfig> MappedWidgets(IEnumerable<WidgetConfig> widgets) =>
        widgets.Where(MappedOrganizeWidgetMetadata.IsMappedOrganize);

    public static IReadOnlyList<string> GetMembers(WidgetConfig widget)
    {
        if (widget.Metadata is null ||
            !widget.Metadata.TryGetValue(MappedOrganizeWidgetMetadata.MembersKey, out string? raw) ||
            string.IsNullOrEmpty(raw))
        {
            return [];
        }

        return raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Stores the members (normalized, de-duplicated, order kept). Returns true when the stored value changed.</summary>
    public static bool SetMembers(WidgetConfig widget, IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized = NormalizePath(path);
            if (seen.Add(normalized))
            {
                ordered.Add(normalized);
            }
        }

        string value = string.Join(Separator, ordered);
        widget.Metadata ??= [];
        if (widget.Metadata.TryGetValue(MappedOrganizeWidgetMetadata.MembersKey, out string? previous) &&
            string.Equals(previous, value, StringComparison.Ordinal))
        {
            return false;
        }

        widget.Metadata[MappedOrganizeWidgetMetadata.MembersKey] = value;
        return true;
    }

    public static bool IsMember(WidgetConfig widget, string path)
    {
        string normalized = NormalizePath(path);
        return GetMembers(widget).Any(member => string.Equals(member, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static WidgetConfig? FindOwner(IEnumerable<WidgetConfig> widgets, string path)
    {
        string normalized = NormalizePath(path);
        return MappedWidgets(widgets).FirstOrDefault(widget =>
            GetMembers(widget).Any(member => string.Equals(member, normalized, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Moves the given paths into the target widget's membership; membership is
    /// exclusive, so the paths leave every other mapped widget. Never touches
    /// the files. Returns the ids of every widget whose membership changed.
    /// </summary>
    public static IReadOnlyList<string> Assign(
        IEnumerable<WidgetConfig> widgets,
        string targetWidgetId,
        IEnumerable<string> paths)
    {
        List<WidgetConfig> mapped = MappedWidgets(widgets).ToList();
        WidgetConfig? target = mapped.FirstOrDefault(widget =>
            string.Equals(widget.Id, targetWidgetId, StringComparison.Ordinal));
        if (target is null)
        {
            return [];
        }

        var incoming = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (incoming.Count == 0)
        {
            return [];
        }

        var incomingSet = incoming.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (WidgetConfig widget in mapped)
        {
            if (ReferenceEquals(widget, target))
            {
                continue;
            }

            IReadOnlyList<string> members = GetMembers(widget);
            if (members.Any(incomingSet.Contains) &&
                SetMembers(widget, members.Where(member => !incomingSet.Contains(member))))
            {
                changed.Add(widget.Id);
            }
        }

        if (SetMembers(target, GetMembers(target).Concat(incoming)))
        {
            changed.Add(target.Id);
        }

        return changed;
    }

    /// <summary>Removes a path (and, for a deleted folder, nothing below it is tracked) from every widget.</summary>
    public static IReadOnlyList<string> RemovePath(IEnumerable<WidgetConfig> widgets, string path)
    {
        string normalized = NormalizePath(path);
        var changed = new List<string>();
        foreach (WidgetConfig widget in MappedWidgets(widgets))
        {
            IReadOnlyList<string> members = GetMembers(widget);
            if (members.Any(member => string.Equals(member, normalized, StringComparison.OrdinalIgnoreCase)) &&
                SetMembers(widget, members.Where(member => !string.Equals(member, normalized, StringComparison.OrdinalIgnoreCase))))
            {
                changed.Add(widget.Id);
            }
        }

        return changed;
    }

    /// <summary>A rename outside or inside DeskBox keeps the entry in the same widget, at the same position.</summary>
    public static IReadOnlyList<string> RenamePath(IEnumerable<WidgetConfig> widgets, string oldPath, string newPath)
    {
        string oldNormalized = NormalizePath(oldPath);
        string newNormalized = NormalizePath(newPath);
        var changed = new List<string>();
        foreach (WidgetConfig widget in MappedWidgets(widgets))
        {
            IReadOnlyList<string> members = GetMembers(widget);
            if (!members.Any(member => string.Equals(member, oldNormalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (SetMembers(widget, members.Select(member =>
                    string.Equals(member, oldNormalized, StringComparison.OrdinalIgnoreCase) ? newNormalized : member)))
            {
                changed.Add(widget.Id);
            }
        }

        return changed;
    }

    /// <summary>Drops members whose entry no longer exists (offline deletes, crash, Explorer-side moves).</summary>
    public static IReadOnlyList<string> Prune(IEnumerable<WidgetConfig> widgets, Func<string, bool> exists)
    {
        var changed = new List<string>();
        foreach (WidgetConfig widget in MappedWidgets(widgets))
        {
            IReadOnlyList<string> members = GetMembers(widget);
            var kept = members.Where(exists).ToList();
            if (kept.Count != members.Count && SetMembers(widget, kept))
            {
                changed.Add(widget.Id);
            }
        }

        return changed;
    }

    public static IReadOnlyList<string> FindUnassigned(
        IEnumerable<WidgetConfig> widgets,
        IEnumerable<string> desktopEntries)
    {
        var assigned = MappedWidgets(widgets)
            .SelectMany(GetMembers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return desktopEntries
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !assigned.Contains(path))
            .ToList();
    }

    /// <summary>
    /// The mapped widget a classified entry routes to: the first widget
    /// collecting the same category, otherwise the 「其他」 widget. Null means
    /// the caller has to create an 「其他」 widget first.
    /// </summary>
    public static WidgetConfig? ResolveTarget(IEnumerable<WidgetConfig> widgets, string categoryId)
    {
        List<WidgetConfig> mapped = MappedWidgets(widgets).Where(widget => !widget.IsDisabled).ToList();
        return mapped.FirstOrDefault(widget => string.Equals(
                   MappedOrganizeWidgetMetadata.GetCategory(widget), categoryId, StringComparison.Ordinal)) ??
               mapped.FirstOrDefault(widget => string.Equals(
                   MappedOrganizeWidgetMetadata.GetCategory(widget),
                   DesktopOrganizationCategoryIds.Other,
                   StringComparison.Ordinal));
    }

    /// <summary>
    /// Category of a desktop entry for mapped routing. Folders have no
    /// extension-based category and land in 「其他」.
    /// </summary>
    public static string Classify(DesktopOrganizationClassifier classifier, string path, bool isDirectory) =>
        isDirectory ? DesktopOrganizationCategoryIds.Other : classifier.Classify(path).CategoryId;

    /// <summary>
    /// Groups unassigned entries by their routed widget. Entries whose route
    /// needs a missing 「其他」 widget are returned under <c>null</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> PlanAssignments(
        IEnumerable<WidgetConfig> widgets,
        IEnumerable<(string Path, string CategoryId)> entries,
        out List<string> needsOtherWidget)
    {
        List<WidgetConfig> snapshot = widgets.ToList();
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        needsOtherWidget = [];
        foreach ((string path, string categoryId) in entries)
        {
            WidgetConfig? target = ResolveTarget(snapshot, categoryId);
            if (target is null)
            {
                needsOtherWidget.Add(path);
                continue;
            }

            if (!result.TryGetValue(target.Id, out List<string>? list))
            {
                list = [];
                result.Add(target.Id, list);
            }

            list.Add(path);
        }

        return result;
    }
}
