using System.Collections.ObjectModel;
using System.IO;
using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NickFanControl;

/// <summary>Die eigentliche Lüfterlogik: Regel-Loop, Kalibrierung, Sicherheitsmodus.</summary>
public sealed class FanController : ObservableObject, IDisposable
{
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly FanManager _mgr = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _tickRunning, _tickPending;
    private DateTime _lastSensorUpdate = DateTime.MinValue;
    private readonly CancellationTokenSource _cts = new();
    private long _tick;
    private float _hot;
    private float _curveTemp;
    private bool _disposed;

    public ObservableCollection<FanVM> Fans { get; } = new();
    /// <summary>Nur die Anschlüsse, an denen wirklich ein Lüfter hängt (oder die der Nutzer eingestellt hat).</summary>
    public ObservableCollection<FanVM> VisibleFans { get; } = new();

    public bool ShowEmptyFans
    {
        get => _cfg.ShowEmptyFans;
        set { _cfg.ShowEmptyFans = value; _cfg.Save(); Raise(); UpdateState(); }
    }

    private int _hiddenFans;
    public int HiddenFans { get => _hiddenFans; private set { if (Set(ref _hiddenFans, value)) Raise(nameof(HiddenText)); } }
    public string HiddenText => _hiddenFans == 0 ? "" : $"{_hiddenFans} leere Anschlüsse ausgeblendet";

    /// <summary>Titel, Text, Warnung? → Tray-Benachrichtigung</summary>
    public event Action<string, string, bool>? Notify;

    /// <summary>Beleuchtung (RGB) – eingebaute Treiber, kein Zusatzprogramm nötig.</summary>
    public RgbController Rgb { get; }

    public FanController()
    {
        _timer.Tick += (_, _) => Tick();
        Rgb = new RgbController(_cfg);
    }

    // ------------------------------------------------------------------ Eigenschaften für die UI

    private double _cpuTemp = double.NaN;
    public double CpuTemp { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }

    private double _gpuTemp = double.NaN;
    public double GpuTemp { get => _gpuTemp; private set => Set(ref _gpuTemp, value); }

    private string _stateText = "Startet …";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    private Brush _stateBrush = Palette.Muted;
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private bool _safety;
    public bool Safety { get => _safety; private set => Set(ref _safety, value); }

    private bool _calibrating;
    public bool Calibrating
    {
        get => _calibrating;
        private set { if (Set(ref _calibrating, value)) Raise(nameof(CanCalibrateAll)); }
    }

    public bool CanCalibrateAll => !Calibrating && Fans.Count > 0;

    public bool Paused
    {
        get => _cfg.Paused;
        set
        {
            if (_cfg.Paused == value) return;
            _cfg.Paused = value;
            _cfg.Save();
            Raise();
            Tick();
        }
    }

    public bool Autostart
    {
        get => _cfg.Autostart;
        set
        {
            if (_cfg.Autostart == value) return;
            _cfg.Autostart = value;
            _cfg.Save();
            if (value)
            {
                if (!NickFanControl.Autostart.Enable(out var err))
                    Notify?.Invoke("Autostart", "Konnte nicht eingerichtet werden: " + err, true);
            }
            else NickFanControl.Autostart.Disable();
            Raise();
        }
    }

    public double SafetyTemp
    {
        get => _cfg.SafetyTempC;
        set
        {
            var v = (int)Math.Round(Math.Clamp(value, 60, 100));
            if (v == _cfg.SafetyTempC) return;
            _cfg.SafetyTempC = v;
            _cfg.SafetyResumeC = v - 10;
            _cfg.Save();
            Raise();
            Raise(nameof(SafetyText));
        }
    }

    public string SafetyText => $"{_cfg.SafetyTempC} °C";

    // ---- Temperatur-Quellen
    public ObservableCollection<TempOption> CpuSources { get; } = new();
    public ObservableCollection<TempOption> GpuSources { get; } = new();

    private TempOption? _cpuSource;
    public TempOption? CpuSource
    {
        get => _cpuSource;
        set
        {
            if (value == null || !Set(ref _cpuSource, value)) return;
            _cfg.CpuSensorId = value.Id;
            _cfg.Save();
            Tick();
        }
    }

    private TempOption? _gpuSource;
    public TempOption? GpuSource
    {
        get => _gpuSource;
        set
        {
            if (value == null || !Set(ref _gpuSource, value)) return;
            _cfg.GpuSensorId = value.Id;
            _cfg.Save();
            Tick();
        }
    }

    private void BuildTempSources()
    {
        CpuSources.Clear();
        GpuSources.Clear();
        CpuSources.Add(new TempOption(null, "Automatisch – Ø P-Kerne"));
        GpuSources.Add(new TempOption(null, "Automatisch – GPU Core"));
        var all = _mgr.TemperatureSensors().ToList();
        foreach (var t in all.Where(t => t.Type == HardwareType.Cpu).Concat(all.Where(t => t.Type != HardwareType.Cpu)))
            CpuSources.Add(new TempOption(t.Id, t.Label));
        foreach (var t in all.Where(t => t.Type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                             .Concat(all.Where(t => t.Type is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))))
            GpuSources.Add(new TempOption(t.Id, t.Label));

        _cpuSource = CpuSources.FirstOrDefault(o => o.Id == _cfg.CpuSensorId) ?? CpuSources[0];
        _gpuSource = GpuSources.FirstOrDefault(o => o.Id == _cfg.GpuSensorId) ?? GpuSources[0];
        Raise(nameof(CpuSource));
        Raise(nameof(GpuSource));
    }

    /// <summary>Sensorliste schreiben (AppData + neben die exe), optional öffnen.</summary>
    public void WriteSensorList(bool open)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            var main = Path.Combine(AppConfig.Folder, "sensoren.txt");
            float? cpu = double.IsNaN(CpuTemp) ? null : (float)CpuTemp;
            float? gpu = double.IsNaN(GpuTemp) ? null : (float)GpuTemp;
            _mgr.DumpSensors(main, cpu, gpu);
            try
            {
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath!);
                if (exeDir != null) File.Copy(main, Path.Combine(exeDir, "sensoren.txt"), true);
            }
            catch { /* Ordner evtl. schreibgeschützt */ }
            if (open) OpenUrl(main);
        }
        catch (Exception ex) { Log.Write("Sensorliste fehlgeschlagen: " + ex.Message); }
    }

    private bool _noFans;
    public bool NoFans { get => _noFans; private set => Set(ref _noFans, value); }

    private string _bannerText = "";
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }

    private bool _showWelcome;
    public bool ShowWelcome { get => _showWelcome; private set => Set(ref _showWelcome, value); }

    public string Version => "v3.7.1";

    // ------------------------------------------------------------------ Start / Ende

    public void Start(bool manualStart)
    {
        _mgr.Open();

        bool upgradeFromV1 = _cfg.ConfigVersion < 2 && _cfg.Fans.Count > 0;
        bool upgradeToV3 = _cfg.ConfigVersion < 3 && _cfg.Fans.Count > 0;
        foreach (var ch in _mgr.Channels)
        {
            bool isNew = !_cfg.Fans.ContainsKey(ch.Id);
            var s = _cfg.Get(ch.Id);
            if (isNew) s.Mode = GuessMode(ch);
            // v1 hat Pumpen auf "Automatisch" gestellt → jetzt feste 30 %
            else if (upgradeFromV1 && IsPumpName(ch) && s.Mode == FanMode.Auto) s.Mode = FanMode.Pump;
            // v3: Lüfter mit allgemeinem Namen ("Fan #1") nicht mehr selbst auf Minimum stellen –
            // das könnte der CPU-Lüfter sein. Der Nutzer ordnet sie einmal zu.
            if (upgradeToV3 && GuessMode(ch) == FanMode.Auto && !IsCpuName(ch))
            {
                s.Mode = FanMode.Auto;
                s.MinPercent = -1;
                s.UserChosen = false;
            }
            Fans.Add(new FanVM(ch, s, _ => Tick(), _cfg.Save));
        }
        if (upgradeToV3) _cfg.FanSetupConfirmed = false;
        _cfg.ConfigVersion = 3;
        _cfg.Save();
        Raise(nameof(CanCalibrateAll));

        if (!_mgr.Opened || _mgr.Channels.Count == 0)
        {
            bool pawnIo = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") != null;
            BannerText = _mgr.Opened
                ? "Es wurden keine steuerbaren Lüfter gefunden. " +
                  (pawnIo ? "PawnIO ist installiert – vermutlich erlaubt dein Mainboard keine Steuerung aus Windows."
                          : "Der Treiber PawnIO fehlt. Die Lüftersteuerung des Mainboards braucht ihn: herunterladen, installieren, App neu starten.")
                : "Die Hardware konnte nicht geöffnet werden (" + _mgr.OpenError + "). Wahrscheinlich fehlt der Treiber PawnIO.";
            NoFans = true;
        }

        if (_cfg.Autostart && !NickFanControl.Autostart.Enable(out var err))
            Notify?.Invoke("Autostart", "Konnte nicht eingerichtet werden: " + err, true);

        if (manualStart && !_cfg.FanSetupConfirmed && Fans.Count > 0) ShowWelcome = true;

        BuildTempSources();
        Tick();
        _timer.Start();
    }

    public void DismissWelcome()
    {
        ShowWelcome = false;
        _cfg.FirstRunDone = true;
        _cfg.FanSetupConfirmed = true;
        _cfg.Save();
    }

    public bool TrayHintShown
    {
        get => _cfg.TrayHintShown;
        set { _cfg.TrayHintShown = value; _cfg.Save(); }
    }

    public void RestoreAll(string why)
    {
        _mgr.ShuttingDown = true;
        try { _mgr.RestoreAll(); Log.Write("Lüfter an BIOS zurückgegeben: " + why); } catch { }
    }

    public void OnResume()
    {
        foreach (var ch in _mgr.Channels) { ch.LastTarget = -1; ch.SoftwareActive = false; }
        Log.Write("Aus Standby aufgewacht – Werte werden neu gesetzt.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _timer.Stop();
        try { Rgb.Dispose(); } catch { }
        _mgr.Dispose();
        Log.Write("Beendet, Lüfter an BIOS zurückgegeben.");
    }

    /// <summary>
    /// Pumpe → feste 30 %, CPU-Lüfter → BIOS. Gehäuselüfter mit eindeutigem Namen → Minimum.
    /// Allgemeine Namen ("Fan #1") → BIOS, bis der Nutzer sie zuordnet (könnte der CPU-Lüfter sein!).
    /// </summary>
    private static FanMode GuessMode(FanChannel ch)
    {
        if (IsPumpName(ch)) return FanMode.Pump;
        if (IsCpuName(ch)) return FanMode.Auto;
        var n = ch.DefaultName.ToUpperInvariant();
        bool caseFan = n.Contains("SYS") || n.Contains("CHA") || n.Contains("CASE") || n.Contains("SYSTEM");
        return caseFan ? FanMode.Minimum : FanMode.Auto;
    }

    private static bool IsCpuName(FanChannel ch) => ch.DefaultName.ToUpperInvariant().Contains("CPU");

    /// <summary>Ab dieser Temperatur werden Minimum-Lüfter stufenlos schneller.</summary>
    private double RampStart => Math.Min(_cfg.RampStartC, _cfg.SafetyTempC - 5);

    /// <summary>Ab dieser Temperatur drehen auch die Minimum-Lüfter hoch (Einstellung in der Oberfläche).</summary>
    public double RampTemp
    {
        get => _cfg.RampStartC;
        set
        {
            var v = (int)Math.Round(Math.Clamp(value, 40, 90));
            if (v == _cfg.RampStartC) return;
            _cfg.RampStartC = v;
            _cfg.Save();
            Raise(); Raise(nameof(RampText));
        }
    }
    public string RampText => $"{_cfg.RampStartC} °C";

    private static bool IsPumpName(FanChannel ch)
    {
        var n = ch.DefaultName.ToUpperInvariant();
        string[] pump = { "PUMP", "AIO", "WATER", "W_PUMP" };
        return pump.Any(n.Contains);
    }

    // ------------------------------------------------------------------ Regel-Loop

    /// <summary>
    /// Regel-Durchlauf. Die Sensoren werden im Hintergrund gelesen – unter Last dauert das deutlich länger
    /// und würde sonst das Fenster einfrieren.
    /// </summary>
    private async void Tick()
    {
        if (_disposed) return;
        if (_tickRunning) { _tickPending = true; return; }
        _tickRunning = true;
        try
        {
            do
            {
                _tickPending = false;
                if (_mgr.Opened && (DateTime.Now - _lastSensorUpdate).TotalMilliseconds > 400)
                {
                    await Task.Run(_mgr.UpdateAll);
                    _lastSensorUpdate = DateTime.Now;
                }
                if (_disposed) return;
                TickCore();
            } while (_tickPending && !_disposed);
        }
        catch (Exception ex) { Log.Write("Regel-Durchlauf fehlgeschlagen: " + ex); }
        finally { _tickRunning = false; }
    }

    private void TickCore()
    {
        if (!_mgr.Opened) { UpdateState(); return; }
        _tick++;

        float? cpu = _mgr.CpuTemp(_cfg.CpuSensorId);
        float? gpu = _mgr.GpuTemp(_cfg.GpuSensorId);
        CpuTemp = cpu ?? double.NaN;
        GpuTemp = gpu ?? double.NaN;
        float hot = Math.Max(cpu ?? 0, gpu ?? 0);

        _hot = hot;
        // geglättete CPU-Temperatur für die Temperatur-Kurve (Spitzen werden abgefedert)
        float cpuNow = cpu ?? hot;
        // steigt sofort mit, fällt langsam ab (damit der Lüfter nicht nervös rauf und runter dreht)
        _curveTemp = _curveTemp <= 0 || cpuNow > _curveTemp ? cpuNow : _curveTemp + (cpuNow - _curveTemp) * 0.12f;
        if (!Safety && hot >= _cfg.SafetyTempC)
        {
            Safety = true;
            Log.Write($"Sicherheitsmodus: {hot:0} °C");
            Notify?.Invoke("Sicherheitsmodus", $"Temperatur {hot:0} °C – alle geregelten Lüfter laufen auf voller Drehzahl.", true);
        }
        else if (Safety && hot < _cfg.SafetyResumeC)
        {
            Safety = false;
            Log.Write($"Sicherheitsmodus beendet: {hot:0} °C");
        }

        foreach (var ch in _mgr.Channels)
        {
            if (ch.Busy) continue;
            var s = _cfg.Get(ch.Id);

            // Sicherheitsmodus: nicht ans BIOS zurückgeben (klappt nicht auf jedem Board),
            // sondern alle von der App geregelten Lüfter auf 100 %
            if (Safety && s.Mode != FanMode.Auto)
            {
                if (!ch.SoftwareActive || ch.LastTarget < 99.5f || _tick % 15 == 0) _mgr.SetPercent(ch, 100);
                ch.Status = "Sicherheitsmodus · 100 %";
                continue;
            }

            string reason =
                s.Mode == FanMode.Auto ? "BIOS regelt automatisch" :
                _cfg.Paused ? "Pausiert – BIOS regelt" :
                s.Mode == FanMode.Minimum && s.MinPercent < 0
                    ? (_cfg.FanSetupConfirmed || s.UserChosen ? "Wartet auf Kalibrierung" : "Bitte zuordnen – BIOS regelt") : "";

            if (reason != "")
            {
                if (ch.SoftwareActive) _mgr.SetAuto(ch);
                ch.Status = reason;
                continue;
            }

            bool pump = s.Mode == FanMode.Pump;
            bool manual = s.Mode == FanMode.Manual;
            bool curve = s.Mode == FanMode.Curve;
            float target = pump ? s.PumpPercent : manual ? s.ManualPercent : s.MinPercent;
            if (curve)
            {
                // Temperatur-Kurve nach der (geglätteten) CPU-Temperatur
                double t = _curveTemp;
                double f = Math.Clamp((t - s.CurveLowC) / Math.Max(1.0, s.CurveHighC - s.CurveLowC), 0, 1);
                float want = (float)Math.Round(s.CurveMinPercent + (100 - s.CurveMinPercent) * f);
                // schnell hoch, langsam runter – damit der Lüfter nicht ständig "pumpt"
                float last = ch.LastTarget < 0 ? want : ch.LastTarget;
                target = want > last ? want : Math.Max(want, last - 2); // hoch sofort, runter langsam
            }
            // Wird es warm, laufen auch "Minimum"-Lüfter stufenlos schneller (bis 100 % an der Sicherheitsgrenze)
            float ramp = 0;
            if (!pump && !manual && !curve && hot > RampStart)
            {
                ramp = (float)Math.Clamp((hot - RampStart) / Math.Max(1, _cfg.SafetyTempC - RampStart), 0, 1);
                target = (float)Math.Round(target + (100 - target) * ramp);
            }
            bool mismatch = ch.Percent is float p && Math.Abs(p - target) > 2.5f;
            if (!ch.SoftwareActive || Math.Abs(ch.LastTarget - target) > 0.1f || mismatch || _tick % 15 == 0)
                _mgr.SetPercent(ch, target);

            // Stillstand-Wächter: steht der Lüfter ~6 s am Stück, anstoßen und Minimum anheben
            bool stalled = !manual && ch.Fan != null && ch.Rpm is float rpm && rpm < 50 &&
                           (DateTime.Now - ch.TargetSince).TotalSeconds > 10 && target < 100;
            ch.StallTicks = stalled ? ch.StallTicks + 1 : 0;
            if (ch.StallTicks >= 3)
            {
                ch.StallTicks = 0;
                _mgr.SetPercent(ch, 70);
                if (pump)
                {
                    // Pumpe darf nie stehen: Wert anheben und warnen
                    s.PumpPercent = Math.Min(100, s.PumpPercent + 5);
                    ch.Status = $"Pumpe stand – auf {s.PumpPercent} % erhöht";
                    Notify?.Invoke("Pumpe stand still", $"Die Pumpe lief bei {s.PumpPercent - 5} % nicht – jetzt {s.PumpPercent} %.", true);
                }
                else
                {
                    if (curve)
                    {
                        s.CurveMinPercent = Math.Min(100, s.CurveMinPercent + 5);
                        ch.Status = $"Lüfter stand – Mindestwert auf {s.CurveMinPercent} % erhöht";
                    }
                    else
                    {
                        s.MinPercent = Math.Min(100, s.MinPercent + 5);
                        ch.Status = $"Lüfter stand – Minimum auf {s.MinPercent} % erhöht";
                    }
                }
                _cfg.Save();
                Log.Write($"{ch.Id}: Stillstand erkannt → {ch.Status}");
                continue;
            }

            ch.Status = pump ? $"Pumpe · fest {s.PumpPercent} %"
                      : manual ? $"Manuell · {s.ManualPercent} %"
                      : curve ? $"Temperatur · {_curveTemp:0} °C → {target:0} %"
                      : ramp > 0 ? $"Minimum aktiv · warm → {target:0} %" : $"Minimum aktiv · {s.MinPercent} %";
        }

        // Sensorliste einmal nach ~20 s schreiben (dann sind alle Werte eingeschwungen)
        if (_tick == 10) WriteSensorList(false);

        // Neue Minimum-Lüfter automatisch kalibrieren
        if (!Calibrating && !Safety && !_cfg.Paused && hot < RampStart)
        {
            var next = Fans.FirstOrDefault(f => !f.Ch.Busy && f.HasFan && f.S is { Mode: FanMode.Minimum, MinPercent: < 0 }
                                                && (_cfg.FanSetupConfirmed || f.S.UserChosen));
            if (next != null) _ = CalibrateAsync(next);
        }

        UpdateState();
    }

    private void UpdateState()
    {
        if (!_mgr.Opened || Fans.Count == 0) { StateText = "Keine Steuerung"; StateBrush = Palette.Bad; }
        else if (Safety) { StateText = "Sicherheitsmodus"; StateBrush = Palette.Bad; }
        else if (_cfg.Paused) { StateText = "Pausiert"; StateBrush = Palette.Warn; }
        else if (Calibrating) { StateText = "Kalibriert …"; StateBrush = Palette.Accent; }
        else { StateText = "Aktiv"; StateBrush = Palette.Good; }

        // Anschlüsse ohne Lüfter ausblenden
        foreach (var f in Fans)
            if (!f.Ch.SeenSpinning && f.Ch.Rpm is float r && r > 100) f.Ch.SeenSpinning = true;
        var visible = Fans.Where(f => _cfg.ShowEmptyFans || f.Ch.SeenSpinning || f.Ch.Busy).ToList();
        if (!visible.SequenceEqual(VisibleFans))
        {
            VisibleFans.Clear();
            foreach (var f in visible) VisibleFans.Add(f);
        }
        HiddenFans = Fans.Count - visible.Count;

        int quiet = visible.Count(f => f.Ch.SoftwareActive && !f.Ch.Busy && f.S.Mode == FanMode.Minimum);
        int fanCount = visible.Count(f => f.S.Mode != FanMode.Pump);
        int pumps = visible.Count(f => f.S.Mode == FanMode.Pump);
        Summary = visible.Count == 0 ? "Keine Lüfter gefunden"
                : $"{fanCount} Lüfter · {quiet} leise geregelt" + (pumps > 0 ? $" · {pumps} Pumpe{(pumps > 1 ? "n" : "")} auf festem Wert" : "");

        foreach (var f in Fans)
        {
            f.CanCalibrate = !Calibrating && !f.Ch.Busy && f.HasFan && !f.IsPump;
            f.Refresh();
        }
    }

    // ------------------------------------------------------------------ Kalibrierung / Identifizieren

    public void RecalibrateAll()
    {
        foreach (var f in Fans)
            if (f.S.Mode == FanMode.Minimum) f.S.MinPercent = -1;
        _cfg.Save();
        Tick();
    }

    public void Calibrate(FanVM f)
    {
        if (Calibrating || f.Ch.Busy || f.IsPump) return;
        if (!f.IsMinimum) f.IsMinimum = true; // ruft Tick nicht doppelt auf, da MinPercent gleich danach -1
        f.S.MinPercent = -1;
        _cfg.Save();
        _ = CalibrateAsync(f);
    }

    private async Task CalibrateAsync(FanVM f)
    {
        var ch = f.Ch;
        if (Calibrating || ch.Busy) return;
        Calibrating = true;
        ch.Busy = true;
        f.ShowProgress = true;
        f.Progress = 0;
        var s = f.S;
        var ct = _cts.Token;
        int[] steps = { 60, 50, 40, 35, 30, 25, 20, 15, 10, 5, 0 };

        void Step(string text, double progress)
        {
            ch.Status = text;
            f.Progress = progress;
            UpdateState();
        }

        try
        {
            Step("Kalibriere · Anlauf mit 70 %", 0.02);
            _mgr.SetPercent(ch, 70);
            await Task.Delay(5000, ct);
            float rpm = await _mgr.MeasureRpmAsync(ch, ct);
            if (rpm < 100)
            {
                s.Mode = FanMode.Auto;
                _mgr.SetAuto(ch);
                _cfg.Save();
                ch.Status = "Keine Drehzahl erkannt – bleibt beim BIOS";
                Log.Write($"{ch.Id}: keine Drehzahl bei 70 % – vermutlich kein Lüfter angeschlossen.");
                return;
            }

            int lastSpinning = 70;
            float lastRpm = rpm;
            for (int i = 0; i < steps.Length; i++)
            {
                if (Safety || _hot >= RampStart) throw new OperationCanceledException("zu warm");
                int p = steps[i];
                Step($"Kalibriere · teste {p} %", (i + 1.0) / (steps.Length + 1));
                _mgr.SetPercent(ch, p);
                await Task.Delay(3500, ct);
                rpm = await _mgr.MeasureRpmAsync(ch, ct);
                if (rpm < 100) break;
                lastSpinning = p;
                lastRpm = rpm;
            }

            int result = Math.Clamp(lastSpinning + 5, 10, 100);

            Step("Kalibriere · wieder anlaufen lassen", 0.97);
            _mgr.SetPercent(ch, 70);
            await Task.Delay(2500, ct);
            _mgr.SetPercent(ch, result);

            s.MinPercent = result;
            _cfg.Save();
            ch.Status = $"Kalibriert · {result} % (ca. {lastRpm:0} U/min)";
            Log.Write($"{ch.Id}: kalibriert auf {result} % (dreht noch bei {lastSpinning} % mit ~{lastRpm:0} U/min)");
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
            {
                _mgr.SetAuto(ch);
                ch.Status = "Kalibrierung abgebrochen";
            }
        }
        catch (Exception ex)
        {
            _mgr.SetAuto(ch);
            ch.Status = "Kalibrierung fehlgeschlagen";
            Log.Write($"{ch.Id}: Kalibrierung fehlgeschlagen: {ex}");
        }
        finally
        {
            ch.Busy = false;
            Calibrating = false;
            f.ShowProgress = false;
            if (!_disposed) UpdateState();
        }
    }

    public async void Identify(FanVM f)
    {
        var ch = f.Ch;
        if (ch.Busy) return;
        ch.Busy = true;
        try
        {
            ch.Status = "Identifizieren · 100 % für 4 Sekunden";
            UpdateState();
            _mgr.SetPercent(ch, 100);
            await Task.Delay(4000, _cts.Token);
            ch.LastTarget = -1;
            if (f.S.Mode == FanMode.Auto) _mgr.SetAuto(ch);
        }
        catch (OperationCanceledException) { }
        finally
        {
            ch.Busy = false;
            if (!_disposed) Tick();
        }
    }

    // ------------------------------------------------------------------ Sonstiges

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Öffnen fehlgeschlagen: " + ex.Message); }
    }
}

public sealed record TempOption(string? Id, string Label)
{
    public override string ToString() => Label;
}
