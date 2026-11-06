using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace NickFanControl;

public partial class App : Application
{
    private const string ShowEventName = "NickFanControl_ShowWindow_Event";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private FanController? _ctl;
    private MainWindow? _win;
    private WF.NotifyIcon? _tray;
    private WF.ToolStripMenuItem? _trayPause;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool startInTray = e.Args.Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        // Nur eine Instanz: zweiter manueller Start holt das Fenster nach vorne
        _mutex = new Mutex(true, "Global\\NickFanControl_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            if (!startInTray)
            {
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { Log.Write("UI-Fehler: " + a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("Fataler Fehler: " + a.ExceptionObject);
        Log.Write($"Start v2 (Tray-Modus: {startInTray})");

        _ctl = new FanController();
        _win = new MainWindow(_ctl);
        _win.Closing += OnWindowClosing;

        BuildTray();
        _ctl.Notify += (title, text, warn) =>
            _tray?.ShowBalloonTip(4000, title, text, warn ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
        _ctl.PropertyChanged += OnControllerChanged;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    if (_showEvent.WaitOne(1000))
                        Dispatcher.BeginInvoke(ShowWindow);
                }
                catch { break; }
            }
        }) { IsBackground = true, Name = "ShowEventListener" }.Start();

        SessionEnding += (_, _) => _ctl?.RestoreAll("Abmelden/Herunterfahren");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _ctl.Start(manualStart: !startInTray);

        if (!startInTray) ShowWindow();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _ctl?.OnResume());
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ctl == null || _tray == null) return;
        if (e.PropertyName is nameof(FanController.StateText) or nameof(FanController.CpuTemp) or nameof(FanController.GpuTemp))
        {
            var text = $"Fan Control – {_ctl.StateText}";
            if (!double.IsNaN(_ctl.CpuTemp)) text += $"\nCPU {_ctl.CpuTemp:0} °C";
            if (!double.IsNaN(_ctl.GpuTemp)) text += $"  GPU {_ctl.GpuTemp:0} °C";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        if (e.PropertyName == nameof(FanController.Paused) && _trayPause != null)
            _trayPause.Checked = _ctl.Paused;
    }

    private void BuildTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        _tray = new WF.NotifyIcon { Icon = icon, Text = "Fan Control", Visible = true };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => ShowWindow());
        _trayPause = new WF.ToolStripMenuItem("Pausieren (BIOS regelt)") { Checked = _ctl!.Paused };
        _trayPause.Click += (_, _) => _ctl.Paused = !_ctl.Paused;
        menu.Items.Add(_trayPause);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
    }

    private void ShowWindow()
    {
        if (_win == null) return;
        _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Topmost = true;  // sicher nach vorne holen
        _win.Topmost = false;
        _win.Focus();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        _win!.Hide();
        if (_ctl != null && !_ctl.TrayHintShown)
        {
            _ctl.TrayHintShown = true;
            _tray?.ShowBalloonTip(4000, "Fan Control läuft weiter",
                "Die Lüftersteuerung bleibt unten rechts im Infobereich aktiv. Rechtsklick auf das Symbol → Beenden.",
                WF.ToolTipIcon.Info);
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _ctl?.Dispose(); // Lüfter zurück ans BIOS
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _win?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            _exiting = true;
            _ctl?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
