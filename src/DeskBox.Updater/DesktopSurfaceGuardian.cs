using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Updater;

internal readonly record struct DesktopSurfaceGuardianArguments(
    int OwnerProcessId,
    long OwnerStartTimeUtcTicks,
    nint NativeListViewHandle,
    int ShellProcessId);

internal static class DesktopSurfaceGuardian
{
    internal const string Command = "--desktop-surface-guardian";
    private const string ListViewClassName = "SysListView32";
    private const int ShowWindowWithoutActivation = 8;

    internal static int Run(IReadOnlyList<string> args)
    {
        if (!TryParseArguments(args, out DesktopSurfaceGuardianArguments arguments))
        {
            return 2;
        }

        try
        {
            using Process owner = Process.GetProcessById(arguments.OwnerProcessId);
            var ownerHandle = owner.SafeHandle;
            if (!IsOwnerProcessInstance(arguments, owner.Id, owner.StartTime.ToUniversalTime().Ticks))
            {
                return 2;
            }

            if (!IsCurrentListView(arguments))
            {
                return 2;
            }

            Console.Out.WriteLine("READY");
            Console.Out.Flush();
            owner.WaitForExit();
            RestoreListViewIfStillOwned(arguments);
            GC.KeepAlive(ownerHandle);
            return 0;
        }
        catch
        {
            return 3;
        }
    }

    internal static bool TryParseArguments(
        IReadOnlyList<string> args,
        out DesktopSurfaceGuardianArguments arguments)
    {
        arguments = default;
        if (args.Count != 5 ||
            !string.Equals(args[0], Command, StringComparison.Ordinal) ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerProcessId) ||
            ownerProcessId <= 0 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long startTimeUtcTicks) ||
            startTimeUtcTicks <= 0 || startTimeUtcTicks > DateTime.MaxValue.Ticks ||
            !ulong.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong windowHandle) ||
            windowHandle == 0 || windowHandle > long.MaxValue ||
            (IntPtr.Size == 4 && windowHandle > int.MaxValue) ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int shellProcessId) ||
            shellProcessId <= 0 || ownerProcessId == shellProcessId)
        {
            return false;
        }

        arguments = new DesktopSurfaceGuardianArguments(
            ownerProcessId,
            startTimeUtcTicks,
            unchecked((nint)(long)windowHandle),
            shellProcessId);
        return true;
    }

    internal static bool IsOwnerProcessInstance(
        DesktopSurfaceGuardianArguments expected,
        int actualProcessId,
        long actualStartTimeUtcTicks) =>
        expected.OwnerProcessId > 0 &&
        expected.OwnerStartTimeUtcTicks > 0 &&
        expected.OwnerProcessId == actualProcessId &&
        expected.OwnerStartTimeUtcTicks == actualStartTimeUtcTicks;

    internal static bool ShouldRestoreListView(
        bool isWindow,
        string? className,
        int windowProcessId,
        int currentShellProcessId,
        int expectedShellProcessId) =>
        isWindow &&
        string.Equals(className, ListViewClassName, StringComparison.Ordinal) &&
        expectedShellProcessId > 0 &&
        windowProcessId == expectedShellProcessId &&
        currentShellProcessId == expectedShellProcessId;

    private static void RestoreListViewIfStillOwned(DesktopSurfaceGuardianArguments arguments)
    {
        if (IsCurrentListView(arguments))
        {
            _ = ShowWindow(arguments.NativeListViewHandle, ShowWindowWithoutActivation);
        }
    }

    private static bool IsCurrentListView(DesktopSurfaceGuardianArguments arguments)
    {
        nint handle = arguments.NativeListViewHandle;
        if (!IsWindow(handle))
        {
            return false;
        }

        var className = new StringBuilder(64);
        if (GetClassNameW(handle, className, className.Capacity) == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out uint windowProcessId);
        nint shellWindow = GetShellWindow();
        _ = GetWindowThreadProcessId(shellWindow, out uint currentShellProcessId);
        return ShouldRestoreListView(
            isWindow: true,
            className.ToString(),
            unchecked((int)windowProcessId),
            unchecked((int)currentShellProcessId),
            arguments.ShellProcessId);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);
}
