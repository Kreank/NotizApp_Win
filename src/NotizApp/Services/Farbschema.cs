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

        // Treibende Licht-Schimmer im Fenster-Hintergrund
        Glow(ziel, "AppGlowWasserBrush", dunkel ? "#2FA9C4" : "#0E7490", dunkel ? 0.16 : 0.11);
        Glow(ziel, "AppGlowKupferBrush", dunkel ? "#D98A54" : "#C67A44", dunkel ? 0.13 : 0.09);
    }

    static void Brush(ResourceDictionary ziel, string key, string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        ziel[key] = b;
    }

    /// <summary>Weicher radialer Schimmer: Akzentfarbe außen in Transparenz derselben Farbe.</summary>
    static void Glow(ResourceDictionary ziel, string key, string hex, double staerke)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var innen = Color.FromArgb((byte)(staerke * 255), c.R, c.G, c.B);
        var aussen = Color.FromArgb(0, c.R, c.G, c.B);
        var b = new RadialGradientBrush(innen, aussen);
        b.Freeze();
        ziel[key] = b;
    }
}
