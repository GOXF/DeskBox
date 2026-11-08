using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Hides / shows Explorer's native desktop icons for 「映射整理」.
///
/// Approach (see docs/architecture/mapped-organize.md): the desktop icon
/// list view (SysListView32 under the desktop SHELLDLL_DefView) is hidden with
/// ShowWindow(SW_HIDE). Unlike flipping Explorer's persisted "Show desktop
/// icons" setting (HideIcons + WM_COMMAND 0x7402 toggle), this state is NOT
/// persisted by Explorer: an Explorer restart, a DeskBox crash followed by a
/// sign-out, or any failure mode always ends with the icons visible again, so
/// DeskBox can never strand a user without desktop icons. The price is that
/// DeskBox must re-apply the hide after Explorer recreates the window, which
/// <see cref="MappedOrganizeService"/> does with a cheap periodic check.
/// </summary>
internal static class DesktopIconVisibilityService
{
    private const int SwShow = 5;

    public static IntPtr FindDesktopIconListView()
    {
        IntPtr found = IntPtr.Zero;
        Win32Helper.EnumWindows((hWnd, _) =>
        {
            IntPtr defView = Win32Helper.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                return true;
            }

            IntPtr listView = Win32Helper.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (listView != IntPtr.Zero && DesktopBlankHitTest.IsShellDesktopListView(listView))
            {
                found = listView;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Returns true when the icons are (now) in the requested state.</summary>
    public static bool Apply(bool hidden)
    {
        IntPtr listView = FindDesktopIconListView();
        if (listView == IntPtr.Zero)
        {
            // Explorer is restarting or not running; nothing to hide, and a
            // freshly created list view is visible by default.
            return !hidden;
        }

        bool visible = Win32Helper.IsWindowVisible(listView);
        if (visible == !hidden)
        {
            return true;
        }

        Win32Helper.ShowWindow(listView, hidden ? Win32Helper.SW_HIDE : SwShow);
        App.Log($"[MappedOrganize] Desktop icons {(hidden ? "hidden" : "shown")} hwnd=0x{listView.ToInt64():X}");
        return true;
    }
}
