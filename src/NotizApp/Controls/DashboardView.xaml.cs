using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
/// Dashboard-Ansicht: freie Fläche (Canvas) mit frei verschieb- und
/// größenverstellbaren Karten. Feste Karten sind Kalender und Termine (aus den
/// Aufgaben "- [ ] Text @JJJJ-MM-TT"); jede von Claude recherchierte
/// Neuigkeit ist eine eigene News-Karte. Anordnung und Größe werden pro Karte
/// dauerhaft gespeichert (<see cref="AppSettings.DashboardLayout"/>). Termin-Klick
/// meldet die Quell-Notiz über <see cref="NotizGeklickt"/> ans Hauptfenster.
/// </summary>
public partial class DashboardView : UserControl
{
    internal static readonly CultureInfo Kultur = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Wird vom Host gesetzt (dieselbe Instanz wie im Editor/Chat).</summary>
    public KiService? Ki { get; set; }

    /// <summary>Wird vom Host NACH dem Konstruktor gesetzt — hält die dauerhaft
    /// gespeicherte Karten-Anordnung (<see cref="AppSettings.DashboardLayout"/>).</summary>
    public SettingsService? Einstellungen { get; set; }

    /// <summary>Gespeichertes Layout wurde für Kalender/Termine bereits angewendet.
    /// Passiert einmalig beim ersten <see cref="Aktualisiere"/>, weil
    /// <see cref="Einstellungen"/> erst nach dem Konstruktor gesetzt ist.</summary>
    bool _layoutAngewendet;

    /// <summary>Ein Termin wurde angeklickt — Host soll die Quell-Notiz öffnen.</summary>
    public event Action<Note>? NotizGeklickt;

    /// <summary>„＋ Termin" oder Doppelklick auf einen Kalendertag — Host soll
    /// für dieses Datum einen Termin anlegen (Zeile in der Kalender-Notiz).</summary>
    public event Action<DateTime>? TerminAnlegenAngefordert;

    /// <summary>Anstehende Termine (nicht erledigt, mit Datum, ab heute), aufsteigend.</summary>
    List<TaskItem> _termine = new();
    /// <summary>Erster Tag des angezeigten Monats.</summary>
    DateOnly _monat;
    /// <summary>Im Kalender ausgewählter Tag (filtert die Terminliste).</summary>
    DateOnly? _ausgewaehlt;

    FeedService? _feed;
    bool _feedLaeuft;
    DateTime _feedGeladen; // wann der Feed zuletzt in die Ansicht kam
    /// <summary>Alle geladenen Feed-Karten.</summary>
    List<FeedKarteVm> _feedKarten = new();

    public DashboardView()
    {
        InitializeComponent();
        var heute = DateOnly.FromDateTime(DateTime.Today);
        _monat = new DateOnly(heute.Year, heute.Month, 1);
        // Verschieben/Größe der festen Karten: überlappungsfrei anordnen + speichern
        KalenderKarte.LayoutGeaendert += KarteGeaendert;
        TermineKarte.LayoutGeaendert += KarteGeaendert;
        AktualisiereKopf();
        ZeichneKalender();
        ZeichneTermine();
    }

    /// <summary>Vom Host bei jedem Anzeigen aufgerufen: Termine aus den
    /// aktuellen Aufgaben übernehmen und alles neu zeichnen.</summary>
    public void Aktualisiere(List<TaskItem> aufgaben)
    {
        WendeLayoutAnWennNoetig();
        var heute = DateOnly.FromDateTime(DateTime.Today);
        _termine = aufgaben
            .Where(a => !a.Erledigt && a.Faellig is { } f && f >= heute)
            .OrderBy(a => a.Faellig)
            .ToList();
        AktualisiereKopf();
        ZeichneKalender();
        ZeichneTermine();
    }

    /// <summary>Gespeicherte Anordnung der festen Karten (Kalender/Termine) einmalig
    /// anwenden, sobald der Host <see cref="Einstellungen"/> gesetzt hat.</summary>
    void WendeLayoutAnWennNoetig()
    {
        if (_layoutAngewendet || Einstellungen is null) return;
        _layoutAngewendet = true;
        WendeLayoutAn(KalenderKarte);
        WendeLayoutAn(TermineKarte);
        Arrangiere(null); // überlappungsfrei einrasten
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
        // MouseDown statt MouseUp: nur dort liefert WPF den ClickCount für den
        // Doppelklick (Termin anlegen); Einfachklick selektiert/filtert weiter
        zelle.MouseLeftButtonDown += TagZelle_MausTaste;
        return zelle;
    }

    void TagZelle_MausTaste(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DateOnly tag) return;
        if (e.ClickCount == 2)
        {
            // Doppelklick: Termin an diesem Tag anlegen (der erste Klick hat
            // bereits selektiert — das Datum kommt aber explizit mit)
            TerminAnlegenAngefordert?.Invoke(tag.ToDateTime(TimeOnly.MinValue));
            e.Handled = true;
            return;
        }
        _ausgewaehlt = _ausgewaehlt == tag ? null : tag; // erneuter Klick hebt die Auswahl auf
        ZeichneKalender();
        ZeichneTermine();
    }

    /// <summary>„＋ Termin"-Knopf: nutzt den selektierten Tag, sonst heute.</summary>
    void TerminAnlegen_Click(object sender, RoutedEventArgs e)
    {
        var tag = _ausgewaehlt ?? DateOnly.FromDateTime(DateTime.Today);
        TerminAnlegenAngefordert?.Invoke(tag.ToDateTime(TimeOnly.MinValue));
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
            StatusText.Text = "⚠ Der KI-Dienst ist nicht verfügbar.";
            return;
        }
        _feed ??= new FeedService(Ki);

        _feedLaeuft = true;
        FeedButton.IsEnabled = false;
        StatusText.Text = "⟳ Claude recherchiert… (das kann einige Minuten dauern)";
        try
        {
            var eintraege = await _feed.HoleAsync(erzwingen,
                s => StatusText.Text = s, CancellationToken.None);
            _feedKarten = eintraege.Select(e => new FeedKarteVm(e)).ToList();
            _feedGeladen = DateTime.Now;
            ZeigeFeedKarten();
            StatusText.Text = _feedKarten.Count == 0
                ? "Noch kein Feed — oben aktualisieren."
                : _feed.Stand is { } stand
                    ? $"Stand: {stand.ToString("dd.MM. HH:mm", Kultur)}"
                    : "";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"⚠ Feed konnte nicht geladen werden: {ex.Message}";
        }
        finally
        {
            _feedLaeuft = false;
            FeedButton.IsEnabled = true;
        }
    }

    // ---------- News-Karten auf dem Board ----------

    /// <summary>Baut für jede Feed-Meldung eine eigene, frei platzierbare Karte.
    /// Alte News-Karten werden zuvor entfernt; Kalender- und Termine-Karte bleiben.
    /// Gespeicherte Position/Größe (per URL) hat Vorrang, sonst fließen neue Karten
    /// an Standardplätze rechts neben den festen Karten.</summary>
    void ZeigeFeedKarten()
    {
        for (int i = Board.Children.Count - 1; i >= 0; i--)
            if (Board.Children[i] is DashCard alt &&
                alt.CardId.StartsWith("news:", StringComparison.Ordinal))
                Board.Children.RemoveAt(i);

        for (int i = 0; i < _feedKarten.Count; i++)
        {
            var karte = BaueNewsKarte(_feedKarten[i]);
            Board.Children.Add(karte);
            // Standard-Fluss (3 Spalten neben den festen Karten)
            Canvas.SetLeft(karte, 330 + (i % 3) * 334);
            Canvas.SetTop(karte, (i / 3) * 224);
            WendeLayoutAn(karte); // gespeicherte Position/Größe überschreibt den Fluss
        }
        Arrangiere(null); // überlappungsfrei einrasten (auch neue News-Karten)
    }

    /// <summary>Erzeugt eine News-Karte (Chip, Datum, Titel, Zusammenfassung,
    /// Domain, „Quelle öffnen"); Klick auf den Inhalt öffnet die URL.</summary>
    DashCard BaueNewsKarte(FeedKarteVm vm)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = vm.ChipLeiseBrush,
            Child = new TextBlock
            {
                Text = vm.ChipText,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = vm.ChipTextBrush,
            },
        };
        var datum = new TextBlock
        {
            Text = vm.DatumAnzeige,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        datum.SetResourceReference(TextBlock.ForegroundProperty, "AppTextLeiseBrush");
        var kopf = new DockPanel();
        DockPanel.SetDock(datum, Dock.Right);
        kopf.Children.Add(datum);
        kopf.Children.Add(chip);

        var titel = new TextBlock
        {
            Text = vm.Titel,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var zusammen = new TextBlock
        {
            Text = vm.Zusammenfassung,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        zusammen.SetResourceReference(TextBlock.ForegroundProperty, "AppTextLeiseBrush");

        var domain = new TextBlock
        {
            Text = vm.Domain,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        domain.SetResourceReference(TextBlock.ForegroundProperty, "AppTextLeiseBrush");
        var quelle = new TextBlock { Text = "Quelle öffnen ↗", FontSize = 11 };
        quelle.SetResourceReference(TextBlock.ForegroundProperty, "AppAkzentBrush");
        var fuss = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(domain, Dock.Right);
        fuss.Children.Add(domain);
        fuss.Children.Add(quelle);

        var stapel = new StackPanel();
        stapel.Children.Add(kopf);
        stapel.Children.Add(titel);
        stapel.Children.Add(zusammen);
        stapel.Children.Add(fuss);

        var inhalt = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stapel,
            Cursor = Cursors.Hand,
            DataContext = vm,
            Background = Brushes.Transparent, // ganze Fläche klickbar
            ToolTip = vm.Url,
        };
        inhalt.MouseLeftButtonUp += FeedKarte_Click;

        var karte = new DashCard
        {
            CardId = "news:" + vm.Url,
            Content = inhalt,
            Width = 320,
            Height = 210,
            MinWidth = 200,
            MinHeight = 140,
        };
        karte.LayoutGeaendert += KarteGeaendert;
        return karte;
    }

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

    // ---------- Karten-Anordnung (überlappungsfrei) + Speichern/Laden ----------

    const double Raster = 8;   // Einrast-Raster in px
    const double Luft = 14;    // Mindestabstand zwischen Karten

    /// <summary>Eine Karte wurde verschoben/verkleinert: alles überlappungsfrei
    /// anordnen (aktive Karte behält Vorrang) und die neue Anordnung speichern.</summary>
    void KarteGeaendert(DashCard c) => Arrangiere(c, speichern: true);

    /// <summary>Karten so anordnen, dass sie sich nie überlappen: die aktive Karte
    /// bleibt an ihrem (eingerasteten) Platz, alle anderen weichen nach unten aus
    /// und rücken dann lückenlos nach oben nach (daneben, sofern horizontal Platz
    /// ist — sonst darunter). Bewegungen werden sanft animiert.</summary>
    void Arrangiere(DashCard? aktiv, bool speichern = false)
    {
        var karten = Board.Children.OfType<DashCard>().ToList();
        if (karten.Count == 0) return;

        // Aktive Karte einrasten (Position + Größe)
        if (aktiv is not null)
        {
            Canvas.SetLeft(aktiv, Math.Max(0, Snap(HoleLinks(aktiv))));
            Canvas.SetTop(aktiv, Math.Max(0, Snap(HoleOben(aktiv))));
            aktiv.Width = Math.Max(aktiv.MinWidth, Snap(Breite(aktiv)));
            aktiv.Height = Math.Max(aktiv.MinHeight, Snap(Hoehe(aktiv)));
        }

        // Reihenfolge: aktive zuerst (Vorrang), dann nach (oben, links)
        var reihenfolge = new List<DashCard>();
        if (aktiv is not null) reihenfolge.Add(aktiv);
        reihenfolge.AddRange(karten.Where(k => k != aktiv)
            .OrderBy(HoleOben).ThenBy(HoleLinks));

        var ziel = new Dictionary<DashCard, (double x, double y)>();

        // Phase 1: nach unten schieben, bis keine Überlappung mit bereits Platzierten
        var platziert = new List<DashCard>();
        foreach (var c in reihenfolge)
        {
            double x = Math.Max(0, Snap(HoleLinks(c)));
            double y = Math.Max(0, Snap(HoleOben(c)));
            bool geschoben = true;
            while (geschoben)
            {
                geschoben = false;
                foreach (var p in platziert)
                {
                    var (px, py) = ziel[p];
                    if (HorizUeberlappt(x, Breite(c), px, Breite(p)) &&
                        y < py + Hoehe(p) + Luft && y + Hoehe(c) + Luft > py)
                    {
                        y = py + Hoehe(p) + Luft;
                        geschoben = true;
                    }
                }
            }
            ziel[c] = (x, y);
            platziert.Add(c);
        }

        // Phase 2: nach oben verdichten (Schwerkraft) — jede Karte auf die
        // darüberliegenden, horizontal überlappenden Karten aufsetzen
        var verdichtet = new List<DashCard>();
        foreach (var c in platziert.OrderBy(p => ziel[p].y).ToList())
        {
            double top = 0;
            foreach (var p in verdichtet)
                if (HorizUeberlappt(ziel[c].x, Breite(c), ziel[p].x, Breite(p)))
                    top = Math.Max(top, ziel[p].y + Hoehe(p) + Luft);
            ziel[c] = (ziel[c].x, top);
            verdichtet.Add(c);
        }

        // Anwenden (sanft), Board neu vermessen, ggf. speichern
        double rechts = 0, unten = 0;
        foreach (var c in karten)
        {
            var (x, y) = ziel[c];
            AnimiereZu(c, x, y);
            rechts = Math.Max(rechts, x + Breite(c));
            unten = Math.Max(unten, y + Hoehe(c));
        }
        Board.Width = rechts + 40;
        Board.Height = unten + 40;

        if (speichern && Einstellungen is not null)
        {
            foreach (var c in karten)
            {
                var (x, y) = ziel[c];
                Einstellungen.Aktuell.DashboardLayout[c.CardId] = string.Format(
                    CultureInfo.InvariantCulture, "{0:0};{1:0};{2:0};{3:0}",
                    x, y, Breite(c), Hoehe(c));
            }
            Einstellungen.Speichere();
        }
    }

    /// <summary>Karte weich an ihre Zielposition gleiten lassen (oder direkt setzen,
    /// wenn Windows-Animationen aus sind).</summary>
    void AnimiereZu(DashCard c, double x, double y)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            return;
        }
        Gleite(c, Canvas.LeftProperty, HoleLinks(c), x);
        Gleite(c, Canvas.TopProperty, HoleOben(c), y);
    }

    static void Gleite(DashCard c, DependencyProperty prop, double von, double bis)
    {
        if (Math.Abs(von - bis) < 0.5)
        {
            c.BeginAnimation(prop, null);
            c.SetValue(prop, bis);
            return;
        }
        var a = new DoubleAnimation(von, bis, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        a.Completed += (_, _) => { c.BeginAnimation(prop, null); c.SetValue(prop, bis); };
        c.BeginAnimation(prop, a);
    }

    static bool HorizUeberlappt(double ax, double aw, double bx, double bw) =>
        ax < bx + bw && ax + aw > bx;

    double Breite(DashCard c) => double.IsNaN(c.Width) ? c.ActualWidth : c.Width;
    double Hoehe(DashCard c) => double.IsNaN(c.Height) ? c.ActualHeight : c.Height;
    static double Snap(double v) => Math.Round(v / Raster) * Raster;

    /// <summary>Gespeichertes Layout einer Karte anwenden (falls vorhanden),
    /// sonst bleibt die zuvor gesetzte Standardposition/-größe erhalten.</summary>
    void WendeLayoutAn(DashCard c)
    {
        if (Einstellungen is null) return;
        if (!Einstellungen.Aktuell.DashboardLayout.TryGetValue(c.CardId, out var s)) return;
        var teile = s.Split(';');
        if (teile.Length < 4) return;
        if (double.TryParse(teile[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(teile[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            double.TryParse(teile[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var breite) &&
            double.TryParse(teile[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var hoehe))
        {
            Canvas.SetLeft(c, Math.Max(0, x));
            Canvas.SetTop(c, Math.Max(0, y));
            c.Width = Math.Max(c.MinWidth, breite);
            c.Height = Math.Max(c.MinHeight, hoehe);
        }
    }

    static double HoleLinks(UIElement e)
    {
        var v = Canvas.GetLeft(e);
        return double.IsNaN(v) ? 0 : v;
    }

    static double HoleOben(UIElement e)
    {
        var v = Canvas.GetTop(e);
        return double.IsNaN(v) ? 0 : v;
    }
}
