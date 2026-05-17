using System.Collections.ObjectModel;
using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class GamesViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly List<Profile> _profiles;

    private Profile? _selectedProfile;
    private bool _hasSelection;

    public ObservableCollection<Profile> Profiles { get; } = [];

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value))
            {
                HasSelection = value is not null;
                if (value is not null) Editor.LoadProfile(value);
            }
        }
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    public ProfileEditorViewModel Editor { get; }

    public ICommand NewProfileCommand    { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand SaveProfileCommand   { get; }
    public ICommand BrowseExeCommand     { get; }

    public GamesViewModel(ProfileStore store, DevicesViewModel devices)
    {
        _store    = store;
        _profiles = store.Load();
        Editor    = new ProfileEditorViewModel(devices.Devices);

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

            _profiles[idx] = updated;
            Profiles[idx]  = updated;
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
    }
}
