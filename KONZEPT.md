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

**Handschrift:** Tintenstriche werden als `.isf`-Datei (Ink Serialized Format) neben
der `.md`-Datei gespeichert und im Markdown referenziert. Der per Erkennung
umgewandelte Text steht direkt im Markdown-Body.

**Speicherort:** Frei wählbarer Ordner (Einstellung). Liegt er in OneDrive oder auf
einem Netzlaufwerk, gibt es Synchronisation "gratis".

## 4. Editor: Tastatur + Stift

Eine Notiz besteht aus einer Folge von **Blöcken**:

- **Textblock** — normale Tastatureingabe (Markdown: Überschriften, Listen, Checkboxen)
- **Tintenblock** — `InkCanvas`-Fläche für Handschrift und Skizzen

Stift-Verhalten (wie heute Standard):

- **Umwandlungs-Button in der Werkzeugleiste** ("Handschrift → Text"):
  - **Aktiv:** Geschriebenes wird erkannt und als Tipptext in die Notiz eingefügt
  - **Inaktiv:** Tinte bleibt Tinte — wie auf Papier
- Handschrift, die Tinte bleibt, wird **im Hintergrund trotzdem erkannt** und der
  Text unsichtbar für Suche + KI mitgespeichert
- Skizzen/Diagramme bleiben immer Tinte (keine Zwangsumwandlung)
- Werkzeuge: Stift (Farbe/Dicke), Textmarker, Radierer, Lasso-Auswahl

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

## 8. KI-Aufbereitung (V2)

- Claude läuft über die vorhandene Subscription in einem **Docker-Container**,
  angesprochen als headless Session (`claude -p …`)
- Die App sendet **nur den Notiz-Body ohne Frontmatter-Kopf**
- Geplante Funktionen: Zusammenfassen, Text glätten/aufarbeiten, Aufgaben extrahieren
- Ergebnis wird als Vorschlag angezeigt und erst nach Bestätigung übernommen

## 9. Erscheinungsbild

- Win11 Fluent Design (Mica-Optik, abgerundete Ecken)
- **Hell/Dunkel folgt automatisch der Windows-Einstellung**

## 10. Roadmap

| Version | Inhalt |
|---|---|
| **V1** | Grundgerüst: Notizbücher/Tags, Editor mit Text- und Tintenblöcken, Handschrift→Text-Button, Hintergrund-Erkennung, Speichern als Markdown+ISF, Volltextsuche, Vorlagen, Schnellerfassung (Tray + Hotkey + Autostart) |
| **V1.1** | Zentrale Aufgabenliste mit Fälligkeiten, Feinschliff Vorlagen, Export (PDF/Druck) |
| **V2** | KI-Integration (Docker/Claude): Zusammenfassen, Aufbereiten, Aufgaben extrahieren |

## 11. Offene Punkte / Risiken

- **Handschrifterkennung Deutsch** setzt das Windows-Sprachpaket "Handschrift"
  voraus — beim ersten Start prüfen und ggf. Hinweis anzeigen
- Der gemischte Block-Editor (Text + Tinte) ist der aufwendigste Teil der App —
  V1 startet bewusst mit einem einfachen Blockmodell (Tintenblock = feste Fläche,
  erweiterbar), statt ein OneNote-Freiform-Canvas nachzubauen
- Später denkbar: Screenshots einfügen und mit Stift markieren (Workflow "Skizzen")
