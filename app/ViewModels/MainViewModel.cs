using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel   Devices   { get; }
    public GamesViewModel     Games     { get; }

    public MainViewModel()
    {
        var resolver = new VidResolver();

        Devices   = new DevicesViewModel(new DeviceEnumerator(resolver));
        Games     = new GamesViewModel(App.ProfileStore, Devices);
        Dashboard = new DashboardViewModel(new LaunchOrchestrator(App.State), App.ProfileStore);
    }
}
