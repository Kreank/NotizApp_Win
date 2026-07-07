using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NotizApp.Models;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>Ein anstehender Termin in der Dashboard-Liste (aus einem TaskItem).</summary>
public class TerminVm
{
    public TerminVm(TaskItem aufgabe)
    {
        Note = aufgabe.Note;
        Text = aufgabe.AnzeigeText;
        NotizTitel = aufgabe.Note.AnzeigeTitel;
        DatumKompakt = aufgabe.Faellig is { } f
            ? f.ToString("ddd dd.MM.", DashboardView.Kultur)
            : "";
    }

    public Note Note { get; }
    /// <summary>Datum kompakt, z.B. "Mi 09.07."</summary>
    public string DatumKompakt { get; }
    public string Text { get; }
    public string NotizTitel { get; }
}

/// <summary>
/// Zentrale Zuordnung der Feed-Kategorien zu Anzeige (Icon + Label) und Farbe —
/// die eine Stelle für Karten-Chips UND Filter-Pillen. Petrol/Kupfer/Leise-Text
/// kommen aus dem Farbschema (hell/dunkel), Grün/Violett sind feste Töne, die
/// in beiden Designs lesbar sind. Der leise Hintergrund ist immer dieselbe
/// Farbe mit Alpha 0x22 — nie festes Schwarz/Weiß.
/// </summary>
public static class FeedKategorien
{
    /// <summary>Anzeige-Infos einer Kategorie: Label plus kräftige Textfarbe
    /// und leiser Hintergrund (gleiche Farbe, Alpha 0x22).</summary>
    public sealed record Anzeige(string Label, Brush Kraeftig, Brush Leise);

    /// <summary>Feste Reihenfolge der Filter-Pillen.</summary>
    public static readonly string[] Reihenfolge = { "ki", "recht", "foerderung", "technik", "branche" };

    /// <summary>Normalisierter Schlüssel — Unbekanntes (auch altes "branche"
    /// aus dem Cache) wird als "branche" gruppiert.</summary>
    public static string Schluessel(string kategorie) => kategorie switch
    {
        "ki" or "recht" or "foerderung" or "technik" => kategorie,
        _ => "branche",
    };

    public static Anzeige Fuer(string kategorie)
    {
        var (label, farbe) = Schluessel(kategorie) switch
        {
            "ki" => ("🤖 KI", FarbeAusResource("AppAkzentBrush", "#0E7490")),
            "recht" => ("⚖ Recht & Normen", FarbeAusResource("AppKupferBrush", "#C0703C")),
            "foerderung" => ("💶 Förderung", Farbe("#3B9E5F")),
            "technik" => ("🔧 Technik", Farbe("#8E5BD8")),
            _ => ("📰 Branche", FarbeAusResource("AppTextLeiseBrush", "#5E7278")),
        };
        return new Anzeige(label, Pinsel(farbe, 0xFF), Pinsel(farbe, 0x22));
    }

    /// <summary>Neutrale „Alle"-Pille im normalen Textton.</summary>
    public static Anzeige Alle()
    {
        var farbe = FarbeAusResource("AppTextBrush", "#1A272C");
        return new Anzeige("Alle", Pinsel(farbe, 0xFF), Pinsel(farbe, 0x22));
    }

    /// <summary>Aktuelle Farbe eines Farbschema-Brushes (folgt hell/dunkel;
    /// die Chips werden bei jedem Feed-Aufbau neu erzeugt).</summary>
    static Color FarbeAusResource(string schluessel, string fallbackHex) =>
        Application.Current?.TryFindResource(schluessel) is SolidColorBrush b
            ? b.Color
            : Farbe(fallbackHex);

    static Color Farbe(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Brush Pinsel(Color farbe, byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, farbe.R, farbe.G, farbe.B));
        b.Freeze();
        return b;
    }
}

/// <summary>Eine Feed-Karte im Dashboard („Neuigkeiten für dich").</summary>
public class FeedKarteVm
{
    public FeedKarteVm(FeedEintrag eintrag)
    {
        Titel = eintrag.Titel;
        Zusammenfassung = eintrag.Zusammenfassung;
        Url = eintrag.Url;
        Kategorie = FeedKategorien.Schluessel(eintrag.Kategorie);
        var anzeige = FeedKategorien.Fuer(eintrag.Kategorie);
        ChipText = anzeige.Label;
        ChipTextBrush = anzeige.Kraeftig;
        ChipLeiseBrush = anzeige.Leise;
        DatumAnzeige = DateOnly.TryParseExact(eintrag.Datum, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM.yyyy", DashboardView.Kultur)
            : eintrag.Datum;
        Domain = Uri.TryCreate(eintrag.Url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host
            : "";
    }

    public string Titel { get; }
    public string Zusammenfassung { get; }
    public string Url { get; }
    /// <summary>Normalisierter Kategorie-Schlüssel (für den Pillen-Filter).</summary>
    public string Kategorie { get; }
    public string ChipText { get; }
    public Brush ChipTextBrush { get; }
    public Brush ChipLeiseBrush { get; }
    public string DatumAnzeige { get; }
    public string Domain { get; }
}

/// <summary>
/// Dashboard-Ansicht: Begrüßung, eigener Monatskalender mit Terminen aus den
/// Aufgaben ("- [ ] Text @JJJJ-MM-TT") und ein von Claude recherchierter
/// Neuigkeiten-Feed (KI, Recht &amp; Normen, Förderung, Technik) mit
/// Kategorie-Filter-Pillen. Termin-Klick meldet die Quell-Notiz über
/// <see cref="NotizGeklickt"/> ans Hauptfenster.
/// </summary>
public partial class DashboardView : UserControl
{
    internal static readonly CultureInfo Kultur = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Wird vom Host gesetzt (dieselbe Instanz wie im Editor/Chat).</summary>
    public KiService? Ki { get; set; }

    /// <summary>Ein Termin wurde angeklickt — Host soll die Quell-Notiz öffnen.</summary>
    public event Action<Note>? NotizGeklickt;

    /// <summary>Anstehende Termine (nicht erledigt, mit Datum, ab heute), aufsteigend.</summary>
    List<TaskItem> _termine = new();
    /// <summary>Erster Tag des angezeigten Monats.</summary>
    DateOnly _monat;
    /// <summary>Im Kalender ausgewählter Tag (filtert die Terminliste).</summary>
    DateOnly? _ausgewaehlt;

    FeedService? _feed;
    bool _feedLaeuft;
    DateTime _feedGeladen; // wann der Feed zuletzt in die Ansicht kam
    /// <summary>Alle geladenen Feed-Karten (ungefiltert).</summary>
    List<FeedKarteVm> _feedKarten = new();
    /// <summary>Aktiver Kategorie-Filter ("alle" oder ein Kategorie-Schlüssel).</summary>
    string _feedFilter = "alle";

    public DashboardView()
    {
        InitializeComponent();
        var heute = DateOnly.FromDateTime(DateTime.Today);
        _monat = new DateOnly(heute.Year, heute.Month, 1);
        AktualisiereKopf();
        ZeichneKalender();
        ZeichneTermine();
    }

    /// <summary>Vom Host bei jedem Anzeigen aufgerufen: Termine aus den
    /// aktuellen Aufgaben übernehmen und alles neu zeichnen.</summary>
    public void Aktualisiere(List<TaskItem> aufgaben)
    {
        var heute = DateOnly.FromDateTime(DateTime.Today);
        _termine = aufgaben
            .Where(a => !a.Erledigt && a.Faellig is { } f && f >= heute)
            .OrderBy(a => a.Faellig)
            .ToList();
        AktualisiereKopf();
        ZeichneKalender();
        ZeichneTermine();
    }

    // ---------- Kopfzeile ----------

    void AktualisiereKopf()
    {
        BegruessungText.Text = DateTime.Now.Hour switch
        {
            >= 5 and < 11 => "Guten Morgen",
            >= 11 and < 18 => "Guten Tag",
            _ => "Guten Abend",
        };
        DatumText.Text = DateTime.Today.ToString("dddd, d. MMMM yyyy", Kultur);
    }

    // ---------- Kalender ----------

    void MonatZurueck_Click(object sender, RoutedEventArgs e)
    {
        _monat = _monat.AddMonths(-1);
        ZeichneKalender();
    }

    void MonatVor_Click(object sender, RoutedEventArgs e)
    {
        _monat = _monat.AddMonths(1);
        ZeichneKalender();
    }

    void ZeichneKalender()
    {
        MonatText.Text = _monat.ToString("MMMM yyyy", Kultur);
        KalenderTage.Children.Clear();

        var heute = DateOnly.FromDateTime(DateTime.Today);
        var terminTage = _termine
            .Where(t => t.Faellig is not null)
            .Select(t => t.Faellig!.Value)
            .ToHashSet();

        // Erste Zeile beginnt beim Montag vor (oder am) Monatsersten
        int versatz = ((int)_monat.DayOfWeek + 6) % 7; // Montag = 0
        var start = _monat.AddDays(-versatz);
        for (int i = 0; i < 42; i++)
            KalenderTage.Children.Add(BaueTagZelle(start.AddDays(i), heute, terminTage));
    }

    FrameworkElement BaueTagZelle(DateOnly tag, DateOnly heute, HashSet<DateOnly> terminTage)
    {
        bool imMonat = tag.Month == _monat.Month && tag.Year == _monat.Year;
        bool istHeute = tag == heute;
        bool ausgewaehlt = tag == _ausgewaehlt;

        var zahl = new TextBlock
        {
            Text = tag.Day.ToString(Kultur),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (istHeute)
        {
            // Helle Zahl auf dem Petrol-Kreis — der Akzent ist in beiden Designs
            // dunkel genug, deshalb hier ausnahmsweise fest Weiß
            zahl.FontWeight = FontWeights.SemiBold;
            zahl.Foreground = Brushes.White;
        }
        else
        {
            zahl.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
        }

        var mitte = new Grid { Width = 28, Height = 28 };
        if (istHeute)
        {
            var kreis = new Ellipse();
            kreis.SetResourceReference(Shape.FillProperty, "AppAkzentBrush");
            mitte.Children.Add(kreis);
        }
        if (ausgewaehlt)
        {
            // Dezenter Auswahl-Ring (auch um den Heute-Kreis herum möglich)
            var ring = new Ellipse { StrokeThickness = 1.5 };
            ring.SetResourceReference(Shape.StrokeProperty, "AppAkzentBrush");
            mitte.Children.Add(ring);
        }
        mitte.Children.Add(zahl);

        var punkt = new Ellipse
        {
            Width = 4,
            Height = 4,
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = terminTage.Contains(tag) ? Visibility.Visible : Visibility.Hidden,
        };
        punkt.SetResourceReference(Shape.FillProperty, "AppKupferBrush");

        var stapel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stapel.Children.Add(mitte);
        stapel.Children.Add(punkt);

        var zelle = new Border
        {
            Child = stapel,
            Background = Brushes.Transparent, // sonst keine Klickfläche
            Cursor = Cursors.Hand,
            Padding = new Thickness(0, 2, 0, 2),
            Opacity = imMonat ? 1.0 : 0.35, // Vor-/Folgemonat ausgegraut
            Tag = tag,
        };
        zelle.MouseLeftButtonUp += TagZelle_Click;
        return zelle;
    }

    void TagZelle_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DateOnly tag) return;
        _ausgewaehlt = _ausgewaehlt == tag ? null : tag; // erneuter Klick hebt die Auswahl auf
        ZeichneKalender();
        ZeichneTermine();
    }

    // ---------- Termine ----------

    void ZeichneTermine()
    {
        var liste = (_ausgewaehlt is { } tag
                ? _termine.Where(t => t.Faellig == tag)
                : _termine)
            .Take(12)
            .Select(t => new TerminVm(t))
            .ToList();
        TerminListe.ItemsSource = liste;

        TermineLeer.Text = _ausgewaehlt is { } gewaehlt
            ? $"Keine Termine am {gewaehlt.ToString("dd.MM.yyyy", Kultur)}."
            : "Keine anstehenden Termine. Aufgaben mit Datum (@2026-07-09) erscheinen hier.";
        TermineLeer.Visibility = liste.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Termin_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TerminVm termin)
            NotizGeklickt?.Invoke(termin.Note);
    }

    // ---------- Neuigkeiten-Feed ----------

    /// <summary>Beim Anzeigen der Ansicht aufrufen: lädt den Feed asynchron,
    /// wenn noch keiner da ist oder der letzte Stand älter als 12 h ist
    /// (dann entscheidet der Cache, ob Claude neu recherchiert).</summary>
    public void LadeFeedWennAlt()
    {
        if (_feedLaeuft) return;
        if (_feedKarten.Count > 0 && DateTime.Now - _feedGeladen < TimeSpan.FromHours(12))
            return;
        _ = LadeFeedAsync(erzwingen: false);
    }

    async void FeedAktualisieren_Click(object sender, RoutedEventArgs e) =>
        await LadeFeedAsync(erzwingen: true);

    async Task LadeFeedAsync(bool erzwingen)
    {
        if (_feedLaeuft) return;
        if (Ki is null)
        {
            FeedHinweis.Text = "⚠ Der KI-Dienst ist nicht verfügbar.";
            FeedHinweis.Visibility = Visibility.Visible;
            return;
        }
        _feed ??= new FeedService(Ki);

        _feedLaeuft = true;
        FeedButton.IsEnabled = false;
        FeedHinweis.Text = "⟳ Feed wird erstellt — Claude recherchiert… (das kann einige Minuten dauern)";
        FeedHinweis.Visibility = Visibility.Visible;
        try
        {
            var eintraege = await _feed.HoleAsync(erzwingen,
                s => StatusText.Text = s, CancellationToken.None);
            _feedKarten = eintraege.Select(e => new FeedKarteVm(e)).ToList();
            _feedGeladen = DateTime.Now;
            // Filter beibehalten, sofern die Kategorie im neuen Feed noch vorkommt
            if (_feedFilter != "alle" && _feedKarten.All(k => k.Kategorie != _feedFilter))
                _feedFilter = "alle";
            BaueFilterPillen();
            ZeigeFeedKarten();
            if (_feedKarten.Count == 0)
            {
                FeedHinweis.Text = "Noch kein Feed — oben aktualisieren.";
                FeedHinweis.Visibility = Visibility.Visible;
            }
            else
            {
                FeedHinweis.Visibility = Visibility.Collapsed;
            }
            StatusText.Text = _feed.Stand is { } stand
                ? $"Stand: {stand.ToString("dd.MM. HH:mm", Kultur)}"
                : "";
        }
        catch (OperationCanceledException)
        {
            FeedHinweis.Text = "Abgebrochen.";
            FeedHinweis.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            FeedHinweis.Text = $"⚠ Feed konnte nicht geladen werden:\n{ex.Message}";
            FeedHinweis.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }
        finally
        {
            _feedLaeuft = false;
            FeedButton.IsEnabled = true;
        }
    }

    // ---------- Kategorie-Filter (Pillen) ----------

    /// <summary>Pillen-Zeile neu aufbauen: „Alle" zuerst, dann je eine Pille pro
    /// Kategorie, die im geladenen Feed tatsächlich vorkommt (feste Reihenfolge).
    /// Es ist immer genau eine Pille aktiv.</summary>
    void BaueFilterPillen()
    {
        FilterLeiste.Children.Clear();
        if (_feedKarten.Count == 0)
        {
            FilterLeiste.Visibility = Visibility.Collapsed;
            return;
        }
        FilterLeiste.Visibility = Visibility.Visible;

        FilterLeiste.Children.Add(BauePille("alle", FeedKategorien.Alle()));
        var vorhanden = _feedKarten.Select(k => k.Kategorie).ToHashSet();
        foreach (var kategorie in FeedKategorien.Reihenfolge.Where(vorhanden.Contains))
            FilterLeiste.Children.Add(BauePille(kategorie, FeedKategorien.Fuer(kategorie)));
    }

    ToggleButton BauePille(string schluessel, FeedKategorien.Anzeige anzeige)
    {
        var pille = new ToggleButton
        {
            Content = anzeige.Label,
            Style = (Style)FindResource("FilterPille"),
            Foreground = anzeige.Kraeftig,
            Background = anzeige.Leise,
            IsChecked = _feedFilter == schluessel,
            Tag = schluessel,
            ToolTip = schluessel == "alle"
                ? "Alle Neuigkeiten zeigen"
                : "Nur diese Kategorie zeigen",
        };
        pille.Click += FilterPille_Click;
        return pille;
    }

    void FilterPille_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton pille || pille.Tag is not string schluessel) return;
        // Abwählen der aktiven Pille fällt auf „Alle" zurück — so ist immer genau eine aktiv
        _feedFilter = pille.IsChecked == true ? schluessel : "alle";
        BaueFilterPillen(); // IsChecked-Zustände aller Pillen neu setzen
        ZeigeFeedKarten();
    }

    /// <summary>Kartenanzeige aus dem gefilterten Bestand neu aufbauen.</summary>
    void ZeigeFeedKarten() =>
        FeedListe.ItemsSource = _feedFilter == "alle"
            ? _feedKarten
            : _feedKarten.Where(k => k.Kategorie == _feedFilter).ToList();

    void FeedKarte_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FeedKarteVm karte) return;
        try
        {
            Process.Start(new ProcessStartInfo(karte.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                $"Der Link konnte nicht geöffnet werden:\n{ex.Message}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
