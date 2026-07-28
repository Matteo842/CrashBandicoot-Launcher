Add-Type -AssemblyName System.Drawing

function New-CheckerPng {
    param(
        [string]$Path,
        [int]$Size,
        [Drawing.Color]$ColorA,
        [Drawing.Color]$ColorB,
        [int]$Cell
    )
    $bmp = New-Object Drawing.Bitmap $Size, $Size
    for ($y = 0; $y -lt $Size; $y++) {
        for ($x = 0; $x -lt $Size; $x++) {
            $useA = ((([int]($x / $Cell)) + ([int]($y / $Cell))) % 2) -eq 0
            $bmp.SetPixel($x, $y, $(if ($useA) { $ColorA } else { $ColorB }))
        }
    }
    $dir = Split-Path $Path
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "wrote $Path"
}

$root = Join-Path $PSScriptRoot "textures"
New-CheckerPng (Join-Path $root "demo_tile_a.png") 64 ([Drawing.Color]::FromArgb(255,255,0,255)) ([Drawing.Color]::FromArgb(255,40,0,40)) 8
New-CheckerPng (Join-Path $root "demo_tile_b.png") 64 ([Drawing.Color]::FromArgb(255,0,220,255)) ([Drawing.Color]::FromArgb(255,0,40,60)) 8
