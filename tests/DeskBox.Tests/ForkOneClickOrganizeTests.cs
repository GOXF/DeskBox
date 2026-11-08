using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>GOXF fork: one-click 「整理桌面」 and the duplicate 「其他」 fix.</summary>
public sealed class ForkOneClickOrganizeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "deskbox-fork-one-click-tests");
    private static readonly string Desktop = Path.Combine(Root, "Desktop");

    private static string D(string name) => Path.Combine(Desktop, name);

    private static DesktopOrganizationFileSnapshot Item(
        string name,
        string categoryId,
        DesktopOrganizationExclusionReason reason = DesktopOrganizationExclusionReason.None,
        bool isDirectory = false) =>
        new(D(name), name, DesktopOrganizationClassifier.NormalizeExtension(Path.GetExtension(name)), 1,
            DateTime.UtcNow, categoryId, null, reason, isDirectory);

    private static WidgetConfig Folder(string id, string name) =>
        new() { Id = id, Name = name, WidgetKind = WidgetKind.File, MappedFolderPath = Path.Combine(Root, "storage", id) };

    private static WidgetConfig Mapped(string id, string name, string categoryId)
    {
        var widget = new WidgetConfig { Id = id, Name = name, WidgetKind = WidgetKind.File, MappedFolderPath = Desktop };
        MappedOrganizeWidgetMetadata.Mark(widget, categoryId);
        return widget;
    }

    private static string Name(string categoryId) => categoryId switch
    {
        DesktopOrganizationCategoryIds.Other => "其他",
        DesktopOrganizationCategoryIds.Images => "图片",
        _ => categoryId,
    };

    private static DesktopOrganizationPlan Plan(
        IReadOnlyCollection<WidgetConfig> widgets,
        IReadOnlyCollection<DesktopOrganizationRule> rules,
        params DesktopOrganizationFileSnapshot[] items) =>
        new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver()).CreatePlan(
            new DesktopOrganizationScanResult { DesktopPath = Desktop, Items = items.ToList() },
            Path.Combine(Root, "storage"),
            widgets,
            rules,
            Name);

    [Fact]
    public void Planner_LoneItemMergedIntoOther_JoinsTheExistingOtherWidget()
    {
        // The reported case: an earlier organize created 「其他」 with a rule for
        // [Other]; folders match it, a single video (Media) is merged into the
        // 「其他」 bucket and used to produce a second, new 「其他」 card.
        WidgetConfig other = Folder("other", "其他");
        var rule = new DesktopOrganizationRule { TargetWidgetId = "other", CategoryIds = [DesktopOrganizationCategoryIds.Other] };

        DesktopOrganizationPlan plan = Plan([other], [rule],
            Item("HarkHubGo-2.0.41.x", DesktopOrganizationCategoryIds.Other),
            Item("notes.xyz", DesktopOrganizationCategoryIds.Other),
            Item("video_2026-10-21.mp4", DesktopOrganizationCategoryIds.Media));

        DesktopOrganizationTargetPlan target = Assert.Single(plan.Targets);
        Assert.False(target.CreatesWidget);
        Assert.Equal("other", target.TargetWidgetId);
        Assert.Equal(3, target.Items.Count);
    }

    [Fact]
    public void Planner_OtherBucketWithoutRuleMatches_StillReusesTheRuleWidget()
    {
        WidgetConfig other = Folder("other", "Misc");
        var rule = new DesktopOrganizationRule { TargetWidgetId = "other", CategoryIds = [DesktopOrganizationCategoryIds.Other] };

        DesktopOrganizationPlan plan = Plan([other], [rule], Item("clip.mp4", DesktopOrganizationCategoryIds.Media));

        DesktopOrganizationTargetPlan target = Assert.Single(plan.Targets);
        Assert.False(target.CreatesWidget);
        Assert.Equal("other", target.TargetWidgetId);
    }

    [Fact]
    public void Planner_SameNamedWidget_IsReusedInsteadOfASecondOne()
    {
        WidgetConfig images = Folder("img", "图片");

        DesktopOrganizationPlan plan = Plan([images], [],
            Item("a.png", DesktopOrganizationCategoryIds.Images),
            Item("b.png", DesktopOrganizationCategoryIds.Images));

        DesktopOrganizationTargetPlan target = Assert.Single(plan.Targets);
        Assert.Equal("img", target.TargetWidgetId);
        Assert.False(target.CreatesWidget);
    }

    [Fact]
    public void CategoryReuse_RespectsExcludedExtensionsDisabledAndMappedWidgets()
    {
        WidgetConfig other = Folder("other", "Misc");
        var rule = new DesktopOrganizationRule
        {
            TargetWidgetId = "other",
            CategoryIds = [DesktopOrganizationCategoryIds.Other],
            ExcludedExtensions = [".mp4"]
        };
        Assert.Null(DesktopOrganizationCategoryReuse.FindExistingWidget(
            DesktopOrganizationCategoryIds.Other, "其他", [".mp4"], [other], [rule]));
        Assert.Same(other, DesktopOrganizationCategoryReuse.FindExistingWidget(
            DesktopOrganizationCategoryIds.Other, "其他", [".xyz"], [other], [rule]));

        WidgetConfig disabled = Folder("off", "其他");
        disabled.IsDisabled = true;
        Assert.Null(DesktopOrganizationCategoryReuse.FindExistingWidget(
            DesktopOrganizationCategoryIds.Other, "其他", [], [disabled], []));

        // A mapped widget can't receive moved files.
        Assert.Null(DesktopOrganizationCategoryReuse.FindExistingWidget(
            DesktopOrganizationCategoryIds.Other, "其他", [], [Mapped("m", "其他", DesktopOrganizationCategoryIds.Other)], []));
        Assert.Null(DesktopOrganizationCategoryReuse.FindExistingWidget(
            DesktopOrganizationCategoryIds.Other, "其他", [], [], []));
    }

    [Fact]
    public void BuildMappedGroups_UsesOwnCategoryWidgetElseBucket_OneGroupPerCategory()
    {
        var targets = new List<DesktopOrganizationTargetPlan>
        {
            new()
            {
                SourceBucketId = "category:Other",
                CategoryId = DesktopOrganizationCategoryIds.Other,
                Items =
                [
                    Item("lone.png", DesktopOrganizationCategoryIds.Images),   // 图片 is mapped → joins it
                    Item("clip.mp4", DesktopOrganizationCategoryIds.Media),    // no mapped 媒体 → stays in 其他
                    Item("Folder", DesktopOrganizationCategoryIds.Other, DesktopOrganizationExclusionReason.None, isDirectory: true),
                ]
            },
            new()
            {
                SourceBucketId = "existing-real-other",
                CategoryId = DesktopOrganizationCategoryIds.Other,
                Items = [Item("x.dat", DesktopOrganizationCategoryIds.Other), Item("clip.mp4", DesktopOrganizationCategoryIds.Media)]
            },
        };

        IReadOnlyList<MappedOrganizeGroup> groups = ForkOneClickOrganizePolicy.BuildMappedGroups(
            targets, [DesktopOrganizationCategoryIds.Images], Name);

        Assert.Equal(2, groups.Count);
        MappedOrganizeGroup other = Assert.Single(groups, group => group.CategoryId == DesktopOrganizationCategoryIds.Other);
        Assert.Equal("其他", other.DisplayName);
        Assert.Equal(new[] { D("clip.mp4"), D("Folder"), D("x.dat") }, other.Paths.ToArray());
        MappedOrganizeGroup images = Assert.Single(groups, group => group.CategoryId == DesktopOrganizationCategoryIds.Images);
        Assert.Equal(new[] { D("lone.png") }, images.Paths.ToArray());
        Assert.Empty(ForkOneClickOrganizePolicy.BuildMappedGroups(null, null, Name));
    }

    [Fact]
    public void FindMappedTarget_ByCategoryThenByName()
    {
        WidgetConfig images = Mapped("img", "图片", DesktopOrganizationCategoryIds.Images);
        WidgetConfig renamedOther = Mapped("oth", " 其他 ", "Custom");

        Assert.Same(images, ForkOneClickOrganizePolicy.FindMappedTarget([renamedOther, images], DesktopOrganizationCategoryIds.Images, "Pictures"));
        Assert.Same(renamedOther, ForkOneClickOrganizePolicy.FindMappedTarget([images, renamedOther], DesktopOrganizationCategoryIds.Other, "其他"));
        Assert.Null(ForkOneClickOrganizePolicy.FindMappedTarget([images], DesktopOrganizationCategoryIds.Documents, "文档"));
        Assert.Null(ForkOneClickOrganizePolicy.FindMappedTarget(null, DesktopOrganizationCategoryIds.Other, null));
    }

    [Fact]
    public void SelectOptIns_TakesFoldersLargeFilesAndOverflowOnly()
    {
        IReadOnlyList<string> optIns = ForkOneClickOrganizePolicy.SelectOptIns(
        [
            Item("Folder", DesktopOrganizationCategoryIds.Other, DesktopOrganizationExclusionReason.Folder, isDirectory: true),
            Item("big.iso", DesktopOrganizationCategoryIds.Packages, DesktopOrganizationExclusionReason.SlowItem),
            Item("more.png", DesktopOrganizationCategoryIds.Images, DesktopOrganizationExclusionReason.BatchLimit),
            Item("hidden.txt", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationExclusionReason.HiddenOrSystem),
            Item("ok.png", DesktopOrganizationCategoryIds.Images),
        ]);

        Assert.Equal(new[] { D("Folder"), D("big.iso"), D("more.png") }, optIns.ToArray());
        Assert.Empty(ForkOneClickOrganizePolicy.SelectOptIns(null));
    }

    [Fact]
    public void Wiring_EntriesRunOneClickAndThePreviewStaysReachable()
    {
        string tray = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.Tray.cs"));
        Assert.Contains("OrganizeDesktopOneClick();", tray, StringComparison.Ordinal);
        Assert.Contains("CreateForkOrganizePreviewTrayItem(contextMenu)", tray, StringComparison.Ordinal);

        string settingsActions = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/InProcessSettingsAppActions.cs"));
        Assert.Contains("_app.OrganizeDesktopOneClick(", settingsActions, StringComparison.Ordinal);

        string blankMenu = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.MappedOrganize.cs"));
        Assert.Contains("RunOneClickOrganize();", blankMenu, StringComparison.Ordinal);
        Assert.Contains("CreateOrganizePreviewMenuItem(flyout)", blankMenu, StringComparison.Ordinal);

        string titleMenu = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.MappedOrganize.cs"));
        Assert.Contains("FileSurfaceContent.RunOneClickOrganize();", titleMenu, StringComparison.Ordinal);
        Assert.Contains("FileSurfaceContent.CreateOrganizePreviewMenuItem(flyout)", titleMenu, StringComparison.Ordinal);

        string planner = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/DesktopOrganizationPlanner.cs"));
        Assert.Contains("DesktopOrganizationCategoryReuse.FindExistingWidget(", planner, StringComparison.Ordinal);

        Assert.Equal("一键整理桌面", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.MenuReorganizeDesktop));
        Assert.Equal("整理预览…", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.MenuOrganizePreview));
    }
}
