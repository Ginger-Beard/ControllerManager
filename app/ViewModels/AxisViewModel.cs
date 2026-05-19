namespace ControllerManager.ViewModels;

public sealed class AxisViewModel : ViewModelBase
{
    public string Name { get; }

    private float _value;
    public float Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    private string _rawText = "—";
    public string RawText
    {
        get => _rawText;
        set => Set(ref _rawText, value);
    }

    public AxisViewModel(string name) => Name = name;
}
