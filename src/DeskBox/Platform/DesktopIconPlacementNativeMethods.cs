using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// GOXF fork, 「映射整理」: moves desktop icons to a given spot through the
/// desktop's own IFolderView (IShellWindows.FindWindowSW(SWC_DESKTOP) →
/// IServiceProvider(SID_STopLevelBrowser) → IShellBrowser.QueryActiveShellView
/// → IFolderView.SelectAndPositionItems), the documented shell route for
/// positioning desktop items. Vtable slots are called through unmanaged
/// function pointers (no RCW, NativeAOT-safe); slot numbers follow
/// shobjidl_core.h / exdisp.h / servprov.h and count IUnknown as 0-2.
/// Must run on an STA thread. Every call is best effort.
/// </summary>
internal static unsafe partial class DesktopIconPlacementNativeMethods
{
    internal readonly record struct ViewInfo(int SpacingX, int SpacingY, bool AutoArrange);

    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const ushort VtI4 = 3;
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioAllView = 2;
    private const uint ShgdnForParsing = 0x8000;
    private const uint SvsiPositionItem = 0x80;
    private const int ShcneUpdateItem = 0x2000;
    private const uint ShcnfPathW = 0x0005;

    // IShellWindows (IDispatch: 3-6) 7 get_Count, 8 Item, 9 _NewEnum, 10 Register,
    // 11 RegisterPending, 12 Revoke, 13 OnNavigate, 14 OnActivated, 15 FindWindowSW.
    private const int ShellWindowsFindWindowSwSlot = 15;

    // IServiceProvider 3 QueryService.
    private const int ServiceProviderQueryServiceSlot = 3;

    // IShellBrowser (IOleWindow: 3-4) 5 InsertMenusSB ... 15 QueryActiveShellView.
    private const int ShellBrowserQueryActiveShellViewSlot = 15;

    // IFolderView 3 GetCurrentViewMode, 4 SetCurrentViewMode, 5 GetFolder, 6 Item,
    // 7 ItemCount, 8 Items, 9 GetSelectionMarkedItem, 10 GetFocusedItem,
    // 11 GetItemPosition, 12 GetSpacing, 13 GetDefaultSpacing, 14 GetAutoArrange,
    // 15 SelectItem, 16 SelectAndPositionItems.
    private const int FolderViewGetFolderSlot = 5;
    private const int FolderViewItemSlot = 6;
    private const int FolderViewItemCountSlot = 7;
    private const int FolderViewGetSpacingSlot = 12;
    private const int FolderViewGetAutoArrangeSlot = 14;
    private const int FolderViewSelectAndPositionItemsSlot = 16;

    // IShellFolder 3 ParseDisplayName ... 11 GetDisplayNameOf.
    private const int ShellFolderGetDisplayNameOfSlot = 11;

    // STRRET is a UINT followed by a 260-byte union (pointer aligned).
    private const int StrRetSize = 272;
    private const int PathBufferChars = 1024;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IidShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr memory);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrRetToBufW")]
    private static partial int StrRetToBuf(IntPtr strRet, IntPtr pidl, IntPtr buffer, uint bufferChars);

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotifyPath(int eventId, uint flags, string path, IntPtr item2);

    /// <summary>Asks the shell to re-read entries whose attributes DeskBox just changed.</summary>
    internal static void NotifyUpdated(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                SHChangeNotifyPath(ShcneUpdateItem, ShcnfPathW, path, IntPtr.Zero);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Reads the desktop view's icon spacing and auto-arrange state.</summary>
    internal static bool TryGetViewInfo(out ViewInfo info)
    {
        info = default;
        IntPtr view = AcquireDesktopFolderView();
        if (view == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            Win32Helper.POINT spacing = default;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Win32Helper.POINT*, int>)Slot(view, FolderViewGetSpacingSlot))(view, &spacing);
            int autoArrange = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(view, FolderViewGetAutoArrangeSlot))(view);
            info = new ViewInfo(hr >= 0 ? spacing.X : 0, hr >= 0 ? spacing.Y : 0, autoArrange == 0);
            return true;
        }
        finally
        {
            Marshal.Release(view);
        }
    }

    /// <summary>
    /// Positions the desktop items whose parsing paths match <paramref name="paths"/>
    /// at the matching client-coordinate origins. Returns how many items were
    /// found in the view and positioned (0 when the view is not ready yet).
    /// </summary>
    internal static int TryPositionItems(IReadOnlyList<string> paths, IReadOnlyList<(int X, int Y)> origins)
    {
        if (paths.Count == 0 || paths.Count != origins.Count)
        {
            return 0;
        }

        IntPtr view = AcquireDesktopFolderView();
        if (view == IntPtr.Zero)
        {
            return 0;
        }

        IntPtr folder = IntPtr.Zero;
        var pidls = new IntPtr[paths.Count];
        IntPtr strRet = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            Guid iidFolder = IidShellFolder;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(view, FolderViewGetFolderSlot))(view, &iidFolder, &folder);
            if (hr < 0 || folder == IntPtr.Zero)
            {
                return 0;
            }

            int count = 0;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int*, int>)Slot(view, FolderViewItemCountSlot))(view, SvgioAllView, &count);
            if (hr < 0 || count <= 0)
            {
                return 0;
            }

            var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                wanted.TryAdd(Normalize(paths[i]), i);
            }

            strRet = Marshal.AllocHGlobal(StrRetSize);
            buffer = Marshal.AllocHGlobal(PathBufferChars * sizeof(char));
            int found = 0;
            for (int index = 0; index < count && found < wanted.Count; index++)
            {
                IntPtr pidl = IntPtr.Zero;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(view, FolderViewItemSlot))(view, index, &pidl);
                if (hr < 0 || pidl == IntPtr.Zero)
                {
                    continue;
                }

                string? parsingName = GetParsingName(folder, pidl, strRet, buffer);
                if (parsingName is not null &&
                    wanted.TryGetValue(Normalize(parsingName), out int slot) &&
                    pidls[slot] == IntPtr.Zero)
                {
                    pidls[slot] = pidl;
                    found++;
                }
                else
                {
                    CoTaskMemFree(pidl);
                }
            }

            if (found == 0)
            {
                return 0;
            }

            var foundPidls = new IntPtr[found];
            var foundPoints = new Win32Helper.POINT[found];
            int next = 0;
            for (int i = 0; i < pidls.Length; i++)
            {
                if (pidls[i] != IntPtr.Zero)
                {
                    foundPidls[next] = pidls[i];
                    foundPoints[next] = new Win32Helper.POINT { X = origins[i].X, Y = origins[i].Y };
                    next++;
                }
            }

            fixed (IntPtr* pidlArray = foundPidls)
            fixed (Win32Helper.POINT* pointArray = foundPoints)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, Win32Helper.POINT*, uint, int>)Slot(view, FolderViewSelectAndPositionItemsSlot))(
                    view, (uint)found, pidlArray, pointArray, SvsiPositionItem);
            }

            return hr >= 0 ? found : 0;
        }
        finally
        {
            foreach (IntPtr pidl in pidls)
            {
                if (pidl != IntPtr.Zero)
                {
                    CoTaskMemFree(pidl);
                }
            }

            if (strRet != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(strRet);
            }

            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (folder != IntPtr.Zero)
            {
                Marshal.Release(folder);
            }

            Marshal.Release(view);
        }
    }

    private static string? GetParsingName(IntPtr folder, IntPtr pidl, IntPtr strRet, IntPtr buffer)
    {
        new Span<byte>((void*)strRet, StrRetSize).Clear();
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, int>)Slot(folder, ShellFolderGetDisplayNameOfSlot))(
            folder, pidl, ShgdnForParsing, strRet);
        if (hr < 0)
        {
            return null;
        }

        // StrRetToBuf frees an STRRET_WSTR allocation itself.
        return StrRetToBuf(strRet, pidl, buffer, PathBufferChars) >= 0
            ? Marshal.PtrToStringUni(buffer)
            : null;
    }

    private static IntPtr AcquireDesktopFolderView()
    {
        IntPtr shellWindows = IntPtr.Zero;
        IntPtr dispatch = IntPtr.Zero;
        IntPtr serviceProvider = IntPtr.Zero;
        IntPtr browser = IntPtr.Zero;
        IntPtr shellView = IntPtr.Zero;
        try
        {
            Guid clsid = ClsidShellWindows;
            Guid iid = IidShellWindows;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxLocalServer | ClsctxInprocServer, ref iid, out shellWindows) < 0 ||
                shellWindows == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // VARIANT is 16 bytes on 32-bit and 24 bytes on 64-bit; 24 covers both.
            byte* location = stackalloc byte[24];
            byte* empty = stackalloc byte[24];
            new Span<byte>(location, 24).Clear();
            new Span<byte>(empty, 24).Clear();
            *(ushort*)location = VtI4;
            *(int*)(location + 8) = CsidlDesktop;
            int hwnd = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, int, int*, int, IntPtr*, int>)Slot(shellWindows, ShellWindowsFindWindowSwSlot))(
                shellWindows, location, empty, SwcDesktop, &hwnd, SwfoNeedDispatch, &dispatch);
            if (hr != 0 || dispatch == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Guid iidServiceProvider = IidServiceProvider;
            if (ComVtable.QueryInterface(dispatch, ref iidServiceProvider, out serviceProvider) < 0 || serviceProvider == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Guid sid = SidTopLevelBrowser;
            Guid iidBrowser = IidShellBrowser;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(serviceProvider, ServiceProviderQueryServiceSlot))(
                serviceProvider, &sid, &iidBrowser, &browser);
            if (hr < 0 || browser == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(browser, ShellBrowserQueryActiveShellViewSlot))(browser, &shellView);
            if (hr < 0 || shellView == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Guid iidFolderView = IidFolderView;
            return ComVtable.QueryInterface(shellView, ref iidFolderView, out IntPtr folderView) >= 0 ? folderView : IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        finally
        {
            foreach (IntPtr unknown in new[] { shellView, browser, serviceProvider, dispatch, shellWindows })
            {
                if (unknown != IntPtr.Zero)
                {
                    Marshal.Release(unknown);
                }
            }
        }
    }

    private static IntPtr Slot(IntPtr comObject, int slot) =>
        ((IntPtr*)*(IntPtr*)comObject)[slot];

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return path;
        }
    }
}
