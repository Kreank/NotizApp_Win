# HANDOFF — Stand 09.07.2026

## Kontext

NotizApp: native Win11-Notiz-App (WPF/.NET 9, Fluent `ThemeMode="System"`) für einen
SHK-Betrieb. Ein einziger Nutzer (der Inhaber), Sprache Deutsch, keine Mehrbenutzer-Themen.
`KONZEPT.md` ist die abgestimmte Quelle — Änderungen daran nur mit dem Nutzer absprechen
und das Dokument mitpflegen. V1, V2 (KI) und der große Freiform-Umbau sind **fertig und
committet**; die App ist in täglicher Benutzung durch den Nutzer.

Arbeitsweise mit dem Nutzer: Features direkt umsetzen ("zieh durch"-Mentalität), auf
Deutsch antworten, jede abgeschlossene Einheit committen (deutsche Commit-Messages,
ASCII-Umlaute, Claude-Trailer), App nach jedem Build neu starten, damit er sofort testet.

## Aktueller Stand (was alles existiert)

- ✅ **Freiform-Canvas-Editor** (Commit `b7482fb`, revidiert das alte Blockmodell):
  Eine Notiz = EINE große `InkCanvas`-Fläche. Textfelder per Doppelklick frei platzierbar,
  Bilder/Dateien (png/jpg/pdf/xlsx/docx/md/txt) als verschieb-/skalierbare Objekte
  (+ Datei-Button, Drag&Drop), Tinte überall — auch über Objekten. PDFs mit
  Erste-Seite-Vorschau (`Windows.Data.Pdf`), Doppelklick öffnet Dateien. Fläche wächst
  automatisch. Papier-Muster (Blanko/Liniert/Kariert/Punkte) pro Notiz.
- ✅ **Handschrift → Text mit Farbvererbung:** Umwandlung erzeugt ein Textfeld an der
  Schreibstelle in der dominanten Stiftfarbe (Design-Automatikfarbe → null = Themefarbe).
  Anschluss-Heuristik hängt fortlaufendes Schreiben ans vorige Feld an. Lasso-Auswahl +
  „⬚→A"-Button wandelt gezielt um. Marker-Striche werden nie umgewandelt.
  Hintergrunderkennung (für Suche/KI) läuft immer.
- ✅ **KI-Chat** (Commit `b0a6b2b`): Panel rechts (💬-Bubble unten rechts / Button in der
  Werkzeugleiste), `claude -p --resume` im Docker-Container, Verlauf über Nachrichten
  hinweg. Checkbox „Aktuelle Notiz mitgeben" (nur Body!). Antworten per Knopf in die
  Notiz; erzeugte Dateien als Anhänge (📌 in Notiz / 💾 speichern).
- ✅ **Bildgenerierung 🎨** (Commit `b39d1ba`): läuft über die **lokale Codex-Desktop-App**
  des Nutzers (`codex exec`, read-only, imagegen-Skill des Codex-Abos — KEIN Docker,
  kein API-Key). App holt die PNGs aus `~/.codex/generated_images/<session-id>/`.
  Arbeitsteilung: Codex generiert Bilder, alles andere Claude.
- ✅ **Claude-Container kann zeichnen** (Commit `d4f45c7`): graphviz, matplotlib,
  librsvg (SVG→PNG), openpyxl (echte .xlsx). E2E getestet.
- ✅ **Farbsystem „Kupfer & Wasser"** (Commit `7aa027f`): `Services/Farbschema.cs` setzt
  semantische Brushes (AppText, AppTextLeise, AppFlaeche, AppFlaecheTief, AppRand,
  AppAkzent, AppAkzentLeise, AppKupfer, AppGlow*) beim Start + live bei Theme-Wechsel.
  Grund: SystemColors-Keys wechseln unterm Fluent-Theme nicht zuverlässig (schwarz auf
  schwarz). Signatur: zwei langsam treibende Licht-Schimmer im Hauptfenster-Hintergrund
  (Code: `MainWindow.StarteGlowAnimation`, aus bei `!SystemParameters.ClientAreaAnimation`).
- ✅ **UI-Verwaltung:** Seitenleisten einzeln einklappbar (Strg+B / Strg+Umschalt+B,
  schmale Rails, Zustand in settings.json); Fokus-Modus F11; Notizbücher per Rechtsklick
  umbenennen/löschen/färben (10 Farben, Punkt in Sidebar + Farbbalken in der Notizliste,
  Mapping in settings.json); Entf löscht markierte Notiz; Chat-Panel-Zustand persistiert.
- ✅ **WERKZEUGE-Tab** (Sidebar unter ANSICHTEN): kleine SHK-Rechner als `UserControl`s,
  über `WerkzeugListe` + Array `Werkzeuge` (MainWindow) per Index umgeschaltet. Bestand:
  Heizlast, Volumenstrom, Wasserinhalt, Ausdehnungsgefäß, Einheiten-Umrechner,
  Gerätewissen. Neues Tool = UserControl + Eintrag in `WerkzeugListe` (XAML) + Array +
  `ErgebnisEinfuegen`-Verdrahtung.
- ✅ **SHK-Aufnahme 📋** (Commit `9b9de6a`, 09.07.): siehe eigener Abschnitt unten.
- ⚠️ **Vom Nutzer noch nicht rückgemeldet:** Wie sich der neue Look in beiden Themes
  anfühlt (Glow zu stark/schwach?) und ob noch konkrete unlesbare Stellen existieren.

## SHK-Aufnahme-Werkzeug (neu, 09.07.2026)

Vollständiger **Bestandsaufnahme-Assistent** für die Baustelle — Kernprinzip (mehrfach vom
Nutzer geschärft): **nur erfassen, was man NUR vor Ort bekommt** (Mengen, Längen, Höhen,
Druck). Nennweiten und Büro-/Rechendaten macht **Viega Viptool Master** — nicht abfragen.
Ableiten statt doppelt erfassen. UI muss schlank & intuitiv sein („Zeit ist Geld").

- **Dateien:** `Controls/ErfassungTool.xaml(.cs)` (UI, einklappbare Expander, editierbare
  Karten), `Services/ErfassungService.cs` (Modell + `ErfassungStore` Persistenz +
  Kataloge/Formeln), `Services/TrinkwasserService.cs` (nur noch DU-Katalog +
  Spitzenvolumenstrom-Formel/Koeffizienten). **Namensraum `Erfassung*`, NICHT `Aufnahme*`**
  — `AufnahmeService` ist die Audio-Gesprächsaufnahme!
- **Persistenz:** mehrere benannte Vorhaben → `%APPDATA%\NotizApp\auslegung\aufnahme.json`,
  Autosave (debounced 800 ms) über `ErfassungBasis.Geaendert` + `PropertyChanged`.
- **Struktur:** ein `Erfassung`-Vorhaben = Szenario-Schalter (Bestand-Begehung / Neubau
  alle Daten / Neubau Teildaten) + gemeinsamer **Gebäude-Kopf** (inkl. Einspeisedruck) +
  je Gewerk:
  - **Trinkwasser:** `Wohneinheit` (×N-Multiplikator für gleiche Wohnungen) → dyn. `Ort`e
    (Bad/Küche/Gäste-WC, Vorlagen in `OrtVorlagen`) → gezählte Entnahmestellen
    (`Fixturen`: WT/Dusche/Wanne/WC/WM/GSW/Küchenspüle). Anschlussleitung je Wohneinheit
    (Länge/Höhe/Bögen/T-Stücke/Reduzierungen, Bestand DN/Material einklappbar). Live ΣVR +
    Vs = a·(ΣVR)^b−c (DIN 1988-300, verifizierte Koeffizienten je Gebäudeart).
  - **Abwasser:** leitet sich automatisch aus den TW-Entnahmestellen ab
    (`AbwasserAusWohneinheiten` → Qww = K·√ΣDU, floored auf größtes Einzel-DU); nur reine
    Abläufe ohne Zapfstelle (`AbwasserExtraKatalog`) werden ergänzt. K aus Nutzung.
  - **Gas:** Wärmeerzeuger wird aus der Heizung abgeleitet (wenn Erzeuger-Typ „Gas"),
    Zusatzgeräte manuell; Verbrennungsluft 1,6 m³/h·kW.
  - **Heizung:** Räume (L×B → überschlägige Heizlast Fläche×spez. Last), Fenster/Türen/
    Nischen einzeln als `Bauteil` (Art/Breite/Höhe/Bemerkung), Erzeuger/Netz, Abgleich A/B.
- **Verschachtelte Sammlungen** (Wohneinheit→Orte, Raum→Bauteile) sind
  `ObservableCollection`; das Tool abonniert Collection+Item-Changes und meldet sie über
  `MeldeGeaendert()` an den Parent → Autosave. Beim Vorhaben-Wechsel `LoeseBindung()`.
- **Export:** je Gewerk ein Markdown-Block in die aktuelle Notiz (`ErgebnisEinfuegen`).

**Verifikations-Trick (wichtig):** WPF-Template-Fehler crashen erst beim Sichtbarwerden
(nicht beim Build). Deshalb vor dem Ausliefern headless prüfen mit einem **net9-Rendertest**
(Wegwerf-Projekt mit `<ProjectReference>` auf NotizApp.csproj, `[STAThread]` → Control `new`en,
ItemsControls per `FindName` füllen, `Measure/Arrange/UpdateLayout`). Windows PowerShell 5.1
geht NICHT (lädt PresentationFramework 9.0 nicht). Lag im Session-Scratchpad unter `rendertest/`.
Klassischer Fehler war ein eigener Expander-Template mit `ContentPresenter ContentSource="Header"`
(löst gegen den ToggleButton auf) → stattdessen `Content="{TemplateBinding Header}"` + schlichter
`ContentPresenter`.

## Dateiformat (seit Freiform-Umbau)

- `.md` mit YAML-Frontmatter-Subset (titel/typ/erstellt/geaendert/tags/kunde/dringlichkeit).
- Body: ein ` ```tinte `-Fence (datei/hoehe/muster/text = erkannte Handschrift) + je Element
  ein Marker `<!--el text|bild|datei x= y= b= [h=] [farbe=] [datei="…"]-->`; bei text folgt
  der Inhalt bis zum nächsten Marker. Eine ISF pro Notiz: `<mdname>.tinte.isf`.
- **Altformat-Migration:** ` ```ink `-Fences (eigene `.t<n>.isf` je Block) werden beim Parsen
  gestapelt (`Note.AltTinten` mit Y-Versätzen); `NoteStore.LadeTinte` führt die Striche
  zusammen, `Speichere`/`Verschiebe` erzwingen die Migration vorher. Alte `.t*.isf` werden
  beim Speichern aufgeräumt (Pattern matcht auch `.tinte.isf` — `gueltig`-Check beachten!).
- Anhänge: `<mdname>.<originalname>` neben der .md; `Loesche`/`Verschiebe` nehmen alle
  Sidecars mit.

## Datenschutz-Regel (hart, mehrfach vom Nutzer bestätigt)

An die KI geht **ausschließlich** der Body (Textelemente in Leserichtung + erkannter
Handschrift-Text). Der komplette Frontmatter-Kopf **inklusive Titel** bleibt lokal
(Titel enthält oft Kundennamen — war kurz geleakt, Fix `51170a8`). Der Claude-Container
sieht den Notizen-Ordner nie (nur Login-Volume + leerer /ausgabe-Mount); Codex bekommt
nur den Auftragstext.

## Architektur-Landkarte

- `Models/Note.cs` — Note + NoteElement (TextElement/BildElement/DateiElement), AltTinte,
  Volltext/Vorschau/NotizbuchFarbBrush
- `Services/Frontmatter.cs` — Parser/Writer beider Formate + Migration
- `Services/NoteStore.cs` — Laden/Speichern/Verschieben/Löschen, Notizbuch-Verwaltung,
  Tinten-Migration
- `Services/KiService.cs` — Docker-Claude (FrageAsync/ErzeugeDokumentAsync/ChatAsync mit
  --resume + JSON-Parsing) und lokale Codex-CLI (FindeCodex/GeneriereBilderAsync)
- `Services/Farbschema.cs` — Farbsystem + IstDunkel(); `Services/NotizbuchFarben.cs` —
  statisches Notizbuch→Farbe-Mapping
- `Services/InkRecognitionService.cs` — WinRT-Handschrifterkennung (geometrie-basiert)
- `Services/TaskService.cs` — Checkboxen aus TextElementen (ElementIndex/ZeilenIndex)
- `Controls/NoteEditor.xaml(.cs)` — das Kernstück: Fläche, Werkzeuge, Element-Hosts
  (ContentControl + InkCanvas.Left/Top-Bindings, DataTemplates je VM), Erkennung,
  Umwandlung, KI-Menü
- `Controls/ElementVms.cs` — TextElementVm/BildElementVm/DateiElementVm + PapierMuster
- `Controls/KiChatPanel.xaml(.cs)` — Chat (➤ Claude / 🎨 Codex), Datei-Anhänge
- `MainWindow.xaml(.cs)` — Spalten inkl. Chat, Rails, Glow-Animation, Notizbuch-Menüs,
  WERKZEUGE-Liste + Array `Werkzeuge`
- `Controls/ErfassungTool.xaml(.cs)` + `Services/ErfassungService.cs` — SHK-Aufnahme
  (siehe eigener Abschnitt); `Services/TrinkwasserService.cs` — DU-Katalog + Vs-Formel;
  weitere Rechner unter `Controls/*Rechner.xaml`
- `docker/claude/Dockerfile` + `docker/einrichten.ps1` — Container (pandoc, weasyprint,
  graphviz, matplotlib, openpyxl, librsvg, curl)

## Umgebungs-Hinweise (erspart Debugging)

- **Build/Run:** `dotnet build NotizApp.sln` · EXE:
  `src\NotizApp\bin\Debug\net9.0-windows10.0.19041.0\NotizApp.exe`. Vor dem Build die
  laufende App beenden (`Get-Process NotizApp | Stop-Process -Force`) — sonst MSB3027,
  die App sitzt als Tray-App auch nach Fenster-Schließen im Prozess.
- **Commits:** mehrzeilige Messages in PowerShell 5.1 scheitern gern (Here-String/`&`) →
  Message in Datei schreiben und `git commit -F <datei>`.
- **Codex-CLI:** `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe` (nicht im PATH).
  `codex exec` wartet auf Stdin-EOF, wenn Stdin umgeleitet ist → leeren String übergeben
  und schließen. Sandbox bleibt praktisch read-only; Bilder landen trotzdem in
  `~/.codex/generated_images/<session-id>/` (Session-Id aus der exec-Kopfzeile parsen).
- **Docker:** Image `notizapp-claude`, Login-Volume `notizapp-claude-config`,
  Token optional als `%APPDATA%\NotizApp\claude.env`. `KiService.StelleDockerBereitAsync`
  startet Docker Desktop bei Bedarf selbst. Nach Dockerfile-Änderung:
  `docker build -t notizapp-claude docker\claude`.
- **Fluent-Theme-Falle:** niemals `SystemColors.*BrushKey` für neue UI verwenden —
  immer die App-Brushes aus `Farbschema.cs` (DynamicResource).
- Settings-Override für Tests: env `NOTIZAPP_APPDATA`.

## Bekannte Kanten / mögliche nächste Schritte

- **Alt-Tinte & Theme:** bereits gezeichnete Striche behalten ihre Farbe; schwarz
  Gezeichnetes ist im Dark Mode schlecht sichtbar (Umfärben markierter Striche wäre ein
  sinnvolles Feature: Lasso-Auswahl + Farbklick).
- **Migration alter Notizen** stapelt mit geschätzten Texthöhen — Feinlayout ggf. von Hand.
- **Chat-Verlauf** lebt nur pro App-Sitzung (Session-Id nicht persistiert) — Nutzer weiß
  das; Wiederherstellen beim Start wäre der nächste logische Ausbau (angeboten, noch
  nicht beauftragt).
- **Aufgaben-Ansicht:** Einfachklick öffnet die Quell-Notiz NICHT (nur Doppelklick);
  Verbesserung + „+ Aufgabe"-Knopf wurden besprochen, Nutzer hat noch nicht entschieden.
- **Fotorealistische Bilder** laufen über Codex (🎨). Falls der Nutzer mehr Kontrolle will:
  OpenAI-API (gpt-image-1) als Zusatzdienst — nur nach Rückfrage (Kosten).
- V1.1 laut Roadmap: Export (PDF/Druck), Feinschliff Vorlagen.
- **SHK-Aufnahme — als Nächstes gewünscht (noch offen):** **Notiz ↔ Aufnahme-Vorhaben
  verlinken**, damit Raumskizzen/Fotos als Notiz am Vorhaben hängen (der Nutzer skizziert
  Räume/Nischen lieber als Bild in einer Notiz). Weitere offene Ideen: Hersteller-DU-Lookup
  fürs Trinkwasser (Feld ist da, Auto-Fill fehlt), Heizkörper als eigenes Sub-Modell statt
  Freitext, echte DIN-1988-300-Vs-Koeffizienten sind für alle 5 Gebäudearten verifiziert
  (Wohngeb./Pflegeheim/Krankenhaus/Hotel/Schule) — für weitere Gebäudearten „Eigene Werte".
- **SHK-Aufnahme — Arbeitsprinzip:** Feldliste vor größerem Bau mit dem Nutzer gegenprüfen
  (er ist Fachmann). Historie: 1. Wurf zu dünn (nur DU-Summe), 2. zu überladen (Bürodaten
  mit drin) — Mitte treffen: nötigste Vor-Ort-Werte, gut geführt. Er testet sofort und
  meldet direkt ("gefällt mir nicht" = Arbeitsauftrag).

## Nutzer-Präferenzen (Kurzfassung)

Deutsch; direkt umsetzen statt lange fragen (bei echten Architektur-Weichen kurz fragen —
AskUserQuestion hat sich bewährt); Datenschutz-Trennung ernst nehmen; er testet selbst
sofort in der laufenden App und meldet Eindrücke ("gefällt mir nicht" = ernst gemeinter
Arbeitsauftrag). Memory-Dateien unter `~/.claude/projects/D--Mitra-NotizApp-Win/memory/`
sind aktuell (u.a. werkzeuge-tab, aufnahme-vollstaendigkeit, wpf-control-rendertest,
ui-vorlieben) — MEMORY.md ist der Index. Projektpfad ist jetzt `D:\Mitra\NotizApp_Win`
(nicht mehr `C:\Dev`); Release-Installation über `.\aktualisieren.ps1`
(→ `%LOCALAPPDATA%\NotizApp`, beendet + startet die App).
