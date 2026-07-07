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
    /// <summary>„Vorlagen verwalten…" im Vorlagen-Menü (Host öffnet die Einstellungen).</summary>
    public event Action? VorlagenVerwaltenAngefordert;

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

    // „Stift zeichnet, Maus tippt": Gibt es einen Digitizer-Stift, zeichnet die Fläche
    // nur, solange er in Reichweite ist — Mausklicks bearbeiten währenddessen Text.
    // Ohne Stift-Hardware bleibt alles beim Alten (Maus zeichnet im Stift-Modus).
    bool _stiftGesehen; // je ein Stylus-Kontakt genügt
    bool _stiftNah;     // Stift schwebt über der Fläche / berührt sie

    public NoteEditor()
    {
        InitializeComponent();
        _initialisiert = true;

        AktualisiereTypBox();

        _erkennungTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.3),
        };
        _erkennungTimer.Tick += ErkennungTimer_Tick;

        _strokes.StrokesChanged += Strokes_Changed;
        Flaeche.Strokes = _strokes;
        Flaeche.StylusInRange += (_, _) => { _stiftGesehen = true; _stiftNah = true; WendeWerkzeugAn(); };
        Flaeche.StylusOutOfRange += (_, _) => { _stiftNah = false; WendeWerkzeugAn(); };
        // Seitentaste des Stifts = Radierer (Zustand wird beim Schweben abgefragt,
        // damit der Modus schon stimmt, wenn die Spitze aufsetzt)
        Flaeche.StylusInAirMove += StiftKnopf_Modus;
        // Umgedrehter Stift (Radierer-Ende, z.B. Surface Pen) radiert ganze Striche
        Flaeche.EditingModeInverted = InkCanvasEditingMode.EraseByStroke;
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
        BrecheFormAb();
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
            AktualisiereTypBox(); // eigene Vorlagen können sich geändert haben
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
                    LinkElement l => new LinkElementVm(l),
                    TabelleElement tab => new TabelleElementVm(tab),
                    _ => throw new InvalidOperationException(),
                };
                if (vm is BildElementVm bild)
                    bild.LadeBild(NotizOrdner);
                if (vm is DateiElementVm datei)
                    _ = datei.LadeVorschauAsync(NotizOrdner);
                if (vm is LinkElementVm link)
                    link.LadeVorschau(NotizOrdner);
                if (vm is TabelleElementVm tabelle)
                    tabelle.SetzeOrdner(NotizOrdner);
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
                if (FindeKind<TextBox>(host) is not { } box) return;
                box.Focus();
                // Per Klick angelegte Felder wieder wegräumen, wenn nichts getippt wurde
                box.LostKeyboardFocus += (_, _) =>
                {
                    if (vm is TextElementVm { Text.Length: 0 } && _elemente.Contains(vm))
                        EntferneElement(vm);
                };
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
        if (_note is null) return;

        // Form-Werkzeug aktiv: Aufziehen beginnen (Maus wie Stift)
        if (FormToggle.IsChecked == true && _formTyp is not null &&
            !IstInElement(e.OriginalSource))
        {
            StarteForm(e.GetPosition(Flaeche));
            e.Handled = true;
            return;
        }

        // Klick auf freie Fläche = Textfeld zum Lostippen.
        // Maus: Einfachklick reicht. Stift/Finger (StylusDevice gesetzt): Doppeltipp,
        // damit ein einzelner Tipp im Auswahl-Modus nicht ständig Felder anlegt.
        if (AktuellerModus() != InkCanvasEditingMode.None) return;
        if (IstInElement(e.OriginalSource)) return;
        if (e.StylusDevice is not null && e.ClickCount != 2) return;

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

    // ---------- Tabellen ----------

    void TabelleEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;
        var vm = new TabelleElementVm { X = 0, Y = NaechsteFreieY(), Breite = 620 };
        vm.FuelleStandard(zeilen: 3, spalten: 3);
        vm.SetzeOrdner(NotizOrdner);
        FuegeElementHinzu(vm);
        PasseHoeheAn();
        MeldeAenderung();
    }

    void Tabelle_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VmVon<TabelleElementVm>(sender) is { } vm)
        {
            vm.AnzeigeHoehe = e.NewSize.Height;
            PasseHoeheAn();
        }
    }

    void TabelleBreite_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Rechte Tabellenkante: alle Spalten proportional skalieren
        VmVon<TabelleElementVm>(sender)?.SkaliereBreite(e.HorizontalChange);
    }

    void TabelleSpalteBreite_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ZelleVm zelle)
            zelle.ZieheSpaltenBreite(e.HorizontalChange);
    }

    void TabelleZeileHoehe_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabellenZeileVm zeile)
            zeile.ZieheHoehe(e.VerticalChange);
    }

    /// <summary>Tab springt zur nächsten Zelle (Umschalt+Tab zurück); in der
    /// letzten Zelle legt Tab eine neue Zeile an.</summary>
    void TabellenZelle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        var box = (TextBox)sender;

        // Host-ContentControl der Tabelle im Visual-Baum suchen
        DependencyObject? d = box;
        ContentControl? host = null;
        while (d is not null && d != Flaeche)
        {
            if (d is ContentControl { Content: TabelleElementVm } cc) { host = cc; break; }
            d = VisualTreeHelper.GetParent(d);
        }
        if (host?.Content is not TabelleElementVm vm) return;

        var boxen = new List<TextBox>();
        SammleKinder(host, boxen);
        int i = boxen.IndexOf(box);
        if (i < 0) return;
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (i > 0) FokusZelle(boxen[i - 1]);
            return;
        }
        if (i + 1 < boxen.Count)
        {
            FokusZelle(boxen[i + 1]);
            return;
        }
        // Letzte Zelle → neue Zeile, deren erste Zelle fokussieren
        int vorher = boxen.Count;
        vm.FuegeZeileHinzu();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var neu = new List<TextBox>();
            SammleKinder(host, neu);
            if (neu.Count > vorher) FokusZelle(neu[vorher]);
        });
    }

    static void FokusZelle(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    static void SammleKinder<T>(DependencyObject wurzel, List<T> ziel) where T : class
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(wurzel); i++)
        {
            var kind = VisualTreeHelper.GetChild(wurzel, i);
            if (kind is T passt) ziel.Add(passt);
            SammleKinder(kind, ziel);
        }
    }

    static ZelleVm? ZelleVonMenu(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement fe
            ? fe.DataContext as ZelleVm
            : null;

    void TabellenZellBild_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null || ZelleVonMenu(sender) is not { } zelle) return;
        var dialog = new OpenFileDialog
        {
            Title = "Bild für die Zelle wählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        zelle.Text = $"![]({KopiereAnhang(dialog.FileName)})";
        MeldeAenderung();
    }

    void TabellenZellBildWeg_Click(object sender, RoutedEventArgs e)
    {
        if (ZelleVonMenu(sender) is { HatBild: true } zelle)
        {
            zelle.Text = "";
            MeldeAenderung();
        }
    }

    void TabelleZeile_Click(object sender, RoutedEventArgs e) =>
        VmVon<TabelleElementVm>(sender)?.FuegeZeileHinzu();

    void TabelleSpalte_Click(object sender, RoutedEventArgs e) =>
        VmVon<TabelleElementVm>(sender)?.FuegeSpalteHinzu();

    void TabelleZeileWeg_Click(object sender, RoutedEventArgs e) =>
        VmVon<TabelleElementVm>(sender)?.EntferneLetzteZeile();

    void TabelleSpalteWeg_Click(object sender, RoutedEventArgs e) =>
        VmVon<TabelleElementVm>(sender)?.EntferneLetzteSpalte();

    /// <summary>PDF-Karte: eine Seite vor/zurück blättern.</summary>
    async void PdfSeiteVor_Click(object sender, RoutedEventArgs e) => await PdfBlaettere(sender, +1);
    async void PdfSeiteZurueck_Click(object sender, RoutedEventArgs e) => await PdfBlaettere(sender, -1);

    Task PdfBlaettere(object sender, int delta) =>
        _note is not null && VmVon<DateiElementVm>(sender) is { } vm
            ? vm.BlaettereAsync(NotizOrdner, delta)
            : Task.CompletedTask;

    /// <summary>Steckt der Klick in einem Button oder der Blätter-Leiste? Dann nicht die Datei öffnen.</summary>
    static bool IstInButton(object? quelle)
    {
        var d = quelle as DependencyObject;
        while (d is not null)
        {
            if (d is Button || d is Border { Name: "SeitenNav" }) return true;
            if (d is ContentControl cc && cc.Content is ElementVm) return false;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void DateiElement_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _note is null) return;
        if (IstInButton(e.OriginalSource)) return; // Doppelklick auf ‹/›/🗑 öffnet nicht die Datei
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

    // ---------- Vorlagen ----------

    void AktualisiereTypBox() =>
        TypBox.ItemsSource = Templates.Alle
            .Select(v => new ComboBoxItem { Content = $"{v.Icon} {v.Name}", Tag = v.Key })
            .ToList();

    void Vorlage_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null) return;

        // Menü bei jedem Öffnen neu aufbauen — eigene Vorlagen können sich ändern
        var menu = new ContextMenu();
        foreach (var v in Templates.Alle.Where(v => v.Key != "leer"))
        {
            var item = new MenuItem { Header = $"{v.Icon} {v.Name}" };
            var vorlage = v;
            item.Click += (_, _) => VorlageEinfuegen(vorlage);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var verwalten = new MenuItem { Header = "⚙ Vorlagen verwalten…" };
        verwalten.Click += (_, _) => VorlagenVerwaltenAngefordert?.Invoke();
        menu.Items.Add(verwalten);

        menu.PlacementTarget = VorlageButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Vorlage in die offene Notiz einfügen: Body als Textfeld (füllt ein
    /// vorhandenes leeres Feld), Titelvorschlag nur wenn der Titel noch leer ist.</summary>
    void VorlageEinfuegen(Vorlage v)
    {
        if (_note is null) return;

        if (TitelBox.Text.Trim().Length == 0 && v.TitelVorschlag.Length > 0)
            TitelBox.Text = v.TitelVorschlag;

        var body = v.Body.TrimEnd();
        if (body.Length > 0)
        {
            var teile = Frontmatter.ElementeAusVorlage(body);
            var leeres = _elemente.OfType<TextElementVm>()
                .FirstOrDefault(t => t.Text.Trim().Length == 0);

            if (teile is [TextElement einzelText] && leeres is not null)
            {
                // Reiner Text und ein leeres Feld wartet: direkt hineinschreiben
                leeres.Text = einzelText.Text;
                leeres.Breite = Math.Max(leeres.Breite, 620);
                if (_hosts.TryGetValue(leeres, out var host) &&
                    FindeKind<TextBox>(host) is { } box)
                {
                    box.Focus();
                }
            }
            else
            {
                // Gemischte Vorlage (Text + Tabellen) unterhalb des Bestehenden stapeln
                double versatz = NaechsteFreieY() - 8;
                foreach (var el in teile)
                {
                    el.Y += versatz;
                    ElementVm vm = el switch
                    {
                        TabelleElement tab => new TabelleElementVm(tab),
                        TextElement t => new TextElementVm(t),
                        _ => throw new InvalidOperationException(),
                    };
                    if (vm is TabelleElementVm tabVm) tabVm.SetzeOrdner(NotizOrdner);
                    FuegeElementHinzu(vm);
                }
            }
        }
        PasseHoeheAn();
        MeldeAenderung();
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

    /// <summary>KI-Body der aktuellen Notiz; null wenn leer/keine Notiz.
    /// Datenschutz-Grundsatz: NUR Textelemente + erkannte Handschrift — der
    /// komplette Frontmatter-Kopf inklusive Titel bleibt lokal (Titel kann
    /// Kundennamen enthalten, z.B. aus der Schnellerfassung).</summary>
    public string? KiBody()
    {
        if (_note is null) return null;
        var body = KiService.ErzeugeKiBody(
            _elemente.Where(e => e is TextElementVm or TabelleElementVm)
                .OrderBy(e => e.Y).ThenBy(e => e.X)
                .Select(e => e switch
                {
                    TextElementVm t => t.Text,
                    TabelleElementVm tab => tab.AlsMarkdown(),
                    _ => "",
                }),
            _tintenText);
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    /// <summary>Volle Pfade aller Anhänge der Notiz (Bilder + Dateien auf der Fläche).</summary>
    public List<string> AnhangPfade()
    {
        if (_note is null) return new();
        return _elemente
            .Select(vm => vm switch
            {
                BildElementVm b => b.Datei,
                DateiElementVm d => d.Datei,
                _ => null,
            })
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => System.IO.Path.Combine(NotizOrdner, d!))
            .Where(System.IO.File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Anhänge nur, wenn der Schalter im KI-Menü aktiv ist (Opt-in).</summary>
    List<string>? AktuelleAnhaenge() =>
        AnhaengeMenuItem.IsChecked && AnhangPfade() is { Count: > 0 } liste ? liste : null;

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
                     RadiererToggle, PunktRadiererToggle, LassoToggle, FormToggle })
        {
            if (t != sender) t.IsChecked = false;
        }
        if (!Equals(sender, FormToggle)) BrecheFormAb();
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
        ? (Farbschema.IstDunkel() ? Colors.White : Colors.Black)
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

    InkCanvasEditingMode AktuellerModus()
    {
        if (RadiererToggle.IsChecked == true) return InkCanvasEditingMode.EraseByStroke;
        if (PunktRadiererToggle.IsChecked == true) return InkCanvasEditingMode.EraseByPoint;
        if (LassoToggle.IsChecked == true) return InkCanvasEditingMode.Select;
        if (FormToggle.IsChecked == true) return InkCanvasEditingMode.None; // Formen ziehen wir selbst
        if (StiftToggle.IsChecked == true || MarkerToggle.IsChecked == true)
        {
            // Stift-Hardware vorhanden, aber gerade nicht in Reichweite:
            // Maus soll tippen/auswählen statt zeichnen
            return _stiftGesehen && !_stiftNah
                ? InkCanvasEditingMode.None
                : InkCanvasEditingMode.Ink;
        }
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

        // Form-Werkzeug: Fadenkreuz statt Pfeil
        Flaeche.UseCustomCursor = FormToggle?.IsChecked == true;
        if (Flaeche.UseCustomCursor) Flaeche.Cursor = Cursors.Cross;

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

    /// <summary>Gedrückte Stift-Seitentaste schaltet bei aktivem Stift/Marker auf
    /// „ganze Striche radieren" um; Loslassen kehrt zum Zeichnen zurück.</summary>
    void StiftKnopf_Modus(object sender, System.Windows.Input.StylusEventArgs e)
    {
        if (StiftToggle.IsChecked != true && MarkerToggle.IsChecked != true) return;

        bool radieren = e.StylusDevice.Inverted;
        foreach (System.Windows.Input.StylusButton knopf in e.StylusDevice.StylusButtons)
        {
            if (knopf.Name.Contains("Barrel", StringComparison.OrdinalIgnoreCase) &&
                knopf.StylusButtonState == System.Windows.Input.StylusButtonState.Down)
            {
                radieren = true;
            }
        }

        var ziel = radieren ? InkCanvasEditingMode.EraseByStroke : AktuellerModus();
        if (Flaeche.EditingMode != ziel) Flaeche.EditingMode = ziel;
    }

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
        if (_note is null) return;
        var punkt = e.GetPosition(Flaeche);

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            double y = punkt.Y;
            foreach (var datei in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (!System.IO.File.Exists(datei)) continue;
                FuegeDateiObjektAn(KopiereAnhang(datei), Math.Max(0, punkt.X), Math.Max(0, y));
                y += 40; // mehrere Dateien leicht versetzt stapeln
            }
            MeldeAenderung();
            return;
        }

        // Kein FileDrop: Link aus dem Browser (Text oder URL-Format) → Web-Clip-Karte
        var text = HoleDropText(e.Data)?.Trim();
        if (text is null) return;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var vm = new LinkElementVm
        {
            Url = text,
            X = Math.Max(0, punkt.X),
            Y = Math.Max(0, punkt.Y),
            Breite = 320,
        };
        vm.Titel = vm.Domain.Length > 0 ? vm.Domain : text; // bis der echte Titel geladen ist
        FuegeElementHinzu(vm);
        PasseHoeheAn();
        MeldeAenderung();
        _ = LadeLinkDatenAsync(vm);
    }

    /// <summary>Titel und Seiten-Vorschau einer Link-Karte nachladen (wirft nie).</summary>
    async Task LadeLinkDatenAsync(LinkElementVm vm)
    {
        await LadeLinkTitelAsync(vm);
        await ErzeugeLinkVorschauAsync(vm);
    }

    /// <summary>Text/URL aus einem DataObject holen (Browser liefern beides).</summary>
    static string? HoleDropText(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) &&
                data.GetData(DataFormats.UnicodeText) is string s &&
                s.Trim().Length > 0)
            {
                return s;
            }
            if (data.GetDataPresent("UniformResourceLocatorW") &&
                data.GetData("UniformResourceLocatorW") is System.IO.MemoryStream ms)
            {
                return System.Text.Encoding.Unicode
                    .GetString(ms.ToArray()).TrimEnd('\0');
            }
        }
        catch
        {
            // fremde Drop-Quellen können sich beliebig verhalten → kein Clip
        }
        return null;
    }

    /// <summary>Ein Client für alle Titel-Abfragen (kurzes Timeout, eigener User-Agent).</summary>
    static readonly System.Net.Http.HttpClient LinkHttp = ErzeugeLinkHttp();

    static System.Net.Http.HttpClient ErzeugeLinkHttp()
    {
        var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NotizApp/1.0");
        return client;
    }

    /// <summary>Seitentitel der Link-Karte asynchron nachladen (Fehler → Domain bleibt).</summary>
    async Task LadeLinkTitelAsync(LinkElementVm vm)
    {
        try
        {
            var html = await LinkHttp.GetStringAsync(vm.Url);
            var m = System.Text.RegularExpressions.Regex.Match(html,
                @"<title[^>]*>\s*(.*?)\s*</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return;
            var titel = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            titel = System.Text.RegularExpressions.Regex.Replace(titel, @"\s+", " ").Trim();
            if (titel.Length > 200) titel = titel[..200];
            if (titel.Length > 0) vm.Titel = titel;
        }
        catch
        {
            // Seite nicht erreichbar/kein HTML → Domain bleibt als Titel stehen
        }
        MeldeAenderung();
    }

    /// <summary>Doppelklick auf die Link-Karte: Seite im Standard-Browser öffnen.</summary>
    void LinkElement_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _note is null) return;
        if (IstInButton(e.OriginalSource)) return; // Doppelklick auf 🗑/⬇ PDF öffnet nicht
        if (VmVon<LinkElementVm>(sender) is not { } vm) return;
        var url = vm.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return; // nur echte Web-Adressen öffnen
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Der Link konnte nicht im Browser geöffnet werden.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    void LinkGroesse_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (VmVon<LinkElementVm>(sender) is not { } vm) return;
        vm.Breite += e.HorizontalChange;
        vm.Hoehe += e.VerticalChange;
        PasseHoeheAn();
    }

    // ---------- Link-Vorschau: Scrollen im Rahmen + Füllen/Einpassen ----------

    /// <summary>Scroll-Position (0..1) als TranslateTransform.Y aufs Vorschaubild
    /// anwenden — geclampt auf den Überstand (Bildhöhe minus Rahmenhöhe).</summary>
    static void WendeLinkScrollAn(Border rahmen, Image bild, LinkElementVm vm)
    {
        if (bild.RenderTransform is not TranslateTransform verschiebung) return;
        double ueberstand = Math.Max(0, bild.ActualHeight - rahmen.ActualHeight);
        verschiebung.Y = vm.VorschauEingepasst
            ? 0
            : -Math.Clamp(vm.VorschauScroll, 0, 1) * ueberstand;
    }

    /// <summary>Vorschaubild + Rahmen einer Link-Karte im Template finden.</summary>
    static (Border Rahmen, Image Bild)? FindeLinkVorschau(DependencyObject wurzel) =>
        FindeKind<Image>(wurzel) is { } bild && bild.Parent is Border rahmen
            ? (rahmen, bild)
            : null;

    /// <summary>Nach jedem Layout (Bild geladen, Karte skaliert) den Scroll nachziehen.</summary>
    void LinkVorschau_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LinkElementVm vm) return;
        var teile = sender switch
        {
            Image bild when bild.Parent is Border rahmen => (rahmen, bild),
            Border rahmen => FindeLinkVorschau(rahmen),
            _ => ((Border, Image)?)null,
        };
        if (teile is { } t) WendeLinkScrollAn(t.Item1, t.Item2, vm);
    }

    /// <summary>Mausrad über der Vorschau: Seite im Rahmen scrollen (nur im
    /// Füllen-Modus und nur, wenn das Bild höher als der Rahmen ist — sonst
    /// scrollt das Rad weiter die Notiz-Fläche).</summary>
    void LinkElement_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (VmVon<LinkElementVm>(sender) is not { } vm) return;
        if (!vm.HatVorschau || vm.VorschauEingepasst) return;
        if (sender is not DependencyObject wurzel) return;
        if (FindeLinkVorschau(wurzel) is not { } teile) return;
        var (rahmen, bild) = teile;

        // Nur wenn der Zeiger wirklich über der Vorschau steht (nicht Titelzeile)
        var pos = e.GetPosition(rahmen);
        if (pos.X < 0 || pos.Y < 0 ||
            pos.X > rahmen.ActualWidth || pos.Y > rahmen.ActualHeight)
        {
            return;
        }

        double ueberstand = bild.ActualHeight - rahmen.ActualHeight;
        if (ueberstand < 1) return; // nichts zu scrollen → Fläche scrollt weiter

        // Eine Rad-Rastung (Delta 120) verschiebt das Bild um ~60 px
        vm.VorschauScroll -= e.Delta / 120.0 * 60.0 / ueberstand;
        WendeLinkScrollAn(rahmen, bild, vm);
        MeldeAenderung();
        e.Handled = true;
    }

    /// <summary>„⤢": zwischen füllendem Ausschnitt (scrollbar) und ganzer
    /// eingepasster Seite (Letterbox) umschalten.</summary>
    void LinkModus_Click(object sender, RoutedEventArgs e)
    {
        if (VmVon<LinkElementVm>(sender) is not { } vm || !vm.HatVorschau) return;
        vm.VorschauEingepasst = !vm.VorschauEingepasst;
        // Transform sofort nachziehen (Layout-Wechsel feuert nicht in jedem Fall)
        if (_hosts.TryGetValue(vm, out var host) &&
            FindeLinkVorschau(host) is { } vorschau)
        {
            WendeLinkScrollAn(vorschau.Rahmen, vorschau.Bild, vm);
        }
    }

    /// <summary>Domain der Karte als dateinamens-tauglicher Baustein ("seite" als Fallback).</summary>
    static string DateiTauglicheDomain(LinkElementVm vm)
    {
        var domain = vm.Domain.Length > 0 ? vm.Domain : "seite";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            domain = domain.Replace(c, '-');
        return domain;
    }

    /// <summary>Edge headless laufen lassen; false bei Timeout (Prozess wird beendet).</summary>
    static async Task<bool> StarteEdgeHeadlessAsync(string edge, string argumente, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(edge, argumente)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var prozess = System.Diagnostics.Process.Start(psi);
        if (prozess is null) return false;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await prozess.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { prozess.Kill(entireProcessTree: true); } catch { }
            return false;
        }
    }

    /// <summary>Seiten-Screenshot headless mit Edge erzeugen, neben die Notiz
    /// kopieren und als Karten-Vorschau übernehmen. Scheitert leise (kein Dialog).</summary>
    async Task ErzeugeLinkVorschauAsync(LinkElementVm vm)
    {
        if (_note is null) return;
        var url = vm.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var edge = FindeEdge();
        if (edge is null) return;

        string? temp = null;
        try
        {
            temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"vorschau-{DateiTauglicheDomain(vm)}.png");
            try { System.IO.File.Delete(temp); } catch { }

            var ok = await StarteEdgeHeadlessAsync(edge,
                $"--headless --disable-gpu --screenshot=\"{temp}\" --window-size=1280,2400 --hide-scrollbars \"{url}\"",
                TimeSpan.FromSeconds(45));
            if (!ok || _note is null || !_elemente.Contains(vm) ||
                !System.IO.File.Exists(temp) || new System.IO.FileInfo(temp).Length == 0)
            {
                return;
            }

            // Alte Vorschau-Datei ersetzen (⟳ lädt neu, es soll kein Datei-Müll bleiben)
            if (vm.VorschauDatei.Length > 0)
            {
                try { System.IO.File.Delete(System.IO.Path.Combine(NotizOrdner, vm.VorschauDatei)); }
                catch { }
            }
            vm.VorschauDatei = KopiereAnhang(temp);
            vm.LadeVorschau(NotizOrdner);
            // Standardgröße vergrößern, sofern der Nutzer sie noch nicht angefasst hat
            if (vm.HatVorschau && Math.Abs(vm.Hoehe - 76) < 0.5)
                vm.Hoehe = 260; // Bild + kompakte Titelzeile
            PasseHoeheAn();
            MeldeAenderung();
        }
        catch
        {
            // ohne Vorschau weiter — die 🔗-Karte funktioniert trotzdem
        }
        finally
        {
            if (temp is not null)
            {
                try { System.IO.File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>„⟳": Titel und Seiten-Vorschau der Link-Karte neu laden
    /// (auch für Links, die vor dem Vorschau-Feature angelegt wurden).</summary>
    async void LinkNeuLaden_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null || sender is not Button knopf) return;
        if (VmVon<LinkElementVm>(sender) is not { } vm) return;
        var alterInhalt = knopf.Content;
        knopf.Content = "⌛";
        knopf.IsEnabled = false;
        try
        {
            await LadeLinkDatenAsync(vm);
        }
        finally
        {
            knopf.Content = alterInhalt;
            knopf.IsEnabled = true;
        }
    }

    /// <summary>msedge.exe suchen: App-Paths-Registry, sonst Standard-Installationspfade.</summary>
    static string? FindeEdge()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key?.GetValue(null) is string pfad && System.IO.File.Exists(pfad))
                return pfad;
        }
        catch
        {
            // kein Registry-Zugriff → Standardpfade probieren
        }
        foreach (var basis in new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
        })
        {
            if (string.IsNullOrWhiteSpace(basis)) continue;
            var pfad = System.IO.Path.Combine(basis, "Microsoft", "Edge", "Application", "msedge.exe");
            if (System.IO.File.Exists(pfad)) return pfad;
        }
        return null;
    }

    /// <summary>„⬇ PDF": Seite headless mit Edge drucken und als Datei-Objekt
    /// neben die Link-Karte legen.</summary>
    async void LinkPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null || sender is not Button knopf) return;
        if (VmVon<LinkElementVm>(sender) is not { } vm) return;
        var url = vm.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var edge = FindeEdge();
        if (edge is null)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Edge wurde nicht gefunden.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var alterInhalt = knopf.Content;
        knopf.Content = "⌛";
        knopf.IsEnabled = false;
        string? temp = null;
        try
        {
            temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"clip-{DateiTauglicheDomain(vm)}.pdf");
            try { System.IO.File.Delete(temp); } catch { }

            await StarteEdgeHeadlessAsync(edge,
                $"--headless --disable-gpu --print-to-pdf=\"{temp}\" --print-to-pdf-no-header \"{url}\"",
                TimeSpan.FromSeconds(60));

            if (_note is not null && System.IO.File.Exists(temp) &&
                new System.IO.FileInfo(temp).Length > 0)
            {
                FuegeDateiObjektAn(KopiereAnhang(temp), vm.X + vm.Breite + 16, vm.Y);
                MeldeAenderung();
            }
            else
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "Die Seite konnte nicht als PDF gesichert werden.",
                    "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "Die Seite konnte nicht als PDF gesichert werden.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (temp is not null)
            {
                try { System.IO.File.Delete(temp); } catch { }
            }
            knopf.Content = alterInhalt;
            knopf.IsEnabled = true;
        }
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
        if (KiBody() is { } body) return body;
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

        var dialog = new KiVorschlagWindow(Ki, aktion, body, AktuelleAnhaenge())
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

        var dialog = new KiVorschlagWindow(Ki, auftrag, body, AktuelleAnhaenge())
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

    // ---------- Formen (Rechteck, Kreis, Dreieck, Linien) ----------

    /// <summary>Markiert Form-Striche in der ISF, damit die Handschrifterkennung
    /// sie nie als Text zu lesen versucht. Überlebt Speichern/Laden.</summary>
    static readonly Guid FormMarker = new("8f6d3b1a-52c4-4e9b-a7d0-4c2b9e5f1a63");

    static bool IstFormStroke(Stroke s) => s.ContainsPropertyData(FormMarker);

    string? _formTyp;
    Point _formStart;
    System.Windows.Shapes.Path? _formVorschau;

    static readonly (string Typ, string Icon, string Name)[] FormKatalog =
    {
        ("rechteck", "▭", "Rechteck"),
        ("quadrat", "□", "Quadrat"),
        ("ellipse", "⬭", "Ellipse"),
        ("kreis", "○", "Kreis"),
        ("dreieck", "△", "Dreieck"),
        ("linie", "─", "Linie"),
        ("linie-strich", "╌", "Linie gestrichelt"),
        ("linie-punkt", "┈", "Linie gepunktet"),
        ("pfeil", "→", "Pfeil"),
        ("doppelpfeil", "↔", "Doppelpfeil"),
    };

    /// <summary>Die zwei Flügelpunkte einer Pfeilspitze bei „zu".</summary>
    static (Point A, Point B) PfeilKopf(Point von, Point zu, double dicke)
    {
        var v = zu - von;
        var l = Math.Max(v.Length, 0.001);
        var u = v / l;
        var n = new Vector(-u.Y, u.X);
        double g = Math.Min(Math.Max(14, dicke * 5), l * 0.5);
        return (zu - u * g + n * (g * 0.55), zu - u * g - n * (g * 0.55));
    }

    void FormToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FormToggle.IsChecked != true)
        {
            // Abgewählt → zurück zum Auswahl-Werkzeug
            BrecheFormAb();
            AuswahlToggle.IsChecked = true;
            return;
        }
        _formTyp ??= FormKatalog[0].Typ;

        var menu = new ContextMenu();
        foreach (var (typ, icon, name) in FormKatalog)
        {
            var item = new MenuItem { Header = $"{icon}  {name}", IsChecked = _formTyp == typ };
            item.Click += (_, _) =>
            {
                _formTyp = typ;
                FormToggle.Content = icon;
                FormToggle.IsChecked = true;
                WendeWerkzeugAn();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = FormToggle;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void StarteForm(Point p)
    {
        _formStart = Begrenze(p);
        _formVorschau = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(AktuelleFarbe()),
            StrokeThickness = Math.Max(1, DickeSlider.Value),
            StrokeDashArray = _formTyp switch
            {
                "linie-strich" => new DoubleCollection { 4, 3 },
                "linie-punkt" => new DoubleCollection { 0.1, 2.6 },
                _ => null,
            },
            StrokeDashCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Data = FormGeometrie(_formTyp!, _formStart, _formStart),
        };
        Flaeche.Children.Add(_formVorschau);
        Flaeche.CaptureMouse();
    }

    void Flaeche_MouseMove(object sender, MouseEventArgs e)
    {
        if (_formVorschau is null) return;
        _formVorschau.Data = FormGeometrie(_formTyp!, _formStart, Begrenze(e.GetPosition(Flaeche)));
    }

    void Flaeche_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_formVorschau is null) return;
        var ende = Begrenze(e.GetPosition(Flaeche));
        BrecheFormAb();

        // Winzige Züge sind Versehen — es entsteht nichts
        var diag = (ende - _formStart).Length;
        if (diag < 5) return;

        _strokes.Add(ErzeugeFormStrokes(_formTyp!, _formStart, ende));
        e.Handled = true;
    }

    void BrecheFormAb()
    {
        if (_formVorschau is not null)
        {
            Flaeche.Children.Remove(_formVorschau);
            _formVorschau = null;
            Flaeche.ReleaseMouseCapture();
        }
    }

    static Point Begrenze(Point p) => new(Math.Max(0, p.X), Math.Max(0, p.Y));

    /// <summary>Quadrat/Kreis: zweiten Eckpunkt auf gleiche Seitenlängen zwingen.</summary>
    static Point ErzwingeSeitengleich(Point a, Point b)
    {
        double s = Math.Min(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + Math.Sign(b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y) * s);
    }

    Geometry FormGeometrie(string typ, Point a, Point b)
    {
        if (typ is "quadrat" or "kreis") b = ErzwingeSeitengleich(a, b);
        var r = new Rect(a, b);
        return typ switch
        {
            "rechteck" or "quadrat" => new RectangleGeometry(r),
            "ellipse" or "kreis" => new EllipseGeometry(r),
            "dreieck" => new PathGeometry(new[]
            {
                new PathFigure(new Point(r.X + r.Width / 2, r.Y), new[]
                {
                    new LineSegment(r.BottomRight, true),
                    new LineSegment(r.BottomLeft, true),
                }, closed: true),
            }),
            "pfeil" or "doppelpfeil" => PfeilGeometrie(typ, a, b),
            _ => new LineGeometry(a, b),
        };
    }

    Geometry PfeilGeometrie(string typ, Point a, Point b)
    {
        double dicke = Math.Max(1, DickeSlider.Value);
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, b));
        var (k1, k2) = PfeilKopf(a, b, dicke);
        g.Children.Add(new LineGeometry(k1, b));
        g.Children.Add(new LineGeometry(k2, b));
        if (typ == "doppelpfeil")
        {
            var (k3, k4) = PfeilKopf(b, a, dicke);
            g.Children.Add(new LineGeometry(k3, a));
            g.Children.Add(new LineGeometry(k4, a));
        }
        return g;
    }

    /// <summary>Form als Tinten-Striche in Stiftfarbe/-dicke — radierbar und
    /// mit dem Lasso verschiebbar wie Handschrift.</summary>
    StrokeCollection ErzeugeFormStrokes(string typ, Point a, Point b)
    {
        var da = new DrawingAttributes
        {
            Color = AktuelleFarbe(),
            Width = Math.Max(1, DickeSlider.Value),
            Height = Math.Max(1, DickeSlider.Value),
            FitToCurve = false, // scharfe Ecken, keine Kurvenglättung
        };
        Stroke Strich(params Point[] punkte)
        {
            var s = new Stroke(new System.Windows.Input.StylusPointCollection(punkte), da);
            s.AddPropertyData(FormMarker, true);
            return s;
        }

        if (typ is "quadrat" or "kreis") b = ErzwingeSeitengleich(a, b);
        var r = new Rect(a, b);
        var sc = new StrokeCollection();
        switch (typ)
        {
            case "rechteck" or "quadrat":
                sc.Add(Strich(r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft, r.TopLeft));
                break;

            case "ellipse" or "kreis":
                var rund = new Point[65];
                for (int i = 0; i <= 64; i++)
                {
                    double w = i * 2 * Math.PI / 64;
                    rund[i] = new Point(r.X + r.Width / 2 * (1 + Math.Cos(w)),
                                        r.Y + r.Height / 2 * (1 + Math.Sin(w)));
                }
                sc.Add(Strich(rund));
                break;

            case "dreieck":
                var spitze = new Point(r.X + r.Width / 2, r.Y);
                sc.Add(Strich(spitze, r.BottomRight, r.BottomLeft, spitze));
                break;

            case "linie":
                sc.Add(Strich(a, b));
                break;

            case "pfeil":
            {
                var (k1, k2) = PfeilKopf(a, b, da.Width);
                // Ein Stroke: Linie + Spitze (mit Rückweg), damit ⌫ den ganzen Pfeil nimmt
                sc.Add(Strich(a, b, k1, b, k2));
                break;
            }

            case "doppelpfeil":
            {
                var (k1, k2) = PfeilKopf(a, b, da.Width);
                var (k3, k4) = PfeilKopf(b, a, da.Width);
                sc.Add(Strich(k3, a, k4, a, b, k1, b, k2));
                break;
            }

            case "linie-strich":
            {
                var v = b - a;
                var laenge = Math.Max(v.Length, 0.001);
                var richtung = v / laenge;
                double strich = Math.Max(10, da.Width * 4), luecke = Math.Max(6, da.Width * 3);
                for (double t = 0; t < laenge; t += strich + luecke)
                    sc.Add(Strich(a + richtung * t, a + richtung * Math.Min(t + strich, laenge)));
                break;
            }

            case "linie-punkt":
            {
                var v = b - a;
                var laenge = Math.Max(v.Length, 0.001);
                var richtung = v / laenge;
                double abstand = Math.Max(7, da.Width * 3);
                for (double t = 0; t <= laenge; t += abstand)
                    sc.Add(Strich(a + richtung * t));
                break;
            }
        }
        return sc;
    }

    // ---------- Handschrifterkennung ----------

    void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_laden || _konvertiere) return;
        foreach (Stroke s in e.Removed)
            _erkennungPending.Remove(s);
        foreach (Stroke s in e.Added)
        {
            // Marker = Hervorhebung, Formen = Geometrie — nie in Text umwandeln
            if (!s.DrawingAttributes.IsHighlighter && !IstFormStroke(s))
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
            _strokes.Where(s => !s.DrawingAttributes.IsHighlighter && !IstFormStroke(s)));
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
            auswahl.Where(s => !s.DrawingAttributes.IsHighlighter && !IstFormStroke(s)));
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
        var auto = Farbschema.IstDunkel() ? Colors.White : Colors.Black;
        return c == auto ? null : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
