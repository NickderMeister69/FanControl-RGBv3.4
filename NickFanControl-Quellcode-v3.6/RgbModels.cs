namespace NickFanControl;

public enum LightEffect { Static, Slideshow, Rainbow, Breathing, Screen, Off }
public enum ScreenRegion { All, Left, Right, Top, Bottom, Center }

// ======================================================================= gespeicherte Einstellungen

public sealed class EffectConfig
{
    public LightEffect Type { get; set; } = LightEffect.Static;
    public List<string> Colors { get; set; } = new() { "#FF0000" };
    /// <summary>Diashow: Sekunden pro Farbe. Atmen: Sekunden pro Atemzug.</summary>
    public double Seconds { get; set; } = 4;
    public bool Fade { get; set; } = true;
    /// <summary>Regenbogen: Umdrehungen pro 10 s.</summary>
    public double Speed { get; set; } = 1;
    public bool Wave { get; set; } = true;
    public ScreenRegion Region { get; set; } = ScreenRegion.All;
    /// <summary>Bildschirm: Farbverstärkung (1 = neutral).</summary>
    public double Boost { get; set; } = 1.3;
    /// <summary>Bildschirm: Glättung 0 (sofort) … 0.95 (sehr träge).</summary>
    public double Smooth { get; set; } = 0.45;

    public EffectConfig Clone() => new()
    {
        Type = Type, Colors = new List<string>(Colors), Seconds = Seconds, Fade = Fade, Speed = Speed,
        Wave = Wave, Region = Region, Boost = Boost, Smooth = Smooth
    };
}

public sealed class LightFanConfig
{
    public string? Name { get; set; }
    public bool FollowSync { get; set; } = true;
    public EffectConfig Effect { get; set; } = new();
}

public sealed class LightZoneConfig
{
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public int FanCount { get; set; } = 1;
    /// <summary>LED-Anzahl am ARGB-Anschluss (0 = Standard).</summary>
    public int LedCount { get; set; }
    /// <summary>Bildschirm-Effekt: eigener Bereich für diesen Anschluss (-1 = wie eingestellt).</summary>
    public int ScreenRegion { get; set; } = -1;
    public List<LightFanConfig> Fans { get; set; } = new();
}

public sealed class RgbConfig
{
    public bool Enabled { get; set; } = true;
    public bool Sync { get; set; } = true;
    public double Brightness { get; set; } = 1.0;
    public EffectConfig Global { get; set; } = new()
    {
        Type = LightEffect.Slideshow,
        Colors = new() { "#FF0000", "#0000FF", "#00FF00", "#FF00FF", "#00FFFF" },
        Seconds = 5,
        Fade = true
    };
    public Dictionary<string, LightZoneConfig> Zones { get; set; } = new();
}

// ======================================================================= Laufzeit (unveränderlich, für den Effekt-Thread)

public readonly struct RgbF
{
    public readonly double R, G, B;
    public RgbF(double r, double g, double b) { R = r; G = g; B = b; }
    public static readonly RgbF Black = new(0, 0, 0);

    public static RgbF FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Black;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)) return Black;
        return new(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }

    public static RgbF Lerp(RgbF a, RgbF b, double t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    public RgbF Scale(double f) => new(R * f, G * f, B * f);

    public byte R8 => (byte)Math.Clamp(Math.Round(R * 255), 0, 255);
    public byte G8 => (byte)Math.Clamp(Math.Round(G * 255), 0, 255);
    public byte B8 => (byte)Math.Clamp(Math.Round(B * 255), 0, 255);

    public static RgbF FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new(r + m, g + m, b + m);
    }

    public void ToHsv(out double h, out double s, out double v)
    {
        double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == R) h = 60 * (((G - B) / d) % 6);
        else if (max == G) h = 60 * ((B - R) / d + 2);
        else h = 60 * ((R - G) / d + 4);
        if (h < 0) h += 360;
    }
}

public sealed class EffectSnap
{
    public LightEffect Type { get; init; }
    public RgbF[] Colors { get; init; } = { RgbF.Black };
    public double Seconds { get; init; } = 4;
    public bool Fade { get; init; } = true;
    public double Speed { get; init; } = 1;
    public bool Wave { get; init; } = true;
    public ScreenRegion Region { get; init; }
    public double Boost { get; init; } = 1.4;
    public double Smooth { get; init; } = 0.6;

    public static EffectSnap From(EffectConfig c) => new()
    {
        Type = c.Type,
        Colors = c.Colors.Count == 0 ? new[] { RgbF.Black } : c.Colors.Select(RgbF.FromHex).ToArray(),
        Seconds = Math.Max(0.5, c.Seconds),
        Fade = c.Fade,
        Speed = c.Speed,
        Wave = c.Wave,
        Region = c.Region,
        Boost = c.Boost,
        Smooth = Math.Clamp(c.Smooth, 0, 0.97)
    };
}

public static class EffectMath
{
    /// <summary>Farbe eines Effekts zum Zeitpunkt t (Sekunden) an Position pos (0…1 innerhalb des Lüfters).</summary>
    public static RgbF Eval(EffectSnap e, double t, double pos, ScreenColors? screen)
    {
        switch (e.Type)
        {
            case LightEffect.Off:
                return RgbF.Black;

            case LightEffect.Static:
                return e.Colors[0];

            case LightEffect.Slideshow:
            {
                int n = e.Colors.Length;
                if (n == 1) return e.Colors[0];
                double period = e.Seconds;
                long idx = (long)Math.Floor(t / period);
                double frac = t / period - idx;
                var a = e.Colors[(int)(idx % n)];
                var b = e.Colors[(int)((idx + 1) % n)];
                if (!e.Fade) return a;
                // Farbe hält 55 % der Zeit, dann weiche Überblendung
                const double hold = 0.55;
                if (frac <= hold) return a;
                double f = (frac - hold) / (1 - hold);
                f = f * f * (3 - 2 * f); // smoothstep
                return RgbF.Lerp(a, b, f);
            }

            case LightEffect.Rainbow:
            {
                double hue = t * e.Speed * 36 + (e.Wave ? pos * 360 : 0);
                return RgbF.FromHsv(hue, 1, 1);
            }

            case LightEffect.Breathing:
            {
                int n = e.Colors.Length;
                long cycle = (long)Math.Floor(t / e.Seconds);
                double frac = t / e.Seconds - cycle;
                double level = 0.04 + 0.96 * (0.5 - 0.5 * Math.Cos(frac * 2 * Math.PI));
                return e.Colors[(int)(cycle % n)].Scale(level);
            }

            case LightEffect.Screen:
            {
                if (screen == null) return RgbF.Black;
                var c = screen.Get(e.Region);
                c.ToHsv(out var h, out var s, out var v);
                // Intensität: Farbe kräftiger und etwas heller
                s = Math.Clamp(s * e.Boost, 0, 1);
                v = Math.Clamp(v * (0.75 + 0.25 * e.Boost), 0, 1);
                return RgbF.FromHsv(h, s, v);
            }
        }
        return RgbF.Black;
    }
}
