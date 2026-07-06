# Einmalige Einrichtung des Claude-Containers fuer die NotizApp-KI-Funktionen.
# Ausfuehren in PowerShell:  .\docker\einrichten.ps1
# Voraussetzung: Docker Desktop laeuft.

$ErrorActionPreference = 'Stop'
$hier = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "[1/3] Baue Docker-Image 'notizapp-claude'..." -ForegroundColor Cyan
docker build -t notizapp-claude (Join-Path $hier 'claude')
if ($LASTEXITCODE -ne 0) { throw "Docker-Build fehlgeschlagen." }

Write-Host "[2/3] Lege Konfigurations-Volume an..." -ForegroundColor Cyan
docker volume create notizapp-claude-config | Out-Null

Write-Host "[3/3] Anmeldung mit deinem Claude-Konto (einmalig)." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Gleich startet Claude im Container. Dort:" -ForegroundColor Yellow
Write-Host "   1. Der Anmelde-Dialog erscheint automatisch (sonst: /login eingeben)"
Write-Host "   2. Den angezeigten Link im Browser oeffnen, anmelden, Code zurueckkopieren"
Write-Host "   3. Danach mit /exit beenden"
Write-Host ""
Read-Host "Enter druecken, um zu starten"

docker run -it --rm -v notizapp-claude-config:/home/claude notizapp-claude

Write-Host ""
Write-Host "Teste die Anmeldung..." -ForegroundColor Cyan
$antwort = 'Sag nur das Wort OK.' | docker run -i --rm -v notizapp-claude-config:/home/claude notizapp-claude -p --output-format text
if ($LASTEXITCODE -eq 0 -and $antwort) {
    Write-Host "Fertig! Claude antwortet: $antwort" -ForegroundColor Green
    Write-Host "Die KI-Funktionen in der NotizApp sind jetzt nutzbar (KI-Button im Editor)."
} else {
    Write-Host "Test fehlgeschlagen - bitte Skript erneut ausfuehren und Anmeldung wiederholen." -ForegroundColor Red
}
