using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NotizApp.Controls;

/// <summary>
/// Anlagenwasserinhalt als Summe der Komponenten (Rohr, FBH, Heizkörper,
/// Wärmeerzeuger, Puffer) — Grundlage für Befüllmenge/Nachspeisung nach VDI 2035.
/// Rohr-Wasserinhalte je Meter aus dem lichten Querschnitt gängiger Dimensionen.
/// </summary>
public partial class WasserinhaltRechner : UserControl
{
    static readonly CultureInfo De = new("de-DE");

    public event Action<string>? ErgebnisEinfuegen;

    string _ergebnisText = "";
    bool _gueltig;

    public WasserinhaltRechner()
    {
        InitializeComponent();

        // Rohr (Verteilung): Wasserinhalt je Meter aus lichtem Innendurchmesser
        Fuelle(RohrTyp, new (string, double)[]
        {
            ("Kupfer 15×1 (0,13 l/m)", 0.133),
            ("Kupfer 18×1 (0,20 l/m)", 0.201),
            ("Kupfer 22×1 (0,31 l/m)", 0.314),
            ("Kupfer 28×1,5 (0,49 l/m)", 0.491),
            ("Kupfer 35×1,5 (0,80 l/m)", 0.804),
            ("Verbund 16×2 (0,11 l/m)", 0.113),
            ("Verbund 20×2 (0,20 l/m)", 0.201),
            ("Verbund 26×3 (0,31 l/m)", 0.314),
            ("Stahl DN15 ½″ (0,20 l/m)", 0.201),
            ("Stahl DN20 ¾″ (0,37 l/m)", 0.366),
            ("Stahl DN25 1″ (0,58 l/m)", 0.581),
        }, 2);

        Fuelle(FbhTyp, new (string, double)[]
        {
            ("16×2 (0,11 l/m)", 0.113),
            ("17×2 (0,13 l/m)", 0.133),
            ("20×2 (0,20 l/m)", 0.201),
        }, 0);

        Berechne();
    }

    static void Fuelle(ComboBox box, (string Text, double Lm)[] werte, int auswahl)
    {
        foreach (var (text, lm) in werte)
            box.Items.Add(new ComboBoxItem
            {
                Content = text,
                Tag = lm.ToString(CultureInfo.InvariantCulture),
            });
        box.SelectedIndex = auswahl;
    }

    void Eingabe_Changed(object sender, TextChangedEventArgs e) => Berechne();
    void RohrTyp_Changed(object sender, SelectionChangedEventArgs e) => Berechne();
    void FbhTyp_Changed(object sender, SelectionChangedEventArgs e) => Berechne();

    static double Lm(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag is string s
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    void Berechne()
    {
        if (ErgebnisLiter is null) return;

        var rohr = Wert(RohrLaenge) * Lm(RohrTyp);
        var fbh = Wert(FbhLaenge) * Lm(FbhTyp);
        var hk = Wert(HkAnzahl) * Wert(HkInhalt);
        var erz = Wert(ErzeugerInhalt);
        var puf = Wert(PufferInhalt);
        var summe = rohr + fbh + hk + erz + puf;

        if (summe <= 0)
        {
            _gueltig = false;
            ErgebnisLiter.Text = "—";
            ErgebnisAufschluesselungText.Text = "";
            return;
        }

        _gueltig = true;
        ErgebnisLiter.Text = $"{summe.ToString("#,##0.#", De)} Liter";

        var teile = new List<string>();
        if (rohr > 0) teile.Add($"Rohr {L(rohr)}");
        if (fbh > 0) teile.Add($"FBH {L(fbh)}");
        if (hk > 0) teile.Add($"Heizkörper {L(hk)}");
        if (erz > 0) teile.Add($"Erzeuger {L(erz)}");
        if (puf > 0) teile.Add($"Puffer {L(puf)}");
        ErgebnisAufschluesselungText.Text = string.Join("  +  ", teile);

        _ergebnisText =
            "**Anlagenwasserinhalt (VDI 2035)**\n" +
            string.Join("\n", teile.Select(t => "- " + t)) +
            $"\n- **Summe: {summe.ToString("#,##0.#", De)} Liter**";
    }

    static string L(double liter) => $"{liter.ToString("#,##0.#", De)} l";

    void Einfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (!_gueltig) { HinweisText.Text = "Bitte zuerst Werte eingeben."; return; }
        ErgebnisEinfuegen?.Invoke(_ergebnisText);
        HinweisText.Text = "In die Notiz eingefügt.";
    }

    void Kopieren_Click(object sender, RoutedEventArgs e)
    {
        if (!_gueltig) { HinweisText.Text = "Bitte zuerst Werte eingeben."; return; }
        try { Clipboard.SetText(_ergebnisText); HinweisText.Text = "In die Zwischenablage kopiert."; }
        catch { HinweisText.Text = "Kopieren fehlgeschlagen."; }
    }

    /// <summary>Feldwert als Zahl (leer/ungültig = 0), Komma wie Punkt.</summary>
    static double Wert(TextBox box)
    {
        var s = (box.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
    }
}
