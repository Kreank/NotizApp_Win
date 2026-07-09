using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
        Dashboard.Einstellungen = settings;
        Dashboard.NotizGeklickt += DashboardNotizGeklickt;
        Dashboard.TerminAnlegenAngefordert += DashboardTerminAnlegen;

        ChatPanel.Ki = Editor.Ki;
        ChatPanel.HoleNotizKontext = () => Editor.KiBody();
        ChatPanel.HoleNotizAnhaenge = () => Editor.AnhangPfade();
        ChatPanel.TextEinfuegen += ChatTextEinfuegen;
        ChatPanel.DateiEinfuegen += ChatDateiEinfuegen;
        if (settings.Aktuell.ChatOffen) SetzeChatSichtbar(true);

        // Werkzeuge: Ergebnis in die aktuelle Notiz einfügen (wie beim Chat)
        HeizlastTool.ErgebnisEinfuegen += ChatTextEinfuegen;
        VolumenstromTool.ErgebnisEinfuegen += ChatTextEinfuegen;
        WasserinhaltTool.ErgebnisEinfuegen += ChatTextEinfuegen;
        AusdehnungTool.ErgebnisEinfuegen += ChatTextEinfuegen;
        GeraetewissenTool.ErgebnisEinfuegen += ChatTextEinfuegen;
        GeraetewissenTool.Einstellungen = settings;
        ErfassungTool.ErgebnisEinfuegen += ChatTextEinfuegen;

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); SpeichereAktuelle(); };

        BaueNeueNotizMenu();
        NotizListe.ContextMenuOpening += NotizListe_ContextMenuOpening;

        SetzeRauschTextur();

        Loaded += (_, _) =>
        {
            ErzeugeUndStartePartikel();
            Animationen.Pulsieren(ChatBubble); // sanft „atmende" Chat-Bubble
        };
        SizeChanged += Partikel_FensterGroesse;

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
            var alleNb = _store.Notizbuecher().ToList();
            foreach (var nb in alleNb)
            {
                // Kinder eines eingeklappten Ordners nicht anzeigen
                if (IstUnterEingeklapptem(nb)) continue;

                // Anzahl inkl. Unterordner — der Klick auf den Ordner zeigt ja auch den Teilbaum
                var anzahl = _store.Notizen.Count(n => NoteStore.ImTeilbaum(n.Notizbuch, nb));
                var hatKinder = alleNb.Any(x => x.StartsWith(nb + "/", StringComparison.Ordinal));
                var eingeklappt = _eingeklappteOrdner.Contains(nb);
                var item = new ListBoxItem
                {
                    Content = NotizbuchEintrag(nb, anzahl, hatKinder, eingeklappt),
                    Tag = nb,
                };

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
        VerlasseWerkzeug();
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
        VerlasseWerkzeug();
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
        VerlasseWerkzeug();
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

    // ---------- Werkzeuge ----------

    bool _werkzeugAnsicht;

    /// <summary>Werkzeug-Ansichten in der Reihenfolge der WERKZEUGE-Liste
    /// (Index = Listeneintrag). Bei neuem Tool hier und in der Liste ergänzen.</summary>
    UIElement[] Werkzeuge => new UIElement[]
        { HeizlastTool, VolumenstromTool, WasserinhaltTool, AusdehnungTool, UmrechnerTool,
          GeraetewissenTool, ErfassungTool };

    /// <summary>Auswahl in der WERKZEUGE-Liste: das gewählte Tool über den Editor legen.</summary>
    void WerkzeugListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aktualisiere || WerkzeugListe.SelectedIndex < 0) return;
        // Nur ein Eintrag hervorheben: andere Navigationslisten abwählen
        _aktualisiere = true;
        AnsichtListe.SelectedIndex = -1;
        NotizbuchListe.SelectedIndex = -1;
        TagListe.SelectedIndex = -1;
        _aktualisiere = false;
        SetzeDashboardSichtbar(false);
        ZeigeWerkzeug(WerkzeugListe.SelectedIndex);
    }

    /// <summary>Das Tool mit dem gegebenen Index zeigen, die übrigen ausblenden
    /// (index &lt; 0 blendet alle aus und gibt den Editor frei).</summary>
    void ZeigeWerkzeug(int index)
    {
        _werkzeugAnsicht = index >= 0;
        var tools = Werkzeuge;
        for (int i = 0; i < tools.Length; i++)
            tools[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = _werkzeugAnsicht ? Visibility.Collapsed : Visibility.Visible;
        if (index >= 0 && tools[index] is FrameworkElement tool)
            Animationen.EinblendenGleiten(tool);
        else if (index < 0 && _aktuelleNotiz is not null) Animationen.Einblenden(Editor);
        ZeigeEditorHinweis();
    }

    /// <summary>Werkzeug-Ansicht verlassen (Auswahl aufheben, Tools ausblenden) —
    /// wird beim Wechsel zu einer Ansicht/Notiz aufgerufen.</summary>
    void VerlasseWerkzeug()
    {
        if (!_werkzeugAnsicht && WerkzeugListe.SelectedIndex < 0) return;
        _aktualisiere = true;
        WerkzeugListe.SelectedIndex = -1;
        _aktualisiere = false;
        ZeigeWerkzeug(-1);
    }

    /// <summary>Ordner-Silhouette (Mappe mit Reiter) für die Sidebar — einfärbbar,
    /// im Gegensatz zum 📁-Farb-Emoji, das seine Farbe nicht ändern kann.</summary>
    static readonly System.Windows.Media.Geometry OrdnerForm =
        System.Windows.Media.Geometry.Parse(
            "M0,2 A2,2 0 0 1 2,0 L5,0 L7,2 L13,2 A2,2 0 0 1 15,4 L15,10 A2,2 0 0 1 13,12 L2,12 A2,2 0 0 1 0,10 Z");

    /// <summary>Eingeklappte Notizbuch-Ordner (voller Pfad). Nur zur Laufzeit —
    /// beim Neustart sind alle Ordner wieder aufgeklappt.</summary>
    readonly HashSet<string> _eingeklappteOrdner = new();

    /// <summary>Ein-/Ausklappen eines Ordners umschalten und Sidebar neu aufbauen.</summary>
    void ToggleEinklappen(string nb)
    {
        if (!_eingeklappteOrdner.Remove(nb)) _eingeklappteOrdner.Add(nb);
        AktualisiereSidebar();
    }

    /// <summary>Liegt <paramref name="nb"/> unter einem eingeklappten Ordner?</summary>
    bool IstUnterEingeklapptem(string nb)
    {
        int idx = 0;
        while ((idx = nb.IndexOf('/', idx)) >= 0)
        {
            if (_eingeklappteOrdner.Contains(nb[..idx])) return true;
            idx++;
        }
        return false;
    }

    /// <summary>Sidebar-Eintrag: optionaler Klapp-Pfeil (bei Unterordnern) +
    /// Ordnersymbol (in der Notizbuch-Farbe, falls gesetzt) + Name + Anzahl.
    /// Unterordner ("Kunden/Meier") werden eingerückt und zeigen nur den letzten Namensteil.</summary>
    StackPanel NotizbuchEintrag(string nb, int anzahl, bool hatKinder, bool eingeklappt)
    {
        int tiefe = nb.Count(c => c == '/');
        var anzeigeName = nb[(nb.LastIndexOf('/') + 1)..];

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(tiefe * 16, 0, 0, 0),
        };

        // Klapp-Pfeil nur bei vorhandenen Unterordnern; sonst Platzhalter für die Ausrichtung.
        if (hatKinder)
        {
            var pfeil = new Button
            {
                Content = eingeklappt ? "▸" : "▾",
                Width = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 0),
                FontSize = 10,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (System.Windows.Media.Brush)FindResource("AppTextBrush"),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = eingeklappt ? "Unterordner anzeigen" : "Unterordner einklappen",
            };
            // Klick auf den Pfeil klappt nur um — er darf die Ordner-Auswahl nicht auslösen
            pfeil.Click += (_, e) => { e.Handled = true; ToggleEinklappen(nb); };
            stack.Children.Add(pfeil);
        }
        else
        {
            stack.Children.Add(new System.Windows.Controls.Border
            {
                Width = 16,
                Background = System.Windows.Media.Brushes.Transparent,
            });
        }

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
        stack.Children.Add(new TextBlock
        {
            Text = $"{anzeigeName}  ({anzahl})",
            VerticalAlignment = VerticalAlignment.Center,
        });
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
        VerlasseWerkzeug();
        SpeichereAktuelle();
        _aktuelleNotiz = note;
        _store.LadeTinte(note);
        Editor.LadeNote(note);
        if (Editor.Visibility == Visibility.Visible) Animationen.EinblendenGleiten(Editor);
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

    void ZeigeEditorHinweis()
    {
        // Im Dashboard keinen „Keine Notiz"-Hinweis über die Ansicht legen
        bool zeigen = _aktuelleNotiz is null && !_dashboardAnsicht && !_werkzeugAnsicht;
        bool wurdeVerborgen = EditorLeerHinweis.Visibility != Visibility.Visible;
        EditorLeerHinweis.Visibility = zeigen ? Visibility.Visible : Visibility.Collapsed;
        if (zeigen && wurdeVerborgen) Animationen.EinblendenGleiten(EditorLeerHinweis);
    }

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
        if (an) Animationen.EinblendenGleiten(Dashboard);
        else if (_aktuelleNotiz is not null) Animationen.Einblenden(Editor);
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
            note = _store.Neu(NoteStore.StandardNotizbuch, "leer");
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
        var notizbuch = _filterNotizbuch ?? NoteStore.StandardNotizbuch;
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
            double ziel = Math.Max(300, _settings.Aktuell.ChatBreite);
            ChatPanel.Visibility = Visibility.Visible;
            SplitterChat.Visibility = Visibility.Visible;
            ChatSpalte.MinWidth = 0; // während der Animation nicht klemmen
            Animationen.SpaltenBreite(ChatSpalte, 0, ziel,
                fertig: () => ChatSpalte.MinWidth = 300);
        }
        else
        {
            if (ChatSpalte.Width.Value > 0)
                _settings.Aktuell.ChatBreite = ChatSpalte.Width.Value;
            double von = ChatSpalte.Width.Value;
            ChatSpalte.MinWidth = 0;
            Animationen.SpaltenBreite(ChatSpalte, von, 0, fertig: () =>
            {
                ChatPanel.Visibility = Visibility.Collapsed;
                SplitterChat.Visibility = Visibility.Collapsed;
            });
        }
        _settings.Aktuell.ChatOffen = an;
        _settings.Speichere();
    }

    void ChatBubble_Click(object sender, RoutedEventArgs e) => SetzeChatSichtbar(!_chatOffen);

    // ---------- Ambient-Hintergrund („Kupfer & Wasser") ----------

    /// <summary>Feine, gekachelte Graustufen-Rausch-Textur erzeugen und über die
    /// Ambient-Ebene legen — dithert das Verlaufs-Banding weg (fester Seed → stabil,
    /// einmalig erzeugt, keine laufende GPU-Last).</summary>
    void SetzeRauschTextur()
    {
        const int n = 160;
        var wb = new WriteableBitmap(n, n, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[n * n * 4];
        var rnd = new Random(12345);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)rnd.Next(0, 256);
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        wb.WritePixels(new Int32Rect(0, 0, n, n), px, n * 4, 0);
        wb.Freeze();
        NebelRausch.Fill = new ImageBrush(wb)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, n, n),
            ViewportUnits = BrushMappingMode.Absolute,
        };
    }


    readonly List<Ellipse> _partikel = new();
    DispatcherTimer? _partikelNeuTimer;
    double _partikelBreite, _partikelHoehe;

    /// <summary>Schwebende Licht-Partikel („Motes") erzeugen und animieren — kleine,
    /// weich glimmende Punkte in den Wasser-/Kupfer-Tönen auf dem tiefdunklen Grund.
    /// Jeder driftet langsam und funkelt (Deckkraft) mit eigener Periode → ruhiger,
    /// „mystischer" Nebel statt generischer Radial-Lichtquellen.</summary>
    void ErzeugeUndStartePartikel()
    {
        double w = ActualWidth > 0 ? ActualWidth : 1240;
        double h = ActualHeight > 0 ? ActualHeight : 760;
        _partikelBreite = w;
        _partikelHoehe = h;

        PartikelEbene.Children.Clear();
        _partikel.Clear();

        var rnd = new Random();
        string[] toene = { "#3FB9D3", "#48C7C0", "#7FD8E6", "#DE9159", "#A9F0E6", "#5FA9C4" };
        int anzahl = Math.Clamp((int)(w * h / 26000), 26, 60);

        for (int i = 0; i < anzahl; i++)
        {
            double kern = 2 + rnd.NextDouble() * 5;
            double glow = kern * (4 + rnd.NextDouble() * 3);
            var f = (Color)ColorConverter.ConvertFromString(toene[rnd.Next(toene.Length)]);

            var pinsel = new RadialGradientBrush();
            pinsel.GradientStops.Add(new GradientStop(Color.FromArgb(235, f.R, f.G, f.B), 0));
            pinsel.GradientStops.Add(new GradientStop(Color.FromArgb(90, f.R, f.G, f.B), 0.4));
            pinsel.GradientStops.Add(new GradientStop(Color.FromArgb(0, f.R, f.G, f.B), 1));
            pinsel.Freeze();

            var e = new Ellipse
            {
                Width = glow,
                Height = glow,
                Fill = pinsel,
                IsHitTestVisible = false,
                Opacity = 0.15 + rnd.NextDouble() * 0.5,
                RenderTransform = new TranslateTransform(),
            };
            Canvas.SetLeft(e, rnd.NextDouble() * w - glow / 2);
            Canvas.SetTop(e, rnd.NextDouble() * h - glow / 2);
            PartikelEbene.Children.Add(e);
            _partikel.Add(e);
        }

        if (!SystemParameters.ClientAreaAnimation) return;

        static DoubleAnimation Drift(double von, double bis, double sek, bool auto = true) =>
            new(von, bis, TimeSpan.FromSeconds(sek))
            {
                AutoReverse = auto,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };

        foreach (var e in _partikel)
        {
            var t = (TranslateTransform)e.RenderTransform;
            double ax = (rnd.NextDouble() * 2 - 1) * (40 + rnd.NextDouble() * 150);
            double ay = (rnd.NextDouble() * 2 - 1) * (40 + rnd.NextDouble() * 150);
            t.BeginAnimation(TranslateTransform.XProperty, Drift(0, ax, 34 + rnd.NextDouble() * 50));
            t.BeginAnimation(TranslateTransform.YProperty, Drift(0, ay, 34 + rnd.NextDouble() * 50));
            double basis = e.Opacity;
            e.BeginAnimation(OpacityProperty, Drift(basis * 0.2, basis, 3 + rnd.NextDouble() * 8));
        }
    }

    /// <summary>Bei deutlicher Fenstergrößenänderung das Partikelfeld neu erzeugen
    /// (entprellt), damit es die ganze Fläche füllt.</summary>
    void Partikel_FensterGroesse(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(ActualWidth - _partikelBreite) < 160 &&
            Math.Abs(ActualHeight - _partikelHoehe) < 160)
            return;
        _partikelNeuTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _partikelNeuTimer.Tick -= PartikelNeu_Tick;
        _partikelNeuTimer.Tick += PartikelNeu_Tick;
        _partikelNeuTimer.Stop();
        _partikelNeuTimer.Start();
    }

    void PartikelNeu_Tick(object? sender, EventArgs e)
    {
        _partikelNeuTimer?.Stop();
        ErzeugeUndStartePartikel();
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
