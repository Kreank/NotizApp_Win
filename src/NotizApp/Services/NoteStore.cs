using System.IO;
using System.Windows.Ink;
using System.Windows.Media;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>
/// Lädt und speichert Notizen als Markdown + Tinten-ISF im Datenordner.
/// Ein Ordner-Level unterhalb des Datenordners = Notizbücher.
/// </summary>
public class NoteStore
{
    public string DataFolder { get; }
    public List<Note> Notizen { get; } = new();

    public NoteStore(string dataFolder) => DataFolder = dataFolder;

    // ---------- Initialisierung ----------

    /// <summary>Der einzige immer vorhandene Standard-Ordner. Fallback für neue Notizen
    /// ohne gewähltes Notizbuch. Andere Ordner legt der Nutzer selbst an — einmal
    /// gelöscht, kommen sie nicht wieder.</summary>
    public const string StandardNotizbuch = "Nachschlagewerk";

    /// <summary>Legt den Standard-Ordner und beim allerersten Mal eine Willkommensnotiz an.</summary>
    public void Initialisieren()
    {
        bool neu = !Directory.Exists(DataFolder) ||
                   !Directory.EnumerateDirectories(DataFolder).Any();
        Directory.CreateDirectory(Path.Combine(DataFolder, StandardNotizbuch));

        if (neu)
        {
            var willkommen = new Note
            {
                Notizbuch = StandardNotizbuch,
                Meta = new NoteMeta
                {
                    Titel = "Willkommen in der NotizApp",
                    Typ = "leer",
                    Tags = { "hilfe" },
                },
                Elemente =
                {
                    new TextElement
                    {
                        X = 0, Y = 8, Breite = 620,
                        Text = """
                        # Willkommen! 👋

                        Kurzüberblick:

                        - **Notizbücher** sind Ordner in der Seitenleiste, **#Tags** vergibst du oben in der Notiz.
                        - **Strg+Alt+N** öffnet von überall die Schnellerfassung (z.B. für Kundenanrufe).
                        - Jede Notiz ist eine **freie Fläche**: Mit dem Stift schreibst du überall, Textfelder setzt du per **Doppelklick**, Bilder und Dateien legst du mit **+ Datei** als verschieb- und skalierbare Objekte ab — Draufzeichnen inklusive.
                        - Mit aktivem **„Handschrift → Text"** wird Geschriebenes automatisch zu Tipptext in der Stiftfarbe. Oder: Striche mit dem **Lasso** markieren und **„Auswahl → Text"** drücken.
                        - Aufgaben: `- [ ] Rückruf @2026-07-08` — erscheinen in der Ansicht **Aufgaben**.
                        - Alles liegt als Markdown-Datei in deinem Datenordner — nichts ist versteckt.

                        - [ ] Diese Notiz gelesen
                        """
                    }
                }
            };
            willkommen.Pfad = NeuerPfad(StandardNotizbuch, "leer");
            Speichere(willkommen);
        }
    }

    /// <summary>Vergibt einen freien Dateipfad für eine neue Notiz.</summary>
    public string NeuerPfad(string notizbuch, string vorlage)
    {
        var ordner = Path.Combine(DataFolder, notizbuch);
        Directory.CreateDirectory(ordner);
        var basis = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var pfad = Path.Combine(ordner, $"{basis}-{vorlage}.md");
        // Kollision (zwei Notizen in derselben Sekunde) → Suffix hochzählen
        int n = 2;
        while (File.Exists(pfad))
            pfad = Path.Combine(ordner, $"{basis}-{vorlage}-{n++}.md");
        return pfad;
    }

    // ---------- Laden ----------

    /// <summary>Alle Notizbücher als relative Pfade ("Kunden", "Kunden/Meier"),
    /// in Baumreihenfolge: Eltern direkt vor ihren Unterordnern.</summary>
    public IEnumerable<string> Notizbuecher()
    {
        if (!Directory.Exists(DataFolder)) return Enumerable.Empty<string>();
        return Directory.EnumerateDirectories(DataFolder, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(DataFolder, d).Replace('\\', '/'))
            // '/' niedriger einsortieren als jedes Namenszeichen, sonst rutschen
            // Unterordner hinter Geschwister wie "Kunden-Anrufe"
            .OrderBy(rel => rel.Replace('/', '\u0001'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Liegt das Notizbuch im Teilbaum unter wurzel (oder ist es die Wurzel selbst)?</summary>
    public static bool ImTeilbaum(string notizbuch, string wurzel) =>
        notizbuch.Equals(wurzel, StringComparison.OrdinalIgnoreCase) ||
        notizbuch.StartsWith(wurzel + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Alle .md-Dateien parsen (Tinte lazy). In-Memory-Index für Suche und Aufgaben.</summary>
    public void LadeAlle()
    {
        Notizen.Clear();
        foreach (var nb in Notizbuecher())
        {
            var ordner = Path.Combine(DataFolder, nb);
            foreach (var md in Directory.EnumerateFiles(ordner, "*.md"))
            {
                try
                {
                    var (meta, body) = Frontmatter.Parse(File.ReadAllText(md));
                    var note = new Note
                    {
                        Pfad = md,
                        Notizbuch = nb,
                        Meta = meta,
                    };
                    Frontmatter.ParseBody(body, note);
                    note.BaueVolltext();
                    Notizen.Add(note);
                }
                catch
                {
                    // defekte Datei überspringen — niemals den App-Start verhindern
                }
            }
        }
        Notizen.Sort((a, b) => b.Meta.Geaendert.CompareTo(a.Meta.Geaendert));
    }

    /// <summary>
    /// Tinte einer Notiz laden (lazy, beim Öffnen im Editor). Notizen im alten
    /// Blockformat: alle alten .t*.isf werden mit ihrem Y-Versatz auf die
    /// Gesamtfläche verschoben und zu EINER StrokeCollection zusammengeführt.
    /// </summary>
    public void LadeTinte(Note note)
    {
        if (note.Tinte is not null) return;
        var ordner = Path.GetDirectoryName(note.Pfad)!;

        if (note.AltTinten is not null)
        {
            var alle = new StrokeCollection();
            foreach (var alt in note.AltTinten)
            {
                var strokes = LadeIsf(Path.Combine(ordner, alt.Datei));
                if (strokes.Count == 0) continue;
                var m = Matrix.Identity;
                m.Translate(alt.OffsetX, alt.OffsetY);
                strokes.Transform(m, applyToStylusTip: false);
                alle.Add(strokes);
            }
            note.Tinte = alle;
            note.AltTinten = null; // migriert — beim nächsten Speichern wird das neue Format geschrieben
            return;
        }

        note.Tinte = note.TintenDatei.Length > 0
            ? LadeIsf(Path.Combine(ordner, note.TintenDatei))
            : new StrokeCollection();
    }

    static StrokeCollection LadeIsf(string pfad)
    {
        try
        {
            if (!File.Exists(pfad)) return new StrokeCollection();
            using var fs = File.OpenRead(pfad);
            return new StrokeCollection(fs);
        }
        catch
        {
            return new StrokeCollection();
        }
    }

    // ---------- Speichern ----------

    /// <summary>
    /// Schreibt .md + Tinten-ISF, räumt alte/verwaiste .t*.isf auf und
    /// aktualisiert Volltext + Geaendert.
    /// </summary>
    public void Speichere(Note note)
    {
        // Altformat erst auf die Fläche migrieren, sonst gingen die
        // Block-Zuordnungen der alten ISF-Dateien verloren
        if (note.AltTinten is not null) LadeTinte(note);

        note.Meta.Geaendert = DateTime.Now;
        var ordner = Path.GetDirectoryName(note.Pfad)!;
        Directory.CreateDirectory(ordner);
        var mdName = Path.GetFileNameWithoutExtension(note.Pfad);

        if (note.Tinte is { Count: > 0 })
        {
            note.TintenDatei = $"{mdName}.tinte.isf";
            using var fs = File.Create(Path.Combine(ordner, note.TintenDatei));
            note.Tinte.Save(fs);
        }
        else if (note.Tinte is not null)
        {
            note.TintenDatei = ""; // Tinte komplett gelöscht → Datei fällt der Aufräumung zum Opfer
        }
        // note.Tinte == null (nie geladen): TintenDatei und ISF unverändert lassen

        File.WriteAllText(note.Pfad, Frontmatter.Schreibe(note));

        // Alte Block-Sidecars (.t1.isf …) und verwaiste Tinten-Dateien löschen
        foreach (var isf in Directory.EnumerateFiles(ordner, $"{mdName}.t*.isf"))
        {
            if (!Path.GetFileName(isf).Equals(note.TintenDatei, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(isf); } catch { }
            }
        }

        note.BaueVolltext();
        note.MeldeAnzeigeGeaendert();
        if (!Notizen.Contains(note)) Notizen.Add(note);
    }

    // ---------- Verwaltung ----------

    public Note Neu(string notizbuch, string vorlageKey)
    {
        var v = Templates.Hole(vorlageKey);
        var note = new Note
        {
            Notizbuch = notizbuch,
            Pfad = NeuerPfad(notizbuch, v.Key),
            Meta = new NoteMeta { Titel = v.TitelVorschlag, Typ = v.Key },
        };
        // Markdown-Tabellen in der Vorlage werden echte Tabellen-Elemente
        note.Elemente.AddRange(Frontmatter.ElementeAusVorlage(v.Body));
        Speichere(note);
        return note;
    }

    /// <summary>Alle Sidecar-Dateien der Notiz (ISF, Bilder, Anhänge): "&lt;mdname&gt;.*" außer der .md selbst.</summary>
    static IEnumerable<string> Sidecars(string ordner, string mdName) =>
        Directory.EnumerateFiles(ordner, $"{mdName}.*")
            .Where(f => !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    public void Loesche(Note note)
    {
        var ordner = Path.GetDirectoryName(note.Pfad)!;
        var mdName = Path.GetFileNameWithoutExtension(note.Pfad);
        try { File.Delete(note.Pfad); } catch { }
        foreach (var sidecar in Sidecars(ordner, mdName).ToList())
        {
            try { File.Delete(sidecar); } catch { }
        }
        Notizen.Remove(note);
    }

    public void Verschiebe(Note note, string zielNotizbuch)
    {
        if (note.Notizbuch == zielNotizbuch) return;
        // Altformat vor dem Verschieben migrieren (schreibt neue .md + .tinte.isf),
        // sonst verlöre der Format-Neuschrieb unten die Block-Zuordnung der Tinte
        if (note.AltTinten is not null) Speichere(note);
        var altOrdner = Path.GetDirectoryName(note.Pfad)!;
        var mdName = Path.GetFileNameWithoutExtension(note.Pfad);
        var zielOrdner = Path.Combine(DataFolder, zielNotizbuch);
        Directory.CreateDirectory(zielOrdner);

        var zielMd = Path.Combine(zielOrdner, Path.GetFileName(note.Pfad));
        int n = 2;
        while (File.Exists(zielMd))
            zielMd = Path.Combine(zielOrdner, $"{mdName}-{n++}.md");

        File.Move(note.Pfad, zielMd);
        var zielBasis = Path.GetFileNameWithoutExtension(zielMd);

        // Alle Sidecars mitnehmen (Präfix ggf. auf neuen Basisnamen umstellen)
        foreach (var sidecar in Sidecars(altOrdner, mdName).ToList())
        {
            var neuName = Path.GetFileName(sidecar)
                .Replace(mdName + ".", zielBasis + ".", StringComparison.Ordinal);
            try { File.Move(sidecar, Path.Combine(zielOrdner, neuName)); } catch { }
        }
        if (zielBasis != mdName)
        {
            // Referenzen in Elementen/Tinte (Dateinamen, Anhang-Links) anpassen
            string Umbenennen(string s) =>
                s.Replace(mdName + ".", zielBasis + ".", StringComparison.Ordinal);
            note.TintenDatei = Umbenennen(note.TintenDatei);
            if (note.AltTinten is not null)
                note.AltTinten = note.AltTinten
                    .Select(a => a with { Datei = Umbenennen(a.Datei) }).ToList();
            foreach (var el in note.Elemente)
            {
                switch (el)
                {
                    case BildElement b: b.Datei = Umbenennen(b.Datei); break;
                    case DateiElement d: d.Datei = Umbenennen(d.Datei); break;
                    case TextElement t: t.Text = Umbenennen(t.Text); break;
                }
            }
        }
        note.Pfad = zielMd;
        note.Notizbuch = zielNotizbuch;
        // Frontmatter/Body mit neuen Dateinamen neu schreiben
        File.WriteAllText(note.Pfad, Frontmatter.Schreibe(note));
        note.MeldeAnzeigeGeaendert();
    }

    /// <summary>Notizbuch anlegen — mit uebergeordnet als Unterordner (z.B. "Kunden" → "Kunden/Meier").</summary>
    public string NeuesNotizbuch(string name, string? uebergeordnet = null)
    {
        name = BereinigeOrdnerName(name);
        var rel = uebergeordnet is null ? name : $"{uebergeordnet}/{name}";
        Directory.CreateDirectory(Path.Combine(DataFolder, rel));
        return rel;
    }

    static string BereinigeOrdnerName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Trim();
        return name.Length == 0 ? "Neues Notizbuch" : name;
    }

    /// <summary>Notizbuch-Ordner umbenennen (nur der letzte Namensteil, der
    /// Ober-Ordner bleibt); passt Pfade der geladenen Notizen im ganzen Teilbaum an.
    /// Liefert den neuen relativen Pfad oder null, wenn das Ziel schon existiert.</summary>
    public string? NotizbuchUmbenennen(string alt, string neuName)
    {
        neuName = BereinigeOrdnerName(neuName);
        var eltern = alt.Contains('/') ? alt[..alt.LastIndexOf('/')] : null;
        var neu = eltern is null ? neuName : $"{eltern}/{neuName}";
        if (neu.Equals(alt, StringComparison.OrdinalIgnoreCase)) return neu;
        var altPfad = Path.Combine(DataFolder, alt);
        var neuPfad = Path.Combine(DataFolder, neu);
        if (!Directory.Exists(altPfad) || Directory.Exists(neuPfad)) return null;

        Directory.Move(altPfad, neuPfad);
        foreach (var note in Notizen.Where(n => ImTeilbaum(n.Notizbuch, alt)))
        {
            note.Notizbuch = neu + note.Notizbuch[alt.Length..];
            note.Pfad = Path.Combine(DataFolder, note.Notizbuch, Path.GetFileName(note.Pfad));
            note.MeldeAnzeigeGeaendert();
        }
        return neu;
    }

    /// <summary>Notizbuch samt aller Notizen, Sidecars und Unterordner löschen.</summary>
    public void NotizbuchLoeschen(string name)
    {
        var pfad = Path.Combine(DataFolder, name);
        try { Directory.Delete(pfad, recursive: true); } catch { }
        Notizen.RemoveAll(n => ImTeilbaum(n.Notizbuch, name));
    }

    /// <summary>Alle in Notizen verwendeten Tags mit Häufigkeit.</summary>
    public IEnumerable<(string Tag, int Anzahl)> AlleTags() =>
        Notizen.SelectMany(n => n.Meta.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
}
