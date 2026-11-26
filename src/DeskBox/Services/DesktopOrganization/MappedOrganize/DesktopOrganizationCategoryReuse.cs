using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// GOXF fork: before the planner proposes a NEW widget for a category bucket,
/// it looks for an existing widget that already stands for that category, so
/// one category never ends up in two same-named widgets (e.g. two 「其他」).
///
/// Why it happened: a widget created by an earlier organize gets a rule for
/// the categories it received (say [Other]). Later, a category with a single
/// item (say one video, Media) is merged into the 「其他」 bucket by
/// <see cref="DesktopOrganizationPlanner"/>; the Media item matches no rule,
/// so the planner proposed a second, new 「其他」 widget next to the existing
/// one that holds the folders.
///
/// A bucket reuses, in order: the widget of an enabled rule that lists the
/// bucket's category (unless the rule excludes one of the bucket's
/// extensions), then a widget whose name equals the bucket's display name.
/// Only widgets the rule resolver could target qualify (file widgets with a
/// folder, not disabled, not mapped-organize). Pure; unit-tested.
/// </summary>
public static class DesktopOrganizationCategoryReuse
{
    public static WidgetConfig? FindExistingWidget(
        string categoryId,
        string displayName,
        IEnumerable<string> bucketExtensions,
        IReadOnlyCollection<WidgetConfig> widgets,
        IReadOnlyCollection<DesktopOrganizationRule> rules)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || widgets is null || widgets.Count == 0)
        {
            return null;
        }

        var eligible = widgets
            .Where(IsEligibleTarget)
            .ToDictionary(widget => widget.Id, StringComparer.Ordinal);
        if (eligible.Count == 0)
        {
            return null;
        }

        var extensions = (bucketExtensions ?? [])
            .Select(DesktopOrganizationClassifier.NormalizeExtension)
            .Where(extension => !string.IsNullOrEmpty(extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        WidgetConfig? byRule = (rules ?? [])
            .Where(rule =>
                rule is not null &&
                rule.IsEnabled &&
                eligible.ContainsKey(rule.TargetWidgetId ?? string.Empty) &&
                rule.CategoryIds.Contains(categoryId, StringComparer.Ordinal) &&
                !rule.ExcludedExtensions
                    .Select(DesktopOrganizationClassifier.NormalizeExtension)
                    .Any(extensions.Contains))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(rule => eligible[rule.TargetWidgetId])
            .FirstOrDefault();
        if (byRule is not null)
        {
            return byRule;
        }

        string name = displayName?.Trim() ?? string.Empty;
        return name.Length == 0
            ? null
            : eligible.Values.FirstOrDefault(widget =>
                string.Equals(widget.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEligibleTarget(WidgetConfig? widget) =>
        widget is not null &&
        widget.WidgetKind == WidgetKind.File &&
        !widget.IsDisabled &&
        !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
        !MappedOrganizeWidgetMetadata.IsMappedOrganize(widget);
}
