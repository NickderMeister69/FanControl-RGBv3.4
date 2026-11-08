using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace NickFanControl;

public sealed class EngineFan
{
    public int Start { get; init; }
    public int Count { get; init; }
    public bool FollowSync { get; init; }
    public EffectSnap? Own { get; init; }
    /// <summary>Zuletzt berechnete Farben (für die Live-Vorschau in der Oberfläche).</summary>
    public volatile RgbF[] Current = Array.Empty<RgbF>();
    public RgbF[]? Smoothed;
    /// <summary>Bildschirm-Effekt mit eigenem Bereich für diesen Anschluss (sonst null).</summary>
    public EffectSnap? ScreenSnap { get; init; }
}

public sealed class EngineZone
{
    public required RgbZone Target { get; init; }
    public int LedCount { get; init; }
    public bool Enabled { get; init; }
    public EngineFan[] Fans { get; init; } = Array.Empty<EngineFan>();
    public byte[]? LastSent;
    public DateTime LastSendTime;
}

public sealed class EngineState
{
    public bool Enabled { get; init; }
    public bool Sync { get; init; }
    public double Brightness { get; init; } = 1;
    public EffectSnap Global { get; init; } = new();
    public EngineZone[] Zones { get; init; } = Array.Empty<EngineZone>();
}

/// <summary>Was die Oberfläche über eine gefundene Zone wissen muss.</summary>
public sealed record ZoneDescriptor(RgbZone Zone, string Key, string DeviceName, string Vendor, string ZoneName, string Kind,
                                    int LedCount, bool Adjustable, int MaxLeds, bool PerLed, int LedsPerFan, int DefaultFans);

/// <summary>
/// Effekt-Thread: findet die RGB-Hardware (eingebaute Treiber, kein Zusatzprogramm nötig),
/// berechnet ~30× pro Sekunde alle Farben und schickt sie an die Geräte.
/// </summary>
public sealed class RgbEngine : IDisposable
{
    private const int PreviewLeds = 24;

    /// <summary>Gamma 2.2 – wie bei Monitoren, damit LED-Farben satt aussehen.</summary>
    private static readonly byte[] Gamma = Enumerable.Range(0, 256)
        .Select(i => (byte)Math.Round(255 * Math.Pow(i / 255.0, 2.2))).ToArray();

    private readonly Thread _thread;
    private readonly SynchronizationContext _ui;
    private readonly ScreenSampler _screen = new();
    private readonly ConcurrentQueue<Action> _actions = new();
    private List<RgbDevice> _devices = new();
    private readonly Dictionary<RgbDevice, int> _errors = new();
    private volatile EngineState _state = new();
    private volatile bool _running = true;
    private RgbF[]? _globalSmoothed;

    public volatile RgbF[] GlobalPreview = new RgbF[PreviewLeds];

    /// <summary>Aktuelle Bildschirmfarben (für die Vorschau in der Oberfläche).</summary>
    public ScreenColors Screen => _screen.Colors;
    public bool ScreenActive => _screen.Active;

    /// <summary>Neue Geräteliste (im UI-Thread).</summary>
    public event Action<List<ZoneDescriptor>, List<DetectedHardware>>? DevicesChanged;
    /// <summary>Statusmeldung (im UI-Thread).</summary>
    public event Action<string, bool>? StatusChanged;

    public string HardwareReportText { get; private set; } = "";

    public RgbEngine(SynchronizationContext ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RgbEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Rescan();
    }

    public void SetState(EngineState s) => _state = s;

    public void Rescan() => _actions.Enqueue(DoScan);

    public void SetLedCount(RgbZone zone, int count) => _actions.Enqueue(() =>
    {
        zone.LedCount = Math.Clamp(count, 0, zone.MaxLeds);
        try { zone.Device.ApplyLedCount(zone); } catch (Exception ex) { Log.Write("LED-Anzahl setzen fehlgeschlagen: " + ex.Message); }
    });

    private void DoScan()
    {
        foreach (var d in _devices) { try { d.Dispose(); } catch { } }
        _devices = new List<RgbDevice>();
        _errors.Clear();
        _state = new EngineState { Enabled = _state.Enabled, Sync = _state.Sync, Brightness = _state.Brightness, Global = _state.Global };

        Post(() => StatusChanged?.Invoke("Suche Geräte …", true));
        var (devices, report, all) = RgbScanner.Scan();
        _devices = devices;
        HardwareReportText = RgbScanner.HardwareReport(all, report);
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            File.WriteAllText(Path.Combine(AppConfig.Folder, "hardware.txt"), HardwareReportText);
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath!);
            if (exeDir != null) File.WriteAllText(Path.Combine(exeDir, "hardware.txt"), HardwareReportText);
        }
        catch { }

        var zones = devices.SelectMany(d => d.Zones.Select(z =>
            new ZoneDescriptor(z, z.Key, d.Name, d.Vendor, z.Name, z.Kind, z.LedCount, z.Adjustable, z.MaxLeds, z.PerLed,
                               z.LedsPerFan, z.DefaultFans))).ToList();
        Post(() => DevicesChanged?.Invoke(zones, report));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int ok = _devices.Count(d => !_errors.TryGetValue(d, out var e) || e < 5);
        string text = _devices.Count == 0
            ? "Keine unterstützten RGB-Geräte gefunden"
            : $"{ok} Gerät{(ok == 1 ? "" : "e")} · {_devices.Sum(d => d.Zones.Count)} Zonen";
        Post(() => StatusChanged?.Invoke(text, _devices.Count > 0));
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    // ------------------------------------------------------------------ Hauptschleife

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        while (_running)
        {
            var frameStart = sw.Elapsed;
            while (_actions.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { Log.Write("RGB-Aktion fehlgeschlagen: " + ex); }
            }

            var st = _state;
            double t = sw.Elapsed.TotalSeconds;

            _screen.Active = st.Enabled && UsesScreen(st);
            ScreenColors? screen = _screen.Active ? _screen.Colors : null;

            // Vorschau des synchronen Effekts (auch ohne Geräte)
            var prev = new RgbF[PreviewLeds];
            for (int i = 0; i < PreviewLeds; i++)
                prev[i] = EffectMath.Eval(st.Global, t, i / (double)PreviewLeds, screen);
            if (st.Global.Type == LightEffect.Screen) prev = SmoothArr(ref _globalSmoothed, prev, st.Global.Smooth);
            GlobalPreview = prev;

            // Farben je Lüfter berechnen
            foreach (var z in st.Zones)
                foreach (var f in z.Fans)
                {
                    var snap = st.Sync || f.FollowSync || f.Own == null ? st.Global : f.Own;
                    if (snap.Type == LightEffect.Screen && f.ScreenSnap != null) snap = f.ScreenSnap;
                    var arr = new RgbF[Math.Max(1, f.Count)];
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] = EffectMath.Eval(snap, t, arr.Length == 1 ? 0.5 : i / (double)arr.Length, screen);
                    if (snap.Type == LightEffect.Screen) arr = SmoothArr(ref f.Smoothed, arr, snap.Smooth);
                    f.Current = arr;
                }

            if (st.Enabled) SendToDevices(st);

            var elapsed = (sw.Elapsed - frameStart).TotalMilliseconds;
            Thread.Sleep((int)Math.Max(5, 33 - elapsed));
        }
    }

    private void SendToDevices(EngineState st)
    {
        var now = DateTime.Now;
        foreach (var dev in _devices)
        {
            if (_errors.TryGetValue(dev, out var errs) && errs >= 5)
            {
                // Gerät hat mehrfach nicht reagiert → 15 s Pause, dann neuer Versuch
                if ((now - dev.LastUpdate).TotalSeconds < 15) continue;
                _errors[dev] = 3;
            }
            if ((now - dev.LastUpdate).TotalMilliseconds < dev.MinIntervalMs) continue;

            bool any = false;
            try
            {
                foreach (var z in st.Zones)
                {
                    if (z.Target.Device != dev || !z.Enabled) continue;
                    int leds = Math.Max(1, z.Target.LedCount);
                    var buf = new byte[leds * 3];
                    foreach (var f in z.Fans)
                    {
                        var cur = f.Current;
                        if (cur.Length == 0) continue;
                        for (int i = 0; i < f.Count && f.Start + i < leds; i++)
                        {
                            var c = cur[Math.Min(i, cur.Length - 1)].Scale(st.Brightness);
                            int o = (f.Start + i) * 3;
                            // Gamma-Korrektur: LEDs sind linear, sonst wirken Farben blass/weißlich
                            buf[o] = Gamma[c.R8]; buf[o + 1] = Gamma[c.G8]; buf[o + 2] = Gamma[c.B8];
                        }
                    }

                    bool changed = z.LastSent == null || !buf.AsSpan().SequenceEqual(z.LastSent);
                    // Regelmäßig erneut senden, damit andere Programme die Farben nicht übernehmen
                    bool keepAlive = dev.KeepAliveSeconds > 0 && (now - z.LastSendTime).TotalSeconds > dev.KeepAliveSeconds;
                    // Anschlüsse mit nur einer Farbe (12-V-RGB, MSI, ASRock …) setzen bei jedem Befehl ihren Effekt neu.
                    // Bei animierten Effekten deshalb höchstens ~8× pro Sekunde und nur bei sichtbarer Änderung –
                    // sonst kommt der Controller nicht hinterher und zeigt nichts an.
                    if (changed && !keepAlive && z.LastSent != null && (!z.Target.PerLed || leds == 1))
                    {
                        if ((now - z.LastSendTime).TotalMilliseconds < 125) changed = false;
                        else
                        {
                            int diff = 0;
                            for (int k = 0; k < buf.Length && k < z.LastSent.Length; k++) diff = Math.Max(diff, Math.Abs(buf[k] - z.LastSent[k]));
                            if (diff < 3) changed = false;
                        }
                    }
                    if (changed || keepAlive)
                    {
                        dev.SetZone(z.Target, buf);
                        z.LastSent = buf;
                        z.LastSendTime = now;
                        any = true;
                    }
                }
                if (any) dev.Commit();
                dev.LastUpdate = now;
                if (_errors.Remove(dev)) UpdateStatus();
            }
            catch (Exception ex)
            {
                int n = _errors.TryGetValue(dev, out var e) ? e + 1 : 1;
                _errors[dev] = n;
                dev.LastUpdate = now.AddSeconds(n); // kurz warten, dann erneut versuchen
                if (n == 1 || n == 5) Log.Write($"RGB: {dev.Name} Fehler ({n}): {ex.Message}");
                if (n == 5) UpdateStatus();
            }
        }
    }

    private static bool UsesScreen(EngineState st)
    {
        if (st.Global.Type == LightEffect.Screen) return true;
        if (st.Sync) return false;
        return st.Zones.Any(z => z.Fans.Any(f => !f.FollowSync && f.Own?.Type == LightEffect.Screen));
    }

    private static RgbF[] SmoothArr(ref RgbF[]? state, RgbF[] next, double smooth)
    {
        if (state == null || state.Length != next.Length) { state = next; return next; }
        var res = new RgbF[next.Length];
        double a = 1 - smooth;
        for (int i = 0; i < next.Length; i++) res[i] = RgbF.Lerp(state[i], next[i], a);
        state = res;
        return res;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(800); } catch { }
        _screen.Dispose();
        foreach (var d in _devices) { try { d.Dispose(); } catch { } }
    }
}
