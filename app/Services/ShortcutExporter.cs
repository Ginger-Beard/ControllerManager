namespace ControllerManager.Services;

public static class ShortcutExporter
{
    /// <summary>
    /// Creates a .lnk that launches the given profile without a UAC prompt by
    /// running through a per-profile Scheduled Task (see LaunchTaskManager).
    /// The .lnk targets <c>schtasks.exe /Run /TN ...</c>; the icon is still
    /// pulled from the game exe so the shortcut looks like the game.
    /// </summary>
    public static void CreateShortcut(string lnkPath, Guid profileId, string gameExePath)
    {
        var appExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("Cannot determine app exe path.");

        // Make sure the launch task exists / is up to date with the current exe path.
        // Updating the task is cheap (idempotent /Create /F) so we do it every time.
        if (!LaunchTaskManager.EnsureTaskForProfile(profileId))
            throw new InvalidOperationException(
                "Could not create the scheduled task that backs this shortcut. " +
                "Try running Controller Manager as an administrator once to register the task.");

        // schtasks.exe is in System32. Resolve a stable path so the .lnk isn't
        // sensitive to PATH changes when the user is in a different shell.
        var schtasks = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

        // Use WScript.Shell COM object via reflection — no interop assembly needed.
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                     ?? throw new InvalidOperationException("WScript.Shell COM not available.");
        var shell    = Activator.CreateInstance(shellType)!;
        var shortcut = shellType.InvokeMember("CreateShortcut",
            System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath })!;
        var st = shortcut.GetType();

        void Set(string prop, object val) =>
            st.InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { val });

        Set("TargetPath",       schtasks);
        Set("Arguments",        $"/Run /TN \"{LaunchTaskManager.TaskName(profileId)}\"");
        Set("WorkingDirectory", Path.GetDirectoryName(appExe) ?? "");
        Set("Description",      "Launch game via Controller Manager");

        // Prefer the game's own icon; fall back to the app exe icon.
        // Icon is independent of TargetPath, so the schtasks indirection is invisible.
        var iconSource = File.Exists(gameExePath) ? gameExePath : appExe;
        Set("IconLocation", $"{iconSource},0");

        // Hide the schtasks console window when the shortcut runs.
        // WindowStyle 7 = "Minimized, no focus."
        Set("WindowStyle", 7);

        st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
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
