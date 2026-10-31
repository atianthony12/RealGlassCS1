using System;
using ColossalFramework;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Water
{
    /// <summary>
    /// The view from under the water (RealGlass/WaterUnder; docs/UNDERWATER-PLAN.md): two command buffers
    /// on the game's camera while the camera is at or below the water's level -- at AfterFinalPass a copy
    /// of the scene's depth, at BeforeImageEffects a copy of the frame and the pass over it on a cube round
    /// the camera -- and, for the light shafts, one on the sun's Light at LightEvent.AfterShadowMap.
    /// Detached the rest of the time: above the water nothing is copied, grabbed or drawn.
    /// </summary>
    /// <remarks>
    /// - Only camera mods take the camera under: the game's own keeps it 2 m over the water
    ///   (CameraController.CalculateCameraHeightOffset). ACME and First Person Camera move the game's
    ///   camera (both reference CameraController and its camera), which <see cref="GameCamera"/> finds.
    /// - The water's level at the camera: TerrainManager.SampleRawHeightSmoothWithWater, the game
    ///   camera's own call (the simulation's two frames blended as the water is drawn), against the
    ///   terrain alone (SampleRawHeightSmooth): no water there, or the camera under the ground, is no
    ///   view from under the water.
    /// - Per pixel the shader decides: under where the near plane's point is below the level, so a
    ///   camera half in the water gets the waterline across the frame.
    /// - Before the image effects: after the fog, the glass and the transparents; bloom and the tone
    ///   mapping come after, as for everything else.
    /// - THE DEPTH (2026-10-30): its own copy of the camera's depth texture, taken by a second buffer at
    ///   AfterFinalPass -- before the forward water and every image effect, where the city reflection's
    ///   copy is taken (WaterSurfaceMask) -- and held as a temporary texture to the pass. Until then it read
    ///   the global _CameraDepthTexture before the image effects, and in the author's game that read far
    ///   too near: every pixel took the frame as drawn, the city above sharp through the surface, no haze.
    /// - The game draws no water seen from below (Cull Back), nor the mod's layers on it (WaterSky,
    ///   WaterFoam: Cull Back): the surface from below is this pass's alone. The city reflection adds
    ///   nothing while the eye itself is under the level; with the eye over it -- this pass on only for
    ///   the near plane dipping in -- the water seen from above keeps the city in it (WaterController).
    /// - THE SURFACE FROM BELOW (2026-10-30, the author's call: the view looking up fell short; the
    ///   shader's header has the model): the world above through Snell's window reads the frame where it is on
    ///   screen and the sky on the water's live cube where it is not, and the ripples are as rough as the
    ///   sky layer averages them from above (<see cref="WaterSkyLayer.ShareRipples"/>); the sun through
    ///   the waves takes its light from the game's.
    /// - THE SHAFTS read the game's own shadow map of the sun (CameraController.LateUpdate gives its
    ///   main light the shadows of the graphics options): right after the light renders it, the map is
    ///   bound for the frame and its cascades' constants are written into a 24 x 1 texture (the
    ///   shader's pass 1), as Unity sets them for the light's own shadows -- probed in the Unity 5.6
    ///   editor (Direct3D 11, OpenGL Core; legacy deferred, four cascades): the shadowed and the sunlit
    ///   point told apart from a later camera pass, the frame byte for byte the same with the capture.
    ///   Not on Metal (not probed there): the sun's light then reaches all the water.
    /// </remarks>
    public sealed class WaterUnder
    {
        private static readonly int IdUnder = Shader.PropertyToID("_RGUnder");
        private static readonly int IdC = Shader.PropertyToID("_RGUnderC");
        private static readonly int IdK = Shader.PropertyToID("_RGUnderK");
        private static readonly int IdVeil = Shader.PropertyToID("_RGUnderVeil");
        private static readonly int IdSun = Shader.PropertyToID("_RGUnderSun");
        private static readonly int IdShafts = Shader.PropertyToID("_RGUnderShafts");
        private static readonly int IdRipples = Shader.PropertyToID("_RGRipples");
        private static readonly int IdFrame = Shader.PropertyToID("_RGUnderFrame");
        private static readonly int IdShadow = Shader.PropertyToID("_RGSunShadow");
        private static readonly int IdShadowData = Shader.PropertyToID("_RGSunShadowData");
        private static readonly int IdSky = Shader.PropertyToID("_RGUnderSky");
        private static readonly int IdSunLight = Shader.PropertyToID("_RGUnderSunLight");
        private static readonly int IdDepth = Shader.PropertyToID("_RGUnderDepth");

        /// <summary>The shader's pass that writes the sun's cascade constants.</summary>
        private const int PassShadowData = 1;

        /// <summary>The shader's pass that copies the camera's depth texture.</summary>
        private const int PassSceneDepth = 2;

        /// <summary>The ripples' slope per decoded unit of their texture: the game shades still water at 0.5 x 0.5 (WaterGame.cginc).</summary>
        private const float RippleSlope = 0.25f;

        /// <summary>Less water than this over the terrain (m) is none.</summary>
        private const float LeastDepth = 0.05f;

        /// <summary>The shafts' steps along a ray, and how far they reach: this over c at green (the veil 99.75% made).</summary>
        private const float ShaftSteps = 24f;

        private const float ShaftReach = 6f;

        /// <summary>How high over the surface a sunbeam's entry is tested (m): the water itself never shades it.</summary>
        private const float ShaftLift = 1f;

        /// <summary>The shafts read the sun's shadow map: probed on Direct3D 11 and OpenGL Core only.</summary>
        public static readonly bool ShaftsSupported = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;

        private readonly Material _material;
        private readonly Material _capture;
        private readonly Mesh _cube;
        private readonly CommandBuffer _buffer;
        private readonly CommandBuffer _depthBuffer;
        private readonly CommandBuffer _shadowBuffer;
        private readonly RenderTexture _shadowData;
        private Camera _camera;
        private Light _sun;
        private bool _addedDepth;
        private bool _loggedOnce;
        private string _shaftState = "";
        private string _skyState = "";

        private WaterUnder(Shader shader)
        {
            _material = new Material(shader) { name = "RealGlass under the water", hideFlags = HideFlags.HideAndDontSave };
            _capture = new Material(shader) { name = "RealGlass the sun's cascades", hideFlags = HideFlags.HideAndDontSave };
            _cube = WaterCityReflection.Cube("RealGlassUnderCube");
            // The scene's depth, copied right after the deferred final pass (THE DEPTH, above); held as a
            // temporary texture to the pass before the image effects, which lets it go.
            _depthBuffer = new CommandBuffer { name = "RealGlass the scene's depth for under the water" };
            _depthBuffer.GetTemporaryRT(IdDepth, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            _depthBuffer.Blit(BuiltinRenderTextureType.CameraTarget, IdDepth, _material, PassSceneDepth);
            _buffer = new CommandBuffer { name = "RealGlass under the water" };
            _buffer.GetTemporaryRT(IdFrame, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            _buffer.Blit(BuiltinRenderTextureType.CameraTarget, IdFrame);
            _buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _buffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 0);
            _buffer.ReleaseTemporaryRT(IdFrame);
            _buffer.ReleaseTemporaryRT(IdDepth);

            // Zeros until the sun's first capture: no cascade holds any point (their radii 0), all sunlit.
            _shadowData = new RenderTexture(24, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "RealGlassSunCascades",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _shadowData.Create();
            Graphics.Blit(Texture2D.blackTexture, _shadowData);
            _material.SetTexture(IdShadowData, _shadowData);
            _shadowBuffer = new CommandBuffer { name = "RealGlass the sun's shadow map for the water" };
            _shadowBuffer.SetGlobalTexture(IdShadow, new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive));
            _shadowBuffer.Blit(BuiltinRenderTextureType.CurrentActive, _shadowData, _capture, PassShadowData);
        }

        /// <summary>Null when the shader is missing or unsupported here (logged by ShaderBundle): no view from under the water.</summary>
        public static WaterUnder Create()
        {
            Shader shader = ShaderBundle.Get(ShaderBundle.WaterUnder);
            if (shader == null || !shader.isSupported)
                return null;
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
            {
                Log.Warn("water: float render textures are unsupported; no view from under the water");
                return null;
            }
            return new WaterUnder(shader);
        }

        public bool Attached
        {
            get { return _camera != null; }
        }

        /// <summary>
        /// The water's level at the camera (m), when the camera is in it or close enough over it that its
        /// near plane may dip in; else false (no water there, the camera under the ground or well above).
        /// </summary>
        public static bool Below(Camera camera, out float level)
        {
            level = float.NaN;
            if (camera == null || !Singleton<TerrainManager>.exists)
                return false;

            TerrainManager terrain = Singleton<TerrainManager>.instance;
            Vector3 p = camera.transform.position;
            float surface = terrain.SampleRawHeightSmoothWithWater(p, true, 0f);
            float ground = terrain.SampleRawHeightSmooth(p);
            if (float.IsNaN(surface) || float.IsInfinity(surface) || surface < ground + LeastDepth || p.y < ground - 0.5f)
                return false;

            // A near plane's corner lies at most 1.3 x its distance from the camera (a 45 deg view, 16:9).
            if (p.y > surface + 1.3f * Mathf.Max(camera.nearClipPlane, 0.01f) + 0.05f)
                return false;

            level = surface;
            return true;
        }

        /// <summary>On the camera this frame (or kept there).</summary>
        /// <param name="level">The water's level at the camera (m).</param>
        /// <param name="vectors">The optics: <see cref="WaterUnderOptics.Vectors"/>.</param>
        /// <param name="caustics">The caustics' strength (0 none, 1 real water's).</param>
        /// <param name="ripples">The ripple texture the water is shaded with (null: a flat surface, no caustics).</param>
        /// <param name="shafts">The Light shafts row: the sun's light only where its shadows let it in.</param>
        /// <param name="sun">The sun's Light (the game's main light, which casts the shadows).</param>
        /// <param name="sunColour">The sun's light (linear: colour x intensity; black while its Light is off).</param>
        /// <param name="sky">The sky on the water, when it draws this frame (its live cube and its ripples' averaging), else null.</param>
        /// <param name="describe">The settings, for the first line in the log.</param>
        public void Show(Camera camera, float level, double[] vectors, float caustics, Texture ripples, bool shafts, Light sun, Color sunColour,
                         WaterSkyLayer sky, string describe)
        {
            if (camera != _camera)
            {
                Detach(false);
                _addedDepth = false;
                camera.AddCommandBuffer(CameraEvent.AfterFinalPass, _depthBuffer);
                camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _buffer);
                _camera = camera;
                string line = "water: under the water on '" + camera.name + "' (level " + level.ToString("F1") + " m, camera at " +
                              camera.transform.position.y.ToString("F1") + " m): the scene's depth copied after the final pass, a copy of the frame and one pass before the image effects; " + describe;
                if (!_loggedOnce)
                    Log.Msg(line);
                else if (Log.Detailed)
                    Log.Detail(line);
                _loggedOnce = true;
            }

            // The glass and the city reflection may give the depth texture back when they go off.
            if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
                _addedDepth = true;
            }

            bool shadowed = Shafts(shafts, sun);

            bool waves = ripples != null;
            if (waves)
                _material.SetTexture(IdRipples, ripples);
            _material.SetVector(IdUnder, new Vector4(level, waves ? Mathf.Max(0f, caustics) : 0f, waves ? RippleSlope : 0f, 0f));
            _material.SetVector(IdSunLight, new Vector4(Mathf.Max(0f, sunColour.r), Mathf.Max(0f, sunColour.g), Mathf.Max(0f, sunColour.b), 0f));
            SkyAbove(sky);
            _material.SetVector(IdC, new Vector4((float)vectors[0], (float)vectors[1], (float)vectors[2], 0f));
            _material.SetVector(IdK, new Vector4((float)vectors[4], (float)vectors[5], (float)vectors[6], 0f));
            _material.SetVector(IdVeil, new Vector4((float)vectors[8], (float)vectors[9], (float)vectors[10], 0f));
            _material.SetVector(IdSun, new Vector4((float)vectors[12], (float)vectors[13], (float)vectors[14], (float)vectors[15]));
            _material.SetVector(IdShafts, new Vector4(shadowed ? 1f : 0f, ShaftSteps, ShaftReach / Mathf.Max((float)vectors[1], 1e-3f), ShaftLift));
        }

        /// <summary>
        /// The world above the surface from below: the live sky where the frame has none (the sky on the
        /// water's cube), the facets as rough as the sky layer averages them -- or, with that layer not
        /// drawn (the Real sky row off, ...), the frame alone, held at the screen's edge, and a rim as
        /// sharp as the texture. One line when that changes.
        /// </summary>
        private void SkyAbove(WaterSkyLayer sky)
        {
            bool live = sky != null && sky.Cube != null;
            if (live)
            {
                sky.ShareRipples(_material);
                _material.SetVector(IdSky, new Vector4(1f, WaterSkyLayer.CubeSize, sky.CubeLastMip, 0f));
            }
            else
            {
                WaterSkyLayer.ClearRipples(_material);
                _material.SetVector(IdSky, Vector4.zero);
            }

            string state = live ? "the live sky above it and its ripples averaged as from above"
                                : "only what the frame shows above it (the sky on the water is not drawn)";
            if (state == _skyState)
                return;
            _skyState = state;
            Log.Msg("water: the surface from below shows " + state);
        }

        /// <summary>
        /// The sun's shadow map grabbed for the shafts this frame (the buffer on its Light, moved when the
        /// light changes), or not: the row off, Metal, no sun, or no shadows from it. One line when that changes.
        /// </summary>
        private bool Shafts(bool wanted, Light sun)
        {
            string why = !wanted ? "switched off"
                : !ShaftsSupported ? "not on " + SystemInfo.graphicsDeviceType
                : sun == null || !sun.isActiveAndEnabled ? "no sun"
                : sun.shadows == LightShadows.None || QualitySettings.shadows == ShadowQuality.Disable || QualitySettings.shadowDistance <= 0f ? "the sun casts no shadows (the graphics options)"
                : null;
            if (why != null)
            {
                ReleaseSun();
                ShaftState("off (" + why + ")");
                return false;
            }

            if (sun != _sun)
            {
                ReleaseSun();
                sun.AddCommandBuffer(LightEvent.AfterShadowMap, _shadowBuffer);
                _sun = sun;
            }
            ShaftState("on: the sun's own shadow map (" + QualitySettings.shadowCascades + " cascades over " + QualitySettings.shadowDistance.ToString("F0") + " m)");
            return true;
        }

        private void ShaftState(string state)
        {
            // The cascades' distance follows the camera's height (CameraController.UpdateTransform): not a state of its own.
            string kind = state.StartsWith("on", StringComparison.Ordinal) ? "on" : state;
            if (kind == _shaftState)
                return;
            _shaftState = kind;
            Log.Msg("water: light shafts under the water " + state);
        }

        private void ReleaseSun()
        {
            if (_sun == null)
                return;
            try
            {
                _sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, _shadowBuffer);
            }
            catch (Exception)
            {
                // A light destroyed with the city takes its buffers with it.
            }
            _sun = null;
        }

        /// <summary>Off the camera and the sun; the depth texture given back when this asked for it and nothing else of ours wants it.</summary>
        public void Detach(bool othersWantDepth)
        {
            ReleaseSun();
            if (_camera == null)
                return;

            try
            {
                _camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, _depthBuffer);
                _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _buffer);
                if (_addedDepth && !othersWantDepth)
                    _camera.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            catch (Exception)
            {
                // A camera destroyed with the city takes its buffers with it.
            }

            _camera = null;
            _addedDepth = false;
            if (Log.Detailed)
                Log.Detail("water: out of the water");
        }

        public void Destroy()
        {
            Detach(true);
            _buffer.Release();
            _depthBuffer.Release();
            _shadowBuffer.Release();
            _shadowData.Release();
            UnityEngine.Object.Destroy(_shadowData);
            UnityEngine.Object.Destroy(_material);
            UnityEngine.Object.Destroy(_capture);
            UnityEngine.Object.Destroy(_cube);
        }
    }
}
