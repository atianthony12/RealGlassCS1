# Pictures of the Water tab with the REAL shaders on this machine's GPU (Unity 5.6,
# UnityProject\Assets\Editor\WaterPreview.cs): a harbour of towers; the water a stand-in for the
# game's own water shader (Editor\PreviewWater.shader: its ripple normal, sky and sun highlight) with
# our moving ripples (RealGlass/WaterRipples) on it, and the city reflection drawn exactly as the mod
# records it, bending its rays off the same ripples. The mod's own numbers (the reflectance table,
# the ripples, the streaks' sigma, the reflection's gain) are read from the built DLL. Laid out as a
# labelled contact sheet in tools\preview\out\<Name>.png -- one row a view, one column a shot.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview\water-render.ps1
#   ... -Views "day,close,high,sunset,night"   one row each
#   ... -Ripples "0,1,2"      the Ripple strength for each column (1 = 100%; 0 = flat water)
#   ... -Frames "0,0.5"       also these seconds later on the game's clock, at 100% (the motion;
#                             "none": no such columns)
#   ... -RippleRms 0.3        the game texture's decoded root-mean-square 100% matches (measured in
#                             the game; the default is the mod's fallback)
#   ... -Day 1 -Night 2.05 -Streaks 0.036 -Length 1.65 -Width 960 -Height 540 -Name water
#   ... -Api glcore           the OpenGL Core shaders instead of Direct3D 11
#   ... -Rect "0,0.105,1,0.895"  the camera's viewport x,y,w,h: the game's own with its toolbar shown
#                             (CameraController.kFullScreenWithoutMenuBarRect; toolbar mods give it the
#                             whole screen)
#   ... -Cards                the facade cards (a preview round, 2026-11-12): the harbour's towers built
#                             as the glass preview's (game-style maps, PreviewBuilding, the game's lit-window
#                             rule), the city map and each tower's card made with the mod's own CityMap and
#                             FacadeCards, and every column twice: today, then with the cards (the
#                             reflection compiled with RG_FACADE_CARDS). Views also "steep" (the towers'
#                             tops above the screen) and "off" (the towers wholly off it), "night..." too.
#                             -CardDebug: a third column, where a card answered (green; red the march's own).
# Run after build-bundle.ps1 (the shader compiles) and a DLL build.

param(
    [int]$Width = 960,
    [int]$Height = 540,
    [string]$Views = "day,close,high,sunset,night",
    [string]$Ripples = "0,1,2",
    [string]$Frames = "0.5,1.0",
    [double]$RippleRms = -1.0,
    [double]$Day = 1.0,
    [double]$Night = 2.05,
    [double]$Streaks = 0.036,
    [double]$Length = 1.65,
    [double]$ExposureDay = 1.6,
    [double]$ExposureNight = 2.5,
    [string]$Name = "water",
    # glcore: the OpenGL Core shaders (Linux's, and the Mac's fallback) on this GPU instead of Direct3D 11.
    [ValidateSet("d3d11", "glcore")]
    [string]$Api = "d3d11",
    [string]$Rect = "",
    [switch]$Cards,
    [switch]$CardDebug,
    [string]$Dll = (Join-Path $PSScriptRoot "..\..\RealGlass\bin\Debug\RealGlass.dll"),
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$project = Resolve-Path (Join-Path $here "..\..\UnityProject")
$out = Join-Path $here "out"
# Not "$frames": PowerShell's names ignore case, and -Frames is a parameter.
$shotDir = Join-Path $out "water-frames"
New-Item -ItemType Directory -Force $out, $shotDir | Out-Null
Get-ChildItem $shotDir -Filter "*.png" | Remove-Item -Force

# The mod's engine-only sources the editor compiles (WaterPreview uses CityMap, FacadeCards, GlassUniforms).
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "sync-sim.ps1")

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path $Dll)))
$optics = $asm.GetType('RealGlass.Water.WaterOptics', $true)
$inv = [Globalization.CultureInfo]::InvariantCulture
function N([double]$v) { return $v.ToString("R", $inv) }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("out " + $shotDir)
$lines.Add("size " + $Width + " " + $Height)
if ($Rect -ne "") { $lines.Add("rect " + ($Rect.Split(',') -join ' ')) }
$tbl = $optics::BuildTable($optics::SlopeVariance($optics::SkyWind))
foreach ($i in 0..32) { $lines.Add("table " + $i + " " + (N $tbl[$i])) }
$w = $optics::Ripples()
$n = $optics::RippleCount; $stride = $optics::WaveStride
for ($i = 0; $i -lt $n; $i++) {
    $at = $i * $stride
    $lines.Add("ripple " + $i + " " + (N $w[$at]) + " " + (N $w[$at + 1]) + " " + (N $w[$at + 2]) + " " + (N $w[$at + 3]))
    $lines.Add("ripple " + ($n + $i) + " " + (N $w[$at + 4]) + " " + (N $w[$at + 5]) + " " + (N $w[$at + 6]) + " " + (N $w[$at + 7]))
}
$rms = if ($RippleRms -gt 0.0) { $RippleRms } else { $optics::DefaultRippleRms }
$lines.Add("ripplerms " + (N $rms))
# The reflection's gain as the mod sets it at the Waves slider's 100% (CityRippleGain).
$lines.Add("citygain " + (N ($optics::CityRippleGain(1.0, $rms))))
$spread = $optics::Spread($optics::SlopeVariance($optics::CityWind))
$lines.Add("streak " + (N ($optics::StreakSigma($Streaks, $spread, 45.0))))
$lines.Add("length " + (N $Length))
$lines.Add("exposure " + (N $ExposureDay) + " " + (N $ExposureNight))

# Columns: each Ripple strength at 443 s into the game's hour, then 100% at each later frame.
$columns = New-Object System.Collections.Generic.List[object]
foreach ($v in $Ripples.Split(',')) {
    $r = [double]::Parse($v, $inv)
    $columns.Add(@{ Ripples = $r; Seconds = 443.0; Label = $(if ($r -eq 0.0) { "flat (no ripples)" } else { "ripples {0:P0}" -f $r }) })
}
if ($Frames -ne "none") {
    foreach ($v in $Frames.Split(',')) { $s = [double]::Parse($v, $inv); $columns.Add(@{ Ripples = 1.0; Seconds = 443.0 + $s; Label = ("100%, {0:F1} s later" -f $s) }) }
}
if ($Cards) {
    # Every column today (card mode 0), then with the cards (1), then (-CardDebug) where they answered (2).
    $lines.Add("cards 1")
    $plain = $columns.ToArray()
    $columns = New-Object System.Collections.Generic.List[object]
    foreach ($col in $plain) {
        $columns.Add(@{ Ripples = $col.Ripples; Seconds = $col.Seconds; Label = $col.Label + ", today"; Card = 0 })
        $columns.Add(@{ Ripples = $col.Ripples; Seconds = $col.Seconds; Label = $col.Label + ", cards"; Card = 1 })
        if ($CardDebug) { $columns.Add(@{ Ripples = $col.Ripples; Seconds = $col.Seconds; Label = "where cards answered"; Card = 2 }) }
    }
}
$rows = $Views.Split(',')
foreach ($view in $rows) {
    $strength = if ($view -like "night*") { $Night } else { $Day }
    for ($c = 0; $c -lt $columns.Count; $c++) {
        $col = $columns[$c]
        $shot = "shot {0}_{1} {0} {2} {3} {4}" -f $view, $c, (N $col.Ripples), (N $col.Seconds), (N $strength)
        if ($Cards) { $shot += " " + $col.Card }
        $lines.Add($shot)
    }
}
[System.IO.File]::WriteAllLines((Join-Path $project "water-preview-input.txt"), $lines)

$output = Join-Path $project "water-preview-output.txt"
if (Test-Path $output) { Remove-Item $output -Force }
$sw = [Diagnostics.Stopwatch]::StartNew()
$unityArgs = @(
    "-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "WaterPreview.Render",
    "-logFile", "`"$(Join-Path $project 'water-preview.log')`"")
if ($Api -eq "glcore") { $unityArgs += "-force-glcore" }
$p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $unityArgs
if (-not (Test-Path $output)) { throw "Unity wrote no output (exit $($p.ExitCode)); see UnityProject\water-preview.log" }
$report = Get-Content $output
$report | ForEach-Object { Write-Host $_ }
if ($report | Where-Object { $_ -like "EXCEPTION*" }) { exit 1 }
Remove-Item $output, (Join-Path $project "water-preview-input.txt") -ErrorAction SilentlyContinue

# The sheet: a label strip over each column, a label at each row's left.
$label = 22; $side = 70
$sheet = New-Object System.Drawing.Bitmap ($side + $columns.Count * $Width), ($label + $rows.Count * $Height)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font "Segoe UI", 11
$brush = [System.Drawing.Brushes]::White
for ($c = 0; $c -lt $columns.Count; $c++) { $g.DrawString($columns[$c].Label, $font, $brush, ($side + $c * $Width + 6), 2) }
for ($r = 0; $r -lt $rows.Count; $r++) {
    $g.DrawString($rows[$r], $font, $brush, 6, ($label + $r * $Height + 6))
    for ($c = 0; $c -lt $columns.Count; $c++) {
        $img = [System.Drawing.Image]::FromFile((Join-Path $shotDir ("{0}_{1}.png" -f $rows[$r], $c)))
        $g.DrawImage($img, ($side + $c * $Width), ($label + $r * $Height), $Width, $Height)
        $img.Dispose()
    }
}
$g.Dispose()
$path = Join-Path $out ($Name + ".png")
$sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Host ("{0} ({1:F0} s)" -f $path, $sw.Elapsed.TotalSeconds)
