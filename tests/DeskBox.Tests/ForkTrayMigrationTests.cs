using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ForkTrayMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeTray_PreservesSeparateOneClickPreviewAndSearchActions(bool canCreateWidget)
    {
        string root = Path.Combine(Path.GetTempPath(), $"DeskBox-fork-tray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var localization = new LocalizationService(new SettingsService(Path.Combine(root, "settings.json")));
            var invoked = new List<string>();
            Task Run(string action)
            {
                invoked.Add(action);
                return Task.CompletedTask;
            }

            var entries = App.BuildTrayMenuEntriesCore(
                localization, canCreateWidget, false, null,
                () => Run("one-click"), _ => Task.CompletedTask,
                () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, () => Task.CompletedTask,
                onOpenGlobalSearch: () => Run("search"),
                onOpenOrganizePreview: () => Run("preview"));

            Assert.Equal(localization.T("Tray.OrganizeDesktop"), entries[0].Text);
            Assert.Equal(ForkOrganizeModeText.Get(localization.CurrentCultureName,
                ForkOrganizeModeText.Key.MenuOrganizePreview), entries[1].Text);
            Assert.Equal(localization.T("Tray.GlobalSearch"), entries[2].Text);
            Assert.Equal(canCreateWidget, entries[0].IsEnabled);
            Assert.Equal(canCreateWidget, entries[1].IsEnabled);
            Assert.True(entries[2].IsEnabled);
            Assert.True(entries[3].IsSeparator);
            int[] commands = entries.Where(entry => !entry.IsSeparator).Select(entry => entry.CommandId).ToArray();
            Assert.Equal(commands.Length, commands.Distinct().Count());
            await entries[0].Action!();
            await entries[1].Action!();
            await entries[2].Action!();
            Assert.Equal(new[] { "one-click", "preview", "search" }, invoked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shutdown_RestoresMappedDesktopOnUiBeforeBoundedTeardown()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        int shutdown = source.IndexOf("private async Task ShutdownApplicationAsync()", StringComparison.Ordinal);
        int dispatch = source.IndexOf("await TryDispatchShutdownToUiAsync(queue)", shutdown, StringComparison.Ordinal);
        int uiCore = source.IndexOf("private async Task ShutdownApplicationOnUiCoreAsync()", dispatch, StringComparison.Ordinal);
        int restore = source.IndexOf("RestoreMappedOrganizeBeforeExit(\"shutdown\")", uiCore, StringComparison.Ordinal);
        int teardown = source.IndexOf("await ShutdownCoreAsync()", uiCore, StringComparison.Ordinal);
        Assert.True(shutdown >= 0 && dispatch > shutdown && uiCore > dispatch);
        Assert.True(restore > uiCore && teardown > restore);
        Assert.Contains("ScheduleForkDevUpdateCheck", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleBackgroundWidgetCatalogSync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopHitTest_SeparatesOwnedSurfaceFromVerifiedShellSurface()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/DesktopBlankHitTest.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("(MappedDesktopSurfaceController.OwnsWindow(listView) ||\n" +
            "                (IsShellDesktopListView(listView) &&\n" +
            "                 IsVerifiedDesktopShellWindow(listView, allowDefView: true))) &&", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MappedDragTracking_StartsOnlyAfterSuccessfulUpstreamPackageSetup()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        int setup = source.IndexOf("e.DragUI.SetContentFromDataPackage();", StringComparison.Ordinal);
        int tracking = source.IndexOf("BeginMappedDragTracking(_activeDragSessionId, _activeDragSourcePaths);", StringComparison.Ordinal);
        int failed = source.IndexOf("stage=PackageSetupFailed", tracking, StringComparison.Ordinal);
        Assert.True(setup >= 0 && tracking > setup && failed > tracking);
        string oneClick = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.ForkOneClickOrganize.cs"));
        Assert.DoesNotContain("H.NotifyIcon", oneClick, StringComparison.Ordinal);
        Assert.Contains("TryShowTrayBalloonNotification", oneClick, StringComparison.Ordinal);
        Assert.Contains("_inProcessFallbackSettingsWindow?.RefreshDesktopOrganizationState()", oneClick, StringComparison.Ordinal);
        Assert.Contains("SettingsIpcMethods.HostDesktopOrgRefresh", oneClick, StringComparison.Ordinal);
        string devUpdate = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.ForkDevUpdate.cs"));
        Assert.DoesNotContain("H.NotifyIcon", devUpdate, StringComparison.Ordinal);
        Assert.Contains("if (TryShowTrayBalloonNotification", devUpdate, StringComparison.Ordinal);
    }
}
