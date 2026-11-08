using System;
using ColossalFramework;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// The always-on lines about the machine and the glass, written when a city loads.
    /// </summary>
    /// <remarks>
    /// This mod's cost is proportional to the glass PIXELS on screen and to the reflection trace's
    /// steps, and it will first be measured on one GPU (an RTX 5070 Ti, where a city is
    /// simulation-bound and much looks free). Every log a player sends should carry what a
    /// quality choice needs: the resolution, the card, and what they run the game at.
    ///
    /// The game's own graphics levels are read for the record and deliberately NOT used to choose
    /// anything: its defaults are the top of the range, so a player who never opened the graphics
    /// options reads as "high-end" whatever is in the machine.
    /// </remarks>
    public static class SystemReport
    {
        /// <summary>The machine's lines are written once a launch; the settings' lines every city.</summary>
        private static bool _machineWritten;

        public static void Write()
        {
            try
            {
                if (!_machineWritten)
                {
                    _machineWritten = true;

                    Log.Msg("system: " + Screen.width + "x" + Screen.height +
                            (Screen.fullScreen ? " fullscreen" : " windowed") +
                            " | RAM " + SystemInfo.systemMemorySize + " MB" +
                            " | GPU '" + SystemInfo.graphicsDeviceName + "' " + SystemInfo.graphicsMemorySize + " MB, " +
                            SystemInfo.graphicsDeviceVersion +
                            " | Unity " + Application.unityVersion +
                            ", colour space " + QualitySettings.activeColorSpace);

                    Log.Msg("game graphics: shadows=" + GameSetting(global::Settings.shadowsQuality) +
                            " shadowDistance=" + GameSetting(global::Settings.shadowsDistance) +
                            " textures=" + GameSetting(global::Settings.texturesQuality) +
                            " levelOfDetail=" + GameSetting(global::Settings.levelOfDetail) +
                            " antialiasing=" + GameSetting(global::Settings.antialiasing) +
                            " anisotropic=" + GameSetting(global::Settings.anisotropicFiltering) +
                            " dof=" + GameSetting(global::Settings.dofMode) +
                            "  (read for the record only)");
                }

                // Every city (a second city in one session may run on other settings): every switch that can suppress or replace what is drawn, whatever the logging
                // level: the mask view above all (it paints buildings magenta). This is Volumetric
                // Weather's DebugSkipDrawLight lesson: a switch that outlived its checkbox hid every
                // vehicle light for a whole session.
                Log.Msg("features: glass=" + On(Settings.GlassEnabled, Settings.Defaults.GlassEnabled) +
                        " maskView=" + On(Settings.GlassShowMask, Settings.Defaults.GlassShowMask) +
                        " distance=" + DistanceName(Int(Settings.GlassDistance, Settings.Defaults.GlassDistance)) +
                        " | preset=" + GlassPresets.LogName(Int(Settings.GlassPreset, Settings.Defaults.GlassPreset)) +
                        " colour=" + Hex(Settings.GlassColor, Settings.Defaults.GlassColor) +
                        " ownColour=" + Percent(Settings.GlassOwnColour, Settings.Defaults.GlassOwnColour) +
                        " paintedDetail=" + Percent(Settings.GlassPaintedDetail, Settings.Defaults.GlassPaintedDetail) +
                        " reflectivity=" + Percent(Settings.GlassReflectivity, Settings.Defaults.GlassReflectivity) +
                        " glancing=" + Percent(Settings.GlassGlancing, Settings.Defaults.GlassGlancing) +
                        " silveryEdgeOn=" + Percent(Settings.GlassEdgeSilver, Settings.Defaults.GlassEdgeSilver) +
                        " clarity=" + Percent(Settings.GlassClarity, Settings.Defaults.GlassClarity) +
                        " strength=" + Percent(Settings.GlassStrength, Settings.Defaults.GlassStrength) +
                        " threshold=" + Percent(Settings.GlassThreshold, Settings.Defaults.GlassThreshold) +
                        " nightWindows=" + On(Settings.GlassNightWindows, Settings.Defaults.GlassNightWindows) +
                        " lodGlassFromModel=" + On(Settings.LodGlassFromModel, Settings.Defaults.LodGlassFromModel) +
                        " offOn=" + Glass.GlassKinds.Describe(Glass.GlassKinds.OffNow()) +
                        " | reflections: screen=" + On(Settings.SsrEnabled, Settings.Defaults.SsrEnabled) +
                        " finishedPicture=" + On(Settings.ReflectFinished, Settings.Defaults.ReflectFinished) +
                        " neighbours=" + On(Settings.ReflectNeighbours, Settings.Defaults.ReflectNeighbours) +
                        " steadyGlints=" + On(Settings.SteadyGlints, Settings.Defaults.SteadyGlints) +
                        " quality=" + QualityName(Int(Settings.SsrQuality, Settings.Defaults.SsrQuality)) +
                        " sky=" + (Int(Settings.SkyReflection, Settings.Defaults.SkyReflection) == 0 ? "live" : "game's map") +
                        " skyClouds=" + On(Settings.SkyClouds, Settings.Defaults.SkyClouds) +
                        " skyDusk=" + Percent(Settings.SkyDusk, Settings.Defaults.SkyDusk) +
                        " offscreen=" + FillName(Int(Settings.OffscreenFill, Settings.Defaults.OffscreenFill)) +
                        " skyCube=" + Settings.SkyCubePixels + "px x" + Int(Settings.SkyCubeRate, Settings.Defaults.SkyCubeRate) + "/frame" +
                        " | panes: storey=" + (Settings.FloorHeight != null ? Settings.FloorHeight.value : Settings.Defaults.FloorHeight).ToString("F1") + "m" +
                        " weather=" + On(Settings.PaneWeather, Settings.Defaults.PaneWeather) +
                        " followWindows=" + On(Settings.PanesFollowWindows, Settings.Defaults.PanesFollowWindows) +
                        " | rooms=" + On(Settings.InteriorsEnabled, Settings.Defaults.InteriorsEnabled) +
                        " byDay=" + On(Settings.InteriorsByDay, Settings.Defaults.InteriorsByDay) +
                        " dayOpacity=" + Percent(Settings.InteriorsDayOpacity, Settings.Defaults.InteriorsDayOpacity) +
                        " depth=" + Percent(Settings.InteriorsDepth, Settings.Defaults.InteriorsDepth) +
                        " matchLods=" + On(Settings.InteriorsMatchLods, Settings.Defaults.InteriorsMatchLods) +
                        " lit=" + Percent(Settings.InteriorsLit, Settings.Defaults.InteriorsLit) +
                        " lateNight=" + On(Settings.InteriorsLateNight, Settings.Defaults.InteriorsLateNight) +
                        " switching=" + Percent(Settings.InteriorsSwitching, Settings.Defaults.InteriorsSwitching) +
                        " brightness=" + Percent(Settings.InteriorsBrightness, Settings.Defaults.InteriorsBrightness) +
                        " coloured=" + Percent(Settings.InteriorsColoured, Settings.Defaults.InteriorsColoured) +
                        " colouredAtWork=" + Percent(Settings.InteriorsColouredWork, Settings.Defaults.InteriorsColouredWork) +
                        " curtains=" + Percent(Settings.InteriorsCurtains, Settings.Defaults.InteriorsCurtains) +
                        " | vehicles=" + On(Settings.VehicleGlass, Settings.Defaults.VehicleGlass) +
                        " tinted=" + Percent(Settings.VehicleTinted, Settings.Defaults.VehicleTinted) +
                        " reflections=" + Percent(Settings.VehicleReflections, Settings.Defaults.VehicleReflections) +
                        " coat=" + Percent(Settings.VehicleCoat, Settings.Defaults.VehicleCoat));

                Log.Msg("panel key: " + (Settings.PanelKey != null ? SettingsXmlFormat.FormatKey(Settings.PanelKey.value) : "?") +
                        ", icon place=" + ModController.IconPlaceName(Settings.IconPlace != null ? Settings.IconPlace.value : Settings.Defaults.IconPlace) +
                        ", panel look=" + UI.PanelLook.Skin +
                        ", advanced tabs=" + On(Settings.ShowAdvancedInPanel, Settings.Defaults.ShowAdvancedInPanel) +
                        " | building picker key: " + (Settings.PickerKey != null ? SettingsXmlFormat.FormatKey(Settings.PickerKey.value) : "?") +
                        " | " + PerAsset.AssetFile.Count + " building asset" + (PerAsset.AssetFile.Count == 1 ? "" : "s") +
                        " with glass of their own (" + PerAsset.AssetFile.FileName + ")");

                // Asked here, not inside: the method names Harmony's types, and without the Harmony
                // mod merely compiling it would fail (IsPatched names none).
                if (Patcher.IsPatched)
                    Patcher.LogSharedMethods();
                else
                    Log.Warn("harmony: the patches are NOT applied (is the Harmony mod enabled?); the glass cannot be drawn");

                if (Patcher.Failed.Length > 0)
                    Log.Warn("harmony: patch classes that failed: " + Patcher.Failed +
                             (Patcher.GlassPatched ? " (the glass's own hooks are in)" : "; the glass cannot be drawn"));
            }
            catch (Exception e)
            {
                Log.Warn("Could not write the system report: " + e.Message);
            }
        }

        /// <summary>
        /// One of the game's own graphics levels. They live as SavedInts in the gameSettings file
        /// under key names the game publishes as public statics, so nothing is guessed.
        /// </summary>
        private static string GameSetting(string key)
        {
            try
            {
                if (GameSettings.FindSettingsFileByName(global::Settings.gameSettingsFile) == null)
                    return "?";

                return new SavedInt(key, global::Settings.gameSettingsFile, -1, false).value.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>The distance tiers' names for the LOG, in English.</summary>
        public static string DistanceName(int value)
        {
            switch (value)
            {
                case 0: return "near only";
                case 1: return "near+middle";
                case 2: return "whole city";
                default: return "unknown (" + value + ")";
            }
        }

        /// <summary>The trace qualities' names for the LOG, in English.</summary>
        public static string QualityName(int value)
        {
            switch (value)
            {
                case 0: return "Low";
                case 1: return "Medium";
                case 2: return "High";
                case 3: return "Ultra";
                default: return "unknown (" + value + ")";
            }
        }

        /// <summary>The off-screen fills' names for the LOG, in English.</summary>
        public static string FillName(int value)
        {
            switch (value)
            {
                case 0: return "mirrored screen";
                case 1: return "city colour";
                case 2: return "sky only";
                default: return "unknown (" + value + ")";
            }
        }

        private static string On(BoolSetting setting, bool fallback)
        {
            return (setting != null ? setting.value : fallback) ? "on" : "off";
        }

        private static int Int(IntSetting setting, int fallback)
        {
            return setting != null ? setting.value : fallback;
        }

        private static string Percent(FloatSetting setting, float fallback)
        {
            return ((setting != null ? setting.value : fallback) * 100f).ToString("F0") + "%";
        }

        private static string Hex(ColorSetting setting, Color32 fallback)
        {
            return ColorText.Format(setting != null ? setting.value : fallback);
        }
    }
}
