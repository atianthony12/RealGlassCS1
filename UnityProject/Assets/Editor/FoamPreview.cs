using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RealGlass.Water;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pictures of the water's foam (RealGlass/WaterFoam) on this machine's GPU -- tools\preview\
/// foam-render.ps1 writes foam-preview-input.txt (the mod's numbers, read from the DLL, and the shots)
/// and runs
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FoamPreview.Render
/// A coast, 4 km square: the sea to the west at 40 m (the game's heights are 0..1024 m), a beach
/// whose waterline wanders, a town of towers on the land, and a river running down to the sea through
/// a rapid. Its water is the GAME'S DATA: the surface heights in two simulation frames, the terrain's
/// heights, and the surface maps (normal, flow, depth) at the game's resolutions and in its
/// encodings, which both the stand-in water (Editor/PreviewGameWater.shader) and the foam read
/// through WaterGame.cginc, as in the game. "tsunami" views add a 3 m bulge running at the shore,
/// "tsunamiwall" views a 20 m wall (seen from below its crest and from a city camera).
/// The camera in legacy deferred HDR like the game's; the city reflection on the sea as the mod
/// records it. Each shot is one PNG (tonemapped, sRGB).
/// </summary>
public static class FoamPreview
{
    private const float SeaLevel = 40f;
    private const float Half = 2048f;
    private const int WaterTexels = 256;
    private const int TerrainTexels = 1024;
    private const int SurfaceTexels = 128;
    private const float HeightScale = 1024f;
    private const float RiverHalf = 32f;

    private sealed class Shot
    {
        public string Name;
        public string View;
        public bool Foam;
        public bool Sources;
        // The foam drawn: 0 none, 1 RealGlass/WaterFoam, 2 its sources, 3 today's foam
        // (Editor/PreviewFoamToday.shader, RealGlass/WaterFoam frozen at 1a26e7e), 4 the layers' view,
        // 5 the surf's distance from the waterline as lines every 10 m over the foam, 6 the same on today's,
        // 7 magenta wherever the game's own test finds water (the foam's view 3).
        public int Mode;
        public float Seconds;
        public float Strength;
        // The sky on the water: 0 the mod's cube until 2026-10-08 (the stand-in's own, x SkyGain); 1 the
        // sky layer (RealGlass/WaterSky) at the Sky slider SkyGain, the stand-in reflecting none.
        public int SkyMode;
        public float SkyGain = WaterOptics.SkyShare;
        // The water's colour (sRGB, the stand-in's _BodyColour); null: the preview's own.
        public Color? Body;
        // Realistic depth (RealGlass/WaterSky's REALISTIC DEPTH, the Water tab's rows): on, Clarity (m),
        // Distortion and Sunlight on the seabed (1 = 100%).
        public bool Depth;
        public float Clarity = 12f;
        public float Distortion = 1f;
        public float DepthCaustics = 1f;
        // The ground under the water: 0 the preview's own (wet sand darkening to a grey-green bed by 6 m),
        // 1 the beach's pale sand all the way down.
        public int Bed;
        // The map theme's _WaterColorUnder for the shot, as the game passes it (a Color to
        // SetGlobalColor); null: neutral, what the shader sees as 0.5 (the bed untinted).
        public Color? UnderColour;
        // The Underwater color row's own colour (sRGB; the theme's row off), null: the theme's.
        public Color? OwnUnder;
        // The two masks' check: where the stand-in draws (red) and where the sky layer does (green).
        public bool Masks;
        // The ripples: 0 the 32 waves (RealGlass/WaterRipples), 1 the FFT waves (RealGlass/WaterWaves).
        public int Waves;
        // The sun's glitter on the sky layer (1 = real water; 0 none, the game's own highlight kept).
        public float Glitter;
        // Its size: every facet's slope x this (1 = the waves' own).
        public float GlitterSize = 1f;
        // The surf's corridor round the real distance from the waterline: its factor (-1 the mod's, 0 no
        // distance map: the depth over the slope alone, as before 2026-10-25).
        public float Corridor = -1f;
        // The view from under the water (RealGlass/WaterUnder): the water's visibility (m; below 0: no
        // such pass, what a camera under the water sees today), the caustics' strength (0 none) and the
        // sun's shafts (0 none: the sun's light everywhere in the water; 1 through the shadow map).
        public float Under = -1f;
        public float Caustics;
        public float Shafts;
        // The veiling light's gain in place of WaterUnderOptics.VeilGain (0: the mod's), to try values.
        public float VeilGain;
        // The view from under the water as it stood at affebf9 (Editor/PreviewUnderToday.shader), for the
        // surface from below redone 2026-10-30.
        public bool UnderToday;
        // The game's fog over the sky in the water's cube (GlassUtil's SkyFog, as WaterSkyLayer since
        // 2026-10-30; false: the bare skybox, as before).
        public bool SkyFog;
    }

    // -GameSky: the game's own skybox (Editor/PreviewGameSky.shader) and its fog's inputs, set by
    // GameSkyGlobals for each view's sun; the screen gets the game's fog over its sky (GlassUtil's
    // SkyFog, the port of it).
    private static bool s_gameSky;

    // "ilsheet 1": the water's dry points next to wet ones hold no more than those neighbours' level, as
    // WaterSimulation.FillHeightMap reads (IL, 2026-11-11); without it every dry point holds the ground,
    // as the preview always had it. Which of the two the game's picture matches is NOT settled: either
    // way the preview's water lies on up the beach as a film to the depth map's cut (magenta: mode 7),
    // with this rule dry islands stand in it. The foam has to look right on both.
    private static bool s_ilSheet;

    // "seabed 1" (foam-render -Seabed): the coast from z = -250 to 300 falls 10 cm a metre to 38 m of water
    // (from 5 m at 50 m out to 30 m at 300 m out), and its bed is pale sand with a hard wavy edge to a dark
    // bed crossing that depth, both with metre-scale detail (Editor/PreviewSeabed.shader): the realistic
    // depth's checks (a sand's edge far out on the seabed; the bend and the sun on it).
    private static bool s_seabed;

    /// <summary>A run of frames for the flicker under temporal anti-aliasing (as WaterPreview's).</summary>
    private sealed class Seq
    {
        public string Name;
        public string View;
        public float Seconds;
        public float Strength;
    }

    /// <summary>What a frame needs: everything the shots set up.</summary>
    private sealed class Scene
    {
        public Coast Coast;
        public Camera Camera;
        public GameObject CameraObject;
        public RenderTexture Target;
        public Light Sun;
        public Material Skybox, Water, Foam, Reflection, Surface, Sky, Under, UnderToday;
        public CommandBuffer UnderBuffer, UnderDepthBuffer, UnderTodayBuffer, ShadowGrab;
        // The under buffer on the camera (null: none).
        public CommandBuffer UnderOn;
        public GameObject Bridge;
        public Camera Capture;
        public RenderTexture SkyCube;
        public CommandBuffer CubeFog;
        private bool _cubeFogOn;

        /// <summary>The game's fog in the water's cube for the next view, or not (before Set: it renders the cube).</summary>
        public void CubeFogFor(Shot shot)
        {
            if (shot.SkyFog == _cubeFogOn)
                return;
            if (shot.SkyFog)
                Capture.AddCommandBuffer(CameraEvent.AfterSkybox, CubeFog);
            else
                Capture.RemoveCommandBuffer(CameraEvent.AfterSkybox, CubeFog);
            _cubeFogOn = shot.SkyFog;
        }
        public List<Tower> Towers;
        public List<Renderer> FoamRenderers, SkyRenderers;
        public Color BodyColour;
        public Vector4 SkyInfo;
        public Vector4[] Rows;
        public float RippleRms;
        public RenderTexture Ripples;
        public float? Tsunami;
        public List<string> Log;

        // The ripples' generators: the 32 waves' material, the FFT waves (null without a "spectrum"
        // line), which one this shot uses, and the FFT's choppiness.
        public Material Generator;
        public WaterWaves Waves;
        public int WaveMode;
        public float Chop;

        // The layered foam: today's foam for the comparison (Editor/PreviewFoamToday.shader).
        public Material FoamToday;

        // The ground's material and its two pictures (Shot.Bed).
        public Material Ground;
        public Texture2D[] Beds;

        /// <summary>
        /// The Color that the game's water sees as 0.5 through Shader.SetGlobalColor. Measured 2026-11-11
        /// in this linear project (the stand-in showing the global, read back raw): SetGlobalColor passes
        /// the numbers through unconverted -- 0.7354 came out 0.7354 -- so 0.5 is passed as 0.5.
        /// </summary>
        public static Color NeutralUnder()
        {
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        }

        /// <summary>
        /// The underwater colour and the realistic depth for a shot (after Set: the sun is the view's), as
        /// WaterController sets them: the theme's colour a global, the row's own (or with the depth on, 0 on
        /// the game's water) on the stand-in's material; the sky layer's variant, copies and numbers
        /// (WaterOptics.DepthSigma, DepthSunUnder, DepthSunShare); and the ground the shot asks for.
        /// </summary>
        public void DepthFor(Shot shot)
        {
            if (!SeabedScene)
                Ground.mainTexture = Beds[Mathf.Clamp(shot.Bed, 0, Beds.Length - 1)];
            Color theme = shot.UnderColour ?? NeutralUnder();
            Shader.SetGlobalColor("_WaterColorUnder", theme);
            Color under = shot.OwnUnder ?? theme;
            Water.SetColor("_WaterColorUnder", shot.Depth ? new Color(0f, 0f, 0f, theme.a) : under);
            if (shot.UnderColour.HasValue || shot.OwnUnder.HasValue)
            {
                Vector4 seen = Shader.GetGlobalVector("_WaterColorUnder");
                Log.Add(string.Format(CultureInfo.InvariantCulture, "under colour: the theme's {0:F4} {1:F4} {2:F4} (the global holds {3:F4} {4:F4} {5:F4}); the game's water's {6}",
                    theme.r, theme.g, theme.b, seen.x, seen.y, seen.z, shot.Depth ? "0 (realistic depth)" : ColorUtility.ToHtmlStringRGB(under)));
            }

            Shader.SetGlobalFloat("_PreviewGameWaterCheck", shot.Masks ? 3f : GameWaterCheck);
            if (!shot.Depth && !shot.Masks)
            {
                Sky.DisableKeyword("RG_WATER_DEPTH");
                return;
            }

            Sky.EnableKeyword("RG_WATER_DEPTH");
            Vector3 toSun = -Sun.transform.forward;
            Color sunLight = Sun.color.linear * Sun.intensity, ambient = RenderSettings.ambientSkyColor.linear;
            Color tint = under.linear;
            double[] sigma = WaterOptics.DepthSigma(shot.Clarity, tint.r, tint.g, tint.b);
            double[] down = WaterOptics.DepthSunUnder(toSun.x, toSun.y, toSun.z);
            double share = WaterOptics.DepthSunShare(toSun.y, 0.2126 * sunLight.r + 0.7152 * sunLight.g + 0.0722 * sunLight.b,
                                                     0.2126 * ambient.r + 0.7152 * ambient.g + 0.0722 * ambient.b);
            Sky.SetVector("_RGWaterDepth", new Vector4(shot.Distortion, shot.DepthCaustics, 0f, shot.Masks ? 1f : 0f));
            Sky.SetVector("_RGWaterDepthSigma", new Vector4((float)sigma[0], (float)sigma[1], (float)sigma[2], 0f));
            Sky.SetVector("_RGWaterDepthSun", new Vector4((float)down[0], (float)down[1], (float)down[2], (float)share));
            Log.Add(string.Format(CultureInfo.InvariantCulture,
                "depth: clarity {0} m, under #{1} -> sigma ({2:F4}, {3:F4}, {4:F4}) /m; sun under the water ({5:F3}, {6:F3}, {7:F3}), share {8:F2}; distortion {9}, sunlight {10}{11}",
                shot.Clarity, ColorUtility.ToHtmlStringRGB(under), sigma[0], sigma[1], sigma[2], down[0], down[1], down[2], share, shot.Distortion, shot.DepthCaustics,
                shot.Masks ? "; MASKS" : ""));
        }

        // The run's stand-in check (foam-render -GameWaterCheck) and the seabed scene (-Seabed).
        public float GameWaterCheck;
        public bool SeabedScene;

        /// <summary>The data, light and camera for a view (true at night).</summary>
        public bool Set(string view, float strength)
        {
            float wantTsunami = view.StartsWith("tsunamiwall", StringComparison.Ordinal) ? Coast.Wall
                : view.StartsWith("tsunami", StringComparison.Ordinal) ? 3f : 0f;
            if (Tsunami != wantTsunami)
            {
                Coast.Tsunami = wantTsunami;
                Coast.Bind(FoamToday != null ? new[] { Water, Foam, FoamToday, Surface, Sky } : new[] { Water, Foam, Surface, Sky }, Log);
                Tsunami = wantTsunami;
            }

            Reflection.SetVector("_RGWaterGeom", new Vector4(SeaLevel, strength, 0f, 0f));
            return SetView(view, CameraObject, Sun, Skybox, Capture, SkyCube, Water, Towers);
        }

        /// <summary>
        /// The sky on the water for a shot: the old cube (the stand-in's, x gain) or the sky layer at the
        /// Sky slider gain; the city reflection takes the place of that sky (skyShare = gain, as the mod
        /// sets it), the stand-in's colour.
        /// </summary>
        public void SkyFor(Shot shot, float strength)
        {
            bool layer = shot.SkyMode == 1;
            Water.SetFloat("_SkyGain", layer ? 0f : shot.SkyGain);
            foreach (Renderer r in SkyRenderers)
                r.enabled = layer;
            Shader.SetGlobalVector("_RGWaterSky", new Vector4(shot.SkyGain, SkyInfo.y, SkyInfo.z, 0f));
            Reflection.SetVector("_RGWaterGeom", new Vector4(SeaLevel, strength, shot.SkyGain, 1f));
            Water.SetColor("_BodyColour", shot.Body.HasValue ? shot.Body.Value : BodyColour);
            // The bubbles take the water's own colour, linear (the mod: the colour it draws the water with).
            Color bodyLinear = (shot.Body.HasValue ? shot.Body.Value : BodyColour).linear;
            Foam.SetVector("_RGFoamWater", new Vector4(bodyLinear.r, bodyLinear.g, bodyLinear.b, 0f));
            if (FoamToday != null)
                FoamToday.SetVector("_RGFoamWater", new Vector4(bodyLinear.r, bodyLinear.g, bodyLinear.b, 0f));

            // The ripples' generator, the sky layer's averaging (the FFT waves' table, at the ripples'
            // root-mean-square slope, or the 32 waves' own), and the sun's glitter: the game's highlight
            // off while it draws (the mod sets the material's _SpecColor to black).
            WaveMode = shot.Waves == 1 && Waves != null ? 1 : 0;
            float rms = 0.5f * RippleRms;
            var table = new Vector4[WaterSpectrum.TableLevels];
            Vector4 tail = Vector4.zero;
            if (WaveMode == 1)
            {
                WaterSpectrum s = Waves.Spectrum;
                float ms = rms * rms;
                for (int e = 0; e < table.Length; e++)
                    table[e] = new Vector4((float)s.Table[4 * e] * ms, (float)s.Table[4 * e + 1] * ms, (float)s.Table[4 * e + 2] * ms, (float)s.Table[4 * e + 3]);
                tail = new Vector4((float)s.TailXX * ms, (float)s.TailZZ * ms, (float)s.TailXZ * ms, 1f);
            }
            Shader.SetGlobalVectorArray("_RGWaterSkyWaves", table);
            Shader.SetGlobalVector("_RGWaterSkyTail", tail);
            Vector3 toSun = -Sun.transform.forward;
            Shader.SetGlobalVector("_RGWaterSun", new Vector4(toSun.x, toSun.y, toSun.z, layer ? shot.Glitter : 0f));
            Color light = Sun.color.linear * Sun.intensity;
            Shader.SetGlobalVector("_RGWaterSunColour", new Vector4(light.r, light.g, light.b, shot.GlitterSize));
            Water.SetFloat("_SpecGain", layer && shot.Glitter > 0f ? 0f : 0.6f);
        }

        /// <summary>The game's clocks at t (SimulationManager: x seconds into the minute, w the same as an angle), and the ripples for it.</summary>
        public void Clock(float t)
        {
            float minute = t % 60f;
            Shader.SetGlobalVector("_SimulationTime", new Vector4(minute, minute / 60f, 2f * Mathf.PI * minute, 2f * Mathf.PI * minute / 60f));
            Shader.SetGlobalVector("_SimulationTime2", new Vector4(t, t / 3600f, 0f, 0f));
            // As WaterRipples draws them: one ARGB32 linear texture with mips, anisotropic 4, redrawn.
            if (Ripples == null)
            {
                Ripples = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    useMipMap = true,
                    autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                };
                Ripples.Create();
            }
            if (WaveMode == 1)
                Waves.Draw(Ripples, 0.5f * RippleRms, Chop);
            else
            {
                Generator.SetVectorArray("_RGRippleWaves", Rows);
                Generator.SetVector("_RGRipple", new Vector4(0.5f * RippleRms, 0f, 0f, 0f));
                Graphics.Blit(Texture2D.blackTexture, Ripples, Generator, 0);
            }
            Water.SetTexture("_WaterNormal", Ripples);
            Sky.SetTexture("_WaterNormal", Ripples);
            Reflection.SetTexture("_RGRipples", Ripples);
            Under.SetTexture("_RGRipples", Ripples);
            UnderToday.SetTexture("_RGRipples", Ripples);
        }

        /// <summary>
        /// The view from under the water for a shot (RealGlass/WaterUnder at BeforeImageEffects), or
        /// none; the city reflection off under it, the bridge and the sun's shadows there for it.
        /// </summary>
        public void UnderFor(Shot shot)
        {
            bool on = shot.Under >= 0f;
            CommandBuffer wanted = on ? (shot.UnderToday ? UnderTodayBuffer : UnderBuffer) : null;
            if (wanted != UnderOn)
            {
                // The shadow grab stays on the sun from one under shot to the next (today's or the new
                // pass's): taken off and put back, the next shot read the last view's cascades.
                if (UnderOn != null)
                    Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, UnderOn);
                if (UnderOn == UnderBuffer && UnderOn != null)
                    Camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, UnderDepthBuffer);
                if (wanted == UnderBuffer)
                    Camera.AddCommandBuffer(CameraEvent.AfterFinalPass, UnderDepthBuffer);
                if (wanted != null)
                    Camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, wanted);
                if (UnderOn == null && wanted != null)
                    Sun.AddCommandBuffer(LightEvent.AfterShadowMap, ShadowGrab);
                else if (UnderOn != null && wanted == null)
                    Sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, ShadowGrab);
                UnderOn = wanted;
            }
            // The game's sun casts shadows (CameraController.LateUpdate); the preview's only in the under
            // views, with the bridge, in every column of them.
            bool view = shot.View != null && shot.View.StartsWith("under", StringComparison.Ordinal);
            Sun.shadows = view ? LightShadows.Soft : LightShadows.None;
            Bridge.SetActive(view);
            Under.SetFloat("_RGUnderDebug", shot.Shafts > 1.5f ? 1f : 0f);
            UnderToday.SetFloat("_RGUnderDebug", shot.Shafts > 1.5f ? 1f : 0f);
            if (!on)
                return;

            Reflection.SetVector("_RGWaterGeom", new Vector4(SeaLevel, 0f, shot.SkyGain, 1f));
            Vector3 toSun = -Sun.transform.forward;
            Color sunLight = Sun.color.linear * Sun.intensity;
            UnderOptics(shot.UnderToday ? UnderToday : Under, Mathf.Max(shot.Under, 1f), shot.Caustics, shot.Shafts, shot.VeilGain, toSun, sunLight,
                        RenderSettings.ambientSkyColor.linear, SeaLevel, Log);
        }

        public void ShowFoam(bool on, bool sources)
        {
            ShowFoam(on ? (sources ? 2 : 1) : 0);
        }

        /// <summary>The foam for a shot's mode (<see cref="Shot.Mode"/>).</summary>
        public void ShowFoam(int mode)
        {
            Material m = (mode == 3 || mode == 6) && FoamToday != null ? FoamToday : Foam;
            foreach (Renderer r in FoamRenderers)
            {
                r.enabled = mode != 0;
                r.sharedMaterial = m;
            }
            var debug = new Vector4(mode == 2 ? 1f : mode == 4 ? 4f : mode == 5 || mode == 6 ? 5f : mode == 7 ? 3f : 0f, 0f, 0f, 0f);
            Foam.SetVector("_RGFoamDebug", debug);
            if (FoamToday != null)
                FoamToday.SetVector("_RGFoamDebug", debug);
        }
    }

    public static void Render()
    {
        string dir = Directory.GetCurrentDirectory();
        var log = new List<string>();
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var table = new Vector4[33];
            var rows = new Vector4[64];
            var foam = new Vector4[6];
            var layers = new Vector4[10];
            var shots = new List<Shot>();
            var seqs = new List<Seq>();
            var taa = new float[] { 64f, 32f, 60f, 1f, 0.94f, 0.15f };
            string outDir = dir;
            int width = 960, height = 540;
            float streak = 0f, length = 1f, exposureDay = 1f, exposureNight = 1f, rippleRms = 0.3f, cityGain = 0f;
            var skyRipples = new Vector4(0.64f, 5f, 1024f, 0.0225f);
            var body = new Color(0.03f, 0.06f, 0.08f, 1f);
            // The FFT waves' spectrum (wind m/s, fetch m, heading degrees, seed, choppiness), if asked for.
            double[] waveSpec = null;
            // The flicker runs' surf corridor (as a shot's Corridor: -1 the mod's, 0 no distance map).
            float flickerCorridor = -1f;
            // The water's surface in the depth texture after it (WaterSurfaceMask's merge, 2026-10-28;
            // false: as before), and the stand-ins for the fog and the clouds that read that texture.
            bool waterDepth = true, fogStandIn = false;
            // The "today" under pass reads a depth at the near plane everywhere (white): what the author's
            // game showed on 2026-10-30, when the pass read the global depth texture late in the frame.
            bool underTodayNear = false;
            // The stand-in's check for every shot (PreviewGameWater's _PreviewGameWaterCheck: 0 none), and
            // pixels whose raw values (before the exposure) are logged for every shot.
            float gameWaterCheck = 0f;
            var probePixels = new List<int[]>();
            Shader.SetGlobalFloat("_PreviewGameWaterCheck", 0f);
            // The game's own foam and rain rings: none (the stand-in's and the sky layer's port of them,
            // WaterGame.cginc, then read 0), unless a run binds some.
            Shader.SetGlobalTexture("_WaterFoam", Texture2D.blackTexture);
            Shader.SetGlobalTexture("_RoadUpwardWetXYS", Texture2D.blackTexture);
            Shader.SetGlobalVector("_WeatherParams", Vector4.zero);
            foreach (string raw in File.ReadAllLines(Path.Combine(dir, "foam-preview-input.txt")))
            {
                string[] p = raw.Split(' ');
                if (p[0] == "out")
                    outDir = raw.Substring(4);
                else if (p[0] == "size")
                {
                    width = int.Parse(p[1], CultureInfo.InvariantCulture);
                    height = int.Parse(p[2], CultureInfo.InvariantCulture);
                }
                else if (p[0] == "table")
                    table[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(WaterPreview.F(p[2]), 0f, 0f, 0f);
                else if (p[0] == "ripple")
                    rows[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(WaterPreview.F(p[2]), WaterPreview.F(p[3]), WaterPreview.F(p[4]), WaterPreview.F(p[5]));
                else if (p[0] == "ripplerms")
                    rippleRms = WaterPreview.F(p[1]);
                else if (p[0] == "citygain")
                    cityGain = WaterPreview.F(p[1]);
                else if (p[0] == "streak")
                    streak = WaterPreview.F(p[1]);
                else if (p[0] == "length")
                    length = WaterPreview.F(p[1]);
                else if (p[0] == "exposure")
                {
                    exposureDay = WaterPreview.F(p[1]);
                    exposureNight = WaterPreview.F(p[2]);
                }
                else if (p[0] == "foam")
                    foam[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(WaterPreview.F(p[2]), WaterPreview.F(p[3]), WaterPreview.F(p[4]), WaterPreview.F(p[5]));
                else if (p[0] == "layer")
                    layers[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(WaterPreview.F(p[2]), WaterPreview.F(p[3]), WaterPreview.F(p[4]), WaterPreview.F(p[5]));
                else if (p[0] == "skyripples")
                    skyRipples = new Vector4(WaterPreview.F(p[1]), WaterPreview.F(p[2]), WaterPreview.F(p[3]), WaterPreview.F(p[4]));
                else if (p[0] == "body")
                    body = Hex(p[1]) ?? body;
                else if (p[0] == "spectrum")
                    waveSpec = new double[] { WaterPreview.F(p[1]), WaterPreview.F(p[2]), WaterPreview.F(p[3]), WaterPreview.F(p[4]), WaterPreview.F(p[5]) };
                else if (p[0] == "undertodaynear")
                    underTodayNear = p[1] != "0";
                else if (p[0] == "depthskew")
                    Shader.SetGlobalFloat("_PreviewDepthSkew", WaterPreview.F(p[1]));
                else if (p[0] == "shot")
                    shots.Add(new Shot
                    {
                        Name = p[1], View = p[2], Foam = p[3] != "0", Sources = p[3] == "2", Mode = int.Parse(p[3], CultureInfo.InvariantCulture),
                        Seconds = WaterPreview.F(p[4]), Strength = WaterPreview.F(p[5]),
                        SkyMode = p.Length > 6 ? int.Parse(p[6], CultureInfo.InvariantCulture) : 0,
                        SkyGain = p.Length > 7 ? WaterPreview.F(p[7]) : WaterOptics.SkyShare,
                        Body = p.Length > 8 ? Hex(p[8]) : null,
                        Waves = p.Length > 9 ? int.Parse(p[9], CultureInfo.InvariantCulture) : 0,
                        Glitter = p.Length > 10 ? WaterPreview.F(p[10]) : 0f,
                        GlitterSize = p.Length > 11 ? WaterPreview.F(p[11]) : 1f,
                        Corridor = p.Length > 12 ? WaterPreview.F(p[12]) : -1f,
                        Under = p.Length > 13 ? WaterPreview.F(p[13]) : -1f,
                        Caustics = p.Length > 14 ? WaterPreview.F(p[14]) : 0f,
                        Shafts = p.Length > 15 ? WaterPreview.F(p[15]) : 0f,
                        VeilGain = p.Length > 16 ? WaterPreview.F(p[16]) : 0f,
                        UnderToday = p.Length > 17 && p[17] == "1",
                    });
                else if (p[0] == "depth" && shots.Count > 0)
                {
                    // on (0/1), clarity m, distortion, sunlight on the seabed
                    Shot last = shots[shots.Count - 1];
                    last.Depth = p[1] != "0";
                    last.Clarity = WaterPreview.F(p[2]);
                    last.Distortion = WaterPreview.F(p[3]);
                    last.DepthCaustics = WaterPreview.F(p[4]);
                }
                else if (p[0] == "bed" && shots.Count > 0)
                    shots[shots.Count - 1].Bed = int.Parse(p[1], CultureInfo.InvariantCulture);
                else if (p[0] == "undercolour" && shots.Count > 0)
                    shots[shots.Count - 1].UnderColour = new Color(WaterPreview.F(p[1]), WaterPreview.F(p[2]), WaterPreview.F(p[3]), 1f);
                else if (p[0] == "ownunder" && shots.Count > 0)
                    shots[shots.Count - 1].OwnUnder = new Color(WaterPreview.F(p[1]), WaterPreview.F(p[2]), WaterPreview.F(p[3]), 1f);
                else if (p[0] == "masks" && shots.Count > 0)
                    shots[shots.Count - 1].Masks = p[1] != "0";
                else if (p[0] == "gamewatercheck")
                    gameWaterCheck = WaterPreview.F(p[1]);
                else if (p[0] == "seabed")
                    s_seabed = p[1] != "0";
                else if (p[0] == "probepixel")
                    probePixels.Add(new int[] { int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture) });
                else if (p[0] == "taa")
                {
                    // frames, warm-up frames, frames a second, jitter spread, stationary blending, sharpen
                    for (int k = 0; k < taa.Length; k++)
                        taa[k] = WaterPreview.F(p[k + 1]);
                }
                else if (p[0] == "seq")
                    seqs.Add(new Seq { Name = p[1], View = p[2], Seconds = WaterPreview.F(p[3]), Strength = WaterPreview.F(p[4]) });
                else if (p[0] == "flickercorridor")
                    flickerCorridor = WaterPreview.F(p[1]);
                else if (p[0] == "waterdepth")
                    waterDepth = p[1] != "0";
                else if (p[0] == "fog")
                    fogStandIn = p[1] != "0";
                else if (p[0] == "gamesky")
                    s_gameSky = p[1] != "0";
                else if (p[0] == "ilsheet")
                    s_ilSheet = p[1] != "0";
                else if (p[0] == "skyfog" && shots.Count > 0)
                    shots[shots.Count - 1].SkyFog = p[1] != "0";
            }

            Shader.SetGlobalVectorArray("_RGSeaTable", table);

            // The foam, and the stand-in for the game's water, both on the same data.
            Shader foamShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterFoam.shader");
            log.Add("foam shader " + (foamShader != null ? foamShader.name + " supported=" + foamShader.isSupported : "MISSING"));
            var foamMaterial = new Material(foamShader);
            foamMaterial.SetTexture("_RGFoamPattern", Pattern());
            string[] names = { "_RGFoamScale", "_RGFoamFeature", "_RGFoamShore", "_RGFoamFast", "_RGFoamFront", "_RGFoamLook" };
            for (int i = 0; i < names.Length; i++)
                foamMaterial.SetVector(names[i], foam[i]);
            string[] layerNames = { "_RGFoamLayers", "_RGFoamSheet", "_RGFoamBubbles", "_RGFoamSurf", "_RGFoamScale2", "_RGFoamExtra", "_RGFoamCrest", "_RGFoamAbsorb", "_RGFoamOuter", "_RGFoamSteep" };
            for (int i = 0; i < layerNames.Length; i++)
                foamMaterial.SetVector(layerNames[i], layers[i]);

            // Today's foam, frozen, for the comparison columns: the same pattern, the vectors it knows (a
            // vector the frozen shader does not declare is ignored).
            Material foamToday = null;
            foreach (Shot s in shots)
            {
                if ((s.Mode != 3 && s.Mode != 6) || foamToday != null)
                    continue;
                Shader todayShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewFoamToday.shader");
                log.Add("today's foam shader " + (todayShader != null ? todayShader.name + " supported=" + todayShader.isSupported : "MISSING"));
                foamToday = new Material(todayShader);
                foamToday.SetTexture("_RGFoamPattern", foamMaterial.GetTexture("_RGFoamPattern"));
                for (int i = 0; i < names.Length; i++)
                    foamToday.SetVector(names[i], foam[i]);
                for (int i = 0; i < layerNames.Length; i++)
                    foamToday.SetVector(layerNames[i], layers[i]);
            }
            var water = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewGameWater.shader"));
            water.SetColor("_BodyColour", body);

            // The sky layer (RealGlass/WaterSky) on the same meshes, as WaterSkyLayer draws it.
            Shader skyShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterSky.shader");
            log.Add("sky shader " + (skyShader != null ? skyShader.name + " supported=" + skyShader.isSupported : "MISSING"));
            var skyMaterial = new Material(skyShader);

            // The real distance from the waterline, as the mod maps it (ShoreDistance, synced).
            Vector4 shoreMapping;
            foamMaterial.SetTexture("_RGShoreDistance", ShoreMap(out shoreMapping, log));
            foamMaterial.SetVector("_RGShoreMapping", shoreMapping);
            if (foamToday != null)
            {
                foamToday.SetTexture("_RGShoreDistance", foamMaterial.GetTexture("_RGShoreDistance"));
                foamToday.SetVector("_RGShoreMapping", shoreMapping);
            }

            var coast = new Coast();
            var waterRenderers = new List<Renderer>();
            var foamRenderers = new List<Renderer>();
            var skyRenderers = new List<Renderer>();
            Material groundMaterial;
            Texture2D[] beds;
            BuildScene(water, foamMaterial, skyMaterial, waterRenderers, foamRenderers, skyRenderers, out groundMaterial, out beds);
            List<Tower> towers = BuildTowers();
            log.Add("scene: " + waterRenderers.Count + " water tiles, " + towers.Count + " towers");

            // Light, sky, the sky's cube for the water (as WaterPreview).
            var skybox = s_gameSky ? new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewGameSky.shader"))
                : new Material(Shader.Find("Skybox/Procedural"));
            RenderSettings.skybox = skybox;
            log.Add("sky: " + (s_gameSky ? "the game's (Editor/PreviewGameSky.shader), with its fog over the sky on screen" : "Unity's procedural"));
            var sunObject = new GameObject("preview sun");
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;
            RenderSettings.sun = sun;
            var skyCube = new RenderTexture(128, 128, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
            };
            skyCube.Create();
            Shader.SetGlobalTexture("_RGWaterSkyCube", skyCube);
            Shader.SetGlobalVector("_RGWaterSkyRipples", skyRipples);
            var captureObject = new GameObject("preview sky capture");
            Camera capture = captureObject.AddComponent<Camera>();
            capture.enabled = false;
            capture.clearFlags = CameraClearFlags.Skybox;
            capture.cullingMask = 0;
            capture.allowHDR = true;
            capture.renderingPath = RenderingPath.Forward;
            water.SetTexture("_Sky", skyCube);

            var cameraObject = new GameObject("preview camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredLighting;
            camera.allowHDR = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 20000f;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture = target;

            // The city reflection on the sea, as WaterCityReflection.Record records it.
            var reflection = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/CityReflection.shader"));
            // The sky the city takes the place of: the live sky, light already (the mod's with the sky layer).
            reflection.SetTexture("_RGWaterTheme", skyCube);
            var cube = WaterPreview.UnitCube();

            // The game's fog over the sky (GlassUtil's SkyFog): in the water's cube for the shots that ask
            // (as GlassSky records it, right after the skybox), and on screen with -GameSky (the game's
            // fog runs after the opaque passes).
            var skyFogMaterial = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/GlassUtil.shader"));
            var skyFogBlock = new MaterialPropertyBlock();
            skyFogBlock.SetVector("_RGSkyFog", new Vector4(camera.farClipPlane, 0f, 0f, 0f));
            var cubeFog = new CommandBuffer { name = "the game's fog in the water's sky (GlassSky's)" };
            cubeFog.DrawMesh(cube, Matrix4x4.identity, skyFogMaterial, 0, 7, skyFogBlock);
            if (s_gameSky)
            {
                var screenFog = new CommandBuffer { name = "the game's fog over the sky (preview)" };
                screenFog.DrawMesh(cube, Matrix4x4.identity, skyFogMaterial, 0, 7, skyFogBlock);
                camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, screenFog);
            }
            int w = Mathf.Max(1, width / 2), h = Mathf.Max(1, height / 2);
            RenderTexture frame = WaterPreview.Target(w, h, true), trace = WaterPreview.Target(w, h, true), streaked = WaterPreview.Target(w, h, false);
            reflection.SetTexture("_RGWaterFrame", frame);
            var buffer = new CommandBuffer { name = "RealGlass water reflection (preview)" };
            // The water's surface, as WaterSurfaceMask records it: right after the forward water, against
            // the scene depth copied at the final pass (which the reflection traces through too); then,
            // merged into that copy, bound as the depth texture of every pass after (the fog, the clouds).
            Shader maskShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterSurfaceMask.shader");
            var surface = new Material(maskShader);
            var surfaceDepth = new RenderTexture(width, height, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            surfaceDepth.filterMode = FilterMode.Point;
            surfaceDepth.Create();
            var sceneDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            var mergedDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            sceneDepth.Create();
            mergedDepth.Create();
            var sceneCopy = new CommandBuffer { name = "RealGlass scene depth before the water (preview)" };
            // The realistic depth's seabed: the frame copied here with the scene's depth, as
            // WaterSurfaceMask's buffer copies them (the frame first), on the sky layer's material.
            var bedFrame = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            bedFrame.Create();
            sceneCopy.Blit(BuiltinRenderTextureType.CameraTarget, bedFrame);
            sceneCopy.Blit(BuiltinRenderTextureType.CameraTarget, sceneDepth, new Material(maskShader), 1);
            skyMaterial.SetTexture("_RGWaterBedFrame", bedFrame);
            skyMaterial.SetTexture("_RGWaterBedDepth", sceneDepth);
            camera.AddCommandBuffer(CameraEvent.AfterFinalPass, sceneCopy);
            surface.SetTexture("_CameraDepthTexture", sceneDepth);
            reflection.SetTexture("_CameraDepthTexture", sceneDepth);
            reflection.SetTexture("_RGWaterSurfaceDepth", surfaceDepth);
            var surfaceBuffer = new CommandBuffer { name = "RealGlass water surface depth (preview)" };
            surfaceBuffer.SetRenderTarget(surfaceDepth);
            surfaceBuffer.ClearRenderTarget(true, true, Color.clear);
            foreach (Renderer renderer in waterRenderers)
                surfaceBuffer.DrawMesh(renderer.GetComponent<MeshFilter>().sharedMesh, renderer.transform.localToWorldMatrix, surface, 0, 0);
            if (waterDepth)
            {
                var merge = new Material(maskShader);
                merge.SetTexture("_CameraDepthTexture", sceneDepth);
                merge.SetTexture("_RGWaterSurfaceDepth", surfaceDepth);
                surfaceBuffer.Blit(BuiltinRenderTextureType.CameraTarget, mergedDepth, merge, 2);
                surfaceBuffer.SetGlobalTexture("_CameraDepthTexture", mergedDepth);
            }
            surfaceBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, surfaceBuffer);
            log.Add("the water's surface in the depth texture after it: " + (waterDepth ? "yes" : "no (as before)") +
                    "; the fog and clouds stand-ins: " + (fogStandIn ? "yes" : "no"));
            if (fogStandIn)
            {
                // The game's fog after the opaque passes (after the surface: buffers run as added), the
                // clouds with the transparents (before the image effects).
                var fogSky = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewFogSky.shader"));
                // Their colours are globals, set per view by SetView (night: a dark horizon's).
                var fog = new CommandBuffer { name = "the game's fog (preview stand-in)" };
                fog.DrawMesh(cube, Matrix4x4.identity, fogSky, 0, 0);
                camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, fog);
                var clouds = new CommandBuffer { name = "Volumetric Weather's clouds (preview stand-in)" };
                clouds.DrawMesh(cube, Matrix4x4.identity, fogSky, 0, 1);
                camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, clouds);
            }
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, frame);
            buffer.SetRenderTarget(trace);
            buffer.ClearRenderTarget(false, true, Color.clear);
            buffer.DrawMesh(cube, Matrix4x4.identity, reflection, 0, 0);
            // The history passes with no history (a still: _RGWaterHistoryInfo stays 0): they read the
            // trace's packed alpha and write the light and the share covered, each to its own buffer.
            RenderTexture accum = WaterPreview.Target(w, h, true), accum2 = WaterPreview.Target(w, h, true), streaked2 = WaterPreview.Target(w, h, false);
            reflection.SetTexture("_RGWaterTraced", trace);
            buffer.SetRenderTarget(accum);
            buffer.DrawMesh(cube, Matrix4x4.identity, reflection, 0, 3);
            buffer.Blit(accum, streaked, reflection, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked", streaked);
            buffer.SetRenderTarget(accum2);
            buffer.DrawMesh(cube, Matrix4x4.identity, reflection, 0, 4);
            buffer.Blit(accum2, streaked2, reflection, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked2", streaked2);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            buffer.DrawMesh(cube, Matrix4x4.identity, reflection, 0, 2);
            camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, buffer);
            reflection.SetVector("_RGWaterMarch", new Vector4(0.9f * camera.farClipPlane, 30f, 0.05f, Mathf.Max(1f, length)));
            reflection.SetVector("_RGWaterStreak", new Vector4(streak, 1f / h, 0f, 0f));
            reflection.SetVector("_RGWaterWave", new Vector4(cityGain * 2f, 3f, 1f, 0f));
            reflection.SetVector("_RGWaterLight", new Vector4(1f, 1f, 1f, 0f));

            // The view from under the water, as the mod would draw it: a copy of the frame, then the pass
            // on a cube round the camera, on the scene depth from before the water; the sun's shadow map
            // and its cascades grabbed after the light renders it (WaterUnder.shader's pass 1).
            Shader underShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterUnder.shader");
            log.Add("under shader " + (underShader != null ? underShader.name + " supported=" + underShader.isSupported : "MISSING"));
            var under = new Material(underShader);
            // Its own copy of the scene's depth after the final pass (pass 2), held as a temporary texture
            // to the pass, as WaterUnder.cs does.
            int underDepth = Shader.PropertyToID("_RGUnderDepth");
            var underDepthBuffer = new CommandBuffer { name = "RealGlass the scene's depth for under the water (preview)" };
            underDepthBuffer.GetTemporaryRT(underDepth, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            underDepthBuffer.Blit(BuiltinRenderTextureType.CameraTarget, underDepth, under, 2);
            var shadowData = new RenderTexture(24, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            shadowData.Create();
            under.SetTexture("_RGSunShadowData", shadowData);
            var shadowGrab = new CommandBuffer { name = "RealGlass the sun's shadow map (preview)" };
            shadowGrab.SetGlobalTexture("_RGSunShadow", new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive));
            shadowGrab.Blit(BuiltinRenderTextureType.CurrentActive, shadowData, new Material(underShader), 1);
            var underBuffer = new CommandBuffer { name = "RealGlass under the water (preview)" };
            int underFrame = Shader.PropertyToID("_RGUnderFrame");
            underBuffer.GetTemporaryRT(underFrame, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            underBuffer.Blit(BuiltinRenderTextureType.CameraTarget, underFrame);
            underBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            underBuffer.DrawMesh(cube, Matrix4x4.identity, under, 0, 0);
            underBuffer.ReleaseTemporaryRT(underFrame);
            underBuffer.ReleaseTemporaryRT(underDepth);
            // The same as it stood at affebf9, before the surface from below was redone.
            Shader underTodayShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewUnderToday.shader");
            log.Add("under (today) shader " + (underTodayShader != null ? underTodayShader.name + " supported=" + underTodayShader.isSupported : "MISSING"));
            var underToday = new Material(underTodayShader);
            underToday.SetTexture("_CameraDepthTexture", underTodayNear ? (Texture)Texture2D.whiteTexture : sceneDepth);
            underToday.SetTexture("_RGSunShadowData", shadowData);
            var underTodayBuffer = new CommandBuffer { name = "RealGlass under the water, today (preview)" };
            underTodayBuffer.GetTemporaryRT(underFrame, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            underTodayBuffer.Blit(BuiltinRenderTextureType.CameraTarget, underFrame);
            underTodayBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            underTodayBuffer.DrawMesh(cube, Matrix4x4.identity, underToday, 0, 0);
            underTodayBuffer.ReleaseTemporaryRT(underFrame);
            // The game's own shadow settings at their highest (the options' Very High: four cascades).
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowDistance = 400f;
            QualitySettings.shadowProjection = ShadowProjection.StableFit;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            GameObject bridge = BuildBridge();

            var scene = new Scene
            {
                Under = under, UnderBuffer = underBuffer, UnderDepthBuffer = underDepthBuffer, UnderToday = underToday, UnderTodayBuffer = underTodayBuffer, ShadowGrab = shadowGrab, Bridge = bridge,
                Coast = coast, Camera = camera, CameraObject = cameraObject, Target = target, Sun = sun, Skybox = skybox, Water = water,
                Foam = foamMaterial, Reflection = reflection, Surface = surface, Sky = skyMaterial, Capture = capture, SkyCube = skyCube, Towers = towers,
                CubeFog = cubeFog, Ground = groundMaterial, Beds = beds,
                FoamRenderers = foamRenderers, SkyRenderers = skyRenderers, Rows = rows, RippleRms = rippleRms, Log = log, BodyColour = body,
                SkyInfo = new Vector4(1f, skyCube.width, 7f, 0f),
                Generator = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterRipples.shader")),
            };
            if (waveSpec != null)
            {
                Shader wavesShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterWaves.shader");
                log.Add("waves shader " + (wavesShader != null ? wavesShader.name + " supported=" + wavesShader.isSupported : "MISSING"));
                var spectrum = new WaterSpectrum(waveSpec[0], waveSpec[1], waveSpec[2], (uint)waveSpec[3]);
                scene.Waves = new WaterWaves(wavesShader);
                scene.Waves.Upload(spectrum);
                scene.Chop = (float)waveSpec[4];
                log.Add(string.Format(CultureInfo.InvariantCulture,
                    "spectrum: wind {0} m/s, fetch {1} km, heading {2} deg: {3} waves, peak {4:F1} m; choppiness {5}; tail {6:F3} of the texture's slopes",
                    waveSpec[0], waveSpec[1] / 1000.0, waveSpec[2], spectrum.WaveCount, spectrum.PeakWavelength, waveSpec[4], spectrum.TailXX + spectrum.TailZZ));
            }
            scene.FoamToday = foamToday;
            scene.GameWaterCheck = gameWaterCheck;
            scene.SeabedScene = s_seabed;
            Shader.SetGlobalColor("_WaterColorUnder", Scene.NeutralUnder());
            scene.SkyFor(new Shot(), 1f);
            scene.UnderFor(new Shot());
            if (seqs.Count > 0)
            {
                Vector4 flickerSteep = layers[9];
                if (flickerCorridor > 0f)
                    flickerSteep.z = flickerCorridor;
                foamMaterial.SetVector("_RGFoamSteep", flickerSteep);
                foamMaterial.SetVector("_RGShoreMapping", flickerCorridor == 0f ? Vector4.zero : shoreMapping);
                log.Add("flicker runs: surf corridor " + (flickerCorridor == 0f ? "none (no distance map)" : flickerSteep.z.ToString(CultureInfo.InvariantCulture)));
                Flicker(seqs, scene, taa, exposureDay, exposureNight, outDir);
            }

            foreach (Shot shot in shots)
            {
                scene.CubeFogFor(shot);
                bool night = scene.Set(shot.View, shot.Strength);
                scene.SkyFor(shot, shot.Strength);
                scene.DepthFor(shot);
                scene.UnderFor(shot);
                scene.ShowFoam(shot.Mode);
                Vector4 steep = layers[9];
                if (shot.Corridor > 0f)
                    steep.z = shot.Corridor;
                foamMaterial.SetVector("_RGFoamSteep", steep);
                foamMaterial.SetVector("_RGShoreMapping", shot.Corridor == 0f ? Vector4.zero : shoreMapping);
                if (foamToday != null)
                {
                    foamToday.SetVector("_RGFoamSteep", steep);
                    foamToday.SetVector("_RGShoreMapping", shot.Corridor == 0f ? Vector4.zero : shoreMapping);
                }
                scene.Clock(shot.Seconds);

                // Under the water, a frame first: the shadow grab fills the sun's cascades for this view
                // (the run's first under shot read none: all sunlit, no shafts).
                if (shot.Under >= 0f)
                    camera.Render();
                camera.Render();
                RenderTexture.active = target;
                var read = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = null;

                float exposure = night ? exposureNight : exposureDay;
                Color[] px = read.GetPixels();
                foreach (int[] at in probePixels)
                {
                    Color c = px[Mathf.Clamp(at[1], 0, height - 1) * width + Mathf.Clamp(at[0], 0, width - 1)];
                    log.Add(string.Format(CultureInfo.InvariantCulture, "pixel {0} {1} of {2}: {3:F5} {4:F5} {5:F5}", at[0], at[1], shot.Name, c.r, c.g, c.b));
                }
                var o = new Color[px.Length];
                if (shot.Masks)
                {
                    // The two masks (the stand-in's red 1000, the sky layer's green 1000): white where both
                    // draw, red where only the game's water does (no seabed there with the depth on), green
                    // where only the sky layer does.
                    // The stand-in's red holds 1000 + 100 x the share of the bed it takes away there.
                    int both = 0, gameOnly = 0, layerOnly = 0, lostOver5 = 0;
                    float lostMost = 0f, lostSum = 0f;
                    for (int i = 0; i < px.Length; i++)
                    {
                        bool game = px[i].r > 500f, layer = px[i].g > 500f;
                        if (game && layer) both++;
                        else if (game)
                        {
                            gameOnly++;
                            float lost = Mathf.Clamp01((px[i].r - 1000f) / 100f);
                            lostMost = Mathf.Max(lostMost, lost);
                            lostSum += lost;
                            if (lost > 0.05f) lostOver5++;
                        }
                        else if (layer) layerOnly++;
                        o[i] = game && layer ? Color.white : game ? Color.red : layer ? Color.green : Color.black;
                    }
                    log.Add(string.Format(CultureInfo.InvariantCulture,
                        "masks of {0}: both {1}, the game's water only {2} (the bed lost there: most {5:P0}, mean {6:P1}, over 5% on {7}), the sky layer only {3} (of {4} pixels)",
                        shot.Name, both, gameOnly, layerOnly, px.Length, lostMost, gameOnly > 0 ? lostSum / gameOnly : 0f, lostOver5));
                }
                else
                {
                    for (int i = 0; i < px.Length; i++)
                        o[i] = new Color(WaterPreview.Tone(px[i].r * exposure), WaterPreview.Tone(px[i].g * exposure), WaterPreview.Tone(px[i].b * exposure), 1f);
                }
                WaterPreview.WritePng(Path.Combine(outDir, shot.Name + ".png"), o, width, height);
                UnityEngine.Object.DestroyImmediate(read);
                log.Add("shot " + shot.Name + " ok");
            }
        }
        catch (Exception e)
        {
            log.Add("EXCEPTION " + e);
        }

        File.WriteAllLines(Path.Combine(dir, "foam-preview-output.txt"), log.ToArray());
    }

    /// <summary>
    /// The foam's flicker under temporal anti-aliasing: per run, the pixels the foam changes (a still
    /// with and without it, more than 2/255 apart), then the run twice -- with the foam and without --
    /// each frame's projection jittered (Halton 2, 3 over 8 frames, as Post-processing Stack v1) and the
    /// game's clocks running, resolved by PreviewTAA.shader; per pixel the mean |second difference in
    /// time| of its displayed brightness after the warm-up. Logged as the share of those pixels over
    /// 2/255 and over 8/255, with TAA and without; the last resolved frame and the flicker map as PNGs.
    /// </summary>
    private static void Flicker(List<Seq> seqs, Scene scene, float[] taa, float exposureDay, float exposureNight, string outDir)
    {
        int frames = (int)taa[0], warm = (int)taa[1];
        float fps = taa[2], spread = taa[3], blend = taa[4], sharpen = taa[5];
        RenderTexture target = scene.Target;
        int width = target.width, height = target.height, n = width * height;
        var resolve = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewTAA.shader"));
        RenderTexture resolved = WaterPreview.Target(width, height, false), history = WaterPreview.Target(width, height, false);
        var read = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
        var lum = new float[3][];
        var raw = new float[3][];
        for (int k = 0; k < 3; k++)
        {
            lum[k] = new float[n];
            raw[k] = new float[n];
        }
        var flicker = new double[n];
        var flickerRaw = new double[n];
        scene.Log.Add("taa: " + frames + " frames (" + warm + " to warm up) at " + fps + " a second; jitter " + spread + ", blending " + blend + ", sharpen " + sharpen);

        foreach (Seq s in seqs)
        {
            bool night = scene.Set(s.View, s.Strength);
            float exposure = night ? exposureNight : exposureDay;

            // The foam's pixels: a still with it and one without.
            scene.Clock(s.Seconds);
            scene.Camera.ResetProjectionMatrix();
            var with = new float[n];
            var without = new float[n];
            scene.ShowFoam(true, false);
            scene.Camera.Render();
            WaterPreview.Brightness(target, read, with, exposure);
            scene.ShowFoam(false, false);
            scene.Camera.Render();
            WaterPreview.Brightness(target, read, without, exposure);
            var mask = new bool[n];
            int masked = 0;
            for (int p = 0; p < n; p++)
            {
                mask[p] = Math.Abs(with[p] - without[p]) > 2f / 255f;
                if (mask[p])
                    masked++;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool foam = pass == 0;
                scene.ShowFoam(foam, false);
                Array.Clear(flicker, 0, n);
                Array.Clear(flickerRaw, 0, n);
                int measured = 0;
                for (int f = 0; f < frames; f++)
                {
                    scene.Clock(s.Seconds + f / fps);
                    int i = f % 8;
                    float jx = (WaterPreview.Halton(i + 1, 2) - 0.5f) * spread, jy = (WaterPreview.Halton(i + 1, 3) - 0.5f) * spread;
                    scene.Camera.ResetProjectionMatrix();
                    Matrix4x4 projection = scene.Camera.projectionMatrix;
                    projection.m02 += 2f * jx / width;
                    projection.m12 += 2f * jy / height;
                    scene.Camera.projectionMatrix = projection;
                    scene.Camera.Render();

                    resolve.SetTexture("_HistoryTex", history);
                    resolve.SetVector("_Jitter", new Vector4(jx / width, jy / height, 0f, 0f));
                    resolve.SetVector("_TAA", new Vector4(sharpen, blend, f == 0 ? 1f : 0f, 0f));
                    Graphics.Blit(target, resolved, resolve, 0);
                    Graphics.Blit(resolved, history);

                    if (f >= warm - 2)
                    {
                        WaterPreview.Brightness(resolved, read, lum[f % 3], exposure);
                        WaterPreview.Brightness(target, read, raw[f % 3], exposure);
                        if (f >= warm)
                        {
                            float[] a = lum[f % 3], b = lum[(f + 2) % 3], c = lum[(f + 1) % 3];
                            float[] ra = raw[f % 3], rb = raw[(f + 2) % 3], rc = raw[(f + 1) % 3];
                            for (int p = 0; p < n; p++)
                            {
                                flicker[p] += Math.Abs(a[p] - 2f * b[p] + c[p]);
                                flickerRaw[p] += Math.Abs(ra[p] - 2f * rb[p] + rc[p]);
                            }
                            measured++;
                        }
                    }
                }
                scene.Camera.ResetProjectionMatrix();

                int over2 = 0, over8 = 0, rawOver2 = 0, rawOver8 = 0;
                double sum = 0.0, rawSum = 0.0;
                for (int p = 0; p < n; p++)
                {
                    if (!mask[p])
                        continue;
                    double v = flicker[p] / Math.Max(1, measured), rv = flickerRaw[p] / Math.Max(1, measured);
                    sum += v;
                    rawSum += rv;
                    if (v > 2.0 / 255.0) over2++;
                    if (v > 8.0 / 255.0) over8++;
                    if (rv > 2.0 / 255.0) rawOver2++;
                    if (rv > 8.0 / 255.0) rawOver8++;
                }
                double w = Math.Max(1, masked);
                scene.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "flicker {0} {1}: foam pixels {2:F1}% of the frame | TAA: mean {3:F2}/255, over 2/255 {4:F2}%, over 8/255 {5:F2}% | no TAA: mean {6:F2}/255, over 2/255 {7:F2}%, over 8/255 {8:F2}%",
                    s.Name, foam ? "with foam" : "without", 100.0 * masked / n, 255.0 * sum / w, 100.0 * over2 / w, 100.0 * over8 / w,
                    255.0 * rawSum / w, 100.0 * rawOver2 / w, 100.0 * rawOver8 / w));

                if (!foam)
                    continue;
                RenderTexture.active = resolved;
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = null;
                Color[] px = read.GetPixels();
                var picture = new Color[n];
                var map = new Color[n];
                for (int p = 0; p < n; p++)
                {
                    Color c = new Color(WaterPreview.Tone(px[p].r * exposure), WaterPreview.Tone(px[p].g * exposure), WaterPreview.Tone(px[p].b * exposure), 1f);
                    picture[p] = c;
                    float v = (float)(flicker[p] / Math.Max(1, measured));
                    map[p] = !mask[p] ? c * 0.33f : v > 8f / 255f ? new Color(1f, 0.15f, 0.1f, 1f) : new Color(Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), 1f);
                    map[p].a = 1f;
                }
                WaterPreview.WritePng(Path.Combine(outDir, s.Name + ".png"), picture, width, height);
                WaterPreview.WritePng(Path.Combine(outDir, s.Name + "-flicker.png"), map, width, height);
            }
        }
    }

    /// <summary>
    /// The view from under the water's uniforms, as Water/WaterUnder.cs sets them: the mod's own optics
    /// (Water/WaterUnderOptics.cs, compiled here from the mod's source by sync-sim.ps1), the ripples'
    /// slope and the world's displacement through the surface, the shafts' steps at the mod's numbers.
    /// </summary>
    private static void UnderOptics(Material m, float visibility, float caustics, float shafts, float veilGain, Vector3 toSun, Color sunLight, Color skyAmbient,
                                    float level, List<string> log)
    {
        double[] v = WaterUnderOptics.Vectors(visibility, new double[] { toSun.x, toSun.y, toSun.z }, new double[] { sunLight.r, sunLight.g, sunLight.b },
                                              new double[] { skyAmbient.r, skyAmbient.g, skyAmbient.b });
        if (veilGain > 0f)
            for (int i = 8; i < 11; i++)
                v[i] *= veilGain / WaterUnderOptics.VeilGain;
        // w: today's frozen copy still nudges the frame by 0.015 uv a unit of slope; the mod's is unused.
        m.SetVector("_RGUnder", new Vector4(level, caustics, 0.25f, m.shader.name == "RealGlass/WaterUnder" ? 0f : 0.015f));
        m.SetVector("_RGUnderC", new Vector4((float)v[0], (float)v[1], (float)v[2], 0f));
        m.SetVector("_RGUnderK", new Vector4((float)v[4], (float)v[5], (float)v[6], 0f));
        m.SetVector("_RGUnderVeil", new Vector4((float)v[8], (float)v[9], (float)v[10], 0f));
        m.SetVector("_RGUnderSun", new Vector4((float)v[12], (float)v[13], (float)v[14], (float)v[15]));
        // The shafts: 24 steps over up to 6 / c at green (where the veil is 99.75% made), the light's
        // entry tested a metre over the surface.
        m.SetVector("_RGUnderShafts", new Vector4(shafts > 0f ? 1f : 0f, 24f, 6f / (float)v[1], 1f));
        // The water's live sky cube (here 128 px, 7 mips) and the sun's light, for the surface from below.
        m.SetVector("_RGUnderSky", new Vector4(1f, 128f, 7f, 0f));
        m.SetVector("_RGUnderSunLight", new Vector4(sunLight.r, sunLight.g, sunLight.b, 0f));
        log.Add(string.Format(CultureInfo.InvariantCulture,
            "under: visibility {0} m -> c ({1:F3}, {2:F3}, {3:F3}) /m, K_d ({4:F3}, {5:F3}, {6:F3}) /m, veil ({7:F4}, {8:F4}, {9:F4}), sun share {10:F2}, caustics {11}, shafts {12}",
            visibility, v[0], v[1], v[2], v[4], v[5], v[6], v[8], v[9], v[10], v[15], caustics, shafts));
    }

    /// <summary>
    /// A bridge over the sea for the views from under the water (a player's picture of the look had
    /// one): a deck 9 m over the water across the view, three piers, and hangers over the deck whose
    /// shadows stripe the water -- what the sun's shafts are seen between.
    /// </summary>
    private static GameObject BuildBridge()
    {
        var root = new GameObject("preview bridge");
        var concrete = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = new Color(0.42f, 0.41f, 0.39f) };
        Action<Vector3, Vector3> box = (centre, size) =>
        {
            GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.transform.position = centre;
            b.transform.localScale = size;
            b.GetComponent<Renderer>().sharedMaterial = concrete;
            b.transform.parent = root.transform;
        };
        const float x = -200f;
        box(new Vector3(x, SeaLevel + 9.75f, 0f), new Vector3(12f, 1.5f, 260f));
        foreach (float z in new[] { -40f, 20f, 80f })
            box(new Vector3(x, SeaLevel - 10f, z), new Vector3(4f, 39.5f, 4f));
        for (float z = -119f; z <= 120f; z += 7f)
        {
            box(new Vector3(x - 5.5f, SeaLevel + 15f, z), new Vector3(1.2f, 9f, 1.2f));
            box(new Vector3(x + 5.5f, SeaLevel + 15f, z), new Vector3(1.2f, 9f, 1.2f));
        }
        box(new Vector3(x - 5.5f, SeaLevel + 19.75f, 0f), new Vector3(0.8f, 0.8f, 260f));
        box(new Vector3(x + 5.5f, SeaLevel + 19.75f, 0f), new Vector3(0.8f, 0.8f, 260f));
        return root;
    }

    /// <summary>The foam's pattern texture, from the mod's own generator (FoamPattern.cs, synced).</summary>
    private static Texture2D Pattern()
    {
        byte[] rgba = FoamPattern.Generate();
        var t = new Texture2D(FoamPattern.Size, FoamPattern.Size, TextureFormat.RGBA32, true, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        var px = new Color32[FoamPattern.Size * FoamPattern.Size];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color32(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
        t.SetPixels32(px);
        t.Apply(true);
        return t;
    }

    /// <summary>
    /// The real distance from the waterline, built by the mod's own ShoreDistance from the coast's
    /// terrain at the game's 16 m points (the game: TerrainManager's 1081 x 1081), an Alpha8 texture as
    /// RealGlass/Water/ShoreMap.cs holds it; and, for its cost, a map the game's size built once.
    /// </summary>
    private static Texture2D ShoreMap(out Vector4 mapping, List<string> log)
    {
        const float cell = 16f;
        int points = (int)(2f * Half / cell) + 1;
        var heights = new ushort[points * points];
        for (int j = 0; j < points; j++)
        {
            for (int i = 0; i < points; i++)
                heights[j * points + i] = Encode(Coast.Ground(-Half + i * cell, -Half + j * cell));
        }

        var distance = new ShoreDistance(points, cell);
        distance.Build(heights, null, HeightScale / 65536f, SeaLevel);
        var t = new Texture2D(distance.Side, distance.Side, TextureFormat.Alpha8, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        t.LoadRawTextureData(distance.Map);
        t.Apply(false);
        float scale, offset;
        distance.Mapping(-Half, out scale, out offset);
        mapping = new Vector4(scale, offset, ShoreDistance.Step, 1f);

        // The game's size: its 1081 points a side, here the coast repeated (the cost, not the look).
        const int gamePoints = 1081;
        var game = new ushort[gamePoints * gamePoints];
        for (int j = 0; j < gamePoints; j++)
        {
            for (int i = 0; i < gamePoints; i++)
                game[j * gamePoints + i] = heights[(j % (points - 1)) * points + i % (points - 1)];
        }
        var big = new ShoreDistance(gamePoints, cell);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        big.Build(game, game, HeightScale / 65536f, SeaLevel);
        long ms = watch.ElapsedMilliseconds;
        log.Add(string.Format(CultureInfo.InvariantCulture, "shore distances: {0} x {0} at {1} m, {2:F1}% sea, {3} coast samples; a game-sized map ({4} x {4}) in {5} ms",
                              distance.Side, distance.Spacing, 100.0 * distance.SeaSamples / ((double)distance.Side * distance.Side), distance.CoastSamples, big.Side, ms));
        return t;
    }

    /// <summary>
    /// The coast's shape and water, as functions of world x, z. The sea at <see cref="SeaLevel"/> west
    /// of a waterline that wanders round x = 0, its bed rising from 14 m deep 500 m out to the beach
    /// (south of z = -450 steeply, 12 cm a metre: the game's coasts);
    /// the land rising to a plateau 6-10 m above the sea; the river in a 64 m channel along z = 210
    /// (meandering 40 m), its surface climbing 0.3% and 3 m at a rapid 380-440 m inland. Round
    /// z = -1350 a bay cut 170 m into the steep coast, its floor a flat shelf 1.6 m deep sagging to
    /// 2.4 m in its middle, the seabed dropping steeply again past its mouth (the author's screenshot,
    /// 2026-10-25: there the surf closed into a ring).
    /// </summary>
    private sealed class Coast
    {
        /// <summary>The bay: its middle along z, its half-width, how far it reaches inland, its shelf's depth and its sag's radius (m).</summary>
        public const float BayZ = -1350f, BayHalf = 280f, BayIndent = 170f, BayShelf = 1.6f, BayBowl = 80f;

        /// <summary>The tsunami's height over the sea (m; 0 none).</summary>
        public float Tsunami;

        /// <summary>
        /// The "tsunamiwall" views' tsunami (m): a wall higher than the camera, so that what lies behind
        /// its face -- its crest, its back, the sea beyond -- must not show through it.
        /// </summary>
        public const float Wall = 20f;

        /// <summary>
        /// The tsunami's front in the older frame, m from the waterline, and how far it runs a frame: a
        /// long wave's speed in 14 m of water, sqrt(g h) = 11.7 m/s, over a water frame (64 simulation
        /// frames: TerrainManager.HeightMap_sampleWaterHeightAndNormal, about a second at normal speed).
        /// </summary>
        public const float Front = -650f;

        public const float Step = 12.5f;

        public static float ShoreX(float z)
        {
            return 30f * Mathf.Sin(z / 140f) + 14f * Mathf.Sin(z / 53f + 1f) + Bay(z);
        }

        /// <summary>How far the bay reaches inland at z (m; 0 outside it).</summary>
        public static float Bay(float z)
        {
            float t = (z - BayZ) / BayHalf;
            if (Mathf.Abs(t) >= 1f)
                return 0f;
            float c = Mathf.Cos(0.5f * Mathf.PI * t);
            return BayIndent * c * c;
        }

        /// <summary>
        /// The bay's water depth (m) at s m from its waterline (negative: the water), bay its reach there:
        /// a beach face as steep as the coast's down to the shelf, the shelf sagging in its middle, and
        /// past the coast's line (its mouth) the coast's own drop below the shelf.
        /// </summary>
        private static float BayDepth(float x, float z, float s, float bay)
        {
            float inside = s + bay;
            if (inside > 0f)
            {
                float dx = x - (ShoreX(BayZ) - 0.5f * BayIndent), dz = z - BayZ;
                float r2 = (dx * dx + dz * dz) / (BayBowl * BayBowl);
                float sag = r2 < 1f ? (1f - r2) * (1f - r2) : 0f;
                return Mathf.Min(0.12f * -s, BayShelf + 0.8f * sag);
            }

            return Mathf.Min(14f, Mathf.Min(BayShelf, 0.12f * bay) - 0.12f * inside);
        }

        /// <summary>The terraformed shelf (a player's harbour, 2026-11-07): from z to z (m), its depth, the deep water past its edge (m).</summary>
        public const float ShelfZ0 = 360f, ShelfZ1 = 1000f, ShelfDepthM = 1.5f, ShelfDeep = 18f;

        /// <summary>The seabed scene's deepest water (m): its ground stays above 0, the heights' range.</summary>
        public const float SeabedDeepest = 38f;

        /// <summary>
        /// The shelf's water depth (m) at s m from the waterline (negative: the water): the beach face down
        /// to a flat shelf, its edge running at a slant across the 8 m terrain grid (-170 m out at z = 360,
        /// -247 m at z = 1000), a cliff 4 m wide down to dredged deep water, easing to the sea's 14 m by
        /// 500 m out. The grid turns the slanted cliff into the game's saw-toothed triangles.
        /// </summary>
        private static float ShelfDepth(float z, float s)
        {
            float edge = -170f - 0.12f * (z - ShelfZ0);
            float shelf = Mathf.Min(0.12f * -s, ShelfDepthM);
            float deep = Mathf.Lerp(ShelfDeep, 14f, Smooth(-420f, -500f, s));
            return Mathf.Lerp(shelf, deep, Mathf.Clamp01((edge - s) / 4f));
        }

        public static float RiverZ(float x)
        {
            return 210f + 40f * Mathf.Sin(x / 260f);
        }

        public static float RiverSurface(float s)
        {
            s = Mathf.Max(s, 0f);
            return SeaLevel + 0.003f * s + 3f * Smooth(380f, 440f, s);
        }

        /// <summary>
        /// The lumpy stretches (2026-11-11, the foam's pointy edges): north of z = 1120 (the gentle beach)
        /// and south of z = -1720 (the steep coast) the ground near the waterline is lumpy as a player's
        /// brush leaves it -- a height at each of the game's 16 m points (+-0.35 m from point to point
        /// over +-0.6 m swells 48 m across; a quarter of that on the gentle beach, half on the steep
        /// coast), straight between them, as the game's terrain is where a patch has no detail heights:
        /// creases along the grid, the waterline a polygon.
        /// </summary>
        public const float LumpsNorth = 1120f, LumpsSouth = -1720f;

        private static float LumpHash(int i, int j, int salt)
        {
            uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        private static float LumpPoint(int i, int j)
        {
            // The 48 m swells, read at the point (straight between their own points too).
            float u = i / 3f, v = j / 3f;
            int iu = Mathf.FloorToInt(u), iv = Mathf.FloorToInt(v);
            float fu = u - iu, fv = v - iv;
            float swell = Mathf.Lerp(Mathf.Lerp(LumpHash(iu, iv, 2), LumpHash(iu + 1, iv, 2), fu),
                                     Mathf.Lerp(LumpHash(iu, iv + 1, 2), LumpHash(iu + 1, iv + 1, 2), fu), fv);
            return 0.35f * LumpHash(i, j, 1) + 0.6f * swell;
        }

        /// <summary>The lumps' height at (x, z), s m from the waterline (m; 0 outside the lumpy stretches).</summary>
        public static float Lumps(float x, float z, float s)
        {
            float where = Mathf.Max(Smooth(LumpsNorth - 80f, LumpsNorth, z), Smooth(LumpsSouth + 70f, LumpsSouth, z));
            where *= Smooth(-320f, -240f, s) * (1f - Smooth(60f, 110f, s));
            if (where <= 0f)
                return 0f;
            // As much as moves the waterline a few metres, no more (a first try at full height made
            // sandbars and islands): a quarter on the gentle beach, half on the steep coast.
            where *= z > 0f ? 0.25f : 0.5f;
            float u = (x + Half) / 16f, v = (z + Half) / 16f;
            int i = Mathf.FloorToInt(u), j = Mathf.FloorToInt(v);
            float fu = u - i, fv = v - j;
            float lump = Mathf.Lerp(Mathf.Lerp(LumpPoint(i, j), LumpPoint(i + 1, j), fu),
                                    Mathf.Lerp(LumpPoint(i, j + 1), LumpPoint(i + 1, j + 1), fu), fv);
            return where * lump;
        }

        public static float Ground(float x, float z)
        {
            return BaseGround(x, z) + Lumps(x, z, x - ShoreX(z));
        }

        private static float BaseGround(float x, float z)
        {
            float s = x - ShoreX(z);
            float land;
            if (s < -500f)
                land = SeaLevel - 14f;
            else if (s < 0f && Bay(z) > 0f)
                land = SeaLevel - BayDepth(x, z, s, Bay(z));
            else if (s < 0f && z > ShelfZ0 && z < ShelfZ1)
                land = SeaLevel - ShelfDepth(z, s);
            else if (s < 0f)
            {
                // South of z = -450 the seabed drops as the game's coasts do (the author's city,
                // 2026-10-24: under 6% of its shore's water shallower than 2 m): 12 cm a metre.
                float gentle = SeaLevel - 14f + 14f * Mathf.Pow((s + 500f) / 500f, 1.6f);
                land = Mathf.Lerp(gentle, SeaLevel - Mathf.Min(14f, -0.12f * s), Smooth(-250f, -450f, z));
            }
            else
                land = SeaLevel + 4.5f * (1f - Mathf.Exp(-s / 60f)) + 4f * Smooth(250f, 600f, s);

            if (s_seabed && s < 0f)
            {
                // The seabed scene (s_seabed): 10 cm a metre down to 38 m, between z -250 and 300.
                float w = Smooth(-350f, -250f, z) * (1f - Smooth(300f, 340f, z));
                land = Mathf.Lerp(land, SeaLevel - Mathf.Min(SeabedDeepest, 0.1f * -s), w);
            }

            float dz = Mathf.Abs(z - RiverZ(x));
            if (s > -120f && dz < RiverHalf + 12f)
            {
                float across = Mathf.Clamp01(dz / RiverHalf);
                float bed = RiverSurface(s) - 0.3f - 4f * (1f - across * across);
                land = Mathf.Min(land, Mathf.Lerp(bed, land, Smooth(RiverHalf - 2f, RiverHalf + 12f, dz)));
            }

            return land;
        }

        /// <summary>The tsunami's height over the sea at s (m from the waterline) in a frame (0 the older).</summary>
        public float Eta(float s, int frame)
        {
            if (Tsunami <= 0f)
                return 0f;
            float front = Front + frame * Step;
            return Tsunami * (1f - Smooth(front - 40f, front + 10f, s)) * Smooth(front - 520f, front - 260f, s);
        }

        /// <summary>The water's surface at (x, z) in a frame (the ground itself where it is dry), and its flow (the game's units).</summary>
        public float Surface(float x, float z, int frame, out Vector2 flow)
        {
            float s = x - ShoreX(z);
            float ground = Ground(x, z);
            flow = Vector2.zero;
            float surface = ground;
            float eta = Eta(s, frame);
            float sea = SeaLevel + eta;
            if (ground < sea)
            {
                surface = sea;
                flow = new Vector2(Mathf.Min(0.45f * eta / 3f, 0.9f), 0f);
            }

            float dz = Mathf.Abs(z - RiverZ(x));
            if (s > -150f && dz < RiverHalf + 12f)
            {
                float across = Mathf.Clamp01(dz / RiverHalf);
                float river = RiverSurface(s);
                if (ground < river && river >= surface)
                {
                    surface = river;
                    float speed = (0.22f + 0.62f * Bump(370f, 450f, s)) * (1f - 0.7f * across * across);
                    float slope = (40f / 260f) * Mathf.Cos(x / 260f);
                    var along = new Vector2(-1f, -slope).normalized;
                    flow = along * speed;
                }
                else if (s < 0f && ground < sea)
                    flow += new Vector2(-0.25f * (1f + s / 150f) * (1f - across * across), 0f);
            }

            return surface;
        }

        /// <summary>The data as the game holds it, on every material drawn on the water's meshes.</summary>
        public void Bind(Material[] materials, List<string> log)
        {
            // The surface heights: a wet point holds its surface, a dry one the ground -- or, with "ilsheet"
            // (WaterSimulation.FillHeightMap, IL 2026-11-11), next to wet points the lower of the ground and
            // those neighbours' mean surface (s_ilSheet has what is and is not settled).
            int n = WaterTexels;
            var levels = new float[2][];
            var wet = new bool[2][];
            for (int frame = 0; frame < 2; frame++)
            {
                levels[frame] = new float[n * n];
                wet[frame] = new bool[n * n];
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float x = -Half + (i + 0.5f) * (2f * Half / n), z = -Half + (j + 0.5f) * (2f * Half / n);
                        Vector2 flow;
                        float level = Surface(x, z, frame, out flow);
                        levels[frame][j * n + i] = level;
                        wet[frame][j * n + i] = level > Ground(x, z) + 1e-4f;
                    }
                }
            }

            var heights = new Color32[n * n];
            var value = new float[2];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int frame = 0; frame < 2; frame++)
                    {
                        int k = j * n + i;
                        float h = levels[frame][k];
                        if (!wet[frame][k])
                        {
                            float sum = 0f;
                            int count = 0;
                            if (i > 0 && wet[frame][k - 1]) { sum += levels[frame][k - 1]; count++; }
                            if (i < n - 1 && wet[frame][k + 1]) { sum += levels[frame][k + 1]; count++; }
                            if (j > 0 && wet[frame][k - n]) { sum += levels[frame][k - n]; count++; }
                            if (j < n - 1 && wet[frame][k + n]) { sum += levels[frame][k + n]; count++; }
                            if (count > 0 && s_ilSheet)
                                h = Mathf.Min(h, sum / count);
                        }
                        value[frame] = h;
                    }

                    ushort a = Encode(value[0]), b = Encode(value[1]);
                    heights[j * n + i] = new Color32((byte)(a >> 8), (byte)(a & 255), (byte)(b >> 8), (byte)(b & 255));
                }
            }

            var ground = new Color32[TerrainTexels * TerrainTexels];
            for (int j = 0; j < TerrainTexels; j++)
            {
                for (int i = 0; i < TerrainTexels; i++)
                {
                    float x = -Half + (i + 0.5f) * (2f * Half / TerrainTexels), z = -Half + (j + 0.5f) * (2f * Half / TerrainTexels);
                    ushort g = Encode(Ground(x, z));
                    ground[j * TerrainTexels + i] = new Color32((byte)(g >> 8), (byte)(g & 255), (byte)(g >> 8), (byte)(g & 255));
                }
            }

            Texture2D heightMap = DataTexture(WaterTexels, heights, FilterMode.Point);
            Texture2D terrainMap = DataTexture(TerrainTexels, ground, FilterMode.Point);
            Texture2D[] surface = new Texture2D[4];
            for (int frame = 0; frame < 2; frame++)
            {
                Color32[] a, b;
                SurfaceMaps(frame, out a, out b);
                surface[frame] = DataTexture(SurfaceTexels, a, FilterMode.Bilinear);
                surface[2 + frame] = DataTexture(SurfaceTexels, b, FilterMode.Bilinear);
            }

            foreach (Material m in materials)
            {
                m.SetTexture("_MainTex", heightMap);
                m.SetTexture("_TerrainHeight", terrainMap);
                m.SetTexture("_SurfaceAOld", surface[0]);
                m.SetTexture("_SurfaceANew", surface[1]);
                m.SetTexture("_SurfaceBOld", surface[2]);
                m.SetTexture("_SurfaceBNew", surface[3]);
                m.SetVector("_HeightMapping", new Vector4(1f / WaterTexels, HeightScale, 1f / WaterTexels, 0f));
                m.SetVector("_TerrainMapping", new Vector4(1f / TerrainTexels, HeightScale, 1f / TerrainTexels, 0f));
                m.SetVector("_SurfaceMapping", new Vector4(0.5f, 0.5f, 1f / (2f * Half), 0f));
                m.SetVector("_TerrainStatus", new Vector4(0f, 0.5f, 0f, 0f));
            }

            log.Add(Tsunami > 0f ? "data: with a " + Tsunami + " m tsunami" : "data: without a tsunami");
        }

        /// <summary>
        /// The surface maps of a frame as WaterSimulation.FillSurfaceMap makes them: per 32 m texel a 1-2-1
        /// weighted 3 x 3 of points 16 m apart; A = (the surface's normal x, pollution 0, normal z),
        /// B = (flow x, depth / 16 m, flow z), each 128 + 127 v (the depth 255 v).
        /// </summary>
        private void SurfaceMaps(int frame, out Color32[] a, out Color32[] b)
        {
            a = new Color32[SurfaceTexels * SurfaceTexels];
            b = new Color32[SurfaceTexels * SurfaceTexels];
            float texel = 2f * Half / SurfaceTexels;
            for (int j = 0; j < SurfaceTexels; j++)
            {
                for (int i = 0; i < SurfaceTexels; i++)
                {
                    float cx = -Half + (i + 0.5f) * texel, cz = -Half + (j + 0.5f) * texel;
                    float sumW = 0f, depth = 0f, wetW = 0f;
                    Vector2 flow = Vector2.zero;
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        for (int di = -1; di <= 1; di++)
                        {
                            float wgt = (di == 0 ? 2f : 1f) * (dj == 0 ? 2f : 1f);
                            float x = cx + di * 16f, z = cz + dj * 16f;
                            Vector2 f;
                            float surface = Surface(x, z, frame, out f);
                            float d = surface - Ground(x, z);
                            sumW += wgt;
                            if (d > 0.01f)
                            {
                                depth += wgt * d;
                                flow += wgt * f;
                                wetW += wgt;
                            }
                        }
                    }

                    Vector2 dummy;
                    float hx0 = Surface(cx - 16f, cz, frame, out dummy), hx1 = Surface(cx + 16f, cz, frame, out dummy);
                    float hz0 = Surface(cx, cz - 16f, frame, out dummy), hz1 = Surface(cx, cz + 16f, frame, out dummy);
                    var n = new Vector3(-(hx1 - hx0) / 32f, 1f, -(hz1 - hz0) / 32f).normalized;
                    Vector2 meanFlow = wetW > 0f ? flow / wetW : Vector2.zero;
                    float meanDepth = depth / sumW;
                    int k = j * SurfaceTexels + i;
                    a[k] = new Color32(Byte(128f + 127f * n.x), 0, Byte(128f + 127f * n.z), 255);
                    b[k] = meanDepth > 0f
                        ? new Color32(Byte(128f + 127f * Mathf.Clamp(meanFlow.x, -1f, 1f)), Byte(255f * Mathf.Clamp01(meanDepth / 16f)), Byte(128f + 127f * Mathf.Clamp(meanFlow.y, -1f, 1f)), 255)
                        : new Color32(128, 0, 128, 255);
                }
            }
        }
    }

    private static ushort Encode(float height)
    {
        return (ushort)Mathf.Clamp(Mathf.RoundToInt(height / HeightScale * 65536f), 0, 65535);
    }

    private static byte Byte(float v)
    {
        return (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    private static float Bump(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return 4f * t * (1f - t);
    }

    private static Texture2D DataTexture(int size, Color32[] px, FilterMode filter)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = filter,
        };
        t.SetPixels32(px);
        t.Apply(false);
        return t;
    }

    /// <summary>
    /// The terrain (deferred, so it is in the depth texture as the game's is) and the water's meshes:
    /// 16 tiles of 128 x 128 quads, 8 m apart, over the 4 km square. The water's are flat: the vertex
    /// program puts each vertex at the simulated surface, as the game's does.
    /// </summary>
    private static void BuildScene(Material water, Material foam, Material sky, List<Renderer> waterRenderers, List<Renderer> foamRenderers,
                                   List<Renderer> skyRenderers, out Material groundMaterial, out Texture2D[] beds)
    {
        // The ground's two pictures (Shot.Bed): the preview's own, and the beach's pale sand all the way
        // down under the water (a theme's sand drawn on the bed as on the beach).
        beds = new Texture2D[2];
        for (int k = 0; k < beds.Length; k++)
        {
            var colours = new Color[TerrainTexels * TerrainTexels];
            for (int j = 0; j < TerrainTexels; j++)
            {
                for (int i = 0; i < TerrainTexels; i++)
                {
                    float x = -Half + (i + 0.5f) * (2f * Half / TerrainTexels), z = -Half + (j + 0.5f) * (2f * Half / TerrainTexels);
                    float y = Coast.Ground(x, z) - SeaLevel;
                    Color sand = new Color(0.62f, 0.56f, 0.44f), wetSand = new Color(0.36f, 0.33f, 0.27f), grass = new Color(0.27f, 0.33f, 0.18f);
                    Color bed = new Color(0.22f, 0.24f, 0.22f);
                    Color c = y < -0.3f ? Color.Lerp(wetSand, bed, Mathf.Clamp01(-y / 6f)) : y < 2.5f ? Color.Lerp(wetSand, sand, Mathf.Clamp01((y + 0.3f) / 1.2f)) : Color.Lerp(sand, grass, Mathf.Clamp01((y - 2.5f) / 2f));
                    if (k == 1 && y < 2.5f)
                        c = sand;
                    colours[j * TerrainTexels + i] = c;
                }
            }

            beds[k] = new Texture2D(TerrainTexels, TerrainTexels, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            beds[k].SetPixels(colours);
            beds[k].Apply(true);
        }

        // The seabed scene: the pale bed, its dark edge and its detail drawn by Editor/PreviewSeabed.shader.
        groundMaterial = s_seabed ? new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewSeabed.shader")) { mainTexture = beds[1] }
            : new Material(Shader.Find("Legacy Shaders/Diffuse")) { mainTexture = beds[0] };

        const int quads = 128;
        const float spacing = 8f;
        for (int tj = 0; tj < 4; tj++)
        {
            for (int ti = 0; ti < 4; ti++)
            {
                float x0 = -Half + ti * quads * spacing, z0 = -Half + tj * quads * spacing;
                Mesh groundMesh = Grid(x0, z0, quads, spacing, true);
                var g = new GameObject("ground " + ti + "," + tj);
                g.AddComponent<MeshFilter>().sharedMesh = groundMesh;
                g.AddComponent<MeshRenderer>().sharedMaterial = groundMaterial;

                Mesh waterMesh = Grid(x0, z0, quads, spacing, false);
                var wgo = new GameObject("water " + ti + "," + tj);
                wgo.AddComponent<MeshFilter>().sharedMesh = waterMesh;
                MeshRenderer wr = wgo.AddComponent<MeshRenderer>();
                wr.sharedMaterial = water;
                waterRenderers.Add(wr);

                var fgo = new GameObject("foam " + ti + "," + tj);
                fgo.AddComponent<MeshFilter>().sharedMesh = waterMesh;
                MeshRenderer fr = fgo.AddComponent<MeshRenderer>();
                fr.sharedMaterial = foam;
                fr.shadowCastingMode = ShadowCastingMode.Off;
                foamRenderers.Add(fr);

                var sgo = new GameObject("sky " + ti + "," + tj);
                sgo.AddComponent<MeshFilter>().sharedMesh = waterMesh;
                MeshRenderer sr = sgo.AddComponent<MeshRenderer>();
                sr.sharedMaterial = sky;
                sr.shadowCastingMode = ShadowCastingMode.Off;
                sr.receiveShadows = false;
                skyRenderers.Add(sr);
            }
        }
    }

    /// <summary>"#RRGGBB" as a colour (sRGB); null for "-" or anything unreadable.</summary>
    private static Color? Hex(string text)
    {
        Color c;
        return text != "-" && ColorUtility.TryParseHtmlString(text, out c) ? c : (Color?)null;
    }

    private static Mesh Grid(float x0, float z0, int quads, float spacing, bool ground)
    {
        int n = quads + 1;
        var vertices = new Vector3[n * n];
        var normals = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                float x = x0 + i * spacing, z = z0 + j * spacing;
                float y = ground ? Coast.Ground(x, z) : 0f;
                vertices[j * n + i] = new Vector3(x, y, z);
                if (ground)
                {
                    float dx = Coast.Ground(x + 2f, z) - Coast.Ground(x - 2f, z), dz = Coast.Ground(x, z + 2f) - Coast.Ground(x, z - 2f);
                    normals[j * n + i] = new Vector3(-dx / 4f, 1f, -dz / 4f).normalized;
                }
                else
                    normals[j * n + i] = Vector3.up;
                uvs[j * n + i] = new Vector2(x / (2f * Half) + 0.5f, z / (2f * Half) + 0.5f);
            }
        }

        var triangles = new int[quads * quads * 6];
        int t = 0;
        for (int j = 0; j < quads; j++)
        {
            for (int i = 0; i < quads; i++)
            {
                int a = j * n + i;
                triangles[t++] = a;
                triangles[t++] = a + n;
                triangles[t++] = a + 1;
                triangles[t++] = a + 1;
                triangles[t++] = a + n;
                triangles[t++] = a + n + 1;
            }
        }

        var mesh = new Mesh { vertices = vertices, normals = normals, uv = uvs, triangles = triangles };
        float size = quads * spacing;
        // The water's vertices move to the surface in the vertex program: bounds that hold any height.
        mesh.bounds = new Bounds(new Vector3(x0 + size / 2f, 100f, z0 + size / 2f), new Vector3(size, 400f, size));
        return mesh;
    }

    private sealed class Tower
    {
        public Renderer Renderer;
        public Material Day, Night;
    }

    /// <summary>A town of window-bay towers on the land behind the beach (WaterPreview's facades).</summary>
    private static List<Tower> BuildTowers()
    {
        var towers = new List<Tower>();
        var random = new System.Random(11);
        Texture2D day = WaterPreview.Facade(false, random), night = WaterPreview.Facade(true, random);
        Texture2D dayIllum = WaterPreview.Illum(day), nightIllum = WaterPreview.Illum(night);
        Color[] facades = { new Color(0.62f, 0.58f, 0.52f), new Color(0.45f, 0.50f, 0.56f), new Color(0.70f, 0.66f, 0.60f), new Color(0.38f, 0.36f, 0.34f), new Color(0.55f, 0.60f, 0.62f) };
        float[,] spec =
        {
            // x, z, width, height
            { 330f, -420f, 40f, 70f }, { 360f, -260f, 34f, 110f }, { 340f, -90f, 44f, 60f }, { 350f, 40f, 36f, 140f },
            { 340f, 380f, 46f, 90f }, { 360f, 520f, 38f, 120f }, { 520f, -360f, 50f, 190f }, { 540f, -140f, 44f, 230f },
            { 560f, 420f, 52f, 200f }, { 760f, -260f, 56f, 280f }, { 740f, 40f, 48f, 250f }, { 780f, 520f, 54f, 220f },
            { 1000f, -120f, 64f, 320f }, { 1050f, 420f, 60f, 270f },
        };
        for (int i = 0; i < spec.GetLength(0); i++)
        {
            float x = spec[i, 0], z = spec[i, 1], width = spec[i, 2], height = spec[i, 3];
            float baseY = Coast.Ground(x, z) - 2f;
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.localScale = new Vector3(width, height, width);
            box.transform.position = new Vector3(x, baseY + height / 2f, z);
            var scale = new Vector2(width / 32f, height / 32f);
            var offset = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            var dayMaterial = new Material(Shader.Find("Legacy Shaders/Self-Illumin/Diffuse")) { color = facades[i % facades.Length], mainTexture = day, mainTextureScale = scale, mainTextureOffset = offset };
            dayMaterial.SetTexture("_Illum", dayIllum);
            dayMaterial.SetTextureScale("_Illum", scale);
            dayMaterial.SetTextureOffset("_Illum", offset);
            var nightMaterial = new Material(Shader.Find("Legacy Shaders/Self-Illumin/Diffuse")) { color = Color.white, mainTexture = night, mainTextureScale = scale, mainTextureOffset = offset };
            nightMaterial.SetTexture("_Illum", nightIllum);
            nightMaterial.SetTextureScale("_Illum", scale);
            nightMaterial.SetTextureOffset("_Illum", offset);
            Renderer renderer = box.GetComponent<Renderer>();
            renderer.sharedMaterial = dayMaterial;
            towers.Add(new Tower { Renderer = renderer, Day = dayMaterial, Night = nightMaterial });
        }

        return towers;
    }

    /// <summary>
    /// The globals the game's skybox and fog read, as the game sets them for this sun
    /// (DayNightProperties.UpdateSharedAtmosphericProperties, FogProperties.Update): the sky's inputs
    /// Volumetric Weather logged from the game (2026-10-30; Rayleigh 1, 680/550/440 nm, exposure 0.71,
    /// linear without the sky's tone mapping), the fog's a theme's as Real Light found them (density
    /// 0.00223, start 596 m, distance 10136, height 5000 m, colour decay 0.20, scattering 1) with the
    /// horizon band of 3000 m Volumetric Weather found (without that mod, which flattens it); the fog's
    /// 3D noise a flat 0.5, no pollution.
    /// </summary>
    private static void GameSkyGlobals(Vector3 toSun, bool night)
    {
        if (night)
            toSun = new Vector3(toSun.x, -0.33f, toSun.z).normalized;
        float u = Mathf.Atan(5.35f * Mathf.Max(toSun.y, -0.1975f)) / 1.1f + 0.739f;
        float day = Mathf.Clamp01(u), sunsetTime = Mathf.Clamp01((u - 1f) * 1.5f), nightTime = 1f - day;
        Shader.SetGlobalVector("_SunDir", toSun);
        Shader.SetGlobalVector("_SkyMultiplier", new Vector4(sunsetTime, 0.71f * 4f * day, nightTime, 0f));
        Shader.SetGlobalVector("_BetaR", new Vector4(0.00581f, 0.01357f, 0.03312f, 0f));
        Shader.SetGlobalVector("_BetaM", new Vector4(0.00166f, 0.00166f, 0.00166f, 0f));
        Shader.SetGlobalVector("_MieConst", new Vector4(0.0017f, 0.0017f * 0.00581f / 0.01357f, 0.0017f * 0.00581f / 0.03312f, 0f));
        Shader.SetGlobalVector("_MiePhase_g", new Vector4(0.2377f, 1.4238f, 1.3020f, 0f));
        Shader.SetGlobalVector("_NightZenithColor", new Vector4(0.00196f, 0.00278f, 0.00388f, 0f));
        Shader.SetGlobalVector("_NightHorizonColor", new Vector4(0.055f, 0.060f, 0.065f, 0f) * nightTime);
        Shader.SetGlobalVector("_ColorCorrection", new Vector4(1f, 2f, 0f, 0f));
        Shader.SetGlobalFloat("_SunSize", 32f);
        Shader.SetGlobalVector("_FogParams0", new Vector4(0f, 0.00223f, 1f / 596f, 10136f));
        Shader.SetGlobalVector("_FogParams1", new Vector4(1f / 5000f, 0.20f, 1f, 1278f));
        Shader.SetGlobalVector("_FogPollutionParams", new Vector4(0f, 0f, 1f / 3000f, 0f));
        Shader.SetGlobalVector("_FogPollutionColor", new Vector4(0.5f, 0.5f, 0.5f, 1f));
        Shader.SetGlobalVector("_FogSpeed", Vector4.zero);
        Shader.SetGlobalVector("_FogNoiseGainConstrastStepScale", new Vector4(1f, 1f, 200f, 0.001f));
        Shader.SetGlobalVector("_NaturalResourceMapping", Vector4.zero);
        Shader.SetGlobalTexture("_NaturalDestruction", Texture2D.blackTexture);
        if (s_fogNoise == null)
        {
            s_fogNoise = new Texture3D(4, 4, 4, TextureFormat.RGBA32, false);
            var grey = new Color[64];
            for (int i = 0; i < grey.Length; i++)
                grey[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
            s_fogNoise.SetPixels(grey);
            s_fogNoise.Apply();
        }
        Shader.SetGlobalTexture("_Fog3DNoiseTex", s_fogNoise);
    }

    private static Texture3D s_fogNoise;

    /// <summary>The light, sky and camera of a view; true at night.</summary>
    private static bool SetView(string view, GameObject cameraObject, Light sun, Material skybox, Camera capture, RenderTexture skyCube, Material water, List<Tower> towers)
    {
        // "view@e": the sunset's sun e degrees up (below 0: down) instead of 6; "...back": behind the
        // camera instead of ahead (the sky away from the sun in the water). The camera is the view's.
        float? sunUp = null;
        bool sunBehind = false;
        int at0 = view.IndexOf('@');
        if (at0 >= 0)
        {
            sunUp = WaterPreview.F(view.Substring(at0 + 1));
            view = view.Substring(0, at0);
        }
        if (view.EndsWith("back", StringComparison.Ordinal))
        {
            sunBehind = true;
            view = view.Substring(0, view.Length - 4);
        }

        bool night = view.StartsWith("night", StringComparison.Ordinal);
        // "glitter" views: the sunset's sun, the camera far out at sea looking into it (web-ocean's);
        // "glitterday": a sun 30 deg up the same way, under a city camera's usual pitch.
        bool glitter = view.StartsWith("glitter", StringComparison.Ordinal);
        bool glitterDay = view == "glitterday";
        bool sunset = view.StartsWith("sunset", StringComparison.Ordinal) || (glitter && !glitterDay);
        foreach (Tower t in towers)
            t.Renderer.sharedMaterial = night ? t.Night : t.Day;

        // Day: the sun high behind the camera's left; sunset: low over the land ahead; night: a dim moon.
        Vector3 toSun = sunset ? new Vector3(0.979f, 0.105f, -0.173f).normalized : night ? new Vector3(-0.3f, 0.6f, 0.5f).normalized
            : glitterDay ? new Vector3(0.853f, 0.5f, -0.150f).normalized : new Vector3(-0.55f, 0.45f, 0.7f).normalized;
        float sunScale = 1f;
        if (sunUp.HasValue)
        {
            var flat = new Vector3(toSun.x, 0f, toSun.z).normalized * (sunBehind ? -1f : 1f);
            float e = sunUp.Value * Mathf.Deg2Rad;
            toSun = flat * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e);
            // The game's sun: x DayTime (0.74 at the horizon, 0 near -11 deg).
            sunScale = Mathf.Clamp01(Mathf.Atan(5.35f * Mathf.Max(toSun.y, -0.1975f)) / 1.1f + 0.739f);
        }
        sun.transform.rotation = Quaternion.LookRotation(-toSun);
        sun.intensity = (night ? 0.12f : sunset ? 1.6f : 1.3f) * sunScale;
        if (s_gameSky)
            GameSkyGlobals(toSun, night);
        sun.color = night ? new Color(0.6f, 0.7f, 1f) : sunset ? new Color(1f, 0.62f, 0.38f) : new Color(1f, 0.95f, 0.88f);
        skybox.SetFloat("_Exposure", night ? 0.04f : 1.1f);
        // The -Fog stand-ins' colours (Editor/PreviewFogSky.shader): by day a grey haze and warm clouds; at
        // night about the night sky's horizon (the game's fog is its sky's scattering along the view).
        Shader.SetGlobalColor("_PreviewFogColour", night ? new Color(0.15f, 0.17f, 0.20f, 1f) : new Color(0.55f, 0.58f, 0.62f, 1f));
        Shader.SetGlobalColor("_PreviewCloudColour", night ? new Color(0.12f, 0.13f, 0.16f, 1f) : new Color(0.42f, 0.36f, 0.33f, 1f));
        skybox.SetFloat("_AtmosphereThickness", 1f);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        Color ambient = night ? new Color(0.010f, 0.012f, 0.020f) : sunset ? new Color(0.30f, 0.30f, 0.38f) : new Color(0.45f, 0.55f, 0.70f);
        RenderSettings.ambientSkyColor = ambient;
        RenderSettings.ambientEquatorColor = night ? new Color(0.008f, 0.009f, 0.012f) : new Color(0.40f, 0.42f, 0.45f);
        RenderSettings.ambientGroundColor = night ? new Color(0.004f, 0.004f, 0.005f) : new Color(0.20f, 0.20f, 0.20f);
        capture.RenderToCubemap(skyCube);
        water.SetVector("_PreviewSun", new Vector4(toSun.x, toSun.y, toSun.z, sun.intensity));
        water.SetColor("_PreviewSunColour", sun.color.linear);
        water.SetColor("_PreviewAmbient", ambient.linear);

        // The views: the coast from a city camera's usual height, close over the beach, high above,
        // over the river's rapid, and at sea before the tsunami's front (sunset and night: the coast).
        Vector3 eye, at;
        if (glitter)
        {
            // 30 m (or 300 m) over the sea at its far edge, facing the sun's way (10 deg south of
            // east), the horizon a little above the middle (or the sea filling most of the frame).
            bool high = view.EndsWith("300", StringComparison.Ordinal);
            float pitch = (glitterDay ? 35f : high ? 11f : 2.5f) * Mathf.Deg2Rad, yaw = -10f * Mathf.Deg2Rad;
            eye = new Vector3(-1950f, SeaLevel + (glitterDay ? 400f : high ? 300f : 30f), 150f);
            at = eye + new Vector3(Mathf.Cos(pitch) * Mathf.Cos(yaw), -Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Sin(yaw)) * 1000f;
        }
        else if (view == "under")
        {
            // 3 m under the sea over 7 m of water, 170 m out, looking toward the beach 12 deg down:
            // the bed rising ahead, the surface's underside above.
            eye = new Vector3(-160f, SeaLevel - 3f, 0f);
            at = new Vector3(-100f, SeaLevel - 15.8f, 8f);
        }
        else if (view == "undersun")
        {
            // The same place looking toward the sun's side, 10 deg up: the water's glow toward it.
            eye = new Vector3(-160f, SeaLevel - 3f, 0f);
            at = new Vector3(-160f - 62f, SeaLevel - 3f + 17.6f, 79f);
        }
        else if (view == "underup")
        {
            // The same place looking up 65 deg toward the town: Snell's window.
            eye = new Vector3(-160f, SeaLevel - 3f, 0f);
            at = new Vector3(-140f, SeaLevel + 40f, 3f);
        }
        else if (view == "underlook" || view == "underzenith" || view == "underaway")
        {
            // The author's reference (2026-10-30, a game's diver looking up at the surface from 28 m): 8 m
            // under the open sea (14 m deep), looking up 40 deg toward the sun's side, 80 deg up (nearly
            // straight up), or 25 deg up away from it.
            Vector3 sunSide = new Vector3(-0.55f, 0f, 0.7f).normalized;
            float pitch = (view == "underlook" ? 40f : view == "underzenith" ? 80f : 25f) * Mathf.Deg2Rad;
            Vector3 flat = view == "underaway" ? -sunSide : sunSide;
            eye = new Vector3(-700f, SeaLevel - 8f, 0f);
            at = eye + (flat * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch)) * 100f;
        }
        else if (view == "undertown")
        {
            // The author's in-game shot (2026-10-30): 11 m under the sea 600 m out, looking toward the town
            // ashore 8 deg up -- the skyline seen through the surface from below.
            eye = new Vector3(-600f, SeaLevel - 11f, 0f);
            at = eye + new Vector3(Mathf.Cos(8f * Mathf.Deg2Rad), Mathf.Sin(8f * Mathf.Deg2Rad), 0f) * 100f;
        }
        else if (view == "underdown")
        {
            // 1.5 m under, over 5 m of water near the beach, looking 40 deg down at the bed (the caustics).
            eye = new Vector3(-110f, SeaLevel - 1.5f, 0f);
            at = new Vector3(-100f, SeaLevel - 9.9f, 2f);
        }
        else if (view == "underhalf")
        {
            // The camera at the waterline (15 cm under), looking at the beach 6 deg down: half in the water.
            eye = new Vector3(-160f, SeaLevel - 0.15f, 0f);
            at = new Vector3(100f, SeaLevel - 27.5f, 40f);
        }
        else if (view == "underbridge")
        {
            // The player's picture: at the waterline (5 cm under: the near plane's rays split there), 22 m
            // before the bridge, a pier ahead, looking along the water 2 deg down.
            eye = new Vector3(-222f, SeaLevel - 0.05f, 22f);
            at = eye + new Vector3(1f, -0.035f, -0.02f) * 100f;
        }
        else if (view == "overbridge")
        {
            // The same, 5 cm over the water: the near plane dips in, the waterline across the frame.
            eye = new Vector3(-222f, SeaLevel + 0.05f, 22f);
            at = eye + new Vector3(1f, -0.035f, -0.02f) * 100f;
        }
        else if (view == "undertop")
        {
            // The author's in-game shot (2026-10-30, later): just under the surface (40 cm) 600 m out,
            // looking toward the town ashore 12 deg up -- the game's near plane, a metre out, crosses the
            // surface for the upper part of the frame.
            eye = new Vector3(-600f, SeaLevel - 0.4f, 0f);
            at = eye + new Vector3(Mathf.Cos(12f * Mathf.Deg2Rad), Mathf.Sin(12f * Mathf.Deg2Rad), 0f) * 100f;
        }
        else if (view == "undershafts")
        {
            // 4 m under, just past the deck's shadow (the sun throws it toward +x, -z), looking toward the
            // sun's side 19 deg up, back through the shadow and the hangers' stripes.
            eye = new Vector3(-176f, SeaLevel - 4f, -10f);
            at = eye + new Vector3(-0.62f, 0.35f, 0.79f) * 100f;
        }
        else if (view == "underbridgedeep")
        {
            // 3 m under, 15 m before the bridge, looking 9 deg up toward the deck's shadow and past it.
            eye = new Vector3(-215f, SeaLevel - 3f, 20f);
            at = eye + new Vector3(1f, 0.15f, -0.3f) * 100f;
        }
        else if (view == "tsunamiwall")
        {
            // 9 m over the sea, 100 m before the wall's face, looking out to sea: the face fills the
            // view's middle and hides all behind it.
            eye = new Vector3(-570f, SeaLevel + 9f, 20f);
            at = new Vector3(-800f, SeaLevel + 16f, 70f);
        }
        else if (view == "tsunamiwallcity")
        {
            // A city camera 70 m up, 400 m off: the face, its crest, the sea beyond over the crest.
            eye = new Vector3(-420f, SeaLevel + 70f, -160f);
            at = new Vector3(-760f, SeaLevel + 8f, 60f);
        }
        else if (view == "photo")
        {
            // The layered foam: his reference picture's view -- the surf seen from 14 m up, nearly
            // straight down over the shore band (the waterline at x = 12).
            eye = new Vector3(-6f, SeaLevel + 14f, 0f);
            at = new Vector3(-3.5f, SeaLevel, 3f);
        }
        else if (view == "beach")
        {
            // The surf from a city camera 90 m up, the beach across the frame.
            eye = new Vector3(-210f, SeaLevel + 90f, -130f);
            at = new Vector3(0f, SeaLevel, 30f);
        }
        else if (view == "steep")
        {
            // The steep coast (as the game's) from a city camera 90 m up; its waterline near x = 38.
            eye = new Vector3(-170f, SeaLevel + 90f, -760f);
            at = new Vector3(30f, SeaLevel, -580f);
        }
        else if (view == "steepclose")
        {
            // The steep coast from 30 m up, close.
            eye = new Vector3(-40f, SeaLevel + 30f, -650f);
            at = new Vector3(25f, SeaLevel, -590f);
        }
        else if (view == "steepdown")
        {
            // The steep coast from 120 m up, looking well down, out over the sea beyond the surf (his
            // picture of streaks of foam out past the big foam, 2026-10-24).
            eye = new Vector3(-60f, SeaLevel + 120f, -560f);
            at = new Vector3(-60f, SeaLevel, -640f);
        }
        else if (view == "steephigh")
        {
            // The steep coast from 300 m up, the usual city view of a shoreline.
            eye = new Vector3(-520f, SeaLevel + 300f, -980f);
            at = new Vector3(20f, SeaLevel, -560f);
        }
        else if (view == "bay")
        {
            // The bay with a flat floor (the author's screenshot, 2026-10-25) from 380 m up, steeply down.
            eye = new Vector3(-200f, SeaLevel + 380f, -1590f);
            at = new Vector3(110f, SeaLevel, -1350f);
        }
        else if (view == "baylow")
        {
            // The bay from a lower city camera, 150 m up.
            eye = new Vector3(-60f, SeaLevel + 150f, -1520f);
            at = new Vector3(110f, SeaLevel, -1360f);
        }
        else if (view == "shelf")
        {
            // A player's harbour (2026-11-07): from 35 m over the sea, across the terraformed shelf
            // (z 360..1000) at the towers ashore, the shelf's edge 400-600 m ahead.
            eye = new Vector3(-760f, SeaLevel + 35f, 560f);
            at = new Vector3(300f, SeaLevel + 20f, 700f);
        }
        else if (view == "shelfnear")
        {
            // Closer: 25 m over the sea, the shelf's edge ~220 m ahead (the player's shot has it mid-frame).
            eye = new Vector3(-420f, SeaLevel + 25f, 600f);
            at = new Vector3(300f, SeaLevel + 15f, 700f);
        }
        else if (view == "shelfedge")
        {
            // The edge itself from 60 m up, 150 m off: the cliff's saw teeth on the 8 m grid.
            eye = new Vector3(-360f, SeaLevel + 60f, 640f);
            at = new Vector3(-215f, SeaLevel - 2f, 700f);
        }
        else if (view == "shelfhigh")
        {
            // The same shelf from a city camera 220 m up, looking well down across its edge.
            eye = new Vector3(-520f, SeaLevel + 220f, 420f);
            at = new Vector3(-160f, SeaLevel, 700f);
        }
        else if (view == "lumps")
        {
            // The lumpy gentle beach (2026-11-11, the foam's pointy edges) from a city camera 90 m up.
            eye = new Vector3(Coast.ShoreX(1400f) - 190f, SeaLevel + 90f, 1400f);
            at = new Vector3(Coast.ShoreX(1560f), SeaLevel, 1560f);
        }
        else if (view == "lumpsdown")
        {
            // The same beach from 150 m up, looking well down: the published screenshot's view.
            eye = new Vector3(Coast.ShoreX(1500f) - 70f, SeaLevel + 150f, 1500f);
            at = new Vector3(Coast.ShoreX(1560f) - 15f, SeaLevel, 1560f);
        }
        else if (view == "lumpsclose")
        {
            // The same beach from 30 m up, close: the swash's edge.
            eye = new Vector3(Coast.ShoreX(1500f) - 45f, SeaLevel + 30f, 1500f);
            at = new Vector3(Coast.ShoreX(1550f), SeaLevel, 1550f);
        }
        else if (view == "lumpsout")
        {
            // Out past that beach's surf from 140 m up, looking well down: the streaks beyond the surf.
            eye = new Vector3(Coast.ShoreX(1480f) - 150f, SeaLevel + 140f, 1480f);
            at = new Vector3(Coast.ShoreX(1560f) - 140f, SeaLevel, 1560f);
        }
        else if (view == "lumpssteep")
        {
            // The lumpy steep coast from a city camera 90 m up.
            eye = new Vector3(Coast.ShoreX(-2000f) - 190f, SeaLevel + 90f, -2000f);
            at = new Vector3(Coast.ShoreX(-1850f), SeaLevel, -1850f);
        }
        else if (view.EndsWith("seabed", StringComparison.Ordinal))
        {
            // The seabed scene (-Seabed) from a city camera 250 m up, out at sea looking in and down across
            // the sand's edge (5 m deep at its right end, 30 m at its left), the shore at the top right.
            eye = new Vector3(-440f, SeaLevel + 250f, -260f);
            at = new Vector3(-170f, SeaLevel, -40f);
        }
        else if (view == "seabedclose")
        {
            // The seabed scene's shallows from 16 m up, close: the beach's last 0-6 m of water.
            eye = new Vector3(-95f, SeaLevel + 16f, -150f);
            at = new Vector3(-40f, SeaLevel, -95f);
        }
        else if (view == "seabedshallow")
        {
            // Over the shallows from 12 m up, looking well down at 2-6 m of water: the bed's detail.
            eye = new Vector3(-75f, SeaLevel + 12f, -140f);
            at = new Vector3(-62f, SeaLevel, -122f);
        }
        else if (view == "seabeddown")
        {
            // Straight over the sand's edge from 60 m up, looking well down (8-15 m of water).
            eye = new Vector3(-180f, SeaLevel + 60f, -120f);
            at = new Vector3(-150f, SeaLevel, -70f);
        }
        else if (view == "nightskyline" || view == "skyline")
        {
            // A player's Reddit shots (2026-11-04): low over the sea (night or day) near its far edge, looking
            // across it at the towers ashore 2.2-3 km away, the shore's reflection under them.
            eye = new Vector3(-1900f, SeaLevel + 18f, 60f);
            at = new Vector3(600f, SeaLevel + 60f, 60f);
        }
        else if (view == "tsunamiclose")
        {
            eye = new Vector3(-940f, 150f, -120f);
            at = new Vector3(-640f, SeaLevel, 40f);
        }
        else if (view.EndsWith("close", StringComparison.Ordinal))
        {
            eye = new Vector3(-110f, 75f, 40f);
            at = new Vector3(25f, SeaLevel, 95f);
        }
        else if (view.EndsWith("high", StringComparison.Ordinal))
        {
            eye = new Vector3(-1300f, 700f, -700f);
            at = new Vector3(100f, SeaLevel, 150f);
        }
        else if (view.EndsWith("river", StringComparison.Ordinal))
        {
            eye = new Vector3(190f, 110f, 60f);
            at = new Vector3(430f, SeaLevel + 3f, 215f);
        }
        else if (view.StartsWith("tsunami", StringComparison.Ordinal))
        {
            eye = new Vector3(-1250f, 180f, -450f);
            at = new Vector3(-560f, SeaLevel, 100f);
        }
        else
        {
            eye = new Vector3(-650f, 150f, -250f);
            at = new Vector3(0f, SeaLevel, 120f);
        }

        cameraObject.transform.position = eye;
        cameraObject.transform.rotation = Quaternion.LookRotation(at - eye);
        return night;
    }
}
