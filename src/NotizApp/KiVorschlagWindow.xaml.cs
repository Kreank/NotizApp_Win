using System.Windows;
using NotizApp.Services;

namespace NotizApp;

/// <summary>
/// Führt eine KI-Aktion aus und zeigt das Ergebnis als Vorschlag.
/// Übernommen wird erst nach Bestätigung; der Text ist vorher editierbar.
/// </summary>
public partial class KiVorschlagWindow : Window
{
    readonly KiService _ki;
    readonly KiAktion _aktion;
    readonly string _body;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Bestätigter Vorschlagstext; null wenn abgebrochen.</summary>
    public string? Ergebnis { get; private set; }

    public KiVorschlagWindow(KiService ki, KiAktion aktion, string body)
    {
        _ki = ki;
        _aktion = aktion;
        _body = body;
        InitializeComponent();
        TitelText.Text = $"✨ {KiService.Beschreibung(aktion)}";
        Loaded += async (_, _) => await StarteAnfrage();
    }

    async Task StarteAnfrage()
    {
        try
        {
            var hinweis = await _ki.PruefeVerfuegbarAsync();
            if (hinweis is not null)
            {
                ZeigeFehler(hinweis);
                return;
            }
            var antwort = await _ki.FrageAsync(_aktion, _body, _cts.Token);
            WartePanel.Visibility = Visibility.Collapsed;
            ErgebnisBox.Text = antwort;
            ErgebnisBox.Visibility = Visibility.Visible;
            UebernehmenButton.Visibility = Visibility.Visible;
            HinweisText.Text = "Vorschlag prüfen — Text ist vor dem Übernehmen editierbar.";
        }
        catch (OperationCanceledException)
        {
            // Fenster wurde geschlossen / abgebrochen
        }
        catch (Exception ex)
        {
            ZeigeFehler(ex.Message);
        }
    }

    void ZeigeFehler(string text)
    {
        WartePanel.Visibility = Visibility.Collapsed;
        FehlerText.Text = text;
        FehlerText.Visibility = Visibility.Visible;
        AbbrechenButton.Content = "Schließen";
    }

    void Uebernehmen_Click(object sender, RoutedEventArgs e)
    {
        Ergebnis = ErgebnisBox.Text.Trim();
        DialogResult = true;
    }

    void Abbrechen_Click(object sender, RoutedEventArgs e) => Close();

    void Fenster_Closing(object sender, System.ComponentModel.CancelEventArgs e) =>
        _cts.Cancel();
}
