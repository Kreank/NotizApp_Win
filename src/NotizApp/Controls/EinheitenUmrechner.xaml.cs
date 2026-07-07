using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NotizApp.Controls;

/// <summary>
/// Einheiten-Umrechner für SHK-Alltagsgrößen. Eingabe eines Werts in einer
/// Einheit → Anzeige in allen Einheiten derselben Größe. Lineare Größen über
/// einen Faktor zur Basiseinheit, Temperatur gesondert (Offset).
/// </summary>
public partial class EinheitenUmrechner : UserControl
{
    static readonly CultureInfo De = new("de-DE");

    record Einheit(string Name, double Faktor); // value_basis = value * Faktor

    class Kategorie
    {
        public string Name = "";
        public Einheit[] Einheiten = Array.Empty<Einheit>();
        public bool IstTemperatur;
    }

    static readonly Kategorie[] Kategorien =
    {
        new()
        {
            Name = "Leistung",
            Einheiten = new[]
            {
                new Einheit("kW", 1000), new Einheit("W", 1),
                new Einheit("kcal/h", 1.163), new Einheit("MW", 1_000_000),
            },
        },
        new()
        {
            Name = "Druck",
            Einheiten = new[]
            {
                new Einheit("bar", 100_000), new Einheit("mbar", 100),
                new Einheit("kPa", 1000), new Einheit("Pa", 1),
                new Einheit("mWS", 9806.65),
            },
        },
        new()
        {
            Name = "Temperatur", IstTemperatur = true,
            Einheiten = new[] { new Einheit("°C", 0), new Einheit("K", 0), new Einheit("°F", 0) },
        },
        new()
        {
            Name = "Volumenstrom",
            Einheiten = new[]
            {
                new Einheit("l/h", 1), new Einheit("m³/h", 1000),
                new Einheit("l/min", 60), new Einheit("l/s", 3600),
            },
        },
        new()
        {
            Name = "Energie",
            Einheiten = new[]
            {
                new Einheit("kWh", 1), new Einheit("Wh", 0.001),
                new Einheit("MJ", 1.0 / 3.6), new Einheit("kcal", 1.0 / 860.4),
            },
        },
    };

    string _kopierText = "";

    public EinheitenUmrechner()
    {
        InitializeComponent();
        foreach (var k in Kategorien) KategorieBox.Items.Add(k.Name);
        KategorieBox.SelectedIndex = 0; // löst Kategorie_Changed aus
    }

    Kategorie Aktuelle => Kategorien[Math.Max(0, KategorieBox.SelectedIndex)];

    void Kategorie_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VonBox is null) return; // vor dem Laden der UI
        VonBox.Items.Clear();
        foreach (var eh in Aktuelle.Einheiten) VonBox.Items.Add(eh.Name);
        VonBox.SelectedIndex = 0;
        Berechne();
    }

    void Von_Changed(object sender, SelectionChangedEventArgs e) => Berechne();
    void Wert_Changed(object sender, TextChangedEventArgs e) => Berechne();

    void Berechne()
    {
        if (ErgebnisListe is null) return;
        ErgebnisListe.Children.Clear();
        _kopierText = "";

        var kat = Aktuelle;
        var von = VonBox.SelectedIndex;
        if (von < 0 || von >= kat.Einheiten.Length) return;

        if (!TryZahl(WertBox.Text, out var wert))
        {
            ErgebnisListe.Children.Add(Zeile("Wert eingeben…", "", leise: true));
            return;
        }

        var vonName = kat.Einheiten[von].Name;
        var zeilen = new List<string>();
        foreach (var (name, ergebnis) in Umrechnen(kat, von, wert))
        {
            bool istQuelle = name == vonName;
            ErgebnisListe.Children.Add(Zeile(name, ergebnis, hervorheben: istQuelle));
            zeilen.Add($"{ergebnis} {name}");
        }
        _kopierText = $"{Zahl(wert)} {vonName} = " + string.Join(" = ", zeilen);
    }

    /// <summary>Alle Einheiten der Größe für den Eingabewert (Index der Quell-Einheit).</summary>
    static IEnumerable<(string Name, string Wert)> Umrechnen(Kategorie kat, int vonIndex, double wert)
    {
        if (kat.IstTemperatur)
        {
            var celsius = kat.Einheiten[vonIndex].Name switch
            {
                "K" => wert - 273.15,
                "°F" => (wert - 32) * 5 / 9,
                _ => wert, // °C
            };
            foreach (var eh in kat.Einheiten)
            {
                double aus = eh.Name switch
                {
                    "K" => celsius + 273.15,
                    "°F" => celsius * 9 / 5 + 32,
                    _ => celsius,
                };
                yield return (eh.Name, aus.ToString("0.##", De));
            }
            yield break;
        }

        var basis = wert * kat.Einheiten[vonIndex].Faktor;
        foreach (var eh in kat.Einheiten)
            yield return (eh.Name, (basis / eh.Faktor).ToString("#,##0.####", De));
    }

    DockPanel Zeile(string einheit, string wert, bool hervorheben = false, bool leise = false)
    {
        var links = new TextBlock
        {
            Text = einheit,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("AppTextLeiseBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var rechts = new TextBlock
        {
            Text = wert,
            FontSize = hervorheben ? 18 : 16,
            FontWeight = hervorheben ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("AppTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (leise) rechts.Text = "";
        var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Opacity = leise ? 0.6 : 1 };
        DockPanel.SetDock(links, Dock.Left);
        dp.Children.Add(links);
        dp.Children.Add(rechts);
        return dp;
    }

    void Kopieren_Click(object sender, RoutedEventArgs e)
    {
        if (_kopierText.Length == 0) { HinweisText.Text = "Bitte zuerst einen Wert eingeben."; return; }
        try { Clipboard.SetText(_kopierText); HinweisText.Text = "In die Zwischenablage kopiert."; }
        catch { HinweisText.Text = "Kopieren fehlgeschlagen."; }
    }

    static string Zahl(double d) => d.ToString("0.####", De);

    static bool TryZahl(string? s, out double wert)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
