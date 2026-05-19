namespace ControllerManager.Models;

public sealed record HandleWatcherEntry(
    string Timestamp,
    string DeviceName,
    string Path);
