using System.IO;

namespace NickFanControl;

// ============================================================================ Corsair Lighting Node / Commander Pro

/// <summary>Corsair Lighting Node Core/Pro, Commander Pro, LS100, LT100, 1000D, SPEC OMEGA (USB 1B1C).</summary>
public sealed class CorsairLightingNodeDevice : RgbDevice
{
    private readonly HidDevice _hid;
    public override int MinIntervalMs => 40;

    public CorsairLightingNodeDevice(HidDevice hid, string name, int channels)
    {
        _hid = hid;
        Name = name;
        Vendor = "Corsair";
        Key = $"corsair:{hid.Info.Pid:X4}:{hid.Info.Serial}";
        for (int i = 0; i < channels; i++)
            Zones.Add(new RgbZone
            {
                Device = this, Index = i, Name = $"RGB-Kanal {i + 1}", Kind = "Corsair-Kanal",
                LedCount = 24, Adjustable = true, MaxLeds = 204, PerLed = true
            });
    }

    private void Cmd(params byte[] payload)
    {
        var buf = new byte[65];
        Array.Copy(payload, 0, buf, 1, Math.Min(payload.Length, 64));
        _hid.Write(buf);
        _hid.Read(30); // Antwort (17 Byte) abholen
    }

    public override void Init()
    {
        Cmd(0x02); // Firmware abfragen (weckt das Gerät auf)
        for (byte ch = 0; ch < Zones.Count; ch++) Cmd(0x39, ch, 100); // Helligkeit 100 %
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        byte ch = (byte)zone.Index;
        Cmd(0x38, ch, 0x02); // Software-Steuerung
        int n = rgb.Length / 3, off = 0;
        var r = new byte[50]; var g = new byte[50]; var b = new byte[50];
        while (off < n)
        {
            int cnt = Math.Min(50, n - off);
            for (int i = 0; i < cnt; i++) { r[i] = rgb[(off + i) * 3]; g[i] = rgb[(off + i) * 3 + 1]; b[i] = rgb[(off + i) * 3 + 2]; }
            foreach (var (color, data) in new[] { ((byte)0, r), ((byte)1, g), ((byte)2, b) })
            {
                var p = new byte[5 + cnt];
                p[0] = 0x32; p[1] = ch; p[2] = (byte)off; p[3] = (byte)cnt; p[4] = color;
                Array.Copy(data, 0, p, 5, cnt);
                Cmd(p);
            }
            off += cnt;
        }
    }

    public override void Commit() => Cmd(0x33, 0xFF);

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ Corsair Commander Core / Core XT

/// <summary>Corsair Commander Core (iCUE-AIOs, Elite Capellix) und Commander Core XT.</summary>
public sealed class CorsairCommanderCoreDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly bool _xt;
    private int _packet;
    private bool _externalSet;
    public override int MinIntervalMs => 50;

    public CorsairCommanderCoreDevice(HidDevice hid, bool xt)
    {
        _hid = hid;
        _xt = xt;
        Name = xt ? "Corsair Commander Core XT" : "Corsair Commander Core";
        Vendor = "Corsair";
        Key = $"corsair:{hid.Info.Pid:X4}:{hid.Info.Serial}";
        _packet = Math.Max(hid.Info.OutputLen, xt ? 385 : 97);
    }

    private byte[] Command(byte c0, byte c1, byte[]? data = null, bool read = true)
    {
        var buf = new byte[_packet];
        buf[1] = 0x08; buf[2] = c0; buf[3] = c1;
        if (data != null) Array.Copy(data, 0, buf, 4, Math.Min(data.Length, _packet - 4));
        _hid.Write(buf);
        if (!read) return Array.Empty<byte>();
        for (int i = 0; i < 10; i++)
        {
            var r = _hid.Read(200);
            if (r == null) break;
            if (r.Length > 0 && r[0] == 0x00) return r.Length > 3 ? r[3..] : Array.Empty<byte>(); // = &buf[3] bei hidapi
        }
        return Array.Empty<byte>();
    }

    private void WriteData(byte e0, byte t0, byte[] data)
    {
        Command(0x0D, 0x00, new byte[] { e0, 0x00 });
        int idx = 0;
        while (idx < data.Length)
        {
            if (idx == 0)
            {
                int len = Math.Min(_packet - 10, data.Length);
                var b = new byte[len + 6];
                int real = data.Length + 2;
                b[0] = (byte)real; b[1] = (byte)(real >> 8); b[4] = t0; b[5] = 0x00;
                Array.Copy(data, 0, b, 6, len);
                Command(0x06, 0x00, b);
                idx += len;
            }
            else
            {
                int len = Math.Min(_packet - 4, data.Length - idx);
                Command(0x07, 0x00, data[idx..(idx + len)]);
                idx += len;
            }
        }
        Command(0x05, 0x01);
    }

    private byte[] ReadData(byte e0)
    {
        Command(0x0D, 0x00, new byte[] { e0, 0x00 });
        var res = Command(0x08, 0x00);
        Command(0x05, 0x01);
        return res;
    }

    private void SetFanMode(bool external)
    {
        var buf = new byte[15];
        buf[0] = 0x07;
        int index = 3, max = 15;
        if (_xt)
        {
            if (external) { buf[1] = 0x01; buf[2] = 0x01; }
            else { index = 2; max = 14; }
        }
        else { buf[1] = 0x01; buf[2] = 0x08; }
        for (int i = index; i < max; i += 2) { buf[i] = 0x01; buf[i + 1] = 0x06; }
        WriteData(0x1E, 0x0D, buf);
        Command(0x01, 0x03, new byte[] { 0x00, 0x02 }); // Software-Modus
    }

    public override void Init()
    {
        var ver = Command(0x02, 0x13);
        if (!_xt && _hid.Info.Pid == 0x0C1C && ver.Length > 0 && ver[0] == 1) _packet = Math.Max(_packet, 1025);
        SetFanMode(false);

        if (_xt)
        {
            Zones.Add(new RgbZone { Device = this, Index = 0, Name = "RGB-Anschluss", Kind = "ARGB-Anschluss", LedCount = 0, Adjustable = true, MaxLeds = 204, PerLed = true });
        }
        else
        {
            int pump = 29;
            var counts = ReadData(0x20);
            if (counts.Length > 7 && counts[2] > 0) pump = counts[6] << 8 | counts[5];
            if (pump <= 0 || pump > 64) pump = 29;
            Zones.Add(new RgbZone { Device = this, Index = 0, Name = "Pumpe", Kind = "AIO-Pumpe", LedCount = pump, MaxLeds = pump, PerLed = true });
        }
        for (int i = 1; i <= 6; i++)
            Zones.Add(new RgbZone
            {
                Device = this, Index = i, Name = $"Lüfter-Anschluss {i}", Kind = "Corsair-Lüfter",
                LedCount = 34, Adjustable = true, MaxLeds = 34, PerLed = true
            });
    }

    private readonly Dictionary<int, byte[]> _zoneColors = new();

    public override void ApplyLedCount(RgbZone zone)
    {
        if (_xt && zone.Index == 0)
        {
            bool ext = zone.LedCount > 0;
            SetFanMode(ext);
            if (ext)
            {
                var b = new byte[15]; b[0] = 0x07; b[1] = (byte)zone.LedCount;
                WriteData(0x1D, 0x0C, b);
            }
            _externalSet = ext;
        }
    }

    public override void SetZone(RgbZone zone, byte[] rgb) => _zoneColors[zone.Index] = rgb;

    public override void Commit()
    {
        int max = _xt ? 1224 : 699;
        var buf = new byte[max];
        int off = 0;
        for (int z = 0; z < Zones.Count; z++)
        {
            var zn = Zones[z];
            int leds = zn.LedCount;
            if (z == 0 && _xt && !_externalSet) leds = 0;
            _zoneColors.TryGetValue(z, out var c);
            for (int i = 0; i < leds && off + 3 <= max; i++)
            {
                if (c != null && i * 3 + 2 < c.Length) { buf[off] = c[i * 3]; buf[off + 1] = c[i * 3 + 1]; buf[off + 2] = c[i * 3 + 2]; }
                off += 3;
            }
            if (z != 0) off += 3 * (34 - leds);
        }
        off = Math.Min(off, max);
        WriteData(0x22, 0x12, buf[..off]);
    }

    public override void Dispose()
    {
        try { Command(0x01, 0x03, new byte[] { 0x00, 0x01 }); } catch { } // zurück an die Hardware
        _hid.Dispose();
    }
}

// ============================================================================ Lian Li Uni Hub

public enum LianLiKind { SL, AL, SLInfinity, SLV2 }

/// <summary>Lian Li Uni Hub SL / AL / SL-Infinity / SL V2 / AL V2 (USB 0CF2).</summary>
public sealed class LianLiUniHubDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly LianLiKind _k;
    private readonly int[] _fans;
    public override int MinIntervalMs => 70;

    public LianLiUniHubDevice(HidDevice hid, LianLiKind kind)
    {
        _hid = hid;
        _k = kind;
        Vendor = "Lian Li";
        Key = $"lianli:{hid.Info.Pid:X4}:{hid.Info.Serial}";
        Name = kind switch
        {
            LianLiKind.SL => "Lian Li Uni Hub SL",
            LianLiKind.AL => "Lian Li Uni Hub AL",
            LianLiKind.SLInfinity => "Lian Li Uni Hub SL Infinity",
            _ => hid.Info.Pid == 0xA104 ? "Lian Li Uni Hub AL V2" : "Lian Li Uni Hub SL V2"
        };
        int channels = kind == LianLiKind.SLInfinity ? 8 : 4;
        _fans = new int[channels];
        for (int i = 0; i < channels; i++)
        {
            string name = kind == LianLiKind.SLInfinity ? $"Kanal {i / 2 + 1} · {(i % 2 == 0 ? "Teil A" : "Teil B")}" : $"Kanal {i + 1}";
            int perFan = kind switch { LianLiKind.SL => 1, LianLiKind.AL => 20, _ => 16 };
            int maxFans = kind switch { LianLiKind.SLV2 => 6, _ => 4 };
            Zones.Add(new RgbZone
            {
                Device = this, Index = i, Name = name, Kind = "Lian-Li-Kanal",
                LedCount = perFan * 3, Adjustable = true, MaxLeds = perFan * maxFans, PerLed = true,
                LedsPerFan = perFan, DefaultFans = 3
            });
        }
    }

    private void Out(byte[] data)
    {
        _hid.Write(data);
        Thread.Sleep(3);
    }

    public override void Init()
    {
        if (_k == LianLiKind.SL)
        {
            var m = new byte[11]; m[0] = 0xE0; m[1] = 0x10; m[2] = 0x34; // nicht zusammengelegt
            Out(m);
        }
    }

    public override void ApplyLedCount(RgbZone zone) => _fans[zone.Index] = 0; // erzwingt neues "Aktivieren"

    private static (byte r, byte g, byte b) Limit(byte r, byte g, byte b, LianLiKind k)
    {
        if (k == LianLiKind.AL)
            return r > 153 && r == g && g == b ? ((byte)153, (byte)153, (byte)153) : (r, g, b);
        int sum = r + g + b;
        if (sum <= 460) return (r, g, b);
        double f = 460.0 / sum;
        return ((byte)(r * f), (byte)(g * f), (byte)(b * f));
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        int ch = zone.Index;
        int perFan = zone.LedsPerFan;
        int fans = Math.Clamp((rgb.Length / 3 + perFan - 1) / Math.Max(1, perFan), 1, 6);

        switch (_k)
        {
            case LianLiKind.SL:
            {
                if (_fans[ch] != fans)
                {
                    var a = new byte[11]; a[0] = 0xE0; a[1] = 0x10; a[2] = 0x32; a[3] = (byte)(0x10 * ch + fans);
                    Out(a);
                    _fans[ch] = fans;
                }
                var c = new byte[2 + 64 * 3];
                c[0] = 0xE0; c[1] = (byte)(0x30 + ch);
                for (int f = 0; f < fans && f < 4; f++)
                {
                    var (r, g, b) = Limit(rgb[f * 3], rgb[f * 3 + 1], rgb[f * 3 + 2], _k);
                    for (int l = 0; l < 16; l++) { int o = 2 + (f * 16 + l) * 3; c[o] = r; c[o + 1] = b; c[o + 2] = g; }
                }
                Out(c);
                var m = new byte[11]; m[0] = 0xE0; m[1] = (byte)(0x10 + ch); m[2] = 0x01; m[3] = 0x02;
                Out(m);
                break;
            }
            case LianLiKind.AL:
            {
                var start = new byte[65]; start[0] = 0xE0; start[1] = 0x10; start[2] = 0x40; start[3] = (byte)(ch + 1); start[4] = (byte)fans;
                Out(start);
                var fanData = new byte[146]; var edgeData = new byte[146];
                fanData[0] = edgeData[0] = 0xE0;
                fanData[1] = (byte)(0x30 + ch * 2); edgeData[1] = (byte)(0x31 + ch * 2);
                int n = rgb.Length / 3;
                for (int i = 0; i < n && i < fans * 20; i++)
                {
                    int fan = i / 20, m = i % 20;
                    var (r, g, b) = Limit(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], _k);
                    if (m < 8) { int o = 2 + (fan * 8 + m) * 3; if (o + 2 < 146) { fanData[o] = r; fanData[o + 1] = b; fanData[o + 2] = g; } }
                    else { int o = 2 + (fan * 12 + m - 8) * 3; if (o + 2 < 146) { edgeData[o] = r; edgeData[o + 1] = b; edgeData[o + 2] = g; } }
                }
                Out(fanData);
                Out(Commit(0x10 + ch * 2));
                Out(edgeData);
                Out(Commit(0x11 + ch * 2));
                break;
            }
            default: // SL V2, AL V2, SL Infinity
            {
                var start = new byte[_k == LianLiKind.SLInfinity ? 65 : 353];
                start[0] = 0xE0; start[1] = 0x10; start[2] = 0x60;
                if (_k == LianLiKind.SLInfinity) { start[3] = (byte)(1 + ch / 2); start[4] = 0x04; }
                else start[3] = (byte)((ch << 4) + fans);
                Out(start);
                var data = new byte[353];
                data[0] = 0xE0; data[1] = (byte)(0x30 + ch);
                int n = Math.Min(rgb.Length / 3, 96);
                for (int i = 0; i < n; i++)
                {
                    var (r, g, b) = Limit(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], _k);
                    data[2 + i * 3] = r; data[3 + i * 3] = b; data[4 + i * 3] = g;
                }
                Out(data);
                var commit = Commit(0x10 + ch);
                if (_k != LianLiKind.SLInfinity) Array.Resize(ref commit, 353);
                Out(commit);
                break;
            }
        }
    }

    private static byte[] Commit(int target)
    {
        var c = new byte[65];
        c[0] = 0xE0; c[1] = (byte)target; c[2] = 0x01; c[3] = 0x02; c[4] = 0x00; c[5] = 0x00; // statisch, langsam, 100 %
        return c;
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ NZXT Hue 2 / RGB & Fan Controller / Kraken

/// <summary>NZXT Hue 2, Smart Device V2, RGB &amp; Fan Controller, Kraken X3/Elite, B650E-Mainboard (USB 1E71).</summary>
public sealed class NzxtHue2Device : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly int _channels;
    public override int MinIntervalMs => 35;

    public NzxtHue2Device(HidDevice hid, string name, int channels)
    {
        _hid = hid;
        _channels = channels;
        Name = name;
        Vendor = "NZXT";
        Key = $"nzxt:{hid.Info.Pid:X4}:{hid.Info.Serial}";
    }

    private static int LedsFor(byte id) => id switch
    {
        0x01 => 10, 0x02 => 8, 0x04 => 10, 0x05 => 8, 0x06 => 6, 0x08 => 14, 0x09 => 15, 0x0A => 10,
        0x0B => 8, 0x0C => 8, 0x10 => 8, 0x11 => 1, 0x13 => 18, 0x14 => 18, 0x15 => 20, 0x16 => 20,
        0x17 => 8, 0x18 => 8, 0x19 => 8, 0x1C => 16, 0x1D => 24, 0x1E => 24, 0x1F => 24, _ => 0
    };

    public override void Init()
    {
        var req = new byte[64]; req[0] = 0x20; req[1] = 0x03;
        _hid.Write(req);
        byte[]? list = null;
        for (int i = 0; i < 10; i++)
        {
            var r = _hid.Read(1000);
            if (r != null && r.Length >= 64 && r[0] == 0x21 && r[1] == 0x03) { list = r; break; }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            int leds = 0, devices = 0;
            if (list != null)
                for (int d = 0; d < 6; d++)
                {
                    int l = LedsFor(list[0x0F + 6 * ch + d]);
                    if (l > 0) { leds += l; devices++; }
                }
            if (leds == 0) continue; // nichts angeschlossen
            Zones.Add(new RgbZone
            {
                Device = this, Index = ch, Name = $"Kanal {ch + 1}", Kind = "NZXT-Kanal",
                LedCount = Math.Min(leds, 40), MaxLeds = Math.Min(leds, 40), PerLed = true, DefaultFans = Math.Max(1, devices)
            });
        }
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        int ch = zone.Index, n = Math.Min(rgb.Length / 3, 40);
        var data = new byte[n * 3];
        for (int i = 0; i < n; i++) { data[i * 3] = rgb[i * 3 + 1]; data[i * 3 + 1] = rgb[i * 3]; data[i * 3 + 2] = rgb[i * 3 + 2]; } // G,R,B
        Direct(ch, 0, data, 0, Math.Min(n, 20));
        if (n > 20) Direct(ch, 1, data, 60, n - 20);
        var apply = new byte[64];
        apply[0] = 0x22; apply[1] = 0xA0; apply[2] = (byte)(1 << ch);
        apply[4] = 0x01; apply[7] = 0x28; apply[10] = 0x80; apply[12] = 0x32; apply[15] = 0x01;
        _hid.Write(apply);
    }

    private void Direct(int ch, int group, byte[] data, int offset, int count)
    {
        var b = new byte[64];
        b[0] = 0x22; b[1] = (byte)(0x10 | group); b[2] = (byte)(1 << ch);
        Array.Copy(data, offset, b, 4, count * 3);
        _hid.Write(b);
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ MSI Mystic Light (112/162/185)

/// <summary>MSI Mystic Light (USB 1462) – ältere Generationen. Eine Farbe pro Anschluss.</summary>
public sealed class MsiMysticDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly byte[] _packet;
    private readonly List<int[]> _zoneOffsets = new();
    public override int MinIntervalMs => 100;

    public MsiMysticDevice(HidDevice hid, byte[] packet)
    {
        _hid = hid;
        _packet = packet;
        Vendor = "MSI";
        Name = "MSI Mystic Light";
        Key = $"msi:{hid.Info.Pid:X4}";
    }

    public override void Init()
    {
        void Add(string name, string kind, params int[] offsets)
        {
            _zoneOffsets.Add(offsets);
            Zones.Add(new RgbZone { Device = this, Index = Zones.Count, Name = name, Kind = kind, LedCount = 1, MaxLeds = 1 });
        }

        var onboard = new List<int>();
        switch (_packet.Length)
        {
            case 185:
                Add("JRGB 1", "RGB-Anschluss", 1);
                Add("JRGB 2", "RGB-Anschluss", 174);
                Add("JRAINBOW 1", "ARGB-Anschluss", 31);
                Add("JRAINBOW 2", "ARGB-Anschluss", 42);
                Add("JCORSAIR", "ARGB-Anschluss", 53);
                onboard.AddRange(new[] { 11, 21 });
                for (int i = 0; i < 10; i++) onboard.Add(74 + i * 10);
                break;
            case 162:
                Add("JRGB 1", "RGB-Anschluss", 1);
                Add("JRGB 2", "RGB-Anschluss", 151);
                Add("JRAINBOW 1", "ARGB-Anschluss", 11);
                Add("JCORSAIR", "ARGB-Anschluss", 21);
                for (int i = 0; i < 11; i++) onboard.Add(41 + i * 10);
                break;
            default: // 112
                Add("JRGB 1", "RGB-Anschluss", 1);
                Add("JRAINBOW 1", "ARGB-Anschluss", 11);
                Add("JCORSAIR", "ARGB-Anschluss", 21);
                for (int i = 0; i < 7; i++) onboard.Add(41 + i * 10);
                break;
        }
        Add("Mainboard-LEDs", "Mainboard", onboard.ToArray());
        Name = $"MSI Mystic Light ({_packet.Length})";
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        var c = Avg(rgb);
        foreach (int o in _zoneOffsets[zone.Index])
        {
            if (o + 9 >= _packet.Length) continue;
            _packet[o] = 0x01;
            _packet[o + 1] = c.R8; _packet[o + 2] = c.G8; _packet[o + 3] = c.B8;
            _packet[o + 4] = 10 << 2;
            _packet[o + 8] = 0x80;
            _packet[o + 9] = 0x00;
        }
    }

    public override void Commit()
    {
        _packet[0] = 0x52;
        _packet[^1] = 0x00;
        _hid.SetFeature(_packet);
    }

    public override void Dispose() => _hid.Dispose();
}

// ============================================================================ MSI Mystic Light (761, neue Boards)

/// <summary>MSI Mystic Light der 800er-Generation (X870/B850/Z890 …) – Einzel-LED-Steuerung der JARGB-Anschlüsse.</summary>
public sealed class Msi761Device : RgbDevice
{
    private static readonly byte[] Initial =
    {
        0x50,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x15,0x78,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x15,0x78,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x15,0x78,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x15,0x78,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x95,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x95,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x95,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0,0,0,0,0,0,0,0,0,0,0,0,0,0x94,0x1E,
        0x09,0xFF,0,0,0,0xFF,0,0,0,0xFF,0xFF,0xFF,0xFF,0x03,0x95,0x1E,
        0x00
    };

    private readonly HidDevice _hid;
    private readonly byte[] _setup = new byte[290];
    private bool _setupDirty = true;
    // Zone → (Setup-Zeile, hdr0, hdr1)
    private static readonly (string name, int row, byte h0, byte h1)[] Map =
    {
        ("JARGB 1", 0, 0x04, 0x00), ("JARGB 2", 1, 0x04, 0x01), ("JARGB 3", 2, 0x04, 0x02), ("JAF (Lüfter-Anschluss)", 3, 0x08, 0x00)
    };
    public override int MinIntervalMs => 60;

    public Msi761Device(HidDevice hid)
    {
        _hid = hid;
        Vendor = "MSI";
        Name = "MSI Mystic Light (800er-Serie)";
        Key = $"msi761:{hid.Info.Pid:X4}";
    }

    public override void Init()
    {
        Array.Copy(Initial, _setup, Math.Min(Initial.Length, _setup.Length));
        for (int i = 4; i < 17; i++)
            foreach (int col in new[] { 1, 2, 6, 10, 11, 12, 13, 14, 15 })
                _setup[i * 16 + col] = 0x00;
        _hid.GetFeature(0x51, Math.Max(_hid.Info.FeatureLen, 500)); // "Lesen aktivieren"
        for (int i = 0; i < Map.Length; i++)
            Zones.Add(new RgbZone
            {
                Device = this, Index = i, Name = Map[i].name, Kind = "ARGB-Anschluss",
                LedCount = 24, Adjustable = true, MaxLeds = 240, PerLed = true
            });
        foreach (var z in Zones) ApplyLedCount(z);
    }

    public override void ApplyLedCount(RgbZone zone)
    {
        int idx = Map[zone.Index].row * 16 + 16;
        if (_setup[idx] != zone.LedCount) { _setup[idx] = (byte)zone.LedCount; _setupDirty = true; }
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        if (_setupDirty) { _hid.SetFeature(_setup); _setupDirty = false; }
        var (_, _, h0, h1) = Map[zone.Index];
        var p = new byte[7 + 720];
        p[0] = 0x51; p[1] = 0x09; p[2] = h0; p[3] = h1; p[6] = (byte)Math.Min(240, rgb.Length / 3);
        Array.Copy(rgb, 0, p, 7, Math.Min(rgb.Length, 720));
        if (!_hid.SetFeature(p)) throw new IOException("MSI: Daten abgelehnt");
    }

    public override void Dispose() => _hid.Dispose();
}
