using System.Windows.Input;

namespace ControllerManager.ViewModels;

public sealed class AxisViewModel : ViewModelBase
{
    public string Name { get; }

    private float _value;
    /// <summary>Normalized 0..1 value from the input monitor.</summary>
    public float Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            if (_isCalibrating) RecordCalibrationSample(value);
        }
    }

    private string _rawText = "—";
    public string RawText { get => _rawText; set => Set(ref _rawText, value); }

    // ── Deadzone calibration ────────────────────────────────────────────────────
    //
    // Same idea as StickViewModel.Calibrate but 1D. The baseline is whatever the
    // axis reads at the moment "Calibrate" is pressed (so pedals at 0, sticks at
    // 0.5, inverted triggers at 1.0 all behave). Max excursion |Value - baseline|
    // is tracked for 5s; the recommended deadzone band is ±(excursion × 1.15)
    // around baseline, clamped to [0, 1].

    private float _deadzoneLower;
    public float DeadzoneLower { get => _deadzoneLower; private set => Set(ref _deadzoneLower, value); }

    private float _deadzoneUpper;
    public float DeadzoneUpper { get => _deadzoneUpper; private set => Set(ref _deadzoneUpper, value); }

    public bool HasDeadzone => DeadzoneUpper > DeadzoneLower;

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private bool _isCalibrating;
    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            if (Set(ref _isCalibrating, value)) OnPropertyChanged(nameof(IsNotCalibrating));
        }
    }
    public bool IsNotCalibrating => !_isCalibrating;

    public ICommand CalibrateCommand { get; }

    private float    _calibrationBaseline;
    private float    _calibrationMaxDrift;
    private DateTime _calibrationEnd;

    public AxisViewModel(string name)
    {
        Name = name;
        CalibrateCommand = new RelayCommand(_ => StartCalibration(), _ => !IsCalibrating);
    }

    private void StartCalibration()
    {
        _calibrationBaseline = _value;
        _calibrationMaxDrift = 0;
        _calibrationEnd      = DateTime.UtcNow.AddSeconds(5);
        IsCalibrating        = true;
        DeadzoneLower        = 0;
        DeadzoneUpper        = 0;
        OnPropertyChanged(nameof(HasDeadzone));
        StatusText = "Hold at rest — measuring 5s...";
    }

    private void RecordCalibrationSample(float v)
    {
        var drift = MathF.Abs(v - _calibrationBaseline);
        if (drift > _calibrationMaxDrift) _calibrationMaxDrift = drift;

        if (DateTime.UtcNow < _calibrationEnd) return;

        // Done — compute the band
        var halfBand = Math.Min(0.5f, _calibrationMaxDrift * 1.15f);
        DeadzoneLower = Math.Max(0f, _calibrationBaseline - halfBand);
        DeadzoneUpper = Math.Min(1f, _calibrationBaseline + halfBand);
        IsCalibrating = false;
        OnPropertyChanged(nameof(HasDeadzone));

        var pct = halfBand * 100f;
        StatusText = $"Drift {_calibrationMaxDrift * 100f:F1}% — deadzone ≥{pct:F0}%";
    }
}
