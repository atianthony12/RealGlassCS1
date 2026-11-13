using System;
using ColossalFramework;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Water
{
    /// <summary>
    /// The game's water, on its two materials only (TerrainManager.m_waterMaterial and
    /// m_waterTransparentMaterial, both public; a material's value overrides the global the game and
    /// Theme Mixer set, and buildings keep the theme's): its own colour, and a cube that reflects the
    /// theme's sky the way real water does. Ported from RealLight's WaterReflection (2026-10-03).
    /// </summary>
    /// <remarks>
    /// What the game does (RealLight's disassembly, 2026-10-02): Custom/Water/Default adds the theme's
    /// _EnvironmentCubemap x 0.5 for clean water at EVERY angle -- no Fresnel, so a white cubemap gives
    /// white water -- lit by the sun and the ambient, over _WaterColorClean.
    /// - _WaterColorClean: the Water color row's (since 2026-10-08, the author: "rather than rely on
    ///   Theme Mixer to edit the water color, we can do that with our mod"), or the theme's (the
    ///   global, read every frame: Theme Mixer writes it there, ThemeMixer.Themes.Water.WaterColor.
    ///   LoadValue), x the Brightness slider.
    /// - _WaterColorUnder (since 2.0.0): the Underwater color row's, over the theme's
    ///   (TerrainProperties.m_waterColorUnder, a global); left alone while the theme's is asked for.
    /// - _EnvironmentCubemap: a small cube (RealGlass/WaterCube as a hidden camera's skybox,
    ///   Camera.RenderToCubemap) of the theme's own cube x rho(reflected ray's height, at least the
    ///   level) x <see cref="WaterOptics.CubeGain"/>: lit by the game as before, it comes back as the
    ///   theme's sky x real water's reflectance x the Sky slider. Re-drawn only when the slider or the
    ///   theme's cube changes (Cubemap Replacer, Theme Mixer). While the sky layer draws the sky per
    ///   pixel (<see cref="WaterSkyLayer"/>), the opaque water's material holds a BLACK cube instead:
    ///   the game adds no sky of its own; ours stays on the transparent material.
    /// - While either is held the global _RGWaterHeld is 1 (else 0): RealLight then leaves the water
    ///   to us (its own copy of these controls).
    /// Given back: the materials' own values, or the theme's where they held none (or RealLight's).
    /// </remarks>
    public sealed class WaterSurface
    {
        public const int Size = 64;

        private static readonly int IdCube = Shader.PropertyToID("_EnvironmentCubemap");
        private static readonly int IdColour = Shader.PropertyToID("_WaterColorClean");
        private static readonly int IdUnder = Shader.PropertyToID("_WaterColorUnder");
        private static readonly int IdHeld = Shader.PropertyToID("_RGWaterHeld");
        private static readonly int IdTheme = Shader.PropertyToID("_RGWaterTheme");
        private static readonly int IdGain = Shader.PropertyToID("_RGWaterGain");
        private static readonly int IdLight = Shader.PropertyToID("_RGWaterLight");

        // A real sky another mod may publish (the author's own RealLight, internal; see WaterCommon.cginc).
        private static readonly int IdRealSky = Shader.PropertyToID("_RLSkyLightCube");
        private static readonly int IdRealSkyWeight = Shader.PropertyToID("_RLSkyLightCubeWeight");

        /// <summary>Seconds between redraws while a real sky is published (it is redrawn every 0.25 s itself).</summary>
        public const float RealSkyInterval = 0.25f;

        private readonly Material _material;
        private readonly RenderTexture _cube;
        // The opaque water's cube while the sky layer draws its sky (WaterSkyLayer): no sky of the game's.
        private readonly RenderTexture _black;
        private bool _blackDrawn;
        private readonly GameObject _object;
        private readonly Camera _capture;
        private Material[] _water;
        private Texture[] _originalCube;
        private Color?[] _originalColour;
        private Color?[] _originalUnder;

        // Which of the two materials had NO value of their own when first seen (the global was what
        // they showed). After a release they carry a copy of the global -- Unity 5.6 cannot take a
        // material property away again -- and that copy would shadow every later change of the global
        // (Theme Mixer's water colour, Cubemap Replacer's day/night cube) until the city was reloaded.
        // So while not held they are kept equal to the global (FollowGlobals). Kept across GiveBack: a
        // second take must not mistake the copy for the material's own (audit, 2026-10-11).
        private Material[] _followed;
        private bool[] _followCube, _followColour, _followUnder;

        // The underwater colour was written on this pair of materials at least once: only from then on
        // are they kept to the theme's while not held (left alone before, the game's own).
        private bool _underTouched;

        private bool _cubeHeld, _colourHeld, _underHeld;
        private string _heldColour = "", _heldUnder = "";
        private Color _loggedThemeUnder = new Color(-1f, -1f, -1f, -1f);
        private Texture _drawnTheme;
        private float _drawnGain = -1f;
        private float _drawnWeight;
        private float _nextRealSky;

        private WaterSurface(Shader shader)
        {
            _material = new Material(shader) { name = "RealGlass water cube", hideFlags = HideFlags.HideAndDontSave };
            _cube = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "RealGlassWaterSky",
                hideFlags = HideFlags.HideAndDontSave,
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            _cube.Create();
            _black = new RenderTexture(8, 8, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "RealGlassWaterNoSky",
                hideFlags = HideFlags.HideAndDontSave,
                dimension = TextureDimension.Cube,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _black.Create();

            _object = new GameObject("RealGlassWaterCubeCamera") { hideFlags = HideFlags.HideAndDontSave };
            _capture = _object.AddComponent<Camera>();
            _capture.enabled = false;
            _capture.clearFlags = CameraClearFlags.Skybox;
            _capture.cullingMask = 0;
            _capture.allowHDR = true;
            _capture.allowMSAA = false;
            _capture.renderingPath = RenderingPath.Forward;
            _capture.useOcclusionCulling = false;
            _capture.nearClipPlane = 0.3f;
            _capture.farClipPlane = 1000f;
            _object.AddComponent<Skybox>().material = _material;
        }

        /// <summary>Null when the shader is missing or unsupported here (logged by ShaderBundle): the water keeps the theme's cube.</summary>
        public static WaterSurface Create()
        {
            Shader shader = ShaderBundle.Get(ShaderBundle.WaterCube);
            return shader != null && shader.isSupported ? new WaterSurface(shader) : null;
        }

        public bool CubeHeld
        {
            get { return _cubeHeld; }
        }

        /// <summary>The theme's cube, as the game set it (the global: our cube is only ever on the two water materials).</summary>
        public static Texture ThemeCube()
        {
            return Shader.GetGlobalTexture(IdCube);
        }

        /// <summary>A published real sky's weight now (0..1; 0 when none is published: an unset global reads 0).</summary>
        public static float RealSkyWeight()
        {
            if (Shader.GetGlobalTexture(IdRealSky) == null)
                return 0f;
            float w = Shader.GetGlobalFloat(IdRealSkyWeight);
            return float.IsNaN(w) ? 0f : Mathf.Clamp01(w);
        }

        /// <summary>
        /// The cube of real water's reflectance on the water's materials. Re-drawn when the slider or
        /// the theme's cube changed; and while a real sky is published (the author's own RealLight,
        /// internal, only while we hold the water), every <see cref="RealSkyInterval"/> s, as that sky
        /// and the light it is divided by change.
        /// </summary>
        /// <param name="light">The light the game's water lights the cube with (linear, floored: <see cref="WaterOptics.FloorLight"/>).</param>
        /// <param name="skyLayer">
        /// The sky layer draws the sky on the opaque water this frame (<see cref="WaterSkyLayer"/>): that
        /// material gets a black cube, so the game adds no sky of its own; the transparent one (info
        /// views, the water tool) keeps ours.
        /// </param>
        public void HoldCube(float skySlider, Color light, bool skyLayer)
        {
            if (!Take())
                return;

            if (skyLayer && !_blackDrawn)
            {
                // The cube's shader with a gain of 0 draws black whatever it reads (a published sky included).
                _material.SetVector(IdGain, Vector4.zero);
                _capture.RenderToCubemap(_black);
                _blackDrawn = true;
                _drawnGain = -1f;
            }

            Texture theme = ThemeCube();
            float gain = (float)WaterOptics.CubeGain(skySlider);
            float weight = RealSkyWeight();
            float now = Time.realtimeSinceStartup;
            bool realSkyDue = (weight > 0f || _drawnWeight > 0f) && now >= _nextRealSky;
            if (theme != _drawnTheme || Mathf.Abs(gain - _drawnGain) > 1e-4f || !_cubeHeld || realSkyDue)
            {
                _nextRealSky = now + RealSkyInterval;
                // The theme's cube read at the mip whose texels are this cube's (64 px): no aliasing.
                int themeSize = theme != null ? Mathf.Max(theme.width, Size) : Size;
                _material.SetTexture(IdTheme, theme);
                _material.SetVector(IdGain, new Vector4(gain, theme != null ? 1f : 0f, Mathf.Log(themeSize / (float)Size, 2f), 0f));
                _material.SetVector(IdLight, new Vector4(light.r, light.g, light.b, 0f));
                _capture.RenderToCubemap(_cube);
                if ((weight > 0f) != (_drawnWeight > 0f))
                    Log.Msg("water: " + (weight > 0f ? "a real sky is published: the water reflects it (weight " + weight.ToString("F2") + ")" : "no real sky now: the water reflects the theme's"));
                _drawnWeight = weight;
                if (theme != _drawnTheme || !_cubeHeld)
                {
                    Log.Msg("water: the map's water reflects the theme's sky (" + (theme != null ? "'" + theme.name + "' " + theme.width + " px" : "none") +
                            ") with real water's reflectance, a " + Size + " px cube; buildings keep the theme's");
                }
                _drawnTheme = theme;
                _drawnGain = gain;
            }

            for (int i = 0; i < 2; i++)
            {
                Texture want = i == 0 && skyLayer ? _black : _cube;
                if (_water[i] != null && _water[i].GetTexture(IdCube) != want)
                    _water[i].SetTexture(IdCube, want);
            }

            _cubeHeld = true;
            Shader.SetGlobalFloat(IdHeld, 1f);
        }

        /// <summary>
        /// The clean water's colour on both water materials: ours (<paramref name="own"/>, sRGB as the
        /// picker shows it) or, with <paramref name="useTheme"/>, the map theme's (the global the game and
        /// Theme Mixer set: it is re-read every frame), x the brightness (in linear light). The theme's
        /// alpha is kept. One log line whenever what is held changes.
        /// </summary>
        public void HoldColour(float brightness, bool useTheme, Color32 own)
        {
            if (!Take())
                return;

            Color theme = Shader.GetGlobalColor(IdColour);
            Color source = useTheme ? theme : (Color)own;
            float k = Mathf.Max(0f, brightness);
            Color colour = (source.linear * k).gamma;
            colour.a = theme.a;
            for (int i = 0; i < 2; i++)
            {
                if (_water[i] != null)
                    _water[i].SetColor(IdColour, colour);
            }

            string held = (useTheme ? "the map theme's" : "ours, " + ColorText.Format(own)) + " x " + Mathf.RoundToInt(k * 100f) +
                          "% (the theme's " + ColorText.Format((Color32)theme) + ")";
            if (!_colourHeld || held != _heldColour)
                Log.Msg("water: the water's colour is " + held);
            _heldColour = held;
            _colourHeld = true;
            Shader.SetGlobalFloat(IdHeld, 1f);
        }

        /// <summary>
        /// The underwater colour (the game's _WaterColorUnder: its seabed through the water is tinted by
        /// 2 x it from about 2 m of water on) on both water materials: ours (<paramref name="own"/>, sRGB as
        /// the picker shows it) unless <paramref name="useTheme"/>. With the theme's asked for (and no
        /// <paramref name="bedOff"/>) nothing is written: the game's own value stays, or, once ours was
        /// there, the theme's global is kept on them (<see cref="FollowGlobals"/>), as for the colour. The
        /// theme's global is logged whenever it changes.
        /// </summary>
        /// <param name="bedOff">
        /// The sky layer draws the seabed itself this frame (the realistic depth: RealGlass/WaterSky): the
        /// OPAQUE material gets 0 -- its seabed gone past its first metre or so of water; the transparent
        /// one (info views, the water tool) keeps the colour.
        /// </param>
        public void HoldUnder(bool useTheme, Color32 own, bool bedOff)
        {
            Color theme = Shader.GetGlobalColor(IdUnder);
            if (theme != _loggedThemeUnder)
            {
                _loggedThemeUnder = theme;
                Log.Msg("water: the map theme's underwater color is " + ColorText.Format((Color32)theme) + " (" + theme.r.ToString("F3") + ", " +
                        theme.g.ToString("F3") + ", " + theme.b.ToString("F3") + " as the game sets it)");
            }

            if (useTheme && !bedOff)
            {
                ReleaseUnder();
                return;
            }

            if (!Take())
                return;

            Color colour = useTheme ? theme : (Color)own;
            colour.a = theme.a;
            for (int i = 0; i < 2; i++)
            {
                Color want = i == 0 && bedOff ? new Color(0f, 0f, 0f, theme.a) : colour;
                if (_water[i] != null && _water[i].GetColor(IdUnder) != want)
                    _water[i].SetColor(IdUnder, want);
            }

            string held = (useTheme ? "the map theme's" : "ours, " + ColorText.Format(own)) + " (the theme's " + ColorText.Format((Color32)theme) + ")" +
                          (bedOff ? "; the game's own seabed off on its water (the realistic depth draws it)" : "");
            if (!_underHeld || held != _heldUnder)
                Log.Msg("water: the underwater color is " + held);
            _heldUnder = held;
            _underHeld = true;
            _underTouched = true;
            Shader.SetGlobalFloat(IdHeld, 1f);
        }

        /// <summary>The theme's underwater colour on the water again (nothing if ours was never there).</summary>
        public void ReleaseUnder()
        {
            if (!_underHeld)
                return;

            _underHeld = false;
            if (_water != null)
            {
                Color theme = Shader.GetGlobalColor(IdUnder);
                for (int i = 0; i < 2; i++)
                {
                    bool follow = _followUnder != null && _followUnder[i];
                    if (_water[i] != null)
                        _water[i].SetColor(IdUnder, follow || !_originalUnder[i].HasValue ? theme : _originalUnder[i].Value);
                }
            }

            Log.Msg("water: the underwater color is the theme's again");
            Held();
        }

        /// <summary>The theme's cube on the water again.</summary>
        public void ReleaseCube()
        {
            if (!_cubeHeld)
                return;

            _cubeHeld = false;
            _drawnTheme = null;
            _drawnWeight = 0f;
            if (_water != null)
            {
                Texture theme = ThemeCube();
                for (int i = 0; i < 2; i++)
                {
                    bool follow = _followCube != null && _followCube[i];
                    if (_water[i] != null)
                        _water[i].SetTexture(IdCube, follow || _originalCube[i] == null ? theme : _originalCube[i]);
                }
            }

            Log.Msg("water: the map's water reflects the theme's cubemap again");
            Held();
        }

        /// <summary>The theme's colour on the water again.</summary>
        public void ReleaseColour()
        {
            if (!_colourHeld)
                return;

            _colourHeld = false;
            if (_water != null)
            {
                Color theme = Shader.GetGlobalColor(IdColour);
                for (int i = 0; i < 2; i++)
                {
                    bool follow = _followColour != null && _followColour[i];
                    if (_water[i] != null)
                        _water[i].SetColor(IdColour, follow || !_originalColour[i].HasValue ? theme : _originalColour[i].Value);
                }
            }

            Log.Msg("water: the water's colour is the theme's again");
            Held();
        }

        public void GiveBack()
        {
            ReleaseCube();
            ReleaseColour();
            ReleaseUnder();
            _water = null;
            Shader.SetGlobalFloat(IdHeld, 0f);
        }

        /// <summary>
        /// Every frame, held or not: a material that showed the global when first seen keeps showing
        /// it -- the copy a release left on it is brought up to the global whenever the global moves
        /// (another mod's doing), as if the property had never been set. Nothing while the value is
        /// held, nothing when they already agree.
        /// </summary>
        public void FollowGlobals()
        {
            if (_followed == null)
                return;

            for (int i = 0; i < 2; i++)
            {
                Material m = _followed[i];
                if (m == null)
                    continue;

                if (!_cubeHeld && _followCube[i])
                {
                    Texture global = ThemeCube();
                    if (global != null && m.GetTexture(IdCube) != global)
                        m.SetTexture(IdCube, global);
                }

                if (!_colourHeld && _followColour[i])
                {
                    Color global = Shader.GetGlobalColor(IdColour);
                    if (m.GetColor(IdColour) != global)
                        m.SetColor(IdColour, global);
                }

                if (!_underHeld && _underTouched && _followUnder[i])
                {
                    Color global = Shader.GetGlobalColor(IdUnder);
                    if (m.GetColor(IdUnder) != global)
                        m.SetColor(IdUnder, global);
                }
            }
        }

        private void Held()
        {
            Shader.SetGlobalFloat(IdHeld, _cubeHeld || _colourHeld || _underHeld ? 1f : 0f);
        }

        /// <summary>
        /// The terrain's two water materials, their own values kept to give back (new materials: the
        /// old given back first). A value RealLight left there (it holds the water until it sees
        /// _RGWaterHeld, a frame later) is not the material's own: the theme's is given back instead.
        /// </summary>
        private bool Take()
        {
            if (!Singleton<TerrainManager>.exists)
                return false;

            TerrainManager terrain = Singleton<TerrainManager>.instance;
            if (_water != null && _water[0] == terrain.m_waterMaterial && _water[1] == terrain.m_waterTransparentMaterial)
                return true;

            GiveBack();
            _water = new[] { terrain.m_waterMaterial, terrain.m_waterTransparentMaterial };
            _originalCube = new Texture[2];
            _originalColour = new Color?[2];
            _originalUnder = new Color?[2];
            Color themeColour = Shader.GetGlobalColor(IdColour);
            Color themeUnder = Shader.GetGlobalColor(IdUnder);
            for (int i = 0; i < 2; i++)
            {
                Material m = _water[i];
                if (m == null)
                    continue;
                if (m.HasProperty(IdCube))
                {
                    Texture t = m.GetTexture(IdCube);
                    if (t != null && !t.name.StartsWith("RealLight", StringComparison.Ordinal))
                        _originalCube[i] = t;
                }
                if (m.HasProperty(IdColour))
                {
                    Color c = m.GetColor(IdColour);
                    if (c == themeColour)
                        _originalColour[i] = c;
                }
                if (m.HasProperty(IdUnder))
                {
                    Color c = m.GetColor(IdUnder);
                    if (c == themeUnder)
                        _originalUnder[i] = c;
                }
            }

            // Decided once per pair of materials (a level's): what they showed when FIRST seen. A later
            // take finds the copies a release left and must not take them for the materials' own.
            if (_followed == null || _followed[0] != _water[0] || _followed[1] != _water[1])
            {
                _followed = new[] { _water[0], _water[1] };
                _followCube = new[] { _originalCube[0] == null, _originalCube[1] == null };
                _followColour = new[] { !_originalColour[0].HasValue, !_originalColour[1].HasValue };
                _followUnder = new[] { !_originalUnder[0].HasValue, !_originalUnder[1].HasValue };
                _underTouched = false;
            }

            return true;
        }

        public void Destroy()
        {
            GiveBack();
            UnityEngine.Object.Destroy(_object);
            _cube.Release();
            UnityEngine.Object.Destroy(_cube);
            _black.Release();
            UnityEngine.Object.Destroy(_black);
            UnityEngine.Object.Destroy(_material);
        }
    }
}
