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

        DispatcherUnhandledException += (_, ex) =>
        {
            System.Windows.MessageBox.Show(
                ex.Exception.ToString(),
                "HID Reorder — Unhandled Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            System.Windows.MessageBox.Show(
                ex.ExceptionObject.ToString(),
                "HID Reorder — Fatal Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);

        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HIDReorder");
            Directory.CreateDirectory(appData);

            State        = new StateStore(Path.Combine(appData, "state.json"));
            ProfileStore = new ProfileStore(Path.Combine(appData, "profiles.json"));
            State.RecoverOnStartup();

            new Views.MainWindow().Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.ToString(),
                "HID Reorder — Startup Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        State.RestoreAll();
        base.OnExit(e);
    }
}
