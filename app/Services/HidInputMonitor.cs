using ControllerManager.Native;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Services;

/// <summary>
/// Opens a HID device interface, reads its capability descriptors, and (optionally)
/// polls input reports on a background thread, decoding them into per-axis float
/// values (0..1 normalised) and per-button bool arrays.
///
/// Two handles are used: one with 0 desired access for capability queries (works
/// for "exclusively opened" devices like gamepads owned by another app), and a
/// separate GENERIC_READ handle for blocking ReadFile polling. The read loop ends
/// when Stop() closes the read handle, which unblocks any pending ReadFile.
/// </summary>
public sealed class HidInputMonitor : IDisposable
{
    public record AxisInfo(
        ushort UsagePage, ushort Usage, string Name,
        int LogicalMin, int LogicalMax,
        // Parent collection identifiers, used to tell real X/Y stick pairs apart
        // from independent axes that happen to use the same usage codes (pedals
        // that report Rx/Ry as separate non-pointer axes, for example).
        ushort LinkCollection, ushort LinkUsage, ushort LinkUsagePage);
    public record ButtonRange(ushort UsagePage, ushort UsageMin, ushort UsageMax);

    public IReadOnlyList<AxisInfo>    Axes             { get; private set; } = [];
    public IReadOnlyList<ButtonRange> ButtonRanges     { get; private set; } = [];
    public int                        TotalButtonCount { get; private set; }

    public event Action<float[]>? AxesUpdated;
    public event Action<bool[]>?  ButtonsUpdated;

    private string?                  _devicePath;
    private SafeFileHandle?          _capsHandle;
    private SafeFileHandle?          _readHandle;
    private IntPtr                   _preparsedData = IntPtr.Zero;
    private ushort                   _inputReportLen;
    private CancellationTokenSource? _cts;
    private Task?                    _pollTask;

    /// <summary>
    /// Opens the device for capability querying and populates Axes/ButtonRanges.
    /// Returns true if the device opened AND has at least one axis or button.
    /// </summary>
    public bool Open(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return false;

        _devicePath = devicePath;

        // Try 0 desired access first — works even when device is owned by another
        // app (HID gamepads bound exclusively by a game, for example)
        _capsHandle = HidApi.CreateFileW(
            devicePath, 0,
            HidApi.FILE_SHARE_READ | HidApi.FILE_SHARE_WRITE,
            IntPtr.Zero, HidApi.OPEN_EXISTING, 0, IntPtr.Zero);

        if (_capsHandle.IsInvalid)
        {
            _capsHandle.Dispose();
            _capsHandle = HidApi.CreateFileW(
                devicePath, HidApi.GENERIC_READ,
                HidApi.FILE_SHARE_READ | HidApi.FILE_SHARE_WRITE,
                IntPtr.Zero, HidApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (_capsHandle.IsInvalid)
            {
                _capsHandle.Dispose();
                _capsHandle = null;
                _devicePath = null;
                return false;
            }
        }

        if (!HidApi.HidD_GetPreparsedData(_capsHandle, out _preparsedData) || _preparsedData == IntPtr.Zero)
        {
            CloseCaps();
            return false;
        }

        if (HidApi.HidP_GetCaps(_preparsedData, out var caps) != HidApi.HIDP_STATUS_SUCCESS)
        {
            CloseCaps();
            return false;
        }

        _inputReportLen = caps.InputReportByteLength;

        var axes    = new List<AxisInfo>();
        var btnRngs = new List<ButtonRange>();

        // ── Value (axis) caps — Generic Desktop (0x01) usages 0x30-0x3F only ────
        // Vendor-defined pages, Consumer Controls, etc. are excluded so devices like
        // Stream Decks and fan controllers don't show phantom axes.
        if (caps.NumberInputValueCaps > 0)
        {
            var valueCaps   = new HidApi.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            ushort vcLength = caps.NumberInputValueCaps;
            if (HidApi.HidP_GetValueCaps(HidApi.HidP_Input_ReportType, valueCaps, ref vcLength, _preparsedData)
                    == HidApi.HIDP_STATUS_SUCCESS)
            {
                int idx = 0;
                for (int i = 0; i < vcLength; i++)
                {
                    var vc = valueCaps[i];
                    if (vc.UsagePage != 0x01) continue;
                    ushort uMin = vc.UsageMin;
                    ushort uMax = vc.IsRange != 0 ? vc.UsageMax : uMin;
                    for (ushort u = uMin; u <= uMax; u++)
                    {
                        if (u < 0x30 || u > 0x3F) continue;
                        axes.Add(new AxisInfo(vc.UsagePage, u,
                            HidApi.GetAxisName(vc.UsagePage, u, ++idx),
                            vc.LogicalMin, vc.LogicalMax,
                            vc.LinkCollection, vc.LinkUsage, vc.LinkUsagePage));
                    }
                }
            }
        }

        // ── Button caps — Button page (0x09) only ─────────────────────────────
        int totalButtons = 0;
        if (caps.NumberInputButtonCaps > 0)
        {
            var btnCaps     = new HidApi.HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
            ushort bcLength = caps.NumberInputButtonCaps;
            if (HidApi.HidP_GetButtonCaps(HidApi.HidP_Input_ReportType, btnCaps, ref bcLength, _preparsedData)
                    == HidApi.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < bcLength; i++)
                {
                    var bc = btnCaps[i];
                    if (bc.UsagePage != 0x09) continue;
                    ushort uMin = bc.UsageMin;
                    ushort uMax = bc.IsRange != 0 ? bc.UsageMax : uMin;
                    btnRngs.Add(new ButtonRange(bc.UsagePage, uMin, uMax));
                    totalButtons += (uMax - uMin + 1);
                }
            }
        }

        Axes             = axes;
        ButtonRanges     = btnRngs;
        TotalButtonCount = totalButtons;

        return axes.Count > 0 || totalButtons > 0;
    }

    /// <summary>
    /// Starts a background polling loop. AxesUpdated/ButtonsUpdated events are
    /// invoked via the provided dispatcher so handlers can safely touch UI.
    /// </summary>
    public void StartPolling(Action<Action> dispatchToUi)
    {
        if (_capsHandle is null || _preparsedData == IntPtr.Zero || _inputReportLen == 0) return;
        if (_devicePath is null) return;
        if (_pollTask is not null && !_pollTask.IsCompleted) return;

        _readHandle = HidApi.CreateFileW(
            _devicePath, HidApi.GENERIC_READ,
            HidApi.FILE_SHARE_READ | HidApi.FILE_SHARE_WRITE,
            IntPtr.Zero, HidApi.OPEN_EXISTING, 0, IntPtr.Zero);

        if (_readHandle.IsInvalid)
        {
            _readHandle.Dispose();
            _readHandle = null;
            return;
        }

        _cts      = new CancellationTokenSource();
        var token = _cts.Token;
        var preparsed  = _preparsedData;
        var reportLen  = _inputReportLen;
        var axes       = Axes.ToArray();
        var btnRngs    = ButtonRanges.ToArray();
        var totalBtns  = TotalButtonCount;
        var readHandle = _readHandle;

        _pollTask = Task.Run(() =>
        {
            var buf       = new byte[reportLen];
            var usageBuf  = totalBtns > 0 ? new ushort[totalBtns] : [];
            var pressed   = new bool[totalBtns];
            var axisVals  = new float[axes.Length];

            while (!token.IsCancellationRequested)
            {
                if (!HidApi.ReadFile(readHandle, buf, reportLen, out uint bytesRead, IntPtr.Zero)
                    || bytesRead == 0)
                {
                    break; // Handle closed / device gone
                }

                // ── Axes ───────────────────────────────────────────────────
                if (axes.Length > 0)
                {
                    for (int i = 0; i < axes.Length; i++)
                    {
                        var ax = axes[i];
                        if (HidApi.HidP_GetUsageValue(
                                HidApi.HidP_Input_ReportType, ax.UsagePage, 0, ax.Usage,
                                out uint raw, preparsed, buf, bytesRead)
                            == HidApi.HIDP_STATUS_SUCCESS)
                        {
                            // HidP_GetUsageValue does NOT sign-extend values smaller
                            // than 32 bits — a 16-bit signed axis at -1 comes back as
                            // raw=0x0000FFFF, not 0xFFFFFFFF. Re-extend by detecting
                            // when raw exceeds LogicalMax: that means the high bit of
                            // the source value was set and the actual signed value is
                            // (raw - 2^bitWidth). Total bit width = (LogicalMax - LogicalMin + 1).
                            // Without this, axes with negative LogicalMin (sticks, signed
                            // trigger encodings) clamp to 1.0 instead of swinging around
                            // their zero point.
                            long bitWidthCount = (long)ax.LogicalMax - ax.LogicalMin + 1;
                            int signed = (ax.LogicalMin < 0 && raw > (uint)ax.LogicalMax)
                                ? (int)((long)raw - bitWidthCount)
                                : (int)raw;

                            int range = ax.LogicalMax - ax.LogicalMin;
                            float norm = range <= 0 ? 0f
                                       : (float)(signed - ax.LogicalMin) / range;
                            if      (norm < 0f) norm = 0f;
                            else if (norm > 1f) norm = 1f;
                            axisVals[i] = norm;
                        }
                    }

                    var snapshot = (float[])axisVals.Clone();
                    dispatchToUi(() => AxesUpdated?.Invoke(snapshot));
                }

                // ── Buttons ────────────────────────────────────────────────
                if (totalBtns > 0)
                {
                    Array.Clear(pressed, 0, pressed.Length);

                    int outIdx = 0;
                    foreach (var range in btnRngs)
                    {
                        uint usageLen = (uint)usageBuf.Length;
                        int rc = HidApi.HidP_GetUsages(
                            HidApi.HidP_Input_ReportType, range.UsagePage, 0,
                            usageBuf, ref usageLen, preparsed, buf, bytesRead);

                        if (rc == HidApi.HIDP_STATUS_SUCCESS)
                        {
                            for (int j = 0; j < usageLen; j++)
                            {
                                ushort u = usageBuf[j];
                                if (u >= range.UsageMin && u <= range.UsageMax)
                                {
                                    int slot = outIdx + (u - range.UsageMin);
                                    if (slot >= 0 && slot < pressed.Length)
                                        pressed[slot] = true;
                                }
                            }
                        }

                        outIdx += (range.UsageMax - range.UsageMin + 1);
                    }

                    var snapshot = (bool[])pressed.Clone();
                    dispatchToUi(() => ButtonsUpdated?.Invoke(snapshot));
                }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        // Closing the read handle unblocks any pending ReadFile call
        _readHandle?.Dispose();
        _readHandle = null;

        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _pollTask = null;
        _cts?.Dispose();
        _cts = null;

        CloseCaps();
        _devicePath = null;
    }

    private void CloseCaps()
    {
        if (_preparsedData != IntPtr.Zero)
        {
            HidApi.HidD_FreePreparsedData(_preparsedData);
            _preparsedData = IntPtr.Zero;
        }
        _capsHandle?.Dispose();
        _capsHandle = null;
    }

    public void Dispose() => Stop();
}
