# Einmalige Einrichtung des Codex-Containers fuer die Bildgenerierung der NotizApp.
# Ausfuehren in PowerShell:  .\docker\codex\einrichten.ps1
# Voraussetzung: Docker Desktop laeuft; die Codex-App (OpenAI) ist auf dem PC
# mit deinem Abo angemeldet (Datei %USERPROFILE%\.codex\auth.json vorhanden).

$ErrorActionPreference = 'Stop'
$hier = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "[1/4] Baue Docker-Image 'notizapp-codex'..." -ForegroundColor Cyan
docker build -t notizapp-codex $hier
if ($LASTEXITCODE -ne 0) { throw "Docker-Build fehlgeschlagen." }

Write-Host "[2/4] Lege Konfigurations-Volume an..." -ForegroundColor Cyan
docker volume create notizapp-codex-config | Out-Null

Write-Host "[3/4] Abo-Login in den Container uebernehmen..." -ForegroundColor Cyan
$authHost = Join-Path $env:USERPROFILE '.codex\auth.json'
if (-not (Test-Path $authHost)) {
    throw "Kein Codex-Login gefunden ($authHost).`n" +
          "Bitte zuerst die Codex-App (OpenAI) installieren und mit deinem Abo anmelden, " +
          "dann dieses Skript erneut ausfuehren."
}
# auth.json als read-only Einzeldatei ins Volume kopieren (als root, dann codex-Rechte setzen)
docker run --rm --user root --entrypoint sh `
    -v notizapp-codex-config:/dest -v "${authHost}:/src/auth.json:ro" notizapp-codex `
    -c "mkdir -p /dest/.codex && cp /src/auth.json /dest/.codex/auth.json && chown -R codex:codex /dest/.codex && chmod 600 /dest/.codex/auth.json"
if ($LASTEXITCODE -ne 0) { throw "Login-Kopie fehlgeschlagen." }

Write-Host "[4/4] Teste die Anmeldung..." -ForegroundColor Cyan
$antwort = '' | docker run -i --rm -v notizapp-codex-config:/home/codex notizapp-codex `
    exec -C /home/codex/arbeit --skip-git-repo-check -s read-only "Antworte nur mit dem Wort OK."
if ($LASTEXITCODE -eq 0) {
    Write-Host "Fertig! Der Codex-Container ist einsatzbereit." -ForegroundColor Green
    Write-Host "Die Bildgenerierung (🎨 im KI-Chat) laeuft jetzt ueber den Container."
} else {
    Write-Host "Test fehlgeschlagen - bitte pruefen, ob auth.json aktuell ist (Codex-App neu anmelden) " -ForegroundColor Red
    Write-Host "und das Skript erneut ausfuehren." -ForegroundColor Red
}
