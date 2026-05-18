using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;

namespace HIDReorder.Services;

public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon  _icon;
    private readonly Window       _window;

    public TrayService(Window window, ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        _window = window;

        _icon = new TaskbarIcon
        {
            ToolTipText = "HID Reorder",
            Icon        = LoadIcon(),
        };

        _icon.TrayMouseDoubleClick += (_, _) => ShowWindow();
        _icon.ContextMenu           = BuildMenu(profiles, orchestrator);

        // Minimize → tray
        _window.StateChanged += OnStateChanged;

        // X button → tray (not exit)
        _window.Closing += OnClosing;

        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void HideToTray() => _window.Hide();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized)
            _window.Hide();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        _window.Hide();
    }

    private ContextMenu BuildMenu(ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        var menu = new ContextMenu();

        // Rebuild on open so profile list stays current
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();

            var showItem = new MenuItem { Header = "Show HID Reorder", FontWeight = FontWeights.Bold };
            showItem.Click += (_, _) => ShowWindow();
            menu.Items.Add(showItem);
            menu.Items.Add(new Separator());

            var loadedProfiles = profiles.Load();
            if (loadedProfiles.Count > 0)
            {
                foreach (var profile in loadedProfiles)
                {
                    var p    = profile;
                    var item = new MenuItem { Header = $"▶  {p.Name}" };
                    item.Click += (_, _) =>
                    {
                        if (!orchestrator.IsRunning)
                            orchestrator.Start(p);
                    };
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
            }

            var restoreItem = new MenuItem { Header = "Restore All Devices" };
            restoreItem.Click += (_, _) => App.State.RestoreAll();
            menu.Items.Add(restoreItem);
            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) =>
            {
                App.State.RestoreAll();
                _icon.Dispose();
                Application.Current.Shutdown();
            };
            menu.Items.Add(exitItem);
        };

        return menu;
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null && File.Exists(exe))
                return System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        _window.StateChanged -= OnStateChanged;
        _window.Closing      -= OnClosing;
        _icon.Dispose();
    }
}
