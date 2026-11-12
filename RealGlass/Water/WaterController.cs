using System;
using ColossalFramework;
using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// The F8 panel's Water tab, for one city (on ModController's GameObject, beside the glass;
    /// independent of the glass's switch): the map's water's colour and brightness (<see cref="WaterSurface"/>),
    /// its sky with real water's reflectance per ripple (<see cref="WaterSkyLayer"/>; our cube where it
    /// cannot draw), the ripples, the foam, the city in it (<see cref="WaterCityReflection"/>) and the
    /// view from under it (<see cref="WaterUnder"/>).
    /// On by default since 2026-10-11 (off from 2026-10-03). Off means nothing is held: the theme's water, and
    /// _RGWaterHeld = 0 so RealLight's copy of these controls may take it.
    /// </summary>
    public sealed class WaterController : MonoBehaviour
    {
        private static readonly int IdTable = Shader.PropertyToID("_RGSeaTable");
        private static readonly int IdWaterColour = Shader.PropertyToID("_WaterColorClean");

        private static readonly int IdGameRipples = Shader.PropertyToID("_WaterNormal");
        private static readonly int IdUnderColour = Shader.PropertyToID("_WaterColorUnder");
        private static readonly int IdGameGrab = Shader.PropertyToID("_BackgroundTexture");

        // The realistic depth's state for the log ("" never yet), its numbers last logged, and the looks at
        // the game's own grab (-1: logged).
        private string _depthState = "";
        private string _depthNumbers = "";
        private int _grabLooks;

        private WaterSurface _surface;
        private WaterSkyLayer _sky;
        private WaterCityReflection _city;
        private WaterRipples _ripples;
        private WaterFoam _foam;
        private WaterUnder _under;
        private Vector4[] _table;
        private readonly double _spread = WaterOptics.Spread(WaterOptics.SlopeVariance(WaterOptics.CityWind));
        private bool _failed;
        private string _cityState = "";

        // The ocean waves' heading (degrees from +x toward +z): the game's wind as the city loaded. Not
        // followed after that: a new heading is a new sea, and the waves would jump to it.
        private double _heading = WaterSpectrum.DefaultHeading;

        // Dynamic waves (WaterWeather): the sea's wind under the game's rain, and the Ripple strength's
        // multiplier this frame (1 unless the weather raises the sea); the target last logged.
        private readonly WaterSea _sea = new WaterSea();
        private double _waveGain = 1.0;
        private double _loggedTarget = double.NaN;

        /// <summary>The city's water controller, for <see cref="ShadedRipplesNow"/>.</summary>
        private static WaterController _current;

        /// <summary>
        /// Our moving ripple texture while the water is held (the Water tab's ripples), else null: the
        /// props made water (Props.PropGlassDraw) ripple with it, or with the game's own.
        /// </summary>
        public static Texture ShadedRipplesNow
        {
            get
            {
                WaterController c = _current;
                return c != null && c._ripples != null && c._ripples.Held ? c._ripples.Texture : null;
            }
        }

        private void Awake()
        {
            _current = this;
        }

        private void Start()
        {
            try
            {
                string wind = "no weather: " + _heading.ToString("F0") + " deg";
                if (Singleton<WeatherManager>.exists)
                {
                    float a = Singleton<WeatherManager>.instance.m_windDirection;
                    if (!float.IsNaN(a) && !float.IsInfinity(a))
                    {
                        _heading = WaterSpectrum.HeadingFromGameWind(a);
                        wind = "the game's wind (" + a.ToString("F0") + " deg) toward " + _heading.ToString("F0") + " deg";
                    }
                }

                double[] rho = WaterOptics.BuildTable(WaterOptics.SlopeVariance(WaterOptics.SkyWind));
                _table = new Vector4[WaterOptics.TableSize];
                for (int i = 0; i < _table.Length; i++)
                    _table[i] = new Vector4((float)rho[i], 0f, 0f, 0f);

                _surface = WaterSurface.Create();
                _sky = WaterSkyLayer.Create();
                _city = WaterCityReflection.Create();
                _ripples = WaterRipples.Create();
                _foam = WaterFoam.Create();
                _under = WaterUnder.Create();
                Log.Msg("water: ready (cube " + (_surface != null ? "yes" : "NO SHADER") + ", sky layer " + (_sky != null ? "yes" : "NO SHADER") +
                        ", city reflection " + (_city != null ? "yes" : "NO SHADER") +
                        ", ripples " + (_ripples != null ? "yes" : "NO SHADER") + ", foam " + (_foam != null ? "yes" : "NO SHADER") +
                        ", under the water " + (_under != null ? "yes" : "NO SHADER") +
                        "; reflectance " + (rho[WaterOptics.TableSize - 1] * 100.0).ToString("F1") + "% looking down, " + (rho[4] * 100.0).ToString("F0") +
                        "% at 1 deg); the ocean waves run with " + wind + "; " + Describe());
            }
            catch (Exception e)
            {
                Log.Error("water: could not start", e);
                _failed = true;
                ErrorNotice.Report(ErrorNotice.Water);
            }
        }

        /// <summary>The Water tab's settings, for the log.</summary>
        public static string Describe()
        {
            if (!On(Settings.WaterEnabled))
                return "realistic water off";
            return "realistic water ON: color " + (On(Settings.WaterThemeColor) ? "the map theme's" : Settings.WaterColor != null ? ColorText.Format(Settings.WaterColor.value) : "?") +
                   ", brightness " + Pct(Settings.WaterBrightness) +
                   ", realistic depth " + (On(Settings.WaterDepth) ? "on (clarity " + Value(Settings.WaterClarity, Settings.Defaults.WaterClarity).ToString("F0") +
                    " m, distortion " + Pct(Settings.WaterDistortion) + ", sunlight on the seabed " + Pct(Settings.WaterCaustics) + ")" : "off") +
                   ", underwater color " + (On(Settings.WaterUnderTheme) ? "the map theme's" : Settings.WaterUnderColor != null ? ColorText.Format(Settings.WaterUnderColor.value) : "?") +
                   ", moving ripples " + (On(Settings.WaterRipples) ? Pct(Settings.WaterRippleStrength) +
                   (On(Settings.WaterOceanWaves) ? " as ocean waves (" + (On(Settings.WaterDynamicWaves) ? "dynamic: the weather's wind, " +
                    WaterWeather.CalmWind.ToString("F0") + " m/s dry to " + WaterWeather.StormWind.ToString("F0") + " m/s in the heaviest rain" :
                    "wind " + Value(Settings.WaterWind, Settings.Defaults.WaterWind).ToString("F1") + " m/s") +
                    ", choppiness " + Pct(Settings.WaterChop) + ")" : " as 32 waves (size " + Pct(Settings.WaterRippleSize) + ", wave size " + Pct(Settings.WaterWaveSize) + ")") : "off") +
                   ", foam " + (On(Settings.WaterFoam) ? Pct(Settings.WaterFoamAmount) + " (shores " + Pct(Settings.WaterFoamShore) + ", fast water " +
                   Pct(Settings.WaterFoamRapids) + ", wave fronts " + Pct(Settings.WaterFoamFronts) + ")" : "off") +
                   ", real sky " + (On(Settings.WaterRealSky) ? Pct(Settings.WaterSky) + ", sun glitter " + Pct(Settings.WaterGlitter) + " (size " + Pct(Settings.WaterGlitterSize) +
                    "), moon glitter " + Pct(Settings.WaterMoonGlitter) + " (size " + Pct(Settings.WaterMoonGlitterSize) + ")" : "off") +
                   ", city " + (On(Settings.WaterCity) ? Pct(Settings.WaterCityDay) + " day / " + Pct(Settings.WaterCityNight) + " night, waves " +
                   Pct(Settings.WaterCityWaves) + ", streaks " + Pct(Settings.WaterCityStreaks) + ", length " + Pct(Settings.WaterCityLength) : "off") +
                   ", " + DescribeUnder();
        }

        /// <summary>The Under the water rows, for the log.</summary>
        private static string DescribeUnder()
        {
            return "underwater view " + (On(Settings.WaterUnder) ? "on: visibility " + Value(Settings.WaterUnderVisibility, Settings.Defaults.WaterUnderVisibility).ToString("F0") +
                   " m, light shafts " + (On(Settings.WaterUnderShafts) ? "on" : "off") + ", sunlight on the seabed " + Pct(Settings.WaterUnderCaustics) : "off");
        }

        private static bool On(BoolSetting s)
        {
            return s != null && s.value;
        }

        /// <summary>
        /// The water's color as the Water tab has it now, sRGB: the map theme's (the global the game
        /// and Theme Mixer write) when that row is on or our water is off, else the Water color row.
        /// The props' water takes it when it has no color of its own (1.1.9).
        /// </summary>
        public static Color32 TabColour()
        {
            bool theme = !On(Settings.WaterEnabled) || On(Settings.WaterThemeColor);
            if (theme)
            {
                Color global = Shader.GetGlobalColor(IdWaterColour);
                if (global.r + global.g + global.b > 1e-4f)
                    return global;
            }
            return Settings.WaterColor != null ? Settings.WaterColor.value : Settings.Defaults.WaterColor;
        }

        /// <summary>The Water brightness row, 1 = 100%.</summary>
        public static float TabBrightness()
        {
            return Value(Settings.WaterBrightness, Settings.Defaults.WaterBrightness);
        }

        private static float Value(FloatSetting s, float fallback)
        {
            return s != null ? s.value : fallback;
        }

        /// <summary>To a tenth where there is one: "0.5%", "100%".</summary>
        private static string Pct(FloatSetting s)
        {
            return s != null ? (Math.Round(s.value * 1000.0) / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%" : "?";
        }

        private void LateUpdate()
        {
            if (_failed)
                return;

            try
            {
                Frame();
            }
            catch (Exception e)
            {
                Log.Error("water: frame failed; the water is given back and stays the game's until the city is reloaded", e);
                _failed = true;
                ErrorNotice.Report(ErrorNotice.Water);
                Release();
            }
        }

        private void Frame()
        {
            if (!On(Settings.WaterEnabled))
            {
                Release();
                FollowGlobals();
                return;
            }

            Shader.SetGlobalVectorArray(IdTable, _table);

            Vector3 toSun;
            Color light = WaterLight(out toSun);

            // First: the sky layer reads the ripples the water is shaded with this frame.
            RipplesFrame();

            bool skyLayer = false;
            if (_surface != null)
            {
                _surface.HoldColour(Value(Settings.WaterBrightness, Settings.Defaults.WaterBrightness), On(Settings.WaterThemeColor),
                                    Settings.WaterColor != null ? Settings.WaterColor.value : Settings.Defaults.WaterColor);
                if (On(Settings.WaterRealSky))
                {
                    skyLayer = SkyFrame();
                    _surface.HoldCube(Value(Settings.WaterSky, Settings.Defaults.WaterSky), light, skyLayer);
                }
                else
                {
                    if (_sky != null)
                        _sky.Hide("off");
                    _surface.ReleaseCube();
                }
            }

            FoamFrame();
            float underLevel;
            Camera underCamera;
            bool under = UnderWanted(out underCamera, out underLevel);
            // The realistic depth reads the frame before the water, which the city reflection's surface
            // record copies (WaterSurfaceMask): asked for before it attaches.
            bool depth = skyLayer && On(Settings.WaterDepth) && _sky != null && _sky.DepthSupported && _city != null;
            // The city reflection first: it may give the depth texture back, which the view from under
            // the water then asks for again. It adds nothing only with the eye itself under the level,
            // where every ray of the frame starts in the water (WaterUnder.shader's test); with the eye
            // over it, however close, the water seen from above keeps the city in it.
            CityFrame(light, toSun, skyLayer, under && underCamera.transform.position.y < underLevel, depth);
            UnderFrame(under, underCamera, underLevel, skyLayer);
            // Last: the game's own seabed is taken away only in a frame whose sky layer draws ours.
            bool bedOff = DepthFrame(skyLayer);
            if (_surface != null)
                _surface.HoldUnder(On(Settings.WaterUnderTheme), Settings.WaterUnderColor != null ? Settings.WaterUnderColor.value : Settings.Defaults.WaterUnderColor, bedOff);
            FollowGlobals();
        }

        /// <summary>
        /// The realistic depth this frame (RealGlass/WaterSky's REALISTIC DEPTH): true when the sky layer
        /// draws the seabed itself -- the row on, the layer drawing, its shader having it, the copies of
        /// the frame and the scene's depth before the water made (WaterSurfaceMask). Only then may the
        /// game's own seabed be taken away. One line whenever that changes.
        /// </summary>
        private bool DepthFrame(bool skyLayer)
        {
            string why = null;
            if (!On(Settings.WaterDepth))
                why = "switched off";
            else if (_sky == null || !skyLayer)
                why = "the sky layer does not draw";
            else if (!_sky.DepthSupported)
                why = "the shaders for this platform (" + SystemInfo.graphicsDeviceType + ") do not have it yet";
            else if (_city == null || _city.SceneDepth == null || _city.BedFrame == null)
                why = "no copy of the frame before the water";
            if (why != null)
            {
                if (_sky != null)
                    _sky.NoDepth();
                DepthState("off (" + why + ")");
                return false;
            }

            float clarity = Value(Settings.WaterClarity, Settings.Defaults.WaterClarity);
            bool theme = On(Settings.WaterUnderTheme);
            Color under = theme ? Shader.GetGlobalColor(IdUnderColour) : (Color)(Settings.WaterUnderColor != null ? Settings.WaterUnderColor.value : Settings.Defaults.WaterUnderColor);
            Color tint = under.linear;
            double[] sigma = WaterOptics.DepthSigma(clarity, tint.r, tint.g, tint.b);

            Color sun, sky;
            Light sunLight;
            Vector3 sunWay;
            SunAndSky(out sunWay, out sun, out sky, out sunLight);
            double[] down = WaterOptics.DepthSunUnder(sunWay.x, sunWay.y, sunWay.z);
            double share = WaterOptics.DepthSunShare(sunWay.y, Luminance(sun), Luminance(sky));
            _sky.Depth(_city.BedFrame, _city.SceneDepth, Value(Settings.WaterDistortion, Settings.Defaults.WaterDistortion),
                       Value(Settings.WaterCaustics, Settings.Defaults.WaterCaustics), new Vector3((float)sigma[0], (float)sigma[1], (float)sigma[2]),
                       new Vector4((float)down[0], (float)down[1], (float)down[2], (float)share));

            string numbers = "clarity " + clarity.ToString("F0") + " m: " + sigma[0].ToString("F3") + " " + sigma[1].ToString("F3") + " " + sigma[2].ToString("F3") +
                             " per m of path (red green blue) from the underwater color " + (theme ? "the theme's " : "") + ColorText.Format((Color32)under) +
                             ", distortion " + Pct(Settings.WaterDistortion) + ", sunlight on the seabed " + Pct(Settings.WaterCaustics);
            if (!_depthState.StartsWith("on", StringComparison.Ordinal))
                Log.Msg("water: realistic depth on (" + numbers + "; the bed copied at the final pass, " + _city.BedFrame.width + " x " + _city.BedFrame.height +
                        "); the game's own seabed off on its water meanwhile");
            else if (numbers != _depthNumbers && Log.Detailed)
                Log.Detail("water: realistic depth: " + numbers);
            _depthNumbers = numbers;
            _depthState = "on";
            LogGameGrab();
            return true;
        }

        private void DepthState(string state)
        {
            if (state == _depthState)
                return;
            // Off from the start with the row off: nothing to say.
            if (_depthState.Length > 0 || state != "off (switched off)")
                Log.Msg("water: realistic depth " + state);
            _depthState = state;
        }

        /// <summary>
        /// Once a city: what the game's own grab of the frame before its water (_BackgroundTexture, a named
        /// GrabPass of Custom/Water/Default) reads as a global here, in case a later version can use it in
        /// place of our copy. Looked at for a while if it is not there at first.
        /// </summary>
        private void LogGameGrab()
        {
            if (_grabLooks < 0)
                return;
            Texture grab = Shader.GetGlobalTexture(IdGameGrab);
            if (grab == null && _grabLooks++ < 600)
            {
                if (_grabLooks == 1)
                    Log.Msg("water: the game's own grab of the frame (_BackgroundTexture) is not set as a global now; looking again for a while");
                return;
            }
            RenderTexture rt = grab as RenderTexture;
            Log.Msg("water: the game's own grab of the frame (_BackgroundTexture): " + (grab == null ? "never seen as a global" :
                    grab.width + " x " + grab.height + (rt != null ? ", " + rt.format : ", " + grab.GetType().Name)));
            _grabLooks = -1;
        }

        private static float Luminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        /// <summary>
        /// Whether the view from under the water draws this frame: switched on, the camera in the water
        /// (or its near plane dipping in: <see cref="WaterUnder.Below"/>), no info view, not the snapshot
        /// tool. <paramref name="level"/>: the water's level at the camera.
        /// </summary>
        private bool UnderWanted(out Camera camera, out float level)
        {
            camera = null;
            level = float.NaN;
            if (_under == null || !On(Settings.WaterUnder))
                return false;
            if (Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None)
                return false;
            if (ToolsModifierControl.toolController != null && ToolsModifierControl.toolController.CurrentTool is SnapshotTool)
                return false;
            camera = GameCamera.Get();
            return WaterUnder.Below(camera, out level);
        }

        /// <summary>
        /// The view from under the water this frame, or off: lit by the daylight that reaches the water
        /// (the sun as the water's light reads it, the sky's ambient); its colour the water's own kind's
        /// (WaterUnderOptics), not the Water color row's: a map theme's near-black water made it grey. The
        /// surface from below sees the sky on the water's live cube, its ripples as rough (skyLayer: that
        /// layer draws this frame).
        /// </summary>
        private void UnderFrame(bool under, Camera camera, float level, bool skyLayer)
        {
            if (_under == null)
                return;
            if (!under)
            {
                if (_under.Attached)
                    _under.Detach(OthersWantDepth());
                return;
            }

            Vector3 toSun;
            Color sun, sky;
            Light sunLight;
            SunAndSky(out toSun, out sun, out sky, out sunLight);
            double[] optics = WaterUnderOptics.Vectors(Value(Settings.WaterUnderVisibility, Settings.Defaults.WaterUnderVisibility),
                                                       new double[] { toSun.x, toSun.y, toSun.z }, new double[] { sun.r, sun.g, sun.b },
                                                       new double[] { sky.r, sky.g, sky.b });
            _under.Show(camera, level, optics, Value(Settings.WaterUnderCaustics, Settings.Defaults.WaterUnderCaustics), ShadedRipples(),
                        On(Settings.WaterUnderShafts), sunLight, sun, skyLayer ? _sky : null, DescribeUnder());
        }

        /// <summary>The glass or the city reflection still read the depth texture.</summary>
        private bool OthersWantDepth()
        {
            return (_city != null && _city.Attached) ||
                   (Glass.GlassController.Instance != null && Glass.GlassController.Instance.enabled && On(Settings.GlassEnabled));
        }

        /// <summary>
        /// What a release left on the water materials stays equal to the game's globals (another
        /// mod may move them): WaterSurface.FollowGlobals and WaterRipples.FollowGlobal, every frame.
        /// </summary>
        private void FollowGlobals()
        {
            if (_surface != null)
                _surface.FollowGlobals();
            if (_ripples != null)
                _ripples.FollowGlobal();
        }

        /// <summary>
        /// The sky on the water per pixel this frame (<see cref="WaterSkyLayer"/>), or not: then our cube
        /// carries it, as before 2026-10-08 (the layer missing or failed, an info view, the snapshot tool).
        /// </summary>
        private bool SkyFrame()
        {
            if (_sky == null)
                return false;

            Camera camera = GameCamera.Get();
            string why = null;
            if (camera == null)
                why = "no camera";
            else if (Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None)
                why = "an info view is open";
            else if (ToolsModifierControl.toolController != null && ToolsModifierControl.toolController.CurrentTool is SnapshotTool)
                why = "the snapshot tool";
            if (why != null)
            {
                _sky.Hide(why);
                return false;
            }

            bool ours = _ripples != null && _ripples.Held;
            double rms = _ripples != null ? _ripples.DecodedRms : WaterOptics.DefaultRippleRms;
            double slope = WaterOptics.RippleSlope(rms, ours ? Value(Settings.WaterRippleStrength, Settings.Defaults.WaterRippleStrength) * _waveGain : 1.0);
            return _sky.Show(Value(Settings.WaterSky, Settings.Defaults.WaterSky), ShadedRipples(), slope * slope,
                             ours ? Value(Settings.WaterRippleSize, Settings.Defaults.WaterRippleSize) : 1.0,
                             ours ? Value(Settings.WaterWaveSize, Settings.Defaults.WaterWaveSize) : 1.0, camera.transform.position, camera.farClipPlane,
                             ours ? _ripples.Ocean : null, slope,
                             new WaterSkyLayer.Glints(Value(Settings.WaterGlitter, Settings.Defaults.WaterGlitter),
                                                      Value(Settings.WaterGlitterSize, Settings.Defaults.WaterGlitterSize),
                                                      Value(Settings.WaterMoonGlitter, Settings.Defaults.WaterMoonGlitter),
                                                      Value(Settings.WaterMoonGlitterSize, Settings.Defaults.WaterMoonGlitterSize)));
        }

        /// <summary>The foam on the game's water this frame, or none (off, an info view, the snapshot tool, no sea level).</summary>
        private void FoamFrame()
        {
            if (_foam == null)
                return;

            float level = float.NaN;
            if (Singleton<TerrainManager>.exists && Singleton<TerrainManager>.instance.WaterSimulation != null)
                level = Singleton<TerrainManager>.instance.WaterSimulation.m_currentSeaLevel;
            bool show = On(Settings.WaterFoam) && !float.IsNaN(level) && !float.IsInfinity(level) &&
                        !(Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None) &&
                        !(ToolsModifierControl.toolController != null && ToolsModifierControl.toolController.CurrentTool is SnapshotTool);
            if (!show)
            {
                _foam.Hide();
                return;
            }

            _foam.Show(Value(Settings.WaterFoamAmount, Settings.Defaults.WaterFoamAmount), Value(Settings.WaterFoamShore, Settings.Defaults.WaterFoamShore),
                       Value(Settings.WaterFoamRapids, Settings.Defaults.WaterFoamRapids), Value(Settings.WaterFoamFronts, Settings.Defaults.WaterFoamFronts), level,
                       Value(Settings.WaterFoamView, 0f));
        }

        /// <summary>
        /// Our moving ripples on the water (drawn this frame), or the game's own again. The game's
        /// texture is measured either way: the city reflection's gain is set against it.
        /// </summary>
        private void RipplesFrame()
        {
            if (_ripples == null)
                return;

            if (On(Settings.WaterRipples))
            {
                bool weather = WeatherFrame();
                float wind = weather ? (float)WaterWeather.Quantize(_sea.Wind) : Value(Settings.WaterWind, Settings.Defaults.WaterWind);
                _ripples.Draw(Value(Settings.WaterRippleStrength, Settings.Defaults.WaterRippleStrength) * (float)_waveGain,
                              Value(Settings.WaterRippleSize, Settings.Defaults.WaterRippleSize),
                              Value(Settings.WaterWaveSize, Settings.Defaults.WaterWaveSize),
                              On(Settings.WaterOceanWaves), wind, _heading,
                              Value(Settings.WaterChop, Settings.Defaults.WaterChop), weather);
                _ripples.Hold();
            }
            else
            {
                StopWeather();
                _ripples.Release();
                _ripples.Measure();
            }
        }

        /// <summary>
        /// Dynamic waves this frame: the sea's wind followed under the game's rain (true; <see cref="_waveGain"/>
        /// its roughness), or not (false: Dynamic waves or the ocean waves off -- the Wind speed row's wind,
        /// exactly as before; turned on again, the sea starts on its weather at once).
        /// </summary>
        private bool WeatherFrame()
        {
            if (!On(Settings.WaterOceanWaves) || !On(Settings.WaterDynamicWaves) || !Singleton<SimulationManager>.exists)
            {
                StopWeather();
                return false;
            }

            SimulationManager sm = Singleton<SimulationManager>.instance;
            double rain = Singleton<WeatherManager>.exists ? Singleton<WeatherManager>.instance.m_currentRain : 0.0;
            bool first = !_sea.Started;
            _sea.Advance(rain, (double)sm.m_referenceFrameIndex + sm.m_referenceTimer);
            _waveGain = WaterWeather.RoughnessGain(_sea.Wind);

            if (first)
            {
                _loggedTarget = _sea.Target;
                Log.Msg("water: dynamic waves: the sea's wind " + _sea.Wind.ToString("F1") + " m/s under " + Rain(rain) + " rain (" +
                        WaterWeather.CalmWind.ToString("F0") + " m/s dry to " + WaterWeather.StormWind.ToString("F0") +
                        " m/s in the heaviest rain), ripples x" + _waveGain.ToString("F2"));
            }
            else if (Math.Abs(_sea.Target - _loggedTarget) >= 1.0)
            {
                _loggedTarget = _sea.Target;
                if (Log.Detailed)
                    Log.Detail("water: dynamic waves: the weather's wind " + _sea.Target.ToString("F1") + " m/s (" + Rain(rain) + " rain); the sea's " +
                               _sea.Wind.ToString("F1") + " m/s, ripples x" + _waveGain.ToString("F2"));
            }

            return true;
        }

        private void StopWeather()
        {
            _sea.Reset();
            _waveGain = 1.0;
            _loggedTarget = double.NaN;
        }

        private static string Rain(double rain)
        {
            return double.IsNaN(rain) ? "?" : (Math.Max(0.0, Math.Min(1.0, rain)) * 100.0).ToString("F0") + "%";
        }

        /// <summary>The ripple texture the water is shaded with now: ours while held, else the game's own.</summary>
        private Texture ShadedRipples()
        {
            if (_ripples != null && _ripples.Held)
                return _ripples.Texture;
            return Shader.GetGlobalTexture(IdGameRipples);
        }

        /// <summary>
        /// The light the game's water lights its cube with (linear; the sun as the glass reads it,
        /// GlassController.ReadFrame): the sun on the level + the sky's ambient, each channel floored
        /// (<see cref="WaterOptics.FloorLight"/>).
        /// </summary>
        private static Color WaterLight(out Vector3 toSun)
        {
            Color sun, sky;
            Light sunLight;
            SunAndSky(out toSun, out sun, out sky, out sunLight);
            Color lit = sun * Mathf.Max(0f, toSun.y) + sky;
            double r = lit.r, g = lit.g, b = lit.b;
            WaterOptics.FloorLight(ref r, ref g, ref b);
            return new Color((float)r, (float)g, (float)b, 1f);
        }

        /// <summary>
        /// The sun as the glass reads it (GlassController.ReadFrame): toward it, its light (linear: colour
        /// x intensity; black while its Light is off -- at night), its Light (null if none), and the sky's
        /// ambient (linear).
        /// </summary>
        private static void SunAndSky(out Vector3 toSun, out Color sun, out Color sky, out Light sunLight)
        {
            toSun = Vector3.down;
            sun = Color.black;
            sunLight = null;
            DayNightProperties dn = DayNightProperties.instance;
            if (dn != null && dn.m_SunLight != null)
            {
                toSun = -dn.m_SunLight.forward;
                sunLight = dn.m_SunLight.GetComponent<Light>();
                if (sunLight != null && sunLight.enabled)
                    sun = sunLight.color.linear * sunLight.intensity;
            }

            sky = RenderSettings.ambientSkyColor.linear;
        }

        /// <param name="eyeUnder">The camera's eye under the water's level (the view from under the water
        /// fills the frame): the city reflection adds nothing, but stays on -- a camera bobbing at the
        /// surface would switch it on and off every frame. Not merely near the level (2026-10-31: a camera
        /// skimming the water lost the city in it).</param>
        /// <param name="depth">The realistic depth wants the frame before the water copied with the scene's
        /// depth (WaterSurfaceMask.BedFrame): the surface is recorded for it with the reflection off, on
        /// Metal too.</param>
        private void CityFrame(Color lit, Vector3 toSun, bool skyLayer, bool eyeUnder, bool depth)
        {
            if (_city == null)
                return;

            _city.WantBed(depth);

            // First what stops the water's surface record as well, then what stops only the reflection.
            Camera camera = GameCamera.Get();
            string stop = null;
            if (camera == null)
                stop = "no camera";
            else if (Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None)
                stop = "an info view is open";
            else if (ToolsModifierControl.toolController != null && ToolsModifierControl.toolController.CurrentTool is SnapshotTool)
                stop = "the snapshot tool";

            string why = stop;
            float level = float.NaN;
            if (why == null && !On(Settings.WaterCity))
                why = "switched off";
            else if (why == null)
            {
                TerrainManager terrain = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance : null;
                if (terrain != null && terrain.WaterSimulation != null)
                    level = terrain.WaterSimulation.m_currentSeaLevel;
                if (float.IsNaN(level) || float.IsInfinity(level))
                    why = "no sea level";
            }

            if (why != null)
            {
                // The tall waves stay solid with the reflection off (the author's call, 2026-10-31: a fix
                // of ours holds wherever the realistic water is on): the surface alone, recorded and put
                // into the depth texture (WaterSurfaceMask), without the reflection's copy and trace. On
                // Metal there is nothing to put in.
                bool surfaceOnly = stop == null && (WaterSurfaceMask.MergesDepth || depth);
                if (surfaceOnly)
                    _city.AttachSurface(camera);
                else if (_city.Attached)
                    _city.Detach();
                CityState("off (" + why + ")" + (surfaceOnly ? (WaterSurfaceMask.MergesDepth ? "; the water's surface still put into the depth texture" :
                                                                "; the frame before the water still copied for the realistic depth") : ""));
                return;
            }

            float day = (float)WaterOptics.Daylight(toSun.y);
            float dayStrength = Value(Settings.WaterCityDay, Settings.Defaults.WaterCityDay);
            float nightStrength = Value(Settings.WaterCityNight, Settings.Defaults.WaterCityNight);
            float strength = eyeUnder ? 0f : nightStrength + (dayStrength - nightStrength) * day;
            float skyShare = _surface != null && _surface.CubeHeld ? Value(Settings.WaterSky, Settings.Defaults.WaterSky) : 0f;
            // The sky the city takes the place of: the live sky the sky layer draws (light already), or
            // the theme's under our cube (lit by the water's light).
            Texture sky = skyLayer && _sky.Cube != null ? _sky.Cube : WaterSurface.ThemeCube();
            Color skyLight = skyLayer && _sky.Cube != null ? Color.white : lit;

            double up = WaterOptics.StreakSigma(Value(Settings.WaterCityStreaks, Settings.Defaults.WaterCityStreaks), _spread, camera.fieldOfView);
            double rms = _ripples != null ? _ripples.DecodedRms : WaterOptics.DefaultRippleRms;
            double gain = WaterOptics.CityRippleGain(Value(Settings.WaterCityWaves, Settings.Defaults.WaterCityWaves), rms);
            _city.SetUniforms(level, strength, skyShare, sky, skyLight, up, ShadedRipples(), gain,
                              Value(Settings.WaterCityLength, Settings.Defaults.WaterCityLength), camera.farClipPlane);
            _city.Attach(camera);
            CityState("on (sea level " + level.ToString("F1") + " m)");
        }

        /// <summary>One line whenever the city reflection's state changes.</summary>
        private void CityState(string state)
        {
            if (state == _cityState)
                return;
            // The sea level moves with the tide of a tsunami: not a state of its own.
            if (state.StartsWith("on", StringComparison.Ordinal) && _cityState.StartsWith("on", StringComparison.Ordinal))
                return;
            _cityState = state;
            Log.Msg("water: city reflection " + state);
        }

        private void Release()
        {
            if (_city != null && _city.Attached)
                _city.Detach();
            if (_under != null && _under.Attached)
                _under.Detach(OthersWantDepth());
            if (_ripples != null)
                _ripples.Release();
            if (_foam != null)
                _foam.Hide();
            if (_sky != null)
            {
                _sky.NoDepth();
                _sky.Hide("off");
            }
            if (_surface != null)
                _surface.GiveBack();
            else
                Shader.SetGlobalFloat("_RGWaterHeld", 0f);
            _cityState = "";
            _depthState = "";
        }

        private void OnDestroy()
        {
            if (_current == this)
                _current = null;
            try
            {
                if (_city != null)
                    _city.Destroy();
                if (_ripples != null)
                    _ripples.Destroy();
                if (_foam != null)
                    _foam.Destroy();
                if (_under != null)
                    _under.Destroy();
                if (_sky != null)
                    _sky.Destroy();
                if (_surface != null)
                    _surface.Destroy();
                Shader.SetGlobalFloat("_RGWaterHeld", 0f);
            }
            catch (Exception e)
            {
                Log.Error("water: could not give the water back", e);
            }

            _city = null;
            _ripples = null;
            _foam = null;
            _under = null;
            _sky = null;
            _surface = null;
        }
    }
}
