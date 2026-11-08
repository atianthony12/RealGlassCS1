# Makes the rooms behind the glass (Glass/RoomAtlas.cs, Interiors.cginc): RealGlass\Resources\
# rooms-night.bytes and rooms-day.bytes, from the rooms listed in rooms.txt. Run it after editing
# rooms.txt or render-rooms.py, then rebuild the mod (the DLL embeds both files).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\interiors\build-rooms.ps1
#   ... -Survey      every downloaded panorama x 6 directions, to choose rooms by eye (no bake)
#
# 1. Downloads each panorama rooms.txt names that is not cached yet: Poly Haven's 2k HDR (CC0,
#    https://polyhaven.com/license), into %LOCALAPPDATA%\RealGlass-build\hdri -- outside the repo.
# 2. Blender (headless, factory settings; its own Python with numpy) runs render-rooms.py: each view
#    cut out of its panorama, graded by day and by night, packed into two PNG atlases.
# 3. Unity 5.6 (headless) runs Editor\RoomAtlasBaker.cs: DXT1 with box-filtered mips, written raw.

param(
    [string]$Blender = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe",
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe",
    [switch]$Survey
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$cache = Join-Path $env:LOCALAPPDATA "RealGlass-build"
$hdri = Join-Path $cache "hdri"
$bake = Join-Path $cache "bake"
$list = Join-Path $PSScriptRoot "rooms.txt"
$script = Join-Path $PSScriptRoot "render-rooms.py"
$project = Join-Path $root "UnityProject"
$resources = Join-Path $root "RealGlass\Resources"
New-Item -ItemType Directory -Force $hdri, $bake | Out-Null
if (-not (Test-Path $Blender)) { throw "Blender not found at $Blender" }

if ($Survey) {
    $out = Join-Path $cache "survey"
    & $Blender -b --factory-startup -P $script -- survey $hdri $out | Where-Object { $_ -match "SURVEY|FAILED" }
    if ($LASTEXITCODE -ne 0) { throw "the survey failed" }
    Write-Host "Views in $out"
    return
}

# 1. The panoramas.
$slugs = Get-Content $list | ForEach-Object { ($_ -split "#")[0].Trim() } | Where-Object { $_ } |
    ForEach-Object { ($_ -split "\s+")[0] } | Where-Object { -not $_.StartsWith("@") } | Sort-Object -Unique
foreach ($s in $slugs) {
    $f = Join-Path $hdri "$s.hdr"
    if (Test-Path $f) { continue }
    Write-Host "downloading $s"
    Invoke-WebRequest -UseBasicParsing -Uri "https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/2k/${s}_2k.hdr" -OutFile $f
}

# 1b. The rooms of our own ("@name" lines): any not rendered yet, with the models they're furnished with.
$own = Get-Content $list | ForEach-Object { ($_ -split "#")[0].Trim() } | Where-Object { $_ -like "@*" } |
    ForEach-Object { ($_ -split "\s+")[0].Substring(1) } | Sort-Object -Unique
$rendered = Join-Path $cache "rendered"
$missing = @($own | Where-Object { -not (Test-Path (Join-Path $rendered "$($_)_day.exr")) })
if ($missing.Count -gt 0) {
    & (Join-Path $PSScriptRoot "fetch-models.ps1")
    Write-Host "rendering $($missing.Count) rooms of our own"
    & $Blender -b --factory-startup -P (Join-Path $PSScriptRoot "render-offices.py") -- render $rendered @missing |
        Where-Object { $_ -match "RENDERED|FAILED" }
    if ($LASTEXITCODE -ne 0) { throw "render-offices.py failed" }
}

# 2. The pictures.
if (Test-Path $bake) { Remove-Item $bake -Recurse -Force }
& $Blender -b --factory-startup -P $script -- bake $hdri $list $bake | Where-Object { $_ -match "BAKE|FAILED|Error" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $bake "rooms-night.png"))) { throw "render-rooms.py failed" }

# 3. DXT1.
$log = Join-Path $project "rooms.log"
if (Test-Path $log) { Remove-Item $log -Force }
$env:RG_ROOMS_IN = $bake
$env:RG_ROOMS_OUT = $resources
$p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    "-batchmode", "-nographics", "-quit",
    "-projectPath", "`"$project`"",
    "-executeMethod", "RoomAtlasBaker.Bake",
    "-logFile", "`"$log`"")
Select-String -Path $log -Pattern "ROOMS" -SimpleMatch | ForEach-Object { Write-Host $_.Line }
if ($p.ExitCode -ne 0 -or -not (Select-String -Path $log -Pattern "ROOMS BAKE OK" -SimpleMatch -Quiet)) {
    throw "RoomAtlasBaker failed; see $log"
}
foreach ($f in "rooms-night.bytes", "rooms-day.bytes") {
    $i = Get-Item (Join-Path $resources $f)
    Write-Host ("OK: {0} ({1:N0} bytes)" -f $i.FullName, $i.Length) -ForegroundColor Green
}
Write-Host "Now rebuild the mod to embed them."
