using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>
/// Sprach-Aufnahme im Editor (🎙-Button): Mikrofon + PC-Ton aufnehmen, lokal
/// mit Whisper transkribieren, die Misch-WAV als Anhang und das Transkript
/// als Textfeld an die Notiz hängen.
/// Button-Zustände: Bereit (🎙) → Aufnahme (⏺ m:ss, rot) → Transkription (⌛ …).
/// </summary>
public partial class NoteEditor
{
    enum AufnahmeZustand { Bereit, Aufnahme, Transkription }

    AufnahmeZustand _aufnahmeZustand = AufnahmeZustand.Bereit;
    AufnahmeService? _aufnahme;
    TranskriptionService? _transkription;
    DispatcherTimer? _aufnahmeTimer;
    DateTime _aufnahmeStart;
    Brush? _aufnahmeButtonFarbe; // Original-Foreground zum Zurücksetzen
    object? _aufnahmeToolTip;    // Original-ToolTip (mit dem § 201-Hinweis)

    async void Aufnahme_Click(object sender, RoutedEventArgs e)
    {
        switch (_aufnahmeZustand)
        {
            case AufnahmeZustand.Bereit:
                StarteAufnahme();
                break;
            case AufnahmeZustand.Aufnahme:
                await StoppeUndTranskribiereAsync();
                break;
            // Transkription: Button ist deaktiviert, hier landet kein Klick
        }
    }

    void StarteAufnahme()
    {
        if (_note is null) return;
        try
        {
            _aufnahme = new AufnahmeService();
            _aufnahme.Starte();
        }
        catch (Exception ex)
        {
            _aufnahme?.Dispose();
            _aufnahme = null;
            MessageBox.Show(Window.GetWindow(this)!,
                "Die Aufnahme konnte nicht gestartet werden.\n" +
                "Bitte prüfen, ob ein Mikrofon angeschlossen und in den Windows-" +
                $"Datenschutzeinstellungen für Apps freigegeben ist.\n\nDetails: {ex.Message}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _aufnahmeZustand = AufnahmeZustand.Aufnahme;
        _aufnahmeStart = DateTime.UtcNow;
        _aufnahmeButtonFarbe ??= AufnahmeButton.Foreground;
        _aufnahmeToolTip ??= AufnahmeButton.ToolTip;
        AufnahmeButton.Foreground = Brushes.Red;
        ZeigeAufnahmeDauer();

        if (_aufnahmeTimer is null)
        {
            _aufnahmeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _aufnahmeTimer.Tick += (_, _) => ZeigeAufnahmeDauer();
        }
        _aufnahmeTimer.Start();
    }

    void ZeigeAufnahmeDauer()
    {
        var dauer = DateTime.UtcNow - _aufnahmeStart;
        AufnahmeButton.Content = $"⏺ {(int)dauer.TotalMinutes}:{dauer.Seconds:00}";
    }

    async Task StoppeUndTranskribiereAsync()
    {
        if (_aufnahme is null) return;
        var aufnahme = _aufnahme;
        var notiz = _note; // Guard: Nutzer könnte während des await die Notiz wechseln
        _aufnahme = null;

        _aufnahmeZustand = AufnahmeZustand.Transkription;
        _aufnahmeTimer?.Stop();
        AufnahmeButton.Content = "⌛ …";
        AufnahmeButton.Foreground = _aufnahmeButtonFarbe;
        AufnahmeButton.IsEnabled = false;

        try
        {
            var wav = await aufnahme.StoppeAsync(CancellationToken.None);

            if (_note != notiz)
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "Die Notiz wurde während der Aufnahme gewechselt — " +
                    $"die Aufnahme liegt unter:\n{wav}",
                    "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // (a) Die fertige Misch-WAV als Anhang neben die Notiz
            FuegeDateiObjektAn(KopiereAnhang(wav));
            MeldeAenderung();

            // (b) Lokal transkribieren und als Textfeld anfügen
            _transkription ??= new TranskriptionService();
            var transkript = await _transkription.TranskribiereAsync(wav,
                s => Dispatcher.Invoke(() => ZeigeTranskriptionsStatus(s)),
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(transkript))
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "In der Aufnahme wurde keine Sprache erkannt.",
                    "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_note == notiz)
            {
                FuegeTextAn($"## 🎙 Gespräch {DateTime.Now:dd.MM.yyyy HH:mm}\n\n{transkript}");
            }
            LoescheLeise(wav);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                $"Aufnahme/Transkription fehlgeschlagen:\n{ex.Message}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            aufnahme.Dispose();
            SetzeAufnahmeButtonZurueck();
        }
    }

    /// <summary>Fortschritt am Button zeigen: Prozentwerte kompakt ("⌛ 42 %"),
    /// der volle Statustext wandert in den ToolTip.</summary>
    void ZeigeTranskriptionsStatus(string status)
    {
        AufnahmeButton.ToolTip = status;
        var m = System.Text.RegularExpressions.Regex.Match(status, @"(\d+)\s*%");
        AufnahmeButton.Content = m.Success ? $"⌛ {m.Groups[1].Value} %" : "⌛ …";
    }

    void SetzeAufnahmeButtonZurueck()
    {
        _aufnahmeZustand = AufnahmeZustand.Bereit;
        _aufnahmeTimer?.Stop();
        AufnahmeButton.Content = "🎙";
        AufnahmeButton.Foreground = _aufnahmeButtonFarbe;
        if (_aufnahmeToolTip is not null) AufnahmeButton.ToolTip = _aufnahmeToolTip;
        AufnahmeButton.IsEnabled = true;
    }

    static void LoescheLeise(string pfad)
    {
        try { System.IO.File.Delete(pfad); } catch { }
    }
}
