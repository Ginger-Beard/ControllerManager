using System.Windows.Input;

namespace ControllerManager.ViewModels;

/// <summary>
/// A 2D X/Y stick visualization. X and Y come from the input monitor as values
/// in the 0..1 range; we translate them to centered coordinates (-1..+1) for
/// display.
///
/// Drift calibration: pressing "Calibrate" starts a 5-second window during which
/// the stick should be left untouched at rest. The largest |X|, |Y|, and radial
/// excursion from center are recorded. The radial value is the recommended
/// in-game deadzone — it's the smallest circle around the centre that contains
/// all the noise the stick produces while idle.
/// </summary>
public sealed class StickViewModel : ViewModelBase
{
    public string Label { get; }

    // Centered coordinates in [-1, 1]. Bindings drive the position dot.
    private float _centeredX;
    public float CenteredX { get => _centeredX; private set => Set(ref _centeredX, value); }

    private float _centeredY;
    public float CenteredY { get => _centeredY; private set => Set(ref _centeredY, value); }

    // Recommended deadzone — the radius (0..1) inside which input is "noise."
    // Updated by drift calibration. Drawn as a translucent circle on the pad.
    private float _deadzoneRadius;
    public float DeadzoneRadius { get => _deadzoneRadius; private set => Set(ref _deadzoneRadius, value); }

    // Display text under the pad ("idle drift: 4.2% — set deadzone ≥5%")
    private string _statusText = "Idle drift not measured.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private bool _isCalibrating;
    public bool IsCalibrating { get => _isCalibrating; private set => Set(ref _isCalibrating, value); }

    public ICommand CalibrateCommand { get; }

    // Per-calibration state
    private DateTime _calibrationEnd;
    private float _maxRadius;
    private float _maxX;
    private float _maxY;

    public StickViewModel(string label)
    {
        Label = label;
        CalibrateCommand = new RelayCommand(_ => StartCalibration(), _ => !IsCalibrating);
    }

    /// <summary>
    /// Called each polling tick with normalized (0..1) X and Y values from the
    /// input monitor. We translate to centered (-1..+1) and, if calibrating,
    /// update the drift maxima.
    /// </summary>
    public void Update(float normalizedX, float normalizedY)
    {
        // 0..1 → -1..+1. Most HID sticks idle near 0.5 → centered 0.0.
        CenteredX = normalizedX * 2f - 1f;
        CenteredY = normalizedY * 2f - 1f;

        if (!IsCalibrating) return;

        var ax = Math.Abs(CenteredX);
        var ay = Math.Abs(CenteredY);
        var r  = MathF.Sqrt(CenteredX * CenteredX + CenteredY * CenteredY);
        if (ax > _maxX)      _maxX      = ax;
        if (ay > _maxY)      _maxY      = ay;
        if (r  > _maxRadius) _maxRadius = r;

        if (DateTime.UtcNow >= _calibrationEnd)
            FinishCalibration();
    }

    private void StartCalibration()
    {
        IsCalibrating   = true;
        _maxRadius      = 0;
        _maxX           = 0;
        _maxY           = 0;
        _calibrationEnd = DateTime.UtcNow.AddSeconds(5);
        StatusText      = "Hold the stick centered — measuring drift...";
        DeadzoneRadius  = 0;
    }

    private void FinishCalibration()
    {
        IsCalibrating  = false;
        // Recommend slightly larger than measured to give breathing room
        DeadzoneRadius = Math.Min(1f, _maxRadius * 1.15f);
        var pct        = DeadzoneRadius * 100f;
        StatusText     = $"Idle drift: {_maxRadius * 100f:F1}% — recommend in-game deadzone ≥{pct:F0}%";
    }
}
