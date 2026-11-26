using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using DeskBox.Platform;
using static DeskBox.Platform.MappedDesktopSurfaceNativeMethods;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>A filtered desktop Shell view; file attributes and Explorer settings are never changed.</summary>
internal sealed partial class MappedDesktopSurfaceController : IDisposable
{
    private static MappedDesktopSurfaceController? s_current;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly HashSet<DispatcherQueueTimer> _placementTimers = [];
    private MappedDesktopShellView? _view;
    private Process? _guardian;
    private IntPtr _nativeListView;
    private IntPtr _host;
    private string[] _excluded = [];
    private bool _hidNative;
    private bool _disposed;
    private string? _lastFailure;
    private Dictionary<string, (int X, int Y)>? _initialPositions;
    private bool _positionsRestored;
    private int _positionReplayAttempts;
    private string? _workAreaSignature;
    private DesktopIconPlacementNativeMethods.ViewInfo _originalViewInfo;
    private bool _viewInfoApplied;
    private const string HostClassName = "DeskBox.MappedDesktopSurface";
    private static ushort s_hostClass;

    internal MappedDesktopSurfaceController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += OnTick;
    }

    internal void Update(IReadOnlyCollection<string> excluded)
    {
        bool changed = !_excluded.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(excluded);
        _excluded = excluded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (changed && _view is not null && !_view.UpdateExcludedPaths(_excluded))
        {
            ReleaseView();
        }

        Reconcile();
        _timer.Start();
    }

    /// <summary>Exact owned-HWND ancestry, not a window-class guess.</summary>
    internal static bool OwnsWindow(IntPtr window)
    {
        IntPtr host = s_current?._host ?? IntPtr.Zero;
        for (int depth = 0; host != IntPtr.Zero && window != IntPtr.Zero && depth < 32; depth++)
        {
            if (window == host)
            {
                return true;
            }

            window = Win32Helper.GetParent(window);
        }

        return false;
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => Reconcile();

    internal static bool TryPlaceOnSurface(IReadOnlyList<string> paths, MappedDesktopDropPoint drop)
    {
        MappedDesktopSurfaceController? surface = s_current;
        return surface is not null && OwnsWindow(drop.ListView) &&
               surface._dispatcher.TryEnqueue(() => surface.PlaceWhenReady(paths.ToArray(), drop));
    }

    private void PlaceWhenReady(string[] paths, MappedDesktopDropPoint drop)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            bool done = _disposed || ++attempts >= 15;
            IntPtr folderView = _view?.AcquireFolderView() ?? IntPtr.Zero;
            try
            {
                if (folderView != IntPtr.Zero && DesktopIconPlacementNativeMethods.TryGetViewInfo(out var info, folderView))
                {
                    var points = MappedDragToDesktopPolicy.ComputeIconOrigins(drop.ClientX, drop.ClientY,
                        info.SpacingX, info.SpacingY, paths.Length, drop.AreaTopClient, drop.AreaBottomClient);
                    done |= info.AutoArrange || DesktopIconPlacementNativeMethods.TryPositionItems(paths, points, folderView) >= paths.Length;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[MappedOrganize] Surface icon placement failed: {ex.Message}");
                done = true;
            }
            finally
            {
                if (folderView != IntPtr.Zero)
                {
                    Marshal.Release(folderView);
                }

                if (done)
                {
                    timer.Stop();
                    _placementTimers.Remove(timer);
                }
            }
        };
        _placementTimers.Add(timer);
        timer.Start();
    }

    private void Reconcile()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IntPtr native = DesktopIconVisibilityService.FindDesktopIconListView();
            if (_nativeListView != IntPtr.Zero &&
                (native != _nativeListView || !Win32Helper.IsWindow(_host) || _guardian is { HasExited: true }))
            {
                ReleaseView();
            }

            if (native == IntPtr.Zero)
            {
                return;
            }

            IntPtr parent = Win32Helper.GetParent(native);
            if (parent == IntPtr.Zero || !GetClientRect(parent, out Win32Helper.RECT bounds) ||
                bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            {
                return;
            }

            if (_view is null)
            {
                // Respect a user who disabled desktop icons before mapping started.
                if (!Win32Helper.IsWindowVisible(native))
                {
                    return;
                }

                _nativeListView = native;
                _initialPositions = DesktopIconPlacementNativeMethods.CapturePositions();
                if (!DesktopIconPlacementNativeMethods.TryGetViewInfo(out _originalViewInfo))
                {
                    throw new InvalidOperationException("Native desktop layout settings are not ready.");
                }
                if (!EnsureHostClass())
                {
                    Fail("Could not register the desktop surface window; native icons remain visible.");
                    return;
                }

                _host = CreateWindowEx(0x08000080, HostClassName, "DeskBox mapped desktop surface",
                    0x46000000, 0, 0, bounds.Right, bounds.Bottom, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
                var includeItem = MappedDesktopShellView.CreateDesktopPresentationFilter(_initialPositions.Keys, userDesktop, publicDesktop);
                if (_host == IntPtr.Zero || !MappedDesktopShellView.TryCreate(_host, bounds, _excluded, out _view, includeItem))
                {
                    Fail("Could not create the filtered desktop view; native icons remain visible.");
                    ReleaseView();
                    return;
                }

                s_current = this;
            }

            Win32Helper.SetWindowPos(_host, IntPtr.Zero, 0, 0, bounds.Right, bounds.Bottom,
                Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_NOZORDER);
            if (!_view.Resize(bounds) || !_view.IsReady)
            {
                if (_hidNative)
                {
                    Fail("Active desktop surface stopped responding; restoring native icons.");
                    ReleaseView();
                }

                return;
            }

            ApplyWorkAreas();

            if (!_viewInfoApplied)
            {
                IntPtr folderView = _view.AcquireFolderView();
                try
                {
                    if (folderView == IntPtr.Zero || !DesktopIconPlacementNativeMethods.TryApplyViewInfo(_originalViewInfo, folderView))
                    {
                        Fail("Could not preserve the native desktop layout settings; native icons remain visible.");
                        ReleaseView();
                        return;
                    }

                    _viewInfoApplied = true;
                    _positionsRestored = _originalViewInfo.AutoArrange;
                }
                finally
                {
                    if (folderView != IntPtr.Zero)
                    {
                        Marshal.Release(folderView);
                    }
                }
            }

            if (!_positionsRestored && _initialPositions is not null)
            {
                var excluded = _excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expected = _initialPositions.Where(pair => !excluded.Contains(pair.Key) &&
                    (!Path.IsPathFullyQualified(pair.Key) || File.Exists(pair.Key) || Directory.Exists(pair.Key))).ToArray();
                int positioned = 0;
                IntPtr folderView = _view.AcquireFolderView();
                if (folderView != IntPtr.Zero)
                {
                    try
                    {
                        positioned = DesktopIconPlacementNativeMethods.TryPositionItems(
                            expected.Select(pair => pair.Key).ToArray(), expected.Select(pair => pair.Value).ToArray(), folderView);
                    }
                    finally
                    {
                        Marshal.Release(folderView);
                    }
                }

                _positionsRestored = positioned >= expected.Length;
                if (!_positionsRestored)
                {
                    if (++_positionReplayAttempts >= 10)
                    {
                        Fail("Could not restore the desktop layout; native icons remain visible.");
                        ReleaseView();
                    }

                    return;
                }
            }

            if (_guardian is null && !TryStartGuardian(native))
            {
                ReleaseView();
                return;
            }

            // A ready replacement and a live crash guardian must precede hiding.
            Win32Helper.ShowWindow(_host, Win32Helper.SW_SHOWNOACTIVATE);
            _hidNative = true;
            Win32Helper.ShowWindow(native, Win32Helper.SW_HIDE);
            _lastFailure = null;
        }
        catch (Exception ex)
        {
            Fail($"Desktop surface unavailable: {ex.Message}");
            ReleaseView();
        }
    }

    private bool TryStartGuardian(IntPtr native)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "DeskBox.Updater.exe");
        if (!File.Exists(helper))
        {
            Fail("Desktop crash guardian is missing; native icons remain visible.");
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(native, out uint shellProcess);
        using Process owner = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     "--desktop-surface-guardian", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                     owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                     native.ToInt64().ToString(CultureInfo.InvariantCulture), shellProcess.ToString(CultureInfo.InvariantCulture),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        _guardian = Process.Start(start);
        if (_guardian is null)
        {
            return false;
        }

        Task<string?> ready = _guardian.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(3)) || ready.Result != "READY" || _guardian.HasExited)
        {
            Fail("Desktop crash guardian did not become ready; native icons remain visible.");
            return false;
        }

        return true;
    }

    /// <summary>Safe even during ProcessExit: no COM calls or UI-thread waits.</summary>
    internal void RestoreNativeIcons()
    {
        IntPtr native = _nativeListView;
        if (_hidNative && native != IntPtr.Zero && Win32Helper.IsWindow(native) &&
            DesktopBlankHitTest.IsShellDesktopListView(native))
        {
            Win32Helper.ShowWindow(native, Win32Helper.SW_SHOWNOACTIVATE);
        }

        _hidNative = false;
    }

    private void ReleaseView()
    {
        bool savePositions = _hidNative && Win32Helper.IsWindow(_nativeListView);
        RestoreNativeIcons();
        foreach (DispatcherQueueTimer timer in _placementTimers)
        {
            timer.Stop();
        }

        _placementTimers.Clear();
        if (savePositions && _view is { IsReady: true })
        {
            IntPtr folderView = _view.AcquireFolderView();
            if (folderView != IntPtr.Zero)
            {
                try
                {
                    if (DesktopIconPlacementNativeMethods.TryGetViewInfo(out var info, folderView) &&
                        DesktopIconPlacementNativeMethods.TryApplyViewInfo(info) && !info.AutoArrange)
                    {
                        var positions = DesktopIconPlacementNativeMethods.CapturePositions(folderView);
                        DesktopIconPlacementNativeMethods.TryPositionItems(positions.Keys.ToArray(), positions.Values.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[MappedOrganize] Could not save desktop icon positions: {ex.Message}");
                }
                finally
                {
                    Marshal.Release(folderView);
                }
            }
        }

        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }

        if (_guardian is not null)
        {
            try
            {
                if (!_guardian.HasExited)
                {
                    _guardian.Kill();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[MappedOrganize] Could not stop desktop guardian: {ex.Message}");
            }

            _guardian.Dispose();
            _guardian = null;
        }

        try
        {
            _view?.Dispose();
        }
        finally
        {
            _view = null;
            if (_host != IntPtr.Zero && Win32Helper.IsWindow(_host))
            {
                DestroyWindow(_host);
            }

            _host = IntPtr.Zero;
            _nativeListView = IntPtr.Zero;
            _initialPositions = null;
            _positionsRestored = false;
            _positionReplayAttempts = 0;
            _workAreaSignature = null;
            _viewInfoApplied = false;
        }
    }

    private unsafe void ApplyWorkAreas()
    {
        IntPtr listView = FindListView(_view?.ViewWindow ?? IntPtr.Zero);
        if (listView == IntPtr.Zero)
        {
            return;
        }

        var areas = new List<Win32Helper.RECT>();
        foreach (var monitor in Win32Helper.GetMonitorWorkAreaInfos())
        {
            Win32Helper.POINT top = new() { X = monitor.WorkArea.Left, Y = monitor.WorkArea.Top };
            Win32Helper.POINT bottom = new() { X = monitor.WorkArea.Right, Y = monitor.WorkArea.Bottom };
            if (Win32Helper.ScreenToClient(listView, ref top) && Win32Helper.ScreenToClient(listView, ref bottom))
            {
                areas.Add(new Win32Helper.RECT { Left = top.X, Top = top.Y, Right = bottom.X, Bottom = bottom.Y });
            }
        }

        string signature = string.Join(";", areas.Select(area => $"{area.Left},{area.Top},{area.Right},{area.Bottom}"));
        if (areas.Count == 0 || signature == _workAreaSignature)
        {
            return;
        }

        Win32Helper.RECT[] rectangles = areas.ToArray();
        fixed (Win32Helper.RECT* pointer = rectangles)
        {
            // This is our own list view, not Explorer's cross-process implementation.
            SendMessage(listView, 0x1000 + 65, (nuint)rectangles.Length, (IntPtr)pointer);
        }

        SendMessage(listView, 0x1000 + 1, 0, new IntPtr(-1));
        SendMessage(listView, 0x1000 + 38, 0, new IntPtr(-1));
        _workAreaSignature = signature;
    }

    private static IntPtr FindListView(IntPtr window, int depth = 0)
    {
        if (window == IntPtr.Zero || depth > 16)
        {
            return IntPtr.Zero;
        }

        var name = new StringBuilder(64);
        Win32Helper.GetClassName(window, name, name.Capacity);
        if (name.ToString() == "SysListView32")
        {
            return window;
        }

        for (IntPtr child = Win32Helper.FindWindowEx(window, IntPtr.Zero, null, null);
             child != IntPtr.Zero;
             child = Win32Helper.FindWindowEx(window, child, null, null))
        {
            if (FindListView(child, depth + 1) is var found && found != IntPtr.Zero)
            {
                return found;
            }
        }

        return IntPtr.Zero;
    }

    private static unsafe bool EnsureHostClass()
    {
        if (s_hostClass != 0)
        {
            return true;
        }

        fixed (char* name = HostClassName)
        {
            var windowClass = new WindowClass
            {
                Procedure = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&HostWindowProcedure,
                Instance = GetModuleHandle(null),
                Name = (IntPtr)name,
            };
            s_hostClass = RegisterClass(ref windowClass);
        }

        return s_hostClass != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr HostWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        message == 0x0014 ? new IntPtr(1) : DefWindowProc(window, message, wParam, lParam);

    private void Fail(string message)
    {
        if (_lastFailure != message)
        {
            App.Log($"[MappedOrganize] {message}");
            _lastFailure = message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        ReleaseView();
    }

}
