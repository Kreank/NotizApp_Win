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

    /// <summary>
    /// Sorgt dafür, dass Docker läuft — startet Docker Desktop bei Bedarf
    /// automatisch und wartet, bis die Engine bereit ist.
    /// Liefert null wenn ok, sonst einen Hinweistext.
    /// </summary>
    public async Task<string?> StelleDockerBereitAsync(Action<string>? status, CancellationToken ct)
    {
        if (await DockerLaeuftAsync(ct)) return null;

        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(exe))
            return "Docker Desktop wurde nicht gefunden.\n\n" +
                   "Bitte Docker Desktop installieren und einmalig\n    .\\docker\\einrichten.ps1\nausführen.";

        status?.Invoke("Docker wird gestartet…");
        try
        {
            // -Autostart = wie beim Windows-Anmelde-Autostart: ohne Dashboard-Fenster
            Process.Start(new ProcessStartInfo(exe, "-Autostart") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return $"Docker Desktop konnte nicht gestartet werden:\n{ex.Message}";
        }

        var ende = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < ende)
        {
            await Task.Delay(3000, ct);
            if (await DockerLaeuftAsync(ct)) return null;
        }
        return "Docker ist nicht rechtzeitig gestartet (2-Minuten-Limit).\n" +
               "Bitte Docker Desktop manuell starten und erneut versuchen.";
    }

    async Task<bool> DockerLaeuftAsync(CancellationToken ct)
    {
        try
        {
            var (code, _, _) = await StarteAsync("docker",
                "info --format {{.ServerVersion}}", stdin: null, ct, TimeSpan.FromSeconds(10));
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prüft, ob das Claude-Image gebaut ist. Liefert null wenn ok, sonst einen Hinweistext.</summary>
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

    /// <summary>
    /// Freier Auftrag ("Erstelle ein Kundenschreiben als PDF", "Such Bilder zu …"):
    /// Claude arbeitet mit Schreibrechten und Internet in einem leeren
    /// Austauschordner, der als /ausgabe gemountet wird. Der Notizen-Ordner
    /// bleibt unerreichbar. Liefert die erzeugten Dateipfade.
    /// </summary>
    public async Task<List<string>> ErzeugeDokumentAsync(
        string auftrag, string body, string ausgabeOrdner, CancellationToken ct)
    {
        Directory.CreateDirectory(ausgabeOrdner);

        var envDatei = Path.Combine(SettingsService.SettingsOrdner, "claude.env");
        var envTeil = File.Exists(envDatei) ? $"--env-file {PsQuote(envDatei)} " : "";

        const string system =
            "Du arbeitest für einen SHK-Handwerksbetrieb (Sanitär/Heizung/Klima) und erstellst " +
            "Dateien im Ordner /ausgabe — NUR dort. " +
            "Für PDFs: Markdown/HTML schreiben und konvertieren mit " +
            "'pandoc eingabe.md -o name.pdf --pdf-engine=weasyprint'. " +
            "Bilder aus dem Netz lädst du mit curl (nur von seriösen Quellen, sinnvolle Dateinamen, " +
            "Formate jpg/png). Sprache Deutsch, sachlich. Erfinde keine Fakten — nutze nur, was in " +
            "der Notiz steht. Lösche Zwischendateien am Ende, sodass in /ausgabe nur die fertigen " +
            "Dateien liegen. Gib zum Schluss nur die erstellten Dateinamen aus.";

        var args = $"run --rm -i {envTeil}" +
                   $"-v {PsQuote(ausgabeOrdner + ":/ausgabe")} " +
                   $"-v {ConfigVolume}:/home/claude {Image} " +
                   $"-p --system-prompt {PsQuote(system)} " +
                   "--dangerously-skip-permissions --output-format text";
        var stdin = $"Auftrag: {auftrag}\n\nNotiz:\n{body}";

        var (code, stdout, stderr) = await StarteAsync("docker", args, stdin, ct,
            TimeSpan.FromMinutes(8));
        var dateien = Directory.Exists(ausgabeOrdner)
            ? Directory.EnumerateFiles(ausgabeOrdner).OrderBy(f => f).ToList()
            : new List<string>();

        if (dateien.Count == 0)
        {
            var fehler = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
            if (fehler.Length > 400) fehler = fehler[..400] + "…";
            throw new InvalidOperationException(code != 0
                ? $"Claude-Aufruf fehlgeschlagen:\n{fehler}"
                : $"Claude hat keine Dateien erstellt.\n\nAntwort:\n{fehler}");
        }
        return dateien;
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
