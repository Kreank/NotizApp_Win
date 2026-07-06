using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotizApp.Models;

namespace NotizApp.Controls;

/// <summary>Basis für die Block-VMs im NoteEditor-ItemsControl.</summary>
public abstract class BlockVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event Action? Geaendert;
    protected void MeldeGeaendert() => Geaendert?.Invoke();
}

public class TextBlockVm : BlockVm
{
    string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnChanged();
            MeldeGeaendert();
        }
    }

    public TextBlockVm() { }
    public TextBlockVm(TextBlockContent c) => _text = c.Text;

    public TextBlockContent ZuModel() => new() { Text = Text };
}

public class InkBlockVm : BlockVm
{
    public const double MinHoehe = 120;
    public const double MaxHoehe = 2000;

    public StrokeCollection Strokes { get; }
    public string Datei { get; set; } = "";
    /// <summary>Dateiname des Hintergrundbilds (relativ zum Notiz-Ordner), null = reine Tinte.</summary>
    public string? Bild { get; set; }
    public string ErkannterText { get; set; } = "";

    Brush _hintergrund = Brushes.Transparent;
    /// <summary>Hintergrund fürs InkCanvas: Bild, Papier-Muster oder Transparent.</summary>
    public Brush Hintergrund
    {
        get => _hintergrund;
        private set { _hintergrund = value; OnChanged(); }
    }

    /// <summary>Papier-Muster: null (blanko), "linien", "karo", "punkte".</summary>
    public string? Muster { get; private set; }

    static readonly string?[] MusterFolge = { null, "linien", "karo", "punkte" };

    public void SetzeMuster(string? muster)
    {
        Muster = muster;
        if (Bild is null) // Bild hat Vorrang vor Muster
            Hintergrund = MusterBrush(muster);
        MeldeGeaendert();
    }

    /// <summary>Muster durchschalten: blanko → liniert → kariert → punkte → blanko.</summary>
    public void NaechstesMuster()
    {
        var i = Array.IndexOf(MusterFolge, Muster);
        SetzeMuster(MusterFolge[(i + 1) % MusterFolge.Length]);
    }

    /// <summary>Kachel-Brush für ein Papier-Muster (dezentes Grau, funktioniert hell wie dunkel).</summary>
    public static Brush MusterBrush(string? muster)
    {
        if (muster is null) return Brushes.Transparent;
        const double kachel = 26;
        var stift = new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1);
        var gruppe = new DrawingGroup();
        // Unsichtbare Füllung, damit die Kachelgröße stimmt
        gruppe.Children.Add(new GeometryDrawing(Brushes.Transparent, null,
            new RectangleGeometry(new Rect(0, 0, kachel, kachel))));
        switch (muster)
        {
            case "linien":
                gruppe.Children.Add(new GeometryDrawing(null, stift,
                    new LineGeometry(new Point(0, kachel), new Point(kachel, kachel))));
                break;
            case "karo":
                gruppe.Children.Add(new GeometryDrawing(null, stift,
                    new LineGeometry(new Point(0, kachel), new Point(kachel, kachel))));
                gruppe.Children.Add(new GeometryDrawing(null, stift,
                    new LineGeometry(new Point(kachel, 0), new Point(kachel, kachel))));
                break;
            case "punkte":
                gruppe.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)), null,
                    new EllipseGeometry(new Point(kachel / 2, kachel / 2), 1.2, 1.2)));
                break;
            default:
                return Brushes.Transparent;
        }
        gruppe.Freeze();
        return new DrawingBrush(gruppe)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, kachel, kachel),
            ViewportUnits = BrushMappingMode.Absolute,
        };
    }

    /// <summary>Hintergrundbild aus Datei laden (ohne Datei-Lock) und Höhe ans Seitenverhältnis anpassen.</summary>
    public void LadeBild(string ordner, double breiteHint = 800)
    {
        if (string.IsNullOrWhiteSpace(Bild)) return;
        try
        {
            var pfad = Path.Combine(ordner, Bild);
            if (!File.Exists(pfad)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // Datei sofort wieder freigeben
            bmp.UriSource = new Uri(pfad);
            bmp.EndInit();
            bmp.Freeze();
            // Immer weißes "Papier" unterlegen: transparente PNGs (Diagramme,
            // Produktbilder mit schwarzer Schrift) wären im Dark Mode sonst unlesbar
            var rect = new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight);
            var gruppe = new DrawingGroup();
            gruppe.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(rect)));
            gruppe.Children.Add(new ImageDrawing(bmp, rect));
            gruppe.Freeze();
            Hintergrund = new DrawingBrush(gruppe) { Stretch = Stretch.Uniform };
            // Beim ersten Einfügen sinnvolle Höhe wählen
            if (Math.Abs(_hoehe - 320) < 0.5 && bmp.PixelWidth > 0)
                _hoehe = Math.Clamp(breiteHint * bmp.PixelHeight / bmp.PixelWidth, MinHoehe, 700);
        }
        catch
        {
            // defektes/fehlendes Bild → Fläche bleibt nutzbar, nur ohne Hintergrund
        }
    }

    double _hoehe = 320;
    public double Hoehe
    {
        get => _hoehe;
        set
        {
            var v = Math.Clamp(value, MinHoehe, MaxHoehe);
            if (Math.Abs(_hoehe - v) < 0.5) return;
            _hoehe = v;
            OnChanged();
            MeldeGeaendert();
        }
    }

    public InkBlockVm() : this(new StrokeCollection()) { }

    public InkBlockVm(StrokeCollection strokes)
    {
        Strokes = strokes;
        Strokes.StrokesChanged += (_, _) =>
        {
            MeldeGeaendert();
            StrokesGeaendert?.Invoke(this);
        };
    }

    public InkBlockVm(InkBlockContent c) : this(c.Strokes ?? new StrokeCollection())
    {
        Datei = c.Datei;
        Bild = c.Bild;
        Muster = c.Muster;
        ErkannterText = c.ErkannterText;
        _hoehe = Math.Clamp(c.Hoehe, MinHoehe, MaxHoehe);
        if (Bild is null)
            _hintergrund = MusterBrush(Muster);
    }

    /// <summary>Feuert bei jeder Strich-Änderung — der Editor hängt hier die Erkennung dran.</summary>
    public event Action<InkBlockVm>? StrokesGeaendert;

    public InkBlockContent ZuModel() => new()
    {
        Datei = Datei,
        Bild = Bild,
        Muster = Muster,
        ErkannterText = ErkannterText,
        Hoehe = Hoehe,
        Strokes = Strokes,
    };
}
