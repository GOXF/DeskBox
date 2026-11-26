using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>Hosts the Shell's desktop namespace in a caller-owned child window.</summary>
internal sealed unsafe partial class MappedDesktopShellView : IDisposable
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint FolderViewIcon = 1;
    private const uint FolderViewDesktop = 0x20;
    private const uint FolderViewTransparent = 0x100;
    private const uint FolderViewNoClientEdge = 0x200;
    private const uint FolderViewNoScroll = 0x400;
    private const uint FolderViewNoBrowserViewState = 0x10000000;
    private const uint ExplorerBrowserNavigateOnce = 0x1;
    private const uint ExplorerBrowserNoBorder = 0x40;
    private const uint ExplorerBrowserNoPersistViewState = 0x80;
    private const uint AllViewItems = 2;
    private const uint MaxPathChars = 32768;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int IExplorerBrowserInitializeSlot = 3;
    private const int IExplorerBrowserDestroySlot = 4;
    private const int IExplorerBrowserSetRectSlot = 5;
    private const int IExplorerBrowserAdviseSlot = 9;
    private const int IExplorerBrowserUnadviseSlot = 10;
    private const int IExplorerBrowserSetOptionsSlot = 11;
    private const int IExplorerBrowserBrowseToObjectSlot = 14;
    private const int IExplorerBrowserGetCurrentViewSlot = 17;
    private const int IFolderFilterSiteSetFilterSlot = 3;
    private const int IFolderViewGetFolderItemCountSlot = 7;
    private const int IShellViewRefreshSlot = 8;
    private const int IOleWindowGetWindowSlot = 3;

    // IIDs and IExplorerBrowser method slots follow the Windows 10 SDK shobjidl_core.h.
    private static readonly Guid ClsidExplorerBrowser = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
    private static readonly Guid IidExplorerBrowser = new("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6");
    private static readonly Guid IidFolderFilterSite = new("C0A651F5-B48B-11D2-B5ED-006097C686F6");
    private static readonly Guid IidFolderFilter = new("9CC22886-DC8E-11D2-B1D0-00C04F8EEB3E");
    private static readonly Guid IidExplorerBrowserEvents = new("361BBDC7-E6EE-4E13-BE58-58E2240C810F");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid IidFolderViewOptions = new("3CC974D2-B302-4D36-AD3E-06D93F695D3F");
    private static readonly Guid IidShellView = new("000214E3-0000-0000-C000-000000000046");
    private static readonly IntPtr FolderFilterVtable = CreateFolderFilterVtable();
    private static readonly IntPtr ExplorerBrowserEventsVtable = CreateExplorerBrowserEventsVtable();

    private readonly IntPtr _parent;
    private readonly int _ownerThreadId;
    private HashSet<string> _excludedPaths;
    private readonly Func<string, bool>? _includeItem;
    private IntPtr _browser;
    private IntPtr _filterSite;
    private IntPtr _filter;
    private IntPtr _events;
    private uint _eventCookie;
    private bool _eventsAdvised;
    private bool _initializeAttempted;
    private bool _disposed;
    private bool _needsRefresh;
    private int _navigationComplete;

    private MappedDesktopShellView(IntPtr parent, HashSet<string> excludedPaths, Func<string, bool>? includeItem)
    {
        _parent = parent;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _excludedPaths = excludedPaths;
        _includeItem = includeItem;
    }

    internal static Func<string, bool> CreateDesktopPresentationFilter(IEnumerable<string> originalItems, params string[] desktopDirectories)
    {
        var original = originalItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = NormalizeExcludedPaths(desktopDirectories.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
        // shortcut: virtual-icon preferences are captured on creation; add registry notifications for live preference editing.
        return path => original.Contains(path) ||
            (!path.StartsWith("::", StringComparison.Ordinal) && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
             Path.IsPathFullyQualified(path) && roots.Contains(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty));
    }

    /// <summary>Creates a filtered desktop-namespace view under <paramref name="parent"/>.</summary>
    internal static bool TryCreate(
        IntPtr parent,
        Win32Helper.RECT clientBounds,
        IReadOnlyCollection<string> excludedPaths,
        out MappedDesktopShellView? view,
        Func<string, bool>? includeItem = null)
    {
        return TryCreateCore(parent, clientBounds, excludedPaths, IntPtr.Zero, out view, includeItem);
    }

    /// <summary>Creates a fixture view from a borrowed shell-folder pointer; intended for native integration tests.</summary>
    internal static bool TryCreateForFolder(
        IntPtr parent,
        Win32Helper.RECT clientBounds,
        IReadOnlyCollection<string> excludedPaths,
        IntPtr sourceFolder,
        out MappedDesktopShellView? view,
        Func<string, bool>? includeItem = null)
    {
        view = null;
        return sourceFolder != IntPtr.Zero &&
               TryCreateCore(parent, clientBounds, excludedPaths, sourceFolder, out view, includeItem);
    }

    private static bool TryCreateCore(
        IntPtr parent,
        Win32Helper.RECT clientBounds,
        IReadOnlyCollection<string> excludedPaths,
        IntPtr sourceFolder,
        out MappedDesktopShellView? view,
        Func<string, bool>? includeItem)
    {
        view = null;
        if (parent == IntPtr.Zero || excludedPaths is null ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
            !TryNormalizeExcludedPaths(excludedPaths, out HashSet<string>? normalized))
        {
            return false;
        }

        MappedDesktopShellView? candidate = null;
        try
        {
            if (!IsWindow(parent))
            {
                return false;
            }

            candidate = new MappedDesktopShellView(parent, normalized, includeItem);
            if (!candidate.Initialize(clientBounds, sourceFolder))
            {
                candidate.Dispose();
                return false;
            }

            view = candidate;
            return true;
        }
        catch (Exception)
        {
            candidate?.Dispose();
            view = null;
            return false;
        }
    }

    /// <summary>True only after ExplorerBrowser exposes a live, populated IFolderView.</summary>
    internal bool IsReady
    {
        get
        {
            if (!CanUseNativeView || !TryGetFolderView(out IntPtr folderView, out _, out _))
            {
                return false;
            }

            ReleaseComObject(folderView);
            return true;
        }
    }

    /// <summary>Returns an AddRef-owned IFolderView pointer for desktop item placement, or zero until ready.</summary>
    internal IntPtr AcquireFolderView()
    {
        return CanUseNativeView && TryGetFolderView(out IntPtr folderView, out _, out _)
            ? folderView
            : IntPtr.Zero;
    }

    /// <summary>The hosted Shell view HWND, or zero until it is ready.</summary>
    internal IntPtr ViewWindow
    {
        get
        {
            if (!CanUseNativeView || !TryGetFolderView(out IntPtr folderView, out IntPtr window, out _))
            {
                return IntPtr.Zero;
            }

            ReleaseComObject(folderView);
            return window;
        }
    }

    /// <summary>Replaces the exact absolute paths hidden from this view and refreshes it.</summary>
    internal bool UpdateExcludedPaths(IReadOnlyCollection<string> excludedPaths)
    {
        if (!CanUseNativeView || excludedPaths is null ||
            !TryNormalizeExcludedPaths(excludedPaths, out HashSet<string>? normalized))
        {
            return false;
        }

        HashSet<string> previous = Volatile.Read(ref _excludedPaths);
        Volatile.Write(ref _excludedPaths, normalized);
        if (!TryGetShellView(out IntPtr shellView))
        {
            // The initial navigation has not produced a view yet; its first enumeration reads this snapshot.
            _needsRefresh = true;
            return true;
        }

        int hr;
        try
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(shellView, IShellViewRefreshSlot))(shellView);
        }
        catch (Exception)
        {
            hr = E_POINTER;
        }
        finally
        {
            ReleaseComObject(shellView);
        }

        if (hr >= 0)
        {
            _needsRefresh = false;
            return true;
        }

        Volatile.Write(ref _excludedPaths, previous);
        _needsRefresh = true;
        if (TryGetShellView(out IntPtr restoreView))
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(restoreView, IShellViewRefreshSlot))(restoreView);
            }
            catch (Exception)
            {
            }
            finally
            {
                ReleaseComObject(restoreView);
            }
        }

        return false;
    }

    /// <summary>Resizes ExplorerBrowser to the caller's client-coordinate rectangle.</summary>
    internal bool Resize(Win32Helper.RECT clientBounds)
    {
        if (!CanUseNativeView || _browser == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32Helper.RECT, int>)Slot(_browser, IExplorerBrowserSetRectSlot))(
                _browser, IntPtr.Zero, clientBounds) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("MappedDesktopShellView must be disposed on its creating STA thread.");
        }

        _disposed = true;
        if (_filterSite != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_filterSite, IFolderFilterSiteSetFilterSlot))(
                    _filterSite, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        if (_eventsAdvised && _browser != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_browser, IExplorerBrowserUnadviseSlot))(
                    _browser, _eventCookie);
            }
            catch (Exception)
            {
            }
            _eventsAdvised = false;
            _eventCookie = 0;
        }

        if (_initializeAttempted && _browser != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_browser, IExplorerBrowserDestroySlot))(_browser);
            }
            catch (Exception)
            {
            }
        }

        ReleaseComObject(_filterSite);
        _filterSite = IntPtr.Zero;
        ReleaseComObject(_filter);
        _filter = IntPtr.Zero;
        ReleaseComObject(_browser);
        _browser = IntPtr.Zero;
        ReleaseComObject(_events);
        _events = IntPtr.Zero;
    }

    private bool Initialize(Win32Helper.RECT bounds, IntPtr sourceFolder)
    {
        Guid clsid = ClsidExplorerBrowser;
        Guid iid = IidExplorerBrowser;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out _browser) < 0 || _browser == IntPtr.Zero)
        {
            return false;
        }

        Guid optionsIid = IidFolderViewOptions;
        if (QueryInterface(_browser, ref optionsIid, out IntPtr options) < 0 || options == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Custom icon positioning must be requested before the Shell view is created.
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Slot(options, 3))(options, 2, 2) < 0)
            {
                return false;
            }
        }
        finally
        {
            ReleaseComObject(options);
        }

        var settings = new FolderSettings
        {
            ViewMode = FolderViewIcon,
            Flags = FolderViewDesktop | FolderViewTransparent | FolderViewNoClientEdge |
                    FolderViewNoScroll | FolderViewNoBrowserViewState
        };
        _initializeAttempted = true;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32Helper.RECT*, FolderSettings*, int>)Slot(_browser, IExplorerBrowserInitializeSlot))(
                _browser, _parent, &bounds, &settings) < 0)
        {
            return false;
        }

        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_browser, IExplorerBrowserSetOptionsSlot))(
                _browser, ExplorerBrowserNavigateOnce | ExplorerBrowserNoBorder | ExplorerBrowserNoPersistViewState) < 0)
        {
            return false;
        }

        Guid filterSiteIid = IidFolderFilterSite;
        if (QueryInterface(_browser, ref filterSiteIid, out _filterSite) < 0 || _filterSite == IntPtr.Zero)
        {
            return false;
        }

        _filter = CreateFolderFilter(this);
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_filterSite, IFolderFilterSiteSetFilterSlot))(
                _filterSite, _filter) < 0)
        {
            return false;
        }

        _events = CreateCallbackObject(this, ExplorerBrowserEventsVtable);
        uint eventCookie = 0;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(_browser, IExplorerBrowserAdviseSlot))(
                _browser, _events, &eventCookie) < 0)
        {
            return false;
        }
        _eventCookie = eventCookie;
        _eventsAdvised = true;

        IntPtr browseFolder = sourceFolder;
        if (browseFolder == IntPtr.Zero)
        {
            int desktopHr = SHGetDesktopFolder(out browseFolder);
            if (desktopHr < 0 || browseFolder == IntPtr.Zero)
            {
                ReleaseComObject(browseFolder);
                return false;
            }
        }
        else
        {
            AddRefComObject(browseFolder);
        }

        try
        {
            // SHGetDesktopFolder is the true namespace root, including Public Desktop and virtual items.
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Slot(_browser, IExplorerBrowserBrowseToObjectSlot))(
                    _browser, browseFolder, 0) < 0)
            {
                return false;
            }
        }
        finally
        {
            ReleaseComObject(browseFolder);
        }

        return true;
    }

    private bool TryGetFolderView(out IntPtr folderView, out IntPtr window, out int itemCount)
    {
        folderView = IntPtr.Zero;
        window = IntPtr.Zero;
        itemCount = 0;
        Guid iid = IidFolderView;
        if (_browser == IntPtr.Zero || Volatile.Read(ref _navigationComplete) == 0)
        {
            return false;
        }

        int viewHr = GetCurrentView(ref iid, out folderView);
        if (viewHr < 0 || folderView == IntPtr.Zero)
        {
            ReleaseComObject(folderView);
            folderView = IntPtr.Zero;
            return false;
        }

        try
        {
            int count = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, int*, int>)Slot(folderView, IFolderViewGetFolderItemCountSlot))(
                    folderView, AllViewItems, &count) < 0 || count < 0)
            {
                return false;
            }
            itemCount = count;

            Guid shellViewIid = IidShellView;
            int shellViewHr = GetCurrentView(ref shellViewIid, out IntPtr shellView);
            if (shellViewHr < 0 || shellView == IntPtr.Zero)
            {
                ReleaseComObject(shellView);
                return false;
            }

            try
            {
                IntPtr viewWindow = IntPtr.Zero;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(shellView, IOleWindowGetWindowSlot))(
                        shellView, &viewWindow) < 0 || viewWindow == IntPtr.Zero || !IsWindow(viewWindow))
                {
                    window = IntPtr.Zero;
                    return false;
                }
                window = viewWindow;
            }
            finally
            {
                ReleaseComObject(shellView);
            }

            if (_needsRefresh)
            {
                if (TryGetShellView(out IntPtr refreshView))
                {
                    try
                    {
                        if (((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(refreshView, IShellViewRefreshSlot))(refreshView) >= 0)
                        {
                            _needsRefresh = false;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(refreshView);
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            window = IntPtr.Zero;
            return false;
        }
        finally
        {
            if (window == IntPtr.Zero)
            {
                ReleaseComObject(folderView);
                folderView = IntPtr.Zero;
            }
        }
    }

    private bool TryGetShellView(out IntPtr shellView)
    {
        shellView = IntPtr.Zero;
        Guid iid = IidShellView;
        try
        {
            if (_browser != IntPtr.Zero && Volatile.Read(ref _navigationComplete) != 0 &&
                GetCurrentView(ref iid, out shellView) >= 0 && shellView != IntPtr.Zero)
            {
                return true;
            }

            ReleaseComObject(shellView);
            shellView = IntPtr.Zero;
            return false;
        }
        catch (Exception)
        {
            ReleaseComObject(shellView);
            shellView = IntPtr.Zero;
            return false;
        }
    }

    private int GetCurrentView(ref Guid iid, out IntPtr view)
    {
        Guid requestedIid = iid;
        IntPtr currentView = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(_browser, IExplorerBrowserGetCurrentViewSlot))(
            _browser, &requestedIid, &currentView);
        view = currentView;
        return hr;
    }

    private bool CanUseNativeView => !_disposed && Environment.CurrentManagedThreadId == _ownerThreadId;

    internal static HashSet<string> NormalizeExcludedPaths(IReadOnlyCollection<string> excludedPaths)
    {
        ArgumentNullException.ThrowIfNull(excludedPaths);
        if (!TryNormalizeExcludedPaths(excludedPaths, out HashSet<string>? normalized))
        {
            throw new ArgumentException("Excluded paths must be fully qualified filesystem paths.", nameof(excludedPaths));
        }

        return normalized;
    }

    internal static bool IsExcludedPath(string path, HashSet<string> excludedPaths)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && excludedPaths.Contains(NormalizePath(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryNormalizeExcludedPaths(IReadOnlyCollection<string> paths, out HashSet<string> normalized)
    {
        normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string? path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                {
                    normalized.Clear();
                    return false;
                }

                normalized.Add(NormalizePath(path));
            }

            return true;
        }
        catch (Exception)
        {
            normalized.Clear();
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IntPtr CreateFolderFilter(MappedDesktopShellView owner)
    {
        return CreateCallbackObject(owner, FolderFilterVtable);
    }

    private static IntPtr CreateCallbackObject(MappedDesktopShellView owner, IntPtr vtable)
    {
        GCHandle ownerHandle = GCHandle.Alloc(owner);
        try
        {
            ShellCallbackObject* callback = (ShellCallbackObject*)NativeMemory.AllocZeroed((nuint)sizeof(ShellCallbackObject));
            if (callback is null)
            {
                throw new OutOfMemoryException();
            }

            callback->Vtable = vtable;
            callback->OwnerHandle = GCHandle.ToIntPtr(ownerHandle);
            callback->ReferenceCount = 1;
            return (IntPtr)callback;
        }
        catch
        {
            ownerHandle.Free();
            throw;
        }
    }

    private static IntPtr CreateFolderFilterVtable()
    {
        IntPtr* vtable = (IntPtr*)NativeMemory.Alloc((nuint)(5 * IntPtr.Size));
        if (vtable is null)
        {
            throw new OutOfMemoryException();
        }

        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&FilterQueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&FilterAddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&FilterRelease;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)&FilterShouldShow;
        vtable[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, uint*, int>)&FilterGetEnumFlags;
        return (IntPtr)vtable;
    }

    private static IntPtr CreateExplorerBrowserEventsVtable()
    {
        IntPtr* vtable = (IntPtr*)NativeMemory.Alloc((nuint)(7 * IntPtr.Size));
        if (vtable is null)
        {
            throw new OutOfMemoryException();
        }

        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&EventsQueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&FilterAddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&FilterRelease;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&EventsNavigationPending;
        vtable[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&EventsViewCreated;
        vtable[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&EventsNavigationComplete;
        vtable[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&EventsNavigationFailed;
        return (IntPtr)vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EventsQueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        try
        {
            if (iid is null || result is null)
            {
                return E_POINTER;
            }

            if (*iid != IidUnknown && *iid != IidExplorerBrowserEvents)
            {
                *result = IntPtr.Zero;
                return E_NOINTERFACE;
            }

            *result = self;
            Interlocked.Increment(ref ((ShellCallbackObject*)self)->ReferenceCount);
            return S_OK;
        }
        catch (Exception)
        {
            return E_POINTER;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EventsNavigationPending(IntPtr self, IntPtr pidlFolder) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EventsViewCreated(IntPtr self, IntPtr shellView) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EventsNavigationComplete(IntPtr self, IntPtr pidlFolder)
    {
        try
        {
            var callback = (ShellCallbackObject*)self;
            var owner = (MappedDesktopShellView?)GCHandle.FromIntPtr(callback->OwnerHandle).Target;
            if (owner is not null)
            {
                Volatile.Write(ref owner._navigationComplete, 1);
            }
        }
        catch (Exception)
        {
        }

        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EventsNavigationFailed(IntPtr self, IntPtr pidlFolder) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FilterQueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        try
        {
            if (iid is null || result is null)
            {
                return E_POINTER;
            }

            if (*iid != IidUnknown && *iid != IidFolderFilter)
            {
                *result = IntPtr.Zero;
                return E_NOINTERFACE;
            }

            *result = self;
            Interlocked.Increment(ref ((ShellCallbackObject*)self)->ReferenceCount);
            return S_OK;
        }
        catch (Exception)
        {
            return E_POINTER;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint FilterAddRef(IntPtr self)
    {
        try
        {
            return (uint)Interlocked.Increment(ref ((ShellCallbackObject*)self)->ReferenceCount);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint FilterRelease(IntPtr self)
    {
        try
        {
            ShellCallbackObject* filter = (ShellCallbackObject*)self;
            int references = Interlocked.Decrement(ref filter->ReferenceCount);
            if (references == 0)
            {
                try
                {
                    GCHandle.FromIntPtr(filter->OwnerHandle).Free();
                }
                catch (Exception)
                {
                }

                NativeMemory.Free(filter);
            }

            return (uint)Math.Max(references, 0);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FilterShouldShow(IntPtr self, IntPtr shellFolder, IntPtr parentPidl, IntPtr childPidl)
    {
        try
        {
            if (parentPidl == IntPtr.Zero || childPidl == IntPtr.Zero)
            {
                return S_OK;
            }

            IntPtr absolutePidl = ILCombine(parentPidl, childPidl);
            if (absolutePidl == IntPtr.Zero)
            {
                return S_OK;
            }

            try
            {
                var filter = (ShellCallbackObject*)self;
                var owner = (MappedDesktopShellView?)GCHandle.FromIntPtr(filter->OwnerHandle).Target;
                if (owner is null)
                {
                    return S_OK;
                }

                char* pathBuffer = stackalloc char[(int)MaxPathChars];
                if (SHGetPathFromIDListEx(absolutePidl, pathBuffer, MaxPathChars, 0))
                {
                    string path = new(pathBuffer);
                    return IsExcludedPath(path, Volatile.Read(ref owner._excludedPaths)) || owner._includeItem?.Invoke(path) == false
                        ? S_FALSE : S_OK;
                }

                if (owner._includeItem is null || SHGetNameFromIDList(absolutePidl, 0x80028000, out IntPtr name) < 0 || name == IntPtr.Zero)
                {
                    return S_OK;
                }

                try
                {
                    return owner._includeItem(Marshal.PtrToStringUni(name) ?? string.Empty) ? S_OK : S_FALSE;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                ILFree(absolutePidl);
            }
        }
        catch (Exception)
        {
            // A native callback must fail open; never let managed exceptions cross the COM boundary.
            return S_OK;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FilterGetEnumFlags(IntPtr self, IntPtr shellFolder, IntPtr parentPidl, IntPtr* hwnd, uint* flags)
    {
        try
        {
            if (hwnd is not null)
            {
                *hwnd = IntPtr.Zero;
            }

            return S_OK;
        }
        catch (Exception)
        {
            return S_OK;
        }
    }

    private static int QueryInterface(IntPtr comObject, ref Guid iid, out IntPtr result)
    {
        Guid requestedIid = iid;
        IntPtr requested = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(comObject, 0))(
            comObject, &requestedIid, &requested);
        result = requested;
        return hr;
    }

    private static IntPtr Slot(IntPtr comObject, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    private static void ReleaseComObject(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, 2))(comObject);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void AddRefComObject(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, 1))(comObject);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FolderSettings
    {
        internal uint ViewMode;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellCallbackObject
    {
        internal IntPtr Vtable;
        internal IntPtr OwnerHandle;
        internal int ReferenceCount;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr punkOuter, uint context, ref Guid iid, out IntPtr instance);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetDesktopFolder(out IntPtr desktopFolder);

    [LibraryImport("shell32.dll")]
    private static partial IntPtr ILCombine(IntPtr parent, IntPtr child);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(IntPtr pidl, uint displayName, out IntPtr name);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListEx(IntPtr pidl, char* path, uint pathLength, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hwnd);
}
