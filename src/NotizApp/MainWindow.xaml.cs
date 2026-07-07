using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NotizApp.Models;
using NotizApp.Services;

namespace NotizApp;

/// <summary>Hauptfenster: Sidebar | Notiz-/Aufgabenliste | Editor.</summary>
public partial class MainWindow : Window
{
    readonly NoteStore _store;
    readonly SettingsService _settings;

    Note? _aktuelleNotiz;
    string? _filterNotizbuch;
    string? _filterTag;
    bool _aufgabenAnsicht;
    bool _aktualisiere; // Guard: Listen werden gerade programmatisch befüllt
    readonly bool _initialisiert; // Guard: InitializeComponent feuert bereits Selection-Events

    readonly DispatcherTimer _autosaveTimer;

    /// <summary>Wird von App gesetzt, wenn wirklich beendet wird (Tray-Menü „Beenden").</summary>
    public bool WirklichSchliessen { get; set; }

    public ICommand NeueNotizCommand { get; }
    public ICommand SpeichernCommand { get; }
    public ICommand SuchenCommand { get; }
    public ICommand NeuLadenCommand { get; }
    public ICommand FokusCommand { get; }
    public ICommand SidebarToggleCommand { get; }
    public ICommand ListeToggleCommand { get; }

    public MainWindow(NoteStore store, SettingsService settings, InkRecognitionService erkennung)
    {
        _store = store;
        _settings = settings;

        NeueNotizCommand = new RelayCommand(() => NeueNotiz_Click(this, new RoutedEventArgs()));
        SpeichernCommand = new RelayCommand(SpeichereAktuelle);
        SuchenCommand = new RelayCommand(() => { SuchBox.Focus(); SuchBox.SelectAll(); });
        NeuLadenCommand = new RelayCommand(NeuLaden);
        FokusCommand = new RelayCommand(() => Editor.SetzeFokusToggle(!_fokusAktiv));
        SidebarToggleCommand = new RelayCommand(() => KlappeSidebar(!_sidebarZu));
        ListeToggleCommand = new RelayCommand(() => KlappeListe(!_listeZu));

        InitializeComponent();
        _initialisiert = true;

        // Gemerkten Einklapp-Zustand der Seitenleisten wiederherstellen
        _sidebarZu = settings.Aktuell.SidebarZu;
        _listeZu = settings.Aktuell.ListeZu;
        if (_sidebarZu || _listeZu) WendeSpaltenAn();

        Editor.Erkennung = erkennung;
        Editor.Ki = new KiService();
        Editor.NotizGeaendert += Editor_NotizGeaendert;
        Editor.SpeichernAngefordert += SpeichereAktuelle;
        Editor.FokusUmgeschaltet += SetzeFokusModus;
        Editor.ChatAngefordert += () => SetzeChatSichtbar(!_chatOffen);
        Editor.VorlagenVerwaltenAngefordert += () =>
            Einstellungen_Click(this, new RoutedEventArgs());

        // Dieselbe KiService-Instanz für Chat und Dashboard-Feed (Editor.Ki
        // wurde gerade eben gesetzt — Reihenfolge beachten)
        Dashboard.Ki = Editor.Ki;
        Dashboard.NotizGeklickt += DashboardNotizGeklickt;
        Dashboard.TerminAnlegenAngefordert += DashboardTerminAnlegen;

        ChatPanel.Ki = Editor.Ki;
        ChatPanel.HoleNotizKontext = () => Editor.KiBody();
        ChatPanel.HoleNotizAnhaenge = () => Editor.AnhangPfade();
        ChatPanel.TextEinfuegen += ChatTextEinfuegen;
        ChatPanel.DateiEinfuegen += ChatDateiEinfuegen;
        if (settings.Aktuell.ChatOffen) SetzeChatSichtbar(true);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); SpeichereAktuelle(); };

        BaueNeueNotizMenu();
        NotizListe.ContextMenuOpening += NotizListe_ContextMenuOpening;

        Loaded += (_, _) => StarteGlowAnimation();

        NotizbuchFarben.Setze(settings.Aktuell.NotizbuchFarben);
        AktualisiereSidebar();
        AktualisiereListe();
        Editor.LadeNote(null);
        ZeigeEditorHinweis();

        // Startansicht ist das Dashboard (Index 0, IsSelected in XAML) — das
        // SelectionChanged-Event feuerte während InitializeComponent aber noch
        // gegen den _initialisiert-Guard, daher hier explizit aktivieren
        SetzeDashboardSichtbar(true);
    }

    // ---------- Sidebar ----------

    void AktualisiereSidebar()
    {
        _aktualisiere = true;
        try
        {
            var nbAuswahl = _filterNotizbuch;
            NotizbuchListe.Items.Clear();
            foreach (var nb in _store.Notizbuecher())
            {
                // Anzahl inkl. Unterordner — der Klick auf den Ordner zeigt ja auch den Teilbaum
                var anzahl = _store.Notizen.Count(n => NoteStore.ImTeilbaum(n.Notizbuch, nb));
                var item = new ListBoxItem { Content = NotizbuchEintrag(nb, anzahl), Tag = nb };

                var menu = new ContextMenu();
                var unterordner = new MenuItem { Header = "Unterordner anlegen…" };
                unterordner.Click += (_, _) => NeuerUnterordner(nb);
                menu.Items.Add(unterordner);
                var umbenennen = new MenuItem { Header = "Umbenennen…" };
                umbenennen.Click += (_, _) => UmbenenneNotizbuch(nb);
                menu.Items.Add(umbenennen);
                menu.Items.Add(BaueFarbMenu(nb));
                menu.Items.Add(new Separator());
                var loeschen = new MenuItem { Header = "Löschen" };
                loeschen.Click += (_, _) => LoescheNotizbuch(nb);
                menu.Items.Add(loeschen);
                item.ContextMenu = menu;

                NotizbuchListe.Items.Add(item);
                if (nb == nbAuswahl) item.IsSelected = true;
            }

            var tagAuswahl = _filterTag;
            TagListe.Items.Clear();
            foreach (var (tag, anzahl) in _store.AlleTags())
            {
                var item = new ListBoxItem { Content = $"#{tag}  ({anzahl})", Tag = tag };
                TagListe.Items.Add(item);
                if (string.Equals(tag, tagAuswahl, StringComparison.OrdinalIgnoreCase))
                    item.IsSelected = true;
            }
        }
        finally
        {
            _aktualisiere = false;
        }
    }

    void AnsichtListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ansichten: 0 = Dashboard, 1 = Alle Notizen, 2 = Aufgaben
        if (!_initialisiert || _aktualisiere || AnsichtListe.SelectedIndex < 0) return;
        _aufgabenAnsicht = AnsichtListe.SelectedIndex == 2;
        SetzeDashboardSichtbar(AnsichtListe.SelectedIndex == 0);
        if (AnsichtListe.SelectedIndex == 1)
        {
            // „Alle Notizen" hebt Notizbuch-/Tag-Filter auf
            _aktualisiere = true;
            NotizbuchListe.SelectedIndex = -1;
            TagListe.SelectedIndex = -1;
            _aktualisiere = false;
            _filterNotizbuch = null;
            _filterTag = null;
        }
        AktualisiereListe();
    }

    void NotizbuchListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aktualisiere) return;
        _filterNotizbuch = (NotizbuchListe.SelectedItem as ListBoxItem)?.Tag as string;
        if (_filterNotizbuch is not null)
        {
            _aktualisiere = true;
            AnsichtListe.SelectedIndex = 1; // Alle Notizen
            TagListe.SelectedIndex = -1;
            _aktualisiere = false;
            _aufgabenAnsicht = false;
            SetzeDashboardSichtbar(false);
            _filterTag = null;
        }
        AktualisiereListe();
    }

    void TagListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aktualisiere) return;
        _filterTag = (TagListe.SelectedItem as ListBoxItem)?.Tag as string;
        if (_filterTag is not null)
        {
            _aktualisiere = true;
            AnsichtListe.SelectedIndex = 1; // Alle Notizen
            NotizbuchListe.SelectedIndex = -1;
            _aktualisiere = false;
            _aufgabenAnsicht = false;
            SetzeDashboardSichtbar(false);
            _filterNotizbuch = null;
        }
        AktualisiereListe();
    }

    /// <summary>Ordner-Silhouette (Mappe mit Reiter) für die Sidebar — einfärbbar,
    /// im Gegensatz zum 📁-Farb-Emoji, das seine Farbe nicht ändern kann.</summary>
    static readonly System.Windows.Media.Geometry OrdnerForm =
        System.Windows.Media.Geometry.Parse(
            "M0,2 A2,2 0 0 1 2,0 L5,0 L7,2 L13,2 A2,2 0 0 1 15,4 L15,10 A2,2 0 0 1 13,12 L2,12 A2,2 0 0 1 0,10 Z");

    /// <summary>Sidebar-Eintrag: Ordnersymbol (in der Notizbuch-Farbe, falls gesetzt)
    /// + Name + Anzahl. Unterordner ("Kunden/Meier") werden eingerückt und
    /// zeigen nur ihren letzten Namensteil.</summary>
    static StackPanel NotizbuchEintrag(string nb, int anzahl)
    {
        int tiefe = nb.Count(c => c == '/');
        var anzeigeName = nb[(nb.LastIndexOf('/') + 1)..];

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(tiefe * 16, 0, 0, 0),
        };
        if (NotizbuchFarben.BrushFuer(nb) is { } brush)
        {
            stack.Children.Add(new System.Windows.Shapes.Path
            {
                Data = OrdnerForm,
                Fill = brush,
                Margin = new Thickness(1, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            stack.Children.Add(new TextBlock { Text = "📁", Margin = new Thickness(0, 0, 6, 0) });
        }
        stack.Children.Add(new TextBlock { Text = $"{anzeigeName}  ({anzahl})" });
        return stack;
    }

    static readonly (string Name, string Hex)[] FarbPalette =
    {
        ("Blau", "#3B78D8"), ("Rot", "#D83B3B"), ("Grün", "#3B9E5F"),
        ("Orange", "#E8871E"), ("Gelb", "#E3C71B"), ("Violett", "#8E5BD8"),
        ("Türkis", "#2BAAB4"), ("Pink", "#D85BA6"), ("Braun", "#8B5A3C"),
        ("Grau", "#8A8A8A"),
    };

    MenuItem BaueFarbMenu(string nb)
    {
        var farbMenu = new MenuItem { Header = "Farbe" };
        foreach (var (name, hex) in FarbPalette)
        {
            var eintrag = new MenuItem
            {
                Header = name,
                Icon = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                },
                IsChecked = NotizbuchFarben.Hex(nb) == hex,
            };
            eintrag.Click += (_, _) => SetzeNotizbuchFarbe(nb, hex);
            farbMenu.Items.Add(eintrag);
        }
        farbMenu.Items.Add(new Separator());
        var keine = new MenuItem { Header = "Keine Farbe" };
        keine.Click += (_, _) => SetzeNotizbuchFarbe(nb, null);
        farbMenu.Items.Add(keine);
        return farbMenu;
    }

    void SetzeNotizbuchFarbe(string nb, string? hex)
    {
        if (hex is null)
            _settings.Aktuell.NotizbuchFarben.Remove(nb);
        else
            _settings.Aktuell.NotizbuchFarben[nb] = hex;
        _settings.Speichere();
        NotizbuchFarben.Setze(_settings.Aktuell.NotizbuchFarben);
        AktualisiereSidebar();
        AktualisiereListe(); // Farbbalken der Notizliste
    }

    void NeuesNotizbuch_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Frage(this, "Neues Notizbuch", "Name des Notizbuchs:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.NeuesNotizbuch(name);
        AktualisiereSidebar();
    }

    void NeuerUnterordner(string eltern)
    {
        var name = TextPromptWindow.Frage(this, "Unterordner anlegen",
            $"Name des Unterordners in „{eltern}“:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.NeuesNotizbuch(name, eltern);
        AktualisiereSidebar();
    }

    void UmbenenneNotizbuch(string nb)
    {
        var alterName = nb[(nb.LastIndexOf('/') + 1)..];
        var neu = TextPromptWindow.Frage(this, "Notizbuch umbenennen",
            $"Neuer Name für „{nb}“:", vorgabe: alterName);
        if (string.IsNullOrWhiteSpace(neu) || neu == alterName) return;

        SpeichereAktuelle(); // offene Änderungen sichern, bevor Pfade wechseln
        var ergebnis = _store.NotizbuchUmbenennen(nb, neu);
        if (ergebnis is null)
        {
            MessageBox.Show(this, $"Es gibt bereits ein Notizbuch „{neu}“.",
                "Umbenennen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Filter, Schnellnotiz-Ziel und Farben im ganzen Teilbaum nachziehen
        string Umziehen(string pfad) => ergebnis + pfad[nb.Length..];
        if (_filterNotizbuch is not null && NoteStore.ImTeilbaum(_filterNotizbuch, nb))
            _filterNotizbuch = Umziehen(_filterNotizbuch);
        if (NoteStore.ImTeilbaum(_settings.Aktuell.QuickNotebook, nb))
            _settings.Aktuell.QuickNotebook = Umziehen(_settings.Aktuell.QuickNotebook);
        var farben = _settings.Aktuell.NotizbuchFarben;
        foreach (var key in farben.Keys.Where(k => NoteStore.ImTeilbaum(k, nb)).ToList())
        {
            var farbe = farben[key];
            farben.Remove(key);
            farben[Umziehen(key)] = farbe;
        }
        NotizbuchFarben.Setze(farben);
        _settings.Speichere();
        AktualisiereSidebar();
        AktualisiereListe();
    }

    void LoescheNotizbuch(string nb)
    {
        var anzahl = _store.Notizen.Count(n => NoteStore.ImTeilbaum(n.Notizbuch, nb));
        var hatUnterordner = _store.Notizbuecher()
            .Any(x => x != nb && NoteStore.ImTeilbaum(x, nb));
        var antwort = MessageBox.Show(this,
            anzahl == 0 && !hatUnterordner
                ? $"Leeres Notizbuch „{nb}“ löschen?"
                : $"Notizbuch „{nb}“ {(hatUnterordner ? "samt Unterordnern " : "")}mit {anzahl} Notiz(en) endgültig löschen?\nDas kann nicht rückgängig gemacht werden.",
            "Notizbuch löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (antwort != MessageBoxResult.Yes) return;

        if (_aktuelleNotiz is not null && NoteStore.ImTeilbaum(_aktuelleNotiz.Notizbuch, nb))
        {
            _autosaveTimer.Stop();
            _aktuelleNotiz = null;
            Editor.LadeNote(null);
            ZeigeEditorHinweis();
        }
        _store.NotizbuchLoeschen(nb);
        if (_filterNotizbuch is not null && NoteStore.ImTeilbaum(_filterNotizbuch, nb))
            _filterNotizbuch = null;
        var geloescht = _settings.Aktuell.NotizbuchFarben.Keys
            .Where(k => NoteStore.ImTeilbaum(k, nb)).ToList();
        if (geloescht.Count > 0)
        {
            foreach (var k in geloescht) _settings.Aktuell.NotizbuchFarben.Remove(k);
            _settings.Speichere();
            NotizbuchFarben.Setze(_settings.Aktuell.NotizbuchFarben);
        }
        AktualisiereSidebar();
        AktualisiereListe();
    }

    void Einstellungen_Click(object sender, RoutedEventArgs e)
    {
        var fenster = new SettingsWindow(_settings) { Owner = this };
        fenster.ShowDialog();
    }

    // ---------- Notiz-/Aufgabenliste ----------

    void AktualisiereListe()
    {
        _aktualisiere = true;
        try
        {
            if (_aufgabenAnsicht)
            {
                NotizListe.Visibility = Visibility.Collapsed;
                AufgabenListe.Visibility = Visibility.Visible;
                var aufgaben = TaskService.Sammle(_store.Notizen);
                AufgabenListe.ItemsSource = aufgaben;
                ListeLeerHinweis.Text = aufgaben.Count == 0
                    ? "Keine Aufgaben. Checkboxen in Notizen: - [ ] Text @2026-07-08"
                    : $"{aufgaben.Count(a => !a.Erledigt)} offen · {aufgaben.Count(a => a.Erledigt)} erledigt";
                return;
            }

            NotizListe.Visibility = Visibility.Visible;
            AufgabenListe.Visibility = Visibility.Collapsed;

            IEnumerable<Note> menge = _store.Notizen;
            if (_filterNotizbuch is not null) // inkl. Unterordner
                menge = menge.Where(n => NoteStore.ImTeilbaum(n.Notizbuch, _filterNotizbuch));
            if (_filterTag is not null)
                menge = menge.Where(n => n.Meta.Tags.Contains(_filterTag, StringComparer.OrdinalIgnoreCase));

            var suche = SuchBox.Text.Trim().ToLowerInvariant();
            if (suche.Length > 0)
            {
                var woerter = suche.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                menge = menge.Where(n => woerter.All(w => n.VolltextCache.Contains(w)));
            }

            var liste = menge.OrderByDescending(n => n.Meta.Geaendert).ToList();
            NotizListe.ItemsSource = liste;
            if (_aktuelleNotiz is not null && liste.Contains(_aktuelleNotiz))
                NotizListe.SelectedItem = _aktuelleNotiz;

            ListeLeerHinweis.Text = liste.Count == 0
                ? (suche.Length > 0 ? "Keine Treffer." : "Noch keine Notizen hier.")
                : $"{liste.Count} Notiz(en)";
        }
        finally
        {
            _aktualisiere = false;
        }
    }

    void SuchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialisiert) return;
        if (_aufgabenAnsicht || _dashboardAnsicht)
        {
            // Tippen in der Suche wechselt zurück zur Notizansicht
            _aktualisiere = true;
            AnsichtListe.SelectedIndex = 1; // Alle Notizen
            _aktualisiere = false;
            _aufgabenAnsicht = false;
            SetzeDashboardSichtbar(false);
        }
        AktualisiereListe();
    }

    void NotizListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aktualisiere) return;
        if (NotizListe.SelectedItem is Note note && note != _aktuelleNotiz)
            OeffneNotiz(note);
    }

    void NotizListe_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (NotizListe.SelectedItem is not Note note)
        {
            e.Handled = true;
            return;
        }
        VerschiebenMenu.Items.Clear();
        foreach (var nb in _store.Notizbuecher())
        {
            // Voller Pfad, Unterordner eingerückt — im flachen Menü sonst nicht zu unterscheiden
            int tiefe = nb.Count(c => c == '/');
            var item = new MenuItem
            {
                Header = new string(' ', tiefe * 3) + (tiefe > 0 ? "↳ " : "") + nb[(nb.LastIndexOf('/') + 1)..],
                IsEnabled = nb != note.Notizbuch,
                ToolTip = nb,
            };
            item.Click += (_, _) =>
            {
                _store.Verschiebe(note, nb);
                AktualisiereSidebar();
                AktualisiereListe();
            };
            VerschiebenMenu.Items.Add(item);
        }
    }

    void NotizListe_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        NotizLoeschen_Click(sender, e);
        e.Handled = true;
    }

    void NotizLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if (NotizListe.SelectedItem is not Note note) return;
        var antwort = MessageBox.Show(this,
            $"„{note.AnzeigeTitel}“ endgültig löschen?",
            "Notiz löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (antwort != MessageBoxResult.Yes) return;

        if (note == _aktuelleNotiz)
        {
            _autosaveTimer.Stop();
            _aktuelleNotiz = null;
            Editor.LadeNote(null);
            ZeigeEditorHinweis();
        }
        _store.Loesche(note);
        AktualisiereSidebar();
        AktualisiereListe();
    }

    // ---------- Aufgaben ----------

    void Aufgabe_Umschalten(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TaskItem item) return;
        // Offene Änderungen der aktuellen Notiz zuerst sichern (könnte dieselbe sein)
        SpeichereAktuelle();
        TaskService.Umschalten(item, _store);
        if (item.Note == _aktuelleNotiz)
            Editor.LadeNote(_aktuelleNotiz); // Editor-Inhalt neu laden
        AktualisiereListe();
    }

    void AufgabenListe_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AufgabenListe.SelectedItem is not TaskItem item) return;
        _aktualisiere = true;
        AnsichtListe.SelectedIndex = 1; // Alle Notizen
        _aktualisiere = false;
        _aufgabenAnsicht = false;
        _filterNotizbuch = null;
        _filterTag = null;
        AktualisiereListe();
        NotizListe.SelectedItem = item.Note;
        OeffneNotiz(item.Note);
    }

    // ---------- Editor / Speichern ----------

    public void OeffneNotiz(Note note)
    {
        SpeichereAktuelle();
        _aktuelleNotiz = note;
        _store.LadeTinte(note);
        Editor.LadeNote(note);
        ZeigeEditorHinweis();
    }

    void Editor_NotizGeaendert()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    void SpeichereAktuelle()
    {
        _autosaveTimer.Stop();
        if (_aktuelleNotiz is null || !Editor.HatNote) return;
        Editor.UebernehmeInNote();
        _store.Speichere(_aktuelleNotiz);
        AktualisiereSidebar(); // Tag-/Zähler-Änderungen

        // Anhang-Suchindex (PDF-Text + Bild-OCR) der Notiz im Hintergrund nachziehen
        var notiz = _aktuelleNotiz;
        _ = Task.Run(async () =>
        {
            try
            {
                await AnhangIndexService.Instanz.IndiziereAsync(
                    new[] { notiz }, CancellationToken.None);
            }
            catch
            {
                // Suche funktioniert auch ohne Anhang-Index
            }
        });
    }

    void ZeigeEditorHinweis() =>
        // Im Dashboard keinen „Keine Notiz"-Hinweis über die Ansicht legen
        EditorLeerHinweis.Visibility =
            _aktuelleNotiz is null && !_dashboardAnsicht
                ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Dashboard ----------

    bool _dashboardAnsicht;

    /// <summary>Dashboard ein-/ausblenden (Editor samt Leer-Hinweis dabei
    /// verstecken/zeigen); beim Einblenden Termine und Feed aktualisieren.
    /// Chat-Bubble und Chat-Panel bleiben unangetastet.</summary>
    void SetzeDashboardSichtbar(bool an)
    {
        if (an == _dashboardAnsicht) return;
        _dashboardAnsicht = an;
        Dashboard.Visibility = an ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = an ? Visibility.Collapsed : Visibility.Visible;
        ZeigeEditorHinweis();
        if (an)
        {
            SpeichereAktuelle(); // damit der Kalender frische Fälligkeiten sieht
            Dashboard.Aktualisiere(TaskService.Sammle(_store.Notizen));
            Dashboard.LadeFeedWennAlt();
        }
    }

    /// <summary>Termin-Klick im Dashboard: zur Notizansicht wechseln und die Quell-Notiz öffnen.</summary>
    void DashboardNotizGeklickt(Note note)
    {
        _aktualisiere = true;
        AnsichtListe.SelectedIndex = 1; // Alle Notizen
        _aktualisiere = false;
        _aufgabenAnsicht = false;
        SetzeDashboardSichtbar(false);
        _filterNotizbuch = null;
        _filterTag = null;
        AktualisiereListe();
        NotizListe.SelectedItem = note;
        OeffneNotiz(note);
    }

    /// <summary>Titel der festen Kalender-Notiz, in der Dashboard-Termine landen.</summary>
    const string KalenderNotizTitel = "📅 Kalender";

    /// <summary>
    /// „＋ Termin" bzw. Doppelklick auf einen Kalendertag im Dashboard:
    /// Termin als normale Aufgabenzeile "- [ ] Text @JJJJ-MM-TT" in die
    /// Kalender-Notiz schreiben — so erscheint er im Kalender, in der
    /// Terminliste UND in der Aufgaben-Ansicht und ist durchsuchbar.
    /// </summary>
    void DashboardTerminAnlegen(DateTime datum)
    {
        var text = TextPromptWindow.Frage(this, "Neuer Termin",
            $"Termin am {datum:dddd, dd.MM.yyyy}:");
        if (string.IsNullOrWhiteSpace(text)) return;

        // Offene Änderungen zuerst sichern — die Kalender-Notiz könnte gerade offen sein
        SpeichereAktuelle();

        var note = _store.Notizen.FirstOrDefault(n => n.Meta.Titel == KalenderNotizTitel);
        if (note is null)
        {
            note = _store.Neu("Eingang", "leer");
            note.Meta.Titel = KalenderNotizTitel;
        }

        var zeile = $"- [ ] {text} @{datum:yyyy-MM-dd}";
        if (note.Elemente.OfType<TextElement>().FirstOrDefault() is { } textElement)
        {
            textElement.Text = string.IsNullOrWhiteSpace(textElement.Text)
                ? zeile
                : textElement.Text.TrimEnd() + "\n" + zeile;
        }
        else
        {
            note.Elemente.Add(new TextElement { X = 0, Y = 8, Breite = 620, Text = zeile });
        }
        _store.Speichere(note);

        if (note == _aktuelleNotiz)
            Editor.LadeNote(_aktuelleNotiz); // Editor-Inhalt neu laden (wie Aufgabe_Umschalten)

        // Neuer Termin sofort im Kalender (Kupfer-Punkt) + in der Terminliste
        Dashboard.Aktualisiere(TaskService.Sammle(_store.Notizen));
        AktualisiereSidebar();
        AktualisiereListe();
    }

    // ---------- Neue Notiz ----------

    void BaueNeueNotizMenu()
    {
        var menu = new ContextMenu();
        foreach (var v in Templates.Alle)
        {
            var item = new MenuItem { Header = $"{v.Icon} {v.Name}", Tag = v.Key };
            item.Click += (_, _) => ErstelleNotiz(v.Key);
            menu.Items.Add(item);
        }
        NeueNotizButton.ContextMenu = menu;
    }

    void NeueNotiz_Click(object sender, RoutedEventArgs e)
    {
        // Linksklick öffnet das Vorlagen-Menü unter dem Button
        BaueNeueNotizMenu(); // eigene Vorlagen können sich geändert haben
        var menu = NeueNotizButton.ContextMenu!;
        menu.PlacementTarget = NeueNotizButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ErstelleNotiz(string vorlage)
    {
        var notizbuch = _filterNotizbuch ?? "Eingang";
        var note = _store.Neu(notizbuch, vorlage);
        AktualisiereSidebar();
        AktualisiereListe();
        NotizListe.SelectedItem = note;
        OeffneNotiz(note);
    }

    // ---------- Sonstiges ----------

    void NeuLaden()
    {
        SpeichereAktuelle();
        var pfad = _aktuelleNotiz?.Pfad;
        _aktuelleNotiz = null;
        Editor.LadeNote(null);
        _store.LadeAlle();
        AktualisiereSidebar();
        AktualisiereListe();
        var wieder = _store.Notizen.FirstOrDefault(n => n.Pfad == pfad);
        if (wieder is not null)
        {
            NotizListe.SelectedItem = wieder;
            OeffneNotiz(wieder);
        }
        ZeigeEditorHinweis();
    }

    // ---------- Seitenleisten einklappen (einzeln + Fokus-Modus) ----------

    bool _fokusAktiv;
    bool _sidebarZu, _listeZu;
    GridLength _sidebarBreite = new(230);
    GridLength _listeBreite = new(330);
    const double SidebarMin = 170, ListeMin = 240;

    void SetzeFokusModus(bool an)
    {
        if (an == _fokusAktiv) return;
        _fokusAktiv = an;
        WendeSpaltenAn();
    }

    void KlappeSidebar(bool zu)
    {
        _sidebarZu = zu;
        WendeSpaltenAn();
        _settings.Aktuell.SidebarZu = zu;
        _settings.Speichere();
    }

    void KlappeListe(bool zu)
    {
        _listeZu = zu;
        WendeSpaltenAn();
        _settings.Aktuell.ListeZu = zu;
        _settings.Speichere();
    }

    void SidebarZuklappen_Click(object sender, RoutedEventArgs e) => KlappeSidebar(true);
    void SidebarAusklappen_Click(object sender, RoutedEventArgs e) => KlappeSidebar(false);
    void ListeZuklappen_Click(object sender, RoutedEventArgs e) => KlappeListe(true);
    void ListeAusklappen_Click(object sender, RoutedEventArgs e) => KlappeListe(false);

    /// <summary>Spaltenzustand anwenden: offen, eingeklappt (schmale Leiste) oder Fokus (ganz weg).</summary>
    void WendeSpaltenAn()
    {
        WendeSpalteAn(SidebarSpalte, SidebarPanel, SidebarRail, SplitterLinks,
            ref _sidebarBreite, SidebarMin, offen: !_fokusAktiv && !_sidebarZu, rail: !_fokusAktiv && _sidebarZu);
        WendeSpalteAn(ListeSpalte, ListePanel, ListeRail, SplitterRechts,
            ref _listeBreite, ListeMin, offen: !_fokusAktiv && !_listeZu, rail: !_fokusAktiv && _listeZu);
    }

    static void WendeSpalteAn(ColumnDefinition spalte, UIElement panel, UIElement railElement,
        UIElement splitter, ref GridLength breite, double minBreite, bool offen, bool rail)
    {
        if (offen)
        {
            spalte.MinWidth = minBreite;
            spalte.Width = breite;
        }
        else
        {
            // Aktuelle (per Splitter gezogene) Breite fürs Wiederausklappen merken
            if (panel.Visibility == Visibility.Visible && spalte.Width.Value > 0)
                breite = spalte.Width;
            spalte.MinWidth = 0;
            spalte.Width = rail ? GridLength.Auto : new GridLength(0);
        }
        panel.Visibility = offen ? Visibility.Visible : Visibility.Collapsed;
        railElement.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        splitter.Visibility = offen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- KI-Chat ----------

    bool _chatOffen;

    void SetzeChatSichtbar(bool an)
    {
        if (an == _chatOffen) return;
        _chatOffen = an;
        if (an)
        {
            ChatSpalte.MinWidth = 300;
            ChatSpalte.Width = new GridLength(Math.Max(300, _settings.Aktuell.ChatBreite));
        }
        else
        {
            if (ChatSpalte.Width.Value > 0)
                _settings.Aktuell.ChatBreite = ChatSpalte.Width.Value;
            ChatSpalte.MinWidth = 0;
            ChatSpalte.Width = new GridLength(0);
        }
        ChatPanel.Visibility = an ? Visibility.Visible : Visibility.Collapsed;
        SplitterChat.Visibility = an ? Visibility.Visible : Visibility.Collapsed;
        _settings.Aktuell.ChatOffen = an;
        _settings.Speichere();
    }

    void ChatBubble_Click(object sender, RoutedEventArgs e) => SetzeChatSichtbar(!_chatOffen);

    // ---------- Hintergrund-Animation („Kupfer & Wasser") ----------

    /// <summary>Die zwei Licht-Schimmer sehr langsam treiben lassen — dezent,
    /// GPU-günstig (nur Positions-Animationen) und aus, wenn Windows
    /// Animationen deaktiviert hat.</summary>
    void StarteGlowAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        static System.Windows.Media.Animation.DoubleAnimation Treiben(
            double von, double bis, int sekunden) => new(von, bis, TimeSpan.FromSeconds(sekunden))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
            },
        };

        GlowWasser.BeginAnimation(Canvas.LeftProperty, Treiben(-280, 40, 55));
        GlowWasser.BeginAnimation(Canvas.TopProperty, Treiben(-320, -120, 70));
        GlowKupfer.BeginAnimation(Canvas.RightProperty, Treiben(-240, -40, 65));
        GlowKupfer.BeginAnimation(Canvas.BottomProperty, Treiben(-280, -90, 50));
    }

    void ChatTextEinfuegen(string text)
    {
        if (!Editor.HatNote)
        {
            MessageBox.Show(this, "Erst eine Notiz öffnen, dann kann ich den Text dort einfügen.",
                "KI-Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Editor.FuegeTextAn(text);
    }

    void ChatDateiEinfuegen(string pfad)
    {
        if (!Editor.HatNote)
        {
            MessageBox.Show(this, "Erst eine Notiz öffnen, dann kann ich die Datei dort ablegen.",
                "KI-Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Editor.FuegeExterneDateiAn(pfad);
    }

    /// <summary>Fenster aus dem Tray heraus anzeigen.</summary>
    public void ZeigeFenster()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SpeichereAktuelle();
        // Gezogene Chat-Breite über App-Neustarts merken
        if (_chatOffen && ChatSpalte.Width.Value > 0 &&
            Math.Abs(_settings.Aktuell.ChatBreite - ChatSpalte.Width.Value) > 1)
        {
            _settings.Aktuell.ChatBreite = ChatSpalte.Width.Value;
            _settings.Speichere();
        }
        if (!WirklichSchliessen)
        {
            // Tray-App: Schließen versteckt nur, Beenden über das Tray-Menü
            e.Cancel = true;
            Hide();
        }
    }
}

/// <summary>Minimaler ICommand für die Tastatur-Shortcuts.</summary>
public class RelayCommand : ICommand
{
    readonly Action _aktion;
    public RelayCommand(Action aktion) => _aktion = aktion;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _aktion();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
