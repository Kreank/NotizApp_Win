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
        ".wav" or ".mp3" or ".m4a" => "🎵",
        _ => "📎",
    };

    ImageSource? _vorschau;
    /// <summary>Aktuelle PDF-Seite als Vorschaubild (null = nur Icon-Karte).</summary>
    public ImageSource? Vorschau
    {
        get => _vorschau;
        private set { _vorschau = value; OnChanged(); OnChanged(nameof(HatVorschau)); }
    }

    public bool HatVorschau => _vorschau is not null;

    /// <summary>Angezeigte PDF-Seite (0-basiert), wird mit der Notiz gespeichert.</summary>
    public int Seite { get; private set; }

    int _seitenAnzahl;
    public bool MehrereSeiten => _seitenAnzahl > 1;
    public string SeitenInfo => $"{Seite + 1} / {_seitenAnzahl}";

    public override double Unterkante => Y + Hoehe;

    public DateiElementVm() { }
    public DateiElementVm(DateiElement el)
    {
        X = el.X; Y = el.Y; Breite = el.Breite;
        Datei = el.Datei;
        _hoehe = Math.Max(MinHoehe, el.Hoehe);
        Seite = Math.Max(0, el.Seite);
        OnChanged(nameof(Name));
        OnChanged(nameof(Icon));
    }

    /// <summary>PDF: gemerkte Seite als Vorschau rendern (Windows.Data.Pdf). Scheitert leise.</summary>
    public Task LadeVorschauAsync(string ordner, bool erstBemessen = false) =>
        RendereSeiteAsync(ordner, erstBemessen);

    /// <summary>Auf der PDF-Karte blättern (delta ±1); die Seite wird mitgespeichert.</summary>
    public async Task BlaettereAsync(string ordner, int delta)
    {
        var ziel = Math.Clamp(Seite + delta, 0, Math.Max(0, _seitenAnzahl - 1));
        if (ziel == Seite || !HatVorschau) return;
        Seite = ziel;
        OnChanged(nameof(SeitenInfo));
        await RendereSeiteAsync(ordner);
        MeldeGeaendert();
    }

    async Task RendereSeiteAsync(string ordner, bool erstBemessen = false)
    {
        if (!Datei.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var pfad = Path.Combine(ordner, Datei);
            if (!File.Exists(pfad)) return;
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(pfad);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(sf);
            if (doc.PageCount == 0) return;
            _seitenAnzahl = (int)doc.PageCount;
            Seite = Math.Clamp(Seite, 0, _seitenAnzahl - 1);
            using var seite = doc.GetPage((uint)Seite);
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
            OnChanged(nameof(MehrereSeiten));
            OnChanged(nameof(SeitenInfo));
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
        var el = new DateiElement { Datei = Datei, Hoehe = Hoehe, Seite = Seite };
        UebernehmePosition(el);
        return el;
    }
}

/// <summary>Eine Zelle der Tabelle: editierbarer Text — oder ein Bild, wenn der
/// Text Markdown-Bildsyntax ist ("![](datei.png)", Datei liegt neben der Notiz).</summary>
public class ZelleVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal Action? Geaendert;
    internal Func<string, ImageSource?>? BildLader;

    string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            MeldeBildNeu();
            Geaendert?.Invoke();
        }
    }

    public bool HatBild => BildDatei is not null;
    public ImageSource? BildQuelle =>
        BildDatei is { } datei ? BildLader?.Invoke(datei) : null;

    string? BildDatei
    {
        get
        {
            var t = _text.Trim();
            if (!t.StartsWith("![") || !t.EndsWith(")")) return null;
            int klammer = t.IndexOf("](", StringComparison.Ordinal);
            return klammer < 0 ? null : t[(klammer + 2)..^1].Trim();
        }
    }

    internal void MeldeBildNeu()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HatBild)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BildQuelle)));
    }
}

/// <summary>Eine Tabellenzeile (nur Container für die Zellen).</summary>
public class TabellenZeileVm
{
    public System.Collections.ObjectModel.ObservableCollection<ZelleVm> Zellen { get; } = new();
}

/// <summary>Tabelle auf der Fläche: verschieb- und breitenverstellbar, Zellen
/// direkt editierbar, erste Zeile ist die Kopfzeile.</summary>
public class TabelleElementVm : ElementVm
{
    public System.Collections.ObjectModel.ObservableCollection<TabellenZeileVm> Zeilen { get; } = new();

    /// <summary>Tatsächlich gerenderte Höhe (setzt der Editor nach dem Layout).</summary>
    public double AnzeigeHoehe { get; set; } = 90;

    public override double Unterkante => Y + AnzeigeHoehe;

    int SpaltenAnzahl => Zeilen.FirstOrDefault()?.Zellen.Count ?? 0;

    public TabelleElementVm() { }
    public TabelleElementVm(TabelleElement el)
    {
        X = el.X; Y = el.Y; Breite = el.Breite;
        foreach (var zeile in el.Zeilen)
            FuegeZeileHinzu(zeile);
    }

    string? _ordner;

    /// <summary>Notizordner setzen — nötig, damit Zellen-Bilder geladen werden können.</summary>
    public void SetzeOrdner(string ordner)
    {
        _ordner = ordner;
        foreach (var zeile in Zeilen)
        {
            foreach (var zelle in zeile.Zellen) zelle.MeldeBildNeu();
        }
    }

    ImageSource? LadeZellBild(string datei)
    {
        if (_ordner is null) return null;
        try
        {
            var pfad = System.IO.Path.Combine(_ordner, datei);
            if (!System.IO.File.Exists(pfad)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 400; // Zellen-Thumbnail — spart Speicher
            bmp.UriSource = new Uri(pfad);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    ZelleVm NeueZelle(string text = "") =>
        new() { Text = text, Geaendert = MeldeGeaendert, BildLader = LadeZellBild };

    public void FuegeZeileHinzu(IEnumerable<string>? werte = null)
    {
        var zeile = new TabellenZeileVm();
        if (werte is not null)
        {
            foreach (var w in werte) zeile.Zellen.Add(NeueZelle(w));
        }
        while (zeile.Zellen.Count < Math.Max(1, SpaltenAnzahl))
            zeile.Zellen.Add(NeueZelle());
        Zeilen.Add(zeile);
        MeldeGeaendert();
    }

    public void FuegeSpalteHinzu()
    {
        foreach (var zeile in Zeilen) zeile.Zellen.Add(NeueZelle());
        MeldeGeaendert();
    }

    public void EntferneLetzteZeile()
    {
        if (Zeilen.Count <= 1) return;
        Zeilen.RemoveAt(Zeilen.Count - 1);
        MeldeGeaendert();
    }

    public void EntferneLetzteSpalte()
    {
        if (SpaltenAnzahl <= 1) return;
        foreach (var zeile in Zeilen)
            zeile.Zellen.RemoveAt(zeile.Zellen.Count - 1);
        MeldeGeaendert();
    }

    /// <summary>Kopf + n leere Zeilen (für den Tabelle-einfügen-Button).</summary>
    public void FuelleStandard(int zeilen, int spalten)
    {
        for (int z = 0; z < zeilen; z++)
            FuegeZeileHinzu(Enumerable.Repeat("", spalten));
    }

    /// <summary>Inhalt als Markdown-Tabelle (für KI-Kontext).</summary>
    public string AlsMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        for (int z = 0; z < Zeilen.Count; z++)
        {
            sb.AppendLine("| " + string.Join(" | ",
                Zeilen[z].Zellen.Select(c => c.Text.Replace("\n", " "))) + " |");
            if (z == 0)
                sb.AppendLine("|" + string.Concat(
                    Enumerable.Repeat(" --- |", Zeilen[z].Zellen.Count)));
        }
        return sb.ToString().TrimEnd('\n');
    }

    public bool IstLeer => Zeilen.All(z => z.Zellen.All(c => c.Text.Trim().Length == 0));

    public override NoteElement ZuModel()
    {
        var el = new TabelleElement
        {
            Zeilen = Zeilen.Select(z => z.Zellen.Select(c => c.Text).ToList()).ToList(),
        };
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
