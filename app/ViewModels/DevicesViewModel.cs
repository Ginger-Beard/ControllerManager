using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ControllerManager.Models;
using ControllerManager.Services;

namespace ControllerManager.ViewModels;

public sealed class DevicesViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceEnumerator _enumerator;
    private readonly HidHideClient   _hidHide;
    internal DeviceEnumerator Enumerator => _enumerator;

    private bool   _showAllHid;
    private bool   _isRefreshing;
    private bool   _refreshInFlight; // shared stacking guard for user + silent refreshes
    private string _statusText = "Ready.";
    private HidDevice? _selectedDevice;

    private HidInputMonitor? _monitor;
    private bool             _isMonitorExpanded;

    // Auto-refresh tick. 2s captures hot-plugs fast enough that a Moonlight/
    // Artemis client connecting (or a controller picked up at the desk) shows
    // up in the picker before the user looks for it, without hammering the
    // HID driver stack. Refresh() short-circuits if one is already in flight,
    // so a slow enumeration won't stack ticks. MergeDevices fires
    // CollectionChanged only on actual adds/removes, so the picker rebuild
    // (ProfileEditorViewModel listens on this) doesn't fire every 2s — only
    // when devices actually changed.
    private readonly DispatcherTimer _autoRefresh = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    public ObservableCollection<HidDevice>       Devices { get; } = [];
    public ObservableCollection<AxisViewModel>   Axes    { get; } = [];
    public ObservableCollection<ButtonViewModel> Buttons { get; } = [];
    public ObservableCollection<StickViewModel>  Sticks  { get; } = [];

    // Maps each Sticks[i] to the two indexes into Axes that drive its X/Y values.
    // (-1 in either slot means "no axis at that index" — shouldn't happen, but
    // makes the indexer-style update loop below safe.)
    private readonly List<(int XIdx, int YIdx)> _stickAxisIndexes = [];

    public bool IsMonitorExpanded
    {
        get => _isMonitorExpanded;
        set { if (Set(ref _isMonitorExpanded, value)) UpdateMonitor(); }
    }

    public bool ShowAllHid
    {
        get => _showAllHid;
        set { if (Set(ref _showAllHid, value)) Refresh(); }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (Set(ref _isRefreshing, value))
                Application.Current.Dispatcher.Invoke(
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public HidDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { if (Set(ref _selectedDevice, value)) UpdateMonitor(); }
    }

    public ICommand RefreshCommand           { get; }
    public ICommand ToggleEnabledCommand     { get; }
    public ICommand CopyAllCommand           { get; }
    public ICommand CopyFriendlyNameCommand  { get; }
    public ICommand CopyVidPidCommand        { get; }
    public ICommand CopyInstanceIdCommand    { get; }
    public ICommand CopyInterfacePathCommand { get; }

    public DevicesViewModel(DeviceEnumerator enumerator, HidHideClient hidHide)
    {
        _enumerator = enumerator;
        _hidHide    = hidHide;

        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsRefreshing);
        CopyAllCommand = new RelayCommand(_ =>
        {
            if (Devices.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Controller Manager — Device Dump  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine(new string('-', 80));
            foreach (var d in Devices)
                sb.AppendLine($"{(d.IsEnabled ? "ON " : "OFF")}  {d.VidPid,-22}  {d.FriendlyName,-55}  {d.InstanceId}");
            Clipboard.SetText(sb.ToString());
        }, _ => Devices.Count > 0);
        ToggleEnabledCommand = new RelayCommand(
            p => ToggleEnabled(p as HidDevice ?? SelectedDevice),
            p => (p as HidDevice ?? SelectedDevice) is not null);
        CopyFriendlyNameCommand  = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.FriendlyName));
        CopyVidPidCommand        = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.VidPid));
        CopyInstanceIdCommand    = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.InstanceId));
        CopyInterfacePathCommand = new RelayCommand(
            p => CopyToClipboard((p as HidDevice ?? SelectedDevice)?.DeviceInterfacePath));

        Refresh();

        _autoRefresh.Tick += (_, _) => { if (!_refreshInFlight) Refresh(silent: true); };
        _autoRefresh.Start();
    }

    public void Refresh() => Refresh(silent: false);

    // silent=true keeps both StatusText and IsRefreshing untouched so the 2s
    // auto-refresh doesn't (a) flicker "Scanning devices..." into the user's
    // face nor (b) flip the Refresh button's enabled state on every tick.
    // Errors still surface either way — silent failures during auto-refresh
    // would be worse. _refreshInFlight gates both user and silent refreshes,
    // preventing two refreshes from overlapping regardless of who triggered.
    private void Refresh(bool silent)
    {
        if (_refreshInFlight) return; // defensive — tick handler already checks
        _refreshInFlight = true;

        if (!silent)
        {
            IsRefreshing = true;
            StatusText   = "Scanning devices...";
        }

        Task.Run(() =>
        {
            try
            {
                var list = _enumerator.GetAll(_showAllHid);

                // Overlay the persistent HidHide blacklist only — the Devices tab
                // controls persistent system-wide hiding, not session hiding.
                // Session state is shown separately on the Dashboard.
                if (_hidHide.IsAvailable)
                {
                    var persistent = _hidHide.GetBlacklist();
                    foreach (var d in list)
                    {
                        if (persistent.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase))
                            d.IsEnabled = false;
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MergeDevices(list);
                    if (!silent)
                    {
                        StatusText   = $"{list.Count} device(s) found.";
                        IsRefreshing = false;
                    }
                    _refreshInFlight = false;
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Error: {ex.Message}";
                    if (!silent) IsRefreshing = false;
                    _refreshInFlight = false;
                });
            }
        });
    }

    private void MergeDevices(List<HidDevice> incoming)
    {
        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!incoming.Any(d => d.InstanceId == Devices[i].InstanceId))
                Devices.RemoveAt(i);
        }

        for (int i = 0; i < incoming.Count; i++)
        {
            var next     = incoming[i];
            var existing = Devices.FirstOrDefault(d => d.InstanceId == next.InstanceId);

            if (existing is null)
                Devices.Insert(Math.Min(i, Devices.Count), next);
            else
                existing.IsEnabled = next.IsEnabled;
        }
    }

    private void ToggleEnabled(HidDevice? device)
    {
        if (device is null) return;
        if (!_hidHide.IsAvailable)
        {
            StatusText = "HidHide is not installed.";
            return;
        }

        IsRefreshing = true;
        StatusText   = device.IsEnabled
            ? $"Hiding {device.FriendlyName}..."
            : $"Showing {device.FriendlyName}...";

        // Apply to every HID child interface that shares the same physical device.
        // Composite devices (e.g. wheel + button box on one USB controller) need
        // each MI_NN interface explicitly blacklisted — HidHide's kernel filter
        // does direct string compare with no ancestor traversal.
        var ids = device.ChildInstanceIds.Count > 0
            ? device.ChildInstanceIds
            : [device.InstanceId];

        Task.Run(() =>
        {
            try
            {
                foreach (var id in ids)
                {
                    if (device.IsEnabled)
                        _hidHide.AddToPersistentBlacklist(id);
                    else
                        _hidHide.RemoveFromPersistentBlacklist(id);
                }

                Application.Current.Dispatcher.Invoke(Refresh);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText   = $"Error: {ex.Message}";
                    IsRefreshing = false;
                });
            }
        });
    }

    private static void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    // ── Live input monitor ──────────────────────────────────────────────────────

    private void UpdateMonitor()
    {
        StopMonitor();
        if (!_isMonitorExpanded) return;
        if (_selectedDevice is null) return;

        var path = _selectedDevice.DeviceInterfacePath;
        if (string.IsNullOrEmpty(path)) return;

        var monitor = new HidInputMonitor();
        bool success = false;
        try
        {
            if (!monitor.Open(path)) return;

            Axes.Clear();
            Buttons.Clear();
            Sticks.Clear();
            _stickAxisIndexes.Clear();

            foreach (var ax in monitor.Axes) Axes.Add(new AxisViewModel(ax.Name));
            for (int i = 1; i <= monitor.TotalButtonCount; i++)
                Buttons.Add(new ButtonViewModel(i.ToString()));

            // Detect 2D stick pairs from the axis list.
            //   X (0x30) + Y (0x31)  → "Left Stick"
            //   Rx (0x33) + Ry (0x34) → "Right Stick"
            //   Z (0x32) + Rz (0x35)  → "Z/Rz" (often triggers, sometimes a stick;
            //                                   we label generically so the user can tell)
            // Generic Desktop usage page only — vendor-defined axes don't get a pad.
            AddPairIfPresent(monitor, 0x30, 0x31, "Left Stick");
            AddPairIfPresent(monitor, 0x33, 0x34, "Right Stick");
            AddPairIfPresent(monitor, 0x32, 0x35, "Z / Rz");

            monitor.AxesUpdated    += OnAxesUpdated;
            monitor.ButtonsUpdated += OnButtonsUpdated;

            _monitor = monitor;
            _monitor.StartPolling(action =>
                Application.Current?.Dispatcher.BeginInvoke(action));
            success = true;
        }
        finally
        {
            if (!success) monitor.Dispose();
        }
    }

    private void AddPairIfPresent(HidInputMonitor monitor, ushort xUsage, ushort yUsage, string label)
    {
        // A real 2D stick descriptor is, by HID convention:
        //   Collection (Application, Joystick/GamePad)
        //     Collection (Physical, Pointer)   ← LinkUsage = 0x01 (Pointer)
        //       Usage X, Usage Y               ← exactly these two GD axes
        //     End Collection
        //   End Collection
        //
        // Sim pedals (Heusinkveld, Fanatec, MOZA pedals) typically dump three+
        // axes at the Application Joystick level with no Pointer sub-collection
        // and no special grouping — X/Y/Rz all share LinkUsage=0x04 (Joystick).
        // Requiring the parent to be Pointer specifically AND to contain only
        // these two GD axes filters those out reliably.
        int xIdx = -1, yIdx = -1;
        for (int i = 0; i < monitor.Axes.Count; i++)
        {
            var ax = monitor.Axes[i];
            if (ax.UsagePage != 0x01) continue;
            if (ax.Usage == xUsage && xIdx < 0) xIdx = i;
            if (ax.Usage == yUsage && yIdx < 0) yIdx = i;
        }
        if (xIdx < 0 || yIdx < 0) return;

        var x = monitor.Axes[xIdx];
        var y = monitor.Axes[yIdx];
        if (x.LinkCollection != y.LinkCollection) return;

        // Strict: only a Pointer collection (LinkUsagePage=0x01, LinkUsage=0x01)
        // qualifies as a stick parent. Joystick/Game Pad top-level collections
        // don't — those are application-level containers that hold whatever
        // analog inputs the device exposes.
        if (x.LinkUsagePage != 0x01 || x.LinkUsage != 0x01) return;

        // And: the parent collection must contain only these two GD axes. If a
        // third GD axis shares the same LinkCollection, this isn't a 2D pointer
        // — it's a multi-axis grouping (rare but defensive).
        int siblingsInCollection = 0;
        for (int i = 0; i < monitor.Axes.Count; i++)
        {
            if (monitor.Axes[i].UsagePage == 0x01 &&
                monitor.Axes[i].LinkCollection == x.LinkCollection)
                siblingsInCollection++;
        }
        if (siblingsInCollection != 2) return;

        Sticks.Add(new StickViewModel(label));
        _stickAxisIndexes.Add((xIdx, yIdx));
    }

    private void OnAxesUpdated(float[] values)
    {
        int n = Math.Min(values.Length, Axes.Count);
        for (int i = 0; i < n; i++) { Axes[i].Value = values[i]; Axes[i].RawText = values[i].ToString("0.00"); }

        // Push X/Y into each detected stick pair. Bounds-check in case axes shrink
        // between polling ticks (defensive — shouldn't happen but cheap to verify).
        for (int s = 0; s < _stickAxisIndexes.Count && s < Sticks.Count; s++)
        {
            var (xIdx, yIdx) = _stickAxisIndexes[s];
            if (xIdx < 0 || xIdx >= values.Length) continue;
            if (yIdx < 0 || yIdx >= values.Length) continue;
            Sticks[s].Update(values[xIdx], values[yIdx]);
        }
    }

    private void OnButtonsUpdated(bool[] pressed)
    {
        int n = Math.Min(pressed.Length, Buttons.Count);
        for (int i = 0; i < n; i++) Buttons[i].IsPressed = pressed[i];
    }

    private void StopMonitor()
    {
        if (_monitor is not null)
        {
            _monitor.AxesUpdated    -= OnAxesUpdated;
            _monitor.ButtonsUpdated -= OnButtonsUpdated;
            _monitor.Stop();
            _monitor.Dispose();
            _monitor = null;
        }
        Axes.Clear();
        Buttons.Clear();
        Sticks.Clear();
        _stickAxisIndexes.Clear();
    }

    public void Dispose()
    {
        _autoRefresh.Stop();
        StopMonitor();
    }
}
