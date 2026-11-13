# Pictures of the water's foam with the REAL shaders on this machine's GPU (Unity 5.6,
# UnityProject\Assets\Editor\FoamPreview.cs): a coast built as the game's own water data (surface
# heights in two simulation frames, terrain heights, the surface maps of normal, flow and depth), a
# stand-in of the game's water on it (Editor\PreviewGameWater.shader), and the foam layer
# (RealGlass/WaterFoam) on the same data, as the mod draws it on the game's water meshes. The mod's
# own numbers (FoamOptics, the ripples, the reflectance table, the city reflection's) are read from
# the built DLL. Laid out as a labelled contact sheet in tools\preview\out\<Name>.png -- one row a
# view, one column a shot.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview\foam-render.ps1
#   ... -Views "coast,close,high,river,tsunami,sunset,night"
#   ... -Views "tsunamiwall,tsunamiwallcity"   a 20 m tsunami wall from 9 m up (below its crest) and from
#                             a city camera: nothing behind its face may show through it
#   ... -Views "under,undersun,underup,underdown,underhalf,underbridge,underbridgedeep" -UnderColumns "off,30:0:0,30:0:1"
#                             the view from under the water (RealGlass/WaterUnder): each column "off" (no such
#                             pass: what a camera under the water sees today) or "visibility m:caustics:shafts"
#                             (caustics 0 none, 1 real; shafts 0 none, 1 through the sun's shadow map); the
#                             water as the mod's defaults (the sky layer, #0E222B, FFT waves, glitter 35% at
#                             50%, foam); the under views add a bridge over the sea and the sun's shadows;
#                             "today:60:0:1" = the pass as it stood at affebf9 (Editor/PreviewUnderToday.shader)
#   ... -Views "underlook,underzenith,underaway" -UnderColumns "today:60:0:1,60:0:1"   the surface from below
#                             (2026-10-30, the author's reference): 8 m under the open sea looking up 40 deg
#                             toward the sun, 80 deg up, and 25 deg up away from it
#   ... -Views "undertop,underbridge,overbridge" -UnderColumns "today:60:0:1,60:0:1"   at the waterline: 40 cm
#                             under looking 12 deg up at the town, 5 cm under / over by the bridge (only the
#                             camera over the water gets the near plane's waterline across the frame)
#   ... -Later 3              also the foam this many seconds later (the motion; 0: no such column)
#   ... -Amount 1 -Shore 1 -Fast 1 -Front 1     the foam's amount and each source's share (1 = the model's)
#   ... -Sources              a last column: each source's amount as a colour (red shore, green fast, blue fronts)
#   ... -Width 960 -Height 540 -Name foam -Api glcore
#   ... -SkyColumns "old:0.8,old:0.3,new:1"   columns of the sky on the water instead of the foam's:
#                             old:g = the mod's cube until 2026-10-08 (the stand-in's) at Sky slider g,
#                             new:g = the sky layer (RealGlass/WaterSky) at g; an optional third part
#                             is the water's colour (new:1:#0E222B, "-" for the default); a fourth the
#                             ripples, "sines" (the 32 waves, default) or "fft" (WaterSpectrum's waves,
#                             RealGlass/WaterWaves); a fifth the sun's glitter on the sky layer (1 = real
#                             water; 0 or none: the game's own highlight kept) -- new:1:-:fft:1; a
#                             sixth its size (every facet's slope x this, its peak kept; default 1); a
#                             seventh "fog": the game's fog over the sky in the water's cube (GlassUtil's
#                             SkyFog, the mod's since 2026-10-30; with -GameSky)
#   ... -GameSky -Views "sunset@2,sunset@0,sunsetback@0,glitter30@1"   the game's own skybox
#                             (Editor/PreviewGameSky.shader) and its fog's inputs, the game's fog over the
#                             sky on screen; "view@e" puts the sunset's sun e degrees up, "...back" behind
#                             the camera: the water red at sunset (2026-10-30) --
#                             -SkyColumns "new:1:-:fft:0.35:0.5,new:1:-:fft:0.35:0.5:fog"
#   ... -Spectrum "7,50000,30,1,1"   the FFT waves: wind m/s, fetch m, heading deg, seed, choppiness
#   ... -Views "glitter30,glitter300"   web-ocean's views: 30 / 300 m over the sea, into a low sun;
#                             "glitterday": 400 m up, 35 deg down, a sun 30 deg up ahead (a city camera's)
#   ... -Body "#0E222B"       the stand-in water's colour for every shot (default the preview's own)
#   ... -RippleRms 0.527      the game's ripple texture's decoded rms (default WaterOptics' fallback, 0.3;
#                             the author's game measured 0.527)
#   ... -Strength 1.43        the Ripple strength (1 = 100%), as the mod passes it: Dynamic waves multiply it
#                             by WaterWeather.RoughnessGain (the city reflection's gain stays the game's texture's)
#   ... -DepthSkew 0.001      the stand-in's vertices each moved up to this (m) along their eye ray, at random:
#                             the rounding the game's own water program and ours differ by in the game (the
#                             driver compiles them apart); 0 = none, the two land on the same depth
#   ... -Fog -WaterDepth 0    stand-ins for the game's fog and Volumetric Weather's clouds (both tell the sky by
#                             the depth texture), with the water's surface left out of that texture as before
#                             2026-10-28 (1, the default: in it, as the mod does): the see-through tsunami
#   ... -Layers              the layered foam (2026-10-24): columns today's foam (Editor/PreviewFoamToday,
#                             frozen at 2b509fc), the layers, the layers -Later s on, and the layers' view
#                             (red fresh, green residual, blue the bubbles under); every column with the mod's
#                             defaults for the rest (the sky layer at WaterOptics.SkyShare, 5% since 2026-11-13; FFT waves, glitter 35% at size 50%,
#                             #0E222B or -Body's). Views photo (his reference's: 14 m over the surf), beach, and
#                             steep / steepclose / steephigh (the coast south of z = -450 drops as the game's do)
#   ... -Times "443,445,447"  with -Layers: a column of the layers at each of these seconds instead
#   ... -Corridors "0,1.5,1.25"   columns of the layered foam with the surf's distance held within a
#                             corridor round the real distance from the waterline (ShoreDistance), each
#                             its factor; 0 = no distance map (the depth over the slope alone, as before
#                             2026-10-25). Views bay / baylow: a bay 170 m into the steep coast, its floor
#                             a flat shelf (the author's screenshot of a ring of surf)
#   ... -Edges -Views "lumps,lumpsdown,lumpsclose,lumpsout,lumpssteep"   the foam's edges (2026-11-11):
#                             columns today's foam (Editor/PreviewFoamToday, frozen at 1a26e7e), the foam,
#                             and each one's distance from the waterline as lines every 10 m. The lumps
#                             views: a gentle and a steep coast with the ground lumpy from one of the
#                             game's 16 m points to the next (a brushed shore: the waterline a polygon)
#   ... -Seabed -DepthColumns "off:under=#2D798C,on:own=#2D798C,on:own=#2D798C:clarity=6" -Views "seabed,seabedclose"
#                             the realistic depth (2.0.0, RealGlass/WaterSky's REALISTIC DEPTH): each column
#                             "off" (the game's own seabed, as its program shows it), "on" (the row on: the
#                             game's taken away, ours drawn) or "masks" (where the stand-in draws, red, against
#                             where the sky layer draws, green: white both; counts in the log), then after it
#                             any of clarity=m (12), distortion=x and caustics=x (1 = 100%), under=... (the
#                             map theme's _WaterColorUnder as the game passes it: #RRGGBB, r/g/b or "game", its
#                             class default; neutral without), own=#RRGGBB (the Underwater color row's own,
#                             the theme's row off), bed=1 (the beach's pale sand all the way down) and foam=0
#                             (no foam layer, to see the bed under the surf). The rest
#                             the mod's defaults, as -Layers. -Seabed: the coast round z = 0 falls to 38 m and
#                             its bed is pale sand with a hard wavy edge to a dark bed from 5 m to 30 m deep,
#                             with metre-scale detail (Editor/PreviewSeabed.shader); views seabed (a city
#                             camera 160 m up), seabedclose (16 m up over the shallows), seabeddown (60 m over
#                             the edge), sunsetseabed, nightseabed. -GameWaterCheck 1: the stand-in shows the
#                             under colour as its shader sees it; 2: leaves the bed out. -ProbePixels "x y;x y":
#                             those pixels' raw values (before the exposure; y from the bottom) logged per shot.
# Run after build-bundle.ps1 (the shaders compile) and a DLL build.

param(
    [int]$Width = 960,
    [int]$Height = 540,
    [string]$Views = "coast,close,high,river,tsunami,sunset,night",
    [double]$Later = 3.0,
    [double]$Amount = 1.0,
    [double]$Shore = 1.0,
    [double]$Fast = 1.0,
    [double]$Front = 1.0,
    # Also a column of where each source puts foam (the shader's preview-only _RGFoamDebug).
    [switch]$Sources,
    # The flicker under temporal anti-aliasing, for these views (comma list; "" none): logged, and each
    # run's last resolved frame + flicker map in the frames folder. -Views "none" for no sheet.
    [string]$Flicker = "",
    [int]$TaaFrames = 48,
    [int]$TaaWarm = 24,
    [double]$Day = 1.0,
    [double]$Night = 2.05,
    [double]$Streaks = 0.036,
    [double]$Length = 1.65,
    [double]$ExposureDay = 1.6,
    [double]$ExposureNight = 2.5,
    [string]$SkyColumns = "",
    [string]$UnderColumns = "",
    [string]$DepthColumns = "",
    [switch]$Seabed,
    [int]$GameWaterCheck = 0,
    [string]$ProbePixels = "",
    [string]$Body = "",
    [string]$Spectrum = "7,50000,30,1,1",
    [double]$RippleRms = -1.0,
    [double]$Strength = 1.0,
    [double]$DepthSkew = 0.0,
    # The "today:" under columns read a depth that is at the near plane everywhere: what the author's game
    # showed on 2026-10-30 (the pass read the global depth texture late in the frame), for the comparison.
    [switch]$UnderTodayNear,
    # The water's surface in the depth texture after it (1, the mod's since 2026-10-28; 0 as before), and
    # stand-ins for the game's fog and Volumetric Weather's clouds, which read that texture (-Fog).
    [int]$WaterDepth = 1,
    [switch]$Fog,
    [switch]$GameSky,
    [switch]$Layers,
    [string]$Times = "",
    [string]$Corridors = "",
    # The foam's edges (2026-11-11): columns today's foam, the foam, and under each the surf's distance
    # from the waterline as lines every 10 m (the shape every bore's line takes).
    [switch]$Edges,
    # The water's dry points next to wet ones holding no more than those neighbours' level, as
    # WaterSimulation.FillHeightMap reads (IL, 2026-11-11): dry islands in the film of water up the beach.
    # Not settled which the game's picture matches; the foam is checked on both.
    [switch]$IlSheet,
    # With -Edges: a last column, magenta wherever the game's own test finds water.
    [switch]$Wet,
    # The flicker runs' surf corridor (-1 the mod's, 0 no distance map; as a -Corridors column).
    [double]$FlickerCorridor = -1.0,
    [string]$Name = "foam",
    [ValidateSet("d3d11", "glcore")]
    [string]$Api = "d3d11",
    [string]$Dll = (Join-Path $PSScriptRoot "..\..\RealGlass\bin\Debug\RealGlass.dll"),
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$project = Resolve-Path (Join-Path $here "..\..\UnityProject")
$out = Join-Path $here "out"
$shotDir = Join-Path $out "foam-frames"
New-Item -ItemType Directory -Force $out, $shotDir | Out-Null
Get-ChildItem $shotDir -Filter "*.png" | Remove-Item -Force

# The editor compiles FoamPattern.cs from the mod's own source.
& (Join-Path $here "sync-sim.ps1")

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path $Dll)))
$optics = $asm.GetType('RealGlass.Water.WaterOptics', $true)
$foam = $asm.GetType('RealGlass.Water.FoamOptics', $true)
# The sky's share on the water for every new install (WaterOptics.SkyShare, 5% since 2026-11-13; the
# columns below that stand for "the mod's defaults" draw with it; new:g columns say their own).
$skyShare = [double]$optics::SkyShare
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
$rms = if ($RippleRms -gt 0.0) { $RippleRms } else { $optics::DefaultRippleRms }
# The preview draws its ripples at 0.5 x this: RippleSlope(rms, strength).
$lines.Add("ripplerms " + (N ($rms * $Strength)))
# The sky layer's ripple averaging (WaterOptics.SkyRippleMips at 100%) and the ripples' mean square slope.
$start = 0.0; $span = 0.0
$optics::SkyRippleMips(1.0, 1.0, [ref]$start, [ref]$span)
$slope = $optics::RippleSlope($rms, $Strength)
$lines.Add("skyripples " + (N $start) + " " + (N $span) + " " + $optics::RippleTexels + " " + (N ($slope * $slope)))
if ($Body -ne "") { $lines.Add("body " + $Body) }
if ($Spectrum -ne "") { $lines.Add("spectrum " + ($Spectrum.Split(',') -join " ")) }
$lines.Add("depthskew " + (N $DepthSkew))
if ($UnderTodayNear) { $lines.Add("undertodaynear 1") }
$lines.Add("waterdepth " + $WaterDepth)
$lines.Add("fog " + $(if ($Fog) { 1 } else { 0 }))
$lines.Add("citygain " + (N ($optics::CityRippleGain(1.0, $rms))))
$spread = $optics::Spread($optics::SlopeVariance($optics::CityWind))
$lines.Add("streak " + (N ($optics::StreakSigma($Streaks, $spread, 45.0))))
$lines.Add("length " + (N $Length))
$lines.Add("exposure " + (N $ExposureDay) + " " + (N $ExposureNight))
$v = $foam::Vectors($Amount, $Shore, $Fast, $Front, 40.0)
for ($i = 0; $i -lt 6; $i++) { $lines.Add("foam " + $i + " " + (N $v[4 * $i]) + " " + (N $v[4 * $i + 1]) + " " + (N $v[4 * $i + 2]) + " " + (N $v[4 * $i + 3])) }
# The layers' vectors (FoamOptics.LayerVectors).
$lv = $foam::LayerVectors()
for ($i = 0; $i -lt $lv.Length / 4; $i++) { $lines.Add("layer " + $i + " " + (N $lv[4 * $i]) + " " + (N $lv[4 * $i + 1]) + " " + (N $lv[4 * $i + 2]) + " " + (N $lv[4 * $i + 3])) }

$columns = New-Object System.Collections.Generic.List[object]
if ($UnderColumns -ne "") {
    foreach ($spec in $UnderColumns.Split(',')) {
        $part = @($spec.Split(':'))
        $vis = -1.0; $caustics = 0.0; $shafts = 0.0; $veil = 0.0
        # "today:..." = the pass as it stood at affebf9 (Editor/PreviewUnderToday.shader).
        $today = $part[0] -eq "today"
        if ($today) { $part = @($part | Select-Object -Skip 1) }
        if ($part[0] -ne "off") {
            $vis = [double]::Parse($part[0], $inv)
            if ($part.Count -gt 1) { $caustics = [double]::Parse($part[1], $inv) }
            if ($part.Count -gt 2) { $shafts = [double]::Parse($part[2], $inv) }
            if ($part.Count -gt 3) { $veil = [double]::Parse($part[3], $inv) }
        }
        $label = if ($vis -lt 0.0) { "today: no underwater view" } else {
            ("underwater, visibility {0} m" -f $vis) + $(if ($caustics -gt 0.0) { (", caustics {0:P0}" -f $caustics) } else { "" }) +
            $(if ($shafts -gt 1.5) { ", the shafts' lit share" } elseif ($shafts -gt 0.0) { ", sun shafts" } else { ", no shafts" }) +
            $(if ($veil -gt 0.0) { (", veil x{0}" -f $veil) } else { "" }) }
        if ($today -and $vis -ge 0.0) { $label = "today: " + $label }
        elseif ($vis -ge 0.0) { $label = "new surface from below: " + $label }
        $columns.Add(@{ Foam = 1; Seconds = 443.0; Mode = 1; Gain = $skyShare; Body = "#0E222B"; Waves = 1; Glitter = 0.35; GlitterSize = 0.5;
                        Corridor = -1.0; Under = $vis; Caustics = $caustics; Shafts = $shafts; VeilGain = $veil; UnderToday = $today; Label = $label })
    }
}
elseif ($DepthColumns -ne "") {
    # Realistic depth: the mod's defaults for everything else (as -Layers), one column a spec.
    $sky = @{ Mode = 1; Gain = $skyShare; Body =$(if ($Body -ne "") { $Body } else { "#0E222B" }); Waves = 1; Glitter = 0.35; GlitterSize = 0.5 }
    function Rgb([string]$u) {
        if ($u -eq "game") { return @(0.55, 0.45, 0.4) }
        if ($u.StartsWith("#")) { return @(([Convert]::ToInt32($u.Substring(1, 2), 16) / 255.0), ([Convert]::ToInt32($u.Substring(3, 2), 16) / 255.0), ([Convert]::ToInt32($u.Substring(5, 2), 16) / 255.0)) }
        return @($u.Split('/') | ForEach-Object { [double]::Parse($_, $inv) })
    }
    foreach ($spec in $DepthColumns.Split(',')) {
        $part = $spec.Split(':')
        $on = $part[0] -eq "on"; $masks = $part[0] -eq "masks"
        $clarity = 12.0; $distortion = 1.0; $caustics = 1.0; $under = $null; $own = $null; $bed = 0; $foamOn = 1
        $label = if ($masks) { "masks: white both, red the game's water only, green the sky layer only" } elseif ($on) { "realistic depth" } else { "realistic depth off" }
        foreach ($t in ($part | Select-Object -Skip 1)) {
            $kv = $t.Split('=')
            switch ($kv[0]) {
                "clarity" { $clarity = [double]::Parse($kv[1], $inv); $label += (", clarity {0} m" -f $clarity) }
                "distortion" { $distortion = [double]::Parse($kv[1], $inv); $label += (", distortion {0:P0}" -f $distortion) }
                "caustics" { $caustics = [double]::Parse($kv[1], $inv); $label += (", sunlight {0:P0}" -f $caustics) }
                "under" { $under = Rgb $kv[1]; $label += ", theme's under " + $kv[1] }
                "own" { $own = Rgb $kv[1]; $label += ", underwater color " + $kv[1] }
                "bed" { $bed = [int]$kv[1]; $label += $(if ($bed -eq 1) { ", pale bed" } else { "" }) }
                "foam" { $foamOn = [int]$kv[1]; if ($foamOn -eq 0) { $label += ", no foam" } }
            }
        }
        if ($on -and $part.Count -eq 1) { $label += " (12 m, 100%, 100%)" }
        $columns.Add(($sky + @{ Foam = $(if ($masks -or $foamOn -eq 0) { 0 } else { 1 }); Seconds = 443.0; Depth = $(if ($on -or $masks) { 1 } else { 0 }); Clarity = $clarity; Distortion = $distortion;
                                DepthCaustics = $caustics; Masks = $masks; Bed = $bed; UnderColour = $under; OwnUnder = $own; Label = $label }))
    }
}
elseif ($Corridors -ne "") {
    # The layered foam with the mod's defaults (as -Layers), one column per corridor factor.
    $sky = @{ Mode = 1; Gain = $skyShare; Body =$(if ($Body -ne "") { $Body } else { "#0E222B" }); Waves = 1; Glitter = 0.35; GlitterSize = 0.5 }
    foreach ($s in $Corridors.Split(',')) {
        $k = [double]::Parse($s, $inv)
        $label = if ($k -eq 0.0) { "no distance map (as before)" } else { "corridor x{0}" -f $k }
        $columns.Add(($sky + @{ Foam = 1; Seconds = 443.0; Corridor = $k; Label = $label }))
    }
}
elseif ($Edges) {
    # The mod's defaults (as -Layers); -Times for other seconds than 443 (the first of them).
    $sky = @{ Mode = 1; Gain = $skyShare; Body =$(if ($Body -ne "") { $Body } else { "#0E222B" }); Waves = 1; Glitter = 0.35; GlitterSize = 0.5 }
    $sec = if ($Times -ne "") { [double]::Parse($Times.Split(',')[0], $inv) } else { 443.0 }
    $columns.Add(($sky + @{ Foam = 3; Seconds = $sec; Label = "today's foam" }))
    $columns.Add(($sky + @{ Foam = 1; Seconds = $sec; Label = "the foam, edges redone" }))
    $columns.Add(($sky + @{ Foam = 6; Seconds = $sec; Label = "today: every 10 m from the waterline, the surf's edge yellow" }))
    $columns.Add(($sky + @{ Foam = 5; Seconds = $sec; Label = "redone: every 10 m from the waterline" }))
    if ($Wet) { $columns.Add(($sky + @{ Foam = 7; Seconds = $sec; Label = "magenta: where the game's own test finds water" })) }
}
elseif ($Layers) {
    # The mod's defaults for everything else: the sky layer at $skyShare, FFT waves, glitter 35% at size 50%, the
    # water #0E222B (or -Body's).
    $sky = @{ Mode = 1; Gain = $skyShare; Body =$(if ($Body -ne "") { $Body } else { "#0E222B" }); Waves = 1; Glitter = 0.35; GlitterSize = 0.5 }
    if ($Times -ne "") {
        # A time series of the layers (the surf's bores passing).
        foreach ($s in $Times.Split(',')) {
            $sec = [double]::Parse($s, $inv)
            $columns.Add(($sky + @{ Foam = 1; Seconds = $sec; Label = ("layered, t = {0:F1} s" -f $sec) }))
        }
    }
    else {
        $columns.Add(($sky + @{ Foam = 3; Seconds = 443.0; Label = "the foam before 2026-11-11 (1a26e7e)" }))
        $columns.Add(($sky + @{ Foam = 1; Seconds = 443.0; Label = "layered foam" }))
        if ($Later -gt 0.0) { $columns.Add(($sky + @{ Foam = 1; Seconds = 443.0 + $Later; Label = ("layered, {0:F1} s later" -f $Later) })) }
        $columns.Add(($sky + @{ Foam = 4; Seconds = 443.0; Label = "layers: red fresh, green residual, blue bubbles under" }))
    }
}
elseif ($SkyColumns -ne "") {
    foreach ($spec in $SkyColumns.Split(',')) {
        $part = $spec.Split(':')
        $mode = if ($part[0] -eq "new") { 1 } else { 0 }
        $gain = [double]::Parse($part[1], $inv)
        $colour = if ($part.Count -gt 2) { $part[2] } else { "-" }
        $waves = if ($part.Count -gt 3 -and $part[3] -eq "fft") { 1 } else { 0 }
        $glint = if ($part.Count -gt 4) { [double]::Parse($part[4], $inv) } else { 0.0 }
        $glintSize = if ($part.Count -gt 5) { [double]::Parse($part[5], $inv) } else { 1.0 }
        $skyFog = $part.Count -gt 6 -and $part[6] -eq "fog"
        $what = if ($mode -eq 1) { "sky per pixel" } else { "cube (until now)" }
        $label = "{0}, sky {1:P0}" -f $what, $gain
        if ($colour -ne "-") { $label += ", " + $colour }
        $label += if ($waves -eq 1) { ", FFT waves" } else { ", 32 waves" }
        $label += if ($glint -gt 0.0) { (", sun glitter {0:P0}" -f $glint) } else { ", the game's sun highlight" }
        if ($glint -gt 0.0 -and $part.Count -gt 5) { $label += (", size {0:P0}" -f $glintSize) }
        if ($skyFog) { $label += ", the game's fog in its sky" }
        $columns.Add(@{ Foam = 1; Seconds = 443.0; Mode = $mode; Gain = $gain; Body = $colour; Waves = $waves; Glitter = $glint; GlitterSize = $glintSize; SkyFog = $skyFog; Label = $label })
    }
}
else {
    $columns.Add(@{ Foam = 0; Seconds = 443.0; Label = "today (no foam layer)" })
    $columns.Add(@{ Foam = 1; Seconds = 443.0; Label = "foam" })
    if ($Later -gt 0.0) { $columns.Add(@{ Foam = 1; Seconds = 443.0 + $Later; Label = ("foam, {0:F1} s later" -f $Later) }) }
    if ($Sources) { $columns.Add(@{ Foam = 2; Seconds = 443.0; Label = "sources: red shore, green fast, blue fronts" }) }
}
if ($Flicker -ne "") {
    # Render It!'s TAA (PPv1): 60 frames a second, jitter spread 1, stationary blending 0.94, sharpen 0.15.
    $lines.Add("taa " + $TaaFrames + " " + $TaaWarm + " 60 1 0.94 0.15")
    $lines.Add("flickercorridor " + (N $FlickerCorridor))
    foreach ($view in $Flicker.Split(',')) {
        $strength = if ($view -like "night*") { $Night } else { $Day }
        $lines.Add(("seq flicker-{0} {0} 443 {1}" -f $view, (N $strength)))
    }
}
$rows = @(if ($Views -eq "none") { } else { $Views.Split(',') })
foreach ($view in $rows) {
    $strength = if ($view -like "night*") { $Night } else { $Day }
    for ($c = 0; $c -lt $columns.Count; $c++) {
        $col = $columns[$c]
        $sky = if ($col.ContainsKey("Mode")) { " {0} {1} {2} {3} {4} {5}" -f $col.Mode, (N $col.Gain), $col.Body, $col.Waves, (N $col.Glitter), (N $col.GlitterSize) } else { "" }
        if ($col.ContainsKey("Corridor")) { $sky += " " + (N $col.Corridor) }
        if ($col.ContainsKey("Under")) { $sky += " {0} {1} {2} {3} {4}" -f (N $col.Under), (N $col.Caustics), (N $col.Shafts), (N $col.VeilGain), $(if ($col.UnderToday) { "1" } else { "0" }) }
        $lines.Add(("shot {0}_{1} {0} {2} {3} {4}" -f $view, $c, $col.Foam, (N $col.Seconds), (N $strength)) + $sky)
        if ($col.SkyFog) { $lines.Add("skyfog 1") }
        if ($col.ContainsKey("Depth")) {
            $lines.Add(("depth {0} {1} {2} {3}" -f $col.Depth, (N $col.Clarity), (N $col.Distortion), (N $col.DepthCaustics)))
            $lines.Add("bed " + $col.Bed)
            if ($col.Masks) { $lines.Add("masks 1") }
            if ($col.UnderColour) { $lines.Add(("undercolour {0} {1} {2}" -f (N $col.UnderColour[0]), (N $col.UnderColour[1]), (N $col.UnderColour[2]))) }
            if ($col.OwnUnder) { $lines.Add(("ownunder {0} {1} {2}" -f (N $col.OwnUnder[0]), (N $col.OwnUnder[1]), (N $col.OwnUnder[2]))) }
        }
    }
}
if ($GameSky) { $lines.Add("gamesky 1") }
if ($IlSheet) { $lines.Add("ilsheet 1") }
if ($Seabed) { $lines.Add("seabed 1") }
if ($GameWaterCheck -ne 0) { $lines.Add("gamewatercheck " + $GameWaterCheck) }
if ($ProbePixels -ne "") { foreach ($at in $ProbePixels.Split(';')) { $lines.Add("probepixel " + $at.Trim()) } }
[System.IO.File]::WriteAllLines((Join-Path $project "foam-preview-input.txt"), $lines)

$output = Join-Path $project "foam-preview-output.txt"
if (Test-Path $output) { Remove-Item $output -Force }
$sw = [Diagnostics.Stopwatch]::StartNew()
$unityArgs = @(
    "-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", "FoamPreview.Render",
    "-logFile", "`"$(Join-Path $project 'foam-preview.log')`"")
if ($Api -eq "glcore") { $unityArgs += "-force-glcore" }
$p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $unityArgs
if (-not (Test-Path $output)) { throw "Unity wrote no output (exit $($p.ExitCode)); see UnityProject\foam-preview.log" }
$report = Get-Content $output
$report | ForEach-Object { Write-Host $_ }
if ($report | Where-Object { $_ -like "EXCEPTION*" }) { exit 1 }
Remove-Item $output, (Join-Path $project "foam-preview-input.txt") -ErrorAction SilentlyContinue
if ($rows.Count -eq 0) { Write-Host ("done ({0:F0} s)" -f $sw.Elapsed.TotalSeconds); exit 0 }

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
