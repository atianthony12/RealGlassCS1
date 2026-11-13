# The facade cards' bake schedule (RealGlass\Glass\CardSchedule.cs), offline: nearest first, the
# per-frame budget, least-recently-seen reuse that never evicts an asset standing in the current map,
# a failed asset not tried again.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-cards.ps1
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "..\RealGlass\Glass\CardSchedule.cs"
Add-Type -TypeDefinition ([IO.File]::ReadAllText($src)) -ReferencedAssemblies System.Core
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("ok      " + $name + "  ->  " + $detail) }
    else { Write-Host ("FAILED  " + $name + "  ->  " + $detail); $script:failed++ }
}
function Keys($jobs) { ($jobs | ForEach-Object { $_.Key }) -join "," }

# Five assets, three slots, two bakes a frame.
$s = New-Object RealGlass.Glass.CardSchedule 3
$s.SetStanding([string[]]@("far", "near", "mid", "nearer", "farther"), [float[]]@(500, 20, 100, 10, 900))
$j1 = $s.Next(2)
Check "nearest first" ((Keys $j1) -eq "nearer,near") (Keys $j1)
Check "the budget" ($j1.Count -eq 2) ("" + $j1.Count + " jobs")
foreach ($j in $j1) { $s.Done($j, $true) }
$j2 = $s.Next(2)
Check "then the next nearest, as far as the slots go" ((Keys $j2) -eq "mid") (Keys $j2)
foreach ($j in $j2) { $s.Done($j, $true) }
$j3 = $s.Next(2)
Check "every slot standing: the rest wait, nothing evicted" ($j3.Count -eq 0 -and $s.Waiting -eq 2 -and $s.Used -eq 3) ("jobs " + $j3.Count + ", waiting " + $s.Waiting + ", used " + $s.Used)
Check "slots kept" ($s.SlotOf("nearer") -gt 0 -and $s.SlotOf("near") -gt 0 -and $s.SlotOf("mid") -gt 0 -and $s.SlotOf("far") -eq 0) ("nearer " + $s.SlotOf("nearer") + ", near " + $s.SlotOf("near") + ", mid " + $s.SlotOf("mid"))

# A new map: "near" and "far" stand, "nearer" and "mid" do not. "mid" was seen in map 1 like "nearer";
# both are free to go, "near" is not.
$s.SetStanding([string[]]@("near", "far", "farther"), [float[]]@(30, 40, 50))
$j4 = $s.Next(5)
Check "reuse: the two not standing are evicted for the two waiting" ((Keys $j4) -eq "far,farther") (Keys $j4)
Check "a standing asset keeps its card" ($s.SlotOf("near") -gt 0) ("near " + $s.SlotOf("near"))
Check "the evicted lose theirs" ($s.SlotOf("nearer") -eq 0 -and $s.SlotOf("mid") -eq 0) ("nearer " + $s.SlotOf("nearer") + ", mid " + $s.SlotOf("mid"))
foreach ($j in $j4) { $s.Done($j, $true) }

# Least recently seen first: map 3 has "far" only; map 4 then needs one slot: "near" (seen in map 2)
# and "farther" (map 2) are older than "far" (map 3) -- and "far" stands anyway.
$s.SetStanding([string[]]@("far"), [float[]]@(5))
$s.SetStanding([string[]]@("far", "new"), [float[]]@(5, 6))
$j5 = $s.Next(1)
$evicted = if ($s.SlotOf("near") -eq 0) { "near" } elseif ($s.SlotOf("farther") -eq 0) { "farther" } else { "none" }
Check "least recently seen goes first, never a standing one" ($j5.Count -eq 1 -and $j5[0].Key -eq "new" -and $evicted -ne "none" -and $s.SlotOf("far") -gt 0) ("new got " + $j5[0].Slot + ", evicted " + $evicted)
foreach ($j in $j5) { $s.Done($j, $true) }

# A failed bake frees its slot and is not tried again.
$t = New-Object RealGlass.Glass.CardSchedule 2
$t.SetStanding([string[]]@("bad", "good"), [float[]]@(1, 2))
$j6 = $t.Next(2)
$t.Done($j6[0], $false); $t.Done($j6[1], $true)
$j7 = $t.Next(2)
Check "a failed asset: slot freed, not retried" ($t.SlotOf("bad") -eq 0 -and $j7.Count -eq 0 -and $t.Failed -eq 1 -and $t.Waiting -eq 0) ("bad " + $t.SlotOf("bad") + ", jobs " + $j7.Count + ", failed " + $t.Failed)

# Duplicate keys in a map count once; no capacity, no jobs.
$u = New-Object RealGlass.Glass.CardSchedule 4
$u.SetStanding([string[]]@("a", "a", "b"), [float[]]@(3, 1, 2))
Check "duplicates once" ($u.Waiting -eq 2) ("waiting " + $u.Waiting)
$z = New-Object RealGlass.Glass.CardSchedule 0
$z.SetStanding([string[]]@("a"), [float[]]@(1))
Check "no slots, no jobs" ($z.Next(3).Count -eq 0) "0"

# Two kinds of slot: the nearest get the full ones, the rest small ones; a near asset takes a full slot
# from the farthest that holds one, its small one freed.
$p = New-Object RealGlass.Glass.CardSchedule 2, 3
$p.SetStanding([string[]]@("a", "b", "c", "d", "e"), [float[]]@(10, 20, 30, 40, 50))
$jp = $p.Next(10)
$full = ($jp | Where-Object { $p.IsFull($_.Slot) } | ForEach-Object { $_.Key }) -join ","
Check "the nearest get the full slots" ($full -eq "a,b" -and $jp.Count -eq 5) ("full: " + $full + ", jobs " + $jp.Count)
foreach ($j in @($jp)) { $p.Done($j, $true) }
$p.SetStanding([string[]]@("a", "b", "c", "d", "e"), [float[]]@(10, 60, 30, 40, 5))
$jq = @($p.Next(10))
Check "a near asset moves to a full slot, taken from the farthest holder, who takes the small one it left" ($jq.Count -eq 2 -and $jq[0].Key -eq "e" -and $p.IsFull($p.SlotOf("e")) -and $jq[1].Key -eq "b" -and -not $p.IsFull($p.SlotOf("b")) -and $p.SlotOf("b") -gt 0) ("jobs " + (Keys $jq) + ", e " + $p.SlotOf("e") + ", b " + $p.SlotOf("b"))
foreach ($j in $jq) { $p.Done($j, $true) }
Check "then nothing to do" (@($p.Next(10)).Count -eq 0) "0 jobs"

# Full: near buildings first -- a nearer asset takes the farthest standing one's slot.
$q = New-Object RealGlass.Glass.CardSchedule 1, 1
$q.SetStanding([string[]]@("x", "y"), [float[]]@(10, 20))
foreach ($j in @($q.Next(5))) { $q.Done($j, $true) }
$q.SetStanding([string[]]@("x", "y", "z"), [float[]]@(10, 20, 15))
$jz = @($q.Next(5))
Check "full: a nearer one evicts the farthest standing" ($jz.Count -eq 1 -and $jz[0].Key -eq "z" -and $q.SlotOf("y") -eq 0 -and $q.Waiting -eq 1) ("jobs " + (Keys $jz) + ", y " + $q.SlotOf("y") + ", waiting " + $q.Waiting)
foreach ($j in $jz) { $q.Done($j, $true) }
Check "and the farthest then waits (no churn)" (@($q.Next(5)).Count -eq 0) "0 jobs"

# Cost: 988 assets, 1,023 slots -- a map and a frame's schedule in microseconds, so no stall of 0.5 s
# can come from it (the 2026-11-13 stalls: garbage collections; FacadeCardsGame now logs them apart).
$big = New-Object RealGlass.Glass.CardSchedule 204, 819
$keys = New-Object 'System.Collections.Generic.List[string]'; $dist = New-Object 'System.Collections.Generic.List[float]'
for ($i = 0; $i -lt 988; $i++) { $keys.Add("asset" + $i); $dist.Add([float](($i * 7919) % 3000)) }
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($m = 0; $m -lt 20; $m++) {
    $big.SetStanding($keys, $dist)
    for ($f = 0; $f -lt 50; $f++) { foreach ($j in @($big.Next(4))) { $big.Done($j, $true) } }
}
$sw.Stop()
$perFrame = $sw.Elapsed.TotalMilliseconds / 1000.0
Check "988 assets: a frame's schedule costs well under a millisecond" ($perFrame -lt 1.0 -and $big.Used -eq 988) (("{0:F3} ms a frame (with PowerShell's own overhead), " -f $perFrame) + $big.Used + " carded of 1023")

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
