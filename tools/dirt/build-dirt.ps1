# Makes the dirt on the glass (Glass/DirtTextures.cs, Dirt.cginc): RealGlass\Resources\dirt-grime.bytes
# and dirt-runs.bytes, from three of ambientCG's CC0 maps. Run it after changing the maps, then rebuild
# the mod (the DLL embeds both files).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\dirt\build-dirt.ps1
#
# 1. Downloads each map's 1K PNG set that is not cached yet from ambientCG (CC0 1.0,
#    https://ambientcg.com/license) into %LOCALAPPDATA%\RealGlass-build\dirt -- outside the repo -- and
#    takes its Opacity map: Surface Imperfections 003 (the film), Surface Imperfections 001 (water
#    spots), Leaking 004 (rain's runs).
# 2. Unity 5.6 (headless, WITH graphics: it checks the result on the GPU) runs Editor\DirtBaker.cs:
#    grime 1024 x 1024 DXT5 (a film, g spots) and runs 1024 x 512 Alpha8, mips box-filtered, linear;
#    then loads both with the mod's own loader, reads them back from the GPU and compares.

param(
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$cache = Join-Path $env:LOCALAPPDATA "RealGlass-build\dirt"
$project = Join-Path $root "UnityProject"
$resources = Join-Path $root "RealGlass\Resources"
New-Item -ItemType Directory -Force $cache | Out-Null

# 1. The maps.
$maps = [ordered]@{ FILM = "SurfaceImperfections003"; SPOTS = "SurfaceImperfections001"; RUNS = "Leaking004" }
$ProgressPreference = "SilentlyContinue"
foreach ($key in $maps.Keys) {
    $id = $maps[$key]
    $zip = Join-Path $cache "${id}_1K-PNG.zip"
    $dir = Join-Path $cache "${id}_1K-PNG"
    if (-not (Test-Path $zip)) {
        Write-Host "downloading $id"
        Invoke-WebRequest -UseBasicParsing -Uri "https://ambientcg.com/get?file=${id}_1K-PNG.zip" -OutFile $zip
    }
    if (-not (Test-Path $dir)) { Expand-Archive $zip -DestinationPath $dir }
    $png = Get-ChildItem $dir -Filter "*_Opacity.png" | Select-Object -First 1
    if ($png -eq $null) { throw "$id has no Opacity map" }
    Set-Item "Env:RG_DIRT_$key" $png.FullName
}

# 2. The textures, and their check.
& (Join-Path $root "tools\preview\sync-sim.ps1")
$log = Join-Path $project "dirt.log"
if (Test-Path $log) { Remove-Item $log -Force }
$env:RG_DIRT_OUT = $resources
$p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    "-batchmode", "-quit",
    "-projectPath", "`"$project`"",
    "-executeMethod", "DirtBaker.Bake",
    "-logFile", "`"$log`"")
Select-String -Path $log -Pattern "DIRT" -SimpleMatch | ForEach-Object { Write-Host $_.Line }
if ($p.ExitCode -ne 0 -or -not (Select-String -Path $log -Pattern "DIRT BAKE OK" -SimpleMatch -Quiet)) {
    throw "DirtBaker failed; see $log"
}
foreach ($f in "dirt-grime.bytes", "dirt-runs.bytes") {
    $i = Get-Item (Join-Path $resources $f)
    Write-Host ("OK: {0} ({1:N0} bytes)" -f $i.FullName, $i.Length) -ForegroundColor Green
}
Write-Host "Now rebuild the mod to embed them."
