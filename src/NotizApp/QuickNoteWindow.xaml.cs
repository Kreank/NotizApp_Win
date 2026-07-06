using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NotizApp.Models;
using NotizApp.Services;

namespace NotizApp;

/// <summary>
/// Kompakte Schnellerfassung (Hotkey Strg+Alt+N / Tray-Menü).
/// Sofort schreibbereit, speichert ins konfigurierte Notizbuch.
/// </summary>
public partial class QuickNoteWindow : Window
{
    readonly NoteStore _store;
    readonly SettingsService _settings;
    readonly InkRecognitionService _erkennung;

    public Note? GespeicherteNotiz { get; private set; }

    public QuickNoteWindow(NoteStore store, SettingsService settings, InkRecognitionService erkennung)
    {
        _store = store;
        _settings = settings;
        _erkennung = erkennung;
        InitializeComponent();

        foreach (var nb in store.Notizbuecher())
            NotizbuchBox.Items.Add(nb);
        NotizbuchBox.SelectedItem = settings.Aktuell.QuickNotebook;
        if (NotizbuchBox.SelectedIndex < 0 && NotizbuchBox.Items.Count > 0)
            NotizbuchBox.SelectedIndex = 0;

        TintenFlaeche.DefaultDrawingAttributes.Color =
            System.Windows.Media.Color.FromRgb(0x3B, 0x78, 0xD8);
        TintenFlaeche.DefaultDrawingAttributes.FitToCurve = true;

        Loaded += (_, _) => KundeBox.Focus();
    }

    string AktuelleVorlage =>
        MeetingToggle.IsChecked == true ? "meeting" :
        AufgabeToggle.IsChecked == true ? "aufgabe" :
        LeerToggle.IsChecked == true ? "leer" : "anruf";

    void Vorlage_Checked(object sender, RoutedEventArgs e)
    {
        if (KundenFelder is null) return; // während InitializeComponent
        foreach (var t in new[] { AnrufToggle, MeetingToggle, AufgabeToggle, LeerToggle })
        {
            if (t != sender) t.IsChecked = false;
        }
        bool anruf = AktuelleVorlage == "anruf";
        KundenFelder.Visibility = anruf ? Visibility.Visible : Visibility.Collapsed;
        if (!anruf && IsLoaded) TitelBox.Focus();
    }

    void Fenster_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Speichern_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    void Abbrechen_Click(object sender, RoutedEventArgs e) => Close();

    async void Speichern_Click(object sender, RoutedEventArgs e)
    {
        var vorlage = AktuelleVorlage;
        var notizbuch = NotizbuchBox.SelectedItem as string ?? "Eingang";
        bool anruf = vorlage == "anruf";

        var titel = TitelBox.Text.Trim();
        if (titel.Length == 0)
        {
            // Titel ableiten: Kundenname, sonst erste Textzeile, sonst Vorlagen-Vorschlag
            if (anruf && KundeBox.Text.Trim().Length > 0)
                titel = $"Anruf {KundeBox.Text.Trim()}";
            else
            {
                var ersteZeile = TextBox.Text.Trim().Split('\n')[0].Trim();
                titel = ersteZeile.Length > 0
                    ? (ersteZeile.Length > 60 ? ersteZeile[..60] + "…" : ersteZeile)
                    : Templates.Hole(vorlage).TitelVorschlag;
            }
        }

        var note = new Note
        {
            Notizbuch = notizbuch,
            Meta = new NoteMeta { Titel = titel, Typ = vorlage },
        };

        if (anruf)
        {
            note.Meta.Kunde.Name = LeerZuNull(KundeBox.Text);
            note.Meta.Kunde.Telefon = LeerZuNull(TelefonBox.Text);
            var dringlichkeit =
                (DringlichkeitBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.IsNullOrEmpty(dringlichkeit))
            {
                note.Meta.Dringlichkeit = dringlichkeit;
                if (dringlichkeit == "notfall")
                    note.Meta.Tags.Add("notfall");
            }
        }

        var text = TextBox.Text.TrimEnd();
        note.Elemente.Add(new TextElement { X = 0, Y = 8, Breite = 620, Text = text });

        if (TintenFlaeche.Strokes.Count > 0)
        {
            // Striche unter das Textfeld auf die Fläche legen
            double versatz = 8 + Math.Max(28, text.Split('\n').Length * 22 + 16) + 24;
            var tinte = TintenFlaeche.Strokes.Clone();
            var m = System.Windows.Media.Matrix.Identity;
            m.Translate(0, versatz);
            tinte.Transform(m, applyToStylusTip: false);
            note.Tinte = tinte;
            note.FlaecheHoehe = tinte.GetBounds().Bottom + 400; // Bounds sind schon versetzt
            // Hintergrunderkennung, damit die Handschrift sofort durchsuchbar ist
            note.TintenText = await _erkennung.ErkenneAsync(tinte) ?? "";
        }

        note.Pfad = _store.NeuerPfad(notizbuch, vorlage);
        _store.Speichere(note);

        GespeicherteNotiz = note;
        Close();
    }

    static string? LeerZuNull(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
