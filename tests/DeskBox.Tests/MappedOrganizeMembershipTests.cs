using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MappedOrganizeMembershipTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "deskbox-mapped-tests", "Desktop");

    private static string D(string name) => Path.Combine(Desktop, name);

    private static WidgetConfig Mapped(string id, string categoryId)
    {
        var widget = new WidgetConfig { Id = id, Name = id, WidgetKind = WidgetKind.File, MappedFolderPath = Desktop };
        MappedOrganizeWidgetMetadata.Mark(widget, categoryId);
        return widget;
    }

    [Fact]
    public void Mark_IdentifiesMappedWidgets_AndOrdinaryWidgetsStayOrdinary()
    {
        var mapped = Mapped("a", DesktopOrganizationCategoryIds.Images);
        var ordinary = new WidgetConfig { Id = "b", MappedFolderPath = Desktop };

        Assert.True(MappedOrganizeWidgetMetadata.IsMappedOrganize(mapped));
        Assert.False(MappedOrganizeWidgetMetadata.IsMappedOrganize(ordinary));
        Assert.Equal(DesktopOrganizationCategoryIds.Images, MappedOrganizeWidgetMetadata.GetCategory(mapped));
        Assert.Empty(MappedOrganizeMembership.GetMembers(mapped));
    }

    [Fact]
    public void Assign_IsExclusive_AndOnlyChangesMembership()
    {
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var other = Mapped("other", DesktopOrganizationCategoryIds.Other);
        var widgets = new List<WidgetConfig> { images, other };

        MappedOrganizeMembership.Assign(widgets, "other", [D("a.png"), D("b.txt")]);
        IReadOnlyList<string> changed = MappedOrganizeMembership.Assign(widgets, "images", [D("a.png")]);

        Assert.Equal(new[] { "other", "images" }, changed);
        Assert.Equal(new[] { D("a.png") }, MappedOrganizeMembership.GetMembers(images));
        Assert.Equal(new[] { D("b.txt") }, MappedOrganizeMembership.GetMembers(other));
        Assert.Same(images, MappedOrganizeMembership.FindOwner(widgets, D("A.PNG")));
    }

    [Fact]
    public void Assign_IgnoresUnknownOrOrdinaryTargets_AndDeduplicates()
    {
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var ordinary = new WidgetConfig { Id = "folder", MappedFolderPath = Desktop };
        var widgets = new List<WidgetConfig> { images, ordinary };

        Assert.Empty(MappedOrganizeMembership.Assign(widgets, "folder", [D("x.png")]));
        Assert.Empty(MappedOrganizeMembership.Assign(widgets, "missing", [D("x.png")]));

        MappedOrganizeMembership.Assign(widgets, "images", [D("x.png"), D("X.png"), D("x.png")]);
        Assert.Single(MappedOrganizeMembership.GetMembers(images));
        Assert.Empty(MappedOrganizeMembership.Assign(widgets, "images", [D("x.png")]));
    }

    [Fact]
    public void Rename_KeepsEntryInSameWidgetAndPosition()
    {
        var docs = Mapped("docs", DesktopOrganizationCategoryIds.Documents);
        var widgets = new List<WidgetConfig> { docs };
        MappedOrganizeMembership.Assign(widgets, "docs", [D("1.txt"), D("2.txt"), D("3.txt")]);

        IReadOnlyList<string> changed = MappedOrganizeMembership.RenamePath(widgets, D("2.txt"), D("renamed.txt"));

        Assert.Equal(new[] { "docs" }, changed);
        Assert.Equal(new[] { D("1.txt"), D("renamed.txt"), D("3.txt") }, MappedOrganizeMembership.GetMembers(docs));
        Assert.Empty(MappedOrganizeMembership.RenamePath(widgets, D("unknown.txt"), D("y.txt")));
    }

    [Fact]
    public void RemoveAndPrune_DropOnlyMissingEntries()
    {
        var docs = Mapped("docs", DesktopOrganizationCategoryIds.Documents);
        var widgets = new List<WidgetConfig> { docs };
        MappedOrganizeMembership.Assign(widgets, "docs", [D("keep.txt"), D("gone.txt"), D("deleted.txt")]);

        Assert.Equal(new[] { "docs" }, MappedOrganizeMembership.RemovePath(widgets, D("deleted.txt")));
        Assert.Equal(new[] { "docs" }, MappedOrganizeMembership.Prune(widgets, path => !path.EndsWith("gone.txt", StringComparison.Ordinal)));
        Assert.Equal(new[] { D("keep.txt") }, MappedOrganizeMembership.GetMembers(docs));
        Assert.Empty(MappedOrganizeMembership.Prune(widgets, _ => true));
    }

    [Fact]
    public void FindUnassigned_ReturnsDesktopEntriesWithoutOwner()
    {
        var docs = Mapped("docs", DesktopOrganizationCategoryIds.Documents);
        var widgets = new List<WidgetConfig> { docs };
        MappedOrganizeMembership.Assign(widgets, "docs", [D("a.txt")]);

        IReadOnlyList<string> unassigned = MappedOrganizeMembership.FindUnassigned(
            widgets, [D("a.txt"), D("b.png"), D("B.PNG"), D("c.lnk")]);

        Assert.Equal(new[] { D("b.png"), D("c.lnk") }, unassigned);
    }

    [Fact]
    public void ResolveTarget_PrefersCategory_ThenOther_ThenNull()
    {
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var other = Mapped("other", DesktopOrganizationCategoryIds.Other);

        Assert.Same(images, MappedOrganizeMembership.ResolveTarget([images, other], DesktopOrganizationCategoryIds.Images));
        Assert.Same(other, MappedOrganizeMembership.ResolveTarget([images, other], DesktopOrganizationCategoryIds.Packages));
        Assert.Null(MappedOrganizeMembership.ResolveTarget([images], DesktopOrganizationCategoryIds.Packages));
    }

    [Fact]
    public void PlanAssignments_UsesClassifierCategories_AndReportsMissingOther()
    {
        var classifier = new DesktopOrganizationClassifier();
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var docs = Mapped("docs", DesktopOrganizationCategoryIds.Documents);
        var entries = new[]
        {
            (D("photo.jpg"), MappedOrganizeMembership.Classify(classifier, D("photo.jpg"), isDirectory: false)),
            (D("report.pdf"), MappedOrganizeMembership.Classify(classifier, D("report.pdf"), isDirectory: false)),
            (D("setup.exe"), MappedOrganizeMembership.Classify(classifier, D("setup.exe"), isDirectory: false)),
            (D("Folder"), MappedOrganizeMembership.Classify(classifier, D("Folder"), isDirectory: true))
        };

        var plan = MappedOrganizeMembership.PlanAssignments([images, docs], entries, out List<string> needsOther);

        Assert.Equal(new[] { D("photo.jpg") }, plan["images"]);
        Assert.Equal(new[] { D("report.pdf") }, plan["docs"]);
        Assert.Equal(new[] { D("setup.exe"), D("Folder") }, needsOther);
    }

    [Fact]
    public void Membership_RoundTripsThroughWidgetMetadata()
    {
        var widget = Mapped("w", DesktopOrganizationCategoryIds.Media);
        MappedOrganizeMembership.SetMembers(widget, [D("song.mp3"), D("clip.mp4")]);

        var copy = new WidgetConfig { Metadata = new Dictionary<string, string>(widget.Metadata) };

        Assert.True(MappedOrganizeWidgetMetadata.IsMappedOrganize(copy));
        Assert.Equal(new[] { D("song.mp3"), D("clip.mp4") }, MappedOrganizeMembership.GetMembers(copy));
        Assert.True(MappedOrganizeMembership.IsMember(copy, D("SONG.mp3")));
    }

    [Fact]
    public void OrdinaryOrganization_NeverRoutesToMappedWidgets()
    {
        var mapped = Mapped("mapped", DesktopOrganizationCategoryIds.Images);
        var rule = new DesktopOrganizationRule
        {
            TargetWidgetId = "mapped",
            CategoryIds = [DesktopOrganizationCategoryIds.Images]
        };
        var item = new DesktopOrganizationFileSnapshot(
            D("a.png"), "a.png", ".png", 1, DateTime.UtcNow,
            DesktopOrganizationCategoryIds.Images, null, DesktopOrganizationExclusionReason.None);

        Assert.Null(new DesktopOrganizationRuleResolver().Resolve(item, [rule], [mapped]));
    }
}
