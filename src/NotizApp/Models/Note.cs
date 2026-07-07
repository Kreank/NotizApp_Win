using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Ink;

namespace NotizApp.Models;

/// <summary>Kundendaten im Frontmatter-Kopf — bleiben lokal, gehen nie an die KI.</summary>
public class KundeInfo
{
    public string? Name { get; set; }
    public string? Telefon { get; set; }
    public string? Adresse { get; set; }

    public bool IstLeer =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Telefon) &&
        string.IsNullOrWhiteSpace(Adresse);
}

/// <summary>Frontmatter-Metadaten einer Notiz.</summary>
public class NoteMeta
{
    public string Titel { get; set; } = "";
    public string Typ { get; set; } = "leer";
    public DateTime Erstellt { get; set; } = DateTime.Now;
    public DateTime Geaendert { get; set; } = DateTime.Now;
    public List<string> Tags { get; set; } = new();
    public KundeInfo Kunde { get; set; } = new();
    public string? Dringlichkeit { get; set; }
}

/// <summary>
/// Ein frei auf der Notiz-Fläche platziertes Element (Freiform-Canvas).
/// Position/Breite in Canvas-Koordinaten (Pixel).
/// </summary>
public abstract class NoteElement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Breite { get; set; } = 620;
}

/// <summary>Textfeld auf der Fläche. Höhe ergibt sich aus dem Inhalt.</summary>
public class TextElement : NoteElement
{
    public string Text { get; set; } = "";
    /// <summary>Hex-Farbe "#RRGGBB", null = Standardfarbe des Designs (hell/dunkel).</summary>
    public string? Farbe { get; set; }
    /// <summary>Schriftart-Name (z.B. "Consolas"), null = Standardschrift.</summary>
    public string? Schrift { get; set; }
}

/// <summary>Bild auf der Fläche (Dateiname neben der .md) — Tinte kann darüber liegen.</summary>
public class BildElement : NoteElement
{
    public string Datei { get; set; } = "";
    public double Hoehe { get; set; } = 240;
}

/// <summary>Abgelegte Datei (xlsx/docx/md/txt/pdf …) als Objekt-Karte auf der Fläche.</summary>
public class DateiElement : NoteElement
{
    public string Datei { get; set; } = "";
    public double Hoehe { get; set; } = 96;
    /// <summary>Zuletzt angezeigte PDF-Seite (0-basiert), bleibt beim Blättern erhalten.</summary>
    public int Seite { get; set; }
}

/// <summary>Web-Link als Karte auf der Fläche (aus dem Browser gezogen).</summary>
public class LinkElement : NoteElement
{
    public string Url { get; set; } = "";
    public string Titel { get; set; } = "";
    public double Hoehe { get; set; } = 76;
    /// <summary>Screenshot der Seite (Dateiname neben der .md), leer = keine Vorschau.</summary>
    public string VorschauDatei { get; set; } = "";
    /// <summary>Scroll-Position der Vorschau im Füllen-Modus, relativ 0..1.</summary>
    public double VorschauScroll { get; set; }
    /// <summary>true = ganze Seite eingepasst (Letterbox) statt füllendem Ausschnitt.</summary>
    public bool VorschauEingepasst { get; set; }
}

/// <summary>Tabelle auf der Fläche: Zeilen mit Textzellen, erste Zeile ist die
/// Kopfzeile. In der .md-Datei als Markdown-Tabelle gespeichert.</summary>
public class TabelleElement : NoteElement
{
    public List<List<string>> Zeilen { get; set; } = new();
    /// <summary>Spaltenbreiten in px; leer = gleichmäßig aus Breite verteilt.</summary>
    public List<double> SpaltenBreiten { get; set; } = new();
    /// <summary>Mindesthöhen der Zeilen in px; leer = automatisch nach Inhalt.</summary>
    public List<double> ZeilenHoehen { get; set; } = new();
}

/// <summary>Alte Tinten-Sidecar-Datei (Blockformat vor dem Freiform-Canvas) samt
/// Versatz, mit dem ihre Striche auf die Gesamtfläche zu verschieben sind.</summary>
public record AltTinte(string Datei, double OffsetX, double OffsetY);

/// <summary>
/// Eine Notiz = eine Markdown-Datei mit Frontmatter + einer Tinten-ISF daneben.
/// Der Body beschreibt frei platzierte Elemente (Text/Bild/Datei); die Handschrift
/// der ganzen Fläche liegt in EINER .tinte.isf.
/// INPC nur für die Eigenschaften, die die Notizliste anzeigt.
/// </summary>
public class Note : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>Voller Pfad der .md-Datei.</summary>
    public string Pfad { get; set; } = "";
    /// <summary>Name des Notizbuch-Ordners.</summary>
    public string Notizbuch { get; set; } = "";

    public NoteMeta Meta { get; set; } = new();

    /// <summary>Frei platzierte Elemente der Fläche.</summary>
    public List<NoteElement> Elemente { get; set; } = new();

    /// <summary>Dateiname der Tinten-ISF ("&lt;mdname&gt;.tinte.isf"), leer = keine Tinte.</summary>
    public string TintenDatei { get; set; } = "";
    /// <summary>Lazy geladen; null solange die ISF-Datei noch nicht gelesen wurde.</summary>
    public StrokeCollection? Tinte { get; set; }
    /// <summary>Im Hintergrund erkannter Text der Handschrift (für Suche + KI).</summary>
    public string TintenText { get; set; } = "";
    /// <summary>Im Hintergrund extrahierter Text der Anhänge (PDF-Text + Bild-OCR, für die Suche).</summary>
    public string AnhangText { get; set; } = "";

    /// <summary>Gespeicherte Höhe der Fläche (wächst beim Schreiben mit).</summary>
    public double FlaecheHoehe { get; set; } = 900;
    /// <summary>Papier-Muster der Fläche: null (blanko), "linien", "karo", "punkte".</summary>
    public string? Muster { get; set; }

    /// <summary>Nicht null = Notiz liegt noch im alten Blockformat; die alten
    /// .t*.isf-Sidecars werden beim ersten Tinte-Laden auf die Fläche migriert.</summary>
    public List<AltTinte>? AltTinten { get; set; }

    /// <summary>Kompletter durchsuchbarer Text (Titel, Tags, Kunde, Textelemente, erkannte Handschrift).</summary>
    public string VolltextCache { get; set; } = "";

    public string Dateiname => Path.GetFileName(Pfad);

    public string AnzeigeTitel =>
        string.IsNullOrWhiteSpace(Meta.Titel) ? "(ohne Titel)" : Meta.Titel;

    public string AnzeigeUntertitel
    {
        get
        {
            var typIcon = Services.Templates.Icon(Meta.Typ);
            var teil = $"{typIcon} {Meta.Geaendert:dd.MM.yyyy HH:mm} · {Notizbuch}";
            if (Meta.Tags.Count > 0)
                teil += " · " + string.Join(" ", Meta.Tags.Select(t => "#" + t));
            return teil;
        }
    }

    /// <summary>Textelemente in Leserichtung (oben nach unten).</summary>
    public IEnumerable<TextElement> TexteInLeserichtung() =>
        Elemente.OfType<TextElement>().OrderBy(t => t.Y).ThenBy(t => t.X);

    public string Vorschau
    {
        get
        {
            foreach (var t in TexteInLeserichtung())
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                var zeile = t.Text.Trim().Split('\n')[0].Trim();
                return zeile.Length > 120 ? zeile[..120] + "…" : zeile;
            }
            if (!string.IsNullOrWhiteSpace(TintenText))
            {
                var zeile = "✍ " + TintenText.Trim().Split('\n')[0].Trim();
                return zeile.Length > 120 ? zeile[..120] + "…" : zeile;
            }
            if (TintenDatei.Length > 0 || AltTinten is { Count: > 0 })
                return "✍ (Handschrift)";
            var datei = Elemente.OfType<DateiElement>().FirstOrDefault()
                ?? (NoteElement?)Elemente.OfType<BildElement>().FirstOrDefault();
            return datei switch
            {
                DateiElement d => "📎 " + d.Datei,
                BildElement b => "🖼 " + b.Datei,
                _ => "",
            };
        }
    }

    /// <summary>Farbbalken der Notizliste (Farbe des Notizbuchs, null = keiner).</summary>
    public System.Windows.Media.Brush? NotizbuchFarbBrush =>
        Services.NotizbuchFarben.BrushFuer(Notizbuch);

    /// <summary>Nach Änderungen aufrufen, damit die Listen-Anzeige aktualisiert wird.</summary>
    public void MeldeAnzeigeGeaendert()
    {
        OnChanged(nameof(AnzeigeTitel));
        OnChanged(nameof(AnzeigeUntertitel));
        OnChanged(nameof(Vorschau));
        OnChanged(nameof(NotizbuchFarbBrush));
    }

    /// <summary>Baut den Volltext-Cache für die Suche neu auf.</summary>
    public void BaueVolltext()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Meta.Titel);
        sb.AppendLine(string.Join(' ', Meta.Tags));
        if (!Meta.Kunde.IstLeer)
            sb.AppendLine($"{Meta.Kunde.Name} {Meta.Kunde.Telefon} {Meta.Kunde.Adresse}");
        foreach (var t in TexteInLeserichtung())
            sb.AppendLine(t.Text);
        foreach (var tab in Elemente.OfType<TabelleElement>())
        {
            foreach (var zeile in tab.Zeilen)
                sb.AppendLine(string.Join(' ', zeile));
        }
        foreach (var d in Elemente.OfType<DateiElement>())
            sb.AppendLine(d.Datei);
        foreach (var l in Elemente.OfType<LinkElement>())
            sb.AppendLine($"{l.Titel} {l.Url}");
        sb.AppendLine(TintenText);
        sb.AppendLine(AnhangText);
        VolltextCache = sb.ToString().ToLowerInvariant();
    }
}
