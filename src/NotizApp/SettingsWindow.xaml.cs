using System.IO;
using System.Windows;
using Microsoft.Win32;
using NotizApp.Services;

namespace NotizApp;

public partial class SettingsWindow : Window
{
    readonly SettingsService _settings;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        OrdnerBox.Text = settings.Aktuell.DataFolder;
        AutostartBox.IsChecked = settings.Aktuell.Autostart;
        HotkeyBox.IsChecked = settings.Aktuell.HotkeyEnabled;

        if (Directory.Exists(settings.Aktuell.DataFolder))
        {
            foreach (var d in Directory.EnumerateDirectories(settings.Aktuell.DataFolder).OrderBy(x => x))
                QuickNotebookBox.Items.Add(Path.GetFileName(d));
        }
        QuickNotebookBox.SelectedItem = settings.Aktuell.QuickNotebook;
        if (QuickNotebookBox.SelectedIndex < 0 && QuickNotebookBox.Items.Count > 0)
            QuickNotebookBox.SelectedIndex = 0;
    }

    void OrdnerAendern_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Neuen Datenordner wählen" };
        if (dialog.ShowDialog(this) == true)
            OrdnerBox.Text = dialog.FolderName;
    }

    void Speichern_Click(object sender, RoutedEventArgs e)
    {
        _settings.Aktuell.DataFolder = OrdnerBox.Text;
        _settings.Aktuell.HotkeyEnabled = HotkeyBox.IsChecked == true;
        if (QuickNotebookBox.SelectedItem is string nb)
            _settings.Aktuell.QuickNotebook = nb;
        _settings.SetzeAutostart(AutostartBox.IsChecked == true); // speichert mit
        _settings.Speichere();

        ((App)Application.Current).UebernehmeEinstellungen();
        DialogResult = true;
    }
}
