using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotizApp.Models;

namespace NotizApp.Controls;

/// <summary>Basis für die frei auf der Fläche platzierten Element-VMs.</summary>
public abstract class ElementVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event Action? Geaendert;
    protected void MeldeGeaendert() => Geaendert?.Invoke();

    public const double MinBreite = 80;

    double _x, _y;
    double _breite = 620;

    public double X
    {
        get => _x;
        set { if (Setze(ref _x, Math.Max(0, value))) MeldeGeaendert(); }
    }

    public double Y
    {
        get => _y;
        set { if (Setze(ref _y, Math.Max(0, value))) MeldeGeaendert(); }
    }

    public double Breite
    {
        get => _breite;
        set { if (Setze(ref _breite, Math.Max(MinBreite, value))) MeldeGeaendert(); }
    }

    protected bool Setze(ref double feld, double wert, [CallerMemberName] string? n = null)
    {
        if (Math.Abs(feld - wert) < 0.5) return false;
        feld = wert;
        OnChanged(n);
        return true;
    }

    /// <summary>Unterkante auf der Fläche — fürs Mitwachsen der Seite.</summary>
    public abstract double Unterkante { get; }

    public abstract NoteElement ZuModel();

    protected void UebernehmePosition(NoteElement el)
    {
        el.X = X;
        el.Y = Y;
        el.Breite = Breite;
    }
}

/// <summary>Textfeld auf der Fläche; Farbe kommt z.B. von umgewandelter Handschrift.</summary>
public class TextElementVm : ElementVm
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

    string? _farbe;
    /// <summary>Hex "#RRGGBB", null = Standard-Textfarbe des Designs.</summary>
    public string? Farbe
    {
        get => _farbe;
        set
        {
            if (_farbe == value) return;
            _farbe = value;
            OnChanged();
            OnChanged(nameof(FarbBrush));
            MeldeGeaendert();
        }
    }

    /// <summary>Brush für die TextBox; null → DataTrigger setzt die Design-Farbe.</summary>
    public Brush? FarbBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_farbe)) return null;
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_farbe));
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }

    /// <summary>Tatsächlich gerenderte Höhe (setzt der Editor nach dem Layout) — nur fürs Mitwachsen.</summary>
    public double AnzeigeHoehe { get; set; } = 28;

    public override double Unterkante => Y + AnzeigeHoehe;

    public TextElementVm() { }
    public TextElementVm(TextElement el)
    {
        X = el.X; Y = el.Y; Breite = el.Breite;
        _text = el.Text;
        _farbe = el.Farbe;
    }

    public override NoteElement ZuModel()
    {
        var el = new TextElement { Text = Text, Farbe = Farbe };
        UebernehmePosition(el);
        return el;
    }
}

/// <summary>Bild-Objekt: verschieb- und skalierbar, Tinte kann darüber liegen.</summary>
public class BildElementVm : ElementVm
{
    public const double MinHoehe = 40;

    public string Datei { get; set; } = "";

    double _hoehe = 240;
    public double Hoehe
    {
        get => _hoehe;
        set { if (Setze(ref _hoehe, Math.Max(MinHoehe, value))) MeldeGeaendert(); }
    }

    /// <summary>Seitenverhältnis des geladenen Bilds (Breite/Höhe), fürs proportionale Skalieren.</summary>
    public double Seitenverhaeltnis { get; private set; } = 4.0 / 3.0;

    Brush _hintergrund = Brushes.White;
    public Brush Hintergrund
    {
        get => _hintergrund;
        private set { _hintergrund = value; OnChanged(); }
    }

    public override double Unterkante => Y + Hoehe;

    public BildElementVm() { }
    public BildElementVm(BildElement el)
    {
        X = el.X; Y = el.Y; Breite = el.Breite;
        Datei = el.Datei;
        _hoehe = Math.Max(MinHoehe, el.Hoehe);
    }

    /// <summary>Bild laden (ohne Datei-Lock); bei erstBemessen Höhe ans Seitenverhältnis anpassen.</summary>
    public void LadeBild(string ordner, bool erstBemessen = false)
    {
        if (string.IsNullOrWhiteSpace(Datei)) return;
        try
        {
            var pfad = Path.Combine(ordner, Datei);
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
            if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
            {
                Seitenverhaeltnis = (double)bmp.PixelWidth / bmp.PixelHeight;
                if (erstBemessen)
                {
                    Breite = Math.Min(480, bmp.PixelWidth);
                    Hoehe = Breite / Seitenverhaeltnis;
                }
            }
        }
        catch
        {
            // defektes/fehlendes Bild → Objekt bleibt nutzbar, nur ohne Inhalt
        }
    }

    public override NoteElement ZuModel()
    {
        var el = new BildElement { Datei = Datei, Hoehe = Hoehe };
        UebernehmePosition(el);
        return el;
    }
}

/// <summary>Datei-Objekt (xlsx/docx/md/txt/pdf …): Karte mit Symbol, PDFs mit Seitenvorschau.
/// Doppelklick öffnet die Datei mit der Standard-Anwendung.</summary>
public class DateiElementVm : ElementVm
{
    public const double MinHoehe = 48;

    public string Datei { get; set; } = "";

    double _hoehe = 96;
    public double Hoehe
    {
        get => _hoehe;
        set { if (Setze(ref _hoehe, Math.Max(MinHoehe, value))) MeldeGeaendert(); }
    }

    public string Name => Path.GetFileName(Datei);

    public string Icon => Path.GetExtension(Datei).ToLowerInvariant() switch
    {
        ".pdf" => "📕",
        ".xlsx" or ".xls" or ".csv" => "📊",
        ".docx" or ".doc" => "📝",
        ".md" or ".txt" => "📄",
        _ => "📎",
    };

    ImageSource? _vorschau;
    /// <summary>Erste PDF-Seite als Vorschaubild (null = nur Icon-Karte).</summary>
    public ImageSource? Vorschau
    {
        get => _vorschau;
        private set { _vorschau = value; OnChanged(); OnChanged(nameof(HatVorschau)); }
    }

    public bool HatVorschau => _vorschau is not null;

    public override double Unterkante => Y + Hoehe;

    public DateiElementVm() { }
    public DateiElementVm(DateiElement el)
    {
        X = el.X; Y = el.Y; Breite = el.Breite;
        Datei = el.Datei;
        _hoehe = Math.Max(MinHoehe, el.Hoehe);
        OnChanged(nameof(Name));
        OnChanged(nameof(Icon));
    }

    /// <summary>PDF: erste Seite als Vorschau rendern (Windows.Data.Pdf). Scheitert leise.</summary>
    public async Task LadeVorschauAsync(string ordner, bool erstBemessen = false)
    {
        if (!Datei.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var pfad = Path.Combine(ordner, Datei);
            if (!File.Exists(pfad)) return;
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(pfad);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(sf);
            if (doc.PageCount == 0) return;
            using var seite = doc.GetPage(0);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await seite.RenderToStreamAsync(stream,
                new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 900 });

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream.AsStreamForRead();
            bmp.EndInit();
            bmp.Freeze();
            Vorschau = bmp;
            if (erstBemessen && seite.Size.Width > 0)
            {
                Breite = 380;
                Hoehe = Breite * seite.Size.Height / seite.Size.Width;
            }
        }
        catch
        {
            // kein PDF-Renderer / defekte Datei → Icon-Karte reicht
        }
    }

    public override NoteElement ZuModel()
    {
        var el = new DateiElement { Datei = Datei, Hoehe = Hoehe };
        UebernehmePosition(el);
        return el;
    }
}

/// <summary>Kachel-Brushes für die Papier-Muster der Fläche (dezentes Grau, hell wie dunkel).</summary>
public static class PapierMuster
{
    public static readonly string?[] Folge = { null, "linien", "karo", "punkte" };

    public static string? Naechstes(string? muster) =>
        Folge[(Array.IndexOf(Folge, muster) + 1) % Folge.Length];

    public static Brush Brush(string? muster)
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
}
