using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickFanControl;

public enum FanMode
{
    Auto = 0,     // BIOS / Mainboard regelt selbst (z. B. CPU-Lüfter, Pumpe)
    Minimum = 1,  // App hält den Lüfter auf der niedrigsten Stufe, bei der er sich noch dreht
    Pump = 2,     // Wasserkühlungs-Pumpe: feste Leistung (Standard 30 %)
    Manual = 3,   // Feste Leistung, vom Nutzer eingestellt
    Curve = 4     // App regelt nach CPU-Temperatur (kühl = leise, warm = schnell)
}

public sealed class FanSetting
{
    public string? CustomName { get; set; }
    public FanMode Mode { get; set; } = FanMode.Auto;
    /// <summary>Kalibrierter Mindestwert in %. -1 = noch nicht kalibriert.</summary>
    public int MinPercent { get; set; } = -1;
    /// <summary>Feste Leistung für Pumpen in %.</summary>
    public int PumpPercent { get; set; } = 30;
    /// <summary>Feste Leistung im Modus "Manuell" in %.</summary>
    public int ManualPercent { get; set; } = 50;
    /// <summary>Temperatur-Kurve: Mindestleistung in %.</summary>
    public int CurveMinPercent { get; set; } = 30;
    /// <summary>Temperatur-Kurve: bis hierhin (°C) Mindestleistung.</summary>
    public int CurveLowC { get; set; } = 50;
    /// <summary>Temperatur-Kurve: ab hier (°C) 100 %.</summary>
    public int CurveHighC { get; set; } = 80;
    /// <summary>true, wenn der Nutzer den Modus dieses Lüfters selbst gewählt hat.</summary>
    public bool UserChosen { get; set; }
}

public sealed class AppConfig
{
    public Dictionary<string, FanSetting> Fans { get; set; } = new();
    public RgbConfig Rgb { get; set; } = new();
    public bool Autostart { get; set; } = true;
    public bool Paused { get; set; }
    /// <summary>Ab dieser CPU-/GPU-Temperatur gibt die App alle Lüfter ans BIOS zurück.</summary>
    public int SafetyTempC { get; set; } = 85;
    /// <summary>Unter dieser Temperatur übernimmt die App wieder.</summary>
    public int SafetyResumeC { get; set; } = 75;
    /// <summary>Ab dieser Temperatur drehen auch die Minimum-Lüfter stufenlos hoch.</summary>
    public int RampStartC { get; set; } = 70;
    public bool TrayHintShown { get; set; }
    public bool FirstRunDone { get; set; }
    public int ConfigVersion { get; set; }
    /// <summary>Erst wenn der Nutzer die Lüfter zugeordnet hat, wird automatisch kalibriert.</summary>
    public bool FanSetupConfirmed { get; set; }
    /// <summary>Auch Anschlüsse ohne Lüfter anzeigen.</summary>
    public bool ShowEmptyFans { get; set; }
    /// <summary>Gewählter Sensor für die CPU-Anzeige (null = automatisch: Ø P-Kerne).</summary>
    public string? CpuSensorId { get; set; }
    /// <summary>Gewählter Sensor für die GPU-Anzeige (null = automatisch: GPU Core).</summary>
    public string? GpuSensorId { get; set; }

    [JsonIgnore]
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NickFanControl");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg != null)
                {
                    cfg.Fans ??= new();
                    cfg.Rgb ??= new();
                    cfg.Rgb.Zones ??= new();
                    cfg.Rgb.Global ??= new();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Config konnte nicht geladen werden: " + ex.Message);
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Write("Config konnte nicht gespeichert werden: " + ex.Message);
        }
    }

    public FanSetting Get(string id)
    {
        if (!Fans.TryGetValue(id, out var s))
        {
            s = new FanSetting();
            Fans[id] = s;
        }
        return s;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Folder);
                var file = Path.Combine(AppConfig.Folder, "log.txt");
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Move(file, file + ".old", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
            }
        }
        catch { /* Logging darf nie die App crashen */ }
    }
}
