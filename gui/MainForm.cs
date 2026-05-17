using HidReorder.Controls;
using HidReorder.Core;

namespace HidReorder;

public sealed class MainForm : Form
{
    // ── Services ────────────────────────────────────────────────────────────────
    private readonly VidResolver    _resolver;
    private readonly ProfileManager _profiles;
    private readonly HidMonitor     _monitor;

    // ── Order tab ───────────────────────────────────────────────────────────────
    private readonly ReorderListBox _deviceList   = new();
    private readonly ComboBox       _profileCombo = new();
    private readonly Button         _saveBtn      = new();
    private readonly Button         _deleteBtn    = new();
    private readonly Button         _refreshBtn   = new();
    private readonly Button         _reorderBtn   = new();
    private readonly Button         _pinBtn       = new();
    private readonly Button         _launchBtn    = new();
    private readonly NumericUpDown  _launchDelay  = new();
    private readonly Label          _statusLabel  = new();

    // ── Launch mode state ────────────────────────────────────────────────────────
    private readonly System.Windows.Forms.Timer _launchTimer = new();
    private List<SimDevice> _launchDisabled = [];
    private int             _launchCountdown;

    // ── Drift tab ───────────────────────────────────────────────────────────────
    private readonly DriftDisplay    _driftDisplay   = new();
    private readonly NumericUpDown   _thresholdBox   = new();
    private readonly Button          _startBtn       = new();
    private readonly Button          _stopBtn        = new();
    private readonly Label           _driftStatus    = new();
    private readonly System.Windows.Forms.Timer _driftTimer = new();

    // ── State ────────────────────────────────────────────────────────────────────
    private bool _profileApplying; // suppress OrderChanged while loading a profile

    private readonly string _ignoredPath;

    public MainForm()
    {
        var appDir   = AppContext.BaseDirectory;
        _resolver    = new VidResolver(Path.Combine(appDir, "vid-names.json"));
        _profiles    = new ProfileManager(Path.Combine(appDir, "profiles.json"));
        _monitor     = new HidMonitor(_resolver);
        _ignoredPath = Path.Combine(appDir, "ignored.json");

        InitForm();
        LoadIgnored();
        RefreshDeviceList();
        RefreshProfileCombo();
        EnsureDefaultProfile();

        _deviceList.IgnoredChanged       += (_, _) => SaveIgnored();
        _deviceList.EnableToggleRequested += (_, dev) =>
        {
            SetStatus(dev.IsEnabled ? $"Disabling {dev.DisplayName}..." : $"Enabling {dev.DisplayName}...");
            _reorderBtn.Enabled = false;
            Task.Run(() =>
            {
                try   { DeviceManager.SetDeviceEnabled(dev, !dev.IsEnabled); }
                catch (Exception ex) { Invoke(() => SetStatus($"Error: {ex.Message}")); return; }
                Invoke(() => { RefreshDeviceList(); _reorderBtn.Enabled = true; });
            });
        };
    }

    private void LoadIgnored()
    {
        if (!File.Exists(_ignoredPath)) return;
        try
        {
            var keys = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                File.ReadAllText(_ignoredPath));
            if (keys is not null) _deviceList.LoadIgnored(keys);
        }
        catch { }
    }

    private void SaveIgnored()
    {
        try
        {
            File.WriteAllText(_ignoredPath,
                System.Text.Json.JsonSerializer.Serialize(
                    _deviceList.GetIgnoredKeys().ToList()));
        }
        catch { }
    }

    // ── Form construction ────────────────────────────────────────────────────────

    private void InitForm()
    {
        Text            = "HID Reorder";
        Size            = new Size(520, 520);
        MinimumSize     = new Size(420, 420);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition   = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildOrderTab());
        // tabs.TabPages.Add(BuildDriftTab());  // not ready to ship
        Controls.Add(tabs);

        _driftTimer.Interval  = 150;
        _driftTimer.Tick     += DriftTimer_Tick;

        _launchTimer.Interval = 1000;
        _launchTimer.Tick    += LaunchTimer_Tick;

        FormClosing += (_, _) => { _driftTimer.Stop(); _launchTimer.Stop(); };
    }

    // ── Order tab ────────────────────────────────────────────────────────────────

    private TabPage BuildOrderTab()
    {
        var tab = new TabPage("Device Order") { Padding = new Padding(8) };

        // Profile row (top)
        var profilePanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 36,
            Padding       = Padding.Empty,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
        };

        _profileCombo.Width         = 160;
        _profileCombo.DropDownStyle = ComboBoxStyle.DropDown;
        _profileCombo.SelectedIndexChanged += ProfileCombo_SelectedIndexChanged;

        StyleButton(_saveBtn,   "Save",   60);
        StyleButton(_deleteBtn, "Delete", 70);
        _saveBtn.Click   += SaveProfile_Click;
        _deleteBtn.Click += DeleteProfile_Click;

        var profileLabel = new Label
        {
            Text      = "Profile:",
            AutoSize  = false,
            Width     = 52,
            Height    = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin    = new Padding(4, 5, 0, 5),
        };

        profilePanel.Controls.AddRange(
            new Control[] { profileLabel, _profileCombo, _saveBtn, _deleteBtn });

        // Status (bottom)
        _statusLabel.Dock      = DockStyle.Bottom;
        _statusLabel.Height    = 22;
        _statusLabel.Text      = "Ready.";
        _statusLabel.ForeColor = SystemColors.GrayText;

        // Bottom button row — pin on left, Refresh + Reorder on right
        var bottomTable = new TableLayoutPanel
        {
            Dock        = DockStyle.Bottom,
            Height      = 36,
            ColumnCount = 2,
            RowCount    = 1,
            Padding     = Padding.Empty,
            Margin      = Padding.Empty,
        };
        bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        StyleButton(_pinBtn, "📌", 36);
        _pinBtn.Click += PinBtn_Click;
        new ToolTip().SetToolTip(_pinBtn, "Keep window on top of other windows");

        var rightFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents  = false,
            Padding       = Padding.Empty,
        };
        StyleButton(_reorderBtn, "Reorder Devices", 140);
        StyleButton(_refreshBtn, "Refresh",          80);
        StyleButton(_launchBtn,  "🎮 Launch FH6",   110);
        _reorderBtn.BackColor = Color.FromArgb(0, 100, 180);
        _reorderBtn.ForeColor = Color.White;
        _reorderBtn.FlatAppearance.BorderColor = Color.FromArgb(0, 80, 160);
        _reorderBtn.Click += ReorderBtn_Click;
        _refreshBtn.Click += (_, _) => RefreshDeviceList();
        _launchBtn.Click  += LaunchFH6_Click;

        _launchDelay.Minimum = 5;
        _launchDelay.Maximum = 120;
        _launchDelay.Value   = 30;
        _launchDelay.Width   = 44;
        _launchDelay.Height  = 26;
        _launchDelay.Margin  = new Padding(4, 5, 0, 5);
        new ToolTip().SetToolTip(_launchDelay, "Seconds after launch before re-enabling disabled devices");

        rightFlow.Controls.AddRange(new Control[] { _reorderBtn, _refreshBtn, _launchBtn, _launchDelay });

        bottomTable.Controls.Add(_pinBtn,   0, 0);
        bottomTable.Controls.Add(rightFlow, 1, 0);

        var bottomPanel = bottomTable;

        // Device list (fills remaining space)
        _deviceList.Dock = DockStyle.Fill;
        _deviceList.OrderChanged += (_, _) =>
        {
            if (!_profileApplying) _profileCombo.Text = "";
        };

        tab.Controls.Add(_deviceList);
        tab.Controls.Add(bottomPanel);
        tab.Controls.Add(_statusLabel);
        tab.Controls.Add(profilePanel);
        return tab;
    }

    // ── Drift tab ────────────────────────────────────────────────────────────────

    private TabPage BuildDriftTab()
    {
        var tab = new TabPage("Drift Monitor") { Padding = new Padding(8) };

        // Control row (top)
        var controlPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 36,
            Padding       = Padding.Empty,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
        };

        _thresholdBox.Minimum   = 1;
        _thresholdBox.Maximum   = 49;
        _thresholdBox.Value     = 5;
        _thresholdBox.Width     = 44;
        _thresholdBox.ValueChanged += (_, _) => _monitor.Threshold = (int)_thresholdBox.Value;

        StyleButton(_startBtn, "Start", 60);
        StyleButton(_stopBtn,  "Stop",  60);
        _startBtn.Click += StartDrift_Click;
        _stopBtn.Click  += StopDrift_Click;
        _stopBtn.Enabled = false;

        var threshLabel  = new Label
        {
            Text = "Flag if >", AutoSize = false, Width = 60, Height = 26,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 5, 0, 5),
        };
        var threshLabel2 = new Label
        {
            Text = "% from center", AutoSize = false, Width = 90, Height = 26,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 5, 8, 5),
        };

        controlPanel.Controls.AddRange(
            new Control[] { threshLabel, _thresholdBox, threshLabel2, _startBtn, _stopBtn });

        // Status
        _driftStatus.Dock      = DockStyle.Bottom;
        _driftStatus.Height    = 22;
        _driftStatus.Text      = "Press Start to begin monitoring.";
        _driftStatus.ForeColor = SystemColors.GrayText;

        // Display
        _driftDisplay.Dock = DockStyle.Fill;

        tab.Controls.Add(_driftDisplay);
        tab.Controls.Add(_driftStatus);
        tab.Controls.Add(controlPanel);
        return tab;
    }

    private static void StyleButton(Button btn, string text, int width)
    {
        btn.Text      = text;
        btn.Width     = width;
        btn.Height    = 26;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = SystemColors.ControlDark;
        btn.Margin    = new Padding(4, 5, 0, 5);  // symmetric top/bottom for centering
    }

    // ── Order tab logic ──────────────────────────────────────────────────────────

    private void RefreshDeviceList()
    {
        SetStatus("Scanning devices...");
        try
        {
            var devices = DeviceManager.GetGameControllers(_resolver);
            var current = _deviceList.OrderedDevices.ToList();

            // If a profile is selected, try to maintain that ordering over the new device list
            if (current.Count > 0)
            {
                var patterns = current.Select(d => d.VidPattern).ToList();
                devices = ApplyOrder(devices, patterns);
            }

            _deviceList.SetDevices(devices);
            SetStatus($"{devices.Count} device(s) found.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void RefreshProfileCombo()
    {
        var text = _profileCombo.Text;
        _profileCombo.Items.Clear();
        foreach (var p in _profiles.All)
            _profileCombo.Items.Add(p.Name);
        _profileCombo.Text = text;
    }

    private void ProfileCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_profileCombo.SelectedItem is not string name) return;
        var profile = _profiles.Get(name);
        if (profile is null) return;

        _profileApplying = true;
        var current  = _deviceList.OrderedDevices.ToList();
        var reordered = ApplyOrder(current, profile.DevicePatterns);
        _deviceList.SetDevices(reordered);
        _profileApplying = false;
    }

    private void SaveProfile_Click(object? sender, EventArgs e)
    {
        var name = _profileCombo.Text.Trim();
        if (string.IsNullOrEmpty(name)) { SetStatus("Enter a profile name first."); return; }

        _profiles.Save(name, _deviceList.OrderedDevices);
        RefreshProfileCombo();
        _profileCombo.Text = name;
        SetStatus($"Profile \"{name}\" saved.");
    }

    private void DeleteProfile_Click(object? sender, EventArgs e)
    {
        var name = _profileCombo.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (MessageBox.Show($"Delete profile \"{name}\"?", "Confirm",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _profiles.Delete(name);
        RefreshProfileCombo();
        _profileCombo.Text = "";
        SetStatus($"Profile \"{name}\" deleted.");
    }

    private void ReorderBtn_Click(object? sender, EventArgs e)
    {
        var checked_  = _deviceList.CheckedDevices.ToList();
        var disabled  = _deviceList.DisabledDevices.ToList();

        if (checked_.Count == 0)
        {
            SetStatus("Check at least one device to include in the reorder.");
            return;
        }

        var first = checked_[0];
        var rest  = checked_.Skip(1).ToList();

        _reorderBtn.Enabled = false;
        SetStatus("Reordering — devices will briefly disconnect...");

        // Progress<T> captures the UI SynchronizationContext — no Invoke needed
        var prog = new Progress<string>(msg =>
        {
            SetStatus(msg);
            if (!msg.StartsWith("Disabling"))
                _reorderBtn.Enabled = true;
        });

        Task.Run(() =>
        {
            try   { DeviceManager.Reorder(first, rest, disabled, prog); }
            catch (Exception ex) { Invoke(() => { SetStatus($"Error: {ex.Message}"); _reorderBtn.Enabled = true; }); }
        });
    }

    private void EnsureDefaultProfile()
    {
        const string name = "Default";
        if (_profiles.Get(name) is null)
        {
            _profiles.Save(name, _deviceList.OrderedDevices);
            RefreshProfileCombo();
        }
        int idx = _profileCombo.Items.IndexOf(name);
        if (idx >= 0) _profileCombo.SelectedIndex = idx;
    }

    private void LaunchFH6_Click(object? sender, EventArgs e)
    {
        _launchDisabled = _deviceList.DisabledDevices.ToList();

        if (_launchDisabled.Count == 0)
        {
            LaunchGame();
            SetStatus("FH6 launched.");
            return;
        }

        _launchBtn.Enabled  = false;
        _reorderBtn.Enabled = false;
        SetStatus("Disabling devices...");

        Task.Run(() =>
        {
            foreach (var dev in _launchDisabled)
                DeviceManager.SetDeviceEnabled(dev, false);

            Invoke(() =>
            {
                LaunchGame();
                _launchCountdown = (int)_launchDelay.Value;
                _launchTimer.Start();
                SetStatus($"FH6 launched — re-enabling devices in {_launchCountdown}s...");
            });
        });
    }

    private void LaunchTimer_Tick(object? sender, EventArgs e)
    {
        _launchCountdown--;
        if (_launchCountdown > 0)
        {
            SetStatus($"Re-enabling devices in {_launchCountdown}s...");
            return;
        }

        _launchTimer.Stop();
        SetStatus("Re-enabling devices...");

        Task.Run(() =>
        {
            foreach (var dev in _launchDisabled)
                DeviceManager.SetDeviceEnabled(dev, true);

            Invoke(() =>
            {
                RefreshDeviceList();
                _launchBtn.Enabled  = true;
                _reorderBtn.Enabled = true;
                SetStatus("All devices re-enabled — good luck!");
                _launchDisabled = [];
            });
        });
    }

    private static void LaunchGame() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "steam://rungameid/2483190",
            UseShellExecute = true,
        });

    private void PinBtn_Click(object? sender, EventArgs e)
    {
        TopMost = !TopMost;
        _pinBtn.BackColor = TopMost ? Color.FromArgb(0, 100, 180) : SystemColors.Control;
        _pinBtn.ForeColor = TopMost ? Color.White : SystemColors.ControlText;
        _pinBtn.FlatAppearance.BorderColor = TopMost
            ? Color.FromArgb(0, 80, 160) : SystemColors.ControlDark;
    }

    private void SetStatus(string msg) =>
        _statusLabel.Text = msg;

    // ── Drift monitor logic ──────────────────────────────────────────────────────

    private void StartDrift_Click(object? sender, EventArgs e)
    {
        _monitor.Threshold = (int)_thresholdBox.Value;
        _driftTimer.Start();
        _startBtn.Enabled = false;
        _stopBtn.Enabled  = true;
        _driftStatus.Text = "Monitoring... axes flagged RED are >threshold% from center.";
    }

    private void StopDrift_Click(object? sender, EventArgs e)
    {
        _driftTimer.Stop();
        _startBtn.Enabled = true;
        _stopBtn.Enabled  = false;
        _driftStatus.Text = "Stopped.";
    }

    private void DriftTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var readings = _monitor.Poll();
            _driftDisplay.Update(readings);

            // Highlight status if any axis is drifting
            bool anyDrift = readings.Any(r => r.Axes.Any(a => a.IsDrifting));
            _driftStatus.ForeColor = anyDrift ? Color.Firebrick : SystemColors.GrayText;
            _driftStatus.Text = anyDrift
                ? "Drift detected! Check the RED axes above to find the culprit."
                : "All axes within threshold.";
        }
        catch { /* poll errors are transient; silently skip frame */ }
    }

    // ── Profile helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sorts <paramref name="devices"/> so they appear in the order described by
    /// <paramref name="patterns"/>. Devices not in the pattern list are appended.
    /// </summary>
    private static List<SimDevice> ApplyOrder(
        List<SimDevice> devices, List<string> patterns)
    {
        var result    = new List<SimDevice>(devices.Count);
        var remaining = devices.ToList();

        foreach (var pat in patterns)
        {
            var match = remaining.FirstOrDefault(d =>
                d.VidPattern.Equals(pat, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            result.Add(match);
            remaining.Remove(match);
        }

        result.AddRange(remaining);
        return result;
    }
}
