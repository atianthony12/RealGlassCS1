using System;
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
            if (_patched)
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
            foreach (Type type in types)
            {
                // Every class here, at startup, the props' hooks too. Never later: applied in the
                // middle of a city, on the picker's first use, the game's text stopped drawing in
                // that frame and the game crashed seconds after (2026-11-13, the one run of it).
                if (!type.IsDefined(typeof(HarmonyPatch), false))
                    continue;

                if (Apply(harmony, type))
                    applied++;
            }

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
