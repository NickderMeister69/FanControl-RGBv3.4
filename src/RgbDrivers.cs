using System.IO;

namespace NickFanControl;

/// <summary>Eine ansteuerbare Beleuchtungs-Zone (z. B. ein ARGB-Anschluss am Mainboard, eine Tastatur).</summary>
public sealed class RgbZone
{
    public required RgbDevice Device { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>"ARGB-Anschluss", "RGB-Anschluss", "Mainboard", "Tastatur" …</summary>
    public string Kind { get; init; } = "";
    public int LedCount { get; set; } = 1;
    /// <summary>true, wenn die LED-Anzahl vom Nutzer eingestellt werden kann (ARGB-Anschlüsse).</summary>
    public bool Adjustable { get; init; }
    public int MaxLeds { get; init; } = 1;
    /// <summary>true, wenn jede LED einzeln gefärbt werden kann (sonst eine Farbe für die ganze Zone).</summary>
    public bool PerLed { get; init; }
    /// <summary>&gt;0: feste LED-Zahl pro Lüfter (Lüfter-Hubs) – die LED-Anzahl ergibt sich aus der Lüfteranzahl.</summary>
    public int LedsPerFan { get; init; }
    /// <summary>Vorgeschlagene Lüfteranzahl (z. B. vom Hub erkannt).</summary>
    public int DefaultFans { get; init; } = 1;
    public string Key => Device.Key + "|" + Name;
}

/// <summary>Basis für alle eingebauten RGB-Treiber. Alle Methoden laufen im Effekt-Thread.</summary>
public abstract class RgbDevice : IDisposable
{
    public string Name { get; protected set; } = "";
    public string Vendor { get; protected set; } = "";
    public string Key { get; protected set; } = "";
    public List<RgbZone> Zones { get; } = new();
    /// <summary>Kleinster Abstand zwischen zwei Updates (manche Controller sind langsam).</summary>
    public virtual int MinIntervalMs => 33;
    /// <summary>
    /// Nur Geräte, die ohne regelmäßige Befehle zurück in ihren eigenen Modus fallen, brauchen ein
    /// "Wach-halten". Alle anderen bekommen nur bei Farbänderung neue Daten – sonst flackern manche Boards.
    /// </summary>
    public virtual double KeepAliveSeconds => 0;
    public DateTime LastUpdate;

    /// <summary>Gerät für Software-Steuerung vorbereiten (Direct-Modus).</summary>
    public abstract void Init();
    /// <summary>Farben einer Zone setzen. rgb = 3 Bytes pro LED.</summary>
    public abstract void SetZone(RgbZone zone, byte[] rgb);
    /// <summary>Nach allen Zonen eines Frames aufgerufen (z. B. zum Übernehmen).</summary>
    public virtual void Commit() { }
    public virtual void ApplyLedCount(RgbZone zone) { }
    public abstract void Dispose();

    protected static RgbF Avg(byte[] rgb)
    {
        int n = rgb.Length / 3;
        if (n == 0) return RgbF.Black;
        double r = 0, g = 0, b = 0;
        for (int i = 0; i < n; i++) { r += rgb[i * 3]; g += rgb[i * 3 + 1]; b += rgb[i * 3 + 2]; }
        return new RgbF(r / n / 255, g / n / 255, b / n / 255);
    }
}

// ============================================================================ ASUS Aura (USB)

/// <summary>ASUS Aura Mainboard-/ARGB-Controller (USB 0B05). Echte Einzel-LED-Steuerung.</summary>
public sealed class AsusAuraDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly List<(byte effectChannel, byte directChannel)> _ch = new();
    private string _fw = "";

    public AsusAuraDevice(HidDevice hid)
    {
        _hid = hid;
        Vendor = "ASUS";
        Name = "ASUS Aura Mainboard";
        Key = $"asus:{hid.Info.Pid:X4}";
    }

    public override void Init()
    {
        // Firmware lesen
        _hid.Write(new byte[] { 0xEC, 0x82 });
        var r = _hid.Read();
        if (r != null && r[1] == 0x02) _fw = System.Text.Encoding.ASCII.GetString(r, 2, 16).TrimEnd('\0', ' ');

        // Konfigurationstabelle
        _hid.Write(new byte[] { 0xEC, 0xB0 });
        r = _hid.Read();
        if (r == null || r[1] != 0x30) throw new IOException("ASUS Aura: Konfiguration nicht lesbar");
        var cfg = new byte[60];
        Array.Copy(r, 4, cfg, 0, 60);

        int mbLeds = cfg[0x1B], rgbHeaders = cfg[0x1D], argbHeaders = cfg[0x02];
        if (mbLeds < rgbHeaders) rgbHeaders = 0;
        bool fromZero = _fw.StartsWith("AULA3-AR32", StringComparison.Ordinal);
        byte effect = 0;

        if (mbLeds > 0)
        {
            byte mbEffect = fromZero ? (byte)argbHeaders : effect;
            _ch.Add((mbEffect, 0x04));
            Zones.Add(new RgbZone { Device = this, Index = Zones.Count, Name = "Mainboard-LEDs", Kind = "Mainboard", LedCount = mbLeds, MaxLeds = mbLeds, PerLed = true });
            if (!fromZero) effect++;
        }
        for (int i = 0; i < argbHeaders; i++)
        {
            _ch.Add((effect, (byte)i));
            Zones.Add(new RgbZone
            {
                Device = this, Index = Zones.Count, Name = $"ARGB-Anschluss {i + 1}", Kind = "ARGB-Anschluss",
                LedCount = 24, Adjustable = true, MaxLeds = 120, PerLed = true
            });
            effect++;
        }

        // "Gen1"-Befehl und Direct-Modus je Kanal
        _hid.Write(new byte[] { 0xEC, 0x52, 0x53, 0x00, 0x01 });
        foreach (var (eff, _) in _ch)
            _hid.Write(new byte[] { 0xEC, 0x35, eff, 0x00, 0x00, 0xFF });
        if (!string.IsNullOrEmpty(_fw)) Name = $"ASUS Aura ({_fw})";
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        byte channel = _ch[zone.Index].directChannel;
        int count = rgb.Length / 3;
        int done = 0, offset = 0;
        byte cmd = channel;
        while (done < count)
        {
            int inPkt = Math.Min(20, count - done);
            bool last = done + inPkt >= count;
            var buf = new byte[65];
            buf[0] = 0xEC;
            buf[1] = 0x40;
            buf[2] = (byte)(last ? cmd | 0x80 : cmd);
            buf[3] = (byte)offset;
            buf[4] = (byte)inPkt;
            Array.Copy(rgb, done * 3, buf, 5, inPkt * 3);
            _hid.Write(buf);
            done += inPkt;
            offset += inPkt;
            if (done > 255) { cmd |= 0x10; offset = done - 256; }
        }
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ Gigabyte RGB Fusion 2 (USB)

/// <summary>
/// Gigabyte/Aorus RGB Fusion 2 (ITE-Controller 048D). ARGB-Anschlüsse (D_LED) mit Einzel-LED-Steuerung
/// und 12-V-RGB-Anschlüsse (LED_C) bzw. feste Mainboard-LEDs mit einer Farbe.
/// Die Belegung kommt aus einer Tabelle je Mainboard-Modell.
/// </summary>
public sealed class GigabyteFusionDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly ushort _pid;
    private readonly string _board;
    private readonly List<GigabyteBoards.Zone> _map = new();
    private readonly Dictionary<int, int[]> _order = new(); // Header → Position von R, G, B
    private uint _effectMask;
    private bool _effectsChanged;
    public override int MinIntervalMs => 50;

    public GigabyteFusionDevice(HidDevice hid, string board)
    {
        _hid = hid;
        _pid = hid.Info.Pid;
        _board = board.Trim();
        Vendor = "Gigabyte";
        Name = string.IsNullOrEmpty(_board) ? "Gigabyte RGB Fusion 2" : "Gigabyte " + _board;
        Key = $"gigabyte:{_pid:X4}";
    }

    private void Cc(byte a, byte b = 0, byte c = 0) => _hid.SetFeature(new byte[] { 0xCC, a, b, c });

    private static int[] Order(uint cal)
    {
        int bb = (int)(cal & 0xFF), gg = (int)((cal >> 8) & 0xFF), rr = (int)((cal >> 16) & 0xFF);
        bool valid = rr <= 2 && gg <= 2 && bb <= 2 && rr != gg && gg != bb && rr != bb;
        return valid ? new[] { rr, gg, bb } : new[] { 1, 0, 2 }; // Standard: GRB
    }

    public override void Init()
    {
        Cc(0x60);
        var rep = _hid.GetFeature(0xCC, 64) ?? throw new IOException("Controller antwortet nicht");
        byte supportFlags = rep[11];
        uint[] cal = new uint[6];
        cal[0] = BitConverter.ToUInt32(rep, 44);
        cal[1] = BitConverter.ToUInt32(rep, 48);
        if (_pid == 0x5711)
        {
            Cc(0x61);
            var c2 = _hid.GetFeature(0xCC, 64);
            if (c2 != null) for (int i = 0; i < 4; i++) cal[2 + i] = BitConverter.ToUInt32(c2, 4 + i * 4);
        }
        _order[0x58] = Order(cal[0]); _order[0x59] = Order(cal[1]); _order[0x62] = Order(cal[2]);
        _order[0x63] = Order(cal[3]); _order[0x64] = Order(cal[4]); _order[0x65] = Order(cal[5]);

        if ((supportFlags & 0x02) != 0) Cc(0x48, 0);  // LampArray aus
        for (byte reg = 0x20; reg <= 0x27; reg++) Cc(reg);
        if (_pid == 0x5711) for (byte reg = 0x90; reg <= 0x92; reg++) Cc(reg);
        Cc(0x28, 0xFF, (byte)(_pid == 0x5711 ? 0x07 : 0x00));
        Cc(0x31, 0); // Beat aus

        // Belegung des Mainboards (oder allgemeiner Standard)
        if (GigabyteBoards.Boards.TryGetValue(_board, out var zones)) _map.AddRange(zones);
        else
        {
            _map.Add(new("D_LED1", true, 0x58));
            _map.Add(new("D_LED2", true, 0x59));
            if (_pid == 0x5711) { _map.Add(new("D_LED3", true, 0x62)); _map.Add(new("D_LED4", true, 0x63)); }
            Log.Write($"Gigabyte: Board \"{_board}\" nicht in der Tabelle – nutze Standard-Belegung.");
        }

        int mask = 0;
        foreach (var m in _map)
        {
            if (m.Strip)
            {
                mask |= m.Code switch { 0x59 => 0x02, 0x62 => 0x08, 0x63 => 0x10, 0x64 => 0x20, 0x65 => 0x40, _ => 0x01 };
                Zones.Add(new RgbZone
                {
                    Device = this, Index = Zones.Count, Name = $"{m.Name} (ARGB)", Kind = "ARGB-Anschluss",
                    LedCount = 24, Adjustable = true, MaxLeds = 120, PerLed = true
                });
            }
            else
            {
                bool header = m.Name.StartsWith("LED_C", StringComparison.OrdinalIgnoreCase);
                Zones.Add(new RgbZone
                {
                    Device = this, Index = Zones.Count, Name = header ? $"{m.Name} (12 V RGB)" : m.Name,
                    Kind = header ? "RGB-Anschluss (12 V)" : "Mainboard-LED", LedCount = 1, MaxLeds = 1
                });
            }
        }
        if (Zones.Count > 0 && _map.Any(m => m.Strip)) ApplyLedCount(Zones.First(z => z.Adjustable));

        // Eingebaute Effekte der ARGB-Anschlüsse aus → Software-Farben
        Cc(0x32, (byte)mask);
        Thread.Sleep(50);
    }

    private static byte CountEnum(int c) => c <= 32 ? (byte)0 : c <= 64 ? (byte)1 : c <= 256 ? (byte)2 : c <= 512 ? (byte)3 : (byte)4;

    public override void ApplyLedCount(RgbZone zone)
    {
        // Reihenfolge der Zähler: D_LED1, D_LED2, D_LED3, D_LED4, Onboard1, Onboard2
        int[] headers = { 0x58, 0x59, 0x62, 0x63, 0x64, 0x65 };
        var e = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            int zi = _map.FindIndex(m => m.Strip && m.Code == headers[i]);
            e[i] = zi >= 0 ? CountEnum(Zones[zi].LedCount) : (byte)0;
        }
        _hid.SetFeature(new byte[] { 0xCC, 0x34, (byte)(e[1] << 4 | e[0]), (byte)(e[3] << 4 | e[2]), (byte)(e[5] << 4 | e[4]) });
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        var m = _map[zone.Index];
        if (m.Strip) SendStrip((byte)m.Code, rgb);
        else SendSingle(m.Code, Avg(rgb));
    }

    private void SendStrip(byte hdr, byte[] rgb)
    {
        var ord = _order.TryGetValue(hdr, out var o) ? o : new[] { 1, 0, 2 };
        int count = rgb.Length / 3, k = 0, sent = 0;
        while (k < count)
        {
            int n = Math.Min(19, count - k);
            var buf = new byte[64];
            buf[0] = 0xCC; buf[1] = hdr;
            buf[2] = (byte)(sent & 0xFF); buf[3] = (byte)(sent >> 8);
            buf[4] = (byte)(n * 3);
            for (int i = 0; i < n; i++)
            {
                int p = 5 + i * 3;
                buf[p + ord[0]] = rgb[(k + i) * 3];
                buf[p + ord[1]] = rgb[(k + i) * 3 + 1];
                buf[p + ord[2]] = rgb[(k + i) * 3 + 2];
            }
            _hid.SetFeature(buf);
            sent += n * 3;
            k += n;
        }
    }

    /// <summary>12-V-RGB / feste LED: statischer Effekt mit einer Farbe.</summary>
    private void SendSingle(int led, RgbF c)
    {
        var b = new byte[64];
        b[0] = 0xCC;
        uint zone;
        if (led < 8) { b[1] = (byte)(0x20 + led); zone = 1u << led; }
        else { b[1] = (byte)(0x90 + led - 8); zone = 1u << led; }
        BitConverter.GetBytes(zone).CopyTo(b, 2);
        b[11] = 1;      // statisch
        b[12] = 255;    // max. Helligkeit
        b[13] = 0;
        b[14] = c.B8; b[15] = c.G8; b[16] = c.R8; // Farbe als BGR
        _hid.SetFeature(b);
        _effectMask |= zone;
        _effectsChanged = true;
    }

    public override void Commit()
    {
        var b = new byte[64];
        b[0] = 0xCC; b[1] = 0x28;
        if (_effectsChanged) BitConverter.GetBytes(_effectMask).CopyTo(b, 2);
        _hid.SetFeature(b);
        _effectsChanged = false;
        _effectMask = 0;
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ ASRock Polychrome (USB)

/// <summary>ASRock Polychrome USB (26CE:01A2). Eine Farbe pro Anschluss.</summary>
public sealed class AsrockPolychromeDevice : RgbDevice
{
    private static readonly string[] ZoneNames =
        { "RGB-Anschluss 1", "RGB-Anschluss 2", "ARGB-Anschluss 1", "ARGB-Anschluss 2", "PCH", "I/O-Abdeckung", "Platine", "ARGB-Anschluss 3" };

    private readonly HidDevice _hid;
    private readonly List<(byte type, bool rgSwap)> _z = new();
    public override int MinIntervalMs => 80;

    public AsrockPolychromeDevice(HidDevice hid)
    {
        _hid = hid;
        Vendor = "ASRock";
        Name = "ASRock Polychrome";
        Key = $"asrock:{hid.Info.Pid:X4}";
    }

    private byte[] Request(byte cmd, byte arg)
    {
        var buf = new byte[65];
        buf[1] = cmd;
        buf[3] = arg;
        _hid.Write(buf);
        return _hid.Read() ?? new byte[65];
    }

    public override void Init()
    {
        var counts = Request(0x14, 0x02);
        var swap = Request(0x14, 0x03);
        var avail = Request(0x14, 0x01);
        byte rgswap = swap.Length > 4 ? swap[4] : (byte)0;
        byte present = avail.Length > 4 ? avail[4] : (byte)0xFF;

        for (int z = 0; z < 8; z++)
        {
            byte cnt = counts.Length > 4 + z ? counts[4 + z] : (byte)0x1E;
            if (cnt == 0x1E || ((present >> z) & 1) == 0) continue;
            _z.Add(((byte)z, ((rgswap >> z) & 1) == 1));
            Zones.Add(new RgbZone
            {
                Device = this, Index = Zones.Count, Name = ZoneNames[z],
                Kind = z is 2 or 3 or 7 ? "ARGB-Anschluss" : z is 0 or 1 ? "RGB-Anschluss" : "Mainboard",
                LedCount = 1, MaxLeds = 1
            });
        }
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        var c = Avg(rgb);
        var (type, rg) = _z[zone.Index];
        var buf = new byte[65];
        buf[1] = 0x10;           // Zone setzen
        buf[3] = type;
        buf[4] = 0x01;           // Static
        buf[5] = rg ? c.R8 : c.G8;
        buf[6] = rg ? c.G8 : c.R8;
        buf[7] = c.B8;
        buf[8] = 0xE0;           // Geschwindigkeit
        buf[9] = 0xFF;
        _hid.Write(buf);
        _hid.Read(100);
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ Erkennung

public sealed record DetectedHardware(string Vendor, string Name, string Status, bool Supported);

public static class RgbScanner
{
    private static readonly HashSet<ushort> AsusPids = new() { 0x1867, 0x1872, 0x18A3, 0x18A5, 0x18F3, 0x1939, 0x19AF, 0x1AA6, 0x1BED };
    private static readonly HashSet<ushort> GigabytePids = new() { 0x8297, 0x8950, 0x5702, 0x5711 };

    private static readonly Dictionary<ushort, (string name, int ch)> CorsairNode = new()
    {
        [0x0C1A] = ("Corsair Lighting Node Core", 1), [0x0C0B] = ("Corsair Lighting Node Pro", 2),
        [0x0C10] = ("Corsair Commander Pro", 2), [0x0C1E] = ("Corsair LS100", 1), [0x1D00] = ("Corsair 1000D Obsidian", 2),
        [0x1D04] = ("Corsair SPEC OMEGA RGB", 2), [0x0C23] = ("Corsair LT100", 2)
    };
    private static readonly HashSet<ushort> CommanderCore = new() { 0x0C1C, 0x0C32, 0x0C1D, 0x0C3C, 0x0C3D, 0x0C3E, 0x0C2A };

    private static readonly Dictionary<ushort, (string name, int ch)> Nzxt = new()
    {
        [0x2001] = ("NZXT Hue 2", 4), [0x2002] = ("NZXT Hue 2 Ambient", 2), [0x2005] = ("NZXT Mainboard-RGB", 2), [0x200B] = ("NZXT Mainboard-RGB", 2),
        [0x2006] = ("NZXT Smart Device V2", 2), [0x200D] = ("NZXT Smart Device V2", 2), [0x200F] = ("NZXT Smart Device V2", 2),
        [0x2007] = ("NZXT Kraken X3", 3), [0x2014] = ("NZXT Kraken X3", 3), [0x3012] = ("NZXT Kraken Elite", 2),
        [0x2009] = ("NZXT RGB & Fan Controller", 2), [0x2010] = ("NZXT RGB & Fan Controller", 2), [0x200E] = ("NZXT RGB & Fan Controller", 2),
        [0x2011] = ("NZXT RGB & Fan Controller", 6), [0x2019] = ("NZXT RGB & Fan Controller", 6), [0x2020] = ("NZXT RGB & Fan Controller", 6),
        [0x201F] = ("NZXT RGB & Fan Controller", 6), [0x2022] = ("NZXT RGB & Fan Controller 2024", 6), [0x201B] = ("NZXT B650E Mainboard", 6),
        [0x2012] = ("NZXT RGB Controller", 3), [0x2021] = ("NZXT RGB Controller", 3)
    };

    private static readonly Dictionary<ushort, LianLiKind> LianLi = new()
    {
        [0xA100] = LianLiKind.SL, [0xA101] = LianLiKind.AL, [0xA102] = LianLiKind.SLInfinity,
        [0xA103] = LianLiKind.SLV2, [0xA104] = LianLiKind.SLV2, [0xA105] = LianLiKind.SLV2
    };

    /// <summary>Bekannte Hersteller/Geräte, die (noch) nicht eingebaut sind.</summary>
    private static readonly Dictionary<ushort, string> OtherVendors = new()
    {
        [0x1B1C] = "Corsair", [0x0CF2] = "Lian Li", [0x1E71] = "NZXT", [0x264A] = "Thermaltake",
        [0x2516] = "Cooler Master", [0x1044] = "Gigabyte (Peripherie)", [0x0951] = "HyperX", [0x03F0] = "HyperX", [0x046D] = "Logitech",
        [0x1038] = "SteelSeries", [0x3633] = "Deepcool", [0x0416] = "Lian Li / Winbond", [0x2F7C] = "Arctic", [0x1462] = "MSI", [0x0B05] = "ASUS"
    };

    public static (List<RgbDevice> devices, List<DetectedHardware> report, List<HidInfo> all) Scan()
    {
        var devices = new List<RgbDevice>();
        var report = new List<DetectedHardware>();
        List<HidInfo> all;
        try { all = HidDevice.Enumerate(); }
        catch (Exception ex)
        {
            Log.Write("HID-Aufzählung fehlgeschlagen: " + ex.Message);
            return (devices, report, new List<HidInfo>());
        }

        var handled = new HashSet<(ushort, ushort)>();

        void Try(string vendor, string name, ushort vid, ushort pid, Func<RgbDevice> create)
        {
            handled.Add((vid, pid));
            RgbDevice? dev = null;
            try
            {
                dev = create();
                dev.Init();
                if (dev.Zones.Count == 0) throw new IOException("keine Beleuchtung angeschlossen");
                devices.Add(dev);
                report.Add(new(vendor, dev.Name, "Wird gesteuert", true));
                Log.Write($"RGB: {dev.Name} erkannt ({dev.Zones.Count} Zonen)");
            }
            catch (Exception ex)
            {
                dev?.Dispose();
                report.Add(new(vendor, name, "Fehler: " + ex.Message, false));
                Log.Write($"RGB: {name} ({vid:X4}:{pid:X4}) fehlgeschlagen: {ex.Message}");
            }
        }

        static HidInfo? Pick(IEnumerable<HidInfo> list, int iface, int usagePage, int usage, Func<HidInfo, bool> ok) =>
            list.Where(ok)
                .OrderByDescending(h => (iface < 0 || h.Interface == iface) ? 2 : 0)
                .ThenByDescending(h => (usagePage < 0 || h.UsagePage == usagePage) && (usage < 0 || h.Usage == usage) ? 1 : 0)
                .FirstOrDefault();

        // ------------------------------------------------------------------ Razer
        foreach (var g in all.Where(h => h.Vid == 0x1532).GroupBy(h => h.Pid))
        {
            handled.Add((0x1532, g.Key));
            if (!RazerDeviceTable.Devices.TryGetValue(g.Key, out var e))
            {
                report.Add(new("Razer", NiceName(g.First(), $"Razer-Gerät {g.Key:X4}"), "Erkannt – Modell noch nicht in der Liste", false));
                continue;
            }
            switch (e.Proto)
            {
                case RazerProto.Argb:
                {
                    var c = g.FirstOrDefault(h => h.Interface == 0 && h.FeatureLen >= 91);
                    var f = g.FirstOrDefault(h => h.Interface == 1 && h.FeatureLen >= 300);
                    if (c == null || f == null) { report.Add(new("Razer", e.Name, "Erkannt – Schnittstellen nicht gefunden", false)); break; }
                    Try("Razer", e.Name, 0x1532, g.Key, () => new RazerArgbDevice(HidDevice.Open(c), HidDevice.Open(f)));
                    break;
                }
                case RazerProto.Kraken1:
                case RazerProto.Kraken3:
                case RazerProto.Kraken4:
                {
                    var h = Pick(g, e.Interface, e.UsagePage, e.Usage, x => x.OutputLen > 0);
                    if (h == null) { report.Add(new("Razer", e.Name, "Erkannt – keine Steuer-Schnittstelle", false)); break; }
                    Try("Razer", e.Name, 0x1532, g.Key, () => new RazerKrakenDevice(HidDevice.Open(h), e));
                    break;
                }
                default:
                {
                    var h = Pick(g, e.Interface, e.UsagePage, e.Usage, x => x.FeatureLen >= 91);
                    if (h == null) { report.Add(new("Razer", e.Name, "Erkannt – keine Steuer-Schnittstelle", false)); break; }
                    Try("Razer", e.Name, 0x1532, g.Key, () => new RazerDevice(HidDevice.Open(h), e));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ ASUS Aura
        foreach (var h in all.Where(h => h.Vid == 0x0B05 && AsusPids.Contains(h.Pid) && h.OutputLen >= 65 && h.UsagePage >= 0xFF00)
                             .GroupBy(h => h.Pid).Select(g => g.First()))
            Try("ASUS", "ASUS Aura", h.Vid, h.Pid, () => new AsusAuraDevice(HidDevice.Open(h)));

        // ------------------------------------------------------------------ Gigabyte RGB Fusion 2
        foreach (var g in all.Where(h => h.Vid == 0x048D && GigabytePids.Contains(h.Pid) && h.UsagePage == 0xFF89).GroupBy(h => h.Pid))
        {
            // Wichtig: die Schnittstelle mit Usage 0xCC (64-Byte-Reports), nicht die 17-Byte-Schnittstelle
            var h = g.FirstOrDefault(x => x.Usage == 0xCC) ?? g.FirstOrDefault(x => x.FeatureLen >= 64);
            if (h != null) Try("Gigabyte", "RGB Fusion 2", h.Vid, h.Pid, () => new GigabyteFusionDevice(HidDevice.Open(h), BoardName()));
        }

        // ------------------------------------------------------------------ MSI Mystic Light
        foreach (var h in all.Where(h => h.Vid == 0x1462 && h.UsagePage == 0x0001 && h.FeatureLen > 0)
                             .GroupBy(h => h.Pid).Select(g => g.OrderByDescending(x => x.FeatureLen).First()))
        {
            int fl = h.FeatureLen;
            int size = fl is 185 or 186 ? 185 : fl is 162 or 163 ? 162 : fl is 112 or 113 ? 112 : 0;
            if (size > 0)
            {
                Try("MSI", "MSI Mystic Light", h.Vid, h.Pid, () =>
                {
                    var dev = HidDevice.Open(h);
                    var pkt = dev.GetFeature(0x52, fl);
                    if (pkt == null) { dev.Dispose(); throw new IOException("Einstellungen nicht lesbar"); }
                    var data = new byte[size];
                    Array.Copy(pkt, data, Math.Min(pkt.Length, size));
                    return new MsiMysticDevice(dev, data);
                });
            }
            else if (fl >= 727)
                Try("MSI", "MSI Mystic Light (800er)", h.Vid, h.Pid, () => new Msi761Device(HidDevice.Open(h)));
        }

        // ------------------------------------------------------------------ ASRock Polychrome
        foreach (var h in all.Where(h => h.Vid == 0x26CE && (h.Pid == 0x01A2 || h.Pid == 0x01A6) && h.OutputLen >= 64)
                             .GroupBy(h => h.Pid).Select(g => g.First()))
            Try("ASRock", "Polychrome", h.Vid, h.Pid, () => new AsrockPolychromeDevice(HidDevice.Open(h)));

        // ------------------------------------------------------------------ Corsair
        foreach (var g in all.Where(h => h.Vid == 0x1B1C).GroupBy(h => h.Pid))
        {
            if (CorsairNode.TryGetValue(g.Key, out var cn))
            {
                var h = g.FirstOrDefault(x => x.OutputLen >= 64);
                if (h != null) Try("Corsair", cn.name, 0x1B1C, g.Key, () => new CorsairLightingNodeDevice(HidDevice.Open(h), cn.name, cn.ch));
            }
            else if (CommanderCore.Contains(g.Key))
            {
                var h = Pick(g, 0, 0xFF42, 0x01, x => x.OutputLen > 0);
                if (h != null) Try("Corsair", "Commander Core", 0x1B1C, g.Key, () => new CorsairCommanderCoreDevice(HidDevice.Open(h), g.Key == 0x0C2A));
            }
        }

        // ------------------------------------------------------------------ Lian Li Uni Hub
        foreach (var g in all.Where(h => h.Vid == 0x0CF2 && LianLi.ContainsKey(h.Pid)).GroupBy(h => h.Pid))
        {
            var h = Pick(g, 1, 0xFF72, 0xA1, x => x.OutputLen > 0);
            if (h != null) Try("Lian Li", "Uni Hub", 0x0CF2, g.Key, () => new LianLiUniHubDevice(HidDevice.Open(h), LianLi[g.Key]));
        }

        // ------------------------------------------------------------------ NZXT
        foreach (var g in all.Where(h => h.Vid == 0x1E71 && Nzxt.ContainsKey(h.Pid)).GroupBy(h => h.Pid))
        {
            var (name, ch) = Nzxt[g.Key];
            var h = g.FirstOrDefault(x => x.OutputLen >= 64);
            if (h != null) Try("NZXT", name, 0x1E71, g.Key, () => new NzxtHue2Device(HidDevice.Open(h), name, ch));
        }

        // ------------------------------------------------------------------ Bekannt, aber nicht eingebaut
        foreach (var g in all.Where(h => OtherVendors.ContainsKey(h.Vid) && !handled.Contains((h.Vid, h.Pid))).GroupBy(h => (h.Vid, h.Pid)))
        {
            var h = g.First();
            if (h.Vid is 0x1462 or 0x0B05 && string.IsNullOrWhiteSpace(h.Product)) continue;
            report.Add(new(OtherVendors[h.Vid], NiceName(h, $"{h.Vid:X4}:{h.Pid:X4}"), "Erkannt – noch nicht unterstützt", false));
        }

        return (devices, report, all);
    }

    /// <summary>Mainboard-Modell aus der Registry (z. B. "B760 DS3H AX DDR4").</summary>
    public static string BoardName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (k?.GetValue("BaseBoardProduct") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    private static string NiceName(HidInfo h, string fallback) =>
        string.IsNullOrWhiteSpace(h.Product) ? fallback : h.Product;

    /// <summary>Alle HID-Geräte als Text (zum Nachrüsten weiterer Hardware).</summary>
    public static string HardwareReport(List<HidInfo> all, List<DetectedHardware> report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Nick Fan Control – Hardware-Liste vom {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("RGB-Geräte:");
        foreach (var r in report) sb.AppendLine($"  [{(r.Supported ? "OK" : "--")}] {r.Vendor}: {r.Name} – {r.Status}");
        sb.AppendLine();
        sb.AppendLine("Alle HID-Schnittstellen:");
        foreach (var h in all.OrderBy(h => h.Vid).ThenBy(h => h.Pid).ThenBy(h => h.Interface)) sb.AppendLine("  " + h);
        return sb.ToString();
    }
}
