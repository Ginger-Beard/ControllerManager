using System.Collections.ObjectModel;
using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class ProfileEditorViewModel : ViewModelBase
{
    private string _name         = "";
    private string _exePath      = "";
    private string _exeName      = "";
    private TriggerMode _trigger = TriggerMode.HandleWatcher;
    private int    _timerSeconds = 30;
    private bool   _isDirty;

    private HidDevice? _selectedAvailable;
    private DeviceRef? _selectedKeep;
    private DeviceRef? _selectedDisable;
    private DeviceRef? _selectedKeepDisabled;

    public Guid? ProfileId { get; private set; }

    public string Name
    {
        get => _name;
        set { Set(ref _name, value); IsDirty = true; }
    }

    public string ExePath
    {
        get => _exePath;
        set { Set(ref _exePath, value); ExeName = Path.GetFileName(value); IsDirty = true; }
    }

    public string ExeName
    {
        get => _exeName;
        private set => Set(ref _exeName, value);
    }

    public TriggerMode TriggerMode
    {
        get => _trigger;
        set { Set(ref _trigger, value); IsDirty = true; }
    }

    public int TimerSeconds
    {
        get => _timerSeconds;
        set { Set(ref _timerSeconds, Math.Clamp(value, 5, 300)); IsDirty = true; }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    // Live device list from DevicesViewModel — source for the picker (filtered view)
    public ObservableCollection<HidDevice> AllDevices { get; }

    // Full device list used when ShowAllHid is on — populated on demand
    private readonly ObservableCollection<HidDevice> _allDevicesExpanded = [];
    private readonly DeviceEnumerator                _enumerator;
    private bool                                     _showAllHid;

    public bool ShowAllHid
    {
        get => _showAllHid;
        set
        {
            if (!Set(ref _showAllHid, value)) return;
            if (value) RefreshExpanded();
            else       _allDevicesExpanded.Clear();
            OnPropertyChanged(nameof(UnassignedDevices));
        }
    }

    private void RefreshExpanded()
    {
        Task.Run(() =>
        {
            var list = _enumerator.GetAll(showAllHid: true);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _allDevicesExpanded.Clear();
                foreach (var d in list) _allDevicesExpanded.Add(d);
                OnPropertyChanged(nameof(UnassignedDevices));
            });
        });
    }

    public ObservableCollection<DeviceRef> KeepEnabled       { get; } = [];
    public ObservableCollection<DeviceRef> DisableThenRestore { get; } = [];
    public ObservableCollection<DeviceRef> KeepDisabled       { get; } = [];

    // Devices not yet assigned to any list — shown in the picker
    public IEnumerable<HidDevice> UnassignedDevices =>
        (_showAllHid ? _allDevicesExpanded : (IEnumerable<HidDevice>)AllDevices)
            .Where(d => !IsAssigned(d));

    public HidDevice? SelectedAvailable
    {
        get => _selectedAvailable;
        set => Set(ref _selectedAvailable, value);
    }
    public DeviceRef? SelectedKeep
    {
        get => _selectedKeep;
        set => Set(ref _selectedKeep, value);
    }
    public DeviceRef? SelectedDisable
    {
        get => _selectedDisable;
        set => Set(ref _selectedDisable, value);
    }
    public DeviceRef? SelectedKeepDisabled
    {
        get => _selectedKeepDisabled;
        set => Set(ref _selectedKeepDisabled, value);
    }

    public ICommand AddToKeepCommand          { get; }
    public ICommand AddToDisableCommand       { get; }
    public ICommand AddToKeepDisabledCommand  { get; }
    public ICommand RemoveKeepCommand         { get; }
    public ICommand RemoveDisableCommand      { get; }
    public ICommand RemoveKeepDisabledCommand { get; }
    public ICommand MoveDisableUpCommand      { get; }
    public ICommand MoveDisableDownCommand    { get; }

    public ProfileEditorViewModel(ObservableCollection<HidDevice> allDevices, DeviceEnumerator enumerator)
    {
        AllDevices  = allDevices;
        _enumerator = enumerator;

        void RefreshUnassigned(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(UnassignedDevices));

        AllDevices.CollectionChanged             += RefreshUnassigned;
        _allDevicesExpanded.CollectionChanged    += RefreshUnassigned;
        KeepEnabled.CollectionChanged            += RefreshUnassigned;
        DisableThenRestore.CollectionChanged     += RefreshUnassigned;
        KeepDisabled.CollectionChanged      += RefreshUnassigned;

        AddToKeepCommand = new RelayCommand(
            _ => AddTo(SelectedAvailable, KeepEnabled),
            _ => SelectedAvailable is not null && !IsAssigned(SelectedAvailable));

        AddToDisableCommand = new RelayCommand(
            _ => AddTo(SelectedAvailable, DisableThenRestore),
            _ => SelectedAvailable is not null && !IsAssigned(SelectedAvailable));

        AddToKeepDisabledCommand = new RelayCommand(
            _ => AddTo(SelectedAvailable, KeepDisabled),
            _ => SelectedAvailable is not null && !IsAssigned(SelectedAvailable));

        RemoveKeepCommand = new RelayCommand(
            _ => Remove(KeepEnabled, SelectedKeep),
            _ => SelectedKeep is not null);

        RemoveDisableCommand = new RelayCommand(
            _ => Remove(DisableThenRestore, SelectedDisable),
            _ => SelectedDisable is not null);

        RemoveKeepDisabledCommand = new RelayCommand(
            _ => Remove(KeepDisabled, SelectedKeepDisabled),
            _ => SelectedKeepDisabled is not null);

        MoveDisableUpCommand = new RelayCommand(
            _ => MoveDisable(-1),
            _ => SelectedDisable is not null && DisableThenRestore.IndexOf(SelectedDisable) > 0);

        MoveDisableDownCommand = new RelayCommand(
            _ => MoveDisable(+1),
            _ => SelectedDisable is not null &&
                 DisableThenRestore.IndexOf(SelectedDisable) < DisableThenRestore.Count - 1);
    }

    private bool IsAssigned(HidDevice d) =>
        KeepEnabled.Any(r => r.InstanceId == d.InstanceId) ||
        DisableThenRestore.Any(r => r.InstanceId == d.InstanceId) ||
        KeepDisabled.Any(r => r.InstanceId == d.InstanceId);

    private void AddTo(HidDevice? device, ObservableCollection<DeviceRef> list)
    {
        if (device is null || IsAssigned(device)) return;
        list.Add(new DeviceRef { InstanceId = device.InstanceId, FriendlyName = device.FriendlyName });
        IsDirty = true;
    }

    private void Remove(ObservableCollection<DeviceRef> list, DeviceRef? item)
    {
        if (item is null) return;
        list.Remove(item);
        IsDirty = true;
    }

    private void MoveDisable(int delta)
    {
        if (SelectedDisable is null) return;
        int idx = DisableThenRestore.IndexOf(SelectedDisable);
        int to  = idx + delta;
        if (to < 0 || to >= DisableThenRestore.Count) return;
        DisableThenRestore.Move(idx, to);
        IsDirty = true;
    }

    public void LoadProfile(Profile p)
    {
        ProfileId    = p.Id;
        _name        = p.Name;
        _exePath     = p.GameExecutablePath;
        _exeName     = p.GameExecutableName;
        _trigger     = p.TriggerMode;
        _timerSeconds = p.TimerSeconds;

        KeepEnabled.Clear();        foreach (var d in p.KeepEnabled)       KeepEnabled.Add(d);
        DisableThenRestore.Clear(); foreach (var d in p.DisableThenRestore) DisableThenRestore.Add(d);
        KeepDisabled.Clear();       foreach (var d in p.KeepDisabled)       KeepDisabled.Add(d);

        IsDirty = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ExePath));
        OnPropertyChanged(nameof(ExeName));
        OnPropertyChanged(nameof(TriggerMode));
        OnPropertyChanged(nameof(TimerSeconds));
    }

    public Profile ToProfile() => new()
    {
        Id                 = ProfileId ?? Guid.NewGuid(),
        Name               = _name,
        GameExecutablePath = _exePath,
        GameExecutableName = _exeName,
        TriggerMode        = _trigger,
        TimerSeconds       = _timerSeconds,
        KeepEnabled        = [.. KeepEnabled],
        DisableThenRestore = [.. DisableThenRestore],
        KeepDisabled       = [.. KeepDisabled],
    };
}
