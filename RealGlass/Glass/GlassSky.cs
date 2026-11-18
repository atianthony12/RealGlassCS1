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
        /// water"); while one shows, all six faces are rendered each frame. The water's sky only: the
        /// glass's cube alpha is the clouds' opacity, which an additive bolt would spoil.
        /// </summary>
        public bool Bolts;

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

        /// <summary>
        /// The global the cube is bound to: the glass's "_RGSky"; the water's sky layer keeps its own
        /// cube under another name (Water/WaterSkyLayer.cs).
        /// </summary>
        public string GlobalName = "_RGSky";

        /// <summary>The live cube (null until the first update, or while the game's map is used).</summary>
        public Texture Cube { get { return _cube; } }

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

            // A new cube renders all six faces at once; after that, a few a frame -- all six while a bolt
            // shows (a strike's flicker lasts a few frames: a face a third of them late would miss it).
            int count = _facesDone < 6 || BoltsDrawn > 0 ? 6 : Mathf.Clamp(facesPerFrame, 1, 6);
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
            Shader.SetGlobalTexture(GlobalName, _cube);
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
        }
    }
}
