# The mirror measurement's sheets (2026-11-21): a contact sheet from a foam-render -MirrorProbe run's frames
# (copied out of tools\preview\out\foam-frames, which the next run clears). One row a view, one column a frame
# suffix: "2" = <view>_2.png (the frame as the mod draws it), "2_I" = the one-mirror picture, "2_est" = the
# composite's sky estimate against the sky the layer put in (red takes more, blue less; white equal).
#   powershell -File tools\preview\mirror-sheet.ps1 -Frames tools\preview\out\mirror-frames\mirror-A `
#     -Views "mirror@60,mirror@33,mirrorcity@60" -Columns "2,3,2_I,2_est" -Labels "100%|5%|I|est" -Name mirror-measure-clouds
param(
    [string]$Frames,
    [string]$Views,
    [string]$Columns,
    [string]$Labels,
    [string]$Name,
    [string]$Title = "",
    [double]$Scale = 0.5,
    # Instead of -Views x -Columns: rows "label=frame,frame,...;label=..." (frame = a file name without .png).
    [string]$Cells = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "out"
$names = $Labels.Split('|')
if ($Cells -ne "") {
    $rows = @(); $files = @()
    foreach ($r in $Cells.Split(';')) { $kv = $r.Split('='); $rows += $kv[0]; $files += ,($kv[1].Split(',')) }
    $cols = $files[0]
} else {
    $rows = $Views.Split(',')
    $cols = $Columns.Split(',')
    $files = @(); foreach ($v in $rows) { $files += ,@($cols | ForEach-Object { "{0}_{1}" -f $v, $_ }) }
}
$first = [System.Drawing.Image]::FromFile((Join-Path $Frames ($files[0][0] + ".png")))
$w = [int]($first.Width * $Scale); $h = [int]($first.Height * $Scale)
$first.Dispose()
$top = if ($Title -ne "") { 44 } else { 22 }
$side = 110
$sheet = New-Object System.Drawing.Bitmap ($side + $cols.Count * $w), ($top + $rows.Count * $h)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$font = New-Object System.Drawing.Font "Segoe UI", 10
$brush = [System.Drawing.Brushes]::White
if ($Title -ne "") { $g.DrawString($Title, $font, $brush, 6, 2) }
for ($c = 0; $c -lt $cols.Count; $c++) { $g.DrawString($names[$c], $font, $brush, ($side + $c * $w + 6), ($top - 20)) }
for ($r = 0; $r -lt $rows.Count; $r++) {
    $g.DrawString($rows[$r], $font, $brush, 6, ($top + $r * $h + 6))
    for ($c = 0; $c -lt $cols.Count; $c++) {
        $img = [System.Drawing.Image]::FromFile((Join-Path $Frames ($files[$r][$c] + ".png")))
        $g.DrawImage($img, ($side + $c * $w), ($top + $r * $h), $w, $h)
        $img.Dispose()
    }
}
$g.Dispose()
$path = Join-Path $out ($Name + ".png")
$sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Host $path
