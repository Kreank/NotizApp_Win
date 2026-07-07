using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NotizApp.Controls;

/// <summary>
/// Überschlägige Heizlast „Variante B": beheizte Fläche × spezifische Heizlast
/// (Kennwert je nach Baualter/Dämmung, überschreibbar). Ergebnis in W und kW,
/// auf Wunsch als Markdown-Block in die aktuelle Notiz.
/// </summary>
public partial class HeizlastRechner : UserControl
{
    static readonly CultureInfo De = new("de-DE");

    /// <summary>Ergebnistext (Markdown) soll in die aktuelle Notiz eingefügt werden.</summary>
    public event Action<string>? ErgebnisEinfuegen;

    string _ergebnisText = "";
    bool _gueltig;

    public HeizlastRechner()
    {
        InitializeComponent();
        SetzeKennwertAusTyp();
        Berechne();
    }

    void TypBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        SetzeKennwertAusTyp();
        Berechne();
    }

    void Eingabe_Changed(object sender, TextChangedEventArgs e) => Berechne();

    /// <summary>Kennwert-Feld aus der Gebäude-Auswahl vorbelegen (bleibt danach editierbar).</summary>
    void SetzeKennwertAusTyp()
    {
        if (KennwertBox is null) return; // Auswahl feuert schon während InitializeComponent
        if ((TypBox.SelectedItem as ComboBoxItem)?.Tag is string tag)
            KennwertBox.Text = tag;
    }

    void Berechne()
    {
        if (ErgebnisKw is null) return; // UI noch nicht vollständig geladen

        if (!TryZahl(FlaecheBox.Text, out var flaeche) || !TryZahl(KennwertBox.Text, out var q)
            || flaeche <= 0 || q <= 0)
        {
            _gueltig = false;
            ErgebnisKw.Text = "—";
            ErgebnisFormel.Text = "";
            return;
        }

        var watt = flaeche * q;
        _gueltig = true;
        ErgebnisKw.Text = $"{(watt / 1000).ToString("0.0", De)} kW";
        ErgebnisFormel.Text = $"{Zahl(flaeche)} m² × {Zahl(q)} W/m² = {watt.ToString("#,##0", De)} W";

        var typ = (TypBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        _ergebnisText =
            "**Überschlägige Heizlast (Variante B)**\n" +
            $"- Beheizte Fläche: {Zahl(flaeche)} m²\n" +
            $"- Gebäude/Dämmung: {typ} ({Zahl(q)} W/m²)\n" +
            $"- Heizlast: {Zahl(flaeche)} m² × {Zahl(q)} W/m² = " +
            $"{watt.ToString("#,##0", De)} W ({(watt / 1000).ToString("0.0", De)} kW)";
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
        try
        {
            Clipboard.SetText(_ergebnisText);
            HinweisText.Text = "In die Zwischenablage kopiert.";
        }
        catch
        {
            HinweisText.Text = "Kopieren fehlgeschlagen.";
        }
    }

    static string Zahl(double d) => d.ToString("0.##", De);

    /// <summary>Zahl parsen, Komma wie Punkt akzeptieren.</summary>
    static bool TryZahl(string? s, out double wert)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
