namespace ControllerManager.ViewModels;

public enum DeviceRole { AlwaysVisible, RevealAfterStart, AlwaysHidden }

public sealed class DeviceAssignmentViewModel : ViewModelBase
{
    public record RoleChoice(DeviceRole Role, string Name);

    public static readonly IReadOnlyList<RoleChoice> AllRoles = [
        new(DeviceRole.AlwaysVisible,    "Always Visible"),
        new(DeviceRole.RevealAfterStart, "Reveal After Start"),
        new(DeviceRole.AlwaysHidden,     "Always Hidden"),
    ];

    public string InstanceId   { get; }
    public string FriendlyName { get; }

    private DeviceRole _role;
    public DeviceRole Role
    {
        get => _role;
        set { if (Set(ref _role, value)) OnPropertyChanged(nameof(IsRevealAfterStart)); }
    }

    private int _delaySeconds;
    public int DelaySeconds
    {
        get => _delaySeconds;
        set => Set(ref _delaySeconds, Math.Max(0, value));
    }

    public bool IsRevealAfterStart => Role == DeviceRole.RevealAfterStart;

    public DeviceAssignmentViewModel(string instanceId, string friendlyName,
                                     DeviceRole role, int delaySeconds = 0)
    {
        InstanceId    = instanceId;
        FriendlyName  = friendlyName;
        _role         = role;
        _delaySeconds = delaySeconds;
    }
}
