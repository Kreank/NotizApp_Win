using System.IO;
using System.Text;
using System.Text.Json;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>
/// Anhang-Suchindex: liest den Text der Anhänge einer Notiz (PDF-Text über
/// PdfPig, Bilder über die Windows-OCR) und legt ihn in Note.AnhangText ab,
/// damit die Volltextsuche auch Anhänge findet. Ergebnisse werden je Datei
/// mit ihrem Änderungszeitpunkt in %APPDATA%\NotizApp\anhangtext.json
/// zwischengespeichert — nur geänderte Dateien werden neu gelesen.
/// Läuft komplett im Hintergrund und darf niemals crashen.
/// </summary>
public class AnhangIndexService
{
    /// <summary>App-weit eine Instanz (ein Cache, ein Lauf zur Zeit).</summary>
    public static AnhangIndexService Instanz { get; } = new();

    class CacheEintrag
    {
        /// <summary>Datei-mtime (UTC) zum Zeitpunkt der Extraktion.</summary>
        public DateTime GeaendertUtc { get; set; }
        public string Text { get; set; } = "";
    }

    static string CachePfad =>
        Path.Combine(SettingsService.SettingsOrdner, "anhangtext.json");

    /// <summary>Mehr Text pro Notiz bringt der Suche nichts mehr (~200 KB).</summary>
    const int MaxTextProNotiz = 200_000;

    /// <summary>Serialisiert die Läufe (Start-Indizierung + Speichern-Läufe).</summary>
    readonly SemaphoreSlim _laufSperre = new(1, 1);

    Dictionary<string, CacheEintrag>? _cache;

    Dictionary<string, CacheEintrag> LadeCache()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(CachePfad))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, CacheEintrag>>(
                    File.ReadAllText(CachePfad));
            }
        }
        catch
        {
            // defekter Cache → einfach neu aufbauen
        }
        return _cache ??= new(StringComparer.OrdinalIgnoreCase);
    }

    void SpeichereCache()
    {
        if (_cache is null) return;
        try
        {
            Directory.CreateDirectory(SettingsService.SettingsOrdner);
            File.WriteAllText(CachePfad, JsonSerializer.Serialize(_cache));
        }
        catch
        {
            // Cache ist nur eine Beschleunigung — Fehler beim Schreiben egal
        }
    }

    /// <summary>
    /// Alle Anhänge der Notizen indizieren und je Notiz AnhangText + Volltext
    /// aktualisieren. Task.Run-tauglich; Fehler je Datei werden übersprungen.
    /// (AnhangText/VolltextCache sind reine Strings ohne UI-Bindung — das
    /// Setzen vom Hintergrund-Thread ist ok; kein MeldeAnzeigeGeaendert hier!)
    /// </summary>
    public async Task IndiziereAsync(IEnumerable<Note> notizen, CancellationToken ct)
    {
        await _laufSperre.WaitAsync(ct);
        try
        {
            var cache = LadeCache();
            bool cacheGeaendert = false;

            foreach (var note in notizen.ToList())
            {
                if (ct.IsCancellationRequested) break;
                string? ordner;
                try { ordner = Path.GetDirectoryName(note.Pfad); }
                catch { continue; }
                if (string.IsNullOrEmpty(ordner)) continue;

                var dateien = note.Elemente.OfType<BildElement>().Select(b => b.Datei)
                    .Concat(note.Elemente.OfType<DateiElement>().Select(d => d.Datei))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                foreach (var datei in dateien)
                {
                    if (ct.IsCancellationRequested) break;
                    if (sb.Length >= MaxTextProNotiz) break;
                    try
                    {
                        var pfad = Path.Combine(ordner, datei);
                        if (!File.Exists(pfad)) continue;
                        var mtime = File.GetLastWriteTimeUtc(pfad);

                        string? text;
                        if (cache.TryGetValue(pfad, out var eintrag) &&
                            eintrag.GeaendertUtc == mtime)
                        {
                            text = eintrag.Text;
                        }
                        else
                        {
                            text = await ExtrahiereAsync(pfad);
                            cache[pfad] = new CacheEintrag
                            {
                                GeaendertUtc = mtime,
                                Text = text ?? "",
                            };
                            cacheGeaendert = true;
                        }
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.AppendLine(text);
                    }
                    catch
                    {
                        // eine unlesbare Datei stoppt nicht den Lauf
                    }
                }

                var neu = sb.ToString();
                if (neu.Length > MaxTextProNotiz) neu = neu[..MaxTextProNotiz];
                if (neu != note.AnhangText)
                {
                    note.AnhangText = neu;
                    note.BaueVolltext();
                }
            }

            if (cacheGeaendert) SpeichereCache();
        }
        finally
        {
            _laufSperre.Release();
        }
    }

    static readonly string[] OcrEndungen =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>Text einer Anhang-Datei extrahieren: PDF über PdfPig, Bilder
    /// über die Windows-OCR. Unbekannte Formate/Fehler → null.</summary>
    public static async Task<string?> ExtrahiereAsync(string pfad)
    {
        var endung = Path.GetExtension(pfad).ToLowerInvariant();
        if (endung == ".pdf")
            return KiService.PdfText(pfad);
        if (OcrEndungen.Contains(endung))
            return await OcrTextAsync(pfad);
        return null;
    }

    /// <summary>Bild mit der Windows-OCR lesen (Profilsprachen, Fallback Deutsch).
    /// Scheitert leise (webp/gif ohne Decoder, riesige Bilder, kein OCR-Paket).</summary>
    static async Task<string?> OcrTextAsync(string pfad)
    {
        try
        {
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(pfad);
            using var stream = await sf.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                    new Windows.Globalization.Language("de"));
            if (engine is null) return null;

            var ergebnis = await engine.RecognizeAsync(bitmap);
            var text = string.Join('\n', ergebnis.Lines
                .Select(z => z.Text)
                .Where(t => t.Trim().Length > 0)).Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null; // Decoder/OCR nicht verfügbar oder Bild unlesbar
        }
    }
}
