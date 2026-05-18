using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel   Devices   { get; }
    public GamesViewModel     Games     { get; }
    public SettingsViewModel  Settings  { get; }

    public MainViewModel()
    {
        var resolver      = new VidResolver();
        var orchestrator  = new LaunchOrchestrator(App.State);
        var processWatcher = new ProcessWatcher(App.ProfileStore, orchestrator);

        Devices  = new DevicesViewModel(new DeviceEnumerator(resolver));
        Games    = new GamesViewModel(App.ProfileStore, Devices);
        Dashboard = new DashboardViewModel(orchestrator, App.ProfileStore);
        Settings  = new SettingsViewModel(App.SettingsStore);

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
