using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NickFanControl;

// ============================================================================ Farbe

public sealed class ColorItem : ObservableObject
{
    public EffectVM Owner { get; }
    private double _h, _s, _v;

    public ColorItem(EffectVM owner, string hex)
    {
        Owner = owner;
        RgbF.FromHex(hex).ToHsv(out _h, out _s, out _v);
        if (_v <= 0) _v = 1;
    }

    public string Hex
    {
        get
        {
            var c = RgbF.FromHsv(_h, _s, _v);
            return $"#{c.R8:X2}{c.G8:X2}{c.B8:X2}";
        }
        set
        {
            var t = value?.Trim() ?? "";
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length != 7 || !int.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out _)) return;
            RgbF.FromHex(t).ToHsv(out var h, out var s, out var v);
            if (s > 0) _h = h;
            _s = s; _v = v;
            Changed();
        }
    }

    /// <summary>Farbton-Regler: immer satte, volle Farbe (keine blassen Mischtöne).</summary>
    public double Hue { get => _h; set { _h = Math.Clamp(value, 0, 360); _s = 1; _v = 1; Changed(); } }
    public double Sat { get => _s * 100; set { _s = Math.Clamp(value / 100, 0, 1); Changed(); } }
    public double Val { get => _v * 100; set { _v = Math.Clamp(value / 100, 0, 1); Changed(); } }

    public Brush Brush => Frozen(RgbF.FromHsv(_h, _s, _v));
    public Brush SatTrack => Gradient(RgbF.FromHsv(_h, 0, _v), RgbF.FromHsv(_h, 1, _v));
    public Brush ValTrack => Gradient(RgbF.Black, RgbF.FromHsv(_h, _s, 1));

    private void Changed()
    {
        Raise(nameof(Hex)); Raise(nameof(Hue)); Raise(nameof(Sat)); Raise(nameof(Val));
        Raise(nameof(Brush)); Raise(nameof(SatTrack)); Raise(nameof(ValTrack));
        Owner.ColorsChanged();
    }

    public static Brush Frozen(RgbF c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R8, c.G8, c.B8));
        b.Freeze();
        return b;
    }

    private static Brush Gradient(RgbF a, RgbF b)
    {
        var g = new LinearGradientBrush(Color.FromRgb(a.R8, a.G8, a.B8), Color.FromRgb(b.R8, b.G8, b.B8), 0);
        g.Freeze();
        return g;
    }
}

// ============================================================================ Effekt

public sealed class EffectVM : ObservableObject
{
    public static string[] RegionNames { get; } = { "Ganzer Bildschirm", "Linke Seite", "Rechte Seite", "Oben", "Unten", "Mitte" };

    private static readonly Dictionary<string, string> OldToSaturated = new()
    {
        ["#EF4444"] = "#FF0000", ["#F97316"] = "#FF4000", ["#F59E0B"] = "#FFB000", ["#EAB308"] = "#FFB000",
        ["#22C55E"] = "#00FF00", ["#14B8A6"] = "#00FF80", ["#38BDF8"] = "#00FFFF", ["#3B82F6"] = "#0080FF",
        ["#6366F1"] = "#0000FF", ["#8B5CF6"] = "#8000FF", ["#EC4899"] = "#FF0060"
    };

    private readonly EffectConfig _c;
    private readonly Action _changed;
    public ObservableCollection<ColorItem> Colors { get; } = new();

    public EffectVM(EffectConfig c, Action changed)
    {
        _c = c;
        _changed = changed;
        if (_c.Colors.Count == 0) _c.Colors.Add("#FF0000");
        // alte, blasse Standardfarben durch satte LED-Farben ersetzen
        for (int i = 0; i < _c.Colors.Count; i++)
            if (OldToSaturated.TryGetValue(_c.Colors[i].ToUpperInvariant(), out var sat)) _c.Colors[i] = sat;
        foreach (var h in _c.Colors) Colors.Add(new ColorItem(this, h));
    }

    public EffectConfig Config => _c;
    public EffectSnap Snap() => EffectSnap.From(_c);

    private void Changed(params string[] props)
    {
        foreach (var p in props) Raise(p);
        _changed();
    }

    // ---- Typ
    private void SetType(LightEffect t)
    {
        if (_c.Type == t) return;
        _c.Type = t;
        Changed(nameof(IsStatic), nameof(IsSlideshow), nameof(IsRainbow), nameof(IsBreathing), nameof(IsScreen), nameof(IsOff),
                nameof(ShowColors), nameof(ShowAddColor), nameof(ShowSeconds), nameof(SecondsLabel), nameof(ShowFade),
                nameof(ShowRainbow), nameof(ShowScreen), nameof(Description), nameof(VisibleColors));
    }

    public bool IsStatic { get => _c.Type == LightEffect.Static; set { if (value) SetType(LightEffect.Static); } }
    public bool IsSlideshow { get => _c.Type == LightEffect.Slideshow; set { if (value) SetType(LightEffect.Slideshow); } }
    public bool IsRainbow { get => _c.Type == LightEffect.Rainbow; set { if (value) SetType(LightEffect.Rainbow); } }
    public bool IsBreathing { get => _c.Type == LightEffect.Breathing; set { if (value) SetType(LightEffect.Breathing); } }
    public bool IsScreen { get => _c.Type == LightEffect.Screen; set { if (value) SetType(LightEffect.Screen); } }
    public bool IsOff { get => _c.Type == LightEffect.Off; set { if (value) SetType(LightEffect.Off); } }

    public bool ShowColors => _c.Type is LightEffect.Static or LightEffect.Slideshow or LightEffect.Breathing;
    public bool ShowAddColor => _c.Type is LightEffect.Slideshow or LightEffect.Breathing;
    public bool ShowSeconds => _c.Type is LightEffect.Slideshow or LightEffect.Breathing;
    public bool ShowFade => _c.Type == LightEffect.Slideshow;
    public bool ShowRainbow => _c.Type == LightEffect.Rainbow;
    public bool ShowScreen => _c.Type == LightEffect.Screen;
    public string SecondsLabel => _c.Type == LightEffect.Breathing ? "Atemzug" : "Pro Farbe";

    /// <summary>Bei "Statisch" nur die erste Farbe zeigen.</summary>
    public IEnumerable<ColorItem> VisibleColors => _c.Type == LightEffect.Static ? Colors.Take(1) : Colors;

    public string Description => _c.Type switch
    {
        LightEffect.Static => "Eine feste Farbe.",
        LightEffect.Slideshow => "Wechselt nacheinander durch deine Farben – wie eine Diashow.",
        LightEffect.Rainbow => "Läuft durch alle Farben des Regenbogens.",
        LightEffect.Breathing => "Pulsiert langsam – bei mehreren Farben abwechselnd.",
        LightEffect.Screen => "Übernimmt die Farben von dem, was gerade auf dem Bildschirm läuft.",
        _ => "Beleuchtung aus."
    };

    // ---- Farben
    public void AddColor()
    {
        string[] presets = { "#FF0000", "#0000FF", "#00FF00", "#FF00FF", "#00FFFF", "#FFB000", "#FFFFFF" };
        var hex = presets.FirstOrDefault(p => Colors.All(c => !c.Hex.Equals(p, StringComparison.OrdinalIgnoreCase))) ?? "#FFFFFF";
        Colors.Add(new ColorItem(this, hex));
        ColorsChanged();
    }

    public void RemoveColor(ColorItem item)
    {
        if (Colors.Count <= 1) return;
        Colors.Remove(item);
        ColorsChanged();
    }

    public bool CanRemove => Colors.Count > 1 && ShowAddColor;

    public void ColorsChanged()
    {
        _c.Colors = Colors.Select(c => c.Hex).ToList();
        Changed(nameof(VisibleColors), nameof(CanRemove));
    }

    // ---- Parameter
    public double Seconds { get => _c.Seconds; set { _c.Seconds = Math.Round(Math.Clamp(value, 0.5, 60), 1); Changed(nameof(Seconds), nameof(SecondsText)); } }
    public string SecondsText => $"{_c.Seconds:0.#} s";
    public bool Fade { get => _c.Fade; set { _c.Fade = value; Changed(nameof(Fade)); } }
    public double Speed { get => _c.Speed; set { _c.Speed = Math.Round(Math.Clamp(value, 0.1, 5), 1); Changed(nameof(Speed), nameof(SpeedText)); } }
    public string SpeedText => $"{_c.Speed:0.0}×";
    public bool Wave { get => _c.Wave; set { _c.Wave = value; Changed(nameof(Wave)); } }
    public int RegionIndex { get => (int)_c.Region; set { _c.Region = (ScreenRegion)Math.Clamp(value, 0, 5); Changed(nameof(RegionIndex)); } }
    public double Boost { get => _c.Boost * 100; set { _c.Boost = Math.Clamp(value / 100, 0.5, 2.5); Changed(nameof(Boost), nameof(BoostText)); } }
    public string BoostText => $"{_c.Boost * 100:0} %";
    /// <summary>Reaktion: 0 = träge, 100 = sofort (intern: Glättung).</summary>
    public double Reaction { get => (1 - _c.Smooth) * 100; set { _c.Smooth = Math.Clamp(1 - value / 100, 0, 0.97); Changed(nameof(Reaction), nameof(ReactionText)); } }
    public string ReactionText => $"{(1 - _c.Smooth) * 100:0} %";
}

// ============================================================================ Lüfter (Teil einer Zone)

public sealed class LightFanVM : ObservableObject
{
    private readonly RgbController _ctl;
    public LightFanConfig Cfg { get; }
    public int Index { get; }
    public EffectVM Effect { get; }
    public EngineFan? Engine { get; set; }
    private readonly string _defaultName;

    public LightFanVM(RgbController ctl, LightFanConfig cfg, int index, string defaultName)
    {
        _ctl = ctl;
        Cfg = cfg;
        Index = index;
        _defaultName = defaultName;
        Effect = new EffectVM(cfg.Effect, ctl.Changed);
    }

    public string Name
    {
        get => Cfg.Name ?? _defaultName;
        set
        {
            var v = value?.Trim();
            Cfg.Name = string.IsNullOrEmpty(v) || v == _defaultName ? null : v;
            _ctl.Changed();
            Raise();
        }
    }

    public bool OwnEffect
    {
        get => !Cfg.FollowSync;
        set
        {
            if (Cfg.FollowSync == !value) return;
            Cfg.FollowSync = !value;
            // beim ersten Einschalten den aktuellen Gesamteffekt als Startpunkt übernehmen
            Raise();
            Raise(nameof(ShowOwnEditor));
            Raise(nameof(ModeText));
            _ctl.Changed();
        }
    }

    public bool ShowOwnEditor => OwnEffect && !_ctl.Sync;
    /// <summary>"Eigener Effekt" nur anbieten, wenn nicht alles synchronisiert ist.</summary>
    public bool ShowOwnToggle => !_ctl.Sync;
    public string ModeText => _ctl.Sync ? "Wie alle anderen Geräte" : OwnEffect ? "Eigener Effekt" : "Wie alle anderen Geräte";

    private Brush _preview = Brushes.Transparent;
    public Brush Preview { get => _preview; set => Set(ref _preview, value); }

    public void RefreshMode()
    {
        Raise(nameof(ShowOwnEditor));
        Raise(nameof(ShowOwnToggle));
        Raise(nameof(ModeText));
    }
}

// ============================================================================ Zone (Anschluss / Gerät)

public sealed class LightZoneVM : ObservableObject
{
    private readonly RgbController _ctl;
    public ZoneDescriptor Desc { get; }
    public LightZoneConfig Cfg { get; }
    public ObservableCollection<LightFanVM> Fans { get; } = new();

    public LightZoneVM(RgbController ctl, ZoneDescriptor desc, LightZoneConfig cfg, bool isNew)
    {
        _ctl = ctl;
        Desc = desc;
        Cfg = cfg;
        if (isNew) Cfg.FanCount = Math.Max(1, desc.DefaultFans);
        if (!desc.PerLed) Cfg.FanCount = 1;
        Cfg.FanCount = Math.Clamp(Cfg.FanCount, 1, MaxFans);
        if (IsHub) Cfg.LedCount = Cfg.FanCount * desc.LedsPerFan;
        RebuildFans();
    }

    /// <summary>Lüfter-Hub: LED-Anzahl ergibt sich aus der Lüfteranzahl.</summary>
    public bool IsHub => Desc.LedsPerFan > 0;
    public bool ShowLedSlider => Desc.Adjustable && !IsHub;
    public int MaxFans => IsHub ? Math.Max(1, Desc.MaxLeds / Desc.LedsPerFan) : 8;

    public string DefaultName => Desc.ZoneName;
    public string DisplayName
    {
        get => Cfg.Name ?? Desc.ZoneName;
        set
        {
            var v = value?.Trim();
            Cfg.Name = string.IsNullOrEmpty(v) || v == Desc.ZoneName ? null : v;
            _ctl.Changed();
            Raise();
        }
    }

    public string SubTitle => $"{Desc.DeviceName} · {Desc.Kind}";

    // ---- Bildschirm-Effekt: Bereich je Anschluss (wie bei SignalRGB: z. B. linke Lüfter = linke Bildseite)
    public static string[] ScreenRegionChoices { get; } =
        { "Wie oben eingestellt", "Ganzer Bildschirm", "Linke Seite", "Rechte Seite", "Oben", "Unten", "Mitte" };

    public int ScreenRegionIndex
    {
        get => Cfg.ScreenRegion + 1;
        set
        {
            Cfg.ScreenRegion = Math.Clamp(value, 0, 6) - 1;
            Raise();
            _ctl.Changed();
        }
    }

    public bool ShowScreenRegion => _ctl.Global.IsScreen;
    public void RefreshScreen() => Raise(nameof(ShowScreenRegion));

    /// <summary>Hat dieser Anschluss etwas einzurichten (LED- oder Lüfteranzahl)?</summary>
    public bool HasSetup => ShowLedSlider || PerLed;
    private bool _showSetup;
    public bool ShowSetup { get => _showSetup; set => Set(ref _showSetup, value); }
    public bool Adjustable => Desc.Adjustable;
    public bool PerLed => Desc.PerLed;
    public int MaxLeds => Desc.MaxLeds;

    public bool Enabled { get => Cfg.Enabled; set { Cfg.Enabled = value; Raise(); _ctl.Changed(); } }

    public int LedCount => IsHub ? Cfg.FanCount * Desc.LedsPerFan : Cfg.LedCount > 0 ? Cfg.LedCount : Desc.LedCount;
    public double LedCountValue
    {
        get => LedCount;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, 0, Desc.MaxLeds));
            if (v == LedCount) return;
            Cfg.LedCount = v;
            _ctl.SetLedCount(this, v);
            Raise(); Raise(nameof(LedText));
            _ctl.Changed();
        }
    }
    public string LedText => LedCount == 0 ? "aus" : $"{LedCount} LEDs";

    public int FanCount => Cfg.FanCount;
    public string FanCountText => Cfg.FanCount == 1 ? "1 Lüfter" : $"{Cfg.FanCount} Lüfter";

    public void ChangeFanCount(int delta)
    {
        int n = Math.Clamp(Cfg.FanCount + delta, 1, MaxFans);
        if (n == Cfg.FanCount) return;
        Cfg.FanCount = n;
        if (IsHub)
        {
            Cfg.LedCount = n * Desc.LedsPerFan;
            _ctl.SetLedCount(this, Cfg.LedCount);
            Raise(nameof(LedText)); Raise(nameof(LedCountValue));
        }
        RebuildFans();
        Raise(nameof(FanCount)); Raise(nameof(FanCountText));
        _ctl.Changed();
    }

    private void RebuildFans()
    {
        while (Cfg.Fans.Count < Cfg.FanCount) Cfg.Fans.Add(new LightFanConfig());
        Fans.Clear();
        for (int i = 0; i < Cfg.FanCount; i++)
        {
            string def = Cfg.FanCount == 1 ? (Desc.PerLed ? "Lüfter / Streifen" : "Beleuchtung") : $"Lüfter {i + 1}";
            Fans.Add(new LightFanVM(_ctl, Cfg.Fans[i], i, def));
        }
    }
}

// ============================================================================ Steuerung der Beleuchtung

public sealed class RgbController : ObservableObject, IDisposable
{
    private readonly AppConfig _cfg;
    private readonly RgbEngine _engine;
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    public EffectVM Global { get; }
    public ObservableCollection<LightZoneVM> Zones { get; } = new();
    public ObservableCollection<DetectedHardware> Hardware { get; } = new();
    public bool PreviewActive { get; set; }

    public RgbController(AppConfig cfg)
    {
        _cfg = cfg;
        _cfg.Rgb.Sync = true; // alles synchron
        Global = new EffectVM(cfg.Rgb.Global, Changed);
        Global.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EffectVM.IsScreen))
                foreach (var z in Zones) z.RefreshScreen();
        };
        _engine = new RgbEngine(SynchronizationContext.Current ?? new DispatcherSynchronizationContext());
        _engine.DevicesChanged += OnDevices;
        _engine.StatusChanged += (text, ok) => { StatusText = text; StatusBrush = ok ? Palette.Good : Palette.Warn; };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        _preview.Tick += (_, _) => UpdatePreview();
        _preview.Start();
        _conflictTimer.Tick += (_, _) => CheckConflicts();
        _conflictTimer.Start();
        CheckConflicts();
        Push();
    }

    // ---- Eigenschaften
    public bool Enabled
    {
        get => _cfg.Rgb.Enabled;
        set { _cfg.Rgb.Enabled = value; Raise(); Changed(); }
    }

    public bool Sync
    {
        get => _cfg.Rgb.Sync;
        set
        {
            _cfg.Rgb.Sync = value;
            Raise();
            foreach (var z in Zones) foreach (var f in z.Fans) f.RefreshMode();
            Changed();
        }
    }

    public double Brightness
    {
        get => _cfg.Rgb.Brightness * 100;
        set { _cfg.Rgb.Brightness = Math.Clamp(value / 100, 0, 1); Raise(); Raise(nameof(BrightnessText)); Changed(); }
    }
    public string BrightnessText => $"{_cfg.Rgb.Brightness * 100:0} %";

    private string _statusText = "Suche Geräte …";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private Brush _statusBrush = Palette.Muted;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    private Brush _globalPreview = Brushes.Transparent;
    public Brush GlobalPreview { get => _globalPreview; private set => Set(ref _globalPreview, value); }

    // ---- Live-Vorschau der Bildschirmfarben
    private Brush _sAll = Brushes.Black, _sL = Brushes.Black, _sR = Brushes.Black, _sT = Brushes.Black, _sB = Brushes.Black, _sC = Brushes.Black;
    public Brush ScreenAll { get => _sAll; private set => Set(ref _sAll, value); }
    public Brush ScreenLeft { get => _sL; private set => Set(ref _sL, value); }
    public Brush ScreenRight { get => _sR; private set => Set(ref _sR, value); }
    public Brush ScreenTop { get => _sT; private set => Set(ref _sT, value); }
    public Brush ScreenBottom { get => _sB; private set => Set(ref _sB, value); }
    public Brush ScreenCenter { get => _sC; private set => Set(ref _sC, value); }

    public bool HasZones => Zones.Count > 0;
    public bool NoZones => Zones.Count == 0;

    // ---- Andere RGB-Programme erkennen (häufigste Ursache für Flackern)
    private readonly DispatcherTimer _conflictTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly (string process, string name)[] KnownRgbApps =
    {
        ("ArmouryCrate", "ASUS Armoury Crate"), ("ArmouryCrate.Service", "ASUS Armoury Crate"), ("LightingService", "ASUS Aura"),
        ("AuraService", "ASUS Aura"), ("MSI.CentralServer", "MSI Center"), ("MSI_Center", "MSI Center"), ("MysticLight", "MSI Mystic Light"),
        ("LEDKeeper2", "MSI Mystic Light"), ("RGBFusion", "Gigabyte RGB Fusion"), ("GCC", "Gigabyte Control Center"),
        ("GigabyteUpdateService", "Gigabyte Control Center"), ("iCUE", "Corsair iCUE"), ("Corsair.Service", "Corsair iCUE"),
        ("RazerCentralService", "Razer Synapse"), ("Razer Synapse 3", "Razer Synapse"), ("RazerAppEngine", "Razer Synapse"),
        ("SignalRgb", "SignalRGB"), ("SignalRgbLauncher", "SignalRGB"), ("OpenRGB", "OpenRGB"), ("NZXT CAM", "NZXT CAM"),
        ("L-Connect 3", "Lian Li L-Connect"), ("L-Connect", "Lian Li L-Connect"), ("PolychromeRGB", "ASRock Polychrome"),
        ("lghub", "Logitech G Hub"), ("SteelSeriesGG", "SteelSeries GG"), ("FanControl", "FanControl (Rem0o)")
    };

    private string _conflictText = "";
    public string ConflictText { get => _conflictText; private set { if (Set(ref _conflictText, value)) Raise(nameof(HasConflict)); } }
    public bool HasConflict => _conflictText.Length > 0;

    private void CheckConflicts()
    {
        try
        {
            var running = KnownRgbApps.Where(a => System.Diagnostics.Process.GetProcessesByName(a.process).Length > 0)
                                      .Select(a => a.name).Distinct().ToList();
            ConflictText = running.Count == 0 ? "" :
                $"Läuft gleichzeitig: {string.Join(", ", running)}. Zwei RGB-Programme überschreiben sich gegenseitig – " +
                "das führt zu Flackern. Bitte dieses Programm beenden (oder im Autostart deaktivieren).";
        }
        catch { }
    }

    // ---- Änderungen
    public void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Push();
    }

    public void SetLedCount(LightZoneVM z, int count) => _engine.SetLedCount(z.Desc.Zone, count);

    public void Rescan() => _engine.Rescan();

    public void OpenHardwareList()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            var p = Path.Combine(AppConfig.Folder, "hardware.txt");
            File.WriteAllText(p, _engine.HardwareReportText);
            FanController.OpenUrl(p);
        }
        catch (Exception ex) { Log.Write("Hardware-Liste: " + ex.Message); }
    }

    private void OnDevices(List<ZoneDescriptor> zones, List<DetectedHardware> report)
    {
        Zones.Clear();
        foreach (var d in zones)
        {
            bool isNew = false;
            if (!_cfg.Rgb.Zones.TryGetValue(d.Key, out var zc))
            {
                zc = new LightZoneConfig();
                _cfg.Rgb.Zones[d.Key] = zc;
                isNew = true;
            }
            // Einfach & synchron: ein Effekt pro Anschluss und immer alle möglichen LEDs ansteuern –
            // so bekommen auch hintereinander gesteckte Lüfter die Farbe (sonst bleibt der Rest alt).
            zc.Fans.RemoveRange(Math.Min(1, zc.Fans.Count), Math.Max(0, zc.Fans.Count - 1));
            if (zc.Fans.Count == 1) zc.Fans[0].FollowSync = true;
            if (d.LedsPerFan > 0) zc.FanCount = Math.Max(1, d.MaxLeds / d.LedsPerFan);
            else
            {
                zc.FanCount = 1;
                if (d.Adjustable) zc.LedCount = Math.Min(d.MaxLeds, 60); // reicht für 3–4 Lüfter, schont den Controller
            }
            var vm = new LightZoneVM(this, d, zc, isNew && false);
            if ((d.Adjustable || vm.IsHub) && vm.LedCount != d.LedCount) _engine.SetLedCount(d.Zone, vm.LedCount);
            Zones.Add(vm);
        }
        Hardware.Clear();
        foreach (var h in report) Hardware.Add(h);
        Raise(nameof(HasZones));
        Raise(nameof(NoZones));
        Changed();
    }

    private void Push()
    {
        var zones = new List<EngineZone>();
        foreach (var z in Zones)
        {
            if (z.LedCount <= 0) continue;
            int leds = Math.Max(1, z.LedCount);
            int n = Math.Max(1, z.Fans.Count);
            int per = Math.Max(1, leds / n);
            var fans = new EngineFan[z.Fans.Count];
            for (int i = 0; i < z.Fans.Count; i++)
            {
                var f = z.Fans[i];
                int start = Math.Min(i * per, leds - 1);
                int count = i == z.Fans.Count - 1 ? leds - start : per;
                EffectSnap? screenSnap = null;
                if (z.Cfg.ScreenRegion >= 0 && Global.IsScreen)
                {
                    var c = Global.Config.Clone();
                    c.Region = (ScreenRegion)z.Cfg.ScreenRegion;
                    screenSnap = EffectSnap.From(c);
                }
                var ef = new EngineFan { Start = start, Count = Math.Max(1, count), FollowSync = !f.OwnEffect, Own = f.Effect.Snap(), ScreenSnap = screenSnap };
                if (f.Engine != null) ef.Current = f.Engine.Current; // Vorschau ohne Flackern weiterführen
                f.Engine = ef;
                fans[i] = ef;
            }
            zones.Add(new EngineZone { Target = z.Desc.Zone, LedCount = leds, Enabled = z.Enabled, Fans = fans });
        }
        _engine.SetState(new EngineState
        {
            Enabled = _cfg.Rgb.Enabled,
            Sync = _cfg.Rgb.Sync,
            Brightness = _cfg.Rgb.Brightness,
            Global = Global.Snap(),
            Zones = zones.ToArray()
        });
    }

    // ---- Live-Vorschau
    private void UpdatePreview()
    {
        if (!PreviewActive) return;
        GlobalPreview = MakeBrush(_engine.GlobalPreview);
        if (Global.IsScreen && _engine.ScreenActive)
        {
            var sc = _engine.Screen;
            ScreenAll = ColorItem.Frozen(sc.Get(ScreenRegion.All));
            ScreenLeft = ColorItem.Frozen(sc.Get(ScreenRegion.Left));
            ScreenRight = ColorItem.Frozen(sc.Get(ScreenRegion.Right));
            ScreenTop = ColorItem.Frozen(sc.Get(ScreenRegion.Top));
            ScreenBottom = ColorItem.Frozen(sc.Get(ScreenRegion.Bottom));
            ScreenCenter = ColorItem.Frozen(sc.Get(ScreenRegion.Center));
        }
        foreach (var z in Zones)
            foreach (var f in z.Fans)
                if (f.Engine != null) f.Preview = MakeBrush(f.Engine.Current);
    }

    private Brush MakeBrush(RgbF[] colors)
    {
        if (colors.Length == 0) return Brushes.Transparent;
        double b = Math.Max(0.15, _cfg.Rgb.Enabled ? _cfg.Rgb.Brightness : 1);
        if (colors.Length == 1) return ColorItem.Frozen(colors[0].Scale(b));
        var stops = new GradientStopCollection();
        int steps = Math.Min(colors.Length, 12);
        for (int i = 0; i < steps; i++)
        {
            int idx = (int)Math.Round(i * (colors.Length - 1) / (double)(steps - 1));
            var c = colors[idx].Scale(b);
            stops.Add(new GradientStop(Color.FromRgb(c.R8, c.G8, c.B8), i / (double)(steps - 1)));
        }
        var g = new LinearGradientBrush(stops, 0);
        g.Freeze();
        return g;
    }

    public void Dispose()
    {
        _preview.Stop();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); _cfg.Save(); }
        _engine.Dispose();
    }
}
