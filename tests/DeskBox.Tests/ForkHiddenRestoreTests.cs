using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>GOXF fork 「映射整理」: restore-before-exit state, startup recovery plan and the hidden-attribute leak policy.</summary>
public sealed class ForkHiddenRestoreTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "deskbox-fork-hidden-tests", "Desktop");

    private static string D(string name) => MappedOrganizeMembership.NormalizePath(Path.Combine(Desktop, name));

    [Fact]
    public void ExitGate_FinalExit_NeverResumes()
    {
        var gate = new MappedOrganizeExitGate();
        Assert.False(gate.IsRestored);
        Assert.True(gate.Restore(sessionEnd: false));
        Assert.True(gate.IsRestored);
        Assert.False(gate.Restore(sessionEnd: false));
        Assert.False(gate.TryResume());
        Assert.True(gate.IsRestored);
    }

    [Fact]
    public void ExitGate_CancelledSessionEnd_ResumesHiding()
    {
        var gate = new MappedOrganizeExitGate();
        Assert.True(gate.Restore(sessionEnd: true));
        Assert.False(gate.Restore(sessionEnd: true)); // QUERYENDSESSION then ENDSESSION
        Assert.True(gate.IsRestored);
        Assert.True(gate.TryResume());
        Assert.False(gate.IsRestored);
        Assert.False(gate.TryResume());
    }

    [Fact]
    public void ExitGate_ShutdownDuringSessionEnd_StaysRestored()
    {
        var gate = new MappedOrganizeExitGate();
        gate.Restore(sessionEnd: true);
        gate.Restore(sessionEnd: false);
        Assert.False(gate.TryResume());
        Assert.True(gate.IsRestored);
    }

    [Fact]
    public void StartupRecovery_RestoresOnlyExistingHiddenLedgerEntries()
    {
        var files = new Dictionary<string, FileAttributes?>(StringComparer.OrdinalIgnoreCase)
        {
            [D("hidden.png")] = FileAttributes.Archive | FileAttributes.Hidden,
            [D("visible.png")] = FileAttributes.Archive,
        };

        IReadOnlyList<string> plan = MappedOrganizeStartupRecovery.PlanRestore(
            [D("hidden.png"), D("visible.png"), D("gone.png")],
            path => files.TryGetValue(path, out FileAttributes? value) ? value : null);

        Assert.Equal(new[] { D("hidden.png") }, plan);
    }

    [Fact]
    public void LeakPolicy_ClassifiesWhereTheEntryWent()
    {
        static bool IsDesktop(string path) =>
            string.Equals(Path.GetDirectoryName(path), MappedOrganizeMembership.NormalizePath(Desktop), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(MappedOrganizeLeakPolicy.Outcome.Gone, MappedOrganizeLeakPolicy.Classify(null, IsDesktop));
        Assert.Equal(MappedOrganizeLeakPolicy.Outcome.StillOnDesktop, MappedOrganizeLeakPolicy.Classify(D("renamed.png"), IsDesktop));
        Assert.Equal(
            MappedOrganizeLeakPolicy.Outcome.LeftDesktop,
            MappedOrganizeLeakPolicy.Classify(Path.Combine(Desktop, "Sub", "moved.png"), IsDesktop));
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\Docs\a.txt", @"C:\Users\me\Docs\a.txt")]
    [InlineData(@"\\?\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"C:\plain\a.txt", @"C:\plain\a.txt")]
    public void LeakPolicy_NormalizesFinalPaths(string input, string expected) =>
        Assert.Equal(expected, MappedOrganizeLeakPolicy.NormalizeFinalPath(input));

    [Fact]
    public void Registry_EffectiveAttributes_TreatDeskBoxHiddenAsNormal()
    {
        FileAttributes hidden = FileAttributes.Archive | FileAttributes.Hidden;
        Assert.Equal(FileAttributes.Archive, MappedOrganizeHiddenRegistry.Effective(hidden, hiddenByDeskBox: true));
        Assert.Equal(hidden, MappedOrganizeHiddenRegistry.Effective(hidden, hiddenByDeskBox: false));
        Assert.Equal(
            FileAttributes.Archive | FileAttributes.System,
            MappedOrganizeHiddenRegistry.Effective(FileAttributes.Archive | FileAttributes.System | FileAttributes.Hidden, hiddenByDeskBox: true));
    }
}
