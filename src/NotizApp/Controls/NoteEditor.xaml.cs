using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NotizApp.Models;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>
/// Freiform-Editor einer Notiz: Titel/Tags/Kundendaten-Kopf, Werkzeugleiste und
/// EINE große Tintenfläche, auf der Textfelder, Bilder und Dateien als frei
/// verschieb- und skalierbare Objekte liegen. Handschrift geht überall — auch
/// über Objekten — und wird auf Wunsch an Ort und Stelle (samt Stiftfarbe)
/// in Tipptext umgewandelt.
/// </summary>
public partial class NoteEditor : UserControl
{
    /// <summary>Wird vom Host gesetzt (App-weit ein Erkenner).</summary>
    public InkRecognitionService? Erkennung { get; set; }

    /// <summary>Wird vom Host gesetzt (KI-Anbindung, V2).</summary>
    public KiService? Ki { get; set; }

    /// <summary>Feuert bei jeder inhaltlichen Änderung — Host macht Autosave-Debounce.</summary>
    public event Action? NotizGeaendert;
    /// <summary>Speichern-Button gedrückt.</summary>
    public event Action? SpeichernAngefordert;
    /// <summary>Fokus-Modus umgeschaltet (Host klappt die Seitenleisten ein/aus).</summary>
    public event Action<bool>? FokusUmgeschaltet;
    /// <summary>Chat-Button gedrückt (Host blendet das Chat-Panel ein/aus).</summary>
    public event Action? ChatAngefordert;

    Note? _note;
    bool _laden;          // Guard: Notiz wird gerade in die UI geladen
    bool _konvertiere;    // Guard: Strokes werden programmatisch entfernt
    bool _initialisiert;  // Guard: InitializeComponent feuert bereits Events

    readonly List<ElementVm> _elemente = new();
    readonly Dictionary<ElementVm, ContentControl> _hosts = new();

    StrokeCollection _strokes = new();
    /// <summary>Seit der letzten Umwandlung neu geschriebene Striche (ohne Marker).</summary>
    readonly List<Stroke> _erkennungPending = new();
    readonly DispatcherTimer _erkennungTimer;
    /// <summary>Hintergrund erkannter Text der verbliebenen Handschrift (Suche/KI).</summary>
    string _tintenText = "";

    string _farbe = "#3B78D8"; // Blau als Default — Schwarz ist im Dark Mode unsichtbar
    string? _muster;

    public NoteEditor()
    {
        InitializeComponent();
        _initialisiert = true;

        TypBox.ItemsSource = Templates.Alle
            .Select(v => new ComboBoxItem { Content = $"{v.Icon} {v.Name}", Tag = v.Key })
            .ToList();

        _erkennungTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.3),
        };
        _erkennungTimer.Tick += ErkennungTimer_Tick;

        _strokes.StrokesChanged += Strokes_Changed;
        Flaeche.Strokes = _strokes;
        WendeWerkzeugAn(); // Auswahl-Modus aktivieren (InkCanvas-Default wäre Zeichnen)

        IsEnabled = false; // bis eine Notiz geladen wird
    }

    public bool HatNote => _note is not null;

    string NotizOrdner => System.IO.Path.GetDirectoryName(_note!.Pfad)!;

    // ---------- Notiz laden / zurückschreiben ----------

    public void LadeNote(Note? note)
    {
        _erkennungTimer.Stop();
        _erkennungPending.Clear();
        _note = note;
        _laden = true;
        try
        {
            foreach (var vm in _elemente.ToList())
                EntferneElement(vm);

            if (note is null)
            {
                TitelBox.Text = "";
                TagsBox.Text = "";
                KundeNameBox.Text = "";
                KundeTelefonBox.Text = "";
                KundeAdresseBox.Text = "";
                DringlichkeitBox.SelectedIndex = 0;
                TypBox.SelectedIndex = -1;
                SetzeStrokes(new StrokeCollection());
                _tintenText = "";
                IsEnabled = false;
                return;
            }

            IsEnabled = true;
            TitelBox.Text = note.Meta.Titel;
            TagsBox.Text = string.Join(", ", note.Meta.Tags);
            KundeNameBox.Text = note.Meta.Kunde.Name ?? "";
            KundeTelefonBox.Text = note.Meta.Kunde.Telefon ?? "";
            KundeAdresseBox.Text = note.Meta.Kunde.Adresse ?? "";
            WaehleCombo(DringlichkeitBox, note.Meta.Dringlichkeit ?? "");
            WaehleCombo(TypBox, note.Meta.Typ);
            KopfExpander.IsExpanded = !note.Meta.Kunde.IstLeer;

            _muster = note.Muster;
            Flaeche.Background = PapierMuster.Brush(_muster);
            _tintenText = note.TintenText;
            SetzeStrokes(note.Tinte ?? new StrokeCollection());

            foreach (var el in note.Elemente)
            {
                ElementVm vm = el switch
                {
                    TextElement t => new TextElementVm(t),
                    BildElement b => new BildElementVm(b),
                    DateiElement d => new DateiElementVm(d),
                    _ => throw new InvalidOperationException(),
                };
                if (vm is BildElementVm bild)
                    bild.LadeBild(NotizOrdner);
                if (vm is DateiElementVm datei)
                    _ = datei.LadeVorschauAsync(NotizOrdner);
                FuegeElementHinzu(vm);
            }
            // Leere Notiz: gleich ein Textfeld zum Lostippen anbieten
            if (_elemente.Count == 0)
                FuegeElementHinzu(new TextElementVm { X = 0, Y = 8, Breite = 620 });

            PasseHoeheAn();
        }
        finally
        {
            _laden = false;
        }
    }

    void SetzeStrokes(StrokeCollection strokes)
    {
        _strokes.StrokesChanged -= Strokes_Changed;
        _strokes = strokes;
        _strokes.StrokesChanged += Strokes_Changed;
        Flaeche.Strokes = _strokes;
    }

    static void WaehleCombo(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    void MeldeAenderung()
    {
        if (_laden || _konvertiere) return;
        NotizGeaendert?.Invoke();
    }

    /// <summary>Schreibt UI-Zustand zurück in die Note (vor dem Speichern aufrufen).</summary>
    public void UebernehmeInNote()
    {
        if (_note is null) return;
        _note.Meta.Titel = TitelBox.Text.Trim();
        _note.Meta.Tags = TagsBox.Text.Split(',')
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _note.Meta.Kunde.Name = LeerZuNull(KundeNameBox.Text);
        _note.Meta.Kunde.Telefon = LeerZuNull(KundeTelefonBox.Text);
        _note.Meta.Kunde.Adresse = LeerZuNull(KundeAdresseBox.Text);
        _note.Meta.Dringlichkeit =
            LeerZuNull((DringlichkeitBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "");
        if ((TypBox.SelectedItem as ComboBoxItem)?.Tag is string typ && typ.Length > 0)
            _note.Meta.Typ = typ;

        _note.Elemente = _elemente
            .Where(vm => vm is not TextElementVm { Text: "" }) // leere Textfelder nicht speichern
            .Select(vm => vm.ZuModel())
            .ToList();
        _note.Tinte = _strokes;
        _note.TintenText = _tintenText;
        _note.Muster = _muster;
        _note.FlaecheHoehe = BenoetigteHoehe() + 200;
    }

    static string? LeerZuNull(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------- Elemente verwalten ----------

    void FuegeElementHinzu(ElementVm vm, bool fokussieren = false)
    {
        vm.Geaendert += MeldeAenderung;
        _elemente.Add(vm);

        var host = new ContentControl { Content = vm, Focusable = false };
        host.SetBinding(InkCanvas.LeftProperty,
            new System.Windows.Data.Binding(nameof(ElementVm.X)) { Source = vm });
        host.SetBinding(InkCanvas.TopProperty,
            new System.Windows.Data.Binding(nameof(ElementVm.Y)) { Source = vm });
        _hosts[vm] = host;
        Flaeche.Children.Add(host);

        if (fokussieren)
        {
            // Erst nach dem Layout gibt es die TextBox im Template
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (FindeKind<TextBox>(host) is { } box) box.Focus();
            });
        }
    }

    void EntferneElement(ElementVm vm)
    {
        vm.Geaendert -= MeldeAenderung;
        _elemente.Remove(vm);
        if (_hosts.Remove(vm, out var host))
            Flaeche.Children.Remove(host);
    }

    static T? FindeKind<T>(DependencyObject wurzel) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(wurzel); i++)
        {
            var kind = VisualTreeHelper.GetChild(wurzel, i);
            if (kind is T passt) return passt;
            if (FindeKind<T>(kind) is { } tiefer) return tiefer;
        }
        return null;
    }

    /// <summary>Steckt der Visual-Baum-Knoten in einem unserer Element-Hosts?</summary>
    bool IstInElement(object? quelle)
    {
        var d = quelle as DependencyObject;
        while (d is not null && d != Flaeche)
        {
            if (d is ContentControl cc && cc.Content is ElementVm) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    double NaechsteFreieY()
    {
        double y = 8;
        foreach (var vm in _elemente)
            y = Math.Max(y, vm.Unterkante + 24);
        if (_strokes.Count > 0)
            y = Math.Max(y, _strokes.GetBounds().Bottom + 24);
        return y;
    }

    // ---------- Fläche: Höhe, Doppelklick, Muster ----------

    double BenoetigteHoehe()
    {
        double h = 300;
        foreach (var vm in _elemente)
            h = Math.Max(h, vm.Unterkante);
        if (_strokes.Count > 0)
            h = Math.Max(h, _strokes.GetBounds().Bottom);
        return h;
    }

    /// <summary>Fläche wächst mit dem Inhalt mit (plus Platz zum Weiterschreiben).</summary>
    void PasseHoeheAn()
    {
        double ziel = Math.Max(BenoetigteHoehe() + 400, Scroller.ViewportHeight - 24);
        if (double.IsNaN(Flaeche.Height) || Math.Abs(Flaeche.Height - ziel) > 1)
            Flaeche.Height = ziel;
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => PasseHoeheAn();

    void Flaeche_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Doppelklick auf freie Fläche (im Auswahl-Modus) = neues Textfeld
        if (e.ClickCount != 2 || _note is null) return;
        if (AktuellerModus() != InkCanvasEditingMode.None) return;
        if (IstInElement(e.OriginalSource)) return;

        var p = e.GetPosition(Flaeche);
        FuegeElementHinzu(new TextElementVm
        {
            X = Math.Max(0, p.X),
            Y = Math.Max(0, p.Y - 10),
            Breite = 320,
        }, fokussieren: true);
        MeldeAenderung();
        e.Handled = true;
    }

    void Muster_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        _muster = PapierMuster.Naechstes(_muster);
        Flaeche.Background = PapierMuster.Brush(_muster);
        MeldeAenderung();
    }

    // ---------- Element-Interaktion (Griffe) ----------

    static T? VmVon<T>(object sender) where T : ElementVm =>
        (sender as FrameworkElement)?.DataContext as T;

    void ElementVerschieben_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (VmVon<ElementVm>(sender) is not { } vm) return;
        vm.X += e.HorizontalChange;
        vm.Y += e.VerticalChange;
        PasseHoeheAn();
    }

    void TextBreite_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (VmVon<TextElementVm>(sender) is { } vm)
            vm.Breite += e.HorizontalChange;
    }

    void BildGroesse_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (VmVon<BildElementVm>(sender) is not { } vm) return;
        vm.Breite += e.HorizontalChange;
        vm.Hoehe = vm.Breite / vm.Seitenverhaeltnis; // proportional skalieren
        PasseHoeheAn();
    }

    void DateiGroesse_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (VmVon<DateiElementVm>(sender) is not { } vm) return;
        vm.Breite += e.HorizontalChange;
        vm.Hoehe += e.VerticalChange;
        PasseHoeheAn();
    }

    void ElementLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if (VmVon<ElementVm>(sender) is not { } vm) return;
        EntferneElement(vm);
        MeldeAenderung();
    }

    void TextElement_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Tatsächliche Höhe fürs Mitwachsen der Fläche merken
        if (VmVon<TextElementVm>(sender) is { } vm)
        {
            vm.AnzeigeHoehe = e.NewSize.Height;
            PasseHoeheAn();
        }
    }

    void DateiElement_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _note is null) return;
        if (VmVon<DateiElementVm>(sender) is not { } vm) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.IO.Path.Combine(NotizOrdner, vm.Datei))
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(Window.GetWindow(this)!,
                $"Die Datei \"{vm.Datei}\" konnte nicht geöffnet werden.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    // ---------- Meta-Events ----------

    void Meta_TextChanged(object sender, TextChangedEventArgs e) => MeldeAenderung();

    void Meta_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialisiert) return;
        MeldeAenderung();
    }

    void DickeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialisiert) return;
        WendeWerkzeugAn();
    }

    void Speichern_Click(object sender, RoutedEventArgs e) => SpeichernAngefordert?.Invoke();

    void Fokus_Umschalten(object sender, RoutedEventArgs e)
    {
        if (!_initialisiert) return;
        FokusUmgeschaltet?.Invoke(FokusToggle.IsChecked == true);
    }

    /// <summary>Fokus-Zustand von außen setzen (F11 im Hauptfenster).</summary>
    public void SetzeFokusToggle(bool an) => FokusToggle.IsChecked = an;

    void Chat_Click(object sender, RoutedEventArgs e) => ChatAngefordert?.Invoke();

    // ---------- Übernahme aus dem KI-Chat ----------

    /// <summary>Titel + KI-Body der aktuellen Notiz (nie der Kundendaten-Kopf); null wenn leer.</summary>
    public (string Titel, string Body)? KiKontext()
    {
        if (_note is null) return null;
        var body = KiService.ErzeugeKiBody(
            _elemente.OfType<TextElementVm>()
                .OrderBy(t => t.Y).ThenBy(t => t.X)
                .Select(t => t.Text),
            _tintenText);
        if (string.IsNullOrWhiteSpace(body)) return null;
        var titel = TitelBox.Text.Trim();
        return (titel.Length == 0 ? "(ohne Titel)" : titel, body);
    }

    /// <summary>Text als neues Textfeld unten an die Notiz anfügen (z.B. Chat-Antwort).</summary>
    public void FuegeTextAn(string text)
    {
        if (_note is null || string.IsNullOrWhiteSpace(text)) return;
        FuegeElementHinzu(new TextElementVm
        {
            X = 0,
            Y = NaechsteFreieY(),
            Breite = 620,
            Text = text.Trim(),
        });
        PasseHoeheAn();
        MeldeAenderung();
    }

    /// <summary>Externe Datei kopieren und als Objekt auf die Fläche legen (z.B. Chat-Anhang).</summary>
    public void FuegeExterneDateiAn(string pfad)
    {
        if (_note is null || !System.IO.File.Exists(pfad)) return;
        FuegeDateiObjektAn(KopiereAnhang(pfad));
        MeldeAenderung();
    }

    // ---------- Werkzeuge ----------

    void Werkzeug_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialisiert) return;
        foreach (var t in new[] { AuswahlToggle, StiftToggle, MarkerToggle,
                     RadiererToggle, PunktRadiererToggle, LassoToggle })
        {
            if (t != sender) t.IsChecked = false;
        }
        WendeWerkzeugAn();
    }

    void Farbe_Click(object sender, RoutedEventArgs e)
    {
        _farbe = (string)((Button)sender).Tag;
        // Farbwahl aktiviert den Stift — außer der Marker ist gerade aktiv (der färbt mit)
        if (MarkerToggle.IsChecked != true)
            StiftToggle.IsChecked = true;
        WendeWerkzeugAn();
    }

    Color AktuelleFarbe() => _farbe == "auto"
        ? (IstDunklesDesign() ? Colors.White : Colors.Black)
        : (Color)ColorConverter.ConvertFromString(_farbe);

    DrawingAttributes AktuelleAttribute()
    {
        double dicke = DickeSlider?.Value ?? 2.2;

        if (MarkerToggle.IsChecked == true)
        {
            // Marker: eigene Farbe nur, wenn eine echte Farbe gewählt ist (auto = Gelb)
            var markerFarbe = _farbe == "auto"
                ? Color.FromArgb(255, 255, 210, 0)
                : AktuelleFarbe();
            return new DrawingAttributes
            {
                Color = markerFarbe,
                IsHighlighter = true,
                Width = dicke * 4,
                Height = dicke * 8,
                StylusTip = StylusTip.Rectangle,
            };
        }

        return new DrawingAttributes
        {
            Color = AktuelleFarbe(),
            Width = dicke,
            Height = dicke,
            FitToCurve = true,
        };
    }

    static bool IstDunklesDesign()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { return false; }
    }

    InkCanvasEditingMode AktuellerModus()
    {
        if (RadiererToggle.IsChecked == true) return InkCanvasEditingMode.EraseByStroke;
        if (PunktRadiererToggle.IsChecked == true) return InkCanvasEditingMode.EraseByPoint;
        if (LassoToggle.IsChecked == true) return InkCanvasEditingMode.Select;
        if (StiftToggle.IsChecked == true || MarkerToggle.IsChecked == true)
            return InkCanvasEditingMode.Ink;
        return InkCanvasEditingMode.None; // Auswahl/Tippen
    }

    void WendeWerkzeugAn()
    {
        var da = AktuelleAttribute();
        var radiererGroesse = Math.Max(4, (DickeSlider?.Value ?? 2.2) * 3);

        // EraserShape greift erst nach einem EditingMode-Wechsel (WPF-Eigenheit)
        Flaeche.EditingMode = InkCanvasEditingMode.None;
        Flaeche.EraserShape = new EllipseStylusShape(radiererGroesse, radiererGroesse);
        Flaeche.DefaultDrawingAttributes = da.Clone();
        Flaeche.EditingMode = AktuellerModus();

        // Vorschau-Punkt in der Werkzeugleiste aktualisieren
        if (VorschauPunkt is not null)
        {
            var d = Math.Clamp(da.Width, 3, 20);
            VorschauPunkt.Width = d;
            VorschauPunkt.Height = d;
            VorschauPunkt.Fill = new SolidColorBrush(da.Color);
        }
    }

    void Flaeche_SelectionChanged(object? sender, EventArgs e) =>
        UmwandelnButton.IsEnabled = Flaeche.GetSelectedStrokes().Count > 0;

    // ---------- Dateien / Bilder als Objekte ----------

    /// <summary>Datei neben die Notiz kopieren (Namensschema &lt;mdname&gt;.&lt;name&gt;) und Zielnamen liefern.</summary>
    string KopiereAnhang(string quelle)
    {
        var ordner = NotizOrdner;
        var mdName = System.IO.Path.GetFileNameWithoutExtension(_note!.Pfad);
        var name = System.IO.Path.GetFileName(quelle);
        var ziel = System.IO.Path.Combine(ordner, $"{mdName}.{name}");
        int n = 2;
        while (System.IO.File.Exists(ziel))
            ziel = System.IO.Path.Combine(ordner, $"{mdName}.{n++}-{name}");
        System.IO.File.Copy(quelle, ziel);
        return System.IO.Path.GetFileName(ziel);
    }

    static readonly string[] BildEndungen = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    static bool IstBild(string pfad) =>
        BildEndungen.Contains(System.IO.Path.GetExtension(pfad).ToLowerInvariant());

    /// <summary>Kopierten Anhang als Objekt auf die Fläche legen (Bild oder Datei-Karte).</summary>
    void FuegeDateiObjektAn(string dateiname, double? x = null, double? y = null)
    {
        double zielY = y ?? NaechsteFreieY();
        double zielX = x ?? 0;
        if (IstBild(dateiname))
        {
            var vm = new BildElementVm { Datei = dateiname, X = zielX, Y = zielY };
            vm.LadeBild(NotizOrdner, erstBemessen: true);
            FuegeElementHinzu(vm);
        }
        else
        {
            var vm = new DateiElementVm
            {
                Datei = dateiname,
                X = zielX,
                Y = zielY,
                Breite = 300,
                Hoehe = 96,
            };
            FuegeElementHinzu(vm);
            _ = vm.LadeVorschauAsync(NotizOrdner, erstBemessen: true);
        }
        PasseHoeheAn();
    }

    void DateiEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Bild oder Datei als Objekt ablegen",
            Multiselect = true,
            Filter = "Unterstützte Dateien|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.pdf;*.xlsx;*.docx;*.md;*.txt" +
                     "|Bilder|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" +
                     "|Dokumente|*.pdf;*.xlsx;*.docx;*.md;*.txt" +
                     "|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var datei in dialog.FileNames)
            FuegeDateiObjektAn(KopiereAnhang(datei));
        MeldeAenderung();
    }

    void Flaeche_Drop(object sender, DragEventArgs e)
    {
        if (_note is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var punkt = e.GetPosition(Flaeche);
        double y = punkt.Y;
        foreach (var datei in (string[])e.Data.GetData(DataFormats.FileDrop))
        {
            if (!System.IO.File.Exists(datei)) continue;
            FuegeDateiObjektAn(KopiereAnhang(datei), Math.Max(0, punkt.X), Math.Max(0, y));
            y += 40; // mehrere Dateien leicht versetzt stapeln
        }
        MeldeAenderung();
    }

    /// <summary>KI-erzeugte Dateien übernehmen: alles als Objekte auf die Fläche.</summary>
    void HaengeDateienAn(List<string> quellen)
    {
        foreach (var quelle in quellen)
            FuegeDateiObjektAn(KopiereAnhang(quelle));
        MeldeAenderung();
    }

    // ---------- KI (V2) ----------

    void Ki_Click(object sender, RoutedEventArgs e)
    {
        // Linksklick öffnet das Aktions-Menü unter dem Button
        var menu = KiButton.ContextMenu!;
        menu.PlacementTarget = KiButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    string? AktuellerKiBody()
    {
        if (KiKontext() is { } kontext) return kontext.Body;
        MessageBox.Show(Window.GetWindow(this)!,
            "Die Notiz enthält noch keinen Text, den die KI verarbeiten könnte.",
            "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    void KiAktion_Click(object sender, RoutedEventArgs e)
    {
        if (Ki is null || _note is null) return;
        var aktion = Enum.Parse<KiAktion>((string)((MenuItem)sender).Tag);
        if (AktuellerKiBody() is not string body) return;

        var dialog = new KiVorschlagWindow(Ki, aktion, body)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Ergebnis))
            return;
        UebernehmeKiErgebnis(aktion, dialog.Ergebnis);
    }

    void KiDokument_Click(object sender, RoutedEventArgs e)
    {
        if (Ki is null || _note is null) return;
        var auftrag = TextPromptWindow.Frage(Window.GetWindow(this)!, "Dateien erstellen/suchen",
            "Was soll Claude erstellen oder suchen?\n(z.B. \"Kundenschreiben mit Terminvorschlag als PDF\"\noder \"Explosionszeichnung / Bilder zur Vaillant ecoTEC\")");
        if (string.IsNullOrWhiteSpace(auftrag)) return;
        if (AktuellerKiBody() is not string body) return;

        var dialog = new KiVorschlagWindow(Ki, auftrag, body)
        {
            Owner = Window.GetWindow(this),
        };
        var ok = dialog.ShowDialog() == true && dialog.ErzeugteDateien.Count > 0;
        if (ok) HaengeDateienAn(dialog.ErzeugteDateien);
        dialog.RaeumeAusgabeAuf();
    }

    /// <summary>Alles auf der Fläche nach unten rücken (Platz für neuen Inhalt oben).</summary>
    void RueckeAllesNachUnten(double delta)
    {
        foreach (var vm in _elemente)
            vm.Y += delta;
        if (_strokes.Count > 0)
        {
            var m = Matrix.Identity;
            m.Translate(0, delta);
            _strokes.Transform(m, applyToStylusTip: false);
        }
    }

    static double GeschaetzteTextHoehe(string text) =>
        Math.Max(28, text.Split('\n').Length * 22 + 16);

    void UebernehmeKiErgebnis(KiAktion aktion, string text)
    {
        switch (aktion)
        {
            case KiAktion.Zusammenfassen:
                // Zusammenfassung ganz nach oben, Bestehendes rückt nach unten
                var kopfText = $"## Zusammenfassung\n\n{text}";
                RueckeAllesNachUnten(GeschaetzteTextHoehe(kopfText) + 24);
                FuegeElementHinzu(new TextElementVm { X = 0, Y = 8, Breite = 620, Text = kopfText });
                break;

            case KiAktion.Aufbereiten:
                var texte = _elemente.OfType<TextElementVm>()
                    .OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
                if (_strokes.Count == 0 && texte.Count == _elemente.Count && texte.Count > 0)
                {
                    // Reine Textnotiz: kompletten Text ersetzen
                    foreach (var alt in texte.Skip(1))
                        EntferneElement(alt);
                    texte[0].Text = text;
                }
                else
                {
                    // Mit Tinte/Objekten: Original behalten, Aufbereitung anhängen
                    FuegeElementHinzu(new TextElementVm
                    {
                        X = 0,
                        Y = NaechsteFreieY(),
                        Breite = 620,
                        Text = $"## Aufbereitet\n\n{text}",
                    });
                }
                break;

            case KiAktion.Aufgaben:
                // Defensiv: nur die Checkbox-Zeilen übernehmen, falls doch Drumherum-Text kommt
                var zeilen = text.Replace("\r\n", "\n").Split('\n')
                    .Where(z => z.TrimStart().StartsWith("- ["))
                    .ToList();
                if (zeilen.Count > 0) text = string.Join('\n', zeilen);
                if (zeilen.Count == 0 || text.Trim().Equals("keine", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(Window.GetWindow(this)!,
                        "Claude hat keine Aufgaben in der Notiz gefunden.",
                        "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                FuegeElementHinzu(new TextElementVm
                {
                    X = 0,
                    Y = NaechsteFreieY(),
                    Breite = 620,
                    Text = $"## Aufgaben\n\n{text}",
                });
                break;
        }
        PasseHoeheAn();
        MeldeAenderung();
    }

    // ---------- Handschrifterkennung ----------

    void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_laden || _konvertiere) return;
        foreach (Stroke s in e.Removed)
            _erkennungPending.Remove(s);
        foreach (Stroke s in e.Added)
        {
            // Marker-Striche sind Hervorhebungen — nie in Text umwandeln
            if (!s.DrawingAttributes.IsHighlighter)
                _erkennungPending.Add(s);
        }
        PasseHoeheAn();
        MeldeAenderung();
        // Debounce: Timer bei jeder Änderung neu starten (auch fürs Nach-Erkennen beim Radieren)
        _erkennungTimer.Stop();
        _erkennungTimer.Start();
    }

    async void ErkennungTimer_Tick(object? sender, EventArgs e)
    {
        _erkennungTimer.Stop();
        if (Erkennung is null || _note is null) { _erkennungPending.Clear(); return; }

        if (ErkennungToggle.IsChecked == true && _erkennungPending.Count > 0)
        {
            var gruppe = new StrokeCollection(
                _erkennungPending.Where(s => _strokes.Contains(s)));
            _erkennungPending.Clear();
            if (gruppe.Count > 0)
            {
                var text = await Erkennung.ErkenneAsync(gruppe);
                if (_erkennungPending.Count > 0)
                {
                    // Während des await wurde weitergeschrieben → alles zusammen
                    // beim nächsten Tick umwandeln (Timer läuft schon wieder)
                    _erkennungPending.AddRange(gruppe);
                    return;
                }
                if (text is not null)
                    KonvertiereZuText(gruppe, text);
            }
        }
        else
        {
            _erkennungPending.Clear();
        }

        // Hintergrunderkennung der verbliebenen Handschrift (für Suche + KI)
        var schrift = new StrokeCollection(
            _strokes.Where(s => !s.DrawingAttributes.IsHighlighter));
        var erkannt = schrift.Count > 0
            ? await Erkennung.ErkenneAsync(schrift) ?? ""
            : "";
        if (erkannt != _tintenText)
        {
            _tintenText = erkannt;
            MeldeAenderung();
        }
    }

    /// <summary>Lasso-Auswahl per Knopfdruck in Tipptext umwandeln (Farbe bleibt).</summary>
    async void AuswahlZuText_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var auswahl = Flaeche.GetSelectedStrokes();
        var schrift = new StrokeCollection(
            auswahl.Where(s => !s.DrawingAttributes.IsHighlighter));
        if (schrift.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Erst mit dem Lasso (⬚) Handschrift auswählen, dann umwandeln.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Erkennung is null) return;

        var text = await Erkennung.ErkenneAsync(schrift);
        if (text is null)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Die Auswahl konnte nicht als Handschrift erkannt werden.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        KonvertiereZuText(schrift, text);
    }

    /// <summary>
    /// Erkannte Striche durch ein Textfeld an derselben Stelle ersetzen — in der
    /// Stiftfarbe. Schließt direkt an ein vorheriges umgewandeltes Feld gleicher
    /// Farbe an, damit fortlaufendes Schreiben ein Feld ergibt.
    /// </summary>
    void KonvertiereZuText(StrokeCollection strokes, string text)
    {
        var bounds = strokes.GetBounds();
        var farbe = DominanteFarbe(strokes);

        _konvertiere = true;
        try
        {
            _strokes.Remove(strokes);
            foreach (Stroke s in strokes)
                _erkennungPending.Remove(s);
        }
        finally
        {
            _konvertiere = false;
        }

        // Anschluss-Heuristik: direkt unter einem Textfeld gleicher Farbe weitergeschrieben?
        var anschluss = _elemente.OfType<TextElementVm>()
            .Where(t => t.Farbe == farbe
                && bounds.Y > t.Y
                && bounds.Y < t.Unterkante + 60
                && bounds.X < t.X + t.Breite
                && bounds.Right > t.X)
            .OrderByDescending(t => t.Y)
            .FirstOrDefault();
        if (anschluss is not null)
        {
            var vorhanden = anschluss.Text.TrimEnd();
            anschluss.Text = vorhanden.Length == 0 ? text : vorhanden + "\n" + text;
        }
        else
        {
            FuegeElementHinzu(new TextElementVm
            {
                X = Math.Max(0, bounds.X),
                Y = Math.Max(0, bounds.Y),
                Breite = Math.Max(240, bounds.Width + 40),
                Farbe = farbe,
                Text = text,
            });
        }
        MeldeAenderung();
        // Hintergrunderkennung der restlichen Handschrift auffrischen
        _erkennungTimer.Stop();
        _erkennungTimer.Start();
    }

    /// <summary>Häufigste Stiftfarbe der Striche; Design-Automatikfarbe (Schwarz/Weiß) → null.</summary>
    string? DominanteFarbe(StrokeCollection strokes)
    {
        var farbe = strokes
            .Where(s => !s.DrawingAttributes.IsHighlighter)
            .GroupBy(s => s.DrawingAttributes.Color)
            .OrderByDescending(g => g.Sum(s => s.StylusPoints.Count))
            .Select(g => (Color?)g.Key)
            .FirstOrDefault();
        if (farbe is not Color c) return null;
        var auto = IstDunklesDesign() ? Colors.White : Colors.Black;
        return c == auto ? null : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
