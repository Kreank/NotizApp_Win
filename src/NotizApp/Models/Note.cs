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

/// <summary>Ein Inhaltsblock der Notiz (Text oder Tinte).</summary>
public abstract class NoteBlock { }

public class TextBlockContent : NoteBlock
{
    public string Text { get; set; } = "";
}

public class InkBlockContent : NoteBlock
{
    /// <summary>Dateiname des ISF-Sidecars, z.B. "20260706-094100-anruf.t1.isf".</summary>
    public string Datei { get; set; } = "";
    /// <summary>Optionales Hintergrundbild (Dateiname neben der .md) — zum Draufzeichnen.</summary>
    public string? Bild { get; set; }
    public string ErkannterText { get; set; } = "";
    public double Hoehe { get; set; } = 320;
    /// <summary>Lazy geladen; null solange die ISF-Datei noch nicht gelesen wurde.</summary>
    public StrokeCollection? Strokes { get; set; }
}

/// <summary>
/// Eine Notiz = eine Markdown-Datei mit Frontmatter + ISF-Sidecars.
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
    public List<NoteBlock> Bloecke { get; set; } = new();

    /// <summary>Kompletter durchsuchbarer Text (Titel, Tags, Kunde, Text- und erkannte Ink-Blöcke).</summary>
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

    public string Vorschau
    {
        get
        {
            foreach (var b in Bloecke)
            {
                string? s = b switch
                {
                    TextBlockContent t => t.Text,
                    InkBlockContent i => string.IsNullOrWhiteSpace(i.ErkannterText) ? "✍ (Handschrift)" : "✍ " + i.ErkannterText,
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var zeile = s.Trim().Split('\n')[0].Trim();
                    return zeile.Length > 120 ? zeile[..120] + "…" : zeile;
                }
            }
            return "";
        }
    }

    /// <summary>Nach Änderungen aufrufen, damit die Listen-Anzeige aktualisiert wird.</summary>
    public void MeldeAnzeigeGeaendert()
    {
        OnChanged(nameof(AnzeigeTitel));
        OnChanged(nameof(AnzeigeUntertitel));
        OnChanged(nameof(Vorschau));
    }

    /// <summary>Baut den Volltext-Cache für die Suche neu auf.</summary>
    public void BaueVolltext()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Meta.Titel);
        sb.AppendLine(string.Join(' ', Meta.Tags));
        if (!Meta.Kunde.IstLeer)
            sb.AppendLine($"{Meta.Kunde.Name} {Meta.Kunde.Telefon} {Meta.Kunde.Adresse}");
        foreach (var b in Bloecke)
        {
            switch (b)
            {
                case TextBlockContent t: sb.AppendLine(t.Text); break;
                case InkBlockContent i: sb.AppendLine(i.ErkannterText); break;
            }
        }
        VolltextCache = sb.ToString().ToLowerInvariant();
    }
}
