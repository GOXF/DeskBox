using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>GOXF fork: global 「整理方式」, 「放回桌面」 and per-item hidden-attribute bookkeeping.</summary>
public sealed class ForkOrganizeModeTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "deskbox-fork-mode-tests", "Desktop");

    private static string D(string name) => MappedOrganizeMembership.NormalizePath(Path.Combine(Desktop, name));

    private static WidgetConfig Mapped(string id, string categoryId)
    {
        var widget = new WidgetConfig { Id = id, Name = id, WidgetKind = WidgetKind.File, MappedFolderPath = Desktop };
        MappedOrganizeWidgetMetadata.Mark(widget, categoryId);
        return widget;
    }

    // ---- setting persistence ----

    [Fact]
    public void Mode_RoundTripsThroughSerialization()
    {
        Assert.Equal(DesktopOrganizeMode.MapOnly, ForkOrganizeModeSettings.Parse(ForkOrganizeModeSettings.Serialize(DesktopOrganizeMode.MapOnly)));
        Assert.Equal(DesktopOrganizeMode.MoveToWidgets, ForkOrganizeModeSettings.Parse(ForkOrganizeModeSettings.Serialize(DesktopOrganizeMode.MoveToWidgets)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"organizeMode\":42}")]
    [InlineData("{\"organizeMode\":\"something\"}")]
    public void Mode_DefaultsToMove_ForMissingOrInvalidContent(string? json)
    {
        Assert.Equal(DesktopOrganizeMode.MoveToWidgets, ForkOrganizeModeSettings.Parse(json));
    }

    [Fact]
    public void Mode_IsPersistedToItsOwnFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "deskbox-fork-mode-tests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, ForkOrganizeModeSettings.FileName);
        try
        {
            Assert.Equal(DesktopOrganizeMode.MoveToWidgets, ForkOrganizeModeSettings.Load(file));

            Assert.True(ForkOrganizeModeSettings.TrySave(DesktopOrganizeMode.MapOnly, file));
            Assert.Equal(DesktopOrganizeMode.MapOnly, ForkOrganizeModeSettings.Load(file));

            Assert.True(ForkOrganizeModeSettings.TrySave(DesktopOrganizeMode.MoveToWidgets, file));
            Assert.Equal(DesktopOrganizeMode.MoveToWidgets, ForkOrganizeModeSettings.Load(file));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- mode branches ----

    [Fact]
    public void Mode_SelectsBetweenMappingAndMoving()
    {
        Assert.True(ForkOrganizeModePolicy.ConfirmMapsInsteadOfMoving(DesktopOrganizeMode.MapOnly));
        Assert.False(ForkOrganizeModePolicy.ConfirmMapsInsteadOfMoving(DesktopOrganizeMode.MoveToWidgets));
        Assert.False(ForkOrganizeModePolicy.AutoOrganizerMayMoveFiles(DesktopOrganizeMode.MapOnly));
        Assert.True(ForkOrganizeModePolicy.AutoOrganizerMayMoveFiles(DesktopOrganizeMode.MoveToWidgets));
    }

    [Theory]
    [InlineData(DesktopOrganizeMode.MapOnly, DesktopOrganizeMode.MoveToWidgets, true, OrganizeModeTransitionAction.DissolveMapping)]
    [InlineData(DesktopOrganizeMode.MapOnly, DesktopOrganizeMode.MoveToWidgets, false, OrganizeModeTransitionAction.None)]
    [InlineData(DesktopOrganizeMode.MoveToWidgets, DesktopOrganizeMode.MapOnly, true, OrganizeModeTransitionAction.None)]
    [InlineData(DesktopOrganizeMode.MapOnly, DesktopOrganizeMode.MapOnly, true, OrganizeModeTransitionAction.None)]
    public void SwitchingBackToMove_LeavesMappedOrganize(
        DesktopOrganizeMode previous,
        DesktopOrganizeMode current,
        bool hasMappedWidgets,
        OrganizeModeTransitionAction expected)
    {
        Assert.Equal(expected, ForkOrganizeModePolicy.OnModeChanged(previous, current, hasMappedWidgets));
    }

    [Fact]
    public void Startup_AdoptsMapOnly_ForInstallsThatAlreadyHaveMappedWidgets()
    {
        var migrated = ForkOrganizeModePolicy.OnStartup(modeFileExists: false, DesktopOrganizeMode.MoveToWidgets, hasMappedWidgets: true);
        Assert.Equal(DesktopOrganizeMode.MapOnly, migrated.Mode);
        Assert.Equal(DesktopOrganizeMode.MapOnly, migrated.Persist);
        Assert.Equal(OrganizeModeTransitionAction.None, migrated.Action);

        var fresh = ForkOrganizeModePolicy.OnStartup(modeFileExists: false, DesktopOrganizeMode.MoveToWidgets, hasMappedWidgets: false);
        Assert.Equal(DesktopOrganizeMode.MoveToWidgets, fresh.Mode);
        Assert.Null(fresh.Persist);

        var stale = ForkOrganizeModePolicy.OnStartup(modeFileExists: true, DesktopOrganizeMode.MoveToWidgets, hasMappedWidgets: true);
        Assert.Equal(OrganizeModeTransitionAction.DissolveMapping, stale.Action);

        var mapped = ForkOrganizeModePolicy.OnStartup(modeFileExists: true, DesktopOrganizeMode.MapOnly, hasMappedWidgets: true);
        Assert.Equal(DesktopOrganizeMode.MapOnly, mapped.Mode);
        Assert.Equal(OrganizeModeTransitionAction.None, mapped.Action);
    }

    // ---- 放回桌面 ----

    [Fact]
    public void KeptOnDesktop_IsExcludedFromAutomaticSorting()
    {
        var images = Mapped("images", DesktopOrganizationCategoryIds.Images);
        var widgets = new List<WidgetConfig> { images };
        MappedOrganizeMembership.Assign(widgets, "images", [D("a.png"), D("b.png")]);

        // 「放回桌面」 = leave the widget + remember the entry.
        MappedOrganizeMembership.RemovePath(widgets, D("a.png"));
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(MappedOrganizeKeepList.Keep(kept, [D("a.png")]));

        IReadOnlyList<string> unassigned = MappedOrganizeMembership.FindUnassigned(widgets, [D("a.png"), D("b.png"), D("c.png")]);
        Assert.Equal(new[] { D("a.png"), D("c.png") }, unassigned);
        Assert.Equal(new[] { D("c.png") }, MappedOrganizeKeepList.FilterUnassigned(unassigned, kept));
        Assert.Equal(new[] { D("b.png") }, MappedOrganizeMembership.GetMembers(images));
    }

    [Fact]
    public void KeptOnDesktop_FollowsRenames_AndIsReleasedByExplicitAssignment()
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MappedOrganizeKeepList.Keep(kept, [D("a.png")]);

        Assert.True(MappedOrganizeKeepList.Rename(kept, D("a.png"), D("renamed.png")));
        Assert.Equal(new[] { D("x.txt") }, MappedOrganizeKeepList.FilterUnassigned([D("renamed.png"), D("x.txt")], kept));
        Assert.Contains(D("renamed.png"), kept);
        Assert.False(MappedOrganizeKeepList.Rename(kept, D("missing.png"), D("other.png")));

        Assert.True(MappedOrganizeKeepList.Release(kept, [D("RENAMED.png")]));
        Assert.Empty(kept);
        Assert.False(MappedOrganizeKeepList.Release(kept, [D("renamed.png")]));
    }

    // ---- hidden attribute bookkeeping ----

    private static Func<string, FileAttributes?> Probe(Dictionary<string, FileAttributes> files) =>
        path => files.TryGetValue(path, out FileAttributes attributes) ? attributes : null;

    [Fact]
    public void Reconcile_HidesVisibleMembers_AndNeverRecordsUserHiddenFiles()
    {
        var files = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [D("visible.png")] = FileAttributes.Archive,
            [D("user-hidden.png")] = FileAttributes.Archive | FileAttributes.Hidden,
        };

        MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile(
            [D("visible.png"), D("user-hidden.png"), D("gone.png")],
            [],
            Probe(files));

        Assert.Equal(new[] { D("visible.png") }, plan.ToHide);
        Assert.Empty(plan.ToUnhide);
        Assert.Empty(plan.Forget);
    }

    [Fact]
    public void Reconcile_RestoresOnlyRecordedEntries_WhenTheyLeaveTheirWidget()
    {
        var files = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [D("ours.png")] = FileAttributes.Hidden,
            [D("ours-already-visible.png")] = FileAttributes.Archive,
            [D("user-hidden.png")] = FileAttributes.Hidden,
            [D("still-mapped.png")] = FileAttributes.Hidden,
        };

        MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile(
            desiredHidden: [D("still-mapped.png")],
            ledger: [D("ours.png"), D("ours-already-visible.png"), D("deleted.png"), D("still-mapped.png")],
            Probe(files));

        Assert.Equal(new[] { D("ours.png") }, plan.ToUnhide);
        Assert.Equal(
            new[] { D("deleted.png"), D("ours-already-visible.png") }.Order(StringComparer.OrdinalIgnoreCase),
            plan.Forget.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plan.ToHide);
        Assert.DoesNotContain(D("user-hidden.png"), plan.ToUnhide);
    }

    [Fact]
    public void Reconcile_ReHidesRecordedMembers_WhoseAttributeWasCleared()
    {
        var files = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [D("a.png")] = FileAttributes.Archive,
        };

        MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile([D("a.png")], [D("a.png")], Probe(files));

        Assert.Equal(new[] { D("a.png") }, plan.ToHide);
    }

    [Fact]
    public void Reconcile_WithNothingDesired_RestoresEverythingRecorded()
    {
        // Exit, leaving mapped organize, switching back to move, crash recovery.
        var files = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [D("a.png")] = FileAttributes.Hidden | FileAttributes.Archive,
            [D("b")] = FileAttributes.Hidden | FileAttributes.Directory,
        };

        MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile([], [D("a.png"), D("b")], Probe(files));

        Assert.Equal(2, plan.ToUnhide.Count);
        Assert.Empty(plan.ToHide);
    }

    [Fact]
    public void Ledger_SerializesOnePathPerLine_AndFollowsRenames()
    {
        string content = MappedOrganizeHiddenLedger.Serialize([D("a.png"), D("A.PNG"), "", D("b.txt")]);
        IReadOnlyList<string> parsed = MappedOrganizeHiddenLedger.Parse(content + "\n\n");
        Assert.Equal(new[] { D("a.png"), D("b.txt") }, parsed);
        Assert.Empty(MappedOrganizeHiddenLedger.Parse(null));

        var ledger = new HashSet<string>(parsed, StringComparer.OrdinalIgnoreCase);
        Assert.True(MappedOrganizeHiddenLedger.Rename(ledger, D("a.png"), D("c.png")));
        Assert.Contains(D("c.png"), ledger);
        Assert.DoesNotContain(D("a.png"), ledger);
    }

    // ---- small helpers ----

    [Fact]
    public void Text_IsLocalizedForChinese_AndFallsBackToEnglish()
    {
        Assert.Equal("只映射，不移动文件", ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, ForkOrganizeModeText.Key.OptionMapOnly));
        Assert.Equal("放回桌面", ForkOrganizeModeText.Get(LocalizationService.LanguageChineseTraditional, ForkOrganizeModeText.Key.MenuKeepOnDesktop));
        Assert.Equal("Move into widgets", ForkOrganizeModeText.Get("de-DE", ForkOrganizeModeText.Key.OptionMove));
        foreach (ForkOrganizeModeText.Key key in Enum.GetValues<ForkOrganizeModeText.Key>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ForkOrganizeModeText.Get(LocalizationService.LanguageChinese, key)));
            Assert.NotEqual(key.ToString(), ForkOrganizeModeText.Get("en-US", key));
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(null, false)]
    [InlineData("1", true)]
    public void ExplorerHiddenItemsSetting_ReadsShowValue(object? value, bool expected)
    {
        Assert.Equal(expected, ExplorerHiddenItemsSetting.IsShowValue(value));
    }
}
