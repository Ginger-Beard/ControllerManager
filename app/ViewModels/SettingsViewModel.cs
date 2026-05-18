using System.Windows.Input;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private AppSettings _settings;

    private bool   _startWithWindows;
    private bool   _processWatcherEnabled;
    private TriggerMode _defaultTriggerMode;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { Set(ref _startWithWindows, value); Save(); }
    }

    public bool ProcessWatcherEnabled
    {
        get => _processWatcherEnabled;
        set { Set(ref _processWatcherEnabled, value); Save(); }
    }

    public TriggerMode DefaultTriggerMode
    {
        get => _defaultTriggerMode;
        set { Set(ref _defaultTriggerMode, value); Save(); }
    }

    public IEnumerable<TriggerMode> TriggerModes { get; } =
        Enum.GetValues<TriggerMode>();

    public SettingsViewModel(SettingsStore store)
    {
        _store    = store;
        _settings = store.Load();

        _startWithWindows     = SettingsStore.GetStartWithWindows();
        _processWatcherEnabled = _settings.ProcessWatcherEnabled;
        _defaultTriggerMode   = _settings.DefaultTriggerMode;
    }

    private void Save()
    {
        _settings.StartWithWindows     = _startWithWindows;
        _settings.ProcessWatcherEnabled = _processWatcherEnabled;
        _settings.DefaultTriggerMode   = _defaultTriggerMode;
        _store.Save(_settings);
    }
}
