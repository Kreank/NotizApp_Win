using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    /// <summary>ImageBrush des Hintergrundbilds fürs InkCanvas (Transparent ohne Bild).</summary>
    public Brush Hintergrund
    {
        get => _hintergrund;
        private set { _hintergrund = value; OnChanged(); }
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
            Hintergrund = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
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
        ErkannterText = c.ErkannterText;
        _hoehe = Math.Clamp(c.Hoehe, MinHoehe, MaxHoehe);
    }

    /// <summary>Feuert bei jeder Strich-Änderung — der Editor hängt hier die Erkennung dran.</summary>
    public event Action<InkBlockVm>? StrokesGeaendert;

    public InkBlockContent ZuModel() => new()
    {
        Datei = Datei,
        Bild = Bild,
        ErkannterText = ErkannterText,
        Hoehe = Hoehe,
        Strokes = Strokes,
    };
}
