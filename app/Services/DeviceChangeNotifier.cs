using System.Runtime.InteropServices;
using ControllerManager.Native;

namespace ControllerManager.Services;

/// <summary>
/// Single-shot kernel notification for HID interface arrival/removal, replacing
/// the old DispatcherTimer/Task.Delay polling loops in DevicesViewModel and
/// LaunchOrchestrator. Subscribers re-enumerate only when devices actually
/// change, so a steady-state Moonlight session no longer hammers the HID stack
/// with CreateFileW every 1–2s (which was causing PnP churn on ViGEm-backed
/// virtual gamepads and noticeable input lag).
///
/// Filters on the HID device interface class GUID so we hear every gaming-HID
/// arrival/departure (including composite child interfaces and virtual gamepads).
///
/// Burst coalescing: composite devices (wheel + buttonbox + audio behind one
/// USB ID) fire one notification per child interface back-to-back. A short
/// debounce window collapses those into a single DevicesChanged so consumers
/// don't enumerate N times per plug-in.
/// </summary>
public sealed class DeviceChangeNotifier : IDisposable
{
    // 200ms is comfortably longer than the inter-arrival gap for the children of
    // one composite device on commodity hardware (observed: tens of ms) but
    // still fast enough that the UI updates feel immediate after a plug.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(200);

    private readonly object _lock = new();
    private IntPtr _handle;
    // Held in a field so the GC doesn't collect it while CfgMgr32 still owns
    // a function pointer to it. Function-pointer-marshalled delegates must
    // outlive every native callback.
    private NotifyCallback? _callbackKeepAlive;
    private System.Threading.Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Raised after device arrival/removal activity has been quiet for
    /// <see cref="DebounceWindow"/>. Fires on a ThreadPool thread — subscribers
    /// must marshal to their own UI/sync context.
    /// </summary>
    public event EventHandler? DevicesChanged;

    /// <summary>True once Start succeeded; false if registration failed or
    /// Start hasn't been called. Consumers can use this to fall back to
    /// manual-refresh-only semantics.</summary>
    public bool IsActive => _handle != IntPtr.Zero;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeviceChangeNotifier));
            if (_handle != IntPtr.Zero) return;

            var filter = new CM_NOTIFY_FILTER
            {
                cbSize     = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
                Flags      = 0,
                FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                Reserved   = 0,
                ClassGuid  = HidApi.HidClassGuid,
            };

            _callbackKeepAlive = OnCmNotify;
            _debounceTimer     = new System.Threading.Timer(OnDebounceElapsed, null,
                                     Timeout.Infinite, Timeout.Infinite);

            int cr = CM_Register_Notification(ref filter, IntPtr.Zero,
                                              _callbackKeepAlive, out _handle);
            if (cr != CR_SUCCESS)
            {
                _handle = IntPtr.Zero;
                _debounceTimer.Dispose();
                _debounceTimer    = null;
                _callbackKeepAlive = null;
                throw new InvalidOperationException(
                    $"CM_Register_Notification failed: CR=0x{cr:X8}");
            }
        }
    }

    // Called by CfgMgr32 on an internal thread for every HID interface
    // arrival/removal. Must return CR_SUCCESS so the OS keeps delivering.
    // We don't care which device changed — consumers re-enumerate to find out.
    private int OnCmNotify(IntPtr notify, IntPtr context,
                           CM_NOTIFY_ACTION action, IntPtr eventData, uint eventDataSize)
    {
        if (action == CM_NOTIFY_ACTION.DEVICEINTERFACEARRIVAL ||
            action == CM_NOTIFY_ACTION.DEVICEINTERFACEREMOVAL)
        {
            // Change() is the debounce: each notification pushes the fire-time
            // out another DebounceWindow. The timer only elapses once the
            // burst is genuinely over.
            _debounceTimer?.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
        return CR_SUCCESS;
    }

    private void OnDebounceElapsed(object? _)
    {
        try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Logger.WriteException("DeviceChangeNotifier", ex); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                CM_Unregister_Notification(_handle);
                _handle = IntPtr.Zero;
            }
            _debounceTimer?.Dispose();
            _debounceTimer    = null;
            // Drop keepalive only after Unregister — CfgMgr32 guarantees no
            // more callbacks once Unregister returns.
            _callbackKeepAlive = null;
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────────

    private const int CR_SUCCESS = 0;
    private const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;

    // Layout mirrors cfgmgr32.h CM_NOTIFY_FILTER exactly. The union after the
    // four header DWORDs is dominated by InstanceId[MAX_DEVICE_ID_LEN]
    // (200 wchars = 400 bytes); fixing Size=416 zero-pads the unused tail so
    // CfgMgr32 doesn't read uninitialized stack as part of the filter.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)]  public uint cbSize;
        [FieldOffset(4)]  public uint Flags;
        [FieldOffset(8)]  public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        // Union: DeviceInterface.ClassGuid lives at offset 16 (after the four
        // header DWORDs). The other union members (DeviceHandle.hTarget,
        // DeviceInstance.InstanceId) are unused for our filter type.
        [FieldOffset(16)] public Guid ClassGuid;
    }

    private enum CM_NOTIFY_ACTION
    {
        DEVICEINTERFACEARRIVAL = 0,
        DEVICEINTERFACEREMOVAL = 1,
        // Other actions (DEVICEINSTANCE*, DEVICEQUERYREMOVE, etc.) are not
        // delivered for DEVICEINTERFACE-typed filters.
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NotifyCallback(IntPtr notify, IntPtr context,
        CM_NOTIFY_ACTION action, IntPtr eventData, uint eventDataSize);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Register_Notification(
        ref CM_NOTIFY_FILTER pFilter, IntPtr pContext,
        NotifyCallback pCallback, out IntPtr pNotifyContext);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Unregister_Notification(IntPtr NotifyContext);
}
