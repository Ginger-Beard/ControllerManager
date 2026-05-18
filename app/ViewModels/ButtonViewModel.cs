namespace HIDReorder.ViewModels;

public sealed class ButtonViewModel : ViewModelBase
{
    public string Label { get; }

    private bool _isPressed;
    public bool IsPressed
    {
        get => _isPressed;
        set => Set(ref _isPressed, value);
    }

    public ButtonViewModel(string label) => Label = label;
}
