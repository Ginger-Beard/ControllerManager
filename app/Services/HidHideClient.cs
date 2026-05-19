using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    private const uint IOCTL_GET_WHITELIST          = 0x80016000;
    private const uint IOCTL_SET_WHITELIST          = 0x80016004;
    private const uint IOCTL_GET_BLACKLIST          = 0x80016008;
    private const uint IOCTL_SET_BLACKLIST          = 0x8001600C;
    private const uint IOCTL_GET_ACTIVE             = 0x80016010;
    private const uint IOCTL_SET_ACTIVE             = 0x80016014;
    private const uint IOCTL_GET_WLINVERSE          = 0x80016018;
    private const uint IOCTL_SET_WLINVERSE          = 0x8001601C;
    private const uint IOCTL_ADD_SESSION_BLACKLIST  = 0x80016020;
    private const uint IOCTL_CLR_SESSION_BLACKLIST  = 0x80016024;

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

    private const int  HWND_BROADCAST       = unchecked((int)0xFFFF);
    private const uint WM_DEVICECHANGE      = 0x0219;
    private const uint DBT_DEVNODES_CHANGED = 0x0007;
    private const uint SMTO_ABORTIFHUNG     = 0x0002;

    private static void BroadcastDeviceChange()
    {
        try { SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_DEVICECHANGE,
                  (UIntPtr)DBT_DEVNODES_CHANGED, IntPtr.Zero,
                  SMTO_ABORTIFHUNG, 1000, out _); }
        catch { }
    }

    // ── State ────────────────────────────────────────────────────────────────────

    public bool IsAvailable { get; }

    // In-memory mirror of the session blacklist (no IOCTL_GET_SESSION_BLACKLIST exists).
    private readonly HashSet<string> _sessionIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SessionBlacklistIds => _sessionIds;

    private bool    _sessionActive;
    private string? _sessionGameNtPath;

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

    public void AddSessionBlacklist(IEnumerable<string> instanceIds)
    {
        if (!IsAvailable) return;
        var ids = instanceIds.ToList();
        var buf = EncodeMultiString(ids);
        if (buf.Length <= 2) return;
        using var h = OpenDevice();
        if (h.IsInvalid) return;
        DeviceIoControl(h, IOCTL_ADD_SESSION_BLACKLIST, buf, (uint)buf.Length, null, 0, out _, IntPtr.Zero);
        foreach (var id in ids) _sessionIds.Add(id);
    }

    public void ClearSessionBlacklist()
    {
        if (!IsAvailable) return;
        using var h = OpenDevice();
        if (h.IsInvalid) return;
        DeviceIoControl(h, IOCTL_CLR_SESSION_BLACKLIST, null, 0, null, 0, out _, IntPtr.Zero);
        _sessionIds.Clear();
    }

    // ── Game session management ───────────────────────────────────────────────────

    /// <summary>
    /// Sets up HidHide for a game session using inverse whitelist mode:
    /// the game exe is the only process denied access — SimHub, Pit House, joy.cpl,
    /// and any process that starts after the session begins all retain full access.
    ///
    /// Note: the Devices tab persistent blacklist is also temporarily accessible to
    /// non-game processes while the session is active (inverse mode relaxes normal-mode
    /// blocking for everything except the game). Persistent hiding is restored when the
    /// session ends. This is acceptable for sim racing: companion apps seeing a
    /// persistently-hidden device during a session causes no harm.
    /// </summary>
    public void BeginGameSession(IEnumerable<string> instanceIds, string gameExePath)
    {
        if (!IsAvailable) return;
        var ids = instanceIds.ToList();
        if (ids.Count == 0) return;

        _sessionGameNtPath = Win32ToNtPath(gameExePath);

        // In inverse mode, the whitelist is a deny list. Our own process must NOT be
        // in it (being unlisted = allowed in inverse mode).
        var whitelist = GetWhitelist()
            .Where(p => !IsOurNtPath(p))
            .ToList();

        if (!string.IsNullOrEmpty(_sessionGameNtPath) &&
            !whitelist.Contains(_sessionGameNtPath, StringComparer.OrdinalIgnoreCase))
            whitelist.Add(_sessionGameNtPath);

        SetWhitelist(whitelist);
        SetInverse(true);
        AddSessionBlacklist(ids);
        SetActive(true);
        _sessionActive = true;

        Logger.Write($"[HidHide] Session started — inverse mode, {ids.Count} device(s) hidden");
    }

    /// <summary>
    /// Replaces the session blacklist with a new set (used for mid-session restore of
    /// DisableThenRestore devices — clear + re-add only the ones that should stay hidden).
    /// </summary>
    public void UpdateSessionBlacklist(IEnumerable<string> remainingInstanceIds)
    {
        if (!IsAvailable || !_sessionActive) return;
        ClearSessionBlacklist();      // clears _sessionIds too
        var remaining = remainingInstanceIds.ToList();
        if (remaining.Count > 0)
            AddSessionBlacklist(remaining);   // repopulates _sessionIds
        Logger.WriteVerbose($"[HidHide] Session blacklist updated — {remaining.Count} device(s) remaining");
    }

    /// <summary>
    /// Tears down the game session: clears the session blacklist, removes the game
    /// exe from the inverse deny list, disables inverse mode, and deactivates HidHide
    /// if no persistent entries remain.
    /// </summary>
    public void EndGameSession()
    {
        if (!IsAvailable) return;

        ClearSessionBlacklist();

        // Remove the game exe from the (inverse) whitelist and turn off inverse mode.
        if (!string.IsNullOrEmpty(_sessionGameNtPath))
        {
            var cleaned = GetWhitelist()
                .Where(p => !p.Equals(_sessionGameNtPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SetWhitelist(cleaned);
        }
        SetInverse(false);

        bool hasPersistent = GetBlacklist().Count > 0;
        if (hasPersistent)
            EnsureOwnPathInWhitelist();
        else
            SetActive(false);

        _sessionActive     = false;
        _sessionGameNtPath = null;

        Logger.Write("[HidHide] Session ended");
    }

    // ── Persistent device toggle (Devices tab) ────────────────────────────────────

    public void AddToPersistentBlacklist(string instanceId)
    {
        if (!IsAvailable) return;

        // Ensure our process can still see and manage the device after hiding it.
        EnsureOwnPathInWhitelist();

        var existing = GetBlacklist();
        if (!existing.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(instanceId);
            SetBlacklist(existing);
        }

        if (!GetActive()) SetActive(true);
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

        // If no more persistent entries and no session active, fully deactivate.
        if (updated.Count == 0 && !_sessionActive)
        {
            SetActive(false);
            // Clean up our own process from the whitelist.
            var wl = GetWhitelist()
                .Where(p => !IsOurNtPath(p))
                .ToList();
            SetWhitelist(wl);
        }
        BroadcastDeviceChange();
        Logger.WriteVerbose($"[HidHide] Persistent blacklist remove: {instanceId}");
    }

    public bool IsInPersistentBlacklist(string instanceId)
    {
        if (!IsAvailable) return false;
        return GetBlacklist().Contains(instanceId, StringComparer.OrdinalIgnoreCase);
    }

    // ── Startup recovery ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called on app startup to clean up any leftover state from a previous crash
    /// that occurred mid-session.
    /// </summary>
    public void RecoverOnStartup()
    {
        if (!IsAvailable) return;

        ClearSessionBlacklist();

        // Clean up any inverse mode left over from a previous crash (no longer used,
        // but guard against stale state from old builds).
        if (GetInverse()) SetInverse(false);

        bool hasPersistent = GetBlacklist().Count > 0;
        if (!hasPersistent)
        {
            SetActive(false);
            SetWhitelist([]);
        }
        else
        {
            // Rebuild whitelist from scratch: only ControllerManager.exe.
            // Session-era process paths from a previous run are stale and must be removed.
            var ownNt = OwnNtPath;
            SetWhitelist(string.IsNullOrEmpty(ownNt) ? [] : [ownNt]);
            Logger.Write("[HidHide] Startup: persistent hiding active — whitelist reset to ControllerManager only");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private void EnsureOwnPathInWhitelist()
    {
        var ownNt = OwnNtPath;
        if (string.IsNullOrEmpty(ownNt)) return;
        var existing = GetWhitelist();
        if (!existing.Contains(ownNt, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(ownNt);
            SetWhitelist(existing);
        }
    }

    private bool IsOurNtPath(string ntPath)
    {
        var own = OwnNtPath;
        return !string.IsNullOrEmpty(own) && own.Equals(ntPath, StringComparison.OrdinalIgnoreCase);
    }

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
        var ms = new System.IO.MemoryStream();
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
