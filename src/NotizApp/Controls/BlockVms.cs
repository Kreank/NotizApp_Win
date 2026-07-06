using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Ink;
using NotizApp.Models;

namespace NotizApp.Controls;

/// <summary>Basis für die Block-VMs im NoteEditor-ItemsControl.</summary>
public abstract class BlockVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event Action? Geaendert;
    protected void MeldeGeaendert() => Geaendert?.Invoke();
}

public class TextBlockVm : BlockVm
{
    string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnChanged();
            MeldeGeaendert();
        }
    }

    public TextBlockVm() { }
    public TextBlockVm(TextBlockContent c) => _text = c.Text;

    public TextBlockContent ZuModel() => new() { Text = Text };
}

public class InkBlockVm : BlockVm
{
    public const double MinHoehe = 120;
    public const double MaxHoehe = 2000;

    public StrokeCollection Strokes { get; }
    public string Datei { get; set; } = "";
    public string ErkannterText { get; set; } = "";

    double _hoehe = 320;
    public double Hoehe
    {
        get => _hoehe;
        set
        {
            var v = Math.Clamp(value, MinHoehe, MaxHoehe);
            if (Math.Abs(_hoehe - v) < 0.5) return;
            _hoehe = v;
            OnChanged();
            MeldeGeaendert();
        }
    }

    public InkBlockVm() : this(new StrokeCollection()) { }

    public InkBlockVm(StrokeCollection strokes)
    {
        Strokes = strokes;
        Strokes.StrokesChanged += (_, _) =>
        {
            MeldeGeaendert();
            StrokesGeaendert?.Invoke(this);
        };
    }

    public InkBlockVm(InkBlockContent c) : this(c.Strokes ?? new StrokeCollection())
    {
        Datei = c.Datei;
        ErkannterText = c.ErkannterText;
        _hoehe = Math.Clamp(c.Hoehe, MinHoehe, MaxHoehe);
    }

    /// <summary>Feuert bei jeder Strich-Änderung — der Editor hängt hier die Erkennung dran.</summary>
    public event Action<InkBlockVm>? StrokesGeaendert;

    public InkBlockContent ZuModel() => new()
    {
        Datei = Datei,
        ErkannterText = ErkannterText,
        Hoehe = Hoehe,
        Strokes = Strokes,
    };
}
