using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NotizApp.Controls;

/// <summary>
/// Volumenstrom aus Heizleistung und Spreizung (Wasser):
/// V̇ [l/h] = Q [W] / (1,163 [Wh/(l·K)] · ΔT). Ergebnis in l/h und m³/h.
/// </summary>
public partial class VolumenstromRechner : UserControl
{
    static readonly CultureInfo De = new("de-DE");
    const double CWasser = 1.163; // Wh/(l·K) = Wärmekapazität Wasser

    public event Action<string>? ErgebnisEinfuegen;

    string _ergebnisText = "";
    bool _gueltig;

    public VolumenstromRechner()
    {
        InitializeComponent();
        SpreizungBox.Text = "20";
        Berechne();
    }

    void Eingabe_Changed(object sender, TextChangedEventArgs e) => Berechne();

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string tag) SpreizungBox.Text = tag;
    }

    void Berechne()
    {
        if (ErgebnisLh is null) return;

        if (!TryZahl(LeistungBox.Text, out var kw) || !TryZahl(SpreizungBox.Text, out var dt)
            || kw <= 0 || dt <= 0)
        {
            _gueltig = false;
            ErgebnisLh.Text = "—";
            ErgebnisFormel.Text = "";
            return;
        }

        var lh = kw * 1000 / (CWasser * dt);
        _gueltig = true;
        ErgebnisLh.Text = $"{lh.ToString("#,##0", De)} l/h";
        ErgebnisFormel.Text =
            $"{Zahl(kw)} kW ÷ (1,163 · {Zahl(dt)} K) = {lh.ToString("#,##0", De)} l/h " +
            $"= {(lh / 1000).ToString("0.00", De)} m³/h";
        _ergebnisText =
            "**Volumenstrom (aus Heizleistung)**\n" +
            $"- Heizleistung: {Zahl(kw)} kW\n" +
            $"- Spreizung ΔT: {Zahl(dt)} K\n" +
            $"- Volumenstrom: {lh.ToString("#,##0", De)} l/h ({(lh / 1000).ToString("0.00", De)} m³/h)";
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

    static string Zahl(double d) => d.ToString("0.##", De);

    static bool TryZahl(string? s, out double wert)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
