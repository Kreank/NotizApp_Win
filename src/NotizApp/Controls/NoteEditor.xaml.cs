using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
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

            foreach (var block in note.Bloecke)
            {
                BlockVm vm = block switch
                {
                    TextBlockContent t => new TextBlockVm(t),
                    InkBlockContent i => new InkBlockVm(i),
                    _ => throw new InvalidOperationException(),
                };
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
        if (sender == DickeBox) { WendeWerkzeugAn(); return; }
        MeldeAenderung();
    }

    void Speichern_Click(object sender, RoutedEventArgs e) => SpeichernAngefordert?.Invoke();

    // ---------- Werkzeuge ----------

    void Werkzeug_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialisiert) return;
        foreach (var t in new[] { StiftToggle, MarkerToggle, RadiererToggle, LassoToggle })
        {
            if (t != sender) t.IsChecked = false;
        }
        WendeWerkzeugAn();
    }

    void Farbe_Click(object sender, RoutedEventArgs e)
    {
        _farbe = (string)((Button)sender).Tag;
        StiftToggle.IsChecked = true; // Farbwahl aktiviert den Stift
        WendeWerkzeugAn();
    }

    DrawingAttributes AktuelleAttribute()
    {
        double dicke = 2.2;
        if ((DickeBox?.SelectedItem as ComboBoxItem)?.Tag is string d)
            dicke = double.Parse(d, System.Globalization.CultureInfo.InvariantCulture);

        if (MarkerToggle.IsChecked == true)
        {
            return new DrawingAttributes
            {
                Color = Color.FromArgb(255, 255, 210, 0),
                IsHighlighter = true,
                Width = dicke * 5,
                Height = dicke * 10,
                StylusTip = StylusTip.Rectangle,
            };
        }

        var farbe = _farbe == "auto"
            ? (IstDunklesDesign() ? Colors.White : Colors.Black)
            : (Color)ColorConverter.ConvertFromString(_farbe);
        return new DrawingAttributes
        {
            Color = farbe,
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
        if (LassoToggle.IsChecked == true) return InkCanvasEditingMode.Select;
        if (StiftToggle.IsChecked == true || MarkerToggle.IsChecked == true)
            return InkCanvasEditingMode.Ink;
        return InkCanvasEditingMode.None;
    }

    void WendeWerkzeugAn()
    {
        var da = AktuelleAttribute();
        var modus = AktuellerModus();
        foreach (var canvas in _canvases)
        {
            canvas.EditingMode = modus;
            canvas.DefaultDrawingAttributes = da.Clone();
        }
    }

    void InkCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        var canvas = (InkCanvas)sender;
        if (!_canvases.Contains(canvas)) _canvases.Add(canvas);
        canvas.EditingMode = AktuellerModus();
        canvas.DefaultDrawingAttributes = AktuelleAttribute();
    }

    void InkCanvas_Unloaded(object sender, RoutedEventArgs e) =>
        _canvases.Remove((InkCanvas)sender);

    // ---------- Blöcke einfügen / löschen / Größe ----------

    void NeueTintenflaeche_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var ink = new InkBlockVm();
        RegistriereVm(ink);
        _bloecke.Add(ink);
        // Danach direkt weiterschreiben können: Text-Block ans Ende
        var text = new TextBlockVm();
        RegistriereVm(text);
        _bloecke.Add(text);
        MeldeAenderung();
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

    // ---------- KI (V2) ----------

    void Ki_Click(object sender, RoutedEventArgs e)
    {
        // Linksklick öffnet das Aktions-Menü unter dem Button
        var menu = KiButton.ContextMenu!;
        menu.PlacementTarget = KiButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void KiAktion_Click(object sender, RoutedEventArgs e)
    {
        if (Ki is null || _note is null) return;
        var aktion = Enum.Parse<KiAktion>((string)((MenuItem)sender).Tag);

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
            return;
        }

        var dialog = new KiVorschlagWindow(Ki, aktion, body)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Ergebnis))
            return;
        UebernehmeKiErgebnis(aktion, dialog.Ergebnis);
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
