using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotizApp.Services;

// =====================================================================================
//  Erfassung = vollständige SHK-Bestandsaufnahme vor Ort als Eingangsdaten für Viptool Master.
//  Ein Vorhaben hält den GEMEINSAMEN Gebäude-Kopf und die GEMEINSAME Teilstrecken-Topologie
//  (Wasser/Abwasser/Gas teilen sich die Leitungen) und darunter je Gewerk die Details.
//  Erste vollständige Ausbaustufe: Trinkwasser. Abwasser/Heizung/Gas folgen im selben Modell.
//  (Namensraum "Erfassung", weil AufnahmeService bereits die Audio-Aufnahme belegt.)
// =====================================================================================

/// <summary>Kleine INotify-Basis mit Setter-Helfer (meldet zusätzlich globale Änderung).</summary>
public abstract class ErfassungBasis : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Feuert bei jeder Feldänderung – das Tool hängt hier Autosave/Neuberechnung ein.</summary>
    [JsonIgnore] public Action? Geaendert { get; set; }

    protected bool Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return false;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        NachAenderung();
        Geaendert?.Invoke();
        return true;
    }

    /// <summary>Hook: nach jeder Feldänderung – z. B. um abhängige Anzeige-Properties zu melden.</summary>
    protected virtual void NachAenderung() { }

    /// <summary>Abgeleitete Klassen melden hierüber ein zusätzliches (berechnetes) Property.</summary>
    protected void Melde(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Gebäude-/Projektkopf – einmal je Vorhaben, gilt für alle Gewerke.</summary>
public class GebaeudeKopf : ErfassungBasis
{
    string _adresse = "", _kunde = "", _gebaeudeart = "Wohngebäude / Seniorenheim",
           _baujahr = "", _zustand = "", _standort = "", _bemerkung = "";
    int _geschosse = 1, _nutzeinheiten = 1, _personen;
    bool _keller, _dachgeschoss;
    double _geschosshoehe = 2.5, _flaeche, _aussentemp = -12, _einspeisedruck;

    /// <summary>Einspeisedruck / Mindestversorgungsdruck WVU (bar) – gilt für die Trinkwasserberechnung.</summary>
    public double Einspeisedruck { get => _einspeisedruck; set => Setze(ref _einspeisedruck, value); }
    public string Adresse { get => _adresse; set => Setze(ref _adresse, value); }
    public string Kunde { get => _kunde; set => Setze(ref _kunde, value); }
    public string Gebaeudeart { get => _gebaeudeart; set => Setze(ref _gebaeudeart, value); }
    public string Baujahr { get => _baujahr; set => Setze(ref _baujahr, value); }
    public string Zustand { get => _zustand; set => Setze(ref _zustand, value); }
    public int Geschosse { get => _geschosse; set => Setze(ref _geschosse, value); }
    public bool Keller { get => _keller; set => Setze(ref _keller, value); }
    public bool Dachgeschoss { get => _dachgeschoss; set => Setze(ref _dachgeschoss, value); }
    public double Geschosshoehe { get => _geschosshoehe; set => Setze(ref _geschosshoehe, value); }
    public double BeheizteFlaeche { get => _flaeche; set => Setze(ref _flaeche, value); }
    public int Nutzeinheiten { get => _nutzeinheiten; set => Setze(ref _nutzeinheiten, value); }
    public int Personen { get => _personen; set => Setze(ref _personen, value); }
    public string Standort { get => _standort; set => Setze(ref _standort, value); }
    public double NormAussentemp { get => _aussentemp; set => Setze(ref _aussentemp, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }
}

/// <summary>Trinkwasser-Randbedingungen am Übergabepunkt (die oft vergessenen Werte).</summary>
public class TwRandbedingungen : ErfassungBasis
{
    double _versorgungsdruck, _zaehlerDp, _enthaertungDp, _hinterdruck, _filterDp;
    string _zaehler = "", _twe = "", _hausanschluss = "", _bemerkung = "";
    bool _filter, _enthaertung, _druckminderer, _systemtrenner, _zirkulation, _dea;

    /// <summary>Mindestversorgungsdruck des WVU (bar) – beim Versorger erfragen!</summary>
    public double Versorgungsdruck { get => _versorgungsdruck; set => Setze(ref _versorgungsdruck, value); }
    public string Hausanschluss { get => _hausanschluss; set => Setze(ref _hausanschluss, value); }
    public string Zaehler { get => _zaehler; set => Setze(ref _zaehler, value); }
    public double ZaehlerDp { get => _zaehlerDp; set => Setze(ref _zaehlerDp, value); }
    public bool Filter { get => _filter; set => Setze(ref _filter, value); }
    public double FilterDp { get => _filterDp; set => Setze(ref _filterDp, value); }
    public bool Enthaertung { get => _enthaertung; set => Setze(ref _enthaertung, value); }
    public double EnthaertungDp { get => _enthaertungDp; set => Setze(ref _enthaertungDp, value); }
    public bool Druckminderer { get => _druckminderer; set => Setze(ref _druckminderer, value); }
    public double Hinterdruck { get => _hinterdruck; set => Setze(ref _hinterdruck, value); }
    public bool Systemtrenner { get => _systemtrenner; set => Setze(ref _systemtrenner, value); }
    /// <summary>Trinkwassererwärmer: Typ/Volumen/Leistung (Freitext).</summary>
    public string Twe { get => _twe; set => Setze(ref _twe, value); }
    public bool Zirkulation { get => _zirkulation; set => Setze(ref _zirkulation, value); }
    /// <summary>Druckerhöhungsanlage nötig/vorhanden.</summary>
    public bool Dea { get => _dea; set => Setze(ref _dea, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }

    string _wwKonzept = "zentral mit Zirkulation";
    /// <summary>Warmwasser-Konzept: zentral m. Zirkulation / zentral o. Zirkulation / dezentral.</summary>
    public string WwKonzept { get => _wwKonzept; set => Setze(ref _wwKonzept, value); }
}

/// <summary>Ein Ort/Raum innerhalb einer Wohneinheit (Bad, Küche, Gäste-WC …) mit
/// gezählten Entnahmestellen.</summary>
public class Ort : ErfassungBasis
{
    string _name = "";
    int _wt, _dusche, _wanne, _wc, _wm, _gsw, _kueche;

    public string Name { get => _name; set => Setze(ref _name, value); }
    public int Waschtisch { get => _wt; set => Setze(ref _wt, value < 0 ? 0 : value); }
    public int Dusche { get => _dusche; set => Setze(ref _dusche, value < 0 ? 0 : value); }
    public int Wanne { get => _wanne; set => Setze(ref _wanne, value < 0 ? 0 : value); }
    public int Wc { get => _wc; set => Setze(ref _wc, value < 0 ? 0 : value); }
    public int Waschmaschine { get => _wm; set => Setze(ref _wm, value < 0 ? 0 : value); }
    public int Geschirrspueler { get => _gsw; set => Setze(ref _gsw, value < 0 ? 0 : value); }
    /// <summary>Küchenspüle.</summary>
    public int Kueche { get => _kueche; set => Setze(ref _kueche, value < 0 ? 0 : value); }
}

/// <summary>Eine Wohneinheit mit Orten (Bad/Küche/…). „Anzahl" bündelt mehrere
/// gleiche Wohnungen (z. B. 8 identische Wohnungen an einem Strang).</summary>
public class Wohneinheit : ErfassungBasis
{
    string _name = "", _werkstoff = "", _nennweite = "";
    int _anzahl = 1, _boegen, _tstuecke, _reduzierungen;
    double _laenge, _hoehe;

    public string Name { get => _name; set => Setze(ref _name, value); }
    /// <summary>Anzahl gleicher Wohnungen dieses Typs.</summary>
    public int Anzahl { get => _anzahl; set => Setze(ref _anzahl, value < 1 ? 1 : value); }
    /// <summary>Orte/Räume der Wohnung mit ihren Entnahmestellen.</summary>
    public ObservableCollection<Ort> Orte { get; set; } = new();

    // ---- Anschlussleitung zur Wohneinheit (vom Strang/Schacht) ----
    /// <summary>Länge der Anschlussleitung (m) – vom Strang/Schacht bis zur Wohnung.</summary>
    public double Laenge { get => _laenge; set => Setze(ref _laenge, value); }
    /// <summary>Geodätischer Höhenunterschied (m).</summary>
    public double Hoehe { get => _hoehe; set => Setze(ref _hoehe, value); }
    public int Boegen { get => _boegen; set => Setze(ref _boegen, value < 0 ? 0 : value); }
    public int TStuecke { get => _tstuecke; set => Setze(ref _tstuecke, value < 0 ? 0 : value); }
    public int Reduzierungen { get => _reduzierungen; set => Setze(ref _reduzierungen, value < 0 ? 0 : value); }
    /// <summary>Bestand (optional): vorhandener Werkstoff.</summary>
    public string Werkstoff { get => _werkstoff; set => Setze(ref _werkstoff, value); }
    /// <summary>Bestand (optional): vorhandene Nennweite.</summary>
    public string Nennweite { get => _nennweite; set => Setze(ref _nennweite, value); }

    /// <summary>Kurz-Zusammenfassung für den Karten-Kopf.</summary>
    [JsonIgnore]
    public string SummeVrText
    {
        get
        {
            int stellen = Orte.Sum(o => o.Waschtisch + o.Dusche + o.Wanne + o.Wc
                + o.Waschmaschine + o.Geschirrspueler + o.Kueche);
            var orte = Orte.Count == 1 ? "1 Ort" : $"{Orte.Count} Orte";
            return Anzahl > 1 ? $"{orte} · {stellen} Stellen × {Anzahl} Whg." : $"{orte} · {stellen} Stellen";
        }
    }

    /// <summary>Von außen aufrufbar, damit verschachtelte Ort-Änderungen die Wohneinheit „dirty" melden.</summary>
    public void MeldeGeaendert() => Melde(nameof(SummeVrText));

    protected override void NachAenderung() => Melde(nameof(SummeVrText));
}

/// <summary>Ein Abwasser-Anschlussobjekt mit Anschlusswert DU (l/s).</summary>
public class AwObjekt : INotifyPropertyChanged
{
    string _bezeichnung = "", _quelle = "";
    double _du;
    int _anzahl = 1;

    public string Bezeichnung { get => _bezeichnung; set => Setze(ref _bezeichnung, value); }
    /// <summary>Anschlusswert DU je Stück in l/s (System I nach DIN EN 12056-2).</summary>
    public double Du { get => _du; set => Setze(ref _du, value); }
    public int Anzahl { get => _anzahl; set => Setze(ref _anzahl, value < 0 ? 0 : value); }
    public string Quelle { get => _quelle; set => Setze(ref _quelle, value); }

    [JsonIgnore] public double SummeDu => Du * Anzahl;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummeDu)));
    }
}

/// <summary>Abwasser-Randdaten (Nutzung/K, System, Rückstauebene, Lüftung …).</summary>
public class AwDaten : ErfassungBasis
{
    string _nutzung = "Wohnhaus / unregelmäßige Nutzung", _system = "System I (Füllungsgrad 0,5)",
           _kanalsystem = "Mischsystem", _rueckstauebene = "", _kanalsohle = "", _bemerkung = "";
    double _k = 0.5, _qc;
    bool _hauptlueftung, _rueckstausicherung, _hebeanlage;

    /// <summary>Nutzung → bestimmt Abflusskennzahl K.</summary>
    public string Nutzung { get => _nutzung; set => Setze(ref _nutzung, value); }
    /// <summary>Abflusskennzahl K (Qww = K·√ΣDU).</summary>
    public double K { get => _k; set => Setze(ref _k, value); }
    public string EntwSystem { get => _system; set => Setze(ref _system, value); }
    public string Kanalsystem { get => _kanalsystem; set => Setze(ref _kanalsystem, value); }
    public string Rueckstauebene { get => _rueckstauebene; set => Setze(ref _rueckstauebene, value); }
    public string Kanalsohle { get => _kanalsohle; set => Setze(ref _kanalsohle, value); }
    /// <summary>Dauerabfluss Qc (l/s) – wird unvermindert addiert.</summary>
    public double Qc { get => _qc; set => Setze(ref _qc, value); }
    public bool Hauptlueftung { get => _hauptlueftung; set => Setze(ref _hauptlueftung, value); }
    public bool Rueckstausicherung { get => _rueckstausicherung; set => Setze(ref _rueckstausicherung, value); }
    public bool Hebeanlage { get => _hebeanlage; set => Setze(ref _hebeanlage, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }
}

/// <summary>Ein Gasgerät mit Nennwärmebelastung.</summary>
public class GasGeraet : INotifyPropertyChanged
{
    string _bezeichnung = "", _kategorie = "C (raumluftunabhängig)", _aufstellraum = "";
    double _belastung;
    int _anzahl = 1;

    public string Bezeichnung { get => _bezeichnung; set => Setze(ref _bezeichnung, value); }
    /// <summary>NennwärmeBELASTUNG je Gerät in kW (nicht -leistung!).</summary>
    public double Belastung { get => _belastung; set => Setze(ref _belastung, value); }
    public int Anzahl { get => _anzahl; set => Setze(ref _anzahl, value < 0 ? 0 : value); }
    /// <summary>Gerätekategorie A / B / C.</summary>
    public string Kategorie { get => _kategorie; set => Setze(ref _kategorie, value); }
    public string Aufstellraum { get => _aufstellraum; set => Setze(ref _aufstellraum, value); }

    [JsonIgnore] public double SummeBelastung => Belastung * Anzahl;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummeBelastung)));
    }
}

/// <summary>Gas-Randdaten (Gasart, Druckstufe, Zähler, Gasströmungswächter …).</summary>
public class GasDaten : ErfassungBasis
{
    string _gasart = "Erdgas H", _druckstufe = "Niederdruck (≤ 23 mbar)", _zaehler = "",
           _gsw = "", _aufstellraum = "", _bemerkung = "";
    double _zaehlerDp, _aufstellraumVolumen;

    public string Gasart { get => _gasart; set => Setze(ref _gasart, value); }
    public string Druckstufe { get => _druckstufe; set => Setze(ref _druckstufe, value); }
    /// <summary>Gaszähler-Baugröße (G4/G6/G10 …).</summary>
    public string Zaehler { get => _zaehler; set => Setze(ref _zaehler, value); }
    public double ZaehlerDp { get => _zaehlerDp; set => Setze(ref _zaehlerDp, value); }
    /// <summary>Gasströmungswächter: Typ (K/M) + Nennwert.</summary>
    public string Gsw { get => _gsw; set => Setze(ref _gsw, value); }
    public string Aufstellraum { get => _aufstellraum; set => Setze(ref _aufstellraum, value); }
    /// <summary>Aufstellraum-Volumen (m³) für Verbrennungsluft-Nachweis.</summary>
    public double AufstellraumVolumen { get => _aufstellraumVolumen; set => Setze(ref _aufstellraumVolumen, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }
}

/// <summary>Ein einzelnes Bauteil eines Raums (Fenster / Tür / Nische) mit Maßen.</summary>
public class Bauteil : INotifyPropertyChanged
{
    string _art = "Fenster", _bemerkung = "";
    double _breite, _hoehe;

    /// <summary>Fenster / Tür / Nische.</summary>
    public string Art { get => _art; set => Setze(ref _art, value); }
    public double Breite { get => _breite; set => Setze(ref _breite, value); }
    public double Hoehe { get => _hoehe; set => Setze(ref _hoehe, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>Ein Raum für die raumweise Heizlast (überschlägig Fläche × spez. Heizlast).</summary>
public class HeizRaum : INotifyPropertyChanged
{
    string _name = "", _heizungsart = "Heizkörper", _heizflaeche = "";
    double _laenge, _breite, _hoehe = 2.5, _sollTemp = 20, _spezHeizlast = 100;

    public string Name { get => _name; set => Setze(ref _name, value); }
    public double Laenge { get => _laenge; set => Setze(ref _laenge, value); }
    public double Breite { get => _breite; set => Setze(ref _breite, value); }
    public double Hoehe { get => _hoehe; set => Setze(ref _hoehe, value); }
    /// <summary>Norm-Innentemperatur des Raums (°C).</summary>
    public double SollTemp { get => _sollTemp; set => Setze(ref _sollTemp, value); }
    /// <summary>Spezifische Heizlast (W/m²) – je Raum überschreibbar.</summary>
    public double SpezHeizlast { get => _spezHeizlast; set => Setze(ref _spezHeizlast, value); }
    /// <summary>Fußbodenheizung / Heizkörper / gemischt.</summary>
    public string Heizungsart { get => _heizungsart; set => Setze(ref _heizungsart, value); }
    /// <summary>Heizkörper-Detail (Typ/Maße/Ventil) – optional Freitext.</summary>
    public string Heizflaeche { get => _heizflaeche; set => Setze(ref _heizflaeche, value); }
    /// <summary>Fenster / Türen / Nischen einzeln mit Maßen.</summary>
    public ObservableCollection<Bauteil> Bauteile { get; set; } = new();

    /// <summary>Grundfläche in m² (Länge × Breite).</summary>
    [JsonIgnore] public double Flaeche => Laenge * Breite;
    /// <summary>Überschlägige Raum-Heizlast in W (Fläche × spez. Heizlast).</summary>
    [JsonIgnore] public double Heizlast => Flaeche * SpezHeizlast;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Von außen aufrufbar, damit verschachtelte Bauteil-Änderungen den Raum „dirty" melden.</summary>
    public void MeldeGeaendert() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bauteile)));

    void Setze<T>(ref T feld, T wert, [CallerMemberName] string? n = null)
    {
        if (Equals(feld, wert)) return;
        feld = wert;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flaeche)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Heizlast)));
    }
}

/// <summary>Heizungs-Randdaten (Erzeuger, Systemtemperaturen, Netz, Abgleich).</summary>
public class HeizDaten : ErfassungBasis
{
    string _erzeuger = "", _pumpe = "", _verfahren = "Verfahren B (mit raumweiser Heizlast)",
           _bemerkung = "";
    double _vorlauf = 55, _ruecklauf = 45, _erzeugerLeistung, _foerderhoehe,
           _wasserinhalt, _magVolumen, _magVordruck;

    /// <summary>Wärmeerzeuger: Typ/Hersteller (Freitext).</summary>
    public string Erzeuger { get => _erzeuger; set => Setze(ref _erzeuger, value); }
    public double ErzeugerLeistung { get => _erzeugerLeistung; set => Setze(ref _erzeugerLeistung, value); }
    public double Vorlauf { get => _vorlauf; set => Setze(ref _vorlauf, value); }
    public double Ruecklauf { get => _ruecklauf; set => Setze(ref _ruecklauf, value); }
    public string Pumpe { get => _pumpe; set => Setze(ref _pumpe, value); }
    public double Foerderhoehe { get => _foerderhoehe; set => Setze(ref _foerderhoehe, value); }
    public double Wasserinhalt { get => _wasserinhalt; set => Setze(ref _wasserinhalt, value); }
    public double MagVolumen { get => _magVolumen; set => Setze(ref _magVolumen, value); }
    public double MagVordruck { get => _magVordruck; set => Setze(ref _magVordruck, value); }
    public string Verfahren { get => _verfahren; set => Setze(ref _verfahren, value); }
    public string Bemerkung { get => _bemerkung; set => Setze(ref _bemerkung, value); }
}

/// <summary>Ein vollständiges Aufnahme-Vorhaben (eine Baustelle/ein Objekt).</summary>
public class Erfassung
{
    public string Name { get; set; } = "Neues Vorhaben";
    /// <summary>Szenario: Bestand-Begehung / Neubau 1:1 / Neubau-Teildaten.</summary>
    public string Szenario { get; set; } = "Bestand-Begehung";
    public GebaeudeKopf Gebaeude { get; set; } = new();

    // ---- Trinkwasser ----
    public TwRandbedingungen TwRand { get; set; } = new();
    public string TwGebaeudeart { get; set; } = "Wohngebäude / Seniorenheim";
    public double KoeffA { get; set; } = 1.48;
    public double KoeffB { get; set; } = 0.19;
    public double KoeffC { get; set; } = 0.94;
    public List<Wohneinheit> Wohneinheiten { get; set; } = new();
    public string TwHygiene { get; set; } = "";

    // ---- Abwasser ----
    public AwDaten Aw { get; set; } = new();
    public List<AwObjekt> AwObjekte { get; set; } = new();

    // ---- Gas ----
    public GasDaten Gas { get; set; } = new();
    public List<GasGeraet> GasGeraete { get; set; } = new();

    // ---- Heizung ----
    public HeizDaten Heiz { get; set; } = new();
    public List<HeizRaum> HeizRaeume { get; set; } = new();
}

public class ErfassungDaten
{
    public List<Erfassung> Vorhaben { get; set; } = new();
}

/// <summary>
/// Lädt/speichert die Aufnahme-Vorhaben unter %APPDATA%\NotizApp\auslegung\aufnahme.json.
/// Nutzt die DU-Kataloge und die Spitzenvolumenstrom-Formel aus <see cref="TrinkwasserService"/>.
/// </summary>
public class ErfassungStore
{
    static string Ordner => Path.Combine(SettingsService.SettingsOrdner, "auslegung");
    static string Pfad => Path.Combine(Ordner, "aufnahme.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ErfassungDaten Lade()
    {
        try
        {
            if (!File.Exists(Pfad)) return new ErfassungDaten();
            return JsonSerializer.Deserialize<ErfassungDaten>(File.ReadAllText(Pfad)) ?? new ErfassungDaten();
        }
        catch
        {
            return new ErfassungDaten();
        }
    }

    public void Speichere(ErfassungDaten daten)
    {
        Directory.CreateDirectory(Ordner);
        File.WriteAllText(Pfad, JsonSerializer.Serialize(daten, JsonOpts));
    }

    // ---- Auswahllisten für ComboBoxen ----

    public static readonly IReadOnlyList<string> Gebaeudearten =
        TrinkwasserService.Gebaeudearten.Select(g => g.Name).ToList();

    public static readonly IReadOnlyList<string> Werkstoffe = new List<string>
    {
        "Kupfer", "Edelstahl", "Mehrschichtverbund", "PE-X", "Stahl verzinkt",
        "PP (Abwasser)", "PE (Abwasser)", "Guss/SML", "Steinzeug",
    };

    public static readonly IReadOnlyList<string> Verlegearten = new List<string>
    {
        "frei/sichtbar", "im Schacht", "unter Putz", "im Estrich", "erdverlegt", "in Dämmung",
    };

    public static readonly IReadOnlyList<string> Gewerke = new List<string>
    {
        "Wasser", "Abwasser", "Gas", "Heizung",
    };

    public static readonly IReadOnlyList<string> Szenarien = new List<string>
    {
        "Bestand-Begehung", "Neubau – alle Daten", "Neubau – Teildaten",
    };

    public static readonly IReadOnlyList<string> WwKonzepte = new List<string>
    {
        "zentral mit Zirkulation", "zentral ohne Zirkulation", "dezentral (Durchlauf)",
    };

    public static readonly IReadOnlyList<string> Heizungsarten = new List<string>
    {
        "Heizkörper", "Fußbodenheizung", "gemischt",
    };

    public static readonly IReadOnlyList<string> BauteilArten = new List<string>
    {
        "Fenster", "Tür", "Nische",
    };

    // ---- Abwasser: Anschlusswerte DU (System I, l/s) nach DIN EN 12056-2 ----

    /// <summary>Entnahmestellen-Kategorie: Berechnungsdurchfluss VR (kalt/warm, l/s) und
    /// Abwasser-Anschlusswert DU (l/s), plus Zugriff auf die Stückzahl je Ort.</summary>
    public sealed record Fixture(string Label, double VrKalt, double VrWarm, double Du,
        Func<Ort, int> Anzahl);

    /// <summary>Die zählbaren Entnahmestellen je Ort (DIN 1988-300 / EN 12056-2).</summary>
    public static readonly IReadOnlyList<Fixture> Fixturen = new List<Fixture>
    {
        new("Waschtisch",     0.07, 0.07, 0.5, o => o.Waschtisch),
        new("Dusche",         0.15, 0.15, 0.6, o => o.Dusche),
        new("Badewanne",      0.15, 0.15, 0.8, o => o.Wanne),
        new("WC",             0.13, 0.00, 2.0, o => o.Wc),
        new("Waschmaschine",  0.15, 0.00, 0.8, o => o.Waschmaschine),
        new("Geschirrspüler", 0.07, 0.00, 0.8, o => o.Geschirrspueler),
        new("Küchenspüle",    0.07, 0.07, 0.8, o => o.Kueche),
    };

    /// <summary>Gesamtzahl einer Fixture über alle Wohneinheiten (Orte × Wohnungsanzahl).</summary>
    static int GesamtAnzahl(IEnumerable<Wohneinheit> wes, Fixture f) =>
        wes.Sum(w => w.Orte.Sum(f.Anzahl) * w.Anzahl);

    /// <summary>Summe der Berechnungsdurchflüsse über alle Wohneinheiten/Orte (× Wohnungsanzahl).</summary>
    public static (double Kalt, double Warm) SummeVr(IEnumerable<Wohneinheit> wes)
    {
        double kalt = 0, warm = 0;
        var liste = wes.ToList();
        foreach (var f in Fixturen)
        {
            int n = GesamtAnzahl(liste, f);
            kalt += n * f.VrKalt;
            warm += n * f.VrWarm;
        }
        return (kalt, warm);
    }

    /// <summary>Abwasser-Objekte automatisch aus den Wohneinheiten/Orten ableiten (je Kategorie summiert).</summary>
    public static List<AwObjekt> AbwasserAusWohneinheiten(IEnumerable<Wohneinheit> wes)
    {
        var liste = wes.ToList();
        var result = new List<AwObjekt>();
        foreach (var f in Fixturen)
        {
            int n = GesamtAnzahl(liste, f);
            if (n > 0 && f.Du > 0)
                result.Add(new AwObjekt { Bezeichnung = f.Label, Du = f.Du, Anzahl = n, Quelle = "aus Trinkwasser" });
        }
        return result;
    }

    /// <summary>Ort-Vorlagen: Name + typische Bestückung (WT, Dusche, Wanne, WC, WM, GSW, Küchenspüle).</summary>
    public static readonly IReadOnlyList<(string Name, int Wt, int Du, int Wa, int Wc, int Wm, int Gsw, int Ks)> OrtVorlagen =
        new List<(string, int, int, int, int, int, int, int)>
    {
        ("Badezimmer", 1, 1, 1, 1, 0, 0, 0),
        ("Küche",      0, 0, 0, 0, 0, 1, 1),
        ("Gäste-WC",   1, 0, 0, 1, 0, 0, 0),
        ("HWR",        0, 0, 0, 0, 1, 0, 0),
        ("Ort",        0, 0, 0, 0, 0, 0, 0),
    };

    // Reine Abläufe OHNE Zapfstelle (kommen nicht aus dem Trinkwasser) — manuell ergänzbar.
    public static readonly IReadOnlyList<DuVorlage> AbwasserExtraKatalog = new List<DuVorlage>
    {
        new("Bodenablauf DN 50",  0.8, 0),
        new("Bodenablauf DN 70",  1.5, 0),
        new("Bodenablauf DN 100", 2.0, 0),
        new("Kellerablauf",       0.8, 0),
        new("Hofablauf",          0.8, 0),
        new("Ausgussbecken",      0.8, 0),
        new("Standrohr / Sifon",  0.8, 0),
    };

    /// <summary>Nutzung → Abflusskennzahl K (DIN EN 12056-2).</summary>
    public static readonly IReadOnlyList<(string Nutzung, double K)> Nutzungsarten =
        new List<(string, double)>
    {
        ("Wohnhaus / unregelmäßige Nutzung", 0.5),
        ("Krankenhaus / Schule / regelmäßige Nutzung", 0.7),
        ("Öffentliche Toiletten / häufige Nutzung", 1.0),
        ("Speziallabor / Sondergebäude", 1.2),
        ("Eigener Wert", 0.5),
    };

    public static readonly IReadOnlyList<string> NutzungsartenNamen =
        Nutzungsarten.Select(n => n.Nutzung).ToList();

    public static readonly IReadOnlyList<string> EntwSysteme = new List<string>
    {
        "System I (Füllungsgrad 0,5)", "System II (0,7)", "System III (1,0)", "System IV",
    };

    public static readonly IReadOnlyList<string> Kanalsysteme = new List<string>
    {
        "Mischsystem", "Trennsystem", "unbekannt",
    };

    /// <summary>Schmutzwasserabfluss Qww = K·√ΣDU (l/s), nie kleiner als das größte Einzel-DU.</summary>
    public static double Schmutzwasserabfluss(double summeDu, double k, double maxEinzelDu)
    {
        if (summeDu <= 0) return 0;
        var qww = k * Math.Sqrt(summeDu);
        return Math.Max(qww, maxEinzelDu);
    }

    // ---- Gas (DVGW-TRGI G 600) ----

    public static readonly IReadOnlyList<string> Gasarten = new List<string>
    {
        "Erdgas H", "Erdgas L", "Flüssiggas (Propan)",
    };

    public static readonly IReadOnlyList<string> Druckstufen = new List<string>
    {
        "Niederdruck (≤ 23 mbar)", "Mitteldruck (> 23 mbar – 1 bar)",
    };

    public static readonly IReadOnlyList<string> Geraetekategorien = new List<string>
    {
        "A (raumluftabhängig, ohne Abgasanlage)",
        "B (raumluftabhängig, mit Abgasanlage)",
        "C (raumluftunabhängig)",
    };

    /// <summary>Typische Gasgeräte mit Vorbelegung der Nennwärmebelastung (kW).</summary>
    public static readonly IReadOnlyList<(string Bezeichnung, double Belastung)> GasGeraetKatalog =
        new List<(string, double)>
    {
        ("Gas-Brennwerttherme (Heizung)", 24),
        ("Gas-Kombitherme (Heizung + WW)", 28),
        ("Gas-Heizkessel", 20),
        ("Gas-Durchlauferhitzer", 21),
        ("Gasherd / Kochstelle", 9),
        ("Gas-Wäschetrockner", 6),
    };

    public static readonly IReadOnlyList<string> GasGeraetNamen =
        GasGeraetKatalog.Select(g => g.Bezeichnung).ToList();

    /// <summary>Verbrennungsluftbedarf: 1,6 m³/h je kW Nennwärmebelastung (TRGI 2018).</summary>
    public static double Verbrennungsluft(double summeBelastung) => 1.6 * summeBelastung;

    // ---- Heizung ----

    public static readonly IReadOnlyList<string> HeizVerfahren = new List<string>
    {
        "Verfahren B (mit raumweiser Heizlast)", "Verfahren A (überschlägig)",
    };

    public static readonly IReadOnlyList<string> ErzeugerTypen = new List<string>
    {
        "Gas-Brennwert", "Öl-Brennwert", "Wärmepumpe (Luft)", "Wärmepumpe (Sole)",
        "Pellet", "Fernwärme", "Hybrid", "Sonstiger",
    };

    /// <summary>Typische Räume: Name, Norm-Innentemperatur (°C), spez. Heizlast (W/m²).</summary>
    public static readonly IReadOnlyList<(string Name, double Temp, double Spez)> RaumKatalog =
        new List<(string, double, double)>
    {
        ("Wohnzimmer",  20, 100),
        ("Schlafzimmer", 18, 100),
        ("Kinderzimmer", 20, 100),
        ("Küche",       20, 100),
        ("Bad",         24, 120),
        ("Flur / Diele", 18,  90),
        ("Büro",        20, 100),
        ("Keller (beheizt)", 15, 80),
    };

    public static readonly IReadOnlyList<string> RaumKatalogNamen =
        RaumKatalog.Select(r => r.Name).ToList();

    /// <summary>Volumenstrom V̇ = Q / (1,163 · ΔT) in l/h (Q in W, ΔT = Spreizung in K).</summary>
    public static double Volumenstrom(double watt, double spreizung)
    {
        if (watt <= 0 || spreizung <= 0) return 0;
        return watt / (1.163 * spreizung);
    }
}
