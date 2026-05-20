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
    private bool   _processWatcherEnabled = true;
    private AcquisitionTrigger _acquisitionTrigger = AcquisitionTrigger.Timer;
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

    public bool ProcessWatcherEnabled
    {
        get => _processWatcherEnabled;
        set { Set(ref _processWatcherEnabled, value); IsDirty = true; }
    }

    public AcquisitionTrigger AcquisitionTrigger
    {
        get => _acquisitionTrigger;
        set
        {
            if (Set(ref _acquisitionTrigger, value))
            {
                IsDirty = true;
                OnPropertyChanged(nameof(IsTimerMode));
            }
        }
    }

    public bool IsTimerMode => _acquisitionTrigger == AcquisitionTrigger.Timer;

    // For the role dropdown's ItemsSource
    public static IReadOnlyList<AcquisitionTriggerChoice> AcquisitionTriggers { get; } = [
        new(AcquisitionTrigger.Timer,             "Fixed time per device"),
        new(AcquisitionTrigger.FirstDeviceOpened, "When game opens first device"),
    ];

    public record AcquisitionTriggerChoice(AcquisitionTrigger Mode, string Label);

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
        if (e.PropertyName is not nameof(DeviceAssignmentViewModel.Role)) return;

        OnPropertyChanged(nameof(HasRevealAfterStart));

        // When the FIRST RevealAfterStart device in the list is set, default its delay
        // to 5s if it's still 0 — this preserves FFB on Forza-style games without the
        // user needing to know to set it manually.
        if (sender is not DeviceAssignmentViewModel vm) return;
        if (vm.Role != DeviceRole.RevealAfterStart) return;
        if (vm.DelaySeconds != 0) return;
        var firstReveal = Assignments.FirstOrDefault(a => a.Role == DeviceRole.RevealAfterStart);
        if (ReferenceEquals(firstReveal, vm))
            vm.DelaySeconds = 5;
    }

    public void LoadProfile(Profile p)
    {
        ProfileId              = p.Id;
        _name                  = p.Name;
        _exePath               = p.GameExecutablePath;
        _exeName               = p.GameExecutableName;
        _processWatcherEnabled = p.ProcessWatcherEnabled;
        _acquisitionTrigger    = p.AcquisitionTrigger;

        // Migrate to current schema-2 timing semantics (absolute "reveal at T+Xs").
        //
        // Schema 0 (legacy): InitialDelaySeconds = wait BEFORE first reveal;
        //                    DelaySeconds        = wait AFTER each reveal.
        //   → Shift right (InitialDelaySeconds becomes first pre-delay; old last
        //     DelaySeconds dropped since "wait after final reveal" was unobservable),
        //     then cumulative-sum to get absolute times.
        //
        // Schema 1: DelaySeconds = wait BEFORE each reveal (relative).
        //   → Cumulative-sum to get absolute times.
        //
        // Schema 2: already absolute — no transformation.
        //
        // Old Timer-mode profiles (TriggerMode=Timer, TimerSeconds>0) used
        // TimerSeconds as the initial delay; carry that forward when
        // InitialDelaySeconds wasn't set.
        int legacyInitial = p.InitialDelaySeconds > 0
            ? p.InitialDelaySeconds
            : (p.TriggerMode == TriggerMode.Timer ? p.TimerSeconds : 0);

        double[] migratedDelays;
        if (p.DisableThenRestore.Count == 0)
        {
            migratedDelays = [];
        }
        else if (p.SchemaVersion < 1)
        {
            // schema 0 → 2: shift + cumulative sum
            migratedDelays = new double[p.DisableThenRestore.Count];
            migratedDelays[0] = legacyInitial;
            for (int i = 1; i < p.DisableThenRestore.Count; i++)
                migratedDelays[i] = migratedDelays[i - 1] + p.DisableThenRestore[i - 1].DelaySeconds;
        }
        else if (p.SchemaVersion < 2)
        {
            // schema 1 → 2: cumulative sum
            migratedDelays = new double[p.DisableThenRestore.Count];
            migratedDelays[0] = p.DisableThenRestore[0].DelaySeconds;
            for (int i = 1; i < p.DisableThenRestore.Count; i++)
                migratedDelays[i] = migratedDelays[i - 1] + p.DisableThenRestore[i].DelaySeconds;
        }
        else
        {
            // schema ≥ 2: absolute already
            migratedDelays = [.. p.DisableThenRestore.Select(d => d.DelaySeconds)];
        }

        // Detach old assignment handlers before clearing
        foreach (var a in Assignments) a.PropertyChanged -= OnAssignmentPropertyChanged;
        Assignments.Clear();

        void Add(DeviceRef d, DeviceRole role, double delay = 0)
        {
            var vm = new DeviceAssignmentViewModel(d.InstanceId, d.FriendlyName, role, delay);
            vm.PropertyChanged += OnAssignmentPropertyChanged;
            Assignments.Add(vm);
        }

        foreach (var d in p.KeepEnabled) Add(d, DeviceRole.AlwaysVisible);
        for (int i = 0; i < p.DisableThenRestore.Count; i++)
            Add(p.DisableThenRestore[i], DeviceRole.RevealAfterStart, migratedDelays[i]);
        foreach (var d in p.KeepDisabled) Add(d, DeviceRole.AlwaysHidden);

        IsDirty = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ExePath));
        OnPropertyChanged(nameof(ExeName));
        OnPropertyChanged(nameof(ProcessWatcherEnabled));
        OnPropertyChanged(nameof(AcquisitionTrigger));
        OnPropertyChanged(nameof(IsTimerMode));
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
            SchemaVersion         = 2, // delays are absolute "reveal at T+Xs" times
            Name                  = _name,
            GameExecutablePath    = _exePath,
            GameExecutableName    = _exeName,
            ProcessWatcherEnabled = _processWatcherEnabled,
            AcquisitionTrigger    = _acquisitionTrigger,
            KeepEnabled           = keepEnabled,
            DisableThenRestore    = disableThenRestore,
            KeepDisabled          = keepDisabled,
        };
    }
}
