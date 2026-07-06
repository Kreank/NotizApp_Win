# HANDOFF — Stand 06.07.2026

## Auftrag

Der Nutzer hat die V1 der NotizApp freigegeben: **"zieh mal durch"** — die komplette V1
laut `KONZEPT.md` autonom implementieren. Anforderungen sind final abgestimmt, nichts
mehr nachfragen, einfach bauen. Danach Build + Startlauf verifizieren und Ergebnis melden.

## Aktueller Stand (aktualisiert 06.07.2026)

- ✅ **V1 komplett implementiert und verifiziert** (Build 0 Warnungen / 0 Fehler)
- ✅ Getestet per Startlauf mit `NOTIZAPP_APPDATA`-Testordner + Screenshots:
  Hauptfenster (Dark Mode), Notiz öffnen/bearbeiten, Aufgaben-Ansicht inkl. Abhaken
  (schreibt `[x]` in die Datei), Schnellnotiz per Strg+Alt+N im Tray-Modus
  (Titel-Ableitung aus Kundenname, Datei in `Kunden-Anrufe`), Single-Instance
  (Zweitstart zeigt Hauptfenster), Tintenfläche zeichnen → ISF-Sidecar + ink-Fence
  → Roundtrip beim Notizwechsel, Volltextsuche
- ⚠️ Nicht real getestet (braucht echten Stift): Erkennungsqualität der Handschrift;
  Autostart-Registry-Eintrag (Code trivial, bewusst nicht im Test gesetzt)
- ✅ V1 committet (`1f9d972`)
- ✅ Docker Desktop 29.6.1 + WSL2 laufen
- ✅ **V2 (KI) implementiert:** `docker/claude/Dockerfile` + `docker/einrichten.ps1`,
  `Services/KiService.cs` (docker run, Body via stdin, 3-Min-Timeout),
  `KiVorschlagWindow` (Vorschlag editierbar, Übernehmen/Abbrechen),
  ✨-Button im Editor (Zusammenfassen / Aufbereiten / Aufgaben extrahieren).
  Übernahme: Zusammenfassung → Block oben; Aufbereiten → ersetzt Text (bzw. hängt
  an, wenn Tinte in der Notiz); Aufgaben → Block unten. Container-Aufruf ohne
  Login getestet (saubere Fehlermeldung mit Hinweis auf einrichten.ps1)
- ⏳ **Nutzer muss einmalig `.\docker\einrichten.ps1` ausführen** (Claude-Login
  im Container, landet im Volume `notizapp-claude-config`) — danach erster
  echter End-to-End-Test der ✨-Funktionen
- ⚠️ UI-Klicktest der V2-Oberfläche abgebrochen (Nutzer arbeitete aktiv am PC,
  Maus-Simulation hätte gestört) — Code baut, Muster identisch zu V1

## Wichtige Umgebungs-Hinweise

- `dotnet` ist evtl. nicht im PATH der Tool-Session → vor dotnet-Aufrufen:
  `$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + $env:Path`
  (nach dem Neustart vermutlich nicht mehr nötig, kurz testen)
- Testbarkeit: Settings-Service soll env-Variable `NOTIZAPP_APPDATA` als Override für den
  Settings-Ordner unterstützen → App lässt sich mit Scratchpad-Testdaten starten, ohne die
  echten Nutzer-Settings anzulegen
- App zum Verifizieren starten, Fenster-Screenshot machen (GetWindowRect + CopyFromScreen), prüfen, Prozess beenden

## V1-Implementierungsplan (bereits durchdacht — so umsetzen)

### Architektur-Entscheidungen

- **Kein MVVM-Framework, keine NuGet-Pakete.** Pragmatisches Code-Behind + kleine INPC-ViewModels. Frontmatter-Parser selbst schreiben (kontrolliertes Subset, wir schreiben die Dateien ja selbst).
- **Suche/Aufgaben-Index für V1 in-memory** (beim Start alle .md parsen — bei ein paar tausend Notizen unkritisch). SQLite erst später bei Bedarf. `KONZEPT.md` Abschnitt 1 entsprechend anpassen.
- **Tray-Icon über WinForms `NotifyIcon`** → `<UseWindowsForms>true</UseWindowsForms>` ins csproj. Icon zur Laufzeit mit System.Drawing zeichnen (blauer Kreis + "N"). In TrayService nur Aliase (`WF = System.Windows.Forms`) verwenden, sonst Namenskonflikte.
- **Handschrifterkennung:** WinRT `Windows.UI.Input.Inking` via CsWinRT (TFM ist schon richtig):
  WPF-Strokes klonen → je Stroke `StylusPoints` → `InkPoint(new Windows.Foundation.Point(x,y), p.PressureFactor)` → `InkStrokeBuilder.CreateStrokeFromInkPoints(pts, Matrix3x2.Identity)` → `InkStrokeContainer` → `new InkRecognizerContainer().RecognizeAsync(container, InkRecognitionTarget.All)` → Kandidaten mit Leerzeichen joinen. Alles in try/catch — bei Fehler `null` (Feature degradiert sanft).
- **Globaler Hotkey** Strg+Alt+N: `RegisterHotKey` (user32) auf message-only `HwndSource` (`HwndSourceParameters` mit `ParentWindow = new IntPtr(-3)`), WM_HOTKEY=0x0312, MOD_ALT=1|MOD_CONTROL=2|MOD_NOREPEAT=0x4000, VK 'N'=0x4E.
- **Single Instance:** benannter Mutex `NotizApp_Instanz` + `EventWaitHandle` `NotizApp_Zeigen` (Zweitstart setzt Event → Erstinstanz zeigt Hauptfenster). `ShutdownMode.OnExplicitShutdown`; Hauptfenster-Close = Hide (Tray-App), Beenden nur über Tray-Menü.
- **Autostart:** HKCU `...\CurrentVersion\Run`, Wert `"<Environment.ProcessPath>" --tray`; Arg `--tray` = Start ohne Fenster.

### Dateiformat

Notiz = `<Notizbuch-Ordner>/<yyyyMMdd-HHmmss>-<vorlage>.md` mit YAML-Frontmatter
(Subset: `titel`, `typ`, `erstellt`, `geaendert`, `tags: [a, b]`, `kunde:` mit 2-Space-Einrückung
`name/telefon/adresse`, `dringlichkeit`). Body = Markdown mit Tinten-Blöcken als Fence:

    ```ink
    datei: <mdname>.t1.isf
    hoehe: 320
    text: erkannter Text (mehrzeilig erlaubt, bis zur schließenden Fence)
    ```

ISF-Sidecars: `<mdname>.t<n>.isf` (WPF `StrokeCollection.Save/Load`). Beim Speichern
verwaiste `.t*.isf` löschen. KI-Übergabe (V2): Frontmatter weglassen, Ink-Fences durch
`text:` ersetzen.

### Dateien / Struktur (Namespace NotizApp, deutsche UI-Strings)

1. `Models/Note.cs` — `NoteMeta`, `KundeInfo`, `Note` (INPC für Listen-Anzeige: `AnzeigeTitel`, `AnzeigeUntertitel`, `Vorschau`, `VolltextCache`), `NoteBlock`/`TextBlockContent`/`InkBlockContent` (Datei, ErkannterText, Hoehe, Strokes)
2. `Services/Frontmatter.cs` — Parse/Write Frontmatter + `BodyFormat` Parse/Write (ink-Fences)
3. `Services/NoteStore.cs` — LadeAlle (ein Ordner-Level = Notizbücher), LadeTinte (lazy), Speichere (inkl. ISF + Orphan-Cleanup + Volltext-Rebuild), Neu(notizbuch, vorlage), Loesche, Verschiebe, Initialisieren (legt `Eingang`, `Kunden-Anrufe`, `Meetings`, `Nachschlagewerk` + Willkommensnotiz an)
4. `Services/SettingsService.cs` — JSON in `%APPDATA%\NotizApp\settings.json` (Override `NOTIZAPP_APPDATA`); Felder: DataFolder, HotkeyEnabled, Autostart, QuickNotebook; + `SetzeAutostart(bool)` (Registry)
5. `Services/InkRecognitionService.cs` — s.o., `Task<string?> ErkenneAsync(StrokeCollection)`
6. `Services/TaskService.cs` — Regex `^(\s*[-*]\s*\[)( |x|X)(\]\s*)(.*)$` über Text-Blöcke, Fälligkeit `@(\d{4}-\d{2}-\d{2})`; `TaskItem` (Note, BlockIndex, ZeilenIndex, Text, Erledigt, Faellig, Ueberfaellig); Umschalten schreibt Zeile um + speichert
7. `Services/Templates.cs` — Vorlagen: anruf (📞, Body "Anliegen/Vereinbart/- [ ] Rückruf"), meeting (👥, Titel "Besprechung <Datum>", Teilnehmer/Notizen/Aufgaben), aufgabe (☑), leer (📄)
8. `Services/TrayService.cs`, `Services/HotkeyService.cs`
9. `Controls/BlockVms.cs` — `TextBlockVm`/`InkBlockVm` (INPC; InkBlockVm hält StrokeCollection, Hoehe clamp 120–2000)
10. `Controls/NoteEditor.xaml(.cs)` — Kernstück, siehe unten
11. `MainWindow.xaml(.cs)` — 3 Spalten mit GridSplittern: Sidebar (Neue Notiz-Button mit Vorlagen-ContextMenu, Ansichten "Alle Notizen"/"Aufgaben", Notizbücher +, Tags, unten ⚙ Einstellungen) | Notizliste mit Suchfeld (+ Aufgabenliste, Visibility-Umschaltung) | Editor. Kontextmenü Notiz: Löschen, Verschieben. Shortcuts: Strg+N/Strg+S/Strg+F/F5. Autosave: 3s-Debounce nach Editor-Änderung + bei Notizwechsel/Close.
12. `QuickNoteWindow.xaml(.cs)` — Topmost, Vorlagen-ToggleRow (Anruf default), Kundenfelder bei Anruf (Name/Telefon/Dringlichkeit), Titel, Body-TextBox, Expander "✍ Stift" mit InkCanvas, Notizbuch-Combo + Speichern (Strg+Enter, Esc=abbrechen). Leerer Titel → aus Kundenname/erster Zeile ableiten. Dringlichkeit "notfall" → auto-Tag `notfall`.
13. `FirstRunWindow` (Datenordner wählen, Vorschlag Dokumente\Notizen, `OpenFolderDialog`), `SettingsWindow`, `TextPromptWindow` (Mini-Eingabedialog für neues Notizbuch)
14. `App.xaml(.cs)` — StartupUri raus, OnStartup: Mutex → Settings → ggf. FirstRun → Store → Tray → Hotkey → MainWindow (außer `--tray`)

### NoteEditor-Design (der heikelste Teil)

- Oben: Titel-TextBox (groß, rahmenlos), Tags-Zeile, Expander **"Kopf: Kundendaten (bleibt lokal, geht nie an die KI)"** mit Name/Telefon/Adresse/Dringlichkeit/Typ
- Werkzeugleiste: ToggleButtons Stift/Marker/Radierer/Lasso, Farb-Buttons (Blau default — Schwarz ist im Dark Mode unsichtbar!), Dicke (Fein/Mittel/Dick), **ToggleButton "Handschrift → Text"**, "+ Tintenfläche", Speichern
- Inhalt: ScrollViewer (PanningMode=VerticalOnly) → ItemsControl mit `ObservableCollection<BlockVm>`, DataTemplates per Typ: TextBox (AcceptsReturn, rahmenlos, Binding Text TwoWay/PropertyChanged) bzw. Border+InkCanvas (Strokes-/Height-Binding, unten Resize-Thumb + 🗑-Button)
- Werkzeug anwenden: InkCanvas-Referenzen über Loaded-Event sammeln, EditingMode + DefaultDrawingAttributes auf alle setzen
- Erkennung: `StrokeCollection.StrokesChanged` abonnieren (Guard-Flags für Laden/Konvertieren!) → Block in Pending-Set + DispatcherTimer 1,3 s neu starten. Tick: pro Block erkennen; **Toggle AN** → Text an vorhergehenden Text-Block anhängen (ggf. neu einfügen), Strokes leeren; **Toggle AUS** → nur `ErkannterText` aktualisieren (Hintergrunderkennung für Suche/KI)
- Beim Löschen einer Tintenfläche benachbarte Text-Blöcke mergen; immer ≥1 Text-Block

### Reihenfolge

Models+Services → `dotnet build` → Editor → MainWindow → Quick/FirstRun/Settings/Tray/Hotkey/App → Build → Startlauf mit `NOTIZAPP_APPDATA`-Testordner + Fenster-Screenshot prüfen → Testdaten aufräumen → fertig melden (Startanleitung: `dotnet run --project src/NotizApp`).

## Danach (nicht V1)

- Docker/WSL2 ist nach dem Neustart frisch installiert → irgendwann prüfen (`docker --version`, `wsl --status`), das ist die Basis für V2 (Claude-Container)
- V1.1: Export/PDF; V2: KI-Anbindung laut KONZEPT Abschnitt 8
