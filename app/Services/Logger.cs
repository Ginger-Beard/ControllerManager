using HIDReorder.Models;

namespace HIDReorder.Services;

public static class Logger
{
    private static string?   _path;
    private static LogLevel  _level = LogLevel.Normal;
    private static readonly object _lock = new();

    public static void Initialize(string directory)
    {
        _path = Path.Combine(directory, "log.txt");

        // Rotate: keep last 500 KB
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
                File.Delete(_path);
        }
        catch { }
    }

    public static void SetLevel(LogLevel level)
    {
        _level = level;
        if (level != LogLevel.Off)
            Write($"=== HIDReorder started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (level={level}) ===");
    }

    public static void Write(string message)
    {
        if (_path is null || _level == LogLevel.Off) return;
        AppendLine(message);
    }

    public static void WriteVerbose(string message)
    {
        if (_path is null || _level < LogLevel.Verbose) return;
        AppendLine(message);
    }

    public static void WriteException(string context, Exception ex)
    {
        if (_path is null || _level == LogLevel.Off) return;
        AppendLine($"[EXCEPTION] {context}: {ex}");
    }

    public static string?   LogFilePath    => _path;
    public static LogLevel  CurrentLevel   => _level;

    private static void AppendLine(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_path!, line + Environment.NewLine); }
            catch { }
        }
    }
}
