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
        // Always inject the client instance so dynamic backend switches (Settings tab)
        // take effect immediately. IsHidHideBackend / UseHidHide checks at call sites
        // decide which path is actually used.
        var orchestrator   = new LaunchOrchestrator(App.State, App.HidHide);
        var processWatcher = new ProcessWatcher(App.ProfileStore, orchestrator);

        Devices   = new DevicesViewModel(new DeviceEnumerator(), App.HidHide);
        Games     = new GamesViewModel(App.ProfileStore, Devices);
        Dashboard = new DashboardViewModel(orchestrator, App.ProfileStore);
        Settings  = new SettingsViewModel(App.SettingsStore);

        // Refresh the Devices tab after an orchestrator session ends so enable/disable
        // state changes made during the flow are reflected without a manual refresh.
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
            else if (req.Op == "restore-all")
            {
                App.State.RestoreAll();
            }
        };
    }

}
