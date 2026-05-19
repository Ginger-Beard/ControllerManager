namespace ControllerManager.Services;

public static class ShortcutExporter
{
    public static void CreateShortcut(string lnkPath, Guid profileId, string gameExePath)
    {
        var appExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("Cannot determine app exe path.");

        // Use WScript.Shell COM object — no interop assembly needed
        var shellType  = Type.GetTypeFromProgID("WScript.Shell")
                      ?? throw new InvalidOperationException("WScript.Shell COM not available.");
        dynamic shell    = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);

        shortcut.TargetPath       = appExe;
        shortcut.Arguments        = $"--launch {profileId}";
        shortcut.WorkingDirectory = Path.GetDirectoryName(appExe) ?? "";
        shortcut.Description      = "Launch game via Controller Manager";

        // Prefer the game's own icon; fall back to the app exe icon
        var iconSource = File.Exists(gameExePath) ? gameExePath : appExe;
        shortcut.IconLocation = $"{iconSource},0";

        shortcut.Save();
    }

    public static string DesktopPath(string profileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"{Sanitize(profileName)}.lnk");

    public static string StartMenuPath(string profileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            $"{Sanitize(profileName)}.lnk");

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
