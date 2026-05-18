using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class DevicesViewModel : ViewModelBase
{
    private readonly DeviceEnumerator _enumerator;

    private bool   _showAllHid;
    private bool   _isRefreshing;
    private string _statusText = "Ready.";
    private HidDevice? _selectedDevice;

    public ObservableCollection<HidDevice> Devices { get; } = [];

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
        set => Set(ref _selectedDevice, value);
    }

    public ICommand RefreshCommand           { get; }
    public ICommand ToggleEnabledCommand     { get; }
    public ICommand CopyFriendlyNameCommand  { get; }
    public ICommand CopyVidPidCommand        { get; }
    public ICommand CopyInstanceIdCommand    { get; }
    public ICommand CopyInterfacePathCommand { get; }

    public DevicesViewModel(DeviceEnumerator enumerator)
    {
        _enumerator = enumerator;

        RefreshCommand       = new RelayCommand(_ => Refresh(), _ => !IsRefreshing);
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
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = $"Error: {ex.Message}");
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(Refresh);
            }
        });
    }

    private static void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }
}
