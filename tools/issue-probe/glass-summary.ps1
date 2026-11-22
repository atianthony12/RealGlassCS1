# The glass arm's results: per version and factor, each view's glass pixels with their own reflection,
# a neighbour's (the fix) or none (the sky shows), and the editor's "Dimensions of color surface" count;
# then, at each reference factor, the plain picture against its reference in the band round glass edges.
param([string]$Out = (Join-Path $PSScriptRoot '..\preview\out\issue-probe-d3d11'), [string]$ReferenceFactor = '2.11')
$ErrorActionPreference = 'Stop'
$imgdiff = Join-Path $PSScriptRoot 'imgdiff.ps1'

function Frames([string]$txt) {
    $list = @()
    foreach ($l in (Get-Content $txt)) {
        $m = [regex]::Match($l, 'RG FRAME row=(\d+) col=(\d+) view=(\S+) variant=(.+?) file=(.+?) \| (.*)$')
        if (-not $m.Success) { continue }
        $i = [regex]::Match($m.Groups[6].Value, 'own (\d+) neighbours (\d+) none (\d+)')
        $list += [pscustomobject]@{ View = $m.Groups[3].Value; Variant = $m.Groups[4].Value; File = (Split-Path $m.Groups[5].Value -Leaf)
            Own = $(if ($i.Success) { [int]$i.Groups[1].Value } else { -1 }); Nb = $(if ($i.Success) { [int]$i.Groups[2].Value } else { -1 })
            None = $(if ($i.Success) { [int]$i.Groups[3].Value } else { -1 }) }
    }
    return $list
}

"version`tfactor`tview`tvariant`town`tneighbours`tnone`tdims"
foreach ($v in @('v200', 'v201', 'head')) {
    foreach ($txt in (Get-ChildItem $Out -Filter "glass-$v-x*.txt" | Where-Object { $_.Name -notmatch '-ref|allsettings' } |
                      Sort-Object { [double]($_.Name -replace "glass-$v-x", '' -replace '\.txt', '') })) {
        $f = $txt.Name -replace "glass-$v-x", '' -replace '\.txt', ''
        $dims = (Select-String -LiteralPath $txt.FullName -Pattern '^dims=(\d+)').Matches[0].Groups[1].Value
        foreach ($fr in (Frames $txt.FullName | Where-Object { $_.Own -ge 0 })) {
            "$v`t$f`t$($fr.View)`t$($fr.Variant)`t$($fr.Own)`t$($fr.Nb)`t$($fr.None)`t$dims"
        }
    }
}

""
"the plain picture under Dynamic Resolution against its reference at x$ReferenceFactor, threshold 16/255 (band = within 2 px of a glass edge)"
foreach ($v in @('v200', 'v201', 'head')) {
    $dr = Join-Path $Out "glass-$v-x$ReferenceFactor.txt"; $ref = Join-Path $Out "glass-$v-x$ReferenceFactor-ref.txt"
    if (-not ((Test-Path $dr) -and (Test-Path $ref))) { continue }
    $a = Frames $ref; $b = Frames $dr
    foreach ($view in ($a | Select-Object -ExpandProperty View -Unique)) {
        $band = ($a | Where-Object { $_.View -eq $view -and $_.Variant -eq 'glass:Debug=15' } | Select-Object -First 1).File
        foreach ($variant in @('glass', 'glass:Trace=0')) {
            $fa = ($a | Where-Object { $_.View -eq $view -and $_.Variant -eq $variant } | Select-Object -First 1).File
            $fb = ($b | Where-Object { $_.View -eq $view -and $_.Variant -eq $variant } | Select-Object -First 1).File
            if (-not $fa -or -not $fb) { continue }
            $r = & powershell -NoProfile -ExecutionPolicy Bypass -File $imgdiff -A (Join-Path $Out "glass-$v-x$ReferenceFactor-ref-frames\$fa") `
                     -B (Join-Path $Out "glass-$v-x$ReferenceFactor-frames\$fb") -Threshold 16 -BandFrom (Join-Path $Out "glass-$v-x$ReferenceFactor-ref-frames\$band")
            "$v`t$view`t$variant`t$r"
        }
    }
}
