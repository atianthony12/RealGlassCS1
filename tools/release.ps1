# Checks and stages a Workshop release, so that the only thing left to do by hand is the click in
# the game's Content Manager (Volumetric Weather's script, 2026-10-20, for this mod). Every step
# checks itself and the script STOPS at the first thing that is not right; nothing it does is hard
# to undo (the one folder it takes away is moved, not deleted).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1 -DryRun   # every check, no copy, no move
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1           # the real thing
#
# What it does, in order:
#   1. Checks the release is ready: the game is closed, the git tree is committed, the ModsLibCS1
#      submodule is pinned and pushed, Mod.Version is newer than the last v* tag, CHANGELOG.md has
#      that version dated (not "unreleased"), no personal name in the repo's files, and the shader
#      bundles were built after the last shader edit.
#   2. Builds (which deploys to Addons\Mods) and checks the deployed DLL: it carries Mod.Version and
#      embeds the three shader bundles at their sizes on disk, both room atlases, the icon and
#      every Localization\*.xml; each bundle holds every shader with real code for every API of
#      its platform; the folder holds exactly the four shipped files, the libraries and notices as
#      in the repo.
#   3. Runs every offline test, the Metal (Mac) and AMD compile checks of the shipped shaders
#      included; one failure stops the release.
#   4. THE FIRST UPLOAD ($workshopId empty): stops there -- the game's Share button uploads
#      Addons\Mods\RealGlass as it stands, and prints the steps by hand.
#      EVERY LATER ONE: copies exactly the four shipped files into the SUBSCRIBED Workshop folder,
#      reads the version and the hashes back out of it, and moves the local Addons\Mods copy out of
#      the way, so the game loads ONE copy of the mod -- the subscribed one, whose Content Manager
#      button reads "Update". A local copy beside it is how a "Share" button (= a brand-new
#      Workshop item) appears; with the folder gone that click cannot be made.
#   5. Prints what is left to do by hand, the GitHub release included.
#
# Why the subscribed folder: a mod's published id comes from its folder name (PluginInfo), so only
# the copy in steamapps\workshop\content\255710\<id> knows it is that item.

param(
    [switch]$DryRun
)

# Not "Stop": under it, PowerShell 5.1 turns anything git or dotnet write to stderr (git's
# line-ending warnings, for one) into an exception. Every step below checks its own result.
$ErrorActionPreference = "Continue"

$root = Split-Path -Parent $PSScriptRoot
# The published item's id (the first upload, 1.0.0, 2026-10-20).
$workshopId = "3823419476"
$workshop = "C:\Program Files (x86)\Steam\steamapps\workshop\content\255710\$workshopId"
$deployed = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\RealGlass"
$parked = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\RealGlass-dev-parked"
$shipped = @("RealGlass.dll", "UnifiedUILib.dll", "CitiesHarmony.API.dll", "THIRD-PARTY-NOTICES.md")
# Where each shipped file other than the DLL comes from: the deployed copy must be this one.
$sources = @{
    "UnifiedUILib.dll"       = Join-Path $root "libs\UnifiedUILib.dll"
    "CitiesHarmony.API.dll"  = Join-Path $root "libs\CitiesHarmony.API.dll"
    "THIRD-PARTY-NOTICES.md" = Join-Path $root "THIRD-PARTY-NOTICES.md"
}
# "metal" compiles every shipped Metal program with Apple's compiler (needs the Metal Developer Tools
# for Windows: see test-metal.ps1); "amd" every shipped shader with AMD's (Radeon GPU Analyzer: see
# test-amd.ps1). reflection-shader and water-surface draw in the Unity 5.6 editor (closed, or the
# project is locked).
$tests = @("settings", "localization", "profiles", "reflection-shader", "water-surface", "metal", "amd")
# The bundles and the platform ids every shader in each must carry (build-bundle.ps1's table).
$bundles = @(
    @{ Name = "win";   Platforms = "4,15" },
    @{ Name = "linux"; Platforms = "15" },
    @{ Name = "mac";   Platforms = "14,15" }
)

$problems = 0

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "   ok    $text" }
function Stop-Release([string]$text) {
    Write-Host "   STOP  $text" -ForegroundColor Red
    if ($DryRun) { $script:problems++; return }
    Write-Host ""
    Write-Host "Nothing was changed after this point. Fix it and run again." -ForegroundColor Red
    exit 1
}

function DllVersion([string]$path) {
    # Reads the metadata only: nothing of the DLL is loaded or run.
    return [System.Reflection.AssemblyName]::GetAssemblyName($path).Version
}

function ReleaseOf([System.Version]$v) { return "{0}.{1}.{2}" -f $v.Major, $v.Minor, $v.Build }

function SameFile([string]$a, [string]$b) {
    return (Get-FileHash $a -Algorithm SHA256).Hash -eq (Get-FileHash $b -Algorithm SHA256).Hash
}

if ($DryRun) { Write-Host "DRY RUN: every check runs, nothing is copied or moved." -ForegroundColor Yellow }

# ---- 1. Is the release ready? ---------------------------------------------------------------

Step "The game is closed"
if (Get-Process -Name "Cities" -ErrorAction SilentlyContinue) {
    Stop-Release "Cities: Skylines is running. Close it: a running game holds the DLLs and would load the old copy."
} else { Ok "not running" }

Step "The git tree is committed"
$dirty = @(git -C $root status --porcelain)
if ($dirty.Count -gt 0) {
    Stop-Release "$($dirty.Count) uncommitted change(s). Commit first, so the tag can point at what was uploaded."
} else { Ok ("clean, at " + (git -C $root rev-parse --short HEAD)) }

# The panel's controls come from the ModsLibCS1 submodule, compiled into the DLL. The tag must point
# at a tree anyone can build: the submodule committed, the commit this repo pins pushed to its remote.
Step "The ModsLibCS1 submodule is committed, pinned and pushed"
$sub = Join-Path $root "ModsLibCS1"
if (-not (Test-Path (Join-Path $sub "ModsLibCS1.props"))) {
    Stop-Release "ModsLibCS1 is missing: git submodule update --init"
} else {
    $subDirty = @(git -C $sub status --porcelain)
    $pinned = ((git -C $root ls-tree HEAD ModsLibCS1) -split "\s+")[2]
    $subHead = (git -C $sub rev-parse HEAD).Trim()
    if ($subDirty.Count -gt 0) {
        Stop-Release "ModsLibCS1 has $($subDirty.Count) uncommitted change(s)"
    } elseif ($subHead -ne $pinned) {
        Stop-Release "ModsLibCS1 is at $subHead but this repo pins ${pinned}: commit the submodule pointer"
    } else {
        git -C $sub fetch -q origin
        $onRemote = @(git -C $sub branch -r --contains $pinned)
        if ($onRemote.Count -eq 0) {
            Stop-Release "ModsLibCS1 commit $pinned is not on its remote: push it, or a clone of this repo cannot build"
        } else { Ok ("pinned " + $pinned.Substring(0, 7) + ", clean, on " + $onRemote[0].Trim()) }
    }
}

Step "The version is new"
$modCs = Get-Content (Join-Path $root "RealGlass\Mod.cs") -Raw
if ($modCs -notmatch 'public const string Version = "(\d+\.\d+\.\d+)"') { Stop-Release "Mod.Version not found in Mod.cs" }
$version = $Matches[1]
$tags = @(git -C $root tag --list "v*" --sort=-v:refname)
$lastTag = if ($tags.Count -gt 0) { $tags[0] } else { "(no tag yet)" }
if ($tags -contains "v$version") {
    Stop-Release "Mod.Version is $version and v$version is already a tag: raise the version first (Mod.cs). A number is never reused."
} else { Ok "Mod.Version $version, last tag $lastTag" }

Step "The changelog has this version dated"
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding UTF8
# The heading is "## 1.0.0 <em dash> 2026-10-20". The dash is built from its code point so this
# file stays pure ASCII: PowerShell 5.1 reads a script with no byte-order mark as ANSI.
$dash = [string][char]0x2014
$heading = "(?m)^## " + [regex]::Escape($version) + " " + $dash + " (.+)$"
if ($changelog -notmatch $heading) {
    Stop-Release "CHANGELOG.md has no '## $version -- ...' section"
} elseif ($Matches[1] -match "unreleased") {
    Stop-Release "CHANGELOG.md still says '## $version -- unreleased': put the date in"
} else { Ok "## $version, dated $($Matches[1].Trim())" }

# No personal name in the repo's files but the copyright lines (README.md, LICENSE). The name is
# the commit author's, word by word, so this script never spells it; whole words only, so the
# GitHub account in a link (the Workshop description's, .gitmodules') is not one.
Step "No personal name in the repo's files"
$words = @(((git -C $root config user.name) -split "\s+") | Where-Object { $_.Length -ge 3 } | ForEach-Object { [regex]::Escape($_) })
if ($words.Count -eq 0) {
    Write-Host "   note  git has no user.name here: not checked"
} else {
    $hits = @(git -C $root grep -w -i -l -E ($words -join "|") -- . ":(exclude)README.md" ":(exclude)LICENSE")
    if ($hits.Count -gt 0) { Stop-Release "the author's name is in: $($hits -join ', ')" } else { Ok "only in README.md and LICENSE" }
}

# The DLL embeds the bundles, and build-bundle.ps1 is the only thing that makes them: a shader
# edited after the last bundle build ships as it was before the edit, with every test green.
Step "The shader bundles were built after the last shader edit"
$shaderSources = @(Get-ChildItem (Join-Path $root "UnityProject\Assets\RealGlass") -File | Where-Object { $_.Extension -eq ".shader" -or $_.Extension -eq ".cginc" })
$newest = $shaderSources | Sort-Object LastWriteTime -Descending | Select-Object -First 1
foreach ($b in $bundles) {
    $file = Get-Item (Join-Path $root "RealGlass\Resources\realglass-$($b.Name).bundle") -ErrorAction SilentlyContinue
    if (-not $file) { Stop-Release "RealGlass\Resources\realglass-$($b.Name).bundle is missing (build-bundle.ps1)"; continue }
    if ($file.LastWriteTime -lt $newest.LastWriteTime) {
        Stop-Release "realglass-$($b.Name).bundle ($($file.LastWriteTime)) is older than $($newest.Name) ($($newest.LastWriteTime)): run build-bundle.ps1"
    } else { Ok "realglass-$($b.Name).bundle $($file.LastWriteTime), after $($newest.Name) $($newest.LastWriteTime)" }
}

# ---- 2. Build ---------------------------------------------------------------------------------

Step "Build (deploys to Addons\Mods)"
$build = @(& dotnet build (Join-Path $root "RealGlass\RealGlass.csproj"))
if ($LASTEXITCODE -ne 0 -or ($build -match "error CS")) {
    $build | Where-Object { $_ -match "error" } | ForEach-Object { Write-Host "   $_" }
    Stop-Release "the build failed"
} else {
    $warnings = @($build | Where-Object { $_ -match "warning CS" })
    Ok "built, $($warnings.Count) compiler warning(s)"
}

Step "The deployed DLL carries Mod.Version"
$deployedDll = Join-Path $deployed "RealGlass.dll"
if (-not (Test-Path $deployedDll)) { Stop-Release "$deployedDll is not there" }
$deployedVersion = DllVersion $deployedDll
if ((ReleaseOf $deployedVersion) -ne $version) {
    Stop-Release ("the deployed DLL is $deployedVersion, not " + $version + ".*")
} else { Ok "$deployedVersion" }

# The csproj embeds a resource only if its file exists, so a missing or renamed file gives a clean
# build, 0 warnings, every test green -- and a stand-down (no bundle) or no rooms on that machine.
# Read the DLL. From its bytes: Windows' application control can block a fresh DLL loaded by path.
Step "The deployed DLL embeds the bundles, the room atlases, the icon and every language file"
$asm = [System.Reflection.Assembly]::ReflectionOnlyLoad([System.IO.File]::ReadAllBytes($deployedDll))
$resourceNames = @($asm.GetManifestResourceNames())
$embedded = @()
foreach ($b in $bundles) { $embedded += ,@("RealGlass.Resources.realglass-$($b.Name).bundle", "RealGlass\Resources\realglass-$($b.Name).bundle") }
$embedded += ,@("RealGlass.Resources.rooms-night.bytes", "RealGlass\Resources\rooms-night.bytes")
$embedded += ,@("RealGlass.Resources.rooms-day.bytes", "RealGlass\Resources\rooms-day.bytes")
$embedded += ,@("RealGlass.Resources.dirt-grime.bytes", "RealGlass\Resources\dirt-grime.bytes")
$embedded += ,@("RealGlass.Resources.dirt-runs.bytes", "RealGlass\Resources\dirt-runs.bytes")
$embedded += ,@("RealGlass.Resources.GlassIcon.png", "RealGlass\Resources\GlassIcon.png")
foreach ($f in Get-ChildItem (Join-Path $root "RealGlass\Localization") -Filter *.xml) {
    $embedded += ,@("Localization.$($f.Name)", "RealGlass\Localization\$($f.Name)")
}
foreach ($e in $embedded) {
    $res = $e[0]
    if ($resourceNames -notcontains $res) { Stop-Release "$res is not embedded"; continue }
    $stream = $asm.GetManifestResourceStream($res)
    $length = $stream.Length
    $stream.Close()
    $onDisk = (Get-Item (Join-Path $root $e[1])).Length
    if ($length -ne $onDisk) { Stop-Release "$res is $length bytes in the DLL, $onDisk on disk ($($e[1])): rebuild" }
    else { Ok "$res ($length bytes, as on disk)" }
}

Step "Every bundle holds every shader, with real code for every API of its platform"
$builderCs = Get-Content (Join-Path $root "UnityProject\Assets\Editor\BundleBuilder.cs") -Raw
$shaderNames = @([regex]::Matches($builderCs, '"Assets/RealGlass/(\w+)\.shader"') | ForEach-Object { "RealGlass/" + $_.Groups[1].Value })
foreach ($b in $bundles) {
    $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\bundle-apis.ps1") `
        -Bundle (Join-Path $root "RealGlass\Resources\realglass-$($b.Name).bundle") -Expect $b.Platforms -Shaders ($shaderNames -join ","))
    if ($LASTEXITCODE -ne 0) {
        $out | Select-Object -Last 5 | ForEach-Object { Write-Host "   $_" }
        Stop-Release "realglass-$($b.Name).bundle failed bundle-apis.ps1"
    } else { Ok "realglass-$($b.Name).bundle: $($shaderNames.Count) shaders, platforms $($b.Platforms)" }
}

Step "The deployed folder holds exactly the four shipped files"
$present = @(Get-ChildItem $deployed -File | Select-Object -ExpandProperty Name)
$extra = @($present | Where-Object { $shipped -notcontains $_ })
$missing = @($shipped | Where-Object { $present -notcontains $_ })
if ($extra.Count -gt 0) { Stop-Release "extra file(s) in Addons\Mods\RealGlass, which would be uploaded: $($extra -join ', ')" }
elseif ($missing.Count -gt 0) { Stop-Release "missing from Addons\Mods\RealGlass: $($missing -join ', ')" }
else {
    $stale = @($sources.Keys | Where-Object { -not (SameFile (Join-Path $deployed $_) $sources[$_]) })
    if ($stale.Count -gt 0) { Stop-Release "not the repo's copy: $($stale -join ', ')" }
    else { Ok (($shipped -join ", ") + "; the libraries and notices identical to the repo's") }
}

# ---- 3. Tests ---------------------------------------------------------------------------------

Step "Every offline test passes"
foreach ($t in $tests) {
    $script = Join-Path $root "tools\test-$t.ps1"
    $takesDll = (Get-Content $script -Raw) -match '\$Dll'
    if ($takesDll) { $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Dll $deployedDll 2>&1) }
    else { $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $script 2>&1) }
    $last = ($out | Select-Object -Last 1) -join ""
    # "all passed" (settings, localization, reflection-shader), "WATER SURFACE TEST OK", "pass: ..." (metal, amd).
    $failed = ($LASTEXITCODE -ne 0) -or (@($out | Where-Object { "$_" -match "^FAIL" }).Count -gt 0) -or ($last -notmatch "pass|TEST OK")
    if ($failed) {
        $out | Where-Object { "$_" -match "^FAIL" } | ForEach-Object { Write-Host "   $_" }
        Stop-Release "test-$t failed ($last)"
    } else { Ok "test-$t : $last" }
}

# Never stops the release: a list to read.
Step "Other installed mods on the methods we patch (to read; it never stops the release)"
$scan = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\scan-mods.ps1") -Dll $deployedDll 2>&1)
$scan | ForEach-Object { Write-Host "   $_" }

# ---- 4. The first upload: Share from Addons\Mods. Every later one: into the subscribed folder. --

if (-not $workshopId) {
    Step "The first upload: Addons\Mods\RealGlass is what the Share button uploads"
    Ok "nothing to copy or move: no Workshop item yet (`$workshopId is empty)"
} else {
    Step "The subscribed Workshop folder (item $workshopId)"
    if (-not (Test-Path $workshop)) {
        Stop-Release "$workshop does not exist: subscribe to the item (and let Steam download it) first"
    } else {
        $liveDll = Join-Path $workshop "RealGlass.dll"
        if (Test-Path $liveDll) { Ok "holds $(DllVersion $liveDll) now" } else { Ok "exists, no DLL in it yet" }
        $others = @(Get-ChildItem $workshop -File | Where-Object { $shipped -notcontains $_.Name } | Select-Object -ExpandProperty Name)
        if ($others.Count -gt 0) { Write-Host "   note  other files there, left alone: $($others -join ', ')" }
    }

    Step "Copy the four files in and read them back"
    if ($DryRun) {
        Write-Host "   dry   would copy $($shipped -join ', ') -> $workshop"
    } else {
        foreach ($name in $shipped) {
            Copy-Item (Join-Path $deployed $name) (Join-Path $workshop $name) -Force -ErrorAction Stop
        }
        $bad = @($shipped | Where-Object { -not (SameFile (Join-Path $deployed $_) (Join-Path $workshop $_)) })
        if ($bad.Count -gt 0) { Stop-Release "$($bad -join ', ') differ(s) after the copy; is Steam re-verifying the folder? Try again." }
        $liveVersion = DllVersion (Join-Path $workshop "RealGlass.dll")
        if ($liveVersion -ne $deployedVersion) { Stop-Release "the Workshop folder reads $liveVersion, expected $deployedVersion" }
        Ok "the subscribed folder now holds $liveVersion, hashes identical"
    }

    Step "Park the local Addons\Mods copy so the game loads only the subscribed one"
    if ($DryRun) {
        Write-Host "   dry   would move $deployed -> $parked"
    } else {
        if (Test-Path $parked) { Remove-Item $parked -Recurse -Force -ErrorAction Stop }
        Move-Item $deployed $parked -ErrorAction Stop
        if (Test-Path $deployed) { Stop-Release "Addons\Mods\RealGlass is still there" }
        Ok "moved to $parked (the next 'dotnet build' recreates the local copy)"
    }
}

# ---- 5. What is left --------------------------------------------------------------------------

Write-Host ""
if ($DryRun -and $problems -gt 0) {
    Write-Host "DRY RUN finished with $problems problem(s) above. A real run would have stopped at the first." -ForegroundColor Yellow
    exit 1
}
if ($DryRun) { Write-Host "DRY RUN finished: every check passed. Run it without -DryRun to stage the release." -ForegroundColor Green; exit 0 }

if (-not $workshopId) {
    Write-Host "Ready: $version ($deployedVersion) is built, tested and deployed in Addons\Mods\RealGlass." -ForegroundColor Green
    Write-Host ""
    Write-Host "By hand, in this order:"
    Write-Host "  1. Launch the game and load a city. The first log line must read '$version'. (Claude reads the log.)"
    Write-Host "  2. Content Manager -> Mods -> Real Glass & Water -> Share. Drop Workshop\PreviewImage.png into"
    Write-Host "     the staging folder (the panel's folder button); title 'Real Glass & Water'; description ="
    Write-Host "     Workshop\Description.bbcode.txt; change note = Workshop\ChangeNotes\$version.bbcode.txt. Share."
    Write-Host "  3. On the Steam page: Harmony (2040656402) as a required item, screenshots, Public."
    Write-Host "     The GitHub repository public too (the description links it)."
    Write-Host "  4. Tell Claude the item's id: it goes into this script (`$workshopId), README and CHANGELOG;"
    Write-Host "     then tag v$version, push the tag, make the GitHub release, park the local copy."
} else {
    Write-Host "Staged: $version ($deployedVersion) is in the subscribed folder and the local copy is parked." -ForegroundColor Green
    Write-Host ""
    Write-Host "By hand, in this order:"
    Write-Host "  1. Skyve: the Workshop item must be ENABLED (it was disabled for development)."
    Write-Host "  2. Launch the game and load a city. The first log line must read '$version'; the settings:"
    Write-Host "     line must have nothing ignored or pulled into its range. (Claude reads the log.)"
    Write-Host "  3. Content Manager -> Mods -> Real Glass & Water. The button must read UPDATE."
    Write-Host "     If it reads Share, STOP: that is a local copy and would create a second item."
    Write-Host "  4. Title 'Real Glass & Water'; description = Workshop\Description.bbcode.txt; paste the change note"
    Write-Host "     (Workshop\ChangeNotes\$version.bbcode.txt); the preview image stays. Click Update."
    Write-Host "  5. On the Steam page: still ONE item ($workshopId), with the new change note."
    Write-Host "  6. Tell Claude: tag v$version, push the tag, make the GitHub release, and open the next"
    Write-Host "     changelog section."
}
