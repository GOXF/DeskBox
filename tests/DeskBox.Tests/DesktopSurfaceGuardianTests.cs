namespace DeskBox.Tests;

public sealed class DesktopSurfaceGuardianTests
{
    [Fact]
    public void GuardianArguments_AcceptValidDecimalValues()
    {
        string[] args = ["--desktop-surface-guardian", "123", "638998765432100000", "456", "789"];

        Assert.True(DeskBox.Updater.DesktopSurfaceGuardian.TryParseArguments(args, out var parsed));
        Assert.Equal(123, parsed.OwnerProcessId);
        Assert.Equal(638998765432100000, parsed.OwnerStartTimeUtcTicks);
        Assert.Equal((nint)456, parsed.NativeListViewHandle);
        Assert.Equal(789, parsed.ShellProcessId);
    }

    [Theory]
    [InlineData("--desktop-surface-guardian", "0", "1", "2", "3")]
    [InlineData("--desktop-surface-guardian", "1", "0", "2", "3")]
    [InlineData("--desktop-surface-guardian", "1", "1", "0", "3")]
    [InlineData("--desktop-surface-guardian", "1", "1", "2", "0")]
    [InlineData("--desktop-surface-guardian", "1", "1", "2", "1")]
    [InlineData("--desktop-surface-guardian", "+1", "1", "2", "3")]
    [InlineData("--desktop-surface-guardian", "1", "1", "2", "3", "extra")]
    [InlineData("--wrong-mode", "1", "1", "2", "3")]
    [InlineData("--desktop-surface-guardian", "1", "3155378976000000000", "2", "3")]
    public void GuardianArguments_RejectInvalidOrUnsafeValues(params string[] args)
    {
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.TryParseArguments(args, out _));
    }

    [Fact]
    public void GuardianArguments_RejectHandlesThatDoNotFitTheProcessArchitecture()
    {
        if (IntPtr.Size != 4)
        {
            return;
        }

        string[] args = ["--desktop-surface-guardian", "1", "1", "2147483648", "2"];
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.TryParseArguments(args, out _));
    }

    [Fact]
    public void OwnerIdentity_RequiresMatchingPidAndStartTime()
    {
        var expected = new DeskBox.Updater.DesktopSurfaceGuardianArguments(123, 456, (nint)789, 321);

        Assert.True(DeskBox.Updater.DesktopSurfaceGuardian.IsOwnerProcessInstance(expected, 123, 456));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.IsOwnerProcessInstance(expected, 124, 456));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.IsOwnerProcessInstance(expected, 123, 457));
    }

    [Fact]
    public void RestorePolicy_RequiresTheExactListViewInTheOriginalShellProcess()
    {
        Assert.True(DeskBox.Updater.DesktopSurfaceGuardian.ShouldRestoreListView(true, "SysListView32", 22, 22, 22));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.ShouldRestoreListView(false, "SysListView32", 22, 22, 22));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.ShouldRestoreListView(true, "Other", 22, 22, 22));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.ShouldRestoreListView(true, "SysListView32", 23, 22, 22));
        Assert.False(DeskBox.Updater.DesktopSurfaceGuardian.ShouldRestoreListView(true, "SysListView32", 22, 23, 22));
    }
}
