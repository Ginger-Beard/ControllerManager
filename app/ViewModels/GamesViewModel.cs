using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class GamesViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ProfileStore     _store;
    private readonly List<Profile>    _profiles;
    private readonly DevicesViewModel _devices;

    private Profile? _selectedProfile;
    private bool     _hasSelection;
    private bool     _hasDesktopShortcut;
    private bool     _hasStartMenuShortcut;
    private string   _healStatusText = "";

    public ObservableCollection<Profile> Profiles { get; } = [];

    public string HealStatusText
    {
        get => _healStatusText;
        private set => Set(ref _healStatusText, value);
    }

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value))
            {
                HasSelection = value is not null;
                if (value is not null)
                {
                    var healed = ProfileHealer.Heal(value, [.. _devices.Devices]);
                    if (healed.Count > 0)
                    {
                        HealStatusText = $"Auto-corrected {healed.Count} stale device ID(s): {string.Join(", ", healed)}";
                        Task.Run(() => _store.Save(_profiles));
                    }
                    else
                    {
                        HealStatusText = "";
                    }

                    Editor.LoadProfile(value);
                }
                RefreshShortcutState();
            }
        }
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    // Drives the Desktop / Start Menu buttons' toggle behaviour (Create vs
    // Remove) via DataTriggers in GamesView.xaml. Path is computed from the
    // persisted profile name, so unsaved name edits in the editor don't flip
    // the buttons until the user actually saves.
    public bool HasDesktopShortcut
    {
        get => _hasDesktopShortcut;
        private set => Set(ref _hasDesktopShortcut, value);
    }

    public bool HasStartMenuShortcut
    {
        get => _hasStartMenuShortcut;
        private set => Set(ref _hasStartMenuShortcut, value);
    }

    private void RefreshShortcutState()
    {
        if (_selectedProfile is null)
        {
            HasDesktopShortcut   = false;
            HasStartMenuShortcut = false;
            return;
        }
        HasDesktopShortcut   = File.Exists(ShortcutExporter.DesktopPath(_selectedProfile.Name));
        HasStartMenuShortcut = File.Exists(ShortcutExporter.StartMenuPath(_selectedProfile.Name));
    }

    public ProfileEditorViewModel  Editor        { get; }

    public ICommand NewProfileCommand              { get; }
    public ICommand DeleteProfileCommand           { get; }
    public ICommand SaveProfileCommand             { get; }
    public ICommand BrowseExeCommand               { get; }
    public ICommand CopySteamCommandCommand        { get; }
    public ICommand CopyLaunchCommandCommand       { get; }
    public ICommand CopyRestoreCommandCommand      { get; }
    public ICommand ToggleDesktopShortcutCommand   { get; }
    public ICommand ToggleStartMenuShortcutCommand { get; }
    public ICommand ExportProfileCommand           { get; }
    public ICommand ImportProfileCommand           { get; }

    public GamesViewModel(ProfileStore store, DevicesViewModel devices)
    {
        _store    = store;
        _devices  = devices;
        _profiles = store.Load();
        Editor        = new ProfileEditorViewModel(devices.Devices, devices.Enumerator);

        foreach (var p in _profiles) Profiles.Add(p);

        NewProfileCommand = new RelayCommand(_ =>
        {
            var p = new Profile { Name = "New Profile" };
            _profiles.Add(p);
            Profiles.Add(p);
            SelectedProfile = p;
            _store.Save(_profiles);
        });

        DeleteProfileCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;

            var confirm = MessageBox.Show(
                $"Delete profile \"{_selectedProfile.Name}\"?\n\nThis cannot be undone.",
                "Controller Manager",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;

            // Drop the per-profile launch task (if any) so we don't leave orphans
            // in Task Scheduler. Idempotent — no-op when no task exists.
            LaunchTaskManager.DeleteTaskForProfile(_selectedProfile.Id);

            _profiles.Remove(_selectedProfile);
            Profiles.Remove(_selectedProfile);
            SelectedProfile = Profiles.FirstOrDefault();
            _store.Save(_profiles);
        }, _ => _selectedProfile is not null);

        SaveProfileCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;

            var updated = Editor.ToProfile();
            var idx     = _profiles.IndexOf(_selectedProfile);
            if (idx < 0) return;

            _profiles[idx]   = updated;
            Profiles[idx]    = updated;
            _selectedProfile = updated;
            HasSelection = true;
            _store.Save(_profiles);
            Editor.LoadProfile(updated); // clears IsDirty
            // Name may have changed — the .lnk paths are name-derived, so
            // recompute which shortcuts exist for the *new* name.
            RefreshShortcutState();
        }, _ => _selectedProfile is not null && Editor.IsDirty);

        BrowseExeCommand = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select game executable",
                Filter = "Executables (*.exe)|*.exe",
            };
            if (dlg.ShowDialog() == true)
                Editor.ExePath = dlg.FileName;
        });

        CopySteamCommandCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                   ?? "ControllerManager.exe";
            var cmd = $"\"{exe}\" --steam-wrap {_selectedProfile.Id} -- %command%";
            Clipboard.SetText(cmd);
        }, _ => _selectedProfile is not null);

        // For Sunshine/Apollo (or any external launcher): one click puts the
        // exact "<exe>" --launch <guid> command on the clipboard. Beats asking
        // the user to export a .lnk and read the command line out of it.
        CopyLaunchCommandCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                   ?? "ControllerManager.exe";
            var cmd = $"\"{exe}\" --launch {_selectedProfile.Id}";
            Clipboard.SetText(cmd);
        }, _ => _selectedProfile is not null);

        // Companion to Copy launch command: profile-agnostic restore command
        // for Sunshine/Apollo's Undo Command field. No profile id required — it
        // restores whatever orchestrator session is currently active.
        CopyRestoreCommandCommand = new RelayCommand(_ =>
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                   ?? "ControllerManager.exe";
            var cmd = $"\"{exe}\" --restore";
            Clipboard.SetText(cmd);
        });

        // Shortcuts only make sense when the profile actually launches a game —
        // a .lnk for a no-exe (Sunshine/Apollo) profile would just trigger the
        // hide phase and dangle there waiting for --restore, which is confusing.
        // Keep CanExecute gated on the exe being set.
        ToggleDesktopShortcutCommand   = MakeShortcutToggleCommand(ShortcutExporter.DesktopPath,   "desktop");
        ToggleStartMenuShortcutCommand = MakeShortcutToggleCommand(ShortcutExporter.StartMenuPath, "Start Menu");

        ExportProfileCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Export Profile",
                Filter     = "JSON (*.json)|*.json",
                FileName   = _selectedProfile.Name,
                DefaultExt = ".json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_selectedProfile, JsonOpts));
                MessageBox.Show($"Profile exported to:\n{dlg.FileName}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }, _ => _selectedProfile is not null);

        ImportProfileCommand = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Import Profile",
                Filter = "JSON (*.json)|*.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json     = File.ReadAllText(dlg.FileName);
                var imported = JsonSerializer.Deserialize<Profile>(json, JsonOpts)
                    ?? throw new InvalidDataException("File did not contain a valid profile.");
                imported.Id = Guid.NewGuid(); // avoid ID collision with existing profiles
                _profiles.Add(imported);
                Profiles.Add(imported);
                SelectedProfile = imported;
                _store.Save(_profiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    // Builds one of the toggle commands. If the .lnk exists, the command
    // removes it; otherwise it creates one. Either way it refreshes the Has*
    // flag the XAML binds to so the button label flips immediately.
    private RelayCommand MakeShortcutToggleCommand(Func<string, string> pathFor, string locationLabel) =>
        new(_ =>
        {
            if (_selectedProfile is null) return;
            var path = pathFor(_selectedProfile.Name);

            if (File.Exists(path))
            {
                try
                {
                    ShortcutExporter.RemoveShortcut(path);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not remove the {locationLabel} shortcut:\n{ex.Message}",
                        "Controller Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                RefreshShortcutState();
                return;
            }

            try
            {
                ShortcutExporter.CreateShortcut(path, _selectedProfile.Id, _selectedProfile.GameExecutablePath);
                MessageBox.Show($"Shortcut created:\n{path}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshShortcutState();
        }, _ => _selectedProfile is not null
             && !string.IsNullOrWhiteSpace(_selectedProfile.GameExecutablePath));

    /// <summary>
    /// Returns the name of another auto-triggered profile that shares the given
    /// executable path, or null if there's no conflict. The current profile (by
    /// id) is excluded so editing your own auto-trigger toggle never self-flags.
    /// </summary>
    public string? FindAutoTriggerConflict(Guid? currentProfileId, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var normalized = exePath.Trim();
        foreach (var p in Profiles)
        {
            if (currentProfileId.HasValue && p.Id == currentProfileId.Value) continue;
            if (!p.ProcessWatcherEnabled) continue;
            if (string.Equals(p.GameExecutablePath?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                return p.Name;
        }
        return null;
    }
}
