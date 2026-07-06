using System.IO;
using System.Windows.Ink;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>
/// Lädt und speichert Notizen als Markdown + ISF-Sidecars im Datenordner.
/// Ein Ordner-Level unterhalb des Datenordners = Notizbücher.
/// </summary>
public class NoteStore
{
    public string DataFolder { get; }
    public List<Note> Notizen { get; } = new();

    public NoteStore(string dataFolder) => DataFolder = dataFolder;

    // ---------- Initialisierung ----------

    static readonly string[] StandardNotizbuecher =
        { "Eingang", "Kunden-Anrufe", "Meetings", "Nachschlagewerk" };

    /// <summary>Legt Standard-Notizbücher und beim allerersten Mal eine Willkommensnotiz an.</summary>
    public void Initialisieren()
    {
        bool neu = !Directory.Exists(DataFolder) ||
                   !Directory.EnumerateDirectories(DataFolder).Any();
        foreach (var nb in StandardNotizbuecher)
            Directory.CreateDirectory(Path.Combine(DataFolder, nb));

        if (neu)
        {
            var willkommen = new Note
            {
                Notizbuch = "Eingang",
                Meta = new NoteMeta
                {
                    Titel = "Willkommen in der NotizApp",
                    Typ = "leer",
                    Tags = { "hilfe" },
                },
                Bloecke =
                {
                    new TextBlockContent
                    {
                        Text = """
                        # Willkommen! 👋

                        Kurzüberblick:

                        - **Notizbücher** sind Ordner in der Seitenleiste, **#Tags** vergibst du oben in der Notiz.
                        - **Strg+Alt+N** öffnet von überall die Schnellerfassung (z.B. für Kundenanrufe).
                        - **+ Tintenfläche** fügt eine Fläche für den Stift ein. Mit aktivem **„Handschrift → Text"** wird Geschriebenes automatisch zu Tipptext.
                        - Aufgaben: `- [ ] Rückruf @2026-07-08` — erscheinen in der Ansicht **Aufgaben**.
                        - Alles liegt als Markdown-Datei in deinem Datenordner — nichts ist versteckt.

                        - [ ] Diese Notiz gelesen
                        """
                    }
                }
            };
            willkommen.Pfad = NeuerPfad("Eingang", "leer");
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

    public IEnumerable<string> Notizbuecher()
    {
        if (!Directory.Exists(DataFolder)) yield break;
        foreach (var d in Directory.EnumerateDirectories(DataFolder).OrderBy(x => x))
            yield return Path.GetFileName(d);
    }

    /// <summary>Alle .md-Dateien parsen (Ink lazy). In-Memory-Index für Suche und Aufgaben.</summary>
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
                        Bloecke = Frontmatter.ParseBody(body),
                    };
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

    /// <summary>ISF-Sidecars einer Notiz laden (lazy, beim Öffnen im Editor).</summary>
    public void LadeTinte(Note note)
    {
        var ordner = Path.GetDirectoryName(note.Pfad)!;
        foreach (var ink in note.Bloecke.OfType<InkBlockContent>())
        {
            if (ink.Strokes is not null) continue;
            var pfad = Path.Combine(ordner, ink.Datei);
            try
            {
                if (ink.Datei.Length > 0 && File.Exists(pfad))
                {
                    using var fs = File.OpenRead(pfad);
                    ink.Strokes = new StrokeCollection(fs);
                }
                else
                {
                    ink.Strokes = new StrokeCollection();
                }
            }
            catch
            {
                ink.Strokes = new StrokeCollection();
            }
        }
    }

    // ---------- Speichern ----------

    /// <summary>
    /// Schreibt .md + alle ISF-Sidecars, räumt verwaiste .t*.isf auf und
    /// aktualisiert Volltext + Geaendert.
    /// </summary>
    public void Speichere(Note note)
    {
        note.Meta.Geaendert = DateTime.Now;
        var ordner = Path.GetDirectoryName(note.Pfad)!;
        Directory.CreateDirectory(ordner);

        // Ink-Dateinamen vergeben/normalisieren: <mdname>.t<n>.isf
        var mdName = Path.GetFileNameWithoutExtension(note.Pfad);
        int t = 1;
        foreach (var ink in note.Bloecke.OfType<InkBlockContent>())
            ink.Datei = $"{mdName}.t{t++}.isf";

        File.WriteAllText(note.Pfad, Frontmatter.Schreibe(note.Meta, note.Bloecke));

        var gueltig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ink in note.Bloecke.OfType<InkBlockContent>())
        {
            gueltig.Add(ink.Datei);
            if (ink.Strokes is null) continue; // nie geladen → Datei unverändert lassen
            var pfad = Path.Combine(ordner, ink.Datei);
            using var fs = File.Create(pfad);
            ink.Strokes.Save(fs);
        }

        // Verwaiste Sidecars dieser Notiz löschen
        foreach (var isf in Directory.EnumerateFiles(ordner, $"{mdName}.t*.isf"))
        {
            if (!gueltig.Contains(Path.GetFileName(isf)))
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
            Bloecke = { new TextBlockContent { Text = v.Body } },
        };
        Speichere(note);
        return note;
    }

    public void Loesche(Note note)
    {
        var ordner = Path.GetDirectoryName(note.Pfad)!;
        var mdName = Path.GetFileNameWithoutExtension(note.Pfad);
        try { File.Delete(note.Pfad); } catch { }
        foreach (var isf in Directory.EnumerateFiles(ordner, $"{mdName}.t*.isf"))
        {
            try { File.Delete(isf); } catch { }
        }
        Notizen.Remove(note);
    }

    public void Verschiebe(Note note, string zielNotizbuch)
    {
        if (note.Notizbuch == zielNotizbuch) return;
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
        foreach (var ink in note.Bloecke.OfType<InkBlockContent>())
        {
            if (ink.Datei.Length == 0) continue;
            var altIsf = Path.Combine(altOrdner, ink.Datei);
            var neuName = ink.Datei.Replace(mdName, zielBasis);
            if (File.Exists(altIsf))
                File.Move(altIsf, Path.Combine(zielOrdner, neuName));
            ink.Datei = neuName;
        }
        note.Pfad = zielMd;
        note.Notizbuch = zielNotizbuch;
        // Frontmatter/Body mit neuen ISF-Namen neu schreiben
        File.WriteAllText(note.Pfad, Frontmatter.Schreibe(note.Meta, note.Bloecke));
        note.MeldeAnzeigeGeaendert();
    }

    public string NeuesNotizbuch(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Trim();
        if (name.Length == 0) name = "Neues Notizbuch";
        Directory.CreateDirectory(Path.Combine(DataFolder, name));
        return name;
    }

    /// <summary>Alle in Notizen verwendeten Tags mit Häufigkeit.</summary>
    public IEnumerable<(string Tag, int Anzahl)> AlleTags() =>
        Notizen.SelectMany(n => n.Meta.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
}
