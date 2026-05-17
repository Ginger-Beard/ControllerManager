using System.Windows;
using HIDReorder.Services;

namespace HIDReorder;

public partial class App : Application
{
    public static StateStore   State        { get; private set; } = null!;
    public static ProfileStore ProfileStore { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HIDReorder");
        Directory.CreateDirectory(appData);

        State        = new StateStore(Path.Combine(appData, "state.json"));
        ProfileStore = new ProfileStore(Path.Combine(appData, "profiles.json"));
        State.RecoverOnStartup();

        // TODO: mutex check + IPC client for single-instance
        // TODO: CLI arg dispatch (--launch, --steam-wrap, --restore-all)

        new Views.MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        State.RestoreAll();
        base.OnExit(e);
    }
}
