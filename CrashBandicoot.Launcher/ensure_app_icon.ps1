# Builds app.ico from repo-root icon.png (PNG-in-ICO, Vista+). Used by the csproj BeforeBuild.
param(
    [Parameter(Mandatory = $true)][string]$PngPath,
    [Parameter(Mandatory = $true)][string]$IcoPath
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PngPath)) {
    Write-Host "[icon] skip: missing $PngPath"
    exit 0
}

$need = $true
if (Test-Path -LiteralPath $IcoPath) {
    $need = (Get-Item -LiteralPath $PngPath).LastWriteTimeUtc -gt (Get-Item -LiteralPath $IcoPath).LastWriteTimeUtc
}
if (-not $need) {
    Write-Host "[icon] up to date: $IcoPath"
    exit 0
}

$png = [System.IO.File]::ReadAllBytes($PngPath)
if ($png.Length -lt 24 -or $png[0] -ne 0x89) { throw "not a PNG: $PngPath" }

# IHDR width/height are big-endian at offset 16
$w = ([uint32]$png[16] -shl 24) -bor ([uint32]$png[17] -shl 16) -bor ([uint32]$png[18] -shl 8) -bor [uint32]$png[19]
$h = ([uint32]$png[20] -shl 24) -bor ([uint32]$png[21] -shl 16) -bor ([uint32]$png[22] -shl 8) -bor [uint32]$png[23]
$wb = if ($w -ge 256) { 0 } else { [byte]$w }
$hb = if ($h -ge 256) { 0 } else { [byte]$h }
$offset = 6 + 16

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]1)
$bw.Write([byte]$wb)
$bw.Write([byte]$hb)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([uint16]1)
$bw.Write([uint16]32)
$bw.Write([uint32]$png.Length)
$bw.Write([uint32]$offset)
$bw.Write($png)
$bw.Flush()

$dir = Split-Path -Parent $IcoPath
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($IcoPath, $ms.ToArray())
Write-Host "[icon] $PngPath (${w}x${h}) -> $IcoPath"
