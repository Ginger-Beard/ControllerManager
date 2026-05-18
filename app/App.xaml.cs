using System.Threading;
using System.Windows;
using HIDReorder.Models;
using HIDReorder.Services;

namespace HIDReorder;

public partial class App : Application
{
    private const string MutexName = "Global\\HIDReorder-3F8A1B2C-4D5E-6F7A-8B9C-0D1E2F3A4B5C";

    public static StateStore    State        { get; private set; } = null!;
    public static ProfileStore  ProfileStore { get; private set; } = null!;
    public static SettingsStore SettingsStore { get; private set; } = null!;
    public static AppSettings   Settings     { get; private set; } = null!;
    public static IpcServer?    Ipc          { get; private set; }

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "HID Reorder — Unhandled Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            MessageBox.Show(ex.ExceptionObject.ToString(), "HID Reorder — Fatal Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

        try
        {
            var args = e.Args;

            // ── Single-instance check ────────────────────────────────────────
            _mutex = new Mutex(true, MutexName, out bool isFirst);

            if (!isFirst)
            {
                // Another instance is running — forward args and exit
                ForwardToRunningInstance(args);
                Shutdown(0);
                return;
            }

            // ── Shared services ──────────────────────────────────────────────
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HIDReorder");
            Directory.CreateDirectory(appData);

            State        = new StateStore(Path.Combine(appData, "state.json"));
            ProfileStore = new ProfileStore(Path.Combine(appData, "profiles.json"));
            SettingsStore = new SettingsStore(Path.Combine(appData, "settings.json"));
            Settings     = SettingsStore.Load();
            State.RecoverOnStartup();

            // ── IPC server ───────────────────────────────────────────────────
            Ipc = new IpcServer();

            // ── CLI dispatch ─────────────────────────────────────────────────
            if (args.Length > 0 && HandleCliArgs(args))
                return; // headless mode (--restore-all)

            new Views.MainWindow().Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "HID Reorder — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool HandleCliArgs(string[] args)
    {
        if (args[0] == "--restore-all")
        {
            State.RestoreAll();
            return true; // exit after restoring, no UI
        }
        // --launch and --steam-wrap handled by IPC round-trip when app is already running;
        // when this IS the first instance, the main window handles them via IpcServer events.
        return false;
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        if (args.Length == 0) return;

        var req = args[0] switch
        {
            "--launch"      => new IpcRequest { Op = "launch",      Args = args[1..] },
            "--steam-wrap"  => new IpcRequest { Op = "steam-wrap",  Args = args[1..] },
            "--restore-all" => new IpcRequest { Op = "restore-all", Args = [] },
            _               => null,
        };

        if (req is not null)
            IpcClient.SendAsync(req).Wait(3000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Ipc?.Dispose();
        State?.RestoreAll();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
