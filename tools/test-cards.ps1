# The facade cards' bake schedule (RealGlass\Glass\CardSchedule.cs), offline: nearest first, the
# per-frame budget, least-recently-seen reuse that never evicts an asset standing in the current map,
# a failed asset not tried again.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-cards.ps1
#
# Also: the schedule with more assets than slots stops at the first asset that finds no slot, and keeps
# Waiting counted. Both are checked against a FROZEN copy of the schedule as it was before (below,
# CardScheduleBefore: the 2026-11-13 Next / Take / Waiting, word for word), job by job, and timed.
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "..\RealGlass\Glass\CardSchedule.cs"
# The schedule as built, plus a timer that runs a frame's calls (Waiting, then Next(1), as
# FacadeCardsGame.Bake does once everything is baked) in C#, so PowerShell's own cost is not counted.
$timing = @'

namespace RealGlass.Glass.TestTiming
{
    public static class Timing
    {
        public static double Frames(RealGlass.Glass.CardSchedule s, int frames)
        {
            int sink = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                sink += s.Waiting;
                sink += s.Next(1).Count;
            }
            sw.Stop();
            return sink < 0 ? -1.0 : sw.Elapsed.TotalMilliseconds / frames;
        }
    }
}
'@
Add-Type -TypeDefinition ([IO.File]::ReadAllText($src) + $timing) -ReferencedAssemblies System.Core

# The schedule before the early stop and the counted Waiting, frozen here (not read from git: once
# the fix is committed, HEAD would be the new code). Only the class's name and namespace differ.
Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System.Collections.Generic;

namespace RealGlass.Glass.Before
{
    public sealed class CardScheduleBefore
    {
        public struct Job
        {
            public string Key;
            public int Slot;
        }

        private readonly int _full, _capacity;
        private readonly Dictionary<string, int> _slotOf = new Dictionary<string, int>();
        private readonly string[] _keyIn;
        private readonly int[] _seen;
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly List<string> _standing = new List<string>();
        private readonly Dictionary<string, int> _rank = new Dictionary<string, int>();
        private readonly List<float> _distance = new List<float>();
        private readonly List<int> _order = new List<int>();
        private readonly List<Job> _jobs = new List<Job>();
        private readonly System.Comparison<int> _byDistance;
        private int _map;

        public CardScheduleBefore(int capacity) : this(capacity, 0)
        {
        }

        public CardScheduleBefore(int full, int small)
        {
            _full = full < 0 ? 0 : full;
            _capacity = _full + (small < 0 ? 0 : small);
            _keyIn = new string[_capacity + 1];
            _seen = new int[_capacity + 1];
            _byDistance = (a, b) => _distance[a].CompareTo(_distance[b]);
        }

        public int Capacity { get { return _capacity; } }
        public int FullCount { get { return _full; } }
        public int Used { get { return _slotOf.Count; } }

        public int Waiting
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _standing.Count; i++)
                {
                    if (!_slotOf.ContainsKey(_standing[i]) && !_failed.Contains(_standing[i]))
                        n++;
                }
                return n;
            }
        }

        public int Failed { get { return _failed.Count; } }

        public int SlotOf(string key)
        {
            int slot;
            return key != null && _slotOf.TryGetValue(key, out slot) ? slot : 0;
        }

        public bool IsFull(int slot)
        {
            return slot >= 1 && slot <= _full;
        }

        public void SetStanding(IList<string> keys, IList<float> distances)
        {
            _map++;
            _standing.Clear();
            _distance.Clear();
            _rank.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (key == null || _rank.ContainsKey(key))
                    continue;
                _rank[key] = -1;
                _standing.Add(key);
                _distance.Add(i < distances.Count ? distances[i] : float.MaxValue);
            }
            _order.Clear();
            for (int i = 0; i < _standing.Count; i++)
                _order.Add(i);
            _order.Sort(_byDistance);
            for (int r = 0; r < _order.Count; r++)
            {
                string key = _standing[_order[r]];
                _rank[key] = r;
                int slot;
                if (_slotOf.TryGetValue(key, out slot))
                    _seen[slot] = _map;
            }
        }

        public List<Job> Next(int maxJobs)
        {
            _jobs.Clear();
            if (maxJobs <= 0 || _capacity == 0)
                return _jobs;

            for (int r = 0; r < _order.Count && _jobs.Count < maxJobs; r++)
            {
                string key = _standing[_order[r]];
                if (_failed.Contains(key))
                    continue;
                int has = SlotOf(key);
                bool wantsFull = _full > 0 && r < _full;
                if (has != 0 && (IsFull(has) || !wantsFull))
                    continue;

                int slot = wantsFull ? Take(1, _full, r) : 0;
                if (slot == 0 && has == 0)
                    slot = Take(_full + 1, _capacity, r);
                if (slot == 0 && has == 0 && _full > 0 && !wantsFull)
                    slot = Take(1, _full, r);
                if (slot == 0)
                    continue;
                if (has != 0)
                    Release(has);
                _keyIn[slot] = key;
                _slotOf[key] = slot;
                _seen[slot] = _map;
                _jobs.Add(new Job { Key = key, Slot = slot });
            }
            return _jobs;
        }

        public void Done(Job job, bool ok)
        {
            if (ok)
                return;
            _failed.Add(job.Key);
            if (job.Slot > 0 && job.Slot <= _capacity && _keyIn[job.Slot] == job.Key)
                Release(job.Slot);
        }

        private void Release(int slot)
        {
            string key = _keyIn[slot];
            if (key != null)
                _slotOf.Remove(key);
            _keyIn[slot] = null;
        }

        private int Take(int lo, int hi, int r)
        {
            int idle = 0, idleSeen = int.MaxValue, far = 0, farRank = r;
            for (int s = lo; s <= hi; s++)
            {
                string key = _keyIn[s];
                if (key == null)
                    return s;
                int rank;
                if (!_rank.TryGetValue(key, out rank))
                {
                    if (_seen[s] < idleSeen)
                    {
                        idleSeen = _seen[s];
                        idle = s;
                    }
                }
                else if (rank > farRank)
                {
                    farRank = rank;
                    far = s;
                }
            }
            int take = idle != 0 ? idle : far;
            if (take != 0)
                Release(take);
            return take;
        }

        public void Clear()
        {
            _slotOf.Clear();
            for (int s = 0; s <= _capacity; s++)
            {
                _keyIn[s] = null;
                _seen[s] = 0;
            }
        }
    }

    public static class BeforeTiming
    {
        public static double Frames(CardScheduleBefore s, int frames)
        {
            int sink = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                sink += s.Waiting;
                sink += s.Next(1).Count;
            }
            sw.Stop();
            return sink < 0 ? -1.0 : sw.Elapsed.TotalMilliseconds / frames;
        }
    }
}
'@
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

# ---- more assets than slots: the early stop and the counted Waiting, against the frozen schedule -----

# Three maps of n assets with distinct distances (i x a prime, mod the prime 10007: no two alike for
# n < 10007): map 1; map 2 with every tenth asset gone, the rest moved and a tenth as many new ones;
# map 3 with only the first 60% of map 1, back at map 1's distances (assets leave, others come back).
function Maps([int]$n) {
    $k1 = New-Object 'System.Collections.Generic.List[string]'; $d1 = New-Object 'System.Collections.Generic.List[float]'
    $k2 = New-Object 'System.Collections.Generic.List[string]'; $d2 = New-Object 'System.Collections.Generic.List[float]'
    $k3 = New-Object 'System.Collections.Generic.List[string]'; $d3 = New-Object 'System.Collections.Generic.List[float]'
    for ($i = 0; $i -lt $n; $i++) {
        $k1.Add("asset" + $i); $d1.Add([float](($i * 7919) % 10007))
        if ($i % 10 -ne 3) { $k2.Add("asset" + $i); $d2.Add([float](2 * (($i * 104729) % 10007))) }
    }
    for ($j = 0; $j -lt [Math]::Max(1, [int][Math]::Floor($n / 10)); $j++) { $k2.Add("new" + $j); $d2.Add([float](2 * (($j * 7919) % 10007) + 1)) }
    for ($i = 0; $i -lt [Math]::Ceiling($n * 0.6); $i++) { $k3.Add("asset" + $i); $d3.Add([float](($i * 7919) % 10007)) }
    return @([pscustomobject]@{ K = $k1; D = $d1 }, [pscustomobject]@{ K = $k2; D = $d2 }, [pscustomobject]@{ K = $k3; D = $d3 })
}

# Next / Done until Next hands out nothing; every job (key:slot) and Waiting after each call go into
# the trace, then every key's slot. Works on either schedule (the same calls by name).
function Drive($s, [int]$budget, $fails, $map, [System.Text.StringBuilder]$trace) {
    $calls = 0
    while ($true) {
        $jobs = $s.Next($budget)
        $n = $jobs.Count
        $taken = New-Object System.Collections.ArrayList
        for ($i = 0; $i -lt $n; $i++) { [void]$taken.Add($jobs[$i]) }
        foreach ($j in $taken) {
            [void]$trace.Append($j.Key).Append(':').Append($j.Slot).Append(' ')
            $s.Done($j, -not $fails.Contains($j.Key))
        }
        [void]$trace.Append('w').Append($s.Waiting).Append("`n")
        $calls++
        if ($n -eq 0) { break }
        if ($calls -gt 200000) { throw "the schedule never ran out of jobs" }
    }
    foreach ($key in $map.K) { [void]$trace.Append($key).Append('=').Append($s.SlotOf($key)).Append(' ') }
    [void]$trace.Append("`n")
}

# The properties a finished map must have: the nearest min(capacity, standing) assets carded, no
# farther one; the nearest FullCount in full slots.
function Properties($s, $map) {
    $idx = 0..($map.K.Count - 1) | Sort-Object { $map.D[$_] }
    $cap = [Math]::Min($s.Capacity, $map.K.Count)
    $bad = 0
    for ($r = 0; $r -lt $idx.Count; $r++) {
        $slot = $s.SlotOf($map.K[$idx[$r]])
        if (($r -lt $cap) -ne ($slot -gt 0)) { $bad++ }
        if ($r -lt $s.FullCount -and -not $s.IsFull($slot)) { $bad++ }
    }
    return $bad
}

$cases = @(
    @{ Full = 2; Small = 3; N = 9; Budgets = @(1, 2, 3, 100) },
    @{ Full = 4; Small = 0; N = 9; Budgets = @(1, 2) },
    @{ Full = 0; Small = 5; N = 9; Budgets = @(1, 3) },
    @{ Full = 204; Small = 819; N = 1100; Budgets = @(1, 4) },
    @{ Full = 204; Small = 819; N = 1500; Budgets = @(1, 4) },
    @{ Full = 204; Small = 819; N = 4000; Budgets = @(1, 4) }
)
foreach ($c in $cases) {
    $maps = Maps $c.N
    $fails = New-Object 'System.Collections.Generic.HashSet[string]'
    for ($i = 5; $i -lt $c.N; $i += 13) { [void]$fails.Add("asset" + $i) }
    for ($j = 2; $j -lt $c.N; $j += 7) { [void]$fails.Add("new" + $j) }
    $none = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($b in $c.Budgets) {
        foreach ($f in @($none, $fails)) {
            $new = New-Object RealGlass.Glass.CardSchedule $c.Full, $c.Small
            $old = New-Object RealGlass.Glass.Before.CardScheduleBefore $c.Full, $c.Small
            $tn = New-Object System.Text.StringBuilder; $to = New-Object System.Text.StringBuilder
            $bad = 0
            foreach ($m in $maps) {
                $new.SetStanding($m.K, $m.D); $old.SetStanding($m.K, $m.D)
                Drive $new $b $f $m $tn
                Drive $old $b $f $m $to
                if ($f.Count -eq 0) { $bad += Properties $new $m }
            }
            $what = "" + $c.N + " assets, " + $c.Full + " + " + $c.Small + " slots, " + $b + " a call, " + $(if ($f.Count -eq 0) { "no failures" } else { "every 13th asset and 7th new one failing" })
            $same = $tn.ToString() -ceq $to.ToString()
            Check ("same jobs, slots and Waiting as before the early stop: " + $what) $same ("trace " + $tn.Length + " chars" + $(if ($same) { "" } else { " vs " + $to.Length }))
            if ($f.Count -eq 0) { Check ("nearest carded, nearest full in full slots, over three maps: " + $what) ($bad -eq 0) ("" + $bad + " assets out of place") }
        }
    }
}

# Waiting is counted again after a Clear (every card forgotten).
$cl = New-Object RealGlass.Glass.CardSchedule 2, 1
$cl.SetStanding([string[]]@("a", "b", "c", "d"), [float[]]@(1, 2, 3, 4))
foreach ($j in @($cl.Next(10))) { $cl.Done($j, $true) }
$w1 = $cl.Waiting
$cl.Clear()
$w2 = $cl.Waiting
$jc = @($cl.Next(10))
Check "after a Clear every standing asset waits again, and is baked again" ($w1 -eq 1 -and $w2 -eq 4 -and $cl.Used -eq 3 -and $jc.Count -eq 3) ("waiting " + $w1 + " then " + $w2 + ", jobs " + $jc.Count + ", used " + $cl.Used)

# Cost once everything is baked: a frame's Waiting + Next(1), in C#, before (frozen) and now.
foreach ($n in @(1500, 4000)) {
    $m = (Maps $n)[0]
    $none = New-Object 'System.Collections.Generic.HashSet[string]'
    $new = New-Object RealGlass.Glass.CardSchedule 204, 819
    $old = New-Object RealGlass.Glass.Before.CardScheduleBefore 204, 819
    $new.SetStanding($m.K, $m.D); $old.SetStanding($m.K, $m.D)
    $sink = New-Object System.Text.StringBuilder
    Drive $new 64 $none $m $sink
    Drive $old 64 $none $m $sink
    [void][RealGlass.Glass.TestTiming.Timing]::Frames($new, 5)
    [void][RealGlass.Glass.Before.BeforeTiming]::Frames($old, 2)
    $now = [RealGlass.Glass.TestTiming.Timing]::Frames($new, 200)
    $before = [RealGlass.Glass.Before.BeforeTiming]::Frames($old, 10)
    Check ("" + $n + " assets, all baked: a frame's Waiting + Next(1) well under a millisecond") ($now -lt 2.0 -and $new.Used -eq 1023) (("{0:F4} ms a frame now (200 frames), {1:F2} ms before (10 frames), " -f $now, $before) + $new.Used + " carded of 1023")
}

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
