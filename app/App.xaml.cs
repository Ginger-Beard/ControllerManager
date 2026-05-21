using System.Threading;
using System.Windows;
using ControllerManager.Cli;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager;

public partial class App : Application
{
    private const string MutexName = "Global\\ControllerManager-3F8A1B2C-4D5E-6F7A-8B9C-0D1E2F3A4B5C";

    public static ProfileStore       ProfileStore  { get; private set; } = null!;
    public static SettingsStore      SettingsStore { get; private set; } = null!;
    public static AppSettings        Settings      { get; set; }         = null!;
    public static HidHideClient      HidHide       { get; private set; } = null!;
    public static LaunchOrchestrator Orchestrator  { get; private set; } = null!;
    public static IpcServer?         Ipc           { get; private set; }
    public static TrayService?       Tray          { get; private set; }

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.WriteException("UI thread", ex.Exception);
            MessageBox.Show(ex.Exception.ToString(), "Controller Manager — Unhandled Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            var domainEx = ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown");
            Logger.WriteException("AppDomain (fatal=" + ex.IsTerminating + ")", domainEx);
            MessageBox.Show(ex.ExceptionObject?.ToString() ?? "Unknown error", "Controller Manager — Fatal Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Logger.WriteException("UnobservedTask", ex.Exception);
            ex.SetObserved();
        };

        try
        {
            var args = e.Args;

            // ── Single-instance check ────────────────────────────────────────
            bool isFirst;
            try
            {
                _mutex = new Mutex(true, MutexName, out isFirst);
            }
            catch (AbandonedMutexException)
            {
                isFirst = true;
            }

            if (!isFirst)
            {
                ForwardToRunningInstance(args);
                Shutdown(0);
                return;
            }

            // --startup is set by the scheduled task created by "Start with Windows".
            // Without it, the user launched the app directly (Start menu, double-click,
            // pinned shortcut, etc.) — always show the window in that case, regardless
            // of the "Start minimized to tray" setting (which only applies at boot).
            bool launchedAtStartup = args.Any(a =>
                a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

            // ── Shared services ──────────────────────────────────────────────
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ControllerManager");
            Directory.CreateDirectory(appData);

            Logger.Initialize(appData);

            ProfileStore  = new ProfileStore(Path.Combine(appData, "profiles.json"));
            SettingsStore = new SettingsStore(Path.Combine(appData, "settings.json"));
            Settings      = SettingsStore.Load();
            Logger.SetLevel(Settings.LogLevel);

            HidHide = new HidHideClient();
            HidHide.RecoverOnStartup();

            if (!HidHide.IsAvailable)
            {
                var result = MessageBox.Show(
                    "HidHide is not installed.\n\n" +
                    "Controller Manager needs the HidHide kernel driver to hide and reveal devices. " +
                    "Without it, profiles will have no effect.\n\n" +
                    "Click OK to open the HidHide download page.",
                    "HidHide not found",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.OK)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "https://github.com/nefarius/HidHide/releases/latest")
                        { UseShellExecute = true });
            }

            // Single orchestrator shared by tray, dashboard, and process watcher.
            // Constructed before MainWindow so MainViewModel can reference it.
            Orchestrator = new Services.LaunchOrchestrator(HidHide);
            Orchestrator.ActivityLogged += (_, msg) => Logger.Write(msg);

            // ── IPC server ───────────────────────────────────────────────────
            Ipc = new IpcServer();

            // ── CLI dispatch — headless steam-wrap when first instance ────────
            if (args.Length > 0 && args[0] == "--steam-wrap")
            {
                SteamWrapInvocation.HandleAsync(args[1..], HidHide, ProfileStore)
                    .GetAwaiter().GetResult();
                Shutdown(0);
                return;
            }

            var window = new Views.MainWindow();
            Tray = new Services.TrayService(window, ProfileStore, Orchestrator);

            if (launchedAtStartup && Settings.StartMinimized)
                Tray.HideToTray();
            else
                window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Controller Manager — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        // No args = user re-launched the exe (Start menu, double-click, etc.) — tell
        // the running instance to surface its window.
        if (args.Length == 0)
        {
            IpcClient.SendAsync(new IpcRequest { Op = "show" }).Wait(3000);
            return;
        }

        var req = args[0] switch
        {
            "--launch"     => new IpcRequest { Op = "launch",     Args = args[1..] },
            "--steam-wrap" => new IpcRequest { Op = "steam-wrap", Args = args[1..] },
            // --startup from a duplicate scheduled-task trigger — ignore silently
            _              => null,
        };

        if (req is not null)
            IpcClient.SendAsync(req).Wait(3000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Ipc?.Dispose();
        HidHide?.EndGameSession();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
