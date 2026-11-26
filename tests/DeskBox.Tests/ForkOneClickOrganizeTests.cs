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
    public void MappedPreviewProjection_ManualExclusionSurvivesAutoAssignment()
    {
        DesktopOrganizationFileSnapshot excluded = Item("a.png", DesktopOrganizationCategoryIds.Images);
        DesktopOrganizationFileSnapshot selected = Item("b.png", DesktopOrganizationCategoryIds.Images);
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = true,
            SourceItems = [excluded, selected],
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "images",
                    CategoryId = DesktopOrganizationCategoryIds.Images,
                    SuggestedDisplayName = "Pictures",
                    TargetWidgetId = "images",
                    Items = [excluded, selected]
                }
            ]
        };
        var session = new DesktopOrganizationWorkbenchSession(
            (basePlan, _) => basePlan,
            () => new Dictionary<string, WidgetConfig>(StringComparer.Ordinal));
        session.SetInitialPlan(plan);
        session.ExcludeItems([excluded.SourcePath]);

        MappedOrganizePreviewProjection projection = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            session.EffectivePlan!,
            session.TargetSelections,
            session.ExcludedSourcePaths,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Name);

        Assert.Equal(new[] { selected.SourcePath }, Assert.Single(projection.Groups).Paths);
        Assert.Equal(new[] { excluded.SourcePath }, projection.KeepOnDesktop);

        // Mirror ApplyGroupsAsync: assign confirmed group paths, persist explicit
        // keep paths, then let automatic routing inspect what remains unassigned.
        var mapped = Mapped("mapped-images", "Pictures", DesktopOrganizationCategoryIds.Images);
        var widgets = new List<WidgetConfig> { mapped };
        MappedOrganizeMembership.Assign(widgets, mapped.Id, projection.Groups.SelectMany(group => group.Paths));
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MappedOrganizeKeepList.Keep(kept, projection.KeepOnDesktop);
        IReadOnlyList<string> autoAssigned = MappedOrganizeKeepList.FilterUnassigned(
            MappedOrganizeMembership.FindUnassigned(widgets, [excluded.SourcePath, selected.SourcePath]),
            kept);

        Assert.Empty(autoAssigned);
    }

    [Fact]
    public void MappedPreviewProjection_OptedInKeptFolderMapsUnlessUserReturnsItToTray()
    {
        DesktopOrganizationFileSnapshot folder = Item("KeptFolder", DesktopOrganizationCategoryIds.Other,
            DesktopOrganizationExclusionReason.Folder, isDirectory: true);
        DesktopOrganizationFileSnapshot included = folder with { ExclusionReason = DesktopOrganizationExclusionReason.None };
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = true,
            SourceItems = [folder],
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "other",
                    CategoryId = DesktopOrganizationCategoryIds.Other,
                    SuggestedDisplayName = "Other",
                    Items = [included]
                }
            ]
        };

        MappedOrganizePreviewProjection includedProjection = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            plan,
            new Dictionary<string, DesktopOrganizationTargetSelection>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([folder.SourcePath], StringComparer.OrdinalIgnoreCase),
            Name);
        Assert.Equal(new[] { folder.SourcePath }, Assert.Single(includedProjection.Groups).Paths);
        Assert.Empty(includedProjection.KeepOnDesktop);

        var returnedToTray = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = true,
            SourceItems = [folder],
            ExcludedItems = [folder],
            Targets = []
        };
        MappedOrganizePreviewProjection returnedProjection = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            returnedToTray,
            new Dictionary<string, DesktopOrganizationTargetSelection>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([folder.SourcePath], StringComparer.OrdinalIgnoreCase),
            Name);
        Assert.Empty(returnedProjection.Groups);
        Assert.Equal(new[] { folder.SourcePath }, returnedProjection.KeepOnDesktop);
    }

    [Fact]
    public void MappedPreviewProjection_SkipsOutOfScopePathsWithoutPersistingThemAsKept()
    {
        DesktopOrganizationFileSnapshot personal = Item("personal.png", DesktopOrganizationCategoryIds.Images);
        DesktopOrganizationFileSnapshot shared = Item("shared.png", DesktopOrganizationCategoryIds.Images) with
        {
            SourceScope = DesktopOrganizationSourceScope.Public
        };
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = true,
            IncludePublicDesktop = false,
            SourceItems = [personal, shared],
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "images",
                    CategoryId = DesktopOrganizationCategoryIds.Images,
                    SuggestedDisplayName = "Pictures",
                    Items = [personal, shared]
                }
            ]
        };

        MappedOrganizePreviewProjection projection = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            plan,
            new Dictionary<string, DesktopOrganizationTargetSelection>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Name);

        Assert.Equal(new[] { personal.SourcePath }, Assert.Single(projection.Groups).Paths);
        Assert.Empty(projection.KeepOnDesktop);
        Assert.Equal(new[] { shared.SourcePath }, projection.SkipAutoAssignPaths);
    }

    [Fact]
    public void MappedPreviewProjection_PreservesCustomBoxIdentityAndEditedNames()
    {
        DesktopOrganizationFileSnapshot work = Item("work.txt", DesktopOrganizationCategoryIds.Documents);
        DesktopOrganizationFileSnapshot personal = Item("personal.txt", DesktopOrganizationCategoryIds.Documents);
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = true,
            SourceItems = [work, personal],
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "custom-work",
                    CategoryId = string.Empty,
                    TargetWidgetId = "custom-work",
                    SuggestedDisplayName = "Work renamed",
                    Items = [work]
                },
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "custom-personal",
                    CategoryId = string.Empty,
                    TargetWidgetId = "custom-personal",
                    SuggestedDisplayName = "Personal renamed",
                    Items = [personal]
                }
            ]
        };

        IReadOnlyList<MappedOrganizeGroup> groups = ForkOneClickOrganizePolicy.BuildMappedPreviewProjection(
            plan,
            new Dictionary<string, DesktopOrganizationTargetSelection>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Name).Groups;

        Assert.Equal(2, groups.Count);
        Assert.Equal("custom:custom-work", groups[0].CategoryId);
        Assert.Equal("Work renamed", groups[0].DisplayName);
        Assert.Equal(new[] { work.SourcePath }, groups[0].Paths);
        Assert.Equal("custom:custom-personal", groups[1].CategoryId);
        Assert.Equal("Personal renamed", groups[1].DisplayName);
        Assert.Equal(new[] { personal.SourcePath }, groups[1].Paths);
    }

    [Fact]
    public void FindMappedTarget_CustomBucketsKeepIdentityEvenWithEqualTitles()
    {
        WidgetConfig first = Mapped("first", "Work", "custom:first");
        WidgetConfig second = Mapped("second", "Work", "custom:second");
        WidgetConfig legacy = Mapped("legacy", "Work", "Custom");
        Assert.Same(second, ForkOneClickOrganizePolicy.FindMappedTarget([first, second], "custom:second", "Work"));
        Assert.Null(ForkOneClickOrganizePolicy.FindMappedTarget([first], "custom:second", "Work"));
        Assert.Null(ForkOneClickOrganizePolicy.FindMappedTarget([first], DesktopOrganizationCategoryIds.Other, "Work"));
        Assert.Same(legacy, ForkOneClickOrganizePolicy.FindMappedTarget([first, legacy], DesktopOrganizationCategoryIds.Other, "Work"));
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
        Assert.Contains("TrayCommandOrganizePreview", tray, StringComparison.Ordinal);
        Assert.Contains("OpenDesktopOrganizationPreview()", tray, StringComparison.Ordinal);

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

        string mappedPreview = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/DesktopOrganizationTaskView.MappedOrganize.cs"));
        Assert.Contains("BuildMappedPreviewProjection(", mappedPreview, StringComparison.Ordinal);
        Assert.Contains("DeferredDesktopOrganizationClosePolicy.CompleteIfRequested(", mappedPreview, StringComparison.Ordinal);

        Assert.Equal("一键整理桌面", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.MenuReorganizeDesktop));
        Assert.Equal("整理预览…", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.MenuOrganizePreview));
    }

    [Fact]
    public async Task DeferredMappedClose_WaitsForInFlightOperationAndConsumesRequestOnce()
    {
        var releaseOperation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closePending = false;
        int closeCount = 0;

        Task execution = CompleteMappedExecutionAsync();
        closePending = true;
        Assert.Equal(0, closeCount);

        releaseOperation.SetResult(true);
        await execution;

        Assert.Equal(1, closeCount);
        Assert.False(closePending);
        DeferredDesktopOrganizationClosePolicy.CompleteIfRequested(ref closePending, () => closeCount++);
        Assert.Equal(1, closeCount);

        async Task CompleteMappedExecutionAsync()
        {
            try
            {
                await releaseOperation.Task;
            }
            finally
            {
                DeferredDesktopOrganizationClosePolicy.CompleteIfRequested(ref closePending, () => closeCount++);
            }
        }
    }
}
