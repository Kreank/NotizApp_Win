using System.IO;
using System.Text.Json;

namespace NotizApp.Services;

/// <summary>Ein Eintrag im Dashboard-Feed „Neuigkeiten für dich".</summary>
public class FeedEintrag
{
    public string Titel { get; set; } = "";
    public string Zusammenfassung { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>"ki", "recht", "foerderung" oder "technik" — Unbekanntes (z.B.
    /// altes "branche" aus dem Cache) bleibt erhalten und wird in der Anzeige
    /// als „Branche" gruppiert.</summary>
    public string Kategorie { get; set; } = "";
    /// <summary>"JJJJ-MM-TT", so wie von Claude geliefert.</summary>
    public string Datum { get; set; } = "";
}

/// <summary>
/// Besorgt den Neuigkeiten-Feed fürs Dashboard: Claude recherchiert im
/// Container (KI, Recht &amp; Normen, Förderung, Technik), das Ergebnis wird
/// 12 Stunden in %APPDATA%\NotizApp\feed.json zwischengespeichert.
/// </summary>
public class FeedService
{
    /// <summary>Cache-Datei: Zeitstempel + Einträge.</summary>
    class FeedCache
    {
        public DateTime Stand { get; set; }
        public List<FeedEintrag> Eintraege { get; set; } = new();
    }

    static readonly TimeSpan MaxAlter = TimeSpan.FromHours(12);
    static string CachePfad => Path.Combine(SettingsService.SettingsOrdner, "feed.json");

    // Claude liefert die Schlüssel klein ("titel", "url" …) — case-insensitiv mappen
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    const string Auftrag =
        "Recherchiere die wichtigsten Neuigkeiten der letzten 14 Tage für den Inhaber " +
        "eines deutschen SHK-Handwerksbetriebs, vier Kategorien: " +
        "(1) kategorie=ki: KI-Neuigkeiten mit Praxisrelevanz (neue Modelle/Werkzeuge, " +
        "Claude/OpenAI/Google, KI im Handwerk). " +
        "(2) kategorie=recht: Gesetze, Normen und Pflichten — GEG, DIN/DVGW/VDI, " +
        "TRGI, Fristen. " +
        "(3) kategorie=foerderung: Förderprogramme — BEG, KfW, weitere Zuschüsse. " +
        "(4) kategorie=technik: Produkte, Werkzeuge und Praxis-Themen fürs " +
        "SHK-Handwerk. " +
        "Liefere ein JSON-Array mit 6 bis 10 Objekten: " +
        "{\"titel\":…, \"zusammenfassung\":2-3 Sätze deutsch mit der Kernaussage, " +
        "\"url\":echte recherchierte Quelle, " +
        "\"kategorie\":\"ki\"|\"recht\"|\"foerderung\"|\"technik\", " +
        "\"datum\":\"JJJJ-MM-TT\"}. Nur das JSON-Array, nichts davor oder danach.";

    readonly KiService _ki;

    public FeedService(KiService ki) => _ki = ki;

    /// <summary>Zeitstempel des zuletzt gelieferten Feeds (Cache oder frisch).</summary>
    public DateTime? Stand { get; private set; }

    /// <summary>
    /// Feed liefern: Cache jünger als 12 h wird direkt verwendet (außer
    /// <paramref name="erzwingen"/>), sonst recherchiert Claude neu.
    /// Fehler kommen als Exception mit verständlichem Text.
    /// </summary>
    public async Task<List<FeedEintrag>> HoleAsync(
        bool erzwingen, Action<string>? status, CancellationToken ct)
    {
        if (!erzwingen && LadeCache() is { } cache &&
            DateTime.Now - cache.Stand < MaxAlter && cache.Eintraege.Count > 0)
        {
            Stand = cache.Stand;
            return cache.Eintraege;
        }

        status?.Invoke("Docker wird geprüft…");
        if (await _ki.StelleDockerBereitAsync(status, ct) is string dockerProblem)
            throw new InvalidOperationException(dockerProblem);
        if (await _ki.PruefeVerfuegbarAsync() is string problem)
            throw new InvalidOperationException(problem);

        status?.Invoke("Claude recherchiert…");
        var antwort = await _ki.RechercheAsync(Auftrag, ct);
        var eintraege = Parse(antwort);
        if (eintraege.Count == 0)
            throw new InvalidOperationException(
                "Die Recherche hat kein verwertbares Ergebnis geliefert — bitte später erneut versuchen.");

        Stand = DateTime.Now;
        SchreibeCache(new FeedCache { Stand = Stand.Value, Eintraege = eintraege });
        return eintraege;
    }

    /// <summary>Claude-Antwort defensiv parsen: Markdown-Zäune und Umgebungstext
    /// abstreifen, fehlerhafte Einträge einzeln überspringen.</summary>
    static List<FeedEintrag> Parse(string antwort)
    {
        var text = antwort.Replace("```json", "").Replace("```", "");
        int von = text.IndexOf('[');
        int bis = text.LastIndexOf(']');
        var eintraege = new List<FeedEintrag>();
        if (von < 0 || bis <= von) return eintraege;

        try
        {
            using var json = JsonDocument.Parse(text[von..(bis + 1)]);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return eintraege;
            foreach (var element in json.RootElement.EnumerateArray())
            {
                try
                {
                    var e = element.Deserialize<FeedEintrag>(JsonOpts);
                    if (e is null || string.IsNullOrWhiteSpace(e.Titel)) continue;
                    if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        continue;
                    // Nur normalisieren — unbekannte Kategorien (z.B. altes
                    // "branche" aus dem Cache) bewusst nicht verwerfen
                    e.Kategorie = e.Kategorie.Trim().ToLowerInvariant();
                    eintraege.Add(e);
                }
                catch (JsonException)
                {
                    // einzelnen kaputten Eintrag überspringen
                }
            }
        }
        catch (JsonException)
        {
            // ganze Antwort unbrauchbar → leere Liste, Aufrufer meldet den Fehler
        }
        return eintraege;
    }

    static FeedCache? LadeCache()
    {
        try
        {
            if (!File.Exists(CachePfad)) return null;
            return JsonSerializer.Deserialize<FeedCache>(File.ReadAllText(CachePfad), JsonOpts);
        }
        catch
        {
            return null; // defekter Cache → einfach neu recherchieren
        }
    }

    static void SchreibeCache(FeedCache cache)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.SettingsOrdner);
            File.WriteAllText(CachePfad, JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch
        {
            // Cache ist nur Komfort — Schreibfehler nicht eskalieren
        }
    }
}
