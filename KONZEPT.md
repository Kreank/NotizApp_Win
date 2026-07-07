# NotizApp — Konzept

Native Windows-11-Notiz-App für den Arbeitsalltag in einem SHK-Betrieb.
Eingabe per Tastatur **und** Handschrift (Stift), KI-Aufbereitung der Inhalte in V2.

Stand: 06.07.2026 — abgestimmt im Anforderungsgespräch.

---

## 1. Technologie

| Bereich | Entscheidung | Begründung |
|---|---|---|
| Sprache / Runtime | **C# / .NET 9** | Wunsch des Nutzers, native MS-Technologie |
| UI-Framework | **WPF** mit .NET-9-Fluent-Theme | Eingebautes `InkCanvas` für Windows Ink; Fluent-Theme liefert Win11-Look mit automatischem Hell/Dunkel |
| Stifteingabe | `InkCanvas` / `StrokeCollection` (Windows Ink) | Nativer Stift-Support inkl. Druckstufen, Radierer, Lasso |
| Handschrifterkennung | Windows-Ink-Erkennung (`InkRecognizerContainer` via CsWinRT, Sprache Deutsch) | Nutzt die eingebaute Windows-Erkennung; **Voraussetzung:** deutsches Handschrift-Sprachpaket in Windows installiert |
| Suche & Aufgaben-Index | **In-Memory** (beim Start werden alle .md geparst) | Bei ein paar tausend Notizen völlig ausreichend; kein DB-Code, keine Sync-Probleme. SQLite erst, falls es später spürbar langsam wird |
| Datenhaltung | Markdown-Dateien + ISF-Dateien in frei wählbarem Ordner | Siehe Abschnitt 3 |

## 2. Organisation der Notizen

**Kombination aus Ordnerstruktur und Tags:**

- **Notizbücher** = Unterordner im Datenordner (z.B. `Kunden-Anrufe`, `Meetings`, `Nachschlagewerk`)
- **Tags** frei vergebbar pro Notiz (z.B. `#notfall`, `#heizung`, `#angebot`), gespeichert im Notizkopf
- Seitenleiste: Notizbuch-Baum + Tag-Liste + Suchfeld

## 3. Datenformat (KI-freundlich, Kopf/Inhalt-Trennung)

Jede Notiz ist eine **Markdown-Datei mit YAML-Frontmatter-Kopf**:

```markdown
---
titel: Anruf Fr. Müller — Heizung fällt aus
typ: anruf
erstellt: 2026-07-06T09:41
tags: [notfall, heizung]
kunde:
  name: Müller, Sabine
  telefon: 0171 2345678
  adresse: Musterweg 3, 12345 Stadt
dringlichkeit: notfall
---

Heizung (Vaillant, ca. 2015) heizt seit gestern Abend nicht mehr.
Fehlercode F.28. Kunde hat bereits Wasser nachgefüllt.

- [ ] Rückruf wegen Terminvereinbarung @2026-07-07
```

**Datenschutz-Konzept für die KI (V2):** Der Frontmatter-Kopf enthält alle
personenbezogenen Daten. Bei der Übergabe an die KI wird **nur der Body unterhalb
des Kopfes** gesendet — Kundendaten verlassen den Rechner nie.

**Handschrift:** Die Tinte der ganzen Notiz-Fläche liegt in **einer**
`.tinte.isf`-Datei (Ink Serialized Format) neben der `.md`-Datei und wird im
Markdown referenziert. Der per Erkennung umgewandelte Text steht als Textelement
direkt im Markdown-Body; frei platzierte Elemente werden über unsichtbare
`<!--el …-->`-Marker (Position/Größe/Farbe) beschrieben — der Body bleibt
dadurch les- und durchsuchbares Markdown. Notizen im alten Blockformat
(```ink-Fences mit eigenen ISF-Dateien) werden beim Öffnen automatisch migriert.

**Speicherort:** Frei wählbarer Ordner (Einstellung). Liegt er in OneDrive oder auf
einem Netzlaufwerk, gibt es Synchronisation "gratis".

## 4. Editor: Freiform-Fläche (Tastatur + Stift auf einer Oberfläche)

Eine Notiz ist **eine große Fläche** (Freiform-Canvas, wie ein Blatt Papier):

- **Textfelder** — per Doppelklick überall setzbar; normale Tastatureingabe
  (Markdown: Überschriften, Listen, Checkboxen), frei verschiebbar, Breite ziehbar
- **Handschrift/Skizzen** — der Stift schreibt überall auf der Fläche, auch
  über Objekten; Papier-Muster (Blanko/Liniert/Kariert/Punkte) pro Notiz
- **Bilder und Dateien** (png, jpg, pdf, xlsx, docx, md, txt) — per „+ Datei",
  Drag&Drop oder aus der KI; liegen als **frei verschieb- und skalierbare
  Objekte** auf der Fläche, Draufzeichnen inklusive. PDFs zeigen die erste
  Seite als Vorschau, Doppelklick öffnet die Datei
- Die Fläche wächst beim Schreiben automatisch nach unten mit

Stift-Verhalten:

- **Umwandlungs-Button in der Werkzeugleiste** ("Handschrift → Text"):
  - **Aktiv:** Geschriebenes wird erkannt und als Textfeld **an der
    Schreibstelle** eingefügt — **in der Stiftfarbe**
  - **Inaktiv:** Tinte bleibt Tinte — wie auf Papier
- **Lasso-Auswahl + „⬚→A"**: markierte Handschrift gezielt in Text umwandeln
  (Position und Stiftfarbe bleiben erhalten)
- Handschrift, die Tinte bleibt, wird **im Hintergrund trotzdem erkannt** und der
  Text unsichtbar für Suche + KI mitgespeichert
- Skizzen/Diagramme und Marker-Striche bleiben immer Tinte (keine Zwangsumwandlung)
- Werkzeuge: Auswählen/Tippen, Stift (Farbe/Dicke), Textmarker, Radierer
  (Strich/punktgenau), Lasso-Auswahl

## 5. Schnellerfassung (Kundenanrufe)

- **Autostart mit Windows + Tray-Icon + globaler Hotkey** (Standard `Strg+Alt+N`, konfigurierbar)
- Hotkey öffnet ein kompaktes Schnellnotiz-Fenster — sofort schreibbereit
- **Vorlagen wählbar:** Anruf, Meeting, Aufgabe, Leer
  - **Anruf-Vorlage:** Felder für Kunde, Telefon, Anliegen, Dringlichkeit (Notfall / Termin / Info / Terminierung) + Freitext/Tinte
  - **Meeting-Vorlage:** Datum, Teilnehmer, Stichpunkte
- Schnellnotiz landet automatisch im passenden Notizbuch (konfigurierbar)

## 6. Aufgaben

- **Checklisten in Notizen:** Markdown-Checkboxen `- [ ]`, abhakbar per Klick/Stift
- **Fälligkeitsdatum** per `@JJJJ-MM-TT` hinter der Aufgabe
- **Zentrale Aufgabenansicht:** sammelt alle offenen Aufgaben aus allen Notizen,
  sortiert nach Fälligkeit, mit Sprung zur Quell-Notiz

## 7. Suche

Volltextsuche über:

- Tipptext aller Notizen
- **erkannten Text aus Handschrift** (Hintergrund-Erkennung, s. Abschnitt 4)
- Titel, Tags, Kundenname im Kopf

## 8. KI-Aufbereitung (V2) — umgesetzt

- Claude läuft über die vorhandene Subscription in einem **Docker-Container**
  (Image `notizapp-claude`, einmalige Einrichtung: `.\docker\einrichten.ps1`),
  angesprochen als headless Session (`claude -p …`, Body via stdin)
- Die App sendet **nur den Notiz-Body ohne Frontmatter-Kopf**; Tinten-Blöcke
  werden durch ihren erkannten Text ersetzt. Der Container hat **keinen
  Zugriff auf den Notizen-Ordner** (nur das Login-Volume ist gemountet)
- Funktionen (✨-Button im Editor): Zusammenfassen, Text aufbereiten,
  Aufgaben extrahieren
- Ergebnis wird als Vorschlag angezeigt (editierbar) und erst nach
  Bestätigung übernommen
- **KI-Chat** (💬-Button, Panel rechts neben dem Editor): freie Unterhaltung
  mit Claude über mehrere Nachrichten (Session-Fortsetzung im Container).
  Optional wird der Body der offenen Notiz mitgegeben („Sieh dir die Notiz an
  und such mir Quellen zu … raus"). Claude recherchiert im Internet mit
  Quellen-URLs, lädt Bilder/Diagramme und erstellt Dateien (PDF/Word/HTML/
  Markdown/CSV) in einem isolierten Austauschordner. Antworten lassen sich
  per Knopf in die Notiz einfügen; Dateien als Objekt auf die Fläche legen
  (📌) oder auf dem PC speichern (💾)
- **Dateien erstellen/suchen** (✨ → „Dateien erstellen/suchen…"): freier
  Auftrag; Claude arbeitet mit Schreibrechten + Internet in einem leeren
  Austauschordner (`/ausgabe`-Mount, pandoc/weasyprint/curl im Image).
  Übernahme nach Bestätigung: alle Dateien landen als **Objekte auf der
  Fläche** (Bilder direkt zeichenbar, PDFs mit Seitenvorschau)

## 9. Erscheinungsbild

- Win11 Fluent Design (Mica-Optik, abgerundete Ecken)
- **Hell/Dunkel folgt automatisch der Windows-Einstellung**

## 10. Roadmap

| Version | Inhalt |
|---|---|
| **V1** | Grundgerüst: Notizbücher/Tags, Editor mit Text- und Tintenblöcken, Handschrift→Text-Button, Hintergrund-Erkennung, Speichern als Markdown+ISF, Volltextsuche, Vorlagen, Schnellerfassung (Tray + Hotkey + Autostart) |
| **V1.5** | Freiform-Editor: eine Fläche pro Notiz, Textfelder/Bilder/Dateien als frei platzierbare Objekte, Umwandlung an Ort und Stelle in Stiftfarbe, Lasso→Text, automatische Migration alter Block-Notizen |
| **V1.1** | Zentrale Aufgabenliste mit Fälligkeiten, Feinschliff Vorlagen, Export (PDF/Druck) |
| **V2** | KI-Integration (Docker/Claude): Zusammenfassen, Aufbereiten, Aufgaben extrahieren |

## 11. Offene Punkte / Risiken

- **Handschrifterkennung Deutsch** setzt das Windows-Sprachpaket "Handschrift"
  voraus — beim ersten Start prüfen und ggf. Hinweis anzeigen
- ~~V1 startet bewusst mit einem einfachen Blockmodell statt OneNote-Freiform-Canvas~~
  → revidiert (Anforderung 07/2026): der Editor **ist** jetzt ein Freiform-Canvas;
  alte Block-Notizen werden beim Öffnen/Speichern automatisch migriert
- ~~Später denkbar: Screenshots einfügen und mit Stift markieren~~ → umgesetzt:
  Bilder/Dateien liegen als Objekte auf der Fläche (Draufzeichnen inklusive)
- Freiform-Grenze: Migrierte Alt-Notizen werden näherungsweise gestapelt
  (geschätzte Texthöhen) — Feinlayout ggf. einmalig von Hand nachschieben
