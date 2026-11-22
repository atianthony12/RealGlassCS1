# The GitHub issues' probe: the same tests on each released version and on the fixed code, each in its
# own git worktree (detached), so old and new are the commits themselves, not a re-creation.
#   powershell -File tools\issue-probe\run.ps1                       every version, both arms
#   ... -Versions head -Arms depth                                   one version, one arm
#   ... -Api glcore                                                  the OpenGL Core shaders
# Arms: depth (IssueProbe.cs on the mod's WaterSurfaceMask: the lines round trees / land / shores) and
# glass (each version's own GlassPreview with a Dynamic Resolution hook: the glass's reflections).
param(
    [string[]]$Versions = @('v200', 'v201', 'head'),
    [string[]]$Arms = @('depth', 'glass'),
    [ValidateSet('d3d11', 'glcore')][string]$Api = 'd3d11',
    [string]$Root = (Join-Path $env:TEMP 'rg-issue-probe'),
    [string]$Out = '',
    [string]$Factors = '0.5,0.75,1,1.25,1.5,2,2.11,3',
    [string]$ReferenceFactors = '1.5,2.11',
    [string]$GlassViews = 'street,close,near,nightclose',
    [string]$Unity = 'C:\Program Files\Unity\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
if ($Out -eq '') { $Out = Join-Path $repo "tools\preview\out\issue-probe-$Api" }
New-Item -ItemType Directory -Force $Out | Out-Null
$commits = @{ v200 = '6c9250e'; v201 = '3e033ba'; head = 'a9d447e' }
# "-Versions v200,v201" through -File arrives as one string: split every list on commas.
$Versions = @($Versions | ForEach-Object { $_.Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
$Arms = @($Arms | ForEach-Object { $_.Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
foreach ($v in $Versions) { if (-not $commits.ContainsKey($v)) { throw "unknown version $v (known: $($commits.Keys -join ", "))" } }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Prepare([string]$v) {
    $wt = Join-Path $Root $v
    if (-not (Test-Path $wt)) {
        & git -C $repo worktree add --detach $wt $commits[$v] | Out-Null
        $lib = Join-Path $repo 'UnityProject\Library'
        if (Test-Path $lib) { Copy-Item -Recurse $lib (Join-Path $wt 'UnityProject\Library') }
    }
    $editor = Join-Path $wt 'UnityProject\Assets\Editor'
    $runtime = Join-Path $wt 'UnityProject\Assets\IssueProbe'
    New-Item -ItemType Directory -Force $runtime | Out-Null
    Copy-Item (Join-Path $here 'IssueProbe.cs') $editor -Force
    Copy-Item (Join-Path $here 'IssueProbeEffects.shader') $editor -Force
    Copy-Item (Join-Path $here 'IssueProbeDrCamera.cs') $runtime -Force
    $preview = Join-Path $editor 'GlassPreview.cs'
    if (-not (Select-String -LiteralPath $preview -Pattern 'ISSUE PROBE' -Quiet)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'hook-glass-preview.ps1') -File $preview | Out-Null
    }
    return $wt
}

function UnityRun([string]$wt, [string]$method, [string]$log) {
    $project = Join-Path $wt 'UnityProject'
    $args = @('-batchmode', '-quit', '-projectPath', "`"$project`"", '-executeMethod', $method, '-logFile', "`"$log`"")
    if ($Api -eq 'glcore') { $args += '-force-glcore' }
    $p = Start-Process -FilePath $Unity -WindowStyle Hidden -Wait -PassThru -ArgumentList $args
    return $p.ExitCode
}

foreach ($v in $Versions) {
    $wt = Prepare $v
    "== $v ($($commits[$v])) in $wt"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $wt 'tools\preview\sync-sim.ps1') | Out-Null

    if ($Arms -contains 'depth') {
        $env:ISSUE_OUT = $Out
        $env:ISSUE_VERSION = $v
        $log = Join-Path $Out "depth-$v.log"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $code = UnityRun $wt 'IssueProbe.Run' $log
        $result = Join-Path $Out "depth-$v.txt"
        "depth arm: Unity exit $code in $([int]$sw.Elapsed.TotalSeconds) s; " + $(if (Test-Path $result) { (Get-Content $result -Tail 1) } else { 'no result file' })
        $dims = @(Select-String -LiteralPath $log -Pattern 'Dimensions of color surface' -ErrorAction SilentlyContinue).Count
        "depth arm: 'Dimensions of color surface' in the editor log: $dims"
        if ($code -ne 0 -or -not (Test-Path $result)) { Get-Content $log -Tail 30; throw "the depth arm failed for $v" }
    }

    if ($Arms -contains 'glass') {
        # The screen's size as the depth arm read it (the same editor), else 640 x 480.
        $sw0 = 640; $sh0 = 480
        $first = Join-Path $Out "depth-$v.txt"
        if (Test-Path $first) {
            $m = [regex]::Match((Get-Content $first -TotalCount 1), 'is (\d+) x (\d+)')
            if ($m.Success) { $sw0 = [int]$m.Groups[1].Value; $sh0 = [int]$m.Groups[2].Value }
        }
        # Where a glass pixel has its own reflection does not depend on the settings (a 2.11x run of all eight:
        # screen trace, finished picture, neighbours, quality, history, Hi-Z, rooms gave the same counts per view):
        # three of them, then the picture itself, with the screen trace on and off.
        $variants = 'glass:Debug=15|glass:Debug=15;Trace=0|glass:Debug=15;Interiors=1|glass|glass:Trace=0'
        $runs = @()
        foreach ($f in $Factors.Split(",")) { if ($f.Trim() -ne "") { $runs += ,@($f.Trim(), "1") } }
        foreach ($f in $ReferenceFactors.Split(",")) { if ($f.Trim() -ne "") { $runs += ,@($f.Trim(), "0") } }
        foreach ($r in $runs) {
            $f = [double]::Parse($r[0], $inv); $dr = $r[1]
            $w = [int][Math]::Round($sw0 * $f); $h = [int][Math]::Round($sh0 * $f)
            $name = "glass-$v-x$($r[0])" + $(if ($dr -eq '0') { '-ref' } else { '' })
            $env:RG_DR = $dr
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $lines = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $wt 'tools\preview\gpu-render.ps1') -Width $w -Height $h `
                       -Views $GlassViews -Variants $variants -Name $name -Api $Api 2>&1
            $lines | Out-File (Join-Path $Out "$name.txt") -Encoding utf8
            $glog = Join-Path $wt 'UnityProject\gpu.log'
            $dims = @(Select-String -LiteralPath $glog -Pattern 'Dimensions of color surface' -ErrorAction SilentlyContinue).Count
            "dims=$dims" | Out-File (Join-Path $Out "$name.txt") -Append -Encoding utf8
            # Keep each frame (the folder is emptied by the next run).
            $frames = Join-Path $wt 'tools\preview\out\gpu-frames'
            $keep = Join-Path $Out "$name-frames"
            New-Item -ItemType Directory -Force $keep | Out-Null
            Get-ChildItem $frames -Filter '*.png' | Copy-Item -Destination $keep -Force
            $sheet = Join-Path $wt "tools\preview\out\$name.png"
            if (Test-Path $sheet) { Copy-Item $sheet $Out -Force }
            "glass arm: $name ($w x $h, DR $dr) $([int]$sw.Elapsed.TotalSeconds) s, 'Dimensions of color surface' $dims"
        }
        Remove-Item Env:\RG_DR -ErrorAction SilentlyContinue
    }
}
"results in $Out"
