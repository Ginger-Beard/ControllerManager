// DeviceWatcher — live HID device visibility monitor for testing ControllerManager profiles.
//
// Usage:
//   DeviceWatcher.exe           (run standalone)
//   Press Q or Ctrl-C to exit.
//
// Set as the "game" in a ControllerManager profile to test hide/show behaviour:
//   1. dotnet publish -c Release -r win-x64 --no-self-contained
//   2. In ControllerManager → Games tab: create a profile, set game exe = DeviceWatcher.exe
//   3. Launch via ControllerManager — it hides configured devices then starts this app
//   4. Watch the table update as devices become accessible / hidden / re-enabled
//
// States:
//   ● OPEN     CreateFile succeeded — device is fully accessible
//   ○ HIDDEN   ERROR_ACCESS_DENIED — HidHide is hiding this device
//   - DISABLED No HID interface found — device is PnP-disabled (pnputil backend)
//   ? NO PATH  Device has VID/PID but interface path couldn't be derived

using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DeviceWatcher;

static class Program
{
    const int PollMs = 500;

    // ── P/Invoke ──────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    const uint FILE_SHARE_READ_WRITE = 0x3;
    const uint OPEN_EXISTING         = 3;
    const int  ERROR_ACCESS_DENIED   = 5;
    const int  ERROR_FILE_NOT_FOUND  = 2;
    const int  ERROR_PATH_NOT_FOUND  = 3;

    static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Entry point ───────────────────────────────────────────────────────────────

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible  = false;
        Console.Title          = "DeviceWatcher — HID visibility monitor";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        _ = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
                    cts.Cancel();
            }
        });

        var lastState = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
        var logLines  = new List<string>();

        Console.Clear();
        PrintHeader();

        while (!cts.Token.IsCancellationRequested)
        {
            var devices = EnumerateHidDevices();
            var changed = new List<(string name, DeviceState prev, DeviceState next)>();

            foreach (var d in devices)
            {
                var state = ProbeState(d.Path);
                if (lastState.TryGetValue(d.InstanceId, out var prev) && prev != state)
                    changed.Add((d.FriendlyName, prev, state));
                lastState[d.InstanceId] = state;
            }

            foreach (var (name, prev, next) in changed)
            {
                var arrow = $"{StateLabel(prev)} → {StateLabel(next)}";
                logLines.Add($"  {DateTime.Now:HH:mm:ss.fff}  {arrow,-26}  {name}");
                if (logLines.Count > 8) logLines.RemoveAt(0);
            }

            // Redraw device table
            Console.SetCursorPosition(0, 4);
            int row = 0;
            foreach (var d in devices.OrderBy(d => d.FriendlyName))
            {
                var state = lastState.TryGetValue(d.InstanceId, out var s) ? s : DeviceState.NoPath;
                var (icon, color) = StateStyle(state);

                Console.ForegroundColor = color;
                Console.Write($"  {icon}  {StateLabel(state),-10}");
                Console.ResetColor();
                Console.WriteLine($"  {d.VidPid,-16}  {Truncate(d.FriendlyName, 52)}".PadRight(80));
                row++;
            }
            // Clear stale rows if list shrank
            for (; row < 20; row++)
                Console.WriteLine(new string(' ', 80));

            // Redraw change log
            Console.SetCursorPosition(0, 26);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  Recent changes:".PadRight(80));
            Console.ResetColor();
            Console.WriteLine();
            foreach (var line in logLines)
                Console.WriteLine(line.PadRight(80));
            for (int i = logLines.Count; i < 8; i++)
                Console.WriteLine(new string(' ', 80));

            try { await Task.Delay(PollMs, cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        Console.CursorVisible = true;
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("DeviceWatcher exited.");
    }

    // ── Header ────────────────────────────────────────────────────────────────────

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  DeviceWatcher — HID visibility monitor     (Q or Ctrl-C to exit)");
        Console.ResetColor();
        Console.WriteLine(new string('─', 80));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {"State",-12}  {"VID:PID",-16}  Device");
        Console.ResetColor();
        Console.WriteLine(new string('─', 80));
    }

    // ── Device enumeration ────────────────────────────────────────────────────────

    record HidEntry(string InstanceId, string FriendlyName, string VidPid, string? Path);

    static List<HidEntry> EnumerateHidDevices()
    {
        var results = new List<HidEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_PnPEntity " +
                "WHERE ClassGuid = '{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var id   = obj["DeviceID"]?.ToString();
                var name = obj["Name"]?.ToString() ?? "(unknown)";
                if (id is null) continue;
                if (id.StartsWith("USB\\",  StringComparison.OrdinalIgnoreCase)) continue;
                if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) continue;

                var vidM = VidRx.Match(id);
                var pidM = PidRx.Match(id);
                if (!vidM.Success || !pidM.Success) continue;

                var vidPid = $"{vidM.Groups[1].Value.ToUpper()}:{pidM.Groups[1].Value.ToUpper()}";
                results.Add(new HidEntry(id, name, vidPid, ToDevicePath(id)));
            }
        }
        catch { }
        return [.. results.OrderBy(d => d.FriendlyName)];
    }

    // Derives the HID device interface path from a device instance ID.
    static string? ToDevicePath(string instanceId)
    {
        const string hidGuid = "{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var normalized = instanceId.Replace('\\', '#').ToLowerInvariant();
        return $@"\\?\hid#{normalized}#{hidGuid}";
    }

    // ── Accessibility probe ────────────────────────────────────────────────────────

    static DeviceState ProbeState(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return DeviceState.NoPath;

        // Open with 0 desired access — enough to detect HidHide blocking.
        using var h = CreateFile(devicePath, 0, FILE_SHARE_READ_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (!h.IsInvalid) return DeviceState.Open;

        return Marshal.GetLastWin32Error() switch
        {
            ERROR_ACCESS_DENIED                          => DeviceState.Hidden,
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => DeviceState.Disabled,
            _                                            => DeviceState.NoPath,
        };
    }

    // ── Display helpers ────────────────────────────────────────────────────────────

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string StateLabel(DeviceState s) => s switch
    {
        DeviceState.Open     => "OPEN",
        DeviceState.Hidden   => "HIDDEN",
        DeviceState.Disabled => "DISABLED",
        _                    => "NO PATH",
    };

    static (string icon, ConsoleColor color) StateStyle(DeviceState s) => s switch
    {
        DeviceState.Open     => ("●", ConsoleColor.Green),
        DeviceState.Hidden   => ("○", ConsoleColor.Red),
        DeviceState.Disabled => ("-", ConsoleColor.DarkYellow),
        _                    => ("?", ConsoleColor.DarkGray),
    };
}

enum DeviceState { Open, Hidden, Disabled, NoPath }
