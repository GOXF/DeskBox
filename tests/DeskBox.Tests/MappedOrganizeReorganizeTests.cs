using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>GOXF fork: 「重新整理桌面」 — 放回桌面 items are re-sorted by the organize preview.</summary>
public sealed class MappedOrganizeReorganizeTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "deskbox-fork-reorganize-tests", "Desktop");

    private static string D(string name) => MappedOrganizeMembership.NormalizePath(Path.Combine(Desktop, name));

    private static WidgetConfig Mapped(string id, string categoryId)
    {
        var widget = new WidgetConfig { Id = id, Name = id, WidgetKind = WidgetKind.File, MappedFolderPath = Desktop };
        MappedOrganizeWidgetMetadata.Mark(widget, categoryId);
        return widget;
    }

    private static WidgetConfig Plain(string id) =>
        new() { Id = id, Name = id, WidgetKind = WidgetKind.File, MappedFolderPath = Path.Combine(Desktop, "..", id) };

    [Fact]
    public void SelectKeptOptIns_OptsInKeptEntriesTheScannerOnlyOffers()
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (string, bool)[] scanned =
        [
            (D("KeptFolder"), true),     // kept folder: opt-in only → tick it
            (D("kept.png"), false),      // kept file: already ticked by the normal rules
            (D("OtherFolder"), true),    // not kept: stays the user's choice
            (D("big.iso"), true),        // kept large file → tick it
        ];

        IReadOnlyList<string> optIns = MappedOrganizeReorganizePolicy.SelectKeptOptIns(
            scanned,
            [D("keptfolder"), D("kept.png"), D("BIG.iso")],
            applied);

        Assert.Equal(new[] { D("KeptFolder"), D("big.iso") }, optIns.ToArray());
    }

    [Fact]
    public void SelectKeptOptIns_AppliesEachPathOncePerPreview()
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (string, bool)[] scanned = [(D("KeptFolder"), true)];

        Assert.Single(MappedOrganizeReorganizePolicy.SelectKeptOptIns(scanned, [D("KeptFolder")], applied));
        // Refresh after the user unticked it: not re-ticked.
        Assert.Empty(MappedOrganizeReorganizePolicy.SelectKeptOptIns(scanned, [D("KeptFolder")], applied));
    }

    [Fact]
    public void KeptOptInHistory_PreservesSessionExclusionAcrossRefreshAndResetsForFreshPreview()
    {
        DesktopOrganizationFileSnapshot folder = new(
            D("KeptFolder"), "KeptFolder", string.Empty, 0, DateTime.UnixEpoch,
            DesktopOrganizationCategoryIds.Other, null, DesktopOrganizationExclusionReason.Folder, IsDirectory: true);
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string> kept = [folder.SourcePath];
        var session = new DesktopOrganizationWorkbenchSession(
            (_, optionalPaths) => BuildPlan(optionalPaths.Contains(folder.SourcePath), folder),
            () => new Dictionary<string, WidgetConfig>(StringComparer.Ordinal));
        session.SetInitialPlan(BuildPlan(false, folder));

        IReadOnlyList<string> initialOptIns = MappedOrganizeReorganizePolicy.SelectKeptOptIns(
            [(folder.SourcePath, folder.CanOptIn)], kept, history);
        Assert.Equal(new[] { folder.SourcePath }, initialOptIns);
        session.IncludeItems(initialOptIns);
        Assert.Contains(session.EffectivePlan!.Targets.SelectMany(target => target.Items), item => item.SourcePath == folder.SourcePath);

        session.ExcludeItems([folder.SourcePath]);
        Assert.Empty(session.OptionalIncludedPaths);

        // A preserving refresh shares the same preview-lifetime history and
        // therefore cannot undo the user's later return-to-tray choice.
        IReadOnlyList<string> refreshOptIns = MappedOrganizeReorganizePolicy.SelectKeptOptIns(
            [(folder.SourcePath, folder.CanOptIn)], kept, history);
        session.IncludeItems(refreshOptIns);
        Assert.Empty(refreshOptIns);
        Assert.Empty(session.OptionalIncludedPaths);
        Assert.DoesNotContain(session.EffectivePlan!.Targets.SelectMany(target => target.Items), item => item.SourcePath == folder.SourcePath);

        history.Clear(); // ResetPreviewState clears this only for a fresh preview.
        Assert.Single(MappedOrganizeReorganizePolicy.SelectKeptOptIns(
            [(folder.SourcePath, folder.CanOptIn)], kept, history));
    }

    [Fact]
    public void SelectKeptOptIns_HandlesEmptyInput()
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(MappedOrganizeReorganizePolicy.SelectKeptOptIns([(D("a"), true)], [], applied));
        Assert.Empty(MappedOrganizeReorganizePolicy.SelectKeptOptIns([], [D("a")], applied));
        Assert.Empty(MappedOrganizeReorganizePolicy.SelectKeptOptIns([("", true), ("  ", true)], [D("a")], applied));
    }

    [Fact]
    public void KeptEntries_SkipAutomaticSorting_ButAConfirmedPreviewClearsTheMark()
    {
        // Automatic sorting / quick 一键整理 keep skipping 放回桌面 entries…
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var widgets = new List<WidgetConfig> { images };
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MappedOrganizeKeepList.Keep(kept, [D("a.png")]);
        IReadOnlyList<string> unassigned = MappedOrganizeMembership.FindUnassigned(widgets, [D("a.png"), D("b.png")]);
        Assert.Equal(new[] { D("b.png") }, MappedOrganizeKeepList.FilterUnassigned(unassigned, kept));

        // …while the preview's confirmed groups release the mark and map them.
        Assert.True(MappedOrganizeKeepList.Release(kept, [D("A.PNG")]));
        MappedOrganizeMembership.Assign(widgets, "images", [D("a.png")]);
        Assert.Empty(kept);
        Assert.Contains(D("a.png"), MappedOrganizeMembership.GetMembers(images));
    }

    [Fact]
    public void ShouldOfferReorganize_ForMappedWidgetsAndTheirTabGroups()
    {
        WidgetConfig mapped = Mapped("m", DesktopOrganizationCategoryIds.Images);
        WidgetConfig tabMate = Plain("tab");
        WidgetConfig loner = Plain("loner");
        var widgets = new List<WidgetConfig> { mapped, tabMate, loner };
        var groups = new List<WidgetGroupConfig>
        {
            new() { Id = "g", MemberIds = ["m", "tab"] },
        };

        Assert.True(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(mapped, widgets, groups));
        Assert.True(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(tabMate, widgets, groups));
        Assert.False(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(loner, widgets, groups));
        Assert.False(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(null, widgets, groups));
        Assert.True(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(mapped, null, null));
        Assert.False(MappedOrganizeReorganizePolicy.ShouldOfferReorganize(tabMate, widgets, null));
    }

    [Fact]
    public void Wiring_MenusAndPreviewHook()
    {
        string preview = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/DesktopOrganizationTaskView.xaml.cs"));
        Assert.Contains("IncludeKeptOnDesktopItems(scanned);", preview, StringComparison.Ordinal);
        Assert.Contains("_keptOnDesktopOptInsApplied.Clear()", preview, StringComparison.Ordinal);
        string mappedPreview = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/DesktopOrganizationTaskView.MappedOrganize.cs"));
        Assert.Contains("_keptOnDesktopOptInsApplied", mappedPreview, StringComparison.Ordinal);

        string titleMenu = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        Assert.Contains("AppendMappedOrganizeMoreMenuItems(flyout);", titleMenu, StringComparison.Ordinal);

        // 「重新整理桌面」 became the one-click 「一键整理桌面」 (ForkOneClickOrganizeTests).
        string titleMenuFork = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.MappedOrganize.cs"));
        Assert.Contains("ForkOrganizeModeText.Key.MenuReorganizeDesktop", titleMenuFork, StringComparison.Ordinal);

        Assert.Equal("一键整理桌面", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.MenuReorganizeDesktop));
        Assert.Equal("Organize desktop", ForkOrganizeModeText.Get("en-US", ForkOrganizeModeText.Key.MenuReorganizeDesktop));
    }

    private static DesktopOrganizationPlan BuildPlan(bool included, DesktopOrganizationFileSnapshot item)
    {
        DesktopOrganizationFileSnapshot effective = included
            ? item with { ExclusionReason = DesktopOrganizationExclusionReason.None }
            : item;
        return new DesktopOrganizationPlan
        {
            SourceItems = [item],
            Targets = included
                ? [new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "other",
                    CategoryId = DesktopOrganizationCategoryIds.Other,
                    TargetWidgetId = "other",
                    SuggestedDisplayName = "Other",
                    CreatesWidget = true,
                    Items = [effective]
                }]
                : [],
            ExcludedItems = included ? [] : [effective]
        };
    }
}
