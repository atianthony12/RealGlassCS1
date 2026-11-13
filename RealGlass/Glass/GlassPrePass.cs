using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// The glass's reflection in a pass of its own (since 1.1.9): what each glass pixel mirrors is
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
    /// <para>THE REFLECTION: then GlassReflection.shader's pass 0 over the screen (a cube round the
    /// camera, the camera's matrices bound) into <see cref="_reflect"/>, and pass 1 blends it with last
    /// frame's result, reprojected along each reflection's virtual point, into one of two targets taken
    /// in turn; the newest is the global _RGReflection Glass.shader reads.</para>
    /// <para>Its buffers are attached right after GlassScreen's (the copies and the depth pyramid it
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
        private readonly CommandBuffer _passes;

        private Camera _camera;
        private int _width, _height;
        private RenderTexture _g0, _g1, _reflect;
        private readonly RenderTexture[] _history = new RenderTexture[2];
        private int _newest, _historyFrames;
        private bool _consumed;
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
            _draws = new CommandBuffer { name = "RealGlass glass pre-pass" };
            _passes = new CommandBuffer { name = "RealGlass glass reflection" };
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

        /// <summary>No pre-pass: Glass.shader reads a black texture (alpha 0) and mirrors the sky.</summary>
        private static void Unbound()
        {
            Shader.SetGlobalTexture(_idReflection, Texture2D.blackTexture);
        }

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
        /// On the camera, its targets the screen's size; its buffers added at event after whatever is
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
            _g0 = Target("RealGlass glass G0", width, height);
            _g1 = Target("RealGlass glass G1", width, height);
            _reflect = Target("RealGlass reflection", width, height);
            _history[0] = Target("RealGlass reflection history A", width, height);
            _history[1] = Target("RealGlass reflection history B", width, height);
            _historyFrames = 0;
            _reflectMat.SetTexture(_idG0, _g0);
            _reflectMat.SetTexture(_idG1, _g1);
            _reflectMat.SetTexture(_idReflect, _reflect);
            _camera.AddCommandBuffer(at, _draws);
            _camera.AddCommandBuffer(at, _passes);
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
        /// Every frame before the camera renders (GlassScreen.Update): the reflection's buffer for this
        /// frame -- the history read from last frame's target, written to the other.
        /// </summary>
        public void Update()
        {
            if (_camera == null)
            {
                Unbound();
                return;
            }

            int prev = _newest, cur = 1 - _newest;
            _newest = cur;
            float weight = _historyFrames > 0 ? Mathf.Clamp01(HistoryWeight) : 0f;
            _historyFrames++;
            _reflectMat.SetVector(_idInfo, new Vector4(weight, 1f / _width, 1f / _height, 0f));

            _passes.Clear();
            // The facade cards' level, measured just before the reflection reads it (FacadeCardGain).
            if (BeforeReflection != null)
                BeforeReflection(_passes, _camera, _width, _height);
            _passes.SetRenderTarget(_reflect);
            _passes.ClearRenderTarget(false, true, Color.clear);
            _passes.DrawMesh(_cube, Matrix4x4.identity, _reflectMat, 0, 0);
            _passes.SetGlobalTexture(_idPrev, _history[prev]);
            _passes.SetRenderTarget(_history[cur]);
            _passes.DrawMesh(_cube, Matrix4x4.identity, _reflectMat, 0, 1);
            _passes.SetGlobalTexture(_idReflection, _history[cur]);
            _passes.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
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
            _draws.DrawMesh(mesh, matrix, _pre[tier], 0, 0, block);
            Recorded++;
        }

        private void ClearDraws()
        {
            _draws.Clear();
            _consumed = false;
            _frame = Frame();
            if (_g0 == null)
                return;
            _draws.SetRenderTarget(new RenderTargetIdentifier[] { _g0, _g1 }, BuiltinRenderTextureType.CameraTarget);
            _draws.ClearRenderTarget(false, true, Color.clear);
        }

        private void BeforeRender(Camera camera)
        {
            // Not after a render earlier this frame: a camera rendered twice a frame (two dynamic
            // resolution mods together) draws the frame's buildings in both renders.
            if (camera == _camera && _frame != Frame())
                ClearDraws();
        }

        private void AfterRender(Camera camera)
        {
            // (The draws' buffer leaves its targets bound; the reflection's, run right after it, ends on the camera's.)
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
            {
                _camera.RemoveCommandBuffer(_at, _draws);
                _camera.RemoveCommandBuffer(_at, _passes);
            }
            _camera = null;
            _draws.Clear();
            _passes.Clear();
            Free(ref _g0);
            Free(ref _g1);
            Free(ref _reflect);
            Free(ref _history[0]);
            Free(ref _history[1]);
            Unbound();
        }

        public void Destroy()
        {
            Detach();
            _draws.Release();
            _passes.Release();
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
