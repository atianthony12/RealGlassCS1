# Downloads the ready-made models of models.txt (Poly Haven, CC0) that are not cached yet: each one's
# 1k glTF with its .bin and textures, into %LOCALAPPDATA%\RealGlass-build\models\<slug>\ -- outside the
# repo, as build-rooms.ps1 does with the panoramas. render-offices.py imports them from there.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\interiors\fetch-models.ps1

$ErrorActionPreference = "Stop"
$list = Join-Path $PSScriptRoot "models.txt"
$root = Join-Path $env:LOCALAPPDATA "RealGlass-build\models"
New-Item -ItemType Directory -Force $root | Out-Null
$agent = "RealGlass-build"

$slugs = Get-Content $list | ForEach-Object { ($_ -split "#")[0].Trim() } | Where-Object { $_ }
foreach ($s in $slugs) {
    $dir = Join-Path $root $s
    $gltf = Join-Path $dir "$s.gltf"
    if (Test-Path $gltf) { continue }
    Write-Host "downloading $s"
    $info = Invoke-RestMethod -UserAgent $agent -Uri "https://api.polyhaven.com/files/$s"
    $g = $info.gltf.'1k'.gltf
    if ($null -eq $g) { throw "$s has no 1k glTF" }
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($p in $g.include.PSObject.Properties) {
        $to = Join-Path $dir ($p.Name -replace '/', '\')
        New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
        Invoke-WebRequest -UseBasicParsing -UserAgent $agent -Uri $p.Value.url -OutFile $to
    }
    # Last: a half-downloaded model has no .gltf and is fetched again.
    Invoke-WebRequest -UseBasicParsing -UserAgent $agent -Uri $g.url -OutFile "$gltf.part"
    Move-Item "$gltf.part" $gltf
}
Write-Host "models: $($slugs.Count) in $root"
