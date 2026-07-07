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
    /// Body für die KI erzeugen: Textelemente in Leserichtung, danach der im
    /// Hintergrund erkannte Text der Handschrift. Kein Frontmatter.
    /// </summary>
    public static string ErzeugeKiBody(IEnumerable<string> texte, string tintenText)
    {
        var sb = new StringBuilder();
        foreach (var text in texte.Append(tintenText))
        {
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

    /// <summary>Schickt Instruktion + Body an Claude im Container und liefert die
    /// Antwort. Anhänge (Bilder/PDFs) werden — falls übergeben — read-only als
    /// /anhang gemountet und PDF-Text lokal extrahiert und angehängt.</summary>
    public async Task<string> FrageAsync(KiAktion aktion, string body,
        IReadOnlyList<string>? anhaenge, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Die Notiz enthält keinen Text für die KI.");

        // Anmelde-Token (von docker\einrichten.ps1 abgelegt) als --env-file mitgeben,
        // damit er nicht auf der Kommandozeile sichtbar ist
        var envDatei = Path.Combine(SettingsService.SettingsOrdner, "claude.env");
        var envTeil = File.Exists(envDatei) ? $"--env-file {PsQuote(envDatei)} " : "";

        var (bodyMitAnhang, mountTeil, tempOrdner) = BereiteAnhaengeVor(body, anhaenge);
        try
        {
            // Instruktion als System-Prompt (wird strikter befolgt), Notiz-Body via stdin.
            // Mit Anhängen braucht Claude Lesezugriff (Read-Werkzeug) → Permissions
            // überspringen; der Container ist ohnehin abgeschottet.
            var rechteTeil = mountTeil.Length > 0 ? "--dangerously-skip-permissions " : "";
            var args = $"run --rm -i {envTeil}{mountTeil}-v {ConfigVolume}:/home/claude {Image} " +
                       $"-p {rechteTeil}--system-prompt {PsQuote(Instruktion(aktion))} --output-format text";
            var zeit = mountTeil.Length > 0 ? TimeSpan.FromMinutes(6) : Zeitlimit;
            var (code, stdout, stderr) = await StarteAsync("docker", args, bodyMitAnhang, ct, zeit);

            if (code != 0) throw ClaudeFehler(stdout, stderr);
            var antwort = stdout.Trim();
            if (antwort.Length == 0)
                throw new InvalidOperationException("Claude hat eine leere Antwort geliefert.");
            return antwort;
        }
        finally
        {
            RaeumeAnhangOrdnerAuf(tempOrdner);
        }
    }

    // ---------- Anhänge für die KI (Opt-in über den Schalter im KI-Menü/Chat) ----------

    /// <summary>Text einer PDF lokal extrahieren (PdfPig); null bei Scan-PDFs ohne Text.</summary>
    public static string? PdfText(string pfad, int maxZeichen = 24000)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pfad);
            var sb = new StringBuilder();
            foreach (var seite in doc.GetPages())
            {
                sb.AppendLine(seite.Text);
                if (sb.Length > maxZeichen) break;
            }
            var text = sb.ToString().Trim();
            if (text.Length == 0) return null;
            return text.Length > maxZeichen ? text[..maxZeichen] + "…" : text;
        }
        catch
        {
            return null; // defekte/verschlüsselte PDF → ohne Textauszug weiter
        }
    }

    /// <summary>Anhänge in einen Temp-Ordner kopieren (wird read-only als /anhang
    /// gemountet — nie der Notizen-Ordner selbst) und PDF-Texte an den Body hängen.</summary>
    static (string Body, string MountTeil, string? TempOrdner) BereiteAnhaengeVor(
        string body, IReadOnlyList<string>? anhaenge)
    {
        if (anhaenge is not { Count: > 0 }) return (body, "", null);

        var temp = Path.Combine(Path.GetTempPath(), "NotizApp-Anhang-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var sb = new StringBuilder(body);
        var namen = new List<string>();
        foreach (var pfad in anhaenge.Where(File.Exists))
        {
            var name = Path.GetFileName(pfad);
            try { File.Copy(pfad, Path.Combine(temp, name), overwrite: true); }
            catch { continue; }
            namen.Add(name);
            if (pfad.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
                PdfText(pfad) is { } text)
            {
                sb.Append($"\n\n--- Anhang {name} (extrahierter PDF-Text) ---\n{text}");
            }
        }
        if (namen.Count == 0)
        {
            RaeumeAnhangOrdnerAuf(temp);
            return (body, "", null);
        }
        sb.Append("\n\n[Die Anhänge der Notiz liegen als Dateien unter /anhang: ");
        sb.Append(string.Join(", ", namen));
        sb.Append(" — sieh dir Bilder und Dokumente dort bei Bedarf direkt an und beziehe ihren Inhalt ein.]");
        return (sb.ToString(), $"-v {PsQuote(temp + ":/anhang:ro")} ", temp);
    }

    static void RaeumeAnhangOrdnerAuf(string? ordner)
    {
        if (ordner is null) return;
        try { Directory.Delete(ordner, recursive: true); } catch { }
    }

    static InvalidOperationException ClaudeFehler(string stdout, string stderr)
    {
        var fehler = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
        if (fehler.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
            fehler.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "Claude ist im Container noch nicht angemeldet.\n\n" +
                "Einmalig im Projektordner ausführen:\n    .\\docker\\einrichten.ps1");
        }
        if (fehler.Contains("Invalid bearer token", StringComparison.OrdinalIgnoreCase) ||
            fehler.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "Der gespeicherte Claude-Token ist ungültig oder abgelaufen.\n\n" +
                "Bitte neu erzeugen:\n    .\\docker\\einrichten.ps1");
        }
        if (fehler.Length > 400) fehler = fehler[..400] + "…";
        return new InvalidOperationException(
            fehler.Length > 0 ? $"Claude-Aufruf fehlgeschlagen:\n{fehler}"
                              : "Claude-Aufruf fehlgeschlagen (unbekannter Fehler).");
    }

    // ---------- Freier Chat ----------

    const string ChatSystem =
        "Du bist der eingebaute KI-Assistent einer Notiz-App in einem deutschen " +
        "SHK-Handwerksbetrieb (Sanitär/Heizung/Klima). Du chattest mit dem Inhaber. " +
        "Antworte auf Deutsch, sachlich und knapp, in Markdown. " +
        "Du darfst im Internet recherchieren. Bei Recherchen gibst du zu jeder " +
        "wichtigen Aussage die Quelle als URL an. Nützliche Bilder/Diagramme lädst du " +
        "mit curl nach /ausgabe (nur seriöse Quellen, Formate jpg/png, sinnvolle " +
        "Dateinamen). Dateien erstellst du ausschließlich im Ordner /ausgabe: " +
        "Markdown/HTML direkt, PDFs mit 'pandoc eingabe.md -o name.pdf " +
        "--pdf-engine=weasyprint', Word mit 'pandoc eingabe.md -o name.docx', " +
        "Excel mit python3 + openpyxl. Technische Diagramme/Schemata (Abläufe, " +
        "Anlagenschemata, Vergleiche) zeichnest du selbst: graphviz (dot), " +
        "matplotlib (python3) oder handgeschriebenes SVG, mit " +
        "'rsvg-convert datei.svg -o datei.png' als PNG exportiert. " +
        "Lösche Zwischendateien, sodass in /ausgabe nur fertige Dateien liegen. " +
        "Wenn dir eine Notiz mitgegeben wurde, beziehe dich darauf; erfinde keine " +
        "Fakten über den Betrieb oder Kunden.";

    /// <summary>
    /// Eine Chat-Nachricht an Claude senden. sessionId=null startet eine neue
    /// Unterhaltung; sonst wird die bestehende fortgesetzt (Verlauf liegt im
    /// Config-Volume). Der Austauschordner wird als /ausgabe gemountet — dort
    /// entstandene Dateien zeigt die App als Anhänge an.
    /// Liefert Antwort + Session-Id für die nächste Nachricht.
    /// </summary>
    public async Task<(string Antwort, string? SessionId)> ChatAsync(
        string nachricht, string? sessionId, string ausgabeOrdner,
        IReadOnlyList<string>? anhaenge, CancellationToken ct)
    {
        Directory.CreateDirectory(ausgabeOrdner);

        var envDatei = Path.Combine(SettingsService.SettingsOrdner, "claude.env");
        var envTeil = File.Exists(envDatei) ? $"--env-file {PsQuote(envDatei)} " : "";
        var resumeTeil = sessionId is null ? "" : $"--resume {PsQuote(sessionId)} ";

        var (nachrichtMitAnhang, mountTeil, tempOrdner) = BereiteAnhaengeVor(nachricht, anhaenge);
        try
        {
            var args = $"run --rm -i {envTeil}{mountTeil}" +
                       $"-v {PsQuote(ausgabeOrdner + ":/ausgabe")} " +
                       $"-v {ConfigVolume}:/home/claude {Image} " +
                       $"-p {resumeTeil}--system-prompt {PsQuote(ChatSystem)} " +
                       "--dangerously-skip-permissions --output-format json";

            var (code, stdout, stderr) = await StarteAsync("docker", args, nachrichtMitAnhang, ct,
                TimeSpan.FromMinutes(10));

            if (code != 0)
            {
                // Session im Volume verloren (z.B. Volume neu erstellt) → frisch starten
                if (sessionId is not null &&
                    (stdout + stderr).Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
                {
                    return await ChatAsync(nachricht, null, ausgabeOrdner, anhaenge, ct);
                }
                throw ClaudeFehler(stdout, stderr);
            }

            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(stdout);
                var antwort = json.RootElement.GetProperty("result").GetString() ?? "";
                var neueSession = json.RootElement.TryGetProperty("session_id", out var sid)
                    ? sid.GetString()
                    : sessionId;
                if (antwort.Trim().Length == 0)
                    throw new InvalidOperationException("Claude hat eine leere Antwort geliefert.");
                return (antwort.Trim(), neueSession);
            }
            catch (System.Text.Json.JsonException)
            {
                // Zur Sicherheit: unerwartetes Format → Rohtext anzeigen
                var roh = stdout.Trim();
                if (roh.Length == 0)
                    throw new InvalidOperationException("Claude hat eine leere Antwort geliefert.");
                return (roh, sessionId);
            }
        }
        finally
        {
            RaeumeAnhangOrdnerAuf(tempOrdner);
        }
    }

    /// <summary>
    /// Freier Auftrag ("Erstelle ein Kundenschreiben als PDF", "Such Bilder zu …"):
    /// Claude arbeitet mit Schreibrechten und Internet in einem leeren
    /// Austauschordner, der als /ausgabe gemountet wird. Der Notizen-Ordner
    /// bleibt unerreichbar. Liefert die erzeugten Dateipfade.
    /// </summary>
    public async Task<List<string>> ErzeugeDokumentAsync(
        string auftrag, string body, string ausgabeOrdner,
        IReadOnlyList<string>? anhaenge, CancellationToken ct)
    {
        Directory.CreateDirectory(ausgabeOrdner);

        var envDatei = Path.Combine(SettingsService.SettingsOrdner, "claude.env");
        var envTeil = File.Exists(envDatei) ? $"--env-file {PsQuote(envDatei)} " : "";

        const string system =
            "Du arbeitest für einen SHK-Handwerksbetrieb (Sanitär/Heizung/Klima) und erstellst " +
            "Dateien im Ordner /ausgabe — NUR dort. " +
            "Für PDFs: Markdown/HTML schreiben und konvertieren mit " +
            "'pandoc eingabe.md -o name.pdf --pdf-engine=weasyprint'. Word: pandoc -o name.docx. " +
            "Excel: python3 + openpyxl. Technische Diagramme/Schemata zeichnest du selbst mit " +
            "graphviz (dot), matplotlib (python3) oder SVG + 'rsvg-convert datei.svg -o datei.png'. " +
            "Bilder aus dem Netz lädst du mit curl (nur von seriösen Quellen, sinnvolle Dateinamen, " +
            "Formate jpg/png). Sprache Deutsch, sachlich. Erfinde keine Fakten — nutze nur, was in " +
            "der Notiz steht. Lösche Zwischendateien am Ende, sodass in /ausgabe nur die fertigen " +
            "Dateien liegen. Gib zum Schluss nur die erstellten Dateinamen aus.";

        var stdinBasis = $"Auftrag: {auftrag}\n\nNotiz:\n{body}";
        var (stdin, mountTeil, tempOrdner) = BereiteAnhaengeVor(stdinBasis, anhaenge);
        List<string> dateien;
        int code;
        string stdout, stderr;
        try
        {
            var args = $"run --rm -i {envTeil}{mountTeil}" +
                       $"-v {PsQuote(ausgabeOrdner + ":/ausgabe")} " +
                       $"-v {ConfigVolume}:/home/claude {Image} " +
                       $"-p --system-prompt {PsQuote(system)} " +
                       "--dangerously-skip-permissions --output-format text";

            (code, stdout, stderr) = await StarteAsync("docker", args, stdin, ct,
                TimeSpan.FromMinutes(8));
        }
        finally
        {
            RaeumeAnhangOrdnerAuf(tempOrdner);
        }
        dateien = Directory.Exists(ausgabeOrdner)
            ? Directory.EnumerateFiles(ausgabeOrdner, "*", SearchOption.AllDirectories)
                .OrderBy(f => f).ToList()
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

    // ---------- Bildgenerierung über die lokale Codex-CLI ----------

    /// <summary>Pfad zur codex.exe der Codex-Desktop-App (OpenAI), null wenn nicht installiert.</summary>
    public static string? FindeCodex()
    {
        try
        {
            var bin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI", "Codex", "bin");
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static string CodexBilderOrdner => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "generated_images");

    /// <summary>
    /// KI-generierte Bilder (Fotos/Illustrationen/Logos) über die lokale
    /// Codex-CLI erzeugen (imagegen-Werkzeug des Codex-Abos). Codex läuft
    /// read-only in einem leeren Ordner und bekommt ausschließlich den
    /// Auftragstext — keine Notizen. Das image_gen-Werkzeug legt die PNGs
    /// unter ~/.codex/generated_images/&lt;session&gt;/ ab; von dort kopiert
    /// die App sie in den Ausgabeordner und liefert die Pfade zurück.
    /// </summary>
    public async Task<List<string>> GeneriereBilderAsync(
        string auftrag, string ausgabeOrdner, CancellationToken ct)
    {
        var codex = FindeCodex() ?? throw new InvalidOperationException(
            "Die Codex-App (OpenAI) wurde nicht gefunden — sie übernimmt die Bildgenerierung.\n" +
            "Bitte die Codex-Desktop-App installieren und anmelden.");
        Directory.CreateDirectory(ausgabeOrdner);

        var prompt =
            "Generiere mit deiner Bildgenerierungs-Fähigkeit (imagegen / image_gen) die " +
            "gewünschten Bilder. Kopiere oder verschiebe KEINE Dateien (die Sitzung ist " +
            "read-only) — die generierten Bilder werden automatisch abgeholt. " +
            $"Auftrag: {auftrag}";
        var args = $"exec -C {PsQuote(ausgabeOrdner)} --skip-git-repo-check " +
                   $"-s read-only {PsQuote(prompt)}";

        // stdin leer übergeben und schließen — sonst wartet codex exec auf Eingabe
        var startZeit = DateTime.UtcNow.AddMinutes(-1); // kleine Uhren-Toleranz
        var (code, stdout, stderr) = await StarteAsync(codex, args, "", ct,
            TimeSpan.FromMinutes(8));
        if (code != 0)
        {
            var fehler = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
            if (fehler.Length > 400) fehler = fehler[^400..];
            throw new InvalidOperationException($"Codex-Aufruf fehlgeschlagen:\n{fehler}");
        }

        // Session-Ordner aus dem Kopf der exec-Ausgabe ("session id: <uuid>";
        // das Format hat sich zwischen Codex-Versionen schon geändert, daher tolerant)
        var session = System.Text.RegularExpressions.Regex.Match(
            stdout + "\n" + stderr, @"(?:session|conversation)[ _]?id:?\s+([0-9a-f\-]{16,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var quelle = session.Success
            ? Path.Combine(CodexBilderOrdner, session.Groups[1].Value)
            : null;
        var bilder = quelle is not null && Directory.Exists(quelle)
            ? Directory.EnumerateFiles(quelle).Where(IstBildDatei).OrderBy(f => f).ToList()
            : new List<string>();

        // Fallback: liefert der Kopf keine Session-ID (mehr), nehmen wir alle Bilder,
        // die seit dem Start dieses Aufrufs unter generated_images entstanden sind
        if (bilder.Count == 0 && Directory.Exists(CodexBilderOrdner))
        {
            bilder = Directory
                .EnumerateFiles(CodexBilderOrdner, "*", SearchOption.AllDirectories)
                .Where(IstBildDatei)
                .Where(f => File.GetLastWriteTimeUtc(f) >= startZeit)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
        }

        if (bilder.Count == 0)
        {
            var antwort = stdout.Trim();
            if (antwort.Length > 400) antwort = antwort[^400..];
            throw new InvalidOperationException(
                $"Es wurde kein generiertes Bild gefunden (unter {CodexBilderOrdner}).\n\n" +
                $"Codex-Antwort:\n{antwort}");
        }

        // In den Austauschordner kopieren (sprechende Namen statt ig_<hash>.png)
        var ziele = new List<string>();
        int n = 1;
        foreach (var bild in bilder)
        {
            var ziel = Path.Combine(ausgabeOrdner,
                $"bild-{DateTime.Now:HHmmss}-{n++}{Path.GetExtension(bild)}");
            File.Copy(bild, ziel, overwrite: true);
            ziele.Add(ziel);
        }
        return ziele;
    }

    static bool IstBildDatei(string f) =>
        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

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
