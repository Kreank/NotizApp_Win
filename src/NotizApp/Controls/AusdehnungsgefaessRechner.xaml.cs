using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NotizApp.Controls;

/// <summary>
/// Membran-Ausdehnungsgefäß (MAG) für geschlossene Heizungsanlagen, überschlägig:
/// V_n = (V_e + V_wv) · (p_e + 1) / (p_e − p_0)
///   V_e  = Anlageninhalt · Ausdehnungskoeffizient (max. Vorlauftemp)
///   V_wv = Wasservorlage = max(0,5 % · Anlageninhalt; 3 l)
///   p_0  = Vordruck = stat. Höhe/10 + 0,3 bar
///   p_e  = Enddruck = Ansprechdruck SV − 0,5 bar
/// Konventionen (0,3 / 0,5 bar, Wasservorlage) sind gängige Praxis und bei Bedarf anpassbar.
/// </summary>
public partial class AusdehnungsgefaessRechner : UserControl
{
    static readonly CultureInfo De = new("de-DE");

    // Handelsübliche Nenngrößen (Liter)
    static readonly int[] Nenngroessen =
        { 8, 12, 18, 25, 35, 50, 80, 100, 140, 200, 250, 300, 400, 500, 600, 800, 1000 };

    public event Action<string>? ErgebnisEinfuegen;

    string _ergebnisText = "";
    bool _gueltig;

    public AusdehnungsgefaessRechner()
    {
        InitializeComponent();

        // Ausdehnungskoeffizient Wasser (Füllung bei ~10 °C)
        Fuelle(TempBox, new (string, double)[]
        {
            ("50 °C", 0.0121), ("60 °C", 0.0171), ("70 °C", 0.0228),
            ("80 °C", 0.0289), ("90 °C", 0.0359),
        }, 2); // Standard 70 °C

        Fuelle(SvBox, new (string, double)[] { ("2,5 bar", 2.5), ("3,0 bar", 3.0) }, 1);

        Berechne();
    }

    static void Fuelle(ComboBox box, (string Text, double Wert)[] werte, int auswahl)
    {
        foreach (var (text, wert) in werte)
            box.Items.Add(new ComboBoxItem { Content = text, Tag = wert.ToString(CultureInfo.InvariantCulture) });
        box.SelectedIndex = auswahl;
    }

    void Eingabe_Changed(object sender, TextChangedEventArgs e) => Berechne();
    void Auswahl_Changed(object sender, SelectionChangedEventArgs e) => Berechne();

    static double TagWert(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag is string s
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    void Berechne()
    {
        if (ErgebnisGroesse is null) return;

        HinweisText.Text = "";
        if (!TryZahl(InhaltBox.Text, out var va) || va <= 0)
        {
            Ungueltig();
            return;
        }
        var beta = TagWert(TempBox);
        var pSv = TagWert(SvBox);
        var hoehe = TryZahl(HoeheBox.Text, out var h) ? h : 0;

        var ve = va * beta;                       // Ausdehnungsvolumen
        var vwv = Math.Max(0.005 * va, 3);        // Wasservorlage, min. 3 l
        var p0 = hoehe / 10.0 + 0.3;              // Vordruck
        var pe = pSv - 0.5;                        // Enddruck

        if (pe - p0 <= 0.1)
        {
            Ungueltig();
            HinweisText.Text = "Enddruck ≤ Vordruck: größeres Sicherheitsventil oder geringere " +
                               "statische Höhe nötig (p_e muss deutlich über p_0 liegen).";
            return;
        }

        var vn = (ve + vwv) * (pe + 1) / (pe - p0);
        var empf = Nenngroessen.FirstOrDefault(g => g >= vn);

        _gueltig = true;
        ErgebnisGroesse.Text = empf > 0 ? $"{empf} Liter" : $"> {Nenngroessen[^1]} l (Sonderfall)";
        ErgebnisDetail.Text =
            $"Rechnerisch nötig: {Z(vn)} l\n" +
            $"Ausdehnung V_e = {Z(ve)} l · Wasservorlage V_wv = {Z(vwv)} l\n" +
            $"Vordruck p_0 = {Z(p0)} bar · Enddruck p_e = {Z(pe)} bar";

        _ergebnisText =
            "**Ausdehnungsgefäß (MAG)**\n" +
            $"- Anlageninhalt: {Z(va)} l, max. Vorlauf: {(TempBox.SelectedItem as ComboBoxItem)?.Content}\n" +
            $"- Ausdehnung V_e: {Z(ve)} l, Wasservorlage: {Z(vwv)} l\n" +
            $"- Vordruck p_0: {Z(p0)} bar, Enddruck p_e: {Z(pe)} bar\n" +
            $"- Nötig: {Z(vn)} l → **empfohlen: {(empf > 0 ? empf + " l" : "Sonderauslegung")}**";
    }

    void Ungueltig()
    {
        _gueltig = false;
        ErgebnisGroesse.Text = "—";
        ErgebnisDetail.Text = "";
    }

    void Einfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (!_gueltig) { HinweisText.Text = "Bitte zuerst gültige Werte eingeben."; return; }
        ErgebnisEinfuegen?.Invoke(_ergebnisText);
        HinweisText.Text = "In die Notiz eingefügt.";
    }

    void Kopieren_Click(object sender, RoutedEventArgs e)
    {
        if (!_gueltig) { HinweisText.Text = "Bitte zuerst gültige Werte eingeben."; return; }
        try { Clipboard.SetText(_ergebnisText); HinweisText.Text = "In die Zwischenablage kopiert."; }
        catch { HinweisText.Text = "Kopieren fehlgeschlagen."; }
    }

    static string Z(double d) => d.ToString("0.##", De);

    static bool TryZahl(string? s, out double wert)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
