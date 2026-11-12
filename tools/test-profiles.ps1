# Offline checks of the profiles' pure parts, run against the BUILT mod DLL, no game needed
# (Volumetric Weather's test-profiles.ps1, with this mod's names):
#
#   dotnet build RealGlass/RealGlass.csproj -p:Deploy=false
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-profiles.ps1
#
# ProfileName: the rules for a new profile's name, which IS its file's name -- Windows' rules on
# every system (a profile made on a Mac must open on Windows), names the same without regard to
# case, a ".xml" typed at the end dropped, and the first free "Profile N". IsFileName: a name
# from a hand edit of <Profile> can never reach outside the profiles folder.
# The file: a profile is written by the same SettingsXmlFormat as RealGlass.xml, so a profile's
# text round-trips, and a broken or foreign one is refused as a FormatException, never anything
# else (Profiles catches exactly that and keeps the look running).
# The catalog (read from UI\SettingsCatalog.cs): what a profile carries. Every row of the options
# page and every hidden row is left out (Profiled = false), and so is every key and text row; the
# rows a profile does carry are listed once, to be read.
#
# Not covered, because it needs the game: Profiles itself (the folder, the watcher, the saves).
# The log says what it did: "profiles:" at startup, "profile:" for every pick, add and remove.

param(
    [string]$Dll = (Join-Path $PSScriptRoot "..\RealGlass\bin\Debug\RealGlass.dll")
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$root = Join-Path $PSScriptRoot "..\RealGlass"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class ProfileTestResolver
{
    public static string[] Dirs;
    [ThreadStatic] static bool busy;
    public static void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        if (busy) return null;
        busy = true;
        try
        {
            string name = new AssemblyName(e.Name).Name;
            foreach (string dir in Dirs)
            {
                string path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
            }
            return null;
        }
        finally { busy = false; }
    }
}
"@
[ProfileTestResolver]::Dirs = @($managed, (Split-Path -Parent $Dll), (Join-Path $PSScriptRoot "..\libs"))
[ProfileTestResolver]::Install()

# Loaded from bytes: Windows' application control can block a freshly built DLL loaded by path.
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$names = $asm.GetType('RealGlass.ProfileName', $true)
$fmt = $asm.GetType('RealGlass.SettingsXmlFormat', $true)
$entryType = $asm.GetType('RealGlass.SettingsXmlFormat+Entry', $true)
$profiles = $asm.GetType('RealGlass.Profiles', $true)
$failed = 0

function Check([string]$name, $actual, $expected) {
    if ("$actual" -ceq "$expected") { Write-Host "ok    $name  ->  $actual" }
    else { Write-Host "FAIL  $name  ->  [$actual]  expected [$expected]"; $script:failed++ }
}

function Why([string]$typed) { $w = $names::Why($typed); if ($w -eq $null) { return 'usable' } else { return $w } }
function List([string[]]$items) { $l = New-Object 'System.Collections.Generic.List[string]'; foreach ($i in $items) { $l.Add($i) }; return ,$l }

# ---- the folder, a name for good ----------------------------------------------------------------

Check "the folder's name" ($profiles.GetField('FolderName').GetValue($null)) "RealGlassProfiles"

# ---- the name rules -------------------------------------------------------------------------

Check "a plain name"                (Why "Downtown")              "usable"
Check "trimmed"                     ($names::Clean("  Downtown  ")) "Downtown"
Check "inner spaces kept"           ($names::Clean("Winter morning")) "Winter morning"
# Built from character codes: Windows PowerShell reads a script without a byte-order mark as ANSI.
$french = "Nuit d'" + [char]0x00E9 + "t" + [char]0x00E9 + " " + [char]0x00E0 + " Paris"
$japanese = -join ([char[]](0x5915, 0x713C, 0x3051, 0x306E, 0x7A7A))
Check "any language"                (Why $french)                 "usable"
Check "any script"                  (Why $japanese)               "usable"
Check "...and kept as typed"        ($names::Clean($french) -ceq $french) "True"
Check "a .xml typed is dropped"     ($names::Clean("Downtown.xml"))  "Downtown"
Check "...in any case"              ($names::Clean("Downtown.XML ")) "Downtown"
Check "...but a name that is only .xml is not a name" (Why ".xml") "Profiles.Invalid.Dot"
Check "empty"                       (Why "")                      "Profiles.Invalid.Empty"
Check "spaces only"                 (Why "    ")                  "Profiles.Invalid.Empty"
Check "null"                        (Why $null)                   "Profiles.Invalid.Empty"
Check "40 characters"               (Why ("a" * 40))              "usable"
Check "41 characters"               (Why ("a" * 41))              "Profiles.Invalid.Long"
Check "a slash"                     (Why "a/b")                   "Profiles.Invalid.Character"
Check "a backslash"                 (Why "a\b")                   "Profiles.Invalid.Character"
Check "a colon"                     (Why "C:glass")               "Profiles.Invalid.Character"
Check "a question mark"             (Why "why?")                  "Profiles.Invalid.Character"
Check "a star"                      (Why "*")                     "Profiles.Invalid.Character"
Check "angle brackets"              (Why "<b>")                   "Profiles.Invalid.Character"
Check "a quote"                     (Why 'say "hi"')              "Profiles.Invalid.Character"
Check "a pipe"                      (Why "a|b")                   "Profiles.Invalid.Character"
Check "a tab (control character)"   (Why "a`tb")                  "Profiles.Invalid.Character"
Check "a leading dot"               (Why ".hidden")               "Profiles.Invalid.Dot"
Check "a trailing dot"              (Why "night.")                "Profiles.Invalid.Dot"
Check "a dot inside is fine"        (Why "v1.2 glass")            "usable"
Check "CON"                         (Why "CON")                   "Profiles.Invalid.Reserved"
Check "con, any case"               (Why "con")                   "Profiles.Invalid.Reserved"
Check "NUL with an extension"       (Why "nul.night")             "Profiles.Invalid.Reserved"
Check "COM1"                        (Why "COM1")                  "Profiles.Invalid.Reserved"
Check "LPT9"                        (Why "lpt9")                  "Profiles.Invalid.Reserved"
Check "COM10 is not a device"       (Why "COM10")                 "usable"
Check "CONSOLE is not a device"     (Why "Console")               "usable"
# A whole name in parentheses is the list's own ("(unsaved profile)", "(mod defaults)").
$fullWidth = [string][char]0xFF08 + "defaults" + [char]0xFF09
Check "(mod defaults)"              (Why "(mod defaults)")        "Profiles.Invalid.Parentheses"
Check "(unsaved profile), spaced"   (Why "  (unsaved profile) ")  "Profiles.Invalid.Parentheses"
Check "full-width parentheses"      (Why $fullWidth)              "Profiles.Invalid.Parentheses"
Check "parentheses inside are fine" (Why "Night (rooms on)")      "usable"
Check "one opening one is fine"     (Why "(draft")                "usable"
Check "a Clean that fails is null"  ($names::Clean("a/b") -eq $null) "True"

# ---- the same name, the next free one ----------------------------------------------------------

Check "same without regard to case" ($names::Same("Downtown", "DOWNTOWN")) "True"
Check "different names"             ($names::Same("Downtown", "Downtowns")) "False"
Check "contains, any case"          ($names::Contains((List @("Clear", "downtown")), "Downtown")) "True"
Check "contains: not there"         ($names::Contains((List @("Clear")), "Downtown")) "False"
Check "the first free"              ($names::NextFree((List @()), "Profile {0}")) "Profile 1"
Check "the next free"               ($names::NextFree((List @("Profile 1", "profile 2", "Profile 4")), "Profile {0}")) "Profile 3"
Check "another language's pattern"  ($names::NextFree((List @("Profil 1")), "Profil {0}")) "Profil 2"

# ---- a name that must stay inside the folder --------------------------------------------------

Check "a listed name"               ($names::IsFileName("Downtown")) "True"
Check "an odd but real file name"   ($names::IsFileName("CON-ish glass (2)")) "True"
Check "empty is not a file"         ($names::IsFileName("")) "False"
Check "'.' is not a file"           ($names::IsFileName(".")) "False"
Check "'..' is not a file"          ($names::IsFileName("..")) "False"
Check "up and out, backslash"       ($names::IsFileName("..\RealGlass")) "False"
Check "up and out, slash"           ($names::IsFileName("../RealGlass")) "False"
Check "a drive"                     ($names::IsFileName("C:\Windows\win")) "False"

# Listed and picked: the pick is kept trimmed and written into RealGlass.xml.
Check "listable: a plain name"      ($names::IsListable("Downtown")) "True"
Check "listable: any language"      ($names::IsListable($french)) "True"
Check "not listable: a leading space (the pick would come back as 'Downtown')" ($names::IsListable(" Downtown")) "False"
Check "not listable: a trailing space" ($names::IsListable("Downtown ")) "False"
Check "not listable: a control character (Linux allows it in a file name)" ($names::IsListable("Downtown" + [char]1)) "False"
Check "not listable: a tab"         ($names::IsListable("a`tb")) "False"
Check "not listable: up and out"    ($names::IsListable("..\RealGlass")) "False"

# ---- a profile's file -------------------------------------------------------------------------

function Entry($section, $group, $name, $value, $comment) {
    $e = [Activator]::CreateInstance($entryType)
    $e.Section = $section; $e.Group = $group; $e.Name = $name; $e.Value = $value; $e.Comment = $comment
    return $e
}

function Parse([string]$text) {
    $version = 0
    $dups = New-Object 'System.Collections.Generic.List[string]'
    try {
        $values = $fmt::Parse($text, [ref]$version, $dups)
        return @{ Values = $values; Version = $version; Error = $null }
    }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException -ne $null -and -not ($inner -is [System.FormatException])) { $inner = $inner.InnerException }
        return @{ Values = $null; Version = 0; Error = $inner }
    }
}

$list = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($entryType))
$list.Add((Entry "In-game panel: Glass tab" "TheGlass" "GlassPreset" "10" "Kind of glass. One of 10 (Reflective blue), 0 (Custom); default 10."))
$list.Add((Entry "In-game panel: Glass tab" $null "GlassColor" "#2E6A80" "Glass color. A color, #RRGGBB; default #2E6A80."))
$list.Add((Entry "In-game panel: Interiors tab" $null "InteriorsEnabled" "true" "Rooms behind the windows. true or false, default true."))
$list.Add((Entry "In-game panel: Water tab" $null "WaterBrightness" "85" "Brightness, in %. 0 to 200, default 100."))
$header = "Real Glass & Water profile: a whole look.`n`n- To share the look, copy this file into the RealGlassProfiles folder -- of another computer."
$text = $fmt::Write($list, $header)

$r = Parse $text
Check "a profile's text parses back" ($r.Error -eq $null) "True"
if ($r.Values -ne $null) {
    Check "a choice by value"  $r.Values["GlassPreset"] "10"
    Check "a colour"           $r.Values["GlassColor"] "#2E6A80"
    Check "a switch"           $r.Values["InteriorsEnabled"] "true"
    Check "a percentage"       $r.Values["WaterBrightness"] "85"
    Check "four values"        $r.Values.Count 4
}

$r = Parse "<RealGlass version=`"99`"><GlassPreset>3</GlassPreset><SomethingNew>3</SomethingNew></RealGlass>"
Check "a newer version is read as far as it goes" ("" + $r.Version + "," + $r.Values["GlassPreset"] + "," + $r.Values["SomethingNew"]) "99,3,3"

$r = Parse "<RealGlass><GlassPreset>1</GlassPreset>"
Check "half a file is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

$r = Parse "<VolumetricClouds><CloudStyle>1</CloudStyle></VolumetricClouds>"
Check "another mod's file is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

$r = Parse "not xml at all"
Check "not XML at all is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

# An editor may save with a byte-order mark (Notepad's "UTF-8 with BOM", or UTF-16). The mod
# reads every file with File.ReadAllText, which takes the mark off; so does this.
$tmp = [IO.Path]::GetTempFileName()
try {
    $xml = "<RealGlass><GlassPreset>3</GlassPreset></RealGlass>"
    [IO.File]::WriteAllText($tmp, $xml, (New-Object System.Text.UTF8Encoding($true)))
    $r = Parse ([IO.File]::ReadAllText($tmp))
    Check "a file saved as UTF-8 with a byte-order mark is read" ($(if ($r.Values) { $r.Values["GlassPreset"] } else { $r.Error.Message })) "3"
    [IO.File]::WriteAllText($tmp, $xml, [System.Text.Encoding]::Unicode)
    $r = Parse ([IO.File]::ReadAllText($tmp))
    Check "a file saved as UTF-16 is read" ($(if ($r.Values) { $r.Values["GlassPreset"] } else { $r.Error.Message })) "3"
}
finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

# ---- what a profile carries (the catalog's source) ---------------------------------------------

$catalog = Get-Content (Join-Path $root "UI\SettingsCatalog.cs") -Raw
$blocks = [regex]::Split($catalog, 'Add\(new Row') | Select-Object -Skip 1
$carried = New-Object 'System.Collections.Generic.List[string]'
$wrong = New-Object 'System.Collections.Generic.List[string]'
foreach ($b in $blocks) {
    $b = $b.Substring(0, $b.IndexOf('});'))
    if ($b -notmatch '(?:Float|Bool|Int|Key|Colour|Text) = Settings\.(\w+)') { continue }  # a button, a status line: no value
    $name = $Matches[1]
    $kind = if ($b -match '(Float|Bool|Int|Key|Colour|Text) = Settings\.') { $Matches[1] } else { '?' }
    $off = $b -match 'Profiled = false'
    $panel = $b -match 'Panel = PanelPage\.'
    $options = $b -match 'Options = OptionsPage\.'
    if ($off) { continue }
    if ($options) { $wrong.Add("$name (options page)") }
    elseif (-not $panel) { $wrong.Add("$name (no panel: housekeeping)") }
    elseif ($kind -eq 'Key' -or $kind -eq 'Text') { $wrong.Add("$name ($kind row)") }
    else { $carried.Add($name) }
}
Check "a profile carries no options-page, hidden, key or text row" ($wrong -join ", ") ""
foreach ($n in @('GlassShowMask', 'SsrQuality', 'WaterPaintSize', 'WaterPaintStrength', 'WaterPaintHardness')) {
    Check "not carried: $n" ($carried.Contains($n)) "False"
}
foreach ($n in @('GlassPreset', 'GlassColor', 'GlassReflectivity', 'GlassClarity', 'InteriorsEnabled', 'WaterEnabled', 'VehicleGlass')) {
    Check "carried: $n" ($carried.Contains($n)) "True"
}
Write-Host "      a profile carries $($carried.Count) rows: $($carried -join ', ')"

Write-Host ""
if ($failed -eq 0) { Write-Host "all passed" } else { Write-Host "$failed FAILED"; exit 1 }
