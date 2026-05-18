using System.Text.Json;
using Microsoft.Win32;
using HIDReorder.Models;

namespace HIDReorder.Services;

public sealed class SettingsStore(string path)
{
    private const string RunKey    = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue  = "HIDReorder";
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enable)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exe is not null) key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public static bool GetStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch { return false; }
    }
}
