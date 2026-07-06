using System.IO;
using System.Windows;
using NotizApp.Services;

namespace NotizApp;

/// <summary>
/// Führt eine KI-Aktion aus und zeigt das Ergebnis als Vorschlag.
/// Übernommen wird erst nach Bestätigung; Text ist vorher editierbar.
/// Im Dokument-Modus (freier Auftrag) werden stattdessen die erzeugten
/// Dateien angezeigt.
/// </summary>
public partial class KiVorschlagWindow : Window
{
    readonly KiService _ki;
    readonly KiAktion _aktion;
    readonly string _body;
    readonly string? _auftrag; // gesetzt = Dokument-Modus
    readonly CancellationTokenSource _cts = new();

    /// <summary>Bestätigter Vorschlagstext; null wenn abgebrochen.</summary>
    public string? Ergebnis { get; private set; }

    /// <summary>Dokument-Modus: erzeugte Dateien (im Austauschordner).</summary>
    public List<string> ErzeugteDateien { get; } = new();
    string? _ausgabeOrdner;

    public KiVorschlagWindow(KiService ki, KiAktion aktion, string body)
    {
        _ki = ki;
        _aktion = aktion;
        _body = body;
        InitializeComponent();
        TitelText.Text = $"✨ {KiService.Beschreibung(aktion)}";
        Loaded += async (_, _) => await StarteAnfrage();
    }

    /// <summary>Dokument-Modus: freier Auftrag, Ergebnis sind Dateien.</summary>
    public KiVorschlagWindow(KiService ki, string auftrag, string body)
    {
        _ki = ki;
        _auftrag = auftrag;
        _body = body;
        InitializeComponent();
        TitelText.Text = "📄 Dateien erstellen";
        Loaded += async (_, _) => await StarteAnfrage();
    }

    async Task StarteAnfrage()
    {
        try
        {
            // Docker bei Bedarf automatisch starten (Statusanzeige im Dialog)
            var hinweis = await _ki.StelleDockerBereitAsync(
                s => Dispatcher.Invoke(() => StatusText.Text = s), _cts.Token);
            hinweis ??= await _ki.PruefeVerfuegbarAsync();
            if (hinweis is not null)
            {
                ZeigeFehler(hinweis);
                return;
            }
            if (_auftrag is not null)
            {
                StatusText.Text = "Claude arbeitet am Auftrag… (kann einige Minuten dauern)";
                _ausgabeOrdner = Path.Combine(Path.GetTempPath(),
                    "NotizApp-KI-" + Guid.NewGuid().ToString("N")[..8]);
                var dateien = await _ki.ErzeugeDokumentAsync(_auftrag, _body, _ausgabeOrdner, _cts.Token);
                ErzeugteDateien.AddRange(dateien);
                WartePanel.Visibility = Visibility.Collapsed;
                ErgebnisBox.Text = "Erstellte Dateien:\n\n" +
                    string.Join("\n", dateien.Select(d => "  " + Path.GetFileName(d)));
                ErgebnisBox.IsReadOnly = true;
                ErgebnisBox.Visibility = Visibility.Visible;
                UebernehmenButton.Content = "An Notiz anhängen";
                UebernehmenButton.Visibility = Visibility.Visible;
                HinweisText.Text = "Bilder werden als zeichenbare Blöcke eingefügt, andere Dateien neben der Notiz gespeichert und verlinkt.";
                return;
            }

            StatusText.Text = "Claude denkt nach…";
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

    void Fenster_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts.Cancel();
        // Austauschordner aufräumen, wenn nichts übernommen wurde
        if (DialogResult != true && _ausgabeOrdner is not null)
        {
            try { Directory.Delete(_ausgabeOrdner, recursive: true); } catch { }
        }
    }

    /// <summary>Vom Aufrufer nach dem Kopieren der Dateien aufzurufen.</summary>
    public void RaeumeAusgabeAuf()
    {
        if (_ausgabeOrdner is not null)
        {
            try { Directory.Delete(_ausgabeOrdner, recursive: true); } catch { }
        }
    }
}
