using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class DevicesViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceEnumerator _enumerator;
    internal DeviceEnumerator Enumerator => _enumerator;

    private bool   _showAllHid;
    private bool   _isRefreshing;
    private string _statusText = "Ready.";
    private HidDevice? _selectedDevice;

    private HidInputMonitor? _monitor;
    private bool             _isMonitorExpanded;

    public ObservableCollection<HidDevice>      Devices { get; } = [];
    public ObservableCollection<AxisViewModel>  Axes    { get; } = [];
    public ObservableCollection<ButtonViewModel> Buttons { get; } = [];

    public bool IsMonitorExpanded
    {
        get => _isMonitorExpanded;
        set { if (Set(ref _isMonitorExpanded, value)) UpdateMonitor(); }
    }

    public bool ShowAllHid
    {
        get => _showAllHid;
        set { if (Set(ref _showAllHid, value)) Refresh(); }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (Set(ref _isRefreshing, value))
                Application.Current.Dispatcher.Invoke(
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public HidDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { if (Set(ref _selectedDevice, value)) UpdateMonitor(); }
    }

    public ICommand RefreshCommand           { get; }
    public ICommand ToggleEnabledCommand     { get; }
    public ICommand CopyAllCommand           { get; }
    public ICommand CopyFriendlyNameCommand  { get; }
    public ICommand CopyVidPidCommand        { get; }
    public ICommand CopyInstanceIdCommand    { get; }
    public ICommand CopyInterfacePathCommand { get; }

    public DevicesViewModel(DeviceEnumerator enumerator)
    {
        _enumerator = enumerator;

        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsRefreshing);
        CopyAllCommand = new RelayCommand(_ =>
        {
            if (Devices.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"HID Reorder — Device Dump  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine(new string('-', 80));
            foreach (var d in Devices)
                sb.AppendLine($"{(d.IsEnabled ? "ON " : "OFF")}  {d.VidPid,-22}  {d.FriendlyName,-55}  {d.InstanceId}");
            Clipboard.SetText(sb.ToString());
        }, _ => Devices.Count > 0);
        ToggleEnabledCommand = new RelayCommand(
            p => ToggleEnabled(p as HidDevice ?? SelectedDevice),
            p => (p as HidDevice ?? SelectedDevice) is not null);
        CopyFriendlyNameCommand  = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.FriendlyName));
        CopyVidPidCommand        = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.VidPid));
        CopyInstanceIdCommand    = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.InstanceId));
        CopyInterfacePathCommand = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.DeviceInterfacePath));

        Refresh();
    }

    public void Refresh()
    {
        IsRefreshing = true;
        StatusText   = "Scanning devices...";

        Task.Run(() =>
        {
            try
            {
                var list = _enumerator.GetAll(_showAllHid);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MergeDevices(list);
                    StatusText   = $"{list.Count} device(s) found.";
                    IsRefreshing = false;
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText   = $"Error: {ex.Message}";
                    IsRefreshing = false;
                });
            }
        });
    }

    private void MergeDevices(List<HidDevice> incoming)
    {
        // Remove devices that disappeared
        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!incoming.Any(d => d.InstanceId == Devices[i].InstanceId))
                Devices.RemoveAt(i);
        }

        // Update changed items and insert new ones
        for (int i = 0; i < incoming.Count; i++)
        {
            var next     = incoming[i];
            var existing = Devices.FirstOrDefault(d => d.InstanceId == next.InstanceId);

            if (existing is null)
            {
                Devices.Insert(Math.Min(i, Devices.Count), next);
            }
            else
            {
                existing.IsEnabled = next.IsEnabled;
            }
        }
    }

    private void ToggleEnabled(HidDevice? device)
    {
        if (device is null) return;

        IsRefreshing = true;
        StatusText   = device.IsEnabled
            ? $"Disabling {device.FriendlyName}..."
            : $"Enabling {device.FriendlyName}...";

        Task.Run(() =>
        {
            try
            {
                if (device.IsEnabled)
                    App.State.RecordDisabled(device);

                DeviceController.SetEnabled(device, !device.IsEnabled);

                if (!device.IsEnabled)
                    App.State.ClearEnabled(device);

                Application.Current.Dispatcher.Invoke(Refresh);
            }
            catch (Exception ex)
            {
                // Don't refresh on failure — keep the error visible in the status bar
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText   = $"Error: {ex.Message}";
                    IsRefreshing = false;
                });
            }
        });
    }

    private static void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    // ── Live input monitor ──────────────────────────────────────────────────────

    private void UpdateMonitor()
    {
        // Always tear down the previous monitor first so handlers and handles
        // from the old device are released before a new one is opened.
        StopMonitor();

        if (!_isMonitorExpanded) return;
        if (_selectedDevice is null) return;

        var path = _selectedDevice.DeviceInterfacePath;
        if (string.IsNullOrEmpty(path)) return;

        var monitor = new HidInputMonitor();
        if (!monitor.Open(path))
        {
            monitor.Dispose();
            return;
        }

        Axes.Clear();
        Buttons.Clear();
        foreach (var ax in monitor.Axes) Axes.Add(new AxisViewModel(ax.Name));
        for (int i = 1; i <= monitor.TotalButtonCount; i++)
            Buttons.Add(new ButtonViewModel(i.ToString()));

        monitor.AxesUpdated += OnAxesUpdated;
        monitor.ButtonsUpdated += OnButtonsUpdated;

        _monitor = monitor;
        _monitor.StartPolling(action =>
            Application.Current?.Dispatcher.BeginInvoke(action));
    }

    private void OnAxesUpdated(float[] values)
    {
        int n = Math.Min(values.Length, Axes.Count);
        for (int i = 0; i < n; i++)
        {
            Axes[i].Value   = values[i];
            Axes[i].RawText = values[i].ToString("0.00");
        }
    }

    private void OnButtonsUpdated(bool[] pressed)
    {
        int n = Math.Min(pressed.Length, Buttons.Count);
        for (int i = 0; i < n; i++)
            Buttons[i].IsPressed = pressed[i];
    }

    private void StopMonitor()
    {
        if (_monitor is not null)
        {
            _monitor.AxesUpdated    -= OnAxesUpdated;
            _monitor.ButtonsUpdated -= OnButtonsUpdated;
            _monitor.Stop();
            _monitor.Dispose();
            _monitor = null;
        }
        Axes.Clear();
        Buttons.Clear();
    }

    public void Dispose() => StopMonitor();
}
