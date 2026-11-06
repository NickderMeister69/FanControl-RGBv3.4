using System.Runtime.InteropServices;

namespace NickFanControl;

/// <summary>Durchschnittsfarben der Bildschirmbereiche (wird vom Sampler ständig aktualisiert).</summary>
public sealed class ScreenColors
{
    private readonly RgbF[] _c = new RgbF[6];
    private readonly object _gate = new();

    public RgbF Get(ScreenRegion r) { lock (_gate) return _c[(int)r]; }
    public void Set(RgbF[] values) { lock (_gate) Array.Copy(values, _c, 6); }
}

/// <summary>
/// Liest den Hauptbildschirm stark verkleinert (96×54) per GDI aus und berechnet daraus Farben.
/// Läuft nur, solange ein Effekt "Bildschirm" aktiv ist. Hinweis: Spiele im exklusiven Vollbild
/// lassen sich so nicht auslesen – dort "Randloses Fenster" verwenden.
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    private const int W = 96, H = 54;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile bool _active;
    public ScreenColors Colors { get; } = new();

    public bool Active { get => _active; set => _active = value; }

    public ScreenSampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ScreenSampler", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero, bits = IntPtr.Zero;
        var pixels = new byte[W * H * 4];
        var sums = new double[6, 4];
        var result = new RgbF[6];
        DxgiCapture? dx = null;
        DateTime dxRetry = DateTime.MinValue;
        int dxErrors = 0;

        while (_running)
        {
            if (!_active)
            {
                if (dx != null) { dx.Dispose(); dx = null; }
                Thread.Sleep(200);
                continue;
            }
            // 1) DXGI (Grafikkarte) – klappt auch bei vielen Vollbild-Spielen
            var now = DateTime.Now;
            if (dx == null && now >= dxRetry)
            {
                try { dx = new DxgiCapture(); dx.Init(); }
                catch (Exception ex)
                {
                    dx?.Dispose(); dx = null; dxRetry = now.AddSeconds(15);
                    Log.Write("DXGI-Erfassung nicht verfügbar, nutze GDI: " + ex.Message);
                }
            }
            if (dx != null)
            {
                try
                {
                    if (dx.TryCapture(pixels, W, H)) Compute(pixels, sums, result);
                    Thread.Sleep(4);
                    continue;
                }
                catch (Exception ex)
                {
                    // z. B. Auflösungswechsel, UAC-Dialog, Sperrbildschirm → neu aufbauen
                    dx.Dispose(); dx = null; dxRetry = now.AddSeconds(2);
                    if (++dxErrors % 20 == 1) Log.Write("DXGI-Erfassung neu gestartet: " + ex.Message);
                }
            }

            // 2) Fallback: GDI
            try
            {
                if (memDc == IntPtr.Zero)
                {
                    screenDc = GetDC(IntPtr.Zero);
                    memDc = CreateCompatibleDC(screenDc);
                    var bi = new BITMAPINFO
                    {
                        biSize = 40, biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32
                    };
                    bmp = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
                    old = SelectObject(memDc, bmp);
                    SetStretchBltMode(memDc, 4); // HALFTONE: mittelt die Pixel → ruhigere Farben
                    SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
                }

                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                StretchBlt(memDc, 0, 0, W, H, screenDc, 0, 0, sw, sh, 0x00CC0020 /*SRCCOPY*/);
                GdiFlush();
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                Compute(pixels, sums, result);
            }
            catch (Exception ex)
            {
                Log.Write("Bildschirm auslesen fehlgeschlagen: " + ex.Message);
                Release(ref screenDc, ref memDc, ref bmp, old);
                Thread.Sleep(1000);
            }
            Thread.Sleep(50); // ~20 Bilder pro Sekunde
        }
        dx?.Dispose();
        Release(ref screenDc, ref memDc, ref bmp, old);
    }

    // Bereiche: Ganz, Links, Rechts, Oben, Unten, Mitte (x0, y0, x1, y1 als Anteil)
    private static readonly (double x0, double y0, double x1, double y1)[] Regions =
    {
        (0, 0, 1, 1), (0, 0, 1 / 3.0, 1), (2 / 3.0, 0, 1, 1), (0, 0, 1, 1 / 3.0), (0, 2 / 3.0, 1, 1), (0.25, 0.25, 0.75, 0.75)
    };
    private const int HueBins = 24;

    /// <summary>
    /// Ermittelt je Bereich die prägende Farbe (wie Ambilight/SignalRGB):
    /// Farbton = häufigste kräftige Farbe, Helligkeit = wie hell die Szene wirklich ist (dunkle Szenen bleiben dunkel).
    /// </summary>
    private void Compute(byte[] pixels, double[,] sums, RgbF[] result)
    {
        var hist = new double[HueBins];
        var hr = new double[HueBins]; var hg = new double[HueBins]; var hb = new double[HueBins];
        for (int k = 0; k < Regions.Length; k++)
        {
            var (fx0, fy0, fx1, fy1) = Regions[k];
            int x0 = (int)(fx0 * W), x1 = (int)(fx1 * W), y0 = (int)(fy0 * H), y1 = (int)(fy1 * H);
            Array.Clear(hist); Array.Clear(hr); Array.Clear(hg); Array.Clear(hb);
            double lumSum = 0, ar = 0, ag = 0, ab = 0; int n = 0;

            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 4;
                    double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    lumSum += lum; ar += r; ag += g; ab += b; n++;
                    if (max < 0.08) continue;                 // fast schwarz: zählt nicht für die Farbe
                    double sat = (max - min) / max;
                    if (sat < 0.18) continue;                 // grau/weiß: zählt nicht für den Farbton
                    double hue;
                    double d = max - min;
                    if (max == r) hue = 60 * (((g - b) / d) % 6);
                    else if (max == g) hue = 60 * ((b - r) / d + 2);
                    else hue = 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                    int bin = (int)(hue / (360.0 / HueBins)) % HueBins;
                    double w = sat * sat * max;               // kräftige, helle Farben zählen am meisten
                    hist[bin] += w; hr[bin] += r * w; hg[bin] += g * w; hb[bin] += b * w;
                }

            if (n == 0) { result[k] = RgbF.Black; continue; }
            double avgLum = lumSum / n;
            // Helligkeit: dunkle Szenen dunkel, helle hell (leicht angehoben, damit es wirkt)
            double level = Math.Clamp(Math.Sqrt(avgLum) * 1.3, 0, 1);
            if (level < 0.04) level = 0;

            // stärkster Farbton (inkl. Nachbarn)
            int best = -1; double bestW = 0, total = 0;
            for (int i = 0; i < HueBins; i++)
            {
                total += hist[i];
                double w = hist[i] + 0.5 * (hist[(i + 1) % HueBins] + hist[(i + HueBins - 1) % HueBins]);
                if (w > bestW) { bestW = w; best = i; }
            }

            RgbF color;
            if (best < 0 || total < n * 0.004)
            {
                // kaum Farbe im Bild (z. B. Text, Desktop, Schnee) → neutrales Licht in Bildfarbe
                double m = Math.Max(ar, Math.Max(ag, ab));
                color = m <= 0 ? RgbF.Black : new RgbF(ar / m, ag / m, ab / m);
            }
            else
            {
                double cw = 0, cr = 0, cg = 0, cb = 0;
                foreach (int j in new[] { (best + HueBins - 1) % HueBins, best, (best + 1) % HueBins })
                { cw += hist[j]; cr += hr[j]; cg += hg[j]; cb += hb[j]; }
                cr /= cw; cg /= cw; cb /= cw;
                double m = Math.Max(cr, Math.Max(cg, cb));
                color = m <= 0 ? RgbF.Black : new RgbF(cr / m, cg / m, cb / m); // auf volle Helligkeit normiert
            }
            result[k] = color.Scale(level);
        }
        Colors.Set(result);
    }

    private static void Add(double[,] s, int k, double r, double g, double b, double w)
    {
        s[k, 0] += r * w; s[k, 1] += g * w; s[k, 2] += b * w; s[k, 3] += w;
    }

    private static void Release(ref IntPtr screenDc, ref IntPtr memDc, ref IntPtr bmp, IntPtr old)
    {
        if (memDc != IntPtr.Zero)
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            DeleteDC(memDc);
        }
        if (bmp != IntPtr.Zero) DeleteObject(bmp);
        if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        screenDc = memDc = bmp = IntPtr.Zero;
    }

    public void Dispose() => _running = false;

    // ------------------------------------------------------------------ GDI

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr pt);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
