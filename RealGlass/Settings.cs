using System;
using ColossalFramework;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// Persisted options, kept in RealGlass.xml next to the log (see <see cref="SettingsXml"/>),
    /// where a player can read and edit them.
    /// </summary>
    /// <remarks>
    /// Every default is a named constant in <see cref="Defaults"/> and nowhere else: the
    /// constructors below read them, the renderer's fallbacks read them, and
    /// <see cref="UI.SettingsCatalog"/> carries the same constant on every row, which is what lets
    /// "Reset all settings to defaults" exist at all. A setting does NOT remember its default --
    /// the constructor's value is only what it holds until the file is read -- so the catalog is
    /// the only thing that can put a default back.
    ///
    /// Editing a default does not change a machine that already has RealGlass.xml: the default is
    /// only used when the setting's line is absent. Use the Reset button (or delete that file with
    /// the game closed) to see a first run.
    ///
    /// The first argument of each constructor is the setting's NAME in the file. When what a
    /// setting means changes, give it a new name: a saved value of a slider that meant something
    /// else is garbage, and the file drops a name nothing reads on its next save. Percent rows are
    /// stored as fractions (47% is 0.47f) and written as the slider shows them (47).
    /// </remarks>
    public static class Settings
    {
        /// <summary>
        /// The key's home in the game's settings system: registered with dontSave, so it is never
        /// written and there is no file to load. The key stays a SavedInputKey because Unified UI
        /// takes one; RealGlass.xml is where it is kept. NO DOT in this name: the game's
        /// PathUtils.AddExtension CHANGES an extension that is already there, and the name is read
        /// back from the path without one (Volumetric Weather's first "X.InMemory" became X.cgs).
        /// </summary>
        private const string InMemory = "RealGlassHotkey";

        // ---- the mod itself (options page) ---------------------------------------------------

        /// <summary>
        /// 0: the game's own language. Otherwise one of <see cref="Localization.LanguageCodes"/>,
        /// counted from 1 -- an append-only table, so a saved choice survives new languages.
        /// </summary>
        public static IntSetting Language { get; private set; }

        /// <summary>Opens and closes the in-game panel. F8 by default.</summary>
        public static SavedInputKey PanelKey { get; private set; }

        /// <summary>
        /// Turns the building picker on and off (PerAsset.AssetPicker): click a building, and the
        /// building window gives its asset glass of its own. Alt+G by default.
        /// </summary>
        public static SavedInputKey PickerKey { get; private set; }

        /// <summary>
        /// THE PANELS' LOOK (ModsLibCS1.UI.Skin), stored by VALUE (append only): 0 See-through, 1
        /// Game blue, 2 Vanilla (the panels as they were), 3 Frosted, the default -- Volumetric
        /// Weather's row. The F8 panel, the building window and the HUD button wear it.
        /// </summary>
        public static IntSetting PanelSkin { get; private set; }

        /// <summary>
        /// Where the panel's button goes, stored by VALUE (append only; <see cref="IconPlaces"/>):
        /// 0 Unified UI's bar (when that is there, else the HUD button), 1 the HUD button, 2 none
        /// (the key alone opens the panel). 1.1.7: it replaced the switch "UseUnifiedUI"
        /// (SettingsXml.Retired), whose "false" an older file reads as the HUD button.
        /// </summary>
        public static IntSetting IconPlace { get; private set; }

        /// <summary>The values of <see cref="IconPlace"/>, as stored. Never renumber.</summary>
        public static class IconPlaces
        {
            public const int UnifiedUI = 0;
            public const int Toolbar = 1;
            public const int Never = 2;
        }

        /// <summary>
        /// Writes every periodic line to the log. Off for players: each Log call is an
        /// open-write-close plus a Unity stack trace, which is fine for a ten-minute test session
        /// and wrong for a four-hour one.
        /// </summary>
        public static BoolSetting DetailedLogging { get; private set; }

        /// <summary>
        /// Where the mod's own button sits when it is not in Unified UI: its top-left corner, in
        /// UI units from the top left of the screen. The player drags it there.
        /// </summary>
        public static FloatSetting HudButtonX { get; private set; }

        /// <summary>Hidden: the foam's diagnostic view (0 none, 1 its sources, 2 / 3 where it draws, 4 its layers: WaterFoam.shader).</summary>
        public static FloatSetting WaterFoamView { get; private set; }
        public static FloatSetting HudButtonY { get; private set; }

        /// <summary>
        /// Hidden: the F8 panel's open headings (Row.Group names, comma separated); every other one is
        /// folded away (Volumetric Weather's). Its headings are its UI.
        /// </summary>
        public static StringSetting PanelOpenGroups { get; private set; }

        // ---- F8 -> Glass ---------------------------------------------------------------------

        /// <summary>The master switch: off, nothing of the glass is drawn.</summary>
        public static BoolSetting GlassEnabled { get; private set; }

        /// <summary>
        /// Which real glass (<see cref="GlassPresets"/>): picking one writes GlassColor,
        /// GlassReflectivity and GlassClarity; moving any of those three by hand makes it
        /// <see cref="GlassPresets.Custom"/>.
        /// </summary>
        public static IntSetting GlassPreset { get; private set; }

        /// <summary>0..1: how much of the building's own painted glass colour is kept (1 = all of it).</summary>
        public static FloatSetting GlassOwnColour { get; private set; }

        /// <summary>
        /// 0..1: how much of the lines an asset painted on its glass stay -- floor bands, mullions,
        /// frames: where its colour map stands out from the storey round it. A full-glass tower
        /// (its whole facade marked shiny) keeps its floors.
        /// </summary>
        public static FloatSetting GlassPaintedDetail { get; private set; }

        /// <summary>The glass's tint (sRGB), where the building's own colour is not kept.</summary>
        public static ColorSetting GlassColor { get; private set; }

        /// <summary>0..0.6: reflectance looking straight at the glass (Fresnel's F0).</summary>
        public static FloatSetting GlassReflectivity { get; private set; }

        /// <summary>0..1: the most the glass reflects at a glancing angle.</summary>
        public static FloatSetting GlassGlancing { get; private set; }

        /// <summary>
        /// 0..1: how far a coated pane's reflection loses its colour edge-on. Physics says all the
        /// way (1): the Fresnel rise is the same for every colour. 0 keeps the tint at every angle.
        /// </summary>
        public static FloatSetting GlassEdgeSilver { get; private set; }

        /// <summary>0..1: how much light passes through: how dark the glass looks by day.</summary>
        public static FloatSetting GlassClarity { get; private set; }

        /// <summary>0..1: the glass over the game's own look (0 = the game's windows, 1 = ours).</summary>
        public static FloatSetting GlassStrength { get; private set; }

        /// <summary>0.4..0.95: how shiny a part of an asset must be to count as glass.</summary>
        public static FloatSetting GlassThreshold { get; private set; }

        /// <summary>Windows the asset lights up at night count as glass too.</summary>
        public static BoolSetting GlassNightWindows { get; private set; }

        /// <summary>
        /// An LOD whose glass is not its model's -- none, or elsewhere -- takes its model's on the middle
        /// and far tiers (Glass/LodGlass.cs). An asset can say otherwise (its building window).
        /// </summary>
        public static BoolSetting LodGlassFromModel { get; private set; }

        /// <summary>A check: paints what counts as glass magenta instead of drawing it.</summary>
        public static BoolSetting GlassShowMask { get; private set; }

        // ---- F8 -> Reflections ---------------------------------------------------------------

        /// <summary>Screen-space reflections: the glass reflects what is on screen.</summary>
        public static BoolSetting SsrEnabled { get; private set; }

        /// <summary>
        /// The glass reflects the FINISHED picture (last frame's, after the fog, the clouds, the
        /// lamps' glow and the other glass), put back where it was: not the copy taken before them.
        /// </summary>
        public static BoolSetting ReflectFinished { get; private set; }

        /// <summary>
        /// The glass mirrors the buildings around it where the screen cannot show them: a map of
        /// the city's roof heights round the view, walked by every reflected ray that misses the
        /// screen (Glass/CityMap.cs) -- the towers across a street instead of open sky.
        /// </summary>
        public static BoolSetting ReflectNeighbours { get; private set; }

        /// <summary>0 Low, 1 Medium, 2 High, 3 Ultra: how finely the reflections are traced.</summary>
        public static IntSetting SsrQuality { get; private set; }

        /// <summary>0..3: multiplier on everything reflected.</summary>
        public static FloatSetting ReflectionBrightness { get; private set; }

        /// <summary>0: the live sky (a small cube of the game's own sky, kept up to date). 1: the game's static environment map.</summary>
        public static IntSetting SkyReflection { get; private set; }

        /// <summary>The live sky carries the clouds in the sky (Volumetric Weather's, when it draws them: its volume and its distant clouds).</summary>
        public static BoolSetting SkyClouds { get; private set; }

        /// <summary>
        /// 0..1: how far the sky in the glass follows a clear sky's fall in the sun's last ten degrees
        /// (the game's own sky keeps noon's light until the sun is 3 degrees up): 1 the real fall,
        /// 0.5 half as many stops of it, 0 the game's sky as it is (Glass/GlassUniforms.Dusk).
        /// </summary>
        public static FloatSetting SkyDusk { get; private set; }

        /// <summary>
        /// What a reflection shows below the horizon when nothing on screen is hit: 0 the screen
        /// mirrored at the edge it left by ("smart invert"), 1 the city's average colour, 2 the sky.
        /// </summary>
        public static IntSetting OffscreenFill { get; private set; }

        /// <summary>0..3: multiplier on the sun's glint off the panes.</summary>
        public static FloatSetting SunGlint { get; private set; }

        /// <summary>
        /// Specular anti-aliasing: where the panes' bend changes within a pixel, the glint and the
        /// reflection widen to match instead of flickering (Kaplanyan et al. 2016).
        /// </summary>
        public static BoolSetting SteadyGlints { get; private set; }

        /// <summary>0..1: how sharp the reflections are (1 = a perfect mirror).</summary>
        public static FloatSetting GlassSmoothness { get; private set; }

        /// <summary>0..0.5: how far in from the screen's edges screen reflections fade out.</summary>
        public static FloatSetting SsrEdgeFade { get; private set; }

        // ---- F8 -> Panes ---------------------------------------------------------------------

        /// <summary>Metres: the width of one pane of glass.</summary>
        public static FloatSetting PaneWidth { get; private set; }

        /// <summary>
        /// Metres from one floor to the next: a pane's height where the building painted no window
        /// of its own, and the floors of the buildings mirrored where the screen cannot show them.
        /// </summary>
        public static FloatSetting FloorHeight { get; private set; }

        /// <summary>0..3: panes bowing like cushions (insulating glass under pressure changes).</summary>
        public static FloatSetting PanePillow { get; private set; }

        /// <summary>0..3: each pane set at a slightly different angle.</summary>
        public static FloatSetting PaneTilt { get; private set; }

        /// <summary>0..3: the ripple toughened glass takes from the furnace rollers.</summary>
        public static FloatSetting RollerWave { get; private set; }

        /// <summary>Panes bulge on hot days and sink on cold ones (the game's temperature).</summary>
        public static BoolSetting PaneWeather { get; private set; }

        /// <summary>
        /// Close up, each pane is a window the asset painted (found between its painted mullions
        /// and transoms), split into panes about the pane width wide: not a grid of its own.
        /// </summary>
        public static BoolSetting PanesFollowWindows { get; private set; }

        // ---- F8 -> Interiors (Glass/Interiors.cs; on by default since 2026-10-11) ----------
        // Off: behind the glass by day a dark inside (GlassUniforms), by night the game's own window
        // (round 7). On: rooms behind every building's windows, OURS at night.

        /// <summary>Rooms behind the windows of every building (an asset can say otherwise, Alt+G).</summary>
        public static BoolSetting InteriorsEnabled { get; private set; }

        /// <summary>The rooms by day too; off, they show from dusk (the author: "by default interiors only show up at night").</summary>
        public static BoolSetting InteriorsByDay { get; private set; }

        /// <summary>0..1: how much of the rooms shows by day (0 the dark inside, as with InteriorsByDay off; 1 the rooms in full).</summary>
        public static FloatSetting InteriorsDayOpacity { get; private set; }

        /// <summary>0..1: the rooms as boxes with depth behind the glass (1) or flat pictures on it (0).</summary>
        public static FloatSetting InteriorsDepth { get; private set; }

        /// <summary>Up close, the rooms laid out as on the LOD models (the storey grid), so nothing switches when a building changes model; off, they follow the asset's painted windows.</summary>
        public static BoolSetting InteriorsMatchLods { get; private set; }

        /// <summary>
        /// Rooms lit where and when the game lights each asset's windows (its illumination map, the
        /// game's timing); off, ours (the three rows below). An asset can say otherwise, Alt+G.
        /// </summary>
        public static BoolSetting InteriorsFollowIllumination { get; private set; }

        /// <summary>0.05..0.9: the share of rooms lit at the busiest hour.</summary>
        public static FloatSetting InteriorsLit { get; private set; }

        /// <summary>The share follows the hour: homes empty out after midnight, offices keep a few floors.</summary>
        public static BoolSetting InteriorsLateNight { get; private set; }

        /// <summary>0..3: how often a room's light changes (1 = about every Interiors.HoursPerChange game hours; 0 never).</summary>
        public static FloatSetting InteriorsSwitching { get; private set; }

        /// <summary>0.25..3: a lit room's light (1 = about the game's own lit window).</summary>
        public static FloatSetting InteriorsBrightness { get; private set; }

        /// <summary>0..0.3: the share of rooms lit by coloured lights (LED, RGB).</summary>
        public static FloatSetting InteriorsColoured { get; private set; }

        /// <summary>0..1: the share of windows with curtains or blinds.</summary>
        public static FloatSetting InteriorsCurtains { get; private set; }

        // ---- Options page -> Rendering (the F8 panel's tab until 2026-10-04) ---------------

        /// <summary>0: near buildings only. 1: near and middle distance. 2: the whole city.</summary>
        public static IntSetting GlassDistance { get; private set; }

        /// <summary>0: 64 px, 1: 128 px, 2: 256 px a face of the live sky cube. See <see cref="SkyCubePixels"/>.</summary>
        public static IntSetting SkyCubeSize { get; private set; }

        /// <summary>1..6: faces of the live sky cube rendered a frame (6 = the whole cube every frame).</summary>
        public static IntSetting SkyCubeRate { get; private set; }

        // ---- F8 -> Water (Water/WaterController.cs; on by default since 2026-10-11) ------

        /// <summary>The Water tab's switch: off, the map's water is the game's and the theme's.</summary>
        public static BoolSetting WaterEnabled { get; private set; }

        /// <summary>The clean water's colour is the map theme's (or Theme Mixer's), not <see cref="WaterColor"/>.</summary>
        public static BoolSetting WaterThemeColor { get; private set; }

        /// <summary>The clean water's own colour, sRGB (Water/WaterSurface.cs).</summary>
        public static ColorSetting WaterColor { get; private set; }

        /// <summary>0.25..4: the water's colour x this.</summary>
        public static FloatSetting WaterBrightness { get; private set; }

        /// <summary>The water reflects the sky as real water does, per ripple (Water/WaterSkyLayer.cs), not the same share at every angle.</summary>
        public static BoolSetting WaterRealSky { get; private set; }

        /// <summary>0..3: that reflection x this (1 = real water).</summary>
        public static FloatSetting WaterSky { get; private set; }

        /// <summary>The city on screen is reflected in the water (screen-space), day and night.</summary>
        public static BoolSetting WaterCity { get; private set; }

        /// <summary>0..3: the city's reflection by day (1 = real water).</summary>
        public static FloatSetting WaterCityDay { get; private set; }

        /// <summary>0..3: the city's reflection by night (1 = real water).</summary>
        public static FloatSetting WaterCityNight { get; private set; }

        /// <summary>0..3: the ripples bend the city's reflection (1 = today's look: WaterOptics.CityRippleGain).</summary>
        public static FloatSetting WaterCityWaves { get; private set; }

        /// <summary>Our moving ripples on the water in place of the game's frozen ones (Water/WaterRipples.cs).</summary>
        public static BoolSetting WaterRipples { get; private set; }

        /// <summary>0..3: the ripples' roughness (1 = the game's own ripple texture's, measured).</summary>
        public static FloatSetting WaterRippleStrength { get; private set; }

        /// <summary>0.5..3: the ripples' lengths (1 = 16 m down to 0.5 m).</summary>
        public static FloatSetting WaterRippleSize { get; private set; }

        /// <summary>0.05..1: the longest wave's share of its length (1 = 16 m at 100% ripple size).</summary>
        public static FloatSetting WaterWaveSize { get; private set; }

        /// <summary>Our ripples drawn as an ocean's waves (Water/WaterSpectrum.cs: an FFT of a wind-sea spectrum), not 32 waves.</summary>
        public static BoolSetting WaterOceanWaves { get; private set; }

        /// <summary>The ocean waves' wind (and with it their roughness) follows the game's weather, not <see cref="WaterWind"/> (Water/WaterWeather.cs).</summary>
        public static BoolSetting WaterDynamicWaves { get; private set; }

        /// <summary>2..20 m/s: the wind the ocean waves are raised by -- their lengths (their roughness stays the Ripple strength's).</summary>
        public static FloatSetting WaterWind { get; private set; }

        /// <summary>0..2: the ocean waves' choppy crests (1 = Tessendorf's lambda 1 on the ripple texture's slopes).</summary>
        public static FloatSetting WaterChop { get; private set; }

        /// <summary>0..2: the sun's glitter on the water (1 = real water's; Water/WaterSkyLayer.cs). The sun's only since 2026-10-21 (the moon's before).</summary>
        public static FloatSetting WaterGlitter { get; private set; }

        /// <summary>0.1..2: the sun's glitter's size, every facet's slope x this (1 = the waves' own), as bright.</summary>
        public static FloatSetting WaterGlitterSize { get; private set; }

        /// <summary>0..2: the moon's glitter on the water at night (1 = real water's).</summary>
        public static FloatSetting WaterMoonGlitter { get; private set; }

        /// <summary>0.1..2: the moon's glitter's size, as the sun's.</summary>
        public static FloatSetting WaterMoonGlitterSize { get; private set; }

        /// <summary>Foam on the water: shores, rapids, wave fronts (Water/WaterFoam.cs).</summary>
        public static BoolSetting WaterFoam { get; private set; }

        /// <summary>0..3: all the foam x this (1 = the model's: Water/FoamOptics.cs).</summary>
        public static FloatSetting WaterFoamAmount { get; private set; }

        /// <summary>0..2: the foam along the sea's shores x this.</summary>
        public static FloatSetting WaterFoamShore { get; private set; }

        /// <summary>0..2: the foam on fast water (rapids, weirs, outflows) x this.</summary>
        public static FloatSetting WaterFoamRapids { get; private set; }

        /// <summary>0..2: the foam on rising wave fronts (tsunamis, floods, dam breaks) x this.</summary>
        public static FloatSetting WaterFoamFronts { get; private set; }

        /// <summary>0..0.1: the waves smear the city's reflection up and down (1 would be a light breeze's).</summary>
        public static FloatSetting WaterCityStreaks { get; private set; }

        /// <summary>1..3: the city's reflection drawn this many times as long as the city is tall (1 = a mirror).</summary>
        public static FloatSetting WaterCityLength { get; private set; }

        /// <summary>The view from under the water while the camera is below its level (Water/WaterUnder.cs; camera mods only).</summary>
        public static BoolSetting WaterUnder { get; private set; }

        /// <summary>5..80 m: how far one sees under the water (a black target's visibility: Water/WaterUnderOptics.cs).</summary>
        public static FloatSetting WaterUnderVisibility { get; private set; }

        /// <summary>0..2: the caustics, sunlight focused by the waves onto what lies under the water (1 = real water's).</summary>
        public static FloatSetting WaterUnderCaustics { get; private set; }

        /// <summary>The sun's light in the water only where its shadows let it in: shafts (the game's own shadow map of the sun).</summary>
        public static BoolSetting WaterUnderShafts { get; private set; }

        /// <summary>Glass on vehicles (Vehicles/VehicleGlassDraw.cs): the windows the game's own vehicle shaders show as windows, and the lamps' lenses.</summary>
        public static BoolSetting VehicleGlass { get; private set; }

        /// <summary>0..1: the share of vehicles with tinted windows (Glass/VehicleGlassLook.cs; the rest clear glass).</summary>
        public static FloatSetting VehicleTinted { get; private set; }

        /// <summary>0..3: the vehicles' reflections, the glass's and the paint's coat (1 = VehicleGlassLook.ReflectionBase times physics).</summary>
        public static FloatSetting VehicleReflections { get; private set; }

        /// <summary>0..1: the clear coat over the vehicles' paint (Glass/VehicleGlassLook.cs; 1 = real lacquer, 0 none).</summary>
        public static FloatSetting VehicleCoat { get; private set; }

        /// <summary>
        /// Every default, in one place. A new one reaches only new installs and a Reset: a player's
        /// file already lists every row.
        /// </summary>
        public static class Defaults
        {
            // ---- housekeeping ----

            /// <summary>The game's own language.</summary>
            public const int Language = 0;

            /// <summary>Frosted glass (ModsLibCS1.UI.Skin.Frosted), as in Volumetric Weather.</summary>
            public const int PanelSkin = 3;
            public const int IconPlace = IconPlaces.UnifiedUI;
            public const bool DetailedLogging = false;

            /// <summary>Under the sister mods' buttons: Volumetric Weather (10, 60), Volumetric Explosions (10, 100), RealFlag (10, 140).</summary>
            public const float HudButtonX = 10f;
            public const float HudButtonY = 180f;

            public const float WaterFoamView = 0f;

            /// <summary>Each tab's first heading open, the rest folded (Volumetric Weather's rule).</summary>
            public const string PanelOpenGroups = "TheGlass,TheReflections,ThePanes,TheRooms,WaterColour";

            // ---- Glass ----
            public const bool GlassEnabled = true;

            /// <summary>
            /// Reflective blue (the author's pick, 2026-10-04; Silver reflective from 2026-09-29).
            /// The colour, reflectivity and clarity below ARE the preset's (tools/test-settings.ps1).
            /// </summary>
            public const int GlassPreset = GlassPresets.ReflectiveBlue;
            public const float GlassOwnColour = 0.30f;

            /// <summary>All of them: the asset's own floors are what a player expects to see (2026-10-01).</summary>
            public const float GlassPaintedDetail = 1f;

            /// <summary>The Reflective blue preset's colour (#2E6A80): a static, because a Color32 cannot be a const.</summary>
            public static readonly Color32 GlassColor = new Color32(0x2E, 0x6A, 0x80, 255);
            public const float GlassReflectivity = 0.20f;
            public const float GlassGlancing = 0.80f;
            public const float GlassEdgeSilver = 1f;
            public const float GlassClarity = 0.24f;
            /// <summary>85%: a little of the game's own windows shows through (the author's, 2026-10-07).</summary>
            public const float GlassStrength = 0.85f;
            public const float GlassThreshold = 0.70f;
            public const bool GlassNightWindows = true;
            /// <summary>On: an LOD with no glass, or glass elsewhere, shows its model's (the author, 2026-10-21).</summary>
            public const bool LodGlassFromModel = true;
            public const bool GlassShowMask = false;

            // ---- Reflections ----
            public const bool SsrEnabled = true;
            public const bool ReflectFinished = true;
            public const bool ReflectNeighbours = true;
            public const int SsrQuality = 3;          // Ultra
            public const float ReflectionBrightness = 0.55f;
            public const int SkyReflection = 0;       // the live sky, with the clouds
            public const bool SkyClouds = true;
            public const float SkyDusk = 1f;          // the clear sky's own fall
            public const int OffscreenFill = 0;       // the mirrored screen
            public const float SunGlint = 3f;
            public const bool SteadyGlints = true;
            public const float GlassSmoothness = 1f;
            public const float SsrEdgeFade = 0.50f;

            // ---- Panes ---- (the author's: every bend at half its slider)
            public const float PaneWidth = 1.4f;
            public const float FloorHeight = 3.8f;    // an office storey
            public const float PanePillow = 1.5f;
            public const float PaneTilt = 1.5f;
            public const float RollerWave = 1.5f;
            public const bool PaneWeather = true;
            public const bool PanesFollowWindows = true;

            // ---- Interiors ---- (on since 2026-10-11, the author; off from 2026-10-07)
            public const bool InteriorsEnabled = true;
            public const bool InteriorsByDay = false;

            /// <summary>Half (the author, 2026-10-24): by day the rooms show faintly, the reflections win.</summary>
            public const float InteriorsDayOpacity = 0.5f;

            /// <summary>Full depth (the author, 2026-10-07: "a slider would be better, some people might not prefer the parallax").</summary>
            public const float InteriorsDepth = 1f;

            /// <summary>On (the author, 2026-10-14: in a big city the rooms "switch" where buildings change to their LODs; an option, some "would prefer accurate position on LODs").</summary>
            public const bool InteriorsMatchLods = true;

            /// <summary>Off (2026-10-25): most assets paint their glass "never lit" (58% of the room glass surveyed).</summary>
            public const bool InteriorsFollowIllumination = false;

            /// <summary>Under half at the busiest hour: "you'll likely never find ALL interiors lit up".</summary>
            public const float InteriorsLit = 0.45f;
            public const bool InteriorsLateNight = true;
            public const float InteriorsSwitching = 1f;
            public const float InteriorsBrightness = 1f;

            /// <summary>"orange/white/led/rgb even but less common": a few rooms in twenty-five.</summary>
            public const float InteriorsColoured = 0.04f;
            public const float InteriorsCurtains = 0.70f;

            // ---- Rendering ---- (the author's: the largest, livest sky)
            public const int GlassDistance = 2;       // the whole city
            public const int SkyCubeSize = 2;         // 256 px
            public const int SkyCubeRate = 6;

            // ---- Water ---- (on since 2026-10-11; inside it, the author's own settings, 2026-10-07)
            public const bool WaterEnabled = true;

            /// <summary>Our own colour, not the theme's (the author, 2026-10-08: "rather than rely on Theme Mixer").</summary>
            public const bool WaterThemeColor = false;

            /// <summary>
            /// Deep lake water (the author's reference, 2026-10-08: Battlefield 6's Lake Mead, near water
            /// #11222A..#1A2C34 with its faint sky in it): dark, a little teal. Set from the preview's
            /// pictures (tools\preview\foam-render.ps1 -SkyColumns).
            /// </summary>
            public static readonly Color32 WaterColor = new Color32(0x0E, 0x22, 0x2B, 255);

            public const float WaterBrightness = 1f;
            public const bool WaterRealSky = true;

            /// <summary>Real water (the sky per ripple since 2026-10-08; 80% while the cube counted each ripple's tilt twice).</summary>
            public const float WaterSky = 1f;
            public const bool WaterCity = true;
            public const float WaterCityDay = 1f;

            /// <summary>300%: the lit city carries the water at night (the author's, 2026-10-07).</summary>
            public const float WaterCityNight = 3f;

            /// <summary>The waves bend the city's reflection; replaced the blur (the author, 2026-10-05: "the water doesn't get blurry").</summary>
            public const float WaterCityWaves = 1f;

            /// <summary>None: the waves bend the reflection, no smear (the author's, 2026-10-07; 1% from 2026-10-04).</summary>
            public const float WaterCityStreaks = 0f;

            /// <summary>130%: the city's reflection a little longer than a mirror's (the author's, 2026-10-07).</summary>
            public const float WaterCityLength = 1.3f;

            /// <summary>Glass on vehicles on (the author, 2026-10-23: "go ahead with the in-game side").</summary>
            public const bool VehicleGlass = true;

            /// <summary>5% of vehicles (the author, 2026-10-23: "percentage of cars which have tinted windows ... like 5%").</summary>
            public const float VehicleTinted = Glass.VehicleGlassLook.DefaultTintedShare;

            /// <summary>
            /// 100% = three times physics (VehicleGlassLook.ReflectionBase): the old scale's 300%, where the
            /// author played, made the base (2026-10-23: "increasing the base reflections ... going more than
            /// 300% but not actually make the slider go above that").
            /// </summary>
            public const float VehicleReflections = 1f;

            /// <summary>Real lacquer over the paint (his pick from the clear-coat trial, 2026-10-23).</summary>
            public const float VehicleCoat = 1f;

            /// <summary>Moving ripples on (the author, 2026-10-05: step 1 of the water plan, "a proof of concept").</summary>
            public const bool WaterRipples = true;

            /// <summary>As rough as the game's own ripples.</summary>
            public const float WaterRippleStrength = 1f;

            /// <summary>16 m down to 0.5 m.</summary>
            public const float WaterRippleSize = 1f;

            /// <summary>The longest waves at full length.</summary>
            public const float WaterWaveSize = 1f;

            /// <summary>Ocean waves (the author, 2026-10-17, after the pictures of web-ocean's: "Yes to both").</summary>
            public const bool WaterOceanWaves = true;

            /// <summary>Dynamic waves (the author, 2026-10-21): a dry day's sea is the Wind row's default's, as before.</summary>
            public const bool WaterDynamicWaves = true;

            /// <summary>A moderate breeze (Beaufort 4): the waves peak about 30 m long over the sea's fetch (the pictures').</summary>
            public const float WaterWind = 7f;

            /// <summary>The pictures' choppiness (lambda 1).</summary>
            public const float WaterChop = 1f;

            /// <summary>35% of real water's (the author, 2026-10-17: "35% looks slightly better, 100% is a bit overbright").</summary>
            public const float WaterGlitter = 0.35f;

            /// <summary>
            /// Half the waves' own spread (the author, 2026-10-21, after the release: "the glitter area is
            /// way too big"; from a city camera 100% covered a third of the screen).
            /// </summary>
            public const float WaterGlitterSize = 0.5f;

            /// <summary>The sun's (the moon's glitter was the Sun glitter row's until 2026-10-21).</summary>
            public const float WaterMoonGlitter = 0.35f;

            public const float WaterMoonGlitterSize = 0.5f;

            /// <summary>Foam on (the author, 2026-10-06: "One thing I'd actually like is the foam").</summary>
            public const bool WaterFoam = true;

            /// <summary>30% of the model's foam, hardly any on fast water (the author's, 2026-10-07).</summary>
            public const float WaterFoamAmount = 0.30f;
            public const float WaterFoamShore = 1f;
            public const float WaterFoamRapids = 0.05f;
            public const float WaterFoamFronts = 0.1f;

            /// <summary>On (the author's ask, 2026-10-24: the seabed looked empty from under the water); it only shows there.</summary>
            public const bool WaterUnder = true;

            /// <summary>60 m: the author's own setting, 2026-10-24; the look of a player's picture he chose (2026-10-28).</summary>
            public const float WaterUnderVisibility = 60f;

            /// <summary>None: that picture has none (2026-10-28).</summary>
            public const float WaterUnderCaustics = 0f;

            /// <summary>On: the author's pick of the picture's shafts, from the sun's real shadows (2026-10-28).</summary>
            public const bool WaterUnderShafts = true;
        }

        private static bool _initialised;

        /// <summary>True once every setting exists (the file is read straight after).</summary>
        public static bool IsInitialised
        {
            get { return _initialised; }
        }

        /// <summary>
        /// Constructs every setting with its default, then reads RealGlass.xml over them. Called
        /// from Mod.OnEnabled, and defensively from everything that needs a setting before that
        /// could have run.
        /// </summary>
        public static void Init()
        {
            if (_initialised)
                return;

            try
            {
                // Looked up first because a reloaded mod finds it already there, and adding a name
                // twice throws.
                if (GameSettings.FindSettingsFileByName(InMemory) == null)
                    GameSettings.AddSettingsFile(new SettingsFile { fileName = InMemory, dontSave = true });

                // In catalog order (UI/SettingsCatalog.cs), which is also the file's order.
                GlassEnabled = new BoolSetting("GlassEnabled", Defaults.GlassEnabled);
                GlassPreset = new IntSetting("GlassPreset", Defaults.GlassPreset);
                GlassOwnColour = new FloatSetting("GlassOwnColour", Defaults.GlassOwnColour);
                GlassPaintedDetail = new FloatSetting("GlassPaintedDetail", Defaults.GlassPaintedDetail);
                GlassColor = new ColorSetting("GlassColor", Defaults.GlassColor);
                GlassReflectivity = new FloatSetting("GlassReflectivity", Defaults.GlassReflectivity);
                GlassGlancing = new FloatSetting("GlassGlancing", Defaults.GlassGlancing);
                GlassEdgeSilver = new FloatSetting("GlassEdgeSilver", Defaults.GlassEdgeSilver);
                GlassClarity = new FloatSetting("GlassClarity", Defaults.GlassClarity);
                GlassStrength = new FloatSetting("GlassStrength", Defaults.GlassStrength);
                GlassThreshold = new FloatSetting("GlassThreshold", Defaults.GlassThreshold);
                GlassNightWindows = new BoolSetting("GlassNightWindows", Defaults.GlassNightWindows);
                LodGlassFromModel = new BoolSetting("LodGlassFromModel", Defaults.LodGlassFromModel);
                GlassShowMask = new BoolSetting("GlassShowMask", Defaults.GlassShowMask);


                SsrEnabled = new BoolSetting("SsrEnabled", Defaults.SsrEnabled);
                ReflectFinished = new BoolSetting("ReflectFinished", Defaults.ReflectFinished);
                ReflectNeighbours = new BoolSetting("ReflectNeighbours", Defaults.ReflectNeighbours);
                SsrQuality = new IntSetting("SsrQuality", Defaults.SsrQuality);
                ReflectionBrightness = new FloatSetting("ReflectionBrightness", Defaults.ReflectionBrightness);
                SkyReflection = new IntSetting("SkyReflection", Defaults.SkyReflection);
                SkyClouds = new BoolSetting("SkyClouds", Defaults.SkyClouds);
                SkyDusk = new FloatSetting("SkyDusk", Defaults.SkyDusk);
                OffscreenFill = new IntSetting("OffscreenFill", Defaults.OffscreenFill);
                SunGlint = new FloatSetting("SunGlint", Defaults.SunGlint);
                SteadyGlints = new BoolSetting("SteadyGlints", Defaults.SteadyGlints);
                GlassSmoothness = new FloatSetting("GlassSmoothness", Defaults.GlassSmoothness);
                SsrEdgeFade = new FloatSetting("SsrEdgeFade", Defaults.SsrEdgeFade);

                PaneWidth = new FloatSetting("PaneWidth", Defaults.PaneWidth);
                FloorHeight = new FloatSetting("FloorHeight", Defaults.FloorHeight);
                PanePillow = new FloatSetting("PanePillow", Defaults.PanePillow);
                PaneTilt = new FloatSetting("PaneTilt", Defaults.PaneTilt);
                RollerWave = new FloatSetting("RollerWave", Defaults.RollerWave);
                PaneWeather = new BoolSetting("PaneWeather", Defaults.PaneWeather);
                PanesFollowWindows = new BoolSetting("PanesFollowWindows", Defaults.PanesFollowWindows);

                InteriorsEnabled = new BoolSetting("InteriorsEnabled", Defaults.InteriorsEnabled);
                InteriorsByDay = new BoolSetting("InteriorsByDay", Defaults.InteriorsByDay);
                InteriorsDayOpacity = new FloatSetting("InteriorsDayOpacity", Defaults.InteriorsDayOpacity);
                InteriorsDepth = new FloatSetting("InteriorsDepth", Defaults.InteriorsDepth);
                InteriorsMatchLods = new BoolSetting("InteriorsMatchLods", Defaults.InteriorsMatchLods);
                InteriorsFollowIllumination = new BoolSetting("InteriorsFollowIllumination", Defaults.InteriorsFollowIllumination);
                InteriorsLit = new FloatSetting("InteriorsLit", Defaults.InteriorsLit);
                InteriorsLateNight = new BoolSetting("InteriorsLateNight", Defaults.InteriorsLateNight);
                InteriorsSwitching = new FloatSetting("InteriorsSwitching", Defaults.InteriorsSwitching);
                InteriorsBrightness = new FloatSetting("InteriorsBrightness", Defaults.InteriorsBrightness);
                InteriorsColoured = new FloatSetting("InteriorsColoured", Defaults.InteriorsColoured);
                InteriorsCurtains = new FloatSetting("InteriorsCurtains", Defaults.InteriorsCurtains);

                WaterEnabled = new BoolSetting("WaterEnabled", Defaults.WaterEnabled);
                WaterThemeColor = new BoolSetting("WaterThemeColor", Defaults.WaterThemeColor);
                WaterColor = new ColorSetting("WaterColor", Defaults.WaterColor);
                WaterBrightness = new FloatSetting("WaterBrightness", Defaults.WaterBrightness);
                WaterRipples = new BoolSetting("WaterRipples", Defaults.WaterRipples);
                WaterRippleStrength = new FloatSetting("WaterRippleStrength", Defaults.WaterRippleStrength);
                WaterRippleSize = new FloatSetting("WaterRippleSize", Defaults.WaterRippleSize);
                WaterWaveSize = new FloatSetting("WaterWaveSize", Defaults.WaterWaveSize);
                WaterOceanWaves = new BoolSetting("WaterOceanWaves", Defaults.WaterOceanWaves);
                WaterDynamicWaves = new BoolSetting("WaterDynamicWaves", Defaults.WaterDynamicWaves);
                WaterWind = new FloatSetting("WaterWind", Defaults.WaterWind);
                WaterChop = new FloatSetting("WaterChop", Defaults.WaterChop);
                WaterFoam = new BoolSetting("WaterFoam", Defaults.WaterFoam);
                WaterFoamAmount = new FloatSetting("WaterFoamAmount", Defaults.WaterFoamAmount);
                WaterFoamShore = new FloatSetting("WaterFoamShore", Defaults.WaterFoamShore);
                WaterFoamRapids = new FloatSetting("WaterFoamRapids", Defaults.WaterFoamRapids);
                WaterFoamFronts = new FloatSetting("WaterFoamFronts", Defaults.WaterFoamFronts);
                WaterRealSky = new BoolSetting("WaterRealSky", Defaults.WaterRealSky);
                WaterSky = new FloatSetting("WaterSky", Defaults.WaterSky);
                WaterGlitter = new FloatSetting("WaterGlitter", Defaults.WaterGlitter);
                WaterGlitterSize = new FloatSetting("WaterGlitterSize", Defaults.WaterGlitterSize);
                WaterMoonGlitter = new FloatSetting("WaterMoonGlitter", Defaults.WaterMoonGlitter);
                WaterMoonGlitterSize = new FloatSetting("WaterMoonGlitterSize", Defaults.WaterMoonGlitterSize);
                WaterCity = new BoolSetting("WaterCity", Defaults.WaterCity);
                WaterCityDay = new FloatSetting("WaterCityDay", Defaults.WaterCityDay);
                WaterCityNight = new FloatSetting("WaterCityNight", Defaults.WaterCityNight);
                WaterCityWaves = new FloatSetting("WaterCityWaves", Defaults.WaterCityWaves);
                WaterCityStreaks = new FloatSetting("WaterCityStreaks", Defaults.WaterCityStreaks);
                WaterCityLength = new FloatSetting("WaterCityLength", Defaults.WaterCityLength);
                WaterUnder = new BoolSetting("WaterUnder", Defaults.WaterUnder);
                WaterUnderVisibility = new FloatSetting("WaterUnderVisibility", Defaults.WaterUnderVisibility);
                WaterUnderCaustics = new FloatSetting("WaterUnderCaustics", Defaults.WaterUnderCaustics);
                WaterUnderShafts = new BoolSetting("WaterUnderShafts", Defaults.WaterUnderShafts);
                VehicleGlass = new BoolSetting("VehicleGlass", Defaults.VehicleGlass);
                VehicleTinted = new FloatSetting("VehicleTinted", Defaults.VehicleTinted);
                VehicleReflections = new FloatSetting("VehicleReflections", Defaults.VehicleReflections);
                VehicleCoat = new FloatSetting("VehicleCoat", Defaults.VehicleCoat);

                Language = new IntSetting("Language", Defaults.Language);
                PanelKey = new SavedInputKey("PanelKey", InMemory, DefaultPanelKey, false);
                PickerKey = new SavedInputKey("PickerKey", InMemory, DefaultPickerKey, false);
                PanelSkin = new IntSetting("PanelSkin", Defaults.PanelSkin);
                IconPlace = new IntSetting("IconPlace", Defaults.IconPlace);

                GlassDistance = new IntSetting("GlassDistance", Defaults.GlassDistance);
                SkyCubeSize = new IntSetting("SkyCubeSize", Defaults.SkyCubeSize);
                SkyCubeRate = new IntSetting("SkyCubeRate", Defaults.SkyCubeRate);

                DetailedLogging = new BoolSetting("DetailedLogging", Defaults.DetailedLogging);

                HudButtonX = new FloatSetting("HudButtonX", Defaults.HudButtonX);
                HudButtonY = new FloatSetting("HudButtonY", Defaults.HudButtonY);
                WaterFoamView = new FloatSetting("WaterFoamView", Defaults.WaterFoamView);
                PanelOpenGroups = new StringSetting("PanelOpenGroups", Defaults.PanelOpenGroups);

                // Before the file is read: reading it walks the catalog, and building the catalog
                // calls Init.
                _initialised = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[RealGlass] Could not initialise settings.");
                Debug.LogException(e);
                return;
            }

            SettingsXml.Load();

            // Each building asset's own glass (RealGlassAssets.xml): after the settings, whose
            // global values every asset falls back on.
            PerAsset.AssetFile.Load();
        }

        /// <summary>
        /// F8 (asked for, 2026-09-29): F7 is the game's own debug-output key
        /// (DefaultSettings.debugOuput), and the game binds no other F key but F1 (quick save) and
        /// F12 (screenshots); the sister mods have F3, F4 and F6. A property because
        /// SavedInputKey.Encode is a method, so it cannot be a const.
        /// </summary>
        public static int DefaultPanelKey
        {
            get { return SavedInputKey.Encode(KeyCode.F8, false, false, false); }
        }

        /// <summary>
        /// Alt+G (asked for, 2026-10-01, as BOB's Alt+B): the game binds nothing to it (no default of
        /// DefaultSettings uses Alt at all), and of the 141 mod assemblies installed here only Network
        /// Anarchy does -- its editor grid, while its road tools are on -- so the key is not taken while
        /// a road tool is (ModController). Ctrl+Alt+G and Alt+Shift+G are bound by no one.
        /// </summary>
        public static int DefaultPickerKey
        {
            get { return SavedInputKey.Encode(KeyCode.G, false, false, true); }
        }

        /// <summary>The live sky cube's face size in pixels, from <see cref="SkyCubeSize"/>: 64, 128 or 256.</summary>
        public static int SkyCubePixels
        {
            get { return CubePixels(SkyCubeSize != null ? SkyCubeSize.value : Defaults.SkyCubeSize); }
        }

        /// <summary>A SkyCubeSize value as pixels. Pure, for the readout and the tests.</summary>
        public static int CubePixels(int sizeValue)
        {
            switch (sizeValue)
            {
                case 0: return 64;
                case 2: return 256;
                default: return 128;
            }
        }
    }
}
