using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NotizApp.Services;

/// <summary>
/// Das Farbsystem der App: „Kupfer &amp; Wasser" — Petrol als Akzent (Wasser),
/// Kupfer als warmer Zweitton (Rohr/Wärme), davor ruhige Glas-Flächen.
/// Alle Oberflächen referenzieren diese semantischen Brushes per
/// DynamicResource; Anwenden() setzt sie passend zum Windows-Design
/// (hell/dunkel) und kann bei Theme-Wechsel erneut aufgerufen werden.
/// Hintergrund: Die SystemColors-Schlüssel wechseln unter dem Fluent-Theme
/// nicht zuverlässig mit — das führte zu schwarzer Schrift auf dunklem Grund.
/// </summary>
public static class Farbschema
{
    /// <summary>Windows-App-Design (nicht das System-Design der Taskleiste).</summary>
    public static bool IstDunkel()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Anwenden(ResourceDictionary ziel)
    {
        bool dunkel = IstDunkel();

        // Text
        Brush(ziel, "AppTextBrush", dunkel ? "#E6EFF1" : "#1A272C");
        Brush(ziel, "AppTextLeiseBrush", dunkel ? "#9BADB2" : "#5E7278");

        // Flächen (leicht transluzent — der Licht-Hintergrund schimmert durch)
        Brush(ziel, "AppFlaecheBrush", dunkel ? "#14FFFFFF" : "#E6FFFFFF");
        Brush(ziel, "AppFlaecheTiefBrush", dunkel ? "#1FFFFFFF" : "#FFFFFFFF");
        Brush(ziel, "AppRandBrush", dunkel ? "#2BFFFFFF" : "#26243F47");

        // Akzente: Petrol (Wasser) + Kupfer (Wärme)
        Brush(ziel, "AppAkzentBrush", dunkel ? "#3FB9D3" : "#0E7490");
        Brush(ziel, "AppAkzentLeiseBrush", dunkel ? "#223FB9D3" : "#170E7490");
        Brush(ziel, "AppKupferBrush", dunkel ? "#DE9159" : "#C0703C");

        // Treibende Licht-Schimmer im Fenster-Hintergrund (Ambient-Ebene)
        Glow(ziel, "AppGlowWasserBrush", dunkel ? "#2FA9C4" : "#0E7490", dunkel ? 0.16 : 0.11);
        Glow(ziel, "AppGlowKupferBrush", dunkel ? "#D98A54" : "#C67A44", dunkel ? 0.13 : 0.09);
        Glow(ziel, "AppGlowMintBrush", dunkel ? "#48C7C0" : "#2A93A6", dunkel ? 0.11 : 0.07);

        // Firmen-Artwork hinter allem: im Dunkeln verschmilzt das Navy mit dem
        // Fond (etwas präsenter ok), im Hellen nur ein Hauch, sonst wirkt es trüb
        ziel["AppFondBildOpazitaet"] = dunkel ? 0.12 : 0.05;

        // Mystischer, tiefdunkler Grund (opak): im Dunkeln fast schwarz mit ruhigem
        // Diagonal-Verlauf für Tiefe; im Hellen ein sehr helles, ruhiges Grau.
        GrundVerlauf(ziel, dunkel);
    }

    /// <summary>Vollflächiger Hintergrund-Verlauf hinter der Partikel-Ebene.</summary>
    static void GrundVerlauf(ResourceDictionary ziel, bool dunkel)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0),
            EndPoint = new Point(0.85, 1),
        };
        if (dunkel)
        {
            b.GradientStops.Add(new GradientStop(Farbe("#0E1519"), 0));
            b.GradientStops.Add(new GradientStop(Farbe("#080B0D"), 0.55));
            b.GradientStops.Add(new GradientStop(Farbe("#040607"), 1));
        }
        else
        {
            b.GradientStops.Add(new GradientStop(Farbe("#F7FAFB"), 0));
            b.GradientStops.Add(new GradientStop(Farbe("#ECF1F3"), 1));
        }
        b.Freeze();
        ziel["AppGrundBrush"] = b;
    }

    static Color Farbe(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static void Brush(ResourceDictionary ziel, string key, string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        ziel[key] = b;
    }

    /// <summary>Weicher radialer Schimmer für die Ambient-Ebene. Mehrere Stops mit
    /// gauß-ähnlicher Alpha-Abnahme — glatter Verlauf ohne sichtbare Ringe (Banding).</summary>
    static void Glow(ResourceDictionary ziel, string key, string hex, double staerke)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new RadialGradientBrush();
        (double pos, double a)[] stops =
        {
            (0.0, 1.0), (0.25, 0.72), (0.45, 0.46), (0.65, 0.24), (0.82, 0.09), (1.0, 0.0),
        };
        foreach (var (pos, a) in stops)
            b.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(staerke * a * 255), c.R, c.G, c.B), pos));
        b.Freeze();
        ziel[key] = b;
    }
}
