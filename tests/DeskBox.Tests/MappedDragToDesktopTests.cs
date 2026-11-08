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

    // ---- full verdict with diagnostics reason ----

    private static readonly string[] Dragged = [Path.Combine(Desktop, "report.docx"), Path.Combine(Desktop, "Chrome.lnk")];

    [Theory]
    [InlineData(false, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Blank, null, false, false, false, "not-mapped-widget")]
    [InlineData(true, true, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Blank, null, false, false, false, "handled-internally")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Blank, null, false, true, false, "escape-pressed")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Blank, null, true, false, false, "mouse-button-down")]
    [InlineData(true, false, MappedDragDropTargetKind.NotDesktop, MappedDragIconHit.Blank, null, false, false, false, "not-desktop")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Item, "Projects", false, false, false, "on-other-icon")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Item, null, false, false, false, "on-other-icon")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Blank, null, false, false, true, "blank")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopHost, MappedDragIconHit.Blank, null, false, false, true, "blank")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Failed, null, false, false, true, "hit-test-failed")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Item, "report", false, false, true, "own-icon")]
    [InlineData(true, false, MappedDragDropTargetKind.DesktopIconList, MappedDragIconHit.Item, "Chrome", false, false, true, "own-icon")]
    public void Evaluate_ReportsAReasonForEveryOutcome(
        bool mapped,
        bool handled,
        MappedDragDropTargetKind target,
        MappedDragIconHit hit,
        string? iconText,
        bool buttonDown,
        bool escape,
        bool expectedPutBack,
        string expectedReason)
    {
        MappedDragPutBackDecision decision = MappedDragToDesktopPolicy.Evaluate(
            mapped, handled, target, hit, iconText, Dragged, buttonDown, escape);

        Assert.Equal(expectedPutBack, decision.PutBack);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Theory]
    [InlineData("report.docx", true)]
    [InlineData("REPORT", true)]
    [InlineData(" report ", true)]
    [InlineData("Chrome", true)]
    [InlineData("Chrome.lnk", true)]
    [InlineData("report.pdf", false)]
    [InlineData("Recycle Bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDraggedItemText_MatchesNameWithOrWithoutExtension(string? label, bool expected)
    {
        Assert.Equal(expected, MappedDragToDesktopPolicy.IsDraggedItemText(label, Dragged));
    }

    [Fact]
    public void IsDraggedItemText_HandlesFoldersAndEmptyInput()
    {
        Assert.True(MappedDragToDesktopPolicy.IsDraggedItemText("Folder", [Path.Combine(Desktop, "Folder") + Path.DirectorySeparatorChar]));
        Assert.False(MappedDragToDesktopPolicy.IsDraggedItemText("Folder", null));
        Assert.False(MappedDragToDesktopPolicy.IsDraggedItemText("Folder", ["", "  "]));
    }

    // ---- release tracking ----

    private static readonly TimeSpan T0 = TimeSpan.Zero;
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan T2 = TimeSpan.FromMilliseconds(900);

    [Fact]
    public void ReleaseTracking_ReleasesWhenTheHeldButtonGoesUp()
    {
        var tracking = new MappedDragReleaseTracking();
        Assert.Equal(MappedDragReleaseState.Pending, tracking.Observe(T0, buttonDown: true, escapeDown: false));
        Assert.Equal(MappedDragReleaseState.Pending, tracking.Observe(T1, buttonDown: true, escapeDown: false));
        Assert.Equal(MappedDragReleaseState.Released, tracking.Observe(T2, buttonDown: false, escapeDown: false));
        Assert.True(tracking.SawButtonDown);

        // Final: later samples do not change it.
        Assert.Equal(MappedDragReleaseState.Released, tracking.Observe(T2, buttonDown: true, escapeDown: true));
    }

    [Fact]
    public void ReleaseTracking_EscapeDuringTheDragCancels()
    {
        var tracking = new MappedDragReleaseTracking();
        tracking.Observe(T0, buttonDown: true, escapeDown: false);
        Assert.Equal(MappedDragReleaseState.Cancelled, tracking.Observe(T1, buttonDown: true, escapeDown: true));
    }

    [Fact]
    public void ReleaseTracking_EscapeBeforeAnyButtonIsIgnored()
    {
        var tracking = new MappedDragReleaseTracking();
        Assert.Equal(MappedDragReleaseState.Pending, tracking.Observe(T0, buttonDown: false, escapeDown: true));
        Assert.Equal(MappedDragReleaseState.Pending, tracking.Observe(T1, buttonDown: true, escapeDown: false));
        Assert.Equal(MappedDragReleaseState.Released, tracking.Observe(T2, buttonDown: false, escapeDown: false));
    }

    [Fact]
    public void ReleaseTracking_WithoutAnyButtonGivesUpAfterTheGrace()
    {
        // Touch / pen drags never press a mouse button: completion decides.
        var tracking = new MappedDragReleaseTracking(TimeSpan.FromMilliseconds(400));
        Assert.Equal(MappedDragReleaseState.Pending, tracking.Observe(T1, buttonDown: false, escapeDown: false));
        Assert.Equal(MappedDragReleaseState.Untracked, tracking.Observe(T2, buttonDown: false, escapeDown: false));
        Assert.False(tracking.SawButtonDown);
    }

    // ---- wiring ----

    [Fact]
    public void DragCompletion_RoutesMappedDesktopDropsToPutBack()
    {
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        Assert.Contains(
            "TryPutBackMappedDragOnDesktop(movedPaths, handledAsStackMembership, dragSessionId, dropResult)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "BeginMappedDragTracking(_activeDragSessionId, _activeDragSourcePaths);",
            surface,
            StringComparison.Ordinal);

        string hook = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.MappedDragToDesktop.cs"));
        Assert.Contains("service.KeepOnDesktop(putBack)", hook, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsMappedOrganizeWidget", hook, StringComparison.Ordinal);
        Assert.Contains("DragToDesktop skip reason=", hook, StringComparison.Ordinal);
    }
}
