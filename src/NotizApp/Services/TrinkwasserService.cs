using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotizApp.Services;

// =====================================================================================
//  Trinkwasser-Erfassung (DU / Berechnungsdurchfluss)
//  Sammelt Entnahmestellen je Vorhaben, bildet die Summen und – optional – den
//  Spitzenvolumenstrom nach DIN 1988-300 (Vs = a · (ΣVR)^b − c). Die Werte dienen als
//  saubere Eingangsdaten für Viptool Master. Alle Norm-/Formelwerte sind überschreibbar.
// =====================================================================================

/// <summary>Eine Entnahmestelle. Kalt/Warm sind Einzeldurchflüsse (DU/VR) in l/s je Stück.</summary>
public class DuZeile : INotifyPropertyChanged
{
    string _bezeichnung = "";
    double _kalt;
    double _warm;
    int _anzahl = 1;
    string _quelle = "";

    /// <summary>Bezeichnung der Entnahmestelle (z. B. „Waschtischbatterie").</summary>
    public string Bezeichnung { get => _bezeichnung; set => Setze(ref _bezeichnung, value); }

    /// <summary>Einzeldurchfluss Kaltwasser (PWC) in l/s.</summary>
    public double Kalt { get => _kalt; set => Setze(ref _kalt, value); }

    /// <summary>Einzeldurchfluss Warmwasser (PWH) in l/s.</summary>
    public double Warm { get => _warm; set => Setze(ref _warm, value); }

    /// <summary>Anzahl gleicher Entnahmestellen.</summary>
    public int Anzahl { get => _anzahl; set => Setze(ref _anzahl, value < 0 ? 0 : value); }

    /// <summary>Herkunft des Wertes: "" = DIN-Standard, sonst Herstellername.</summary>
    public string Quelle { get => _quelle; set => Setze(ref _quelle, value); }

    [JsonIgnore] public double SummeKalt => Kalt * Anzahl;
    [JsonIgnore] public double SummeWarm => Warm * Anzahl;
    [JsonIgnore] public double SummeGesamt => (Kalt + Warm) * Anzahl;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        // Summen hängen an mehreren Feldern → immer mitmelden
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummeKalt)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummeWarm)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummeGesamt)));
    }

    public DuZeile Kopie() => new()
    {
        Bezeichnung = Bezeichnung, Kalt = Kalt, Warm = Warm, Anzahl = Anzahl, Quelle = Quelle,
    };
}

/// <summary>Ein Erfassungsvorhaben (z. B. eine Baustelle/ein Objekt) mit seinen Zeilen.</summary>
public class TwVorhaben
{
    public string Name { get; set; } = "Neues Vorhaben";

    /// <summary>Gebäudeart – bestimmt die Vorbelegung der Spitzenvolumenstrom-Formel.</summary>
    public string Gebaeudeart { get; set; } = "Wohngebäude / Seniorenheim";

    /// <summary>Formelkoeffizienten Vs = a·(ΣVR)^b − c (überschreibbar, DIN 1988-300 Tab. 3).</summary>
    public double KoeffA { get; set; } = 1.48;
    public double KoeffB { get; set; } = 0.19;
    public double KoeffC { get; set; } = 0.94;

    public string Bemerkung { get; set; } = "";

    public List<DuZeile> Zeilen { get; set; } = new();
}

/// <summary>Gesamter Datenbestand des Trinkwasser-Tools (alle Vorhaben).</summary>
public class TwDaten
{
    public List<TwVorhaben> Vorhaben { get; set; } = new();
}

/// <summary>Vordefinierte Entnahmestelle für den Schnell-Eintrag (Standard-DU-Tabelle).</summary>
public record DuVorlage(string Bezeichnung, double Kalt, double Warm);

/// <summary>Vorbelegung der Spitzenvolumenstrom-Formel je Gebäudeart.</summary>
public record GebaeudeVorlage(string Name, double A, double B, double C);

/// <summary>
/// Lädt/speichert die Trinkwasser-Vorhaben unter %APPDATA%\NotizApp\auslegung\trinkwasser.json
/// und stellt den Standard-DU-Katalog sowie die Gebäudearten bereit.
/// </summary>
public class TrinkwasserService
{
    static string Ordner => Path.Combine(SettingsService.SettingsOrdner, "auslegung");
    static string Pfad => Path.Combine(Ordner, "trinkwasser.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TwDaten Lade()
    {
        try
        {
            if (!File.Exists(Pfad)) return new TwDaten();
            return JsonSerializer.Deserialize<TwDaten>(File.ReadAllText(Pfad)) ?? new TwDaten();
        }
        catch
        {
            return new TwDaten();
        }
    }

    public void Speichere(TwDaten daten)
    {
        Directory.CreateDirectory(Ordner);
        File.WriteAllText(Pfad, JsonSerializer.Serialize(daten, JsonOpts));
    }

    // ---------- Standard-DU-Tabelle (Berechnungsdurchfluss, l/s) ----------
    // Übliche/„schlechteste" Normwerte in Anlehnung an DIN 1988-300 Tab. 2.
    // Alle Werte in der App überschreibbar – hier nur als schneller Standard-Vorschlag.

    // Werte = Berechnungsdurchflüsse VR nach DIN 1988-300 Tab. 3 (aktuelle Werte,
    // nicht die alten DIN-1988-3-Werte). Auslaufventile sind Einzelzapfstellen → nur kalt.

    public static readonly IReadOnlyList<DuVorlage> Standardkatalog = new List<DuVorlage>
    {
        new("Waschtischbatterie",              0.07, 0.07),
        new("Spültischbatterie (Küche)",       0.07, 0.07),
        new("Bidet / Sitzwaschbecken",         0.07, 0.07),
        new("Duschbatterie",                   0.15, 0.15),
        new("Wannenbatterie (Badewanne)",      0.15, 0.15),
        new("WC-Spülkasten (Füllventil)",      0.13, 0.00),
        new("WC-Druckspüler (DN 20)",          1.00, 0.00),
        new("Urinal-Druckspüler",              0.30, 0.00),
        new("Haushalts-Geschirrspüler",        0.07, 0.00),
        new("Haushalts-Waschmaschine",         0.15, 0.00),
        new("Auslaufventil m. Strahlregler DN 10/15", 0.15, 0.00),
        new("Auslaufventil o. Strahlregler DN 15", 0.30, 0.00),
        new("Auslaufventil o. Strahlregler DN 20", 0.50, 0.00),
        new("Auslaufventil o. Strahlregler DN 25", 1.00, 0.00),
    };

    // ---------- Gebäudearten (Spitzenvolumenstrom-Koeffizienten) ----------
    // Vs = a·(ΣVR)^b − c  (DIN 1988-300:2012-05, Abb. 1 / Tab. 4).
    // Verifizierte Konstanten je Gebäudetyp.

    public static readonly IReadOnlyList<GebaeudeVorlage> Gebaeudearten = new List<GebaeudeVorlage>
    {
        new("Wohngebäude / Seniorenheim",   1.48, 0.19, 0.94),
        new("Pflegeheim",                   1.40, 0.14, 0.92),
        new("Bettenhaus im Krankenhaus",    0.75, 0.44, 0.18),
        new("Hotel",                        0.70, 0.48, 0.13),
        new("Schule / Verwaltungsgebäude",  0.91, 0.31, 0.38),
        new("Eigene Werte",                 1.48, 0.19, 0.94),
    };

    /// <summary>Spitzenvolumenstrom Vs = a·(ΣVR)^b − c (l/s). Nie negativ.</summary>
    public static double Spitzenvolumenstrom(double summeVr, double a, double b, double c)
    {
        if (summeVr <= 0) return 0;
        var vs = a * Math.Pow(summeVr, b) - c;
        return vs < 0 ? 0 : vs;
    }
}
