namespace DeskBox.Services;

/// <summary>GOXF fork: the UI culture for <see cref="ForkOrganizeModeText"/>, read from the running app (main or settings process).</summary>
public static partial class ForkOrganizeModeText
{
    private static string? CurrentCulture()
    {
        try
        {
            return App.Current?.LocalizationService?.CurrentCultureName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
