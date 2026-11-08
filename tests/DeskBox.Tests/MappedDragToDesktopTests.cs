using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>GOXF fork: 「从映射格子拖到桌面 = 放回桌面」 pure decisions.</summary>
public sealed class MappedDragToDesktopTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "deskbox-fork-drag-tests", "Desktop");
    private static readonly string Elsewhere = Path.Combine(Path.GetTempPath(), "deskbox-fork-drag-tests", "Docs");

    private static string D(string name) => MappedOrganizeMembership.NormalizePath(Path.Combine(Desktop, name));

    private static bool IsDesktopEntry(string path) =>
        string.Equals(Path.GetDirectoryName(MappedOrganizeMembership.NormalizePath(path)), MappedOrganizeMembership.NormalizePath(Desktop), StringComparison.OrdinalIgnoreCase);

    // ---- target window class chain ----

    [Theory]
    [InlineData(MappedDragDropTargetKind.DesktopIconList, "SysListView32>SHELLDLL_DefView>Progman")]
    [InlineData(MappedDragDropTargetKind.DesktopIconList, "SysListView32>SHELLDLL_DefView>WorkerW")]
    [InlineData(MappedDragDropTargetKind.DesktopIconList, "SHELLDLL_DefView>Progman")]
    [InlineData(MappedDragDropTargetKind.DesktopIconList, "syslistview32>shelldll_defview>workerw")]
    [InlineData(MappedDragDropTargetKind.DesktopHost, "WorkerW")]
    [InlineData(MappedDragDropTargetKind.DesktopHost, "Progman")]
    public void ClassifyTarget_AcceptsShellDesktop(MappedDragDropTargetKind expected, string chain)
    {
        Assert.Equal(expected, MappedDragToDesktopPolicy.ClassifyTarget(chain.Split('>'), ownedByDeskBox: false, ownedByShell: true));
    }

    [Theory]
    [InlineData("SysListView32>SHELLDLL_DefView>DUIViewWndClassName>CabinetWClass")] // Explorer folder window
    [InlineData("SysListView32")] // third-party list view
    [InlineData("Shell_TrayWnd")]
    [InlineData("MSTaskSwWClass>ReBarWindow32>Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("WinUIDesktopWin32WindowClass")]
    [InlineData("")]
    public void ClassifyTarget_RejectsOtherWindows(string chain)
    {
        Assert.Equal(
            MappedDragDropTargetKind.NotDesktop,
            MappedDragToDesktopPolicy.ClassifyTarget(chain.Split('>'), ownedByDeskBox: false, ownedByShell: true));
    }

    [Fact]
    public void ClassifyTarget_RejectsDeskBoxWindowsBeddedIntoWorkerW()
    {
        Assert.Equal(
            MappedDragDropTargetKind.NotDesktop,
            MappedDragToDesktopPolicy.ClassifyTarget(new[] { "WinUIDesktopWin32WindowClass", "WorkerW" }, ownedByDeskBox: true, ownedByShell: true));
    }

    [Fact]
    public void ClassifyTarget_RejectsWindowsOutsideTheShellProcess()
    {
        Assert.Equal(
            MappedDragDropTargetKind.NotDesktop,
            MappedDragToDesktopPolicy.ClassifyTarget(new[] { "SysListView32", "SHELLDLL_DefView", "WorkerW" }, ownedByDeskBox: false, ownedByShell: false));
    }

    [Fact]
    public void ClassifyTarget_RejectsEmptyChain()
    {
        Assert.Equal(MappedDragDropTargetKind.NotDesktop, MappedDragToDesktopPolicy.ClassifyTarget(Array.Empty<string>(), false, true));
    }

    // ---- when a drop becomes 「放回桌面」 ----

    [Fact]
    public void ShouldPutBack_OnlyForMappedWidgetDropsOnBlankDesktop()
    {
        Assert.True(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.DesktopIconList, false));
        Assert.True(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.DesktopHost, false));

        // Move-mode widget keeps the upstream drag-out.
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(false, false, MappedDragDropTargetKind.DesktopIconList, false));
        // DeskBox handled the drop (another widget / reorder).
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(true, true, MappedDragDropTargetKind.DesktopIconList, false));
        // Another folder / app.
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.NotDesktop, false));
        // On a desktop icon (folder, Recycle Bin): real move, upstream path.
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.DesktopIconList, true));
    }

    [Fact]
    public void ShouldPutBack_IgnoresCancelledOrUnfinishedDrags()
    {
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.DesktopIconList, false, mouseButtonDown: true));
        Assert.False(MappedDragToDesktopPolicy.ShouldPutBack(true, false, MappedDragDropTargetKind.DesktopIconList, false, escapePressed: true));
    }

    // ---- which paths are put back ----

    [Fact]
    public void ComputePutBackPaths_KeepsExistingDesktopEntriesOnceInOrder()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { D("a.txt"), D("Folder"), D("c.lnk") };
        string[] dragged =
        [
            Path.Combine(Desktop, "a.txt"),
            Path.Combine(Desktop, "Folder") + Path.DirectorySeparatorChar,
            Path.Combine(Desktop, "A.TXT"),
            Path.Combine(Desktop, "gone.txt"),
            Path.Combine(Elsewhere, "c.lnk"),
            Path.Combine(Desktop, "c.lnk"),
            "",
            "   ",
        ];

        IReadOnlyList<string> result = MappedDragToDesktopPolicy.ComputePutBackPaths(dragged, existing.Contains, IsDesktopEntry);

        Assert.Equal(new[] { D("a.txt"), D("Folder"), D("c.lnk") }, result.ToArray());
    }

    [Fact]
    public void ComputePutBackPaths_HandlesEmptyInput()
    {
        Assert.Empty(MappedDragToDesktopPolicy.ComputePutBackPaths(null, _ => true, _ => true));
        Assert.Empty(MappedDragToDesktopPolicy.ComputePutBackPaths(Array.Empty<string>(), _ => true, _ => true));
    }

    // ---- icon positions ----

    [Fact]
    public void ComputeIconOrigins_CentresFirstIconAndStacksTheRestInAColumn()
    {
        IReadOnlyList<(int X, int Y)> origins = MappedDragToDesktopPolicy.ComputeIconOrigins(500, 300, 80, 100, 3, 0, 1040);

        Assert.Equal(new (int X, int Y)[] { (460, 250), (460, 350), (460, 450) }, origins.ToArray());
    }

    [Fact]
    public void ComputeIconOrigins_WrapsIntoNextColumnAtWorkAreaBottom()
    {
        // Rows that fit below y0=850 within bottom 1040: (1040-850)/100 = 1.
        IReadOnlyList<(int X, int Y)> origins = MappedDragToDesktopPolicy.ComputeIconOrigins(500, 900, 80, 100, 3, 0, 1040);

        Assert.Equal(new (int X, int Y)[] { (460, 850), (540, 850), (620, 850) }, origins.ToArray());
    }

    [Fact]
    public void ComputeIconOrigins_ClampsIntoWorkAreaAndDefaultsSpacing()
    {
        IReadOnlyList<(int X, int Y)> origins = MappedDragToDesktopPolicy.ComputeIconOrigins(10, 1030, 0, 0, 1, 0, 1040);

        Assert.Equal(new (int X, int Y)[] { (10 - 37, 940) }, origins.ToArray());
        Assert.Empty(MappedDragToDesktopPolicy.ComputeIconOrigins(0, 0, 80, 100, 0, 0, 1040));
    }

    [Fact]
    public void ComputeIconOrigins_WithoutWorkAreaKeepsOneColumn()
    {
        IReadOnlyList<(int X, int Y)> origins = MappedDragToDesktopPolicy.ComputeIconOrigins(100, 100, 80, 100, 2, 0, 0);

        Assert.Equal(new (int X, int Y)[] { (60, 50), (60, 150) }, origins.ToArray());
    }

    // ---- wiring ----

    [Fact]
    public void DragCompletion_RoutesMappedDesktopDropsToPutBack()
    {
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        Assert.Contains("TryPutBackMappedDragOnDesktop(movedPaths, handledAsStackMembership)", surface, StringComparison.Ordinal);

        string hook = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.MappedDragToDesktop.cs"));
        Assert.Contains("service.KeepOnDesktop(paths)", hook, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsMappedOrganizeWidget", hook, StringComparison.Ordinal);
    }
}
