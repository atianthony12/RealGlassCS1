using System;
using ColossalFramework;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Water
{
    /// <summary>
    /// The city in the map's water (RealGlass/CityReflection; ported from RealLight's
    /// CityReflectionOverlay, 2026-10-03): one command buffer on the main camera at
    /// CameraEvent.AfterImageEffectsOpaque -- a half-size copy of the frame (what the reflected rays
    /// meet), the reflection traced into a half-size buffer, streaked up and down by a Gaussian, then
    /// added over the water. Day and night.
    /// </summary>
    /// <remarks>
    /// - WHERE: the game's visible water meshes, recorded with their simulation heights in a separate
    ///   depth buffer. The seabed in the scene depth never supplies the reflection's surface position.
    /// - THE RAY: followed through the camera's depth texture as it stood before the forward water
    ///   (<see cref="WaterSurfaceMask.SceneDepth"/>; McGuire &amp; Mara 2014), 96 steps
    ///   growing geometrically to the camera's far plane x 0.9 (at most <see cref="WaterOptics.MaxDistance"/>),
    ///   the crossing refined by bisection; only what stands above the level counts as a hit.
    /// - HOW MUCH: rho at the view's angle (<see cref="WaterOptics.BuildTable"/>) x the strength, by
    ///   day or by night: faint from above, strong across the water.
    /// - WHAT IT REPLACES: where the ray meets the city, the city takes the place of the sky the
    ///   water shows: rho x strength x city is added, and the sky's light -- rho x min(strength, 1) x
    ///   skyShare x that sky's brightness x the share of the pixel the city covers -- leaves the pixel
    ///   AS A SHARE OF THE PIXEL, never as a colour (2.0.0: a mean sky's blue taken from a ripple
    ///   showing a paler sky left orange under dark trees). The share covered travels beside the light
    ///   in buffers of its own (the second accumulated / history / streaked: passes 4 and 1). Above
    ///   100% the strength brightens the city, never the sky it replaces (until 2026-11-04 it scaled
    ///   both: dark and red at 300%); below, both fade together. skyShare the Sky slider
    ///   while our sky is on the water (0 otherwise); that sky the live sky the sky layer draws
    ///   (<see cref="WaterSkyLayer"/>; light 1), or the theme's x its light under our cube.
    ///   THE ONE MIRROR (2.0.2): while the sky layer draws, nothing is taken out here any more -- the layer
    ///   leaves out the share of the sky this frame's reflection covers (traced before the water: the
    ///   camera-pan fix; until then last frame's, reprojected), and the composite adds the city's light at
    ///   that place. The estimate above is what is left for the water under our cube (the sky layer not drawn).
    /// - THE WAVES: the ray leaves the ripples' surface at the water's point, not the flat sea: the
    ///   ripple texture the water is shaded with (<see cref="WaterRipples"/>), read as the game reads
    ///   it, its slopes x <see cref="WaterOptics.CityRippleGain"/> (the Waves slider). 0: the flat
    ///   sea's mirror, exactly.
    /// - HOW LONG: the ray's slope is divided by the Length slider (<see cref="WaterOptics.MirrorSlope"/>);
    ///   the sky it replaces is still the flat sea's mirror.
    /// Three Unity 5.6 facts from RealLight's editor test: CommandBuffer.Blit binds its source only
    /// when the shader declares _MainTex in Properties; _MainTex_TexelSize read 0 (the texel size is a
    /// uniform); the streaked buffer must be bound with the buffer's own SetGlobalTexture.
    /// </remarks>
    public sealed class WaterCityReflection
    {
        private static readonly int IdFrame = Shader.PropertyToID("_RGWaterFrame");
        private static readonly int IdGeom = Shader.PropertyToID("_RGWaterGeom");
        private static readonly int IdMarch = Shader.PropertyToID("_RGWaterMarch");
        private static readonly int IdStreak = Shader.PropertyToID("_RGWaterStreak");
        private static readonly int IdStreaked = Shader.PropertyToID("_RGWaterStreaked");
        private static readonly int IdWave = Shader.PropertyToID("_RGWaterWave");
        private static readonly int IdRipples = Shader.PropertyToID("_RGRipples");
        private static readonly int IdTheme = Shader.PropertyToID("_RGWaterTheme");
        private static readonly int IdLight = Shader.PropertyToID("_RGWaterLight");
        private static readonly int IdDepth = Shader.PropertyToID("_CameraDepthTexture");

        private readonly Material _material;
        private readonly Mesh _cube;
        private readonly CommandBuffer _buffer;
        private readonly WaterSurfaceMask _surface;
        private static WaterCityReflection s_active;
        private Material _gameWater;
        private bool _surfaceFailed;
        private static readonly int IdTraced = Shader.PropertyToID("_RGWaterTraced");
        private static readonly int IdHistory = Shader.PropertyToID("_RGWaterHistory");
        private static readonly int IdHistory2 = Shader.PropertyToID("_RGWaterHistory2");
        private static readonly int IdStreaked2 = Shader.PropertyToID("_RGWaterStreaked2");
        private static readonly int IdPrevVP = Shader.PropertyToID("_RGWaterPrevVP");
        private static readonly int IdHistoryInfo = Shader.PropertyToID("_RGWaterHistoryInfo");

        /// <summary>How much of last frame's reflection each frame keeps (the history pass, clamped to this frame's neighbourhood).</summary>
        private const float HistoryWeight = 0.75f;

        private RenderTexture _frame, _trace, _accum, _history, _streaked;
        // The share of each water texel the city covers, beside the light: the same three stages.
        private RenderTexture _accum2, _history2, _streaked2;
        // The camera-pan fix: the trace's second target (the facade cards' light) and the light gathered at its hits.
        private RenderTexture _traceCards, _gathered;
        private Matrix4x4 _lastVP;
        private int _historyFrames;
        // THE ONE MIRROR: the composite takes nothing away this frame (the sky layer leaves out the covered sky).
        private bool _mirrorOn;
        // The buffer before the water (AfterFinalPass, after the surface's): the trace and the coverage.
        private readonly CommandBuffer _preBuffer;
        private bool _preCullHooked;
        private static readonly int IdTracedCards = Shader.PropertyToID("_RGWaterTracedCards");
        private static readonly int IdGathered = Shader.PropertyToID("_RGWaterGathered");
        private static readonly int IdOneMirror = Shader.PropertyToID("_RGWaterOneMirror");
        private float _strength;
        private Camera _camera;
        /// <summary>The reflection's buffer is on <see cref="_camera"/>; without it only the surface is (<see cref="AttachSurface"/>).</summary>
        private bool _reflecting;
        // The size of the surface's depth copies as last logged, and whether it was not the screen's.
        private int _depthWidth, _depthHeight;
        private bool _depthScaled;
        private int _refusedWidth, _refusedHeight;
        private bool _addedDepth;
        private bool _loggedWater;

        private WaterCityReflection(Shader shader, Shader surfaceShader)
        {
            _material = new Material(shader) { name = "RealGlass city reflection", hideFlags = HideFlags.HideAndDontSave };
            _cube = Cube("RealGlassWaterCube");
            _buffer = new CommandBuffer { name = "RealGlass water reflection" };
            _preBuffer = new CommandBuffer { name = "RealGlass water reflection, before the water" };
            _surface = new WaterSurfaceMask(surfaceShader);
        }

        /// <summary>Null when the shader is missing or unsupported here (logged by ShaderBundle): the water reflects no city.</summary>
        public static WaterCityReflection Create()
        {
            Shader shader = ShaderBundle.Get(ShaderBundle.CityReflection);
            Shader surface = ShaderBundle.Get(ShaderBundle.WaterSurfaceMask);
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
            {
                Log.Warn("water: surface depth textures are unsupported; city reflections are disabled");
                return null;
            }
            return shader != null && shader.isSupported && surface != null && surface.isSupported ? new WaterCityReflection(shader, surface) : null;
        }

        /// <summary>Called by the shared water draw hook, even with foam switched off.</summary>
        public static void DrawSurface(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera,
                                       int submesh, MaterialPropertyBlock properties)
        {
            WaterCityReflection active = s_active;
            if (active == null || active._surfaceFailed || (object)material == null || (object)material != (object)active._gameWater)
                return;
            try
            {
                active._surface.Draw(mesh, matrix, layer, camera, submesh, properties);
            }
            catch (Exception e)
            {
                active._surfaceFailed = true;
                // The sky layer stops leaving the sky out at once: nothing would put the city in its place.
                WaterSkyLayer.DropOneMirror();
                active._mirrorOn = false;
                active.Detach();
                Log.Error("water: surface capture failed; city reflections are disabled until the city reloads", e);
            }
        }

        public bool Attached
        {
            get { return _camera != null; }
        }

        /// <summary>
        /// The facade cards (a trial): the trace (pass 0) compiled with the cards' keyword, set on this
        /// material only (Glass/FacadeCardsGame decides). False takes it off.
        /// </summary>
        public void SetCards(bool on)
        {
            if (_material == null)
                return;
            if (on)
                _material.EnableKeyword(RealGlass.Glass.FacadeCards.Keyword);
            else
                _material.DisableKeyword(RealGlass.Glass.FacadeCards.Keyword);
        }

        /// <summary>The scene's raw depth before the forward water, while attached (else null or stale).</summary>
        public RenderTexture SceneDepth
        {
            get { return _camera != null && !_surfaceFailed ? _surface.SceneDepth : null; }
        }

        /// <summary>The frame before the forward water (the realistic depth's seabed), while attached and asked for.</summary>
        public RenderTexture BedFrame
        {
            get { return _camera != null && !_surfaceFailed ? _surface.BedFrame : null; }
        }

        /// <summary>Whether the surface's buffer copies the frame too (<see cref="BedFrame"/>), from the next attach.</summary>
        public void WantBed(bool want)
        {
            _surface.BedWanted = want;
        }

        /// <summary>A unit cube round the origin, drawn round the camera (the vertex shader puts it at the far plane; WaterUnder's too).</summary>
        internal static Mesh Cube(string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, // -z
                4, 5, 6, 4, 6, 7, // +z
                0, 1, 5, 0, 5, 4, // -y
                3, 7, 6, 3, 6, 2, // +y
                0, 4, 7, 0, 7, 3, // -x
                1, 2, 6, 1, 6, 5, // +x
            };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        private static int Half(int pixels)
        {
            return Mathf.Max(1, pixels / 2);
        }

        private static RenderTexture Target(string name, int width, int height, bool mips)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = mips,
                autoGenerateMips = mips,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }

        /// <summary>
        /// The two buffers (the camera-pan fix: one frame, no lag). BEFORE THE WATER (AfterFinalPass, after the
        /// water's surface is recorded there): the trace's rays and the share each texel covers (pass 0, into a
        /// float target of hits + the facade cards' light), that share's history and streaks (4, 1) ->
        /// _RGWaterStreaked2, which the sky layer reads in this same render to leave out the covered sky. AFTER THE
        /// FOG (AfterImageEffectsOpaque): a half-size copy of the frame, the city's light at the trace's hits in it
        /// (5) -- the frame as drawn, the mod's glass and the game's fog in it -- its history and streaks (3, 1),
        /// then onto the water (2). (Until 2.0.2 one buffer after the fog; the sky layer read the coverage a frame
        /// old, reprojected.)
        /// </summary>
        private void Record(int width, int height)
        {
            _buffer.Clear();
            _preBuffer.Clear();
            Free(ref _frame);
            Free(ref _trace);
            Free(ref _traceCards);
            Free(ref _gathered);
            Free(ref _accum);
            Free(ref _history);
            Free(ref _streaked);
            Free(ref _accum2);
            Free(ref _history2);
            Free(ref _streaked2);

            int w = Half(width), h = Half(height);
            _frame = Target("RealGlassWaterFrame", w, h, true);
            // The hits' screen place in a float target (half precision is a texel off near uv 1 at 4K).
            _trace = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "RealGlassWaterTrace",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _trace.Create();
            _traceCards = Target("RealGlassWaterTraceCards", w, h, false);
            _gathered = Target("RealGlassWaterGathered", w, h, false);
            _accum = Target("RealGlassWaterAccumulated", w, h, true);
            _history = Target("RealGlassWaterHistory", w, h, false);
            _streaked = Target("RealGlassWaterStreaked", w, h, false);
            _accum2 = Target("RealGlassWaterCovered", w, h, true);
            _history2 = Target("RealGlassWaterCoveredHistory", w, h, false);
            _streaked2 = Target("RealGlassWaterCoveredStreaked", w, h, false);
            _material.SetTexture(IdFrame, _frame);
            _material.SetTexture(IdTraced, _trace);
            _material.SetTexture(IdTracedCards, _traceCards);
            _material.SetTexture(IdGathered, _gathered);
            _material.SetTexture(IdHistory, _history);
            _material.SetTexture(IdHistory2, _history2);
            _historyFrames = 0;

            // Before the water: the rays and the coverage.
            _preBuffer.SetRenderTarget(new RenderTargetIdentifier[] { _trace, _traceCards }, _trace);
            _preBuffer.ClearRenderTarget(false, true, Color.clear);
            _preBuffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 0);
            _preBuffer.SetRenderTarget(_accum2);
            _preBuffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 4);
            _preBuffer.Blit(_accum2, _history2);
            _preBuffer.Blit(_accum2, _streaked2, _material, 1);
            _preBuffer.SetGlobalTexture(IdStreaked2, _streaked2);
            _preBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);

            // After the fog: the light at the hits, its history (against the flicker), onto the water.
            _buffer.Blit(BuiltinRenderTextureType.CameraTarget, _frame);
            _buffer.SetRenderTarget(_gathered);
            _buffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 5);
            _buffer.SetRenderTarget(_accum);
            _buffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 3);
            _buffer.Blit(_accum, _history);
            _buffer.Blit(_accum, _streaked, _material, 1);
            _buffer.SetGlobalTexture(IdStreaked, _streaked);
            _buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _buffer.DrawMesh(_cube, Matrix4x4.identity, _material, 0, 2);
        }

        /// <summary>On the camera (or kept there, its buffers re-made on a resize).</summary>
        public void Attach(Camera camera)
        {
            Attach(camera, true);
        }

        /// <summary>
        /// Only the water's surface on the camera -- recorded and put into the depth texture
        /// (<see cref="WaterSurfaceMask"/>) -- and no reflection: the City in the water row off. The tall
        /// waves stay solid against the sky, the fog and the clouds (the author's call, 2026-10-31).
        /// </summary>
        public void AttachSurface(Camera camera)
        {
            Attach(camera, false);
        }

        private void Attach(Camera camera, bool reflect)
        {
            if (camera == null || _surfaceFailed)
            {
                Detach();
                return;
            }

            if (camera == _camera)
            {
                PrepareSurface(camera);
                // The glass gives the depth texture back when it goes off, also when it was ours first.
                if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
                {
                    camera.depthTextureMode |= DepthTextureMode.Depth;
                    _addedDepth = true;
                }
            }
            else
            {
                Detach();
                _addedDepth = (camera.depthTextureMode & DepthTextureMode.Depth) == 0;
                camera.depthTextureMode |= DepthTextureMode.Depth;
                PrepareSurface(camera);
                _camera = camera;
                s_active = this;
                Log.Msg((WaterSurfaceMask.MergesDepth
                    ? "water: its surface put into the depth texture right after it is drawn: the fog, the clouds and the effects after them see the water, not what lies behind it"
                    : "water: the depth texture holds the water by itself here (" + SystemInfo.graphicsDeviceType + "): nothing merged") +
                    (_addedDepth ? " (depth texture asked for)" : ""));
                LogWater();
                if (!WaterFoam.Hooked)
                    Log.Warn("water: surface capture requires both water draw hooks; found " + WaterFoam.Hooks());
            }

            if (reflect != _reflecting)
            {
                _reflecting = reflect;
                if (reflect)
                {
                    Record(camera.pixelWidth, camera.pixelHeight);
                    // After the water's own buffers at the final pass (the surface's: added by PrepareSurface,
                    // before this), so the trace sees this render's surface; buffers run in the order added.
                    camera.AddCommandBuffer(CameraEvent.AfterFinalPass, _preBuffer);
                    camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _buffer);
                    HookPreCull(true);
                    Log.Msg("water: the city is reflected on the captured water surface on '" + camera.name + "' (" + _frame.width + " x " + _frame.height +
                            ": the rays traced at half size off the waves before the water, at the final pass -- the sky layer leaves out this frame's " +
                            "covered sky -- and the city's light taken at their hits after the fog, streaked, then onto the water)");
                }
                else
                {
                    camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, _preBuffer);
                    camera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _buffer);
                    HookPreCull(false);
                    _historyFrames = 0;
                    OneMirror(false);
                    Log.Msg("water: the city reflection is off; the water's surface still goes into the depth texture");
                }
            }

            if (!_reflecting)
                return;
            if (_frame == null || _frame.width != Half(camera.pixelWidth) || _frame.height != Half(camera.pixelHeight))
                Record(camera.pixelWidth, camera.pixelHeight);
        }

        private void HookPreCull(bool on)
        {
            if (on == _preCullHooked)
                return;
            if (on)
                Camera.onPreCull += BeforeRender;
            else
                Camera.onPreCull -= BeforeRender;
            _preCullHooked = on;
        }

        private void PrepareSurface(Camera camera)
        {
            _surface.Attach(camera);
            NoteSize(camera);
            _material.SetTexture("_RGWaterSurfaceDepth", _surface.Texture);
            // The scene as it stood before the forward water, in place of the camera's depth texture
            // (on Metal that holds the water itself by now: WaterSurfaceMask).
            _material.SetTexture(IdDepth, _surface.SceneDepth);
            _gameWater = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance.m_waterMaterial : null;
            _surface.SetMapping(_gameWater);
        }

        /// <summary>
        /// One line when the water's depth copies take a size that is not the screen's (a mod that renders
        /// the camera into its own target, such as a dynamic resolution one), and when they are back.
        /// </summary>
        private void NoteSize(Camera camera)
        {
            RenderTexture depth = _surface.Texture;
            if (_surface.RefusedWidth != _refusedWidth || _surface.RefusedHeight != _refusedHeight)
            {
                _refusedWidth = _surface.RefusedWidth;
                _refusedHeight = _surface.RefusedHeight;
                if (_refusedWidth > 0)
                    Log.Warn("water: its depth copies could not be made at the render's size (" + _refusedWidth + " x " + _refusedHeight +
                             "): they stay the screen's size, and thin lines can show round whatever stands against the far background");
            }
            if (depth == null || camera.targetTexture != null || (depth.width == _depthWidth && depth.height == _depthHeight))
                return;
            bool scaled = depth.width != camera.pixelWidth || depth.height != camera.pixelHeight;
            if (scaled)
                Log.Msg("water: the camera renders at " + depth.width + " x " + depth.height + ", the screen is " + camera.pixelWidth + " x " + camera.pixelHeight +
                        " (another mod renders it into its own target): the water's depth copies follow the render");
            else if (_depthScaled)
                Log.Msg("water: the camera renders at the screen's size again (" + depth.width + " x " + depth.height + ")");
            _depthWidth = depth.width;
            _depthHeight = depth.height;
            _depthScaled = scaled;
        }

        /// <summary>
        /// At each render of the reflection's camera (its onPreCull: the pose and the size it renders with, a
        /// dynamic resolution's target on it already): the history's view-projection -- the render before this
        /// one's (OpenGL convention, as the glass's reprojection) -- and its weight, none on the first render after
        /// the buffers are made. Per render, not in a LateUpdate (2.0.2 took it there: a camera moved after it,
        /// and the history slid by that much); with two renders a frame the second reads the first's history
        /// under the first's matrix. The streaks' texel height too, from the trace's size.
        /// </summary>
        private void BeforeRender(Camera camera)
        {
            if (camera == null || camera != _camera || !_reflecting || _trace == null)
                return;
            Matrix4x4 vp = camera.projectionMatrix * camera.worldToCameraMatrix;
            bool ok = _historyFrames > 0;
            _material.SetMatrix(IdPrevVP, ok ? _lastVP : vp);
            _material.SetVector(IdHistoryInfo, new Vector4(ok ? HistoryWeight : 0f, 1f / _trace.width, 1f / _trace.height, 0f));
            _lastVP = vp;
            _historyFrames++;
        }

        /// <summary>
        /// The city's reflection is on the game's camera this frame (not only its surface): the sky layer may
        /// leave out the sky it stands in for (<see cref="OneMirror"/>).
        /// </summary>
        public bool MirrorReady
        {
            get { return _camera != null && _reflecting && !_surfaceFailed; }
        }

        /// <summary>The city reflection's strength this frame (the layer leaves out the covered sky at most x 1).</summary>
        public float Strength
        {
            get { return _strength; }
        }

        /// <summary>The camera the reflection is on (null when detached).</summary>
        public Camera Camera
        {
            get { return _camera; }
        }

        /// <summary>
        /// The one mirror this frame (true: the composite only adds, the sky layer having left out the sky the
        /// city stands in for: <see cref="WaterSkyLayer.OneMirror"/>), or the composite's estimate of the sky (false).
        /// </summary>
        public void OneMirror(bool on)
        {
            _mirrorOn = on && MirrorReady;
            _material.SetVector(IdOneMirror, new Vector4(_mirrorOn ? 1f : 0f, 0f, 0f, 0f));
        }

        public void Detach()
        {
            if (s_active == this)
                s_active = null;
            _surface.Detach();
            _mirrorOn = false;
            _material.SetVector(IdOneMirror, Vector4.zero);
            HookPreCull(false);
            if (_camera == null)
                return;

            try
            {
                if (_reflecting)
                {
                    _camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, _preBuffer);
                    _camera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _buffer);
                }
                // Given back only when nothing else of ours wants it: the glass asks for it too.
                if (_addedDepth && !GlassWantsDepth())
                    _camera.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            catch (Exception)
            {
                // A camera destroyed with the city takes its buffers with it.
            }

            Log.Msg(_reflecting ? "water: the city reflection is off" : "water: the water's surface no longer goes into the depth texture");
            _camera = null;
            _reflecting = false;
            _addedDepth = false;
            _historyFrames = 0;
        }

        private static bool GlassWantsDepth()
        {
            return Glass.GlassController.Instance != null && Glass.GlassController.Instance.enabled &&
                   Settings.GlassEnabled != null && Settings.GlassEnabled.value;
        }

        /// <summary>This frame's uniforms.</summary>
        /// <param name="level">The water's level, world metres.</param>
        /// <param name="strength">The city reflection's strength now (day and night blended).</param>
        /// <param name="skyShare">The Sky slider while our cube is on the water, else 0.</param>
        /// <param name="theme">The theme's cube (what the water shows of the sky).</param>
        /// <param name="light">The light the game's water lights the cube with (linear): the sun on the level + the sky's ambient.</param>
        /// <param name="sigmaUp">The streaks' Gaussian up and down, uv (<see cref="WaterOptics.StreakSigma"/>).</param>
        /// <param name="ripples">The ripple texture the water is shaded with (null: the flat sea's mirror).</param>
        /// <param name="rippleGain">The gain on the ripples' slopes (<see cref="WaterOptics.CityRippleGain"/>; 0: the flat sea's mirror).</param>
        /// <param name="length">The Length slider: the reflection this many times as long as the city is tall (<see cref="WaterOptics.MirrorSlope"/>).</param>
        public void SetUniforms(float level, float strength, float skyShare, Texture theme, Color light, double sigmaUp, Texture ripples,
                                double rippleGain, float length, float farPlane)
        {
            float waves = ripples != null ? Mathf.Max(0f, (float)rippleGain) : 0f;
            if (ripples != null)
                _material.SetTexture(IdRipples, ripples);
            _strength = strength;
            float reach = Mathf.Min((float)WaterOptics.MaxDistance, 0.9f * farPlane);
            int themeSize = theme != null ? Mathf.Max(theme.width, 1) : 1;
            _material.SetVector(IdGeom, new Vector4(level, strength, theme != null ? skyShare : 0f, Mathf.Max(0f, Mathf.Log(themeSize / 64f, 2f))));
            _material.SetVector(IdMarch, new Vector4(reach, (float)WaterOptics.Thickness, (float)WaterOptics.EdgeFade, Mathf.Max(1f, length)));
            _material.SetVector(IdStreak, new Vector4((float)sigmaUp, _trace != null ? 1f / _trace.height : 0f, 0f, 0f));
            // Against TAA's flicker (measured, tools\preview\water-flicker.ps1, 2026-10-05): the ripples
            // read over 3 trace pixels (only waves the screen resolves bend the rays), the city read
            // as wide as a ray cone spreads (z = 1), the gain x 2 to keep the waviness. (8 pixels and
            // x 1.5 flickered least, but the author missed the night's waviness: "I really liked it
            // better before".) The ray cone is OFF since the history pass (2026-10-06): with both, the
            // author found the reflection "too blurry".
            _material.SetVector(IdWave, new Vector4(Mathf.Max(0f, waves) * 2f, 3f, 0f, 0f));
            if (theme != null)
                _material.SetTexture(IdTheme, theme);
            _material.SetVector(IdLight, new Vector4(light.r, light.g, light.b, 0f));
        }

        /// <summary>
        /// Once a city: the game's two water materials and their render queues -- the reflection is
        /// drawn after the opaque queue, so water drawn later (the transparent queue) would cover it.
        /// </summary>
        private void LogWater()
        {
            if (_loggedWater || !Singleton<TerrainManager>.exists)
                return;

            _loggedWater = true;
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            Log.Msg("water: the game's water -- transparent water " + terrain.TransparentWater +
                    "; default " + Describe(terrain.m_waterMaterial) + "; transparent " + Describe(terrain.m_waterTransparentMaterial));
        }

        private static string Describe(Material m)
        {
            if (m == null)
                return "none";
            Shader s = m.shader;
            return "'" + (s != null ? s.name : "no shader") + "' queue " + m.renderQueue +
                   (m.renderQueue > 2500 ? " (AFTER the city reflection: it covers it)" : "");
        }

        public void Destroy()
        {
            Detach();
            _buffer.Release();
            _preBuffer.Release();
            _surface.Destroy();
            Free(ref _frame);
            Free(ref _trace);
            Free(ref _traceCards);
            Free(ref _gathered);
            Free(ref _accum);
            Free(ref _history);
            Free(ref _streaked);
            Free(ref _accum2);
            Free(ref _history2);
            Free(ref _streaked2);
            UnityEngine.Object.Destroy(_material);
            UnityEngine.Object.Destroy(_cube);
        }
    }
}
