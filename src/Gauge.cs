using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NickFanControl;

/// <summary>Runde Temperaturanzeige (270°-Bogen) mit weicher Animation.</summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Gauge),
        new PropertyMetadata(double.NaN, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnAtProperty = DependencyProperty.Register(
        nameof(WarnAt), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(70.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DangerAtProperty = DependencyProperty.Register(
        nameof(DangerAt), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(85.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public double WarnAt { get => (double)GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
    public double DangerAt { get => (double)GetValue(DangerAtProperty); set => SetValue(DangerAtProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private static readonly Typeface ValueFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (Gauge)d;
        var v = (double)e.NewValue;
        if (double.IsNaN(v))
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.SetValue(DisplayValueProperty, 0.0);
            g.InvalidateVisual();
            return;
        }
        var anim = new DoubleAnimation(v, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        g.BeginAnimation(DisplayValueProperty, anim);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = Math.Min(
            double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double thickness = size * 0.075;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - thickness;

        const double start = 135, sweepTotal = 270;

        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, radius, start, sweepTotal));

        bool known = !double.IsNaN(Value);
        double shown = (double)GetValue(DisplayValueProperty);
        double frac = known ? Math.Clamp(shown / Max, 0, 1) : 0;

        Color col = !known ? Color.FromRgb(96, 104, 120)
                  : shown >= DangerAt ? Color.FromRgb(239, 68, 68)
                  : shown >= WarnAt ? Color.FromRgb(245, 158, 11)
                  : Color.FromRgb(56, 189, 248);

        if (frac > 0.002)
        {
            var brush = new LinearGradientBrush(
                Color.FromArgb(255, (byte)(col.R * 0.75), (byte)(col.G * 0.75), (byte)(col.B * 0.9)), col, 0);
            var valuePen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, valuePen, Arc(center, radius, start, sweepTotal * frac));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string valueText = known ? $"{shown:0}°" : "–";
        var ft = new FormattedText(valueText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ValueFace, size * 0.24, Brushes.White, dpi);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2 - size * 0.03));

        var lbl = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            LabelFace, size * 0.085, new SolidColorBrush(Color.FromRgb(139, 147, 161)), dpi);
        dc.DrawText(lbl, new Point(center.X - lbl.Width / 2, center.Y + size * 0.16));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.99);
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
