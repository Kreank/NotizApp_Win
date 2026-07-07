using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>
/// Parser/Writer für unser kontrolliertes YAML-Frontmatter-Subset und das
/// Freiform-Body-Format: ein ```tinte-Fence (eine ISF pro Notiz) plus
/// &lt;!--el …--&gt;-Marker für frei platzierte Text-/Bild-/Datei-Elemente.
/// Kein vollwertiges YAML/HTML — wir schreiben die Dateien selbst und lesen
/// nur, was wir schreiben. Das alte Blockformat (```ink-Fences) wird beim
/// Lesen erkannt und in Elemente migriert.
/// </summary>
public static partial class Frontmatter
{
    const string DatumFormat = "yyyy-MM-ddTHH:mm";

    // ---------- Parsen: Kopf ----------

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

    // ---------- Schreiben: Kopf ----------

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

    // ---------- Body: Elemente + Tinte ----------

    [GeneratedRegex("""(\w+)=(?:"([^"]*)"|(\S+?))(?=\s|-->|$)""")]
    private static partial Regex AttrRegex();

    /// <summary>Füllt Elemente/Tinte/Fläche der Notiz aus dem Body (beide Formate).</summary>
    public static void ParseBody(string body, Note note)
    {
        body = body.Replace("\r\n", "\n");
        if (body.Contains("```ink\n") || body.Contains("```ink\r") ||
            (!body.Contains("<!--el ") && !body.Contains("```tinte")))
        {
            ParseAltesBlockFormat(body, note);
            return;
        }

        var zeilen = body.Split('\n');
        TextElement? offenerText = null;
        TabelleElement? offeneTabelle = null;
        var textZeilen = new StringBuilder();

        void SchliesseText()
        {
            if (offenerText is not null)
            {
                offenerText.Text = textZeilen.ToString().Trim('\n');
                textZeilen.Clear();
                offenerText = null;
            }
            if (offeneTabelle is not null)
            {
                NormalisiereTabelle(offeneTabelle);
                offeneTabelle = null;
            }
        }

        for (int i = 0; i < zeilen.Length; i++)
        {
            var zeile = zeilen[i];

            if (zeile.TrimEnd() == "```tinte")
            {
                SchliesseText();
                var erkannt = new StringBuilder();
                bool inText = false;
                i++;
                for (; i < zeilen.Length && zeilen[i].TrimEnd() != "```"; i++)
                {
                    var z = zeilen[i];
                    if (!inText && z.StartsWith("datei:"))
                        note.TintenDatei = z["datei:".Length..].Trim();
                    else if (!inText && z.StartsWith("hoehe:") &&
                             double.TryParse(z["hoehe:".Length..].Trim(), CultureInfo.InvariantCulture, out var h))
                        note.FlaecheHoehe = h;
                    else if (!inText && z.StartsWith("muster:"))
                        note.Muster = z["muster:".Length..].Trim() is { Length: > 0 } m ? m : null;
                    else if (!inText && z.StartsWith("text:"))
                    {
                        inText = true;
                        var rest = z["text:".Length..].TrimStart();
                        if (rest.Length > 0) erkannt.AppendLine(rest);
                    }
                    else if (inText)
                        erkannt.AppendLine(z);
                }
                note.TintenText = erkannt.ToString().TrimEnd('\n');
                continue;
            }

            if (zeile.StartsWith("<!--el ") && zeile.TrimEnd().EndsWith("-->"))
            {
                SchliesseText();
                var attrs = new Dictionary<string, string>();
                foreach (Match m in AttrRegex().Matches(zeile))
                    attrs[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

                var typ = zeile["<!--el ".Length..].TrimStart().Split(' ', 2)[0];
                double Zahl(string key, double std) =>
                    attrs.TryGetValue(key, out var v) &&
                    double.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : std;

                NoteElement? el = typ switch
                {
                    "text" => new TextElement
                    {
                        Farbe = attrs.TryGetValue("farbe", out var f) && f.Length > 0 ? f : null,
                    },
                    "bild" => new BildElement
                    {
                        Datei = attrs.GetValueOrDefault("datei", ""),
                        Hoehe = Zahl("h", 240),
                    },
                    "datei" => new DateiElement
                    {
                        Datei = attrs.GetValueOrDefault("datei", ""),
                        Hoehe = Zahl("h", 96),
                        Seite = (int)Zahl("seite", 0),
                    },
                    "link" => new LinkElement
                    {
                        Url = attrs.GetValueOrDefault("url", ""),
                        Titel = attrs.GetValueOrDefault("titel", ""),
                        Hoehe = Zahl("h", 76),
                        VorschauDatei = attrs.GetValueOrDefault("vorschau", ""),
                        VorschauScroll = Zahl("vscroll", 0),
                        VorschauEingepasst =
                            attrs.GetValueOrDefault("vmodus", "") == "einpassen",
                    },
                    "tabelle" => new TabelleElement
                    {
                        SpaltenBreiten = ZahlListe(attrs.GetValueOrDefault("sb")),
                        ZeilenHoehen = ZahlListe(attrs.GetValueOrDefault("zh")),
                    },
                    _ => null,
                };
                if (el is null) continue;
                el.X = Zahl("x", 0);
                el.Y = Zahl("y", 0);
                el.Breite = Zahl("b", 620);
                note.Elemente.Add(el);
                if (el is TextElement te) offenerText = te;
                if (el is TabelleElement tab) offeneTabelle = tab;
                continue;
            }

            if (offeneTabelle is not null)
            {
                // Markdown-Tabellenzeilen einsammeln; alles andere beendet die Tabelle
                if (zeile.TrimStart().StartsWith('|'))
                    offeneTabelle.Zeilen.Add(ParseTabellenZeile(zeile));
                else if (zeile.Trim().Length > 0)
                    SchliesseText();
                continue;
            }

            if (offenerText is not null)
                textZeilen.AppendLine(zeile);
            // Zeilen außerhalb jedes Elements (sollte es nicht geben) werden ignoriert.
        }
        SchliesseText();
    }

    // ---------- Markdown-Tabellen ----------

    /// <summary>"120,340,80" → Liste positiver Zahlen (für Spaltenbreiten/Zeilenhöhen).</summary>
    static List<double> ZahlListe(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? new()
            : s.Split(',')
                .Select(t => double.TryParse(t.Trim(), CultureInfo.InvariantCulture, out var d) ? d : 0)
                .Where(d => d > 0)
                .ToList();

    /// <summary>Zelleninhalt fürs Markdown: Pipes und Umbrüche maskieren.</summary>
    static string MdZelle(string s) => s
        .Replace("\\", "\\\\")
        .Replace("|", "\\|")
        .Replace("\r\n", "\n")
        .Replace("\n", "<br>");

    static string MdZelleZurueck(string s)
    {
        s = s.Trim().Replace("<br>", "\n");
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>"| a | b |" → Zellen, maskierte \| bleiben Inhalt.</summary>
    static List<string> ParseTabellenZeile(string zeile)
    {
        var inhalt = zeile.Trim();
        if (inhalt.StartsWith('|')) inhalt = inhalt[1..];
        if (inhalt.EndsWith('|') && !inhalt.EndsWith("\\|")) inhalt = inhalt[..^1];

        var zellen = new List<string>();
        var aktuelle = new StringBuilder();
        for (int i = 0; i < inhalt.Length; i++)
        {
            if (inhalt[i] == '\\' && i + 1 < inhalt.Length)
            {
                aktuelle.Append(inhalt[i]).Append(inhalt[i + 1]);
                i++;
            }
            else if (inhalt[i] == '|')
            {
                zellen.Add(MdZelleZurueck(aktuelle.ToString()));
                aktuelle.Clear();
            }
            else
            {
                aktuelle.Append(inhalt[i]);
            }
        }
        zellen.Add(MdZelleZurueck(aktuelle.ToString()));
        return zellen;
    }

    /// <summary>Markdown-Trennzeile (|---|---|) entfernen und alle Zeilen auf
    /// gleiche Spaltenzahl auffüllen.</summary>
    static void NormalisiereTabelle(TabelleElement tab)
    {
        tab.Zeilen.RemoveAll(z =>
            z.Count > 0 && z.All(zelle => zelle.Trim().Length == 0 ||
                zelle.Trim().All(c => c is '-' or ':')) &&
            z.Any(zelle => zelle.Contains('-')));
        int spalten = tab.Zeilen.Count == 0 ? 0 : tab.Zeilen.Max(z => z.Count);
        foreach (var zeile in tab.Zeilen)
        {
            while (zeile.Count < spalten) zeile.Add("");
        }
        if (tab.Zeilen.Count == 0)
            tab.Zeilen.Add(new List<string> { "", "" });
    }

    /// <summary>Vorlagen-Body in Elemente zerlegen: Markdown-Tabellenzeilen
    /// ("| … | … |") werden echte Tabellen-Elemente, alles andere Textfelder —
    /// untereinander auf der Fläche gestapelt.</summary>
    public static List<NoteElement> ElementeAusVorlage(string body)
    {
        var elemente = new List<NoteElement>();
        double y = 8;
        var text = new StringBuilder();
        TabelleElement? tabelle = null;

        void SchliesseText()
        {
            var t = text.ToString().Trim('\n');
            text.Clear();
            if (t.Length == 0) return;
            elemente.Add(new TextElement { X = 0, Y = y, Breite = 620, Text = t });
            y += GeschaetzteTextHoehe(t) + 16;
        }
        void SchliesseTabelle()
        {
            if (tabelle is null) return;
            NormalisiereTabelle(tabelle);
            tabelle.Y = y;
            elemente.Add(tabelle);
            y += tabelle.Zeilen.Count * 30 + 24;
            tabelle = null;
        }

        foreach (var zeile in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (zeile.TrimStart().StartsWith('|'))
            {
                if (tabelle is null)
                {
                    SchliesseText();
                    tabelle = new TabelleElement { X = 0, Breite = 620 };
                }
                tabelle.Zeilen.Add(ParseTabellenZeile(zeile));
            }
            else
            {
                SchliesseTabelle();
                text.AppendLine(zeile);
            }
        }
        SchliesseTabelle();
        SchliesseText();
        if (elemente.Count == 0)
            elemente.Add(new TextElement { X = 0, Y = 8, Breite = 620, Text = "" });
        return elemente;
    }

    // ---------- Altes Blockformat lesen und in Elemente migrieren ----------

    /// <summary>
    /// Altformat: Fließtext + ```ink-Fences (eigene ISF je Block). Die Blöcke
    /// werden untereinander auf die Fläche gestapelt; die Striche der alten
    /// ISF-Dateien werden erst beim Tinte-Laden mit dem hier bestimmten
    /// Y-Versatz auf die Gesamtfläche verschoben (Note.AltTinten).
    /// </summary>
    static void ParseAltesBlockFormat(string body, Note note)
    {
        note.AltTinten = new List<AltTinte>();
        var zeilen = body.Split('\n');
        var text = new StringBuilder();
        double y = 8;

        void SchliesseText()
        {
            var t = text.ToString().Trim('\n');
            text.Clear();
            if (t.Length == 0) return;
            note.Elemente.Add(new TextElement { X = 0, Y = y, Breite = 620, Text = t });
            y += GeschaetzteTextHoehe(t) + 16;
        }

        for (int i = 0; i < zeilen.Length; i++)
        {
            if (zeilen[i].TrimEnd() == "```ink")
            {
                SchliesseText();
                string datei = "", bild = "";
                string? muster = null;
                double hoehe = 320;
                var erkannt = new StringBuilder();
                bool inText = false;
                i++;
                for (; i < zeilen.Length && zeilen[i].TrimEnd() != "```"; i++)
                {
                    var z = zeilen[i];
                    if (!inText && z.StartsWith("datei:"))
                        datei = z["datei:".Length..].Trim();
                    else if (!inText && z.StartsWith("bild:"))
                        bild = z["bild:".Length..].Trim();
                    else if (!inText && z.StartsWith("muster:"))
                        muster = z["muster:".Length..].Trim() is { Length: > 0 } m ? m : null;
                    else if (!inText && z.StartsWith("hoehe:") &&
                             double.TryParse(z["hoehe:".Length..].Trim(), CultureInfo.InvariantCulture, out var h))
                        hoehe = h;
                    else if (!inText && z.StartsWith("text:"))
                    {
                        inText = true;
                        var rest = z["text:".Length..].TrimStart();
                        if (rest.Length > 0) erkannt.AppendLine(rest);
                    }
                    else if (inText)
                        erkannt.AppendLine(z);
                }

                if (bild.Length > 0)
                    note.Elemente.Add(new BildElement { X = 0, Y = y, Breite = 620, Datei = bild, Hoehe = hoehe });
                note.Muster ??= muster;
                if (datei.Length > 0)
                    note.AltTinten.Add(new AltTinte(datei, 0, y));
                var e = erkannt.ToString().TrimEnd('\n');
                if (e.Length > 0)
                    note.TintenText = note.TintenText.Length == 0 ? e : note.TintenText + "\n" + e;
                y += hoehe + 16;
            }
            else
            {
                text.AppendLine(zeilen[i]);
            }
        }
        SchliesseText();
        note.FlaecheHoehe = Math.Max(900, y + 400);
    }

    /// <summary>Grobe Höhe eines Textblocks im Editor (14pt, umbrochen bei ~620px).</summary>
    static double GeschaetzteTextHoehe(string text)
    {
        int visuelleZeilen = 0;
        foreach (var z in text.Split('\n'))
            visuelleZeilen += Math.Max(1, (int)Math.Ceiling(z.Length / 80.0));
        return Math.Max(28, visuelleZeilen * 22 + 8);
    }

    // ---------- Schreiben: Body ----------

    public static string SchreibeBody(Note note)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.Append("```tinte\n");
        if (note.TintenDatei.Length > 0)
            sb.Append($"datei: {note.TintenDatei}\n");
        sb.Append($"hoehe: {((int)note.FlaecheHoehe).ToString(inv)}\n");
        if (!string.IsNullOrWhiteSpace(note.Muster))
            sb.Append($"muster: {note.Muster}\n");
        if (!string.IsNullOrWhiteSpace(note.TintenText))
            sb.Append($"text: {note.TintenText.Replace("\r\n", "\n").TrimEnd('\n')}\n");
        sb.Append("```\n\n");

        foreach (var el in note.Elemente.OrderBy(e => e.Y).ThenBy(e => e.X))
        {
            string pos = $"x={(int)el.X} y={(int)el.Y} b={(int)el.Breite}";
            switch (el)
            {
                case TextElement t:
                    sb.Append($"<!--el text {pos}");
                    if (!string.IsNullOrWhiteSpace(t.Farbe))
                        sb.Append($" farbe={t.Farbe}");
                    sb.Append("-->\n");
                    sb.Append(t.Text.Replace("\r\n", "\n").TrimEnd('\n'));
                    sb.Append("\n\n");
                    break;
                case BildElement b:
                    sb.Append($"<!--el bild {pos} h={(int)b.Hoehe} datei=\"{b.Datei}\"-->\n\n");
                    break;
                case DateiElement d:
                    sb.Append($"<!--el datei {pos} h={(int)d.Hoehe} datei=\"{d.Datei}\"");
                    if (d.Seite > 0) sb.Append($" seite={d.Seite}");
                    sb.Append("-->\n\n");
                    break;
                case LinkElement l:
                    // Anführungszeichen würden den Attribut-Parser brechen → ersetzen
                    var titel = Bereinige(l.Titel).Replace('"', '\'');
                    if (titel.Length > 200) titel = titel[..200];
                    var url = Bereinige(l.Url).Replace('"', '\'');
                    sb.Append($"<!--el link {pos} h={(int)l.Hoehe} url=\"{url}\" titel=\"{titel}\"");
                    if (!string.IsNullOrWhiteSpace(l.VorschauDatei))
                        sb.Append($" vorschau=\"{l.VorschauDatei}\"");
                    if (l.VorschauScroll > 0.001)
                        sb.Append($" vscroll={l.VorschauScroll.ToString("0.###", inv)}");
                    if (l.VorschauEingepasst)
                        sb.Append(" vmodus=\"einpassen\"");
                    sb.Append("-->\n\n");
                    break;
                case TabelleElement tab when tab.Zeilen.Count > 0:
                    sb.Append($"<!--el tabelle {pos}");
                    if (tab.SpaltenBreiten.Count > 0)
                        sb.Append($" sb=\"{string.Join(',', tab.SpaltenBreiten.Select(x => (int)x))}\"");
                    if (tab.ZeilenHoehen.Any(h => h > 28))
                        sb.Append($" zh=\"{string.Join(',', tab.ZeilenHoehen.Select(x => (int)x))}\"");
                    sb.Append("-->\n");
                    for (int z = 0; z < tab.Zeilen.Count; z++)
                    {
                        sb.Append("| " + string.Join(" | ", tab.Zeilen[z].Select(MdZelle)) + " |\n");
                        if (z == 0) // Trennzeile → lesbares GitHub-Markdown
                            sb.Append("|" + string.Concat(
                                Enumerable.Repeat(" --- |", tab.Zeilen[z].Count)) + "\n");
                    }
                    sb.Append('\n');
                    break;
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Kompletten Dateiinhalt erzeugen.</summary>
    public static string Schreibe(Note note) =>
        SchreibeKopf(note.Meta) + "\n" + SchreibeBody(note);
}
