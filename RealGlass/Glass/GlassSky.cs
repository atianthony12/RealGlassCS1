using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// The sky the glass reflects when the screen has nothing: a small HDR cube of the game's OWN
    /// skybox (RenderSettings.skybox: its scattering sky, sun and all), rendered by a hidden camera
    /// that draws nothing else (culling mask 0, clear = skybox), a face or a few a frame -- the way
    /// Mirror's Edge Catalyst kept its reflection cubes in step with the time of day, and GTA V
    /// rendered its cubemap every frame -- with Volumetric Weather's clouds drawn into it when that
    /// mod draws them. Or the game's static environment map, if asked.
    /// For the glass (a util material given), between the skybox and the clouds: the sky's light at
    /// the sun's height (GlassUniforms.Dusk; the game's sky keeps noon's until the sun is 3 degrees
    /// up), and the cube's alpha cleared, so the clouds drawn over it leave their opacity there --
    /// how much of the sun's disc they hide (CloudAlpha). For the water (Fog given), the game's fog
    /// over the sky there instead, as the player sees it on screen.
    /// Engine types only: the GPU preview runs this same file.
    /// </summary>
    public sealed class GlassSky
    {
        /// <summary>
        /// Volumetric Weather's clouds: a mesh renderer in the scene (a box round the camera, its
        /// raymarch material), found by name.
        /// </summary>
        public const string CloudVolumeName = "VolumetricCloudsVolume";

        /// <summary>
        /// Volumetric Weather's distant clouds (its 1.5 sky layer: cirrus, sheep clouds, far clouds,
        /// layers of cloud, photographed): a child of the cloud box, the box mesh with a material of
        /// its own, enabled while any of them is above 0. Found by name.
        /// </summary>
        public const string DistantCloudsName = "VolumetricCloudsSkyLayer";

        /// <summary>GlassUtil's pass that sets the sky's light and clears alpha in a face (SkyBackground).</summary>
        public const int PassSkyBackground = 5;

        /// <summary>GlassUtil's pass that lays the game's fog over the sky in a face (SkyFog).</summary>
        public const int PassSkyFog = 7;

        /// <summary>
        /// GlassUtil, for the game's fog over the sky between the skybox and the clouds, as on screen
        /// (the water's sky); null: none (the glass's). <see cref="FogFar"/> is the main camera's far
        /// plane, where the game's fog puts a pixel of sky.
        /// </summary>
        public Material Fog;

        public float FogFar = 10000f;

        /// <summary>
        /// Volumetric Weather's lightning bolts drawn into the cube, after the sky and its fog and before the
        /// clouds, as on screen (2026-11-18, the author: "Lightning strikes should reflect in Real Glass's
        /// water"); while one shows, or a flash lights the clouds (any Volumetric Weather version), and for
        /// <see cref="LitTail"/> frames after, all six faces are rendered each frame. The water's sky only: the
        /// glass's cube alpha is the clouds' opacity, which an additive bolt would spoil.
        /// </summary>
        public bool Bolts;

        /// <summary>True once a flash in the clouds, with no bolt drawn, has had all six faces redrawn (for a log line).</summary>
        public bool FlashFollowed { get; private set; }

        /// <summary>How many bolts the last update drew into the cube.</summary>
        public int BoltsDrawn { get; private set; }

        /// <summary>"" until looked for; then "found" or why not (Volumetric Weather absent or older than its bolts API).</summary>
        public static string BoltsApi { get { return s_boltsApi; } }

        // Volumetric Weather's VolumetricClouds.Sky.LightningBolts (ApiVersion >= 1), read by reflection:
        // the mod is optional and this one references nothing of it.
        private static System.Func<CommandBuffer, int> s_drawBolts;
        private static System.Func<int> s_boltCount;
        private static string s_boltsApi = "";
        private static float s_nextBoltsSearch;
        private static System.Type s_boltsType;

        /// <summary>
        /// Frames all six faces are still redrawn after the last lightning (bolt or flash), so the cube
        /// goes dark again everywhere at once.
        /// </summary>
        public const int LitTail = 2;

        // Volumetric Weather's flashes in its clouds: a value on the cloud box's own material (CloudLightning sets
        // _FlashCount there each frame, 0 with none; its own Smoother clouds reads it back the same way).
        private static readonly int IdFlashCount = Shader.PropertyToID("_FlashCount");

        private int _litFrames;

        /// <summary>
        /// THE MATCHED MAP (the water's "Sky in the water: The game's own", 2026-11-20): RealGlass/WaterCube,
        /// drawn as a second hidden camera's skybox (its pass with _RGWaterGain.w = 1), and the map it draws
        /// (the global _EnvironmentCubemap: the theme's, or a cubemap mod's -- Lumina, Dynamic Cubemaps,
        /// Cubemap Replacer, which all swap that global). Both set: the live cube is still rendered, every
        /// feature as before, but only as the reference -- the cube bound (and <see cref="Cube"/>) is the
        /// map at the same size and mips, x the live sky's mean luminance over the upper sky / the map's
        /// (the shader's gain: a photograph follows the time of day and the weather; no read-back). The
        /// photograph's own clouds and sun are what it shows: Volumetric Weather's clouds and bolts reach
        /// it only through that brightness. Either null: the live cube is bound, as before.
        /// </summary>
        public Material Match;

        /// <summary>The map <see cref="Match"/> draws (set before each <see cref="Update"/>); null: none.</summary>
        public Texture MatchMap;

        /// <summary>True while the cube bound is the matched map.</summary>
        public bool Matching { get; private set; }

        private static readonly int IdMatchTheme = Shader.PropertyToID("_RGWaterTheme");
        private static readonly int IdMatchRef = Shader.PropertyToID("_RGWaterMatchRef");
        private static readonly int IdMatchGain = Shader.PropertyToID("_RGWaterGain");
        private static readonly int IdMatch = Shader.PropertyToID("_RGWaterMatch");

        private GameObject _matchHolder;
        private Camera _matchCamera;
        private RenderTexture _matched;
        private Texture _matchedMap;
        private int _matchedFaces;

        private GameObject _holder;
        private Camera _camera;
        private RenderTexture _cube;
        private int _size;
        private int _nextFace;
        private int _facesDone;

        private CommandBuffer _buffer;
        private MaterialPropertyBlock _cloudBlock;
        private MaterialPropertyBlock _backgroundBlock;
        private MaterialPropertyBlock _fogBlock;
        private Mesh _quad;
        private Mesh _box;
        private MeshRenderer _cloudRenderer;
        private MeshFilter _cloudFilter;
        private float _nextCloudSearch;
        private MeshRenderer _distantRenderer;
        private MeshFilter _distantFilter;
        private float _nextDistantSearch;

        /// <summary>Mip levels of the cube (0 when the game's map is used: its own count is unknown).</summary>
        public int Mips { get; private set; }

        /// <summary>Faces rendered since the cube was made (6 = complete).</summary>
        public int FacesDone { get { return _facesDone; } }

        /// <summary>True while the live sky carries the clouds.</summary>
        public bool DrawsClouds { get; private set; }

        /// <summary>True while the live sky carries Volumetric Weather's distant clouds.</summary>
        public bool DrawsDistantClouds { get; private set; }

        /// <summary>
        /// True while the cube's alpha is the opacity of the clouds in it: cleared after the skybox,
        /// then left by the clouds' premultiplied blend (1 - prod(1 - a)); 0 where there are none.
        /// </summary>
        public bool CloudAlpha { get; private set; }

        public string Problem = "";

        /// <summary>Why the matched map could not be drawn this frame ("" when it was, or none was asked for): the live cube is bound instead.</summary>
        public string MatchProblem = "";

        /// <summary>
        /// The global the cube is bound to: the glass's "_RGSky"; the water's sky layer keeps its own
        /// cube under another name (Water/WaterSkyLayer.cs).
        /// </summary>
        public string GlobalName = "_RGSky";

        /// <summary>
        /// The cube bound: the live one, or the matched map while <see cref="Matching"/> (null until the
        /// first update, or while the game's map is bound as it is).
        /// </summary>
        public Texture Cube { get { return Matching && _matched != null ? (Texture)_matched : _cube; } }

        /// <summary>
        /// live: our cube of the skybox at size x size per face, facesPerFrame faces refreshed now,
        /// with the clouds in it when clouds is set and there are clouds to draw (and the distant
        /// clouds when distantClouds is set too); else gameMap (the game's _EnvironmentCubemap) is
        /// bound as it is. util: GlassUtil, for the sky's light x dusk under the clouds and the
        /// alpha they leave (null: the skybox as it is, alpha undefined -- the water's sky).
        /// </summary>
        public void Update(bool live, int size, int facesPerFrame, Texture gameMap, Vector3 position, bool clouds,
                           bool distantClouds, Material util, float dusk)
        {
            if (!live || RenderSettings.skybox == null)
            {
                Problem = live ? "no skybox" : "";
                DrawsClouds = false;
                DrawsDistantClouds = false;
                CloudAlpha = false;
                Release();
                Mips = gameMap is Cubemap ? ((Cubemap)gameMap).mipmapCount : 1;
                if (gameMap != null)
                    Shader.SetGlobalTexture(GlobalName, gameMap);
                return;
            }

            size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(size), 16, 512);
            if (_cube == null || !_cube.IsCreated() || _size != size)
            {
                Release();
                _size = size;
                _cube = new RenderTexture(size, size, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                _cube.name = "RealGlass sky";
                _cube.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                _cube.useMipMap = true;
                _cube.autoGenerateMips = true;
                _cube.filterMode = FilterMode.Trilinear;
                _cube.Create();
                _nextFace = 0;
                _facesDone = 0;
                int mips = 1;
                for (int s = size; s > 1; s >>= 1)
                    mips++;
                Mips = mips;
            }

            if (_camera == null)
            {
                _holder = new GameObject("RealGlass sky camera");
                _holder.hideFlags = HideFlags.HideAndDontSave;
                _camera = _holder.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.cullingMask = 0;
                _camera.clearFlags = CameraClearFlags.Skybox;
                _camera.renderingPath = RenderingPath.Forward;
                _camera.allowHDR = true;
                _camera.allowMSAA = false;
                _camera.nearClipPlane = 1f;
                _camera.farClipPlane = 100000f;
            }

            _holder.transform.position = position;
            Record(clouds, clouds && distantClouds, util, dusk);

            // A new cube renders all six faces at once; after that, a few a frame -- all six while lightning
            // shows and for LitTail frames after (a strike's flicker lasts a few frames: a face a third of them
            // late would miss it). 2026-11-19, the author: the water's reflections "acting up" in a storm --
            // until then only a drawn BOLT asked for six, so a flash in the clouds with no bolt drawn (most of
            // a storm's light, and a bolt's own dark flicker frames) lit the water's sky two faces at a time
            // and left it lit on the faces not yet redrawn.
            bool flash = Bolts && BoltsDrawn == 0 && Flashing();
            if (flash)
                FlashFollowed = true;
            if (Bolts && (BoltsDrawn > 0 || flash))
                _litFrames = LitTail + 1;
            else if (_litFrames > 0)
                _litFrames--;
            int count = _facesDone < 6 || _litFrames > 0 ? 6 : Mathf.Clamp(facesPerFrame, 1, 6);
            int mask = 0;
            for (int i = 0; i < count; i++)
            {
                mask |= 1 << _nextFace;
                _nextFace = (_nextFace + 1) % 6;
            }

            if (!_camera.RenderToCubemap(_cube, mask))
            {
                Problem = "RenderToCubemap failed";
                return;
            }

            _facesDone = Mathf.Min(_facesDone + count, 6);
            Problem = "";
            if (Match != null && MatchMap != null)
            {
                if (!DrawMatched(mask))
                {
                    // The live sky instead, this frame: the water keeps its sky (a Problem would hide it).
                    Matching = false;
                    MatchProblem = "RenderToCubemap failed";
                    Shader.SetGlobalTexture(GlobalName, _cube);
                    return;
                }
                MatchProblem = "";
                Matching = true;
                Shader.SetGlobalTexture(GlobalName, _matched);
                return;
            }

            ReleaseMatched();
            Shader.SetGlobalTexture(GlobalName, _cube);
        }

        /// <summary>
        /// THE MATCHED MAP: the faces the live cube just refreshed (all six for a new cube or a new map), drawn
        /// by RealGlass/WaterCube as a hidden camera's skybox from the map and the live cube (its gain).
        /// </summary>
        private bool DrawMatched(int mask)
        {
            if (_matched == null || !_matched.IsCreated() || _matched.width != _size)
            {
                ReleaseMatched();
                _matched = new RenderTexture(_size, _size, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                _matched.name = "RealGlass sky (the game's map, matched)";
                _matched.hideFlags = HideFlags.HideAndDontSave;
                _matched.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                _matched.useMipMap = true;
                _matched.autoGenerateMips = true;
                _matched.filterMode = FilterMode.Trilinear;
                _matched.Create();
                _matchedFaces = 0;
            }

            if (_matchCamera == null)
            {
                _matchHolder = new GameObject("RealGlass matched sky camera");
                _matchHolder.hideFlags = HideFlags.HideAndDontSave;
                _matchCamera = _matchHolder.AddComponent<Camera>();
                _matchCamera.enabled = false;
                _matchCamera.cullingMask = 0;
                _matchCamera.clearFlags = CameraClearFlags.Skybox;
                _matchCamera.renderingPath = RenderingPath.Forward;
                _matchCamera.allowHDR = true;
                _matchCamera.allowMSAA = false;
                _matchCamera.useOcclusionCulling = false;
                _matchCamera.nearClipPlane = 0.3f;
                _matchCamera.farClipPlane = 1000f;
                _matchHolder.AddComponent<Skybox>().material = Match;
            }

            Skybox skybox = _matchHolder.GetComponent<Skybox>();
            if (skybox != null && skybox.material != Match)
                skybox.material = Match;

            if (MatchMap != _matchedMap)
            {
                _matchedMap = MatchMap;
                _matchedFaces = 0;
            }
            if (_matchedFaces < 6)
                mask = 63;

            // The map read at the mip whose texels are this cube's (no aliasing); both means at the mip of
            // about 4 x 4 texels a face (the live cube's mips run to 1 x 1).
            float mapSize = Mathf.Max(MatchMap.width, 1);
            Match.SetTexture(IdMatchTheme, MatchMap);
            Match.SetTexture(IdMatchRef, _cube);
            Match.SetVector(IdMatchGain, new Vector4(1f, 1f, Mathf.Max(0f, Mathf.Log(mapSize / _size, 2f)), 1f));
            Match.SetVector(IdMatch, new Vector4(Mathf.Max(0f, Mathf.Log(_size, 2f) - 2f), Mathf.Max(0f, Mathf.Log(mapSize, 2f) - 2f), 0f, 0f));

            if (!_matchCamera.RenderToCubemap(_matched, mask))
                return false;
            _matchedFaces = mask == 63 ? 6 : Mathf.Min(_matchedFaces + 1, 6);
            return true;
        }

        private void ReleaseMatched()
        {
            Matching = false;
            MatchProblem = "";
            _matchedMap = null;
            _matchedFaces = 0;
            if (_matched != null)
            {
                _matched.Release();
                Object.DestroyImmediate(_matched);
                _matched = null;
            }
        }

        /// <summary>
        /// What the sky camera draws right after the skybox, in each face, from one command buffer:
        /// (1) for the glass, GlassUtil's SkyBackground -- the sky's light x dusk, alpha cleared;
        /// (1b) for the water, GlassUtil's SkyFog -- the game's fog over the sky, which on screen comes
        /// before the clouds too (the [ImageEffectOpaque] fog, then Volumetric Weather's transparents);
        /// (2) Volumetric Weather's distant clouds and (3) its volumetric clouds, each its own mesh
        /// with its own material -- the same clouds the player sees, from the same point, in the
        /// order its queues draw them (sky layer 2507 before the clouds). Unreal's Sky Light captures
        /// its volumetric clouds into the reflection cube the same way. The volume's scene-depth test
        /// is off for this draw alone (_UseDepth = 0 in a property block: the main camera's depth
        /// texture means nothing to a face of the cube); the distant clouds' test has no switch
        /// ("sky pixels only", SkyLayer.shader), so while they are drawn the sky camera makes its own
        /// depth texture -- nothing in it, all sky. Without it the test read whatever depth was bound
        /// last and drew nothing (probed, Unity 5.6, Direct3D 11 and OpenGL Core, 2026-10-19).
        /// </summary>
        private void Record(bool clouds, bool distant, Material util, float dusk)
        {
            BoltsDrawn = 0;
            bool cloudsNow = Find(clouds, CloudVolumeName, ref _cloudRenderer, ref _cloudFilter, ref _nextCloudSearch);
            bool distantNow = Find(distant, DistantCloudsName, ref _distantRenderer, ref _distantFilter, ref _nextDistantSearch);
            bool background = util != null;
            bool fog = Fog != null;
            DrawsClouds = cloudsNow;
            DrawsDistantClouds = distantNow;
            CloudAlpha = background;
            _camera.depthTextureMode = distantNow ? DepthTextureMode.Depth : DepthTextureMode.None;

            if (!background && !fog && !cloudsNow && !distantNow)
            {
                DropBuffer();
                return;
            }

            if (_buffer == null)
            {
                _buffer = new CommandBuffer();
                _buffer.name = "RealGlass sky";
                _cloudBlock = new MaterialPropertyBlock();
                _backgroundBlock = new MaterialPropertyBlock();
                _fogBlock = new MaterialPropertyBlock();
                _camera.AddCommandBuffer(CameraEvent.AfterSkybox, _buffer);
            }

            _buffer.Clear();
            if (background)
            {
                if (_quad == null)
                    _quad = GlassScreen.ClipQuad();
                _backgroundBlock.SetVector("_RGSkyDusk", new Vector4(Mathf.Clamp01(dusk), 0f, 0f, 0f));
                _buffer.DrawMesh(_quad, Matrix4x4.identity, util, 0, PassSkyBackground, _backgroundBlock);
            }

            if (fog)
            {
                if (_box == null)
                    _box = Box();
                _fogBlock.SetVector("_RGSkyFog", new Vector4(Mathf.Max(1f, FogFar), 0f, 0f, 0f));
                _buffer.DrawMesh(_box, Matrix4x4.identity, Fog, 0, PassSkyFog, _fogBlock);
            }

            // The bolts, before every cloud: the cloud pass lays its cloud, rain and fog over them, as on screen.
            BoltsDrawn = 0;
            if (Bolts && FindBolts() && s_boltCount() > 0)
                BoltsDrawn = s_drawBolts(_buffer);

            if (distantNow)
                _buffer.DrawMesh(_distantFilter.sharedMesh, _distantRenderer.transform.localToWorldMatrix, _distantRenderer.sharedMaterial, 0, 0);

            if (cloudsNow)
            {
                _cloudBlock.SetFloat("_UseDepth", 0f);
                _buffer.DrawMesh(_cloudFilter.sharedMesh, _cloudRenderer.transform.localToWorldMatrix, _cloudRenderer.sharedMaterial, 0, 0, _cloudBlock);
            }
        }

        /// <summary>True while the clouds drawn into the cube carry a lightning flash (Volumetric Weather's _FlashCount).</summary>
        private bool Flashing()
        {
            if (!DrawsClouds || _cloudRenderer == null)
                return false;
            Material clouds = _cloudRenderer.sharedMaterial;
            return clouds != null && clouds.GetFloat(IdFlashCount) > 0.5f;
        }

        /// <summary>
        /// Volumetric Weather's renderer called name, when wanted: looked for every 2 s while absent;
        /// true while it is there, enabled and drawable.
        /// </summary>
        private static bool Find(bool wanted, string name, ref MeshRenderer renderer, ref MeshFilter filter, ref float nextSearch)
        {
            if (wanted && renderer == null && Time.realtimeSinceStartup >= nextSearch)
            {
                nextSearch = Time.realtimeSinceStartup + 2f;
                GameObject found = GameObject.Find(name);
                if (found != null)
                {
                    renderer = found.GetComponent<MeshRenderer>();
                    filter = found.GetComponent<MeshFilter>();
                }
            }

            return wanted && renderer != null && renderer.enabled && filter != null && filter.sharedMesh != null &&
                   renderer.sharedMaterial != null;
        }

        /// <summary>
        /// Volumetric Weather's bolts API, looked for every 10 s (the mod may load after this one, or be
        /// rebuilt with the game running). True while bound.
        /// </summary>
        private static bool FindBolts()
        {
            if (Time.realtimeSinceStartup < s_nextBoltsSearch)
                return s_drawBolts != null;
            s_nextBoltsSearch = Time.realtimeSinceStartup + 10f;
            try
            {
                // The NEWEST copy: the game loads a rebuilt mod beside the old one (a new version number)
                // with the game running, and the old copy's bolts stop. Bound again only when it changes.
                System.Type type = null;
                System.Version newest = null;
                foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Reflection.AssemblyName name = assembly.GetName();
                    if (name.Name != "VolumetricClouds" || (newest != null && name.Version <= newest))
                        continue;
                    System.Type found = assembly.GetType("VolumetricClouds.Sky.LightningBolts", false);
                    if (found != null)
                    {
                        type = found;
                        newest = name.Version;
                    }
                }
                if (type != null && type == s_boltsType)
                    return s_drawBolts != null;
                s_drawBolts = null;
                s_boltCount = null;
                s_boltsType = type;
                if (type == null)
                {
                    s_boltsApi = "Volumetric Weather absent, or older than its lightning API";
                    return false;
                }
                System.Reflection.FieldInfo version = type.GetField("ApiVersion");
                if (version == null || (int)version.GetValue(null) < 1)
                {
                    s_boltsApi = "Volumetric Weather's lightning API version unknown";
                    return false;
                }
                System.Reflection.MethodInfo draw = type.GetMethod("DrawInto", new[] { typeof(CommandBuffer) });
                System.Reflection.PropertyInfo count = type.GetProperty("Count");
                if (draw == null || count == null || draw.ReturnType != typeof(int))
                {
                    s_boltsApi = "Volumetric Weather's lightning API lacks DrawInto / Count";
                    return false;
                }
                s_boltCount = (System.Func<int>)System.Delegate.CreateDelegate(typeof(System.Func<int>), count.GetGetMethod());
                s_drawBolts = (System.Func<CommandBuffer, int>)System.Delegate.CreateDelegate(typeof(System.Func<CommandBuffer, int>), draw);
                s_boltsApi = "found";
                return true;
            }
            catch (System.Exception e)
            {
                s_boltsApi = "Volumetric Weather's lightning API: " + e.GetType().Name + ": " + e.Message;
                s_drawBolts = null;
                s_boltCount = null;
                s_boltsType = null;
                return false;
            }
        }

        /// <summary>A unit cube round the origin, drawn round the camera (SkyFog's vertex program puts it at the far plane).</summary>
        private static Mesh Box()
        {
            var mesh = new Mesh { name = "RealGlass sky box", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
            };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        private void DropBuffer()
        {
            if (_buffer == null)
                return;

            if (_camera != null)
                _camera.RemoveCommandBuffer(CameraEvent.AfterSkybox, _buffer);
            _buffer.Release();
            _buffer = null;
        }

        public void Release()
        {
            ReleaseMatched();
            if (_cube != null)
            {
                _cube.Release();
                Object.DestroyImmediate(_cube);
                _cube = null;
            }

            _size = 0;
            _facesDone = 0;
        }

        public void Destroy()
        {
            Release();
            DropBuffer();
            DrawsClouds = false;
            DrawsDistantClouds = false;
            CloudAlpha = false;
            _cloudRenderer = null;
            _cloudFilter = null;
            _distantRenderer = null;
            _distantFilter = null;
            if (_quad != null)
            {
                Object.DestroyImmediate(_quad);
                _quad = null;
            }
            if (_box != null)
            {
                Object.DestroyImmediate(_box);
                _box = null;
            }
            if (_holder != null)
            {
                Object.DestroyImmediate(_holder);
                _holder = null;
                _camera = null;
            }
            if (_matchHolder != null)
            {
                Object.DestroyImmediate(_matchHolder);
                _matchHolder = null;
                _matchCamera = null;
            }
        }
    }
}
