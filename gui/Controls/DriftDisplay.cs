namespace HidReorder.Controls;

/// <summary>
/// Double-buffered owner-drawn panel that shows live axis readings for all
/// detected joystick devices. Call Update() to push new data and repaint.
/// </summary>
public sealed class DriftDisplay : Panel
{
    private DeviceReading[] _readings = [];

    private static readonly Font DeviceFont = new("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font AxisFont   = new("Segoe UI", 8.5f);

    private const int PadV         = 10;  // vertical padding between sections
    private const int DeviceHeaderH = 22;
    private const int AxisRowH      = 22;
    private const int LabelW        = 36;  // "X", "Y", etc.
    private const int PctW          = 40;  // "100% !" text width
    private const int BarPadH       =  6;  // top/bottom padding inside bar row

    public DriftDisplay()
    {
        DoubleBuffered   = true;
        BackColor        = SystemColors.ControlLight;
        AutoScroll       = true;
        AutoScrollMargin = new Size(0, PadV);
    }

    public void Update(DeviceReading[] readings)
    {
        _readings = readings;

        // Calculate total content height so the scrollbar knows the extent
        int totalH = PadV;
        foreach (var dev in readings)
            totalH += DeviceHeaderH + dev.Axes.Length * AxisRowH + PadV;
        AutoScrollMinSize = new Size(0, totalH);

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g    = e.Graphics;
        int w    = ClientSize.Width;
        int barW = Math.Max(40, w - LabelW - PctW - 24);
        int y    = PadV + AutoScrollPosition.Y;   // offset for scroll

        foreach (var dev in _readings)
        {
            // ── Device header ──────────────────────────────────────────────────
            TextRenderer.DrawText(g, dev.Label, DeviceFont,
                new Rectangle(8, y, w - 16, DeviceHeaderH),
                SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += DeviceHeaderH;

            // ── Axes ───────────────────────────────────────────────────────────
            foreach (var axis in dev.Axes)
            {
                int barX    = 8 + LabelW;
                int barTop  = y + BarPadH;
                int barHt   = AxisRowH - BarPadH * 2;
                int fillW   = (int)(axis.Percent / 100.0 * barW);

                // Axis label
                TextRenderer.DrawText(g, axis.Name, AxisFont,
                    new Rectangle(8, y, LabelW, AxisRowH),
                    SystemColors.GrayText,
                    TextFormatFlags.VerticalCenter);

                // Bar background
                var bgRect = new Rectangle(barX, barTop, barW, barHt);
                g.FillRectangle(SystemBrushes.ControlDark, bgRect);

                // Bar fill
                if (fillW > 1)
                {
                    using var fill = new SolidBrush(
                        axis.IsDrifting ? Color.Firebrick : Color.SeaGreen);
                    g.FillRectangle(fill, barX, barTop, fillW, barHt);
                }

                // Percentage + flag
                var pctColor = axis.IsDrifting ? Color.Firebrick : SystemColors.ControlText;
                var pctText  = axis.IsDrifting ? $"{axis.Percent}% !" : $"{axis.Percent}%";
                TextRenderer.DrawText(g, pctText, AxisFont,
                    new Rectangle(barX + barW + 4, y, PctW, AxisRowH),
                    pctColor,
                    TextFormatFlags.VerticalCenter);

                y += AxisRowH;
            }

            y += PadV;

            // Divider
            if (dev != _readings[^1])
            {
                using var divPen = new Pen(SystemColors.ControlDark, 1);
                g.DrawLine(divPen, 8, y - PadV / 2, w - 8, y - PadV / 2);
            }
        }

        if (_readings.Length == 0)
        {
            TextRenderer.DrawText(g, "No joystick devices found.",
                DeviceFont, ClientRectangle, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
