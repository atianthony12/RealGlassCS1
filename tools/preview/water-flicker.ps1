# The water's FLICKER under temporal anti-aliasing, measured on this machine's GPU (Unity 5.6,
# UnityProject\Assets\Editor\WaterPreview.cs, its sequence mode): the harbour of water-render.ps1, each
# run 64 frames with the camera's projection jittered as Post-processing Stack v1's TAA jitters it
# (what Render It! runs; Halton 2, 3 over 8 frames) and the game's clocks running at 60 frames a
# second, each frame resolved by a stand-in of that TAA (Editor\PreviewTAA.shader: un-jitter,
# sharpen, history clipped to the neighbourhood's box, blended). After 32 frames to warm up, each
# water pixel's mean |second difference in time| of its displayed brightness: steady motion has
# none, flicker a lot. Printed per run (the share of the water over 2/255 and over 8/255, with TAA
# and on the raw frames), and a sheet in tools\preview\out\<Name>.png: per run the last resolved
# frame and its flicker map (grey = flicker x 25, red over 8/255).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview\water-flicker.ps1
#   ... -Views "night,day,sunset,close"
#   ... -Runs "vanilla,today,F4,band36F4"   from the presets below
#   ... -RippleRms 0.176 -Day 0.15 -Night 2.05   the author's game (2026-10-05 log) and his sliders
#   ... -Ripples 1.25 -Streaks 0 -Length 1.5      his Ripple strength, Streaks and Length (2026-10-06)
#   ... -Jitter 1 -Blending 0.94 -Sharpen 0.15   his Render It! profile's TAA
# Run after build-bundle.ps1 (the shaders compile) and a DLL build.

param(
    [int]$Width = 960,
    [int]$Height = 540,
    [string]$Views = "night,day,sunset,close",
    [string]$Runs = "vanilla,frozencity,moving,today",
    [double]$RippleRms = 0.176,
    [double]$Ripples = 1.0,
    [double]$Day = 0.15,
    [double]$Night = 2.05,
    [double]$Streaks = 0.036,
    [double]$Length = 1.65,
    [double]$ExposureDay = 1.6,
    [double]$ExposureNight = 2.5,
    [int]$FrameCount = 64,
    [int]$Warm = 32,
    [double]$Fps = 60.0,
    [double]$Jitter = 1.0,
    [double]$Blending = 0.94,
    [double]$Sharpen = 0.15,
    [int]$Dump = 0,
    [string]$Name = "water-flicker",
    [ValidateSet("d3d11", "glcore")]
    [string]$Api = "d3d11",
    # The facade cards (2026-11-12): -1 today's harbour; 0 the harbour of game-style towers with their
    # city map and cards (water-render.ps1 -Cards), the reflection as today; 1 the same with the cards read.
    [int]$CardMode = -1,
    [string]$Dll = (Join-Path $PSScriptRoot "..\..\RealGlass\bin\Debug\RealGlass.dll"),
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

# The runs: Frozen (the clocks stopped: the game's own still water), City (the reflection on, at the
# view's strength), Gain (x the mod's reflection gain at 100%), Foot (the reflection reads the
# ripples over this many trace pixels), A / B (the ripple texture's levels drawn one by one, a wave
# gone below A texels a wavelength there, in full from B; 0 0: the base level, mips by the GPU),
# Jit (x the jitter; 0: the clocks alone move the picture).
$presets = @{
    "vanilla"    = @{ Frozen = 1; City = 0; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 1; Label = "frozen ripples, no city reflection (~ vanilla)" }
    "frozencity" = @{ Frozen = 1; City = 1; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 1; Label = "frozen ripples + city reflection" }
    "moving"     = @{ Frozen = 0; City = 0; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 1; Label = "moving ripples, no city reflection" }
    "today"      = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 1; Label = "moving + city reflection (build 38947)" }
    "nojitter"   = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 0; Label = "as today, no jitter (motion alone)" }
    "flat"       = @{ Frozen = 0; City = 1; Gain = 0; Foot = 1; A = 0; B = 0; Jit = 1; Label = "moving ripples, city reflection a flat mirror" }
    "F2"         = @{ Frozen = 0; City = 1; Gain = 1; Foot = 2; A = 0; B = 0; Jit = 1; Label = "reflection bends over 2 px" }
    "F4"         = @{ Frozen = 0; City = 1; Gain = 1; Foot = 4; A = 0; B = 0; Jit = 1; Label = "reflection bends over 4 px" }
    "F8"         = @{ Frozen = 0; City = 1; Gain = 1; Foot = 8; A = 0; B = 0; Jit = 1; Label = "reflection bends over 8 px" }
    "band24"     = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 2; B = 4; Jit = 1; Label = "ripple levels 2-4 texels" }
    "band36"     = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 3; B = 6; Jit = 1; Label = "ripple levels 3-6 texels" }
    "band48"     = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 4; B = 8; Jit = 1; Label = "ripple levels 4-8 texels" }
    "band24F4"   = @{ Frozen = 0; City = 1; Gain = 1; Foot = 4; A = 2; B = 4; Jit = 1; Label = "levels 2-4 + bends over 4 px" }
    "band36F4"   = @{ Frozen = 0; City = 1; Gain = 1; Foot = 4; A = 3; B = 6; Jit = 1; Label = "levels 3-6 + bends over 4 px" }
    "band36F8"   = @{ Frozen = 0; City = 1; Gain = 1; Foot = 8; A = 3; B = 6; Jit = 1; Label = "levels 3-6 + bends over 8 px" }
    "band36F4g2" = @{ Frozen = 0; City = 1; Gain = 2; Foot = 4; A = 3; B = 6; Jit = 1; Label = "levels 3-6 + 4 px, gain x2" }
    "band48F4"   = @{ Frozen = 0; City = 1; Gain = 1; Foot = 4; A = 4; B = 8; Jit = 1; Label = "levels 4-8 + bends over 4 px" }
    "cone"      = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 0; B = 0; Jit = 1; Cone = 1; Label = "ray cone alone" }
    "F4c"        = @{ Frozen = 0; City = 1; Gain = 1; Foot = 4; A = 0; B = 0; Jit = 1; Cone = 1; Label = "bends over 4 px + ray cone" }
    "F6c"        = @{ Frozen = 0; City = 1; Gain = 1; Foot = 6; A = 0; B = 0; Jit = 1; Cone = 1; Label = "bends over 6 px + ray cone" }
    "F8c"        = @{ Frozen = 0; City = 1; Gain = 1; Foot = 8; A = 0; B = 0; Jit = 1; Cone = 1; Label = "bends over 8 px + ray cone" }
    "F8cg15"     = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 8; A = 0; B = 0; Jit = 1; Cone = 1; Label = "8 px + cone, gain x1.5" }
    "F8cg2"      = @{ Frozen = 0; City = 1; Gain = 2; Foot = 8; A = 0; B = 0; Jit = 1; Cone = 1; Label = "8 px + cone, gain x2" }
    "b36F8c"     = @{ Frozen = 0; City = 1; Gain = 1; Foot = 8; A = 3; B = 6; Jit = 1; Cone = 1; Label = "levels 3-6 + 8 px + cone" }
    "b48F8c"     = @{ Frozen = 0; City = 1; Gain = 1; Foot = 8; A = 4; B = 8; Jit = 1; Cone = 1; Label = "levels 4-8 + 8 px + cone" }
    "band612"    = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 6; B = 12; Jit = 1; Label = "ripple levels 6-12 texels" }
    "band816"    = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 8; B = 16; Jit = 1; Label = "ripple levels 8-16 texels" }
    "fixA"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 8; A = 0; B = 0; Jit = 1; Cone = 1; Label = "8 px + cone, gain x1.5" }
    "fixB"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 8; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 8 px + cone, gain x1.5" }
    "fixC"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 8; A = 6; B = 12; Jit = 1; Cone = 1; Label = "levels 6-12 + 8 px + cone, gain x1.5" }
    "fixD"       = @{ Frozen = 0; City = 1; Gain = 2; Foot = 8; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 8 px + cone, gain x2" }
    "fixE"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 1; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 1 px + cone, gain x1.5" }
    "fixF"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 2; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 2 px + cone, gain x1.5" }
    "fixG"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 1; A = 6; B = 12; Jit = 1; Cone = 1; Label = "levels 6-12 + 1 px + cone, gain x1.5" }
    "fixH"       = @{ Frozen = 0; City = 1; Gain = 1; Foot = 1; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 1 px + cone, gain x1" }
    "fixI"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 4; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 4 px + cone, gain x1.5" }
    "fixJ"       = @{ Frozen = 0; City = 1; Gain = 2; Foot = 4; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 4 px + cone, gain x2" }
    "fixK"       = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 8; B = 16; Jit = 1; Cone = 1; Label = "levels 8-16 + 3 px + cone, gain x2" }
    "fixL"       = @{ Frozen = 0; City = 1; Gain = 1.5; Foot = 4; A = 6; B = 12; Jit = 1; Cone = 1; Label = "levels 6-12 + 4 px + cone, gain x1.5" }
    # Without TAA (2026-10-06: "flickering too much, even without TAA"): no jitter, the clocks alone.
    # The mod's reflection gain is x 2 the Waves slider's; his file has Waves 300% -> Gain 6.
    "ntMoving"   = @{ Frozen = 0; City = 0; Gain = 1; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: moving ripples, no city reflection" }
    "ntFrozen"   = @{ Frozen = 1; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: frozen ripples, his reflection" }
    "ntFlat"     = @{ Frozen = 0; City = 1; Gain = 0; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: moving ripples, reflection a flat mirror" }
    "ntW100"     = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: waves 100% (deployed gain)" }
    "ntW300"     = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: waves 300% (his file)" }
    # Dist = 1: the flat mirror traced, its picture distorted by the waves at the composite.
    "ntD100"     = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 0; B = 0; Jit = 0; Dist = 1; Label = "no TAA: DISTORTED mirror, waves 100%" }
    "ntD300"     = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Dist = 1; Label = "no TAA: DISTORTED mirror, waves 300%" }
    "D300"       = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 1; Dist = 1; Label = "TAA: DISTORTED mirror, waves 300%" }
    "W300"       = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 1; Cone = 1; Label = "TAA: bent rays, waves 300%" }
    "ntF8g2"     = @{ Frozen = 0; City = 1; Gain = 2; Foot = 8; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: 8 px, waves 100%" }
    "ntF8g6"     = @{ Frozen = 0; City = 1; Gain = 6; Foot = 8; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: 8 px, waves 300%" }
    "ntF6g3"     = @{ Frozen = 0; City = 1; Gain = 3; Foot = 6; A = 0; B = 0; Jit = 0; Cone = 1; Label = "no TAA: 6 px, gain 3" }
    # Hist = w: the reflection's own history pass (CityReflection pass 3), keeping w of the last frame.
    "ntW100h85"  = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Hist = 0.85; Label = "no TAA: waves 100% + history 0.85" }
    "ntW300h85"  = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Hist = 0.85; Label = "no TAA: waves 300% + history 0.85" }
    "W300h85"    = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 1; Cone = 1; Hist = 0.85; Label = "TAA: waves 300% + history 0.85" }
    # Sub = n: each frame the mean of n times over the frame (a shutter open the whole frame: what
    # motion blur aims at).
    "ntW100s8"   = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Sub = 8; Label = "no TAA: bent rays 100%, shutter open 8 sub-frames" }
    "ntW300s8"   = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Sub = 8; Label = "no TAA: bent rays 300%, shutter open 8 sub-frames" }
    "ntD100s8"   = @{ Frozen = 0; City = 1; Gain = 2; Foot = 3; A = 0; B = 0; Jit = 0; Dist = 1; Sub = 8; Label = "no TAA: DISTORTED 100%, shutter open 8 sub-frames" }
    "ntD300s8"   = @{ Frozen = 0; City = 1; Gain = 6; Foot = 3; A = 0; B = 0; Jit = 0; Dist = 1; Sub = 8; Label = "no TAA: DISTORTED 300%, shutter open 8 sub-frames" }
    "ntMovings8" = @{ Frozen = 0; City = 0; Gain = 1; Foot = 3; A = 0; B = 0; Jit = 0; Cone = 1; Sub = 8; Label = "no TAA: no city reflection, shutter open 8 sub-frames" }
}

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$project = Resolve-Path (Join-Path $here "..\..\UnityProject")
$out = Join-Path $here "out"
$shotDir = Join-Path $out "flicker-frames"
New-Item -ItemType Directory -Force $out, $shotDir | Out-Null
Get-ChildItem $shotDir -Filter "*.png" | Remove-Item -Force

# The mod's engine-only sources the editor compiles (WaterPreview uses CityMap, FacadeCards, GlassUniforms).
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "sync-sim.ps1")

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path $Dll)))
$optics = $asm.GetType('RealGlass.Water.WaterOptics', $true)
$inv = [Globalization.CultureInfo]::InvariantCulture
function N([double]$v) { return $v.ToString("R", $inv) }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("out " + $shotDir)
$lines.Add("size " + $Width + " " + $Height)
$tbl = $optics::BuildTable($optics::SlopeVariance($optics::SkyWind))
foreach ($i in 0..32) { $lines.Add("table " + $i + " " + (N $tbl[$i])) }
$w = $optics::Ripples()
$n = $optics::RippleCount; $stride = $optics::WaveStride
for ($i = 0; $i -lt $n; $i++) {
    $at = $i * $stride
    $lines.Add("ripple " + $i + " " + (N $w[$at]) + " " + (N $w[$at + 1]) + " " + (N $w[$at + 2]) + " " + (N $w[$at + 3]))
    $lines.Add("ripple " + ($n + $i) + " " + (N $w[$at + 4]) + " " + (N $w[$at + 5]) + " " + (N $w[$at + 6]) + " " + (N $w[$at + 7]))
}
$lines.Add("ripplerms " + (N $RippleRms))
$gain = $optics::CityRippleGain(1.0, $RippleRms)
$spread = $optics::Spread($optics::SlopeVariance($optics::CityWind))
$lines.Add("streak " + (N ($optics::StreakSigma($Streaks, $spread, 45.0))))
$lines.Add("length " + (N $Length))
$lines.Add("exposure " + (N $ExposureDay) + " " + (N $ExposureNight))
if ($CardMode -ge 0) { $lines.Add("cards 1") }
$lines.Add("taa " + $FrameCount + " " + $Warm + " " + (N $Fps) + " " + (N $Jitter) + " " + (N $Blending) + " " + (N $Sharpen) + " " + $Dump)

$runList = $Runs.Split(',')
foreach ($r in $runList) { if (-not $presets.ContainsKey($r)) { throw "no run '$r' (presets: $($presets.Keys -join ', '))" } }
$viewList = $Views.Split(',')
foreach ($view in $viewList) {
    $strength = if ($view -like "night*") { $Night } else { $Day }
    foreach ($r in $runList) {
        $p = $presets[$r]
        $city = if ($p.City -eq 1) { $strength } else { 0.0 }
        $cone = if ($p.ContainsKey("Cone")) { $p.Cone } else { 0 }
        $dist = if ($p.ContainsKey("Dist")) { $p.Dist } else { 0 }
        $sub = if ($p.ContainsKey("Sub")) { $p.Sub } else { 1 }
        $hist = if ($p.ContainsKey("Hist")) { $p.Hist } else { 0 }
        $seqLine = "seq {0}_{1} {0} {10} 443 {2} {3} {4} {5} {6} {7} {8} {9} {11} {12} {13}" -f $view, $r, (N $city), $p.Frozen, (N ($gain * $p.Gain)), (N $p.Foot), (N $p.A), (N $p.B), (N $p.Jit), $cone, (N $Ripples), $dist, $sub, (N $hist)
        if ($CardMode -ge 0) { $seqLine += " " + $CardMode }
        $lines.Add($seqLine)
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
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $unityArgs
if (-not (Test-Path $output)) { throw "Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\water-preview.log" }
$report = Get-Content $output
$report | ForEach-Object { Write-Host $_ }
if ($report | Where-Object { $_ -like "EXCEPTION*" }) { exit 1 }
Remove-Item $output, (Join-Path $project "water-preview-input.txt") -ErrorAction SilentlyContinue

# The sheet: one row per run and view, the picture then the flicker map; labels on top.
$label = 22
$sheet = New-Object System.Drawing.Bitmap (2 * $Width), (($label + $Height) * $viewList.Count * $runList.Count)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font "Segoe UI", 11
$brush = [System.Drawing.Brushes]::White
$row = 0
foreach ($view in $viewList) {
    foreach ($r in $runList) {
        $top = $row * ($label + $Height)
        $stat = $report | Where-Object { $_ -like ("flicker {0}_{1}:*" -f $view, $r) } | Select-Object -First 1
        $short = if (-not $stat) { "" } elseif ($presets[$r].Jit -eq 0) { ($stat -replace '^.*\| no TAA: ', 'no TAA: ') } else { ($stat -replace '^.*\| TAA: ', 'TAA: ' -replace ' \| no TAA.*$', '') }
        $g.DrawString(("{0} -- {1}   [{2}]" -f $view, $presets[$r].Label, $short), $font, $brush, 6, ($top + 2))
        foreach ($k in 0..1) {
            $file = Join-Path $shotDir ("{0}_{1}{2}.png" -f $view, $r, $(if ($k -eq 1) { "-flicker" } else { "" }))
            $img = [System.Drawing.Image]::FromFile($file)
            $g.DrawImage($img, ($k * $Width), ($top + $label), $Width, $Height)
            $img.Dispose()
        }
        $row++
    }
}
$g.Dispose()
$path = Join-Path $out ($Name + ".png")
$sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Host ("{0} ({1:F0} s)" -f $path, $sw.Elapsed.TotalSeconds)
