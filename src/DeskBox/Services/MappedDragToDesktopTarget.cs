using System.Text;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>Where a mapped-widget drag was released, resolved against the live desktop windows.</summary>
internal readonly record struct MappedDesktopDropPoint(
    MappedDragDropTargetKind Kind,
    MappedDragIconHit IconHit,
    string? IconText,
    IntPtr ListView,
    int ClientX,
    int ClientY,
    int AreaTopClient,
    int AreaBottomClient,
    string Chain = "",
    string? HitFailure = null)
{
    internal static MappedDesktopDropPoint None { get; } =
        new(MappedDragDropTargetKind.NotDesktop, MappedDragIconHit.Blank, null, IntPtr.Zero, 0, 0, 0, 0);

    /// <summary>One-line diagnostics for <c>[MappedOrganize] DragToDesktop …</c> log lines.</summary>
    internal string Describe() =>
        $"target={Kind} icon={IconHit}" +
        (IconText is null ? string.Empty : $" iconText='{IconText}'") +
        (HitFailure is null ? string.Empty : $" hitFailure={HitFailure}") +
        $" chain='{Chain}'";
}

/// <summary>
/// GOXF fork, 「映射整理」: native probe behind 「从映射格子拖到桌面 = 放回桌面」.
/// Reads the window under the release point (every monitor: the desktop list
/// view spans the virtual screen, and Windows 11 may host it under WorkerW),
/// classifies it with <see cref="MappedDragToDesktopPolicy.ClassifyTarget"/>,
/// and tells blank desktop, an icon (with its label) and a failed hit test
/// apart with <see cref="DesktopListViewProbeNativeMethods"/>. The shell drag
/// image (SysDragImage) can still sit under the cursor at the instant of
/// release; the probe waits briefly for it to go away.
/// </summary>
internal static class MappedDragToDesktopTarget
{
    private const int MaxAncestorDepth = 16;
    private const int DragImageRetries = 4;

    private static readonly string[] OverlayClasses = ["SysDragImage", "DragImageWindow"];

    internal static MappedDesktopDropPoint Probe(Win32Helper.POINT screenPoint)
    {
        try
        {
            return ProbeCore(screenPoint);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Desktop drop probe failed: {ex.Message}");
            return MappedDesktopDropPoint.None;
        }
    }

    private static MappedDesktopDropPoint ProbeCore(Win32Helper.POINT screenPoint)
    {
        IntPtr hit = Win32Helper.WindowFromPoint(screenPoint);
        for (int retry = 0; hit != IntPtr.Zero && retry < DragImageRetries && IsOverlay(hit); retry++)
        {
            Thread.Sleep(25);
            hit = Win32Helper.WindowFromPoint(screenPoint);
        }

        if (hit == IntPtr.Zero)
        {
            return MappedDesktopDropPoint.None with { Chain = "<no window>" };
        }

        var chain = new List<string>();
        IntPtr listView = IntPtr.Zero;
        IntPtr defView = IntPtr.Zero;
        IntPtr current = hit;
        for (int depth = 0; current != IntPtr.Zero && depth < MaxAncestorDepth; depth++)
        {
            string className = ClassOf(current);
            chain.Add(className);
            if (listView == IntPtr.Zero && string.Equals(className, "SysListView32", StringComparison.Ordinal))
            {
                listView = current;
            }

            if (defView == IntPtr.Zero && string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal))
            {
                defView = current;
            }

            current = Win32Helper.GetParent(current);
        }

        IntPtr root = Win32Helper.GetAncestor(hit, Win32Helper.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            chain.Add(ClassOf(root));
        }

        uint currentProcess = (uint)Environment.ProcessId;
        Win32Helper.GetWindowThreadProcessId(hit, out uint hitProcess);
        Win32Helper.GetWindowThreadProcessId(root == IntPtr.Zero ? hit : root, out uint rootProcess);
        bool ownedByDeskBox = hitProcess == currentProcess || rootProcess == currentProcess;

        // Same identity rule as the upstream desktop probes: compare with the
        // shell window's process, and keep the class verdict while Explorer is
        // restarting (GetShellWindow returns zero).
        bool ownedByShell = true;
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow != IntPtr.Zero)
        {
            Win32Helper.GetWindowThreadProcessId(shellWindow, out uint shellProcess);
            ownedByShell = shellProcess == 0 || rootProcess == 0 || shellProcess == rootProcess;
        }

        MappedDragDropTargetKind kind = MappedDragToDesktopPolicy.ClassifyTarget(chain, ownedByDeskBox, ownedByShell);
        string chainText = string.Join(">", chain) +
                           (ownedByDeskBox ? " [DeskBox]" : string.Empty) +
                           (ownedByShell ? string.Empty : " [not shell]");
        if (kind == MappedDragDropTargetKind.NotDesktop)
        {
            return MappedDesktopDropPoint.None with { Chain = chainText };
        }

        if (listView == IntPtr.Zero && defView != IntPtr.Zero)
        {
            listView = Win32Helper.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        MappedDragIconHit iconHit = MappedDragIconHit.Blank;
        string? iconText = null;
        string? hitFailure = null;
        if (kind == MappedDragDropTargetKind.DesktopIconList)
        {
            DesktopListViewHit listHit = DesktopListViewProbeNativeMethods.HitTest(listView, screenPoint);
            if (!listHit.Succeeded)
            {
                iconHit = MappedDragIconHit.Failed;
                hitFailure = listHit.Failure;
            }
            else if (listHit.ItemIndex >= 0)
            {
                iconHit = MappedDragIconHit.Item;
                iconText = listHit.ItemText;
            }
        }

        int clientX = screenPoint.X;
        int clientY = screenPoint.Y;
        if (listView != IntPtr.Zero)
        {
            Win32Helper.POINT client = screenPoint;
            if (Win32Helper.ScreenToClient(listView, ref client))
            {
                clientX = client.X;
                clientY = client.Y;
            }
            else
            {
                listView = IntPtr.Zero;
            }
        }

        int offsetY = clientY - screenPoint.Y;
        int areaTop = 0;
        int areaBottom = 0;
        if (Win32Helper.TryGetMonitorWorkArea(screenPoint.X, screenPoint.Y, out _, out Win32Helper.RECT work))
        {
            areaTop = work.Top + offsetY;
            areaBottom = work.Bottom + offsetY;
        }

        return new MappedDesktopDropPoint(
            kind, iconHit, iconText, listView, clientX, clientY, areaTop, areaBottom, chainText, hitFailure);
    }

    private static bool IsOverlay(IntPtr window)
    {
        IntPtr root = Win32Helper.GetAncestor(window, Win32Helper.GA_ROOT);
        string className = ClassOf(root == IntPtr.Zero ? window : root);
        return OverlayClasses.Any(overlay => string.Equals(overlay, className, StringComparison.OrdinalIgnoreCase));
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return Win32Helper.GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}
