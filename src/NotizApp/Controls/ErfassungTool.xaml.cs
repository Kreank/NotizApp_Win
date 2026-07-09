using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NotizApp.Services;

namespace NotizApp.Controls;

/// <summary>
/// SHK-Aufnahme: ein Vorhaben mit gemeinsamem Gebäude-Kopf und gemeinsamer
/// Teilstrecken-Topologie, darunter je Gewerk die Details (erste Ausbaustufe:
/// Trinkwasser). Einklappbare Abschnitte, Autosave über <see cref="ErfassungStore"/>.
/// </summary>
public partial class ErfassungTool : UserControl
{
    static readonly CultureInfo De = new("de-DE");

    sealed record KatalogItem(DuVorlage Vorlage, string Anzeige);
    sealed record GasKatalogItem(string Bezeichnung, double Belastung, string Anzeige);

    public event Action<string>? ErgebnisEinfuegen;

    readonly ErfassungStore _store = new();
    readonly DispatcherTimer _autosave;
    ErfassungDaten _daten;
    Erfassung? _aktuell;
    ObservableCollection<Wohneinheit> _we = new();
    ObservableCollection<AwObjekt> _aw = new();
    List<AwObjekt> _awAbgeleitet = new();
    ObservableCollection<GasGeraet> _gas = new();
    ObservableCollection<HeizRaum> _heiz = new();
    bool _laden;

    public ErfassungTool()
    {
        InitializeComponent();

        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SpeichereJetzt(); };

        AwVorlageBox.ItemsSource = ErfassungStore.AbwasserExtraKatalog
            .Select(v => new KatalogItem(v, $"{v.Bezeichnung}  ({Zahl(v.Kalt)} l/s)"))
            .ToList();
        if (AwVorlageBox.Items.Count > 0) AwVorlageBox.SelectedIndex = 0;

        GasVorlageBox.ItemsSource = ErfassungStore.GasGeraetKatalog
            .Select(g => new GasKatalogItem(g.Bezeichnung, g.Belastung, $"{g.Bezeichnung}  ({Zahl(g.Belastung)} kW)"))
            .ToList();
        if (GasVorlageBox.Items.Count > 0) GasVorlageBox.SelectedIndex = 0;

        if (HeizRaumVorlageBox.Items.Count > 0) HeizRaumVorlageBox.SelectedIndex = 0;

        _daten = _store.Lade();
        if (_daten.Vorhaben.Count == 0)
            _daten.Vorhaben.Add(new Erfassung { Name = "Vorhaben 1" });

        FuelleVorhabenBox();
        VorhabenBox.SelectedIndex = 0;
    }

    // ---------- Vorhaben ----------

    void FuelleVorhabenBox()
    {
        _laden = true;
        VorhabenBox.ItemsSource = null;
        VorhabenBox.ItemsSource = _daten.Vorhaben.Select(v => v.Name).ToList();
        _laden = false;
    }

    void VorhabenBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_laden || VorhabenBox.SelectedIndex < 0) return;
        ZeigeVorhaben(_daten.Vorhaben[VorhabenBox.SelectedIndex]);
    }

    void ZeigeVorhaben(Erfassung v)
    {
        _laden = true;
        LoeseBindung();
        _aktuell = v;
        DataContext = v;

        // Gemeinsame Änderungs-Meldung für Autosave/Neuberechnung
        v.Gebaeude.Geaendert = OnGeaendert;
        v.TwRand.Geaendert = OnGeaendert;
        v.Aw.Geaendert = OnGeaendert;

        _we = new ObservableCollection<Wohneinheit>(v.Wohneinheiten);
        _we.CollectionChanged += We_CollectionChanged;
        foreach (var w in _we) { w.PropertyChanged += We_ItemChanged; SubWe(w); }
        WeListe.ItemsSource = _we;

        _aw = new ObservableCollection<AwObjekt>(v.AwObjekte);
        _aw.CollectionChanged += Aw_CollectionChanged;
        foreach (var o in _aw) o.PropertyChanged += Aw_ItemChanged;
        AwGrid.ItemsSource = _aw;

        v.Gas.Geaendert = OnGeaendert;
        _gas = new ObservableCollection<GasGeraet>(v.GasGeraete);
        _gas.CollectionChanged += Gas_CollectionChanged;
        foreach (var g in _gas) g.PropertyChanged += Gas_ItemChanged;
        GasListe.ItemsSource = _gas;

        v.Heiz.Geaendert = OnGeaendert;
        _heiz = new ObservableCollection<HeizRaum>(v.HeizRaeume);
        _heiz.CollectionChanged += Heiz_CollectionChanged;
        foreach (var r in _heiz) { r.PropertyChanged += Heiz_ItemChanged; SubRoom(r); }
        HeizListe.ItemsSource = _heiz;

        KoeffA.Text = Zahl(v.KoeffA);
        KoeffB.Text = Zahl(v.KoeffB);
        KoeffC.Text = Zahl(v.KoeffC);

        _laden = false;
        UpdateSzenarioHinweis();
        Aktualisiere();
    }

    void Szenario_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_laden || _aktuell is null) return;
        _aktuell.Szenario = SzenarioBox.SelectedItem as string ?? "Bestand-Begehung";
        UpdateSzenarioHinweis();
        PlaneSpeichern();
    }

    void UpdateSzenarioHinweis()
    {
        var s = SzenarioBox.SelectedItem as string ?? "";
        SzenarioHinweis.Text = s switch
        {
            "Neubau – alle Daten" =>
                "Alle Planungsdaten liegen vor → direkt 1:1 in Viptool Master übertragen. Eine Aufnahme ist nicht nötig.",
            "Neubau – Teildaten" =>
                "Es fehlen Daten → Begehung nötig. Erfasse unten wie bei einer Bestandsaufnahme, was vor Ort feststeht.",
            _ =>
                "Vor-Ort-Begehung: nur erfassen, was du nur vor Ort bekommst (Mengen, Längen, Höhen, Druck). Nennweiten rechnet Master.",
        };
    }

    void Neu_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Frage(Window.GetWindow(this)!, "Neues Vorhaben",
            "Name des Vorhabens:", $"Vorhaben {_daten.Vorhaben.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _daten.Vorhaben.Add(new Erfassung { Name = name });
        FuelleVorhabenBox();
        VorhabenBox.SelectedIndex = _daten.Vorhaben.Count - 1;
        SpeichereJetzt();
    }

    void Umbenennen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        var name = TextPromptWindow.Frage(Window.GetWindow(this)!, "Vorhaben umbenennen",
            "Neuer Name:", _aktuell.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _aktuell.Name = name;
        var idx = _daten.Vorhaben.IndexOf(_aktuell);
        FuelleVorhabenBox();
        VorhabenBox.SelectedIndex = idx;
        SpeichereJetzt();
    }

    void VorhabenLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        if (MessageBox.Show($"Vorhaben „{_aktuell.Name}“ komplett löschen?",
                "Löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _daten.Vorhaben.Remove(_aktuell);
        if (_daten.Vorhaben.Count == 0)
            _daten.Vorhaben.Add(new Erfassung { Name = "Vorhaben 1" });
        FuelleVorhabenBox();
        VorhabenBox.SelectedIndex = 0;
        SpeichereJetzt();
    }

    // ---------- Gebäudeart → Koeffizienten ----------

    void Gebaeudeart_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_laden || _aktuell is null) return;
        var art = GebaeudeartBox.SelectedItem as string;
        var g = TrinkwasserService.Gebaeudearten.FirstOrDefault(x => x.Name == art);
        if (g is not null && g.Name != "Eigene Werte")
        {
            _laden = true;
            KoeffA.Text = Zahl(g.A);
            KoeffB.Text = Zahl(g.B);
            KoeffC.Text = Zahl(g.C);
            _laden = false;
            UebernehmeKoeff();
        }
        OnGeaendert();
    }

    void Koeff_Changed(object sender, TextChangedEventArgs e)
    {
        if (_laden || _aktuell is null) return;
        UebernehmeKoeff();
        Aktualisiere();
        PlaneSpeichern();
    }

    void UebernehmeKoeff()
    {
        if (_aktuell is null) return;
        if (TryZahl(KoeffA.Text, out var a)) _aktuell.KoeffA = a;
        if (TryZahl(KoeffB.Text, out var b)) _aktuell.KoeffB = b;
        if (TryZahl(KoeffC.Text, out var c)) _aktuell.KoeffC = c;
    }

    // ---------- Wohneinheiten & Orte ----------

    void WeHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        _we.Add(new Wohneinheit { Name = $"Wohnung {_we.Count + 1}", Anzahl = 1 });
    }

    void WeLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Wohneinheit w) _we.Remove(w);
    }

    void We_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (Wohneinheit w in e.OldItems) { w.PropertyChanged -= We_ItemChanged; UnsubWe(w); }
        if (e.NewItems is not null) foreach (Wohneinheit w in e.NewItems) { w.PropertyChanged += We_ItemChanged; SubWe(w); }
        OnGeaendert();
    }

    void We_ItemChanged(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    // Orte je Wohneinheit – verschachtelte Änderungen an die Wohneinheit melden
    void SubWe(Wohneinheit w)
    {
        w.Orte.CollectionChanged += WeOrte_Changed;
        foreach (var o in w.Orte) o.PropertyChanged += Ort_Changed;
    }

    void UnsubWe(Wohneinheit w)
    {
        w.Orte.CollectionChanged -= WeOrte_Changed;
        foreach (var o in w.Orte) o.PropertyChanged -= Ort_Changed;
    }

    void WeOrte_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (Ort o in e.OldItems) o.PropertyChanged -= Ort_Changed;
        if (e.NewItems is not null) foreach (Ort o in e.NewItems) o.PropertyChanged += Ort_Changed;
        (_we.FirstOrDefault(w => w.Orte == sender))?.MeldeGeaendert();
        OnGeaendert();
    }

    void Ort_Changed(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    void OrtHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Wohneinheit w) return;
        var name = (sender as FrameworkElement)?.Tag as string ?? "Ort";
        var v = ErfassungStore.OrtVorlagen.FirstOrDefault(x => x.Name == name);
        w.Orte.Add(new Ort
        {
            Name = v.Name ?? name,
            Waschtisch = v.Wt, Dusche = v.Du, Wanne = v.Wa, Wc = v.Wc,
            Waschmaschine = v.Wm, Geschirrspueler = v.Gsw, Kueche = v.Ks,
        });
    }

    void OrtLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Ort o) return;
        var we = _we.FirstOrDefault(w => w.Orte.Contains(o));
        we?.Orte.Remove(o);
    }

    // ---------- Abwasser ----------

    void AwNutzung_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_laden || _aktuell is null) return;
        var name = AwNutzungBox.SelectedItem as string;
        var treffer = ErfassungStore.Nutzungsarten.FirstOrDefault(n => n.Nutzung == name);
        if (treffer.Nutzung is not null && treffer.Nutzung != "Eigener Wert")
            _aktuell.Aw.K = treffer.K; // aktualisiert das gebundene K-Feld via PropertyChanged
        OnGeaendert();
    }

    void AwK_Changed(object sender, TextChangedEventArgs e) { if (!_laden) OnGeaendert(); }
    void AwQc_Changed(object sender, TextChangedEventArgs e) { if (!_laden) OnGeaendert(); }

    void AwVorlageHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null || AwVorlageBox.SelectedItem is not KatalogItem ki) return;
        _aw.Add(new AwObjekt
        {
            Bezeichnung = ki.Vorlage.Bezeichnung,
            Du = ki.Vorlage.Kalt,
            Anzahl = 1,
            Quelle = "DIN EN 12056",
        });
    }

    void AwLeereZeile_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        _aw.Add(new AwObjekt { Anzahl = 1 });
    }

    void AwLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AwObjekt o) _aw.Remove(o);
    }

    void Aw_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (AwObjekt o in e.OldItems) o.PropertyChanged -= Aw_ItemChanged;
        if (e.NewItems is not null) foreach (AwObjekt o in e.NewItems) o.PropertyChanged += Aw_ItemChanged;
        OnGeaendert();
    }

    void Aw_ItemChanged(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    // ---------- Gas ----------

    void GasVorlageHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null || GasVorlageBox.SelectedItem is not GasKatalogItem ki) return;
        _gas.Add(new GasGeraet { Bezeichnung = ki.Bezeichnung, Belastung = ki.Belastung, Anzahl = 1 });
    }

    void GasLeer_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        _gas.Add(new GasGeraet { Anzahl = 1 });
    }

    void GasLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GasGeraet g) _gas.Remove(g);
    }

    void Gas_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (GasGeraet g in e.OldItems) g.PropertyChanged -= Gas_ItemChanged;
        if (e.NewItems is not null) foreach (GasGeraet g in e.NewItems) g.PropertyChanged += Gas_ItemChanged;
        OnGeaendert();
    }

    void Gas_ItemChanged(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    // ---------- Heizung ----------

    void HeizRaumHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        var name = HeizRaumVorlageBox.SelectedItem as string;
        var v = ErfassungStore.RaumKatalog.FirstOrDefault(r => r.Name == name);
        _heiz.Add(v.Name is not null
            ? new HeizRaum { Name = v.Name, SollTemp = v.Temp, SpezHeizlast = v.Spez }
            : new HeizRaum());
    }

    void HeizRaumLeer_Click(object sender, RoutedEventArgs e)
    {
        if (_aktuell is null) return;
        _heiz.Add(new HeizRaum());
    }

    void HeizRaumLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HeizRaum r) _heiz.Remove(r);
    }

    void Heiz_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (HeizRaum r in e.OldItems) { r.PropertyChanged -= Heiz_ItemChanged; UnsubRoom(r); }
        if (e.NewItems is not null) foreach (HeizRaum r in e.NewItems) { r.PropertyChanged += Heiz_ItemChanged; SubRoom(r); }
        OnGeaendert();
    }

    void Heiz_ItemChanged(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    // Bauteile (Fenster/Türen/Nischen) je Raum – verschachtelte Änderungen an den Raum melden
    void SubRoom(HeizRaum r)
    {
        r.Bauteile.CollectionChanged += RoomBauteile_Changed;
        foreach (var b in r.Bauteile) b.PropertyChanged += Bauteil_Changed;
    }

    void UnsubRoom(HeizRaum r)
    {
        r.Bauteile.CollectionChanged -= RoomBauteile_Changed;
        foreach (var b in r.Bauteile) b.PropertyChanged -= Bauteil_Changed;
    }

    void RoomBauteile_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (Bauteil b in e.OldItems) b.PropertyChanged -= Bauteil_Changed;
        if (e.NewItems is not null) foreach (Bauteil b in e.NewItems) b.PropertyChanged += Bauteil_Changed;
        OnGeaendert();
    }

    void Bauteil_Changed(object? sender, PropertyChangedEventArgs e) => OnGeaendert();

    void BauteilHinzufuegen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HeizRaum r && (sender as FrameworkElement)?.Tag is string art)
            r.Bauteile.Add(new Bauteil { Art = art });
    }

    void BauteilLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Bauteil b) return;
        var room = _heiz.FirstOrDefault(r => r.Bauteile.Contains(b));
        room?.Bauteile.Remove(b);
    }

    // ---------- gemeinsame Änderung ----------

    void OnGeaendert()
    {
        if (_laden) return;
        Aktualisiere();
        PlaneSpeichern();
    }

    void LoeseBindung()
    {
        _we.CollectionChanged -= We_CollectionChanged;
        foreach (var w in _we) { w.PropertyChanged -= We_ItemChanged; UnsubWe(w); }
        _aw.CollectionChanged -= Aw_CollectionChanged;
        foreach (var o in _aw) o.PropertyChanged -= Aw_ItemChanged;
        _gas.CollectionChanged -= Gas_CollectionChanged;
        foreach (var g in _gas) g.PropertyChanged -= Gas_ItemChanged;
        _heiz.CollectionChanged -= Heiz_CollectionChanged;
        foreach (var r in _heiz) { r.PropertyChanged -= Heiz_ItemChanged; UnsubRoom(r); }
        if (_aktuell is not null)
        {
            _aktuell.Gebaeude.Geaendert = null;
            _aktuell.TwRand.Geaendert = null;
            _aktuell.Aw.Geaendert = null;
            _aktuell.Gas.Geaendert = null;
            _aktuell.Heiz.Geaendert = null;
        }
    }

    // ---------- Berechnung / Kopf-Hinweise ----------

    void Aktualisiere()
    {
        if (SummeGesamt is null) return;

        WeLeer.Visibility = _we.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int wohnungen = _we.Sum(w => w.Anzahl);
        TwKopfHinweis.Text = _we.Count == 0 ? "0 Wohneinheiten"
            : $"{_we.Count} Typen · {wohnungen} Whg.";

        var (kalt, warm) = ErfassungStore.SummeVr(_we);
        double gesamt = kalt + warm;
        SummeKalt.Text = $"{Zahl(kalt)} l/s";
        SummeWarm.Text = $"{Zahl(warm)} l/s";
        SummeGesamt.Text = $"{Zahl(gesamt)} l/s";

        TryZahl(KoeffA.Text, out var a);
        TryZahl(KoeffB.Text, out var b);
        TryZahl(KoeffC.Text, out var c);
        var vs = TrinkwasserService.Spitzenvolumenstrom(gesamt, a, b, c);
        VsErgebnis.Text = gesamt > 0 ? $"{Zahl(vs)} l/s" : "—";

        // ---- Abwasser (aus Trinkwasser abgeleitet + zusätzliche Abläufe) ----
        _awAbgeleitet = ErfassungStore.AbwasserAusWohneinheiten(_we);
        AwAbgeleitetListe.ItemsSource = _awAbgeleitet;
        AwAbgeleitetLeer.Visibility = _awAbgeleitet.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        double duAbg = _awAbgeleitet.Sum(o => o.SummeDu);
        AwAbgeleitetSumme.Text = $"ΣDU {Zahl(duAbg)} l/s";

        AwGrid.Visibility = _aw.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        double duExtra = _aw.Sum(o => o.SummeDu);

        double summeDu = duAbg + duExtra;
        double maxDu = 0;
        if (_awAbgeleitet.Count > 0) maxDu = Math.Max(maxDu, _awAbgeleitet.Max(o => o.Du));
        if (_aw.Count > 0) maxDu = Math.Max(maxDu, _aw.Max(o => o.Du));
        int objN = _awAbgeleitet.Count + _aw.Count;
        AwKopfHinweis.Text = objN == 1 ? "1 Objekt" : $"{objN} Objekte";

        double k = _aktuell?.Aw.K ?? 0.5;
        double qc = _aktuell?.Aw.Qc ?? 0;
        var qww = ErfassungStore.Schmutzwasserabfluss(summeDu, k, maxDu);
        AwSummeDu.Text = $"{Zahl(summeDu)} l/s";
        AwQww.Text = summeDu > 0 ? $"{Zahl(qww)} l/s" : "—";
        AwQtot.Text = (summeDu > 0 || qc > 0) ? $"{Zahl(qww + qc)} l/s" : "—";

        // ---- Gas (Wärmeerzeuger aus Heizung ableiten + zusätzliche Geräte) ----
        GasKopfHinweis.Text = _gas.Count == 1 ? "1 Gerät" : $"{_gas.Count} Geräte";
        GasLeer.Visibility = _gas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        double belastungAbg = GasAusHeizung(out var abgText);
        GasAbgeleitet.Text = abgText;
        GasAbgeleitetBox.Visibility = belastungAbg > 0 ? Visibility.Visible : Visibility.Collapsed;
        double belastung = belastungAbg + _gas.Sum(g => g.SummeBelastung);
        GasSummeBelastung.Text = $"{Zahl(belastung)} kW";
        GasVerbrennungsluft.Text = belastung > 0
            ? $"{Zahl(ErfassungStore.Verbrennungsluft(belastung))} m³/h" : "—";

        // ---- Heizung ----
        HeizKopfHinweis.Text = _heiz.Count == 1 ? "1 Raum" : $"{_heiz.Count} Räume";
        HeizLeer.Visibility = _heiz.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        double watt = _heiz.Sum(r => r.Heizlast);
        HeizGesamt.Text = $"{(watt / 1000).ToString("0.0", De)} kW";
        double spreizung = (_aktuell?.Heiz.Vorlauf ?? 0) - (_aktuell?.Heiz.Ruecklauf ?? 0);
        var vstrom = ErfassungStore.Volumenstrom(watt, spreizung);
        HeizVolumenstrom.Text = vstrom > 0 ? $"{vstrom.ToString("#,##0", De)} l/h" : "—";
    }

    // ---------- Persistenz ----------

    void PlaneSpeichern() { _autosave.Stop(); _autosave.Start(); }

    void SpeichereJetzt()
    {
        if (_aktuell is not null)
        {
            _aktuell.Wohneinheiten = _we.ToList();
            _aktuell.AwObjekte = _aw.ToList();
            _aktuell.GasGeraete = _gas.ToList();
            _aktuell.HeizRaeume = _heiz.ToList();
            UebernehmeKoeff();
        }
        try { _store.Speichere(_daten); } catch { /* best effort */ }
    }

    // ---------- Export Trinkwasser ----------

    string BaueTwMarkdown()
    {
        var g = _aktuell!.Gebaeude;
        var r = _aktuell.TwRand;
        var (kalt, warm) = ErfassungStore.SummeVr(_we);
        double gesamt = kalt + warm;
        TryZahl(KoeffA.Text, out var a);
        TryZahl(KoeffB.Text, out var b);
        TryZahl(KoeffC.Text, out var c);
        var vs = TrinkwasserService.Spitzenvolumenstrom(gesamt, a, b, c);
        int wohnungen = _we.Sum(w => w.Anzahl);

        var sb = new StringBuilder();
        sb.AppendLine($"**Trinkwasser-Aufnahme: {_aktuell.Name}** ({_aktuell.Szenario})");
        if (g.Kunde.Length > 0 || g.Adresse.Length > 0)
            sb.AppendLine($"- Objekt: {g.Kunde} {g.Adresse}".TrimEnd());
        sb.AppendLine($"- Gebäudeart: {g.Gebaeudeart} · Wohneinheiten: {wohnungen}");
        if (g.Einspeisedruck > 0) sb.AppendLine($"- Einspeisedruck: {Zahl(g.Einspeisedruck)} bar");
        sb.AppendLine($"- Warmwasser: {r.WwKonzept}");
        sb.AppendLine();

        sb.AppendLine("| Wohneinheit | × Whg. | Orte | WT | Du | Wa | WC | WM | GSp | Kü | Anschl. m | Bögen | T-St. |");
        sb.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var w in _we)
        {
            var orte = string.Join(", ", w.Orte.Select(o => o.Name));
            sb.AppendLine($"| {w.Name} | {w.Anzahl} | {orte} | " +
                          $"{w.Orte.Sum(o => o.Waschtisch)} | {w.Orte.Sum(o => o.Dusche)} | {w.Orte.Sum(o => o.Wanne)} | " +
                          $"{w.Orte.Sum(o => o.Wc)} | {w.Orte.Sum(o => o.Waschmaschine)} | {w.Orte.Sum(o => o.Geschirrspueler)} | " +
                          $"{w.Orte.Sum(o => o.Kueche)} | {Zahl(w.Laenge)} | {w.Boegen} | {w.TStuecke} |");
        }
        sb.AppendLine();
        sb.AppendLine($"- ΣVR kalt {Zahl(kalt)} · warm {Zahl(warm)} · **gesamt {Zahl(gesamt)} l/s** · " +
                      $"Spitzenvolumenstrom Vs = **{Zahl(vs)} l/s** (a={Zahl(a)} b={Zahl(b)} c={Zahl(c)})");

        if (r.Bemerkung.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Hygiene/Sonstiges:** {r.Bemerkung.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    void TwEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_we.Count == 0) { TwHinweis.Text = "Keine Wohneinheit erfasst."; return; }
        ErgebnisEinfuegen?.Invoke(BaueTwMarkdown());
        TwHinweis.Text = "In die Notiz eingefügt.";
    }

    void TwKopieren_Click(object sender, RoutedEventArgs e)
    {
        if (_we.Count == 0) { TwHinweis.Text = "Keine Wohneinheit erfasst."; return; }
        try { Clipboard.SetText(BaueTwMarkdown()); TwHinweis.Text = "In die Zwischenablage kopiert."; }
        catch { TwHinweis.Text = "Kopieren fehlgeschlagen."; }
    }

    // ---------- Export Abwasser ----------

    string BaueAwMarkdown()
    {
        var aw = _aktuell!.Aw;
        var alle = _awAbgeleitet.Concat(_aw).ToList();
        double summeDu = alle.Sum(o => o.SummeDu);
        double maxDu = alle.Count > 0 ? alle.Max(o => o.Du) : 0;
        var qww = ErfassungStore.Schmutzwasserabfluss(summeDu, aw.K, maxDu);
        var qtot = qww + aw.Qc;

        var sb = new StringBuilder();
        sb.AppendLine($"**Abwasser-Aufnahme: {_aktuell.Name}**");
        sb.AppendLine($"- Nutzung: {aw.Nutzung} (K = {Zahl(aw.K)})");
        sb.AppendLine($"- System: {aw.EntwSystem} · öffentl. Kanal: {aw.Kanalsystem}");
        if (aw.Rueckstauebene.Length > 0) sb.AppendLine($"- Rückstauebene: {aw.Rueckstauebene}");
        if (aw.Kanalsohle.Length > 0) sb.AppendLine($"- Anschlusskanal: {aw.Kanalsohle}");
        var merk = new List<string>();
        if (aw.Hauptlueftung) merk.Add("Hauptlüftung über Dach");
        if (aw.Rueckstausicherung) merk.Add("Rückstausicherung");
        if (aw.Hebeanlage) merk.Add("Hebeanlage erforderlich");
        if (merk.Count > 0) sb.AppendLine($"- {string.Join(" · ", merk)}");
        sb.AppendLine();

        sb.AppendLine("| Objekt | Anzahl | DU l/s | ΣDU l/s | Quelle |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var o in alle)
            sb.AppendLine($"| {o.Bezeichnung} | {o.Anzahl} | {Zahl(o.Du)} | {Zahl(o.SummeDu)} | {o.Quelle} |");
        sb.AppendLine($"| **Summe** | {alle.Sum(o => o.Anzahl)} | | **{Zahl(summeDu)}** | |");
        sb.AppendLine();
        sb.AppendLine($"- ΣDU: **{Zahl(summeDu)} l/s** · Qww = K·√ΣDU = **{Zahl(qww)} l/s** · " +
                      $"Qtot (+Qc {Zahl(aw.Qc)}) = **{Zahl(qtot)} l/s**");

        if (aw.Bemerkung.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Sonstiges:** {aw.Bemerkung.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    void AwEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_awAbgeleitet.Count == 0 && _aw.Count == 0) { AwHinweis.Text = "Keine Objekte erfasst."; return; }
        ErgebnisEinfuegen?.Invoke(BaueAwMarkdown());
        AwHinweis.Text = "In die Notiz eingefügt.";
    }

    void AwKopieren_Click(object sender, RoutedEventArgs e)
    {
        if (_awAbgeleitet.Count == 0 && _aw.Count == 0) { AwHinweis.Text = "Keine Objekte erfasst."; return; }
        try { Clipboard.SetText(BaueAwMarkdown()); AwHinweis.Text = "In die Zwischenablage kopiert."; }
        catch { AwHinweis.Text = "Kopieren fehlgeschlagen."; }
    }

    // ---------- Export Gas ----------

    string BaueGasMarkdown()
    {
        var gas = _aktuell!.Gas;
        double belastungAbg = GasAusHeizung(out var abgText);
        double belastung = belastungAbg + _gas.Sum(g => g.SummeBelastung);
        var luft = ErfassungStore.Verbrennungsluft(belastung);

        var sb = new StringBuilder();
        sb.AppendLine($"**Gas-Aufnahme: {_aktuell.Name}**");
        sb.AppendLine($"- Gasart: {gas.Gasart} · Druckstufe: {gas.Druckstufe}");
        if (gas.Zaehler.Length > 0) sb.AppendLine($"- Gaszähler: {gas.Zaehler} (Δp {Zahl(gas.ZaehlerDp)} mbar)");
        if (gas.Gsw.Length > 0) sb.AppendLine($"- Gasströmungswächter: {gas.Gsw}");
        if (gas.Aufstellraum.Length > 0)
            sb.AppendLine($"- Aufstellraum: {gas.Aufstellraum} ({Zahl(gas.AufstellraumVolumen)} m³)");
        sb.AppendLine();

        sb.AppendLine("| Gerät | Anzahl | Belastung kW | Σ kW | Kategorie |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        if (belastungAbg > 0)
            sb.AppendLine($"| {abgText.Replace("aus Heizung: ", "")} | 1 | {Zahl(belastungAbg)} | {Zahl(belastungAbg)} | aus Heizung |");
        foreach (var g in _gas)
            sb.AppendLine($"| {g.Bezeichnung} | {g.Anzahl} | {Zahl(g.Belastung)} | {Zahl(g.SummeBelastung)} | {g.Kategorie} |");
        sb.AppendLine($"| **Summe** | | | **{Zahl(belastung)}** | |");
        sb.AppendLine();
        sb.AppendLine($"- Summe Nennbelastung: **{Zahl(belastung)} kW** · " +
                      $"Verbrennungsluft (1,6 m³/h·kW): **{Zahl(luft)} m³/h**");

        if (gas.Bemerkung.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Sonstiges/Prüfung:** {gas.Bemerkung.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    void GasEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_gas.Count == 0 && GasAusHeizung(out _) <= 0) { GasHinweis.Text = "Keine Gasgeräte erfasst."; return; }
        ErgebnisEinfuegen?.Invoke(BaueGasMarkdown());
        GasHinweis.Text = "In die Notiz eingefügt.";
    }

    void GasKopieren_Click(object sender, RoutedEventArgs e)
    {
        if (_gas.Count == 0 && GasAusHeizung(out _) <= 0) { GasHinweis.Text = "Keine Gasgeräte erfasst."; return; }
        try { Clipboard.SetText(BaueGasMarkdown()); GasHinweis.Text = "In die Zwischenablage kopiert."; }
        catch { GasHinweis.Text = "Kopieren fehlgeschlagen."; }
    }

    // ---------- Export Heizung ----------

    string BaueHeizMarkdown()
    {
        var h = _aktuell!.Heiz;
        double watt = _heiz.Sum(r => r.Heizlast);
        double spreizung = h.Vorlauf - h.Ruecklauf;
        var vstrom = ErfassungStore.Volumenstrom(watt, spreizung);

        var sb = new StringBuilder();
        sb.AppendLine($"**Heizung-Aufnahme: {_aktuell.Name}**");
        sb.AppendLine($"- Erzeuger: {h.Erzeuger} {(h.ErzeugerLeistung > 0 ? $"({Zahl(h.ErzeugerLeistung)} kW)" : "")}".TrimEnd());
        sb.AppendLine($"- Systemtemperatur: {Zahl(h.Vorlauf)}/{Zahl(h.Ruecklauf)} °C (Spreizung {Zahl(spreizung)} K)");
        if (h.Pumpe.Length > 0) sb.AppendLine($"- Pumpe: {h.Pumpe} (Förderhöhe {Zahl(h.Foerderhoehe)} m)");
        if (h.Wasserinhalt > 0 || h.MagVolumen > 0)
            sb.AppendLine($"- Wasserinhalt {Zahl(h.Wasserinhalt)} l · MAG {Zahl(h.MagVolumen)} l (Vordruck {Zahl(h.MagVordruck)} bar)");
        sb.AppendLine($"- Abgleich: {h.Verfahren}");
        sb.AppendLine();

        sb.AppendLine("| Raum | Fläche m² | θi °C | W/m² | Heizlast W | Heizfläche |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var r in _heiz)
            sb.AppendLine($"| {r.Name} | {Zahl(r.Flaeche)} | {Zahl(r.SollTemp)} | {Zahl(r.SpezHeizlast)} | " +
                          $"{r.Heizlast.ToString("#,##0", De)} | {r.Heizflaeche} |");
        sb.AppendLine($"| **Summe** | | | | **{watt.ToString("#,##0", De)} W** | |");
        foreach (var r in _heiz.Where(r => r.Bauteile.Count > 0))
            sb.AppendLine($"- *{r.Name}:* " + string.Join(", ", r.Bauteile.Select(b =>
                $"{b.Art} {Zahl(b.Breite)}×{Zahl(b.Hoehe)} m" +
                (b.Bemerkung.Trim().Length > 0 ? $" ({b.Bemerkung.Trim()})" : ""))));
        sb.AppendLine();
        sb.AppendLine($"- Gesamt-Heizlast (überschlägig): **{(watt / 1000).ToString("0.0", De)} kW** · " +
                      $"Volumenstrom: **{vstrom.ToString("#,##0", De)} l/h**");

        if (h.Bemerkung.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Abgleich/Sonstiges:** {h.Bemerkung.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    void HeizEinfuegen_Click(object sender, RoutedEventArgs e)
    {
        if (_heiz.Count == 0) { HeizHinweis.Text = "Keine Räume erfasst."; return; }
        ErgebnisEinfuegen?.Invoke(BaueHeizMarkdown());
        HeizHinweis.Text = "In die Notiz eingefügt.";
    }

    void HeizKopieren_Click(object sender, RoutedEventArgs e)
    {
        if (_heiz.Count == 0) { HeizHinweis.Text = "Keine Räume erfasst."; return; }
        try { Clipboard.SetText(BaueHeizMarkdown()); HeizHinweis.Text = "In die Zwischenablage kopiert."; }
        catch { HeizHinweis.Text = "Kopieren fehlgeschlagen."; }
    }

    // ---------- Helfer ----------

    /// <summary>Gasbedarf aus der Heizung: Wärmeerzeuger-Leistung, wenn Erzeuger ein Gas-Typ ist.</summary>
    double GasAusHeizung(out string text)
    {
        text = "";
        if (_aktuell is null) return 0;
        var h = _aktuell.Heiz;
        if (h.Erzeuger.Contains("Gas", StringComparison.OrdinalIgnoreCase) && h.ErzeugerLeistung > 0)
        {
            text = $"aus Heizung: {h.Erzeuger} · {Zahl(h.ErzeugerLeistung)} kW";
            return h.ErzeugerLeistung;
        }
        return 0;
    }

    static string Zahl(double d) => d.ToString("0.###", De);

    static bool TryZahl(string? s, out double wert)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }
}
