using System.IO;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace NotizApp.Services;

/// <summary>
/// Lokale Sprach-Transkription mit Whisper (ggml-small, deutsch).
/// Das Modell liegt unter %APPDATA%\NotizApp\modelle\ und wird beim ersten
/// Einsatz automatisch von Hugging Face geladen — danach läuft alles offline,
/// keine Audiodaten verlassen den Rechner.
/// Erwartet WAV-Dateien mit 16 kHz mono 16 bit (liefert der AufnahmeService).
/// </summary>
public class TranskriptionService
{
    const string ModellUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";

    static string ModellPfad => Path.Combine(
        SettingsService.SettingsOrdner, "modelle", "ggml-small.bin");

    /// <summary>
    /// WAV transkribieren (deutsch). Liefert den Text, ein Segment je Zeile
    /// mit Zeitstempel [mm:ss] am Anfang. Status-Callback meldet Fortschritt
    /// (Modell-Download, Transkription) — Aufrufer muss selbst auf den
    /// UI-Thread wechseln.
    /// </summary>
    public async Task<string> TranskribiereAsync(
        string wavPfad, Action<string>? status, CancellationToken ct)
    {
        await StelleModellBereitAsync(status, ct);
        status?.Invoke("Transkription läuft…");

        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(ModellPfad);
            await using var processor = factory.CreateBuilder()
                .WithLanguage("de")
                .Build();

            var sb = new StringBuilder();
            await using var wav = File.OpenRead(wavPfad);
            await foreach (var segment in processor.ProcessAsync(wav, ct))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0) continue;
                sb.AppendLine(
                    $"[{(int)segment.Start.TotalMinutes:00}:{segment.Start.Seconds:00}] {text}");
            }
            return sb.ToString().TrimEnd('\n');
        }, ct);
    }

    /// <summary>
    /// Modell bei Bedarf herunterladen (~470 MB, einmalig): erst in eine
    /// .tmp-Datei, dann umbenennen — ein abgebrochener Download hinterlässt
    /// so nie ein kaputtes Modell.
    /// </summary>
    static async Task StelleModellBereitAsync(Action<string>? status, CancellationToken ct)
    {
        if (File.Exists(ModellPfad)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ModellPfad)!);
        var tmp = ModellPfad + ".tmp";
        status?.Invoke("Whisper-Modell wird geladen… (einmalig, ~470 MB)");

        try
        {
            // Kein 100-s-Standard-Timeout — der Download darf dauern, ct bricht ab
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var antwort = await http.GetAsync(
                ModellUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            antwort.EnsureSuccessStatusCode();
            var gesamt = antwort.Content.Headers.ContentLength;

            await using (var quelle = await antwort.Content.ReadAsStreamAsync(ct))
            await using (var ziel = File.Create(tmp))
            {
                var puffer = new byte[81920];
                long geladen = 0;
                int gelesen, letztesProzent = -1;
                while ((gelesen = await quelle.ReadAsync(puffer, ct)) > 0)
                {
                    await ziel.WriteAsync(puffer.AsMemory(0, gelesen), ct);
                    geladen += gelesen;
                    if (gesamt is > 0)
                    {
                        int prozent = (int)(geladen * 100 / gesamt.Value);
                        if (prozent != letztesProzent)
                        {
                            letztesProzent = prozent;
                            status?.Invoke($"Modell wird geladen… {prozent} %");
                        }
                    }
                }
            }
            File.Move(tmp, ModellPfad, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
