using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// GOXF/DeskBox fork: the global 「整理方式」 choice (移动到格子 / 只映射，不移动文件)
/// at the same level as 「自动整理新文件」. Code-built inside one host panel so
/// the upstream section code and its pinned binding inventory stay untouched;
/// the value lives in <see cref="ForkOrganizeModeSettings"/> (own file), which
/// the main process watches and applies.
/// </summary>
public sealed partial class DesktopOrganizationSettingsSection
{
    private ComboBox? _forkOrganizeModeCombo;
    private InfoBar? _forkHiddenItemsInfo;
    private InfoBar? _forkModeStatusInfo;
    private bool _forkOrganizeModeRefreshing;

    private void ForkOrganizeModeHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_forkOrganizeModeCombo is null)
        {
            BuildForkOrganizeModeCard();
        }

        RefreshForkOrganizeMode();
    }

    private void BuildForkOrganizeModeCard()
    {
        _forkOrganizeModeCombo = new ComboBox { MinWidth = 180 };
        _forkOrganizeModeCombo.Items.Add(new ComboBoxItem
        {
            Content = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OptionMove),
            Tag = DesktopOrganizeMode.MoveToWidgets
        });
        _forkOrganizeModeCombo.Items.Add(new ComboBoxItem
        {
            Content = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.OptionMapOnly),
            Tag = DesktopOrganizeMode.MapOnly
        });
        AutomationProperties.SetName(_forkOrganizeModeCombo, ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SettingTitle));
        _forkOrganizeModeCombo.SelectionChanged += ForkOrganizeModeCombo_SelectionChanged;

        var card = new CommunityToolkit.WinUI.Controls.SettingsCard
        {
            Header = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SettingTitle),
            Description = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SettingDescription),
            HeaderIcon = new FontIcon { Glyph = "\uE8B7" },
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Content = _forkOrganizeModeCombo
        };

        _forkHiddenItemsInfo = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = false,
            Title = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.HiddenFilesShownTitle),
            Message = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.HiddenFilesShownBody)
        };

        _forkModeStatusInfo = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = false
        };

        ForkOrganizeModeHost.Children.Add(card);
        ForkOrganizeModeHost.Children.Add(new TextBlock
        {
            Name = "ForkMapOnlyNote",
            Text = ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.MapOnlyNote),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 16, 4),
            Foreground = SecondaryTextBrush(),
            Visibility = Visibility.Collapsed
        });
        ForkOrganizeModeHost.Children.Add(_forkHiddenItemsInfo);
        ForkOrganizeModeHost.Children.Add(_forkModeStatusInfo);
    }

    private void RefreshForkOrganizeMode()
    {
        if (_forkOrganizeModeCombo is null)
        {
            return;
        }

        DesktopOrganizeMode mode = ForkOrganizeModeSettings.Load();
        _forkOrganizeModeRefreshing = true;
        _forkOrganizeModeCombo.SelectedIndex = mode == DesktopOrganizeMode.MapOnly ? 1 : 0;
        _forkOrganizeModeRefreshing = false;
        ApplyForkOrganizeModeDetails(mode);
    }

    private void ApplyForkOrganizeModeDetails(DesktopOrganizeMode mode)
    {
        bool mapOnly = mode == DesktopOrganizeMode.MapOnly;
        foreach (UIElement child in ForkOrganizeModeHost.Children)
        {
            if (child is TextBlock { Name: "ForkMapOnlyNote" } note)
            {
                note.Foreground = SecondaryTextBrush();
                note.Visibility = mapOnly ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (_forkHiddenItemsInfo is not null)
        {
            _forkHiddenItemsInfo.IsOpen = mapOnly && ExplorerHiddenItemsSetting.IsShowingHiddenItems();
        }
    }

    private void ForkOrganizeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_forkOrganizeModeRefreshing ||
            _forkOrganizeModeCombo?.SelectedItem is not ComboBoxItem { Tag: DesktopOrganizeMode mode })
        {
            return;
        }

        if (mode == ForkOrganizeModeSettings.Load())
        {
            ApplyForkOrganizeModeDetails(mode);
            return;
        }

        bool saved = ForkOrganizeModeSettings.TrySave(mode);
        if (!saved)
        {
            RefreshForkOrganizeMode();
        }
        else
        {
            ApplyForkOrganizeModeDetails(mode);
        }

        if (_forkModeStatusInfo is null)
        {
            return;
        }

        _forkModeStatusInfo.Severity = saved ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        _forkModeStatusInfo.Title = saved
            ? ForkOrganizeModeText.Get(mode == DesktopOrganizeMode.MapOnly
                ? ForkOrganizeModeText.Key.SwitchedToMapTitle
                : ForkOrganizeModeText.Key.SwitchedToMoveTitle)
            : ForkOrganizeModeText.Get(ForkOrganizeModeText.Key.SaveFailed);
        _forkModeStatusInfo.Message = saved
            ? ForkOrganizeModeText.Get(mode == DesktopOrganizeMode.MapOnly
                ? ForkOrganizeModeText.Key.SwitchedToMapBody
                : ForkOrganizeModeText.Key.SwitchedToMoveBody)
            : string.Empty;
        _forkModeStatusInfo.IsOpen = true;
    }
}
