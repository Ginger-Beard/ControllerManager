using System.Windows;

namespace HIDReorder.Services;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Window                          _window;

    public TrayService(Window window, ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        _window = window;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "HID Reorder",
            Icon    = LoadIcon(),
            Visible = true,
        };

        _icon.DoubleClick      += (_, _) => ShowWindow();
        _icon.ContextMenuStrip  = BuildMenu(profiles, orchestrator);

        _window.StateChanged += OnStateChanged;
        _window.Closing      += OnClosing;

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

    private System.Windows.Forms.ContextMenuStrip BuildMenu(ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu, profiles, orchestrator);
        return menu;
    }

    private void RebuildMenu(System.Windows.Forms.ContextMenuStrip menu, ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        menu.Items.Clear();

        var showItem = new System.Windows.Forms.ToolStripMenuItem("Show HID Reorder");
        showItem.Font = new System.Drawing.Font(showItem.Font, System.Drawing.FontStyle.Bold);
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        foreach (var profile in profiles.Load())
        {
            var p    = profile;
            var item = new System.Windows.Forms.ToolStripMenuItem($"▶  {p.Name}");
            item.Click += (_, _) =>
            {
                if (!orchestrator.IsRunning)
                    orchestrator.Start(p);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var restoreItem = new System.Windows.Forms.ToolStripMenuItem("Restore All Devices");
        restoreItem.Click += (_, _) => App.State.RestoreAll();
        menu.Items.Add(restoreItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            App.State.RestoreAll();
            _icon.Visible = false;
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);
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
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _window.StateChanged -= OnStateChanged;
        _window.Closing      -= OnClosing;
    }
}
