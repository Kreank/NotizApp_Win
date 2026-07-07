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

        ChatPanel.Ki = Editor.Ki;
        ChatPanel.HoleNotizKontext = () => Editor.KiKontext();
        ChatPanel.TextEinfuegen += ChatTextEinfuegen;
        ChatPanel.DateiEinfuegen += ChatDateiEinfuegen;
        if (settings.Aktuell.ChatOffen) SetzeChatSichtbar(true);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); SpeichereAktuelle(); };

        BaueNeueNotizMenu();
        NotizListe.ContextMenuOpening += NotizListe_ContextMenuOpening;

        AktualisiereSidebar();
        AktualisiereListe();
        Editor.LadeNote(null);
        ZeigeEditorHinweis();
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
                var anzahl = _store.Notizen.Count(n => n.Notizbuch == nb);
                var item = new ListBoxItem { Content = $"📁 {nb}  ({anzahl})", Tag = nb };

                var menu = new ContextMenu();
                var umbenennen = new MenuItem { Header = "Umbenennen…" };
                umbenennen.Click += (_, _) => UmbenenneNotizbuch(nb);
                var loeschen = new MenuItem { Header = "Löschen" };
                loeschen.Click += (_, _) => LoescheNotizbuch(nb);
                menu.Items.Add(umbenennen);
                menu.Items.Add(new Separator());
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
        if (!_initialisiert || _aktualisiere || AnsichtListe.SelectedIndex < 0) return;
        _aufgabenAnsicht = AnsichtListe.SelectedIndex == 1;
        if (!_aufgabenAnsicht)
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
            AnsichtListe.SelectedIndex = 0;
            TagListe.SelectedIndex = -1;
            _aktualisiere = false;
            _aufgabenAnsicht = false;
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
            AnsichtListe.SelectedIndex = 0;
            NotizbuchListe.SelectedIndex = -1;
            _aktualisiere = false;
            _aufgabenAnsicht = false;
            _filterNotizbuch = null;
        }
        AktualisiereListe();
    }

    void NeuesNotizbuch_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Frage(this, "Neues Notizbuch", "Name des Notizbuchs:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.NeuesNotizbuch(name);
        AktualisiereSidebar();
    }

    void UmbenenneNotizbuch(string nb)
    {
        var neu = TextPromptWindow.Frage(this, "Notizbuch umbenennen",
            $"Neuer Name für „{nb}“:", vorgabe: nb);
        if (string.IsNullOrWhiteSpace(neu) || neu == nb) return;

        SpeichereAktuelle(); // offene Änderungen sichern, bevor Pfade wechseln
        var ergebnis = _store.NotizbuchUmbenennen(nb, neu);
        if (ergebnis is null)
        {
            MessageBox.Show(this, $"Es gibt bereits ein Notizbuch „{neu}“.",
                "Umbenennen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_filterNotizbuch == nb) _filterNotizbuch = ergebnis;
        if (_settings.Aktuell.QuickNotebook == nb)
        {
            _settings.Aktuell.QuickNotebook = ergebnis;
            _settings.Speichere();
        }
        AktualisiereSidebar();
        AktualisiereListe();
    }

    void LoescheNotizbuch(string nb)
    {
        var anzahl = _store.Notizen.Count(n => n.Notizbuch == nb);
        var antwort = MessageBox.Show(this,
            anzahl == 0
                ? $"Leeres Notizbuch „{nb}“ löschen?"
                : $"Notizbuch „{nb}“ mit {anzahl} Notiz(en) endgültig löschen?\nDas kann nicht rückgängig gemacht werden.",
            "Notizbuch löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (antwort != MessageBoxResult.Yes) return;

        if (_aktuelleNotiz?.Notizbuch == nb)
        {
            _autosaveTimer.Stop();
            _aktuelleNotiz = null;
            Editor.LadeNote(null);
            ZeigeEditorHinweis();
        }
        _store.NotizbuchLoeschen(nb);
        if (_filterNotizbuch == nb) _filterNotizbuch = null;
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
            if (_filterNotizbuch is not null)
                menge = menge.Where(n => n.Notizbuch == _filterNotizbuch);
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
        if (_aufgabenAnsicht)
        {
            _aktualisiere = true;
            AnsichtListe.SelectedIndex = 0;
            _aktualisiere = false;
            _aufgabenAnsicht = false;
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
            var item = new MenuItem { Header = nb, IsEnabled = nb != note.Notizbuch };
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
        AnsichtListe.SelectedIndex = 0;
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
    }

    void ZeigeEditorHinweis() =>
        EditorLeerHinweis.Visibility =
            _aktuelleNotiz is null ? Visibility.Visible : Visibility.Collapsed;

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
    GridLength _chatBreite = new(380);

    void SetzeChatSichtbar(bool an)
    {
        if (an == _chatOffen) return;
        _chatOffen = an;
        if (an)
        {
            ChatSpalte.MinWidth = 300;
            ChatSpalte.Width = _chatBreite;
        }
        else
        {
            if (ChatSpalte.Width.Value > 0) _chatBreite = ChatSpalte.Width;
            ChatSpalte.MinWidth = 0;
            ChatSpalte.Width = new GridLength(0);
        }
        ChatPanel.Visibility = an ? Visibility.Visible : Visibility.Collapsed;
        SplitterChat.Visibility = an ? Visibility.Visible : Visibility.Collapsed;
        _settings.Aktuell.ChatOffen = an;
        _settings.Speichere();
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
