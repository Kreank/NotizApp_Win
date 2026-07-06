namespace NotizApp.Services;

/// <summary>Eine Notiz-Vorlage für neue Notizen / Schnellerfassung.</summary>
public record Vorlage(string Key, string Icon, string Name, string TitelVorschlag, string Body);

/// <summary>Eingebaute Vorlagen (anruf, meeting, aufgabe, leer).</summary>
public static class Templates
{
    // Bewusst pro Zugriff neu aufgebaut: der Meeting-Titel enthält das aktuelle Datum,
    // und die App läuft als Tray-App oft tagelang.
    public static IReadOnlyList<Vorlage> Alle => new List<Vorlage>
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
        new("leer", "📄", "Leere Notiz",
            "",
            ""),
    };

    public static Vorlage Hole(string key) =>
        Alle.FirstOrDefault(v => v.Key == key) ?? Alle[^1];

    public static string Icon(string typ) => Hole(typ).Icon;
}
