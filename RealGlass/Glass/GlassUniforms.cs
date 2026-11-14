using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The one place that turns the player's settings and the frame into the glass shader's
    /// uniforms (GlassCommon.cginc's _RG* globals). Engine types only, no game types: the GPU
    /// preview runs this same file (tools/preview/sync-sim.ps1).
    /// </summary>
    public static class GlassUniforms
    {
        // The trace per quality at 1080 lines: [stride px, steps]. The stride grows with the
        // screen's height so a step covers the same share of the picture at 4K as at 1080p.
        private static readonly float[] Strides = { 12f, 8f, 5f, 3f };
        private static readonly float[] Steps = { 16f, 28f, 48f, 80f };

        /// <summary>Real curtain-wall numbers behind the 100% of each pane slider.</summary>
        public const float PillowAt100 = 0.004f;   // m of bow at a pane's middle (insulating units: a few mm)
        public const float TiltAt100 = 0.004f;     // rad, ~0.23 degrees of install tolerance
        public const float RollerAt100 = 0.00006f; // m, half the peak-to-valley of a roller wave
        public const float RollerLength = 0.35f;   // m, the furnace's roller pitch

        /// <summary>
        /// The inside of a building seen through the glass by day, as a share of the sky's light: dark
        /// -- indoor light is a few percent of daylight. 10% is where the author had the rooms by day
        /// (round 5); the rooms went in round 7, this is their average. At night there is nothing of
        /// ours behind the glass: the game's own window shows, lit as its asset and the game light it.
        /// </summary>
        public const float InsideByDay = 0.10f;

        /// <summary>
        /// The clear sky's light on level ground by the sun's height, every half degree from 0 to 10,
        /// as a share of its light with the sun 10 degrees up: Bird &amp; Riordan's spectral clear-sky
        /// model (SPCTRAL2, 1986) for 60 km of visibility over ground that gives back 15% (at 0, the
        /// first half degree of twilight: 10x darker every 2.6 degrees). A real sky darkens all through
        /// the sun's last ten degrees; the game's stays at noon's until the sun is 3.16 degrees up.
        /// </summary>
        private static readonly float[] ClearSkyBelow10 =
        {
            0.0206f, 0.0319f, 0.0724f, 0.1191f, 0.1700f, 0.2236f, 0.2789f, 0.3351f, 0.3916f, 0.4479f, 0.5036f,
            0.5586f, 0.6126f, 0.6655f, 0.7172f, 0.7676f, 0.8167f, 0.8645f, 0.9109f, 0.9561f, 1.0000f,
        };

        private static int _glass, _tint, _mask, _refl, _trace, _fill, _pane, _pane2, _light,
                           _sunDir, _sunColor, _ambSky, _ambGround, _glass2, _colorMap, _features, _budget, _budget2,
                           _sunThrough, _dirt;
        private static bool _ids;

        private static void Ids()
        {
            if (_ids)
                return;
            _glass = Shader.PropertyToID("_RGGlass");
            _tint = Shader.PropertyToID("_RGTint");
            _mask = Shader.PropertyToID("_RGMask");
            _refl = Shader.PropertyToID("_RGRefl");
            _trace = Shader.PropertyToID("_RGTrace");
            _fill = Shader.PropertyToID("_RGFill");
            _pane = Shader.PropertyToID("_RGPane");
            _pane2 = Shader.PropertyToID("_RGPane2");
            _light = Shader.PropertyToID("_RGLight");
            _sunDir = Shader.PropertyToID("_RGSunDir");
            _sunColor = Shader.PropertyToID("_RGSunColor");
            _ambSky = Shader.PropertyToID("_RGAmbSky");
            _ambGround = Shader.PropertyToID("_RGAmbGround");
            _glass2 = Shader.PropertyToID("_RGGlass2");
            _colorMap = Shader.PropertyToID("_RGColorMap");
            _features = Shader.PropertyToID("_RGFeatures");
            _budget = Shader.PropertyToID("_RGBudget");
            _budget2 = Shader.PropertyToID("_RGBudget2");
            _sunThrough = Shader.PropertyToID("_RGSunThrough");
            _dirt = Shader.PropertyToID("_RGDirt");
            _ids = true;
        }

        /// <summary>0 (night) .. 1 (day), from how high the sun stands: civil dusk to a few degrees up.</summary>
        public static float Daylight(float sunY)
        {
            return Smooth(-0.10f, 0.10f, sunY);
        }

        /// <summary>The clear sky's light at this sun height (degrees), as a share of its light at 10: 1 above.</summary>
        public static float ClearSky(float elevation)
        {
            float x = Mathf.Clamp(elevation, 0f, 10f) * 2f;
            int i = Mathf.Min((int)x, ClearSkyBelow10.Length - 2);
            return Mathf.Lerp(ClearSkyBelow10[i], ClearSkyBelow10[i + 1], x - i);
        }

        /// <summary>
        /// The game's own sky by the sun's height, as a share of its sky by day: DayNightProperties'
        /// DayTime, clamp01(atan(5.35 max(sun y, -0.1975)) / 1.1 + 0.739), which scales the whole day
        /// sky (IL): 1 with the sun above 3.16 degrees, 0.74 at the horizon, 0 near -11.
        /// </summary>
        public static float GameSkyDay(float sunY)
        {
            return Mathf.Clamp01(Mathf.Atan(5.35f * Mathf.Max(sunY, -0.1975f)) / 1.1f + 0.739f);
        }

        /// <summary>
        /// What the sky in the glass is multiplied by (GlassUtil's SkyBackground pass in the live sky's
        /// cube, under the clouds): the clear sky's light at the sun's height (ClearSky) to the power
        /// strength -- 1 the real fall, 0.5 half as many stops of it -- over the game's own sky's
        /// (GameSkyDay), never above 1. The glass's sky is then the dimmer of the two: the real sky's
        /// light down to the horizon and held there below it, the game's own fade wherever that is
        /// darker, so it only darkens through twilight and the game's night sky is left as it is.
        /// </summary>
        public static float Dusk(float sunY, float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= 0f)
                return 1f;

            float elevation = Mathf.Asin(Mathf.Clamp(sunY, -1f, 1f)) * Mathf.Rad2Deg;
            float target = Mathf.Pow(ClearSky(elevation), strength);
            float game = GameSkyDay(sunY);
            return game <= target ? 1f : target / game;
        }

        /// <summary>
        /// -1 all panes sunk .. +1 all bulging: sealed insulating units bulge when warm and sink when
        /// cold (their gas against the air). Neutral at 15 C, as far as it goes at +-20 C from it.
        /// </summary>
        public static float BowBias(float temperature)
        {
            return Mathf.Clamp((temperature - 15f) / 20f, -0.7f, 0.7f);
        }

        /// <summary>The glass tint as the shader wants it: linear, its brightest channel 1.</summary>
        public static Vector3 Tint(float r, float g, float b)
        {
            float lr = ToLinear(r), lg = ToLinear(g), lb = ToLinear(b);
            float max = Mathf.Max(lr, Mathf.Max(lg, lb));
            if (max < 0.002f)
                return new Vector3(1f, 1f, 1f);
            return new Vector3(lr / max, lg / max, lb / max);
        }

        /// <summary>sRGB to linear (IEC 61966-2-1), in managed code so the tests can run it.</summary>
        public static float ToLinear(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>The trace's stride in pixels for this quality and screen height.</summary>
        public static float Stride(int quality, int screenHeight)
        {
            int q = Mathf.Clamp(quality, 0, Strides.Length - 1);
            return Mathf.Max(1f, Mathf.Round(Strides[q] * Mathf.Max(screenHeight, 360) / 1080f));
        }

        public static float StepCount(int quality)
        {
            return Steps[Mathf.Clamp(quality, 0, Steps.Length - 1)];
        }

        public static void Apply(GlassParams p, GlassFrame f, GlassQuality q)
        {
            Ids();
            // What the shader's loops may spend (GlassQuality): x cells of the city walk, y its
            // coarse blocks, w steps each way up and down to a painted pane's top and bottom;
            // budget2 x across to its sides (0: the panes' own grid), y of the Hi-Z trace (read only
            // while GlassScreen has built the pyramid: _RGHiZInfo.w).
            bool panes = p.PanesFollowWindows && q.PaneSearchSteps > 0 && q.WindowSearchSteps > 0;
            Shader.SetGlobalVector(_budget, new Vector4(q.CityWalkSteps, q.CityWalkBlocks, 0f, panes ? q.WindowSearchSteps : 0f));
            Shader.SetGlobalVector(_budget2, new Vector4(panes ? q.PaneSearchSteps : 0f, q.HiZSteps, 0f, 0f));
            Vector3 tint = Tint(p.TintR, p.TintG, p.TintB);
            float rough = Mathf.Clamp01(1f - p.Smoothness);
            float daylight = Daylight(f.SunY);
            float sunUp = Smooth(-0.02f, 0.06f, f.SunY);

            Shader.SetGlobalVector(_colorMap, new Vector4(Mathf.Max(f.ColorMapWidth, 1), Mathf.Max(f.ColorMapHeight, 1), 0f, 0f));
            // y how much of the asset's painted lines stay (Glass.shader, PaintedWall), z how they show.
            Shader.SetGlobalVector(_glass2, new Vector4(p.EdgeSilver, Mathf.Clamp01(p.PaintedDetail), p.DetailMode, 0f));
            // x the buildings round in the reflections (the map itself says whether it is there:
            // _RGCityMapST.w), w steady glints.
            Shader.SetGlobalVector(_features, new Vector4(p.Neighbours ? 1f : 0f, 0f, 0f, p.SteadyGlints ? 1f : 0f));

            Shader.SetGlobalVector(_glass, new Vector4(p.Reflectivity, p.Glancing, p.Clarity, p.Enabled ? p.Strength : 0f));
            Shader.SetGlobalVector(_tint, new Vector4(tint.x, tint.y, tint.z, p.OwnColour));
            Shader.SetGlobalVector(_mask, new Vector4(p.Threshold, p.NightWindows ? 1f : 0f, p.ShowMask ? 1f : 0f, 0f));
            Shader.SetGlobalVector(_refl, new Vector4(p.ReflectionBrightness, rough, p.SunGlint, Mathf.Max(p.EdgeFade, 0.001f)));
            Shader.SetGlobalVector(_trace, new Vector4(p.Trace ? 1f : 0f, StepCount(p.Quality), Stride(p.Quality, f.ScreenHeight), 1f));
            Shader.SetGlobalVector(_fill, new Vector4(p.OffscreenFill, f.GroundY, Mathf.Max(f.CubeMips - 1, 0), 0f));
            Shader.SetGlobalVector(_pane, new Vector4(Mathf.Max(p.PaneWidth, 0.2f), p.Pillow * PillowAt100, p.Tilt * TiltAt100, p.RollerWave * RollerAt100));
            Shader.SetGlobalVector(_pane2, new Vector4(p.PaneWeather ? BowBias(f.Temperature) : 0f, RollerLength, Mathf.Max(p.FloorHeight, 1f), 0f));
            Shader.SetGlobalVector(_light, new Vector4(daylight, InsideByDay, 0f, 0f));
            // x the share of buildings with dirt on their panes (Dirt.cginc; a building's own amount comes
            // from its glass slot): 0 skips all of it; y 1 while its textures are bound (else none anywhere,
            // a building's own included); z how far it has built up since rain (DirtWeather; 1 without).
            Shader.SetGlobalVector(_dirt, new Vector4(Mathf.Clamp01(p.DirtShare), f.DirtReady ? 1f : 0f,
                                                      p.DirtDynamic ? Mathf.Clamp01(f.DirtBuiltUp) : 1f, 0f));
            Shader.SetGlobalVector(_sunDir, new Vector4(f.SunX, f.SunY, f.SunZ, sunUp));
            // The sun's disc through the clouds: where the live sky's cube carries them, their opacity
            // is its alpha (GlassSky), read in the sun's direction two mips down (a cloud's edge
            // crossing the sun fades it over a degree or so, not a texel).
            Shader.SetGlobalVector(_sunThrough, new Vector4(f.CloudAlpha ? 1f : 0f, Mathf.Min(2f, Mathf.Max(f.CubeMips - 1, 0)), 0f, 0f));
            Shader.SetGlobalVector(_sunColor, new Vector4(f.SunR, f.SunG, f.SunB, 0f));
            Shader.SetGlobalVector(_ambSky, new Vector4(f.AmbSkyR, f.AmbSkyG, f.AmbSkyB, 0f));
            Shader.SetGlobalVector(_ambGround, new Vector4(f.AmbGroundR, f.AmbGroundG, f.AmbGroundB, 0f));
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
