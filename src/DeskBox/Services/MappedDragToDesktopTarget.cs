using System.Text;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>Where a mapped-widget drag was released, resolved against the live desktop windows.</summary>
internal readonly record struct MappedDesktopDropPoint(
    MappedDragDropTargetKind Kind,
    bool OnIcon,
    IntPtr ListView,
    int ClientX,
    int ClientY,
    int AreaTopClient,
    int AreaBottomClient)
{
    internal static MappedDesktopDropPoint None { get; } =
        new(MappedDragDropTargetKind.NotDesktop, false, IntPtr.Zero, 0, 0, 0, 0);
}

/// <summary>
/// GOXF fork, 「映射整理」: native probe behind 「从映射格子拖到桌面 = 放回桌面」.
/// Reads the window under the release point (every monitor: the desktop list
/// view spans the virtual screen, and Windows 11 may host it under WorkerW),
/// classifies it with <see cref="MappedDragToDesktopPolicy.ClassifyTarget"/>,
/// and tells blank desktop apart from an icon with the upstream
/// <see cref="DesktopBlankHitTest"/>.
/// </summary>
internal static class MappedDragToDesktopTarget
{
    private const int MaxAncestorDepth = 16;

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
        if (hit == IntPtr.Zero)
        {
            return MappedDesktopDropPoint.None;
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
        if (kind == MappedDragDropTargetKind.NotDesktop)
        {
            return MappedDesktopDropPoint.None;
        }

        if (listView == IntPtr.Zero && defView != IntPtr.Zero)
        {
            listView = Win32Helper.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        // DesktopBlankHitTest answers false both for an icon and for a failed
        // hit test; either way the drop is left to the upstream handling.
        bool onIcon = kind == MappedDragDropTargetKind.DesktopIconList &&
                      !DesktopBlankHitTest.IsBlankDesktopPoint(screenPoint);

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

        return new MappedDesktopDropPoint(kind, onIcon, listView, clientX, clientY, areaTop, areaBottom);
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return Win32Helper.GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}
