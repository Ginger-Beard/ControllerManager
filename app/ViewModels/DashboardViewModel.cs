using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly LaunchOrchestrator         _orchestrator;
    private readonly ProfileStore               _profileStore;
    private readonly ObservableCollection<HidDevice> _liveDevices;

    private Profile? _selectedProfile;
    private string   _statusText       = "Ready.";
    private string   _sessionText      = "";
    private bool     _isRunning;
    private bool     _hasSelection;
    private bool     _isSessionActive;

    public ObservableCollection<Profile> Profiles     { get; } = [];
    public ObservableCollection<string>  ActivityLog  { get; } = [];

    // Left column — devices every process can see (not in persistent blacklist).
    public ObservableCollection<string> SystemDevices { get; } = [];
    // Right column — devices the active game can see (not in session blacklist).
    // Empty when no session is running.
    public ObservableCollection<string> GameDevices   { get; } = [];

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set { Set(ref _selectedProfile, value); HasSelection = value is not null; }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SessionText
    {
        get => _sessionText;
        private set => Set(ref _sessionText, value);
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

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set => Set(ref _isSessionActive, value);
    }

    public ICommand LaunchCommand  { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CopyLogCommand { get; }

    public DashboardViewModel(LaunchOrchestrator orchestrator, ProfileStore profileStore,
                              ObservableCollection<HidDevice> liveDevices)
    {
        _orchestrator = orchestrator;
        _profileStore = profileStore;
        _liveDevices  = liveDevices;

        LaunchCommand = new RelayCommand(
            _ => Launch(),
            _ => HasSelection && !IsRunning);

        RestoreCommand = new RelayCommand(
            _ => _ = _orchestrator.AbortAsync());

        CopyLogCommand = new RelayCommand(
            _ =>
            {
                if (ActivityLog.Count > 0)
                    Clipboard.SetText(string.Join(Environment.NewLine, ActivityLog));
            },
            _ => ActivityLog.Count > 0);

        _orchestrator.StateChanged   += OnStateChanged;
        _orchestrator.ActivityLogged += OnActivityLogged;

        RefreshProfiles();
        RefreshDeviceLists();
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
        // Always load a fresh copy from disk so profile changes saved in the
        // Games tab are picked up without needing a Dashboard refresh.
        var fresh = _profileStore.Load().FirstOrDefault(p => p.Id == _selectedProfile.Id)
                    ?? _selectedProfile;
        _orchestrator.Start(fresh);
    }

    private void OnStateChanged(object? _, OrchestratorState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsRunning       = state != OrchestratorState.Idle;
            IsSessionActive = IsRunning;

            var profile = _orchestrator.ActiveProfile;
            SessionText = profile is not null
                ? $"Detected {profile.GameExecutableName} — {profile.Name}"
                : "";

            StatusText = state switch
            {
                OrchestratorState.HidingDevices         => "Hiding devices...",
                OrchestratorState.LaunchingGame         => "Launching game...",
                OrchestratorState.WaitingForAcquisition => "Waiting for game to acquire devices...",
                OrchestratorState.RestoringDevices      => "Revealing devices...",
                OrchestratorState.Monitoring            => "Game running — monitoring for exit...",
                OrchestratorState.Idle                  => "Ready.",
                _                                       => state.ToString(),
            };

            RefreshDeviceLists();
        });
    }

    /// <summary>
    /// Rebuilds the System and Game device columns from the live device list
    /// and the current HidHide blacklist state.
    /// </summary>
    public void RefreshDeviceLists()
    {
        SystemDevices.Clear();
        GameDevices.Clear();

        if (!App.HidHide.IsAvailable) return;

        var persistent = App.HidHide.GetBlacklist()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = App.HidHide.SessionBlacklistIds;

        foreach (var d in _liveDevices)
        {
            // System: visible to all processes — not in persistent blacklist.
            if (!persistent.Contains(d.InstanceId))
                SystemDevices.Add(d.FriendlyName);

            // Game: visible to the active game — not in session blacklist.
            // Only populated during an active session.
            if (IsSessionActive && !session.Contains(d.InstanceId))
                GameDevices.Add(d.FriendlyName);
        }
    }

    private void OnActivityLogged(object? _, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ActivityLog.Add(message);
            if (ActivityLog.Count > 200) ActivityLog.RemoveAt(0);
        });
    }

    public void Dispose()
    {
        // Orchestrator is owned by App (shared with tray/process-watcher) — don't dispose it.
        _orchestrator.StateChanged   -= OnStateChanged;
        _orchestrator.ActivityLogged -= OnActivityLogged;
    }
}
