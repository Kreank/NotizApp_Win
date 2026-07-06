using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace NotizApp;

/// <summary>Erster Start: Datenordner wählen.</summary>
public partial class FirstRunWindow : Window
{
    public string? GewaehlterOrdner { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();
        OrdnerBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notizen");
    }

    void Durchsuchen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Datenordner für Notizen wählen",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) == true)
            OrdnerBox.Text = dialog.FolderName;
    }

    void Los_Click(object sender, RoutedEventArgs e)
    {
        var pfad = OrdnerBox.Text.Trim();
        if (pfad.Length == 0) return;
        try
        {
            Directory.CreateDirectory(pfad);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ordner kann nicht angelegt werden:\n{ex.Message}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        GewaehlterOrdner = pfad;
        DialogResult = true;
    }
}
