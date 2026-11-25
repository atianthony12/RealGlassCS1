using System;
using System.Collections.Generic;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// Harmony bootstrap. The CitiesHarmony mod provides the actual Harmony assembly at runtime,
    /// so patching is deferred until it reports ready rather than assumed. Every [HarmonyPatch]
    /// class in this assembly is applied, each on its own so one failure cannot stop the rest.
    /// Today they are all the glass's draw hooks (namespace RealGlass.Glass).
    /// </summary>
    /// <remarks>
    /// CitiesHarmony's Harmony is 2.0.4, which writes an <c>object[] __args</c> back over every
    /// ref argument after the patch runs (RealFlag found Move It's clicks broken by one): never
    /// take __args in a patch here.
    /// </remarks>
    public static class Patcher
    {
        public const string HarmonyId = "realglass.glass";

        private static bool _patched;
        private static string _failed = "";
        private static bool _glassFailed;
        private static bool _propsFailed;

        /// <summary>At least one patch class applied (so there is something to list and to unpatch).</summary>
        public static bool IsPatched => _patched;

        /// <summary>Every patch class of the glass's draw hooks (namespace RealGlass.Glass) applied.</summary>
        public static bool GlassPatched => _patched && !_glassFailed;

        /// <summary>
        /// A patch class of the props' draw hooks (namespace RealGlass.Props) could not be applied: the
        /// props' glass and water cannot be drawn whole (GlassController.UpdateProps keeps them off and
        /// says so). Not another mod's own hook (PropImtPatch): without it only that mod's props go without.
        /// </summary>
        public static bool PropsFailed => _propsFailed;

        /// <summary>The patch classes that failed, comma-separated; empty when none did.</summary>
        public static string Failed => _failed;

        public static void PatchOnReady()
        {
            try
            {
                HarmonyHelper.DoOnHarmonyReady(Patch);
            }
            catch (Exception e)
            {
                Log.Error("Could not schedule Harmony patching.", e);
            }
        }

        /// <summary>Prompts the player to subscribe if the Harmony mod is missing.</summary>
        public static void EnsureHarmony()
        {
            try
            {
                if (!HarmonyHelper.IsHarmonyInstalled)
                {
                    Log.Warn("Harmony is NOT installed; the glass cannot be drawn without it.");
                    HarmonyHelper.EnsureHarmonyInstalled();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static void Patch()
        {
            // Switched off by the game check (GameUpdate): nothing of ours is patched.
            if (_patched || GameUpdate.Off)
                return;

            // One class at a time, not PatchAll: PatchAll stops at the first class that throws, and
            // a patch on the game's debug panel (removed since) once took every glass hook down
            // with it (2026-09-28).
            Harmony harmony;
            Type[] types;
            try
            {
                harmony = new Harmony(HarmonyId);
                types = typeof(Patcher).Assembly.GetTypes();
            }
            catch (Exception e)
            {
                Log.Error("Harmony patching failed; the glass will not be drawn.", e);
                return;
            }

            int applied = 0;
            _failed = "";
            _glassFailed = false;
            _propsFailed = false;
            _vehiclesTaken = false;
            _waterTaken = false;
            ModsLibCS1.GameCheck check = GameUpdate.Current;
            Type otherMods = null;
            foreach (Type type in types)
            {
                // Every class here, at startup, the props' hooks too. Never later: applied in the
                // middle of a city, on the picker's first use, the game's text stopped drawing in
                // that frame and the game crashed seconds after (2026-11-13, the one run of it).
                if (!type.IsDefined(typeof(HarmonyPatch), false))
                    continue;

                // Another mod's method (Intersection Marking Tool's): not the game's, so not the
                // check's -- applied after it, on its own, and only if the check held.
                if (type == typeof(Props.PropImtPatch))
                {
                    otherMods = type;
                    continue;
                }

                // Through the game check (GameUpdate): one class that will not apply, or a
                // transpiler that finds nothing to change, is the game changed under the mod --
                // nothing applied after it, every patch of ours undone, the mod off. Never
                // half-patched.
                if (check != null ? ApplyChecked(check, harmony, type) : Apply(harmony, type))
                    applied++;
            }

            if (check != null && check.UndoIfFailed(() => harmony.UnpatchAll(HarmonyId)))
            {
                Log.Msg("Harmony patches removed (" + HarmonyId + "): the game check failed");
                GameUpdate.PatchFailed();
                return;
            }

            if (otherMods != null && Apply(harmony, otherMods))
                applied++;

            _patched = applied > 0;
            Log.Msg("Harmony patches applied (" + HarmonyId + "): " + applied + " classes" +
                    (_failed.Length > 0 ? ", FAILED: " + _failed : "") +
                    ". Harmony installed=" + HarmonyHelper.IsHarmonyInstalled);
        }

        private const string PropsNamespace = "RealGlass.Props";

        private static bool Apply(Harmony harmony, Type type)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                return true;
            }
            catch (Exception e)
            {
                bool glass = type.Namespace == "RealGlass.Glass";
                bool props = type.Namespace == PropsNamespace && type != typeof(Props.PropImtPatch);
                _glassFailed |= glass;
                _propsFailed |= props;
                _failed += (_failed.Length > 0 ? ", " : "") + type.Name;
                Log.Error("Harmony: " + type.Name + " could not be applied; " +
                          (glass ? "the glass will not be drawn."
                              : props ? "the props' glass and water will not be drawn; the rest is unaffected."
                              : "the rest is unaffected."), e);
                return false;
            }
        }

        // ---- the game check's patching (GameUpdate) --------------------------------------------

        // While a class with a transpiler is applied through the check: what each of its runs
        // changed, by method. Null otherwise, so the re-runs Harmony makes whenever any mod patches
        // one of those methods later are never counted.
        private static List<KeyValuePair<MethodBase, int>> _transpiled;
        private static bool _vehiclesTaken;
        private static bool _waterTaken;

        /// <summary>
        /// Another mod's transpiler had taken the game's vehicle draws before ours ran at start-up (ours
        /// found none, and the game's code had not changed under it): the vehicles' glass stays off this
        /// launch (GlassController), with its error message.
        /// </summary>
        public static bool VehiclesTaken => _vehiclesTaken;

        /// <summary>The same for the water's draws (the foam's hook): the realistic water stays off this launch (WaterController).</summary>
        public static bool WaterTaken => _waterTaken;

        /// <summary>
        /// From each of our game transpilers, every run: how many places it changed in that method
        /// (0 on its catch path, which hands the code back as it came). Kept only during the class's
        /// first application through the check. Never throws (it runs inside Harmony's patching).
        /// </summary>
        internal static void Transpiled(MethodBase original, int changed)
        {
            try
            {
                List<KeyValuePair<MethodBase, int>> runs = _transpiled;
                if (runs != null)
                    runs.Add(new KeyValuePair<MethodBase, int>(original, changed));
            }
            catch
            {
                // nothing recorded: the check finds no report and says so
            }
        }

        /// <summary>
        /// One class through the game check. A class with a transpiler is read as it is applied: a
        /// target where ours changed nothing is a problem of the check -- unless another mod's
        /// transpiler sits on that method already (ours runs last, on its output: then the 0 is that
        /// mod's doing, not the game's, and only that part goes off with its message, as when a part
        /// fails).
        /// </summary>
        private static bool ApplyChecked(ModsLibCS1.GameCheck check, Harmony harmony, Type type)
        {
            bool transpiles = type.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null;
            return check.Patch(type.Name, () =>
            {
                List<KeyValuePair<MethodBase, int>> runs = transpiles ? new List<KeyValuePair<MethodBase, int>>() : null;
                _transpiled = runs;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                finally
                {
                    _transpiled = null;
                }

                if (transpiles)
                    CheckTranspiled(check, type, runs);
            });
        }

        private static void CheckTranspiled(ModsLibCS1.GameCheck check, Type type, List<KeyValuePair<MethodBase, int>> runs)
        {
            if (runs.Count == 0)
            {
                check.Problem("the patch " + type.Name + "'s transpiler reported nothing when it was applied");
                return;
            }

            var seen = new List<MethodBase>();
            foreach (KeyValuePair<MethodBase, int> run in runs)
            {
                MethodBase method = run.Key;
                if (method != null && seen.Contains(method))
                    continue;   // the first run is the one that met the code as it stood
                seen.Add(method);
                if (run.Value > 0)
                    continue;

                string where = method != null ? method.DeclaringType.Name + "." + method.Name : "?";
                string others = method != null ? OtherTranspilers(method) : "";
                if (others.Length == 0)
                {
                    check.Problem("the patch " + type.Name + " found nothing to change in " + where);
                    continue;
                }

                bool water = type.Namespace == "RealGlass.Water";
                if (water)
                    _waterTaken = true;
                else
                    _vehiclesTaken = true;
                ErrorNotice.Report(water ? ErrorNotice.Water : ErrorNotice.Vehicles);
                Log.Warn("Harmony: " + type.Name + " found nothing to change in " + where + ", which " + others +
                         " transpiles before ours: that mod's doing, not the game's. " +
                         (water ? "The realistic water" : "The vehicles' glass") + " stays off; the rest of the mod runs");
            }
        }

        /// <summary>Every other Harmony ID with a transpiler on the method, comma-separated; empty when none.</summary>
        private static string OtherTranspilers(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null)
                    return "";
                string others = "";
                foreach (HarmonyLib.Patch patch in info.Transpilers)
                {
                    if (patch.owner != HarmonyId && others.IndexOf(patch.owner, StringComparison.Ordinal) < 0)
                        others += (others.Length > 0 ? ", " : "") + patch.owner;
                }
                return others;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// One line naming every other Harmony ID that patches a method we patch, written with the
        /// system report. A clash on one of these methods is the likeliest way another mod and ours
        /// break each other, and without this line a report cannot say which mod. Only Harmony
        /// patches are visible here: an old mod that detours a method by hand does not show up.
        /// </summary>
        public static void LogSharedMethods()
        {
            if (!_patched)
                return;

            try
            {
                string ours = "";
                string shared = "";

                foreach (MethodBase method in new Harmony(HarmonyId).GetPatchedMethods())
                {
                    ours += (ours.Length > 0 ? ", " : "") + method.DeclaringType.Name + "." + method.Name;

                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null)
                        continue;

                    string others = "";
                    foreach (string owner in info.Owners)
                    {
                        if (owner != HarmonyId)
                            others += (others.Length > 0 ? ", " : "") + owner;
                    }

                    if (others.Length > 0)
                        shared += (shared.Length > 0 ? "; " : "") + method.DeclaringType.Name + "." + method.Name + " by " + others;
                }

                Log.Msg("harmony: we patch " + (ours.Length > 0 ? ours : "nothing") +
                        "; other mods patching the same methods: " + (shared.Length > 0 ? shared : "none"));
            }
            catch (Exception e)
            {
                Log.Warn("Could not list other mods' patches: " + e.Message);
            }
        }

        public static void Unpatch()
        {
            if (!_patched)
                return;

            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Msg("Harmony patches removed.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            _patched = false;
            _failed = "";
            _glassFailed = false;
            _propsFailed = false;
        }
    }
}
