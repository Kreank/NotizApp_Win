using System.Windows;

namespace NotizApp;

/// <summary>Mini-Eingabedialog (z.B. Name eines neuen Notizbuchs).</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => EingabeBox.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Zeigt den Dialog und gibt die Eingabe zurück (null bei Abbruch).</summary>
    public static string? Frage(Window owner, string titel, string frage, string vorgabe = "")
    {
        var w = new TextPromptWindow { Owner = owner, Title = titel };
        w.FrageText.Text = frage;
        w.EingabeBox.Text = vorgabe;
        w.EingabeBox.SelectAll();
        return w.ShowDialog() == true ? w.EingabeBox.Text.Trim() : null;
    }
}
