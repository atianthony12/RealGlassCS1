namespace RealGlass.Glass
{
    /// <summary>
    /// Everything the player sets about the glass, in the settings' own units (a percent row's
    /// 47% is 0.47). PURE: no engine or game types, so the GPU preview (UnityProject, through
    /// tools/preview/sync-sim.ps1) fills the same struct and draws with the same uniforms.
    /// GlassController fills it from Settings; the preview from its command line.
    /// </summary>
    public struct GlassParams
    {
        // Glass
        public bool Enabled;
        public float TintR, TintG, TintB;   // the chosen glass colour, sRGB 0..1
        public float OwnColour;             // 0..1: how much of the building's own painted colour is kept
        public float PaintedDetail;         // 0..1: how much of the lines the asset painted on its glass (floor bands, frames) stay
        public int DetailMode;              // the preview's comparison: 0 the lines as painted (the glass steps aside), 1 under the glass's sheen
        public float Reflectivity;          // 0..0.6: reflectance looking straight at it (F0)
        public float Glancing;              // 0..1: reflectance edge-on
        public float Clarity;               // 0..1: visible light let through
        public float Strength;              // 0..1: our glass over the game's
        public float Threshold;             // 0.4..0.95: what counts as glass (the asset's shininess)
        public bool ThresholdAuto;          // each asset's threshold read from its textures (ThresholdDetect) where it has none of its own
        public bool NightWindows;           // windows lit at night count as glass
        public bool LodGlass;               // an LOD whose glass differs from its model's (none, or elsewhere) takes the model's (LodGlass.cs)
        public bool ShowMask;
        public float EdgeSilver;            // 0..1: how far the reflection's colour turns silver edge-on (1 = as physics has it)

        // Reflections
        public bool Trace;                  // screen-space reflections
        public bool Finished;               // reflect last frame's finished picture (fog, clouds, lamps), reprojected
        public bool Neighbours;             // rays that miss the screen walk the city's height map (CityMap)
        public bool CityCards;              // where that walk meets a building, its facade card (FacadeCards; a trial)
        public int Quality;                // 0 Low .. 3 Ultra
        public float ReflectionBrightness;  // 0..3
        public int SkySource;               // 0 live sky, 1 the game's environment map
        public bool SkyClouds;              // the live sky carries the clouds (Volumetric Weather's volume and distant clouds)
        public float SkyDusk;               // 0..1: how far the sky in the glass follows a clear sky's fall in the sun's last ten degrees (GlassUniforms.Dusk)
        public int OffscreenFill;           // 0 mirrored screen, 1 city colour, 2 sky only
        public float SunGlint;              // 0..3
        public bool SteadyGlints;           // specular anti-aliasing of the bent panes
        public float Smoothness;            // 0..1
        public float EdgeFade;              // 0..0.5

        // Panes
        public float PaneWidth;             // metres
        public float FloorHeight;           // metres: a storey (a pane's height on its own grid; the mirrored neighbours' floors)
        public float Pillow;                // 0..3
        public float Tilt;                  // 0..3
        public float RollerWave;            // 0..3
        public bool PaneWeather;
        public bool PanesFollowWindows;     // close up, the panes are the windows the asset painted
        public float Dirt;                  // 0..1: the share of buildings with dirt on their panes (Dirt.cginc; 0 = none, skipped)
        public bool DirtDynamic;            // rain washes the dirt off; it builds up again over a game month (DirtWeather)

        // Interiors (Interiors.cs, Interiors.cginc): rooms behind every window, on by default
        public bool Interiors;              // rooms behind the windows of every building (an asset can say otherwise)
        public bool InteriorsByDay;         // by day too; off: the dark inside by day, the rooms from dusk
        public float InteriorsDayOpacity;   // 0..1: by day, the dark inside (0) .. the rooms in full (1)
        public float InteriorsDepth;        // 0..1: a room with depth behind the glass (1) .. a flat picture on it (0)
        public bool InteriorsMatchLods;     // up close, the rooms laid out as on the LOD models (the storey grid); off: the asset's painted windows
        public bool InteriorsFollowIllumination;   // rooms lit where and when the game lights each asset's windows (an asset can say otherwise)
        public float InteriorsLit;          // 0.05..0.9: the share of rooms lit at the busiest hour
        public bool InteriorsLateNight;     // the share follows the hour (homes empty out after midnight)
        public float InteriorsSwitching;    // 0..3: how often a room's light changes (1 = every Interiors.HoursPerChange game hours)
        public float InteriorsBrightness;   // 0.25..3: a lit room's light (1 = about the game's own lit window)
        public float InteriorsColoured;     // 0..0.3: the share of rooms lit by coloured lights (LED, RGB): homes, hotels, leisure
        public float InteriorsColouredWork; // 0..0.3: the same in offices, shops, industry and public buildings
        public float InteriorsCurtains;     // 0..1: the share of windows with curtains or blinds
        public int GlassOffKinds;           // the kinds of building with no glass (GlassKinds bits; 0 = every kind on)

        /// <summary>The defaults the mod ships (Settings.Defaults); tools/test-settings.ps1 checks they agree.</summary>
        public static GlassParams Default()
        {
            GlassParams p;
            p.Enabled = true;
            p.TintR = 0xA9 / 255f;
            p.TintG = 0xB4 / 255f;
            p.TintB = 0xBA / 255f;
            p.OwnColour = 0.30f;
            p.PaintedDetail = 1.00f;
            p.DetailMode = 0;
            p.Reflectivity = 0.35f;
            p.Glancing = 0.80f;
            p.Clarity = 0.20f;
            p.Strength = 0.85f;
            p.Threshold = 0.70f;
            p.ThresholdAuto = true;
            p.NightWindows = true;
            p.LodGlass = true;
            p.ShowMask = false;
            p.EdgeSilver = 1.00f;

            p.Trace = true;
            p.Finished = true;
            p.Neighbours = true;
            p.CityCards = true;
            p.Quality = 3;
            p.ReflectionBrightness = 0.55f;
            p.SkySource = 0;
            p.SkyClouds = true;
            p.SkyDusk = 1.00f;
            p.OffscreenFill = 0;
            p.SunGlint = 3.00f;
            p.SteadyGlints = true;
            p.Smoothness = 1.00f;
            p.EdgeFade = 0.50f;

            p.PaneWidth = 1.4f;
            p.FloorHeight = 3.8f;
            p.Pillow = 1.50f;
            p.Tilt = 1.50f;
            p.RollerWave = 1.50f;
            p.PaneWeather = true;
            p.PanesFollowWindows = true;
            p.Dirt = 0f;
            p.DirtDynamic = true;

            p.Interiors = true;
            p.InteriorsByDay = false;
            p.InteriorsDayOpacity = 0.5f;
            p.InteriorsDepth = 1f;
            p.InteriorsMatchLods = true;
            p.InteriorsFollowIllumination = false;
            p.InteriorsLit = 0.45f;
            p.InteriorsLateNight = true;
            p.InteriorsSwitching = 1f;
            p.InteriorsBrightness = 1f;
            p.InteriorsColoured = 0.04f;
            p.InteriorsColouredWork = 0f;
            p.InteriorsCurtains = 0.70f;
            p.GlassOffKinds = 0;
            return p;
        }
    }

    /// <summary>
    /// What the scene gives the glass this frame: the sun, the sky's light, where the ground is.
    /// The mod reads the game (DayNightProperties, RenderSettings, TerrainManager, WeatherManager);
    /// the preview its own scene.
    /// </summary>
    public struct GlassFrame
    {
        public float SunX, SunY, SunZ;           // towards the sun, world, unit
        public float SunR, SunG, SunB;           // the sun's radiance (linear colour x intensity)
        public float AmbSkyR, AmbSkyG, AmbSkyB;  // light from the sky (linear)
        public float AmbGroundR, AmbGroundG, AmbGroundB;
        public float GroundY;                    // world height of the ground around the view
        public float Temperature;                // degrees C (the game's weather)
        public int ScreenHeight;                 // pixels
        public int CubeMips;                     // mip levels of the sky cube
        public bool CloudAlpha;                  // the sky cube's alpha is the clouds' opacity (GlassSky.CloudAlpha)
        public int ColorMapWidth;                // the game's object colour map, whose texel a building's
        public int ColorMapHeight;               // colour index names (RenderManager.OBJECT_COLORMAP_*)
        public float Hour;                       // the game's time of day, 0..24
        public float Clock;                      // game hours since the city's day 0, wrapped (Interiors.ClockWrap): the rooms' switching
        public bool DirtReady;                   // the dirt's textures are loaded and bound (DirtTextures): no dirt drawn without them
        public float DirtBuiltUp;                // 0 just washed by rain .. 1 built up all the way (DirtWeather; read with DirtDynamic)
    }
}
