# Offline checks of the settings, run against the BUILT mod DLL, no game needed:
#
#   dotnet build RealGlass/RealGlass.csproj -p:Deploy=false
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-settings.ps1
#
# What is covered:
# - SettingsXmlFormat, the text of RealGlass.xml: numbers survive the trip slider -> file -> slider
#   exactly, in any Windows culture; the spellings a hand edit may use are read (and the dangerous
#   ones refused: "0,5", "NaN", "8" meaning Backspace); keys format and parse both ways, F8 (the
#   panel's key, and the default the DLL carries) included; a written file parses back to the same values, comments included (an
#   XML comment may not hold "--", and the labels do); a broken or foreign file is reported,
#   never half-read.
# - ColorText, the colour field's and the file's reader: #RRGGBB both ways, the other spellings.
# - GlassPresets against the catalog: every preset is a choice of the GlassPreset row, in range
#   of the rows it writes, and the default preset IS the default colour, reflectivity and clarity
#   (or the default is Custom, which writes nothing).
# - Every slider row's default inside its own range (read from UI\SettingsCatalog.cs, with the
#   constants' values from the DLL): a default out of range would be clamped and written back.
#
# Not covered, because it needs the game: SettingsXml itself (the catalog, the saver). The log
# says what that did on every start.

param(
    [string]$Dll = (Join-Path $PSScriptRoot "..\RealGlass\bin\Debug\RealGlass.dll")
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$root = Join-Path $PSScriptRoot "..\RealGlass"

Add-Type -TypeDefinition @"
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
public static class TestResolver
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
public static class TestHelp
{
    // Single-precision arithmetic, as the mod does it: PowerShell would promote to double.
    public static float Mul(float a, float b) { return a * b; }
    public static float Div(float a, float b) { return a / b; }

    // PowerShell 5.1 puts the thread's culture back after every statement, so the switch has
    // to happen in the same call as the method under test.
    public static object InCulture(string culture, MethodInfo method, object[] args)
    {
        CultureInfo saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
        try { return method.Invoke(null, args); }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    public static string CultureOf(string culture, double value)
    {
        CultureInfo saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
        try { return value.ToString(); }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }
}
"@
[TestResolver]::Dirs = @($managed, (Split-Path -Parent $Dll), (Join-Path $PSScriptRoot "..\libs"))
[TestResolver]::Install()

# Loaded from bytes: Windows' application control can block a freshly built DLL loaded by path.
$unity = [System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$fmt = $asm.GetType('RealGlass.SettingsXmlFormat', $true)
$entryType = $asm.GetType('RealGlass.SettingsXmlFormat+Entry', $true)
$colorText = $asm.GetType('RealGlass.ColorText', $true)
$presets = $asm.GetType('RealGlass.GlassPresets', $true)
$defaults = $asm.GetType('RealGlass.Settings+Defaults', $true)
$settings = $asm.GetType('RealGlass.Settings', $true)
$color32 = $unity.GetType('UnityEngine.Color32', $true)
$failed = 0

function Check([string]$name, $actual, $expected) {
    if ("$actual" -ceq "$expected") { Write-Host "ok    $name  ->  $actual" }
    else { Write-Host "FAIL  $name  ->  [$actual]  expected [$expected]"; $script:failed++ }
}

function TryNumber([string]$text) {
    $v = 0.0
    if ($fmt::TryParseNumber($text, [ref]$v)) { return $v } else { return 'refused' }
}

function TryBool([string]$text) {
    $v = $false
    if ($fmt::TryParseBool($text, [ref]$v)) { return $v } else { return 'refused' }
}

function TryKey([string]$text) {
    $v = 0
    if ($fmt::TryParseKey($text, [ref]$v)) { return $v } else { return 'refused' }
}

function TryColour([string]$text) {
    $args1 = [object[]]@($text, [Activator]::CreateInstance($color32))
    if ($colorText.GetMethod('TryParse').Invoke($null, $args1)) { return $colorText::Format($args1[1]) } else { return 'refused' }
}

function Parse([string]$text) {
    $version = 0
    $dups = New-Object 'System.Collections.Generic.List[string]'
    try {
        $values = $fmt::Parse($text, [ref]$version, $dups)
        return @{ Values = $values; Version = $version; Duplicates = $dups; Error = $null }
    }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException -ne $null -and -not ($inner -is [System.FormatException])) { $inner = $inner.InnerException }
        return @{ Values = $null; Version = 0; Duplicates = $dups; Error = $inner }
    }
}

function Default([string]$name) {
    $f = $defaults.GetField($name)
    if ($f -eq $null) { return $null }
    return $f.GetValue($null)
}

# ---- numbers ----------------------------------------------------------------------------

Check "47%"            ($fmt::FormatNumber([double][TestHelp]::Mul(0.47, 100))) "47"
Check "3.8 m"          ($fmt::FormatNumber([double][single]3.8)) "3.8"
Check "a negative"     ($fmt::FormatNumber([double][single]-0.4)) "-0.4"
Check "no -0"          ($fmt::FormatNumber(-0.00000001)) "0"
Check "four decimals"  ($fmt::FormatNumber(0.123456)) "0.1235"

# Every whole-percent slider position from 0 to 300%: stored -> written -> read -> stored must
# give back the very same float, and the number written must be the one the slider shows.
$bad = 0
foreach ($p in 0..300) {
    $stored = [TestHelp]::Div([single]$p, 100)
    $text = $fmt::FormatNumber([double][TestHelp]::Mul($stored, 100))
    $back = [TestHelp]::Div([single](TryNumber $text), 100)
    if ($back -ne $stored -or $text -ne "$p") { $bad++ ; Write-Host "   $p% -> '$text' -> $back" }
}
Check "every percent survives the file exactly" $bad 0

# The metre sliders step 0.1 and 0.5, which store float noise (38 * 0.1f = 3.8000002); four
# decimals drop it on purpose. What must hold: the value comes back within that noise, and
# writing it again gives the same text -- or a saved file would keep changing under the player.
$bad = 0
foreach ($i in 8..200) {
    $stored = [TestHelp]::Mul([single]$i, [single]0.1)
    $text = $fmt::FormatNumber([double]$stored)
    $back = [single](TryNumber $text)
    $again = $fmt::FormatNumber([double]$back)
    if ([math]::Abs([double]$back - [double]$stored) -gt 1e-5 -or $again -ne $text) { $bad++ ; Write-Host "   $stored -> '$text' -> $back -> '$again'" }
}
Check "a 0.1-step slider: stable text, value within float noise" $bad 0

Check "dot"            (TryNumber "0.5") 0.5
Check "comma refused"  (TryNumber "0,5") "refused"
Check "no thousands"   (TryNumber "13,500") "refused"
Check "percent sign"   (TryNumber "47%") 47
Check "spaces"         (TryNumber " 12 ") 12
Check "exponent"       (TryNumber "1e3") 1000
Check "NaN refused"    (TryNumber "NaN") "refused"
Check "inf refused"    (TryNumber "Infinity") "refused"
Check "empty refused"  (TryNumber "") "refused"

# A German Windows writes 0,5 -- the file must not.
$format = $fmt.GetMethod('FormatNumber')
$parse = $fmt.GetMethod('TryParseNumber')
Check "de-DE really is a comma culture" ([TestHelp]::CultureOf('de-DE', 0.5)) "0,5"
Check "de-DE writes a dot" ([TestHelp]::InCulture('de-DE', $format, @([double]0.5))) "0.5"
$args2 = [object[]]@("0.5", [double]0)
[void][TestHelp]::InCulture('de-DE', $parse, $args2)
Check "de-DE reads a dot" $args2[1] 0.5
$args2 = [object[]]@("0,5", [double]0)
Check "de-DE still refuses a comma" ([TestHelp]::InCulture('de-DE', $parse, $args2)) "False"

# ---- switches ---------------------------------------------------------------------------

Check "true"   (TryBool "true") "True"
Check "TRUE"   (TryBool "TRUE") "True"
Check "on"     (TryBool " on ") "True"
Check "1"      (TryBool "1") "True"
Check "false"  (TryBool "false") "False"
Check "no"     (TryBool "no") "False"
Check "maybe"  (TryBool "maybe") "refused"

# ---- keys -------------------------------------------------------------------------------

$ctrl = 0x40000000; $shift = 0x20000000; $alt = 0x10000000
$F7 = 288; $F8 = 289; $G = 103; $Alpha8 = 56; $Backspace = 8

Check "F8 (this mod's key) out"  ($fmt::FormatKey($F8)) "F8"
Check "F8 in"                    (TryKey "F8") $F8
Check "the panel key's default is F8, not the game's debug key F7" ($fmt::FormatKey($settings::DefaultPanelKey)) "F8"
Check "F7 in"                    (TryKey "F7") $F7
Check "Ctrl+Shift+G out"         ($fmt::FormatKey($G -bor $ctrl -bor $shift)) "Ctrl+Shift+G"
Check "Alt+F7 out"               ($fmt::FormatKey($F7 -bor $alt)) "Alt+F7"
Check "None out"                 ($fmt::FormatKey(0)) "None"
Check "Ctrl+None out"            ($fmt::FormatKey($ctrl)) "None"
Check "any case, any order"      (TryKey "shift+CONTROL+g") ($G -bor $ctrl -bor $shift)
Check "None in"                  (TryKey "none") 0
Check "a digit is the top row, not a value" (TryKey "8") $Alpha8
Check "Backspace by name"        (TryKey "Backspace") $Backspace
Check "a number refused"         (TryKey "288") "refused"
Check "a flags list refused"     (TryKey "F7,F8") "refused"
Check "unknown modifier"         (TryKey "Hyper+F7") "refused"
Check "modifier alone"           (TryKey "Ctrl+") "refused"
Check "unknown key"              (TryKey "Foo") "refused"

$keyType = $unity.GetType('UnityEngine.KeyCode')
$bad = 0
foreach ($code in [Enum]::GetValues($keyType)) {
    if ([int]$code -eq 0) { continue }
    foreach ($mods in @(0, $ctrl, ($ctrl -bor $shift -bor $alt))) {
        $encoded = [int]$code -bor $mods
        $text = $fmt::FormatKey($encoded)
        $back = TryKey $text
        # Aliased enum names print as one of the pair; the value is what has to come back.
        if ($back -ne $encoded) { $bad++; if ($bad -le 5) { Write-Host "   $encoded -> '$text' -> $back" } }
    }
}
Check "every Unity key, with modifiers, both ways" $bad 0

# ---- colours ----------------------------------------------------------------------------

Check "#RRGGBB"               (TryColour "#2A5C78") "#2A5C78"
Check "lower case, no #"      (TryColour "2a5c78") "#2A5C78"
Check "0x"                    (TryColour "0x2A5C78") "#2A5C78"
Check "#RGB"                  (TryColour "#FFF") "#FFFFFF"
Check "a list, 0..255"        (TryColour "42, 92, 120") "#2A5C78"
Check "rgb()"                 (TryColour "rgb(42, 92, 120)") "#2A5C78"
Check "a list, 0..1"          (TryColour "1, 0.5, 0") "#FF8000"
Check "percentages"           (TryColour "100%, 50%, 0%") "#FF8000"
Check "a name"                (TryColour "gold") "#FFD700"
Check "#RRGGBBAA, alpha gone" (TryColour "#2A5C7880") "#2A5C78"
Check "bare fff refused"      (TryColour "fff") "refused"
Check "rubbish refused"       (TryColour "glass") "refused"

# ---- the glass presets --------------------------------------------------------------------

$count = $presets::Count
Check "ten real-glass presets" $count 10
$custom = $presets.GetField('Custom').GetValue($null)
Check "Custom is 0" $custom 0

$catalog = Get-Content (Join-Path $root "UI\SettingsCatalog.cs") -Raw
$presetValues = @()
if ($catalog -match 'Int = Settings\.GlassPreset,[\s\S]*?ChoiceValues = new\[\] \{([^}]*)\}') {
    $presetValues = @($Matches[1] -split ',' | ForEach-Object { [int]$_.Trim() })
}
Check "the GlassPreset row offers every preset and Custom, the default first, Custom last" (($presetValues | ForEach-Object { "$_" }) -join ",") "10,8,3,2,4,5,6,7,1,9,0"
Check "  every preset value once" (($presetValues | Sort-Object | ForEach-Object { "$_" }) -join ",") "0,1,2,3,4,5,6,7,8,9,10"

$bad = @()
for ($i = 0; $i -lt $count; $i++) {
    $p = $presets::At($i)
    if ($p.Value -ne $i + 1) { $bad += "$($p.LogName) has value $($p.Value), not $($i + 1) (append only, in value order)" }
    if ($p.Reflectivity -lt 0 -or $p.Reflectivity -gt 0.6) { $bad += "$($p.LogName) reflectivity $($p.Reflectivity) outside the row's 0..60%" }
    if ($p.Clarity -lt 0 -or $p.Clarity -gt 1) { $bad += "$($p.LogName) clarity $($p.Clarity) outside 0..100%" }
    if ($p.Colour.a -ne 255) { $bad += "$($p.LogName) colour not opaque" }
}
Check "every preset in value order and inside its rows' ranges" ($bad -join "; ") ""

# The default glass is either a preset whose numbers ARE the default colour, reflectivity and
# clarity, or Custom (which writes nothing): never a preset that a Reset would contradict.
$defaultPreset = Default 'GlassPreset'
if ($defaultPreset -eq $custom) {
    Check "the default glass is Custom: the three rows' own defaults stand" $defaultPreset $custom
    $blue = $presets::At(10 - 1)
    Check "  in Reflective blue's colour" ($colorText::Format($blue.Colour)) ($colorText::Format((Default 'GlassColor')))
    Check "  with Reflective blue's clarity" ([single]$blue.Clarity) ([single](Default 'GlassClarity'))
}
else {
    $default = $presets::At($defaultPreset - 1)
    Check "the default preset's colour is the default colour" ($colorText::Format($default.Colour)) ($colorText::Format((Default 'GlassColor')))
    Check "  and its reflectivity the default's" ([single]$default.Reflectivity) ([single](Default 'GlassReflectivity'))
    Check "  and its clarity the default's" ([single]$default.Clarity) ([single](Default 'GlassClarity'))
}

$preset = [Activator]::CreateInstance($asm.GetType('RealGlass.GlassPreset', $true))
$tryArgs = [object[]]@(0, $preset)
Check "TryGet(Custom) is false: Custom writes nothing" ($presets.GetMethod('TryGet').Invoke($null, $tryArgs)) "False"
$tryArgs = [object[]]@(42, $preset)
Check "TryGet(an unknown value) is false" ($presets.GetMethod('TryGet').Invoke($null, $tryArgs)) "False"

Check "the sky cube's sizes" ("" + $settings::CubePixels(0) + "," + $settings::CubePixels(1) + "," + $settings::CubePixels(2) + "," + $settings::CubePixels(7)) "64,128,256,128"

# ---- every slider's default inside its range ----------------------------------------------

$blocks = [regex]::Split($catalog, 'Add\(new Row') | Select-Object -Skip 1
$bad = @()
$checked = 0
foreach ($b in $blocks) {
    $b = $b.Substring(0, $b.IndexOf('});'))
    if ($b -notmatch 'DefaultFloat = Settings\.Defaults\.(\w+)') { continue }
    $name = $Matches[1]
    if ($b -notmatch 'Min = ([\d.]+)f, Max = ([\d.]+)f') { $bad += "$name (no range)"; continue }
    $min = [double]$Matches[1]; $max = [double]$Matches[2]
    $percent = $b -match 'Kind = RowKind\.Percent'
    $value = Default $name
    if ($value -eq $null) { $bad += "$name (no such default)"; continue }
    $shown = if ($percent) { [double][TestHelp]::Mul([single]$value, 100) } else { [double]$value }
    if ($shown -lt $min - 1e-4 -or $shown -gt $max + 1e-4) { $bad += "$name $shown outside $min..$max" }
    $checked++
}
Check "every slider default is inside its slider ($checked rows)" ($bad -join "; ") ""

# ---- the file ---------------------------------------------------------------------------

function Entry($section, $group, $name, $value, $comment) {
    $e = [Activator]::CreateInstance($entryType)
    $e.Section = $section; $e.Group = $group; $e.Name = $name; $e.Value = $value; $e.Comment = $comment
    return $e
}

$list = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($entryType))
$list.Add((Entry "In-game panel: Glass tab" $null "GlassEnabled" "true" "Realistic glass. true or false, default true."))
$list.Add((Entry "In-game panel: Glass tab" "The glass" "GlassReflectivity" "12" "Reflectivity, head-on, in %. 0 to 60, default 12."))
$list.Add((Entry "In-game panel: Glass tab" $null "GlassColor" "#2A5C78" "A label -- with a dash -- and one at the end -"))
$list.Add((Entry "The game's options page" $null "PanelKey" "F8" "Open this panel. A key such as F8."))
$list.Add((Entry "The game's options page" $null "Odd" "<&>" $null))
$text = $fmt::Write($list, "Header line one`nHeader -- line two ends in a dash -")

$r = Parse $text
Check "written file parses" ($r.Error -eq $null) "True"
if ($r.Values -ne $null) {
    Check "version"           $r.Version 1
    Check "a switch"          $r.Values["GlassEnabled"] "true"
    Check "a percentage"      $r.Values["GlassReflectivity"] "12"
    Check "a colour"          $r.Values["GlassColor"] "#2A5C78"
    Check "a key"             $r.Values["PanelKey"] "F8"
    Check "escaped text"      $r.Values["Odd"] "<&>"
    Check "names ignore case" $r.Values["glassreflectivity"] "12"
    Check "five values"       $r.Values.Count 5
}
Check "CRLF line ends"       ($text.Contains("`r`n")) "True"
Check "the root is <RealGlass>" ($text -match "<RealGlass version=`"1`">") "True"
Check "one section heading each" ([regex]::Matches($text, "==========  ?The game's options page").Count) 1

$r = Parse "<RealGlass version=`"1`"><Mine><A>1</A></Mine><B> 2 </B></RealGlass>"
Check "wrapped lines are looked into" ("" + $r.Values["A"] + "," + $r.Values["B"]) "1,2"

$r = Parse "<RealGlass><A>1</A><a>2</a></RealGlass>"
Check "twice: the last one wins" $r.Values["A"] "2"
Check "twice: and it is reported" ($r.Duplicates -join ",") "a"
Check "no version attribute reads as 0" $r.Version 0

$r = Parse "<RealGlass>`r`n  <A>1</A>`r`n  <B>2</B>`r`n"
Check "broken file is refused" ($r.Error -is [System.FormatException]) "True"
Check "...and says where" ($r.Error.Message -match "[Ll]ine") "True"
Write-Host "      (the parser says: $($r.Error.Message))"

$r = Parse "<VolumetricClouds><A>1</A></VolumetricClouds>"
Check "another mod's file is refused" ($r.Error -is [System.FormatException]) "True"

$r = Parse ""
Check "an empty file is refused" ($r.Error -is [System.FormatException]) "True"

$r = Parse "<RealGlass><!-- a -- b --><A>1</A></RealGlass>"
Check "a '--' in a hand-written comment is refused, not half-read" ($r.Error -is [System.FormatException]) "True"

# ---- the assets file (RealGlassAssets.xml) ----------------------------------------------------
# Each building asset's own glass: written and read back exactly, ranges pulled in and said, a
# kind of glass by number or by name, nothing half-read from a broken file.

$recType = $asm.GetType('RealGlass.PerAsset.AssetRecord', $true)
$assetFmt = $asm.GetType('RealGlass.PerAsset.AssetFileFormat', $true)
$recList = [System.Collections.Generic.List``1].MakeGenericType($recType)

function NewRecord([string]$name) { return [Activator]::CreateInstance($recType, [object[]]@($name)) }

# Parse never throws: Error is null when the whole file was read, else why not (and Records what
# was salvaged). Thrown is anything that escaped it anyway -- always a failure.
function ParseAssets([string]$text) {
    $version = 0
    $errorText = $null
    $problems = New-Object 'System.Collections.Generic.List[string]'
    try {
        $records = $assetFmt::Parse($text, [ref]$version, $problems, [ref]$errorText)
        return @{ Records = @($records); Version = $version; Problems = $problems; Error = $errorText; Thrown = $null }
    }
    catch {
        return @{ Records = @(); Version = 0; Problems = $problems; Error = "thrown"; Thrown = $_.Exception }
    }
}

$green = [Activator]::CreateInstance($color32)
$green.r = 0x6E; $green.g = 0x9C; $green.b = 0x7C; $green.a = 255
$a = NewRecord "123456789.Glass Tower 42_Data"
$a.Title = 'Glass Tower <42> & "co"'
$a.Strength = [TestHelp]::Div(47, 100)
$a.PaintedDetail = [single]0
$a.Preset = 5
$a.Colour = $green
$a.Reflectivity = [TestHelp]::Div(24, 100)
$a.Clarity = [TestHelp]::Div(27, 100)
$a.OwnColour = [TestHelp]::Div(30, 100)
$a.Threshold = [TestHelp]::Div(15, 100)
$a.NightWindows = $false
$b = NewRecord "Tower B"
$b.Strength = [TestHelp]::Div(60, 100)
$empty = NewRecord "Follows everything"
$list = [Activator]::CreateInstance($recList)
$list.Add($b); $list.Add($empty); $list.Add($a)
$text = $assetFmt::Write($list, "Header line one`nHeader -- line two")

$r = ParseAssets $text
Check "the assets file parses back" ($r.Error -eq $null) "True"
if ($r.Records -ne $null) {
    Check "  an asset that follows everything is not written" $r.Records.Count 2
    Check "  sorted by name" (($r.Records | ForEach-Object { $_.Name }) -join " | ") "123456789.Glass Tower 42_Data | Tower B"
    $back = $r.Records[0]
    Check "  the title, escaped and back" $back.Title 'Glass Tower <42> & "co"'
    Check "  strength exactly" ($back.Strength -eq $a.Strength) "True"
    # PowerShell hands a Nullable over as its value, or $null.
    Check "  painted detail 0 is kept (not 'follows')" ("" + ($back.PaintedDetail -ne $null) + " " + $back.PaintedDetail) "True 0"
    Check "  kind of glass" $back.Preset 5
    Check "  colour" ($colorText::Format($back.Colour)) "#6E9C7C"
    Check "  reflectivity, clarity, own colour exactly" ("" + ($back.Reflectivity -eq $a.Reflectivity) + ($back.Clarity -eq $a.Clarity) + ($back.OwnColour -eq $a.OwnColour)) "TrueTrueTrue"
    Check "  shininess needed 15% (below the global row's 40)" ($back.Threshold -eq $a.Threshold) "True"
    Check "  lit windows switched OFF is kept (not 'follows')" ("" + ($back.NightWindows -ne $null) + " " + $back.NightWindows) "True False"
    Check "  what it does not set stays unset" ("" + ($r.Records[1].Preset -eq $null) + ($r.Records[1].Colour -eq $null) + ($r.Records[1].PaintedDetail -eq $null)) "TrueTrueTrue"
    Check "  no problems" ($r.Problems -join "; ") ""
    Check "  version" $r.Version 1
}
Check "  written as the sliders show it" ($text -match "<GlassStrength>47</GlassStrength>" -and $text -match "<GlassReflectivity>24</GlassReflectivity>" -and
                                         $text -match "<GlassThreshold>15</GlassThreshold>" -and $text -match "<GlassNightWindows>false</GlassNightWindows>") "True"

$r = ParseAssets "<RealGlassAssets><Asset name=`"T`"><GlassThreshold>5</GlassThreshold><GlassNightWindows>maybe</GlassNightWindows></Asset><Asset name=`"U`"><GlassThreshold>99</GlassThreshold><GlassNightWindows> ON </GlassNightWindows></Asset></RealGlassAssets>"
Check "shininess needed: pulled into 10..95" ("" + $r.Records[0].Threshold + " " + $r.Records[1].Threshold) "0.1 0.95"
Check "  lit windows: on/off spellings, nonsense refused and said" ("" + ($r.Records[0].NightWindows -eq $null) + " " + $r.Records[1].NightWindows + " " + $r.Problems.Count) "True True 3"
Check "  the root is <RealGlassAssets>" ($text -match "<RealGlassAssets version=`"1`">") "True"

# The rooms behind an asset's windows (the Interiors tab's switch, per asset): off is kept, on is
# kept, and an asset with nothing else of its own is still written.
$rooms = NewRecord "Rooms off"
$rooms.Interiors = $false
$roomsOn = NewRecord "Rooms on"
$roomsOn.Interiors = $true
$roomsList = [Activator]::CreateInstance($recList)
$roomsList.Add($rooms); $roomsList.Add($roomsOn)
$roomsText = $assetFmt::Write($roomsList, "")
$r = ParseAssets $roomsText
Check "rooms per asset: written under the global switch's name" ($roomsText -match "<InteriorsEnabled>false</InteriorsEnabled>" -and $roomsText -match "<InteriorsEnabled>true</InteriorsEnabled>") "True"
Check "  both read back, off and on (sorted by name)" ("" + $r.Records.Count + " " + $r.Records[0].Interiors + " " + $r.Records[1].Interiors) "2 False True"
Check "  an asset with only that is not empty, and has no glass of its own" ("" + $rooms.IsEmpty + " " + $rooms.HasGlass) "False False"

# The curtains in an asset's rooms (the Interiors tab's share, per asset: yes or no): off and on
# both kept, under a name of their own (InteriorsCurtains is the share, a percentage).
$noCurtains = NewRecord "Curtains off"
$noCurtains.Curtains = $false
$curtainsOn = NewRecord "Curtains on"
$curtainsOn.Curtains = $true
$curtainsList = [Activator]::CreateInstance($recList)
$curtainsList.Add($noCurtains); $curtainsList.Add($curtainsOn); $curtainsList.Add((NewRecord "Curtains follow"))
$curtainsText = $assetFmt::Write($curtainsList, "")
$r = ParseAssets $curtainsText
Check "curtains per asset: written as true or false, never under the share's name" ($curtainsText -match "<InteriorsCurtainsEnabled>false</InteriorsCurtainsEnabled>" -and $curtainsText -match "<InteriorsCurtainsEnabled>true</InteriorsCurtainsEnabled>" -and $curtainsText -notmatch "<InteriorsCurtains>") "True"
Check "  both read back, the following one not written (sorted by name)" ("" + $r.Records.Count + " " + $r.Records[0].Name + "=" + $r.Records[0].Curtains + " " + $r.Records[1].Name + "=" + $r.Records[1].Curtains) "2 Curtains off=False Curtains on=True"
Check "  an asset with only that is not empty, needs no glass slot, and differs from one without" ("" + $noCurtains.IsEmpty + " " + $noCurtains.HasGlass + " " + $noCurtains.SameSettings((NewRecord "Curtains off"))) "False False False"
$r = ParseAssets "<RealGlassAssets><Asset name=`"Q`"><InteriorsCurtainsEnabled>few</InteriorsCurtainsEnabled></Asset></RealGlassAssets>"
Check "  nonsense refused and said" ("" + ($r.Records[0].Curtains -eq $null) + " " + $r.Problems.Count) "True 1"

# Its rooms lit by its illumination map (the Interiors tab's switch, per asset): yes and no both
# kept under the global row's name, in the players' file AND the creators' (RealGlassDefinition.xml).
$mapOn = NewRecord "Map on"
$mapOn.FollowIllumination = $true
$mapOff = NewRecord "Map off"
$mapOff.FollowIllumination = $false
$mapList = [Activator]::CreateInstance($recList)
$mapList.Add($mapOn); $mapList.Add($mapOff); $mapList.Add((NewRecord "Map follow"))
$mapText = $assetFmt::Write($mapList, "")
$r = ParseAssets $mapText
Check "illumination map per asset: written as true or false under the global row's name" ($mapText -match "<InteriorsFollowIllumination>true</InteriorsFollowIllumination>" -and $mapText -match "<InteriorsFollowIllumination>false</InteriorsFollowIllumination>") "True"
Check "  both read back, the following one not written (sorted by name)" ("" + $r.Records.Count + " " + $r.Records[0].Name + "=" + $r.Records[0].FollowIllumination + " " + $r.Records[1].Name + "=" + $r.Records[1].FollowIllumination) "2 Map off=False Map on=True"
Check "  an asset with only that is not empty, needs no glass slot, and differs from one without" ("" + $mapOn.IsEmpty + " " + $mapOn.HasGlass + " " + $mapOn.SameSettings((NewRecord "Map on"))) "False False False"
# The creators' file as the asset editor writes it on Save (MergeDefinition) and a city reads it.
$defProblems = New-Object 'System.Collections.Generic.List[string]'
$defError = $null
$defText = $assetFmt::MergeDefinition($null, "Map Tower", $mapOn, "", $defProblems, [ref]$defError)
$defVersion = 0
$defRead = $assetFmt::Parse($defText, $assetFmt::DefinitionRoot, [ref]$defVersion, $defProblems, [ref]$defError)
Check "  the creators' file (Save in the asset editor): written under its root, read back" ("" + ($defText -match ("<" + $assetFmt::DefinitionRoot + " ")) + " " + ($defText -match "<InteriorsFollowIllumination>true</InteriorsFollowIllumination>") + " " + $defRead.Count + " " + $defRead[0].FollowIllumination + " " + $defProblems.Count) "True True 1 True 0"
$r = ParseAssets "<RealGlassAssets><Asset name=`"Q`"><InteriorsFollowIllumination>maybe</InteriorsFollowIllumination></Asset></RealGlassAssets>"
Check "  nonsense refused and said" ("" + ($r.Records[0].FollowIllumination -eq $null) + " " + $r.Problems.Count) "True 1"

# Its LODs' glass (the Glass tab's "LODs show the model's glass", per asset): the model's (true)
# and the LOD's own (false) both kept; unset is automatic and not written.
$lodModel = NewRecord "LOD model"
$lodModel.LodGlass = $true
$lodOwn = NewRecord "LOD own"
$lodOwn.LodGlass = $false
$lodList = [Activator]::CreateInstance($recList)
$lodList.Add($lodModel); $lodList.Add($lodOwn); $lodList.Add((NewRecord "LOD auto"))
$lodText = $assetFmt::Write($lodList, "")
$r = ParseAssets $lodText
Check "LOD glass per asset: written under the global switch's name" ($lodText -match "<LodGlassFromModel>true</LodGlassFromModel>" -and $lodText -match "<LodGlassFromModel>false</LodGlassFromModel>") "True"
Check "  both read back, the automatic one not written (sorted by name)" ("" + $r.Records.Count + " " + $r.Records[0].Name + "=" + $r.Records[0].LodGlass + " " + $r.Records[1].Name + "=" + $r.Records[1].LodGlass) "2 LOD model=True LOD own=False"
Check "  an asset with only that is not empty, needs no glass slot, and differs from one without" ("" + $lodOwn.IsEmpty + " " + $lodOwn.HasGlass + " " + $lodOwn.SameSettings((NewRecord "LOD own"))) "False False False"
$r = ParseAssets "<RealGlassAssets><Asset name=`"Q`"><LodGlassFromModel>sometimes</LodGlassFromModel></Asset></RealGlassAssets>"
Check "  nonsense refused and said" ("" + ($r.Records[0].LodGlass -eq $null) + " " + $r.Problems.Count) "True 1"

$r = ParseAssets ("<RealGlassAssets version=`"1`"><Asset name=`"X`"><GlassStrength>150</GlassStrength><GlassReflectivity>70%</GlassReflectivity>" +
                  "<GlassClarity>abc</GlassClarity><GlassPreset>Silver reflective</GlassPreset><Foo>1</Foo></Asset>" +
                  "<Thing/><Asset><GlassStrength>5</GlassStrength></Asset></RealGlassAssets>")
$x = $r.Records[0]
Check "out of range: pulled in" ("" + $x.Strength + " " + $x.Reflectivity) "1 0.6"
Check "  a value that cannot be read is left out" ($x.Clarity -eq $null) "True"
Check "  a kind of glass by its English name" $x.Preset 8
Check "  six problems said (two ranges, a number, an unknown name, an unknown element, a nameless asset)" $r.Problems.Count 6
Write-Host "      (the problems: $($r.Problems -join ' / '))"

$preset = 0
Check "kind of glass: Custom by name" ($assetFmt::TryPreset("custom", [ref]$preset)) "True"
Check "kind of glass: an unknown number refused" ($assetFmt::TryPreset("42", [ref]$preset)) "False"
Check "kind of glass: every number of a preset" ((1..10 | Where-Object { $assetFmt::TryPreset("$_", [ref]$preset) }).Count) 10

$r = ParseAssets "<RealGlassAssets><Asset name=`"A`"><GlassStrength>10</GlassStrength></Asset><Asset name=`"A`"><GlassStrength>20</GlassStrength></Asset></RealGlassAssets>"
Check "an asset twice: the last one" ("" + $r.Records.Count + " " + $r.Records[0].Strength) "1 0.2"
Check "  and it is said" ($r.Problems -join "") "A: written twice, the last one taken"

# ---- fool-proofing: a hand-edited file never throws, and loses only what cannot be read --------

$good = "<RealGlassAssets version=`"1`">`r`n  <Asset name=`"A`">`r`n    <GlassStrength>10</GlassStrength>`r`n  </Asset>`r`n" +
        "  <Asset name=`"B`">`r`n    <GlassStrength>20</GlassStrength>`r`n  </Asset>`r`n" +
        "  <Asset name=`"C`">`r`n    <GlassStrength>30</GlassStrength>`r`n  </Asset>`r`n</RealGlassAssets>`r`n"

$r = ParseAssets ($good.Substring(0, $good.IndexOf("</Asset>", $good.IndexOf("name=`"B`""))))
Check "cut short: said, not thrown" ("" + ($r.Thrown -eq $null) + " " + ($r.Error -ne $null)) "True True"
Check "  every asset before the cut, and the cut one" (($r.Records | ForEach-Object { $_.Name + "=" + $_.Strength }) -join " ") "A=0.1 B=0.2"

$r = ParseAssets ($good.Replace("<Asset name=`"B`">", "<Asset name=`"B>"))
Check "a broken asset in the middle: the others read" (($r.Records | ForEach-Object { $_.Name }) -join " ") "A C"
Check "  and which one, by its line" (@($r.Problems | Where-Object { $_ -match "line 5" }).Count) 1

$r = ParseAssets ($good.Replace("</GlassStrength>`r`n  </Asset>`r`n  <Asset name=`"C`">", "</GlassStrength>`r`n  <Asset name=`"C`">"))
Check "an asset without its end tag: still read" (($r.Records | ForEach-Object { $_.Name + "=" + $_.Strength }) -join " ") "A=0.1 B=0.2 C=0.3"

$r = ParseAssets "<RealGlassAssets><Asset name=`"A`">"
Check "a file cut inside its first asset: that asset, nothing thrown" ("" + ($r.Thrown -eq $null) + " " + $r.Records.Count + " " + ($r.Error -ne $null)) "True 1 True"
$r = ParseAssets "<RealGlass><Asset name=`"A`"><GlassStrength>5</GlassStrength></Asset></RealGlass>"
Check "another root: said, its assets still taken" ("" + ($r.Error -ne $null) + " " + $r.Records.Count) "True 1"
$r = ParseAssets "<RealGlass><GlassStrength>5</GlassStrength></RealGlass>"
Check "the settings file pasted here: nothing taken" ("" + ($r.Error -ne $null) + " " + $r.Records.Count) "True 0"
$r = ParseAssets "glass, please"
Check "not XML at all: said, nothing taken, nothing thrown" ("" + ($r.Thrown -eq $null) + " " + ($r.Error -ne $null) + " " + $r.Records.Count) "True True 0"
$r = ParseAssets ""
Check "an empty file: read in full, no assets" ("" + ($r.Error -eq $null) + " " + $r.Records.Count) "True 0"
$r = ParseAssets "  `r`n  "
Check "a blank file: the same" ("" + ($r.Error -eq $null) + " " + $r.Records.Count) "True 0"
$r = ParseAssets ([string]$null)
Check "no text at all: the same" ("" + ($r.Thrown -eq $null) + " " + $r.Records.Count) "True 0"
$bomb = "<!DOCTYPE r [<!ENTITY a `"aaaaaaaaaa`"><!ENTITY b `"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;`">]>" +
        "<RealGlassAssets><Asset name=`"&b;`"><GlassStrength>5</GlassStrength></Asset></RealGlassAssets>"
$r = ParseAssets $bomb
Check "a DTD is refused, its entities never expanded" ("" + ($r.Thrown -eq $null) + " " + ($r.Error -ne $null) + " " + (@($r.Records | Where-Object { $_.Name -match "aaaa" }).Count)) "True True 0"
$r = ParseAssets "<RealGlassAssets><Asset name=`"A`"><GlassStrength>5</GlassStrength></Asset><!-- a -- b --></RealGlassAssets>"
Check "a bad comment: the asset before it still read" (($r.Records | ForEach-Object { $_.Name }) -join " ") "A"
Check "a kind of glass written 5.0" ($assetFmt::TryPreset("5.0", [ref]$preset)) "True"
Check "  but not 5.5" ($assetFmt::TryPreset("5.5", [ref]$preset)) "False"

# Every cut of a real file, and a thousand random corruptions of it: never an exception.
$full = $assetFmt::Write($list, "Header line one`nHeader -- line two")
$thrown = 0; $read = 0
for ($n = 0; $n -le $full.Length; $n += 3) {
    $r = ParseAssets $full.Substring(0, $n)
    if ($r.Thrown -ne $null) { $thrown++; if ($thrown -le 3) { Write-Host "   cut at $n threw $($r.Thrown.GetType().Name): $($r.Thrown.Message)" } }
    $read += $r.Records.Count
}
Check "every cut of a written file ($([int]($full.Length / 3) + 1) cuts): nothing thrown" $thrown 0
$rand = New-Object System.Random 20261001
$junk = '<', '>', '/', '"', '&', '=', ' ', 'x', '!', '-', [char]0, [char]0x1F, [char]0xD800, 'Asset', '</Asset>', '<Asset name="Z">', ']]>', '<![CDATA[', '&amp;', '&#0;'
$thrown = 0
for ($k = 0; $k -lt 1000; $k++) {
    $chars = New-Object System.Text.StringBuilder $full
    for ($m = 0; $m -lt 1 + $rand.Next(6); $m++) {
        $at = $rand.Next($chars.Length)
        if ($rand.Next(3) -eq 0) { [void]$chars.Remove($at, [Math]::Min($rand.Next(1, 40), $chars.Length - $at)) }
        else { [void]$chars.Insert($at, [string]$junk[$rand.Next($junk.Count)]) }
    }
    $r = ParseAssets $chars.ToString()
    if ($r.Thrown -ne $null) { $thrown++; if ($thrown -le 3) { Write-Host "   corruption $k threw $($r.Thrown.GetType().Name): $($r.Thrown.Message)" } }
}
Check "a thousand random corruptions: nothing thrown" $thrown 0

# What the mod writes, it reads back: a title with characters XML forbids is cleaned, not written.
$odd = NewRecord "Odd"
$odd.Title = "Tower" + [char]1 + [char]0x1F + " B" + [char]0xD800 + "`t2"
$odd.Strength = [TestHelp]::Div(50, 100)
$oddList = [Activator]::CreateInstance($recList)
$oddList.Add($odd)
$r = ParseAssets ($assetFmt::Write($oddList, ""))
Check "a title with control characters: written so it reads back" ("" + ($r.Error -eq $null) + " '" + $r.Records[0].Title + "'") "True 'Tower B 2'"

# "Give this asset the Glass tab's settings again": nothing of its own is left, so its entry goes.
$fileType = $asm.GetType('RealGlass.PerAsset.AssetFile', $true)
$kept = NewRecord "Reset me"
$kept.Strength = [TestHelp]::Div(40, 100)
$fileType::Put($kept)
$before = $fileType::Count
$fileType::Put((NewRecord "Reset me"))
$afterList = [Activator]::CreateInstance($recList)
foreach ($rec in $fileType::All) { $afterList.Add($rec) }
Check "back to the global settings: the asset's entry is gone" ("" + $before + " " + $fileType::Count + " " + ($fileType::Get("Reset me") -eq $null) + " " + ($assetFmt::Write($afterList, "") -match "Reset me")) "1 0 True False"

$r = ParseAssets "<RealGlass><Asset name=`"A`"/></RealGlass>"
Check "the settings file is not an assets file" ($r.Error -ne $null) "True"

# ---- a creator's glass (RealGlassDefinition.xml, PerAsset\CreatorFile.cs) ---------------------
# Shipped beside an asset's .crp and named by the asset's OWN name: the game saves the package again
# under its Workshop id on upload, so "Tower.Tower_Data" in the creator's game is
# "1407415712.Tower_Data" in a player's. Beneath the player's own glass for the asset (Alt+G).

Check "own name: the package before the first dot, _Data off" ($assetFmt::OwnName("1407415712.Tour Incity - Lyon_Data")) "Tour Incity - Lyon"
Check "  a dot in the asset's own name stays" ($assetFmt::OwnName("Tower.Tower 2.0_Data")) "Tower 2.0"
Check "  the game's own assets (no package) have none" ($assetFmt::OwnName("H1 2x2 Sweatshop06") -eq $null) "True"
Check "the same asset: Tower_Data and Tower, letter case aside; not Tower 2" ("" + $assetFmt::SameAsset("Tower_Data", "Tower") + $assetFmt::SameAsset("tower", "Tower") + $assetFmt::SameAsset("Tower 2", "Tower")) "TrueTrueFalse"

$defs = [Activator]::CreateInstance($recList)
$d1 = NewRecord "Tour Incity - Lyon"
$d1.Reflectivity = [TestHelp]::Div(30, 100)
$d2 = NewRecord "1407415712.Other_Data"
$d2.Strength = [TestHelp]::Div(50, 100)
$defs.Add($d2); $defs.Add($d1)
Check "found by its own name in a player's city (the Workshop id) and the creator's (the save name)" (($assetFmt::FindDefinition($defs, "1407415712.Tour Incity - Lyon_Data")).Name + " | " + ($assetFmt::FindDefinition($defs, "Tour Incity.Tour Incity - Lyon_Data")).Name) "Tour Incity - Lyon | Tour Incity - Lyon"
Check "  by the full name too" ($assetFmt::FindDefinition($defs, "1407415712.Other_Data")).Name "1407415712.Other_Data"
Check "  none for another asset, none from no file" ("" + ($assetFmt::FindDefinition($defs, "1407415712.Third_Data") -eq $null) + ($assetFmt::FindDefinition($null, "1.A_Data") -eq $null)) "TrueTrue"

function MergeDef([string]$existing, [string]$name, $record) {
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $err = $null
    $text = $assetFmt::MergeDefinition($existing, $name, $record, "Header", $problems, [ref]$err)
    $version = 0; $err2 = $null
    $back = $assetFmt::Parse($text, $assetFmt::DefinitionRoot, [ref]$version, $problems, [ref]$err2)
    return @{ Text = $text; Error = $err; Back = @($back); BackError = $err2 }
}
$c1 = NewRecord "anything"
$c1.Colour = $green
$c1.Threshold = [TestHelp]::Div(80, 100)
$m = MergeDef $null "Tower_Data" $c1
Check "a new creators' file: the asset under its own name, under its own root" ("" + ($m.Text -match '<RealGlassDefinition version="1">') + ($m.Text -match '<Asset name="Tower">') + ($m.Text -match '<GlassColor>#6E9C7C</GlassColor>') + ($m.BackError -eq $null)) "TrueTrueTrueTrue"
$c2 = NewRecord "anything"
$c2.Strength = [TestHelp]::Div(50, 100)
$m = MergeDef $m.Text "Annex" $c2
Check "  another asset saved into the same folder: both kept" (($m.Back | ForEach-Object { $_.Name }) -join " | ") "Annex | Tower"
$m = MergeDef $m.Text "tower" $c2
$tower = $m.Back | Where-Object { $_.Name -eq "tower" }
Check "  saved again: replaced (letter case aside), the other kept" ("" + $m.Back.Count + " " + $tower.Strength + " " + ($tower.Colour -eq $null)) "2 0.5 True"
$m = MergeDef $m.Text "Tower" (NewRecord "anything")
Check "  saved with nothing of its own: its entry taken out" (($m.Back | ForEach-Object { $_.Name }) -join " | ") "Annex"
$m = MergeDef "<RealGlassDefinition><Asset name=`"A`"><GlassStrength>5</GlassStrength></Asset><Asset name=`"B><GlassStrength>6</GlassStrength></Asset></RealGlassDefinition>" "C" $c2
Check "  a broken file: said (kept aside by the caller), what it holds salvaged" ("" + ($m.Error -ne $null) + " " + (($m.Back | ForEach-Object { $_.Name }) -join ",")) "True A,C"
Check "the creators' file is not the player's" ((ParseAssets (MergeDef $null "Tower" $c1).Text).Error -ne $null) "True"

# The player's glass for an asset over its creator's (PerAsset\AssetRecord.Over).
$top = NewRecord "T"
$top.Preset = 8
$under = NewRecord "T"
$under.Colour = $green
$under.Strength = [TestHelp]::Div(60, 100)
$under.Interiors = $false
$o = $recType::Over($top, $under)
Check "player over creator: the player's kind of glass brings its own color, not the creator's" ("" + ($colorText::Format($o.Colour) -ne "#6E9C7C") + " " + $o.Preset) "True 8"
Check "  the creator's other settings stay beneath" ("" + $o.Strength + " " + $o.Interiors) "0.6 False"
$under.Curtains = $false
$top.Curtains = $true
$o = $recType::Over($top, $under)
$top.Curtains = $null
$o2 = $recType::Over($top, $under)
Check "  curtains: the player's yes over the creator's no; the creator's no where the player says nothing" ("" + $o.Curtains + " " + $o2.Curtains) "True False"
$under.FollowIllumination = $true
$top.FollowIllumination = $false
$o = $recType::Over($top, $under)
$top.FollowIllumination = $null
$o2 = $recType::Over($top, $under)
Check "  illumination map: the player's no over the creator's yes; the creator's yes where the player says nothing" ("" + $o.FollowIllumination + " " + $o2.FollowIllumination) "False True"
$top = NewRecord "T"
$top.Reflectivity = [TestHelp]::Div(10, 100)
$under = NewRecord "T"
$under.Preset = 5
$o = $recType::Over($top, $under)
Check "  a number of the player's over the creator's kind: Custom, the kind's other numbers kept" ("" + $o.Preset + " " + $o.Reflectivity + " " + ($o.Colour -ne $null) + " " + ($o.Clarity -ne $null)) "0 0.1 True True"
Check "  one side missing or empty: the other, as it is" ("" + [object]::ReferenceEquals($recType::Over($null, $under), $under) + [object]::ReferenceEquals($recType::Over($top, (NewRecord "T")), $top)) "TrueTrue"

# ---- the renderer's own copy of the defaults ------------------------------------------------
# GlassParams.Default() (Glass\GlassParams.cs) is what the GPU preview draws with; it must be
# what the mod ships (Settings.Defaults), or the preview's pictures are of another look.

$paramsType = $asm.GetType('RealGlass.Glass.GlassParams', $true)
$gp = $paramsType.GetMethod('Default').Invoke($null, @())
$pairs = @(
    @('Enabled', 'GlassEnabled'), @('OwnColour', 'GlassOwnColour'), @('PaintedDetail', 'GlassPaintedDetail'), @('Reflectivity', 'GlassReflectivity'),
    @('Glancing', 'GlassGlancing'), @('Clarity', 'GlassClarity'), @('Strength', 'GlassStrength'),
    @('Threshold', 'GlassThreshold'), @('NightWindows', 'GlassNightWindows'), @('LodGlass', 'LodGlassFromModel'), @('ShowMask', 'GlassShowMask'),
    @('Trace', 'SsrEnabled'), @('Quality', 'SsrQuality'), @('ReflectionBrightness', 'ReflectionBrightness'),
    @('SkySource', 'SkyReflection'), @('OffscreenFill', 'OffscreenFill'), @('SunGlint', 'SunGlint'),
    @('Smoothness', 'GlassSmoothness'), @('EdgeFade', 'SsrEdgeFade'), @('PaneWidth', 'PaneWidth'),
    @('FloorHeight', 'FloorHeight'), @('Pillow', 'PanePillow'), @('Tilt', 'PaneTilt'), @('RollerWave', 'RollerWave'),
    @('PaneWeather', 'PaneWeather'), @('EdgeSilver', 'GlassEdgeSilver'), @('Finished', 'ReflectFinished'), @('SkyClouds', 'SkyClouds'), @('SkyDusk', 'SkyDusk'),
    @('Neighbours', 'ReflectNeighbours'), @('SteadyGlints', 'SteadyGlints'), @('PanesFollowWindows', 'PanesFollowWindows'),
    @('Interiors', 'InteriorsEnabled'), @('InteriorsByDay', 'InteriorsByDay'), @('InteriorsDayOpacity', 'InteriorsDayOpacity'), @('InteriorsDepth', 'InteriorsDepth'), @('InteriorsMatchLods', 'InteriorsMatchLods'),
    @('InteriorsFollowIllumination', 'InteriorsFollowIllumination'), @('InteriorsLit', 'InteriorsLit'),
    @('InteriorsLateNight', 'InteriorsLateNight'), @('InteriorsSwitching', 'InteriorsSwitching'),
    @('InteriorsBrightness', 'InteriorsBrightness'), @('InteriorsColoured', 'InteriorsColoured'), @('InteriorsCurtains', 'InteriorsCurtains'))
$mismatch = @()
foreach ($pair in $pairs) {
    $mine = $paramsType.GetField($pair[0]).GetValue($gp)
    $theirs = Default $pair[1]
    if ("$mine" -ne "$theirs") { $mismatch += "$($pair[0]) $mine vs $($pair[1]) $theirs" }
}
Check "GlassParams.Default() = Settings.Defaults, row by row" ($mismatch -join "; ") ""
$c = Default 'GlassColor'
$tint = "{0:F4} {1:F4} {2:F4}" -f $paramsType.GetField('TintR').GetValue($gp), $paramsType.GetField('TintG').GetValue($gp), $paramsType.GetField('TintB').GetValue($gp)
Check "  and the glass colour" $tint ("{0:F4} {1:F4} {2:F4}" -f ($c.r / 255.0), ($c.g / 255.0), ($c.b / 255.0))

# ---- the sky in the glass at dusk (Glass\GlassUniforms.cs) ----------------------------------
# The clear sky's light in the sun's last ten degrees (SPCTRAL2) over the game's own sky's
# (DayTime: noon's above 3.16 degrees), never above 1; held at the horizon's below it.
$uniforms = $asm.GetType('RealGlass.Glass.GlassUniforms', $true)
function SinDeg([double]$d) { return [single][Math]::Sin($d * [Math]::PI / 180.0) }
Check "dusk: the clear sky's table is 1 at 10 degrees, half at 5" ("{0:F4} {1:F4}" -f $uniforms::ClearSky(10.0), $uniforms::ClearSky(5.0)) "1.0000 0.5036"
Check "  the game's sky: full above 3.16 degrees, 0.74 at the horizon, none at -12" ("{0:F3} {1:F3} {2:F3}" -f $uniforms::GameSkyDay((SinDeg 3.5)), $uniforms::GameSkyDay([single]0), $uniforms::GameSkyDay((SinDeg -12))) "1.000 0.739 0.000"
Check "  the sky in the glass untouched with the sun 10 degrees up and more" ("{0:F4} {1:F4}" -f $uniforms::Dusk((SinDeg 10), [single]1), $uniforms::Dusk((SinDeg 40), [single]1)) "1.0000 1.0000"
$at5 = $uniforms::Dusk((SinDeg 5), [single]1)
Check "  5 degrees: the clear sky's half; at 50% its square root" ("{0:F4} {1:F4}" -f $at5, $uniforms::Dusk((SinDeg 5), [single]0.5)) ("0.5036 {0:F4}" -f [Math]::Sqrt($at5))
Check "  at the horizon: the clear sky's over the game's own fade" ("{0:F4}" -f $uniforms::Dusk([single]0, [single]1)) ("{0:F4}" -f (0.0206 / $uniforms::GameSkyDay([single]0)))
Check "  0%: the game's sky as it is, at any height" ("{0:F4} {1:F4}" -f $uniforms::Dusk([single]0, [single]0), $uniforms::Dusk((SinDeg 3), [single]0)) "1.0000 1.0000"
Check "  night: the game's own night sky, untouched" ("{0:F4}" -f $uniforms::Dusk((SinDeg -12), [single]1)) "1.0000"
$rises = @()
foreach ($strength in @(1.0, 0.5, 0.25)) {
    $last = 2.0
    for ($e = 15.0; $e -ge -14.0; $e -= 0.25) {
        $y = SinDeg $e
        $glassSky = $uniforms::GameSkyDay($y) * $uniforms::Dusk($y, [single]$strength)
        if ($glassSky -gt $last + 1e-6) { $rises += "$strength at $e" }
        $last = $glassSky
    }
}
Check "  the sky in the glass only darkens as the sun goes down (100%, 50%, 25%)" ($rises -join "; ") ""

# ---- the rooms behind the glass (Glass\Interiors.cs) ----------------------------------------
# Which rooms are lit at an hour, how long a room keeps its light, and a building's texel.
$interiors = $asm.GetType('RealGlass.Glass.Interiors', $true)
Check "rooms: a texel is kind + 8 x the asset's switch (an office, off: 9)" ($interiors::Texel($interiors::KindOffice, $interiors::AssetOff)) 9
Check "  following the global switch: the kind alone" ($interiors::Texel($interiors::KindShopHigh, $interiors::FollowGlobal)) 7
Check "  + 24 x the asset's curtains (an office, rooms on, no curtains: 41)" ($interiors::Texel($interiors::KindOffice, $interiors::AssetOn, $interiors::AssetOff)) 41
Check "  + 72 x its illumination map (an office, rooms following, curtains following, the map on: 145)" ($interiors::Texel($interiors::KindOffice, $interiors::FollowGlobal, $interiors::FollowGlobal, $interiors::AssetOn)) 145
Check "  the most it holds stays under 255 (not looked at yet); a kind past the last is the last" ("" + $interiors::Texel(15, $interiors::AssetOn, $interiors::AssetOn, $interiors::AssetOn) + " " + $interiors::Texel($interiors::KindShopHigh, $interiors::AssetOn, $interiors::AssetOn, $interiors::AssetOn)) "215 215"
# What Interiors.cginc's RoomKind does with a texel, in the same arithmetic (floor from halfway).
$decode = { param($a8) $s = [Math]::Floor($a8 / 8); $u = [Math]::Floor(($s + 0.5) / 3); $t = [Math]::Floor(($u + 0.5) / 3); "" + ($a8 % 8) + " " + ($s - 3 * $u) + " " + ($u - 3 * $t) + " " + $t }
$every = @()
foreach ($k in 0..7) { foreach ($r in 0..2) { foreach ($c in 0..2) { foreach ($m in 0..2) {
    $got = & $decode ([int]$interiors::Texel($k, $r, $c, $m))
    if ($got -ne "$k $r $c $m") { $every += "$k $r $c $m -> $got" }
} } } }
Check "  every kind and switches decode back as written (the shader's arithmetic)" ($every -join "; ") ""
Check "  a room keeps its light 4 game hours at 100%, 2 at 200%, for good at 0%" ("" + $interiors::HoursPerState(1.0) + " " + $interiors::HoursPerState(2.0) + " " + $interiors::HoursPerState(0.0)) "4 2 0"
$gpRooms = $paramsType.GetMethod('Default').Invoke($null, @())
$boxedRooms = [object]$gpRooms
$paramsType.GetField('InteriorsLit').SetValue($boxedRooms, [single]0.5)
$evening = $interiors::Shares($boxedRooms, 21.0)
$small = $interiors::Shares($boxedRooms, 3.0)
Check "  homes at 21:00: the slider's share" ("{0:F3}" -f $evening.x) "0.500"
Check "  homes at 03:00: far fewer" ($small.x -lt $evening.x * 0.3) "True"
Check "  offices at 03:00: some left on" ($small.y -gt 0.05) "True"
Check "  shops shut at night, open by day" ("" + ($small.z -lt 0.1) + " " + (($interiors::Shares($boxedRooms, 12.0)).z -gt 0.45)) "True True"
$paramsType.GetField('InteriorsLateNight').SetValue($boxedRooms, $false)
$flat = $interiors::Shares($boxedRooms, 3.0)
Check "  not following the hour: the slider's share all night" ("{0:F2} {1:F2} {2:F2} {3:F2}" -f $flat.x, $flat.y, $flat.z, $flat.w) "0.50 0.50 0.50 0.50"
$paramsType.GetField('InteriorsLit').SetValue($boxedRooms, [single]1.0)
Check "  never every room: at most 95%" ("{0:F2}" -f ($interiors::Shares($boxedRooms, 21.0)).x) "0.95"
$curve = $interiors.GetField('Homes', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
Check "  the curve is continuous at midnight" ("{0:F3}" -f $interiors::Curve($curve, 23.999)) ("{0:F3}" -f $interiors::Curve($curve, 0.0))

# ---- the water (Water\WaterOptics.cs) ------------------------------------------------------
# The rough sea's reflectance table both water shaders read (_RGSeaTable), and the constants the
# Water tab's controller hands them.

$optics = $asm.GetType('RealGlass.Water.WaterOptics', $true)
$rho = $optics::BuildTable($optics::SlopeVariance(5.0))
Check "the table has 33 entries" $rho.Length 33
Check "F0 of water is 0.0204" ("{0:F4}" -f $optics::F0) "0.0204"
$down = $rho[32]
Check "looking straight down: about 2% (F0, a little more on a rough sea)" ($down -gt 0.020 -and $down -lt 0.03) "True"
Check "at the horizon: much more, under a smooth surface's 100% (waves)" ($rho[0] -gt 0.2 -and $rho[0] -lt 0.9) "True"
$bad = 0
for ($i = 1; $i -lt 33; $i++) { if ($rho[$i] -gt $rho[$i - 1] + 1e-9) { $bad++ } }
Check "less reflection the steeper one looks, all the way" $bad 0
Check "the lookup at the entries is the table" ("{0:F6}" -f $optics::Lookup($rho, 0.25)) ("{0:F6}" -f $rho[16])
Check "the lookup clamps below the level" ("{0:F6}" -f $optics::Lookup($rho, -1)) ("{0:F6}" -f $rho[0])
Write-Host ("   rho: {0:P1} looking down, {1:P1} at 1/64, {2:P1} at the horizon" -f $down, $rho[4], $rho[0])
Check "the cube's gain divides out the game's 0.5" $optics::CubeGain(1.0) 2
Check "  and a negative slider is none" $optics::CubeGain(-1.0) 0
Check "daylight: night below -0.10" $optics::Daylight(-0.2) 0
Check "  day above 0.10" $optics::Daylight(0.2) 1
Check "  half at the horizon" $optics::Daylight(0.0) 0.5
Check "streaks 100%: the spread over the view (fov 90: 0.05 uv)" ("{0:F6}" -f $optics::StreakSigma(1.0, 0.1, 90.0)) ("{0:F6}" -f 0.05)
Check "  a negative slider is none" $optics::StreakSigma(-1.0, 0.1, 90.0) 0
Check "the city's streaks: 2 x the slope in one plane" ("{0:F6}" -f $optics::Spread(0.02)) ("{0:F6}" -f 0.2)

# The ripples (WaterOptics.Ripples): 32 waves with whole wave numbers across the texture (it tiles),
# 16 m to 0.5 m before rounding, round the heading, deep water's speeds for their true lengths in
# whole cycles an hour, every wave vector its own, the slopes' mean square exactly 1.
$waves = $optics::Ripples()
$n = $optics::RippleCount; $stride = $optics::WaveStride; $tile = $optics::RippleTile
Check "the ripples: 32 of them, 8 numbers each" $waves.Length ($n * $stride)
$meanSquare = 0.0; $badWhole = 0; $badLength = 0; $badDir = 0; $badSpeed = 0; $badPhase = 0; $badUnit = 0
$seen = @{}; $dupes = 0
$ratio = [Math]::Pow(0.5 / 16.0, 1.0 / 31.0)
for ($i = 0; $i -lt $n; $i++) {
    $at = $i * $stride
    $m = $waves[$at]; $nn = $waves[$at + 1]; $perHour = $waves[$at + 2]; $s = $waves[$at + 3]; $phase = $waves[$at + 4]
    $dx = $waves[$at + 5]; $dz = $waves[$at + 6]
    $meanSquare += $s * $s / 2.0
    if ($m -ne [Math]::Round($m) -or $nn -ne [Math]::Round($nn) -or $perHour -ne [Math]::Round($perHour)) { $badWhole++ }
    $key = "$m,$nn"; if ($seen.ContainsKey($key)) { $dupes++ } else { $seen[$key] = 1 }
    $across = [Math]::Sqrt($m * $m + $nn * $nn)
    $len = $tile / $across
    $want = 16.0 * [Math]::Pow($ratio, $i)
    # Rounding the wave numbers moves a length a little (the longest most: 10 cycles across).
    if ([Math]::Abs($len / $want - 1.0) -gt 0.08) { $badLength++ }
    $heading = [Math]::Atan2($nn, $m) * 180.0 / [Math]::PI
    if ([Math]::Abs($heading - 30.0) -gt 75.0) { $badDir++ }
    if ([Math]::Abs($dx - $m / $across) -gt 1e-12 -or [Math]::Abs($dz - $nn / $across) -gt 1e-12) { $badUnit++ }
    $omega = [Math]::Sqrt(9.81 * 2.0 * [Math]::PI * $across / $tile)
    if ([Math]::Abs($perHour * 2.0 * [Math]::PI / 3600.0 / $omega - 1.0) -gt 0.001) { $badSpeed++ }
    if ($phase -lt 0.0 -or $phase -ge 1.0) { $badPhase++ }
}
Check "  whole wave numbers and whole cycles an hour (it tiles; the hour wraps without a jump)" $badWhole 0
Check "  every wave vector its own" $dupes 0
Check "  lengths 16 m to 0.5 m (within 8% after rounding)" $badLength 0
Check "  every direction near the heading's 30 +- 70 deg (rounding moves it a little)" $badDir 0
Check "  directions are the wave vectors, unit" $badUnit 0
Check "  deep water's speed (omega^2 = g k) for the true length, to 0.1%" $badSpeed 0
Check "  phases in cycles, 0..1" $badPhase 0
Check "  the slopes' mean square is exactly 1" ("{0:F8}" -f $meanSquare) ("{0:F8}" -f 1.0)
Check "  the shortest is 3 texels or more" (($tile / $optics::RippleTexels) * 3.0 -le 0.5 + 1e-9) "True"
Check "the game's texture decoded x 2 - 1, halved: rms 0.4 is a slope of 0.2; at 150% 0.3" ("{0:F4} {1:F4}" -f $optics::RippleSlope(0.4, 1.0), $optics::RippleSlope(0.4, 1.5)) "0.2000 0.3000"
Check "the city reflection's gain: 100% bends by WaveSlope, whatever the texture" ("{0:F4}" -f ($optics::CityRippleGain(1.0, 0.4) * $optics::RippleSlope(0.4, 1.0))) ("{0:F4}" -f $optics::WaveSlope)
Check "  0% is the flat mirror; no texture measured is none" ("{0} {1}" -f $optics::CityRippleGain(0.0, 0.4), $optics::CityRippleGain(1.0, 0.0)) "0 0"
$mean = 0.0; $rms = 0.0
$optics::DecodedStats([byte[]]@(255, 0, 128, 128), [byte[]]@(128, 128, 255, 0), [ref]$mean, [ref]$rms)
# 128 decodes to 1/255 x 2 - 1 = +0.0039, not 0: the four vectors' mean is that, (0.0078, 0.0078) / 4.
Check "decoded statistics: four unit vectors (+-x, +-y) average out, rms 1" ("{0:F4} {1:F4}" -f $mean, $rms) ("{0:F4} {1:F4}" -f ([Math]::Sqrt(2.0) * 2.0 * (128.0 / 255.0 * 2.0 - 1.0) / 4.0), 1.0)
Check "the ripples' defaults: moving, 100%; the city reflection's waves 100%" ("{0} {1} {2}" -f (Default 'WaterRipples'), (Default 'WaterRippleStrength'), (Default 'WaterCityWaves')) "True 1 1"
Check "the reflection's length 150%: the ray's slope 2/3 of the mirror's" ("{0:F6}" -f $optics::MirrorSlope(0.3, 1.5)) ("{0:F6}" -f 0.2)
Check "  length 100%: the mirror" ("{0:F6}" -f $optics::MirrorSlope(0.3, 1.0)) ("{0:F6}" -f 0.3)
Check "  below 100% (or unset, 0): read as the mirror" ("{0:F6}" -f $optics::MirrorSlope(0.3, 0.0)) ("{0:F6}" -f 0.3)
Check "the streaks' default is 0%, the length's 130% (the author's, 2026-10-07)" ("{0} {1}" -f (Default 'WaterCityStreaks'), (Default 'WaterCityLength')) "0 1.3"
Check "  the sky in the water 100% (real water, per ripple), the city by night 300%" ("{0} {1}" -f (Default 'WaterSky'), (Default 'WaterCityNight')) "1 3"
Check "the water's colour is ours by default (not the theme's)" (Default 'WaterThemeColor') "False"

# Deep water (opt-in): off by default; off keeps every share of the sky; on, steep views keep the
# row's share and the grazing water all of it, between them rising without a step.
Check "Deep water is off by default" (Default 'WaterDeep') "False"
$keepOff = @(0.02, 0.1, 0.5) | ForEach-Object { $optics::DeepSkyKeep($_, 0.0) }
Check "  off: the sky untouched at every reflectance (the shaders' _RGWaterDeep 0)" ($keepOff -join " ") "1 1 1"
Check "  on: a steep view (2-4%) keeps the row's share, the grazing water (30%+) all of it" ("{0:F3} {1:F3} {2:F3} {3:F3}" -f $optics::DeepSkyKeep(0.02, 0.1), $optics::DeepSkyKeep(0.04, 0.1), $optics::DeepSkyKeep(0.30, 0.1), $optics::DeepSkyKeep(0.8, 0.1)) "0.100 0.100 1.000 1.000"
Check "  half way between, half way up" ("{0:F3}" -f $optics::DeepSkyKeep(0.17, 0.1)) "0.550"
Check "  the deep colour covers nothing at the waterline, 63% at 6 m, almost all past 30 m" ("{0:F2} {1:F2} {2:F2} {3:F2}" -f $optics::DeepCover(-1.0), $optics::DeepCover(0.0), $optics::DeepCover(6.0), $optics::DeepCover(30.0)) "0.00 0.00 0.63 0.99"
Check "  the path to a flat 10 m bed: 10 m from overhead, 11.80 m at 45 deg, 15.12 m grazing (Snell, 1.333)" ("{0:F2} {1:F2} {2:F2}" -f $optics::DeepPathFlat(10.0, 1.0), $optics::DeepPathFlat(10.0, [Math]::Sqrt(0.5)), $optics::DeepPathFlat(10.0, 0.0)) "10.00 11.80 15.12"
Check "  the march reaches 5 fades (30 m, 99.3%) in 8 steps (WaterSky's DeepReach / DeepSteps)" ("{0:F0} {1:F3} {2}" -f ($optics::DeepReach * $optics::DeepFade), $optics::DeepCover($optics::DeepReach * $optics::DeepFade), $optics::DeepSteps) "30 0.993 8"

# The sky per ripple (2026-10-08): the old cube counted each ripple's tilt twice.
$view = 35.0
$old = ($optics::OldCubeReflectance($rho, $view, -10.0) + $optics::OldCubeReflectance($rho, $view, 10.0)) / 2.0
$real = ($optics::FacetReflectance($view, -10.0) + $optics::FacetReflectance($view, 10.0)) / 2.0
Check "the old cube: a facet 10 deg away at a 35 deg view got the 15 deg ray's 21%" ("{0:F3}" -f $optics::OldCubeReflectance($rho, $view, -10.0)) ("{0:F3}" -f $optics::Lookup($rho, [Math]::Sin(15.0 * [Math]::PI / 180.0)))
Check "  its true reflectance is 8.3% (Schlick at 65 deg)" ("{0:F3}" -f $optics::FacetReflectance($view, -10.0)) "0.083"
Check "  so a +-10 deg pair saw about twice the sky (1.8x..2.6x)" ($old / $real -gt 1.8 -and $old / $real -lt 2.6) "True"
Check "the sky layer: resolved ripples are Schlick on the facet" ("{0:F6}" -f $optics::SkyReflectance($rho, 0.5, 0.0)) ("{0:F6}" -f $optics::Fresnel(0.5))
Check "  averaged away, the rough sea's table" ("{0:F6}" -f $optics::SkyReflectance($rho, 0.5, 1.0)) ("{0:F6}" -f $optics::Lookup($rho, 0.5))
$start = 0.0; $span = 0.0
$optics::SkyRippleMips(1.0, 1.0, [ref]$start, [ref]$span)
Check "  the shortest ripple (0.5 m) goes at mip 0.64, the longest (16 m) 5 mips later" ("{0:F2} {1:F2}" -f $start, $span) "0.64 5.00"
Check "the city reflection reaches the far plane (30 km at most)" $optics::MaxDistance 30000

$r = 1.2; $g = 1.1; $b = 1.0
$optics::FloorLight([ref]$r, [ref]$g, [ref]$b)
Check "the water's light: an ordinary light untouched" ("{0} {1} {2}" -f $r, $g, $b) "1.2 1.1 1"
$r = 0.8; $g = 0.2; $b = 0.0
$optics::FloorLight([ref]$r, [ref]$g, [ref]$b)
Check "  a deep-red dusk: blue floored at 5% of the luminance" ("{0:F5}" -f $b) ("{0:F5}" -f ((0.2126 * 0.8 + 0.7152 * 0.2) * 0.05))
$r = 0.0; $g = 0.0; $b = 0.0
$optics::FloorLight([ref]$r, [ref]$g, [ref]$b)
Check "  no light at all: 1e-3 each (never a division by 0)" ("{0} {1} {2}" -f $r, $g, $b) "0.001 0.001 0.001"

# The Water tab and the interiors are on by default (the author, 2026-10-11).
Check "the water controls are on by default" (Default 'WaterEnabled') "True"
Check "the interiors are on by default" (Default 'InteriorsEnabled') "True"

# ---- the foam (Water/FoamPattern.cs, Water/FoamOptics.cs) ----
$pattern = $asm.GetType('RealGlass.Water.FoamPattern', $true)
$foamOptics = $asm.GetType('RealGlass.Water.FoamOptics', $true)
$size = $pattern::Size
$texels = $pattern::Generate()
$again = $pattern::Generate()
Check "the foam's pattern: RGBA, a side of Size texels" $texels.Length ($size * $size * 4)
$same = $true
for ($i = 0; $i -lt $texels.Length; $i += 97) { if ($texels[$i] -ne $again[$i]) { $same = $false; break } }
Check "  the same every time (no clock, no Random)" $same "True"
# Each channel uniform on 0..255 (histogram-equalised): every value size^2 / 256 times, give or take one.
# r the lace, g the patches, b the bubbles' holes, a the bubble clouds (the layers, 2026-10-24).
foreach ($channel in 0, 1, 2, 3) {
    $counts = New-Object int[] 256
    for ($i = $channel; $i -lt $texels.Length; $i += 4) { $counts[$texels[$i]]++ }
    $expected = $size * $size / 256
    $off = 0
    foreach ($c in $counts) { if ([Math]::Abs($c - $expected) -gt 1) { $off++ } }
    Check ("  channel {0} uniform: every value {1} times" -f $channel, $expected) $off 0
}
# It tiles: the steps across the wrap (last column to first, last row to first) are no bigger than the
# steps inside it, on average (a seam would be a line of large jumps).
function Byte([int]$x, [int]$y, [int]$c) { return [int]$texels[(($y % $size) * $size + ($x % $size)) * 4 + $c] }
foreach ($channel in 0, 1, 2, 3) {
    $inside = 0.0; $wrap = 0.0
    for ($y = 0; $y -lt $size; $y++) {
        $inside += [Math]::Abs((Byte ($size / 2) $y $channel) - (Byte ($size / 2 - 1) $y $channel))
        $wrap += [Math]::Abs((Byte 0 $y $channel) - (Byte ($size - 1) $y $channel))
    }
    Check ("  channel {0} tiles: the step across the wrap no bigger than inside (x 1.5)" -f $channel) ($wrap -le 1.5 * $inside) "True"
}
$v = $foamOptics::Vectors(1.0, 1.0, 1.0, 1.0, 40.0)
Check "the foam's vectors: 6 of 4" $v.Length 24
Check "  the lace's and the patches' repeats a metre" ("{0:F5} {1:F5}" -f $v[0], $v[1]) ("{0:F5} {1:F5}" -f (1.0 / $pattern::LaceTile), (1.0 / $pattern::PatchTile))
Check "  shore: amount, depth, the sea's level, its tolerance" ("{0} {1} {2} {3}" -f $v[8], $v[9], $v[10], $v[11]) ("1 {0} 40 {1}" -f $foamOptics::ShoreDepth, $foamOptics::SeaTolerance)
Check "  fast water starts below where it is full; fronts too" (($v[13] -lt $v[14]) -and ($v[17] -lt $v[18])) "True"
Check "  the reflectance is Koepke's dense foam (20-55%)" (($v[20] -ge 0.2) -and ($v[20] -le 0.55)) "True"
Check "  the flow carries it as the game's ripples: 0.14 of 1/0.0061 m every 2.5 s" ("{0:F3}" -f $v[21]) ("{0:F3}" -f (0.14 / 0.0061 / 2.5))
Check "  the advection's cycle divides the game's 60 s clock" ((60.0 % $v[22]) -eq 0.0) "True"
Check "  a negative share is none" ("{0} {1}" -f ($foamOptics::Vectors(-1.0, -1.0, 1.0, 1.0, 40.0))[23], ($foamOptics::Vectors(-1.0, -1.0, 1.0, 1.0, 40.0))[8]) "0 0"
Check "the foam's defaults: on, 30%; shores 100%, fast water 5%, fronts 10%" ("{0} {1} {2} {3} {4}" -f (Default 'WaterFoam'), (Default 'WaterFoamAmount'), (Default 'WaterFoamShore'), (Default 'WaterFoamRapids'), (Default 'WaterFoamFronts')) "True 0.3 1 0.05 0.1"
# The layers (FoamOptics.LayerVectors): the foam's ages, and the surf in a band measured from the waterline.
$lv = $foamOptics::LayerVectors()
Check "the foam's layers: 10 vectors of 4" $lv.Length 40
Check "  outside the surf: the swell breaks out to 3 x its width, to 20 m deep, a streak's white lasting 0.4 s" ("{0} {1} {2}" -f $lv[32], $lv[33], $lv[34]) "3 20 0.4"
Check "  fronts only on a face steeper than the author's calm sea ever is (6%)" (($lv[36] -ge 0.06) -and ($lv[37] -gt $lv[36])) "True"
Check "  fresh foam goes first, the residual sheet next, the bubbles last" (($lv[0] -lt $lv[1]) -and ($lv[1] -lt $lv[2])) "True"
Check "  a fresh sheet is brighter than an old one, both within Koepke's 20-55%" (($lv[3] -gt $lv[4]) -and ($lv[4] -ge 0.2) -and ($lv[3] -le 0.55)) "True"
Check "  a bore every few seconds, dividing the game's 60 s clock" ((60.0 % $lv[12]) -eq 0.0) "True"
Check "  the surf: 70 m from the waterline, to 8 m deep, the seabed's slope over +-8 m (no less than 1%)" ("{0} {1} {2} {3}" -f $lv[23], $lv[26], $lv[25], $lv[27]) "70 8 8 0.01"
# On a coast as steep as the author's (2 m deep within ~30 m: 7%) the surf now spans 70 m of it; the
# 1.2 m band of depth it replaced was 17 m. At the bores' pace (a 1:30 beach) a bore crosses the band in
# 2 sqrt(70) / sqrt(g / 30) = 29 s: about four of them in it at once, 7.5 s apart.
$cross = 2.0 * [Math]::Sqrt($lv[23]) / [Math]::Sqrt(9.80665 * $lv[13])
Check "  about four bores in the surf at once" ([Math]::Round($cross / $lv[12])) 4
Check "  pure water absorbs red most, blue least (Pope & Fry 1997)" (($lv[28] -gt $lv[29]) -and ($lv[29] -gt $lv[30])) "True"
Check "  the surf's corridor round the real distance: x 1.5, give or take 12 m" ("{0} {1}" -f $lv[38], $lv[39]) "1.5 12"

# ---- the shore's distances (Water/ShoreDistance.cs: Felzenszwalb & Huttenlocher's transform) ----
# Against the definition, sample by sample: the land/sea mask as the class takes it (the lower of two
# height sets, bilinear between the points), the features the land next to the sea, each sample's
# nearest feature found by trying them all.
Add-Type -TypeDefinition @"
using System;
public static class ShoreTest
{
    public static byte[] Brute(ushort[] h, ushort[] h2, int n, float cell, float unit, float seaLevel, int factor, float step, int zero)
    {
        int side = (n - 1) * factor + 1;
        float sea = seaLevel / unit, spacing = cell / factor;
        var land = new bool[side * side];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int cx = Math.Min(i / factor, n - 2), cz = Math.Min(j / factor, n - 2);
                float fx = (i - cx * factor) / (float)factor, fz = (j - cz * factor) / (float)factor;
                float v = Bi(h, cz * n + cx, n, fx, fz);
                if (h2 != null) v = Math.Min(v, Bi(h2, cz * n + cx, n, fx, fz));
                land[j * side + i] = v >= sea;
            }
        var fi = new System.Collections.Generic.List<int>();
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int k = j * side + i;
                if (land[k] && ((i > 0 && !land[k - 1]) || (i < side - 1 && !land[k + 1]) || (j > 0 && !land[k - side]) || (j < side - 1 && !land[k + side])))
                    fi.Add(k);
            }
        var map = new byte[side * side];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int k = j * side + i;
                int best = int.MaxValue;
                foreach (int f in fi) { int dx = f % side - i, dz = f / side - j; best = Math.Min(best, dx * dx + dz * dz); }
                // Single precision, as the class rounds (a half-step tie must fall the same way).
                float r = best == int.MaxValue ? 1e9f : (float)Math.Sqrt(best);
                float metres = land[k] ? -(r + 0.5f) * spacing : (r - 0.5f) * spacing;
                map[k] = (byte)Math.Max(0, Math.Min(255, (double)zero + Math.Round(metres / step)));
            }
        return map;
    }
    static float Bi(ushort[] h, int at, int n, float fx, float fz)
    {
        float a = h[at] + fx * (h[at + 1] - h[at]), b = h[at + n] + fx * (h[at + n + 1] - h[at + n]);
        return a + fz * (b - a);
    }
    // A coast of blobs: heights in 1/64 m round a sea at 40 m, land up to 10 m above it, sea 14 m below.
    public static ushort[] Coast(int n, int seed, double landShare)
    {
        var rnd = new Random(seed);
        var cx = new double[6]; var cz = new double[6]; var cr = new double[6];
        for (int b = 0; b < 6; b++) { cx[b] = rnd.NextDouble() * n; cz[b] = rnd.NextDouble() * n; cr[b] = (0.1 + 0.25 * rnd.NextDouble()) * n * landShare * 2; }
        var h = new ushort[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double m = -1;
                for (int b = 0; b < 6; b++) m = Math.Max(m, 1 - Math.Sqrt((i - cx[b]) * (i - cx[b]) + (j - cz[b]) * (j - cz[b])) / cr[b]);
                double metres = 40 + (m > 0 ? 10 * m + 0.3 : -14 * Math.Min(1, -m * 3) - 0.3) + 0.4 * (rnd.NextDouble() - 0.5);
                h[j * n + i] = (ushort)Math.Round(metres * 64);
            }
        return h;
    }
}
"@
$shore = $asm.GetType('RealGlass.Water.ShoreDistance', $true)
$worst = 0; $off = 0; $total = 0
foreach ($seed in 1, 2, 3) {
    $n = 41
    $h = [ShoreTest]::Coast($n, $seed, 0.5)
    $h2 = if ($seed -eq 2) { [ShoreTest]::Coast($n, 9, 0.4) } else { $null }
    $sd = [Activator]::CreateInstance($shore, @([int]$n, [single]16))
    $sd.Build($h, $h2, [single](1.0 / 64.0), [single]40)
    $want = [ShoreTest]::Brute($h, $h2, $n, 16, (1.0 / 64.0), 40, $shore::Factor, $shore::Step, $shore::Zero)
    for ($k = 0; $k -lt $want.Length; $k++) {
        $d = [Math]::Abs([int]$sd.Map[$k] - [int]$want[$k])
        if ($d -gt $worst) { $worst = $d }
        if ($d -ne 0) { $off++ }
        $total++
    }
}
Check "the shore's distances: every sample as the definition's, on three coasts (one with two height sets)" ("{0} {1}" -f $off, $worst) "0 0"
$sea = 0; $land = 0; foreach ($b in $sd.Map) { if ($b -gt $shore::Zero) { $sea++ } elseif ($b -lt $shore::Zero) { $land++ } }
Check "  the coasts have both sea and land, the map both signs" (($sea -gt 500) -and ($land -gt 500) -and ($sd.SeaSamples -gt 0) -and ($sd.CoastSamples -gt 0)) "True"
# An island in a wide sea: far out the byte ends at 255 (past 254 m), far inland at 0.
$n = 81; $h = New-Object 'uint16[]' ($n * $n)
for ($j = 0; $j -lt $n; $j++) { for ($i = 0; $i -lt $n; $i++) { $h[$j * $n + $i] = if ($i -lt 50 -and $j -lt 50) { 50 * 64 } else { 26 * 64 } } }
$sd = [Activator]::CreateInstance($shore, @([int]$n, [single]16))
$sd.Build($h, $null, [single](1.0 / 64.0), [single]40)
$side = $sd.Side
Check "  a map of 81 points a side: 161 samples, 8 m apart" ("{0} {1}" -f $side, $sd.Spacing) "161 8"
Check "  far out at sea 255, deep inland 0" ("{0} {1}" -f $sd.Map[($side - 1) * $side + $side - 1], $sd.Map[0]) "255 0"
# Along a row across the coast (the land's last point at x = 49 cells: sample 98): the waterline between
# samples 98 and 99, the sea's samples 4, 12, 20 m out, the land's 4, 12 m in.
$row = 40 * $side
$a = @(); foreach ($i in 97, 98, 99, 100, 101) { $a += $shore::Metres($sd.Map[$row + $i]) }
Check "  across the coast: 12 m and 4 m in, 4, 12, 20 m out (steps of 2 m)" ($a -join " ") "-12 -4 4 12 20"
$scale = 0.0; $offset = 0.0
$game = [Activator]::CreateInstance($shore, @([int]1081, [single]16))
$game.Mapping([single]-8640, [ref]$scale, [ref]$offset)
Check "  the game's map: 2161 samples a side; the world's middle at uv 0.5, the first point at its texel's centre" ("{0} {1:F6} {2:F6}" -f $game.Side, ($scale * 0 + $offset), (-8640 * $scale + $offset)) ("2161 0.500000 {0:F6}" -f (0.5 / 2161))

# ---- the view from under the water (Water/WaterUnderOptics.cs) ----
$under = $asm.GetType('RealGlass.Water.WaterUnderOptics', $true)
Check "Under the water's defaults: on, 60 m, light shafts on, sunlight on the seabed 0%" ("{0} {1} {2} {3}" -f (Default 'WaterUnder'), (Default 'WaterUnderVisibility'), (Default 'WaterUnderShafts'), (Default 'WaterUnderCaustics')) "True 60 True 0"
$noon = [double[]]@(0.0, 1.0, 0.0); $white = [double[]]@(1.0, 1.0, 1.0); $grey = [double[]]@(0.2, 0.2, 0.2)
$u = $under::Vectors(25.0, $noon, $white, $grey)
$u60 = $under::Vectors(60.0, $noon, $white, $grey)
Check "  c at green = 4.8 / the visibility (Zaneveld & Pegau): 25 m, 60 m" ("{0:F4} {1:F4}" -f $u[1], $u60[1]) ("{0:F4} {1:F4}" -f (4.8 / 25.0), (4.8 / 60.0))
Check "  red lost first, then blue (the water's yellow), green carries furthest" (($u[0] -gt $u[2]) -and ($u[2] -gt $u[1])) "True"
Check "  the daylight fades slower than a beam (K_d < c), faster for red" (($u[4] -lt $u[0]) -and ($u[5] -lt $u[1]) -and ($u[6] -lt $u[2]) -and ($u[4] -gt $u[5])) "True"
Check "  never clearer than pure water: red's c at 60 m held at its absorption + scattering" ("{0:F4}" -f $u60[0]) ("{0:F4}" -f (0.264 + 0.00288 * [Math]::Pow(610.0 / 500.0, -4.32)))
Check "  the veil teal: red under green under blue, green 60..100% of blue" (($u[8] -lt $u[9]) -and ($u[9] -lt $u[10]) -and ($u[9] / $u[10] -gt 0.6)) "True"
Check "  the water's colour its kind's at every visibility (25 m, 60 m)" ("{0:F6} {1:F6}" -f ($u[8] / $u[9]), ($u[10] / $u[9])) ("{0:F6} {1:F6}" -f ($u60[8] / $u60[9]), ($u60[10] / $u60[9]))
$u2 = $under::Vectors(25.0, $noon, [double[]]@(2.0, 2.0, 2.0), [double[]]@(0.4, 0.4, 0.4))
Check "  the veil follows the daylight: twice the sun and sky, twice the veil" ("{0:F6}" -f ($u2[9] / $u[9])) "2.000000"
$f = 0.02
Check "  the noon sun straight down, its share of the daylight" ("{0:F3} {1:F3} {2:F3} {3:F3}" -f $u[12], $u[13], $u[14], $u[15]) ("0.000 1.000 0.000 {0:F3}" -f ((1.0 - $f) / (1.0 - $f + 0.2)))
$low = [double[]]@(0.866, 0.5, 0.0)
$ul = $under::Vectors(25.0, $low, $white, $grey)
Check "  a sun 30 deg up refracted toward the vertical (sin 60 / 1.333)" ("{0:F4} {1:F4}" -f $ul[12], $ul[13]) ("{0:F4} {1:F4}" -f (0.866 / 1.333), [Math]::Sqrt(1.0 - [Math]::Pow(0.866 / 1.333, 2)))
$night = $under::Vectors(25.0, [double[]]@(0.0, -0.5, 0.866), $white, $grey)
Check "  at night: no sun share, the sky's light only" ("{0} {1:F6}" -f $night[15], $night[9]) ("0 {0:F6}" -f ($u[9] * 0.2 / (1.0 - $f + 0.2)))
$wild = $under::Vectors([double]::NaN, [double[]]@([double]::NaN, [double]::PositiveInfinity, 0.0), $white, $grey)
$finite = $true; foreach ($x in $wild) { if ([double]::IsNaN($x) -or [double]::IsInfinity($x)) { $finite = $false } }
Check "  NaN and infinity in: every number finite" $finite "True"
$clear = $under::Vectors(1000.0, $noon, $white, $grey)
Check "  the visibility held to 3..80 m" ("{0:F4}" -f $clear[1]) ("{0:F4}" -f (4.8 / 80.0))

# ---- the FFT waves (WaterSpectrum; the GPU against it: UnityProject\Assets\Editor\WaterWavesTest.cs) --
$spectrumType = $asm.GetType('RealGlass.Water.WaterSpectrum', $true)
# The GPU's steps (Stockham, radix 2) against the inverse DFT's definition, on a random line of 64.
$wsN = 64; $wsRnd = New-Object Random 5
$wsRe = New-Object double[] $wsN; $wsIm = New-Object double[] $wsN
for ($i = 0; $i -lt $wsN; $i++) { $wsRe[$i] = $wsRnd.NextDouble() - 0.5; $wsIm[$i] = $wsRnd.NextDouble() - 0.5 }
$wsDre = New-Object double[] $wsN; $wsDim = New-Object double[] $wsN
for ($j = 0; $j -lt $wsN; $j++) {
    for ($k = 0; $k -lt $wsN; $k++) {
        $ang = 2.0 * [Math]::PI * $j * $k / $wsN
        $wsDre[$j] += $wsRe[$k] * [Math]::Cos($ang) - $wsIm[$k] * [Math]::Sin($ang)
        $wsDim[$j] += $wsRe[$k] * [Math]::Sin($ang) + $wsIm[$k] * [Math]::Cos($ang)
    }
}
$spectrumType::Inverse1D($wsRe, $wsIm)
$wsWorst = 0.0
for ($j = 0; $j -lt $wsN; $j++) { $wsWorst = [Math]::Max($wsWorst, [Math]::Max([Math]::Abs($wsRe[$j] - $wsDre[$j]), [Math]::Abs($wsIm[$j] - $wsDim[$j]))) }
Check "the FFT's steps are the inverse DFT (sum X e^(+i k x), 64 points)" ($wsWorst -lt 1e-9) "True"

$ws = [Activator]::CreateInstance($spectrumType, @([double]7.0, [double]50000.0, [double]30.0, [uint32]1))
$ws2 = [Activator]::CreateInstance($spectrumType, @([double]7.0, [double]50000.0, [double]30.0, [uint32]1))
$ws3 = [Activator]::CreateInstance($spectrumType, @([double]7.0, [double]50000.0, [double]30.0, [uint32]2))
# Texel (20, 3) of cascade 1: a wave it holds.
$wsAt = 4 * (3 * 256 + 20)
Check "the waves: one seed, one sea (and another seed, another)" ("{0} {1} {2}" -f ($ws.H0[1][$wsAt] -ne 0.0), ($ws.H0[1][$wsAt] -eq $ws2.H0[1][$wsAt]), ($ws.H0[1][$wsAt] -eq $ws3.H0[1][$wsAt])) "True True False"
Check "  a 7 m/s wind over 50 km: JONSWAP's peak 25-35 m long" (($ws.PeakWavelength -gt 25.0) -and ($ws.PeakWavelength -lt 35.0)) "True"
$wsWhole = $true
foreach ($c in 0, 1) { for ($t = 0; $t -lt 65536; $t += 97) { $cy = $ws.H0[$c][4 * $t + 2]; if ($cy -lt 1.0 -or $cy -ne [Math]::Round($cy)) { $wsWhole = $false } } }
Check "  every wave a whole number of cycles an hour (the game's hour clock wraps with no jump)" $wsWhole "True"
$wsSym = $true; $wsTwice = $false
for ($m = -127; $m -le 127; $m += 3) {
    for ($q = -127; $q -le 127; $q += 5) {
        foreach ($c in 0, 1) { if ($spectrumType::Holds($c, $m, $q) -ne $spectrumType::Holds($c, -$m, -$q)) { $wsSym = $false } }
        if ($spectrumType::Holds(1, $m, $q) -and $spectrumType::Holds(0, 4 * $m, 4 * $q)) { $wsTwice = $true }
    }
}
Check "  every wave held has its -k twin (a real surface); none held by both cascades" ("{0} {1}" -f $wsSym, $wsTwice) "True False"
$wsSpread = 0.0
for ($t = 0; $t -lt 2000; $t++) { $th = -[Math]::PI + ($t + 0.5) * 2.0 * [Math]::PI / 2000; $wsSpread += $spectrumType::Spreading(1.3, $th) * 2.0 * [Math]::PI / 2000 }
Check "  Donelan-Banner's spreading is a unit over the circle" ("{0:F4}" -f $wsSpread) "1.0000"
$hx = $null; $hz = $null; $dxx = $null; $dzz = $null; $dxz = $null; $hh = $null
$wsMs = 0.0
foreach ($c in 0, 1) {
    $ws.Fields($c, 0.123, [ref]$hx, [ref]$hz, [ref]$dxx, [ref]$dzz, [ref]$dxz, [ref]$hh)
    $sum = 0.0
    for ($t = 0; $t -lt $hx.Length; $t++) { $sum += $hx[$t] * $hx[$t] + $hz[$t] * $hz[$t] }
    $wsMs += $sum / $hx.Length
}
Check "  scaled to a unit mean-square slope (both cascades, one moment of the hour: within 5%)" ([Math]::Abs($wsMs - 1.0) -lt 0.05) "True"
$wsTable = $ws.Table
$wsRising = $true
for ($e = 1; $e -lt 12; $e++) { if ($wsTable[4 * $e + 3] -lt $wsTable[4 * ($e - 1) + 3]) { $wsRising = $false } }
Check "  what the mips average away: none magnified, more each mip, all of it by the last" ("{0} {1} {2:F3}" -f $wsTable[3], $wsRising, $wsTable[47]) "0 True 1.000"
$wsCoxMunk = $optics::SlopeVariance(7.0)
$wsAll = $ws.RawMeanSquare * (1.0 + $ws.TailXX + $ws.TailZZ)
Check ("  the texture's slopes + the tail down to 1.7 cm within 15% of Cox & Munk's sea at 7 m/s ({0:F4} vs {1:F4})" -f $wsAll, $wsCoxMunk) ([Math]::Abs($wsAll / $wsCoxMunk - 1.0) -lt 0.15) "True"
# The game's wind (WeatherManager.m_windDirection a): _WindDirection = (sin a, 0, cos a), its WindZone
# turned to a + 180: a = 0 blows toward -z (heading 270), a = 90 toward -x (180).
Check "  the waves run with the game's wind: 0 deg -> 270, 90 -> 180, 225 -> 45" ("{0:F0} {1:F0} {2:F0}" -f $spectrumType::HeadingFromGameWind(0.0), $spectrumType::HeadingFromGameWind(90.0), $spectrumType::HeadingFromGameWind(225.0)) "270 180 45"
Check "the ocean waves' defaults: on, 7 m/s, choppiness 100%; the sun's glitter 35%" ("{0} {1} {2} {3}" -f (Default 'WaterOceanWaves'), (Default 'WaterWind'), (Default 'WaterChop'), (Default 'WaterGlitter')) "True 7 1 0.35"
Check "the glitter's sizes and the moon's own glitter: sun 50%, moon 35% at 50%" ("{0} {1} {2}" -f (Default 'WaterGlitterSize'), (Default 'WaterMoonGlitter'), (Default 'WaterMoonGlitterSize')) "0.5 0.35 0.5"

# ---- dynamic waves (Water\WaterWeather.cs: the waves' wind from the game's rain) -----------------
$weather = $asm.GetType('RealGlass.Water.WaterWeather', $true)
$seaType = $asm.GetType('RealGlass.Water.WaterSea', $true)
Check "dynamic waves: on by default; a dry day's wind is the Wind row's default" ("{0} {1}" -f (Default 'WaterDynamicWaves'), ($weather::CalmWind -eq (Default 'WaterWind'))) "True True"
Check "  the weather's wind: dry 7, the lightest rain 9, the heaviest 15 m/s; out of range held" ("{0} {1} {2} {3} {4}" -f $weather::Target(0.0), $weather::Target(0.25), $weather::Target(1.0), $weather::Target(-1.0), $weather::Target(5.0)) "7 9 15 7 15"
Check "  the ripples' roughness (Cox & Munk): x1 dry, rising with the wind, x1.43 at 15 m/s" ("{0:F3} {1} {2:F2}" -f $weather::RoughnessGain(7.0), ($weather::RoughnessGain(11.0) -gt 1.0 -and $weather::RoughnessGain(11.0) -lt $weather::RoughnessGain(15.0)), $weather::RoughnessGain(15.0)) "1.000 True 1.43"
Check "  the Shore Protection Manual's durations over 50 km: 7 m/s 6.0-6.6 h, 15 m/s 4.6-5.2 h" ("{0} {1}" -f (($weather::DevelopmentHours(7.0, 50000.0) -gt 6.0) -and ($weather::DevelopmentHours(7.0, 50000.0) -lt 6.6)), (($weather::DevelopmentHours(15.0, 50000.0) -gt 4.6) -and ($weather::DevelopmentHours(15.0, 50000.0) -lt 5.2))) "True True"
Check "  95% of the way in the target's duration; no time, no change" ("{0:F3} {1}" -f (($weather::Follow(7.0, 15.0, $weather::DevelopmentHours(15.0, 50000.0)) - 7.0) / 8.0), $weather::Follow(9.0, 15.0, 0.0)) "0.950 9"
$up = ($weather::Follow(7.0, 15.0, 1.0) - 7.0) / 8.0
$down = (15.0 - $weather::Follow(15.0, 7.0, 1.0)) / 8.0
Check "  it falls back slower than it rises (an hour: rising share > falling share)" ($up -gt $down) "True"
Check "  the spectrum's steps: a quarter m/s" ("{0} {1} {2}" -f $weather::Quantize(7.1), $weather::Quantize(7.13), $weather::Quantize(14.9)) "7 7.25 15"
$sea = [Activator]::CreateInstance($seaType)
$fph = $weather::FramesPerDay / 24.0
$sea.Advance(1.0, 1000.0)
$s1 = $sea.Wind
$sea.Advance(1.0, 1000.0)
$s2 = $sea.Wind
$sea.Advance(0.0, 1000.0 + $fph)
$s3 = $sea.Wind
$sea.Advance(0.0, 1000.0 + $fph)
$s4 = $sea.Wind
Check "  a city starts on its weather; paused, the sea holds; an hour after the rain, it is falling" ("{0} {1} {2} {3}" -f $s1, ($s2 -eq $s1), (($s3 -lt 15.0) -and ($s3 -gt 7.0)), ($s4 -eq $s3)) "15 True True True"
$sea.Advance(0.0, 500.0)
$s5 = $sea.Wind
$sea.Advance(1.0, 500.0 + 30.0 * $weather::FramesPerDay)
$s6 = $sea.Wind
$sea.Reset()
$sea.Advance([double]::NaN, 10.0)
Check "  time running back, or a jump of days: put on the weather at once; no rain known, dry" ("{0} {1} {2} {3}" -f $s5, $s6, $sea.Wind, $sea.Started) "7 15 7 True"

# ---- Procedural Objects' buildings (Glass\ProceduralPick.cs, Glass\ProceduralAccess.cs) -------
$v3 = $unity.GetType('UnityEngine.Vector3', $true)
function V([float]$x, [float]$y, [float]$z) { return [Activator]::CreateInstance($v3, [object[]]@([single]$x, [single]$y, [single]$z)) }
$pick = $asm.GetType('RealGlass.Glass.ProceduralPick', $true)
$boxMin = V -1 0 -1
$boxMax = V 1 10 1
Check "picking: a ray meets a box where it enters it" ("{0:F2}" -f $pick::RayBox((V 0 5 -10), (V 0 0 1), $boxMin, $boxMax)) "9.00"
Check "  from inside it, at once" ("{0:F2}" -f $pick::RayBox((V 0 5 0), (V 0 0 1), $boxMin, $boxMax)) "0.00"
Check "  passing beside it, or along a face outside it, never" ("{0} {1}" -f $pick::RayBox((V 3 5 -10), (V 0 0 1), $boxMin, $boxMax), $pick::RayBox((V 0 11 -10), (V 0 0 1), $boxMin, $boxMax)) "-1 -1"
Check "  pointing away, never" ($pick::RayBox((V 0 5 -10), (V 0 0 -1), $boxMin, $boxMax)) "-1"
# A wall of two triangles at z = 0, x and y in -1..1.
$wall = [Array]::CreateInstance($v3, 4)
$wall[0] = V -1 -1 0; $wall[1] = V 1 -1 0; $wall[2] = V 1 1 0; $wall[3] = V -1 1 0
$wallTri = [int[]]@(0, 1, 2, 0, 2, 3)
Check "  a ray meets the nearest triangle, from the front and from behind" ("{0:F2} {1:F2}" -f $pick::RayTriangles((V 0.3 0.2 -5), (V 0 0 1), $wall, $wallTri, 1000.0), $pick::RayTriangles((V -0.5 0.4 5), (V 0 0 -1), $wall, $wallTri, 1000.0)) "5.00 5.00"
Check "  on the edge two triangles share, too" ("{0:F2}" -f $pick::RayTriangles((V 0 0 -5), (V 0 0 1), $wall, $wallTri, 1000.0)) "5.00"
Check "  not beside the wall, not past the nearest already met, not behind its start" ("{0} {1} {2}" -f $pick::RayTriangles((V 1.5 0 -5), (V 0 0 1), $wall, $wallTri, 1000.0), $pick::RayTriangles((V 0 0 -5), (V 0 0 1), $wall, $wallTri, 4.0), $pick::RayTriangles((V 0 0 5), (V 0 0 1), $wall, $wallTri, 1000.0)) "-1 -1 -1"
# A slanted ray, against the plane's own answer.
$slant = $pick::RayTriangles((V 0.2 -0.3 -4), (V 0.1 0.05 0.9938), $wall, $wallTri, 1000.0)
Check "  a slanted ray where it crosses the wall's plane" ("{0:F3}" -f $slant) ("{0:F3}" -f (4.0 / 0.9938))

$poDll = "C:\Program Files (x86)\Steam\steamapps\workshop\content\255710\1094334744\ProceduralObjects.dll"
if (Test-Path $poDll) {
    # Procedural Objects as installed here, from its DLL: what Real Glass reads of it, on objects
    # made without its constructors (no game running), their fields set as it sets them.
    $poAsm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($poDll))
    $accessType = $asm.GetType('RealGlass.Glass.ProceduralAccess', $true)
    $problem = $null
    $access = $accessType::Create($poAsm.GetType('ProceduralObjects.ProceduralObjectsLogic', $true), [ref]$problem)
    Check "Procedural Objects (its DLL here): everything Real Glass reads of it is there" ("{0}" -f $problem) ""
    if ($access -ne $null) {
        Check "  its version" $access.Version "1.7.8"
        $poType = $poAsm.GetType('ProceduralObjects.Classes.ProceduralObject', $true)
        $layerType = $poAsm.GetType('ProceduralObjects.Classes.Layer', $true)
        $optionsType = $poAsm.GetType('ProceduralObjects.Classes.RenderOptions', $true)
        $o = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($poType)
        $poType.GetField('baseInfoType').SetValue($o, 'BUILDING')
        $poType.GetField('m_position').SetValue($o, (V 10 20 30))
        $poType.GetField('id').SetValue($o, 1234)
        Check "  an object's kind, place and number" ("{0} {1} {2}" -f $access.Kind.Invoke($o), $access.Position.Invoke($o), $access.Id.Invoke($o)) "BUILDING (10.0, 20.0, 30.0) 1234"
        $options = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($optionsType)
        $visibility = $poType.GetField('m_visibility')
        $layer = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($layerType)
        function Drawn($inside, $hidden, [int]$vis, $night) {
            $poType.GetField('_insideRenderView').SetValue($o, [bool]$inside)
            if ($hidden -eq $null) { $poType.GetField('layer').SetValue($o, $null) }
            else { $layerType.GetField('m_isHidden').SetValue($layer, [bool]$hidden); $poType.GetField('layer').SetValue($o, $layer) }
            $visibility.SetValue($o, [Enum]::ToObject($visibility.FieldType, $vis))
            return $access.Drawn.Invoke($o, $options, [bool]$night)
        }
        Check "  drawn as it draws: in range, no layer, always" (Drawn $true $null 0 $false) "True"
        Check "  not out of range, not on a hidden layer; on a shown one" ("{0} {1} {2}" -f (Drawn $false $null 0 $false), (Drawn $true $true 0 $false), (Drawn $true $false 0 $false)) "False False True"
        Check "  night only: by night, not by day; day only: by day, not by night" ("{0} {1} {2} {3}" -f (Drawn $true $null 1 $true), (Drawn $true $null 1 $false), (Drawn $true $null 2 $false), (Drawn $true $null 2 $true)) "True False True False"
    }
}
else {
    Write-Host "skip  Procedural Objects is not installed here: what Real Glass reads of it is not checked"
}

# ---- the model's glass on its LOD (Glass\LodGlass.cs) ---------------------------------------
# A tower 10 x 10 x 30 m, four sides, its model's texture laid out side by side (u) and its LOD's
# turned about the diagonal (the sides stacked in v): the two layouts share nothing, so only the
# building's geometry can carry the model's glass over. The model's glass: all of side 0, the
# left half of the others. Then LODs with no glass, the same glass, glass in the other half,
# shaped unlike the model; the verdicts; the shares; the mask and its mips; the read-back.
Add-Type -TypeDefinition @"
using System;
public static class LodTest
{
    // Side k of a box (x -hx..hx, z -hz..hz, y 0..h), t along it: 0 +z, 1 +x, 2 -z, 3 -x.
    static void Corner(int k, float t, float hx, float hz, out float x, out float z)
    {
        switch (k)
        {
            case 0: x = -hx + 2f * hx * t; z = hz; break;
            case 1: x = hx; z = hz - 2f * hz * t; break;
            case 2: x = hx - 2f * hx * t; z = -hz; break;
            default: x = -hx; z = -hz + 2f * hz * t; break;
        }
    }
    public static float[] Pos(float hx, float hz, float h)
    {
        var p = new float[16 * 3];
        for (int k = 0; k < 4; k++)
            for (int c = 0; c < 4; c++)
            {
                float t = c == 1 || c == 2 ? 1f : 0f, y = c >= 2 ? h : 0f, x, z;
                Corner(k, t, hx, hz, out x, out z);
                int i = 3 * (4 * k + c);
                p[i] = x; p[i + 1] = y; p[i + 2] = z;
            }
        return p;
    }
    // The model's layout: u = (k + t) / 4, v = height; turned (the LOD's): u = height, v = (k + t) / 4.
    public static float[] Uv(bool turned)
    {
        var uv = new float[16 * 2];
        for (int k = 0; k < 4; k++)
            for (int c = 0; c < 4; c++)
            {
                float t = c == 1 || c == 2 ? 1f : 0f, y = c >= 2 ? 1f : 0f, along = (k + t) / 4f;
                int i = 2 * (4 * k + c);
                uv[i] = turned ? y : along; uv[i + 1] = turned ? along : y;
            }
        return uv;
    }
    public static int[] Tri()
    {
        var t = new int[4 * 6];
        for (int k = 0; k < 4; k++)
        {
            int b = 4 * k;
            t[6 * k] = b; t[6 * k + 1] = b + 1; t[6 * k + 2] = b + 2;
            t[6 * k + 3] = b; t[6 * k + 4] = b + 2; t[6 * k + 5] = b + 3;
        }
        return t;
    }
    // Glass along the sides: mode 0 none, 1 side 0 and the left half of the others, 2 the other
    // halves (glass elsewhere), 3 everywhere. lodLayout: the sides run along v.
    public static bool Glass(int mode, float along)
    {
        int k = Math.Min(3, (int)(along * 4f));
        float t = along * 4f - k;
        if (mode == 1) return k == 0 || t < 0.5f;
        if (mode == 2) return k != 0 && t >= 0.5f;
        return mode == 3;
    }
    public static byte[] Picture(int mode, bool lodLayout)
    {
        var p = new byte[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                p[y * 256 + x] = Glass(mode, ((lodLayout ? y : x) + 0.5f) / 256f) ? (byte)255 : (byte)0;
        return p;
    }
    // How many of an LOD grid's texels hold what the model's glass says at their point (> 200
    // where glass, < 55 where not), the texels within 1.5 texels of a side's edge left out.
    public static int Right(byte[] grid, int side, int mode, out int judged)
    {
        int right = 0; judged = 0;
        for (int y = 0; y < side; y++)
        {
            float along = (y + 0.5f) / side, t = along * 4f - (int)(along * 4f);
            float edge = Math.Min(Math.Abs(t), Math.Min(Math.Abs(t - 0.5f), Math.Abs(1f - t))) * side / 4f;
            if (edge < 1.5f) continue;
            for (int x = 0; x < side; x++)
            {
                judged++;
                int v = grid[y * side + x];
                if (Glass(mode, along) ? v > 200 : v < 55) right++;
            }
        }
        return right;
    }
}
"@
$lg = $asm.GetType('RealGlass.Glass.LodGlass', $true)
$lgEntry = $asm.GetType('RealGlass.Glass.LodGlassEntry', $true)
$v4 = $unity.GetType('UnityEngine.Vector4', $true)
function V4([double]$x, [double]$y, [double]$z, [double]$w) { return [Activator]::CreateInstance($v4, [object[]]@([single]$x, [single]$y, [single]$z, [single]$w)) }
function Tower([int]$modelMode, [int]$lodMode, [double]$lodHx = 5, [double]$lodHz = 5) {
    $e = [Activator]::CreateInstance($lgEntry)
    $e.Asset = "tower"; $e.Name = "tower"
    $e.Rect = V4 0.25 0.5 0.25 0.25
    $e.ModelSt = V4 1 1 0 0
    $e.ModelPos = [LodTest]::Pos(5, 5, 30); $e.ModelUv = [LodTest]::Uv($false); $e.ModelTri = [LodTest]::Tri()
    $e.LodPos = [LodTest]::Pos($lodHx, $lodHz, 30); $e.LodUv = [LodTest]::Uv($true); $e.LodTri = [LodTest]::Tri()
    $e.ModelShine = [LodTest]::Picture($modelMode, $false); $e.LodShine = [LodTest]::Picture($lodMode, $true)
    $e.SideX = 64; $e.SideY = 64
    $e.Threshold = [single]0.7
    $lg::Transfer($e)
    return $e
}

$none = Tower 1 0
$judged = 0
$right = [LodTest]::Right($none.Grid, 64, 1, [ref]$judged)
Check "LOD glass: an LOD laid out otherwise, compared: every texel on its mesh, all met the model, on its surface" ("{0} {1} {2} {3:F2} '{4}'" -f $none.Transferred, $none.OnLod, $none.Met, $none.Gap, $none.Problem) "True 4096 4096 0.00 ''"
Check "  the model's glass carried onto the LOD's own layout, texel by texel (away from the sides' edges)" ("{0} of {1}" -f $right, $judged) ("{0} of {0}" -f $judged)
Check "  the geometry and the pictures let go" ("{0} {1} {2}" -f ($none.ModelPos -eq $null), ($none.LodTri -eq $null), ($none.ModelShine -eq $null)) "True True True"

$same = Tower 1 1
$wrong = Tower 1 2
$bare = Tower 0 0
$off = Tower 1 0 9 9
$half = Tower 1 0 5 9
$entries = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($lgEntry))
foreach ($t in @($none, $same, $wrong, $bare, $off, $half)) { $entries.Add($t) }
$changed = $lg::DecideAll($entries, $true)
Check "  verdicts: none of its own -> the model's, the same -> agrees, elsewhere -> the model's, no glass, 4 m off, half off" (($entries | ForEach-Object { $_.Verdict }) -join " ") "3 1 3 2 4 4"
Check "  its numbers: model glass 62.5%, the LOD's 0, alike 0; the same one alike 1; the one elsewhere alike 0" ("{0:F3} {1:F2} {2:F2} {3:F2} {4:F2}" -f $none.ModelShare, $none.LodShare, $none.Agreement, $same.Agreement, $wrong.Agreement) "0.625 0.00 0.00 1.00 0.00"
Check "  reach: all, none 4 m away, part where only two sides overlap the model" ("{0:F2} {1:F2} {2}" -f $none.Reach, $off.Reach, ($half.Reach -gt 0.1 -and $half.Reach -lt 0.6)) "1.00 0.00 True"
Check "  a change of the borrowing set is said" $changed "True"
Check "  the same again changes nothing; switched off: off, and that is a change" ("{0} {1} {2}" -f $lg::DecideAll($entries, $true), $lg::DecideAll($entries, $false), $none.Verdict) "False True 7"
$none.Mode = $lg::ModeOwn; $same.Mode = $lg::ModeModel
[void]$lg::DecideAll($entries, $true)
Check "  the building window: its own kept, the model's asked for" ("{0} {1}" -f $none.Verdict, $same.Verdict) "6 5"
Check "  only the model's and the asked-for draw the model's glass" ((0..8 | Where-Object { $lg::Borrows($_) }) -join " ") "3 5"
$v = @($lg::Decide(0, $true, $false, 0, 0.0, 0.0, 0.0, 1.0), $lg::Decide(0, $true, $true, 10, [single]::NaN, 0.0, 0.5, 0.0))
Check "  not compared: failed; a NaN reach: shaped unlike (never the model's)" ($v -join " ") "8 4"

$joint = New-Object 'int[]' 1024
$joint[0 * 32 + 31] = 30; $joint[31 * 32 + 31] = 50; $joint[0] = 20
$sl = [single]0; $sm = [single]0; $sa = [single]0
$lg::Shares($joint, [single]0.7, [ref]$sl, [ref]$sm, [ref]$sa)
Check "  shares: the LOD's, the model's, both over either" ("{0:F2} {1:F2} {2:F3}" -f $sl, $sm, $sa) "0.50 0.80 0.625"
$lg::Shares((New-Object 'int[]' 1024), [single]0.7, [ref]$sl, [ref]$sm, [ref]$sa)
Check "  nothing met: no shares, nothing to disagree on" ("{0:F2} {1:F2} {2:F2}" -f $sl, $sm, $sa) "0.00 0.00 1.00"

# The mask: 16 x 16 and its mips, one LOD's rectangle (a quarter of the atlas) all glass.
$box = [Activator]::CreateInstance($lgEntry)
$box.Rect = V4 0.25 0.5 0.25 0.25
$box.SideX = 4; $box.SideY = 4
$box.Grid = [byte[]](@(255) * 16)
$one = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($lgEntry))
$one.Add($box)
$mask = $lg::BuildMask($one, 16, 16)
$inside = 0; $outside = 0
for ($y = 0; $y -lt 16; $y++) { for ($x = 0; $x -lt 16; $x++) { if ($x -ge 4 -and $x -lt 8 -and $y -ge 8 -and $y -lt 12) { $inside += $mask[$y * 16 + $x] } else { $outside += $mask[$y * 16 + $x] } } }
Check "  the mask: 16x16 + every mip, 255 over the rectangle's 16 texels, 0 elsewhere" ("{0} {1} {2} {3}" -f $mask.Length, $lg::MipCount(16, 16), $inside, $outside) "341 5 4080 0"
$mip1 = 256; $m1 = @(); for ($y = 0; $y -lt 8; $y++) { for ($x = 0; $x -lt 8; $x++) { if ($mask[$mip1 + $y * 8 + $x] -gt 0) { $m1 += "$x,$y" } } }
Check "  its first mip: the rectangle's 2 x 2, nothing round it" ($m1 -join " ") "2,4 3,4 2,5 3,5"
Check "  encoding: 0 is the LOD's own, shine 0..255 is 1..255, half way 128" ("{0} {1} {2}" -f $lg::Encode(0), $lg::Encode(255), $lg::Encode(128)) "1 255 128"
$mw = 0; $mh = 0
$lg::MaskSize(8192, 8192, [ref]$mw, [ref]$mh); $a = "$mw x $mh"
$lg::MaskSize(4096, 2048, [ref]$mw, [ref]$mh); $b = "$mw x $mh"
Check "  the mask's size for an 8192 atlas, a 4096 x 2048 one" "$a, $b" "4096 x 4096, 4096 x 2048"
$lg::SideFor($box, 4096, 4096)
Check "  an LOD's grid: its rectangle's texels in the mask, at most 128" ("{0} {1}" -f $box.SideX, $box.SideY) "128 128"

# The read-back: tile 0's lower half (0, 255, 0, 255), upper half (255, 0, 255, 0); either way up;
# then one LOD's tile into its two pictures, both ways up.
function Px([byte]$r, [byte]$g, [byte]$b, [byte]$a) { return [Activator]::CreateInstance($color32, [object[]]@($r, $g, $b, $a)) }
$tw = 2048; $th = 512
$low = Px 0 255 0 255; $high = Px 255 0 255 0
$up = [Array]::CreateInstance($color32, $tw * $th); $down = [Array]::CreateInstance($color32, $tw * $th)
for ($y = 0; $y -lt 256; $y++) { for ($x = 0; $x -lt 4; $x++) {
    $c = if ($y -lt 128) { $low } else { $high }
    $up[$y * $tw + $x] = $c; $down[($th - 1 - $y) * $tw + $x] = $c
} }
$flipped = $false
$foundUp = $lg::FindMarker($up, $tw, $th, [ref]$flipped); $upFlipped = $flipped
$foundDown = $lg::FindMarker($down, $tw, $th, [ref]$flipped); $downFlipped = $flipped
$foundNone = $lg::FindMarker([Array]::CreateInstance($color32, $tw * $th), $tw, $th, [ref]$flipped)
Check "  the read-back's marker: found as drawn, found upside down, not found in an empty one" ("$foundUp $upFlipped $foundDown $downFlipped $foundNone") "True False True True False"
$tile = [Array]::CreateInstance($color32, $tw * $th)
$tile[0 * $tw + 256 + 3] = Px 200 10 0 255; $tile[255 * $tw + 256 + 7] = Px 7 99 0 255
$e = [Activator]::CreateInstance($lgEntry)
$lg::TakeTile($e, $tile, $tw, $th, 256, 0, $false)
$a = "{0} {1} {2} {3}" -f $e.ModelShine[3], $e.LodShine[3], $e.ModelShine[255 * 256 + 7], $e.LodShine[255 * 256 + 7]
$lg::TakeTile($e, $tile, $tw, $th, 256, 256, $true)
$b = "{0} {1}" -f $e.ModelShine[255 * 256 + 3], $e.LodShine[7]
Check "  a tile: r the model's, g the LOD's; read upside down, its rows the other way" "$a; $b" "200 10 7 99; 200 99"
Check "LOD glass: on by default" (Default 'LodGlassFromModel') "True"

# ---- the rooms fitted to the windows (Glass\RoomFit.cs) --------------------------------------
# A tower 20 x 20 x 60 m, its four sides side by side in its texture (u), its height up v; its
# shine map 512 x 512 painted with windows: a shop front 0.3..4.2 m, then storeys of 3.2 m from
# 4.5 m with windows 1.0..2.6 m above each floor, 1.7 m wide every 2.5 m along. Then strips, plain
# glass, and a round tower of 40 narrow facets.
Add-Type -TypeDefinition @"
using System;
public static class FitTest
{
    // An upright prism of n sides of radius r (n = 4: a box turned 45 degrees -- here a 20 m box
    // when sides4 is set), height h; uv: side k at u = (k + t) / n, v = y / h.
    public static float[] Pos(int n, float r, float h, bool box)
    {
        var p = new float[n * 4 * 3];
        for (int k = 0; k < n; k++)
            for (int c = 0; c < 4; c++)
            {
                int e = c == 1 || c == 2 ? k + 1 : k;
                double a = 2.0 * Math.PI * e / n + (box ? Math.PI / 4.0 : 0.0);
                float x = (float)(Math.Cos(a) * r), z = (float)(Math.Sin(a) * r), y = c >= 2 ? h : 0f;
                int i = 3 * (4 * k + c);
                p[i] = x; p[i + 1] = y; p[i + 2] = z;
            }
        return p;
    }
    public static float[] Uv(int n)
    {
        var uv = new float[n * 4 * 2];
        for (int k = 0; k < n; k++)
            for (int c = 0; c < 4; c++)
            {
                int i = 2 * (4 * k + c);
                uv[i] = (k + (c == 1 || c == 2 ? 1f : 0f)) / n; uv[i + 1] = c >= 2 ? 1f : 0f;
            }
        return uv;
    }
    public static int[] Tri(int n)
    {
        var t = new int[n * 6];
        for (int k = 0; k < n; k++)
        {
            int b = 4 * k;
            t[6 * k] = b; t[6 * k + 1] = b + 2; t[6 * k + 2] = b + 1;
            t[6 * k + 3] = b; t[6 * k + 4] = b + 3; t[6 * k + 5] = b + 2;
        }
        return t;
    }
    // mode 0: the tower above; 1 strips (windows 1.0..1.4 m a 3.2 m storey from the ground); 2 all glass.
    public static byte[] Picture(int mode, float h, float sideWidth, int sides)
    {
        var p = new byte[512 * 512];
        for (int row = 0; row < 512; row++)
            for (int col = 0; col < 512; col++)
            {
                double y = (row + 0.5) / 512.0 * h, u = (col + 0.5) / 512.0 * sides;
                double s = (u - Math.Floor(u)) * sideWidth;
                bool glass;
                if (mode == 2) glass = true;
                else if (mode == 1) glass = Mod(y, 3.2) >= 1.0 && Mod(y, 3.2) < 1.4;
                else if (y < 4.5) glass = y >= 0.3 && y < 4.2 && Mod(s, 2.5) >= 0.2;
                else glass = Mod(y - 4.5, 3.2) >= 1.0 && Mod(y - 4.5, 3.2) < 2.6 && Mod(s, 2.5) >= 0.4 && Mod(s, 2.5) < 2.1;
                p[row * 512 + col] = glass ? (byte)230 : (byte)20;
            }
        return p;
    }
    static double Mod(double a, double b) { return a - b * Math.Floor(a / b); }

    // A method called with the thread in another culture (PowerShell puts the culture back after every statement).
    public static object InvokeIn(string culture, System.Reflection.MethodInfo method, object target, object[] args)
    {
        var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture);
        try { return method.Invoke(target, args); }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = saved; }
    }
}
"@
$rf = $asm.GetType('RealGlass.Glass.RoomFit', $true)
$box = [single](10 * [math]::Sqrt(2))
function Fit([int]$mode, [int]$n = 4, [single]$radius = $box, [single]$sideWidth = 20) {
    $pos = [FitTest]::Pos($n, $radius, 60, $n -eq 4)
    return $rf::Measure($pos, [FitTest]::Uv($n), [FitTest]::Tri($n), [FitTest]::Picture($mode, 60, $sideWidth, $n), 512, 1, 1, 0, 0, $false, 6)
}
$tower = Fit 0
Check "Rooms fitted: a tower measured, its upright faces 4 x 20 x 60 m" ("{0} '{1}' {2:F0}" -f $tower.Measured, $tower.Problem, $tower.UprightArea) "True '' 4800"
Check "  its storey: 3.2 m (to a centimetre), a strong rhythm" ("{0:F2} {1}" -f $tower.Storey, ($tower.StoreyRhythm -gt 0.8)) "3.20 True"
Check "  its windows 1.6 m a storey (to 15 cm: a texel is 12 cm); the floor 0.9 m under them: phase 1.4 (to 15 cm)" ("{0} {1}" -f ([math]::Abs($tower.WindowBand - 1.6) -le 0.15), ([math]::Abs($tower.FloorPhase - 1.4) -le 0.15)) "True True"
Check "  the ground storey up to the first floor above the shop front (4.5 m, to 15 cm)" ([math]::Abs($tower.GroundTop - 4.5) -le 0.15) "True"
Check "  rooms: yes" $tower.Rooms "True"
$found = @(); foreach ($k in 0..15) { if ($tower.Sides[$k].Found) { $found += $k } }
Check "  four sides found, headed 0, 90, 180, 270 degrees" ($found -join " ") "0 4 8 12"
$s = $tower.Sides[0]
$wall = $s.Phase - 2.5 * [math]::Floor($s.Phase / 2.5)
Check "  a side: windows every 2.5 m (to 2 cm), a room two of them (an office's 6 m), its walls between windows" ("{0} {1:F1} {2}" -f ([math]::Abs($s.Module - 2.5) -le 0.02), $s.Width, ([math]::Min($wall, 2.5 - $wall) -le 0.1)) "True 5.0 True"
Check "  no side too narrow" $tower.SidesNarrow "0"
$strips = Fit 1
Check "  windows 0.4 m a storey: strips, no rooms" ("{0:F1} {1} {2}" -f $strips.Storey, ($strips.WindowBand -lt 0.6), $strips.Rooms) "3.2 True False"
$plain = Fit 2
Check "  all glass: no storey, no side (the storey grid)" ("{0} {1} {2}" -f $plain.Storey, $plain.SidesFound, $plain.Rooms) "0 0 True"
$round = Fit 0 40 18 ([single](2 * 18 * [math]::Sin([math]::PI / 40)))
Check "  a round tower of 40 facets 2.8 m wide: every side too narrow for a room" ($round.SidesNarrow -eq 16) "True"
$frac = 0.0
$sides = @(@(1, 0), @(0, 1), @(-1, 0), @(0, -1), @([math]::Cos(0.19), [math]::Sin(0.19))) | ForEach-Object { $rf::SideOf($_[0], $_[1], [ref]$frac) }
Check "  sides by heading: 0, 90, 180, 270 degrees, and 11 degrees (still side 0)" ($sides -join " ") "0 4 8 12 0"
Check "  room widths by kind: homes, offices, industry, shops with offices above" ("{0} {1} {2} {3}" -f $rf::CellWidth(0), $rf::CellWidth(1), $rf::CellWidth(6), $rf::CellWidth(7)) "3.6 6 8 6"
$none = $rf::Measure($null, $null, $null, $null, 0, 1, 1, 0, 0, $false, 6)
Check "  nothing to measure: a problem, nothing thrown" ("{0} '{1}'" -f $none.Measured, $none.Problem) "False 'no mesh'"

# ---- the fits remembered between loads (Glass\RoomFitCache.cs) -------------------------------
$rc = $asm.GetType('RealGlass.Glass.RoomFitCache', $true)
$cache = [Activator]::CreateInstance($rc)
$cache.Put("tower", "v1 16 s1 i24", $tower)
$cache.Put("round", "v1 160 s1 i240", $round)
$cache.Put("broken", "v1 0", $none)
$text = $cache.Format()
$skipped = 0; $reason = ""
$back = $rc::Parse($text, [ref]$skipped, [ref]$reason)
$t2 = $null
$hit = $back.TryGet("tower", "v1 16 s1 i24", [ref]$t2)
Check "Fits remembered: written and read back, three assets, nothing skipped" ("{0} {1} '{2}' {3}" -f $back.Count, $skipped, $reason, $hit) "3 0 '' True"
$same = ($t2.Storey -eq $tower.Storey) -and ($t2.FloorPhase -eq $tower.FloorPhase) -and ($t2.GroundTop -eq $tower.GroundTop) -and ($t2.WindowBand -eq $tower.WindowBand) -and
        ($t2.Sides[0].Width -eq $tower.Sides[0].Width) -and ($t2.Sides[0].Phase -eq $tower.Sides[0].Phase) -and ($t2.Sides[8].Module -eq $tower.Sides[8].Module)
Check "  every number exactly as measured; the counts made again" ("{0} {1} {2}" -f $same, $t2.SidesFound, $t2.SidesNarrow) ("True {0} {1}" -f $tower.SidesFound, $tower.SidesNarrow)
$r2 = $null; [void]$back.TryGet("round", "v1 160 s1 i240", [ref]$r2); $n2 = $null; [void]$back.TryGet("broken", "v1 0", [ref]$n2)
Check "  the round tower's narrow sides; the unmeasured one still a problem" ("{0} {1} '{2}'" -f $r2.SidesNarrow, $n2.Measured, $n2.Problem) "16 False 'no mesh'"
$miss = $back.TryGet("tower", "v1 16 s1 i30", [ref]$t2)
Check "  another model (its fingerprint): not remembered -- measured again" $miss "False"
$cacheDe = [Activator]::CreateInstance($rc)
$cacheDe.Put("tower", "v1 16 s1 i24", $tower); $cacheDe.Put("round", "v1 160 s1 i240", $round); $cacheDe.Put("broken", "v1 0", $none)
$textDe = [FitTest]::InvokeIn("de-DE", $rc.GetMethod("Format"), $cacheDe, $null)
$parseArgs = [object[]]@($textDe, 0, "")
$backDe = [FitTest]::InvokeIn("de-DE", $rc.GetMethod("Parse"), $null, $parseArgs)
Check "  in a German Windows (decimal comma): the same text, read back whole" ("{0} {1} {2}" -f ($textDe -ceq $text), $backDe.Count, $parseArgs[1]) "True 3 0"
$old = $text -replace "RealGlassRoomFits 1", "RealGlassRoomFits 2"
$o = $rc::Parse($old, [ref]$skipped, [ref]$reason)
Check "  a file of another format: not used (all measured again)" ("{0} '{1}'" -f $o.Count, $reason) "0 'format 2, not 1'"
$f = $rc::Parse("<RealGlass version=`"1`"/>", [ref]$skipped, [ref]$reason)
Check "  not a file of fits: not used" ("{0} '{1}'" -f $f.Count, $reason) "0 'not a file of fits'"
$lines = $text.Split("`n")
$bad = @(); foreach ($l in $lines) { if ($l.StartsWith("tower")) { $bad += ($l -replace "^tower\t", "tower2`t" -replace "\t[0-9.E-]+ ", "`tNaN ") } elseif ($l.StartsWith("round")) { $bad += "round`tonly two parts" } else { $bad += $l } }
$b = $rc::Parse(($bad -join "`n"), [ref]$skipped, [ref]$reason)
Check "  a line with NaN and a cut line: skipped, the rest read" ("{0} {1} '{2}'" -f $b.Count, $skipped, $reason) "1 2 ''"
$threw = $false
try { foreach ($junk in @("", "RealGlassRoomFits", "RealGlassRoomFits 1`n`t`t`t`t", "RealGlassRoomFits 1`nx`ty`t`t1 2 3`t", [string]::new([char]0, 50))) { [void]$rc::Parse($junk, [ref]$skipped, [ref]$reason) } } catch { $threw = $true }
Check "  junk of every kind: nothing thrown" $threw "False"

# ---- the vehicles' glass (Glass\VehicleGlassLook.cs) ---------------------------------------
# Each vehicle's own number (Seed) from its id as the game gives it (InstanceID.RawData, IL: a moving
# vehicle 0x02000000 + its index, a parked one 0x07000000 + its own): the share of them with tinted
# windows the slider asks for, and each tinted car its own shade -- VehicleGlass.shader's
# frac(seed x 97.13) between the lightest and the darkest film. (A float hash of these ids gave
# every moving car one of four numbers: 25% tinted at 5%, every one the same shade.)
$look = $asm.GetType('RealGlass.Glass.VehicleGlassLook', $true)
$vehicleSeeds = @{}
$outside = 0
foreach ($kind in @(@{ Name = 'moving'; Base = [uint32]0x02000000; Count = 16383 }, @{ Name = 'parked'; Base = [uint32]0x07000000; Count = 32767 })) {
    $list = New-Object 'System.Collections.Generic.List[single]'
    for ($k = 1; $k -le $kind.Count; $k++) {
        $s = $look::Seed([uint32]($kind.Base -bor [uint32]$k))
        if ($s -lt 0 -or $s -ge 1) { $outside++ }
        $list.Add($s)
    }
    $vehicleSeeds[$kind.Name] = $list
}
$distinct = New-Object 'System.Collections.Generic.HashSet[single]'
$all = 0
foreach ($list in $vehicleSeeds.Values) { foreach ($s in $list) { [void]$distinct.Add($s); $all++ } }
Check "vehicles: each its own number, 0..1 (16383 moving, 32767 parked: over 99% different)" ("{0} {1}" -f $outside, ($distinct.Count -gt 0.99 * $all)) "0 True"
foreach ($name in @('moving', 'parked')) {
    $list = $vehicleSeeds[$name]
    $at5 = 0; $at30 = 0; $tenths = New-Object int[] 10
    foreach ($s in $list) {
        if ($s -lt 0.3) { $at30++ }
        if ($s -lt 0.05) { $at5++; $tenths[[int][Math]::Floor((($s * 97.13) % 1.0) * 10.0)]++ }
    }
    $p5 = 100.0 * $at5 / $list.Count; $p30 = 100.0 * $at30 / $list.Count
    $fewest = ($tenths | Measure-Object -Minimum).Minimum
    Check "  $name`: tinted at 5% and 30% as asked (to half a point, to a point)" ("{0} {1}" -f ([Math]::Abs($p5 - 5) -le 0.5), ([Math]::Abs($p30 - 30) -le 1)) "True True"
    Check "  $name`: the tinted cars' shades spread over the film's range (every tenth of it at least half its share)" ($fewest -ge $at5 / 20) "True"
}

Write-Host ""
if ($failed -eq 0) { Write-Host "all passed" } else { Write-Host "$failed FAILED"; exit 1 }
