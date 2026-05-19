using System.Collections.ObjectModel;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class ProfileEditorViewModel : ViewModelBase
{
    private readonly DeviceEnumerator _enumerator;

    private string _name                  = "";
    private string _exePath               = "";
    private string _exeName               = "";
    private int    _initialDelaySeconds   = 5;
    private bool   _processWatcherEnabled = true;
    private bool   _isDirty;
    private bool   _showAllHid;

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

    public bool ProcessWatcherEnabled
    {
        get => _processWatcherEnabled;
        set { Set(ref _processWatcherEnabled, value); IsDirty = true; }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    // Picker devices — managed locally so the Games tab's "Show all" toggle is
    // independent of the Devices tab's toggle. Always reflects the current
    // ShowAllHid value here, never the upstream DevicesViewModel's filter state.
    private readonly ObservableCollection<HidDevice> _pickerDevices = [];

    public bool ShowAllHid
    {
        get => _showAllHid;
        set
        {
            if (Set(ref _showAllHid, value))
                RefreshPickerDevices();
        }
    }

    // Single ordered list of device assignments — replaces three separate lists
    public ObservableCollection<DeviceAssignmentViewModel> Assignments { get; } = [];

    // Devices not yet assigned to the profile — what the picker shows
    public IEnumerable<HidDevice> UnassignedDevices =>
        _pickerDevices.Where(d => !IsAssigned(d));

    // True only when there's at least one RevealAfterStart assignment — used
    // by the editor to gate the "Pause before first reveal" control
    public bool HasRevealAfterStart =>
        Assignments.Any(a => a.Role == DeviceRole.RevealAfterStart);

    public HidDevice? SelectedAvailable
    {
        get => _selectedAvailable;
        set => Set(ref _selectedAvailable, value);
    }

    public ICommand AddCommand      { get; }
    public ICommand AddAllCommand   { get; }
    public ICommand RemoveCommand   { get; }
    public ICommand MoveUpCommand   { get; }
    public ICommand MoveDownCommand { get; }

    public ProfileEditorViewModel(ObservableCollection<HidDevice> sharedDeviceList, DeviceEnumerator enumerator)
    {
        _enumerator = enumerator;

        // Treat upstream collection-change as a "devices were plugged/unplugged" signal —
        // re-query locally with our own ShowAllHid so the picker stays accurate.
        sharedDeviceList.CollectionChanged += (_, _) => RefreshPickerDevices();

        _pickerDevices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(UnassignedDevices));
        Assignments.CollectionChanged    += (_, _) =>
        {
            OnPropertyChanged(nameof(UnassignedDevices));
            OnPropertyChanged(nameof(HasRevealAfterStart));
        };

        AddCommand = new RelayCommand(
            _ => AddSelected(),
            _ => SelectedAvailable is not null && !IsAssigned(SelectedAvailable));

        // Adds every device currently in the picker (respects ShowAllHid).
        // Each gets added as AlwaysVisible by default, matching the single-add behavior.
        AddAllCommand = new RelayCommand(
            _ => AddAllUnassigned(),
            _ => UnassignedDevices.Any());

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

        RefreshPickerDevices();
    }

    private void RefreshPickerDevices()
    {
        var snapshot = _showAllHid;
        Task.Run(() =>
        {
            var list = _enumerator.GetAll(snapshot);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _pickerDevices.Clear();
                foreach (var d in list) _pickerDevices.Add(d);
            });
        });
    }

    private bool IsAssigned(HidDevice d) =>
        Assignments.Any(a => a.InstanceId == d.InstanceId);

    private void AddSelected()
    {
        var device = SelectedAvailable;
        if (device is null || IsAssigned(device)) return;
        var vm = new DeviceAssignmentViewModel(
            device.InstanceId, device.FriendlyName, DeviceRole.AlwaysVisible);
        vm.PropertyChanged += OnAssignmentPropertyChanged;
        Assignments.Add(vm);
        IsDirty = true;
    }

    private void AddAllUnassigned()
    {
        // Snapshot first — adding to Assignments mutates UnassignedDevices' source.
        var toAdd = UnassignedDevices.ToList();
        if (toAdd.Count == 0) return;

        foreach (var device in toAdd)
        {
            var vm = new DeviceAssignmentViewModel(
                device.InstanceId, device.FriendlyName, DeviceRole.AlwaysVisible);
            vm.PropertyChanged += OnAssignmentPropertyChanged;
            Assignments.Add(vm);
        }
        IsDirty = true;
    }

    private void RemoveAssignment(DeviceAssignmentViewModel item)
    {
        item.PropertyChanged -= OnAssignmentPropertyChanged;
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

    private void OnAssignmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Track edits so unsaved-changes indicator is accurate; also recompute
        // HasRevealAfterStart when an assignment's role changes.
        IsDirty = true;
        if (e.PropertyName is nameof(DeviceAssignmentViewModel.Role))
            OnPropertyChanged(nameof(HasRevealAfterStart));
    }

    public void LoadProfile(Profile p)
    {
        ProfileId              = p.Id;
        _name                  = p.Name;
        _exePath               = p.GameExecutablePath;
        _exeName               = p.GameExecutableName;
        _processWatcherEnabled = p.ProcessWatcherEnabled;

        // Migrate old Timer-mode profiles: if no initialDelaySeconds was saved but
        // the profile used the Timer trigger, carry forward TimerSeconds as the delay.
        _initialDelaySeconds = p.InitialDelaySeconds > 0 ? p.InitialDelaySeconds
            : (p.TriggerMode == TriggerMode.Timer ? p.TimerSeconds : 0);

        // Detach old assignment handlers before clearing
        foreach (var a in Assignments) a.PropertyChanged -= OnAssignmentPropertyChanged;
        Assignments.Clear();

        void Add(DeviceRef d, DeviceRole role, int delay = 0)
        {
            var vm = new DeviceAssignmentViewModel(d.InstanceId, d.FriendlyName, role, delay);
            vm.PropertyChanged += OnAssignmentPropertyChanged;
            Assignments.Add(vm);
        }

        foreach (var d in p.KeepEnabled)        Add(d, DeviceRole.AlwaysVisible);
        foreach (var d in p.DisableThenRestore) Add(d, DeviceRole.RevealAfterStart, d.DelaySeconds);
        foreach (var d in p.KeepDisabled)       Add(d, DeviceRole.AlwaysHidden);

        IsDirty = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ExePath));
        OnPropertyChanged(nameof(ExeName));
        OnPropertyChanged(nameof(InitialDelaySeconds));
        OnPropertyChanged(nameof(ProcessWatcherEnabled));
        OnPropertyChanged(nameof(HasRevealAfterStart));
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
            Id                    = ProfileId ?? Guid.NewGuid(),
            Name                  = _name,
            GameExecutablePath    = _exePath,
            GameExecutableName    = _exeName,
            InitialDelaySeconds   = _initialDelaySeconds,
            ProcessWatcherEnabled = _processWatcherEnabled,
            KeepEnabled           = keepEnabled,
            DisableThenRestore    = disableThenRestore,
            KeepDisabled          = keepDisabled,
        };
    }
}
