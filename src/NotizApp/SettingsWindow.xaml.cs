using System.IO;
using System.Windows;
using Microsoft.Win32;
using NotizApp.Services;

namespace NotizApp;

public partial class SettingsWindow : Window
{
    readonly SettingsService _settings;
    /// <summary>Datenordner beim Öffnen des Dialogs — dorthin werden die Vorlagen
    /// gespeichert, auch wenn der Nutzer gerade einen neuen Ordner wählt
    /// (der Wechsel greift erst nach dem Neustart).</summary>
    readonly string _datenOrdner;
    /// <summary>Arbeitskopie: wird erst beim Speichern übernommen.</summary>
    readonly List<EigeneVorlage> _vorlagen;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        _datenOrdner = settings.Aktuell.DataFolder;
        _vorlagen = Templates.Eigene.Select(v => v.Kopie()).ToList();
        InitializeComponent();

        OrdnerBox.Text = settings.Aktuell.DataFolder;
        AutostartBox.IsChecked = settings.Aktuell.Autostart;
        HotkeyBox.IsChecked = settings.Aktuell.HotkeyEnabled;

        if (Directory.Exists(settings.Aktuell.DataFolder))
        {
            // Auch Unterordner anbieten (relative Pfade wie "Kunden/Meier")
            foreach (var d in Directory.EnumerateDirectories(settings.Aktuell.DataFolder,
                         "*", SearchOption.AllDirectories).OrderBy(x => x))
            {
                QuickNotebookBox.Items.Add(
                    Path.GetRelativePath(settings.Aktuell.DataFolder, d).Replace('\\', '/'));
            }
        }
        QuickNotebookBox.SelectedItem = settings.Aktuell.QuickNotebook;
        if (QuickNotebookBox.SelectedIndex < 0 && QuickNotebookBox.Items.Count > 0)
            QuickNotebookBox.SelectedIndex = 0;

        AktualisiereVorlagenListe();
    }

    void OrdnerAendern_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Neuen Datenordner wählen" };
        if (dialog.ShowDialog(this) == true)
            OrdnerBox.Text = dialog.FolderName;
    }

    // ---------- Eigene Vorlagen ----------

    void AktualisiereVorlagenListe(int auswahl = -1)
    {
        VorlagenListe.Items.Clear();
        foreach (var v in _vorlagen)
            VorlagenListe.Items.Add($"{v.Icon} {v.Name}");
        VorlagenListe.SelectedIndex = auswahl;
    }

    void VorlageNeu_Click(object sender, RoutedEventArgs e)
    {
        var vorlage = new EigeneVorlage();
        vorlage.NeuerKey();
        var dialog = new VorlageEditWindow(vorlage) { Owner = this, Title = "Neue Vorlage" };
        if (dialog.ShowDialog() != true) return;
        _vorlagen.Add(vorlage);
        AktualisiereVorlagenListe(_vorlagen.Count - 1);
    }

    void VorlageBearbeiten_Click(object sender, RoutedEventArgs e)
    {
        int i = VorlagenListe.SelectedIndex;
        if (i < 0 || i >= _vorlagen.Count) return;
        var dialog = new VorlageEditWindow(_vorlagen[i]) { Owner = this };
        if (dialog.ShowDialog() == true)
            AktualisiereVorlagenListe(i);
    }

    void VorlageLoeschen_Click(object sender, RoutedEventArgs e)
    {
        int i = VorlagenListe.SelectedIndex;
        if (i < 0) return;
        if (MessageBox.Show(this,
                $"Vorlage \"{_vorlagen[i].Name}\" wirklich löschen?",
                "NotizApp", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }
        _vorlagen.RemoveAt(i);
        AktualisiereVorlagenListe();
    }

    // ---------- Speichern ----------

    void Speichern_Click(object sender, RoutedEventArgs e)
    {
        _settings.Aktuell.DataFolder = OrdnerBox.Text;
        _settings.Aktuell.HotkeyEnabled = HotkeyBox.IsChecked == true;
        if (QuickNotebookBox.SelectedItem is string nb)
            _settings.Aktuell.QuickNotebook = nb;
        _settings.SetzeAutostart(AutostartBox.IsChecked == true); // speichert mit
        _settings.Speichere();
        Templates.SpeichereEigene(_datenOrdner, _vorlagen);

        ((App)Application.Current).UebernehmeEinstellungen();
        DialogResult = true;
    }
}
