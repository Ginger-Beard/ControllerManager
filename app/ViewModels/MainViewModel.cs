using ControllerManager.Cli;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel   Devices   { get; }
    public GamesViewModel     Games     { get; }
    public SettingsViewModel  Settings  { get; }

    private CancellationTokenSource? _refreshDebounce;

    public MainViewModel()
    {
        var orchestrator = App.Orchestrator;
        var enumerator   = new DeviceEnumerator();

        Devices   = new DevicesViewModel(enumerator, App.HidHide);
        Games     = new GamesViewModel(App.ProfileStore, Devices);
        Dashboard = new DashboardViewModel(orchestrator, App.ProfileStore, Devices.Devices, enumerator);
        Settings  = new SettingsViewModel(App.SettingsStore);

        // ProcessWatcher always runs — per-profile flags (Profile.ProcessWatcherEnabled)
        // gate individual profiles, no global on/off needed.
        var processWatcher = new ProcessWatcher(App.ProfileStore, orchestrator);

        orchestrator.StateChanged += (_, state) =>
        {
            if (state != OrchestratorState.Idle) return;
            _refreshDebounce?.Cancel();
            _refreshDebounce = new CancellationTokenSource();
            var token = _refreshDebounce.Token;
            Task.Delay(400, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    System.Windows.Application.Current.Dispatcher.Invoke(Devices.Refresh);
            });
        };

        processWatcher.Start();

        App.Ipc?.RequestReceived += (_, req) =>
        {
            if (req.Op == "launch" && Guid.TryParse(req.Args.FirstOrDefault(), out var id))
            {
                var profile = App.ProfileStore.Load().FirstOrDefault(p => p.Id == id);
                if (profile is not null)
                    System.Windows.Application.Current.Dispatcher.Invoke(
                        () => orchestrator.Start(profile));
            }
            else if (req.Op == "steam-wrap")
            {
                _ = Task.Run(() => SteamWrapInvocation.HandleAsync(
                    req.Args, App.HidHide, App.ProfileStore));
            }
            else if (req.Op == "show")
            {
                System.Windows.Application.Current.Dispatcher.Invoke(
                    () => App.Tray?.ShowWindow());
            }
        };
    }
}
