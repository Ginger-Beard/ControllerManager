using System.Runtime.InteropServices;
using ControllerManager.Native;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Services;

/// <summary>
/// Watches for the moment a target game process opens one of the
/// always-visible HID device files configured for the profile.
///
/// EAC-safe: ETW is one-way kernel telemetry — we never touch the game's
/// process, handles, or memory.
///
/// Path matching: ETW reports file names in NT object form
/// (e.g. <c>\Device\HID00000123</c>), not the Win32 symbolic link form we
/// have on <see cref="Models.HidDevice.DeviceInterfacePath"/>. <see cref="Start"/>
/// translates each Win32 path to its NT path via <c>NtQueryObject</c> at watch
/// start. Only events whose <c>FileName</c> matches one of those NT paths fire
/// the <see cref="Acquired"/> signal — failed opens on hidden devices (different
/// path) and unrelated HID activity (different process) are filtered out.
/// </summary>
public sealed class FirstDeviceAcquisitionWatcher : IDisposable
{
    private const string SessionName = "ControllerManager_DeviceAcquisition";

    private TraceEventSession? _session;
    private Task?              _processTask;
    private int                _targetPid = -1;
    private HashSet<string>    _watchedNtPaths = new(StringComparer.OrdinalIgnoreCase);
    private int                _fired;

    public event Action? Acquired;

    /// <summary>
    /// Subscribe to the kernel file-create stream filtered to the game's PID
    /// AND the NT paths of the supplied Win32 device interface paths. Returns
    /// true if ETW + path resolution succeeded; false on any failure (caller
    /// should fall back to timer-based reveal).
    /// </summary>
    public bool Start(int targetPid, IEnumerable<string> alwaysVisibleDevicePaths)
    {
        if (_session != null)
            throw new InvalidOperationException("FirstDeviceAcquisitionWatcher already started");

        _targetPid = targetPid;
        _fired     = 0;
        _watchedNtPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolve each Win32 device interface path (\\?\HID#xxx#{guid}) to its
        // NT object path (\Device\xxx) so the substring match below is exact.
        foreach (var win32Path in alwaysVisibleDevicePaths)
        {
            var nt = ResolveNtPath(win32Path);
            if (!string.IsNullOrEmpty(nt))
            {
                _watchedNtPaths.Add(nt);
                Logger.WriteVerbose($"[Acquisition] Watching: {nt}");
            }
            else
            {
                Logger.WriteVerbose($"[Acquisition] Could not resolve NT path for: {win32Path}");
            }
        }

        if (_watchedNtPaths.Count == 0)
        {
            Logger.Write("[Acquisition] No resolvable always-visible device paths — ETW watcher won't help.");
            return false;
        }

        try
        {
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
            };

            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit);
            _session.Source.Kernel.FileIOCreate += OnFileCreate;

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Logger.WriteException("ETW Source.Process", ex); }
            });

            Logger.WriteVerbose($"[Acquisition] Watching PID {targetPid} for {_watchedNtPaths.Count} path(s)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteException("FirstDeviceAcquisitionWatcher.Start", ex);
            CleanupSession();
            return false;
        }
    }

    private void OnFileCreate(FileIOCreateTraceData data)
    {
        if (_targetPid < 0 || data.ProcessID != _targetPid) return;
        var path = data.FileName;
        if (string.IsNullOrEmpty(path)) return;

        // Exact match against any resolved NT path. ETW occasionally reports
        // a path with case differences or leading prefix variation — ordinal
        // case-insensitive equality is the safe default here.
        if (!_watchedNtPaths.Contains(path)) return;

        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        Logger.Write($"[Acquisition] Game opened watched device: {path}");
        try { Acquired?.Invoke(); } catch (Exception ex) { Logger.WriteException("Acquired handler", ex); }
    }

    public void Stop()
    {
        if (_session == null) return;
        Logger.WriteVerbose("[Acquisition] Stopping ETW session");
        try { _session.Source.Kernel.FileIOCreate -= OnFileCreate; } catch { }
        CleanupSession();
        try { _processTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _processTask = null;
    }

    private void CleanupSession()
    {
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    public void Dispose() => Stop();

    // ── NT path resolution ──────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x7;
    private const uint OPEN_EXISTING                = 3;

    /// <summary>
    /// Translates a Win32 device interface path (<c>\\?\HID#xxx#{guid}</c>) to
    /// the underlying NT object path (<c>\Device\xxx</c>) the kernel reports
    /// in ETW. Returns null on any failure.
    /// </summary>
    private static string? ResolveNtPath(string win32Path)
    {
        if (string.IsNullOrEmpty(win32Path)) return null;

        // Open with 0 access — works even when the device is owned exclusively
        // by another process. We just need a handle to query the object name.
        using var h = CreateFileW(win32Path, 0,
            FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // OBJECT_NAME_INFORMATION is a UNICODE_STRING (Length, MaxLen, Buffer)
        // immediately followed by the string data. Query for buffer size first.
        const int bufSize = 1024;
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            int status = NtDll.NtQueryObject(h.DangerousGetHandle(),
                NtDll.ObjectNameInformation, buf, bufSize, out _);
            if (status != 0) return null;

            // UNICODE_STRING on x64: Length(2) + MaxLen(2) + pad(4) + Buffer(8) = 16 bytes
            short length = Marshal.ReadInt16(buf, 0);
            IntPtr strPtr = Marshal.ReadIntPtr(buf, 8);
            if (length <= 0 || strPtr == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(strPtr, length / 2);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
