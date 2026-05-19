using System.Collections.ObjectModel;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class ProfileEditorViewModel : ViewModelBase
{
    private string _name                = "";
    private string _exePath             = "";
    private string _exeName             = "";
    private int    _initialDelaySeconds = 5;
    private bool   _isDirty;

    private HidDevice? _selectedAvailable;

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

    public int InitialDelaySeconds
    {
        get => _initialDelaySeconds;
        set { Set(ref _initialDelaySeconds, Math.Max(0, value)); IsDirty = true; }
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

    // Single ordered list of device assignments — replaces three separate lists
    public ObservableCollection<DeviceAssignmentViewModel> Assignments { get; } = [];

    // Devices not yet assigned — shown in the picker
    public IEnumerable<HidDevice> UnassignedDevices =>
        (_showAllHid ? _allDevicesExpanded : (IEnumerable<HidDevice>)AllDevices)
            .Where(d => !IsAssigned(d));

    public HidDevice? SelectedAvailable
    {
        get => _selectedAvailable;
        set => Set(ref _selectedAvailable, value);
    }

    public ICommand AddCommand      { get; }
    public ICommand RemoveCommand   { get; }
    public ICommand MoveUpCommand   { get; }
    public ICommand MoveDownCommand { get; }

    public ProfileEditorViewModel(ObservableCollection<HidDevice> allDevices, DeviceEnumerator enumerator)
    {
        AllDevices  = allDevices;
        _enumerator = enumerator;

        void RefreshUnassigned(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(UnassignedDevices));

        AllDevices.CollectionChanged          += RefreshUnassigned;
        _allDevicesExpanded.CollectionChanged += RefreshUnassigned;
        Assignments.CollectionChanged         += RefreshUnassigned;

        AddCommand = new RelayCommand(
            _ => AddSelected(),
            _ => SelectedAvailable is not null && !IsAssigned(SelectedAvailable));

        RemoveCommand = new RelayCommand(
            p => { if (p is DeviceAssignmentViewModel a) RemoveAssignment(a); },
            p => p is DeviceAssignmentViewModel);

        MoveUpCommand = new RelayCommand(
            p => { if (p is DeviceAssignmentViewModel a) MoveAssignment(a, -1); },
            p => p is DeviceAssignmentViewModel a && Assignments.IndexOf(a) > 0);

        MoveDownCommand = new RelayCommand(
            p => { if (p is DeviceAssignmentViewModel a) MoveAssignment(a, +1); },
            p => p is DeviceAssignmentViewModel a &&
                 Assignments.IndexOf(a) >= 0 &&
                 Assignments.IndexOf(a) < Assignments.Count - 1);
    }

    private bool IsAssigned(HidDevice d) =>
        Assignments.Any(a => a.InstanceId == d.InstanceId);

    private void AddSelected()
    {
        var device = SelectedAvailable;
        if (device is null || IsAssigned(device)) return;
        Assignments.Add(new DeviceAssignmentViewModel(
            device.InstanceId, device.FriendlyName, DeviceRole.AlwaysVisible));
        IsDirty = true;
    }

    private void RemoveAssignment(DeviceAssignmentViewModel item)
    {
        Assignments.Remove(item);
        IsDirty = true;
    }

    private void MoveAssignment(DeviceAssignmentViewModel item, int delta)
    {
        int idx = Assignments.IndexOf(item);
        int to  = idx + delta;
        if (idx < 0 || to < 0 || to >= Assignments.Count) return;
        Assignments.Move(idx, to);
        IsDirty = true;
    }

    public void LoadProfile(Profile p)
    {
        ProfileId    = p.Id;
        _name        = p.Name;
        _exePath     = p.GameExecutablePath;
        _exeName     = p.GameExecutableName;

        // Migrate old Timer-mode profiles: if no initialDelaySeconds was saved but
        // the profile used the Timer trigger, carry forward TimerSeconds as the delay.
        _initialDelaySeconds = p.InitialDelaySeconds > 0 ? p.InitialDelaySeconds
            : (p.TriggerMode == TriggerMode.Timer ? p.TimerSeconds : 0);

        Assignments.Clear();
        foreach (var d in p.KeepEnabled)
            Assignments.Add(new DeviceAssignmentViewModel(
                d.InstanceId, d.FriendlyName, DeviceRole.AlwaysVisible));
        foreach (var d in p.DisableThenRestore)
            Assignments.Add(new DeviceAssignmentViewModel(
                d.InstanceId, d.FriendlyName, DeviceRole.RevealAfterStart, d.DelaySeconds));
        foreach (var d in p.KeepDisabled)
            Assignments.Add(new DeviceAssignmentViewModel(
                d.InstanceId, d.FriendlyName, DeviceRole.AlwaysHidden));

        IsDirty = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ExePath));
        OnPropertyChanged(nameof(ExeName));
        OnPropertyChanged(nameof(InitialDelaySeconds));
    }

    public Profile ToProfile()
    {
        var keepEnabled        = new List<DeviceRef>();
        var disableThenRestore = new List<DeviceRef>();
        var keepDisabled       = new List<DeviceRef>();

        foreach (var a in Assignments)
        {
            var dref = new DeviceRef
            {
                InstanceId   = a.InstanceId,
                FriendlyName = a.FriendlyName,
            };
            switch (a.Role)
            {
                case DeviceRole.AlwaysVisible:
                    keepEnabled.Add(dref);
                    break;
                case DeviceRole.RevealAfterStart:
                    dref.DelaySeconds = a.DelaySeconds;
                    disableThenRestore.Add(dref);
                    break;
                case DeviceRole.AlwaysHidden:
                    keepDisabled.Add(dref);
                    break;
            }
        }

        return new Profile
        {
            Id                  = ProfileId ?? Guid.NewGuid(),
            Name                = _name,
            GameExecutablePath  = _exePath,
            GameExecutableName  = _exeName,
            InitialDelaySeconds = _initialDelaySeconds,
            KeepEnabled         = keepEnabled,
            DisableThenRestore  = disableThenRestore,
            KeepDisabled        = keepDisabled,
        };
    }
}
