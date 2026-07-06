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
    bool _defekt;

    public async Task<string?> ErkenneAsync(StrokeCollection strokes)
    {
        if (_defekt || strokes.Count == 0) return null;
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
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // z.B. fehlendes Handschrift-Sprachpaket → Feature deaktivieren,
            // nicht bei jedem Strich erneut scheitern
            _defekt = true;
            return null;
        }
    }
}
