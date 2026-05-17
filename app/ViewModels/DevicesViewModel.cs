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

    public ICommand RefreshCommand       { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand CopyInstanceIdCommand { get; }
    public ICommand CopyInterfacePathCommand { get; }

    public DevicesViewModel(DeviceEnumerator enumerator)
    {
        _enumerator = enumerator;

        RefreshCommand       = new RelayCommand(_ => Refresh(), _ => !IsRefreshing);
        ToggleEnabledCommand = new RelayCommand(
            p => ToggleEnabled(p as HidDevice ?? SelectedDevice),
            p => (p as HidDevice ?? SelectedDevice) is not null && !IsRefreshing);
        CopyInstanceIdCommand = new RelayCommand(
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
                    Devices.Clear();
                    foreach (var d in list) Devices.Add(d);
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
