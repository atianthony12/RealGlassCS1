using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// The glass's reflection in a pass of its own (since 2.0.0): what each glass pixel mirrors is
    /// worked out over the whole screen, before the glass is drawn, and blended with last frame's --
    /// the history a forward pass drawn building by building cannot keep, and what keeps the
    /// reflections from crawling under temporal anti-aliasing.
    /// </summary>
    /// <remarks>
    /// <para>THE PRE-PASS: every glass draw (GlassDraw's four paths, the asset editor's building, the
    /// preview's) is recorded beside it into a command buffer (<see cref="Record"/>: the same mesh,
    /// matrix and property block -- copied at the call, probed 2026-11-08), drawn at AfterFinalPass
    /// with GlassPrePass.shader into two targets bound together with the camera's own depth (probed:
    /// ZTest against it holds on Direct3D 11 and OpenGL Core). Cleared when the frame changes, as
    /// WaterSurfaceMask's recorded water is: a camera rendered twice a frame keeps the frame's draws.</para>
    /// <para>THE TWO TARGETS ARE THE SIZE OF THE RENDER, not of the camera as it stands outside one:
    /// temporaries at the camera's size (-1, -1), taken at every render. A dynamic resolution mod renders
    /// the game's camera into a target of its own only for the length of the render (screen x its
    /// factor, and made at the city's load: a later change of the game's resolution leaves it as it was),
    /// and colour targets of another size than the depth they are bound with are refused or tested
    /// against the wrong part of it (probed 2026-11-15, Direct3D 11 and OpenGL Core: above the render's
    /// size, the depth's corner of our size; below it, nothing drawn and the draws landing in the target
    /// bound before). A temporary lives only within the buffer that took it (probed: "not found" in the
    /// next one), so the reflection's passes follow the draws in the same buffer, appended as the camera
    /// starts its render (<see cref="Close"/>): the game queues every glass draw before that, in
    /// RenderManager.LateUpdate.</para>
    /// <para>THE REFLECTION: then GlassReflection.shader's pass 0 over the screen (a cube round the
    /// camera, the camera's matrices bound) into <see cref="_reflect"/>, and pass 1 blends it with last
    /// frame's result, reprojected along each reflection's virtual point, into one of two targets taken
    /// in turn; the newest is the global _RGReflection Glass.shader reads. These keep the size the
    /// copies have (GlassScreen's): they are read by screen uv only.</para>
    /// <para>Its buffer is attached right after GlassScreen's (the copies and the depth pyramid it
    /// reads), by GlassScreen itself: buffers at one event run in the order added. Engine types only:
    /// the GPU preview runs this same file.</para>
    /// </remarks>
    public sealed class GlassPrePass
    {
        public const int TierNear = 0, TierLod = 1, TierGroup = 2;

        /// <summary>How much of last frame's reflection each frame keeps (the water's history weight; tuned on tools/preview/glass-flicker.ps1).</summary>
        public static float HistoryWeight = 0.85f;

        /// <summary>The frame's number (the editor's tests count their own: there every render is one frame).</summary>
        public static System.Func<int> Frame = () => Time.frameCount;

        private static readonly string[] Keywords = { "RG_NEAR", "RG_LOD", "RG_GROUP" };
        private static int _idG0, _idG1, _idReflect, _idPrev, _idReflection, _idInfo;
        private static bool _ids;

        private readonly Material[] _pre = new Material[3];
        private readonly Material _reflectMat;
        private readonly Mesh _cube;
        private readonly CommandBuffer _draws;

        private Camera _camera;
        private int _width, _height;
        private RenderTexture _reflect;
        private readonly RenderTexture[] _history = new RenderTexture[2];
        private int _newest, _historyFrames;
        private int _prev = 1, _cur;
        private bool _consumed, _closed;
        private int _frame = -1;
        private Camera.CameraCallback _beforeRender, _afterRender;

        /// <summary>Draws recorded since the counters were last read (the 5 s line).</summary>
        public int Recorded;

        /// <summary>Why it is not working ("" when it is).</summary>
        public string Problem = "";

        /// <summary>
        /// Commands put into the reflection's buffer each frame just before the reflection pass (the buffer,
        /// the camera, the targets' width and height): the facade cards' level from the frame. Null: none.
        /// </summary>
        public System.Action<CommandBuffer, Camera, int, int> BeforeReflection;

        /// <summary>The reflection pass's material (GlassReflection.shader): the facade cards' keyword is set on it, not globally.</summary>
        public Material Reflection
        {
            get { return _reflectMat; }
        }

        /// <summary>The pre-pass's material of a tier (the preview sets its depth offset as the glass's).</summary>
        public Material Material(int tier)
        {
            return _pre[tier];
        }

        /// <summary>The pre-pass on a camera and its targets made: what Glass.shader reads is its result.</summary>
        public bool Attached
        {
            get { return _camera != null; }
        }

        public GlassPrePass(Shader prePass, Shader reflection)
        {
            Ids();
            for (int k = 0; k < 3; k++)
            {
                _pre[k] = new Material(prePass) { name = "RealGlass pre-pass " + Keywords[k], hideFlags = HideFlags.HideAndDontSave };
                _pre[k].EnableKeyword(Keywords[k]);
            }
            _reflectMat = new Material(reflection) { name = "RealGlass reflection", hideFlags = HideFlags.HideAndDontSave };
            _cube = Cube();
            _draws = new CommandBuffer { name = "RealGlass glass pre-pass and reflection" };
            Unbound();
        }

        private static void Ids()
        {
            if (_ids)
                return;
            _idG0 = Shader.PropertyToID("_RGGlassG0");
            _idG1 = Shader.PropertyToID("_RGGlassG1");
            _idReflect = Shader.PropertyToID("_RGReflect");
            _idPrev = Shader.PropertyToID("_RGReflectionPrev");
            _idReflection = Shader.PropertyToID("_RGReflection");
            _idInfo = Shader.PropertyToID("_RGReflectionInfo");
            _ids = true;
        }

        /// <summary>
        /// No pre-pass (off the camera, its targets not made, its shaders missing or refused here):
        /// Glass.shader reads alpha 0 everywhere, which is "no reflection found", and mirrors the sky.
        /// A texel of our own: what a built-in or a never-set texture holds in its alpha is the
        /// engine's business, and any alpha above 0 would be taken for a reflection (one flat colour
        /// on every pane).
        /// </summary>
        public static void Unbound()
        {
            Ids();
            if (_none == null)
            {
                _none = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                _none.name = "RealGlass no reflection";
                _none.hideFlags = HideFlags.HideAndDontSave;
                _none.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                _none.Apply(false, true);
            }
            Shader.SetGlobalTexture(_idReflection, _none);
        }

        private static Texture2D _none;

        /// <summary>A unit cube round the origin: drawn round the camera, GlassReflection's vertex program puts it at the far plane.</summary>
        private static Mesh Cube()
        {
            var mesh = new Mesh { name = "RealGlass reflection cube", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2,
                0, 4, 7, 0, 7, 3,
                1, 2, 6, 1, 6, 5,
            };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        private static RenderTexture Target(string name, int width, int height)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// On the camera, the reflection's targets the copies' size (width x height; the pre-pass's own
        /// two are the render's, taken at each render); its buffer added at event after whatever is
        /// there already (GlassScreen calls it right after adding its own). False when the targets
        /// cannot be made (no half floats): the glass then mirrors the sky.
        /// </summary>
        public bool Attach(Camera camera, CameraEvent at, int width, int height)
        {
            Detach();
            if (camera == null || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) || SystemInfo.supportedRenderTargetCount < 2)
            {
                Problem = camera == null ? "no camera" : "no two half-float targets at once";
                return false;
            }
            _camera = camera;
            _width = width;
            _height = height;
            _reflect = Target("RealGlass reflection", width, height);
            _history[0] = Target("RealGlass reflection history A", width, height);
            _history[1] = Target("RealGlass reflection history B", width, height);
            _historyFrames = 0;
            _prev = 1 - _newest;
            _cur = _newest;
            // The pre-pass's two targets are globals set in the buffer (temporaries): never set them on
            // the material, whose own value would win over the global.
            _reflectMat.SetTexture(_idReflect, _reflect);
            _reflectMat.SetVector(_idInfo, new Vector4(0f, 1f / _width, 1f / _height, 0f));
            _camera.AddCommandBuffer(at, _draws);
            _at = at;
            _beforeRender = BeforeRender;
            _afterRender = AfterRender;
            Camera.onPreCull += _beforeRender;
            Camera.onPostRender += _afterRender;
            ClearDraws();
            Problem = "";
            return true;
        }

        private CameraEvent _at;

        /// <summary>
        /// Every frame before the camera renders (GlassScreen.Update): this frame's history -- read from
        /// last frame's target, written to the other. The passes themselves go into the buffer as the
        /// camera starts its render (<see cref="Close"/>).
        /// </summary>
        public void Update()
        {
            if (_camera == null)
            {
                Unbound();
                return;
            }

            _prev = _newest;
            _cur = 1 - _newest;
            _newest = _cur;
            float weight = _historyFrames > 0 ? Mathf.Clamp01(HistoryWeight) : 0f;
            _historyFrames++;
            _reflectMat.SetVector(_idInfo, new Vector4(weight, 1f / _width, 1f / _height, 0f));
        }

        /// <summary>
        /// One glass draw, recorded beside it: mesh at matrix through the tier's pre-pass, block the
        /// draw's own (copied now). layer: the draw's layer (a camera that does not render it records nothing).
        /// </summary>
        public void Record(Mesh mesh, Matrix4x4 matrix, int tier, MaterialPropertyBlock block, int layer)
        {
            if (_camera == null || mesh == null || (_camera.cullingMask & (1 << layer)) == 0)
                return;
            if (_consumed || _frame != Frame())
                ClearDraws();
            // The reflection's passes already follow the draws (the camera has started its render): a
            // draw now would land after them. The game queues none there; should another mod, it is
            // left out of the pre-pass (that pane mirrors the sky this frame), never drawn out of order.
            if (_closed)
                return;
            _draws.DrawMesh(mesh, matrix, _pre[tier], 0, 0, block);
            Recorded++;
        }

        private void ClearDraws()
        {
            _draws.Clear();
            _consumed = false;
            _closed = false;
            _frame = Frame();
            if (_reflect == null)
                return;
            // The size of the render (-1, -1: the camera's as it renders), so they match the depth they
            // are bound with whatever renders the camera, and whatever the screen's size.
            _draws.GetTemporaryRT(_idG0, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _draws.GetTemporaryRT(_idG1, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _draws.SetRenderTarget(new RenderTargetIdentifier[] { _idG0, _idG1 }, BuiltinRenderTextureType.CameraTarget);
            _draws.ClearRenderTarget(false, true, Color.clear);
        }

        /// <summary>
        /// The reflection's passes after the frame's draws, in the same buffer (the two temporaries live
        /// only within it), then the temporaries given back. Once a buffer: a camera rendered twice a
        /// frame runs it twice, each render at its own size.
        /// </summary>
        private void Close()
        {
            if (_closed || _reflect == null)
                return;
            _closed = true;
            _draws.SetGlobalTexture(_idG0, _idG0);
            _draws.SetGlobalTexture(_idG1, _idG1);
            // The facade cards' level, measured just before the reflection reads it (FacadeCardGain).
            if (BeforeReflection != null)
                BeforeReflection(_draws, _camera, _width, _height);
            _draws.SetRenderTarget(_reflect);
            _draws.ClearRenderTarget(false, true, Color.clear);
            _draws.DrawMesh(_cube, Matrix4x4.identity, _reflectMat, 0, 0);
            _draws.SetGlobalTexture(_idPrev, _history[_prev]);
            _draws.SetRenderTarget(_history[_cur]);
            // Cleared first: should the pass draw nothing on some GPU, the glass reads "no reflection"
            // (the sky), never what the target held before.
            _draws.ClearRenderTarget(false, true, Color.clear);
            _draws.DrawMesh(_cube, Matrix4x4.identity, _reflectMat, 0, 1);
            _draws.SetGlobalTexture(_idReflection, _history[_cur]);
            _draws.ReleaseTemporaryRT(_idG0);
            _draws.ReleaseTemporaryRT(_idG1);
            _draws.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        private void BeforeRender(Camera camera)
        {
            if (camera != _camera)
                return;
            // Not after a render earlier this frame: a camera rendered twice a frame (two dynamic
            // resolution mods together) draws the frame's buildings in both renders.
            if (_frame != Frame())
                ClearDraws();
            Close();
        }

        private void AfterRender(Camera camera)
        {
            if (camera == _camera)
                _consumed = true;
        }

        /// <summary>Off the camera, the targets freed (resize, off, unload).</summary>
        public void Detach()
        {
            if (_beforeRender != null)
            {
                Camera.onPreCull -= _beforeRender;
                Camera.onPostRender -= _afterRender;
                _beforeRender = _afterRender = null;
            }
            if (_camera != null)
                _camera.RemoveCommandBuffer(_at, _draws);
            _camera = null;
            _draws.Clear();
            _closed = false;
            Free(ref _reflect);
            Free(ref _history[0]);
            Free(ref _history[1]);
            Unbound();
        }

        public void Destroy()
        {
            Detach();
            _draws.Release();
            for (int k = 0; k < 3; k++)
                Dispose(_pre[k]);
            Dispose(_reflectMat);
            Dispose(_cube);
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Dispose(rt);
            rt = null;
        }

        private static void Dispose(Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
