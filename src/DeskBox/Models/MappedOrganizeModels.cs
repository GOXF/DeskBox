namespace DeskBox.Models;

/// <summary>
/// Fork feature 「映射整理」 (virtual desktop organize). A mapped-organize widget
/// is an ordinary File widget whose <see cref="WidgetConfig.MappedFolderPath"/>
/// is the user Desktop (the existing desktop branch of the folder loader
/// already merges the Public Desktop) and that only shows the desktop entries
/// recorded as its members. Files never leave the Desktop folders; only
/// membership is stored, inside <see cref="WidgetConfig.Metadata"/> so it is
/// persisted, backed up and removed together with the widget without any new
/// serializer surface. See docs/architecture/mapped-organize.md.
/// </summary>
public static class MappedOrganizeWidgetMetadata
{
    /// <summary>Metadata flag marking a File widget as a mapped-organize widget.</summary>
    public const string FlagKey = "DeskBox.MappedOrganize";

    /// <summary>Metadata value carrying the classifier category the widget collects.</summary>
    public const string CategoryKey = "DeskBox.MappedOrganize.Category";

    /// <summary>Newline-separated absolute member paths (a path can never contain a newline).</summary>
    public const string MembersKey = "DeskBox.MappedOrganize.Members";

    public const string FlagValue = "1";

    public static bool IsMappedOrganize(WidgetConfig? widget) =>
        widget is not null &&
        widget.WidgetKind == WidgetKind.File &&
        widget.Metadata is not null &&
        widget.Metadata.TryGetValue(FlagKey, out string? value) &&
        string.Equals(value, FlagValue, StringComparison.Ordinal);

    public static string GetCategory(WidgetConfig widget) =>
        widget.Metadata is not null &&
        widget.Metadata.TryGetValue(CategoryKey, out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : DesktopOrganizationCategoryIds.Other;

    public static void Mark(WidgetConfig widget, string categoryId)
    {
        widget.Metadata ??= [];
        widget.Metadata[FlagKey] = FlagValue;
        widget.Metadata[CategoryKey] = string.IsNullOrWhiteSpace(categoryId)
            ? DesktopOrganizationCategoryIds.Other
            : categoryId;
        if (!widget.Metadata.ContainsKey(MembersKey))
        {
            widget.Metadata[MembersKey] = string.Empty;
        }
    }
}

/// <summary>One group of the mapped-organize preview: a category and the desktop paths it collects.</summary>
public sealed record MappedOrganizeGroup(
    string CategoryId,
    string DisplayName,
    DesktopOrganizationRect? PlannedBounds,
    IReadOnlyList<string> Paths);
