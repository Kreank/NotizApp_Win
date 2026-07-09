using System.IO;
using System.Windows;
using NotizApp.Services;

namespace NotizApp;

/// <summary>
/// Startablauf: Single-Instance-Mutex → Settings (ggf. FirstRun) → Store →
/// Tray → Hotkey → Hauptfenster (außer bei --tray).
/// Die App lebt im Tray; Beenden nur über das Tray-Menü.
/// </summary>
public partial class App : Application
{
    const string MutexName = "NotizApp_Instanz";
    const string EventName = "NotizApp_Zeigen";

    Mutex? _mutex;
    EventWaitHandle? _zeigenEvent;
    RegisteredWaitHandle? _zeigenWait;

    SettingsService _settings = null!;
    NoteStore _store = null!;
    InkRecognitionService _erkennung = null!;
    TrayService? _tray;
    HotkeyService? _hotkey;
    MainWindow? _hauptfenster;
    QuickNoteWindow? _quickFenster;

    /// <summary>Fehlerprotokoll neben der installierten App.</summary>
    static string FehlerLogPfad => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotizApp", "fehler.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool trayStart = e.Args.Contains("--tray");

        // Fehler im UI-Thread abfangen: protokollieren, melden, weiterlaufen —
        // statt die App wortlos zu schließen.
        DispatcherUnhandledException += (_, args) =>
        {
            Protokolliere("UI-Thread", args.Exception);
            args.Handled = true;
            MessageBox.Show(
                "In der NotizApp ist ein Fehler aufgetreten. Die Notiz ist nicht verloren, " +
                "aber der letzte Schritt wurde nicht ausgeführt.\n\n" +
                $"{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                $"Einzelheiten: {FehlerLogPfad}",
                "NotizApp", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // Hintergrund-Threads lassen sich nicht retten — wenigstens die Spur sichern.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Protokolliere("Hintergrund-Thread", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Protokolliere("Task", args.Exception);
            args.SetObserved();
        };

        // Farbsystem „Kupfer & Wasser" anwenden und bei Theme-Wechsel nachziehen
        Farbschema.Anwenden(Resources);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += Theme_Geaendert;

        // ---- Single Instance ----
        _mutex = new Mutex(true, MutexName, out bool erste);
        _zeigenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!erste)
        {
            // Zweitstart: Erstinstanz anstoßen, ihr Hauptfenster zu zeigen
            _zeigenEvent.Set();
            Shutdown();
            return;
        }
        _zeigenWait = ThreadPool.RegisterWaitForSingleObject(_zeigenEvent,
            (_, _) => Dispatcher.BeginInvoke(ZeigeHauptfenster),
            null, -1, false);

        // ---- Settings / erster Start ----
        _settings = new SettingsService();
        if (!_settings.Lade())
        {
            var firstRun = new FirstRunWindow();
            if (firstRun.ShowDialog() != true || firstRun.GewaehlterOrdner is null)
            {
                Shutdown();
                return;
            }
            _settings.Aktuell.DataFolder = firstRun.GewaehlterOrdner;
            _settings.Speichere();
        }

        // ---- Daten ----
        _store = new NoteStore(_settings.Aktuell.DataFolder);
        _store.Initialisieren();
        _store.LadeAlle();
        Templates.LadeEigene(_settings.Aktuell.DataFolder);
        _erkennung = new InkRecognitionService();

        // Anhang-Suchindex (PDF-Text + Bild-OCR) im Hintergrund aufbauen —
        // darf den Start nie blockieren und nie crashen
        var notizen = _store.Notizen.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await AnhangIndexService.Instanz.IndiziereAsync(notizen, CancellationToken.None);
            }
            catch
            {
                // Suche funktioniert auch ohne Anhang-Index
            }
        });

        // ---- Tray ----
        _tray = new TrayService();
        _tray.OeffnenGeklickt += ZeigeHauptfenster;
        _tray.SchnellnotizGeklickt += ZeigeSchnellnotiz;
        _tray.BeendenGeklickt += Beenden;
        _tray.Anzeigen();

        // ---- Hotkey ----
        _hotkey = new HotkeyService();
        _hotkey.HotkeyGedrueckt += ZeigeSchnellnotiz;
        UebernehmeEinstellungen();

        if (!trayStart)
            ZeigeHauptfenster();
    }

    /// <summary>Fehler mit Zeitstempel und vollem Stack anhängen. Darf selbst nie werfen.</summary>
    static void Protokolliere(string quelle, Exception? fehler)
    {
        if (fehler is null) return;
        try
        {
            var pfad = FehlerLogPfad;
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.AppendAllText(pfad,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{quelle}] ==={Environment.NewLine}" +
                $"{fehler}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ohne Protokoll weiterlaufen ist besser als deswegen abzustürzen.
        }
    }

    /// <summary>Nach Änderungen im Einstellungsdialog anwenden (aktuell: Hotkey an/aus).</summary>
    public void UebernehmeEinstellungen()
    {
        if (_hotkey is null) return;
        if (_settings.Aktuell.HotkeyEnabled)
        {
            if (!_hotkey.Aktivieren())
            {
                MessageBox.Show(
                    "Der Hotkey Strg+Alt+N ist bereits durch ein anderes Programm belegt.",
                    "NotizApp", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else
        {
            _hotkey.Deaktivieren();
        }
    }

    void ZeigeHauptfenster()
    {
        if (_hauptfenster is null)
        {
            _hauptfenster = new MainWindow(_store, _settings, _erkennung);
        }
        _hauptfenster.ZeigeFenster();
    }

    void ZeigeSchnellnotiz()
    {
        if (_quickFenster is not null)
        {
            _quickFenster.Activate();
            return;
        }
        _quickFenster = new QuickNoteWindow(_store, _settings, _erkennung);
        _quickFenster.Closed += (_, _) =>
        {
            var gespeichert = _quickFenster?.GespeicherteNotiz;
            _quickFenster = null;
            // Falls das Hauptfenster offen ist: Liste auffrischen
            if (gespeichert is not null && _hauptfenster?.IsVisible == true)
                _hauptfenster.OeffneNotiz(gespeichert);
        };
        _quickFenster.Show();
        _quickFenster.Activate();
    }

    void Beenden()
    {
        if (_hauptfenster is not null)
        {
            _hauptfenster.WirklichSchliessen = true;
            _hauptfenster.Close(); // speichert offene Änderungen
        }
        Shutdown();
    }

    void Theme_Geaendert(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => Farbschema.Anwenden(Resources));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= Theme_Geaendert;
        _zeigenWait?.Unregister(null);
        _hotkey?.Dispose();
        _tray?.Dispose();
        _zeigenEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
