using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private AppSettings _settings;

    private bool        _startWithWindows;
    private bool        _startMinimized;
    private bool        _processWatcherEnabled;
    private LogLevel    _logLevel;
    private bool        _alwaysOnTop;
    private bool        _backendIsHidHide;

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

    public bool ProcessWatcherEnabled
    {
        get => _processWatcherEnabled;
        set { Set(ref _processWatcherEnabled, value); Save(); }
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

    // ── HidHide backend selection ─────────────────────────────────────────────────

    /// <summary>True when HidHide is installed.</summary>
    public bool HidHideInstalled => App.HidHide.IsAvailable;

    /// <summary>
    /// HidHide radio button.  Setting to true is instant; setting to false (i.e. switching
    /// to pnputil) shows a confirmation dialog per CRITERIA.
    /// </summary>
    public bool BackendIsHidHide
    {
        get => _backendIsHidHide;
        set
        {
            if (value)
            {
                // Switching to HidHide — no confirmation needed.
                if (Set(ref _backendIsHidHide, true))
                {
                    _settings.DeviceHidingBackend = DeviceHidingBackend.Auto;
                    Save();
                }
            }
            else
            {
                // Switching to pnputil — require confirmation per CRITERIA.
                var result = MessageBox.Show(
                    "The basic (pnputil) backend disables devices at the driver level rather than " +
                    "filtering access. If something goes wrong mid-session, devices may appear " +
                    "disabled in Device Manager and require manual re-enabling or a reboot to recover.\n\n" +
                    "HidHide is strongly recommended. Only switch if you have a specific reason.",
                    "Switch to legacy backend?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    Set(ref _backendIsHidHide, false);
                    _settings.DeviceHidingBackend = DeviceHidingBackend.Pnputil;
                    Save();
                }
                // else: snap back — fire OnPropertyChanged so the UI re-reads the true value.
                OnPropertyChanged(nameof(BackendIsHidHide));
                OnPropertyChanged(nameof(BackendIsPnputil));
            }
        }
    }

    /// <summary>pnputil radio button — inverse of BackendIsHidHide.</summary>
    public bool BackendIsPnputil
    {
        get => !_backendIsHidHide;
        set { if (value) BackendIsHidHide = false; }
    }

    // ── Logging ───────────────────────────────────────────────────────────────────

    public IEnumerable<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public string LogFilePath => Logger.LogFilePath ?? "(logging not initialized)";

    public ICommand OpenLogFolderCommand    { get; }
    public ICommand OpenHidHideDownloadCommand { get; }

    public SettingsViewModel(SettingsStore store)
    {
        _store    = store;
        _settings = store.Load();

        _startWithWindows      = SettingsStore.GetStartWithWindows();
        _startMinimized        = _settings.StartMinimized;
        _processWatcherEnabled = _settings.ProcessWatcherEnabled;
        _logLevel              = _settings.LogLevel;
        _alwaysOnTop           = _settings.AlwaysOnTop;
        _backendIsHidHide      = _settings.DeviceHidingBackend != DeviceHidingBackend.Pnputil;

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
        _settings.StartWithWindows      = _startWithWindows;
        _settings.StartMinimized        = _startMinimized;
        _settings.ProcessWatcherEnabled = _processWatcherEnabled;
        _settings.LogLevel              = _logLevel;
        _settings.AlwaysOnTop           = _alwaysOnTop;
        _store.Save(_settings);
        App.Settings = _settings; // keep App.Settings in sync for UseHidHide check
    }
}
