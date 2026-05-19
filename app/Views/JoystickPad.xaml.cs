using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ControllerManager.ViewModels;

namespace ControllerManager.Views;

public partial class JoystickPad : UserControl
{
    private double _padW = 100;
    private double _padH = 100;

    public JoystickPad()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded           += (_, _) => Detach();
    }

    private StickViewModel? _vm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _vm = DataContext as StickViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateDot();
        UpdateDeadzoneCircle();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StickViewModel.CenteredX):
            case nameof(StickViewModel.CenteredY):
                UpdateDot();
                break;
            case nameof(StickViewModel.DeadzoneRadius):
                UpdateDeadzoneCircle();
                break;
        }
    }

    private void Pad_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _padW = e.NewSize.Width;
        _padH = e.NewSize.Height;
        UpdateDot();
        UpdateDeadzoneCircle();
    }

    private void UpdateDot()
    {
        if (_vm is null) return;

        // Map centered (-1..+1) to pixel position. Y is inverted so +Y reads as
        // "up" on screen (most HID sticks send +Y when the stick is pulled down,
        // but for visualization purposes flipping feels natural).
        var dotR  = PositionDot.Width / 2;
        var centerX = _padW / 2;
        var centerY = _padH / 2;
        var px = centerX + _vm.CenteredX * (_padW / 2) - dotR;
        var py = centerY + _vm.CenteredY * (_padH / 2) - dotR;

        // Clamp into the pad so the dot doesn't escape if a value briefly exceeds 1
        px = Math.Max(0, Math.Min(_padW - PositionDot.Width,  px));
        py = Math.Max(0, Math.Min(_padH - PositionDot.Height, py));

        PositionDot.Margin = new Thickness(px, py, 0, 0);
    }

    private void UpdateDeadzoneCircle()
    {
        if (_vm is null) return;
        var r = _vm.DeadzoneRadius;
        if (r <= 0)
        {
            DeadzoneCircle.Width  = 0;
            DeadzoneCircle.Height = 0;
            return;
        }
        var diameter = r * _padW; // r is fractional radius; diameter = 2r * (padW/2)
        DeadzoneCircle.Width  = diameter;
        DeadzoneCircle.Height = diameter;
    }
}
