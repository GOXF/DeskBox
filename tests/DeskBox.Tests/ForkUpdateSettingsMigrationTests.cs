using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class ForkUpdateSettingsMigrationTests
{
    [Fact]
    public void ReleaseNotes_PreservesWinRtBrowserLaunchWithIsolatedShellFallback()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ReleaseNotesWindow.xaml.cs"));
        int launcher = source.IndexOf("await Launcher.LaunchUriAsync(uri)", StringComparison.Ordinal);
        int fallback = source.IndexOf("ShellLaunchService.FireAndForgetOpenPath(url)", StringComparison.Ordinal);
        Assert.True(launcher >= 0 && fallback > launcher);
        Assert.Contains("AppUpdateManifest.IsSafeReleaseNotesUrl(manifest.ReleaseNotesUrl)", source, StringComparison.Ordinal);
        Assert.Contains("AppDistributionService.Current.IsMicrosoftStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateDetails_ContainsForkControlsWithoutRestoringOldSettingsLayout()
    {
        var document = XDocument.Load(TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        XNamespace services = "using:DeskBox.Services";
        XElement details = Assert.Single(document.Descendants().Where(element =>
            element.Attribute(services + "Localized.HeaderKey")?.Value == "Settings.Update.Details"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement template = Assert.Single(details.Ancestors().Where(element => element.Name.LocalName == "DataTemplate"));
        Assert.Equal("viewModels:SettingsViewModel", template.Attribute(xaml + "DataType")?.Value);
        string[] bindings = details.Descendants().Attributes().Select(attribute => attribute.Value).ToArray();
        Assert.Contains("{x:Bind UpstreamReleaseNoticeText, Mode=OneWay}", bindings);
        Assert.Contains("{x:Bind ForkDevUpdateVisibility, Mode=OneWay}", bindings);
        Assert.Contains("{x:Bind UpdateDownloadProxy, Mode=TwoWay}", bindings);
        Assert.Contains("ForkDevUpdateButton_Click", bindings);
        Assert.Contains("{Binding UpdateProgressVisibility}", bindings);
        XElement health = Assert.Single(document.Descendants().Where(element =>
            element.Attribute(services + "Localized.HeaderKey")?.Value == "Settings.RuntimeHealth.Title"));
        Assert.DoesNotContain(health.Descendants().Attributes(), attribute => attribute.Value.Contains("ForkDevUpdate", StringComparison.Ordinal));
    }
}
