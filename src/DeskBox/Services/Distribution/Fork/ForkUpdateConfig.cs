namespace DeskBox.Services;

/// <summary>
/// GOXF/DeskBox fork: where the installed app looks for its own updates and
/// for upstream (Tianyu199509/DeskBox) releases. Kept in a fork-only file so
/// upstream changes to <see cref="AppUpdateService"/> merge with only a few
/// one-line touch points.
///
/// Fork versions are the upstream base version plus a 4th "fork revision"
/// segment (1.5.9 + 1 => 1.5.9.1, see Directory.Build.targets). Fork releases
/// are tagged v1.5.9.1 on GOXF/DeskBox with the same asset names upstream
/// uses (DeskBox_Setup_1.5.9.1_x64.exe + .sha256).
/// </summary>
public static class ForkUpdateConfig
{
    public const string ForkOwner = "GOXF";
    public const string UpstreamOwner = "Tianyu199509";
    public const string RepositoryName = "DeskBox";

    /// <summary>The fork's own releases: the only source of installers.</summary>
    public const string ForkLatestReleaseApiUrl = "https://api.github.com/repos/GOXF/DeskBox/releases/latest";

    /// <summary>Upstream releases: informational only, never downloaded.</summary>
    public const string UpstreamLatestReleaseApiUrl = "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest";

    public const string ForkReleasesPageUrl = "https://github.com/GOXF/DeskBox/releases/latest";
    public const string UpstreamReleasesPageUrl = "https://github.com/Tianyu199509/DeskBox/releases/latest";

    /// <summary>
    /// The fork publishes no JSON manifest. The upstream one at
    /// deskbox.fun/update/stable.json describes upstream installers and must
    /// never drive this build's updater, so the default is empty (= skip).
    /// </summary>
    public const string ManifestUrl = "";

    internal const string ForkReleaseDownloadPathPrefix = "/GOXF/DeskBox/releases/download/";

    /// <summary>
    /// Only this fork's GitHub release downloads may be installed. The asset
    /// bytes arrive by redirect from objects.githubusercontent.com, which the
    /// HTTP client follows and which therefore is never trusted as an origin.
    /// Upstream hosts (deskbox.fun, github.com/Tianyu199509) are deliberately
    /// NOT trusted: an upstream installer would overwrite the fork build.
    /// </summary>
    public static bool IsTrustedInstallerDownloadUri(Uri uri)
    {
        return uri.IsAbsoluteUri &&
            string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith(ForkReleaseDownloadPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Release-tag page for one version: 4-segment versions are fork builds
    /// (GOXF/DeskBox), 3-segment versions are upstream releases whose notes
    /// live on Tianyu199509/DeskBox.
    /// </summary>
    public static string GetReleaseTagUrl(string version)
    {
        string trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        string owner = IsForkVersion(trimmed) ? ForkOwner : UpstreamOwner;
        return $"https://github.com/{owner}/{RepositoryName}/releases/tag/v{trimmed}";
    }

    public static bool IsForkVersion(string? version)
    {
        return AppUpdateService.TryParseVersion(version, out Version parsed) && parsed.Revision >= 0;
    }

    /// <summary>
    /// The upstream base (first three segments) of a fork or upstream
    /// version: "1.5.9.1" => "1.5.9", "v1.6.0" => "1.6.0". Empty when the
    /// text is not a version.
    /// </summary>
    public static string GetBaseVersion(string? version)
    {
        if (!AppUpdateService.TryParseVersion(version, out Version parsed))
        {
            return string.Empty;
        }

        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build)).ToString(3);
    }

    /// <summary>
    /// True when upstream released a version newer than the upstream base
    /// this build was made from. Only the first three segments are compared.
    /// </summary>
    public static bool IsUpstreamNewer(string? currentVersion, string? upstreamVersion)
    {
        string currentBase = GetBaseVersion(currentVersion);
        string upstreamBase = GetBaseVersion(upstreamVersion);
        return currentBase.Length > 0 &&
            upstreamBase.Length > 0 &&
            Version.Parse(upstreamBase) > Version.Parse(currentBase);
    }
}
