# Offline check of the game-update safeguard (RealGlass/GameUpdate.cs on ModsLibCS1's GameCheck),
# run against the BUILT mod DLL and the INSTALLED game's DLLs, no game needed (the same test as
# the other mods' with the safeguard, with this mod's names):
#   - Real Glass's requirement list (GameUpdate.Require) finds everything in this game: 0 problems
#     (after a game update, this is the first thing to run: it names what changed);
#   - every [HarmonyPatch] class's target in the DLL is among what Require checked (a patch added
#     without its line in Require fails here); Intersection Marking Tool's (another mod's) is left out;
#   - GAMECHECK_FAIL=Real Glass & Water adds exactly one problem (the forced switch-off for testing),
#     another mod's name none;
#   - GameCheck itself, and GameUpdate's by-name binding test, report what they should.
#
#   dotnet build RealGlass/RealGlass.csproj -p:Deploy=false
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-gamecheck.ps1
#
# Not covered, because it needs the game: the switch-off itself (the pop-up, the Loader standing
# down, the transpilers' counts, the patches undone, the save keys left as they were).

param(
    [string]$Dll = (Join-Path $PSScriptRoot "..\RealGlass\bin\Debug\RealGlass.dll")
)

$ErrorActionPreference = "Stop"
$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$libs = Join-Path $PSScriptRoot "..\libs"

# Every assembly from its bytes (Smart App Control blocks LoadFrom of a fresh unsigned DLL), found by name.
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class RgGcResolver
{
    public static string[] Dirs;
    static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    public static void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    public static Assembly Load(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        Assembly a;
        if (Loaded.TryGetValue(name, out a)) return a;
        a = Assembly.Load(File.ReadAllBytes(path));
        Loaded[name] = a;
        return a;
    }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        Assembly a;
        if (Loaded.TryGetValue(name, out a)) return a;
        foreach (string dir in Dirs)
        {
            string path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path)) return Load(path);
        }
        return null;
    }
}
"@

[RgGcResolver]::Dirs = @($managed, (Split-Path $Dll), $libs)
[RgGcResolver]::Install()
foreach ($n in "Assembly-CSharp", "ColossalManaged", "ICities", "UnityEngine") { [void][RgGcResolver]::Load((Join-Path $managed "$n.dll")) }
$mod = [RgGcResolver]::Load((Resolve-Path $Dll).Path)

$checkType = $mod.GetType("ModsLibCS1.GameCheck", $true)
$update = $mod.GetType("RealGlass.GameUpdate", $true)
$require = $update.GetMethod("Require")
$asm = [RgGcResolver]::Load((Join-Path $managed "Assembly-CSharp.dll"))

$failed = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { "ok    $what" } else { "FAIL  $what -- $detail"; $script:failed++ }
}

$script:lines = New-Object System.Collections.Generic.List[string]
$sink = [Action[string]] { param($s) $script:lines.Add($s) }
function NewCheck { $script:lines.Clear(); return [Activator]::CreateInstance($checkType, @("Real Glass & Water", $sink)) }

# 1. This game has everything.
$env:GAMECHECK_FAIL = $null
$c = NewCheck
[void]$require.Invoke($null, @($c))
Check "the installed game has everything Real Glass & Water needs by name" (-not $c.Failed) ($script:lines -join " | ")
$targets = @($update.GetProperty("Targets").GetValue($null, $null))
"      ($($targets.Count) patch targets checked)"

# 2. Every patch class's target is among them.
function Same([System.Reflection.MethodBase]$m, [string]$type, [string]$name, $params) {
    if ($m.DeclaringType.FullName -ne $type -or $m.Name -ne $name) { return $false }
    if ($params -eq $null) { return $true }
    $have = @($m.GetParameters() | ForEach-Object { $_.ParameterType.FullName })
    if ($have.Count -ne $params.Count) { return $false }
    for ($i = 0; $i -lt $have.Count; $i++) { if ($have[$i] -ne $params[$i]) { return $false } }
    return $true
}
function Covered([string]$type, [string]$name, $params) {
    foreach ($t in $targets) { if (Same $t $type $name $params) { return $t } }
    return $null
}
# A prefix / postfix binds its arguments by NAME and ___m_x to a field: each must be on the target.
function BindsOk($patchType, [System.Reflection.MethodBase]$t) {
    $bad = @()
    $have = @($t.GetParameters() | ForEach-Object { $_.Name })
    foreach ($m in $patchType.GetMethods([Reflection.BindingFlags]"Static, Public, NonPublic")) {
        if ($m.Name -ne "Prefix" -and $m.Name -ne "Postfix") { continue }
        foreach ($p in $m.GetParameters()) {
            if ($p.Name.StartsWith("___")) {
                $f = $null
                for ($d = $t.DeclaringType; $d -ne $null -and $f -eq $null; $d = $d.BaseType) {
                    $f = $d.GetField($p.Name.Substring(3), [Reflection.BindingFlags]"Instance, Static, Public, NonPublic, DeclaredOnly")
                }
                if ($f -eq $null) { $bad += $p.Name }
            }
            elseif (-not $p.Name.StartsWith("__") -and $have -notcontains $p.Name) { $bad += $p.Name }
        }
    }
    return $bad
}

# The bare [HarmonyPatch] classes (TargetMethod / TargetMethods, read by hand: invoking them needs
# Harmony's AccessTools): each target as the class finds it, by parameter count where it has overloads.
$bare = @{
    "RealGlass.Vehicles.VehicleRenderInstancePatch" = @(, @("Vehicle", "RenderInstance", 16))
    "RealGlass.Water.WaterFoamDrawPatch"            = @(@("TerrainPatch", "Render", 1), @("TerrainPatch", "RenderSubPatch", 10))
    "RealGlass.Props.PropImtPatch"                  = @()   # Intersection Marking Tool's: another mod, not checked
}

$classes = 0
foreach ($type in $mod.GetTypes()) {
    $attrs = @($type.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq "HarmonyLib.HarmonyPatch" })
    if ($attrs.Count -eq 0) { continue }
    $classes++
    $declaring = $null; $method = $null; $params = $null; $variations = $null
    foreach ($a in $attrs) {
        foreach ($arg in $a.ConstructorArguments) {
            if ($arg.ArgumentType.FullName -eq "System.Type") { $declaring = $arg.Value }
            elseif ($arg.ArgumentType.FullName -eq "System.String") { $method = $arg.Value }
            elseif ($arg.ArgumentType.FullName -eq "System.Type[]") { $params = @($arg.Value | ForEach-Object { $_.Value }) }
            elseif ($arg.ArgumentType.FullName -eq "HarmonyLib.ArgumentType[]") { $variations = @($arg.Value | ForEach-Object { [int]$_.Value }) }
        }
    }

    if ($declaring -eq $null) {
        if (-not $bare.ContainsKey($type.FullName)) { Check "$($type.Name): a bare [HarmonyPatch] class listed here" $false "add its targets to `$bare (and to GameUpdate.Require)"; continue }
        foreach ($b in $bare[$type.FullName]) {
            $ok = $false
            foreach ($t in $targets) { if ($t.DeclaringType.Name -eq $b[0] -and $t.Name -eq $b[1] -and $t.GetParameters().Length -eq $b[2]) { $ok = $true } }
            Check "$($type.Name) -> $($b[0]).$($b[1]) checked" $ok "not among GameUpdate.Require's targets"
        }
        continue
    }

    $names = $null
    if ($params -ne $null) {
        $names = @()
        for ($i = 0; $i -lt $params.Count; $i++) {
            $p = $params[$i]
            # ArgumentType: 0 Normal, 1 Ref, 2 Out (both by-ref), 3 Pointer.
            if ($variations -ne $null -and $i -lt $variations.Count) {
                if ($variations[$i] -eq 1 -or $variations[$i] -eq 2) { $p = $p.MakeByRefType() }
                elseif ($variations[$i] -eq 3) { $p = $p.MakePointerType() }
            }
            $names += $p.FullName
        }
    }
    $found = Covered $declaring.FullName $method $names
    Check "$($type.Name) -> $($declaring.Name).$method checked" ($found -ne $null) "not among GameUpdate.Require's targets"
    if ($found -ne $null) {
        $bad = @(BindsOk $type $found)
        Check "$($type.Name): every argument it binds is on the target" ($bad.Count -eq 0) ("missing: " + ($bad -join ", "))
    }
}
Check "patch classes found in the DLL: $classes (each checked above)" ($classes -gt 0) "none: is it the right DLL?"

# 3. The forced failure: exactly one problem.
$env:GAMECHECK_FAIL = "real glass & water"
$c = NewCheck
[void]$require.Invoke($null, @($c))
$env:GAMECHECK_FAIL = $null
Check "GAMECHECK_FAIL=Real Glass & Water: one problem" ($c.Failed -and $c.Problems.Count -eq 1) ("$($c.Problems.Count): " + ($script:lines -join " | "))
$env:GAMECHECK_FAIL = "Another Mod"
$c = NewCheck
[void]$require.Invoke($null, @($c))
$env:GAMECHECK_FAIL = $null
Check "GAMECHECK_FAIL naming another mod: none" (-not $c.Failed) ($script:lines -join " | ")

# 4. GameUpdate's own name test: Harmony binds by NAME, wherever the parameter stands.
$inst = [Reflection.BindingFlags]"Instance, Public, NonPublic"
$stat = [Reflection.BindingFlags]"Static, Public, NonPublic"
$target = $update.GetMethod("Target", $stat)
$camera = $asm.GetType("RenderManager+CameraInfo", $true)
$buildingAI = $asm.GetType("BuildingAI", $true)
$sub = [Type[]]@($camera, $asm.GetType("Building", $true).MakeByRefType(), $asm.GetType("BuildingInfo", $true), $asm.GetType("BuildingInfoBase", $true),
                 [UnityEngine.Matrix4x4], $asm.GetType("RenderManager+Instance", $true).MakeByRefType(), [bool])

$c = NewCheck
[void]$target.Invoke($null, @($c, $buildingAI, "RenderMesh", $stat, $sub, [string[]]@("subInfo", "instance")))
Check "a bound name found away from its place is fine" (-not $c.Failed) ($script:lines -join " | ")

$c = NewCheck
[void]$target.Invoke($null, @($c, $buildingAI, "RenderMesh", $stat, $sub, [string[]]@("cameraInfo", "subMesh")))
Check "a bound name the method lacks is a problem" ($c.Problems.Count -eq 1 -and $script:lines[0] -match "'subMesh'") ($script:lines -join " | ")

$c = NewCheck
[void]$target.Invoke($null, @($c, $asm.GetType("PropManager", $true), "EndRenderingImpl", ($inst -bor [Reflection.BindingFlags]::DeclaredOnly), [Type[]]@($camera), [string[]]@()))
Check "an override declared on the type is found" (-not $c.Failed) ($script:lines -join " | ")

$c = NewCheck
[void]$target.Invoke($null, @($c, $asm.GetType("PropManager", $true), "ToString", ($inst -bor [Reflection.BindingFlags]::DeclaredOnly), [Type[]]@(), [string[]]@()))
Check "a method only a base declares is a problem with DeclaredOnly" ($c.Failed) ($script:lines -join " | ")

# 5. GameCheck's own reports.
$propTool = $asm.GetType("PropTool", $true)
$renderManager = $asm.GetType("RenderManager", $true)

$c = NewCheck
[void]$c.RequireField($renderManager, "m_noSuchField", $stat, $null)
Check "a missing field is a problem" ($c.Problems.Count -eq 1) ""

$c = NewCheck
[void]$c.RequireField($asm.GetType("ExceptionPanel", $true), "m_Binding", [Reflection.BindingFlags]"Instance, NonPublic", [int])
Check "a field of another type is a problem" ($c.Problems.Count -eq 1 -and $script:lines[0] -match "is a") ($script:lines -join " | ")

$c = NewCheck
[void]$c.RequireMethod($propTool, "RenderOverlay", $stat, [Type[]]@([float]), [string[]]@())
Check "other parameter types are a problem" ($c.Problems.Count -eq 1) ($script:lines -join " | ")

$c = NewCheck
$ok1 = $c.Patch("Good", [Action] { })
$ok2 = $c.Patch("Bad", [Action] { throw (New-Object System.MissingMethodException "BuildingAI", "Gone") })
$ok3 = $c.Patch("After", [Action] { })
$script:undone = 0
$u1 = $c.UndoIfFailed([Action] { $script:undone++ })
$u2 = $c.UndoIfFailed([Action] { $script:undone++ })
Check "a patch that throws is a problem; none applied after it; undone once" ($ok1 -and -not $ok2 -and -not $ok3 -and $u1 -and $u2 -and $script:undone -eq 1 -and $c.Problems.Count -eq 1) ("$ok1 $ok2 $ok3 $u1 $u2 $($script:undone) | " + ($script:lines -join " | "))

$c = NewCheck
$script:undone = 0
Check "nothing failed: nothing undone" (-not $c.UndoIfFailed([Action] { $script:undone++ }) -and $script:undone -eq 0) ""

""
if ($failed -gt 0) { "$failed FAILED"; exit 1 } else { "all passed" }
