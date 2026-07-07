using System.Windows.Media;

namespace NotizApp.Services;

/// <summary>
/// App-weite Zuordnung Notizbuch → Farbe (aus den Settings gespeist).
/// Statisch, damit die Notizliste (Note.NotizbuchFarbBrush) ohne
/// Settings-Durchreichen darauf zugreifen kann.
/// </summary>
public static class NotizbuchFarben
{
    static Dictionary<string, string> _farben = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Brush> _brushCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Setze(Dictionary<string, string> farben)
    {
        _farben = new Dictionary<string, string>(farben, StringComparer.OrdinalIgnoreCase);
        _brushCache.Clear();
    }

    public static string? Hex(string notizbuch) =>
        _farben.TryGetValue(notizbuch, out var f) ? f : null;

    public static Brush? BrushFuer(string notizbuch)
    {
        if (Hex(notizbuch) is not { } hex) return null;
        if (_brushCache.TryGetValue(hex, out var brush)) return brush;
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            _brushCache[hex] = b;
            return b;
        }
        catch
        {
            return null;
        }
    }
}
