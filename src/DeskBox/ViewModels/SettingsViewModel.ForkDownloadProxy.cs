using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// GOXF/DeskBox fork: "update download proxy" field under the update details.
/// Text lives here (not Strings/*.json) for the same merge-conflict reason as
/// SettingsViewModel.ForkUpstream.cs. Saved to its own file on edit; the main
/// process reads it on the next download.
/// </summary>
public partial class SettingsViewModel
{
    private string? _updateDownloadProxy;
    private string _updateDownloadProxyStatus = string.Empty;

    public Visibility UpdateDownloadProxyVisibility =>
        IsDirectInstallerUpdateDelivery ? Visibility.Visible : Visibility.Collapsed;

    public string UpdateDownloadProxy
    {
        get => _updateDownloadProxy ??= ForkDownloadProxySettings.Load();
        set
        {
            string normalized = (value ?? string.Empty).Trim();
            if (string.Equals(normalized, UpdateDownloadProxy, StringComparison.Ordinal))
            {
                return;
            }

            bool saved = ForkDownloadProxySettings.TrySave(normalized);
            if (saved)
            {
                _updateDownloadProxy = normalized;
            }

            _updateDownloadProxyStatus = ForkProxyText(saved
                ? (normalized.Length == 0 ? ProxyText.Cleared : ProxyText.Saved)
                : ProxyText.Invalid);
            OnPropertyChanged(nameof(UpdateDownloadProxy));
            OnPropertyChanged(nameof(UpdateDownloadProxyStatus));
        }
    }

    public string UpdateDownloadProxyLabel => ForkProxyText(ProxyText.Label);

    public string UpdateDownloadProxyStatus =>
        _updateDownloadProxyStatus.Length > 0 ? _updateDownloadProxyStatus : ForkProxyText(ProxyText.Hint);

    internal enum ProxyText { Label, Hint, Saved, Cleared, Invalid }

    internal string ForkProxyText(ProxyText key) =>
        FormatForkProxyText(_localizationService.CurrentCultureName, key);

    internal static string FormatForkProxyText(string? cultureName, ProxyText key)
    {
        bool zhHans = cultureName == LocalizationService.LanguageChinese;
        bool zhHant = cultureName == LocalizationService.LanguageChineseTraditional;
        return key switch
        {
            ProxyText.Label => zhHans ? "更新下载代理（可选）" : zhHant ? "更新下載代理（可選）" : "Update download proxy (optional)",
            ProxyText.Hint => zhHans
                ? "GitHub 下载慢时填写，如 https://gh-proxy.com/ 或含 {url} 的模板；先走代理，30 秒无数据自动改用 GitHub 直连。安装包仍按 GitHub 公布的 SHA-256 校验。"
                : zhHant
                    ? "GitHub 下載慢時填寫，如 https://gh-proxy.com/ 或含 {url} 的範本；先走代理，30 秒無資料自動改用 GitHub 直連。安裝包仍按 GitHub 公布的 SHA-256 校驗。"
                    : "For slow GitHub downloads, e.g. https://gh-proxy.com/ or a template containing {url}. The proxy is tried first; after 30 s without data DeskBox falls back to GitHub. Installers are still verified against GitHub's SHA-256.",
            ProxyText.Saved => zhHans ? "已保存，下次下载更新时生效。" : zhHant ? "已儲存，下次下載更新時生效。" : "Saved. Used for the next update download.",
            ProxyText.Cleared => zhHans ? "已清除，将直连 GitHub 下载。" : zhHant ? "已清除，將直連 GitHub 下載。" : "Cleared. Updates download directly from GitHub.",
            _ => zhHans ? "地址无效，需以 http:// 或 https:// 开头。" : zhHant ? "位址無效，需以 http:// 或 https:// 開頭。" : "Invalid address: it must start with http:// or https://.",
        };
    }
}
