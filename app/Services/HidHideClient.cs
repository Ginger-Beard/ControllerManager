using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ControllerManager.Native;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Services;

/// <summary>
/// Client for the HidHide kernel filter driver.
/// All methods are no-ops when <see cref="IsAvailable"/> is false (driver not installed).
///
/// IOCTL codes — CTL_CODE(32769, f, METHOD_BUFFERED=0, FILE_READ_DATA=1):
///   = (32769 &lt;&lt; 16) | (1 &lt;&lt; 14) | (f &lt;&lt; 2)  =  0x80014000 | (f &lt;&lt; 2)
/// Whitelist stores NT device paths (\Device\HarddiskVolumeX\...), not Win32 paths.
/// Session blacklist auto-cleans when our process exits (keyed by caller PID in the driver).
/// </summary>
public sealed class HidHideClient
{
    // ── IOCTL codes (verified against HidHideIoctlContract.h + ioctl_contract_tests.cpp) ──
    // Session-blacklist IOCTLs (0x80016020/24) exist only in the modified driver in our
    // HidHide/ source repo; the stock 1.4.181.0 ships without them. We use the persistent
    // blacklist with snapshot/restore around sessions instead — see BeginGameSession.

    private const uint IOCTL_GET_WHITELIST = 0x80016000;
    private const uint IOCTL_SET_WHITELIST = 0x80016004;
    private const uint IOCTL_GET_BLACKLIST = 0x80016008;
    private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
    private const uint IOCTL_GET_ACTIVE    = 0x80016010;
    private const uint IOCTL_SET_ACTIVE    = 0x80016014;
    private const uint IOCTL_GET_WLINVERSE = 0x80016018;
    private const uint IOCTL_SET_WLINVERSE = 0x8001601C;

    private const string ControlDevicePath = @"\\.\HidHide";

    // ── P/Invoke ──────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, char[] lpExeName, ref uint lpdwSize);

    private const uint PROCESS_NAME_NATIVE               = 0x1;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const uint GENERIC_READ  = 0x80000000;
    private const uint FILE_SHARE_ALL = 0x7; // READ | WRITE | DELETE
    private const uint OPEN_EXISTING  = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // Broadcast a device-change notification so apps like joy.cpl re-enumerate immediately
    // rather than showing stale state. HidHide hides at file-open level and sends no
    // WM_DEVICECHANGE itself — we do it manually after persistent blacklist changes.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private const int  HWND_BROADCAST       = 0xFFFF;
    private const uint WM_DEVICECHANGE      = 0x0219;
    private const uint DBT_DEVNODES_CHANGED = 0x0007;
    private const uint SMTO_ABORTIFHUNG     = 0x0002;

    private static void BroadcastDeviceChange()
    {
        // 100ms cap per top-level window. WM_DEVICECHANGE handlers are usually a
        // re-enumerate call that returns in microseconds; only hung windows hit
        // the timeout, and SMTO_ABORTIFHUNG already bails on those without
        // waiting. 1000ms (the old value) was overkill — with many windows open,
        // a few stuck ones could turn a 5-device reveal into a multi-second
        // operation, pushing later reveals past the game's hot-plug window.
        try { SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_DEVICECHANGE,
                  (UIntPtr)DBT_DEVNODES_CHANGED, IntPtr.Zero,
                  SMTO_ABORTIFHUNG, 100, out _); }
        catch { }
    }

    // ── State ────────────────────────────────────────────────────────────────────

    public bool IsAvailable { get; }

    // In-memory set of device instance IDs currently hidden for this session (for Dashboard UI).
    private readonly HashSet<string> _sessionIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SessionBlacklistIds => _sessionIds;

    private bool          _sessionActive;
    private string?       _sessionGameNtPath;
    // Snapshot of the persistent BL before the session started.
    // Session management modifies the persistent BL (installed driver has no session BL IOCTL).
    // Restoring this snapshot on EndGameSession rolls back all session changes atomically.
    private List<string>? _preSessionPersistentBl;

    public HidHideClient()
    {
        using var h = OpenDevice();
        IsAvailable = !h.IsInvalid;
        Logger.WriteVerbose($"[HidHide] Driver {(IsAvailable ? "found" : "not installed")}");
    }

    // ── Public raw API ────────────────────────────────────────────────────────────

    public List<string> GetWhitelist()  => IsAvailable ? IoCtlGetMultiString(IOCTL_GET_WHITELIST)  : [];
    public List<string> GetBlacklist()  => IsAvailable ? IoCtlGetMultiString(IOCTL_GET_BLACKLIST)  : [];
    public bool         GetActive()     => IsAvailable && IoCtlGetBool(IOCTL_GET_ACTIVE);
    public bool         GetInverse()    => IsAvailable && IoCtlGetBool(IOCTL_GET_WLINVERSE);

    public void SetWhitelist(IEnumerable<string> paths)       { if (IsAvailable) IoCtlSetMultiString(IOCTL_SET_WHITELIST, paths); }
    public void SetBlacklist(IEnumerable<string> instanceIds) { if (IsAvailable) IoCtlSetMultiString(IOCTL_SET_BLACKLIST, instanceIds); }
    public void SetActive(bool v)                              { if (IsAvailable) IoCtlSetBool(IOCTL_SET_ACTIVE, v); }
    public void SetInverse(bool v)                             { if (IsAvailable) IoCtlSetBool(IOCTL_SET_WLINVERSE, v); }


    // ── State machine ─────────────────────────────────────────────────────────────
    //
    // Two modes, clean transition via ApplyState():
    //
    //   No session active:
    //     Active=true (if persistent BL non-empty), Inverse=false,
    //     Whitelist=[ControllerManager.exe]
    //     → Devices in persistent BL hidden from all processes except CM.
    //     → Nothing to hide: Active=false, Whitelist=[], Inverse=false.
    //
    //   Session active:
    //     Active=true, Inverse=true, Whitelist=[game.exe]
    //     → Only the game is denied. SimHub, Pit House, joy.cpl, any process that
    //       starts during the session: all allowed automatically (not in deny list).
    //       No enumeration, no wildcards needed.
    //     → CM is also not in the deny list, so it retains full access — the user
    //       never needs to know about this.
    //
    // ApplyState() is the ONLY function that writes Active, Whitelist, and Inverse.
    // All callers update their data then call ApplyState(); no scattered state writes.

    private void ApplyState()
    {
        if (_sessionActive)
        {
            // Inverse mode: deny list = [game.exe]. Everyone else allowed.
            // CM is not in the deny list so it can always manage devices.
            var deny = new List<string>();
            if (!string.IsNullOrEmpty(_sessionGameNtPath)) deny.Add(_sessionGameNtPath);
            SetWhitelist(deny);
            SetInverse(true);
            SetActive(true);
        }
        else if (GetBlacklist().Count > 0)
        {
            // Normal mode: allow list = [CM only]. Everything else denied for hidden devices.
            var ownNt = OwnNtPath;
            SetWhitelist(string.IsNullOrEmpty(ownNt) ? [] : [ownNt]);
            SetInverse(false);
            SetActive(true);
        }
        else
        {
            // Nothing to hide — completely off.
            SetWhitelist([]);
            SetInverse(false);
            SetActive(false);
        }
    }

    // ── Game session management ───────────────────────────────────────────────────
    //
    // The installed HidHide driver (1.4.181.0) does NOT support session blacklist IOCTLs
    // (IOCTL_ADD_SESSION_BLACKLIST / CLR). We use the persistent blacklist instead:
    //   BeginGameSession  → snapshot pre-session BL, write session BL to persistent BL
    //   UpdateSessionBL   → update persistent BL to reflect revealed devices
    //   EndGameSession    → restore persistent BL from snapshot
    //
    // The snapshot means a CM crash leaves session devices in the persistent BL; the user
    // will see them as globally hidden on next launch and can re-enable them from Devices tab.

    /// <summary>Begins a game session: hides devices from the game and records always-visible ones.</summary>
    /// <param name="hideIds">Devices to hide from the game.</param>
    /// <param name="alwaysVisibleIds">Devices the game must see (KeepEnabled).
    /// Temporarily removed from the persistent BL if present, so profile intent wins
    /// over any global Devices-tab setting.</param>
    public void BeginGameSession(IEnumerable<string> hideIds,
                                 IEnumerable<string> alwaysVisibleIds,
                                 string gameExePath)
    {
        if (!IsAvailable) return;
        var ids = hideIds.ToList();
        if (ids.Count == 0) return;

        // Snapshot the BL before any changes — restored verbatim by EndGameSession.
        _preSessionPersistentBl = GetBlacklist();

        var alwaysVisible = alwaysVisibleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // New persistent BL = (pre-session ∪ session devices) − always-visible
        var blSet = new HashSet<string>(_preSessionPersistentBl, StringComparer.OrdinalIgnoreCase);
        blSet.UnionWith(ids);
        blSet.ExceptWith(alwaysVisible);
        SetBlacklist(blSet);
        BroadcastDeviceChange();

        // Mirror session IDs for Dashboard UI
        _sessionIds.Clear();
        foreach (var id in ids) _sessionIds.Add(id);

        var removedCount = _preSessionPersistentBl.Count(id => alwaysVisible.Contains(id));
        if (removedCount > 0)
            Logger.Write($"[HidHide] Temporarily removed {removedCount} device(s) from persistent BL (profile overrides global)");

        _sessionGameNtPath = Win32ToNtPath(gameExePath);
        _sessionActive     = true;
        ApplyState();

        Logger.Write($"[HidHide] Session started — {ids.Count} device(s) hidden");
    }

    /// <summary>
    /// After the game process starts, query its actual NT image path from the kernel
    /// and re-apply the deny list with the correct path. Only called when the original
    /// Win32→NT conversion failed (e.g. \\wsl.localhost\... UNC paths).
    ///
    /// For normal C:\... paths, Win32ToNtPath already produces a correct \Device\... NT
    /// path, so this short-circuits without ever opening the game process. This keeps
    /// us from touching anti-cheat-protected processes unnecessarily.
    /// </summary>
    public void UpdateSessionGameNtPath(int pid)
    {
        if (!IsAvailable || !_sessionActive) return;

        // Already a proper NT device path — no kernel query needed.
        if (_sessionGameNtPath?.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) == true)
            return;

        var hProcess = NtDll.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return;

        try
        {
            var buf  = new char[1024];
            uint len = (uint)buf.Length;
            if (!QueryFullProcessImageNameW(hProcess, PROCESS_NAME_NATIVE, buf, ref len) || len == 0)
                return;

            var ntPath = new string(buf, 0, (int)len);
            if (ntPath.Equals(_sessionGameNtPath, StringComparison.OrdinalIgnoreCase))
                return; // already correct

            Logger.Write($"[HidHide] Corrected game NT path: {ntPath}");
            _sessionGameNtPath = ntPath;
            ApplyState(); // re-apply deny list with correct path
        }
        finally { NtDll.CloseHandle(hProcess); }
    }

    public void UpdateSessionBlacklist(IEnumerable<string> remainingInstanceIds)
    {
        if (!IsAvailable || !_sessionActive) return;
        var remaining = remainingInstanceIds.ToList();

        // Current persistent BL = (pre-session base) ∪ _sessionIds.
        // Next persistent BL    = (pre-session base) ∪ remaining.
        // Apply as delta: remove (sessionIds − remaining), add (remaining − sessionIds).
        var current = GetBlacklist();
        var blSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        blSet.ExceptWith(_sessionIds);
        blSet.UnionWith(remaining);
        SetBlacklist(blSet);
        BroadcastDeviceChange();

        _sessionIds.Clear();
        foreach (var id in remaining) _sessionIds.Add(id);

        Logger.WriteVerbose($"[HidHide] Session BL updated — {remaining.Count} remaining");
    }

    public void EndGameSession()
    {
        if (!IsAvailable) return;

        // Restore persistent BL verbatim to pre-session state.
        // This undoes both the always-visible removal and the session device additions.
        if (_preSessionPersistentBl != null)
        {
            SetBlacklist(_preSessionPersistentBl);
            BroadcastDeviceChange();
        }

        _sessionActive          = false;
        _sessionGameNtPath      = null;
        _preSessionPersistentBl = null;
        _sessionIds.Clear();
        ApplyState();

        Logger.Write("[HidHide] Session ended");
    }

    // ── Persistent device toggle (Devices tab) ────────────────────────────────────

    public void AddToPersistentBlacklist(string instanceId)
    {
        if (!IsAvailable) return;

        var existing = GetBlacklist();
        if (existing.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            return; // no-op

        existing.Add(instanceId);
        SetBlacklist(existing);

        // Only re-apply when no session is active — during a session ApplyState
        // is already correct (inverse mode, game in deny list).
        if (!_sessionActive) ApplyState();

        BroadcastDeviceChange();
        Logger.WriteVerbose($"[HidHide] Persistent blacklist add: {instanceId}");
    }

    public void RemoveFromPersistentBlacklist(string instanceId)
    {
        if (!IsAvailable) return;

        var updated = GetBlacklist()
            .Where(id => !id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SetBlacklist(updated);

        if (!_sessionActive) ApplyState();

        BroadcastDeviceChange();
        Logger.WriteVerbose($"[HidHide] Persistent blacklist remove: {instanceId}");
    }

    public bool IsInPersistentBlacklist(string instanceId)
    {
        if (!IsAvailable) return false;
        return GetBlacklist().Contains(instanceId, StringComparer.OrdinalIgnoreCase);
    }

    // ── Startup recovery ──────────────────────────────────────────────────────────

    public void RecoverOnStartup()
    {
        if (!IsAvailable) return;

        // Clear any stale in-memory session state. If CM crashed during a session,
        // session devices may be stuck in the persistent BL — the user will see them
        // as disabled on the Devices tab and can re-enable them manually.
        _sessionActive          = false;
        _sessionGameNtPath      = null;
        _preSessionPersistentBl = null;
        _sessionIds.Clear();

        // ApplyState rebuilds whitelist/inverse/active from the persistent BL.
        ApplyState();

        Logger.Write("[HidHide] Startup recovery complete");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static string? _ownNtPath;
    private static string? OwnNtPath
    {
        get
        {
            if (_ownNtPath != null) return _ownNtPath;
            var win32 = Environment.ProcessPath;
            if (string.IsNullOrEmpty(win32)) return null;
            _ownNtPath = Win32ToNtPath(win32);
            return _ownNtPath;
        }
    }

    /// <summary>
    /// Converts a Win32 path (C:\...) to an NT device path (\Device\HarddiskVolumeX\...).
    /// HidHide's whitelist requires NT device paths, not Win32 paths.
    /// </summary>
    private static string Win32ToNtPath(string win32Path)
    {
        if (win32Path.Length < 3 || win32Path[1] != ':') return win32Path;

        var drive = win32Path[..2]; // e.g. "C:"
        var buf   = new char[512];
        uint len  = QueryDosDevice(drive, buf, (uint)buf.Length);
        if (len == 0) return win32Path;

        // QueryDosDevice returns null-separated strings; take the first (shortest) entry.
        var ntDevice = new string(buf, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries)[0];
        return ntDevice + win32Path[2..]; // e.g. \Device\HarddiskVolume3 + \rest\of\path
    }

    private SafeFileHandle OpenDevice() =>
        CreateFile(ControlDevicePath, GENERIC_READ, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

    // ── Multi-string encode/decode ─────────────────────────────────────────────────

    private static readonly byte[] NullWChar = [0, 0];

    private static byte[] EncodeMultiString(IEnumerable<string> strings)
    {
        using var ms = new System.IO.MemoryStream();
        foreach (var s in strings)
        {
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.Write(NullWChar); // null terminator
        }
        ms.Write(NullWChar); // double-null terminator
        return ms.ToArray();
    }

    private static List<string> DecodeMultiString(byte[] data, int byteCount)
    {
        if (byteCount < 4) return [];
        var str = Encoding.Unicode.GetString(data, 0, byteCount & ~1); // ensure even byte count
        return [.. str.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    // ── IOCTL primitives ──────────────────────────────────────────────────────────

    private List<string> IoCtlGetMultiString(uint ioctl)
    {
        using var h = OpenDevice();
        if (h.IsInvalid) return [];

        // First call: get required buffer size (bytes).
        DeviceIoControl(h, ioctl, null, 0, null, 0, out uint needed, IntPtr.Zero);
        if (needed == 0) return [];

        var buf = new byte[needed];
        if (!DeviceIoControl(h, ioctl, null, 0, buf, (uint)buf.Length, out uint returned, IntPtr.Zero))
            return [];

        return DecodeMultiString(buf, (int)returned);
    }

    private void IoCtlSetMultiString(uint ioctl, IEnumerable<string> values)
    {
        using var h = OpenDevice();
        if (h.IsInvalid) return;
        var buf = EncodeMultiString(values);
        DeviceIoControl(h, ioctl, buf, (uint)buf.Length, null, 0, out _, IntPtr.Zero);
    }

    private bool IoCtlGetBool(uint ioctl)
    {
        using var h = OpenDevice();
        if (h.IsInvalid) return false;
        var buf = new byte[1];
        if (!DeviceIoControl(h, ioctl, null, 0, buf, 1, out uint returned, IntPtr.Zero))
            return false;
        return returned >= 1 && buf[0] != 0;
    }

    private void IoCtlSetBool(uint ioctl, bool value)
    {
        using var h = OpenDevice();
        if (h.IsInvalid) return;
        var buf = new byte[] { (byte)(value ? 1 : 0) };
        DeviceIoControl(h, ioctl, buf, 1, null, 0, out _, IntPtr.Zero);
    }
}
