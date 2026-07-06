using System.Globalization;
using System.Text.RegularExpressions;
using NotizApp.Models;

namespace NotizApp.Services;

/// <summary>Eine Checkbox-Zeile aus einer Notiz, für die zentrale Aufgabenansicht.</summary>
public class TaskItem
{
    public required Note Note { get; init; }
    public required int ElementIndex { get; init; }
    public required int ZeilenIndex { get; init; }
    public required string Text { get; init; }
    public required bool Erledigt { get; init; }
    public DateOnly? Faellig { get; init; }

    public bool Ueberfaellig =>
        !Erledigt && Faellig is { } f && f < DateOnly.FromDateTime(DateTime.Today);

    public string AnzeigeText => Text.Length == 0 ? "(leere Aufgabe)" : Text;

    public string AnzeigeQuelle
    {
        get
        {
            var s = Note.AnzeigeTitel;
            if (Faellig is { } f) s += $" · fällig {f:dd.MM.yyyy}";
            return s;
        }
    }
}

/// <summary>
/// Findet Markdown-Checkboxen (`- [ ]` / `- [x]`) in den Textelementen aller Notizen
/// und schaltet sie um (Zeile umschreiben + Notiz speichern).
/// </summary>
public static partial class TaskService
{
    [GeneratedRegex(@"^(\s*[-*]\s*\[)( |x|X)(\]\s*)(.*)$")]
    private static partial Regex CheckboxRegex();

    [GeneratedRegex(@"@(\d{4}-\d{2}-\d{2})")]
    private static partial Regex FaelligRegex();

    public static List<TaskItem> Sammle(IEnumerable<Note> notizen)
    {
        var items = new List<TaskItem>();
        foreach (var note in notizen)
        {
            for (int b = 0; b < note.Elemente.Count; b++)
            {
                if (note.Elemente[b] is not TextElement text) continue;
                var zeilen = text.Text.Replace("\r\n", "\n").Split('\n');
                for (int z = 0; z < zeilen.Length; z++)
                {
                    var m = CheckboxRegex().Match(zeilen[z]);
                    if (!m.Success) continue;

                    var inhalt = m.Groups[4].Value.Trim();
                    DateOnly? faellig = null;
                    var fm = FaelligRegex().Match(inhalt);
                    if (fm.Success && DateOnly.TryParseExact(fm.Groups[1].Value,
                            "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var d))
                    {
                        faellig = d;
                        inhalt = FaelligRegex().Replace(inhalt, "").Trim();
                    }

                    items.Add(new TaskItem
                    {
                        Note = note,
                        ElementIndex = b,
                        ZeilenIndex = z,
                        Text = inhalt,
                        Erledigt = m.Groups[2].Value is "x" or "X",
                        Faellig = faellig,
                    });
                }
            }
        }
        return items
            .OrderBy(t => t.Erledigt)
            .ThenBy(t => t.Faellig ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.Note.Meta.Geaendert)
            .ToList();
    }

    /// <summary>Checkbox in der Quell-Notiz umschalten und Notiz speichern.</summary>
    public static void Umschalten(TaskItem item, NoteStore store)
    {
        if (item.Note.Elemente.ElementAtOrDefault(item.ElementIndex) is not TextElement text)
            return;
        var zeilen = text.Text.Replace("\r\n", "\n").Split('\n');
        if (item.ZeilenIndex >= zeilen.Length) return;

        var m = CheckboxRegex().Match(zeilen[item.ZeilenIndex]);
        if (!m.Success) return;

        var neu = m.Groups[2].Value == " " ? "x" : " ";
        zeilen[item.ZeilenIndex] =
            m.Groups[1].Value + neu + m.Groups[3].Value + m.Groups[4].Value;
        text.Text = string.Join('\n', zeilen);
        store.Speichere(item.Note);
    }
}
