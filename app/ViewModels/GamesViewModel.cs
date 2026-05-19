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
                    if (!string.IsNullOrEmpty(value.GameExecutableName))
                        HandleWatcher.ProcessName = value.GameExecutableName;
                }
            }
        }
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    public ProfileEditorViewModel  Editor        { get; }
    public HandleWatcherViewModel  HandleWatcher { get; }

    public ICommand NewProfileCommand              { get; }
    public ICommand DeleteProfileCommand           { get; }
    public ICommand SaveProfileCommand             { get; }
    public ICommand BrowseExeCommand               { get; }
    public ICommand CopySteamCommandCommand        { get; }
    public ICommand CreateDesktopShortcutCommand   { get; }
    public ICommand CreateStartMenuShortcutCommand { get; }
    public ICommand ExportProfileCommand           { get; }
    public ICommand ImportProfileCommand           { get; }

    public GamesViewModel(ProfileStore store, DevicesViewModel devices)
    {
        _store    = store;
        _devices  = devices;
        _profiles = store.Load();
        Editor        = new ProfileEditorViewModel(devices.Devices, devices.Enumerator);
        HandleWatcher = new HandleWatcherViewModel(devices.Devices);

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

        CreateDesktopShortcutCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;
            try
            {
                var path = ShortcutExporter.DesktopPath(_selectedProfile.Name);
                ShortcutExporter.CreateShortcut(path, _selectedProfile.Id, _selectedProfile.GameExecutablePath);
                MessageBox.Show($"Shortcut created:\n{path}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }, _ => _selectedProfile is not null);

        CreateStartMenuShortcutCommand = new RelayCommand(_ =>
        {
            if (_selectedProfile is null) return;
            try
            {
                var path = ShortcutExporter.StartMenuPath(_selectedProfile.Name);
                ShortcutExporter.CreateShortcut(path, _selectedProfile.Id, _selectedProfile.GameExecutablePath);
                MessageBox.Show($"Shortcut created:\n{path}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Controller Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }, _ => _selectedProfile is not null);

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
}
