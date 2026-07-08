using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NotizApp.Controls;

/// <summary>
/// Eine frei auf dem Dashboard-Board (Canvas) platzierbare Karte: per oberer
/// Ziehleiste verschiebbar, per Griff unten rechts in der Größe ziehbar.
/// Das Aussehen (Template) kommt als Style aus <c>DashboardView.xaml</c>.
/// Nach dem Verschieben bzw. Größe-Ändern wird <see cref="LayoutGeaendert"/>
/// gefeuert, damit der Host Position und Größe dauerhaft speichern kann.
/// </summary>
[TemplatePart(Name = "PART_Move", Type = typeof(Thumb))]
[TemplatePart(Name = "PART_Resize", Type = typeof(Thumb))]
public class DashCard : ContentControl
{
    /// <summary>Stabile Kennung der Karte (z.B. "kalender", "termine", "news:URL").</summary>
    public string CardId { get; set; } = "";

    /// <summary>Wird bei Drag-Ende (Verschieben) UND Resize-Ende gefeuert.</summary>
    public event Action<DashCard>? LayoutGeaendert;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Move") is Thumb verschieben)
        {
            verschieben.DragDelta += Verschieben_DragDelta;
            verschieben.DragCompleted += (_, _) => LayoutGeaendert?.Invoke(this);
        }
        if (GetTemplateChild("PART_Resize") is Thumb groesse)
        {
            groesse.DragDelta += Groesse_DragDelta;
            groesse.DragCompleted += (_, _) => LayoutGeaendert?.Invoke(this);
        }
    }

    void Verschieben_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Canvas.SetLeft(this, Math.Max(0, HoleLinks() + e.HorizontalChange));
        Canvas.SetTop(this, Math.Max(0, HoleOben() + e.VerticalChange));
    }

    void Groesse_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, (double.IsNaN(Width) ? ActualWidth : Width) + e.HorizontalChange);
        Height = Math.Max(MinHeight, (double.IsNaN(Height) ? ActualHeight : Height) + e.VerticalChange);
    }

    /// <summary>Canvas.Left der Karte; NaN (nicht gesetzt) wird als 0 behandelt.</summary>
    double HoleLinks()
    {
        var v = Canvas.GetLeft(this);
        return double.IsNaN(v) ? 0 : v;
    }

    /// <summary>Canvas.Top der Karte; NaN (nicht gesetzt) wird als 0 behandelt.</summary>
    double HoleOben()
    {
        var v = Canvas.GetTop(this);
        return double.IsNaN(v) ? 0 : v;
    }
}
