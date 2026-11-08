namespace DeskBox.Services;

/// <summary>Where a drag released from a mapped widget landed, as far as 「放回桌面」 cares.</summary>
public enum MappedDragDropTargetKind
{
    /// <summary>Anything that is not the shell desktop (another app, a folder window, the taskbar, DeskBox itself).</summary>
    NotDesktop,

    /// <summary>The desktop icon list (SHELLDLL_DefView / SysListView32 inside Progman or WorkerW).</summary>
    DesktopIconList,

    /// <summary>The desktop host window itself (Progman / WorkerW, e.g. wallpaper area without a list view).</summary>
    DesktopHost,
}

/// <summary>
/// GOXF fork, 「映射整理」: pure decisions for 「从映射格子拖到桌面 = 放回桌面」.
/// A mapped item already lives in the desktop folder, so Explorer treats a drop
/// on the desktop as a same-folder move (a no-op) and the item would stay hidden
/// and stay in the widget. DeskBox therefore recognises that drop itself and
/// runs 「放回桌面」. The native probing lives in <see cref="MappedDragToDesktopTarget"/>;
/// everything here is testable without a desktop.
/// </summary>
public static class MappedDragToDesktopPolicy
{
    private const int DefaultSpacingX = 75;
    private const int DefaultSpacingY = 100;

    private static readonly string[] TaskbarClasses =
    [
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
    ];

    /// <summary>
    /// Classifies the window under the release point from its class chain
    /// (the hit window first, then each parent up to the root). A window owned
    /// by DeskBox itself is never the desktop, even when a widget is bedded
    /// into WorkerW; a window outside the shell process is rejected too.
    /// </summary>
    public static MappedDragDropTargetKind ClassifyTarget(
        IReadOnlyList<string> classChain,
        bool ownedByDeskBox,
        bool ownedByShell)
    {
        if (classChain is null || classChain.Count == 0 || ownedByDeskBox || !ownedByShell)
        {
            return MappedDragDropTargetKind.NotDesktop;
        }

        bool hasDefView = false;
        bool hasHost = false;
        foreach (string className in classChain)
        {
            if (string.IsNullOrEmpty(className))
            {
                continue;
            }

            if (TaskbarClasses.Any(taskbar => string.Equals(taskbar, className, StringComparison.OrdinalIgnoreCase)))
            {
                return MappedDragDropTargetKind.NotDesktop;
            }

            hasDefView |= string.Equals(className, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);
            hasHost |= string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase);
        }

        if (hasDefView)
        {
            // SHELLDLL_DefView also hosts the file list of every Explorer
            // folder window; only the one under Progman/WorkerW is the desktop.
            return hasHost ? MappedDragDropTargetKind.DesktopIconList : MappedDragDropTargetKind.NotDesktop;
        }

        return hasHost ? MappedDragDropTargetKind.DesktopHost : MappedDragDropTargetKind.NotDesktop;
    }

    /// <summary>
    /// Whether a completed drag should become 「放回桌面」: it came from a mapped
    /// widget, DeskBox did not handle the drop itself, it landed on the desktop,
    /// and not on an existing icon (a drop on a folder / the Recycle Bin icon is
    /// a real move, which the upstream drag-out handling keeps doing). A drag
    /// cancelled with Esc, or a completion reported while a mouse button is
    /// still held (the stack popover closing mid-drag), is not a drop.
    /// </summary>
    public static bool ShouldPutBack(
        bool isMappedWidget,
        bool handledInternally,
        MappedDragDropTargetKind target,
        bool droppedOnIcon,
        bool mouseButtonDown = false,
        bool escapePressed = false) =>
        isMappedWidget &&
        !handledInternally &&
        target != MappedDragDropTargetKind.NotDesktop &&
        !droppedOnIcon &&
        !mouseButtonDown &&
        !escapePressed;

    /// <summary>
    /// The entries to put back: dragged paths that still exist and sit directly
    /// in a desktop folder, normalised and de-duplicated (multi-selection drags
    /// carry every selected path; a stack drag carries none).
    /// </summary>
    public static IReadOnlyList<string> ComputePutBackPaths(
        IEnumerable<string>? draggedPaths,
        Func<string, bool> exists,
        Func<string, bool> isDesktopEntry)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(isDesktopEntry);
        if (draggedPaths is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string raw in draggedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path;
            try
            {
                path = MappedOrganizeMembership.NormalizePath(raw);
            }
            catch (Exception)
            {
                continue;
            }

            if (path.Length == 0 || !isDesktopEntry(path) || !exists(path) || !seen.Add(path))
            {
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// Top-left positions (desktop list-view client coordinates) for the icons
    /// put back by one drop: the first is centred on the release point, the
    /// rest follow below it in a column and wrap into the next column at the
    /// bottom of the work area — the same column-first order as the desktop.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> ComputeIconOrigins(
        int dropX,
        int dropY,
        int spacingX,
        int spacingY,
        int count,
        int areaTop,
        int areaBottom)
    {
        if (count <= 0)
        {
            return [];
        }

        int sx = spacingX > 0 ? spacingX : DefaultSpacingX;
        int sy = spacingY > 0 ? spacingY : DefaultSpacingY;
        int x0 = dropX - (sx / 2);
        int y0 = dropY - (sy / 2);
        bool hasArea = areaBottom - areaTop >= sy;
        if (hasArea)
        {
            y0 = Math.Clamp(y0, areaTop, areaBottom - sy);
        }

        int rows = hasArea ? Math.Max(1, (areaBottom - y0) / sy) : count;
        var origins = new List<(int X, int Y)>(count);
        for (int i = 0; i < count; i++)
        {
            int column = i / rows;
            int row = i % rows;
            origins.Add((x0 + (column * sx), y0 + (row * sy)));
        }

        return origins;
    }
}
