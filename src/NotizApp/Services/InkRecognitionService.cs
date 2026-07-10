using System.Numerics;
using System.Windows.Ink;
using Windows.UI.Input.Inking;

namespace NotizApp.Services;

/// <summary>
/// Handschrifterkennung über die eingebaute Windows-Erkennung
/// (WinRT Windows.UI.Input.Inking via CsWinRT).
/// WPF-Strokes werden in WinRT-InkStrokes konvertiert und erkannt.
/// Degradiert sanft: bei jedem Fehler (kein Sprachpaket, WinRT nicht
/// verfügbar, …) kommt null zurück.
/// </summary>
public class InkRecognitionService
{
    InkRecognizerContainer? _recognizer;

    // Erst nach mehreren Fehlern in Folge aufgeben (fehlendes Sprachpaket o.Ä.);
    // ein einzelner Ausrutscher darf die Erkennung nicht bis zum Neustart lahmlegen.
    int _fehlerInFolge;
    const int MaxFehlerInFolge = 3;

    public async Task<string?> ErkenneAsync(StrokeCollection strokes)
    {
        if (_fehlerInFolge >= MaxFehlerInFolge || strokes.Count == 0) return null;
        try
        {
            _recognizer ??= new InkRecognizerContainer();

            var builder = new InkStrokeBuilder();
            var container = new InkStrokeContainer();
            foreach (var stroke in strokes)
            {
                var punkte = stroke.StylusPoints
                    .Select(p => new InkPoint(
                        new Windows.Foundation.Point(p.X, p.Y), p.PressureFactor))
                    .ToList();
                if (punkte.Count == 0) continue;
                container.AddStroke(
                    builder.CreateStrokeFromInkPoints(punkte, Matrix3x2.Identity));
            }
            if (container.GetStrokes().Count == 0) return null;

            var ergebnisse = await _recognizer.RecognizeAsync(
                container, InkRecognitionTarget.All);
            if (ergebnisse is null || ergebnisse.Count == 0) return null;

            var text = string.Join(' ',
                ergebnisse.Select(r => r.GetTextCandidates().FirstOrDefault())
                          .Where(t => !string.IsNullOrWhiteSpace(t)));
            _fehlerInFolge = 0;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _fehlerInFolge++;
            _recognizer = null; // beim nächsten Versuch frisch aufbauen
            App.Protokolliere("Handschrift-Erkennung", ex);
            return null;
        }
    }
}
