using System.Diagnostics;
using System.IO;
using System.Windows;
using ControllerManager.Services;

namespace ControllerManager.Views;

public partial class CalibrationDialog : Window
{
    private readonly CalibrationRunner             _runner = new();
    private readonly CancellationTokenSource       _cts    = new();
    private readonly string                        _gameExePath;
    private readonly string                        _gameDisplayName;
    private Task<CalibrationRunner.Result>?        _runTask;
    private Process?                               _gameProcess;

    public CalibrationDialog(string gameExePath, string gameDisplayName)
    {
        InitializeComponent();
        _gameExePath     = gameExePath;
        _gameDisplayName = string.IsNullOrEmpty(gameDisplayName) ? "the game" : gameDisplayName;
        Title            = $"Timing test — {_gameDisplayName}";
        Closing         += OnClosing;

        if (string.IsNullOrWhiteSpace(_gameExePath) || !File.Exists(_gameExePath))
        {
            StartButton.IsEnabled = false;
            StatusText.Text       = "Profile has no valid game executable.";
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // Sequence: capture baseline → launch game → wait for user to click
        // Stop → terminate game → capture final → show results.
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility  = Visibility.Visible;
        StopButton.Content     = $"Stop {_gameDisplayName} and show results";
        StopButton.IsEnabled   = false;  // disabled until game actually launches
        StatusText.Text        = "Capturing baseline...";
        WaitingText.Text       = "Capturing baseline. Game will launch automatically.";

        // Launch the game as soon as the baseline is captured. The runner
        // fires BaselineCaptured on its background thread; marshal to UI.
        _runner.BaselineCaptured += OnBaselineCaptured;
        _runTask = Task.Run(() => _runner.RunAsync(_cts.Token));
    }

    private void OnBaselineCaptured()
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text  = "Baseline captured. Launching game...";
            WaitingText.Text = $"Game is running. Let it load through to a menu (~30s), then click Stop.";

            try
            {
                _gameProcess = Process.Start(new ProcessStartInfo
                {
                    FileName         = _gameExePath,
                    UseShellExecute  = true,
                    WorkingDirectory = Path.GetDirectoryName(_gameExePath) ?? "",
                });

                if (_gameProcess is null)
                {
                    StatusText.Text = "Game failed to launch (no process handle).";
                    return;
                }

                StopButton.IsEnabled = true;
                StatusText.Text      = $"Launched {_gameDisplayName} (PID {_gameProcess.Id}). Stop when ready.";
            }
            catch (Exception ex)
            {
                StatusText.Text      = $"Failed to launch game: {ex.Message}";
                StopButton.IsEnabled = true;  // user can still stop to see baseline-only results
            }
        });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StatusText.Text      = $"Stopping {_gameDisplayName}...";

        // Try to terminate the game on a worker thread (don't block UI). For
        // a measurement-only run we don't care about saved state, so a hard
        // kill after a short graceful attempt is fine.
        await Task.Run(() =>
        {
            try
            {
                if (_gameProcess is { HasExited: false } proc)
                {
                    try { proc.CloseMainWindow(); } catch { }
                    if (!proc.WaitForExit(3000))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteVerbose($"[Calibration] Game termination error: {ex.Message}");
            }
        });

        StatusText.Text = "Capturing final snapshot...";

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
        // Make sure the runner stops cleanly and the game is terminated if
        // the user closes the window without clicking Stop first.
        try { _cts.Cancel(); } catch { }
        try
        {
            if (_gameProcess is { HasExited: false } proc)
            {
                try { proc.CloseMainWindow(); } catch { }
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }
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
