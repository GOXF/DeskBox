using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Tests;

public sealed class MappedDesktopShellViewTests
{
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid BhidShellFolder = new("3981E224-F559-11D3-8E3A-00C04F6837D5");
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x80;
    private const uint SvgioAllView = 2;
    private const uint SigdnFileSystemPath = 0x80058000;

    [Fact]
    public void NativeFilter_HidesOnlyMappedFixture_RefreshesAndDisposesOnItsSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunFixture();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "MappedDesktopShellView fixture STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The hidden-host ExplorerBrowser fixture timed out.");
        Assert.True(failure is null, failure?.ToString());
    }

    [Fact]
    public void ExcludedPaths_AreNormalizedAndMatchedExactly()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox-MappedDesktopShellView", Guid.NewGuid().ToString("N"));
        string mapped = Path.Combine(root, "mapped.txt");
        string sibling = Path.Combine(root, "mapped.txt.backup");
        HashSet<string> excluded = MappedDesktopShellView.NormalizeExcludedPaths([mapped]);

        Assert.True(MappedDesktopShellView.IsExcludedPath(Path.Combine(root, "sub", "..", "MAPPED.txt"), excluded));
        Assert.False(MappedDesktopShellView.IsExcludedPath(sibling, excluded));
        Assert.Throws<ArgumentException>(() => MappedDesktopShellView.NormalizeExcludedPaths(["mapped.txt"]));
    }

    private static void RunFixture()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "DeskBox-MappedDesktopShellView-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string mappedPath = Path.Combine(fixture, "mapped-fixture.txt");
        string unmappedPath = Path.Combine(fixture, "unmapped-fixture.txt");
        File.WriteAllText(mappedPath, "mapped fixture");
        File.WriteAllText(unmappedPath, "unmapped fixture");
        FileAttributes mappedAttributes = File.GetAttributes(mappedPath);
        FileAttributes unmappedAttributes = File.GetAttributes(unmappedPath);

        IntPtr host = IntPtr.Zero;
        IntPtr sourceFolder = IntPtr.Zero;
        MappedDesktopShellView? view = null;
        try
        {
            host = CreateWindowExW(
                WsExToolWindow,
                "STATIC",
                "DeskBox native filter fixture",
                WsPopup,
                0,
                0,
                800,
                600,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandleW(null),
                IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, host);

            sourceFolder = GetShellFolder(fixture);
            var bounds = new Win32Helper.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 };
            Assert.True(
                MappedDesktopShellView.TryCreateForFolder(host, bounds, [mappedPath], sourceFolder, out view),
                "ExplorerBrowser fixture creation failed; the test must not silently skip native filtering.");
            Assert.NotNull(view);

            // The view owns its own reference; dropping this borrowed test reference catches lifetime mistakes.
            Marshal.Release(sourceFolder);
            sourceFolder = IntPtr.Zero;

            var initiallyVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(unmappedPath) };
            Assert.True(
                WaitForVisibleItems(view!, initiallyVisible, TimeSpan.FromSeconds(15), out HashSet<string>? initialItems),
                "The populated fixture view did not contain only the unmapped file.");
            Assert.True(view!.IsReady);
            Assert.NotEqual(IntPtr.Zero, view.ViewWindow);
            Assert.True(initiallyVisible.SetEquals(initialItems!));
            Assert.Equal(mappedAttributes, File.GetAttributes(mappedPath));
            Assert.Equal(unmappedAttributes, File.GetAttributes(unmappedPath));

            Assert.True(view.UpdateExcludedPaths(Array.Empty<string>()), "Clearing the filter and refreshing failed.");
            var bothVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileName(mappedPath),
                Path.GetFileName(unmappedPath)
            };
            Assert.True(
                WaitForVisibleItems(view, bothVisible, TimeSpan.FromSeconds(15), out HashSet<string>? updatedItems),
                "After removing the exclusion, both fixture files were not visible.");
            Assert.True(bothVisible.SetEquals(updatedItems!));
            Assert.Equal(mappedAttributes, File.GetAttributes(mappedPath));
            Assert.Equal(unmappedAttributes, File.GetAttributes(unmappedPath));

            IntPtr positionedView = view.AcquireFolderView();
            Assert.NotEqual(IntPtr.Zero, positionedView);
            try
            {
                Assert.Equal(2, DesktopIconPlacementNativeMethods.CapturePositions(positionedView).Count);
                Assert.Equal(1, DesktopIconPlacementNativeMethods.TryPositionItems(
                    [unmappedPath], [(120, 140)], positionedView));
                var positions = DesktopIconPlacementNativeMethods.CapturePositions(positionedView);
                Assert.Equal((120, 140), positions[Path.GetFullPath(unmappedPath)]);
                Assert.True(DesktopIconPlacementNativeMethods.TryGetViewInfo(out var info, positionedView));
                Assert.True(DesktopIconPlacementNativeMethods.TryApplyViewInfo(info with { IconSize = 64, AutoArrange = true }, positionedView));
                Assert.True(DesktopIconPlacementNativeMethods.TryGetViewInfo(out var changedInfo, positionedView));
                Assert.Equal(64, changedInfo.IconSize);
                Assert.True(changedInfo.AutoArrange);
                Assert.True(DesktopIconPlacementNativeMethods.TryApplyViewInfo(info, positionedView));
            }
            finally
            {
                Marshal.Release(positionedView);
            }

            view.Dispose();
            view.Dispose();
            Assert.False(view.IsReady);
            Assert.Equal(mappedAttributes, File.GetAttributes(mappedPath));
            Assert.Equal(unmappedAttributes, File.GetAttributes(unmappedPath));
        }
        finally
        {
            view?.Dispose();
            if (sourceFolder != IntPtr.Zero)
            {
                Marshal.Release(sourceFolder);
            }

            if (host != IntPtr.Zero)
            {
                DestroyWindow(host);
            }

            Directory.Delete(fixture, recursive: true);
        }
    }

    private static bool WaitForVisibleItems(
        MappedDesktopShellView view,
        HashSet<string> expected,
        TimeSpan timeout,
        out HashSet<string>? actual)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            PumpMessages();
            actual = TryReadVisibleItemNames(view);
            if (actual is not null && actual.SetEquals(expected))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        actual = TryReadVisibleItemNames(view);
        return actual is not null && actual.SetEquals(expected);
    }

    private static HashSet<string>? TryReadVisibleItemNames(MappedDesktopShellView view)
    {
        IntPtr folderView = view.AcquireFolderView();
        if (folderView == IntPtr.Zero)
        {
            return null;
        }

        IntPtr shellFolder = IntPtr.Zero;
        try
        {
            int countHr = ComVtable.Slot<ItemCountInvoker>(folderView, 7)(folderView, SvgioAllView, out int count);
            if (countHr < 0 || count < 0)
            {
                return null;
            }

            Guid iid = IidShellFolder;
            if (ComVtable.Slot<GetFolderInvoker>(folderView, 5)(folderView, ref iid, out shellFolder) < 0 || shellFolder == IntPtr.Zero)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < count; index++)
            {
                IntPtr childPidl = IntPtr.Zero;
                IntPtr shellItem = IntPtr.Zero;
                IntPtr displayName = IntPtr.Zero;
                try
                {
                    if (ComVtable.Slot<GetItemInvoker>(folderView, 6)(folderView, index, out childPidl) < 0 || childPidl == IntPtr.Zero)
                    {
                        return null;
                    }

                    Guid itemIid = IidShellItem;
                    if (SHCreateItemWithParent(IntPtr.Zero, shellFolder, childPidl, ref itemIid, out shellItem) < 0 || shellItem == IntPtr.Zero)
                    {
                        return null;
                    }

                    if (ComVtable.Slot<GetDisplayNameInvoker>(shellItem, 5)(shellItem, SigdnFileSystemPath, out displayName) < 0 || displayName == IntPtr.Zero)
                    {
                        return null;
                    }

                    string? path = Marshal.PtrToStringUni(displayName);
                    if (path is null)
                    {
                        return null;
                    }

                    names.Add(Path.GetFileName(path));
                }
                finally
                {
                    if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName);
                    if (shellItem != IntPtr.Zero) Marshal.Release(shellItem);
                    if (childPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidl);
                }
            }

            return names;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (shellFolder != IntPtr.Zero) Marshal.Release(shellFolder);
            Marshal.Release(folderView);
        }
    }

    private static IntPtr GetShellFolder(string path)
    {
        Guid itemIid = IidShellItem;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemIid, out IntPtr shellItem);
        Assert.True(hr >= 0 && shellItem != IntPtr.Zero, $"SHCreateItemFromParsingName failed: 0x{hr:X8}");

        try
        {
            Guid shellFolderIid = IidShellFolder;
            Guid handler = BhidShellFolder;
            hr = ComVtable.Slot<BindToHandlerInvoker>(shellItem, 3)(shellItem, IntPtr.Zero, ref handler, ref shellFolderIid, out IntPtr shellFolder);
            Assert.True(hr >= 0 && shellFolder != IntPtr.Zero, $"IShellItem.BindToHandler(BHID_SFObject) failed: 0x{hr:X8}");
            return shellFolder;
        }
        finally
        {
            Marshal.Release(shellItem);
        }
    }

    private static void PumpMessages()
    {
        while (PeekMessageW(out NativeMessage message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemCountInvoker(IntPtr self, uint flags, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFolderInvoker(IntPtr self, ref Guid iid, out IntPtr folder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetItemInvoker(IntPtr self, int index, out IntPtr pidl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDisplayNameInvoker(IntPtr self, uint nameType, out IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BindToHandlerInvoker(IntPtr self, IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemWithParent(IntPtr parentPidl, IntPtr parentFolder, IntPtr childPidl, ref Guid iid, out IntPtr item);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
