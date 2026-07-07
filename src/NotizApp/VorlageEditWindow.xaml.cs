using System.Windows;
using NotizApp.Services;

namespace NotizApp;

/// <summary>Dialog zum Anlegen/Bearbeiten einer eigenen Notiz-Vorlage.</summary>
public partial class VorlageEditWindow : Window
{
    readonly EigeneVorlage _vorlage;

    public VorlageEditWindow(EigeneVorlage vorlage)
    {
        _vorlage = vorlage;
        InitializeComponent();

        IconBox.Text = vorlage.Icon;
        NameBox.Text = vorlage.Name;
        TitelBox.Text = vorlage.TitelVorschlag;
        BodyBox.Text = vorlage.Body;
        NameBox.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (NameBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Bitte einen Namen für die Vorlage angeben.",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }
        _vorlage.Icon = IconBox.Text.Trim().Length == 0 ? "📋" : IconBox.Text.Trim();
        _vorlage.Name = NameBox.Text.Trim();
        _vorlage.TitelVorschlag = TitelBox.Text.Trim();
        _vorlage.Body = BodyBox.Text.Replace("\r\n", "\n");
        DialogResult = true;
    }
}
