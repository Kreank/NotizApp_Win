using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>Eine Nachricht im KI-Chat (Sprechblase).</summary>
public class ChatNachricht
{
    public bool VonMir { get; init; }
    public bool IstFehler { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Von Claude in /ausgabe erstellte oder geladene Dateien.</summary>
    public ObservableCollection<ChatDatei> Dateien { get; } = new();
}

public record ChatDatei(string Pfad)
{
    public string Name => Path.GetFileName(Pfad);
}

/// <summary>
/// Freier KI-Chat rechts neben dem Editor: Recherche mit Quellenangaben,
/// Dateien erstellen (PDF/Word/HTML/Markdown/CSV), Bilder laden — Ergebnisse
/// lassen sich in die aktuelle Notiz legen oder auf dem PC speichern.
/// Kontext ist optional der Body der offenen Notiz (nie der Kundendaten-Kopf).
/// </summary>
public partial class KiChatPanel : UserControl
{
    /// <summary>Wird vom Host gesetzt (dieselbe Instanz wie im Editor).</summary>
    public KiService? Ki { get; set; }

    /// <summary>Liefert den KI-Body der aktuellen Notiz (nur Text/Handschrift —
    /// nie Kopf oder Titel), oder null wenn keine Notiz offen/leer.</summary>
    public Func<string?>? HoleNotizKontext { get; set; }

    /// <summary>Liefert die Anhang-Pfade der aktuellen Notiz (Bilder/Dateien).</summary>
    public Func<List<string>>? HoleNotizAnhaenge { get; set; }

    /// <summary>Text soll unten an die aktuelle Notiz angefügt werden.</summary>
    public event Action<string>? TextEinfuegen;
    /// <summary>Datei soll als Objekt in die aktuelle Notiz gelegt werden.</summary>
    public event Action<string>? DateiEinfuegen;

    readonly ObservableCollection<ChatNachricht> _nachrichten = new();
    string? _sessionId;
    string _ausgabeOrdner;
    bool _laeuft;
    CancellationTokenSource? _cts;

    public KiChatPanel()
    {
        InitializeComponent();
        NachrichtenListe.ItemsSource = _nachrichten;
        _ausgabeOrdner = NeuerAusgabeOrdner();
        if (KiService.FindeCodex() is null)
        {
            BildButton.IsEnabled = false;
            BildButton.ToolTip = "Bildgenerierung nicht verfügbar: Codex-Desktop-App (OpenAI) nicht gefunden.";
        }
    }

    static string NeuerAusgabeOrdner() =>
        Path.Combine(Path.GetTempPath(), "NotizApp-Chat",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    // ---------- Senden ----------

    void EingabeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            Senden_Click(sender, e);
            e.Handled = true;
        }
    }

    async void Senden_Click(object sender, RoutedEventArgs e)
    {
        if (_laeuft || Ki is null) return;
        var frage = EingabeBox.Text.Trim();
        if (frage.Length == 0) return;

        EingabeBox.Clear();
        LeerHinweis.Visibility = Visibility.Collapsed;
        _nachrichten.Add(new ChatNachricht { VonMir = true, Text = frage });
        ScrolleAnsEnde();

        // Notiz-Kontext voranstellen — bewusst ohne Titel: der gehört zum
        // Frontmatter-Kopf und kann Kundennamen enthalten
        var prompt = frage;
        if (KontextCheck.IsChecked == true && HoleNotizKontext?.Invoke() is { } kontext)
        {
            prompt = $"Meine aktuell geöffnete Notiz:\n\n{kontext}\n\n---\n\n{frage}";
        }

        _laeuft = true;
        _cts = new CancellationTokenSource();
        SendenButton.IsEnabled = false;
        AbbrechenButton.Visibility = Visibility.Visible;
        try
        {
            if (await Ki.StelleDockerBereitAsync(s => StatusText.Text = s, _cts.Token) is string dockerProblem)
            {
                ZeigeFehler(dockerProblem);
                return;
            }
            if (await Ki.PruefeVerfuegbarAsync() is string problem)
            {
                ZeigeFehler(problem);
                return;
            }

            StatusText.Text = "Claude arbeitet…";
            var vorher = VorhandeneDateien();
            var anhaenge = AnhangCheck.IsChecked == true
                ? HoleNotizAnhaenge?.Invoke() is { Count: > 0 } liste ? liste : null
                : null;
            var (antwort, session) = await Ki.ChatAsync(prompt, _sessionId, _ausgabeOrdner, anhaenge, _cts.Token);
            _sessionId = session;

            var nachricht = new ChatNachricht { VonMir = false, Text = antwort };
            foreach (var datei in VorhandeneDateien().Except(vorher, StringComparer.OrdinalIgnoreCase))
                nachricht.Dateien.Add(new ChatDatei(datei));
            _nachrichten.Add(nachricht);
        }
        catch (OperationCanceledException)
        {
            ZeigeFehler("Abgebrochen.");
        }
        catch (Exception ex)
        {
            ZeigeFehler(ex.Message);
        }
        finally
        {
            _laeuft = false;
            _cts = null;
            StatusText.Text = "";
            SendenButton.IsEnabled = true;
            AbbrechenButton.Visibility = Visibility.Collapsed;
            ScrolleAnsEnde();
            EingabeBox.Focus();
        }
    }

    /// <summary>🎨: Eingabetext als Bildauftrag an die lokale Codex-CLI statt an Claude.</summary>
    async void BildGenerieren_Click(object sender, RoutedEventArgs e)
    {
        if (_laeuft || Ki is null) return;
        var auftrag = EingabeBox.Text.Trim();
        if (auftrag.Length == 0)
        {
            ZeigeFehler("Erst die Bildbeschreibung ins Eingabefeld tippen, dann 🎨 drücken.");
            return;
        }

        EingabeBox.Clear();
        LeerHinweis.Visibility = Visibility.Collapsed;
        _nachrichten.Add(new ChatNachricht { VonMir = true, Text = $"🎨 {auftrag}" });
        ScrolleAnsEnde();

        _laeuft = true;
        _cts = new CancellationTokenSource();
        SendenButton.IsEnabled = false;
        BildButton.IsEnabled = false;
        AbbrechenButton.Visibility = Visibility.Visible;

        // Mitlaufende Uhr: Bildgenerierung dauert oft 2–4 Minuten — ohne sichtbaren
        // Fortschritt wirkt das schnell wie ein Hänger
        var startZeit = DateTime.Now;
        StatusText.Text = "Codex generiert Bild… (dauert oft 2–4 Minuten)";
        var uhr = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        uhr.Tick += (_, _) => StatusText.Text =
            $"Codex generiert Bild… {DateTime.Now - startZeit:m\\:ss} (dauert oft 2–4 Minuten)";
        uhr.Start();
        try
        {
            // Läuft lokal (Codex-App) — Docker wird hierfür nicht gebraucht
            var dateien = await Ki.GeneriereBilderAsync(auftrag, _ausgabeOrdner, _cts.Token);
            var nachricht = new ChatNachricht
            {
                Text = dateien.Count == 1
                    ? "Bild erstellt — mit 📌 in die Notiz legen oder mit 💾 speichern."
                    : $"{dateien.Count} Bilder erstellt — mit 📌 in die Notiz legen oder mit 💾 speichern.",
            };
            foreach (var datei in dateien)
                nachricht.Dateien.Add(new ChatDatei(datei));
            _nachrichten.Add(nachricht);
        }
        catch (OperationCanceledException)
        {
            ZeigeFehler("Abgebrochen.");
        }
        catch (Exception ex)
        {
            ZeigeFehler(ex.Message);
        }
        finally
        {
            uhr.Stop();
            _laeuft = false;
            _cts = null;
            StatusText.Text = "";
            SendenButton.IsEnabled = true;
            BildButton.IsEnabled = KiService.FindeCodex() is not null;
            AbbrechenButton.Visibility = Visibility.Collapsed;
            ScrolleAnsEnde();
            EingabeBox.Focus();
        }
    }

    void ZeigeFehler(string text) =>
        _nachrichten.Add(new ChatNachricht { IstFehler = true, Text = $"⚠ {text}" });

    HashSet<string> VorhandeneDateien() =>
        Directory.Exists(_ausgabeOrdner)
            ? Directory.EnumerateFiles(_ausgabeOrdner, "*", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void ScrolleAnsEnde() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            () => ChatScroller.ScrollToEnd());

    void Abbrechen_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void NeuerChat_Click(object sender, RoutedEventArgs e)
    {
        if (_laeuft) _cts?.Cancel();
        _nachrichten.Clear();
        _sessionId = null;
        _ausgabeOrdner = NeuerAusgabeOrdner();
        LeerHinweis.Visibility = Visibility.Visible;
    }

    // ---------- Ergebnisse übernehmen ----------

    void TextInNotiz_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ChatNachricht n)
            TextEinfuegen?.Invoke(n.Text);
    }

    void DateiInNotiz_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ChatDatei d)
            DateiEinfuegen?.Invoke(d.Pfad);
    }

    void DateiSpeichern_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatDatei d) return;
        var dialog = new SaveFileDialog
        {
            FileName = d.Name,
            Filter = "Alle Dateien|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.Copy(d.Pfad, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                $"Speichern fehlgeschlagen:\n{ex.Message}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
