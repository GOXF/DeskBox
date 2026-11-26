using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// GOXF/DeskBox fork: optional download proxy for update installers, for
/// networks where github.com release downloads stall. Stored in its own small
/// file (not the upstream settings model) so the settings process writes it
/// and the main process reads it at download time without any IPC, and
/// upstream settings changes never conflict with it.
///
/// The proxy only carries bytes. The installer's SHA-256 still comes from the
/// GitHub API (asset digest / .sha256), and the original URL must still pass
/// <see cref="ForkUpdateConfig.IsTrustedInstallerDownloadUri"/>, so a bad or
/// hostile proxy can at worst fail the download, never install other bytes.
///
/// Formats: a prefix such as "https://gh-proxy.com/" (original URL appended)
/// or a template containing "{url}" (replaced with the original URL).
/// </summary>
public static class ForkDownloadProxySettings
{
    private const string FileName = "fork-update.json";
    private const string UrlPlaceholder = "{url}";

    private static string FilePath => Path.Combine(DeskBoxDataPathService.Current.RootPath, FileName);

    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return string.Empty;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.TryGetProperty("downloadProxy", out JsonElement value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            // Missing data root (tests), unreadable or malformed file: no proxy.
            return string.Empty;
        }
    }

    /// <summary>Saves a normalized value; returns false when it is not a usable http(s) proxy.</summary>
    public static bool TrySave(string? proxy)
    {
        string value = (proxy ?? string.Empty).Trim();
        if (value.Length > 0 && !IsValidProxy(value))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "{\"downloadProxy\":" + JsonEncode(value) + "}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsValidProxy(string proxy)
    {
        string probe = proxy.Trim().Replace(UrlPlaceholder, "x", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(probe, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            !string.IsNullOrEmpty(uri.Host);
    }

    public static Uri? BuildProxiedUri(string? proxy, Uri original)
    {
        if (string.IsNullOrWhiteSpace(proxy) || !IsValidProxy(proxy))
        {
            return null;
        }

        string value = proxy.Trim();
        string url = value.Contains(UrlPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? value.Replace(UrlPlaceholder, original.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
            : value.TrimEnd('/') + "/" + original.AbsoluteUri;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? result) ? result : null;
    }

    /// <summary>Proxy first (when set), then the original GitHub URL as fallback.</summary>
    public static IReadOnlyList<Uri> GetDownloadCandidates(Uri original, string? proxy)
    {
        Uri? proxied = BuildProxiedUri(proxy, original);
        return proxied is null || proxied == original ? [original] : [proxied, original];
    }

    private static string JsonEncode(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                    else { sb.Append(c); }
                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
