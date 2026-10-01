using System.Diagnostics;
using System.Windows;
using ControllerManager.Services;

namespace ControllerManager.Views;

/// <summary>
/// First-launch prompt shown when the HidHide driver is missing. Offers a
/// one-click silent install (see <see cref="HidHideInstaller"/>). Set
/// <see cref="Installed"/> is true when the install reported success, so the
/// caller knows to re-probe the driver.
/// </summary>
public partial class HidHideInstallDialog : Window
{
    /// <summary>True when HidHide was installed successfully during this dialog.</summary>
    public bool Installed { get; private set; }

    /// <summary>True when the user ticked "Don't remind me again".</summary>
    public bool DontRemindAgain => DontRemindCheckbox.IsChecked == true;

    public HidHideInstallDialog()
    {
        InitializeComponent();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled       = false;
        SkipButton.IsEnabled          = false;
        DontRemindCheckbox.IsEnabled  = false;
        StatusPanel.Visibility        = Visibility.Visible;

        var progress = new Progress<string>(msg => StatusText.Text = msg);
        var result   = await HidHideInstaller.InstallAsync(progress);

        Progress.IsIndeterminate = false;
        StatusText.Text          = result.Message;

        if (result.Success)
        {
            Progress.Value = 100;
            Installed      = true;
            // Brief pause so the success message is readable, then close.
            await Task.Delay(1200);
            DialogResult = true;
            return;
        }

        // Failure — let the user retry or fall back to the manual download page.
        Progress.Value               = 0;
        InstallButton.IsEnabled      = true;
        SkipButton.IsEnabled         = true;
        DontRemindCheckbox.IsEnabled = true;
        InstallButton.Content        = "Retry";
        SkipButton.Content      = "Open download page";
        SkipButton.Click       -= Skip_Click;
        SkipButton.Click       += OpenDownloadPage_Click;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(HidHideInstaller.ReleasesPage)
                { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.WriteException("HidHideInstallDialog.OpenDownloadPage", ex); }
        DialogResult = false;
    }
}
