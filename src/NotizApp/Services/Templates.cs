using System.IO;
using System.Text.Json;

namespace NotizApp.Services;

/// <summary>Eine Notiz-Vorlage für neue Notizen / Schnellerfassung.</summary>
public record Vorlage(string Key, string Icon, string Name, string TitelVorschlag, string Body);

/// <summary>Vom Nutzer angelegte Vorlage (Einstellungen → Eigene Vorlagen).
/// In Titel und Inhalt stehen {datum} und {zeit} für den Einfüge-Zeitpunkt.</summary>
public class EigeneVorlage
{
    public string Key { get; set; } = "";
    public string Icon { get; set; } = "📋";
    public string Name { get; set; } = "";
    public string TitelVorschlag { get; set; } = "";
    public string Body { get; set; } = "";

    public string NeuerKey() => Key = "eigen-" + Guid.NewGuid().ToString("N")[..8];

    public EigeneVorlage Kopie() => (EigeneVorlage)MemberwiseClone();
}

/// <summary>Eingebaute Vorlagen (anruf, meeting, aufgabe, leer) plus die eigenen
/// des Nutzers aus vorlagen.json im Datenordner.</summary>
public static class Templates
{
    /// <summary>Eigene Vorlagen; beim Start via LadeEigene gefüllt.</summary>
    public static List<EigeneVorlage> Eigene { get; private set; } = new();

    // Bewusst pro Zugriff neu aufgebaut: Titel/Body können das aktuelle Datum enthalten,
    // und die App läuft als Tray-App oft tagelang.
    public static IReadOnlyList<Vorlage> Alle
    {
        get
        {
            var liste = new List<Vorlage>
            {
                new("anruf", "📞", "Anruf",
                    "Anruf",
                    "**Anliegen:**\n\n\n**Vereinbart:**\n\n\n- [ ] Rückruf\n"),
                new("meeting", "👥", "Meeting",
                    $"Besprechung {DateTime.Now:dd.MM.yyyy}",
                    "**Teilnehmer:**\n\n\n**Notizen:**\n\n\n**Aufgaben:**\n\n- [ ] \n"),
                new("aufgabe", "☑", "Aufgabe",
                    "Aufgabe",
                    "- [ ] \n"),
            };
            liste.AddRange(Eigene.Select(e => new Vorlage(
                e.Key,
                string.IsNullOrWhiteSpace(e.Icon) ? "📋" : e.Icon.Trim(),
                e.Name,
                ErsetzePlatzhalter(e.TitelVorschlag),
                ErsetzePlatzhalter(e.Body))));
            liste.Add(new("leer", "📄", "Leere Notiz", "", ""));
            return liste;
        }
    }

    public static Vorlage Hole(string key) =>
        Alle.FirstOrDefault(v => v.Key == key) ?? Alle[^1];

    public static string Icon(string typ) => Hole(typ).Icon;

    static string ErsetzePlatzhalter(string s) => s
        .Replace("{datum}", DateTime.Now.ToString("dd.MM.yyyy"))
        .Replace("{zeit}", DateTime.Now.ToString("HH:mm"));

    // ---------- Eigene Vorlagen laden/speichern (vorlagen.json im Datenordner) ----------

    static string Pfad(string datenOrdner) => Path.Combine(datenOrdner, "vorlagen.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Beim App-Start aufrufen. Fehlt die Datei, werden Beispiel-Vorlagen
    /// (Begehung, Notfall-Anruf) angelegt — als Startpunkt zum Anpassen.</summary>
    public static void LadeEigene(string datenOrdner)
    {
        try
        {
            if (File.Exists(Pfad(datenOrdner)))
            {
                Eigene = JsonSerializer.Deserialize<List<EigeneVorlage>>(
                    File.ReadAllText(Pfad(datenOrdner))) ?? new();
                return;
            }
            SpeichereEigene(datenOrdner, Beispiele());
        }
        catch
        {
            Eigene = new(); // defekte/nicht lesbare Datei → ohne eigene Vorlagen weiterarbeiten
        }
    }

    public static void SpeichereEigene(string datenOrdner, List<EigeneVorlage> vorlagen)
    {
        Eigene = vorlagen;
        try
        {
            Directory.CreateDirectory(datenOrdner);
            File.WriteAllText(Pfad(datenOrdner), JsonSerializer.Serialize(vorlagen, JsonOpts));
        }
        catch
        {
            // Ordner nicht beschreibbar → Vorlagen gelten wenigstens für diese Sitzung
        }
    }

    static List<EigeneVorlage> Beispiele() => new()
    {
        new EigeneVorlage
        {
            Key = "eigen-begehung",
            Icon = "🏠",
            Name = "Begehung",
            TitelVorschlag = "Begehung {datum}",
            Body =
                "**Objekt / Anlage:**\n\n\n" +
                "**Aufmaß / Material:**\n\n\n" +
                "- [ ] Fotos gemacht (Anlage, Typenschild, Zugänge)\n" +
                "- [ ] Aufmaß genommen\n" +
                "- [ ] Absperrungen / Anschlüsse geprüft\n" +
                "- [ ] Materialbedarf notiert\n" +
                "- [ ] Nächste Schritte mit Kunde besprochen\n\n" +
                "**Sonstiges:**\n",
        },
        new EigeneVorlage
        {
            Key = "eigen-notfall",
            Icon = "🚨",
            Name = "Notfall-Anruf",
            TitelVorschlag = "Notfall {datum} {zeit}",
            Body =
                "**Was ist passiert? (Wasser/Gas/Heizung)**\n\n\n" +
                "**Seit wann? Was wurde schon unternommen?**\n\n\n" +
                "- [ ] Rückrufnummer notiert\n" +
                "- [ ] Adresse + Zugang geklärt (Schlüssel, Etage)\n" +
                "- [ ] Absperren erklärt (Wasser / Gas / Strom)\n" +
                "- [ ] Foto vom Schaden schicken lassen\n" +
                "- [ ] Anfahrt / Termin zugesagt:\n",
        },
    };
}
