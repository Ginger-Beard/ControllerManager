using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly LaunchOrchestrator _orchestrator;
    private readonly ProfileStore       _profileStore;

    private Profile? _selectedProfile;
    private string   _statusText  = "Ready.";
    private bool     _isRunning;
    private bool     _hasSelection;

    public ObservableCollection<Profile> Profiles      { get; } = [];
    public ObservableCollection<string>  ActivityLog   { get; } = [];

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            Set(ref _selectedProfile, value);
            HasSelection = value is not null;
            OnPropertyChanged(nameof(KeepEnabled));
            OnPropertyChanged(nameof(DisableThenRestore));
            OnPropertyChanged(nameof(KeepDisabled));
        }
    }

    public IEnumerable<Models.DeviceRef> KeepEnabled        => _selectedProfile?.KeepEnabled        ?? [];
    public IEnumerable<Models.DeviceRef> DisableThenRestore => _selectedProfile?.DisableThenRestore  ?? [];
    public IEnumerable<Models.DeviceRef> KeepDisabled       => _selectedProfile?.KeepDisabled        ?? [];

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    public ICommand LaunchCommand   { get; }
    public ICommand RestoreCommand  { get; }
    public ICommand CopyLogCommand  { get; }

    public DashboardViewModel(LaunchOrchestrator orchestrator, ProfileStore profileStore)
    {
        _orchestrator = orchestrator;
        _profileStore = profileStore;

        LaunchCommand = new RelayCommand(
            _ => Launch(),
            _ => HasSelection && !IsRunning);

        RestoreCommand = new RelayCommand(
            _ => _ = _orchestrator.AbortAsync());

        CopyLogCommand = new RelayCommand(
            _ =>
            {
                if (ActivityLog.Count > 0)
                    System.Windows.Clipboard.SetText(
                        string.Join(Environment.NewLine, ActivityLog));
            },
            _ => ActivityLog.Count > 0);

        _orchestrator.StateChanged    += OnStateChanged;
        _orchestrator.ActivityLogged  += OnActivityLogged;

        RefreshProfiles();
    }

    public void RefreshProfiles()
    {
        var selected = _selectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.Load()) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selected)
                       ?? Profiles.FirstOrDefault();
    }

    private void Launch()
    {
        if (_selectedProfile is null) return;
        ActivityLog.Clear();
        _orchestrator.Start(_selectedProfile);
    }

    private void OnStateChanged(object? _, OrchestratorState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsRunning  = state != OrchestratorState.Idle;
            StatusText = state switch
            {
                OrchestratorState.DisablingDevices    => "Disabling devices...",
                OrchestratorState.LaunchingGame       => "Launching game...",
                OrchestratorState.WaitingForAcquisition => "Waiting for game to acquire wheel...",
                OrchestratorState.RestoringDevices    => "Re-enabling devices...",
                OrchestratorState.Monitoring          => "Game running — monitoring for exit...",
                OrchestratorState.Idle                => "Ready.",
                _                                     => state.ToString(),
            };
        });
    }

    private void OnActivityLogged(object? _, string message)
    {
        Application.Current.Dispatcher.Invoke(() => AddLog(message));
    }

    private void AddLog(string message)
    {
        ActivityLog.Add(message);
        if (ActivityLog.Count > 200) ActivityLog.RemoveAt(0);
    }

    public void Dispose() => _orchestrator.Dispose();
}
