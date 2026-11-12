# Offline checks of the language files against the code, no game needed (Volumetric Weather's
# check, for this mod):
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-localization.ps1
#
# LocalizationFile.cs has no Unity dependency, so its SOURCE is compiled here and reads the real
# Localization\*.xml. Then:
#   - every file parses, with no key written twice and every {n} well-formed;
#   - English has every key the code asks for: the rows of SettingsCatalog.cs (Label, Note,
#     Confirm, Group, choice names, choice units -- read from the source, as the in-game check
#     reads them from the built rows), the tab names, the building window's rows, and every
#     key-shaped string literal in *.cs (Glass\ included);
#   - English has no key nothing asks for (dead text a translator would translate for nothing);
#   - a tooltip (Name.Tooltip) belongs to a row that exists and fits one line, 70 characters. Only
#     rows whose name leaves a question about what they do have one (the author, 2026-10-24: "remove
#     redundant tooltips ... all that are obvious what they do"; 2026-10-21: "add tooltips where
#     needed"; 2026-10-04 he had taken out every row's: "the labels are already pretty descriptive");
#   - a translation has no key English lacks, keeps English's {n} and \n in every text, and says
#     which English keys it has not done yet;
#   - wrapped texts come out as one line, \n as a real break.
# The same key check runs in the game at startup (SettingsCatalog.LogLayout), from the real rows.

$root = Join-Path $PSScriptRoot "..\RealGlass"
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
[void]$cp.ReferencedAssemblies.Add("System.dll")
[void]$cp.ReferencedAssemblies.Add("System.Xml.dll")
# In memory: Windows' application control can block a freshly compiled DLL loaded by path.
$cp.GenerateInMemory = $true
Add-Type -TypeDefinition (Get-Content (Join-Path $root "LocalizationFile.cs") -Raw) -CompilerParameters $cp

$failed = 0
function Check([string]$name, $actual, $expected) {
    if ("$actual" -ceq "$expected") { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name  ->  [$actual]  expected [$expected]"; $script:failed++ }
}

function Load([string]$path) {
    $code = ''; $name = ''
    $dups = New-Object 'System.Collections.Generic.List[string]'
    $table = [RealGlass.LocalizationFile]::Parse([IO.File]::ReadAllText($path), [ref]$code, [ref]$name, $dups)
    return @{ Table = $table; Code = $code; Name = $name; Duplicates = $dups }
}

$en = Load (Join-Path $root "Localization\en.xml")
Check "en.xml: code" $en.Code "en"
Check "en.xml: no key twice" ($en.Duplicates -join ", ") ""

# ---- what the code needs ------------------------------------------------------------------

$needed = New-Object 'System.Collections.Generic.HashSet[string]'

# Setting property -> its name, from Settings.cs.
$settingsSrc = Get-Content (Join-Path $root "Settings.cs") -Raw
$nameOf = @{}
foreach ($m in [regex]::Matches($settingsSrc, '(\w+) = new (?:Float|Bool|Int|Color|String)Setting\("(\w+)"')) { $nameOf[$m.Groups[1].Value] = $m.Groups[2].Value }
foreach ($m in [regex]::Matches($settingsSrc, '(\w+) = new SavedInputKey\("(\w+)"')) { $nameOf[$m.Groups[1].Value] = $m.Groups[2].Value }

# The rows, one block per "Add(new Row".
$catalog = Get-Content (Join-Path $root "UI\SettingsCatalog.cs") -Raw
$blocks = [regex]::Split($catalog, 'Add\(new Row') | Select-Object -Skip 1
$rows = 0
$rowNames = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($b in $blocks) {
    $b = $b.Substring(0, $b.IndexOf('});'))
    $rows++
    $kind = if ($b -match 'Kind = RowKind\.(\w+)') { $Matches[1] } else { '?' }
    $name = $null
    if ($b -match '(?:Float|Bool|Int|Key|Colour|Text) = Settings\.(\w+)') { $name = $nameOf[$Matches[1]] }
    if ($b -match 'Id = "(\w+)"') { $name = $Matches[1] }
    if (-not $name) { Write-Host "FAIL  a row with neither a setting nor an Id:`n$b"; $failed++; continue }
    [void]$rowNames.Add($name)

    if ($kind -ne 'Status') { [void]$needed.Add("$name.Label") }
    if ($b -match 'HasNote = true') { [void]$needed.Add("$name.Note") }
    if ($b -match 'Confirm = true') { [void]$needed.Add("$name.Confirm") }
    if ($b -match 'Group = "(\w+)"') { [void]$needed.Add("Group." + $Matches[1]) }
    if ($b -match 'ChoiceKeys = new\[\]\s*\{([^}]*)\}') {
        foreach ($k in [regex]::Matches($Matches[1], '"([^"]+)"')) { [void]$needed.Add($k.Groups[1].Value) }
    }
    if ($b -match 'ChoiceUnit = "([^"]+)"') { [void]$needed.Add($Matches[1]) }
}
Write-Host "      ($rows rows in the catalog)"

# The panel's tabs. (The options page has no tabs, so OptionsPage names none.)
if ($catalog -match "public enum PanelPage \{([^}]*)\}") {
    foreach ($v in ($Matches[1] -split ',')) { $v = $v.Trim(); if ($v -and $v -ne 'None') { [void]$needed.Add("Tab.$v") } }
}

# The building window's and the creator window's rows (PerAsset\AssetCatalog.cs, not the
# catalog's; the creator window's preview switches are CreatorAsset.cs's settings): each setting
# shown (its Label), the group headings. Status lines and buttons are key-shaped literals ("Asset.Pick").
$assetRows = 0
foreach ($file in @("PerAsset\AssetCatalog.cs", "PerAsset\CreatorAsset.cs", "PerAsset\PropCatalog.cs")) {
    $assetSrc = Get-Content (Join-Path $root $file) -Raw
    foreach ($m in [regex]::Matches($assetSrc, 'new (?:Float|Bool|Int|Color)Setting\("(\w+)"')) {
        [void]$needed.Add($m.Groups[1].Value + ".Label"); $assetRows++
        [void]$rowNames.Add($m.Groups[1].Value)
    }
    foreach ($m in [regex]::Matches($assetSrc, 'Group = "(\w+)"|Percent\(_\w+, "(\w+)"|Preview\(CreatorAsset\.\w+, CreatorAsset\.\w+, "(\w+)"')) {
        $g = if ($m.Groups[1].Success) { $m.Groups[1].Value } elseif ($m.Groups[2].Success) { $m.Groups[2].Value } else { $m.Groups[3].Value }
        [void]$needed.Add("Group.$g")
    }
}
Write-Host "      ($assetRows rows in the building and creator windows)"

# Key-shaped literals anywhere in the code ("Unit.Pixels", "Colour.Copy"...), including the ones
# picked by a ?: that a Localization.Get("...") search would miss.
$prefixes = 'Mod|Tab|Key|Status|Unit|Readout|File|Language|Group|ResetAll|Reset|Colour|GlassPreset|SsrQuality|SkyReflection|OffscreenFill|GlassDistance|Asset|AssetFile|Picker|Creator|CreatorFile|Error|Props|Profiles|ShorePaint|WaterFoamPaintClear'
foreach ($file in Get-ChildItem $root -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    foreach ($m in [regex]::Matches((Get-Content $file.FullName -Raw), "`"((?:$prefixes)\.[A-Z][A-Za-z.]*[A-Za-z])`"")) {
        [void]$needed.Add($m.Groups[1].Value)
    }
}

# A tooltip is optional (Row.Tooltip: English decides which rows have one), so it is asked for
# only where English has it -- and only a row's: one with no row is dead text, caught below.
$tipCount = 0
foreach ($key in @($en.Table.Keys)) {
    if ($key -match '^(\w+)\.Tooltip$' -and $rowNames.Contains($Matches[1])) { [void]$needed.Add($key); $tipCount++ }
}
Write-Host "      ($tipCount rows with a tooltip)"

$missing = @($needed | Where-Object { -not $en.Table.ContainsKey($_) } | Sort-Object)
Check "English has every key the code needs ($($needed.Count))" ($missing -join ", ") ""

$unused = @($en.Table.Keys | Where-Object { -not $needed.Contains($_) } | Sort-Object)
Check "English has no key nothing uses" ($unused -join ", ") ""

# ---- every text ---------------------------------------------------------------------------

$bad = @()
$dummy = [object[]]@(0..9 | ForEach-Object { "#$_" })
foreach ($key in $en.Table.Keys) {
    $text = $en.Table[$key]
    if ([string]::IsNullOrEmpty($text)) { $bad += "$key (empty)"; continue }
    try { [void][string]::Format($text, $dummy) } catch { $bad += "$key (bad {n}: $text)" }
    if ($text -match '  \S' -and $key -notmatch '^Status\.') { $bad += "$key (double space)" }
}
Check "every English text is non-empty with well-formed {n}" ($bad -join "; ") ""

# Tooltips: one short line in the game's tooltip box (Volumetric Weather's rule of thumb).
$long = @($en.Table.Keys | Where-Object { $_ -match '\.Tooltip$' -and $en.Table[$_].Length -gt 70 } | Sort-Object |
    ForEach-Object { "$_ ($($en.Table[$_].Length))" })
Check "every tooltip fits one line (70 characters)" ($long -join ", ") ""

Check "a wrapped text is one line" ($en.Table["Mod.Unsupported"] -match "switched itself off: nothing in this city") "True"
Check "\n is a real break, no stray spaces" ($en.Table["ResetAll.Confirm"] -match "how the mod comes\?`n`nThis throws away") "True"
$header = $en.Table["File.Header"] -split "`n"
Check "the settings file header: 11 lines, blank second line" ("" + $header.Count + "," + ($header[1] -eq "")) "11,True"

# ---- translations -------------------------------------------------------------------------

foreach ($file in Get-ChildItem (Join-Path $root "Localization") -Filter *.xml | Where-Object { $_.Name -ne 'en.xml' }) {
    $t = Load $file.FullName
    $code = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    Check "$($file.Name): code matches the file name" $t.Code $code
    Check "$($file.Name): no key twice" ($t.Duplicates -join ", ") ""
    $extra = @($t.Table.Keys | Where-Object { -not $en.Table.ContainsKey($_) } | Sort-Object)
    Check "$($file.Name): no key English lacks" ($extra -join ", ") ""
    # A {n} left out formats without an error (the number just goes missing); a \n left out runs
    # two lines of the settings file's header into one.
    $differ = @()
    $holes = { param($s) (@([regex]::Matches($s, '\{\d+\}') | ForEach-Object { $_.Value }) | Sort-Object) -join "," }
    foreach ($key in @($t.Table.Keys | Where-Object { $en.Table.ContainsKey($_) } | Sort-Object)) {
        $mine = $t.Table[$key]; $english = $en.Table[$key]
        if ((& $holes $mine) -ne (& $holes $english)) { $differ += "$key ({n})" }
        if ($mine.Split("`n").Count -ne $english.Split("`n").Count) { $differ += "$key (\n)" }
        try { [void][string]::Format($mine, $dummy) } catch { $differ += "$key (bad {n})" }
    }
    Check "$($file.Name): the same {n} and \n as English" ($differ -join ", ") ""
    $todo = @($en.Table.Keys | Where-Object { -not $t.Table.ContainsKey($_) }).Count
    Write-Host "      $($file.Name): $($t.Table.Count) texts, $todo still in English"
}

Write-Host ""
if ($failed -eq 0) { Write-Host "all passed" } else { Write-Host "$failed FAILED"; exit 1 }
