using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// Result of the informational upstream check: upstream's latest release
/// tag compared against the upstream base of the running fork build.
/// </summary>
public sealed record UpstreamReleaseInfo(
    string UpstreamVersion,
    string CurrentBaseVersion,
    string ReleaseUrl,
    bool IsNewer);

/// <summary>
/// GOXF/DeskBox fork: asks Tianyu199509/DeskBox's GitHub "latest release"
/// whether upstream has moved past the base this build was made from. Purely
/// informational: nothing is downloaded from upstream, and any failure
/// (offline, rate limit, malformed JSON) yields null so it can never block or
/// alter the normal update flow.
/// </summary>
public static class UpstreamReleaseCheckService
{
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskBox/{AppUpdateService.GetCurrentAppVersion()}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Task<UpstreamReleaseInfo?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        return CheckAsync(currentVersion, s_httpClient, ForkUpdateConfig.UpstreamLatestReleaseApiUrl, cancellationToken);
    }

    internal static async Task<UpstreamReleaseInfo?> CheckAsync(
        string currentVersion,
        HttpClient httpClient,
        string latestReleaseApiUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(latestReleaseApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseLatestRelease(currentVersion, json);
        }
        catch (Exception ex)
        {
            App.Log($"[Update] Upstream release check skipped: {ex.GetType().Name}");
            return null;
        }
    }

    internal static UpstreamReleaseInfo? ParseLatestRelease(string currentVersion, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tag_name", out JsonElement tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string upstreamVersion = ForkUpdateConfig.GetBaseVersion(tagElement.GetString());
            string currentBase = ForkUpdateConfig.GetBaseVersion(currentVersion);
            if (upstreamVersion.Length == 0 || currentBase.Length == 0)
            {
                return null;
            }

            string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement) &&
                urlElement.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps
                    ? uri.ToString()
                    : ForkUpdateConfig.UpstreamReleasesPageUrl;

            return new UpstreamReleaseInfo(
                upstreamVersion,
                currentBase,
                releaseUrl,
                ForkUpdateConfig.IsUpstreamNewer(currentBase, upstreamVersion));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
