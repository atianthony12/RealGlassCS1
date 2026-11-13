# The Water tab's city reflection SHADER against the mod's model, in numbers (no pictures; RealLight's
# test, ported 2026-10-03): the reflection's uniforms as the mod sets them, the shader drawn by Unity
# 5.6 the way the mod draws it (UnityProject\Assets\Editor\CityReflectionTest.cs: a half-size copy of
# the frame, the trace into a half-size buffer with mips, the streaks up and down, onto the water
# -- one command buffer at AfterImageEffectsOpaque) over a plane of water at the sea's level and a lit
# wall 60 m high standing past it, each listed point rendered with and without the reflection:
#   - SHARP (waves and streaks 0), with and without the sky's share: water whose mirror ray meets the
#     wall = the water + rho(view) x (strength x wall - min(strength, 1) x s x share x the theme's sky x its light), s
#     the largest <= 1 that takes no channel of the water below 2% of it, never below black (the share
#     at most 1 / rho; above 100% the rest taken in the colour of what is left: CityReflection's
#     SkyTaken, 2026-11-13); water whose ray passes beside or over the wall, the wall itself and the sky:
#     untouched;
#   - STREAKS 100%: at the side edge as sharp (up and down only); at the top edge, part way.
#   - LENGTH 150% (the ray's slope 2/3 of the mirror's): water whose mirror ray passes over the wall
#     now meets it (rho x the wall, sharp), water that met it still does, the rest untouched.
#   - A TILTED SEA (a ripple texture of one value: the sea falling 5% toward the wall and rising 5%
#     toward +z): each point's ray reflected off that slope, by the model -- water whose mirror passed
#     over the wall now meets it, as the bent ray says; the rest as the model says.
#   - RIPPLES (RealGlass/WaterRipples' texture, read as the game reads it, the gain the mod sets):
#     the wall and the sky untouched, the middle of the reflection still the wall's.
# Then the ripple generator itself, texel by texel, and the mod's measurement of the game's ripple
# texture on a DXT5 stand-in (WaterRippleTest.cs). Then the ocean waves' FFT against its CPU reference,
# on Direct3D 11 and OpenGL Core (WaterWavesTest.cs). Last the glass's: the model's glass carried onto
# an LOD laid out otherwise, read back, its mask uploaded and read back, on both APIs (LodGlassTest.cs).
# Run after build-bundle.ps1 (the shader compiles) and a DLL build:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-reflection-shader.ps1

param(
    [string]$Dll = (Join-Path $PSScriptRoot "..\RealGlass\bin\Debug\RealGlass.dll"),
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$project = Join-Path $PSScriptRoot "..\UnityProject"
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$optics = $asm.GetType('RealGlass.Water.WaterOptics', $true)
$inv = [System.Globalization.CultureInfo]::InvariantCulture
function N([double]$v) { return $v.ToString("R", $inv) }
$deg = [Math]::PI / 180.0
$failed = 0

function Check([string]$name, $ok, $detail) {
    if ($ok) { Write-Host "ok    $name  ->  $detail" }
    else { Write-Host "FAIL  $name  ->  $detail"; $script:failed++ }
}
function Worst([double[]]$got, [double[]]$want) {
    $w = 0.0
    foreach ($k in 0..2) { $rel = [Math]::Abs($got[$k] - $want[$k]) / [Math]::Max(2e-3, [Math]::Abs($want[$k])); if ($rel -gt $w) { $w = $rel } }
    return $w
}
function Show([double[]]$c) { return "{0:F4} {1:F4} {2:F4}" -f $c[0], $c[1], $c[2] }
function Three($q, [int]$at) { return [double[]]@([double]::Parse($q[$at], $inv), [double]::Parse($q[$at + 1], $inv), [double]::Parse($q[$at + 2], $inv)) }

$level = 40.0
$eye = [double[]]@(0.0, ($level + 30.0), 0.0)
$face = 390.0; $wallTop = 60.0; $wallHalf = 150.0
$tbl = $optics::BuildTable($optics::SlopeVariance($optics::SkyWind))
$spread = $optics::Spread($optics::SlopeVariance($optics::CityWind))
# The theme's sky and the light the water lights it with: one colour each.
$theme = [double[]]@(0.30, 0.45, 0.70)
$light = [double[]]@(1.2, 1.1, 1.0)
# A published real sky (light already: not multiplied by the light).
$realSky = [double[]]@(0.25, 0.40, 0.90)

# A point on the water seen this far below the level, at this azimuth (from +x toward +z).
function WaterPoint([double]$below, [double]$azimuth) {
    $c = [Math]::Cos($below * $deg)
    $d = [double[]]@(($c * [Math]::Cos($azimuth * $deg)), (-[Math]::Sin($below * $deg)), ($c * [Math]::Sin($azimuth * $deg)))
    $t = ($eye[1] - $level) / -$d[1]
    return [double[]]@(($eye[0] + $d[0] * $t), $level, ($eye[2] + $d[2] * $t))
}
# The reflection's edges: the mirror ray of water at azimuth a meets the face at z = 390 tan a (the
# side, z 150), and at 390 tan b - 30 m above the water seen b below (the top, 60 m).
$sideEdge = [Math]::Atan($wallHalf / $face) / $deg
$topEdge = [Math]::Atan(($wallTop + 30.0) / $face) / $deg
$points = @(
    @{ Name = "water8"; P = (WaterPoint 8.0 0.0); Kind = "hit"; Say = "water 8 deg down, the wall in its ray" },
    @{ Name = "water10az-12"; P = (WaterPoint 10.0 -12.0); Kind = "hit"; Say = "water 10 deg down, 12 deg aside" },
    @{ Name = "nearTop"; P = (WaterPoint 11.5 0.0); Kind = "hit"; Say = "water 11.5 deg down, below the top's reflection" },
    @{ Name = "nearSide"; P = (WaterPoint 8.0 17.0); Kind = "hit"; Say = "water 8 deg down, 17 deg aside" },
    @{ Name = "edgeTop"; P = (WaterPoint $topEdge 0.0); Kind = "edge"; Say = "the top's reflection" },
    @{ Name = "edgeSide"; P = (WaterPoint 8.0 $sideEdge); Kind = "edge"; Say = "the side's reflection" },
    @{ Name = "water6"; P = (WaterPoint 6.0 0.0); Kind = "hit"; Say = "water 6 deg down, near the wall's foot (not its own water)" },
    @{ Name = "overTop"; P = (WaterPoint 16.0 0.0); Kind = "same"; Say = "water whose ray passes over the wall" },
    @{ Name = "miss"; P = (WaterPoint 8.0 26.0); Kind = "same"; Say = "water whose ray passes beside the wall" },
    @{ Name = "wall"; P = [double[]]@($face, ($level + 30.0), 0.0); Kind = "same"; Say = "the wall" },
    @{ Name = "sky"; P = [double[]]@(1000.0, ($level + 400.0), 466.0); Kind = "same"; Say = "the sky" }
)

$cases = @(
    @{ Name = "raised lake, sea level unchanged"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 0.0; Raised = 100.0 },
    @{ Name = "sharp, no sky share"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 0.0 },
    @{ Name = "sharp, sky share 100%"; Share = 1.0; Water = "0.6 0.9 1.0"; Streaks = 0.0 },
    @{ Name = "sharp, sky share 100%, a real sky at 60%"; Share = 1.0; Water = "0.6 0.9 1.0"; Streaks = 0.0; Real = 0.6 },
    # The night's default strength: the wall x 3, the sky it replaces still x 1 (until 2026-11-04 the
    # sky x 3 too: dark buildings black with red edges).
    @{ Name = "sharp, sky share 100%, strength 300%"; Share = 1.0; Water = "0.6 0.9 1.0"; Streaks = 0.0; Strength = 3.0 },
    # Below 100% the wall and the sky both scale: the reflection fades toward the bare water.
    @{ Name = "sharp, sky share 100%, strength 50%"; Share = 1.0; Water = "0.6 0.9 1.0"; Streaks = 0.0; Strength = 0.5 },
    # The sky's share since 2026-11-13: 5% for new installs; an older file keeps its own, up to 300%.
    # Water whiter than the sky: taken in the sky's colour x 3, the blue went first and the water was
    # left orange (the author's screenshot, 2026-11-13). Above 100% what is left keeps 100%'s hue.
    @{ Name = "sharp, sky share 5%, white water"; Share = 0.05; Water = "0.9 0.9 0.9"; Streaks = 0.0 },
    @{ Name = "sharp, sky share 100%, white water"; Share = 1.0; Water = "0.9 0.9 0.9"; Streaks = 0.0 },
    @{ Name = "sharp, sky share 300%, white water"; Share = 3.0; Water = "0.9 0.9 0.9"; Streaks = 0.0 },
    @{ Name = "streaks 100%"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 1.0 },
    # "Stretch", not "Length": every hashtable answers .Length (1) when it has no such key, and the
    # sharp checks below were skipped that way from 2026-10-04 to 2026-10-05.
    @{ Name = "length 150%"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 0.0; Stretch = 1.5 },
    @{ Name = "a tilted sea"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 0.0; Tilt = 0.05 },
    @{ Name = "ripples, today's gain"; Share = 0.0; Water = "0.05 0.12 0.15"; Streaks = 0.0; Waves = 1.0 }
)
$results = @{}

# The tilted sea: a ripple texture of ONE value, x = y = 0.5 + t: both of the game's reads give
# (t, t), so the slope is (-t, +t) x the gain everywhere -- the sea falling 5% toward the wall (+x)
# and rising 5% toward +z.
function TiltSlope([double]$t) { return [double[]]@((-$t), $t) }
# The ripples' case: the generator's texture at a root-mean-square slope of 0.06 (decoded 0.12), the
# reflection's gain set as the mod sets it (CityRippleGain: today's 0.03 at 100%).
$rippleTable = $optics::Ripples()
$generatedSlope = 0.06

foreach ($case in $cases) {
    $sy = $optics::StreakSigma($case.Streaks, $spread, 60.0)

    $lines = New-Object System.Collections.Generic.List[string]
    $strength = if ($case.Strength) { [double]$case.Strength } else { 1.0 }
    $lines.Add("_RGWaterGeom " + (N $level) + " " + (N $strength) + " " + (N $case.Share) + " 0")
    $length = if ($case.Stretch) { [double]$case.Stretch } else { 1.0 }
    $lines.Add("_RGWaterMarch " + (N $optics::MaxDistance) + " " + (N $optics::Thickness) + " " + (N $optics::EdgeFade) + " " + (N $length))
    # The half-size buffers of the 257 px test: 128 texels.
    $lines.Add("_RGWaterStreak " + (N $sy) + " " + (N (1.0 / 128.0)) + " 0 0")
    $lines.Add("_RGWaterLight " + (N $light[0]) + " " + (N $light[1]) + " " + (N $light[2]) + " 0")
    # The ripples: none (gain 0), the tilted sea's one value, or the generator's.
    $gain = 0.0
    if ($case.Tilt) {
        $gain = 1.0
        $v = 0.5 + [double]$case.Tilt
        $lines.Add("uniform " + (N $v) + " " + (N $v) + " 1 " + (N $v))
    }
    elseif ($case.Waves) {
        $gain = $optics::CityRippleGain([double]$case.Waves, 2.0 * $generatedSlope)
        $lines.Add("generate " + (N $generatedSlope))
        for ($i = 0; $i -lt 32; $i++) {
            $lines.Add("ripple " + $i + " " + (($rippleTable[($i * 8)..($i * 8 + 3)] | ForEach-Object { N $_ }) -join " "))
            $lines.Add("ripple " + (32 + $i) + " " + (($rippleTable[($i * 8 + 4)..($i * 8 + 7)] | ForEach-Object { N $_ }) -join " "))
        }
    }
    $lines.Add("_RGWaterWave " + (N $gain) + " 0 0 0")
    # The game's clocks: 23 s into its minute, 443 s into its hour.
    $lines.Add("simtime 23 " + (N (23.0 / 60.0)) + " " + (N (23.0 * 2.0 * [Math]::PI)) + " " + (N (23.0 * 2.0 * [Math]::PI / 60.0)))
    $lines.Add("simtime2 443 " + (N (443.0 / 3600.0)) + " 0 0")
    foreach ($i in 0..32) { $lines.Add("table " + $i + " " + (N $tbl[$i])) }
    $lines.Add("theme " + (N $theme[0]) + " " + (N $theme[1]) + " " + (N $theme[2]))
    $real = if ($case.Real) { [double]$case.Real } else { 0.0 }
    if ($real -gt 0.0) { $lines.Add("realsky " + (N $realSky[0]) + " " + (N $realSky[1]) + " " + (N $realSky[2]) + " " + (N $real)) }
    $raised = if ($case.Raised) { [double]$case.Raised } else { 0.0 }
    $lines.Add("level " + (N ($level + $raised)))
    $lines.Add("water " + $case.Water)
    $lines.Add("eye " + (N $eye[0]) + " " + (N ($eye[1] + $raised)) + " " + (N $eye[2]))
    $lines.Add("look 1 -0.12 0")
    $lines.Add("wall " + (N ($face + 10.0)) + " " + (N ($level + $raised + $wallTop / 2.0)) + " 0 20 " + (N $wallTop) + " " + (N (2.0 * $wallHalf)))
    foreach ($pt in $points) { $lines.Add("point " + $pt.Name + " " + (N $pt.P[0]) + " " + (N ($pt.P[1] + $raised)) + " " + (N $pt.P[2])) }
    [System.IO.File]::WriteAllLines((Join-Path $project "reflection-test-input.txt"), $lines)

    $out = Join-Path $project "reflection-test-output.txt"
    if (Test-Path $out) { Remove-Item $out -Force }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
        "-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "CityReflectionTest.Run",
        "-logFile", "`"$(Join-Path $project 'reflection-test.log')`"")
    if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\reflection-test.log"; exit 1 }
    $got = Get-Content $out
    Write-Host ("{0} (streaks' sigma {1:P2}): {2}" -f $case.Name, $sy, $got[0])
    $got | Where-Object { $_ -like "EXCEPTION*" } | ForEach-Object { Write-Host $_; $script:failed++ }

    $rows = @{}
    foreach ($line in ($got | Where-Object { $_ -like "point *" })) { $q = $line.Split(' '); $rows[$q[1]] = $q }
    if ($rows.Count -ne $points.Count) { Check "every point came back" $false ("{0} of {1}" -f $rows.Count, $points.Count); continue }
    $results[$case.Name] = $rows

    if ($case.Streaks -gt 0.0 -or $case.Stretch -or $case.Waves) { continue }

    # THE TILTED SEA: each point's ray reflected off the slope where it meets the water, followed
    # to the wall's face (x = 390, 60 m tall, z within 150): the wall where it lands clearly on it,
    # untouched where clearly beside or over it, not judged within 5 m of an edge.
    if ($case.Tilt) {
        $wallColour = Three $rows["wall"] 10
        foreach ($pt in $points) {
            if ($pt.Name -eq "wall" -or $pt.Name -eq "sky") { continue }
            $q = $rows[$pt.Name]
            $d = Three $q 6; $plain = Three $q 10; $shader = Three $q 14
            $t = ($eye[1] - $level) / -$d[1]
            $px = $eye[0] + $d[0] * $t; $pz = $eye[2] + $d[2] * $t
            $sl = TiltSlope ([double]$case.Tilt)
            $nl = [Math]::Sqrt(1.0 + $sl[0] * $sl[0] + $sl[1] * $sl[1])
            $n = [double[]]@((-$sl[0] / $nl), (1.0 / $nl), (-$sl[1] / $nl))
            $dn = $d[0] * $n[0] + $d[1] * $n[1] + $d[2] * $n[2]
            $r = [double[]]@(($d[0] - 2.0 * $dn * $n[0]), [Math]::Abs($d[1] - 2.0 * $dn * $n[1]), ($d[2] - 2.0 * $dn * $n[2]))
            $s = ($face - $px) / $r[0]
            $y = $r[1] * $s; $z = $pz + $r[2] * $s
            $land = "{0:F1} m up, z {1:F1}" -f $y, $z
            # Within 5 m of an edge is not judged: a half-size texel spans ~3.3 m at the wall (390 m out,
            # 128 texels over 60 deg), so its pixel straddles the edge.
            if ($y -gt 6.0 -and $y -lt ($wallTop - 5.0) -and [Math]::Abs($z) -lt ($wallHalf - 5.0)) {
                $rho = $optics::Lookup($tbl, -$d[1])
                $want = [double[]]@(0.0, 0.0, 0.0)
                foreach ($k in 0..2) { $want[$k] = $plain[$k] + $rho * $wallColour[$k] }
                $w = Worst $shader $want
                Check ("tilted sea, {0}: its ray lands on the wall ({1})" -f $pt.Say, $land) ($w -lt 0.015) ("shader {0}  model {1}  worst {2:P2}" -f (Show $shader), (Show $want), $w)
            }
            elseif ($y -gt ($wallTop + 5.0) -or [Math]::Abs($z) -gt ($wallHalf + 5.0)) {
                $w = Worst $shader $plain
                Check ("tilted sea, {0}: its ray passes the wall ({1}): untouched" -f $pt.Say, $land) ($w -lt 1e-4) ("{0} (without {1})" -f (Show $shader), (Show $plain))
            }
            else { Write-Host ("      tilted sea, {0}: lands at an edge ({1}), not judged" -f $pt.Say, $land) }
        }
        continue
    }

    # SHARP: pixel by pixel against the model.
    $wallColour = Three $rows["wall"] 10
    foreach ($pt in $points) {
        if ($pt.Kind -eq "edge") { continue }
        $q = $rows[$pt.Name]
        $ray = Three $q 6
        $plain = Three $q 10
        $shader = Three $q 14
        if ($pt.Kind -eq "same") {
            $w = Worst $shader $plain
            Check ("{0}: untouched" -f $pt.Say) ($w -lt 1e-4) ("{0} (without {1})" -f (Show $shader), (Show $plain))
            continue
        }

        $rho = $optics::Lookup($tbl, -$ray[1])
        $want = [double[]]@(0.0, 0.0, 0.0)
        $add = [double[]]@(0.0, 0.0, 0.0)
        # The sky taken away (CityReflection's SkyTaken): the share at most 1 / rho (the sky layer's alpha
        # stops at 1); up to 100% the sky's colour, never more than the water pixel holds, one scale for
        # every channel; the share above 100% in the colour of what is left, at the sky's brightness.
        $share = [Math]::Min([double]$case.Share, 1.0 / [Math]::Max($rho, 1e-4))
        $unit = [double[]]@(0.0, 0.0, 0.0)
        $skies = [double[]]@(0.0, 0.0, 0.0)
        $left = [double[]]@(0.0, 0.0, 0.0)
        $scale = 1.0
        foreach ($k in 0..2) {
            $unit[$k] = $theme[$k] * $light[$k] * (1.0 - $real) + $realSky[$k] * $real
            $skies[$k] = [Math]::Min($share, 1.0) * $unit[$k]
            $scale = [Math]::Min($scale, 0.98 * $plain[$k] / [Math]::Max($rho * $skies[$k], 1e-6))
        }
        $scale = [Math]::Max($scale, 0.0)
        foreach ($k in 0..2) { $skies[$k] *= $scale; $left[$k] = [Math]::Max($plain[$k] / [Math]::Max($rho, 1e-4) - $skies[$k], 0.0) }
        $lumOf = { param($c) 0.2126 * $c[0] + 0.7152 * $c[1] + 0.0722 * $c[2] }
        $more = [Math]::Min([Math]::Max($share - 1.0, 0.0) * $scale * (& $lumOf $unit) / [Math]::Max((& $lumOf $left), 1e-6), 0.9)
        foreach ($k in 0..2) {
            $add[$k] = $rho * ($strength * $wallColour[$k] - [Math]::Min($strength, 1.0) * ($skies[$k] + $left[$k] * $more))
            $want[$k] = $plain[$k] + [Math]::Max($add[$k], -0.98 * $plain[$k])
        }
        $w = Worst $shader $want
        Check ("{0}: rho {1:P1}" -f $pt.Say, $rho) ($w -lt 0.015) ("shader {0}  model {1}  (without {2}, added {3})  worst {4:P2}" -f (Show $shader), (Show $want), (Show $plain), (Show $add), $w)
        if ($case.Share -eq 0.0) {
            Check "  the reflection is there (brighter than the water alone)" ($shader[0] -gt $plain[0] * 1.05) ("red {0:F4} vs {1:F4}" -f $shader[0], $plain[0])
        }
    }
}

# What each case added at a point (red: the wall is red-heavy), and the checks on the streaks,
# the length and the waves.
function AddedAt([string]$case, [string]$name) {
    $q = $results[$case][$name]
    return ([double]::Parse($q[14], $inv) - [double]::Parse($q[10], $inv))
}
if ($results.Count -eq $cases.Count) {
    # The water left where the wall is reflected (the shader's pixel less rho x the wall), its red over
    # its blue: at 300% no warmer than at 100% (the shader before 2026-11-13: 23.7 against 3.4 here).
    function LeftRedOverBlue([string]$case) {
        $q = $results[$case]["water8"]
        $rho = $optics::Lookup($tbl, -([double]::Parse($q[7], $inv)))
        $wall = Three $results[$case]["wall"] 10; $shader = Three $q 14
        return ($shader[0] - $rho * $wall[0]) / [Math]::Max($shader[2] - $rho * $wall[2], 1e-6)
    }
    $rb1 = LeftRedOverBlue "sharp, sky share 100%, white water"; $rb3 = LeftRedOverBlue "sharp, sky share 300%, white water"
    $rb005 = LeftRedOverBlue "sharp, sky share 5%, white water"
    Check "sky share 300%: the water left no warmer than at 100% (red over blue)" ($rb3 -le $rb1 * 1.01) ("{0:F3} at 300%, {1:F3} at 100%, {2:F3} at 5%" -f $rb3, $rb1, $rb005)

    $rc = "ripples, today's gain"
    $sharpMid = AddedAt "sharp, no sky share" "water8"; $wavesMid = AddedAt $rc "water8"
    Check "ripples: the middle of the reflection still the wall's" ([Math]::Abs($wavesMid / $sharpMid - 1.0) -lt 0.03) ("{0:F4} vs {1:F4}" -f $wavesMid, $sharpMid)
    Check "  the wall and the sky: untouched" ((AddedAt $rc "wall") -eq 0.0 -and (AddedAt $rc "sky") -eq 0.0) ""
    $line = ""
    foreach ($pt in $points) { $line += (" {0} {1:F4}/{2:F4}" -f $pt.Name, (AddedAt $rc $pt.Name), (AddedAt "sharp, no sky share" $pt.Name)) }
    Write-Host ("      ripples vs flat, red added:" + $line)

    $sharpSide = AddedAt "sharp, no sky share" "edgeSide"; $streakSide = AddedAt "streaks 100%" "edgeSide"
    $sideRef = AddedAt "sharp, no sky share" "nearSide"
    Check "streaks: up and down only -- the side's edge as sharp" ([Math]::Abs($streakSide - $sharpSide) -lt 0.1 * $sideRef) ("{0:F4} vs {1:F4} sharp" -f $streakSide, $sharpSide)
    $streakTop = (AddedAt "streaks 100%" "edgeTop") / (AddedAt "sharp, no sky share" "nearTop")
    Check "  the top's edge stretched (part way)" ($streakTop -gt 0.15 -and $streakTop -lt 0.85) ("{0:P0} of the sharp reflection 1.5 deg below" -f $streakTop)

    # Water 16 deg down: its mirror ray passes over the wall (60 m) at 1:1; at 150% its slope is
    # tan 16 / 1.5 and it meets the wall some 54 m up -- the reflection drawn longer.
    $q = $results["length 150%"]["overTop"]
    $ray = Three $q 6; $plain = Three $q 10; $shader = Three $q 14
    $wallColour = Three $results["sharp, no sky share"]["wall"] 10
    $rho = $optics::Lookup($tbl, -$ray[1])
    $want = [double[]]@(0.0, 0.0, 0.0)
    foreach ($k in 0..2) { $want[$k] = $plain[$k] + $rho * $wallColour[$k] }
    $w = Worst $shader $want
    Check "length 150%: water whose mirror passed over the wall now meets it" ($w -lt 0.015) ("shader {0}  model {1}  (without {2})  worst {3:P2}" -f (Show $shader), (Show $want), (Show $plain), $w)
    Check "  at 100% the same water had nothing" ([Math]::Abs((AddedAt "sharp, no sky share" "overTop")) -lt 1e-4) ""
    $mid = (AddedAt "length 150%" "water8") / (AddedAt "sharp, no sky share" "water8")
    Check "  water that met the wall still does, as brightly" ([Math]::Abs($mid - 1.0) -lt 0.015) ("{0:P1} of the mirror's" -f $mid)
    Check "  the wall, the sky and water beside the wall: untouched" ((AddedAt "length 150%" "wall") -eq 0.0 -and (AddedAt "length 150%" "sky") -eq 0.0 -and
        [Math]::Abs((AddedAt "length 150%" "miss")) -lt 1e-4) ""
}
else { Check "every case came back" $false ("{0} of {1}" -f $results.Count, $cases.Count) }

Remove-Item (Join-Path $project "reflection-test-output.txt") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $project "reflection-test-input.txt") -ErrorAction SilentlyContinue

# ---- the ripples (UnityProject\Assets\Editor\WaterRippleTest.cs) -----------------------------
# RealGlass/WaterRipples drawn as the mod draws it (1024 x 1024 ARGB32 linear, mips), read back: each
# listed texel against the model -- slope = sum s x dir x cos(2 pi (m u + n v - c hour + phase)) x the
# root-mean-square, written x = 0.5 - slope.x, y = 0.5 + slope.y (clamped) in .a/.r and .g -- within 1.5
# of 255; the whole texture's decoded root-mean-square = 2 x the slope's (whole wave numbers: the
# waves' cross terms vanish over the tile). And the mod's measurement of the game's texture on a DXT5
# stand-in (Graphics.Blit + ReadPixels: the engine calls probed): measured against the pattern's own.
$rippleSlope = 0.08
$hourSeconds = 1234.5
$size = 1024
$texels = @(@(0, 0), @(17, 200), @(128, 128), @(511, 3), @(77, 951), @(1023, 1023), @(640, 333))
$lines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt 32; $i++) {
    $lines.Add("ripple " + $i + " " + (($rippleTable[($i * 8)..($i * 8 + 3)] | ForEach-Object { N $_ }) -join " "))
    $lines.Add("ripple " + (32 + $i) + " " + (($rippleTable[($i * 8 + 4)..($i * 8 + 7)] | ForEach-Object { N $_ }) -join " "))
}
$lines.Add("slope " + (N $rippleSlope))
$lines.Add("simtime2 " + (N $hourSeconds) + " " + (N ($hourSeconds / 3600.0)) + " 0 0")
$lines.Add("size " + $size)
foreach ($t in $texels) { $lines.Add("texel " + $t[0] + " " + $t[1]) }
[System.IO.File]::WriteAllLines((Join-Path $project "ripple-test-input.txt"), $lines)
$out = Join-Path $project "ripple-test-output.txt"
if (Test-Path $out) { Remove-Item $out -Force }
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "WaterRippleTest.Run",
    "-logFile", "`"$(Join-Path $project 'ripple-test.log')`"")
if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\ripple-test.log"; exit 1 }
$got = Get-Content $out
Write-Host ("ripples: " + $got[0])
$got | Where-Object { $_ -like "EXCEPTION*" } | ForEach-Object { Write-Host $_; $script:failed++ }
$hour = [double]([float]($hourSeconds / 3600.0))
$worst = 0.0; $detail = ""
foreach ($line in ($got | Where-Object { $_ -like "texel *" })) {
    $q = $line.Split(' ')
    $tx = [int]$q[1]; $ty = [int]$q[2]
    $u = ($tx + 0.5) / $size; $v = ($ty + 0.5) / $size
    $sx = 0.0; $sz = 0.0
    for ($i = 0; $i -lt 32; $i++) {
        $at = $i * 8
        $cycles = $rippleTable[$at] * $u + $rippleTable[$at + 1] * $v - $rippleTable[$at + 2] * $hour + $rippleTable[$at + 4]
        $c = $rippleTable[$at + 3] * [Math]::Cos(2.0 * [Math]::PI * ($cycles - [Math]::Floor($cycles)))
        $sx += $rippleTable[$at + 5] * $c; $sz += $rippleTable[$at + 6] * $c
    }
    $sx *= $rippleSlope; $sz *= $rippleSlope
    $wantX = 255.0 * (0.5 + 0.5 * [Math]::Max(-1.0, [Math]::Min(1.0, -2.0 * $sx)))
    $wantY = 255.0 * (0.5 + 0.5 * [Math]::Max(-1.0, [Math]::Min(1.0, 2.0 * $sz)))
    $e = [Math]::Max([Math]::Abs([double]$q[6] - $wantX), [Math]::Max([Math]::Abs([double]$q[3] - $wantX), [Math]::Abs([double]$q[4] - $wantY)))
    if ($e -gt $worst) { $worst = $e; $detail = ("texel {0},{1}: got a {2} r {3} g {4}, model x {5:F1} y {6:F1}" -f $tx, $ty, $q[6], $q[3], $q[4], $wantX, $wantY) }
}
Check "ripples: every listed texel = the model (x in .a and .r, y in .g)" ($worst -le 1.5) ("worst {0:F2} of 255 ({1})" -f $worst, $detail)
$rmsLine = $got | Where-Object { $_ -like "rms *" } | Select-Object -First 1
$rms = [double]::Parse($rmsLine.Split(' ')[1], $inv)
Check "  the texture's decoded root-mean-square = 2 x the slope's" ([Math]::Abs($rms / (2.0 * $rippleSlope) - 1.0) -lt 0.02) ("{0:F4} vs {1:F4}" -f $rms, (2.0 * $rippleSlope))
$src = $got | Where-Object { $_ -like "probe source *" } | Select-Object -First 1
$meas = $got | Where-Object { $_ -like "probe measured *" } | Select-Object -First 1
if ($src -and $meas) {
    $srcRms = [double]::Parse($src.Split(' ')[-1], $inv); $measRms = [double]::Parse($meas.Split(' ')[-1], $inv)
    Write-Host ("      " + $src)
    Check "  the measurement on a DXT5 stand-in: Graphics.Blit + ReadPixels give its rms" ([Math]::Abs($measRms / $srcRms - 1.0) -lt 0.05) ("{0:F4} measured vs {1:F4}" -f $measRms, $srcRms)
}
else { Check "  the measurement probe ran" $false "no probe lines" }
Remove-Item $out -ErrorAction SilentlyContinue
Remove-Item (Join-Path $project "ripple-test-input.txt") -ErrorAction SilentlyContinue

# ---- the water's sky cube (UnityProject\Assets\Editor\WaterCubeTest.cs) ----------------------
# Drawn as the mod draws it (a hidden camera's skybox, RenderToCubemap); each face's centre against
# rho(max(d.y, 0)) x gain x lerp(theme x light, real sky, weight) / light. Faces +X -X +Y -Y +Z -Z;
# one colour per face in each cube, so a face read back names its direction.
$faceUp = @(0.0, 0.0, 1.0, -1.0, 0.0, 0.0)
$themeFaces = @(@(0.30, 0.45, 0.70), @(0.20, 0.30, 0.40), @(0.50, 0.60, 0.90), @(0.10, 0.10, 0.10), @(0.35, 0.40, 0.60), @(0.25, 0.35, 0.55))
$realFaces = @(@(0.80, 1.10, 2.00), @(0.60, 0.80, 1.40), @(1.50, 2.00, 3.50), @(0.20, 0.20, 0.30), @(0.70, 1.00, 1.90), @(0.65, 0.90, 1.70))
$cubeLight = [double[]]@(1.6, 1.4, 1.1)
$slider = 1.0
$gain = $optics::CubeGain($slider)
$cubeCases = @(
    @{ Name = "no real sky published (RealGlass alone)"; Publish = $false; Weight = 0.0 },
    @{ Name = "a real sky published at weight 0 (night)"; Publish = $true; Weight = 0.0 },
    @{ Name = "a real sky at weight 0.5 (dusk)"; Publish = $true; Weight = 0.5 },
    @{ Name = "a real sky at weight 1 (day)"; Publish = $true; Weight = 1.0 }
)
foreach ($case in $cubeCases) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($i in 0..32) { $lines.Add("table " + $i + " " + (N $tbl[$i])) }
    $lines.Add("_RGWaterGain " + (N $gain) + " 1 0 0")
    $lines.Add("_RGWaterLight " + (N $cubeLight[0]) + " " + (N $cubeLight[1]) + " " + (N $cubeLight[2]) + " 0")
    foreach ($f in 0..5) {
        $lines.Add("theme " + $f + " " + (N $themeFaces[$f][0]) + " " + (N $themeFaces[$f][1]) + " " + (N $themeFaces[$f][2]))
        if ($case.Publish) { $lines.Add("real " + $f + " " + (N $realFaces[$f][0]) + " " + (N $realFaces[$f][1]) + " " + (N $realFaces[$f][2])) }
    }
    $lines.Add("weight " + (N $case.Weight))
    [System.IO.File]::WriteAllLines((Join-Path $project "cube-test-input.txt"), $lines)

    $out = Join-Path $project "cube-test-output.txt"
    if (Test-Path $out) { Remove-Item $out -Force }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
        "-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "WaterCubeTest.Run",
        "-logFile", "`"$(Join-Path $project 'cube-test.log')`"")
    if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\cube-test.log"; exit 1 }
    $got = Get-Content $out
    Write-Host ("cube, {0}: {1}; {2}" -f $case.Name, $got[0], $got[1])
    $got | Where-Object { $_ -like "EXCEPTION*" } | ForEach-Object { Write-Host $_; $script:failed++ }
    $worst = 0.0; $detail = ""
    foreach ($line in ($got | Where-Object { $_ -like "face *" })) {
        $q = $line.Split(' ')
        $f = [int]$q[1]
        $shader = Three $q 2
        # The four centre texels read back lie half a texel off the axis: on a side face two look
        # 0.9 deg up, two 0.9 deg down, and rho changes fast near the horizon -- averaged as read.
        $half = 1.0 / 64.0
        $n = [Math]::Sqrt(1.0 + 2.0 * $half * $half)
        $ys = if ($faceUp[$f] -ne 0.0) { @(($faceUp[$f] / $n)) * 4 } else { @(($half / $n), ($half / $n), (-$half / $n), (-$half / $n)) }
        $rho = 0.0
        foreach ($y in $ys) { $rho += $optics::Lookup($tbl, [Math]::Max($y, 0.0)) / 4.0 }
        $want = [double[]]@(0.0, 0.0, 0.0)
        foreach ($k in 0..2) {
            $sky = $themeFaces[$f][$k] * $cubeLight[$k] * (1.0 - $case.Weight) + $realFaces[$f][$k] * $case.Weight
            $want[$k] = $rho * $gain * $sky / $cubeLight[$k]
        }
        $w = Worst $shader $want
        if ($w -gt $worst) { $worst = $w; $detail = "face {0}: shader {1} model {2}" -f $f, (Show $shader), (Show $want) }
    }
    Check ("  every face = rho x gain x the sky blended / light") ($worst -lt 0.005) ("worst {0:P2} ({1})" -f $worst, $detail)
}
Remove-Item (Join-Path $project "cube-test-output.txt") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $project "cube-test-input.txt") -ErrorAction SilentlyContinue

# ---- the ocean waves (UnityProject\Assets\Editor\WaterWavesTest.cs) ---------------------------
# The FFT on the GPU against WaterSpectrum's CPU reference, field by field, and the composed ripple
# texture byte by byte -- on Direct3D 11 and on OpenGL Core (a NaN only OpenGL made, 2026-10-17).
foreach ($api in @("d3d11", "glcore")) {
    $out = Join-Path $project "waves-test-output.txt"
    if (Test-Path $out) { Remove-Item $out -Force }
    $wavesArgs = @("-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "WaterWavesTest.Run",
        "-logFile", "`"$(Join-Path $project 'waves-test.log')`"")
    if ($api -eq "glcore") { $wavesArgs += "-force-glcore" }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $wavesArgs
    if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\waves-test.log"; exit 1 }
    $got = Get-Content $out
    Write-Host ("ocean waves, {0}: {1}" -f $api, $got[0])
    foreach ($line in ($got | Where-Object { $_ -like "ok *" -or $_ -like "FAIL*" -or $_ -like "EXCEPTION*" })) {
        Write-Host ("  " + $line)
        if ($line -notlike "ok *") { $failed++ }
    }
    Remove-Item $out -ErrorAction SilentlyContinue
}

# ---- the model's glass on its LOD (UnityProject\Assets\Editor\LodGlassTest.cs) -----------------
# The read-back of a model's shine map and two LODs' rectangles of an atlas, texel by texel; then a
# tower whose LOD is laid out unlike its model compared, its mask uploaded (Alpha8, every mip by
# LoadRawTextureData) and read back byte for byte -- on Direct3D 11 and on OpenGL Core, whose
# render targets run the other way up.
foreach ($api in @("d3d11", "glcore")) {
    $out = Join-Path $project "lod-glass-test-output.txt"
    if (Test-Path $out) { Remove-Item $out -Force }
    $lodArgs = @("-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "LodGlassTest.Run",
        "-logFile", "`"$(Join-Path $project 'lod-glass-test.log')`"")
    if ($api -eq "glcore") { $lodArgs += "-force-glcore" }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $lodArgs
    if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\lod-glass-test.log"; exit 1 }
    $got = Get-Content $out
    Write-Host ("LOD glass, {0}: {1}" -f $api, $got[0])
    foreach ($line in ($got | Where-Object { $_ -like "ok *" -or $_ -like "FAIL*" -or $_ -like "EXCEPTION*" })) {
        Write-Host ("  " + $line)
        if ($line -notlike "ok *") { $failed++ }
    }
    Remove-Item $out -ErrorAction SilentlyContinue
}
# ---- the glass reflection's history (UnityProject\Assets\Editor\GlassReflectionTest.cs) -----------
# GlassReflection.shader's pass 1 on synthetic inputs: last frame's value held to this frame's 3 x 3
# box, blended at the weight inside it, none without history or glass; and the history fetched where
# the reflection's virtual point was on last frame's screen after a known camera move -- on Direct3D
# 11 and on OpenGL Core.
foreach ($api in @("d3d11", "glcore")) {
    $out = Join-Path $project "glass-reflection-test-output.txt"
    if (Test-Path $out) { Remove-Item $out -Force }
    $grArgs = @("-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "GlassReflectionTest.Run",
        "-logFile", "`"$(Join-Path $project 'glass-reflection-test.log')`"")
    if ($api -eq "glcore") { $grArgs += "-force-glcore" }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $grArgs
    if (-not (Test-Path $out)) { Write-Host "FAIL  Unity wrote no output (exit $($proc.ExitCode)); see UnityProject\glass-reflection-test.log"; exit 1 }
    $got = Get-Content $out
    Write-Host ("glass reflection history, {0}: {1}" -f $api, $got[0])
    foreach ($line in ($got | Where-Object { $_ -like "ok *" -or $_ -like "FAIL*" -or $_ -like "EXCEPTION*" })) {
        Write-Host ("  " + $line)
        if ($line -notlike "ok *") { $failed++ }
    }
    Remove-Item $out -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -eq 0) { Write-Host "all passed" } else { Write-Host "$failed FAILED"; exit 1 }
