using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MappedDesktopPresentationTests
{
    [Fact]
    public void Presentation_PreservesEnabledVirtualItemsAndNewDesktopFiles_WithoutExtraNamespaceFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox-presentation");
        string desktop = Path.Combine(root, "Desktop");
        string publicDesktop = Path.Combine(root, "PublicDesktop");
        string recycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        string network = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
        string enabledFolder = Path.Combine(root, "EnabledFolder");
        var include = MappedDesktopShellView.CreateDesktopPresentationFilter([recycleBin, enabledFolder], desktop, publicDesktop);

        Assert.True(include(recycleBin));
        Assert.True(include(enabledFolder));
        Assert.True(include(Path.Combine(desktop, "new.png")));
        Assert.True(include(Path.Combine(publicDesktop, "shared.lnk")));
        Assert.False(include(network));
        Assert.False(include(Path.Combine(root, "Documents")));
        Assert.False(include(Path.Combine(desktop, "Folder", "nested.txt")));
        Assert.False(include("relative.txt"));
    }
}
