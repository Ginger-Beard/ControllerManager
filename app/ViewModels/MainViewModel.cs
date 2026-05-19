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
        var orchestrator   = new LaunchOrchestrator(App.HidHide);
        orchestrator.ActivityLogged += (_, msg) => Logger.Write(msg);
        var processWatcher = new ProcessWatcher(App.ProfileStore, orchestrator);

        Devices   = new DevicesViewModel(new DeviceEnumerator(), App.HidHide);
        Games     = new GamesViewModel(App.ProfileStore, Devices);
        Dashboard = new DashboardViewModel(orchestrator, App.ProfileStore, Devices.Devices);
        Settings  = new SettingsViewModel(App.SettingsStore);

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

        if (App.Settings.ProcessWatcherEnabled)
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
        };
    }
}
