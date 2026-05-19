using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class HandleWatcherViewModel : ViewModelBase, IDisposable
{
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HandleWatcher                    _watcher;
    private readonly VidResolver                      _resolver = new();
    private readonly ObservableCollection<HidDevice>? _liveDevices;

    private string _processName  = "gameinputsvc";
    private string _statusText   = "Not watching.";
    private bool   _isWatching;
    private bool   _diagnosticMode;
    private string _pollStats    = "";

    public string ProcessName
    {
        get => _processName;
        set => Set(ref _processName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsWatching
    {
        get => _isWatching;
        private set => Set(ref _isWatching, value);
    }

    public bool DiagnosticMode
    {
        get => _diagnosticMode;
        set { Set(ref _diagnosticMode, value); _watcher.DiagnosticMode = value; }
    }

    public string PollStats
    {
        get => _pollStats;
        private set => Set(ref _pollStats, value);
    }

    public ObservableCollection<HandleWatcherEntry> Events { get; } = [];

    public ICommand StartCommand   { get; }
    public ICommand StopCommand    { get; }
    public ICommand ClearCommand   { get; }
    public ICommand CopyAllCommand { get; }

    public HandleWatcherViewModel(ObservableCollection<HidDevice>? liveDevices = null)
    {
        _watcher     = new HandleWatcher();
        _liveDevices = liveDevices;
        StartCommand   = new RelayCommand(_ => StartWatching(), _ => !IsWatching);
        StopCommand    = new RelayCommand(_ => StopWatching(),  _ =>  IsWatching);
        ClearCommand   = new RelayCommand(_ => Events.Clear());
        CopyAllCommand = new RelayCommand(_ =>
        {
            if (Events.Count > 0)
                Clipboard.SetText(string.Join(Environment.NewLine,
                    Events.Select(e => $"{e.Timestamp}  {e.DeviceName,-28}  {e.Path}")));
        });

        _watcher.HidHandleOpened += (_, e) =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                Events.Insert(0, new HandleWatcherEntry(
                    Timestamp:  $"{e.Timestamp:HH:mm:ss.fff}",
                    DeviceName: ResolveName(e.DevicePath),
                    Path:       e.DevicePath));
                if (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
            });

        _watcher.PollStats += (_, stats) =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (stats.total == -1)
                    PollStats = "Failed to open process (OpenProcess denied — check admin rights)";
                else if (stats.ntStatus != 0)
                    PollStats = $"NtQueryInformationProcess failed: 0x{stats.ntStatus:X8}";
                else
                    PollStats = $"Scanning: {stats.total} handles, {stats.named} named";
            });
    }

    private string ResolveName(string path)
    {
        var m = VidPidRx.Match(path);
        if (!m.Success) return "";

        var vid = m.Groups[1].Value.ToUpperInvariant();
        var pid = m.Groups[2].Value.ToUpperInvariant();

        // Prefer the full display name from the live device list
        var live = _liveDevices?.FirstOrDefault(d =>
            d.VendorId.Equals(vid, StringComparison.OrdinalIgnoreCase) &&
            d.ProductId.Equals(pid, StringComparison.OrdinalIgnoreCase));

        return live?.FriendlyName ?? _resolver.Resolve(vid, pid);
    }

    private void StartWatching()
    {
        IsWatching = true;
        StatusText = $"Waiting for '{_processName}'...";

        var targetName = _processName.Trim().ToLowerInvariant().Replace(".exe", "");

        Task.Run(async () =>
        {
            while (true)
            {
                if (!IsWatching) return;

                var proc = Process.GetProcessesByName(targetName).FirstOrDefault();
                if (proc is not null)
                {
                    _watcher.Start(proc.Id);
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusText = $"Watching PID {proc.Id} ({proc.ProcessName}.exe)");
                    return;
                }

                await Task.Delay(500);
            }
        });
    }

    private void StopWatching()
    {
        _watcher.Stop();
        IsWatching = false;
        StatusText = "Stopped.";
    }

    public void Dispose() => _watcher.Dispose();
}
