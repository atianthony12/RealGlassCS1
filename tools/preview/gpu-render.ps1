# Renders a block of towers with the REAL glass shader on this machine's GPU: Unity 5.6 (the
# editor that builds the bundles) draws the city through the same Graphics.DrawMesh + property
# block path the mod uses, with the mod's own uniforms, screen copy and sky cube, in the game's
# legacy deferred lighting (UnityProject\Assets\Editor\GlassPreview.cs), and this script lays the
# frames out as a labelled contact sheet in tools\preview\out\<Name>.png.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview\gpu-render.ps1
#   ... -Views "aerial,steep,street,low,night,close"   one row each
#   ... -Variants "vanilla|glass|glass:Trace=0"         one column each; "glass:" takes GlassParams
#                                                       overrides (Field=value;Field=value)
#   ... -Width 640 -Height 360 -Sun 1.3 -Exposure 0.6 -Name sheet
#   ... -Api glcore                                     the OpenGL Core shaders (Linux's, and the Mac's
#                                                       fallback) on this GPU instead of Direct3D 11
#   ... -Storey 3.0                                     the stand-ins painted with storeys this tall (3.8 m)
#   ... -Cards                                          the facade cards (a preview round, 2026-11-12): every
#                                                       building's card baked with the mod's FacadeCards
#                                                       (pictures cards-*.png beside the frames); the variant
#                                                       keys Cards=1 (the reflection pass compiled with them),
#                                                       Refine=0, CardRay=1, CardDebug=1|2 (with Debug=16);
#                                                       -CardSize "atlas,tileW,tileH" (2048,32,64)
#   ... -Shapes                                         four more towers east of the city for the rooms'
#                                                       guards (views "shapes", "nightshapes"): a round one
#                                                       shaded smooth, a box with smoothed corners, a glass
#                                                       pyramid, a 12-sided one shaded flat

param(
    [int]$Width = 480,
    [int]$Height = 270,
    [string]$Views = "aerial,steep,street,low,night",
    [string]$Variants = "vanilla|glass",
    [double]$Sun = 2.7,
    [double]$Exposure = 0.4,
    [string]$Name = "gpu",
    [ValidateSet("d3d11", "glcore")]
    [string]$Api = "d3d11",
    [double]$Storey = 3.8,
    [switch]$Shapes,
    # Five towers west of the city (views framed, framedmid, framedfar), glass all over with frames as
    # their own geometry 0.02 / 0.05 / 0.1 / 0.25 / 1 m in front of it ("glass:Ofs=f,u" sets the
    # glass pass's depth Offset).
    [switch]$Framed,
    # Real vehicle assets for the street south of the middle block (views car, carback, cars,
    # carsabove, carsfar, night...): Workshop .crp files on this machine, ";"-separated, read by the
    # editor (Assets\Editor\PreviewVehicles.cs) -- preview pictures only, nothing of them is copied.
    [string]$Vehicles = "",
    # See-through props made glass or water, for the plaza south of the street (views props,
    # propslow, propsnorth, propsfar): "1:<.crp>" glass, "2:<.crp>" water, ";"-separated, read the
    # same way. Keys Prop=0, Prop.Refl=x, Prop.Clarity=x, Prop.Shine=x, Prop.Surface=1|2.
    [string]$Props = "",
    [switch]$Cards,
    [string]$CardSize = "",
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$root = Resolve-Path (Join-Path $here "..\..")
$project = Join-Path $root "UnityProject"
$out = Join-Path $here "out"
$frames = Join-Path $out "gpu-frames"
$log = Join-Path $project "gpu.log"
New-Item -ItemType Directory -Force $out, $frames | Out-Null
Get-ChildItem $frames -Filter "*.png" | Remove-Item -Force
Get-ChildItem $frames -Filter "*.raw" | Remove-Item -Force

if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity" }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "sync-sim.ps1")

$inv = [Globalization.CultureInfo]::InvariantCulture
$env:RG_OUT = $frames
$env:RG_W = "$Width"
$env:RG_H = "$Height"
$env:RG_VIEWS = $Views
$env:RG_VARIANTS = $Variants
$env:RG_SUN = $Sun.ToString($inv)
$env:RG_EXPOSURE = $Exposure.ToString($inv)
$env:RG_STOREY = $Storey.ToString($inv)
$env:RG_SHAPES = $(if ($Shapes) { "1" } else { "0" })
$env:RG_FRAMED = $(if ($Framed) { "1" } else { "0" })
$env:RG_VEHICLES = $Vehicles
$env:RG_PROPS = $Props
if ($Cards) { $env:RG_CARDS = "1" }
if ($CardSize -ne "") { $env:RG_CARDSIZE = $CardSize }

if (Test-Path $log) { Remove-Item $log -Force }
$sw = [Diagnostics.Stopwatch]::StartNew()
$unityArgs = @(
    "-batchmode", "-quit",
    "-projectPath", "`"$project`"",
    "-executeMethod", "GlassPreview.Render",
    "-logFile", "`"$log`"")
if ($Api -eq "glcore") { $unityArgs += "-force-glcore" }
$p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $unityArgs
"Unity ran for $([int]$sw.Elapsed.TotalSeconds) s, exit code $($p.ExitCode)"

$lines = Get-Content $log
$lines | Where-Object { $_ -match "RG PREVIEW|RG FRAME|Shader error|Shader warning|error CS|RG PREVIEW FAILED" } | ForEach-Object { $_ }
if ($p.ExitCode -ne 0 -or -not ($lines | Where-Object { $_ -eq "RG PREVIEW OK" })) {
    $lines | Select-Object -Last 40
    throw "the GPU preview did not finish; see $log"
}

$frameLines = @($lines | Where-Object { $_ -match "^RG FRAME " } | ForEach-Object {
    $m = [regex]::Match($_, "^RG FRAME row=(\d+) col=(\d+) view=(\S+) variant=(.+?) file=(.+?) \| (.*)$")
    @{ Row = [int]$m.Groups[1].Value; Col = [int]$m.Groups[2].Value; View = $m.Groups[3].Value; Variant = $m.Groups[4].Value;
       File = $m.Groups[5].Value; Info = $m.Groups[6].Value }
})
if ($frameLines.Count -eq 0) { throw "no frames were rendered" }

$rows = ($frameLines | ForEach-Object { $_.Row } | Measure-Object -Maximum).Maximum + 1
$cols = ($frameLines | ForEach-Object { $_.Col } | Measure-Object -Maximum).Maximum + 1
$labelH = 16
$sheet = New-Object System.Drawing.Bitmap ([int]($Width * $cols)), ([int](($Height + $labelH) * $rows))
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font "Segoe UI", 8
$brush = [System.Drawing.Brushes]::White

foreach ($f in $frameLines) {
    $img = [System.Drawing.Image]::FromFile($f.File)
    $g.DrawImage($img, $f.Col * $Width, $f.Row * ($Height + $labelH) + $labelH, $Width, $Height)
    $img.Dispose()
    $g.DrawString(("{0}  {1}" -f $f.View, $f.Variant), $font, $brush, ($f.Col * $Width + 2), ($f.Row * ($Height + $labelH) + 1))
}
$g.Dispose()

$file = Join-Path $out ("{0}.png" -f $Name)
$sheet.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
"saved $file"
