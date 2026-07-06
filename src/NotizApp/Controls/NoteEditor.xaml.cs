using System.Collections.ObjectModel;
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
/// Block-Editor einer Notiz: Titel/Tags/Kundendaten-Kopf, Werkzeugleiste,
/// Folge von Text- und Tintenblöcken, Handschrifterkennung.
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

    Note? _note;
    bool _laden;          // Guard: Notiz wird gerade in die UI geladen
    bool _konvertiere;    // Guard: Strokes werden programmatisch geleert
    bool _initialisiert;  // Guard: InitializeComponent feuert bereits Events

    readonly ObservableCollection<BlockVm> _bloecke = new();
    readonly List<InkCanvas> _canvases = new();
    readonly HashSet<InkBlockVm> _erkennungPending = new();
    readonly DispatcherTimer _erkennungTimer;

    string _farbe = "#3B78D8"; // Blau als Default — Schwarz ist im Dark Mode unsichtbar

    public NoteEditor()
    {
        InitializeComponent();
        _initialisiert = true;
        BlockListe.ItemsSource = _bloecke;

        TypBox.ItemsSource = Templates.Alle
            .Select(v => new ComboBoxItem { Content = $"{v.Icon} {v.Name}", Tag = v.Key })
            .ToList();

        _erkennungTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.3),
        };
        _erkennungTimer.Tick += ErkennungTimer_Tick;

        IsEnabled = false; // bis eine Notiz geladen wird
    }

    public bool HatNote => _note is not null;

    // ---------- Notiz laden / zurückschreiben ----------

    public void LadeNote(Note? note)
    {
        _erkennungTimer.Stop();
        _erkennungPending.Clear();
        _note = note;
        _laden = true;
        try
        {
            _bloecke.Clear();
            if (note is null)
            {
                TitelBox.Text = "";
                TagsBox.Text = "";
                KundeNameBox.Text = "";
                KundeTelefonBox.Text = "";
                KundeAdresseBox.Text = "";
                DringlichkeitBox.SelectedIndex = 0;
                TypBox.SelectedIndex = -1;
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

            var notizOrdner = System.IO.Path.GetDirectoryName(note.Pfad)!;
            foreach (var block in note.Bloecke)
            {
                BlockVm vm = block switch
                {
                    TextBlockContent t => new TextBlockVm(t),
                    InkBlockContent i => new InkBlockVm(i),
                    _ => throw new InvalidOperationException(),
                };
                if (vm is InkBlockVm ib && ib.Bild is not null)
                    ib.LadeBild(notizOrdner);
                RegistriereVm(vm);
                _bloecke.Add(vm);
            }
            if (_bloecke.Count == 0)
            {
                var vm = new TextBlockVm();
                RegistriereVm(vm);
                _bloecke.Add(vm);
            }
        }
        finally
        {
            _laden = false;
        }
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

    void RegistriereVm(BlockVm vm)
    {
        vm.Geaendert += MeldeAenderung;
        if (vm is InkBlockVm ink)
            ink.StrokesGeaendert += Ink_StrokesGeaendert;
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

        _note.Bloecke = _bloecke.Select<BlockVm, NoteBlock>(vm => vm switch
        {
            TextBlockVm t => t.ZuModel(),
            InkBlockVm i => i.ZuModel(),
            _ => throw new InvalidOperationException(),
        }).ToList();
    }

    static string? LeerZuNull(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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

    // ---------- Werkzeuge ----------

    void Werkzeug_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialisiert) return;
        foreach (var t in new[] { StiftToggle, MarkerToggle, RadiererToggle, PunktRadiererToggle, LassoToggle })
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
        return InkCanvasEditingMode.None;
    }

    void WendeWerkzeugAn()
    {
        var da = AktuelleAttribute();
        var modus = AktuellerModus();
        var radiererGroesse = Math.Max(4, (DickeSlider?.Value ?? 2.2) * 3);
        foreach (var canvas in _canvases)
            WendeWerkzeugAn(canvas, da, modus, radiererGroesse);

        // Vorschau-Punkt in der Werkzeugleiste aktualisieren
        if (VorschauPunkt is not null)
        {
            var d = Math.Clamp(da.Width, 3, 20);
            VorschauPunkt.Width = d;
            VorschauPunkt.Height = d;
            VorschauPunkt.Fill = new SolidColorBrush(da.Color);
        }
    }

    static void WendeWerkzeugAn(InkCanvas canvas, DrawingAttributes da,
        InkCanvasEditingMode modus, double radiererGroesse)
    {
        // EraserShape greift erst nach einem EditingMode-Wechsel (WPF-Eigenheit)
        canvas.EditingMode = InkCanvasEditingMode.None;
        canvas.EraserShape = new EllipseStylusShape(radiererGroesse, radiererGroesse);
        canvas.DefaultDrawingAttributes = da.Clone();
        canvas.EditingMode = modus;
    }

    void InkCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        var canvas = (InkCanvas)sender;
        if (!_canvases.Contains(canvas)) _canvases.Add(canvas);
        WendeWerkzeugAn(canvas, AktuelleAttribute(), AktuellerModus(),
            Math.Max(4, (DickeSlider?.Value ?? 2.2) * 3));
    }

    void InkCanvas_Unloaded(object sender, RoutedEventArgs e) =>
        _canvases.Remove((InkCanvas)sender);

    // ---------- Blöcke einfügen / löschen / Größe ----------

    void NeueTintenflaeche_Click(object sender, RoutedEventArgs e)
    {
        // Linksklick öffnet das Papier-Menü (Blanko/Liniert/Kariert/Punktraster)
        var menu = TintenflaecheButton.ContextMenu!;
        menu.PlacementTarget = TintenflaecheButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void NeueTintenflaecheMuster_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var muster = (string)((MenuItem)sender).Tag;
        var ink = new InkBlockVm();
        ink.SetzeMuster(muster.Length == 0 ? null : muster);
        RegistriereVm(ink);
        _bloecke.Add(ink);
        // Danach direkt weiterschreiben können: Text-Block ans Ende
        var text = new TextBlockVm();
        RegistriereVm(text);
        _bloecke.Add(text);
        MeldeAenderung();
    }

    void InkMuster_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InkBlockVm ink)
            ink.NaechstesMuster();
    }

    void InkLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not InkBlockVm ink) return;
        int i = _bloecke.IndexOf(ink);
        if (i < 0) return;
        _erkennungPending.Remove(ink);
        _bloecke.RemoveAt(i);

        // Benachbarte Text-Blöcke zusammenführen
        if (i > 0 && i < _bloecke.Count &&
            _bloecke[i - 1] is TextBlockVm davor && _bloecke[i] is TextBlockVm danach)
        {
            var zusammen = davor.Text.TrimEnd('\n');
            if (zusammen.Length > 0 && danach.Text.Trim().Length > 0)
                zusammen += "\n\n";
            davor.Text = zusammen + danach.Text;
            _bloecke.RemoveAt(i);
        }
        if (!_bloecke.OfType<TextBlockVm>().Any())
        {
            var t = new TextBlockVm();
            RegistriereVm(t);
            _bloecke.Add(t);
        }
        MeldeAenderung();
    }

    void InkResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InkBlockVm ink)
            ink.Hoehe += e.VerticalChange;
    }

    // ---------- Bilder / Anhänge ----------

    /// <summary>Datei neben die Notiz kopieren (Namensschema <mdname>.<name>) und Zielnamen liefern.</summary>
    string KopiereAnhang(string quelle)
    {
        var ordner = System.IO.Path.GetDirectoryName(_note!.Pfad)!;
        var mdName = System.IO.Path.GetFileNameWithoutExtension(_note.Pfad);
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

    /// <summary>Bild als zeichenbaren Block (InkCanvas mit Hintergrundbild) anhängen.</summary>
    void FuegeBildBlockAn(string dateiname)
    {
        var ordner = System.IO.Path.GetDirectoryName(_note!.Pfad)!;
        var ink = new InkBlockVm { Bild = dateiname };
        ink.LadeBild(ordner, Math.Max(400, BlockListe.ActualWidth));
        RegistriereVm(ink);
        _bloecke.Add(ink);
    }

    void BildEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Bild einfügen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        FuegeBildBlockAn(KopiereAnhang(dialog.FileName));
        var text = new TextBlockVm();
        RegistriereVm(text);
        _bloecke.Add(text);
        MeldeAenderung();
    }

    /// <summary>KI-erzeugte Dateien übernehmen: Bilder als zeichenbare Blöcke, Rest als Link.</summary>
    void HaengeDateienAn(List<string> quellen)
    {
        var links = new List<string>();
        string? ersterAnhang = null;
        foreach (var quelle in quellen)
        {
            var dateiname = KopiereAnhang(quelle);
            if (IstBild(quelle))
            {
                FuegeBildBlockAn(dateiname);
            }
            else
            {
                links.Add($"📎 [{System.IO.Path.GetFileName(quelle)}]({dateiname})");
                ersterAnhang ??= System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_note!.Pfad)!, dateiname);
            }
        }
        if (links.Count > 0)
        {
            var letzter = _bloecke.OfType<TextBlockVm>().LastOrDefault();
            if (letzter is null)
            {
                letzter = new TextBlockVm();
                RegistriereVm(letzter);
                _bloecke.Add(letzter);
            }
            var t = letzter.Text.TrimEnd();
            letzter.Text = (t.Length > 0 ? t + "\n\n" : "") + string.Join('\n', links);
        }
        MeldeAenderung();
        // Nicht-Bild-Anhänge im Explorer zeigen, damit man sie direkt öffnen kann
        if (ersterAnhang is not null)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ersterAnhang}\"");
            }
            catch { }
        }
    }

    // ---------- KI (V2) ----------

    void Ki_Click(object sender, RoutedEventArgs e)
    {
        // Linksklick öffnet das Aktions-Menü unter dem Button
        var menu = KiButton.ContextMenu!;
        menu.PlacementTarget = KiButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    string? AktuellerKiBody()
    {
        var body = KiService.ErzeugeKiBody(_bloecke.Select<BlockVm, NoteBlock>(vm => vm switch
        {
            TextBlockVm t => t.ZuModel(),
            InkBlockVm i => i.ZuModel(),
            _ => throw new InvalidOperationException(),
        }));
        if (string.IsNullOrWhiteSpace(body))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Die Notiz enthält noch keinen Text, den die KI verarbeiten könnte.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return body;
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

    void UebernehmeKiErgebnis(KiAktion aktion, string text)
    {
        switch (aktion)
        {
            case KiAktion.Zusammenfassen:
                // Zusammenfassung als neuen Block an den Anfang
                var kopf = new TextBlockVm { Text = $"## Zusammenfassung\n\n{text}" };
                RegistriereVm(kopf);
                _bloecke.Insert(0, kopf);
                break;

            case KiAktion.Aufbereiten:
                if (!_bloecke.OfType<InkBlockVm>().Any())
                {
                    // Reine Textnotiz: kompletten Text ersetzen
                    foreach (var alt in _bloecke.OfType<TextBlockVm>().Skip(1).ToList())
                        _bloecke.Remove(alt);
                    _bloecke.OfType<TextBlockVm>().First().Text = text;
                }
                else
                {
                    // Mit Tinte: Original (inkl. Skizzen) behalten, Aufbereitung anhängen
                    var neu = new TextBlockVm { Text = $"## Aufbereitet\n\n{text}" };
                    RegistriereVm(neu);
                    _bloecke.Add(neu);
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
                var aufgaben = new TextBlockVm { Text = $"## Aufgaben\n\n{text}" };
                RegistriereVm(aufgaben);
                _bloecke.Add(aufgaben);
                break;
        }
        MeldeAenderung();
    }

    // ---------- Handschrifterkennung ----------

    void Ink_StrokesGeaendert(InkBlockVm vm)
    {
        if (_laden || _konvertiere) return;
        _erkennungPending.Add(vm);
        // Debounce: Timer bei jedem Strich neu starten
        _erkennungTimer.Stop();
        _erkennungTimer.Start();
    }

    async void ErkennungTimer_Tick(object? sender, EventArgs e)
    {
        _erkennungTimer.Stop();
        if (Erkennung is null) { _erkennungPending.Clear(); return; }

        var faellig = _erkennungPending.ToList();
        _erkennungPending.Clear();
        bool umwandeln = ErkennungToggle.IsChecked == true;

        foreach (var vm in faellig)
        {
            if (!_bloecke.Contains(vm)) continue;
            var text = await Erkennung.ErkenneAsync(vm.Strokes);
            // Während des await kann weitergeschrieben worden sein → dann neu anstoßen
            if (_erkennungPending.Contains(vm)) continue;
            if (text is null) continue;

            if (umwandeln)
                KonvertiereZuText(vm, text);
            else if (vm.ErkannterText != text)
            {
                vm.ErkannterText = text; // Hintergrunderkennung für Suche/KI
                MeldeAenderung();
            }
        }
    }

    /// <summary>Toggle „Handschrift → Text": erkannten Text in den vorhergehenden
    /// Text-Block übernehmen und die Striche entfernen.</summary>
    void KonvertiereZuText(InkBlockVm ink, string text)
    {
        int i = _bloecke.IndexOf(ink);
        if (i < 0) return;

        TextBlockVm? ziel = null;
        for (int j = i - 1; j >= 0; j--)
        {
            if (_bloecke[j] is TextBlockVm t) { ziel = t; break; }
        }
        if (ziel is null)
        {
            ziel = new TextBlockVm();
            RegistriereVm(ziel);
            _bloecke.Insert(i, ziel);
        }

        var vorhanden = ziel.Text.TrimEnd();
        ziel.Text = vorhanden.Length == 0 ? text : vorhanden + "\n" + text;

        _konvertiere = true;
        try
        {
            ink.Strokes.Clear();
            ink.ErkannterText = "";
        }
        finally
        {
            _konvertiere = false;
        }
        MeldeAenderung();
    }
}
