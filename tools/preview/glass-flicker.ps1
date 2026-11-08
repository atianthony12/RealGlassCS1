# The glass's FLICKER under temporal anti-aliasing, measured on this machine's GPU: gpu-render.ps1's
# city (Editor\GlassPreview.cs) in its sequence mode (RG_SEQ), the water's harness for the glass
# (water-flicker.ps1): each run 64 frames with the projection jittered as Post-processing Stack v1's TAA
# jitters it (Halton 2, 3 over 8 frames), each frame resolved by its stand-in (Editor\PreviewTAA.shader),
# and after 32 frames to warm up each GLASS pixel's mean |second difference in time| of its displayed
# brightness (the glass: a frame in Glass.shader's mask view against one without glass). Printed per
# run (the glass's share over 2/255 and over 8/255, resolved and raw); one sheet in
# tools\preview\out\<Name>.png, per run the last resolved frame and its flicker map (grey = flicker x 25,
# red over 8/255).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview\glass-flicker.ps1
#   ... -Pan                       a moving camera (~0.3 px of parallax a frame), history fetched along
#                                  the depth's camera motion (PreviewTAA pass 1)
#   ... -Runs "vanilla|glass"      gpu-render.ps1 variants
#   ... -Jitter 1 -Blending 0.94 -Sharpen 0.15 -MotionBlending 0.85   his Render It! profile's TAA
# Measuring trap: the finished picture carries the previous run's frames; every run warms up 32.

param(
    [string]$Views = "close,near,street,aerial,nightclose,nightnear",
    [string]$Runs = "vanilla|glass",
    [int]$Width = 480,
    [int]$Height = 270,
    [int]$FrameCount = 64,
    [int]$Warm = 32,
    [double]$Jitter = 1.0,
    [double]$Blending = 0.94,
    [double]$Sharpen = 0.15,
    [double]$MotionBlending = 0.85,
    [switch]$Pan,
    [string]$Name = "glass-flicker",
    [ValidateSet("d3d11", "glcore")]
    [string]$Api = "d3d11"
)

$inv = [Globalization.CultureInfo]::InvariantCulture
$env:RG_SEQ = (@($FrameCount, $Warm, $Jitter, $Blending, $Sharpen, $MotionBlending, $(if ($Pan) { 1 } else { 0 })) | ForEach-Object { ([double]$_).ToString($inv) }) -join ","
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "gpu-render.ps1") -Views $Views -Variants $Runs -Width $Width -Height $Height -Name $Name -Api $Api
    Get-Content (Join-Path $PSScriptRoot "..\..\UnityProject\gpu.log") | Where-Object { $_ -match "^RG FLICKER" }
}
finally {
    Remove-Item Env:\RG_SEQ -ErrorAction SilentlyContinue
}
