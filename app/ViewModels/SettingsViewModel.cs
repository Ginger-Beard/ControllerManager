using System.Diagnostics;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private AppSettings _settings;

    private bool     _startWithWindows;
    private bool     _startMinimized;
    private LogLevel _logLevel;
    private bool     _alwaysOnTop;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { Set(ref _startWithWindows, value); Save(); }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set { Set(ref _startMinimized, value); Save(); }
    }

    public LogLevel LogLevel
    {
        get => _logLevel;
        set { Set(ref _logLevel, value); Logger.SetLevel(value); Save(); }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set { Set(ref _alwaysOnTop, value); Save(); }
    }

    // ── HidHide status ────────────────────────────────────────────────────────────

    public bool HidHideInstalled => App.HidHide.IsAvailable;

    // ── Logging ───────────────────────────────────────────────────────────────────

    public IEnumerable<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public string LogFilePath => Logger.LogFilePath ?? "(logging not initialized)";

    public ICommand OpenLogFolderCommand       { get; }
    public ICommand OpenHidHideDownloadCommand { get; }

    public SettingsViewModel(SettingsStore store)
    {
        _store    = store;
        _settings = store.Load();

        _startWithWindows = SettingsStore.GetStartWithWindows();
        _startMinimized   = _settings.StartMinimized;
        _logLevel         = _settings.LogLevel;
        _alwaysOnTop      = _settings.AlwaysOnTop;

        OpenLogFolderCommand = new RelayCommand(_ =>
        {
            var path = Logger.LogFilePath;
            if (path is null) return;
            var folder = Path.GetDirectoryName(path);
            if (folder is not null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        });

        OpenHidHideDownloadCommand = new RelayCommand(_ =>
            Process.Start(new ProcessStartInfo(
                "https://github.com/nefarius/HidHide/releases/latest")
                { UseShellExecute = true }));
    }

    private void Save()
    {
        _settings.StartWithWindows = _startWithWindows;
        _settings.StartMinimized   = _startMinimized;
        _settings.LogLevel         = _logLevel;
        _settings.AlwaysOnTop      = _alwaysOnTop;
        _store.Save(_settings);
        App.Settings = _settings;
    }
}
