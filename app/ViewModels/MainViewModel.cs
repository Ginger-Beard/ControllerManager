using HIDReorder.Services;

namespace HIDReorder.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public DevicesViewModel Devices { get; }
    public GamesViewModel   Games   { get; }

    public MainViewModel()
    {
        var appDir   = AppContext.BaseDirectory;
        var resolver = new VidResolver(Path.Combine(appDir, "vid-names.json"));

        Devices = new DevicesViewModel(new DeviceEnumerator(resolver));
        Games   = new GamesViewModel(App.ProfileStore, Devices);
    }
}
