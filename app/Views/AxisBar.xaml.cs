using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ControllerManager.ViewModels;

namespace ControllerManager.Views;

/// <summary>
/// Renders an axis: a 0..1 fill bar plus a translucent green band showing
/// the calibrated deadzone range from <see cref="AxisViewModel.DeadzoneLower"/>
/// to <see cref="AxisViewModel.DeadzoneUpper"/>. The fill bar shows the current
/// Value. Both update via direct width assignments in code-behind because XAML
/// converters can't easily multiply by the live ActualWidth of the parent.
/// </summary>
public partial class AxisBar : UserControl
{
    private double _width;
    private AxisViewModel? _vm;

    public AxisBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded           += (_, _) => Detach();
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _vm = DataContext as AxisViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Redraw();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any of these affect what we render — keep the conditions tight so unrelated
        // notifications (StatusText, IsCalibrating) don't trigger a redraw.
        switch (e.PropertyName)
        {
            case nameof(AxisViewModel.Value):
            case nameof(AxisViewModel.DeadzoneLower):
            case nameof(AxisViewModel.DeadzoneUpper):
                Redraw();
                break;
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _width = e.NewSize.Width;
        Redraw();
    }

    private void Redraw()
    {
        if (_vm is null || _width <= 0) return;

        // Fill: from 0 to value × width
        var v = Math.Max(0f, Math.Min(1f, _vm.Value));
        FillRect.Width = _width * v;

        // Deadzone band: from DeadzoneLower × width, extending across the range.
        // When lower==upper the band collapses to 0 width and disappears.
        var lo = Math.Max(0f, Math.Min(1f, _vm.DeadzoneLower));
        var hi = Math.Max(lo, Math.Min(1f, _vm.DeadzoneUpper));
        DeadzoneRect.Margin = new Thickness(_width * lo, 0, 0, 0);
        DeadzoneRect.Width  = _width * (hi - lo);
    }
}
