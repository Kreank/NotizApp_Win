using System.Diagnostics;
using System.IO;
using System.Text;
using NotizApp.Models;

namespace NotizApp.Services;

public enum KiAktion { Zusammenfassen, Aufbereiten, Aufgaben }

/// <summary>
/// KI-Anbindung (V2): Claude läuft headless in einem Docker-Container
/// (Image "notizapp-claude", Login im Volume "notizapp-claude-config").
///
/// Datenschutz: Es wird ausschließlich der Notiz-Body übergeben — ohne
/// Frontmatter-Kopf, also ohne Kundendaten. Der Container hat keinerlei
/// Zugriff auf den Notizen-Ordner.
/// </summary>
public class KiService
{
    const string Image = "notizapp-claude";
    const string ConfigVolume = "notizapp-claude-config";
    static readonly TimeSpan Zeitlimit = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Body für die KI erzeugen: Text-Blöcke unverändert, Tinten-Blöcke
    /// durch ihren erkannten Text ersetzt. Kein Frontmatter.
    /// </summary>
    public static string ErzeugeKiBody(IEnumerable<NoteBlock> bloecke)
    {
        var sb = new StringBuilder();
        foreach (var b in bloecke)
        {
            var text = b switch
            {
                TextBlockContent t => t.Text,
                InkBlockContent i => i.ErkannterText,
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text.Trim());
                sb.Append("\n\n");
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static string Beschreibung(KiAktion aktion) => aktion switch
    {
        KiAktion.Zusammenfassen => "Zusammenfassen",
        KiAktion.Aufbereiten => "Text aufbereiten",
        KiAktion.Aufgaben => "Aufgaben extrahieren",
        _ => aktion.ToString(),
    };

    static string Instruktion(KiAktion aktion) => aktion switch
    {
        KiAktion.Zusammenfassen =>
            "Du bekommst eine Arbeitsnotiz aus einem SHK-Handwerksbetrieb (Sanitär/Heizung/Klima). " +
            "Fasse sie in 3-5 knappen Markdown-Stichpunkten auf Deutsch zusammen. " +
            "Erfinde nichts dazu. Antworte ausschließlich mit den Stichpunkten, ohne Einleitung.",
        KiAktion.Aufbereiten =>
            "Du bekommst eine Arbeitsnotiz aus einem SHK-Handwerksbetrieb (Sanitär/Heizung/Klima), " +
            "oft stichwortartig oder aus Handschrift erkannt (mit möglichen Erkennungsfehlern). " +
            "Glätte und strukturiere den Text auf Deutsch: vollständige, klare Sätze bzw. saubere " +
            "Markdown-Stichpunkte, offensichtliche Erkennungsfehler korrigieren. Alle Fakten, Zahlen " +
            "und Namen exakt erhalten, nichts dazu erfinden. Antworte ausschließlich mit dem " +
            "überarbeiteten Text, ohne Einleitung.",
        KiAktion.Aufgaben =>
            "Du bekommst eine Arbeitsnotiz aus einem SHK-Handwerksbetrieb (Sanitär/Heizung/Klima). " +
            "Extrahiere alle darin enthaltenen oder klar implizierten Aufgaben/To-dos als " +
            "Markdown-Checkboxen im Format \"- [ ] Aufgabe\". Wenn ein Datum erkennbar ist, hänge " +
            "\" @JJJJ-MM-TT\" an. Erfinde keine Aufgaben. Antworte ausschließlich mit der Liste, " +
            "ohne Einleitung. Falls keine Aufgaben enthalten sind, antworte mit \"keine\".",
        _ => throw new ArgumentOutOfRangeException(nameof(aktion)),
    };

    /// <summary>Prüft, ob Docker läuft und das Image gebaut ist. Liefert null wenn ok, sonst einen Hinweistext.</summary>
    public async Task<string?> PruefeVerfuegbarAsync()
    {
        try
        {
            var (code, _, _) = await StarteAsync("docker",
                $"image inspect {Image}", stdin: null, CancellationToken.None,
                TimeSpan.FromSeconds(15));
            return code == 0
                ? null
                : "Der Claude-Container ist noch nicht eingerichtet.\n\n" +
                  "Einmalig im Projektordner ausführen:\n    .\\docker\\einrichten.ps1";
        }
        catch (Exception)
        {
            return "Docker wurde nicht gefunden oder läuft nicht.\n\n" +
                   "Bitte Docker Desktop starten und ggf. einmalig\n    .\\docker\\einrichten.ps1\nausführen.";
        }
    }

    /// <summary>Schickt Instruktion + Body an Claude im Container und liefert die Antwort.</summary>
    public async Task<string> FrageAsync(KiAktion aktion, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Die Notiz enthält keinen Text für die KI.");

        // Anmelde-Token (von docker\einrichten.ps1 abgelegt) als --env-file mitgeben,
        // damit er nicht auf der Kommandozeile sichtbar ist
        var envDatei = Path.Combine(SettingsService.SettingsOrdner, "claude.env");
        var envTeil = File.Exists(envDatei) ? $"--env-file {PsQuote(envDatei)} " : "";

        // Instruktion als System-Prompt (wird strikter befolgt), Notiz-Body via stdin
        var args = $"run --rm -i {envTeil}-v {ConfigVolume}:/home/claude {Image} " +
                   $"-p --system-prompt {PsQuote(Instruktion(aktion))} --output-format text";
        var (code, stdout, stderr) = await StarteAsync("docker", args, body, ct, Zeitlimit);

        if (code != 0)
        {
            var fehler = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
            if (fehler.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                fehler.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Claude ist im Container noch nicht angemeldet.\n\n" +
                    "Einmalig im Projektordner ausführen:\n    .\\docker\\einrichten.ps1");
            }
            if (fehler.Contains("Invalid bearer token", StringComparison.OrdinalIgnoreCase) ||
                fehler.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Der gespeicherte Claude-Token ist ungültig oder abgelaufen.\n\n" +
                    "Bitte neu erzeugen:\n    .\\docker\\einrichten.ps1");
            }
            if (fehler.Length > 400) fehler = fehler[..400] + "…";
            throw new InvalidOperationException(
                fehler.Length > 0 ? $"Claude-Aufruf fehlgeschlagen:\n{fehler}"
                                  : "Claude-Aufruf fehlgeschlagen (unbekannter Fehler).");
        }
        var antwort = stdout.Trim();
        if (antwort.Length == 0)
            throw new InvalidOperationException("Claude hat eine leere Antwort geliefert.");
        return antwort;
    }

    /// <summary>Argument für die Windows-Kommandozeile quoten (Backslash-Escaping für ").</summary>
    static string PsQuote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static async Task<(int Code, string Stdout, string Stderr)> StarteAsync(
        string exe, string args, string? stdin, CancellationToken ct, TimeSpan zeitlimit)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdin is not null)
            psi.StandardInputEncoding = Encoding.UTF8;

        using var prozess = Process.Start(psi)
            ?? throw new InvalidOperationException($"{exe} konnte nicht gestartet werden.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(zeitlimit);
        using var _ = cts.Token.Register(() =>
        {
            try { prozess.Kill(entireProcessTree: true); } catch { }
        });

        if (stdin is not null)
        {
            await prozess.StandardInput.WriteAsync(stdin);
            prozess.StandardInput.Close();
        }
        var stdoutTask = prozess.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = prozess.StandardError.ReadToEndAsync(CancellationToken.None);
        await prozess.WaitForExitAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        if (cts.IsCancellationRequested)
            throw new TimeoutException("Claude hat nicht rechtzeitig geantwortet (Zeitlimit erreicht).");

        return (prozess.ExitCode, await stdoutTask, await stderrTask);
    }
}
