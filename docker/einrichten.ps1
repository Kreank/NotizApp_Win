# Einmalige Einrichtung des Claude-Containers fuer die NotizApp-KI-Funktionen.
# Ausfuehren in PowerShell:  .\docker\einrichten.ps1
# Voraussetzung: Docker Desktop laeuft; Claude Code ist auf dem PC installiert.

$ErrorActionPreference = 'Stop'
$hier = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "[1/3] Baue Docker-Image 'notizapp-claude'..." -ForegroundColor Cyan
docker build -t notizapp-claude (Join-Path $hier 'claude')
if ($LASTEXITCODE -ne 0) { throw "Docker-Build fehlgeschlagen." }

Write-Host "[2/3] Lege Konfigurations-Volume an..." -ForegroundColor Cyan
docker volume create notizapp-claude-config | Out-Null

Write-Host "[3/3] Anmelde-Token erzeugen (einmalig)." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Gleich laeuft 'claude setup-token' hier auf dem PC:" -ForegroundColor Yellow
Write-Host "   1. Der Browser oeffnet sich automatisch -> mit deinem Claude-Konto freigeben"
Write-Host "   2. Am Ende zeigt das Terminal einen langen Token (sk-ant-oat01-...)"
Write-Host "   3. Den Token kopieren - er wird gleich hier abgefragt"
Write-Host ""
Read-Host "Enter druecken, um zu starten"

claude setup-token

Write-Host ""
$token = (Read-Host "Token hier einfuegen (sk-ant-oat01-...)").Trim()
if ($token -notmatch '^sk-ant-') {
    throw "Das sieht nicht wie ein Claude-Token aus (muss mit sk-ant- beginnen). Bitte Skript erneut ausfuehren."
}

# Token als Env-Datei fuer 'docker run --env-file' ablegen (nur lokal, nicht im Repo)
$ordner = Join-Path $env:APPDATA 'NotizApp'
New-Item -ItemType Directory -Force $ordner | Out-Null
$envDatei = Join-Path $ordner 'claude.env'
[IO.File]::WriteAllText($envDatei, "CLAUDE_CODE_OAUTH_TOKEN=$token")
Write-Host "Token gespeichert: $envDatei"

Write-Host ""
Write-Host "Teste die Anmeldung..." -ForegroundColor Cyan
$antwort = 'Sag nur das Wort OK.' | docker run -i --rm --env-file $envDatei -v notizapp-claude-config:/home/claude notizapp-claude -p --output-format text
if ($LASTEXITCODE -eq 0 -and $antwort) {
    Write-Host "Fertig! Claude antwortet: $antwort" -ForegroundColor Green
    Write-Host "Die KI-Funktionen in der NotizApp sind jetzt nutzbar (KI-Button im Editor)."
} else {
    Write-Host "Test fehlgeschlagen - bitte Skript erneut ausfuehren." -ForegroundColor Red
}
