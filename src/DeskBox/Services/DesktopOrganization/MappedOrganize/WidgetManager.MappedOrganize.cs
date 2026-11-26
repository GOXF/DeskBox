using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Fork feature 「映射整理」: widget-manager entry points used by
/// <see cref="MappedOrganizeService"/>. Mapped widgets intentionally share the
/// Desktop as their folder, so they are added like organizer-created widgets
/// (no folder-conflict check, no mapped-folder desktop shortcut).
/// </summary>
public sealed partial class WidgetManager
{
    internal void AddMappedOrganizeConfig(WidgetConfig config, DesktopOrganizationRect? plannedBounds)
    {
        RectInt32? explicitBounds = plannedBounds is { } bounds
            ? new RectInt32(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                Math.Max(1, (int)Math.Round(bounds.Width)),
                Math.Max(1, (int)Math.Round(bounds.Height)))
            : null;
        ApplyNewWidgetPlacement(config, new NewWidgetPlacementContext(explicitBounds));
        _settingsService.Settings.WidgetLayout.Widgets.Add(config);
    }

    internal Task<bool> ShowMappedOrganizeWidgetAsync(WidgetConfig config) =>
        ShowWidgetAsync(config.Id, reveal: true, autoRestoreOnReveal: false);

    internal bool IsWidgetPendingDeletion(string widgetId) => IsDeleted(widgetId);
}
