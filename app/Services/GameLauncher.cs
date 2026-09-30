using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ControllerManager.Services;

public enum LaunchMethod { SteamUrl, DeElevatedExplorer, DirectElevated }

/// <summary>
/// Launches a game at the user's normal integrity level even though this app runs
/// elevated. Steam games are started through steam://rungameid/ so Steam itself
/// (and the game) are never launched elevated.
/// </summary>
public static class GameLauncher
{
    private static readonly Regex InstallDirRx = new("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
    private static readonly Regex AppIdRx      = new("\"appid\"\\s+\"(\\d+)\"",        RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolves the Steam app ID for an exe under steamapps\common\, via steam_appid.txt
    /// next to (or above) the exe, else the matching appmanifest_*.acf. Null if not a Steam game.
    /// </summary>
    public static string? TryResolveSteamAppId(string exePath)
    {
        try
        {
            var full   = Path.GetFullPath(exePath);
            var marker = @"\steamapps\common\";
            var idx    = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var steamapps = full[..(idx + @"\steamapps".Length)];
            var rest      = full[(idx + marker.Length)..];
            var sep       = rest.IndexOf('\\');
            var installDir = sep < 0 ? rest : rest[..sep];
            var gameRoot   = Path.Combine(steamapps, "common", installDir);

            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir))
            {
                var idFile = Path.Combine(dir, "steam_appid.txt");
                if (File.Exists(idFile)
                    && uint.TryParse(File.ReadAllText(idFile).Trim(), out var id))
                    return id.ToString();

                if (string.Equals(dir.TrimEnd('\\'), gameRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    break;
                dir = Path.GetDirectoryName(dir);
            }

            if (Directory.Exists(steamapps))
            {
                foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    var text = File.ReadAllText(acf);
                    var m    = InstallDirRx.Match(text);
                    if (!m.Success || !m.Groups[1].Value.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var a = AppIdRx.Match(text);
                    if (a.Success) return a.Groups[1].Value;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.WriteException("GameLauncher.ResolveAppId", ex);
            return null;
        }
    }

    public static LaunchMethod Launch(string exePath, Action<string> log)
    {
        var appId  = TryResolveSteamAppId(exePath);
        var target = appId is not null ? $"steam://rungameid/{appId}" : exePath;
        var method = appId is not null ? LaunchMethod.SteamUrl : LaunchMethod.DeElevatedExplorer;

        string reason;
        try
        {
            // explorer.exe hands the request to the already-running desktop shell, which
            // starts the target at the user's normal integrity level — so neither the game
            // nor Steam is started elevated.
            var psi = new ProcessStartInfo
            {
                FileName        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(target);

            using var p = Process.Start(psi);
            if (p is not null)
            {
                log($"Launch via {method}: explorer.exe \"{target}\" (de-elevated via desktop shell)");
                return method;
            }
            reason = "Process.Start returned null";
        }
        catch (Exception ex)
        {
            Logger.WriteException("GameLauncher.Explorer", ex);
            reason = ex.Message;
        }

        // Only reached when explorer never started (never after a wait timeout — that would launch twice).
        var direct = new ProcessStartInfo { FileName = target, UseShellExecute = true };
        if (appId is null)
            direct.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
        using (Process.Start(direct)) { }

        log($"WARNING: explorer hand-off failed ({reason}); launched {target} directly - game runs ELEVATED.");
        return LaunchMethod.DirectElevated;
    }
}
