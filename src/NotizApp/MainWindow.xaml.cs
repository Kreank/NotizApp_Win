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

    public MainWindow(NoteStore store, SettingsService settings, InkRecognitionService erkennung)
    {
        _store = store;
        _settings = settings;

        NeueNotizCommand = new RelayCommand(() => NeueNotiz_Click(this, new RoutedEventArgs()));
        SpeichernCommand = new RelayCommand(SpeichereAktuelle);
        SuchenCommand = new RelayCommand(() => { SuchBox.Focus(); SuchBox.SelectAll(); });
        NeuLadenCommand = new RelayCommand(NeuLaden);

        InitializeComponent();
        _initialisiert = true;

        Editor.Erkennung = erkennung;
        Editor.NotizGeaendert += Editor_NotizGeaendert;
        Editor.SpeichernAngefordert += SpeichereAktuelle;

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
