using System.IO;

namespace NickFanControl;

/// <summary>
/// Razer-Tastaturen, -Mäuse, -Mauspads usw. direkt über USB (ohne Synapse).
/// Einzel-LED-Steuerung: Die Zone hat so viele "LEDs" wie das Gerät Spalten hat –
/// Effekte laufen dadurch als Welle von links nach rechts über die Tastatur.
/// </summary>
public sealed class RazerDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly RazerDeviceTable.Entry _e;
    private bool _staticFallback;
    public override int MinIntervalMs => _e.Rows > 2 ? 45 : 35;

    public RazerDevice(HidDevice hid, RazerDeviceTable.Entry e)
    {
        _hid = hid;
        _e = e;
        Name = e.Name;
        Vendor = "Razer";
        Key = $"razer:{hid.Info.Pid:X4}:{hid.Info.Serial}";
        int cols = Math.Max(1, e.Cols);
        Zones.Add(new RgbZone
        {
            Device = this, Index = 0, Name = e.Kind, Kind = e.Kind,
            LedCount = cols, MaxLeds = cols, PerLed = cols > 1
        });
    }

    public override void Init() { }

    // ---- Report-Aufbau (91 Byte Feature-Report, Report-ID 0)
    private byte[] NewReport(byte cls, byte id, byte size)
    {
        var r = new byte[91];
        r[2] = _e.TransactionId;
        r[6] = size;
        r[7] = cls;
        r[8] = id;
        return r;
    }

    private void Send(byte[] r)
    {
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= r[i];
        r[89] = crc;
        if (!_hid.SetFeature(r)) throw new IOException("Razer: Befehl abgelehnt");
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        if (_staticFallback) { SendStatic(Avg(rgb)); return; }
        try { SendFrame(rgb); }
        catch (IOException)
        {
            // manche Geräte kennen keine Einzel-LED-Frames → einfache Farbe
            _staticFallback = true;
            Log.Write($"Razer {_e.Name}: Einzel-LED nicht möglich, nutze Einzelfarbe.");
            SendStatic(Avg(rgb));
        }
    }

    private void SendFrame(byte[] rgb)
    {
        int cols = Math.Max(1, _e.Cols), rows = Math.Max(1, _e.Rows);
        int n = rgb.Length / 3;
        var row = new byte[cols * 3];
        for (int c = 0; c < cols; c++)
        {
            int src = Math.Min(c, n - 1) * 3;
            row[c * 3] = rgb[src]; row[c * 3 + 1] = rgb[src + 1]; row[c * 3 + 2] = rgb[src + 2];
        }

        for (int r = 0; r < rows; r++)
        {
            for (int start = 0; start < cols; start += 25)
            {
                int stop = Math.Min(cols, start + 25) - 1;
                int len = (stop - start + 1) * 3;
                byte[] rep;
                switch (_e.Proto)
                {
                    case RazerProto.Ext:
                        rep = NewReport(0x0F, 0x03, (byte)(len + 5));
                        rep[11] = (byte)r; rep[12] = (byte)start; rep[13] = (byte)stop;
                        Array.Copy(row, start * 3, rep, 14, len);
                        break;
                    case RazerProto.Linear:
                        rep = NewReport(0x03, 0x0C, 0x32);
                        rep[9] = (byte)start; rep[10] = (byte)stop;
                        Array.Copy(row, start * 3, rep, 11, len);
                        break;
                    default:
                        rep = NewReport(0x03, 0x0B, (byte)(len + 4));
                        rep[9] = 0xFF; rep[10] = (byte)r; rep[11] = (byte)start; rep[12] = (byte)stop;
                        Array.Copy(row, start * 3, rep, 13, len);
                        break;
                }
                Send(rep);
            }
            if (_e.Proto == RazerProto.Linear) break;
        }

        // "Custom"-Modus aktivieren, damit der Frame angezeigt wird
        if (_e.Proto == RazerProto.Ext)
        {
            var m = NewReport(0x0F, 0x02, 0x0C);
            m[11] = 0x08;
            Send(m);
        }
        else
        {
            var m = NewReport(0x03, 0x0A, 0x02);
            m[9] = 0x05;
            Send(m);
        }
    }

    private void SendStatic(RgbF c)
    {
        byte[] r;
        if (_e.Proto == RazerProto.Ext)
        {
            r = NewReport(0x0F, 0x02, 0x09);
            r[10] = _e.LedId; r[11] = 0x01; r[14] = 0x01;
            r[15] = c.R8; r[16] = c.G8; r[17] = c.B8;
        }
        else
        {
            r = NewReport(0x03, 0x0A, 0x04);
            r[9] = 0x06; r[10] = c.R8; r[11] = c.G8; r[12] = c.B8;
        }
        Send(r);
    }

    public override void Dispose() => _hid.Dispose();
}

/// <summary>Razer Chroma Addressable RGB Controller (1532:0F1F) – 6 ARGB-Kanäle für Lüfter und LED-Streifen.</summary>
public sealed class RazerArgbDevice : RgbDevice
{
    private readonly HidDevice _ctrl;   // Interface 0: Befehle (91 Byte)
    private readonly HidDevice _frame;  // Interface 1: Farbdaten (321 Byte)
    private readonly int[] _sizes = new int[6];
    private bool _sizesDirty = true;

    public RazerArgbDevice(HidDevice ctrl, HidDevice frame)
    {
        _ctrl = ctrl;
        _frame = frame;
        Name = "Razer Chroma ARGB-Controller";
        Vendor = "Razer";
        Key = $"razer:0F1F:{ctrl.Info.Serial}";
        for (int i = 0; i < 6; i++)
            Zones.Add(new RgbZone
            {
                Device = this, Index = i, Name = $"ARGB-Kanal {i + 1}", Kind = "ARGB-Anschluss",
                LedCount = 24, Adjustable = true, MaxLeds = 80, PerLed = true
            });
    }

    public override void Init() { for (int i = 0; i < 6; i++) _sizes[i] = Zones[i].LedCount; _sizesDirty = true; }

    public override void ApplyLedCount(RgbZone zone)
    {
        _sizes[zone.Index] = zone.LedCount;
        _sizesDirty = true;
    }

    private void SendSizes()
    {
        var r = new byte[91];
        r[2] = 0x3F; r[6] = 0x0D; r[7] = 0x0F; r[8] = 0x08;
        r[9] = 0x06;
        for (int i = 0; i < 6; i++)
        {
            r[10 + i * 2] = (byte)(_sizes[i] == 0 ? i + 1 : 0x19);
            r[11 + i * 2] = (byte)_sizes[i];
        }
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= r[i];
        r[89] = crc;
        _ctrl.SetFeature(r);
        _sizesDirty = false;
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        if (_sizesDirty) SendSizes();
        int ch = zone.Index;
        var rep = new byte[321];
        rep[0] = 0x00;
        rep[1] = (byte)(ch < 5 ? 0x04 : 0x84);
        rep[2] = (byte)ch;
        rep[3] = (byte)ch;
        rep[5] = 79;
        Array.Copy(rgb, 0, rep, 6, Math.Min(rgb.Length, 240));
        if (!_frame.SetFeature(rep)) throw new IOException("Razer ARGB: Frame abgelehnt");
    }

    public override void Dispose() { _ctrl.Dispose(); _frame.Dispose(); }
}

/// <summary>Razer Kraken Headsets (drei Protokoll-Generationen).</summary>
public sealed class RazerKrakenDevice : RgbDevice
{
    private readonly HidDevice _hid;
    private readonly RazerDeviceTable.Entry _e;
    private readonly ushort _pid;
    public override int MinIntervalMs => 50;

    public RazerKrakenDevice(HidDevice hid, RazerDeviceTable.Entry e)
    {
        _hid = hid;
        _e = e;
        _pid = hid.Info.Pid;
        Name = e.Name;
        Vendor = "Razer";
        Key = $"razer:{_pid:X4}:{hid.Info.Serial}";
        int leds = e.Proto == RazerProto.Kraken1 ? 1 : Math.Max(1, e.Cols);
        Zones.Add(new RgbZone { Device = this, Index = 0, Name = "Headset", Kind = "Headset", LedCount = leds, MaxLeds = leds, PerLed = leds > 1 });
    }

    public override void Init()
    {
        if (_e.Proto == RazerProto.Kraken3)
        {
            // Direct-Modus einschalten
            var b = new byte[15];
            b[0] = 0x40; b[1] = 0x01; b[3] = 0x0F; b[4] = 0x08;
            _hid.Write(b);
        }
    }

    public override void SetZone(RgbZone zone, byte[] rgb)
    {
        switch (_e.Proto)
        {
            case RazerProto.Kraken1:
            {
                bool newer = _pid is 0x0510 or 0x0560 or 0x0527;
                ushort modeAddr = newer ? (ushort)0x172D : (ushort)0x1008;
                var c = Avg(rgb);
                if (_pid is 0x0504 or 0x0510 or 0x0560 or 0x0527)
                {
                    var col = new byte[37];
                    col[0] = 0x04; col[1] = 0x40; col[2] = 3; col[3] = 0x11; col[4] = 0x89; // Adresse 0x1189
                    col[5] = c.R8; col[6] = c.G8; col[7] = c.B8;
                    _hid.Write(col);
                }
                var eff = new byte[37];
                eff[0] = 0x04; eff[1] = 0x40; eff[2] = 1; eff[3] = (byte)(modeAddr >> 8); eff[4] = (byte)modeAddr;
                eff[5] = 0x01; // statisch an
                _hid.Write(eff);
                break;
            }
            case RazerProto.Kraken3:
            {
                var b = new byte[15];
                b[0] = 0x40; b[1] = 0x03;
                int n = Math.Min(rgb.Length / 3, 4);
                for (int i = 0; i < n; i++)
                {
                    b[3 + i * 3] = rgb[i * 3]; b[4 + i * 3] = rgb[i * 3 + 1]; b[5 + i * 3] = rgb[i * 3 + 2];
                }
                _hid.Write(b);
                break;
            }
            case RazerProto.Kraken4:
            {
                int cols = Math.Max(1, _e.Cols);
                var b = new byte[64];
                b[0] = 0x02;
                b[2] = _e.TransactionId;
                b[6] = (byte)(5 + 3 * cols);
                b[7] = 0x0F; b[8] = 0x03;
                b[9] = (byte)(_pid is 0x056C or 0x0588 ? 0x80 : 0x00);
                b[14] = (byte)(cols - 1);
                int n = rgb.Length / 3;
                for (int i = 0; i < cols && 15 + i * 3 + 2 < 62; i++)
                {
                    int s = Math.Min(i, n - 1) * 3;
                    b[15 + i * 3] = rgb[s]; b[16 + i * 3] = rgb[s + 1]; b[17 + i * 3] = rgb[s + 2];
                }
                _hid.Write(b);
                break;
            }
        }
    }

    public override void Dispose() => _hid.Dispose();
}
