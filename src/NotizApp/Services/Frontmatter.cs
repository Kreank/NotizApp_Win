using System.Globalization;
using System.Text;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>
/// Parser/Writer für unser kontrolliertes YAML-Frontmatter-Subset und das
/// Body-Format mit ```ink-Fences. Kein vollwertiges YAML — wir schreiben die
/// Dateien selbst und lesen nur, was wir schreiben.
/// </summary>
public static class Frontmatter
{
    const string DatumFormat = "yyyy-MM-ddTHH:mm";

    // ---------- Parsen ----------

    /// <summary>Zerlegt den kompletten Dateiinhalt in Meta + Body-Text.</summary>
    public static (NoteMeta Meta, string Body) Parse(string inhalt)
    {
        var meta = new NoteMeta();
        inhalt = inhalt.Replace("\r\n", "\n");

        if (!inhalt.StartsWith("---\n"))
            return (meta, inhalt);

        int ende = inhalt.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (ende < 0)
            return (meta, inhalt);

        var kopf = inhalt[4..ende];
        var body = inhalt[(ende + 4)..].TrimStart('\n');

        string? aktuellerAbschnitt = null;
        foreach (var zeile in kopf.Split('\n'))
        {
            if (zeile.Trim().Length == 0) continue;

            bool eingerueckt = zeile.StartsWith("  ");
            var (key, wert) = TrenneKeyWert(zeile.Trim());
            if (key is null) continue;

            if (eingerueckt && aktuellerAbschnitt == "kunde")
            {
                switch (key)
                {
                    case "name": meta.Kunde.Name = wert; break;
                    case "telefon": meta.Kunde.Telefon = wert; break;
                    case "adresse": meta.Kunde.Adresse = wert; break;
                }
                continue;
            }

            aktuellerAbschnitt = null;
            switch (key)
            {
                case "titel": meta.Titel = wert ?? ""; break;
                case "typ": meta.Typ = wert ?? "leer"; break;
                case "erstellt": meta.Erstellt = ParseDatum(wert) ?? meta.Erstellt; break;
                case "geaendert": meta.Geaendert = ParseDatum(wert) ?? meta.Geaendert; break;
                case "dringlichkeit": meta.Dringlichkeit = wert; break;
                case "tags": meta.Tags = ParseTags(wert); break;
                case "kunde": aktuellerAbschnitt = "kunde"; break;
            }
        }
        return (meta, body);
    }

    static (string? Key, string? Wert) TrenneKeyWert(string zeile)
    {
        int i = zeile.IndexOf(':');
        if (i < 0) return (null, null);
        var key = zeile[..i].Trim();
        var wert = zeile[(i + 1)..].Trim();
        return (key, wert.Length == 0 ? null : wert);
    }

    static DateTime? ParseDatum(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    static List<string> ParseTags(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new();
        s = s.Trim().TrimStart('[').TrimEnd(']');
        return s.Split(',')
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
    }

    // ---------- Schreiben ----------

    public static string SchreibeKopf(NoteMeta meta)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"titel: {Bereinige(meta.Titel)}\n");
        sb.Append($"typ: {meta.Typ}\n");
        sb.Append($"erstellt: {meta.Erstellt.ToString(DatumFormat, CultureInfo.InvariantCulture)}\n");
        sb.Append($"geaendert: {meta.Geaendert.ToString(DatumFormat, CultureInfo.InvariantCulture)}\n");
        if (meta.Tags.Count > 0)
            sb.Append($"tags: [{string.Join(", ", meta.Tags.Select(Bereinige))}]\n");
        if (!meta.Kunde.IstLeer)
        {
            sb.Append("kunde:\n");
            if (!string.IsNullOrWhiteSpace(meta.Kunde.Name)) sb.Append($"  name: {Bereinige(meta.Kunde.Name)}\n");
            if (!string.IsNullOrWhiteSpace(meta.Kunde.Telefon)) sb.Append($"  telefon: {Bereinige(meta.Kunde.Telefon)}\n");
            if (!string.IsNullOrWhiteSpace(meta.Kunde.Adresse)) sb.Append($"  adresse: {Bereinige(meta.Kunde.Adresse)}\n");
        }
        if (!string.IsNullOrWhiteSpace(meta.Dringlichkeit))
            sb.Append($"dringlichkeit: {meta.Dringlichkeit}\n");
        sb.Append("---\n");
        return sb.ToString();
    }

    /// <summary>Zeilenumbrüche raus — unser Frontmatter ist strikt einzeilig pro Feld.</summary>
    static string Bereinige(string? s) =>
        (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();

    // ---------- Body: ink-Fences ----------

    public static List<NoteBlock> ParseBody(string body)
    {
        var bloecke = new List<NoteBlock>();
        var zeilen = body.Replace("\r\n", "\n").Split('\n');
        var text = new StringBuilder();

        void SchliesseText()
        {
            var t = text.ToString().Trim('\n');
            text.Clear();
            bloecke.Add(new TextBlockContent { Text = t });
        }

        for (int i = 0; i < zeilen.Length; i++)
        {
            if (zeilen[i].TrimEnd() == "```ink")
            {
                SchliesseText();
                var ink = new InkBlockContent();
                var erkannt = new StringBuilder();
                bool inText = false;
                i++;
                for (; i < zeilen.Length && zeilen[i].TrimEnd() != "```"; i++)
                {
                    var z = zeilen[i];
                    if (!inText && z.StartsWith("datei:"))
                        ink.Datei = z["datei:".Length..].Trim();
                    else if (!inText && z.StartsWith("bild:"))
                        ink.Bild = z["bild:".Length..].Trim() is { Length: > 0 } b ? b : null;
                    else if (!inText && z.StartsWith("muster:"))
                        ink.Muster = z["muster:".Length..].Trim() is { Length: > 0 } m ? m : null;
                    else if (!inText && z.StartsWith("hoehe:") &&
                             double.TryParse(z["hoehe:".Length..].Trim(), CultureInfo.InvariantCulture, out var h))
                        ink.Hoehe = h;
                    else if (!inText && z.StartsWith("text:"))
                    {
                        inText = true;
                        var rest = z["text:".Length..].TrimStart();
                        if (rest.Length > 0) erkannt.AppendLine(rest);
                    }
                    else if (inText)
                        erkannt.AppendLine(z);
                }
                ink.ErkannterText = erkannt.ToString().TrimEnd('\n');
                bloecke.Add(ink);
            }
            else
            {
                text.AppendLine(zeilen[i]);
            }
        }
        SchliesseText();

        // Mindestens ein Text-Block muss existieren (Editor-Invariante).
        if (!bloecke.Any(b => b is TextBlockContent))
            bloecke.Add(new TextBlockContent());
        return bloecke;
    }

    public static string SchreibeBody(List<NoteBlock> bloecke)
    {
        var sb = new StringBuilder();
        foreach (var b in bloecke)
        {
            switch (b)
            {
                case TextBlockContent t:
                    sb.Append(t.Text.Replace("\r\n", "\n").TrimEnd('\n'));
                    sb.Append("\n\n");
                    break;
                case InkBlockContent i:
                    sb.Append("```ink\n");
                    sb.Append($"datei: {i.Datei}\n");
                    if (!string.IsNullOrWhiteSpace(i.Bild))
                        sb.Append($"bild: {i.Bild}\n");
                    if (!string.IsNullOrWhiteSpace(i.Muster))
                        sb.Append($"muster: {i.Muster}\n");
                    sb.Append($"hoehe: {((int)i.Hoehe).ToString(CultureInfo.InvariantCulture)}\n");
                    if (!string.IsNullOrWhiteSpace(i.ErkannterText))
                        sb.Append($"text: {i.ErkannterText.Replace("\r\n", "\n").TrimEnd('\n')}\n");
                    sb.Append("```\n\n");
                    break;
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Kompletten Dateiinhalt erzeugen.</summary>
    public static string Schreibe(NoteMeta meta, List<NoteBlock> bloecke) =>
        SchreibeKopf(meta) + "\n" + SchreibeBody(bloecke);
}
