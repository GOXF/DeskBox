using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// GOXF/DeskBox fork: "update and restart" for a local dev build. Starts
/// scripts/fork-dev-update.ps1 in its own visible window (pull dev → Debug
/// build → relaunch), then shuts DeskBox down the same way an installer
/// update does so the script can replace the binaries.
/// </summary>
public sealed partial class SettingsWindow
{
    private async void ForkDevUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        if (!ViewModel.StartForkDevUpdateScript(out string message))
        {
            if (sender is Button failedButton)
            {
                failedButton.IsEnabled = true;
            }

            await ShowInfoDialogAsync(ViewModel.ForkDevUpdateButtonText, message);
            return;
        }

        await _appActions.ShutdownForUpdateAsync();
    }
}
