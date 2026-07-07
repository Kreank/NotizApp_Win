using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NotizApp.Services;

public class AppSettings
{
    public string DataFolder { get; set; } = "";
    public bool HotkeyEnabled { get; set; } = true;
    public bool Autostart { get; set; } = false;
    /// <summary>Ziel-Notizbuch für die Schnellerfassung.</summary>
    public string QuickNotebook { get; set; } = "Kunden-Anrufe";
    /// <summary>Linke Seitenleiste (Notizbücher/Tags) eingeklappt.</summary>
    public bool SidebarZu { get; set; }
    /// <summary>Mittlere Spalte (Suche/Notizliste) eingeklappt.</summary>
    public bool ListeZu { get; set; }
    /// <summary>KI-Chat-Panel rechts eingeblendet.</summary>
    public bool ChatOffen { get; set; }
    /// <summary>Breite des KI-Chat-Panels in Pixeln.</summary>
    public double ChatBreite { get; set; } = 380;
    /// <summary>Farbe je Notizbuch (Name → Hex "#RRGGBB").</summary>
    public Dictionary<string, string> NotizbuchFarben { get; set; } = new();
}

/// <summary>
/// Lädt/speichert settings.json in %APPDATA%\NotizApp.
/// Env-Variable NOTIZAPP_APPDATA überschreibt den Ordner (für Tests).
/// </summary>
public class SettingsService
{
    public AppSettings Aktuell { get; private set; } = new();

    public static string SettingsOrdner
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("NOTIZAPP_APPDATA");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NotizApp");
        }
    }

    static string SettingsPfad => Path.Combine(SettingsOrdner, "settings.json");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public bool Lade()
    {
        try
        {
            if (!File.Exists(SettingsPfad)) return false;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPfad));
            if (s is null) return false;
            Aktuell = s;
            return !string.IsNullOrWhiteSpace(s.DataFolder);
        }
        catch
        {
            return false;
        }
    }

    public void Speichere()
    {
        Directory.CreateDirectory(SettingsOrdner);
        File.WriteAllText(SettingsPfad, JsonSerializer.Serialize(Aktuell, JsonOpts));
    }

    // ---------- Autostart (HKCU Run-Key) ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "NotizApp";

    /// <summary>Registry-Autostart setzen/entfernen und in den Settings vermerken.</summary>
    public void SetzeAutostart(bool an)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (an)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(RunName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
            }
            Aktuell.Autostart = an;
            Speichere();
        }
        catch
        {
            // Registry nicht beschreibbar → Einstellung bleibt unverändert
        }
    }
}
