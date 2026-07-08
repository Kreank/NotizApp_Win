using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>Anzeige-Aufbereitung eines <see cref="WissenTreffer"/> für die Liste.</summary>
public class WissenTrefferVm
{
    readonly WissenTreffer _t;

    public WissenTrefferVm(WissenTreffer t)
    {
        _t = t;
        var (label, farbe) = Kategorie(t.KatalogArt);
        ArtLabel = label;
        HatArt = label.Length > 0;
        ArtTextBrush = Pinsel(farbe, 0xFF);
        ArtLeiseBrush = Pinsel(farbe, 0x22);
    }

    public string Bezeichnung => _t.Bezeichnung;
    public string Artikelnummer => _t.Artikelnummer;
    public string ArtLabel { get; }
    public bool HatArt { get; }
    public Brush ArtTextBrush { get; }
    public Brush ArtLeiseBrush { get; }

    public string PreisAnzeige
    {
        get
        {
            if (_t.Preis is not { } p) return "";
            var einheit = string.IsNullOrWhiteSpace(_t.Einheit) ? "" : " / " + _t.Einheit;
            return $"{p.ToString("0.00", DashboardView.Kultur)} €{einheit}";
        }
    }

    /// <summary>Zeile aus Hersteller, Warengruppe und Stand (die Art.-Nr. hat einen eigenen Chip).</summary>
    public string Meta => string.Join("   ·   ", new[]
    {
        Leer(_t.Hersteller) ? null : _t.Hersteller,
        Leer(_t.WarengruppeName) ? null : _t.WarengruppeName,
        Leer(_t.Stand) ? null : "Stand " + _t.Stand,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public bool HatLangtext => !Leer(_t.Langtext);

    /// <summary>Langtext lesbar aufbereitet: der Rohtext ist alle ~40 Zeichen hart
    /// umgebrochen. Hier werden Fortsetzungszeilen wieder zu Fließtext verbunden,
    /// „- "-Punkte zu Bullets (•) und Doppel-Umbrüche als Absätze erhalten.</summary>
    public string LangtextAufbereitet
    {
        get
        {
            if (Leer(_t.Langtext)) return "";
            var zeilen = _t.Langtext!.Replace("\r\n", "\n").Split('\n');
            var aus = new List<string>();
            var puffer = new System.Text.StringBuilder();
            void Leeren() { if (puffer.Length > 0) { aus.Add(puffer.ToString()); puffer.Clear(); } }

            foreach (var roh in zeilen)
            {
                var t = roh.Trim();
                if (t.Length == 0)
                {
                    Leeren();
                    if (aus.Count > 0 && aus[^1].Length > 0) aus.Add(""); // Absatz
                }
                else if (t.StartsWith("- ") || t.StartsWith("•"))
                {
                    Leeren();
                    puffer.Append("•  ").Append(t.TrimStart('-', '•', ' '));
                }
                else if (t.EndsWith(":") && puffer.Length == 0)
                {
                    aus.Add(t); // Überschrift auf eigener Zeile
                }
                else
                {
                    if (puffer.Length > 0)
                    {
                        if (puffer[^1] == '-')
                        {
                            // Zeilenumbruch-Trennung: bei kleiner Fortsetzung den
                            // Trennstrich entfernen (Silbentrennung), sonst behalten
                            // (echte Bindestriche wie „pH-Wert"), immer ohne Leerzeichen
                            if (char.IsLower(t[0])) puffer.Length--;
                        }
                        else
                        {
                            puffer.Append(' ');
                        }
                    }
                    puffer.Append(t);
                }
            }
            Leeren();
            return string.Join("\n", aus).Trim();
        }
    }

    /// <summary>Kompakter Textblock zum Einfügen in die Notiz.</summary>
    public string NotizText
    {
        get
        {
            var kopf = new List<string>();
            if (!Leer(_t.Artikelnummer)) kopf.Add("Art.-Nr. " + _t.Artikelnummer);
            if (!Leer(_t.Hersteller)) kopf.Add(_t.Hersteller!);
            if (PreisAnzeige.Length > 0) kopf.Add(PreisAnzeige);
            var kurz = string.Join(" ", new[] { _t.Kurztext1, _t.Kurztext2 }
                .Where(s => !Leer(s)));

            var sb = new System.Text.StringBuilder();
            sb.Append("**").Append(_t.Bezeichnung).Append("**\n");
            if (kopf.Count > 0) sb.Append(string.Join(" · ", kopf)).Append('\n');
            if (!Leer(kurz)) sb.Append(kurz).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
    }

    static bool Leer(string? s) => string.IsNullOrWhiteSpace(s);

    static (string Label, Color Farbe) Kategorie(string? art) => (art ?? "").ToLowerInvariant() switch
    {
        "wartung" => ("🔧 Wartung", Hex("#C0703C")),
        "inbetriebnahme" => ("▶ Inbetriebnahme", Hex("#0E7490")),
        "produkte" or "produkt" => ("📦 Produkt", Hex("#3B9E5F")),
        "ersatzteil" or "ersatzteile" => ("🧩 Ersatzteil", Hex("#8E5BD8")),
        "" => ("", Hex("#5E7278")),
        _ => (art!, Hex("#5E7278")),
    };

    static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    static Brush Pinsel(Color c, byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Werkzeug „Gerätewissen": Volltextsuche in der Wissensbasis (REST-API mit
/// API-Key aus den Settings). Treffer lassen sich in die aktuelle Notiz übernehmen.
/// </summary>
public partial class GeraetewissenTool : UserControl
{
    /// <summary>Treffer-Text in die aktuelle Notiz einfügen (wie die Rechner-Werkzeuge).</summary>
    public event Action<string>? ErgebnisEinfuegen;

    /// <summary>Vom Host gesetzt — liefert Endpunkt-URL und API-Key.</summary>
    public SettingsService? Einstellungen { get; set; }

    GeraeteWissenService? _dienst;
    CancellationTokenSource? _cts;

    public GeraetewissenTool()
    {
        InitializeComponent();
    }

    GeraeteWissenService? Dienst()
    {
        if (Einstellungen is null) return null;
        _dienst ??= new GeraeteWissenService(
            Einstellungen.Aktuell.GeraetewissenUrl,
            Einstellungen.Aktuell.GeraetewissenApiKey);
        return _dienst;
    }

    void SuchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SucheAsync(); }
    }

    void SuchenButton_Click(object sender, RoutedEventArgs e) => _ = SucheAsync();

    async Task SucheAsync()
    {
        var frage = SuchBox.Text.Trim();
        if (frage.Length < 2)
        {
            StatusText.Text = "Bitte einen Suchbegriff eingeben (mind. 2 Zeichen).";
            return;
        }

        var d = Dienst();
        if (d is null || !d.Konfiguriert)
        {
            StatusText.Text = "⚠ Kein Endpunkt/API-Key hinterlegt (Einstellungen).";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SuchenButton.IsEnabled = false;
        StatusText.Text = "🔎 Suche läuft…";
        try
        {
            var treffer = await d.SucheAsync(frage, token);
            if (token.IsCancellationRequested) return;
            TrefferListe.ItemsSource = treffer.Select(t => new WissenTrefferVm(t)).ToList();
            StatusText.Text = treffer.Count == 0
                ? "Keine Treffer."
                : $"{treffer.Count} Treffer";
        }
        catch (OperationCanceledException)
        {
            // neuere Suche hat übernommen — nichts tun
        }
        catch (Exception ex)
        {
            TrefferListe.ItemsSource = null;
            StatusText.Text = $"⚠ Abruf fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            SuchenButton.IsEnabled = true;
        }
    }

    void Einfuegen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WissenTrefferVm vm)
            ErgebnisEinfuegen?.Invoke(vm.NotizText);
    }

    void KopiereNummer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WissenTrefferVm vm) return;
        try
        {
            Clipboard.SetText(vm.Artikelnummer);
            StatusText.Text = $"Artikelnummer {vm.Artikelnummer} kopiert.";
        }
        catch
        {
            // Zwischenablage belegt — leise ignorieren
        }
    }
}
