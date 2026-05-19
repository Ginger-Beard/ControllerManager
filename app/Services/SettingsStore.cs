using System.Diagnostics;
using System.Text.Json;
using ControllerManager.Models;

namespace ControllerManager.Services;

public sealed class SettingsStore(string path)
{
    private const string TaskName = "ControllerManager_Startup";
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts)
                ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(settings, Opts)); }
        catch { }
        ApplyStartWithWindows(settings.StartWithWindows);
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        try
        {
            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (exe is null) return;

                // Create a scheduled task that runs elevated at logon with no UAC prompt.
                // /RL HIGHEST = "Run with highest privileges"; /IT = only when user is logged on.
                // --startup arg tells the app this launch came from boot, so "Start minimized
                // to tray" only applies here (manual launches always show the window).
                RunSchtasks($"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --startup\" " +
                            $"/SC ONLOGON /RL HIGHEST /IT /DELAY 0000:10");
            }
            else
            {
                RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
            }
        }
        catch { }
    }

    public static bool GetStartWithWindows()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/Query /TN \"{TaskName}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void RunSchtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName               = "schtasks.exe",
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        })!;
        p.WaitForExit(10_000);
    }
}
