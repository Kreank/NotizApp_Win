using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotizApp.Services;

/// <summary>
/// Zentrale, dezente UI-Bewegungen — passend zum „Kupfer &amp; Wasser"-Design.
/// Alles ist GPU-günstig (nur Opazität/Transform) und respektiert die
/// Windows-Einstellung „Animationen anzeigen": ist sie aus, passiert nichts
/// (die Elemente werden nur in ihren Zielzustand gesetzt).
/// </summary>
public static class Animationen
{
    /// <summary>Sind Fenster-Animationen laut Windows erwünscht?</summary>
    public static bool An => SystemParameters.ClientAreaAnimation;

    static readonly IEasingFunction Weich =
        new SineEase { EasingMode = EasingMode.EaseInOut };
    static readonly IEasingFunction WeichRaus =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Sanft einblenden (Opazität von→1). Ohne Animationen sofort sichtbar.</summary>
    public static void Einblenden(UIElement el, double ms = 200, double von = 0)
    {
        if (!An) { el.Opacity = 1; return; }
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = von;
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(von, 1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = WeichRaus,
            });
    }

    /// <summary>Sanft ausblenden (Opazität→0), danach Rückruf (z.B. zum Ausblenden/Collapse).</summary>
    public static void Ausblenden(UIElement el, double ms = 300, Action? fertig = null)
    {
        if (!An) { el.Opacity = 0; fertig?.Invoke(); return; }
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = WeichRaus };
        anim.Completed += (_, _) => fertig?.Invoke();
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Sanft einblenden und dabei ein paar Pixel nach oben gleiten
    /// (für frisch erscheinende Inhalte wie den geöffneten Editor).</summary>
    public static void EinblendenGleiten(FrameworkElement el, double ms = 240, double versatz = 10)
    {
        Einblenden(el, ms);
        if (!An) return;
        var t = HoleTranslate(el);
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(versatz, 0, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = WeichRaus,
            });
    }

    /// <summary>Endlos-Schweben (leichte Auf-/Ab-Bewegung) für Illustrationen.</summary>
    public static void Schweben(FrameworkElement el, double amplitude = 6, double sekunden = 4.5)
    {
        if (!An) return;
        var t = HoleTranslate(el);
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-amplitude / 2, amplitude / 2, TimeSpan.FromSeconds(sekunden))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Weich,
            });
    }

    /// <summary>Dezentes Dauer-Pulsieren (leichtes Größer/Kleiner) — z.B. für die Chat-Bubble.</summary>
    public static void Pulsieren(FrameworkElement el, double von = 1, double bis = 1.055,
        double sekunden = 2.0)
    {
        if (!An) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = HoleScale(el);
        var anim = new DoubleAnimation(von, bis, TimeSpan.FromSeconds(sekunden))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Weich,
        };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Kurzer Skalier-Impuls zum aktuellen Zustand (z.B. Hover an/aus).</summary>
    public static void Skaliere(FrameworkElement el, double ziel, double ms = 130)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = HoleScale(el);
        if (!An) { s.ScaleX = s.ScaleY = ziel; return; }
        var anim = new DoubleAnimation(ziel, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = WeichRaus,
        };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Spaltenbreite weich animieren (Ein-/Ausklappen). Nach dem Lauf
    /// wird der Zielwert als Basiswert gesetzt, damit der Splitter wieder greift.</summary>
    public static void SpaltenBreite(System.Windows.Controls.ColumnDefinition spalte,
        double von, double bis, double ms = 220, Action? fertig = null)
    {
        if (!An)
        {
            spalte.Width = new GridLength(bis);
            fertig?.Invoke();
            return;
        }
        var anim = new GridLengthAnimation
        {
            From = new GridLength(von),
            To = new GridLength(bis),
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = WeichRaus,
        };
        anim.Completed += (_, _) =>
        {
            spalte.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
            spalte.Width = new GridLength(bis);
            fertig?.Invoke();
        };
        spalte.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, anim);
    }

    static TranslateTransform HoleTranslate(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup g)
        {
            foreach (var tr in g.Children)
                if (tr is TranslateTransform tt) return tt;
            var neu = new TranslateTransform();
            g.Children.Add(neu);
            return neu;
        }
        var solo = new TranslateTransform();
        el.RenderTransform = solo;
        return solo;
    }

    static ScaleTransform HoleScale(FrameworkElement el)
    {
        if (el.RenderTransform is ScaleTransform s) return s;
        if (el.RenderTransform is TransformGroup g)
        {
            foreach (var tr in g.Children)
                if (tr is ScaleTransform ss) return ss;
            var neu = new ScaleTransform(1, 1);
            g.Children.Add(neu);
            return neu;
        }
        // Vorhandene (Translate-)Transform erhalten: in eine Gruppe packen
        if (el.RenderTransform is Transform vorhanden and not MatrixTransform)
        {
            var gruppe = new TransformGroup();
            gruppe.Children.Add(vorhanden);
            var neu = new ScaleTransform(1, 1);
            gruppe.Children.Add(neu);
            el.RenderTransform = gruppe;
            return neu;
        }
        var solo = new ScaleTransform(1, 1);
        el.RenderTransform = solo;
        return solo;
    }
}

/// <summary>Animiert eine <see cref="GridLength"/> in Pixeln (WPF kann das nicht ab Werk).</summary>
public class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public GridLength From { get; set; }
    public GridLength To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }

    public override object GetCurrentValue(object defaultOriginValue,
        object defaultDestinationValue, AnimationClock clock)
    {
        double von = From.Value, bis = To.Value;
        double p = clock.CurrentProgress ?? 0;
        if (EasingFunction is not null) p = EasingFunction.Ease(p);
        return new GridLength(von + (bis - von) * p, GridUnitType.Pixel);
    }
}
