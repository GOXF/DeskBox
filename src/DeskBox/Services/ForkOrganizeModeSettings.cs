using System.Text.Json;

namespace DeskBox.Services;

/// <summary>How 「开始整理」 and automatic organization treat desktop items (GOXF fork).</summary>
public enum DesktopOrganizeMode
{
    /// <summary>Upstream behavior: files are moved into the widgets' folders.</summary>
    MoveToWidgets = 0,

    /// <summary>「映射整理」: files stay in the Desktop folder; widgets only record membership.</summary>
    MapOnly = 1,
}

/// <summary>What the main process does when it observes the organize mode.</summary>
public enum OrganizeModeTransitionAction
{
    None,

    /// <summary>Leave mapped organize: dissolve every mapped widget and restore hidden desktop items.</summary>
    DissolveMapping,
}

/// <summary>
/// GOXF/DeskBox fork: the global 「整理方式」 setting (settings page → 桌面整理).
/// Stored in its own small file next to <c>fork-update.json</c> instead of the
/// upstream settings model, so the settings process writes it and the main
/// process reads it at use time without new IPC or serializer surface, and
/// upstream settings changes never conflict with it.
/// </summary>
public static class ForkOrganizeModeSettings
{
    internal const string FileName = "fork-organize.json";
    private const string ModeProperty = "organizeMode";
    private const string MapOnlyValue = "mapOnly";
    private const string MoveValue = "move";


    /// <summary>Raised in the main process (by the mapped-organize service's file watcher) after the mode changed on disk.</summary>
    public static event Action<DesktopOrganizeMode>? Changed;

    internal static string FilePath =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, FileName);

    public static bool Exists()
    {
        try
        {
            return File.Exists(FilePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Current mode; anything missing or unreadable is the upstream default (move).</summary>
    public static DesktopOrganizeMode Load()
    {
        try
        {
            return Load(FilePath);
        }
        catch (Exception)
        {
            return DesktopOrganizeMode.MoveToWidgets;
        }
    }

    internal static DesktopOrganizeMode Load(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? Parse(File.ReadAllText(filePath)) : DesktopOrganizeMode.MoveToWidgets;
        }
        catch (Exception)
        {
            return DesktopOrganizeMode.MoveToWidgets;
        }
    }

    public static bool TrySave(DesktopOrganizeMode mode)
    {
        try
        {
            return TrySave(mode, FilePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TrySave(DesktopOrganizeMode mode, string filePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, Serialize(mode));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static DesktopOrganizeMode Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DesktopOrganizeMode.MoveToWidgets;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(ModeProperty, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String &&
                   string.Equals(value.GetString(), MapOnlyValue, StringComparison.OrdinalIgnoreCase)
                ? DesktopOrganizeMode.MapOnly
                : DesktopOrganizeMode.MoveToWidgets;
        }
        catch (JsonException)
        {
            return DesktopOrganizeMode.MoveToWidgets;
        }
    }

    public static string Serialize(DesktopOrganizeMode mode) =>
        "{\"" + ModeProperty + "\":\"" + (mode == DesktopOrganizeMode.MapOnly ? MapOnlyValue : MoveValue) + "\"}";

    internal static void RaiseChanged(DesktopOrganizeMode mode) => Changed?.Invoke(mode);
}

/// <summary>Pure decisions around the organize mode (unit-tested in ForkOrganizeModeTests).</summary>
public static class ForkOrganizeModePolicy
{
    /// <summary>「开始整理」 in the preview: map instead of move?</summary>
    public static bool ConfirmMapsInsteadOfMoving(DesktopOrganizeMode mode) => mode == DesktopOrganizeMode.MapOnly;

    /// <summary>May the upstream automatic organizer move a new desktop file?</summary>
    public static bool AutoOrganizerMayMoveFiles(DesktopOrganizeMode mode) => mode == DesktopOrganizeMode.MoveToWidgets;

    /// <summary>The settings page switched the mode while DeskBox runs.</summary>
    public static OrganizeModeTransitionAction OnModeChanged(
        DesktopOrganizeMode previous,
        DesktopOrganizeMode current,
        bool hasMappedWidgets) =>
        previous == DesktopOrganizeMode.MapOnly &&
        current == DesktopOrganizeMode.MoveToWidgets &&
        hasMappedWidgets
            ? OrganizeModeTransitionAction.DissolveMapping
            : OrganizeModeTransitionAction.None;

    /// <summary>
    /// Startup. Installs that used the old per-preview checkbox have mapped
    /// widgets but no mode file yet: adopt 「只映射」 for them (returned as the
    /// mode to persist). A stored 「移动到格子」 with mapped widgets left over
    /// means the switch was not applied while running: leave mapped organize.
    /// </summary>
    public static (DesktopOrganizeMode Mode, DesktopOrganizeMode? Persist, OrganizeModeTransitionAction Action) OnStartup(
        bool modeFileExists,
        DesktopOrganizeMode storedMode,
        bool hasMappedWidgets)
    {
        if (!modeFileExists)
        {
            return hasMappedWidgets
                ? (DesktopOrganizeMode.MapOnly, DesktopOrganizeMode.MapOnly, OrganizeModeTransitionAction.None)
                : (DesktopOrganizeMode.MoveToWidgets, null, OrganizeModeTransitionAction.None);
        }

        return storedMode == DesktopOrganizeMode.MoveToWidgets && hasMappedWidgets
            ? (storedMode, null, OrganizeModeTransitionAction.DissolveMapping)
            : (storedMode, null, OrganizeModeTransitionAction.None);
    }
}

/// <summary>
/// Fork text for the organize-mode setting and 「放回桌面」. Inline zh-Hans /
/// zh-Hant / English (other UI languages fall back to English) for the same
/// merge-conflict reason as SettingsViewModel.ForkUpstream.cs: no upstream
/// Strings/*.json edits.
/// </summary>
public static partial class ForkOrganizeModeText
{
    public enum Key
    {
        SettingTitle,
        SettingDescription,
        OptionMove,
        OptionMapOnly,
        MapOnlyNote,
        HiddenFilesShownTitle,
        HiddenFilesShownBody,
        SwitchedToMoveTitle,
        SwitchedToMoveBody,
        SwitchedToMapTitle,
        SwitchedToMapBody,
        SaveFailed,
        PreviewModeFormat,
        PreviewChange,
        PreviewTooltipMove,
        PreviewTooltipMapOnly,
        MenuKeepOnDesktop,
        KeptOnDesktopFormat,
        MenuReorganizeDesktop,
        MenuOrganizePreview,
        OneClickDoneTitle,
        OneClickDoneFormat,
        OneClickPartialFormat,
        OneClickNothingTitle,
        OneClickNothingBody,
        OneClickFailedTitle,
        OneClickFailedBody,
    }

    /// <summary>UI-culture lookup lives in Helpers/ForkOrganizeModeText.Culture.cs (no App globals in Services).</summary>
    public static string Get(Key key) => Get(CurrentCulture(), key);

    public static string Get(string? cultureName, Key key)
    {
        bool hans = cultureName == LocalizationService.LanguageChinese;
        bool hant = cultureName == LocalizationService.LanguageChineseTraditional;
        string Pick(string zhHans, string zhHant, string en) => hans ? zhHans : hant ? zhHant : en;
        return key switch
        {
            Key.SettingTitle => Pick("整理方式", "整理方式", "Organize mode"),
            Key.SettingDescription => Pick(
                "一键整理桌面、整理预览和自动整理新文件按此方式执行。",
                "一鍵整理桌面、整理預覽和自動整理新檔案按此方式執行。",
                "Used by Organize desktop, the organize preview and automatic organization of new files."),
            Key.OptionMove => Pick("移动到格子", "移動到格子", "Move into widgets"),
            Key.OptionMapOnly => Pick("只映射，不移动文件", "只映射，不移動檔案", "Map only, don't move files"),
            Key.MapOnlyNote => Pick(
                "文件留在桌面文件夹中，格子只记录归属；被映射的项会在桌面上隐藏（设置隐藏属性，退出映射、切回移动或退出 DeskBox 时还原）。映射格子里的项可右键「放回桌面」。新文件由映射格子自动归类，可在映射格子空白处右键切换自动/手动。",
                "檔案留在桌面資料夾中，格子只記錄歸屬；被映射的項目會在桌面上隱藏（設定隱藏屬性，退出映射、切回移動或結束 DeskBox 時還原）。映射格子裡的項目可按右鍵「放回桌面」。新檔案由映射格子自動歸類，可在映射格子空白處按右鍵切換自動/手動。",
                "Files stay in the Desktop folder and widgets only record membership. Mapped items are hidden on the desktop (hidden attribute, restored when mapping ends, you switch back to moving, or DeskBox exits). Right-click an item in a mapped widget to put it back on the desktop. New files are sorted into mapped widgets automatically; switch auto/manual from a mapped widget's blank-area menu."),
            Key.HiddenFilesShownTitle => Pick(
                "资源管理器正在显示隐藏的项目",
                "檔案總管正在顯示隱藏的項目",
                "File Explorer is showing hidden items"),
            Key.HiddenFilesShownBody => Pick(
                "映射模式通过隐藏属性在桌面上隐藏被映射的项。当前开启了「显示隐藏的项目」，这些项仍会以半透明图标显示在桌面上。可在资源管理器「查看 → 显示 → 隐藏的项目」中关闭。",
                "映射模式透過隱藏屬性在桌面上隱藏被映射的項目。目前開啟了「顯示隱藏的項目」，這些項目仍會以半透明圖示顯示在桌面上。可在檔案總管「檢視 → 顯示 → 隱藏的項目」中關閉。",
                "Map-only mode hides mapped items on the desktop with the hidden attribute. \"Show hidden items\" is on, so they still appear as faded icons. Turn it off in File Explorer under View → Show → Hidden items."),
            Key.SwitchedToMoveTitle => Pick("已切换为「移动到格子」", "已切換為「移動到格子」", "Switched to Move into widgets"),
            Key.SwitchedToMoveBody => Pick(
                "已有的映射格子会退出，被隐藏的桌面项会恢复显示（文件本身没有移动过）。",
                "已有的映射格子會退出，被隱藏的桌面項目會恢復顯示（檔案本身沒有移動過）。",
                "Existing mapped widgets are dissolved and hidden desktop items become visible again (the files were never moved)."),
            Key.SwitchedToMapTitle => Pick("已切换为「只映射，不移动文件」", "已切換為「只映射，不移動檔案」", "Switched to Map only"),
            Key.SwitchedToMapBody => Pick(
                "下次「开始整理」会创建映射格子，文件不会被移动。",
                "下次「開始整理」會建立映射格子，檔案不會被移動。",
                "The next Start organizing creates mapped widgets; no file is moved."),
            Key.SaveFailed => Pick("无法保存整理方式。", "無法儲存整理方式。", "Couldn't save the organize mode."),
            Key.PreviewModeFormat => Pick("整理方式：{0}", "整理方式：{0}", "Mode: {0}"),
            Key.PreviewChange => Pick("（在设置中更改）", "（在設定中變更）", "(change in Settings)"),
            Key.PreviewTooltipMove => Pick(
                "「开始整理」会把文件移动到格子。可在 设置 → 桌面整理 → 整理方式 中改为只映射。",
                "「開始整理」會把檔案移動到格子。可在 設定 → 桌面整理 → 整理方式 中改為只映射。",
                "Start organizing moves files into widgets. Change it under Settings → Desktop organization → Organize mode."),
            Key.PreviewTooltipMapOnly => Pick(
                "「开始整理」只创建映射格子，文件留在桌面文件夹中，被映射的项在桌面上隐藏。可在 设置 → 桌面整理 → 整理方式 中更改。",
                "「開始整理」只建立映射格子，檔案留在桌面資料夾中，被映射的項目在桌面上隱藏。可在 設定 → 桌面整理 → 整理方式 中變更。",
                "Start organizing only creates mapped widgets: files stay in the Desktop folder and mapped items are hidden on the desktop. Change it under Settings → Desktop organization → Organize mode."),
            Key.MenuKeepOnDesktop => Pick("放回桌面", "放回桌面", "Put back on desktop"),
            Key.KeptOnDesktopFormat => Pick(
                "已将 {0} 个项目放回桌面，自动归类不会再收回它们；点「一键整理」会把它们一并整理进格子。",
                "已將 {0} 個項目放回桌面，自動歸類不會再收回它們；點「一鍵整理」會把它們一併整理進格子。",
                "Put {0} items back on the desktop. Automatic sorting leaves them there; Organize desktop sorts them into widgets again."),
            Key.MenuReorganizeDesktop => Pick("一键整理桌面", "一鍵整理桌面", "Organize desktop"),
            Key.MenuOrganizePreview => Pick("整理预览…", "整理預覽…", "Organize with preview…"),
            Key.OneClickDoneTitle => Pick("桌面已整理", "桌面已整理", "Desktop organized"),
            Key.OneClickDoneFormat => Pick(
                "已整理 {0} 个项目（{1}）。",
                "已整理 {0} 個項目（{1}）。",
                "Organized {0} items ({1})."),
            Key.OneClickPartialFormat => Pick(
                "已整理 {0} 个项目，{1} 个项目未能整理（可在「整理预览…」中查看）。",
                "已整理 {0} 個項目，{1} 個項目未能整理（可在「整理預覽…」中查看）。",
                "Organized {0} items; {1} items couldn't be organized (see Organize with preview…)."),
            Key.OneClickNothingTitle => Pick("桌面已经很整洁", "桌面已經很整潔", "Desktop is already tidy"),
            Key.OneClickNothingBody => Pick(
                "没有需要整理的项目。",
                "沒有需要整理的項目。",
                "There is nothing to organize."),
            Key.OneClickFailedTitle => Pick("整理桌面失败", "整理桌面失敗", "Couldn't organize the desktop"),
            Key.OneClickFailedBody => Pick(
                "可以从托盘菜单「整理预览…」打开预览重试。",
                "可以從系統匣選單「整理預覽…」開啟預覽重試。",
                "Open Organize with preview… from the tray menu to try again."),
            _ => key.ToString(),
        };
    }

    public static string Format(Key key, params object[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), values);

    public static string ModeName(DesktopOrganizeMode mode) =>
        Get(mode == DesktopOrganizeMode.MapOnly ? Key.OptionMapOnly : Key.OptionMove);

}

/// <summary>Reads Explorer's 「显示隐藏的项目」 (HKCU …\Explorer\Advanced\Hidden: 1 = show).</summary>
public static class ExplorerHiddenItemsSetting
{
    public static bool IsShowingHiddenItems()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return IsShowValue(key?.GetValue("Hidden"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Explorer writes 1 for 「显示」 and 2 for 「不显示」.</summary>
    public static bool IsShowValue(object? value) => value switch
    {
        int number => number == 1,
        long number => number == 1,
        string text => text.Trim() == "1",
        _ => false,
    };
}
