using System.Windows;
using ControllerManager.Services;

namespace ControllerManager.Views;

public partial class CalibrationDialog : Window
{
    private readonly CalibrationRunner             _runner = new();
    private readonly CancellationTokenSource       _cts    = new();
    private Task<CalibrationRunner.Result>?        _runTask;

    public CalibrationDialog()
    {
        InitializeComponent();
        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Kick off the test on a background task. The runner captures the
        // baseline snapshot immediately, then waits on the cancellation token.
        StatusText.Text = "Baseline snapshot in progress...";
        _runTask = Task.Run(() => _runner.RunAsync(_cts.Token));

        // Update status once baseline is done (we don't have a direct hook,
        // but a short delay is fine — baseline capture is sub-second in
        // practice).
        Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (StopButton.IsEnabled)
                    StatusText.Text = "Baseline ready. Play the game now.";
            });
        });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StatusText.Text      = "Capturing final snapshot...";

        // Cancel the runner's internal wait so it proceeds to the final
        // rundown, then await its result.
        _cts.Cancel();

        if (_runTask is null) { StatusText.Text = "Run task missing — internal error."; return; }
        CalibrationRunner.Result result;
        try
        {
            result = await _runTask;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
            ShowCloseButton();
            return;
        }

        ShowResults(result);
    }

    private void ShowResults(CalibrationRunner.Result result)
    {
        HeadlineText.Text = "Results";
        BodyText.Text     = $"Snapshot interval: {(result.FinalAt - result.BaselineAt).TotalSeconds:0.0}s. Devices are sorted by how many input reports the system pulled during the test — biggest mover is the most-used device, typically the game's slot #1 wheel.";

        WaitingText.Visibility = Visibility.Collapsed;
        ResultsGrid.Visibility = Visibility.Visible;

        var rows = result.Devices
            .Select(d => new ResultRow(d))
            .ToList();
        ResultsGrid.ItemsSource = rows;

        StatusText.Text = "";
        ShowCloseButton();
    }

    private void ShowCloseButton()
    {
        StopButton.Visibility  = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Make sure the runner stops cleanly if the user closes the window
        // without clicking Stop first.
        try { _cts.Cancel(); } catch { }
    }

    // ── Row view-model for the DataGrid ──────────────────────────────────────

    private sealed class ResultRow
    {
        public ResultRow(CalibrationRunner.DeviceActivity a)
        {
            DeviceDescription = string.IsNullOrEmpty(a.DeviceDescription)
                ? a.DeviceInstancePath
                : a.DeviceDescription;
            VidPidDisplay     = a.VendorId == 0 && a.ProductId == 0
                ? "—"
                : $"{a.VendorId:X4}:{a.ProductId:X4}";
            ReadsDelta        = a.ReadsDelta;
            LastReadDisplay   = a.LastReadAtUtc is null
                ? "—"
                : a.LastReadAtUtc.Value.ToLocalTime().ToString("HH:mm:ss");
        }

        public string DeviceDescription { get; }
        public string VidPidDisplay     { get; }
        public long   ReadsDelta        { get; }
        public string LastReadDisplay   { get; }
    }
}
