# NotizApp aktualisieren: laufende App beenden -> Release bauen ->
# nach %LOCALAPPDATA%\NotizApp installieren -> App wieder starten.
# Verknüpfungen und Autostart zeigen auf diesen festen Ort und bleiben gültig.
$ErrorActionPreference = 'Stop'
$projekt = Join-Path $PSScriptRoot 'src\NotizApp\NotizApp.csproj'
$ziel = Join-Path $env:LOCALAPPDATA 'NotizApp'

Write-Host '1/4  NotizApp beenden...'
$p = Get-Process NotizApp -ErrorAction SilentlyContinue
if ($p) {
    $p.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 2
    $p = Get-Process NotizApp -ErrorAction SilentlyContinue
    if ($p) { Stop-Process -InputObject $p -Force }
    Start-Sleep -Seconds 1
}

Write-Host '2/4  Release bauen...'
dotnet publish $projekt -c Release -o $ziel --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen - App wurde nicht aktualisiert.' }

Write-Host '3/4  Installiert nach ' $ziel

Write-Host '4/4  NotizApp starten...'
Start-Process (Join-Path $ziel 'NotizApp.exe')
Write-Host 'Fertig.'
