using System.IO;
using LibreHardwareMonitor.Hardware;

namespace NickFanControl;

/// <summary>Ein steuerbarer Lüfteranschluss am Mainboard (Control-Sensor + passender Drehzahl-Sensor).</summary>
public sealed class FanChannel
{
    public required IHardware Hardware { get; init; }
    public required ISensor Control { get; init; }
    public ISensor? Fan { get; init; }

    public string Id => Control.Identifier.ToString();
    public string DefaultName => Fan?.Name ?? Control.Name;
    public string ChipName => Hardware.Name;

    public float? Rpm => Fan?.Value;
    public float? Percent => Control.Value;

    /// <summary>true, solange Kalibrierung/Identifizieren läuft – dann regelt der Hauptloop diesen Lüfter nicht.</summary>
    public bool Busy { get; set; }
    /// <summary>true, wenn die App gerade einen Software-Wert gesetzt hat.</summary>
    public bool SoftwareActive { get; set; }
    public float LastTarget { get; set; } = -1;
    public DateTime TargetSince { get; set; }
    public string Status { get; set; } = "";
    public int StallTicks { get; set; }
    /// <summary>true, sobald an diesem Anschluss einmal eine Drehzahl gemessen wurde (= Lüfter angeschlossen).</summary>
    public bool SeenSpinning { get; set; }
}

public sealed class FanManager : IDisposable
{
    private readonly Computer _computer;
    public List<FanChannel> Channels { get; } = new();
    public bool Opened { get; private set; }
    public string? OpenError { get; private set; }

    public FanManager()
    {
        _computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsControllerEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false
        };
    }

    public void Open()
    {
        try
        {
            _computer.Open();
            Opened = true;
            UpdateAll();
            Discover();
            Log.Write($"Hardware geöffnet, {Channels.Count} steuerbare Lüfter gefunden.");
        }
        catch (Exception ex)
        {
            OpenError = ex.Message;
            Log.Write("Hardware konnte nicht geöffnet werden: " + ex);
        }
    }

    private void Discover()
    {
        Channels.Clear();
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
        {
            foreach (var sub in Flatten(hw))
            {
                var fans = sub.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
                foreach (var ctrl in sub.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null)
                                                 .OrderBy(s => s.Index))
                {
                    var fan = fans.FirstOrDefault(f => f.Index == ctrl.Index);
                    Channels.Add(new FanChannel { Hardware = sub, Control = ctrl, Fan = fan });
                }
            }
        }
    }

    private static IEnumerable<IHardware> Flatten(IHardware hw)
    {
        yield return hw;
        foreach (var sub in hw.SubHardware)
            foreach (var x in Flatten(sub))
                yield return x;
    }

    /// <summary>Schützt den Hardware-Zugriff (Sensoren lesen läuft im Hintergrund, Lüfter setzen im UI-Thread).</summary>
    public readonly object HwLock = new();

    /// <summary>
    /// true beim Herunterfahren/Neustart: dann NICHT erst auf 100 % stellen – sonst bleibt der Chip
    /// bei einem Neustart auf 100 % stehen und die Lüfter laufen bis Windows auf Vollgas.
    /// </summary>
    public bool ShuttingDown { get; set; }

    public void UpdateAll()
    {
        if (!Opened) return;
        lock (HwLock)
        foreach (var hw in _computer.Hardware)
            foreach (var h in Flatten(hw))
            {
                try { h.Update(); } catch { /* einzelne Sensoren dürfen ausfallen */ }
            }
    }

    /// <summary>Alle echten Temperatursensoren (ohne "Distance to TjMax" usw.).</summary>
    public IEnumerable<(string Id, string Label, HardwareType Type)> TemperatureSensors()
    {
        foreach (var hw in _computer.Hardware)
            foreach (var h in Flatten(hw))
                foreach (var s in h.Sensors.Where(IsRealTemp).OrderBy(s => s.Index))
                    yield return (s.Identifier.ToString(), $"{s.Name}  ·  {ShortHw(h)}", hw.HardwareType);
    }

    private static string ShortHw(IHardware h) => h.Name.Length > 28 ? h.Name[..28] + "…" : h.Name;

    private static bool IsRealTemp(ISensor s) =>
        s.SensorType == SensorType.Temperature &&
        !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase) &&
        !s.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase) &&
        !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plausibler Wert? (0 °C oder 120+ °C sind Lesefehler)</summary>
    private static bool Plausible(float? v) => v is > 5 and < 115;

    private ISensor? FindSensor(string id)
    {
        foreach (var hw in _computer.Hardware)
            foreach (var h in Flatten(hw))
                foreach (var s in h.Sensors)
                    if (s.Identifier.ToString() == id) return s;
        return null;
    }

    /// <summary>
    /// CPU-Temperatur. Standard: Durchschnitt der P-Kerne (Einzelwerte springen zu sehr),
    /// sonst Durchschnitt aller Kerne, sonst CPU Package / Tctl.
    /// </summary>
    public float? CpuTemp(string? sensorId)
    {
        if (!string.IsNullOrEmpty(sensorId))
        {
            var sel = FindSensor(sensorId);
            if (sel != null) return Plausible(sel.Value) ? sel.Value : null;
        }

        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            var temps = hw.Sensors.Where(s => IsRealTemp(s) && Plausible(s.Value)).ToList();

            var pCores = temps.Where(s => s.Name.StartsWith("P-Core", StringComparison.OrdinalIgnoreCase) &&
                                          s.Name.Contains('#')).ToList();
            if (pCores.Count > 0) return pCores.Average(s => s.Value!.Value);

            var cores = temps.Where(s => s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cores.Count > 0) return cores.Average(s => s.Value!.Value);

            var avg = temps.FirstOrDefault(s => s.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase));
            if (avg != null) return avg.Value;

            var pkg = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                   ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase));
            if (pkg != null) return pkg.Value;
        }
        return null;
    }

    /// <summary>GPU-Temperatur. Standard: "GPU Core" (nicht Hot Spot / Speicher).</summary>
    public float? GpuTemp(string? sensorId)
    {
        if (!string.IsNullOrEmpty(sensorId))
        {
            var sel = FindSensor(sensorId);
            if (sel != null) return Plausible(sel.Value) ? sel.Value : null;
        }

        float? best = null;
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd))
        {
            var temps = hw.Sensors.Where(s => IsRealTemp(s) && Plausible(s.Value)).ToList();
            var t = temps.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                 ?? temps.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                                              !s.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase))
                 ?? temps.FirstOrDefault();
            if (t?.Value != null) best = best == null ? t.Value : Math.Max(best.Value, t.Value.Value);
        }
        return best;
    }

    /// <summary>Schreibt alle Sensoren mit aktuellen Werten in eine Textdatei (zur Kontrolle).</summary>
    public void DumpSensors(string path, float? cpuShown, float? gpuShown)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Nick Fan Control – Sensorliste vom {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Angezeigt: CPU {cpuShown?.ToString("0.0") ?? "–"} °C, GPU {gpuShown?.ToString("0.0") ?? "–"} °C");
        sb.AppendLine();
        foreach (var hw in _computer.Hardware)
            foreach (var h in Flatten(hw))
            {
                sb.AppendLine($"[{h.HardwareType}] {h.Name}");
                foreach (var s in h.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control)
                                           .OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                    sb.AppendLine($"   {s.SensorType,-12} {s.Name,-32} {s.Value?.ToString("0.0") ?? "–",8}    {s.Identifier}");
                sb.AppendLine();
            }
        File.WriteAllText(path, sb.ToString());
    }

    public void SetPercent(FanChannel ch, float percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            lock (HwLock) ch.Control.Control!.SetSoftware(percent);
            ch.SoftwareActive = true;
            if (Math.Abs(ch.LastTarget - percent) > 0.1f)
            {
                ch.LastTarget = percent;
                ch.TargetSince = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            ch.Status = "Fehler beim Setzen";
            Log.Write($"SetSoftware({ch.Id}, {percent}) fehlgeschlagen: {ex.Message}");
        }
    }

    public void SetAuto(FanChannel ch)
    {
        // Sicherheitsnetz: Manche Mainboards (z. B. Gigabyte mit ITE-Chip) übernehmen die Rückgabe ans BIOS
        // nicht zuverlässig. Deshalb erst auf 100 % – klappt die Rückgabe nicht, läuft der Lüfter wenigstens voll.
        if (ch.SoftwareActive && !ShuttingDown)
        {
            try { lock (HwLock) ch.Control.Control!.SetSoftware(100); } catch { }
        }
        try { lock (HwLock) ch.Control.Control!.SetDefault(); }
        catch (Exception ex) { Log.Write($"SetDefault({ch.Id}) fehlgeschlagen: {ex.Message}"); }
        ch.SoftwareActive = false;
        ch.LastTarget = -1;
    }

    /// <summary>Gibt alle Lüfter ans BIOS zurück (beim Beenden, Abmelden, Sicherheitsmodus).</summary>
    public void RestoreAll()
    {
        foreach (var ch in Channels) SetAuto(ch);
    }

    /// <summary>Liest die Drehzahl ein paar Mal und mittelt (Tachosignale schwanken).</summary>
    public async Task<float> MeasureRpmAsync(FanChannel ch, CancellationToken ct, int samples = 3)
    {
        float sum = 0;
        int n = 0;
        for (int i = 0; i < samples; i++)
        {
            if (i > 0) await Task.Delay(500, ct);
            await Task.Run(() => { try { lock (HwLock) ch.Hardware.Update(); } catch { } }, ct);
            if (ch.Rpm is float r) { sum += r; n++; }
        }
        return n == 0 ? 0 : sum / n;
    }

    public void Dispose()
    {
        try { RestoreAll(); } catch { }
        try { lock (HwLock) _computer.Close(); } catch { }
    }
}
