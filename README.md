# NotizApp_Win

Notiz-App für einen SHK-Handwerksbetrieb (Windows, WPF/.NET 9). Jede Notiz ist
eine freie Fläche: Handschrift (Stift), Tipptext, Tabellen, Bilder, PDFs und
Web-Links liegen als frei platzierbare Objekte auf einem Canvas — gespeichert
als offenes Markdown mit Frontmatter plus einer Tinten-ISF daneben.

## Highlights

- **Stift zuerst:** Handschrift überall, automatische Erkennung zu Tipptext,
  Formen-Werkzeug (Rechtecke, Kreise, Pfeile, Linien), Stift-Seitentaste radiert
- **Dashboard:** Monatskalender mit Terminen aus den Notizen, News-Feed
  (KI + SHK-Branche) per KI-Recherche
- **KI-Anbindung:** 
  Zusammenfassen, Berichte/Dateien erstellen, Chat mit Notiz-Kontext.
  Kundendaten-Kopf und Titel verlassen den PC nie; Anhänge nur per Opt-in
- **Bildgenerierung**
- **Sprach-Transkription**
- **Suche:** Volltext über Notizen, erkannte Handschrift, PDF-Inhalte und
  Bildtexte (Windows-OCR)
- Notizbücher mit Unterordnern, eigene Vorlagen, Schnellerfassung per
  globalem Hotkey (Strg+Alt+N), Tray-App

## Entwicklung

```powershell
dotnet build src/NotizApp/NotizApp.csproj      # bauen (Debug)
.\aktualisieren.ps1                            # Release nach %LOCALAPPDATA%\NotizApp installieren
```

Konzept und Fahrplan: siehe `KONZEPT.md`.
