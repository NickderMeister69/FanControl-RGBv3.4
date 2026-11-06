using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NickFanControl;

public partial class MainWindow : Window
{
    private readonly FanController _ctl;
    private TimeSpan _lastFrame = TimeSpan.Zero;

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(540.0));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public LogoFan LogoFan { get; } = new();

    public MainWindow(FanController ctl)
    {
        _ctl = ctl;
        InitializeComponent();
        DataContext = ctl;

        SizeChanged += (_, _) => UpdateCardWidth();
        StateChanged += (_, _) =>
        {
            // Rahmenloses Fenster ragt maximiert über den Bildschirmrand → ausgleichen
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _ctl.Rgb.PreviewActive = IsVisible && NavLights.IsChecked == true;
            if (IsVisible)
            {
                _lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
        };
    }

    private void UpdateCardWidth()
    {
        // Platz = Fensterbreite − Rahmen − ScrollViewer-Padding − Scrollbar
        double avail = ActualWidth - RootBorder.Margin.Left - RootBorder.Margin.Right - 2 - 20 - 12;
        int cols = avail >= 1040 ? 2 : 1;
        CardWidth = Math.Max(420, Math.Floor(avail / cols) - 16 - 1);
    }

    /// <summary>Dreht die Lüfter-Symbole passend zur echten Drehzahl (nur wenn das Fenster sichtbar ist).</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero || now == _lastFrame) { _lastFrame = now; return; }
        double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        foreach (var f in _ctl.Fans)
        {
            if (f.Rpm <= 1) continue;
            // sichtbare Geschwindigkeit: 1000 U/min ≈ 400 °/s (echte Drehzahl würde nur flimmern)
            f.Angle = (f.Angle + f.Rpm * 0.4 * dt) % 360;
        }
        LogoFan.Angle = (LogoFan.Angle + 60 * dt) % 360;
    }

    // ------------------------------------------------------------------ Titelleiste

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close(); // App fängt Closing ab → Tray

    // ------------------------------------------------------------------ Aktionen

    private static FanVM? Vm(object sender) => (sender as FrameworkElement)?.DataContext as FanVM;

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } f) _ctl.Calibrate(f);
    }

    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } f) _ctl.Identify(f);
    }

    private void CalibrateAll_Click(object sender, RoutedEventArgs e) => _ctl.RecalibrateAll();

    private void Welcome_Click(object sender, RoutedEventArgs e) => _ctl.DismissWelcome();

    private void PawnIo_Click(object sender, RoutedEventArgs e) => FanController.OpenUrl("https://pawnio.eu");

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.Folder);
        FanController.OpenUrl(AppConfig.Folder);
    }

    // ------------------------------------------------------------------ Navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (FansPage == null || LightsPage == null) return;
        bool lights = NavLights.IsChecked == true;
        FansPage.Visibility = lights ? Visibility.Collapsed : Visibility.Visible;
        LightsPage.Visibility = lights ? Visibility.Visible : Visibility.Collapsed;
        _ctl.Rgb.PreviewActive = lights && IsVisible;
    }

    // ------------------------------------------------------------------ Beleuchtung

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ctl.Rgb.Rescan();
    private void Hardware_Click(object sender, RoutedEventArgs e) => _ctl.Rgb.OpenHardwareList();

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EffectVM fx) fx.AddColor();
    }

    private void RemoveColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ColorItem c) c.Owner.RemoveColor(c);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ColorItem c && fe.Tag is string hex)
        {
            c.Hex = hex;
            // Farbwähler nach der Auswahl schließen
            DependencyObject? d = fe;
            while (d != null && d is not System.Windows.Controls.Primitives.Popup) d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
            if (d is System.Windows.Controls.Primitives.Popup p) p.IsOpen = false;
        }
    }

    private void FanMinus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LightZoneVM z) z.ChangeFanCount(-1);
    }

    private void FanPlus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LightZoneVM z) z.ChangeFanCount(1);
    }

    private void Sensors_Click(object sender, RoutedEventArgs e) => _ctl.WriteSensorList(true);

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { Log.Write("Neustart fehlgeschlagen: " + ex.Message); }
        ((App)Application.Current).ExitApp();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        // Textfeld neben dem Stift-Button finden und zum Bearbeiten fokussieren
        if (sender is FrameworkElement b && b.Parent is Panel p && p.Children.OfType<TextBox>().FirstOrDefault() is { } tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void Name_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.Dispatcher.BeginInvoke(tb.SelectAll);
    }

    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox tb)
        {
            if (e.Key == Key.Enter) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            else tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }
}
