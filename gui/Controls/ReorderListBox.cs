using System.Windows.Forms.VisualStyles;

namespace HidReorder.Controls;

/// <summary>
/// Owner-drawn ListBox with hamburger drag handles, per-item checkboxes, and
/// a right-click "Ignore" option.
///
/// Three device states:
///   Checked   — included in reorder and re-enabled in slot order
///   Unchecked — disabled by the reorder script and left off
///   Ignored   — completely untouched (right-click to set)
/// </summary>
public sealed class ReorderListBox : ListBox
{
    private readonly HashSet<string> _unchecked = [];
    private readonly HashSet<string> _ignored   = [];

    private int   _dragSourceIdx  = -1;
    private int   _dropTargetIdx  = -1;
    private int   _rightClickIdx  = -1;
    private Point _mouseDownPt;
    private bool  _dragging;

    private readonly ToolTip _tip = new() { AutomaticDelay = 500, AutoPopDelay = 8000 };
    private int _lastTipIdx  = -1;
    private int _lastTipZone = -1;

    private const int HandleW    = 28;
    private const int CheckW     = 22;
    private const int TextStart  = HandleW + CheckW;
    private const int ToggleBtnW = 42;
    private const int BadgeW     = 72;
    private const int DragThresh =  4;

    public event EventHandler?            OrderChanged;
    public event EventHandler?            IgnoredChanged;
    public event EventHandler<SimDevice>? EnableToggleRequested;

    public ReorderListBox()
    {
        DrawMode      = DrawMode.OwnerDrawFixed;
        ItemHeight    = 30;
        AllowDrop     = true;
        SelectionMode = SelectionMode.One;
        BorderStyle   = BorderStyle.FixedSingle;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (_rightClickIdx < 0 || _rightClickIdx >= Items.Count) return;

            var dev = (SimDevice)Items[_rightClickIdx]!;
            if (IsIgnored(dev))
            {
                menu.Items.Add("Stop ignoring — include in reorder cycle", null,
                    (_, _) => SetIgnored(dev, false));
            }
            else
            {
                menu.Items.Add("Ignore — skip entirely (not disabled or re-enabled)", null,
                    (_, _) => SetIgnored(dev, true));
                menu.Items.Add(new ToolStripMenuItem(
                    "  ⚠  Ignored devices may stay at their current Windows slot") { Enabled = false });
            }
        };
        ContextMenuStrip = menu;
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    public IEnumerable<SimDevice> OrderedDevices  => Items.Cast<SimDevice>();
    public IEnumerable<SimDevice> CheckedDevices  => Items.Cast<SimDevice>().Where(d => !IsIgnored(d) &&  IsChecked(d));
    public IEnumerable<SimDevice> DisabledDevices => Items.Cast<SimDevice>().Where(d => !IsIgnored(d) && !IsChecked(d));
    public IEnumerable<SimDevice> IgnoredDevices  => Items.Cast<SimDevice>().Where(IsIgnored);

    public bool IsChecked(SimDevice d) => !_unchecked.Contains(d.VidPidLabel);
    public bool IsIgnored(SimDevice d) => _ignored.Contains(d.VidPidLabel);

    public IReadOnlyCollection<string> GetIgnoredKeys() => _ignored;

    public void LoadIgnored(IEnumerable<string> keys)
    {
        _ignored.Clear();
        foreach (var k in keys) _ignored.Add(k);
        Invalidate();
    }

    public void SetDevices(IEnumerable<SimDevice> devices)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var d in devices) Items.Add(d);
        EndUpdate();
        SortIgnoredToBottom();
        Invalidate();
    }

    private void SortIgnoredToBottom()
    {
        var active  = Items.Cast<SimDevice>().Where(d => !IsIgnored(d)).ToList();
        var ignored = Items.Cast<SimDevice>().Where(d =>  IsIgnored(d)).ToList();
        if (ignored.Count == 0) return;

        BeginUpdate();
        Items.Clear();
        foreach (var d in active)  Items.Add(d);
        foreach (var d in ignored) Items.Add(d);
        EndUpdate();
    }

    // ── Owner draw ───────────────────────────────────────────────────────────────

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        e.DrawBackground();
        var  dev      = (SimDevice)Items[e.Index]!;
        bool ignored  = IsIgnored(dev);
        bool checked_ = !ignored && IsChecked(dev);

        // SLOT #1 badge tracks the first non-ignored checked device
        int firstCheckedIdx = Enumerable.Range(0, Items.Count)
            .FirstOrDefault(i =>
            {
                var d = (SimDevice)Items[i]!;
                return !IsIgnored(d) && IsChecked(d);
            }, -1);
        bool isFirst = !ignored && checked_ && e.Index == firstCheckedIdx;

        var bounds    = e.Bounds;
        var textColor = (ignored || !checked_) ? SystemColors.GrayText : e.ForeColor;

        // ── Hamburger handle ──────────────────────────────────────────────────
        DrawHandle(e.Graphics, bounds, textColor);

        // ── Checkbox / ignored indicator ──────────────────────────────────────
        if (ignored)
        {
            // Em dash where the checkbox would be
            TextRenderer.DrawText(e.Graphics, "—",
                (e.Font ?? Font),
                new Rectangle(bounds.X + HandleW, bounds.Y, CheckW, bounds.Height),
                SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        else
        {
            var state = checked_ ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal;
            int cy    = bounds.Y + (bounds.Height - 13) / 2;
            CheckBoxRenderer.DrawCheckBox(
                e.Graphics, new Point(bounds.X + HandleW + 4, cy), state);
        }

        // ── Device name ───────────────────────────────────────────────────────
        int textRight = ToggleBtnW + ((isFirst || ignored) ? BadgeW + 4 : 4);
        TextRenderer.DrawText(
            e.Graphics, dev.DisplayName, e.Font ?? Font,
            new Rectangle(bounds.X + TextStart, bounds.Y,
                          bounds.Width - TextStart - textRight, bounds.Height),
            textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Strikethrough for ignored devices
        if (ignored)
        {
            int midY = bounds.Y + bounds.Height / 2;
            using var pen = new Pen(SystemColors.GrayText);
            e.Graphics.DrawLine(pen,
                bounds.X + TextStart, midY,
                bounds.Right - BadgeW - ToggleBtnW - 4, midY);
        }

        // ── Enable / disable toggle ───────────────────────────────────────────
        var toggleRect = new Rectangle(
            bounds.Right - BadgeW - ToggleBtnW + 2, bounds.Y + 5,
            ToggleBtnW - 6, bounds.Height - 10);
        var (toggleLabel, toggleFg, toggleBg) = dev.IsEnabled
            ? ("ON",  Color.White,             Color.SeaGreen)
            : ("OFF", SystemColors.ControlText, SystemColors.Control);
        using (var fill = new SolidBrush(toggleBg))
            e.Graphics.FillRectangle(fill, toggleRect);
        using (var border = new Pen(dev.IsEnabled ? Color.SeaGreen : SystemColors.ControlDark))
            e.Graphics.DrawRectangle(border, toggleRect);
        using var toggleFont = new Font((e.Font ?? Font).FontFamily, 7f, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, toggleLabel, toggleFont, toggleRect,
            toggleFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // ── Right-side badge ──────────────────────────────────────────────────
        if (isFirst || ignored)
        {
            using var badgeFont = new Font((e.Font ?? Font).FontFamily, 7.5f, FontStyle.Bold);
            var (label, color) = isFirst
                ? ("SLOT #1", Color.Goldenrod)
                : ("IGNORED", Color.Gray);
            TextRenderer.DrawText(
                e.Graphics, label, badgeFont,
                new Rectangle(bounds.Right - BadgeW, bounds.Y, BadgeW - 4, bounds.Height),
                color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        // ── Drop indicator ────────────────────────────────────────────────────
        if (_dragging && e.Index == _dropTargetIdx)
        {
            using var pen = new Pen(Color.DodgerBlue, 2);
            e.Graphics.DrawLine(pen,
                bounds.Left + 6,  bounds.Top + 1,
                bounds.Right - 6, bounds.Top + 1);
        }
    }

    private static void DrawHandle(Graphics g, Rectangle bounds, Color baseColor)
    {
        int cx  = bounds.X + HandleW / 2;
        int mid = bounds.Y + bounds.Height / 2;
        using var pen = new Pen(Color.FromArgb(120, baseColor), 1.5f);
        pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        const int hw = 8;
        g.DrawLine(pen, cx - hw, mid - 4, cx + hw, mid - 4);
        g.DrawLine(pen, cx - hw, mid,     cx + hw, mid);
        g.DrawLine(pen, cx - hw, mid + 4, cx + hw, mid + 4);
    }

    // ── State changes ────────────────────────────────────────────────────────────

    private void ToggleChecked(int idx)
    {
        var dev = (SimDevice)Items[idx]!;
        if (_unchecked.Contains(dev.VidPidLabel))
            _unchecked.Remove(dev.VidPidLabel);
        else
            _unchecked.Add(dev.VidPidLabel);
        Invalidate();
    }

    private void SetIgnored(SimDevice dev, bool ignore)
    {
        if (ignore) _ignored.Add(dev.VidPidLabel);
        else        _ignored.Remove(dev.VidPidLabel);
        SortIgnoredToBottom();
        Invalidate();
        IgnoredChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Mouse handling ────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            _rightClickIdx = IndexFromPoint(e.Location);
            return; // ContextMenuStrip shows via ContextMenuStrip property automatically
        }

        if (e.Button != MouseButtons.Left) return;

        _mouseDownPt = e.Location;

        int toggleStart = ClientSize.Width - BadgeW - ToggleBtnW;
        int toggleEnd   = ClientSize.Width - BadgeW;

        if (e.X >= toggleStart && e.X < toggleEnd)
        {
            int idx = IndexFromPoint(e.Location);
            if (idx >= 0)
                EnableToggleRequested?.Invoke(this, (SimDevice)Items[idx]!);
        }
        else if (e.X < HandleW)
        {
            int idx = IndexFromPoint(e.Location);
            if (idx >= 0 && !IsIgnored((SimDevice)Items[idx]!))
                _dragSourceIdx = idx;
        }
        else if (e.X < TextStart)
        {
            // Checkbox zone — ignored devices don't toggle
            int idx = IndexFromPoint(e.Location);
            if (idx >= 0 && !IsIgnored((SimDevice)Items[idx]!))
                ToggleChecked(idx);
            _dragSourceIdx = -1;
        }
        else
        {
            _dragSourceIdx = -1;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (e.Button == MouseButtons.Left && _dragSourceIdx >= 0)
        {
            if (Math.Abs(e.X - _mouseDownPt.X) > DragThresh ||
                Math.Abs(e.Y - _mouseDownPt.Y) > DragThresh)
            {
                _tip.Hide(this);
                _lastTipIdx = _lastTipZone = -1;
                _dragging = true;
                DoDragDrop(Items[_dragSourceIdx]!, DragDropEffects.Move);
                _dragging      = false;
                _dropTargetIdx = -1;
                _dragSourceIdx = -1;
                Invalidate();
            }
            return;
        }

        int idx  = IndexFromPoint(e.Location);
        int zone = e.X < HandleW ? 0 : e.X < TextStart ? 1 : 2;
        if (idx == _lastTipIdx && zone == _lastTipZone) return;
        _lastTipIdx  = idx;
        _lastTipZone = zone;

        if (idx < 0 || idx >= Items.Count) { _tip.Hide(this); return; }

        var dev = (SimDevice)Items[idx]!;
        string? tip = (zone, IsIgnored(dev), IsChecked(dev)) switch
        {
            (1, false, true)  => "Checked — included in reorder.\nDisabled then re-enabled in slot order.",
            (1, false, false) => "Unchecked — disabled only.\nDevice is disabled during reorder and left off — it won't show up\nin games until re-enabled. Useful for hiding sim gear when\nswitching to controller games. Note: replugging the device\nor rebooting may re-enable it.",
            (2, true,  _)     => "Ignored — not touched during reorder.\n⚠ This device will not be cycled, so it may stay at\nits current Windows slot position.",
            _                 => null,
        };

        if (tip is not null) _tip.Show(tip, this, e.X + 14, e.Y + 14);
        else _tip.Hide(this);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide(this);
        _lastTipIdx = _lastTipZone = -1;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragSourceIdx = -1;
    }

    // ── Drag-drop ────────────────────────────────────────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = e.Data?.GetData(typeof(SimDevice)) is SimDevice
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs drge)
    {
        drge.Effect = DragDropEffects.Move;
        int idx = IndexFromPoint(PointToClient(new Point(drge.X, drge.Y)));
        if (idx < 0) idx = Items.Count - 1;
        if (idx != _dropTargetIdx) { _dropTargetIdx = idx; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        _dropTargetIdx = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs drge)
    {
        if (drge.Data?.GetData(typeof(SimDevice)) is not SimDevice device) return;

        int dropIdx = IndexFromPoint(PointToClient(new Point(drge.X, drge.Y)));
        if (dropIdx < 0) dropIdx = Items.Count - 1;

        int srcIdx = Items.IndexOf(device);
        if (srcIdx < 0 || srcIdx == dropIdx) return;

        BeginUpdate();
        Items.RemoveAt(srcIdx);
        if (dropIdx > srcIdx) dropIdx--;
        Items.Insert(dropIdx, device);
        SelectedIndex = dropIdx;
        EndUpdate();

        _dropTargetIdx = -1;
        SortIgnoredToBottom();
        Invalidate();
        OrderChanged?.Invoke(this, EventArgs.Empty);
    }
}
