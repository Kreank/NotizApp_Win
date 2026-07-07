# Wandelt ein quadratisches PNG in eine Windows-ICO mit allen gängigen Größen um.
# Aufruf: .\png-zu-ico.ps1 -Quelle logo.png -Ziel app.ico
param(
    [Parameter(Mandatory = $true)][string]$Quelle,
    [Parameter(Mandatory = $true)][string]$Ziel
)

Add-Type -AssemblyName System.Drawing

$groessen = 16, 24, 32, 48, 64, 128, 256
$quellBild = [System.Drawing.Image]::FromFile((Resolve-Path $Quelle))
$eintraege = @()

foreach ($g in $groessen) {
    $bmp = New-Object System.Drawing.Bitmap($g, $g)
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gr.DrawImage($quellBild, 0, 0, $g, $g)
    $gr.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $eintraege += , @($g, $ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}
$quellBild.Dispose()

# ICO-Container schreiben (PNG-Einträge sind seit Vista erlaubt)
$fs = [System.IO.File]::Create($Ziel)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                  # reserviert
$bw.Write([uint16]1)                  # Typ: Icon
$bw.Write([uint16]$eintraege.Count)   # Anzahl
$offset = 6 + 16 * $eintraege.Count
foreach ($e in $eintraege) {
    $g = $e[0]; $daten = $e[1]
    $b = if ($g -ge 256) { 0 } else { $g }
    $bw.Write([byte]$b)               # Breite (0 = 256)
    $bw.Write([byte]$b)               # Höhe
    $bw.Write([byte]0)                # Palette
    $bw.Write([byte]0)                # reserviert
    $bw.Write([uint16]1)              # Farbebenen
    $bw.Write([uint16]32)             # Bits pro Pixel
    $bw.Write([uint32]$daten.Length)  # Datenlänge
    $bw.Write([uint32]$offset)        # Datenversatz
    $offset += $daten.Length
}
foreach ($e in $eintraege) { $bw.Write($e[1]) }
$bw.Close()
Write-Host "ICO geschrieben: $Ziel ($($eintraege.Count) Größen)"
