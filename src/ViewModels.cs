using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace NickFanControl;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class Palette
{
    public static readonly SolidColorBrush Accent = Freeze(Color.FromRgb(56, 189, 248));
    public static readonly SolidColorBrush Good = Freeze(Color.FromRgb(34, 197, 94));
    public static readonly SolidColorBrush Warn = Freeze(Color.FromRgb(245, 158, 11));
    public static readonly SolidColorBrush Bad = Freeze(Color.FromRgb(239, 68, 68));
    public static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(139, 147, 161));
    public static readonly SolidColorBrush Pump = Freeze(Color.FromRgb(167, 139, 250));
    public static readonly SolidColorBrush Idle = Freeze(Color.FromRgb(96, 104, 120));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Eine Lüfter-Karte in der Oberfläche.</summary>
public sealed class FanVM : ObservableObject
{
    private readonly Action<FanVM> _modeChanged;
    private readonly Action _save;

    public FanChannel Ch { get; }
    public FanSetting S { get; }

    public FanVM(FanChannel ch, FanSetting s, Action<FanVM> modeChanged, Action save)
    {
        Ch = ch;
        S = s;
        _modeChanged = modeChanged;
        _save = save;
    }

    public string DisplayName
    {
        get => S.CustomName ?? Ch.DefaultName;
        set
        {
            var v = value?.Trim();
            S.CustomName = string.IsNullOrEmpty(v) || v == Ch.DefaultName ? null : v;
            _save();
            Raise();
            Raise(nameof(OriginalNameText));
        }
    }

    /// <summary>Zeigt den Originalnamen, wenn der Lüfter umbenannt wurde.</summary>
    public string OriginalNameText => S.CustomName == null ? "" : $"{Ch.DefaultName} · ";

    public string Chip => Ch.ChipName;
    public bool HasFan => Ch.Fan != null;

    private double _rpm;
    public double Rpm { get => _rpm; private set { if (Set(ref _rpm, value)) Raise(nameof(RpmText)); } }
    public string RpmText => HasFan ? $"{_rpm:0}" : "–";

    private double _percent;
    public double Percent { get => _percent; private set { if (Set(ref _percent, value)) Raise(nameof(PercentText)); } }
    public string PercentText => $"Leistung {_percent:0} %";

    public bool IsMinimum
    {
        get => S.Mode == FanMode.Minimum;
        set { if (value && S.Mode != FanMode.Minimum) ChangeMode(FanMode.Minimum); }
    }

    public bool IsPump
    {
        get => S.Mode == FanMode.Pump;
        set { if (value && S.Mode != FanMode.Pump) ChangeMode(FanMode.Pump); }
    }

    public bool IsManual
    {
        get => S.Mode == FanMode.Manual;
        set { if (value && S.Mode != FanMode.Manual) ChangeMode(FanMode.Manual); }
    }

    public bool IsCurve
    {
        get => S.Mode == FanMode.Curve;
        set { if (value && S.Mode != FanMode.Curve) ChangeMode(FanMode.Curve); }
    }

    public bool IsAuto
    {
        get => S.Mode == FanMode.Auto;
        set { if (value && S.Mode != FanMode.Auto) ChangeMode(FanMode.Auto); }
    }

    private void ChangeMode(FanMode m)
    {
        S.Mode = m;
        S.UserChosen = true;
        _save();
        Raise(nameof(IsMinimum));
        Raise(nameof(IsPump));
        Raise(nameof(IsManual));
        Raise(nameof(IsCurve));
        Raise(nameof(IsAuto));
        Raise(nameof(MinEnabled));
        Raise(nameof(IconBrush));
        Raise(nameof(SliderValue));
        Raise(nameof(SliderText));
        Raise(nameof(SliderLabel));
        _modeChanged(this);
    }

    /// <summary>Schieberegler: Minimum, feste Pumpenleistung oder manuelle Leistung – je nach Modus.</summary>
    public double SliderValue
    {
        get => S.Mode switch
        {
            FanMode.Pump => S.PumpPercent,
            FanMode.Manual => S.ManualPercent,
            FanMode.Curve => S.CurveMinPercent,
            _ => Math.Max(0, S.MinPercent)
        };
        set
        {
            var v = (int)Math.Round(Math.Clamp(value, 0, 100));
            switch (S.Mode)
            {
                case FanMode.Pump: if (v == S.PumpPercent) return; S.PumpPercent = v; break;
                case FanMode.Manual: if (v == S.ManualPercent) return; S.ManualPercent = v; break;
                case FanMode.Curve: if (v == S.CurveMinPercent) return; S.CurveMinPercent = v; break;
                default: if (v == S.MinPercent) return; S.MinPercent = v; break;
            }
            _save();
            Raise();
            Raise(nameof(SliderText));
            _modeChanged(this); // sofort anwenden
        }
    }

    public string SliderText => S.Mode switch
    {
        FanMode.Pump => $"{S.PumpPercent} %",
        FanMode.Manual => $"{S.ManualPercent} %",
        FanMode.Curve => $"{S.CurveMinPercent} %",
        _ => S.MinPercent < 0 ? "–" : $"{S.MinPercent} %"
    };
    public string SliderLabel => S.Mode switch { FanMode.Pump => "Pumpe", FanMode.Manual => "Leistung", FanMode.Curve => "Mindestens", _ => "Minimum" };

    // ---- Temperatur-Kurve
    public double CurveLow
    {
        get => S.CurveLowC;
        set
        {
            S.CurveLowC = (int)Math.Round(Math.Clamp(value, 30, 85));
            if (S.CurveHighC < S.CurveLowC + 5) { S.CurveHighC = S.CurveLowC + 5; Raise(nameof(CurveHigh)); Raise(nameof(CurveHighText)); }
            _save(); Raise(); Raise(nameof(CurveLowText)); _modeChanged(this);
        }
    }
    public string CurveLowText => $"{S.CurveLowC} °C";

    public double CurveHigh
    {
        get => S.CurveHighC;
        set
        {
            S.CurveHighC = (int)Math.Round(Math.Clamp(value, 40, 95));
            if (S.CurveLowC > S.CurveHighC - 5) { S.CurveLowC = S.CurveHighC - 5; Raise(nameof(CurveLow)); Raise(nameof(CurveLowText)); }
            _save(); Raise(); Raise(nameof(CurveHighText)); _modeChanged(this);
        }
    }
    public string CurveHighText => $"{S.CurveHighC} °C";

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(MinEnabled));
            Raise(nameof(ModeEnabled));
            Raise(nameof(IconBrush));
        }
    }

    public bool ModeEnabled => !Busy;

    public bool MinEnabled => (IsMinimum || IsPump || IsManual || IsCurve) && !Busy;

    private bool _canCalibrate = true;
    public bool CanCalibrate { get => _canCalibrate; set => Set(ref _canCalibrate, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private Brush _statusBrush = Palette.Muted;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private bool _showProgress;
    public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }

    public Brush IconBrush => Busy ? Palette.Warn : IsMinimum ? Palette.Accent : IsPump ? Palette.Pump : IsManual ? Palette.Good : IsCurve ? Palette.Warn : Palette.Muted;

    private double _angle;
    /// <summary>Drehwinkel des animierten Lüfter-Symbols.</summary>
    public double Angle { get => _angle; set => Set(ref _angle, value); }

    public void Refresh()
    {
        Rpm = Ch.Rpm ?? 0;
        Percent = Ch.Percent ?? 0;
        Busy = Ch.Busy;
        Status = Ch.Status;

        StatusBrush =
            Ch.Busy ? Palette.Warn :
            Ch.Status.StartsWith("Minimum") || Ch.Status.StartsWith("Kalibriert") ? Palette.Good :
            Ch.Status.StartsWith("Pumpe ·") ? Palette.Pump :
            Ch.Status.StartsWith("Sicherheit") || Ch.Status.Contains("stand") || Ch.Status.Contains("fehl") ? Palette.Bad :
            Palette.Muted;

        Raise(nameof(SliderValue));
        Raise(nameof(SliderText));
        Raise(nameof(IconBrush));
    }
}

/// <summary>Das kleine Logo in der Titelleiste (dreht sich langsam).</summary>
public sealed class LogoFan : ObservableObject
{
    private double _angle;
    public double Angle { get => _angle; set => Set(ref _angle, value); }
    public Brush IconBrush { get; } = Brushes.White;
}
